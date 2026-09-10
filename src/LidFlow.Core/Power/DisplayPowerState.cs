namespace LidFlow.Core.Power;

/// <summary>
/// Display power state from GUID_SESSION_DISPLAY_STATUS (MONITOR_DISPLAY_STATE).
/// <para>
/// Tracked separately from lid state because they are genuinely different things and the
/// distinction is what makes the effect honest: a lid event says the hinge moved, this says
/// whether there is still a lit panel to draw on. Once the display is off, continuing to
/// render is pure waste, and the app stops.
/// </para>
/// </summary>
public enum DisplayPowerState
{
    Unknown = -1,

    /// <summary>PowerMonitorOff.</summary>
    Off = 0,

    /// <summary>PowerMonitorOn.</summary>
    On = 1,

    /// <summary>PowerMonitorDim.</summary>
    Dimmed = 2,
}

/// <summary>Parses MONITOR_DISPLAY_STATE payloads.</summary>
public static class DisplayPowerStateParser
{
    public static DisplayPowerState Parse(uint value) => value switch
    {
        0u => DisplayPowerState.Off,
        1u => DisplayPowerState.On,
        2u => DisplayPowerState.Dimmed,
        _ => DisplayPowerState.Unknown,
    };

    public static DisplayPowerState Parse(System.ReadOnlySpan<byte> data)
    {
        if (data.Length != 4)
        {
            return DisplayPowerState.Unknown;
        }

        uint value = (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
        return Parse(value);
    }
}
