# RK3588 平台 SOP 检测系统 — 移植方案

> 目标：在 RK3588（ARM64 Linux + NPU）上重建一套**SOP 检测系统**，SOP 检测能力 **100% 保留**（条件、违规、配方、Modbus、记录、看板逐条对齐），Windows WPF 版继续保留用于产线 PC。
>
> 编制：2026-10-02　　状态：待评审

---

## 一、目标与硬约束

| 项 | 要求 |
|---|---|
| SOP 检测 | **100% 保留**：12 类条件、8 类违规、稳定帧门控语义、跳步/超时/错序判定全部一致 |
| 配方兼容 | **现有 `configs/sop/*.yaml` 原样可跑**，不重新标定、不改格式 |
| 推理 | 必须用 NPU（6 TOPS），性能达标：SOP 作业 ≥15 FPS，实时检测 ≥25 FPS |
| 联锁 | Modbus 保留（03 读 X1-X8 触发、06 写 Y1-Y8 脉冲/锁存） |
| 可追溯 | 与 Windows 版**逐帧决策对拍一致**（见第五章，这是"100% 保留"的唯一可验证方式） |

---

## 二、现状盘点（Windows 版）

### 2.1 架构分层

```
┌─ WPF UI 层（不可移植，需重写）──────────────────┐
│  MainWindow / SOPModuleView / SOPConfigWindow   │
│  SopWizardWindow / SopDashboardWindow           │
├─ 主控层（部分可移植）───────────────────────────┤
│  MainViewModel：相机帧 → 推理调度 → 叠加渲染    │
│  CameraManager：Web/ONVIF/海康/Mock 四类相机      │
├─ SOP 模块（★ 100% 移植，10.5k 行）─────────────┤
│  Models: SOPWorkflow / SOPYamlConfig(1151)      │
│          SOPStateMachine(497) / RegionConfig    │
│  Services: StepConditionEvaluator(878)          │
│            ViolationDetector(432)               │
│            CrossCameraEvaluator / SopRunRecorder│
│            SOPWorkflowManager / RegionConfigLoader│
│            MediaPipeHandDetector(1362) ← 换后端  │
│            YoloPoseHandEstimationService(514)←换 │
│  SOPModule(1470)：帧处理主循环 + 状态机回填      │
├─ 平台依赖（全部要换）───────────────────────────┤
│  YoloDotNet(CUDA/CPU EP) · ONNX Runtime          │
│  OpenCV DSHOW 相机 · NModbus · SkiaSharp        │
│  YamlDotNet · NAudio · HandyControl(WPF 控件)    │
└─────────────────────────────────────────────────┘
```

### 2.2 SOP 引擎移植面（必须 100% 对齐的清单）

**条件类型 12 类**（`ConditionType`）
`ObjectPresent` `ObjectInZone` `ObjectStable` `ObjectAbsent` `SequenceComplete` `TimeElapsed`
`HandInRegion` `HandNotInRegion` `HandStable` `HandMoveFromTo` `HandNearObject` `Custom`

**违规类型 8 类**（`ViolationType`）
`SkipStep` `WrongOrder` `Timeout` `ForbiddenObject` `ObjectRemoved` `ZoneIntrusion` `MissingRequiredObject` `Custom`

**YAML 配方结构**（顶层节点，键名必须逐字一致）
`sop.{name,version,description,steps,regions,settings,model,monitoring,cross_camera}`
`step.{id,name,timeout,standard_duration_ms,transitions,detection,description,camera,model,violation_rules,forbidden_objects,must_keep,required_objects,modbus_trigger}`

**关键判定语义（踩过坑的，必须原样复刻）**
1. **稳定帧门控**：仅对**当前步骤**生效（跳步探测走原始单帧判定）；跳过 `HandStable`/`ObjectStable`（自管理帧数）、`HandMoveFromTo`（瞬时交互）、`TimeElapsed`/`SequenceComplete`；`Start/Reset` 清零计数。
2. `EvaluateConditions` 是 **AND 短路**；一个步骤 = 一个检测节点。
3. `hand:any` 实际优先右手；区域坐标是绝对像素。
4. `ObjectStable` 的保持/丢失机制（曾经存在的物体被移除 → `ObjectRemoved`）。
5. `ModbusTrigger`：`StepCompleted.HasViolations` → 功能码 06 写 Y 口（latch/pulse_ms）。

---

## 三、RK3588 平台调研结论（2026-10）

| 项 | 结论 |
|---|---|
| NPU | 6 TOPS INT8，3 核；YOLOv8n INT8 单核 **73.5 FPS**（瑞芯微 Model Zoo 实测），YOLOv8s 38 FPS |
| 转换工具链 | **RKNN-Toolkit2 v2.3.2**（仅 x86 Linux；Windows 需 WSL2/Docker），产出 `.rknn` |
| 板端运行时 | **rknn-toolkit-lite2 v2.3.2**（aarch64 wheel）或 **RKNN Runtime C API**（`librknnrt.so`，C++ 产品首选） |
| 手部模型 | MediaPipe 为 TFLite，需拆包 → TFLite/ONNX → RKNN。**风险：landmark 量化后关键点误差可能从 ~3px 劣化到 ~15px**（社区实测），直接影响 `HandStable`/`HandMoveFromTo` 判定 |
| 相机 | USB → V4L2；MIPI CSI → GStreamer/RKISP。无 DSHOW |
| 限制 | RKNN 动态 shape 弱（需固定输入尺寸）；算子不支持时**静默 CPU fallback**（`verbose=True` 看 build 日志）；Modbus 走 `/dev/ttyS*` 或 USB 转串口 |

---

## 四、目标架构（C++17 + 分层解耦）

```
┌──────────────────────────────────────────────────────────┐
│ UI 层：Qt 6 Widgets（面板/配置/看板/标定）                 │
├──────────────────────────────────────────────────────────┤
│ 主控层：EngineHost（帧调度 · 线程池 · 录像 · 缩略图存图）    │
├───────────────────────┬──────────────────────────────────┤
│ ★ SOP 引擎（100% 移植）│ 推理层（可插拔后端）               │
│  yaml_config(yaml-cpp) │  IDetector                       │
│  workflow              │   ├─ YoloRknnBackend  (NPU)      │
│  state_machine         │   ├─ YoloOrtBackend   (CPU NEON) │
│  step_condition        │   └─ HandBackend                 │
│  violation_detector    │      ├─ PalmRknn    (NPU 192²)    │
│  cross_camera          │      └─ LandmarkOrt (CPU 224²)  │
│  run_recorder(jsonl)   │  ← RKNN / ONNX Runtime 可切换     │
│  modbus_io(06 脉冲)     │                                  │
├───────────────────────┴──────────────────────────────────┤
│ 设备层：V4L2 相机 · Modbus RTU/TCP · ALSA 音效 · GPIO/LED   │
└──────────────────────────────────────────────────────────┘
```

**关键设计**：SOP 引擎**只吃结构化输入**（`Detection` / `HandResult` JSON 结构），完全不碰推理与相机。
→ 好处：① 引擎可**离线单测**（喂回放数据，秒级跑完几百帧，不需要板子）；② 换 NPU/CPU 后端不影响引擎；③ 这正是逐帧对拍能落地的基础。

---

## 五、"100% 保留"的保障机制：黄金回放对拍

这是本方案最重要的一环。没有它，"100% 保留"只是口号。

**做法（三步）**：

1. **在 Windows 版加一个旁路录制器**（不改引擎行为）
   - 录制：每帧的原始图像（PNG/JPEG 序列）+ 该帧送入引擎的 `detections` / `hand result` + 引擎输出决策（`StepChanged` / `ViolationDetected` / `StepCompleted` 序列）
   - 产物：`golden/cases/<case_name>/{frames/*.jpg, inputs.jsonl, decisions.jsonl}`
   - 覆盖用例：**现有全部 5 个配方**（喝水/安全帽/手机/…）+ 人工构造的跳步、超时、错序、漏放、违禁物、区域闯入等异常场景

2. **RK3588 版离线对拍工具**（先在 x86 上跑通，再上板）
   - 输入同一份 `inputs.jsonl`（绕过推理与相机），只跑 SOP 引擎
   - 逐帧比对 `decisions.jsonl`：步骤切换时刻、违规类型/描述、通过/失败判定
   - **判定标准：100% 逐条一致**（0 差异）

3. **板端闭环对拍**
   - 板端用同一份帧序列跑完整链路（相机→RKNN 推理→引擎），与 Windows 版决策比对
   - 允许的差异**仅限**推理误差导致的边界抖动（由稳定帧门控吸收）；结构性差异为 0

**为什么这套能落地**：引擎是纯函数式的（帧输入 → 决策输出），无随机、无时间依赖（时间由 `inputs.jsonl` 显式给出），因此可完全确定性复现。

---

## 六、分阶段实施计划

| 阶段 | 内容 | 产出 | 验收标准 | 工期 |
|---|---|---|---|---|
| **P0 基线** | Windows 版加旁路录制器；跑现有配方 + 构造异常场景，产出黄金数据集 | `golden/` 数据集 + 录制器（提交到本仓） | 5 个配方 + ≥8 类异常场景各有完整 `frames/inputs/decisions` | 2 天 |
| **P1 引擎移植** | yaml-cpp 解析 → workflow → state_machine → step_condition → violation_detector → cross_camera → run_recorder。**离线跑，不碰板子** | C++ 引擎 + `sop_replay` 对拍工具 | **与 Windows 版 decisions 逐帧 100% 一致** | 4~5 天 |
| **P2 推理层** | RKNN 转换（YOLOv8n/s + palm/landmark）；`IDetector` 接口 + RKNN/ORT 两后端；精度对拍（同一批图，比框/关键点 IoU） | `.rknn` 模型 + 推理层 + 精度报告 | YOLO mAP 掉点 <1%；手部关键点平均误差 <5px | 4~5 天 |
| **P3 设备与 UI** | V4L2/GStreamer 相机、Modbus(03/06)、Qt 面板（状态栏+步骤时间轴+违规横幅+标定+配置+看板）、音效、违规存图 | 可运行板端程序 | 完整跑通一个配方（含违规触发 + Modbus 脉冲） | 5~6 天 |
| **P4 整场对拍与调优** | 板端端到端对拍；三核 NPU 调度；帧率调优；打包 systemd 服务 | 验收报告 + 部署包 | 逐帧一致；SOP ≥15 FPS、实时 ≥25 FPS；连续跑 8h 无内存泄漏 | 3~4 天 |

**合计约 3.5~4 周**（单人）。P1 结束就能拿到一个"无模型可验证"的正确引擎——**风险最早暴露，后面全是加法**。

### 模型转换补充
- `yolo_models/` 有 18 个 onnx：先转生产实际用的 2~3 个，其余保留 ONNX CPU 兜底（`IDetector` 可运行时选路）。
- RKNN 输入尺寸固定 640×640（与现有 ONNX 一致，不改变几何口径 → 区域坐标/检测框无需换算）。
- 转换在 WSL2 Ubuntu 22.04 容器内做（Windows 原生不支持）。

---

## 七、关键风险与对策

| 风险 | 影响 | 对策 |
|---|---|---|
| **手部 landmark 量化掉点**（3px→15px） | `HandStable`/`HandMoveFromTo` 误判，SOP 行为与 PC 不一致 | landmark 保留 **CPU FP32（ONNX Runtime ARM NEON）**，只把 palm 放 NPU；用 P2 的关键点对拍数据决策；必要时降 `stable_frames` 之外不动任何阈值 |
| RKNN 算子静默 CPU fallback | NPU 提速不及预期 | 转换时 `verbose=True` 抓 fallback 报告；构建脚本内置检查 |
| YOLO 量化掉框 | `ObjectInZone` 边界判定漂移 | 校准集用**生产现场真实帧**（≥200 张）；必要时该模型走 FP16（`quantized_dtype="fp16"`）保精度 |
| 时间源差异 | 超时/节拍判定不同 | 引擎内统一用**单调时钟 + 显式时间戳输入**（对拍模式下由 `inputs.jsonl` 提供），不依赖系统时钟 |
| 帧数据格式坑 | 红蓝互换/绿蓝错位，历史踩过 | 移植铁律：`CameraImageData` 语义统一为 **RGB24**，所有后端显式声明布局并在启动时自检 |
| 板端算力不足 | 帧率不达标 | 三核 NPU 并行（`core_mask`）；`InferenceInterval` 跳帧；必要时 palm/landmark 降分辨率 |
| 调试困难 | 无 VS 断点 | 保留 `--headless --replay <dir>` 离线模式 + 结构化日志（JSONL），出问题可离线复现 |

---

## 八、文件级映射表（C# → C++）

| C#（现工程） | C++（新工程） | 备注 |
|---|---|---|
| `Models/SOPWorkflow.cs` | `sop/workflow.hpp` | 结构体 + 枚举，字段/默认值逐字对齐 |
| `Models/SOPYamlConfig.cs` | `sop/yaml_config.cpp` | yaml-cpp；**snake_case 键名完全一致**；保留忽略未知字段 |
| `Models/SOPStateMachine.cs` | `sop/state_machine.cpp` | 含事件回调（StepChanged/Violation/Completed） |
| `Services/StepConditionEvaluator.cs` | `sop/step_condition.cpp` | ★ 稳定帧门控 + 区域约束语义原样复刻 |
| `Services/ViolationDetector.cs` | `sop/violation_detector.cpp` | 含跳步探测的单帧旁路调用 |
| `Services/CrossCameraEvaluator.cs` | `sop/cross_camera.cpp` | 冷却逻辑一致 |
| `Services/SopRunRecorder.cs` | `sop/run_recorder.cpp` | `sop_records/sop_run_*.jsonl/.csv` 同格式 |
| `Services/SOPWorkflowManager.cs` | `sop/workflow_manager.cpp` | 加载/校验/热重载 |
| `SOPModule.cs` | `sop/engine.cpp` | 帧处理主循环 + 状态机回填 |
| `Services/MediaPipeHandDetector.cs` | `infer/hand_rknn.cpp` + `infer/hand_ort.cpp` | palm→NPU，landmark→CPU |
| `Services/YoloPoseHandEstimationService.cs` | `infer/detector.cpp` | `IDetector` 双后端 |
| `ModbusService`（UI 工程） | `device/modbus_io.cpp` | 03 读 X / 06 写 Y，latch+pulse |
| `WebCameraService` / `OnvifCameraService` | `device/camera_v4l2.cpp` | V4L2 + MJPG 协商（沿用已验证的诊断日志） |
| `SOPModuleView` / `SOPConfigWindow` / `SopDashboardWindow` | `qt/sop_panel.*` / `qt/config_window.*` / `qt/dashboard.*` | 布局与配色沿用现版本（含循环/违规计数、违规横幅） |
| `DeviceBenchmarkService` | `qt/benchmark_window.*` | RKNN/NPU 版本帧率基准 |

---

## 九、性能预算（RK3588，目标）

| 环节 | 预算 | 手段 |
|---|---|---|
| YOLOv8n INT8 推理 | ≤15 ms | NPU 单核 73 FPS；三核可并行多路相机 |
| palm（192²） | ≤8 ms | NPU INT8 |
| landmark（224²） | ≤20 ms | CPU FP32 NEON（保精度优先） |
| 引擎评估 | ≤1 ms | 纯 C++，可忽略 |
| 采集 + 转换 + 渲染 | ≤8 ms | V4L2 MJPG 直采，OpenCV 绘制 |
| **合计** | **SOP ≥15 FPS / 实时 ≥25 FPS** | 帧率不足时开 `InferenceInterval=2` 跳帧 |

---

## 十、决策记录（2026-10-02 已拍板）

| 决策点 | 结论 |
|---|---|
| 技术栈 | **C++17 + Qt6 Widgets**（RKNN Runtime C API，产品化首选） |
| 手部推理 | **全部走 NPU（INT8）**：palm(192²) + landmark(224²) 均转 `.rknn` |
| UI 形态 | **Qt Widgets 面板**，布局/配色/计数/违规横幅沿用现 WPF 版 |
| 首期范围 | **先 1 个配方端到端跑通**（建议选 `sop_cup_usage.yaml` 喝水流程——同时覆盖 YOLO 检测类与 MediaPipe 手部动作类条件，最能压测全链路），验证架构后再铺开 5 个配方 |

### 10.1 因"手部全 NPU"而必须挂的精度闸门

全 NPU 是最快路径（端到端约 15ms），但 landmark INT8 量化存在掉点风险，且会直接改变 `HandStable`/`HandMoveFromTo` 的判定。因此 P2 阶段设**硬性验收闸门**，不达标即切混合方案（架构已预留，`IDetector` 运行时可切）：

| 闸门 | 指标 | 不达标处置 |
|---|---|---|
| G1 转换健康度 | `rknn.build(verbose)` 无 CPU fallback 告警 | 改图结构 / 拆分模型 / 退回 CPU 算子 |
| G2 检测框对拍 | 同批生产帧，RKNN vs Windows ONNX，框 IoU ≥0.9 比例 ≥95% | 校准集换现场真图；仍不达标则该模型走 FP16 |
| G3 关键点对拍 | palm box 偏差 <2%；**landmark 21 点平均误差 <5px** | **切混合方案：palm=NPU + landmark=CPU FP32** |
| G4 行为对拍 | 同帧序列下，5 类手部条件的判定与 Windows 版逐帧一致 | 调整区域/阈值（记入配方差异表，不改代码） |

> 说明：`stable_frames` 与区域坐标**不动**来迁就量化误差——一旦为板子单独改阈值，"100% 保留"就名存实亡。阈值只允许因 G4 判定差异而**记录在案**。

---

## 十一、开工顺序（按决策落地）

```
P0 基线录制  →  P1 引擎移植+离线对拍(100%一致)  →  P2 RKNN推理+G1~G4闸门
   (2天)              (4~5天, 纯C++不碰板子)              (4~5天)
        →  P3 Qt面板+V4L2+Modbus, 单配方端到端  →  P4 整场对拍+三核调优+打包
              (5~6天)                                (3~4天)
```

建议现在只启动 **P0**（在 Windows 版加旁路录制器 + 产出黄金数据集）——这一步不动板子、不改引擎行为，且是后续所有"一致性验证"的唯一依据。P0 完成后再进 P1。

