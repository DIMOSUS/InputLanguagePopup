using InputLanguagePopup.Settings;
using Xunit;

namespace InputLanguagePopup.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Normalize_ClampsOutOfRangeValues()
    {
        var s = new AppSettings
        {
            PopupDurationMs = 999_999,
            FirstLayoutCheckDelayMs = -100,
            SecondLayoutCheckDelayMs = 10_000,
            CaretOffsetX = 5_000,
            CaretOffsetY = -5_000,
            CursorOffsetX = 999,
            CursorOffsetY = -999,
        };

        s.Normalize();

        Assert.Equal(5000, s.PopupDurationMs);          // clamped to max
        Assert.Equal(0, s.FirstLayoutCheckDelayMs);      // clamped to min
        Assert.Equal(2000, s.SecondLayoutCheckDelayMs);  // clamped to max
        Assert.Equal(200, s.CaretOffsetX);
        Assert.Equal(-200, s.CaretOffsetY);
        Assert.Equal(200, s.CursorOffsetX);
        Assert.Equal(-200, s.CursorOffsetY);
    }

    [Fact]
    public void Normalize_LeavesValidValuesUnchanged()
    {
        var s = new AppSettings
        {
            PopupDurationMs = 850,
            FirstLayoutCheckDelayMs = 50,
            SecondLayoutCheckDelayMs = 140,
            CaretOffsetX = 8,
            CaretOffsetY = 8,
            CursorOffsetX = 16,
            CursorOffsetY = 20,
        };

        s.Normalize();

        Assert.Equal(850, s.PopupDurationMs);
        Assert.Equal(50, s.FirstLayoutCheckDelayMs);
        Assert.Equal(140, s.SecondLayoutCheckDelayMs);
        Assert.Equal(8, s.CaretOffsetX);
        Assert.Equal(20, s.CursorOffsetY);
    }

    [Fact]
    public void Defaults_MatchSpecification()
    {
        var s = new AppSettings();

        Assert.True(s.Enabled);
        Assert.Equal(850, s.PopupDurationMs);
        Assert.Equal(50, s.FirstLayoutCheckDelayMs);
        Assert.Equal(140, s.SecondLayoutCheckDelayMs);
        Assert.True(s.UseUiAutomation);
        Assert.True(s.HandleWinSpace);
        Assert.False(s.StartWithWindows);
    }
}
