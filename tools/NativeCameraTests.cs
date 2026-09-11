using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Jvdp.LightDarkroomOverlay;

internal static class NativeCameraTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int ex, string cls, string text, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr id, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, uint msg, IntPtr w, string text);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")]
    private static extern uint InSendMessageEx(IntPtr reserved);

    private sealed class QueueCheckedCombo : NativeWindow
    {
        internal string Root;
        protected override void WndProc(ref Message message)
        {
            if ((message.Msg == 0x014f || message.Msg == 0x0100 || message.Msg == 0x0101) && InSendMessageEx(IntPtr.Zero) != 0)
                File.AppendAllText(Path.Combine(Root, "sent-mutations.txt"), message.Msg.ToString("X") + "\n");
            base.WndProc(ref message);
        }
    }

    private sealed class CameraPanel : Panel
    {
        internal Action Commit;
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0111 && (m.WParam.ToInt64() & 65535) == 107 &&
                ((m.WParam.ToInt64() >> 16) & 65535) == 9 && Commit != null) Commit();
            base.WndProc(ref m);
        }
    }
    private sealed class Fixture : Form, IMessageFilter
    {
        private readonly string root;
        private readonly bool ignoreCommit;
        private readonly int delay;
        private readonly CameraPanel camera = new CameraPanel();
        private readonly Panel other = new Panel();
        private IntPtr iso;
        private string persisted = "800";
        private bool settings;
        private int page = 1;
        private readonly IntPtr[] pageMarkers = new IntPtr[11];
        private readonly QueueCheckedCombo checkedCombo = new QueueCheckedCombo();
        internal Fixture(string root, bool ignoreCommit, bool legacy)
        {
            this.root = root; this.ignoreCommit = ignoreCommit; delay = ignoreCommit ? 0 : 450;
            Text = legacy ? "Bruiloft - zaterdag" : "Darkroom isolated native test"; ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.FixedSingle; StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(SystemInformation.VirtualScreen.Right + 1024, SystemInformation.VirtualScreen.Top, 500, 350);
            foreach (Screen screen in Screen.AllScreens)
                if (Bounds.IntersectsWith(screen.Bounds)) throw new Exception("Test window must stay outside every display.");
            camera.Bounds = other.Bounds = new Rectangle(0, 40, 450, 260);
            Controls.Add(camera); Controls.Add(other);
            if (!legacy) CreateWindowEx(0, "STATIC", "toolbar", 0x50000000, 0, 0, 50, 20, Handle, new IntPtr(4083), IntPtr.Zero, IntPtr.Zero);
            if (!legacy)
            {
                // A second top-level toolbar in the same application used to
                // make FindEditor reject an otherwise usable editor as ambiguous.
                IntPtr palette = CreateWindowEx(0x08000080, "STATIC", "Owned toolbar", unchecked((int)0x90c00000),
                    Bounds.Left, Bounds.Top + 400, 100, 80, Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                CreateWindowEx(0, "STATIC", "toolbar", 0x50000000, 0, 0, 50, 20, palette, new IntPtr(4083), IntPtr.Zero, IntPtr.Zero);
            }
            foreach (int id in legacy ? new int[0] : new[] { 104, 105, 106 })
                CreateWindowEx(0, "STATIC", "Camera parameter", 0x50000000, 5, (id - 104) * 24, 150, 22, camera.Handle, new IntPtr(id), IntPtr.Zero, IntPtr.Zero);
            if (legacy) CreateWindowEx(0, "STATIC", "ISO:", 0x50000000, 5, 65, 100, 22, camera.Handle, new IntPtr(501), IntPtr.Zero, IntPtr.Zero);
            iso = Combo(camera.Handle);
            checkedCombo.Root = root; checkedCombo.AssignHandle(iso);
            Application.AddMessageFilter(this);
            Combo(other.Handle); // A real numeric combo with ID 107 on the wrong page.
            for (int i = 0; i < pageMarkers.Length; i++)
                pageMarkers[i] = CreateWindowEx(0, "STATIC", "Settings page " + i, 0x50000000, 5, 5, 150, 20,
                    other.Handle, new IntPtr(6000 + i), IntPtr.Zero, IntPtr.Zero);
            camera.Commit = delegate
            {
                if (!ignoreCommit) persisted = NativeDarkroomNavigation.ReadSelection(iso);
                File.AppendAllText(Path.Combine(root, "events.txt"), "commit=" + persisted + "\n");
            };
            UpdatePage();
            Shown += delegate
            {
                File.WriteAllText(Path.Combine(root, "ready.new"), Handle.ToInt64().ToString());
                File.Move(Path.Combine(root, "ready.new"), Path.Combine(root, "ready.txt"));
            };
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams p = base.CreateParams; p.ExStyle |= 0x08000000; return p; }
        }
        private static IntPtr Combo(IntPtr parent)
        {
            IntPtr combo = CreateWindowEx(0, "COMBOBOX", "", 0x50210003, 5, 95, 220, 130,
                parent, new IntPtr(107), IntPtr.Zero, IntPtr.Zero);
            foreach (string value in new[] { "400", "800", "1600", "3200" }) SendMessage(combo, 0x0143, IntPtr.Zero, value);
            SendMessage(combo, 0x014e, new IntPtr(1), IntPtr.Zero);
            return combo;
        }
        private void UpdatePage()
        {
            for (int i = 0; i < pageMarkers.Length; i++) ShowWindow(pageMarkers[i], i == page ? 5 : 0);
            camera.Visible = settings && page == 5; other.Visible = settings && page != 5;
            if (camera.Visible) SendMessage(iso, 0x014e, new IntPtr(persisted == "1600" ? 2 : 1), IntPtr.Zero);
            File.WriteAllText(Path.Combine(root, "page.txt"), page.ToString());
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0111 && m.LParam == IntPtr.Zero)
            {
                int command = (int)(m.WParam.ToInt64() & 65535);
                if (command == 543 || command == 545 || command == 662 || command == 33776)
                {
                    File.AppendAllText(Path.Combine(root, "commands.txt"), command + "\n");
                    if (InSendMessageEx(IntPtr.Zero) != 0)
                    {
                        File.AppendAllText(Path.Combine(root, "sent-mutations.txt"), "WM_COMMAND " + command + "\n");
                        // Model a receiver that cannot service its normal queued
                        // work while entered through a synchronous command.
                        Thread.Sleep(1000); m.Result = IntPtr.Zero; return;
                    }
                    Thread.Sleep(delay);
                    if (command == 662 && File.Exists(Path.Combine(root, "stall-next.txt")))
                    { m.Result = IntPtr.Zero; return; }
                    if (command == 543) settings = false;
                    if (command == 545) settings = true;
                    if (command == 662) page = (page + 1) % 11;
                    if (command == 33776) File.WriteAllText(Path.Combine(root, "start.txt"), "started");
                    UpdatePage();
                    m.Result = IntPtr.Zero; return;
                }
            }
            base.WndProc(ref m);
        }
        public bool PreFilterMessage(ref Message message)
        {
            if (message.HWnd == iso && (message.Msg == 0x0100 || message.Msg == 0x0101))
                File.AppendAllText(Path.Combine(root, "queued-keys.txt"), message.WParam.ToString() + "\n");
            return false;
        }
    }
    private static int checks;
    private static void Check(bool condition, string text)
    {
        if (!condition) throw new Exception(text);
        checks++; Console.WriteLine("PASS: " + text);
    }
    private static void Fails(Action action, string text)
    {
        bool failed = false;
        try { action(); } catch (InvalidOperationException) { failed = true; }
        catch (TimeoutException) { failed = true; }
        Check(failed, text);
    }
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 1 && args[0] == "--fixture")
        {
            Application.Run(new Fixture(args[1], Array.IndexOf(args, "--reject") >= 0,
                Array.IndexOf(args, "--legacy") >= 0)); return 0;
        }
        foreach (bool legacy in new[] { false, true })
        foreach (bool rejectedCommit in new[] { false, true })
        {
            string root = Path.Combine(args[0], (legacy ? "legacy-" : "") + (rejectedCommit ? "rejected-commit" : "slow-navigation")); Directory.CreateDirectory(root);
            using (Process fixture = Process.Start(new ProcessStartInfo {
                FileName = Assembly.GetExecutingAssembly().Location,
                Arguments = "--fixture \"" + root + "\"" + (rejectedCommit ? " --reject" : "") + (legacy ? " --legacy" : ""),
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }))
            try
            {
                Stopwatch clock = Stopwatch.StartNew();
                string ready = Path.Combine(root, "ready.txt");
                while (!File.Exists(ready) && !fixture.HasExited && clock.ElapsedMilliseconds < 10000) Thread.Sleep(20);
                Check(File.Exists(ready) && !fixture.HasExited, "Isolated native fixture started");
                NativeDarkroomNavigation native = new NativeDarkroomNavigation(fixture.Id, Console.WriteLine);
                Check(native.EditorWindow.ToInt64().ToString() == File.ReadAllText(ready),
                    legacy ? "Toolbar-free event window resolves through the compatibility fallback" : "Multiple owned toolbar windows resolve to the real editor");
                Check(IsWindowVisible(native.EditorWindow), "Native visibility semantics tested on an off-screen window");
                if (!legacy && !rejectedCommit)
                {
                    native.SendCommand(543);
                    Check(native.VisibleIsoControl == IntPtr.Zero, "Already-selected Originals permits one subsequent Settings request");
                }
                clock.Restart(); native.SendCommand(545);
                if (!rejectedCommit) Check(clock.ElapsedMilliseconds >= 400, "Posted WM_COMMAND produces an observed page transition before continuing");
                Check(native.VisibleIsoControl == IntPtr.Zero, "Numeric ID 107 on another page is not Camera ISO");
                DarkroomNavigation navigation = new DarkroomNavigation(native, delegate(string text) { Console.WriteLine(text); });
                DateTime deadline = DateTime.UtcNow.AddSeconds(30);
                navigation.OpenCamera(deadline);
                Check(File.ReadAllText(Path.Combine(root, "page.txt")) == "5", "Navigation finishes on Camera page despite delayed handlers");
                native.SelectIso("1600", deadline);
                navigation.VerifyCameraIso("1600", deadline);
                Check(File.Exists(Path.Combine(root, "events.txt")), "Enter reaches the real ComboBox selection-commit notification");
                Check(File.Exists(Path.Combine(root, "queued-keys.txt")), "ISO keys pass through the normal application message queue");
                navigation.ReopenCamera(deadline);
                if (rejectedCommit)
                {
                    Fails(delegate { navigation.VerifyCameraIso("1600", deadline); }, "Readback after reopening detects a rejected ISO change");
                    Fails(delegate { navigation.StartBoothAfterIso("1600", deadline); }, "Rejected change cannot start Booth");
                }
                else
                {
                    navigation.VerifyCameraIso("1600", deadline);
                    Check(native.ReadIsoValue(native.VisibleIsoControl) == "1600", "Committed ISO persists after reopening Camera Settings");
                    native.SendCommand(662);
                    Fails(delegate { navigation.StartBoothAfterIso("1600", deadline); }, "A last-moment page change cannot start Booth");
                }
                if (legacy && rejectedCommit)
                {
                    native.SendCommand(662);
                    File.WriteAllText(Path.Combine(root, "stall-next.txt"), "No observable page transition");
                    int before = File.ReadAllLines(Path.Combine(root, "commands.txt")).Length;
                    Fails(delegate { navigation.OpenCamera(DateTime.UtcNow.AddSeconds(5)); }, "Unchanged page stops navigation instead of queuing more Next commands");
                    string[] commands = File.ReadAllLines(Path.Combine(root, "commands.txt"));
                    int advances = 0;
                    for (int i = before; i < commands.Length; i++) if (commands[i] == "662") advances++;
                    Check(advances == 1, "A stalled page receives exactly one Next command");
                }
                Check(!File.Exists(Path.Combine(root, "start.txt")), "No unverified Start Booth command was sent");
                Check(!File.Exists(Path.Combine(root, "sent-mutations.txt")), "No command, dropdown change or ISO key used synchronous cross-process dispatch");
            }
            finally
            {
                if (!fixture.HasExited) fixture.CloseMainWindow();
                if (!fixture.WaitForExit(5000)) { fixture.Kill(); fixture.WaitForExit(5000); }
            }
        }
        Console.WriteLine("PASS: " + checks + " real native window/ComboBox checks; no Darkroom process, camera or desktop input used.");
        return 0;
    }
}
