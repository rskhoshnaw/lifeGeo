@echo off
rem Double-click: stop the API, the panel and the database container.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\local\Stop-All.ps1"
pause
