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
    internal sealed class TouchNumberInput : UserControl
    {
        private readonly TextBox input;

        internal event EventHandler ValueTextChanged;
        internal event EventHandler InputLeave;

        internal string ValueText
        {
            get { return input.Text; }
            set
            {
                string next = value ?? "";
                if (input.Text != next)
                    input.Text = next;
            }
        }

        internal bool InputReadOnly
        {
            get { return input.ReadOnly; }
            set
            {
                input.ReadOnly = value;
                BackColor = value
                    ? Color.FromArgb(247, 248, 250)
                    : Color.White;
                input.BackColor = BackColor;
            }
        }

        internal TouchNumberInput()
        {
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.White;
            BorderStyle = BorderStyle.FixedSingle;
            MinimumSize = new Size(112, 68);
            Size = new Size(112, 68);
            Padding = new Padding(14, 8, 14, 8);
            TabStop = true;

            input = new TextBox();
            input.BorderStyle = BorderStyle.None;
            input.Font = new Font("Segoe UI", 18, FontStyle.Bold);
            input.TextAlign = HorizontalAlignment.Center;
            input.BackColor = BackColor;
            input.ShortcutsEnabled = true;
            input.KeyPress += delegate(object sender, KeyPressEventArgs key)
            {
                if (!Char.IsControl(key.KeyChar) &&
                    !Char.IsDigit(key.KeyChar))
                    key.Handled = true;
            };
            input.Enter += delegate { input.SelectAll(); };
            input.TextChanged += delegate
            {
                EventHandler handler = ValueTextChanged;
                if (handler != null)
                    handler(this, EventArgs.Empty);
            };
            input.Leave += delegate
            {
                EventHandler handler = InputLeave;
                if (handler != null)
                    handler(this, EventArgs.Empty);
            };
            Controls.Add(input);

            Click += delegate { FocusInput(); };
            MouseDown += delegate { FocusInput(); };
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (input == null)
                return;
            int preferredHeight = input.PreferredHeight;
            int top = Math.Max(Padding.Top,
                (ClientSize.Height - preferredHeight) / 2);
            input.SetBounds(
                Padding.Left,
                top,
                Math.Max(1, ClientSize.Width - Padding.Horizontal),
                preferredHeight);
        }

        internal void SelectAllText()
        {
            input.Focus();
            input.SelectAll();
        }

        private void FocusInput()
        {
            if (!input.ReadOnly)
                SelectAllText();
        }
    }

    internal sealed class IsoMappingForm : Form
    {
        internal Func<bool> Persist;
        private static readonly Color Background = Color.FromArgb(247, 248, 250);
        private static readonly Color Card = Color.White;
        private static readonly Color Border = Color.FromArgb(218, 222, 229);
        private static readonly Color TextPrimary = Color.FromArgb(31, 35, 40);
        private static readonly Color TextMuted = Color.FromArgb(99, 108, 118);
        private static readonly Color Accent = Color.FromArgb(31, 111, 235);

        private readonly RadioButton defaultProfile;
        private readonly RadioButton customProfile;
        private readonly DataGridView grid;
        private readonly Panel bandEditorCard;
        private readonly TableLayoutPanel bandRowsHost;
        private readonly List<Label> touchRangeLabels =
            new List<Label>();
        private readonly Button addButton;
        private readonly Button removeButton;
        private readonly Button saveButton;
        private readonly Label explanation;
        private readonly Panel profileCard;
        private readonly Panel previewCard;
        private readonly LightRangeControl previewRange;
        private readonly NumericUpDown previewLightInput;
        private readonly NumericUpDown stabilitySecondsInput;
        private readonly Label previewText;
        private readonly List<IsoBand> defaultBands;
        private readonly int[] supportedIsos;
        private readonly Button isoSettingsTab;
        private readonly Button coverSettingsTab;
        private readonly Button updateButton;
        private readonly Label boothAndVersionLabel;
        private readonly System.Windows.Forms.Timer updateStatusTimer;
        private readonly Panel coverSettingsPanel;
        private readonly RadioButton defaultCoverText;
        private readonly RadioButton customCoverText;
        private readonly TextBox coverTitleInput;
        private readonly TextBox coverMessageInput;
        private readonly Label coverPreviewTitle;
        private readonly Label coverPreviewMessage;
        private readonly string defaultCoverTitle;
        private readonly string defaultCoverMessage;
        private readonly string boothName;
        private string draftCustomCoverTitle;
        private string draftCustomCoverMessage;
        private bool renderingCoverText;
        private bool showingCoverSettings;
        private bool rendering;
        private bool renderingTouchEditors;
        private bool renderedCustom;
        private bool initialized;
        private bool dirty;
        private readonly string updaterExecutablePath;
        private readonly string updaterStatusPath;
        private readonly Dictionary<Control, float> textFitBaseFontSizes =
            new Dictionary<Control, float>();
        private DateTime updateCheckStartedAtUtc = DateTime.MinValue;
        private bool textFitActive;

        public bool UseCustomMapping { get; private set; }
        public List<IsoBand> CustomBands { get; private set; }
        public int StabilitySeconds
        {
            get { return (int)stabilitySecondsInput.Value; }
        }
        public bool UseCustomCoverText { get; private set; }
        public string CustomCoverTitle
        {
            get { return draftCustomCoverTitle; }
        }
        public string CustomCoverMessage
        {
            get { return draftCustomCoverMessage; }
        }

        public IsoMappingForm(
            bool useCustomMapping,
            List<IsoBand> customBands,
            List<IsoBand> defaults,
            int[] isoValues,
            string boothName,
            int stabilitySeconds,
            bool useCustomCoverText,
            string customCoverTitle,
            string customCoverMessage,
            string defaultCoverTitleValue,
            string defaultCoverMessageValue)
        {
            Text = "Instellingen";
            ClientSize = new Size(1000, 720);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Background;
            ForeColor = TextPrimary;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10, FontStyle.Regular);

            defaultBands = CloneBands(defaults);
            CustomBands = CloneBands(customBands);
            supportedIsos = (int[])isoValues.Clone();
            this.boothName = boothName;
            UseCustomCoverText = useCustomCoverText;
            defaultCoverTitle = defaultCoverTitleValue;
            defaultCoverMessage = defaultCoverMessageValue;
            draftCustomCoverTitle = String.IsNullOrWhiteSpace(customCoverTitle)
                ? defaultCoverTitle : customCoverTitle;
            draftCustomCoverMessage = String.IsNullOrWhiteSpace(customCoverMessage)
                ? defaultCoverMessage : customCoverMessage;
            string localRoot = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "JvdP", "LightDarkroomOverlay");
            updaterExecutablePath = Path.Combine(
                localRoot, "JvdpAutoUpdater.exe");
            updaterStatusPath = Path.Combine(
                localRoot, "updater-status.txt");

            Label title = MakeDialogLabel(
                "Instellingen", 18, FontStyle.Bold, TextPrimary);
            title.SetBounds(24, 18, 196, 38);
            Controls.Add(title);

            isoSettingsTab = MakeDialogButton("Licht && ISO", true);
            isoSettingsTab.SetBounds(210, 14, 118, 40);
            isoSettingsTab.Click += delegate { ShowSettingsSection(false); };
            Controls.Add(isoSettingsTab);

            coverSettingsTab = MakeDialogButton("Overlay settings", false);
            coverSettingsTab.SetBounds(336, 14, 156, 40);
            coverSettingsTab.Click += delegate { ShowSettingsSection(true); };
            Controls.Add(coverSettingsTab);

            boothAndVersionLabel = MakeDialogLabel(
                "Booth: " + boothName + "  ·  Versie " +
                BuildInfo.Version, 9, FontStyle.Regular, TextMuted);
            boothAndVersionLabel.TextAlign = ContentAlignment.MiddleRight;
            boothAndVersionLabel.SetBounds(
                ClientSize.Width - 520, 48, 280, 18);
            boothAndVersionLabel.Anchor = AnchorStyles.Top |
                AnchorStyles.Right;
            Controls.Add(boothAndVersionLabel);

            updateButton = MakeDialogButton(
                "Updates controleren", false);
            updateButton.SetBounds(
                ClientSize.Width - 224, 14, 200, 40);
            updateButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            updateButton.AccessibleName =
                "Nu controleren op software-updates";
            updateButton.Click += delegate { StartUpdateCheck(); };
            Controls.Add(updateButton);

            updateStatusTimer = new System.Windows.Forms.Timer();
            updateStatusTimer.Interval = 500;
            updateStatusTimer.Tick += delegate { RefreshUpdateStatus(); };

            profileCard = new Panel();
            profileCard.BackColor = Card;
            profileCard.BorderStyle = BorderStyle.FixedSingle;
            profileCard.SetBounds(24, 68, ClientSize.Width - 48, 100);
            profileCard.Anchor = AnchorStyles.Top |
                AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(profileCard);

            defaultProfile = new RadioButton();
            defaultProfile.Text = "Standaardprofiel · alle booths";
            defaultProfile.ForeColor = TextPrimary;
            defaultProfile.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            defaultProfile.SetBounds(16, 12, 290, 28);
            defaultProfile.AccessibleName = "Gebruik standaardprofiel voor alle booths";
            profileCard.Controls.Add(defaultProfile);

            customProfile = new RadioButton();
            customProfile.Text = "Eigen profiel · deze booth";
            customProfile.ForeColor = TextPrimary;
            customProfile.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            customProfile.SetBounds(350, 12, 300, 28);
            customProfile.AccessibleName = "Gebruik eigen profiel voor deze booth";
            profileCard.Controls.Add(customProfile);

            explanation = MakeDialogLabel(
                "", 9, FontStyle.Regular, TextMuted);
            explanation.SetBounds(18, 46, profileCard.Width - 318, 44);
            explanation.Anchor = AnchorStyles.Top |
                AnchorStyles.Left | AnchorStyles.Right;
            profileCard.Controls.Add(explanation);

            Label stabilityCaption = MakeDialogLabel(
                "Licht stabiel (seconden)", 9, FontStyle.Regular,
                TextMuted);
            stabilityCaption.TextAlign = ContentAlignment.MiddleRight;
            stabilityCaption.SetBounds(profileCard.Width - 286, 50, 190, 32);
            stabilityCaption.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            profileCard.Controls.Add(stabilityCaption);

            stabilitySecondsInput = new NumericUpDown();
            stabilitySecondsInput.Minimum = 5;
            stabilitySecondsInput.Maximum = 300;
            stabilitySecondsInput.Increment = 5;
            stabilitySecondsInput.Value = Math.Max(5,
                Math.Min(300, stabilitySeconds));
            stabilitySecondsInput.Font = new Font(
                "Segoe UI", 10, FontStyle.Regular);
            stabilitySecondsInput.TextAlign = HorizontalAlignment.Center;
            stabilitySecondsInput.SetBounds(
                profileCard.Width - 86, 52, 68, 28);
            stabilitySecondsInput.Anchor = AnchorStyles.Top |
                AnchorStyles.Right;
            stabilitySecondsInput.AccessibleName =
                "Aantal seconden dat de lichtwaarde stabiel moet zijn";
            profileCard.Controls.Add(stabilitySecondsInput);

            previewCard = new Panel();
            previewCard.BackColor = Card;
            previewCard.BorderStyle = BorderStyle.FixedSingle;
            previewCard.SetBounds(24, 182, ClientSize.Width - 48, 150);
            previewCard.Anchor = AnchorStyles.Top | AnchorStyles.Left |
                AnchorStyles.Right;
            Controls.Add(previewCard);

            Label previewCaption = MakeDialogLabel(
                "Grafische preview", 10, FontStyle.Bold, TextPrimary);
            previewCaption.SetBounds(14, 7, 150, 28);
            previewCard.Controls.Add(previewCaption);

            Label previewLightCaption = MakeDialogLabel(
                "Voorbeeldlichtwaarde", 9, FontStyle.Regular, TextMuted);
            previewLightCaption.SetBounds(180, 7, 155, 28);
            previewCard.Controls.Add(previewLightCaption);

            previewLightInput = new NumericUpDown();
            previewLightInput.Minimum = 0;
            previewLightInput.Maximum = 100;
            previewLightInput.Value = 63;
            previewLightInput.Font = new Font("Segoe UI", 10, FontStyle.Regular);
            previewLightInput.SetBounds(338, 7, 70, 28);
            previewLightInput.AccessibleName = "Voorbeeldlichtwaarde";
            previewCard.Controls.Add(previewLightInput);

            previewText = MakeDialogLabel(
                "", 9, FontStyle.Bold, Color.FromArgb(20, 102, 196));
            previewText.TextAlign = ContentAlignment.MiddleRight;
            previewText.SetBounds(420, 7, previewCard.Width - 434, 28);
            previewText.Anchor = AnchorStyles.Top | AnchorStyles.Left |
                AnchorStyles.Right;
            previewCard.Controls.Add(previewText);

            previewRange = new LightRangeControl();
            previewRange.Interactive = true;
            previewRange.Cursor = Cursors.Hand;
            previewRange.AccessibleName =
                "Versleepbare voorbeeldlichtwaarde";
            previewRange.SetBounds(14, 43, previewCard.Width - 28, 92);
            previewRange.Anchor = AnchorStyles.Top | AnchorStyles.Left |
                AnchorStyles.Right;
            previewCard.Controls.Add(previewRange);

            grid = new DataGridView();
            grid.SetBounds(24, 346, ClientSize.Width - 48,
                ClientSize.Height - 346 - 92);
            grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom |
                AnchorStyles.Left | AnchorStyles.Right;
            grid.BackgroundColor = Card;
            grid.BorderStyle = BorderStyle.FixedSingle;
            grid.GridColor = Border;
            grid.RowHeadersVisible = false;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.ColumnHeadersHeightSizeMode =
                DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            grid.AutoSizeRowsMode =
                DataGridViewAutoSizeRowsMode.AllCells;
            grid.RowTemplate.MinimumHeight = 36;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Border;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = TextPrimary;
            grid.ColumnHeadersDefaultCellStyle.Font =
                new Font("Segoe UI", 9, FontStyle.Bold);
            grid.DefaultCellStyle.BackColor = Card;
            grid.DefaultCellStyle.ForeColor = TextPrimary;
            grid.DefaultCellStyle.SelectionBackColor =
                Color.FromArgb(32, 77, 126);
            grid.DefaultCellStyle.SelectionForeColor = Color.White;
            grid.DefaultCellStyle.Font = new Font("Segoe UI", 10);
            grid.AccessibleName = "Licht naar ISO-bereiken";

            DataGridViewTextBoxColumn fromColumn =
                new DataGridViewTextBoxColumn();
            fromColumn.HeaderText = "Licht vanaf";
            fromColumn.ReadOnly = true;
            grid.Columns.Add(fromColumn);

            DataGridViewTextBoxColumn throughColumn =
                new DataGridViewTextBoxColumn();
            throughColumn.HeaderText = "Tot en met";
            grid.Columns.Add(throughColumn);

            DataGridViewComboBoxColumn isoColumn =
                new DataGridViewComboBoxColumn();
            isoColumn.HeaderText = "Darkroom ISO";
            isoColumn.DataSource = supportedIsos;
            isoColumn.FlatStyle = FlatStyle.Flat;
            grid.Columns.Add(isoColumn);
            grid.Visible = false;

            bandEditorCard = new Panel();
            bandEditorCard.BackColor = Card;
            bandEditorCard.BorderStyle = BorderStyle.FixedSingle;
            bandEditorCard.AutoScroll = true;
            bandEditorCard.AccessibleName =
                "Touchvriendelijke lichtbereiken";

            bandRowsHost = new TableLayoutPanel();
            bandRowsHost.AutoSize = true;
            bandRowsHost.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            bandRowsHost.Dock = DockStyle.Top;
            bandRowsHost.ColumnCount = 1;
            bandRowsHost.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            bandRowsHost.Padding = new Padding(12, 10, 12, 12);
            bandRowsHost.AccessibleName =
                "Grote aanraakbediening voor lichtbereiken";
            bandEditorCard.Controls.Add(bandRowsHost);
            bandEditorCard.Controls.Add(grid);
            Controls.Add(bandEditorCard);

            addButton = MakeDialogButton("Bereik toevoegen", false);
            addButton.SetBounds(24, ClientSize.Height - 72, 150, 42);
            addButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            addButton.Click += delegate { AddRange(); };
            Controls.Add(addButton);

            removeButton = MakeDialogButton("Bereik verwijderen", false);
            removeButton.SetBounds(182, ClientSize.Height - 72, 158, 42);
            removeButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            removeButton.Click += delegate { RemoveRange(); };
            Controls.Add(removeButton);

            Button cancelButton = MakeDialogButton("Terug", false);
            cancelButton.SetBounds(
                ClientSize.Width - 434, ClientSize.Height - 72, 220, 42);
            cancelButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            cancelButton.Click += delegate
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };
            Controls.Add(cancelButton);

            saveButton = MakeDialogButton("Opslaan", true);
            saveButton.SetBounds(
                ClientSize.Width - 206, ClientSize.Height - 72, 182, 42);
            saveButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            saveButton.Click += delegate { SaveAndClose(); };
            Controls.Add(saveButton);

            coverSettingsPanel = new Panel();
            coverSettingsPanel.BackColor = Background;
            coverSettingsPanel.SetBounds(24, 68,
                ClientSize.Width - 48, ClientSize.Height - 160);
            coverSettingsPanel.Anchor = AnchorStyles.Top |
                AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            coverSettingsPanel.Visible = false;
            Controls.Add(coverSettingsPanel);

            Panel coverModeCard = new Panel();
            coverModeCard.BackColor = Card;
            coverModeCard.BorderStyle = BorderStyle.FixedSingle;
            coverModeCard.SetBounds(0, 0, coverSettingsPanel.Width, 100);
            coverModeCard.Anchor = AnchorStyles.Top |
                AnchorStyles.Left | AnchorStyles.Right;
            coverSettingsPanel.Controls.Add(coverModeCard);

            defaultCoverText = new RadioButton();
            defaultCoverText.Text = "Standaardtekst · alle booths";
            defaultCoverText.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            defaultCoverText.ForeColor = TextPrimary;
            defaultCoverText.SetBounds(16, 12, 290, 28);
            coverModeCard.Controls.Add(defaultCoverText);

            customCoverText = new RadioButton();
            customCoverText.Text = "Eigen tekst · deze booth";
            customCoverText.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            customCoverText.ForeColor = TextPrimary;
            customCoverText.SetBounds(350, 12, 300, 28);
            coverModeCard.Controls.Add(customCoverText);

            Label coverExplanation = MakeDialogLabel(
                "De standaardtekst wordt via de software-update op alle " +
                "booths gebruikt. Een eigen tekst geldt alleen hier.",
                9, FontStyle.Regular, TextMuted);
            coverExplanation.SetBounds(18, 46,
                coverModeCard.Width - 36, 44);
            coverExplanation.Anchor = AnchorStyles.Top |
                AnchorStyles.Left | AnchorStyles.Right;
            coverModeCard.Controls.Add(coverExplanation);

            Panel coverEditorCard = new Panel();
            coverEditorCard.BackColor = Card;
            coverEditorCard.BorderStyle = BorderStyle.FixedSingle;
            coverEditorCard.SetBounds(0, 114,
                coverSettingsPanel.Width, 160);
            coverEditorCard.Anchor = AnchorStyles.Top |
                AnchorStyles.Left | AnchorStyles.Right;
            coverSettingsPanel.Controls.Add(coverEditorCard);

            Label coverTitleCaption = MakeDialogLabel(
                "Koptekst", 9, FontStyle.Bold, TextPrimary);
            coverTitleCaption.SetBounds(16, 10, 96, 32);
            coverEditorCard.Controls.Add(coverTitleCaption);

            coverTitleInput = new TextBox();
            coverTitleInput.Font = new Font("Segoe UI", 11, FontStyle.Regular);
            coverTitleInput.MaxLength = 60;
            coverTitleInput.SetBounds(120, 10,
                coverEditorCard.Width - 138, 32);
            coverTitleInput.Anchor = AnchorStyles.Top |
                AnchorStyles.Left | AnchorStyles.Right;
            coverTitleInput.AccessibleName = "Koptekst volledig scherm";
            coverEditorCard.Controls.Add(coverTitleInput);

            Label coverMessageCaption = MakeDialogLabel(
                "Bericht", 9, FontStyle.Bold, TextPrimary);
            coverMessageCaption.SetBounds(16, 54, 96, 32);
            coverEditorCard.Controls.Add(coverMessageCaption);

            coverMessageInput = new TextBox();
            coverMessageInput.Font = new Font("Segoe UI", 10, FontStyle.Regular);
            coverMessageInput.Multiline = true;
            coverMessageInput.AcceptsReturn = true;
            coverMessageInput.ScrollBars = ScrollBars.Vertical;
            coverMessageInput.MaxLength = 240;
            coverMessageInput.SetBounds(120, 54,
                coverEditorCard.Width - 138, 88);
            coverMessageInput.Anchor = AnchorStyles.Top |
                AnchorStyles.Left | AnchorStyles.Right;
            coverMessageInput.AccessibleName = "Bericht volledig scherm";
            coverEditorCard.Controls.Add(coverMessageInput);

            Panel coverPreviewCard = new Panel();
            coverPreviewCard.BackColor = Card;
            coverPreviewCard.BorderStyle = BorderStyle.FixedSingle;
            coverPreviewCard.SetBounds(0, 288, coverSettingsPanel.Width,
                Math.Max(110, coverSettingsPanel.Height - 288));
            coverPreviewCard.Anchor = AnchorStyles.Top |
                AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            coverSettingsPanel.Controls.Add(coverPreviewCard);

            Label coverPreviewCaption = MakeDialogLabel(
                "Voorbeeld volledig scherm", 9, FontStyle.Bold, TextMuted);
            coverPreviewCaption.SetBounds(14, 6, 220, 24);
            coverPreviewCard.Controls.Add(coverPreviewCaption);

            coverPreviewTitle = MakeDialogLabel(
                "", 22, FontStyle.Bold, Color.FromArgb(31, 38, 58));
            coverPreviewTitle.TextAlign = ContentAlignment.BottomCenter;
            coverPreviewTitle.SetBounds(20, 28,
                coverPreviewCard.Width - 40, 46);
            coverPreviewTitle.Anchor = AnchorStyles.Top |
                AnchorStyles.Left | AnchorStyles.Right;
            coverPreviewCard.Controls.Add(coverPreviewTitle);

            coverPreviewMessage = MakeDialogLabel(
                "", 12, FontStyle.Bold, Color.FromArgb(73, 82, 105));
            coverPreviewMessage.TextAlign = ContentAlignment.TopCenter;
            coverPreviewMessage.SetBounds(20, 76,
                coverPreviewCard.Width - 40,
                Math.Max(46, coverPreviewCard.Height - 82));
            coverPreviewMessage.Anchor = AnchorStyles.Top |
                AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            coverPreviewCard.Controls.Add(coverPreviewMessage);

            BuildResponsiveCoverLayout(
                coverModeCard, coverExplanation,
                coverEditorCard, coverTitleCaption, coverMessageCaption,
                coverPreviewCard, coverPreviewCaption);
            BuildResponsiveSettingsLayout(
                title, stabilityCaption, previewCaption,
                previewLightCaption, cancelButton);
            CaptureTextFitFonts(this);
            Resize += delegate
            {
                EnsureTextFitsWithinBounds();
            };

            AcceptButton = saveButton;
            CancelButton = cancelButton;
            defaultProfile.CheckedChanged += delegate { ProfileChanged(); };
            customProfile.CheckedChanged += delegate { ProfileChanged(); };
            previewLightInput.ValueChanged += delegate { UpdatePreview(); };
            previewRange.LightValueChanged += delegate
            {
                int value = previewRange.LightValue;
                if (value >= previewLightInput.Minimum &&
                    value <= previewLightInput.Maximum &&
                    previewLightInput.Value != value)
                    previewLightInput.Value = value;
            };
            stabilitySecondsInput.ValueChanged += delegate { MarkDirty(); };
            defaultCoverText.CheckedChanged += delegate
            {
                CoverTextModeChanged();
            };
            customCoverText.CheckedChanged += delegate
            {
                CoverTextModeChanged();
            };
            coverTitleInput.TextChanged += delegate
            {
                CoverTextChanged();
            };
            coverMessageInput.TextChanged += delegate
            {
                CoverTextChanged();
            };
            grid.CellValueChanged += delegate
            {
                MarkDirty();
                UpdatePreview();
            };
            grid.CurrentCellDirtyStateChanged += delegate
            {
                if (grid.IsCurrentCellDirty)
                    grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            if (useCustomMapping)
                customProfile.Checked = true;
            else
                defaultProfile.Checked = true;
            if (useCustomCoverText)
                customCoverText.Checked = true;
            else
                defaultCoverText.Checked = true;
            RenderRows();
            RefreshCoverTextControls();
            ShowSettingsSection(false);
            initialized = true;
            dirty = false;
            UpdatePreview();
            UpdateSaveState();
            FormClosed += delegate
            {
                updateStatusTimer.Stop();
                updateStatusTimer.Dispose();
            };
            RefreshUpdateStatus();
            EnsureTextFitsWithinBounds();
        }

        private void CaptureTextFitFonts(Control root)
        {
            if (root == null)
                return;
            if ((root is Label || root is Button) &&
                !textFitBaseFontSizes.ContainsKey(root))
            {
                textFitBaseFontSizes.Add(root, root.Font.Size);
                root.TextChanged += delegate
                {
                    if (!textFitActive && !root.IsDisposed)
                        OverlayForm.FitTextControl(root);
                };
                root.SizeChanged += delegate
                {
                    if (!textFitActive && !root.IsDisposed)
                        OverlayForm.FitTextControl(root);
                };
            }
            foreach (Control child in root.Controls)
                CaptureTextFitFonts(child);
        }

        private void EnsureTextFitsWithinBounds()
        {
            if (textFitActive || IsDisposed)
                return;
            textFitActive = true;
            try
            {
                RestoreTextFitFonts(this);
                OverlayForm.FitTextTree(this);
            }
            finally
            {
                textFitActive = false;
            }
        }

        private void RestoreTextFitFonts(Control root)
        {
            float baseSize;
            if (textFitBaseFontSizes.TryGetValue(root, out baseSize) &&
                Math.Abs(root.Font.Size - baseSize) > 0.05f)
                OverlayForm.ReplaceControlFont(root, new Font(
                    root.Font.FontFamily.Name,
                    baseSize, root.Font.Style));
            foreach (Control child in root.Controls)
                RestoreTextFitFonts(child);
        }

        private void BuildResponsiveCoverLayout(
            Panel coverModeCard,
            Label coverExplanation,
            Panel coverEditorCard,
            Label coverTitleCaption,
            Label coverMessageCaption,
            Panel coverPreviewCard,
            Label coverPreviewCaption)
        {
            coverSettingsPanel.SuspendLayout();
            coverSettingsPanel.Controls.Clear();
            coverSettingsPanel.AutoScroll = true;

            TableLayoutPanel coverRoot = new TableLayoutPanel();
            coverRoot.AutoSize = true;
            coverRoot.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            coverRoot.Dock = DockStyle.Top;
            coverRoot.BackColor = Background;
            coverRoot.ColumnCount = 1;
            coverRoot.RowCount = 3;
            coverRoot.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            coverRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            coverRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            coverRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            coverModeCard.Controls.Clear();
            coverModeCard.AutoSize = true;
            coverModeCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            coverModeCard.Dock = DockStyle.Fill;
            coverModeCard.Margin = new Padding(0, 0, 0, 12);
            TableLayoutPanel modeLayout = new TableLayoutPanel();
            modeLayout.AutoSize = true;
            modeLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            modeLayout.Dock = DockStyle.Fill;
            modeLayout.Padding = new Padding(16, 8, 16, 10);
            modeLayout.ColumnCount = 1;
            modeLayout.RowCount = 2;
            modeLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            modeLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            modeLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            FlowLayoutPanel coverModes = new FlowLayoutPanel();
            coverModes.AutoSize = true;
            coverModes.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            coverModes.Dock = DockStyle.Fill;
            coverModes.FlowDirection = FlowDirection.LeftToRight;
            coverModes.WrapContents = true;
            coverModes.Margin = Padding.Empty;
            defaultCoverText.AutoSize = true;
            defaultCoverText.Margin = new Padding(0, 2, 42, 4);
            customCoverText.AutoSize = true;
            customCoverText.Margin = new Padding(0, 2, 0, 4);
            coverModes.Controls.Add(defaultCoverText);
            coverModes.Controls.Add(customCoverText);

            coverExplanation.AutoSize = true;
            coverExplanation.AutoEllipsis = false;
            coverExplanation.MaximumSize = new Size(900, 0);
            coverExplanation.Margin = Padding.Empty;
            modeLayout.Controls.Add(coverModes, 0, 0);
            modeLayout.Controls.Add(coverExplanation, 0, 1);
            coverModeCard.Controls.Add(modeLayout);

            coverEditorCard.Controls.Clear();
            coverEditorCard.AutoSize = true;
            coverEditorCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            coverEditorCard.Dock = DockStyle.Fill;
            coverEditorCard.Margin = new Padding(0, 0, 0, 12);
            TableLayoutPanel editorLayout = new TableLayoutPanel();
            editorLayout.AutoSize = true;
            editorLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            editorLayout.Dock = DockStyle.Fill;
            editorLayout.Padding = new Padding(16, 10, 16, 12);
            editorLayout.ColumnCount = 2;
            editorLayout.RowCount = 2;
            editorLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.AutoSize));
            editorLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            editorLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editorLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            coverTitleCaption.AutoSize = true;
            coverTitleCaption.Margin = new Padding(0, 7, 18, 12);
            coverTitleInput.Dock = DockStyle.Fill;
            coverTitleInput.MinimumSize = new Size(
                240, coverTitleInput.PreferredHeight);
            coverTitleInput.Margin = new Padding(0, 0, 0, 12);
            coverMessageCaption.AutoSize = true;
            coverMessageCaption.Margin = new Padding(0, 7, 18, 0);
            coverMessageInput.Dock = DockStyle.Fill;
            coverMessageInput.MinimumSize = new Size(240, 100);
            coverMessageInput.Margin = Padding.Empty;
            editorLayout.Controls.Add(coverTitleCaption, 0, 0);
            editorLayout.Controls.Add(coverTitleInput, 1, 0);
            editorLayout.Controls.Add(coverMessageCaption, 0, 1);
            editorLayout.Controls.Add(coverMessageInput, 1, 1);
            coverEditorCard.Controls.Add(editorLayout);

            coverPreviewCard.Controls.Clear();
            coverPreviewCard.AutoSize = true;
            coverPreviewCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            coverPreviewCard.Dock = DockStyle.Fill;
            coverPreviewCard.MinimumSize = new Size(0, 180);
            coverPreviewCard.Margin = Padding.Empty;
            TableLayoutPanel coverPreviewLayout = new TableLayoutPanel();
            coverPreviewLayout.AutoSize = true;
            coverPreviewLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            coverPreviewLayout.Dock = DockStyle.Fill;
            coverPreviewLayout.Padding = new Padding(14, 8, 14, 14);
            coverPreviewLayout.ColumnCount = 1;
            coverPreviewLayout.RowCount = 3;
            coverPreviewLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            coverPreviewLayout.RowStyles.Add(
                new RowStyle(SizeType.AutoSize));
            coverPreviewLayout.RowStyles.Add(
                new RowStyle(SizeType.AutoSize));
            coverPreviewLayout.RowStyles.Add(
                new RowStyle(SizeType.AutoSize));

            coverPreviewCaption.AutoSize = true;
            coverPreviewCaption.Margin = new Padding(0, 0, 0, 12);
            coverPreviewTitle.AutoSize = true;
            coverPreviewTitle.AutoEllipsis = false;
            // GDI+ wraps even an unusually long word at character
            // boundaries. That keeps custom titles fully visible instead of
            // silently clipping them at the right edge.
            coverPreviewTitle.UseCompatibleTextRendering = true;
            coverPreviewTitle.MaximumSize = new Size(900, 0);
            coverPreviewTitle.Dock = DockStyle.Fill;
            coverPreviewTitle.TextAlign = ContentAlignment.MiddleCenter;
            coverPreviewTitle.Margin = new Padding(8, 0, 8, 8);
            coverPreviewMessage.AutoSize = true;
            coverPreviewMessage.AutoEllipsis = false;
            coverPreviewMessage.UseCompatibleTextRendering = true;
            coverPreviewMessage.MaximumSize = new Size(900, 0);
            coverPreviewMessage.Dock = DockStyle.Fill;
            coverPreviewMessage.TextAlign = ContentAlignment.TopCenter;
            coverPreviewMessage.Margin = new Padding(8, 0, 8, 0);
            coverPreviewLayout.Controls.Add(coverPreviewCaption, 0, 0);
            coverPreviewLayout.Controls.Add(coverPreviewTitle, 0, 1);
            coverPreviewLayout.Controls.Add(coverPreviewMessage, 0, 2);
            coverPreviewCard.Controls.Add(coverPreviewLayout);

            coverRoot.Controls.Add(coverModeCard, 0, 0);
            coverRoot.Controls.Add(coverEditorCard, 0, 1);
            coverRoot.Controls.Add(coverPreviewCard, 0, 2);
            coverSettingsPanel.Controls.Add(coverRoot);

            bool updatingCoverExtent = false;
            EventHandler updateCoverExtent = delegate
            {
                if (updatingCoverExtent || coverRoot.IsDisposed)
                    return;
                updatingCoverExtent = true;
                try
                {
                    int textWidth = Math.Max(240,
                        coverRoot.ClientSize.Width - 64);
                    coverExplanation.MaximumSize =
                        new Size(textWidth, 0);
                    coverPreviewTitle.MaximumSize =
                        new Size(textWidth, 0);
                    coverPreviewMessage.MaximumSize =
                        new Size(textWidth, 0);
                    coverRoot.PerformLayout();
                    int requiredRootHeight = 1;
                    foreach (Control section in coverRoot.Controls)
                        requiredRootHeight = Math.Max(
                            requiredRootHeight,
                            section.Bottom + section.Margin.Bottom);
                    if (coverRoot.MinimumSize.Height !=
                        requiredRootHeight)
                        coverRoot.MinimumSize = new Size(
                            0, requiredRootHeight);
                    if (coverRoot.Height < requiredRootHeight)
                        coverRoot.Height = requiredRootHeight;
                    coverSettingsPanel.AutoScrollMinSize = new Size(
                        0, Math.Max(requiredRootHeight,
                            coverRoot.PreferredSize.Height) + 12);
                }
                finally
                {
                    updatingCoverExtent = false;
                }
            };
            coverRoot.SizeChanged += updateCoverExtent;
            coverSettingsPanel.SizeChanged += updateCoverExtent;
            updateCoverExtent(this, EventArgs.Empty);
            coverSettingsPanel.ResumeLayout(true);
        }

        private void BuildResponsiveSettingsLayout(
            Label title,
            Label stabilityCaption,
            Label previewCaption,
            Label previewLightCaption,
            Button cancelButton)
        {
            SuspendLayout();

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.AutoScroll = true;
            root.BackColor = Background;
            root.Padding = new Padding(24, 8, 24, 12);
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            TableLayoutPanel header = new TableLayoutPanel();
            header.AutoSize = true;
            header.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            header.Dock = DockStyle.Fill;
            header.Margin = new Padding(0, 0, 0, 6);
            header.ColumnCount = 2;
            header.RowCount = 2;
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            header.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            FlowLayoutPanel navigation = new FlowLayoutPanel();
            navigation.AutoSize = true;
            navigation.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            navigation.Dock = DockStyle.Fill;
            navigation.FlowDirection = FlowDirection.LeftToRight;
            navigation.WrapContents = true;
            navigation.Margin = Padding.Empty;

            title.AutoSize = true;
            title.Margin = new Padding(0, 5, 16, 4);
            isoSettingsTab.AutoSize = true;
            isoSettingsTab.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            isoSettingsTab.MinimumSize = new Size(132, 52);
            isoSettingsTab.Padding = new Padding(12, 0, 12, 0);
            isoSettingsTab.Margin = new Padding(0, 0, 8, 4);
            coverSettingsTab.AutoSize = true;
            coverSettingsTab.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            coverSettingsTab.MinimumSize = new Size(176, 52);
            coverSettingsTab.Padding = new Padding(12, 0, 12, 0);
            coverSettingsTab.Margin = new Padding(0, 0, 0, 4);
            navigation.Controls.Add(title);
            navigation.Controls.Add(isoSettingsTab);
            navigation.Controls.Add(coverSettingsTab);

            updateButton.AutoSize = true;
            updateButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            updateButton.MinimumSize = new Size(216, 52);
            updateButton.Padding = new Padding(12, 0, 12, 0);
            updateButton.Margin = new Padding(16, 0, 0, 4);

            boothAndVersionLabel.AutoEllipsis = false;
            boothAndVersionLabel.AutoSize = true;
            boothAndVersionLabel.MaximumSize = new Size(900, 0);
            boothAndVersionLabel.Anchor = AnchorStyles.Top |
                AnchorStyles.Right;
            boothAndVersionLabel.Margin = Padding.Empty;
            boothAndVersionLabel.TextAlign = ContentAlignment.TopRight;

            header.Controls.Add(navigation, 0, 0);
            header.Controls.Add(updateButton, 1, 0);
            header.Controls.Add(boothAndVersionLabel, 0, 1);
            header.SetColumnSpan(boothAndVersionLabel, 2);

            profileCard.AutoSize = true;
            profileCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            profileCard.Dock = DockStyle.Fill;
            profileCard.Margin = new Padding(0, 0, 0, 12);
            profileCard.Controls.Clear();

            TableLayoutPanel profileLayout = new TableLayoutPanel();
            profileLayout.AutoSize = true;
            profileLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            profileLayout.Dock = DockStyle.Fill;
            profileLayout.Padding = new Padding(16, 8, 16, 10);
            profileLayout.ColumnCount = 2;
            profileLayout.RowCount = 2;
            profileLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            profileLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.AutoSize));
            profileLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            profileLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            FlowLayoutPanel profileModes = new FlowLayoutPanel();
            profileModes.AutoSize = true;
            profileModes.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            profileModes.Dock = DockStyle.Fill;
            profileModes.FlowDirection = FlowDirection.LeftToRight;
            profileModes.WrapContents = true;
            profileModes.Margin = Padding.Empty;
            defaultProfile.AutoSize = true;
            defaultProfile.Font = new Font(
                "Segoe UI", 11.5f, FontStyle.Bold);
            defaultProfile.MinimumSize = new Size(0, 48);
            defaultProfile.Margin = new Padding(0, 2, 42, 4);
            customProfile.AutoSize = true;
            customProfile.Font = new Font(
                "Segoe UI", 11.5f, FontStyle.Bold);
            customProfile.MinimumSize = new Size(0, 48);
            customProfile.Margin = new Padding(0, 2, 0, 4);
            profileModes.Controls.Add(defaultProfile);
            profileModes.Controls.Add(customProfile);
            profileLayout.Controls.Add(profileModes, 0, 0);
            profileLayout.SetColumnSpan(profileModes, 2);

            explanation.AutoSize = true;
            explanation.AutoEllipsis = false;
            explanation.MaximumSize = new Size(700, 0);
            explanation.Anchor = AnchorStyles.Left |
                AnchorStyles.Top | AnchorStyles.Right;
            explanation.Margin = new Padding(0, 0, 16, 0);
            profileLayout.Controls.Add(explanation, 0, 1);

            FlowLayoutPanel stability = new FlowLayoutPanel();
            stability.AutoSize = true;
            stability.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            stability.Anchor = AnchorStyles.Right;
            stability.FlowDirection = FlowDirection.LeftToRight;
            stability.WrapContents = false;
            stability.Margin = Padding.Empty;
            stabilityCaption.AutoSize = true;
            stabilityCaption.Font = new Font(
                "Segoe UI", 10.5f, FontStyle.Bold);
            stabilityCaption.Margin = new Padding(0, 14, 12, 0);
            Button stabilityMinus = MakeTouchStepButton(
                "−", "Vijf seconden minder");
            stabilityMinus.Click += delegate
            {
                decimal value = Math.Max(
                    stabilitySecondsInput.Minimum,
                    stabilitySecondsInput.Value - 5);
                stabilitySecondsInput.Value = value;
            };
            stabilitySecondsInput.AutoSize = false;
            stabilitySecondsInput.Font = new Font(
                "Segoe UI", 14, FontStyle.Bold);
            stabilitySecondsInput.MinimumSize = new Size(96, 52);
            stabilitySecondsInput.Height = 54;
            stabilitySecondsInput.Margin = new Padding(8, 0, 8, 0);
            Button stabilityPlus = MakeTouchStepButton(
                "+", "Vijf seconden meer");
            stabilityPlus.Click += delegate
            {
                decimal value = Math.Min(
                    stabilitySecondsInput.Maximum,
                    stabilitySecondsInput.Value + 5);
                stabilitySecondsInput.Value = value;
            };
            stability.Controls.Add(stabilityCaption);
            stability.Controls.Add(stabilityMinus);
            stability.Controls.Add(stabilitySecondsInput);
            stability.Controls.Add(stabilityPlus);
            profileLayout.Controls.Add(stability, 1, 1);
            profileCard.Controls.Add(profileLayout);

            previewCard.AutoSize = true;
            previewCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            previewCard.Dock = DockStyle.Fill;
            previewCard.Margin = new Padding(0, 0, 0, 12);
            previewCard.Controls.Clear();

            TableLayoutPanel previewLayout = new TableLayoutPanel();
            previewLayout.AutoSize = true;
            previewLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            previewLayout.Dock = DockStyle.Fill;
            previewLayout.Padding = new Padding(14, 10, 14, 12);
            previewLayout.ColumnCount = 4;
            previewLayout.RowCount = 2;
            previewLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            previewLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            previewLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            previewLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            previewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            previewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            previewCaption.AutoSize = true;
            previewCaption.Margin = new Padding(0, 6, 28, 0);
            previewLightCaption.AutoSize = true;
            previewLightCaption.Margin = new Padding(0, 14, 10, 0);
            Button previewMinus = MakeTouchStepButton(
                "−", "Voorbeeldlichtwaarde één lager");
            previewMinus.Click += delegate
            {
                previewLightInput.Value = Math.Max(
                    previewLightInput.Minimum,
                    previewLightInput.Value - 1);
            };
            previewLightInput.AutoSize = false;
            previewLightInput.Font = new Font(
                "Segoe UI", 14, FontStyle.Bold);
            previewLightInput.MinimumSize = new Size(92, 52);
            previewLightInput.Height = 54;
            previewLightInput.Margin = new Padding(8, 0, 8, 0);
            Button previewPlus = MakeTouchStepButton(
                "+", "Voorbeeldlichtwaarde één hoger");
            previewPlus.Click += delegate
            {
                previewLightInput.Value = Math.Min(
                    previewLightInput.Maximum,
                    previewLightInput.Value + 1);
            };
            previewText.AutoSize = true;
            previewText.AutoEllipsis = false;
            previewText.Anchor = AnchorStyles.Top |
                AnchorStyles.Right;
            previewText.Margin = Padding.Empty;
            previewRange.Dock = DockStyle.Fill;
            // Keep the two text lines, drag marker and slider comfortably
            // separated even before the DPI-aware minimum is calculated.
            previewRange.MinimumSize = new Size(0, 128);
            previewRange.Margin = Padding.Empty;
            previewLayout.Controls.Add(previewCaption, 0, 0);
            previewLayout.Controls.Add(previewLightCaption, 1, 0);
            FlowLayoutPanel previewStepper = new FlowLayoutPanel();
            previewStepper.AutoSize = true;
            previewStepper.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            previewStepper.FlowDirection = FlowDirection.LeftToRight;
            previewStepper.WrapContents = false;
            previewStepper.Margin = Padding.Empty;
            previewStepper.Controls.Add(previewMinus);
            previewStepper.Controls.Add(previewLightInput);
            previewStepper.Controls.Add(previewPlus);
            previewLayout.Controls.Add(previewStepper, 2, 0);
            previewLayout.Controls.Add(previewText, 3, 0);
            previewLayout.Controls.Add(previewRange, 0, 1);
            previewLayout.SetColumnSpan(previewRange, 4);
            previewCard.Controls.Add(previewLayout);

            bandEditorCard.Dock = DockStyle.Fill;
            // This row receives the remaining height. Keep its minimum inside
            // the actual free row height on short 720px Surface viewports; the
            // card's own scrollbar exposes every full-size touch row.
            bandEditorCard.MinimumSize = new Size(0, 52);
            bandEditorCard.Margin = new Padding(0, 0, 0, 12);

            TableLayoutPanel footer = new TableLayoutPanel();
            footer.AutoSize = true;
            footer.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            footer.Dock = DockStyle.Fill;
            footer.Margin = Padding.Empty;
            footer.ColumnCount = 3;
            footer.RowCount = 2;
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            FlowLayoutPanel rangeActions = new FlowLayoutPanel();
            rangeActions.AutoSize = true;
            rangeActions.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            rangeActions.FlowDirection = FlowDirection.LeftToRight;
            rangeActions.WrapContents = false;
            rangeActions.Margin = Padding.Empty;
            addButton.AutoSize = true;
            addButton.MinimumSize = new Size(180, 54);
            addButton.Padding = new Padding(10, 0, 10, 0);
            addButton.Margin = new Padding(0, 0, 8, 0);
            rangeActions.Controls.Add(addButton);

            FlowLayoutPanel saveActions = new FlowLayoutPanel();
            saveActions.AutoSize = true;
            saveActions.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            saveActions.FlowDirection = FlowDirection.LeftToRight;
            saveActions.WrapContents = false;
            saveActions.Margin = Padding.Empty;
            cancelButton.AutoSize = true;
            cancelButton.MinimumSize = new Size(140, 54);
            cancelButton.Padding = new Padding(14, 0, 14, 0);
            cancelButton.Margin = new Padding(0, 0, 8, 0);
            saveButton.AutoSize = true;
            saveButton.MinimumSize = new Size(160, 54);
            saveButton.Padding = new Padding(14, 0, 14, 0);
            saveButton.Margin = Padding.Empty;
            saveActions.Controls.Add(saveButton);
            saveActions.Controls.Add(cancelButton);

            footer.Controls.Add(rangeActions, 0, 0);
            footer.Controls.Add(saveActions, 2, 0);
            rangeActions.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            saveActions.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            bool layingOutFooter = false;
            EventHandler layoutFooter = delegate
            {
                if (layingOutFooter || footer.IsDisposed)
                    return;
                layingOutFooter = true;
                try
                {
                    int requiredWidth =
                        rangeActions.PreferredSize.Width +
                        saveActions.PreferredSize.Width + 24;
                    bool stack = footer.ClientSize.Width < requiredWidth;
                    if (stack)
                    {
                        footer.SetCellPosition(
                            rangeActions, new TableLayoutPanelCellPosition(0, 0));
                        footer.SetColumnSpan(rangeActions, 3);
                        footer.SetCellPosition(
                            saveActions, new TableLayoutPanelCellPosition(0, 1));
                        footer.SetColumnSpan(saveActions, 3);
                    }
                    else
                    {
                        footer.SetCellPosition(
                            rangeActions, new TableLayoutPanelCellPosition(0, 0));
                        footer.SetColumnSpan(rangeActions, 1);
                        footer.SetCellPosition(
                            saveActions, new TableLayoutPanelCellPosition(2, 0));
                        footer.SetColumnSpan(saveActions, 1);
                    }
                }
                finally
                {
                    layingOutFooter = false;
                }
            };
            footer.SizeChanged += layoutFooter;
            rangeActions.SizeChanged += layoutFooter;
            saveActions.SizeChanged += layoutFooter;
            layoutFooter(this, EventArgs.Empty);

            coverSettingsPanel.Dock = DockStyle.Fill;
            coverSettingsPanel.AutoScroll = true;
            coverSettingsPanel.Margin = new Padding(0, 0, 0, 10);

            Controls.Clear();
            root.Controls.Add(header, 0, 0);
            root.Controls.Add(profileCard, 0, 1);
            root.Controls.Add(previewCard, 0, 2);
            root.Controls.Add(bandEditorCard, 0, 3);
            root.Controls.Add(coverSettingsPanel, 0, 1);
            root.SetRowSpan(coverSettingsPanel, 3);
            root.Controls.Add(footer, 0, 4);
            Controls.Add(root);

            ResumeLayout(true);
        }

        private void StartUpdateCheck()
        {
            try
            {
                if (!File.Exists(updaterExecutablePath))
                    throw new FileNotFoundException(
                        "De automatische updater is niet geïnstalleerd.");
                try
                {
                    if (File.Exists(updaterStatusPath))
                        File.Delete(updaterStatusPath);
                }
                catch { }

                updateCheckStartedAtUtc = DateTime.UtcNow;
                SetUpdateButtonState(
                    "Controleren...", TextMuted, false,
                    "GitHub wordt gecontroleerd op een nieuwere versie.");
                updateStatusTimer.Start();
                Process.Start(new ProcessStartInfo {
                    FileName = updaterExecutablePath,
                    Arguments = "--check-now",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(
                        updaterExecutablePath)
                });
            }
            catch (Exception exception)
            {
                updateStatusTimer.Stop();
                updateCheckStartedAtUtc = DateTime.MinValue;
                SetUpdateButtonState(
                    "Opnieuw proberen", Color.FromArgb(186, 36, 36),
                    true, exception.Message);
            }
        }

        private void RefreshUpdateStatus()
        {
            DateTime nowUtc = DateTime.UtcNow;
            if (ShowUpdateTimeoutIfNeeded(nowUtc))
                return;
            if (!File.Exists(updaterStatusPath))
                return;

            Dictionary<string, string> values =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string line in File.ReadAllLines(updaterStatusPath))
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0)
                        continue;
                    values[line.Substring(0, separator)] =
                        line.Substring(separator + 1);
                }
            }
            catch
            {
                return;
            }

            DateTime statusFileUtc = DateTime.MinValue;
            try
            {
                statusFileUtc = File.GetLastWriteTimeUtc(updaterStatusPath);
            }
            catch { }
            ApplyUpdateStatusValues(values, statusFileUtc, nowUtc);
        }

        private void ApplyUpdateStatusValues(
            Dictionary<string, string> values,
            DateTime statusFileUtc, DateTime nowUtc)
        {
            string state;
            if (!values.TryGetValue("State", out state))
                return;
            string message;
            if (!values.TryGetValue("Message", out message))
                message = "";
            string available;
            if (!values.TryGetValue("AvailableVersion", out available))
                available = "";

            bool transient =
                String.Equals(state, "checking",
                    StringComparison.OrdinalIgnoreCase) ||
                String.Equals(state, "downloading",
                    StringComparison.OrdinalIgnoreCase) ||
                String.Equals(state, "installing",
                    StringComparison.OrdinalIgnoreCase);
            if (transient)
            {
                if (updateCheckStartedAtUtc == DateTime.MinValue)
                {
                    string checkedValue;
                    values.TryGetValue("Checked", out checkedValue);
                    updateCheckStartedAtUtc = ResolveUpdateStatusStartUtc(
                        checkedValue, statusFileUtc, nowUtc);
                }
                if (ShowUpdateTimeoutIfNeeded(nowUtc))
                    return;
                // Settings can be opened after an updater process has already
                // started. Keep polling that existing state as well; otherwise
                // Downloaden/Installeren would remain disabled forever.
                updateStatusTimer.Start();
            }

            if (String.Equals(state, "checking",
                StringComparison.OrdinalIgnoreCase))
                SetUpdateButtonState(
                    "Controleren...", TextMuted, false, message);
            else if (String.Equals(state, "downloading",
                StringComparison.OrdinalIgnoreCase))
                SetUpdateButtonState(
                    "Downloaden " + available, Accent, false, message);
            else if (String.Equals(state, "installing",
                StringComparison.OrdinalIgnoreCase))
                SetUpdateButtonState(
                    "Installeren " + available, Accent, false, message);
            else if (String.Equals(state, "ready", StringComparison.OrdinalIgnoreCase))
            {
                updateStatusTimer.Stop(); updateCheckStartedAtUtc = DateTime.MinValue;
                SetUpdateButtonState("Update installeren", Accent, true, message);
            }
            else if (String.Equals(state, "blocked", StringComparison.OrdinalIgnoreCase))
            {
                updateStatusTimer.Stop(); updateCheckStartedAtUtc = DateTime.MinValue;
                SetUpdateButtonState("Opnieuw controleren", Color.FromArgb(137, 87, 0), true, message);
            }
            else if (String.Equals(state, "current",
                StringComparison.OrdinalIgnoreCase))
            {
                updateStatusTimer.Stop();
                updateCheckStartedAtUtc = DateTime.MinValue;
                SetUpdateButtonState(
                    "Versie is actueel", Color.FromArgb(34, 132, 67),
                    true, message);
            }
            else if (String.Equals(state, "installed",
                StringComparison.OrdinalIgnoreCase))
            {
                updateStatusTimer.Stop();
                updateCheckStartedAtUtc = DateTime.MinValue;
                SetUpdateButtonState(
                    "Versie " + available + " geïnstalleerd",
                    Color.FromArgb(34, 132, 67), true, message);
            }
            else if (String.Equals(state, "error",
                StringComparison.OrdinalIgnoreCase))
            {
                updateStatusTimer.Stop();
                updateCheckStartedAtUtc = DateTime.MinValue;
                SetUpdateButtonState(
                    "Opnieuw proberen", Color.FromArgb(186, 36, 36),
                    true, message);
            }
        }

        private bool ShowUpdateTimeoutIfNeeded(DateTime nowUtc)
        {
            if (updateCheckStartedAtUtc == DateTime.MinValue ||
                nowUtc - updateCheckStartedAtUtc <= TimeSpan.FromMinutes(2))
                return false;
            updateStatusTimer.Stop();
            updateCheckStartedAtUtc = DateTime.MinValue;
            SetUpdateButtonState(
                "Opnieuw proberen", Color.FromArgb(186, 36, 36),
                true, "De updatecontrole duurde te lang.");
            return true;
        }

        internal static DateTime ResolveUpdateStatusStartUtc(
            string checkedValue, DateTime statusFileUtc, DateTime nowUtc)
        {
            DateTime parsed;
            if (!String.IsNullOrWhiteSpace(checkedValue) &&
                DateTime.TryParse(checkedValue, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out parsed))
            {
                parsed = parsed.ToUniversalTime();
                if (parsed <= nowUtc.AddMinutes(1))
                    return parsed;
            }
            if (statusFileUtc != DateTime.MinValue)
            {
                DateTime normalized = statusFileUtc.ToUniversalTime();
                if (normalized <= nowUtc.AddMinutes(1))
                    return normalized;
            }
            return nowUtc;
        }

        internal bool VerifyStaleUpdateRecoveryForLayoutTest()
        {
            DateTime nowUtc = DateTime.UtcNow;
            DateTime staleUtc = nowUtc.AddMinutes(-3);
            Dictionary<string, string> values =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
            values["State"] = "installing";
            values["Checked"] = staleUtc.ToString("o");
            values["AvailableVersion"] = "99.0.0";
            values["Message"] = "Bestaande installatie wordt afgerond.";
            updateCheckStartedAtUtc = DateTime.MinValue;
            ApplyUpdateStatusValues(values, staleUtc, nowUtc);
            bool recovered = updateButton.Enabled &&
                String.Equals(updateButton.Text, "Opnieuw proberen",
                    StringComparison.Ordinal) &&
                !updateStatusTimer.Enabled;
            updateCheckStartedAtUtc = DateTime.MinValue;
            return recovered;
        }

        internal bool VerifyTouchProfileEditingForLayoutTest()
        {
            if (!customProfile.Checked)
                customProfile.Checked = true;
            RenderRows();
            if (CustomBands.Count < 2 || bandRowsHost.Controls.Count < 3)
                return false;

            TableLayoutPanel firstRow =
                bandRowsHost.GetControlFromPosition(0, 1)
                    as TableLayoutPanel;
            if (firstRow == null)
                return false;
            TouchNumberInput maximumInput = null;
            ComboBox isoInput = null;
            foreach (Control control in firstRow.Controls)
            {
                if (maximumInput == null)
                    maximumInput = control as TouchNumberInput;
                if (isoInput == null)
                    isoInput = control as ComboBox;
            }
            if (maximumInput == null || isoInput == null ||
                isoInput.Items.Count < 2)
                return false;

            int originalMaximum = CustomBands[0].MaximumLight;
            int originalIso = CustomBands[0].Iso;
            maximumInput.ValueText =
                (originalMaximum + 1).ToString();
            bool maximumChanged =
                CustomBands[0].MaximumLight == originalMaximum + 1 &&
                Convert.ToInt32(grid.Rows[1].Cells[0].Value) ==
                    originalMaximum + 2;

            maximumInput.ValueText = originalMaximum.ToString();
            int alternativeIndex = isoInput.SelectedIndex == 0 ? 1 : 0;
            isoInput.SelectedIndex = alternativeIndex;
            bool isoChanged = CustomBands[0].Iso ==
                (int)isoInput.SelectedItem;
            CustomBands[0].Iso = originalIso;
            RenderRows();
            return maximumChanged && isoChanged;
        }

        private void SetUpdateButtonState(
            string text, Color color, bool enabled, string description)
        {
            updateButton.Text = text;
            updateButton.Enabled = enabled;
            updateButton.ForeColor = color;
            updateButton.BackColor = Card;
            updateButton.FlatAppearance.BorderColor = color;
            updateButton.AccessibleDescription = description;
            boothAndVersionLabel.Text =
                (String.IsNullOrWhiteSpace(description)
                    ? ""
                    : description + "  ·  ") +
                "Booth: " + boothName + "  ·  Versie " +
                BuildInfo.Version;
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.S))
            {
                SaveAndClose();
                return true;
            }
            return base.ProcessCmdKey(ref message, keyData);
        }

        private void MarkDirty()
        {
            if (!initialized || rendering)
                return;
            dirty = true;
            UpdateSaveState();
        }

        private void UpdateSaveState()
        {
            if (saveButton == null)
                return;
            saveButton.Enabled = initialized && dirty;
            saveButton.BackColor = saveButton.Enabled
                ? Accent
                : Color.FromArgb(225, 228, 233);
            saveButton.ForeColor = saveButton.Enabled
                ? Color.White
                : Color.FromArgb(112, 119, 128);
        }

        private void ShowSettingsSection(bool fullpage)
        {
            showingCoverSettings = fullpage;
            profileCard.Visible = !fullpage;
            previewCard.Visible = !fullpage;
            // The grid remains an internal data model. The visible editor uses
            // large touch controls instead of tiny cells and drop-down arrows.
            grid.Visible = false;
            bandEditorCard.Visible = !fullpage;
            coverSettingsPanel.Visible = fullpage;
            if (fullpage)
            {
                addButton.Visible = false;
                removeButton.Visible = false;
                coverSettingsPanel.BringToFront();
            }
            else
            {
                addButton.Visible = customProfile.Checked;
                removeButton.Visible = customProfile.Checked;
            }
            SetSectionButtonStyle(isoSettingsTab, !fullpage);
            SetSectionButtonStyle(coverSettingsTab, fullpage);
            saveButton.BringToFront();
        }

        internal void ShowSectionForLayoutTest(
            bool fullpage, bool useMaximumText)
        {
            boothAndVersionLabel.Text =
                "Updatecontrole voltooid · Booth: " +
                "JVDP-SURFACE-BOOTH-MET-EEN-LANGE-NAAM" +
                " · Versie " + BuildInfo.Version;
            if (useMaximumText)
            {
                customCoverText.Checked = true;
                coverTitleInput.Text = new string('W', 60);
                coverMessageInput.Text =
                    "Dit is een maximale testtekst voor de volledige " +
                    "schermweergave. Alle woorden moeten leesbaar blijven, " +
                    "automatisch afbreken en binnen hun eigen vlak staan. " +
                    "Geen regel mag onder een knop, titel, invoerveld of " +
                    "voorbeeld terechtkomen. Ook op een compact Surface-" +
                    "scherm blijft deze volledige tekst bereikbaar en " +
                    "duidelijk zichtbaar voor de gebruiker.";
                if (coverMessageInput.Text.Length < 240)
                    coverMessageInput.Text = coverMessageInput.Text.PadRight(
                        240, '.');
            }
            else
            {
                // Exercise the shared six-range production default in every
                // settings matrix case, independent of this PC's saved booth
                // profile.
                CustomBands = CloneBands(defaultBands);
                customProfile.Checked = true;
                RenderRows();
            }
            ShowSettingsSection(fullpage);
            EnsureTextFitsWithinBounds();
        }

        private static void SetSectionButtonStyle(Button button, bool active)
        {
            button.FlatAppearance.BorderSize = active ? 0 : 1;
            button.BackColor = active ? Accent : Card;
            button.ForeColor = active ? Color.White : Accent;
        }

        private void CoverTextModeChanged()
        {
            if (renderingCoverText)
                return;
            RefreshCoverTextControls();
            MarkDirty();
        }

        private void CoverTextChanged()
        {
            if (renderingCoverText)
                return;
            if (customCoverText.Checked)
            {
                draftCustomCoverTitle = coverTitleInput.Text;
                draftCustomCoverMessage = coverMessageInput.Text;
                MarkDirty();
            }
            UpdateCoverTextPreview();
        }

        private void RefreshCoverTextControls()
        {
            renderingCoverText = true;
            try
            {
                bool custom = customCoverText.Checked;
                coverTitleInput.Enabled = custom;
                coverMessageInput.Enabled = custom;
                coverTitleInput.Text = custom
                    ? draftCustomCoverTitle : defaultCoverTitle;
                coverMessageInput.Text = custom
                    ? draftCustomCoverMessage : defaultCoverMessage;
            }
            finally
            {
                renderingCoverText = false;
            }
            UpdateCoverTextPreview();
        }

        private void UpdateCoverTextPreview()
        {
            if (coverPreviewTitle == null || coverPreviewMessage == null)
                return;
            coverPreviewTitle.Text = coverTitleInput.Text;
            coverPreviewMessage.Text = coverMessageInput.Text;
        }

        private void UpdatePreview()
        {
            if (previewRange == null || previewLightInput == null)
                return;
            List<IsoBand> bands = GetPreviewBands();
            int light = (int)previewLightInput.Value;
            int iso = -1;
            foreach (IsoBand band in bands)
            {
                if (light <= band.MaximumLight)
                {
                    iso = band.Iso;
                    break;
                }
            }
            previewRange.UpdateData(bands, light, iso);
            previewText.Text = iso > 0
                ? "Licht " + light + " → ISO " + iso
                : "Ongeldige indeling";
            previewText.ForeColor = iso > 0
                ? Color.FromArgb(20, 102, 196)
                : Color.FromArgb(186, 36, 36);
        }

        private List<IsoBand> GetPreviewBands()
        {
            if (!customProfile.Checked || grid.Rows.Count == 0)
                return CloneBands(defaultBands);
            try
            {
                List<IsoBand> result = new List<IsoBand>();
                int previous = -1;
                foreach (DataGridViewRow row in grid.Rows)
                {
                    int maximum = Convert.ToInt32(row.Cells[1].Value);
                    int iso = Convert.ToInt32(row.Cells[2].Value);
                    if (maximum <= previous || maximum > 100)
                        throw new InvalidOperationException();
                    result.Add(new IsoBand(maximum, iso));
                    previous = maximum;
                }
                if (result.Count == 0 || previous != 100)
                    throw new InvalidOperationException();
                return result;
            }
            catch
            {
                return CloneBands(CustomBands);
            }
        }

        private void ProfileChanged()
        {
            if (rendering)
                return;
            if (renderedCustom && !customProfile.Checked)
                CaptureCustomBands(false);
            RenderRows();
            MarkDirty();
        }

        private void RenderRows()
        {
            rendering = true;
            try
            {
                bool custom = customProfile.Checked;
                renderedCustom = custom;
                List<IsoBand> source = custom ? CustomBands : defaultBands;
                grid.Rows.Clear();
                int lower = 0;
                for (int index = 0; index < source.Count; index++)
                {
                    IsoBand band = source[index];
                    int row = grid.Rows.Add(lower, band.MaximumLight, band.Iso);
                    grid.Rows[row].Cells[0].ReadOnly = true;
                    grid.Rows[row].Cells[1].ReadOnly =
                        !custom || index == source.Count - 1;
                    lower = band.MaximumLight + 1;
                }
                grid.ReadOnly = !custom;
                if (custom)
                {
                    grid.Columns[0].ReadOnly = true;
                    grid.Columns[1].ReadOnly = false;
                    grid.Rows[grid.Rows.Count - 1].Cells[1].ReadOnly = true;
                }
                ((DataGridViewComboBoxColumn)grid.Columns[2]).DisplayStyle = custom
                    ? DataGridViewComboBoxDisplayStyle.DropDownButton
                    : DataGridViewComboBoxDisplayStyle.Nothing;
                addButton.Enabled = custom && source.Count < 8;
                removeButton.Enabled = custom && source.Count > 1;
                addButton.Visible = custom && !showingCoverSettings;
                removeButton.Visible = custom && !showingCoverSettings;
                explanation.Text = custom
                    ? "Gebruik de grote − en + knoppen. De bereiken sluiten " +
                      "automatisch op elkaar aan."
                    : "Dit standaardprofiel wordt vanuit de centrale " +
                      "software-update voor alle booths beheerd.";
                grid.ClearSelection();
                RenderTouchBandEditors(source, custom);
                UpdatePreview();
            }
            finally
            {
                rendering = false;
            }
        }

        private void RenderTouchBandEditors(
            List<IsoBand> source, bool custom)
        {
            renderingTouchEditors = true;
            bandRowsHost.SuspendLayout();
            try
            {
                bandRowsHost.Controls.Clear();
                bandRowsHost.RowStyles.Clear();
                touchRangeLabels.Clear();
                bandRowsHost.RowCount = source.Count + 1;

                Label heading = MakeDialogLabel(
                    custom
                        ? "Lichtbereiken aanpassen"
                        : "Standaard lichtbereiken",
                    12.5f, FontStyle.Bold, TextPrimary);
                heading.AutoSize = true;
                heading.Margin = new Padding(4, 0, 4, 10);
                bandRowsHost.Controls.Add(heading, 0, 0);
                bandRowsHost.RowStyles.Add(
                    new RowStyle(SizeType.AutoSize));

                int lower = 0;
                for (int index = 0; index < source.Count; index++)
                {
                    int rowIndex = index;
                    IsoBand band = source[index];
                    TableLayoutPanel row = new TableLayoutPanel();
                    row.AutoSize = true;
                    row.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                    row.Dock = DockStyle.Top;
                    row.BackColor = index % 2 == 0
                        ? Color.FromArgb(250, 251, 252)
                        : Card;
                    row.Margin = new Padding(0, 0, 0, 8);
                    row.Padding = new Padding(10, 8, 10, 8);
                    row.ColumnCount = 8;
                    row.RowCount = 1;
                    row.ColumnStyles.Add(
                        new ColumnStyle(SizeType.AutoSize));
                    row.ColumnStyles.Add(
                        new ColumnStyle(SizeType.AutoSize));
                    row.ColumnStyles.Add(
                        new ColumnStyle(SizeType.AutoSize));
                    row.ColumnStyles.Add(
                        new ColumnStyle(SizeType.AutoSize));
                    row.ColumnStyles.Add(
                        new ColumnStyle(SizeType.AutoSize));
                    row.ColumnStyles.Add(
                        new ColumnStyle(SizeType.AutoSize));
                    row.ColumnStyles.Add(
                        new ColumnStyle(SizeType.AutoSize));
                    row.ColumnStyles.Add(
                        new ColumnStyle(SizeType.Percent, 100));
                    row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    row.AccessibleName = "Lichtbereik " + (index + 1);

                    Label rangeLabel = MakeDialogLabel(
                        lower + "–" + band.MaximumLight,
                        13, FontStyle.Bold, TextPrimary);
                    rangeLabel.AutoSize = true;
                    rangeLabel.MinimumSize = new Size(112, 68);
                    rangeLabel.TextAlign = ContentAlignment.MiddleLeft;
                    rangeLabel.Margin = new Padding(0, 0, 16, 0);
                    rangeLabel.AccessibleName =
                        "Grenzen van lichtbereik " + (index + 1);
                    touchRangeLabels.Add(rangeLabel);

                    if (!custom)
                    {
                        Label fixedIso = MakeDialogLabel(
                            "ISO " + band.Iso,
                            13, FontStyle.Bold, Accent);
                        fixedIso.AutoSize = true;
                        fixedIso.MinimumSize = new Size(160, 68);
                        fixedIso.TextAlign = ContentAlignment.MiddleLeft;
                        fixedIso.Margin = Padding.Empty;
                        fixedIso.AccessibleName =
                            "Vaste Darkroom ISO voor lichtbereik " +
                            (index + 1);
                        row.Controls.Add(rangeLabel, 0, 0);
                        row.Controls.Add(fixedIso, 1, 0);
                        row.Controls.Add(new Panel(), 7, 0);
                        bandRowsHost.Controls.Add(row, 0, index + 1);
                        bandRowsHost.RowStyles.Add(
                            new RowStyle(SizeType.AutoSize));
                        lower = band.MaximumLight + 1;
                        continue;
                    }

                    Button decrease = MakeTouchStepButton(
                        "−", "Eindwaarde van bereik " +
                        (index + 1) + " één lager");
                    decrease.Enabled = custom && index < source.Count - 1;

                    TouchNumberInput maximumInput =
                        new TouchNumberInput();
                    maximumInput.ValueText =
                        band.MaximumLight.ToString();
                    maximumInput.Margin = new Padding(10, 0, 10, 0);
                    maximumInput.InputReadOnly =
                        index == source.Count - 1;
                    maximumInput.AccessibleName =
                        "Eindwaarde van lichtbereik " + (index + 1);

                    Button increase = MakeTouchStepButton(
                        "+", "Eindwaarde van bereik " +
                        (index + 1) + " één hoger");
                    increase.Enabled = custom && index < source.Count - 1;

                    Label isoCaption = MakeDialogLabel(
                        "wordt ISO", 10.5f, FontStyle.Regular, TextMuted);
                    isoCaption.AutoSize = true;
                    isoCaption.MinimumSize = new Size(94, 68);
                    isoCaption.TextAlign = ContentAlignment.MiddleRight;
                    isoCaption.Margin = new Padding(18, 0, 12, 0);

                    ComboBox isoInput = new ComboBox();
                    isoInput.DropDownStyle = ComboBoxStyle.DropDownList;
                    isoInput.Font = new Font(
                        "Segoe UI", 16, FontStyle.Bold);
                    isoInput.IntegralHeight = false;
                    isoInput.DropDownHeight = 300;
                    isoInput.DrawMode = DrawMode.OwnerDrawFixed;
                    isoInput.ItemHeight = 52;
                    isoInput.MinimumSize = new Size(170, 64);
                    isoInput.Width = 170;
                    isoInput.Margin = Padding.Empty;
                    isoInput.Enabled = custom;
                    isoInput.AccessibleName =
                        "Darkroom ISO voor lichtbereik " + (index + 1);
                    foreach (int supportedIso in supportedIsos)
                        isoInput.Items.Add(supportedIso);
                    isoInput.SelectedItem = band.Iso;
                    isoInput.DrawItem += delegate(object sender,
                        DrawItemEventArgs draw)
                    {
                        draw.DrawBackground();
                        if (draw.Index >= 0 &&
                            draw.Index < isoInput.Items.Count)
                        {
                            TextRenderer.DrawText(
                                draw.Graphics,
                                isoInput.Items[draw.Index].ToString(),
                                isoInput.Font,
                                draw.Bounds,
                                isoInput.ForeColor,
                                TextFormatFlags.Left |
                                TextFormatFlags.VerticalCenter |
                                TextFormatFlags.NoPrefix);
                        }
                        draw.DrawFocusRectangle();
                    };

                    Button delete = MakeDialogButton(
                        "Verwijder", false);
                    delete.AutoSize = true;
                    delete.MinimumSize = new Size(136, 68);
                    delete.Font = new Font(
                        "Segoe UI", 10.5f, FontStyle.Bold);
                    delete.Margin = new Padding(12, 0, 0, 0);
                    delete.Visible = custom;
                    delete.Enabled = custom && source.Count > 1;
                    delete.AccessibleName =
                        "Lichtbereik " + (index + 1) + " verwijderen";

                    decrease.Click += delegate
                    {
                        int value;
                        if (!Int32.TryParse(
                                maximumInput.ValueText, out value))
                            value = CustomBands[rowIndex].MaximumLight;
                        int minimum = rowIndex == 0 ? 0 :
                            CustomBands[rowIndex - 1].MaximumLight + 1;
                        if (value > minimum)
                            maximumInput.ValueText =
                                (value - 1).ToString();
                    };
                    increase.Click += delegate
                    {
                        int value;
                        if (!Int32.TryParse(
                                maximumInput.ValueText, out value))
                            value = CustomBands[rowIndex].MaximumLight;
                        int maximum = rowIndex < CustomBands.Count - 1
                            ? CustomBands[rowIndex + 1].MaximumLight - 1
                            : 100;
                        if (value < maximum)
                            maximumInput.ValueText =
                                (value + 1).ToString();
                    };
                    maximumInput.ValueTextChanged += delegate
                    {
                        int parsed;
                        if (Int32.TryParse(
                                maximumInput.ValueText, out parsed))
                            ApplyTouchMaximum(rowIndex, parsed);
                    };
                    maximumInput.InputLeave += delegate
                    {
                        if (rowIndex < CustomBands.Count)
                            maximumInput.ValueText =
                                CustomBands[rowIndex].MaximumLight.ToString();
                    };
                    isoInput.SelectedIndexChanged += delegate
                    {
                        if (isoInput.SelectedItem != null)
                            ApplyTouchIso(rowIndex,
                                (int)isoInput.SelectedItem);
                    };
                    delete.Click += delegate
                    {
                        RemoveRangeAt(rowIndex);
                    };

                    row.Controls.Add(rangeLabel, 0, 0);
                    row.Controls.Add(decrease, 1, 0);
                    row.Controls.Add(maximumInput, 2, 0);
                    row.Controls.Add(increase, 3, 0);
                    row.Controls.Add(isoCaption, 4, 0);
                    row.Controls.Add(isoInput, 5, 0);
                    row.Controls.Add(delete, 6, 0);
                    row.Controls.Add(new Panel(), 7, 0);
                    bandRowsHost.Controls.Add(row, 0, index + 1);
                    bandRowsHost.RowStyles.Add(
                        new RowStyle(SizeType.AutoSize));
                    lower = band.MaximumLight + 1;
                }
            }
            finally
            {
                bandRowsHost.ResumeLayout(true);
                renderingTouchEditors = false;
            }
        }

        private void ApplyTouchMaximum(int rowIndex, int maximum)
        {
            if (renderingTouchEditors || !customProfile.Checked ||
                rowIndex < 0 || rowIndex >= grid.Rows.Count - 1)
                return;
            int minimum = rowIndex == 0 ? 0 :
                CustomBands[rowIndex - 1].MaximumLight + 1;
            int allowedMaximum =
                CustomBands[rowIndex + 1].MaximumLight - 1;
            if (maximum < minimum || maximum > allowedMaximum)
                return;
            grid.Rows[rowIndex].Cells[1].Value = maximum;
            grid.Rows[rowIndex + 1].Cells[0].Value = maximum + 1;
            if (!CaptureCustomBands(false))
                return;
            int lower = 0;
            for (int index = 0;
                index < CustomBands.Count &&
                index < touchRangeLabels.Count; index++)
            {
                touchRangeLabels[index].Text = lower + "–" +
                    CustomBands[index].MaximumLight;
                lower = CustomBands[index].MaximumLight + 1;
            }
            UpdatePreview();
            MarkDirty();
        }

        private void ApplyTouchIso(int rowIndex, int iso)
        {
            if (renderingTouchEditors || !customProfile.Checked ||
                rowIndex < 0 || rowIndex >= grid.Rows.Count)
                return;
            grid.Rows[rowIndex].Cells[2].Value = iso;
            if (!CaptureCustomBands(false))
                return;
            UpdatePreview();
            MarkDirty();
        }

        private void RemoveRangeAt(int rowIndex)
        {
            if (!customProfile.Checked || !CaptureCustomBands(true) ||
                CustomBands.Count <= 1 || rowIndex < 0 ||
                rowIndex >= CustomBands.Count)
                return;
            CustomBands.RemoveAt(rowIndex);
            CustomBands[CustomBands.Count - 1].MaximumLight = 100;
            RenderRows();
            MarkDirty();
        }

        private bool CaptureCustomBands(bool showError)
        {
            try
            {
                grid.EndEdit();
                List<IsoBand> bands = new List<IsoBand>();
                int previous = -1;
                foreach (DataGridViewRow row in grid.Rows)
                {
                    int maximum = Convert.ToInt32(row.Cells[1].Value);
                    int iso = Convert.ToInt32(row.Cells[2].Value);
                    if (maximum <= previous || maximum > 100 ||
                        Array.IndexOf(supportedIsos, iso) < 0)
                        throw new InvalidOperationException();
                    bands.Add(new IsoBand(maximum, iso));
                    previous = maximum;
                }
                if (bands.Count < 1 || bands.Count > 8 || previous != 100)
                    throw new InvalidOperationException();
                CustomBands = bands;
                return true;
            }
            catch
            {
                if (showError)
                    MessageBox.Show(this,
                        "Controleer de lichtbereiken. Ze moeten zonder gaten " +
                        "oplopen van 0 tot en met 100.",
                        "Ongeldig ISO-profiel", MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                return false;
            }
        }

        private void AddRange()
        {
            if (!customProfile.Checked || !CaptureCustomBands(true) ||
                CustomBands.Count >= 8)
                return;
            int last = CustomBands.Count - 1;
            int lower = last == 0 ? 0 : CustomBands[last - 1].MaximumLight + 1;
            if (lower >= 100)
            {
                MessageBox.Show(this, "Er is geen ruimte voor nog een bereik.",
                    "Bereik toevoegen", MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
            int split = (lower + 99) / 2;
            CustomBands.Insert(last,
                new IsoBand(split, CustomBands[last].Iso));
            RenderRows();
            grid.ClearSelection();
            grid.Rows[last].Selected = true;
            MarkDirty();
        }

        private void RemoveRange()
        {
            if (!customProfile.Checked || !CaptureCustomBands(true) ||
                CustomBands.Count <= 1)
                return;
            int selected = grid.SelectedRows.Count > 0
                ? grid.SelectedRows[0].Index
                : CustomBands.Count - 2;
            selected = Math.Max(0, Math.Min(CustomBands.Count - 1, selected));
            CustomBands.RemoveAt(selected);
            CustomBands[CustomBands.Count - 1].MaximumLight = 100;
            RenderRows();
            MarkDirty();
        }

        private void SaveAndClose()
        {
            if (!dirty)
                return;
            if (customProfile.Checked && !CaptureCustomBands(true))
                return;
            if (customCoverText.Checked &&
                (String.IsNullOrWhiteSpace(draftCustomCoverTitle) ||
                 String.IsNullOrWhiteSpace(draftCustomCoverMessage)))
            {
                ShowSettingsSection(true);
                MessageBox.Show(this,
                    "Vul zowel de koptekst als het bericht voor het " +
                    "volledige scherm in.",
                    "Tekst ontbreekt", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
            UseCustomMapping = customProfile.Checked;
            UseCustomCoverText = customCoverText.Checked;
            if (Persist != null && !Persist()) return;
            DialogResult = DialogResult.OK;
            Close();
        }

        private static List<IsoBand> CloneBands(List<IsoBand> source)
        {
            List<IsoBand> copy = new List<IsoBand>();
            foreach (IsoBand band in source)
                copy.Add(band.Clone());
            return copy;
        }

        private static Label MakeDialogLabel(
            string text, float size, FontStyle style, Color color)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = false;
            label.Font = new Font("Segoe UI", size, style);
            label.ForeColor = color;
            label.BackColor = Color.Transparent;
            label.TextAlign = ContentAlignment.MiddleLeft;
            return label;
        }

        private static Button MakeDialogButton(string text, bool primary)
        {
            Button button = new Button();
            button.Text = text;
            button.Font = new Font("Segoe UI", 11, FontStyle.Bold);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Accent;
            button.BackColor = primary ? Accent : Card;
            button.ForeColor = primary ? Color.White : Accent;
            button.UseVisualStyleBackColor = false;
            button.MinimumSize = new Size(48, 48);
            button.AccessibleRole = AccessibleRole.PushButton;
            return button;
        }

        private static Button MakeTouchStepButton(
            string text, string accessibleName)
        {
            Button button = MakeDialogButton(text, false);
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.MinimumSize = new Size(68, 68);
            button.Padding = new Padding(18, 8, 18, 8);
            button.Font = new Font("Segoe UI", 18, FontStyle.Bold);
            button.Margin = Padding.Empty;
            button.AccessibleName = accessibleName;
            return button;
        }
    }

}
