using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

#if LEGACY
[assembly: AssemblyFileVersion("24.5.16.0")]
#elif MAINTENANCE
[assembly: AssemblyFileVersion("24.6.0.0")]
#elif FUTURE
[assembly: AssemblyFileVersion("25.0.0.0")]
#elif LIVE
[assembly: AssemblyFileVersion("24.6.5.0")]
#else
[assembly: AssemblyFileVersion("24.6.1.0")]
#endif

internal static class UpdateShutdownFixture
{
    private sealed class HiddenOverlay : Form
    {
        private bool exitRequested = false;
        protected override void SetVisibleCore(bool visible) { base.SetVisibleCore(false); }
        protected override void OnFormClosing(FormClosingEventArgs args)
        {
            // Like the released overlay, an ordinary Close only hides the app.
            if (!exitRequested && args.CloseReason == CloseReason.UserClosing) args.Cancel = true;
            base.OnFormClosing(args);
        }
        protected override void WndProc(ref Message message)
        {
#if !LEGACY && !MAINTENANCE && !FUTURE
#if LIVE
            if (message.Msg == 0x8002 && File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "iso-busy.txt"))) return;
#endif
            if (message.Msg == 0x8002) { exitRequested = true; Close(); return; }
#endif
            base.WndProc(ref message);
        }
    }
    [STAThread]
    private static void Main(string[] args)
    {
        using (HiddenOverlay form = new HiddenOverlay())
        {
            IntPtr handle = form.Handle;
            string ready = Path.Combine(args[0], "ready.txt");
            File.WriteAllText(ready + ".new", handle.ToInt64().ToString());
            File.Move(ready + ".new", ready);
            Application.Run(form);
        }
        File.WriteAllText(Path.Combine(args[0], "normal-exit.txt"), "Message loop returned normally.");
    }
}
