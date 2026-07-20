using Microsoft.Win32;
using InputLanguagePopup.Diagnostics;

namespace InputLanguagePopup.Input;

/// <summary>
/// Reads which chord Windows is currently configured to switch input language /
/// keyboard layout with, from HKCU\Keyboard Layout\Toggle:
///
///   "Language Hotkey" / legacy "Hotkey" — switch input language,
///   "Layout Hotkey"                     — switch keyboard layout,
///   values: "1" = Left Alt+Shift, "2" = Ctrl+Shift, "3" = none, "4" = grave (`).
///
/// The key is read on every completed chord (a registry read costs microseconds
/// and happens off the hot path), so changes made in Windows settings take
/// effect immediately, with no watcher and no restart. Win+Space is hardwired in
/// Windows and intentionally not represented here.
/// </summary>
public sealed class SystemHotkeyService
{
    private const string ToggleKeyPath = @"Keyboard Layout\Toggle";

    private readonly Logger _logger;
    private bool _graveLogged;
    private bool _errorLogged;

    public SystemHotkeyService(Logger logger)
    {
        _logger = logger;
    }

    /// <summary>Which chords currently switch language/layout, per system settings.</summary>
    public (bool CtrlShift, bool AltShift) GetConfiguredChords()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ToggleKeyPath, writable: false);
            if (key is null)
            {
                // No key at all — the Windows default is Left Alt+Shift.
                return (CtrlShift: false, AltShift: true);
            }

            var language = ReadCode(key, "Language Hotkey") ?? ReadCode(key, "Hotkey") ?? 1;
            var layout = ReadCode(key, "Layout Hotkey") ?? 3;

            if ((language == 4 || layout == 4) && !_graveLogged)
            {
                _graveLogged = true;
                _logger.Info("The grave-accent layout hotkey (code 4) is configured; the indicator does not support it.");
            }

            return (
                CtrlShift: language == 2 || layout == 2,
                AltShift: language == 1 || layout == 1);
        }
        catch (Exception ex)
        {
            if (!_errorLogged)
            {
                _errorLogged = true;
                _logger.Error("Failed to read the system layout hotkey; treating both chords as active.", ex);
            }

            // Graceful degradation: better to show the indicator on either chord
            // than to never show it.
            return (CtrlShift: true, AltShift: true);
        }
    }

    private static int? ReadCode(RegistryKey key, string valueName)
    {
        var raw = key.GetValue(valueName) as string;
        return int.TryParse(raw, out var code) ? code : null;
    }
}
