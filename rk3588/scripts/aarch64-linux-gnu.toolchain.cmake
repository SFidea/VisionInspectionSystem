# =============================================================================
#  aarch64-linux-gnu 交叉编译工具链（RK3588）
# =============================================================================
#  用法：
#    cmake -S . -B build-arm \
#          -DCMAKE_TOOLCHAIN_FILE=scripts/aarch64-linux-gnu.toolchain.cmake \
#          -DENABLE_RKNN=ON -DBUILD_UI=ON
#
#  前置：安装交叉编译工具链
#    sudo apt install -y gcc-aarch64-linux-gnu g++-aarch64-linux-gnu
#
#  ⚠️ Qt6 若用 apt 安装，交叉编译 Qt 需额外配置 host triple；
#     实践上更推荐在板子上装 Qt6 开发包后本机构建。
# =============================================================================

set(CMAKE_SYSTEM_NAME Linux)
set(CMAKE_SYSTEM_PROCESSOR aarch64)

set(TOOLCHAIN_PREFIX aarch64-linux-gnu-)

set(CMAKE_C_COMPILER   ${TOOLCHAIN_PREFIX}gcc)
set(CMAKE_CXX_COMPILER ${TOOLCHAIN_PREFIX}g++)
set(CMAKE_AR           ${TOOLCHAIN_PREFIX}ar)
set(CMAKE_RANLIB       ${TOOLCHAIN_PREFIX}ranlib)
set(CMAKE_STRIP        ${TOOLCHAIN_PREFIX}strip)

set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_PACKAGE ONLY)

# RK3588 建议开 NEON / SIMD
set(CMAKE_C_FLAGS   "-march=armv8.2-a+crypto+fp16+dotprod")
set(CMAKE_CXX_FLAGS "-march=armv8.2-a+crypto+fp16+dotprod")
