using System;
using LidFlow.Core.Configuration;
using LidFlow.Core.Lid;
using Xunit;

namespace LidFlow.Core.Tests;

public sealed class LidAngleTrackerTests
{
    /// <summary>
    /// Drives the tracker with a pitch ramp, one sample every 16 ms, the way a
    /// ~60 Hz sensor would.
    /// </summary>
    private static double Feed(LidAngleTracker tracker, double from, double to, double seconds, double startTime = 0d)
    {
        const double Step = 1d / 60d;
        int samples = Math.Max((int)(seconds / Step), 1);

        for (int i = 0; i <= samples; i++)
        {
            double fraction = i / (double)samples;
            tracker.Update(from + ((to - from) * fraction), startTime + (fraction * seconds));
        }

        return startTime + seconds;
    }

    /// <summary>Holds pitch still long enough for the tracker to consider it at rest.</summary>
    private static double Settle(LidAngleTracker tracker, double pitch, double startTime)
    {
        for (int i = 0; i < 40; i++)
        {
            tracker.Update(pitch, startTime + (i / 60d));
        }

        return startTime + (40 / 60d);
    }

    [Fact]
    public void StartsUncalibratedAndReportsNothing()
    {
        LidAngleTracker tracker = new();

        Assert.False(tracker.IsCalibrated);
        Assert.False(tracker.TryGetProgress(out _, out _));
    }

    [Fact]
    public void LearnsTheRangeFromTheLidSwitchesTwoEvents()
    {
        // This is the whole idea: the switch only ever says 0 or 1, but those two
        // moments are exactly the two ends of the pitch range - and once both are
        // known, everything in between becomes readable.
        LidAngleTracker tracker = new();

        double t = Settle(tracker, 95d, 0d);
        tracker.ObserveLidState(LidState.Open);
        t = Settle(tracker, 95d, t);

        Assert.True(tracker.IsCalibrated, "Open alone is not enough.");

        t = Feed(tracker, 95d, 20d, 0.4d, t);
        tracker.ObserveLidState(LidState.Closed);

        Assert.True(tracker.IsCalibrated);
        Assert.Equal(-75d, tracker.SweepDegrees, 0);
    }

    [Fact]
    public void ReportsPositionContinuouslyBetweenTheTwoEvents()
    {
        LidAngleTracker tracker = Calibrated(openPitch: 95d, closedPitch: 20d);

        // Half way through the pitch range is half way closed.
        tracker.Update(57.5d, 100d);
        Assert.True(tracker.TryGetProgress(out float progress, out _));
        Assert.Equal(0.5f, progress, 2);

        tracker.Update(95d, 101d);
        Assert.True(tracker.TryGetProgress(out progress, out _));
        Assert.Equal(0f, progress, 3);

        tracker.Update(20d, 102d);
        Assert.True(tracker.TryGetProgress(out progress, out _));
        Assert.Equal(1f, progress, 3);
    }

    [Fact]
    public void PositionIsClampedBeyondEitherEnd()
    {
        LidAngleTracker tracker = Calibrated(openPitch: 95d, closedPitch: 20d);

        tracker.Update(140d, 100d);
        tracker.TryGetProgress(out float beyondOpen, out _);
        Assert.Equal(0f, beyondOpen, 4);

        tracker.Update(-30d, 101d);
        tracker.TryGetProgress(out float beyondClosed, out _);
        Assert.Equal(1f, beyondClosed, 4);
    }

    [Fact]
    public void MountingDirectionDoesNotMatter()
    {
        // The sign falls out of the subtraction, so a sensor mounted the other way
        // round works with no configuration.
        LidAngleTracker normal = Calibrated(openPitch: 95d, closedPitch: 20d);
        LidAngleTracker inverted = Calibrated(openPitch: 20d, closedPitch: 95d);

        normal.Update(57.5d, 10d);
        inverted.Update(57.5d, 10d);

        normal.TryGetProgress(out float a, out _);
        inverted.TryGetProgress(out float b, out _);

        Assert.Equal(0.5f, a, 2);
        Assert.Equal(0.5f, b, 2);
    }

    [Fact]
    public void MotionDirectionDistinguishesClosingFromOpening()
    {
        LidAngleTracker tracker = Calibrated(openPitch: 95d, closedPitch: 20d);

        double t = Feed(tracker, 95d, 60d, 0.3d, 10d);
        Assert.Equal(LidState.Closed, tracker.MotionDirection);

        Feed(tracker, 60d, 95d, 0.3d, t);
        Assert.Equal(LidState.Open, tracker.MotionDirection);
    }

    [Fact]
    public void AStationaryLidReportsNoMotionAndNoRate()
    {
        // Load-bearing for the blur: a lid held part-way must be sharp.
        LidAngleTracker tracker = Calibrated(openPitch: 95d, closedPitch: 20d);

        Settle(tracker, 57.5d, 10d);

        Assert.False(tracker.IsMoving);
        Assert.Null(tracker.MotionDirection);

        tracker.TryGetProgress(out float progress, out float rate);
        Assert.Equal(0.5f, progress, 2);
        Assert.InRange(MathF.Abs(rate), 0f, 0.05f);
    }

    [Fact]
    public void RateIsPositiveWhileClosingRegardlessOfMounting()
    {
        LidAngleTracker normal = Calibrated(openPitch: 95d, closedPitch: 20d);
        LidAngleTracker inverted = Calibrated(openPitch: 20d, closedPitch: 95d);

        Feed(normal, 95d, 40d, 0.25d, 10d);
        Feed(inverted, 20d, 75d, 0.25d, 10d);

        normal.TryGetProgress(out _, out float normalRate);
        inverted.TryGetProgress(out _, out float invertedRate);

        Assert.True(normalRate > 0f, "Closing should give a positive progress rate.");
        Assert.True(invertedRate > 0f, "And so should closing on an inverted sensor.");
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(5d)]
    [InlineData(19d)]
    public void ImplausiblySmallSweepsAreRejected(double sweep)
    {
        // A tiny range means the two observations were not of a real lid movement -
        // most likely the sensor is in the base, not the lid. Better to fall back
        // to the timed path than to drive the effect from noise.
        LidAngleTracker tracker = new();

        double t = Settle(tracker, 90d, 0d);
        tracker.ObserveLidState(LidState.Open);
        t = Settle(tracker, 90d, t);

        tracker.Update(90d - sweep, t + 1d);
        tracker.ObserveLidState(LidState.Closed);

        Assert.False(tracker.IsCalibrated);
        Assert.False(tracker.TryGetProgress(out _, out _));
    }

    [Fact]
    public void ImplausiblyLargeSweepsAreRejected()
    {
        LidAngleTracker tracker = new();

        double t = Settle(tracker, 0d, 0d);
        tracker.ObserveLidState(LidState.Open);
        t = Settle(tracker, 0d, t);

        tracker.Update(300d, t + 1d);
        tracker.ObserveLidState(LidState.Closed);

        Assert.False(tracker.IsCalibrated);
    }

    [Fact]
    public void TheOpenReferenceFollowsTheAngleTheUserActuallyWorksAt()
    {
        LidAngleTracker tracker = Calibrated(openPitch: 95d, closedPitch: 20d);

        // The user reopens the lid to a different angle and leaves it there.
        tracker.ObserveLidState(LidState.Open);
        Settle(tracker, 110d, 50d);

        Assert.True(tracker.IsCalibrated);
        Assert.Equal(-90d, tracker.SweepDegrees, 0);

        tracker.Update(110d, 60d);
        tracker.TryGetProgress(out float progress, out _);
        Assert.Equal(0f, progress, 3);
    }

    [Fact]
    public void TheOpenReferenceIsNotSampledWhileTheLidIsStillMoving()
    {
        // The switch fires as soon as the lid lifts, when it is nowhere near its
        // final angle, so sampling immediately would learn a wrong reference.
        LidAngleTracker tracker = new();

        double t = Settle(tracker, 95d, 0d);
        tracker.ObserveLidState(LidState.Open);
        t = Settle(tracker, 95d, t);
        t = Feed(tracker, 95d, 25d, 0.4d, t);
        tracker.ObserveLidState(LidState.Closed);

        double sweepAfterFirstCycle = tracker.SweepDegrees;

        // Now open again: the switch fires early, mid-travel.
        tracker.ObserveLidState(LidState.Open);
        tracker.Update(30d, t + 0.01d);

        // Still the old reference, because the lid has not settled.
        Assert.Equal(sweepAfterFirstCycle, tracker.SweepDegrees, 0);
    }

    [Fact]
    public void NonFiniteReadingsAreIgnored()
    {
        LidAngleTracker tracker = Calibrated(openPitch: 95d, closedPitch: 20d);

        tracker.Update(57.5d, 10d);
        tracker.Update(double.NaN, 11d);
        tracker.Update(double.PositiveInfinity, 12d);

        Assert.Equal(57.5d, tracker.LastPitchDegrees, 3);
        Assert.True(tracker.TryGetProgress(out float progress, out _));
        Assert.Equal(0.5f, progress, 2);
    }

    [Fact]
    public void CalibrationRoundTripsThroughConfiguration()
    {
        LidAngleTracker tracker = Calibrated(openPitch: 95d, closedPitch: 20d);

        LidAngleCalibration saved = tracker.Snapshot();
        Assert.True(saved.IsValid);

        LidAngleTracker restored = new(saved);
        Assert.True(restored.IsCalibrated);

        restored.Update(57.5d, 10d);
        Assert.True(restored.TryGetProgress(out float progress, out _));
        Assert.Equal(0.5f, progress, 2);
    }

    [Fact]
    public void ResetDiscardsTheLearnedRange()
    {
        LidAngleTracker tracker = Calibrated(openPitch: 95d, closedPitch: 20d);

        tracker.ResetCalibration();

        Assert.False(tracker.IsCalibrated);
        Assert.False(tracker.TryGetProgress(out _, out _));
    }

    [Fact]
    public void ConfigurationRejectsAPersistedCalibrationWithAnImplausibleSweep()
    {
        AnimationConfig config = new()
        {
            InclinometerCalibration = new LidAngleCalibration
            {
                IsValid = true,
                OpenPitchDegrees = 90d,
                ClosedPitchDegrees = 88d,
            },
        };

        config.Normalize();

        Assert.False(config.InclinometerCalibration.IsValid);
    }

    [Fact]
    public void InclinometerTrackingIsOnByDefaultButNeverStartsATransition()
    {
        AnimationConfig config = new();
        config.Normalize();

        Assert.True(config.UseInclinometerWhenAvailable);

        // Off by default: an inclinometer cannot tell a moving lid from a moving
        // laptop, so letting it start a transition risks false positives.
        Assert.False(config.AllowInclinometerEarlyClose);
    }

    private static LidAngleTracker Calibrated(double openPitch, double closedPitch)
    {
        LidAngleTracker tracker = new(new LidAngleCalibration
        {
            IsValid = true,
            OpenPitchDegrees = openPitch,
            ClosedPitchDegrees = closedPitch,
        });

        Assert.True(tracker.IsCalibrated);
        return tracker;
    }
}
