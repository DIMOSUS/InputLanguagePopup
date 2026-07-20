using InputLanguagePopup.Input;
using Xunit;

namespace InputLanguagePopup.Tests;

public class SystemHotkeyServiceTests
{
    [Theory]
    // language, layout  ->  ctrlShift, altShift
    [InlineData(2, 3, true, false)]   // Ctrl+Shift language, layout off
    [InlineData(1, 3, false, true)]   // Alt+Shift language, layout off
    [InlineData(3, 3, false, false)]  // both disabled
    [InlineData(2, 1, true, true)]    // Ctrl+Shift language + Alt+Shift layout
    [InlineData(1, 2, true, true)]    // Alt+Shift language + Ctrl+Shift layout
    [InlineData(4, 3, false, false)]  // grave language (unsupported) -> nothing
    public void InterpretCodes_MapsExpected(int language, int layout, bool ctrlShift, bool altShift)
    {
        var result = SystemHotkeyService.InterpretCodes(language, layout);

        Assert.Equal(ctrlShift, result.CtrlShift);
        Assert.Equal(altShift, result.AltShift);
    }

    [Fact]
    public void InterpretCodes_MissingLanguage_DefaultsToAltShift()
    {
        // No "Language Hotkey" value at all -> Windows default is Alt+Shift.
        var result = SystemHotkeyService.InterpretCodes(language: null, layout: null);

        Assert.False(result.CtrlShift);
        Assert.True(result.AltShift);
    }

    [Fact]
    public void InterpretCodes_MissingLayout_TreatedAsDisabled()
    {
        var result = SystemHotkeyService.InterpretCodes(language: 2, layout: null);

        Assert.True(result.CtrlShift);
        Assert.False(result.AltShift);
    }
}
