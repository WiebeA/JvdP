using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Jvdp.WindowsIntegration;

namespace Jvdp.LightDarkroomOverlay
{
    internal interface IInstanceActivationPort
    {
        bool TryAcquire(int timeoutMilliseconds);
        bool TryShowExisting(int timeoutMilliseconds);
        bool ConfirmRestart();
        bool RestartUnresponsiveCopy();
        void ReportRestartFailed();
    }

    internal static class InstanceActivation
    {
        internal static bool AcquireOrActivate(IInstanceActivationPort port, bool startup)
        {
            if (port.TryAcquire(0)) return true;
            if (startup || port.TryShowExisting(1500)) return false;
            if (port.TryAcquire(250)) return true; // Previous process may just have exited.
            if (!port.ConfirmRestart()) return false;
            if (port.RestartUnresponsiveCopy() && port.TryAcquire(3000)) return true;
            port.ReportRestartFailed();
            return false;
        }

        internal static void Log(string message)
        {
            try
            {
                string directory = Path.GetDirectoryName(StartMenuShortcut.InstalledExecutable);
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "startup.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
            catch { }
        }

        internal static bool IsSameCopy(string candidatePath, int candidateSession,
            int candidateId, string ownPath, int ownSession, int ownId)
        {
            return candidateId != ownId && candidateSession == ownSession &&
                StartMenuShortcut.SamePath(candidatePath, ownPath);
        }
    }

    internal sealed class NativeInstanceActivation : IInstanceActivationPort
    {
        private readonly Mutex mutex;
        private readonly string executable;
        private readonly int ownId;
        private readonly int ownSession;
        private readonly string processName;

        internal NativeInstanceActivation(Mutex mutex, string executable)
        {
            this.mutex = mutex;
            this.executable = executable;
            using (Process current = Process.GetCurrentProcess())
            {
                ownId = current.Id;
                ownSession = current.SessionId;
                processName = current.ProcessName;
            }
        }

        public bool TryAcquire(int timeoutMilliseconds)
        {
            try { return mutex.WaitOne(timeoutMilliseconds, false); }
            catch (AbandonedMutexException) { return true; }
        }

        private bool IsSameCopy(Process process)
        {
            return InstanceActivation.IsSameCopy(process.MainModule.FileName,
                process.SessionId, process.Id, executable, ownSession, ownId);
        }

        private HashSet<int> SameCopyIds()
        {
            HashSet<int> ids = new HashSet<int>();
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                try { if (IsSameCopy(process)) ids.Add(process.Id); }
                catch { }
                finally { process.Dispose(); }
            }
            return ids;
        }

        public bool TryShowExisting(int timeoutMilliseconds)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < timeoutMilliseconds)
            {
                HashSet<int> ids = SameCopyIds();
                IntPtr window = IntPtr.Zero;
                uint windowProcess = 0;
                EnumProc callback = delegate(IntPtr candidate, IntPtr unused)
                {
                    uint pid;
                    GetWindowThreadProcessId(candidate, out pid);
                    if (!ids.Contains((int)pid)) return true;
                    StringBuilder title = new StringBuilder(256);
                    GetWindowText(candidate, title, title.Capacity);
                    if (!String.Equals(title.ToString(), "JvdP Lichtregeling", StringComparison.Ordinal)) return true;
                    window = candidate;
                    windowProcess = pid;
                    return false;
                };
                EnumWindows(callback, IntPtr.Zero);
                GC.KeepAlive(callback);
                if (window != IntPtr.Zero)
                {
                    AllowSetForegroundWindow(windowProcess);
                    IntPtr result;
                    int remaining = Math.Max(1, timeoutMilliseconds - (int)timer.ElapsedMilliseconds);
                    bool shown = SendMessageTimeout(window, 0x8001, IntPtr.Zero, IntPtr.Zero,
                        0x0003, (uint)remaining, out result) != IntPtr.Zero;
                    InstanceActivation.Log(shown ? "Existing dashboard activated." : "Existing dashboard did not respond.");
                    return shown;
                }
                Thread.Sleep(50); // Another launch may still be constructing its window.
            }
            InstanceActivation.Log("Instance mutex occupied, but no reachable dashboard was found.");
            return false;
        }

        public bool ConfirmRestart()
        {
            return MessageBox.Show(
                "JvdP Lichtregeling draait nog, maar het venster is niet bereikbaar.\r\n\r\n" +
                "Wil je het vastgelopen exemplaar sluiten en opnieuw starten? " +
                "Niet-opgeslagen wijzigingen in de lichtsoftware kunnen verloren gaan. " +
                "Darkroom Booth wordt niet afgesloten.", "JvdP Lichtregeling opnieuw starten",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        public bool RestartUnresponsiveCopy()
        {
            bool succeeded = true;
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (!IsSameCopy(process)) continue;
                    InstanceActivation.Log("User-authorized restart of this installed copy; pid=" + process.Id + ".");
                    process.Kill();
                    if (!process.WaitForExit(2000)) succeeded = false;
                }
                catch (Exception error)
                {
                    InstanceActivation.Log("Restart could not stop previous copy: " + error.Message);
                    succeeded = false;
                }
                finally { process.Dispose(); }
            }
            return succeeded;
        }

        public void ReportRestartFailed()
        {
            MessageBox.Show("Opnieuw starten is nog niet gelukt. Sluit de eerdere Windows-foutmelding " +
                "als die nog openstaat en probeer opnieuw via Start.\r\n\r\n" +
                "Programma: " + executable, "JvdP Lichtregeling", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private delegate bool EnumProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr window, uint message,
            IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    }
}
