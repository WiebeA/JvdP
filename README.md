# JvdP Light Sensor for Darkroom Booth

This repository builds the ESP32-C3 light sensor firmware, the Windows Darkroom
tray application, the installer and the automatic updater.

Version is controlled by the single VERSION file.

## How it works

1. The ESP reads the LDR on GPIO 0 and converts it to a 0–100 light value.
2. It sends only `JVDP|light=...` over USB serial.
3. The Windows tray application scans all serial ports. A port is accepted only after a
   valid JVDP line is received, so booth PCs do not need a fixed COM number.
4. The Windows application maps the light value to ISO. Each booth can use the
   centrally maintained default profile or its own locally saved custom profile.
5. The application waits for a stable PC-mapped target and controls Darkroom
   through its native controls.
6. The updater checks the selected GitHub release channel every 30 minutes.

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

The production tray application automatically checks every available serial port and
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
tray application and updater automatically at sign-in.

The public repository needs no GitHub account or token. New installations use
the stable update channel automatically. Run `JvdpAutoUpdater.exe --configure`
only when a booth must switch between the stable and test channels.

The application starts active in the Windows system tray when the booth user signs in.
Double-clicking the lamp tray icon opens the light, touch-friendly Windows dashboard.
Minimizing or closing the window always sends it back to the tray; only the explicit
`Afsluiten` tray command exits the application completely. Starting the executable a
second time reopens the already running dashboard instead of creating a competing
sensor connection. As soon
as Darkroom Booth is running and the PC-mapped target ISO has been stable for 30
seconds, the ISO check/change runs automatically and Booth Mode is started. The
stability period is adjustable from 5 to 300 seconds under the expandable technical
details and is remembered after a reboot. `ISO ... nu toepassen` remains available
as an override only while a valid light value, mapping and Darkroom connection exist.
The bottom-right footer shows the installed version and the local installation date
and time, so an operator can immediately verify which build is active on a booth.

## ISO profiles

ISO mapping now belongs to the Windows application, not to the ESP. The default
profile is defined once in the released application and is therefore identical on
all booths. A booth can switch to a custom profile; that custom mapping is stored
only on that booth. Selecting or editing a profile has no effect until the operator
chooses `Opslaan`. The editor shows both a table and a graphical preview.
Ranges always connect, cover the complete 0–100 light scale and support ISO 100
through 25600.

Serial-port discovery and Darkroom status inspection run on background threads.
Slow COM ports or an unresponsive Darkroom control therefore no longer block the
Windows message loop or make the application show `Not responding`.

## Updating booths

After initial setup no manual PC update is needed. The updater:

1. checks GitHub immediately at Windows sign-in and then every 30 minutes;
2. downloads the newest allowed release;
3. verifies the installer SHA-256 checksum;
4. installs it silently;
5. restarts the tray application and updater.

An update started from the settings page reopens the dashboard visibly after
installation. Automatic background updates continue to restart into the tray.
Installer errors are written back to the update status so the settings page can
show a retry action instead of remaining on `Installeren`.

The default ISO profile ships inside that verified release. Publishing a new stable
release from the central PC therefore distributes both software changes and a changed
default profile to every online booth. Changing settings through the UI on one booth
does not publish them to the other booths; central defaults must be changed in the
release source and published as a newer release. Booth-specific custom profiles remain
local.

ESP firmware is included in every release. Firmware deployment remains a
separate OTA or USB action because a booth PC may not be connected to the ESP
Wi-Fi network while it has internet access.

## Legacy files

pc-agent contains the previous PowerShell agent and standalone OTA uploader.
They are kept for recovery but are not installed. See pc-agent/LEGACY.md.
