#!/bin/bash
# ============================================================================
#  build_replay.sh — Linux/macOS/Git-Bash 下编译回放对拍程序
# ============================================================================
#  Windows 下请用 scripts/build_replay.bat（需先跑 vcvars64.bat）
# ============================================================================
set -e

cd "$(dirname "$0")/.."
mkdir -p build/obj

echo "[BUILD] compiling replay_main..."
g++ -std=c++17 -O2 -Wall -Wextra -o build/replay_main \
    -I engine/include \
    -I inference/include \
    engine/src/types.cpp \
    engine/src/yaml_config.cpp \
    engine/src/step_condition.cpp \
    engine/src/violation_detector.cpp \
    engine/src/state_machine.cpp \
    tools/replay/replay_main.cpp

echo "[OK] build/replay_main"