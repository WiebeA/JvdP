using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Automation;

namespace Jvdp.LightDarkroomOverlay
{
    internal sealed class SensorState
    {
        public readonly object Sync = new object();
        public int Light = -1;
        public int MappedIso = -1;
        public DateTime CandidateSince = DateTime.MinValue;
        public string SerialStatus = "Searching for ESP...";
        public string LastSerialError = "";
    }

    internal sealed class IsoBand
    {
        public int MaximumLight;
        public int Iso;

        public IsoBand(int maximumLight, int iso)
        {
            MaximumLight = maximumLight;
            Iso = iso;
        }

        public IsoBand Clone()
        {
            return new IsoBand(MaximumLight, Iso);
        }
    }

    internal enum OverlayButtonIcon
    {
        Minimize,
        Close,
        Eye
    }

    internal sealed class OverlayIconButton : Button
    {
        private readonly OverlayButtonIcon icon;
        private bool hovered;
        private bool pressed;
        public bool EyeSlashed { get; set; }

        public OverlayIconButton(OverlayButtonIcon iconKind)
        {
            icon = iconKind;
            AutoSize = false;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Color.FromArgb(48, 54, 61);
            ForeColor = Color.FromArgb(232, 238, 245);
            TabStop = false;
            UseVisualStyleBackColor = false;
            AccessibleRole = AccessibleRole.PushButton;
            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovered = false;
            pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            pressed = true;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color background = pressed
                ? Color.FromArgb(82, 89, 99)
                : hovered
                    ? Color.FromArgb(65, 72, 82)
                    : BackColor;
            e.Graphics.Clear(background);
            e.Graphics.SmoothingMode =
                System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            float cx = ClientSize.Width / 2f;
            float cy = ClientSize.Height / 2f;
            using (Pen pen = new Pen(ForeColor, 2f))
            {
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                if (icon == OverlayButtonIcon.Minimize)
                {
                    e.Graphics.DrawLine(pen, cx - 5f, cy, cx + 5f, cy);
                }
                else if (icon == OverlayButtonIcon.Close)
                {
                    e.Graphics.DrawLine(pen, cx - 5f, cy - 5f, cx + 5f, cy + 5f);
                    e.Graphics.DrawLine(pen, cx + 5f, cy - 5f, cx - 5f, cy + 5f);
                }
                else
                {
                    RectangleF eye = new RectangleF(cx - 8f, cy - 5f, 16f, 10f);
                    e.Graphics.DrawEllipse(pen, eye);
                    using (SolidBrush pupil = new SolidBrush(ForeColor))
                        e.Graphics.FillEllipse(pupil, cx - 2.5f, cy - 2.5f, 5f, 5f);
                    if (EyeSlashed)
                        e.Graphics.DrawLine(pen, cx - 8f, cy + 7f, cx + 8f, cy - 7f);
                }
            }
        }
    }

    internal sealed class LightRangeControl : Control
    {
        internal sealed class LayoutSnapshot
        {
            internal Rectangle RangeTextBounds;
            internal Rectangle IsoTextBounds;
            internal int BarY;
            internal int MarkerRadius;
            internal int RequiredHeight;
            internal int RangeMeasuredHeight;
            internal int IsoMeasuredHeight;
        }

        private List<IsoBand> bands = new List<IsoBand>();
        private int lightValue = -1;
        private int activeIso = -1;
        private bool dragging;
        private bool interactive;
        private bool updatingReadableMinimumSize;
        private float visualScale = 1f;

        public bool Interactive
        {
            get { return interactive; }
            set
            {
                if (interactive == value)
                    return;
                interactive = value;
                UpdateReadableMinimumSize();
                Invalidate();
            }
        }

        public float VisualScale
        {
            get { return visualScale; }
            set
            {
                visualScale = Math.Max(1f, Math.Min(1.3f, value));
                UpdateReadableMinimumSize();
                Invalidate();
            }
        }

        public int LightValue
        {
            get { return lightValue; }
        }

        public event EventHandler LightValueChanged;

        public LightRangeControl()
        {
            BackColor = Color.White;
            AccessibleRole = AccessibleRole.Graphic;
            AccessibleName = "Licht naar ISO-bereiken";
            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateReadableMinimumSize();
        }

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            UpdateReadableMinimumSize();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateReadableMinimumSize();
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            Size preferred = base.GetPreferredSize(proposedSize);
            return new Size(
                Math.Max(preferred.Width, MinimumSize.Width),
                Math.Max(preferred.Height, MinimumSize.Height));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Interactive || e.Button != MouseButtons.Left)
                return;
            dragging = true;
            Capture = true;
            SetInteractiveLightValue(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (Interactive && dragging)
                SetInteractiveLightValue(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!Interactive || e.Button != MouseButtons.Left)
                return;
            SetInteractiveLightValue(e.X);
            dragging = false;
            Capture = false;
        }

        private void SetInteractiveLightValue(int mouseX)
        {
            float paintScale = GetPaintScale();
            int barLeft = (int)Math.Round(19 * paintScale);
            int barRight = Math.Max(barLeft + 1,
                ClientSize.Width - (int)Math.Round(19 * paintScale));
            int value = (int)Math.Round(
                100.0 * (mouseX - barLeft) / (barRight - barLeft));
            value = Math.Max(0, Math.Min(100, value));
            if (value == lightValue)
                return;
            lightValue = value;
            Invalidate();
            EventHandler handler = LightValueChanged;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        public void UpdateData(List<IsoBand> source, int light, int iso)
        {
            bands = new List<IsoBand>();
            foreach (IsoBand band in source)
                bands.Add(band.Clone());
            lightValue = light;
            activeIso = iso;
            AccessibleDescription = light >= 0 && iso > 0
                ? "Lichtwaarde " + light + " wordt ISO " + iso + "."
                : "Er is nog geen geldige lichtmeting.";
            UpdateReadableMinimumSize();
            Invalidate();
        }

        private void UpdateReadableMinimumSize()
        {
            if (bands.Count == 0 || updatingReadableMinimumSize)
                return;
            updatingReadableMinimumSize = true;
            Graphics measurementGraphics = IsHandleCreated
                ? CreateGraphics() : null;
            try
            {
                int widest = 0;
                int lower = 0;
                using (Font readable = new Font(
                    "Segoe UI", OverlayForm.MinimumReadableUiFontSize,
                    FontStyle.Bold))
                {
                    foreach (IsoBand band in bands)
                    {
                        widest = Math.Max(widest, MeasurePaintTextWidth(
                            measurementGraphics,
                            lower + "–" + band.MaximumLight, readable));
                        widest = Math.Max(widest, MeasurePaintTextWidth(
                            measurementGraphics,
                            "ISO " + band.Iso, readable));
                        lower = band.MaximumLight + 1;
                    }
                }
                float dpiScale = GetDpiScale();
                int horizontalInset = (int)Math.Ceiling(20 * dpiScale);
                int requiredWidth = bands.Count *
                    (widest + horizontalInset) + 2;
                int layoutWidth = Math.Max(
                    Math.Max(ClientSize.Width, MinimumSize.Width),
                    requiredWidth);
                int cellWidth = Math.Max(1,
                    (layoutWidth - 2) / bands.Count);
                int textWidth = Math.Max(1, cellWidth -
                    (int)Math.Ceiling(10 * GetPaintScale()));
                using (Font rangeFont = CreateReadablePaintFont(
                    9.5f * visualScale, FontStyle.Bold,
                    textWidth, false, measurementGraphics))
                using (Font isoFont = CreateReadablePaintFont(
                    9f * visualScale, FontStyle.Regular,
                    textWidth, true, measurementGraphics))
                {
                    LayoutSnapshot layout = CalculateVerticalLayout(
                        new Rectangle(1, 1,
                            Math.Max(1, layoutWidth - 2), 1),
                        rangeFont, isoFont, measurementGraphics);
                    Size required = new Size(
                        Math.Max(MinimumSize.Width, requiredWidth),
                        Math.Max(MinimumSize.Height,
                            layout.RequiredHeight));
                    if (MinimumSize != required)
                        MinimumSize = required;
                }
            }
            finally
            {
                if (measurementGraphics != null)
                    measurementGraphics.Dispose();
                updatingReadableMinimumSize = false;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode =
                System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            if (bands.Count == 0)
                return;

            Rectangle content = new Rectangle(1, 1,
                Math.Max(1, ClientSize.Width - 2),
                Math.Max(1, ClientSize.Height - 2));
            using (Pen border = new Pen(Color.FromArgb(218, 222, 229)))
                e.Graphics.DrawRectangle(border, content);

            int cellWidth = Math.Max(1, content.Width / bands.Count);
            int lower = 0;
            int activeIndex = -1;
            float paintScale = GetPaintScale();
            int textWidth = Math.Max(1,
                cellWidth - (int)Math.Round(8 * paintScale));
            using (Font rangeFont = CreateReadablePaintFont(
                9.5f * visualScale, FontStyle.Bold, textWidth, false,
                e.Graphics))
            using (Font isoFont = CreateReadablePaintFont(
                9f * visualScale, FontStyle.Regular, textWidth, true,
                e.Graphics))
            {
                LayoutSnapshot layout = CalculateVerticalLayout(
                    content, rangeFont, isoFont, e.Graphics);
                for (int index = 0; index < bands.Count; index++)
                {
                    IsoBand band = bands[index];
                    int left = content.Left + index * cellWidth;
                    int right = index == bands.Count - 1
                        ? content.Right
                        : left + cellWidth;
                    Rectangle cell = new Rectangle(left, content.Top,
                        Math.Max(1, right - left), content.Height);
                    bool active = lightValue >= lower &&
                        lightValue <= band.MaximumLight;
                    if (active)
                    {
                        activeIndex = index;
                        using (SolidBrush highlight = new SolidBrush(
                            Color.FromArgb(236, 245, 255)))
                            e.Graphics.FillRectangle(highlight, cell);
                    }
                    if (index > 0)
                    {
                        using (Pen divider = new Pen(
                            Color.FromArgb(218, 222, 229)))
                            e.Graphics.DrawLine(divider, left, content.Top,
                                left, content.Bottom);
                    }
                    Color rangeColor = active
                        ? Color.FromArgb(20, 102, 196)
                        : Color.FromArgb(62, 68, 77);
                    TextRenderer.DrawText(e.Graphics,
                        lower + "–" + band.MaximumLight, rangeFont,
                        new Rectangle(
                            left + (int)Math.Round(4 * paintScale),
                            layout.RangeTextBounds.Top,
                            Math.Max(1, right - left -
                                (int)Math.Round(8 * paintScale)),
                            layout.RangeTextBounds.Height),
                        rangeColor, TextFormatFlags.HorizontalCenter |
                        TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(e.Graphics,
                        "ISO " + band.Iso, isoFont,
                        new Rectangle(
                            left + (int)Math.Round(4 * paintScale),
                            layout.IsoTextBounds.Top,
                            Math.Max(1, right - left -
                                (int)Math.Round(8 * paintScale)),
                            layout.IsoTextBounds.Height),
                        active ? Color.FromArgb(20, 102, 196) :
                            Color.FromArgb(101, 108, 118),
                        TextFormatFlags.HorizontalCenter |
                        TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPadding);
                    lower = band.MaximumLight + 1;
                }

                int barY = layout.BarY;
                int barLeft = content.Left +
                    (int)Math.Round(18 * paintScale);
                int barRight = content.Right -
                    (int)Math.Round(18 * paintScale);
                using (Pen baseBar = new Pen(
                    Color.FromArgb(190, 196, 204), 5f * paintScale))
                {
                    baseBar.StartCap =
                        System.Drawing.Drawing2D.LineCap.Round;
                    baseBar.EndCap =
                        System.Drawing.Drawing2D.LineCap.Round;
                    e.Graphics.DrawLine(
                        baseBar, barLeft, barY, barRight, barY);
                }
                if (activeIndex >= 0)
                {
                    int segmentLeft = barLeft +
                        (barRight - barLeft) * activeIndex / bands.Count;
                    int segmentRight = barLeft +
                        (barRight - barLeft) *
                        (activeIndex + 1) / bands.Count;
                    using (Pen activeBar = new Pen(
                        Color.FromArgb(31, 111, 235), 5f * paintScale))
                    {
                        activeBar.StartCap =
                            System.Drawing.Drawing2D.LineCap.Round;
                        activeBar.EndCap =
                            System.Drawing.Drawing2D.LineCap.Round;
                        e.Graphics.DrawLine(activeBar, segmentLeft, barY,
                            segmentRight, barY);
                    }
                }
                if (lightValue >= 0)
                {
                    int markerX = barLeft + (int)Math.Round(
                        (barRight - barLeft) * Math.Max(0,
                            Math.Min(100, lightValue)) / 100.0);
                    int markerRadius = layout.MarkerRadius;
                    using (SolidBrush marker = new SolidBrush(
                        Color.FromArgb(31, 111, 235)))
                        e.Graphics.FillEllipse(marker,
                            markerX - markerRadius,
                            barY - markerRadius, markerRadius * 2,
                            markerRadius * 2);
                    using (Pen whiteRing = new Pen(
                        Color.White, 2f * paintScale))
                        e.Graphics.DrawEllipse(whiteRing,
                            markerX - markerRadius,
                            barY - markerRadius, markerRadius * 2,
                            markerRadius * 2);
                }
            }
        }

        internal LayoutSnapshot GetLayoutSnapshotForTest()
        {
            Rectangle content = new Rectangle(1, 1,
                Math.Max(1, ClientSize.Width - 2),
                Math.Max(1, ClientSize.Height - 2));
            int cellWidth = Math.Max(1,
                content.Width / Math.Max(1, bands.Count));
            int textWidth = Math.Max(1, cellWidth -
                (int)Math.Round(8 * GetPaintScale()));
            Graphics measurementGraphics = IsHandleCreated
                ? CreateGraphics() : null;
            try
            {
                using (Font rangeFont = CreateReadablePaintFont(
                    9.5f * visualScale, FontStyle.Bold,
                    textWidth, false, measurementGraphics))
                using (Font isoFont = CreateReadablePaintFont(
                    9f * visualScale, FontStyle.Regular,
                    textWidth, true, measurementGraphics))
                    return CalculateVerticalLayout(
                        content, rangeFont, isoFont,
                        measurementGraphics);
            }
            finally
            {
                if (measurementGraphics != null)
                    measurementGraphics.Dispose();
            }
        }

        private LayoutSnapshot CalculateVerticalLayout(
            Rectangle content, Font rangeFont, Font isoFont,
            IDeviceContext deviceContext)
        {
            float dpiScale = GetDpiScale();
            int rangeMeasuredHeight = MeasurePaintTextHeight(
                deviceContext, rangeFont);
            int isoMeasuredHeight = MeasurePaintTextHeight(
                deviceContext, isoFont);
            return CalculateVerticalLayoutCore(
                content, rangeMeasuredHeight, isoMeasuredHeight,
                dpiScale, visualScale, Interactive);
        }

        private int MeasurePaintTextHeight(
            IDeviceContext deviceContext, Font font)
        {
            if (deviceContext != null)
                return TextRenderer.MeasureText(
                    deviceContext, "Ag", font, Size.Empty,
                    TextFormatFlags.NoPadding |
                    TextFormatFlags.NoPrefix |
                    TextFormatFlags.SingleLine).Height;
            return (int)Math.Ceiling(font.GetHeight(DeviceDpi));
        }

        private int MeasurePaintTextWidth(
            IDeviceContext deviceContext, string value, Font font)
        {
            if (deviceContext != null)
                return TextRenderer.MeasureText(
                    deviceContext, value, font, Size.Empty,
                    TextFormatFlags.NoPadding |
                    TextFormatFlags.NoPrefix |
                    TextFormatFlags.SingleLine).Width;
            return TextRenderer.MeasureText(
                value, font, Size.Empty,
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoPrefix |
                TextFormatFlags.SingleLine).Width;
        }

        internal static LayoutSnapshot CalculateVerticalLayoutForTest(
            Rectangle content, int rangeMeasuredHeight,
            int isoMeasuredHeight, float dpiScale,
            float visualScale, bool interactive)
        {
            return CalculateVerticalLayoutCore(
                content, rangeMeasuredHeight, isoMeasuredHeight,
                dpiScale, visualScale, interactive);
        }

        private static LayoutSnapshot CalculateVerticalLayoutCore(
            Rectangle content, int rangeMeasuredHeight,
            int isoMeasuredHeight, float dpiScale,
            float visualScale, bool interactive)
        {
            dpiScale = Math.Max(1f, dpiScale);
            visualScale = Math.Max(1f, Math.Min(1.3f, visualScale));
            float paintScale = dpiScale * visualScale;
            int lineSafety = (int)Math.Ceiling(4 * dpiScale);
            int rangeHeight = rangeMeasuredHeight + lineSafety;
            int isoHeight = isoMeasuredHeight + lineSafety;
            int topPadding = (int)Math.Ceiling(12 * dpiScale);
            int lineGap = (int)Math.Ceiling(8 * dpiScale);
            int textToBarGap = (int)Math.Ceiling(12 * dpiScale);
            int bottomPadding = (int)Math.Ceiling(12 * dpiScale);
            int markerRadius = (int)Math.Ceiling(
                (interactive ? 12 : 9) * paintScale);

            Rectangle rangeBounds = new Rectangle(
                content.Left,
                content.Top + topPadding,
                content.Width, rangeHeight);
            Rectangle isoBounds = new Rectangle(
                content.Left,
                rangeBounds.Bottom + lineGap,
                content.Width, isoHeight);
            int minimumBarY = isoBounds.Bottom +
                textToBarGap + markerRadius;
            int preferredBarY = content.Bottom -
                bottomPadding - markerRadius;
            int barY = Math.Max(minimumBarY, preferredBarY);
            int requiredHeight = topPadding + rangeHeight +
                lineGap + isoHeight + textToBarGap +
                markerRadius * 2 + bottomPadding + 2;

            return new LayoutSnapshot {
                RangeTextBounds = rangeBounds,
                IsoTextBounds = isoBounds,
                BarY = barY,
                MarkerRadius = markerRadius,
                RequiredHeight = requiredHeight,
                RangeMeasuredHeight = rangeMeasuredHeight,
                IsoMeasuredHeight = isoMeasuredHeight
            };
        }

        private Font CreateReadablePaintFont(
            float preferredSize, FontStyle style,
            int availableWidth, bool isoText,
            IDeviceContext deviceContext)
        {
            float minimum = Math.Min(
                preferredSize, OverlayForm.MinimumReadableUiFontSize);
            for (float size = preferredSize;
                size >= minimum; size -= 0.5f)
            {
                Font candidate = new Font("Segoe UI", size, style);
                bool fits = true;
                int lower = 0;
                foreach (IsoBand band in bands)
                {
                    string value = isoText
                        ? "ISO " + band.Iso
                        : lower + "–" + band.MaximumLight;
                    int width = MeasurePaintTextWidth(
                        deviceContext, value, candidate);
                    if (width > availableWidth)
                    {
                        fits = false;
                        break;
                    }
                    lower = band.MaximumLight + 1;
                }
                if (fits)
                    return candidate;
                candidate.Dispose();
            }
            return new Font("Segoe UI", minimum, style);
        }

        private float GetPaintScale()
        {
            return visualScale * GetDpiScale();
        }

        private float GetDpiScale()
        {
            return Math.Max(1f, DeviceDpi / 96f);
        }
    }

    internal sealed class OverlayForm : Form
    {
        internal const float MinimumReadableUiFontSize = 8.5f;
        private const int HotkeyTogglePause = 1;
        private const int HotkeyEmergencyPause = 2;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const int WmHotkey = 0x0312;
        private const int WmActivateExistingInstance = 0x8001;
        private const uint WmCommand = 0x0111;
        private const int DarkroomSettingsToolId = 0x0294;
        private const int DarkroomNextSettingsToolId = 0x0296;
        private const int DarkroomCameraSettingsCommand = 33082;
        private const int DarkroomToolbarControlId = 4083;
        private const int DarkroomCommandStartBooth = 0x83F0;
        private static readonly string BuildTag = BuildInfo.Version;
        private const string DefaultCoverTitle = "Please wait!";
        private const string DefaultCoverMessage =
            "I'm fine-tuning the lighting\r\nso you look absolutely amazing!";
        private const int DefaultStabilitySeconds = 60;
        private const uint EventSystemForeground = 0x0003;
        private const uint EventSystemPopupStart = 0x0006;
        private const uint EventObjectShow = 0x8002;
        private const uint WinEventOutOfContext = 0x0000;
        private static readonly string PhoneHotspotSsid = BuildInfo.ApSsid;
        private static readonly string PhoneHotspotPassword = BuildInfo.ApPassword;
        private const string PhoneDashboardAddress = "http://192.168.9.1";
        private const uint ProcessVmRead = 0x0010;
        private const uint ProcessQueryInformation = 0x0400;
        private const uint MemCommit = 0x1000;
        private const uint MemPrivate = 0x20000;
        private const uint PageGuard = 0x0100;
        private const uint PageNoAccess = 0x0001;
        private const uint MainPageVtableRva = 0x00162bdc;
        private const uint MainFrameVtableRva = 0x00158f74;
        private const uint ToolbarVtableRva = 0x00151ea4;
        private const uint EventSettingsVtableRva = 0x00150ecc;

        private readonly SensorState sensor = new SensorState();
        private readonly System.Windows.Forms.Timer uiTimer;
        private System.Threading.Timer reconnectTimer;
        private System.Threading.Timer darkroomProbeTimer;
        private readonly NotifyIcon trayIcon;
        private readonly Label modeLabel;
        private readonly Button statusButton;
        private readonly Panel statusPanel;
        private readonly TableLayoutPanel statusPanelLayout;
        private readonly Label statusPanelTitle;
        private readonly Label statusPanelMessage;
        private readonly Button statusPanelToggleButton;
        private readonly Label lightLabel;
        private readonly Label targetIsoLabel;
        private readonly Label serialLabel;
        private readonly Label darkroomLabel;
        private readonly Label boothLabel;
        private readonly Label currentIsoLabel;
        private readonly Label countdownLabel;
        private readonly NumericUpDown stabilitySecondsInput;
        private readonly Label actionLabel;
        private readonly Panel progressFill;
        private readonly Button runActionButton;
        private readonly Button coverToggleButton;
        private readonly ComboBox mappingModeInput;
        private readonly Button mappingSettingsButton;
        private readonly Label mappingSummaryLabel;
        private readonly Label overallStatusLabel;
        private readonly Label activeRangeLabel;
        private readonly Label lastMeasuredLabel;
        private readonly Label profileNameLabel;
        private readonly Label detailsSummaryLabel;
        private readonly Button pauseButton;
        private readonly Button detailsToggleButton;
        private readonly Panel detailsPanel;
        private readonly TableLayoutPanel detailsLayout;
        private readonly Panel progressBack;
        private readonly LightRangeControl rangeControl;
        private readonly TableLayoutPanel metrics;
        private readonly FlowLayoutPanel dashboard;
        private readonly Panel mappingPanel;
        private readonly TableLayoutPanel mappingLayout;
        private readonly Label mappingTitleLabel;
        private readonly Panel profilePanel;
        private readonly TableLayoutPanel dashboardProfileLayout;
        private readonly Label profileCaptionLabel;
        private readonly Panel detailsHeader;
        private readonly TableLayoutPanel detailsHeaderLayout;
        private readonly TableLayoutPanel actionPanel;
        private readonly Panel mainHeaderPanel;
        private readonly Label mainTitleLabel;
        private readonly Panel mainHeaderDivider;
        private readonly Label versionFooterLabel;
        private readonly Button settingsButton;
        private IsoMappingForm settingsPage;
        private readonly ToolStripMenuItem trayStatusMenuItem;
        private readonly ToolStripMenuItem trayPauseMenuItem;
        private readonly ToolStripMenuItem trayApplyMenuItem;

        private SerialPort serial;
        private readonly object serialSync = new object();
        private int reconnectActive;
        private int serialFaulted;
        private volatile bool serialReady;
        private string activeSerialPort = "";
        private DateTime serialOpenedAt = DateTime.MinValue;
        private DateTime lastJvdpLineAt = DateTime.MinValue;
        private readonly Dictionary<string, DateTime> serialRetryAfter =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private bool paused;
        private volatile bool manualCoverVisible;
        private volatile bool manualActionRunning;
        private volatile bool manualActionFailed;
        private volatile bool currentActionAutomatic;
        private string manualActionStatus = "";
        private DateTime manualActionStatusUntil = DateTime.MinValue;
        private DateTime nextAutomaticAttemptAt = DateTime.MinValue;
        private int stabilitySeconds = DefaultStabilitySeconds;
        private string currentDarkroomIso = "Unknown";
        private DateTime currentIsoReadAt = DateTime.MinValue;
        private bool darkroomRunning;
        private bool boothMode;
        private int darkroomProcessId;
        private int lastAppliedTargetIso;
        private int lastAppliedDarkroomProcessId;
        private readonly string logPath;
        private readonly string positionPath;
        private readonly string sizePath;
        private readonly string stabilityPath;
        private readonly string coverTextPath;
        private readonly string automaticStatePath;
        private readonly List<ActionCoverForm> actionCovers = new List<ActionCoverForm>();
        private ManualCoverControlForm manualCoverControl;
        private readonly System.Windows.Forms.Timer coverGuardTimer;
        private readonly object coverGuardSync = new object();
        private readonly WinEventDelegate coverWinEventDelegate;
        private IntPtr coverSystemEventHook = IntPtr.Zero;
        private IntPtr coverObjectShowHook = IntPtr.Zero;
        private IntPtr[] guardedCoverHandles = new IntPtr[0];
        private IntPtr guardedManualControlHandle = IntPtr.Zero;
        private int coverRaiseActive;
        private readonly bool startInTray;
        private readonly bool layoutTestMode;
        private readonly bool headlessLayoutTestMode;
        private bool exitRequested;
        private volatile bool shuttingDown;
        private readonly object mappingSync = new object();
        private readonly object coverTextSync = new object();
        private readonly object logSync = new object();
        private readonly string mappingPath;
        private bool useCustomMapping;
        private List<IsoBand> customIsoBands;
        private bool updatingMappingControls;
        private int darkroomProbeActive;
        private bool useCustomCoverText;
        private string customCoverTitle = DefaultCoverTitle;
        private string customCoverMessage = DefaultCoverMessage;
        private readonly Dictionary<Control, float> responsiveFontSizes =
            new Dictionary<Control, float>();
        private readonly Dictionary<Control, float> textFitBaseFontSizes =
            new Dictionary<Control, float>();
        private float lastResponsiveScale = -1f;
        private float lastResponsiveDpiScale = -1f;
        private bool textFitActive;
        private bool mainChromeLayoutActive;

        private static readonly int[] SupportedIsoValues = {
            100, 125, 160, 200, 250, 320, 400, 500, 640, 800,
            1000, 1250, 1600, 2000, 2500, 3200, 4000, 5000,
            6400, 8000, 10000, 12800, 25600
        };

        public OverlayForm(bool startMinimizedToTray)
            : this(startMinimizedToTray, false)
        {
        }

        internal OverlayForm(
            bool startMinimizedToTray, bool enableLayoutTestMode)
            : this(startMinimizedToTray, enableLayoutTestMode, false)
        {
        }

        internal OverlayForm(
            bool startMinimizedToTray, bool enableLayoutTestMode,
            bool enableHeadlessLayoutTestMode)
        {
            layoutTestMode = enableLayoutTestMode;
            headlessLayoutTestMode = enableLayoutTestMode &&
                enableHeadlessLayoutTestMode;
            Text = "JvdP Lichtregeling";
            Icon applicationIcon = Icon.ExtractAssociatedIcon(
                Application.ExecutablePath);
            Icon = applicationIcon ?? SystemIcons.Information;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.Sizable;
            ClientSize = new Size(1240, 820);
            MinimumSize = SizeFromClientSize(new Size(1000, 720));
            StartPosition = FormStartPosition.Manual;
            MaximizeBox = true;
            MinimizeBox = true;
            TopMost = false;
            ShowInTaskbar = true;
            BackColor = Color.FromArgb(247, 248, 250);
            ForeColor = Color.FromArgb(31, 35, 40);
            Font = new Font("Segoe UI", 10, FontStyle.Regular);
            Opacity = 1.0;
            DoubleBuffered = true;
            startInTray = startMinimizedToTray;

            string localRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JvdP", "LightDarkroomOverlay");
            Directory.CreateDirectory(localRoot);
            logPath = Path.Combine(localRoot, "overlay.log");
            positionPath = Path.Combine(localRoot, "overlay-position.txt");
            sizePath = Path.Combine(localRoot, "overlay-size.txt");
            stabilityPath = Path.Combine(localRoot, "stability-seconds.txt");
            mappingPath = Path.Combine(localRoot, "iso-mapping.txt");
            coverTextPath = Path.Combine(localRoot, "fullpage-text.txt");
            automaticStatePath = Path.Combine(
                localRoot, "automatic-adjustment-state.txt");
            stabilitySeconds = LoadStabilitySeconds();
            paused = LoadAutomaticAdjustmentStopped();
            customIsoBands = CreateDefaultIsoBands();
            LoadIsoMappingSettings();
            LoadCoverTextSettings();
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Size preferredSize = new Size(
                Math.Min(1240, Math.Max(1000, area.Width - 48)),
                Math.Min(820, Math.Max(720, area.Height - 48)));
            ClientSize = preferredSize;
            Size savedSize;
            if (TryLoadOverlaySize(out savedSize))
                ClientSize = new Size(
                    Math.Min(Math.Max(preferredSize.Width, savedSize.Width),
                        Math.Max(1000, area.Width - 24)),
                    Math.Min(Math.Max(preferredSize.Height, savedSize.Height),
                        Math.Max(720, area.Height - 24)));
            Point initialLocation = new Point(
                area.Left + (area.Width - Width) / 2,
                area.Top + (area.Height - Height) / 2);
            Point savedLocation;
            if (TryLoadOverlayPosition(out savedLocation))
                initialLocation = savedLocation;
            initialLocation = new Point(
                Math.Max(area.Left,
                    Math.Min(initialLocation.X, area.Right - Width)),
                Math.Max(area.Top,
                    Math.Min(initialLocation.Y, area.Bottom - Height)));
            Location = initialLocation;
            WindowState = layoutTestMode
                ? FormWindowState.Normal
                : FormWindowState.Maximized;

            mainHeaderPanel = new Panel();
            mainHeaderPanel.BackColor = Color.White;
            mainHeaderPanel.SetBounds(0, 0, ClientSize.Width, 64);
            mainHeaderPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left |
                AnchorStyles.Right;
            Controls.Add(mainHeaderPanel);

            mainTitleLabel = MakeLabel(
                "JvdP Lichtregeling", 18, FontStyle.Bold,
                Color.FromArgb(18, 22, 27));
            // The header is measured explicitly. AutoSize can re-apply a
            // stale pre-DPI preferred width when an embedded settings form
            // creates its handle, which used to leave only part of the title.
            mainTitleLabel.AutoSize = false;
            mainTitleLabel.SetBounds(24, 8, 270, 48);
            mainHeaderPanel.Controls.Add(mainTitleLabel);

            settingsButton = MakeLightButton("Instellingen", false);
            settingsButton.AccessibleName = "Profielinstellingen openen";
            settingsButton.SetBounds(
                mainHeaderPanel.Width - 264, 12, 240, 40);
            settingsButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            settingsButton.Click += delegate
            {
                if (settingsPage != null && !settingsPage.IsDisposed)
                {
                    settingsPage.DialogResult = DialogResult.Cancel;
                    settingsPage.Close();
                }
                else
                {
                    ShowIsoMappingSettings();
                }
            };
            mainHeaderPanel.Controls.Add(settingsButton);

            modeLabel = MakeLabel(
                "Automatisch actief", 10, FontStyle.Bold,
                Color.FromArgb(31, 111, 235));
            modeLabel.TextAlign = ContentAlignment.MiddleRight;
            modeLabel.Visible = false;

            overallStatusLabel = MakeLabel(
                "Bezig met verbinden", 10, FontStyle.Bold,
                Color.FromArgb(137, 87, 0));
            overallStatusLabel.TextAlign = ContentAlignment.MiddleRight;
            overallStatusLabel.AccessibleName = "Algemene systeemstatus";
            overallStatusLabel.Visible = false;

            statusButton = MakeLightButton("Bezig met verbinden", false);
            statusButton.AccessibleName = "Status en bediening openen";
            statusButton.SetBounds(
                (mainHeaderPanel.Width - 220) / 2, 12, 220, 40);
            statusButton.Anchor = AnchorStyles.Top;
            statusButton.Click += delegate { ToggleStatusPanel(); };
            mainHeaderPanel.Controls.Add(statusButton);

            mainHeaderDivider = new Panel();
            mainHeaderDivider.BackColor = Color.FromArgb(218, 222, 229);
            mainHeaderDivider.SetBounds(
                0, 63, mainHeaderPanel.Width, 1);
            mainHeaderDivider.Anchor = AnchorStyles.Left | AnchorStyles.Right |
                AnchorStyles.Bottom;
            mainHeaderPanel.Controls.Add(mainHeaderDivider);
            mainHeaderPanel.SizeChanged += delegate { LayoutMainChrome(); };
            mainTitleLabel.LocationChanged +=
                delegate { LayoutMainChrome(); };
            mainTitleLabel.SizeChanged +=
                delegate { LayoutMainChrome(); };
            settingsButton.TextChanged += delegate { LayoutMainChrome(); };
            settingsButton.FontChanged += delegate { LayoutMainChrome(); };
            settingsButton.LocationChanged +=
                delegate { LayoutMainChrome(); };
            settingsButton.SizeChanged += delegate { LayoutMainChrome(); };
            statusButton.TextChanged += delegate { LayoutMainChrome(); };
            statusButton.FontChanged += delegate { LayoutMainChrome(); };
            statusButton.LocationChanged +=
                delegate { LayoutMainChrome(); };
            statusButton.SizeChanged += delegate { LayoutMainChrome(); };

            ResizeEnd += delegate
            {
                SaveOverlayPosition();
                SaveOverlaySize();
            };
            Resize += delegate
            {
                HideInTrayWhenMinimized();
                LayoutMainChrome();
                PositionStatusPanel();
                ApplyResponsiveTypography();
            };

            dashboard = new FlowLayoutPanel();
            dashboard.FlowDirection = FlowDirection.TopDown;
            dashboard.WrapContents = false;
            dashboard.AutoScroll = true;
            dashboard.Padding = new Padding(24, 18, 24, 18);
            dashboard.BackColor = BackColor;
            dashboard.SetBounds(0, 64, ClientSize.Width,
                Math.Max(1, ClientSize.Height - 92));
            dashboard.Anchor = AnchorStyles.Top | AnchorStyles.Bottom |
                AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(dashboard);

            versionFooterLabel = MakeLabel(
                BuildVersionFooterText(localRoot), 8.5f, FontStyle.Regular,
                Color.FromArgb(99, 108, 118));
            versionFooterLabel.TextAlign = ContentAlignment.MiddleRight;
            versionFooterLabel.SetBounds(24, ClientSize.Height - 28,
                ClientSize.Width - 48, 24);
            versionFooterLabel.Anchor = AnchorStyles.Bottom |
                AnchorStyles.Left | AnchorStyles.Right;
            versionFooterLabel.AccessibleName =
                "Geïnstalleerde softwareversie";
            versionFooterLabel.AccessibleDescription =
                "Datum en tijd waarop deze versie is geïnstalleerd.";
            Controls.Add(versionFooterLabel);

            int sectionWidth = Math.Max(640, dashboard.ClientSize.Width - 68);
            mappingPanel = MakeLightSurface(sectionWidth, 1);
            mappingPanel.AutoSize = true;
            mappingPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            mappingPanel.AccessibleName =
                "Live lichtwaarde en ISO-overzicht";
            mappingPanel.Margin = new Padding(0, 0, 0, 12);
            dashboard.Controls.Add(mappingPanel);

            mappingLayout = new TableLayoutPanel();
            mappingLayout.AutoSize = true;
            mappingLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            mappingLayout.Dock = DockStyle.Top;
            mappingLayout.Margin = Padding.Empty;
            mappingLayout.Padding = new Padding(20, 12, 20, 16);
            mappingLayout.ColumnCount = 1;
            mappingLayout.RowCount = 3;
            mappingLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            mappingLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            mappingLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            mappingLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            mappingPanel.Controls.Add(mappingLayout);

            TableLayoutPanel mappingHeader = new TableLayoutPanel();
            mappingHeader.AutoSize = true;
            mappingHeader.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            mappingHeader.Dock = DockStyle.Fill;
            mappingHeader.Margin = Padding.Empty;
            mappingHeader.ColumnCount = 2;
            mappingHeader.RowCount = 1;
            mappingHeader.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            mappingHeader.ColumnStyles.Add(
                new ColumnStyle(SizeType.AutoSize));
            mappingHeader.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            mappingLayout.Controls.Add(mappingHeader, 0, 0);

            mappingTitleLabel = MakeLabel(
                "Live lichtwaarde → Doel-ISO", 13, FontStyle.Regular,
                Color.FromArgb(31, 35, 40));
            mappingTitleLabel.AutoSize = true;
            mappingTitleLabel.AutoEllipsis = false;
            mappingTitleLabel.Dock = DockStyle.Fill;
            mappingTitleLabel.Margin = new Padding(0, 0, 16, 0);
            mappingHeader.Controls.Add(mappingTitleLabel, 0, 0);

            lastMeasuredLabel = MakeLabel(
                "Wachten op eerste meting", 9, FontStyle.Regular,
                Color.FromArgb(99, 108, 118));
            lastMeasuredLabel.AutoSize = true;
            lastMeasuredLabel.AutoEllipsis = false;
            lastMeasuredLabel.TextAlign = ContentAlignment.MiddleRight;
            lastMeasuredLabel.Anchor = AnchorStyles.Right;
            lastMeasuredLabel.Margin = Padding.Empty;
            mappingHeader.Controls.Add(lastMeasuredLabel, 1, 0);

            rangeControl = new LightRangeControl();
            rangeControl.Dock = DockStyle.Fill;
            rangeControl.MinimumSize = new Size(0, 116);
            rangeControl.Margin = new Padding(0, 10, 0, 12);
            mappingLayout.Controls.Add(rangeControl, 0, 1);

            metrics = new TableLayoutPanel();
            metrics.AutoSize = true;
            metrics.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            metrics.Dock = DockStyle.Fill;
            metrics.Margin = Padding.Empty;
            metrics.Padding = new Padding(0, 2, 0, 4);
            metrics.AccessibleName = "Actuele lichtwaarden";
            metrics.ColumnCount = 3;
            metrics.RowCount = 2;
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            metrics.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            metrics.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            mappingLayout.Controls.Add(metrics, 0, 2);

            Label lightCaption = MakeLabel(
                "Lichtwaarde", 10, FontStyle.Regular,
                Color.FromArgb(54, 65, 82));
            lightCaption.AutoSize = true;
            lightCaption.AutoEllipsis = false;
            lightCaption.TextAlign = ContentAlignment.MiddleCenter;
            metrics.Controls.Add(lightCaption, 0, 0);
            lightCaption.Dock = DockStyle.Fill;
            lightCaption.Margin = new Padding(4, 0, 4, 4);

            Label rangeCaption = MakeLabel(
                "Actief bereik", 10, FontStyle.Regular,
                Color.FromArgb(54, 65, 82));
            rangeCaption.AutoSize = true;
            rangeCaption.AutoEllipsis = false;
            rangeCaption.TextAlign = ContentAlignment.MiddleCenter;
            metrics.Controls.Add(rangeCaption, 1, 0);
            rangeCaption.Dock = DockStyle.Fill;
            rangeCaption.Margin = new Padding(4, 0, 4, 4);

            Label targetCaption = MakeLabel(
                "Doel-ISO", 10, FontStyle.Regular,
                Color.FromArgb(54, 65, 82));
            targetCaption.AutoSize = true;
            targetCaption.AutoEllipsis = false;
            targetCaption.TextAlign = ContentAlignment.MiddleCenter;
            metrics.Controls.Add(targetCaption, 2, 0);
            targetCaption.Dock = DockStyle.Fill;
            targetCaption.Margin = new Padding(4, 0, 4, 4);

            lightLabel = MakeLabel(
                "—", 30, FontStyle.Bold, Color.FromArgb(31, 111, 235));
            lightLabel.AutoSize = true;
            lightLabel.AutoEllipsis = false;
            lightLabel.TextAlign = ContentAlignment.MiddleCenter;
            lightLabel.Dock = DockStyle.Fill;
            lightLabel.Margin = new Padding(4, 0, 4, 6);
            lightLabel.AccessibleName = "Actuele lichtwaarde";
            metrics.Controls.Add(lightLabel, 0, 1);

            activeRangeLabel = MakeLabel(
                "—", 23, FontStyle.Bold, Color.FromArgb(31, 111, 235));
            activeRangeLabel.AutoSize = true;
            activeRangeLabel.AutoEllipsis = false;
            activeRangeLabel.TextAlign = ContentAlignment.MiddleCenter;
            activeRangeLabel.Dock = DockStyle.Fill;
            activeRangeLabel.Margin = new Padding(4, 0, 4, 6);
            activeRangeLabel.AccessibleName = "Actief lichtbereik";
            metrics.Controls.Add(activeRangeLabel, 1, 1);

            targetIsoLabel = MakeLabel(
                "—", 30, FontStyle.Bold, Color.FromArgb(31, 111, 235));
            targetIsoLabel.AutoSize = true;
            targetIsoLabel.AutoEllipsis = false;
            targetIsoLabel.TextAlign = ContentAlignment.MiddleCenter;
            targetIsoLabel.Dock = DockStyle.Fill;
            targetIsoLabel.Margin = new Padding(4, 0, 4, 6);
            targetIsoLabel.AccessibleName = "Doel ISO";
            metrics.Controls.Add(targetIsoLabel, 2, 1);

            mappingModeInput = new ComboBox();
            mappingModeInput.Items.Add("Standaard · alle booths");
            mappingModeInput.Items.Add("Eigen · deze booth");
            mappingModeInput.Visible = false;
            mappingSummaryLabel = MakeLabel(
                "", 9, FontStyle.Regular, Color.FromArgb(99, 108, 118));
            mappingSummaryLabel.Visible = false;

            profilePanel = MakeLightSurface(sectionWidth, 1);
            profilePanel.AutoSize = true;
            profilePanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            profilePanel.Margin = new Padding(0, 0, 0, 10);
            dashboard.Controls.Add(profilePanel);

            dashboardProfileLayout = new TableLayoutPanel();
            dashboardProfileLayout.AutoSize = true;
            dashboardProfileLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            dashboardProfileLayout.Dock = DockStyle.Top;
            dashboardProfileLayout.Margin = Padding.Empty;
            dashboardProfileLayout.Padding = new Padding(18, 10, 18, 10);
            dashboardProfileLayout.ColumnCount = 2;
            dashboardProfileLayout.RowCount = 2;
            dashboardProfileLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            dashboardProfileLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.AutoSize));
            dashboardProfileLayout.RowStyles.Add(
                new RowStyle(SizeType.AutoSize));
            dashboardProfileLayout.RowStyles.Add(
                new RowStyle(SizeType.AutoSize));
            profilePanel.Controls.Add(dashboardProfileLayout);

            profileCaptionLabel = MakeLabel(
                "Actief profiel", 9, FontStyle.Regular,
                Color.FromArgb(99, 108, 118));
            profileCaptionLabel.AutoSize = true;
            profileCaptionLabel.AutoEllipsis = false;
            profileCaptionLabel.Dock = DockStyle.Fill;
            profileCaptionLabel.Margin = new Padding(0, 0, 16, 2);
            dashboardProfileLayout.Controls.Add(profileCaptionLabel, 0, 0);

            profileNameLabel = MakeLabel(
                "Standaard · alle booths", 11, FontStyle.Bold,
                Color.FromArgb(31, 35, 40));
            profileNameLabel.AutoSize = true;
            profileNameLabel.AutoEllipsis = false;
            profileNameLabel.Dock = DockStyle.Fill;
            profileNameLabel.Margin = new Padding(0, 0, 16, 0);
            dashboardProfileLayout.Controls.Add(profileNameLabel, 0, 1);

            mappingSettingsButton = MakeLightButton("Profiel bekijken", false);
            mappingSettingsButton.AutoSize = true;
            mappingSettingsButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            mappingSettingsButton.MinimumSize = new Size(154, 42);
            mappingSettingsButton.Anchor = AnchorStyles.Right;
            mappingSettingsButton.Margin = Padding.Empty;
            mappingSettingsButton.Click += delegate { ShowIsoMappingSettings(); };
            dashboardProfileLayout.Controls.Add(mappingSettingsButton, 1, 0);
            dashboardProfileLayout.SetRowSpan(mappingSettingsButton, 2);

            detailsHeader = MakeLightSurface(sectionWidth, 1);
            detailsHeader.AutoSize = true;
            detailsHeader.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            detailsHeader.Margin = new Padding(0, 0, 0, 4);
            dashboard.Controls.Add(detailsHeader);

            detailsHeaderLayout = new TableLayoutPanel();
            detailsHeaderLayout.AutoSize = true;
            detailsHeaderLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            detailsHeaderLayout.Dock = DockStyle.Top;
            detailsHeaderLayout.Margin = Padding.Empty;
            detailsHeaderLayout.Padding = new Padding(10, 5, 18, 5);
            detailsHeaderLayout.ColumnCount = 2;
            detailsHeaderLayout.RowCount = 1;
            detailsHeaderLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            detailsHeaderLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.AutoSize));
            detailsHeaderLayout.RowStyles.Add(
                new RowStyle(SizeType.AutoSize));
            detailsHeader.Controls.Add(detailsHeaderLayout);

            detailsToggleButton = MakeLightButton(
                "Verbindingen en technische details", false);
            detailsToggleButton.FlatAppearance.BorderSize = 0;
            detailsToggleButton.TextAlign = ContentAlignment.MiddleLeft;
            detailsToggleButton.AutoSize = true;
            detailsToggleButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            detailsToggleButton.MinimumSize = new Size(280, 42);
            detailsToggleButton.Anchor = AnchorStyles.Left;
            detailsToggleButton.Margin = new Padding(0, 0, 16, 0);
            detailsToggleButton.AccessibleDescription =
                "Toont of verbergt technische verbindingsinformatie.";
            detailsHeaderLayout.Controls.Add(detailsToggleButton, 0, 0);

            detailsSummaryLabel = MakeLabel(
                "Sensor zoeken · Darkroom zoeken", 9, FontStyle.Regular,
                Color.FromArgb(99, 108, 118));
            detailsSummaryLabel.AutoSize = true;
            detailsSummaryLabel.AutoEllipsis = false;
            detailsSummaryLabel.TextAlign = ContentAlignment.MiddleRight;
            detailsSummaryLabel.Anchor = AnchorStyles.Right;
            detailsSummaryLabel.Margin = Padding.Empty;
            detailsHeaderLayout.Controls.Add(detailsSummaryLabel, 1, 0);

            detailsPanel = MakeLightSurface(sectionWidth, 1);
            detailsPanel.AutoSize = true;
            detailsPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            detailsPanel.Margin = new Padding(0, 0, 0, 8);
            detailsPanel.Visible = false;
            dashboard.Controls.Add(detailsPanel);

            detailsLayout = new TableLayoutPanel();
            detailsLayout.AutoSize = true;
            detailsLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            detailsLayout.Dock = DockStyle.Top;
            detailsLayout.Padding = new Padding(18, 10, 18, 10);
            detailsLayout.ColumnCount = 2;
            detailsLayout.RowCount = 6;
            detailsLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.AutoSize));
            detailsLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            for (int detailRow = 0; detailRow < 5; detailRow++)
                detailsLayout.RowStyles.Add(
                    new RowStyle(SizeType.AutoSize));
            detailsLayout.RowStyles.Add(
                new RowStyle(SizeType.Absolute, 20));
            detailsPanel.Controls.Add(detailsLayout);

            Label sensorCaption = MakeLightDetailCaption("Lichtsensor");
            detailsLayout.Controls.Add(sensorCaption, 0, 0);
            serialLabel = MakeLabel(
                "Sensor zoeken", 10, FontStyle.Bold,
                Color.FromArgb(137, 87, 0));
            ConfigureDetailValueLabel(serialLabel);
            detailsLayout.Controls.Add(serialLabel, 1, 0);

            Label darkroomCaption = MakeLightDetailCaption("Darkroom");
            detailsLayout.Controls.Add(darkroomCaption, 0, 1);
            darkroomLabel = MakeLabel(
                "Niet actief", 10, FontStyle.Bold,
                Color.FromArgb(186, 36, 36));
            ConfigureDetailValueLabel(darkroomLabel);
            detailsLayout.Controls.Add(darkroomLabel, 1, 1);

            Label automaticCaption = MakeLightDetailCaption(
                "Automatisch aanpassen");
            detailsLayout.Controls.Add(automaticCaption, 0, 2);
            boothLabel = MakeLabel(
                "Niet actief", 10, FontStyle.Bold,
                Color.FromArgb(186, 36, 36));
            ConfigureDetailValueLabel(boothLabel);
            detailsLayout.Controls.Add(boothLabel, 1, 2);

            Label currentIsoCaption = MakeLightDetailCaption(
                "ISO in Darkroom");
            detailsLayout.Controls.Add(currentIsoCaption, 0, 3);
            currentIsoLabel = MakeLabel(
                "Onbekend", 10, FontStyle.Bold,
                Color.FromArgb(99, 108, 118));
            ConfigureDetailValueLabel(currentIsoLabel);
            detailsLayout.Controls.Add(currentIsoLabel, 1, 3);

            Label stabilityCaption = MakeLabel(
                "Wachttijd vóór aanpassen", 9, FontStyle.Regular,
                Color.FromArgb(99, 108, 118));
            stabilityCaption.AutoSize = true;
            stabilityCaption.AutoEllipsis = false;
            stabilityCaption.Dock = DockStyle.Fill;
            stabilityCaption.Margin = new Padding(0, 6, 20, 6);
            detailsLayout.Controls.Add(stabilityCaption, 0, 4);

            FlowLayoutPanel stabilityFlow = new FlowLayoutPanel();
            stabilityFlow.AutoSize = true;
            stabilityFlow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            stabilityFlow.Dock = DockStyle.Fill;
            stabilityFlow.FlowDirection = FlowDirection.LeftToRight;
            stabilityFlow.WrapContents = true;
            stabilityFlow.Margin = Padding.Empty;
            stabilitySecondsInput = new NumericUpDown();
            stabilitySecondsInput.Minimum = 5;
            stabilitySecondsInput.Maximum = 300;
            stabilitySecondsInput.Increment = 5;
            stabilitySecondsInput.Value = stabilitySeconds;
            stabilitySecondsInput.Font = new Font("Segoe UI", 10, FontStyle.Regular);
            stabilitySecondsInput.ForeColor = Color.FromArgb(31, 35, 40);
            stabilitySecondsInput.BackColor = Color.White;
            stabilitySecondsInput.BorderStyle = BorderStyle.FixedSingle;
            stabilitySecondsInput.TextAlign = HorizontalAlignment.Center;
            stabilitySecondsInput.AutoSize = true;
            stabilitySecondsInput.MinimumSize = new Size(72, 0);
            stabilitySecondsInput.Height = stabilitySecondsInput.PreferredHeight;
            stabilitySecondsInput.Margin = new Padding(0, 2, 14, 4);
            stabilitySecondsInput.AccessibleName =
                "Wachttijd voor automatisch aanpassen in seconden";
            stabilitySecondsInput.ValueChanged += delegate
            {
                stabilitySeconds = (int)stabilitySecondsInput.Value;
                SaveStabilitySeconds();
                RefreshUi();
            };
            stabilityFlow.Controls.Add(stabilitySecondsInput);
            countdownLabel = MakeLabel(
                "0,0 / 60,0 sec", 9, FontStyle.Regular,
                Color.FromArgb(99, 108, 118));
            countdownLabel.AutoSize = true;
            countdownLabel.AutoEllipsis = false;
            countdownLabel.Margin = new Padding(0, 7, 0, 4);
            stabilityFlow.Controls.Add(countdownLabel);
            detailsLayout.Controls.Add(stabilityFlow, 1, 4);

            progressBack = new Panel();
            progressBack.BackColor = Color.FromArgb(218, 222, 229);
            progressBack.Dock = DockStyle.Fill;
            progressBack.MinimumSize = new Size(1, 6);
            progressBack.Margin = new Padding(0, 7, 0, 7);
            detailsLayout.Controls.Add(progressBack, 0, 5);
            detailsLayout.SetColumnSpan(progressBack, 2);
            progressFill = new Panel();
            progressFill.BackColor = Color.FromArgb(31, 111, 235);
            progressFill.Dock = DockStyle.Left;
            progressFill.Width = 0;
            progressBack.Controls.Add(progressFill);

            detailsToggleButton.Click += delegate
            {
                detailsPanel.Visible = !detailsPanel.Visible;
                detailsToggleButton.Text = detailsPanel.Visible
                    ? "Technische details verbergen"
                    : "Verbindingen en technische details";
                detailsToggleButton.AccessibleDescription = detailsPanel.Visible
                    ? "Technische verbindingsinformatie is zichtbaar."
                    : "Technische verbindingsinformatie is verborgen.";
            };

            actionLabel = MakeLabel(
                "Wachten op een lichtmeting van de sensor.", 10,
                FontStyle.Regular, Color.FromArgb(71, 78, 88));
            actionLabel.AutoSize = true;
            actionLabel.AutoEllipsis = false;
            actionLabel.MinimumSize = new Size(sectionWidth, 0);
            actionLabel.MaximumSize = new Size(sectionWidth, 0);
            actionLabel.Margin = new Padding(0, 0, 0, 8);
            actionLabel.TextAlign = ContentAlignment.MiddleLeft;
            actionLabel.AccessibleName = "Actuele activiteit";
            dashboard.Controls.Add(actionLabel);

            actionPanel = new TableLayoutPanel();
            actionPanel.AutoSize = true;
            actionPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            actionPanel.ColumnCount = 2;
            actionPanel.RowCount = 1;
            actionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            actionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            actionPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            actionPanel.MinimumSize = new Size(sectionWidth, 0);
            actionPanel.MaximumSize = new Size(sectionWidth, 0);
            actionPanel.Margin = new Padding(0);
            dashboard.Controls.Add(actionPanel);

            pauseButton = MakeLightButton(
                "Automatisch aanpassen stoppen", false);
            pauseButton.AutoSize = true;
            pauseButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            pauseButton.MinimumSize = new Size(1, 52);
            pauseButton.Dock = DockStyle.Fill;
            pauseButton.Margin = new Padding(0, 0, 6, 0);
            pauseButton.Click += delegate { TogglePause(); };
            actionPanel.Controls.Add(pauseButton, 0, 0);

            runActionButton = MakeLightButton("Doel-ISO nu toepassen", true);
            runActionButton.AutoSize = true;
            runActionButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            runActionButton.MinimumSize = new Size(1, 52);
            runActionButton.Dock = DockStyle.Fill;
            runActionButton.Margin = new Padding(6, 0, 0, 0);
            runActionButton.Click += delegate { StartTargetIsoAction(false); };
            actionPanel.Controls.Add(runActionButton, 1, 0);

            coverToggleButton = new Button();
            coverToggleButton.Visible = false;

            dashboard.SizeChanged += delegate
            {
                LayoutDashboardWidths();
            };
            UpdateMappingControls();

            statusPanel = MakeLightSurface(500, 270);
            statusPanel.Visible = false;
            statusPanel.AutoScroll = true;
            statusPanel.Padding = Padding.Empty;
            statusPanel.AccessibleName = "Statuspaneel";
            Controls.Add(statusPanel);

            statusPanelLayout = new TableLayoutPanel();
            statusPanelLayout.AutoSize = true;
            statusPanelLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            statusPanelLayout.Dock = DockStyle.Top;
            statusPanelLayout.Padding = new Padding(22, 14, 22, 20);
            statusPanelLayout.ColumnCount = 1;
            statusPanelLayout.RowCount = 3;
            statusPanelLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            statusPanelLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            statusPanelLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            statusPanelLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            statusPanel.Controls.Add(statusPanelLayout);

            TableLayoutPanel statusHeader = new TableLayoutPanel();
            statusHeader.AutoSize = true;
            statusHeader.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            statusHeader.Dock = DockStyle.Fill;
            statusHeader.ColumnCount = 2;
            statusHeader.RowCount = 1;
            statusHeader.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            statusHeader.ColumnStyles.Add(
                new ColumnStyle(SizeType.AutoSize));
            statusHeader.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            statusHeader.Margin = new Padding(0, 0, 0, 8);

            statusPanelTitle = MakeLabel(
                "Systeemstatus", 17, FontStyle.Bold,
                Color.FromArgb(31, 35, 40));
            statusPanelTitle.AutoSize = true;
            statusPanelTitle.AutoEllipsis = false;
            statusPanelTitle.Dock = DockStyle.Fill;
            statusPanelTitle.Margin = new Padding(0, 4, 12, 0);
            statusHeader.Controls.Add(statusPanelTitle, 0, 0);

            Button closeStatusButton = MakeLightButton("×", false);
            closeStatusButton.Font = new Font("Segoe UI", 16, FontStyle.Regular);
            closeStatusButton.AccessibleName = "Status sluiten";
            closeStatusButton.AutoSize = true;
            closeStatusButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            closeStatusButton.MinimumSize = new Size(42, 42);
            closeStatusButton.Padding = Padding.Empty;
            closeStatusButton.Margin = Padding.Empty;
            closeStatusButton.Click += delegate { statusPanel.Visible = false; };
            statusHeader.Controls.Add(closeStatusButton, 1, 0);
            statusPanelLayout.Controls.Add(statusHeader, 0, 0);

            statusPanelMessage = MakeLabel(
                "", 10, FontStyle.Regular, Color.FromArgb(71, 78, 88));
            statusPanelMessage.AutoSize = true;
            statusPanelMessage.AutoEllipsis = false;
            statusPanelMessage.Dock = DockStyle.Fill;
            statusPanelMessage.MaximumSize = new Size(456, 0);
            statusPanelMessage.Margin = new Padding(0, 0, 0, 18);
            statusPanelLayout.Controls.Add(statusPanelMessage, 0, 1);

            statusPanelToggleButton = MakeLightButton(
                "Automatisch aanpassen stoppen", false);
            statusPanelToggleButton.AutoSize = true;
            statusPanelToggleButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            statusPanelToggleButton.Dock = DockStyle.Fill;
            statusPanelToggleButton.MinimumSize = new Size(1, 50);
            statusPanelToggleButton.Padding = new Padding(16, 0, 16, 0);
            statusPanelToggleButton.Margin = Padding.Empty;
            statusPanelToggleButton.Click += delegate { TogglePause(); };
            statusPanelLayout.Controls.Add(statusPanelToggleButton, 0, 2);
            statusPanelMessage.TextChanged += delegate
            {
                PositionStatusPanel();
            };
            statusPanelToggleButton.TextChanged += delegate
            {
                PositionStatusPanel();
            };
            PositionStatusPanel();

            trayIcon = new NotifyIcon();
            trayIcon.Icon = Icon;
            trayIcon.Text = "JvdP Lichtregeling";
            trayIcon.Visible = !layoutTestMode;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = new Font("Segoe UI", 10, FontStyle.Regular);
            trayStatusMenuItem = new ToolStripMenuItem("Bezig met verbinden");
            trayStatusMenuItem.Enabled = false;
            menu.Items.Add(trayStatusMenuItem);
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem openMenuItem = new ToolStripMenuItem(
                "Dashboard openen", null, delegate { ShowMainWindow(); });
            openMenuItem.Font = new Font(openMenuItem.Font, FontStyle.Bold);
            menu.Items.Add(openMenuItem);
            trayPauseMenuItem = new ToolStripMenuItem(
                "Automatisch aanpassen stoppen", null,
                delegate { TogglePause(); });
            menu.Items.Add(trayPauseMenuItem);
            trayApplyMenuItem = new ToolStripMenuItem(
                "Doel-ISO toepassen", null,
                delegate { StartTargetIsoAction(false); });
            trayApplyMenuItem.Enabled = false;
            menu.Items.Add(trayApplyMenuItem);
            menu.Items.Add("Profielinstellingen", null,
                delegate { ShowIsoMappingSettings(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Afsluiten", null, delegate { ExitApplication(); });
            trayIcon.ContextMenuStrip = menu;
            trayIcon.DoubleClick += delegate { ShowMainWindow(); };

            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 500;
            uiTimer.Tick += delegate { RefreshUi(); };
            if (!layoutTestMode)
                uiTimer.Start();

            coverWinEventDelegate = CoverWinEvent;
            coverGuardTimer = new System.Windows.Forms.Timer();
            coverGuardTimer.Interval = 250;
            coverGuardTimer.Tick += delegate { RaiseFullscreenCovers(); };

            CaptureResponsiveFonts(dashboard);
            CaptureTextFitFonts(this);
            LayoutMainChrome();
            ApplyResponsiveTypography();

            Shown += delegate
            {
                if (layoutTestMode)
                    return;
                RegisterHotKey(Handle, HotkeyTogglePause, ModControl | ModAlt, (uint)Keys.F9);
                RegisterHotKey(Handle, HotkeyEmergencyPause, ModControl | ModAlt, (uint)Keys.F12);
                StartBackgroundMonitors();
                Log("Overlay started; build=" + BuildTag +
                    "; automatic Darkroom actions " +
                    (paused ? "stopped" : "started") + "; stability=" +
                    stabilitySeconds + " seconds.");
                if (startInTray)
                    BeginInvoke(new MethodInvoker(HideInTray));
            };

            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (layoutTestMode)
                {
                    uiTimer.Stop();
                    shuttingDown = true;
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                    return;
                }
                Log("Window close requested; reason=" +
                    args.CloseReason + "; exitRequested=" +
                    exitRequested + ".");
                if (!exitRequested &&
                    (args.CloseReason == CloseReason.UserClosing ||
                     args.CloseReason == CloseReason.None))
                {
                    args.Cancel = true;
                    HideInTray();
                    return;
                }
                shuttingDown = true;
                StopCoverZOrderGuard();
                StopBackgroundMonitors();
                SaveOverlayPosition();
                SaveOverlaySize();
                UnregisterHotKey(Handle, HotkeyTogglePause);
                UnregisterHotKey(Handle, HotkeyEmergencyPause);
                CloseSerial();
                trayIcon.Visible = false;
                trayIcon.Dispose();
                Log("Overlay stopped.");
            };
        }

        protected override bool ShowWithoutActivation
        {
            get { return layoutTestMode || base.ShowWithoutActivation; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                if (layoutTestMode)
                {
                    const int WsExTransparent = 0x00000020;
                    const int WsExToolWindow = 0x00000080;
                    const int WsExNoActivate = 0x08000000;
                    parameters.ExStyle |= WsExTransparent |
                        WsExToolWindow | WsExNoActivate;
                }
                return parameters;
            }
        }

        private void CaptureResponsiveFonts(Control root)
        {
            if (root == null)
                return;
            if ((root is Label || root is Button ||
                 root is NumericUpDown) &&
                !responsiveFontSizes.ContainsKey(root))
                responsiveFontSizes.Add(root, root.Font.Size);
            foreach (Control child in root.Controls)
                CaptureResponsiveFonts(child);
        }

        private void CaptureTextFitFonts(Control root)
        {
            if (root == null)
                return;
            if ((root is Label || root is Button) &&
                !textFitBaseFontSizes.ContainsKey(root))
            {
                textFitBaseFontSizes.Add(root, root.Font.Size);
                root.TextChanged += delegate
                {
                    if (!textFitActive && !root.IsDisposed)
                        FitTextControl(root);
                };
            }
            foreach (Control child in root.Controls)
                CaptureTextFitFonts(child);
        }

        private void EnsureTextFitsWithinBounds(Control root)
        {
            if (textFitActive || root == null)
                return;
            textFitActive = true;
            try
            {
                RestoreTargetFonts(root);
                FitTextTree(root);
            }
            finally
            {
                textFitActive = false;
            }
        }

        private void RestoreTargetFonts(Control root)
        {
            float baseSize;
            if (textFitBaseFontSizes.TryGetValue(root, out baseSize))
            {
                float target = responsiveFontSizes.ContainsKey(root)
                    ? baseSize * GetResponsiveScale()
                    : baseSize;
                if (Math.Abs(root.Font.Size - target) > 0.05f)
                    ReplaceControlFont(root, new Font(
                        "Segoe UI", target, root.Font.Style));
            }
            foreach (Control child in root.Controls)
                RestoreTargetFonts(child);
        }

        internal static void FitTextTree(Control root)
        {
            if (root.Visible &&
                (root is Label || root is Button))
                FitTextControl(root);
            foreach (Control child in root.Controls)
                FitTextTree(child);
        }

        internal static bool FitTextControl(Control control)
        {
            if (control.AutoSize || String.IsNullOrEmpty(control.Text) ||
                control.ClientSize.Width <= 0 ||
                control.ClientSize.Height <= 0)
                return true;

            float dpiScale = Math.Max(1f, control.DeviceDpi / 96f);
            int horizontalInset = (int)Math.Ceiling(
                (control is Button ? 24 : 4) * dpiScale);
            int verticalInset = (int)Math.Ceiling(
                (control is Button ? 14 : 2) * dpiScale);
            int availableWidth = Math.Max(1,
                control.ClientSize.Width - control.Padding.Horizontal -
                horizontalInset);
            int availableHeight = Math.Max(1,
                control.ClientSize.Height - control.Padding.Vertical -
                verticalInset);
            float targetSize = control.Font.Size;
            float minimumReadableSize = Math.Min(
                targetSize, MinimumReadableUiFontSize);
            FontStyle style = control.Font.Style;
            string family = control.Font.FontFamily.Name;
            bool isButton = control is Button;

            for (float size = targetSize;
                size >= minimumReadableSize; size -= 0.5f)
            {
                using (Font candidate = new Font(family, size, style))
                {
                    Size line = TextRenderer.MeasureText(
                        "Ag", candidate, Size.Empty,
                        TextFormatFlags.NoPadding |
                        TextFormatFlags.SingleLine);
                    bool multiline = !isButton &&
                        (control.Text.IndexOfAny(
                            new[] { '\r', '\n' }) >= 0 ||
                         availableHeight >= line.Height * 2);
                    TextFormatFlags flags = TextFormatFlags.NoPadding |
                        TextFormatFlags.NoPrefix |
                        (multiline
                            ? TextFormatFlags.WordBreak
                            : TextFormatFlags.SingleLine);
                    Size measured = TextRenderer.MeasureText(
                        control.Text, candidate,
                        new Size(availableWidth,
                            multiline ? Int32.MaxValue : availableHeight),
                        flags);
                    if (measured.Width <= availableWidth + 1 &&
                        measured.Height <= availableHeight + 1)
                    {
                        if (Math.Abs(control.Font.Size - size) > 0.05f)
                            ReplaceControlFont(control,
                                new Font(family, size, style));
                        return true;
                    }
                }
            }

            if (control.Font.Size > minimumReadableSize)
                ReplaceControlFont(control,
                    new Font(family, minimumReadableSize, style));
            return ExpandControlForFullText(
                control, horizontalInset, verticalInset);
        }

        private static bool ExpandControlForFullText(
            Control control, int horizontalInset, int verticalInset)
        {
            bool isButton = control is Button;
            int availableWidth = Math.Max(1,
                control.ClientSize.Width - control.Padding.Horizontal -
                horizontalInset);
            TextFormatFlags flags = TextFormatFlags.NoPadding |
                TextFormatFlags.NoPrefix |
                (isButton
                    ? TextFormatFlags.SingleLine
                    : TextFormatFlags.WordBreak);
            Size measured = TextRenderer.MeasureText(
                control.Text, control.Font,
                isButton
                    ? Size.Empty
                    : new Size(availableWidth, Int32.MaxValue),
                flags);
            int requiredWidth = isButton
                ? measured.Width + control.Padding.Horizontal +
                    horizontalInset
                : Math.Max(control.Width,
                    measured.Width + control.Padding.Horizontal +
                    horizontalInset);
            int requiredHeight = measured.Height +
                control.Padding.Vertical + verticalInset;
            Size originalSize = control.Size;
            Size requiredSize = new Size(
                Math.Max(originalSize.Width, requiredWidth),
                Math.Max(originalSize.Height, requiredHeight));

            control.MinimumSize = new Size(
                Math.Max(control.MinimumSize.Width, requiredSize.Width),
                Math.Max(control.MinimumSize.Height, requiredSize.Height));
            if (control.Dock == DockStyle.None)
                control.Size = requiredSize;

            Label label = control as Label;
            if (label != null)
                label.AutoEllipsis = false;
            ShiftFixedSiblingsAfterExpansion(
                control, originalSize, requiredSize);
            if (control.Parent != null)
                control.Parent.PerformLayout();
            EnsureScrollableExtent(control);

            if (TextFitsAtCurrentSize(control))
                return true;

            // This branch is intentionally structural rather than another
            // font reduction: AutoSize exposes the full text and the nearest
            // scrollable ancestor receives an explicit content extent.
            control.AutoSize = true;
            if (label != null)
                label.MaximumSize = new Size(requiredSize.Width, 0);
            Button button = control as Button;
            if (button != null)
                button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            if (control.Parent != null)
                control.Parent.PerformLayout();
            EnsureScrollableExtent(control);
            return true;
        }

        private static bool TextFitsAtCurrentSize(Control control)
        {
            if (control.AutoSize || String.IsNullOrEmpty(control.Text))
                return true;
            float dpiScale = Math.Max(1f, control.DeviceDpi / 96f);
            int horizontalInset = (int)Math.Ceiling(
                (control is Button ? 24 : 4) * dpiScale);
            int verticalInset = (int)Math.Ceiling(
                (control is Button ? 14 : 2) * dpiScale);
            int width = Math.Max(1,
                control.ClientSize.Width - control.Padding.Horizontal -
                horizontalInset);
            int height = Math.Max(1,
                control.ClientSize.Height - control.Padding.Vertical -
                verticalInset);
            bool multiline = !(control is Button);
            Size measured = TextRenderer.MeasureText(
                control.Text, control.Font,
                new Size(width, multiline ? Int32.MaxValue : height),
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoPrefix |
                (multiline
                    ? TextFormatFlags.WordBreak
                    : TextFormatFlags.SingleLine));
            return measured.Width <= width + 1 &&
                measured.Height <= height + 1;
        }

        private static void ShiftFixedSiblingsAfterExpansion(
            Control control, Size originalSize, Size requiredSize)
        {
            Control parent = control.Parent;
            if (parent == null || parent is TableLayoutPanel ||
                parent is FlowLayoutPanel)
                return;
            int addedHeight = requiredSize.Height - originalSize.Height;
            int addedWidth = requiredSize.Width - originalSize.Width;
            if (addedHeight <= 0 && addedWidth <= 0)
                return;
            Rectangle expanded = new Rectangle(
                control.Location, requiredSize);
            foreach (Control sibling in parent.Controls)
            {
                if (sibling == control || !sibling.Visible ||
                    !expanded.IntersectsWith(sibling.Bounds))
                    continue;
                if (addedHeight > 0 &&
                    sibling.Top >= control.Top + originalSize.Height)
                    sibling.Top = expanded.Bottom + 4;
                else if (addedWidth > 0 &&
                    sibling.Left >= control.Left + originalSize.Width)
                    sibling.Left = expanded.Right + 4;
            }
        }

        private static void EnsureScrollableExtent(Control control)
        {
            Control current = control.Parent;
            ScrollableControl scrollable = null;
            int right = control.Right;
            int bottom = control.Bottom;
            while (current != null)
            {
                ScrollableControl candidate = current as ScrollableControl;
                if (candidate != null && candidate.AutoScroll)
                {
                    scrollable = candidate;
                    break;
                }
                right += current.Left;
                bottom += current.Top;
                current = current.Parent;
            }
            if (scrollable == null)
            {
                current = control.Parent;
                right = control.Right;
                bottom = control.Bottom;
                while (current != null)
                {
                    ScrollableControl candidate =
                        current as ScrollableControl;
                    if (candidate != null)
                    {
                        scrollable = candidate;
                        scrollable.AutoScroll = true;
                        break;
                    }
                    right += current.Left;
                    bottom += current.Top;
                    current = current.Parent;
                }
            }
            if (scrollable == null)
                return;
            scrollable.AutoScrollMinSize = new Size(
                Math.Max(scrollable.AutoScrollMinSize.Width, right + 8),
                Math.Max(scrollable.AutoScrollMinSize.Height, bottom + 8));
        }

        internal static void ReplaceControlFont(
            Control control, Font replacement)
        {
            Font previous = control.Font;
            control.Font = replacement;
            if (previous != null && previous != replacement &&
                !previous.IsSystemFont)
                previous.Dispose();
        }

        private void ApplyResponsiveTypography()
        {
            float scale = GetResponsiveScale();
            float dpiScale = GetCurrentDpiScale();
            bool scaleChanged =
                Math.Abs(scale - lastResponsiveScale) >= 0.025f ||
                Math.Abs(dpiScale - lastResponsiveDpiScale) >= 0.01f;
            lastResponsiveScale = scale;
            lastResponsiveDpiScale = dpiScale;

            if (scaleChanged)
            {
                foreach (KeyValuePair<Control, float> item in
                    responsiveFontSizes)
                {
                    if (item.Key == null || item.Key.IsDisposed)
                        continue;
                    ReplaceControlFont(item.Key, new Font(
                        "Segoe UI", item.Value * scale,
                        item.Key.Font.Style));
                }
                if (rangeControl != null)
                    rangeControl.VisualScale = scale;
                float layoutScale = scale * dpiScale;
                dashboard.Padding = new Padding(
                    (int)Math.Ceiling(24 * layoutScale),
                    (int)Math.Ceiling(18 * layoutScale),
                    (int)Math.Ceiling(24 * layoutScale),
                    (int)Math.Ceiling(18 * layoutScale));
            }
            LayoutDashboardWidths();
            dashboard.PerformLayout();
            EnsureTextFitsWithinBounds(this);
        }

        private float GetCurrentDpiScale()
        {
            return Math.Max(1f, DeviceDpi / 96f);
        }

        private int ScaleLogical(int value)
        {
            return Math.Max(1,
                (int)Math.Ceiling(value * GetCurrentDpiScale()));
        }

        private void LayoutMainChrome()
        {
            if (mainHeaderPanel == null || mainTitleLabel == null ||
                settingsButton == null || statusButton == null ||
                mainChromeLayoutActive)
                return;
            mainChromeLayoutActive = true;
            try
            {

                int margin = ScaleLogical(24);
                int gap = ScaleLogical(12);
                Size titleSize = TextRenderer.MeasureText(
                    mainTitleLabel.Text, mainTitleLabel.Font,
                    Size.Empty, TextFormatFlags.NoPadding |
                    TextFormatFlags.SingleLine);
                Size settingsSize = MeasureSafeButton(
                    settingsButton, ScaleLogical(190), ScaleLogical(44));
                Size statusSize = MeasureSafeButton(
                    statusButton, ScaleLogical(220), ScaleLogical(44));
                int rowHeight = Math.Max(
                    Math.Max(titleSize.Height + ScaleLogical(16),
                        settingsSize.Height + ScaleLogical(12)),
                    statusSize.Height + ScaleLogical(12));

                int settingsLeft = Math.Max(margin,
                    ClientSize.Width - margin - settingsSize.Width);
                int titleRight = margin + titleSize.Width;
                int centeredStatusLeft = Math.Max(margin,
                    (ClientSize.Width - statusSize.Width) / 2);
                bool wrapStatus =
                    centeredStatusLeft < titleRight + gap ||
                    centeredStatusLeft + statusSize.Width >
                        settingsLeft - gap;
                int headerHeight = Math.Max(ScaleLogical(64),
                    wrapStatus ? rowHeight * 2 : rowHeight);

                mainHeaderPanel.SetBounds(
                    0, 0, ClientSize.Width, headerHeight);
                mainTitleLabel.SetBounds(
                    margin,
                    Math.Max(0,
                        (rowHeight - titleSize.Height) / 2),
                    Math.Max(1, titleSize.Width + ScaleLogical(4)),
                    Math.Max(1, titleSize.Height + ScaleLogical(2)));
                settingsButton.SetBounds(
                    settingsLeft,
                    Math.Max(0, (rowHeight - settingsSize.Height) / 2),
                    settingsSize.Width, settingsSize.Height);
                statusButton.SetBounds(
                    Math.Max(margin,
                        (ClientSize.Width - statusSize.Width) / 2),
                    wrapStatus
                        ? rowHeight + Math.Max(0,
                            (rowHeight - statusSize.Height) / 2)
                        : Math.Max(0,
                            (rowHeight - statusSize.Height) / 2),
                    statusSize.Width, statusSize.Height);

                if (mainHeaderDivider != null)
                    mainHeaderDivider.SetBounds(
                        0, headerHeight - 1, ClientSize.Width, 1);

                int footerHeight = ScaleLogical(32);
                if (versionFooterLabel != null)
                {
                    Size footerText = TextRenderer.MeasureText(
                        versionFooterLabel.Text, versionFooterLabel.Font,
                        new Size(Math.Max(1,
                            ClientSize.Width - margin * 2), Int32.MaxValue),
                        TextFormatFlags.NoPadding |
                        TextFormatFlags.SingleLine);
                    footerHeight = Math.Max(footerHeight,
                        footerText.Height + ScaleLogical(8));
                    versionFooterLabel.SetBounds(
                        margin, ClientSize.Height - footerHeight,
                        Math.Max(1, ClientSize.Width - margin * 2),
                        footerHeight);
                }

                int contentHeight = Math.Max(1,
                    ClientSize.Height - headerHeight - footerHeight);
                if (dashboard != null)
                    dashboard.SetBounds(
                        0, headerHeight, ClientSize.Width, contentHeight);
                if (settingsPage != null && !settingsPage.IsDisposed)
                    settingsPage.SetBounds(
                        0, headerHeight, ClientSize.Width, contentHeight);
                mainHeaderPanel.BringToFront();
                if (versionFooterLabel != null)
                    versionFooterLabel.BringToFront();
                mainHeaderPanel.Invalidate(true);
                if (settingsPage != null && !settingsPage.IsDisposed)
                    mainHeaderPanel.Update();
            }
            finally
            {
                mainChromeLayoutActive = false;
            }
        }

        private static Size MeasureSafeButton(
            Button button, int minimumWidth, int minimumHeight)
        {
            Size measured = TextRenderer.MeasureText(
                button.Text, button.Font, Size.Empty,
                TextFormatFlags.NoPadding |
                TextFormatFlags.SingleLine);
            return new Size(
                Math.Max(minimumWidth,
                    measured.Width + Math.Max(24,
                        button.Padding.Horizontal + 20)),
                Math.Max(minimumHeight,
                    measured.Height + Math.Max(16,
                        button.Padding.Vertical + 12)));
        }

        private float GetResponsiveScale()
        {
            float dpiScale = GetCurrentDpiScale();
            float widthScale = ClientSize.Width / dpiScale / 1100f;
            float heightScale = ClientSize.Height / dpiScale / 720f;
            return Math.Max(1f,
                Math.Min(1.45f, Math.Min(widthScale, heightScale)));
        }

        private void LayoutDashboardWidths()
        {
            if (dashboard == null || mappingPanel == null ||
                mappingTitleLabel == null || profilePanel == null ||
                detailsHeader == null || actionPanel == null)
                return;

            float layoutScale = GetResponsiveScale() *
                GetCurrentDpiScale();
            int side = (int)Math.Ceiling(20 * layoutScale);
            int smallSide = (int)Math.Ceiling(18 * layoutScale);
            int gap = (int)Math.Ceiling(12 * layoutScale);
            int viewportWidth = Math.Max(1,
                dashboard.ClientSize.Width - dashboard.Padding.Horizontal -
                (dashboard.VerticalScroll.Visible
                    ? SystemInformation.VerticalScrollBarWidth : 0) -
                (int)Math.Ceiling(20 * layoutScale));

            Size mappingTitleSize = MeasureSafeText(mappingTitleLabel);
            Size measuredSize = MeasureSafeText(lastMeasuredLabel);
            Size profileNameSize = MeasureSafeText(profileNameLabel);
            Size profileButtonSize = MeasureSafeButton(
                mappingSettingsButton,
                (int)Math.Ceiling(154 * layoutScale),
                (int)Math.Ceiling(42 * layoutScale));
            Size detailsButtonText = MeasureSafeText(detailsToggleButton);
            Size detailsSummarySize = MeasureSafeText(detailsSummaryLabel);

            int mappingTitleWidth = Math.Max(
                (int)Math.Ceiling(250 * layoutScale),
                mappingTitleSize.Width + (int)Math.Ceiling(8 * layoutScale));
            int measuredWidth = Math.Max(
                (int)Math.Ceiling(260 * layoutScale),
                measuredSize.Width + (int)Math.Ceiling(8 * layoutScale));
            int detailsButtonWidth = Math.Max(
                (int)Math.Ceiling(280 * layoutScale),
                detailsButtonText.Width + (int)Math.Ceiling(28 * layoutScale));
            int detailsSummaryWidth = Math.Max(
                (int)Math.Ceiling(260 * layoutScale),
                detailsSummarySize.Width + (int)Math.Ceiling(8 * layoutScale));
            int profileTextWidth = Math.Max(
                (int)Math.Ceiling(220 * layoutScale),
                profileNameSize.Width + (int)Math.Ceiling(8 * layoutScale));

            int sectionWidth = Math.Max(viewportWidth,
                (int)Math.Ceiling(640 * layoutScale));
            sectionWidth = Math.Max(sectionWidth,
                side * 2 + mappingTitleWidth + gap + measuredWidth);
            sectionWidth = Math.Max(sectionWidth,
                smallSide * 2 + profileTextWidth + gap +
                profileButtonSize.Width);
            sectionWidth = Math.Max(sectionWidth,
                smallSide * 2 + detailsButtonWidth + gap +
                detailsSummaryWidth);

            SizeContentDrivenPanel(
                mappingPanel, mappingLayout, sectionWidth);
            SizeContentDrivenPanel(
                profilePanel, dashboardProfileLayout, sectionWidth);
            SizeContentDrivenPanel(
                detailsHeader, detailsHeaderLayout, sectionWidth);

            if (detailsLayout != null)
            {
                int detailValueWidth = Math.Max(
                    (int)Math.Ceiling(260 * layoutScale),
                    sectionWidth - (int)Math.Ceiling(260 * layoutScale));
                serialLabel.MaximumSize = new Size(detailValueWidth, 0);
                darkroomLabel.MaximumSize = new Size(detailValueWidth, 0);
                boothLabel.MaximumSize = new Size(detailValueWidth, 0);
                currentIsoLabel.MaximumSize = new Size(detailValueWidth, 0);
                SizeContentDrivenPanel(
                    detailsPanel, detailsLayout, sectionWidth);
            }

            SizeContentDrivenControl(actionLabel, sectionWidth);
            SizeContentDrivenControl(actionPanel, sectionWidth);
            dashboard.PerformLayout();
        }

        private static void SizeContentDrivenPanel(
            Panel panel, Control content, int width)
        {
            if (panel == null || content == null)
                return;

            width = Math.Max(1, width);
            panel.SuspendLayout();
            content.SuspendLayout();
            try
            {
                panel.MinimumSize = Size.Empty;
                panel.MaximumSize = Size.Empty;
                panel.Width = width;

                int frameWidth = Math.Max(0,
                    panel.Width - panel.ClientSize.Width);
                int frameHeight = Math.Max(0,
                    panel.Height - panel.ClientSize.Height);
                int contentWidth = Math.Max(1,
                    width - frameWidth - panel.Padding.Horizontal);

                content.MinimumSize = new Size(contentWidth, 0);
                content.MaximumSize = new Size(contentWidth, 0);
                content.Width = contentWidth;
                content.PerformLayout();

                Size preferred = content.GetPreferredSize(
                    new Size(contentWidth, 0));
                int contentHeight = Math.Max(1, preferred.Height);
                content.Height = contentHeight;
                int panelHeight = contentHeight +
                    panel.Padding.Vertical + frameHeight;

                panel.MinimumSize = new Size(width, panelHeight);
                panel.MaximumSize = new Size(width, 0);
                panel.Size = new Size(width, panelHeight);
            }
            finally
            {
                content.ResumeLayout(true);
                panel.ResumeLayout(true);
            }
        }

        private static void SizeContentDrivenControl(
            Control control, int width)
        {
            if (control == null)
                return;

            width = Math.Max(1, width);
            control.MinimumSize = Size.Empty;
            control.MaximumSize = new Size(width, 0);
            control.Width = width;
            control.PerformLayout();
            int height = Math.Max(1,
                control.GetPreferredSize(new Size(width, 0)).Height);
            control.MinimumSize = new Size(width, height);
            control.MaximumSize = new Size(width, 0);
            control.Size = new Size(width, height);
        }

        private static Size MeasureSafeText(Control control)
        {
            return TextRenderer.MeasureText(
                String.IsNullOrEmpty(control.Text) ? " " : control.Text,
                control.Font, Size.Empty,
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoPrefix |
                TextFormatFlags.SingleLine);
        }

        private void HideInTrayWhenMinimized()
        {
            if (WindowState == FormWindowState.Minimized)
                HideInTray();
        }

        private void HideInTray()
        {
            ShowInTaskbar = false;
            Hide();
        }

        private void ShowMainWindow()
        {
            if (layoutTestMode)
            {
                ShowInTaskbar = false;
                if (!Visible && !headlessLayoutTestMode)
                    Show();
                return;
            }
            ShowInTaskbar = true;
            Show();
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            BringToFront();
            Activate();
        }

        private void ToggleStatusPanel()
        {
            statusPanel.Visible = !statusPanel.Visible;
            if (statusPanel.Visible)
            {
                PositionStatusPanel();
                statusPanel.BringToFront();
                statusPanel.Focus();
            }
        }

        private void PositionStatusPanel()
        {
            if (statusPanel == null || statusPanelLayout == null)
                return;
            int contentTop = mainHeaderPanel == null
                ? ScaleLogical(64) : mainHeaderPanel.Bottom;
            int width = Math.Min(
                ScaleLogical(500),
                Math.Max(ScaleLogical(420),
                    ClientSize.Width - ScaleLogical(48)));
            int textWidth = Math.Max(1,
                width - ScaleLogical(46));
            statusPanelMessage.MaximumSize = new Size(textWidth, 0);
            statusPanelLayout.Width = Math.Max(1, width - 2);
            statusPanelLayout.PerformLayout();
            int desiredHeight = Math.Max(ScaleLogical(230),
                statusPanelLayout.PreferredSize.Height + 2);
            int availableHeight = Math.Max(ScaleLogical(200),
                ClientSize.Height - contentTop - ScaleLogical(36));
            int height = Math.Min(desiredHeight, availableHeight);
            statusPanel.AutoScrollMinSize = new Size(
                0, statusPanelLayout.PreferredSize.Height);
            statusPanel.SetBounds(
                Math.Max(ScaleLogical(12),
                    (ClientSize.Width - width) / 2),
                contentTop + Math.Max(ScaleLogical(18),
                    (ClientSize.Height - contentTop - height) / 2),
                width, height);
            statusPanelLayout.Width = Math.Max(1,
                statusPanel.ClientSize.Width -
                (statusPanel.VerticalScroll.Visible
                    ? SystemInformation.VerticalScrollBarWidth : 0));
        }

        private void ExitApplication()
        {
            exitRequested = true;
            Close();
        }

        private void StartBackgroundMonitors()
        {
            reconnectTimer = new System.Threading.Timer(
                EnsureSerialConnectedInBackground, null, 0, 2000);
            darkroomProbeTimer = new System.Threading.Timer(
                ProbeDarkroomInBackground, null, 0, 1000);
        }

        private void StopBackgroundMonitors()
        {
            System.Threading.Timer reconnect = reconnectTimer;
            reconnectTimer = null;
            if (reconnect != null)
                reconnect.Dispose();

            System.Threading.Timer probe = darkroomProbeTimer;
            darkroomProbeTimer = null;
            if (probe != null)
                probe.Dispose();
        }

        private void EnsureSerialConnectedInBackground(object state)
        {
            if (shuttingDown)
                return;
            if (Interlocked.Exchange(ref reconnectActive, 1) != 0)
                return;
            try
            {
                EnsureSerialConnected();
            }
            catch (Exception exception)
            {
                Log("Serial monitor failed: " + exception.Message);
            }
            finally
            {
                Interlocked.Exchange(ref reconnectActive, 0);
            }
        }

        private void ProbeDarkroomInBackground(object state)
        {
            if (shuttingDown)
                return;
            if (Interlocked.Exchange(ref darkroomProbeActive, 1) != 0)
                return;
            try
            {
                UpdateDarkroomState();
            }
            catch (Exception exception)
            {
                Log("Darkroom monitor failed: " + exception.Message);
            }
            finally
            {
                Interlocked.Exchange(ref darkroomProbeActive, 0);
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmActivateExistingInstance)
            {
                ShowMainWindow();
                message.Result = IntPtr.Zero;
                return;
            }
            if (message.Msg == WmHotkey)
            {
                int id = message.WParam.ToInt32();
                if (id == HotkeyTogglePause)
                    TogglePause();
                else if (id == HotkeyEmergencyPause)
                    SetPaused(true, "Emergency stop hotkey pressed.");
            }
            base.WndProc(ref message);
        }

        private bool TryLoadOverlayPosition(out Point location)
        {
            location = Point.Empty;
            try
            {
                if (!File.Exists(positionPath))
                    return false;
                string[] parts = File.ReadAllText(positionPath)
                    .Trim().Split(',');
                int x;
                int y;
                if (parts.Length != 2 ||
                    !Int32.TryParse(parts[0], out x) ||
                    !Int32.TryParse(parts[1], out y))
                    return false;

                Rectangle candidate = new Rectangle(
                    x, y, Width, Height);
                foreach (Screen screen in Screen.AllScreens)
                {
                    Rectangle visible = Rectangle.Intersect(
                        candidate, screen.WorkingArea);
                    if (visible.Width >= 100 && visible.Height >= 50)
                    {
                        location = new Point(x, y);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private void SaveOverlayPosition()
        {
            try
            {
                if (WindowState != FormWindowState.Normal)
                    return;
                File.WriteAllText(
                    positionPath,
                    Location.X + "," + Location.Y);
            }
            catch { }
        }

        private bool TryLoadOverlaySize(out Size size)
        {
            size = Size.Empty;
            try
            {
                if (!File.Exists(sizePath))
                    return false;
                string[] parts = File.ReadAllText(sizePath)
                    .Trim().Split(',');
                int width;
                int height;
                if (parts.Length != 2 ||
                    !Int32.TryParse(parts[0], out width) ||
                    !Int32.TryParse(parts[1], out height))
                    return false;
                width = Math.Max(1000, Math.Min(1800, width));
                height = Math.Max(720, Math.Min(1100, height));
                size = new Size(width, height);
                return true;
            }
            catch { }
            return false;
        }

        private void SaveOverlaySize()
        {
            try
            {
                if (WindowState != FormWindowState.Normal ||
                    String.IsNullOrWhiteSpace(sizePath))
                    return;
                File.WriteAllText(
                    sizePath,
                    ClientSize.Width + "," + ClientSize.Height);
            }
            catch { }
        }

        private int LoadStabilitySeconds()
        {
            try
            {
                int value;
                if (File.Exists(stabilityPath) &&
                    Int32.TryParse(
                        File.ReadAllText(stabilityPath).Trim(), out value))
                    return Math.Max(5, Math.Min(300, value));
            }
            catch { }
            return DefaultStabilitySeconds;
        }

        private void SaveStabilitySeconds()
        {
            try
            {
                File.WriteAllText(
                    stabilityPath, stabilitySeconds.ToString());
            }
            catch { }
        }

        private bool LoadAutomaticAdjustmentStopped()
        {
            try
            {
                return File.Exists(automaticStatePath) &&
                    File.ReadAllText(automaticStatePath).Trim().Equals(
                        "stopped", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void SaveAutomaticAdjustmentState()
        {
            try
            {
                File.WriteAllText(automaticStatePath,
                    paused ? "stopped" : "started",
                    new UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                Log("Automatic adjustment state could not be saved: " +
                    exception.Message);
            }
        }

        private void LoadCoverTextSettings()
        {
            try
            {
                if (!File.Exists(coverTextPath))
                    return;
                foreach (string source in File.ReadAllLines(coverTextPath))
                {
                    string line = source.Trim();
                    if (line.Equals("mode=custom",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        useCustomCoverText = true;
                    }
                    else if (line.StartsWith("title=",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        customCoverTitle = DecodeSettingText(
                            line.Substring(6), DefaultCoverTitle);
                    }
                    else if (line.StartsWith("message=",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        customCoverMessage = DecodeSettingText(
                            line.Substring(8), DefaultCoverMessage);
                    }
                }
            }
            catch (Exception exception)
            {
                Log("Fullpage text settings could not be loaded: " +
                    exception.Message);
            }
        }

        private void SaveCoverTextSettings()
        {
            try
            {
                string title;
                string message;
                bool custom;
                lock (coverTextSync)
                {
                    custom = useCustomCoverText;
                    title = customCoverTitle;
                    message = customCoverMessage;
                }
                File.WriteAllLines(coverTextPath, new[] {
                    custom ? "mode=custom" : "mode=default",
                    "title=" + EncodeSettingText(title),
                    "message=" + EncodeSettingText(message)
                }, new UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                Log("Fullpage text settings could not be saved: " +
                    exception.Message);
            }
        }

        private static string EncodeSettingText(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
        }

        private static string DecodeSettingText(
            string value, string fallback)
        {
            try
            {
                string decoded = Encoding.UTF8.GetString(
                    Convert.FromBase64String(value));
                return String.IsNullOrWhiteSpace(decoded) ? fallback : decoded;
            }
            catch
            {
                return fallback;
            }
        }

        private string GetActiveCoverTitle()
        {
            lock (coverTextSync)
                return useCustomCoverText
                    ? customCoverTitle
                    : DefaultCoverTitle;
        }

        private string GetActiveCoverMessage()
        {
            lock (coverTextSync)
                return useCustomCoverText
                    ? customCoverMessage
                    : DefaultCoverMessage;
        }

        private static List<IsoBand> CreateDefaultIsoBands()
        {
            return new List<IsoBand> {
                new IsoBand(16, 3200),
                new IsoBand(33, 2500),
                new IsoBand(50, 1600),
                new IsoBand(67, 1000),
                new IsoBand(84, 640),
                new IsoBand(100, 400)
            };
        }

        private void LoadIsoMappingSettings()
        {
            try
            {
                if (!File.Exists(mappingPath))
                    return;
                string[] lines = File.ReadAllLines(mappingPath);
                bool customMode = false;
                List<IsoBand> loaded = new List<IsoBand>();
                foreach (string source in lines)
                {
                    string line = source.Trim();
                    if (line.Equals("mode=custom",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        customMode = true;
                        continue;
                    }
                    if (!line.StartsWith("band=",
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    string[] parts = line.Substring(5).Split(',');
                    int maximum;
                    int iso;
                    if (parts.Length == 2 &&
                        Int32.TryParse(parts[0], out maximum) &&
                        Int32.TryParse(parts[1], out iso))
                        loaded.Add(new IsoBand(maximum, iso));
                }
                if (IsoBandsAreValid(loaded))
                    customIsoBands = loaded;
                useCustomMapping = customMode;
            }
            catch (Exception exception)
            {
                Log("ISO mapping settings could not be loaded: " +
                    exception.Message);
            }
        }

        private void SaveIsoMappingSettings()
        {
            try
            {
                List<string> lines = new List<string>();
                lock (mappingSync)
                {
                    lines.Add(useCustomMapping
                        ? "mode=custom"
                        : "mode=default");
                    foreach (IsoBand band in customIsoBands)
                        lines.Add("band=" + band.MaximumLight + "," + band.Iso);
                }
                File.WriteAllLines(mappingPath, lines.ToArray(),
                    new UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                Log("ISO mapping settings could not be saved: " +
                    exception.Message);
            }
        }

        private static bool IsoBandsAreValid(List<IsoBand> bands)
        {
            if (bands == null || bands.Count < 1 || bands.Count > 8)
                return false;
            int previous = -1;
            foreach (IsoBand band in bands)
            {
                if (band.MaximumLight <= previous ||
                    band.MaximumLight > 100 ||
                    Array.IndexOf(SupportedIsoValues, band.Iso) < 0)
                    return false;
                previous = band.MaximumLight;
            }
            return previous == 100;
        }

        private List<IsoBand> GetActiveIsoBands()
        {
            lock (mappingSync)
            {
                List<IsoBand> source = useCustomMapping
                    ? customIsoBands
                    : CreateDefaultIsoBands();
                List<IsoBand> copy = new List<IsoBand>();
                foreach (IsoBand band in source)
                    copy.Add(band.Clone());
                return copy;
            }
        }

        private int MapLightToIso(int light)
        {
            List<IsoBand> bands = GetActiveIsoBands();
            foreach (IsoBand band in bands)
                if (light <= band.MaximumLight)
                    return band.Iso;
            return bands[bands.Count - 1].Iso;
        }

        private void RecalculateMappedIso()
        {
            int light;
            lock (sensor.Sync)
            {
                light = sensor.Light;
                if (light < 0)
                {
                    sensor.MappedIso = -1;
                    sensor.CandidateSince = DateTime.MinValue;
                    return;
                }
            }
            int mappedIso = MapLightToIso(light);
            lock (sensor.Sync)
            {
                if (sensor.Light != light)
                    return;
                if (sensor.MappedIso != mappedIso)
                {
                    sensor.MappedIso = mappedIso;
                    sensor.CandidateSince = DateTime.Now;
                    Log("PC mapping changed target ISO to " + mappedIso +
                        " at light " + light + ".");
                }
            }
        }

        private string BuildMappingSummary()
        {
            List<IsoBand> bands = GetActiveIsoBands();
            List<string> parts = new List<string>();
            int lower = 0;
            foreach (IsoBand band in bands)
            {
                parts.Add(lower + "–" + band.MaximumLight +
                    " → ISO " + band.Iso);
                lower = band.MaximumLight + 1;
            }
            return String.Join("   ·   ", parts.ToArray());
        }

        private void UpdateMappingControls()
        {
            if (mappingModeInput == null || mappingSummaryLabel == null)
                return;
            updatingMappingControls = true;
            try
            {
                mappingModeInput.SelectedIndex = useCustomMapping ? 1 : 0;
                mappingSummaryLabel.Text =
                    (useCustomMapping
                        ? "Eigen profiel voor " + Environment.MachineName
                        : "Standaardprofiel voor alle booths") +
                    Environment.NewLine + BuildMappingSummary();
                if (profileNameLabel != null)
                    profileNameLabel.Text = useCustomMapping
                        ? "Eigen · " + Environment.MachineName
                        : "Standaard · alle booths";
                mappingSettingsButton.Text = useCustomMapping
                    ? "Profiel wijzigen"
                    : "Profiel bekijken";
                if (rangeControl != null)
                {
                    int light;
                    int iso;
                    lock (sensor.Sync)
                    {
                        light = sensor.Light;
                        iso = sensor.MappedIso;
                    }
                    rangeControl.UpdateData(GetActiveIsoBands(), light, iso);
                }
            }
            finally
            {
                updatingMappingControls = false;
            }
        }

        private void ShowIsoMappingSettings()
        {
            ShowMainWindow();
            if (settingsPage != null && !settingsPage.IsDisposed)
            {
                settingsPage.BringToFront();
                settingsPage.Focus();
                return;
            }

            List<IsoBand> custom;
            bool customMode;
            lock (mappingSync)
            {
                customMode = useCustomMapping;
                custom = new List<IsoBand>();
                foreach (IsoBand band in customIsoBands)
                    custom.Add(band.Clone());
            }

            IsoMappingForm page = new IsoMappingForm(
                customMode, custom, CreateDefaultIsoBands(),
                SupportedIsoValues, Environment.MachineName,
                stabilitySeconds, useCustomCoverText,
                customCoverTitle, customCoverMessage,
                DefaultCoverTitle, DefaultCoverMessage);
            settingsPage = page;
            page.TopLevel = false;
            page.FormBorderStyle = FormBorderStyle.None;
            int contentTop = mainHeaderPanel.Bottom;
            int contentBottom = versionFooterLabel.Top;
            page.SetBounds(0, contentTop, ClientSize.Width,
                Math.Max(1, contentBottom - contentTop));
            page.Anchor = AnchorStyles.Top | AnchorStyles.Bottom |
                AnchorStyles.Left | AnchorStyles.Right;
            page.FormClosed += delegate
            {
                if (page.DialogResult == DialogResult.OK)
                {
                    lock (mappingSync)
                    {
                        useCustomMapping = page.UseCustomMapping;
                        customIsoBands = page.CustomBands;
                    }
                    stabilitySeconds = page.StabilitySeconds;
                    lock (coverTextSync)
                    {
                        useCustomCoverText = page.UseCustomCoverText;
                        customCoverTitle = page.CustomCoverTitle;
                        customCoverMessage = page.CustomCoverMessage;
                    }
                    if (stabilitySecondsInput.Value != stabilitySeconds)
                        stabilitySecondsInput.Value = stabilitySeconds;
                    SaveStabilitySeconds();
                    SaveIsoMappingSettings();
                    SaveCoverTextSettings();
                    RecalculateMappedIso();
                    UpdateMappingControls();
                    RefreshUi();
                    Log("Settings saved; profile=" +
                        (useCustomMapping ? "custom" : "default") +
                        "; booth=" + Environment.MachineName +
                        "; stability=" + stabilitySeconds + " seconds" +
                        "; coverText=" +
                        (useCustomCoverText ? "custom" : "default") + ".");
                }
                settingsPage = null;
                dashboard.Visible = true;
                settingsButton.Text = "Instellingen";
                settingsButton.AccessibleName =
                    "Profielinstellingen openen";
            };
            // Creating the embedded form can briefly trigger a DPI auto-scale
            // pass on its siblings. Keep the header off-screen during that
            // pass, then publish only the final measured layout.
            mainHeaderPanel.Visible = false;
            try
            {
                Controls.Add(page);
                dashboard.Visible = false;
                settingsButton.Text = "Terug naar dashboard";
                settingsButton.AccessibleName = "Terug naar dashboard";
                page.BringToFront();
                page.Show();
                page.Focus();
                LayoutMainChrome();
            }
            finally
            {
                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        if (IsDisposed)
                            return;
                        LayoutMainChrome();
                        mainHeaderPanel.Visible = true;
                        mainHeaderPanel.BringToFront();
                        mainHeaderPanel.Refresh();
                    }));
                }
                catch
                {
                    mainHeaderPanel.Visible = true;
                }
            }
        }

        internal void PrepareDashboardForLayoutTest(bool showDetails)
        {
            if (!layoutTestMode)
                throw new InvalidOperationException(
                    "Layout test hooks are only available in layout test mode.");
            serialLabel.Text =
                "COM123 verbonden · JVDP lichtdata wordt stabiel ontvangen";
            darkroomLabel.Text =
                "Darkroombooth actief · camera-instellingen beschikbaar";
            boothLabel.Text =
                "Automatisch aanpassen staat gestart en blijft actief";
            currentIsoLabel.Text = "ISO 2500 bevestigd in Darkroom";
            detailsSummaryLabel.Text =
                "Sensor verbonden · Darkroom actief · automatisch gestart";
            lastMeasuredLabel.Text =
                "Laatste lichtmeting 63 · doelwaarde ISO 1000";
            lightLabel.Text = "100";
            activeRangeLabel.Text = "85–100";
            targetIsoLabel.Text = "25600";
            rangeControl.UpdateData(CreateDefaultIsoBands(), 100, 400);
            actionLabel.Text =
                "De lichtwaarde is stabiel; de doel-ISO kan veilig worden toegepast.";
            detailsPanel.Visible = showDetails;
            detailsToggleButton.Text = showDetails
                ? "Technische details verbergen"
                : "Verbindingen en technische details";
            LayoutDashboardWidths();
            ApplyResponsiveTypography();
        }

        internal void ShowStatusForLayoutTest()
        {
            if (!layoutTestMode)
                throw new InvalidOperationException(
                    "Layout test hooks are only available in layout test mode.");
            statusButton.Text = "Actie nodig · controleer de verbinding";
            statusPanelTitle.Text = "Systeemstatus en bediening";
            statusPanelMessage.Text =
                "De lichtsensor ontvangt gegevens, maar Darkroom heeft de " +
                "camera-instellingen nog niet beschikbaar gemaakt. Controleer " +
                "of Darkroombooth actief is. Automatisch aanpassen blijft " +
                "gestopt totdat je het hier weer start.";
            statusPanelToggleButton.Text = "Automatisch aanpassen starten";
            statusPanel.Visible = true;
            PositionStatusPanel();
            statusPanel.BringToFront();
        }

        internal bool StatusPanelVisibleForLayoutTest
        {
            get
            {
                return layoutTestMode && statusPanel.Visible &&
                    statusPanel.Bounds.Width > 0 &&
                    statusPanel.Bounds.Height > 0;
            }
        }

        internal void ShowSettingsForLayoutTest(bool showCoverSettings)
        {
            if (!layoutTestMode)
                throw new InvalidOperationException(
                    "Layout test hooks are only available in layout test mode.");
            ShowIsoMappingSettings();
            if (settingsPage != null)
                settingsPage.ShowSectionForLayoutTest(
                    showCoverSettings, showCoverSettings);
            LayoutMainChrome();
        }

        internal bool VerifyStaleUpdateRecoveryForLayoutTest()
        {
            if (!layoutTestMode)
                throw new InvalidOperationException(
                    "Layout test hooks are only available in layout test mode.");
            return settingsPage != null &&
                settingsPage.VerifyStaleUpdateRecoveryForLayoutTest();
        }

        private static string BuildVersionFooterText(string localRoot)
        {
            DateTime installedAt = File.GetLastWriteTime(
                Application.ExecutablePath);
            string metadataPath = Path.Combine(localRoot, "installation.txt");
            try
            {
                if (File.Exists(metadataPath))
                {
                    foreach (string line in File.ReadAllLines(metadataPath))
                    {
                        const string prefix = "Installed=";
                        if (!line.StartsWith(prefix,
                            StringComparison.OrdinalIgnoreCase))
                            continue;

                        DateTime parsed;
                        if (DateTime.TryParse(
                            line.Substring(prefix.Length).Trim(),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeLocal, out parsed))
                            installedAt = parsed;
                        break;
                    }
                }
            }
            catch
            {
                // The executable timestamp remains a safe fallback.
            }

            return "Laatst bijgewerkt: " +
                installedAt.ToString("dd-MM-yyyy HH:mm",
                    CultureInfo.InvariantCulture) +
                "  ·  Versie " + BuildTag;
        }

        private static Label MakeLabel(
            string text, float size, FontStyle style, Color color)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = false;
            label.Font = new Font("Segoe UI", size, style);
            label.ForeColor = color;
            label.BackColor = Color.Transparent;
            label.TextAlign = ContentAlignment.MiddleLeft;
            return label;
        }

        private static Panel MakeLightSurface(int width, int height)
        {
            Panel panel = new Panel();
            panel.BackColor = Color.White;
            panel.BorderStyle = BorderStyle.FixedSingle;
            panel.SetBounds(0, 0, width, height);
            return panel;
        }

        private static Button MakeLightButton(string text, bool primary)
        {
            Button button = new Button();
            button.Text = text;
            button.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(31, 111, 235);
            button.BackColor = primary
                ? Color.FromArgb(31, 111, 235)
                : Color.White;
            button.ForeColor = primary
                ? Color.White
                : Color.FromArgb(20, 102, 196);
            button.UseVisualStyleBackColor = false;
            button.MinimumSize = new Size(44, 44);
            button.AccessibleRole = AccessibleRole.PushButton;
            return button;
        }

        private static Label MakeLightDetailCaption(string text)
        {
            Label caption = MakeLabel(
                text, 9, FontStyle.Regular, Color.FromArgb(99, 108, 118));
            caption.AutoSize = true;
            caption.AutoEllipsis = false;
            caption.Dock = DockStyle.Fill;
            caption.Margin = new Padding(0, 6, 24, 6);
            return caption;
        }

        private static void ConfigureDetailValueLabel(Label label)
        {
            label.AutoSize = true;
            label.AutoEllipsis = false;
            label.Dock = DockStyle.Fill;
            label.Margin = new Padding(0, 6, 0, 6);
            label.MaximumSize = new Size(760, 0);
            label.TextAlign = ContentAlignment.MiddleLeft;
        }

        private void AddConnectionRow(
            Panel panel, string caption, string value, int top, int rightInset)
        {
            Label left = MakeLabel(
                caption, 8, FontStyle.Bold, Color.FromArgb(139, 148, 158));
            left.SetBounds(12, top, 100, 24);
            panel.Controls.Add(left);

            Label right = MakeLabel(
                value, 9, FontStyle.Bold, Color.FromArgb(201, 209, 217));
            right.SetBounds(112, top, panel.Width - 112 - rightInset, 24);
            right.Anchor = AnchorStyles.Top |
                AnchorStyles.Left | AnchorStyles.Right;
            panel.Controls.Add(right);
        }

        private Label AddStatusRow(string caption, int top)
        {
            Label left = MakeLabel(
                caption, 9, FontStyle.Bold, Color.FromArgb(139, 148, 158));
            left.SetBounds(20, top, 180, 28);
            Controls.Add(left);

            Label right = MakeLabel(
                "—", 10, FontStyle.Bold, Color.FromArgb(201, 209, 217));
            right.TextAlign = ContentAlignment.MiddleRight;
            right.SetBounds(200, top, ClientSize.Width / 2 - 220, 28);
            right.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            Controls.Add(right);
            return right;
        }

        private void AddSeparator(int top)
        {
            Panel separator = new Panel();
            separator.BackColor = Color.FromArgb(48, 54, 61);
            separator.SetBounds(20, top, ClientSize.Width - 40, 1);
            separator.Anchor = AnchorStyles.Top | AnchorStyles.Left |
                AnchorStyles.Right;
            Controls.Add(separator);
        }

        private void TogglePause()
        {
            SetPaused(!paused, paused
                ? "Automatisch aanpassen gestart."
                : "Automatisch aanpassen gestopt.");
        }

        private void SetPaused(bool value, string message)
        {
            paused = value;
            SaveAutomaticAdjustmentState();
            Log(message);
            RefreshUi();
        }

        private void StartTargetIsoAction(bool automatic)
        {
            if (manualActionRunning)
                return;
            int requestedIso;
            lock (sensor.Sync)
                requestedIso = sensor.MappedIso;
            if (!serialReady || requestedIso <= 0 || !darkroomRunning)
            {
                manualActionFailed = true;
                manualActionStatus = !serialReady || requestedIso <= 0
                    ? "Er is nog geen geldige lichtmeting."
                    : "Darkroom is niet actief.";
                manualActionStatusUntil = DateTime.Now.AddSeconds(6);
                RefreshUi();
                return;
            }
            manualActionRunning = true;
            manualActionFailed = false;
            currentActionAutomatic = automatic;
            manualActionStatus = automatic
                ? "Darkroom automatisch voorbereiden…"
                : "Darkroom voorbereiden…";
            manualActionStatusUntil = DateTime.MaxValue;
            runActionButton.Enabled = false;
            runActionButton.Text = "ISO toepassen…";
            coverToggleButton.Enabled = false;
            pauseButton.Enabled = false;
            Log((automatic ? "Automatic" : "Manual") +
                " PC-mapped target ISO action requested.");
            Thread worker = new Thread(RunTargetIsoAction);
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        private void RunTargetIsoAction()
        {
            bool automatic = currentActionAutomatic;
            Process process = null;
            string finalStatus;
            bool failed = false;
            bool coverShown = false;
            bool keepCoverVisible = manualCoverVisible;
            try
            {
                int desiredIso;
                lock (sensor.Sync)
                    desiredIso = sensor.MappedIso;
                if (desiredIso <= 0)
                    throw new InvalidOperationException(
                        "No valid target ISO is available from the PC mapping.");
                Log("Locked PC-mapped target ISO " + desiredIso +
                    " for this " + (automatic ? "automatic" : "manual") +
                    " action before Darkroom navigation.");
                process = FindDarkroomProcess();
                if (process == null)
                    throw new InvalidOperationException("Darkroom Booth is not running.");
                IntPtr window = process.MainWindowHandle;
                ShowWindowAsync(window, 9);
                SetForegroundWindow(window);
                Thread.Sleep(250);
                coverShown = ShowFullscreenCovers(false);
                Thread.Sleep(250);
                LogDarkroomWindowState(process, window);
                EnsureDarkroomReady(window);
                SetManualActionStatus(
                    "Step 1/3 - opening Darkroom Camera settings...");
                IntPtr combo = EnsureCameraSettingsOpen(process, window);
                Log("Direct ISO control found: handle=" + combo +
                    ", automationId=107. Camera was opened through Darkroom's " +
                    "native Event Settings command and property sheet; no " +
                    "coordinates or mouse input used.");
                List<ComboItem> items = ReadComboItems(combo);
                string observedIso = ReadComboSelection(combo);
                Log("ISO state: PC target=" + desiredIso +
                    ", Darkroom current=" + observedIso +
                    ", available choices=" + items.Count + ".");
                ComboItem target = FindClosestNumericIso(items, desiredIso);
                if (target == null)
                    throw new InvalidOperationException(
                        "Darkroom does not expose any numeric ISO values.");
                if (!String.Equals(
                        target.Value, desiredIso.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                    Log("PC target ISO " + desiredIso +
                        " is unavailable; using nearest Darkroom ISO " +
                        target.Value + ".");

                long currentIndex = SendMessageWithTimeout(
                    combo, 0x0147, IntPtr.Zero, IntPtr.Zero).ToInt64();
                if (currentIndex == target.Index)
                {
                    currentDarkroomIso = target.Value;
                    currentIsoReadAt = DateTime.Now;
                    finalStatus = "No change - Darkroom is already at target ISO " +
                                  target.Value + ".";
                    Log(finalStatus);
                }
                else
                {
                    SetManualActionStatus(
                        "Step 2/3 - selecting target ISO " +
                        target.Value + "...");
                    string selectionMethod = SelectIsoWithoutCoordinates(
                        window, ref combo, target);
                    Log("ISO selection method: " + selectionMethod + ".");
                    Thread.Sleep(250);
                    for (int check = 0; check < 10; check++)
                    {
                        if (DismissDarkroomPropertyError(window))
                            throw new InvalidOperationException(
                                "Darkroom reported that ISO " +
                                target.Value + " could not be set.");
                        Thread.Sleep(150);
                    }
                    string confirmed = ReadComboSelection(combo);
                    if (!String.Equals(
                            confirmed, target.Value,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "Darkroom did not confirm target ISO " +
                            target.Value + ".");
                    currentDarkroomIso = confirmed;
                    currentIsoReadAt = DateTime.Now;
                    finalStatus = "Done - Darkroom ISO now matches target ISO " +
                                  confirmed + ".";
                    Log(finalStatus);
                }

                StartBoothModeAfterAction(window);
                lastAppliedTargetIso = desiredIso;
                lastAppliedDarkroomProcessId = process.Id;
                finalStatus += " Booth mode started.";
                Log(finalStatus);
            }
            catch (Exception exception)
            {
                failed = true;
                finalStatus = "Action stopped: " + exception.Message;
                Log(finalStatus);
                if (process != null && IsWindow(process.MainWindowHandle))
                {
                    try
                    {
                        Log("Fail-safe recovery: restoring Booth Mode...");
                        StartBoothModeAfterAction(process.MainWindowHandle);
                        finalStatus += " Booth Mode was restored.";
                        Log("Fail-safe recovery completed.");
                    }
                    catch (Exception recoveryException)
                    {
                        finalStatus += " Booth Mode recovery also failed: " +
                            recoveryException.Message;
                        Log("Fail-safe recovery failed: " +
                            recoveryException.Message);
                    }
                }
            }
            finally
            {
                if (coverShown)
                {
                    if (keepCoverVisible)
                        KeepManualCoverVisibleAfterAction();
                    else
                        HideFullscreenCovers();
                }
                if (process != null)
                    process.Dispose();
            }
            manualActionFailed = failed;
            manualActionStatus = finalStatus;
            manualActionStatusUntil = DateTime.Now.AddSeconds(12);
            if (automatic && failed)
            {
                nextAutomaticAttemptAt = DateTime.Now.AddSeconds(
                    Math.Max(30, stabilitySeconds));
                Log("Automatic retry delayed until " +
                    nextAutomaticAttemptAt.ToString("HH:mm:ss") + ".");
            }
            else if (!failed)
            {
                nextAutomaticAttemptAt = DateTime.MinValue;
            }
            manualActionRunning = false;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    runActionButton.Enabled = true;
                    runActionButton.Text = "Doel-ISO nu toepassen";
                    coverToggleButton.Enabled = true;
                    pauseButton.Enabled = true;
                    UpdateCoverToggleButton();
                    RefreshUi();
                });
            }
            catch { }
        }

        private void ToggleManualCover()
        {
            if (manualActionRunning)
                return;

            manualCoverVisible = !manualCoverVisible;
            if (manualCoverVisible)
            {
                ShowFullscreenCovers(true);
                Log("Fullscreen covers shown manually.");
            }
            else
            {
                HideFullscreenCovers();
                Log("Fullscreen covers hidden manually.");
            }
            UpdateCoverToggleButton();
        }

        private void UpdateCoverToggleButton()
        {
            coverToggleButton.Text = manualCoverVisible
                ? "AFDEKKING STOPPEN"
                : "VOLLEDIG SCHERM AFDEKKEN";
            coverToggleButton.BackColor = manualCoverVisible
                ? Color.FromArgb(187, 45, 59)
                : Color.FromArgb(33, 38, 45);
            coverToggleButton.ForeColor = manualCoverVisible
                ? Color.White
                : Color.FromArgb(121, 192, 255);
        }

        private bool ShowFullscreenCovers(bool keepControlsVisible)
        {
            MethodInvoker show = delegate
            {
                foreach (ActionCoverForm existing in actionCovers)
                {
                    existing.Close();
                    existing.Dispose();
                }
                actionCovers.Clear();

                foreach (Screen screen in Screen.AllScreens)
                {
                    ActionCoverForm cover =
                        new ActionCoverForm(screen.Bounds,
                            GetActiveCoverTitle(),
                            GetActiveCoverMessage());
                    actionCovers.Add(cover);
                    cover.Show();
                    cover.BringToFront();
                }

                if (manualCoverControl != null)
                {
                    manualCoverControl.Close();
                    manualCoverControl.Dispose();
                    manualCoverControl = null;
                }
                if (keepControlsVisible)
                    ShowManualCoverControl();
                StartCoverZOrderGuard();
            };
            if (InvokeRequired)
                Invoke(show);
            else
                show();

            Log("Fullscreen covers shown on " +
                Screen.AllScreens.Length + " display(s).");
            return true;
        }

        private void StartCoverZOrderGuard()
        {
            RefreshGuardedCoverHandles();
            if (coverSystemEventHook == IntPtr.Zero)
            {
                coverSystemEventHook = SetWinEventHook(
                    EventSystemForeground, EventSystemPopupStart,
                    IntPtr.Zero, coverWinEventDelegate,
                    0, 0, WinEventOutOfContext);
            }
            if (coverObjectShowHook == IntPtr.Zero)
            {
                coverObjectShowHook = SetWinEventHook(
                    EventObjectShow, EventObjectShow,
                    IntPtr.Zero, coverWinEventDelegate,
                    0, 0, WinEventOutOfContext);
            }
            coverGuardTimer.Start();
            RaiseGuardedCoverHandles();
            Log("Instant fullscreen cover guard started; systemHook=" +
                coverSystemEventHook + ", showHook=" +
                coverObjectShowHook + ".");
        }

        private void StopCoverZOrderGuard()
        {
            coverGuardTimer.Stop();
            if (coverSystemEventHook != IntPtr.Zero)
            {
                UnhookWinEvent(coverSystemEventHook);
                coverSystemEventHook = IntPtr.Zero;
            }
            if (coverObjectShowHook != IntPtr.Zero)
            {
                UnhookWinEvent(coverObjectShowHook);
                coverObjectShowHook = IntPtr.Zero;
            }
            lock (coverGuardSync)
            {
                guardedCoverHandles = new IntPtr[0];
                guardedManualControlHandle = IntPtr.Zero;
            }
        }

        private void RefreshGuardedCoverHandles()
        {
            List<IntPtr> handles = new List<IntPtr>();
            foreach (ActionCoverForm cover in actionCovers)
            {
                if (!cover.IsDisposed && cover.IsHandleCreated)
                    handles.Add(cover.Handle);
            }
            IntPtr manualHandle = IntPtr.Zero;
            if (manualCoverControl != null &&
                !manualCoverControl.IsDisposed &&
                manualCoverControl.IsHandleCreated)
                manualHandle = manualCoverControl.Handle;

            lock (coverGuardSync)
            {
                guardedCoverHandles = handles.ToArray();
                guardedManualControlHandle = manualHandle;
            }
        }

        private void CoverWinEvent(
            IntPtr hook, uint eventType, IntPtr window,
            int objectId, int childId,
            uint eventThread, uint eventTime)
        {
            RaiseGuardedCoverHandles();
        }

        private void RaiseGuardedCoverHandles()
        {
            if (Interlocked.Exchange(ref coverRaiseActive, 1) != 0)
                return;
            try
            {
                IntPtr[] handles;
                IntPtr manualHandle;
                lock (coverGuardSync)
                {
                    handles = guardedCoverHandles;
                    manualHandle = guardedManualControlHandle;
                }
                foreach (IntPtr handle in handles)
                {
                    if (handle != IntPtr.Zero && IsWindow(handle))
                        SetWindowPos(
                            handle, new IntPtr(-1),
                            0, 0, 0, 0,
                            0x0001 | 0x0002 | 0x0010);
                }
                if (manualHandle != IntPtr.Zero && IsWindow(manualHandle))
                    SetWindowPos(
                        manualHandle, new IntPtr(-1),
                        0, 0, 0, 0,
                        0x0001 | 0x0002 | 0x0010);
            }
            finally
            {
                Interlocked.Exchange(ref coverRaiseActive, 0);
            }
        }

        private void RaiseFullscreenCovers()
        {
            foreach (ActionCoverForm cover in actionCovers)
            {
                if (!cover.IsDisposed && cover.IsHandleCreated)
                    SetWindowPos(
                        cover.Handle, new IntPtr(-1),
                        0, 0, 0, 0,
                        0x0001 | 0x0002 | 0x0010 | 0x0040);
            }
            if (manualCoverControl != null &&
                !manualCoverControl.IsDisposed &&
                manualCoverControl.IsHandleCreated)
            {
                SetWindowPos(
                    manualCoverControl.Handle, new IntPtr(-1),
                    0, 0, 0, 0,
                    0x0001 | 0x0002 | 0x0010 | 0x0040);
            }
        }

        private void ShowManualCoverControl()
        {
            if (manualCoverControl != null)
            {
                manualCoverControl.Close();
                manualCoverControl.Dispose();
            }
            manualCoverControl = new ManualCoverControlForm(
                Screen.PrimaryScreen.WorkingArea,
                delegate { ToggleManualCover(); });
            manualCoverControl.Show();
            manualCoverControl.BringToFront();
            RefreshGuardedCoverHandles();
            RaiseGuardedCoverHandles();
        }
        private void KeepManualCoverVisibleAfterAction()
        {
            try
            {
                MethodInvoker restoreControls = delegate
                {
                    ShowManualCoverControl();
                    UpdateCoverToggleButton();
                };
                if (InvokeRequired)
                    Invoke(restoreControls);
                else
                    restoreControls();
                Log("Manually shown fullscreen covers remain visible.");
            }
            catch { }
        }

        private void HideFullscreenCovers()
        {
            try
            {
                MethodInvoker hide = delegate
                {
                    StopCoverZOrderGuard();
                    foreach (ActionCoverForm cover in actionCovers)
                    {
                        cover.Close();
                        cover.Dispose();
                    }
                    actionCovers.Clear();
                    if (manualCoverControl != null)
                    {
                        manualCoverControl.Close();
                        manualCoverControl.Dispose();
                        manualCoverControl = null;
                    }
                };
                if (InvokeRequired)
                    Invoke(hide);
                else
                    hide();
                Log("Fullscreen covers hidden.");
            }
            catch { }
        }
        private void SetManualActionStatus(string status)
        {
            manualActionStatus = status;
            Log(status);
        }

        private static Process FindDarkroomProcess()
        {
            Process[] candidates = Process.GetProcessesByName("DarkroomBooth");
            Process selected = null;
            foreach (Process candidate in candidates)
            {
                if (selected == null && candidate.MainWindowHandle != IntPtr.Zero)
                    selected = candidate;
                else
                    candidate.Dispose();
            }
            return selected;
        }

        private void EnsureDarkroomReady(IntPtr window)
        {
            if (FindAutomationElementByName(window, "Activate") != null &&
                FindAutomationElementByName(window, "Start Trial") != null)
            {
                LogVisibleDarkroomNames(window, "Activation screen detected");
                throw new InvalidOperationException(
                    "Darkroom is showing Activate/Start Trial instead of Events.");
            }
        }

        private IntPtr EnsureCameraSettingsOpen(
            Process process, IntPtr window)
        {
            IntPtr combo = FindIsoControlWithAutomation(window);
            if (combo != IntPtr.Zero)
                return combo;

            SetForegroundWindow(window);
            SendVirtualKey(Keys.Escape);
            Thread.Sleep(700);

            IntPtr toolbar = FindExactDarkroomToolbar(process, window);
            if (toolbar == IntPtr.Zero)
            {
                string backDetail;
                if (TryInvokeSemanticControl(
                        process.Id, "Back to Events", out backDetail))
                {
                    Log("Left Global Settings through exact semantic control (" +
                        backDetail + "); no coordinates used.");
                    Thread.Sleep(900);
                    toolbar = FindExactDarkroomToolbar(process, window);
                }
            }
            if (toolbar == IntPtr.Zero)
                throw new InvalidOperationException(
                    "Darkroom's exact PhotoBooth toolbar could not be " +
                    "validated. No Darkroom command was sent.");

            IntPtr commandTarget = GetParent(toolbar);
            if (commandTarget == IntPtr.Zero)
                throw new InvalidOperationException(
                    "Darkroom's validated toolbar has no command target. " +
                    "No Darkroom command was sent.");

            if (!PostMessage(
                    commandTarget, WmCommand,
                    new IntPtr(DarkroomSettingsToolId), toolbar))
                throw new InvalidOperationException(
                    "Darkroom rejected the single native Settings command.");
            Log("Activated Settings exactly once through validated CXToolbar " +
                "handle=" + toolbar + ".");

            Thread.Sleep(700);
            if (!PostMessage(
                    commandTarget, WmCommand,
                    new IntPtr(DarkroomCameraSettingsCommand), IntPtr.Zero))
                throw new InvalidOperationException(
                    "Darkroom rejected its native Camera settings command.");
            Log("Requested Darkroom Camera settings directly through native " +
                "command " + DarkroomCameraSettingsCommand + ".");
            combo = WaitForStableVisibleChildById(window, 107, 1800);
            if (combo != IntPtr.Zero)
            {
                Log("Reached Darkroom Camera settings directly; stable visible " +
                    "ISO control 107 handle=" + combo + ".");
                return combo;
            }
            Log("Direct Camera command did not expose ISO control 107; " +
                "continuing with bounded native page navigation.");

            // Settings opens Darkroom's current native settings page. Use
            // Darkroom's own Next Settings tool until ISO control 107 appears.
            // Eleven pages means ten advances reach Camera from any page.
            for (int pageDistance = 0; pageDistance <= 10; pageDistance++)
            {
                for (int poll = 0; poll < 6; poll++)
                {
                    combo = FindVisibleChildById(window, 107);
                    if (combo != IntPtr.Zero)
                    {
                        combo = WaitForStableVisibleChildById(
                            window, 107, 1200);
                        if (combo == IntPtr.Zero)
                            continue;
                        Log("Reached Darkroom Camera settings through native " +
                            "page navigation; stable visible ISO control 107 " +
                            "handle=" + combo + ", page advances=" +
                            pageDistance + ".");
                        return combo;
                    }
                    Thread.Sleep(100);
                }

                if (pageDistance == 10)
                    break;
                if (!PostMessage(
                        commandTarget, WmCommand,
                        new IntPtr(DarkroomNextSettingsToolId), toolbar))
                    throw new InvalidOperationException(
                        "Darkroom rejected its native Next Settings command.");
                Log("Advanced one Darkroom settings page through native tool " +
                    DarkroomNextSettingsToolId + "; step=" +
                    (pageDistance + 1) + "/10.");
            }

            throw new InvalidOperationException(
                "Darkroom's native settings pages did not expose ISO control " +
                "107 after a complete bounded cycle. No fallback was used.");
        }
        private IntPtr FindExactDarkroomToolbar(
            Process process, IntPtr mainWindow)
        {
            IntPtr processHandle = OpenProcess(
                ProcessVmRead | ProcessQueryInformation, false, process.Id);
            if (processHandle == IntPtr.Zero)
            {
                Log("Read-only Darkroom process access was unavailable.");
                return IntPtr.Zero;
            }

            try
            {
                uint moduleBase = unchecked(
                    (uint)process.MainModule.BaseAddress.ToInt64());
                uint expectedMainPageVtable = moduleBase + MainPageVtableRva;
                ulong address = 0x10000;
                int informationSize = Marshal.SizeOf(
                    typeof(MemoryBasicInformation));

                while (address < 0x80000000UL)
                {
                    MemoryBasicInformation information;
                    UIntPtr queried = VirtualQueryEx(
                        processHandle, new IntPtr((long)address),
                        out information, new UIntPtr((uint)informationSize));
                    if (queried == UIntPtr.Zero)
                        break;

                    ulong regionStart = unchecked(
                        (ulong)information.BaseAddress.ToInt64());
                    ulong regionSize = information.RegionSize.ToUInt64();
                    ulong next = regionStart + regionSize;
                    if (next <= address)
                        break;

                    bool readable = information.State == MemCommit &&
                        information.Type == MemPrivate &&
                        (information.Protect & PageGuard) == 0 &&
                        (information.Protect & PageNoAccess) == 0;
                    if (readable && regionStart < 0x80000000UL)
                    {
                        IntPtr toolbar = ScanRegionForToolbar(
                            processHandle, process.Id, mainWindow,
                            moduleBase, expectedMainPageVtable,
                            regionStart,
                            Math.Min(regionSize, 0x80000000UL - regionStart));
                        if (toolbar != IntPtr.Zero)
                        {
                            Log("Validated exact CXToolbar through read-only " +
                                "Darkroom object identity; handle=" + toolbar + ".");
                            return toolbar;
                        }
                    }
                    address = next;
                }
            }
            catch (Exception exception)
            {
                Log("Exact toolbar validation failed safely: " +
                    exception.Message);
            }
            finally
            {
                CloseHandle(processHandle);
            }
            return IntPtr.Zero;
        }

        private IntPtr ScanRegionForToolbar(
            IntPtr processHandle, int processId, IntPtr mainWindow,
            uint moduleBase, uint expectedMainPageVtable,
            ulong regionStart, ulong regionSize)
        {
            const int chunkSize = 1024 * 1024;
            byte[] buffer = new byte[chunkSize + 3];
            ulong offset = 0;
            while (offset < regionSize)
            {
                int requested = (int)Math.Min(
                    (ulong)chunkSize, regionSize - offset);
                UIntPtr bytesRead;
                if (!ReadProcessMemory(
                        processHandle,
                        new IntPtr((long)(regionStart + offset)),
                        buffer, new UIntPtr((uint)requested), out bytesRead))
                {
                    offset += (ulong)requested;
                    continue;
                }
                int available = (int)bytesRead.ToUInt64();
                for (int index = 0; index + 4 <= available; index += 4)
                {
                    if (BitConverter.ToUInt32(buffer, index) !=
                        expectedMainPageVtable)
                        continue;
                    uint candidate = unchecked(
                        (uint)(regionStart + offset + (ulong)index));
                    IntPtr toolbar = ValidateMainPageCandidate(
                        processHandle, processId, mainWindow,
                        moduleBase, candidate);
                    if (toolbar != IntPtr.Zero)
                        return toolbar;
                }
                offset += (ulong)requested;
            }
            return IntPtr.Zero;
        }

        private IntPtr ValidateMainPageCandidate(
            IntPtr processHandle, int processId, IntPtr mainWindow,
            uint moduleBase, uint candidate)
        {
            uint mainFrame;
            uint toolbarObject;
            uint settingsObject;
            if (!TryReadUInt32(processHandle, candidate + 0x20, out mainFrame) ||
                !TryReadUInt32(processHandle, candidate + 0x4c, out toolbarObject) ||
                !TryReadUInt32(processHandle, candidate + 0x1a4, out settingsObject))
                return IntPtr.Zero;

            uint vtable;
            uint mainFrameWindow;
            if (mainFrame == 0 ||
                !TryReadUInt32(processHandle, mainFrame, out vtable) ||
                vtable != moduleBase + MainFrameVtableRva ||
                !TryReadUInt32(
                    processHandle, mainFrame + 0x20, out mainFrameWindow) ||
                mainFrameWindow != unchecked((uint)mainWindow.ToInt64()))
                return IntPtr.Zero;

            if (settingsObject != 0 &&
                (!TryReadUInt32(processHandle, settingsObject, out vtable) ||
                 vtable != moduleBase + EventSettingsVtableRva))
                return IntPtr.Zero;

            uint toolbarWindow;
            if (toolbarObject == 0 ||
                !TryReadUInt32(processHandle, toolbarObject, out vtable) ||
                vtable != moduleBase + ToolbarVtableRva ||
                !TryReadUInt32(
                    processHandle, toolbarObject + 0x20, out toolbarWindow))
                return IntPtr.Zero;

            IntPtr handle = new IntPtr(unchecked((int)toolbarWindow));
            if (!IsWindow(handle) ||
                GetDlgCtrlID(handle) != DarkroomToolbarControlId)
                return IntPtr.Zero;
            uint actualProcessId;
            GetWindowThreadProcessId(handle, out actualProcessId);
            return actualProcessId == (uint)processId
                ? handle : IntPtr.Zero;
        }

        private static bool TryReadUInt32(
            IntPtr processHandle, uint address, out uint value)
        {
            byte[] bytes = new byte[4];
            UIntPtr bytesRead;
            bool success = ReadProcessMemory(
                processHandle, new IntPtr(unchecked((int)address)),
                bytes, new UIntPtr(4), out bytesRead);
            value = success && bytesRead.ToUInt64() == 4
                ? BitConverter.ToUInt32(bytes, 0) : 0;
            return success && bytesRead.ToUInt64() == 4;
        }

        private bool TryInvokeSemanticControl(
            int processId, string exactName, out string detail)
        {
            detail = "";
            foreach (IntPtr handle in FindProcessWindows(processId))
            {
                try
                {
                    AutomationElement root = AutomationElement.FromHandle(handle);
                    AutomationElementCollection elements = root.FindAll(
                        TreeScope.Subtree,
                        new PropertyCondition(
                            AutomationElement.NameProperty, exactName));
                    for (int index = 0; index < elements.Count; index++)
                    {
                        if (!elements[index].Current.IsEnabled ||
                            elements[index].Current.IsOffscreen)
                            continue;
                        string method;
                        if (TryInvokeWithoutCoordinates(elements[index], out method))
                        {
                            detail = "UI Automation " + method +
                                ", window=" + handle;
                            return true;
                        }
                    }
                }
                catch { }
            }
            return false;
        }

        private static List<IntPtr> FindProcessWindows(int processId)
        {
            List<IntPtr> windows = new List<IntPtr>();
            EnumWindowProc collect = delegate(IntPtr handle, IntPtr parameter)
            {
                uint ownerProcess;
                GetWindowThreadProcessId(handle, out ownerProcess);
                if (ownerProcess == (uint)processId && !windows.Contains(handle))
                {
                    windows.Add(handle);
                    EnumChildProc collectChild = delegate(
                        IntPtr child, IntPtr childParameter)
                    {
                        if (!windows.Contains(child))
                            windows.Add(child);
                        return true;
                    };
                    EnumChildWindows(handle, collectChild, IntPtr.Zero);
                    GC.KeepAlive(collectChild);
                }
                return true;
            };
            EnumWindows(collect, IntPtr.Zero);
            GC.KeepAlive(collect);
            return windows;
        }
        private void StartBoothModeAfterAction(IntPtr window)
        {
            SetManualActionStatus(
                "Step 3/3 - starting Booth Mode...");

            // Leave Event Settings and address Darkroom's own Start Booth
            // command. This is the same semantic command used by Darkroom's
            // Start Booth Mode control, without locating or clicking it.
            SetForegroundWindow(window);
            SendVirtualKey(Keys.Escape);
            Thread.Sleep(550);
            if (!PostMessage(
                    window, WmCommand,
                    new IntPtr(DarkroomCommandStartBooth), IntPtr.Zero))
                throw new InvalidOperationException(
                    "Darkroom rejected its native Start Booth Mode command.");
            Log("Started Booth Mode through Darkroom command " +
                DarkroomCommandStartBooth +
                "; no coordinates or mouse input used.");
            Thread.Sleep(1600);

            boothMode = true;
            Log("Start Booth Mode command completed.");
        }
        private static bool TryInvokeWithoutCoordinates(
            AutomationElement element, out string method)
        {
            method = "";
            try
            {
                object pattern;
                if (element.TryGetCurrentPattern(
                    InvokePattern.Pattern, out pattern))
                {
                    ((InvokePattern)pattern).Invoke();
                    method = "UI Automation InvokePattern";
                    return true;
                }
                if (element.TryGetCurrentPattern(
                    SelectionItemPattern.Pattern, out pattern))
                {
                    ((SelectionItemPattern)pattern).Select();
                    method = "UI Automation SelectionItemPattern";
                    return true;
                }

            }
            catch { }
            return false;
        }
        private static AutomationElement FindAutomationElementByName(
            IntPtr window, string expectedName)
        {
            try
            {
                AutomationElement root = AutomationElement.FromHandle(window);
                PropertyCondition exact = new PropertyCondition(
                    AutomationElement.NameProperty,
                    expectedName,
                    PropertyConditionFlags.IgnoreCase);
                AutomationElement found = root.FindFirst(
                    TreeScope.Descendants, exact);
                if (found != null)
                    return found;

                AutomationElementCollection all = root.FindAll(
                    TreeScope.Descendants, Condition.TrueCondition);
                for (int index = 0; index < all.Count; index++)
                {
                    string name = all[index].Current.Name;
                    if (!String.IsNullOrWhiteSpace(name) &&
                        name.IndexOf(
                            expectedName,
                            StringComparison.OrdinalIgnoreCase) >= 0)
                        return all[index];
                }
            }
            catch { }
            return null;
        }

        private void LogDarkroomWindowState(
            Process process, IntPtr window)
        {
            try
            {
                System.Windows.Rect rect = AutomationElement
                    .FromHandle(window).Current.BoundingRectangle;
                Log("Darkroom state: pid=" + process.Id +
                    ", title='" + process.MainWindowTitle +
                    "', UIA rect=(" + (int)rect.X + "," +
                    (int)rect.Y + "," + (int)rect.Width + "x" +
                    (int)rect.Height + ").");
            }
            catch (Exception exception)
            {
                Log("Darkroom state logging failed: " +
                    exception.Message);
            }
        }
        private void LogVisibleDarkroomNames(
            IntPtr window, string reason)
        {
            try
            {
                AutomationElement root = AutomationElement.FromHandle(window);
                AutomationElementCollection all = root.FindAll(
                    TreeScope.Descendants, Condition.TrueCondition);
                List<string> names = new List<string>();
                for (int index = 0; index < all.Count && names.Count < 25; index++)
                {
                    string name = all[index].Current.Name;
                    if (!String.IsNullOrWhiteSpace(name) &&
                        !names.Contains(name))
                        names.Add(name.Replace("\r", " ").Replace("\n", " "));
                }
                Log(reason + ": visible names=[" +
                    String.Join(" | ", names.ToArray()) + "].");
            }
            catch (Exception exception)
            {
                Log(reason + ": UI-name logging failed: " +
                    exception.Message);
            }
        }
        private IntPtr FindIsoControlWithAutomation(IntPtr window)
        {
            try
            {
                AutomationElement root = AutomationElement.FromHandle(window);
                AndCondition isoCondition = new AndCondition(
                    new PropertyCondition(
                        AutomationElement.AutomationIdProperty, "107"),
                    new PropertyCondition(
                        AutomationElement.ClassNameProperty, "ComboBox"));
                AutomationElement isoElement = root.FindFirst(
                    TreeScope.Descendants, isoCondition);
                if (isoElement != null &&
                    isoElement.Current.NativeWindowHandle != 0 &&
                    !isoElement.Current.IsOffscreen)
                {
                    IntPtr handle = new IntPtr(
                        isoElement.Current.NativeWindowHandle);
                    if (IsWindowVisible(handle))
                    {
                        Log("Visible ISO control discovered with Windows UI " +
                            "Automation: automationId=107, handle=" +
                            handle + ".");
                        return handle;
                    }
                }
            }
            catch (Exception exception)
            {
                Log("UI Automation ISO discovery failed: " +
                    exception.Message);
            }

            IntPtr nativeFallback = FindVisibleChildById(window, 107);
            if (nativeFallback != IntPtr.Zero)
                Log("ISO control found by native control ID 107 fallback; " +
                    "still no coordinates or mouse input used.");
            return nativeFallback;
        }
        private static List<IntPtr> FindChildrenById(IntPtr root, int controlId)
        {
            List<IntPtr> found = new List<IntPtr>();
            EnumChildProc callback = delegate(IntPtr handle, IntPtr parameter)
            {
                if (GetDlgCtrlID(handle) == controlId)
                    found.Add(handle);
                return true;
            };
            EnumChildWindows(root, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return found;
        }

        private static List<ComboItem> ReadComboItems(IntPtr combo)
        {
            long count = SendMessageWithTimeout(
                combo, 0x0146, IntPtr.Zero, IntPtr.Zero).ToInt64();
            if (count <= 0 || count > 100)
                throw new InvalidOperationException("The ISO list is empty.");
            List<ComboItem> items = new List<ComboItem>();
            for (int index = 0; index < count; index++)
            {
                long length = SendMessageWithTimeout(
                    combo, 0x0149,
                    new IntPtr(index), IntPtr.Zero).ToInt64();
                if (length < 0 || length > 128)
                    continue;
                StringBuilder value = new StringBuilder((int)length + 1);
                SendMessageWithTimeout(
                    combo, 0x0148, new IntPtr(index), value);
                items.Add(new ComboItem(index, value.ToString()));
            }
            return items;
        }

        private static ComboItem FindClosestNumericIso(
            List<ComboItem> items, int requestedIso)
        {
            ComboItem closest = null;
            double closestDistance = Double.MaxValue;
            foreach (ComboItem item in items)
            {
                int numericIso;
                if (!Int32.TryParse(item.Value.Trim(), out numericIso) ||
                    numericIso <= 0)
                    continue;
                double distance = Math.Abs(
                    Math.Log((double)numericIso / requestedIso));
                if (closest == null || distance < closestDistance)
                {
                    closest = item;
                    closestDistance = distance;
                }
            }
            return closest;
        }

        private string SelectIsoWithoutCoordinates(
            IntPtr window, ref IntPtr combo, ComboItem target)
        {
            combo = WaitForStableVisibleChildById(window, 107, 1800);
            if (combo == IntPtr.Zero)
                throw new InvalidOperationException(
                    "Darkroom ISO control 107 did not remain visibly stable.");
            Log("Opening the native ISO dropdown.");
            if (!PostMessage(
                    combo, 0x014f, new IntPtr(1), IntPtr.Zero))
                throw new InvalidOperationException(
                    "Darkroom rejected opening the native ISO dropdown.");
            Thread.Sleep(180);
            int currentIndex = (int)SendMessageWithTimeout(
                combo, 0x0147, IntPtr.Zero, IntPtr.Zero).ToInt64();
            if (currentIndex < 0)
                throw new InvalidOperationException(
                    "Darkroom did not expose its current ISO list position.");
            int steps = Math.Abs(target.Index - currentIndex);
            Keys direction = target.Index > currentIndex
                ? Keys.Down : Keys.Up;
            for (int step = 0; step < steps; step++)
            {
                PostComboKey(combo, direction);
                Thread.Sleep(35);
            }
            Log("Native dropdown reached ISO " + target.Value +
                "; confirming the highlighted item.");
            PostComboKey(combo, Keys.Enter);

            DateTime deadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < deadline)
            {
                long selected = SendMessageWithTimeout(
                    combo, 0x0147, IntPtr.Zero, IntPtr.Zero).ToInt64();
                if (selected == target.Index)
                {
                    Thread.Sleep(350);
                    return "native ComboBox dropdown keyboard selection " +
                        "(coordinate-free, timeout-safe)";
                }
                Thread.Sleep(100);
            }
            throw new InvalidOperationException(
                "Darkroom did not accept the ISO dropdown selection in time.");
        }

        private static void PostComboKey(IntPtr combo, Keys key)
        {
            if (!PostMessage(
                    combo, 0x0100,
                    new IntPtr((int)key), IntPtr.Zero) ||
                !PostMessage(
                    combo, 0x0101,
                    new IntPtr((int)key), IntPtr.Zero))
                throw new InvalidOperationException(
                    "Darkroom rejected native ISO dropdown keyboard input.");
        }
        private bool DismissDarkroomPropertyError(IntPtr window)
        {
            try
            {
                AutomationElement root = AutomationElement.FromHandle(window);
                AutomationElement error = root.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(
                        AutomationElement.NameProperty,
                        "The property could not be set.",
                        PropertyConditionFlags.IgnoreCase));
                if (error == null)
                    return false;

                AutomationElement dialog = error;
                AutomationElement parent =
                    TreeWalker.ControlViewWalker.GetParent(dialog);
                while (parent != null && !parent.Equals(root))
                {
                    dialog = parent;
                    parent = TreeWalker.ControlViewWalker.GetParent(dialog);
                }
                int dialogHandle = dialog.Current.NativeWindowHandle;
                if (dialogHandle != 0)
                    PostMessage(
                        new IntPtr(dialogHandle), 0x0010,
                        IntPtr.Zero, IntPtr.Zero);
                Log("Darkroom property-error dialog detected and closed.");
                return true;
            }
            catch (Exception exception)
            {
                Log("Darkroom property-error check failed: " +
                    exception.Message);
                return false;
            }
        }

        private static void SendVirtualKey(Keys key)
        {
            byte code = (byte)key;
            keybd_event(code, 0, 0, UIntPtr.Zero);
            keybd_event(code, 0, 0x0002, UIntPtr.Zero);
            Thread.Sleep(45);
        }
        private void EnsureSerialConnected()
        {
            if (shuttingDown)
                return;
            if (Interlocked.Exchange(ref serialFaulted, 0) != 0)
                CloseSerial();

            if (serial != null && serial.IsOpen)
            {
                bool neverValidated = lastJvdpLineAt < serialOpenedAt &&
                    DateTime.Now - serialOpenedAt > TimeSpan.FromSeconds(5);
                bool becameSilent = lastJvdpLineAt >= serialOpenedAt &&
                    DateTime.Now - lastJvdpLineAt > TimeSpan.FromSeconds(5);
                if (neverValidated || becameSilent)
                {
                    string rejectedPort = activeSerialPort;
                    lock (serialRetryAfter)
                        serialRetryAfter[rejectedPort] =
                            DateTime.Now.AddSeconds(20);
                    Log((neverValidated ? "Rejected " : "Disconnected ") +
                        rejectedPort +
                        ": no JVDP light data received within 5 seconds.");
                    lock (sensor.Sync)
                        sensor.SerialStatus = neverValidated
                            ? rejectedPort + " is not the ESP"
                            : rejectedPort + " stopped sending light data";
                    serialReady = false;
                    CloseSerial();
                }
                else
                    return;
            }

            CloseSerial();
            List<string> candidates = FindCandidateSerialPorts();
            foreach (string portName in candidates)
            {
                DateTime retryAt;
                lock (serialRetryAfter)
                {
                    if (serialRetryAfter.TryGetValue(portName, out retryAt) &&
                        retryAt > DateTime.Now)
                        continue;
                }

                SerialPort candidate = null;
                try
                {
                    candidate = new SerialPort(portName, 115200);
                    candidate.DtrEnable = false;
                    candidate.RtsEnable = false;
                    candidate.NewLine = "\n";
                    candidate.ReadTimeout = 1000;
                    candidate.DataReceived += SerialDataReceived;
                    candidate.Open();
                    if (shuttingDown)
                    {
                        candidate.DataReceived -= SerialDataReceived;
                        candidate.Dispose();
                        return;
                    }
                    serial = candidate;
                    activeSerialPort = portName;
                    serialOpenedAt = DateTime.Now;
                    lastJvdpLineAt = DateTime.MinValue;
                    serialReady = false;
                    lock (sensor.Sync)
                    {
                        sensor.SerialStatus = portName + " opened; checking ESP";
                        sensor.LastSerialError = "";
                    }
                    Log("Opened " + portName +
                        "; waiting for JVDP sensor signature.");
                    return;
                }
                catch (Exception exception)
                {
                    if (candidate != null)
                    {
                        try
                        {
                            candidate.DataReceived -= SerialDataReceived;
                            candidate.Dispose();
                        }
                        catch { }
                    }
                    lock (serialRetryAfter)
                        serialRetryAfter[portName] = DateTime.Now.AddSeconds(5);
                    lock (sensor.Sync)
                        sensor.LastSerialError = exception.Message;
                }
            }

            lock (sensor.Sync)
                sensor.SerialStatus = candidates.Count == 0
                    ? "No serial ports found"
                    : "ESP serial port unavailable";
            serialReady = false;
        }

        private static List<string> FindCandidateSerialPorts()
        {
            Dictionary<string, int> scores =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string port in SerialPort.GetPortNames())
                scores[port] = 10;

            try
            {
                using (RegistryKey usbRoot = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Enum\USB"))
                {
                    if (usbRoot != null)
                    {
                        foreach (string deviceName in usbRoot.GetSubKeyNames())
                        {
                            using (RegistryKey device =
                                usbRoot.OpenSubKey(deviceName))
                            {
                                if (device == null)
                                    continue;
                                foreach (string instanceName in
                                    device.GetSubKeyNames())
                                {
                                    using (RegistryKey instance =
                                        device.OpenSubKey(instanceName))
                                    using (RegistryKey parameters =
                                        instance == null ? null :
                                        instance.OpenSubKey("Device Parameters"))
                                    {
                                        if (instance == null || parameters == null)
                                            continue;
                                        string port = Convert.ToString(
                                            parameters.GetValue("PortName", ""));
                                        if (String.IsNullOrWhiteSpace(port))
                                            continue;
                                        string identity = (deviceName + " " +
                                            Convert.ToString(instance.GetValue(
                                                "FriendlyName", "")) + " " +
                                            Convert.ToString(instance.GetValue(
                                                "DeviceDesc", "")) + " " +
                                            Convert.ToString(instance.GetValue(
                                                "Mfg", ""))).ToUpperInvariant();
                                        int score = 40;
                                        if (identity.Contains("VID_303A") &&
                                            identity.Contains("PID_1001"))
                                            score = 100;
                                        else if (identity.Contains("ESP32") ||
                                                 identity.Contains("ESPRESSIF"))
                                            score = 90;
                                        else if (identity.Contains("USB SERIAL") ||
                                                 identity.Contains("SERIEEL USB"))
                                            score = 60;
                                        int existing;
                                        if (!scores.TryGetValue(port, out existing) ||
                                            score > existing)
                                            scores[port] = score;
                                    }
                                }
                            }
                        }
                    }
                }

                using (RegistryKey serialMap = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DEVICEMAP\SERIALCOMM"))
                {
                    if (serialMap != null)
                    {
                        foreach (string valueName in serialMap.GetValueNames())
                        {
                            string port = Convert.ToString(
                                serialMap.GetValue(valueName, ""));
                            if (String.IsNullOrWhiteSpace(port))
                                continue;
                            int score = valueName.ToUpperInvariant().Contains("USB")
                                ? 50 : 20;
                            int existing;
                            if (!scores.TryGetValue(port, out existing) ||
                                score > existing)
                                scores[port] = score;
                        }
                    }
                }
            }
            catch { }

            List<string> ports = new List<string>(scores.Keys);
            ports.Sort(delegate(string left, string right)
            {
                int scoreCompare = scores[right].CompareTo(scores[left]);
                if (scoreCompare != 0)
                    return scoreCompare;
                return SerialPortNumber(left).CompareTo(SerialPortNumber(right));
            });
            return ports;
        }

        private static int SerialPortNumber(string port)
        {
            Match match = Regex.Match(port ?? "", @"(\d+)$");
            int number;
            return match.Success && Int32.TryParse(match.Groups[1].Value, out number)
                ? number : Int32.MaxValue;
        }

        private void CloseSerial()
        {
            SerialPort old;
            lock (serialSync)
            {
                old = serial;
                serial = null;
                activeSerialPort = "";
                serialOpenedAt = DateTime.MinValue;
                lastJvdpLineAt = DateTime.MinValue;
                serialReady = false;
            }
            if (old == null)
                return;
            try
            {
                old.DataReceived -= SerialDataReceived;
                if (old.IsOpen)
                    old.Close();
            }
            catch { }
            old.Dispose();
        }

        private void SerialDataReceived(object sender, SerialDataReceivedEventArgs args)
        {
            try
            {
                SerialPort active = sender as SerialPort;
                if (active == null || !ReferenceEquals(active, serial) ||
                    !active.IsOpen)
                    return;

                while (active.BytesToRead > 0)
                {
                    string line = active.ReadLine().Trim();
                    Match machineMatch = Regex.Match(
                        line, @"^JVDP\|light=(\d{1,3})(?:\|iso=\d+)?$");
                    if (!machineMatch.Success)
                        continue;

                    int light = Int32.Parse(machineMatch.Groups[1].Value);
                    if (light < 0 || light > 100)
                        continue;
                    int mappedIso = MapLightToIso(light);
                    lock (sensor.Sync)
                    {
                        lastJvdpLineAt = DateTime.Now;
                        if (sensor.MappedIso != mappedIso)
                        {
                            sensor.MappedIso = mappedIso;
                            sensor.CandidateSince = DateTime.Now;
                            Log(string.Format(
                                "PC mapped light {1} to target ISO {0}.",
                                mappedIso, light));
                        }
                        sensor.Light = light;
                        sensor.SerialStatus = active.PortName + " connected";
                    }
                    serialReady = true;
                }
            }
            catch (TimeoutException) { }
            catch (Exception exception)
            {
                string failedPort = activeSerialPort;
                if (!String.IsNullOrWhiteSpace(failedPort))
                {
                    lock (serialRetryAfter)
                        serialRetryAfter[failedPort] = DateTime.Now.AddSeconds(5);
                }
                lock (sensor.Sync)
                {
                    sensor.SerialStatus = String.IsNullOrWhiteSpace(failedPort)
                        ? "ESP disconnected"
                        : failedPort + " disconnected";
                    sensor.LastSerialError = exception.Message;
                }
                serialReady = false;
                Interlocked.Exchange(ref serialFaulted, 1);
            }
        }

        private void RefreshUi()
        {
            int light;
            int targetIso;
            DateTime candidateSince;
            string serialStatus;
            DateTime lastMeasurement;
            lock (sensor.Sync)
            {
                light = sensor.Light;
                targetIso = sensor.MappedIso;
                candidateSince = sensor.CandidateSince;
                serialStatus = sensor.SerialStatus;
                lastMeasurement = lastJvdpLineAt;
            }

            lightLabel.Text = light >= 0 ? light.ToString() : "—";
            targetIsoLabel.Text = targetIso > 0 ? targetIso.ToString() : "—";
            activeRangeLabel.Text = GetActiveRangeText(light);
            rangeControl.UpdateData(GetActiveIsoBands(), light, targetIso);
            if (lastMeasurement == DateTime.MinValue)
            {
                lastMeasuredLabel.Text = "Wachten op eerste meting";
            }
            else
            {
                double age = Math.Max(0,
                    (DateTime.Now - lastMeasurement).TotalSeconds);
                lastMeasuredLabel.Text = "Laatste meting: " +
                    lastMeasurement.ToString("HH:mm:ss") + " · " +
                    (age < 1 ? "zojuist" : String.Format("{0:0} sec geleden", age));
            }
            trayIcon.Text = light >= 0 && targetIso > 0
                ? "JvdP Lichtsensor - licht " + light + " naar ISO " + targetIso
                : "JvdP Lichtsensor - wachten op lichtdata";

            bool sensorConnected = serialReady;
            SetStatusLabel(serialLabel,
                sensorConnected ? "Verbonden" : "Sensor zoeken",
                sensorConnected);
            SetStatusLabel(
                darkroomLabel,
                darkroomRunning ? "Verbonden" : "Niet actief",
                darkroomRunning);
            SetStatusLabel(
                boothLabel,
                boothMode ? "Actief" : "Niet actief",
                boothMode);

            string isoText = currentDarkroomIso;
            if (isoText == "Unknown")
                isoText = "Onbekend";
            if (currentIsoReadAt != DateTime.MinValue)
                isoText += " · gecontroleerd";
            SetStatusLabel(
                currentIsoLabel,
                isoText,
                currentDarkroomIso != "Unknown");

            detailsSummaryLabel.Text =
                (sensorConnected ? "Sensor verbonden" : "Sensor zoeken") +
                " · " +
                (darkroomRunning ? "Darkroom verbonden" : "Darkroom niet actief");
            detailsSummaryLabel.ForeColor = sensorConnected && darkroomRunning
                ? Color.FromArgb(34, 132, 67)
                : !darkroomRunning
                    ? Color.FromArgb(186, 36, 36)
                    : Color.FromArgb(137, 87, 0);

            double stableSeconds = 0.0;
            if (candidateSince != DateTime.MinValue)
            {
                stableSeconds = Math.Min(
                    stabilitySeconds,
                    (DateTime.Now - candidateSince).TotalSeconds);
            }
            countdownLabel.Text = string.Format(
                "{0:0.0} / {1:0.0} sec", stableSeconds, stabilitySeconds);
            progressFill.Width = (int)Math.Round(
                progressFill.Parent.ClientSize.Width *
                stableSeconds / stabilitySeconds);
            progressFill.BackColor = stableSeconds >= stabilitySeconds
                ? Color.FromArgb(34, 132, 67)
                : Color.FromArgb(31, 111, 235);

            TryStartAutomaticAction(targetIso, stableSeconds, sensorConnected);

            bool ready = sensorConnected && targetIso > 0 &&
                darkroomRunning && boothMode && !paused;
            if (manualActionRunning)
            {
                overallStatusLabel.Text = "ISO toepassen…";
                overallStatusLabel.ForeColor = Color.FromArgb(137, 87, 0);
                modeLabel.Text = "Automatisch actief";
            }
            else if (paused)
            {
                overallStatusLabel.Text = "Automatisch gestopt";
                overallStatusLabel.ForeColor = Color.FromArgb(99, 108, 118);
                modeLabel.Text = "Automatisch gestopt";
            }
            else if (!sensorConnected || targetIso <= 0)
            {
                overallStatusLabel.Text = "Sensor zoeken";
                overallStatusLabel.ForeColor = Color.FromArgb(137, 87, 0);
                modeLabel.Text = "Automatisch actief";
            }
            else if (!darkroomRunning || !boothMode)
            {
                overallStatusLabel.Text = "Actie nodig";
                overallStatusLabel.ForeColor = Color.FromArgb(186, 36, 36);
                modeLabel.Text = "Automatisch actief";
            }
            else
            {
                overallStatusLabel.Text = "Klaar";
                overallStatusLabel.ForeColor = Color.FromArgb(34, 132, 67);
                modeLabel.Text = "Automatisch actief";
            }
            modeLabel.ForeColor = paused
                ? Color.FromArgb(137, 87, 0)
                : Color.FromArgb(20, 102, 196);
            UpdateHeaderStatus(sensorConnected, targetIso, ready);

            bool canApply = !manualActionRunning && sensorConnected &&
                targetIso > 0 && darkroomRunning;
            runActionButton.Enabled = canApply;
            runActionButton.BackColor = canApply
                ? Color.FromArgb(31, 111, 235)
                : Color.FromArgb(225, 228, 233);
            runActionButton.ForeColor = canApply
                ? Color.White
                : Color.FromArgb(112, 119, 128);
            runActionButton.Text = targetIso > 0
                ? "ISO " + targetIso + " nu toepassen"
                : "Doel-ISO nu toepassen";
            pauseButton.Enabled = !manualActionRunning;
            pauseButton.Text = paused
                ? "Automatisch aanpassen starten"
                : "Automatisch aanpassen stoppen";
            statusPanelToggleButton.Enabled = !manualActionRunning;
            statusPanelToggleButton.Text = pauseButton.Text;
            trayStatusMenuItem.Text = overallStatusLabel.Text + " — " +
                detailsSummaryLabel.Text;
            trayPauseMenuItem.Text = pauseButton.Text;
            trayApplyMenuItem.Text = targetIso > 0
                ? "ISO " + targetIso + " toepassen"
                : "Doel-ISO toepassen";
            trayApplyMenuItem.Enabled = canApply;

            bool showActionStatus =
                manualActionRunning || DateTime.Now < manualActionStatusUntil;
            actionLabel.Text = showActionStatus
                ? manualActionStatus
                : BuildActionText(targetIso, stableSeconds, sensorConnected);
            actionLabel.ForeColor = showActionStatus && manualActionFailed
                ? Color.FromArgb(186, 36, 36)
                : (manualActionRunning
                    ? Color.FromArgb(137, 87, 0)
                    : Color.FromArgb(71, 78, 88));
            LayoutMainChrome();
            LayoutDashboardWidths();
            FitTextControl(lastMeasuredLabel);
            FitTextControl(detailsSummaryLabel);
            FitTextControl(actionLabel);
        }

        private void UpdateHeaderStatus(
            bool sensorConnected, int targetIso, bool ready)
        {
            Color color = overallStatusLabel.ForeColor;
            statusButton.Text = overallStatusLabel.Text + "  ›";
            statusButton.ForeColor = color;
            statusButton.FlatAppearance.BorderColor = color;
            statusButton.BackColor = paused
                ? Color.FromArgb(247, 248, 250)
                : ready
                    ? Color.FromArgb(240, 249, 243)
                    : Color.FromArgb(255, 247, 235);
            statusPanelTitle.Text = overallStatusLabel.Text;
            statusPanelTitle.ForeColor = color;

            if (manualActionRunning)
            {
                statusPanelMessage.Text =
                    "De ISO-instelling wordt nu in Darkroom aangepast. " +
                    "Wacht tot deze bewerking klaar is.";
            }
            else if (paused)
            {
                statusPanelMessage.Text =
                    "Automatisch aanpassen staat uit en blijft ook na een " +
                    "herstart uit. Klik op Starten om het later weer aan " +
                    "te zetten.";
            }
            else if (!sensorConnected || targetIso <= 0)
            {
                statusPanelMessage.Text =
                    "Er komt nog geen geldige lichtmeting binnen. Controleer " +
                    "of de JvdP-lichtsensor is aangesloten en actief is.";
            }
            else if (!darkroomRunning)
            {
                statusPanelMessage.Text =
                    "Darkroom Booth is niet actief. Start Darkroom Booth; " +
                    "daarna kan de software de doel-ISO automatisch toepassen.";
            }
            else if (!boothMode)
            {
                statusPanelMessage.Text =
                    "Darkroom is geopend, maar Booth Mode is niet actief. " +
                    "Start Booth Mode om automatisch aanpassen te gebruiken.";
            }
            else
            {
                statusPanelMessage.Text =
                    "De lichtsensor en Darkroom zijn verbonden. Automatisch " +
                    "aanpassen staat aan en gebruikt de ingestelde wachttijd.";
            }
        }

        private string GetActiveRangeText(int light)
        {
            if (light < 0)
                return "—";
            int lower = 0;
            foreach (IsoBand band in GetActiveIsoBands())
            {
                if (light <= band.MaximumLight)
                    return lower + "–" + band.MaximumLight;
                lower = band.MaximumLight + 1;
            }
            return "—";
        }

        private void TryStartAutomaticAction(
            int targetIso, double stableSeconds, bool serialReady)
        {
            if (paused || manualActionRunning || !serialReady ||
                targetIso <= 0 || !darkroomRunning ||
                stableSeconds < stabilitySeconds ||
                DateTime.Now < nextAutomaticAttemptAt)
                return;

            if (lastAppliedTargetIso == targetIso &&
                lastAppliedDarkroomProcessId == darkroomProcessId)
                return;

            if (String.Equals(
                    currentDarkroomIso, targetIso.ToString(),
                    StringComparison.OrdinalIgnoreCase))
                return;

            StartTargetIsoAction(true);
        }

        private void SetStatusLabel(Label label, string text, bool good)
        {
            label.Text = text;
            label.ForeColor = good
                ? Color.FromArgb(81, 216, 138)
                : Color.FromArgb(248, 81, 73);
        }

        private string BuildActionText(
            int targetIso, double stableSeconds, bool serialReady)
        {
            if (paused)
                return "Automatisch aanpassen is gestopt.";
            if (!serialReady || targetIso <= 0)
                return "Wachten op een lichtmeting van de sensor.";
            if (!darkroomRunning)
                return "Darkroom is niet actief. ISO toepassen is uitgeschakeld.";
            if (DateTime.Now < nextAutomaticAttemptAt)
                return string.Format(
                    "Nieuwe automatische poging over {0:0} seconden.",
                    (nextAutomaticAttemptAt - DateTime.Now).TotalSeconds);
            if (stableSeconds < stabilitySeconds)
                return string.Format(
                    "Doel-ISO wordt nog {0:0.0} seconden gecontroleerd.",
                    stabilitySeconds - stableSeconds);
            if (lastAppliedTargetIso == targetIso &&
                lastAppliedDarkroomProcessId == darkroomProcessId)
                return string.Format(
                    "ISO {0} is automatisch toegepast.",
                    targetIso);
            if (currentDarkroomIso == "Unknown")
                return string.Format(
                    "Huidige Darkroom-ISO controleren; doel is ISO {0}.",
                    targetIso);
            if (String.Equals(
                    currentDarkroomIso, targetIso.ToString(),
                    StringComparison.OrdinalIgnoreCase))
                return string.Format(
                    "Darkroom staat al op ISO {0}.", targetIso);
            return string.Format(
                "Darkroom wordt automatisch aangepast van ISO {0} naar {1}.",
                currentDarkroomIso, targetIso);
        }

        private void UpdateDarkroomState()
        {
            Process process = null;
            try
            {
                Process[] candidates =
                    Process.GetProcessesByName("DarkroomBooth");
                foreach (Process candidate in candidates)
                {
                    if (candidate.MainWindowHandle != IntPtr.Zero)
                    {
                        process = candidate;
                        break;
                    }
                    candidate.Dispose();
                }
            }
            catch { }

            if (process == null)
            {
                if (darkroomProcessId != 0)
                    Log("Darkroom Booth stopped; cached ISO cleared.");
                ResetDarkroomSession();
                return;
            }

            try
            {
                int processId = process.Id;
                if (darkroomProcessId != processId)
                {
                    darkroomProcessId = processId;
                    currentDarkroomIso = "Unknown";
                    currentIsoReadAt = DateTime.MinValue;
                    lastAppliedTargetIso = 0;
                    lastAppliedDarkroomProcessId = 0;
                    nextAutomaticAttemptAt = DateTime.MinValue;
                    Log("Darkroom Booth detected; process=" + processId +
                        "; ISO will be verified automatically.");
                }

                darkroomRunning = true;
                IntPtr window = process.MainWindowHandle;
                boothMode = IsIconic(window) || WindowFillsScreen(window);

                IntPtr isoCombo = FindVisibleChildById(window, 107);
                if (isoCombo != IntPtr.Zero)
                {
                    string value = ReadComboSelection(isoCombo);
                    if (!String.IsNullOrWhiteSpace(value))
                    {
                        currentDarkroomIso = value;
                        currentIsoReadAt = DateTime.Now;
                    }
                }
            }
            catch
            {
                ResetDarkroomSession();
            }
            finally
            {
                process.Dispose();
            }
        }

        private void ResetDarkroomSession()
        {
            darkroomRunning = false;
            boothMode = false;
            darkroomProcessId = 0;
            currentDarkroomIso = "Unknown";
            currentIsoReadAt = DateTime.MinValue;
            lastAppliedTargetIso = 0;
            lastAppliedDarkroomProcessId = 0;
            nextAutomaticAttemptAt = DateTime.MinValue;
        }
        private static bool WindowFillsScreen(IntPtr window)
        {
            NativeMethods.Rect rect;
            if (!GetWindowRect(window, out rect))
                return false;
            Rectangle screen = Screen.FromHandle(window).Bounds;
            return rect.Left <= screen.Left + 20 &&
                   rect.Top <= screen.Top + 20 &&
                   rect.Right >= screen.Right - 20 &&
                   rect.Bottom >= screen.Bottom - 20;
        }

        private static IntPtr FindChildById(IntPtr root, int controlId)
        {
            IntPtr found = IntPtr.Zero;
            EnumChildProc callback = delegate(IntPtr handle, IntPtr parameter)
            {
                if (GetDlgCtrlID(handle) == controlId)
                {
                    found = handle;
                    return false;
                }
                return true;
            };
            EnumChildWindows(root, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return found;
        }

        private static IntPtr FindVisibleChildById(
            IntPtr root, int controlId)
        {
            IntPtr found = IntPtr.Zero;
            EnumChildProc callback = delegate(IntPtr handle, IntPtr parameter)
            {
                if (GetDlgCtrlID(handle) == controlId &&
                    IsWindowVisible(handle))
                {
                    found = handle;
                    return false;
                }
                return true;
            };
            EnumChildWindows(root, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return found;
        }
        private static IntPtr WaitForStableVisibleChildById(
            IntPtr root, int controlId, int timeoutMilliseconds)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(
                timeoutMilliseconds);
            IntPtr last = IntPtr.Zero;
            int confirmations = 0;
            while (DateTime.UtcNow < deadline)
            {
                IntPtr current = FindVisibleChildById(root, controlId);
                if (current != IntPtr.Zero && current == last &&
                    IsWindowVisible(current))
                {
                    confirmations++;
                    if (confirmations >= 3)
                        return current;
                }
                else
                {
                    last = current;
                    confirmations = current == IntPtr.Zero ? 0 : 1;
                }
                Thread.Sleep(80);
            }
            return IntPtr.Zero;
        }
        private static string ReadComboSelection(IntPtr combo)
        {
            long selected = SendMessageWithTimeout(
                combo, 0x0147, IntPtr.Zero, IntPtr.Zero).ToInt64();
            if (selected < 0)
                return "";
            long length = SendMessageWithTimeout(
                combo, 0x0149, new IntPtr(selected), IntPtr.Zero).ToInt64();
            if (length < 0 || length > 128)
                return "";
            StringBuilder value = new StringBuilder((int)length + 1);
            SendMessageWithTimeout(
                combo, 0x0148, new IntPtr(selected), value);
            return value.ToString();
        }

        private static IntPtr SendMessageWithTimeout(
            IntPtr handle, uint message, IntPtr wParam, IntPtr lParam)
        {
            IntPtr result;
            IntPtr completed = SendMessageTimeout(
                handle, message, wParam, lParam,
                0x0001 | 0x0002, 2000, out result);
            if (completed == IntPtr.Zero)
                throw new TimeoutException(
                    "Darkroom did not respond to native message 0x" +
                    message.ToString("X") + " within two seconds.");
            return result;
        }

        private static IntPtr SendMessageWithTimeout(
            IntPtr handle, uint message, IntPtr wParam, StringBuilder lParam)
        {
            IntPtr result;
            IntPtr completed = SendMessageTimeout(
                handle, message, wParam, lParam,
                0x0001 | 0x0002, 2000, out result);
            if (completed == IntPtr.Zero)
                throw new TimeoutException(
                    "Darkroom did not return ISO dropdown text within two seconds.");
            return result;
        }

        private void Log(string message)
        {
            try
            {
                lock (logSync)
                {
                    File.AppendAllText(
                        logPath,
                        string.Format(
                            "{0:yyyy-MM-dd HH:mm:ss.fff}  {1}{2}",
                            DateTime.Now, message, Environment.NewLine));
                }
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public ushort PartitionId;
            public UIntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
            uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(
            IntPtr process, IntPtr baseAddress,
            [Out] byte[] buffer, UIntPtr size, out UIntPtr bytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr VirtualQueryEx(
            IntPtr process, IntPtr address,
            out MemoryBasicInformation information, UIntPtr length);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            IntPtr handle, out uint processId);

        private delegate bool EnumWindowProc(
            IntPtr handle, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(
            EnumWindowProc callback, IntPtr parameter);


        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(
            IntPtr handle, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr handle, int id);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr handle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(
            IntPtr handle, StringBuilder className, int maximumCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string messageName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr handle, uint message, IntPtr wParam, IntPtr lParam,
            uint flags, uint timeoutMilliseconds, out IntPtr result);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr handle, uint message, IntPtr wParam, StringBuilder lParam,
            uint flags, uint timeoutMilliseconds, out IntPtr result);

        [DllImport("oleacc.dll")]
        private static extern int ObjectFromLresult(
            IntPtr result, ref Guid interfaceId, IntPtr wParam,
            [MarshalAs(UnmanagedType.Interface)] out object value);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr handle);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr handle, IntPtr insertAfter,
            int x, int y, int width, int height, uint flags);

        private delegate void WinEventDelegate(
            IntPtr hook, uint eventType, IntPtr window,
            int objectId, int childId,
            uint eventThread, uint eventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax, IntPtr module,
            WinEventDelegate callback,
            uint processId, uint threadId, uint flags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr handle, int command);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

        private delegate bool EnumChildProc(IntPtr handle, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(
            IntPtr parent, EnumChildProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern int GetDlgCtrlID(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(
            IntPtr handle, out NativeMethods.Rect rect);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(
            IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool PostMessage(
            IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(
            IntPtr handle, uint message, IntPtr wParam, StringBuilder lParam);
    }


    internal sealed class IsoMappingForm : Form
    {
        private static readonly Color Background = Color.FromArgb(247, 248, 250);
        private static readonly Color Card = Color.White;
        private static readonly Color Border = Color.FromArgb(218, 222, 229);
        private static readonly Color TextPrimary = Color.FromArgb(31, 35, 40);
        private static readonly Color TextMuted = Color.FromArgb(99, 108, 118);
        private static readonly Color Accent = Color.FromArgb(31, 111, 235);

        private readonly RadioButton defaultProfile;
        private readonly RadioButton customProfile;
        private readonly DataGridView grid;
        private readonly Button addButton;
        private readonly Button removeButton;
        private readonly Button saveButton;
        private readonly Label explanation;
        private readonly Panel profileCard;
        private readonly Panel previewCard;
        private readonly LightRangeControl previewRange;
        private readonly NumericUpDown previewLightInput;
        private readonly NumericUpDown stabilitySecondsInput;
        private readonly Label previewText;
        private readonly List<IsoBand> defaultBands;
        private readonly int[] supportedIsos;
        private readonly Button isoSettingsTab;
        private readonly Button coverSettingsTab;
        private readonly Button updateButton;
        private readonly Label boothAndVersionLabel;
        private readonly System.Windows.Forms.Timer updateStatusTimer;
        private readonly Panel coverSettingsPanel;
        private readonly RadioButton defaultCoverText;
        private readonly RadioButton customCoverText;
        private readonly TextBox coverTitleInput;
        private readonly TextBox coverMessageInput;
        private readonly Label coverPreviewTitle;
        private readonly Label coverPreviewMessage;
        private readonly string defaultCoverTitle;
        private readonly string defaultCoverMessage;
        private readonly string boothName;
        private string draftCustomCoverTitle;
        private string draftCustomCoverMessage;
        private bool renderingCoverText;
        private bool showingCoverSettings;
        private bool rendering;
        private bool renderedCustom;
        private bool initialized;
        private bool dirty;
        private readonly string updaterExecutablePath;
        private readonly string updaterStatusPath;
        private readonly Dictionary<Control, float> textFitBaseFontSizes =
            new Dictionary<Control, float>();
        private DateTime updateCheckStartedAtUtc = DateTime.MinValue;
        private bool textFitActive;

        public bool UseCustomMapping { get; private set; }
        public List<IsoBand> CustomBands { get; private set; }
        public int StabilitySeconds
        {
            get { return (int)stabilitySecondsInput.Value; }
        }
        public bool UseCustomCoverText { get; private set; }
        public string CustomCoverTitle
        {
            get { return draftCustomCoverTitle; }
        }
        public string CustomCoverMessage
        {
            get { return draftCustomCoverMessage; }
        }

        public IsoMappingForm(
            bool useCustomMapping,
            List<IsoBand> customBands,
            List<IsoBand> defaults,
            int[] isoValues,
            string boothName,
            int stabilitySeconds,
            bool useCustomCoverText,
            string customCoverTitle,
            string customCoverMessage,
            string defaultCoverTitleValue,
            string defaultCoverMessageValue)
        {
            Text = "Instellingen";
            ClientSize = new Size(1000, 720);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Background;
            ForeColor = TextPrimary;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10, FontStyle.Regular);

            defaultBands = CloneBands(defaults);
            CustomBands = CloneBands(customBands);
            supportedIsos = (int[])isoValues.Clone();
            this.boothName = boothName;
            UseCustomCoverText = useCustomCoverText;
            defaultCoverTitle = defaultCoverTitleValue;
            defaultCoverMessage = defaultCoverMessageValue;
            draftCustomCoverTitle = String.IsNullOrWhiteSpace(customCoverTitle)
                ? defaultCoverTitle : customCoverTitle;
            draftCustomCoverMessage = String.IsNullOrWhiteSpace(customCoverMessage)
                ? defaultCoverMessage : customCoverMessage;
            string localRoot = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "JvdP", "LightDarkroomOverlay");
            updaterExecutablePath = Path.Combine(
                localRoot, "JvdpAutoUpdater.exe");
            updaterStatusPath = Path.Combine(
                localRoot, "updater-status.txt");

            Label title = MakeDialogLabel(
                "Instellingen", 18, FontStyle.Bold, TextPrimary);
            title.SetBounds(24, 18, 196, 38);
            Controls.Add(title);

            isoSettingsTab = MakeDialogButton("Licht && ISO", true);
            isoSettingsTab.SetBounds(210, 14, 118, 40);
            isoSettingsTab.Click += delegate { ShowSettingsSection(false); };
            Controls.Add(isoSettingsTab);

            coverSettingsTab = MakeDialogButton("Overlay settings", false);
            coverSettingsTab.SetBounds(336, 14, 156, 40);
            coverSettingsTab.Click += delegate { ShowSettingsSection(true); };
            Controls.Add(coverSettingsTab);

            boothAndVersionLabel = MakeDialogLabel(
                "Booth: " + boothName + "  ·  Versie " +
                BuildInfo.Version, 9, FontStyle.Regular, TextMuted);
            boothAndVersionLabel.TextAlign = ContentAlignment.MiddleRight;
            boothAndVersionLabel.SetBounds(
                ClientSize.Width - 520, 48, 280, 18);
            boothAndVersionLabel.Anchor = AnchorStyles.Top |
                AnchorStyles.Right;
            Controls.Add(boothAndVersionLabel);

            updateButton = MakeDialogButton(
                "Updates controleren", false);
            updateButton.SetBounds(
                ClientSize.Width - 224, 14, 200, 40);
            updateButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            updateButton.AccessibleName =
                "Nu controleren op software-updates";
            updateButton.Click += delegate { StartUpdateCheck(); };
            Controls.Add(updateButton);

            updateStatusTimer = new System.Windows.Forms.Timer();
            updateStatusTimer.Interval = 500;
            updateStatusTimer.Tick += delegate { RefreshUpdateStatus(); };

            profileCard = new Panel();
            profileCard.BackColor = Card;
            profileCard.BorderStyle = BorderStyle.FixedSingle;
            profileCard.SetBounds(24, 68, ClientSize.Width - 48, 100);
            profileCard.Anchor = AnchorStyles.Top |
                AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(profileCard);

            defaultProfile = new RadioButton();
            defaultProfile.Text = "Standaardprofiel · alle booths";
            defaultProfile.ForeColor = TextPrimary;
            defaultProfile.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            defaultProfile.SetBounds(16, 12, 290, 28);
            defaultProfile.AccessibleName = "Gebruik standaardprofiel voor alle booths";
            profileCard.Controls.Add(defaultProfile);

            customProfile = new RadioButton();
            customProfile.Text = "Eigen profiel · deze booth";
            customProfile.ForeColor = TextPrimary;
            customProfile.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            customProfile.SetBounds(350, 12, 300, 28);
            customProfile.AccessibleName = "Gebruik eigen profiel voor deze booth";
            profileCard.Controls.Add(customProfile);

            explanation = MakeDialogLabel(
                "", 9, FontStyle.Regular, TextMuted);
            explanation.SetBounds(18, 46, profileCard.Width - 318, 44);
            explanation.Anchor = AnchorStyles.Top |
                AnchorStyles.Left | AnchorStyles.Right;
            profileCard.Controls.Add(explanation);

            Label stabilityCaption = MakeDialogLabel(
                "Licht stabiel (seconden)", 9, FontStyle.Regular,
                TextMuted);
            stabilityCaption.TextAlign = ContentAlignment.MiddleRight;
            stabilityCaption.SetBounds(profileCard.Width - 286, 50, 190, 32);
            stabilityCaption.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            profileCard.Controls.Add(stabilityCaption);

            stabilitySecondsInput = new NumericUpDown();
            stabilitySecondsInput.Minimum = 5;
            stabilitySecondsInput.Maximum = 300;
            stabilitySecondsInput.Increment = 5;
            stabilitySecondsInput.Value = Math.Max(5,
                Math.Min(300, stabilitySeconds));
            stabilitySecondsInput.Font = new Font(
                "Segoe UI", 10, FontStyle.Regular);
            stabilitySecondsInput.TextAlign = HorizontalAlignment.Center;
            stabilitySecondsInput.SetBounds(
                profileCard.Width - 86, 52, 68, 28);
            stabilitySecondsInput.Anchor = AnchorStyles.Top |
                AnchorStyles.Right;
            stabilitySecondsInput.AccessibleName =
                "Aantal seconden dat de lichtwaarde stabiel moet zijn";
            profileCard.Controls.Add(stabilitySecondsInput);

            previewCard = new Panel();
            previewCard.BackColor = Card;
            previewCard.BorderStyle = BorderStyle.FixedSingle;
            previewCard.SetBounds(24, 182, ClientSize.Width - 48, 150);
            previewCard.Anchor = AnchorStyles.Top | AnchorStyles.Left |
                AnchorStyles.Right;
            Controls.Add(previewCard);

            Label previewCaption = MakeDialogLabel(
                "Grafische preview", 10, FontStyle.Bold, TextPrimary);
            previewCaption.SetBounds(14, 7, 150, 28);
            previewCard.Controls.Add(previewCaption);

            Label previewLightCaption = MakeDialogLabel(
                "Voorbeeldlichtwaarde", 9, FontStyle.Regular, TextMuted);
            previewLightCaption.SetBounds(180, 7, 155, 28);
            previewCard.Controls.Add(previewLightCaption);

            previewLightInput = new NumericUpDown();
            previewLightInput.Minimum = 0;
            previewLightInput.Maximum = 100;
            previewLightInput.Value = 63;
            previewLightInput.Font = new Font("Segoe UI", 10, FontStyle.Regular);
            previewLightInput.SetBounds(338, 7, 70, 28);
            previewLightInput.AccessibleName = "Voorbeeldlichtwaarde";
            previewCard.Controls.Add(previewLightInput);

            previewText = MakeDialogLabel(
                "", 9, FontStyle.Bold, Color.FromArgb(20, 102, 196));
            previewText.TextAlign = ContentAlignment.MiddleRight;
            previewText.SetBounds(420, 7, previewCard.Width - 434, 28);
            previewText.Anchor = AnchorStyles.Top | AnchorStyles.Left |
                AnchorStyles.Right;
            previewCard.Controls.Add(previewText);

            previewRange = new LightRangeControl();
            previewRange.Interactive = true;
            previewRange.Cursor = Cursors.Hand;
            previewRange.AccessibleName =
                "Versleepbare voorbeeldlichtwaarde";
            previewRange.SetBounds(14, 43, previewCard.Width - 28, 92);
            previewRange.Anchor = AnchorStyles.Top | AnchorStyles.Left |
                AnchorStyles.Right;
            previewCard.Controls.Add(previewRange);

            grid = new DataGridView();
            grid.SetBounds(24, 346, ClientSize.Width - 48,
                ClientSize.Height - 346 - 92);
            grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom |
                AnchorStyles.Left | AnchorStyles.Right;
            grid.BackgroundColor = Card;
            grid.BorderStyle = BorderStyle.FixedSingle;
            grid.GridColor = Border;
            grid.RowHeadersVisible = false;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.ColumnHeadersHeightSizeMode =
                DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            grid.AutoSizeRowsMode =
                DataGridViewAutoSizeRowsMode.AllCells;
            grid.RowTemplate.MinimumHeight = 36;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Border;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = TextPrimary;
            grid.ColumnHeadersDefaultCellStyle.Font =
                new Font("Segoe UI", 9, FontStyle.Bold);
            grid.DefaultCellStyle.BackColor = Card;
            grid.DefaultCellStyle.ForeColor = TextPrimary;
            grid.DefaultCellStyle.SelectionBackColor =
                Color.FromArgb(32, 77, 126);
            grid.DefaultCellStyle.SelectionForeColor = Color.White;
            grid.DefaultCellStyle.Font = new Font("Segoe UI", 10);
            grid.AccessibleName = "Licht naar ISO-bereiken";

            DataGridViewTextBoxColumn fromColumn =
                new DataGridViewTextBoxColumn();
            fromColumn.HeaderText = "Licht vanaf";
            fromColumn.ReadOnly = true;
            grid.Columns.Add(fromColumn);

            DataGridViewTextBoxColumn throughColumn =
                new DataGridViewTextBoxColumn();
            throughColumn.HeaderText = "Tot en met";
            grid.Columns.Add(throughColumn);

            DataGridViewComboBoxColumn isoColumn =
                new DataGridViewComboBoxColumn();
            isoColumn.HeaderText = "Darkroom ISO";
            isoColumn.DataSource = supportedIsos;
            isoColumn.FlatStyle = FlatStyle.Flat;
            grid.Columns.Add(isoColumn);
            Controls.Add(grid);

            addButton = MakeDialogButton("Bereik toevoegen", false);
            addButton.SetBounds(24, ClientSize.Height - 72, 150, 42);
            addButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            addButton.Click += delegate { AddRange(); };
            Controls.Add(addButton);

            removeButton = MakeDialogButton("Bereik verwijderen", false);
            removeButton.SetBounds(182, ClientSize.Height - 72, 158, 42);
            removeButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            removeButton.Click += delegate { RemoveRange(); };
            Controls.Add(removeButton);

            Button cancelButton = MakeDialogButton("Terug", false);
            cancelButton.SetBounds(
                ClientSize.Width - 434, ClientSize.Height - 72, 220, 42);
            cancelButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            cancelButton.Click += delegate
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };
            Controls.Add(cancelButton);

            saveButton = MakeDialogButton("Opslaan", true);
            saveButton.SetBounds(
                ClientSize.Width - 206, ClientSize.Height - 72, 182, 42);
            saveButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            saveButton.Click += delegate { SaveAndClose(); };
            Controls.Add(saveButton);

            coverSettingsPanel = new Panel();
            coverSettingsPanel.BackColor = Background;
            coverSettingsPanel.SetBounds(24, 68,
                ClientSize.Width - 48, ClientSize.Height - 160);
            coverSettingsPanel.Anchor = AnchorStyles.Top |
                AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            coverSettingsPanel.Visible = false;
            Controls.Add(coverSettingsPanel);

            Panel coverModeCard = new Panel();
            coverModeCard.BackColor = Card;
            coverModeCard.BorderStyle = BorderStyle.FixedSingle;
            coverModeCard.SetBounds(0, 0, coverSettingsPanel.Width, 100);
            coverModeCard.Anchor = AnchorStyles.Top |
                AnchorStyles.Left | AnchorStyles.Right;
            coverSettingsPanel.Controls.Add(coverModeCard);

            defaultCoverText = new RadioButton();
            defaultCoverText.Text = "Standaardtekst · alle booths";
            defaultCoverText.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            defaultCoverText.ForeColor = TextPrimary;
            defaultCoverText.SetBounds(16, 12, 290, 28);
            coverModeCard.Controls.Add(defaultCoverText);

            customCoverText = new RadioButton();
            customCoverText.Text = "Eigen tekst · deze booth";
            customCoverText.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            customCoverText.ForeColor = TextPrimary;
            customCoverText.SetBounds(350, 12, 300, 28);
            coverModeCard.Controls.Add(customCoverText);

            Label coverExplanation = MakeDialogLabel(
                "De standaardtekst wordt via de software-update op alle " +
                "booths gebruikt. Een eigen tekst geldt alleen hier.",
                9, FontStyle.Regular, TextMuted);
            coverExplanation.SetBounds(18, 46,
                coverModeCard.Width - 36, 44);
            coverExplanation.Anchor = AnchorStyles.Top |
                AnchorStyles.Left | AnchorStyles.Right;
            coverModeCard.Controls.Add(coverExplanation);

            Panel coverEditorCard = new Panel();
            coverEditorCard.BackColor = Card;
            coverEditorCard.BorderStyle = BorderStyle.FixedSingle;
            coverEditorCard.SetBounds(0, 114,
                coverSettingsPanel.Width, 160);
            coverEditorCard.Anchor = AnchorStyles.Top |
                AnchorStyles.Left | AnchorStyles.Right;
            coverSettingsPanel.Controls.Add(coverEditorCard);

            Label coverTitleCaption = MakeDialogLabel(
                "Koptekst", 9, FontStyle.Bold, TextPrimary);
            coverTitleCaption.SetBounds(16, 10, 96, 32);
            coverEditorCard.Controls.Add(coverTitleCaption);

            coverTitleInput = new TextBox();
            coverTitleInput.Font = new Font("Segoe UI", 11, FontStyle.Regular);
            coverTitleInput.MaxLength = 60;
            coverTitleInput.SetBounds(120, 10,
                coverEditorCard.Width - 138, 32);
            coverTitleInput.Anchor = AnchorStyles.Top |
                AnchorStyles.Left | AnchorStyles.Right;
            coverTitleInput.AccessibleName = "Koptekst volledig scherm";
            coverEditorCard.Controls.Add(coverTitleInput);

            Label coverMessageCaption = MakeDialogLabel(
                "Bericht", 9, FontStyle.Bold, TextPrimary);
            coverMessageCaption.SetBounds(16, 54, 96, 32);
            coverEditorCard.Controls.Add(coverMessageCaption);

            coverMessageInput = new TextBox();
            coverMessageInput.Font = new Font("Segoe UI", 10, FontStyle.Regular);
            coverMessageInput.Multiline = true;
            coverMessageInput.AcceptsReturn = true;
            coverMessageInput.ScrollBars = ScrollBars.Vertical;
            coverMessageInput.MaxLength = 240;
            coverMessageInput.SetBounds(120, 54,
                coverEditorCard.Width - 138, 88);
            coverMessageInput.Anchor = AnchorStyles.Top |
                AnchorStyles.Left | AnchorStyles.Right;
            coverMessageInput.AccessibleName = "Bericht volledig scherm";
            coverEditorCard.Controls.Add(coverMessageInput);

            Panel coverPreviewCard = new Panel();
            coverPreviewCard.BackColor = Card;
            coverPreviewCard.BorderStyle = BorderStyle.FixedSingle;
            coverPreviewCard.SetBounds(0, 288, coverSettingsPanel.Width,
                Math.Max(110, coverSettingsPanel.Height - 288));
            coverPreviewCard.Anchor = AnchorStyles.Top |
                AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            coverSettingsPanel.Controls.Add(coverPreviewCard);

            Label coverPreviewCaption = MakeDialogLabel(
                "Voorbeeld volledig scherm", 9, FontStyle.Bold, TextMuted);
            coverPreviewCaption.SetBounds(14, 6, 220, 24);
            coverPreviewCard.Controls.Add(coverPreviewCaption);

            coverPreviewTitle = MakeDialogLabel(
                "", 22, FontStyle.Bold, Color.FromArgb(31, 38, 58));
            coverPreviewTitle.TextAlign = ContentAlignment.BottomCenter;
            coverPreviewTitle.SetBounds(20, 28,
                coverPreviewCard.Width - 40, 46);
            coverPreviewTitle.Anchor = AnchorStyles.Top |
                AnchorStyles.Left | AnchorStyles.Right;
            coverPreviewCard.Controls.Add(coverPreviewTitle);

            coverPreviewMessage = MakeDialogLabel(
                "", 12, FontStyle.Bold, Color.FromArgb(73, 82, 105));
            coverPreviewMessage.TextAlign = ContentAlignment.TopCenter;
            coverPreviewMessage.SetBounds(20, 76,
                coverPreviewCard.Width - 40,
                Math.Max(46, coverPreviewCard.Height - 82));
            coverPreviewMessage.Anchor = AnchorStyles.Top |
                AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            coverPreviewCard.Controls.Add(coverPreviewMessage);

            BuildResponsiveCoverLayout(
                coverModeCard, coverExplanation,
                coverEditorCard, coverTitleCaption, coverMessageCaption,
                coverPreviewCard, coverPreviewCaption);
            BuildResponsiveSettingsLayout(
                title, stabilityCaption, previewCaption,
                previewLightCaption, cancelButton);
            CaptureTextFitFonts(this);
            Resize += delegate
            {
                EnsureTextFitsWithinBounds();
            };

            AcceptButton = saveButton;
            CancelButton = cancelButton;
            defaultProfile.CheckedChanged += delegate { ProfileChanged(); };
            customProfile.CheckedChanged += delegate { ProfileChanged(); };
            previewLightInput.ValueChanged += delegate { UpdatePreview(); };
            previewRange.LightValueChanged += delegate
            {
                int value = previewRange.LightValue;
                if (value >= previewLightInput.Minimum &&
                    value <= previewLightInput.Maximum &&
                    previewLightInput.Value != value)
                    previewLightInput.Value = value;
            };
            stabilitySecondsInput.ValueChanged += delegate { MarkDirty(); };
            defaultCoverText.CheckedChanged += delegate
            {
                CoverTextModeChanged();
            };
            customCoverText.CheckedChanged += delegate
            {
                CoverTextModeChanged();
            };
            coverTitleInput.TextChanged += delegate
            {
                CoverTextChanged();
            };
            coverMessageInput.TextChanged += delegate
            {
                CoverTextChanged();
            };
            grid.CellValueChanged += delegate
            {
                MarkDirty();
                UpdatePreview();
            };
            grid.CurrentCellDirtyStateChanged += delegate
            {
                if (grid.IsCurrentCellDirty)
                    grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            if (useCustomMapping)
                customProfile.Checked = true;
            else
                defaultProfile.Checked = true;
            if (useCustomCoverText)
                customCoverText.Checked = true;
            else
                defaultCoverText.Checked = true;
            RenderRows();
            RefreshCoverTextControls();
            ShowSettingsSection(false);
            initialized = true;
            dirty = false;
            UpdatePreview();
            UpdateSaveState();
            FormClosed += delegate
            {
                updateStatusTimer.Stop();
                updateStatusTimer.Dispose();
            };
            RefreshUpdateStatus();
            EnsureTextFitsWithinBounds();
        }

        private void CaptureTextFitFonts(Control root)
        {
            if (root == null)
                return;
            if ((root is Label || root is Button) &&
                !textFitBaseFontSizes.ContainsKey(root))
            {
                textFitBaseFontSizes.Add(root, root.Font.Size);
                root.TextChanged += delegate
                {
                    if (!textFitActive && !root.IsDisposed)
                        OverlayForm.FitTextControl(root);
                };
                root.SizeChanged += delegate
                {
                    if (!textFitActive && !root.IsDisposed)
                        OverlayForm.FitTextControl(root);
                };
            }
            foreach (Control child in root.Controls)
                CaptureTextFitFonts(child);
        }

        private void EnsureTextFitsWithinBounds()
        {
            if (textFitActive || IsDisposed)
                return;
            textFitActive = true;
            try
            {
                RestoreTextFitFonts(this);
                OverlayForm.FitTextTree(this);
            }
            finally
            {
                textFitActive = false;
            }
        }

        private void RestoreTextFitFonts(Control root)
        {
            float baseSize;
            if (textFitBaseFontSizes.TryGetValue(root, out baseSize) &&
                Math.Abs(root.Font.Size - baseSize) > 0.05f)
                OverlayForm.ReplaceControlFont(root, new Font(
                    root.Font.FontFamily.Name,
                    baseSize, root.Font.Style));
            foreach (Control child in root.Controls)
                RestoreTextFitFonts(child);
        }

        private void BuildResponsiveCoverLayout(
            Panel coverModeCard,
            Label coverExplanation,
            Panel coverEditorCard,
            Label coverTitleCaption,
            Label coverMessageCaption,
            Panel coverPreviewCard,
            Label coverPreviewCaption)
        {
            coverSettingsPanel.SuspendLayout();
            coverSettingsPanel.Controls.Clear();
            coverSettingsPanel.AutoScroll = true;

            TableLayoutPanel coverRoot = new TableLayoutPanel();
            coverRoot.AutoSize = true;
            coverRoot.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            coverRoot.Dock = DockStyle.Top;
            coverRoot.BackColor = Background;
            coverRoot.ColumnCount = 1;
            coverRoot.RowCount = 3;
            coverRoot.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            coverRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            coverRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            coverRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            coverModeCard.Controls.Clear();
            coverModeCard.AutoSize = true;
            coverModeCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            coverModeCard.Dock = DockStyle.Fill;
            coverModeCard.Margin = new Padding(0, 0, 0, 12);
            TableLayoutPanel modeLayout = new TableLayoutPanel();
            modeLayout.AutoSize = true;
            modeLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            modeLayout.Dock = DockStyle.Fill;
            modeLayout.Padding = new Padding(16, 8, 16, 10);
            modeLayout.ColumnCount = 1;
            modeLayout.RowCount = 2;
            modeLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            modeLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            modeLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            FlowLayoutPanel coverModes = new FlowLayoutPanel();
            coverModes.AutoSize = true;
            coverModes.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            coverModes.Dock = DockStyle.Fill;
            coverModes.FlowDirection = FlowDirection.LeftToRight;
            coverModes.WrapContents = true;
            coverModes.Margin = Padding.Empty;
            defaultCoverText.AutoSize = true;
            defaultCoverText.Margin = new Padding(0, 2, 42, 4);
            customCoverText.AutoSize = true;
            customCoverText.Margin = new Padding(0, 2, 0, 4);
            coverModes.Controls.Add(defaultCoverText);
            coverModes.Controls.Add(customCoverText);

            coverExplanation.AutoSize = true;
            coverExplanation.AutoEllipsis = false;
            coverExplanation.MaximumSize = new Size(900, 0);
            coverExplanation.Margin = Padding.Empty;
            modeLayout.Controls.Add(coverModes, 0, 0);
            modeLayout.Controls.Add(coverExplanation, 0, 1);
            coverModeCard.Controls.Add(modeLayout);

            coverEditorCard.Controls.Clear();
            coverEditorCard.AutoSize = true;
            coverEditorCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            coverEditorCard.Dock = DockStyle.Fill;
            coverEditorCard.Margin = new Padding(0, 0, 0, 12);
            TableLayoutPanel editorLayout = new TableLayoutPanel();
            editorLayout.AutoSize = true;
            editorLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            editorLayout.Dock = DockStyle.Fill;
            editorLayout.Padding = new Padding(16, 10, 16, 12);
            editorLayout.ColumnCount = 2;
            editorLayout.RowCount = 2;
            editorLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.AutoSize));
            editorLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            editorLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editorLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            coverTitleCaption.AutoSize = true;
            coverTitleCaption.Margin = new Padding(0, 7, 18, 12);
            coverTitleInput.Dock = DockStyle.Fill;
            coverTitleInput.MinimumSize = new Size(
                240, coverTitleInput.PreferredHeight);
            coverTitleInput.Margin = new Padding(0, 0, 0, 12);
            coverMessageCaption.AutoSize = true;
            coverMessageCaption.Margin = new Padding(0, 7, 18, 0);
            coverMessageInput.Dock = DockStyle.Fill;
            coverMessageInput.MinimumSize = new Size(240, 100);
            coverMessageInput.Margin = Padding.Empty;
            editorLayout.Controls.Add(coverTitleCaption, 0, 0);
            editorLayout.Controls.Add(coverTitleInput, 1, 0);
            editorLayout.Controls.Add(coverMessageCaption, 0, 1);
            editorLayout.Controls.Add(coverMessageInput, 1, 1);
            coverEditorCard.Controls.Add(editorLayout);

            coverPreviewCard.Controls.Clear();
            coverPreviewCard.AutoSize = true;
            coverPreviewCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            coverPreviewCard.Dock = DockStyle.Fill;
            coverPreviewCard.MinimumSize = new Size(0, 180);
            coverPreviewCard.Margin = Padding.Empty;
            TableLayoutPanel coverPreviewLayout = new TableLayoutPanel();
            coverPreviewLayout.AutoSize = true;
            coverPreviewLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            coverPreviewLayout.Dock = DockStyle.Fill;
            coverPreviewLayout.Padding = new Padding(14, 8, 14, 14);
            coverPreviewLayout.ColumnCount = 1;
            coverPreviewLayout.RowCount = 3;
            coverPreviewLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            coverPreviewLayout.RowStyles.Add(
                new RowStyle(SizeType.AutoSize));
            coverPreviewLayout.RowStyles.Add(
                new RowStyle(SizeType.AutoSize));
            coverPreviewLayout.RowStyles.Add(
                new RowStyle(SizeType.AutoSize));

            coverPreviewCaption.AutoSize = true;
            coverPreviewCaption.Margin = new Padding(0, 0, 0, 12);
            coverPreviewTitle.AutoSize = true;
            coverPreviewTitle.AutoEllipsis = false;
            // GDI+ wraps even an unusually long word at character
            // boundaries. That keeps custom titles fully visible instead of
            // silently clipping them at the right edge.
            coverPreviewTitle.UseCompatibleTextRendering = true;
            coverPreviewTitle.MaximumSize = new Size(900, 0);
            coverPreviewTitle.Dock = DockStyle.Fill;
            coverPreviewTitle.TextAlign = ContentAlignment.MiddleCenter;
            coverPreviewTitle.Margin = new Padding(8, 0, 8, 8);
            coverPreviewMessage.AutoSize = true;
            coverPreviewMessage.AutoEllipsis = false;
            coverPreviewMessage.UseCompatibleTextRendering = true;
            coverPreviewMessage.MaximumSize = new Size(900, 0);
            coverPreviewMessage.Dock = DockStyle.Fill;
            coverPreviewMessage.TextAlign = ContentAlignment.TopCenter;
            coverPreviewMessage.Margin = new Padding(8, 0, 8, 0);
            coverPreviewLayout.Controls.Add(coverPreviewCaption, 0, 0);
            coverPreviewLayout.Controls.Add(coverPreviewTitle, 0, 1);
            coverPreviewLayout.Controls.Add(coverPreviewMessage, 0, 2);
            coverPreviewCard.Controls.Add(coverPreviewLayout);

            coverRoot.Controls.Add(coverModeCard, 0, 0);
            coverRoot.Controls.Add(coverEditorCard, 0, 1);
            coverRoot.Controls.Add(coverPreviewCard, 0, 2);
            coverSettingsPanel.Controls.Add(coverRoot);

            bool updatingCoverExtent = false;
            EventHandler updateCoverExtent = delegate
            {
                if (updatingCoverExtent || coverRoot.IsDisposed)
                    return;
                updatingCoverExtent = true;
                try
                {
                    int textWidth = Math.Max(240,
                        coverRoot.ClientSize.Width - 64);
                    coverExplanation.MaximumSize =
                        new Size(textWidth, 0);
                    coverPreviewTitle.MaximumSize =
                        new Size(textWidth, 0);
                    coverPreviewMessage.MaximumSize =
                        new Size(textWidth, 0);
                    coverRoot.PerformLayout();
                    int requiredRootHeight = 1;
                    foreach (Control section in coverRoot.Controls)
                        requiredRootHeight = Math.Max(
                            requiredRootHeight,
                            section.Bottom + section.Margin.Bottom);
                    if (coverRoot.MinimumSize.Height !=
                        requiredRootHeight)
                        coverRoot.MinimumSize = new Size(
                            0, requiredRootHeight);
                    if (coverRoot.Height < requiredRootHeight)
                        coverRoot.Height = requiredRootHeight;
                    coverSettingsPanel.AutoScrollMinSize = new Size(
                        0, Math.Max(requiredRootHeight,
                            coverRoot.PreferredSize.Height) + 12);
                }
                finally
                {
                    updatingCoverExtent = false;
                }
            };
            coverRoot.SizeChanged += updateCoverExtent;
            coverSettingsPanel.SizeChanged += updateCoverExtent;
            updateCoverExtent(this, EventArgs.Empty);
            coverSettingsPanel.ResumeLayout(true);
        }

        private void BuildResponsiveSettingsLayout(
            Label title,
            Label stabilityCaption,
            Label previewCaption,
            Label previewLightCaption,
            Button cancelButton)
        {
            SuspendLayout();

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.AutoScroll = true;
            root.BackColor = Background;
            root.Padding = new Padding(24, 8, 24, 12);
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            TableLayoutPanel header = new TableLayoutPanel();
            header.AutoSize = true;
            header.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            header.Dock = DockStyle.Fill;
            header.Margin = new Padding(0, 0, 0, 6);
            header.ColumnCount = 2;
            header.RowCount = 2;
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            header.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            FlowLayoutPanel navigation = new FlowLayoutPanel();
            navigation.AutoSize = true;
            navigation.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            navigation.Dock = DockStyle.Fill;
            navigation.FlowDirection = FlowDirection.LeftToRight;
            navigation.WrapContents = true;
            navigation.Margin = Padding.Empty;

            title.AutoSize = true;
            title.Margin = new Padding(0, 5, 16, 4);
            isoSettingsTab.AutoSize = true;
            isoSettingsTab.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            isoSettingsTab.MinimumSize = new Size(118, 42);
            isoSettingsTab.Padding = new Padding(12, 0, 12, 0);
            isoSettingsTab.Margin = new Padding(0, 0, 8, 4);
            coverSettingsTab.AutoSize = true;
            coverSettingsTab.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            coverSettingsTab.MinimumSize = new Size(156, 42);
            coverSettingsTab.Padding = new Padding(12, 0, 12, 0);
            coverSettingsTab.Margin = new Padding(0, 0, 0, 4);
            navigation.Controls.Add(title);
            navigation.Controls.Add(isoSettingsTab);
            navigation.Controls.Add(coverSettingsTab);

            updateButton.AutoSize = true;
            updateButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            updateButton.MinimumSize = new Size(200, 42);
            updateButton.Padding = new Padding(12, 0, 12, 0);
            updateButton.Margin = new Padding(16, 0, 0, 4);

            boothAndVersionLabel.AutoEllipsis = false;
            boothAndVersionLabel.AutoSize = true;
            boothAndVersionLabel.MaximumSize = new Size(900, 0);
            boothAndVersionLabel.Anchor = AnchorStyles.Top |
                AnchorStyles.Right;
            boothAndVersionLabel.Margin = Padding.Empty;
            boothAndVersionLabel.TextAlign = ContentAlignment.TopRight;

            header.Controls.Add(navigation, 0, 0);
            header.Controls.Add(updateButton, 1, 0);
            header.Controls.Add(boothAndVersionLabel, 0, 1);
            header.SetColumnSpan(boothAndVersionLabel, 2);

            profileCard.AutoSize = true;
            profileCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            profileCard.Dock = DockStyle.Fill;
            profileCard.Margin = new Padding(0, 0, 0, 12);
            profileCard.Controls.Clear();

            TableLayoutPanel profileLayout = new TableLayoutPanel();
            profileLayout.AutoSize = true;
            profileLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            profileLayout.Dock = DockStyle.Fill;
            profileLayout.Padding = new Padding(16, 8, 16, 10);
            profileLayout.ColumnCount = 2;
            profileLayout.RowCount = 2;
            profileLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            profileLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.AutoSize));
            profileLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            profileLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            FlowLayoutPanel profileModes = new FlowLayoutPanel();
            profileModes.AutoSize = true;
            profileModes.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            profileModes.Dock = DockStyle.Fill;
            profileModes.FlowDirection = FlowDirection.LeftToRight;
            profileModes.WrapContents = true;
            profileModes.Margin = Padding.Empty;
            defaultProfile.AutoSize = true;
            defaultProfile.Margin = new Padding(0, 2, 42, 4);
            customProfile.AutoSize = true;
            customProfile.Margin = new Padding(0, 2, 0, 4);
            profileModes.Controls.Add(defaultProfile);
            profileModes.Controls.Add(customProfile);
            profileLayout.Controls.Add(profileModes, 0, 0);
            profileLayout.SetColumnSpan(profileModes, 2);

            explanation.AutoSize = true;
            explanation.AutoEllipsis = false;
            explanation.MaximumSize = new Size(700, 0);
            explanation.Anchor = AnchorStyles.Left |
                AnchorStyles.Top | AnchorStyles.Right;
            explanation.Margin = new Padding(0, 0, 16, 0);
            profileLayout.Controls.Add(explanation, 0, 1);

            FlowLayoutPanel stability = new FlowLayoutPanel();
            stability.AutoSize = true;
            stability.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            stability.Anchor = AnchorStyles.Right;
            stability.FlowDirection = FlowDirection.LeftToRight;
            stability.WrapContents = false;
            stability.Margin = Padding.Empty;
            stabilityCaption.AutoSize = true;
            stabilityCaption.Margin = new Padding(0, 8, 10, 0);
            stabilitySecondsInput.AutoSize = true;
            stabilitySecondsInput.MinimumSize = new Size(68, 0);
            stabilitySecondsInput.Height =
                stabilitySecondsInput.PreferredHeight;
            stabilitySecondsInput.Margin = new Padding(0, 4, 0, 0);
            stability.Controls.Add(stabilityCaption);
            stability.Controls.Add(stabilitySecondsInput);
            profileLayout.Controls.Add(stability, 1, 1);
            profileCard.Controls.Add(profileLayout);

            previewCard.AutoSize = true;
            previewCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            previewCard.Dock = DockStyle.Fill;
            previewCard.Margin = new Padding(0, 0, 0, 12);
            previewCard.Controls.Clear();

            TableLayoutPanel previewLayout = new TableLayoutPanel();
            previewLayout.AutoSize = true;
            previewLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            previewLayout.Dock = DockStyle.Fill;
            previewLayout.Padding = new Padding(14, 10, 14, 12);
            previewLayout.ColumnCount = 4;
            previewLayout.RowCount = 2;
            previewLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            previewLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            previewLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            previewLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            previewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            previewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            previewCaption.AutoSize = true;
            previewCaption.Margin = new Padding(0, 6, 28, 0);
            previewLightCaption.AutoSize = true;
            previewLightCaption.Margin = new Padding(0, 7, 8, 0);
            previewLightInput.AutoSize = true;
            previewLightInput.MinimumSize = new Size(70, 0);
            previewLightInput.Height = previewLightInput.PreferredHeight;
            previewLightInput.Margin = new Padding(0, 3, 18, 0);
            previewText.AutoSize = true;
            previewText.AutoEllipsis = false;
            previewText.Anchor = AnchorStyles.Top |
                AnchorStyles.Right;
            previewText.Margin = Padding.Empty;
            previewRange.Dock = DockStyle.Fill;
            // Keep the two text lines, drag marker and slider comfortably
            // separated even before the DPI-aware minimum is calculated.
            previewRange.MinimumSize = new Size(0, 128);
            previewRange.Margin = Padding.Empty;
            previewLayout.Controls.Add(previewCaption, 0, 0);
            previewLayout.Controls.Add(previewLightCaption, 1, 0);
            previewLayout.Controls.Add(previewLightInput, 2, 0);
            previewLayout.Controls.Add(previewText, 3, 0);
            previewLayout.Controls.Add(previewRange, 0, 1);
            previewLayout.SetColumnSpan(previewRange, 4);
            previewCard.Controls.Add(previewLayout);

            grid.Dock = DockStyle.Fill;
            grid.Margin = new Padding(0, 0, 0, 10);

            TableLayoutPanel footer = new TableLayoutPanel();
            footer.AutoSize = true;
            footer.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            footer.Dock = DockStyle.Fill;
            footer.Margin = Padding.Empty;
            footer.ColumnCount = 3;
            footer.RowCount = 2;
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            FlowLayoutPanel rangeActions = new FlowLayoutPanel();
            rangeActions.AutoSize = true;
            rangeActions.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            rangeActions.FlowDirection = FlowDirection.LeftToRight;
            rangeActions.WrapContents = false;
            rangeActions.Margin = Padding.Empty;
            addButton.AutoSize = true;
            addButton.MinimumSize = new Size(150, 42);
            addButton.Padding = new Padding(10, 0, 10, 0);
            addButton.Margin = new Padding(0, 0, 8, 0);
            removeButton.AutoSize = true;
            removeButton.MinimumSize = new Size(158, 42);
            removeButton.Padding = new Padding(10, 0, 10, 0);
            removeButton.Margin = Padding.Empty;
            rangeActions.Controls.Add(addButton);
            rangeActions.Controls.Add(removeButton);

            FlowLayoutPanel saveActions = new FlowLayoutPanel();
            saveActions.AutoSize = true;
            saveActions.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            saveActions.FlowDirection = FlowDirection.LeftToRight;
            saveActions.WrapContents = false;
            saveActions.Margin = Padding.Empty;
            cancelButton.AutoSize = true;
            cancelButton.MinimumSize = new Size(120, 42);
            cancelButton.Padding = new Padding(14, 0, 14, 0);
            cancelButton.Margin = new Padding(0, 0, 8, 0);
            saveButton.AutoSize = true;
            saveButton.MinimumSize = new Size(140, 42);
            saveButton.Padding = new Padding(14, 0, 14, 0);
            saveButton.Margin = Padding.Empty;
            saveActions.Controls.Add(saveButton);
            saveActions.Controls.Add(cancelButton);

            footer.Controls.Add(rangeActions, 0, 0);
            footer.Controls.Add(saveActions, 2, 0);
            rangeActions.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            saveActions.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            bool layingOutFooter = false;
            EventHandler layoutFooter = delegate
            {
                if (layingOutFooter || footer.IsDisposed)
                    return;
                layingOutFooter = true;
                try
                {
                    int requiredWidth =
                        rangeActions.PreferredSize.Width +
                        saveActions.PreferredSize.Width + 24;
                    bool stack = footer.ClientSize.Width < requiredWidth;
                    if (stack)
                    {
                        footer.SetCellPosition(
                            rangeActions, new TableLayoutPanelCellPosition(0, 0));
                        footer.SetColumnSpan(rangeActions, 3);
                        footer.SetCellPosition(
                            saveActions, new TableLayoutPanelCellPosition(0, 1));
                        footer.SetColumnSpan(saveActions, 3);
                    }
                    else
                    {
                        footer.SetCellPosition(
                            rangeActions, new TableLayoutPanelCellPosition(0, 0));
                        footer.SetColumnSpan(rangeActions, 1);
                        footer.SetCellPosition(
                            saveActions, new TableLayoutPanelCellPosition(2, 0));
                        footer.SetColumnSpan(saveActions, 1);
                    }
                }
                finally
                {
                    layingOutFooter = false;
                }
            };
            footer.SizeChanged += layoutFooter;
            rangeActions.SizeChanged += layoutFooter;
            saveActions.SizeChanged += layoutFooter;
            layoutFooter(this, EventArgs.Empty);

            coverSettingsPanel.Dock = DockStyle.Fill;
            coverSettingsPanel.AutoScroll = true;
            coverSettingsPanel.Margin = new Padding(0, 0, 0, 10);

            Controls.Clear();
            root.Controls.Add(header, 0, 0);
            root.Controls.Add(profileCard, 0, 1);
            root.Controls.Add(previewCard, 0, 2);
            root.Controls.Add(grid, 0, 3);
            root.Controls.Add(coverSettingsPanel, 0, 1);
            root.SetRowSpan(coverSettingsPanel, 3);
            root.Controls.Add(footer, 0, 4);
            Controls.Add(root);

            ResumeLayout(true);
        }

        private void StartUpdateCheck()
        {
            try
            {
                if (!File.Exists(updaterExecutablePath))
                    throw new FileNotFoundException(
                        "De automatische updater is niet geïnstalleerd.");
                try
                {
                    if (File.Exists(updaterStatusPath))
                        File.Delete(updaterStatusPath);
                }
                catch { }

                updateCheckStartedAtUtc = DateTime.UtcNow;
                SetUpdateButtonState(
                    "Controleren...", TextMuted, false,
                    "GitHub wordt gecontroleerd op een nieuwere versie.");
                updateStatusTimer.Start();
                Process.Start(new ProcessStartInfo {
                    FileName = updaterExecutablePath,
                    Arguments = "--check-now",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(
                        updaterExecutablePath)
                });
            }
            catch (Exception exception)
            {
                updateStatusTimer.Stop();
                updateCheckStartedAtUtc = DateTime.MinValue;
                SetUpdateButtonState(
                    "Opnieuw proberen", Color.FromArgb(186, 36, 36),
                    true, exception.Message);
            }
        }

        private void RefreshUpdateStatus()
        {
            DateTime nowUtc = DateTime.UtcNow;
            if (ShowUpdateTimeoutIfNeeded(nowUtc))
                return;
            if (!File.Exists(updaterStatusPath))
                return;

            Dictionary<string, string> values =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string line in File.ReadAllLines(updaterStatusPath))
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0)
                        continue;
                    values[line.Substring(0, separator)] =
                        line.Substring(separator + 1);
                }
            }
            catch
            {
                return;
            }

            DateTime statusFileUtc = DateTime.MinValue;
            try
            {
                statusFileUtc = File.GetLastWriteTimeUtc(updaterStatusPath);
            }
            catch { }
            ApplyUpdateStatusValues(values, statusFileUtc, nowUtc);
        }

        private void ApplyUpdateStatusValues(
            Dictionary<string, string> values,
            DateTime statusFileUtc, DateTime nowUtc)
        {
            string state;
            if (!values.TryGetValue("State", out state))
                return;
            string message;
            if (!values.TryGetValue("Message", out message))
                message = "";
            string available;
            if (!values.TryGetValue("AvailableVersion", out available))
                available = "";

            bool transient =
                String.Equals(state, "checking",
                    StringComparison.OrdinalIgnoreCase) ||
                String.Equals(state, "downloading",
                    StringComparison.OrdinalIgnoreCase) ||
                String.Equals(state, "installing",
                    StringComparison.OrdinalIgnoreCase);
            if (transient)
            {
                if (updateCheckStartedAtUtc == DateTime.MinValue)
                {
                    string checkedValue;
                    values.TryGetValue("Checked", out checkedValue);
                    updateCheckStartedAtUtc = ResolveUpdateStatusStartUtc(
                        checkedValue, statusFileUtc, nowUtc);
                }
                if (ShowUpdateTimeoutIfNeeded(nowUtc))
                    return;
                // Settings can be opened after an updater process has already
                // started. Keep polling that existing state as well; otherwise
                // Downloaden/Installeren would remain disabled forever.
                updateStatusTimer.Start();
            }

            if (String.Equals(state, "checking",
                StringComparison.OrdinalIgnoreCase))
                SetUpdateButtonState(
                    "Controleren...", TextMuted, false, message);
            else if (String.Equals(state, "downloading",
                StringComparison.OrdinalIgnoreCase))
                SetUpdateButtonState(
                    "Downloaden " + available, Accent, false, message);
            else if (String.Equals(state, "installing",
                StringComparison.OrdinalIgnoreCase))
                SetUpdateButtonState(
                    "Installeren " + available, Accent, false, message);
            else if (String.Equals(state, "current",
                StringComparison.OrdinalIgnoreCase))
            {
                updateStatusTimer.Stop();
                updateCheckStartedAtUtc = DateTime.MinValue;
                SetUpdateButtonState(
                    "Versie is actueel", Color.FromArgb(34, 132, 67),
                    true, message);
            }
            else if (String.Equals(state, "installed",
                StringComparison.OrdinalIgnoreCase))
            {
                updateStatusTimer.Stop();
                updateCheckStartedAtUtc = DateTime.MinValue;
                SetUpdateButtonState(
                    "Versie " + available + " geïnstalleerd",
                    Color.FromArgb(34, 132, 67), true, message);
            }
            else if (String.Equals(state, "error",
                StringComparison.OrdinalIgnoreCase))
            {
                updateStatusTimer.Stop();
                updateCheckStartedAtUtc = DateTime.MinValue;
                SetUpdateButtonState(
                    "Opnieuw proberen", Color.FromArgb(186, 36, 36),
                    true, message);
            }
        }

        private bool ShowUpdateTimeoutIfNeeded(DateTime nowUtc)
        {
            if (updateCheckStartedAtUtc == DateTime.MinValue ||
                nowUtc - updateCheckStartedAtUtc <= TimeSpan.FromMinutes(2))
                return false;
            updateStatusTimer.Stop();
            updateCheckStartedAtUtc = DateTime.MinValue;
            SetUpdateButtonState(
                "Opnieuw proberen", Color.FromArgb(186, 36, 36),
                true, "De updatecontrole duurde te lang.");
            return true;
        }

        internal static DateTime ResolveUpdateStatusStartUtc(
            string checkedValue, DateTime statusFileUtc, DateTime nowUtc)
        {
            DateTime parsed;
            if (!String.IsNullOrWhiteSpace(checkedValue) &&
                DateTime.TryParse(checkedValue, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out parsed))
            {
                parsed = parsed.ToUniversalTime();
                if (parsed <= nowUtc.AddMinutes(1))
                    return parsed;
            }
            if (statusFileUtc != DateTime.MinValue)
            {
                DateTime normalized = statusFileUtc.ToUniversalTime();
                if (normalized <= nowUtc.AddMinutes(1))
                    return normalized;
            }
            return nowUtc;
        }

        internal bool VerifyStaleUpdateRecoveryForLayoutTest()
        {
            DateTime nowUtc = DateTime.UtcNow;
            DateTime staleUtc = nowUtc.AddMinutes(-3);
            Dictionary<string, string> values =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
            values["State"] = "installing";
            values["Checked"] = staleUtc.ToString("o");
            values["AvailableVersion"] = "99.0.0";
            values["Message"] = "Bestaande installatie wordt afgerond.";
            updateCheckStartedAtUtc = DateTime.MinValue;
            ApplyUpdateStatusValues(values, staleUtc, nowUtc);
            bool recovered = updateButton.Enabled &&
                String.Equals(updateButton.Text, "Opnieuw proberen",
                    StringComparison.Ordinal) &&
                !updateStatusTimer.Enabled;
            updateCheckStartedAtUtc = DateTime.MinValue;
            return recovered;
        }

        private void SetUpdateButtonState(
            string text, Color color, bool enabled, string description)
        {
            updateButton.Text = text;
            updateButton.Enabled = enabled;
            updateButton.ForeColor = color;
            updateButton.BackColor = Card;
            updateButton.FlatAppearance.BorderColor = color;
            updateButton.AccessibleDescription = description;
            boothAndVersionLabel.Text =
                (String.IsNullOrWhiteSpace(description)
                    ? ""
                    : description + "  ·  ") +
                "Booth: " + boothName + "  ·  Versie " +
                BuildInfo.Version;
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.S))
            {
                SaveAndClose();
                return true;
            }
            return base.ProcessCmdKey(ref message, keyData);
        }

        private void MarkDirty()
        {
            if (!initialized || rendering)
                return;
            dirty = true;
            UpdateSaveState();
        }

        private void UpdateSaveState()
        {
            if (saveButton == null)
                return;
            saveButton.Enabled = initialized && dirty;
            saveButton.BackColor = saveButton.Enabled
                ? Accent
                : Color.FromArgb(225, 228, 233);
            saveButton.ForeColor = saveButton.Enabled
                ? Color.White
                : Color.FromArgb(112, 119, 128);
        }

        private void ShowSettingsSection(bool fullpage)
        {
            showingCoverSettings = fullpage;
            profileCard.Visible = !fullpage;
            previewCard.Visible = !fullpage;
            grid.Visible = !fullpage;
            coverSettingsPanel.Visible = fullpage;
            if (fullpage)
            {
                addButton.Visible = false;
                removeButton.Visible = false;
                coverSettingsPanel.BringToFront();
            }
            else
            {
                addButton.Visible = customProfile.Checked;
                removeButton.Visible = customProfile.Checked;
            }
            SetSectionButtonStyle(isoSettingsTab, !fullpage);
            SetSectionButtonStyle(coverSettingsTab, fullpage);
            saveButton.BringToFront();
        }

        internal void ShowSectionForLayoutTest(
            bool fullpage, bool useMaximumText)
        {
            boothAndVersionLabel.Text =
                "Updatecontrole voltooid · Booth: " +
                "JVDP-SURFACE-BOOTH-MET-EEN-LANGE-NAAM" +
                " · Versie " + BuildInfo.Version;
            if (useMaximumText)
            {
                customCoverText.Checked = true;
                coverTitleInput.Text = new string('W', 60);
                coverMessageInput.Text =
                    "Dit is een maximale testtekst voor de volledige " +
                    "schermweergave. Alle woorden moeten leesbaar blijven, " +
                    "automatisch afbreken en binnen hun eigen vlak staan. " +
                    "Geen regel mag onder een knop, titel, invoerveld of " +
                    "voorbeeld terechtkomen. Ook op een compact Surface-" +
                    "scherm blijft deze volledige tekst bereikbaar en " +
                    "duidelijk zichtbaar voor de gebruiker.";
                if (coverMessageInput.Text.Length < 240)
                    coverMessageInput.Text = coverMessageInput.Text.PadRight(
                        240, '.');
            }
            ShowSettingsSection(fullpage);
            EnsureTextFitsWithinBounds();
        }

        private static void SetSectionButtonStyle(Button button, bool active)
        {
            button.FlatAppearance.BorderSize = active ? 0 : 1;
            button.BackColor = active ? Accent : Card;
            button.ForeColor = active ? Color.White : Accent;
        }

        private void CoverTextModeChanged()
        {
            if (renderingCoverText)
                return;
            RefreshCoverTextControls();
            MarkDirty();
        }

        private void CoverTextChanged()
        {
            if (renderingCoverText)
                return;
            if (customCoverText.Checked)
            {
                draftCustomCoverTitle = coverTitleInput.Text;
                draftCustomCoverMessage = coverMessageInput.Text;
                MarkDirty();
            }
            UpdateCoverTextPreview();
        }

        private void RefreshCoverTextControls()
        {
            renderingCoverText = true;
            try
            {
                bool custom = customCoverText.Checked;
                coverTitleInput.Enabled = custom;
                coverMessageInput.Enabled = custom;
                coverTitleInput.Text = custom
                    ? draftCustomCoverTitle : defaultCoverTitle;
                coverMessageInput.Text = custom
                    ? draftCustomCoverMessage : defaultCoverMessage;
            }
            finally
            {
                renderingCoverText = false;
            }
            UpdateCoverTextPreview();
        }

        private void UpdateCoverTextPreview()
        {
            if (coverPreviewTitle == null || coverPreviewMessage == null)
                return;
            coverPreviewTitle.Text = coverTitleInput.Text;
            coverPreviewMessage.Text = coverMessageInput.Text;
        }

        private void UpdatePreview()
        {
            if (previewRange == null || previewLightInput == null)
                return;
            List<IsoBand> bands = GetPreviewBands();
            int light = (int)previewLightInput.Value;
            int iso = -1;
            foreach (IsoBand band in bands)
            {
                if (light <= band.MaximumLight)
                {
                    iso = band.Iso;
                    break;
                }
            }
            previewRange.UpdateData(bands, light, iso);
            previewText.Text = iso > 0
                ? "Licht " + light + " → ISO " + iso
                : "Ongeldige indeling";
            previewText.ForeColor = iso > 0
                ? Color.FromArgb(20, 102, 196)
                : Color.FromArgb(186, 36, 36);
        }

        private List<IsoBand> GetPreviewBands()
        {
            if (!customProfile.Checked || grid.Rows.Count == 0)
                return CloneBands(defaultBands);
            try
            {
                List<IsoBand> result = new List<IsoBand>();
                int previous = -1;
                foreach (DataGridViewRow row in grid.Rows)
                {
                    int maximum = Convert.ToInt32(row.Cells[1].Value);
                    int iso = Convert.ToInt32(row.Cells[2].Value);
                    if (maximum <= previous || maximum > 100)
                        throw new InvalidOperationException();
                    result.Add(new IsoBand(maximum, iso));
                    previous = maximum;
                }
                if (result.Count == 0 || previous != 100)
                    throw new InvalidOperationException();
                return result;
            }
            catch
            {
                return CloneBands(CustomBands);
            }
        }

        private void ProfileChanged()
        {
            if (rendering)
                return;
            if (renderedCustom && !customProfile.Checked)
                CaptureCustomBands(false);
            RenderRows();
            MarkDirty();
        }

        private void RenderRows()
        {
            rendering = true;
            try
            {
                bool custom = customProfile.Checked;
                renderedCustom = custom;
                List<IsoBand> source = custom ? CustomBands : defaultBands;
                grid.Rows.Clear();
                int lower = 0;
                for (int index = 0; index < source.Count; index++)
                {
                    IsoBand band = source[index];
                    int row = grid.Rows.Add(lower, band.MaximumLight, band.Iso);
                    grid.Rows[row].Cells[0].ReadOnly = true;
                    grid.Rows[row].Cells[1].ReadOnly =
                        !custom || index == source.Count - 1;
                    lower = band.MaximumLight + 1;
                }
                grid.ReadOnly = !custom;
                if (custom)
                {
                    grid.Columns[0].ReadOnly = true;
                    grid.Columns[1].ReadOnly = false;
                    grid.Rows[grid.Rows.Count - 1].Cells[1].ReadOnly = true;
                }
                ((DataGridViewComboBoxColumn)grid.Columns[2]).DisplayStyle = custom
                    ? DataGridViewComboBoxDisplayStyle.DropDownButton
                    : DataGridViewComboBoxDisplayStyle.Nothing;
                addButton.Enabled = custom && source.Count < 8;
                removeButton.Enabled = custom && source.Count > 1;
                addButton.Visible = custom && !showingCoverSettings;
                removeButton.Visible = custom && !showingCoverSettings;
                explanation.Text = custom
                    ? "Dit profiel wordt alleen op deze booth opgeslagen. " +
                      "De bereiken sluiten automatisch op elkaar aan."
                    : "Dit standaardprofiel wordt vanuit de centrale " +
                      "software-update voor alle booths beheerd.";
                grid.ClearSelection();
                UpdatePreview();
            }
            finally
            {
                rendering = false;
            }
        }

        private bool CaptureCustomBands(bool showError)
        {
            try
            {
                grid.EndEdit();
                List<IsoBand> bands = new List<IsoBand>();
                int previous = -1;
                foreach (DataGridViewRow row in grid.Rows)
                {
                    int maximum = Convert.ToInt32(row.Cells[1].Value);
                    int iso = Convert.ToInt32(row.Cells[2].Value);
                    if (maximum <= previous || maximum > 100 ||
                        Array.IndexOf(supportedIsos, iso) < 0)
                        throw new InvalidOperationException();
                    bands.Add(new IsoBand(maximum, iso));
                    previous = maximum;
                }
                if (bands.Count < 1 || bands.Count > 8 || previous != 100)
                    throw new InvalidOperationException();
                CustomBands = bands;
                return true;
            }
            catch
            {
                if (showError)
                    MessageBox.Show(this,
                        "Controleer de lichtbereiken. Ze moeten zonder gaten " +
                        "oplopen van 0 tot en met 100.",
                        "Ongeldig ISO-profiel", MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                return false;
            }
        }

        private void AddRange()
        {
            if (!customProfile.Checked || !CaptureCustomBands(true) ||
                CustomBands.Count >= 8)
                return;
            int last = CustomBands.Count - 1;
            int lower = last == 0 ? 0 : CustomBands[last - 1].MaximumLight + 1;
            if (lower >= 100)
            {
                MessageBox.Show(this, "Er is geen ruimte voor nog een bereik.",
                    "Bereik toevoegen", MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
            int split = (lower + 99) / 2;
            CustomBands.Insert(last,
                new IsoBand(split, CustomBands[last].Iso));
            RenderRows();
            grid.ClearSelection();
            grid.Rows[last].Selected = true;
            MarkDirty();
        }

        private void RemoveRange()
        {
            if (!customProfile.Checked || !CaptureCustomBands(true) ||
                CustomBands.Count <= 1)
                return;
            int selected = grid.SelectedRows.Count > 0
                ? grid.SelectedRows[0].Index
                : CustomBands.Count - 2;
            selected = Math.Max(0, Math.Min(CustomBands.Count - 1, selected));
            CustomBands.RemoveAt(selected);
            CustomBands[CustomBands.Count - 1].MaximumLight = 100;
            RenderRows();
            MarkDirty();
        }

        private void SaveAndClose()
        {
            if (!dirty)
                return;
            if (customProfile.Checked && !CaptureCustomBands(true))
                return;
            if (customCoverText.Checked &&
                (String.IsNullOrWhiteSpace(draftCustomCoverTitle) ||
                 String.IsNullOrWhiteSpace(draftCustomCoverMessage)))
            {
                ShowSettingsSection(true);
                MessageBox.Show(this,
                    "Vul zowel de koptekst als het bericht voor het " +
                    "volledige scherm in.",
                    "Tekst ontbreekt", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
            UseCustomMapping = customProfile.Checked;
            UseCustomCoverText = customCoverText.Checked;
            DialogResult = DialogResult.OK;
            Close();
        }

        private static List<IsoBand> CloneBands(List<IsoBand> source)
        {
            List<IsoBand> copy = new List<IsoBand>();
            foreach (IsoBand band in source)
                copy.Add(band.Clone());
            return copy;
        }

        private static Label MakeDialogLabel(
            string text, float size, FontStyle style, Color color)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = false;
            label.Font = new Font("Segoe UI", size, style);
            label.ForeColor = color;
            label.BackColor = Color.Transparent;
            label.TextAlign = ContentAlignment.MiddleLeft;
            return label;
        }

        private static Button MakeDialogButton(string text, bool primary)
        {
            Button button = new Button();
            button.Text = text;
            button.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Accent;
            button.BackColor = primary ? Accent : Card;
            button.ForeColor = primary ? Color.White : Accent;
            button.UseVisualStyleBackColor = false;
            button.MinimumSize = new Size(44, 42);
            button.AccessibleRole = AccessibleRole.PushButton;
            return button;
        }
    }

    internal sealed class ManualCoverControlForm : Form
    {
        internal ManualCoverControlForm(
            Rectangle workingArea, EventHandler hideAction)
        {
            Text = "Volledig scherm verbergen";
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10, FontStyle.Regular);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(13, 17, 23);
            TopMost = true;
            // Keep this as an unowned native top-level window. WinForms uses a
            // hidden parking window as owner when ShowInTaskbar is false; on a
            // mixed-DPI PC that gives the control the parking window's DPI.
            // WS_EX_TOOLWINDOW below keeps it out of the taskbar instead.
            ShowInTaskbar = true;

            Button hideButton = new Button();
            hideButton.Text = "VOLLEDIG SCHERM VERBERGEN";
            hideButton.Dock = DockStyle.Fill;
            hideButton.AutoSize = true;
            hideButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            hideButton.Padding = new Padding(20, 10, 20, 10);
            hideButton.Font = new Font("Segoe UI", 11, FontStyle.Bold);
            hideButton.ForeColor = Color.White;
            hideButton.BackColor = Color.FromArgb(187, 45, 59);
            hideButton.FlatStyle = FlatStyle.Flat;
            hideButton.FlatAppearance.BorderSize = 0;
            hideButton.Click += hideAction;
            Controls.Add(hideButton);
            Size textSize = TextRenderer.MeasureText(
                hideButton.Text, hideButton.Font, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            ClientSize = new Size(
                Math.Max(300, textSize.Width + 48),
                Math.Max(64, textSize.Height + 28));
            MinimumSize = Size;
            Location = new Point(
                workingArea.Right - Width - 24,
                workingArea.Top + 24);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WsExToolWindow = 0x00000080;
                const int WsExAppWindow = 0x00040000;
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle &= ~WsExAppWindow;
                parameters.ExStyle |= WsExToolWindow;
                return parameters;
            }
        }
    }
    internal sealed class ActionCoverForm : Form
    {
        private readonly string coverTitle;
        private readonly string coverMessage;

        internal ActionCoverForm(
            Rectangle bounds, string title, string message)
        {
            Text = "JvdP Camera Screen";
            coverTitle = title;
            coverMessage = message;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            BackColor = Color.White;
            ForeColor = Color.FromArgb(31, 38, 58);
            TopMost = true;
            // Stay unowned so the window is created with the target monitor's
            // DPI. WS_EX_TOOLWINDOW keeps it out of taskbar and Alt+Tab.
            ShowInTaskbar = true;
            AllowTransparency = false;
            Opacity = 1.0;
            DoubleBuffered = true;
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WsExToolWindow = 0x00000080;
                const int WsExAppWindow = 0x00040000;
                const int WsExNoActivate = 0x08000000;
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle &= ~WsExAppWindow;
                parameters.ExStyle |= WsExToolWindow | WsExNoActivate;
                return parameters;
            }
        }


        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            args.Graphics.Clear(Color.White);
            args.Graphics.TextRenderingHint =
                System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            RectangleF titleArea;
            RectangleF detailArea;
            CalculateTextAreas(
                ClientSize, out titleArea, out detailArea);

            using (StringFormat centered = CreateCenteredTextFormat())
            using (Font titleFont = CreateFittedFont(
                args.Graphics, coverTitle, "Britannic Bold",
                FontStyle.Regular, 54f, 15f, titleArea.Size, centered))
            using (Font detailFont = CreateFittedFont(
                args.Graphics, coverMessage, "Britannic Bold",
                FontStyle.Regular, 34f, 13f, detailArea.Size, centered))
            using (Brush titleBrush = new SolidBrush(
                Color.FromArgb(31, 38, 58)))
            using (Brush detailBrush = new SolidBrush(
                Color.FromArgb(73, 82, 105)))
            {
                args.Graphics.DrawString(
                    coverTitle, titleFont, titleBrush,
                    titleArea, centered);
                args.Graphics.DrawString(
                    coverMessage, detailFont, detailBrush,
                    detailArea, centered);
            }
        }

        internal static void CalculateTextAreas(
            Size clientSize, out RectangleF titleArea,
            out RectangleF detailArea)
        {
            float horizontalMargin = Math.Max(
                36f, clientSize.Width * 0.07f);
            float verticalMargin = Math.Max(
                32f, clientSize.Height * 0.07f);
            float contentWidth = Math.Max(
                1f, clientSize.Width - horizontalMargin * 2f);
            float contentHeight = Math.Max(
                1f, clientSize.Height - verticalMargin * 2f);
            float gap = Math.Max(16f, contentHeight * 0.035f);
            float titleHeight = Math.Max(
                54f, (contentHeight - gap) * 0.34f);
            float messageHeight = Math.Max(
                54f, contentHeight - titleHeight - gap);
            titleArea = new RectangleF(
                horizontalMargin, verticalMargin,
                contentWidth, titleHeight);
            detailArea = new RectangleF(
                horizontalMargin, verticalMargin + titleHeight + gap,
                contentWidth, messageHeight);
        }

        private static StringFormat CreateCenteredTextFormat()
        {
            StringFormat format = new StringFormat();
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.None;
            return format;
        }

        private static Font CreateFittedFont(
            Graphics graphics, string text, string family,
            FontStyle style, float maximumSize, float minimumSize,
            SizeF available, StringFormat format)
        {
            string value = String.IsNullOrEmpty(text) ? " " : text;
            float readableMinimum = Math.Max(
                OverlayForm.MinimumReadableUiFontSize,
                Math.Min(maximumSize, minimumSize));
            for (float size = maximumSize;
                size >= readableMinimum; size -= 1f)
            {
                Font candidate = new Font(family, size, style);
                SizeF measured = graphics.MeasureString(
                    value, candidate,
                    Math.Max(1, (int)Math.Floor(available.Width)), format);
                if (measured.Width <= available.Width + 1f &&
                    measured.Height <= available.Height + 1f)
                    return candidate;
                candidate.Dispose();
            }
            return new Font(family, readableMinimum, style);
        }
    }
    internal sealed class ComboItem
    {
        internal readonly int Index;
        internal readonly string Value;

        internal ComboItem(int index, string value)
        {
            Index = index;
            Value = value;
        }
    }
    internal static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ComboBoxInfo
        {
            internal int Size;
            internal Rect ItemRect;
            internal Rect ButtonRect;
            internal uint ButtonState;
            internal IntPtr ComboHandle;
            internal IntPtr ItemHandle;
            internal IntPtr ListHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }
    }

    internal static class Program
    {
        private const int WmActivateExistingInstance = 0x8001;

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(
            IntPtr dpiContext);

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(
            IntPtr dpiContext);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(
            string className, string windowName);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(
            IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

        [STAThread]
        private static void Main(string[] args)
        {
            EnablePerMonitorDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool startInTray = Array.Exists(
                args, delegate(string value)
                {
                    return String.Equals(value, "--startup",
                        StringComparison.OrdinalIgnoreCase);
                });
            using (Mutex instanceMutex = new Mutex(
                false, @"Local\JvDPLichtregeling"))
            {
                bool ownsInstance = false;
                try
                {
                    ownsInstance = instanceMutex.WaitOne(0, false);
                }
                catch (AbandonedMutexException)
                {
                    ownsInstance = true;
                }
                if (!ownsInstance)
                {
                    IntPtr existing = FindWindow(
                        null, "JvdP Lichtregeling");
                    if (existing != IntPtr.Zero)
                        PostMessage(existing,
                            WmActivateExistingInstance,
                            IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                try
                {
                    Application.Run(new OverlayForm(startInTray));
                }
                finally
                {
                    instanceMutex.ReleaseMutex();
                }
            }
        }

        internal static void EnablePerMonitorDpiAwareness()
        {
            IntPtr perMonitorV2 = new IntPtr(-4);
            try
            {
                if (!SetProcessDpiAwarenessContext(perMonitorV2))
                    SetProcessDPIAware();
            }
            catch (EntryPointNotFoundException)
            {
                SetProcessDPIAware();
            }
            try
            {
                // A deployment manifest or a host can establish process DPI
                // awareness before Main runs. Explicitly setting the UI
                // thread keeps every newly created form PerMonitorV2 in that
                // situation as well, including when moved to a Surface panel.
                SetThreadDpiAwarenessContext(perMonitorV2);
            }
            catch (EntryPointNotFoundException)
            {
            }
        }
    }
}
