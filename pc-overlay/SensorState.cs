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
    internal sealed class SensorState
    {
        public readonly object Sync = new object();
        public int Light = -1;
        public readonly LightCheckCycle CheckCycle = new LightCheckCycle();
        public int MappedIso { get { return CheckCycle.TargetIso; } set { CheckCycle.TargetIso = value; } }
        public DateTime CandidateSince { get { return CheckCycle.StartedAt; } set { CheckCycle.StartedAt = value; } }
        public string SerialStatus = "Searching for ESP...";
        public string LastSerialError = "";
    }

    internal sealed class IsoBand
    {
        public int MaximumLight;
        public int Iso;

        public IsoBand() { }

        public IsoBand(int maximumLight, int iso)
        {
            MaximumLight = maximumLight;
            Iso = iso;
        }

        public IsoBand Clone()
        {
            return new IsoBand(MaximumLight, Iso);
        }
    }

}
