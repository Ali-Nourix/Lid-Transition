using System;
using System.IO;
using LidFlow.Core.Animation;
using LidFlow.Core.Configuration;
using LidFlow.Core.Monitors;
using Xunit;

namespace LidFlow.Core.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void DefaultsMatchTheDocumentedDesignIntent()
    {
        LidFlowConfig config = new();
        config.Normalize();

        Assert.True(config.Animation.EnableAnimation);

        // The brief's suggested envelopes: close 250-420 ms, open 220-380 ms, open faster.
        Assert.InRange(config.Animation.CloseDurationMs, 250, 420);
        Assert.InRange(config.Animation.OpenDurationMs, 220, 380);
        Assert.True(config.Animation.OpenDurationMs < config.Animation.CloseDurationMs);

        // Safe monitor default: internal panel only, no external displays.
        Assert.Equal(MonitorSelectionMode.InternalPanel, config.Monitor.Mode);
        Assert.False(config.Monitor.AllowExternalDisplays);

        // Debug UI off by default; this is what ships.
        Assert.False(config.Diagnostics.ShowDebugHud);
        Assert.False(config.Diagnostics.VerboseFrameLogging);

        // No auto-start without the user asking.
        Assert.False(config.Behavior.StartWithWindows);
    }

    [Fact]
    public void NormalizeClampsHostileValues()
    {
        LidFlowConfig config = new()
        {
            Version = -4,
            Animation = new AnimationConfig
            {
                CloseDurationMs = -100,
                OpenDurationMs = 10_000_000,
                PanelMaxAngleDeg = 400f,
                PerspectiveStrength = -5f,
                ShadowStrength = -3f,
                MaxBlur = float.NaN,
                BlurFalloff = 0f,
                GlareWidth = 0f,
                Quality = (AnimationQuality)999,
            },
            Monitor = new MonitorConfig { Mode = (MonitorSelectionMode)77, ManualDisplayId = "   " },
            Behavior = new BehaviorConfig { CaptureTimeoutMs = -1, CaptureBackend = (CaptureBackend)55 },
            Diagnostics = new DiagnosticsConfig { PreviewHoldMs = -900 },
        };

        config.Normalize();

        Assert.Equal(1, config.Version);
        Assert.InRange(config.Animation.CloseDurationMs, 80, 2000);
        Assert.InRange(config.Animation.OpenDurationMs, 80, 2000);
        Assert.InRange(config.Animation.PanelMaxAngleDeg, 5f, 179f);
        Assert.InRange(config.Animation.PerspectiveStrength, 0f, 4f);
        Assert.InRange(config.Animation.ShadowStrength, 0f, 1f);
        Assert.False(float.IsNaN(config.Animation.MaxBlur));
        Assert.True(config.Animation.BlurFalloff >= 0.05f);
        Assert.True(config.Animation.GlareWidth >= 0.001f);
        Assert.Equal(AnimationQuality.Balanced, config.Animation.Quality);
        Assert.Equal(MonitorSelectionMode.InternalPanel, config.Monitor.Mode);
        Assert.Null(config.Monitor.ManualDisplayId);
        Assert.True(config.Behavior.CaptureTimeoutMs >= 5);
        Assert.Equal(CaptureBackend.Auto, config.Behavior.CaptureBackend);
        Assert.Equal(0, config.Diagnostics.PreviewHoldMs);
    }

    [Fact]
    public void NormalizeReplacesNullSections()
    {
        LidFlowConfig config = new()
        {
            Animation = null!,
            Monitor = null!,
            Behavior = null!,
            Diagnostics = null!,
        };

        config.Normalize();

        Assert.NotNull(config.Animation);
        Assert.NotNull(config.Monitor);
        Assert.NotNull(config.Behavior);
        Assert.NotNull(config.Diagnostics);
    }

    [Fact]
    public void RoundTripsThroughJson()
    {
        LidFlowConfig original = new();
        original.Animation.CloseDurationMs = 375;
        original.Animation.PerspectiveStrength = 1.2f;
        original.Animation.Quality = AnimationQuality.High;
        original.Animation.CloseEasing = new EasingSettings
        {
            Preset = EasingPreset.CustomBezier,
            Bezier = new[] { 0.1f, 0.9f, 0.2f, 1f },
        };
        original.Monitor.Mode = MonitorSelectionMode.Manual;
        original.Monitor.ManualDisplayId = @"\\.\DISPLAY2";
        original.Normalize();

        string json = ConfigStore.Serialize(original);
        LidFlowConfig? restored = ConfigStore.Deserialize(json);

        Assert.NotNull(restored);
        restored!.Normalize();

        Assert.Equal(375, restored.Animation.CloseDurationMs);
        Assert.Equal(1.2f, restored.Animation.PerspectiveStrength, 4);
        Assert.Equal(AnimationQuality.High, restored.Animation.Quality);
        Assert.Equal(EasingPreset.CustomBezier, restored.Animation.CloseEasing.Preset);
        Assert.Equal(MonitorSelectionMode.Manual, restored.Monitor.Mode);
        Assert.Equal(@"\\.\DISPLAY2", restored.Monitor.ManualDisplayId);
    }

    [Fact]
    public void EnumsSerializeAsReadableNames()
    {
        // A config file is meant to be hand-edited, so enums must not be bare integers.
        LidFlowConfig config = new();
        config.Animation.Quality = AnimationQuality.High;
        config.Normalize();

        string json = ConfigStore.Serialize(config);

        Assert.Contains("\"High\"", json, StringComparison.Ordinal);
        Assert.Contains("\"InternalPanel\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFileYieldsDefaultsWithoutThrowing()
    {
        ConfigLoadResult result = ConfigStore.Load(Path.Combine(Path.GetTempPath(), "lidflow-does-not-exist-" + Guid.NewGuid().ToString("N") + ".json"));

        Assert.Equal(ConfigLoadStatus.NotFound, result.Status);
        Assert.NotNull(result.Config);
        Assert.True(result.Config.Animation.EnableAnimation);
    }

    [Fact]
    public void MalformedFileFallsBackToDefaultsAndExplainsWhy()
    {
        string path = Path.Combine(Path.GetTempPath(), "lidflow-broken-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{ this is not json at all ");

        try
        {
            ConfigLoadResult result = ConfigStore.Load(path);

            Assert.Equal(ConfigLoadStatus.Invalid, result.Status);
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
            Assert.True(result.Config.Animation.EnableAnimation);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PartialConfigKeepsDefaultsForOmittedValues()
    {
        LidFlowConfig? parsed = ConfigStore.Deserialize("{ \"animation\": { \"closeDurationMs\": 300 } }");

        Assert.NotNull(parsed);
        parsed!.Normalize();

        Assert.Equal(300, parsed.Animation.CloseDurationMs);
        Assert.Equal(new AnimationConfig().OpenDurationMs, parsed.Animation.OpenDurationMs);
        Assert.Equal(MonitorSelectionMode.InternalPanel, parsed.Monitor.Mode);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreTolerated()
    {
        LidFlowConfig? parsed = ConfigStore.Deserialize(
            "{ /* tuning notes */ \"animation\": { \"maxBlur\": 40, }, }");

        Assert.NotNull(parsed);
        Assert.Equal(40f, parsed!.Animation.MaxBlur);
    }

    [Fact]
    public void SaveIsAtomicAndReloadable()
    {
        string path = Path.Combine(Path.GetTempPath(), "lidflow-save-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            LidFlowConfig config = new();
            config.Animation.MaxBlur = 44f;
            config.Normalize();

            Assert.True(ConfigStore.TrySave(path, config, out string? error), error);
            Assert.Null(error);
            Assert.False(File.Exists(path + ".tmp"), "Temporary file must not be left behind.");

            // Overwriting an existing file goes down the File.Replace path.
            config.Animation.MaxBlur = 21f;
            Assert.True(ConfigStore.TrySave(path, config, out error), error);

            ConfigLoadResult reloaded = ConfigStore.Load(path);
            Assert.Equal(ConfigLoadStatus.Loaded, reloaded.Status);
            Assert.Equal(21f, reloaded.Config.Animation.MaxBlur);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void CloneIsDeep()
    {
        LidFlowConfig original = new();
        original.Normalize();

        LidFlowConfig copy = original.Clone();
        copy.Animation.MaxBlur = 123f;
        copy.Animation.CloseEasing.Bezier[0] = 0.99f;
        copy.Monitor.ManualDisplayId = "changed";

        Assert.NotEqual(123f, original.Animation.MaxBlur);
        Assert.NotEqual(0.99f, original.Animation.CloseEasing.Bezier[0]);
        Assert.Null(original.Monitor.ManualDisplayId);
    }
}
