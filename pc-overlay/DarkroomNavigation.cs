using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Jvdp.LightDarkroomOverlay
{
    // Kept separate from the form so navigation can be tested without opening
    // Darkroom, sending input to the desktop or connecting a camera.
    internal interface IDarkroomNavigationPort
    {
        bool BoothVisible { get; }
        bool EditorAvailable { get; }
        IntPtr VisibleIsoControl { get; }
        void RequireReady();
        void ExitBooth();
        void RestoreEditor();
        void SendCommand(int command);
        void Wait(int milliseconds);
    }

    internal sealed class DarkroomNavigation
    {
        internal const int SettingsPageCommand = 0x0221;
        internal const int NextSettingsPageCommand = 0x0296;
        internal const int StartBoothCommand = 0x83F0;
        private readonly IDarkroomNavigationPort port;
        private readonly Action<string> log;
        internal bool StartedInBooth { get; private set; }
        internal bool NavigationStarted { get; private set; }
        private bool boothStartRequested;

        internal DarkroomNavigation(
            IDarkroomNavigationPort port, Action<string> log)
        {
            this.port = port;
            this.log = log;
        }

        internal IntPtr OpenCamera(DateTime deadlineUtc)
        {
            Remaining(deadlineUtc);
            // All readiness checks precede the first command. In particular,
            // failure to identify the editor must not take a working booth down.
            port.RequireReady();
            StartedInBooth = port.BoothVisible;
            NavigationStarted = true;
            if (StartedInBooth)
            {
                log("Leaving the identified Darkroom Booth window.");
                port.ExitBooth();
                if (!WaitUntil(delegate { return !port.BoothVisible; },
                        deadlineUtc, 2000))
                    throw new InvalidOperationException(
                        "Darkroom heeft Booth Mode niet verlaten. " +
                        "De ISO is niet gewijzigd.");
            }
            if (!port.EditorAvailable)
                port.RestoreEditor();
            if (!WaitUntil(delegate { return port.EditorAvailable; },
                    deadlineUtc, 1500))
                throw new InvalidOperationException(
                    "Het Darkroom-hoofdvenster is niet beschikbaar. " +
                    "Sluit een eventueel geopend Darkroom-dialoogvenster.");

            IntPtr combo = WaitForIso(deadlineUtc, 240);
            if (combo != IntPtr.Zero)
                return combo;

            // 0x221 is the Settings *page*, not 0x294, which opens a modal
            // Settings picker. 33082 is a return value of that picker, not a
            // command handler. Menu-style WM_COMMAND is routed by CXFrame to
            // the current page; it does not require a CXToolbar pointer.
            log("Opening the Darkroom Settings page through command 545.");
            port.SendCommand(SettingsPageCommand);
            combo = WaitForIso(deadlineUtc, 500);
            if (combo != IntPtr.Zero)
                return combo;

            // Darkroom has eleven native settings pages. An initial check plus
            // ten advances visits each page once, with no unbounded search.
            for (int page = 1; page <= 10; page++)
            {
                Remaining(deadlineUtc);
                port.SendCommand(NextSettingsPageCommand);
                log("Darkroom settings page advance " + page + "/10.");
                combo = WaitForIso(deadlineUtc, 400);
                if (combo != IntPtr.Zero)
                    return combo;
            }
            throw new InvalidOperationException(
                "Darkroom toont geen bruikbare ISO-keuze op de Camera-pagina. " +
                "Controleer de cameraverbinding in Darkroom.");
        }

        internal void StartBooth(DateTime deadlineUtc)
        {
            Remaining(deadlineUtc);
            port.RequireReady();
            if (port.BoothVisible)
                return;
            if (!port.EditorAvailable)
                throw new InvalidOperationException(
                    "Darkroom kan Booth Mode niet starten: " +
                    "het hoofdvenster is geblokkeerd of niet beschikbaar.");
            // A queued Start must not be sent a second time by recovery if the
            // visible confirmation is late; its outcome is then unknown.
            boothStartRequested = true;
            port.SendCommand(StartBoothCommand);
            if (!WaitUntil(delegate { return port.BoothVisible; },
                    deadlineUtc, 5000))
                throw new InvalidOperationException(
                    "Darkroom heeft nog geen Booth Mode-venster geopend. " +
                    "Controleer een eventuele melding in Darkroom.");
            log("Darkroom Booth window confirmed after Start Booth command.");
        }

        internal bool ShouldRestoreBooth
        {
            get
            {
                return NavigationStarted && StartedInBooth &&
                    !boothStartRequested && !port.BoothVisible;
            }
        }

        private IntPtr WaitForIso(DateTime deadlineUtc, int budget)
        {
            IntPtr previous = IntPtr.Zero;
            int confirmations = 0;
            IntPtr found = IntPtr.Zero;
            WaitUntil(delegate
            {
                IntPtr current = port.VisibleIsoControl;
                confirmations = current != IntPtr.Zero && current == previous
                    ? confirmations + 1 : (current == IntPtr.Zero ? 0 : 1);
                previous = current;
                if (confirmations < 3)
                    return false;
                found = current;
                return true;
            }, deadlineUtc, budget);
            return found;
        }

        private bool WaitUntil(Func<bool> condition, DateTime deadlineUtc, int budget)
        {
            int elapsed = 0;
            while (true)
            {
                int remaining = Remaining(deadlineUtc);
                if (condition())
                    return true;
                if (elapsed >= budget)
                    return false;
                int delay = Math.Min(80, Math.Min(budget - elapsed, remaining));
                port.Wait(delay);
                elapsed += delay;
            }
        }

        private static int Remaining(DateTime deadlineUtc)
        {
            int remaining = (int)Math.Ceiling(
                (deadlineUtc - DateTime.UtcNow).TotalMilliseconds);
            if (remaining <= 0)
                throw new TimeoutException(
                    "Darkroom reageerde niet binnen de maximale actietijd.");
            return remaining;
        }
    }

    internal sealed class NativeDarkroomNavigation : IDarkroomNavigationPort
    {
        private const int ToolbarControlId = 4083;
        private const int IsoControlId = 107;
        private readonly int processId;
        internal IntPtr EditorWindow { get; private set; }

        internal NativeDarkroomNavigation(int processId)
        {
            this.processId = processId;
            EditorWindow = FindEditor(processId);
            RequireReady();
        }

        // Both Darkroom toolbars share ID 4083. Their common top-level owner,
        // not either toolbar's position or private C++ object, is the command
        // destination. Hidden children are included while Booth Mode is active.
        internal static IntPtr FindEditor(int processId)
        {
            List<IntPtr> candidates = new List<IntPtr>();
            List<IntPtr> windows = ProcessWindows(processId);
            foreach (IntPtr window in windows)
            {
                if (FindChild(window, ToolbarControlId, false, false) != IntPtr.Zero)
                    candidates.Add(window);
            }
            if (candidates.Count > 0)
                return SelectSingleEditor(candidates);
            // Some versions destroy rather than hide their editor controls in
            // Booth Mode. The unique unowned, captioned application frame is
            // still identifiable without reading its private process memory.
            foreach (IntPtr window in windows)
            {
                StringBuilder caption = new StringBuilder(512);
                GetWindowText(window, caption, caption.Capacity);
                if (GetWindow(window, 4) == IntPtr.Zero &&
                    (GetWindowLong(window, -16) & 0x00c00000) != 0 &&
                    !String.Equals(ClassName(window), "#32770", StringComparison.Ordinal) &&
                    caption.ToString().IndexOf("Darkroom", StringComparison.OrdinalIgnoreCase) >= 0)
                    candidates.Add(window);
            }
            return SelectSingleEditor(candidates);
        }

        internal static IntPtr SelectSingleEditor(IList<IntPtr> candidates)
        {
            IntPtr result = IntPtr.Zero;
            foreach (IntPtr candidate in candidates)
            {
                if (candidate == IntPtr.Zero || candidate == result)
                    continue;
                if (result != IntPtr.Zero)
                    return IntPtr.Zero;
                result = candidate;
            }
            return result;
        }

        public void RequireReady()
        {
            uint owner;
            if (EditorWindow == IntPtr.Zero || !IsWindow(EditorWindow) ||
                GetWindowThreadProcessId(EditorWindow, out owner) == 0 ||
                owner != (uint)processId)
                throw new InvalidOperationException(
                    "Het Darkroom-hoofdvenster kon niet eenduidig worden gevonden. " +
                    "Open eerst het gewenste event in Darkroom. " +
                    "Booth Mode en ISO zijn niet gewijzigd.");
            if (!IsWindowEnabled(EditorWindow) && !BoothVisible)
                throw new InvalidOperationException(
                    "Darkroom wacht op een dialoogvenster. Sluit dit eerst; " +
                    "de ISO is niet gewijzigd.");
        }

        public bool BoothVisible
        {
            get { return FindBoothWindow(processId, EditorWindow) != IntPtr.Zero; }
        }

        public bool EditorAvailable
        {
            get
            {
                return IsWindow(EditorWindow) && IsWindowVisible(EditorWindow) &&
                    IsWindowEnabled(EditorWindow) && !IsIconic(EditorWindow) &&
                    !BoothVisible;
            }
        }

        public IntPtr VisibleIsoControl
        {
            get { return FindVisibleIsoControl(EditorWindow); }
        }

        internal static IntPtr FindVisibleIsoControl(IntPtr editor)
        {
            return FindChild(editor, IsoControlId, true, true);
        }

        public void ExitBooth()
        {
            IntPtr booth = FindBoothWindow(processId, EditorWindow);
            if (booth == IntPtr.Zero)
                return;
            // Target Darkroom's own message queue. A global Escape could be
            // swallowed by our cover or delivered to an unrelated application.
            uint scan = MapVirtualKey(0x1b, 0);
            PostChecked(booth, 0x0100, new IntPtr(0x1b),
                new IntPtr(1 | ((int)scan << 16)));
            PostChecked(booth, 0x0101, new IntPtr(0x1b),
                new IntPtr(unchecked((int)0xc0000001) | ((int)scan << 16)));
        }

        public void RestoreEditor()
        {
            if (!BoothVisible)
                ShowWindowAsync(EditorWindow, 9);
        }

        public void SendCommand(int command)
        {
            RequireReady();
            PostChecked(EditorWindow, 0x0111, new IntPtr(command), IntPtr.Zero);
        }

        public void Wait(int milliseconds) { Thread.Sleep(milliseconds); }

        private static void PostChecked(
            IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (PostMessage(window, message, wParam, lParam))
                return;
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(error == 5
                ? "Windows blokkeert de Darkroom-bediening. Start Darkroom en " +
                  "JvdP Lichtregeling onder dezelfde gebruiker en met dezelfde rechten."
                : "Darkroom-opdracht kon niet worden verzonden: " +
                  new Win32Exception(error).Message);
        }

        internal static IntPtr FindBoothWindow(int processId, IntPtr editor)
        {
            foreach (IntPtr window in ProcessWindows(processId))
            {
                if (!IsWindowVisible(window) || IsIconic(window))
                    continue;
                long style = GetWindowLong(window, -16);
                long extendedStyle = GetWindowLong(window, -20);
                if ((extendedStyle & 0x80) != 0 || // tool window
                    (style & 0x00c00000) != 0 || // caption: normal/maximized editor
                    String.Equals(ClassName(window), "#32770", StringComparison.Ordinal))
                    continue;
                if (window == editor &&
                    FindChild(editor, ToolbarControlId, true, false) != IntPtr.Zero)
                    continue;
                Rect rect;
                if (!GetWindowRect(window, out rect))
                    continue;
                Rectangle bounds = Rectangle.FromLTRB(
                    rect.Left, rect.Top, rect.Right, rect.Bottom);
                Rectangle monitor = Screen.FromHandle(window).Bounds;
                if (IsBoothSurface(bounds, monitor, style, extendedStyle))
                    return window;
            }
            return IntPtr.Zero;
        }

        internal static bool IsBoothSurface(
            Rectangle bounds, Rectangle monitor, long style, long extendedStyle)
        {
            if ((style & 0x00c00000) != 0 || (extendedStyle & 0x80) != 0 ||
                monitor.Width <= 0 || monitor.Height <= 0)
                return false;
            Rectangle visible = Rectangle.Intersect(bounds, monitor);
            return visible.Width >= monitor.Width * 0.9 &&
                visible.Height >= monitor.Height * 0.9;
        }

        private static IntPtr FindChild(
            IntPtr root, int id, bool visibleOnly, bool comboOnly)
        {
            if (root == IntPtr.Zero)
                return IntPtr.Zero;
            IntPtr result = IntPtr.Zero;
            EnumProc callback = delegate(IntPtr child, IntPtr unused)
            {
                if (GetDlgCtrlID(child) == id &&
                    (!visibleOnly || (IsWindowVisible(child) && IsWindowEnabled(child))) &&
                    (!comboOnly || IsUsableCombo(child)))
                {
                    result = child;
                    return false;
                }
                return true;
            };
            EnumChildWindows(root, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return result;
        }

        private static bool IsUsableCombo(IntPtr window)
        {
            // Darkroom may subclass the ComboBox under its own class name.
            // Validate its read-only native protocol rather than a class string.
            IntPtr count;
            return SendMessageTimeout(window, 0x0146, IntPtr.Zero, IntPtr.Zero,
                0x0002, 150, out count) != IntPtr.Zero &&
                count.ToInt64() > 0 && count.ToInt64() <= 256;
        }

        private static string ClassName(IntPtr window)
        {
            StringBuilder value = new StringBuilder(256);
            GetClassName(window, value, value.Capacity);
            return value.ToString();
        }

        private static List<IntPtr> ProcessWindows(int processId)
        {
            List<IntPtr> result = new List<IntPtr>();
            EnumProc callback = delegate(IntPtr window, IntPtr unused)
            {
                uint owner;
                GetWindowThreadProcessId(window, out owner);
                if (owner == (uint)processId)
                    result.Add(window);
                return true;
            };
            EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return result;
        }

        private delegate bool EnumProc(IntPtr window, IntPtr parameter);
        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr root, EnumProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr window, uint message,
            IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    }
}
