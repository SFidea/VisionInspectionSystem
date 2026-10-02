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
//  【勘察结论 2026-10-03】Windows 版 SOP 手部判定主路径是
//  YoloPoseHandEstimationService（YOLO COCO Pose），不是 MediaPipe。
//  C# 侧 HandPose 用【枚举型 HandKeypointType】，本就不依赖 21 点序号。
//  保留语义槽位的原因：换 RKNN 模型时模型 kpt 顺序可能变，
//  显式对照表能把改动收敛到 hand_kpt_map.cpp 一个文件，避免静默判错。
// -----------------------------------------------------------------------------
//  COCO Pose 17 点标准索引（若沿用官方人体 pose 模型）：
//    0 Nose  1 LEye 2 REye 3 LEar 4 REar
//    5 LShoulder 6 RShoulder 7 LElbow 8 RElbow
//    9 LWrist  10 RWrist  11 LHip  12 RHip
//    13 LKnee 14 RKnee  15 LAnkle 16 RAnkle
//  ⚠️ 注意：COCO 的腕是 9/10（不是 15/16，15/16 是踝）。
//     映射文档里若出现 15/16 需按实际模型 metadata 复核。
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

// 关键点置信度低于此值视为无效（与 C# 版 HandLandmark visibility 阈值对齐，
// 具体数值见 rk3588/docs/移植台账.md「阈值对齐表」）
inline constexpr float kHandPointMinScore = 0.5f;

struct HandResult {
    // 21 个点的原始缓冲（保留原始顺序，便于对拍时逐点比对 Windows 版输出）
    std::array<Point, 21> points{};
    std::array<float,  21> scores{};

    // ★ 语义槽位视图：引擎【只用】这个，不碰上面的 points
    std::array<Point, kHandSlotCount> slot{};
    std::array<bool,  kHandSlotCount> slotValid{};

    Rect  box;                 // 手部外框
    bool  isLeft  = false;     // 左右手（推断自几何，无法用颜色区分）
    float score   = 0.0f;      // 手部整体置信度
    int   trackId = -1;

    bool valid() const { return box.valid() && score > 0.0f; }

    // 取语义槽位坐标；槽位无效时返回 fallback（默认取框中心）
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
