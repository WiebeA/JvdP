using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Jvdp.Reliability;

namespace Jvdp.LightDarkroomOverlay
{
    internal sealed partial class OverlayForm
    {
        private int LoadStabilitySeconds()
        {
            try
            {
                int value;
                if (File.Exists(stabilityPath) &&
                    Int32.TryParse(
                        File.ReadAllText(stabilityPath).Trim(), out value))
                    return Math.Max(5, Math.Min(300, value));
            }
            catch { }
            return DefaultStabilitySeconds;
        }

        private void SaveStabilitySeconds()
        {
            if (layoutTestMode || boothSettings == null) return;
            try
            {
                BoothSettings settings = CaptureBoothSettings();
                settings.Revision++; settings.Save(settingsPath); boothSettings = settings;
                lock (sensor.Sync) sensor.CandidateSince = sensor.MappedIso > 0 ? DateTime.Now : DateTime.MinValue;
            }
            catch (Exception error)
            {
                stabilitySeconds = boothSettings.Stability;
                stabilitySecondsInput.Value = stabilitySeconds;
                MessageBox.Show(this, "Wachttijd niet opgeslagen: " + error.Message, "Opslaan mislukt");
            }
        }

        private bool LoadAutomaticAdjustmentStopped()
        {
            try
            {
                return File.Exists(automaticStatePath) &&
                    File.ReadAllText(automaticStatePath).Trim().Equals(
                        "stopped", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void SaveAutomaticAdjustmentState()
        {
            if (!layoutTestMode) ReliableFiles.Write(automaticStatePath, paused ? "stopped" : "started");
        }

        private void LoadCoverTextSettings()
        {
            try
            {
                if (!File.Exists(coverTextPath))
                    return;
                foreach (string source in File.ReadAllLines(coverTextPath))
                {
                    string line = source.Trim();
                    if (line.Equals("mode=custom",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        useCustomCoverText = true;
                    }
                    else if (line.StartsWith("title=",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        customCoverTitle = DecodeSettingText(
                            line.Substring(6), DefaultCoverTitle);
                    }
                    else if (line.StartsWith("message=",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        customCoverMessage = DecodeSettingText(
                            line.Substring(8), DefaultCoverMessage);
                    }
                }
            }
            catch (Exception exception)
            {
                Log("Fullpage text settings could not be loaded: " +
                    exception.Message);
            }
        }

        private static string EncodeSettingText(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
        }

        private static string DecodeSettingText(
            string value, string fallback)
        {
            try
            {
                string decoded = Encoding.UTF8.GetString(
                    Convert.FromBase64String(value));
                return String.IsNullOrWhiteSpace(decoded) ? fallback : decoded;
            }
            catch
            {
                return fallback;
            }
        }

        private string GetActiveCoverTitle()
        {
            lock (coverTextSync)
                return useCustomCoverText
                    ? customCoverTitle
                    : DefaultCoverTitle;
        }

        private string GetActiveCoverMessage()
        {
            lock (coverTextSync)
                return useCustomCoverText
                    ? customCoverMessage
                    : DefaultCoverMessage;
        }

        private static List<IsoBand> CreateDefaultIsoBands()
        {
            return new List<IsoBand> {
                new IsoBand(16, 3200),
                new IsoBand(33, 2500),
                new IsoBand(50, 1600),
                new IsoBand(67, 1000),
                new IsoBand(84, 640),
                new IsoBand(100, 400)
            };
        }

        private void LoadIsoMappingSettings()
        {
            try
            {
                if (!File.Exists(mappingPath))
                    return;
                string[] lines = File.ReadAllLines(mappingPath);
                bool customMode = false;
                List<IsoBand> loaded = new List<IsoBand>();
                foreach (string source in lines)
                {
                    string line = source.Trim();
                    if (line.Equals("mode=custom",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        customMode = true;
                        continue;
                    }
                    if (!line.StartsWith("band=",
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    string[] parts = line.Substring(5).Split(',');
                    int maximum;
                    int iso;
                    if (parts.Length == 2 &&
                        Int32.TryParse(parts[0], out maximum) &&
                        Int32.TryParse(parts[1], out iso))
                        loaded.Add(new IsoBand(maximum, iso));
                }
                if (IsoBandsAreValid(loaded))
                    customIsoBands = loaded;
                useCustomMapping = customMode;
            }
            catch (Exception exception)
            {
                Log("ISO mapping settings could not be loaded: " +
                    exception.Message);
            }
        }

        private static bool IsoBandsAreValid(List<IsoBand> bands)
        {
            if (bands == null || bands.Count < 1 || bands.Count > 8)
                return false;
            int previous = -1;
            foreach (IsoBand band in bands)
            {
                if (band.MaximumLight <= previous ||
                    band.MaximumLight > 100 ||
                    Array.IndexOf(SupportedIsoValues, band.Iso) < 0)
                    return false;
                previous = band.MaximumLight;
            }
            return previous == 100;
        }

        private List<IsoBand> GetActiveIsoBands()
        {
            lock (mappingSync)
            {
                List<IsoBand> source = useCustomMapping
                    ? customIsoBands
                    : CreateDefaultIsoBands();
                List<IsoBand> copy = new List<IsoBand>();
                foreach (IsoBand band in source)
                    copy.Add(band.Clone());
                return copy;
            }
        }

        private int MapLightToIso(int light)
        {
            List<IsoBand> bands = GetActiveIsoBands();
            lock (sensor.Sync) return lightRegulation.Map(light, bands);
        }

        private void RecalculateMappedIso()
        {
            int light;
            lock (sensor.Sync)
            {
                light = sensor.Light;
                if (light < 0)
                {
                    sensor.MappedIso = -1;
                    sensor.CandidateSince = DateTime.MinValue;
                    return;
                }
            }
            lock (sensor.Sync) lightRegulation.Reset();
            int mappedIso = MapLightToIso(light);
            lock (sensor.Sync)
            {
                if (sensor.Light != light)
                    return;
                if (sensor.MappedIso != mappedIso)
                {
                    sensor.MappedIso = mappedIso;
                    sensor.CandidateSince = DateTime.Now;
                    Log("PC mapping changed target ISO to " + mappedIso +
                        " at light " + light + ".");
                }
            }
        }

        private string BuildMappingSummary()
        {
            List<IsoBand> bands = GetActiveIsoBands();
            List<string> parts = new List<string>();
            int lower = 0;
            foreach (IsoBand band in bands)
            {
                parts.Add(lower + "–" + band.MaximumLight +
                    " → ISO " + band.Iso);
                lower = band.MaximumLight + 1;
            }
            return String.Join("   ·   ", parts.ToArray());
        }

        private void UpdateMappingControls()
        {
            if (mappingModeInput == null || mappingSummaryLabel == null)
                return;

            mappingModeInput.SelectedIndex = useCustomMapping ? 1 : 0;
            mappingSummaryLabel.Text =
                (useCustomMapping
                    ? "Eigen profiel voor " + BoothDisplayName
                    : "Standaardprofiel voor alle booths") +
                Environment.NewLine + BuildMappingSummary();
            if (profileNameLabel != null)
                profileNameLabel.Text = useCustomMapping
                    ? "Eigen · " + BoothDisplayName
                    : "Standaard · alle booths";
            mappingSettingsButton.Text = useCustomMapping
                ? "Profiel wijzigen"
                : "Profiel bekijken";
            if (rangeControl != null)
            {
                int light;
                int iso;
                lock (sensor.Sync)
                {
                    light = sensor.Light;
                    iso = sensor.MappedIso;
                }
                rangeControl.UpdateData(GetActiveIsoBands(), light, iso);
            }
        }

        private void ShowIsoMappingSettings()
        {
            ShowMainWindow();
            if (settingsPage != null && !settingsPage.IsDisposed)
            {
                settingsPage.BringToFront();
                settingsPage.Focus();
                return;
            }

            List<IsoBand> custom;
            bool customMode;
            lock (mappingSync)
            {
                customMode = useCustomMapping;
                custom = new List<IsoBand>();
                foreach (IsoBand band in customIsoBands)
                    custom.Add(band.Clone());
            }

            IsoMappingForm page = new IsoMappingForm(
                customMode, custom, CreateDefaultIsoBands(),
                SupportedIsoValues, BoothDisplayName,
                stabilitySeconds, useCustomCoverText,
                customCoverTitle, customCoverMessage,
                DefaultCoverTitle, DefaultCoverMessage);
            page.Persist = delegate { return PersistProfilePage(page); };
            settingsPage = page;
            page.TopLevel = false;
            page.FormBorderStyle = FormBorderStyle.None;
            int contentTop = mainHeaderPanel.Bottom;
            int contentBottom = versionFooterLabel.Top;
            page.SetBounds(0, contentTop, ClientSize.Width,
                Math.Max(1, contentBottom - contentTop));
            page.Anchor = AnchorStyles.Top | AnchorStyles.Bottom |
                AnchorStyles.Left | AnchorStyles.Right;
            page.FormClosed += delegate
            {
                if (page.DialogResult == DialogResult.OK)
                {
                    lock (mappingSync)
                    {
                        useCustomMapping = page.UseCustomMapping;
                        customIsoBands = page.CustomBands;
                    }
                    stabilitySeconds = page.StabilitySeconds;
                    lock (coverTextSync)
                    {
                        useCustomCoverText = page.UseCustomCoverText;
                        customCoverTitle = page.CustomCoverTitle;
                        customCoverMessage = page.CustomCoverMessage;
                    }
                    if (stabilitySecondsInput.Value != stabilitySeconds)
                        stabilitySecondsInput.Value = stabilitySeconds;
                    // The complete profile was committed atomically before this page closed.
                    RecalculateMappedIso();
                    UpdateMappingControls();
                    RefreshUi();
                    Log("Settings saved; profile=" +
                        (useCustomMapping ? "custom" : "default") +
                        "; booth=" + BoothDisplayName +
                        "; stability=" + stabilitySeconds + " seconds" +
                        "; coverText=" +
                        (useCustomCoverText ? "custom" : "default") + ".");
                }
                settingsPage = null;
                dashboard.Visible = true;
                settingsButton.Text = "Instellingen";
                settingsButton.AccessibleName =
                    "Profielinstellingen openen";
            };
            // Creating the embedded form can briefly trigger a DPI auto-scale
            // pass on its siblings. Keep the header off-screen during that
            // pass, then publish only the final measured layout.
            mainHeaderPanel.Visible = false;
            try
            {
                Controls.Add(page);
                dashboard.Visible = false;
                settingsButton.Text = "Terug naar dashboard";
                settingsButton.AccessibleName = "Terug naar dashboard";
                page.BringToFront();
                page.Show();
                page.Focus();
                LayoutMainChrome();
            }
            finally
            {
                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        if (IsDisposed)
                            return;
                        LayoutMainChrome();
                        mainHeaderPanel.Visible = true;
                        mainHeaderPanel.BringToFront();
                        mainHeaderPanel.Refresh();
                    }));
                }
                catch
                {
                    mainHeaderPanel.Visible = true;
                }
            }
        }

    }
}
