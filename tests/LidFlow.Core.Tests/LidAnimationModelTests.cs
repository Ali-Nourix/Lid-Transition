using System;
using LidFlow.Core.Animation;
using LidFlow.Core.Configuration;
using Xunit;

namespace LidFlow.Core.Tests;

public sealed class LidAnimationModelTests
{
    private static AnimationConfig Config()
    {
        AnimationConfig config = new();
        config.Normalize();
        return config;
    }

    /// <summary>
    /// The forward projection the shader inverts: screen height above the hinge of
    /// a point at distance <paramref name="s"/> along the panel. Replicated here so
    /// the tests can reason about the geometry the shader actually produces.
    /// </summary>
    private static float ProjectedHeight(in LidFrameParameters frame, float s)
    {
        float denominator = 1f + (s * frame.SinTheta * frame.Perspective);
        return denominator <= 1e-5f ? 0f : s * frame.CosTheta / denominator;
    }

    /// <summary>Projected half-width of the panel at distance <paramref name="s"/>, relative to fully open.</summary>
    private static float ProjectedWidth(in LidFrameParameters frame, float s) =>
        1f / (1f + (s * frame.SinTheta * frame.Perspective));

    [Fact]
    public void ClosingFirstFrameIsTheIdentityProjectionWithNoEffects()
    {
        // This is the "no flash, no frame mismatch" guarantee: the first frame of a
        // close must be indistinguishable from the real desktop. That means the
        // projection has to be exactly the identity and every optical term zero.
        LidAnimationModel model = new(Config(), TransitionKind.Close);
        LidFrameParameters frame = model.Evaluate(0f);

        Assert.Equal(0f, frame.Progress, 5);
        Assert.Equal(0f, frame.AngleDegrees, 5);
        Assert.Equal(1f, frame.CosTheta, 5);
        Assert.Equal(0f, frame.SinTheta, 5);

        // No rotation means no foreshortening and no keystone anywhere.
        for (int i = 0; i <= 10; i++)
        {
            float s = i / 10f;
            Assert.Equal(s, ProjectedHeight(frame, s), 5);
            Assert.Equal(1f, ProjectedWidth(frame, s), 5);
        }

        Assert.Equal(0f, frame.BlurRadiusPx, 5);
        Assert.Equal(0f, frame.ShadowStrength, 5);
        Assert.Equal(0f, frame.OffAxisWash, 5);
        Assert.Equal(0f, frame.GlareStrength, 5);
        Assert.Equal(0f, frame.DistortionStrength, 5);
        Assert.Equal(0f, frame.BezelAmbient, 5);
        Assert.Equal(1f, frame.Luminance, 5);
        Assert.Equal(1f, frame.ProjectedCoverage, 5);
    }

    [Fact]
    public void ClosingLastFrameLeavesThePanelEdgeOnAndCoveringNothing()
    {
        LidAnimationModel model = new(Config(), TransitionKind.Close);
        LidFrameParameters frame = model.Evaluate(1f);

        Assert.Equal(1f, frame.Progress, 5);
        Assert.Equal(90f, frame.AngleDegrees, 4);
        Assert.Equal(0f, frame.CosTheta, 5);
        Assert.Equal(0f, frame.ProjectedCoverage, 5);
    }

    [Fact]
    public void OpeningRunsFromClosedToOpen()
    {
        LidAnimationModel model = new(Config(), TransitionKind.Open);

        Assert.Equal(1f, model.Evaluate(0f).Progress, 5);
        Assert.Equal(0f, model.Evaluate(0f).ProjectedCoverage, 5);

        LidFrameParameters last = model.Evaluate(1f);
        Assert.Equal(0f, last.Progress, 5);
        Assert.Equal(1f, last.ProjectedCoverage, 5);
        Assert.Equal(0f, last.BlurRadiusPx, 5);
    }

    [Fact]
    public void OpeningIsNotAMirroredReplayOfClosing()
    {
        AnimationConfig config = Config();
        LidAnimationModel close = new(config, TransitionKind.Close);
        LidAnimationModel open = new(config, TransitionKind.Open);

        Assert.True(open.DurationMs < close.DurationMs);

        bool differs = false;
        for (int i = 1; i < 20; i++)
        {
            float t = i / 20f;
            if (MathF.Abs(close.Evaluate(t).Progress - (1f - open.Evaluate(t).Progress)) > 1e-3f)
            {
                differs = true;
                break;
            }
        }

        Assert.True(differs, "Opening should not be the exact time-reverse of closing.");
    }

    [Fact]
    public void PanelCoverageShrinksMonotonicallyWhileClosing()
    {
        LidAnimationModel model = new(Config(), TransitionKind.Close);
        float previous = float.MaxValue;

        for (int i = 0; i <= 300; i++)
        {
            float coverage = model.Evaluate(i / 300f).ProjectedCoverage;
            Assert.True(coverage <= previous + 1e-4f, $"Coverage grew at t={i / 300f}");
            previous = coverage;
        }
    }

    [Fact]
    public void RotationForeshortensTowardTheHingeSoBlackGrowsFromTheTop()
    {
        // The defining geometry: the hinge edge stays put and the far edge travels
        // toward it, so whatever the panel stops covering is at the TOP.
        LidAnimationModel model = new(Config(), TransitionKind.Close);
        LidFrameParameters mid = model.Evaluate(0.5f);

        Assert.True(mid.AngleDegrees > 0f);

        // The hinge edge does not move.
        Assert.Equal(0f, ProjectedHeight(mid, 0f), 5);

        // Every row above it has moved down, and the further up it started the
        // further it has come.
        float previousShift = -1f;
        for (int i = 1; i <= 10; i++)
        {
            float s = i / 10f;
            float shift = s - ProjectedHeight(mid, s);

            Assert.True(shift > 0f, $"Row s={s} should have moved toward the hinge.");
            Assert.True(shift > previousShift, "Rows further from the hinge must move further.");
            previousShift = shift;
        }
    }

    [Fact]
    public void RotationKeystonesSoThePanelNarrowsTowardTheFarEdge()
    {
        LidAnimationModel model = new(Config(), TransitionKind.Close);
        LidFrameParameters mid = model.Evaluate(0.5f);

        float atHinge = ProjectedWidth(mid, 0f);
        float atFarEdge = ProjectedWidth(mid, 1f);

        Assert.Equal(1f, atHinge, 5);
        Assert.True(atFarEdge < atHinge, "The far edge must project narrower than the hinge edge.");

        // Monotonic, so the panel outline is a clean trapezoid rather than a
        // pinched shape.
        float previous = float.MaxValue;
        for (int i = 0; i <= 10; i++)
        {
            float width = ProjectedWidth(mid, i / 10f);
            Assert.True(width <= previous + 1e-5f);
            previous = width;
        }
    }

    [Fact]
    public void WithoutPerspectiveThereIsNoKeystone()
    {
        // Orthographic squash: still foreshortens, but no convergence. Useful as a
        // tuning baseline, and it proves the keystone comes from the projection.
        AnimationConfig config = Config();
        config.PerspectiveStrength = 0f;
        config.Normalize();

        LidFrameParameters mid = new LidAnimationModel(config, TransitionKind.Close).Evaluate(0.5f);

        Assert.Equal(1f, ProjectedWidth(mid, 1f), 5);
        Assert.True(ProjectedHeight(mid, 1f) < 1f, "It should still foreshorten.");
    }

    [Fact]
    public void BlurIsStrongestAtTheFarEdgeAndZeroAtTheHinge()
    {
        // The point the reference makes plainly: blur comes from the top and fades
        // downward, because a row's speed is proportional to its distance from the
        // hinge. The shader multiplies BlurRadiusPx by pow(s, BlurFalloff), so the
        // gradient is asserted here through that relationship.
        LidAnimationModel model = new(Config(), TransitionKind.Close);
        LidFrameParameters mid = model.Evaluate(0.4f);

        Assert.True(mid.BlurRadiusPx > 1f, "There should be real blur mid-transition.");

        float atHinge = mid.BlurRadiusPx * MathF.Pow(0f, mid.BlurFalloff);
        float quarter = mid.BlurRadiusPx * MathF.Pow(0.25f, mid.BlurFalloff);
        float half = mid.BlurRadiusPx * MathF.Pow(0.5f, mid.BlurFalloff);
        float atFarEdge = mid.BlurRadiusPx * MathF.Pow(1f, mid.BlurFalloff);

        Assert.Equal(0f, atHinge, 5);
        Assert.True(quarter < half);
        Assert.True(half < atFarEdge);
        Assert.Equal(mid.BlurRadiusPx, atFarEdge, 4);
    }

    [Fact]
    public void BlurIsZeroAtBothEndsAndPeaksInBetween()
    {
        LidAnimationModel model = new(Config(), TransitionKind.Close);

        Assert.Equal(0f, model.Evaluate(0f).BlurRadiusPx, 5);
        Assert.Equal(0f, model.Evaluate(1f).BlurRadiusPx, 5);

        float peak = 0f;
        for (int i = 0; i <= 200; i++)
        {
            peak = MathF.Max(peak, model.Evaluate(i / 200f).BlurRadiusPx);
        }

        Assert.True(peak > 1f, "Blur must actually be applied somewhere in the middle.");
    }

    [Fact]
    public void BlurFollowsRotationSpeedNotElapsedTime()
    {
        AnimationConfig config = Config();
        config.BlurVelocityInfluence = 1f;
        config.Normalize();

        LidAnimationModel model = new(config, TransitionKind.Close);

        LidFrameParameters early = model.Evaluate(0.2f);
        LidFrameParameters late = model.Evaluate(0.97f);

        Assert.True(early.EdgeVelocity > late.EdgeVelocity);
        Assert.True(early.BlurRadiusPx > late.BlurRadiusPx);
    }

    [Fact]
    public void ShadowAndWashAlsoWeightTowardTheFarEdge()
    {
        // Same physical reason as the blur: the far edge is the most oblique and
        // the furthest away, so it dims and washes out first.
        LidAnimationModel model = new(Config(), TransitionKind.Close);
        LidFrameParameters mid = model.Evaluate(0.5f);

        Assert.True(mid.ShadowStrength > 0f);
        Assert.True(mid.ShadowFalloff > 1f, "The dim should concentrate toward the far edge.");
        Assert.True(mid.OffAxisWash > 0f);

        float dimAtHinge = mid.ShadowStrength * MathF.Pow(0f, mid.ShadowFalloff);
        float dimAtFarEdge = mid.ShadowStrength * MathF.Pow(1f, mid.ShadowFalloff);

        Assert.Equal(0f, dimAtHinge, 5);
        Assert.True(dimAtFarEdge > dimAtHinge);
    }

    [Fact]
    public void GlareSitsOnTheLeadingEdge()
    {
        LidAnimationModel model = new(Config(), TransitionKind.Close);
        LidFrameParameters mid = model.Evaluate(0.5f);

        Assert.True(mid.GlareStrength > 0f);

        // The shader's envelope is exp(-((1-s)/width)^2), so it peaks at s = 1.
        float atFarEdge = MathF.Exp(-MathF.Pow((1f - 1f) / mid.GlareWidth, 2f));
        float atHinge = MathF.Exp(-MathF.Pow((1f - 0f) / mid.GlareWidth, 2f));

        Assert.Equal(1f, atFarEdge, 5);
        Assert.True(atHinge < 0.01f, "The highlight must not reach the hinge edge.");
    }

    [Fact]
    public void TheHingeEdgeStaysSharpAndBrightThroughout()
    {
        // The single most load-bearing consequence of the model: whatever else
        // happens, the row at the hinge is neither blurred nor dimmed, because it
        // is not moving. If this ever fails the effect reads as a global fade.
        LidAnimationModel model = new(Config(), TransitionKind.Close);

        for (int i = 0; i <= 50; i++)
        {
            LidFrameParameters frame = model.Evaluate(i / 50f);

            Assert.Equal(0f, frame.BlurRadiusPx * MathF.Pow(0f, frame.BlurFalloff), 5);
            Assert.Equal(0f, frame.ShadowStrength * MathF.Pow(0f, frame.ShadowFalloff), 5);
        }
    }

    [Fact]
    public void TheHingeSitsBelowTheVisiblePanel()
    {
        // A real hinge is behind the bottom bezel, so the bottom row foreshortens
        // slightly too rather than being pinned dead still.
        LidFrameParameters frame = new LidAnimationModel(Config(), TransitionKind.Close).Evaluate(0.5f);

        Assert.True(frame.PivotV > 1f, "The pivot must be below the bottom of the display.");
        Assert.True(frame.PivotV < 1.5f, "But not so far below that the panel barely moves.");
    }

    [Fact]
    public void DisablingEffectsZeroesTheCorrespondingChannels()
    {
        AnimationConfig config = Config();
        config.EnableBlur = false;
        config.EnableShadow = false;
        config.EnableDistortion = false;
        config.EnableGlare = false;
        config.Normalize();

        LidAnimationModel model = new(config, TransitionKind.Close);

        for (int i = 0; i <= 40; i++)
        {
            LidFrameParameters frame = model.Evaluate(i / 40f);
            Assert.Equal(0f, frame.BlurRadiusPx, 5);
            Assert.Equal(0f, frame.ShadowStrength, 5);
            Assert.Equal(0f, frame.DistortionStrength, 5);
            Assert.Equal(0f, frame.GlareStrength, 5);
            Assert.Equal(0f, frame.BezelAmbient, 5);
        }

        // Rotation is geometry, not an effect, so it must still happen.
        Assert.True(model.Evaluate(0.5f).AngleDegrees > 0f);
    }

    [Fact]
    public void BezelAmbientRampsInButStaysFarBelowContentLuminance()
    {
        LidAnimationModel model = new(Config(), TransitionKind.Close);

        Assert.Equal(0f, model.Evaluate(0f).BezelAmbient, 5);

        float mid = model.Evaluate(0.5f).BezelAmbient;
        Assert.True(mid > 0f, "Bezel ambient should be present mid-transition.");
        Assert.True(mid < 0.05f, $"Bezel ambient {mid} is too strong to read as a bezel.");
    }

    [Theory]
    [InlineData(AnimationQuality.Performance, 5)]
    [InlineData(AnimationQuality.Balanced, 9)]
    [InlineData(AnimationQuality.High, 13)]
    public void QualityTierSelectsTapCount(AnimationQuality quality, int expectedTaps)
    {
        AnimationConfig config = Config();
        config.Quality = quality;

        LidAnimationModel model = new(config, TransitionKind.Close);

        Assert.Equal(expectedTaps, model.BlurTaps);
        Assert.Equal(expectedTaps, model.Evaluate(0.5f).BlurTaps);
    }

    [Fact]
    public void ProgressInversionRoundTripsForBothDirections()
    {
        AnimationConfig config = Config();

        foreach (TransitionKind kind in new[] { TransitionKind.Close, TransitionKind.Open })
        {
            LidAnimationModel model = new(config, kind);

            for (int i = 1; i < 20; i++)
            {
                float target = i / 20f;
                float t = model.FindLinearTimeForPanelProgress(target);
                float actual = model.Evaluate(t).Progress;

                Assert.InRange(actual, target - 0.02f, target + 0.02f);
            }
        }
    }

    [Fact]
    public void ReversalMatchesPanelPositionAcrossDirections()
    {
        AnimationConfig config = Config();
        LidAnimationModel close = new(config, TransitionKind.Close);
        LidAnimationModel open = new(config, TransitionKind.Open);

        LidFrameParameters interrupted = close.Evaluate(0.4f);
        float resumeT = open.FindLinearTimeForPanelProgress(interrupted.Progress);
        LidFrameParameters resumed = open.Evaluate(resumeT);

        Assert.InRange(resumed.Progress, interrupted.Progress - 0.02f, interrupted.Progress + 0.02f);
        Assert.InRange(resumed.AngleDegrees, interrupted.AngleDegrees - 2f, interrupted.AngleDegrees + 2f);
        Assert.InRange(
            resumed.ProjectedCoverage,
            interrupted.ProjectedCoverage - 0.03f,
            interrupted.ProjectedCoverage + 0.03f);
    }

    [Fact]
    public void EveryOutputStaysFiniteForRandomConfigurations()
    {
        // A hand-edited config.json must never be able to produce NaN, which on the
        // GPU would show up as a garbage frame rather than a clean failure.
        Random random = new(20260910);

        for (int iteration = 0; iteration < 400; iteration++)
        {
            AnimationConfig config = new()
            {
                PanelMaxAngleDeg = (float)random.NextDouble() * 400f - 100f,
                PerspectiveStrength = (float)random.NextDouble() * 12f - 4f,
                HingeOffset = (float)random.NextDouble() * 4f - 2f,
                EdgeSoftness = (float)random.NextDouble() * 200f - 50f,
                MaxBlur = (float)random.NextDouble() * 900f - 100f,
                BlurFalloff = (float)random.NextDouble() * 20f - 5f,
                BlurVelocityInfluence = (float)random.NextDouble() * 3f - 1f,
                BlackOpacity = (float)random.NextDouble() * 3f - 1f,
                ShadowStrength = (float)random.NextDouble() * 4f - 2f,
                ShadowFalloff = (float)random.NextDouble() * 20f - 5f,
                GlareStrength = (float)random.NextDouble() * 5f - 2f,
                GlareWidth = (float)random.NextDouble() * 5f - 2f,
                DistortionStrength = (float)random.NextDouble() * 20f - 5f,
                OffAxisWash = (float)random.NextDouble() * 4f - 2f,
                GlobalDim = (float)random.NextDouble() * 4f - 2f,
                BezelAmbient = (float)random.NextDouble() * 4f - 2f,
                BezelFalloffPx = (float)random.NextDouble() * 900f - 200f,
                CloseDurationMs = random.Next(-5000, 100_000),
                OpenDurationMs = random.Next(-5000, 100_000),
            };
            config.Normalize();

            foreach (TransitionKind kind in new[] { TransitionKind.Close, TransitionKind.Open })
            {
                LidAnimationModel model = new(config, kind);

                for (int i = 0; i <= 16; i++)
                {
                    AssertAllFinite(model.Evaluate(i / 16f));
                }
            }
        }
    }

    private static void AssertAllFinite(LidFrameParameters frame)
    {
        Assert.True(float.IsFinite(frame.Progress), nameof(frame.Progress));
        Assert.True(float.IsFinite(frame.EdgeVelocity), nameof(frame.EdgeVelocity));
        Assert.True(float.IsFinite(frame.AngleDegrees), nameof(frame.AngleDegrees));
        Assert.True(float.IsFinite(frame.CosTheta), nameof(frame.CosTheta));
        Assert.True(float.IsFinite(frame.SinTheta), nameof(frame.SinTheta));
        Assert.True(float.IsFinite(frame.Perspective), nameof(frame.Perspective));
        Assert.True(float.IsFinite(frame.PivotV), nameof(frame.PivotV));
        Assert.True(float.IsFinite(frame.EdgeSoftnessPx), nameof(frame.EdgeSoftnessPx));
        Assert.True(float.IsFinite(frame.BlurRadiusPx), nameof(frame.BlurRadiusPx));
        Assert.True(float.IsFinite(frame.BlurFalloff), nameof(frame.BlurFalloff));
        Assert.True(float.IsFinite(frame.BlackOpacity), nameof(frame.BlackOpacity));
        Assert.True(float.IsFinite(frame.ShadowStrength), nameof(frame.ShadowStrength));
        Assert.True(float.IsFinite(frame.ShadowFalloff), nameof(frame.ShadowFalloff));
        Assert.True(float.IsFinite(frame.OffAxisWash), nameof(frame.OffAxisWash));
        Assert.True(float.IsFinite(frame.Luminance), nameof(frame.Luminance));
        Assert.True(float.IsFinite(frame.GlareStrength), nameof(frame.GlareStrength));
        Assert.True(float.IsFinite(frame.GlareWidth), nameof(frame.GlareWidth));
        Assert.True(float.IsFinite(frame.DistortionStrength), nameof(frame.DistortionStrength));
        Assert.True(float.IsFinite(frame.BezelAmbient), nameof(frame.BezelAmbient));
        Assert.True(float.IsFinite(frame.BezelFalloffPx), nameof(frame.BezelFalloffPx));
        Assert.True(float.IsFinite(frame.ProjectedCoverage), nameof(frame.ProjectedCoverage));
    }
}
