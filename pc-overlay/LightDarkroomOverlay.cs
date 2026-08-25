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
        private List<IsoBand> bands = new List<IsoBand>();
        private int lightValue = -1;
        private int activeIso = -1;
        private bool dragging;
        private float visualScale = 1f;

        public bool Interactive { get; set; }

        public float VisualScale
        {
            get { return visualScale; }
            set
            {
                visualScale = Math.Max(1f, Math.Min(1.3f, value));
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
            int barLeft = 19;
            int barRight = Math.Max(barLeft + 1, ClientSize.Width - 19);
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
            Invalidate();
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
            using (Font rangeFont = new Font(
                "Segoe UI", 9.5f * visualScale, FontStyle.Bold))
            using (Font isoFont = new Font(
                "Segoe UI", 9f * visualScale, FontStyle.Regular))
            {
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
                        new Rectangle(left + 4,
                            content.Top + (int)Math.Round(8 * visualScale),
                            Math.Max(1, right - left - 8),
                            (int)Math.Round(24 * visualScale)),
                        rangeColor, TextFormatFlags.HorizontalCenter |
                        TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(e.Graphics,
                        "ISO " + band.Iso, isoFont,
                        new Rectangle(left + 4,
                            content.Top + (int)Math.Round(34 * visualScale),
                            Math.Max(1, right - left - 8),
                            (int)Math.Round(23 * visualScale)),
                        active ? Color.FromArgb(20, 102, 196) :
                            Color.FromArgb(101, 108, 118),
                        TextFormatFlags.HorizontalCenter |
                        TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPadding);
                    lower = band.MaximumLight + 1;
                }
            }

            int barY = content.Bottom - (int)Math.Round(19 * visualScale);
            int barLeft = content.Left + (int)Math.Round(18 * visualScale);
            int barRight = content.Right - (int)Math.Round(18 * visualScale);
            using (Pen baseBar = new Pen(
                Color.FromArgb(190, 196, 204), 5f * visualScale))
            {
                baseBar.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                baseBar.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                e.Graphics.DrawLine(baseBar, barLeft, barY, barRight, barY);
            }
            if (activeIndex >= 0)
            {
                int segmentLeft = barLeft +
                    (barRight - barLeft) * activeIndex / bands.Count;
                int segmentRight = barLeft +
                    (barRight - barLeft) * (activeIndex + 1) / bands.Count;
                using (Pen activeBar = new Pen(
                    Color.FromArgb(31, 111, 235), 5f * visualScale))
                {
                    activeBar.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    activeBar.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    e.Graphics.DrawLine(activeBar, segmentLeft, barY,
                        segmentRight, barY);
                }
            }
            if (lightValue >= 0)
            {
                int markerX = barLeft + (int)Math.Round(
                    (barRight - barLeft) * Math.Max(0,
                        Math.Min(100, lightValue)) / 100.0);
                int markerRadius = (int)Math.Round(
                    (Interactive ? 11 : 8) * visualScale);
                using (SolidBrush marker = new SolidBrush(
                    Color.FromArgb(31, 111, 235)))
                    e.Graphics.FillEllipse(marker, markerX - markerRadius,
                        barY - markerRadius, markerRadius * 2,
                        markerRadius * 2);
                using (Pen whiteRing = new Pen(Color.White, 2f))
                    e.Graphics.DrawEllipse(whiteRing,
                        markerX - markerRadius, barY - markerRadius,
                        markerRadius * 2, markerRadius * 2);
            }
        }
    }

    internal sealed class OverlayForm : Form
    {
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
        private readonly LightRangeControl rangeControl;
        private readonly TableLayoutPanel metrics;
        private readonly FlowLayoutPanel dashboard;
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
        private int stabilitySeconds = 30;
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
        private readonly Dictionary<Control, Rectangle>
            responsiveDashboardBounds =
                new Dictionary<Control, Rectangle>();
        private readonly Dictionary<Control, Padding>
            responsiveDashboardMargins =
                new Dictionary<Control, Padding>();
        private float lastResponsiveScale = -1f;

        private static readonly int[] SupportedIsoValues = {
            100, 125, 160, 200, 250, 320, 400, 500, 640, 800,
            1000, 1250, 1600, 2000, 2500, 3200, 4000, 5000,
            6400, 8000, 10000, 12800, 25600
        };

        public OverlayForm(bool startMinimizedToTray)
        {
            Text = "JvdP Lichtregeling";
            Icon applicationIcon = Icon.ExtractAssociatedIcon(
                Application.ExecutablePath);
            Icon = applicationIcon ?? SystemIcons.Information;
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
            WindowState = FormWindowState.Maximized;

            Panel header = new Panel();
            header.BackColor = Color.White;
            header.SetBounds(0, 0, ClientSize.Width, 64);
            header.Anchor = AnchorStyles.Top | AnchorStyles.Left |
                AnchorStyles.Right;
            Controls.Add(header);

            Label title = MakeLabel(
                "JvdP Lichtregeling", 18, FontStyle.Bold,
                Color.FromArgb(18, 22, 27));
            title.SetBounds(24, 8, 270, 48);
            header.Controls.Add(title);

            settingsButton = MakeLightButton("Instellingen", false);
            settingsButton.AccessibleName = "Profielinstellingen openen";
            settingsButton.SetBounds(header.Width - 264, 12, 240, 40);
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
            header.Controls.Add(settingsButton);

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
            statusButton.SetBounds((header.Width - 220) / 2, 12, 220, 40);
            statusButton.Anchor = AnchorStyles.Top;
            statusButton.Click += delegate { ToggleStatusPanel(); };
            header.Controls.Add(statusButton);
            header.SizeChanged += delegate
            {
                statusButton.Left =
                    Math.Max(270, (header.ClientSize.Width -
                        statusButton.Width) / 2);
            };

            Panel headerDivider = new Panel();
            headerDivider.BackColor = Color.FromArgb(218, 222, 229);
            headerDivider.SetBounds(0, 63, header.Width, 1);
            headerDivider.Anchor = AnchorStyles.Left | AnchorStyles.Right |
                AnchorStyles.Bottom;
            header.Controls.Add(headerDivider);

            ResizeEnd += delegate
            {
                SaveOverlayPosition();
                SaveOverlaySize();
            };
            Resize += delegate
            {
                HideInTrayWhenMinimized();
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
            Panel mappingPanel = MakeLightSurface(sectionWidth, 264);
            mappingPanel.Margin = new Padding(0, 0, 0, 12);
            dashboard.Controls.Add(mappingPanel);

            Label mappingTitle = MakeLabel(
                "Live lichtwaarde → Doel-ISO", 13, FontStyle.Regular,
                Color.FromArgb(31, 35, 40));
            mappingTitle.SetBounds(20, 10, 370, 34);
            mappingPanel.Controls.Add(mappingTitle);

            lastMeasuredLabel = MakeLabel(
                "Wachten op eerste meting", 9, FontStyle.Regular,
                Color.FromArgb(99, 108, 118));
            lastMeasuredLabel.TextAlign = ContentAlignment.MiddleRight;
            lastMeasuredLabel.SetBounds(mappingPanel.Width - 300, 10, 280, 34);
            lastMeasuredLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            mappingPanel.Controls.Add(lastMeasuredLabel);

            rangeControl = new LightRangeControl();
            rangeControl.SetBounds(20, 48, mappingPanel.Width - 40, 100);
            rangeControl.Anchor = AnchorStyles.Top | AnchorStyles.Left |
                AnchorStyles.Right;
            mappingPanel.Controls.Add(rangeControl);

            metrics = new TableLayoutPanel();
            metrics.ColumnCount = 3;
            metrics.RowCount = 2;
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            metrics.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            metrics.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            metrics.SetBounds(20, 158, mappingPanel.Width - 40, 88);
            metrics.Anchor = AnchorStyles.Top | AnchorStyles.Left |
                AnchorStyles.Right;
            mappingPanel.Controls.Add(metrics);

            Label lightCaption = MakeLabel(
                "Lichtwaarde", 10, FontStyle.Regular,
                Color.FromArgb(54, 65, 82));
            lightCaption.TextAlign = ContentAlignment.MiddleCenter;
            metrics.Controls.Add(lightCaption, 0, 0);
            lightCaption.Dock = DockStyle.Fill;

            Label rangeCaption = MakeLabel(
                "Actief bereik", 10, FontStyle.Regular,
                Color.FromArgb(54, 65, 82));
            rangeCaption.TextAlign = ContentAlignment.MiddleCenter;
            metrics.Controls.Add(rangeCaption, 1, 0);
            rangeCaption.Dock = DockStyle.Fill;

            Label targetCaption = MakeLabel(
                "Doel-ISO", 10, FontStyle.Regular,
                Color.FromArgb(54, 65, 82));
            targetCaption.TextAlign = ContentAlignment.MiddleCenter;
            metrics.Controls.Add(targetCaption, 2, 0);
            targetCaption.Dock = DockStyle.Fill;

            lightLabel = MakeLabel(
                "—", 30, FontStyle.Bold, Color.FromArgb(31, 111, 235));
            lightLabel.TextAlign = ContentAlignment.MiddleCenter;
            lightLabel.Dock = DockStyle.Fill;
            lightLabel.AccessibleName = "Actuele lichtwaarde";
            metrics.Controls.Add(lightLabel, 0, 1);

            activeRangeLabel = MakeLabel(
                "—", 23, FontStyle.Bold, Color.FromArgb(31, 111, 235));
            activeRangeLabel.TextAlign = ContentAlignment.MiddleCenter;
            activeRangeLabel.Dock = DockStyle.Fill;
            activeRangeLabel.AccessibleName = "Actief lichtbereik";
            metrics.Controls.Add(activeRangeLabel, 1, 1);

            targetIsoLabel = MakeLabel(
                "—", 30, FontStyle.Bold, Color.FromArgb(31, 111, 235));
            targetIsoLabel.TextAlign = ContentAlignment.MiddleCenter;
            targetIsoLabel.Dock = DockStyle.Fill;
            targetIsoLabel.AccessibleName = "Doel ISO";
            metrics.Controls.Add(targetIsoLabel, 2, 1);

            mappingModeInput = new ComboBox();
            mappingModeInput.Items.Add("Standaard · alle booths");
            mappingModeInput.Items.Add("Eigen · deze booth");
            mappingModeInput.Visible = false;
            mappingSummaryLabel = MakeLabel(
                "", 9, FontStyle.Regular, Color.FromArgb(99, 108, 118));
            mappingSummaryLabel.Visible = false;

            Panel profilePanel = MakeLightSurface(sectionWidth, 62);
            profilePanel.Margin = new Padding(0, 0, 0, 10);
            dashboard.Controls.Add(profilePanel);
            Label profileCaption = MakeLabel(
                "Actief profiel", 9, FontStyle.Regular,
                Color.FromArgb(99, 108, 118));
            profileCaption.SetBounds(18, 7, 180, 22);
            profilePanel.Controls.Add(profileCaption);
            profileNameLabel = MakeLabel(
                "Standaard · alle booths", 11, FontStyle.Bold,
                Color.FromArgb(31, 35, 40));
            profileNameLabel.SetBounds(18, 27, profilePanel.Width - 210, 28);
            profileNameLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left |
                AnchorStyles.Right;
            profilePanel.Controls.Add(profileNameLabel);
            mappingSettingsButton = MakeLightButton("Profiel bekijken", false);
            mappingSettingsButton.SetBounds(profilePanel.Width - 172, 10, 154, 42);
            mappingSettingsButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            mappingSettingsButton.Click += delegate { ShowIsoMappingSettings(); };
            profilePanel.Controls.Add(mappingSettingsButton);

            Panel detailsHeader = MakeLightSurface(sectionWidth, 52);
            detailsHeader.Margin = new Padding(0, 0, 0, 4);
            dashboard.Controls.Add(detailsHeader);
            detailsToggleButton = MakeLightButton(
                "Verbindingen en technische details", false);
            detailsToggleButton.FlatAppearance.BorderSize = 0;
            detailsToggleButton.TextAlign = ContentAlignment.MiddleLeft;
            detailsToggleButton.SetBounds(10, 5,
                Math.Min(360, detailsHeader.Width - 300), 42);
            detailsToggleButton.AccessibleDescription =
                "Toont of verbergt technische verbindingsinformatie.";
            detailsHeader.Controls.Add(detailsToggleButton);
            detailsSummaryLabel = MakeLabel(
                "Sensor zoeken · Darkroom zoeken", 9, FontStyle.Regular,
                Color.FromArgb(99, 108, 118));
            detailsSummaryLabel.TextAlign = ContentAlignment.MiddleRight;
            detailsSummaryLabel.SetBounds(detailsHeader.Width - 390, 5, 372, 42);
            detailsSummaryLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            detailsHeader.Controls.Add(detailsSummaryLabel);

            detailsPanel = MakeLightSurface(sectionWidth, 148);
            detailsPanel.Margin = new Padding(0, 0, 0, 8);
            detailsPanel.Visible = false;
            dashboard.Controls.Add(detailsPanel);

            AddLightDetailCaption(detailsPanel, "Lichtsensor", 18, 10);
            serialLabel = MakeLabel(
                "Sensor zoeken", 10, FontStyle.Bold,
                Color.FromArgb(137, 87, 0));
            serialLabel.SetBounds(160, 10, detailsPanel.Width / 2 - 178, 28);
            detailsPanel.Controls.Add(serialLabel);
            AddLightDetailCaption(detailsPanel, "Darkroom", 18, 42);
            darkroomLabel = MakeLabel(
                "Niet actief", 10, FontStyle.Bold,
                Color.FromArgb(186, 36, 36));
            darkroomLabel.SetBounds(160, 42, detailsPanel.Width / 2 - 178, 28);
            detailsPanel.Controls.Add(darkroomLabel);

            int rightDetail = detailsPanel.Width / 2 + 10;
            AddLightDetailCaption(detailsPanel, "Automatisch aanpassen",
                rightDetail, 10);
            boothLabel = MakeLabel(
                "Niet actief", 10, FontStyle.Bold,
                Color.FromArgb(186, 36, 36));
            boothLabel.SetBounds(rightDetail + 175, 10,
                detailsPanel.Width - rightDetail - 193, 28);
            boothLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            detailsPanel.Controls.Add(boothLabel);
            AddLightDetailCaption(detailsPanel, "ISO in Darkroom",
                rightDetail, 42);
            currentIsoLabel = MakeLabel(
                "Onbekend", 10, FontStyle.Bold,
                Color.FromArgb(99, 108, 118));
            currentIsoLabel.SetBounds(rightDetail + 175, 42,
                detailsPanel.Width - rightDetail - 193, 28);
            currentIsoLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            detailsPanel.Controls.Add(currentIsoLabel);

            Label stabilityCaption = MakeLabel(
                "Wachttijd vóór aanpassen", 9, FontStyle.Regular,
                Color.FromArgb(99, 108, 118));
            stabilityCaption.SetBounds(18, 82, 170, 28);
            detailsPanel.Controls.Add(stabilityCaption);
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
            stabilitySecondsInput.SetBounds(190, 82, 72, 28);
            stabilitySecondsInput.AccessibleName =
                "Wachttijd voor automatisch aanpassen in seconden";
            stabilitySecondsInput.ValueChanged += delegate
            {
                stabilitySeconds = (int)stabilitySecondsInput.Value;
                SaveStabilitySeconds();
                RefreshUi();
            };
            detailsPanel.Controls.Add(stabilitySecondsInput);
            countdownLabel = MakeLabel(
                "0,0 / 30,0 sec", 9, FontStyle.Regular,
                Color.FromArgb(99, 108, 118));
            countdownLabel.SetBounds(276, 82, 130, 28);
            detailsPanel.Controls.Add(countdownLabel);
            Panel progressBack = new Panel();
            progressBack.BackColor = Color.FromArgb(218, 222, 229);
            progressBack.SetBounds(18, 121, detailsPanel.Width - 36, 6);
            progressBack.Anchor = AnchorStyles.Top | AnchorStyles.Left |
                AnchorStyles.Right;
            detailsPanel.Controls.Add(progressBack);
            progressFill = new Panel();
            progressFill.BackColor = Color.FromArgb(31, 111, 235);
            progressFill.SetBounds(0, 0, 0, 6);
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
            actionLabel.SetBounds(0, 0, sectionWidth, 32);
            actionLabel.Margin = new Padding(0, 0, 0, 8);
            actionLabel.TextAlign = ContentAlignment.MiddleLeft;
            actionLabel.AccessibleName = "Actuele activiteit";
            dashboard.Controls.Add(actionLabel);

            TableLayoutPanel actionPanel = new TableLayoutPanel();
            actionPanel.ColumnCount = 2;
            actionPanel.RowCount = 1;
            actionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            actionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            actionPanel.SetBounds(0, 0, sectionWidth, 58);
            actionPanel.Margin = new Padding(0);
            dashboard.Controls.Add(actionPanel);

            pauseButton = MakeLightButton(
                "Automatisch aanpassen stoppen", false);
            pauseButton.Dock = DockStyle.Fill;
            pauseButton.Margin = new Padding(0, 0, 6, 0);
            pauseButton.Click += delegate { TogglePause(); };
            actionPanel.Controls.Add(pauseButton, 0, 0);

            runActionButton = MakeLightButton("Doel-ISO nu toepassen", true);
            runActionButton.Dock = DockStyle.Fill;
            runActionButton.Margin = new Padding(6, 0, 0, 0);
            runActionButton.Click += delegate { StartTargetIsoAction(false); };
            actionPanel.Controls.Add(runActionButton, 1, 0);

            coverToggleButton = new Button();
            coverToggleButton.Visible = false;

            dashboard.SizeChanged += delegate
            {
                int width = Math.Max(640,
                    dashboard.ClientSize.Width - dashboard.Padding.Horizontal -
                    (dashboard.VerticalScroll.Visible
                        ? SystemInformation.VerticalScrollBarWidth : 0) - 20);
                mappingPanel.Width = width;
                profilePanel.Width = width;
                detailsHeader.Width = width;
                detailsPanel.Width = width;
                actionLabel.Width = width;
                actionPanel.Width = width;
                float scale = GetResponsiveScale();
                mappingTitle.Width = (int)Math.Round(370 * scale);
                lastMeasuredLabel.Width = (int)Math.Round(280 * scale);
                lastMeasuredLabel.Left = mappingPanel.Width -
                    lastMeasuredLabel.Width - 20;
                mappingSettingsButton.Width =
                    (int)Math.Round(154 * scale);
                mappingSettingsButton.Left = profilePanel.Width -
                    mappingSettingsButton.Width - 18;
                profileNameLabel.Width = Math.Max(220,
                    mappingSettingsButton.Left - 36);
                detailsToggleButton.Width = Math.Min(
                    (int)Math.Round(360 * scale),
                    Math.Max(360, detailsHeader.Width - 390));
                detailsSummaryLabel.Width = Math.Min(
                    (int)Math.Round(372 * scale),
                    Math.Max(280, detailsHeader.Width -
                        detailsToggleButton.Right - 36));
                detailsSummaryLabel.Left = detailsHeader.Width -
                    detailsSummaryLabel.Width - 18;
                dashboard.PerformLayout();
            };
            UpdateMappingControls();

            statusPanel = MakeLightSurface(500, 270);
            statusPanel.Visible = false;
            statusPanel.Padding = new Padding(22);
            Controls.Add(statusPanel);

            statusPanelTitle = MakeLabel(
                "Systeemstatus", 17, FontStyle.Bold,
                Color.FromArgb(31, 35, 40));
            statusPanelTitle.SetBounds(22, 14, 390, 42);
            statusPanel.Controls.Add(statusPanelTitle);

            Button closeStatusButton = MakeLightButton("×", false);
            closeStatusButton.Font = new Font("Segoe UI", 16, FontStyle.Regular);
            closeStatusButton.AccessibleName = "Status sluiten";
            closeStatusButton.SetBounds(statusPanel.Width - 58, 12, 42, 42);
            closeStatusButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            closeStatusButton.Click += delegate { statusPanel.Visible = false; };
            statusPanel.Controls.Add(closeStatusButton);

            statusPanelMessage = MakeLabel(
                "", 10, FontStyle.Regular, Color.FromArgb(71, 78, 88));
            statusPanelMessage.SetBounds(22, 64,
                statusPanel.Width - 44, 112);
            statusPanelMessage.Anchor = AnchorStyles.Top |
                AnchorStyles.Left | AnchorStyles.Right;
            statusPanel.Controls.Add(statusPanelMessage);

            statusPanelToggleButton = MakeLightButton(
                "Automatisch aanpassen stoppen", false);
            statusPanelToggleButton.SetBounds(22, 196,
                statusPanel.Width - 44, 50);
            statusPanelToggleButton.Anchor = AnchorStyles.Bottom |
                AnchorStyles.Left | AnchorStyles.Right;
            statusPanelToggleButton.Click += delegate { TogglePause(); };
            statusPanel.Controls.Add(statusPanelToggleButton);
            PositionStatusPanel();

            trayIcon = new NotifyIcon();
            trayIcon.Icon = Icon;
            trayIcon.Text = "JvdP Lichtregeling";
            trayIcon.Visible = true;
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
            uiTimer.Start();

            coverWinEventDelegate = CoverWinEvent;
            coverGuardTimer = new System.Windows.Forms.Timer();
            coverGuardTimer.Interval = 250;
            coverGuardTimer.Tick += delegate { RaiseFullscreenCovers(); };

            CaptureResponsiveFonts(dashboard);
            CaptureResponsiveDashboardLayout(dashboard);
            ApplyResponsiveTypography();

            Shown += delegate
            {
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

        private void ApplyResponsiveTypography()
        {
            float scale = GetResponsiveScale();
            if (Math.Abs(scale - lastResponsiveScale) < 0.025f)
                return;
            lastResponsiveScale = scale;

            foreach (KeyValuePair<Control, float> item in
                responsiveFontSizes)
            {
                if (item.Key == null || item.Key.IsDisposed)
                    continue;
                item.Key.Font = new Font(
                    "Segoe UI", item.Value * scale,
                    item.Key.Font.Style);
            }
            if (rangeControl != null)
                rangeControl.VisualScale = scale;
            if (metrics != null && metrics.RowStyles.Count > 0)
                metrics.RowStyles[0].Height =
                    (int)Math.Ceiling(31 * scale);

            foreach (KeyValuePair<Control, Rectangle> item in
                responsiveDashboardBounds)
            {
                Control control = item.Key;
                if (control == null || control.IsDisposed ||
                    control.Dock != DockStyle.None)
                    continue;
                Rectangle basis = item.Value;
                if (control.Parent == dashboard)
                {
                    control.Height = (int)Math.Round(
                        basis.Height * scale);
                    Padding margin;
                    if (responsiveDashboardMargins.TryGetValue(
                        control, out margin))
                        control.Margin = new Padding(
                            margin.Left,
                            (int)Math.Round(margin.Top * scale),
                            margin.Right,
                            (int)Math.Round(margin.Bottom * scale));
                    continue;
                }
                control.Top = (int)Math.Round(basis.Top * scale);
                control.Height = (int)Math.Round(basis.Height * scale);
            }
            dashboard.Padding = new Padding(
                24, (int)Math.Round(18 * scale),
                24, (int)Math.Round(18 * scale));
            dashboard.PerformLayout();
        }

        private float GetResponsiveScale()
        {
            float widthScale = ClientSize.Width / 1100f;
            float heightScale = ClientSize.Height / 720f;
            return Math.Max(1f,
                Math.Min(1.45f, Math.Min(widthScale, heightScale)));
        }

        private void CaptureResponsiveDashboardLayout(Control root)
        {
            if (root == null)
                return;
            foreach (Control child in root.Controls)
            {
                if (!responsiveDashboardBounds.ContainsKey(child))
                {
                    responsiveDashboardBounds.Add(child, child.Bounds);
                    responsiveDashboardMargins.Add(child, child.Margin);
                }
                CaptureResponsiveDashboardLayout(child);
            }
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
            if (statusPanel == null)
                return;
            int width = Math.Min(500, Math.Max(420, ClientSize.Width - 48));
            int height = 270;
            statusPanel.SetBounds(
                Math.Max(12, (ClientSize.Width - width) / 2),
                64 + Math.Max(18, (ClientSize.Height - 64 - height) / 2),
                width, height);
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
            return 30;
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
                new IsoBand(25, 3200),
                new IsoBand(50, 1600),
                new IsoBand(75, 800),
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
            page.SetBounds(0, 64, ClientSize.Width,
                Math.Max(1, ClientSize.Height - 92));
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
            Controls.Add(page);
            dashboard.Visible = false;
            settingsButton.Text = "Terug naar dashboard";
            settingsButton.AccessibleName = "Terug naar dashboard";
            page.BringToFront();
            page.Show();
            page.Focus();
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

        private static void AddLightDetailCaption(
            Panel panel, string text, int left, int top)
        {
            Label caption = MakeLabel(
                text, 9, FontStyle.Regular, Color.FromArgb(99, 108, 118));
            caption.SetBounds(left, top, 168, 28);
            panel.Controls.Add(caption);
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
        private DateTime updateCheckStartedAtUtc = DateTime.MinValue;

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
            grid.ColumnHeadersHeight = 36;
            grid.RowTemplate.Height = 36;
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

            BuildResponsiveSettingsLayout(
                title, stabilityCaption, previewCaption,
                previewLightCaption, cancelButton);

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
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));

            FlowLayoutPanel navigation = new FlowLayoutPanel();
            navigation.AutoSize = true;
            navigation.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            navigation.Dock = DockStyle.Fill;
            navigation.FlowDirection = FlowDirection.LeftToRight;
            navigation.WrapContents = true;
            navigation.Margin = Padding.Empty;

            title.AutoSize = true;
            title.Margin = new Padding(0, 5, 16, 4);
            isoSettingsTab.Size = new Size(118, 42);
            isoSettingsTab.Margin = new Padding(0, 0, 8, 4);
            coverSettingsTab.Size = new Size(156, 42);
            coverSettingsTab.Margin = new Padding(0, 0, 0, 4);
            navigation.Controls.Add(title);
            navigation.Controls.Add(isoSettingsTab);
            navigation.Controls.Add(coverSettingsTab);

            updateButton.AutoSize = true;
            updateButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            updateButton.MinimumSize = new Size(200, 42);
            updateButton.Padding = new Padding(12, 0, 12, 0);
            updateButton.Margin = new Padding(16, 0, 0, 4);

            boothAndVersionLabel.AutoEllipsis = true;
            boothAndVersionLabel.Dock = DockStyle.Fill;
            boothAndVersionLabel.Margin = Padding.Empty;
            boothAndVersionLabel.TextAlign = ContentAlignment.MiddleRight;

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
            profileLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

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

            explanation.Dock = DockStyle.Fill;
            explanation.AutoEllipsis = true;
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
            stabilitySecondsInput.Size = new Size(68, 28);
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
            previewLayout.Padding = new Padding(14, 6, 14, 8);
            previewLayout.ColumnCount = 4;
            previewLayout.RowCount = 2;
            previewLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            previewLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            previewLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            previewLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            previewLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            previewLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));

            previewCaption.AutoSize = true;
            previewCaption.Margin = new Padding(0, 6, 28, 0);
            previewLightCaption.AutoSize = true;
            previewLightCaption.Margin = new Padding(0, 7, 8, 0);
            previewLightInput.Size = new Size(70, 28);
            previewLightInput.Margin = new Padding(0, 3, 18, 0);
            previewText.Dock = DockStyle.Fill;
            previewText.AutoEllipsis = true;
            previewText.Margin = Padding.Empty;
            previewRange.Dock = DockStyle.Fill;
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
            footer.RowCount = 1;
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
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

            coverSettingsPanel.Dock = DockStyle.Fill;
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
                SetUpdateButtonState(
                    "Opnieuw proberen", Color.FromArgb(186, 36, 36),
                    true, exception.Message);
            }
        }

        private void RefreshUpdateStatus()
        {
            if (updateCheckStartedAtUtc != DateTime.MinValue &&
                DateTime.UtcNow - updateCheckStartedAtUtc >
                    TimeSpan.FromMinutes(2))
            {
                updateStatusTimer.Stop();
                SetUpdateButtonState(
                    "Opnieuw proberen", Color.FromArgb(186, 36, 36),
                    true, "De updatecontrole duurde te lang.");
                return;
            }
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

            string state;
            if (!values.TryGetValue("State", out state))
                return;
            string message;
            if (!values.TryGetValue("Message", out message))
                message = "";
            string available;
            if (!values.TryGetValue("AvailableVersion", out available))
                available = "";

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
                SetUpdateButtonState(
                    "Versie is actueel", Color.FromArgb(34, 132, 67),
                    true, message);
            }
            else if (String.Equals(state, "installed",
                StringComparison.OrdinalIgnoreCase))
            {
                updateStatusTimer.Stop();
                SetUpdateButtonState(
                    "Versie " + available + " geïnstalleerd",
                    Color.FromArgb(34, 132, 67), true, message);
            }
            else if (String.Equals(state, "error",
                StringComparison.OrdinalIgnoreCase))
            {
                updateStatusTimer.Stop();
                SetUpdateButtonState(
                    "Opnieuw proberen", Color.FromArgb(186, 36, 36),
                    true, message);
            }
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
            Text = "Hide fullscreen cover";
            Width = 300;
            Height = 64;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(
                workingArea.Right - Width - 24,
                workingArea.Top + 24);
            BackColor = Color.FromArgb(13, 17, 23);
            TopMost = true;
            ShowInTaskbar = false;

            Button hideButton = new Button();
            hideButton.Text = "HIDE FULLSCREEN COVER";
            hideButton.Dock = DockStyle.Fill;
            hideButton.Font = new Font("Segoe UI", 11, FontStyle.Bold);
            hideButton.ForeColor = Color.White;
            hideButton.BackColor = Color.FromArgb(187, 45, 59);
            hideButton.FlatStyle = FlatStyle.Flat;
            hideButton.FlatAppearance.BorderSize = 0;
            hideButton.Click += hideAction;
            Controls.Add(hideButton);
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
            ShowInTaskbar = false;
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
                const int WsExNoActivate = 0x08000000;
                CreateParams parameters = base.CreateParams;
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

            float horizontalMargin = Math.Max(
                36f, ClientSize.Width * 0.07f);
            float verticalMargin = Math.Max(
                32f, ClientSize.Height * 0.07f);
            float contentWidth = Math.Max(
                1f, ClientSize.Width - horizontalMargin * 2f);
            float contentHeight = Math.Max(
                1f, ClientSize.Height - verticalMargin * 2f);
            float gap = Math.Max(16f, contentHeight * 0.035f);
            float titleHeight = Math.Max(
                54f, (contentHeight - gap) * 0.34f);
            float messageHeight = Math.Max(
                54f, contentHeight - titleHeight - gap);
            RectangleF titleArea = new RectangleF(
                horizontalMargin, verticalMargin,
                contentWidth, titleHeight);
            RectangleF detailArea = new RectangleF(
                horizontalMargin, verticalMargin + titleHeight + gap,
                contentWidth, messageHeight);

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
            for (float size = maximumSize; size >= minimumSize; size -= 1f)
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
            return new Font(family, minimumSize, style);
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
            try
            {
                if (!SetProcessDpiAwarenessContext(new IntPtr(-4)))
                    SetProcessDPIAware();
            }
            catch (EntryPointNotFoundException)
            {
                SetProcessDPIAware();
            }
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
    }
}
