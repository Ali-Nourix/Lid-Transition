using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LidFlow.App.Interop;
using LidFlow.Core.Diagnostics;
using LidFlow.Core.Lid;
using LidFlow.Core.Power;

namespace LidFlow.App.Power;

/// <summary>Raised for every power setting notification LidFlow subscribes to.</summary>
internal sealed class PowerSettingEventArgs : EventArgs
{
    public PowerSettingEventArgs(Guid setting, byte[] data)
    {
        Setting = setting;
        Data = data;
    }

    public Guid Setting { get; }

    public byte[] Data { get; }
}

/// <summary>
/// Hidden top-level window that receives the app's Win32 messages: power
/// notifications, suspend/resume, preview hotkeys and display changes.
/// <para>
/// It is a real top-level window rather than a message-only
/// (<c>HWND_MESSAGE</c>) one on purpose. Power <i>setting</i> notifications are
/// delivered directly to the registered handle and would work either way, but
/// <c>PBT_APMSUSPEND</c> and <c>PBT_APMRESUMEAUTOMATIC</c> are broadcast to
/// top-level windows only, and a message-only window would never see them - so
/// the app would not know the machine was going to sleep.
/// </para>
/// <para>
/// It is created without <c>WS_VISIBLE</c> and with <c>WS_EX_TOOLWINDOW</c>, so
/// it never appears on screen, in the taskbar, or in Alt+Tab.
/// </para>
/// </summary>
internal sealed class PowerEventWindow : NativeWindow, IDisposable
{
    // Arbitrary but stable ids, scoped to this window by RegisterHotKey.
    private const int HotkeyIdPreviewClose = 0x11D0;
    private const int HotkeyIdPreviewOpen = 0x11D1;

    /// <summary>
    /// Posted to request one animation frame.
    /// <para>
    /// The frame loop is driven by posted messages rather than by a timer or a
    /// render thread. Presenting with a sync interval of 1 already paces the
    /// animation to the panel's real refresh rate, so all that is needed is a way
    /// to come back for the next frame - and going through the message queue means
    /// the pump is serviced between every frame. That matters more than it sounds:
    /// a lid re-opening mid-close arrives as a window message, and a loop that
    /// blocked the pump for the animation's duration would not see it until the
    /// animation it was supposed to interrupt had already finished.
    /// </para>
    /// <para>
    /// It also keeps every Direct3D, DXGI and DirectComposition call on a single
    /// thread, which is what lets the device be created with
    /// <c>Singlethreaded</c> and removes a whole class of races.
    /// </para>
    /// </summary>
    private const uint WM_LIDFLOW_FRAME = 0x8000 + 1;   // WM_APP + 1

    private readonly ILidFlowLog _log;
    private readonly List<IntPtr> _registrations = new();
    private bool _hotkeysRegistered;
    private bool _disposed;

    public PowerEventWindow(ILidFlowLog log)
    {
        _log = log ?? NullLog.Instance;

        CreateParams createParams = new()
        {
            Caption = "LidFlow.PowerEvents",
            ClassName = null,
            Style = 0,                                       // no WS_VISIBLE: never shown
            ExStyle = unchecked((int)NativeMethods.WS_EX_TOOLWINDOW),
            X = 0,
            Y = 0,
            Width = 0,
            Height = 0,
            Parent = IntPtr.Zero,                            // top-level, so broadcasts arrive
        };

        CreateHandle(createParams);
    }

    /// <summary>A subscribed power setting changed.</summary>
    public event EventHandler<PowerSettingEventArgs>? PowerSettingChanged;

    /// <summary>The machine is suspending. There is no way to veto this.</summary>
    public event EventHandler? Suspending;

    /// <summary>The machine resumed.</summary>
    public event EventHandler? Resumed;

    /// <summary>Display topology, resolution or DPI changed.</summary>
    public event EventHandler? DisplayConfigurationChanged;

    /// <summary>Ctrl+Alt+Shift+C.</summary>
    public event EventHandler? PreviewClosePressed;

    /// <summary>Ctrl+Alt+Shift+O.</summary>
    public event EventHandler? PreviewOpenPressed;

    /// <summary>One animation frame is due. See <see cref="RequestFrame"/>.</summary>
    public event EventHandler? FrameRequested;

    /// <summary>Queues a single animation frame.</summary>
    public void RequestFrame() => NativeMethods.PostMessageW(Handle, WM_LIDFLOW_FRAME, IntPtr.Zero, IntPtr.Zero);

    /// <summary>
    /// Subscribes to a power setting. Failure is logged and tolerated: losing the
    /// display-state notification degrades efficiency, not correctness.
    /// </summary>
    public unsafe bool RegisterPowerSetting(Guid setting)
    {
        Guid local = setting;
        IntPtr handle = NativeMethods.RegisterPowerSettingNotification(
            Handle,
            &local,
            NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE);

        if (handle == IntPtr.Zero)
        {
            _log.Warn($"RegisterPowerSettingNotification failed for {setting} (error {Marshal.GetLastWin32Error()}).");
            return false;
        }

        _registrations.Add(handle);
        return true;
    }

    /// <summary>Registers the preview hotkeys. Returns false if another app owns them.</summary>
    public bool RegisterPreviewHotkeys()
    {
        if (_hotkeysRegistered)
        {
            return true;
        }

        const uint modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT;
        const uint vkC = 0x43;
        const uint vkO = 0x4F;

        bool close = NativeMethods.RegisterHotKey(Handle, HotkeyIdPreviewClose, modifiers, vkC);
        bool open = NativeMethods.RegisterHotKey(Handle, HotkeyIdPreviewOpen, modifiers, vkO);

        if (!close || !open)
        {
            _log.Warn("Preview hotkeys could not be registered; another application already owns them.");

            if (close)
            {
                NativeMethods.UnregisterHotKey(Handle, HotkeyIdPreviewClose);
            }

            if (open)
            {
                NativeMethods.UnregisterHotKey(Handle, HotkeyIdPreviewOpen);
            }

            return false;
        }

        _hotkeysRegistered = true;
        return true;
    }

    public void UnregisterPreviewHotkeys()
    {
        if (!_hotkeysRegistered)
        {
            return;
        }

        NativeMethods.UnregisterHotKey(Handle, HotkeyIdPreviewClose);
        NativeMethods.UnregisterHotKey(Handle, HotkeyIdPreviewOpen);
        _hotkeysRegistered = false;
    }

    protected override void WndProc(ref Message m)
    {
        switch ((uint)m.Msg)
        {
            case NativeMethods.WM_POWERBROADCAST:
                HandlePowerBroadcast(ref m);
                return;

            case NativeMethods.WM_HOTKEY:
                if ((int)m.WParam == HotkeyIdPreviewClose)
                {
                    PreviewClosePressed?.Invoke(this, EventArgs.Empty);
                }
                else if ((int)m.WParam == HotkeyIdPreviewOpen)
                {
                    PreviewOpenPressed?.Invoke(this, EventArgs.Empty);
                }

                return;

            case WM_LIDFLOW_FRAME:
                FrameRequested?.Invoke(this, EventArgs.Empty);
                return;

            case NativeMethods.WM_DISPLAYCHANGE:
            case NativeMethods.WM_DPICHANGED:
                DisplayConfigurationChanged?.Invoke(this, EventArgs.Empty);
                break;
        }

        base.WndProc(ref m);
    }

    private unsafe void HandlePowerBroadcast(ref Message m)
    {
        int evt = (int)m.WParam;

        switch (evt)
        {
            case NativeMethods.PBT_APMSUSPEND:
                _log.Info("PBT_APMSUSPEND: system is suspending.");
                Suspending?.Invoke(this, EventArgs.Empty);
                break;

            case NativeMethods.PBT_APMRESUMEAUTOMATIC:
            case NativeMethods.PBT_APMRESUMESUSPEND:
                _log.Info("System resumed.");
                Resumed?.Invoke(this, EventArgs.Empty);
                break;

            case NativeMethods.PBT_POWERSETTINGCHANGE:
                if (m.LParam != IntPtr.Zero)
                {
                    NativeMethods.POWERBROADCAST_SETTING* setting =
                        (NativeMethods.POWERBROADCAST_SETTING*)m.LParam;

                    // DataLength is attacker-free (it comes from the kernel) but a
                    // sanity bound keeps a driver quirk from turning into a huge
                    // allocation on a power-critical code path.
                    uint length = setting->DataLength;
                    if (length > 64)
                    {
                        length = 64;
                    }

                    byte[] data = new byte[length];
                    fixed (byte* destination = data)
                    {
                        Buffer.MemoryCopy(&setting->Data, destination, length, length);
                    }

                    PowerSettingChanged?.Invoke(this, new PowerSettingEventArgs(setting->PowerSetting, data));
                }

                break;
        }

        // PBT_APMQUERYSUSPEND has been ignored by Windows since Vista - an
        // application cannot veto sleep - so there is deliberately no attempt to
        // deny it here.
        m.Result = (IntPtr)1;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        UnregisterPreviewHotkeys();

        foreach (IntPtr registration in _registrations)
        {
            NativeMethods.UnregisterPowerSettingNotification(registration);
        }

        _registrations.Clear();

        if (Handle != IntPtr.Zero)
        {
            DestroyHandle();
        }
    }
}
