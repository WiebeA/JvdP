using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Jvdp.WindowsIntegration
{
    internal static class StartMenuShortcut
    {
        internal const string FileName = "JvdP Lichtregeling - Lichtsensor.lnk";
        internal static string InstalledExecutable
        {
            get { return Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "JvdP",
                "LightDarkroomOverlay", "JvdpLightDarkroomOverlay.exe"); }
        }
        internal static string MenuPath
        {
            get { return Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.Programs), FileName); }
        }

        internal static void EnsureForInstalledApp(string executable)
        {
            // Portable builds and test runners must never register themselves as
            // the installed app. The shortcut always points at the fixed install.
            if (!SamePath(executable, InstalledExecutable) || !File.Exists(executable)) return;
            if (File.Exists(MenuPath))
            {
                try
                {
                    ShortcutInfo info = Read(MenuPath);
                    if (SamePath(info.Target, executable) && info.Arguments.Length == 0) return;
                }
                catch { /* Repair a missing, unreadable or obsolete shortcut. */ }
            }
            Write(MenuPath, executable);
        }

        internal static void Write(string shortcutPath, string executable)
        {
            executable = Path.GetFullPath(executable);
            shortcutPath = Path.GetFullPath(shortcutPath);
            if (!File.Exists(executable)) throw new FileNotFoundException("Programma ontbreekt.", executable);
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath));
            bool existed = File.Exists(shortcutPath);
            object instance = new ShellLink();
            try
            {
                IShellLinkW link = (IShellLinkW)instance;
                link.SetPath(executable);
                link.SetArguments(""); // Start-menu launch opens the dashboard, not --startup/tray.
                link.SetWorkingDirectory(Path.GetDirectoryName(executable));
                link.SetDescription("JvdP Lichtregeling - lichtsensor voor Jongens van de Photobooth en Darkroom Booth");
                link.SetIconLocation(executable, 0);
                link.SetShowCmd(1);
                ((IPersistFile)instance).Save(shortcutPath, true);
            }
            finally { Marshal.ReleaseComObject(instance); }
            SHChangeNotify(existed ? 0x00002000u : 0x00000002u, 0x0005, shortcutPath, IntPtr.Zero);
        }

        internal static void RemoveInstalledShortcut()
        {
            if (!File.Exists(MenuPath)) return;
            // Do not delete a user-repurposed shortcut pointing to another app.
            if (!SamePath(Read(MenuPath).Target, InstalledExecutable)) return;
            File.Delete(MenuPath);
            SHChangeNotify(0x00000004, 0x0005, MenuPath, IntPtr.Zero);
        }

        internal sealed class ShortcutInfo
        {
            internal string Target;
            internal string Arguments;
            internal string WorkingDirectory;
        }

        internal static ShortcutInfo Read(string path)
        {
            object instance = new ShellLink();
            try
            {
                ((IPersistFile)instance).Load(path, 0);
                IShellLinkW link = (IShellLinkW)instance;
                StringBuilder target = new StringBuilder(1024);
                StringBuilder arguments = new StringBuilder(1024);
                StringBuilder working = new StringBuilder(1024);
                link.GetPath(target, target.Capacity, IntPtr.Zero, 4);
                link.GetArguments(arguments, arguments.Capacity);
                link.GetWorkingDirectory(working, working.Capacity);
                return new ShortcutInfo { Target = target.ToString(), Arguments = arguments.ToString(),
                    WorkingDirectory = working.ToString() };
            }
            finally { Marshal.ReleaseComObject(instance); }
        }

        internal static bool SamePath(string first, string second)
        {
            return !String.IsNullOrWhiteSpace(first) && !String.IsNullOrWhiteSpace(second) &&
                String.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(uint eventId, uint flags, string item, IntPtr other);

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int count, IntPtr findData, uint flags);
            void GetIDList(out IntPtr idList);
            void SetIDList(IntPtr idList);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int command);
            void SetShowCmd(int command);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr window, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
        }
    }
}
