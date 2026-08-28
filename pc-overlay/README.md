# JvdP Light to Darkroom

Windows system-tray application for the JvdP ESP light sensor and Darkroom Booth.

At Windows sign-in the application starts silently in the system tray. Double-click
the tray icon or choose `Openen` to show the normal Windows window. Minimizing
or closing that window returns it to the tray. Choose `Afsluiten` in the tray menu to stop
the application completely.

## Current build

The current build version comes from the repository VERSION file. It controls Darkroom without coordinates, cursor movement, mouse clicks or full accessibility-tree scans.

Automatic mode is the default. Once the PC-mapped target has remained unchanged for
the configured stability period (60 seconds by default) and Darkroom Booth is
running, the application automatically performs the following flow. It starts Booth
Mode when the flow is finished. `RUN NOW (MANUAL)` remains available as an
immediate override.

The ESP sends only its 0–100 light value. This application maps that value to the
target ISO. A booth can use the released default profile shared by all booths, or
enable and edit its own local profile. The stability period can be changed in the
window from 5 to 300 seconds. Both choices are saved per Windows user and restored
after a reboot.

Serial discovery and the periodic Darkroom control probe run outside the UI thread.
Opening a slow COM port or waiting for a Darkroom native control cannot freeze the
main window.

1. Identifies the unique Darkroom editor from its native window tree before
   changing anything. Temporary Booth windows are not used as command targets.
2. If Booth Mode is visible, sends Escape to that specific Darkroom window's
   message queue. No global keyboard input or foreground-window change is used.
3. Reuses an already stable Camera page, or sends Originals `543` then Settings
   `545` in order. Reselecting Settings alone would open a modal menu instead.
4. Advances through at most ten native settings pages with command `662`.
   `33082` is a Settings picker return value, not a Camera command handler.
5. Stops when enabled, visible ISO ComboBox `107` supports native ComboBox queries
   and retains the same window handle for three observations.
6. Finds the requested ISO with one exact native ComboBox lookup. The full list is
   read only when the connected camera does not offer that exact ISO.
7. Confirms the selected ISO, checks only separate visible Darkroom error dialogs,
   then starts and visibly confirms Booth Mode again. An open Settings navigation
   menu is positively identified and cancelled first. Its HTML title and labels
   are read only for this purpose, with a bounded background reader; other dialogs
   remain open. Confirmed ISO and failed Booth return are reported separately.

The v5 accessibility/MSAA Camera search was removed after the test showed that Darkroom had already opened the native `Photos` settings page and the overlay then terminated during accessibility enumeration. Camera no longer needs to be found as a tile or label.

The normal action does not traverse Darkroom's full UI Automation tree or read its
process memory. Each step is timed and settings/ISO navigation has a ten-second
deadline. Starting Booth Mode has a separate five-second confirmation window.
A minimized or maximized editor alone is never confirmation of Booth Mode. On
failure, recovery is attempted only if this action started in Booth Mode and left
it. Preflight failure does not change the running booth.

Run `./test-darkroom-navigation.ps1` from the repository root for the simulated
navigation regression test. It sends no desktop or camera input and is also run
by the release workflow. This is not a substitute for checking a real camera.

Detailed flow and logs: `DARKROOM-FLOW-AND-LOGGING.md`.
## ISO commit fix (v7)

Darkroom keeps inactive settings pages alive as hidden controls. v6 could therefore find stale control ID 107 while Camera was not visible, change its displayed selection, and incorrectly read that value back even though the camera property remained unchanged. v7 accepts only a visible ISO control. It then sends Darkroom's parent window the native ComboBox notification chain `CBN_SELCHANGE`, `CBN_SELENDOK` and `CBN_CLOSEUP`, so the camera setting is committed rather than only displayed.
## v8 Darkroom ISO handler

Offline parsing of Darkroom Booth's MFC message map proves that Camera ISO control 107 is bound to handler `0x482740` as a plain `WM_COMMAND` with notification code 0. The v7 CBN notification sequence was incorrect and could block during the first synchronous parent notification.

V8 selects the exact value on the visible ISO ComboBox, posts plain `WM_COMMAND 107` to the Camera page, waits for Darkroom's handler, then activates visible Camera `Refresh` button 1104. The refreshed ComboBox value is read by the existing confirmation step. A display-only selection therefore fails verification instead of being reported as successful.
## v9 stable dropdown selection

The Settings menu is not clicked twice. One Settings command opens the current settings page; Darkroom's native Next Settings tool reaches Camera.

V9 locks the ESP target ISO immediately when an automatic or manual action starts, before settings navigation. It then requires ISO control 107 to remain visible with the same handle for three consecutive checks. The exact ComboBox is opened with `CB_SHOWDROPDOWN`, the target index is selected, and Enter is processed by the visible dropped ComboBox itself. No fabricated parent notification and no coordinate input is used.
## v10 native key commit

V9 opened the visible dropdown and displayed the target, but `CB_SETCURSEL` still bypassed the ComboBox's own selection-change path. V10 stages the adjacent list item and then sends one native Up/Down key transition to land on the target through the ComboBox window procedure. It confirms with Enter, activates Camera Refresh 1104, reacquires stable visible ISO control 107, and only then allows the existing value check and Start Booth flow.
