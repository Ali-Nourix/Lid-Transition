using System;
using LidFlow.App.Interop;
using LidFlow.App.Power;
using LidFlow.Core.Diagnostics;
using LidFlow.Core.Lid;
using LidFlow.Core.Power;

namespace LidFlow.App.Lid;

/// <summary>
/// Real lid monitor, driven by <c>GUID_LIDSWITCH_STATE_CHANGE</c> power setting
/// notifications.
/// <para>
/// There is no polling anywhere: the OS pushes a message when the hardware lid
/// switch changes, which is both the lowest-latency route available to user mode
/// and the only one that costs nothing while idle. No WMI queries, no timers, no
/// input hooks, no watching the display state as a proxy.
/// </para>
/// </summary>
internal sealed class WindowsLidMonitor : LidMonitorBase
{
    private readonly PowerEventWindow _window;
    private readonly ILidFlowLog _log;
    private bool _subscribed;

    public WindowsLidMonitor(PowerEventWindow window, ILidFlowLog log)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _log = log ?? NullLog.Instance;
    }

    /// <summary>
    /// Last reported display power state. Distinct from lid state: the lid says
    /// the hinge moved, this says whether a lit panel still exists.
    /// </summary>
    public DisplayPowerState DisplayPower { get; private set; } = DisplayPowerState.Unknown;

    /// <summary>Raised when the display's power state changes.</summary>
    public event EventHandler<DisplayPowerState>? DisplayPowerChanged;

    protected override void OnStart()
    {
        if (_subscribed)
        {
            return;
        }

        _window.PowerSettingChanged += OnPowerSettingChanged;

        bool lid = _window.RegisterPowerSetting(PowerSettingGuids.LidSwitchStateChange);
        bool display = _window.RegisterPowerSetting(PowerSettingGuids.SessionDisplayStatus);

        if (!display)
        {
            // Pre-Windows 8 form. Kept as a fallback because it costs one call and
            // losing display-state entirely would mean rendering into a dark panel.
            _window.RegisterPowerSetting(PowerSettingGuids.MonitorPowerOn);
        }

        _subscribed = true;

        _log.Info(lid
            ? "Subscribed to GUID_LIDSWITCH_STATE_CHANGE."
            : "Could not subscribe to GUID_LIDSWITCH_STATE_CHANGE; lid events will not arrive.");
    }

    protected override void OnStop()
    {
        if (!_subscribed)
        {
            return;
        }

        _window.PowerSettingChanged -= OnPowerSettingChanged;
        _subscribed = false;
    }

    private void OnPowerSettingChanged(object? sender, PowerSettingEventArgs e)
    {
        if (e.Setting == PowerSettingGuids.LidSwitchStateChange)
        {
            LidState state = LidStateParser.Parse(e.Data);

            if (state == LidState.Unknown)
            {
                _log.Warn($"Lid notification carried an unexpected {e.Data.Length}-byte payload; ignoring.");
                return;
            }

            _log.Info($"Lid switch: {state}.");
            ReportState(state);
            return;
        }

        if (e.Setting == PowerSettingGuids.SessionDisplayStatus || e.Setting == PowerSettingGuids.MonitorPowerOn)
        {
            DisplayPowerState display = e.Setting == PowerSettingGuids.MonitorPowerOn
                ? (DisplayPowerStateParser.Parse(e.Data) == DisplayPowerState.Off ? DisplayPowerState.Off : DisplayPowerState.On)
                : DisplayPowerStateParser.Parse(e.Data);

            if (display == DisplayPowerState.Unknown || display == DisplayPower)
            {
                return;
            }

            DisplayPower = display;
            _log.Info($"Display power: {display}.");
            DisplayPowerChanged?.Invoke(this, display);
        }
    }
}
