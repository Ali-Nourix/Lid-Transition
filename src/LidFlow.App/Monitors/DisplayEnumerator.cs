using System;
using System.Collections.Generic;
using LidFlow.App.Interop;
using LidFlow.Core.Diagnostics;
using LidFlow.Core.Monitors;
using Vortice.DXGI;

namespace LidFlow.App.Monitors;

/// <summary>A display plus the DXGI coordinates needed to capture and present on it.</summary>
internal sealed class DisplayTarget
{
    public required DisplayInfo Info { get; init; }

    /// <summary>Index of the adapter that drives this output.</summary>
    public required int AdapterIndex { get; init; }

    /// <summary>Index of the output within that adapter.</summary>
    public required int OutputIndex { get; init; }

    public required IntPtr MonitorHandle { get; init; }

    /// <summary>Rotation Windows is applying to this output.</summary>
    public required ModeRotation Rotation { get; init; }

    public override string ToString() => Info.ToString();
}

/// <summary>
/// Enumerates active displays.
/// <para>
/// DXGI is the primary source rather than <c>EnumDisplayMonitors</c>, because it
/// is the one enumeration that gives us everything the rest of the pipeline
/// needs in the same coordinate system it will be used in: the device name, the
/// desktop rectangle in physical pixels, the <c>HMONITOR</c>, the rotation, the
/// colour space, and - crucially - the adapter/output indices that the capture
/// path needs to open a duplication on exactly this panel.
/// </para>
/// <para>
/// Connector technology, which is what actually identifies the built-in panel,
/// is not in DXGI, so it is read separately from the Connecting and Configuring
/// Displays API and matched back by GDI device name.
/// </para>
/// </summary>
internal static class DisplayEnumerator
{
    public static List<DisplayTarget> Enumerate(ILidFlowLog log)
    {
        log ??= NullLog.Instance;
        List<DisplayTarget> results = new(4);

        Dictionary<string, ConnectorInfo> connectors = ReadConnectorInfo(log);

        IDXGIFactory1? factory = null;

        try
        {
            factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                if (factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter).Failure || adapter is null)
                {
                    break;
                }

                try
                {
                    long luid = adapter.Description1.Luid;

                    for (uint outputIndex = 0; ; outputIndex++)
                    {
                        if (adapter.EnumOutputs(outputIndex, out IDXGIOutput? output).Failure || output is null)
                        {
                            break;
                        }

                        try
                        {
                            DisplayTarget? target = Describe(output, connectors, (int)adapterIndex, (int)outputIndex, luid, log);
                            if (target is not null)
                            {
                                results.Add(target);
                            }
                        }
                        finally
                        {
                            output.Dispose();
                        }
                    }
                }
                finally
                {
                    adapter.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            // A failure here means no animation, not a crash: without a display
            // list there is nothing to target and the app simply stays idle.
            log.Error("Display enumeration failed.", ex);
        }
        finally
        {
            factory?.Dispose();
        }

        return results;
    }

    private static DisplayTarget? Describe(
        IDXGIOutput output,
        Dictionary<string, ConnectorInfo> connectors,
        int adapterIndex,
        int outputIndex,
        long adapterLuid,
        ILidFlowLog log)
    {
        OutputDescription description = output.Description;

        if (!description.AttachedToDesktop)
        {
            // Present but not part of the desktop - for example the internal panel
            // while the machine is docked with the lid shut. Not a valid target.
            return null;
        }

        string deviceName = description.DeviceName ?? string.Empty;

        int width = description.DesktopCoordinates.Right - description.DesktopCoordinates.Left;
        int height = description.DesktopCoordinates.Bottom - description.DesktopCoordinates.Top;

        if (width <= 0 || height <= 0)
        {
            return null;
        }

        connectors.TryGetValue(Normalize(deviceName), out ConnectorInfo connector);

        DisplayInfo info = new()
        {
            Id = deviceName,
            FriendlyName = string.IsNullOrWhiteSpace(connector.FriendlyName) ? deviceName : connector.FriendlyName,
            DevicePath = connector.DevicePath ?? string.Empty,
            Bounds = new DisplayBounds(
                description.DesktopCoordinates.Left,
                description.DesktopCoordinates.Top,
                width,
                height),
            IsPrimary = description.DesktopCoordinates.Left == 0 && description.DesktopCoordinates.Top == 0,
            InternalConfidence = connector.Confidence,
            DpiScale = QueryDpiScale(description.Monitor),
            RefreshHz = QueryRefreshRate(deviceName),
            IsHdr = IsHdrColourSpace(output),
            AdapterLuid = adapterLuid,
            OutputIndex = outputIndex,
        };

        log.Debug($"Display: {info} rotation={description.Rotation} adapter={adapterIndex} output={outputIndex}");

        return new DisplayTarget
        {
            Info = info,
            AdapterIndex = adapterIndex,
            OutputIndex = outputIndex,
            MonitorHandle = description.Monitor,
            Rotation = description.Rotation,
        };
    }

    /// <summary>
    /// True when the output is currently presenting in an HDR colour space.
    /// <para>
    /// Only the ST.2084 (PQ) spaces count: an SDR panel reports
    /// <c>RgbFullG22NoneP709</c>. This decides whether the pipeline runs in FP16
    /// scRGB or 8-bit sRGB.
    /// </para>
    /// </summary>
    private static bool IsHdrColourSpace(IDXGIOutput output)
    {
        try
        {
            using IDXGIOutput6? output6 = output.QueryInterfaceOrNull<IDXGIOutput6>();
            if (output6 is null)
            {
                return false;
            }

            ColorSpaceType space = output6.Description1.ColorSpace;
            return space is ColorSpaceType.RgbFullG2084NoneP2020 or ColorSpaceType.RgbStudioG2084NoneP2020;
        }
        catch (Exception)
        {
            // IDXGIOutput6 needs Windows 10 1703; absence just means "assume SDR".
            return false;
        }
    }

    private static unsafe float QueryDpiScale(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero)
        {
            return 1f;
        }

        uint dpiX;
        uint dpiY;

        if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, &dpiX, &dpiY) != 0 || dpiX == 0)
        {
            return 1f;
        }

        return dpiX / 96f;
    }

    private static unsafe int QueryRefreshRate(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
        {
            return 0;
        }

        NativeMethods.DEVMODEW mode = default;
        mode.dmSize = (ushort)sizeof(NativeMethods.DEVMODEW);

        fixed (char* name = deviceName)
        {
            if (!NativeMethods.EnumDisplaySettingsExW(name, NativeMethods.ENUM_CURRENT_SETTINGS, &mode, 0))
            {
                return 0;
            }
        }

        return (int)mode.dmDisplayFrequency;
    }

    private readonly struct ConnectorInfo
    {
        public ConnectorInfo(InternalPanelConfidence confidence, string friendlyName, string devicePath)
        {
            Confidence = confidence;
            FriendlyName = friendlyName;
            DevicePath = devicePath;
        }

        public InternalPanelConfidence Confidence { get; }

        public string FriendlyName { get; }

        public string DevicePath { get; }
    }

    /// <summary>
    /// Maps GDI device name to connector technology using QueryDisplayConfig.
    /// <para>
    /// This is the only reliable way to tell the built-in panel from an external
    /// monitor. <c>DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL</c> and LVDS mean the
    /// panel is wired into the chassis; the embedded DisplayPort and embedded UDI
    /// connectors mean the same thing in practice but are reported by some
    /// firmware for external-capable ports, so they are ranked one step lower and
    /// the selector prefers a Certain match when both exist.
    /// </para>
    /// </summary>
    private static unsafe Dictionary<string, ConnectorInfo> ReadConnectorInfo(ILidFlowLog log)
    {
        Dictionary<string, ConnectorInfo> map = new(4, StringComparer.OrdinalIgnoreCase);

        try
        {
            uint pathCount;
            uint modeCount;

            if (NativeMethods.GetDisplayConfigBufferSizes(
                    NativeMethods.QDC_ONLY_ACTIVE_PATHS, &pathCount, &modeCount) != 0)
            {
                log.Warn("GetDisplayConfigBufferSizes failed; internal panel detection unavailable.");
                return map;
            }

            if (pathCount == 0)
            {
                return map;
            }

            NativeMethods.DISPLAYCONFIG_PATH_INFO[] paths = new NativeMethods.DISPLAYCONFIG_PATH_INFO[pathCount];
            NativeMethods.DISPLAYCONFIG_MODE_INFO[] modes = new NativeMethods.DISPLAYCONFIG_MODE_INFO[Math.Max(modeCount, 1)];

            fixed (NativeMethods.DISPLAYCONFIG_PATH_INFO* pathPtr = paths)
            fixed (NativeMethods.DISPLAYCONFIG_MODE_INFO* modePtr = modes)
            {
                if (NativeMethods.QueryDisplayConfig(
                        NativeMethods.QDC_ONLY_ACTIVE_PATHS,
                        &pathCount,
                        pathPtr,
                        &modeCount,
                        modePtr,
                        IntPtr.Zero) != 0)
                {
                    log.Warn("QueryDisplayConfig failed; internal panel detection unavailable.");
                    return map;
                }

                for (uint i = 0; i < pathCount; i++)
                {
                    NativeMethods.DISPLAYCONFIG_PATH_INFO path = pathPtr[i];

                    string? gdiName = QuerySourceName(path);
                    if (string.IsNullOrEmpty(gdiName))
                    {
                        continue;
                    }

                    QueryTargetName(path, out string friendlyName, out string devicePath, out uint technology);

                    // The path's own outputTechnology is authoritative; the target
                    // name query is only for the human-readable strings, and can
                    // legitimately fail on a forced/headless target.
                    uint effectiveTechnology = path.TargetInfo.OutputTechnology;
                    if (effectiveTechnology == 0 && technology != 0)
                    {
                        effectiveTechnology = technology;
                    }

                    map[Normalize(gdiName)] = new ConnectorInfo(
                        Classify(effectiveTechnology),
                        friendlyName,
                        devicePath);
                }
            }
        }
        catch (Exception ex)
        {
            log.Error("Reading display connector information failed.", ex);
        }

        return map;
    }

    private static InternalPanelConfidence Classify(uint outputTechnology) => outputTechnology switch
    {
        NativeMethods.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL => InternalPanelConfidence.Certain,
        NativeMethods.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS => InternalPanelConfidence.Certain,
        NativeMethods.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED => InternalPanelConfidence.Likely,
        NativeMethods.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED => InternalPanelConfidence.Likely,
        _ => InternalPanelConfidence.No,
    };

    private static unsafe string? QuerySourceName(NativeMethods.DISPLAYCONFIG_PATH_INFO path)
    {
        NativeMethods.DISPLAYCONFIG_SOURCE_DEVICE_NAME request = default;
        request.Header.Type = NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
        request.Header.Size = (uint)sizeof(NativeMethods.DISPLAYCONFIG_SOURCE_DEVICE_NAME);
        request.Header.AdapterId = path.SourceInfo.AdapterId;
        request.Header.Id = path.SourceInfo.Id;

        if (NativeMethods.DisplayConfigGetDeviceInfo(&request.Header) != 0)
        {
            return null;
        }

        return new string(request.ViewGdiDeviceName, 0, Length(request.ViewGdiDeviceName, 32));
    }

    private static unsafe void QueryTargetName(
        NativeMethods.DISPLAYCONFIG_PATH_INFO path,
        out string friendlyName,
        out string devicePath,
        out uint outputTechnology)
    {
        friendlyName = string.Empty;
        devicePath = string.Empty;
        outputTechnology = 0;

        NativeMethods.DISPLAYCONFIG_TARGET_DEVICE_NAME request = default;
        request.Header.Type = NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
        request.Header.Size = (uint)sizeof(NativeMethods.DISPLAYCONFIG_TARGET_DEVICE_NAME);
        request.Header.AdapterId = path.TargetInfo.AdapterId;
        request.Header.Id = path.TargetInfo.Id;

        if (NativeMethods.DisplayConfigGetDeviceInfo(&request.Header) != 0)
        {
            return;
        }

        outputTechnology = request.OutputTechnology;
        friendlyName = new string(request.MonitorFriendlyDeviceName, 0, Length(request.MonitorFriendlyDeviceName, 64));
        devicePath = new string(request.MonitorDevicePath, 0, Length(request.MonitorDevicePath, 128));
    }

    private static unsafe int Length(char* buffer, int capacity)
    {
        for (int i = 0; i < capacity; i++)
        {
            if (buffer[i] == '\0')
            {
                return i;
            }
        }

        return capacity;
    }

    /// <summary>
    /// DXGI and the CCD API both report <c>\\.\DISPLAYn</c>, but not always with
    /// identical trailing content, so compare on the trimmed value.
    /// </summary>
    private static string Normalize(string deviceName) => deviceName.Trim().TrimEnd('\0');
}
