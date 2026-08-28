using System;
using System.Collections.Generic;
using System.Drawing;
using Jvdp.LightDarkroomOverlay;

internal static class DarkroomNavigationTests
{
    private sealed class FakePort : IDarkroomNavigationPort
    {
        internal readonly List<int> Commands = new List<int>();
        internal bool Ready = true;
        internal bool InBooth;
        internal bool Editor = true;
        internal bool ExitWorks = true;
        internal bool StartWorks = true;
        internal bool SettingsOpen;
        internal bool SettingsMenu;
        internal bool OtherDialog;
        internal bool CancelWorks = true;
        internal bool MenuOnExit;
        internal int MenuOnIsoRead;
        internal int MenuCloseDelay;
        internal int MenuCancels;
        private int closeMenuAt = -1;
        internal int CameraPage = 5;
        internal int Page;
        internal int Exits;
        internal int Restores;
        internal int Waited;
        internal bool UnstableIso;
        private int isoReads;
        public bool BoothVisible { get { return InBooth; } }
        public bool EditorAvailable
        {
            get { return Editor && !InBooth && !SettingsMenu && !OtherDialog; }
        }
        public IntPtr VisibleIsoControl
        {
            get
            {
                isoReads++;
                if (MenuOnIsoRead > 0 && isoReads == MenuOnIsoRead)
                    SettingsMenu = true;
                if (!EditorAvailable)
                    return IntPtr.Zero;
                if (UnstableIso)
                    return new IntPtr(isoReads);
                return SettingsOpen && Page == CameraPage
                    ? new IntPtr(107) : IntPtr.Zero;
            }
        }
        public void RequireReady()
        {
            if (!Ready) throw new InvalidOperationException("No editor");
            if (!InBooth && (SettingsMenu || OtherDialog))
                throw new InvalidOperationException("Blocked by dialog");
        }
        public bool TryDismissSettingsMenu(int timeoutMilliseconds)
        {
            if (!Ready || !SettingsMenu || OtherDialog || !CancelWorks)
                return false;
            MenuCancels++;
            if (MenuCloseDelay == 0)
                SettingsMenu = false;
            else
                closeMenuAt = Waited + MenuCloseDelay;
            return true;
        }
        public void ExitBooth()
        {
            Exits++;
            if (ExitWorks)
            {
                InBooth = false;
                if (MenuOnExit) SettingsMenu = true;
            }
        }
        public void RestoreEditor() { Restores++; Editor = true; }
        public void SendCommand(int command)
        {
            RequireReady();
            Commands.Add(command);
            if (command == DarkroomNavigation.OriginalsPageCommand)
                SettingsOpen = false;
            if (command == DarkroomNavigation.SettingsPageCommand)
            {
                // Real Darkroom reselecting Settings opens the modal picker.
                if (SettingsOpen) SettingsMenu = true;
                SettingsOpen = true;
            }
            if (command == DarkroomNavigation.NextSettingsPageCommand)
                Page = (Page + 1) % 11;
            if (command == DarkroomNavigation.StartBoothCommand && StartWorks)
                InBooth = true;
        }
        public void Wait(int milliseconds)
        {
            Waited += milliseconds;
            if (closeMenuAt >= 0 && Waited >= closeMenuAt)
            {
                SettingsMenu = false;
                closeMenuAt = -1;
            }
        }
    }

    private static int assertions;
    private static DateTime Deadline { get { return DateTime.UtcNow.AddSeconds(10); } }
    private static DarkroomNavigation Create(FakePort port)
    {
        return new DarkroomNavigation(port, delegate(string ignored) { });
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        assertions++;
    }
    private static void Fails(Action action, string name)
    {
        try { action(); } catch (InvalidOperationException) { assertions++; return; }
        catch (TimeoutException) { assertions++; return; }
        throw new Exception(name + " unexpectedly succeeded");
    }

    private static int Main()
    {
        try
        {
            FakePort missing = new FakePort { Ready = false, InBooth = true };
            DarkroomNavigation n = Create(missing);
            Fails(delegate { n.OpenCamera(Deadline); }, "Missing editor");
            Check(missing.Exits == 0 && missing.Commands.Count == 0 &&
                !n.ShouldRestoreBooth, "Preflight failure changes nothing");

            FakePort open = new FakePort { SettingsOpen = true, Page = 5 };
            Check(Create(open).OpenCamera(Deadline) == new IntPtr(107) &&
                open.Commands.Count == 0, "Reuse only a stable visible ISO");

            for (int page = 0; page < 11; page++)
            {
                foreach (bool alreadyInSettings in new bool[] { false, true })
                {
                    FakePort settings = new FakePort {
                        Page = page, SettingsOpen = alreadyInSettings };
                    Check(Create(settings).OpenCamera(Deadline) == new IntPtr(107),
                        "Find camera from page " + page + ", Settings=" + alreadyInSettings);
                    if (settings.Commands.Count > 0)
                        Check(settings.Commands[0] == 543 && settings.Commands[1] == 545 &&
                            settings.Commands.Count <= 12,
                            "Originals before Settings, bounded navigation");
                    Check(!settings.SettingsMenu && settings.MenuCancels == 0,
                        "Page navigation does not accidentally open a picker");
                    foreach (int command in settings.Commands)
                        Check(command == 543 || command == 545 || command == 662,
                            "Never send picker return value 33082 or toolbar command 660");
                }
            }

            FakePort toggle = new FakePort { SettingsOpen = true };
            toggle.SendCommand(545);
            Check(toggle.SettingsMenu && !toggle.EditorAvailable,
                "Fake reproduces the non-idempotent Settings command from Darkroom");

            FakePort menu = new FakePort { SettingsOpen = true, SettingsMenu = true,
                Page = 5, MenuCloseDelay = 240 };
            n = Create(menu);
            Check(n.OpenCamera(Deadline) == new IntPtr(107) && menu.MenuCancels == 1 &&
                menu.Commands.Count == 0 && menu.Waited >= 240,
                "Existing picker closes and editor is reenabled before accepting ISO");
            menu.SettingsMenu = true; // Reported case: ISO set, menu blocks return.
            n.StartBooth(Deadline);
            Check(menu.MenuCancels == 2 && menu.InBooth && menu.Commands.Count == 1,
                "Picker after confirmed ISO is closed before starting Booth Mode");

            FakePort menuAfterExit = new FakePort { InBooth = true, MenuOnExit = true };
            n = Create(menuAfterExit);
            n.OpenCamera(Deadline);
            n.StartBooth(Deadline);
            Check(menuAfterExit.Exits == 1 && menuAfterExit.MenuCancels == 1 &&
                menuAfterExit.InBooth, "Picker appearing after leaving Booth Mode is recovered");

            FakePort lateMenu = new FakePort { SettingsOpen = true, Page = 5, MenuOnIsoRead = 2 };
            n = Create(lateMenu);
            Check(n.OpenCamera(Deadline) == new IntPtr(107) && lateMenu.MenuCancels == 1,
                "Late picker cannot make an underlying ISO look ready");
            n.StartBooth(Deadline);
            Check(lateMenu.InBooth, "Booth starts after late picker recovery");

            FakePort unknownDialog = new FakePort { OtherDialog = true };
            n = Create(unknownDialog);
            Fails(delegate { n.OpenCamera(Deadline); }, "Unrecognized dialog");
            Fails(delegate { n.StartBooth(Deadline); }, "Unrecognized dialog on return");
            Check(unknownDialog.Commands.Count == 0 && unknownDialog.MenuCancels == 0,
                "Never cancel camera errors, confirmation or other dialogs");

            FakePort cannotCancel = new FakePort { SettingsMenu = true, CancelWorks = false };
            n = Create(cannotCancel);
            Fails(delegate { n.StartBooth(Deadline); }, "Unresponsive picker");
            Check(cannotCancel.Commands.Count == 0 && cannotCancel.SettingsMenu,
                "Unreadable or unresponsive picker does not receive blind commands");

            FakePort slowCancel = new FakePort { SettingsMenu = true, MenuCloseDelay = 2000 };
            n = Create(slowCancel);
            Fails(delegate { n.StartBooth(Deadline); }, "Picker does not reenable editor in time");
            Check(slowCancel.Commands.Count == 0 && slowCancel.MenuCancels == 1 &&
                slowCancel.Waited == 1000, "Bounded menu close, no repeated Cancel or Start");

            FakePort booth = new FakePort { InBooth = true, Editor = false };
            n = Create(booth);
            n.OpenCamera(Deadline);
            Check(booth.Exits == 1 && booth.Restores == 1 && n.ShouldRestoreBooth,
                "Exit identified booth before restoring editor");
            n.StartBooth(Deadline);
            Check(booth.InBooth && !n.ShouldRestoreBooth &&
                booth.Commands[booth.Commands.Count - 1] == 33776,
                "Start sent once to editor and visible booth confirmed");
            int commands = booth.Commands.Count;
            n.StartBooth(Deadline);
            Check(booth.Commands.Count == commands, "Do not start an already visible booth");

            FakePort stuck = new FakePort { InBooth = true, ExitWorks = false };
            n = Create(stuck);
            Fails(delegate { n.OpenCamera(Deadline); }, "Booth exit timeout");
            Check(stuck.Commands.Count == 0 && stuck.Restores == 0 &&
                !n.ShouldRestoreBooth && stuck.Waited <= 2000,
                "Failed exit never edits settings or starts another booth");

            FakePort noCamera = new FakePort { CameraPage = -1 };
            n = Create(noCamera);
            Fails(delegate { n.OpenCamera(Deadline); }, "Missing camera");
            Check(noCamera.Commands.Count == 12 && noCamera.Waited <= 4800 &&
                !n.ShouldRestoreBooth, "No infinite page cycle or unsolicited recovery");

            FakePort flickeringControl = new FakePort { UnstableIso = true };
            n = Create(flickeringControl);
            Fails(delegate { n.OpenCamera(Deadline); }, "Unstable ISO handle");

            FakePort cannotStart = new FakePort { StartWorks = false };
            n = Create(cannotStart);
            Fails(delegate { n.StartBooth(Deadline); }, "No visible booth after start");
            Check(cannotStart.Commands.Count == 1 && cannotStart.Waited <= 5000,
                "Bounded start, no repeated commands or false success");

            FakePort lateStart = new FakePort { InBooth = true, StartWorks = false };
            n = Create(lateStart);
            n.OpenCamera(Deadline);
            Fails(delegate { n.StartBooth(Deadline); }, "Late start after leaving booth");
            Check(!n.ShouldRestoreBooth,
                "Recovery must not resend a Start command whose outcome is unknown");

            FakePort expired = new FakePort { InBooth = true };
            n = Create(expired);
            Fails(delegate { n.OpenCamera(DateTime.UtcNow.AddSeconds(-1)); }, "Expired deadline");
            Check(expired.Commands.Count == 0 && expired.Exits == 0,
                "Expired action sends no command or Escape");

            Check(NativeDarkroomNavigation.SelectSingleEditor(new IntPtr[] {
                new IntPtr(42), new IntPtr(42) }) == new IntPtr(42),
                "Duplicate toolbar IDs with one root are not ambiguous");
            Check(NativeDarkroomNavigation.SelectSingleEditor(new IntPtr[] {
                new IntPtr(42), new IntPtr(43) }) == IntPtr.Zero,
                "Ambiguous editor roots are rejected");
            Check(NativeDarkroomNavigation.SelectSingleEditor(new IntPtr[0]) == IntPtr.Zero,
                "Missing editor is rejected");
            Rectangle monitor = new Rectangle(0, 0, 1920, 1080);
            Check(!NativeDarkroomNavigation.IsBoothSurface(monitor, monitor, 0xc00000, 0),
                "Maximized normal editor is not Booth Mode");
            Check(!NativeDarkroomNavigation.IsBoothSurface(new Rectangle(-32000, -32000, 160, 30),
                monitor, 0, 0), "Minimized editor is not Booth Mode");
            Check(NativeDarkroomNavigation.IsBoothSurface(new Rectangle(0, 0, 1920, 1040),
                monitor, 0, 0), "Borderless booth tolerates taskbar and scaling margins");
            Check(!NativeDarkroomNavigation.IsBoothSurface(monitor, monitor, 0, 0x80),
                "Tool windows are not booths");
            Check(!NativeDarkroomNavigation.IsBoothSurface(new Rectangle(0, 0, 500, 500),
                monitor, 0, 0), "Small preview is not Booth Mode");

            string menuText = "Global Settings\r\nEvent Info Screens Output\r\n" +
                "Output Queue Controls Text Timing\r\nSession History Camera Liveview Video\r\n" +
                "Help Slideshow Device Control Wrap Up\r\nStart Booth Mode";
            Check(NativeDarkroomNavigation.IsSettingsMenuContent("menu", menuText),
                "Recognize the supplied Darkroom Settings menu");
            Check(NativeDarkroomNavigation.IsSettingsMenuContent(" MENU ", menuText.Replace(" ", "  ")),
                "Menu detection tolerates whitespace and case");
            Check(!NativeDarkroomNavigation.IsSettingsMenuContent("Camera error", menuText),
                "A different dialog title cannot pass menu recognition");
            Check(!NativeDarkroomNavigation.IsSettingsMenuContent("menu", "The property could not be set."),
                "Camera errors are not navigation menus");
            Check(!NativeDarkroomNavigation.IsSettingsMenuContent("menu", menuText.Replace("Camera", "CameraError")),
                "Menu detection requires complete labels, not substrings");
            Check(!NativeDarkroomNavigation.IsSettingsMenuContent(null, null), "No content is not a menu");
            foreach (string label in new string[] { "Start Booth Mode", "Global Settings", "Camera",
                    "Output Queue", "Session History", "Event Info" })
                Check(!NativeDarkroomNavigation.IsSettingsMenuContent("menu", menuText.Replace(label, "")),
                    "All menu identity labels required: " + label);

            string afterIso = DarkroomActionStatus.Failure("3200", true, "Blocked by dialog");
            Check(afterIso.Contains("ISO 3200 is door Darkroom bevestigd") &&
                afterIso.Contains("Booth Mode") && !afterIso.Contains("niet gewijzigd"),
                "Return failure preserves confirmed ISO instead of claiming no change");
            Check(DarkroomActionStatus.Failure(null, true, "Timeout").Contains("niet bevestigd"),
                "Unconfirmed selection is reported as unknown, not unchanged");
            Check(DarkroomActionStatus.Failure(null, false, "No editor").Contains("voordat"),
                "Preflight failure distinguished from selection or return failure");
            Console.WriteLine("PASS: " + assertions + " navigation assertions; no desktop or camera input.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error.Message);
            return 1;
        }
    }
}
