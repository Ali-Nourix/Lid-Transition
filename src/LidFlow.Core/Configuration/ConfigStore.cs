using System;
using System.IO;
using System.Text.Json;

namespace LidFlow.Core.Configuration;

/// <summary>Outcome of a configuration load.</summary>
public enum ConfigLoadStatus
{
    /// <summary>File existed and parsed.</summary>
    Loaded = 0,

    /// <summary>File did not exist; defaults are in use.</summary>
    NotFound,

    /// <summary>File existed but could not be read or parsed; defaults are in use.</summary>
    Invalid,
}

/// <summary>Result of a configuration load.</summary>
public readonly struct ConfigLoadResult
{
    public ConfigLoadResult(LidFlowConfig config, ConfigLoadStatus status, string? message = null)
    {
        Config = config;
        Status = status;
        Message = message;
    }

    public LidFlowConfig Config { get; }

    public ConfigLoadStatus Status { get; }

    /// <summary>Parse error detail when <see cref="Status"/> is <see cref="ConfigLoadStatus.Invalid"/>.</summary>
    public string? Message { get; }
}

/// <summary>
/// Loads and saves <see cref="LidFlowConfig"/> as JSON.
/// <para>
/// A malformed or partially-written config never stops the app: loading falls back to
/// defaults and reports why. Saving is atomic (write to a temporary file, then replace) so
/// a crash or a power loss mid-save cannot leave an unparseable config behind — which
/// matters more than usual here, because the whole point of this app is running while the
/// machine is being put to sleep.
/// </para>
/// </summary>
public static class ConfigStore
{
    public static ConfigLoadResult Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            LidFlowConfig defaults = new();
            defaults.Normalize();
            return new ConfigLoadResult(defaults, ConfigLoadStatus.NotFound);
        }

        try
        {
            string json = File.ReadAllText(path);
            LidFlowConfig? parsed = Deserialize(json);

            if (parsed is null)
            {
                LidFlowConfig defaults = new();
                defaults.Normalize();
                return new ConfigLoadResult(defaults, ConfigLoadStatus.Invalid, "Configuration deserialized to null.");
            }

            parsed.Normalize();
            return new ConfigLoadResult(parsed, ConfigLoadStatus.Loaded);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LidFlowConfig defaults = new();
            defaults.Normalize();
            return new ConfigLoadResult(defaults, ConfigLoadStatus.Invalid, ex.Message);
        }
    }

    public static LidFlowConfig? Deserialize(string json) =>
        JsonSerializer.Deserialize(json, ConfigJsonContext.Default.LidFlowConfig);

    public static string Serialize(LidFlowConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return JsonSerializer.Serialize(config, ConfigJsonContext.Default.LidFlowConfig);
    }

    /// <summary>Writes the config atomically. Returns false instead of throwing on IO failure.</summary>
    public static bool TrySave(string path, LidFlowConfig config, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Configuration path is empty.";
            return false;
        }

        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = Serialize(config);
            string temp = path + ".tmp";

            File.WriteAllText(temp, json);

            // File.Replace preserves the original on failure but needs the target to
            // exist, so fall back to Move for a first write.
            if (File.Exists(path))
            {
                File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
        {
            error = ex.Message;
            return false;
        }
    }
}
