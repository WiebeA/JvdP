using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Jvdp.LightDarkroomOverlay;
using Jvdp.LightDarkroomInstaller;
using Jvdp.Reliability;

internal static class ReliabilityTests
{
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    private static int assertions;
    private static void Check(bool condition, string text)
    {
        assertions++;
        if (!condition) throw new Exception(text);
    }
    private static void Reject(Action action, string text)
    {
        bool rejected = false; try { action(); } catch { rejected = true; }
        Check(rejected, text);
    }
    private static void Set(object target, string name, object value)
    { target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).SetValue(target, value); }
    private static int Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
        long tick = Stopwatch.Frequency;
        LightCheckCycle cycle = new LightCheckCycle(delegate { return tick; });
        cycle.TargetIso = 1600; cycle.StartedAt = DateTime.Now;
        tick += 20 * Stopwatch.Frequency;
        Check(cycle.ElapsedSeconds(60) == 20, "Monotonic stability after 20 seconds");
        DateTime checkedSince = cycle.StartedAt;
        cycle.Invalidate(); tick += 90 * Stopwatch.Frequency;
        cycle.TargetIso = 1600; cycle.StartedAt = DateTime.Now;
        Check(cycle.ElapsedSeconds(60) == 0, "Same target after gap starts a full new period");
        Check(!cycle.CompleteCheck(1600, checkedSince.AddDays(-1), DateTime.Now), "Old action cannot reset a new cycle");
        tick += 60 * Stopwatch.Frequency;
        Check(cycle.ElapsedSeconds(60) == 60, "Wall-clock date is not needed for elapsed time");

        object overlay = FormatterServices.GetUninitializedObject(typeof(OverlayForm));
        SensorState sensor = new SensorState { Light = 40, MappedIso = 1600, CandidateSince = DateTime.Now.AddMinutes(-2) };
        Set(overlay, "serialSync", new object()); Set(overlay, "sensor", sensor); Set(overlay, "lightRegulation", new LightRegulation());
        Set(overlay, "serialReady", true);
        typeof(OverlayForm).GetMethod("CloseSerial", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(overlay, null);
        Check(sensor.Light == -1 && sensor.MappedIso == -1 && sensor.CandidateSince == DateTime.MinValue,
            "Real CloseSerial invalidates light, target and stability even without an open port");

        IsoConfirmation iso = new IsoConfirmation(delegate { return tick; });
        iso.Confirm(42, 1600, "1600"); Check(iso.Matches(42, 1600), "Confirmed requested ISO");
        iso.Observe(42, "800"); Check(!iso.Matches(42, 1600), "New ISO 800 invalidates old target 1600");
        Check(iso.Matches(42, 800), "Fresh native reading is usable");
        iso.Confirm(42, 2500, "3200"); Check(iso.Matches(42, 2500), "Supported substitute avoids repeated adjustments");
        iso.Observe(42, "3200"); Check(iso.Matches(42, 2500), "Matching observation preserves substitute");
        Check(!iso.Matches(43, 2500), "Process change invalidates cache");
        tick += 901 * Stopwatch.Frequency; Check(!iso.Matches(42, 2500), "Cache expires for periodic safe verification");

        SensorReading reading;
        Check(SensorReading.TryParse("JVDP|light=16", out reading) && !reading.Extended, "Legacy protocol");
        Check(SensorReading.TryParse("JVDP|light=16|iso=3200", out reading), "Legacy ISO field ignored");
        Check(SensorReading.TryParse("JVDP2|light=16|raw=660|id=AB12|fw=24.6.0|seq=4|uptime=4500", out reading) && reading.Raw == 660, "Versioned protocol");
        foreach (string bad in new[] { "JVDP|light=101", "JVDP|light=-1", "JVDP|light=3|light=4", "JVDP2|light=16",
            "JVDP2|light=16|raw=5000|id=AB|fw=1|seq=1|uptime=0", new string('x', 1000), "unrelated serial device" })
            Check(!SensorReading.TryParse(bad, out reading), "Reject invalid frame: " + bad.Substring(0, Math.Min(40, bad.Length)));
        List<IsoBand> bands = new List<IsoBand> { new IsoBand(16, 3200), new IsoBand(100, 2500) };
        LightRegulation policy = new LightRegulation { Margin = 2 };
        Check(policy.Map(16, bands) == 3200, "Initial band");
        for (int i = 0; i < 20; i++) Check(policy.Map(i % 2 == 0 ? 17 : 16, bands) == 3200, "Boundary noise is held");
        Check(policy.Map(19, bands) == 2500, "Sustained value beyond margin changes band");
        Check(policy.Map(15, bands) == 2500 && policy.Map(14, bands) == 3200, "Reverse margin");
        policy.DarkRaw = 1000; policy.BrightRaw = 3000;
        Check(policy.Calibrate(new SensorReading { Raw = 2000 }) == 50, "Calibrated midpoint");
        Check(policy.Calibrate(new SensorReading { Raw = 0 }) == 0 && policy.Calibrate(new SensorReading { Raw = 4095 }) == 100, "Calibration clamps extremes");
        policy.DarkRaw = 3000; policy.BrightRaw = 1000;
        Check(policy.Calibrate(new SensorReading { Raw = 1000 }) == 100, "Reverse sensor polarity");
        policy.MaximumIso = 1600; Check(policy.Map(0, bands) == 1600, "Maximum ISO respected");
        ComboItem limited = (ComboItem)typeof(OverlayForm).GetMethod("FindClosestNumericIso", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { new List<ComboItem> { new ComboItem(0, "800"), new ComboItem(1, "2000") }, 1600, 1600 });
        Check(limited.Value == "800", "Camera fallback cannot exceed configured maximum ISO");

        BoothSettings settings = new BoothSettings { Name = "Booth café", Bands = bands };
        string profile = Path.Combine(root, "profile.json"); settings.Save(profile);
        Check(BoothSettings.Parse(File.ReadAllText(profile)).Name == "Booth café", "Profile JSON round-trip with accents");
        string original = File.ReadAllText(profile);
        string legacyProfile = original.Replace(",\"RequireSessionSignals\":false", "");
        Check(!legacyProfile.Contains("RequireSessionSignals") && !BoothSettings.Parse(legacyProfile).RequireSessionSignals,
            "Existing profiles restore normal automatic regulation without new configuration");
        BoothSettings linkedProfile = BoothSettings.Parse(original); linkedProfile.RequireSessionSignals = true;
        Check(BoothSettings.Parse(linkedProfile.Serialize()).RequireSessionSignals, "Explicit session integration survives profile export/import");
        settings.BrightRaw = settings.DarkRaw;
        Reject(delegate { settings.Save(profile); }, "Invalid calibration rejected before writing");
        Check(File.ReadAllText(profile) == original, "Failed save preserves previous profile");
        Reject(delegate { ReliableFiles.Write(root, "not a file"); }, "Unwritable destination reports failure");
        Reject(delegate { BoothSettings.Parse("{}"); }, "Missing mapping rejected");

        DateTime now = DateTime.UtcNow;
        string identity = "42:1234";
        Check(BoothCoordination.ReadSession(root, identity, now) == "unknown", "Missing session not idle");
        ReliableFiles.Write(Path.Combine(root, "session-state.txt"), identity + "\nidle\n" + now.ToString("o"));
        Check(BoothCoordination.ReadSession(root, identity, now.AddSeconds(29)) == "idle", "Fresh idle accepted");
        Check(BoothCoordination.ReadSession(root, identity, now.AddSeconds(31)) == "unknown", "Stale idle not accepted");
        Check(BoothCoordination.ReadSession(root, "different", now) == "unknown", "Old process session ignored");
        Check(BoothCoordination.ReadSession(root, identity, now.AddSeconds(-1)) == "unknown", "Future timestamp not accepted");
        ReliableFiles.Write(Path.Combine(root, "session-state.txt"), identity + "\nbusy\n" + now.ToString("o"));
        Check(BoothCoordination.ReadSession(root, identity, now.AddHours(1)) == "busy", "Timeout never turns busy into idle");
        Set(overlay, "localDirectory", root); Set(overlay, "darkroomIdentity", identity);
        Set(overlay, "darkroomRunning", true); Set(overlay, "darkroomVersion", "3.01.1434.0");
        Set(overlay, "lastSensorTick", Stopwatch.GetTimestamp());
        BoothSettings activeSettings = BoothSettings.Parse(original);
        Set(overlay, "boothSettings", activeSettings);
        MethodInfo gate = typeof(OverlayForm).GetMethod("GetActionBlockReason", BindingFlags.Instance | BindingFlags.NonPublic);
        Check(gate.Invoke(overlay, new object[] { true }) != null, "Busy session blocks even initial preparation");
        File.Delete(Path.Combine(root, "session-state.txt"));
        Set(overlay, "boothMode", true); Set(overlay, "initialPreparationDone", true);
        foreach (string version in new[] { "2.01.1354", "2.01.1354.0", "3.01.1434.0", "3.1.9999", "" })
        {
            Set(overlay, "darkroomVersion", version);
            Check(gate.Invoke(overlay, new object[] { true }) == null, "Automatic regulation without idle signal for Darkroom " + version);
            Check(gate.Invoke(overlay, new object[] { false }) == null, "Manual regulation without idle signal for Darkroom " + version);
        }
        Set(overlay, "darkroomVersion", "2.01.1354");
        MethodInfo compatibility = typeof(OverlayForm).GetMethod("GetCompatibilityStatus", BindingFlags.Instance | BindingFlags.NonPublic);
        Check(((string)compatibility.Invoke(overlay, null)).Contains("praktijktest nog niet vastgelegd"), "Unknown version is not falsely marked tested");
        activeSettings.RequireSessionSignals = true;
        Check(gate.Invoke(overlay, new object[] { true }) != null, "Explicit session integration blocks missing idle");
        Check(gate.Invoke(overlay, new object[] { false }) != null, "Explicit session integration also guards manual actions");
        ReliableFiles.Write(Path.Combine(root, "session-state.txt"), identity + "\nidle\n" + DateTime.UtcNow.ToString("o"));
        Set(overlay, "boothMode", true);
        Check(gate.Invoke(overlay, new object[] { true }) == null, "Fresh idle permits a prepared action");
        ReliableFiles.Write(Path.Combine(root, "session-state.txt"), identity + "\nidle\n" + DateTime.UtcNow.AddMinutes(-1).ToString("o"));
        Check(gate.Invoke(overlay, new object[] { true }) != null, "Real action gate rejects stale idle in Booth Mode");
        activeSettings.RequireSessionSignals = false;
        Check(gate.Invoke(overlay, new object[] { true }) == null, "Normal regulation does not require renewing an expired idle signal");
        ReliableFiles.Write(Path.Combine(root, "session-state.txt"), identity + "\nbusy\n" + DateTime.UtcNow.ToString("o"));
        Check(gate.Invoke(overlay, new object[] { true }) != null, "An explicit busy signal still blocks in normal mode");
        ReliableFiles.Write(Path.Combine(root, "session-state.txt"), identity + "\nidle\n" + DateTime.UtcNow.AddMinutes(-1).ToString("o"));
        activeSettings.RequireSessionSignals = true;
        Set(overlay, "initialPreparationDone", false);
        Set(overlay, "boothMode", false);
        Check(gate.Invoke(overlay, new object[] { true }) == null, "Initial preparation outside Booth Mode remains possible");
        ReliableFiles.Write(Path.Combine(root, "update-in-progress.txt"), "installer is checking startup health");
        Check(((string)gate.Invoke(overlay, new object[] { true })).Contains("Update"), "New overlay does not start an automatic ISO action before the update commits");
        Check(((string)gate.Invoke(overlay, new object[] { false })).Contains("Update"), "Manual ISO changes also wait for the update transaction");
        File.Delete(Path.Combine(root, "update-in-progress.txt"));
        Check(gate.Invoke(overlay, new object[] { true }) == null, "Regulation becomes eligible again after update completion");
        Set(overlay, "maintenanceMode", true);
        Check(gate.Invoke(overlay, new object[] { false }) != null, "Maintenance blocks manual as well as automatic actions");
        Check(!BoothCoordination.HasMaintenance(root, now), "No maintenance means no unattended install");
        ReliableFiles.Write(Path.Combine(root, "maintenance-until.txt"), now.AddHours(1).ToString("o"));
        Check(BoothCoordination.HasMaintenance(root, now) && !BoothCoordination.HasMaintenance(root, now.AddHours(2)), "Maintenance expires");
        using (OperationLease lease = OperationLease.TryAcquire(0))
        {
            Check(lease != null, "Operation lease acquired"); bool competing = true;
            Thread thread = new Thread(delegate() { using (OperationLease other = OperationLease.TryAcquire(0)) competing = other != null; });
            thread.Start(); thread.Join(); Check(!competing, "ISO action and update cannot own lease together");
        }

        string install = Path.Combine(root, "transaction"); Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "app.exe"), "old app");
        File.WriteAllText(Path.Combine(install, "updater.exe"), "old updater");
        UpdateTransaction transaction = new UpdateTransaction(install);
        transaction.Begin(new[] { "app.exe", "updater.exe", "helper.exe" });
        File.WriteAllText(Path.Combine(install, "app.exe"), "new app");
        File.WriteAllText(Path.Combine(install, "helper.exe"), "new helper");
        // Simulate termination after only some files were installed; reconstruct from disk.
        transaction = new UpdateTransaction(install); Check(transaction.Pending, "Interrupted update is detectable");
        transaction.Rollback();
        Check(File.ReadAllText(Path.Combine(install, "app.exe")) == "old app" && File.ReadAllText(Path.Combine(install, "updater.exe")) == "old updater", "Complete previous version restored");
        Check(!File.Exists(Path.Combine(install, "helper.exe")) && !transaction.Pending, "New file removed and rollback completed");
        transaction.Rollback(); Check(!transaction.Pending, "Rollback is repeatable");
        transaction.Begin(new[] { "app.exe" }); File.WriteAllText(Path.Combine(install, "app.exe"), "good new app"); transaction.Commit();
        Check(File.ReadAllText(Path.Combine(install, "app.exe")) == "good new app" && File.Exists(Path.Combine(install, "previous-version", "app.exe")), "Committed version retains backup");
        Reject(delegate { transaction.Begin(new[] { "../escape" }); }, "Rollback paths confined to install root");
        Check(OverlayForm.IsVerifiedDarkroom("3.01.1434.0") && !OverlayForm.IsVerifiedDarkroom("3.1.9999"), "Explicit Darkroom compatibility");
        Application.EnableVisualStyles();
        using (IsoMappingForm updates = new IsoMappingForm(false, bands, bands, new[] { 2500, 3200 },
            "Update test", 60, false, "", "", "Test", "Test"))
        {
            MethodInfo applyStatus = typeof(IsoMappingForm).GetMethod("ApplyUpdateStatusValues", BindingFlags.Instance | BindingFlags.NonPublic);
            Button updateButton = (Button)typeof(IsoMappingForm).GetField("updateButton", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(updates);
            Dictionary<string, string> status = new Dictionary<string, string> {
                { "State", "ready" }, { "AvailableVersion", "v24.6.1" }, { "Message", "Sluit Darkroom en installeer de update." } };
            applyStatus.Invoke(updates, new object[] { status, now, now });
            Check(updateButton.Enabled && updateButton.Text == "Update installeren", "Downloaded update offers an enabled installation button");
            Check(updateButton.AccessibleDescription == status["Message"], "Installation button exposes the next step");
            status["State"] = "blocked"; status["Message"] = "Deze release is geblokkeerd na een mislukte installatie.";
            applyStatus.Invoke(updates, new object[] { status, now, now });
            Check(updateButton.Enabled && updateButton.Text == "Opnieuw controleren" && updateButton.AccessibleDescription == status["Message"],
                "Blocked release offers recheck with the actual reason, not a maintenance instruction");
        }
        List<LightHistoryPoint> graphData = new List<LightHistoryPoint>();
        for (int i = 0; i < 120; i++) graphData.Add(new LightHistoryPoint { At = now.AddSeconds(i), Light = 40 + i / 4,
            Target = 1600, Confirmed = "1600", Note = i == 60 ? "ISO bevestigd: 1600" : "" });
        foreach (Size size in new[] { new Size(820, 730), new Size(680, 580), new Size(1000, 800) })
        using (ReliabilityForm form = new ReliabilityForm(BoothSettings.Parse(original), delegate { return graphData; },
            delegate { return 1800; }, delegate { return "Sensor AB12 · firmware 24.6.0 · Darkroom 3.01.1434\r\nDoel 1600 · bevestigd 1600 om 12:30:00\r\nFotosessie bezig; de ISO-aanpassing wacht."; }))
        {
            form.ClientSize = size;
            IntPtr handle = form.Handle;
            // Allow child controls to render without showing the native form.
            MethodInfo setState = typeof(Control).GetMethod("SetState", BindingFlags.Instance | BindingFlags.NonPublic);
            setState.Invoke(form, new object[] { 2, true });
            form.CreateControl(); form.PerformLayout();
            Check(!IsWindowVisible(handle), "New settings rendering does not show a desktop window");
            ComboBox limitInput = (ComboBox)typeof(ReliabilityForm).GetField("maximum", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
            Check(Convert.ToInt32(limitInput.SelectedItem) == 25600, "Maximum ISO is loaded into the actual selector");
            CheckBox sessionInput = (CheckBox)typeof(ReliabilityForm).GetField("requireSessionSignals", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
            Check(!sessionInput.Checked, "Session integration is visibly optional by default");
            sessionInput.Checked = true;
            typeof(ReliabilityForm).GetMethod("CaptureDraft", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
            BoothSettings draft = (BoothSettings)typeof(ReliabilityForm).GetField("draft", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
            Check(draft.RequireSessionSignals, "Settings checkbox records the explicit choice");
            sessionInput.Checked = false;
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(root, "reliability-" + size.Width + "x" + size.Height + ".png"), ImageFormat.Png);
            }
            setState.Invoke(form, new object[] { 2, false });
        }
        Console.WriteLine("PASS: " + assertions + " reliability assertions; no live app, COM port or camera used.");
        return 0;
    }
}
