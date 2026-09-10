namespace LidFlow.Core.Lid;

/// <summary>
/// Lid monitor driven programmatically instead of by hardware. Used by the preview hotkeys,
/// the <c>--preview-close</c> / <c>--preview-open</c> command line switches, and the tests.
/// <para>
/// It intentionally goes through the same <see cref="LidMonitorBase"/> plumbing as the real
/// monitor so a preview exercises the identical capture, state-machine and render path — the
/// only difference is where the event came from.
/// </para>
/// </summary>
public sealed class MockLidMonitor : LidMonitorBase
{
    public MockLidMonitor(LidState initialState = LidState.Open)
    {
        InitialState = initialState;
    }

    public LidState InitialState { get; }

    public void SetState(LidState state) => ReportState(state);

    public void Close() => ReportState(LidState.Closed);

    public void Open() => ReportState(LidState.Open);

    protected override void OnStart() => ReportState(InitialState);

    protected override void OnStop()
    {
        // Nothing to release: this monitor has no platform resources.
    }
}
