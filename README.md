# LidFlow

A lid-close/open transition for Windows laptops. When the lid moves, LidFlow
freezes the desktop and closes a physical-looking aperture over it, so the
content appears to stay where it is while the panel closes around it.

It is not a window animation and not a fade to black. The snapshot is never
scaled; what changes is the aperture, plus the optical treatment of the pixels
near its moving edge.

---

## Read this first: when you can actually see it

LidFlow can only draw while the panel is still lit and the lid is still open far
enough to see. Windows fires the lid switch near the end of the lid's travel and
then powers the internal panel off, and **no application can change that** —
`SetThreadExecutionState` is documented as unable to prevent a lid-close sleep,
and `PBT_APMQUERYSUSPEND` has been ignored since Vista.

So:

| Your "when I close the lid" setting | What you see |
| --- | --- |
| **Sleep** or **Hibernate** (usual default) | The animation plays in the window between the switch firing and the panel going dark. Short, but it is the case the timings are tuned for. |
| **Do nothing** | Same visible window — Windows still powers the panel off — but nothing interrupts the animation. Best case. |
| **Shut down** | Little or nothing. |

LidFlow **never changes your power settings.** If you want to try "Do nothing",
change it yourself in Control Panel → Power Options → *Choose what closing the lid
does*.

The opening animation is fully visible when the session is not locked. If Windows
requires sign-in on wake, the lock screen is drawn on a separate secure desktop
that no application can render to, and it wins.

The full reasoning, with sources, is in [docs/RESEARCH.md](docs/RESEARCH.md).

**Tune it without closing your laptop**: press <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>Shift</kbd>+<kbd>C</kbd>
to preview a close and <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>Shift</kbd>+<kbd>O</kbd>
for an open. These run the identical pipeline a real lid event does.

---

## Requirements

- **Windows 11**, or **Windows 10 version 2004 (build 19041)** or later.
  On older Windows 10 the overlay cannot be excluded from screen capture, which
  disables the opening animation. LidFlow detects this and says so in its log.
- A Direct3D 11 capable GPU. If none is usable it falls back to WARP, and if that
  fails too it stays resident and skips the effect rather than crashing.
- **No administrator rights. No network access. No telemetry.**

---

## Install

Download `LidFlow-Windows-x64.zip` from
[Releases](https://github.com/Ali-Nourix/Lid-Transition/releases), extract it, and
either:

- run `LidFlow.exe` directly — it is self-contained and needs no .NET install; or
- run `INSTALL.cmd`, which copies it to `%LOCALAPPDATA%\Programs\LidFlow`,
  registers it to start at sign-in and launches it.

`UNINSTALL.cmd` reverses all of that and asks before deleting your settings.

LidFlow lives in the notification area. Double-click the tray icon for settings.

---

## Build from source

```cmd
BUILD-WINDOWS.cmd
```

That is the whole thing. It checks prerequisites, restores, builds, runs the unit
tests, publishes and packages.

**The only prerequisite is the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)**
(or newer). No Windows SDK, no `fxc`, no Visual Studio Build Tools, no C++
toolchain — the shader is compiled at runtime by the `d3dcompiler` that ships with
Windows. If something is missing, the script says exactly what and how to get it,
and never installs anything itself.

Output:

```
dist/
  LidFlow/                      <- runnable payload
    LidFlow.exe
    config.example.json
    ...
  LidFlow-Windows-x64.zip
  LidFlow-Windows-Source.zip
```

Useful switches:

```cmd
BUILD-WINDOWS.cmd -SkipTests
BUILD-WINDOWS.cmd -NoZip
powershell -File BUILD-WINDOWS.ps1 -SelfContained:$false   # ~5 MB, needs .NET Desktop Runtime
```

The self-contained archive is around 80 MB because it bundles the .NET runtime;
`-SelfContained:$false` produces a much smaller build for machines that already
have the runtime.

### Building on Linux or macOS

The projects **compile** anywhere (`dotnet build LidFlow.sln`) and the test suite
**runs** anywhere, because all the interesting logic lives in a platform-neutral
assembly. Publishing a runnable Windows binary needs a Windows host — the CI
workflow does exactly that on `windows-latest`.

---

## Command line

```
LidFlow.exe                  Run in the notification area (normal use)
LidFlow.exe --preview-close  Play the closing transition once at start-up
LidFlow.exe --preview-open   Play the opening transition once at start-up
LidFlow.exe --debug          Show the developer overlay
LidFlow.exe --version
LidFlow.exe --help
```

---

## Settings

Tray icon → **Settings**, or double-click the tray icon. The dialog exposes what
changes behaviour; the full tuning surface lives in `config.json`.

| Location | Purpose |
| --- | --- |
| `%LOCALAPPDATA%\LidFlow\config.json` | Your settings |
| `config.json` next to `LidFlow.exe` | Portable override — takes precedence, so a tuning config can travel with the build |
| `%LOCALAPPDATA%\LidFlow\Logs\` | Rolling log: lid state, monitor selection, capture results, animation state, exceptions. Never screen content. |

[`config.example.json`](config.example.json) is generated from the real defaults,
so it cannot drift. Every value is documented in
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#configuration-reference).

A malformed `config.json` never stops the app: it falls back to defaults and
records why.

### The values worth knowing about

| Key | Default | What it does |
| --- | --- | --- |
| `closeDurationMs` / `openDurationMs` | `340` / `285` | Opening is deliberately shorter — it should feel more energetic. |
| `hingeBias` | `0.62` | Where the aperture converges, as a fraction of panel height. Below centre, so the top edge travels further and faster than the bottom. **This is the single most important value for the illusion**: at `0.5` it reads as a camera shutter, not a hinge. |
| `perspectiveStrength` | `0.85` | How much the aperture narrows toward the top (keystone). The main "rotating away" cue. |
| `maxBlur` / `blurRadius` | `34` / `175` | Peak blur radius, and the distance inside the edge over which it ramps. Reference pixels at a 1080-tall panel. |
| `blurVelocityInfluence` | `0.72` | How much blur follows the edge's *speed* rather than elapsed time. At `1.0` a stationary panel is perfectly sharp. |
| `shadowStrength` / `shadowExtentPx` | `0.88` / `155` | Depth and reach of the luminance falloff behind the edge — what stops the boundary reading as a drawn rectangle. |
| `monitor.mode` | `InternalPanel` | Only the built-in panel physically moves, so external displays need an explicit opt-in. |
| `useHingeAngleWhenAvailable` | `true` | See below. |

Pixel-named values are authored against a 1080-tall panel and scaled by the real
panel height, so the effect looks identical at 1080p, 1440p and 4K, and at every
DPI scale.

---

## Hinge-angle tracking

If your machine has a hinge-angle sensor (dual-screen and foldable hardware —
most clamshell laptops do not), LidFlow reads it and the aperture follows the
hinge one-to-one instead of playing a timed animation. Stopping half way, moving
slowly and reversing then all behave correctly, and the motion blur comes from how
fast you are actually moving the lid.

It is on by default, detected at start-up, and its absence is not an error —
`--debug` shows which input mode is active. Ordinary laptops use the timed path,
which is why the easing curves and durations matter as much as they do.

---

## What it does not do

- It does not change your power settings, lid-close action, hibernate settings or
  power plan. Ever.
- It does not claim to keep rendering after Windows powers the panel down. It
  cannot, and it does not try.
- It does not animate external monitors unless you ask it to.
- It does not run while a full-screen game or presentation is active, because a
  topmost overlay can drop those out of exclusive fullscreen.
- It does not talk to the network, and it does not log what is on your screen.

---

## Troubleshooting

**Nothing happens when I close the lid.** Check
`%LOCALAPPDATA%\LidFlow\Logs\lidflow.log` for `Lid switch:` lines. If there are
none, Windows is not reporting a lid device — try the preview hotkeys to confirm
the pipeline works. If the previews work and real lid events do not, the panel is
probably powering off before anything is drawn; try lid-close action "Do nothing".

**A yellow border flashes around the screen.** You have selected the
`WindowsGraphicsCapture` backend. Windows draws that border for unpackaged apps
and it cannot be turned off. Set `behavior.captureBackend` back to `Auto`.

**It runs on the wrong monitor.** `monitor.mode` is `InternalPanel` by default but
falls back to primary when the built-in panel cannot be identified — `--debug`
shows the selection reason. Use `Manual` and pick the display explicitly.

**The animation looks like a fade, not a panel.** Check that `hingeBias` is not
near `0.5` and that `perspectiveStrength` is non-zero.

---

## Repository layout

```
src/LidFlow.Core     Platform-neutral: animation model, state machine, easing,
                     configuration, monitor selection. Unit-tested on any OS.
src/LidFlow.App      Windows: Win32 interop, D3D11/DXGI, DirectComposition
                     overlay, capture backends, tray, settings.
tests/               125 unit tests over LidFlow.Core.
docs/RESEARCH.md     What was established before implementation, with sources,
                     and the platform limits that bound the effect.
docs/ARCHITECTURE.md Pipeline, module map, state machine, config reference.
docs/TESTING.md      Automated coverage and the manual hardware checklist.
```

## Verification status

- **125 unit tests** over the animation model, state machine, easing, hinge-angle
  mapping, configuration and monitor selection — green.
- The **whole solution compiles clean**, Windows app included.
- **CI builds it on a real `windows-latest` runner** with the same
  `BUILD-WINDOWS.ps1` you would run, and publishes the artifacts.
- **The on-screen result has not been verified on physical hardware.** A CI runner
  has no laptop lid and no display. [docs/TESTING.md](docs/TESTING.md) is the
  manual checklist for that.

## Licence

MIT — see [LICENSE](LICENSE).

LidFlow is an independent recreation of an observed visual behaviour. It contains
no Apple code, assets or trademarks, and makes no claim about how Apple or any
other vendor implements anything. See
[docs/RESEARCH.md §9](docs/RESEARCH.md#9-apple-visual-behaviour-analysis).
