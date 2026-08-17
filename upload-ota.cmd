@echo off
cd /d "%~dp0"
title Light Sensor OTA Update

if "%JVDP_OTA_PASSWORD%"=="" (
  echo Set JVDP_OTA_PASSWORD before running this updater.
  exit /b 2
)

echo Connect this PC to Wi-Fi network JvdP-LightSensor first.
echo OTA target: 192.168.9.1
echo.

pio run --environment esp32-c3-ota --target upload

echo.
if %errorlevel%==0 (
  echo OTA update completed successfully.
) else (
  echo OTA update failed. Check the Wi-Fi connection and try again.
)
pause
