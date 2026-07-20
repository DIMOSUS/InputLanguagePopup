namespace InputLanguagePopup.Settings;

/// <summary>
/// Persisted user settings. Property names / defaults mirror the JSON schema in
/// the specification. All timings are in milliseconds and offsets in logical px.
/// </summary>
public sealed class AppSettings
{
    public bool Enabled { get; set; } = true;

    public int PopupDurationMs { get; set; } = 850;

    public int FirstLayoutCheckDelayMs { get; set; } = 50;

    public int SecondLayoutCheckDelayMs { get; set; } = 140;

    public int CaretOffsetX { get; set; } = 8;

    public int CaretOffsetY { get; set; } = 8;

    public int CursorOffsetX { get; set; } = 16;

    public int CursorOffsetY { get; set; } = 20;

    public bool UseUiAutomation { get; set; } = true;

    public bool StartWithWindows { get; set; } = false;

    /// <summary>Clamp values to sane ranges so a hand-edited file cannot break the app.</summary>
    public void Normalize()
    {
        PopupDurationMs = Math.Clamp(PopupDurationMs, 200, 5000);
        FirstLayoutCheckDelayMs = Math.Clamp(FirstLayoutCheckDelayMs, 0, 2000);
        SecondLayoutCheckDelayMs = Math.Clamp(SecondLayoutCheckDelayMs, 0, 2000);
        CaretOffsetX = Math.Clamp(CaretOffsetX, -200, 200);
        CaretOffsetY = Math.Clamp(CaretOffsetY, -200, 200);
        CursorOffsetX = Math.Clamp(CursorOffsetX, -200, 200);
        CursorOffsetY = Math.Clamp(CursorOffsetY, -200, 200);
    }
}
