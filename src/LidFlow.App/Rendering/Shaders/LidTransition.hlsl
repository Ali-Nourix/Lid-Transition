//=============================================================================
// LidFlow - lid transition shader
//
// One pass. Input is the frozen desktop snapshot (with a full mip chain); output
// is the composited frame for the overlay swap chain.
//
// The illusion this implements is "the panel closes around the content", not
// "the screenshot shrinks". The snapshot is sampled at (very nearly) its own
// coordinates throughout; what changes is the aperture through which it is
// visible, and the optical treatment of the pixels near the aperture's moving
// edge.
//
// Coordinate spaces
// -----------------
//   uv     : [0,1]^2 across the panel, y down.
//   panel  : uv with x scaled by the aspect ratio, so one unit is one panel
//            HEIGHT in both axes. Every distance, radius and softness below is
//            in panel units, which is why the effect is resolution- and
//            DPI-independent: the host converts its reference pixels (authored
//            against a 1080-tall panel) by dividing by 1080, and nothing else
//            in the pipeline depends on the real pixel count.
//
// Colour
// ------
// The snapshot is bound through an _SRGB shader-resource view and the render
// target is an _SRGB view too, so texture reads arrive linear and writes are
// re-encoded by the hardware. All the darkening below therefore happens in
// linear light, which is the difference between a panel dimming and a muddy
// grey wash. On an HDR output the surfaces are already linear scRGB and the
// same code is correct unchanged.
//=============================================================================

cbuffer TransitionParams : register(b0)
{
    // --- panel / sampling -------------------------------------------------
    float2 SnapshotTexelSize;   // 1 / snapshot dimensions
    float  Aspect;              // panel width / height
    float  PanelHeightPx;       // real panel height, for mip selection only

    float4 UvTransform;         // 2x2 matrix (a,b,c,d) handling display rotation
    float2 UvOffset;            // translation that goes with UvTransform
    float  MaxMipLevel;
    float  Progress;            // 0 = open, 1 = closed

    // --- aperture geometry ------------------------------------------------
    float  ApertureTop;         // uv.y of the top edge (negative when fully open)
    float  ApertureBottom;      // uv.y of the bottom edge (> 1 when fully open)
    float  SideInset;           // per-side inset at the hinge line, fraction of width
    float  Keystone;            // extra per-side inset at the aperture top

    float  HingeY;              // uv.y of the projected hinge line
    float  CornerRadius;        // panel units
    float  EdgeSoftness;        // panel units
    float  ShadowExtent;        // panel units

    // --- optical treatment -------------------------------------------------
    float  ShadowStrength;
    float  BlurRadius;          // panel units, already gated by edge speed
    float  BlurExtent;          // panel units
    float  BlackOpacity;

    float  GlareStrength;
    float  GlareOffset;         // panel units
    float  GlareWidth;          // panel units
    float  WarpStrength;

    float  Distortion;
    float  OffAxisWash;
    float  Luminance;
    float  EdgeVelocity;

    float  DitherAmount;
    float  BlurTaps;
    float2 Padding;

    // Cursor placement in snapshot UV space: xy = top-left, zw = size.
    // A zero size means "no separate cursor to draw".
    float4 CursorRect;
};

Texture2D<float4> Snapshot : register(t0);
Texture2D<float4> Cursor : register(t1);
SamplerState LinearClamp : register(s0);

//-----------------------------------------------------------------------------
// Vertex stage: a single oversized triangle, no vertex or index buffer bound.
//-----------------------------------------------------------------------------
struct VsOut
{
    float4 Position : SV_Position;
    float2 Uv       : TEXCOORD0;
};

VsOut FullscreenVS(uint vertexId : SV_VertexID)
{
    // (-1,-1), (-1,3), (3,-1) covers the viewport with one triangle; the
    // hardware clips the excess, which is cheaper than a quad and avoids the
    // diagonal seam that a two-triangle quad can show under some drivers.
    float2 position = float2((vertexId == 2) ? 3.0 : -1.0,
                             (vertexId == 1) ? 3.0 : -1.0);

    VsOut output;
    output.Position = float4(position, 0.0, 1.0);
    output.Uv = float2(position.x * 0.5 + 0.5, 0.5 - position.y * 0.5);
    return output;
}

//-----------------------------------------------------------------------------
// Aperture signed distance field.
//
// A rounded box whose half-width varies with y. Evaluating a y-dependent
// half-width inside a box SDF is an approximation to a true rounded-trapezoid
// SDF, but the keystone is a few percent of the width, so the error is far
// below the softness band it feeds and it buys a closed form that is stable
// everywhere, including after the aperture collapses to zero height.
//
// Returns the signed distance in PANEL units, negative inside the aperture.
//-----------------------------------------------------------------------------
float ApertureSdf(float2 uv)
{
    // How far above the hinge we are, 0 at the hinge and 1 at the top of the
    // panel. Below the hinge the aperture keeps its full width: that edge is
    // the one closest to the viewer, so it is not foreshortened.
    float aboveHinge = saturate((HingeY - uv.y) / max(HingeY, 1e-5));
    float inset = SideInset + (Keystone * aboveHinge);

    float halfWidth = max(0.5 - inset, 0.0) * Aspect;
    float halfHeight = max((ApertureBottom - ApertureTop) * 0.5, 0.0);
    float centreY = (ApertureBottom + ApertureTop) * 0.5;

    // Corner radius can never exceed the smaller half-extent, or the SDF
    // inverts and the aperture blows up as it collapses.
    float radius = min(CornerRadius, min(halfWidth, halfHeight));

    float2 offset = float2((uv.x - 0.5) * Aspect, uv.y - centreY);
    float2 q = abs(offset) - float2(halfWidth, halfHeight) + radius;

    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
}

//-----------------------------------------------------------------------------
// Outward normal of the aperture boundary, by central difference.
//
// Differentiating numerically rather than by hand keeps the normal consistent
// with the approximated trapezoid SDF above (an analytic box normal would
// disagree with it in the keystoned region) and costs four cheap evaluations.
//-----------------------------------------------------------------------------
float2 ApertureNormal(float2 uv)
{
    const float h = 1.0 / 512.0;
    float2 delta = float2(h / max(Aspect, 1e-5), h);

    float dx = ApertureSdf(uv + float2(delta.x, 0.0)) - ApertureSdf(uv - float2(delta.x, 0.0));
    float dy = ApertureSdf(uv + float2(0.0, delta.y)) - ApertureSdf(uv - float2(0.0, delta.y));

    return normalize(float2(dx, dy) + float2(1e-7, 1e-7));
}

//-----------------------------------------------------------------------------
// Interleaved gradient noise. Used for dithering, and deliberately a function
// of pixel position only: a noise pattern that changed per frame would crawl
// visibly across the wide dark gradients this effect is made of.
//-----------------------------------------------------------------------------
float InterleavedGradientNoise(float2 pixel)
{
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

//-----------------------------------------------------------------------------
// Directional, per-pixel-variable blur.
//
// The blur direction is the aperture normal, so it smears along the direction
// the edge is travelling; the radius comes from the animation model, which
// gates it on the edge's instantaneous speed. Wide radii are served by the
// snapshot's mip chain rather than by more taps, which keeps a 4K frame at a
// fixed, small cost regardless of how much blur is asked for.
//-----------------------------------------------------------------------------
float3 SampleBlurred(float2 uv, float2 direction, float radiusPanel)
{
    if (radiusPanel <= 1e-5)
    {
        return Snapshot.SampleLevel(LinearClamp, uv, 0.0).rgb;
    }

    int taps = (int)BlurTaps;
    float radiusPixels = radiusPanel * PanelHeightPx;

    // Choose the mip whose texels are about as wide as the gap between taps,
    // so the kernel stays gap-free instead of banding into visible copies.
    float spacing = max(radiusPixels / max(taps * 0.5, 1.0), 1.0);
    float lod = clamp(log2(spacing), 0.0, MaxMipLevel);

    // Direction in uv space; x is compressed because uv.x spans the wider axis.
    float2 offset = direction * radiusPanel * float2(1.0 / max(Aspect, 1e-5), 1.0);

    float3 sum = 0.0;
    float weightSum = 0.0;
    float halfSpan = (taps - 1) * 0.5;

    [loop]
    for (int i = 0; i < taps; i++)
    {
        float t = (i - halfSpan) / max(halfSpan, 1.0);   // -1 .. 1
        float weight = exp(-2.2 * t * t);             // Gaussian-ish falloff
        sum += Snapshot.SampleLevel(LinearClamp, uv + (offset * t), lod).rgb * weight;
        weightSum += weight;
    }

    return sum / max(weightSum, 1e-5);
}

//-----------------------------------------------------------------------------
// Pixel stage.
//-----------------------------------------------------------------------------
float4 TransitionPS(VsOut input) : SV_Target
{
    float2 uv = input.Uv;

    float signedDistance = ApertureSdf(uv);
    float distanceInside = -signedDistance;             // positive inside the aperture

    // Everything outside the panel edge is occluded; bail out before doing any
    // sampling work at all. Once the aperture has collapsed this is the whole
    // screen, so the final held-black frame costs essentially nothing.
    float edgeMask = smoothstep(0.0, max(EdgeSoftness, 1e-5), distanceInside);
    float occlusion = lerp(1.0, edgeMask, saturate(BlackOpacity));

    if (occlusion <= 0.0009)
    {
        return float4(0.0, 0.0, 0.0, 1.0);
    }

    // How close this pixel is to the moving edge, 1 at the edge and 0 deep in
    // the middle. Every optical term is scaled by this, which is what keeps the
    // centre of the frame stable while the edges do the moving.
    float edgeInfluence = 1.0 - smoothstep(0.0, max(BlurExtent, 1e-5), distanceInside);

    float2 normal = ApertureNormal(uv);

    // --- sample-coordinate warp ------------------------------------------
    // Pushing the sample coordinate away from the hinge compresses the image
    // toward it, which is the foreshortening a tilting panel would produce.
    // Shaped by edgeInfluence so the middle of the frame stays pixel-stable.
    float2 warped = uv;
    warped.y = HingeY + ((warped.y - HingeY) * (1.0 + (WarpStrength * edgeInfluence)));

    // A touch of outward refraction just inside the edge, as if through the
    // panel's own glass. Quadratic so it is confined to the last few percent.
    warped += normal * (Distortion * edgeInfluence * edgeInfluence * 0.004);

    // Display rotation. Desktop Duplication always hands back an un-rotated
    // surface with the desktop rotated inside it, so the correction happens
    // here rather than by re-orienting the texture on the CPU.
    float2 sampleUv = float2(dot(warped, UvTransform.xy), dot(warped, UvTransform.zw)) + UvOffset;

    // --- content ----------------------------------------------------------
    float radius = BlurRadius * edgeInfluence;
    float3 colour = SampleBlurred(sampleUv, normal, radius);

    // --- pointer ----------------------------------------------------------
    // When the driver draws the pointer on a hardware plane it is absent from
    // the captured desktop, so it has to be composited back in - otherwise the
    // cursor would vanish on the transition's very first frame, which is exactly
    // the kind of mismatch that gives the illusion away. Sampled at the same
    // warped coordinate as the content so it moves with it.
    if (CursorRect.z > 0.0 && CursorRect.w > 0.0)
    {
        float2 cursorUv = (sampleUv - CursorRect.xy) / CursorRect.zw;

        if (cursorUv.x >= 0.0 && cursorUv.x <= 1.0 && cursorUv.y >= 0.0 && cursorUv.y <= 1.0)
        {
            float4 pointer = Cursor.SampleLevel(LinearClamp, cursorUv, 0.0);
            colour = lerp(colour, pointer.rgb, saturate(pointer.a));
        }
    }

    // --- off-axis viewing --------------------------------------------------
    // A real panel rotating away loses contrast and saturation, and does so
    // more the further a point is from the hinge, because that is where the
    // viewing angle is worst. Asymmetric for the same reason.
    float axis = (uv.y < HingeY)
        ? ((HingeY - uv.y) / max(HingeY, 1e-5))
        : (((uv.y - HingeY) / max(1.0 - HingeY, 1e-5)) * 0.45);
    float wash = saturate(OffAxisWash * saturate(axis));

    float grey = dot(colour, float3(0.2126, 0.7152, 0.0722));
    colour = lerp(colour, grey.xxx, wash * 0.35);
    colour = (colour * (1.0 - (0.42 * wash))) + (0.012 * wash);

    // --- luminance falloff behind the edge --------------------------------
    // A wide, soft gradient that precedes the hard edge. This is what stops the
    // boundary reading as a drawn rectangle: the panel darkens into its own
    // edge instead of meeting it abruptly.
    float shadow = smoothstep(0.0, max(ShadowExtent, 1e-5), distanceInside);
    colour *= lerp(1.0 - saturate(ShadowStrength), 1.0, shadow);

    // --- specular sweep ---------------------------------------------------
    // The reflection that slides across a glossy panel as it turns. Cool-tinted
    // and additive, peaking a little way inside the edge.
    float glareT = (distanceInside - GlareOffset) / max(GlareWidth, 1e-5);
    float glare = exp(-(glareT * glareT)) * GlareStrength;
    colour += glare * float3(0.88, 0.94, 1.0);

    // --- global dim and occlusion ----------------------------------------
    colour *= Luminance;
    colour *= occlusion;

    // --- dither ------------------------------------------------------------
    // Two decorrelated noise samples make a triangular distribution, which is
    // what actually removes banding rather than just softening it. Amplitude is
    // well under one 8-bit code value, so it is invisible on its own.
    if (DitherAmount > 0.0)
    {
        float2 pixel = uv / max(SnapshotTexelSize, float2(1e-9, 1e-9));
        float n1 = InterleavedGradientNoise(pixel);
        float n2 = InterleavedGradientNoise(pixel + float2(37.0, 17.0));
        colour += (n1 - n2) * DitherAmount;
    }

    return float4(max(colour, 0.0), 1.0);
}
