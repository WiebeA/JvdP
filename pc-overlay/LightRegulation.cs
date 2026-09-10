using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace Jvdp.LightDarkroomOverlay
{
    internal sealed class SensorReading
    {
        internal int Light;
        internal int Raw = -1;
        internal string Id = "";
        internal string Firmware = "legacy";
        internal uint Sequence;
        internal uint Uptime;
        internal bool Extended;

        internal static bool TryParse(string line, out SensorReading reading)
        {
            reading = null;
            if (String.IsNullOrEmpty(line) || line.Length > 384) return false;
            string[] parts = line.Split('|');
            bool extended = parts[0] == "JVDP2";
            if (!extended && parts[0] != "JVDP") return false;
            Dictionary<string, string> values = new Dictionary<string, string>();
            for (int i = 1; i < parts.Length; i++)
            {
                int separator = parts[i].IndexOf('=');
                if (separator <= 0) return false;
                string key = parts[i].Substring(0, separator);
                if (values.ContainsKey(key)) return false;
                values.Add(key, parts[i].Substring(separator + 1));
            }
            string text;
            int light;
            if (!values.TryGetValue("light", out text) || !Int32.TryParse(text, NumberStyles.None,
                CultureInfo.InvariantCulture, out light) || light < 0 || light > 100) return false;
            SensorReading result = new SensorReading { Light = light, Extended = extended };
            if (extended)
            {
                if (!values.TryGetValue("raw", out text) || !Int32.TryParse(text, out result.Raw) ||
                    result.Raw < 0 || result.Raw > 4095) return false;
                if (!values.TryGetValue("id", out result.Id) || !SafeToken(result.Id, 32)) return false;
                if (!values.TryGetValue("fw", out result.Firmware) || !SafeToken(result.Firmware, 40)) return false;
                if (!values.TryGetValue("seq", out text) || !UInt32.TryParse(text, out result.Sequence)) return false;
                if (!values.TryGetValue("uptime", out text) || !UInt32.TryParse(text, out result.Uptime)) return false;
            }
            reading = result;
            return true;
        }

        private static bool SafeToken(string value, int length)
        {
            if (String.IsNullOrEmpty(value) || value.Length > length) return false;
            foreach (char c in value)
                if (!Char.IsLetterOrDigit(c) && c != '.' && c != '-' && c != '_') return false;
            return true;
        }
    }

    internal sealed class LightRegulation
    {
        private int activeBand = -1;
        internal int DarkRaw = 0;
        internal int BrightRaw = 4095;
        internal int Margin = 2;
        internal int MaximumIso = 25600;
        internal void Reset() { activeBand = -1; }

        internal int Calibrate(SensorReading reading)
        {
            int raw = reading.Raw >= 0 ? reading.Raw : (reading.Light * 4095 / 100);
            double value = 100.0 * (raw - DarkRaw) / (BrightRaw - DarkRaw);
            return (int)Math.Max(0, Math.Min(100, Math.Round(value)));
        }

        internal int Map(int light, IList<IsoBand> bands)
        {
            if (activeBand >= 0 && activeBand < bands.Count)
            {
                int low = activeBand == 0 ? 0 : bands[activeBand - 1].MaximumLight + 1;
                int high = bands[activeBand].MaximumLight;
                if (light >= low - Margin && light <= high + Margin)
                    return Math.Min(MaximumIso, bands[activeBand].Iso);
            }
            for (int i = 0; i < bands.Count; i++)
                if (light <= bands[i].MaximumLight)
                {
                    activeBand = i;
                    return Math.Min(MaximumIso, bands[i].Iso);
                }
            throw new InvalidOperationException("Het profiel dekt de lichtwaarde niet.");
        }
    }

    internal sealed class IsoConfirmation
    {
        private readonly object sync = new object();
        private int processId, target, confirmed;
        private long verifiedAt;
        private readonly Func<long> clock;
        internal IsoConfirmation() : this(Stopwatch.GetTimestamp) { }
        internal IsoConfirmation(Func<long> monotonicClock) { clock = monotonicClock; }
        internal void Clear() { lock (sync) { processId = target = confirmed = 0; verifiedAt = 0; } }
        internal void Confirm(int process, int requested, string actual)
        {
            int numeric;
            if (!Int32.TryParse(actual, out numeric)) { Clear(); return; }
            lock (sync)
            {
                processId = process; target = requested; confirmed = numeric;
                verifiedAt = clock();
            }
        }
        internal void Observe(int process, string actual)
        {
            int numeric;
            if (!Int32.TryParse(actual, out numeric)) { Clear(); return; }
            lock (sync)
            {
                if (process != processId || numeric != confirmed) { target = 0; confirmed = numeric; }
                processId = process;
                verifiedAt = clock();
            }
        }
        internal bool Matches(int process, int requested)
        {
            lock (sync)
                return process == processId && verifiedAt != 0 &&
                    (clock() - verifiedAt) / (double)Stopwatch.Frequency < 900 &&
                    (target == requested || (target == 0 && confirmed == requested));
        }
    }
}
