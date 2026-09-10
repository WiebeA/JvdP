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
    internal sealed class ManualCoverControlForm : Form
    {
        internal ManualCoverControlForm(
            Rectangle workingArea, EventHandler hideAction)
        {
            Text = "Volledig scherm verbergen";
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10, FontStyle.Regular);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(13, 17, 23);
            TopMost = true;
            // Keep this as an unowned native top-level window. WinForms uses a
            // hidden parking window as owner when ShowInTaskbar is false; on a
            // mixed-DPI PC that gives the control the parking window's DPI.
            // WS_EX_TOOLWINDOW below keeps it out of the taskbar instead.
            ShowInTaskbar = true;

            Button hideButton = new Button();
            hideButton.Text = "VOLLEDIG SCHERM VERBERGEN";
            hideButton.Dock = DockStyle.Fill;
            hideButton.AutoSize = true;
            hideButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            hideButton.Padding = new Padding(20, 10, 20, 10);
            hideButton.Font = new Font("Segoe UI", 11, FontStyle.Bold);
            hideButton.ForeColor = Color.White;
            hideButton.BackColor = Color.FromArgb(187, 45, 59);
            hideButton.FlatStyle = FlatStyle.Flat;
            hideButton.FlatAppearance.BorderSize = 0;
            hideButton.Click += hideAction;
            Controls.Add(hideButton);
            Size textSize = TextRenderer.MeasureText(
                hideButton.Text, hideButton.Font, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            ClientSize = new Size(
                Math.Max(300, textSize.Width + 48),
                Math.Max(64, textSize.Height + 28));
            MinimumSize = Size;
            Location = new Point(
                workingArea.Right - Width - 24,
                workingArea.Top + 24);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WsExToolWindow = 0x00000080;
                const int WsExAppWindow = 0x00040000;
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle &= ~WsExAppWindow;
                parameters.ExStyle |= WsExToolWindow;
                return parameters;
            }
        }
    }
    internal sealed class ActionCoverForm : Form
    {
        private readonly string coverTitle;
        private readonly string coverMessage;

        internal ActionCoverForm(
            Rectangle bounds, string title, string message)
        {
            Text = "JvdP Camera Screen";
            coverTitle = title;
            coverMessage = message;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            BackColor = Color.White;
            ForeColor = Color.FromArgb(31, 38, 58);
            TopMost = true;
            // Stay unowned so the window is created with the target monitor's
            // DPI. WS_EX_TOOLWINDOW keeps it out of taskbar and Alt+Tab.
            ShowInTaskbar = true;
            AllowTransparency = false;
            Opacity = 1.0;
            DoubleBuffered = true;
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WsExToolWindow = 0x00000080;
                const int WsExAppWindow = 0x00040000;
                const int WsExNoActivate = 0x08000000;
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle &= ~WsExAppWindow;
                parameters.ExStyle |= WsExToolWindow | WsExNoActivate;
                return parameters;
            }
        }


        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            args.Graphics.Clear(Color.White);
            args.Graphics.TextRenderingHint =
                System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            RectangleF titleArea;
            RectangleF detailArea;
            CalculateTextAreas(
                ClientSize, out titleArea, out detailArea);

            using (StringFormat centered = CreateCenteredTextFormat())
            using (Font titleFont = CreateFittedFont(
                args.Graphics, coverTitle, "Britannic Bold",
                FontStyle.Regular, 54f, 15f, titleArea.Size, centered))
            using (Font detailFont = CreateFittedFont(
                args.Graphics, coverMessage, "Britannic Bold",
                FontStyle.Regular, 34f, 13f, detailArea.Size, centered))
            using (Brush titleBrush = new SolidBrush(
                Color.FromArgb(31, 38, 58)))
            using (Brush detailBrush = new SolidBrush(
                Color.FromArgb(73, 82, 105)))
            {
                args.Graphics.DrawString(
                    coverTitle, titleFont, titleBrush,
                    titleArea, centered);
                args.Graphics.DrawString(
                    coverMessage, detailFont, detailBrush,
                    detailArea, centered);
            }
        }

        internal static void CalculateTextAreas(
            Size clientSize, out RectangleF titleArea,
            out RectangleF detailArea)
        {
            float horizontalMargin = Math.Max(
                36f, clientSize.Width * 0.07f);
            float verticalMargin = Math.Max(
                32f, clientSize.Height * 0.07f);
            float contentWidth = Math.Max(
                1f, clientSize.Width - horizontalMargin * 2f);
            float contentHeight = Math.Max(
                1f, clientSize.Height - verticalMargin * 2f);
            float gap = Math.Max(16f, contentHeight * 0.035f);
            float titleHeight = Math.Max(
                54f, (contentHeight - gap) * 0.34f);
            float messageHeight = Math.Max(
                54f, contentHeight - titleHeight - gap);
            titleArea = new RectangleF(
                horizontalMargin, verticalMargin,
                contentWidth, titleHeight);
            detailArea = new RectangleF(
                horizontalMargin, verticalMargin + titleHeight + gap,
                contentWidth, messageHeight);
        }

        private static StringFormat CreateCenteredTextFormat()
        {
            StringFormat format = new StringFormat();
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.None;
            return format;
        }

        private static Font CreateFittedFont(
            Graphics graphics, string text, string family,
            FontStyle style, float maximumSize, float minimumSize,
            SizeF available, StringFormat format)
        {
            string value = String.IsNullOrEmpty(text) ? " " : text;
            float readableMinimum = Math.Max(
                OverlayForm.MinimumReadableUiFontSize,
                Math.Min(maximumSize, minimumSize));
            for (float size = maximumSize;
                size >= readableMinimum; size -= 1f)
            {
                Font candidate = new Font(family, size, style);
                SizeF measured = graphics.MeasureString(
                    value, candidate,
                    Math.Max(1, (int)Math.Floor(available.Width)), format);
                if (measured.Width <= available.Width + 1f &&
                    measured.Height <= available.Height + 1f)
                    return candidate;
                candidate.Dispose();
            }
            return new Font(family, readableMinimum, style);
        }
    }
    internal sealed class ComboItem
    {
        internal readonly int Index;
        internal readonly string Value;

        internal ComboItem(int index, string value)
        {
            Index = index;
            Value = value;
        }
    }
    internal static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ComboBoxInfo
        {
            internal int Size;
            internal Rect ItemRect;
            internal Rect ButtonRect;
            internal uint ButtonState;
            internal IntPtr ComboHandle;
            internal IntPtr ItemHandle;
            internal IntPtr ListHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }
    }

}
