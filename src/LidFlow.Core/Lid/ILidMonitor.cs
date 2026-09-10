using System;

namespace LidFlow.Core.Lid;

/// <summary>Arguments for <see cref="ILidMonitor.LidStateChanged"/>.</summary>
public sealed class LidStateChangedEventArgs : EventArgs
{
    public LidStateChangedEventArgs(LidState previous, LidState current)
    {
        Previous = previous;
        Current = current;
    }

    public LidState Previous { get; }

    public LidState Current { get; }
}

/// <summary>
/// Source of lid open/close events.
/// <para>
/// Abstracted for two reasons: the real implementation needs a message pump and a physical
/// lid, and the visual effect has to be tunable without closing the laptop after every
/// shader edit. <c>MockLidMonitor</c> drives the identical pipeline from a hotkey or a
/// command-line switch.
/// </para>
/// </summary>
public interface ILidMonitor : IDisposable
{
    /// <summary>Last known lid state. <see cref="LidState.Unknown"/> until the platform reports one.</summary>
    LidState State { get; }

    /// <summary>Raised when the lid state actually changes. Repeats of the same state are suppressed.</summary>
    event EventHandler<LidStateChangedEventArgs>? LidStateChanged;

    /// <summary>Begins listening. Idempotent.</summary>
    void Start();

    /// <summary>Stops listening. Idempotent.</summary>
    void Stop();
}
