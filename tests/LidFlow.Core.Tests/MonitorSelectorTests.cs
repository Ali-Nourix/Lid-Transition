using System;
using System.Collections.Generic;
using LidFlow.Core.Monitors;
using Xunit;

namespace LidFlow.Core.Tests;

public sealed class MonitorSelectorTests
{
    private static DisplayInfo Display(
        string id,
        bool primary = false,
        InternalPanelConfidence confidence = InternalPanelConfidence.No,
        int width = 1920,
        int height = 1080,
        int x = 0,
        int y = 0) => new()
        {
            Id = id,
            FriendlyName = id,
            DevicePath = @"\\?\DISPLAY#" + id,
            Bounds = new DisplayBounds(x, y, width, height),
            IsPrimary = primary,
            InternalConfidence = confidence,
        };

    [Fact]
    public void InternalPanelIsPickedEvenWhenAnExternalDisplayIsPrimary()
    {
        List<DisplayInfo> displays = new()
        {
            Display("EXTERNAL", primary: true, confidence: InternalPanelConfidence.No, width: 3840, height: 2160),
            Display("PANEL", confidence: InternalPanelConfidence.Certain),
        };

        MonitorSelectionResult result = MonitorSelector.Select(displays, MonitorSelectionMode.InternalPanel);

        Assert.Equal(MonitorSelectionReason.Exact, result.Reason);
        Assert.Single(result.Targets);
        Assert.Equal("PANEL", result.Targets[0].Id);
    }

    [Fact]
    public void MostConfidentInternalPanelWins()
    {
        List<DisplayInfo> displays = new()
        {
            Display("MAYBE", confidence: InternalPanelConfidence.Likely),
            Display("DEFINITELY", confidence: InternalPanelConfidence.Certain),
        };

        MonitorSelectionResult result = MonitorSelector.Select(displays, MonitorSelectionMode.InternalPanel);

        Assert.Equal("DEFINITELY", result.Targets[0].Id);
    }

    [Fact]
    public void SingleUnidentifiedDisplayIsUsedButFlaggedAsAGuess()
    {
        List<DisplayInfo> displays = new() { Display("ONLY", primary: true, confidence: InternalPanelConfidence.Unknown) };

        MonitorSelectionResult result = MonitorSelector.Select(displays, MonitorSelectionMode.InternalPanel);

        Assert.Single(result.Targets);
        Assert.Equal(MonitorSelectionReason.InternalPanelNotIdentified, result.Reason);
    }

    [Fact]
    public void MultipleUnidentifiedDisplaysFallBackToPrimaryAndSaySo()
    {
        List<DisplayInfo> displays = new()
        {
            Display("A", confidence: InternalPanelConfidence.Unknown),
            Display("B", primary: true, confidence: InternalPanelConfidence.Unknown),
        };

        MonitorSelectionResult result = MonitorSelector.Select(displays, MonitorSelectionMode.InternalPanel);

        Assert.Single(result.Targets);
        Assert.Equal("B", result.Targets[0].Id);
        Assert.Equal(MonitorSelectionReason.InternalPanelNotIdentified, result.Reason);
    }

    [Fact]
    public void AllDisplaysRequiresTheExternalOptIn()
    {
        List<DisplayInfo> displays = new()
        {
            Display("PANEL", confidence: InternalPanelConfidence.Certain),
            Display("EXTERNAL", primary: true),
        };

        MonitorSelectionResult denied = MonitorSelector.Select(
            displays,
            MonitorSelectionMode.AllDisplays,
            allowExternalDisplays: false);

        Assert.Single(denied.Targets);
        Assert.Equal("PANEL", denied.Targets[0].Id);
        Assert.Equal(MonitorSelectionReason.ExternalDisplaysNotAllowed, denied.Reason);

        MonitorSelectionResult allowed = MonitorSelector.Select(
            displays,
            MonitorSelectionMode.AllDisplays,
            allowExternalDisplays: true);

        Assert.Equal(2, allowed.Targets.Count);
        Assert.Equal(MonitorSelectionReason.Exact, allowed.Reason);
    }

    [Fact]
    public void PrimaryModeFallsBackToTheInternalPanelWhenExternalsAreNotAllowed()
    {
        List<DisplayInfo> displays = new()
        {
            Display("PANEL", confidence: InternalPanelConfidence.Certain),
            Display("EXTERNAL", primary: true),
        };

        MonitorSelectionResult result = MonitorSelector.Select(
            displays,
            MonitorSelectionMode.PrimaryDisplay,
            allowExternalDisplays: false);

        Assert.Equal("PANEL", result.Targets[0].Id);
        Assert.Equal(MonitorSelectionReason.ExternalDisplaysNotAllowed, result.Reason);
    }

    [Fact]
    public void PrimaryModeUsesThePrimaryDisplayWhenItIsTheInternalPanel()
    {
        List<DisplayInfo> displays = new()
        {
            Display("PANEL", primary: true, confidence: InternalPanelConfidence.Certain),
            Display("EXTERNAL"),
        };

        MonitorSelectionResult result = MonitorSelector.Select(displays, MonitorSelectionMode.PrimaryDisplay);

        Assert.Equal("PANEL", result.Targets[0].Id);
        Assert.Equal(MonitorSelectionReason.Exact, result.Reason);
    }

    [Fact]
    public void ManualSelectionMatchesByDevicePathOrDeviceName()
    {
        List<DisplayInfo> displays = new()
        {
            Display("PANEL", confidence: InternalPanelConfidence.Certain),
            Display("EXTERNAL", primary: true),
        };

        MonitorSelectionResult byName = MonitorSelector.Select(
            displays,
            MonitorSelectionMode.Manual,
            "EXTERNAL",
            allowExternalDisplays: true);
        Assert.Equal("EXTERNAL", byName.Targets[0].Id);

        MonitorSelectionResult byPath = MonitorSelector.Select(
            displays,
            MonitorSelectionMode.Manual,
            @"\\?\DISPLAY#EXTERNAL",
            allowExternalDisplays: true);
        Assert.Equal("EXTERNAL", byPath.Targets[0].Id);
    }

    [Fact]
    public void MissingManualTargetFallsBackAndReportsWhy()
    {
        List<DisplayInfo> displays = new() { Display("PANEL", confidence: InternalPanelConfidence.Certain) };

        MonitorSelectionResult result = MonitorSelector.Select(
            displays,
            MonitorSelectionMode.Manual,
            "A-MONITOR-THAT-WAS-UNPLUGGED");

        Assert.Equal("PANEL", result.Targets[0].Id);
        Assert.Equal(MonitorSelectionReason.ManualTargetMissing, result.Reason);
    }

    [Fact]
    public void NoDisplaysMeansNoTargetRatherThanAnException()
    {
        MonitorSelectionResult empty = MonitorSelector.Select(Array.Empty<DisplayInfo>(), MonitorSelectionMode.InternalPanel);

        Assert.False(empty.HasTarget);
        Assert.Equal(MonitorSelectionReason.NoTarget, empty.Reason);
        Assert.Null(empty.Primary);
    }

    [Fact]
    public void EmptyBoundsDisplaysAreNeverSelected()
    {
        // A display that is present but inactive (internal panel disabled while docked)
        // has empty bounds and must not be treated as a target.
        List<DisplayInfo> displays = new()
        {
            Display("PANEL", confidence: InternalPanelConfidence.Certain, width: 0, height: 0),
            Display("EXTERNAL", primary: true),
        };

        MonitorSelectionResult result = MonitorSelector.Select(displays, MonitorSelectionMode.InternalPanel);

        Assert.Equal("EXTERNAL", result.Targets[0].Id);
        Assert.Equal(MonitorSelectionReason.InternalPanelNotIdentified, result.Reason);
    }

    [Fact]
    public void MirroredDisplaysWithIdenticalBoundsStillResolveToTheInternalPanel()
    {
        List<DisplayInfo> displays = new()
        {
            Display("PANEL", primary: true, confidence: InternalPanelConfidence.Certain),
            Display("CLONE"),
        };

        MonitorSelectionResult result = MonitorSelector.Select(displays, MonitorSelectionMode.InternalPanel);

        Assert.Single(result.Targets);
        Assert.Equal("PANEL", result.Targets[0].Id);
    }
}
