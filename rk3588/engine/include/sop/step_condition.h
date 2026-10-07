// =============================================================================
//  step_condition.h  —  StepConditionEvaluator
// =============================================================================
//  1:1 移植自 C# src/Modules/VisionInspection.Modules.SOP/Services/
//      StepConditionEvaluator.cs（878 行）
//
//  ★ 判定语义铁律（详见 src/step_condition.cpp 顶部注释与
//    rk3588/docs/手部关键点映射与区域换算.md 第五节）：
//    · Center() = 全部关键点算术平均（不是 bbox 中心/腕点/palm）
//    · SelectHand 先右手后回退；IsValidGesture 阈值为 8（非默认 15）
//    · HandStable 位移容差 20px
//    · IsHandHoldingObject 三级判定，末级外扩 30px
//    · IsInZone（物体）矩形相交；手部区域判定中心点包含 —— 语义不同勿混用
// =============================================================================
#pragma once

#include <map>
#include <set>
#include <string>
#include <vector>

#include "sop/types.h"

namespace vis {
namespace sop {

class SOPStateMachine;

/// 跨帧跟踪状态键：trackId → 状态
using HandTrackMap = std::map<int, HandTrackState>;

// =============================================================================
//  StepConditionEvaluator —— 条件评估器
// =============================================================================
class StepConditionEvaluator {
public:
    explicit StepConditionEvaluator(SOPStateMachine* sm = nullptr,
                                    const std::vector<ZoneDefinition>* zones = nullptr);

    /// 评估一个步骤的全部条件（AND 短路）
    /// @param ignoreFromRegion 跳步探测时为 true，放松 visitedFrom 约束
    /// @param currentStepElapsedMs 当前步骤已用时（TimeElapsed 条件用）
    ConditionEvaluationResult EvaluateConditions(
        const SOPStep& step,
        const std::vector<Detection>& detections,
        const FrameDetections* handInput = nullptr,
        bool ignoreFromRegion = false,
        int64_t currentStepElapsedMs = 0);

    /// 更新区域定义（状态机加载配方后调用）
    void updateZones(const std::vector<ZoneDefinition>& zones);

    /// ★ 清空稳定帧计数 —— 状态机 Start()/Reset() 必须调用。
    ///   防上一轮残留计数让新一轮步骤"秒过"。照抄 C# ResetStableCounts。
    void ResetStableCounts();

    /// 清空全部跨帧状态（手轨迹 + 物体区域访问 + 稳定计数）
    void clearAllState();

    HandTrackMap&       handTracks()       { return _handTracks; }
    const HandTrackMap& handTracks() const { return _handTracks; }

private:
    // ---- 内部判定（各对应 C# 私有方法）----
    ConditionCheckResult CheckCondition(const StepCondition& cond,
                                        const std::vector<Detection>& detections,
                                        const FrameDetections* handInput,
                                        bool ignoreFromRegion);
    ConditionCheckResult CheckHandCondition(const StepCondition& cond,
                                           const std::vector<Detection>& detections,
                                           const FrameDetections* handInput,
                                           bool ignoreFromRegion);
    ConditionCheckResult CheckHandMoveFromTo(const StepCondition& cond,
                                             const std::vector<Detection>& detections,
                                             const FrameDetections* handInput,
                                             bool ignoreFromRegion,
                                             const HandResult* hand,
                                             const Point& center);
    ConditionCheckResult CheckHandNearObject(const StepCondition& cond,
                                             const std::vector<Detection>& detections,
                                             const FrameDetections* handInput);
    ConditionCheckResult CheckObjectPresent(const StepCondition& cond,
                                           const std::vector<Detection>& detections);
    ConditionCheckResult CheckObjectInZone(const StepCondition& cond,
                                           const std::vector<Detection>& detections);
    ConditionCheckResult CheckObjectAbsent(const StepCondition& cond,
                                           const std::vector<Detection>& detections);
    ConditionCheckResult CheckObjectStable(const StepCondition& cond);
    ConditionCheckResult CheckTimeElapsed(const StepCondition& cond, int64_t stepElapsedMs);
    ConditionCheckResult CheckSequenceComplete(const StepCondition& cond);

    /// ★ 三级判定：框相交 → 中心在物框内 → 中心在物框外扩 30px 内
    static bool IsHandHoldingObject(const HandResult& hand, const Detection& obj,
                                    std::string* detail = nullptr);

    /// 选择目标手：优先 HandSide 指定手（默认右手），取不到回退任意 IsValidGesture(8) 手
    const HandResult* SelectHand(const StepCondition& cond,
                                 const FrameDetections* handInput);

    /// 稳定帧门控 —— 仅当前步骤生效，跳过清单照抄 C#
    void ApplyStableFrameGate(int stepId, int conditionIndex,
                              const StepCondition& cond, ConditionCheckResult& result);

    void UpdateHandTracks(const FrameDetections* handInput);
    void UpdateObjectRegions(const std::vector<Detection>& detections);

    HandTrackState* findTrack(int trackId);
    const ZoneDefinition* findZone(const string& id) const;
    static string lower(string s);

    SOPStateMachine* _stateMachine = nullptr;
    std::map<string, ZoneDefinition> _zones;

    /// ★ 跨帧累积状态 —— 移植时必须一并实现，否则复位残留会污染下一轮判定
    HandTrackMap _handTracks;

    /// 物体（按标签）最近访问过的区域 —— 用于 HandMoveFromTo 的 visitedFromByObject。
    /// ★ 喝水 SOP 的 mouth_region 就靠这条，否则"放回"步骤永远卡死。
    std::map<string, std::set<string>> _objectVisitedRegions;

    /// 稳定帧计数：key = "{stepId}:{conditionIndex}"
    std::map<string, int> _stablePassCounts;
    int _lastEvaluatedStepId = -1;

    /// 当前步骤已用时（毫秒），TimeElapsed 条件判定用
    int64_t _currentStepElapsedMs = 0;
};

}  // namespace sop
}  // namespace vis