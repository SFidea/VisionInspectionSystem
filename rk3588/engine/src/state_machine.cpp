// =============================================================================
//  state_machine.cpp  —  SOPStateMachine 的 C++ 移植
// =============================================================================
//  1:1 移植自 C# SOPStateMachine.cs。对拍时重点核对：
//    · 起始步骤取【最小 StepId】而非硬编码 1
//    · Start/Reset 必须 ResetStableCounts()
//    · ProcessFrame 顺序：更新跟踪 → 违规检测 → 条件评估 → 步骤推进
//    · LastEvaluation 为录制/对拍只读快照，不参与判定
// =============================================================================
#include "sop/state_machine.h"

#include <algorithm>
#include <cmath>

namespace vis {
namespace sop {

SOPStateMachine::SOPStateMachine(const std::vector<ZoneDefinition>* zones)
    : _conditionEvaluator(this, zones), _violationDetector(this) {
    if (zones) {
        _zoneList = *zones;
        _violationDetector.updateZones(*zones);
    }
}

SOPStateMachine::~SOPStateMachine() = default;

namespace {

/// 小写化（对齐 C# 的 OrdinalIgnoreCase 比较口径）
std::string toLower(std::string s) {
    for (char& c : s) if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    return s;
}

}  // namespace

// =============================================================================
//  生命周期 —— 对齐 SOPStateMachine.cs:52/57
// =============================================================================
void SOPStateMachine::UpdateZones(const std::vector<ZoneDefinition>& zones) {
    _zoneList = zones;
    _conditionEvaluator.updateZones(zones);
    _violationDetector.updateZones(zones);
}

void SOPStateMachine::Start(const SOPWorkflow& workflow, int64_t nowMs) {
    _workflow = const_cast<SOPWorkflow*>(&workflow);

    _stepHistory.clear();
    _violations.clear();
    _trackedObjects.clear();
    _lastSeenMs.clear();

    // ★ 清上一轮残留的稳定帧计数，防止新一轮步骤"秒过"
    _conditionEvaluator.ResetStableCounts();
    _violationDetector.resetCooldowns();

    // ★ 起始步骤取工作流中最小的 StepId（配方 id 不一定从 1 开始）
    if (!_workflow->steps.empty()) {
        const SOPStep* first = &_workflow->steps[0];
        for (const auto& s : _workflow->steps)
            if (s.stepId < first->stepId) first = &s;
        _currentStepId = first->stepId;
    } else {
        _currentStepId = 1;
    }

    _stepStartMs = nowMs;
    _workflowStartMs = nowMs;
    _lastEvaluation = nullptr;

    ChangeState(SOPExecutionState::Running);

    if (_onStepChanged)
        _onStepChanged(-1, _currentStepId,
                       currentStep() ? currentStep()->stepName : string());
}

void SOPStateMachine::StartAt(const SOPWorkflow& workflow, int64_t nowMs, int stepId) {
    Start(workflow, nowMs);
    // 只有当目标步骤存在时才跳转
    if (stepId > 0 && workflow.findStep(stepId)) {
        int prev = _currentStepId;
        _currentStepId = stepId;
        _stepStartMs = nowMs;
        // ★ 跳转后必须清稳定帧计数，否则新步骤会"秒过"
        _conditionEvaluator.ResetStableCounts();
        if (_onStepChanged)
            _onStepChanged(prev, _currentStepId, currentStep() ? currentStep()->stepName : "");
    }
}

void SOPStateMachine::Reset(int64_t nowMs) {
    // ★ 与 Start 同样必须清稳定帧计数与跨帧状态
    _conditionEvaluator.clearAllState();
    _trackedObjects.clear();
    _lastSeenMs.clear();
    _stepHistory.clear();
    _violations.clear();
    _lastEvaluation = nullptr;

    if (_workflow && !_workflow->steps.empty()) {
        const SOPStep* first = &_workflow->steps[0];
        for (const auto& s : _workflow->steps)
            if (s.stepId < first->stepId) first = &s;
        _currentStepId = first->stepId;
    }

    _stepStartMs = nowMs;
    _workflowStartMs = nowMs;
    ChangeState(SOPExecutionState::Idle);
}

void SOPStateMachine::ChangeState(SOPExecutionState s) {
    if (_currentState == s) return;
    _currentState = s;
    if (_onStateChanged) _onStateChanged(s);
}

// =============================================================================
//  ProcessFrame —— 对齐 SOPStateMachine.cs:68
//  顺序：① 更新物体跟踪 ② 违规检测 ③ 条件评估 ④ 步骤推进
// =============================================================================
void SOPStateMachine::ProcessFrame(const std::vector<Detection>& detections,
                                   const FrameDetections* handInput,
                                   int64_t timestampMs) {
    if (!_workflow || _currentState != SOPExecutionState::Running) return;

    const SOPStep* step = currentStep();
    if (!step) return;

    // ① 更新物体跟踪
    UpdateTrackedObjects(detections, timestampMs);

    // ② 违规检测（超时 / 违规物体 / 区域入侵 / 缺料 等）
    DetectViolations(*step, detections, handInput, timestampMs);

    // ③ 条件评估（AND 短路）
    _lastEvalStorage = _conditionEvaluator.EvaluateConditions(
        *step, detections, handInput, false);
    // ★ 只读快照：供黄金录制与离线对拍，不参与任何判定
    _lastEvaluation = &_lastEvalStorage;

    // ④ 步骤推进
    if (_lastEvalStorage.isPass) {
        CompleteCurrentStep(timestampMs);
    }
}

// =============================================================================
//  ProbeNextStep —— 跳步探测用（探测性调用）
//  ★ ignoreFromRegion=true：跳步场景本就跳过中间步骤，
//    物体自然没经过起始区域（如没到嘴边），若强制要求 visitedFrom 会导致跳步漏报。
// =============================================================================
ConditionEvaluationResult SOPStateMachine::ProbeNextStep(
    int stepId,
    const std::vector<Detection>& detections,
    const FrameDetections* handInput) const {

    if (!_workflow) return ConditionEvaluationResult{};
    const SOPStep* s = _workflow->findStep(stepId);
    if (!s) return ConditionEvaluationResult{};

    // 注意：EvaluateConditions 非 const，这里用 const_cast。
    // ★ 但 isCurrentStep 判定使稳定帧门控与清零都不会作用于本步骤
    //   （step.stepId != currentStepId）—— 这正是防互踩的关键。
    return const_cast<StepConditionEvaluator&>(_conditionEvaluator)
        .EvaluateConditions(*s, detections, handInput, /*ignoreFromRegion=*/true);
}

// =============================================================================
//  CompleteCurrentStep —— 对齐 SOPStateMachine.cs:267
// =============================================================================
void SOPStateMachine::CompleteCurrentStep(int64_t timestampMs) {
    if (!_workflow) return;
    const SOPStep* step = currentStep();
    if (!step) return;

    StepExecutionRecord rec;
    rec.stepId = _currentStepId;
    rec.stepName = step->stepName;
    rec.startMs = _stepStartMs;
    rec.endMs = timestampMs;
    rec.durationSec = (timestampMs - _stepStartMs) / 1000.0;
    rec.standardDurationSec = step->standardDurationSec;
    rec.hasViolations = false;
    for (const auto& v : _violations)
        if (v.stepId == _currentStepId) { rec.hasViolations = true; break; }
    _stepHistory.push_back(rec);

    bool hasViolations = rec.hasViolations;
    if (_onStepCompleted) _onStepCompleted(_currentStepId, hasViolations);

    // 流程是否结束
    if (step->nextSteps.empty()) {
        bool pass = !hasViolations;
        ChangeState(pass ? SOPExecutionState::Completed : SOPExecutionState::Faulted);
        if (_onWorkflowCompleted)
            _onWorkflowCompleted(pass, _workflow->name);
        return;
    }

    // 取下一个步骤（NextSteps 是 id 的字符串列表）
    int nextId = _currentStepId;
    try { nextId = std::stoi(step->nextSteps[0]); }
    catch (...) { nextId = _currentStepId + 1; }

    const SOPStep* next = _workflow->findStep(nextId);
    if (!next) {
        ChangeState(hasViolations ? SOPExecutionState::Faulted
                                  : SOPExecutionState::Completed);
        if (_onWorkflowCompleted)
            _onWorkflowCompleted(!hasViolations, _workflow->name);
        return;
    }

    int prev = _currentStepId;
    _currentStepId = nextId;
    _stepStartMs = timestampMs;
    // ★ 步骤切换时清稳定帧计数（否则新步骤秒过）
    _conditionEvaluator.ResetStableCounts();

    if (_onStepChanged) _onStepChanged(prev, _currentStepId, next->stepName);
}

// =============================================================================
//  物体跟踪 —— 对齐 C# UpdateTrackedObjects
// =============================================================================
void SOPStateMachine::UpdateTrackedObjects(const std::vector<Detection>& detections,
                                           int64_t ts) {
    std::set<int> alive;
    for (const auto& d : detections) {
        if (d.className.empty()) continue;
        // 简化：以标签聚合为稳定 trackId（与 C# 的跟踪器行为不完全一致，
        // 但 CheckObjectStable 只关心"该标签是否稳定"，此聚合已足够）
        int key = static_cast<int>(std::hash<string>{}(toLower(d.className)));

        auto it = _trackedObjects.find(key);
        if (it == _trackedObjects.end()) {
            TrackedObject o;
            o.trackId = key;
            o.label = d.className;
            o.box = d.box;
            o.confidence = d.score;
            _trackedObjects[key] = o;
        } else {
            it->second.box = d.box;
            it->second.confidence = d.score;
            it->second.isLost = false;
            it->second.lostFrames = 0;
        }
        TrackedObject& o = _trackedObjects[key];
        // 中心轨迹（CheckObjectStable 用）
        Point c{ o.box.centerX(), o.box.centerY() };
        o.centerHistory.push_back(c);
        while (static_cast<int>(o.centerHistory.size()) > kMaxHistory)
            o.centerHistory.erase(o.centerHistory.begin());

        _lastSeenMs[key] = static_cast<int>(ts);
        alive.insert(key);
    }

    // 标记丢失
    for (auto& kv : _trackedObjects) {
        if (alive.count(kv.first) == 0) {
            kv.second.isLost = true;
            kv.second.lostFrames++;
        }
    }
}

// =============================================================================
//  违规检测 —— 对齐 C# ViolationDetector（核心：超时 / 违规物体）
// =============================================================================
void SOPStateMachine::DetectViolations(const SOPStep& step,
                                       const std::vector<Detection>& detections,
                                       const FrameDetections* handInput,
                                       int64_t ts) {
    // ★ 委托 ViolationDetector（8 类违规 + 3 秒冷却 + 跳步探测）
    auto violations = _violationDetector.detect(step, detections, handInput, ts);
    for (const auto& v : violations) {
        recordViolation(v);
        if (_onViolationEval)
            _onViolationEval(toString(v.type), true, v.description);
    }
}

void SOPStateMachine::recordViolation(const ViolationRecord& v) {
    _violations.push_back(v);
    if (_onViolation) _onViolation(v);
}

}  // namespace sop
}  // namespace vis