using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LidFlow.App.Interop;
using LidFlow.App.Monitors;
using LidFlow.App.Rendering;
using LidFlow.Core.Diagnostics;
using LidFlow.Core.Monitors;
using SharpGen.Runtime;
using Vortice.DirectComposition;
using Vortice.DXGI;

namespace LidFlow.App.Overlay;

/// <summary>
/// The borderless, click-through, capture-excluded window the transition is drawn
/// into, plus its DirectComposition visual and swap chain.
/// <para>
/// Window styles, and why each one is there:
/// </para>
/// <list type="bullet">
/// <item><c>WS_POPUP</c> - no frame, no caption, no non-client area at all.</item>
/// <item><c>WS_EX_TOPMOST</c> - above ordinary windows for the duration of the
/// transition.</item>
/// <item><c>WS_EX_NOACTIVATE</c> - showing the overlay must never change which
/// application has focus.</item>
/// <item><c>WS_EX_TOOLWINDOW</c> - keeps it out of the taskbar and Alt+Tab.</item>
/// <item><c>WS_EX_TRANSPARENT</c> - hit-testing passes through to whatever is
/// underneath, so clicks and hovers are unaffected. Backed up by returning
/// <c>HTTRANSPARENT</c> from <c>WM_NCHITTEST</c>, because the style alone is
/// ignored by some injected input paths.</item>
/// <item><c>WS_EX_NOREDIRECTIONBITMAP</c> - no redirection surface is allocated,
/// which is what lets the window exist on screen with nothing to show rather
/// than briefly presenting an uninitialized buffer.</item>
/// </list>
/// <para>
/// The window is created once and kept hidden between transitions. It is never
/// left topmost over an idle desktop, because a permanently-topmost window can
/// interfere with exclusive-fullscreen presentation.
/// </para>
/// </summary>
internal sealed unsafe class OverlayWindow : IDisposable
{
    private const string WindowClassName = "LidFlow.Overlay";
    private static ushort _registeredClass;
    private static readonly object ClassLock = new();

    private readonly GraphicsDevice _graphics;
    private readonly ILidFlowLog _log;

    private IDCompositionTarget? _target;
    private IDCompositionVisual? _visual;
    private IDXGISwapChain1? _swapChain;
    private Vortice.Direct3D11.ID3D11RenderTargetView? _renderTargetView;

    private DisplayBounds _bounds;
    private bool _visible;
    private bool _disposed;

    private OverlayWindow(IntPtr handle, GraphicsDevice graphics, ILidFlowLog log)
    {
        Handle = handle;
        _graphics = graphics;
        _log = log;
    }

    public IntPtr Handle { get; }

    public DisplayBounds Bounds => _bounds;

    public bool IsVisible => _visible;

    /// <summary>
    /// True when <c>WDA_EXCLUDEFROMCAPTURE</c> was accepted, meaning the overlay is
    /// invisible to desktop capture.
    /// <para>
    /// When false - Windows 10 before version 2004 - the opening animation cannot
    /// capture the desktop from behind a black overlay, and the capture path has to
    /// hide the overlay around the acquisition instead. The distinction is recorded
    /// here rather than assumed anywhere downstream.
    /// </para>
    /// </summary>
    public bool ExcludedFromCapture { get; private set; }

    /// <summary>Backbuffer format currently in use.</summary>
    public Format BackBufferFormat { get; private set; } = Format.B8G8R8A8_UNorm;

    public static OverlayWindow? TryCreate(GraphicsDevice graphics, ILidFlowLog log)
    {
        log ??= NullLog.Instance;

        try
        {
            EnsureWindowClass();

            IntPtr handle;

            fixed (char* className = WindowClassName)
            fixed (char* windowName = "LidFlow")
            {
                handle = NativeMethods.CreateWindowExW(
                    NativeMethods.WS_EX_TOPMOST
                        | NativeMethods.WS_EX_NOACTIVATE
                        | NativeMethods.WS_EX_TOOLWINDOW
                        | NativeMethods.WS_EX_TRANSPARENT
                        | NativeMethods.WS_EX_NOREDIRECTIONBITMAP,
                    className,
                    windowName,
                    NativeMethods.WS_POPUP,          // deliberately not WS_VISIBLE
                    0,
                    0,
                    1,
                    1,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    NativeMethods.GetModuleHandleW(null),
                    IntPtr.Zero);
            }

            if (handle == IntPtr.Zero)
            {
                log.Error($"CreateWindowEx for the overlay failed (error {Marshal.GetLastWin32Error()}).");
                return null;
            }

            OverlayWindow window = new(handle, graphics, log);
            window.ApplyCaptureExclusion();
            return window;
        }
        catch (Exception ex)
        {
            log.Error("Creating the overlay window failed.", ex);
            return null;
        }
    }

    private static void EnsureWindowClass()
    {
        lock (ClassLock)
        {
            if (_registeredClass != 0)
            {
                return;
            }

            fixed (char* className = WindowClassName)
            {
                NativeMethods.WNDCLASSEXW wndClass = new()
                {
                    cbSize = (uint)sizeof(NativeMethods.WNDCLASSEXW),
                    style = 0,
                    lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WindowProc,
                    hInstance = NativeMethods.GetModuleHandleW(null),

                    // No background brush: the window must never paint anything of
                    // its own. Every pixel comes from the composition swap chain.
                    hbrBackground = IntPtr.Zero,
                    hCursor = IntPtr.Zero,
                    lpszClassName = className,
                };

                _registeredClass = NativeMethods.RegisterClassExW(&wndClass);

                if (_registeredClass == 0)
                {
                    int error = Marshal.GetLastWin32Error();

                    // 1410 is ERROR_CLASS_ALREADY_EXISTS, which is benign.
                    if (error != 1410)
                    {
                        throw new InvalidOperationException($"RegisterClassEx failed with error {error}.");
                    }

                    _registeredClass = 1;
                }
            }
        }
    }

    [UnmanagedCallersOnly]
    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // The overlay is inert. WM_NCHITTEST is answered explicitly so the window
        // is transparent to input even on paths that ignore WS_EX_TRANSPARENT, and
        // close requests are refused because the window's lifetime belongs to the
        // transition state machine, not to the shell.
        switch (msg)
        {
            case NativeMethods.WM_NCHITTEST:
                return NativeMethods.HTTRANSPARENT;

            case NativeMethods.WM_CLOSE:
                return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ApplyCaptureExclusion()
    {
        if (NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE))
        {
            // The call also succeeds on pre-2004 builds, where it silently behaves
            // as WDA_MONITOR, so read the value back and confirm what actually
            // stuck rather than trusting the return code.
            uint affinity = 0;
            if (NativeMethods.GetWindowDisplayAffinity(Handle, &affinity) &&
                affinity == NativeMethods.WDA_EXCLUDEFROMCAPTURE)
            {
                ExcludedFromCapture = true;
                _log.Info("Overlay is excluded from screen capture (WDA_EXCLUDEFROMCAPTURE).");
                return;
            }
        }

        ExcludedFromCapture = false;
        _log.Warn(
            "WDA_EXCLUDEFROMCAPTURE is unavailable (needs Windows 10 2004 or later). " +
            "The overlay will be hidden around each capture instead.");
    }

    /// <summary>
    /// Points the overlay at a display and makes sure the swap chain matches it
    /// exactly. Safe to call repeatedly; the swap chain is only recreated when the
    /// size or format actually changes.
    /// </summary>
    public bool Prepare(DisplayTarget display, bool preferHdr)
    {
        DisplayBounds bounds = display.Info.Bounds;

        if (bounds.IsEmpty)
        {
            return false;
        }

        Format desired = preferHdr && display.Info.IsHdr
            ? Format.R16G16B16A16_Float
            : Format.B8G8R8A8_UNorm;

        try
        {
            // Move first, so a resolution change repositions the window before the
            // swap chain is sized to the new bounds.
            if (_bounds != bounds)
            {
                NativeMethods.SetWindowPos(
                    Handle,
                    NativeMethods.HWND_TOPMOST,
                    bounds.X,
                    bounds.Y,
                    bounds.Width,
                    bounds.Height,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_NOSENDCHANGING);

                _bounds = bounds;
            }

            if (_swapChain is not null && BackBufferFormat != desired)
            {
                ReleaseSwapChain();
            }

            if (_swapChain is null)
            {
                CreateSwapChain(bounds, desired);
            }
            else
            {
                ResizeSwapChain(bounds);
            }

            return _swapChain is not null && _renderTargetView is not null;
        }
        catch (Exception ex)
        {
            _log.Error("Preparing the overlay swap chain failed.", ex);
            return false;
        }
    }

    private void CreateSwapChain(DisplayBounds bounds, Format format)
    {
        SwapChainDescription1 description = new()
        {
            Width = (uint)bounds.Width,
            Height = (uint)bounds.Height,
            Format = format,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,

            // Two buffers is enough for a short, vsync-paced animation and keeps
            // the presentation latency to a single frame.
            BufferCount = 2,

            // Composition swap chains must use Stretch scaling.
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,

            // The overlay is opaque: the shader writes black where the panel
            // occludes the content, so there is nothing to blend with the desktop.
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
            Flags = SwapChainFlags.None,
        };

        _swapChain = _graphics.Factory.CreateSwapChainForComposition(_graphics.Device, description, null);
        BackBufferFormat = format;

        CreateRenderTargetView();

        // Build the visual tree once. From here on, a Present is enough to update
        // what is on screen - no further Commit is needed - which is what keeps the
        // per-frame cost down and the first frame instant.
        _visual ??= _graphics.Composition.CreateVisual();
        _visual.SetContent(_swapChain);

        if (_target is null)
        {
            _graphics.Composition.CreateTargetForHwnd(Handle, true, out _target).CheckError();
            _target.SetRoot(_visual);
        }

        _graphics.Composition.Commit().CheckError();
    }

    private void ResizeSwapChain(DisplayBounds bounds)
    {
        if (_swapChain is null)
        {
            return;
        }

        SwapChainDescription1 current = _swapChain.Description1;
        if (current.Width == (uint)bounds.Width && current.Height == (uint)bounds.Height)
        {
            return;
        }

        _renderTargetView?.Dispose();
        _renderTargetView = null;

        // The back buffers cannot be resized while they are still bound as a
        // render target, so unbind first.
        _graphics.Context.OMSetRenderTargets(
            0,
            Array.Empty<Vortice.Direct3D11.ID3D11RenderTargetView>(),
            null!);

        _swapChain.ResizeBuffers(2, (uint)bounds.Width, (uint)bounds.Height, BackBufferFormat, SwapChainFlags.None)
            .CheckError();

        CreateRenderTargetView();
    }

    private void CreateRenderTargetView()
    {
        if (_swapChain is null)
        {
            return;
        }

        using Vortice.Direct3D11.ID3D11Texture2D backBuffer = _swapChain.GetBuffer<Vortice.Direct3D11.ID3D11Texture2D>(0);

        // An _SRGB render target view over a UNORM flip-model back buffer. This is
        // what lets the shader do all of its darkening in linear light and have the
        // hardware re-encode on write; the alternative - blending in gamma space -
        // is the difference between a panel dimming and a grey wash.
        Format viewFormat = BackBufferFormat == Format.B8G8R8A8_UNorm
            ? Format.B8G8R8A8_UNorm_SRgb
            : BackBufferFormat;

        Vortice.Direct3D11.RenderTargetViewDescription description = new()
        {
            Format = viewFormat,
            ViewDimension = Vortice.Direct3D11.RenderTargetViewDimension.Texture2D,
        };

        _renderTargetView = _graphics.Device.CreateRenderTargetView(backBuffer, description);
    }

    public Vortice.Direct3D11.ID3D11RenderTargetView? RenderTargetView => _renderTargetView;

    /// <summary>
    /// Presents the frame that has just been rendered.
    /// <para>
    /// Synchronized to the compositor so the animation cannot tear, which also
    /// paces the render loop at the panel's real refresh rate - 60 Hz, 120 Hz or
    /// whatever the display is running at - without a timer.
    /// </para>
    /// </summary>
    public bool Present()
    {
        if (_swapChain is null)
        {
            return false;
        }

        Result result = _swapChain.Present(1, PresentFlags.None);

        if (result.Failure)
        {
            if (result.Code == unchecked((int)0x887A0005) || result.Code == unchecked((int)0x887A0007))
            {
                // DXGI_ERROR_DEVICE_REMOVED / DXGI_ERROR_DEVICE_RESET.
                _log.Warn($"Present reported a lost device ({result}).");
                return false;
            }

            _log.Warn($"Present failed: {result}.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Shows the overlay. Only ever called after a frame has been rendered and
    /// presented, so the first thing that appears on screen is a correct frame
    /// rather than an empty buffer.
    /// </summary>
    public void Show()
    {
        if (_visible)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            Handle,
            NativeMethods.HWND_TOPMOST,
            _bounds.X,
            _bounds.Y,
            _bounds.Width,
            _bounds.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOOWNERZORDER);

        _visible = true;
    }

    public void Hide()
    {
        if (!_visible)
        {
            return;
        }

        NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE);
        _visible = false;
    }

    private void ReleaseSwapChain()
    {
        _renderTargetView?.Dispose();
        _renderTargetView = null;

        if (_visual is not null)
        {
            _visual.SetContent(null!);
            _graphics.Composition.Commit();
        }

        _swapChain?.Dispose();
        _swapChain = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Hide();
        ReleaseSwapChain();

        _target?.Dispose();
        _visual?.Dispose();

        if (Handle != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(Handle);
        }
    }
}
