# Darkroom flow and logging

## Current flow — 24.5.15

The fixed-vtable process-memory scan has been removed. The command destination is
the unique native editor window owning the toolbar children, including hidden
children in Booth Mode. Duplicate toolbar IDs within one editor are harmless. If
those controls have been destroyed, a unique unowned, captioned Darkroom frame is
accepted; multiple possible editors are rejected before navigation. The transient
`Process.MainWindowHandle` is not used for actions.

Static analysis of the installed Darkroom 3.01.1434 executable and XPhotoLib proves:

- `CXFrame::OnCmdMsg` routes menu-style commands to its active page.
- Command `545` (`0x221`) selects Settings when coming from another tab. Selecting
  Settings **again** opens its modal navigation picker. It is not idempotent.
- Commands `660`–`662` are handled on that page; `660` opens a modal Settings picker,
  while `662` advances the current settings page.
- `33082` is a return value interpreted inside the modal picker, not a standalone
  Camera command handler. Sending it as `WM_COMMAND` is not a direct Camera action.

The implementation reuses an already stable Camera control. Otherwise it sends
Originals `543`, then Settings `545` to the same native message queue, in order.
This makes Settings a page transition even when Darkroom was already on another
Settings subpage. At most ten `662` commands then find a stable, visible, enabled
ISO control `107`. All commands use `lParam = 0`
and are addressed to the validated editor. No toolbar pointer, guessed coordinates,
global Escape key, C++ vtable address or process-memory scan is needed.

An existing Settings picker (including one left open by an older version) is
handled before Camera navigation and before returning to Booth Mode. Only a
visible `#32770` dialog owned by this editor, containing an `Internet Explorer_Server`
with HTML title `menu` and the six expected complete navigation labels, is
cancelled with `IDCANCEL`. The HTML identity comes from Darkroom's embedded
`NAVMENU_HTM` resource. Other dialogs, camera errors and unrecognized menus are
left open. This is a narrowly scoped, read-only HTML identity query, not a full
UIA/MSAA tree scan or HTML button click. Its single background reader has a
700 ms caller timeout, cannot send commands and cannot close a window after a
timeout. The editor must reenable before subsequent commands. ISO children under
a disabled editor are not accepted as ready controls.

Booth Mode is confirmed through a visible, non-tool, borderless Darkroom surface on
its display, rather than the editor's minimized state or maximized bounds. Escape
is posted only to the identified Booth window. Starting Booth Mode sends `33776`
once to the editor and waits up to five seconds for a visible Booth surface. On
failure, recovery happens only when this action had left an existing Booth Mode.
If ISO was confirmed but returning fails, the status preserves the confirmed ISO
and names the Booth Mode failure. An attempted but unconfirmed selection is
reported as unknown, never as an unchanged ISO.

After a normal start or a recovery start, the automatic covers are removed first.
The full-size Booth surfaces are then raised with `HWND_TOP` and asynchronous
positioning (no resize or permanent topmost change), invalidated for repaint, and
Darkroom is activated. Presentation requires three consecutive observations of a
foreground Darkroom process with no visible JvdP window covering any full-size
Booth background. A foreground camera preview alone is insufficient. One bounded
presentation retry is allowed; this never resends Start Booth or resizes a preview.
If the user intentionally left the manual cover on, that cover remains on.

The light-check timer now starts its next period after a successful ISO action
or a completed period requiring no change. The dashboard says `Controleert licht`
and shows the next light-check countdown instead of remaining on `Klaar` / 60/60.
Serial readings still arrive continuously; a check with unchanged ISO does not
leave Booth Mode. New target-ISO changes still start their own full stability
period. An action captures the exact target and period it validated, and cannot
reset a newer period which started while that action was running.

`tools/DarkroomNavigationTests.cs` covers page navigation from all eleven starting
pages both inside and outside Settings, the non-idempotent Settings command,
existing/late/slow navigation menus, unknown dialogs, duplicate/ambiguous editor
roots, failed preflight, failed exit/start, missing or unstable ISO controls,
deadlines, stage-aware status, full-screen presentation after cover removal,
bounded focus recovery, repeated light-check cycles and preservation of newer
stability periods, plus borderless-versus-captioned windows.
Tests use an in-memory fake and never touch the user's desktop or camera.
The real camera/booth is not exercised by these tests.

Microsoft's [command-routing documentation](https://learn.microsoft.com/en-us/cpp/mfc/tn021-command-and-message-routing?view=msvc-170)
describes the menu-style command mechanism used here. Darkroom's internal command
IDs are established by local executable analysis, not guaranteed by that document.

## Historical native flow (superseded)

Darkroom is never controlled with screen coordinates, synthesized mouse input, sampled pixels or screenshot-relative positions.

Offline executable analysis and the passive live window inventory established:

- Settings tool: `660` (`0x0294`)
- Previous Settings page: `661` (`0x0295`)
- Next Settings page: `662` (`0x0296`)
- Camera menu command: `33082`
- Camera internal page index: `5`
- PhotoBooth toolbar control ID: `4083`
- ISO ComboBox control ID: `107`
- Start Booth Mode: `33776` (`0x83F0`)

Two Darkroom controls share ID 4083. The overlay validates the real `CXToolbar` through read-only process memory, vtable identity, main-frame relation, owning process and window handle. It sends Settings exactly once.

The 2026-07-31 16:11 test proved that this opened Darkroom's native `Photos` settings page. The overlay then disappeared while enumerating the custom Settings UI through MSAA. Darkroom remained running and responsive. Therefore v6 contains no Camera accessibility scan at all.

After Settings opens, v6 sends Darkroom's native Next Settings command and checks for native ISO control 107 after every page. Darkroom has eleven settings pages, so at most ten advances reach Camera from any starting page. The loop is bounded and stops immediately when ISO appears.

## Expected v6 log

```text
Overlay started; build=23.0.0
Validated exact CXToolbar through read-only Darkroom object identity; handle=...
Activated Settings exactly once through validated CXToolbar handle=...
Advanced one Darkroom settings page through native tool 662; step=1/10.
Reached Darkroom Camera settings through native page navigation; ISO control 107 handle=..., page advances=...
ISO state: ESP target=..., Darkroom current=..., available choices=...
```

## Log location

`%LOCALAPPDATA%\JvdP\LightDarkroomOverlay\overlay.log`

Old lines remain in this append-only log and may describe removed v1-v5 implementations.
## v7 ISO persistence

The 16:25 and 16:26 logs reported target 3200 and locally read back 3200, but the next Camera open still showed 800. Cause: Darkroom retains hidden settings-page controls, and v6 accepted hidden ISO control 107. `CB_SETCURSEL` changed that stale ComboBox without committing the camera property.

v7 requires `IsWindowVisible` for ISO control 107, opens Camera for every run, and sends the control's parent the native notification sequence `CBN_SELCHANGE`, `CBN_SELENDOK`, and `CBN_CLOSEUP`. A hidden ComboBox can no longer produce a false success.
## v8 Darkroom ISO handler

Offline parsing of Darkroom Booth's MFC message map proves that Camera ISO control 107 is bound to handler `0x482740` as a plain `WM_COMMAND` with notification code 0. The v7 CBN notification sequence was incorrect and could block during the first synchronous parent notification.

V8 selects the exact value on the visible ISO ComboBox, posts plain `WM_COMMAND 107` to the Camera page, waits for Darkroom's handler, then activates visible Camera `Refresh` button 1104. The refreshed ComboBox value is read by the existing confirmation step. A display-only selection therefore fails verification instead of being reported as successful.
## v9 stable dropdown selection

The Settings menu is not clicked twice. One Settings command opens the current settings page; Darkroom's native Next Settings tool reaches Camera.

V9 locks the ESP target ISO immediately when an automatic or manual action starts, before settings navigation. It then requires ISO control 107 to remain visible with the same handle for three consecutive checks. The exact ComboBox is opened with `CB_SHOWDROPDOWN`, the target index is selected, and Enter is processed by the visible dropped ComboBox itself. No fabricated parent notification and no coordinate input is used.
## v10 native key commit

V9 opened the visible dropdown and displayed the target, but `CB_SETCURSEL` still bypassed the ComboBox's own selection-change path. V10 stages the adjacent list item and then sends one native Up/Down key transition to land on the target through the ComboBox window procedure. It confirms with Enter, activates Camera Refresh 1104, reacquires stable visible ISO control 107, and only then allows the existing value check and Start Booth flow.
