using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Jvdp.LightDarkroomInstaller
{
    internal static partial class InstallerProgram
    {
        private static int Install(
            bool quiet, string testDirectory, bool showAfterUpdate)
        {
            bool testMode = !String.IsNullOrWhiteSpace(testDirectory);
            string installDirectory = testMode
                ? Path.GetFullPath(testDirectory)
                : Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                    "JvdP", "LightDarkroomOverlay");
            string installedExe =
                Path.Combine(installDirectory, InstalledExeName);
            string installedUpdater =
                Path.Combine(installDirectory, UpdaterExeName);
            string stagedExe = installedExe + ".new";
            string stagedUpdater = installedUpdater + ".new";
            string installedUninstaller =
                Path.Combine(installDirectory, UninstallerName);

            Directory.CreateDirectory(installDirectory);
            bool overlayWasVisible = false;
            if (!testMode)
                overlayWasVisible = StopRunningComponents();

            ExtractPayload(PayloadResource, stagedExe);
            ExtractPayload(UpdaterResource, stagedUpdater);
            ValidatePortableExecutable(stagedExe, "overlay");
            ValidatePortableExecutable(stagedUpdater, "updater");
            ReplaceStagedFile(stagedExe, installedExe);
            ReplaceStagedFile(stagedUpdater, installedUpdater);
            string installedReleaseTag =
                WriteInstallMetadata(installDirectory);

            if (testMode)
            {
                File.AppendAllText(
                    Path.Combine(installDirectory, "install-test.log"),
                    DateTime.Now.ToString("s") +
                    " Payloads extracted and validated.\r\n");
                return 0;
            }

            File.Copy(Application.ExecutablePath,
                installedUninstaller, true);
            DisableLegacyAgentStartup();
            RegisterAutomaticStartup(installedExe, installedUpdater);
            RegisterUninstaller(installDirectory, installedExe,
                installedUpdater, installedUninstaller);
            StartComponents(installedExe, installedUpdater,
                installDirectory, quiet,
                showAfterUpdate || overlayWasVisible);
            WriteInstallerStatus("installed",
                "Versie " + installedReleaseTag + " is geïnstalleerd.");
            ShowMessage(
                ProductName + " is geinstalleerd en gestart.\r\n\r\n" +
                "De lichtsensor-app en automatische updater starten voortaan " +
                "wanneer deze Windows-gebruiker zich aanmeldt.\r\n\r\n" +
                "Installatiemap:\r\n" + installDirectory,
                MessageBoxIcon.Information, quiet);
            return 0;
        }

        private static int Uninstall(bool quiet)
        {
            if (!quiet)
            {
                DialogResult answer = MessageBox.Show(
                    "Wil je " + ProductName + " verwijderen?",
                    ProductName, MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes)
                    return 0;
            }

            string installDirectory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "JvdP", "LightDarkroomOverlay");
            StopRunningComponents();
            RemoveRegistryEntries();
            DisableLegacyAgentStartup();
            if (Directory.Exists(installDirectory))
                ScheduleDirectoryRemoval(installDirectory);
            ShowMessage(ProductName + " is verwijderd.",
                MessageBoxIcon.Information, quiet);
            return 0;
        }

        private static void ExtractPayload(
            string resourceName, string destination)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream resource =
                assembly.GetManifestResourceStream(resourceName))
            {
                if (resource == null)
                    throw new InvalidOperationException(
                        "Ingebouwd installatiebestand ontbreekt: " +
                        resourceName);
                using (FileStream output = new FileStream(
                    destination, FileMode.Create,
                    FileAccess.Write, FileShare.None))
                    resource.CopyTo(output);
            }
        }

        private static void ValidatePortableExecutable(
            string path, string component)
        {
            FileInfo file = new FileInfo(path);
            if (!file.Exists || file.Length < 4096)
                throw new InvalidDataException(
                    "Het uitgepakte " + component +
                    "bestand is onvolledig.");
            using (FileStream stream = File.OpenRead(path))
                if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
                    throw new InvalidDataException(
                        "Het uitgepakte " + component +
                        "bestand is geen geldig Windows-programma.");
        }

        private static void ReplaceStagedFile(
            string stagedPath, string destinationPath)
        {
            Exception lastError = null;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    if (File.Exists(destinationPath))
                    {
                        string backupPath = destinationPath + ".backup";
                        if (File.Exists(backupPath))
                            File.Delete(backupPath);
                        File.Replace(stagedPath, destinationPath,
                            backupPath, true);
                        try
                        {
                            if (File.Exists(backupPath))
                                File.Delete(backupPath);
                        }
                        catch { }
                    }
                    else
                    {
                        File.Move(stagedPath, destinationPath);
                    }
                    return;
                }
                catch (IOException exception)
                {
                    lastError = exception;
                }
                catch (UnauthorizedAccessException exception)
                {
                    lastError = exception;
                }
                Thread.Sleep(250);
            }
            throw new IOException(
                "Het actieve programmabestand kon na meerdere pogingen " +
                "niet veilig worden vervangen.", lastError);
        }

        private static string WriteInstallMetadata(string installDirectory)
        {
            string pendingTagPath =
                Path.Combine(installDirectory, "pending-release-tag.txt");
            string releaseTag = File.Exists(pendingTagPath)
                ? File.ReadAllText(pendingTagPath).Trim()
                : "v" + Version;
            if (String.IsNullOrWhiteSpace(releaseTag))
                releaseTag = "v" + Version;

            string metadata =
                "Product=" + ProductName + "\r\n" +
                "Version=" + Version + "\r\n" +
                "ReleaseTag=" + releaseTag + "\r\n" +
                "Installed=" + DateTime.Now.ToString("s") + "\r\n" +
                "Startup=Current user logon\r\n" +
                "SerialPort=Automatic JVDP validation\r\n" +
                "Updates=Automatic public stable/test channels\r\n";
            File.WriteAllText(
                Path.Combine(installDirectory, "installation.txt"),
                metadata, new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(installDirectory, "release-tag.txt"),
                releaseTag, new UTF8Encoding(false));
            if (File.Exists(pendingTagPath))
                File.Delete(pendingTagPath);
            return releaseTag;
        }

        private static void StartComponents(
            string installedExe,
            string installedUpdater,
            string installDirectory,
            bool quiet,
            bool showAfterUpdate)
        {
            Process.Start(new ProcessStartInfo {
                FileName = installedExe,
                Arguments = showAfterUpdate
                    ? ""
                    : (quiet ? "--startup" : ""),
                WorkingDirectory = installDirectory,
                UseShellExecute = true
            });

            Process.Start(new ProcessStartInfo {
                FileName = installedUpdater,
                WorkingDirectory = installDirectory,
                UseShellExecute = true
            });
        }

        private static void WriteInstallerStatus(
            string state, string message)
        {
            try
            {
                string installDirectory = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "JvdP", "LightDarkroomOverlay");
                Directory.CreateDirectory(installDirectory);
                string status =
                    "State=" + state + Environment.NewLine +
                    "Checked=" + DateTime.UtcNow.ToString("o") +
                    Environment.NewLine +
                    "InstalledVersion=" + Version + Environment.NewLine +
                    "AvailableVersion=v" + Version + Environment.NewLine +
                    "Message=" + (message ?? "").Replace("\r", " ")
                        .Replace("\n", " ") + Environment.NewLine;
                File.WriteAllText(
                    Path.Combine(installDirectory, "updater-status.txt"),
                    status, new UTF8Encoding(false));
            }
            catch { }
        }

        private static void TryRestartOverlayAfterFailure()
        {
            try
            {
                string installDirectory = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "JvdP", "LightDarkroomOverlay");
                string installedExe = Path.Combine(
                    installDirectory, InstalledExeName);
                if (!File.Exists(installedExe))
                    return;
                Process.Start(new ProcessStartInfo {
                    FileName = installedExe,
                    WorkingDirectory = installDirectory,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}
