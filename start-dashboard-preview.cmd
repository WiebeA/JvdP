@echo off
cd /d "%~dp0"
title Light Sensor Dashboard Preview

where py >nul 2>nul
if %errorlevel%==0 (
  py -3 dashboard_preview.py
) else (
  python dashboard_preview.py
)

if not %errorlevel%==0 (
  echo.
  echo The preview could not start. Check whether Python is installed.
  pause
)
