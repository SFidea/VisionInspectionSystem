using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using VisionInspection.Core.Services;

namespace VisionInspection.UI.Services
{
    using VisionInspection.UI.Models;

    /// <summary>
    /// 自动预标注：用已初始化的检测服务对抽出的帧推理，导出 YOLO 格式标注。
    /// 输出：<labelDir>/<同名>.txt  —— class_id cx cy w h（全部归一化，中心点坐标）
    /// 坐标取自 DetectedObject.PixelBoundingBox（像素），按实际图片宽高归一化，避免归一化字段歧义。
    /// </summary>
    public class AutoLabelExporter
    {
        private readonly IDetectionService _detector;

        public AutoLabelExporter(IDetectionService detector)
        {
            _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        }

        public List<string> GetClasses()
        {
            try { return _detector.GetClasses() ?? new List<string>(); }
            catch { return new List<string>(); }
        }

        /// <summary>
        /// 对已抽出的帧批量生成 YOLO 标注。
        /// </summary>
        /// <returns>(标注件数, 无目标帧数, 被移除的文件名列表)</returns>
        public async Task<(int Labeled, int Negative, List<string> Removed)> ExportAsync(
            List<FrameRecord> records,
            string imageDir,
            string labelDir,
            AutoLabelConfig cfg,
            IProgress<AcquireProgress>? progress = null,
            CancellationToken ct = default)
        {
            Directory.CreateDirectory(labelDir);

            int labeled = 0, negative = 0;
            var removed = new List<string>();

            if (cfg.ExportClassesFile)
            {
                try
                {
                    File.WriteAllLines(System.IO.Path.Combine(labelDir, "classes.txt"), GetClasses());
                }
                catch { }
            }

            for (var i = 0; i < records.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var rec = records[i];

                var imagePath = System.IO.Path.Combine(imageDir, rec.FileName);
                if (!File.Exists(imagePath)) continue;

                DetectionResult? result = null;
                try
                {
                    using var bmp = SKBitmap.Decode(imagePath);
                    if (bmp == null) continue;
                    result = await _detector.DetectAsync(bmp);
                }
                catch
                {
                    // 单张推理失败不中断整体流程
                }

                var objects = result?.Objects ?? new List<DetectedObject>();
                objects = objects
                    .Where(o => o.IsInRoi)
                    .Where(o => o.Confidence >= cfg.ConfThreshold && ConfidenceAtLeast(o.Confidence, cfg.ConfThreshold))
                    .ToList();

                if (cfg.MaxInstances > 0 && objects.Count > cfg.MaxInstances) objects = objects.Take(cfg.MaxInstances).ToList();

                rec.Instances = objects.Count;

                var tooFew = cfg.MinInstances > 0 && objects.Count < cfg.MinInstances;
                var empty = objects.Count == 0;

                if (empty && !cfg.KeepNegativeFrames)
                {
                    // 负样本不要 → 删除图片与标注，从结果里剔除
                    try { File.Delete(imagePath); } catch { }
                    removed.Add(rec.FileName);
                    negative++;
                    continue;
                }

                if (tooFew)
                {
                    try { File.Delete(imagePath); } catch { }
                    removed.Add(rec.FileName);
                    continue;
                }

                var lines = new List<string>();
                using (var probe = SKBitmap.Decode(imagePath))
                {
                    var imgW = probe?.Width ?? 0;
                    var imgH = probe?.Height ?? 0;

                    foreach (var o in objects)
                    {
                        var box = o.PixelBoundingBox;
                        var w = box.Width > 0 ? box.Width : (box.Right - box.Left);
                        var h = box.Height > 0 ? box.Height : (box.Bottom - box.Top);

                        if (imgW <= 0 || imgH <= 0 || w <= 0 || h <= 0) continue;

                        var cx = (box.Left + w / 2.0) / imgW;
                        var cy = (box.Top + h / 2.0) / imgH;
                        var nw = w / (double)imgW;
                        var nh = h / (double)imgH;

                        lines.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1:0.######} {2:0.######} {3:0.######} {4:0.######}",
                            o.ClassId, Clamp01(cx), Clamp01(cy), Clamp01(nw), Clamp01(nh)));
                    }
                }

                var txtPath = System.IO.Path.Combine(labelDir,
                    System.IO.Path.GetFileNameWithoutExtension(rec.FileName) + ".txt");

                try { File.WriteAllLines(txtPath, lines); } catch { }

                if (empty) negative++;
                else labeled++;

                progress?.Report(new AcquireProgress
                {
                    Phase = "预标注",
                    Message = $"已处理 {i + 1} / {records.Count}",
                    Percent = 100.0 * (i + 1) / records.Count
                });
            }

            return (labeled, negative, removed);
        }

        private static bool ConfidenceAtLeast(float v, float th) => v >= th - 1e-6f;

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
    }
}
