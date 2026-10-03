namespace VisionInspection.Modules.SOP.Models;

/// <summary>
/// 状态机只读快照 —— 黄金数据集 decisions.jsonl 的数据源。
///
/// 设计：由 SOPModule 在引擎 ProcessFrame 之后，从 SOPStateMachine 的公开只读属性
/// （CurrentStepId / CurrentState / LastEvaluation / Violations）组装而来。
/// 刻意【不持有】状态机内部对象的引用，录制器与引擎保持单向解耦。
/// </summary>
public class SOPStateMachineSnapshot
{
    /// <summary>帧内时间戳（相对录制起始的毫秒）</summary>
    public DateTime Timestamp { get; set; }

    public int StepId { get; set; }
    public string StepName { get; set; } = "";
    public string State { get; set; } = "Idle";

    /// <summary>本帧步骤条件是否全部满足（= 步骤将推进）</summary>
    public bool IsPass { get; set; }

    /// <summary>通过后将进入的步骤 Id，-1 表示流程终止</summary>
    public int NextStepId { get; set; } = -1;

    /// <summary>
    /// ★ 12 类条件逐帧布尔结果 —— G4 对拍的核心字段。
    /// Key 用 ConditionType 枚举名（如 "HandInRegion" / "ObjectInZone"），
    /// RK3588 侧按同名 key 逐帧比对。
    /// </summary>
    public Dictionary<string, bool> Conditions { get; set; } = new();

    /// <summary>本帧触发的违规（8 类 ViolationType 名 + 描述）</summary>
    public List<string> Violations { get; set; } = new();
}
