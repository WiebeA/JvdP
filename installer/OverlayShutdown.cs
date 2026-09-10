using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Jvdp.Reliability;

namespace Jvdp.LightDarkroomInstaller
{
    internal static class OverlayShutdown
    {
        private delegate bool WindowCallback(IntPtr window, IntPtr unused);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(WindowCallback callback, IntPtr unused);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);

        internal static bool Stop(Process process, string root, int session, Action requireDarkroomClosed)
        {
            if (process.HasExited) return true;
            string path = process.MainModule.FileName;
            if (process.SessionId != session ||
                !String.Equals(Path.GetFileName(path), "JvdpLightDarkroomOverlay.exe", StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(Path.GetDirectoryName(path), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                return false;

            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            Version version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
            bool legacy = version.Major > 0 && version <= new Version(24, 6, 0, 0);
            requireDarkroomClosed();
            EnumWindows(delegate(IntPtr window, IntPtr unused)
            {
                uint id;
                GetWindowThreadProcessId(window, out id);
                if (id == process.Id)
                {
                    PostMessage(window, BoothCoordination.ShutdownForUpdate, IntPtr.Zero, IntPtr.Zero);
                }
                return true;
            }, IntPtr.Zero);
            if (process.WaitForExit(legacy ? 1000 : 7000)) return true;

            // Released versions through 24.6.0 cannot accept this request outside
            // maintenance. End their WinForms message loop normally, rather than
            // killing the process. Never use this compatibility path for newer apps.
            if (legacy)
            {
                requireDarkroomClosed();
                HashSet<uint> threads = new HashSet<uint>();
                EnumWindows(delegate(IntPtr window, IntPtr unused)
                {
                    uint id;
                    uint thread = GetWindowThreadProcessId(window, out id);
                    if (id == process.Id && threads.Add(thread))
                        PostThreadMessage(thread, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
                    return true;
                }, IntPtr.Zero);
                if (process.WaitForExit(7000)) return true;
            }
            throw new InvalidOperationException("De lichtregeling reageert niet op het afsluitverzoek. De installatie is gestopt; probeer het opnieuw zodra de app reageert.");
        }
    }
}
