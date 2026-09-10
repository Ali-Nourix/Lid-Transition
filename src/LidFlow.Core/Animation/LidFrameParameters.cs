namespace LidFlow.Core.Animation;

/// <summary>
/// Every value the transition shader needs for one frame, all derived from a
/// single normalized progress value.
/// </summary>
/// <remarks>
/// <para>
/// The geometry is a physical one: the panel is a rectangle hinged along its
/// bottom edge, rotating away from the viewer by <see cref="AngleDegrees"/>, with
/// the desktop image painted on it. At zero rotation the projection is exactly
/// the identity, so the panel lands precisely on the physical display.
/// </para>
/// <para>
/// Nearly every optical term is a function of <c>s</c>, position along the panel
/// measured from the hinge (0 at the hinge edge, 1 at the far edge). That is not
/// a convenience: a point's speed is proportional to its distance from the hinge,
/// so <c>s</c> is what actually governs how much a given row smears, dims and
/// loses contrast.
/// </para>
/// <para>
/// <c>*Px</c> quantities are reference pixels at a 1080-tall display; the
/// renderer converts them to panel-height units by dividing by 1080, which is why
/// nothing in the pipeline depends on the real resolution or DPI.
/// </para>
/// </remarks>
public readonly struct LidFrameParameters
{
    /// <summary>Eased panel progress. 0 = fully open (snapshot untouched), 1 = fully closed (black).</summary>
    public float Progress { get; init; }

    /// <summary>Normalized instantaneous rotation speed, 0..1.</summary>
    public float EdgeVelocity { get; init; }

    /// <summary>Rotation away from the viewer, in degrees. 0 = flat against the display.</summary>
    public float AngleDegrees { get; init; }

    public float CosTheta { get; init; }

    public float SinTheta { get; init; }

    /// <summary>Reciprocal viewing distance, in panel heights. Drives the keystone.</summary>
    public float Perspective { get; init; }

    /// <summary>uv.y of the projected hinge. At or just below 1, i.e. the bottom of the display.</summary>
    public float PivotV { get; init; }

    /// <summary>Soft width of the panel's own edge.</summary>
    public float EdgeSoftnessPx { get; init; }

    /// <summary>Blur radius at the far edge, already gated by rotation speed.</summary>
    public float BlurRadiusPx { get; init; }

    /// <summary>Exponent shaping how blur falls off from the far edge toward the hinge.</summary>
    public float BlurFalloff { get; init; }

    /// <summary>Opacity of the region the panel no longer covers, 0..1.</summary>
    public float BlackOpacity { get; init; }

    /// <summary>Depth of the luminance falloff toward the far edge.</summary>
    public float ShadowStrength { get; init; }

    /// <summary>Exponent shaping that falloff along the panel.</summary>
    public float ShadowFalloff { get; init; }

    /// <summary>Contrast and saturation loss toward the far edge.</summary>
    public float OffAxisWash { get; init; }

    /// <summary>Global luminance multiplier applied before occlusion.</summary>
    public float Luminance { get; init; }

    public float GlareStrength { get; init; }

    /// <summary>Width of the leading-edge highlight, in <c>s</c> units.</summary>
    public float GlareWidth { get; init; }

    /// <summary>Optical (glass) term near the far edge.</summary>
    public float DistortionStrength { get; init; }

    /// <summary>Ambient light picked up by the bezel, just outside the panel.</summary>
    public float BezelAmbient { get; init; }

    /// <summary>Distance outside the panel edge over which the bezel ambient decays.</summary>
    public float BezelFalloffPx { get; init; }

    /// <summary>Blur tap count for this quality tier.</summary>
    public int BlurTaps { get; init; }

    public bool DitherEnabled { get; init; }

    /// <summary>
    /// Fraction of the display height the panel still covers, for tests and
    /// diagnostics. Derived from the same projection the shader uses.
    /// </summary>
    public float ProjectedCoverage
    {
        get
        {
            // Screen height of the panel's far edge, from the forward projection
            // screenT(s) = s*cos / (1 + s*sin*k), evaluated at s = 1.
            float denominator = 1f + (SinTheta * Perspective);
            if (denominator <= 1e-5f)
            {
                return 0f;
            }

            float screenT = CosTheta / denominator;
            return screenT < 0f ? 0f : (screenT > 1f ? 1f : screenT);
        }
    }
}
