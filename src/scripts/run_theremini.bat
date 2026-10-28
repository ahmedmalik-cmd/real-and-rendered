@echo off
REM ─── Theremini → OSC bridge ──────────────────────────────────────────────
REM Reads MIDI from the Theremini and sends OSC to localhost:9000
REM Auto-restarts on crash. Close this window to stop.

title Theremini Bridge

set "SCRIPT=%~dp0..\python\theremini_bridge.py"

if not exist "%SCRIPT%" (
    echo ERROR: Script not found at %SCRIPT%
    pause
    exit /b 1
)

:loop
echo.
echo [%date% %time%] Starting theremini_bridge.py...
python "%SCRIPT%"
echo [%date% %time%] Exited with code %errorlevel%. Restarting in 5 s...
timeout /t 5 /nobreak
goto loop
