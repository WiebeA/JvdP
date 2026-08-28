using System;

namespace Jvdp.LightDarkroomOverlay
{
    internal sealed class LightCheckCycle
    {
        internal int TargetIso = -1;
        internal DateTime StartedAt = DateTime.MinValue;

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
