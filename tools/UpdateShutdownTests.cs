using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Jvdp.LightDarkroomInstaller;
using Jvdp.Reliability;

internal static class UpdateShutdownTests
{
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);
    private static int checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++; Console.WriteLine("PASS: " + message);
    }
    private static void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, message);
    }
    private static void Main(string[] args)
    {
        foreach (bool manual in new[] { false, true })
        foreach (bool maintenance in new[] { false, true })
        foreach (bool darkroom in new[] { false, true })
            Check(BoothCoordination.CanInstall(manual, maintenance, darkroom) == (!darkroom && (manual || maintenance)),
                "Update permission: manual=" + manual + ", maintenance=" + maintenance + ", Darkroom=" + darkroom);

        foreach (string name in new[] { "legacy", "maintenance", "current", "future" })
        {
            string root = Path.Combine(args[0], name);
            using (Process process = Process.Start(new ProcessStartInfo {
                FileName = Path.Combine(root, "JvdpLightDarkroomOverlay.exe"),
                Arguments = "\"" + root + "\"", UseShellExecute = false,
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }))
            try
            {
                Stopwatch clock = Stopwatch.StartNew();
                string ready = Path.Combine(root, "ready.txt");
                while (!File.Exists(ready) && !process.HasExited && clock.ElapsedMilliseconds < 10000) Thread.Sleep(20);
                Check(File.Exists(ready) && !process.HasExited, name + " started in isolated test directory");
                Check(!IsWindowVisible(new IntPtr(Int64.Parse(File.ReadAllText(ready)))), name + " window stays hidden");
                int session = Process.GetCurrentProcess().SessionId;
                Check(!OverlayShutdown.Stop(process, Path.Combine(root, "other"), session, delegate { }), "Different install directory is untouched");
                Check(!OverlayShutdown.Stop(process, root, session + 1, delegate { }), "Different Windows session is untouched");
                Reject(delegate { OverlayShutdown.Stop(process, root, session, delegate { throw new InvalidOperationException("Darkroom active"); }); },
                    "Active Darkroom blocks shutdown");
                Check(!process.HasExited, "Rejected update preserves running overlay");
                int guards = 0;
                if (name == "future")
                {
                    Reject(delegate { OverlayShutdown.Stop(process, root, session, delegate { guards++; }); }, "Newer app refusing shutdown is never forced closed");
                    Check(!process.HasExited, "Newer app is still running after refusal");
                }
                else
                {
                    if (name == "legacy")
                    {
                        Reject(delegate { OverlayShutdown.Stop(process, root, session, delegate {
                            if (++guards == 2) throw new InvalidOperationException("Darkroom started meanwhile");
                        }); }, "Darkroom is rechecked before legacy shutdown");
                        Check(!process.HasExited, "Legacy app remains running when Darkroom starts meanwhile");
                        guards = 0;
                    }
                    Check(OverlayShutdown.Stop(process, root, session, delegate { guards++; }), name + " accepts upgrade without manual exit or maintenance");
                    Check(File.Exists(Path.Combine(root, "normal-exit.txt")), name + " message loop exits normally, without Process.Kill");
                    Check(guards == (name == "current" ? 1 : 2), name + " uses the appropriate shutdown protocol");
                }
            }
            finally
            {
                // Only this test's own isolated fixture is eligible for cleanup.
                if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); }
            }
        }
        Console.WriteLine("PASS: " + checks + " update/shutdown checks; installed app and Darkroom untouched.");
    }
}
