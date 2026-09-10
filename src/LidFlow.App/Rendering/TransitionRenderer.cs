using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using LidFlow.App.Overlay;
using LidFlow.Core.Animation;
using LidFlow.Core.Configuration;
using LidFlow.Core.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LidFlow.App.Rendering;

/// <summary>
/// Draws one transition frame: a single fullscreen pass over the frozen snapshot.
/// <para>
/// Everything variable about a frame arrives through the constant buffer, which
/// is derived entirely from <see cref="LidFrameParameters"/> - so the visual
/// design lives in testable CPU code and this class is only responsible for
/// getting it onto the GPU. There is one draw call and no render-to-texture
/// ping-pong, which is why the cost is flat with resolution.
/// </para>
/// </summary>
internal sealed class TransitionRenderer : IDisposable
{
    private readonly GraphicsDevice _graphics;
    private readonly ILidFlowLog _log;

    private ID3D11Buffer? _constantBuffer;
    private ID3D11RasterizerState? _rasterizer;
    private ID3D11DepthStencilState? _depthStencil;
    private ID3D11BlendState? _blend;
    private bool _disposed;

    public TransitionRenderer(GraphicsDevice graphics, ILidFlowLog log)
    {
        _graphics = graphics ?? throw new ArgumentNullException(nameof(graphics));
        _log = log ?? NullLog.Instance;
    }

    public bool Initialize()
    {
        try
        {
            // A layout mismatch here would show up as a garbled frame rather than
            // an error, so it is checked once, loudly, at startup.
            int actual = Unsafe.SizeOf<TransitionConstants>();
            if (actual != TransitionConstants.ExpectedSize)
            {
                throw new InvalidOperationException(
                    $"TransitionConstants is {actual} bytes; the shader expects {TransitionConstants.ExpectedSize}.");
            }

            _constantBuffer = _graphics.Device.CreateBuffer(
                new BufferDescription
                {
                    ByteWidth = (uint)TransitionConstants.ExpectedSize,
                    Usage = ResourceUsage.Dynamic,
                    BindFlags = BindFlags.ConstantBuffer,
                    CPUAccessFlags = CpuAccessFlags.Write,
                },
                IntPtr.Zero);

            // The fullscreen triangle's winding depends on nothing useful, so
            // culling is simply off rather than relying on a particular order.
            _rasterizer = _graphics.Device.CreateRasterizerState(new RasterizerDescription
            {
                CullMode = CullMode.None,
                FillMode = FillMode.Solid,
                DepthClipEnable = false,
                ScissorEnable = false,
                MultisampleEnable = false,
                AntialiasedLineEnable = false,
            });

            // No depth buffer is bound; disabling depth explicitly keeps the state
            // deterministic regardless of what ran on this context before.
            _depthStencil = _graphics.Device.CreateDepthStencilState(new DepthStencilDescription
            {
                DepthEnable = false,
                DepthWriteMask = DepthWriteMask.Zero,
                StencilEnable = false,
            });

            // The overlay is opaque: the shader already resolves occlusion to
            // black, so there is nothing to blend against.
            _blend = _graphics.Device.CreateBlendState(BlendDescription.Opaque);

            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Renderer initialization failed.", ex);
            return false;
        }
    }

    /// <summary>
    /// Renders and presents one frame. Returns false if the device was lost, in
    /// which case the caller abandons the transition and rebuilds.
    /// </summary>
    public bool RenderFrame(
        OverlayWindow overlay,
        DesktopSnapshot snapshot,
        in LidFrameParameters frame,
        ModeRotation rotation,
        bool ditherEnabled,
        Capture.CursorSnapshot? cursor)
    {
        if (_constantBuffer is null ||
            _graphics.VertexShader is null ||
            _graphics.PixelShader is null ||
            _graphics.LinearClamp is null)
        {
            return false;
        }

        ID3D11RenderTargetView? renderTarget = overlay.RenderTargetView;
        ID3D11ShaderResourceView? snapshotView = snapshot.View;

        if (renderTarget is null || snapshotView is null || !snapshot.HasContent)
        {
            return false;
        }

        int width = overlay.Bounds.Width;
        int height = overlay.Bounds.Height;

        if (width <= 0 || height <= 0)
        {
            return false;
        }

        try
        {
            ID3D11DeviceContext context = _graphics.Context;

            UpdateConstants(snapshot, frame, rotation, width, height, ditherEnabled, cursor);

            context.OMSetRenderTargets(renderTarget, null!);
            context.RSSetViewport(0f, 0f, width, height);
            context.RSSetState(_rasterizer);
            context.OMSetDepthStencilState(_depthStencil);
            context.OMSetBlendState(_blend);

            // No vertex or index buffer: the vertex shader synthesizes a single
            // oversized triangle from SV_VertexID.
            context.IASetInputLayout(null);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

            context.VSSetShader(_graphics.VertexShader);
            context.PSSetShader(_graphics.PixelShader);
            context.PSSetShaderResource(0, snapshotView);

            // Slot 1 is the pointer. Bound unconditionally so a stale view from a
            // previous transition can never be sampled; the shader ignores it when
            // CursorRect has zero size.
            context.PSSetShaderResource(1, cursor is not null && cursor.HasCursor ? cursor.View! : null!);
            context.PSSetSampler(0, _graphics.LinearClamp);
            context.PSSetConstantBuffer(0, _constantBuffer);

            // The triangle covers every pixel and the shader writes all of them,
            // so there is deliberately no clear: it would be pure write bandwidth.
            context.Draw(3, 0);

            // Unbind the snapshot before presenting. It is about to be overwritten
            // by the next capture, and leaving it bound would make that copy stall.
            context.PSSetShaderResource(0, null!);
            context.PSSetShaderResource(1, null!);

            return overlay.Present();
        }
        catch (Exception ex)
        {
            _log.Error("Rendering a transition frame failed.", ex);
            return false;
        }
    }

    private void UpdateConstants(
        DesktopSnapshot snapshot,
        in LidFrameParameters frame,
        ModeRotation rotation,
        int width,
        int height,
        bool ditherEnabled,
        Capture.CursorSnapshot? cursor)
    {
        // Reference pixels are authored against a 1080-tall panel, and the shader
        // measures everything in panel-height units - so the conversion is a plain
        // divide by the reference height and is independent of the real resolution
        // and DPI. That is the whole reason the effect looks identical at 1080p and
        // at 4K, and at every scaling factor.
        const float ToPanelUnits = 1f / AnimationConfig.ReferenceHeight;

        GetUvTransform(rotation, out Vector4 transform, out Vector2 offset);

        TransitionConstants constants = new()
        {
            SnapshotTexelSize = new Vector2(
                snapshot.Width > 0 ? 1f / snapshot.Width : 0f,
                snapshot.Height > 0 ? 1f / snapshot.Height : 0f),
            Aspect = height > 0 ? width / (float)height : 1f,
            PanelHeightPx = height,

            UvTransform = transform,
            UvOffset = offset,
            MaxMipLevel = Math.Max(snapshot.MipLevels - 1, 0),
            Progress = frame.Progress,

            ApertureTop = frame.ApertureTop,
            ApertureBottom = frame.ApertureBottom,
            SideInset = frame.SideInset,
            Keystone = frame.Keystone,

            HingeY = frame.HingeY,
            CornerRadius = frame.CornerRadiusPx * ToPanelUnits,
            EdgeSoftness = frame.EdgeSoftnessPx * ToPanelUnits,
            ShadowExtent = frame.ShadowExtentPx * ToPanelUnits,

            ShadowStrength = frame.ShadowStrength,
            BlurRadius = frame.BlurRadiusPx * ToPanelUnits,
            BlurExtent = frame.BlurExtentPx * ToPanelUnits,
            BlackOpacity = frame.BlackOpacity,

            GlareStrength = frame.GlareStrength,
            GlareOffset = frame.GlareOffsetPx * ToPanelUnits,
            GlareWidth = frame.GlareWidthPx * ToPanelUnits,
            WarpStrength = frame.WarpStrength,

            Distortion = frame.DistortionStrength,
            OffAxisWash = frame.OffAxisWash,
            Luminance = frame.Luminance,
            EdgeVelocity = frame.EdgeVelocity,

            // Amplitude is well under one 8-bit code value; the triangular
            // distribution in the shader is what actually breaks up the banding.
            DitherAmount = ditherEnabled && frame.DitherEnabled ? 1f / 1020f : 0f,
            BlurTaps = frame.BlurTaps,
            Padding = Vector2.Zero,

            CursorRect = cursor is not null && cursor.HasCursor ? cursor.Rect : Vector4.Zero,

            BezelAmbient = frame.BezelAmbient,
            BezelFalloff = frame.BezelFalloffPx * ToPanelUnits,
            Padding2 = Vector2.Zero,
        };

        MappedSubresource mapped = _graphics.Context.Map(_constantBuffer!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);

        try
        {
            unsafe
            {
                *(TransitionConstants*)mapped.DataPointer = constants;
            }
        }
        finally
        {
            _graphics.Context.Unmap(_constantBuffer!, 0);
        }
    }

    /// <summary>
    /// Maps overlay (desktop-space) UV to capture-surface UV for a rotated display.
    /// <para>
    /// Desktop Duplication always hands back an un-rotated surface with the desktop
    /// rotated inside it - a portrait 768x1024 desktop arrives as a 1024x768
    /// surface - so the correction has to happen at sample time. Handling it here
    /// rather than by rotating the texture on the CPU keeps the capture path a
    /// single GPU-to-GPU copy.
    /// </para>
    /// <para>
    /// The capture backends that already deliver desktop-oriented frames
    /// (Windows.Graphics.Capture and GDI) pass <see cref="ModeRotation.Identity"/>.
    /// </para>
    /// </summary>
    internal static void GetUvTransform(ModeRotation rotation, out Vector4 transform, out Vector2 offset)
    {
        switch (rotation)
        {
            case ModeRotation.Rotate90:
                transform = new Vector4(0f, -1f, 1f, 0f);
                offset = new Vector2(1f, 0f);
                break;

            case ModeRotation.Rotate180:
                transform = new Vector4(-1f, 0f, 0f, -1f);
                offset = new Vector2(1f, 1f);
                break;

            case ModeRotation.Rotate270:
                transform = new Vector4(0f, 1f, -1f, 0f);
                offset = new Vector2(0f, 1f);
                break;

            case ModeRotation.Identity:
            case ModeRotation.Unspecified:
            default:
                transform = new Vector4(1f, 0f, 0f, 1f);
                offset = Vector2.Zero;
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _blend?.Dispose();
        _depthStencil?.Dispose();
        _rasterizer?.Dispose();
        _constantBuffer?.Dispose();
    }
}
