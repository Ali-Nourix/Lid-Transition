using System;

namespace LidFlow.Core.Animation;

/// <summary>
/// Easing primitives used by the lid transition.
/// <para>
/// Linear interpolation is deliberately available but never used as a default: a
/// physically-moving panel never has constant velocity. The defaults chosen in
/// <see cref="Configuration.AnimationConfig"/> are cubic Beziers with a fast
/// initial movement and a controlled, short deceleration.
/// </para>
/// </summary>
public static class Easing
{
    /// <summary>Clamps <paramref name="value"/> into the inclusive [0,1] range.</summary>
    public static float Clamp01(float value)
    {
        if (value < 0f)
        {
            return 0f;
        }

        return value > 1f ? 1f : value;
    }

    /// <summary>Linear. Provided for tests and for a "reduced motion" style debug mode only.</summary>
    public static float Linear(float t) => Clamp01(t);

    public static float EaseInCubic(float t)
    {
        t = Clamp01(t);
        return t * t * t;
    }

    public static float EaseOutCubic(float t)
    {
        t = Clamp01(t);
        float f = 1f - t;
        return 1f - (f * f * f);
    }

    public static float EaseInOutCubic(float t)
    {
        t = Clamp01(t);
        if (t < 0.5f)
        {
            return 4f * t * t * t;
        }

        float f = (-2f * t) + 2f;
        return 1f - (f * f * f / 2f);
    }

    public static float EaseOutQuart(float t)
    {
        t = Clamp01(t);
        float f = 1f - t;
        return 1f - (f * f * f * f);
    }

    public static float EaseOutQuint(float t)
    {
        t = Clamp01(t);
        float f = 1f - t;
        return 1f - (f * f * f * f * f);
    }

    public static float EaseInOutQuint(float t)
    {
        t = Clamp01(t);
        if (t < 0.5f)
        {
            return 16f * t * t * t * t * t;
        }

        float f = (-2f * t) + 2f;
        return 1f - (f * f * f * f * f / 2f);
    }

    public static float EaseOutExpo(float t)
    {
        t = Clamp01(t);
        return t >= 1f ? 1f : 1f - MathF.Pow(2f, -10f * t);
    }

    /// <summary>
    /// Critically-damped-ish spring settle, normalized so that f(0)=0 and f(1)=1.
    /// <paramref name="damping"/> below 1 introduces a small overshoot; the defaults
    /// keep it at or above 1 so the panel never visibly bounces.
    /// </summary>
    public static float Spring(float t, float damping = 1f, float frequency = 3.2f)
    {
        t = Clamp01(t);
        if (t <= 0f)
        {
            return 0f;
        }

        if (t >= 1f)
        {
            return 1f;
        }

        float omega = frequency * MathF.PI;
        float envelope = MathF.Exp(-damping * omega * t);

        if (damping >= 1f)
        {
            // Critically damped: no oscillation at all, just an exponential settle.
            return 1f - (envelope * (1f + (damping * omega * t)));
        }

        float damped = omega * MathF.Sqrt(1f - (damping * damping));
        return 1f - (envelope * (MathF.Cos(damped * t) + (damping * omega / damped * MathF.Sin(damped * t))));
    }
}
