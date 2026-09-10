using System;
using System.Diagnostics;
using LidFlow.App.Interop;
using LidFlow.App.Monitors;
using LidFlow.App.Rendering;
using LidFlow.Core.Diagnostics;
using LidFlow.Core.Monitors;

namespace LidFlow.App.Capture;

/// <summary>
/// Last-resort capture through GDI <c>BitBlt</c>.
/// <para>
/// Slower than the GPU paths - the frame makes a round trip through system memory
/// - but it has one property the others do not: it always returns the current
/// desktop, even when absolutely nothing on screen has changed. Desktop
/// Duplication, by contrast, only hands over a frame when the desktop updates, so
/// on a completely static screen it can time out. This backend is what makes
/// "capture always succeeds" true rather than merely likely.
/// </para>
/// <para>
/// Its limitations are inherent to GDI and are the reason it is not the default:
/// content drawn on a hardware overlay (some video playback) and DRM-protected
/// windows come back black, and it cannot represent HDR.
/// </para>
/// </summary>
internal sealed class GdiSnapshotSource : ISnapshotSource
{
    private readonly ILidFlowLog _log;
    private byte[] _buffer = Array.Empty<byte>();
    private bool _disposed;

    public GdiSnapshotSource(ILidFlowLog log)
    {
        _log = log ?? NullLog.Instance;
    }

    public string Name => "GDI";

    public bool SupportsHdr => false;

    /// <summary>
    /// False. GDI reads the composed screen, so an excluded overlay would still be
    /// read as its rendered content; the caller has to hide the overlay around the
    /// capture when using this backend.
    /// </summary>
    public bool HonoursCaptureExclusion => false;

    public void Warm(DisplayTarget display)
    {
        // Pre-size the staging buffer so the first capture does not allocate.
        EnsureBuffer(display.Info.Bounds);
    }

    public unsafe CaptureResult TryCapture(DisplayTarget display, DesktopSnapshot snapshot, int timeoutMs)
    {
        long start = Stopwatch.GetTimestamp();

        DisplayBounds bounds = display.Info.Bounds;
        if (bounds.IsEmpty)
        {
            return CaptureResult.Failed(CaptureFailure.Error, "Display has no bounds.");
        }

        if (!snapshot.EnsureSurface(bounds.Width, bounds.Height, hdr: false))
        {
            return CaptureResult.Failed(CaptureFailure.Error, "Snapshot surface allocation failed.");
        }

        IntPtr displayDc = IntPtr.Zero;
        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;

        // CreateDC handles are deleted, GetDC handles are released, and using the
        // wrong call on either is documented as invalid - so remember which it is
        // rather than trying both.
        bool displayDcOwned = false;
        int blitOriginX = 0;
        int blitOriginY = 0;

        try
        {
            // A DC for this specific display, so multi-monitor coordinates do not
            // have to be reasoned about: the blit origin is simply (0,0).
            fixed (char* driver = "DISPLAY")
            fixed (char* device = display.Info.Id)
            {
                displayDc = NativeMethods.CreateDCW(driver, device, null, IntPtr.Zero);
            }

            if (displayDc != IntPtr.Zero)
            {
                displayDcOwned = true;
            }
            else
            {
                // Fall back to the whole virtual screen. That DC's origin is the
                // top-left of the virtual desktop, not of this display, so the blit
                // has to be offset by the display's position.
                displayDc = NativeMethods.GetDC(IntPtr.Zero);
                if (displayDc == IntPtr.Zero)
                {
                    return CaptureResult.Failed(CaptureFailure.Unavailable, "No display device context.");
                }

                blitOriginX = bounds.X;
                blitOriginY = bounds.Y;
            }

            memoryDc = NativeMethods.CreateCompatibleDC(displayDc);
            if (memoryDc == IntPtr.Zero)
            {
                return CaptureResult.Failed(CaptureFailure.Error, "CreateCompatibleDC failed.");
            }

            NativeMethods.BITMAPINFOHEADER header = new()
            {
                biSize = (uint)sizeof(NativeMethods.BITMAPINFOHEADER),
                biWidth = bounds.Width,

                // Negative height for a top-down DIB, matching texture row order.
                biHeight = -bounds.Height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB,
            };

            void* bits;
            bitmap = NativeMethods.CreateDIBSection(
                displayDc,
                &header,
                NativeMethods.DIB_RGB_COLORS,
                &bits,
                IntPtr.Zero,
                0);

            if (bitmap == IntPtr.Zero || bits is null)
            {
                return CaptureResult.Failed(CaptureFailure.Error, "CreateDIBSection failed.");
            }

            previous = NativeMethods.SelectObject(memoryDc, bitmap);

            // CAPTUREBLT includes layered windows, which is what makes the snapshot
            // match what the user is actually looking at.
            if (!NativeMethods.BitBlt(
                    memoryDc,
                    0,
                    0,
                    bounds.Width,
                    bounds.Height,
                    displayDc,
                    blitOriginX,
                    blitOriginY,
                    NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
            {
                return CaptureResult.Failed(CaptureFailure.Error, "BitBlt failed.");
            }

            NativeMethods.GdiFlush();

            int stride = bounds.Width * 4;
            int required = stride * bounds.Height;
            EnsureBuffer(bounds);

            new ReadOnlySpan<byte>(bits, required).CopyTo(_buffer);

            // BitBlt leaves the alpha byte undefined; the shader's occlusion maths
            // assumes an opaque snapshot, so force it.
            fixed (byte* destination = _buffer)
            {
                uint* pixels = (uint*)destination;
                int count = required / 4;
                for (int i = 0; i < count; i++)
                {
                    pixels[i] |= 0xFF00_0000u;
                }
            }

            if (!snapshot.Upload(new ReadOnlySpan<byte>(_buffer, 0, required), stride))
            {
                return CaptureResult.Failed(CaptureFailure.Error, "Uploading the GDI frame failed.");
            }

            double elapsed = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            return CaptureResult.Ok(elapsed);
        }
        catch (Exception ex)
        {
            _log.Error("GDI capture threw.", ex);
            return CaptureResult.Failed(CaptureFailure.Error, ex.Message);
        }
        finally
        {
            if (memoryDc != IntPtr.Zero)
            {
                if (previous != IntPtr.Zero)
                {
                    NativeMethods.SelectObject(memoryDc, previous);
                }

                NativeMethods.DeleteDC(memoryDc);
            }

            if (bitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
            }

            if (displayDc != IntPtr.Zero)
            {
                if (displayDcOwned)
                {
                    NativeMethods.DeleteDC(displayDc);
                }
                else
                {
                    NativeMethods.ReleaseDC(IntPtr.Zero, displayDc);
                }
            }
        }
    }

    private void EnsureBuffer(DisplayBounds bounds)
    {
        int required = bounds.Width * 4 * bounds.Height;
        if (required > 0 && _buffer.Length < required)
        {
            _buffer = new byte[required];
        }
    }

    public void Invalidate()
    {
        // Nothing cached between captures beyond the staging buffer, which is
        // resized on demand.
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _buffer = Array.Empty<byte>();
    }
}
