//=============================================================================
// LidFlow - lid transition shader
//
// One pass. Input is the frozen desktop snapshot (with a full mip chain);
// output is the composited frame for the overlay swap chain.
//
// THE MODEL
// ---------
// The panel is treated as a real rectangle hinged along its bottom edge and
// rotating away from the viewer. The desktop image is painted ON that
// rectangle, so it does not move independently: what changes is where the
// rectangle projects to on screen. At zero rotation the projection is exactly
// the identity - the panel lands precisely on the physical display and the frame
// is pixel-identical to the captured desktop.
//
// Everything else follows from that one idea rather than being layered on:
//
//   * Black grows from the top and the top corners, because that is where the
//     foreshortened, keystoned panel no longer covers the screen.
//   * Blur is strongest at the top and fades to nothing at the hinge, because a
//     point's speed is proportional to its distance from the hinge. The hinge
//     edge barely moves, so it stays sharp.
//   * The top loses contrast first, because it is the most off-axis.
//
// Coordinate spaces
// -----------------
//   uv      : [0,1]^2 across the physical display, y down.
//   content : [0,1]^2 across the captured desktop.
//   s       : position along the panel measured from the hinge. 0 at the hinge
//             edge, 1 at the far (top) edge. This is the natural variable for
//             the whole effect and nearly everything below is a function of it.
//   panel   : uv with x scaled by the aspect ratio, so one unit is one display
//             HEIGHT in both axes. Every radius and softness is in these units,
//             which is why the effect is resolution- and DPI-independent.
//
// Colour
// ------
// The snapshot is bound through an _SRGB shader-resource view and the render
// target is an _SRGB view too, so texture reads arrive linear and writes are
// re-encoded by the hardware. All the darkening below therefore happens in
// linear light, which is the difference between a panel dimming and a muddy
// grey wash.
//=============================================================================

cbuffer TransitionParams : register(b0)
{
    // --- panel / sampling -------------------------------------------------
    float2 SnapshotTexelSize;   // 1 / snapshot dimensions
    float  Aspect;              // display width / height
    float  PanelHeightPx;       // real display height, for mip selection

    float4 UvTransform;         // 2x2 matrix (a,b,c,d) handling display rotation
    float2 UvOffset;            // translation that goes with UvTransform
    float  MaxMipLevel;
    float  Progress;            // 0 = open, 1 = closed

    // --- panel geometry ---------------------------------------------------
    float  CosTheta;            // cos of the rotation away from the viewer
    float  SinTheta;
    float  Perspective;         // 1 / viewing distance, in panel heights
    float  PivotV;              // uv.y of the projected hinge (>= 1: below the screen)

    // --- optical treatment -------------------------------------------------
    float  EdgeSoftness;        // panel units
    float  BlurRadius;          // panel units, at the far edge, velocity-gated
    float  BlurFalloff;          // exponent shaping blur along s
    float  BlackOpacity;

    float  ShadowStrength;
    float  ShadowFalloff;       // exponent shaping the dim along s
    float  OffAxisWash;
    float  Luminance;

    float  GlareStrength;
    float  GlareWidth;          // in s units, measured in from the far edge
    float  Distortion;
    float  EdgeVelocity;

    float  DitherAmount;
    float  BlurTaps;
    float  BezelAmbient;
    float  BezelFalloff;        // panel units

    // Cursor placement in content UV space: xy = top-left, zw = size.
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
    // diagonal seam a two-triangle quad can show under some drivers.
    float2 position = float2((vertexId == 2) ? 3.0 : -1.0,
                             (vertexId == 1) ? 3.0 : -1.0);

    VsOut output;
    output.Position = float4(position, 0.0, 1.0);
    output.Uv = float2(position.x * 0.5 + 0.5, 0.5 - position.y * 0.5);
    return output;
}

//-----------------------------------------------------------------------------
// Inverse projection: screen point -> position on the rotating panel.
//
// Side view, with the hinge at the origin, the viewer along +y and depth along
// +z. A point at distance s along the panel sits at
//
//     height  y(s) = s * cos(theta)
//     depth   z(s) = s * sin(theta)
//
// A pinhole projection with k = 1 / viewing distance divides by (1 + z*k), so
// its projected height above the hinge is
//
//     screenT(s) = s * cos(theta) / (1 + s * sin(theta) * k)
//
// which inverts in closed form:
//
//     s = screenT / (cos(theta) - screenT * sin(theta) * k)
//
// The same divisor widens the panel's projected width, which is the keystone:
// rows further up are narrower on screen, so screen pixels beyond them fall off
// the panel and read as black. At theta = 0 this is exactly the identity.
//
// Returns false when the row is past the horizon, where the projection has no
// solution and the correct answer is simply "not on the panel".
//-----------------------------------------------------------------------------
bool ProjectToPanel(float2 uv, out float2 content, out float s)
{
    content = uv;
    s = 0.0;

    float pivot = max(PivotV, 1e-4);

    // Height above the projected hinge, normalized so that a fully-open panel
    // maps the whole display to s in [0,1].
    float screenT = (pivot - uv.y) / pivot;

    float denom = CosTheta - (screenT * SinTheta * Perspective);

    if (denom <= 1e-4)
    {
        return false;
    }

    s = screenT / denom;

    // Rows further from the hinge are further away, so they are compressed on
    // screen by exactly this factor - and therefore expanded when going the
    // other way, from screen to content.
    float widen = 1.0 + (s * SinTheta * Perspective);

    content = float2(
        0.5 + ((uv.x - 0.5) * widen),
        pivot * (1.0 - s));

    return true;
}

//-----------------------------------------------------------------------------
// Interleaved gradient noise. Used for dithering, and deliberately a function
// of pixel position only: a pattern that changed per frame would crawl visibly
// across the wide dark gradients this effect is made of.
//-----------------------------------------------------------------------------
float InterleavedGradientNoise(float2 pixel)
{
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

//-----------------------------------------------------------------------------
// Directional, per-pixel-variable blur.
//
// The radius comes from the panel position: a point s along the panel moves at
// a speed proportional to s, so the far edge smears and the hinge edge does
// not. The direction is the screen-space direction that panel motion projects
// to, which is essentially vertical.
//
// Wide radii are served by the snapshot's mip chain rather than by more taps,
// which keeps a 4K frame at a fixed, small cost however much blur is asked for.
//-----------------------------------------------------------------------------
float3 SampleBlurred(float2 content, float2 direction, float radiusPanel)
{
    if (radiusPanel <= 1e-5)
    {
        return Snapshot.SampleLevel(LinearClamp, content, 0.0).rgb;
    }

    // Guarded: a zero or negative tap count would divide the accumulated weight
    // by nothing and return black, which would look like a hole rather than an
    // error.
    int taps = max((int)BlurTaps, 1);
    float radiusPixels = radiusPanel * PanelHeightPx;

    // Choose the mip whose texels are about as wide as the gap between taps, so
    // the kernel stays gap-free instead of banding into visible copies.
    float spacing = max(radiusPixels / max(taps * 0.5, 1.0), 1.0);
    float lod = clamp(log2(spacing), 0.0, MaxMipLevel);

    float2 offset = direction * radiusPanel * float2(1.0 / max(Aspect, 1e-5), 1.0);

    float3 sum = 0.0;
    float weightSum = 0.0;
    float halfSpan = (taps - 1) * 0.5;

    [loop]
    for (int i = 0; i < taps; i++)
    {
        float t = (i - halfSpan) / max(halfSpan, 1.0);   // -1 .. 1
        float weight = exp(-2.2 * t * t);                // Gaussian-ish falloff
        sum += Snapshot.SampleLevel(LinearClamp, content + (offset * t), lod).rgb * weight;
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

    float2 content;
    float s;
    bool onPanel = ProjectToPanel(uv, content, s);

    // --- how far inside the panel this pixel is ---------------------------
    // Measured in panel units so the softness is the same on every axis, and
    // antialiased with the screen-space derivative so the edge stays crisp
    // however strongly the projection is compressing that region - a fixed
    // softness would smear badly near the horizon.
    float insideX = min(content.x, 1.0 - content.x) * Aspect;
    float insideY = min(content.y, 1.0 - content.y);
    float inside = min(insideX, insideY);

    float derivative = clamp(fwidth(inside), 1e-6, 0.05);
    float softness = max(EdgeSoftness, derivative);

    float coverage = onPanel ? smoothstep(0.0, softness, inside) : 0.0;
    float occlusion = lerp(1.0, coverage, saturate(BlackOpacity));

    // --- bezel ambient -----------------------------------------------------
    // A real bezel is not a void: it picks up a little ambient light, brightest
    // right against the glass. Without this the boundary between content and
    // black is mathematically perfect and reads as a hole cut in the image
    // rather than an object occluding it.
    float outside = max(-inside, 0.0);
    float bezel = BezelAmbient * exp(-outside / max(BezelFalloff, 1e-5));

    if (occlusion <= 0.0009 && bezel <= 0.0006)
    {
        return float4(0.0, 0.0, 0.0, 1.0);
    }

    // Distance along the panel, clamped: this drives every optical term below.
    float along = saturate(s);

    // --- optical distortion ------------------------------------------------
    // A touch of outward refraction toward the far edge, as if through the
    // panel's own glass. Quadratic, so it is confined to the last few percent.
    float2 sampleContent = content;
    sampleContent.y -= Distortion * along * along * 0.004;

    // Display rotation. Desktop Duplication always hands back an un-rotated
    // surface with the desktop rotated inside it, so the correction happens
    // here rather than by re-orienting the texture on the CPU.
    float2 sampleUv = float2(
        dot(sampleContent, UvTransform.xy),
        dot(sampleContent, UvTransform.zw)) + UvOffset;

    // --- content ----------------------------------------------------------
    // Blur grows with distance from the hinge. The exponent lets the falloff be
    // shaped without changing where it starts or ends.
    float radius = BlurRadius * pow(along, max(BlurFalloff, 0.05));

    // Panel motion projects to very nearly straight up and down on screen.
    float3 colour = SampleBlurred(sampleUv, float2(0.0, 1.0), radius);

    // --- pointer ----------------------------------------------------------
    // When the driver draws the pointer on a hardware plane it is absent from
    // the captured desktop, so it has to be composited back in - otherwise the
    // cursor would vanish on the transition's very first frame, which is exactly
    // the kind of mismatch that gives the illusion away.
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
    // A panel rotating away loses contrast and saturation, and does so more the
    // further a point is from the hinge, because that is where the viewing angle
    // is worst.
    float wash = saturate(OffAxisWash * along);

    float grey = dot(colour, float3(0.2126, 0.7152, 0.0722));
    colour = lerp(colour, grey.xxx, wash * 0.35);
    colour = (colour * (1.0 - (0.42 * wash))) + (0.012 * wash);

    // --- luminance falloff toward the far edge ----------------------------
    // The far edge is both further away and more oblique, so it is dimmer. A
    // wide, soft gradient rather than a step: it is what stops the boundary
    // reading as a drawn rectangle.
    float dim = ShadowStrength * pow(along, max(ShadowFalloff, 0.05));
    colour *= saturate(1.0 - dim);

    // --- specular sweep ---------------------------------------------------
    // The highlight that runs along the panel's leading edge as it turns.
    // Cool-tinted, additive, and strongest while the panel is actually moving.
    float glareT = (1.0 - along) / max(GlareWidth, 1e-5);
    float glare = exp(-(glareT * glareT)) * GlareStrength;
    colour += glare * float3(0.88, 0.94, 1.0);

    // --- global dim and occlusion ----------------------------------------
    colour *= Luminance;
    colour *= occlusion;

    // Slightly cool, because a bezel reflects the room rather than the panel.
    colour += bezel * float3(0.90, 0.94, 1.0);

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
