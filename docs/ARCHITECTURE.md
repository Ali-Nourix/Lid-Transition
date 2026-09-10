# LidFlow — architecture

Companion to [RESEARCH.md](RESEARCH.md), which explains *why* these choices were
made. This document explains *what* the code does.

---

## The pipeline

```
                      GUID_LIDSWITCH_STATE_CHANGE
                       (or HingeAngleSensor, when present)
                                  |
                                  v
                        WindowsLidMonitor  ---------> TransitionStateMachine
                                  |                        |
                                  |                        | action
                                  v                        v
                          CaptureService            TransitionController
                       (duplication -> WGC -> GDI)         |
                                  |                        |
                                  v                        |
   desktop ------> GPU texture -> DesktopSnapshot          |
                                  + full mip chain         |
                                  + CursorSnapshot         |
                                                           v
                                                   TransitionRenderer
                                          one fullscreen pass, LidTransition.hlsl
                                                           |
                                                           v
                                        composition swap chain -> IDCompositionVisual
                                                           |
                                                           v
                                         OverlayWindow (borderless, click-through,
                                          topmost, WDA_EXCLUDEFROMCAPTURE)
                                                           |
                                                           v
                                                        panel
```

Every animated value in the shader comes from a single normalized progress value
via `LidAnimationModel`. There is no animation state in the renderer and none in
the shader.

---

## Assemblies

### `LidFlow.Core` — `net8.0`, no Windows APIs

Deliberately platform-neutral so the interesting logic is testable anywhere,
including CI without a GPU or a display.

| Area | Type | Responsibility |
| --- | --- | --- |
| Animation | `Easing`, `CubicBezierEasing`, `EasingCurve` | Timing functions. The Bezier solver is Newton-Raphson with a bisection fallback, allocation-free. |
| Animation | `LidAnimationModel` | **The visual design as maths.** Progress in, every shader parameter out. |
| Animation | `LidFrameParameters` | One frame's worth of parameters. |
| Lid | `LidState`, `LidStateParser` | The documented 0/1 payload, with anything else rejected as `Unknown`. |
| Lid | `ILidMonitor`, `LidMonitorBase`, `MockLidMonitor` | Lid event source and its test/preview double. |
| Lid | `HingeAngleMapping` | Hinge angle → panel progress, by sine of the angle. |
| State | `TransitionStateMachine` | The only thing that may change state. |
| Config | `LidFlowConfig` + sections, `ConfigStore` | Typed settings, clamped on load, atomic save. |
| Monitors | `DisplayInfo`, `MonitorSelector` | Which display to animate, and why. |
| Diagnostics | `ILidFlowLog`, `RollingFileLog` | Flushed-per-line logging. |

### `LidFlow.App` — `net8.0-windows`, WinExe

| Area | Type | Responsibility |
| --- | --- | --- |
| Interop | `NativeMethods` | Every Win32 entry point used, grouped by header. Source-generated P/Invoke. |
| Interop | `PowerSettingGuids` | The power setting GUIDs from WinNT.h. |
| Power | `PowerEventWindow` | Hidden **top-level** window: power notifications, suspend/resume, hotkeys, display changes, and the posted frame message. |
| Lid | `WindowsLidMonitor` | `RegisterPowerSettingNotification` on the lid switch and session display status. |
| Lid | `HingeAngleMonitor` | `Windows.Devices.Sensors.HingeAngleSensor`, feature-detected. |
| Monitors | `DisplayEnumerator` | DXGI-first enumeration plus CCD connector technology. |
| Capture | `DuplicationSnapshotSource` | DXGI Desktop Duplication. Default. |
| Capture | `WgcSnapshotSource` | Windows.Graphics.Capture. Opt-in, for HDR. |
| Capture | `GdiSnapshotSource` | BitBlt. The fallback that cannot fail. |
| Capture | `CursorSnapshot` | Pointer as a premultiplied-alpha texture. |
| Capture | `CaptureService` | Backend chain, preference memory, exclusion rules. |
| Rendering | `GraphicsDevice` | D3D11 + DXGI + DirectComposition device; runtime shader compilation. |
| Rendering | `DesktopSnapshot` | Typeless texture with a full mip chain and an sRGB view. |
| Rendering | `TransitionRenderer` | Constant buffer and the single draw call. |
| Overlay | `OverlayWindow` | The window, its DComp visual and its composition swap chain. |
| — | `TransitionController` | Orchestration. Owns every graphics resource. |
| Tray | `TrayApplicationContext` | Lifetime, tray icon, settings, debug overlay. |

---

## The state machine

```
                            +--------------+
              +------------>|   IdleOpen   |<-----------------+
              |             +--------------+                 |
              |                    | LidClosed                | AnimationCompleted
              |                    v                          | / CaptureFailed
              | CaptureFailed  +-------------------+           |
              +----------------| CapturingForClose |           |
              | / LidOpened    +-------------------+           |
              |                    | CaptureSucceeded          |
              |                    v                           |
              |             +----------------+          +---------------+
              |             | CloseAnimation |--------->| OpenAnimation |
              |             +----------------+ LidOpened+---------------+
              |                    |            (reversal)     ^
              |                    | AnimationCompleted        | CaptureSucceeded
              |                    v                           |
              |             +--------------+          +------------------+
              +-------------|    Closed    |--------->| CapturingForOpen |
                 Abort      +--------------+ LidOpened+------------------+
                                   |
                                   | Suspending            (Disabled is reachable
                                   v                        from every state)
                            +--------------+
                            |  Suspended   |
                            +--------------+
```

Properties worth stating, all covered by tests:

- **Reversal is continuous.** Opening and closing use different curves, so
  resuming the opposite animation at the same *time* would jump the panel. The
  machine reports the panel position on screen, and the controller inverts the new
  curve to find the time that matches it.
- **Capture direction is explicit.** `CapturingForClose` runs with the overlay
  down; `CapturingForOpen` runs with it up and black, capturing through it. Two
  states rather than one with a flag, because the difference matters.
- **Every stop is explicit about the overlay.** `HoldBlack` leaves it up,
  `HideOverlay` and `AbortToIdle` take it down. Nothing leaves it stranded.
- **A resume does not imply the lid opened.** Modern Standby wakes for maintenance
  with the lid shut, so the machine waits for the lid switch.
- **Unknown triggers are ignored, not guessed at.**

---

## The effect, in order

Per pixel, in `LidTransition.hlsl`:

1. **Aperture SDF.** A rounded box whose half-width varies with `y`, evaluated in
   *panel units* (uv with x scaled by aspect, so one unit is one panel height in
   both axes). Converges on a hinge line at `hingeBias`, not the centre.
2. **Early out.** Fully occluded pixels return black before any sampling. The
   held-black final frame therefore costs almost nothing.
3. **Edge normal** by central difference on the SDF, so it agrees with the
   approximated trapezoid rather than an idealized box.
4. **Coordinate warp.** Sample coordinates pushed away from the hinge, scaled by
   proximity to the edge, so the image compresses toward the hinge at the edges and
   the centre stays pixel-stable. Plus a quadratic refraction term along the normal.
5. **Rotation transform** for rotated displays (Desktop Duplication delivers an
   un-rotated surface).
6. **Directional blur.** Radius from the model — already gated on edge speed —
   direction along the normal, wide radii served by the mip chain so the tap count
   stays 5/9/13 regardless of resolution.
7. **Pointer composite**, at the same warped coordinate as the content.
8. **Off-axis wash.** Contrast and saturation fall with distance from the hinge,
   asymmetrically.
9. **Luminance falloff** behind the edge — the wide soft gradient that stops the
   boundary reading as a drawn rectangle.
10. **Specular sweep.** Cool-tinted additive highlight just inside the edge,
    peaking mid-motion.
11. **Global dim, then occlusion** to black.
12. **Triangular dither** from two decorrelated noise samples, amplitude under one
    8-bit code value.

All of it in linear light: the snapshot is read through an `_SRGB` view and the
render target is an `_SRGB` view over a UNORM flip-model back buffer, so the
hardware does both transfers.

### Why the endpoints are exact

At full open every effect term is zero *and* the aperture edges sit outside the
panel (an overshoot proportional to the edge softness), so the first frame is
pixel-identical to the captured desktop. At full close the aperture has zero
height, so the last frame is pure black. Neither is a tuned approximation; both
fall out of the construction, and both are asserted by tests.

---

## Configuration reference

`%LOCALAPPDATA%\LidFlow\config.json`, or `config.json` beside the executable
(which wins). Values named `*Px` are **reference pixels at a 1080-tall panel** and
are scaled by the real panel height; everything else is a 0..1 fraction.

### `animation`

| Key | Default | Meaning |
| --- | --- | --- |
| `enableAnimation` | `true` | Master switch. False keeps the app resident but silent. |
| `useHingeAngleWhenAvailable` | `true` | Track a hinge-angle sensor when the hardware has one. |
| `hingeClosedAngleDeg` | `4` | Angle at or below which the panel counts as closed. |
| `hingeOpenAngleDeg` | `55` | Angle at or above which the effect is fully cleared. |
| `hingeVelocityReference` | `3` | Progress-per-second that saturates the blur in hinge mode. |
| `closeDurationMs` | `340` | Closing duration (timed mode). |
| `openDurationMs` | `285` | Opening duration. Shorter on purpose. |
| `hingeBias` | `0.62` | Normalized Y where the aperture converges. **Most important single value.** |
| `edgeExpansion` | `0.13` | Total horizontal closure, as a fraction of width. |
| `sideDelay` | `0.18` | Progress before the sides start moving. |
| `perspectiveStrength` | `0.85` | Keystone: how much more the aperture narrows at the top. |
| `edgeSoftness` | `2.6` | Antialias width of the panel edge itself. |
| `cornerRadiusPx` | `30` | Aperture corner rounding at full close. |
| `shadowExtentPx` | `155` | Reach of the luminance falloff inside the edge. |
| `shadowStrength` | `0.88` | Depth of that falloff. |
| `maxBlur` | `34` | Peak blur radius at the edge. |
| `blurRadius` | `175` | Distance over which blur ramps to `maxBlur`. |
| `blurVelocityInfluence` | `0.72` | How much blur follows edge speed vs. elapsed time. |
| `blackOpacity` | `1.0` | Opacity of the occluded region. Below 1 is for tuning only. |
| `glareStrength` | `0.055` | Specular sweep intensity. |
| `glareOffsetPx` | `26` | Distance inside the edge where it peaks. |
| `glareWidthPx` | `62` | Width of the highlight band. |
| `warpStrength` | `0.030` | Edge-localized pull toward the hinge. |
| `distortionStrength` | `0.5` | Optical refraction term near the edge. |
| `offAxisWash` | `0.35` | Contrast/saturation loss with distance from the hinge. |
| `globalDim` | `0.10` | Overall luminance loss at full close. |
| `bezelAmbient` | `0.010` | Ambient light the bezel picks up just outside the aperture. Small, but it is what stops the boundary reading as a hole cut in the image rather than an object occluding it. `0` gives a pure-black edge. |
| `bezelFalloffPx` | `22` | Distance outside the edge over which that ambient decays. |
| `enableBlur` / `enableShadow` / `enableDistortion` / `enableGlare` / `enableDither` | `true` | Per-effect switches, useful for isolating one channel while tuning. |
| `quality` | `Balanced` | `Performance` / `Balanced` / `High` → 5 / 9 / 13 blur taps. |
| `closeEasing` / `openEasing` | `LidClose` / `LidOpen` | `{ preset, bezier[4], springDamping, springFrequency }`. Presets: `CustomBezier`, `Linear`, `EaseOutCubic`, `EaseInOutCubic`, `EaseOutQuart`, `EaseOutQuint`, `EaseInOutQuint`, `EaseOutExpo`, `Spring`, `LidClose`, `LidOpen`. |

### `monitor`

| Key | Default | Meaning |
| --- | --- | --- |
| `mode` | `InternalPanel` | `InternalPanel` / `PrimaryDisplay` / `AllDisplays` / `Manual`. |
| `manualDisplayId` | `null` | Device path or `\\.\DISPLAYn` for `Manual`. |
| `allowExternalDisplays` | `false` | Required for anything but the built-in panel. |

### `behavior`

| Key | Default | Meaning |
| --- | --- | --- |
| `startWithWindows` | `false` | Per-user `Run` key. No elevation. |
| `skipWhenFullscreenAppActive` | `true` | Skip while a game or presentation owns the screen. |
| `enablePreviewHotkeys` | `true` | Ctrl+Alt+Shift+C / O. |
| `warmCapture` | `false` | Keep a capture backend initialized for zero acquisition latency. |
| `captureTimeoutMs` | `45` | Give up and try the next backend after this. |
| `captureBackend` | `Auto` | `Auto` / `DesktopDuplication` / `WindowsGraphicsCapture` / `Gdi`. |
| `includeCursorInSnapshot` | `true` | Composite the pointer, which capture APIs omit. |

### `diagnostics`

| Key | Default | Meaning |
| --- | --- | --- |
| `showDebugHud` | `false` | Developer overlay. Off in the shipped default. |
| `enableFileLogging` | `true` | Rolling log under `%LOCALAPPDATA%\LidFlow\Logs`. |
| `verboseFrameLogging` | `false` | Debug-level logging. For shader tuning only. |
| `previewHoldMs` | `350` | Hold the final preview frame so a still can be inspected. |

Out-of-range values are clamped on load rather than rejected — a hand-edited
config can never produce a broken effect, and a 400-test fuzz pass asserts no
configuration yields a non-finite shader parameter.

---

## Failure handling

| Failure | Response |
| --- | --- |
| Capture fails on close | Skip the animation entirely; stock Windows behaviour. |
| Capture fails on open | Reveal using the pre-close snapshot if there is one, else drop the overlay so the user sees their desktop rather than black. |
| No D3D11 device | Fall back to WARP, then run resident and inert. |
| Device lost / GPU removed | Detected via `DeviceRemovedReason`; rebuild the whole graphics stack. |
| Display disconnected, mode or DPI change | Abort the transition, re-enumerate, re-target; rebuild the device if the adapter changed. |
| Suspend mid-animation | Stop rendering, leave the overlay black. |
| Display powers off mid-animation | Jump to the final frame and stop. |
| Shader compilation fails | Logged, app runs without the effect rather than crashing. |
| `config.json` malformed | Defaults, with the parse error logged. |
| Logging sink fails repeatedly | Disabled permanently rather than retried every frame. |
| Two instances | Named mutex; the second explains itself and exits. |
