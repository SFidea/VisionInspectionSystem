namespace VisionInspection.UI.Models
{
    /// <summary>
    /// 数据采集（录像 + 抽帧）完整配置
    /// </summary>
    public class DataAcquisitionConfig
    {
        public RecordConfig Record { get; set; } = new();
        public ExtractConfig Extract { get; set; } = new();
        public NamingConfig Naming { get; set; } = new();
        public AutoLabelConfig AutoLabel { get; set; } = new();
    }

    /// <summary>
    /// 录像参数
    /// </summary>
    public class RecordConfig
    {
        public string OutputRoot { get; set; } = "dataset";
        public string VideoCodec { get; set; } = "libx264";
        public int Crf { get; set; } = 18;
        public string Preset { get; set; } = "veryfast";
        /// <summary>
        /// 喂给 ffmpeg 的原始像素格式。
        /// 全工程约定：CameraImageData.Data 为 **RGB24**
        /// （WebCameraService / OnvifCameraService 均显式 CvtColor(BGR2RGB)，
        ///  HikvisionCameraService 目标格式为 PixelType_Gvsp_RGB8_Packed，
        ///  预览 CameraFrameConverter 也按 Rgb24 解释）。
        /// 因此这里默认必须是 rgb24；若误用 bgr24 会导致录制视频红蓝通道互换。
        /// 可选值：rgb24 / bgr24 / gray / auto（auto = 彩色取 rgb24、单通道取 gray）。
        /// </summary>
        public string SourcePixelFormat { get; set; } = "rgb24";

        /// <summary>录制帧率上限，0 = 跟随相机</summary>
        public double RecordFps { get; set; } = 0;
        public int QueueCapacity { get; set; } = 60;
        /// <summary>最长录制秒数，0 = 不限</summary>
        public double MaxDurationSec { get; set; } = 0;
        /// <summary>最大文件体积 MB，0 = 不限</summary>
        public long MaxFileSizeMB { get; set; } = 0;
        public bool KeepRawVideo { get; set; } = true;
    }

    public enum ExtractStrategy
    {
        TargetCount,
        IntervalFrames,
        IntervalMs,
        TargetFps,
        SceneChange
    }

    public enum DedupMethod
    {
        PHash,
        DHash,
        Histogram
    }

    public enum DedupScope
    {
        PreviousKept,
        AllKept
    }

    public enum AutoResizeMode
    {
        None,
        LimitLongEdge,
        FixedSize,
        Percent
    }

    /// <summary>
    /// 抽帧参数
    /// </summary>
    public class ExtractConfig
    {
        // ===== 采样策略 =====
        public ExtractStrategy Strategy { get; set; } = ExtractStrategy.TargetCount;
        public int IntervalFrames { get; set; } = 10;
        public double IntervalMs { get; set; } = 500;
        public double TargetFps { get; set; } = 2;
        public int TargetCount { get; set; } = 200;
        /// <summary>
        /// 场景切换阈值（ffmpeg scene 评分，衡量相邻帧差异）。
        /// 实测（务必先看数据再调）：
        ///   ① 静态机位航拍 1280x720@30fps / 66s：0.02→33 张，0.05→2 张，0.1→0 张
        ///   ② 手持拍摄 4K@60fps / 前30s：最大评分仅 0.054，0.005→209 帧，0.01→57 帧，0.02→5 帧
        /// 结论：该阈值极小敏感，且高帧率视频相邻帧差异天然更小 → 默认取 0.01。
        /// 想要"只抓剧烈变化"再往上加，想要更多帧则往下调。
        /// </summary>
        public double SceneThreshold { get; set; } = 0.01;
        public double StartTimeSec { get; set; } = 0;
        public double EndTimeSec { get; set; } = 0;

        // ===== 质量过滤 =====
        public bool EnableQualityFilter { get; set; } = true;
        /// <summary>
        /// 清晰度下限（拉普拉斯方差）。
        /// ⚠️ 该指标与「内容频率 + 像素密度」强相关，跨视频不可比，实测参考：
        ///   · 静态航拍 clear 帧（输出 640 宽）：≈ 3000+
        ///   · 手持 4K 缩到 1280 宽：典型 30~100，真正模糊 &lt; 10
        ///   · 4K 原图反而比缩图更低（相邻像素相关性高）
        /// 故默认取 20 —— 只剔真正的糊帧，避免把正常画面误杀；需要更严格就往上调。
        /// </summary>
        public double MinSharpness { get; set; } = 20;
        public double MinBrightness { get; set; } = 30;
        public double MaxBrightness { get; set; } = 225;
        public double MinContrast { get; set; } = 15;

        // ===== 去重 =====
        public bool EnableDedup { get; set; } = true;
        public DedupMethod Method { get; set; } = DedupMethod.PHash;
        public int HammingThreshold { get; set; } = 8;
        public double HistogramThreshold { get; set; } = 0.985;
        public DedupScope Scope { get; set; } = DedupScope.PreviousKept;
        public bool CrossSessionDedup { get; set; } = false;

        // ===== 输出 =====
        public AutoResizeMode Resize { get; set; } = AutoResizeMode.LimitLongEdge;
        public int MaxLongEdge { get; set; } = 1920;
        public int FixedWidth { get; set; } = 1280;
        public int FixedHeight { get; set; } = 720;
        public int PercentScale { get; set; } = 50;
        public bool Grayscale { get; set; } = false;
        public string OutputFormat { get; set; } = "jpg";
        public int JpegQuality { get; set; } = 95;

        // ===== 守护 =====
        public int MaxOutputCount { get; set; } = 2000;
        /// <summary>并行打分线程数，0 = 自动</summary>
        public int WorkerThreads { get; set; } = 0;
        public bool CleanupTemp { get; set; } = true;
    }

    /// <summary>
    /// 命名与元数据
    /// </summary>
    public class NamingConfig
    {
        public string FilePattern { get; set; } = "{session}_{index:D6}";
        public int StartIndex { get; set; } = 0;
        public bool SubFolderPerSession { get; set; } = true;
        public bool ExportMeta { get; set; } = true;
    }

    /// <summary>
    /// 自动预标注
    /// </summary>
    public class AutoLabelConfig
    {
        public bool EnableAutoLabel { get; set; } = true;
        /// <summary>留空则跟随主界面已加载的模型</summary>
        public string ModelPath { get; set; } = "";
        public float ConfThreshold { get; set; } = 0.25f;
        public float IouThreshold { get; set; } = 0.45f;
        public bool ExportClassesFile { get; set; } = true;
        public bool KeepNegativeFrames { get; set; } = true;
        public int MinInstances { get; set; } = 0;
        public int MaxInstances { get; set; } = 0;
    }

    /// <summary>
    /// 视频探测信息
    /// </summary>
    public class VideoMeta
    {
        public double DurationSec { get; set; }
        public double Fps { get; set; }
        public long FrameCount { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    /// <summary>
    /// 抽帧结果摘要
    /// </summary>
    public class ExtractSummary
    {
        public string SessionDir { get; set; } = "";
        public string ImageDir { get; set; } = "";
        public int CandidateCount { get; set; }
        public int KeptCount { get; set; }
        public int RejectedBlur { get; set; }
        public int RejectedBrightness { get; set; }
        public int RejectedContrast { get; set; }
        public int RejectedDuplicate { get; set; }
        public int LabeledCount { get; set; }
        public int NegativeCount { get; set; }
        public double ElapsedSec { get; set; }
        public string Message { get; set; } = "";
        public System.Collections.Generic.List<FrameRecord> Records { get; set; } = new();
    }

    /// <summary>
    /// 单帧记录（用于 frames.csv / meta.json / 结果列表）
    /// </summary>
    public class FrameRecord
    {
        public string FileName { get; set; } = "";
        public double SourceTimeSec { get; set; }
        public double Sharpness { get; set; }
        public double Brightness { get; set; }
        public double Contrast { get; set; }
        /// <summary>-1 表示未做预标注</summary>
        public int Instances { get; set; } = -1;
    }

    /// <summary>
    /// 抽帧/标注入口进度
    /// </summary>
    public class AcquireProgress
    {
        public string Phase { get; set; } = "";
        public string Message { get; set; } = "";
        public double Percent { get; set; }
    }
}
