namespace LidFlow.Core.Animation;

/// <summary>Which direction the panel is moving.</summary>
public enum TransitionKind
{
    /// <summary>Panel rotating away from the viewer: aperture collapses toward the hinge.</summary>
    Close = 0,

    /// <summary>Panel rotating toward the viewer: aperture expands from the hinge.</summary>
    Open = 1,
}
