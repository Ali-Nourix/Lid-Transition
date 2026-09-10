using System;
using System.Runtime.InteropServices;

namespace LidFlow.App.Interop;

/// <summary>
/// Win32 entry points used by LidFlow, grouped by the header they come from.
/// <para>
/// Deliberately narrow: nothing here is speculative, every import is used, and
/// the lifetime and failure behaviour of each one is described where it is
/// called. Direct3D, DXGI and DirectComposition are reached through the Vortice
/// bindings instead of being duplicated here.
/// </para>
/// </summary>
internal static unsafe partial class NativeMethods
{
    private const string User32 = "user32.dll";
    private const string Kernel32 = "kernel32.dll";
    private const string Shcore = "shcore.dll";
    private const string Gdi32 = "gdi32.dll";
    private const string Shell32 = "shell32.dll";
    private const string Dwmapi = "dwmapi.dll";

    // ---------------------------------------------------------------- windows

    public const int GWL_EXSTYLE = -20;

    public const uint WS_POPUP = 0x8000_0000;
    public const uint WS_VISIBLE = 0x1000_0000;
    public const uint WS_CLIPCHILDREN = 0x0200_0000;

    public const uint WS_EX_TOPMOST = 0x0000_0008;

    /// <summary>Never becomes the foreground window, so a transition cannot steal focus.</summary>
    public const uint WS_EX_NOACTIVATE = 0x0800_0000;

    /// <summary>Keeps the overlay out of the taskbar and out of Alt+Tab.</summary>
    public const uint WS_EX_TOOLWINDOW = 0x0000_0080;

    /// <summary>Hit-testing falls through to whatever is underneath.</summary>
    public const uint WS_EX_TRANSPARENT = 0x0000_0020;

    public const uint WS_EX_LAYERED = 0x0008_0000;

    /// <summary>
    /// No redirection surface is allocated for the window. Required for the
    /// DirectComposition path, and the reason the overlay can be shown while its
    /// visual tree is still empty without flashing anything: with no redirection
    /// bitmap there is no uninitialized buffer for DWM to put on screen.
    /// </summary>
    public const uint WS_EX_NOREDIRECTIONBITMAP = 0x0020_0000;

    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const uint SWP_NOSENDCHANGING = 0x0400;

    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const uint WM_DESTROY = 0x0002;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_DISPLAYCHANGE = 0x007E;
    public const uint WM_DPICHANGED = 0x02E0;
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_POWERBROADCAST = 0x0218;
    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint WM_NCHITTEST = 0x0084;

    public const int HTTRANSPARENT = -1;

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public IntPtr hIconSm;
    }

    [LibraryImport(User32, EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassExW(WNDCLASSEXW* wndClass);

    [LibraryImport(User32, EntryPoint = "CreateWindowExW", SetLastError = true)]
    public static partial IntPtr CreateWindowExW(
        uint exStyle,
        char* className,
        char* windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [LibraryImport(User32, EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport(User32, EntryPoint = "DefWindowProcW")]
    public static partial IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport(User32, EntryPoint = "DestroyWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(IntPtr hwnd);

    [LibraryImport(User32, EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr hwnd, int cmdShow);

    [LibraryImport(User32, EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [LibraryImport(User32, EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport(User32, EntryPoint = "GetForegroundWindow")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport(User32, EntryPoint = "GetWindowRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hwnd, RECT* rect);

    [LibraryImport(User32, EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

    [LibraryImport(Kernel32, EntryPoint = "GetModuleHandleW", SetLastError = true)]
    public static partial IntPtr GetModuleHandleW(char* moduleName);

    // ------------------------------------------------------- capture exclusion

    public const uint WDA_NONE = 0x0000_0000;
    public const uint WDA_MONITOR = 0x0000_0001;

    /// <summary>
    /// Windows 10 version 2004 and later: the window is composited to the
    /// monitor but omitted from screen capture entirely.
    /// <para>
    /// This is what makes the opening animation possible. The overlay is on
    /// screen and opaque black while a fresh snapshot of the desktop behind it
    /// is captured; without this flag the capture would just return our own
    /// black frame. On builds before 2004 the flag silently degrades to
    /// WDA_MONITOR, which is not equivalent, so the app detects that and falls
    /// back to hiding the overlay around the capture instead.
    /// </para>
    /// </summary>
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x0000_0011;

    [LibraryImport(User32, EntryPoint = "SetWindowDisplayAffinity", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [LibraryImport(User32, EntryPoint = "GetWindowDisplayAffinity", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowDisplayAffinity(IntPtr hwnd, uint* affinity);

    // -------------------------------------------------------------- power

    public const int PBT_APMQUERYSUSPEND = 0x0000;
    public const int PBT_APMSUSPEND = 0x0004;
    public const int PBT_APMRESUMESUSPEND = 0x0007;
    public const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    public const int PBT_POWERSETTINGCHANGE = 0x8013;

    public const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0x0000_0000;

    /// <summary>
    /// POWERBROADCAST_SETTING. <c>Data</c> is a variable-length array; the field
    /// declared here is its first byte, and the payload length is in
    /// <c>DataLength</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }

    [LibraryImport(User32, EntryPoint = "RegisterPowerSettingNotification", SetLastError = true)]
    public static partial IntPtr RegisterPowerSettingNotification(IntPtr recipient, Guid* powerSettingGuid, uint flags);

    [LibraryImport(User32, EntryPoint = "UnregisterPowerSettingNotification", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterPowerSettingNotification(IntPtr handle);

    public const uint ES_CONTINUOUS = 0x8000_0000;
    public const uint ES_DISPLAY_REQUIRED = 0x0000_0002;
    public const uint ES_SYSTEM_REQUIRED = 0x0000_0001;

    /// <summary>
    /// Resets the display/system idle timers.
    /// <para>
    /// Microsoft is explicit that this "cannot be used to prevent the user from
    /// putting the computer to sleep" and that applications "should respect that
    /// the user expects a certain behavior when they close the lid". LidFlow uses
    /// it only to stop the idle timer from blanking the panel in the middle of a
    /// transition that is already running - never to try to survive a lid-close
    /// sleep, which is not possible and is not attempted.
    /// </para>
    /// </summary>
    [LibraryImport(Kernel32, EntryPoint = "SetThreadExecutionState")]
    public static partial uint SetThreadExecutionState(uint flags);

    // -------------------------------------------------------------- hotkeys

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_NOREPEAT = 0x4000;

    [LibraryImport(User32, EntryPoint = "RegisterHotKey", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [LibraryImport(User32, EntryPoint = "UnregisterHotKey", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hwnd, int id);

    // -------------------------------------------------------------- monitors

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    public const int MDT_EFFECTIVE_DPI = 0;

    [LibraryImport(Shcore, EntryPoint = "GetDpiForMonitor")]
    public static partial int GetDpiForMonitor(IntPtr monitor, int dpiType, uint* dpiX, uint* dpiY);

    public const int ENUM_CURRENT_SETTINGS = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODEW
    {
        private const int CCHDEVICENAME = 32;
        private const int CCHFORMNAME = 32;

        public fixed char dmDeviceName[CCHDEVICENAME];
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        public fixed char dmFormName[CCHFORMNAME];
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [LibraryImport(User32, EntryPoint = "EnumDisplaySettingsExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplaySettingsExW(char* deviceName, int modeNum, DEVMODEW* devMode, uint flags);

    // ------------------------------------------- connector technology (CCD API)

    public const uint QDC_ONLY_ACTIVE_PATHS = 0x0000_0002;

    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    // From DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS = 6;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED = 11;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED = 13;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL = 0x8000_0000;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;

        public long ToInt64() => ((long)HighPart << 32) | LowPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint Type;
        public uint Size;
        public LUID AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        public fixed char ViewGdiDeviceName[32];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        public fixed char MonitorFriendlyDeviceName[64];
        public fixed char MonitorDevicePath[128];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public DISPLAYCONFIG_RATIONAL RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO SourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO TargetInfo;
        public uint Flags;
    }

    /// <summary>
    /// DISPLAYCONFIG_MODE_INFO. The trailing union is declared as an opaque blob
    /// because LidFlow never reads it - QueryDisplayConfig simply requires a
    /// correctly-sized array to be supplied alongside the path array.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_MODE_INFO
    {
        public uint InfoType;
        public uint Id;
        public LUID AdapterId;
        public fixed byte Union[48];
    }

    [LibraryImport(User32, EntryPoint = "GetDisplayConfigBufferSizes")]
    public static partial int GetDisplayConfigBufferSizes(uint flags, uint* numPathArrayElements, uint* numModeInfoArrayElements);

    [LibraryImport(User32, EntryPoint = "QueryDisplayConfig")]
    public static partial int QueryDisplayConfig(
        uint flags,
        uint* numPathArrayElements,
        DISPLAYCONFIG_PATH_INFO* pathArray,
        uint* numModeInfoArrayElements,
        DISPLAYCONFIG_MODE_INFO* modeInfoArray,
        IntPtr currentTopologyId);

    [LibraryImport(User32, EntryPoint = "DisplayConfigGetDeviceInfo")]
    public static partial int DisplayConfigGetDeviceInfo(DISPLAYCONFIG_DEVICE_INFO_HEADER* requestPacket);

    // ------------------------------------------------------- fullscreen state

    public const int QUNS_NOT_PRESENT = 1;
    public const int QUNS_BUSY = 2;
    public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    public const int QUNS_PRESENTATION_MODE = 4;
    public const int QUNS_ACCEPTS_NOTIFICATIONS = 5;
    public const int QUNS_QUIET_TIME = 6;
    public const int QUNS_APP = 7;

    /// <summary>
    /// Reports whether a full-screen Direct3D application or presentation mode is
    /// active. Used to skip the transition rather than risk kicking a game out of
    /// exclusive fullscreen with a topmost overlay.
    /// </summary>
    [LibraryImport(Shell32, EntryPoint = "SHQueryUserNotificationState")]
    public static partial int SHQueryUserNotificationState(int* state);

    public const int DWMWA_CLOAKED = 14;

    [LibraryImport(Dwmapi, EntryPoint = "DwmGetWindowAttribute")]
    public static partial int DwmGetWindowAttribute(IntPtr hwnd, int attribute, void* value, int size);

    // -------------------------------------------------------------- cursor

    public const int CURSOR_SHOWING = 0x0000_0001;

    [StructLayout(LayoutKind.Sequential)]
    public struct CURSORINFO
    {
        public uint cbSize;
        public uint flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [LibraryImport(User32, EntryPoint = "GetCursorInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorInfo(CURSORINFO* cursorInfo);

    [LibraryImport(User32, EntryPoint = "DestroyIcon", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr icon);

    [LibraryImport(User32, EntryPoint = "GetIconInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetIconInfo(IntPtr icon, ICONINFO* iconInfo);

    [LibraryImport(User32, EntryPoint = "DrawIconEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DrawIconEx(
        IntPtr hdc,
        int xLeft,
        int yTop,
        IntPtr icon,
        int cxWidth,
        int cyWidth,
        uint istepIfAniCur,
        IntPtr hbrFlickerFreeDraw,
        uint diFlags);

    public const uint DI_NORMAL = 0x0003;

    // ----------------------------------------------------------------- GDI

    public const uint SRCCOPY = 0x00CC_0020;
    public const uint CAPTUREBLT = 0x4000_0000;
    public const int BI_RGB = 0;
    public const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [LibraryImport(User32, EntryPoint = "GetDC")]
    public static partial IntPtr GetDC(IntPtr hwnd);

    [LibraryImport(User32, EntryPoint = "ReleaseDC")]
    public static partial int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [LibraryImport(Gdi32, EntryPoint = "CreateDCW", SetLastError = true)]
    public static partial IntPtr CreateDCW(char* driver, char* device, char* port, IntPtr initData);

    [LibraryImport(Gdi32, EntryPoint = "CreateCompatibleDC", SetLastError = true)]
    public static partial IntPtr CreateCompatibleDC(IntPtr hdc);

    [LibraryImport(Gdi32, EntryPoint = "CreateDIBSection", SetLastError = true)]
    public static partial IntPtr CreateDIBSection(
        IntPtr hdc,
        BITMAPINFOHEADER* bitmapInfo,
        uint usage,
        void** bits,
        IntPtr section,
        uint offset);

    [LibraryImport(Gdi32, EntryPoint = "SelectObject")]
    public static partial IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [LibraryImport(Gdi32, EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr obj);

    [LibraryImport(Gdi32, EntryPoint = "DeleteDC")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(IntPtr hdc);

    [LibraryImport(Gdi32, EntryPoint = "BitBlt", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(
        IntPtr destDc,
        int x,
        int y,
        int cx,
        int cy,
        IntPtr srcDc,
        int x1,
        int y1,
        uint rop);

    /// <summary>GDI BITMAP header, used to measure a cursor's bitmaps.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [LibraryImport(Gdi32, EntryPoint = "GetObjectW")]
    public static partial int GetObjectW(IntPtr handle, int count, void* buffer);

    [LibraryImport(Gdi32, EntryPoint = "GdiFlush")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GdiFlush();
}
