using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionInspection.Modules.SOP.Services;

/// <summary>
/// 黄金数据集录制配置。持久化到 configs/golden_recorder.json
/// </summary>
public class GoldenRecorderConfig
{
    /// <summary>总开关。默认关闭，录制不影响常规生产。</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>输出根目录。空 = 相对基目录 golden/</summary>
    public string OutputRoot { get; set; } = "golden";

    /// <summary>当前案例名（一次录制对应一个 case，如 cup_usage_normal）</summary>
    public string CaseName { get; set; } = "";

    /// <summary>案例说明，写入 meta.json 便于回溯</summary>
    public string CaseNote { get; set; } = "";

    /// <summary>是否保存原图帧（关则只存 jsonl，体积小但无法回放重推理）</summary>
    public bool SaveFrames { get; set; } = true;

    /// <summary>保存原图的 JPEG 质量（70~95，越高越清晰、体积越大）</summary>
    public int JpegQuality { get; set; } = 90;

    /// <summary>帧间隔抽稀：1=每帧都存，2=隔帧存。用于控制数据集体积。</summary>
    public int FrameStride { get; set; } = 1;

    /// <summary>单案例最大帧数上限（保护磁盘），0=不限</summary>
    public int MaxFrames { get; set; } = 2000;

    /// <summary>队列容量上限。超出则丢最旧帧（只丢录制，不影响检测）。</summary>
    public int QueueCapacity { get; set; } = 64;
}
