@echo off
rem Double-click: database + API + panel (opens the browser). See docs/LOCAL-SETUP.md.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\local\Start-All.ps1"
pause
