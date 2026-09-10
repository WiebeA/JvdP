using System;
using System.Diagnostics;

namespace Jvdp.LightDarkroomOverlay
{
    internal sealed class LightCheckCycle
    {
        internal int TargetIso = -1;
        private DateTime startedAt = DateTime.MinValue;
        private long startedTick;
        private readonly Func<long> clock;
        internal LightCheckCycle() : this(Stopwatch.GetTimestamp) { }
        internal LightCheckCycle(Func<long> monotonicClock) { clock = monotonicClock; }
        internal DateTime StartedAt
        {
            get { return startedAt; }
            set { startedAt = value; startedTick = clock(); }
        }
        internal void Invalidate() { TargetIso = -1; StartedAt = DateTime.MinValue; }
        internal double ElapsedSeconds(int periodSeconds)
        {
            return StartedAt == DateTime.MinValue ? 0 : Math.Max(0, Math.Min(periodSeconds,
                (clock() - startedTick) / (double)Stopwatch.Frequency));
        }

        internal double ElapsedSeconds(DateTime now, int periodSeconds)
        {
            return StartedAt == DateTime.MinValue ? 0
                : Math.Max(0, Math.Min(periodSeconds, (now - StartedAt).TotalSeconds));
        }

        internal bool CompleteCheck(int checkedTarget, DateTime checkedSince, DateTime now)
        {
            // A sensor change during an ISO action already started its own
            // stability period. Completing the old action must not erase it.
            if (checkedTarget <= 0 || TargetIso != checkedTarget ||
                StartedAt == DateTime.MinValue || StartedAt != checkedSince)
                return false;
            StartedAt = now;
            return true;
        }
    }
}
