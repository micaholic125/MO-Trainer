@echo off
REM ============================================================================
REM  MO-Trainer : read-only probe, round 2 (find the model description block)
REM
REM  This only READS the game process memory (OpenProcess + ReadProcessMemory).
REM  It does NOT write, does NOT inject anything, does NOT touch game files.
REM
REM  Why it needs administrator: the game itself runs as administrator.
REM
REM  Result is written to:  dist\reports\building-compare2.txt
REM  This run scans all readable memory, so it may take 10-60 seconds.
REM ============================================================================
setlocal
set "HERE=%~dp0"
set "PY=C:\Users\12599\AppData\Local\Python\pythoncore-3.14-64\python.exe"
if not exist "%PY%" set "PY=python"

echo Starting the read-only probe (round 2)...
echo A Windows permission prompt will appear - click "Yes".
echo It scans memory, so please wait 10-60 seconds.
echo.

powershell -NoProfile -Command "Start-Process -FilePath '%PY%' -ArgumentList '-I','%HERE%probe-typeobject.py' -Verb RunAs -Wait"

echo.
echo Done. Result file:
echo   %HERE%..\dist\reports\building-compare2.txt
echo.
pause
