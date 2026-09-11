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

        private static void RequireLegacyShutdownReady()
        {
            if (BoothCoordination.DarkroomInCurrentSession())
                throw new InvalidOperationException("Deze oudere lichtregeling kan zichzelf nog niet afsluiten voor een update terwijl Darkroom openstaat. Sluit alleen JvdP Lichtregeling via het traymenu en start de installatie opnieuw. Darkroom kan openblijven; vanaf versie 24.6.5 gebeurt dit automatisch.");
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
                OverlayShutdown.Stop(process, root, session, RequireLegacyShutdownReady);
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
