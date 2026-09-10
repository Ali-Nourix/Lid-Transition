namespace LidFlow.Core.StateMachine;

/// <summary>Result of feeding a trigger to <see cref="TransitionStateMachine"/>.</summary>
public readonly struct TransitionOutcome
{
    /// <summary>False when the trigger was not meaningful in the current state and was
    /// ignored. Ignoring is always safe: it never leaves a partially-applied transition.</summary>
    public bool Accepted { get; init; }

    public TransitionState From { get; init; }

    public TransitionState To { get; init; }

    public TransitionAction Action { get; init; }

    /// <summary>
    /// Panel-space progress the new animation must start from (0 = fully open, 1 = fully
    /// closed). For a normal transition this is the natural endpoint. When an in-flight
    /// animation is reversed it is the panel position currently on screen, so the new
    /// animation picks up exactly where the old one was instead of jumping.
    /// </summary>
    public float StartPanelProgress { get; init; }

    /// <summary>True when this outcome reversed an in-flight animation.</summary>
    public bool IsReversal { get; init; }

    public static TransitionOutcome Ignored(TransitionState state) => new()
    {
        Accepted = false,
        From = state,
        To = state,
        Action = TransitionAction.None,
        StartPanelProgress = 0f,
        IsReversal = false,
    };
}
