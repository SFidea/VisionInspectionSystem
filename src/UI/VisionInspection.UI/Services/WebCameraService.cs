using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using VisionInspection.Core.Services;

namespace VisionInspection.UI.Services
{
    /// <summary>
    /// 笔记本摄像头 / UVC USB 相机服务实现 - 使用OpenCV DirectShow后端
    /// 支持 DJI Pocket3 等消费级 UVC 相机。
    /// 注意：UVC 相机必须走 DirectShow / MediaFoundation，不能用海康 MVS SDK 驱动。
    /// </summary>
    public class WebCameraService : ICameraService
    {
        #region 字段

        private VideoCapture? _capture;
        private bool _isConnected = false;
        private volatile bool _isGrabbing = false;
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _grabTask;

        // 保护 _capture 访问：采集线程 Read 与主线程 Release 不能并发，
        // 否则采集线程读取已释放的原生对象会触发 System.AccessViolationException
        private readonly object _captureLock = new object();

        // ⭐ 进程级 DSHOW 互斥锁：DirectShow 原生图不允许并发创建/销毁。
        // 连接（开图）、断开（销毁图）、设备枚举（逐索引试开图）若并发执行，
        // 旧图还在异步销毁、新图已开始创建，会触发原生层 AccessViolationException
        // （实测：TTQ 相机断开后立刻连内置相机即崩）。所有 DSHOW 操作必须串行。
        private static readonly object DshowGlobalLock = new object();

        // 当前被本进程占用的相机索引（防双开：枚举跳过已连接索引，重复打开同一设备同样会原生崩溃）
        private static readonly HashSet<int> OccupiedIndices = new HashSet<int>();

        // 实测帧率统计（ GrabLoop 内累计，每 2 秒输出一次诊断日志）
        private int _fpsFrameCount;
        private DateTime _fpsWindowStart = DateTime.Now;

        #endregion

        #region 属性

        public bool IsConnected => _isConnected;
        public bool IsGrabbing => _isGrabbing;
        public CameraInfo CurrentCamera { get; private set; } = new CameraInfo();

        #endregion

        #region 事件

        public event EventHandler<byte[]>? ImageGrabbed;
        public event EventHandler<CameraImageData>? ImageDataGrabbed;
        public event EventHandler<bool>? ConnectionStatusChanged;
        public event EventHandler<string>? ErrorOccurred;

        #endregion

        #region 相机枚举

        /// <summary>
        /// 枚举可用 UVC 相机（DirectShow 后端，遍历 0..10 索引）
        /// </summary>
        public Task<List<CameraInfo>> EnumCamerasAsync()
        {
            return Task.Run(() =>
            {
                var cameras = new List<CameraInfo>();

                // 尝试打开前 10 个摄像头索引（UVC 相机可能占用多个索引）
                for (int i = 0; i < 10; i++)
                {
                    try
                    {
                        // 跳过已被本进程占用的索引：对同一设备重复开 DSHOW 图会原生崩溃
                        lock (DshowGlobalLock)
                        {
                            if (OccupiedIndices.Contains(i)) continue;

                            using var capture = new VideoCapture(i, VideoCaptureAPIs.DSHOW);
                            if (!capture.IsOpened())
                                continue;

                            // 尝试读取一帧以确认相机可用
                            using var frame = new Mat();
                            if (capture.Read(frame) && !frame.Empty())
                            {
                                cameras.Add(new CameraInfo
                                {
                                    Id = $"webcam_{i}",
                                    Name = $"摄像头 {i}",
                                    Model = "UVC Camera",
                                    SerialNumber = $"{i}",
                                    InterfaceType = "USB",
                                    Index = (uint)i,
                                    InterfaceIndex = 0,
                                    Type = 0,
                                    DisplayName = $"🎥 摄像头 {i} ({frame.Width}x{frame.Height})",
                                    ExtInfo = i // 存储索引
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"检查摄像头 {i} 失败: {ex.Message}");
                    }
                }

                return cameras;
            });
        }

        #endregion

        #region 连接控制

        /// <summary>
        /// 连接相机（使用 DirectShow 后端，对消费级 UVC 相机兼容性最好）
        /// </summary>
        public Task<bool> ConnectAsync(CameraInfo camera)
        {
            return Task.Run(() =>
            {
                try
                {
                    // 断开已有连接（内部会先停止采集并等待采集线程完全退出）
                    Disconnect();

                    if (camera?.ExtInfo == null)
                    {
                        ErrorOccurred?.Invoke(this, "相机信息无效");
                        return false;
                    }

                    int cameraIndex = (int)camera.ExtInfo;

                    // DSHOW 图串行化：断开旧相机的图可能仍在销毁，必须等它完成再开新图
                    lock (DshowGlobalLock)
                    {
                        lock (_captureLock)
                        {
                            // 后端选择：MSMF 优先——DSHOW 后端对"反复开图/关图"存在已知原生缺陷
                            //（图销毁是系统级异步，即使进程内串行，紧接的开图仍可能撞上未完成的销毁而 AV 崩溃）。
                            // MSMF（Media Foundation）对顺序开关稳定得多；打不开再回退 DSHOW。
                            _capture = TryOpen(cameraIndex, VideoCaptureAPIs.MSMF);
                            string backend = "MSMF";
                            if (_capture == null)
                            {
                                _capture = TryOpen(cameraIndex, VideoCaptureAPIs.DSHOW);
                                backend = "DSHOW";
                            }

                            if (_capture == null)
                            {
                                ErrorOccurred?.Invoke(this, $"无法打开摄像头 {cameraIndex}（MSMF/DSHOW 均失败）");
                                return false;
                            }

                            // 请求 MJPG 编码：UVC 相机在 USB2 带宽下 YUY2@720p 常被限制在 5~10fps，
                            // MJPG 可达 30fps（其它软件帧率高通常就是用了 MJPG）。
                            // 相机不支持时该设置会被忽略，不影响采集。
                            _capture.Set(VideoCaptureProperties.FourCC, OpenCvSharp.FourCC.FromString("MJPG"));
                            _capture.Set(VideoCaptureProperties.Fps, 30);

                            // 请求 1280x720（多数 UVC 相机可协商；失败则保持相机默认分辨率，不影响采集）
                            _capture.Set(VideoCaptureProperties.FrameWidth, 1280);
                            _capture.Set(VideoCaptureProperties.FrameHeight, 720);

                            double negotiatedFps = _capture.Get(VideoCaptureProperties.Fps);

                            // 实际生效的像素格式（诊断帧率问题关键：4.3fps@720p 通常意味着 MJPG 未生效、仍在跑 YUY2）
                            int fcc = (int)_capture.Get(VideoCaptureProperties.FourCC);
                            string fccStr = System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(fcc)).TrimEnd('\0');

                            string negoInfo =
                                $"[WebCamera] 摄像头 {cameraIndex} 协商结果[{backend}]: " +
                                $"{_capture.Get(VideoCaptureProperties.FrameWidth):F0}x{_capture.Get(VideoCaptureProperties.FrameHeight):F0} " +
                                $"@ {negotiatedFps:F1}fps 格式={fccStr}";
                            System.Diagnostics.Debug.WriteLine(negoInfo);
                            Console.WriteLine(negoInfo);
                        }

                        OccupiedIndices.Add(cameraIndex);
                    }

                    _isConnected = true;
                    CurrentCamera = camera;

                    ConnectionStatusChanged?.Invoke(this, true);
                    return true;
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, $"连接摄像头异常：{ex.Message}");
                    return false;
                }
            });
        }

        /// <summary>
        /// 以指定后端尝试打开相机索引，失败返回 null（不抛异常）
        /// </summary>
        private static VideoCapture? TryOpen(int index, VideoCaptureAPIs api)
        {
            try
            {
                var cap = new VideoCapture(index, api);
                if (cap.IsOpened()) return cap;
                cap.Dispose();
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public void Disconnect()
        {
            try
            {
                // 先停止采集（确保采集线程完全退出后再释放 capture，避免原生访问冲突）
                bool cleanStop = StopGrabbingCore();

                int releasedIndex = -1;
                lock (DshowGlobalLock)
                {
                    lock (_captureLock)
                    {
                        if (_isConnected && _capture != null)
                        {
                            if (CurrentCamera?.ExtInfo != null)
                                releasedIndex = (int)CurrentCamera.ExtInfo;

                            if (cleanStop)
                            {
                                _capture.Release();
                                _capture.Dispose();
                            }
                            else
                            {
                                // ⚠️ 采集线程仍卡在原生 Read 里：此时 Release/Dispose 必然 AV 崩溃。
                                // 只解除引用（泄漏这张图），绝不触碰原生指针——旧会话已与新图隔离，不会再被访问。
                                System.Diagnostics.Debug.WriteLine("[WebCamera] 采集线程未退出，跳过 Release（放弃该图以防崩溃）");
                            }

                            _capture = null;
                            _isConnected = false;
                            System.Diagnostics.Debug.WriteLine("摄像头已断开");
                        }
                    }

                    // 后端图的销毁是系统级异步的：紧接其后的新开图（连接另一台相机 / 设备枚举）
                    // 会撞上未完成的销毁而原生崩溃，留缓冲让图完全释放
                    if (releasedIndex >= 0)
                    {
                        Thread.Sleep(500);
                        OccupiedIndices.Remove(releasedIndex);
                    }
                }

                CurrentCamera = new CameraInfo();
                ConnectionStatusChanged?.Invoke(this, false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"断开连接异常：{ex.Message}");
            }
        }

        #endregion

        #region 采集控制

        /// <summary>
        /// 开始采集
        /// </summary>
        public Task<bool> StartGrabbingAsync()
        {
            return Task.Run(() =>
            {
                if (!_isConnected || _capture == null)
                {
                    ErrorOccurred?.Invoke(this, "设备未连接");
                    return false;
                }

                try
                {
                    // 会话隔离：把当前 VideoCapture 实例固定进本会话。
                    // 若该会话被放弃（线程卡在 Read 里），它只会触碰自己这张图，
                    // 绝不会误读 _capture 字段指向的"下一台相机的新图"
                    VideoCapture? sessionCapture;
                    lock (_captureLock) sessionCapture = _capture;

                    if (sessionCapture == null)
                    {
                        ErrorOccurred?.Invoke(this, "设备未连接");
                        return false;
                    }

                    _cancellationTokenSource = new CancellationTokenSource();
                    _isGrabbing = true;

                    var ct = _cancellationTokenSource.Token;
                    _grabTask = Task.Run(() => GrabLoop(sessionCapture, ct));

                    return true;
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, $"开始采集异常：{ex.Message}");
                    _isGrabbing = false;
                    return false;
                }
            });
        }

        /// <summary>
        /// 采集循环：在 _captureLock 保护下读取帧，避免与释放操作并发导致原生崩溃。
        /// sessionCapture 固定为本会话的实例——会话被弃后线程即使从阻塞的 Read 中醒来，
        /// 也会因 _capture 不再指向该实例而立即退出，绝不触碰新相机的图。
        /// </summary>
        private void GrabLoop(VideoCapture sessionCapture, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _isGrabbing)
            {
                Mat? frame = null;
                lock (_captureLock)
                {
                    if (_capture == null || !ReferenceEquals(_capture, sessionCapture)) break;
                    if (!sessionCapture.IsOpened()) break;

                    frame = new Mat();
                    if (!sessionCapture.Read(frame) || frame.Empty())
                    {
                        frame.Dispose();
                        frame = null;
                    }
                }

                if (frame == null)
                {
                    Thread.Sleep(33);
                    continue;
                }

                try
                {
                    using (frame)
                    using (var rgbFrame = new Mat())
                    {
                        Cv2.CvtColor(frame, rgbFrame, ColorConversionCodes.BGR2RGB);

                        // 确保数据连续：不连续时克隆为连续 Mat，避免拷贝越界
                        using var continuousFrame = rgbFrame.IsContinuous() ? rgbFrame : rgbFrame.Clone();

                        // 用 Mat 实际字节数计算（比 width*height*channels 更安全，兼容非 4 字节对齐的 stride）
                        long totalBytes = continuousFrame.Total() * continuousFrame.ElemSize();

                        byte[] data = new byte[totalBytes];
                        System.Runtime.InteropServices.Marshal.Copy(continuousFrame.Data, data, 0, (int)totalBytes);

                        ImageGrabbed?.Invoke(this, data);
                        ImageDataGrabbed?.Invoke(this, new CameraImageData
                        {
                            Data = data,
                            Width = continuousFrame.Width,
                            Height = continuousFrame.Height,
                            IsColor = true,
                            Channels = continuousFrame.Channels()
                        });

                        // 实测帧率诊断：每 2 秒输出一次真实采集帧率（对比协商帧率，
                        // 定位"帧率低"到底是相机协商问题还是程序消费问题）
                        _fpsFrameCount++;
                        var now = DateTime.Now;
                        if ((now - _fpsWindowStart).TotalSeconds >= 2)
                        {
                            string fpsInfo =
                                $"[WebCamera] 实测帧率: {_fpsFrameCount / (now - _fpsWindowStart).TotalSeconds:F1} fps " +
                                $"({continuousFrame.Width}x{continuousFrame.Height})";
                            System.Diagnostics.Debug.WriteLine(fpsInfo);
                            Console.WriteLine(fpsInfo);
                            _fpsFrameCount = 0;
                            _fpsWindowStart = now;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"采集异常：{ex.Message}");
                }

                // ⚠️ 成功取帧后不再强制 Sleep(33)：DirectShow 的 Read 本身按相机帧节拍阻塞，
                // 额外睡眠会把 30fps 相机压到 ~15fps（USB 相机帧率低的根因，2026-09-29 修复）。
                // 帧间隔完全由相机与 Read 阻塞决定，循环只随帧率空转。
            }
        }

        /// <summary>
        /// 停止采集（对外无返回值版本）
        /// </summary>
        public void StopGrabbing()
        {
            StopGrabbingCore();
        }

        /// <summary>
        /// 停止采集核心：等待采集线程真正退出。
        /// 返回 false 表示线程卡在原生 Read 里未退出——此时调用方【绝不能】Release/Dispose，
        /// 否则触发 System.AccessViolationException（原生层无托管堆栈，进程直接崩溃）。
        /// </summary>
        private bool StopGrabbingCore()
        {
            try
            {
                if (!_isGrabbing)
                {
                    // 未在采集：直接返回（避免连接/断开时刷屏"摄像头采集已停止"）
                    return true;
                }

                _isGrabbing = false;
                _cancellationTokenSource?.Cancel();

                var task = _grabTask;
                if (task != null)
                {
                    // Read 阻塞在死设备上可能长时间不返回，给足 10 秒；
                    // 仍未退出则报告失败，由 Disconnect 走"放弃释放"路径
                    bool exited = task.Wait(10000);
                    if (!exited)
                    {
                        System.Diagnostics.Debug.WriteLine("[WebCamera] 采集线程 10 秒未退出");
                        return false;
                    }
                    _grabTask = null;
                }

                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;

                System.Diagnostics.Debug.WriteLine("摄像头采集已停止");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"停止采集异常：{ex.Message}");
                return false;
            }
        }

        #endregion

        #region 参数设置（UVC 相机仅支持有限调节；返回默认范围避免 UI 提示"范围无效"）

        public Task<bool> SetExposureTimeAsync(float exposureTime)
        {
            // UVC 相机通常不支持工业式曝光时间设置
            return Task.FromResult(false);
        }

        public Task<bool> SetGainAsync(float gain)
        {
            return Task.FromResult(false);
        }

        public Task<float> GetExposureTimeAsync()
        {
            return Task.FromResult(0f);
        }

        public Task<float> GetGainAsync()
        {
            return Task.FromResult(0f);
        }

        public Task<(float Min, float Max)> GetExposureTimeRangeAsync()
        {
            // 返回与默认值一致的范围，避免 CameraConfigWindow 打印"曝光范围无效"且 Slider 被重置
            return Task.FromResult((20f, 10000000f));
        }

        public Task<(float Min, float Max)> GetGainRangeAsync()
        {
            return Task.FromResult((0f, 20f));
        }

        #endregion

        #region 资源释放

        public void Dispose()
        {
            Disconnect();
        }

        #endregion
    }
}
