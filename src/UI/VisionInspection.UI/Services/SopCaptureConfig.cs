using System;

namespace VisionInspection.UI.Services
{
    /// <summary>
    /// SOP 检测画面缩略图与违规自动存图配置。
    /// 持久化到 configs/sop_capture.json。
    /// </summary>
    public class SopCaptureConfig
    {
        /// <summary>SOP 检测运行时是否在面板内显示实时缩略图（默认开）。</summary>
        public bool EnableThumbnail { get; set; } = true;

        /// <summary>每周期结果为 NG（违规）时是否自动保存当前帧截图（默认开）。</summary>
        public bool EnableViolationCapture { get; set; } = true;

        /// <summary>违规截图保存目录（相对程序基目录），默认 sop_captures。</summary>
        public string CaptureDirectory { get; set; } = "sop_captures";
    }
}
