using System;
using LidFlow.Core.Monitors;

namespace LidFlow.Core.Configuration;

/// <summary>Which desktop capture backend to use for the frozen snapshot.</summary>
public enum CaptureBackend
{
    /// <summary>
    /// Try DXGI Desktop Duplication, then Windows.Graphics.Capture, then GDI. Recommended.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// DXGI Desktop Duplication. GPU-resident, per-output, and — unlike
    /// Windows.Graphics.Capture — draws no capture indicator on an unpackaged app.
    /// Always delivers BGRA8, so HDR content is not preserved.
    /// </summary>
    DesktopDuplication = 1,

    /// <summary>
    /// Windows.Graphics.Capture. Preserves HDR via FP16 surfaces, but on an unpackaged
    /// build Windows draws a yellow capture border that cannot be disabled. Opt-in.
    /// </summary>
    WindowsGraphicsCapture = 2,

    /// <summary>GDI BitBlt. Last-resort fallback; misses hardware-overlay and protected content.</summary>
    Gdi = 3,
}

/// <summary>Display targeting.</summary>
public sealed class MonitorConfig
{
    public MonitorSelectionMode Mode { get; set; } = MonitorSelectionMode.InternalPanel;

    /// <summary>Device path or <c>\\.\DISPLAYn</c> for <see cref="MonitorSelectionMode.Manual"/>.</summary>
    public string? ManualDisplayId { get; set; }

    /// <summary>
    /// Whether the animation may be drawn on a display that is not the built-in panel.
    /// Off by default: an external monitor is not physically moving, so animating it would
    /// be misleading.
    /// </summary>
    public bool AllowExternalDisplays { get; set; }

    public void Normalize()
    {
        if (!Enum.IsDefined(typeof(MonitorSelectionMode), Mode))
        {
            Mode = MonitorSelectionMode.InternalPanel;
        }

        if (string.IsNullOrWhiteSpace(ManualDisplayId))
        {
            ManualDisplayId = null;
        }
    }

    public MonitorConfig Clone() => new()
    {
        Mode = Mode,
        ManualDisplayId = ManualDisplayId,
        AllowExternalDisplays = AllowExternalDisplays,
    };
}

/// <summary>Runtime behaviour that is not part of the visual design.</summary>
public sealed class BehaviorConfig
{
    /// <summary>Register the app to start at sign-in (per-user Run key; no admin rights).</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// Skip the animation while a foreground application owns the whole screen — games and
    /// exclusive-fullscreen video. Drawing a topmost overlay over those can drop them out
    /// of fullscreen or stutter them, and the effect is not worth that.
    /// </summary>
    public bool SkipWhenFullscreenAppActive { get; set; } = true;

    /// <summary>Register Ctrl+Alt+Shift+C / O to preview the transitions.</summary>
    public bool EnablePreviewHotkeys { get; set; } = true;

    /// <summary>
    /// Keep a capture backend warm so the snapshot is ready with no acquisition latency at
    /// all. Costs a little idle memory and keeps a duplication object alive; off by default
    /// because on-demand acquisition is fast enough in practice.
    /// </summary>
    public bool WarmCapture { get; set; }

    /// <summary>How long to wait for a snapshot before giving up and skipping the animation.</summary>
    public int CaptureTimeoutMs { get; set; } = 45;

    public CaptureBackend CaptureBackend { get; set; } = CaptureBackend.Auto;

    /// <summary>Composite the mouse cursor into the snapshot when the driver reports it
    /// separately, so the pointer does not blink out of existence on the first frame.</summary>
    public bool IncludeCursorInSnapshot { get; set; } = true;

    public void Normalize()
    {
        CaptureTimeoutMs = AnimationConfig.Clamp(CaptureTimeoutMs, 5, 1000);

        if (!Enum.IsDefined(typeof(CaptureBackend), CaptureBackend))
        {
            CaptureBackend = CaptureBackend.Auto;
        }
    }

    public BehaviorConfig Clone() => new()
    {
        StartWithWindows = StartWithWindows,
        SkipWhenFullscreenAppActive = SkipWhenFullscreenAppActive,
        EnablePreviewHotkeys = EnablePreviewHotkeys,
        WarmCapture = WarmCapture,
        CaptureTimeoutMs = CaptureTimeoutMs,
        CaptureBackend = CaptureBackend,
        IncludeCursorInSnapshot = IncludeCursorInSnapshot,
    };
}

/// <summary>Development aids. All off in a production build's default config.</summary>
public sealed class DiagnosticsConfig
{
    /// <summary>Draw the on-screen HUD (FPS, capture latency, state, monitor, DPI, GPU).</summary>
    public bool ShowDebugHud { get; set; }

    /// <summary>Write a rolling log file under %LOCALAPPDATA%\LidFlow\Logs.</summary>
    public bool EnableFileLogging { get; set; } = true;

    /// <summary>Log every frame's parameters. Extremely verbose; for shader tuning only.</summary>
    public bool VerboseFrameLogging { get; set; }

    /// <summary>
    /// Hold the final frame of a preview on screen for this long so a still can be
    /// inspected. Ignored for real lid events.
    /// </summary>
    public int PreviewHoldMs { get; set; } = 350;

    public void Normalize() => PreviewHoldMs = AnimationConfig.Clamp(PreviewHoldMs, 0, 10_000);

    public DiagnosticsConfig Clone() => new()
    {
        ShowDebugHud = ShowDebugHud,
        EnableFileLogging = EnableFileLogging,
        VerboseFrameLogging = VerboseFrameLogging,
        PreviewHoldMs = PreviewHoldMs,
    };
}

/// <summary>Root configuration object, serialized to config.json.</summary>
public sealed class LidFlowConfig
{
    /// <summary>Schema version, so a future format change can migrate instead of failing.</summary>
    public int Version { get; set; } = 1;

    public AnimationConfig Animation { get; set; } = new();

    public MonitorConfig Monitor { get; set; } = new();

    public BehaviorConfig Behavior { get; set; } = new();

    public DiagnosticsConfig Diagnostics { get; set; } = new();

    public void Normalize()
    {
        if (Version <= 0)
        {
            Version = 1;
        }

        Animation ??= new AnimationConfig();
        Monitor ??= new MonitorConfig();
        Behavior ??= new BehaviorConfig();
        Diagnostics ??= new DiagnosticsConfig();

        Animation.Normalize();
        Monitor.Normalize();
        Behavior.Normalize();
        Diagnostics.Normalize();
    }

    public LidFlowConfig Clone() => new()
    {
        Version = Version,
        Animation = Animation.Clone(),
        Monitor = Monitor.Clone(),
        Behavior = Behavior.Clone(),
        Diagnostics = Diagnostics.Clone(),
    };
}
