// =============================================================================
//  types.h  —  SOP 引擎核心类型定义
// =============================================================================
//  ★★★ 移植铁律 ★★★
//  枚举成员名、顺序必须与 C# 逐字对齐（SOPWorkflow.cs ConditionType / ViolationType）。
//     原因：decisions.jsonl 里 conditions 的 key 就是枚举名，
//           RK3588 对拍时按字符串比对，名字差一个字母就全盘失败。
//
//  本文件只含纯数据结构与枚举，【不 include 任何推理/相机/UI 头文件】，
//  以保证引擎可离线单测、可逐帧回放对拍。
// =============================================================================
#pragma once

#include <cstdint>
#include <map>
#include <string>
#include <vector>

#include "detector.h"   // Detection / HandResult（纯结构）

namespace vis {
namespace sop {

using std::string;
using std::vector;

// =============================================================================
//  1. 枚举（名字/顺序与 C# 逐字对齐，勿改）
// =============================================================================

/// 12 类条件 —— 对齐 SOPWorkflow.cs:101
enum class ConditionType : int {
    ObjectPresent = 0,
    ObjectInZone,
    ObjectStable,
    ObjectAbsent,
    SequenceComplete,
    TimeElapsed,
    // ===== 手部动作条件 =====
    HandInRegion,       // 指定手进入某区域
    HandNotInRegion,    // 指定手不在某区域
    HandStable,         // 指定手在区域内稳定 N 帧
    HandMoveFromTo,     // 指定手从区域 A 移动到区域 B
    HandNearObject,     // 指定手靠近某目标物体
    Custom
};

/// 8 类违规 —— 对齐 SOPWorkflow.cs:129
enum class ViolationType : int {
    SkipStep = 0,
    WrongOrder,
    Timeout,
    ForbiddenObject,
    ObjectRemoved,
    ZoneIntrusion,
    MissingRequiredObject,   // 漏放/缺料
    Custom
};

/// 执行状态
enum class SOPExecutionState : int {
    Idle = 0,
    Running,
    Paused,
    Completed,
    Faulted
};

/// 手的左右 —— 对齐 C# HandType { Unknown=0, Left=1, Right=2 }
enum class HandType : int {
    Unknown = 0,
    Left = 1,
    Right = 2
};

/// 手部关键点类型 —— 对齐 C# HandKeypointType（21 项，序号 = MediaPipe 标准）
enum class HandKeypointType : int {
    Wrist = 0,
    // 拇指 1-4
    ThumbCMC = 1, ThumbMCP = 2, ThumbIP = 3, ThumbTip = 4,
    // 食指 5-8
    IndexFingerMCP = 5, IndexFingerPIP = 6, IndexFingerDIP = 7, IndexFingerTip = 8,
    // 中指 9-12
    MiddleFingerMCP = 9, MiddleFingerPIP = 10, MiddleFingerDIP = 11, MiddleFingerTip = 12,
    // 无名指 13-16
    RingFingerMCP = 13, RingFingerPIP = 14, RingFingerDIP = 15, RingFingerTip = 16,
    // 小指 17-20
    PinkyMCP = 17, PinkyPIP = 18, PinkyDIP = 19, PinkyTip = 20
};

// =============================================================================
//  2. 枚举 ↔ 字符串（序列化必须与 C# 的 ToString() 完全一致）
// =============================================================================

const char* toString(ConditionType t);
const char* toString(ViolationType t);
const char* toString(SOPExecutionState s);
const char* toString(HandType t);
bool parseConditionType(const string& s, ConditionType* out);
bool parseViolationType(const string& s, ViolationType* out);

// =============================================================================
//  3. 区域定义
// =============================================================================

/// 区域 —— 对齐 C# ZoneDefinition
struct ZoneDefinition {
    string id;
    string name;
    float x = 0.0f, y = 0.0f;
    float width = 0.0f, height = 0.0f;

    float left()   const { return x; }
    float top()    const { return y; }
    float right()  const { return x + width; }
    float bottom() const { return y + height; }
    bool  valid()  const { return width > 0.0f && height > 0.0f; }
};

// =============================================================================
//  4. 条件 / 违规规则 / 步骤
// =============================================================================

/// 步骤条件 —— 对齐 C# StepCondition
struct StepCondition {
    ConditionType type = ConditionType::Custom;

    /// 目标物体标签（COCO 名，如 "cup"），空 = 不指定
    string targetObject;

    /// 关联区域 id（HandInRegion / HandNotInRegion / HandStable / ObjectInZone 用）
    string zoneId;

    /// HandMoveFromTo 的起止区域
    string fromRegion;
    string toRegion;

    float minConfidence = 0.6f;
    int   stableFrames = 3;
    int   durationMs   = 0;

    /// HandStable 的位移容差（px），C# 默认 20
    float tolerance = 20.0f;

    /// 其它参数（C# 里是 Dictionary<string,object>）
    /// 当前仅用到 HandSide / Tolerance / FromRegion / ToRegion
    std::map<string, string> parameters;

    /// 参数取值（照抄 C# Parameters.GetValueOrDefault 语义）
    string param(const string& key, const string& def) const;
    float  paramFloat(const string& key, float def) const;
};

/// 违规规则 —— 对齐 C# ViolationRule
struct ViolationRule {
    ViolationType type = ViolationType::Custom;
    string description;
    string severity = "warning";
};

/// Modbus 触发配置 —— 对齐 C# ModbusTriggerConfig
struct ModbusTriggerConfig {
    bool   enabled = false;
    int    address = 0;
    int    pulseMs = 100;
    bool   latch = false;
};

/// SOP 步骤 —— 对齐 C# SOPStep
struct SOPStep {
    int    stepId = 0;
    string stepName;
    string description;
    int    order = 0;

    double standardDurationSec = 0.0;   // 标准耗时（秒），来自 standard_duration_ms
    int    timeoutSec = 30;

    vector<StepCondition>  passConditions;   // C# PassConditions
    vector<ViolationRule>  violationRules;

    /// 禁止物体标签（C# YAML 的 forbidden_objects）
    vector<string> forbiddenObjects;

    /// 必须放置的物料（C# YAML 的 required_objects）
    /// 数据驱动：每个配方在 YAML 声明自己的必放物料，切换产品即换 YAML，引擎无需改代码。
    vector<string> requiredObjects;

    /// 必须保持存在的物体（C# YAML 的 must_keep）
    vector<string> mustKeep;

    vector<int>            requiredPreviousSteps;
    vector<string>         nextSteps;         // C# NextSteps（步骤 id 的字符串）

    string cameraId = "main_camera";
    string modelPath;                        // 空 = 全局模型

    ModbusTriggerConfig modbusTrigger;
};

// =============================================================================
//  5. 工作流 —— 对齐 C# SOPWorkflow
// =============================================================================

/// 全局设置 —— 对齐 C# SOPGlobalSettings（SOPWorkflow.cs:149）
/// ★ enable_timeout_detection 必须从配方 settings 读取：
///   喝水配方设为 false，此时 C# 不产生 Timeout 违规，
///   若 C++ 忽略该开关会产生大量假违规（实测 90 vs 基线 7）。
struct SOPGlobalSettings {
    bool enableSkipDetection = true;
    bool enableTimeoutDetection = true;
    int  stableFrameCount = 5;
    float confidenceThreshold = 0.6f;
    float positionTolerance = 20.0f;
    bool autoResetOnComplete = true;
    int  resetDelaySec = 1;
};

struct SOPWorkflow {
    string name = "SOP";
    string version;
    string description;
    vector<SOPStep> steps;
    std::map<string, ZoneDefinition> zones;   // id -> 定义

    /// 全局模型路径（步骤未指定时用）
    string modelPath;

    /// ★ 全局设置（来自配方 settings 段）
    SOPGlobalSettings settings;

    /// 起始步骤 id
    int startStepId = 0;

    const SOPStep* findStep(int stepId) const;
};

// =============================================================================
//  6. 跨帧跟踪状态 —— 对齐 C# HandTrackState / TrackedObject
// =============================================================================

/// 中心历史最大长度 —— 对齐 C# HandTrackState.MaxHistory = 30
inline constexpr int kMaxHistory = 30;

/// RecentRegions 最大条数 —— 对齐 C# `if (RecentRegions.Count > 20) Remove(First())`
inline constexpr int kMaxRecentRegions = 20;

/// 单只手的跟踪状态 —— 对齐 C# HandTrackState
/// ★ 这些是跨步骤累积状态，移植时必须一并实现，否则复位后残留会污染下一轮判定
struct HandTrackState {
    int    trackId = -1;
    float  boxLeft = 0, boxTop = 0, boxRight = 0, boxBottom = 0;

    /// ★ GetCenter() 的历史序列 —— HandStable 算最大相邻位移用
    vector<Point> centerHistory;

    /// 近期访问过的区域名 —— HandMoveFromTo 的 visitedFromByHand 用
    /// ★ C# 用 HashSet<string>（天然去重），此处用 vector + 插入时去重来等价
    std::vector<string> recentRegions;

    void updateCenter(Point c);
    void updateBox(const Rect& r);
    bool hasVisited(const string& regionId) const;
    /// 记录访问过的区域（去重，超上限丢最旧）—— 对齐 C# UpdateHandTracks
    void noteVisited(const string& regionId);
    void clearHistory();
};

/// 物体跟踪状态 —— 对齐 C# TrackedObject
struct TrackedObject {
    int    trackId = -1;
    string label;
    float  confidence = 0.0f;
    Rect box;
    bool   isLost = false;
    int    lostFrames = 0;

    /// 中心轨迹 —— CheckObjectStable 算最大相邻位移用
    std::vector<Point> centerHistory;
};

// =============================================================================
//  7. 判定结果 —— 对齐 C# ConditionCheckResult / ConditionEvaluationResult
// =============================================================================

struct ConditionCheckResult {
    bool   isMet = false;
    string message;
};

struct ConditionEvaluationResult {
    bool   isPass = false;
    string message;
    int    failedIndex = -1;                     // 失败的 PassConditions 下标，-1 = 无
    vector<ConditionCheckResult> checkResults;   // 与 PassConditions 同序
};

/// 违规记录 —— 对齐 C# ViolationRecord
struct ViolationRecord {
    int64_t timestampMs = 0;
    ViolationType type = ViolationType::Custom;
    string description;
    int    stepId = 0;
    string evidence;
};

// =============================================================================
//  8. 手部结果便捷视图（引擎侧消费 detetcor.h 的 HandResult）
// =============================================================================

/// ★ 与 C# HandPose 对应。
/// 注意：Center() 必须复刻 C# HandPose.GetCenter() ——【全部关键点的算术平均】。
///      不是 bbox 中心、不是腕点、不是 MCP 均值。
///      分母 = 有效关键点数（实测基线：494 帧全部 = 21）。
struct HandView {
    const HandResult* h = nullptr;

    bool valid() const { return h != nullptr && h->valid(); }

    /// ★ C# HandPose.GetCenter()（HandPoseEstimationModels.cs:108）
    Point center() const {
        if (!h) return Point{0.0f, 0.0f};
        return handCenter(*h);      // detector.h 中的照抄实现
    }

    /// C# HandPose.IsValidGesture(minValidPoints)
    /// 注意：C# 方法默认值是 15，但 SelectHand 两处调用均传 8 —— 此处按 8
    bool isValidGesture(int minValidPoints = kHandMinValidPoints) const {
        return h && h->isValidGesture(minValidPoints);
    }

    /// C# TrackId = (handType == Left ? 0 : 1)
    int trackId() const { return h ? h->trackId : -1; }
    HandType handType() const {
        return (h && h->isLeft) ? HandType::Left : HandType::Right;
    }
    const Rect& box() const { return h->box; }
};

}  // namespace sop
}  // namespace vis