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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // camelCase on disk (matches the documented schema); case-insensitive
        // reading also accepts hand-edited or legacy PascalCase files.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

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
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            settings.Normalize();

            // Re-save so fields added in newer versions appear in the file with
            // their defaults instead of staying invisible.
            Save(settings);
            return settings;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to load settings; falling back to defaults.", ex);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            settings.Normalize();
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to save settings.", ex);
        }
    }
}
