using System;

namespace LidFlow.App.Interop;

/// <summary>
/// Power setting GUIDs from WinNT.h, with the values LidFlow cares about.
/// </summary>
internal static class PowerSettingGuids
{
    /// <summary>
    /// GUID_LIDSWITCH_STATE_CHANGE. Data is a DWORD: 0 = lid closed, 1 = lid opened.
    /// <para>
    /// Windows will not deliver this at all until a lid device has been found and
    /// its state is known, so a desktop machine simply never sees it - which is
    /// exactly the behaviour we want, rather than something to work around.
    /// </para>
    /// </summary>
    public static readonly Guid LidSwitchStateChange = new("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");

    /// <summary>
    /// GUID_SESSION_DISPLAY_STATUS. Data is a MONITOR_DISPLAY_STATE DWORD:
    /// 0 = off, 1 = on, 2 = dimmed.
    /// <para>
    /// This is the correct notification for an interactive user-mode app;
    /// GUID_CONSOLE_DISPLAY_STATE is the session-0 equivalent and
    /// GUID_MONITOR_POWER_ON is the pre-Windows 8 form. LidFlow uses it to know
    /// when there is no longer a lit panel to draw on, and stops rendering.
    /// </para>
    /// </summary>
    public static readonly Guid SessionDisplayStatus = new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");

    /// <summary>GUID_MONITOR_POWER_ON. Legacy fallback for the above on older builds.</summary>
    public static readonly Guid MonitorPowerOn = new("02731015-4510-4526-99E6-E5A17EBD1AEA");

    /// <summary>GUID_ACDC_POWER_SOURCE. Logged only; the effect does not depend on it.</summary>
    public static readonly Guid AcDcPowerSource = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
}
