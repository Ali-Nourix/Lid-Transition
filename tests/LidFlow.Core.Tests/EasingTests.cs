using System;
using LidFlow.Core.Animation;
using Xunit;

namespace LidFlow.Core.Tests;

public sealed class EasingTests
{
    [Theory]
    [InlineData(EasingPreset.Linear)]
    [InlineData(EasingPreset.EaseOutCubic)]
    [InlineData(EasingPreset.EaseInOutCubic)]
    [InlineData(EasingPreset.EaseOutQuart)]
    [InlineData(EasingPreset.EaseOutQuint)]
    [InlineData(EasingPreset.EaseInOutQuint)]
    [InlineData(EasingPreset.EaseOutExpo)]
    [InlineData(EasingPreset.Spring)]
    [InlineData(EasingPreset.LidClose)]
    [InlineData(EasingPreset.LidOpen)]
    public void EveryPresetPinsBothEndpoints(EasingPreset preset)
    {
        EasingCurve curve = new(preset, CubicBezierEasing.LidClose);

        Assert.InRange(curve.Evaluate(0f), -1e-4f, 1e-4f);
        Assert.InRange(curve.Evaluate(1f), 1f - 1e-4f, 1f + 1e-4f);
    }

    [Theory]
    [InlineData(EasingPreset.EaseOutCubic)]
    [InlineData(EasingPreset.EaseOutQuart)]
    [InlineData(EasingPreset.EaseOutQuint)]
    [InlineData(EasingPreset.LidClose)]
    [InlineData(EasingPreset.LidOpen)]
    public void EaseOutPresetsAreMonotonic(EasingPreset preset)
    {
        EasingCurve curve = new(preset, CubicBezierEasing.LidClose);
        float previous = -1f;

        for (int i = 0; i <= 200; i++)
        {
            float value = curve.Evaluate(i / 200f);
            Assert.True(value >= previous - 1e-4f, $"Curve went backwards at t={i / 200f}: {value} < {previous}");
            previous = value;
        }
    }

    [Fact]
    public void InputIsClampedRatherThanExtrapolated()
    {
        EasingCurve curve = EasingCurve.DefaultClose;

        Assert.Equal(0f, curve.Evaluate(-5f), 4);
        Assert.Equal(1f, curve.Evaluate(5f), 4);
    }

    [Fact]
    public void LidCurvesFrontLoadTheirMotion()
    {
        // "Fast initial movement, controlled deceleration": more than half the distance
        // must be covered in the first third of the time.
        Assert.True(CubicBezierEasing.LidClose.Evaluate(1f / 3f) > 0.5f);
        Assert.True(CubicBezierEasing.LidOpen.Evaluate(1f / 3f) > 0.5f);
    }

    [Fact]
    public void OpenCurveIsMoreEnergeticThanCloseCurve()
    {
        // Opening should feel snappier: at every early sample it is further along.
        for (int i = 1; i <= 10; i++)
        {
            float t = i / 40f;
            Assert.True(
                CubicBezierEasing.LidOpen.Evaluate(t) >= CubicBezierEasing.LidClose.Evaluate(t),
                $"Open curve trailed close curve at t={t}");
        }
    }

    [Fact]
    public void IdentityBezierIsExactlyLinear()
    {
        CubicBezierEasing identity = new(0f, 0f, 1f, 1f);

        for (int i = 0; i <= 20; i++)
        {
            float t = i / 20f;
            Assert.Equal(t, identity.Evaluate(t), 5);
        }
    }

    [Fact]
    public void BezierSolverIsAccurateForAVeryFlatCurve()
    {
        // A near-vertical then near-flat curve is the pathological case for Newton-Raphson;
        // the bisection fallback has to catch it.
        CubicBezierEasing steep = new(0.001f, 0.999f, 0.999f, 1f);

        for (int i = 0; i <= 50; i++)
        {
            float t = i / 50f;
            float value = steep.Evaluate(t);
            Assert.False(float.IsNaN(value));
            Assert.InRange(value, -1e-3f, 1f + 1e-3f);
        }
    }

    [Fact]
    public void SpringWithCriticalDampingDoesNotOvershoot()
    {
        for (int i = 0; i <= 100; i++)
        {
            float value = Easing.Spring(i / 100f, damping: 1f);
            Assert.InRange(value, -1e-4f, 1f + 1e-4f);
        }
    }

    [Fact]
    public void VelocityIsZeroAtRestAndPositiveWhileMoving()
    {
        EasingCurve curve = EasingCurve.DefaultClose;

        Assert.True(curve.EvaluateVelocity(1f) < 0.05f, "Ease-out curve should be nearly stopped at the end.");
        Assert.True(curve.EvaluateVelocity(0.15f) > 0f, "Curve should be moving early on.");
    }

    [Fact]
    public void BezierRejectsOutOfRangeTimeControlPoints()
    {
        // X outside [0,1] would make time run backwards; it must be clamped.
        CubicBezierEasing curve = new(-3f, 0.5f, 7f, 0.5f);

        Assert.Equal(0f, curve.X1);
        Assert.Equal(1f, curve.X2);
    }
}
