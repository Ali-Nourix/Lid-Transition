using System;
using System.Collections.Generic;
using System.Diagnostics;
using LidFlow.App.Capture;
using LidFlow.App.Lid;
using LidFlow.App.Interop;
using LidFlow.App.Monitors;
using LidFlow.App.Overlay;
using LidFlow.App.Power;
using LidFlow.App.Rendering;
using LidFlow.Core.Animation;
using LidFlow.Core.Configuration;
using LidFlow.Core.Diagnostics;
using LidFlow.Core.Lid;
using LidFlow.Core.Monitors;
using LidFlow.Core.Power;
using LidFlow.Core.StateMachine;

namespace LidFlow.App;

/// <summary>
/// Ties the lid events, the state machine, the capture pipeline and the renderer
/// together, and owns every graphics resource.
/// <para>
/// Everything here runs on the single UI thread. Frames are driven by posted
/// messages (see <c>PowerEventWindow.RequestFrame</c>), which keeps the message
/// pump serviced between frames so an interrupting lid event is seen immediately,
/// and keeps all Direct3D use on one thread.
/// </para>
/// </summary>
internal sealed class TransitionController : IDisposable
{
    private readonly PowerEventWindow _messageWindow;
    private readonly ILidFlowLog _log;

    private readonly TransitionStateMachine _machine;
    private readonly Stopwatch _clock = new();

    private LidFlowConfig _config;
    private GraphicsDevice? _graphics;
    private OverlayWindow? _overlay;
    private TransitionRenderer? _renderer;
    private DesktopSnapshot? _snapshot;
    private CursorSnapshot? _cursor;
    private CaptureService? _capture;

    private List<DisplayTarget> _displays = new();
    private DisplayTarget? _activeDisplay;
    private MonitorSelectionReason _selectionReason = MonitorSelectionReason.NoTarget;

    private LidAnimationModel? _model;
    private TransitionKind _kind;
    private float _startLinearT;
    private float _panelProgress;

    private bool _graphicsReady;
    private bool _frameQueued;
    private bool _disposed;

    // Continuous lid position
    private readonly HingeAngleMonitor _hinge;
    private readonly InclinometerMonitor _inclinometer;
    private readonly LidAngleTracker _angleTracker;
    private double _hingeProgressRate;
    private float _lastHingeProgress = float.NaN;
    private long _lastHingeStamp;

    // Diagnostics
    private double _lastFrameMs;
    private int _framesThisTransition;
    private double _lastFps;
    private long _lastFrameStamp;

    public TransitionController(PowerEventWindow messageWindow, LidFlowConfig config, ILidFlowLog log)
    {
        _messageWindow = messageWindow ?? throw new ArgumentNullException(nameof(messageWindow));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? NullLog.Instance;

        _machine = new TransitionStateMachine(config.Animation.EnableAnimation);

        _hinge = new HingeAngleMonitor(_log);
        _inclinometer = new InclinometerMonitor(_log);
        _angleTracker = new LidAngleTracker(config.Animation.InclinometerCalibration);

        _messageWindow.FrameRequested += (_, _) => OnFrame();
        _messageWindow.Suspending += (_, _) => Fire(TransitionTrigger.Suspending);
        _messageWindow.Resumed += (_, _) => OnResumed();
        _messageWindow.DisplayConfigurationChanged += (_, _) => OnDisplayConfigurationChanged();
    }

    public TransitionState State => _machine.Current;

    public DisplayPowerState DisplayPower { get; private set; } = DisplayPowerState.Unknown;

    /// <summary>Snapshot of the current diagnostics, for the debug HUD.</summary>
    public DiagnosticsSnapshot Diagnostics => new()
    {
        State = _machine.Current,
        PanelProgress = _panelProgress,
        Fps = _lastFps,
        LastFrameMs = _lastFrameMs,
        CaptureBackend = _capture?.LastBackend ?? "-",
        CaptureLatencyMs = _capture?.LastLatencyMs ?? 0d,
        Display = _activeDisplay?.Info,
        SelectionReason = _selectionReason,
        Adapter = _graphics?.AdapterName ?? "-",
        SoftwareRenderer = _graphics?.IsSoftware ?? false,
        CaptureExcluded = _overlay?.ExcludedFromCapture ?? false,
        DisplayPower = DisplayPower,
        AngleSource = AngleSource,
        HingeAngleDegrees = _hinge.Current?.AngleDegrees,
        LidPitchDegrees = _inclinometer.IsAvailable ? _angleTracker.LastPitchDegrees : null,
        PitchSensor = _inclinometer.SourceName,
        CalibrationSweepDegrees = _angleTracker.IsCalibrated ? _angleTracker.SweepDegrees : null,
    };

    /// <summary>
    /// True when the panel position is read from a real hinge-angle sensor.
    /// Absolute and exact, so it is preferred over everything else.
    /// </summary>
    private bool IsHingeTracking => _config.Animation.UseHingeAngleWhenAvailable
        && _hinge.IsAvailable
        && _hinge.Current is not null;

    /// <summary>
    /// True when the panel position is read from a calibrated lid inclinometer.
    /// Relative rather than absolute, but available on far more machines.
    /// </summary>
    private bool IsInclinometerTracking => !IsHingeTracking
        && _config.Animation.UseInclinometerWhenAvailable
        && _inclinometer.IsAvailable
        && _angleTracker.IsCalibrated;

    /// <summary>Which continuous source, if any, is driving the current transition.</summary>
    private LidAngleSourceKind AngleSource => IsHingeTracking
        ? LidAngleSourceKind.HingeAngleSensor
        : (IsInclinometerTracking ? LidAngleSourceKind.LidInclinometer : LidAngleSourceKind.None);

    // ------------------------------------------------------------------ startup

    /// <summary>
    /// Builds the display list, selects a target and creates the graphics stack.
    /// Returns false when the machine cannot support the effect, in which case the
    /// app stays resident but inert rather than failing to start.
    /// </summary>
    public bool Initialize()
    {
        if (_config.Animation.UseHingeAngleWhenAvailable && _hinge.TryStart())
        {
            // Readings arrive on a sensor thread. PostMessage is thread-safe, so
            // the frame request simply hops to the UI thread, where every graphics
            // call already lives.
            _hinge.AngleChanged += (_, _) =>
            {
                if (_machine.IsAnimating)
                {
                    _messageWindow.RequestFrame();
                }
            };
        }

        if (_config.Animation.UseInclinometerWhenAvailable && !_hinge.IsAvailable && _inclinometer.TryStart())
        {
            _inclinometer.PitchChanged += (_, sample) =>
            {
                _angleTracker.Update(sample.PitchDegrees, sample.TimestampSeconds);

                if (_machine.IsAnimating)
                {
                    _messageWindow.RequestFrame();
                }
            };

            if (_angleTracker.IsCalibrated)
            {
                _log.Info(
                    $"Using a persisted inclinometer calibration (sweep {_angleTracker.SweepDegrees:0.#} deg); " +
                    "the lid position will be tracked from the first transition.");
            }
            else
            {
                _log.Info(
                    "Inclinometer available but not yet calibrated. The first close/open cycle teaches it the " +
                    "pitch range; until then the transition is timed.");
            }
        }

        RefreshDisplays();

        if (_activeDisplay is null)
        {
            _log.Warn("No suitable display; the transition is disabled until the display configuration changes.");
            return false;
        }

        return EnsureGraphics();
    }

    private bool EnsureGraphics()
    {
        if (_graphicsReady && _graphics is not null && !_graphics.IsDeviceLost())
        {
            return true;
        }

        ReleaseGraphics();

        if (_activeDisplay is null)
        {
            return false;
        }

        // Pinned to the target display's adapter so Desktop Duplication can open
        // on it; see GraphicsDevice.TryCreate.
        _graphics = GraphicsDevice.TryCreate(_log, _activeDisplay.Info.AdapterLuid);
        if (_graphics is null)
        {
            return false;
        }

        _overlay = OverlayWindow.TryCreate(_graphics, _log);
        if (_overlay is null)
        {
            ReleaseGraphics();
            return false;
        }

        _renderer = new TransitionRenderer(_graphics, _log);
        if (!_renderer.Initialize())
        {
            ReleaseGraphics();
            return false;
        }

        _snapshot = new DesktopSnapshot(_graphics, _log);
        _cursor = new CursorSnapshot(_graphics, _log);
        _capture = new CaptureService(_graphics, _config, _log);

        if (!_overlay.Prepare(_activeDisplay, preferHdr: _config.Behavior.CaptureBackend == CaptureBackend.WindowsGraphicsCapture))
        {
            ReleaseGraphics();
            return false;
        }

        if (_config.Behavior.WarmCapture)
        {
            _capture.Warm(_activeDisplay);
        }

        _graphicsReady = true;
        return true;
    }

    private void RefreshDisplays()
    {
        _displays = DisplayEnumerator.Enumerate(_log);

        List<DisplayInfo> infos = new(_displays.Count);
        foreach (DisplayTarget target in _displays)
        {
            infos.Add(target.Info);
        }

        MonitorSelectionResult selection = MonitorSelector.Select(
            infos,
            _config.Monitor.Mode,
            _config.Monitor.ManualDisplayId,
            _config.Monitor.AllowExternalDisplays);

        _selectionReason = selection.Reason;
        DisplayInfo? chosen = selection.Primary;

        _activeDisplay = null;

        if (chosen is not null)
        {
            foreach (DisplayTarget target in _displays)
            {
                if (ReferenceEquals(target.Info, chosen))
                {
                    _activeDisplay = target;
                    break;
                }
            }
        }

        if (_activeDisplay is not null)
        {
            _log.Info($"Animating {_activeDisplay.Info} (selection: {_selectionReason}).");
        }
    }

    // ------------------------------------------------------------- lid plumbing

    public void OnLidStateChanged(LidState state)
    {
        // The switch is what calibrates the inclinometer: it supplies the two ends
        // of the pitch range that the readings in between are measured against.
        _angleTracker.ObserveLidState(state);

        switch (state)
        {
            case LidState.Closed:
                Fire(TransitionTrigger.LidClosed);
                break;

            case LidState.Open:
                Fire(TransitionTrigger.LidOpened);
                break;
        }
    }

    public void OnDisplayPowerChanged(DisplayPowerState state)
    {
        DisplayPower = state;

        // Once the panel is off there is nothing to draw on. Continuing to render
        // would burn power for no visual result, so the animation is abandoned and
        // the overlay is left black - which is also the right state to be in when
        // the panel comes back on.
        if (state == DisplayPowerState.Off && _machine.IsAnimating)
        {
            _log.Info("Display powered off mid-animation; holding the final frame.");
            CompleteAnimation(jumpToEnd: true);
        }
    }

    public void PreviewClose() => Fire(TransitionTrigger.LidClosed);

    public void PreviewOpen() => Fire(TransitionTrigger.LidOpened);

    public void ApplyConfiguration(LidFlowConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));

        Fire(config.Animation.EnableAnimation ? TransitionTrigger.Enable : TransitionTrigger.Disable);

        // Backend selection and monitor targeting can both change, so rebuild.
        _capture?.Invalidate();
        RefreshDisplays();

        if (_activeDisplay is not null && _graphicsReady && _overlay is not null)
        {
            _overlay.Prepare(_activeDisplay, preferHdr: false);
        }
    }

    private void OnResumed()
    {
        // A resume can bring back a different GPU state entirely (hibernate tears
        // the adapter down), so validate before trusting any resource.
        if (_graphics is not null && _graphics.IsDeviceLost())
        {
            _log.Info("Graphics device was lost across suspend; rebuilding.");
            _graphicsReady = false;
        }

        Fire(TransitionTrigger.Resumed);
    }

    private void OnDisplayConfigurationChanged()
    {
        _log.Info("Display configuration changed.");

        string? previousId = _activeDisplay?.Info.Id;
        long previousAdapter = _activeDisplay?.Info.AdapterLuid ?? 0;

        _capture?.Invalidate();
        RefreshDisplays();

        bool targetChanged = _activeDisplay?.Info.Id != previousId;
        bool adapterChanged = (_activeDisplay?.Info.AdapterLuid ?? 0) != previousAdapter;

        // A mid-transition topology change invalidates the snapshot's geometry, so
        // the honest response is to abandon it rather than stretch a stale frame.
        if (_machine.IsAnimating || _machine.Current == TransitionState.Closed)
        {
            Fire(TransitionTrigger.Abort);
        }

        if (adapterChanged)
        {
            _graphicsReady = false;
        }
        else if (targetChanged && _activeDisplay is not null && _overlay is not null)
        {
            _overlay.Prepare(_activeDisplay, preferHdr: false);
        }
    }

    // ----------------------------------------------------------- state machine

    private void Fire(TransitionTrigger trigger)
    {
        TransitionOutcome outcome = _machine.Fire(trigger, _panelProgress);

        if (!outcome.Accepted)
        {
            return;
        }

        _log.Debug($"{outcome.From} -> {outcome.To} ({trigger}) action={outcome.Action}");

        switch (outcome.Action)
        {
            case TransitionAction.CaptureForClose:
                BeginCapture(forOpen: false);
                break;

            case TransitionAction.CaptureForOpen:
                BeginCapture(forOpen: true);
                break;

            case TransitionAction.StartCloseAnimation:
                StartAnimation(TransitionKind.Close, outcome.StartPanelProgress);
                break;

            case TransitionAction.StartOpenAnimation:
                StartAnimation(TransitionKind.Open, outcome.StartPanelProgress);
                break;

            case TransitionAction.HoldBlack:
                HoldBlack();
                break;

            case TransitionAction.HideOverlay:
            case TransitionAction.AbortToIdle:
                HideOverlay();
                break;
        }
    }

    private void BeginCapture(bool forOpen)
    {
        if (!_config.Animation.EnableAnimation)
        {
            Fire(TransitionTrigger.CaptureFailed);
            return;
        }

        // A game or a presentation owning the screen is a deliberate skip: putting
        // a topmost overlay over exclusive-fullscreen presentation can drop the app
        // out of fullscreen, and the effect is not worth that.
        if (!forOpen && _config.Behavior.SkipWhenFullscreenAppActive && IsFullscreenAppActive())
        {
            _log.Info("A full-screen application is active; skipping the transition.");
            Fire(TransitionTrigger.CaptureFailed);
            return;
        }

        if (!EnsureGraphics() || _activeDisplay is null || _snapshot is null || _capture is null || _overlay is null)
        {
            Fire(TransitionTrigger.CaptureFailed);
            return;
        }

        bool captured = _capture.TryCapture(
            _activeDisplay,
            _snapshot,
            _overlay,
            overlayIsUp: forOpen,
            _config.Behavior.CaptureTimeoutMs);

        if (!captured)
        {
            // For the reveal we can still animate using the frame captured before
            // the panel went dark: it is what the user was last looking at, so it
            // is a defensible thing to open onto.
            if (forOpen && _snapshot.HasContent)
            {
                _log.Info("Reveal is using the pre-close snapshot because a fresh capture was unavailable.");
                Fire(TransitionTrigger.CaptureSucceeded);
                return;
            }

            Fire(TransitionTrigger.CaptureFailed);
            return;
        }

        if (_config.Behavior.IncludeCursorInSnapshot && _cursor is not null)
        {
            // The pointer usually lives on a hardware plane and is therefore absent
            // from every capture API, so it is grabbed separately and composited by
            // the shader. Failing is fine - it just means no pointer.
            _cursor.TryCapture(_activeDisplay.Info.Bounds);
        }
        else
        {
            _cursor?.Clear();
        }

        Fire(TransitionTrigger.CaptureSucceeded);
    }

    private void StartAnimation(TransitionKind kind, float startPanelProgress)
    {
        if (_activeDisplay is null || _overlay is null || _renderer is null || _snapshot is null)
        {
            Fire(TransitionTrigger.Abort);
            return;
        }

        _kind = kind;
        _model = new LidAnimationModel(_config.Animation, kind);

        // Resume at the panel position already on screen rather than at this
        // animation's natural start, so reversing mid-flight is continuous.
        _startLinearT = _model.FindLinearTimeForPanelProgress(startPanelProgress);

        _framesThisTransition = 0;
        _clock.Restart();
        _lastFrameStamp = Stopwatch.GetTimestamp();

        _lastHingeProgress = float.NaN;
        _lastHingeStamp = 0;
        _hingeProgressRate = 0d;

        // Render and present the first frame BEFORE showing the window. With no
        // redirection surface and content already committed, the first thing that
        // ever appears on screen is a correct frame - there is no empty buffer for
        // DWM to put up, which is what would otherwise show as a black flash.
        if (!RenderCurrentFrame())
        {
            Fire(TransitionTrigger.Abort);
            return;
        }

        _overlay.Show();
        QueueFrame();
    }

    private void HoldBlack()
    {
        if (_overlay is null || _renderer is null || _model is null)
        {
            HideOverlay();
            return;
        }

        _clock.Stop();
        _panelProgress = 1f;

        // One last frame at full close: the aperture has zero height, so this is
        // pure black. Held rather than hidden, so that if the panel lights up again
        // the user sees black and not their desktop.
        LidFrameParameters frame = new LidAnimationModel(_config.Animation, TransitionKind.Close).Evaluate(1f);
        RenderFrame(frame);
        _overlay.Show();
    }

    private void HideOverlay()
    {
        _clock.Stop();
        _panelProgress = 0f;
        _cursor?.Clear();
        _overlay?.Hide();
    }

    // ------------------------------------------------------------- frame loop

    private void QueueFrame()
    {
        if (_frameQueued)
        {
            return;
        }

        _frameQueued = true;
        _messageWindow.RequestFrame();
    }

    private void OnFrame()
    {
        _frameQueued = false;

        if (!_machine.IsAnimating || _model is null)
        {
            return;
        }

        if (AngleSource != LidAngleSourceKind.None)
        {
            // Tracking mode has no duration: the transition is over when the
            // lid actually reaches an endpoint, not when a timer expires.
            float target = _kind == TransitionKind.Close ? 1f : 0f;

            if (!RenderCurrentFrame())
            {
                Fire(TransitionTrigger.Abort);
                return;
            }

            if (Math.Abs(_panelProgress - target) <= 0.002f)
            {
                CompleteAnimation(jumpToEnd: false);
                return;
            }

            // Another frame is requested by the next sensor reading. Keeping a
            // frame queued as well means a stationary lid still repaints, which is
            // what lets the blur decay to zero once movement stops.
            QueueFrame();
            return;
        }

        if (_clock.Elapsed.TotalMilliseconds >= EffectiveDurationMs())
        {
            CompleteAnimation(jumpToEnd: true);
            return;
        }

        if (!RenderCurrentFrame())
        {
            Fire(TransitionTrigger.Abort);
            return;
        }

        QueueFrame();
    }

    private double EffectiveDurationMs()
    {
        if (_model is null)
        {
            return 0d;
        }

        // Reversing part-way through should not take the full duration - only the
        // remaining part of it - otherwise a small correction would crawl.
        return _model.DurationMs * (1.0 - _startLinearT);
    }

    private bool RenderCurrentFrame()
    {
        if (_model is null)
        {
            return false;
        }

        if (AngleSource != LidAngleSourceKind.None)
        {
            return RenderFromLidPosition();
        }

        double duration = Math.Max(EffectiveDurationMs(), 1d);
        float local = (float)Math.Clamp(_clock.Elapsed.TotalMilliseconds / duration, 0d, 1d);

        // Animation time is wall-clock, not frame-counted, so a dropped frame
        // shortens the animation rather than slowing it down.
        float linearT = _startLinearT + ((1f - _startLinearT) * local);

        LidFrameParameters frame = _model.Evaluate(linearT);
        _panelProgress = frame.Progress;

        return RenderFrame(frame);
    }

    /// <summary>
    /// Renders the frame implied by the lid's actual position.
    /// <para>
    /// The rotation comes straight from the hardware - a hinge-angle sensor where
    /// there is one, otherwise a calibrated lid inclinometer - and the blur from
    /// the measured rate of change, so the effect tracks the user's hand instead
    /// of guessing. Everything else is produced by the same animation model the
    /// timed path uses, so the modes cannot look like different effects.
    /// </para>
    /// </summary>
    private bool RenderFromLidPosition()
    {
        if (_model is null)
        {
            return false;
        }

        float progress;

        if (IsHingeTracking)
        {
            HingeAngleSample? sample = _hinge.Current;
            if (sample is null)
            {
                return false;
            }

            progress = LidFlow.Core.Lid.HingeAngleMapping.ProgressFromAngle(
                sample.Value.AngleDegrees,
                _config.Animation.HingeClosedAngleDeg,
                _config.Animation.HingeOpenAngleDeg);
        }
        else if (!_angleTracker.TryGetProgress(out progress, out _))
        {
            return false;
        }

        long now = Stopwatch.GetTimestamp();

        if (!float.IsNaN(_lastHingeProgress) && _lastHingeStamp != 0)
        {
            double seconds = (now - _lastHingeStamp) / (double)Stopwatch.Frequency;

            if (seconds > 0.001d)
            {
                double instantaneous = (progress - _lastHingeProgress) / seconds;

                // One-pole smoothing. Raw frame-to-frame differences of a sensor
                // reading are noisy enough to make the blur flicker; this keeps the
                // response quick while removing the jitter.
                const double Alpha = 0.35d;
                _hingeProgressRate = (Alpha * instantaneous) + ((1d - Alpha) * _hingeProgressRate);
            }
        }

        _lastHingeProgress = progress;
        _lastHingeStamp = now;

        float velocity = LidFlow.Core.Lid.HingeAngleMapping.NormalizeVelocity(
            _hingeProgressRate,
            _config.Animation.HingeVelocityReference);

        _panelProgress = progress;

        LidFrameParameters frame = _model.EvaluateAtProgress(progress, velocity);
        return RenderFrame(frame);
    }

    private bool RenderFrame(in LidFrameParameters frame)
    {
        if (_overlay is null || _renderer is null || _snapshot is null || _activeDisplay is null)
        {
            return false;
        }

        long start = Stopwatch.GetTimestamp();

        bool ok = _renderer.RenderFrame(
            _overlay,
            _snapshot,
            frame,
            _activeDisplay.Rotation,
            _config.Animation.EnableDither,
            _cursor);

        long now = Stopwatch.GetTimestamp();
        _lastFrameMs = (now - start) * 1000.0 / Stopwatch.Frequency;

        double sinceLast = (now - _lastFrameStamp) * 1000.0 / Stopwatch.Frequency;
        if (sinceLast > 0.01)
        {
            _lastFps = 1000.0 / sinceLast;
        }

        _lastFrameStamp = now;
        _framesThisTransition++;

        if (!ok && _graphics is not null && _graphics.IsDeviceLost())
        {
            _graphicsReady = false;
        }

        return ok;
    }

    private void CompleteAnimation(bool jumpToEnd)
    {
        if (_model is not null && jumpToEnd)
        {
            LidFrameParameters last = _model.Evaluate(1f);
            _panelProgress = last.Progress;
            RenderFrame(last);
        }

        _clock.Stop();
        _log.Debug($"{_kind} animation finished in {_framesThisTransition} frames.");
        Fire(TransitionTrigger.AnimationCompleted);
    }

    // ------------------------------------------------------------------ helpers

    private static unsafe bool IsFullscreenAppActive()
    {
        int state;
        if (NativeMethods.SHQueryUserNotificationState(&state) != 0)
        {
            return false;
        }

        return state is NativeMethods.QUNS_RUNNING_D3D_FULL_SCREEN
            or NativeMethods.QUNS_PRESENTATION_MODE
            or NativeMethods.QUNS_BUSY;
    }

    private void ReleaseGraphics()
    {
        _graphicsReady = false;

        _capture?.Dispose();
        _capture = null;

        _cursor?.Dispose();
        _cursor = null;

        _snapshot?.Dispose();
        _snapshot = null;

        _renderer?.Dispose();
        _renderer = null;

        _overlay?.Dispose();
        _overlay = null;

        _graphics?.Dispose();
        _graphics = null;
    }

    /// <summary>
    /// The inclinometer calibration learned this session, for persisting. Null when
    /// there is nothing new worth saving.
    /// </summary>
    public LidAngleCalibration? LearnedCalibration =>
        _angleTracker.IsCalibrated ? _angleTracker.Snapshot() : null;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inclinometer.Dispose();
        _hinge.Dispose();
        ReleaseGraphics();
    }
}

/// <summary>Values the debug HUD displays. Never shown in a production build.</summary>
internal readonly struct DiagnosticsSnapshot
{
    public TransitionState State { get; init; }

    public float PanelProgress { get; init; }

    public double Fps { get; init; }

    public double LastFrameMs { get; init; }

    public string CaptureBackend { get; init; }

    public double CaptureLatencyMs { get; init; }

    public DisplayInfo? Display { get; init; }

    public MonitorSelectionReason SelectionReason { get; init; }

    public string Adapter { get; init; }

    public bool SoftwareRenderer { get; init; }

    public bool CaptureExcluded { get; init; }

    public DisplayPowerState DisplayPower { get; init; }

    /// <summary>Which continuous source, if any, is driving the transition.</summary>
    public LidAngleSourceKind AngleSource { get; init; }

    /// <summary>Current hinge angle, when a hinge-angle sensor is present.</summary>
    public double? HingeAngleDegrees { get; init; }

    /// <summary>Current lid pitch, when an inclinometer or accelerometer is present.</summary>
    public double? LidPitchDegrees { get; init; }

    /// <summary>Which pitch sensor was found.</summary>
    public string PitchSensor { get; init; }

    /// <summary>Learned pitch sweep, once calibrated.</summary>
    public double? CalibrationSweepDegrees { get; init; }
}
