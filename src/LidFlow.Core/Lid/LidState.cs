namespace LidFlow.Core.Lid;

/// <summary>Physical lid position as reported by the platform.</summary>
public enum LidState
{
    /// <summary>No lid device has been found yet, or its state is not yet known.
    /// <para>
    /// This is a real state, not a placeholder: Windows explicitly documents that the
    /// GUID_LIDSWITCH_STATE_CHANGE callback "won't be called until a lid device is found
    /// and its current state is known", so a desktop PC — or a laptop very early in boot —
    /// legitimately sits here forever.
    /// </para></summary>
    Unknown = -1,

    /// <summary>GUID_LIDSWITCH_STATE_CHANGE data == 0.</summary>
    Closed = 0,

    /// <summary>GUID_LIDSWITCH_STATE_CHANGE data == 1.</summary>
    Open = 1,
}
