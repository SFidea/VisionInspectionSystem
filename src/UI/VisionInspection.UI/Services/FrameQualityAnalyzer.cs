using System;
using OpenCvSharp;

namespace VisionInspection.UI.Services
{
    /// <summary>
    /// 单帧图像质量与指纹。用于抽帧后的「精筛」：清晰度 / 亮度 / 对比度 + 感知哈希去重。
    /// </summary>
    public struct FrameMetrics
    {
        /// <summary>清晰度（拉普拉斯方差）</summary>
        public double Sharpness;

        /// <summary>灰度均值（0-255）</summary>
        public double Brightness;

        /// <summary>灰度标准差（对比度）</summary>
        public double Contrast;

        /// <summary>感知哈希 pHash（64bit）</summary>
        public ulong PHash;

        /// <summary>差值哈希 dHash（64bit）</summary>
        public ulong DHash;

        /// <summary>归一化灰度直方图（256 bin）</summary>
        public double[] Histogram;
    }

    /// <summary>
    /// 帧质量分析器（OpenCvSharp 实现）
    /// </summary>
    public static class FrameQualityAnalyzer
    {
        /// <summary>
        /// 分析一张图片，返回清晰度 / 亮度 / 对比度 / 哈希指纹。
        /// </summary>
        public static FrameMetrics Analyze(string imagePath)
        {
            var m = new FrameMetrics();

            using var src = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (src.Empty())
            {
                m.Histogram = new double[256];
                return m;
            }

            using var gray = src.CvtColor(ColorConversionCodes.BGR2GRAY);

            // 亮度 / 对比度
            Cv2.MeanStdDev(gray, out var mean, out var std);
            m.Brightness = mean.Val0;
            m.Contrast = std.Val0;

            // 清晰度：拉普拉斯方差
            using var lap = new Mat();
            Cv2.Laplacian(gray, lap, MatType.CV_64F);
            Cv2.MeanStdDev(lap, out _, out var lapStd);
            m.Sharpness = lapStd.Val0 * lapStd.Val0;

            // 指纹
            m.PHash = ComputePHash(gray);
            m.DHash = ComputeDHash(gray);
            m.Histogram = ComputeHistogram(gray);

            return m;
        }

        private static ulong ComputePHash(Mat gray)
        {
            try
            {
                using var small = gray.Resize(new Size(32, 32), 0, 0, InterpolationFlags.Area);
                using var f32 = new Mat();
                small.ConvertTo(f32, MatType.CV_32F);
                using var dct = new Mat();
                Cv2.Dct(f32, dct);

                var vals = new double[64];
                for (var r = 0; r < 8; r++)
                {
                    for (var c = 0; c < 8; c++)
                    {
                        vals[r * 8 + c] = dct.At<float>(r, c);
                    }
                }

                // 取除直流分量外的中值作为阈值
                var tail = new double[63];
                Array.Copy(vals, 1, tail, 0, 63);
                Array.Sort(tail);
                var median = tail[31];

                ulong hash = 0;
                for (var i = 0; i < 64; i++)
                {
                    if (vals[i] > median) hash |= 1UL << i;
                }

                return hash;
            }
            catch
            {
                return 0;
            }
        }

        private static ulong ComputeDHash(Mat gray)
        {
            try
            {
                using var small = gray.Resize(new Size(9, 8), 0, 0, InterpolationFlags.Area);
                ulong hash = 0;
                var bit = 0;
                for (var r = 0; r < 8; r++)
                {
                    for (var c = 0; c < 8; c++)
                    {
                        if (small.At<byte>(r, c) > small.At<byte>(r, c + 1)) hash |= 1UL << bit;
                        bit++;
                    }
                }

                return hash;
            }
            catch
            {
                return 0;
            }
        }

        private static double[] ComputeHistogram(Mat gray)
        {
            var hist = new double[256];
            try
            {
                using var h = new Mat();
                int[] channels = { 0 };
                int[] histSize = { 256 };
                Rangef[] ranges = { new Rangef(0, 256) };
                Cv2.CalcHist(new[] { gray }, channels, null, h, 1, histSize, ranges);
                Cv2.Normalize(h, h, 1, 0, NormTypes.L1);

                for (var i = 0; i < 256; i++)
                {
                    hist[i] = h.At<float>(i, 0);
                }
            }
            catch
            {
                // 保持全 0
            }

            return hist;
        }

        /// <summary>汉明距离（0-64，越小越像）</summary>
        public static int HammingDistance(ulong a, ulong b)
        {
            ulong x = a ^ b;
            var c = 0;
            while (x != 0)
            {
                c += (int)(x & 1UL);
                x >>= 1;
            }

            return c;
        }

        /// <summary>直方图相似度（0-1，越接近 1 越像）</summary>
        public static double HistogramSimilarity(double[] a, double[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return 0;

            double ma = 0, mb = 0;
            for (var i = 0; i < a.Length; i++)
            {
                ma += a[i];
                mb += b[i];
            }

            ma /= a.Length;
            mb /= b.Length;

            double num = 0, da = 0, db = 0;
            for (var i = 0; i < a.Length; i++)
            {
                var x = a[i] - ma;
                var y = b[i] - mb;
                num += x * y;
                da += x * x;
                db += y * y;
            }

            if (da <= 0 || db <= 0) return 0;
            return num / Math.Sqrt(da * db);
        }
    }
}
