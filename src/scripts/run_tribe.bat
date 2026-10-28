@echo off
REM ─── TRIBE v2 perception pipeline ────────────────────────────────────────
REM Captures Kinect color clips, sends to TRIBE server (via ngrok),
REM feeds into LLaVA with memory + presence + strange mode,
REM serves perception JSON at localhost:8183.
REM Auto-restarts on crash. Close this window to stop.
REM
REM IMPORTANT: set TRIBE_URL / CLIP_URL to the second PC before starting
REM (see ..\config.example.env). An ngrok URL changes every session on the
REM free plan, so update TRIBE_URL before the week-long run.

title TRIBE Perception Pipeline

set "SCRIPT=%~dp0..\python\tribe_kinect_client.py"

if not exist "%SCRIPT%" (
    echo ERROR: Script not found at %SCRIPT%
    pause
    exit /b 1
)

:loop
echo.
echo [%date% %time%] Starting tribe_kinect_client.py...
python "%SCRIPT%"
echo [%date% %time%] Exited with code %errorlevel%. Restarting in 10 s...
timeout /t 10 /nobreak
goto loop
