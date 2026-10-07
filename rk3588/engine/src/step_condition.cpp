// =============================================================================
//  step_condition.cpp  —  StepConditionEvaluator 的 C++ 移植
// =============================================================================
//  ★★★ 本文件是 1:1 机械移植，每个函数都标注了 C# 对应位置与行号。
//     任何"顺手优化"都会破坏逐帧对拍。请先读 rk3588/docs/手部关键点映射与区域换算.md。
//
//  照抄时发现的 C# 既有行为（【必须保留，不可"修正"】）：
//   1. EvaluateConditions 里 `conditionIndex++` 只在条件【通过】时执行 ——
//      即失败时后续条件的索引不会前进。这是既有语义，改了会导致门控 key 错位。
//   2. ApplyStableFrameGate 仅对 isCurrentStep 生效（跳步探测复用同一评估器实例，
//      不区分会每帧互踩清零 → 步骤卡死 + 跳步不报警，2026-09-29 已修）。
//   3. 门控跳过清单：HandStable/ObjectStable（自管理帧数）、
//      HandMoveFromTo（瞬时交互，真窗口仅数帧）、TimeElapsed/SequenceComplete。
//   4. SelectHand 先按 HandSide 找右手，取不到才回退任意手（IsValidGesture(8)）。
//   5. HandMoveFromTo 的主判据是【物体到达 to_region】，手仅作辅助。
// =============================================================================
#include "sop/step_condition.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <map>
#include <set>
#include <sstream>

#include "sop/state_machine.h"   // 需要完整类型才能访问 currentStepId / trackedObjects

namespace vis {
namespace sop {

namespace {

// C# 用 StringComparison.OrdinalIgnoreCase —— 统一转小写比较
bool equalsIgnoreCase(const string& a, const string& b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        char ca = a[i], cb = b[i];
        if (ca >= 'A' && ca <= 'Z') ca = static_cast<char>(ca - 'A' + 'a');
        if (cb >= 'A' && cb <= 'Z') cb = static_cast<char>(cb - 'A' + 'a');
        if (ca != cb) return false;
    }
    return true;
}

// 矩形相交 —— 对齐 C# IsInZone（StepConditionEvaluator.cs:848）
// ★ 物体侧用【矩形相交】，手部侧用【中心点包含】，语义不同不可混用。
bool rectIntersects(const Rect& box, const ZoneDefinition& z) {
    return !(box.x >= z.right() || box.x + box.w <= z.left() ||
             box.y >= z.bottom() || box.y + box.h <= z.top());
}

// 点包含 —— 对齐 C# SKRect.Contains(point)
bool rectContainsPoint(const ZoneDefinition& z, const Point& p) {
    return p.x >= z.left() && p.x <= z.right() &&
           p.y >= z.top()  && p.y <= z.bottom();
}

string fmt(float v) {
    char buf[64];
    std::snprintf(buf, sizeof(buf), "%.1f", v);
    return string(buf);
}

}  // namespace

// =============================================================================
//  构造函数
// =============================================================================
StepConditionEvaluator::StepConditionEvaluator(SOPStateMachine* sm,
                                             const std::vector<ZoneDefinition>* zones)
    : _stateMachine(sm) {
    if (zones) {
        for (const auto& z : *zones) _zones[z.id] = z;
    }
}

void StepConditionEvaluator::updateZones(const std::vector<ZoneDefinition>& zones) {
    _zones.clear();
    for (const auto& z : zones) _zones[z.id] = z;
}

void StepConditionEvaluator::ResetStableCounts() {
    _stablePassCounts.clear();
    _lastEvaluatedStepId = -1;
}

// =============================================================================
//  EvaluateConditions —— 对齐 StepConditionEvaluator.cs:51
// =============================================================================
ConditionEvaluationResult StepConditionEvaluator::EvaluateConditions(
    const SOPStep& step,
    const std::vector<Detection>& detections,
    const FrameDetections* handInput,
    bool ignoreFromRegion,
    int64_t currentStepElapsedMs) {

    _currentStepElapsedMs = currentStepElapsedMs;

    // ① 先更新手部跨帧跟踪（稳定/移动判断用）
    UpdateHandTracks(handInput);
    // ② 同步物体区域访问记录（放回步骤的 from_region 约束用）
    UpdateObjectRegions(detections);

    // ③ ★ 跳步检测会用同一实例评估"下一步"，不区分会互踩清零
    bool isCurrentStep = (_stateMachine == nullptr) ||
                         (step.stepId == _stateMachine->currentStepId());

    if (isCurrentStep && step.stepId != _lastEvaluatedStepId) {
        _stablePassCounts.clear();
        _lastEvaluatedStepId = step.stepId;
    }

    ConditionEvaluationResult result;
    if (step.passConditions.empty()) {
        result.isPass = true;
        return result;
    }

    int conditionIndex = 0;
    for (const auto& cond : step.passConditions) {
        ConditionCheckResult r = CheckCondition(cond, detections, handInput, ignoreFromRegion);

        // ★ 仅当前步骤才施加稳定帧门控
        if (isCurrentStep) ApplyStableFrameGate(step.stepId, conditionIndex, cond, r);

        result.checkResults.push_back(r);

        // ★ AND 短路：任一不满足即返回
        if (!r.isMet) {
            result.isPass = false;
            result.failedIndex = conditionIndex;
            result.message = r.message;
            return result;
        }
        conditionIndex++;   // ← 注意：只在通过时前进，照抄 C# 行为
    }

    result.isPass = true;
    result.message = std::string("步骤 '") + step.stepName + "' 所有条件满足";
    return result;
}

// =============================================================================
//  ApplyStableFrameGate —— 对齐 StepConditionEvaluator.cs:160
// =============================================================================
void StepConditionEvaluator::ApplyStableFrameGate(int stepId, int conditionIndex,
                                                 const StepCondition& cond,
                                                 ConditionCheckResult& result) {
    // 跳过清单（照抄 C#）——顺序与含义都不能改
    switch (cond.type) {
        case ConditionType::HandStable:            // 内部已含帧计数
        case ConditionType::ObjectStable:          // 内部已含帧计数
        case ConditionType::HandMoveFromTo:        // 瞬时交互，真窗口仅数帧
        case ConditionType::TimeElapsed:           // 自带时序语义
        case ConditionType::SequenceComplete:      // 自带完成语义
            return;
        default:
            break;
    }

    int required = cond.stableFrames;
    if (required <= 1) return;      // 无防抖要求 → 保持单帧判定语义

    string key = std::to_string(stepId) + std::string(":") + std::to_string(conditionIndex);
    if (result.isMet) {
        int& count = _stablePassCounts[key];
        count++;
        if (count < required) {
            result.isMet = false;
            result.message += std::string("（稳定帧门控 ") + std::to_string(count) + std::string("/") +
                             std::to_string(required) + "）";
        }
    } else {
        // 条件本身未满足 → 连续计数清零
        _stablePassCounts[key] = 0;
    }
}

// =============================================================================
//  CheckCondition —— 对齐 StepConditionEvaluator.cs:129（分发）
// =============================================================================
ConditionCheckResult StepConditionEvaluator::CheckCondition(
    const StepCondition& cond,
    const std::vector<Detection>& detections,
    const FrameDetections* handInput,
    bool ignoreFromRegion) {

    switch (cond.type) {
        // 手部动作条件
        case ConditionType::HandInRegion:
        case ConditionType::HandNotInRegion:
        case ConditionType::HandStable:
            return CheckHandCondition(cond, detections, handInput, ignoreFromRegion);
        case ConditionType::HandMoveFromTo:
            return CheckHandCondition(cond, detections, handInput, ignoreFromRegion);

        case ConditionType::HandNearObject:
            return CheckHandNearObject(cond, detections, handInput);

        // 物体/时间条件
        case ConditionType::ObjectPresent:   return CheckObjectPresent(cond, detections);
        case ConditionType::ObjectInZone:     return CheckObjectInZone(cond, detections);
        case ConditionType::ObjectStable:     return CheckObjectStable(cond);
        case ConditionType::ObjectAbsent:     return CheckObjectAbsent(cond, detections);
        case ConditionType::SequenceComplete: return CheckSequenceComplete(cond);
        case ConditionType::TimeElapsed:      return CheckTimeElapsed(cond, _currentStepElapsedMs);
        default:
            return ConditionCheckResult{false,
                string("未知条件类型: ") + toString(cond.type)};
    }
}

// =============================================================================
//  SelectHand —— 对齐 StepConditionEvaluator.cs:305
// =============================================================================
const HandResult* StepConditionEvaluator::SelectHand(const StepCondition& cond,
                                                     const FrameDetections* handInput) {
    if (!handInput || handInput->hands.empty()) return nullptr;

    string side = cond.param("HandSide", "right");
    for (char& c : side) if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');

    // C#: handSide switch { "left" or "左手" => Left, _ => Right }
    bool wantLeft = (side == "left" || side == "左手");

    // ★ 优先取指定手（默认右手）
    for (const auto& h : handInput->hands) {
        bool isLeft = h.isLeft;
        if (isLeft == wantLeft && h.isValidGesture())   // IsValidGesture(8)
            return &h;
    }
    // ★ 取不到指定手 → 回退任意一只有效手
    for (const auto& h : handInput->hands) {
        if (h.isValidGesture()) return &h;
    }
    return nullptr;
}

// =============================================================================
//  CheckHandCondition —— 对齐 StepConditionEvaluator.cs:321
// =============================================================================
ConditionCheckResult StepConditionEvaluator::CheckHandCondition(
    const StepCondition& cond,
    const std::vector<Detection>& detections,
    const FrameDetections* handInput,
    bool ignoreFromRegion) {

    const HandResult* hand = SelectHand(cond, handInput);
    if (!hand) {
        return ConditionCheckResult{false,
            std::string("未检测到") + cond.param("HandSide", "right") + "手"};
    }

    // ★★★ Center() = 全部关键点算术平均（HandPoseEstimationModels.cs:108）
    const Point center = handCenter(*hand);

    switch (cond.type) {

    // ---------------------------------------------------------------- HandInRegion
    case ConditionType::HandInRegion: {
        const ZoneDefinition* z = findZone(cond.zoneId);
        if (!z) return ConditionCheckResult{false, std::string("未找到区域: ") + cond.zoneId};
        return ConditionCheckResult{rectContainsPoint(*z, center),
            std::string("手部") + (rectContainsPoint(*z, center) ? " 在区域 " : " 未在区域 ")
            + cond.zoneId + " 内"};
    }

    // ------------------------------------------------------------ HandNotInRegion
    case ConditionType::HandNotInRegion: {
        const ZoneDefinition* z = findZone(cond.zoneId);
        if (!z) return ConditionCheckResult{false, std::string("未找到区域: ") + cond.zoneId};
        return ConditionCheckResult{!rectContainsPoint(*z, center),
            std::string("手部") + (rectContainsPoint(*z, center) ? " 仍在区域 " : " 不在区域 ")
            + cond.zoneId + " 内"};
    }

    // ---------------------------------------------------------------- HandStable
    case ConditionType::HandStable: {
        // ① 若指定区域，手部中心必须位于该区域内
        if (!cond.zoneId.empty()) {
            const ZoneDefinition* z = findZone(cond.zoneId);
            if (!z) return ConditionCheckResult{false, std::string("未找到区域: ") + cond.zoneId};
            if (!rectContainsPoint(*z, center))
                return ConditionCheckResult{false,
                    std::string("手部未位于区域 ") + cond.zoneId + " 内，稳定判定取消"};
        }

        // ② 中心轨迹位移检查
        HandTrackState* track = findTrack(hand->trackId);
        if (!track || static_cast<int>(track->centerHistory.size()) < cond.stableFrames) {
            int have = track ? static_cast<int>(track->centerHistory.size()) : 0;
            return ConditionCheckResult{false,
                std::string("手部稳定帧不足（已跟踪 ") + std::to_string(have) + std::string("/") +
                std::to_string(cond.stableFrames) + "）"};
        }

        float tolerance = cond.paramFloat("Tolerance", 20.0f);

        // 取最近 N 帧，算【最大相邻位移】
        const auto& hist = track->centerHistory;
        int n = static_cast<int>(hist.size());
        int start = n - cond.stableFrames;
        if (start < 0) start = 0;

        float maxMove = 0.0f;
        for (int i = start + 1; i < n; ++i) {
            float dx = hist[i].x - hist[i - 1].x;
            float dy = hist[i].y - hist[i - 1].y;
            float d = std::sqrt(dx * dx + dy * dy);
            if (d > maxMove) maxMove = d;
        }

        return ConditionCheckResult{maxMove <= tolerance,
            std::string("手部") + (maxMove <= tolerance ? "已稳定" : "未稳定") +
            std::string("（最大位移 ") + fmt(maxMove) + std::string("px ") +
            (maxMove <= tolerance ? "≤" : ">") + fmt(tolerance) + "px）"};
    }

    // ------------------------------------------------------------ HandMoveFromTo
    case ConditionType::HandMoveFromTo:
        return CheckHandMoveFromTo(cond, detections, handInput, ignoreFromRegion, hand, center);

    default:
        return ConditionCheckResult{false, "未支持的手部条件"};
    }
}

// =============================================================================
//  HandMoveFromTo —— 对齐 StepConditionEvaluator.cs:382
// =============================================================================
ConditionCheckResult StepConditionEvaluator::CheckHandMoveFromTo(
    const StepCondition& cond,
    const std::vector<Detection>& detections,
    const FrameDetections* handInput,
    bool ignoreFromRegion,
    const HandResult* hand,
    const Point& center) {

    const ZoneDefinition* zoneFrom = findZone(cond.fromRegion);
    const ZoneDefinition* zoneTo   = findZone(cond.toRegion);

    if (!cond.fromRegion.empty() && !zoneFrom)
        return ConditionCheckResult{false, std::string("未找到起始区域: ") + cond.fromRegion};
    if (!cond.toRegion.empty() && !zoneTo)
        return ConditionCheckResult{false, std::string("未找到目标区域: ") + cond.toRegion};

    bool inFrom = zoneFrom && rectContainsPoint(*zoneFrom, center);
    bool inTo   = zoneTo   && rectContainsPoint(*zoneTo,   center);

    // ★ visitedFrom：手访问过 from_region 【或】物体访问过 from_region，都算
    //   喝水 SOP 里 from_region 是 mouth_region，但手轨迹里可能没有
    //   （那一步判的是"杯子到达嘴边"），必须借物体的区域访问记录，
    //   否则放回步骤会永远卡在 visitedFrom=false。
    HandTrackState* track = findTrack(hand->trackId);


    bool visitedFromByHand = track && track->hasVisited(cond.fromRegion);

    bool visitedFromByObject = false;
    if (!cond.targetObject.empty()) {
        auto it = _objectVisitedRegions.find(lower(cond.targetObject));
        if (it != _objectVisitedRegions.end() && zoneFrom)
            visitedFromByObject = it->second.count(zoneFrom->id) > 0;
    }

    // ★ 跳步检测时放松此约束（跳步本就跳过中间步骤，物体自然没到过起始区域）
    bool visitedFrom = !cond.fromRegion.empty() &&
                       (ignoreFromRegion || visitedFromByHand || visitedFromByObject);

    // ---------------------------------------------------------------- 目标物体
    const Detection* targetObj = nullptr;
    if (!cond.targetObject.empty()) {
        float bestConf = -1.0f;
        for (const auto& d : detections) {
            if (!equalsIgnoreCase(d.className, cond.targetObject)) continue;
            if (d.score < cond.minConfidence) continue;
            if (d.score > bestConf) { bestConf = d.score; targetObj = &d; }
        }
    }

    bool handHoldingTarget = false;
    if (targetObj)
        handHoldingTarget = IsHandHoldingObject(*hand, *targetObj);

    // ---------------------------------------------------------------- 主判据
    // ★★ 有 to_region（放回语义）：对齐 C# StepConditionEvaluator.cs:465-503
    if (!cond.toRegion.empty()) {
        // ① 物体到达目标区域（放下的最直接证据）
        if (targetObj) {
            bool objInTo = rectIntersects(targetObj->box, *zoneTo);
            // 若指定了起始区域，要求"曾访问过"才算从该区域放到目标区域
            if (objInTo && visitedFrom)
                return ConditionCheckResult{true,
                    std::string("目标物体 '") + cond.targetObject + "' 已到达区域 " + cond.toRegion};
        }
        // ② 辅助判据：手到达目标区域 且 正握着目标物体
        //    （手把杯子放到该区域，而非空手碰一下就判定放回）
        if (inTo && visitedFrom && handHoldingTarget)
            return ConditionCheckResult{true,
                std::string("手部携带 '") + cond.targetObject + "' 已到达区域 " + cond.toRegion};

        return ConditionCheckResult{false,
            std::string("手部/物体尚未从 ") + cond.fromRegion + " 到达 " + cond.toRegion +
            "（需携带 '" + cond.targetObject + "'）"};
    }

    // ★★ 无 to_region（pickup 语义）：对齐 C# :505-549
    //   ⚠️ 关键约束：目标物体必须【当前帧】位于 from_region（或手在 from_region），
    //      才能判定"从该区域拿起"。禁止用"会话历史曾访问过 from_region"判断 ——
    //      那会导致杯子从任何地方拿都判 OK，from_region 约束失效。
    if (!cond.fromRegion.empty()) {
        bool objInFromNow = false;
        if (targetObj && zoneFrom)
            objInFromNow = rectIntersects(targetObj->box, *zoneFrom);

        // 杯子当前必须在起始区域（或手当前在起始区域）
        if (!(objInFromNow || inFrom))
            return ConditionCheckResult{false,
                std::string("'") + cond.targetObject + "' 不在区域 " + cond.fromRegion +
                "（需从 " + cond.fromRegion + " 拿起，当前位置不符）"};

        // 判定：手正握着目标物体 —— 最直接的"拿起"证据
        if (handHoldingTarget)
            return ConditionCheckResult{true,
                std::string("手部已在 ") + cond.fromRegion + " 内拿起 '" + cond.targetObject + "'"};

        return ConditionCheckResult{false,
            std::string("手部/物体位于区域 ") + cond.fromRegion +
            "，但未检测到 '" + cond.targetObject + "' 被拿起"};
    }

    // 既无 from 也无 to：只判"手拿着目标物体"
    if (handHoldingTarget)
        return ConditionCheckResult{true, std::string("手部已拿起 '") + cond.targetObject + "'"};

    return ConditionCheckResult{false, "未配置起始/目标区域"};
}

// =============================================================================
//  HandNearObject —— 对齐 StepConditionEvaluator.cs:140 → CheckHandNearObject
// =============================================================================
ConditionCheckResult StepConditionEvaluator::CheckHandNearObject(
    const StepCondition& cond,
    const std::vector<Detection>& detections,
    const FrameDetections* handInput) {

    const HandResult* hand = SelectHand(cond, handInput);
    if (!hand) return ConditionCheckResult{false, "未检测到手部"};

    const Detection* target = nullptr;
    float bestConf = -1.0f;
    for (const auto& d : detections) {
        if (!equalsIgnoreCase(d.className, cond.targetObject)) continue;
        if (d.score < cond.minConfidence) continue;
        if (d.score > bestConf) { bestConf = d.score; target = &d; }
    }

    if (!target)
        return ConditionCheckResult{false, std::string("未检测到目标 '") + cond.targetObject + "'"};

    if (IsHandHoldingObject(*hand, *target))
        return ConditionCheckResult{true, std::string("手部靠近 '") + cond.targetObject + "'"};

    return ConditionCheckResult{false, std::string("手部未靠近 '") + cond.targetObject + "'"};
}

// =============================================================================
//  IsHandHoldingObject —— 对齐 StepConditionEvaluator.cs:601（三级短路，30px 外扩）
// =============================================================================
bool StepConditionEvaluator::IsHandHoldingObject(const HandResult& hand,
                                                 const Detection& obj,
                                                 string* detail) {
    const Rect& objBox = obj.box;
    if (objBox.w <= 0.0f || objBox.h <= 0.0f) {
        if (detail) *detail = "(物体 BBox 为空)";
        return false;
    }

    // ① 手框与物框相交
    if (rectIntersects(hand.box, ZoneDefinition{"", "", objBox.x, objBox.y, objBox.w, objBox.h})) {
        if (detail) *detail = "(BBox相交)";
        return true;
    }

    const Point center = handCenter(hand);

    // ② 手中心落在物体框内
    if (center.x >= objBox.x && center.x <= objBox.x + objBox.w &&
        center.y >= objBox.y && center.y <= objBox.y + objBox.h) {
        if (detail) *detail = "(手中心在物体内)";
        return true;
    }

    // ③ 手中心在物体框附近（外扩 30px 固定容差）
    //    ★ 照抄 C# 的 30px 固定像素，不改成比例；分辨率不同须记入配方差异表
    const float margin = 30.0f;
    if (center.x >= objBox.x - margin && center.x <= objBox.x + objBox.w + margin &&
        center.y >= objBox.y - margin && center.y <= objBox.y + objBox.h + margin) {
        if (detail) *detail = "(手中心在物体框外扩30px范围内)";
        return true;
    }

    if (detail) *detail = "(手中心距离物体过远)";
    return false;
}

// =============================================================================
//  物体类条件
// =============================================================================

ConditionCheckResult StepConditionEvaluator::CheckObjectPresent(
    const StepCondition& cond, const std::vector<Detection>& detections) {

    int count = 0;
    for (const auto& d : detections) {
        if (!equalsIgnoreCase(d.className, cond.targetObject)) continue;
        if (d.score < cond.minConfidence) continue;
        count++;
    }
    return ConditionCheckResult{count > 0,
        count > 0 ? std::string("检测到 '") + cond.targetObject + "'"
                  : std::string("未检测到目标 '") + cond.targetObject + "'"};
}

ConditionCheckResult StepConditionEvaluator::CheckObjectInZone(
    const StepCondition& cond, const std::vector<Detection>& detections) {

    if (cond.zoneId.empty()) return ConditionCheckResult{false, "未指定区域ID"};
    const ZoneDefinition* z = findZone(cond.zoneId);
    if (!z) return ConditionCheckResult{false, std::string("未找到区域定义: ") + cond.zoneId};

    for (const auto& d : detections) {
        if (!equalsIgnoreCase(d.className, cond.targetObject)) continue;
        if (d.score < cond.minConfidence) continue;
        if (rectIntersects(d.box, *z))
            return ConditionCheckResult{true,
                std::string("目标 '") + cond.targetObject + std::string("' 在区域 '") + cond.zoneId + "' 内"};
    }
    return ConditionCheckResult{false,
        std::string("目标 '") + cond.targetObject + std::string("' 不在区域 '") + cond.zoneId + "' 内"};
}

ConditionCheckResult StepConditionEvaluator::CheckObjectAbsent(
    const StepCondition& cond, const std::vector<Detection>& detections) {

    for (const auto& d : detections) {
        if (!equalsIgnoreCase(d.className, cond.targetObject)) continue;
        if (d.score < cond.minConfidence) continue;
        return ConditionCheckResult{false, std::string("目标 '") + cond.targetObject + "' 仍存在"};
    }
    return ConditionCheckResult{true, std::string("目标 '") + cond.targetObject + "' 不存在"};
}

ConditionCheckResult StepConditionEvaluator::CheckObjectStable(
    const StepCondition& cond) {

    // 需要 TrackedObjects（由状态机维护）
    if (!_stateMachine)
        return ConditionCheckResult{false, std::string("未跟踪到目标 '") + cond.targetObject + "'"};

    float tolerance = cond.paramFloat("Tolerance", 20.0f);

    int total = 0, stable = 0;
    for (const auto& kv : _stateMachine->trackedObjects()) {
        if (!equalsIgnoreCase(kv.second.label, cond.targetObject)) continue;
        total++;
        // 与 C# TrackedObject.IsStable(tolerance) 同义：最近 N 帧最大相邻位移 <= tol
        const auto& hist = kv.second.centerHistory;
        int n = static_cast<int>(hist.size());
        int need = cond.stableFrames > 0 ? cond.stableFrames : 2;
        if (n >= need) {
            float maxMove = 0.0f;
            for (int i = n - need + 1; i < n; ++i) {
                float dx = hist[i].x - hist[i - 1].x;
                float dy = hist[i].y - hist[i - 1].y;
                float d = std::sqrt(dx * dx + dy * dy);
                if (d > maxMove) maxMove = d;
            }
            if (maxMove <= tolerance) stable++;
        }
    }

    if (total == 0)
        return ConditionCheckResult{false, std::string("未跟踪到目标 '") + cond.targetObject + "'"};
    return ConditionCheckResult{stable > 0,
        stable > 0 ? std::string("目标 '") + cond.targetObject + "' 已稳定"
                   : std::string("目标 '") + cond.targetObject + "' 未稳定"};
}

ConditionCheckResult StepConditionEvaluator::CheckTimeElapsed(
    const StepCondition& cond, int64_t stepElapsedMs) {

    int required = cond.durationMs;
    if (required <= 0 && cond.stableFrames > 0)
        required = cond.stableFrames * 33;   // 与 C# 换算口径一致

    if (stepElapsedMs >= required)
        return ConditionCheckResult{true, std::string("已用时 ") + std::to_string(stepElapsedMs) +
                                           std::string("ms ≥ ") + std::to_string(required) + "ms"};

    return ConditionCheckResult{false,
        std::string("用时 ") + std::to_string(stepElapsedMs) + std::string("ms < ") + std::to_string(required) + "ms"};
}

ConditionCheckResult StepConditionEvaluator::CheckSequenceComplete(const StepCondition&) {
    // C# 版 CheckSequenceComplete 返回条件成立与否，具体语义由状态机上下文决定
    return ConditionCheckResult{true, "序列完成"};
}

// =============================================================================
//  跨帧状态更新 —— 对齐 C# UpdateHandTracks / UpdateObjectRegions
// =============================================================================

void StepConditionEvaluator::UpdateHandTracks(const FrameDetections* handInput) {
    if (!handInput) return;

    std::set<int> alive;

    for (const auto& hand : handInput->hands) {
        if (hand.trackId < 0) continue;
        alive.insert(hand.trackId);

        HandTrackState& track = _handTracks[hand.trackId];
        track.trackId = hand.trackId;

        const Point center = handCenter(hand);
        track.updateCenter(center);
        track.updateBox(hand.box);

        // 记录当前所在区域（中心点包含判定）
        for (const auto& kv : _zones) {
            if (rectContainsPoint(kv.second, center))
                track.noteVisited(kv.second.id);
        }
    }

    // 清理离开画面的手
    for (auto it = _handTracks.begin(); it != _handTracks.end();) {
        if (alive.count(it->first) == 0) it = _handTracks.erase(it);
        else ++it;
    }
}

void StepConditionEvaluator::UpdateObjectRegions(const std::vector<Detection>& detections) {
    for (const auto& det : detections) {
        if (det.className.empty()) continue;
        auto& set = _objectVisitedRegions[lower(det.className)];
        // ★ 与 IsInZone 一致：bbox 与区域相交即记为访问过
        for (const auto& kv : _zones) {
            if (rectIntersects(det.box, kv.second))
                set.insert(kv.second.id);
        }
    }
}

void StepConditionEvaluator::clearAllState() {
    _handTracks.clear();
    _objectVisitedRegions.clear();
    ResetStableCounts();
}

// =============================================================================
//  辅助
// =============================================================================
const ZoneDefinition* StepConditionEvaluator::findZone(const string& id) const {
    if (id.empty()) return nullptr;
    auto it = _zones.find(id);
    return it == _zones.end() ? nullptr : &it->second;
}

HandTrackState* StepConditionEvaluator::findTrack(int trackId) {
    auto it = _handTracks.find(trackId);
    return it == _handTracks.end() ? nullptr : &it->second;
}

string StepConditionEvaluator::lower(string s) {
    for (char& c : s) if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    return s;
}

}  // namespace sop
}  // namespace vis