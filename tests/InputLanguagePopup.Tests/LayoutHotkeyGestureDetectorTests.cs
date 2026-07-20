using InputLanguagePopup.Hooking;
using Xunit;

namespace InputLanguagePopup.Tests;

public class LayoutHotkeyGestureDetectorTests
{
    // Virtual key codes mirrored from the detector for readable tests.
    private const int VK_SPACE = 0x20;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_E = 0x45;
    private const int VK_S = 0x53;
    private const int VK_T = 0x54;
    private const int VK_LWIN = 0x5B;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4;   // Left Alt
    private const int VK_RMENU = 0xA5;   // Right Alt (AltGr)

    private static (LayoutHotkeyGestureDetector Detector, List<LayoutGesture> Fired) CreateDetector()
    {
        var detector = new LayoutHotkeyGestureDetector();
        var fired = new List<LayoutGesture>();
        detector.GestureRecognized += g => fired.Add(g);
        return (detector, fired);
    }

    // ---- Ctrl+Shift ------------------------------------------------------

    [Fact]
    public void CtrlDown_ShiftDown_ShiftUp_CtrlUp_FiresCtrlShiftOnce()
    {
        var (d, fired) = CreateDetector();

        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LCONTROL);

        Assert.Equal(new[] { LayoutGesture.CtrlShift }, fired);
    }

    [Fact]
    public void ShiftDown_CtrlDown_CtrlUp_ShiftUp_FiresCtrlShiftOnce()
    {
        var (d, fired) = CreateDetector();

        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyUp(VK_LCONTROL);
        d.OnKeyUp(VK_LSHIFT);

        Assert.Equal(new[] { LayoutGesture.CtrlShift }, fired);
    }

    [Fact]
    public void RightCtrlAndRightShift_FiresCtrlShiftOnce()
    {
        var (d, fired) = CreateDetector();

        d.OnKeyDown(VK_RCONTROL);
        d.OnKeyDown(VK_RSHIFT);
        d.OnKeyUp(VK_RSHIFT);
        d.OnKeyUp(VK_RCONTROL);

        Assert.Equal(new[] { LayoutGesture.CtrlShift }, fired);
    }

    [Fact]
    public void LeftCtrl_RightShift_FiresCtrlShiftOnce()
    {
        var (d, fired) = CreateDetector();

        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_RSHIFT);
        d.OnKeyUp(VK_LCONTROL);
        d.OnKeyUp(VK_RSHIFT);

        Assert.Equal(new[] { LayoutGesture.CtrlShift }, fired);
    }

    // ---- Alt+Shift -------------------------------------------------------

    [Fact]
    public void AltDown_ShiftDown_Release_FiresAltShiftOnce()
    {
        var (d, fired) = CreateDetector();

        d.OnKeyDown(VK_LMENU);
        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LMENU);

        Assert.Equal(new[] { LayoutGesture.AltShift }, fired);
    }

    [Fact]
    public void ShiftDown_AltDown_Release_FiresAltShiftOnce()
    {
        var (d, fired) = CreateDetector();

        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyDown(VK_LMENU);
        d.OnKeyUp(VK_LMENU);
        d.OnKeyUp(VK_LSHIFT);

        Assert.Equal(new[] { LayoutGesture.AltShift }, fired);
    }

    [Fact]
    public void AltShiftWithThirdKey_DoesNotFire()
    {
        var (d, fired) = CreateDetector();

        d.OnKeyDown(VK_LMENU);
        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyDown(VK_T);
        d.OnKeyUp(VK_T);
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LMENU);

        Assert.Empty(fired);
    }

    // ---- Win+Space -------------------------------------------------------

    [Fact]
    public void WinSpace_FiresOnWinRelease()
    {
        var (d, fired) = CreateDetector();

        d.OnKeyDown(VK_LWIN);
        d.OnKeyDown(VK_SPACE);
        d.OnKeyUp(VK_SPACE);
        d.OnKeyUp(VK_LWIN);

        Assert.Equal(new[] { LayoutGesture.WinSpace }, fired);
    }

    [Fact]
    public void WinShiftSpace_ReverseCycling_Fires()
    {
        var (d, fired) = CreateDetector();

        d.OnKeyDown(VK_LWIN);
        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyDown(VK_SPACE);
        d.OnKeyUp(VK_SPACE);
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LWIN);

        Assert.Equal(new[] { LayoutGesture.WinSpace }, fired);
    }

    [Fact]
    public void WinSpace_MultipleSpacePresses_FireOnce()
    {
        var (d, fired) = CreateDetector();

        d.OnKeyDown(VK_LWIN);
        for (var i = 0; i < 4; i++)
        {
            d.OnKeyDown(VK_SPACE); // cycling through layouts in the flyout
            d.OnKeyUp(VK_SPACE);
        }

        d.OnKeyUp(VK_LWIN);

        Assert.Equal(new[] { LayoutGesture.WinSpace }, fired);
    }

    [Fact]
    public void WinSpace_WinReleasedBeforeSpace_StillFires()
    {
        var (d, fired) = CreateDetector();

        d.OnKeyDown(VK_LWIN);
        d.OnKeyDown(VK_SPACE);
        d.OnKeyUp(VK_LWIN);   // Win up while Space still held
        d.OnKeyUp(VK_SPACE);

        Assert.Equal(new[] { LayoutGesture.WinSpace }, fired);
    }

    [Fact]
    public void WinE_DoesNotFire()
    {
        var (d, fired) = CreateDetector();

        d.OnKeyDown(VK_LWIN);
        d.OnKeyDown(VK_E);
        d.OnKeyUp(VK_E);
        d.OnKeyUp(VK_LWIN);

        Assert.Empty(fired);
    }

    [Fact]
    public void CtrlSpace_DoesNotFire()
    {
        var (d, fired) = CreateDetector();

        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_SPACE);
        d.OnKeyUp(VK_SPACE);
        d.OnKeyUp(VK_LCONTROL);

        Assert.Empty(fired);
    }

    [Fact]
    public void WinAloneTap_DoesNotFire()
    {
        var (d, fired) = CreateDetector();

        d.OnKeyDown(VK_LWIN);
        d.OnKeyUp(VK_LWIN);

        Assert.Empty(fired);
    }

    // ---- Chord exclusivity ----------------------------------------------

    [Fact]
    public void CtrlAltShift_DoesNotFire()
    {
        var (d, fired) = CreateDetector();

        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_LMENU);
        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LMENU);
        d.OnKeyUp(VK_LCONTROL);

        Assert.Empty(fired);
    }

    [Fact]
    public void AltGrTap_CtrlPlusRightAlt_DoesNotFire()
    {
        var (d, fired) = CreateDetector();

        // AltGr arrives as LCtrl down + RAlt down.
        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_RMENU);
        d.OnKeyUp(VK_RMENU);
        d.OnKeyUp(VK_LCONTROL);

        Assert.Empty(fired);
    }

    [Fact]
    public void AltGrPlusShift_DoesNotFire()
    {
        var (d, fired) = CreateDetector();

        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_RMENU);
        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_RMENU);
        d.OnKeyUp(VK_LCONTROL);

        Assert.Empty(fired);
    }

    // ---- Third-key cancellation (spec scenarios) -------------------------

    [Fact]
    public void CtrlShiftS_DoesNotFire()
    {
        var (d, fired) = CreateDetector();

        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyDown(VK_S);
        d.OnKeyUp(VK_S);
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LCONTROL);

        Assert.Empty(fired);
    }

    [Fact]
    public void CtrlShiftT_DoesNotFire()
    {
        var (d, fired) = CreateDetector();

        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyDown(VK_T);
        d.OnKeyUp(VK_T);
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LCONTROL);

        Assert.Empty(fired);
    }

    [Fact]
    public void CtrlShiftEsc_DoesNotFire()
    {
        var (d, fired) = CreateDetector();

        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyDown(VK_ESCAPE);
        d.OnKeyUp(VK_ESCAPE);
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LCONTROL);

        Assert.Empty(fired);
    }

    [Fact]
    public void ThirdKeyReleasedBeforeModifiers_StillCancels()
    {
        var (d, fired) = CreateDetector();

        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_S);      // cancels
        d.OnKeyUp(VK_S);
        d.OnKeyDown(VK_LSHIFT); // both held now, but chord already cancelled
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LCONTROL);

        Assert.Empty(fired);
    }

    // ---- Auto-repeat and single modifiers --------------------------------

    [Fact]
    public void ModifierAutoRepeat_DoesNotCauseExtraFires()
    {
        var (d, fired) = CreateDetector();

        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_LSHIFT);
        // Simulate auto-repeat: extra key-down events without key-ups.
        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LCONTROL);

        Assert.Equal(new[] { LayoutGesture.CtrlShift }, fired);
    }

    [Fact]
    public void CtrlAlone_DoesNotFire()
    {
        var (d, fired) = CreateDetector();

        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyUp(VK_LCONTROL);

        Assert.Empty(fired);
    }

    [Fact]
    public void ShiftAlone_DoesNotFire()
    {
        var (d, fired) = CreateDetector();

        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyUp(VK_LSHIFT);

        Assert.Empty(fired);
    }

    [Fact]
    public void AltAlone_DoesNotFire()
    {
        var (d, fired) = CreateDetector();

        d.OnKeyDown(VK_LMENU);
        d.OnKeyUp(VK_LMENU);

        Assert.Empty(fired);
    }

    // ---- Sequences -------------------------------------------------------

    [Fact]
    public void ConsecutiveChords_FireOncePerChord_WithCorrectKinds()
    {
        var (d, fired) = CreateDetector();

        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LCONTROL);

        d.OnKeyDown(VK_LMENU);
        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LMENU);

        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LCONTROL);

        Assert.Equal(
            new[] { LayoutGesture.CtrlShift, LayoutGesture.AltShift, LayoutGesture.CtrlShift },
            fired);
    }

    // ---- Staleness / reset -----------------------------------------------

    [Fact]
    public void Reset_ClearsState_NoStuckModifiers()
    {
        var (d, fired) = CreateDetector();

        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_LSHIFT);
        d.Reset();

        // Stray key-ups after a reset must not fire anything.
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LCONTROL);
        Assert.Empty(fired);

        // A fresh, clean chord after reset still works exactly once.
        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LCONTROL);
        Assert.Equal(new[] { LayoutGesture.CtrlShift }, fired);
    }

    [Fact]
    public void StaleChord_AfterLostKeyUps_DoesNotFire_AndRecovers()
    {
        long now = 0;
        var d = new LayoutHotkeyGestureDetector(() => now);
        var fired = new List<LayoutGesture>();
        d.GestureRecognized += g => fired.Add(g);

        // Chord starts, then a UAC prompt steals the desktop: key-ups never arrive.
        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_LSHIFT);

        // Much later, stray release events arrive.
        now = LayoutHotkeyGestureDetector.StaleChordTimeoutMs + 1_000;
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LCONTROL);
        Assert.Empty(fired); // stale chord must not fire

        // A fresh clean chord right after still works exactly once.
        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LCONTROL);
        Assert.Equal(new[] { LayoutGesture.CtrlShift }, fired);
    }

    [Fact]
    public void SlowChord_WithGapsUnderTimeout_StillFires()
    {
        long now = 0;
        var d = new LayoutHotkeyGestureDetector(() => now);
        var fired = new List<LayoutGesture>();
        d.GestureRecognized += g => fired.Add(g);

        var step = LayoutHotkeyGestureDetector.StaleChordTimeoutMs - 1_000;
        d.OnKeyDown(VK_LCONTROL);
        now += step;
        d.OnKeyDown(VK_LSHIFT);
        now += step;
        d.OnKeyUp(VK_LSHIFT);
        now += step;
        d.OnKeyUp(VK_LCONTROL);

        Assert.Equal(new[] { LayoutGesture.CtrlShift }, fired);
    }
}
