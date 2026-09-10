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
        private void StartTargetIsoAction(bool automatic)
        {
            if (manualActionRunning) return;
            string blocked = GetActionBlockReason(automatic);
            if (blocked != null)
            {
                manualActionStatus = blocked;
                manualActionStatusUntil = DateTime.Now.AddSeconds(12);
                RefreshUi();
                return;
            }
            int requestedIso;
            DateTime requestedSince;
            lock (sensor.Sync)
            {
                if (automatic && (paused || sensor.CandidateSince == DateTime.MinValue ||
                        sensor.CheckCycle.ElapsedSeconds(stabilitySeconds) < stabilitySeconds))
                    return;
                requestedIso = sensor.MappedIso;
                requestedSince = sensor.CandidateSince;
            }
            if (!serialReady || requestedIso <= 0 || !darkroomRunning)
            {
                manualActionFailed = true;
                manualActionStatus = !serialReady || requestedIso <= 0
                    ? "Er is nog geen geldige lichtmeting."
                    : "Darkroom is niet actief.";
                manualActionStatusUntil = DateTime.Now.AddSeconds(6);
                RefreshUi();
                return;
            }
            manualActionRunning = true;
            manualActionFailed = false;
            manualActionStatus = automatic
                ? "Automatische ISO-aanpassing starten…"
                : "ISO-aanpassing starten…";
            manualActionStatusUntil = DateTime.MaxValue;
            runActionButton.Enabled = false;
            runActionButton.Text = "ISO toepassen…";
            coverToggleButton.Enabled = false;
            pauseButton.Enabled = false;
            Log((automatic ? "Automatic" : "Manual") +
                " PC-mapped target ISO action requested.");
            // Keep the target whose stability period was checked. A sensor update
            // between this thread and the worker must not bypass the new countdown.
            Thread worker = new Thread(delegate() {
                RunTargetIsoAction(requestedIso, requestedSince, automatic);
            });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        private void RunTargetIsoAction(int desiredIso, DateTime checkedSince, bool automatic)
        {
            Process process = null;
            OperationLease operation = null;
            DarkroomNavigation navigation = null;
            string confirmedIso = null;
            bool selectionAttempted = false;
            string finalStatus;
            bool failed = false;
            bool coverShown = false;
            bool keepCoverVisible = manualCoverVisible;
            Stopwatch actionTimer = Stopwatch.StartNew();
            long previousStepMilliseconds = 0;
            DateTime actionDeadlineUtc = DateTime.UtcNow.AddMilliseconds(
                DarkroomActionDeadlineMilliseconds);
            try
            {
                operation = OperationLease.TryAcquire(0);
                if (operation == null) throw new InvalidOperationException("Een sessie of update wordt voorbereid.");
                string blocked = GetActionBlockReason(automatic);
                if (blocked != null) throw new InvalidOperationException(blocked);
                lock (sensor.Sync)
                    if (!serialReady || !SensorIsFresh() || sensor.MappedIso != desiredIso ||
                        sensor.CandidateSince != checkedSince || (automatic &&
                        sensor.CheckCycle.ElapsedSeconds(stabilitySeconds) < stabilitySeconds))
                        throw new InvalidOperationException("De lichtmeting is gewijzigd of onderbroken. Wacht op verse stabiele metingen.");
                if (desiredIso <= 0)
                    throw new InvalidOperationException(
                        "No valid target ISO is available from the PC mapping.");
                Log("Locked PC-mapped target ISO " + desiredIso +
                    " for this " + (automatic ? "automatic" : "manual") +
                    " action before Darkroom navigation.");
                process = FindDarkroomProcess();
                if (process == null)
                    throw new InvalidOperationException("Darkroom Booth is not running.");
                NativeDarkroomNavigation native =
                    new NativeDarkroomNavigation(process.Id);
                navigation = new DarkroomNavigation(native, Log);
                IntPtr window = native.EditorWindow;
                LogActionTiming(
                    actionTimer, ref previousStepMilliseconds,
                    "Darkroom process found");
                coverShown = ShowFullscreenCovers(false);
                SleepWithinActionDeadline(actionDeadlineUtc, 100);
                blocked = GetActionBlockReason(automatic);
                if (blocked != null) throw new InvalidOperationException(blocked);
                LogDarkroomWindowState(process, window);
                SetManualActionStatus(
                    "Stap 1/3 — Camera-instellingen openen…");
                IntPtr combo = navigation.OpenCamera(actionDeadlineUtc);
                LogActionTiming(
                    actionTimer, ref previousStepMilliseconds,
                    "Camera settings ready");
                Log("Direct ISO control found: handle=" + combo +
                    ", automationId=107. Camera was opened through Darkroom's " +
                    "native Event Settings command and property sheet; no " +
                    "coordinates or mouse input used.");
                string observedIso = ReadComboSelection(combo);
                ComboItem target = FindTargetComboItem(combo, desiredIso);
                Log("ISO state: PC target=" + desiredIso +
                    ", Darkroom current=" + observedIso +
                    ", selected target index=" +
                    (target == null ? "none" : target.Index.ToString()) + ".");
                if (target == null)
                    throw new InvalidOperationException(
                        "Darkroom does not expose any numeric ISO values.");
                if (!String.Equals(
                        target.Value, desiredIso.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                    Log("PC target ISO " + desiredIso +
                        " is unavailable; using nearest Darkroom ISO " +
                        target.Value + ".");

                long currentIndex = SendMessageWithTimeout(
                    combo, 0x0147, IntPtr.Zero, IntPtr.Zero).ToInt64();
                if (currentIndex == target.Index)
                {
                    confirmedIso = target.Value;
                    currentDarkroomIso = target.Value;
                    currentIsoReadAt = DateTime.Now;
                    finalStatus = "No change - Darkroom is already at target ISO " +
                                  target.Value + ".";
                    Log(finalStatus);
                }
                else
                {
                    if (BoothCoordination.ReadSession(localDirectory, darkroomIdentity, DateTime.UtcNow) == "busy")
                        throw new InvalidOperationException("Darkroom meldt een nieuwe fotosessie; de ISO-aanpassing is afgebroken.");
                    SetManualActionStatus(
                        "Stap 2/3 — ISO " + target.Value + " toepassen…");
                    selectionAttempted = true;
                    string selectionMethod = SelectIsoWithoutCoordinates(
                        window, ref combo, target, actionDeadlineUtc);
                    Log("ISO selection method: " + selectionMethod + ".");
                    SleepWithinActionDeadline(actionDeadlineUtc, 250);
                    Log("Checking native Darkroom error dialogs (no accessibility scan).");
                    if (native.DismissCameraPropertyError())
                        throw new InvalidOperationException(
                            "Darkroom reported that ISO " +
                            target.Value + " could not be set.");
                    native.RequireReady();
                    string confirmed = ReadComboSelection(combo);
                    if (!String.Equals(
                            confirmed, target.Value,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "Darkroom did not confirm target ISO " +
                            target.Value + ".");
                    confirmedIso = confirmed;
                    currentDarkroomIso = confirmed;
                    currentIsoReadAt = DateTime.Now;
                    finalStatus = "Done - Darkroom ISO now matches target ISO " +
                                  confirmed + ".";
                    Log(finalStatus);
                }

                LogActionTiming(
                    actionTimer, ref previousStepMilliseconds,
                    "ISO verified");
                SetManualActionStatus("Stap 3/3 — Booth Mode starten…");
                navigation.StartBooth(DateTime.UtcNow.AddSeconds(5));
                if (!keepCoverVisible)
                {
                    navigation.RevealBooth(HideFullscreenCovers, DateTime.UtcNow.AddSeconds(3));
                    coverShown = false;
                }
                boothMode = true;
                initialPreparationDone = true;
                lastAppliedTargetIso = desiredIso;
                lastAppliedDarkroomProcessId = process.Id;
                isoConfirmation.Confirm(process.Id, desiredIso, confirmedIso);
                finalStatus += " Booth mode started.";
                LogActionTiming(
                    actionTimer, ref previousStepMilliseconds,
                    "Booth Mode restored");
                Log(finalStatus);
            }
            catch (Exception exception)
            {
                failed = true;
                isoConfirmation.Clear();
                finalStatus = DarkroomActionStatus.Failure(
                    confirmedIso, selectionAttempted, exception.Message);
                Log(finalStatus);
                if (navigation != null && navigation.ShouldRestoreBooth)
                {
                    try
                    {
                        Log("Fail-safe recovery: restoring Booth Mode...");
                        navigation.StartBooth(DateTime.UtcNow.AddSeconds(5));
                        if (!keepCoverVisible)
                        {
                            navigation.RevealBooth(HideFullscreenCovers, DateTime.UtcNow.AddSeconds(3));
                            coverShown = false;
                        }
                        boothMode = true;
                        if (confirmedIso != null)
                        {
                            failed = false;
                            lastAppliedTargetIso = desiredIso;
                            lastAppliedDarkroomProcessId = process.Id;
                            isoConfirmation.Confirm(process.Id, desiredIso, confirmedIso);
                            initialPreparationDone = true;
                            finalStatus = "ISO " + confirmedIso +
                                " is door Darkroom bevestigd. Booth Mode is hersteld.";
                        }
                        else
                            finalStatus += " Booth Mode is hersteld.";
                        Log("Fail-safe recovery completed.");
                    }
                    catch (Exception recoveryException)
                    {
                        finalStatus += " Booth Mode herstellen is niet gelukt: " +
                            recoveryException.Message;
                        Log("Fail-safe recovery failed: " +
                            recoveryException.Message);
                    }
                }
            }
            finally
            {
                if (coverShown)
                {
                    if (keepCoverVisible)
                        KeepManualCoverVisibleAfterAction();
                    else
                        HideFullscreenCovers();
                }
                if (process != null) process.Dispose();
                if (operation != null) operation.Dispose();
            }
            manualActionFailed = failed;
            manualActionStatus = finalStatus;
            if (!failed)
            {
                automaticFailures = 0;
                RestartLightCheck(desiredIso, checkedSince);
            }
            else if (automatic && ++automaticFailures >= 3)
            {
                automaticFault = "Drie aanpassingen mislukt. Controleer de camera en kies Herstellen in Kalibratie en diagnose.";
                manualActionStatusUntil = DateTime.MaxValue;
            }
            RecordHistory(failed ? "Aanpassing mislukt" : "ISO bevestigd: " + confirmedIso);
            Log("Darkroom ISO action finished in " +
                actionTimer.ElapsedMilliseconds + " ms; result=" +
                (failed ? "failed" : "success") + ".");
            manualActionStatusUntil = automaticFault == null ? DateTime.Now.AddSeconds(12) : DateTime.MaxValue;
            if (automatic && failed)
            {
                nextAutomaticAttemptAt = DateTime.Now.AddSeconds(
                    Math.Max(30, stabilitySeconds));
                Log("Automatic retry delayed until " +
                    nextAutomaticAttemptAt.ToString("HH:mm:ss") + ".");
            }
            else if (!failed)
            {
                nextAutomaticAttemptAt = DateTime.MinValue;
            }
            manualActionRunning = false;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    runActionButton.Enabled = true;
                    runActionButton.Text = "Doel-ISO nu toepassen";
                    coverToggleButton.Enabled = true;
                    pauseButton.Enabled = true;
                    UpdateCoverToggleButton();
                    RefreshUi();
                });
            }
            catch { }
        }

    }
}
