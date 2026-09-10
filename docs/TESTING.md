# LidFlow — testing

## Automated

```cmd
dotnet test tests\LidFlow.Core.Tests\LidFlow.Core.Tests.csproj
```

**125 tests, green.** They run on any OS — no GPU, no display, no lid — because
everything worth asserting lives in the platform-neutral `LidFlow.Core`. CI runs
them on `windows-latest` as part of `BUILD-WINDOWS.ps1`.

### What is covered, and why those things

| Area | Notable assertions |
| --- | --- |
| **Easing** | Every preset pins both endpoints exactly. Ease-out presets are monotonic over 200 samples. Input is clamped, not extrapolated. The Bezier solver survives a near-degenerate curve (`0.001, 0.999, 0.999, 1`) — the case that breaks Newton-Raphson and needs the bisection fallback. `LidClose`/`LidOpen` cover more than half their distance in the first third of their time, and the open curve leads the close curve at every early sample. |
| **Animation model** | The closing first frame leaves the snapshot *completely* untouched: aperture outside the panel, every effect term zero, luminance exactly 1. The last frame has zero aperture height. Aperture height is monotonic over 300 samples. The top edge travels further than the bottom (the hinge asymmetry). Blur is zero at both ends and positive in between. With full velocity influence, blur tracks speed rather than progress. Opening is *not* the time-reverse of closing. Per-quality tap counts. |
| **Reversal** | Progress inversion round-trips within 0.02 in both directions, and an open animation resumed at a matched time shows the same aperture as the close animation it replaced — the property that makes a mid-flight reversal continuous rather than a jump. |
| **Fuzz** | 400 random configurations × both directions × 17 samples, asserting every one of ~20 output parameters is finite. This is the guard against a hand-edited `config.json` producing NaN, which on the GPU shows as a garbage frame rather than an error. |
| **State machine** | Both happy paths. Capture failure on close skips the animation; on open it *drops* the overlay rather than leaving the user staring at black. Reversal from mid-animation in both directions, carrying the on-screen panel position. 50 rounds of rapid toggling never strand the overlay or stack animations. Suspend mid-close leaves it black; suspend while idle leaves it down. A resume alone does not assume the lid opened. `Abort` from all six live states returns to idle with the overlay down. Disable/enable from any state. Unknown triggers are ignored. `NaN` progress is treated as zero. |
| **Hinge angle** | Closed/open clamping. Monotonic over 0–120°. The mapping follows `sin(angle)`, not the angle — asserted by checking the mid-angle maps to ~29% closed, not 50%. Inverted config ranges tolerated. Non-finite inputs never produce non-finite output. Velocity saturates and ignores direction. Derived lid state uses hysteresis so sensor noise cannot flip it. |
| **Hinge-driven rendering** | Timed and angle-driven paths produce *identical* geometry at the same panel position, so the two input modes cannot look like different effects. A stationary lid has exactly zero motion blur while the aperture stays put. |
| **Lid parsing** | Only the documented `0`/`1` are accepted; every other value and every wrong payload length is `Unknown`. Duplicate states are suppressed — load-bearing, because a repeated "closed" mid-animation would otherwise restart the transition. |
| **Monitor selection** | Internal panel wins over an external primary. Highest confidence wins. A single unidentified display is used but flagged as a guess. Several unidentified displays fall back to primary and say so. `AllDisplays` and `PrimaryDisplay` honour the external opt-in. Manual matches by device path or device name. A missing manual target falls back and reports why. Empty-bounds displays (the internal panel while docked) are never selected. |
| **Configuration** | Defaults land inside the brief's envelopes (close 250–420 ms, open 220–380 ms, open faster than close) and are safe (internal panel only, no debug HUD, no auto-start). Hostile values are clamped. Null sections are replaced. JSON round-trips. Enums serialize as readable names. Missing files, malformed files, partial configs, comments and trailing commas all handled. Saving is atomic and leaves no `.tmp`. `Clone` is deep. |

### What automated tests deliberately do **not** cover

The GPU pipeline, the overlay window, the capture backends and the power plumbing
are not unit-tested. They are thin adapters over Windows APIs whose behaviour
cannot be faithfully faked, and a mock of `IDXGIOutputDuplication` would only
assert that the mock was called. Their correctness rests on compile-time checking,
the documented contracts recorded in [RESEARCH.md](RESEARCH.md), and the manual
checklist below.

---

## Manual checklist

Requires a physical laptop. Run `LidFlow.exe --debug` so the developer overlay
shows state, FPS, capture backend and latency, the selected display and the input
mode.

Record the machine first: model, Windows build, GPU(s), panel resolution and DPI,
refresh rate, lid-close action, and whether sign-in on wake is required.

### Core loop

| # | Test | Expected |
| --- | --- | --- |
| 1 | `--preview-close` | Aperture closes from the edges to a hinge line below centre. Content does not scale. Ends fully black. |
| 2 | `--preview-open` | Reverse, visibly quicker and more energetic. Ends on the live desktop with no seam. |
| 3 | Frame 1 of a close | **Indistinguishable from the desktop.** No flash, no dim, no shift, no cursor disappearing. |
| 4 | Last frame of a close | Pure black. No visible rectangle edge or grey banding. |
| 5 | Close the lid slowly | Animation plays; note how much is visible before the panel dies. |
| 6 | Close the lid quickly | Same, no stutter. |
| 7 | Open the lid | Reveal plays from black. |
| 8 | Rapid toggle (5+ cycles) | No stuck overlay, no double animation, no black desktop left behind. |
| 9 | Toggle mid-animation | Reverses **from where the panel is**, without jumping. |

### Visual quality

| # | Test | Expected |
| --- | --- | --- |
| 10 | Watch the boundary | Soft, with a luminance gradient ahead of it. Not a hard rectangle. |
| 11 | Watch the centre | Pixel-stable throughout. |
| 12 | Watch the top vs bottom edge | Top travels further and faster. Aperture narrows toward the top. |
| 13 | Blur | Present near the moving edge, absent in the centre, decaying as the motion settles. |
| 14 | Dark gradient on an 8-bit panel | No banding. |
| 15 | On a bright white page vs a dark page | Both read as a panel dimming, not a grey wash. |

### Displays and scaling

| # | Test | Expected |
| --- | --- | --- |
| 16 | 100 / 125 / 150 / 175 / 200% scaling | Overlay covers the panel exactly. Effect looks the same at every scale. |
| 17 | Non-1080p panel (1440p, 4K, 3:2, 16:10) | Same relative geometry. Nothing clipped or letterboxed. |
| 18 | External monitor attached | Only the built-in panel animates. |
| 19 | External as primary | Still only the built-in panel (default settings). |
| 20 | `AllDisplays` + external opt-in | Both animate. |
| 21 | Mirrored displays | No duplicate or fighting overlays. |
| 22 | Unplug a display mid-animation | Transition aborts cleanly; overlay does not stick. |
| 23 | Change resolution mid-animation | Same. |
| 24 | Internal panel disabled while docked | No animation, no error. |
| 25 | Rotated display | Snapshot is correctly oriented (**unverified in development**). |

### Power

| # | Test | Expected |
| --- | --- | --- |
| 26 | Lid action = Sleep | Animation plays as far as the panel allows; machine sleeps normally. LidFlow must not delay it. |
| 27 | Lid action = Do nothing | Animation completes; overlay held black. |
| 28 | Lid action = Hibernate | No hang, no crash. |
| 29 | Resume from sleep, lid still shut | Overlay stays black; **no reveal** (a resume is not a lid-open). |
| 30 | Resume with sign-in required | Lock screen wins; no corrupt frame or stuck overlay. |
| 31 | Resume with sign-in not required | Reveal plays. |
| 32 | Modern Standby machine | Behaves as above, with a shorter visible window. |
| 33 | Battery vs AC | No difference beyond the OS's own lid action. |
| 34 | Verify power settings untouched | `powercfg /q` before and after — identical. |

### Application compatibility

| # | Test | Expected |
| --- | --- | --- |
| 35 | Chrome, Edge, Firefox windowed | Snapshot correct, windows unmodified afterwards. |
| 36 | Browser video playing | Video frame captured (or, on the GDI fallback, documented as possibly black). |
| 37 | Borderless-fullscreen game | Skipped by default. |
| 38 | Exclusive-fullscreen D3D game | Skipped. Game does **not** drop out of fullscreen. |
| 39 | HDR content on an HDR panel | Default path flattens HDR (expected). WGC backend preserves it, with the capture border. |
| 40 | Photoshop / Illustrator / After Effects / Figma | Snapshot correct; no window state changed after the transition. |
| 41 | Click through the overlay area right after a transition | Input goes to the app underneath. |
| 42 | Focus | The overlay never steals focus; the foreground window is unchanged. |
| 43 | Taskbar and Alt+Tab | LidFlow's overlay appears in neither. |

### Robustness

| # | Test | Expected |
| --- | --- | --- |
| 44 | Launch a second instance | Message, then exits. |
| 45 | Delete `config.json` | Defaults; app runs. |
| 46 | Corrupt `config.json` | Defaults; parse error logged. |
| 47 | Set every value out of range | Clamped; effect still sane. |
| 48 | Disable animation in settings | Nothing draws; hotkeys inert. |
| 49 | `startWithWindows` on, then reboot | Starts, tray icon present. |
| 50 | `INSTALL.cmd` then `UNINSTALL.cmd` | Clean install, clean removal, `Run` key gone. |
| 51 | Run from a read-only folder | Works; config and logs under `%LOCALAPPDATA%`. |
| 52 | Update the GPU driver while running | Device-lost recovery, or a clean skip. |
| 53 | Run for 24 h with many transitions | No handle, memory or GPU-resource growth. |
| 54 | Check the log | Lid state, monitor, capture result, animation state. **No screen content, no user data.** |
| 55 | Confirm no network traffic | e.g. Resource Monitor — LidFlow makes no connections. |

### Performance

| # | Test | Expected |
| --- | --- | --- |
| 56 | FPS in `--debug` during a transition | At the panel's refresh rate: 60 Hz min, 120 Hz where supported. |
| 57 | GPU frame time | Well under one frame at native resolution. |
| 58 | CPU while idle | Effectively zero — the app is event-driven with no polling. |
| 59 | Capture latency in `--debug` | Small enough that the transition starts immediately. |
| 60 | Input lag during a transition | None perceptible; the message pump stays serviced. |

### If a hinge-angle sensor is present

| # | Test | Expected |
| --- | --- | --- |
| 61 | `--debug` input mode | Reports "hinge angle sensor". |
| 62 | Move the lid slowly | Aperture tracks the hinge one-to-one. |
| 63 | Stop half way | Aperture holds position and is **sharp** — blur decays to nothing. |
| 64 | Reverse direction mid-move | Follows the hinge with no jump. |
| 65 | Move fast | Blur scales with actual speed. |

---

## Reporting a problem

Include: the machine details above, `%LOCALAPPDATA%\LidFlow\Logs\lidflow.log`,
the debug overlay contents, and whether the preview hotkeys reproduce it. The
preview path is identical to the real one, so "previews work but the lid does not"
is a very different problem from "both fail" and worth stating either way.
