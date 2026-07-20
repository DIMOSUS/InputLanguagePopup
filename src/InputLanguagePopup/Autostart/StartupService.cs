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

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
            {
                _logger.Warn("Could not open the Run registry key.");
                return;
            }

            if (enabled)
            {
                var command = GetExpectedCommand();
                if (command is null)
                {
                    return; // reason already logged
                }

                // Only write when the value is missing or points somewhere else —
                // this repairs a stale path left behind after moving a portable exe.
                var existing = key.GetValue(ValueName) as string;
                if (string.Equals(existing, command, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                key.SetValue(ValueName, command);
                _logger.Info(existing is null
                    ? "Autostart entry created."
                    : "Autostart entry updated to the current executable path.");
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                _logger.Info("Autostart entry removed.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to update startup registry value.", ex);
        }
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
