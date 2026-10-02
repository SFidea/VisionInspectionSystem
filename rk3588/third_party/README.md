# third_party — 第三方代码引入规范

> 许可证红线（见 `../../docs/RK3588_SOP部署方案.md` 13.2 / 13.4）

## 允许引入

| 许可证 | 说明 |
|---|---|
| Apache-2.0 | 需保留 NOTICE 与版权声明 |
| BSD-3 / BSD-2 | 需保留版权声明与免责声明 |
| MIT | 需保留版权声明 |
| RKNN Runtime | 随 rknn-toolkit-lite2 分发，遵循瑞芯微 EULA |

## 禁止引入

| 项目 | 原因 |
|---|---|
| `airockchip/ultralytics_yolov8` | **AGPL-3.0** —— 传染性协议，闭源商用必须避开。可读不可抄 |
| Gitee `pomimi/monitoring-system` | **无许可证** —— 法律上默认保留所有权利，不可抄码。只能看架构思路 |
| `rockchip-linux/mpp` | 无明确许可证，仅在确需 H.264 硬解时按瑞芯微官方渠道获取 |
| 任何来源不明的 StackOverflow / 博客代码片段 | 需自行重写并注明思路来源 |

## 可抄码清单（已核实许可证）

| 仓库 | 许可证 | 引入位置 |
|---|---|---|
| `alebal123bal/khadas_yolov8n_multithread` | Apache-2.0 | `inference/rknn/`（三核 worker 池参考）、`device/rga/`、`device/camera_v4l2/` |
| `cqu20160901/yolov8n_rknn_Cplusplus_dfl` | 无（该仓库未声明）⚠️ | `inference/yolo/yolo_decode.cpp` —— **需重写而非抄**，只借鉴 DFL 解码算法思路（算法本身是 YOLOv8 论文公开内容） |
| `yingliuzhizhuzed/RK3588_RKNN_MPP_RGA_demo` | 无 ⚠️ | 同上，**只借鉴架构**（Qt+MPP+RGA+DMA buffer 分层） |
| `airockchip/rknn_model_zoo` | Apache-2.0 | `inference/rknn/`、`tools/rknn_convert/` —— 可直接抄 |
| `airockchip/rknn-toolkit2` | 瑞芯微 EULA | 转换工具链（不进仓库） |

## 引入流程

1. 确认许可证允许 → 记入本文件
2. `git submodule add` 或拷贝到 `third_party/<name>/`
3. 保留原始 LICENSE / NOTICE 文件
4. 在 `THIRD_PARTY.md` 记录来源 URL + commit hash + 许可证
