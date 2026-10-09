@echo off
REM ============================================================================
REM  MO-Trainer : read-only building compare
REM
REM  This only READS the game process memory (OpenProcess + ReadProcessMemory).
REM  It does NOT write, does NOT inject anything, does NOT touch game files.
REM
REM  Why it needs administrator: the game itself is running as administrator,
REM  so a normal-privilege program cannot read it (error 5, access denied).
REM
REM  Result is written to:  dist\reports\building-compare.txt
REM ============================================================================
setlocal
set "HERE=%~dp0"
set "PY=C:\Users\12599\AppData\Local\Python\pythoncore-3.14-64\python.exe"
if not exist "%PY%" set "PY=python"

echo Starting the read-only compare...
echo A Windows permission prompt will appear - click "Yes".
echo.

powershell -NoProfile -Command "Start-Process -FilePath '%PY%' -ArgumentList '-I','%HERE%probe-buildings.py' -Verb RunAs -Wait"

echo.
echo Done. Result file:
echo   %HERE%..\dist\reports\building-compare.txt
echo.
pause
