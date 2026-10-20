@echo off
rem Installs Internet Cafe Simulator 2 Co-op next to your Steam copy of the game (the Steam copy is not changed).
if not exist "%~dp0install.ps1" (
  echo Unzip the whole ICS2-Coop-Windows zip into a folder first, then run this file from there.
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
pause
