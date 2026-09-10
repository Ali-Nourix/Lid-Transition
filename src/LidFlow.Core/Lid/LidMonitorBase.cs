using System;

namespace LidFlow.Core.Lid;

/// <summary>
/// Shared bookkeeping for lid monitors: holds the last known state and raises the change
/// event only on an actual change.
/// <para>
/// Deduplication matters. Windows can deliver a lid notification that repeats the current
/// state (for example the initial notification once a lid device is discovered), and a
/// duplicate "closed" arriving mid-animation would otherwise restart the transition.
/// </para>
/// </summary>
public abstract class LidMonitorBase : ILidMonitor
{
    private readonly object _gate = new();
    private LidState _state = LidState.Unknown;
    private bool _started;
    private bool _disposed;

    public LidState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public event EventHandler<LidStateChangedEventArgs>? LidStateChanged;

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
        }

        OnStart();
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }

            _started = false;
        }

        OnStop();
    }

    /// <summary>Called by derived monitors when the platform reports a state.</summary>
    protected void ReportState(LidState state)
    {
        LidState previous;

        lock (_gate)
        {
            if (_state == state)
            {
                return;
            }

            previous = _state;
            _state = state;
        }

        LidStateChanged?.Invoke(this, new LidStateChangedEventArgs(previous, state));
    }

    protected abstract void OnStart();

    protected abstract void OnStop();

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            Stop();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
