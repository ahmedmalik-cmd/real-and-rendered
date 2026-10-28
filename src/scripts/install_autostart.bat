@echo off
REM ─── One-time installer: makes launch_all.bat run automatically at login ─
REM Run this once. Creates a shortcut in your Windows Startup folder.
REM To undo: delete the shortcut from shell:startup.

set "HERE=%~dp0"
set "LAUNCHER=%HERE%launch_all.bat"
set "STARTUP=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup"
set "SHORTCUT=%STARTUP%\TheraminInstallation.lnk"

powershell -NoProfile -Command ^
  "$s = (New-Object -ComObject WScript.Shell).CreateShortcut('%SHORTCUT%');" ^
  "$s.TargetPath = '%LAUNCHER%';" ^
  "$s.WorkingDirectory = '%HERE%';" ^
  "$s.WindowStyle = 1;" ^
  "$s.Save();"

if exist "%SHORTCUT%" (
    echo Installed startup shortcut:
    echo   %SHORTCUT%
    echo.
    echo It will run launch_all.bat next time you log into Windows.
    echo To remove: delete that shortcut, or run:  del "%SHORTCUT%"
) else (
    echo Failed to create startup shortcut.
)
pause
