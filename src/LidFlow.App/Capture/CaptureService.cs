using System;
using System.Collections.Generic;
using LidFlow.App.Monitors;
using LidFlow.App.Overlay;
using LidFlow.App.Rendering;
using LidFlow.Core.Configuration;
using LidFlow.Core.Diagnostics;

namespace LidFlow.App.Capture;

/// <summary>
/// Picks a capture backend and acquires the frozen frame.
/// <para>
/// The backends are tried in order and the first that returns a frame wins, with
/// the winner remembered so subsequent transitions go straight to it. The order
/// is not arbitrary:
/// </para>
/// <list type="number">
/// <item>Desktop Duplication - GPU-resident, no capture indicator, but only
/// yields a frame when the desktop actually changes.</item>
/// <item>Windows.Graphics.Capture - only when HDR fidelity is wanted, because it
/// shows a capture border on an unpackaged app.</item>
/// <item>GDI - slower, but always returns the current desktop, which is what
/// covers the completely-static-screen case the first backend can miss.</item>
/// </list>
/// </summary>
internal sealed class CaptureService : IDisposable
{
    private readonly List<ISnapshotSource> _sources = new(3);
    private readonly ILidFlowLog _log;
    private ISnapshotSource? _preferred;
    private bool _disposed;

    public CaptureService(GraphicsDevice graphics, LidFlowConfig config, ILidFlowLog log)
    {
        _log = log ?? NullLog.Instance;

        CaptureBackend requested = config.Behavior.CaptureBackend;

        switch (requested)
        {
            case CaptureBackend.DesktopDuplication:
                _sources.Add(new DuplicationSnapshotSource(graphics, _log));
                break;

            case CaptureBackend.WindowsGraphicsCapture:
                _sources.Add(new WgcSnapshotSource(graphics, _log));
                break;

            case CaptureBackend.Gdi:
                _sources.Add(new GdiSnapshotSource(_log));
                break;

            case CaptureBackend.Auto:
            default:
                _sources.Add(new DuplicationSnapshotSource(graphics, _log));

                if (WgcSnapshotSource.IsSupported)
                {
                    _sources.Add(new WgcSnapshotSource(graphics, _log));
                }

                break;
        }

        // GDI is always appended as the final fallback, whatever was requested,
        // because it is the only backend that cannot fail on a static desktop.
        if (requested != CaptureBackend.Gdi)
        {
            _sources.Add(new GdiSnapshotSource(_log));
        }

        _log.Info($"Capture backends: {string.Join(" -> ", _sources.ConvertAll(s => s.Name))}.");
    }

    /// <summary>Name of the backend that last produced a frame, for the debug HUD.</summary>
    public string LastBackend { get; private set; } = "-";

    /// <summary>Latency of the last successful capture, in milliseconds.</summary>
    public double LastLatencyMs { get; private set; }

    /// <summary>
    /// Acquires a snapshot.
    /// </summary>
    /// <param name="overlayIsUp">
    /// True when the overlay is currently on screen and black - which is the case
    /// for the opening transition. A backend that does not honour
    /// <c>WDA_EXCLUDEFROMCAPTURE</c> would then capture our own black frame, so
    /// such backends are skipped rather than allowed to produce a useless
    /// snapshot.
    /// </param>
    public bool TryCapture(
        DisplayTarget display,
        DesktopSnapshot snapshot,
        OverlayWindow overlay,
        bool overlayIsUp,
        int timeoutMs)
    {
        // If the platform could not give us capture exclusion at all, no backend
        // can read the desktop from behind the overlay.
        bool requireExclusion = overlayIsUp && overlay.ExcludedFromCapture;
        bool overlayBlocksCapture = overlayIsUp && !overlay.ExcludedFromCapture;

        if (overlayBlocksCapture)
        {
            _log.Warn("Overlay cannot be excluded from capture on this build; the reveal will use the previous snapshot.");
            return false;
        }

        // Try the backend that worked last time first.
        if (_preferred is not null && TryOne(_preferred, display, snapshot, timeoutMs, requireExclusion))
        {
            return true;
        }

        foreach (ISnapshotSource source in _sources)
        {
            if (ReferenceEquals(source, _preferred))
            {
                continue;
            }

            if (TryOne(source, display, snapshot, timeoutMs, requireExclusion))
            {
                _preferred = source;
                return true;
            }
        }

        _log.Warn("Every capture backend failed; the transition will be skipped.");
        return false;
    }

    private bool TryOne(
        ISnapshotSource source,
        DisplayTarget display,
        DesktopSnapshot snapshot,
        int timeoutMs,
        bool requireExclusion)
    {
        if (requireExclusion && !source.HonoursCaptureExclusion)
        {
            return false;
        }

        CaptureResult result = source.TryCapture(display, snapshot, timeoutMs);

        if (result.Success)
        {
            LastBackend = source.Name;
            LastLatencyMs = result.LatencyMs;
            return true;
        }

        _log.Debug($"{source.Name} capture failed: {result.Failure} {result.Message}");
        return false;
    }

    /// <summary>Pre-initializes the preferred backend so the first capture is not the slow one.</summary>
    public void Warm(DisplayTarget display)
    {
        foreach (ISnapshotSource source in _sources)
        {
            source.Warm(display);
        }
    }

    /// <summary>Drops per-display state after a display topology or mode change.</summary>
    public void Invalidate()
    {
        foreach (ISnapshotSource source in _sources)
        {
            source.Invalidate();
        }

        _preferred = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (ISnapshotSource source in _sources)
        {
            source.Dispose();
        }

        _sources.Clear();
    }
}
