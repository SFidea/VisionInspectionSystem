// =============================================================================
//  violation_detector.cpp  —  ViolationDetector 的 C++ 移植
// =============================================================================
//  1:1 移植自 C# ViolationDetector.cs。跳步检测的三要点见头文件注释。
// =============================================================================
#include "sop/violation_detector.h"

#include <algorithm>
#include <cstdio>

#include "sop/state_machine.h"

namespace vis {
namespace sop {

namespace {

/// 冷却期秒数 —— 对齐 C# IsOnCooldown 的 `TotalSeconds < 3`
constexpr int64_t kCooldownMs = 3000;

string upperKey(ViolationType t, int stepId) {
    return std::string(toString(t)) + "_" + std::to_string(stepId);
}

bool equalsIgnoreCase(const std::string& a, const std::string& b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        char ca = a[i], cb = b[i];
        if (ca >= 'A' && ca <= 'Z') ca = static_cast<char>(ca - 'A' + 'a');
        if (cb >= 'A' && cb <= 'Z') cb = static_cast<char>(cb - 'A' + 'a');
        if (ca != cb) return false;
    }
    return true;
}

}  // namespace

// =============================================================================
ViolationDetector::ViolationDetector(SOPStateMachine* sm) : _stateMachine(sm) {}

void ViolationDetector::updateZones(const std::vector<ZoneDefinition>& zones) {
    _zones.clear();
    for (const auto& z : zones) _zones[z.id] = z;
}

const ZoneDefinition* ViolationDetector::findZone(const std::string& id) const {
    if (id.empty()) return nullptr;
    auto it = _zones.find(id);
    return it == _zones.end() ? nullptr : &it->second;
}

// =============================================================================
//  detect —— 主入口（对齐 C# DetectViolations）
//  ★ 调用时机：在 ProcessFrame 里【条件评估之前】
// =============================================================================
std::vector<ViolationRecord> ViolationDetector::detect(
    const SOPStep& step,
    const std::vector<Detection>& detections,
    const FrameDetections* handInput,
    int64_t timestampMs) {

    std::vector<ViolationRecord> out;

    const SOPWorkflow* wf = _stateMachine ? _stateMachine->workflow() : nullptr;
    if (!wf) return out;

    // ---- 记录出现过的物体（ObjectRemoved 用）----
    for (const auto& d : detections) {
        if (d.className.empty()) continue;
        if (std::find(_seenObjects.begin(), _seenObjects.end(), d.className) == _seenObjects.end())
            _seenObjects.push_back(d.className);
    }

    ViolationRecord v;

    // =====================================================================
    //  通道一：★ 规则驱动 —— 遍历 step.ViolationRules 按类型分派
    //         （对齐 C# DetectViolations:40-61）
    //         ★ 规则为空则不产生该类违规 —— 这是与基线一致的关键
    // =====================================================================
    for (const auto& rule : step.violationRules) {
        const ViolationRecord* found = nullptr;
        ViolationRecord tmp;

        switch (rule.type) {
            case ViolationType::Timeout:
                if (detectTimeout(step, timestampMs, &tmp)) found = &tmp;
                break;
            case ViolationType::ForbiddenObject:
                if (detectForbiddenObject(step, detections, timestampMs, &tmp)) found = &tmp;
                break;
            case ViolationType::ObjectRemoved:
                if (detectObjectRemoved(step, detections, timestampMs, &tmp)) found = &tmp;
                break;
            case ViolationType::WrongOrder:
                if (detectWrongOrder(step, timestampMs, &tmp)) found = &tmp;
                break;
            case ViolationType::MissingRequiredObject:
                if (detectMissingRequiredObject(step, detections, timestampMs, &tmp)) found = &tmp;
                break;
            default:
                break;
        }

        if (found && !isOnCooldown(*found)) {
            out.push_back(*found);
            setCooldown(*found);
        }
    }

    // =====================================================================
    //  通道二：全局超时（对齐 C# :63-72）
    // =====================================================================
    if (wf->settings.enableTimeoutDetection && detectTimeout(step, timestampMs, &v)) {
        if (!isOnCooldown(v)) { out.push_back(v); setCooldown(v); }
    }

    // =====================================================================
    //  通道三：★ 全局跳步（对齐 C# :74-89）
    //         仅在 EnableSkipDetection 开启、且当前不是第一步时启用
    // =====================================================================
    if (wf->settings.enableSkipDetection && !wf->steps.empty()) {
        int minStepId = wf->steps[0].stepId;
        for (const auto& s : wf->steps)
            if (s.stepId < minStepId) minStepId = s.stepId;

        if (step.stepId > minStepId) {
            if (detectSkipStep(step, detections, handInput, timestampMs, &v)) {
                if (!isOnCooldown(v)) { out.push_back(v); setCooldown(v); }
            }
        }
    }

    return out;
}

// =============================================================================
//  ★ detectSkipStep —— 对齐 C# :338-374
// =============================================================================
bool ViolationDetector::detectSkipStep(const SOPStep& step,
                                       const std::vector<Detection>& detections,
                                       const FrameDetections* handInput,
                                       int64_t timestampMs,
                                       ViolationRecord* out) {
    const SOPWorkflow* wf = _stateMachine ? _stateMachine->workflow() : nullptr;
    if (!wf || wf->settings.enableSkipDetection != true) return false;

    // ① 只检查「下一个」步骤：StepId 更大的最小者
    const SOPStep* nextStep = nullptr;
    for (const auto& s : wf->steps) {
        if (s.stepId <= step.stepId) continue;
        if (!nextStep || s.stepId < nextStep->stepId) nextStep = &s;
    }
    if (!nextStep) return false;

    // ② ★ 排除纯时间条件步骤（如 complete 的 time_elapsed），
    //    否则只要等 1 秒就误报跳步
    if (nextStep->passConditions.size() == 1 &&
        nextStep->passConditions[0].type == ConditionType::TimeElapsed)
        return false;

    // ③ ★ ignoreFromRegion=true —— 跳步时物体没经过起始区域，
    //    若要求 visitedFrom 会漏报。只需核心动作意图即判定"正在做下一步"。
    auto eval = _stateMachine->ProbeNextStep(nextStep->stepId, detections, handInput);
    if (!eval.isPass) return false;

    out->timestampMs = timestampMs;
    out->type = ViolationType::SkipStep;
    out->stepId = step.stepId;
    out->description = std::string("可能跳过了步骤 '") + step.stepName +
                       "'，直接满足下一步 '" + nextStep->stepName + "'";
    // Evidence 格式对齐 C#：Skip_{cur}_{next}_{HHmmss}
    out->evidence = std::string("Skip_") + std::to_string(step.stepId) + "_" +
                    std::to_string(nextStep->stepId) + "_" +
                    std::to_string(timestampMs / 1000);
    return true;
}

// =============================================================================
//  detectWrongOrder —— 对齐 C# :379-405
// =============================================================================
bool ViolationDetector::detectWrongOrder(const SOPStep& step, int64_t ts,
                                         ViolationRecord* out) {
    if (step.requiredPreviousSteps.empty()) return false;
    if (!_stateMachine) return false;

    // 已完成的步骤集合
    std::vector<int> completed;
    for (const auto& h : _stateMachine->stepHistory()) completed.push_back(h.stepId);

    std::vector<int> missing;
    for (int req : step.requiredPreviousSteps) {
        if (std::find(completed.begin(), completed.end(), req) == completed.end())
            missing.push_back(req);
    }
    if (missing.empty()) return false;

    std::string list;
    for (int i = 0; i < static_cast<int>(missing.size()); ++i) {
        if (i > 0) list += ",";
        list += std::to_string(missing[i]);
    }

    out->timestampMs = ts;
    out->type = ViolationType::WrongOrder;
    out->stepId = step.stepId;
    out->description = "步骤顺序错误，缺少前置步骤: " + list;
    out->evidence = "WrongOrder_" + std::to_string(step.stepId) + "_" + list;
    return true;
}

// =============================================================================
//  detectForbiddenObject
// =============================================================================
bool ViolationDetector::detectForbiddenObject(const SOPStep& step,
                                              const std::vector<Detection>& detections,
                                              int64_t ts, ViolationRecord* out) {
    for (const auto& rule : step.violationRules) {
        if (rule.type != ViolationType::ForbiddenObject) continue;
        for (const auto& d : detections) {
            if (d.className.empty()) continue;
            // C# 版语义：规则描述里存禁止物体名
            if (!equalsIgnoreCase(d.className, rule.description)) continue;

            out->timestampMs = ts;
            out->type = ViolationType::ForbiddenObject;
            out->stepId = step.stepId;
            out->description = "检测到违规物体 '" + d.className + "'";
            out->evidence = "Forbidden_" + std::to_string(step.stepId) + "_" + d.className;
            return true;
        }
    }
    return false;
}

// =============================================================================
//  detectObjectRemoved —— 对齐 C# DetectObjectRemoved
//  语义：「曾经存在过，现在消失」的物体（不是"一开始就缺"）
// =============================================================================
bool ViolationDetector::detectObjectRemoved(const SOPStep& step,
                                           const std::vector<Detection>& detections,
                                           int64_t ts, ViolationRecord* out) {
    // 只在步骤声明了 must_keep（必须保持存在）时才判定
    if (step.mustKeep.empty()) return false;

    for (const auto& label : step.mustKeep) {
        bool stillThere = false;
        for (const auto& d : detections) {
            if (equalsIgnoreCase(d.className, label)) { stillThere = true; break; }
        }
        if (stillThere) continue;

        out->timestampMs = ts;
        out->type = ViolationType::ObjectRemoved;
        out->stepId = step.stepId;
        out->description = "物体 '" + label + "' 已从场景中消失";
        out->evidence = "Removed_" + std::to_string(step.stepId) + "_" + label;
        return true;
    }
    return false;
}

// =============================================================================
//  detectMissingRequiredObject —— 对齐 C# :212
//  语义：步骤声明的必放物料缺失（数据驱动，每个配方在 required_objects 声明）
// =============================================================================
bool ViolationDetector::detectMissingRequiredObject(const SOPStep& step,
                                                   const std::vector<Detection>& detections,
                                                   int64_t ts, ViolationRecord* out) {
    // ★ 只在步骤有 MissingRequiredObject 规则时才判定 —— 对齐 C# `if (reqRules.Count == 0) return null`
    bool hasRule = false;
    for (const auto& r : step.violationRules)
        if (r.type == ViolationType::MissingRequiredObject) { hasRule = true; break; }
    if (!hasRule && step.requiredObjects.empty()) return false;

    // 汇集必放物料（类别 + 最小置信度）
    std::vector<std::string> required = step.requiredObjects;
    if (required.empty()) return false;

    for (const auto& label : required) {
        bool found = false;
        for (const auto& d : detections) {
            if (equalsIgnoreCase(d.className, label)) { found = true; break; }
        }
        if (!found) {
            out->timestampMs = ts;
            out->type = ViolationType::MissingRequiredObject;
            out->stepId = step.stepId;
            out->description = "缺少必放物料 '" + label + "'";
            out->evidence = "Missing_" + std::to_string(step.stepId) + "_" + label;
            return true;
        }
    }
    return false;
}

// =============================================================================
//  detectTimeout —— 对齐 C# DetectTimeout
// =============================================================================
bool ViolationDetector::detectTimeout(const SOPStep& step, int64_t ts,
                                      ViolationRecord* out) {
    if (!_stateMachine) return false;
    if (step.timeoutSec <= 0) return false;

    int64_t elapsed = ts - _stateMachine->stepStartMs();
    if (elapsed <= static_cast<int64_t>(step.timeoutSec) * 1000) return false;

    out->timestampMs = ts;
    out->type = ViolationType::Timeout;
    out->stepId = step.stepId;
    out->description = std::string("步骤 '") + step.stepName + "' 超时";
    out->evidence = "Timeout_" + std::to_string(step.stepId);
    return true;
}

// =============================================================================
//  冷却期 —— 对齐 C# IsOnCooldown / SetCooldown（3 秒）
// =============================================================================
bool ViolationDetector::isOnCooldown(const ViolationRecord& v) const {
    auto it = _cooldowns.find(upperKey(v.type, v.stepId));
    if (it == _cooldowns.end()) return false;
    if (v.timestampMs < it->second) return false;
    return (v.timestampMs - it->second) < kCooldownMs;
}

void ViolationDetector::setCooldown(const ViolationRecord& v) {
    _cooldowns[upperKey(v.type, v.stepId)] = v.timestampMs;
}

}  // namespace sop
}  // namespace vis