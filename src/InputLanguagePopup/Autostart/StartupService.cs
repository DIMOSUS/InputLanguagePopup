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
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    _logger.Warn("Environment.ProcessPath was empty; cannot set autostart.");
                    return;
                }

                key.SetValue(ValueName, $"\"{exePath}\"");
                _logger.Info("Autostart entry created.");
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
}
