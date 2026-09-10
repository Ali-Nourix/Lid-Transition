using System.Numerics;
using System.Runtime.InteropServices;

namespace LidFlow.App.Rendering;

/// <summary>
/// CPU mirror of the <c>TransitionParams</c> constant buffer in
/// LidTransition.hlsl.
/// <para>
/// The field order here must match the shader exactly. It is arranged so that no
/// member straddles a 16-byte boundary, which is what HLSL's packing rules
/// require and what makes a plain sequential layout correct without any explicit
/// padding between groups. Total size is 160 bytes: ten float4 registers.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct TransitionConstants
{
    // register c0
    public Vector2 SnapshotTexelSize;
    public float Aspect;
    public float PanelHeightPx;

    // register c1
    public Vector4 UvTransform;

    // register c2
    public Vector2 UvOffset;
    public float MaxMipLevel;
    public float Progress;

    // register c3
    public float ApertureTop;
    public float ApertureBottom;
    public float SideInset;
    public float Keystone;

    // register c4
    public float HingeY;
    public float CornerRadius;
    public float EdgeSoftness;
    public float ShadowExtent;

    // register c5
    public float ShadowStrength;
    public float BlurRadius;
    public float BlurExtent;
    public float BlackOpacity;

    // register c6
    public float GlareStrength;
    public float GlareOffset;
    public float GlareWidth;
    public float WarpStrength;

    // register c7
    public float Distortion;
    public float OffAxisWash;
    public float Luminance;
    public float EdgeVelocity;

    // register c8
    public float DitherAmount;
    public float BlurTaps;
    public Vector2 Padding;

    // register c9
    public Vector4 CursorRect;

    /// <summary>Expected size in bytes. Asserted at startup against the real size.</summary>
    public const int ExpectedSize = 160;
}
