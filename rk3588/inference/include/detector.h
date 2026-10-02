// =============================================================================
//  detector.h  —  推理层与 SOP 引擎之间的【唯一契约】
// =============================================================================
//  设计铁律（务必保持）：
//   1. 本头文件只含【纯数据结构】，不 include 任何 RKNN / OpenCV / Qt / 相机头文件。
//      → engine/ 只依赖本文件，因此 SOP 引擎可离线单测、可逐帧回放对拍。
//   2. 字段命名与 C# 版一一对应（见 rk3588/docs/移植台账.md 映射表），
//      迁移时按此结构逐字段比对，不允许"顺手优化"。
//   3. HandResult 暴露的是【语义槽位】，不是模型原始关键点序号。
//      MediaPipe 21 点与 YOLOv8-pose 手部点的序号定义完全不同，
//      映射在 inference/hand/ 内完成，引擎侧只认 HandSlot 枚举。
// =============================================================================
#pragma once

#include <array>
#include <cstdint>
#include <string>
#include <vector>

namespace vis {

// =============================================================================
//  1. 基础几何
// =============================================================================
struct Point {
    float x = 0.0f;   // 像素坐标，原点左上，x 向右
    float y = 0.0f;   // 像素坐标，原点左上，y 向下
};

struct Rect {
    float x = 0.0f;
    float y = 0.0f;
    float w = 0.0f;
    float h = 0.0f;

    float centerX() const { return x + w * 0.5f; }
    float centerY() const { return y + h * 0.5f; }
    float area() const { return w * h; }
    bool valid() const { return w > 1.0f && h > 1.0f; }
};

// =============================================================================
//  2. 目标检测结果（YOLO 输出）
// =============================================================================
struct Detection {
    Rect box;
    int   classId   = -1;      // 类别索引，对应配方 model.classes 顺序
    float score     = 0.0f;    // 置信度 [0,1]
    int   trackId   = -1;      // 跨帧跟踪 ID，-1 表示未跟踪
    std::string className;     // 冗余人类可读名，仅用于日志/UI，引擎不依赖

    bool valid() const { return box.valid() && score > 0.0f; }
};

// =============================================================================
//  3. 手部关键点 —— 语义槽位化
// =============================================================================
//  【勘察结论 2026-10-03，经三轮源码勘察】
//  Windows 版真实链路（类名 YoloPoseHandEstimationService 有误导性）：
//     ① YOLOv8-pose 检测【人体】，取手腕定位 → 裁剪 224×224
//     ② MediaPipe Landmark（hand_landmark_sparse_Nx3x224x224.onnx）在裁剪图上出 21 点
//     ③ 反变换：x/224 → ×cropRect.W/H → +cropRect.Left/Top → 原图坐标
//     ④ HandPose.Keypoints ← 21 个 HandKeypoint，置信度【硬编码 0.9f】
//  即 MediaPipe 确实在主路径上；21 点序号与 MediaPipe 标准一致。
//
//  【为何仍要语义槽位】RKNN 侧换模型后 kpt 顺序由训练 label 决定，
//  显式对照表把改动收敛到 hand_kpt_map.cpp 一个文件，避免静默判错。
// -----------------------------------------------------------------------------
//  C# HandKeypointType 完整定义（21 项，序号 = MediaPipe 标准）：
//    0 Wrist | 1 ThumbCMC 2 ThumbMCP 3 ThumbIP 4 ThumbTip
//    5 IndexFingerMCP 6 IndexFingerPIP 7 IndexFingerDIP 8 IndexFingerTip
//    9 MiddleFingerMCP 10 ...PIP 11 ...DIP 12 ...Tip
//   13 RingFingerMCP 14 ...PIP 15 ...DIP 16 ...Tip
//   17 PinkyMCP 18 PinkyPIP 19 PinkyDIP 20 PinkyTip
//
//  ⚠️ C++ 侧【必须输出 21 点】。原因见 handCenter()：分母 = 实际点数，
//     点数变化会让中心物理位置系统性偏移 → HandInRegion/HandStable 判定改变。
//
//  ⚠️ COCO Pose 17 点人体模型【不适用】于本项目：其腕为 9(LWrist)/10(RWrist)，
//     15/16 是踝。YOLOv8-pose 在此链路中只用于【找手腕位置】，
//     不直接提供 21 点手部关键点。
// -----------------------------------------------------------------------------
enum class HandSlot : int {
    kWrist      = 0,   // 腕关节（所有 HandInRegion / 稳定判定基准）
    kThumbTip   = 1,   // 拇指尖
    kIndexTip   = 2,   // 食指尖
    kMiddleTip  = 3,   // 中指尖
    kRingTip    = 4,   // 无名指尖
    kPinkyTip   = 5,   // 小指尖
    kIndexMcp   = 6,   // 食指第二关节（HandMoveFromTo 起止判定常用）
    kPalmCenter = 7,   // 掌心（由框中心或关键点均值导出，HandNearObject 用）
    kThumbIp    = 8,   // 拇指第二关节
    kPinkyMcp   = 9,   // 小指第二关节（手掌张开度判定）
    kCount      = 10
};

inline constexpr int kHandSlotCount = static_cast<int>(HandSlot::kCount);

// 关键点置信度低于此值视为无效（与 C# 版 HandKeypoint.IsValid 对齐，
// 具体数值见 rk3588/docs/移植台账.md「阈值对齐表」）
inline constexpr float kHandPointMinScore = 0.5f;

// ★ 与 C# HandPose.IsValidGesture(default) 对齐
inline constexpr int   kHandMinValidPoints = 8;

// =============================================================================
//  ★★★ GetCenter() —— 移植时最容易错的一处，务必照抄
// =============================================================================
//  C# 原码（HandPoseEstimationModels.cs:108）：
//      if (Keypoints.Count == 0) return new SKPoint(0, 0);
//      float avgX = Keypoints.Average(k => k.X);
//      float avgY = Keypoints.Average(k => k.Y);
//  即【全部关键点的算术平均】，不是 bbox 中心、不是腕点、不是 palm center(MCP均值)。
//
//  ⚠️ 关键点置信度在 C# 侧是【硬编码 0.9f】，列表内恒定，
//     故"过滤低分点"与"不过滤"在此等价 —— 但仍须保证【有效点数一致】。
//
//  ⚠️ 分母 = 实际点数。C# 侧循环是 `i < landmarks.Count && i < 21`，
//     即实际可能少于 21 点。RKNN 侧【必须输出与 Windows 版实测一致的点数】，
//     否则中心物理位置系统性偏移 → HandInRegion/HandStable 判定改变（G4 必挂）。
//
//  ⚠️ 语义对比：
//     手部判定 HandInRegion  → 【中心点包含】(zoneRect.Contains(center))
//     物体判定 IsInZone      → 【矩形相交】(box.Intersects(zone))
//     两者语义不同，不可混用。
//
//  ⚠️ 本函数 ≠ kPalmCenter（后者是 5 指 MCP 均值，当前 C# 判定链路并未使用）。
inline Point handCenter(const HandResult& h) {
    float sx = 0.0f, sy = 0.0f;
    int n = 0;
    for (int i = 0; i < 21; ++i) {
        if (h.scores[i] >= kHandPointMinScore) {   // 仅统计有效点
            sx += h.points[i].x;
            sy += h.points[i].y;
            ++n;
        }
    }
    if (n == 0) return Point{0.0f, 0.0f};          // 与 C# 的 (0,0) 一致
    return Point{sx / n, sy / n};
}

struct HandResult {
    // 21 个点的原始缓冲（保留原始顺序，便于对拍时逐点比对 Windows 版输出）
    std::array<Point, 21> points{};
    std::array<float,  21> scores{};

    // ★ 实际有效关键点个数。GetCenter() 的分母就是它，
    //   须与 Windows 版实测一致，否则中心偏移导致 G4 对拍失败。
    int   validPointCount = 0;

    // ★ 语义槽位视图：条件判定用这个，不碰上面的 points
    std::array<Point, kHandSlotCount> slot{};
    std::array<bool,  kHandSlotCount> slotValid{};

    Rect  box;                 // 手部外框
    bool  isLeft  = false;     // 左右手（HandType.Left=1 / Right=2）
    float score   = 0.0f;      // 手部整体置信度
    int   trackId = -1;        // C# 侧固定：Left→0, Right→1

    bool valid() const { return box.valid() && score > 0.0f; }

    // 对齐 C# HandPose.IsValidGesture（SelectHand 两处调用均传 minValid=8，
    // 非方法默认值 15）
    bool isValidGesture(int minValid = kHandMinValidPoints) const {
        return validPointCount >= minValid;
    }

    // 取语义槽位坐标；槽位无效时返回 fallback
    Point slotPoint(HandSlot s) const {
        int i = static_cast<int>(s);
        if (i < 0 || i >= kHandSlotCount || !slotValid[i]) {
            return Point{box.centerX(), box.centerY()};
        }
        return slot[i];
    }

    // 槽位是否有效（条件判定用，避免用 fallback 点误判）
    bool hasSlot(HandSlot s) const {
        int i = static_cast<int>(s);
        return i >= 0 && i < kHandSlotCount && slotValid[i];
    }
};

// =============================================================================
//  4. 单帧推理输出（引擎的输入）
// =============================================================================
struct FrameDetections {
    std::vector<Detection> objects;
    std::vector<HandResult> hands;      // 顺序无要求，引擎内部按 trackId 跟踪
    uint64_t frameIndex  = 0;
    int64_t  timestampMs = 0;           // 单调递增，跨相机对齐用
    int      width = 0, height = 0;

    // 查询辅助（引擎用）
    const Detection* findByClass(int classId) const {
        for (const auto& d : objects) if (d.classId == classId) return &d;
        return nullptr;
    }
    std::vector<Detection> findAllByClass(int classId) const {
        std::vector<Detection> r;
        for (const auto& d : objects) if (d.classId == classId) r.push_back(d);
        return r;
    }
};

// =============================================================================
//  5. 检测器接口（推理层可插拔）
// =============================================================================
//  板端实现：YoloRknnBackend（YOLO NPU）/ HandYoloPoseBackend（手部 YOLOv8-pose NPU）
//  PC 对拍实现：OnnxOrtBackend（复刻 Windows 版行为，用于生成基准）
// =============================================================================
class IDetector {
public:
    virtual ~IDetector() = default;

    virtual bool init(const std::string& modelDir) = 0;
    virtual void release() = 0;

    // 单帧推理。frame 为 RGB24 或 NV12，由 backend 自行识别
    virtual bool infer(const void* frame, int width, int height, int stride,
                       FrameDetections* out) = 0;

    virtual const char* backendName() const = 0;
};

}  // namespace vis
