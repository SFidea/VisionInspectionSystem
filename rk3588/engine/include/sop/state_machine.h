// =============================================================================
//  state_machine.h  —  SOPStateMachine 的 C++ 移植
// =============================================================================
//  1:1 移植自 C# src/Modules/VisionInspection.Modules.SOP/Models/SOPStateMachine.cs
//
//  ★ 与评估器共享实例（ViolationDetector 的 DetectSkipStep 也要用）——
//    这是 2026-09-29 回归的根源：若评估器不区分"当前步骤"与"探测下一步"，
//    两者会互踩稳定帧计数，导致步骤卡死 + 跳步不报警。
// =============================================================================
#pragma once

#include <functional>
#include <string>
#include <vector>

#include "sop/step_condition.h"
#include "sop/types.h"

namespace vis {
namespace sop {

class SOPStateMachine;

// ---- 事件回调（对齐 C# 的 event EventHandler<>）----
using StepChangedCb      = std::function<void(int prevStep, int curStep, const std::string& stepName)>;
using ViolationCb        = std::function<void(const ViolationRecord& v)>;
using StateChangedCb     = std::function<void(SOPExecutionState s)>;
using WorkflowCompletedCb = std::function<void(bool isPass, const std::string& workflowName)>;
using StepCompletedCb    = std::function<void(int stepId, bool hasViolations)>;
using ViolationEvalCb    = std::function<void(const std::string& type, bool isTriggered,
                                               const std::string& desc)>;

/// 单步执行记录
struct StepExecutionRecord {
    int    stepId = 0;
    std::string stepName;
    int64_t startMs = 0;
    int64_t endMs = 0;
    double durationSec = 0.0;
    double standardDurationSec = 0.0;
    bool   hasViolations = false;
};

class SOPStateMachine {
public:
    explicit SOPStateMachine(const std::vector<ZoneDefinition>* zones = nullptr);
    ~SOPStateMachine();

    // ---- 生命周期 ----
    /// ★ 起始步骤取工作流中【最小的 StepId】（配方步骤 id 不一定从 1 开始），
    ///   且必须调 ResetStableCounts()，防上一轮残留计数让新一轮"秒过"。
    void Start(const SOPWorkflow& workflow, int64_t nowMs);

    /// ★ 从指定步骤起步（黄金回放对拍用）
    ///   原因：录制窗口中途打开时 Windows 状态机已在中间步骤，
    ///   若 C++ 从配方第一步起步会造成"步骤错位"，比对全是假差异。
    void StartAt(const SOPWorkflow& workflow, int64_t nowMs, int stepId);

    void Reset(int64_t nowMs);

    /// 单帧推进 —— 对齐 C# ProcessFrame
    void ProcessFrame(const std::vector<Detection>& detections,
                      const FrameDetections* handInput,
                      int64_t timestampMs);

    void UpdateZones(const std::vector<ZoneDefinition>& zones);

    // ---- 只读状态 ----
    SOPExecutionState currentState() const { return _currentState; }
    int  currentStepId() const { return _currentStepId; }
    int64_t stepStartMs() const { return _stepStartMs; }
    int64_t stepElapsedMs(int64_t nowMs) const { return nowMs - _stepStartMs; }
    const SOPWorkflow* workflow() const { return _workflow; }

    /// 当前步骤（找不到返回 nullptr）
    const SOPStep* currentStep() const { return _workflow ? _workflow->findStep(_currentStepId) : nullptr; }

    /// ★ 黄金数据集录制 / 离线对拍用的只读快照（不参与判定）
    const ConditionEvaluationResult* lastEvaluation() const { return _lastEvaluation; }

    const std::vector<ViolationRecord>&      violations() const { return _violations; }
    const std::vector<StepExecutionRecord>&   stepHistory() const { return _stepHistory; }
    const std::map<int, HandTrackState>&     handTracks() const {
        return _conditionEvaluator.handTracks();
    }
    /// 物体跟踪状态（CheckObjectStable 用）
    const std::map<int, TrackedObject>&      trackedObjects() const { return _trackedObjects; }

    // ---- 违规判定（供 ViolationDetector 使用）----
    /// ★ 跳步探测：评估"下一步"，ignoreFromRegion=true。
    ///   注意这是【探测性调用】，与主路径共用评估器实例 —— 稳定帧门控已做隔离。
    ConditionEvaluationResult ProbeNextStep(int stepId,
                                            const std::vector<Detection>& detections,
                                            const FrameDetections* handInput) const;

    StepConditionEvaluator&       evaluator()       { return _conditionEvaluator; }
    const StepConditionEvaluator& evaluator() const { return _conditionEvaluator; }

    // ---- 事件 ----
    void setStepChangedCb(StepChangedCb cb)       { _onStepChanged = std::move(cb); }
    void setViolationCb(ViolationCb cb)             { _onViolation = std::move(cb); }
    void setStateChangedCb(StateChangedCb cb)      { _onStateChanged = std::move(cb); }
    void setWorkflowCompletedCb(WorkflowCompletedCb cb) { _onWorkflowCompleted = std::move(cb); }
    void setStepCompletedCb(StepCompletedCb cb)    { _onStepCompleted = std::move(cb); }
    void setViolationEvalCb(ViolationEvalCb cb)    { _onViolationEval = std::move(cb); }

    /// 触发一条违规记录（ViolationDetector 与状态机共用）
    void recordViolation(const ViolationRecord& v);

private:
    void CompleteCurrentStep(int64_t timestampMs);
    void UpdateTrackedObjects(const std::vector<Detection>& detections, int64_t ts);
    void DetectViolations(const SOPStep& step,
                          const std::vector<Detection>& detections,
                          const FrameDetections* handInput,
                          int64_t ts);
    void ChangeState(SOPExecutionState s);

private:
    SOPWorkflow* _workflow = nullptr;
    std::vector<ZoneDefinition> _zoneList;

    SOPStateMachine* _selfStateMachine = this;   // 供评估器回查 currentStepId
    StepConditionEvaluator _conditionEvaluator;

    SOPExecutionState _currentState = SOPExecutionState::Idle;
    int     _currentStepId = 0;
    int64_t _stepStartMs = 0;
    int64_t _workflowStartMs = 0;

    std::vector<ViolationRecord>    _violations;
    std::vector<StepExecutionRecord> _stepHistory;
    std::map<int, TrackedObject>     _trackedObjects;
    std::map<int, int>               _lastSeenMs;

    ConditionEvaluationResult _lastEvalStorage;
    const ConditionEvaluationResult* _lastEvaluation = nullptr;

    // ---- 回调 ----
    StepChangedCb       _onStepChanged;
    ViolationCb         _onViolation;
    StateChangedCb      _onStateChanged;
    WorkflowCompletedCb _onWorkflowCompleted;
    StepCompletedCb     _onStepCompleted;
    ViolationEvalCb     _onViolationEval;
};

}  // namespace sop
}  // namespace vis