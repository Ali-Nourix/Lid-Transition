namespace LidFlow.Core.Animation;

/// <summary>
/// Every value the transition shader needs for one frame, all derived from a single
/// normalized progress value.
/// </summary>
/// <remarks>
/// Coordinate conventions, matching the shader:
/// <list type="bullet">
/// <item>Y quantities are fractions of panel <b>height</b>, 0 = top, 1 = bottom.</item>
/// <item>X quantities are fractions of panel <b>width</b>.</item>
/// <item><c>*Px</c> quantities are reference pixels at a 1080-tall panel; the renderer
/// scales them to the real panel height.</item>
/// </list>
/// </remarks>
public readonly struct LidFrameParameters
{
    /// <summary>Eased panel progress. 0 = fully open (snapshot untouched), 1 = fully closed (black).</summary>
    public float Progress { get; init; }

    /// <summary>Normalized instantaneous edge speed, 0..1, peak-normalized for the active curve.</summary>
    public float EdgeVelocity { get; init; }

    /// <summary>Y of the aperture's top edge. Negative at full open so the edge starts off-panel.</summary>
    public float ApertureTop { get; init; }

    /// <summary>Y of the aperture's bottom edge. Above 1 at full open.</summary>
    public float ApertureBottom { get; init; }

    /// <summary>Per-side horizontal inset at the hinge line, as a fraction of width.</summary>
    public float SideInset { get; init; }

    /// <summary>Extra per-side inset applied at the aperture's top edge, as a fraction of
    /// width. This is the keystone: the aperture becomes a trapezoid narrowing away from
    /// the hinge, which is the geometric cue for a panel tilting away from the viewer.</summary>
    public float Keystone { get; init; }

    /// <summary>Normalized Y of the projected hinge line: where the aperture converges.</summary>
    public float HingeY { get; init; }

    /// <summary>Aperture corner rounding.</summary>
    public float CornerRadiusPx { get; init; }

    /// <summary>Soft width of the panel edge itself.</summary>
    public float EdgeSoftnessPx { get; init; }

    /// <summary>Distance inside the edge over which luminance falls to the occluded level.</summary>
    public float ShadowExtentPx { get; init; }

    /// <summary>Depth of that falloff, 0..1.</summary>
    public float ShadowStrength { get; init; }

    /// <summary>Peak blur radius immediately behind the edge.</summary>
    public float BlurRadiusPx { get; init; }

    /// <summary>Distance inside the edge over which blur ramps up.</summary>
    public float BlurExtentPx { get; init; }

    /// <summary>Opacity of the occluded region, 0..1.</summary>
    public float BlackOpacity { get; init; }

    public float GlareStrength { get; init; }

    public float GlareOffsetPx { get; init; }

    public float GlareWidthPx { get; init; }

    /// <summary>Edge-localized sample-coordinate pull toward the hinge.</summary>
    public float WarpStrength { get; init; }

    /// <summary>Optical (barrel) term near the moving edge.</summary>
    public float DistortionStrength { get; init; }

    /// <summary>Contrast/saturation loss, weighted by distance from the hinge.</summary>
    public float OffAxisWash { get; init; }

    /// <summary>Global luminance multiplier applied before occlusion.</summary>
    public float Luminance { get; init; }

    /// <summary>Ambient light picked up by the panel bezel, just outside the aperture.</summary>
    public float BezelAmbient { get; init; }

    /// <summary>Distance outside the edge over which the bezel ambient decays.</summary>
    public float BezelFalloffPx { get; init; }

    /// <summary>Blur tap count for this quality tier.</summary>
    public int BlurTaps { get; init; }

    public bool DitherEnabled { get; init; }

    /// <summary>Aperture height as a fraction of panel height, clamped at 0.</summary>
    public float ApertureHeight
    {
        get
        {
            float h = ApertureBottom - ApertureTop;
            return h < 0f ? 0f : h;
        }
    }
}
