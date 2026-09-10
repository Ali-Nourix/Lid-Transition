using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using LidFlow.App.Diagnostics;
using LidFlow.App.Lid;
using LidFlow.App.Monitors;
using LidFlow.App.Power;
using LidFlow.App.Settings;
using LidFlow.Core.Configuration;
using LidFlow.Core.Diagnostics;
using LidFlow.Core.Lid;

namespace LidFlow.App.Tray;

/// <summary>
/// The application's lifetime and its tray presence.
/// <para>
/// LidFlow has no main window: it sits in the notification area, listens for lid
/// events, and draws only during a transition. The tray icon exists so the app is
/// discoverable and quittable - a background process with no visible affordance
/// is worse behaviour, not better.
/// </para>
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly ILidFlowLog _log;
    private readonly PowerEventWindow _messageWindow;
    private readonly WindowsLidMonitor _lidMonitor;
    private readonly TransitionController _controller;
    private readonly NotifyIcon _trayIcon;
    private readonly Icon _icon;

    private LidFlowConfig _config;
    private bool _hasPendingPreview;
    private bool _pendingPreviewClose;
    private SettingsForm? _settingsForm;
    private DebugHudForm? _debugHud;
    private bool _disposed;

    public TrayApplicationContext(LidFlowConfig config, ILidFlowLog log, bool forceDebugHud)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? NullLog.Instance;

        if (forceDebugHud)
        {
            _config.Diagnostics.ShowDebugHud = true;
        }

        _messageWindow = new PowerEventWindow(_log);
        _controller = new TransitionController(_messageWindow, _config, _log);

        _lidMonitor = new WindowsLidMonitor(_messageWindow, _log);
        _lidMonitor.LidStateChanged += OnLidStateChanged;
        _lidMonitor.DisplayPowerChanged += (_, state) => _controller.OnDisplayPowerChanged(state);

        _icon = AppIcon.Create();
        _trayIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "LidFlow",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };

        _trayIcon.DoubleClick += (_, _) => ShowSettings();

        if (!_controller.Initialize())
        {
            _log.Warn("LidFlow started without a usable graphics pipeline; transitions will be skipped.");
            _trayIcon.Text = "LidFlow - no usable display";
        }

        _lidMonitor.Start();

        if (_config.Behavior.EnablePreviewHotkeys)
        {
            _messageWindow.RegisterPreviewHotkeys();
        }

        _messageWindow.PreviewClosePressed += (_, _) => _controller.PreviewClose();
        _messageWindow.PreviewOpenPressed += (_, _) => _controller.PreviewOpen();

        _messageWindow.StartupRequested += (_, _) =>
        {
            if (!_hasPendingPreview)
            {
                return;
            }

            _hasPendingPreview = false;

            if (_pendingPreviewClose)
            {
                _controller.PreviewClose();
            }
            else
            {
                _controller.PreviewOpen();
            }
        };

        ApplyDebugHud();

        _log.Info($"LidFlow ready. Lid state: {_lidMonitor.State}.");
    }

    /// <summary>
    /// Schedules a preview to run once the message loop is pumping, so it goes
    /// through exactly the same path a real lid event does. Used by the
    /// command-line switches.
    /// </summary>
    public void QueuePreview(bool close)
    {
        _pendingPreviewClose = close;
        _hasPendingPreview = true;
        _messageWindow.RequestStartup();
    }

    private ContextMenuStrip BuildMenu()
    {
        ContextMenuStrip menu = new();

        menu.Items.Add("Preview close", null, (_, _) => _controller.PreviewClose());
        menu.Items.Add("Preview open", null, (_, _) => _controller.PreviewOpen());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings...", null, (_, _) => ShowSettings());
        menu.Items.Add("Open log folder", null, (_, _) => OpenLogFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        return menu;
    }

    private void OnLidStateChanged(object? sender, LidStateChangedEventArgs e) =>
        _controller.OnLidStateChanged(e.Current);

    private void ShowSettings()
    {
        if (_settingsForm is not null && !_settingsForm.IsDisposed)
        {
            _settingsForm.Activate();
            return;
        }

        System.Collections.Generic.List<DisplayTarget> displays = DisplayEnumerator.Enumerate(_log);

        _settingsForm = new SettingsForm(_config, displays);
        _settingsForm.PreviewRequested += (_, _) =>
        {
            // Apply first, so the preview shows what the user just changed.
            ApplyConfiguration(_settingsForm!.Result, persist: false);
            _controller.PreviewClose();
        };

        try
        {
            if (_settingsForm.ShowDialog() == DialogResult.OK)
            {
                ApplyConfiguration(_settingsForm.Result, persist: true);
            }
            else
            {
                // Cancel must also undo anything a Preview applied.
                ApplyConfiguration(_config, persist: false);
            }
        }
        finally
        {
            _settingsForm.Dispose();
            _settingsForm = null;
        }
    }

    private void ApplyConfiguration(LidFlowConfig config, bool persist)
    {
        _config = config.Clone();
        _config.Normalize();

        _controller.ApplyConfiguration(_config);

        if (_config.Behavior.EnablePreviewHotkeys)
        {
            _messageWindow.RegisterPreviewHotkeys();
        }
        else
        {
            _messageWindow.UnregisterPreviewHotkeys();
        }

        StartupRegistration.TrySet(_config.Behavior.StartWithWindows, _log);
        ApplyDebugHud();

        if (persist)
        {
            if (ConfigStore.TrySave(AppPaths.ConfigFile, _config, out string? error))
            {
                _log.Info("Configuration saved.");
            }
            else
            {
                _log.Warn($"Configuration could not be saved: {error}");
            }
        }
    }

    /// <summary>
    /// Saves the inclinometer calibration learned this session.
    /// <para>
    /// Worth persisting because it is learned from real lid events: discarding it
    /// would mean the first close after every launch fell back to the timed path,
    /// even on a machine that had already taught it the pitch range.
    /// </para>
    /// </summary>
    private void PersistLearnedCalibration()
    {
        LidAngleCalibration? learned = _controller.LearnedCalibration;

        if (learned is null || !learned.IsValid)
        {
            return;
        }

        LidAngleCalibration stored = _config.Animation.InclinometerCalibration;

        // Only write when it actually changed, so shutting down does not rewrite
        // config.json every time.
        bool unchanged = stored.IsValid
            && Math.Abs(stored.OpenPitchDegrees - learned.OpenPitchDegrees) < 0.5d
            && Math.Abs(stored.ClosedPitchDegrees - learned.ClosedPitchDegrees) < 0.5d;

        if (unchanged)
        {
            return;
        }

        _config.Animation.InclinometerCalibration = learned;

        if (ConfigStore.TrySave(AppPaths.ConfigFile, _config, out string? error))
        {
            _log.Info($"Saved the learned lid calibration ({learned.ClosedPitchDegrees - learned.OpenPitchDegrees:0.#} deg sweep).");
        }
        else
        {
            _log.Warn($"Could not save the learned lid calibration: {error}");
        }
    }

    private void ApplyDebugHud()
    {
        bool wanted = _config.Diagnostics.ShowDebugHud;

        if (wanted && (_debugHud is null || _debugHud.IsDisposed))
        {
            _debugHud = new DebugHudForm(() => _controller.Diagnostics);
            _debugHud.Show();
        }
        else if (!wanted && _debugHud is not null)
        {
            _debugHud.Close();
            _debugHud.Dispose();
            _debugHud = null;
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            AppPaths.EnsureDataDirectory();
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.LogDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not open the log folder: {ex.Message}");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;

            _log.Info("LidFlow shutting down.");

            PersistLearnedCalibration();

            // Order matters: stop listening before tearing down what the events
            // would touch.
            _lidMonitor.LidStateChanged -= OnLidStateChanged;
            _lidMonitor.Dispose();

            _debugHud?.Dispose();
            _settingsForm?.Dispose();

            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _icon.Dispose();

            _controller.Dispose();
            _messageWindow.Dispose();
        }

        base.Dispose(disposing);
    }
}
