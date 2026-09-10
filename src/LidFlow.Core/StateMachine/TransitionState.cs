namespace LidFlow.Core.StateMachine;

/// <summary>Explicit states of the lid transition. There is no implicit state anywhere else.</summary>
public enum TransitionState
{
    /// <summary>Lid open, nothing on screen, overlay hidden. Steady state.</summary>
    IdleOpen = 0,

    /// <summary>Lid just closed; acquiring the desktop snapshot. Overlay still hidden.</summary>
    CapturingForClose,

    /// <summary>Closing animation running. Overlay visible and opaque.</summary>
    CloseAnimation,

    /// <summary>Panel held closed: overlay visible, fully black.</summary>
    Closed,

    /// <summary>Lid just opened; acquiring a fresh snapshot <i>behind</i> the still-black
    /// overlay, so the reveal shows the current desktop rather than a stale pre-sleep frame.</summary>
    CapturingForOpen,

    /// <summary>Opening animation running.</summary>
    OpenAnimation,

    /// <summary>System is suspending. Rendering stopped; the overlay is left black so the
    /// panel does not flash the desktop if it lights up before we are scheduled again.</summary>
    Suspended,

    /// <summary>Animation disabled by configuration or unsupported on this machine.</summary>
    Disabled,
}

/// <summary>What the host should do as a result of a state change.</summary>
public enum TransitionAction
{
    None = 0,

    /// <summary>Acquire a snapshot for a closing transition.</summary>
    CaptureForClose,

    /// <summary>Acquire a snapshot for an opening transition (overlay stays black meanwhile).</summary>
    CaptureForOpen,

    /// <summary>Show the overlay and run the closing animation from
    /// <see cref="TransitionOutcome.StartProgress"/>.</summary>
    StartCloseAnimation,

    /// <summary>Show the overlay and run the opening animation from
    /// <see cref="TransitionOutcome.StartProgress"/>.</summary>
    StartOpenAnimation,

    /// <summary>Keep the overlay up, fully black, and stop the render loop.</summary>
    HoldBlack,

    /// <summary>Hide the overlay and release the snapshot. Back to steady state.</summary>
    HideOverlay,

    /// <summary>Abandon the transition without animating; let Windows behave normally.</summary>
    AbortToIdle,
}

/// <summary>Events that drive the state machine.</summary>
public enum TransitionTrigger
{
    /// <summary>GUID_LIDSWITCH_STATE_CHANGE reported 0, or a preview-close was requested.</summary>
    LidClosed = 0,

    /// <summary>GUID_LIDSWITCH_STATE_CHANGE reported 1, or a preview-open was requested.</summary>
    LidOpened,

    CaptureSucceeded,

    CaptureFailed,

    AnimationCompleted,

    /// <summary>PBT_APMSUSPEND: the machine is going to sleep.</summary>
    Suspending,

    /// <summary>PBT_APMRESUMEAUTOMATIC / PBT_APMRESUMESUSPEND.</summary>
    Resumed,

    /// <summary>Graphics device lost, display disconnected, or mode/DPI change. Bail out safely.</summary>
    Abort,

    /// <summary>Configuration disabled the animation.</summary>
    Disable,

    /// <summary>Configuration re-enabled the animation.</summary>
    Enable,
}
