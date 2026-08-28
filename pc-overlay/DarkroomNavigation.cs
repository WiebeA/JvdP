using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Jvdp.LightDarkroomOverlay
{
    // Kept separate from the form so navigation can be tested without opening
    // Darkroom, sending input to the desktop or connecting a camera.
    internal interface IDarkroomNavigationPort
    {
        bool BoothVisible { get; }
        bool BoothPresented { get; }
        bool EditorAvailable { get; }
        IntPtr VisibleIsoControl { get; }
        void RequireReady();
        bool TryDismissSettingsMenu(int timeoutMilliseconds);
        void ExitBooth();
        void RestoreEditor();
        void PresentBooth();
        void SendCommand(int command);
        void Wait(int milliseconds);
    }

    internal static class DarkroomActionStatus
    {
        internal static string Failure(string confirmedIso, bool selectionAttempted, string error)
        {
            if (!String.IsNullOrEmpty(confirmedIso))
                return "ISO " + confirmedIso + " is door Darkroom bevestigd, maar " +
                    "terugkeren naar Booth Mode is niet gelukt: " + error;
            return selectionAttempted
                ? "ISO-aanpassing niet afgerond; de uiteindelijke ISO is niet bevestigd. " + error
                : "ISO-aanpassing gestopt voordat de ISO-keuze is verstuurd: " + error;
        }
    }

    internal sealed class DarkroomNavigation
    {
        internal const int OriginalsPageCommand = 0x021F;
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
            EnsureReady(deadlineUtc);
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
            EnsureReady(deadlineUtc);
            if (!WaitUntil(delegate { return port.EditorAvailable; },
                    deadlineUtc, 1500))
                throw new InvalidOperationException(
                    "Het Darkroom-hoofdvenster is niet beschikbaar. " +
                    "Sluit een eventueel geopend Darkroom-dialoogvenster.");

            IntPtr combo = WaitForIso(deadlineUtc, 240);
            if (combo != IntPtr.Zero)
                return combo;

            // Selecting Settings AGAIN opens its modal picker. First select
            // Originals so this is a page transition, not a repeated selection.
            // Both commands go to the same native queue, in this order. Neither
            // depends on focus, timing a mouse click or a private toolbar object.
            log("Opening Settings through Originals -> Settings (543 -> 545).");
            port.SendCommand(OriginalsPageCommand);
            port.SendCommand(SettingsPageCommand);
            combo = WaitForIso(deadlineUtc, 500);
            if (combo != IntPtr.Zero)
                return combo;

            // Darkroom has eleven native settings pages. An initial check plus
            // ten advances visits each page once, with no unbounded search.
            for (int page = 1; page <= 10; page++)
            {
                Remaining(deadlineUtc);
                EnsureReady(deadlineUtc);
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
            EnsureReady(deadlineUtc);
            if (port.BoothVisible)
                return;
            if (!port.EditorAvailable)
                throw new InvalidOperationException(
                    "Darkroom kan Booth Mode niet starten: " +
                    "het hoofdvenster is geblokkeerd of niet beschikbaar.");
            // A queued Start must not be sent a second time by recovery if the
            // visible confirmation is late; its outcome is then unknown.
            port.SendCommand(StartBoothCommand);
            boothStartRequested = true;
            if (!WaitUntil(delegate { return port.BoothVisible; },
                    deadlineUtc, 5000))
                throw new InvalidOperationException(
                    "Darkroom heeft nog geen Booth Mode-venster geopend. " +
                    "Controleer een eventuele melding in Darkroom.");
            log("Darkroom Booth window confirmed after Start Booth command.");
        }

        internal void RevealBooth(Action removeCovers, DateTime deadlineUtc)
        {
            Remaining(deadlineUtc);
            if (!port.BoothVisible)
                throw new InvalidOperationException(
                    "Het volledige Darkroom Booth-scherm is nog niet beschikbaar.");
            // IsWindowVisible also returns true for a window behind our dashboard.
            // First remove our covers, THEN present the full Booth background. A
            // floating camera preview alone is not a successful handoff.
            removeCovers();
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Remaining(deadlineUtc);
                port.PresentBooth();
                int confirmations = 0;
                if (WaitUntil(delegate
                    {
                        confirmations = port.BoothPresented ? confirmations + 1 : 0;
                        return confirmations >= 3;
                    }, deadlineUtc, 700))
                {
                    log("Full Darkroom Booth screen presented after removing covers.");
                    return;
                }
            }
            throw new InvalidOperationException(
                "Booth Mode is gestart, maar het volledige Darkroom-scherm " +
                "kon niet naar de voorgrond worden gebracht.");
        }

        private void EnsureReady(DateTime deadlineUtc)
        {
            // A Settings picker is navigation, not a camera error. Dismiss only
            // that positively identified popup; never press Escape on an unknown
            // dialog. This also handles a picker left open by an older version.
            int remaining = Remaining(deadlineUtc);
            if (!port.BoothVisible && !port.EditorAvailable &&
                port.TryDismissSettingsMenu(Math.Min(700, remaining)))
            {
                log("Recognized Darkroom Settings menu; cancelling navigation popup.");
                if (!WaitUntil(delegate { return port.EditorAvailable; },
                        deadlineUtc, 1000))
                    throw new InvalidOperationException(
                        "Het Darkroom-instellingenmenu is gesloten, maar het " +
                        "hoofdvenster is nog niet beschikbaar.");
            }
            port.RequireReady();
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
                EnsureReady(deadlineUtc);
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
        private static int menuReadInProgress;
        internal IntPtr EditorWindow { get; private set; }

        internal NativeDarkroomNavigation(int processId)
        {
            this.processId = processId;
            EditorWindow = FindEditor(processId);
            RequireEditorIdentity();
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
            RequireEditorIdentity();
            if (!IsWindowEnabled(EditorWindow) && !BoothVisible)
                throw new InvalidOperationException(
                    "Darkroom wacht op een ander dialoogvenster. " +
                    "Dit venster wordt niet automatisch gesloten.");
        }

        private void RequireEditorIdentity()
        {
            uint owner;
            if (EditorWindow == IntPtr.Zero || !IsWindow(EditorWindow) ||
                GetWindowThreadProcessId(EditorWindow, out owner) == 0 ||
                owner != (uint)processId)
                throw new InvalidOperationException(
                    "Het Darkroom-hoofdvenster kon niet eenduidig worden gevonden. " +
                    "Open eerst het gewenste event in Darkroom.");
        }

        public bool BoothVisible
        {
            get { return FindBoothWindow(processId, EditorWindow) != IntPtr.Zero; }
        }

        public bool BoothPresented
        {
            get
            {
                uint foregroundProcess;
                GetWindowThreadProcessId(GetForegroundWindow(), out foregroundProcess);
                if (foregroundProcess != (uint)processId)
                    return false;
                List<IntPtr> booths = FindBoothWindows(processId, EditorWindow);
                if (booths.Count == 0)
                    return false;
                foreach (IntPtr booth in booths)
                {
                    Rect boothRect;
                    if (!GetWindowRect(booth, out boothRect))
                        return false;
                    Rectangle bounds = Rectangle.FromLTRB(
                        boothRect.Left, boothRect.Top, boothRect.Right, boothRect.Bottom);
                    // Check the whole background's z-order, not just the process
                    // owning the foreground camera-preview window.
                    IntPtr above = GetWindow(booth, 3); // GW_HWNDPREV
                    for (int count = 0; above != IntPtr.Zero && count < 256; count++)
                    {
                        uint owner;
                        GetWindowThreadProcessId(above, out owner);
                        Rect rect;
                        if (owner == GetCurrentProcessId() && IsWindowVisible(above) &&
                            !IsIconic(above) && GetWindowRect(above, out rect) &&
                            bounds.IntersectsWith(Rectangle.FromLTRB(
                                rect.Left, rect.Top, rect.Right, rect.Bottom)))
                            return false;
                        above = GetWindow(above, 3);
                    }
                }
                return true;
            }
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
            // Child windows can report enabled even while their modal owner is
            // disabled. Never accept the ISO underneath a popup as usable.
            if (editor == IntPtr.Zero || !IsWindowVisible(editor) ||
                !IsWindowEnabled(editor) || IsIconic(editor))
                return IntPtr.Zero;
            return FindChild(editor, IsoControlId, true, true);
        }

        public bool TryDismissSettingsMenu(int timeoutMilliseconds)
        {
            RequireEditorIdentity();
            if (IsWindowEnabled(EditorWindow) || BoothVisible)
                return false;
            Stopwatch timer = Stopwatch.StartNew();
            foreach (IntPtr dialog in ProcessWindows(processId))
            {
                if (!IsOwnedDialog(dialog))
                    continue;
                IntPtr browser = FindHtmlWindow(dialog);
                int remaining = timeoutMilliseconds - (int)timer.ElapsedMilliseconds;
                if (browser == IntPtr.Zero || remaining <= 0)
                    continue;
                if (!ReadSettingsMenu(browser, remaining))
                    continue;
                // The reader is read-only, and a timeout can never close a later
                // dialog. Revalidate ownership and HWNDs immediately before cancel.
                if (!IsOwnedDialog(dialog) || !IsWindowEnabled(dialog) ||
                    !IsChild(dialog, browser) || IsWindowEnabled(EditorWindow))
                    return false;
                PostChecked(dialog, 0x0111, new IntPtr(2), IntPtr.Zero); // IDCANCEL
                return true;
            }
            return false;
        }

        private bool IsOwnedDialog(IntPtr dialog)
        {
            uint ownerProcess;
            if (!IsWindowVisible(dialog) ||
                !String.Equals(ClassName(dialog), "#32770", StringComparison.Ordinal) ||
                GetWindowThreadProcessId(dialog, out ownerProcess) == 0 ||
                ownerProcess != (uint)processId)
                return false;
            IntPtr owner = GetWindow(dialog, 4); // GW_OWNER
            for (int depth = 0; owner != IntPtr.Zero && depth < 8; depth++)
            {
                if (owner == EditorWindow)
                    return true;
                owner = GetWindow(owner, 4);
            }
            return false;
        }

        private static IntPtr FindHtmlWindow(IntPtr dialog)
        {
            IntPtr result = IntPtr.Zero;
            EnumProc callback = delegate(IntPtr child, IntPtr unused)
            {
                if (IsWindowVisible(child) && String.Equals(ClassName(child),
                        "Internet Explorer_Server", StringComparison.Ordinal))
                {
                    result = child;
                    return false;
                }
                return true;
            };
            EnumChildWindows(dialog, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return result;
        }

        // The native Settings picker is an HTML dialog (NAVMENU_HTM, title
        // "menu"). Read only that popup, never the full UIA/MSAA tree. Cross-
        // process COM may stall: one bounded background reader keeps the action
        // responsive, and cannot send commands even if it completes too late.
        private static bool ReadSettingsMenu(IntPtr browser, int timeoutMilliseconds)
        {
            if (Interlocked.CompareExchange(ref menuReadInProgress, 1, 0) != 0)
                return false;
            bool recognized = false;
            Thread reader = new Thread(delegate()
            {
                object document = null;
                object body = null;
                try
                {
                    uint message = RegisterWindowMessage("WM_HTML_GETOBJECT");
                    IntPtr result;
                    if (message == 0 || SendMessageTimeout(browser, message,
                            IntPtr.Zero, IntPtr.Zero, 0x0003, 200, out result) == IntPtr.Zero ||
                        result == IntPtr.Zero)
                        return;
                    Guid documentInterface = new Guid("332C4425-26CB-11D0-B483-00C04FD90119");
                    if (ObjectFromLresult(result, ref documentInterface,
                            IntPtr.Zero, out document) != 0 || document == null)
                        return;
                    string title = Convert.ToString(HtmlProperty(document, "title"));
                    if (!String.Equals(title.Trim(), "menu", StringComparison.OrdinalIgnoreCase))
                        return;
                    body = HtmlProperty(document, "body");
                    if (body != null)
                        recognized = IsSettingsMenuContent(title,
                            Convert.ToString(HtmlProperty(body, "innerText")));
                }
                catch (Exception) { /* Unrecognized/unreadable dialogs stay open. */ }
                finally
                {
                    try { ReleaseHtmlObject(body); ReleaseHtmlObject(document); }
                    finally { Interlocked.Exchange(ref menuReadInProgress, 0); }
                }
            });
            reader.IsBackground = true;
            reader.SetApartmentState(ApartmentState.STA);
            try { reader.Start(); }
            catch
            {
                Interlocked.Exchange(ref menuReadInProgress, 0);
                throw;
            }
            return reader.Join(Math.Max(1, timeoutMilliseconds)) && recognized;
        }

        private static object HtmlProperty(object target, string property)
        {
            return target.GetType().InvokeMember(property, BindingFlags.GetProperty,
                null, target, null);
        }

        private static void ReleaseHtmlObject(object value)
        {
            try
            {
                if (value != null && Marshal.IsComObject(value))
                    Marshal.ReleaseComObject(value);
            }
            catch (Exception) { /* Do not terminate the app on COM cleanup failure. */ }
        }

        internal static bool IsSettingsMenuContent(string title, string body)
        {
            if (String.IsNullOrEmpty(title) || String.IsNullOrEmpty(body) ||
                !String.Equals(title.Trim(), "menu", StringComparison.OrdinalIgnoreCase))
                return false;
            string text = " " + Regex.Replace(body, @"\s+", " ").Trim() + " ";
            foreach (string label in new string[] { "Start Booth Mode", "Global Settings",
                    "Camera", "Output Queue", "Session History", "Event Info" })
                if (text.IndexOf(" " + label + " ", StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            return true;
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

        public void PresentBooth()
        {
            RequireReady();
            List<IntPtr> booths = FindBoothWindows(processId, EditorWindow);
            if (booths.Count == 0)
                throw new InvalidOperationException(
                    "Alleen een cameravoorvertoning is niet voldoende: " +
                    "het volledige Booth-scherm ontbreekt.");
            // Preserve Darkroom's own dimensions and topmost policy. Never
            // maximize the preview, hide it or make the booth permanently topmost.
            for (int index = booths.Count - 1; index >= 0; index--)
            {
                IntPtr booth = booths[index];
                if (!SetWindowPos(booth, IntPtr.Zero, 0, 0, 0, 0,
                        0x4000 | 0x0010 | 0x0001 | 0x0002))
                    throw new InvalidOperationException(
                        "Windows kon het volledige Darkroom-scherm niet naar voren plaatsen.");
                // Invalidate now; let Darkroom paint on its own thread. Do not
                // synchronously block our UI on its drawing or camera pipeline.
                RedrawWindow(booth, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0080);
            }
            SetForegroundWindow(booths[0]);
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
            List<IntPtr> booths = FindBoothWindows(processId, editor);
            return booths.Count == 0 ? IntPtr.Zero : booths[0];
        }

        private static List<IntPtr> FindBoothWindows(int processId, IntPtr editor)
        {
            List<IntPtr> booths = new List<IntPtr>();
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
                    booths.Add(window);
            }
            return booths;
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
        [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after,
            int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string name);
        [DllImport("oleacc.dll")]
        private static extern int ObjectFromLresult(IntPtr result, ref Guid interfaceId,
            IntPtr wParam, [MarshalAs(UnmanagedType.Interface)] out object document);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr window, uint message,
            IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    }
}
