using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Jvdp.Reliability;

namespace Jvdp.LightDarkroomInstaller
{
    internal static partial class InstallerProgram
    {
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr windowHandle);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr argument);
        private delegate bool EnumWindowCallback(IntPtr window, IntPtr argument);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

        private static void RequireDarkroomClosed()
        {
            int session = Process.GetCurrentProcess().SessionId;
            foreach (Process process in Process.GetProcessesByName("DarkroomBooth"))
                using (process)
                    if (process.SessionId == session)
                        throw new InvalidOperationException("Sluit Darkroom na het evenement voordat je de software bijwerkt.");
        }

        private static void StopForUpdate(string root)
        {
            int session = Process.GetCurrentProcess().SessionId;
            List<Process> components = new List<Process>();
            components.AddRange(Process.GetProcessesByName("JvdpLightDarkroomOverlay"));
            components.AddRange(Process.GetProcessesByName("JvdpAutoUpdater"));
            // Keep the old updater alive if the old overlay refuses shutdown.
            foreach (Process process in components)
            using (process)
            {
                string path;
                try
                {
                    if (process.SessionId != session || (process.ProcessName != "JvdpLightDarkroomOverlay" && process.ProcessName != "JvdpAutoUpdater")) continue;
                    path = process.MainModule.FileName;
                }
                catch { continue; }
                if (!String.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase)) continue;
                if (process.ProcessName == "JvdpAutoUpdater")
                {
                    process.Kill(); process.WaitForExit(5000); continue;
                }
                EnumWindows(delegate(IntPtr window, IntPtr unused)
                {
                    uint id; GetWindowThreadProcessId(window, out id);
                    if (id == process.Id) PostMessage(window, BoothCoordination.ShutdownForUpdate, IntPtr.Zero, IntPtr.Zero);
                    return true;
                }, IntPtr.Zero);
                if (!process.WaitForExit(7000))
                    throw new InvalidOperationException("De lichtregeling kon niet gecontroleerd sluiten. Sluit de app via Afsluiten of start Onderhoud en probeer opnieuw.");
            }
        }

        private static bool StopRunningComponents()
        {
            bool overlayWasVisible = false;
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    bool isOverlay = process.ProcessName.StartsWith(
                        "JvdpLightDarkroomOverlay",
                        StringComparison.OrdinalIgnoreCase);
                    bool isUpdater = process.ProcessName.StartsWith(
                        "JvdpAutoUpdater",
                        StringComparison.OrdinalIgnoreCase);
                    if (!isOverlay && !isUpdater) continue;
                    if (process.SessionId != Process.GetCurrentProcess().SessionId ||
                        !String.Equals(Path.GetDirectoryName(process.MainModule.FileName), BoothCoordination.Root, StringComparison.OrdinalIgnoreCase)) continue;
                    if (isOverlay)
                    {
                        process.Refresh();
                        IntPtr mainWindow = process.MainWindowHandle;
                        if (mainWindow != IntPtr.Zero &&
                            IsWindowVisible(mainWindow))
                            overlayWasVisible = true;
                    }
                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch { }
                finally { process.Dispose(); }
            }
            return overlayWasVisible;
        }

        private static void ScheduleDirectoryRemoval(string directory)
        {
            string cleanupScript = Path.Combine(
                Path.GetTempPath(), "jvdp-light-cleanup-" +
                Guid.NewGuid().ToString("N") + ".cmd");
            string script =
                "@echo off\r\n" +
                "ping 127.0.0.1 -n 3 >nul\r\n" +
                "rmdir /s /q " + Quote(directory) + "\r\n" +
                "del /f /q \"%~f0\"\r\n";
            File.WriteAllText(
                cleanupScript, script, Encoding.ASCII);
            Process.Start(new ProcessStartInfo {
                FileName = "cmd.exe",
                Arguments = "/c " + Quote(cleanupScript),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
    }
}
