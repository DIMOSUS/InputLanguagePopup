using System;
using System.IO;
using System.Text;

namespace InputLanguagePopup.Diagnostics;

/// <summary>
/// Lightweight, dependency-free file logger with simple size-based rotation.
/// Deliberately never logs individual keystrokes — see the security section of
/// the specification. Thread-safe; safe to call from the hook thread.
/// </summary>
public sealed class Logger : IDisposable
{
    private const long MaxFileBytes = 1 * 1024 * 1024; // 1 MB
    private const int MaxFiles = 5;

    private readonly object _sync = new();
    private readonly string _logDirectory;
    private readonly string _currentFile;
    private bool _disposed;

    public Logger(string appDataDirectory)
    {
        _logDirectory = Path.Combine(appDataDirectory, "logs");
        Directory.CreateDirectory(_logDirectory);
        _currentFile = Path.Combine(_logDirectory, "app.log");
    }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message} :: {ex}");

    private void Write(string level, string message)
    {
        if (_disposed)
        {
            return;
        }

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";

        lock (_sync)
        {
            try
            {
                RotateIfNeeded();
                File.AppendAllText(_currentFile, line, Encoding.UTF8);
            }
            catch
            {
                // Logging must never crash the application. Swallow I/O errors.
            }
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(_currentFile);
            if (!info.Exists || info.Length < MaxFileBytes)
            {
                return;
            }

            // Shift app.log.(n-1) -> app.log.n, dropping the oldest.
            var oldest = Path.Combine(_logDirectory, $"app.log.{MaxFiles - 1}");
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (var i = MaxFiles - 2; i >= 1; i--)
            {
                var src = Path.Combine(_logDirectory, $"app.log.{i}");
                var dst = Path.Combine(_logDirectory, $"app.log.{i + 1}");
                if (File.Exists(src))
                {
                    File.Move(src, dst, overwrite: true);
                }
            }

            File.Move(_currentFile, Path.Combine(_logDirectory, "app.log.1"), overwrite: true);
        }
        catch
        {
            // Ignore rotation failures.
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
