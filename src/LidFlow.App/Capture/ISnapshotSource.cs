using System;
using LidFlow.App.Monitors;
using LidFlow.App.Rendering;

namespace LidFlow.App.Capture;

/// <summary>Why a capture attempt did not produce a frame.</summary>
internal enum CaptureFailure
{
    None = 0,

    /// <summary>The backend is not available on this machine or configuration.</summary>
    Unavailable,

    /// <summary>No frame arrived within the deadline.</summary>
    Timeout,

    /// <summary>The duplication was invalidated - mode change, session switch, or another app took over.</summary>
    AccessLost,

    /// <summary>The graphics device was removed or reset.</summary>
    DeviceLost,

    /// <summary>Anything else; the message carries the detail.</summary>
    Error,
}

/// <summary>Result of one capture attempt.</summary>
internal readonly struct CaptureResult
{
    private CaptureResult(bool success, CaptureFailure failure, string? message, double latencyMs)
    {
        Success = success;
        Failure = failure;
        Message = message;
        LatencyMs = latencyMs;
    }

    public bool Success { get; }

    public CaptureFailure Failure { get; }

    public string? Message { get; }

    /// <summary>Wall-clock time from request to frame in the snapshot texture.</summary>
    public double LatencyMs { get; }

    public static CaptureResult Ok(double latencyMs) => new(true, CaptureFailure.None, null, latencyMs);

    public static CaptureResult Failed(CaptureFailure failure, string? message = null) =>
        new(false, failure, message, 0d);
}

/// <summary>
/// A desktop capture backend.
/// <para>
/// Abstracted because the three available mechanisms have genuinely different
/// trade-offs and none of them is right in every situation - see
/// docs/RESEARCH.md. The pipeline tries them in order and uses the first that
/// delivers a frame.
/// </para>
/// </summary>
internal interface ISnapshotSource : IDisposable
{
    string Name { get; }

    /// <summary>Whether this backend can preserve an HDR panel's full range.</summary>
    bool SupportsHdr { get; }

    /// <summary>
    /// Whether this backend honours <c>WDA_EXCLUDEFROMCAPTURE</c>, and therefore
    /// whether the overlay can stay on screen while capturing behind it. If not,
    /// the caller must hide the overlay around the capture.
    /// </summary>
    bool HonoursCaptureExclusion { get; }

    /// <summary>Acquires one frame of <paramref name="display"/> into <paramref name="snapshot"/>.</summary>
    CaptureResult TryCapture(DisplayTarget display, DesktopSnapshot snapshot, int timeoutMs);

    /// <summary>
    /// Pre-creates whatever the backend needs so the first real capture is not the
    /// one that pays for initialization.
    /// </summary>
    void Warm(DisplayTarget display);

    /// <summary>Drops cached per-display state after a display or device change.</summary>
    void Invalidate();
}
