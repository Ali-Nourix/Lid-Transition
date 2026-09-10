using System;
using System.IO;

namespace LidFlow.App;

/// <summary>
/// Where LidFlow keeps its files.
/// <para>
/// Everything lives under the per-user local application data folder, so nothing
/// needs administrator rights and nothing is written next to the executable -
/// which matters because the app is meant to be runnable from a read-only folder
/// or a USB stick.
/// </para>
/// </summary>
internal static class AppPaths
{
    public const string ProductName = "LidFlow";

    /// <summary>%LOCALAPPDATA%\LidFlow</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductName);

    /// <summary>%LOCALAPPDATA%\LidFlow\config.json</summary>
    public static string ConfigFile => Path.Combine(DataDirectory, "config.json");

    /// <summary>%LOCALAPPDATA%\LidFlow\Logs</summary>
    public static string LogDirectory => Path.Combine(DataDirectory, "Logs");

    /// <summary>%LOCALAPPDATA%\LidFlow\Logs\lidflow.log</summary>
    public static string LogFile => Path.Combine(LogDirectory, "lidflow.log");

    /// <summary>
    /// A config.json sitting beside the executable, if there is one. Used for
    /// portable and development setups: it takes precedence over the per-user file
    /// so a tuning config can travel with the build.
    /// </summary>
    public static string? PortableConfigFile
    {
        get
        {
            try
            {
                string? directory = Path.GetDirectoryName(Environment.ProcessPath);
                if (string.IsNullOrEmpty(directory))
                {
                    return null;
                }

                string candidate = Path.Combine(directory, "config.json");
                return File.Exists(candidate) ? candidate : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>The config file that should actually be read.</summary>
    public static string EffectiveConfigFile => PortableConfigFile ?? ConfigFile;

    public static void EnsureDataDirectory()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(LogDirectory);
        }
        catch (Exception)
        {
            // Logging and configuration both degrade gracefully if this fails, so
            // there is nothing useful to do here.
        }
    }
}
