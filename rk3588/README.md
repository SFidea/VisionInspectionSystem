# rk3588 — RK3588 SOP 检测系统

Windows WPF 版 SOP-AI 作业监控系统的 **RK3588 平台移植工程**。
与 `src/`（Windows 版）**物理隔离**，互不干扰。

> 方案全文：[`../docs/RK3588_SOP部署方案.md`](../docs/RK3588_SOP部署方案.md)
> 移植进度与差异：[`docs/移植台账.md`](docs/移植台账.md)

---

## 硬约束（改代码前必读）

| # | 铁律 |
|---|---|
| 1 | **SOP 检测 100% 保留** —— 12 类条件、8 类违规、稳定帧门控语义、跳步/超时/错序判定全部与 Windows 版一致 |
| 2 | **`engine/` 不 include 任何 rknn / Qt / V4L2 / OpenCV 头文件**，只依赖 `inference/include/detector.h` 的纯数据结构（CMake 已用 include 隔离强制） |
| 3 | **`stable_frames` 与区域容差一律不改**来迁就板端精度。出现行为差异 → 先修推理层映射 → 再换 FP16 → 最后记入《配方差异表》由 Sir 裁决 |
| 4 | **三核 NPU 必须手动绑 `core_mask`，禁用 `auto`**（抢占调度会让帧间隔抖动，直接改写 `timeout`/`stable_frames` 判定） |
| 5 | **`configs/sop/*.yaml` 从 Windows 版原样拷贝**，不改任何格式；解析器必须容忍未知字段（YamlDotNet 旧坑） |
| 6 | **只引入 Apache-2.0 / BSD-3 / MIT 代码**。AGPL（`ultralytics_yolov8`）与无许可证项目一律只读不抄 |
| 7 | **每次对拍结果落档** `docs/移植台账.md`，不只在聊天里说 |

---

## 目录结构

```
rk3588/
├─ CMakeLists.txt          顶层构建（Native / RK3588 双模式）
├─ engine/                 ★ SOP 引擎（100% 移植，检测器无关）
│  ├─ include/sop/         types / state_machine / evaluator / violation / yaml
│  ├─ src/                 实现
│  └─ tests/               离线单测 + 回放对拍
├─ inference/              推理层（可插拔 IDetector 后端）
│  ├─ include/detector.h   ★ 引擎与推理层的唯一契约
│  ├─ rknn/                RKNN 封装 + 三核 worker 池
│  ├─ yolo/                YOLOv8 DFL 解码 + NMS
│  └─ hand/                ★ 手部 YOLOv8-pose + 语义映射（替代 MediaPipe）
├─ device/                 V4L2 相机 / Modbus / ALSA / RGA
├─ ui/                     Qt 6 Widgets（main_window / config / wizard / dashboard）
├─ tools/                  rknn_convert（PC 侧）/ replay（对拍）/ bench（基准）
├─ configs/sop/            配方（原样拷贝自 Windows 版）
├─ models/                 .rknn 模型（不入 git）
└─ docs/                   移植台账 + 专项文档
```

---

## 构建

### PC 侧（开发 / 离线对拍，不需要板子）
```bash
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build -j
ctest --test-dir build --output-on-failure
```
此模式不依赖 RKNN/Qt，只编译 `engine` + `tools`，用于 P0/P1 的**离线对拍**。

### 板端（交叉编译 aarch64）
```bash
# 依赖
sudo apt install -y cmake g++ libyaml-cpp-dev libasound2-dev qt6-base-dev

# RKNN Runtime（随 rknn-toolkit-lite2 分发的 aarch64 whl）
pip install rknn-toolkit-lite2 --no-cache-dir

cmake -S . -B build-arm \
      -DCMAKE_TOOLCHAIN_FILE=scripts/aarch64-linux-gnu.toolchain.cmake \
      -DENABLE_RKNN=ON -DBUILD_UI=ON
cmake --build build-arm -j
```

### 部署到板子
```bash
scp -r build-arm/bin/sop_app root@<板子IP>:/opt/sop/
# 附带 configs/sop/*.yaml 与 models/*.rknn
```

---

## 实施顺序

```
P0 基线录制(Windows侧)  →  P1 引擎移植+离线对拍  →  P2 手部YOLOv8-pose接入
     2天                        4~5天                    5~6天
   →  P3 Qt面板+V4L2+Modbus  →  P4 整场对拍+三核调优+打包
          5~6天                    3~4天
```

**当前状态：全部未开始。** 等 Sir 说"开始"后启动 P0。

---

## 相关文档

| 文档 | 内容 |
|---|---|
| [`../docs/RK3588_SOP部署方案.md`](../docs/RK3588_SOP部署方案.md) | 方案全文（架构 / 阶段 / 风险 / 三核 NPU / 开源借鉴） |
| [`docs/手部关键点映射与区域换算.md`](docs/手部关键点映射与区域换算.md) | ★ 路线 B 关键点语义映射与区域换算 |
| [`docs/移植台账.md`](docs/移植台账.md) | 逐文件移植进度、阈值对齐表、差异记录 |
