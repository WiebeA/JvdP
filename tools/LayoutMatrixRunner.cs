using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Jvdp.LightDarkroomOverlay;

namespace Jvdp.LayoutTests
{
    internal static class LayoutMatrixRunner
    {
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(
            IntPtr dpiContext);

        private static Rectangle matrixWorkingArea;
        private static int matrixScreenDpi = 96;
        private static bool headlessMode;
        private static readonly MethodInfo ControlGetStateMethod =
            typeof(Control).GetMethod("GetState",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly Size[] Sizes = {
            new Size(1000, 720),
            new Size(1024, 768),
            new Size(1100, 720),
            new Size(1180, 760),
            new Size(1240, 820),
            new Size(1280, 720),
            new Size(1366, 768),
            new Size(1440, 900),
            new Size(1600, 900),
            new Size(1920, 1080)
        };

        private static readonly string[] States = {
            "dashboard",
            "technische-details",
            "statusvlak",
            "licht-en-iso",
            "overlay-instellingen"
        };

        private sealed class CaptureResult
        {
            internal string State;
            internal Size RequestedSize;
            internal Size ActualSize;
            internal int Dpi;
            internal string Path;
            internal readonly List<string> Issues = new List<string>();
        }

        [STAThread]
        private static int Main(string[] args)
        {
            Program.EnablePerMonitorDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            headlessMode = Array.Exists(args, delegate(string value)
            {
                return String.Equals(value, "--headless",
                    StringComparison.OrdinalIgnoreCase);
            });
            if (headlessMode)
            {
                try
                {
                    // A hidden WinForms tree does not receive the same
                    // per-monitor show/layout cycle as a real window. Keep
                    // this CI pass on a deterministic 96-DPI logical canvas;
                    // the production calculator is separately exercised at
                    // 100, 125, 150, 175 and 200 percent below.
                    SetThreadDpiAwarenessContext(new IntPtr(-1));
                }
                catch (EntryPointNotFoundException)
                {
                }
            }
            SelectHighestDpiScreen();
            string outputArgument = Array.Find(args, delegate(string value)
            {
                return !value.StartsWith("--", StringComparison.Ordinal);
            });
            string outputDirectory = !String.IsNullOrWhiteSpace(outputArgument)
                ? Path.GetFullPath(outputArgument)
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "layout-matrix");
            Directory.CreateDirectory(outputDirectory);

            List<CaptureResult> results = new List<CaptureResult>();
            results.Add(CaptureRangeDpiRegression());
            Dictionary<string, List<string>> stateImages =
                new Dictionary<string, List<string>>(
                    StringComparer.OrdinalIgnoreCase);
            foreach (string state in States)
                stateImages[state] = new List<string>();
            stateImages["volledig-scherm"] = new List<string>();

            for (int sizeIndex = 0; sizeIndex < Sizes.Length; sizeIndex++)
            {
                foreach (string state in States)
                {
                    CaptureResult result = CaptureApplicationState(
                        outputDirectory, sizeIndex, Sizes[sizeIndex], state);
                    results.Add(result);
                    stateImages[state].Add(result.Path);
                }
                CaptureResult fullscreen = CaptureFullscreenState(
                    outputDirectory, sizeIndex, Sizes[sizeIndex]);
                results.Add(fullscreen);
                stateImages["volledig-scherm"].Add(fullscreen.Path);
            }

            if (!headlessMode)
                foreach (KeyValuePair<string, List<string>> item in stateImages)
                    CreateContactSheet(outputDirectory, item.Key, item.Value);

            CaptureResult manualCover = CaptureManualCover(outputDirectory);
            WriteReport(outputDirectory, results, manualCover);
            int issueCount = 0;
            foreach (CaptureResult result in results)
                issueCount += result.Issues.Count;
            issueCount += manualCover.Issues.Count;
            Console.WriteLine(
                "Layout matrix: {0} captures, {1} issues, output={2}",
                results.Count, issueCount, outputDirectory);
            return issueCount == 0 ? 0 : 1;
        }

        private static CaptureResult CaptureApplicationState(
            string outputDirectory, int sizeIndex,
            Size requestedSize, string state)
        {
            CaptureResult result = new CaptureResult();
            result.State = state;
            result.RequestedSize = requestedSize;
            string fileName = String.Format(
                "{0:00}-{1}x{2}-{3}.png", sizeIndex + 1,
                requestedSize.Width, requestedSize.Height, state);
            result.Path = Path.Combine(outputDirectory, fileName);

            using (OverlayForm form = new OverlayForm(
                false, true, headlessMode))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = matrixWorkingArea.Location;
                form.MinimumSize = Size.Empty;
                form.MaximumSize = Size.Empty;
                form.ClientSize = requestedSize;
                if (headlessMode)
                {
                    IntPtr handle = form.Handle;
                    form.Location = matrixWorkingArea.Location;
                    form.ClientSize = requestedSize;
                    Application.DoEvents();
                }
                else
                {
                    form.Opacity = 0.0;
                    form.Show();
                    // Match the real application (a normal taskbar window)
                    // and reapply the target after HWND creation. Windows can
                    // otherwise initially associate a hidden test window with
                    // the primary display and keep it at that display's DPI.
                    form.Location = matrixWorkingArea.Location;
                    form.ClientSize = requestedSize;
                    Application.DoEvents();
                }
                if (state == "technische-details")
                    form.PrepareDashboardForLayoutTest(true);
                else if (state == "statusvlak")
                {
                    form.PrepareDashboardForLayoutTest(false);
                    form.ShowStatusForLayoutTest();
                }
                else if (state == "licht-en-iso")
                    form.ShowSettingsForLayoutTest(false);
                else if (state == "overlay-instellingen")
                    form.ShowSettingsForLayoutTest(true);
                else
                    form.PrepareDashboardForLayoutTest(false);

                // Opening an embedded settings page must never change the
                // requested matrix viewport. Reapply it after the page handle
                // exists so every settings capture exercises the exact case.
                form.ClientSize = requestedSize;
                if (state == "statusvlak")
                    form.ShowStatusForLayoutTest();
                Stabilize(form);
                if (headlessMode && form.Visible)
                    result.Issues.Add(
                        "Headless matrix heeft onverwacht een hoofdvenster getoond.");
                result.ActualSize = form.ClientSize;
                result.Dpi = GetEffectiveDpi(form);
                ValidateCapturedDpi(result);
                ValidateControlTree(form, result.Issues);
                ValidateRangeControls(form, result.Issues);
                ValidateContentDrivenDashboard(form, result.Issues);
                Control statusOverlay = null;
                if (!headlessMode && state == "statusvlak" &&
                    !form.StatusPanelVisibleForLayoutTest)
                    result.Issues.Add(
                        "Het statuspaneel is niet zichtbaar in de statuscase.");
                if (state == "statusvlak")
                {
                    statusOverlay = FindControlByAccessibleName(
                        form, "Statuspaneel");
                    if (statusOverlay == null ||
                        !IsVisibleForValidation(statusOverlay, form))
                        result.Issues.Add(
                            "Het statuspaneel ontbreekt in de control tree.");
                    else
                    {
                        if (!form.ClientRectangle.Contains(
                                statusOverlay.Bounds))
                            result.Issues.Add(
                                "Het statuspaneel valt buiten het venster: " +
                                statusOverlay.Bounds + ".");
                        if (form.Controls.GetChildIndex(statusOverlay) != 0)
                            result.Issues.Add(
                                "Het statuspaneel ligt niet voor de dashboardcontrols.");
                    }
                }
                if ((state == "licht-en-iso" ||
                     state == "overlay-instellingen") &&
                    FindControlByText(form,
                        "Terug naar dashboard") == null)
                    result.Issues.Add(
                        "Navigatieknop 'Terug naar dashboard' ontbreekt.");
                if (!headlessMode)
                    SaveControlBitmap(form, result.Path, statusOverlay);
                if (state == "dashboard" &&
                    !form.VerifyStableDashboardRefreshForLayoutTest())
                    result.Issues.Add(
                        "Herhaalde identieke statusupdates wijzigen nog steeds de dashboardlayout.");
                if (state == "licht-en-iso" &&
                    !form.VerifyStaleUpdateRecoveryForLayoutTest())
                    result.Issues.Add(
                        "Een vastgelopen bestaande updatestatus wordt niet opnieuw probeerbaar.");
                if (state == "licht-en-iso")
                {
                    ValidateTouchProfileEditor(form, result.Issues);
                    if (!form.VerifyTouchProfileEditingForLayoutTest())
                        result.Issues.Add(
                            "De grote grens- of ISO-bediening wijzigt het profiel niet correct.");
                }
                form.Close();
                if (!headlessMode)
                    Application.DoEvents();
            }
            return result;
        }

        private static CaptureResult CaptureFullscreenState(
            string outputDirectory, int sizeIndex, Size requestedSize)
        {
            CaptureResult result = new CaptureResult();
            result.State = "volledig-scherm";
            result.RequestedSize = requestedSize;
            result.ActualSize = requestedSize;
            string fileName = String.Format(
                "{0:00}-{1}x{2}-volledig-scherm.png", sizeIndex + 1,
                requestedSize.Width, requestedSize.Height);
            result.Path = Path.Combine(outputDirectory, fileName);
            string title = new string('W', 60);
            string message = BuildMaximumCoverMessage();
            using (ActionCoverForm form = new ActionCoverForm(
                new Rectangle(Point.Empty, requestedSize), title, message))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = matrixWorkingArea.Location;
                form.TopMost = false;
                form.ClientSize = requestedSize;
                if (headlessMode)
                {
                    IntPtr handle = form.Handle;
                    form.Location = matrixWorkingArea.Location;
                    form.ClientSize = requestedSize;
                    Application.DoEvents();
                }
                else
                {
                    form.Opacity = 0.0;
                    form.Show();
                }
                Stabilize(form);
                if (headlessMode && form.Visible)
                    result.Issues.Add(
                        "Headless matrix heeft onverwacht een fullscreenvenster getoond.");
                result.ActualSize = form.ClientSize;
                result.Dpi = GetEffectiveDpi(form);
                ValidateCapturedDpi(result);
                ValidateFullscreenText(
                    result.ActualSize, title, message, result.Issues);
                if (!headlessMode)
                    SaveControlBitmap(form, result.Path);
                form.Close();
                if (!headlessMode)
                    Application.DoEvents();
            }
            return result;
        }

        private static CaptureResult CaptureManualCover(
            string outputDirectory)
        {
            CaptureResult result = new CaptureResult();
            result.State = "volledig-scherm-verbergen";
            result.RequestedSize = new Size(1920, 1080);
            result.Path = Path.Combine(
                outputDirectory, "manual-cover-button.png");
            using (ManualCoverControlForm form =
                new ManualCoverControlForm(
                    new Rectangle(matrixWorkingArea.Location,
                        new Size(1920, 1080)),
                    delegate { }))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = matrixWorkingArea.Location;
                form.TopMost = false;
                if (headlessMode)
                {
                    IntPtr handle = form.Handle;
                    form.Location = matrixWorkingArea.Location;
                    Application.DoEvents();
                }
                else
                {
                    form.Opacity = 0.0;
                    form.Show();
                }
                Stabilize(form);
                if (headlessMode && form.Visible)
                    result.Issues.Add(
                        "Headless matrix heeft onverwacht de verbergknop getoond.");
                result.ActualSize = form.ClientSize;
                result.Dpi = GetEffectiveDpi(form);
                ValidateCapturedDpi(result);
                ValidateControlTree(form, result.Issues);
                if (FindControlByText(
                        form, "VOLLEDIG SCHERM VERBERGEN") == null)
                    result.Issues.Add(
                        "De knop om het volledige scherm te verbergen ontbreekt.");
                if (!headlessMode)
                    SaveControlBitmap(form, result.Path);
                form.Close();
                if (!headlessMode)
                    Application.DoEvents();
            }
            return result;
        }

        private static void Stabilize(Control root)
        {
            for (int pass = 0; pass < 4; pass++)
            {
                root.PerformLayout();
                if (headlessMode)
                    FitVisibleTextTreeForValidation(root, root);
                else
                    OverlayForm.FitTextTree(root);
                if (!headlessMode)
                    root.Refresh();
                Application.DoEvents();
                if (!headlessMode)
                    Thread.Sleep(20);
            }
        }

        private static void FitVisibleTextTreeForValidation(
            Control control, Control root)
        {
            if (control != root &&
                IsVisibleForValidation(control, root) &&
                (control is Label || control is Button))
                OverlayForm.FitTextControl(control);
            foreach (Control child in control.Controls)
                FitVisibleTextTreeForValidation(child, root);
        }

        private static void ValidateCapturedDpi(CaptureResult result)
        {
            if (result.Dpi != matrixScreenDpi)
                result.Issues.Add(String.Format(
                    "Verkeerde monitor-DPI: vastgelegd={0}, verwacht={1}.",
                    result.Dpi, matrixScreenDpi));
        }

        private static void SaveControlBitmap(
            Control control, string path, Control overlay = null)
        {
            Size size = control.ClientSize;
            using (Bitmap bitmap = new Bitmap(
                Math.Max(1, size.Width), Math.Max(1, size.Height)))
            {
                control.DrawToBitmap(bitmap,
                    new Rectangle(Point.Empty, bitmap.Size));
                // DrawToBitmap does not reliably include overlapping WinForms
                // siblings. Composite the exact status control at its tested
                // bounds so the contact sheet remains valid visual evidence.
                if (overlay != null && overlay.Visible)
                {
                    using (Bitmap overlayBitmap = new Bitmap(
                        Math.Max(1, overlay.ClientSize.Width),
                        Math.Max(1, overlay.ClientSize.Height)))
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        overlay.DrawToBitmap(overlayBitmap,
                            new Rectangle(Point.Empty, overlayBitmap.Size));
                        graphics.DrawImageUnscaled(
                            overlayBitmap, overlay.Left, overlay.Top);
                    }
                }
                bitmap.Save(path, ImageFormat.Png);
            }
        }

        private static void ValidateControlTree(
            Control root, List<string> issues)
        {
            Queue<Control> queue = new Queue<Control>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                Control parent = queue.Dequeue();
                List<Control> visibleChildren = new List<Control>();
                foreach (Control child in parent.Controls)
                {
                    if (IsVisibleForValidation(child, root))
                    {
                        visibleChildren.Add(child);
                        queue.Enqueue(child);
                        ValidateControlBounds(child, issues);
                        ValidateReadableText(child, issues);
                        ValidateDataGrid(child as DataGridView, issues);
                    }
                }
                ValidateSiblingOverlaps(parent, visibleChildren, issues);
            }
        }

        private static void ValidateControlBounds(
            Control control, List<string> issues)
        {
            Control parent = control.Parent;
            if (parent == null)
                return;
            ScrollableControl scrollable = parent as ScrollableControl;
            Rectangle allowed = parent.ClientRectangle;
            Rectangle bounds = control.Bounds;
            bool horizontalReachable = scrollable != null &&
                scrollable.AutoScroll &&
                scrollable.HorizontalScroll.Visible;
            bool verticalReachable = scrollable != null &&
                scrollable.AutoScroll &&
                scrollable.VerticalScroll.Visible;
            if (bounds.Left < allowed.Left - 2 ||
                bounds.Top < allowed.Top - 2 ||
                (bounds.Right > allowed.Right + 2 &&
                    !horizontalReachable) ||
                (bounds.Bottom > allowed.Bottom + 2 &&
                    !verticalReachable))
                issues.Add(String.Format(
                    "Control buiten parent: {0} '{1}' bounds={2}, parent={3} {4}.",
                    control.GetType().Name, SafeText(control.Text),
                    bounds, parent.GetType().Name, allowed));
        }

        private static void ValidateReadableText(
            Control control, List<string> issues)
        {
            if (String.IsNullOrWhiteSpace(control.Text) ||
                !(control is Label || control is Button ||
                  control is RadioButton || control is CheckBox))
                return;
            if (control.Font.Size + 0.05f <
                OverlayForm.MinimumReadableUiFontSize)
                issues.Add(String.Format(
                    "Onleesbaar klein font: {0} '{1}' = {2:0.0} pt.",
                    control.GetType().Name, SafeText(control.Text),
                    control.Font.Size));

            // AutoSize is the production fallback when text cannot fit in a
            // fixed box at the readable font floor. Validate the size WinForms
            // itself requests instead of subtracting fixed-box insets again;
            // doing both reports every correctly grown label/button as clipped.
            if (control.AutoSize)
            {
                Label autoLabel = control as Label;
                if (autoLabel != null)
                {
                    int proposedWidth = Math.Max(1,
                        autoLabel.ClientSize.Width);
                    Size labelPreferred = autoLabel.GetPreferredSize(
                        new Size(proposedWidth, 0));
                    if (labelPreferred.Width >
                            autoLabel.ClientSize.Width + 2 ||
                        labelPreferred.Height >
                            autoLabel.ClientSize.Height + 2)
                        issues.Add(String.Format(
                            "AutoSize-label past niet: '{0}' nodig={1}, beschikbaar={2}x{3}.",
                            SafeText(autoLabel.Text), labelPreferred,
                            autoLabel.ClientSize.Width,
                            autoLabel.ClientSize.Height));
                    return;
                }
                Size preferred = control.GetPreferredSize(
                    control.MaximumSize.IsEmpty
                        ? Size.Empty : control.MaximumSize);
                if (control.ClientSize.Width + 2 < preferred.Width ||
                    control.ClientSize.Height + 2 < preferred.Height)
                    issues.Add(String.Format(
                        "AutoSize-tekst past niet: {0} '{1}' voorkeur={2}, beschikbaar={3}.",
                        control.GetType().Name, SafeText(control.Text),
                        preferred, control.ClientSize));
                return;
            }
            bool button = control is Button ||
                control is RadioButton || control is CheckBox;
            float dpiScale = Math.Max(1f, control.DeviceDpi / 96f);
            int horizontalInset = (int)Math.Ceiling(
                (button ? 24 : 4) * dpiScale);
            int verticalInset = (int)Math.Ceiling(
                (button ? 14 : 2) * dpiScale);
            int width = Math.Max(1,
                control.ClientSize.Width - control.Padding.Horizontal -
                horizontalInset);
            int height = Math.Max(1,
                control.ClientSize.Height - control.Padding.Vertical -
                verticalInset);
            Size line = TextRenderer.MeasureText(
                "Ag", control.Font, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            bool multiline = !button &&
                (control.Text.IndexOfAny(new char[] { '\r', '\n' }) >= 0 ||
                 height >= line.Height * 2);
            Size measured = TextRenderer.MeasureText(
                control.Text, control.Font,
                new Size(width, multiline ? Int32.MaxValue : height),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
                (multiline
                    ? TextFormatFlags.WordBreak
                    : TextFormatFlags.SingleLine));
            if (measured.Width > width + 1 || measured.Height > height + 1)
                issues.Add(String.Format(
                    "Tekst past niet: {0} '{1}' nodig={2}, beschikbaar={3}x{4}.",
                    control.GetType().Name, SafeText(control.Text),
                    measured, width, height));
        }

        private static void ValidateSiblingOverlaps(
            Control parent, List<Control> controls, List<string> issues)
        {
            if (parent is NumericUpDown || parent is TextBox ||
                parent is ComboBox || parent is DataGridView)
                return;
            for (int first = 0; first < controls.Count; first++)
            {
                if (!IsLeafForOverlap(controls[first]))
                    continue;
                for (int second = first + 1;
                    second < controls.Count; second++)
                {
                    if (!IsLeafForOverlap(controls[second]))
                        continue;
                    Rectangle overlap = Rectangle.Intersect(
                        controls[first].Bounds, controls[second].Bounds);
                    if (overlap.Width > 2 && overlap.Height > 2)
                        issues.Add(String.Format(
                            "Sibling-overlap in {0}: {1} '{2}' en {3} '{4}' = {5}.",
                            parent.GetType().Name,
                            controls[first].GetType().Name,
                            SafeText(controls[first].Text),
                            controls[second].GetType().Name,
                            SafeText(controls[second].Text), overlap));
                }
            }
        }

        private static bool IsLeafForOverlap(Control control)
        {
            return control is Label || control is Button ||
                control is RadioButton || control is CheckBox ||
                control is NumericUpDown || control is TextBox ||
                control is ComboBox || control is LightRangeControl;
        }

        private static void ValidateDataGrid(
            DataGridView grid, List<string> issues)
        {
            if (grid == null)
                return;
            Font headerFont = grid.ColumnHeadersDefaultCellStyle.Font ??
                grid.Font;
            int headerLine = TextRenderer.MeasureText(
                "Ag", headerFont, Size.Empty,
                TextFormatFlags.NoPadding |
                TextFormatFlags.SingleLine).Height;
            if (grid.ColumnHeadersVisible &&
                grid.ColumnHeadersHeight < headerLine + 4)
                issues.Add("DataGrid-kopregel is te laag voor de tekst.");
            foreach (DataGridViewRow row in grid.Rows)
                if (row.Visible && row.Height < grid.Font.Height + 4)
                    issues.Add("DataGrid-rij is te laag voor de tekst.");
        }

        private static void ValidateRangeControls(
            Control root, List<string> issues)
        {
            foreach (Control control in EnumerateControls(root))
            {
                LightRangeControl range = control as LightRangeControl;
                if (range == null ||
                    !IsVisibleForValidation(range, root))
                    continue;
                FieldInfo bandsField = typeof(LightRangeControl).GetField(
                    "bands", BindingFlags.Instance | BindingFlags.NonPublic);
                List<IsoBand> bands = bandsField == null
                    ? null : bandsField.GetValue(range) as List<IsoBand>;
                if (bands == null || bands.Count == 0)
                    continue;
                LightRangeControl.LayoutSnapshot layout =
                    range.GetLayoutSnapshotForTest();
                Rectangle client = range.ClientRectangle;
                if (range.ClientSize.Height < layout.RequiredHeight)
                    issues.Add(String.Format(
                        "Bereikvak is te laag: {0}px, minimaal {1}px nodig voor tekst en schuifbalk.",
                        range.ClientSize.Height, layout.RequiredHeight));
                if (layout.RangeTextBounds.Top < client.Top ||
                    layout.RangeTextBounds.Bottom > client.Bottom ||
                    layout.IsoTextBounds.Top < client.Top ||
                    layout.IsoTextBounds.Bottom > client.Bottom)
                    issues.Add(String.Format(
                        "Bereiktekst valt buiten het tekenvlak: bereik={0}, ISO={1}, vlak={2}.",
                        layout.RangeTextBounds,
                        layout.IsoTextBounds, client));
                if (layout.RangeTextBounds.Height <
                        layout.RangeMeasuredHeight ||
                    layout.IsoTextBounds.Height <
                        layout.IsoMeasuredHeight)
                    issues.Add(String.Format(
                        "Bereikteksthoogte is onvoldoende: bereik {0}/{1}px, ISO {2}/{3}px.",
                        layout.RangeTextBounds.Height,
                        layout.RangeMeasuredHeight,
                        layout.IsoTextBounds.Height,
                        layout.IsoMeasuredHeight));
                int markerTop = layout.BarY - layout.MarkerRadius;
                int markerBottom = layout.BarY + layout.MarkerRadius;
                if (layout.IsoTextBounds.Bottom >= markerTop)
                    issues.Add(String.Format(
                        "ISO-tekst en schuifbalk overlappen verticaal: tekst tot {0}px, balk vanaf {1}px.",
                        layout.IsoTextBounds.Bottom, markerTop));
                if (markerBottom > client.Bottom)
                    issues.Add(String.Format(
                        "Schuifmarkering valt buiten het bereikvak: tot {0}px, vlak tot {1}px.",
                        markerBottom, client.Bottom));
                int cellWidth = Math.Max(1,
                    (range.ClientSize.Width - 2) / bands.Count);
                int available = Math.Max(1, cellWidth -
                    (int)Math.Ceiling(16 *
                        Math.Max(1f, range.DeviceDpi / 96f)));
                int lower = 0;
                using (Font font = new Font(
                    "Segoe UI", OverlayForm.MinimumReadableUiFontSize,
                    FontStyle.Bold))
                {
                    foreach (IsoBand band in bands)
                    {
                        string[] values = {
                            lower + "–" + band.MaximumLight,
                            "ISO " + band.Iso
                        };
                        foreach (string value in values)
                        {
                            int width = TextRenderer.MeasureText(
                                value, font, Size.Empty,
                                TextFormatFlags.NoPadding |
                                TextFormatFlags.SingleLine).Width;
                            if (width > available)
                                issues.Add(String.Format(
                                    "Bereiktekst '{0}' past niet leesbaar in cel: {1}>{2}.",
                                    value, width, available));
                        }
                        lower = band.MaximumLight + 1;
                    }
                }
            }
        }

        private static void ValidateContentDrivenDashboard(
            Control root, List<string> issues)
        {
            Panel mappingPanel = FindControlByAccessibleName(
                root, "Live lichtwaarde en ISO-overzicht") as Panel;
            if (mappingPanel == null ||
                !IsVisibleForValidation(mappingPanel, root))
                return;

            TableLayoutPanel mappingLayout = null;
            foreach (Control child in mappingPanel.Controls)
            {
                mappingLayout = child as TableLayoutPanel;
                if (mappingLayout != null)
                    break;
            }
            if (mappingLayout == null)
            {
                issues.Add(
                    "Het lichtwaardevak mist zijn inhoudsgestuurde layout.");
                return;
            }

            Size layoutPreferred = mappingLayout.GetPreferredSize(
                new Size(Math.Max(1, mappingLayout.ClientSize.Width), 0));
            if (mappingLayout.ClientSize.Height + 2 <
                    layoutPreferred.Height ||
                mappingPanel.ClientSize.Height + 2 < mappingLayout.Bottom)
                issues.Add(String.Format(
                    "Het lichtwaardevak is kleiner dan zijn inhoud: " +
                    "kaart={0}, layout={1}, voorkeur={2}.",
                    mappingPanel.ClientSize, mappingLayout.Bounds,
                    layoutPreferred));

            TableLayoutPanel metrics = FindControlByAccessibleName(
                root, "Actuele lichtwaarden") as TableLayoutPanel;
            if (metrics == null)
            {
                issues.Add("De inhoudsgestuurde meetwaardenlayout ontbreekt.");
                return;
            }
            foreach (RowStyle style in metrics.RowStyles)
                if (style.SizeType != SizeType.AutoSize)
                    issues.Add(
                        "Een meetwaardenrij heeft geen AutoSize-hoogte.");

            string[] names = {
                "Actuele lichtwaarde", "Actief lichtbereik", "Doel ISO"
            };
            foreach (string name in names)
            {
                Label label = FindControlByAccessibleName(
                    metrics, name) as Label;
                if (label == null)
                {
                    issues.Add("Meetwaarde ontbreekt: " + name + ".");
                    continue;
                }
                if (!label.AutoSize)
                    issues.Add("Meetwaarde gebruikt geen AutoSize: " +
                        name + ".");
                Size preferred = label.GetPreferredSize(
                    new Size(Math.Max(1, label.ClientSize.Width), 0));
                if (label.ClientSize.Height + 2 < preferred.Height)
                    issues.Add(String.Format(
                        "Meetwaarde wordt verticaal afgesneden: {0} " +
                        "heeft {1}px en vereist {2}px.",
                        name, label.ClientSize.Height, preferred.Height));
                if (label.Parent != null &&
                    label.Bounds.Bottom >
                        label.Parent.ClientRectangle.Bottom + 2)
                    issues.Add("Meetwaarde valt buiten zijn parent: " +
                        name + ".");
            }
        }

        private static void ValidateTouchProfileEditor(
            Control root, List<string> issues)
        {
            Panel card = FindControlByAccessibleName(
                root, "Touchvriendelijke lichtbereiken") as Panel;
            if (card == null || !IsVisibleForValidation(card, root))
            {
                issues.Add(
                    "De touchvriendelijke editor voor lichtbereiken ontbreekt.");
                return;
            }

            int rowCount = 0;
            int editableCount = 0;
            Queue<Control> queue = new Queue<Control>();
            queue.Enqueue(card);
            while (queue.Count > 0)
            {
                Control control = queue.Dequeue();
                if (control.AccessibleName != null &&
                    control.AccessibleName.StartsWith(
                        "Lichtbereik ", StringComparison.Ordinal) &&
                    control is TableLayoutPanel)
                    rowCount++;
                bool touchInput = control is Button ||
                    control is NumericUpDown || control is TextBox ||
                    control is ComboBox;
                if (touchInput && control.Enabled &&
                    IsVisibleForValidation(control, root))
                {
                    editableCount++;
                    float scale = Math.Max(1f, control.DeviceDpi / 96f);
                    int minimumPixels = (int)Math.Floor(48 * scale);
                    if (control.ClientSize.Height + 2 < minimumPixels)
                        issues.Add(String.Format(
                            "Touchbediening is te laag: {0} '{1}' heeft {2}px, minimaal {3}px.",
                            control.GetType().Name, SafeText(control.Text),
                            control.ClientSize.Height, minimumPixels));
                }
                foreach (Control child in control.Controls)
                    queue.Enqueue(child);
            }
            if (rowCount != 6)
                issues.Add(String.Format(
                    "De standaard touch-editor toont {0} in plaats van 6 bereiken.",
                    rowCount));
            if (editableCount < 24)
                issues.Add(String.Format(
                    "De touch-editor bevat te weinig grote directe bedieningen: {0}.",
                    editableCount));
        }

        private static CaptureResult CaptureRangeDpiRegression()
        {
            CaptureResult result = new CaptureResult();
            result.State = "bereik-dpi-regressie";
            result.RequestedSize = Size.Empty;
            result.ActualSize = Size.Empty;
            result.Dpi = 0;
            FieldInfo stabilityDefault = typeof(OverlayForm).GetField(
                "DefaultStabilitySeconds",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (stabilityDefault == null ||
                (int)stabilityDefault.GetRawConstantValue() != 60)
                result.Issues.Add(
                    "De standaard wachttijd voor lichtstabiliteit is niet 60 seconden.");
            float[] dpiScales = { 1f, 1.25f, 1.5f, 1.75f, 2f };
            float[] visualScales = { 1f, 1.3f };
            int[] bandCounts = { 6, 8 };
            foreach (float dpiScale in dpiScales)
            {
                foreach (float visualScale in visualScales)
                {
                    foreach (int bandCount in bandCounts)
                    {
                        int rangeHeight = (int)Math.Ceiling(
                            17 * dpiScale * visualScale);
                        int isoHeight = (int)Math.Ceiling(
                            16 * dpiScale * visualScale);
                        int width = (int)Math.Ceiling(
                            bandCount * 150 * dpiScale);
                        LightRangeControl.LayoutSnapshot required =
                            LightRangeControl.CalculateVerticalLayoutForTest(
                                new Rectangle(1, 1,
                                    Math.Max(1, width - 2), 1),
                                rangeHeight, isoHeight, dpiScale,
                                visualScale, true);
                        Rectangle client = new Rectangle(
                            0, 0, width, required.RequiredHeight);
                        Rectangle content = new Rectangle(
                            1, 1, width - 2,
                            required.RequiredHeight - 2);
                        LightRangeControl.LayoutSnapshot layout =
                            LightRangeControl.CalculateVerticalLayoutForTest(
                                content, rangeHeight, isoHeight,
                                dpiScale, visualScale, true);
                        int markerTop = layout.BarY -
                            layout.MarkerRadius;
                        int markerBottom = layout.BarY +
                            layout.MarkerRadius;
                        int minimumTextBarGap = (int)Math.Ceiling(
                            12 * dpiScale);
                        if (!client.Contains(layout.RangeTextBounds) ||
                            !client.Contains(layout.IsoTextBounds) ||
                            layout.RangeTextBounds.Bottom >
                                layout.IsoTextBounds.Top ||
                            layout.IsoTextBounds.Bottom +
                                minimumTextBarGap > markerTop ||
                            markerBottom > client.Bottom ||
                            layout.RequiredHeight != client.Height)
                            result.Issues.Add(String.Format(
                                "Bereiklayout faalt bij {0:0}% DPI, schaal {1:0.0}, {2} bereiken: bereik={3}, ISO={4}, marker={5}..{6}, vlak={7}.",
                                dpiScale * 100, visualScale,
                                bandCount, layout.RangeTextBounds,
                                layout.IsoTextBounds, markerTop,
                                markerBottom, client));
                    }
                }
            }
            return result;
        }

        private static IEnumerable<Control> EnumerateControls(Control root)
        {
            foreach (Control child in root.Controls)
            {
                yield return child;
                foreach (Control descendant in EnumerateControls(child))
                    yield return descendant;
            }
        }

        private static Control FindControlByText(Control root, string text)
        {
            foreach (Control control in EnumerateControls(root))
                if (IsVisibleForValidation(control, root) && String.Equals(
                    control.Text, text, StringComparison.Ordinal))
                    return control;
            return null;
        }

        private static Control FindControlByAccessibleName(
            Control root, string accessibleName)
        {
            foreach (Control control in EnumerateControls(root))
                if (String.Equals(control.AccessibleName, accessibleName,
                        StringComparison.Ordinal))
                    return control;
            return null;
        }

        private static bool IsVisibleForValidation(
            Control control, Control root)
        {
            if (!headlessMode)
                return control.Visible;
            Control current = control;
            while (current != null && current != root)
            {
                if (!HasOwnVisibleFlag(current))
                    return false;
                current = current.Parent;
            }
            return current == root;
        }

        private static bool HasOwnVisibleFlag(Control control)
        {
            if (ControlGetStateMethod == null)
                throw new InvalidOperationException(
                    "De interne WinForms-zichtbaarheidscontrole ontbreekt; " +
                    "de headless layouttest kan daardoor niet betrouwbaar draaien.");
            try
            {
                return (bool)ControlGetStateMethod.Invoke(
                    control, new object[] { 0x00000002 });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "De interne WinForms-zichtbaarheidscontrole is mislukt; " +
                    "de headless layouttest is afgebroken.", ex);
            }
        }

        private static void ValidateFullscreenText(
            Size size, string title,
            string message, List<string> issues)
        {
            RectangleF titleArea;
            RectangleF messageArea;
            ActionCoverForm.CalculateTextAreas(
                size, out titleArea, out messageArea);
            using (Bitmap bitmap = new Bitmap(1, 1))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (Font titleFont = new Font(
                "Britannic Bold", 15f, FontStyle.Regular))
            using (Font messageFont = new Font(
                "Britannic Bold", 13f, FontStyle.Regular))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.None;
                SizeF titleSize = graphics.MeasureString(
                    title, titleFont, (int)titleArea.Width, format);
                SizeF messageSize = graphics.MeasureString(
                    message, messageFont, (int)messageArea.Width, format);
                if (titleSize.Width > titleArea.Width + 1f ||
                    titleSize.Height > titleArea.Height + 1f)
                    issues.Add("Fullscreen-kop past niet op minimaal 15 pt.");
                if (messageSize.Width > messageArea.Width + 1f ||
                    messageSize.Height > messageArea.Height + 1f)
                    issues.Add("Fullscreen-bericht past niet op minimaal 13 pt.");
            }
        }

        private static string BuildMaximumCoverMessage()
        {
            string value =
                "Dit is een maximale testtekst voor de volledige " +
                "schermweergave. Alle woorden moeten leesbaar blijven, " +
                "automatisch afbreken en volledig zichtbaar zijn. Geen " +
                "regel mag worden afgesneden of buiten het scherm vallen. ";
            if (value.Length < 240)
                value = value.PadRight(240, '.');
            return value.Substring(0, 240);
        }

        private static void SelectHighestDpiScreen()
        {
            if (headlessMode)
            {
                Screen headlessScreen = Screen.PrimaryScreen;
                int headlessDpi = ProbeHiddenScreenDpi(headlessScreen);
                foreach (Screen screen in Screen.AllScreens)
                {
                    int dpi = ProbeHiddenScreenDpi(screen);
                    Console.WriteLine(
                        "Layout matrix headless probe: {0}, bounds={1}, work={2}, dpi={3}",
                        screen.DeviceName, screen.Bounds,
                        screen.WorkingArea, dpi);
                    if (dpi < headlessDpi ||
                        (dpi == headlessDpi && screen.Primary))
                    {
                        headlessScreen = screen;
                        headlessDpi = dpi;
                    }
                }
                matrixWorkingArea = headlessScreen.WorkingArea;
                matrixScreenDpi = headlessDpi;
                Console.WriteLine(
                    "Layout matrix display: {0}, work={1}, dpi={2}, headless=true",
                    headlessScreen.DeviceName, matrixWorkingArea,
                    matrixScreenDpi);
                return;
            }
            Screen selected = Screen.PrimaryScreen;
            int selectedDpi = 96;
            foreach (Screen screen in Screen.AllScreens)
            {
                int dpi = ProbeScreenDpi(screen);
                Console.WriteLine(
                    "Layout matrix probe: {0}, bounds={1}, work={2}, dpi={3}",
                    screen.DeviceName, screen.Bounds,
                    screen.WorkingArea, dpi);
                if (dpi > selectedDpi ||
                    (dpi == selectedDpi && screen.Primary))
                {
                    selected = screen;
                    selectedDpi = dpi;
                }
            }
            matrixWorkingArea = selected.WorkingArea;
            matrixScreenDpi = selectedDpi;
            Console.WriteLine(
                "Layout matrix display: {0}, work={1}, dpi={2}",
                selected.DeviceName, matrixWorkingArea, matrixScreenDpi);
        }

        private static int ProbeScreenDpi(Screen screen)
        {
            using (Form probe = new Form())
            {
                probe.FormBorderStyle = FormBorderStyle.None;
                probe.StartPosition = FormStartPosition.Manual;
                probe.ShowInTaskbar = false;
                Rectangle target = new Rectangle(
                    screen.WorkingArea.Left + 8,
                    screen.WorkingArea.Top + 8, 64, 64);
                probe.Bounds = target;
                // Opacity can create a handle while the form still has its
                // default position on the primary display. Set it only after
                // the target monitor is known, then reapply the bounds once
                // the native window exists so Windows assigns its real DPI.
                probe.Opacity = 0.01;
                probe.Show();
                probe.Bounds = target;
                Application.DoEvents();
                int dpi = GetEffectiveDpi(probe);
                probe.Close();
                Application.DoEvents();
                return dpi;
            }
        }

        private static int ProbeHiddenScreenDpi(Screen screen)
        {
            using (Form probe = new Form())
            {
                probe.FormBorderStyle = FormBorderStyle.None;
                probe.StartPosition = FormStartPosition.Manual;
                probe.ShowInTaskbar = false;
                Rectangle target = new Rectangle(
                    screen.WorkingArea.Left + 8,
                    screen.WorkingArea.Top + 8, 64, 64);
                probe.Bounds = target;
                IntPtr handle = probe.Handle;
                // A hidden HWND is initially associated with the primary
                // monitor. Move it again after creation so Windows sends the
                // per-monitor DPI transition without ever showing the form.
                probe.Bounds = target;
                Application.DoEvents();
                return GetEffectiveDpi(probe);
            }
        }

        private static void CreateContactSheet(
            string outputDirectory, string state, List<string> paths)
        {
            const int columns = 2;
            const int tileWidth = 520;
            const int tileHeight = 350;
            const int labelHeight = 28;
            int rows = (paths.Count + columns - 1) / columns;
            using (Bitmap sheet = new Bitmap(
                columns * tileWidth, rows * (tileHeight + labelHeight)))
            using (Graphics graphics = Graphics.FromImage(sheet))
            using (Font labelFont = new Font(
                "Segoe UI", 10, FontStyle.Bold))
            {
                graphics.Clear(Color.FromArgb(238, 241, 245));
                for (int index = 0; index < paths.Count; index++)
                {
                    int column = index % columns;
                    int row = index / columns;
                    Rectangle tile = new Rectangle(
                        column * tileWidth, row * (tileHeight + labelHeight),
                        tileWidth, tileHeight);
                    using (Image source = Image.FromFile(paths[index]))
                    {
                        float scale = Math.Min(
                            (tileWidth - 12f) / source.Width,
                            (tileHeight - 12f) / source.Height);
                        Size drawn = new Size(
                            Math.Max(1, (int)(source.Width * scale)),
                            Math.Max(1, (int)(source.Height * scale)));
                        Rectangle destination = new Rectangle(
                            tile.Left + (tile.Width - drawn.Width) / 2,
                            tile.Top + (tile.Height - drawn.Height) / 2,
                            drawn.Width, drawn.Height);
                        graphics.DrawImage(source, destination);
                    }
                    string label = Path.GetFileNameWithoutExtension(
                        paths[index]);
                    graphics.DrawString(label, labelFont, Brushes.Black,
                        new RectangleF(
                            tile.Left + 8, tile.Bottom,
                            tile.Width - 16, labelHeight));
                }
                sheet.Save(Path.Combine(outputDirectory,
                    "matrix-" + state + ".png"), ImageFormat.Png);
            }
        }

        private static void WriteReport(
            string outputDirectory, List<CaptureResult> results,
            CaptureResult manualCover)
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("JvdP layout matrix");
            report.AppendLine("Generated=" + DateTime.Now.ToString("s"));
            report.AppendLine("Version=" + BuildInfo.Version);
            report.AppendLine("Cases=" + results.Count);
            report.AppendLine("MatrixDisplayWorkArea=" + matrixWorkingArea);
            report.AppendLine("MatrixDisplayDpi=" + matrixScreenDpi);
            report.AppendLine("ManualCover=" + manualCover.Path);
            int totalIssues = 0;
            foreach (CaptureResult result in results)
            {
                totalIssues += result.Issues.Count;
                report.AppendFormat(
                    "{0} requested={1}x{2} actual={3}x{4} dpi={5} issues={6}\r\n",
                    result.State, result.RequestedSize.Width,
                    result.RequestedSize.Height, result.ActualSize.Width,
                    result.ActualSize.Height, result.Dpi,
                    result.Issues.Count);
                foreach (string issue in result.Issues)
                    report.AppendLine("  - " + issue);
            }
            totalIssues += manualCover.Issues.Count;
            report.AppendFormat(
                "{0} actual={1}x{2} dpi={3} issues={4}\r\n",
                manualCover.State, manualCover.ActualSize.Width,
                manualCover.ActualSize.Height, manualCover.Dpi,
                manualCover.Issues.Count);
            foreach (string issue in manualCover.Issues)
                report.AppendLine("  - " + issue);
            report.Insert(0, "Issues=" + totalIssues + "\r\n");
            File.WriteAllText(
                Path.Combine(outputDirectory, "layout-matrix-report.txt"),
                report.ToString(), new UTF8Encoding(false));
        }

        private static string SafeText(string value)
        {
            if (String.IsNullOrEmpty(value))
                return "";
            string cleaned = value.Replace("\r", " ").Replace("\n", " ");
            return cleaned.Length <= 70
                ? cleaned : cleaned.Substring(0, 67) + "...";
        }

        private static int GetEffectiveDpi(Control control)
        {
            try
            {
                uint dpi = control != null && control.IsHandleCreated
                    ? GetDpiForWindow(control.Handle) : 0;
                if (dpi > 0)
                    return (int)dpi;
            }
            catch (EntryPointNotFoundException)
            {
            }
            return control == null ? 96 : Math.Max(96, control.DeviceDpi);
        }
    }
}
