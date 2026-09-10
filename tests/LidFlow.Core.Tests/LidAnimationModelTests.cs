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

    [Fact]
    public void ClosingFirstFrameLeavesTheSnapshotCompletelyUntouched()
    {
        // This is the "no flash, no frame mismatch" guarantee: the very first frame of a
        // close must be indistinguishable from the real desktop, so every effect term has
        // to be exactly zero and the aperture must sit outside the panel.
        LidAnimationModel model = new(Config(), TransitionKind.Close);
        LidFrameParameters frame = model.Evaluate(0f);

        Assert.Equal(0f, frame.Progress, 5);
        Assert.True(frame.ApertureTop < 0f, "Top edge must start above the panel.");
        Assert.True(frame.ApertureBottom > 1f, "Bottom edge must start below the panel.");
        Assert.Equal(0f, frame.SideInset, 5);
        Assert.Equal(0f, frame.Keystone, 5);
        Assert.Equal(0f, frame.BlurRadiusPx, 5);
        Assert.Equal(0f, frame.ShadowStrength, 5);
        Assert.Equal(0f, frame.ShadowExtentPx, 5);
        Assert.Equal(0f, frame.WarpStrength, 5);
        Assert.Equal(0f, frame.DistortionStrength, 5);
        Assert.Equal(0f, frame.OffAxisWash, 5);
        Assert.Equal(0f, frame.GlareStrength, 5);
        Assert.Equal(0f, frame.CornerRadiusPx, 5);
        Assert.Equal(0f, frame.BezelAmbient, 5);
        Assert.Equal(1f, frame.Luminance, 5);
    }

    [Fact]
    public void BezelAmbientRampsInButStaysFarBelowContentLuminance()
    {
        // The bezel term exists to stop the boundary reading as a hole cut in the
        // image. It has to be absent on the first frame, present once the panel is
        // moving, and small enough never to look like a glow effect.
        LidAnimationModel model = new(Config(), TransitionKind.Close);

        Assert.Equal(0f, model.Evaluate(0f).BezelAmbient, 5);

        float mid = model.Evaluate(0.5f).BezelAmbient;
        Assert.True(mid > 0f, "Bezel ambient should be present mid-transition.");
        Assert.True(mid < 0.05f, $"Bezel ambient {mid} is too strong to read as a bezel.");
    }

    [Fact]
    public void DisablingShadowAlsoRemovesTheBezelAmbient()
    {
        // They are the same physical idea - light behaviour at the panel edge - so
        // turning the edge treatment off must remove both.
        AnimationConfig config = Config();
        config.EnableShadow = false;
        config.Normalize();

        LidAnimationModel model = new(config, TransitionKind.Close);

        for (int i = 0; i <= 20; i++)
        {
            Assert.Equal(0f, model.Evaluate(i / 20f).BezelAmbient, 5);
        }
    }

    [Fact]
    public void ClosingLastFrameCollapsesTheApertureToNothing()
    {
        LidAnimationModel model = new(Config(), TransitionKind.Close);
        LidFrameParameters frame = model.Evaluate(1f);

        Assert.Equal(1f, frame.Progress, 5);
        Assert.Equal(0f, frame.ApertureHeight, 5);
        Assert.Equal(frame.HingeY, frame.ApertureTop, 5);
        Assert.Equal(frame.HingeY, frame.ApertureBottom, 5);
    }

    [Fact]
    public void OpeningRunsFromClosedToOpen()
    {
        LidAnimationModel model = new(Config(), TransitionKind.Open);

        Assert.Equal(1f, model.Evaluate(0f).Progress, 5);
        Assert.Equal(0f, model.Evaluate(0f).ApertureHeight, 5);

        LidFrameParameters last = model.Evaluate(1f);
        Assert.Equal(0f, last.Progress, 5);
        Assert.True(last.ApertureTop < 0f);
        Assert.True(last.ApertureBottom > 1f);
        Assert.Equal(0f, last.BlurRadiusPx, 5);
    }

    [Fact]
    public void OpeningIsNotAMirroredReplayOfClosing()
    {
        // Requirement: opening uses its own tuned curve and its own (shorter) duration.
        AnimationConfig config = Config();
        LidAnimationModel close = new(config, TransitionKind.Close);
        LidAnimationModel open = new(config, TransitionKind.Open);

        Assert.True(open.DurationMs < close.DurationMs);

        bool differs = false;
        for (int i = 1; i < 20; i++)
        {
            float t = i / 20f;
            // Compare panel position at mirrored times.
            if (MathF.Abs(close.Evaluate(t).Progress - (1f - open.Evaluate(t).Progress)) > 1e-3f)
            {
                differs = true;
                break;
            }
        }

        Assert.True(differs, "Opening should not be the exact time-reverse of closing.");
    }

    [Fact]
    public void ApertureHeightShrinksMonotonicallyWhileClosing()
    {
        LidAnimationModel model = new(Config(), TransitionKind.Close);
        float previous = float.MaxValue;

        for (int i = 0; i <= 300; i++)
        {
            float height = model.Evaluate(i / 300f).ApertureHeight;
            Assert.True(height <= previous + 1e-4f, $"Aperture grew at t={i / 300f}");
            previous = height;
        }
    }

    [Fact]
    public void TopEdgeTravelsFurtherThanBottomEdge()
    {
        // The asymmetry is what makes it read as a hinge below the screen rather than an iris.
        AnimationConfig config = Config();
        LidAnimationModel model = new(config, TransitionKind.Close);

        LidFrameParameters start = model.Evaluate(0f);
        LidFrameParameters end = model.Evaluate(1f);

        float topTravel = end.ApertureTop - start.ApertureTop;
        float bottomTravel = start.ApertureBottom - end.ApertureBottom;

        Assert.True(topTravel > bottomTravel, $"Top travel {topTravel} should exceed bottom travel {bottomTravel}.");
        Assert.True(config.HingeBias > 0.5f, "Default hinge line should sit below centre.");
    }

    [Fact]
    public void ApertureIsATrapezoidNarrowingAwayFromTheHinge()
    {
        LidAnimationModel model = new(Config(), TransitionKind.Close);
        LidFrameParameters mid = model.Evaluate(0.65f);

        Assert.True(mid.SideInset > 0f, "Sides should have started closing by mid-transition.");
        Assert.True(mid.Keystone > 0f, "Keystone must be non-zero for the perspective cue.");
    }

    [Fact]
    public void SidesStartClosingLaterThanTopAndBottom()
    {
        AnimationConfig config = Config();
        LidAnimationModel model = new(config, TransitionKind.Close);

        // Find a time whose panel progress is below the side delay and assert the sides
        // have not moved, while the top/bottom edges already have.
        float t = model.FindLinearTimeForPanelProgress(config.SideDelay * 0.5f);
        LidFrameParameters frame = model.Evaluate(t);

        Assert.Equal(0f, frame.SideInset, 5);
        Assert.True(frame.ApertureHeight < 1f, "Top/bottom edges should already be moving.");
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
    public void BlurFollowsEdgeSpeedNotElapsedTime()
    {
        // With full velocity influence the blur must vanish once the edge stops moving,
        // even though progress is still high.
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
        }
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
        // Reversing mid-flight must not jump the panel: the open animation resumed at the
        // matched time has to show the same aperture as the close animation it replaced.
        AnimationConfig config = Config();
        LidAnimationModel close = new(config, TransitionKind.Close);
        LidAnimationModel open = new(config, TransitionKind.Open);

        LidFrameParameters interrupted = close.Evaluate(0.4f);
        float resumeT = open.FindLinearTimeForPanelProgress(interrupted.Progress);
        LidFrameParameters resumed = open.Evaluate(resumeT);

        Assert.InRange(resumed.Progress, interrupted.Progress - 0.02f, interrupted.Progress + 0.02f);
        Assert.InRange(resumed.ApertureTop, interrupted.ApertureTop - 0.02f, interrupted.ApertureTop + 0.02f);
        Assert.InRange(resumed.ApertureBottom, interrupted.ApertureBottom - 0.02f, interrupted.ApertureBottom + 0.02f);
    }

    [Fact]
    public void EveryOutputStaysFiniteForRandomConfigurations()
    {
        // A hand-edited config.json must never be able to produce NaN, which on the GPU
        // would show up as a garbage frame rather than a clean failure.
        Random random = new(20260910);

        for (int iteration = 0; iteration < 400; iteration++)
        {
            AnimationConfig config = new()
            {
                HingeBias = (float)random.NextDouble() * 3f - 1f,
                EdgeExpansion = (float)random.NextDouble() * 4f - 1f,
                SideDelay = (float)random.NextDouble() * 3f - 1f,
                PerspectiveStrength = (float)random.NextDouble() * 10f - 2f,
                EdgeSoftness = (float)random.NextDouble() * 200f - 50f,
                CornerRadiusPx = (float)random.NextDouble() * 1000f - 200f,
                ShadowExtentPx = (float)random.NextDouble() * 3000f - 500f,
                ShadowStrength = (float)random.NextDouble() * 4f - 2f,
                MaxBlur = (float)random.NextDouble() * 900f - 100f,
                BlurRadius = (float)random.NextDouble() * 5000f - 1000f,
                BlurVelocityInfluence = (float)random.NextDouble() * 3f - 1f,
                BlackOpacity = (float)random.NextDouble() * 3f - 1f,
                GlareStrength = (float)random.NextDouble() * 5f - 2f,
                GlareOffsetPx = (float)random.NextDouble() * 2000f - 500f,
                GlareWidthPx = (float)random.NextDouble() * 2000f - 500f,
                WarpStrength = (float)random.NextDouble() * 4f - 2f,
                DistortionStrength = (float)random.NextDouble() * 20f - 5f,
                OffAxisWash = (float)random.NextDouble() * 4f - 2f,
                GlobalDim = (float)random.NextDouble() * 4f - 2f,
                CloseDurationMs = random.Next(-5000, 100_000),
                OpenDurationMs = random.Next(-5000, 100_000),
            };
            config.Normalize();

            foreach (TransitionKind kind in new[] { TransitionKind.Close, TransitionKind.Open })
            {
                LidAnimationModel model = new(config, kind);

                for (int i = 0; i <= 16; i++)
                {
                    LidFrameParameters frame = model.Evaluate(i / 16f);
                    AssertAllFinite(frame);
                }
            }
        }
    }

    private static void AssertAllFinite(LidFrameParameters frame)
    {
        Assert.True(float.IsFinite(frame.Progress), nameof(frame.Progress));
        Assert.True(float.IsFinite(frame.EdgeVelocity), nameof(frame.EdgeVelocity));
        Assert.True(float.IsFinite(frame.ApertureTop), nameof(frame.ApertureTop));
        Assert.True(float.IsFinite(frame.ApertureBottom), nameof(frame.ApertureBottom));
        Assert.True(float.IsFinite(frame.SideInset), nameof(frame.SideInset));
        Assert.True(float.IsFinite(frame.Keystone), nameof(frame.Keystone));
        Assert.True(float.IsFinite(frame.HingeY), nameof(frame.HingeY));
        Assert.True(float.IsFinite(frame.CornerRadiusPx), nameof(frame.CornerRadiusPx));
        Assert.True(float.IsFinite(frame.EdgeSoftnessPx), nameof(frame.EdgeSoftnessPx));
        Assert.True(float.IsFinite(frame.ShadowExtentPx), nameof(frame.ShadowExtentPx));
        Assert.True(float.IsFinite(frame.ShadowStrength), nameof(frame.ShadowStrength));
        Assert.True(float.IsFinite(frame.BlurRadiusPx), nameof(frame.BlurRadiusPx));
        Assert.True(float.IsFinite(frame.BlurExtentPx), nameof(frame.BlurExtentPx));
        Assert.True(float.IsFinite(frame.BlackOpacity), nameof(frame.BlackOpacity));
        Assert.True(float.IsFinite(frame.GlareStrength), nameof(frame.GlareStrength));
        Assert.True(float.IsFinite(frame.GlareOffsetPx), nameof(frame.GlareOffsetPx));
        Assert.True(float.IsFinite(frame.GlareWidthPx), nameof(frame.GlareWidthPx));
        Assert.True(float.IsFinite(frame.WarpStrength), nameof(frame.WarpStrength));
        Assert.True(float.IsFinite(frame.DistortionStrength), nameof(frame.DistortionStrength));
        Assert.True(float.IsFinite(frame.OffAxisWash), nameof(frame.OffAxisWash));
        Assert.True(float.IsFinite(frame.Luminance), nameof(frame.Luminance));
        Assert.True(float.IsFinite(frame.BezelAmbient), nameof(frame.BezelAmbient));
        Assert.True(float.IsFinite(frame.BezelFalloffPx), nameof(frame.BezelFalloffPx));
    }
}
