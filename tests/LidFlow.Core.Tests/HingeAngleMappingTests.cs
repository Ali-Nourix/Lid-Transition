using System;
using LidFlow.Core.Animation;
using LidFlow.Core.Configuration;
using LidFlow.Core.Lid;
using Xunit;

namespace LidFlow.Core.Tests;

public sealed class HingeAngleMappingTests
{
    [Fact]
    public void ClosedAngleMapsToFullyClosed()
    {
        Assert.Equal(1f, HingeAngleMapping.ProgressFromAngle(0d), 5);
        Assert.Equal(1f, HingeAngleMapping.ProgressFromAngle(HingeAngleMapping.DefaultClosedAngleDegrees), 5);
        Assert.Equal(1f, HingeAngleMapping.ProgressFromAngle(-10d), 5);
    }

    [Fact]
    public void OpenAngleAndBeyondMapToFullyOpen()
    {
        Assert.Equal(0f, HingeAngleMapping.ProgressFromAngle(HingeAngleMapping.DefaultOpenAngleDegrees), 5);
        Assert.Equal(0f, HingeAngleMapping.ProgressFromAngle(90d), 5);
        Assert.Equal(0f, HingeAngleMapping.ProgressFromAngle(135d), 5);
    }

    [Fact]
    public void ProgressDecreasesMonotonicallyAsTheLidOpens()
    {
        float previous = 2f;

        for (int degrees = 0; degrees <= 120; degrees++)
        {
            float progress = HingeAngleMapping.ProgressFromAngle(degrees);
            Assert.InRange(progress, 0f, 1f);
            Assert.True(progress <= previous + 1e-5f, $"Progress rose at {degrees} deg.");
            previous = progress;
        }
    }

    [Fact]
    public void MappingIsLinearInTheAngle()
    {
        // Progress here means "how far through its travel the hinge is", which is
        // exactly what the sensor reports. The geometry - the sine and cosine of
        // the rotation - lives in the projection, so curving it here as well would
        // double-apply it and make the image lag the physical panel.
        const float Closed = 0f;
        const float Open = 90f;

        Assert.Equal(0.5f, HingeAngleMapping.ProgressFromAngle(45d, Closed, Open), 4);
        Assert.Equal(0.75f, HingeAngleMapping.ProgressFromAngle(22.5d, Closed, Open), 4);
        Assert.Equal(0.25f, HingeAngleMapping.ProgressFromAngle(67.5d, Closed, Open), 4);
    }

    [Fact]
    public void InvertedAngleRangeIsToleratedRatherThanProducingNonsense()
    {
        float progress = HingeAngleMapping.ProgressFromAngle(30d, closedAngleDegrees: 80f, openAngleDegrees: 10f);

        Assert.InRange(progress, 0f, 1f);
        Assert.False(float.IsNaN(progress));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteAnglesNeverProduceNonFiniteProgress(double angle)
    {
        float progress = HingeAngleMapping.ProgressFromAngle(angle);

        Assert.True(float.IsFinite(progress));
        Assert.InRange(progress, 0f, 1f);
    }

    [Fact]
    public void VelocityNormalizationSaturatesAndIgnoresDirection()
    {
        Assert.Equal(0f, HingeAngleMapping.NormalizeVelocity(0d), 5);
        Assert.Equal(1f, HingeAngleMapping.NormalizeVelocity(3d, referenceRate: 3f), 5);
        Assert.Equal(1f, HingeAngleMapping.NormalizeVelocity(30d, referenceRate: 3f), 5);

        // Closing and opening at the same speed must blur identically.
        Assert.Equal(
            HingeAngleMapping.NormalizeVelocity(1.5d, 3f),
            HingeAngleMapping.NormalizeVelocity(-1.5d, 3f),
            5);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void VelocityNormalizationRejectsNonFiniteRates(double rate) =>
        Assert.Equal(0f, HingeAngleMapping.NormalizeVelocity(rate), 5);

    [Fact]
    public void DerivedLidStateUsesHysteresis()
    {
        // Below the close threshold and above the open threshold are unambiguous.
        Assert.Equal(LidState.Closed, HingeAngleMapping.StateFromAngle(2d, LidState.Open));
        Assert.Equal(LidState.Open, HingeAngleMapping.StateFromAngle(40d, LidState.Closed));

        // Inside the band the previous state is retained, so sensor noise around a
        // single threshold cannot flip the state repeatedly.
        Assert.Equal(LidState.Open, HingeAngleMapping.StateFromAngle(8d, LidState.Open));
        Assert.Equal(LidState.Closed, HingeAngleMapping.StateFromAngle(8d, LidState.Closed));
    }

    [Fact]
    public void UnknownStateIsPreservedInsideTheHysteresisBand() =>
        Assert.Equal(LidState.Unknown, HingeAngleMapping.StateFromAngle(8d, LidState.Unknown));
}

public sealed class HingeDrivenAnimationTests
{
    private static AnimationConfig Config()
    {
        AnimationConfig config = new();
        config.Normalize();
        return config;
    }

    [Fact]
    public void HingeDrivenEndpointsMatchTheTimedPathExactly()
    {
        // The two input modes must be visually interchangeable, so the geometry at
        // a given panel position has to be identical whichever produced it.
        AnimationConfig config = Config();
        LidAnimationModel model = new(config, TransitionKind.Close);

        LidFrameParameters timedStart = model.Evaluate(0f);
        LidFrameParameters hingeStart = model.EvaluateAtProgress(0f, 0f);

        Assert.Equal(timedStart.AngleDegrees, hingeStart.AngleDegrees, 5);
        Assert.Equal(timedStart.CosTheta, hingeStart.CosTheta, 5);
        Assert.Equal(timedStart.SinTheta, hingeStart.SinTheta, 5);
        Assert.Equal(timedStart.BlurRadiusPx, hingeStart.BlurRadiusPx, 5);

        LidFrameParameters timedEnd = model.Evaluate(1f);
        LidFrameParameters hingeEnd = model.EvaluateAtProgress(1f, 0f);

        Assert.Equal(timedEnd.AngleDegrees, hingeEnd.AngleDegrees, 5);
        Assert.Equal(0f, hingeEnd.ProjectedCoverage, 5);
    }

    [Fact]
    public void AStationaryLidHasNoMotionBlur()
    {
        // The defining property of hinge tracking: blur comes from movement, so a
        // lid held still part-way through must be sharp, however far it has closed.
        AnimationConfig config = Config();
        config.BlurVelocityInfluence = 1f;
        config.Normalize();

        LidAnimationModel model = new(config, TransitionKind.Close);

        LidFrameParameters still = model.EvaluateAtProgress(0.5f, normalizedVelocity: 0f);
        LidFrameParameters moving = model.EvaluateAtProgress(0.5f, normalizedVelocity: 1f);

        Assert.Equal(0f, still.BlurRadiusPx, 5);
        Assert.True(moving.BlurRadiusPx > 1f);

        // The panel itself must be in the same place either way: only the blur
        // depends on how fast it is moving.
        Assert.Equal(still.AngleDegrees, moving.AngleDegrees, 5);
        Assert.Equal(still.ProjectedCoverage, moving.ProjectedCoverage, 5);
    }

    [Fact]
    public void HingeDrivenParametersStayFiniteAcrossTheWholeRange()
    {
        AnimationConfig config = Config();

        foreach (TransitionKind kind in new[] { TransitionKind.Close, TransitionKind.Open })
        {
            LidAnimationModel model = new(config, kind);

            for (int p = 0; p <= 40; p++)
            {
                for (int v = 0; v <= 4; v++)
                {
                    LidFrameParameters frame = model.EvaluateAtProgress(p / 40f, v / 4f);

                    Assert.True(float.IsFinite(frame.AngleDegrees));
                    Assert.True(float.IsFinite(frame.CosTheta));
                    Assert.True(float.IsFinite(frame.SinTheta));
                    Assert.True(float.IsFinite(frame.ProjectedCoverage));
                    Assert.True(float.IsFinite(frame.BlurRadiusPx));
                    Assert.True(float.IsFinite(frame.ShadowStrength));
                    Assert.True(float.IsFinite(frame.Luminance));
                    Assert.InRange(frame.Progress, 0f, 1f);
                }
            }
        }
    }

    [Fact]
    public void OutOfRangeInputsAreClampedRatherThanExtrapolated()
    {
        LidAnimationModel model = new(Config(), TransitionKind.Close);

        LidFrameParameters low = model.EvaluateAtProgress(-4f, -2f);
        LidFrameParameters high = model.EvaluateAtProgress(9f, 12f);

        Assert.Equal(0f, low.Progress, 5);
        Assert.Equal(1f, high.Progress, 5);
        Assert.True(float.IsFinite(high.BlurRadiusPx));
    }

    [Fact]
    public void HingeAngleConfigurationIsClampedToASaneRange()
    {
        AnimationConfig config = new()
        {
            HingeClosedAngleDeg = 120f,
            HingeOpenAngleDeg = 5f,
            HingeVelocityReference = -3f,
        };

        config.Normalize();

        Assert.True(config.HingeOpenAngleDeg > config.HingeClosedAngleDeg);
        Assert.True(config.HingeVelocityReference > 0f);
    }

    [Fact]
    public void HingeTrackingIsOnByDefaultButDegradesSilently()
    {
        // Enabled by default because it is strictly better where present; the app
        // detects its absence at runtime and uses the timed path instead.
        AnimationConfig config = Config();
        Assert.True(config.UseHingeAngleWhenAvailable);
    }
}
