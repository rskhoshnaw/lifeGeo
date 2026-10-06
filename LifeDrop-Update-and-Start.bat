@echo off
rem Double-click: get the newest code of main, then database + API + panel.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\local\Start-All.ps1" -Pull
pause
