@echo off
REM ─── Kinect bridge (depth + color HTTP at localhost:8182) ────────────────
REM Auto-restarts on crash. Close this window to stop.

title Kinect Bridge

REM Set BRIDGE_EXE (env var) to the built ThereminKinectBridge.exe.
if not defined BRIDGE_EXE set "BRIDGE_EXE=%~dp0..\kinect-bridge\bin\Release\ThereminKinectBridge.exe"

if not exist "%BRIDGE_EXE%" (
    echo ERROR: Bridge exe not found at %BRIDGE_EXE%
    echo Edit BRIDGE_EXE in this batch file to point at the right location.
    pause
    exit /b 1
)

:loop
echo.
echo [%date% %time%] Starting Kinect bridge...
"%BRIDGE_EXE%"
echo [%date% %time%] Bridge exited with code %errorlevel%. Restarting in 5 s...
timeout /t 5 /nobreak
goto loop
