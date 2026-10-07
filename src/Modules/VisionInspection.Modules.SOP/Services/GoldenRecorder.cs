using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SkiaSharp;
using VisionInspection.Core.Models;
using VisionInspection.Modules.SOP.Models;
using YoloDotNet.Models;

namespace VisionInspection.Modules.SOP.Services;

/// <summary>
/// ★ 黄金数据集旁路录制器（P0）
///
/// 设计铁律：
///   1. 【零侵入】不改任何判定逻辑，只在 SOPModule 引擎调用点旁复制数据。
///   2. 【不打断检测】写盘完全异步；任何异常只丢该帧，绝不向检测流程抛。
///   3. 【不拖慢帧率】编码与落盘全在后台线程，检测线程只做一次入队（O(1)）。
///   4. 【默认关闭】需在配置中显式开启，生产环境不受影响。
///
/// 产出（每个 case 一个目录）：
///   golden/cases/&lt;case&gt;/frames/000001.jpg
///   golden/cases/&lt;case&gt;/inputs.jsonl     每帧"看到了什么"（物体框 + 手部21关键点）
///   golden/cases/&lt;case&gt;/decisions.jsonl  每帧"判成了什么"（12类条件逐帧布尔 + 违规）★验收依据
///   golden/cases/&lt;case&gt;/meta.json        案例元信息
///   golden/cases/&lt;case&gt;/summary.json     录制统计
/// </summary>
public sealed class GoldenRecorder : IDisposable
{
    // ---- 常量：与 C# 端语义对齐，改动须同步 rk3588/docs/移植台账.md ----
    private const int MaxHandKeypoints = 21;

    // ---- 运行态（全部实例字段，避免静态共享导致多窗口串扰）----
    private readonly ConcurrentQueue<FrameRecord> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _lock = new();

    private CancellationTokenSource? _cts;
    private Task? _worker;
    private StreamWriter? _inputsWriter;
    private StreamWriter? _decisionsWriter;
    private string _caseDir = "";
    private int _queueCapacity = 64;
    private int _frameStride = 1;
    private int _jpegQuality = 90;
    private bool _saveFrames = true;
    private int _maxFrames;              // 0 = 不限
    private DateTime _t0;                // 本次录制起始时刻，用于算相对毫秒
    private int _frameIndex;
    private long _droppedCount, _writtenCount, _errorCount;
    private readonly Stopwatch _sw = new();

    public bool IsRecording { get; private set; }
    public string CurrentCaseDir => _caseDir;
    public long WrittenCount => Interlocked.Read(ref _writtenCount);
    public long DroppedCount => Interlocked.Read(ref _droppedCount);
    public long ErrorCount => Interlocked.Read(ref _errorCount);
    public string StatusLine =>
        IsRecording
            ? $"录制中 [{System.IO.Path.GetFileName(_caseDir)}] 已写 {WrittenCount} 丢弃 {DroppedCount} 错误 {ErrorCount}"
            : "未录制";

    // ================================================================ 生命周期

    /// <summary>开始录制。失败返回 false（调用方仅记日志，不打断检测）</summary>
    public bool Start(GoldenRecorderConfig cfg)
    {
        lock (_lock)
        {
            if (IsRecording) return true;
            try
            {
                if (string.IsNullOrWhiteSpace(cfg.CaseName))
                {
                    Console.WriteLine("[Golden] CaseName 为空，拒绝启动");
                    return false;
                }

                // 同步运行参数（此后 RecordFrame 只读字段，不碰配置对象）
                _saveFrames = cfg.SaveFrames;
                _jpegQuality = Math.Clamp(cfg.JpegQuality, 50, 100);
                _frameStride = Math.Max(1, cfg.FrameStride);
                _queueCapacity = Math.Max(8, cfg.QueueCapacity);
                _maxFrames = Math.Max(0, cfg.MaxFrames);

                var root = ResolveRoot(cfg.OutputRoot);
                _caseDir = System.IO.Path.Combine(root, "cases", Sanitize(cfg.CaseName));
                Directory.CreateDirectory(_caseDir);
                if (_saveFrames)
                    Directory.CreateDirectory(System.IO.Path.Combine(_caseDir, "frames"));

                // 追加模式：同名 case 续录，不覆盖已有数据
                var enc = new UTF8Encoding(false);
                _inputsWriter = new StreamWriter(
                    System.IO.Path.Combine(_caseDir, "inputs.jsonl"), true, enc) { AutoFlush = true };
                _decisionsWriter = new StreamWriter(
                    System.IO.Path.Combine(_caseDir, "decisions.jsonl"), true, enc) { AutoFlush = true };

                _t0 = DateTime.Now;
                _frameIndex = 0;
                _droppedCount = _writtenCount = _errorCount = 0;
                _queue.Clear();

                WriteMeta(cfg);

                _cts = new CancellationTokenSource();
                _worker = Task.Run(() => WorkerLoop(_cts.Token));
                IsRecording = true;
                _sw.Restart();
                Console.WriteLine($"[Golden] ▶ 开始录制 case='{cfg.CaseName}' → {_caseDir}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Golden] ✗ 启动失败: {ex.Message}");
                SafeCleanup();
                return false;
            }
        }
    }

    /// <summary>结束录制并落盘统计</summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (!IsRecording) return;
            IsRecording = false;
            try { _cts?.Cancel(); } catch { }
            // 给队列一个落盘窗口，最多等 5 秒
            try { _worker?.Wait(TimeSpan.FromSeconds(5)); } catch { }
            CloseWriters();
            WriteSummary();
            Console.WriteLine(
                $"[Golden] ■ 结束录制：写入 {WrittenCount}，丢弃 {DroppedCount}，错误 {ErrorCount}，耗时 {_sw.Elapsed.TotalSeconds:F1}s");
            SafeCleanup();
        }
    }

    public void Dispose()
    {
        Stop();
        _signal.Dispose();
    }

    // ================================================================ 采集入口

    /// <summary>
    /// 【旁路入口】记录一帧。由 SOPModule 在引擎 ProcessFrame 之后调用。
    /// 全程 try/catch 包裹，任何异常都不外泄到检测流程。
    /// </summary>
    public void RecordFrame(
        CaptureFrame? frame,
        IReadOnlyList<ObjectDetection> detections,
        HandPoseEstimationResult? handResult,
        SOPStateMachineSnapshot snapshot)
    {
        if (!IsRecording) return;
        try
        {
            // 抽稀
            _frameIndex++;
            if (_frameStride > 1 && _frameIndex % _frameStride != 0) return;

            // 帧数上限
            if (_maxFrames > 0 && _frameIndex > _maxFrames) return;

            var src = frame?.Image;
            int w = src?.Width ?? 0, h = src?.Height ?? 0;

            // ⚠️ 必须在【检测线程内】把 SKBitmap 编码成 JPEG 字节，不能把 SKBitmap 传给后台线程。
            // 原因：相机侧对 SKBitmap 做了帧缓冲复用 —— 新帧到达时旧 bitmap 被 Dispose()
            // （MainViewModel: `_lastInferenceFrames[camId] = bitmap` + 旧图 Dispose），
            // 后台线程再读它就是访问已释放的原生内存 → AccessViolationException(0x80004003)。
            // 编码耗时约 1~3ms（1280x720），发生在检测线程，但换来的是绝对安全。
            byte[]? jpeg = null;
            if (_saveFrames && src != null)
            {
                jpeg = TryEncodeJpeg(src);
            }

            if (_queue.Count >= _queueCapacity)
            {
                // 队列满：丢最旧一帧（保时序连续性优先于完整性）
                if (_queue.TryDequeue(out _)) Interlocked.Increment(ref _droppedCount);
            }

            _queue.Enqueue(new FrameRecord
            {
                Index = _frameIndex,
                Timestamp = frame?.Timestamp ?? DateTime.Now,
                FrameNumber = frame?.FrameNumber ?? _frameIndex,
                Width = w,
                Height = h,
                JpegBytes = jpeg,
                Detections = CopyDetections(detections),
                Hands = CopyHands(handResult),
                Snapshot = snapshot
            });
            _signal.Release();
        }
        catch
        {
            Interlocked.Increment(ref _errorCount);
            // 静默吞掉：录制失败绝不影响生产检测
        }
    }

    /// <summary>
    /// 在调用线程内把 SKBitmap 编码为 JPEG 字节。失败只丢该帧，绝不外泄。
    /// </summary>
    private byte[]? TryEncodeJpeg(SKBitmap bmp)
    {
        try
        {
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, _jpegQuality);
            return data.ToArray();
        }
        catch
        {
            Interlocked.Increment(ref _errorCount);
            return null;
        }
    }

    /// <summary>YOLO 结果列表会被下一帧复用（实例级 List），必须立即拷贝。</summary>
    private static List<ObjectDetection> CopyDetections(IReadOnlyList<ObjectDetection> src)
    {
        var list = new List<ObjectDetection>(src.Count);
        foreach (var d in src)
        {
            list.Add(new ObjectDetection
            {
                Label = d.Label,
                Confidence = d.Confidence,
                BoundingBox = d.BoundingBox,
                Id = d.Id
            });
        }
        return list;
    }

    /// <summary>
    /// 深拷贝手部结果：HandPose / HandKeypoint 都是引用对象，后台线程读它们时
    /// 推理层可能已在下一帧重建或复用，必须把关键点数据整份拷出来。
    /// </summary>
    private static List<HandPose> CopyHands(HandPoseEstimationResult? src)
    {
        var list = new List<HandPose>();
        if (src?.Hands == null) return list;
        foreach (var h in src.Hands)
        {
            var kps = new List<HandKeypoint>(h.Keypoints.Count);
            foreach (var k in h.Keypoints)
                kps.Add(new HandKeypoint(k.Type, k.X, k.Y, k.Z, k.Confidence));

            list.Add(new HandPose
            {
                TrackId = h.TrackId,
                HandType = h.HandType,
                BoundingBox = h.BoundingBox,
                Timestamp = h.Timestamp,
                Keypoints = kps
            });
        }
        return list;
    }

    // ================================================================ 后台写盘

    private void WorkerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_queue.TryDequeue(out var rec))
                {
                    Persist(rec);
                    Interlocked.Increment(ref _writtenCount);
                }
                else
                {
                    _signal.Wait(200, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                Interlocked.Increment(ref _errorCount);
            }
        }

        // 收尾：把队列里剩下的写完
        while (_queue.TryDequeue(out var rec))
        {
            try { Persist(rec); Interlocked.Increment(ref _writtenCount); }
            catch { Interlocked.Increment(ref _errorCount); }
        }
    }

    private void Persist(FrameRecord rec)
    {
        // 只写已编码好的 JPEG 字节 —— 后台线程绝不触碰 SKBitmap
        if (rec.JpegBytes != null && rec.JpegBytes.Length > 0)
        {
            var imgPath = System.IO.Path.Combine(_caseDir, "frames", $"{rec.Index:D6}.jpg");
            File.WriteAllBytes(imgPath, rec.JpegBytes);
        }

        _inputsWriter?.WriteLine(BuildInputJson(rec));
        _decisionsWriter?.WriteLine(BuildDecisionJson(rec));
    }

    // ================================================================ JSON 构建

    private string BuildInputJson(FrameRecord rec)
    {
        long ts = (long)(rec.Timestamp - _t0).TotalMilliseconds;
        var sb = new StringBuilder(1024);
        sb.Append("{\"frame\":").Append(rec.Index)
          .Append(",\"ts_ms\":").Append(ts)
          .Append(",\"frame_no\":").Append(rec.FrameNumber)
          .Append(",\"w\":").Append(rec.Width)
          .Append(",\"h\":").Append(rec.Height)
          .Append(",\"objects\":[");
        for (int i = 0; i < rec.Detections.Count; i++)
        {
            var d = rec.Detections[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"label\":").Append(JsonSerializer.Serialize(d.Label?.Name ?? "?"))
              .Append(",\"conf\":").Append(Math.Round(d.Confidence, 4))
              .Append(",\"track_id\":").Append(d.Id ?? -1)
              .Append(",\"box\":[").Append(d.BoundingBox.Left).Append(',')
              .Append(d.BoundingBox.Top).Append(',')
              .Append(d.BoundingBox.Right).Append(',')
              .Append(d.BoundingBox.Bottom).Append("]}");
        }
        sb.Append("],\"hands\":[");
        for (int i = 0; i < rec.Hands.Count; i++)
        {
            var h = rec.Hands[i];
            if (i > 0) sb.Append(',');
            // ★ GetCenter() 与 C# HandPoseEstimationModels.cs:108 完全一致：全部关键点算术平均
            var c = h.GetCenter();
            sb.Append("{\"track_id\":").Append(h.TrackId)
              .Append(",\"hand_type\":").Append((int)h.HandType)
              .Append(",\"is_left\":").Append(h.HandType == HandType.Left ? "true" : "false")
              // valid_points 是 GetCenter() 的分母，RK3588 侧必须与此一致
              .Append(",\"valid_points\":").Append(h.Keypoints.Count)
              .Append(",\"center\":[").Append(Math.Round(c.X, 2)).Append(',').Append(Math.Round(c.Y, 2)).Append(']')
              .Append(",\"box\":[").Append(Math.Round(h.BoundingBox.Left, 1)).Append(',')
              .Append(Math.Round(h.BoundingBox.Top, 1)).Append(',')
              .Append(Math.Round(h.BoundingBox.Right, 1)).Append(',')
              .Append(Math.Round(h.BoundingBox.Bottom, 1)).Append(']')
              .Append(",\"keypoints\":[");
            int n = Math.Min(h.Keypoints.Count, MaxHandKeypoints);
            for (int k = 0; k < n; k++)
            {
                if (k > 0) sb.Append(',');
                var kp = h.Keypoints[k];
                sb.Append('[').Append(Math.Round(kp.X, 1)).Append(',')
                  .Append(Math.Round(kp.Y, 1)).Append(']');
            }
            sb.Append("]}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private string BuildDecisionJson(FrameRecord rec)
    {
        var s = rec.Snapshot;
        var sb = new StringBuilder(512);
        sb.Append("{\"frame\":").Append(rec.Index)
          .Append(",\"ts_ms\":").Append((long)(s.Timestamp - _t0).TotalMilliseconds)
          .Append(",\"step_id\":").Append(s.StepId)
          .Append(",\"step_name\":").Append(JsonSerializer.Serialize(s.StepName))
          .Append(",\"state\":").Append(JsonSerializer.Serialize(s.State))
          .Append(",\"is_pass\":").Append(s.IsPass ? "true" : "false")
          .Append(",\"next_step\":").Append(s.NextStepId)
          .Append(",\"conditions\":{");
        bool first = true;
        foreach (var kv in s.Conditions)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(JsonSerializer.Serialize(kv.Key)).Append(':')
              .Append(kv.Value ? "true" : "false");
        }
        sb.Append("},\"violations\":[");
        for (int i = 0; i < s.Violations.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(JsonSerializer.Serialize(s.Violations[i]));
        }
        sb.Append("]}");
        return sb.ToString();
    }

    // ================================================================ 辅助

    private static string ResolveRoot(string? outputRoot)
    {
        if (string.IsNullOrWhiteSpace(outputRoot))
            return System.IO.Path.Combine(AppContext.BaseDirectory, "golden");
        return System.IO.Path.IsPathRooted(outputRoot)
            ? outputRoot
            : System.IO.Path.Combine(AppContext.BaseDirectory, outputRoot);
    }

    private void WriteMeta(GoldenRecorderConfig cfg)
    {
        try
        {
            var meta = new
            {
                schema_version = 1,
                case_name = cfg.CaseName,
                note = cfg.CaseNote,
                recorded_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                host = Environment.MachineName,
                save_frames = _saveFrames,
                jpeg_quality = _jpegQuality,
                frame_stride = _frameStride,
                max_frames = _maxFrames,
                reminder = "decisions.jsonl 为 RK3588 板端对拍验收依据（12类条件逐帧布尔）"
            };
            File.WriteAllText(
                System.IO.Path.Combine(_caseDir, "meta.json"),
                JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch (Exception ex) { Console.WriteLine($"[Golden] meta 写入失败: {ex.Message}"); }
    }

    private void WriteSummary()
    {
        try
        {
            var sum = new
            {
                finished_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                duration_sec = Math.Round(_sw.Elapsed.TotalSeconds, 1),
                frames_written = WrittenCount,
                frames_dropped = DroppedCount,
                errors = ErrorCount
            };
            File.WriteAllText(
                System.IO.Path.Combine(_caseDir, "summary.json"),
                JsonSerializer.Serialize(sum, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { }
    }

    private void CloseWriters()
    {
        try { _inputsWriter?.Flush(); _inputsWriter?.Dispose(); } catch { }
        try { _decisionsWriter?.Flush(); _decisionsWriter?.Dispose(); } catch { }
        _inputsWriter = null;
        _decisionsWriter = null;
    }

    private void SafeCleanup()
    {
        try { _cts?.Dispose(); } catch { }
        _cts = null;
        _worker = null;
        _queue.Clear();
    }

    private static string Sanitize(string name)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}

/// <summary>
/// 一帧的完整录制载荷（在检测线程构造，入队后交给后台写盘线程）。
/// 所有引用均为拷贝，YOLO 结果列表已被 CopyDetections 隔离。
/// </summary>
internal sealed class FrameRecord
{
    public int Index;
    public DateTime Timestamp;
    public int FrameNumber;
    public int Width, Height;
    /// <summary>已在检测线程内编码好的 JPEG 字节。后台线程绝不触碰 SKBitmap（会被相机 Dispose）。</summary>
    public byte[]? JpegBytes;
    public List<ObjectDetection> Detections = new();
    public List<HandPose> Hands = new();
    public SOPStateMachineSnapshot Snapshot = new();
}
