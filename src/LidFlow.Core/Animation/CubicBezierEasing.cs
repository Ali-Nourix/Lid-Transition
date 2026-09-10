using System;

namespace LidFlow.Core.Animation;

/// <summary>
/// A CSS-style cubic Bezier timing function with fixed endpoints (0,0) and (1,1).
/// <para>
/// Solving x(t) = target for t has no closed form, so this uses Newton-Raphson with a
/// bisection fallback — the same approach browsers use. It is allocation-free and
/// deterministic, which matters because it is evaluated on the render thread.
/// </para>
/// </summary>
public readonly struct CubicBezierEasing : IEquatable<CubicBezierEasing>
{
    private const int NewtonIterations = 8;
    private const float NewtonMinSlope = 1e-4f;
    private const float SubdivisionPrecision = 1e-7f;
    private const int SubdivisionMaxIterations = 32;

    public CubicBezierEasing(float x1, float y1, float x2, float y2)
    {
        // The X control points must stay inside [0,1] or the curve is not a valid
        // timing function (time would run backwards). Y is free, which is what
        // allows overshoot curves.
        X1 = Easing.Clamp01(x1);
        Y1 = y1;
        X2 = Easing.Clamp01(x2);
        Y2 = y2;
    }

    public float X1 { get; }

    public float Y1 { get; }

    public float X2 { get; }

    public float Y2 { get; }

    /// <summary>Fast start, long controlled deceleration. Default for the closing animation.</summary>
    public static CubicBezierEasing LidClose => new(0.24f, 0.92f, 0.20f, 1.0f);

    /// <summary>Slightly more energetic than <see cref="LidClose"/>. Default for the opening animation.</summary>
    public static CubicBezierEasing LidOpen => new(0.14f, 0.98f, 0.24f, 1.0f);

    /// <summary>Symmetric ease used by secondary channels such as the side/keystone closure.</summary>
    public static CubicBezierEasing Smooth => new(0.42f, 0.0f, 0.58f, 1.0f);

    public float Evaluate(float x)
    {
        x = Easing.Clamp01(x);

        // Identity curve: skip the solve entirely.
        if (X1 == Y1 && X2 == Y2)
        {
            return x;
        }

        if (x <= 0f)
        {
            return 0f;
        }

        if (x >= 1f)
        {
            return 1f;
        }

        return SampleY(SolveT(x));
    }

    private float SolveT(float x)
    {
        float t = x;

        for (int i = 0; i < NewtonIterations; i++)
        {
            float slope = SampleDerivativeX(t);
            if (MathF.Abs(slope) < NewtonMinSlope)
            {
                break;
            }

            float error = SampleX(t) - x;
            if (MathF.Abs(error) < SubdivisionPrecision)
            {
                return t;
            }

            t -= error / slope;
        }

        // Newton can leave the unit interval on very flat curves; bisect instead.
        float low = 0f;
        float high = 1f;
        t = x;

        for (int i = 0; i < SubdivisionMaxIterations; i++)
        {
            float current = SampleX(t);
            float error = current - x;

            if (MathF.Abs(error) < SubdivisionPrecision)
            {
                break;
            }

            if (error > 0f)
            {
                high = t;
            }
            else
            {
                low = t;
            }

            t = ((high - low) * 0.5f) + low;
        }

        return t;
    }

    private float SampleX(float t) => Bezier(t, X1, X2);

    private float SampleY(float t) => Bezier(t, Y1, Y2);

    private float SampleDerivativeX(float t) => BezierDerivative(t, X1, X2);

    // B(t) for a cubic with p0 = 0 and p3 = 1, expanded into Horner form.
    private static float Bezier(float t, float p1, float p2)
    {
        float a = 1f - (3f * p2) + (3f * p1);
        float b = (3f * p2) - (6f * p1);
        float c = 3f * p1;
        return ((((a * t) + b) * t) + c) * t;
    }

    private static float BezierDerivative(float t, float p1, float p2)
    {
        float a = 1f - (3f * p2) + (3f * p1);
        float b = (3f * p2) - (6f * p1);
        float c = 3f * p1;
        return (3f * a * t * t) + (2f * b * t) + c;
    }

    public bool Equals(CubicBezierEasing other) =>
        X1.Equals(other.X1) && Y1.Equals(other.Y1) && X2.Equals(other.X2) && Y2.Equals(other.Y2);

    public override bool Equals(object? obj) => obj is CubicBezierEasing other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X1, Y1, X2, Y2);

    public override string ToString() =>
        string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "cubic-bezier({0:0.###}, {1:0.###}, {2:0.###}, {3:0.###})",
            X1,
            Y1,
            X2,
            Y2);

    public static bool operator ==(CubicBezierEasing left, CubicBezierEasing right) => left.Equals(right);

    public static bool operator !=(CubicBezierEasing left, CubicBezierEasing right) => !left.Equals(right);
}
