@echo off
REM ─── Unity build (point cloud renderer, projectors, HUD) ─────────────────
REM Auto-restarts on crash. Close this window to stop.
REM
REM Set BUILD_EXE (env var) to the .exe in your build output folder.

title Unity Build

if not defined BUILD_EXE set "BUILD_EXE=%~dp0..\..\Builds\DualKinectPointCloud.exe"

if not exist "%BUILD_EXE%" (
    echo ERROR: Build not found at %BUILD_EXE%
    echo.
    echo Edit BUILD_EXE in this batch file to point at the .exe Unity created.
    echo Typical: ^<project^>\Build\DualKinectPointCloud.exe
    pause
    exit /b 1
)

:loop
echo.
echo [%date% %time%] Launching Unity build...
"%BUILD_EXE%"
echo [%date% %time%] Build exited with code %errorlevel%. Restarting in 10 s...
timeout /t 10 /nobreak
goto loop
