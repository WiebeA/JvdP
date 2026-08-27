# Design QA — JvdP Lichtregeling 24.5.8

## Audit health

Final result: passed. The dashboard refresh is stable and the profile editor is
touch-friendly. No actionable P0, P1 or P2 layout issue remains in the tested
matrix.

## Evidence and scope

- Original problem evidence:
  `artifacts/audit-dashboard-clipping-20260827/01-dashboard-metrics-clipping.png`.
- Side-by-side visual comparison:
  `artifacts/audit-dashboard-clipping-20260827/02-before-after-v24.5.6.png`.
- Video evidence for the original dashboard flash:
  `artifacts/audit-dashboard-flash-20260827/02-flicker-sequence.png`.
- Automated and rendered implementation evidence:
  `artifacts/layout-matrix-24.5.7-surface-final/`.
- Exact client sizes: 1000×720, 1024×768, 1100×720, 1180×760, 1240×820,
  1280×720, 1366×768, 1440×900, 1600×900 and 1920×1080.
- Six states per size: dashboard, expanded technical details, open status panel,
  Light & ISO settings, Overlay settings and the full-screen action message.
- Total: 60 rendered application cases, a separate full-screen hide control and
  a geometry regression at 100%, 125%, 150%, 175% and 200% DPI scaling.
- The automated checks reject sibling intersections, controls outside a fixed
  parent, text that does not fit, UI fonts below 8.5 pt, undersized grid rows and
  range labels that do not fit at the readable minimum. Dashboard tests use the
  deliberately difficult values `100`, `85–100` and `25600`.
- Eight identical dashboard refreshes are executed per tested size. The test
  rejects any changed dashboard card bounds or needless range-control redraw,
  which directly guards against the half-second blanking seen in the video.
- The profile test changes a range boundary and an ISO through the visible touch
  controls, verifies the underlying six-range profile, and restores the values.
- The status case additionally proves that the modal panel is visible, completely
  inside the client area and above the dashboard in the z-order.
- Settings and full-screen cases use deliberately long booth, status, title and
  message strings to exercise wrapping and scrolling.

## Findings and implementation

- The dashboard flash was reproduced from the supplied video. Its repeated
  blank frames matched the 500 ms UI timer exactly. Each tick reset the card
  minimum and maximum sizes to empty, performed a full layout and then restored
  them, exposing the collapsed intermediate state to Windows.
- Status refreshes now update data only. Layout is recalculated only after an
  actual resize or text/structure change. Content-driven cards are idempotent
  and the light-range control skips repainting when its data did not change.
- The profile grid has been replaced visually by six large touch rows. Each row
  has 54 px minus/plus targets, a large direct numeric field, a large ISO picker
  and its own remove action. Tapping a numeric field selects the current value,
  so a replacement can be typed immediately on a Surface.
- Stability time and preview light value now also have large minus/plus controls.
  The editor card scrolls independently on short viewports, while Save, Back and
  Add range remain fixed and reachable.

- The reported defect was reproduced at 1920×1080 and 144 DPI: Windows requested
  88 px for a large metric label while its fixed row supplied only 83 px. The
  lower five pixels were therefore guaranteed to be clipped.
- Dashboard cards now use nested AutoSize table layouts. Every text row contributes
  its own preferred height to its parent, and the outer dashboard scrolls when the
  viewport is shorter than the content. No dashboard text row uses a fixed or
  percentage height anymore.
- Responsive typography changes only fonts and horizontal constraints. It no
  longer writes stale `Top` or `Height` values back into the new dashboard tree.
- The regression compares every large metric label against WinForms'
  `Label.GetPreferredSize()` result and rejects a card whose client area is smaller
  than its content. This matches the renderer used on the booths instead of the
  earlier optimistic no-padding text estimate.
- Profile labels receive measured vertical spacing; the caption and selected
  profile cannot occupy the same pixels.
- Technical details and the status panel use content-driven table layouts with
  wrapping and intentional scrolling when the viewport is shorter than the
  content.
- The progress fill is docked inside its track and cannot grow taller than it.
- Settings use nested table and flow layouts. Navigation always reads
  `Terug naar dashboard`; footer actions always read `Opslaan` and `Terug`.
- Light-range labels are measured against the control's real drawing surface.
  The range and ISO lines each have their own height, padding and gap. The slider
  and drag marker are always placed below both text zones, and the control reports
  the resulting DPI-aware minimum and preferred height to its parent layout.
- The range regression now rejects vertical text clipping, a slider intersecting
  the ISO line, a marker outside the control and insufficient control height. It
  covers both six- and eight-range geometry at every tested DPI scale.
- Full-screen preview labels use character-safe wrapping, so even a deliberately
  unbroken 60-character title remains completely reachable and visible.
- Full-screen title and message text have readable minimums of 15 pt and 13 pt.
  Text wraps without an enclosing frame and the entire message remains reachable.
- The updater configuration form and the manual full-screen hide control are also
  content-driven and DPI-scaled.
- The installed overlay now ships with a .NET Framework 4.7
  `PerMonitorV2` configuration. The installer stages, validates and atomically
  replaces that configuration together with the application executable.
- The same 10-size matrix runs on every main-branch build and before every tagged
  release, so a later overlap or truncation blocks publication.

## Default stability period

When no booth-specific value has been saved, the automatic ISO stability period
is now 60 seconds. A deliberately saved value remains intact during an update.

## Default light mapping

The new shared default has six connected ranges: 0–16 → ISO 3200,
17–33 → ISO 2500, 34–50 → ISO 1600, 51–67 → ISO 1000,
68–84 → ISO 640 and 85–100 → ISO 400. All values are supported by the editor.
Custom booth profiles remain local and are not overwritten by an update.

## Evidence limit

The final ten-size matrix was executed on the physical Microsoft Surface display
at 144 DPI (150% Windows scaling) and passed all 60 rendered cases with zero
issues. The release workflow repeats the matrix in its Windows runner environment.
The shared production geometry calculator additionally passed 100%, 125%, 150%,
175% and 200% assertions. A physical 200% booth monitor is not available in this
workspace, so that scale remains a rollout smoke test rather than a claim made
from rendered physical-monitor evidence.
