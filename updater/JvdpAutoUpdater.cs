using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Jvdp.AutoUpdater
{
    internal sealed class GitHubAsset
    {
        public string name { get; set; }
        public string url { get; set; }
    }

    internal sealed class GitHubRelease
    {
        public string tag_name { get; set; }
        public bool draft { get; set; }
        public bool prerelease { get; set; }
        public GitHubAsset[] assets { get; set; }
    }

    internal static class Program
    {
        private const string Repository = "WiebeA/JvdP";
        private const string InstallerAsset =
            "JvdP-Photobooth-Lichtsensor-Installatie.exe";
        private const string ChecksumsAsset = "SHA256SUMS.txt";
        private const string CheckNowEventName =
            @"Local\JvdPAutoUpdaterCheckNow";
        private static readonly string LocalRoot = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "JvdP", "LightDarkroomOverlay");

        private static readonly string ChannelPath =
            Path.Combine(LocalRoot, "update-channel.txt");
        private static readonly string ReleaseTagPath =
            Path.Combine(LocalRoot, "release-tag.txt");
        private static readonly string PendingReleaseTagPath =
            Path.Combine(LocalRoot, "pending-release-tag.txt");
        private static readonly string LogPath =
            Path.Combine(LocalRoot, "updater.log");
        private static readonly string StatusPath =
            Path.Combine(LocalRoot, "updater-status.txt");

        [STAThread]
        private static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Directory.CreateDirectory(LocalRoot);

            if (HasFlag(args, "--configure"))
                return Configure();

            bool requestedNow = HasFlag(args, "--check-now");
            if (requestedNow && SignalRunningUpdater())
                return 0;

            bool once = HasFlag(args, "--once");
            bool ownsMutex;
            using (Mutex mutex = new Mutex(
                true, @"Local\JvdPAutoUpdater", out ownsMutex))
            {
                if (!ownsMutex)
                    return 0;

                using (EventWaitHandle checkNowEvent = new EventWaitHandle(
                    false, EventResetMode.AutoReset, CheckNowEventName))
                {
                    do
                    {
                        try
                        {
                            if (CheckForUpdate())
                                return 0;
                        }
                        catch (Exception exception)
                        {
                            WriteStatus("error", "",
                                "Updatecontrole mislukt: " +
                                exception.Message);
                            Log("Update check failed: " + exception.Message);
                        }

                        if (once)
                            return 0;
                        checkNowEvent.WaitOne(TimeSpan.FromMinutes(30));
                    }
                    while (true);
                }
            }
        }

        private static bool SignalRunningUpdater()
        {
            try
            {
                using (EventWaitHandle checkNowEvent =
                    EventWaitHandle.OpenExisting(CheckNowEventName))
                {
                    checkNowEvent.Set();
                    return true;
                }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
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

        private static int Configure()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using (Form form = new Form())
            using (Label explanation = new Label())
            using (Label channelLabel = new Label())
            using (ComboBox channelBox = new ComboBox())
            using (Button saveButton = new Button())
            using (Button cancelButton = new Button())
            {
                form.Text = "JvdP automatic updates";
                form.Width = 520;
                form.Height = 225;
                form.StartPosition = FormStartPosition.CenterScreen;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;

                explanation.Text =
                    "Updates are downloaded from the public WiebeA/JvdP " +
                    "repository. No GitHub account or token is required.";
                explanation.SetBounds(20, 18, 465, 45);

                channelLabel.Text = "Update channel";
                channelLabel.SetBounds(20, 82, 120, 22);
                channelBox.DropDownStyle = ComboBoxStyle.DropDownList;
                channelBox.Items.AddRange(new object[] { "stable", "test" });
                channelBox.SelectedItem = LoadChannel();
                if (channelBox.SelectedIndex < 0)
                    channelBox.SelectedIndex = 0;
                channelBox.SetBounds(145, 80, 160, 25);

                saveButton.Text = "Save";
                saveButton.DialogResult = DialogResult.OK;
                saveButton.SetBounds(305, 130, 85, 30);
                cancelButton.Text = "Cancel";
                cancelButton.DialogResult = DialogResult.Cancel;
                cancelButton.SetBounds(400, 130, 85, 30);

                form.Controls.AddRange(new Control[] {
                    explanation, channelLabel,
                    channelBox, saveButton, cancelButton
                });
                form.AcceptButton = saveButton;
                form.CancelButton = cancelButton;

                if (form.ShowDialog() != DialogResult.OK)
                    return 0;

                File.WriteAllText(ChannelPath,
                    Convert.ToString(channelBox.SelectedItem),
                    new UTF8Encoding(false));
                Log("Automatic updates configured for channel " +
                    Convert.ToString(channelBox.SelectedItem) + ".");
                Process.Start(new ProcessStartInfo {
                    FileName = Application.ExecutablePath,
                    UseShellExecute = true
                });
                MessageBox.Show("Automatic updates are now enabled.",
                    "JvdP automatic updates", MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return 0;
            }
        }

        private static string LoadChannel()
        {
            if (!File.Exists(ChannelPath))
                return "stable";
            string value = File.ReadAllText(ChannelPath).Trim().ToLowerInvariant();
            return value == "test" ? "test" : "stable";
        }

        private static bool CheckForUpdate()
        {
            string channel = LoadChannel();
            WriteStatus("checking", "", "Controleren op updates...");
            GitHubRelease release = GetRelease(channel);
            if (release == null || release.draft ||
                String.IsNullOrWhiteSpace(release.tag_name))
            {
                WriteStatus("current", "",
                    "Deze versie is actueel.");
                return false;
            }
            if (!Regex.IsMatch(release.tag_name, @"^[0-9A-Za-z._-]+$"))
                throw new InvalidDataException("The release tag is invalid.");

            string installedTag = File.Exists(ReleaseTagPath)
                ? File.ReadAllText(ReleaseTagPath).Trim()
                : "v" + BuildInfo.Version;
            if (String.Equals(installedTag, release.tag_name,
                StringComparison.OrdinalIgnoreCase))
            {
                WriteStatus("current", release.tag_name,
                    "Deze versie is actueel.");
                return false;
            }

            Version available = ParseVersion(release.tag_name);
            Version installed = ParseVersion(installedTag);
            if (available != null && installed != null &&
                available < installed)
            {
                WriteStatus("current", release.tag_name,
                    "Deze versie is nieuwer dan de gepubliceerde versie.");
                return false;
            }

            GitHubAsset installer = FindAsset(release, InstallerAsset);
            GitHubAsset checksums = FindAsset(release, ChecksumsAsset);
            if (installer == null || checksums == null)
                throw new InvalidDataException(
                    "The release is missing the installer or checksums.");

            WriteStatus("downloading", release.tag_name,
                "Update " + release.tag_name + " downloaden...");
            string checksumText = Encoding.UTF8.GetString(
                DownloadAsset(checksums));
            string expectedHash = FindExpectedHash(
                checksumText, InstallerAsset);
            byte[] installerBytes = DownloadAsset(installer);
            string actualHash = ComputeSha256(installerBytes);
            if (!String.Equals(expectedHash, actualHash,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The installer checksum does not match.");

            string temporaryInstaller = Path.Combine(
                Path.GetTempPath(), "JvdP-Light-Update-" +
                release.tag_name + ".exe");
            File.WriteAllBytes(temporaryInstaller, installerBytes);
            File.WriteAllText(PendingReleaseTagPath, release.tag_name,
                new UTF8Encoding(false));
            WriteStatus("installing", release.tag_name,
                "Update " + release.tag_name + " installeren...");
            Log("Installing release " + release.tag_name +
                " from channel " + channel + ".");

            Process.Start(new ProcessStartInfo {
                FileName = temporaryInstaller,
                Arguments = "--quiet",
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath()
            });
            return true;
        }

        private static GitHubRelease GetRelease(string channel)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            string baseUrl = "https://api.github.com/repos/" +
                Repository + "/releases";
            if (channel == "stable")
                return serializer.Deserialize<GitHubRelease>(
                    DownloadText(baseUrl + "/latest"));

            GitHubRelease[] releases =
                serializer.Deserialize<GitHubRelease[]>(
                    DownloadText(baseUrl + "?per_page=20"));
            foreach (GitHubRelease release in releases)
                if (!release.draft)
                    return release;
            return null;
        }

        private static GitHubAsset FindAsset(
            GitHubRelease release, string name)
        {
            if (release.assets == null)
                return null;
            foreach (GitHubAsset asset in release.assets)
                if (String.Equals(asset.name, name,
                    StringComparison.OrdinalIgnoreCase))
                    return asset;
            return null;
        }

        private static string DownloadText(string url)
        {
            return Encoding.UTF8.GetString(
                Download(url, "application/vnd.github+json"));
        }

        private static byte[] DownloadAsset(GitHubAsset asset)
        {
            return Download(asset.url, "application/octet-stream");
        }

        private static byte[] Download(string url, string accept)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "JvdP-AutoUpdater/" + BuildInfo.Version;
            request.Accept = accept;
            request.AutomaticDecompression =
                DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (HttpWebResponse response =
                (HttpWebResponse)request.GetResponse())
            using (Stream input = response.GetResponseStream())
            using (MemoryStream output = new MemoryStream())
            {
                input.CopyTo(output);
                return output.ToArray();
            }
        }

        private static string FindExpectedHash(
            string checksumText, string fileName)
        {
            foreach (string rawLine in checksumText.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                int separator = line.IndexOfAny(new[] { ' ', '\t' });
                if (separator <= 0)
                    continue;
                string hash = line.Substring(0, separator).Trim();
                string name = line.Substring(separator).Trim().TrimStart('*');
                if (String.Equals(name, fileName,
                    StringComparison.OrdinalIgnoreCase) &&
                    Regex.IsMatch(hash, "^[0-9a-fA-F]{64}$"))
                    return hash;
            }
            throw new InvalidDataException(
                "No valid checksum was published for " + fileName + ".");
        }

        private static string ComputeSha256(byte[] content)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(content);
                StringBuilder text = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash)
                    text.Append(value.ToString("x2"));
                return text.ToString();
            }
        }

        private static Version ParseVersion(string tag)
        {
            string value = tag.Trim().TrimStart('v', 'V');
            int suffix = value.IndexOfAny(new[] { '-', '+' });
            if (suffix >= 0)
                value = value.Substring(0, suffix);
            Version parsed;
            return Version.TryParse(value, out parsed) ? parsed : null;
        }

        private static void WriteStatus(
            string state, string availableVersion, string message)
        {
            try
            {
                Directory.CreateDirectory(LocalRoot);
                string content =
                    "State=" + SanitizeStatusValue(state) +
                    Environment.NewLine +
                    "Checked=" + DateTime.UtcNow.ToString("o") +
                    Environment.NewLine +
                    "InstalledVersion=" + BuildInfo.Version +
                    Environment.NewLine +
                    "AvailableVersion=" +
                    SanitizeStatusValue(availableVersion) +
                    Environment.NewLine +
                    "Message=" + SanitizeStatusValue(message) +
                    Environment.NewLine;
                File.WriteAllText(StatusPath, content,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        private static string SanitizeStatusValue(string value)
        {
            return (value ?? "").Replace("\r", " ").Replace("\n", " ");
        }

        private static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(LocalRoot);
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("s") + "  " + message +
                    Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
