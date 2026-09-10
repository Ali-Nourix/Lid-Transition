using System;
using System.Diagnostics;
using LidFlow.Core.Diagnostics;
using Windows.Devices.Sensors;

namespace LidFlow.App.Lid;

/// <summary>
/// Reads lid pitch from the machine's inclinometer, or from its accelerometer if
/// there is no inclinometer.
/// <para>
/// This is the practical answer to the lid switch being binary. On any laptop
/// with auto-rotate or a tablet mode the sensor is mounted in the display, so its
/// pitch tracks the lid one-to-one - and unlike a hinge-angle sensor, that is
/// common hardware rather than rare. It reports a relative angle, which
/// <see cref="LidFlow.Core.Lid.LidAngleTracker"/> calibrates against the lid
/// switch's own two events.
/// </para>
/// <para>
/// An inclinometer is preferred over the raw accelerometer because Windows has
/// already fused and filtered it into a stable pitch; the accelerometer path
/// derives pitch from gravity directly and is only used when no inclinometer is
/// exposed.
/// </para>
/// </summary>
internal sealed class InclinometerMonitor : IDisposable
{
    /// <summary>~60 Hz. Fast enough to track a lid without flooding the pump.</summary>
    private const uint TargetReportIntervalMs = 16;

    private readonly ILidFlowLog _log;

    private Inclinometer? _inclinometer;
    private Accelerometer? _accelerometer;
    private bool _disposed;

    public InclinometerMonitor(ILidFlowLog log)
    {
        _log = log ?? NullLog.Instance;
    }

    /// <summary>True when some source of lid pitch was found.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>Which sensor is supplying pitch, for logging and the debug overlay.</summary>
    public string SourceName { get; private set; } = "none";

    /// <summary>Raised on every reading, on a sensor thread. Pitch in degrees, timestamp in seconds.</summary>
    public event EventHandler<(double PitchDegrees, double TimestampSeconds)>? PitchChanged;

    public bool TryStart()
    {
        if (IsAvailable)
        {
            return true;
        }

        try
        {
            _inclinometer = Inclinometer.GetDefault();

            if (_inclinometer is not null)
            {
                _inclinometer.ReportInterval = Math.Max(_inclinometer.MinimumReportInterval, TargetReportIntervalMs);
                _inclinometer.ReadingChanged += OnInclinometerReading;

                IsAvailable = true;
                SourceName = "inclinometer";
                _log.Info($"Inclinometer found (report interval {_inclinometer.ReportInterval} ms); lid pitch is readable.");
                return true;
            }

            _accelerometer = Accelerometer.GetDefault();

            if (_accelerometer is not null)
            {
                _accelerometer.ReportInterval = Math.Max(_accelerometer.MinimumReportInterval, TargetReportIntervalMs);
                _accelerometer.ReadingChanged += OnAccelerometerReading;

                IsAvailable = true;
                SourceName = "accelerometer";
                _log.Info("No inclinometer; deriving lid pitch from the accelerometer.");
                return true;
            }

            _log.Info("No inclinometer or accelerometer; the transition will use the timed path.");
            return false;
        }
        catch (Exception ex)
        {
            // Sensor stacks are a common source of driver-specific failures, and
            // none of them should stop the app: the timed path is always there.
            _log.Info($"Lid pitch sensor unavailable ({ex.GetType().Name}: {ex.Message}); using the timed path.");
            Stop();
            return false;
        }
    }

    private void OnInclinometerReading(Inclinometer sender, InclinometerReadingChangedEventArgs args) =>
        PitchChanged?.Invoke(this, (args.Reading.PitchDegrees, Now()));

    private void OnAccelerometerReading(Accelerometer sender, AccelerometerReadingChangedEventArgs args)
    {
        AccelerometerReading reading = args.Reading;

        // Pitch about the device's X axis from the gravity vector. atan2 rather
        // than asin so it stays well-conditioned through the full rotation.
        double pitch = Math.Atan2(reading.AccelerationZ, reading.AccelerationY) * 180d / Math.PI;

        PitchChanged?.Invoke(this, (pitch, Now()));
    }

    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private void Stop()
    {
        if (_inclinometer is not null)
        {
            _inclinometer.ReadingChanged -= OnInclinometerReading;
            _inclinometer = null;
        }

        if (_accelerometer is not null)
        {
            _accelerometer.ReadingChanged -= OnAccelerometerReading;
            _accelerometer = null;
        }

        IsAvailable = false;
        SourceName = "none";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
