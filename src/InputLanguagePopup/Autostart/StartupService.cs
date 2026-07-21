using System.IO;
using Microsoft.Win32;
using InputLanguagePopup.Diagnostics;

namespace InputLanguagePopup.Autostart;

/// <summary>
/// Manages the optional "Start with Windows" entry under
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run. Requires no admin rights
/// and only ever touches this application's own value.
/// </summary>
public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "InputLanguagePopup";

    private readonly Logger _logger;

    public StartupService(Logger logger)
    {
        _logger = logger;
    }

    /// <summary>True if a non-empty autostart value exists (regardless of its path).</summary>
    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrEmpty(value);
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to read startup registry value.", ex);
            return false;
        }
    }

    /// <summary>
    /// Register or remove the autostart entry. Returns <c>true</c> if the registry
    /// now reflects the requested state, <c>false</c> if the operation could not be
    /// completed (e.g. running under the dotnet host, or a registry error) — the
    /// caller should not persist an "enabled" setting on a <c>false</c> result.
    /// </summary>
    public bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
            {
                _logger.Warn("Could not open the Run registry key.");
                return false;
            }

            if (enabled)
            {
                var command = GetExpectedCommand();
                if (command is null)
                {
                    return false; // reason already logged
                }

                var existing = key.GetValue(ValueName) as string;
                if (string.Equals(existing, command, StringComparison.OrdinalIgnoreCase))
                {
                    return true; // already correct
                }

                // Repair a *stale* entry (the recorded executable no longer exists,
                // e.g. the portable exe was moved), but never hijack a valid entry
                // that points at another existing copy — otherwise simply running a
                // second/dev build would steal the user's autostart.
                if (existing is not null && TargetExists(existing))
                {
                    _logger.Info("Autostart already points at an existing executable; leaving it unchanged.");
                    return true;
                }

                key.SetValue(ValueName, command);
                _logger.Info(existing is null
                    ? "Autostart entry created."
                    : "Autostart entry repaired (previous target no longer exists).");

                return true;
            }

            if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                _logger.Info("Autostart entry removed.");
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to update startup registry value.", ex);
            return false;
        }
    }

    /// <summary>True if the executable quoted in a Run value still exists on disk.</summary>
    private static bool TargetExists(string command)
    {
        var path = command.Trim();
        if (path.StartsWith('"'))
        {
            var end = path.IndexOf('"', 1);
            if (end < 0)
            {
                return false;
            }

            path = path[1..end];
        }

        return path.Length > 0 && File.Exists(path);
    }

    /// <summary>
    /// The command that should be stored, or <c>null</c> if autostart cannot be
    /// registered meaningfully (empty path, or running under the dotnet host during
    /// a <c>dotnet run</c> development session — where ProcessPath is dotnet.exe and
    /// a bare entry would just relaunch the host with no app).
    /// </summary>
    private string? GetExpectedCommand()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            _logger.Warn("Environment.ProcessPath was empty; cannot set autostart.");
            return null;
        }

        var fileName = Path.GetFileNameWithoutExtension(exePath);
        if (string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Warn("Running under the dotnet host (development run); skipping autostart registration.");
            return null;
        }

        return $"\"{exePath}\"";
    }
}
