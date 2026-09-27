using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using SkiaSharp;
using VisionInspection.Core.Services;
using VisionInspection.Modules.Detection;
using VisionInspection.Modules.SOP.Models;
using VisionInspection.Modules.SOP.Services;
using VisionInspection.UI.Models;

namespace VisionInspection.UI.Services
{
    /// <summary>
    /// 设备性能基准测试：测量 MediaPipe 手部两阶段模型与 YOLO 检测模型在 CPU / GPU 上的推理帧率，
    /// 用于验证部署机是否满足性能要求。
    ///
    /// 设计要点：
    /// 1) 预热帧不计入统计——首次 CUDA 初始化与内存分配耗时可达数百毫秒，不预热会严重低估 FPS。
    /// 2) GPU 请求失败绝不静默回退——MediaPipe 内部会自动回退 CPU，这里通过 UsingGpu 如实报告，
    ///    否则会把 CPU 成绩误报成 GPU 成绩，得出错误的部署结论。
    /// 3) 每个「模型 × 设备」组合独立创建/销毁推理会话，避免显存累积影响后续项目。
    /// </summary>
    public class DeviceBenchmarkService
    {
        private const string PalmModelFileName = "palm_detection_full_Nx3x192x192_post.onnx";
        private const string LandmarkModelFileName = "hand_landmark_sparse_Nx3x224x224.onnx";

        /// <summary>
        /// 执行基准测试。
        /// </summary>
        /// <param name="opt">测试参数</param>
        /// <param name="realFrameProvider">真实帧提供器（真实帧模式用）；返回 null 时自动回退合成帧</param>
        /// <param name="progress">进度回调</param>
        /// <param name="ct">取消令牌</param>
        public async Task<BenchmarkReport> RunAsync(
            BenchmarkOptions opt,
            Func<SKBitmap?>? realFrameProvider,
            IProgress<BenchmarkProgress>? progress,
            CancellationToken ct = default)
        {
            var report = new BenchmarkReport { Timestamp = DateTime.Now, Options = opt };

            progress?.Report(new BenchmarkProgress { Message = "采集运行环境信息…", Percent = 0 });
            report.Environment = await Task.Run(() => GetEnvironment(), ct);

            using var frame = PrepareFrame(opt, realFrameProvider);
            if (frame == null)
                throw new InvalidOperationException("无法准备测试帧：真实帧不可用且合成帧创建失败。");

            var devices = new List<string>();
            if (opt.RunCpu) devices.Add("CPU");
            if (opt.RunGpu) devices.Add("GPU");

            int totalItems = devices.Count * ((opt.TestMediaPipe ? 1 : 0) + opt.YoloModelPaths.Count);
            int finished = 0;

            foreach (var device in devices)
            {
                if (device == "GPU" && !report.Environment.CudaAvailable)
                {
                    if (opt.TestMediaPipe)
                    {
                        report.Items.Add(MakeCudaUnavailableItem("MediaPipe 手部（palm+landmark）", "MediaPipe 手部", opt));
                        finished++;
                    }
                    foreach (var p in opt.YoloModelPaths)
                    {
                        report.Items.Add(MakeCudaUnavailableItem(Path.GetFileName(p), "YOLO 检测", opt));
                        finished++;
                    }
                    continue;
                }

                if (opt.TestMediaPipe)
                {
                    progress?.Report(new BenchmarkProgress
                    {
                        Message = $"测试 MediaPipe 手部（{device}）…",
                        Percent = Percent(finished, totalItems)
                    });
                    var item = await RunMediaPipeAsync(opt, frame, device, progress, finished, totalItems, ct);
                    report.Items.Add(item);
                    finished++;
                    progress?.Report(new BenchmarkProgress
                    {
                        Message = $"MediaPipe 手部（{device}）完成：{item.AvgFps:F1} FPS",
                        Percent = Percent(finished, totalItems),
                        CompletedItem = item
                    });
                }

                foreach (var modelPath in opt.YoloModelPaths)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new BenchmarkProgress
                    {
                        Message = $"测试 {Path.GetFileName(modelPath)}（{device}）…",
                        Percent = Percent(finished, totalItems)
                    });
                    var item = await RunYoloAsync(opt, frame, modelPath, device, progress, finished, totalItems, ct);
                    report.Items.Add(item);
                    finished++;
                    progress?.Report(new BenchmarkProgress
                    {
                        Message = $"{Path.GetFileName(modelPath)}（{device}）完成：{item.AvgFps:F1} FPS",
                        Percent = Percent(finished, totalItems),
                        CompletedItem = item
                    });
                }
            }

            ComputeSpeedups(report);
            progress?.Report(new BenchmarkProgress { Message = "测试完成", Percent = 100 });
            return report;
        }

        private static int Percent(int finished, int total) =>
            total <= 0 ? 0 : (int)(finished * 100.0 / total);

        private static BenchmarkResultItem MakeCudaUnavailableItem(string name, string category, BenchmarkOptions opt)
        {
            return new BenchmarkResultItem
            {
                Name = name,
                Category = category,
                RequestedDevice = "GPU",
                ActualDevice = "不可用",
                TargetFps = (int)opt.Scenario,
                Error = "CUDA 不可用：当前 ONNX Runtime 未启用 CUDA 执行提供器，或显卡驱动/CUDA 运行时缺失"
            };
        }

        // ==================== MediaPipe ====================

        private async Task<BenchmarkResultItem> RunMediaPipeAsync(
            BenchmarkOptions opt, SKBitmap frame, string device,
            IProgress<BenchmarkProgress>? progress, int finished, int total, CancellationToken ct)
        {
            var item = new BenchmarkResultItem
            {
                Name = "MediaPipe 手部（palm+landmark）",
                Category = "MediaPipe 手部",
                RequestedDevice = device,
                TargetFps = (int)opt.Scenario
            };

            var palmPath = FindModelFile(PalmModelFileName);
            var landmarkPath = FindModelFile(LandmarkModelFileName);
            if (palmPath == null || landmarkPath == null)
            {
                item.ActualDevice = "-";
                item.Error = $"未找到 MediaPipe 模型文件（models/ 下需有 {PalmModelFileName} 与 {LandmarkModelFileName}）";
                return item;
            }

            await Task.Run(() =>
            {
                var cfg = new HandPoseEstimationConfig
                {
                    PalmModelPath = palmPath,
                    LandmarkModelPath = landmarkPath,
                    UseGpu = device == "GPU",
                    // ⚠️ 显式固化这两个值，保证结果可复现且贴近线上真实开销：
                    // InferenceInterval=1 → 不跳帧，每帧都真跑模型（否则测的是"跳过帧"的假高帧率）；
                    // MaxNumHands=2 → 与线上默认一致，端到端含最多 2 遍 palm 多手抑制。
                    InferenceInterval = 1,
                    MaxNumHands = 2
                };

                using var detector = new MediaPipeHandDetector(cfg);
                detector.Initialize();

                if (!detector.IsInitialized)
                {
                    item.ActualDevice = "-";
                    item.Error = "MediaPipe 模型初始化失败";
                    return;
                }

                item.ActualDevice = detector.UsingGpu ? "GPU" : "CPU";
                item.FellBackToCpu = device == "GPU" && !detector.UsingGpu;

                // 1) 预热
                for (int i = 0; i < opt.WarmupFrames && !ct.IsCancellationRequested; i++)
                    detector.DetectHands(frame);

                // 2) 计时：端到端
                var samples = new List<double>();
                for (int i = 0; i < opt.TestFrames && !ct.IsCancellationRequested; i++)
                {
                    var sw = Stopwatch.StartNew();
                    detector.DetectHands(frame);
                    sw.Stop();
                    samples.Add(sw.Elapsed.TotalMilliseconds);

                    if (i % 5 == 0)
                    {
                        progress?.Report(new BenchmarkProgress
                        {
                            Message = $"MediaPipe 手部（{device}）{i + 1}/{opt.TestFrames}",
                            Percent = Percent(finished, total)
                        });
                    }
                }

                if (ct.IsCancellationRequested)
                {
                    item.Error = "已取消";
                    return;
                }

                ApplyStats(item, samples);

                // 3) 分阶段耗时（额外少量帧，不计入 FPS）
                int stageFrames = Math.Min(10, Math.Max(1, opt.TestFrames));
                double palmSum = 0, landmarkSum = 0;
                for (int i = 0; i < stageFrames; i++)
                {
                    var (pm, lm) = detector.MeasureStageLatency(frame);
                    palmSum += pm;
                    landmarkSum += lm;
                }
                item.PalmMs = palmSum / stageFrames;
                item.LandmarkMs = landmarkSum / stageFrames;
            }, ct);

            return item;
        }

        // ==================== YOLO ====================

        private async Task<BenchmarkResultItem> RunYoloAsync(
            BenchmarkOptions opt, SKBitmap frame, string modelPath, string device,
            IProgress<BenchmarkProgress>? progress, int finished, int total, CancellationToken ct)
        {
            var item = new BenchmarkResultItem
            {
                Name = Path.GetFileName(modelPath),
                Category = "YOLO 检测",
                RequestedDevice = device,
                TargetFps = (int)opt.Scenario
            };

            if (!File.Exists(modelPath))
            {
                item.ActualDevice = "-";
                item.Error = "模型文件不存在";
                return item;
            }

            var service = new YoloDetectionService();
            try
            {
                var info = new ModelInfo
                {
                    ModelPath = modelPath,
                    UseGpu = device == "GPU",
                    GpuId = opt.GpuId,
                    Name = Path.GetFileName(modelPath)
                };

                var ok = await service.InitializeAsync(info);
                if (!ok)
                {
                    item.ActualDevice = "-";
                    item.Error = device == "GPU"
                        ? "GPU(CUDA) 初始化失败：CUDA 不可用，未静默回退 CPU"
                        : "模型初始化失败";
                    return item;
                }

                item.ActualDevice = device;

                // 复用一份副本，避免把每帧的图像拷贝开销计入推理耗时
                using var work = frame.Copy() ?? throw new InvalidOperationException("测试帧拷贝失败");

                for (int i = 0; i < opt.WarmupFrames && !ct.IsCancellationRequested; i++)
                    await service.DetectAsync(work);

                var samples = new List<double>();
                for (int i = 0; i < opt.TestFrames && !ct.IsCancellationRequested; i++)
                {
                    var sw = Stopwatch.StartNew();
                    await service.DetectAsync(work);
                    sw.Stop();
                    samples.Add(sw.Elapsed.TotalMilliseconds);

                    if (i % 5 == 0)
                    {
                        progress?.Report(new BenchmarkProgress
                        {
                            Message = $"{item.Name}（{device}）{i + 1}/{opt.TestFrames}",
                            Percent = Percent(finished, total)
                        });
                    }
                }

                if (ct.IsCancellationRequested)
                {
                    item.Error = "已取消";
                    return item;
                }

                ApplyStats(item, samples);
                return item;
            }
            finally
            {
                service.Dispose();
            }
        }

        // ==================== 统计与辅助 ====================

        private static void ApplyStats(BenchmarkResultItem item, List<double> samples)
        {
            if (samples.Count == 0)
            {
                item.Error ??= "无有效样本";
                return;
            }

            var sorted = samples.OrderBy(x => x).ToList();
            item.AvgMs = samples.Average();
            item.MinMs = sorted[0];
            item.MaxMs = sorted[^1];
            item.P50Ms = sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.50))];
            item.P95Ms = sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.95))];
            item.AvgFps = item.AvgMs > 0 ? 1000.0 / item.AvgMs : 0;
        }

        private static void ComputeSpeedups(BenchmarkReport report)
        {
            foreach (var group in report.Items.Where(i => i.Error == null).GroupBy(i => i.Name))
            {
                var cpu = group.FirstOrDefault(i => i.ActualDevice == "CPU");
                var gpu = group.FirstOrDefault(i => i.ActualDevice == "GPU");
                if (cpu != null && gpu != null && cpu.AvgFps > 0)
                    report.Speedups[group.Key] = gpu.AvgFps / cpu.AvgFps;
            }
        }

        private static SKBitmap? PrepareFrame(BenchmarkOptions opt, Func<SKBitmap?>? realFrameProvider)
        {
            if (opt.UseRealFrame)
            {
                try
                {
                    var real = realFrameProvider?.Invoke();
                    if (real != null)
                        return real.Copy();
                }
                catch
                {
                    // 真实帧不可用 → 回退合成帧
                }
            }
            return CreateSyntheticFrame(opt.Width, opt.Height);
        }

        /// <summary>
        /// 生成合成测试帧：固定随机种子的色块图案。
        /// 使用固定种子保证同一台机器多次测试结果可复现、不同机器之间横向可比。
        /// </summary>
        public static SKBitmap CreateSyntheticFrame(int width, int height)
        {
            width = Math.Clamp(width, 64, 4096);
            height = Math.Clamp(height, 64, 4096);

            var bmp = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bmp);
            canvas.Clear(new SKColor(58, 66, 74));

            var rnd = new Random(20260927);
            using var paint = new SKPaint { IsAntialias = true };
            for (int i = 0; i < 28; i++)
            {
                paint.Color = new SKColor((byte)rnd.Next(40, 240), (byte)rnd.Next(40, 240), (byte)rnd.Next(40, 240));
                float x = rnd.Next(0, width);
                float y = rnd.Next(0, height);
                float w = rnd.Next(width / 12, width / 4);
                float h = rnd.Next(height / 12, height / 4);
                canvas.DrawRect(SKRect.Create(x, y, w, h), paint);
            }

            // 中心画一个类肤色区域，尽量让手掌检测有候选（即使检不出，分阶段计时也会强制跑 landmark）
            paint.Color = new SKColor(210, 170, 150);
            canvas.DrawOval(SKRect.Create(width * 0.3f, height * 0.25f, width * 0.4f, height * 0.5f), paint);

            return bmp;
        }

        /// <summary>
        /// 逐级向上查找模型文件（避免硬编码 ..\\..\\ 层数）。
        /// </summary>
        public static string? FindModelFile(string fileName)
        {
            // 上限 8 层：从 bin/Debug/net8.0-windows 回到项目根需要 7 层（net8.0-windows→Debug→bin→UI工程→UI→src→根→..），
            // 取 8 留一层余量。层数写小会静默找不到模型。
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                var candidate = Path.Combine(dir.FullName, "models", fileName);
                if (File.Exists(candidate)) return candidate;

                candidate = Path.Combine(dir.FullName, fileName);
                if (File.Exists(candidate)) return candidate;

                dir = dir.Parent;
            }
            return null;
        }

        // ==================== 环境信息 ====================

        /// <summary>
        /// 采集运行环境信息（含 CUDA 可用性探测，可能耗时数百毫秒，建议在后台线程调用）。
        /// </summary>
        public static BenchmarkEnvironment GetEnvironment()
        {
            var env = new BenchmarkEnvironment();

            try { env.OS = RuntimeInformation.OSDescription; } catch { env.OS = "未知"; }
            try { env.Cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? ""; } catch { }
            env.CpuCores = Environment.ProcessorCount;

            try
            {
                env.TotalMemoryMB = (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024);
            }
            catch { }

            try
            {
                env.OnnxRuntimeVersion = typeof(InferenceSession).Assembly.GetName().Version?.ToString() ?? "";
            }
            catch { }

            // CUDA 探测：用真实模型建一次 CUDA 会话，最可靠（仅追加 EP 不足以判断驱动/运行时是否可用）
            var probeModel = FindModelFile(PalmModelFileName) ?? FindModelFile(LandmarkModelFileName);
            env.CudaAvailable = ProbeCuda(probeModel);
            if (env.CudaAvailable)
                env.GpuName = QueryGpuName();

            return env;
        }

        private static bool ProbeCuda(string? probeModelPath)
        {
            try
            {
                using var options = new SessionOptions();
                options.AppendExecutionProvider_CUDA(0);

                if (string.IsNullOrEmpty(probeModelPath) || !File.Exists(probeModelPath))
                    return true; // 无模型可探测，仅能确认 EP 可追加

                using var session = new InferenceSession(probeModelPath, options);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string QueryGpuName()
        {
            try
            {
                var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=name --format=csv,noheader")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return "";
                if (!p.WaitForExit(3000))
                {
                    p.Kill();
                    return "";
                }
                var output = p.StandardOutput.ReadToEnd();
                var first = output.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                return first?.Trim() ?? "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 扫描可用于基准测试的 YOLO 模型（yolo_models 目录下的 onnx）。
        /// </summary>
        public static List<string> ScanYoloModels()
        {
            var results = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                for (int i = 0; i < 8 && dir != null; i++)
                {
                    // 1) yolo_models（主来源，位于项目根，从 bin 向上 7 层）
                    var yoloDir = Path.Combine(dir.FullName, "yolo_models");
                    if (Directory.Exists(yoloDir))
                    {
                        foreach (var f in Directory.GetFiles(yoloDir, "*.onnx", SearchOption.TopDirectoryOnly))
                        {
                            if (seen.Add(Path.GetFileName(f))) results.Add(f);
                        }
                        break;
                    }
                    dir = dir.Parent;
                }

                // 2) 兜底：部分部署会把模型统一复制到 bin/models/ 下，
                //    此时 yolo_models 不存在，需从 models 里挑出非 MediaPipe 的检测模型。
                var modelsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models");
                if (Directory.Exists(modelsDir))
                {
                    foreach (var f in Directory.GetFiles(modelsDir, "*.onnx", SearchOption.TopDirectoryOnly))
                    {
                        var name = Path.GetFileName(f);
                        if (name.Equals(PalmModelFileName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (name.Equals(LandmarkModelFileName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (seen.Add(name)) results.Add(f);
                    }
                }
            }
            catch { }

            return results.OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
