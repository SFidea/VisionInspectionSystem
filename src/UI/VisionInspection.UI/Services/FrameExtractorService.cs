using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VisionInspection.UI.Services
{
    using VisionInspection.UI.Models;

    /// <summary>
    /// 抽帧服务：ffmpeg 粗抽 → OpenCV 精筛（清晰度/亮度/对比度/去重）→ 命名落盘 + 元数据。
    /// 本类不依赖 WPF，便于离线控制台测试。
    /// </summary>
    public class FrameExtractorService
    {
        /// <summary>
        /// 探测视频信息（时长/帧率/帧数/分辨率）
        /// </summary>
        public async Task<VideoMeta> ProbeAsync(string videoPath, CancellationToken ct = default)
        {
            if (!FfmpegLocator.EnsureAvailable(out var ffmpeg, out var ffprobe, out var err))
                throw new InvalidOperationException(err);

            var meta = new VideoMeta();

            var psi = new ProcessStartInfo(ffprobe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-select_streams");
            psi.ArgumentList.Add("v:0");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("stream=width,height,r_frame_rate,nb_frames,avg_frame_rate");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("format=duration");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("json");
            psi.ArgumentList.Add(videoPath);

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 ffprobe");
            var stdout = await p.StandardOutput.ReadToEndAsync();
            await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync(ct);

            try
            {
                using var doc = JsonDocument.Parse(stdout);
                if (doc.RootElement.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0)
                {
                    var s = streams[0];
                    if (s.TryGetProperty("width", out var w)) meta.Width = GetInt32(w);
                    if (s.TryGetProperty("height", out var h)) meta.Height = GetInt32(h);

                    if (s.TryGetProperty("avg_frame_rate", out var afr) && TryParseFrameRate(GetString(afr), out var rf))
                        meta.Fps = rf;
                    if (meta.Fps <= 0 && s.TryGetProperty("r_frame_rate", out var rfr) && TryParseFrameRate(GetString(rfr), out var rf2))
                        meta.Fps = rf2;

                    // nb_frames 有时是字符串（"1980"），有时是数字，必须兼容
                    if (s.TryGetProperty("nb_frames", out var nf))
                        meta.FrameCount = GetInt64(nf);
                }

                if (doc.RootElement.TryGetProperty("format", out var fmt) &&
                    fmt.TryGetProperty("duration", out var dur))
                {
                    meta.DurationSec = GetDouble(dur);
                }
            }
            catch
            {
                // 解析失败保持默认值
            }

            if (meta.DurationSec <= 0 && meta.FrameCount > 0 && meta.Fps > 0)
                meta.DurationSec = meta.FrameCount / meta.Fps;

            if (meta.FrameCount <= 0 && meta.DurationSec > 0 && meta.Fps > 0)
                meta.FrameCount = (long)Math.Round(meta.DurationSec * meta.Fps);

            return meta;
        }

        /// <summary>
        /// 快速预估：按当前策略粗抽会产生多少候选帧（不落盘、不解码）。
        /// </summary>
        public async Task<int> EstimateAsync(string videoPath, ExtractConfig cfg, CancellationToken ct = default)
        {
            var meta = await ProbeAsync(videoPath, ct);
            if (cfg.MaxOutputCount <= 0) cfg.MaxOutputCount = int.MaxValue;

            return EstimateCandidateCount(meta, cfg);
        }

        /// <summary>
        /// 执行抽帧全流程
        /// </summary>
        public async Task<ExtractSummary> ExtractAsync(
            string videoPath,
            string sessionDir,
            ExtractConfig cfg,
            NamingConfig naming,
            IProgress<AcquireProgress>? progress = null,
            CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            var summary = new ExtractSummary { SessionDir = sessionDir };

            if (!File.Exists(videoPath))
                throw new FileNotFoundException("源视频不存在", videoPath);

            if (!FfmpegLocator.EnsureAvailable(out var ffmpeg, out _, out var err))
                throw new InvalidOperationException(err);

            var imagesDir = System.IO.Path.Combine(sessionDir, "images");
            Directory.CreateDirectory(imagesDir);
            summary.ImageDir = imagesDir;

            var tmpDir = System.IO.Path.Combine(sessionDir, "_tmp");
            Directory.CreateDirectory(tmpDir);

            // ---- ① 探测 ----
            progress?.Report(new AcquireProgress { Phase = "探测", Message = "读取视频信息...", Percent = 2 });
            var meta = await ProbeAsync(videoPath, ct);
            ct.ThrowIfCancellationRequested();

            var ext = NormalizeExt(cfg.OutputFormat);
            var outPattern = System.IO.Path.Combine(tmpDir, "f_%08d." + ext);

            // ---- ② 粗抽 ----
            progress?.Report(new AcquireProgress { Phase = "粗抽", Message = "ffmpeg 抽帧中...", Percent = 5 });
            var filters = BuildFilters(cfg, meta, out var stepFrames, out var needVfr);
            var args = BuildFfmpegArgs(videoPath, cfg, meta, filters, needVfr, outPattern, ext);

            var (candidates, exitCode) = await RunCoarseExtractionAsync(ffmpeg, args, progress, ct, tmpDir, ext);
            summary.CandidateCount = candidates.Count;

            if (candidates.Count == 0)
            {
                if (cfg.CleanupTemp) TryDeleteDir(tmpDir);
                summary.ElapsedSec = sw.Elapsed.TotalSeconds;
                summary.Message = "粗抽未产出任何帧。" +
                    (cfg.Strategy == ExtractStrategy.SceneChange
                        ? $"场景阈值 {cfg.SceneThreshold} 可能过高（该视频画面变化量不足），建议降到 0.05 以下。"
                        : "请检查采样参数或视频内容。");
                return summary;
            }

            // 数量守护
            if (cfg.MaxOutputCount > 0 && candidates.Count > cfg.MaxOutputCount)
                candidates = candidates.Take(cfg.MaxOutputCount).ToList();

            // ---- ③ 精筛 ----
            progress?.Report(new AcquireProgress { Phase = "精筛", Message = $"分析 {candidates.Count} 张候选帧...", Percent = 45 });
            var metrics = await ScoreAllAsync(candidates, cfg.WorkerThreads, progress, ct);

            var kept = new List<FrameRecord>();
            var keptHashes = new List<ulong>();
            var keptHists = new List<double[]>();
            HashSet<ulong>? crossSession = cfg.CrossSessionDedup ? LoadDupIndex(sessionDir) : null;

            double timeStepSec = 0;
            if (stepFrames > 0 && meta.Fps > 0) timeStepSec = stepFrames / meta.Fps;
            else if (cfg.Strategy == ExtractStrategy.TargetFps) timeStepSec = 1.0 / Math.Max(cfg.TargetFps, 0.01);
            else if (cfg.Strategy == ExtractStrategy.IntervalMs) timeStepSec = cfg.IntervalMs / 1000.0;

            var index = naming.StartIndex;
            for (var i = 0; i < candidates.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var m = metrics[i];

                if (cfg.EnableQualityFilter)
                {
                    if (m.Sharpness < cfg.MinSharpness) { summary.RejectedBlur++; TryDelete(candidates[i]); continue; }
                    if (m.Brightness < cfg.MinBrightness || m.Brightness > cfg.MaxBrightness) { summary.RejectedBrightness++; TryDelete(candidates[i]); continue; }
                    if (m.Contrast < cfg.MinContrast) { summary.RejectedContrast++; TryDelete(candidates[i]); continue; }
                }

                if (cfg.EnableDedup && IsDuplicate(m, cfg, keptHashes, keptHists, crossSession))
                {
                    summary.RejectedDuplicate++;
                    TryDelete(candidates[i]);
                    continue;
                }

                // 保留
                double srcTime = -1;
                if (cfg.Strategy == ExtractStrategy.SceneChange) srcTime = -1;
                else srcTime = cfg.StartTimeSec + i * timeStepSec;

                var name = RenderFileName(naming.FilePattern, sessionDir, index, srcTime);
                var dest = System.IO.Path.Combine(imagesDir, name + "." + ext);

                try
                {
                    File.Move(candidates[i], dest, true);
                }
                catch
                {
                    TryDelete(candidates[i]);
                    continue;
                }

                var rec = new FrameRecord
                {
                    FileName = System.IO.Path.GetFileName(dest),
                    SourceTimeSec = Math.Round(srcTime, 3),
                    Sharpness = Math.Round(m.Sharpness, 1),
                    Brightness = Math.Round(m.Brightness, 1),
                    Contrast = Math.Round(m.Contrast, 1)
                };

                kept.Add(rec);
                keptHashes.Add(cfg.Method == DedupMethod.DHash ? m.DHash : m.PHash);
                keptHists.Add(m.Histogram);

                if (crossSession != null) crossSession.Add(cfg.Method == DedupMethod.DHash ? m.DHash : m.PHash);

                index++;

                progress?.Report(new AcquireProgress
                {
                    Phase = "精筛",
                    Message = $"已保留 {kept.Count} / {candidates.Count}",
                    Percent = 45 + 45.0 * i / candidates.Count
                });
            }

            summary.Records = kept;
            summary.KeptCount = kept.Count;

            if (crossSession != null) SaveDupIndex(sessionDir, crossSession);

            // ---- ④ 元数据 ----
            if (naming.ExportMeta)
                WriteMetadata(sessionDir, videoPath, meta, cfg, naming, summary);

            if (cfg.CleanupTemp) TryDeleteDir(tmpDir);

            sw.Stop();
            summary.ElapsedSec = Math.Round(sw.Elapsed.TotalSeconds, 2);
            summary.Message = $"候选 {summary.CandidateCount} → 保留 {summary.KeptCount}";

            progress?.Report(new AcquireProgress { Phase = "完成", Message = summary.Message, Percent = 100 });
            return summary;
        }

        // ==================== 内部实现 ====================

        private static async Task<(List<string> Files, int ExitCode)> RunCoarseExtractionAsync(
            string ffmpeg, List<string> args, IProgress<AcquireProgress>? progress, CancellationToken ct,
            string tmpDir, string ext)
        {
            var psi = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 ffmpeg");

            var errorBuilder = new StringBuilder();
            var stdoutTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await p.StandardOutput.ReadLineAsync()) != null)
                {
                    if (line.StartsWith("frame=", StringComparison.Ordinal) &&
                        int.TryParse(line.AsSpan(6).Trim(), out var frame))
                    {
                        progress?.Report(new AcquireProgress
                        {
                            Phase = "粗抽",
                            Message = $"ffmpeg 已处理 {frame} 帧",
                            Percent = 5 + Math.Min(35, frame / 20.0)
                        });
                    }
                }
            }, ct);

            var errorTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await p.StandardError.ReadLineAsync()) != null)
                {
                    if (errorBuilder.Length < 4000) errorBuilder.AppendLine(line);
                }
            }, ct);

            await p.WaitForExitAsync(ct);
            try { await Task.WhenAll(stdoutTask, errorTask); } catch { /* 忽略读取中断 */ }

            // 注意：select 类过滤可能一帧都选不出来（例如场景阈值过高），
            // 此时 ffmpeg 会非零退出。不能一概抛异常，交给上层按"实际产出文件数"判断。
            if (p.ExitCode != 0)
                Debug.WriteLine("[抽帧] ffmpeg 非零退出 " + p.ExitCode + "：" + SplitTail(errorBuilder.ToString(), 400));

            var collected = Directory.Exists(tmpDir)
                ? Directory.GetFiles(tmpDir, "*." + ext).OrderBy(x => x, StringComparer.Ordinal).ToList()
                : new List<string>();

            return (collected, p.ExitCode);
        }

        private int EstimateCandidateCount(VideoMeta meta, ExtractConfig cfg)
        {
            if (meta.DurationSec <= 0 && meta.FrameCount <= 0) return 0;

            var startSec = Math.Max(0, cfg.StartTimeSec);
            var endSec = cfg.EndTimeSec > 0 ? Math.Min(cfg.EndTimeSec, meta.DurationSec) : meta.DurationSec;
            var duration = Math.Max(0, endSec - startSec);
            if (duration <= 0 && meta.DurationSec > 0) duration = meta.DurationSec;

            long totalFrames = meta.FrameCount;
            if (totalFrames <= 0 && meta.Fps > 0) totalFrames = (long)Math.Round(meta.DurationSec * meta.Fps);

            double rangeFrames = totalFrames;
            if (meta.DurationSec > 0 && totalFrames > 0)
                rangeFrames = totalFrames * (duration / meta.DurationSec);

            return cfg.Strategy switch
            {
                ExtractStrategy.TargetCount => Math.Max(1, cfg.TargetCount),
                ExtractStrategy.IntervalFrames => (int)Math.Max(1, Math.Round(rangeFrames / Math.Max(1, cfg.IntervalFrames))),
                ExtractStrategy.IntervalMs => (int)Math.Max(1, Math.Round(duration * 1000.0 / Math.Max(1, cfg.IntervalMs))),
                ExtractStrategy.TargetFps => (int)Math.Max(1, Math.Round(duration * Math.Max(0.01, cfg.TargetFps))),
                ExtractStrategy.SceneChange => -1, // 由画面变化量决定，无法预估
                _ => 0
            };
        }

        private static List<string> BuildFilters(ExtractConfig cfg, VideoMeta meta, out int stepFrames, out bool needVfr)
        {
            stepFrames = 0;
            needVfr = false;

            var files = new List<string>();
            double outFps = 0;

            switch (cfg.Strategy)
            {
                case ExtractStrategy.TargetCount:
                case ExtractStrategy.IntervalFrames:
                    {
                        // 计算生效范围内的帧数，再均匀取
                        var totalFrames = meta.FrameCount;
                        if (totalFrames <= 0 && meta.Fps > 0) totalFrames = (long)Math.Round(meta.DurationSec * meta.Fps);

                        var startSec = Math.Max(0, cfg.StartTimeSec);
                        var endSec = cfg.EndTimeSec > 0 ? Math.Min(cfg.EndTimeSec, meta.DurationSec) : meta.DurationSec;
                        var duration = Math.Max(0, endSec - startSec);
                        if (duration <= 0) duration = meta.DurationSec;

                        double rangeFrames = totalFrames;
                        if (meta.DurationSec > 0 && totalFrames > 0)
                            rangeFrames = totalFrames * (duration / meta.DurationSec);

                        var step = cfg.Strategy == ExtractStrategy.IntervalFrames
                            ? Math.Max(1, cfg.IntervalFrames)
                            : (int)Math.Max(1, Math.Round(rangeFrames / Math.Max(1, cfg.TargetCount)));

                        stepFrames = step;
                        files.Add($"select='not(mod(n\\,{step}))'");
                        needVfr = true;
                        break;
                    }
                case ExtractStrategy.IntervalMs:
                    outFps = 1000.0 / Math.Max(1, cfg.IntervalMs);
                    files.Add($"fps={outFps.ToString(CultureInfo.InvariantCulture)}");
                    break;
                case ExtractStrategy.TargetFps:
                    outFps = Math.Max(0.01, cfg.TargetFps);
                    files.Add($"fps={outFps.ToString(CultureInfo.InvariantCulture)}");
                    break;
                case ExtractStrategy.SceneChange:
                    files.Add($"select='gt(scene\\,{cfg.SceneThreshold.ToString(CultureInfo.InvariantCulture)})'");
                    needVfr = true;
                    break;
            }

            // 缩放
            switch (cfg.Resize)
            {
                case AutoResizeMode.LimitLongEdge:
                    files.Add($"scale=w='min({cfg.MaxLongEdge},iw)':h='min({cfg.MaxLongEdge},ih)':force_original_aspect_ratio=decrease");
                    break;
                case AutoResizeMode.FixedSize:
                    files.Add($"scale={cfg.FixedWidth}:{cfg.FixedHeight}:force_original_aspect_ratio=decrease");
                    break;
                case AutoResizeMode.Percent:
                    files.Add($"scale=iw*{cfg.PercentScale}/100:ih*{cfg.PercentScale}/100");
                    break;
            }

            if (cfg.Grayscale) files.Add("hue=s=0");

            return files;
        }

        private static List<string> BuildFfmpegArgs(string videoPath, ExtractConfig cfg, VideoMeta meta,
            List<string> filters, bool needVfr, string outPattern, string ext)
        {
            var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-progress", "pipe:1", "-nostats" };

            // 输入前 seek
            if (cfg.StartTimeSec > 0)
            {
                args.Add("-ss");
                args.Add(cfg.StartTimeSec.ToString(CultureInfo.InvariantCulture));
            }

            args.Add("-i");
            args.Add(videoPath);

            // 输出时长限制
            if (cfg.EndTimeSec > 0)
            {
                var start = Math.Max(0, cfg.StartTimeSec);
                var dur = Math.Max(0, Math.Min(cfg.EndTimeSec, meta.DurationSec) - start);
                args.Add("-t");
                args.Add(dur.ToString(CultureInfo.InvariantCulture));
            }

            if (filters.Count > 0)
            {
                args.Add("-vf");
                args.Add(string.Join(",", filters));
            }

            if (needVfr) args.Add("-vsync");
            if (needVfr) args.Add("vfr");

            if (ext == "jpg")
            {
                args.Add("-q:v");
                args.Add(MapJpegQuality(cfg.JpegQuality).ToString(CultureInfo.InvariantCulture));
            }
            else if (ext == "png")
            {
                args.Add("-compression_level");
                args.Add("6");
            }

            args.Add(outPattern);
            return args;
        }

        private static int MapJpegQuality(int quality01To100)
        {
            var q = Math.Clamp(quality01To100, 1, 100);
            var v = (int)Math.Round(2 + (100 - q) * 29.0 / 100.0);
            return Math.Clamp(v, 2, 31);
        }

        private async Task<List<FrameMetrics>> ScoreAllAsync(List<string> files, int workerThreads,
            IProgress<AcquireProgress>? progress, CancellationToken ct)
        {
            var metrics = new FrameMetrics[files.Count];
            var opts = new ParallelOptions
            {
                MaxDegreeOfParallelism = workerThreads > 0 ? workerThreads : Math.Max(1, Environment.ProcessorCount / 2),
                CancellationToken = ct
            };

            var done = 0;
            await Task.Run(() =>
            {
                Parallel.For(0, files.Count, opts, i =>
                {
                    ct.ThrowIfCancellationRequested();
                    metrics[i] = FrameQualityAnalyzer.Analyze(files[i]);
                    var d = Interlocked.Increment(ref done);
                    if (d % 10 == 0 || d == files.Count)
                    {
                        progress?.Report(new AcquireProgress
                        {
                            Phase = "打分",
                            Message = $"分析质量 {d} / {files.Count}",
                            Percent = 40 + 5.0 * d / files.Count
                        });
                    }
                });
            }, ct);

            return metrics.ToList();
        }

        private static bool IsDuplicate(FrameMetrics m, ExtractConfig cfg, List<ulong> keptHashes,
            List<double[]> keptHists, HashSet<ulong>? crossSession)
        {
            var hash = cfg.Method == DedupMethod.DHash ? m.DHash : m.PHash;
            if (crossSession != null && hash != 0 && crossSession.Contains(hash)) return true;

            var list = cfg.Scope == DedupScope.AllKept ? keptHashes : (keptHashes.Count > 0 ? new List<ulong> { keptHashes[^1] } : new List<ulong>());

            if (cfg.Method == DedupMethod.Histogram)
            {
                var hists = cfg.Scope == DedupScope.AllKept ? keptHists : (keptHists.Count > 0 ? new List<double[]> { keptHists[^1] } : new List<double[]>());
                foreach (var h in hists)
                {
                    if (FrameQualityAnalyzer.HistogramSimilarity(m.Histogram, h) >= cfg.HistogramThreshold) return true;
                }
                return false;
            }

            foreach (var k in list)
            {
                if (k == 0) continue;
                if (FrameQualityAnalyzer.HammingDistance(hash, k) <= cfg.HammingThreshold) return true;
            }

            return false;
        }

        private static HashSet<ulong> LoadDupIndex(string sessionDir)
        {
            var set = new HashSet<ulong>();
            try
            {
                var parent = Directory.GetParent(sessionDir.TrimEnd('\\', '/'));
                if (parent == null) return set;
                var file = System.IO.Path.Combine(parent.FullName, "_dedup_index.txt");
                if (!File.Exists(file)) return set;
                foreach (var line in File.ReadAllLines(file))
                {
                    if (ulong.TryParse(line, out var v)) set.Add(v);
                }
            }
            catch { }

            return set;
        }

        private static void SaveDupIndex(string sessionDir, HashSet<ulong> set)
        {
            try
            {
                var parent = Directory.GetParent(sessionDir.TrimEnd('\\', '/'));
                if (parent == null) return;
                var file = System.IO.Path.Combine(parent.FullName, "_dedup_index.txt");
                Directory.CreateDirectory(parent.FullName);
                File.WriteAllLines(file, set.Select(x => x.ToString()));
            }
            catch { }
        }

        private static void WriteMetadata(string sessionDir, string videoPath, VideoMeta meta,
            ExtractConfig cfg, NamingConfig naming, ExtractSummary summary)
        {
            try
            {
                Directory.CreateDirectory(sessionDir);

                var sb = new StringBuilder();
                sb.AppendLine("file_name,source_time_sec,sharpness,brightness,contrast");
                foreach (var r in summary.Records)
                {
                    sb.AppendLine(string.Join(",",
                        r.FileName,
                        r.SourceTimeSec.ToString(CultureInfo.InvariantCulture),
                        r.Sharpness.ToString(CultureInfo.InvariantCulture),
                        r.Brightness.ToString(CultureInfo.InvariantCulture),
                        r.Contrast.ToString(CultureInfo.InvariantCulture)));
                }
                File.WriteAllText(System.IO.Path.Combine(sessionDir, "frames.csv"), sb.ToString());

                var metaObj = new Dictionary<string, object>
                {
                    ["source_video"] = videoPath,
                    ["probe"] = new Dictionary<string, object>
                    {
                        ["duration_sec"] = meta.DurationSec,
                        ["fps"] = meta.Fps,
                        ["frame_count"] = meta.FrameCount,
                        ["width"] = meta.Width,
                        ["height"] = meta.Height
                    },
                    ["extract"] = new Dictionary<string, object>
                    {
                        ["strategy"] = cfg.Strategy.ToString(),
                        ["target_count"] = cfg.TargetCount,
                        ["interval_frames"] = cfg.IntervalFrames,
                        ["interval_ms"] = cfg.IntervalMs,
                        ["target_fps"] = cfg.TargetFps,
                        ["scene_threshold"] = cfg.SceneThreshold,
                        ["start_time_sec"] = cfg.StartTimeSec,
                        ["end_time_sec"] = cfg.EndTimeSec,
                        ["quality_filter"] = cfg.EnableQualityFilter,
                        ["min_sharpness"] = cfg.MinSharpness,
                        ["min_brightness"] = cfg.MinBrightness,
                        ["max_brightness"] = cfg.MaxBrightness,
                        ["min_contrast"] = cfg.MinContrast,
                        ["dedup"] = cfg.EnableDedup,
                        ["dedup_method"] = cfg.Method.ToString(),
                        ["hamming_threshold"] = cfg.HammingThreshold,
                        ["histogram_threshold"] = cfg.HistogramThreshold,
                        ["dedup_scope"] = cfg.Scope.ToString(),
                        ["resize"] = cfg.Resize.ToString(),
                        ["max_long_edge"] = cfg.MaxLongEdge,
                        ["grayscale"] = cfg.Grayscale,
                        ["output_format"] = cfg.OutputFormat,
                        ["jpeg_quality"] = cfg.JpegQuality
                    },
                    ["naming"] = new Dictionary<string, object>
                    {
                        ["file_pattern"] = naming.FilePattern,
                        ["start_index"] = naming.StartIndex
                    },
                    ["result"] = new Dictionary<string, object>
                    {
                        ["candidate_count"] = summary.CandidateCount,
                        ["kept_count"] = summary.KeptCount,
                        ["rejected_blur"] = summary.RejectedBlur,
                        ["rejected_brightness"] = summary.RejectedBrightness,
                        ["rejected_contrast"] = summary.RejectedContrast,
                        ["rejected_duplicate"] = summary.RejectedDuplicate,
                        ["elapsed_sec"] = summary.ElapsedSec
                    }
                };

                File.WriteAllText(System.IO.Path.Combine(sessionDir, "meta.json"),
                    JsonSerializer.Serialize(metaObj, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // 元数据写入失败不影响主流程
            }
        }

        public static string RenderFileName(string pattern, string sessionDir, int index, double sourceTimeSec)
        {
            var session = System.IO.Path.GetFileName(sessionDir.TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(pattern)) pattern = "{session}_{index:D6}";

            var ts = sourceTimeSec >= 0 ? sourceTimeSec : 0;

            return Regex.Replace(pattern, @"\{(\w+)(?::([^}]+))?\}", match =>
            {
                var name = match.Groups[1].Value.ToLowerInvariant();
                var fmt = match.Groups[2].Success ? match.Groups[2].Value : "";

                string Render(object v) => string.IsNullOrEmpty(fmt) ? Convert.ToString(v, CultureInfo.InvariantCulture)
                                                                     : string.Format(CultureInfo.InvariantCulture, "{0:" + fmt + "}", v);

                return name switch
                {
                    "session" => session,
                    "index" or "i" => Render(index),
                    "frame" or "n" => Render(sourceTimeSec >= 0 ? (int)Math.Round(sourceTimeSec * 1000) : 0),
                    "timestamp" or "time" => Render(ts),
                    "time_ms" or "ts" => Render((int)Math.Round(ts * 1000)),
                    _ => match.Value
                };
            });
        }

        public static string NormalizeExt(string format)
        {
            var f = (format ?? "jpg").Trim().TrimStart('.').ToLowerInvariant();
            if (f is "jpeg") return "jpg";
            if (f is not ("jpg" or "png" or "bmp")) return "jpg";
            return f;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        private static string SplitTail(string s, int max)
        {
            return s.Length <= max ? s : "..." + s[^max..];
        }

        // ffprobe JSON 里同一字段可能是字符串也可能是数字，统一取值避免 GetString() 抛 InvalidOperationException
        private static string GetString(JsonElement e)
            => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString();

        private static int GetInt32(JsonElement e)
            => e.ValueKind == JsonValueKind.Number ? e.GetInt32()
               : int.TryParse(GetString(e), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

        private static long GetInt64(JsonElement e)
            => e.ValueKind == JsonValueKind.Number ? e.GetInt64()
               : long.TryParse(GetString(e), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

        private static double GetDouble(JsonElement e)
            => e.ValueKind == JsonValueKind.Number ? e.GetDouble()
               : double.TryParse(GetString(e), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

        private static bool TryParseFrameRate(string? s, out double fps)
        {
            fps = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;

            var parts = s.Split('/');
            try
            {
                if (parts.Length == 2)
                {
                    var a = double.Parse(parts[0], CultureInfo.InvariantCulture);
                    var b = double.Parse(parts[1], CultureInfo.InvariantCulture);
                    if (b != 0) { fps = a / b; return true; }
                }
                else
                {
                    fps = double.Parse(s, CultureInfo.InvariantCulture);
                    return true;
                }
            }
            catch { }

            return false;
        }
    }
}
