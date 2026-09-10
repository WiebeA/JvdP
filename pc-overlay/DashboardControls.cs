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
    internal enum OverlayButtonIcon
    {
        Minimize,
        Close,
        Eye
    }

    internal sealed class OverlayIconButton : Button
    {
        private readonly OverlayButtonIcon icon;
        private bool hovered;
        private bool pressed;
        public bool EyeSlashed { get; set; }

        public OverlayIconButton(OverlayButtonIcon iconKind)
        {
            icon = iconKind;
            AutoSize = false;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Color.FromArgb(48, 54, 61);
            ForeColor = Color.FromArgb(232, 238, 245);
            TabStop = false;
            UseVisualStyleBackColor = false;
            AccessibleRole = AccessibleRole.PushButton;
            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovered = false;
            pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            pressed = true;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color background = pressed
                ? Color.FromArgb(82, 89, 99)
                : hovered
                    ? Color.FromArgb(65, 72, 82)
                    : BackColor;
            e.Graphics.Clear(background);
            e.Graphics.SmoothingMode =
                System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            float cx = ClientSize.Width / 2f;
            float cy = ClientSize.Height / 2f;
            using (Pen pen = new Pen(ForeColor, 2f))
            {
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                if (icon == OverlayButtonIcon.Minimize)
                {
                    e.Graphics.DrawLine(pen, cx - 5f, cy, cx + 5f, cy);
                }
                else if (icon == OverlayButtonIcon.Close)
                {
                    e.Graphics.DrawLine(pen, cx - 5f, cy - 5f, cx + 5f, cy + 5f);
                    e.Graphics.DrawLine(pen, cx + 5f, cy - 5f, cx - 5f, cy + 5f);
                }
                else
                {
                    RectangleF eye = new RectangleF(cx - 8f, cy - 5f, 16f, 10f);
                    e.Graphics.DrawEllipse(pen, eye);
                    using (SolidBrush pupil = new SolidBrush(ForeColor))
                        e.Graphics.FillEllipse(pupil, cx - 2.5f, cy - 2.5f, 5f, 5f);
                    if (EyeSlashed)
                        e.Graphics.DrawLine(pen, cx - 8f, cy + 7f, cx + 8f, cy - 7f);
                }
            }
        }
    }

    internal sealed class LightRangeControl : Control
    {
        internal sealed class LayoutSnapshot
        {
            internal Rectangle RangeTextBounds;
            internal Rectangle IsoTextBounds;
            internal int BarY;
            internal int MarkerRadius;
            internal int RequiredHeight;
            internal int RangeMeasuredHeight;
            internal int IsoMeasuredHeight;
        }

        private List<IsoBand> bands = new List<IsoBand>();
        private int lightValue = -1;
        private int activeIso = -1;
        private bool dragging;
        private bool interactive;
        private bool updatingReadableMinimumSize;
        private float visualScale = 1f;
        private int dataChangeCount;

        public bool Interactive
        {
            get { return interactive; }
            set
            {
                if (interactive == value)
                    return;
                interactive = value;
                UpdateReadableMinimumSize();
                Invalidate();
            }
        }

        public float VisualScale
        {
            get { return visualScale; }
            set
            {
                visualScale = Math.Max(1f, Math.Min(1.3f, value));
                UpdateReadableMinimumSize();
                Invalidate();
            }
        }

        public int LightValue
        {
            get { return lightValue; }
        }

        public event EventHandler LightValueChanged;

        internal int DataChangeCountForLayoutTest
        {
            get { return dataChangeCount; }
        }

        public LightRangeControl()
        {
            BackColor = Color.White;
            AccessibleRole = AccessibleRole.Graphic;
            AccessibleName = "Licht naar ISO-bereiken";
            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateReadableMinimumSize();
        }

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            UpdateReadableMinimumSize();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateReadableMinimumSize();
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            Size preferred = base.GetPreferredSize(proposedSize);
            return new Size(
                Math.Max(preferred.Width, MinimumSize.Width),
                Math.Max(preferred.Height, MinimumSize.Height));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Interactive || e.Button != MouseButtons.Left)
                return;
            dragging = true;
            Capture = true;
            SetInteractiveLightValue(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (Interactive && dragging)
                SetInteractiveLightValue(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!Interactive || e.Button != MouseButtons.Left)
                return;
            SetInteractiveLightValue(e.X);
            dragging = false;
            Capture = false;
        }

        private void SetInteractiveLightValue(int mouseX)
        {
            float paintScale = GetPaintScale();
            int barLeft = (int)Math.Round(19 * paintScale);
            int barRight = Math.Max(barLeft + 1,
                ClientSize.Width - (int)Math.Round(19 * paintScale));
            int value = (int)Math.Round(
                100.0 * (mouseX - barLeft) / (barRight - barLeft));
            value = Math.Max(0, Math.Min(100, value));
            if (value == lightValue)
                return;
            lightValue = value;
            Invalidate();
            EventHandler handler = LightValueChanged;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        public void UpdateData(List<IsoBand> source, int light, int iso)
        {
            bool unchanged = lightValue == light && activeIso == iso &&
                bands.Count == source.Count;
            if (unchanged)
            {
                for (int index = 0; index < source.Count; index++)
                {
                    if (bands[index].MaximumLight !=
                            source[index].MaximumLight ||
                        bands[index].Iso != source[index].Iso)
                    {
                        unchanged = false;
                        break;
                    }
                }
            }
            if (unchanged)
                return;

            bands = new List<IsoBand>();
            foreach (IsoBand band in source)
                bands.Add(band.Clone());
            lightValue = light;
            activeIso = iso;
            dataChangeCount++;
            AccessibleDescription = light >= 0 && iso > 0
                ? "Lichtwaarde " + light + " wordt ISO " + iso + "."
                : "Er is nog geen geldige lichtmeting.";
            UpdateReadableMinimumSize();
            Invalidate();
        }

        private void UpdateReadableMinimumSize()
        {
            if (bands.Count == 0 || updatingReadableMinimumSize)
                return;
            updatingReadableMinimumSize = true;
            Graphics measurementGraphics = IsHandleCreated
                ? CreateGraphics() : null;
            try
            {
                int widest = 0;
                int lower = 0;
                using (Font readable = new Font(
                    "Segoe UI", OverlayForm.MinimumReadableUiFontSize,
                    FontStyle.Bold))
                {
                    foreach (IsoBand band in bands)
                    {
                        widest = Math.Max(widest, MeasurePaintTextWidth(
                            measurementGraphics,
                            lower + "–" + band.MaximumLight, readable));
                        widest = Math.Max(widest, MeasurePaintTextWidth(
                            measurementGraphics,
                            "ISO " + band.Iso, readable));
                        lower = band.MaximumLight + 1;
                    }
                }
                float dpiScale = GetDpiScale();
                int horizontalInset = (int)Math.Ceiling(20 * dpiScale);
                int requiredWidth = bands.Count *
                    (widest + horizontalInset) + 2;
                int layoutWidth = Math.Max(
                    Math.Max(ClientSize.Width, MinimumSize.Width),
                    requiredWidth);
                int cellWidth = Math.Max(1,
                    (layoutWidth - 2) / bands.Count);
                int textWidth = Math.Max(1, cellWidth -
                    (int)Math.Ceiling(10 * GetPaintScale()));
                using (Font rangeFont = CreateReadablePaintFont(
                    9.5f * visualScale, FontStyle.Bold,
                    textWidth, false, measurementGraphics))
                using (Font isoFont = CreateReadablePaintFont(
                    9f * visualScale, FontStyle.Regular,
                    textWidth, true, measurementGraphics))
                {
                    LayoutSnapshot layout = CalculateVerticalLayout(
                        new Rectangle(1, 1,
                            Math.Max(1, layoutWidth - 2), 1),
                        rangeFont, isoFont, measurementGraphics);
                    Size required = new Size(
                        Math.Max(MinimumSize.Width, requiredWidth),
                        Math.Max(MinimumSize.Height,
                            layout.RequiredHeight));
                    if (MinimumSize != required)
                        MinimumSize = required;
                }
            }
            finally
            {
                if (measurementGraphics != null)
                    measurementGraphics.Dispose();
                updatingReadableMinimumSize = false;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode =
                System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            if (bands.Count == 0)
                return;

            Rectangle content = new Rectangle(1, 1,
                Math.Max(1, ClientSize.Width - 2),
                Math.Max(1, ClientSize.Height - 2));
            using (Pen border = new Pen(Color.FromArgb(218, 222, 229)))
                e.Graphics.DrawRectangle(border, content);

            int cellWidth = Math.Max(1, content.Width / bands.Count);
            int lower = 0;
            int activeIndex = -1;
            float paintScale = GetPaintScale();
            int textWidth = Math.Max(1,
                cellWidth - (int)Math.Round(8 * paintScale));
            using (Font rangeFont = CreateReadablePaintFont(
                9.5f * visualScale, FontStyle.Bold, textWidth, false,
                e.Graphics))
            using (Font isoFont = CreateReadablePaintFont(
                9f * visualScale, FontStyle.Regular, textWidth, true,
                e.Graphics))
            {
                LayoutSnapshot layout = CalculateVerticalLayout(
                    content, rangeFont, isoFont, e.Graphics);
                for (int index = 0; index < bands.Count; index++)
                {
                    IsoBand band = bands[index];
                    int left = content.Left + index * cellWidth;
                    int right = index == bands.Count - 1
                        ? content.Right
                        : left + cellWidth;
                    Rectangle cell = new Rectangle(left, content.Top,
                        Math.Max(1, right - left), content.Height);
                    bool active = lightValue >= lower &&
                        lightValue <= band.MaximumLight;
                    if (active)
                    {
                        activeIndex = index;
                        using (SolidBrush highlight = new SolidBrush(
                            Color.FromArgb(236, 245, 255)))
                            e.Graphics.FillRectangle(highlight, cell);
                    }
                    if (index > 0)
                    {
                        using (Pen divider = new Pen(
                            Color.FromArgb(218, 222, 229)))
                            e.Graphics.DrawLine(divider, left, content.Top,
                                left, content.Bottom);
                    }
                    Color rangeColor = active
                        ? Color.FromArgb(20, 102, 196)
                        : Color.FromArgb(62, 68, 77);
                    TextRenderer.DrawText(e.Graphics,
                        lower + "–" + band.MaximumLight, rangeFont,
                        new Rectangle(
                            left + (int)Math.Round(4 * paintScale),
                            layout.RangeTextBounds.Top,
                            Math.Max(1, right - left -
                                (int)Math.Round(8 * paintScale)),
                            layout.RangeTextBounds.Height),
                        rangeColor, TextFormatFlags.HorizontalCenter |
                        TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(e.Graphics,
                        "ISO " + band.Iso, isoFont,
                        new Rectangle(
                            left + (int)Math.Round(4 * paintScale),
                            layout.IsoTextBounds.Top,
                            Math.Max(1, right - left -
                                (int)Math.Round(8 * paintScale)),
                            layout.IsoTextBounds.Height),
                        active ? Color.FromArgb(20, 102, 196) :
                            Color.FromArgb(101, 108, 118),
                        TextFormatFlags.HorizontalCenter |
                        TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPadding);
                    lower = band.MaximumLight + 1;
                }

                int barY = layout.BarY;
                int barLeft = content.Left +
                    (int)Math.Round(18 * paintScale);
                int barRight = content.Right -
                    (int)Math.Round(18 * paintScale);
                using (Pen baseBar = new Pen(
                    Color.FromArgb(190, 196, 204), 5f * paintScale))
                {
                    baseBar.StartCap =
                        System.Drawing.Drawing2D.LineCap.Round;
                    baseBar.EndCap =
                        System.Drawing.Drawing2D.LineCap.Round;
                    e.Graphics.DrawLine(
                        baseBar, barLeft, barY, barRight, barY);
                }
                if (activeIndex >= 0)
                {
                    int segmentLeft = barLeft +
                        (barRight - barLeft) * activeIndex / bands.Count;
                    int segmentRight = barLeft +
                        (barRight - barLeft) *
                        (activeIndex + 1) / bands.Count;
                    using (Pen activeBar = new Pen(
                        Color.FromArgb(31, 111, 235), 5f * paintScale))
                    {
                        activeBar.StartCap =
                            System.Drawing.Drawing2D.LineCap.Round;
                        activeBar.EndCap =
                            System.Drawing.Drawing2D.LineCap.Round;
                        e.Graphics.DrawLine(activeBar, segmentLeft, barY,
                            segmentRight, barY);
                    }
                }
                if (lightValue >= 0)
                {
                    int markerX = barLeft + (int)Math.Round(
                        (barRight - barLeft) * Math.Max(0,
                            Math.Min(100, lightValue)) / 100.0);
                    int markerRadius = layout.MarkerRadius;
                    using (SolidBrush marker = new SolidBrush(
                        Color.FromArgb(31, 111, 235)))
                        e.Graphics.FillEllipse(marker,
                            markerX - markerRadius,
                            barY - markerRadius, markerRadius * 2,
                            markerRadius * 2);
                    using (Pen whiteRing = new Pen(
                        Color.White, 2f * paintScale))
                        e.Graphics.DrawEllipse(whiteRing,
                            markerX - markerRadius,
                            barY - markerRadius, markerRadius * 2,
                            markerRadius * 2);
                }
            }
        }

        internal LayoutSnapshot GetLayoutSnapshotForTest()
        {
            Rectangle content = new Rectangle(1, 1,
                Math.Max(1, ClientSize.Width - 2),
                Math.Max(1, ClientSize.Height - 2));
            int cellWidth = Math.Max(1,
                content.Width / Math.Max(1, bands.Count));
            int textWidth = Math.Max(1, cellWidth -
                (int)Math.Round(8 * GetPaintScale()));
            Graphics measurementGraphics = IsHandleCreated
                ? CreateGraphics() : null;
            try
            {
                using (Font rangeFont = CreateReadablePaintFont(
                    9.5f * visualScale, FontStyle.Bold,
                    textWidth, false, measurementGraphics))
                using (Font isoFont = CreateReadablePaintFont(
                    9f * visualScale, FontStyle.Regular,
                    textWidth, true, measurementGraphics))
                    return CalculateVerticalLayout(
                        content, rangeFont, isoFont,
                        measurementGraphics);
            }
            finally
            {
                if (measurementGraphics != null)
                    measurementGraphics.Dispose();
            }
        }

        private LayoutSnapshot CalculateVerticalLayout(
            Rectangle content, Font rangeFont, Font isoFont,
            IDeviceContext deviceContext)
        {
            float dpiScale = GetDpiScale();
            int rangeMeasuredHeight = MeasurePaintTextHeight(
                deviceContext, rangeFont);
            int isoMeasuredHeight = MeasurePaintTextHeight(
                deviceContext, isoFont);
            return CalculateVerticalLayoutCore(
                content, rangeMeasuredHeight, isoMeasuredHeight,
                dpiScale, visualScale, Interactive);
        }

        private int MeasurePaintTextHeight(
            IDeviceContext deviceContext, Font font)
        {
            if (deviceContext != null)
                return TextRenderer.MeasureText(
                    deviceContext, "Ag", font, Size.Empty,
                    TextFormatFlags.NoPadding |
                    TextFormatFlags.NoPrefix |
                    TextFormatFlags.SingleLine).Height;
            return (int)Math.Ceiling(font.GetHeight(DeviceDpi));
        }

        private int MeasurePaintTextWidth(
            IDeviceContext deviceContext, string value, Font font)
        {
            if (deviceContext != null)
                return TextRenderer.MeasureText(
                    deviceContext, value, font, Size.Empty,
                    TextFormatFlags.NoPadding |
                    TextFormatFlags.NoPrefix |
                    TextFormatFlags.SingleLine).Width;
            return TextRenderer.MeasureText(
                value, font, Size.Empty,
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoPrefix |
                TextFormatFlags.SingleLine).Width;
        }

        internal static LayoutSnapshot CalculateVerticalLayoutForTest(
            Rectangle content, int rangeMeasuredHeight,
            int isoMeasuredHeight, float dpiScale,
            float visualScale, bool interactive)
        {
            return CalculateVerticalLayoutCore(
                content, rangeMeasuredHeight, isoMeasuredHeight,
                dpiScale, visualScale, interactive);
        }

        private static LayoutSnapshot CalculateVerticalLayoutCore(
            Rectangle content, int rangeMeasuredHeight,
            int isoMeasuredHeight, float dpiScale,
            float visualScale, bool interactive)
        {
            dpiScale = Math.Max(1f, dpiScale);
            visualScale = Math.Max(1f, Math.Min(1.3f, visualScale));
            float paintScale = dpiScale * visualScale;
            int lineSafety = (int)Math.Ceiling(4 * dpiScale);
            int rangeHeight = rangeMeasuredHeight + lineSafety;
            int isoHeight = isoMeasuredHeight + lineSafety;
            int topPadding = (int)Math.Ceiling(12 * dpiScale);
            int lineGap = (int)Math.Ceiling(8 * dpiScale);
            int textToBarGap = (int)Math.Ceiling(12 * dpiScale);
            int bottomPadding = (int)Math.Ceiling(12 * dpiScale);
            int markerRadius = (int)Math.Ceiling(
                (interactive ? 12 : 9) * paintScale);

            Rectangle rangeBounds = new Rectangle(
                content.Left,
                content.Top + topPadding,
                content.Width, rangeHeight);
            Rectangle isoBounds = new Rectangle(
                content.Left,
                rangeBounds.Bottom + lineGap,
                content.Width, isoHeight);
            int minimumBarY = isoBounds.Bottom +
                textToBarGap + markerRadius;
            int preferredBarY = content.Bottom -
                bottomPadding - markerRadius;
            int barY = Math.Max(minimumBarY, preferredBarY);
            int requiredHeight = topPadding + rangeHeight +
                lineGap + isoHeight + textToBarGap +
                markerRadius * 2 + bottomPadding + 2;

            return new LayoutSnapshot {
                RangeTextBounds = rangeBounds,
                IsoTextBounds = isoBounds,
                BarY = barY,
                MarkerRadius = markerRadius,
                RequiredHeight = requiredHeight,
                RangeMeasuredHeight = rangeMeasuredHeight,
                IsoMeasuredHeight = isoMeasuredHeight
            };
        }

        private Font CreateReadablePaintFont(
            float preferredSize, FontStyle style,
            int availableWidth, bool isoText,
            IDeviceContext deviceContext)
        {
            float minimum = Math.Min(
                preferredSize, OverlayForm.MinimumReadableUiFontSize);
            for (float size = preferredSize;
                size >= minimum; size -= 0.5f)
            {
                Font candidate = new Font("Segoe UI", size, style);
                bool fits = true;
                int lower = 0;
                foreach (IsoBand band in bands)
                {
                    string value = isoText
                        ? "ISO " + band.Iso
                        : lower + "–" + band.MaximumLight;
                    int width = MeasurePaintTextWidth(
                        deviceContext, value, candidate);
                    if (width > availableWidth)
                    {
                        fits = false;
                        break;
                    }
                    lower = band.MaximumLight + 1;
                }
                if (fits)
                    return candidate;
                candidate.Dispose();
            }
            return new Font("Segoe UI", minimum, style);
        }

        private float GetPaintScale()
        {
            return visualScale * GetDpiScale();
        }

        private float GetDpiScale()
        {
            return Math.Max(1f, DeviceDpi / 96f);
        }
    }

}
