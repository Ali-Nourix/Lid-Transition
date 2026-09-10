using System;

namespace LidFlow.Core.Lid;

/// <summary>
/// Converts a physical hinge angle into panel progress.
/// <para>
/// This is the ideal way to drive the effect, and it is what the macOS
/// equivalent of this app does: instead of playing a fixed-length animation and
/// hoping it lines up with the user's hand, the image tracks the hinge, so the
/// panel on screen is always exactly where the real panel is - including when
/// the user stops half way, or changes their mind.
/// </para>
/// <para>
/// Windows only exposes a continuous angle on hardware with a hinge-angle
/// sensor, which in practice means dual-screen and foldable devices rather than
/// ordinary clamshell laptops. Where it is missing, the app falls back to a
/// timed animation. See docs/RESEARCH.md.
/// </para>
/// </summary>
public static class HingeAngleMapping
{
    /// <summary>Angle at or below which the panel counts as fully closed.</summary>
    public const float DefaultClosedAngleDegrees = 4f;

    /// <summary>Angle at or above which the effect is fully cleared.</summary>
    public const float DefaultOpenAngleDegrees = 55f;

    /// <summary>
    /// Maps a hinge angle to panel progress, where 0 is fully open and 1 is fully
    /// closed.
    /// <para>
    /// The mapping is linear in the angle, and deliberately so: the panel is
    /// rendered as a real rectangle rotated by that angle and projected, so all
    /// the trigonometry lives in the projection where it belongs. Progress here is
    /// just "how far through its travel the hinge is", which is exactly what the
    /// sensor reports. Curving it here as well would double-apply the geometry and
    /// make the image lag the physical panel.
    /// </para>
    /// </summary>
    public static float ProgressFromAngle(
        double angleDegrees,
        float closedAngleDegrees = DefaultClosedAngleDegrees,
        float openAngleDegrees = DefaultOpenAngleDegrees)
    {
        if (double.IsNaN(angleDegrees) || double.IsInfinity(angleDegrees))
        {
            return 0f;
        }

        // Guard against a config that inverts or collapses the range.
        if (openAngleDegrees <= closedAngleDegrees)
        {
            openAngleDegrees = closedAngleDegrees + 1f;
        }

        double span = openAngleDegrees - closedAngleDegrees;
        double open = (angleDegrees - closedAngleDegrees) / span;

        return (float)Math.Clamp(1d - open, 0d, 1d);
    }

    /// <summary>
    /// Normalizes a rate of change of panel progress into the 0..1 range the blur
    /// expects, so motion blur is driven by how fast the lid is actually moving.
    /// </summary>
    /// <param name="progressPerSecond">Absolute rate of change of panel progress.</param>
    /// <param name="referenceRate">
    /// Rate that saturates the blur. The default corresponds to closing the lid in
    /// roughly a third of a second, which is about as fast as a lid is ever shut.
    /// </param>
    public static float NormalizeVelocity(double progressPerSecond, float referenceRate = 3f)
    {
        if (double.IsNaN(progressPerSecond) || double.IsInfinity(progressPerSecond))
        {
            return 0f;
        }

        if (referenceRate <= 0f)
        {
            return 0f;
        }

        return (float)Math.Clamp(Math.Abs(progressPerSecond) / referenceRate, 0d, 1d);
    }

    /// <summary>
    /// Derives a binary lid state from an angle, so a device with an angle sensor
    /// but no lid switch still drives the state machine.
    /// <para>
    /// The two thresholds are deliberately different: closing is reported at a
    /// smaller angle than opening. Without that hysteresis, sensor noise around a
    /// single threshold would flip the state repeatedly and restart the transition
    /// on every jitter.
    /// </para>
    /// </summary>
    public static LidState StateFromAngle(
        double angleDegrees,
        LidState current,
        float closeThresholdDegrees = 5f,
        float openThresholdDegrees = 12f)
    {
        if (double.IsNaN(angleDegrees))
        {
            return current;
        }

        if (angleDegrees <= closeThresholdDegrees)
        {
            return LidState.Closed;
        }

        if (angleDegrees >= openThresholdDegrees)
        {
            return LidState.Open;
        }

        // Inside the hysteresis band, keep whatever we already reported.
        return current;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
}
