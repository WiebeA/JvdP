# Design QA — JvdP Lichtregeling 24.5.5

## Audit health

Final result: passed. No actionable P0, P1 or P2 layout issue remains in the
tested matrix.

## Evidence and scope

- Original problem evidence:
  `artifacts/audit-range-clipping-20260827/01-booth-settings-clipping.png`
  and `02-range-clipping-detail.png`.
- Automated and rendered implementation evidence:
  `artifacts/layout-matrix-24.5.5-final/`.
- Exact client sizes: 1000×720, 1024×768, 1100×720, 1180×760, 1240×820,
  1280×720, 1366×768, 1440×900, 1600×900 and 1920×1080.
- Six states per size: dashboard, expanded technical details, open status panel,
  Light & ISO settings, Overlay settings and the full-screen action message.
- Total: 60 rendered application cases, a separate full-screen hide control and
  a geometry regression at 100%, 125%, 150%, 175% and 200% DPI scaling.
- The automated checks reject sibling intersections, controls outside a fixed
  parent, text that does not fit, UI fonts below 8.5 pt, undersized grid rows and
  range labels that do not fit at the readable minimum.
- The status case additionally proves that the modal panel is visible, completely
  inside the client area and above the dashboard in the z-order.
- Settings and full-screen cases use deliberately long booth, status, title and
  message strings to exercise wrapping and scrolling.

## Findings and implementation

- Dashboard surfaces now resize from their measured child bounds. Increasing the
  window and its type no longer leaves the light metrics, active profile or
  technical status controls inside a stale fixed-height parent.
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
