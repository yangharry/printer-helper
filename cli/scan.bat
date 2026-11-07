@echo off
rem Double-click to scan everything in the document feeder into one PDF.
rem Options are passed through, e.g.  scan.bat -Color -Dpi 300
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scan.ps1" %*
echo.
pause
