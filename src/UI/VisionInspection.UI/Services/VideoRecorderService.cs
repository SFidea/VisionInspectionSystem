using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VisionInspection.Core.Services;

namespace VisionInspection.UI.Services
{
    using VisionInspection.UI.Models;

    /// <summary>
    /// 录像服务：相机帧 → 有界队列 → ffmpeg stdin → mp4。
    /// 取帧与写盘解耦，队列满时丢弃最新帧（内存恒定，不拖慢预览）。
    /// </summary>
    public class VideoRecorderService : IDisposable
    {
        public bool IsRecording { get; private set; }
        public int FramesWritten { get; private set; }
        public int FramesDropped { get; private set; }
        public long FileBytes { get; private set; }
        public string? CurrentFile { get; private set; }

        private BlockingCollection<CameraImageData>? _queue;
        private CancellationTokenSource? _cts;
        private Task? _writerTask;
        private Process? _ffmpeg;
        private StreamWriter? _stdin;
        private Stream? _stdinStream;
        private Stopwatch? _sw;
        private DateTime _recordStart;
        private RecordConfig _cfg = new();
        private DateTime _lastAccepted = DateTime.MinValue;
        private string _stopReason = "";

        /// <summary>录制结束回调（参数为结束原因）</summary>
        public event Action<string>? RecordingStopped;

        public TimeSpan Elapsed => IsRecording || _recordStart != default
            ? DateTime.Now - _recordStart
            : TimeSpan.Zero;

        /// <summary>开始录制到指定输出文件</summary>
        public void Start(string outputFile, RecordConfig cfg)
        {
            if (IsRecording) return;

            if (!FfmpegLocator.EnsureAvailable(out var ffmpeg, out _, out var err))
                throw new InvalidOperationException(err);

            var dir = System.IO.Path.GetDirectoryName(outputFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            _cfg = cfg;
            CurrentFile = outputFile;
            FileBytes = 0;
            FramesWritten = 0;
            FramesDropped = 0;
            _stopReason = "";
            _sw = Stopwatch.StartNew();
            _recordStart = DateTime.Now;
            _lastAccepted = DateTime.MinValue;

            var capacity = cfg.QueueCapacity > 0 ? cfg.QueueCapacity : 60;
            _queue = new BlockingCollection<CameraImageData>(capacity);
            _cts = new CancellationTokenSource();
            IsRecording = true;

            _writerTask = Task.Run(() => WriterLoop(ffmpeg, _cts.Token));
        }

        /// <summary>投入一帧（由 FrameGrabbed 事件调用）。返回是否被接受。</summary>
        public bool EnqueueFrame(CameraImageData data)
        {
            if (!IsRecording || data == null || _queue == null) return false;

            // 帧率上限节流（跟随相机时不过滤）
            if (_cfg.RecordFps > 0)
            {
                var interval = 1.0 / _cfg.RecordFps;
                var now = DateTime.Now;
                if (_lastAccepted != DateTime.MinValue && (now - _lastAccepted).TotalSeconds < interval)
                    return false;
                _lastAccepted = now;
            }

            if (_queue.IsAddingCompleted) return false;

            // 队列满则丢弃最新帧，保持内存恒定
            if (!_queue.TryAdd(data))
            {
                FramesDropped++;
                return false;
            }

            return true;
        }

        public async Task StopAsync(string reason = "手动停止")
        {
            if (!IsRecording) return;

            _stopReason = reason;
            IsRecording = false;

            try { _queue?.CompleteAdding(); } catch { }

            if (_writerTask != null)
            {
                try { await Task.WhenAny(_writerTask, Task.Delay(10000)); } catch { }
            }

            CloseFfmpeg();

            try
            {
                if (CurrentFile != null && File.Exists(CurrentFile))
                    FileBytes = new FileInfo(CurrentFile).Length;
            }
            catch { }

            RecordingStopped?.Invoke(reason);
        }

        private void WriterLoop(string ffmpeg, CancellationToken token)
        {
            try
            {
                foreach (var frame in _queue!.GetConsumingEnumerable(token))
                {
                    if (!EnsureFfmpegStarted(ffmpeg, frame)) break;
                    WriteFrame(frame);

                    // 安全护栏
                    if (_cfg.MaxDurationSec > 0 && (DateTime.Now - _recordStart).TotalSeconds >= _cfg.MaxDurationSec)
                    {
                        _stopReason = $"达到时长上限 {_cfg.MaxDurationSec} 秒";
                        break;
                    }

                    if (_cfg.MaxFileSizeMB > 0 && CurrentFile != null && File.Exists(CurrentFile))
                    {
                        var mb = new FileInfo(CurrentFile).Length / 1024.0 / 1024.0;
                        if (mb >= _cfg.MaxFileSizeMB)
                        {
                            _stopReason = $"达到体积上限 {_cfg.MaxFileSizeMB} MB";
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _stopReason = "已取消";
            }
            catch (Exception ex)
            {
                _stopReason = "写盘异常：" + ex.Message;
            }
        }

        private bool EnsureFfmpegStarted(string ffmpeg, CameraImageData frame)
        {
            if (_ffmpeg != null) return true;

            try
            {
                var channels = frame.Channels > 0 ? frame.Channels : (frame.IsColor ? 3 : 1);
                var pixFmt = channels >= 3 ? "bgr24" : "gray";
                var fps = _cfg.RecordFps > 0 ? _cfg.RecordFps : 25.0;
                var size = $"{frame.Width}x{frame.Height}";

                var psi = new ProcessStartInfo(ffmpeg)
                {
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardErrorEncoding = Encoding.UTF8
                };

                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-hide_banner");
                psi.ArgumentList.Add("-loglevel");
                psi.ArgumentList.Add("error");
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("rawvideo");
                psi.ArgumentList.Add("-pix_fmt");
                psi.ArgumentList.Add(pixFmt);
                psi.ArgumentList.Add("-s");
                psi.ArgumentList.Add(size);
                psi.ArgumentList.Add("-r");
                psi.ArgumentList.Add(fps.ToString(CultureInfo.InvariantCulture));
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add("-");

                if (_cfg.VideoCodec == "mjpeg")
                {
                    psi.ArgumentList.Add("-c:v");
                    psi.ArgumentList.Add("mjpeg");
                    psi.ArgumentList.Add("-q:v");
                    psi.ArgumentList.Add(Math.Clamp(_cfg.Crf, 2, 31).ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    psi.ArgumentList.Add("-c:v");
                    psi.ArgumentList.Add(_cfg.VideoCodec);
                    psi.ArgumentList.Add("-preset");
                    psi.ArgumentList.Add(_cfg.Preset);
                    psi.ArgumentList.Add("-crf");
                    psi.ArgumentList.Add(Math.Clamp(_cfg.Crf, 0, 51).ToString(CultureInfo.InvariantCulture));
                    psi.ArgumentList.Add("-pix_fmt");
                    psi.ArgumentList.Add("yuv420p");
                }

                psi.ArgumentList.Add(CurrentFile!);

                _ffmpeg = Process.Start(psi);
                if (_ffmpeg == null) return false;

                _stdinStream = _ffmpeg.StandardInput.BaseStream;
                _stdin = _ffmpeg.StandardInput;

                // 异步读取 stderr，避免缓冲区满导致死锁
                _ = Task.Run(() =>
                {
                    try { _ffmpeg.StandardError.ReadToEnd(); } catch { }
                });

                return true;
            }
            catch
            {
                return false;
            }
        }

        private void WriteFrame(CameraImageData frame)
        {
            if (_stdinStream == null) return;

            try
            {
                _stdinStream.Write(frame.Data, 0, frame.Data.Length);
                _stdinStream.Flush();
                FramesWritten++;
            }
            catch
            {
                FramesDropped++;
            }
        }

        private void CloseFfmpeg()
        {
            try
            {
                if (_stdin != null)
                {
                    try { _stdin.Close(); } catch { }
                    _stdin = null;
                    _stdinStream = null;
                }

                if (_ffmpeg != null)
                {
                    if (!_ffmpeg.WaitForExit(8000))
                    {
                        try { _ffmpeg.Kill(true); } catch { }
                    }

                    _ffmpeg.Dispose();
                    _ffmpeg = null;
                }
            }
            catch
            {
                // 关闭异常忽略
            }
        }

        public string GetStopReason() => _stopReason;

        public void Dispose()
        {
            try
            {
                if (IsRecording)
                {
                    // 同步收尾，避免 Dispose 中异步等待
                    _ = Task.Run(async () => await StopAsync("释放资源"));
                    Thread.Sleep(500);
                }
            }
            catch { }
            finally
            {
                CloseFfmpeg();
                _cts?.Dispose();
                _queue?.Dispose();
            }
        }
    }
}
