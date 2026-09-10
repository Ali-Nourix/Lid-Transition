using System;
using LidFlow.Core.Diagnostics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LidFlow.App.Rendering;

/// <summary>
/// The frozen desktop frame the transition is drawn from: a GPU texture with a
/// full mip chain and a shader resource view.
/// <para>
/// The mip chain is not an optimization detail, it is how the blur works. The
/// shader needs a per-pixel-variable radius that can reach a hundred pixels or
/// more near the moving edge; doing that with taps alone would cost hundreds of
/// samples per pixel at 4K. Sampling a pre-filtered mip instead keeps the cost
/// fixed and small no matter how wide the blur gets.
/// </para>
/// <para>
/// The texture is allocated as a typeless format so it can be filled by a copy
/// from the capture surface (which is always UNORM) while being <i>read</i>
/// through an _SRGB view, so the shader receives linear values without a manual
/// conversion.
/// </para>
/// </summary>
internal sealed class DesktopSnapshot : IDisposable
{
    private readonly GraphicsDevice _graphics;
    private readonly ILidFlowLog _log;

    private ID3D11Texture2D? _texture;
    private ID3D11ShaderResourceView? _view;
    private bool _disposed;

    public DesktopSnapshot(GraphicsDevice graphics, ILidFlowLog log)
    {
        _graphics = graphics ?? throw new ArgumentNullException(nameof(graphics));
        _log = log ?? NullLog.Instance;
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int MipLevels { get; private set; }

    public Format Format { get; private set; } = Format.Unknown;

    public ID3D11ShaderResourceView? View => _view;

    /// <summary>True once a frame has actually been written into the texture.</summary>
    public bool HasContent { get; private set; }

    /// <summary>
    /// Makes sure a texture of the right size and format exists. Reallocates only
    /// when something actually changed, so a resolution change is handled without
    /// churning GPU memory on every transition.
    /// </summary>
    public bool EnsureSurface(int width, int height, bool hdr)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        Format desired = hdr ? Format.R16G16B16A16_Float : Format.B8G8R8A8_Typeless;

        if (_texture is not null && Width == width && Height == height && Format == desired)
        {
            return true;
        }

        Release();

        try
        {
            // Full mip chain: 1 + floor(log2(max(w,h))).
            int mips = 1;
            int largest = Math.Max(width, height);
            while (largest > 1)
            {
                largest >>= 1;
                mips++;
            }

            Texture2DDescription description = new()
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = (uint)mips,
                ArraySize = 1,
                Format = desired,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,

                // RenderTarget is required alongside GenerateMips: the mip
                // generation path renders into the smaller levels.
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.GenerateMips,
            };

            _texture = _graphics.Device.CreateTexture2D(description);

            Format viewFormat = hdr ? Format.R16G16B16A16_Float : Format.B8G8R8A8_UNorm_SRgb;

            ShaderResourceViewDescription viewDescription = new()
            {
                Format = viewFormat,
                ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,
                Texture2D = new Texture2DShaderResourceView
                {
                    MostDetailedMip = 0,
                    MipLevels = (uint)mips,
                },
            };

            _view = _graphics.Device.CreateShaderResourceView(_texture, viewDescription);

            Width = width;
            Height = height;
            MipLevels = mips;
            Format = desired;
            HasContent = false;

            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Allocating a {width}x{height} snapshot surface failed.", ex);
            Release();
            return false;
        }
    }

    /// <summary>
    /// Copies a captured GPU surface into mip 0 and regenerates the chain.
    /// <para>
    /// <c>CopySubresourceRegion</c> rather than <c>CopyResource</c> because the
    /// source has one mip level and the destination has a full chain, which
    /// <c>CopyResource</c> refuses.
    /// </para>
    /// </summary>
    public bool CopyFrom(ID3D11Texture2D source)
    {
        if (_texture is null || _view is null)
        {
            return false;
        }

        try
        {
            _graphics.Context.CopySubresourceRegion(_texture, 0, 0, 0, 0, source, 0);
            _graphics.Context.GenerateMips(_view);
            HasContent = true;
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Copying the captured frame into the snapshot surface failed.", ex);
            return false;
        }
    }

    /// <summary>
    /// Uploads a CPU-side BGRA buffer. Used only by the GDI fallback, where the
    /// frame never existed on the GPU to begin with.
    /// </summary>
    public unsafe bool Upload(ReadOnlySpan<byte> bgra, int rowPitch)
    {
        if (_texture is null || _view is null || bgra.IsEmpty)
        {
            return false;
        }

        try
        {
            fixed (byte* data = bgra)
            {
                _graphics.Context.UpdateSubresource(
                    _texture,
                    0,
                    null,
                    (IntPtr)data,
                    (uint)rowPitch,
                    0);
            }

            _graphics.Context.GenerateMips(_view);
            HasContent = true;
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Uploading the captured frame failed.", ex);
            return false;
        }
    }

    private void Release()
    {
        _view?.Dispose();
        _view = null;
        _texture?.Dispose();
        _texture = null;
        Width = 0;
        Height = 0;
        MipLevels = 0;
        Format = Format.Unknown;
        HasContent = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Release();
    }
}
