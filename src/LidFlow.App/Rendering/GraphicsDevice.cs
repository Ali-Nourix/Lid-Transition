using System;
using System.IO;
using System.Reflection;
using LidFlow.Core.Diagnostics;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;

namespace LidFlow.App.Rendering;

/// <summary>Raised when a shader fails to compile, which is a programming error rather than a runtime condition.</summary>
internal sealed class ShaderCompilationException : Exception
{
    public ShaderCompilationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Owns the Direct3D 11 device, the DXGI factory and the DirectComposition
/// device, and compiles the transition shader.
/// <para>
/// Created once, at startup, and kept warm for the life of the process. That is
/// deliberate: the window in which a lid animation is visible is only as long as
/// the panel stays lit while the hinge is closing, so there is no budget to
/// create a device when the event arrives.
/// </para>
/// </summary>
internal sealed class GraphicsDevice : IDisposable
{
    private const string ShaderResourceName = "LidFlow.Shaders.LidTransition.hlsl";

    private readonly ILidFlowLog _log;
    private bool _disposed;

    private GraphicsDevice(
        ID3D11Device device,
        ID3D11DeviceContext context,
        IDXGIDevice dxgiDevice,
        IDXGIFactory2 factory,
        IDCompositionDevice composition,
        FeatureLevel featureLevel,
        string adapterName,
        bool isSoftware,
        ILidFlowLog log)
    {
        Device = device;
        Context = context;
        DxgiDevice = dxgiDevice;
        Factory = factory;
        Composition = composition;
        FeatureLevel = featureLevel;
        AdapterName = adapterName;
        IsSoftware = isSoftware;
        _log = log;
    }

    public ID3D11Device Device { get; }

    public ID3D11DeviceContext Context { get; }

    public IDXGIDevice DxgiDevice { get; }

    public IDXGIFactory2 Factory { get; }

    /// <summary>
    /// The DirectComposition device that composes the overlay's swap chain.
    /// <para>
    /// DirectComposition rather than an HWND swap chain because it lets the
    /// overlay's content be committed <i>before</i> the window is shown. With no
    /// redirection surface and an empty visual tree there is no intermediate
    /// state for DWM to put on screen, which is how the transition starts without
    /// a black frame.
    /// </para>
    /// </summary>
    public IDCompositionDevice Composition { get; }

    public FeatureLevel FeatureLevel { get; }

    public string AdapterName { get; }

    /// <summary>True when we fell back to the WARP software rasterizer.</summary>
    public bool IsSoftware { get; }

    public ID3D11VertexShader? VertexShader { get; private set; }

    public ID3D11PixelShader? PixelShader { get; private set; }

    public ID3D11SamplerState? LinearClamp { get; private set; }

    /// <summary>
    /// Creates the device, falling back through hardware then WARP. Returns null
    /// when neither works, in which case the app runs without any animation
    /// rather than failing to start.
    /// </summary>
    /// <param name="adapterLuid">
    /// LUID of the adapter that drives the display the transition will run on, or
    /// null for the default adapter.
    /// <para>
    /// Pinning the device to that specific adapter is not a micro-optimization, it
    /// is what makes capture work at all on a hybrid-graphics laptop.
    /// <c>IDXGIOutput1::DuplicateOutput</c> requires a device created on the
    /// adapter that owns the output, and on those machines the built-in panel
    /// hangs off the integrated GPU while the default adapter is often the
    /// discrete one. Using the panel's own adapter for both capture and
    /// presentation avoids the cross-adapter shared-texture path entirely.
    /// </para>
    /// </param>
    public static GraphicsDevice? TryCreate(ILidFlowLog log, long? adapterLuid = null)
    {
        log ??= NullLog.Instance;

        GraphicsDevice? device = TryCreate(DriverType.Hardware, log, adapterLuid);

        if (device is null && adapterLuid is not null)
        {
            log.Warn("Could not create a device on the display's own adapter; trying the default adapter.");
            device = TryCreate(DriverType.Hardware, log, null);
        }

        if (device is null)
        {
            log.Warn("Hardware Direct3D 11 device unavailable; falling back to the WARP software rasterizer.");
            device = TryCreate(DriverType.Warp, log, null);
        }

        if (device is null)
        {
            log.Error("No usable Direct3D 11 device. The transition will be skipped entirely.");
            return null;
        }

        try
        {
            device.CompileShaders();
        }
        catch (Exception ex)
        {
            log.Error("Shader initialization failed.", ex);
            device.Dispose();
            return null;
        }

        log.Info($"Graphics device ready: {device.AdapterName} ({device.FeatureLevel}{(device.IsSoftware ? ", software" : string.Empty)}).");
        return device;
    }

    private static GraphicsDevice? TryCreate(DriverType driverType, ILidFlowLog log, long? adapterLuid)
    {
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        IDXGIDevice? dxgiDevice = null;
        IDXGIAdapter? adapter = null;
        IDXGIFactory2? factory = null;
        IDCompositionDevice? composition = null;
        IDXGIAdapter1? requestedAdapter = null;

        try
        {
            FeatureLevel[] levels =
            {
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1,
                FeatureLevel.Level_10_0,
            };

            // BgraSupport is required for DirectComposition interop. SingleThreaded
            // is safe and slightly cheaper because every device call in this app is
            // made from the UI thread.
            DeviceCreationFlags flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.Singlethreaded;

            // A specific adapter and DriverType.Hardware are mutually exclusive in
            // D3D11CreateDevice: passing an adapter requires DriverType.Unknown.
            requestedAdapter = adapterLuid is null ? null : FindAdapter(adapterLuid.Value);
            DriverType effectiveDriverType = requestedAdapter is null ? driverType : DriverType.Unknown;

            Result result = D3D11.D3D11CreateDevice(
                requestedAdapter,
                effectiveDriverType,
                flags,
                levels,
                out device,
                out FeatureLevel featureLevel,
                out context);

            if (result.Failure || device is null || context is null)
            {
                log.Debug($"D3D11CreateDevice({driverType}) failed: {result}.");
                return null;
            }

            dxgiDevice = device.QueryInterface<IDXGIDevice>();
            adapter = dxgiDevice.GetAdapter();
            factory = adapter.GetParent<IDXGIFactory2>();

            string adapterName = adapter.Description.Description ?? driverType.ToString();

            composition = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);

            GraphicsDevice graphics = new(
                device,
                context,
                dxgiDevice,
                factory,
                composition,
                featureLevel,
                adapterName,
                driverType == DriverType.Warp,
                log)
            {
                AdapterLuid = adapter.Description.Luid,
            };

            adapter.Dispose();
            requestedAdapter?.Dispose();
            return graphics;
        }
        catch (Exception ex)
        {
            log.Debug($"Graphics device creation ({driverType}) threw: {ex.Message}");

            requestedAdapter?.Dispose();
            composition?.Dispose();
            factory?.Dispose();
            adapter?.Dispose();
            dxgiDevice?.Dispose();
            context?.Dispose();
            device?.Dispose();
            return null;
        }
    }

    /// <summary>Finds an adapter by LUID, or null when it is no longer present.</summary>
    private static IDXGIAdapter1? FindAdapter(long luid)
    {
        IDXGIFactory1? factory = null;

        try
        {
            factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            for (uint index = 0; ; index++)
            {
                if (factory.EnumAdapters1(index, out IDXGIAdapter1? candidate).Failure || candidate is null)
                {
                    return null;
                }

                if (candidate.Description1.Luid == luid)
                {
                    return candidate;
                }

                candidate.Dispose();
            }
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            factory?.Dispose();
        }
    }

    /// <summary>The LUID of the adapter this device was created on.</summary>
    public long AdapterLuid { get; private set; }

    /// <summary>
    /// Compiles the embedded HLSL and creates the pipeline state that does not
    /// depend on a particular display.
    /// <para>
    /// The shader is compiled at startup rather than at build time so the project
    /// needs nothing but the .NET SDK to build - no Windows SDK, no fxc. It costs
    /// a few milliseconds once, and never on the transition's critical path.
    /// </para>
    /// </summary>
    private void CompileShaders()
    {
        string source = ReadEmbeddedShader();

        // Feature level 10 hardware cannot run shader model 5 bytecode. The shader
        // itself is written to be valid under both profiles.
        bool sm5 = FeatureLevel >= FeatureLevel.Level_11_0;
        string vsProfile = sm5 ? "vs_5_0" : "vs_4_0";
        string psProfile = sm5 ? "ps_5_0" : "ps_4_0";

        byte[] vertexBytecode = Compile(source, "FullscreenVS", vsProfile);
        byte[] pixelBytecode = Compile(source, "TransitionPS", psProfile);

        VertexShader = Device.CreateVertexShader(vertexBytecode);
        PixelShader = Device.CreatePixelShader(pixelBytecode);

        // Clamp addressing matters: the blur reaches outside the snapshot near the
        // panel border, and wrapping would drag the opposite edge into frame.
        LinearClamp = Device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0f,
            MaxLOD = float.MaxValue,
        });
    }

    private static byte[] Compile(string source, string entryPoint, string profile)
    {
        Result result = Compiler.Compile(
            source,
            entryPoint,
            "LidTransition.hlsl",
            profile,
            out Vortice.Direct3D.Blob? bytecode,
            out Vortice.Direct3D.Blob? errors);

        try
        {
            if (result.Failure || bytecode is null)
            {
                string detail = errors is not null ? errors.AsString() : result.Description;
                throw new ShaderCompilationException($"Compiling {entryPoint} ({profile}) failed: {detail}");
            }

            return bytecode.AsBytes();
        }
        finally
        {
            bytecode?.Dispose();
            errors?.Dispose();
        }
    }

    private static string ReadEmbeddedShader()
    {
        Assembly assembly = typeof(GraphicsDevice).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(ShaderResourceName);

        if (stream is null)
        {
            throw new ShaderCompilationException($"Embedded shader '{ShaderResourceName}' is missing from the assembly.");
        }

        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Whether the device has been lost (driver reset, GPU removed, or a
    /// hibernate/resume that tore the adapter down). The caller rebuilds
    /// everything rather than trying to salvage individual resources.
    /// </summary>
    public bool IsDeviceLost()
    {
        try
        {
            Result reason = Device.DeviceRemovedReason;
            return reason.Failure;
        }
        catch (Exception)
        {
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        LinearClamp?.Dispose();
        PixelShader?.Dispose();
        VertexShader?.Dispose();

        Composition.Dispose();
        Factory.Dispose();
        DxgiDevice.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
