# JvdP Light to Darkroom Overlay

Windows overlay for the JvdP ESP light sensor and Darkroom Booth.

## Current build

The current build version comes from the repository VERSION file. It controls Darkroom without coordinates, cursor movement, mouse clicks, DOM or MSAA.

Automatic mode is the default. Once the ESP target has remained unchanged for
the configured stability period (30 seconds by default) and Darkroom Booth is
running, the overlay automatically performs the following flow. It starts Booth
Mode when the flow is finished. `RUN NOW (MANUAL)` remains available as an
immediate override.

The stability period can be changed in the overlay from 5 to 300 seconds. The
setting is saved per Windows user and restored after a reboot.

1. Leaves Booth Mode with Escape when necessary.
2. Identifies Darkroom's one real `CXToolbar` through read-only process metadata.
3. Sends Settings command `660` exactly once.
4. Uses Darkroom's own native `Next Settings` command `662` (`0x0296`) to advance through at most ten pages.
5. Stops as soon as native ISO ComboBox control `107` appears; that is the Camera page.
6. Reads the current ISO and changes it only when it differs from the ESP target.
7. Confirms the selected ISO, then starts Booth Mode again.

The v5 accessibility/MSAA Camera search was removed after the test showed that Darkroom had already opened the native `Photos` settings page and the overlay then terminated during accessibility enumeration. Camera no longer needs to be found as a tile or label.

If exact toolbar validation fails, a visible UI Automation `Back to Events` control may be invoked once. Otherwise the action stops without fallback commands.

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