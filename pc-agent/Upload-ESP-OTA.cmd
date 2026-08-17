@echo off
cd /d "%~dp0"
title JvdP Light Sensor OTA Update

if "%JVDP_OTA_PASSWORD%"=="" (
  echo Set JVDP_OTA_PASSWORD before running this updater.
  exit /b 2
)

echo Make sure this PC is connected to JvdP-LightSensor.
echo.
JvdpEspOtaUploader.exe firmware.bin 192.168.9.1 "%JVDP_OTA_PASSWORD%"

echo.
pause
