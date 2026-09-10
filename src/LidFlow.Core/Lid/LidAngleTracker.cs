using System;

namespace LidFlow.Core.Lid;

/// <summary>Where a continuous lid position is coming from.</summary>
public enum LidAngleSourceKind
{
    /// <summary>No continuous source; the transition is a timed animation.</summary>
    None = 0,

    /// <summary>A real hinge-angle sensor. Absolute and exact.</summary>
    HingeAngleSensor = 1,

    /// <summary>
    /// An inclinometer or accelerometer mounted in the lid. Relative, and needs
    /// calibrating against the lid switch, but present on far more machines.
    /// </summary>
    LidInclinometer = 2,
}

/// <summary>
/// Learned mapping between lid-mounted inclinometer pitch and lid position.
/// <para>
/// Persisted so the mapping survives a restart: it is learned from real lid
/// events, so throwing it away would mean the first close after every launch
/// falls back to the timed path.
/// </para>
/// </summary>
public sealed class LidAngleCalibration
{
    /// <summary>Pitch observed while the lid was open and stationary.</summary>
    public double OpenPitchDegrees { get; set; }

    /// <summary>Pitch observed at the moment the lid switch reported closed.</summary>
    public double ClosedPitchDegrees { get; set; }

    /// <summary>True once both ends have been observed and the sweep is plausible.</summary>
    public bool IsValid { get; set; }

    public LidAngleCalibration Clone() => new()
    {
        OpenPitchDegrees = OpenPitchDegrees,
        ClosedPitchDegrees = ClosedPitchDegrees,
        IsValid = IsValid,
    };
}

/// <summary>
/// Turns a stream of lid-mounted inclinometer readings into continuous lid
/// position, self-calibrating from the binary lid switch.
/// <para>
/// <b>Why this exists.</b> <c>GUID_LIDSWITCH_STATE_CHANGE</c> is strictly binary:
/// it reports 0 or 1 and there is no value in between, so on its own it can only
/// say <i>that</i> the lid moved, never <i>how far</i>. A real hinge-angle sensor
/// answers that directly but exists on very little hardware. An inclinometer,
/// however, is present on any machine with auto-rotate or a tablet mode, and on
/// those the sensor is in the display - so as the lid moves, its pitch moves with
/// it.
/// </para>
/// <para>
/// <b>How it calibrates.</b> Exactly from the two events the switch does give.
/// When the switch says closed, the current pitch is one end of the range; while
/// the lid sits open and still, the current pitch is the other. Two observations
/// and the whole range in between becomes readable. The sign falls out of the
/// subtraction, so it does not matter which way round the sensor is mounted.
/// </para>
/// <para>
/// <b>What it deliberately does not do.</b> An inclinometer cannot tell a moving
/// lid from a moving laptop - picking the machine up changes pitch just as
/// closing the lid does. So by default this only supplies <i>position</i> for a
/// transition the lid switch has already started; it never starts one itself.
/// That keeps a false positive impossible while still filling in everything
/// between the two events, which is the part that was missing.
/// </para>
/// </summary>
public sealed class LidAngleTracker
{
    /// <summary>Below this, the observed range is too small to be a real lid sweep.</summary>
    public const double MinimumPlausibleSweepDegrees = 20d;

    /// <summary>Above this, the two observations were not of the same axis.</summary>
    public const double MaximumPlausibleSweepDegrees = 175d;

    private const double StationaryThresholdDegreesPerSecond = 1.5d;
    private const double SmoothingAlpha = 0.35d;

    private readonly double _minimumSweep;

    private double _openPitch;
    private double _closedPitch;
    private bool _calibrated;

    private double _lastPitch = double.NaN;
    private double _lastTimestamp;
    private double _pitchRate;
    private bool _hasSample;
    private bool _openIsObservable;

    public LidAngleTracker(LidAngleCalibration? calibration = null, double minimumSweepDegrees = MinimumPlausibleSweepDegrees)
    {
        _minimumSweep = minimumSweepDegrees <= 0d ? MinimumPlausibleSweepDegrees : minimumSweepDegrees;

        if (calibration is { IsValid: true })
        {
            _openPitch = calibration.OpenPitchDegrees;
            _closedPitch = calibration.ClosedPitchDegrees;
            _calibrated = IsSweepPlausible(_closedPitch - _openPitch);
        }
    }

    /// <summary>True once the pitch range has been learned and is usable.</summary>
    public bool IsCalibrated => _calibrated;

    /// <summary>Signed pitch travel from fully open to fully closed.</summary>
    public double SweepDegrees => _closedPitch - _openPitch;

    /// <summary>Most recent pitch reading, or NaN before the first sample.</summary>
    public double LastPitchDegrees => _lastPitch;

    /// <summary>Smoothed rate of change of pitch, in degrees per second.</summary>
    public double PitchRateDegreesPerSecond => _pitchRate;

    /// <summary>True while the lid appears to be moving rather than resting.</summary>
    public bool IsMoving => Math.Abs(_pitchRate) > StationaryThresholdDegreesPerSecond;

    /// <summary>The calibration to persist.</summary>
    public LidAngleCalibration Snapshot() => new()
    {
        OpenPitchDegrees = _openPitch,
        ClosedPitchDegrees = _closedPitch,
        IsValid = _calibrated,
    };

    /// <summary>
    /// Feeds a reading. <paramref name="timestampSeconds"/> only needs to be
    /// monotonic and in seconds; its origin is irrelevant.
    /// </summary>
    public void Update(double pitchDegrees, double timestampSeconds)
    {
        if (double.IsNaN(pitchDegrees) || double.IsInfinity(pitchDegrees))
        {
            return;
        }

        if (_hasSample)
        {
            double elapsed = timestampSeconds - _lastTimestamp;

            // Below a millisecond the difference is noise amplified by a tiny
            // denominator, so the previous rate is kept.
            if (elapsed > 0.001d)
            {
                double instantaneous = (pitchDegrees - _lastPitch) / elapsed;
                _pitchRate = (SmoothingAlpha * instantaneous) + ((1d - SmoothingAlpha) * _pitchRate);
            }
        }

        _lastPitch = pitchDegrees;
        _lastTimestamp = timestampSeconds;
        _hasSample = true;

        // While the lid is open and still, this is what "fully open" looks like.
        // Tracked continuously rather than once, so it follows the angle the user
        // actually works at instead of whatever it was the first time.
        if (_openIsObservable && !IsMoving)
        {
            _openPitch = pitchDegrees;
            _calibrated = IsSweepPlausible(SweepDegrees);
        }
    }

    /// <summary>
    /// Tells the tracker what the lid switch just reported, which is what drives
    /// calibration.
    /// </summary>
    public void ObserveLidState(LidState state)
    {
        switch (state)
        {
            case LidState.Closed:
                if (_hasSample)
                {
                    // The switch fires close to fully shut, so this is the closed end
                    // of the range to within a few degrees.
                    _closedPitch = _lastPitch;
                    _calibrated = IsSweepPlausible(SweepDegrees);
                }

                _openIsObservable = false;
                break;

            case LidState.Open:
                // Do not sample immediately: the lid is still moving. Update() will
                // take the reading once it settles.
                _openIsObservable = true;
                break;

            default:
                _openIsObservable = false;
                break;
        }
    }

    /// <summary>
    /// Current lid position, 0 = fully open, 1 = fully closed. Returns false when
    /// there is nothing trustworthy to report.
    /// </summary>
    public bool TryGetProgress(out float progress, out float progressPerSecond)
    {
        progress = 0f;
        progressPerSecond = 0f;

        if (!_calibrated || !_hasSample)
        {
            return false;
        }

        double sweep = SweepDegrees;
        if (Math.Abs(sweep) < 1e-6)
        {
            return false;
        }

        progress = (float)Math.Clamp((_lastPitch - _openPitch) / sweep, 0d, 1d);

        // Rate in progress units, which is what the blur wants. Dividing by the
        // signed sweep also makes it positive for closing and negative for
        // opening, regardless of sensor mounting.
        progressPerSecond = (float)(_pitchRate / sweep);

        return true;
    }

    /// <summary>
    /// Direction the lid is currently moving, or null when it is effectively
    /// still. Only meaningful once calibrated.
    /// </summary>
    public LidState? MotionDirection
    {
        get
        {
            if (!_calibrated || !IsMoving)
            {
                return null;
            }

            double sweep = SweepDegrees;
            if (Math.Abs(sweep) < 1e-6)
            {
                return null;
            }

            // Positive progress rate means heading toward closed.
            return (_pitchRate / sweep) > 0d ? LidState.Closed : LidState.Open;
        }
    }

    /// <summary>Discards the learned range, e.g. after a docking or orientation change.</summary>
    public void ResetCalibration()
    {
        _calibrated = false;
        _openIsObservable = false;
    }

    private bool IsSweepPlausible(double sweep)
    {
        double magnitude = Math.Abs(sweep);
        return magnitude >= _minimumSweep && magnitude <= MaximumPlausibleSweepDegrees;
    }
}
