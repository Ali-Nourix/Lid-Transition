using System;

namespace LidFlow.Core.StateMachine;

/// <summary>
/// The single authority on what the overlay is doing. Every lid event, capture result,
/// animation completion and power event goes through <see cref="Fire"/>, which is the only
/// thing that may change state.
/// <para>
/// The design goal is that no sequence of events — including physically impossible ones —
/// can leave the overlay stuck on screen or produce two concurrent animations. Unknown or
/// out-of-order triggers are ignored rather than guessed at, and every path that stops
/// animating is explicit about whether the overlay is left up (black) or taken down.
/// </para>
/// </summary>
public sealed class TransitionStateMachine
{
    private bool _overlayUp;

    public TransitionStateMachine(bool enabled = true)
    {
        Current = enabled ? TransitionState.IdleOpen : TransitionState.Disabled;
    }

    public TransitionState Current { get; private set; }

    /// <summary>Whether the overlay window should currently be on screen.</summary>
    public bool OverlayShouldBeVisible => _overlayUp;

    /// <summary>True while an animation is running and needs frames.</summary>
    public bool IsAnimating => Current is TransitionState.CloseAnimation or TransitionState.OpenAnimation;

    /// <summary>
    /// Feeds a trigger to the machine.
    /// </summary>
    /// <param name="trigger">What happened.</param>
    /// <param name="currentPanelProgress">
    /// The panel progress currently on screen (0 = open, 1 = closed). Only consulted when
    /// the trigger reverses an in-flight animation.
    /// </param>
    public TransitionOutcome Fire(TransitionTrigger trigger, float currentPanelProgress = 0f)
    {
        currentPanelProgress = Clamp01(currentPanelProgress);

        // Enable/disable is handled uniformly: it is valid from any state.
        switch (trigger)
        {
            case TransitionTrigger.Disable when Current != TransitionState.Disabled:
                return Apply(TransitionState.Disabled, TransitionAction.HideOverlay);

            case TransitionTrigger.Disable:
                return TransitionOutcome.Ignored(Current);

            case TransitionTrigger.Enable when Current == TransitionState.Disabled:
                return Apply(TransitionState.IdleOpen, TransitionAction.None);

            case TransitionTrigger.Enable:
                return TransitionOutcome.Ignored(Current);
        }

        if (Current == TransitionState.Disabled)
        {
            return TransitionOutcome.Ignored(Current);
        }

        return Current switch
        {
            TransitionState.IdleOpen => FromIdleOpen(trigger),
            TransitionState.CapturingForClose => FromCapturingForClose(trigger),
            TransitionState.CloseAnimation => FromCloseAnimation(trigger, currentPanelProgress),
            TransitionState.Closed => FromClosed(trigger),
            TransitionState.CapturingForOpen => FromCapturingForOpen(trigger),
            TransitionState.OpenAnimation => FromOpenAnimation(trigger, currentPanelProgress),
            TransitionState.Suspended => FromSuspended(trigger),
            _ => TransitionOutcome.Ignored(Current),
        };
    }

    private TransitionOutcome FromIdleOpen(TransitionTrigger trigger) => trigger switch
    {
        TransitionTrigger.LidClosed => Apply(TransitionState.CapturingForClose, TransitionAction.CaptureForClose),

        // Suspending with nothing on screen: there is nothing to protect, so stay down.
        TransitionTrigger.Suspending => Apply(TransitionState.Suspended, TransitionAction.HideOverlay),

        _ => TransitionOutcome.Ignored(Current),
    };

    private TransitionOutcome FromCapturingForClose(TransitionTrigger trigger) => trigger switch
    {
        TransitionTrigger.CaptureSucceeded =>
            Apply(TransitionState.CloseAnimation, TransitionAction.StartCloseAnimation, startPanelProgress: 0f),

        // No snapshot means no illusion to draw. Skipping is the correct fallback: the
        // user simply gets stock Windows behaviour instead of a broken effect.
        TransitionTrigger.CaptureFailed => Apply(TransitionState.IdleOpen, TransitionAction.AbortToIdle),

        // Lid re-opened before the first frame was drawn: nothing was shown, so nothing
        // needs to be un-shown.
        TransitionTrigger.LidOpened => Apply(TransitionState.IdleOpen, TransitionAction.AbortToIdle),

        TransitionTrigger.Abort => Apply(TransitionState.IdleOpen, TransitionAction.AbortToIdle),
        TransitionTrigger.Suspending => Apply(TransitionState.Suspended, TransitionAction.HideOverlay),

        _ => TransitionOutcome.Ignored(Current),
    };

    private TransitionOutcome FromCloseAnimation(TransitionTrigger trigger, float panelProgress) => trigger switch
    {
        TransitionTrigger.AnimationCompleted => Apply(TransitionState.Closed, TransitionAction.HoldBlack),

        // Rapid toggle: reverse from wherever the panel currently is.
        TransitionTrigger.LidOpened => Apply(
            TransitionState.OpenAnimation,
            TransitionAction.StartOpenAnimation,
            startPanelProgress: panelProgress,
            isReversal: true),

        TransitionTrigger.Abort => Apply(TransitionState.IdleOpen, TransitionAction.HideOverlay),

        // Mid-close sleep: leave the overlay black rather than snapping back to the desktop.
        TransitionTrigger.Suspending => Apply(TransitionState.Suspended, TransitionAction.HoldBlack),

        _ => TransitionOutcome.Ignored(Current),
    };

    private TransitionOutcome FromClosed(TransitionTrigger trigger) => trigger switch
    {
        TransitionTrigger.LidOpened => Apply(TransitionState.CapturingForOpen, TransitionAction.CaptureForOpen),
        TransitionTrigger.Abort => Apply(TransitionState.IdleOpen, TransitionAction.HideOverlay),
        TransitionTrigger.Suspending => Apply(TransitionState.Suspended, TransitionAction.HoldBlack),

        // A resume on its own does not mean the lid opened — modern standby wakes for
        // maintenance with the lid shut. Wait for the lid switch to say so.
        _ => TransitionOutcome.Ignored(Current),
    };

    private TransitionOutcome FromCapturingForOpen(TransitionTrigger trigger) => trigger switch
    {
        TransitionTrigger.CaptureSucceeded =>
            Apply(TransitionState.OpenAnimation, TransitionAction.StartOpenAnimation, startPanelProgress: 1f),

        // Without a snapshot there is nothing to reveal through; drop the overlay so the
        // user sees their desktop immediately rather than a black screen.
        TransitionTrigger.CaptureFailed => Apply(TransitionState.IdleOpen, TransitionAction.HideOverlay),

        TransitionTrigger.LidClosed => Apply(TransitionState.Closed, TransitionAction.HoldBlack),
        TransitionTrigger.Abort => Apply(TransitionState.IdleOpen, TransitionAction.HideOverlay),
        TransitionTrigger.Suspending => Apply(TransitionState.Suspended, TransitionAction.HoldBlack),

        _ => TransitionOutcome.Ignored(Current),
    };

    private TransitionOutcome FromOpenAnimation(TransitionTrigger trigger, float panelProgress) => trigger switch
    {
        TransitionTrigger.AnimationCompleted => Apply(TransitionState.IdleOpen, TransitionAction.HideOverlay),

        TransitionTrigger.LidClosed => Apply(
            TransitionState.CloseAnimation,
            TransitionAction.StartCloseAnimation,
            startPanelProgress: panelProgress,
            isReversal: true),

        TransitionTrigger.Abort => Apply(TransitionState.IdleOpen, TransitionAction.HideOverlay),
        TransitionTrigger.Suspending => Apply(TransitionState.Suspended, TransitionAction.HoldBlack),

        _ => TransitionOutcome.Ignored(Current),
    };

    private TransitionOutcome FromSuspended(TransitionTrigger trigger)
    {
        switch (trigger)
        {
            case TransitionTrigger.Resumed:
                return _overlayUp
                    ? Apply(TransitionState.Closed, TransitionAction.HoldBlack)
                    : Apply(TransitionState.IdleOpen, TransitionAction.HideOverlay);

            case TransitionTrigger.LidOpened:
                return _overlayUp
                    ? Apply(TransitionState.CapturingForOpen, TransitionAction.CaptureForOpen)
                    : Apply(TransitionState.IdleOpen, TransitionAction.HideOverlay);

            case TransitionTrigger.LidClosed:
                // Closed again while suspended. If we still own the screen, stay black.
                return _overlayUp
                    ? Apply(TransitionState.Closed, TransitionAction.HoldBlack)
                    : TransitionOutcome.Ignored(Current);

            case TransitionTrigger.Abort:
                return Apply(TransitionState.IdleOpen, TransitionAction.HideOverlay);

            default:
                return TransitionOutcome.Ignored(Current);
        }
    }

    private TransitionOutcome Apply(
        TransitionState to,
        TransitionAction action,
        float startPanelProgress = 0f,
        bool isReversal = false)
    {
        TransitionState from = Current;
        Current = to;

        _overlayUp = action switch
        {
            TransitionAction.StartCloseAnimation => true,
            TransitionAction.StartOpenAnimation => true,
            TransitionAction.HoldBlack => true,
            TransitionAction.HideOverlay => false,
            TransitionAction.AbortToIdle => false,

            // Capture actions do not change whether the overlay is up: CaptureForClose
            // runs with it down, CaptureForOpen runs with it up and black.
            _ => _overlayUp,
        };

        return new TransitionOutcome
        {
            Accepted = true,
            From = from,
            To = to,
            Action = action,
            StartPanelProgress = Clamp01(startPanelProgress),
            IsReversal = isReversal,
        };
    }

    private static float Clamp01(float v)
    {
        if (float.IsNaN(v))
        {
            return 0f;
        }

        return v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
