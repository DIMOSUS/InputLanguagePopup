using InputLanguagePopup.Input;
using Xunit;

namespace InputLanguagePopup.Tests;

public class InputLanguageServiceTests
{
    [Theory]
    [InlineData("EN", false, "EN")]
    [InlineData("EN", true, "EN CAPS")]
    [InlineData("RU", true, "RU CAPS")]
    [InlineData("LT", false, "LT")]
    public void ComposeDisplayText_AppendsCapsWhenOn(string code, bool caps, string expected)
    {
        Assert.Equal(expected, InputLanguageService.ComposeDisplayText(code, caps));
    }
}
