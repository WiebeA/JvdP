# Darkroom flow and logging

## Fast cached flow

The current application performs the read-only `CXToolbar` validation immediately
after leaving Booth Mode. Some Darkroom versions do not expose that control while
Booth Mode is active, so automatic adjustment must never wait for a background
preparation in that state. The validated toolbar and command target are then reused
for the remaining lifetime of that Darkroom process.

The action path uses native child-window enumeration before accessibility APIs,
addresses Camera directly with command `33082`, finds an exact ISO with
`CB_FINDSTRINGEXACT`, and checks only separate visible error dialogs after the
selection. The former ten full accessibility-tree error scans are not part of the
action anymore. Per-step durations are written as `Darkroom timing:` log records and
the settings/change phase has a ten-second deadline.

## Current native flow

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
