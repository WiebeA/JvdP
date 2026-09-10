using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Jvdp.LightDarkroomOverlay
{
    internal sealed class ReliabilityForm : Form
    {
        internal Action<BoothSettings> SaveSettings;
        internal Func<string> Diagnose;
        internal Action Recover, Idle;
        internal Action<bool> Maintenance;
        private BoothSettings draft;
        private readonly TextBox name = new TextBox(), camera = new TextBox();
        private readonly NumericUpDown dark = Number(0, 4095), bright = Number(0, 4095), margin = Number(0, 10);
        private readonly ComboBox maximum = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly CheckBox compatible = new CheckBox { Text = "Deze Darkroom-versie is op deze booth getest", AutoSize = true };
        private readonly CheckBox requireSessionSignals = new CheckBox { Text = "Wachten op een Darkroom-rustsignaal", AutoSize = true };
        private readonly Timer timer = new Timer { Interval = 1000 };
        private readonly Label status = new Label { AutoSize = true, Dock = DockStyle.Fill };
        private readonly HistoryGraph graph;

        internal ReliabilityForm(BoothSettings settings, Func<List<LightHistoryPoint>> history, Func<int> raw, Func<string> readStatus)
        {
            draft = settings.Clone();
            Text = "Kalibratie en diagnose";
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            Font = new Font("Segoe UI", 10); ClientSize = new Size(820, 730); MinimumSize = new Size(680, 580);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(247, 248, 250);
            TableLayoutPanel page = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(18) };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37));
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 63));
            Panel scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            scroll.Controls.Add(page); Controls.Add(scroll);
            FlowLayoutPanel footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(12), WrapContents = true };
            Controls.Add(footer);
            AddRow(page, "Boothnaam", name); AddRow(page, "Camera / opstelling", camera);
            AddRow(page, "Donker referentiepunt (ADC)", dark); AddRow(page, "Licht referentiepunt (ADC)", bright);
            FlowLayoutPanel capture = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            capture.Controls.Add(Button("Neem donker over", delegate { int value = raw(); if (value < 0) throw new InvalidOperationException("Voor overnemen is nieuwe sensorfirmware met ruwe ADC-metingen nodig."); dark.Value = value; }));
            capture.Controls.Add(Button("Neem licht over", delegate { int value = raw(); if (value < 0) throw new InvalidOperationException("Voor overnemen is nieuwe sensorfirmware met ruwe ADC-metingen nodig."); bright.Value = value; }));
            AddRow(page, "Kalibratie", capture);
            AddRow(page, "Marge rond ISO-grenzen", margin);
            maximum.Items.AddRange(Array.ConvertAll(OverlayForm.SupportedIsoValues, delegate(int value) { return (object)value; }));
            AddRow(page, "Maximale ISO", maximum);
            AddRow(page, "Compatibiliteit", compatible);
            AddRow(page, "Extra sessiekoppeling", requireSessionSignals);
            Label explanation = new Label { AutoSize = true, MaximumSize = new Size(440, 0), Text =
                "De marge voorkomt wisselen bij grensruis. Controleer de belichting met testfoto's. " +
                "De regeling werkt standaard zonder rustsignaal. Schakel de extra sessiekoppeling alleen in nadat die in Darkroom is ingesteld en getest. " +
                "Een rustsignaal geldt 30 seconden; de ingestelde stabiliteitstijd moet ook verstreken zijn. Compatibiliteit legt een praktijktest vast en blokkeert de regeling niet." };
            AddRow(page, "Werking", explanation);
            AddRow(page, "Verbinding en ISO", status);
            graph = new HistoryGraph(history) { Dock = DockStyle.Fill, Height = 155, MinimumSize = new Size(1, 155) };
            AddRow(page, "Lichtverloop · laatste 15 min", graph);
            FlowLayoutPanel operations = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            operations.Controls.Add(Button("Diagnosepakket opslaan", delegate
            {
                string path = Diagnose();
                MessageBox.Show(this, "Diagnosepakket opgeslagen:\r\n" + path, "Diagnose");
            }));
            operations.Controls.Add(Button("Storing herstellen", delegate { Recover(); }));
            operations.Controls.Add(Button("Sessiekoppeling uitleg", delegate
            {
                string path = Path.Combine(Jvdp.Reliability.BoothCoordination.Root, "Sessiekoppeling.txt");
                using (Form help = new Form { Text = "Darkroom sessiekoppeling", ClientSize = new Size(760, 600), StartPosition = FormStartPosition.CenterParent })
                {
                    help.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                        Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10), Text = File.ReadAllText(path).Replace("\n", "\r\n") });
                    help.ShowDialog(this);
                }
            }));
            operations.Controls.Add(Button("Booth is nu vrij (30 sec)", delegate { Idle(); }));
            operations.Controls.Add(Button("Onderhoud starten", delegate { Maintenance(true); }));
            operations.Controls.Add(Button("Onderhoud stoppen", delegate { Maintenance(false); }));
            AddRow(page, "Beheer", operations);
            footer.Controls.Add(Button("Opslaan", delegate { CaptureDraft(); SaveSettings(draft); DialogResult = DialogResult.OK; Close(); }));
            footer.Controls.Add(Button("Profiel exporteren", delegate
            {
                CaptureDraft();
                using (SaveFileDialog dialog = new SaveFileDialog { Filter = "Boothprofiel (*.json)|*.json", FileName = "booth-profiel.json" })
                    if (dialog.ShowDialog(this) == DialogResult.OK) draft.Save(dialog.FileName);
            }));
            footer.Controls.Add(Button("Profiel importeren", delegate
            {
                using (OpenFileDialog dialog = new OpenFileDialog { Filter = "Boothprofiel (*.json)|*.json" })
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        string ownId = draft.BoothId;
                        draft = BoothSettings.Parse(File.ReadAllText(dialog.FileName)); draft.BoothId = ownId; LoadDraft();
                    }
            }));
            footer.Controls.Add(Button("Sluiten", delegate { Close(); }));
            LoadDraft(); status.Text = readStatus();
            timer.Tick += delegate { status.Text = readStatus(); graph.Invalidate(); };
            timer.Start(); FormClosed += delegate { timer.Stop(); timer.Dispose(); };
        }
        private void LoadDraft()
        {
            name.Text = draft.Name; camera.Text = draft.Camera; dark.Value = draft.DarkRaw; bright.Value = draft.BrightRaw;
            margin.Value = draft.Margin; maximum.SelectedItem = draft.MaximumIso; compatible.Checked = draft.AllowUntestedDarkroom;
            requireSessionSignals.Checked = draft.RequireSessionSignals;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
        private void CaptureDraft()
        {
            draft.Name = name.Text.Trim(); draft.Camera = camera.Text.Trim(); draft.DarkRaw = (int)dark.Value;
            draft.BrightRaw = (int)bright.Value; draft.Margin = (int)margin.Value;
            draft.MaximumIso = Convert.ToInt32(maximum.SelectedItem); draft.AllowUntestedDarkroom = compatible.Checked;
            draft.RequireSessionSignals = requireSessionSignals.Checked; draft.Validate();
        }
        private static NumericUpDown Number(int min, int max) { return new NumericUpDown { Minimum = min, Maximum = max, Height = 36 }; }
        private static void AddRow(TableLayoutPanel page, string text, Control input)
        {
            int row = page.RowCount++; page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.Controls.Add(new Label { Text = text, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 12, 10) }, 0, row);
            input.Dock = DockStyle.Fill; input.Margin = new Padding(0, 5, 0, 8); page.Controls.Add(input, 1, row);
        }
        private Button Button(string text, Action action)
        {
            Button button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(100, 42), Padding = new Padding(8, 2, 8, 2) };
            button.Click += delegate
            {
                try { action(); }
                catch (Exception error) { MessageBox.Show(this, error.Message, "Actie niet uitgevoerd", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            };
            return button;
        }
    }

    internal sealed class HistoryGraph : Control
    {
        private readonly Func<List<LightHistoryPoint>> history;
        internal HistoryGraph(Func<List<LightHistoryPoint>> values) { history = values; DoubleBuffered = true; BackColor = Color.White; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            List<LightHistoryPoint> points = history();
            e.Graphics.DrawString("100", Font, Brushes.Gray, 2, 2);
            e.Graphics.DrawString("0", Font, Brushes.Gray, 2, Math.Max(20, Height - 24));
            if (points.Count < 2) { e.Graphics.DrawString("Wachten op metingen…", Font, Brushes.Gray, 40, 40); return; }
            double total = Math.Max(1, (points[points.Count - 1].At - points[0].At).TotalSeconds);
            PointF? previous = null;
            using (Pen line = new Pen(Color.FromArgb(31, 111, 235), 2))
            foreach (LightHistoryPoint point in points)
            {
                float x = 35 + (float)((point.At - points[0].At).TotalSeconds / total * Math.Max(1, Width - 45));
                float y = 10 + (100 - Math.Max(0, point.Light)) / 100f * Math.Max(1, Height - 35);
                PointF current = new PointF(x, y);
                if (previous.HasValue && point.Light >= 0) e.Graphics.DrawLine(line, previous.Value, current);
                if (!String.IsNullOrEmpty(point.Note)) e.Graphics.FillEllipse(point.Note.Contains("mislukt") ? Brushes.Firebrick : Brushes.ForestGreen, x - 3, y - 3, 6, 6);
                previous = point.Light >= 0 ? (PointF?)current : null;
            }
        }
    }
}
