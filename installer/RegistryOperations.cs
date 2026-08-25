using System;
using System.IO;
using Microsoft.Win32;

namespace Jvdp.LightDarkroomInstaller
{
    internal static partial class InstallerProgram
    {
        private static void RegisterAutomaticStartup(
            string installedExe, string installedUpdater)
        {
            using (RegistryKey runKey =
                Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (runKey == null)
                    throw new InvalidOperationException(
                        "Windows automatisch starten kon niet worden ingesteld.");
                runKey.SetValue(RunValueName,
                    Quote(installedExe) + " --startup",
                    RegistryValueKind.String);
                runKey.SetValue(UpdaterRunValueName,
                    Quote(installedUpdater), RegistryValueKind.String);
            }
        }

        private static void RegisterUninstaller(
            string installDirectory,
            string installedExe,
            string installedUpdater,
            string installedUninstaller)
        {
            using (RegistryKey key =
                Registry.CurrentUser.CreateSubKey(UninstallKeyPath))
            {
                if (key == null)
                    throw new InvalidOperationException(
                        "De Windows-verwijderoptie kon niet worden geregistreerd.");
                key.SetValue("DisplayName", ProductName);
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", "Jongens van de Photobooth");
                key.SetValue("InstallLocation", installDirectory);
                key.SetValue("DisplayIcon", installedExe);
                key.SetValue("UninstallString",
                    Quote(installedUninstaller) + " --uninstall");
                key.SetValue("QuietUninstallString",
                    Quote(installedUninstaller) + " --uninstall --quiet");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                long estimatedBytes =
                    new FileInfo(installedExe).Length +
                    new FileInfo(installedUpdater).Length +
                    new FileInfo(installedUninstaller).Length;
                key.SetValue("EstimatedSize",
                    Math.Max(1, (int)(estimatedBytes / 1024)),
                    RegistryValueKind.DWord);
            }
        }

        private static void RemoveRegistryEntries()
        {
            using (RegistryKey runKey =
                Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (runKey != null)
                {
                    runKey.DeleteValue(RunValueName, false);
                    runKey.DeleteValue(UpdaterRunValueName, false);
                }
            }
            Registry.CurrentUser.DeleteSubKeyTree(
                UninstallKeyPath, false);
        }

        private static void DisableLegacyAgentStartup()
        {
            string legacyStartup = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.Startup),
                "JvdP-Darkroom-Iso-Agent.cmd");
            if (File.Exists(legacyStartup))
                File.Delete(legacyStartup);
        }
    }
}
