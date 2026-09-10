using System;
using System.Diagnostics;
using LidFlow.Core.Diagnostics;
using LidFlow.Core.Lid;
using Windows.Devices.Sensors;

namespace LidFlow.App.Lid;

/// <summary>A hinge angle reading and the rate it is changing at.</summary>
internal readonly struct HingeAngleSample
{
    public HingeAngleSample(double angleDegrees, double degreesPerSecond)
    {
        AngleDegrees = angleDegrees;
        DegreesPerSecond = degreesPerSecond;
    }

    public double AngleDegrees { get; }

    public double DegreesPerSecond { get; }
}

/// <summary>
/// Reads the physical hinge angle from <c>Windows.Devices.Sensors.HingeAngleSensor</c>.
/// <para>
/// When this is available the effect stops being an animation and becomes a
/// direct read-out of the hardware: the aperture is wherever the lid is, so
/// pausing half way, moving slowly, or reversing all behave correctly without any
/// special handling. That is how the macOS app this effect is modelled on works,
/// using the lid-angle sensor Apple silicon MacBooks expose.
/// </para>
/// <para>
/// On Windows the sensor exists mainly on dual-screen and foldable hardware, not
/// on ordinary clamshell laptops, so this is treated strictly as an enhancement:
/// <see cref="IsAvailable"/> is false on most machines and the app uses the timed
/// path instead. It is never required, and its absence is not an error.
/// </para>
/// </summary>
internal sealed class HingeAngleMonitor : IDisposable
{
    private readonly ILidFlowLog _log;
    private readonly object _gate = new();

    private HingeAngleSensor? _sensor;
    private double _lastAngle = double.NaN;
    private long _lastStamp;
    private double _degreesPerSecond;
    private bool _disposed;

    public HingeAngleMonitor(ILidFlowLog log)
    {
        _log = log ?? NullLog.Instance;
    }

    /// <summary>True when this machine actually has a hinge-angle sensor.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>Most recent reading, or null if none has arrived.</summary>
    public HingeAngleSample? Current
    {
        get
        {
            lock (_gate)
            {
                return double.IsNaN(_lastAngle)
                    ? null
                    : new HingeAngleSample(_lastAngle, _degreesPerSecond);
            }
        }
    }

    /// <summary>Raised on every reading, on a sensor thread.</summary>
    public event EventHandler<HingeAngleSample>? AngleChanged;

    /// <summary>
    /// Looks for the sensor and starts listening. Returns false when the hardware
    /// has none, which is the common case and not a failure.
    /// </summary>
    public bool TryStart()
    {
        if (IsAvailable)
        {
            return true;
        }

        try
        {
            // Blocking on the async factory is acceptable here: it runs once at
            // start-up, before any transition can occur, and the alternative would
            // be an async start-up path for a capability most machines lack.
            _sensor = HingeAngleSensor.GetDefaultAsync().AsTask().GetAwaiter().GetResult();

            if (_sensor is null)
            {
                _log.Info("No hinge-angle sensor on this machine; using the timed animation path.");
                return false;
            }

            // Ask for the finest reporting the hardware supports: the whole point
            // is to track the lid closely, and a coarse threshold would quantize
            // the aperture into visible steps.
            _sensor.ReportThresholdInDegrees = _sensor.MinReportThresholdInDegrees;

            _sensor.ReadingChanged += OnReadingChanged;

            HingeAngleReading initial = _sensor.GetCurrentReadingAsync().AsTask().GetAwaiter().GetResult();
            Record(initial.AngleInDegrees);

            IsAvailable = true;
            _log.Info(
                $"Hinge-angle sensor found (min report threshold {_sensor.MinReportThresholdInDegrees:0.##} deg); " +
                $"tracking the lid directly. Current angle {initial.AngleInDegrees:0.#} deg.");

            return true;
        }
        catch (Exception ex)
        {
            // Sensor stacks are a common source of driver-specific failures, and
            // none of them should stop the app: the timed path is always there.
            _log.Info($"Hinge-angle sensor unavailable ({ex.GetType().Name}: {ex.Message}); using the timed path.");
            _sensor = null;
            IsAvailable = false;
            return false;
        }
    }

    private void OnReadingChanged(HingeAngleSensor sender, HingeAngleSensorReadingChangedEventArgs args)
    {
        double angle = args.Reading.AngleInDegrees;
        Record(angle);
        AngleChanged?.Invoke(this, new HingeAngleSample(angle, _degreesPerSecond));
    }

    private void Record(double angle)
    {
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();

            if (!double.IsNaN(_lastAngle) && _lastStamp != 0)
            {
                double seconds = (now - _lastStamp) / (double)Stopwatch.Frequency;

                // Below a millisecond the difference is noise amplified by a tiny
                // denominator, so the previous rate is kept instead.
                if (seconds > 0.001)
                {
                    _degreesPerSecond = (angle - _lastAngle) / seconds;
                }
            }

            _lastAngle = angle;
            _lastStamp = now;
        }
    }

    /// <summary>
    /// Panel progress implied by the current angle, or null when there is no
    /// reading to use.
    /// </summary>
    public float? ProgressFor(float closedAngleDeg, float openAngleDeg)
    {
        HingeAngleSample? sample = Current;
        return sample is null
            ? null
            : HingeAngleMapping.ProgressFromAngle(sample.Value.AngleDegrees, closedAngleDeg, openAngleDeg);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_sensor is not null)
        {
            _sensor.ReadingChanged -= OnReadingChanged;
            _sensor = null;
        }

        IsAvailable = false;
    }
}
