using System;

namespace LidFlow.Core.Monitors;

/// <summary>Virtual-screen rectangle in physical pixels.</summary>
public readonly struct DisplayBounds : IEquatable<DisplayBounds>
{
    public DisplayBounds(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width < 0 ? 0 : width;
        Height = height < 0 ? 0 : height;
    }

    public int X { get; }

    public int Y { get; }

    public int Width { get; }

    public int Height { get; }

    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public float Aspect => Height <= 0 ? 1f : Width / (float)Height;

    public bool Equals(DisplayBounds other) =>
        X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;

    public override bool Equals(object? obj) => obj is DisplayBounds other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);

    public override string ToString() =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}x{1}+{2}+{3}", Width, Height, X, Y);

    public static bool operator ==(DisplayBounds left, DisplayBounds right) => left.Equals(right);

    public static bool operator !=(DisplayBounds left, DisplayBounds right) => !left.Equals(right);
}

/// <summary>
/// How confident we are that a display is the machine's built-in panel.
/// <para>
/// This matters because the animation should only ever run on the panel that physically
/// moves. Windows exposes connector technology through QueryDisplayConfig, which is
/// authoritative when present, but a docked or virtualized setup can leave it ambiguous —
/// so the confidence is carried explicitly instead of being flattened into a boolean guess.
/// </para>
/// </summary>
public enum InternalPanelConfidence
{
    /// <summary>Connector technology is external (HDMI, DisplayPort, wireless, ...).</summary>
    No = 0,

    /// <summary>No connector information available.</summary>
    Unknown = 1,

    /// <summary>Connector reports an embedded panel (embedded DisplayPort / embedded UDI).</summary>
    Likely = 2,

    /// <summary>Connector reports DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL or LVDS.</summary>
    Certain = 3,
}

/// <summary>A display, as far as the animation needs to know about one.</summary>
public sealed class DisplayInfo
{
    /// <summary>Stable-ish GDI device name, e.g. <c>\\.\DISPLAY1</c>. Used for manual selection.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>EDID friendly name when available, otherwise the device name.</summary>
    public string FriendlyName { get; init; } = string.Empty;

    /// <summary>Device interface path; more stable than <see cref="Id"/> across reboots.</summary>
    public string DevicePath { get; init; } = string.Empty;

    public DisplayBounds Bounds { get; init; }

    public bool IsPrimary { get; init; }

    public InternalPanelConfidence InternalConfidence { get; init; } = InternalPanelConfidence.Unknown;

    /// <summary>Effective DPI scale, 1.0 at 100%.</summary>
    public float DpiScale { get; init; } = 1f;

    /// <summary>Reported refresh rate in Hz, 0 when unknown.</summary>
    public int RefreshHz { get; init; }

    /// <summary>True when the output is currently in an HDR colour space.</summary>
    public bool IsHdr { get; init; }

    /// <summary>Adapter LUID, for matching a display to the DXGI output that drives it.</summary>
    public long AdapterLuid { get; init; }

    /// <summary>DXGI output index within its adapter.</summary>
    public int OutputIndex { get; init; }

    /// <summary>Convenience: treat Likely and Certain as internal.</summary>
    public bool IsInternalPanel => InternalConfidence >= InternalPanelConfidence.Likely;

    public override string ToString() =>
        string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "{0} ({1}) {2} @{3}Hz dpi={4:0.##}{5}{6}{7}",
            FriendlyName,
            Id,
            Bounds,
            RefreshHz,
            DpiScale,
            IsPrimary ? " primary" : string.Empty,
            IsInternalPanel ? " internal" : string.Empty,
            IsHdr ? " hdr" : string.Empty);
}
