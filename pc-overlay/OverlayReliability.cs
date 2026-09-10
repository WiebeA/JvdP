using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Jvdp.Reliability;

namespace Jvdp.LightDarkroomOverlay
{
    internal sealed partial class OverlayForm
    {
        private readonly LightRegulation lightRegulation = new LightRegulation();
        private readonly IsoConfirmation isoConfirmation = new IsoConfirmation();
        private BoothSettings boothSettings;
        private string BoothDisplayName { get { return boothSettings == null ? Environment.MachineName : boothSettings.Name; } }
        private string settingsPath;
        private string localDirectory;
        private volatile string automaticFault;
        private int automaticFailures;
        private volatile bool maintenanceMode;
        private volatile bool initialPreparationDone;
        private volatile string darkroomIdentity = "";
        private volatile string darkroomVersion = "";
        private volatile bool richProtocol;
        private string sensorId = "unknown", sensorFirmware = "legacy";
        private uint lastSensorSequence, lastSensorUptime;
        private long lastSensorTick;
        private SensorReading lastRawReading;
        private string rememberedPort = "";
        private readonly Dictionary<string, int> serialFailures = new Dictionary<string, int>();
        private readonly List<LightHistoryPoint> history = new List<LightHistoryPoint>();

        private void InitializeReliability(string root)
        {
            localDirectory = root;
            settingsPath = Path.Combine(root, "booth-profile.json");
            maintenanceMode = !layoutTestMode && BoothCoordination.HasMaintenance(root, DateTime.UtcNow);
            boothSettings = new BoothSettings { Bands = customIsoBands, Stability = stabilitySeconds,
                CustomMapping = useCustomMapping, CustomCover = useCustomCoverText,
                CoverTitle = customCoverTitle, CoverMessage = customCoverMessage };
            if (layoutTestMode) return;
            try
            {
                if (File.Exists(settingsPath)) ApplyBoothSettings(BoothSettings.Parse(File.ReadAllText(settingsPath)));
                else boothSettings.Save(settingsPath);
                string portPath = Path.Combine(root, "last-sensor-port.txt");
                if (File.Exists(portPath)) rememberedPort = File.ReadAllText(portPath).Trim();
            }
            catch (Exception error) { automaticFault = "Profiel niet geladen of opgeslagen: " + error.Message; Log(automaticFault); }
        }

        private void ApplyBoothSettings(BoothSettings settings)
        {
            settings.Validate();
            lock (mappingSync) { useCustomMapping = settings.CustomMapping; customIsoBands = settings.Bands; }
            lock (sensor.Sync)
            {
                lightRegulation.DarkRaw = settings.DarkRaw; lightRegulation.BrightRaw = settings.BrightRaw;
                lightRegulation.Margin = settings.Margin; lightRegulation.MaximumIso = settings.MaximumIso;
                lightRegulation.Reset(); sensor.CheckCycle.Invalidate();
            }
            stabilitySeconds = settings.Stability;
            useCustomCoverText = settings.CustomCover; customCoverTitle = settings.CoverTitle; customCoverMessage = settings.CoverMessage;
            boothSettings = settings;
            isoConfirmation.Clear();
        }

        private BoothSettings CaptureBoothSettings()
        {
            BoothSettings settings = boothSettings.Clone();
            lock (mappingSync) { settings.CustomMapping = useCustomMapping; settings.Bands = new List<IsoBand>(customIsoBands); }
            settings.Stability = stabilitySeconds;
            settings.CustomCover = useCustomCoverText; settings.CoverTitle = customCoverTitle; settings.CoverMessage = customCoverMessage;
            return settings;
        }

        private bool PersistProfilePage(IsoMappingForm page)
        {
            try
            {
                if (manualActionRunning) throw new InvalidOperationException("Wacht tot de ISO-aanpassing klaar is voordat je het profiel opslaat.");
                BoothSettings next = CaptureBoothSettings();
                next.CustomMapping = page.UseCustomMapping; next.Bands = page.CustomBands;
                next.Stability = page.StabilitySeconds; next.CustomCover = page.UseCustomCoverText;
                next.CoverTitle = page.CustomCoverTitle; next.CoverMessage = page.CustomCoverMessage;
                next.Revision++;
                next.Save(settingsPath);
                ApplyBoothSettings(next);
                return true;
            }
            catch (Exception error)
            {
                Log("Settings not saved: " + error.Message);
                MessageBox.Show(page, "Opslaan is niet gelukt. Het vorige profiel blijft actief.\r\n" + error.Message,
                    "Profiel niet opgeslagen", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private bool SensorIsFresh()
        {
            long tick = Interlocked.Read(ref lastSensorTick);
            return tick != 0 && (Stopwatch.GetTimestamp() - tick) / (double)Stopwatch.Frequency <= 5;
        }

        private string GetActionBlockReason(bool automatic)
        {
            if (layoutTestMode) return null;
            if (maintenanceMode) return "Onderhoud actief; ISO-aanpassingen staan stil.";
            if (automatic && automaticFault != null) return automaticFault;
            if (!SensorIsFresh()) return "Wachten op verse sensormetingen.";
            if (!darkroomRunning) return "Start Darkroom om de camera voor te bereiden.";
            string session = BoothCoordination.ReadSession(localDirectory, darkroomIdentity, DateTime.UtcNow);
            if (session == "busy") return "Fotosessie bezig; de ISO-aanpassing wacht.";
            // Upgrading an existing booth must preserve its automatic regulation.
            // Requiring events is an explicit choice after configuring Darkroom.
            if (!boothSettings.RequireSessionSignals) return null;
            if (!initialPreparationDone && !boothMode) return null;
            if (session != "idle") return "Wachten op een bevestigd rustmoment. Controleer de sessiekoppeling.";
            return null;
        }

        private string GetCompatibilityStatus()
        {
            if (String.IsNullOrWhiteSpace(darkroomVersion)) return "Darkroom-versie nog niet beschikbaar.";
            if (boothSettings.AllowUntestedDarkroom && boothSettings.TestedDarkroomVersion == darkroomVersion)
                return "Darkroom " + darkroomVersion + ": praktijktest bevestigd voor deze booth.";
            if (IsVerifiedDarkroom(darkroomVersion))
                return "Darkroom " + darkroomVersion + ": opgenomen in de navigatiecontrole.";
            return "Darkroom " + darkroomVersion + ": praktijktest nog niet vastgelegd. Dit blokkeert de regeling niet; de app controleert de bediening bij elke ISO-actie.";
        }
        internal static bool IsVerifiedDarkroom(string version)
        {
            Version value;
            return Version.TryParse(version, out value) && value.Major == 3 && value.Minor == 1 && value.Build == 1434;
        }

        private void WriteStartupHealth()
        {
            if (layoutTestMode) return;
            // A queued timer tick proves the new UI message loop is running.
            System.Windows.Forms.Timer health = new System.Windows.Forms.Timer { Interval = 1000 };
            health.Tick += delegate
            {
                health.Stop(); health.Dispose();
                try
                {
                    string tokenPath = Path.Combine(localDirectory, "update-health-token.txt");
                    if (automaticFault == null && File.Exists(tokenPath))
                        ReliableFiles.Write(Path.Combine(localDirectory, "update-health-ok.txt"),
                            File.ReadAllText(tokenPath).Trim() + "\n" + BuildInfo.Version + "\n" + Process.GetCurrentProcess().Id);
                }
                catch (Exception error) { Log("Startup health write failed: " + error.Message); }
            };
            health.Start();
        }

        private void ResetSerialBackoff()
        {
            lock (serialRetryAfter) serialRetryAfter.Clear();
            lock (serialFailures) serialFailures.Clear();
        }
        private int SerialRetrySeconds(string port)
        {
            lock (serialFailures)
            {
                int count; serialFailures.TryGetValue(port, out count);
                serialFailures[port] = Math.Min(5, count + 1);
                return Math.Min(300, 20 * (1 << Math.Min(4, count)));
            }
        }
        private void PrioritizeRememberedPort(List<string> ports)
        {
            if (ports.Remove(rememberedPort)) ports.Insert(0, rememberedPort);
        }
        private void RememberSerialPort(string port)
        {
            if (rememberedPort == port) return;
            rememberedPort = port;
            try { ReliableFiles.Write(Path.Combine(localDirectory, "last-sensor-port.txt"), port); }
            catch (Exception error) { Log("Sensor port could not be saved: " + error.Message); }
        }

        private void RecordHistory(string message)
        {
            int light, target;
            lock (sensor.Sync) { light = sensor.Light; target = sensor.MappedIso; }
            lock (history)
            {
                if (message.Length == 0 && history.Count > 0 && (DateTime.Now - history[history.Count - 1].At).TotalSeconds < 0.8) return;
                history.Add(new LightHistoryPoint { At = DateTime.Now, Light = light, Target = target, Confirmed = currentDarkroomIso, Note = message });
                while (history.Count > 900) history.RemoveAt(0);
            }
        }
        private List<LightHistoryPoint> HistorySnapshot() { lock (history) return new List<LightHistoryPoint>(history); }

        private string ExportDiagnostics()
        {
            string folder = Path.Combine(localDirectory, "diagnostics", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(folder);
            BoothSettings settings = CaptureBoothSettings();
            ReliableFiles.Write(Path.Combine(folder, "booth-profile.json"), settings.Serialize());
            ReliableFiles.Write(Path.Combine(folder, "status.txt"),
                "BoothId=" + settings.BoothId + "\nBooth=" + settings.Name + "\nApp=" + BuildInfo.Version +
                "\nDarkroom=" + darkroomVersion + "\nSensor=" + sensorId + "\nFirmware=" + sensorFirmware +
                "\nPort=" + activeSerialPort + "\nLastMeasurement=" + lastJvdpLineAt.ToString("o") +
                "\nConfirmedISO=" + currentDarkroomIso + "\nISOChecked=" + currentIsoReadAt.ToString("o") +
                "\nSession=" + BoothCoordination.ReadSession(localDirectory, darkroomIdentity, DateTime.UtcNow) +
                "\nRequireSessionSignals=" + boothSettings.RequireSessionSignals +
                "\nCompatibility=" + GetCompatibilityStatus() +
                "\nFault=" + automaticFault);
            foreach (string name in new[] { "overlay.log", "overlay.log.1", "updater.log", "updater-status.txt", "startup.log", "installation.txt" })
                if (File.Exists(Path.Combine(localDirectory, name))) File.Copy(Path.Combine(localDirectory, name), Path.Combine(folder, name));
            StringBuilder csv = new StringBuilder("time,light,target,confirmed,note\n");
            foreach (LightHistoryPoint point in HistorySnapshot())
                csv.AppendLine(point.At.ToString("o") + "," + point.Light + "," + point.Target + "," + point.Confirmed + ",\"" + point.Note.Replace("\"", "\"\"") + "\"");
            ReliableFiles.Write(Path.Combine(folder, "light-history.csv"), csv.ToString());
            ZipFile.CreateFromDirectory(folder, folder + ".zip");
            return folder + ".zip";
        }

        private void ShowReliabilityTools()
        {
            using (ReliabilityForm form = new ReliabilityForm(CaptureBoothSettings(), HistorySnapshot,
                delegate { lock (sensor.Sync) return lastRawReading == null ? -1 : lastRawReading.Raw; },
                delegate { return "Sensor " + sensorId + " · firmware " + sensorFirmware + " · Darkroom " + darkroomVersion +
                    "\r\nDoel " + sensor.MappedIso + " · bevestigd " + currentDarkroomIso + " om " + currentIsoReadAt.ToString("HH:mm:ss") +
                    "\r\n" + (GetActionBlockReason(true) ?? "Gereed om aan te passen") +
                    "\r\n" + (boothSettings.RequireSessionSignals ? "Sessiekoppeling verplicht; rustsignaal maximaal 30 seconden geldig." : "Normale automatische regeling; rustsignaal niet verplicht.") +
                    "\r\n" + GetCompatibilityStatus(); }))
            {
                form.SaveSettings = delegate(BoothSettings settings)
                {
                    if (manualActionRunning) throw new InvalidOperationException("Wacht tot de ISO-aanpassing klaar is.");
                    settings.BoothId = boothSettings.BoothId;
                    settings.TestedDarkroomVersion = settings.AllowUntestedDarkroom ? darkroomVersion : "";
                    settings.Revision = boothSettings.Revision + 1;
                    settings.Save(settingsPath); ApplyBoothSettings(settings);
                    stabilitySecondsInput.Value = settings.Stability;
                    UpdateMappingControls(); RefreshUi();
                };
                form.Diagnose = ExportDiagnostics;
                form.Recover = delegate { if (manualActionRunning) throw new InvalidOperationException("Wacht tot de ISO-aanpassing klaar is.");
                    automaticFault = null; automaticFailures = 0; isoConfirmation.Clear();
                    lock (sensor.Sync) sensor.CheckCycle.Invalidate(); ResetSerialBackoff(); };
                form.Idle = delegate
                {
                    if (manualActionRunning || !darkroomRunning) throw new InvalidOperationException("Wacht tot Darkroom gereed is.");
                    using (OperationLease lease = OperationLease.TryAcquire(0))
                    {
                        if (lease == null) throw new InvalidOperationException("Er loopt een actie.");
                        ReliableFiles.Write(Path.Combine(localDirectory, "session-state.txt"), darkroomIdentity + "\nidle\n" + DateTime.UtcNow.ToString("o"));
                    }
                };
                form.Maintenance = delegate(bool enable)
                {
                    if (manualActionRunning) throw new InvalidOperationException("Wacht tot de ISO-aanpassing klaar is.");
                    if (enable && darkroomRunning) throw new InvalidOperationException("Sluit Darkroom na afloop van het evenement voordat je onderhoud start.");
                    ReliableFiles.Write(Path.Combine(localDirectory, "maintenance-until.txt"), (enable ? DateTime.UtcNow.AddHours(1) : DateTime.UtcNow).ToString("o"));
                    maintenanceMode = enable;
                    form.Text = enable ? "Kalibratie en diagnose — onderhoud actief" : "Kalibratie en diagnose";
                    if (enable)
                    {
                        string updater = Path.Combine(localDirectory, "JvdpAutoUpdater.exe");
                        if (File.Exists(updater)) Process.Start(new ProcessStartInfo { FileName = updater, Arguments = "--check-now", UseShellExecute = false, CreateNoWindow = true });
                    }
                };
                form.ShowDialog(this);
            }
        }
    }

    internal sealed class LightHistoryPoint
    {
        internal DateTime At;
        internal int Light, Target;
        internal string Confirmed, Note;
    }
}
