using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
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
        public int Raw = -1;
        public int TargetIso = -1;
        public DateTime CandidateSince = DateTime.MinValue;
        public string SerialStatus = "Searching for ESP...";
        public string LastSerialError = "";
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

    internal sealed class OverlayForm : Form
    {
        private const int HotkeyTogglePause = 1;
        private const int HotkeyEmergencyPause = 2;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const int WmHotkey = 0x0312;
        private const uint WmCommand = 0x0111;
        private const int DarkroomSettingsToolId = 0x0294;
        private const int DarkroomNextSettingsToolId = 0x0296;
        private const int DarkroomCameraSettingsCommand = 33082;
        private const int DarkroomToolbarControlId = 4083;
        private const int DarkroomCommandStartBooth = 0x83F0;
        private static readonly string BuildTag = BuildInfo.Version;
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
        private readonly System.Windows.Forms.Timer reconnectTimer;
        private readonly NotifyIcon trayIcon;
        private readonly Label modeLabel;
        private readonly Label lightLabel;
        private readonly Label rawLabel;
        private readonly Label targetIsoLabel;
        private readonly Label serialLabel;
        private readonly Label darkroomLabel;
        private readonly Label boothLabel;
        private readonly Label currentIsoLabel;
        private readonly Label countdownLabel;
        private readonly Label actionLabel;
        private readonly Panel progressFill;
        private readonly Button runActionButton;
        private readonly Button coverToggleButton;

        private SerialPort serial;
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
        private string currentDarkroomIso = "Unknown";
        private DateTime currentIsoReadAt = DateTime.MinValue;
        private bool darkroomRunning;
        private bool boothMode;
        private readonly string logPath;
        private readonly string positionPath;
        private readonly string sizePath;
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

        public OverlayForm()
        {
            Text = "JvdP Light → Darkroom Observer";
            Text = "JvdP Light to Darkroom";
            Width = 500;
            Height = 650;
            MinimumSize = new Size(450, 640);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = true;
            BackColor = Color.FromArgb(13, 17, 23);
            ForeColor = Color.White;
            Opacity = 1.0;
            DoubleBuffered = true;

            string localRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JvdP", "LightDarkroomOverlay");
            Directory.CreateDirectory(localRoot);
            logPath = Path.Combine(localRoot, "overlay.log");
            positionPath = Path.Combine(localRoot, "overlay-position.txt");
            sizePath = Path.Combine(localRoot, "overlay-size.txt");
            Size savedSize;
            if (TryLoadOverlaySize(out savedSize))
                Size = new Size(
                    Math.Max(savedSize.Width, 450),
                    Math.Max(savedSize.Height, 640));
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Point initialLocation = new Point(
                area.Right - Width - 18, area.Top + 18);
            Point savedLocation;
            if (TryLoadOverlayPosition(out savedLocation))
                initialLocation = savedLocation;
            Location = initialLocation;

            Label title = MakeLabel(
                "JvdP LIGHT", 18, FontStyle.Bold,
                Color.FromArgb(232, 238, 245));
            title.SetBounds(14, 7, 260, 48);
            title.Cursor = Cursors.SizeAll;
            title.MouseDown += BeginOverlayDrag;
            Controls.Add(title);
            MouseDown += BeginOverlayDrag;
            Move += delegate { SaveOverlayPosition(); };
            Resize += delegate { SaveOverlaySize(); };

            modeLabel = MakeLabel(
                "OBSERVE ONLY", 10, FontStyle.Bold, Color.Black);
            modeLabel.BackColor = Color.FromArgb(81, 216, 138);
            modeLabel.TextAlign = ContentAlignment.MiddleCenter;
            modeLabel.SetBounds(ClientSize.Width - 254, 14, 110, 34);
            modeLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Controls.Add(modeLabel);

            Button minimizeButton = MakeWindowButton(OverlayButtonIcon.Minimize);
            minimizeButton.AccessibleName = "Minimize";
            minimizeButton.SetBounds(ClientSize.Width - 92, 14, 34, 34);
            minimizeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            minimizeButton.Click += delegate
            {
                WindowState = FormWindowState.Minimized;
            };
            Controls.Add(minimizeButton);

            Button closeButton = MakeWindowButton(OverlayButtonIcon.Close);
            closeButton.AccessibleName = "Close";
            closeButton.SetBounds(ClientSize.Width - 50, 14, 34, 34);
            closeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            closeButton.Click += delegate { Close(); };
            Controls.Add(closeButton);
            minimizeButton.BringToFront();
            closeButton.BringToFront();

            Label lightCaption = MakeLabel(
                "LIVE LIGHT", 9, FontStyle.Bold, Color.FromArgb(139, 148, 158));
            lightCaption.SetBounds(20, 62, 150, 24);
            Controls.Add(lightCaption);

            lightLabel = MakeLabel(
                "—", 44, FontStyle.Bold, Color.FromArgb(247, 201, 72));
            lightLabel.SetBounds(18, 86, 200, 96);
            Controls.Add(lightLabel);

            rawLabel = MakeLabel(
                "Raw: —", 10, FontStyle.Regular, Color.FromArgb(139, 148, 158));
            rawLabel.SetBounds(20, 184, 180, 24);
            Controls.Add(rawLabel);

            Label targetCaption = MakeLabel(
                "TARGET ISO", 9, FontStyle.Bold, Color.FromArgb(139, 148, 158));
            targetCaption.SetBounds(ClientSize.Width - 192, 62, 150, 24);
            targetCaption.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Controls.Add(targetCaption);

            targetIsoLabel = MakeLabel(
                "—", 29, FontStyle.Bold, Color.FromArgb(121, 192, 255));
            targetIsoLabel.SetBounds(ClientSize.Width - 204, 86, 180, 96);
            targetIsoLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Controls.Add(targetIsoLabel);

            AddSeparator(214);
            serialLabel = AddStatusRow("ESP / SERIAL", 228);
            darkroomLabel = AddStatusRow("DARKROOM", 256);
            boothLabel = AddStatusRow("BOOTH MODE", 284);
            currentIsoLabel = AddStatusRow("CURRENT DRB ISO", 312);

            Label stabilityCaption = MakeLabel(
                "STABILITY", 9, FontStyle.Bold, Color.FromArgb(139, 148, 158));
            stabilityCaption.SetBounds(20, 346, 120, 24);
            Controls.Add(stabilityCaption);

            countdownLabel = MakeLabel(
                "0.0 / 5.0 s", 9, FontStyle.Bold, Color.FromArgb(201, 209, 217));
            countdownLabel.TextAlign = ContentAlignment.MiddleRight;
            countdownLabel.SetBounds(ClientSize.Width - 150, 346, 130, 24);
            countdownLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Controls.Add(countdownLabel);

            Panel progressBack = new Panel();
            progressBack.BackColor = Color.FromArgb(48, 54, 61);
            progressBack.SetBounds(20, 372, ClientSize.Width - 40, 7);
            progressBack.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(progressBack);

            progressFill = new Panel();
            progressFill.BackColor = Color.FromArgb(247, 201, 72);
            progressFill.SetBounds(0, 0, 0, 7);
            progressBack.Controls.Add(progressFill);

            actionLabel = MakeLabel(
                "Waiting for sensor data…", 10, FontStyle.Regular,
                Color.FromArgb(201, 209, 217));
            actionLabel.SetBounds(20, 390, ClientSize.Width - 40,
                ClientSize.Height - 390 - 250);
            actionLabel.Anchor = AnchorStyles.Top | AnchorStyles.Bottom |
                AnchorStyles.Left | AnchorStyles.Right;
            actionLabel.TextAlign = ContentAlignment.TopLeft;
            actionLabel.Padding = new Padding(4, 8, 4, 8);
            Controls.Add(actionLabel);

            Panel phonePanel = new Panel();
            phonePanel.BackColor = Color.FromArgb(22, 27, 34);
            phonePanel.SetBounds(20, ClientSize.Height - 242,
                ClientSize.Width - 40, 117);
            phonePanel.Anchor = AnchorStyles.Bottom |
                AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(phonePanel);

            Label phoneTitle = MakeLabel(
                "PHONE CONNECTION", 9, FontStyle.Bold,
                Color.FromArgb(232, 238, 245));
            phoneTitle.SetBounds(12, 5, 220, 23);
            phonePanel.Controls.Add(phoneTitle);

            Label phoneHint = MakeLabel(
                "Join Wi-Fi, then open the address", 8, FontStyle.Regular,
                Color.FromArgb(139, 148, 158));
            phoneHint.TextAlign = ContentAlignment.MiddleRight;
            phoneHint.SetBounds(220, 5, phonePanel.Width - 232, 23);
            phoneHint.Anchor = AnchorStyles.Top |
                AnchorStyles.Left | AnchorStyles.Right;
            phonePanel.Controls.Add(phoneHint);

            AddConnectionRow(phonePanel, "WI-FI", PhoneHotspotSsid, 30, 42);

            Label passwordCaption = MakeLabel(
                "PASSWORD", 8, FontStyle.Bold,
                Color.FromArgb(139, 148, 158));
            passwordCaption.SetBounds(12, 56, 100, 24);
            phonePanel.Controls.Add(passwordCaption);

            Label passwordValue = MakeLabel(
                new String('\u2022', 12), 9, FontStyle.Bold,
                Color.FromArgb(201, 209, 217));
            passwordValue.SetBounds(112, 56, phonePanel.Width - 274, 24);
            passwordValue.Anchor = AnchorStyles.Top |
                AnchorStyles.Left | AnchorStyles.Right;
            phonePanel.Controls.Add(passwordValue);

            Label eyeHint = MakeLabel(
                "Show password", 8, FontStyle.Regular,
                Color.FromArgb(139, 148, 158));
            eyeHint.TextAlign = ContentAlignment.MiddleRight;
            eyeHint.SetBounds(phonePanel.Width - 242, 54, 194, 28);
            eyeHint.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            phonePanel.Controls.Add(eyeHint);

            OverlayIconButton eyeButton =
                new OverlayIconButton(OverlayButtonIcon.Eye);
            eyeButton.AccessibleName = "Show password";
            eyeButton.SetBounds(phonePanel.Width - 42, 54, 30, 28);
            eyeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            bool passwordVisible = false;
            eyeButton.Click += delegate
            {
                passwordVisible = !passwordVisible;
                passwordValue.Text = passwordVisible
                    ? PhoneHotspotPassword
                    : new String('\u2022', 12);
                eyeButton.EyeSlashed = passwordVisible;
                eyeButton.AccessibleName = passwordVisible
                    ? "Hide password"
                    : "Show password";
                eyeHint.Text = passwordVisible
                    ? "Hide password"
                    : "Show password";
                eyeButton.Invalidate();
            };
            phonePanel.Controls.Add(eyeButton);

            AddConnectionRow(
                phonePanel, "OPEN", PhoneDashboardAddress, 84, 12);

            runActionButton = new Button();
            runActionButton.Text = "RUN ACTION";
            runActionButton.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            runActionButton.ForeColor = Color.White;
            runActionButton.BackColor = Color.FromArgb(31, 111, 235);
            runActionButton.FlatStyle = FlatStyle.Flat;
            runActionButton.FlatAppearance.BorderSize = 0;
            runActionButton.SetBounds(20, ClientSize.Height - 115,
                ClientSize.Width - 40, 42);
            runActionButton.Anchor = AnchorStyles.Bottom |
                AnchorStyles.Left | AnchorStyles.Right;
            runActionButton.Click += delegate { StartManualTargetIsoAction(); };
            Controls.Add(runActionButton);

            coverToggleButton = new Button();
            coverToggleButton.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            coverToggleButton.ForeColor = Color.White;
            coverToggleButton.FlatStyle = FlatStyle.Flat;
            coverToggleButton.FlatAppearance.BorderSize = 0;
            coverToggleButton.SetBounds(20, ClientSize.Height - 67,
                ClientSize.Width - 40, 42);
            coverToggleButton.Anchor = AnchorStyles.Bottom |
                AnchorStyles.Left | AnchorStyles.Right;
            coverToggleButton.Click += delegate { ToggleManualCover(); };
            Controls.Add(coverToggleButton);
            UpdateCoverToggleButton();

            Label resizeGrip = MakeLabel(
                "◢", 11, FontStyle.Bold, Color.FromArgb(139, 148, 158));
            resizeGrip.TextAlign = ContentAlignment.BottomRight;
            resizeGrip.SetBounds(
                ClientSize.Width - 18, ClientSize.Height - 18, 16, 16);
            resizeGrip.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            resizeGrip.Cursor = Cursors.SizeNWSE;
            resizeGrip.MouseDown += BeginOverlayResize;
            Controls.Add(resizeGrip);
            resizeGrip.BringToFront();

            trayIcon = new NotifyIcon();
            trayIcon.Icon = SystemIcons.Information;
            trayIcon.Text = "JvdP Light to Darkroom";
            trayIcon.Text = "JvdP Light → Darkroom Observer";
            trayIcon.Text = "JvdP Light to Darkroom";
            trayIcon.Visible = true;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Show / hide overlay", null, delegate { Visible = !Visible; });
            menu.Items.Add("Pause / observe", null, delegate { TogglePause(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit observer", null, delegate { Close(); });
            trayIcon.ContextMenuStrip = menu;
            trayIcon.DoubleClick += delegate { Visible = !Visible; };

            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 250;
            uiTimer.Tick += delegate { RefreshUi(); };
            uiTimer.Start();

            reconnectTimer = new System.Windows.Forms.Timer();
            reconnectTimer.Interval = 2000;
            reconnectTimer.Tick += delegate { EnsureSerialConnected(); };
            reconnectTimer.Start();

            coverWinEventDelegate = CoverWinEvent;
            coverGuardTimer = new System.Windows.Forms.Timer();
            coverGuardTimer.Interval = 15;
            coverGuardTimer.Tick += delegate { RaiseFullscreenCovers(); };

            Shown += delegate
            {
                RegisterHotKey(Handle, HotkeyTogglePause, ModControl | ModAlt, (uint)Keys.F9);
                RegisterHotKey(Handle, HotkeyEmergencyPause, ModControl | ModAlt, (uint)Keys.F12);
                EnsureSerialConnected();
                Log("Overlay started; build=" + BuildTag + "; manual Darkroom test action is available.");
            };

            FormClosing += delegate
            {
                StopCoverZOrderGuard();
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

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmHotkey)
            {
                int id = message.WParam.ToInt32();
                if (id == HotkeyTogglePause)
                    TogglePause();
                else if (id == HotkeyEmergencyPause)
                    SetPaused(true, "Emergency pause hotkey pressed.");
            }
            base.WndProc(ref message);
        }

        private void BeginOverlayDrag(
            object sender, MouseEventArgs args)
        {
            if (args.Button != MouseButtons.Left)
                return;
            ReleaseCapture();
            SendMessage(
                Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
            SaveOverlayPosition();
        }

        private void BeginOverlayResize(
            object sender, MouseEventArgs args)
        {
            if (args.Button != MouseButtons.Left)
                return;
            ReleaseCapture();
            SendMessage(
                Handle, 0x00A1, new IntPtr(17), IntPtr.Zero);
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
                width = Math.Max(450, Math.Min(1200, width));
                height = Math.Max(530, Math.Min(1200, height));
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

        private static Button MakeWindowButton(OverlayButtonIcon icon)
        {
            return new OverlayIconButton(icon);
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
            right.SetBounds(200, top, ClientSize.Width - 220, 28);
            right.Anchor = AnchorStyles.Top | AnchorStyles.Left |
                AnchorStyles.Right;
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
            SetPaused(!paused, paused ? "Observer resumed." : "Observer paused.");
        }

        private void SetPaused(bool value, string message)
        {
            paused = value;
            Log(message);
            RefreshUi();
        }

        private void StartManualTargetIsoAction()
        {
            if (manualActionRunning)
                return;
            manualActionRunning = true;
            manualActionFailed = false;
            manualActionStatus = "Starting manual Darkroom test...";
            manualActionStatusUntil = DateTime.MaxValue;
            runActionButton.Enabled = false;
            runActionButton.Text = "RUNNING...";
            coverToggleButton.Enabled = false;
            Log("Manual ESP Target ISO action requested.");
            Thread worker = new Thread(RunManualTargetIsoAction);
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        private void RunManualTargetIsoAction()
        {
            Process process = null;
            string finalStatus;
            bool failed = false;
            bool coverShown = false;
            bool keepCoverVisible = manualCoverVisible;
            try
            {
                int desiredIso;
                lock (sensor.Sync)
                    desiredIso = sensor.TargetIso;
                if (desiredIso <= 0)
                    throw new InvalidOperationException(
                        "No valid Target ISO has been received from the ESP.");
                Log("Locked ESP Target ISO " + desiredIso +
                    " for this RUN ACTION before Darkroom navigation.");
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
                Log("ISO state: ESP target=" + desiredIso +
                    ", Darkroom current=" + observedIso +
                    ", available choices=" + items.Count + ".");
                ComboItem target = FindClosestNumericIso(items, desiredIso);
                if (target == null)
                    throw new InvalidOperationException(
                        "Darkroom does not expose any numeric ISO values.");
                if (!String.Equals(
                        target.Value, desiredIso.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                    Log("ESP Target ISO " + desiredIso +
                        " is unavailable; using nearest Darkroom ISO " +
                        target.Value + ".");

                long currentIndex = SendMessageWithTimeout(
                    combo, 0x0147, IntPtr.Zero, IntPtr.Zero).ToInt64();
                if (currentIndex == target.Index)
                {
                    currentDarkroomIso = target.Value;
                    currentIsoReadAt = DateTime.Now;
                    finalStatus = "No change - Darkroom is already at ESP Target ISO " +
                                  target.Value + ".";
                    Log(finalStatus);
                }
                else
                {
                    SetManualActionStatus(
                        "Step 2/3 - selecting ESP Target ISO " +
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
                            "Darkroom did not confirm ESP Target ISO " +
                            target.Value + ".");
                    currentDarkroomIso = confirmed;
                    currentIsoReadAt = DateTime.Now;
                    finalStatus = "Done - Darkroom ISO now matches ESP Target ISO " +
                                  confirmed + ".";
                    Log(finalStatus);
                }

                StartBoothModeAfterAction(window);
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
            manualActionRunning = false;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    runActionButton.Enabled = true;
                    runActionButton.Text = "RUN ACTION";
                    coverToggleButton.Enabled = true;
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
                ? "HIDE FULLSCREEN COVER"
                : "SHOW FULLSCREEN COVER";
            coverToggleButton.BackColor = manualCoverVisible
                ? Color.FromArgb(187, 45, 59)
                : Color.FromArgb(35, 134, 54);
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
                        new ActionCoverForm(screen.Bounds);
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
            if (serial != null && serial.IsOpen)
            {
                if (lastJvdpLineAt < serialOpenedAt &&
                    DateTime.Now - serialOpenedAt > TimeSpan.FromSeconds(5))
                {
                    string rejectedPort = activeSerialPort;
                    lock (serialRetryAfter)
                        serialRetryAfter[rejectedPort] =
                            DateTime.Now.AddSeconds(20);
                    Log("Rejected " + rejectedPort +
                        ": no JVDP sensor data received within 5 seconds.");
                    lock (sensor.Sync)
                        sensor.SerialStatus = rejectedPort + " is not the ESP";
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
                    serial = candidate;
                    activeSerialPort = portName;
                    serialOpenedAt = DateTime.Now;
                    lastJvdpLineAt = DateTime.MinValue;
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
            SerialPort old = serial;
            serial = null;
            activeSerialPort = "";
            serialOpenedAt = DateTime.MinValue;
            lastJvdpLineAt = DateTime.MinValue;
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
                SerialPort active = serial;
                if (active == null || !active.IsOpen)
                    return;

                while (active.BytesToRead > 0)
                {
                    string line = active.ReadLine().Trim();
                    Match rawMatch = Regex.Match(
                        line, @"^Raw:\s*(\d+)\s*\|\s*Light:");
                    if (rawMatch.Success)
                    {
                        lock (sensor.Sync)
                            sensor.Raw = Int32.Parse(rawMatch.Groups[1].Value);
                    }

                    Match machineMatch = Regex.Match(
                        line, @"^JVDP\|light=(\d{1,3})\|iso=(\d+)$");
                    if (!machineMatch.Success)
                        continue;

                    int light = Int32.Parse(machineMatch.Groups[1].Value);
                    int iso = Int32.Parse(machineMatch.Groups[2].Value);
                    lock (sensor.Sync)
                    {
                        lastJvdpLineAt = DateTime.Now;
                        if (sensor.TargetIso != iso)
                        {
                            sensor.TargetIso = iso;
                            sensor.CandidateSince = DateTime.Now;
                            Log(string.Format(
                                "New target ISO {0} at light {1}.", iso, light));
                        }
                        sensor.Light = light;
                        sensor.SerialStatus = active.PortName + " connected";
                    }
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
                CloseSerial();
            }
        }

        private void RefreshUi()
        {
            UpdateDarkroomState();

            int light;
            int raw;
            int targetIso;
            DateTime candidateSince;
            string serialStatus;
            lock (sensor.Sync)
            {
                light = sensor.Light;
                raw = sensor.Raw;
                targetIso = sensor.TargetIso;
                candidateSince = sensor.CandidateSince;
                serialStatus = sensor.SerialStatus;
            }

            modeLabel.Text = manualActionRunning
                ? "RUNNING"
                : (paused ? "PAUSED" : "MANUAL");
            modeLabel.BackColor = manualActionRunning
                ? Color.FromArgb(247, 201, 72)
                : (paused
                    ? Color.FromArgb(248, 81, 73)
                    : Color.FromArgb(81, 216, 138));

            lightLabel.Text = light >= 0 ? light.ToString() : "—";
            rawLabel.Text = raw >= 0 ? "Raw: " + raw : "Raw: —";
            targetIsoLabel.Text = targetIso > 0 ? targetIso.ToString() : "—";

            SetStatusLabel(
                serialLabel, serialStatus,
                serial != null && serial.IsOpen);
            SetStatusLabel(
                darkroomLabel,
                darkroomRunning ? "Running" : "Not running",
                darkroomRunning);
            SetStatusLabel(
                boothLabel,
                boothMode ? "Active" : "Inactive",
                boothMode);

            string isoText = currentDarkroomIso;
            if (currentIsoReadAt != DateTime.MinValue)
                isoText += "  ·  verified";
            SetStatusLabel(
                currentIsoLabel,
                isoText,
                currentDarkroomIso != "Unknown");

            double stableSeconds = 0.0;
            if (candidateSince != DateTime.MinValue)
                stableSeconds = Math.Min(5.0, (DateTime.Now - candidateSince).TotalSeconds);
            countdownLabel.Text = string.Format("{0:0.0} / 5.0 s", stableSeconds);
            progressFill.Width = (int)Math.Round(
                progressFill.Parent.ClientSize.Width *
                stableSeconds / 5.0);
            progressFill.BackColor = stableSeconds >= 5.0
                ? Color.FromArgb(81, 216, 138)
                : Color.FromArgb(247, 201, 72);

            bool showManualStatus = manualActionRunning || DateTime.Now < manualActionStatusUntil;
            actionLabel.Text = showManualStatus
                ? manualActionStatus
                : BuildActionText(targetIso, stableSeconds, serial != null && serial.IsOpen);
            actionLabel.ForeColor = showManualStatus && manualActionFailed
                ? Color.FromArgb(248, 81, 73)
                : (manualActionRunning
                    ? Color.FromArgb(247, 201, 72)
                    : Color.FromArgb(201, 209, 217));
        }

        private void SetStatusLabel(Label label, string text, bool good)
        {
            label.Text = text;
            label.ForeColor = good
                ? Color.FromArgb(81, 216, 138)
                : Color.FromArgb(248, 81, 73);
        }

        private string BuildActionText(int targetIso, double stableSeconds, bool serialReady)
        {
            if (paused)
                return "Paused — no Darkroom actions are possible.";
            if (!serialReady || targetIso <= 0)
                return "Waiting for ESP sensor data…";
            if (!darkroomRunning)
                return "Would wait: Darkroom Booth is not running.";
            if (!boothMode)
                return "Would wait: Booth mode is not active.";
            if (stableSeconds < 5.0)
                return string.Format(
                    "Would wait {0:0.0}s for a stable target.", 5.0 - stableSeconds);
            if (currentDarkroomIso == "Unknown")
                return string.Format(
                    "Would verify current Darkroom ISO, target {0}.", targetIso);
            if (String.Equals(currentDarkroomIso, targetIso.ToString(),
                              StringComparison.OrdinalIgnoreCase))
                return string.Format(
                    "No change needed — Darkroom is already ISO {0}.", targetIso);
            return string.Format(
                "Would change Darkroom ISO {0} → {1}.",
                currentDarkroomIso, targetIso);
        }

        private void UpdateDarkroomState()
        {
            Process process = null;
            try
            {
                Process[] candidates = Process.GetProcessesByName("DarkroomBooth");
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
                darkroomRunning = false;
                boothMode = false;
                return;
            }

            try
            {
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
                darkroomRunning = false;
                boothMode = false;
            }
            finally
            {
                process.Dispose();
            }
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
                File.AppendAllText(
                    logPath,
                    string.Format(
                        "{0:yyyy-MM-dd HH:mm:ss.fff}  {1}{2}",
                        DateTime.Now, message, Environment.NewLine));
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
        internal ActionCoverForm(Rectangle bounds)
        {
            Text = "JvdP Camera Screen";
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

            using (Font titleFont = new Font(
                "Britannic Bold", 46, FontStyle.Regular))
            using (Font detailFont = new Font(
                "Britannic Bold", 27, FontStyle.Regular))
            using (Brush titleBrush = new SolidBrush(
                Color.FromArgb(31, 38, 58)))
            using (Brush detailBrush = new SolidBrush(
                Color.FromArgb(73, 82, 105)))
            using (StringFormat centered = new StringFormat())
            {
                centered.Alignment = StringAlignment.Center;
                centered.LineAlignment = StringAlignment.Center;

                string title = "Please wait!";
                string detail =
                    "I'm fine-tuning the lighting" + Environment.NewLine +
                    "so you look absolutely amazing!";

                float blockHeight = 230f;
                float blockTop = (ClientSize.Height - blockHeight) / 2f;
                RectangleF titleArea = new RectangleF(
                    50f, blockTop,
                    Math.Max(0f, ClientSize.Width - 100f), 82f);
                RectangleF detailArea = new RectangleF(
                    50f, blockTop + 90f,
                    Math.Max(0f, ClientSize.Width - 100f), 132f);

                args.Graphics.DrawString(
                    title, titleFont, titleBrush,
                    titleArea, centered);
                args.Graphics.DrawString(
                    detail, detailFont, detailBrush,
                    detailArea, centered);
            }
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
        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(
            IntPtr dpiContext);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main()
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
            Application.Run(new OverlayForm());
        }
    }
}
