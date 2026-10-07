@echo off
REM ============================================================================
REM  build_replay.bat - compile the golden-replay comparison tool (P1)
REM ============================================================================
REM  Usage: scripts\build_replay.bat
REM  Output: build\replay_main.exe
REM  Deps: MSVC only (no yaml-cpp / RKNN / third-party libs)
REM ============================================================================
setlocal

set "VCVARS=E:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
if not exist "%VCVARS%" (
    echo [ERROR] vcvars64.bat not found
    echo         Run vswhere to get the real path, then edit this script.
    exit /b 1
)

call "%VCVARS%" >nul
if errorlevel 1 (
    echo [ERROR] vcvars64.bat failed
    exit /b 1
)

cd /d "%~dp0.."
if not exist build mkdir build

echo [BUILD] compiling replay_main...
cl /nologo /std:c++17 /O2 /EHsc /W3 /utf-8 ^
   /I engine\include ^
   /I inference\include ^
   engine\src\types.cpp ^
   engine\src\yaml_config.cpp ^
   engine\src\step_condition.cpp ^
   engine\src\state_machine.cpp ^
   tools\replay\replay_main.cpp ^
   /Fe:build\replay_main.exe ^
   /Fo:build\obj\

if errorlevel 1 (
    echo [ERROR] build failed
    exit /b 1
)

echo [OK] build\replay_main.exe
endlocal