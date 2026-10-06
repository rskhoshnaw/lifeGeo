@echo off
rem Double-click: database + API + panel + the app on the phone connected by USB (USB debugging on).
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\local\Start-All.ps1" -Android
pause
