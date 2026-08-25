# Design QA — JvdP Lichtregeling 24.5.2

## Comparison target

- Source evidence: `C:\Users\waude\AppData\Local\Temp\codex-clipboard-3af6b407-d751-4f99-bbd1-5d5d8f15b67b.png`.
- Source state: Licht & ISO settings at a 1357 × 400 crop, showing the settings title touching the active tab and a clipped `Opslaan en` action.
- Implementation evidence: the locally installed native Windows build captured through Windows Graphics Capture in the same Licht & ISO state at its minimum window size and maximized size.
- Additional implementation state: the Overlay settings tab at maximized size.
- The source and implementation captures were reviewed together for control bounds, text visibility, spacing and resizing behavior.

## Final findings

No actionable P0, P1 or P2 visual differences remain.

- The settings title, both tabs and update action now live in a responsive two-row header. Windows DPI scaling can grow the controls without allowing their cells to overlap.
- Profile controls, the stability input, preview toolbar, mapping grid and footer actions use nested layout containers instead of independent screen coordinates.
- The minimum-size capture shows complete labels and complete `Opslaan` and `Terug` buttons.
- The maximized capture preserves the same hierarchy without stretched text, collisions or detached controls.
- The Overlay settings tab uses the same shared header and footer, and its title, message editor and preview remain within their own cards.
- The update status line uses ellipsis when booth/status text exceeds the available width; it cannot render through the update button or navigation.

## Interaction verification

- Licht & ISO and Overlay settings switch in place.
- The graphical light marker and numeric preview remain linked.
- `Opslaan` remains disabled until a setting changes and keeps its complete label.
- `Terug` returns without saving.
- The standalone installer payload test extracted and validated the overlay and updater successfully.
- The application remained responsive while switching tabs and resizing from the minimum size to maximized.

## Comparison history

### Pass 1 — supplied screenshot

- P1: `Instellingen` and the active `Licht & ISO` tab could touch or overlap under DPI scaling.
- P1: action text was clipped to `Opslaan en`, leaving the action ambiguous.
- P2: booth/version text and update state occupied loose absolute positions and could collide at narrower widths.

### Pass 2 — responsive implementation

- Replaced the loose settings coordinates with nested table/flow layouts.
- Renamed the primary action to the unambiguous `Opslaan`.
- Verified the same screen at minimum and maximum window sizes and checked the Overlay tab.
- No P0/P1/P2 layout issue remains.

## Follow-up hardware test

- A physical Microsoft Surface at 150% and 200% Windows scaling remains a useful booth-hardware regression check, but the controls now use non-overlapping layout cells and measured preferred sizes rather than collision-prone fixed positions.

final result: passed
