@echo off
REM ─── Ollama LLM server (LLaVA inference, port 11434) ─────────────────────
REM Auto-restarts on crash. Close this window to stop.
REM Note: Ollama installer also creates a Windows service that may already
REM run automatically — in that case you can skip launching this wrapper.

title Ollama Server

:loop
echo.
echo [%date% %time%] Starting ollama serve...
ollama serve
echo [%date% %time%] Ollama exited with code %errorlevel%. Restarting in 5 s...
timeout /t 5 /nobreak
goto loop
