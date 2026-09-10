using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using LidFlow.App.Monitors;
using LidFlow.Core.Configuration;
using LidFlow.Core.Monitors;

namespace LidFlow.App.Settings;

/// <summary>
/// The settings dialog.
/// <para>
/// Deliberately plain: standard Windows controls, standard spacing, no custom
/// painting. The brief for this app is that the effect should feel like part of
/// the hardware, and a settings window that draws attention to itself works
/// against that. Only the values that meaningfully change behaviour are exposed;
/// the full tuning surface stays in config.json, where a developer tuning the
/// shader wants it.
/// </para>
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly LidFlowConfig _working;
    private readonly IReadOnlyList<DisplayTarget> _displays;

    private readonly CheckBox _enableAnimation = new();
    private readonly NumericUpDown _closeDuration = new();
    private readonly NumericUpDown _openDuration = new();
    private readonly TrackBar _blur = new();
    private readonly TrackBar _edgeDarkness = new();
    private readonly ComboBox _quality = new();

    private readonly ComboBox _monitorMode = new();
    private readonly ComboBox _manualDisplay = new();

    private readonly CheckBox _startWithWindows = new();
    private readonly CheckBox _skipFullscreen = new();
    private readonly CheckBox _previewHotkeys = new();
    private readonly CheckBox _showDebugHud = new();

    public SettingsForm(LidFlowConfig config, IReadOnlyList<DisplayTarget> displays)
    {
        _working = (config ?? throw new ArgumentNullException(nameof(config))).Clone();
        _displays = displays ?? Array.Empty<DisplayTarget>();

        Text = "LidFlow";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI Variable Text", 9f, FontStyle.Regular, GraphicsUnit.Point);
        ClientSize = new Size(470, 560);
        ShowIcon = true;
        Icon = Diagnostics.AppIcon.Create();

        BuildLayout();
        LoadFromConfig();
    }

    /// <summary>The edited configuration, valid once the dialog returns OK.</summary>
    public LidFlowConfig Result => _working;

    private void BuildLayout()
    {
        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(14),
        };

        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(BuildAnimationGroup());
        root.Controls.Add(BuildMonitorGroup());
        root.Controls.Add(BuildBehaviourGroup());
        root.Controls.Add(new Panel { Dock = DockStyle.Fill });
        root.Controls.Add(BuildButtons());

        Controls.Add(root);
    }

    private Control BuildAnimationGroup()
    {
        GroupBox group = new()
        {
            Text = "Animation",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10),
        };

        TableLayoutPanel grid = NewGrid(6);

        _enableAnimation.Text = "Enable the lid animation";
        _enableAnimation.AutoSize = true;
        grid.Controls.Add(_enableAnimation, 0, 0);
        grid.SetColumnSpan(_enableAnimation, 2);

        _closeDuration.Minimum = 80;
        _closeDuration.Maximum = 2000;
        _closeDuration.Increment = 10;
        _closeDuration.Width = 90;
        AddRow(grid, 1, "Close duration (ms)", _closeDuration);

        _openDuration.Minimum = 80;
        _openDuration.Maximum = 2000;
        _openDuration.Increment = 10;
        _openDuration.Width = 90;
        AddRow(grid, 2, "Open duration (ms)", _openDuration);

        ConfigureSlider(_blur, 0, 200);
        AddRow(grid, 3, "Blur intensity", _blur);

        ConfigureSlider(_edgeDarkness, 0, 100);
        AddRow(grid, 4, "Edge darkness", _edgeDarkness);

        _quality.DropDownStyle = ComboBoxStyle.DropDownList;
        _quality.Width = 160;
        _quality.Items.AddRange(new object[] { "Performance", "Balanced", "High" });
        AddRow(grid, 5, "Animation quality", _quality);

        group.Controls.Add(grid);
        return group;
    }

    private Control BuildMonitorGroup()
    {
        GroupBox group = new()
        {
            Text = "Monitor",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10),
        };

        TableLayoutPanel grid = NewGrid(3);

        _monitorMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _monitorMode.Width = 220;
        _monitorMode.Items.AddRange(new object[]
        {
            "Built-in display only (recommended)",
            "Primary display",
            "All displays",
            "A specific display",
        });
        _monitorMode.SelectedIndexChanged += (_, _) => UpdateManualDisplayState();
        AddRow(grid, 0, "Animate", _monitorMode);

        _manualDisplay.DropDownStyle = ComboBoxStyle.DropDownList;
        _manualDisplay.Width = 220;

        foreach (DisplayTarget display in _displays)
        {
            _manualDisplay.Items.Add(new DisplayChoice(display.Info));
        }

        AddRow(grid, 1, "Display", _manualDisplay);

        Label note = new()
        {
            AutoSize = true,
            MaximumSize = new Size(400, 0),
            ForeColor = SystemColors.GrayText,
            Text =
                "Only the built-in panel physically moves when the lid closes, so " +
                "animating an external monitor is off by default.",
        };

        grid.Controls.Add(note, 0, 2);
        grid.SetColumnSpan(note, 2);

        group.Controls.Add(grid);
        return group;
    }

    private Control BuildBehaviourGroup()
    {
        GroupBox group = new()
        {
            Text = "Behaviour",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10),
        };

        TableLayoutPanel grid = NewGrid(4);
        grid.ColumnStyles.Clear();
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        _startWithWindows.Text = "Start with Windows";
        _startWithWindows.AutoSize = true;
        grid.Controls.Add(_startWithWindows, 0, 0);

        _skipFullscreen.Text = "Skip while a full-screen app or game is running";
        _skipFullscreen.AutoSize = true;
        grid.Controls.Add(_skipFullscreen, 0, 1);

        _previewHotkeys.Text = "Preview hotkeys (Ctrl+Alt+Shift+C / O)";
        _previewHotkeys.AutoSize = true;
        grid.Controls.Add(_previewHotkeys, 0, 2);

        _showDebugHud.Text = "Show the developer overlay (FPS, state, capture latency)";
        _showDebugHud.AutoSize = true;
        grid.Controls.Add(_showDebugHud, 0, 3);

        group.Controls.Add(grid);
        return group;
    }

    private Control BuildButtons()
    {
        FlowLayoutPanel panel = new()
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0),
        };

        Button ok = new() { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(88, 28) };
        Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(88, 28) };
        Button preview = new() { Text = "Preview", AutoSize = true, MinimumSize = new Size(88, 28) };

        ok.Click += (_, _) => SaveToConfig();
        preview.Click += (_, _) =>
        {
            SaveToConfig();
            PreviewRequested?.Invoke(this, EventArgs.Empty);
        };

        panel.Controls.Add(cancel);
        panel.Controls.Add(ok);
        panel.Controls.Add(preview);

        AcceptButton = ok;
        CancelButton = cancel;

        return panel;
    }

    /// <summary>Raised when the user asks for a preview from inside the dialog.</summary>
    public event EventHandler? PreviewRequested;

    private static TableLayoutPanel NewGrid(int rows)
    {
        TableLayoutPanel grid = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = rows,
        };

        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        return grid;
    }

    private static void AddRow(TableLayoutPanel grid, int row, string label, Control control)
    {
        grid.Controls.Add(
            new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 0) },
            0,
            row);

        control.Anchor = AnchorStyles.Left;
        grid.Controls.Add(control, 1, row);
    }

    private static void ConfigureSlider(TrackBar slider, int minimum, int maximum)
    {
        slider.Minimum = minimum;
        slider.Maximum = maximum;
        slider.TickStyle = TickStyle.None;
        slider.Width = 220;
        slider.AutoSize = false;
        slider.Height = 28;
    }

    private void LoadFromConfig()
    {
        _enableAnimation.Checked = _working.Animation.EnableAnimation;
        _closeDuration.Value = Math.Clamp(_working.Animation.CloseDurationMs, 80, 2000);
        _openDuration.Value = Math.Clamp(_working.Animation.OpenDurationMs, 80, 2000);
        _blur.Value = Math.Clamp((int)Math.Round(_working.Animation.MaxBlur), _blur.Minimum, _blur.Maximum);
        _edgeDarkness.Value = Math.Clamp((int)Math.Round(_working.Animation.ShadowStrength * 100f), 0, 100);
        _quality.SelectedIndex = (int)_working.Animation.Quality;

        _monitorMode.SelectedIndex = _working.Monitor.Mode switch
        {
            MonitorSelectionMode.PrimaryDisplay => 1,
            MonitorSelectionMode.AllDisplays => 2,
            MonitorSelectionMode.Manual => 3,
            _ => 0,
        };

        for (int i = 0; i < _manualDisplay.Items.Count; i++)
        {
            if (_manualDisplay.Items[i] is DisplayChoice choice && choice.Matches(_working.Monitor.ManualDisplayId))
            {
                _manualDisplay.SelectedIndex = i;
                break;
            }
        }

        if (_manualDisplay.SelectedIndex < 0 && _manualDisplay.Items.Count > 0)
        {
            _manualDisplay.SelectedIndex = 0;
        }

        _startWithWindows.Checked = StartupRegistration.IsRegistered();
        _skipFullscreen.Checked = _working.Behavior.SkipWhenFullscreenAppActive;
        _previewHotkeys.Checked = _working.Behavior.EnablePreviewHotkeys;
        _showDebugHud.Checked = _working.Diagnostics.ShowDebugHud;

        UpdateManualDisplayState();
    }

    private void UpdateManualDisplayState() =>
        _manualDisplay.Enabled = _monitorMode.SelectedIndex == 3 && _manualDisplay.Items.Count > 0;

    private void SaveToConfig()
    {
        _working.Animation.EnableAnimation = _enableAnimation.Checked;
        _working.Animation.CloseDurationMs = (int)_closeDuration.Value;
        _working.Animation.OpenDurationMs = (int)_openDuration.Value;
        _working.Animation.MaxBlur = _blur.Value;
        _working.Animation.ShadowStrength = _edgeDarkness.Value / 100f;
        _working.Animation.Quality = (AnimationQuality)Math.Clamp(_quality.SelectedIndex, 0, 2);

        _working.Monitor.Mode = _monitorMode.SelectedIndex switch
        {
            1 => MonitorSelectionMode.PrimaryDisplay,
            2 => MonitorSelectionMode.AllDisplays,
            3 => MonitorSelectionMode.Manual,
            _ => MonitorSelectionMode.InternalPanel,
        };

        // "All displays" and "a specific external display" only make sense with the
        // external opt-in, so selecting them grants it rather than silently doing
        // nothing.
        _working.Monitor.AllowExternalDisplays =
            _working.Monitor.Mode is MonitorSelectionMode.AllDisplays or MonitorSelectionMode.Manual
            || _working.Monitor.Mode == MonitorSelectionMode.PrimaryDisplay;

        _working.Monitor.ManualDisplayId = _manualDisplay.SelectedItem is DisplayChoice choice ? choice.Id : null;

        _working.Behavior.SkipWhenFullscreenAppActive = _skipFullscreen.Checked;
        _working.Behavior.EnablePreviewHotkeys = _previewHotkeys.Checked;
        _working.Diagnostics.ShowDebugHud = _showDebugHud.Checked;
        _working.Behavior.StartWithWindows = _startWithWindows.Checked;

        _working.Normalize();
    }

    private sealed class DisplayChoice
    {
        private readonly DisplayInfo _info;

        public DisplayChoice(DisplayInfo info)
        {
            _info = info;
        }

        public string Id => string.IsNullOrEmpty(_info.DevicePath) ? _info.Id : _info.DevicePath;

        public bool Matches(string? id) =>
            !string.IsNullOrWhiteSpace(id)
            && (string.Equals(id, _info.Id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, _info.DevicePath, StringComparison.OrdinalIgnoreCase));

        public override string ToString()
        {
            string kind = _info.IsInternalPanel ? "built-in" : "external";
            return $"{_info.FriendlyName} - {_info.Bounds.Width}x{_info.Bounds.Height} ({kind})";
        }
    }
}
