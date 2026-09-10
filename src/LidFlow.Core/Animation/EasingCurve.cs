using System;

namespace LidFlow.Core.Animation;

/// <summary>Named easing presets selectable from configuration.</summary>
public enum EasingPreset
{
    /// <summary>Use the explicit cubic Bezier control points from configuration.</summary>
    CustomBezier = 0,
    Linear,
    EaseOutCubic,
    EaseInOutCubic,
    EaseOutQuart,
    EaseOutQuint,
    EaseInOutQuint,
    EaseOutExpo,
    Spring,
    LidClose,
    LidOpen,
}

/// <summary>
/// A resolved easing function: a preset plus the parameters the preset needs.
/// Kept as a struct so the render loop can evaluate it without allocating.
/// </summary>
public readonly struct EasingCurve
{
    public EasingCurve(EasingPreset preset, CubicBezierEasing bezier, float springDamping = 1f, float springFrequency = 3.2f)
    {
        Preset = preset;
        Bezier = bezier;
        SpringDamping = springDamping <= 0f ? 1f : springDamping;
        SpringFrequency = springFrequency <= 0f ? 3.2f : springFrequency;
    }

    public EasingPreset Preset { get; }

    public CubicBezierEasing Bezier { get; }

    public float SpringDamping { get; }

    public float SpringFrequency { get; }

    public static EasingCurve DefaultClose => new(EasingPreset.LidClose, CubicBezierEasing.LidClose);

    public static EasingCurve DefaultOpen => new(EasingPreset.LidOpen, CubicBezierEasing.LidOpen);

    public static EasingCurve FromBezier(float x1, float y1, float x2, float y2) =>
        new(EasingPreset.CustomBezier, new CubicBezierEasing(x1, y1, x2, y2));

    public float Evaluate(float t) => Preset switch
    {
        EasingPreset.Linear => Easing.Linear(t),
        EasingPreset.EaseOutCubic => Easing.EaseOutCubic(t),
        EasingPreset.EaseInOutCubic => Easing.EaseInOutCubic(t),
        EasingPreset.EaseOutQuart => Easing.EaseOutQuart(t),
        EasingPreset.EaseOutQuint => Easing.EaseOutQuint(t),
        EasingPreset.EaseInOutQuint => Easing.EaseInOutQuint(t),
        EasingPreset.EaseOutExpo => Easing.EaseOutExpo(t),
        EasingPreset.Spring => Easing.Spring(t, SpringDamping, SpringFrequency),
        EasingPreset.LidClose => CubicBezierEasing.LidClose.Evaluate(t),
        EasingPreset.LidOpen => CubicBezierEasing.LidOpen.Evaluate(t),
        EasingPreset.CustomBezier => Bezier.Evaluate(t),
        _ => Easing.EaseOutCubic(t),
    };

    /// <summary>
    /// Normalized rate of change of the eased value at <paramref name="t"/>, obtained by
    /// central difference. Used to drive motion blur from the panel edge's instantaneous
    /// speed rather than from progress directly — blur then peaks while the edge is
    /// actually moving fast and decays as it settles, which is what makes it read as
    /// optical motion instead of a fade.
    /// </summary>
    public float EvaluateVelocity(float t)
    {
        const float H = 1f / 512f;

        float a = Easing.Clamp01(t - H);
        float b = Easing.Clamp01(t + H);
        float span = b - a;

        if (span <= 0f)
        {
            return 0f;
        }

        float velocity = (Evaluate(b) - Evaluate(a)) / span;
        return velocity < 0f ? 0f : velocity;
    }
}
