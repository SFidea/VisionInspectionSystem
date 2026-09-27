using System;
using System.Collections.Generic;

namespace VisionInspection.UI.Models
{
    /// <summary>
    /// 性能验收场景档位：不同的使用场景对推理帧率的要求不同。
    /// 数值 = 该场景要求的最低 FPS。
    /// </summary>
    public enum BenchmarkScenario
    {
        /// <summary>实时检测：相机实时画面逐帧推理，要求 ≥ 25 FPS</summary>
        Realtime = 25,

        /// <summary>SOP 作业监控：工序步骤判定允许略低帧率，要求 ≥ 15 FPS</summary>
        Sop = 15,

        /// <summary>离线分析：录像/图片批量处理，要求 ≥ 5 FPS</summary>
        Offline = 5
    }

    public static class BenchmarkScenarioExtensions
    {
        public static string GetLabel(this BenchmarkScenario s) => s switch
        {
            BenchmarkScenario.Realtime => "实时检测（≥ 25 FPS）",
            BenchmarkScenario.Sop => "SOP 作业监控（≥ 15 FPS）",
            BenchmarkScenario.Offline => "离线分析（≥ 5 FPS）",
            _ => s.ToString()
        };
    }

    /// <summary>
    /// 基准测试参数。
    /// </summary>
    public class BenchmarkOptions
    {
        /// <summary>是否测试 MediaPipe 手部两阶段模型（palm 192 → landmark 224）</summary>
        public bool TestMediaPipe { get; set; } = true;

        /// <summary>待测试的 YOLO 检测模型路径（可多个）</summary>
        public List<string> YoloModelPaths { get; set; } = new();

        /// <summary>是否跑 CPU</summary>
        public bool RunCpu { get; set; } = true;

        /// <summary>是否跑 GPU（CUDA）</summary>
        public bool RunGpu { get; set; } = true;

        /// <summary>预热帧数（不计入统计，用于消除首次 CUDA 初始化/内存分配的影响）</summary>
        public int WarmupFrames { get; set; } = 5;

        /// <summary>正式计时帧数</summary>
        public int TestFrames { get; set; } = 50;

        /// <summary>合成测试帧宽度（真实帧模式无效）</summary>
        public int Width { get; set; } = 640;

        /// <summary>合成测试帧高度（真实帧模式无效）</summary>
        public int Height { get; set; } = 480;

        /// <summary>true = 使用真实帧（相机/已加载图像）；false = 使用合成测试帧</summary>
        public bool UseRealFrame { get; set; }

        /// <summary>验收场景档位（决定达标阈值）</summary>
        public BenchmarkScenario Scenario { get; set; } = BenchmarkScenario.Realtime;

        /// <summary>CUDA 设备号</summary>
        public int GpuId { get; set; }
    }

    /// <summary>
    /// 单项测试结果（一个「模型 × 设备」组合）。
    /// </summary>
    public class BenchmarkResultItem
    {
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";

        /// <summary>请求使用的设备（CPU / GPU）</summary>
        public string RequestedDevice { get; set; } = "";

        /// <summary>实际使用的设备（CPU / GPU）。GPU 不可用时如实报告 CPU，避免误判部署结论。</summary>
        public string ActualDevice { get; set; } = "";

        /// <summary>是否发生了「请求 GPU 但回退 CPU」</summary>
        public bool FellBackToCpu { get; set; }

        public double AvgFps { get; set; }
        public double AvgMs { get; set; }
        public double P50Ms { get; set; }
        public double P95Ms { get; set; }
        public double MinMs { get; set; }
        public double MaxMs { get; set; }

        /// <summary>MediaPipe 第一阶段（palm 检测）平均耗时 ms；非 MediaPipe 项为 0</summary>
        public double PalmMs { get; set; }

        /// <summary>MediaPipe 第二阶段（landmark）平均耗时 ms；非 MediaPipe 项为 0</summary>
        public double LandmarkMs { get; set; }

        public int TargetFps { get; set; }

        public bool Passed => Error == null && AvgFps >= TargetFps;

        /// <summary>非 null 表示该项测试失败（模型缺失 / CUDA 不可用等）</summary>
        public string? Error { get; set; }

        public string StatusText =>
            Error != null ? "失败" : (Passed ? "达标" : "不达标");
    }

    /// <summary>
    /// 运行环境信息，用于判断部署机配置。
    /// </summary>
    public class BenchmarkEnvironment
    {
        public string OS { get; set; } = "";
        public string Cpu { get; set; } = "";
        public int CpuCores { get; set; }
        public long TotalMemoryMB { get; set; }
        public string OnnxRuntimeVersion { get; set; } = "";
        public bool CudaAvailable { get; set; }
        public string GpuName { get; set; } = "";
    }

    public class BenchmarkReport
    {
        public DateTime Timestamp { get; set; }
        public BenchmarkEnvironment Environment { get; set; } = new();
        public List<BenchmarkResultItem> Items { get; set; } = new();
        public BenchmarkOptions Options { get; set; } = new();

        /// <summary>CPU vs GPU 加速比（同模型），key = 模型名</summary>
        public Dictionary<string, double> Speedups { get; set; } = new();
    }

    /// <summary>
    /// 测试进度回调。
    /// </summary>
    public class BenchmarkProgress
    {
        public string Message { get; set; } = "";

        /// <summary>0-100；小于 0 表示不确定进度</summary>
        public int Percent { get; set; }

        public BenchmarkResultItem? CompletedItem { get; set; }
    }
}
