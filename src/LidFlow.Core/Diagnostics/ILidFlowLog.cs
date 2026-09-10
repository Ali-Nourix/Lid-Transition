using System;

namespace LidFlow.Core.Diagnostics;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>
/// Minimal logging sink.
/// <para>
/// Deliberately tiny and dependency-free. Production logging is limited to lid state,
/// monitor selection, capture results, animation state and exceptions — never screen
/// content and never anything the user typed or looked at.
/// </para>
/// </summary>
public interface ILidFlowLog
{
    void Log(LogLevel level, string message, Exception? exception = null);
}

/// <summary>Sink that discards everything. Used when logging is disabled.</summary>
public sealed class NullLog : ILidFlowLog
{
    public static NullLog Instance { get; } = new();

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        // Intentionally empty.
    }
}

/// <summary>Convenience wrappers so call sites stay readable.</summary>
public static class LogExtensions
{
    public static void Debug(this ILidFlowLog log, string message) => log.Log(LogLevel.Debug, message);

    public static void Info(this ILidFlowLog log, string message) => log.Log(LogLevel.Info, message);

    public static void Warn(this ILidFlowLog log, string message) => log.Log(LogLevel.Warning, message);

    public static void Error(this ILidFlowLog log, string message, Exception? exception = null) =>
        log.Log(LogLevel.Error, message, exception);
}
