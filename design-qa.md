# Design QA — JvDP Lichtregeling 24.0.0

## Comparison target

- Source visual truth: `C:\Users\waude\.codex\generated_images\01a0340f-23ba-79c1-bb2f-fcd8d9e43830\exec-76b79f99-5cd5-418d-bcf7-dcde56a97041.png`
- Final implementation: `G:\Mijn Drive\01. Klanten G\Jongens van de Photobooth\output\ux-redesign-20260824\dashboard-final-960-v3.png`
- Normalized comparison: `G:\Mijn Drive\01. Klanten G\Jongens van de Photobooth\output\ux-redesign-20260824\design-comparison-final.png`
- Minimum-size evidence: `G:\Mijn Drive\01. Klanten G\Jongens van de Photobooth\output\ux-redesign-20260824\dashboard-final-min-v2.png`
- Profile evidence: `G:\Mijn Drive\01. Klanten G\Jongens van de Photobooth\output\ux-redesign-20260824\profile-dialog-light-v1.png` and `profile-dialog-custom-v1.png`
- Target viewport: 960 × 680 px, 100% Windows scale.
- Source pixels: 1490 × 1056; normalized to 960 × 680 with high-quality bicubic sampling.
- Implementation pixels: 960 × 680 at device scale 1.
- Additional viewport: 760 × 620 px at device scale 1.
- State: source shows a healthy example value (light 63, ISO 800); implementation evidence shows the real offline empty state because no ESP or Darkroom was connected. Layout and static copy were compared directly; dynamic value color and marker behavior were verified from the same rendering code used by the profile preview.

## Final findings

No actionable P0, P1 or P2 differences remain.

- Fonts and typography: both target and implementation use the Segoe UI family, a clear title/value/body hierarchy and readable Dutch labels. The native implementation intentionally uses slightly stronger Windows text rendering.
- Spacing and layout rhythm: the live mapping remains the dominant region, followed by active profile, collapsed connection details and two actions. Native square borders replace the mock's soft web-like radii, which is intentional for the requested calm Windows-tool direction.
- Colors and visual tokens: white and pale-gray surfaces, restrained blue interaction color, amber waiting state, green ready state and red action-needed state match the target semantics. Disabled ISO application now uses a neutral gray surface rather than an active blue surface.
- Image and icon fidelity: the supplied real lamp application icon is retained. The main screen does not substitute raster assets with drawn placeholders. Decorative mock icons were omitted where native text controls communicate the same function more clearly.
- Copy and content: the interface is consistently Dutch. COM-port churn, Wi-Fi credentials and implementation detail are absent from the default dashboard.
- Responsiveness: 960 × 680 and the 760 × 620 minimum were captured. No persistent controls overlap, clip or require horizontal scrolling in the final pass.
- Accessibility: primary controls are at least 44 px high, color is paired with text, controls have accessible names/roles and the MSAA object for `Profiel bekijken` reports role `PushButton`, state `Focusable` and default action `Druk op`. Full Narrator/NVDA and 200% DPI testing remains a hardware-environment follow-up rather than a visual blocker.

## Focused-region evidence

The normalized full-view comparison is readable enough to judge typography, range labels, profile row, detail disclosure and action controls without a separate crop. The profile dialog has no selected-source frame, so it was checked separately against the approved guideline: table and graphical preview are both present, the default profile is read-only, and selecting the custom profile enables `Opslaan en activeren` without silently changing the active mapping.

## Interaction verification

- Profile dialog opens from the dashboard.
- Switching to `Eigen profiel` enables `Opslaan en activeren`; cancelling leaves the active mapping unchanged.
- Technical details expand and collapse.
- Invalid manual ISO application remains disabled.
- `--startup` starts the process active but hidden in the tray.
- Starting a second instance exits the second process and reopens the existing dashboard.
- A normal system-close command produces `CloseReason.UserClosing`, hides the window and leaves the process responsive.
- The final executable remained responsive during repeated captures and interaction checks.
- The latest 200 native application log lines contained no unhandled, monitor or UI-thread exception.

## Comparison history

### Pass 1

- P2: the disabled primary ISO button retained an active blue surface and had weak disabled-state clarity.
- P2: resizing to the 760 × 620 minimum produced a horizontal scrollbar.
- Fixes: added explicit disabled colors and reduced/recalculated flow-section width with a fresh layout pass.

### Pass 2

- Post-fix evidence: `dashboard-final-960-v3.png` and `dashboard-final-min-v2.png`.
- The disabled action is visibly neutral and the minimum-size dashboard has no horizontal scrollbar.
- No actionable P0/P1/P2 findings remain.

## Follow-up polish / test gaps

- Verify the live ready state with a physical ESP and Darkroom Booth session.
- Run Narrator or NVDA plus 150% and 200% Windows DPI on the production Surface before the stable GitHub release.

final result: passed
