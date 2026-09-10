using System;
using System.Diagnostics;
using LidFlow.App.Monitors;
using LidFlow.App.Rendering;
using LidFlow.Core.Diagnostics;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LidFlow.App.Capture;

/// <summary>
/// Desktop capture through the DXGI Desktop Duplication API. This is the default
/// backend.
/// <para>
/// Chosen over Windows.Graphics.Capture for one decisive reason: WGC draws a
/// yellow capture border around the display, and turning that off requires the
/// <c>graphicsCaptureWithoutBorder</c> capability in a <i>package manifest</i>,
/// which an unpackaged desktop app does not have. A coloured border flashing
/// around the panel immediately before the animation would be exactly the kind of
/// visible chrome this effect must not have.
/// </para>
/// <para>
/// Duplication also gives us the frame as a GPU texture on the adapter we are
/// already rendering on, so acquiring a snapshot is a single GPU-to-GPU copy with
/// no readback. Its limitations are real and documented: the surface is always
/// BGRA8 - so HDR is flattened - and it is delivered un-rotated, with the
/// rotation corrected at sample time in the shader.
/// </para>
/// </summary>
internal sealed class DuplicationSnapshotSource : ISnapshotSource
{
    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
    private const int DxgiErrorAccessLost = unchecked((int)0x887A0026);
    private const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005);
    private const int DxgiErrorDeviceReset = unchecked((int)0x887A0007);
    private const int DxgiErrorSessionDisconnected = unchecked((int)0x887A0028);
    private const int DxgiErrorNotCurrentlyAvailable = unchecked((int)0x887A0022);

    private readonly GraphicsDevice _graphics;
    private readonly ILidFlowLog _log;

    private IDXGIOutputDuplication? _duplication;
    private string? _duplicatedDisplayId;
    private bool _holdingFrame;
    private bool _disposed;

    public DuplicationSnapshotSource(GraphicsDevice graphics, ILidFlowLog log)
    {
        _graphics = graphics ?? throw new ArgumentNullException(nameof(graphics));
        _log = log ?? NullLog.Instance;
    }

    public string Name => "DesktopDuplication";

    /// <summary>
    /// False: the duplicated surface is documented as always
    /// <c>DXGI_FORMAT_B8G8R8A8_UNORM</c> "no matter what the current display mode
    /// is", so an HDR desktop is flattened to 8-bit SDR.
    /// </summary>
    public bool SupportsHdr => false;

    /// <summary>
    /// True: windows marked <c>WDA_EXCLUDEFROMCAPTURE</c> are omitted from the
    /// duplicated image, which is what allows the opening animation to capture the
    /// live desktop from behind the still-black overlay.
    /// </summary>
    public bool HonoursCaptureExclusion => true;

    public void Warm(DisplayTarget display)
    {
        try
        {
            EnsureDuplication(display);
        }
        catch (Exception ex)
        {
            _log.Debug($"Warming desktop duplication failed: {ex.Message}");
        }
    }

    public CaptureResult TryCapture(DisplayTarget display, DesktopSnapshot snapshot, int timeoutMs)
    {
        long start = Stopwatch.GetTimestamp();

        try
        {
            if (!EnsureDuplication(display))
            {
                return CaptureResult.Failed(CaptureFailure.Unavailable, "Output duplication could not be created.");
            }

            if (!snapshot.EnsureSurface(display.Info.Bounds.Width, display.Info.Bounds.Height, hdr: false))
            {
                return CaptureResult.Failed(CaptureFailure.Error, "Snapshot surface allocation failed.");
            }

            // A frame may still be held from a previous attempt that threw; the API
            // requires releasing before acquiring again.
            ReleaseHeldFrame();

            // Every acquired surface contains the complete current desktop - the
            // dirty and move rectangles only describe what changed - so a single
            // successful acquire is all we need, and a "pointer moved only" frame is
            // just as usable as any other.
            int deadlineMs = Math.Max(timeoutMs, 1);
            int perAttempt = Math.Max(deadlineMs / 3, 4);
            long deadline = start + (long)(Stopwatch.Frequency * (deadlineMs / 1000.0));

            do
            {
                Result result = _duplication!.AcquireNextFrame(
                    (uint)perAttempt,
                    out OutduplFrameInfo _,
                    out IDXGIResource? resource);

                if (result.Success && resource is not null)
                {
                    try
                    {
                        using ID3D11Texture2D texture = resource.QueryInterface<ID3D11Texture2D>();
                        _holdingFrame = true;

                        if (!snapshot.CopyFrom(texture))
                        {
                            return CaptureResult.Failed(CaptureFailure.Error, "Copying the duplicated frame failed.");
                        }
                    }
                    finally
                    {
                        resource.Dispose();

                        // The acquired surface is only valid until ReleaseFrame, and
                        // the copy above is already queued, so release immediately.
                        ReleaseHeldFrame();
                    }

                    double elapsed = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                    return CaptureResult.Ok(elapsed);
                }

                resource?.Dispose();

                switch (result.Code)
                {
                    case DxgiErrorWaitTimeout:
                        // Nothing on the desktop changed. Keep waiting until the
                        // deadline; if it expires the caller falls back to a backend
                        // that does not depend on desktop updates.
                        continue;

                    case DxgiErrorAccessLost:
                    case DxgiErrorSessionDisconnected:
                        _log.Info($"Desktop duplication access lost ({result}); it will be recreated.");
                        DisposeDuplication();
                        return CaptureResult.Failed(CaptureFailure.AccessLost, result.ToString());

                    case DxgiErrorDeviceRemoved:
                    case DxgiErrorDeviceReset:
                        DisposeDuplication();
                        return CaptureResult.Failed(CaptureFailure.DeviceLost, result.ToString());

                    default:
                        DisposeDuplication();
                        return CaptureResult.Failed(CaptureFailure.Error, result.ToString());
                }
            }
            while (Stopwatch.GetTimestamp() < deadline);

            return CaptureResult.Failed(
                CaptureFailure.Timeout,
                $"No desktop frame within {deadlineMs} ms.");
        }
        catch (SharpGenException ex)
        {
            DisposeDuplication();
            return CaptureResult.Failed(CaptureFailure.Error, ex.Message);
        }
        catch (Exception ex)
        {
            DisposeDuplication();
            _log.Error("Desktop duplication capture threw.", ex);
            return CaptureResult.Failed(CaptureFailure.Error, ex.Message);
        }
    }

    private bool EnsureDuplication(DisplayTarget display)
    {
        if (_duplication is not null && _duplicatedDisplayId == display.Info.Id)
        {
            return true;
        }

        DisposeDuplication();

        IDXGIFactory1? factory = null;
        IDXGIAdapter1? adapter = null;
        IDXGIOutput? output = null;
        IDXGIOutput1? output1 = null;

        try
        {
            factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            if (factory.EnumAdapters1((uint)display.AdapterIndex, out adapter).Failure || adapter is null)
            {
                return false;
            }

            if (adapter.EnumOutputs((uint)display.OutputIndex, out output).Failure || output is null)
            {
                return false;
            }

            output1 = output.QueryInterfaceOrNull<IDXGIOutput1>();
            if (output1 is null)
            {
                _log.Warn("IDXGIOutput1 is unavailable; desktop duplication cannot be used.");
                return false;
            }

            // DuplicateOutput requires a device created on the adapter that owns
            // this output, which GraphicsDevice guarantees by construction.
            _duplication = output1.DuplicateOutput(_graphics.Device);
            _duplicatedDisplayId = display.Info.Id;
            _holdingFrame = false;

            _log.Debug($"Desktop duplication opened for {display.Info.Id}.");
            return true;
        }
        catch (SharpGenException ex)
        {
            if (ex.ResultCode.Code == DxgiErrorNotCurrentlyAvailable)
            {
                // The documented limit on concurrent duplications for an output has
                // been reached by other applications.
                _log.Warn("Desktop duplication is not currently available (another application holds it).");
            }
            else
            {
                _log.Warn($"DuplicateOutput failed: {ex.ResultCode}.");
            }

            DisposeDuplication();
            return false;
        }
        finally
        {
            output1?.Dispose();
            output?.Dispose();
            adapter?.Dispose();
            factory?.Dispose();
        }
    }

    private void ReleaseHeldFrame()
    {
        if (!_holdingFrame || _duplication is null)
        {
            return;
        }

        try
        {
            _duplication.ReleaseFrame();
        }
        catch (Exception)
        {
            // Releasing can legitimately fail once access has been lost; the
            // duplication object is discarded in that case anyway.
        }

        _holdingFrame = false;
    }

    private void DisposeDuplication()
    {
        ReleaseHeldFrame();
        _duplication?.Dispose();
        _duplication = null;
        _duplicatedDisplayId = null;
    }

    public void Invalidate() => DisposeDuplication();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeDuplication();
    }
}
