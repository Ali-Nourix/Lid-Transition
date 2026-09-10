using System;
using System.Collections.Generic;

namespace LidFlow.Core.Monitors;

/// <summary>Which display(s) the transition should be drawn on.</summary>
public enum MonitorSelectionMode
{
    /// <summary>Default and safest: only the built-in laptop panel — the one that physically moves.</summary>
    InternalPanel = 0,

    /// <summary>Whichever display Windows currently calls primary.</summary>
    PrimaryDisplay = 1,

    /// <summary>Every active display. Opt-in only.</summary>
    AllDisplays = 2,

    /// <summary>A specific display chosen in settings.</summary>
    Manual = 3,
}

/// <summary>Why the selector ended up with the displays it returned.</summary>
public enum MonitorSelectionReason
{
    /// <summary>The requested mode was satisfied exactly.</summary>
    Exact = 0,

    /// <summary>Internal panel was requested but could not be identified; fell back.</summary>
    InternalPanelNotIdentified,

    /// <summary>The manually-selected display is no longer present; fell back.</summary>
    ManualTargetMissing,

    /// <summary>External displays are not permitted by configuration, so they were dropped.</summary>
    ExternalDisplaysNotAllowed,

    /// <summary>Nothing suitable was found. No animation should run.</summary>
    NoTarget,
}

/// <summary>Outcome of <see cref="MonitorSelector.Select"/>.</summary>
public readonly struct MonitorSelectionResult
{
    public MonitorSelectionResult(IReadOnlyList<DisplayInfo> targets, MonitorSelectionReason reason)
    {
        Targets = targets ?? Array.Empty<DisplayInfo>();
        Reason = reason;
    }

    public IReadOnlyList<DisplayInfo> Targets { get; }

    public MonitorSelectionReason Reason { get; }

    public bool HasTarget => Targets.Count > 0;

    public DisplayInfo? Primary => Targets.Count > 0 ? Targets[0] : null;
}
