using System;
using System.IO;
using System.Reflection;
using Jvdp.LightDarkroomOverlay;
using Jvdp.WindowsIntegration;

internal static class DesktopIntegrationTests
{
    private sealed class FakeActivation : IInstanceActivationPort
    {
        internal bool Acquired;
        internal bool Responsive;
        internal bool Consent;
        internal bool RestartWorks = true;
        internal bool ExitDuringActivation;
        internal int Shows;
        internal int Prompts;
        internal int Restarts;
        internal int Failures;
        public bool TryAcquire(int timeout) { return Acquired; }
        public bool TryShowExisting(int timeout)
        {
            Shows++;
            if (ExitDuringActivation) Acquired = true;
            return Responsive;
        }
        public bool ConfirmRestart() { Prompts++; return Consent; }
        public bool RestartUnresponsiveCopy()
        {
            Restarts++;
            Acquired = RestartWorks;
            return RestartWorks;
        }
        public void ReportRestartFailed() { Failures++; }
    }

    private static int assertions;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        assertions++;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string output = Path.GetFullPath(args[0]);
            string overlay = Path.GetFullPath(args[1]);
            string install = Path.GetFullPath(args[2]);
            string shortcut = Path.Combine(output, "test start menu", StartMenuShortcut.FileName);
            StartMenuShortcut.Write(shortcut, overlay);
            StartMenuShortcut.ShortcutInfo info = StartMenuShortcut.Read(shortcut);
            Check(StartMenuShortcut.SamePath(info.Target, overlay), "Shortcut targets the actual executable");
            Check(info.Arguments == "", "Start menu opens the dashboard, never --startup/tray");
            Check(StartMenuShortcut.SamePath(info.WorkingDirectory, Path.GetDirectoryName(overlay)),
                "Working directory remains valid with spaces in paths");
            StartMenuShortcut.Write(shortcut, overlay);
            Check(Directory.GetFiles(Path.GetDirectoryName(shortcut), "*.lnk").Length == 1,
                "Updates replace the same shortcut, not duplicate entries");
            Check(StartMenuShortcut.FileName.Contains("JvdP") && StartMenuShortcut.FileName.Contains("Lichtregeling") &&
                StartMenuShortcut.FileName.Contains("Lichtsensor"), "Searchable product names in Start-menu entry");

            string installedExe = Path.Combine(install, "JvdpLightDarkroomOverlay.exe");
            info = StartMenuShortcut.Read(Path.Combine(install, "start-menu", StartMenuShortcut.FileName));
            Check(File.Exists(installedExe) && StartMenuShortcut.SamePath(info.Target, installedExe),
                "Installer test mode produces shortcut targeting its extracted payload");
            Check(info.Arguments == "", "Installed shortcut opens a normal window");
            foreach (AssemblyName dependency in Assembly.ReflectionOnlyLoadFrom(overlay).GetReferencedAssemblies())
                Check(dependency.Name != "UIAutomationClient" && dependency.Name != "UIAutomationTypes",
                    "Released app no longer references external UI Automation client: " + dependency.Name);

            FakeActivation fresh = new FakeActivation { Acquired = true };
            Check(InstanceActivation.AcquireOrActivate(fresh, false) && fresh.Shows == 0,
                "Normal launch owns mutex and opens application");
            FakeActivation healthy = new FakeActivation { Responsive = true };
            Check(!InstanceActivation.AcquireOrActivate(healthy, false) && healthy.Shows == 1 &&
                healthy.Prompts == 0 && healthy.Restarts == 0, "Healthy tray instance is shown, never killed");
            FakeActivation startup = new FakeActivation();
            Check(!InstanceActivation.AcquireOrActivate(startup, true) && startup.Shows == 0 &&
                startup.Prompts == 0, "Duplicate Windows startup stays silent");
            FakeActivation died = new FakeActivation { ExitDuringActivation = true };
            Check(InstanceActivation.AcquireOrActivate(died, false) && died.Restarts == 0,
                "Previous process exiting during activation allows a fresh start");
            FakeActivation declined = new FakeActivation();
            Check(!InstanceActivation.AcquireOrActivate(declined, false) && declined.Prompts == 1 &&
                declined.Restarts == 0, "Never kill an old process without restart consent");
            FakeActivation hung = new FakeActivation { Consent = true };
            Check(InstanceActivation.AcquireOrActivate(hung, false) && hung.Restarts == 1 &&
                hung.Failures == 0, "Hung instance can be restarted from Start without a silent exit");
            FakeActivation cannotRestart = new FakeActivation { Consent = true, RestartWorks = false };
            Check(!InstanceActivation.AcquireOrActivate(cannotRestart, false) && cannotRestart.Failures == 1,
                "Failed recovery reports what happened instead of silently disappearing");
            Check(InstanceActivation.IsSameCopy(overlay, 2, 42, overlay, 2, 43), "Same installed copy eligible");
            Check(!InstanceActivation.IsSameCopy(overlay, 2, 43, overlay, 2, 43), "Never terminate the new launcher");
            Check(!InstanceActivation.IsSameCopy(overlay, 3, 42, overlay, 2, 43), "Never terminate another session");
            Check(!InstanceActivation.IsSameCopy(Path.Combine(output, "DarkroomBooth.exe"), 2, 42,
                overlay, 2, 43), "Never terminate Darkroom or a differently installed copy");
            Console.WriteLine("PASS: " + assertions + " desktop integration assertions; no real app, Start menu or processes changed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
