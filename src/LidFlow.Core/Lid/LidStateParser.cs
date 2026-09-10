namespace LidFlow.Core.Lid;

/// <summary>
/// Parses the payload of a GUID_LIDSWITCH_STATE_CHANGE power setting notification.
/// <para>
/// Windows documents exactly two values (0 = closed, 1 = opened) delivered as a DWORD in
/// the <c>Data</c> member of POWERBROADCAST_SETTING. Anything else is treated as unknown
/// rather than coerced, so a driver reporting something unexpected can never be mistaken
/// for a real lid event.
/// </para>
/// </summary>
public static class LidStateParser
{
    /// <summary>Size in bytes of the documented payload.</summary>
    public const int ExpectedDataLength = 4;

    public static LidState Parse(uint value) => value switch
    {
        0u => LidState.Closed,
        1u => LidState.Open,
        _ => LidState.Unknown,
    };

    /// <summary>
    /// Parses the raw <c>Data</c> bytes of a POWERBROADCAST_SETTING. The payload is a
    /// little-endian DWORD; a payload of any other length is rejected.
    /// </summary>
    public static LidState Parse(System.ReadOnlySpan<byte> data)
    {
        if (data.Length != ExpectedDataLength)
        {
            return LidState.Unknown;
        }

        uint value = (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
        return Parse(value);
    }
}
