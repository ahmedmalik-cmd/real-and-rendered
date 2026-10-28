@echo off
REM ─── Launch the full installation ────────────────────────────────────────
REM Opens each component in its own window with auto-restart.
REM Components start in dependency order with short delays so each can boot.
REM
REM To stop everything: close each opened window, or just shut down the PC.

title Launch All

set "HERE=%~dp0"

echo Launching installation components in dependency order...
echo.

echo [1/5] Kinect bridge (needs to be first — others poll it)
start "Kinect Bridge"  cmd /k "%HERE%run_kinect_bridge.bat"
timeout /t 4 /nobreak >nul

echo [2/5] Ollama LLM server (may already be a Windows service)
start "Ollama"         cmd /k "%HERE%run_ollama.bat"
timeout /t 6 /nobreak >nul

echo [3/5] Theremini → OSC bridge
start "Theremini"      cmd /k "%HERE%run_theremini.bat"
timeout /t 2 /nobreak >nul

echo [4/5] TRIBE v2 perception pipeline (..\python)
start "TRIBE"          cmd /k "%HERE%run_tribe.bat"
timeout /t 3 /nobreak >nul

echo [5/5] Unity build (point cloud, projectors)
start "Unity Build"    cmd /k "%HERE%run_unity_build.bat"

echo.
echo All components launched. Each is in its own window and will auto-restart on crash.
echo Close any window to stop that component. Close this window to dismiss the launcher.
echo.
pause
