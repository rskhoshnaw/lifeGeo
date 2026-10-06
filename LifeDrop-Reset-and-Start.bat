@echo off
rem Double-click: rebuild the local database (local data is replaced), then start everything.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\local\Start-All.ps1" -Reset
pause
