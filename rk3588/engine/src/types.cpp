// =============================================================================
//  enum_names.cpp  —  枚举 ↔ 字符串映射
// =============================================================================
//  ★★★ 为什么这个文件单独存在 ★★★
//  decisions.jsonl 里 conditions 的 key 是【枚举名字符串】（由 C# ToString() 产生）。
//  RK3588 对拍时按字符串逐帧比对判定结果 ——
//  只要这里有一个字对不上，该条件就会 100% 不一致，且原因极难定位。
//  所以：名字与顺序必须与 C# 逐字对齐，改动前先核对 SOPWorkflow.cs:101 / :129。
// =============================================================================
#include "sop/types.h"

#include <cstring>

namespace vis {
namespace sop {

// =============================================================================
//  ConditionType —— 对齐 SOPWorkflow.cs:101（顺序即声明顺序）
// =============================================================================
namespace {
struct CondName { ConditionType t; const char* name; };
constexpr CondName kCondNames[] = {
    {ConditionType::ObjectPresent,     "ObjectPresent"},
    {ConditionType::ObjectInZone,       "ObjectInZone"},
    {ConditionType::ObjectStable,       "ObjectStable"},
    {ConditionType::ObjectAbsent,       "ObjectAbsent"},
    {ConditionType::SequenceComplete,   "SequenceComplete"},
    {ConditionType::TimeElapsed,        "TimeElapsed"},
    {ConditionType::HandInRegion,       "HandInRegion"},
    {ConditionType::HandNotInRegion,    "HandNotInRegion"},
    {ConditionType::HandStable,         "HandStable"},
    {ConditionType::HandMoveFromTo,     "HandMoveFromTo"},
    {ConditionType::HandNearObject,     "HandNearObject"},
    {ConditionType::Custom,             "Custom"},
};

struct ViolName { ViolationType t; const char* name; };
constexpr ViolName kViolNames[] = {
    {ViolationType::SkipStep,             "SkipStep"},
    {ViolationType::WrongOrder,           "WrongOrder"},
    {ViolationType::Timeout,              "Timeout"},
    {ViolationType::ForbiddenObject,      "ForbiddenObject"},
    {ViolationType::ObjectRemoved,        "ObjectRemoved"},
    {ViolationType::ZoneIntrusion,        "ZoneIntrusion"},
    {ViolationType::MissingRequiredObject,"MissingRequiredObject"},
    {ViolationType::Custom,               "Custom"},
};
}  // namespace

const char* toString(ConditionType t) {
    for (const auto& c : kCondNames) if (c.t == t) return c.name;
    return "Custom";
}

const char* toString(ViolationType t) {
    for (const auto& v : kViolNames) if (v.t == t) return v.name;
    return "Custom";
}

const char* toString(SOPExecutionState s) {
    switch (s) {
        case SOPExecutionState::Idle:      return "Idle";
        case SOPExecutionState::Running:   return "Running";
        case SOPExecutionState::Paused:    return "Paused";
        case SOPExecutionState::Completed: return "Completed";
        case SOPExecutionState::Faulted:   return "Faulted";
    }
    return "Idle";
}

const char* toString(HandType t) {
    switch (t) {
        case HandType::Unknown: return "Unknown";
        case HandType::Left:    return "Left";
        case HandType::Right:   return "Right";
    }
    return "Unknown";
}

bool parseConditionType(const string& s, ConditionType* out) {
    for (const auto& c : kCondNames)
        if (s == c.name) { *out = c.t; return true; }
    return false;
}

bool parseViolationType(const string& s, ViolationType* out) {
    for (const auto& v : kViolNames)
        if (s == v.name) { *out = v.t; return true; }
    return false;
}

// =============================================================================
//  StepCondition::param —— 照抄 C# Parameters.GetValueOrDefault 语义：
//  取不到返回默认值；数字字符串转 float。
// =============================================================================
string StepCondition::param(const string& key, const string& def) const {
    auto it = parameters.find(key);
    if (it == parameters.end() || it->second.empty()) return def;
    return it->second;
}

float StepCondition::paramFloat(const string& key, float def) const {
    auto it = parameters.find(key);
    if (it == parameters.end() || it->second.empty()) return def;
    try { return std::stof(it->second); }
    catch (...) { return def; }
}

const SOPStep* SOPWorkflow::findStep(int stepId) const {
    for (const auto& s : steps)
        if (s.stepId == stepId) return &s;
    return nullptr;
}

// =============================================================================
//  HandTrackState —— 对齐 C# StepConditionEvaluator:205/251/365
// =============================================================================
void HandTrackState::updateCenter(Point c) {
    centerHistory.push_back(c);
    // 上限保护：超过 MaxHistory 就丢最旧的（保证 HandStable 只看最近 N 帧）
    while (static_cast<int>(centerHistory.size()) > kMaxHistory)
        centerHistory.erase(centerHistory.begin());
}

void HandTrackState::updateBox(const Rect& r) {
    boxLeft = r.x; boxTop = r.y;
    boxRight = r.x + r.w; boxBottom = r.y + r.h;
}

bool HandTrackState::hasVisited(const string& regionId) const {
    if (regionId.empty()) return false;
    for (const auto& r : recentRegions)
        if (r == regionId) return true;
    return false;
}

void HandTrackState::noteVisited(const string& regionId) {
    if (regionId.empty()) return;
    if (hasVisited(regionId)) return;              // HashSet 语义：天然去重
    recentRegions.push_back(regionId);
    // 对齐 C#：超过 20 条就丢最旧的一条
    while (static_cast<int>(recentRegions.size()) > kMaxRecentRegions)
        recentRegions.erase(recentRegions.begin());
}

void HandTrackState::clearHistory() {
    centerHistory.clear();
    recentRegions.clear();
}

}  // namespace sop
}  // namespace vis