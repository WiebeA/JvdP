using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Jvdp.Reliability;

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
            string installedConfig =
                Path.Combine(installDirectory, InstalledConfigName);
            string stagedExe = installedExe + ".new";
            string stagedUpdater = installedUpdater + ".new";
            string stagedConfig = installedConfig + ".new";
            string installedUninstaller =
                Path.Combine(installDirectory, UninstallerName);

            Directory.CreateDirectory(installDirectory);
            string helper = Path.Combine(installDirectory, "JvdpSessionSignal.exe");
            ExtractPayload(PayloadResource, stagedExe);
            ExtractPayload(UpdaterResource, stagedUpdater);
            ExtractPayload(OverlayConfigResource, stagedConfig);
            ExtractPayload("JvdpSessionSignal.exe", helper + ".new");
            string guide = Path.Combine(installDirectory, "Sessiekoppeling.txt");
            ExtractPayload("SessionGuide.txt", guide + ".new");
            ValidatePortableExecutable(stagedExe, "overlay");
            ValidatePortableExecutable(stagedUpdater, "updater");
            ValidatePortableExecutable(helper + ".new", "sessiekoppeling");
            ValidateConfiguration(stagedConfig);

            using (OperationLease lease = testMode ? null : OperationLease.TryAcquire(0))
            {
                if (!testMode && lease == null)
                    throw new InvalidOperationException("Een ISO-aanpassing loopt. Probeer installeren opnieuw zodra deze klaar is; Darkroom kan openblijven.");
                if (!testMode)
                {
                    StopForUpdate(installDirectory);
                }
                UpdateTransaction transaction = new UpdateTransaction(installDirectory);
                if (transaction.Pending) transaction.Rollback();
                transaction.Begin(new[] { InstalledExeName, UpdaterExeName, InstalledConfigName, "JvdpSessionSignal.exe",
                    UninstallerName, "installation.txt", "release-tag.txt", "booth-profile.json", "Sessiekoppeling.txt" });
                Process startedOverlay = null;
                try
                {
                    ReplaceStagedFile(stagedExe, installedExe);
                    ReplaceStagedFile(stagedUpdater, installedUpdater);
                    ReplaceStagedFile(stagedConfig, installedConfig);
                    ReplaceStagedFile(helper + ".new", helper);
                    ReplaceStagedFile(guide + ".new", guide);
                    if (testMode && HasFlag(Environment.GetCommandLineArgs(), "--test-fail-after-files"))
                        throw new IOException("Simulated interruption after payload replacement.");
                    if (testMode)
                    {
                        WriteInstallMetadata(installDirectory);
                        Jvdp.WindowsIntegration.StartMenuShortcut.Write(
                            Path.Combine(installDirectory, "start-menu", Jvdp.WindowsIntegration.StartMenuShortcut.FileName), installedExe);
                        transaction.Commit();
                        File.AppendAllText(Path.Combine(installDirectory, "install-test.log"), "Payloads extracted and validated.\r\n");
                        return 0;
                    }
                    File.Copy(Application.ExecutablePath, installedUninstaller, true);
                    string token = Guid.NewGuid().ToString("N");
                    ReliableFiles.Write(Path.Combine(installDirectory, "update-health-token.txt"), token);
                    startedOverlay = Process.Start(new ProcessStartInfo { FileName = installedExe,
                        Arguments = "--startup", WorkingDirectory = installDirectory, UseShellExecute = false });
                    if (!WaitForStartupHealth(installDirectory, token, startedOverlay, 20000))
                        throw new InvalidOperationException("De nieuwe app heeft geen gezonde opstart bevestigd. De vorige versie wordt hersteld.");
                    string installedReleaseTag = WriteInstallMetadata(installDirectory);
                    DisableLegacyAgentStartup();
                    RegisterAutomaticStartup(installedExe, installedUpdater);
                    Jvdp.WindowsIntegration.StartMenuShortcut.Write(Jvdp.WindowsIntegration.StartMenuShortcut.MenuPath, installedExe);
                    RegisterUninstaller(installDirectory, installedExe, installedUpdater, installedUninstaller);
                    transaction.Commit();
                    WriteInstallerStatus("installed", "Versie " + installedReleaseTag + " is geïnstalleerd en gestart.");
                    Process.Start(new ProcessStartInfo { FileName = installedUpdater, WorkingDirectory = installDirectory, UseShellExecute = false, CreateNoWindow = true });
                    if (showAfterUpdate || !quiet) Process.Start(installedExe);
                    ShowMessage(ProductName + " is geïnstalleerd. Open Kalibratie en diagnose om de booth in te stellen.", MessageBoxIcon.Information, quiet);
                    return 0;
                }
                catch
                {
                    // After commit the new version is healthy. A shortcut or
                    // updater launch failure must never kill it or claim rollback.
                    if (!transaction.Pending) throw;
                    if (startedOverlay != null)
                    {
                        try { if (!startedOverlay.HasExited) { startedOverlay.Kill(); startedOverlay.WaitForExit(5000); } } catch { }
                    }
                    transaction.Rollback();
                    if (!testMode)
                    {
                        ReliableFiles.Write(Path.Combine(installDirectory, "blocked-release.txt"), "v" + Version);
                        if (File.Exists(installedExe)) StartComponents(installedExe, installedUpdater, installDirectory, true, true);
                    }
                    throw;
                }
                finally { if (startedOverlay != null) startedOverlay.Dispose(); }
            }
        }

        private static bool WaitForStartupHealth(string root, string token, Process process, int timeout)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < timeout)
            {
                if (process == null || process.HasExited) return false;
                try
                {
                    string[] lines = File.ReadAllLines(Path.Combine(root, "update-health-ok.txt"));
                    if (lines.Length == 3 && lines[0] == token && lines[1] == Version && lines[2] == process.Id.ToString()) return true;
                }
                catch (IOException) { }
                Thread.Sleep(100);
            }
            return false;
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
            Jvdp.WindowsIntegration.StartMenuShortcut.RemoveInstalledShortcut();
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

        private static void ValidateConfiguration(string path)
        {
            string content = File.ReadAllText(path);
            if (content.IndexOf("DpiAwareness",
                    StringComparison.OrdinalIgnoreCase) < 0 ||
                content.IndexOf("PerMonitorV2",
                    StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidDataException(
                    "De DPI-configuratie van de overlay is onvolledig.");
        }

        private static string WriteInstallMetadata(string installDirectory)
        {
            string pendingTagPath =
                Path.Combine(installDirectory, "pending-release-tag.txt");
            string releaseTag = "v" + Version;

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
                string installedExe = Path.Combine(installDirectory, InstalledExeName);
                string installedVersion = File.Exists(installedExe)
                    ? FileVersionInfo.GetVersionInfo(installedExe).ProductVersion : "";
                string status =
                    "State=" + state + Environment.NewLine +
                    "Checked=" + DateTime.UtcNow.ToString("o") +
                    Environment.NewLine +
                    "InstalledVersion=" + installedVersion + Environment.NewLine +
                    "AvailableVersion=v" + Version + Environment.NewLine +
                    "Message=" + (message ?? "").Replace("\r", " ")
                        .Replace("\n", " ") + Environment.NewLine;
                ReliableFiles.Write(Path.Combine(installDirectory, "updater-status.txt"), status);
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
                foreach (Process process in Process.GetProcessesByName("JvdpLightDarkroomOverlay"))
                    using (process)
                        if (process.SessionId == Process.GetCurrentProcess().SessionId &&
                            String.Equals(process.MainModule.FileName, installedExe, StringComparison.OrdinalIgnoreCase)) return;
                Process.Start(new ProcessStartInfo {
                    FileName = installedExe,
                    Arguments = "--startup",
                    WorkingDirectory = installDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch { }
        }
    }
}
