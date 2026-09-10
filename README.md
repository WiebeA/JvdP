# JvdP Light Sensor for Darkroom Booth

This repository builds the ESP32-C3 light sensor firmware, the Windows Darkroom
tray application, the installer and the automatic updater.

Version is controlled by the single VERSION file.

## How it works

1. The ESP reads the LDR on GPIO 0 and converts it to a 0–100 light value.
2. It sends legacy `JVDP|light=...` plus a versioned `JVDP2` metadata record over USB serial.
3. The Windows tray application scans all serial ports. A port is accepted only after a
   valid JVDP line is received, so booth PCs do not need a fixed COM number.
4. The Windows application maps the light value to ISO. Each booth can use the
   centrally maintained default profile or its own locally saved custom profile.
5. The application waits for a stable PC-mapped target and controls Darkroom
   through its native controls.
6. The updater checks every 30 minutes; installation waits for maintenance with Darkroom closed.

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
as Darkroom Booth is running and the PC-mapped target ISO has been stable for 60
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

The default profile uses six gradual ranges:

| Light value | Darkroom ISO |
| --- | ---: |
| 0–16 | 3200 |
| 17–33 | 2500 |
| 34–50 | 1600 |
| 51–67 | 1000 |
| 68–84 | 640 |
| 85–100 | 400 |

Custom booth profiles remain unchanged during an update. Booths that use the
default profile automatically receive this mapping with the application release.

Serial-port discovery and Darkroom status inspection run on background threads.
Slow COM ports or an unresponsive Darkroom control therefore no longer block the
Windows message loop or make the application show `Not responding`.

## Reliability, calibration and sessions (24.6.2)

See [implementation notes](docs/IMPLEMENTATIE-24.6.0.md) and
[24.6.2 behavior changes](docs/RELEASE-24.6.2.md), plus
[Darkroom session setup](docs/SESSION-INTEGRATION.md).

Normal automatic regulation works without session signals, including when upgrading
an existing booth profile. To require an idle signal, explicitly enable
**Wachten op een Darkroom-rustsignaal** after configuring and testing the integration.
With that option enabled, unknown or stale session state blocks actions in Booth Mode;
initial preparation outside Booth Mode remains possible. A reported busy session
always blocks actions. Without the integration, the app cannot infer guest-session
boundaries and uses the existing cover and native navigation flow.
Unknown Darkroom versions are informational, not a version-based lockout. The app
still checks the actual native controls and confirms the ISO on every action.
The compatibility checkbox records an operator's practical test for that version.

Open **Verbindingen en technische details → Kalibratie en diagnose** for
calibration, boundary margins, ISO limits, booth/camera names, profile import/export,
light history, diagnostics, fault recovery and maintenance. The tray menu also
opens this window. A sensor interruption restarts the full stability period;
three consecutive action failures block further automatic attempts.

## Updating booths

The updater downloads and verifies releases in the background. To install:

1. Finish the event and close Darkroom.
2. Click **Updates controleren** or **Update installeren** in the profile settings.
3. The updater checks the release and the installer closes the light app automatically,
   including when upgrading a running 24.5.16 or 24.6.0 installation.
4. The previous version is retained until the new app confirms a healthy start.
5. A failed update restores the previous version and blocks that failed release.

Unattended installation still requires **Onderhoud starten** in **Kalibratie en diagnose**.
Maintenance authorization expires after one hour. **Onderhoud stoppen** resumes
normal operation. Clicking the update button authorizes that manual installation
without a separate maintenance step; Darkroom must still be closed.
The 24.6.0 updater itself still requires maintenance before launching an installer;
users already on that version can start maintenance once or run the 24.6.1 installer directly.

Profiles are saved atomically in `booth-profile.json`; existing local settings are
imported on first launch. Custom profiles survive updates. Firmware is still a
separate USB/OTA deployment. Old firmware remains readable by the new application.

## Verification

Run `./build.ps1 -SkipFirmware`, then `./test-darkroom-navigation.ps1`,
`./test-desktop-integration.ps1`, `./test-reliability.ps1` and
`./test-layout-matrix.ps1 -SkipBuild -Headless`. Reliability tests inject installer
failure only into an isolated test directory; they do not operate the real booth.
The workflows run these checks alongside the firmware build.

## Legacy files

pc-agent contains the previous PowerShell agent and standalone OTA uploader.
They are kept for recovery but are not installed. See pc-agent/LEGACY.md.
