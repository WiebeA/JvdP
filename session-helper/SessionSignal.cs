using System;
using System.Diagnostics;
using System.IO;
using Jvdp.Reliability;

internal static class SessionSignal
{
    private static int Main(string[] args)
    {
        if (args.Length != 1 || (args[0] != "busy" && args[0] != "idle")) return 2;
        // Never wait for an ISO lease from a Darkroom callback: Darkroom may be
        // synchronously waiting for this process while navigation needs its UI.
        // Publish the signal immediately; the overlay rechecks before navigation.
        {
            Process[] processes = Process.GetProcessesByName("DarkroomBooth");
            try
            {
                Process own = Process.GetCurrentProcess();
                Process match = null;
                foreach (Process process in processes)
                    if (process.SessionId == own.SessionId)
                    {
                        if (match != null) return 4;
                        match = process;
                    }
                if (match == null) return 4;
                ReliableFiles.Write(Path.Combine(BoothCoordination.Root, "session-state.txt"),
                    BoothCoordination.ProcessIdentity(match) + "\n" + args[0] + "\n" + DateTime.UtcNow.ToString("o"));
                return 0;
            }
            catch (Exception error)
            {
                try { ReliableFiles.AppendLog(Path.Combine(BoothCoordination.Root, "session-helper.log"),
                    DateTime.UtcNow.ToString("o") + " " + error.Message); } catch { }
                return 1;
            }
            finally { foreach (Process process in processes) process.Dispose(); }
        }
    }
}
