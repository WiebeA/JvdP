using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Jvdp.Reliability
{
    internal sealed class OperationLease : IDisposable
    {
        private Mutex mutex;
        private OperationLease(Mutex value) { mutex = value; }
        internal static OperationLease TryAcquire(int timeout)
        {
            Mutex value = new Mutex(false, @"Local\JvdPBoothOperation");
            bool acquired;
            try { acquired = value.WaitOne(timeout); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) { value.Dispose(); return null; }
            return new OperationLease(value);
        }
        public void Dispose()
        {
            if (mutex == null) return;
            mutex.ReleaseMutex(); mutex.Dispose(); mutex = null;
        }
    }

    internal static class BoothCoordination
    {
        internal const int ShutdownForUpdate = 0x8002;
        internal static string Root
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JvdP", "LightDarkroomOverlay"); }
        }
        internal static string ProcessIdentity(Process process)
        {
            return process.Id + ":" + process.StartTime.ToUniversalTime().Ticks;
        }
        internal static string ReadSession(string root, string identity, DateTime now)
        {
            try
            {
                string[] values = File.ReadAllLines(Path.Combine(root, "session-state.txt"));
                DateTime at;
                if (values.Length != 3 || values[0] != identity ||
                    !DateTime.TryParse(values[2], null, DateTimeStyles.RoundtripKind, out at)) return "unknown";
                double age = (now - at.ToUniversalTime()).TotalSeconds;
                if (age < 0) return "unknown";
                if (values[1] == "busy") return "busy";
                return values[1] == "idle" && age <= 30 ? "idle" : "unknown";
            }
            catch { return "unknown"; }
        }
        internal static bool HasMaintenance(string root, DateTime now)
        {
            try
            {
                DateTime until;
                return DateTime.TryParse(File.ReadAllText(Path.Combine(root, "maintenance-until.txt")),
                    null, DateTimeStyles.RoundtripKind, out until) && until > now && until <= now.AddHours(2);
            }
            catch { return false; }
        }
    }
}
