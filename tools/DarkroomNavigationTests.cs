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
        internal int CameraPage = 5;
        internal int Page;
        internal int Exits;
        internal int Restores;
        internal int Waited;
        internal bool UnstableIso;
        private int isoReads;
        public bool BoothVisible { get { return InBooth; } }
        public bool EditorAvailable { get { return Editor && !InBooth; } }
        public IntPtr VisibleIsoControl
        {
            get
            {
                if (UnstableIso)
                    return new IntPtr(++isoReads);
                return SettingsOpen && Page == CameraPage
                    ? new IntPtr(107) : IntPtr.Zero;
            }
        }
        public void RequireReady()
        {
            if (!Ready) throw new InvalidOperationException("No editor");
        }
        public void ExitBooth()
        {
            Exits++;
            if (ExitWorks) InBooth = false;
        }
        public void RestoreEditor() { Restores++; Editor = true; }
        public void SendCommand(int command)
        {
            RequireReady();
            Commands.Add(command);
            if (command == DarkroomNavigation.SettingsPageCommand)
                SettingsOpen = true;
            if (command == DarkroomNavigation.NextSettingsPageCommand)
                Page = (Page + 1) % 11;
            if (command == DarkroomNavigation.StartBoothCommand && StartWorks)
                InBooth = true;
        }
        public void Wait(int milliseconds) { Waited += milliseconds; }
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
                FakePort settings = new FakePort { Page = page };
                Check(Create(settings).OpenCamera(Deadline) == new IntPtr(107),
                    "Find camera from page " + page);
                Check(settings.Commands[0] == 545 && settings.Commands.Count <= 11,
                    "Open page, not picker, and bound navigation");
                foreach (int command in settings.Commands)
                    Check(command == 545 || command == 662,
                        "Never send picker return value 33082 or toolbar command 660");
            }

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
            Check(noCamera.Commands.Count == 11 && noCamera.Waited <= 4800 &&
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
