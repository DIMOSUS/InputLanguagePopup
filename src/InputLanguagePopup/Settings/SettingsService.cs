using System;
using System.IO;
using System.Text.Json;
using InputLanguagePopup.Diagnostics;

namespace InputLanguagePopup.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON under
/// %LocalAppData%\InputLanguagePopup\settings.json. Corrupt or missing files
/// fall back to defaults rather than throwing.
/// </summary>
public sealed class SettingsService
{
    private readonly string _settingsPath;
    private readonly Logger _logger;

    public SettingsService(string appDataDirectory, Logger logger)
    {
        _logger = logger;
        Directory.CreateDirectory(appDataDirectory);
        _settingsPath = Path.Combine(appDataDirectory, "settings.json");
    }

    public string SettingsPath => _settingsPath;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                _logger.Info("Settings file not found; using defaults and creating one.");
                var defaults = new AppSettings();
                Save(defaults);
                return defaults;
            }

            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            settings.Normalize();

            // Re-save so fields added in newer versions appear in the file with
            // their defaults instead of staying invisible.
            Save(settings);
            return settings;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to load settings; quarantining the file and writing defaults.", ex);

            // Move the unreadable file aside so it is not retried on every launch,
            // then write a fresh default file (matches the documented behaviour).
            QuarantineCorruptFile();

            var defaults = new AppSettings();
            Save(defaults);
            return defaults;
        }
    }

    private void QuarantineCorruptFile()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var corruptPath = Path.Combine(
                    Path.GetDirectoryName(_settingsPath)!,
                    $"settings.corrupt.{stamp}.json");
                File.Move(_settingsPath, corruptPath, overwrite: true);
                _logger.Info($"Corrupt settings moved to {corruptPath}.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to quarantine the corrupt settings file.", ex);
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            settings.Normalize();
            var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to save settings.", ex);
        }
    }
}
