# =============================================================================
#  RK3588 SOP 检测系统 — 顶层构建
# =============================================================================
#  技术栈：C++17 + Qt6 Widgets + RKNN Runtime C API
#  目标板：RK3588（aarch64-linux-gnu）
#  方案全文：docs/RK3588_SOP部署方案.md
# =============================================================================
cmake_minimum_required(VERSION 3.16)
project(rk3588_sop LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# -----------------------------------------------------------------------------
#  构建模式
#   Native  : PC 上开发/单测/离线对拍，不依赖 RKNN（engine + tests 可单独编译）
#   RK3588   : 交叉编译完整板端程序，链接 librknnrt.so / Qt6 / V4L2
# -----------------------------------------------------------------------------
option(BUILD_UI      "构建 Qt6 界面"            OFF)
option(BUILD_TESTS   "构建离线单测与回放对拍"    ON)
option(ENABLE_RKNN   "启用 RKNN NPU 推理"       OFF)
option(ENABLE_RGA    "启用 RGA 硬件预处理"      OFF)

if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release)
endif()

set(CMAKE_RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin)
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/lib)
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/lib)

# =============================================================================
#  engine/ — SOP 引擎（★ 100% 移植，检测器无关）
# =============================================================================
#  硬约束：本目录【不得】include 任何 rknn / Qt / V4L2 / OpenCV 头文件，
#          只允许依赖 inference/include/detector.h 的纯数据结构。
#          CMake 层用 include 隔离强制约束——违反会直接编译失败。
# =============================================================================
add_library(sop_engine STATIC
    engine/src/yaml_config.cpp
    engine/src/workflow.cpp
    engine/src/state_machine.cpp
    engine/src/step_condition.cpp
    engine/src/violation_detector.cpp
    engine/src/cross_camera.cpp
    engine/src/run_recorder.cpp
    engine/src/modbus_io.cpp
)

target_include_directories(sop_engine PUBLIC
    ${CMAKE_CURRENT_SOURCE_DIR}/engine/include
    ${CMAKE_CURRENT_SOURCE_DIR}/inference/include     # 唯一外部依赖：数据结构
)

find_package(Threads REQUIRED)
target_link_libraries(sop_engine PUBLIC Threads::Threads)

# yaml-cpp：解析 configs/sop/*.yaml（键名必须与 Windows 版逐字一致）
find_package(yaml-cpp QUIET)
if(yaml-cpp_FOUND)
    target_link_libraries(sop_engine PUBLIC yaml-cpp)
    message(STATUS "[sop] yaml-cpp: found")
else()
    message(WARNING "[sop] yaml-cpp 未找到 —— 配方加载将不可用（需安装 libyaml-cpp-dev）")
endif()

# =============================================================================
#  inference/ — 推理层（可插拔后端）
# =============================================================================
if(ENABLE_RKNN)
    add_library(sop_inference STATIC
        inference/rknn/rknn_wrapper.cpp
        inference/rknn/npu_worker_pool.cpp     # ★ 三核 worker 池，见方案第十章
        inference/yolo/yolo_decode.cpp         # YOLOv8 DFL 头解码（参考 cqu20160901）
        inference/yolo/nms.cpp
        inference/yolo/yolo_rknn_backend.cpp
        inference/hand/hand_kpt_map.cpp         # ★ 手部语义映射（替代 MediaPipe）
        inference/hand/hand_yolopose_backend.cpp
        inference/hand/hand_tracker.cpp
    )
    target_include_directories(sop_inference PUBLIC
        ${CMAKE_CURRENT_SOURCE_DIR}/inference/include
    )
    target_link_libraries(sop_inference PUBLIC sop_engine)

    # librknnrt.so：随 rknn-toolkit-lite2 分发
    find_library(RKNNRT_LIB NAMES rknnrt HINTS
        /usr/lib /usr/local/lib ${CMAKE_CURRENT_SOURCE_DIR}/third_party/rknn/lib)
    if(RKNNRT_LIB)
        target_link_libraries(sop_inference PUBLIC ${RKNNRT_LIB})
        message(STATUS "[sop] librknnrt: ${RKNNRT_LIB}")
    else()
        message(FATAL_ERROR "[sop] 未找到 librknnrt.so —— 请安装 rknn-toolkit-lite2 或指定 RKNNRT 路径")
    endif()
else()
    message(STATUS "[sop] ENABLE_RKNN=OFF —— 仅构建引擎（PC 离线对拍模式）")
endif()

# =============================================================================
#  device/ — 设备层
# =============================================================================
add_library(sop_device STATIC
    device/camera_v4l2/v4l2_camera.cpp
    device/modbus/modbus_io_linux.cpp
    device/audio/alsa_player.cpp
)
target_include_directories(sop_device PUBLIC
    ${CMAKE_CURRENT_SOURCE_DIR}/device
)
target_link_libraries(sop_device PUBLIC sop_engine)
find_package(ALSA QUIET)
if(ALSA_FOUND)
    target_link_libraries(sop_device PUBLIC alsa)
endif()

# =============================================================================
#  tools/ — 回放对拍与基准
# =============================================================================
if(BUILD_TESTS)
    enable_testing()

    # 离线回放对拍：喂 golden 数据集的 decisions.jsonl，逐帧比对判定结果
    add_executable(replay_compare tools/replay/replay_compare.cpp)
    target_link_libraries(replay_compare PRIVATE sop_engine)
    add_test(NAME replay_compare COMMAND replay_compare)

    # 引擎单元测试
    add_executable(engine_tests engine/tests/test_main.cpp)
    target_link_libraries(engine_tests PRIVATE sop_engine)
    add_test(NAME engine_tests COMMAND engine_tests)

    if(ENABLE_RKNN)
        add_executable(rknn_bench tools/bench/rknn_bench.cpp)
        target_link_libraries(rknn_bench PRIVATE sop_inference)
    endif()
endif()

# =============================================================================
#  ui/ — Qt 6 Widgets
# =============================================================================
if(BUILD_UI)
    set(CMAKE_AUTOMOC ON)
    find_package(Qt6 REQUIRED COMPONENTS Widgets Charts)
    add_executable(sop_app
        ui/main_window/main_window.cpp
        ui/config/config_window.cpp
        ui/wizard/wizard_window.cpp
        ui/dashboard/dashboard_window.cpp
    )
    target_link_libraries(sop_app PRIVATE
        Qt6::Widgets Qt6::Charts sop_engine sop_device)
    if(ENABLE_RKNN)
        target_link_libraries(sop_app PRIVATE sop_inference)
    endif()
endif()

message(STATUS "==================================================")
message(STATUS " rk3588_sop 配置: UI=${BUILD_UI} RKNN=${ENABLE_RKNN} TESTS=${BUILD_TESTS}")
message(STATUS "==================================================")
