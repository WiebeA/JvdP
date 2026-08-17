# JvdP Light Sensor for Darkroom Booth

This repository builds the ESP32-C3 light sensor firmware, the Windows Darkroom
overlay, the installer and the automatic updater.

Version is controlled by the single VERSION file.

## How it works

1. The ESP reads the LDR on GPIO 0 and maps the light percentage to an ISO.
2. It sends JVDP|light=...|iso=... over USB serial.
3. The Windows overlay scans all serial ports. A port is accepted only after a
   valid JVDP line is received, so booth PCs do not need a fixed COM number.
4. The overlay waits for a stable target and controls Darkroom through its native
   controls.
5. The updater checks the selected GitHub release channel every 30 minutes.

## Passwords

Two different passwords are used:

- JVDP_AP_PASSWORD is the operator-facing password for JvdP-LightSensor.
- JVDP_OTA_PASSWORD authorizes firmware replacement and must remain separate.

They are supplied only during builds. They are not stored in this repository.
For local builds set both environment variables. For releases configure them as
GitHub Actions repository secrets.

## Local build

Requirements:

- Windows with .NET Framework 4.x compiler
- Python and PlatformIO for firmware builds

Run:

    $env:JVDP_AP_PASSWORD = "<operator password>"
    $env:JVDP_OTA_PASSWORD = "<different OTA password>"
    ./build.ps1

Use ./build.ps1 -SkipFirmware to compile only the Windows applications.

Generated files are written to artifacts/:

- JvdP-Photobooth-Lichtsensor-Installatie.exe
- JvdpLightDarkroomOverlay.exe
- JvdpAutoUpdater.exe
- firmware.bin

## ESP port detection

The production overlay automatically checks every available serial port and
validates the ESP protocol. For a standalone diagnostic run:

    ./tools/Find-JvdpEspPort.ps1

No COM port is committed to the repository. A developer can still pass an
explicit port to PlatformIO when flashing over USB.

## Releases

The Build workflow compiles every branch and pull request with non-production
test credentials. A release is created by:

1. Update VERSION, for example to 23.1.0-test.1 or 23.1.0.
2. Merge the tested change to main.
3. Push the exact matching tag, for example v23.1.0-test.1.
4. GitHub builds the installer and firmware and publishes SHA-256 checksums.

Tags containing a hyphen are prereleases for the test channel. Normal semantic
version tags are stable releases.

## Installing a booth

Download the installer from the selected GitHub release and run it once under
the Windows account used by the booth. It installs per user and starts both the
overlay and updater automatically at sign-in.

The public repository needs no GitHub account or token. New installations use
the stable update channel automatically. Run `JvdpAutoUpdater.exe --configure`
only when a booth must switch between the stable and test channels.

The overlay starts automatically when the Windows booth user signs in. As soon
as Darkroom Booth is running and the ESP target ISO has been stable for 30
seconds, the ISO check/change runs automatically and Booth Mode is started. The
stability period is adjustable from 5 to 300 seconds in the overlay and is
remembered after a reboot. `RUN NOW (MANUAL)` remains available as an override.

## Updating booths

After initial setup no manual PC update is needed. The updater:

1. checks GitHub every 30 minutes;
2. downloads the newest allowed release;
3. verifies the installer SHA-256 checksum;
4. installs it silently;
5. restarts the overlay and updater.

ESP firmware is included in every release. Firmware deployment remains a
separate OTA or USB action because a booth PC may not be connected to the ESP
Wi-Fi network while it has internet access.

## Legacy files

pc-agent contains the previous PowerShell agent and standalone OTA uploader.
They are kept for recovery but are not installed. See pc-agent/LEGACY.md.
