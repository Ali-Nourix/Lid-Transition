using System;
using Microsoft.Win32;
using LidFlow.Core.Diagnostics;

namespace LidFlow.App;

/// <summary>
/// Registers LidFlow to start at sign-in.
/// <para>
/// Uses the per-user <c>Run</c> key, which needs no administrator rights, is
/// visible and disableable in Task Manager's Startup tab, and is removed cleanly
/// by unregistering. Deliberately not a scheduled task or a service: neither is
/// appropriate for something that has to draw in the interactive session, and
/// both would need elevation to install.
/// </para>
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LidFlow";

    public static bool IsRegistered()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool TrySet(bool enabled, ILidFlowLog log)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("Could not open the per-user Run key.");

            if (enabled)
            {
                string? executable = Environment.ProcessPath;
                if (string.IsNullOrEmpty(executable))
                {
                    return false;
                }

                // Quoted, because the install path routinely contains spaces.
                key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
                log.Info("Registered for start-up.");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                log.Info("Removed the start-up registration.");
            }

            return true;
        }
        catch (Exception ex)
        {
            log.Error("Updating the start-up registration failed.", ex);
            return false;
        }
    }
}
