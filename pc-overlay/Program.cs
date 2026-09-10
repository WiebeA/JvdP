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
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(
            IntPtr dpiContext);

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(
            IntPtr dpiContext);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main(string[] args)
        {
            EnablePerMonitorDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs error)
            {
                InstanceActivation.Log("Unhandled exception: " + Convert.ToString(error.ExceptionObject));
            };
            try { Jvdp.WindowsIntegration.StartMenuShortcut.EnsureForInstalledApp(Application.ExecutablePath); }
            catch (Exception error) { InstanceActivation.Log("Start-menu repair failed: " + error.Message); }
            bool startInTray = Array.Exists(
                args, delegate(string value)
                {
                    return String.Equals(value, "--startup",
                        StringComparison.OrdinalIgnoreCase);
                });
            using (Mutex instanceMutex = new Mutex(
                false, @"Local\JvDPLichtregeling"))
            {
                bool ownsInstance = InstanceActivation.AcquireOrActivate(
                    new NativeInstanceActivation(instanceMutex, Application.ExecutablePath), startInTray);
                if (!ownsInstance)
                    return;
                try
                {
                    Application.Run(new OverlayForm(startInTray));
                }
                finally
                {
                    instanceMutex.ReleaseMutex();
                }
            }
        }

        internal static void EnablePerMonitorDpiAwareness()
        {
            IntPtr perMonitorV2 = new IntPtr(-4);
            try
            {
                if (!SetProcessDpiAwarenessContext(perMonitorV2))
                    SetProcessDPIAware();
            }
            catch (EntryPointNotFoundException)
            {
                SetProcessDPIAware();
            }
            try
            {
                // A deployment manifest or a host can establish process DPI
                // awareness before Main runs. Explicitly setting the UI
                // thread keeps every newly created form PerMonitorV2 in that
                // situation as well, including when moved to a Surface panel.
                SetThreadDpiAwarenessContext(perMonitorV2);
            }
            catch (EntryPointNotFoundException)
            {
            }
        }
    }
}
