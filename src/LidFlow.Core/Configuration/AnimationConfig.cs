using System;
using LidFlow.Core.Animation;
using LidFlow.Core.Lid;

namespace LidFlow.Core.Configuration;

/// <summary>Rendering quality tier. Trades shader tap counts for fidelity.</summary>
public enum AnimationQuality
{
    /// <summary>Fewest taps. For weak integrated GPUs or very high resolutions.</summary>
    Performance = 0,

    /// <summary>Default. Visually indistinguishable from High on most panels.</summary>
    Balanced = 1,

    /// <summary>Widest blur kernel and highest tap count.</summary>
    High = 2,
}

/// <summary>
/// Every tunable value for the transition, in one place. No magic numbers live in the
/// renderer or the shader — the shader receives a constant buffer derived entirely from
/// this configuration and a single normalized progress value.
/// </summary>
/// <remarks>
/// Units:
/// <list type="bullet">
/// <item>Values named <c>*Px</c> are in <b>reference pixels at a 1080-pixel-tall panel</b>.
/// The renderer scales them by <c>actualHeight / 1080</c> so the effect looks identical at
/// 1080p, 1440p, 4K and at every DPI scale.</item>
/// <item>Everything else is a normalized 0..1 fraction.</item>
/// </list>
/// </remarks>
public sealed class AnimationConfig
{
    /// <summary>The panel height the <c>*Px</c> values are authored against.</summary>
    public const float ReferenceHeight = 1080f;

    /// <summary>Master switch. When false the app stays resident but never draws.</summary>
    public bool EnableAnimation { get; set; } = true;

    /// <summary>
    /// Track the physical hinge angle when the hardware reports one, instead of
    /// playing a fixed-length animation.
    /// <para>
    /// This is strictly better where it is available: the image follows the lid
    /// exactly, so stopping half way or reversing looks right for free. Most
    /// clamshell laptops have no hinge-angle sensor and fall back to the timed
    /// path automatically.
    /// </para>
    /// </summary>
    public bool UseHingeAngleWhenAvailable { get; set; } = true;

    /// <summary>
    /// Fall back to a lid-mounted inclinometer when there is no hinge-angle sensor.
    /// <para>
    /// The lid switch only reports 0 or 1, so on its own it can say that the lid
    /// moved but never how far. An inclinometer is in the display on any machine
    /// with auto-rotate or a tablet mode, so its pitch moves with the lid - and the
    /// two switch events are enough to calibrate the range. It supplies position
    /// for a transition the switch has already started; it never starts one, so a
    /// laptop being picked up cannot trigger the effect.
    /// </para>
    /// </summary>
    public bool UseInclinometerWhenAvailable { get; set; } = true;

    /// <summary>
    /// Also let the inclinometer <i>start</i> a closing transition, before the lid
    /// switch fires.
    /// <para>
    /// Off by default and best-effort. The switch fires near the end of the lid's
    /// travel, so waiting for it leaves very little of the close visible; starting
    /// from detected motion gives the animation real runway. The cost is that an
    /// inclinometer cannot distinguish a moving lid from a moving laptop, so
    /// tilting the machine can trigger it.
    /// </para>
    /// </summary>
    public bool AllowInclinometerEarlyClose { get; set; }

    /// <summary>
    /// Learned inclinometer range, persisted so the first close after a restart
    /// does not have to fall back to the timed path.
    /// </summary>
    public LidAngleCalibration InclinometerCalibration { get; set; } = new();

    /// <summary>Hinge angle at or below which the panel counts as fully closed.</summary>
    public float HingeClosedAngleDeg { get; set; } = 4f;

    /// <summary>Hinge angle at or above which the effect is fully cleared.</summary>
    public float HingeOpenAngleDeg { get; set; } = 55f;

    /// <summary>
    /// Rate of panel progress, per second, that saturates the motion blur in
    /// hinge-tracking mode. The default is about a third of a second for a full
    /// close, which is roughly as fast as a lid is ever shut.
    /// </summary>
    public float HingeVelocityReference { get; set; } = 3f;

    /// <summary>Closing duration. The lid switch fires while the panel is still partly
    /// visible, so this has to be short; see docs/RESEARCH.md on the visibility window.</summary>
    public int CloseDurationMs { get; set; } = 340;

    /// <summary>Opening duration. Deliberately shorter than closing — opening should feel
    /// more energetic.</summary>
    public int OpenDurationMs { get; set; } = 285;

    /// <summary>
    /// Total rotation the panel sweeps through, in degrees, from flat against the
    /// display to fully closed.
    /// <para>
    /// At 90 degrees the panel ends edge-on, covering nothing, which is what makes
    /// the final frame pure black by construction rather than by fading.
    /// </para>
    /// </summary>
    public float PanelMaxAngleDeg { get; set; } = 90f;

    /// <summary>
    /// Reciprocal viewing distance, in panel heights, used by the projection.
    /// <para>
    /// This is the strength of the keystone - how much more the top of the panel
    /// narrows than the bottom - and it is the main cue that the panel is rotating
    /// rather than scaling. 0 gives an orthographic squash with no convergence.
    /// </para>
    /// </summary>
    public float PerspectiveStrength { get; set; } = 0.85f;

    /// <summary>
    /// How far below the visible panel the hinge actually sits, in panel heights.
    /// <para>
    /// A real hinge is behind the bottom bezel rather than on the last row of
    /// pixels, so the bottom edge foreshortens slightly too instead of being
    /// pinned dead still.
    /// </para>
    /// </summary>
    public float HingeOffset { get; set; } = 0.05f;

    /// <summary>Antialias/soft width of the panel edge itself.</summary>
    public float EdgeSoftness { get; set; } = 2.6f;

    /// <summary>
    /// Peak blur radius, reached at the panel's far (top) edge.
    /// <para>
    /// Blur is not uniform and not edge-localized: it scales with distance from
    /// the hinge, because that is proportional to how fast that row is actually
    /// moving. The hinge edge stays sharp.
    /// </para>
    /// </summary>
    public float MaxBlur { get; set; } = 56f;

    /// <summary>
    /// Exponent shaping the blur gradient along the panel. 1 is linear in distance
    /// from the hinge; above 1 concentrates the blur toward the far edge.
    /// </summary>
    public float BlurFalloff { get; set; } = 1.15f;

    /// <summary>
    /// How much the blur is gated by rotation speed (0..1). At 1 the blur exists
    /// only while the panel is actually moving, which is what makes it read as
    /// motion rather than defocus.
    /// </summary>
    public float BlurVelocityInfluence { get; set; } = 0.72f;

    /// <summary>Opacity of the region the panel no longer covers. 1.0 is fully black.</summary>
    public float BlackOpacity { get; set; } = 1.0f;

    /// <summary>Depth of the luminance falloff toward the panel's far edge.</summary>
    public float ShadowStrength { get; set; } = 0.55f;

    /// <summary>Exponent shaping that falloff along the panel.</summary>
    public float ShadowFalloff { get; set; } = 1.6f;

    /// <summary>Strength of the highlight running along the panel's leading edge.</summary>
    public float GlareStrength { get; set; } = 0.055f;

    /// <summary>Width of that highlight, as a fraction of the panel's length.</summary>
    public float GlareWidth { get; set; } = 0.10f;

    /// <summary>Scales a subtle optical (glass) term near the far edge.</summary>
    public float DistortionStrength { get; set; } = 0.5f;

    /// <summary>Contrast and saturation loss toward the far edge, from off-axis viewing.</summary>
    public float OffAxisWash { get; set; } = 0.35f;

    /// <summary>Overall luminance loss at full close, before occlusion.</summary>
    public float GlobalDim { get; set; } = 0.10f;

    /// <summary>
    /// Ambient light the panel bezel picks up, just outside the panel edge.
    /// <para>
    /// Small but load-bearing: a mathematically perfect black boundary reads as a
    /// hole cut in the image, whereas a faint brightening against the glass reads
    /// as an object occluding it. Set to 0 for a pure-black edge.
    /// </para>
    /// </summary>
    public float BezelAmbient { get; set; } = 0.010f;

    /// <summary>Distance outside the panel edge over which the bezel ambient decays.</summary>
    public float BezelFalloffPx { get; set; } = 22f;

    public bool EnableBlur { get; set; } = true;

    public bool EnableShadow { get; set; } = true;

    public bool EnableDistortion { get; set; } = true;

    public bool EnableGlare { get; set; } = true;

    /// <summary>Ordered dithering before quantization. Prevents banding in the wide dark
    /// gradients on 8-bit panels. Costs nothing; leave on.</summary>
    public bool EnableDither { get; set; } = true;

    public AnimationQuality Quality { get; set; } = AnimationQuality.Balanced;

    public EasingSettings CloseEasing { get; set; } = EasingSettings.ForClose();

    public EasingSettings OpenEasing { get; set; } = EasingSettings.ForOpen();

    /// <summary>
    /// Clamps every field into a sane range. Called after loading configuration so a
    /// hand-edited config.json can never produce a broken or seizure-inducing effect.
    /// </summary>
    public void Normalize()
    {
        CloseDurationMs = Clamp(CloseDurationMs, 80, 2000);
        OpenDurationMs = Clamp(OpenDurationMs, 80, 2000);

        PanelMaxAngleDeg = Clamp(PanelMaxAngleDeg, 5f, 179f);
        PerspectiveStrength = Clamp(PerspectiveStrength, 0f, 4f);
        HingeOffset = Clamp(HingeOffset, 0f, 1f);

        HingeClosedAngleDeg = Clamp(HingeClosedAngleDeg, 0f, 170f);
        HingeOpenAngleDeg = Clamp(HingeOpenAngleDeg, 1f, 180f);

        // An inverted or collapsed angle range would make the mapping
        // meaningless, so keep at least a degree of travel between the two.
        if (HingeOpenAngleDeg <= HingeClosedAngleDeg)
        {
            HingeOpenAngleDeg = Clamp(HingeClosedAngleDeg + 1f, 1f, 180f);
        }

        HingeVelocityReference = Clamp(HingeVelocityReference, 0.1f, 50f);

        InclinometerCalibration ??= new LidAngleCalibration();

        // A persisted calibration whose range is implausible is worse than none:
        // it would drive the animation from a mapping that never applied.
        double sweep = Math.Abs(InclinometerCalibration.ClosedPitchDegrees - InclinometerCalibration.OpenPitchDegrees);
        if (InclinometerCalibration.IsValid &&
            (sweep < LidAngleTracker.MinimumPlausibleSweepDegrees || sweep > LidAngleTracker.MaximumPlausibleSweepDegrees))
        {
            InclinometerCalibration.IsValid = false;
        }

        EdgeSoftness = Clamp(EdgeSoftness, 0.5f, 64f);

        MaxBlur = Clamp(MaxBlur, 0f, 400f);
        BlurFalloff = Clamp(BlurFalloff, 0.05f, 8f);
        BlurVelocityInfluence = Clamp(BlurVelocityInfluence, 0f, 1f);

        BlackOpacity = Clamp(BlackOpacity, 0f, 1f);

        ShadowStrength = Clamp(ShadowStrength, 0f, 1f);
        ShadowFalloff = Clamp(ShadowFalloff, 0.05f, 8f);

        GlareStrength = Clamp(GlareStrength, 0f, 1f);
        GlareWidth = Clamp(GlareWidth, 0.001f, 1f);

        DistortionStrength = Clamp(DistortionStrength, 0f, 4f);
        OffAxisWash = Clamp(OffAxisWash, 0f, 1f);
        GlobalDim = Clamp(GlobalDim, 0f, 1f);
        BezelAmbient = Clamp(BezelAmbient, 0f, 0.25f);
        BezelFalloffPx = Clamp(BezelFalloffPx, 1f, 400f);

        if (!Enum.IsDefined(typeof(AnimationQuality), Quality))
        {
            Quality = AnimationQuality.Balanced;
        }

        CloseEasing ??= EasingSettings.ForClose();
        OpenEasing ??= EasingSettings.ForOpen();
        CloseEasing.Normalize();
        OpenEasing.Normalize();
    }

    public AnimationConfig Clone() => new()
    {
        EnableAnimation = EnableAnimation,
        UseHingeAngleWhenAvailable = UseHingeAngleWhenAvailable,
        UseInclinometerWhenAvailable = UseInclinometerWhenAvailable,
        AllowInclinometerEarlyClose = AllowInclinometerEarlyClose,
        InclinometerCalibration = (InclinometerCalibration ?? new LidAngleCalibration()).Clone(),
        HingeClosedAngleDeg = HingeClosedAngleDeg,
        HingeOpenAngleDeg = HingeOpenAngleDeg,
        HingeVelocityReference = HingeVelocityReference,
        CloseDurationMs = CloseDurationMs,
        OpenDurationMs = OpenDurationMs,
        PanelMaxAngleDeg = PanelMaxAngleDeg,
        PerspectiveStrength = PerspectiveStrength,
        HingeOffset = HingeOffset,
        EdgeSoftness = EdgeSoftness,
        MaxBlur = MaxBlur,
        BlurFalloff = BlurFalloff,
        BlurVelocityInfluence = BlurVelocityInfluence,
        BlackOpacity = BlackOpacity,
        ShadowStrength = ShadowStrength,
        ShadowFalloff = ShadowFalloff,
        GlareStrength = GlareStrength,
        GlareWidth = GlareWidth,
        DistortionStrength = DistortionStrength,
        OffAxisWash = OffAxisWash,
        GlobalDim = GlobalDim,
        BezelAmbient = BezelAmbient,
        BezelFalloffPx = BezelFalloffPx,
        EnableBlur = EnableBlur,
        EnableShadow = EnableShadow,
        EnableDistortion = EnableDistortion,
        EnableGlare = EnableGlare,
        EnableDither = EnableDither,
        Quality = Quality,
        CloseEasing = CloseEasing.Clone(),
        OpenEasing = OpenEasing.Clone(),
    };

    internal static int Clamp(int value, int min, int max) => value < min ? min : (value > max ? max : value);

    internal static float Clamp(float value, float min, float max)
    {
        if (float.IsNaN(value))
        {
            return min;
        }

        return value < min ? min : (value > max ? max : value);
    }
}

/// <summary>Serializable easing selection.</summary>
public sealed class EasingSettings
{
    public EasingPreset Preset { get; set; } = EasingPreset.LidClose;

    /// <summary>Bezier control points, used when <see cref="Preset"/> is
    /// <see cref="EasingPreset.CustomBezier"/>.</summary>
    public float[] Bezier { get; set; } = new[] { 0.24f, 0.92f, 0.20f, 1.0f };

    public float SpringDamping { get; set; } = 1.0f;

    public float SpringFrequency { get; set; } = 3.2f;

    public static EasingSettings ForClose() => new()
    {
        Preset = EasingPreset.LidClose,
        Bezier = new[]
        {
            CubicBezierEasing.LidClose.X1,
            CubicBezierEasing.LidClose.Y1,
            CubicBezierEasing.LidClose.X2,
            CubicBezierEasing.LidClose.Y2,
        },
    };

    public static EasingSettings ForOpen() => new()
    {
        Preset = EasingPreset.LidOpen,
        Bezier = new[]
        {
            CubicBezierEasing.LidOpen.X1,
            CubicBezierEasing.LidOpen.Y1,
            CubicBezierEasing.LidOpen.X2,
            CubicBezierEasing.LidOpen.Y2,
        },
    };

    public void Normalize()
    {
        if (!Enum.IsDefined(typeof(EasingPreset), Preset))
        {
            Preset = EasingPreset.EaseOutCubic;
        }

        if (Bezier is null || Bezier.Length != 4)
        {
            Bezier = new[] { 0.24f, 0.92f, 0.20f, 1.0f };
        }

        SpringDamping = AnimationConfig.Clamp(SpringDamping, 0.1f, 4f);
        SpringFrequency = AnimationConfig.Clamp(SpringFrequency, 0.25f, 12f);
    }

    public EasingCurve ToCurve()
    {
        float[] b = Bezier is { Length: 4 } ? Bezier : new[] { 0.24f, 0.92f, 0.20f, 1.0f };
        return new EasingCurve(Preset, new CubicBezierEasing(b[0], b[1], b[2], b[3]), SpringDamping, SpringFrequency);
    }

    public EasingSettings Clone() => new()
    {
        Preset = Preset,
        Bezier = (float[])(Bezier ?? Array.Empty<float>()).Clone(),
        SpringDamping = SpringDamping,
        SpringFrequency = SpringFrequency,
    };
}
