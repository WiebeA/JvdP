using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Jvdp.LightDarkroomOverlay
{
    internal sealed partial class NativeDarkroomNavigation
    {
        public string ReadIsoValue(IntPtr control) { return ReadSelection(control); }
        public bool IsIsoDropDownOpen(IntPtr control)
        {
            return IsSelectionOpen(control);
        }
        internal static bool IsSelectionOpen(IntPtr control)
        {
            return SendCompleted(control, 0x0157, IntPtr.Zero, IntPtr.Zero, 1000) != IntPtr.Zero;
        }
        internal static string ReadSelection(IntPtr control)
        {
            int index = SendCompleted(control, 0x0147, IntPtr.Zero, IntPtr.Zero, 1000).ToInt32();
            if (index < 0) return "";
            return ReadItem(control, index);
        }
        private static string ReadItem(IntPtr control, int index)
        {
            int length = SendCompleted(control, 0x0149, new IntPtr(index), IntPtr.Zero, 1000).ToInt32();
            if (length < 0 || length > 128) return "";
            StringBuilder text = new StringBuilder(length + 1);
            IntPtr result;
            if (SendMessageTimeout(control, 0x0148, new IntPtr(index), text, 0x0002 | 0x0020, 1000, out result) == IntPtr.Zero)
                throw new TimeoutException("De ISO-keuze kon niet worden teruggelezen.");
            return text.ToString();
        }
        private static int RemainingIsoTime(DateTime deadline)
        {
            int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
            if (remaining <= 0) throw new TimeoutException("De ISO-aanpassing duurde te lang.");
            return Math.Min(8000, remaining);
        }
        internal void SelectIso(string expected, DateTime deadline)
        {
            RequireReady();
            IntPtr combo = VisibleIsoControl;
            if (combo == IntPtr.Zero) throw new InvalidOperationException("Camera Settings is niet actief.");
            // Resolve the value on this exact live control, not an index captured
            // before a page transition or a camera refresh.
            int count = SendCompleted(combo, 0x0146, IntPtr.Zero, IntPtr.Zero, RemainingIsoTime(deadline)).ToInt32();
            if (count < 1 || count > 100) throw new InvalidOperationException("De ISO-lijst is niet beschikbaar.");
            int target = -1;
            for (int i = 0; i < count; i++)
            {
                RemainingIsoTime(deadline);
                if (ReadItem(combo, i) == expected) { target = i; break; }
            }
            if (target < 0) throw new InvalidOperationException("ISO " + expected + " ontbreekt in de actuele cameralijst.");
            PostChecked(combo, 0x014f, new IntPtr(1), IntPtr.Zero);
            WaitForIsoState(combo, delegate { return IsIsoDropDownOpen(combo); }, deadline, "De ISO-keuzelijst is niet geopend.");
            int current = SendCompleted(combo, 0x0147, IntPtr.Zero, IntPtr.Zero, RemainingIsoTime(deadline)).ToInt32();
            if (current < 0) throw new InvalidOperationException("De huidige ISO-keuze ontbreekt.");
            int key = (int)(target > current ? Keys.Down : Keys.Up);
            for (int step = 0; step < Math.Abs(target - current); step++)
            {
                if (VisibleIsoControl != combo) throw new InvalidOperationException("Camera Settings veranderde tijdens de ISO-keuze.");
                int next = current + (target > current ? step + 1 : -(step + 1));
                PostIsoKey(combo, key);
                WaitForIsoState(combo, delegate {
                    return SendCompleted(combo, 0x0147, IntPtr.Zero, IntPtr.Zero, RemainingIsoTime(deadline)).ToInt32() == next;
                }, deadline, "Darkroom heeft de volgende ISO-keuze niet verwerkt.");
            }
            if (VisibleIsoControl != combo) throw new InvalidOperationException("Camera Settings is niet meer actief.");
            PostIsoKey(combo, (int)Keys.Enter);
            WaitForIsoState(combo, delegate { return !IsIsoDropDownOpen(combo) && ReadIsoValue(combo) == expected; },
                deadline, "Darkroom heeft de ISO-keuze nog niet geaccepteerd.");
        }

        private static void PostIsoKey(IntPtr combo, int key)
        {
            int scan = (int)MapVirtualKey((uint)key, 0);
            PostChecked(combo, 0x0100, new IntPtr(key), new IntPtr(1 | (scan << 16)));
            PostChecked(combo, 0x0101, new IntPtr(key), new IntPtr(unchecked((int)0xc0000001) | (scan << 16)));
        }

        private void WaitForIsoState(IntPtr combo, Func<bool> accepted, DateTime deadline, string failure)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 3000)
            {
                RemainingIsoTime(deadline);
                Thread.Sleep(80);
                RequireReady();
                if (VisibleIsoControl != combo) throw new InvalidOperationException("Camera Settings veranderde tijdens de ISO-keuze.");
                if (accepted()) return;
            }
            throw new TimeoutException(failure);
        }
    }
}
