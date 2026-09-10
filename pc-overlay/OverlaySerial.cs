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
        private void EnsureSerialConnected()
        {
            if (shuttingDown)
                return;
            if (Interlocked.Exchange(ref serialFaulted, 0) != 0)
                CloseSerial();

            if (serial != null && serial.IsOpen)
            {
                bool neverValidated = lastJvdpLineAt < serialOpenedAt &&
                    DateTime.Now - serialOpenedAt > TimeSpan.FromSeconds(5);
                bool becameSilent = lastJvdpLineAt >= serialOpenedAt &&
                    DateTime.Now - lastJvdpLineAt > TimeSpan.FromSeconds(5);
                if (neverValidated || becameSilent)
                {
                    string rejectedPort = activeSerialPort;
                    lock (serialRetryAfter)
                        serialRetryAfter[rejectedPort] =
                            DateTime.Now.AddSeconds(SerialRetrySeconds(rejectedPort));
                    Log((neverValidated ? "Rejected " : "Disconnected ") +
                        rejectedPort +
                        ": no JVDP light data received within 5 seconds.");
                    lock (sensor.Sync)
                        sensor.SerialStatus = neverValidated
                            ? rejectedPort + " is not the ESP"
                            : rejectedPort + " stopped sending light data";
                    serialReady = false;
                    CloseSerial();
                }
                else
                    return;
            }

            if (serial != null) CloseSerial();
            List<string> candidates = FindCandidateSerialPorts();
            PrioritizeRememberedPort(candidates);
            foreach (string portName in candidates)
            {
                DateTime retryAt;
                lock (serialRetryAfter)
                {
                    if (serialRetryAfter.TryGetValue(portName, out retryAt) &&
                        retryAt > DateTime.Now)
                        continue;
                }

                SerialPort candidate = null;
                try
                {
                    candidate = new SerialPort(portName, 115200);
                    candidate.DtrEnable = false;
                    candidate.RtsEnable = false;
                    candidate.NewLine = "\n";
                    candidate.ReadTimeout = 1000;
                    candidate.DataReceived += SerialDataReceived;
                    candidate.Open();
                    if (shuttingDown)
                    {
                        candidate.DataReceived -= SerialDataReceived;
                        candidate.Dispose();
                        return;
                    }
                    serial = candidate;
                    activeSerialPort = portName;
                    serialOpenedAt = DateTime.Now;
                    lastJvdpLineAt = DateTime.MinValue;
                    serialReady = false;
                    lock (sensor.Sync)
                    {
                        sensor.SerialStatus = portName + " opened; checking ESP";
                        sensor.LastSerialError = "";
                    }
                    Log("Opened " + portName +
                        "; waiting for JVDP sensor signature.");
                    return;
                }
                catch (Exception exception)
                {
                    if (candidate != null)
                    {
                        try
                        {
                            candidate.DataReceived -= SerialDataReceived;
                            candidate.Dispose();
                        }
                        catch { }
                    }
                    lock (serialRetryAfter)
                        serialRetryAfter[portName] = DateTime.Now.AddSeconds(5);
                    lock (sensor.Sync)
                        sensor.LastSerialError = exception.Message;
                }
            }

            lock (sensor.Sync)
                sensor.SerialStatus = candidates.Count == 0
                    ? "No serial ports found"
                    : "ESP serial port unavailable";
            serialReady = false;
        }

        private static List<string> FindCandidateSerialPorts()
        {
            Dictionary<string, int> scores =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string port in SerialPort.GetPortNames())
                scores[port] = 10;

            try
            {
                using (RegistryKey usbRoot = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Enum\USB"))
                {
                    if (usbRoot != null)
                    {
                        foreach (string deviceName in usbRoot.GetSubKeyNames())
                        {
                            using (RegistryKey device =
                                usbRoot.OpenSubKey(deviceName))
                            {
                                if (device == null)
                                    continue;
                                foreach (string instanceName in
                                    device.GetSubKeyNames())
                                {
                                    using (RegistryKey instance =
                                        device.OpenSubKey(instanceName))
                                    using (RegistryKey parameters =
                                        instance == null ? null :
                                        instance.OpenSubKey("Device Parameters"))
                                    {
                                        if (instance == null || parameters == null)
                                            continue;
                                        string port = Convert.ToString(
                                            parameters.GetValue("PortName", ""));
                                        if (String.IsNullOrWhiteSpace(port))
                                            continue;
                                        string identity = (deviceName + " " +
                                            Convert.ToString(instance.GetValue(
                                                "FriendlyName", "")) + " " +
                                            Convert.ToString(instance.GetValue(
                                                "DeviceDesc", "")) + " " +
                                            Convert.ToString(instance.GetValue(
                                                "Mfg", ""))).ToUpperInvariant();
                                        int score = 40;
                                        if (identity.Contains("VID_303A") &&
                                            identity.Contains("PID_1001"))
                                            score = 100;
                                        else if (identity.Contains("ESP32") ||
                                                 identity.Contains("ESPRESSIF"))
                                            score = 90;
                                        else if (identity.Contains("USB SERIAL") ||
                                                 identity.Contains("SERIEEL USB"))
                                            score = 60;
                                        int existing;
                                        if (!scores.TryGetValue(port, out existing) ||
                                            score > existing)
                                            scores[port] = score;
                                    }
                                }
                            }
                        }
                    }
                }

                using (RegistryKey serialMap = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DEVICEMAP\SERIALCOMM"))
                {
                    if (serialMap != null)
                    {
                        foreach (string valueName in serialMap.GetValueNames())
                        {
                            string port = Convert.ToString(
                                serialMap.GetValue(valueName, ""));
                            if (String.IsNullOrWhiteSpace(port))
                                continue;
                            int score = valueName.ToUpperInvariant().Contains("USB")
                                ? 50 : 20;
                            int existing;
                            if (!scores.TryGetValue(port, out existing) ||
                                score > existing)
                                scores[port] = score;
                        }
                    }
                }
            }
            catch { }

            List<string> ports = new List<string>(scores.Keys);
            ports.Sort(delegate(string left, string right)
            {
                int scoreCompare = scores[right].CompareTo(scores[left]);
                if (scoreCompare != 0)
                    return scoreCompare;
                return SerialPortNumber(left).CompareTo(SerialPortNumber(right));
            });
            return ports;
        }

        private static int SerialPortNumber(string port)
        {
            Match match = Regex.Match(port ?? "", @"(\d+)$");
            int number;
            return match.Success && Int32.TryParse(match.Groups[1].Value, out number)
                ? number : Int32.MaxValue;
        }

        private void CloseSerial()
        {
            SerialPort old;
            lock (serialSync)
            {
                old = serial;
                serial = null;
                activeSerialPort = "";
                serialOpenedAt = DateTime.MinValue;
                lastJvdpLineAt = DateTime.MinValue;
                serialReady = false;
                lock (sensor.Sync)
                {
                    sensor.Light = -1;
                    sensor.CheckCycle.Invalidate();
                    lightRegulation.Reset();
                    richProtocol = false;
                    Interlocked.Exchange(ref lastSensorTick, 0);
                }
            }
            if (old == null) return;
            try
            {
                old.DataReceived -= SerialDataReceived;
                if (old.IsOpen)
                    old.Close();
            }
            catch { }
            old.Dispose();
        }

        private void SerialDataReceived(object sender, SerialDataReceivedEventArgs args)
        {
            try
            {
                SerialPort active = sender as SerialPort;
                if (active == null || !ReferenceEquals(active, serial) ||
                    !active.IsOpen)
                    return;

                while (active.BytesToRead > 0)
                {
                    string line = active.ReadLine().Trim();
                    lock (serialSync)
                    {
                    if (!ReferenceEquals(active, serial) || shuttingDown) return;
                    SensorReading reading;
                    if (!SensorReading.TryParse(line, out reading)) continue;
                    if (richProtocol && !reading.Extended) continue;
                    if (!SensorIsFresh())
                    {
                        lock (sensor.Sync) { sensor.CheckCycle.Invalidate(); lightRegulation.Reset(); }
                    }
                    if (reading.Extended && richProtocol && reading.Id == sensorId && reading.Sequence == lastSensorSequence) continue;
                    if (reading.Extended && richProtocol && (reading.Id != sensorId || reading.Uptime < lastSensorUptime))
                    {
                        lock (sensor.Sync) { sensor.CheckCycle.Invalidate(); lightRegulation.Reset(); }
                        isoConfirmation.Clear();
                    }
                    if (reading.Extended)
                    {
                        richProtocol = true; sensorId = reading.Id; sensorFirmware = reading.Firmware;
                        lastSensorSequence = reading.Sequence; lastSensorUptime = reading.Uptime;
                    }
                    int light;
                    lock (sensor.Sync) { lastRawReading = reading; light = lightRegulation.Calibrate(reading); }
                    int mappedIso = MapLightToIso(light);
                    lock (sensor.Sync)
                    {
                        if (sensor.MappedIso != mappedIso || sensor.CandidateSince == DateTime.MinValue)
                        {
                            sensor.MappedIso = mappedIso;
                            sensor.CandidateSince = DateTime.Now;
                            Log("PC mapped light " + light + " to target ISO " + mappedIso + ".");
                        }
                        sensor.Light = light;
                        lastJvdpLineAt = DateTime.Now;
                        Interlocked.Exchange(ref lastSensorTick, Stopwatch.GetTimestamp());
                        sensor.SerialStatus = active.PortName + " connected";
                    }
                    serialReady = true;
                    }
                    RememberSerialPort(active.PortName);
                    RecordHistory("");
                }
            }
            catch (TimeoutException) { }
            catch (Exception exception)
            {
                string failedPort = activeSerialPort;
                if (!String.IsNullOrWhiteSpace(failedPort))
                {
                    lock (serialRetryAfter)
                        serialRetryAfter[failedPort] = DateTime.Now.AddSeconds(5);
                }
                lock (sensor.Sync)
                {
                    sensor.SerialStatus = String.IsNullOrWhiteSpace(failedPort)
                        ? "ESP disconnected"
                        : failedPort + " disconnected";
                    sensor.LastSerialError = exception.Message;
                }
                serialReady = false;
                Interlocked.Exchange(ref serialFaulted, 1);
            }
        }

    }
}
