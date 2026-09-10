using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace LidFlow.Core.Diagnostics;

/// <summary>
/// Appends to a single log file, rolling it aside once it exceeds a size cap and keeping a
/// small number of previous files.
/// <para>
/// Writes are serialized and each one is flushed, because this process can be terminated at
/// any moment by the machine suspending — a buffered log would routinely lose exactly the
/// lines that explain what happened just before sleep.
/// </para>
/// </summary>
public sealed class RollingFileLog : ILidFlowLog, IDisposable
{
    private const long DefaultMaxBytes = 512 * 1024;
    private const int DefaultRetainedFiles = 3;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _retainedFiles;
    private readonly LogLevel _minimumLevel;
    private bool _disposed;
    private bool _broken;

    public RollingFileLog(
        string path,
        LogLevel minimumLevel = LogLevel.Info,
        long maxBytes = DefaultMaxBytes,
        int retainedFiles = DefaultRetainedFiles)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _minimumLevel = minimumLevel;
        _maxBytes = maxBytes > 4096 ? maxBytes : 4096;
        _retainedFiles = retainedFiles < 0 ? 0 : retainedFiles;
    }

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        if (level < _minimumLevel || _disposed || _broken)
        {
            return;
        }

        StringBuilder builder = new(message.Length + 64);
        builder.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
        builder.Append(" [");
        builder.Append(LevelTag(level));
        builder.Append("] ");
        builder.Append(message);

        if (exception is not null)
        {
            builder.AppendLine();
            builder.Append("    ");
            builder.Append(exception.GetType().FullName);
            builder.Append(": ");
            builder.Append(exception.Message);

            if (!string.IsNullOrEmpty(exception.StackTrace))
            {
                builder.AppendLine();
                builder.Append(exception.StackTrace);
            }
        }

        WriteLine(builder.ToString());
    }

    private void WriteLine(string line)
    {
        lock (_gate)
        {
            if (_disposed || _broken)
            {
                return;
            }

            try
            {
                string? directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                RollIfNeeded();

                using FileStream stream = new(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                // Logging must never take the app down, and a sink that keeps failing
                // would just burn CPU on every frame. Give up quietly and permanently.
                _broken = true;
            }
        }
    }

    private void RollIfNeeded()
    {
        FileInfo info = new(_path);
        if (!info.Exists || info.Length < _maxBytes)
        {
            return;
        }

        for (int index = _retainedFiles; index >= 1; index--)
        {
            string older = FormatRolled(index);
            string newer = index == 1 ? _path : FormatRolled(index - 1);

            if (!File.Exists(newer))
            {
                continue;
            }

            if (index == _retainedFiles)
            {
                File.Delete(older);
            }

            File.Move(newer, older, overwrite: true);
        }

        if (_retainedFiles == 0 && File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private string FormatRolled(int index)
    {
        string directory = Path.GetDirectoryName(_path) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(_path);
        string extension = Path.GetExtension(_path);
        return Path.Combine(directory, string.Concat(name, ".", index.ToString(CultureInfo.InvariantCulture), extension));
    }

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        _ => "???",
    };

    public void Dispose() => _disposed = true;
}
