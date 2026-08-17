using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Jvdp.LightDarkroomInstaller
{
    internal static partial class InstallerProgram
    {
        private static void StopRunningComponents()
        {
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
                    if (!isOverlay && !isUpdater)
                        continue;
                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch { }
                finally { process.Dispose(); }
            }
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
