using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using LidFlow.App.Monitors;
using LidFlow.App.Rendering;
using LidFlow.Core.Diagnostics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace LidFlow.App.Capture;

/// <summary>
/// Desktop capture through Windows.Graphics.Capture.
/// <para>
/// Opt-in only, and secondary to Desktop Duplication, for a specific reason: on
/// an unpackaged desktop app Windows draws a yellow capture indicator around the
/// captured display and it cannot be switched off. Disabling it requires setting
/// <c>GraphicsCaptureSession.IsBorderRequired = false</c>, which in turn requires
/// user consent via <c>GraphicsCaptureAccess.RequestAccessAsync(Borderless)</c>,
/// which requires the <c>graphicsCaptureWithoutBorder</c> capability declared in
/// a <i>package manifest</i> - something LidFlow, shipping as a plain executable,
/// does not have.
/// </para>
/// <para>
/// It is still worth having, because it is the only backend that can preserve an
/// HDR desktop: it can deliver FP16 surfaces, whereas Desktop Duplication is
/// documented as always returning BGRA8. On an HDR panel a user who would rather
/// see a brief capture indicator than a tone-flattened snapshot can select it.
/// </para>
/// </summary>
internal sealed class WgcSnapshotSource : ISnapshotSource
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private readonly GraphicsDevice _graphics;
    private readonly ILidFlowLog _log;

    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private string? _capturedDisplayId;
    private bool _hdrSession;
    private bool _disposed;

    public WgcSnapshotSource(GraphicsDevice graphics, ILidFlowLog log)
    {
        _graphics = graphics ?? throw new ArgumentNullException(nameof(graphics));
        _log = log ?? NullLog.Instance;
    }

    public string Name => "WindowsGraphicsCapture";

    public bool SupportsHdr => true;

    /// <summary>
    /// True. Windows.Graphics.Capture omits windows marked
    /// <c>WDA_EXCLUDEFROMCAPTURE</c>, which is the flag's primary purpose.
    /// </summary>
    public bool HonoursCaptureExclusion => true;

    /// <summary>Whether this machine supports the API at all.</summary>
    public static bool IsSupported
    {
        get
        {
            try
            {
                return GraphicsCaptureSession.IsSupported();
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public void Warm(DisplayTarget display)
    {
        try
        {
            EnsureSession(display);
        }
        catch (Exception ex)
        {
            _log.Debug($"Warming Windows.Graphics.Capture failed: {ex.Message}");
        }
    }

    public CaptureResult TryCapture(DisplayTarget display, DesktopSnapshot snapshot, int timeoutMs)
    {
        long start = Stopwatch.GetTimestamp();

        try
        {
            if (!EnsureSession(display))
            {
                return CaptureResult.Failed(CaptureFailure.Unavailable, "Capture session could not be created.");
            }

            if (!snapshot.EnsureSurface(display.Info.Bounds.Width, display.Info.Bounds.Height, _hdrSession))
            {
                return CaptureResult.Failed(CaptureFailure.Error, "Snapshot surface allocation failed.");
            }

            // The frame pool delivers a frame promptly after StartCapture even for a
            // static desktop, but poll rather than assume, so a stalled compositor
            // degrades into a fallback instead of a hang.
            int deadlineMs = Math.Max(timeoutMs, 1);
            long deadline = start + (long)(Stopwatch.Frequency * (deadlineMs / 1000.0));

            do
            {
                using Direct3D11CaptureFrame? frame = _framePool!.TryGetNextFrame();

                if (frame is not null)
                {
                    using Vortice.Direct3D11.ID3D11Texture2D texture = GetTexture(frame.Surface);

                    if (!snapshot.CopyFrom(texture))
                    {
                        return CaptureResult.Failed(CaptureFailure.Error, "Copying the captured frame failed.");
                    }

                    double elapsed = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                    return CaptureResult.Ok(elapsed);
                }

                Thread.Sleep(1);
            }
            while (Stopwatch.GetTimestamp() < deadline);

            return CaptureResult.Failed(CaptureFailure.Timeout, $"No captured frame within {deadlineMs} ms.");
        }
        catch (Exception ex)
        {
            _log.Error("Windows.Graphics.Capture threw.", ex);
            DisposeSession();
            return CaptureResult.Failed(CaptureFailure.Error, ex.Message);
        }
    }

    private bool EnsureSession(DisplayTarget display)
    {
        if (_session is not null && _capturedDisplayId == display.Info.Id)
        {
            return true;
        }

        DisposeSession();

        if (!IsSupported)
        {
            return false;
        }

        try
        {
            _winrtDevice ??= CreateWinRtDevice();

            _item = CreateItemForMonitor(display.MonitorHandle);
            if (_item is null)
            {
                return false;
            }

            _hdrSession = display.Info.IsHdr;

            // FP16 for HDR, which is the entire reason to prefer this backend.
            DirectXPixelFormat format = _hdrSession
                ? DirectXPixelFormat.R16G16B16A16Float
                : DirectXPixelFormat.B8G8R8A8UIntNormalized;

            // CreateFreeThreaded, not Create: the latter requires a DispatcherQueue
            // on the calling thread, which a plain Win32 message loop does not have.
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice,
                format,
                1,
                new SizeInt32(display.Info.Bounds.Width, display.Info.Bounds.Height));

            _session = _framePool.CreateCaptureSession(_item);

            TrySuppressCaptureBorder(_session);

            _session.StartCapture();
            _capturedDisplayId = display.Info.Id;
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"Creating a Windows.Graphics.Capture session failed: {ex.Message}");
            DisposeSession();
            return false;
        }
    }

    /// <summary>
    /// Attempts to turn off the capture indicator. Expected to fail on an
    /// unpackaged build; the attempt is made because it costs nothing and succeeds
    /// if the app is ever repackaged with the required capability.
    /// </summary>
    private void TrySuppressCaptureBorder(GraphicsCaptureSession session)
    {
        try
        {
            if (ApiInformation.IsBorderRequiredPropertyPresent)
            {
                session.IsBorderRequired = false;
                _log.Debug("Requested a borderless capture session.");
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"Capture border cannot be suppressed (expected without a package identity): {ex.Message}");
        }
    }

    private IDirect3DDevice CreateWinRtDevice()
    {
        int hr = CreateDirect3D11DeviceFromDXGIDevice(_graphics.DxgiDevice.NativePointer, out IntPtr inspectable);

        if (hr != 0 || inspectable == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice failed (0x{hr:X8}).");
        }

        try
        {
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    /// <summary>
    /// Wraps an <c>HMONITOR</c> as a <see cref="GraphicsCaptureItem"/>.
    /// <para>
    /// There is no projected API for this: monitor and window items are created
    /// through <c>IGraphicsCaptureItemInterop</c> on the class's activation
    /// factory, which is reached here through <c>RoGetActivationFactory</c> rather
    /// than a projection helper so it does not depend on C#/WinRT internals.
    /// </para>
    /// </summary>
    private GraphicsCaptureItem? CreateItemForMonitor(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero)
        {
            return null;
        }

        const string ClassName = "Windows.Graphics.Capture.GraphicsCaptureItem";

        IntPtr classId = IntPtr.Zero;
        IntPtr factoryPtr = IntPtr.Zero;

        try
        {
            int hr = WindowsCreateString(ClassName, ClassName.Length, out classId);
            if (hr != 0)
            {
                return null;
            }

            Guid interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
            hr = RoGetActivationFactory(classId, ref interopIid, out factoryPtr);

            if (hr != 0 || factoryPtr == IntPtr.Zero)
            {
                _log.Warn($"RoGetActivationFactory for GraphicsCaptureItem failed (0x{hr:X8}).");
                return null;
            }

            IGraphicsCaptureItemInterop interop =
                (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);

            Guid itemIid = GraphicsCaptureItemIid;
            IntPtr itemPtr = interop.CreateForMonitor(monitor, ref itemIid);

            if (itemPtr == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
            }
            finally
            {
                Marshal.Release(itemPtr);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"CreateForMonitor failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (factoryPtr != IntPtr.Zero)
            {
                Marshal.Release(factoryPtr);
            }

            if (classId != IntPtr.Zero)
            {
                WindowsDeleteString(classId);
            }
        }
    }

    /// <summary>Unwraps the D3D11 texture behind a WinRT capture surface.</summary>
    private static Vortice.Direct3D11.ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        IDirect3DDxgiInterfaceAccess access = (IDirect3DDxgiInterfaceAccess)(object)surface;
        Guid iid = typeof(Vortice.Direct3D11.ID3D11Texture2D).GUID;
        IntPtr texturePtr = access.GetInterface(ref iid);

        return new Vortice.Direct3D11.ID3D11Texture2D(texturePtr);
    }

    private void DisposeSession()
    {
        _session?.Dispose();
        _session = null;

        _framePool?.Dispose();
        _framePool = null;

        _item = null;
        _capturedDisplayId = null;
    }

    public void Invalidate() => DisposeSession();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeSession();
        _winrtDevice?.Dispose();
        _winrtDevice = null;
    }

    // ------------------------------------------------------------------ interop

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);

        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(ref Guid iid);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    /// <summary>Feature detection for the borderless-capture property.</summary>
    private static class ApiInformation
    {
        private static readonly bool Present = Detect();

        public static bool IsBorderRequiredPropertyPresent => Present;

        private static bool Detect()
        {
            try
            {
                return Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(
                    "Windows.Graphics.Capture.GraphicsCaptureSession",
                    "IsBorderRequired");
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
