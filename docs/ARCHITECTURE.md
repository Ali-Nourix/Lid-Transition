# LidFlow — architecture

Companion to [RESEARCH.md](RESEARCH.md), which explains *why* these choices were
made. This document explains *what* the code does.

---

## The pipeline

```
                      GUID_LIDSWITCH_STATE_CHANGE   (whether)
                    + HingeAngleSensor / lid inclinometer   (where)
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
| Lid | `HingeAngleMapping` | Hinge sensor angle → panel progress. Linear: the geometry lives in the projection. |
| Lid | `LidAngleTracker` | Lid-inclinometer pitch → panel position, self-calibrating from the lid switch's two events. |
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
| Lid | `InclinometerMonitor` | `Inclinometer`, falling back to `Accelerometer`, for lid pitch. |
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

### The model

The panel is treated as a real rectangle hinged along its bottom edge, with the
desktop image painted on it, rotating away from the viewer. Nothing slides the
content around independently; what changes is where that rectangle projects to on
screen.

Three things then fall out of the geometry rather than being layered on:

- **Black grows from the top and the top corners**, because that is where the
  foreshortened, keystoned panel stops covering the display.
- **Blur is strongest at the top and vanishes at the hinge**, because a point's
  speed is proportional to its distance from the hinge. The hinge edge barely
  moves, so it stays sharp.
- **The top loses contrast first**, because it is the most off-axis.

### The projection

In a side view with the hinge at the origin, a point at distance `s` along the
panel sits at height `s·cos θ` and depth `s·sin θ`. A pinhole projection with
`k = 1 / viewing distance` divides by `1 + z·k`, so its projected height above
the hinge is

```
screenT(s) = s·cos θ / (1 + s·sin θ·k)
```

which inverts in closed form — which is what lets the whole thing be one pass
with no iteration:

```
s = screenT / (cos θ − screenT·sin θ·k)
```

The same divisor widens the panel's projected width, and that is the keystone:
rows further up are narrower on screen, so screen pixels beyond them fall off the
panel and read as black.

At `θ = 0` this is exactly the identity — verified numerically to 6×10⁻⁸ over a
41×41 grid — so the panel lands precisely on the physical display and the first
frame is pixel-identical to the captured desktop. At `θ = 90°` the panel is
edge-on and covers nothing, so the last frame is pure black. Neither endpoint is
a tuned approximation; both are properties of the construction, and both are
asserted by tests.

`s` is the natural variable for the whole effect, and nearly every optical term
below is a function of it.

### Per pixel, in `LidTransition.hlsl`

1. **Inverse projection** — screen pixel to panel position `s` and content
   coordinate. Rows past the horizon have no solution and are simply not on the
   panel.
2. **Coverage** — how far inside the panel the content coordinate is, measured in
   panel units and antialiased with the screen-space derivative (`fwidth`), so the
   edge stays crisp however strongly the projection is compressing that region. A
   fixed softness would smear badly near the horizon.
3. **Early out** — fully occluded pixels return black before any sampling. The
   held-black final frame therefore costs almost nothing.
4. **Glass refraction** — a small quadratic term toward the far edge.
5. **Rotation transform** for rotated displays (Desktop Duplication delivers an
   un-rotated surface).
6. **Directional blur** — radius `MaxBlur · s^BlurFalloff`, gated on rotation
   speed, smeared vertically (which is where panel motion projects). Wide radii
   are served by the mip chain, so the tap count stays 5/9/13 regardless of
   resolution.
7. **Pointer composite**, at the same content coordinate.
8. **Off-axis wash** — contrast and saturation fall with `s`.
9. **Luminance falloff** — `ShadowStrength · s^ShadowFalloff`, the wide soft
   gradient that stops the boundary reading as a drawn rectangle.
10. **Specular sweep** — cool-tinted additive highlight on the leading edge,
    strongest while the panel is turning.
11. **Global dim, then occlusion** to black, plus the bezel ambient just outside
    the panel.
12. **Triangular dither** from two decorrelated noise samples, amplitude under one
    8-bit code value.

All of it in linear light: the snapshot is read through an `_SRGB` view and the
render target is an `_SRGB` view over a UNORM flip-model back buffer, so the
hardware does both transfers.

## Configuration reference

`%LOCALAPPDATA%\LidFlow\config.json`, or `config.json` beside the executable
(which wins). Values named `*Px` are **reference pixels at a 1080-tall panel** and
are scaled by the real panel height; everything else is a 0..1 fraction.

### `animation`

| Key | Default | Meaning |
| --- | --- | --- |
| `enableAnimation` | `true` | Master switch. False keeps the app resident but silent. |
| `useHingeAngleWhenAvailable` | `true` | Track a hinge-angle sensor when the hardware has one (tier 1). |
| `useInclinometerWhenAvailable` | `true` | Otherwise track a lid-mounted inclinometer, calibrated against the lid switch (tier 2). |
| `allowInclinometerEarlyClose` | `false` | Let detected lid motion *start* a close, before the switch fires. Gives the close real runway, at the cost of the ambiguity between a moving lid and a moving laptop. |
| `inclinometerCalibration` | learned | Persisted pitch range. Written automatically; delete it to re-learn. |
| `hingeClosedAngleDeg` | `4` | Sensor angle at or below which the lid counts as closed. |
| `hingeOpenAngleDeg` | `55` | Sensor angle at or above which the effect is fully cleared. |
| `hingeVelocityReference` | `3` | Progress-per-second that saturates the blur in hinge mode. |
| `closeDurationMs` | `340` | Closing duration (timed mode). |
| `openDurationMs` | `285` | Opening duration. Shorter on purpose. |
| `panelMaxAngleDeg` | `90` | Total rotation swept, in degrees. 90 ends edge-on, which is what makes the final frame pure black by construction. Lowering it means the last frame is not fully black. |
| `perspectiveStrength` | `0.85` | Reciprocal viewing distance, in panel heights. **The keystone strength** — how much more the top narrows than the bottom, and the main cue that the panel is rotating rather than scaling. `0` gives an orthographic squash with no convergence. |
| `hingeOffset` | `0.05` | How far below the visible panel the hinge sits, in panel heights. A real hinge is behind the bottom bezel, so the bottom row foreshortens slightly too rather than being pinned dead still. |
| `edgeSoftness` | `2.6` | Soft width of the panel's own edge. |
| `maxBlur` | `56` | **Blur radius at the far (top) edge.** Blur is neither uniform nor edge-localized: it scales with distance from the hinge, because that is proportional to how fast that row is moving. The hinge edge stays sharp. |
| `blurFalloff` | `1.15` | Exponent shaping the blur gradient along the panel. `1` is linear in distance from the hinge; higher concentrates it toward the top. |
| `blurVelocityInfluence` | `0.72` | How much blur follows rotation speed vs. elapsed time. At `1.0` a stationary panel is perfectly sharp. |
| `blackOpacity` | `1.0` | Opacity of the region the panel no longer covers. Below 1 is for tuning only. |
| `shadowStrength` | `0.55` | Depth of the luminance falloff toward the far edge. |
| `shadowFalloff` | `1.6` | Exponent shaping that falloff along the panel. |
| `glareStrength` | `0.055` | Highlight running along the panel's leading edge. |
| `glareWidth` | `0.10` | Width of that highlight, as a fraction of the panel's length. |
| `distortionStrength` | `0.5` | Optical (glass) term near the far edge. |
| `offAxisWash` | `0.35` | Contrast and saturation loss toward the far edge. |
| `globalDim` | `0.10` | Overall luminance loss at full close. |
| `bezelAmbient` | `0.010` | Ambient light the bezel picks up just outside the panel edge. Small, but it is what stops the boundary reading as a hole cut in the image rather than an object occluding it. `0` gives a pure-black edge. |
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
