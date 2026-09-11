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
using Jvdp.Reliability;

namespace Jvdp.LightDarkroomOverlay
{
    internal sealed partial class OverlayForm : Form
    {
        internal const float MinimumReadableUiFontSize = 8.5f;
        private const int HotkeyTogglePause = 1;
        private const int HotkeyEmergencyPause = 2;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const int WmHotkey = 0x0312;
        private const int WmActivateExistingInstance = 0x8001;
        private const int DarkroomActionDeadlineMilliseconds = 30000;
        private static readonly string BuildTag = BuildInfo.Version;
        private const string DefaultCoverTitle = "Please wait!";
        private const string DefaultCoverMessage =
            "I'm fine-tuning the lighting\r\nso you look absolutely amazing!";
        private const int DefaultStabilitySeconds = 60;
        private const uint EventSystemForeground = 0x0003;
        private const uint EventSystemPopupStart = 0x0006;
        private const uint EventObjectShow = 0x8002;
        private const uint WinEventOutOfContext = 0x0000;
        private const string PhoneDashboardAddress = "http://192.168.9.1";

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
        private int darkroomProbeActive;
        private int uiRefreshActive;
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

        internal static readonly int[] SupportedIsoValues = {
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
            InitializeReliability(localRoot);
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
                if (stabilitySeconds == (int)stabilitySecondsInput.Value) return;
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
            Button reliabilityButton = MakeLightButton("Kalibratie en diagnose", false);
            reliabilityButton.AutoSize = true; reliabilityButton.MinimumSize = new Size(1, 44);
            reliabilityButton.Dock = DockStyle.Fill;
            reliabilityButton.Click += delegate { ShowReliabilityTools(); };
            detailsLayout.RowCount = 7;
            detailsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            detailsLayout.Controls.Add(reliabilityButton, 0, 6);
            detailsLayout.SetColumnSpan(reliabilityButton, 2);
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
            menu.Items.Add("Kalibratie en diagnose", null, delegate { ShowReliabilityTools(); });
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
                WriteStartupHealth();
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
                if (panel.Width != width)
                    panel.Width = width;

                int frameWidth = Math.Max(0,
                    panel.Width - panel.ClientSize.Width);
                int frameHeight = Math.Max(0,
                    panel.Height - panel.ClientSize.Height);
                int contentWidth = Math.Max(1,
                    width - frameWidth - panel.Padding.Horizontal);

                Size contentMinimum = new Size(contentWidth, 0);
                Size contentMaximum = new Size(contentWidth, 0);
                if (content.MinimumSize != contentMinimum)
                    content.MinimumSize = contentMinimum;
                if (content.MaximumSize != contentMaximum)
                    content.MaximumSize = contentMaximum;
                if (content.Width != contentWidth)
                    content.Width = contentWidth;
                content.PerformLayout();

                Size preferred = content.GetPreferredSize(
                    new Size(contentWidth, 0));
                int contentHeight = Math.Max(1, preferred.Height);
                if (content.Height != contentHeight)
                    content.Height = contentHeight;
                int panelHeight = contentHeight +
                    panel.Padding.Vertical + frameHeight;

                Size panelMinimum = new Size(width, panelHeight);
                Size panelMaximum = new Size(width, 0);
                Size panelSize = new Size(width, panelHeight);
                if (panel.MinimumSize != panelMinimum)
                    panel.MinimumSize = panelMinimum;
                if (panel.MaximumSize != panelMaximum)
                    panel.MaximumSize = panelMaximum;
                if (panel.Size != panelSize)
                    panel.Size = panelSize;
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
            if (manualActionRunning)
            {
                manualActionStatus = "De app sluit pas nadat de ISO-aanpassing klaar is. Probeer daarna opnieuw.";
                ShowMainWindow();
                return;
            }
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
            if (message.Msg == BoothCoordination.ShutdownForUpdate)
            {
                // The installer owns the operation lease. Only our own active
                // ISO action can delay shutdown; Darkroom stays untouched.
                if (!manualActionRunning) ExitApplication();
                return;
            }
            if (message.Msg == 0x0219) ResetSerialBackoff();
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

        internal bool VerifyTouchProfileEditingForLayoutTest()
        {
            if (!layoutTestMode)
                throw new InvalidOperationException(
                    "Layout test hooks are only available in layout test mode.");
            return settingsPage != null &&
                settingsPage.VerifyTouchProfileEditingForLayoutTest();
        }

        internal bool VerifyStableDashboardRefreshForLayoutTest()
        {
            if (!layoutTestMode)
                throw new InvalidOperationException(
                    "Layout test hooks are only available in layout test mode.");

            lock (sensor.Sync)
            {
                sensor.Light = 63;
                sensor.MappedIso = 1000;
                sensor.CandidateSince = DateTime.MinValue;
                sensor.SerialStatus = "COM123 connected";
            }
            lastJvdpLineAt = DateTime.Now;
            RefreshUi();
            PerformLayout();
            dashboard.PerformLayout();

            Rectangle mappingBefore = mappingPanel.Bounds;
            Rectangle mappingContentBefore = mappingLayout.Bounds;
            Rectangle metricsBefore = metrics.Bounds;
            Rectangle profileBefore = profilePanel.Bounds;
            Rectangle detailsBefore = detailsHeader.Bounds;
            Rectangle actionBefore = actionPanel.Bounds;
            int rangeChangesBefore =
                rangeControl.DataChangeCountForLayoutTest;

            for (int pass = 0; pass < 8; pass++)
                RefreshUi();

            return mappingPanel.Bounds == mappingBefore &&
                mappingLayout.Bounds == mappingContentBefore &&
                metrics.Bounds == metricsBefore &&
                profilePanel.Bounds == profileBefore &&
                detailsHeader.Bounds == detailsBefore &&
                actionPanel.Bounds == actionBefore &&
                rangeControl.DataChangeCountForLayoutTest ==
                    rangeChangesBefore;
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
            bool old = paused;
            paused = value;
            try { SaveAutomaticAdjustmentState(); }
            catch (Exception error) { paused = old; MessageBox.Show(this, "De instelling is niet opgeslagen: " + error.Message, "Opslaan mislukt"); return; }
            Log(message);
            RefreshUi();
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

        private void LogActionTiming(
            Stopwatch timer, ref long previousStepMilliseconds,
            string step)
        {
            long elapsed = timer.ElapsedMilliseconds;
            Log("Darkroom timing: " + step + "=" +
                (elapsed - previousStepMilliseconds) +
                " ms; total=" + elapsed + " ms.");
            previousStepMilliseconds = elapsed;
        }

        private static int GetRemainingActionMilliseconds(
            DateTime actionDeadlineUtc)
        {
            int remaining = (int)Math.Ceiling(
                (actionDeadlineUtc - DateTime.UtcNow).TotalMilliseconds);
            if (remaining <= 0)
                throw new TimeoutException(
                    "Darkroom reageerde niet binnen de maximale actietijd.");
            return remaining;
        }

        private static void SleepWithinActionDeadline(
            DateTime actionDeadlineUtc, int milliseconds)
        {
            int remaining = GetRemainingActionMilliseconds(actionDeadlineUtc);
            Thread.Sleep(Math.Min(milliseconds, remaining));
            GetRemainingActionMilliseconds(actionDeadlineUtc);
        }

        private static bool WaitForCondition(
            Func<bool> condition, int timeoutMilliseconds)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(
                Math.Max(1, timeoutMilliseconds));
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (condition())
                        return true;
                }
                catch { }
                Thread.Sleep(50);
            }
            try { return condition(); }
            catch { return false; }
        }

        private static Process FindDarkroomProcess()
        {
            List<Process> candidates = new List<Process>();
            int currentSession = Process.GetCurrentProcess().SessionId;
            foreach (Process candidate in Process.GetProcessesByName("DarkroomBooth"))
            {
                try { if (candidate.SessionId == currentSession) { candidates.Add(candidate); continue; } }
                catch { }
                candidate.Dispose();
            }
            if (candidates.Count == 1) return candidates[0];
            foreach (Process candidate in candidates) candidate.Dispose();
            return null;
        }


        private static List<IntPtr> FindTopLevelProcessWindows(int processId)
        {
            List<IntPtr> windows = new List<IntPtr>();
            EnumWindowProc collect = delegate(IntPtr handle, IntPtr parameter)
            {
                uint ownerProcess;
                GetWindowThreadProcessId(handle, out ownerProcess);
                if (ownerProcess == (uint)processId)
                    windows.Add(handle);
                return true;
            };
            EnumWindows(collect, IntPtr.Zero);
            GC.KeepAlive(collect);
            return windows;
        }
        private void LogDarkroomWindowState(
            Process process, IntPtr window)
        {
            try
            {
                NativeMethods.Rect rect;
                if (!GetWindowRect(window, out rect))
                    throw new InvalidOperationException(
                        "Darkroom window bounds are unavailable.");
                Log("Darkroom state: pid=" + process.Id +
                    ", title='" + process.MainWindowTitle +
                    "', native rect=(" + rect.Left + "," +
                    rect.Top + "," + (rect.Right - rect.Left) + "x" +
                    (rect.Bottom - rect.Top) + ").");
            }
            catch (Exception exception)
            {
                Log("Darkroom state logging failed: " +
                    exception.Message);
            }
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
            List<ComboItem> items, int requestedIso, int maximumIso)
        {
            ComboItem closest = null;
            double closestDistance = Double.MaxValue;
            foreach (ComboItem item in items)
            {
                int numericIso;
                if (!Int32.TryParse(item.Value.Trim(), out numericIso) ||
                    numericIso <= 0 || numericIso > maximumIso)
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

        private ComboItem FindTargetComboItem(
            IntPtr combo, int requestedIso)
        {
            string requested = requestedIso.ToString(
                CultureInfo.InvariantCulture);
            int exactIndex = (int)SendMessageWithTimeout(
                combo, 0x0158, new IntPtr(-1), requested).ToInt64();
            if (exactIndex >= 0)
                return new ComboItem(exactIndex, requested);

            // A camera can expose a reduced ISO list. Only enumerate the full
            // dropdown in that exceptional case to preserve nearest-value
            // behaviour without making every normal action pay that cost.
            return FindClosestNumericIso(
                ReadComboItems(combo), requestedIso, lightRegulation.MaximumIso);
        }

        private void RefreshUi()
        {
            // Timer messages, control layout and failed action starts can reenter
            // the message loop. Never recursively render/start actions on the stack.
            if (Interlocked.Exchange(ref uiRefreshActive, 1) != 0)
                return;
            try { RefreshUiCore(); }
            finally { Interlocked.Exchange(ref uiRefreshActive, 0); }
        }

        private void RefreshUiCore()
        {
            if (!layoutTestMode && serialReady && !SensorIsFresh())
            {
                serialReady = false;
                lock (sensor.Sync) { sensor.CheckCycle.Invalidate(); lightRegulation.Reset(); }
                Interlocked.Exchange(ref serialFaulted, 1);
            }
            if (maintenanceMode && !BoothCoordination.HasMaintenance(localDirectory, DateTime.UtcNow)) maintenanceMode = false;
            int light;
            int targetIso;
            DateTime candidateSince;
            string serialStatus;
            DateTime lastMeasurement;
            double stableSeconds;
            lock (sensor.Sync)
            {
                light = sensor.Light;
                targetIso = sensor.MappedIso;
                candidateSince = sensor.CandidateSince;
                serialStatus = sensor.SerialStatus;
                lastMeasurement = lastJvdpLineAt;
                stableSeconds = sensor.CheckCycle.ElapsedSeconds(stabilitySeconds);
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

            bool sensorConnected = serialReady && SensorIsFresh();
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
                isoText += " · " + currentIsoReadAt.ToString("HH:mm:ss");
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

            TryStartAutomaticAction(targetIso, stableSeconds, sensorConnected, candidateSince);
            // A completed check may have started a fresh window. Render its
            // current countdown now, not a stale 60/60 until the next UI tick.
            lock (sensor.Sync)
                stableSeconds = sensor.CheckCycle.ElapsedSeconds(stabilitySeconds);
            countdownLabel.Text = string.Format(
                "{0:0.0} / {1:0.0} sec", stableSeconds, stabilitySeconds);
            progressFill.Width = (int)Math.Round(
                progressFill.Parent.ClientSize.Width *
                stableSeconds / stabilitySeconds);
            progressFill.BackColor = stableSeconds >= stabilitySeconds
                ? Color.FromArgb(34, 132, 67)
                : Color.FromArgb(31, 111, 235);

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
                overallStatusLabel.Text = "Controleert licht";
                overallStatusLabel.ForeColor = Color.FromArgb(34, 132, 67);
                modeLabel.Text = "Automatisch actief";
            }
            modeLabel.ForeColor = paused
                ? Color.FromArgb(137, 87, 0)
                : Color.FromArgb(20, 102, 196);
            if (automaticFault != null) { overallStatusLabel.Text = "Lichtregeling gestopt na storing"; overallStatusLabel.ForeColor = Color.Firebrick; }
            else if (maintenanceMode) overallStatusLabel.Text = "Onderhoud";
            else if (GetActionBlockReason(true) != null && sensorConnected && darkroomRunning && !paused)
                overallStatusLabel.Text = "Wacht op een vrij moment";
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

            string waitingReason = GetActionBlockReason(true);
            bool showActionStatus =
                manualActionRunning || DateTime.Now < manualActionStatusUntil;
            // After a successful adjustment show the new countdown immediately.
            // Keep errors and a manual result while automatic mode is stopped.
            if (!manualActionRunning && !manualActionFailed && !paused)
                showActionStatus = false;
            actionLabel.Text = showActionStatus
                ? manualActionStatus
                : (waitingReason ?? BuildActionText(targetIso, stableSeconds, sensorConnected));
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

            if (automaticFault != null) statusPanelMessage.Text = automaticFault;
            else if (manualActionRunning)
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
                    "Bij het opstarten wordt Booth Mode na stabiele metingen automatisch voorbereid.";
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
            int targetIso, double stableSeconds, bool serialReady, DateTime candidateSince)
        {
            if (GetActionBlockReason(true) != null || paused || manualActionRunning || !serialReady ||
                targetIso <= 0 || !darkroomRunning ||
                stableSeconds < stabilitySeconds ||
                DateTime.Now < nextAutomaticAttemptAt)
                return;

            if (IsTargetIsoAlreadySet(targetIso) && (initialPreparationDone || boothMode))
            {
                RestartLightCheck(targetIso, candidateSince);
                return;
            }

            StartTargetIsoAction(true);
        }

        private bool IsTargetIsoAlreadySet(int targetIso)
        {
            return isoConfirmation.Matches(darkroomProcessId, targetIso);
        }

        private void RestartLightCheck(int checkedTarget, DateTime checkedSince)
        {
            lock (sensor.Sync)
                sensor.CheckCycle.CompleteCheck(checkedTarget, checkedSince, DateTime.Now);
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
            if (IsTargetIsoAlreadySet(targetIso))
                return string.Format(
                    "Lichtbewaking actief · ISO {0} ingesteld. Volgende lichtcontrole over {1:0} seconden.",
                    currentDarkroomIso == "Unknown" ? targetIso.ToString() : currentDarkroomIso,
                    Math.Max(0, stabilitySeconds - stableSeconds));
            if (stableSeconds < stabilitySeconds)
                return string.Format(
                    "Doel-ISO wordt nog {0:0.0} seconden gecontroleerd.",
                    stabilitySeconds - stableSeconds);
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
                process = FindDarkroomProcess();
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
                    isoConfirmation.Clear();
                    initialPreparationDone = false;
                    lastAppliedTargetIso = 0;
                    lastAppliedDarkroomProcessId = 0;
                    nextAutomaticAttemptAt = DateTime.MinValue;
                    Log("Darkroom Booth detected; process=" + processId +
                        "; ISO will be verified automatically.");
                }

                darkroomRunning = true;
                darkroomIdentity = BoothCoordination.ProcessIdentity(process);
                darkroomVersion = process.MainModule.FileVersionInfo.FileVersion;
                IntPtr window = NativeDarkroomNavigation.FindEditor(processId);
                bool previousBoothMode = boothMode;
                boothMode = NativeDarkroomNavigation.FindBoothWindow(
                    processId, window) != IntPtr.Zero;
                if (previousBoothMode && !boothMode && !manualActionRunning) isoConfirmation.Clear();

                IntPtr isoCombo = manualActionRunning ? IntPtr.Zero : NativeDarkroomNavigation.FindVisibleIsoControl(window);
                if (isoCombo != IntPtr.Zero && !NativeDarkroomNavigation.IsSelectionOpen(isoCombo))
                {
                    string value = ReadComboSelection(isoCombo);
                    if (!manualActionRunning && !String.IsNullOrWhiteSpace(value))
                    {
                        currentDarkroomIso = value;
                        currentIsoReadAt = DateTime.Now;
                        isoConfirmation.Observe(processId, value);
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
            darkroomIdentity = "";
            isoConfirmation.Clear();
            initialPreparationDone = false;
            boothMode = false;
            darkroomProcessId = 0;
            currentDarkroomIso = "Unknown";
            currentIsoReadAt = DateTime.MinValue;
            lastAppliedTargetIso = 0;
            lastAppliedDarkroomProcessId = 0;
            nextAutomaticAttemptAt = DateTime.MinValue;
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
            if (controlId == 107)
                return NativeDarkroomNavigation.FindVisibleIsoControl(root);
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
            return NativeDarkroomNavigation.ReadSelection(combo);
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

        private static IntPtr SendMessageWithTimeout(
            IntPtr handle, uint message, IntPtr wParam, string lParam)
        {
            IntPtr result;
            IntPtr completed = SendMessageTimeout(
                handle, message, wParam, lParam,
                0x0001 | 0x0002, 2000, out result);
            if (completed == IntPtr.Zero)
                throw new TimeoutException(
                    "Darkroom did not search the ISO dropdown within two seconds.");
            return result;
        }

        private void Log(string message)
        {
            try
            {
                lock (logSync)
                {
                    ReliableFiles.AppendLog(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message);
                }
            }
            catch { }
        }


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

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr handle, uint message, IntPtr wParam, string lParam,
            uint flags, uint timeoutMilliseconds, out IntPtr result);

        [DllImport("oleacc.dll")]
        private static extern int ObjectFromLresult(
            IntPtr result, ref Guid interfaceId, IntPtr wParam,
            [MarshalAs(UnmanagedType.Interface)] out object value);


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


}
