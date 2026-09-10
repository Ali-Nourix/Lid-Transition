# LidFlow — research and architecture decisions

This document records what was established before implementation, and separates
three very different kinds of statement:

- **FACT** — documented behaviour, with a source.
- **INFERENCE** — a conclusion drawn from facts or from widely-reported
  behaviour, labelled as such.
- **VISUAL RECREATION** — a design decision made to reproduce an *observed*
  visual result. Independent work; no claim about anyone else's implementation.

Sources are listed inline and collected at the end.

---

## 1. Windows lid event mechanism

### What the OS provides

**FACT.** Windows exposes lid state as a *power setting notification*, not as a
device or input event. An application registers with
`RegisterPowerSettingNotification` and receives `WM_POWERBROADCAST` messages with
`wParam == PBT_POWERSETTINGCHANGE` and an `lParam` pointing at a
`POWERBROADCAST_SETTING` structure whose `PowerSetting` field identifies which
setting changed and whose `Data` field carries the new value. For an interactive
application the `Flags` parameter is zero and `hRecipient` is a window handle.
([Registering for Power Events][power-events], [WM_POWERBROADCAST][wm-power])

**FACT.** The relevant setting is
`GUID_LIDSWITCH_STATE_CHANGE` = `BA3E0F4D-B817-4094-A2D1-D56379E6A0F3`. `Data` is
a `DWORD`: `0x0` means the lid is closed, `0x1` means the lid is opened. The
documentation adds that "the callback won't be called until a lid device is found
and its current state is known". ([Power Setting GUIDs][power-guids])

That last sentence is load-bearing rather than a footnote: on a desktop PC the
notification legitimately never arrives. `LidState.Unknown` is therefore a real,
permanent state in this codebase, not a placeholder to be optimized away.

**FACT.** Display power state is a *separate* notification.
`GUID_SESSION_DISPLAY_STATUS` = `2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5` carries a
`MONITOR_DISPLAY_STATE` (`0` off, `1` on, `2` dimmed) and is documented as the one
"all applications that run in an interactive user-mode session should use".
`GUID_CONSOLE_DISPLAY_STATE` is the session-0 equivalent, and
`GUID_MONITOR_POWER_ON` is the pre-Windows-8 form. ([Power Setting GUIDs][power-guids])

LidFlow subscribes to both the lid switch and the session display status, because
they answer different questions: *did the hinge move* and *is there still a lit
panel to draw on*.

### What was deliberately not used

Polling, WMI queries, timers, keyboard/mouse hooks and inferring lid state from
display state were all rejected. The notification is pushed by the OS, costs
nothing while idle, and is the only mechanism with no latency floor of its own.

### The continuous-angle alternative

**FACT.** Windows also has `Windows.Devices.Sensors.HingeAngleSensor`, which
reports a *continuous* hinge angle in degrees with a configurable report
threshold. ([HingeAngleSensor][hinge-sensor])

**INFERENCE.** In practice this sensor is present on dual-screen and foldable
hardware rather than on ordinary clamshell laptops. Windows has no documented
API that exposes a continuous lid angle for a conventional laptop lid; the lid
switch is binary by design.

This distinction turned out to be the single most important finding of the whole
investigation, and section 10 returns to it.

---

## 2. Power management — the honest limitation

This is the part of the brief most likely to be answered dishonestly, so it is
stated plainly.

**FACT.** An application cannot prevent or delay a lid-close sleep.
`SetThreadExecutionState`, the obvious candidate, is documented in the clearest
possible terms:

> The `SetThreadExecutionState` function cannot be used to prevent the user from
> putting the computer to sleep. Applications should respect that the user expects
> a certain behavior when they close the lid on their laptop or press the power
> button.

([SetThreadExecutionState][ste])

`ES_DISPLAY_REQUIRED` and `ES_SYSTEM_REQUIRED` reset the *idle* timers. They have
no effect on a user-initiated power transition. The same is true of the
`PowerCreateRequest` family, which is a more modern spelling of the same idea.

**FACT.** `PBT_APMQUERYSUSPEND` — the message that once let an application object
to sleep — has not been honoured since Windows Vista. There is no veto.
([WM_POWERBROADCAST messages][wm-power-msgs])

**FACT.** On a Modern Standby (S0 low-power idle) system, "Modern Standby starts
when the user causes the system to enter sleep (e.g. user pressing the power
button, closing the lid, idling out, or selecting Sleep from the power button in
the Windows Start menu)", and all of its power modes "occur with the screen turned
off". ([Modern Standby][modern-standby])

**FACT.** The lid-close action itself is a *power policy* setting (do nothing /
sleep / hibernate / shut down), configured by the user or the OEM.

### What this means for the effect

The animation is only visible while two things are simultaneously true: the panel
is still powered, and the lid is still open far enough to see it. Concretely:

| Lid-close action | What the user actually sees |
| --- | --- |
| Sleep or Hibernate (the usual default) | The animation plays in the interval between the lid switch firing and the panel powering down. That interval is short. |
| Do nothing | Windows still powers the internal panel off when the lid closes, so the interval is bounded the same way — the effect is simply not interrupted by a system transition. |
| Shut down | Little or nothing is visible. |

**INFERENCE.** A laptop lid switch is typically a magnetic sensor that trips
somewhere in the last few tens of degrees of travel, not at the moment the user
starts moving the lid. Together with the panel power-down above, this bounds the
useful animation length to a few hundred milliseconds. It is *why* the close
duration defaults to 340 ms rather than something more leisurely, and why the
first frame must be presented with essentially no latency. The exact trigger angle
is hardware-specific and no general specification was found for it.

### The opening side

**INFERENCE.** On resume, if "require sign-in on wakeup" is on, the lock screen is
drawn on the Winlogon secure desktop, where no ordinary application can render.
The opening animation is therefore fully visible when the session is *not* locked,
and is otherwise pre-empted by the logon UI. This follows directly from session
isolation and was not separately measured.

### What LidFlow does about it

- It never modifies sleep settings, the lid-close action, hibernate settings or
  power plans. There is no code that writes power policy.
- `SetThreadExecutionState` is imported but used only to avoid the *idle* timer
  blanking the panel during a transition that is already running. It is never
  used to try to survive a lid-close sleep, because that does not work.
- `PBT_APMSUSPEND` is treated as "stop rendering and leave the overlay black",
  not as something to argue with.
- When the display power notification reports `Off` mid-animation, rendering
  stops immediately: there is nothing to draw on and continuing would only cost
  battery.

---

## 3. Capture architecture

Three mechanisms were evaluated. The decision hinged on something not obvious at
the outset.

### DXGI Desktop Duplication — chosen as the default

**FACT.** `IDXGIOutput1::DuplicateOutput` yields an `IDXGIOutputDuplication`
whose `AcquireNextFrame` hands back the desktop as a GPU texture. Documented
properties that mattered:

- The surface format "is always `DXGI_FORMAT_B8G8R8A8_UNORM` no matter what the
  current display mode is". **This means HDR is flattened.**
- Under a rotated display mode, "the surface that you receive from
  `AcquireNextFrame` is always in the un-rotated orientation, and the desktop
  image is rotated within the surface". The client must correct for it.
- The mouse pointer may or may not be composited into the image; when the adapter
  draws it on an overlay plane, `AcquireNextFrame` reports a separate pointer and
  the client must draw it itself.
- A frame must be released before the next is acquired, and the acquired surface
  is only valid until then.
- `DXGI_ERROR_ACCESS_LOST` must be handled by recreating the duplication.

([Desktop Duplication API][dda])

**INFERENCE.** Because dirty and move rectangles describe only *what changed*, and
the surface itself always holds the complete current desktop, a single successful
`AcquireNextFrame` is sufficient for a one-shot snapshot — including a frame whose
only update was the pointer. LidFlow relies on this and copies unconditionally.

**FACT.** Windows marked `WDA_EXCLUDEFROMCAPTURE` are omitted from the duplicated
content. A behaviour change in 24H2 makes `AcquireNextFrame` also *wake* for
updates to such a window, but the window's pixels remain excluded from the image.
([Win32CaptureSample issue #83][dda-24h2])

### Windows.Graphics.Capture — rejected as the default, kept as opt-in

The brief named this API, and the research produced a concrete reason not to make
it the default.

**FACT.** Disabling the capture indicator requires setting
`GraphicsCaptureSession.IsBorderRequired = false`. That in turn requires user
consent through `GraphicsCaptureAccess.RequestAccessAsync` with
`GraphicsCaptureAccessKind.Borderless`, and:

> To call **RequestAccessAsync** with **GraphicsCaptureAccessKind.Borderless**, you
> must declare the **graphicsCaptureWithoutBorder** capability in your app's
> package manifest.

The property itself requires Windows 10 build 20348 or later.
([GraphicsCaptureSession.IsBorderRequired][border-required])

LidFlow ships as a plain executable with no package identity, so it has no package
manifest and cannot declare that capability. **INFERENCE** (well supported by both
the documentation above and widespread reports): a monitor capture from an
unpackaged app therefore shows a coloured border around the display for the life
of the session. A border flashing around the panel immediately before a
"hardware-like" animation is exactly the visible chrome the brief forbids.

It is still implemented and selectable, because it is the *only* backend that can
deliver FP16 surfaces and so preserve an HDR desktop. On an HDR panel that is a
real trade the user can make. `Direct3D11CaptureFramePool.CreateFreeThreaded` is
used rather than `Create`, because the latter requires a `DispatcherQueue` on the
calling thread and a plain Win32 message loop has none. ([Screen capture][wgc])

Monitor items have no projected constructor; they are created through
`IGraphicsCaptureItemInterop::CreateForMonitor` on the class's activation factory.
([IGraphicsCaptureItemInterop][interop])

### GDI BitBlt — kept as the final fallback

Slow, and blind to hardware-overlay and protected content. It has one property the
others lack: it returns the current desktop even when nothing has changed at all,
where Desktop Duplication can time out on a completely static screen. It is what
makes "capture always succeeds" true rather than merely likely.

### Anti-recursion, and why the opening animation works at all

**FACT.** `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` — value
`0x00000011`, introduced in Windows 10 version 2004 — makes a window "displayed
only on a monitor; everywhere else, the window does not appear at all". Two
documented caveats matter: it "works only when the Desktop Window Manager is
composing the desktop", and "setting the display affinity to
`WDA_EXCLUDEFROMCAPTURE` on previous version of Windows will behave as if
`WDA_MONITOR` is applied". ([SetWindowDisplayAffinity][affinity])

This single flag is what makes the reveal possible. The opening animation needs a
snapshot of the *live desktop* while an opaque black overlay is already on screen
— otherwise the panel lighting up would show the desktop before the animation
could start. With the overlay excluded from capture, the capture reads straight
through it.

Because the pre-2004 fallback (`WDA_MONITOR`) captures the window as blank rather
than omitting it, it is **not** equivalent. LidFlow reads the affinity back with
`GetWindowDisplayAffinity` and records what actually stuck, rather than trusting
the return code, and degrades explicitly when the flag is unavailable.

---

## 4. Rendering and overlay architecture

### Presentation: DirectComposition, not an HWND swap chain

The requirement "the user must never see a black frame flash" drove this.

**FACT.** `WS_EX_NOREDIRECTIONBITMAP` tells the composition engine not to
allocate a redirection surface for a window. ([High-Performance Window Layering][layering])
`DCompositionCreateDevice` plus `IDCompositionDevice::CreateTargetForHwnd` and a
swap chain from `IDXGIFactory2::CreateSwapChainForComposition` compose content
into such a window. ([DirectComposition basic concepts][dcomp])

The consequence LidFlow depends on: with no redirection bitmap there is no
intermediate buffer for DWM to put on screen, so the window can exist with nothing
to show. The sequence is therefore

1. render frame 0 into the composition swap chain,
2. `Present`,
3. *then* show the window.

The first thing that ever reaches the panel is a correct frame. With an HWND swap
chain the ordering is not guaranteed and a one-frame flash of an uninitialized
back buffer is possible.

### Window styles

| Style | Why |
| --- | --- |
| `WS_POPUP` | No frame, caption or non-client area. |
| `WS_EX_TOPMOST` | Above ordinary windows for the transition only. |
| `WS_EX_NOACTIVATE` | Showing the overlay must never move focus. |
| `WS_EX_TOOLWINDOW` | Keeps it out of the taskbar and Alt+Tab. |
| `WS_EX_TRANSPARENT` | Hit-testing falls through. Backed up by returning `HTTRANSPARENT` from `WM_NCHITTEST`, because the style alone is ignored on some injected-input paths. |
| `WS_EX_NOREDIRECTIONBITMAP` | See above. |

The overlay is created once and kept **hidden** between transitions rather than
left permanently topmost, because a persistently topmost window can interfere with
exclusive-fullscreen presentation.

### Colour

The snapshot is bound through a `*_UNORM_SRGB` shader-resource view and the render
target through a `*_UNORM_SRGB` view over a UNORM flip-model back buffer, so
texture reads arrive linear and writes are re-encoded by the hardware. Every
darkening operation therefore happens in linear light. Doing this in gamma space
is the difference between a panel dimming and a muddy grey wash, and it costs
nothing — the hardware does the transfer.

### Threading and the frame loop

Frames are driven by a **posted window message**, not a timer and not a render
thread. Presenting with a sync interval of 1 already paces the animation to the
panel's real refresh rate, so the only thing needed is a way to come back for the
next frame; going through the message queue means the pump is serviced *between*
every frame.

That is not a stylistic choice. A lid re-opening mid-close arrives as a window
message, and a loop that blocked the pump for the animation's duration would not
observe it until the animation it was supposed to interrupt had already finished.
It also keeps every Direct3D, DXGI and DirectComposition call on one thread, which
is what allows `DeviceCreationFlags.Singlethreaded` and removes a class of races
outright.

---

## 5. Multi-monitor strategy

**FACT.** Connector technology is exposed through the Connecting and Configuring
Displays API: `QueryDisplayConfig` with `QDC_ONLY_ACTIVE_PATHS`, then
`DisplayConfigGetDeviceInfo` with `DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME`,
gives a `DISPLAYCONFIG_TARGET_DEVICE_NAME` whose `outputTechnology` comes from
`DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY`. The values that identify a built-in panel
are `DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL` (`0x80000000`) and `..._LVDS` (`6`);
`..._DISPLAYPORT_EMBEDDED` (`11`) and `..._UDI_EMBEDDED` (`13`) indicate an
embedded connector.
([DISPLAYCONFIG_TARGET_DEVICE_NAME][target-name], [DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY][vot])

Enumeration is DXGI-first (`EnumAdapters1` → `EnumOutputs`), because that is the
one source that yields the device name, the desktop rectangle in physical pixels,
the `HMONITOR`, the rotation, the colour space *and* the adapter/output indices the
capture path needs — all in the coordinate system they will be used in. Connector
technology is then matched back by GDI device name.

Because identification can be ambiguous, confidence is carried explicitly
(`No` / `Unknown` / `Likely` / `Certain`) instead of being flattened into a
boolean guess, and the selector prefers a `Certain` match. The fallback chain is:
identified internal panel → the only display, if there is one → primary, recorded
as a guess so the log and the debug overlay say so.

**Default: internal panel only.** An external monitor is not physically moving, so
animating it would be misleading. External displays require an explicit opt-in.

### Hybrid graphics

**FACT.** `DuplicateOutput` requires a device created on the adapter that owns the
output.

**INFERENCE.** On a hybrid-graphics laptop the built-in panel is typically driven
by the integrated GPU while the default adapter may be the discrete one, so a
device created on the default adapter cannot duplicate the panel.

LidFlow therefore creates its D3D11 device on the *target display's own* adapter,
found by LUID. This eliminates the cross-adapter shared-texture path entirely
rather than implementing it.

---

## 6. DPI strategy

Per-monitor-v2 awareness is declared **in the application manifest**, not through
`Application.SetHighDpiMode`. The manifest applies before any managed code runs,
and the overlay needs true physical monitor coordinates from the very first lid
event; `SetHighDpiMode` runs too late to guarantee that. Under any lesser
awareness mode Windows would hand back virtualized coordinates and then
bitmap-stretch the result, mis-positioning the overlay *and* blurring the
snapshot.

Resolution and DPI independence is structural rather than a set of special cases.
The shader measures every distance in **panel-height units** (uv with x scaled by
the aspect ratio), and tunable pixel values are authored against a 1080-tall
reference panel. Converting one to the other is a divide by 1080 and nothing in the
pipeline depends on the real pixel count, so 1080p at 100% and 4K at 200% produce
the same effect at the same relative scale. Nothing is hardcoded to 1920×1080.

---

## 7. Performance strategy

- One fullscreen pass, one draw call, no render-to-texture ping-pong.
- Wide blur is served by the snapshot's **mip chain** rather than by more taps: a
  variable radius reaching a hundred-plus pixels would otherwise need hundreds of
  samples per pixel at 4K. Tap count is 5/9/13 by quality tier and the mip level is
  chosen so the kernel stays gap-free.
- Occluded pixels return black before any sampling work, so the held-black final
  frame is nearly free.
- The device, overlay window, compiled shaders and pipeline state are all created
  at start-up and kept warm. The visible window for a lid animation is too short
  to create a device inside.
- Capture is a single GPU-to-GPU copy on the same adapter that presents; no
  readback.
- `GCLatencyMode.SustainedLowLatency` for the process, and no allocation on the
  per-frame path.
- Animation time is wall-clock, not frame-counted, so a dropped frame shortens the
  animation rather than slowing it down.

Frame pacing comes from `Present(1)`, so the loop runs at whatever the panel does —
60 Hz, 120 Hz or otherwise — without a timer.

**Not verified:** no frame-time measurements on real hardware have been taken. See
`docs/TESTING.md`.

---

## 8. Security considerations

- No network code of any kind. No telemetry, analytics or cloud calls.
- `asInvoker` in the manifest. No administrator rights are requested, because
  reading the lid switch, capturing one's own session and drawing a window are all
  ordinary user-mode operations.
- The only registry write is the per-user `Run` value for start-up, which Task
  Manager can disable and `UNINSTALL.cmd` removes.
- Logs record lid state, monitor selection, capture results, animation state and
  exceptions. Screen content is never written to disk, and neither is anything the
  user typed or looked at.
- Everything lives under `%LOCALAPPDATA%`, so the app runs from a read-only folder.

The one thing worth calling out: LidFlow captures the screen. That is inherent to
the effect. The frame lives in GPU memory for the duration of a transition and is
never copied off the machine.

---

## 9. Apple visual behaviour analysis

The brief asked specifically about Apple's September 2026 material, and about the
reference images.

**FACT.** Apple announced the foldable iPhone Duo on 9 September 2026 at Apple
Park. On the fold transition, Apple's own description is that "software transitions
gracefully between the outer and inner displays as the device opens, with controls
staying in a consistent position while content scales up".
([MacRumors — Apple Announces Foldable 'iPhone Duo'][macrumors])

That sentence is the most useful single input to this project. *Controls staying in
a consistent position* is precisely the principle the brief describes as "content
stays fixed", stated by Apple as a design goal for a form-factor transition. It is
independent confirmation that positional stability — not scaling the whole frame —
is the thing that makes such a transition read as physical.

**FACT.** The reference images in the brief are of **Bendy**, a third-party macOS
application ("iPhone Duo but MacBook"). Its own description of the effect is "your
desktop tilts, blurs and settles as it comes down" and "the screen bends, blurs and
shades, just like when your lid comes down". It offers three styles with adjustable
perspective, blur and shadow. Technically it states that it "reads the hinge angle
from the Mac's built-in sensor (no polling or accessibility workarounds)", captures
the screen and renders the tilt with Metal, and requires macOS 14 or later on an
Apple silicon MacBook **with a lid angle sensor**. ([Bendy][bendy])

**No claim is made about how Apple or Bendy implement anything internally.** No
code, assets, trademarks or algorithms from either were used or consulted. What was
used is the publicly stated behaviour above, plus the two photographs in the brief.

### VISUAL RECREATION — what was observed and what was built

From the reference images, the observable properties are: the content stays at its
own coordinates; black grows inward from the panel's physical edges; the visible
band is bounded top and bottom with soft, blurred boundaries; the whole thing reads
as a panel rotating away rather than a rectangle shrinking.

The geometry that produces this in LidFlow is independent construction, reasoned
from physics rather than copied:

1. **Hinge-biased asymmetry.** The aperture converges on a line at 62% of panel
   height rather than on the centre, so the top edge travels 1.63× further — and
   therefore faster — than the bottom. A symmetric iris reads as a shutter; an
   asymmetric one reads as a hinge below the screen.
2. **Keystone.** The sides close *more at the top than at the hinge*, making the
   aperture a trapezoid. This is the projection a real surface tilting away
   produces, and it is the strongest single cue that the panel is rotating rather
   than scaling.
3. **Sides delayed.** They start at 18% progress and travel less than the top and
   bottom, matching the reference images where the motion is dominated by the
   horizontal edges.
4. **Blur from speed, not from time.** The blur radius is gated on the edge's
   *instantaneous* velocity, so it appears because something is moving and decays
   as it settles — which is what makes it read as optical motion rather than a
   defocus fade.
5. **Off-axis wash.** Contrast and saturation fall with distance from the hinge,
   because that is where a real LCD's viewing angle is worst. Asymmetric for the
   same reason.
6. **Specular sweep.** A faint, cool-tinted highlight just inside the closing edge:
   the reflection that slides across a glossy panel as it turns.
7. **Edge-localized warp.** The sample coordinate is pushed away from the hinge
   near the edge only, compressing the image toward it. Shaped to vanish in the
   middle so the centre stays pixel-stable — the "content stays fixed" requirement
   taken literally.
8. **Triangular dither.** Wide dark gradients band badly on 8-bit panels. Two
   decorrelated noise samples, amplitude well under one code value.

Both endpoints are exact by construction, which is a correctness property rather
than a tuning one: at full open every effect term is zero and the aperture sits
*outside* the panel, so the first frame is pixel-identical to the captured desktop;
at full close the aperture has zero height, so the last frame is pure black.

---

## 10. The most important gap: angle versus switch

Comparing the platforms produced a conclusion worth stating on its own.

- Bendy reads a **continuous lid angle** from Apple silicon hardware, so its image
  can track the hinge one-to-one. Stopping half way, moving slowly and reversing
  all behave correctly for free, because the image is a read-out of the hardware
  rather than an animation.
- Windows gives an ordinary clamshell laptop a **binary** lid event, fired once at
  a hardware threshold. There is no angle to track.

This is a genuine fidelity gap and it is not closeable in software on hardware that
has no sensor. It is also the reason several design decisions look the way they do:
the animation is timed, the easing curves matter a great deal, the duration must be
short, and reversal has to be handled explicitly by matching panel *position* across
two different curves rather than falling out of the physics.

Where Windows *does* expose `HingeAngleSensor`, LidFlow uses it: panel position
comes from the hinge and blur from the measured rate of change, through the same
animation model, so the two input modes cannot drift apart visually. It is enabled
by default, feature-detected, and its absence is not an error. On most clamshell
laptops it will be absent and the timed path will run.

---

## 11. Chosen architecture, and why

**C# on .NET 8, `net8.0-windows`, with Win32 interop, Direct3D 11, DXGI and
DirectComposition. Windows Forms for the tray icon, the settings dialog and the
hidden message window only.**

Why not **WinUI 3 / Windows App SDK**, which the brief suggested first:

- The effect needs a custom pixel shader. Windows.UI.Composition offers a fixed
  effect graph with no custom HLSL; getting there means Win2D plus a shader-effect
  layer, which is more moving parts than a D3D11 device and one `Draw` call.
- It adds a runtime dependency and a heavier start-up path for an app whose entire
  job is to react to an event within milliseconds.
- Nothing in the visible product is XAML. There is no UI in the overlay at all.

Why not **C++/Win32**, the brief's other option:

- It was the strongest candidate, and the deciding factor was verifiability. This
  project was developed in a Linux container with no MSVC and no Windows SDK. A
  C++/WinRT build could not have been compiled even once before delivery, whereas
  `net8.0-windows` with `EnableWindowsTargeting` compiles on Linux — so every line
  here was compiler-checked continuously, and the platform-neutral half is
  unit-tested continuously.
- The latency argument for C++ does not apply to the actual hot path. The device,
  window and shaders are all warm before an event arrives; per transition the work
  is a capture copy and a draw call. GC is kept out of the way with
  `SustainedLowLatency` and an allocation-free frame path.
- Self-contained publishing removes the runtime-installation concern.

Why Windows Forms *only* for chrome: hand-rolling a tray icon, a message pump and a
settings dialog in P/Invoke is several hundred lines of untestable interop for no
visual benefit. It is confined to chrome — the overlay is a raw `CreateWindowEx`
window with a D3D11 swap chain composed by DirectComposition, because the styles
and commit ordering it needs are not expressible through a managed UI framework.

Shaders are compiled at **runtime** by the `d3dcompiler` that ships with Windows.
That is why `BUILD-WINDOWS.cmd` needs nothing but the .NET SDK — no Windows SDK, no
`fxc`, no C++ toolchain — and it costs a few milliseconds once, off the critical
path.

---

## 12. Rejected alternatives

| Alternative | Why not |
| --- | --- |
| WinUI 3 / Windows App SDK | No custom HLSL; runtime dependency; heavier start-up. Section 11. |
| C++/Win32 + DirectComposition | Technically excellent, but unverifiable in this environment. Section 11. |
| WPF | Same shader problem as WinUI, plus its own composition layer in the way. |
| Electron / Tauri / any HTML rendering | Explicitly excluded by the brief, and correctly: a browser cannot produce a click-through, capture-excluded, DirectComposition-composed overlay with frame-accurate presentation. |
| Windows.Graphics.Capture as the default | Capture border cannot be disabled without a package manifest. Section 3. |
| GDI BitBlt as the default | Slow, misses overlay and protected content, no HDR. Kept as the fallback that cannot fail. |
| HWND swap chain | Cannot guarantee the first visible pixel is a correct frame. Section 4. |
| Dedicated render thread | Would need the overlay's messages pumped on that thread; a non-pumping owner of a visible window risks hanging `WM_NCHITTEST` senders. The posted-message loop is single-threaded and yields to the pump every frame. |
| WinForms timer for frames | ~15 ms resolution and jitter; `Present(1)` already paces perfectly. |
| Cross-adapter shared textures | Unnecessary once the device is created on the target display's adapter. Section 5. |
| Parsing Desktop Duplication pointer shapes | Three formats including a monochrome AND/XOR pair. `GetCursorInfo` + `DrawIconEx` over black and white recovers alpha for all cursor types in one code path that works for every backend. |
| `SetThreadExecutionState` to keep the panel alive | Documented not to work for lid close. Section 2. |
| Vetoing sleep via `PBT_APMQUERYSUSPEND` | Ignored since Vista. Section 2. |

---

## 13. Known limitations

Stated plainly, including the ones that cannot be fixed.

1. **The visible window is short and hardware-dependent.** The lid switch fires
   near the end of travel and Windows powers the panel down on close. Under some
   power configurations very little is visible. This is not a bug and no software
   can widen it. Section 2.
2. **No continuous lid angle on ordinary laptops.** The timed animation is an
   approximation of the user's hand. Section 10.
3. **The opening animation is pre-empted by the lock screen** when sign-in on
   wakeup is required, because the logon UI is a different desktop.
4. **HDR is flattened on the default capture path.** Desktop Duplication is always
   BGRA8. Selecting the Windows.Graphics.Capture backend preserves HDR at the cost
   of a visible capture border.
5. **Windows 10 before version 2004** loses `WDA_EXCLUDEFROMCAPTURE`, and with it
   the ability to capture the desktop from behind the overlay — so the reveal falls
   back to the pre-close snapshot. The app detects this rather than assuming it.
6. **The transition is skipped while a full-screen application is active**, via
   `SHQueryUserNotificationState`. A topmost overlay can drop an exclusive-fullscreen
   game out of fullscreen, and the effect is not worth that.
7. **Rotated displays are handled but unverified.** The un-rotated-surface
   correction is implemented as a UV transform; no rotated panel was available to
   confirm the mapping.
8. **Monochrome cursors with XOR-invert regions** are approximated as opaque. Such a
   region has no fixed colour; the approximation matches how it looks over mid-tones.
9. **Nothing has been verified on physical hardware.** CI compiles the project,
   runs the unit tests and publishes the artifacts on a real Windows runner, but a
   CI runner has no laptop lid and no display, so the on-screen result is unverified.
   `docs/TESTING.md` is the manual checklist.
10. **The self-contained archive is large** (~80 MB) because it bundles the .NET
    runtime. `BUILD-WINDOWS.ps1 -SelfContained:$false` produces a far smaller build
    that requires the .NET Desktop Runtime.

---

## Sources

- [Registering for Power Events][power-events] — `https://learn.microsoft.com/en-us/windows/win32/power/registering-for-power-events`
- [WM_POWERBROADCAST][wm-power] — `https://learn.microsoft.com/en-us/windows/win32/power/wm-powerbroadcast`
- [WM_POWERBROADCAST Messages][wm-power-msgs] — `https://learn.microsoft.com/en-us/windows/win32/power/wm-powerbroadcast-messages`
- [Power Setting GUIDs][power-guids] — `https://learn.microsoft.com/en-us/windows/win32/power/power-setting-guids`
- [SetThreadExecutionState][ste] — `https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setthreadexecutionstate`
- [Modern Standby][modern-standby] — `https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/modern-standby`
- [System power states][power-states] — `https://learn.microsoft.com/en-us/windows/win32/power/system-power-states`
- [Desktop Duplication API][dda] — `https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api`
- [Desktop Duplication capture behaviour change in 24H2][dda-24h2] — `https://github.com/robmikh/Win32CaptureSample/issues/83`
- [SetWindowDisplayAffinity][affinity] — `https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity`
- [GraphicsCaptureSession.IsBorderRequired][border-required] — `https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired`
- [Screen capture (Windows.Graphics.Capture)][wgc] — `https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture`
- [IGraphicsCaptureItemInterop::CreateForMonitor][interop] — `https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createformonitor`
- [HingeAngleSensor][hinge-sensor] — `https://learn.microsoft.com/en-us/uwp/api/windows.devices.sensors.hingeanglesensor`
- [DISPLAYCONFIG_TARGET_DEVICE_NAME][target-name] — `https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_target_device_name`
- [DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY][vot] — `https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ne-wingdi-displayconfig_video_output_technology`
- [High-Performance Window Layering Using the Windows Composition Engine][layering] — `https://learn.microsoft.com/en-us/archive/msdn-magazine/2014/june/windows-with-c-high-performance-window-layering-using-the-windows-composition-engine`
- [DirectComposition basic concepts][dcomp] — `https://learn.microsoft.com/en-us/windows/win32/directcomp/basic-concepts`
- [Win32CaptureSample (Microsoft, robmikh)][sample] — `https://github.com/robmikh/Win32CaptureSample`
- [MacRumors — Apple Announces Foldable 'iPhone Duo' (9 Sep 2026)][macrumors] — `https://www.macrumors.com/2026/09/09/apple-announces-foldable-iphone-duo/`
- [Bendy — "iPhone Duo but MacBook"][bendy] — `https://trybendy.app/`

[power-events]: https://learn.microsoft.com/en-us/windows/win32/power/registering-for-power-events
[wm-power]: https://learn.microsoft.com/en-us/windows/win32/power/wm-powerbroadcast
[wm-power-msgs]: https://learn.microsoft.com/en-us/windows/win32/power/wm-powerbroadcast-messages
[power-guids]: https://learn.microsoft.com/en-us/windows/win32/power/power-setting-guids
[ste]: https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setthreadexecutionstate
[modern-standby]: https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/modern-standby
[power-states]: https://learn.microsoft.com/en-us/windows/win32/power/system-power-states
[dda]: https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api
[dda-24h2]: https://github.com/robmikh/Win32CaptureSample/issues/83
[affinity]: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity
[border-required]: https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired
[wgc]: https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture
[interop]: https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createformonitor
[hinge-sensor]: https://learn.microsoft.com/en-us/uwp/api/windows.devices.sensors.hingeanglesensor
[target-name]: https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_target_device_name
[vot]: https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ne-wingdi-displayconfig_video_output_technology
[layering]: https://learn.microsoft.com/en-us/archive/msdn-magazine/2014/june/windows-with-c-high-performance-window-layering-using-the-windows-composition-engine
[dcomp]: https://learn.microsoft.com/en-us/windows/win32/directcomp/basic-concepts
[sample]: https://github.com/robmikh/Win32CaptureSample
[macrumors]: https://www.macrumors.com/2026/09/09/apple-announces-foldable-iphone-duo/
[bendy]: https://trybendy.app/
