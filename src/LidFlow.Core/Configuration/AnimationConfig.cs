using System;
using LidFlow.Core.Animation;

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
    /// Normalized Y where the aperture converges, i.e. the projected hinge line.
    /// 0.5 would be a symmetric iris; below-centre values make the top edge travel further
    /// and faster than the bottom, which is what reads as a panel rotating about a hinge
    /// below the screen rather than a shrinking rectangle.
    /// </summary>
    public float HingeBias { get; set; } = 0.62f;

    /// <summary>How far the side edges close in, as a fraction of panel width (total, both sides).</summary>
    public float EdgeExpansion { get; set; } = 0.13f;

    /// <summary>Normalized progress before the side edges start moving. Keeps the early
    /// part of the motion dominated by the top/bottom edges.</summary>
    public float SideDelay { get; set; } = 0.18f;

    /// <summary>
    /// Drives the trapezoidal narrowing of the aperture toward the top (keystone) — the
    /// geometric signature of a surface tilting away from the viewer.
    /// </summary>
    public float PerspectiveStrength { get; set; } = 0.85f;

    /// <summary>Antialias/soft width of the panel edge itself.</summary>
    public float EdgeSoftness { get; set; } = 2.6f;

    /// <summary>Corner rounding of the aperture at full close. Grows with progress.</summary>
    public float CornerRadiusPx { get; set; } = 30f;

    /// <summary>Distance inside the edge over which luminance falls off toward black.</summary>
    public float ShadowExtentPx { get; set; } = 155f;

    /// <summary>Depth of that luminance falloff. 0 disables the gradient, leaving a hard edge.</summary>
    public float ShadowStrength { get; set; } = 0.88f;

    /// <summary>Peak blur radius reached immediately behind the moving edge.</summary>
    public float MaxBlur { get; set; } = 34f;

    /// <summary>Distance inside the edge over which blur ramps from zero to <see cref="MaxBlur"/>.</summary>
    public float BlurRadius { get; set; } = 175f;

    /// <summary>
    /// How much the blur is gated by the edge's instantaneous speed (0..1). At 1 the blur
    /// exists only while the edge is actually moving, which is what makes it read as
    /// optical motion rather than a defocus fade.
    /// </summary>
    public float BlurVelocityInfluence { get; set; } = 0.72f;

    /// <summary>Opacity of the occluded region. 1.0 is fully black; lower values are for tuning only.</summary>
    public float BlackOpacity { get; set; } = 1.0f;

    /// <summary>Strength of the faint highlight that sweeps just inside the closing edge.</summary>
    public float GlareStrength { get; set; } = 0.055f;

    /// <summary>Distance inside the edge at which the highlight peaks.</summary>
    public float GlareOffsetPx { get; set; } = 26f;

    /// <summary>Width of the highlight band.</summary>
    public float GlareWidthPx { get; set; } = 62f;

    /// <summary>
    /// Edge-localized coordinate pull toward the hinge. Small on purpose: the centre of the
    /// snapshot must stay pixel-stable, so this is shaped to vanish away from the edges.
    /// </summary>
    public float WarpStrength { get; set; } = 0.030f;

    /// <summary>Scales the subtle optical (barrel) term applied near the moving edge.</summary>
    public float DistortionStrength { get; set; } = 0.5f;

    /// <summary>Contrast/saturation loss far from the hinge, mimicking off-axis LCD viewing.</summary>
    public float OffAxisWash { get; set; } = 0.35f;

    /// <summary>Overall luminance loss at full close, before occlusion.</summary>
    public float GlobalDim { get; set; } = 0.10f;

    /// <summary>
    /// Ambient light the panel bezel picks up, just outside the aperture.
    /// <para>
    /// Small but load-bearing: a mathematically perfect black boundary reads as a
    /// hole cut in the image, whereas a faint brightening against the glass reads
    /// as an object occluding it. Set to 0 for a pure-black edge.
    /// </para>
    /// </summary>
    public float BezelAmbient { get; set; } = 0.010f;

    /// <summary>Distance outside the edge over which the bezel ambient decays.</summary>
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

        HingeBias = Clamp(HingeBias, 0.05f, 0.95f);

        HingeClosedAngleDeg = Clamp(HingeClosedAngleDeg, 0f, 170f);
        HingeOpenAngleDeg = Clamp(HingeOpenAngleDeg, 1f, 180f);

        // An inverted or collapsed angle range would make the mapping meaningless,
        // so keep at least a degree of travel between the two.
        if (HingeOpenAngleDeg <= HingeClosedAngleDeg)
        {
            HingeOpenAngleDeg = Clamp(HingeClosedAngleDeg + 1f, 1f, 180f);
        }

        HingeVelocityReference = Clamp(HingeVelocityReference, 0.1f, 50f);
        EdgeExpansion = Clamp(EdgeExpansion, 0f, 0.9f);
        SideDelay = Clamp(SideDelay, 0f, 0.9f);
        PerspectiveStrength = Clamp(PerspectiveStrength, 0f, 3f);

        EdgeSoftness = Clamp(EdgeSoftness, 0.5f, 64f);
        CornerRadiusPx = Clamp(CornerRadiusPx, 0f, 400f);

        ShadowExtentPx = Clamp(ShadowExtentPx, 0f, 900f);
        ShadowStrength = Clamp(ShadowStrength, 0f, 1f);

        MaxBlur = Clamp(MaxBlur, 0f, 200f);
        BlurRadius = Clamp(BlurRadius, 1f, 1200f);
        BlurVelocityInfluence = Clamp(BlurVelocityInfluence, 0f, 1f);

        BlackOpacity = Clamp(BlackOpacity, 0f, 1f);

        GlareStrength = Clamp(GlareStrength, 0f, 1f);
        GlareOffsetPx = Clamp(GlareOffsetPx, 0f, 600f);
        GlareWidthPx = Clamp(GlareWidthPx, 1f, 600f);

        WarpStrength = Clamp(WarpStrength, 0f, 0.5f);
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
        HingeClosedAngleDeg = HingeClosedAngleDeg,
        HingeOpenAngleDeg = HingeOpenAngleDeg,
        HingeVelocityReference = HingeVelocityReference,
        CloseDurationMs = CloseDurationMs,
        OpenDurationMs = OpenDurationMs,
        HingeBias = HingeBias,
        EdgeExpansion = EdgeExpansion,
        SideDelay = SideDelay,
        PerspectiveStrength = PerspectiveStrength,
        EdgeSoftness = EdgeSoftness,
        CornerRadiusPx = CornerRadiusPx,
        ShadowExtentPx = ShadowExtentPx,
        ShadowStrength = ShadowStrength,
        MaxBlur = MaxBlur,
        BlurRadius = BlurRadius,
        BlurVelocityInfluence = BlurVelocityInfluence,
        BlackOpacity = BlackOpacity,
        GlareStrength = GlareStrength,
        GlareOffsetPx = GlareOffsetPx,
        GlareWidthPx = GlareWidthPx,
        WarpStrength = WarpStrength,
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
