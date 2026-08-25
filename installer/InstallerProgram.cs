using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Jvdp.LightDarkroomInstaller
{
    internal static partial class InstallerProgram
    {
        private const string ProductName = "JvdP Light to Darkroom";
        private const string Version = BuildInfo.Version;
        private const string PayloadResource = "JvdpLightDarkroomOverlay.exe";
        private const string UpdaterResource = "JvdpAutoUpdater.exe";
        private const string InstalledExeName = "JvdpLightDarkroomOverlay.exe";
        private const string UpdaterExeName = "JvdpAutoUpdater.exe";
        private const string UninstallerName = "JvdP-Light-Darkroom-Uninstall.exe";
        private const string RunKeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "JvdP Light to Darkroom";
        private const string UpdaterRunValueName = "JvdP Automatic Updates";
        private const string UninstallKeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\JvdPLightDarkroom";

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool quiet = HasFlag(args, "--quiet");
            bool showAfterUpdate = HasFlag(args, "--show-after-update");
            bool uninstall = HasFlag(args, "--uninstall") ||
                Path.GetFileNameWithoutExtension(Application.ExecutablePath)
                    .IndexOf("uninstall", StringComparison.OrdinalIgnoreCase) >= 0;
            string testDirectory = GetOption(args, "--test-dir=");

            bool ownsMutex;
            using (Mutex mutex = new Mutex(
                true, @"Local\JvdPLightDarkroomInstaller", out ownsMutex))
            {
                if (!ownsMutex)
                {
                    ShowMessage("De installatie draait al.",
                        MessageBoxIcon.Information, quiet);
                    return 2;
                }
                try
                {
                    return uninstall
                        ? Uninstall(quiet)
                        : Install(quiet, testDirectory, showAfterUpdate);
                }
                catch (Exception exception)
                {
                    WriteInstallerStatus("error",
                        "Installatie mislukt: " + exception.Message);
                    if (quiet && !uninstall &&
                        String.IsNullOrWhiteSpace(testDirectory))
                        TryRestartOverlayAfterFailure();
                    ShowMessage(
                        "De installatie is niet voltooid.\r\n\r\n" +
                        exception.Message,
                        MessageBoxIcon.Error, quiet);
                    return 1;
                }
            }
        }

        private static bool HasFlag(string[] args, string flag)
        {
            foreach (string argument in args)
                if (String.Equals(argument, flag,
                    StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static string GetOption(string[] args, string prefix)
        {
            foreach (string argument in args)
                if (argument.StartsWith(prefix,
                    StringComparison.OrdinalIgnoreCase))
                    return argument.Substring(prefix.Length).Trim('"');
            return "";
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static void ShowMessage(
            string message, MessageBoxIcon icon, bool quiet)
        {
            if (!quiet)
                MessageBox.Show(message, ProductName,
                    MessageBoxButtons.OK, icon);
        }
    }
}
