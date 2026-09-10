using LidFlow.Core.Lid;
using LidFlow.Core.Power;
using Xunit;

namespace LidFlow.Core.Tests;

public sealed class LidStateParserTests
{
    [Theory]
    [InlineData(0u, LidState.Closed)]
    [InlineData(1u, LidState.Open)]
    [InlineData(2u, LidState.Unknown)]
    [InlineData(uint.MaxValue, LidState.Unknown)]
    public void ParsesDocumentedValuesAndRejectsTheRest(uint value, LidState expected) =>
        Assert.Equal(expected, LidStateParser.Parse(value));

    [Fact]
    public void ParsesLittleEndianDwordPayload()
    {
        Assert.Equal(LidState.Closed, LidStateParser.Parse(new byte[] { 0, 0, 0, 0 }));
        Assert.Equal(LidState.Open, LidStateParser.Parse(new byte[] { 1, 0, 0, 0 }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(8)]
    public void RejectsPayloadsOfTheWrongLength(int length) =>
        Assert.Equal(LidState.Unknown, LidStateParser.Parse(new byte[length]));

    [Theory]
    [InlineData(0u, DisplayPowerState.Off)]
    [InlineData(1u, DisplayPowerState.On)]
    [InlineData(2u, DisplayPowerState.Dimmed)]
    [InlineData(9u, DisplayPowerState.Unknown)]
    public void ParsesDisplayPowerState(uint value, DisplayPowerState expected) =>
        Assert.Equal(expected, DisplayPowerStateParser.Parse(value));
}

public sealed class MockLidMonitorTests
{
    [Fact]
    public void StartReportsTheInitialState()
    {
        using MockLidMonitor monitor = new(LidState.Open);
        int events = 0;
        monitor.LidStateChanged += (_, _) => events++;

        monitor.Start();

        Assert.Equal(LidState.Open, monitor.State);
        Assert.Equal(1, events);
    }

    [Fact]
    public void RepeatedIdenticalStatesAreSuppressed()
    {
        // A duplicate "closed" arriving mid-animation would otherwise restart the
        // transition, so deduplication is load-bearing rather than cosmetic.
        using MockLidMonitor monitor = new(LidState.Open);
        monitor.Start();

        int events = 0;
        monitor.LidStateChanged += (_, _) => events++;

        monitor.Close();
        monitor.Close();
        monitor.Close();

        Assert.Equal(1, events);
        Assert.Equal(LidState.Closed, monitor.State);
    }

    [Fact]
    public void TransitionsReportPreviousAndCurrentState()
    {
        using MockLidMonitor monitor = new(LidState.Open);
        monitor.Start();

        LidStateChangedEventArgs? captured = null;
        monitor.LidStateChanged += (_, e) => captured = e;

        monitor.Close();

        Assert.NotNull(captured);
        Assert.Equal(LidState.Open, captured!.Previous);
        Assert.Equal(LidState.Closed, captured.Current);
    }

    [Fact]
    public void StartAndStopAreIdempotent()
    {
        using MockLidMonitor monitor = new(LidState.Closed);

        monitor.Start();
        monitor.Start();
        monitor.Stop();
        monitor.Stop();

        Assert.Equal(LidState.Closed, monitor.State);
    }
}
