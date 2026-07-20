using InputLanguagePopup.Input;
using Xunit;

namespace InputLanguagePopup.Tests;

public class CapsLockTrackerTests
{
    private const int VK_CAPITAL = 0x14;
    private const int VK_A = 0x41;

    [Fact]
    public void InitialState_IsHonoured()
    {
        Assert.False(new CapsLockTracker(false).IsOn);
        Assert.True(new CapsLockTracker(true).IsOn);
    }

    [Fact]
    public void KeyDown_TogglesOnce()
    {
        var t = new CapsLockTracker(false);

        t.OnKeyDown(VK_CAPITAL);
        t.OnKeyUp(VK_CAPITAL);
        Assert.True(t.IsOn);

        t.OnKeyDown(VK_CAPITAL);
        t.OnKeyUp(VK_CAPITAL);
        Assert.False(t.IsOn);
    }

    [Fact]
    public void AutoRepeat_DoesNotReToggle()
    {
        var t = new CapsLockTracker(false);

        t.OnKeyDown(VK_CAPITAL);
        t.OnKeyDown(VK_CAPITAL); // auto-repeat
        t.OnKeyDown(VK_CAPITAL); // auto-repeat
        Assert.True(t.IsOn);     // still a single toggle

        t.OnKeyUp(VK_CAPITAL);
        Assert.True(t.IsOn);
    }

    [Fact]
    public void OtherKeys_DoNotAffectState()
    {
        var t = new CapsLockTracker(true);

        t.OnKeyDown(VK_A);
        t.OnKeyUp(VK_A);

        Assert.True(t.IsOn);
    }

    [Fact]
    public void Resync_ReBaselinesAndClearsLatch()
    {
        var t = new CapsLockTracker(false);
        t.OnKeyDown(VK_CAPITAL); // physically down, on

        t.Resync(false); // e.g. after lock screen; latch cleared

        // A fresh press toggles from the re-synced baseline.
        t.OnKeyDown(VK_CAPITAL);
        Assert.True(t.IsOn);
    }
}
