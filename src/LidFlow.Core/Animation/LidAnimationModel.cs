using System;
using LidFlow.Core.Configuration;

namespace LidFlow.Core.Animation;

/// <summary>
/// Turns one normalized progress value into the full set of per-frame shader parameters.
/// <para>
/// This is the whole visual design expressed as maths, and it is deliberately free of any
/// graphics API so it can be reasoned about and unit-tested directly.
/// </para>
/// <para><b>The geometry.</b> The snapshot is never scaled or faded as a whole. Instead an
/// aperture closes over it, and the aperture is built to read as a physical panel rotating
/// about a hinge below the screen rather than as a shrinking rectangle:
/// </para>
/// <list type="number">
/// <item>The aperture converges on a hinge line at <see cref="AnimationConfig.HingeBias"/>
/// rather than on the centre, so the top edge travels further — and therefore faster —
/// than the bottom edge.</item>
/// <item>The sides close later and less than the top and bottom, and they close
/// <i>more at the top than at the hinge</i>, making the aperture a trapezoid. That keystone
/// is the same projection you get from a real surface tilting away from you.</item>
/// <item>Blur is gated by the edge's instantaneous speed, so it appears because something
/// is moving, not because time is passing.</item>
/// </list>
/// <para>
/// Both endpoints are exact by construction: at full open the aperture edges sit
/// <i>outside</i> the panel and every effect term is zero, so the first frame of a
/// transition is pixel-identical to the captured desktop. At full close the aperture has
/// zero height, so the last frame is pure black.
/// </para>
/// </summary>
public sealed class LidAnimationModel
{
    // Progress windows over which effect terms ramp in and out. Chosen so that the first
    // and last frames are exact (see class remarks) without a visible "kick".
    private const float EffectOnset = 0.085f;
    private const float BlurTailStart = 0.88f;
    private const float CornerOnset = 0.35f;

    private readonly AnimationConfig _config;
    private readonly EasingCurve _curve;
    private readonly float _peakVelocity;
    private readonly float _overshoot;

    public LidAnimationModel(AnimationConfig config, TransitionKind kind)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        Kind = kind;

        EasingSettings settings = kind == TransitionKind.Close ? config.CloseEasing : config.OpenEasing;
        _curve = (settings ?? EasingSettings.ForClose()).ToCurve();

        // Peak velocity is curve-dependent (a stiff ease-out starts far above 1), so
        // normalize against it once here instead of guessing a constant. Sampled, not
        // differentiated analytically, so it works for spring presets too.
        _peakVelocity = ComputePeakVelocity(_curve);

        // At full open the edges must sit clear of the panel, otherwise the edge
        // antialias band would darken a thin rim on the very first frame.
        _overshoot = ((config.EdgeSoftness * 2f) + 1f) / AnimationConfig.ReferenceHeight;

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
    /// Evaluates the transition at linear time <paramref name="linearT"/> in [0,1], where 0
    /// is the start of this transition and 1 is its end — regardless of direction.
    /// </summary>
    public LidFrameParameters Evaluate(float linearT)
    {
        linearT = Easing.Clamp01(linearT);

        float eased = Easing.Clamp01(_curve.Evaluate(linearT));

        // p is panel-space progress: 0 = open, 1 = closed. Opening runs it backwards, but
        // through its own curve, so opening is not a mirrored replay of closing.
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
    /// This is the path used when a hinge-angle sensor is available: the panel
    /// position comes from the physical hinge and the blur is driven by how fast
    /// the lid is genuinely moving, rather than by where a timed curve thinks it
    /// should be. Everything downstream - geometry, falloff, glare, dithering - is
    /// identical, so the two input modes cannot drift apart visually.
    /// </para>
    /// </summary>
    public LidFrameParameters EvaluateAtProgress(float panelProgress, float normalizedVelocity) =>
        Build(Easing.Clamp01(panelProgress), Easing.Clamp01(normalizedVelocity));

    private LidFrameParameters Build(float p, float velocity)
    {
        float hinge = _config.HingeBias;

        // --- Aperture geometry -------------------------------------------------------
        float apertureTop = Lerp(-_overshoot, hinge, p);
        float apertureBottom = Lerp(1f + _overshoot, hinge, p);

        // Sides start later and close less than the top/bottom.
        float sideT = _config.SideDelay >= 1f ? 0f : Easing.Clamp01((p - _config.SideDelay) / (1f - _config.SideDelay));
        float sideEased = CubicBezierEasing.Smooth.Evaluate(sideT);
        float sideInset = _config.EdgeExpansion * 0.5f * sideEased;
        float keystone = sideInset * _config.PerspectiveStrength * 0.55f;

        // --- Effect ramps ------------------------------------------------------------
        float onset = Smoothstep(0f, EffectOnset, p);
        float blurGate = onset * (1f - Smoothstep(BlurTailStart, 1f, p));

        float blurScale = _config.EnableBlur
            ? blurGate * Lerp(1f, velocity, _config.BlurVelocityInfluence)
            : 0f;

        float shadowRamp = _config.EnableShadow ? onset : 0f;

        // The glare peaks mid-motion: it is a reflection sweeping across the panel, so it
        // is strongest while the panel is actually turning.
        float glareEnvelope = _config.EnableGlare ? blurGate * Lerp(0.35f, 1f, velocity) : 0f;

        float distortion = _config.EnableDistortion
            ? _config.DistortionStrength * onset * Lerp(0.4f, 1f, velocity)
            : 0f;

        return new LidFrameParameters
        {
            Progress = p,
            EdgeVelocity = velocity,

            ApertureTop = apertureTop,
            ApertureBottom = apertureBottom,
            SideInset = sideInset,
            Keystone = keystone,
            HingeY = hinge,

            CornerRadiusPx = _config.CornerRadiusPx * Smoothstep(0f, CornerOnset, p),
            EdgeSoftnessPx = _config.EdgeSoftness,

            ShadowExtentPx = _config.ShadowExtentPx * shadowRamp,
            ShadowStrength = _config.ShadowStrength * shadowRamp,

            BlurRadiusPx = _config.MaxBlur * blurScale,
            BlurExtentPx = _config.BlurRadius,

            BlackOpacity = _config.BlackOpacity,

            GlareStrength = _config.GlareStrength * glareEnvelope,
            GlareOffsetPx = _config.GlareOffsetPx,
            GlareWidthPx = _config.GlareWidthPx,

            WarpStrength = _config.WarpStrength * onset * Lerp(0.5f, 1f, velocity),
            DistortionStrength = distortion,
            OffAxisWash = _config.OffAxisWash * onset,
            Luminance = 1f - (_config.GlobalDim * onset),

            BlurTaps = BlurTaps,
            DitherEnabled = _config.EnableDither,
        };
    }

    /// <summary>
    /// Inverse of <see cref="Evaluate"/> for the progress channel: finds the linear time at
    /// which this transition's panel progress equals <paramref name="panelProgress"/>.
    /// <para>
    /// Used when a transition is reversed mid-flight. The opening and closing curves are
    /// different by design, so resuming an opposite-direction animation at the same linear
    /// time would visibly jump the panel; matching panel <i>position</i> instead makes the
    /// reversal continuous. Bisection is used rather than an analytic inverse so this works
    /// for every easing preset, including springs, which are not monotonic when damping is
    /// below 1.
    /// </para>
    /// </summary>
    public float FindLinearTimeForPanelProgress(float panelProgress)
    {
        panelProgress = Easing.Clamp01(panelProgress);

        // Panel progress is monotonic in linear time for every non-overshooting curve, but
        // the direction depends on the transition: closing rises, opening falls.
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
    public LidFrameParameters ClosedFrame() => Kind == TransitionKind.Close
        ? Evaluate(1f)
        : Evaluate(0f);

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

    /// <summary>Hermite smoothstep, matching HLSL's <c>smoothstep</c> so the CPU-side model
    /// and the shader agree exactly.</summary>
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
