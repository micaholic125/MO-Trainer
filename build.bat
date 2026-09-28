@echo off
rem MO-Trainer one-click build. See build.ps1 for details.
setlocal
chcp 65001 >nul 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" (
  echo [FAILED] exit code %RC%
) else (
  echo [DONE] output in dist\
)
endlocal & exit /b %RC%
