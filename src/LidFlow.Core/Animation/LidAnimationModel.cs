using System;
using LidFlow.Core.Configuration;

namespace LidFlow.Core.Animation;

/// <summary>
/// Turns one normalized progress value into the full set of per-frame shader
/// parameters.
/// <para>
/// This is the whole visual design expressed as maths, and it is deliberately
/// free of any graphics API so it can be reasoned about and unit-tested directly.
/// </para>
/// <para><b>The model.</b> The panel is a rectangle hinged along its bottom edge
/// with the desktop image painted on it, rotating away from the viewer. Nothing
/// slides the content around independently; what changes is where that rectangle
/// projects to on screen. Three things then fall out rather than being layered
/// on:
/// </para>
/// <list type="number">
/// <item>Black grows from the top and the top corners, because that is where the
/// foreshortened, keystoned panel stops covering the display.</item>
/// <item>Blur is strongest at the top and vanishes at the hinge, because a
/// point's speed is proportional to its distance from the hinge. The hinge edge
/// barely moves, so it stays sharp.</item>
/// <item>The top loses contrast first, because it is the most off-axis.</item>
/// </list>
/// <para>
/// Both endpoints are exact by construction: at zero rotation the projection is
/// the identity and every effect term is zero, so the first frame of a transition
/// is pixel-identical to the captured desktop; at full rotation the panel is
/// edge-on and covers nothing, so the last frame is pure black.
/// </para>
/// </summary>
public sealed class LidAnimationModel
{
    // Progress window over which effect terms ramp in. Chosen so the first frame
    // is exact (see class remarks) without a visible "kick".
    private const float EffectOnset = 0.085f;
    private const float BlurTailStart = 0.92f;

    private readonly AnimationConfig _config;
    private readonly EasingCurve _curve;
    private readonly float _peakVelocity;

    public LidAnimationModel(AnimationConfig config, TransitionKind kind)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        Kind = kind;

        EasingSettings settings = kind == TransitionKind.Close ? config.CloseEasing : config.OpenEasing;
        _curve = (settings ?? EasingSettings.ForClose()).ToCurve();

        // Peak velocity is curve-dependent (a stiff ease-out starts far above 1),
        // so normalize against it once here instead of guessing a constant.
        // Sampled rather than differentiated analytically, so it also works for
        // the spring presets.
        _peakVelocity = ComputePeakVelocity(_curve);

        DurationMs = kind == TransitionKind.Close ? config.CloseDurationMs : config.OpenDurationMs;
    }

    public TransitionKind Kind { get; }

    public int DurationMs { get; }

    public EasingCurve Curve => _curve;

    /// <summary>Blur tap count per quality tier. Odd counts keep the kernel centred.</summary>
    public int BlurTaps => _config.Quality switch
    {
        AnimationQuality.Performance => 5,
        AnimationQuality.High => 13,
        _ => 9,
    };

    /// <summary>
    /// Evaluates the transition at linear time <paramref name="linearT"/> in
    /// [0,1], where 0 is the start of this transition and 1 is its end -
    /// regardless of direction.
    /// </summary>
    public LidFrameParameters Evaluate(float linearT)
    {
        linearT = Easing.Clamp01(linearT);

        float eased = Easing.Clamp01(_curve.Evaluate(linearT));

        // p is panel-space progress: 0 = open, 1 = closed. Opening runs it
        // backwards, but through its own curve, so opening is not a mirrored
        // replay of closing.
        float p = Kind == TransitionKind.Close ? eased : 1f - eased;

        float velocity = _peakVelocity > 0f
            ? Easing.Clamp01(_curve.EvaluateVelocity(linearT) / _peakVelocity)
            : 0f;

        return Build(p, velocity);
    }

    /// <summary>
    /// Builds the frame parameters from an externally-measured panel position and
    /// speed, bypassing the easing curve entirely.
    /// <para>
    /// This is the path used when a hinge-angle sensor is available: the rotation
    /// comes from the physical hinge and the blur is driven by how fast the lid is
    /// genuinely moving, rather than by where a timed curve thinks it should be.
    /// Everything downstream is identical, so the two input modes cannot drift
    /// apart visually.
    /// </para>
    /// </summary>
    public LidFrameParameters EvaluateAtProgress(float panelProgress, float normalizedVelocity) =>
        Build(Easing.Clamp01(panelProgress), Easing.Clamp01(normalizedVelocity));

    /// <summary>
    /// Inverse of <see cref="Evaluate"/> for the progress channel: finds the
    /// linear time at which this transition's panel progress equals
    /// <paramref name="panelProgress"/>.
    /// <para>
    /// Used when a transition is reversed mid-flight. The opening and closing
    /// curves are different by design, so resuming an opposite-direction animation
    /// at the same linear time would visibly jump the panel; matching panel
    /// <i>position</i> instead makes the reversal continuous. Bisection rather
    /// than an analytic inverse, so it works for every easing preset.
    /// </para>
    /// </summary>
    public float FindLinearTimeForPanelProgress(float panelProgress)
    {
        panelProgress = Easing.Clamp01(panelProgress);

        bool rising = Kind == TransitionKind.Close;

        float low = 0f;
        float high = 1f;

        for (int i = 0; i < 24; i++)
        {
            float mid = (low + high) * 0.5f;
            float value = Evaluate(mid).Progress;

            if (rising == (value < panelProgress))
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return (low + high) * 0.5f;
    }

    /// <summary>The frame shown while the panel is held closed: pure black, nothing else.</summary>
    public LidFrameParameters ClosedFrame() => Build(1f, 0f);

    private LidFrameParameters Build(float p, float velocity)
    {
        // Rotation is linear in progress, and the projection supplies the
        // geometry. Putting the trigonometry in the projection rather than in the
        // progress mapping is what makes a hinge-angle sensor map straight onto
        // this: the sensor reports an angle, and an angle is what this wants.
        float angle = _config.PanelMaxAngleDeg * p;
        float radians = angle * MathF.PI / 180f;

        // --- effect ramps ------------------------------------------------------
        float onset = Smoothstep(0f, EffectOnset, p);

        // Blur fades out at the very end because by then almost nothing is
        // visible to smear.
        float blurGate = onset * (1f - Smoothstep(BlurTailStart, 1f, p));

        float blurScale = _config.EnableBlur
            ? blurGate * Lerp(1f, velocity, _config.BlurVelocityInfluence)
            : 0f;

        float shadowRamp = _config.EnableShadow ? onset : 0f;

        // The glare is a reflection sweeping across the panel, so it is strongest
        // while the panel is actually turning.
        float glareEnvelope = _config.EnableGlare ? blurGate * Lerp(0.35f, 1f, velocity) : 0f;

        float distortion = _config.EnableDistortion
            ? _config.DistortionStrength * onset * Lerp(0.4f, 1f, velocity)
            : 0f;

        return new LidFrameParameters
        {
            Progress = p,
            EdgeVelocity = velocity,

            AngleDegrees = angle,
            CosTheta = MathF.Cos(radians),
            SinTheta = MathF.Sin(radians),
            Perspective = _config.PerspectiveStrength,

            // The real hinge sits a little below the visible panel, behind the
            // bezel, so the bottom edge foreshortens slightly too instead of being
            // pinned dead still.
            PivotV = 1f + _config.HingeOffset,

            EdgeSoftnessPx = _config.EdgeSoftness,

            BlurRadiusPx = _config.MaxBlur * blurScale,
            BlurFalloff = _config.BlurFalloff,

            BlackOpacity = _config.BlackOpacity,

            ShadowStrength = _config.ShadowStrength * shadowRamp,
            ShadowFalloff = _config.ShadowFalloff,

            OffAxisWash = _config.OffAxisWash * onset,
            Luminance = 1f - (_config.GlobalDim * onset),

            GlareStrength = _config.GlareStrength * glareEnvelope,
            GlareWidth = _config.GlareWidth,

            DistortionStrength = distortion,

            BezelAmbient = _config.EnableShadow ? _config.BezelAmbient * onset : 0f,
            BezelFalloffPx = _config.BezelFalloffPx,

            BlurTaps = BlurTaps,
            DitherEnabled = _config.EnableDither,
        };
    }

    private static float ComputePeakVelocity(EasingCurve curve)
    {
        const int Samples = 128;
        float peak = 0f;

        for (int i = 0; i <= Samples; i++)
        {
            float velocity = curve.EvaluateVelocity(i / (float)Samples);
            if (velocity > peak)
            {
                peak = velocity;
            }
        }

        return peak;
    }

    internal static float Lerp(float a, float b, float t) => a + ((b - a) * t);

    /// <summary>Hermite smoothstep, matching HLSL's <c>smoothstep</c> so the
    /// CPU-side model and the shader agree exactly.</summary>
    internal static float Smoothstep(float edge0, float edge1, float x)
    {
        if (edge1 <= edge0)
        {
            return x < edge0 ? 0f : 1f;
        }

        float t = Easing.Clamp01((x - edge0) / (edge1 - edge0));
        return t * t * (3f - (2f * t));
    }
}
