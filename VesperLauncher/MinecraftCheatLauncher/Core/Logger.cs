using System;
using System.IO;
using VesperLauncher.Utils;

namespace VesperLauncher.Core;

public sealed class Logger
{
    private const long DefaultMaxLogFileBytes = 2 * 1024 * 1024;
    private const int DefaultMaxRotatedFiles = 5;
    private readonly object _writeLock = new();
    private readonly long _maxLogFileBytes;
    private readonly int _maxRotatedFiles;

    public Logger(
        string componentName,
        string? username = null,
        long maxLogFileBytes = DefaultMaxLogFileBytes,
        int maxRotatedFiles = DefaultMaxRotatedFiles)
    {
        ComponentName = PathHelper.SanitizePathSegment(componentName, "launcher");
        LogDirectory = PathHelper.CreateLogSessionDirectory(username ?? Environment.UserName);
        LogFilePath = Path.Combine(LogDirectory, $"{ComponentName}.log");
        _maxLogFileBytes = Math.Max(256 * 1024, maxLogFileBytes);
        _maxRotatedFiles = Math.Clamp(maxRotatedFiles, 1, 20);
    }

    public string ComponentName { get; }

    public string LogDirectory { get; }

    public string LogFilePath { get; }

    public void Info(string message)
    {
        Write("INFO", message);
    }

    public void Warning(string message)
    {
        Write("WARN", message);
    }

    public void Error(Exception exception, string message)
    {
        Write("ERROR", $"{message}{Environment.NewLine}{exception}");
    }

    public void Error(string message)
    {
        Write("ERROR", message);
    }

    public void WriteRaw(string entry)
    {
        if (string.IsNullOrEmpty(entry))
        {
            return;
        }

        WriteEntry(entry.EndsWith(Environment.NewLine, StringComparison.Ordinal)
            ? entry
            : entry + Environment.NewLine);
    }

    private void Write(string level, string message)
    {
        var entry =
            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] [{level}] [{ComponentName}] {message}" +
            Environment.NewLine +
            new string('-', 70) +
            Environment.NewLine;

        WriteEntry(entry);
    }

    private void WriteEntry(string entry)
    {
        try
        {
            lock (_writeLock)
            {
                PathHelper.EnsureDirectory(LogDirectory);
                RotateIfNeeded();
                File.AppendAllText(LogFilePath, entry);
            }
        }
        catch
        {
            // Logging must never crash the launcher.
        }
    }

    private void RotateIfNeeded()
    {
        var fileInfo = new FileInfo(LogFilePath);
        if (!fileInfo.Exists || fileInfo.Length < _maxLogFileBytes)
        {
            return;
        }

        DeleteOldestRotation();
        for (var index = _maxRotatedFiles - 1; index >= 1; index--)
        {
            var source = $"{LogFilePath}.{index}";
            var target = $"{LogFilePath}.{index + 1}";
            MoveIfExists(source, target);
        }

        MoveIfExists(LogFilePath, $"{LogFilePath}.1");
    }

    private void DeleteOldestRotation()
    {
        var oldestPath = $"{LogFilePath}.{_maxRotatedFiles}";
        if (File.Exists(oldestPath))
        {
            File.Delete(oldestPath);
        }
    }

    private static void MoveIfExists(string source, string target)
    {
        if (!File.Exists(source))
        {
            return;
        }

        if (File.Exists(target))
        {
            File.Delete(target);
        }

        File.Move(source, target);
    }
}

