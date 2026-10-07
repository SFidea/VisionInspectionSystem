// =============================================================================
//  violation_detector.h  —  ViolationDetector 的 C++ 移植
// =============================================================================
//  1:1 移植自 C# src/Modules/VisionInspection.Modules.SOP/Services/ViolationDetector.cs
//
//  ★★ 跳步检测（DetectSkipStep）有三个必须照抄的要点：
//   1. 只检查「下一个」步骤（StepId 更大的最小者），不是所有后续步骤
//   2. 排除「纯时间条件」的下一步（如 complete 的 time_elapsed），
//      否则只要等 1 秒就误报跳步
//   3. 评估下一步必须 ignoreFromRegion=true —— 用户跳步时物体没经过起始区域
//      （如没到嘴边），若强制要求 visitedFrom 会漏报
//  ★ 冷却期 3 秒：同一 (违规类型, 步骤Id) 3 秒内不重复上报。
//    这解释了基线里 SkipStep 为何是 7 帧、分散在 3 个时刻（27s / 105s / 111s）。
// =============================================================================
#pragma once

#include <map>
#include <string>
#include <vector>

#include "sop/types.h"

namespace vis {
namespace sop {

class SOPStateMachine;

/// 单个违规类型的冷却记录
struct ViolationCooldown {
    int64_t lastMs = -1;    // -1 = 未冷却过
};

/// 违规检测器 —— 8 类违规
class ViolationDetector {
public:
    explicit ViolationDetector(SOPStateMachine* sm = nullptr);

    void updateZones(const std::vector<ZoneDefinition>& zones);

    /// 检测当前步骤的全部违规（对齐 C# DetectViolations）
    std::vector<ViolationRecord> detect(const SOPStep& step,
                                        const std::vector<Detection>& detections,
                                        const FrameDetections* handInput,
                                        int64_t timestampMs);

    void resetCooldowns() { _cooldowns.clear(); }

private:
    /// ★ 跳步检测（对齐 C# :338-374）
    bool detectSkipStep(const SOPStep& step,
                        const std::vector<Detection>& detections,
                        const FrameDetections* handInput,
                        int64_t timestampMs,
                        ViolationRecord* out);

    /// 顺序错误（对齐 C# :379-405）
    bool detectWrongOrder(const SOPStep& step, int64_t timestampMs,
                          ViolationRecord* out);

    /// 违规物体（对齐 C# violation_rules 中的 forbidden_objects）
    bool detectForbiddenObject(const SOPStep& step,
                               const std::vector<Detection>& detections,
                               int64_t timestampMs, ViolationRecord* out);

    /// 物体移除（曾经存在过，现在没了）—— 依赖 step.mustKeep
    bool detectObjectRemoved(const SOPStep& step, const std::vector<Detection>& detections,
                             int64_t timestampMs, ViolationRecord* out);

    /// ★ 缺料（必放物料缺失）—— 依赖 step.requiredObjects + MissingRequiredObject 规则
    bool detectMissingRequiredObject(const SOPStep& step,
                                     const std::vector<Detection>& detections,
                                     int64_t timestampMs, ViolationRecord* out);

    /// 超时（对齐 C# DetectTimeout；受 settings.enable_timeout_detection 控制）
    bool detectTimeout(const SOPStep& step, int64_t timestampMs,
                       ViolationRecord* out);

    /// ★ 冷却期判定（对齐 C# IsOnCooldown，3 秒）
    bool isOnCooldown(const ViolationRecord& v) const;
    void setCooldown(const ViolationRecord& v);

    const ZoneDefinition* findZone(const std::string& id) const;

private:
    SOPStateMachine* _stateMachine = nullptr;
    std::map<std::string, ZoneDefinition> _zones;

    /// key = "{ViolationType}_{stepId}" → 上次上报时刻
    std::map<std::string, int64_t> _cooldowns;

    /// 历史上出现过的物体标签（用于 ObjectRemoved 判定）
    std::vector<std::string> _seenObjects;
};

}  // namespace sop
}  // namespace vis