# Design QA — JvdP Lichtregeling 24.5.3

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
- The update status row grows and wraps when booth/status text needs more room; text is never replaced by an ellipsis or rendered through the update button or navigation.
- Both forms now have an explicit 96-DPI design baseline. Runtime geometry uses the monitor DPI while the responsive font scale uses logical window dimensions, preventing the former double-scaling on Surface displays.
- A final text-fit guard remeasures every visible fixed-size label and button after text, size or DPI changes. Content-driven rows grow first; font reduction is only a last-resort safety net.

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

- The implementation now has explicit DPI baselines, DPI-scaled geometry, measured button widths, content-driven rows and non-overlapping layout cells. It was visually tested at minimum and maximized window sizes on the current Windows system. A short booth smoke test at the booth's actual 150%/200% display scaling remains part of rollout verification.

final result: passed
