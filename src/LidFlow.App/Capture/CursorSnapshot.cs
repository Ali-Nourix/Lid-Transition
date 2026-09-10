using System;
using System.Numerics;
using LidFlow.App.Interop;
using LidFlow.App.Rendering;
using LidFlow.Core.Diagnostics;
using LidFlow.Core.Monitors;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LidFlow.App.Capture;

/// <summary>
/// Captures the mouse pointer as a premultiplied-alpha texture so it can be
/// composited back into the frozen snapshot.
/// <para>
/// This is needed because the pointer is usually drawn by the display controller
/// on a separate hardware plane and is therefore <i>absent</i> from every desktop
/// capture API. Without this the cursor would disappear the instant the overlay
/// appeared - a one-frame discontinuity that reads as a glitch even when the rest
/// of the effect is perfect.
/// </para>
/// <para>
/// Alpha is recovered by drawing the cursor twice, once over black and once over
/// white, and solving for coverage: <c>alpha = 1 - (white - black)</c>. That is
/// necessary because <c>DrawIconEx</c> does not write an alpha channel for
/// classic AND/XOR cursors, so a single draw onto a transparent surface would
/// come back completely invisible. The one case this cannot represent exactly is
/// a monochrome cursor's XOR-invert region, which has no fixed colour at all; it
/// is clamped to opaque, which matches how such cursors look over mid-tones.
/// </para>
/// </summary>
internal sealed class CursorSnapshot : IDisposable
{
    private const int MaxCursorSize = 256;

    private readonly GraphicsDevice _graphics;
    private readonly ILidFlowLog _log;

    private ID3D11Texture2D? _texture;
    private ID3D11ShaderResourceView? _view;
    private int _textureWidth;
    private int _textureHeight;
    private bool _disposed;

    public CursorSnapshot(GraphicsDevice graphics, ILidFlowLog log)
    {
        _graphics = graphics ?? throw new ArgumentNullException(nameof(graphics));
        _log = log ?? NullLog.Instance;
    }

    public ID3D11ShaderResourceView? View => _view;

    /// <summary>Placement in snapshot UV space: xy = top-left, zw = size. Zero when there is no pointer.</summary>
    public Vector4 Rect { get; private set; }

    public bool HasCursor => Rect.Z > 0f && Rect.W > 0f && _view is not null;

    public void Clear() => Rect = Vector4.Zero;

    /// <summary>
    /// Grabs the current pointer, if it is visible and inside
    /// <paramref name="displayBounds"/>. Failure is never fatal: the transition
    /// just runs without a composited pointer.
    /// </summary>
    public unsafe bool TryCapture(DisplayBounds displayBounds)
    {
        Clear();

        try
        {
            NativeMethods.CURSORINFO info = default;
            info.cbSize = (uint)sizeof(NativeMethods.CURSORINFO);

            if (!NativeMethods.GetCursorInfo(&info))
            {
                return false;
            }

            if ((info.flags & NativeMethods.CURSOR_SHOWING) == 0 || info.hCursor == IntPtr.Zero)
            {
                return false;
            }

            NativeMethods.ICONINFO iconInfo = default;
            if (!NativeMethods.GetIconInfo(info.hCursor, &iconInfo))
            {
                return false;
            }

            int width;
            int height;

            try
            {
                if (!MeasureCursor(iconInfo, out width, out height))
                {
                    return false;
                }
            }
            finally
            {
                if (iconInfo.hbmMask != IntPtr.Zero)
                {
                    NativeMethods.DeleteObject(iconInfo.hbmMask);
                }

                if (iconInfo.hbmColor != IntPtr.Zero)
                {
                    NativeMethods.DeleteObject(iconInfo.hbmColor);
                }
            }

            // Screen position of the cursor's top-left, accounting for the hotspot.
            int screenX = info.ptScreenPos.X - iconInfo.xHotspot;
            int screenY = info.ptScreenPos.Y - iconInfo.yHotspot;

            // Entirely off this display: nothing to composite.
            if (screenX + width < displayBounds.X ||
                screenY + height < displayBounds.Y ||
                screenX > displayBounds.Right ||
                screenY > displayBounds.Bottom)
            {
                return false;
            }

            byte[]? pixels = RenderCursorToBgra(info.hCursor, width, height);
            if (pixels is null)
            {
                return false;
            }

            if (!EnsureTexture(width, height))
            {
                return false;
            }

            fixed (byte* data = pixels)
            {
                _graphics.Context.UpdateSubresource(
                    _texture!,
                    0,
                    null,
                    (IntPtr)data,
                    (uint)(width * 4),
                    0);
            }

            Rect = new Vector4(
                (screenX - displayBounds.X) / (float)displayBounds.Width,
                (screenY - displayBounds.Y) / (float)displayBounds.Height,
                width / (float)displayBounds.Width,
                height / (float)displayBounds.Height);

            return true;
        }
        catch (Exception ex)
        {
            _log.Debug($"Cursor capture skipped: {ex.Message}");
            Clear();
            return false;
        }
    }

    private static unsafe bool MeasureCursor(NativeMethods.ICONINFO iconInfo, out int width, out int height)
    {
        width = 0;
        height = 0;

        // GetIconInfo hands back bitmaps rather than dimensions, so the size comes
        // from the colour bitmap when there is one and from the mask otherwise -
        // and a mask-only (monochrome) cursor stacks its AND and XOR masks
        // vertically, so its real height is half the bitmap's.
        IntPtr bitmap = iconInfo.hbmColor != IntPtr.Zero ? iconInfo.hbmColor : iconInfo.hbmMask;
        if (bitmap == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.BITMAP header = default;
        int written = NativeMethods.GetObjectW(bitmap, sizeof(NativeMethods.BITMAP), &header);
        if (written == 0 || header.bmWidth <= 0 || header.bmHeight == 0)
        {
            return false;
        }

        width = header.bmWidth;
        height = iconInfo.hbmColor != IntPtr.Zero ? header.bmHeight : header.bmHeight / 2;

        if (width <= 0 || height <= 0 || width > MaxCursorSize || height > MaxCursorSize)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Draws the cursor over black and over white, then solves for per-pixel
    /// alpha. Returns straight (non-premultiplied) BGRA.
    /// </summary>
    private unsafe byte[]? RenderCursorToBgra(IntPtr cursor, int width, int height)
    {
        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return null;
        }

        IntPtr memoryDc = IntPtr.Zero;
        IntPtr blackBitmap = IntPtr.Zero;
        IntPtr whiteBitmap = IntPtr.Zero;

        try
        {
            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                return null;
            }

            byte* blackBits;
            byte* whiteBits;

            blackBitmap = CreateSurface(screenDc, width, height, out blackBits);
            whiteBitmap = CreateSurface(screenDc, width, height, out whiteBits);

            if (blackBitmap == IntPtr.Zero || whiteBitmap == IntPtr.Zero)
            {
                return null;
            }

            int pixelCount = width * height;

            // Black background is already zeroed by CreateDIBSection; fill the
            // other one with opaque white.
            for (int i = 0; i < pixelCount; i++)
            {
                ((uint*)whiteBits)[i] = 0xFFFF_FFFFu;
            }

            if (!DrawOnto(memoryDc, blackBitmap, cursor, width, height) ||
                !DrawOnto(memoryDc, whiteBitmap, cursor, width, height))
            {
                return null;
            }

            NativeMethods.GdiFlush();

            byte[] result = new byte[pixelCount * 4];

            for (int i = 0; i < pixelCount; i++)
            {
                int offset = i * 4;

                byte blackB = blackBits[offset + 0];
                byte blackG = blackBits[offset + 1];
                byte blackR = blackBits[offset + 2];

                byte whiteB = whiteBits[offset + 0];
                byte whiteG = whiteBits[offset + 1];
                byte whiteR = whiteBits[offset + 2];

                // Over black the result is alpha*colour; over white it is
                // alpha*colour + (1-alpha). Subtracting gives the transparency
                // directly, and the strongest channel is the safest estimate for a
                // shape whose colour we do not otherwise know.
                int transparency = Math.Max(
                    Math.Max(whiteB - blackB, whiteG - blackG),
                    whiteR - blackR);

                int alpha = 255 - Math.Clamp(transparency, 0, 255);

                result[offset + 0] = blackB;
                result[offset + 1] = blackG;
                result[offset + 2] = blackR;
                result[offset + 3] = (byte)alpha;

                // The black-background draw is already premultiplied, so undo it
                // to get straight alpha, which is what the shader's lerp expects.
                if (alpha is > 0 and < 255)
                {
                    result[offset + 0] = (byte)Math.Min(255, blackB * 255 / alpha);
                    result[offset + 1] = (byte)Math.Min(255, blackG * 255 / alpha);
                    result[offset + 2] = (byte)Math.Min(255, blackR * 255 / alpha);
                }
            }

            return result;
        }
        finally
        {
            if (blackBitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(blackBitmap);
            }

            if (whiteBitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(whiteBitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memoryDc);
            }

            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static unsafe IntPtr CreateSurface(IntPtr referenceDc, int width, int height, out byte* bits)
    {
        NativeMethods.BITMAPINFOHEADER header = new()
        {
            biSize = (uint)sizeof(NativeMethods.BITMAPINFOHEADER),
            biWidth = width,

            // Negative height gives a top-down DIB, matching texture row order.
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = NativeMethods.BI_RGB,
        };

        void* raw;
        IntPtr bitmap = NativeMethods.CreateDIBSection(
            referenceDc,
            &header,
            NativeMethods.DIB_RGB_COLORS,
            &raw,
            IntPtr.Zero,
            0);

        bits = (byte*)raw;
        return bitmap;
    }

    private static bool DrawOnto(IntPtr memoryDc, IntPtr bitmap, IntPtr cursor, int width, int height)
    {
        IntPtr previous = NativeMethods.SelectObject(memoryDc, bitmap);

        try
        {
            return NativeMethods.DrawIconEx(
                memoryDc,
                0,
                0,
                cursor,
                width,
                height,
                0,
                IntPtr.Zero,
                NativeMethods.DI_NORMAL);
        }
        finally
        {
            NativeMethods.SelectObject(memoryDc, previous);
        }
    }

    private bool EnsureTexture(int width, int height)
    {
        if (_texture is not null && _textureWidth == width && _textureHeight == height)
        {
            return true;
        }

        _view?.Dispose();
        _view = null;
        _texture?.Dispose();
        _texture = null;

        try
        {
            _texture = _graphics.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_Typeless,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            });

            // sRGB view, so the pointer is blended in the same linear space as the
            // desktop content around it.
            _view = _graphics.Device.CreateShaderResourceView(_texture, new ShaderResourceViewDescription
            {
                Format = Format.B8G8R8A8_UNorm_SRgb,
                ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,
                Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 },
            });

            _textureWidth = width;
            _textureHeight = height;
            return true;
        }
        catch (Exception ex)
        {
            _log.Debug($"Cursor texture allocation failed: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _view?.Dispose();
        _texture?.Dispose();
    }
}
