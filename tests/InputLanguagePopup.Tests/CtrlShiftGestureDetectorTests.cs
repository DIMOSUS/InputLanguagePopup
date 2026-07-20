using InputLanguagePopup.Hooking;
using Xunit;

namespace InputLanguagePopup.Tests;

public class CtrlShiftGestureDetectorTests
{
    // Virtual key codes mirrored from the detector for readable tests.
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_S = 0x53;
    private const int VK_T = 0x54;
    private const int VK_ESCAPE = 0x1B;

    private static (CtrlShiftGestureDetector Detector, Counter Counter) CreateDetector()
    {
        var detector = new CtrlShiftGestureDetector();
        var counter = new Counter();
        detector.GestureRecognized += (_, _) => counter.Count++;
        return (detector, counter);
    }

    private sealed class Counter
    {
        public int Count;
    }

    [Fact] // Scenario 1
    public void CtrlDown_ShiftDown_ShiftUp_CtrlUp_FiresOnce()
    {
        var (d, c) = CreateDetector();

        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LCONTROL);

        Assert.Equal(1, c.Count);
    }

    [Fact] // Scenario 2
    public void ShiftDown_CtrlDown_CtrlUp_ShiftUp_FiresOnce()
    {
        var (d, c) = CreateDetector();

        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyUp(VK_LCONTROL);
        d.OnKeyUp(VK_LSHIFT);

        Assert.Equal(1, c.Count);
    }

    [Fact] // Scenario 3
    public void RightCtrlAndRightShift_FiresOnce()
    {
        var (d, c) = CreateDetector();

        d.OnKeyDown(VK_RCONTROL);
        d.OnKeyDown(VK_RSHIFT);
        d.OnKeyUp(VK_RSHIFT);
        d.OnKeyUp(VK_RCONTROL);

        Assert.Equal(1, c.Count);
    }

    [Fact] // Mixed left/right modifiers still count as a clean chord
    public void LeftCtrl_RightShift_FiresOnce()
    {
        var (d, c) = CreateDetector();

        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_RSHIFT);
        d.OnKeyUp(VK_LCONTROL);
        d.OnKeyUp(VK_RSHIFT);

        Assert.Equal(1, c.Count);
    }

    [Fact] // Scenario 4
    public void CtrlShiftS_DoesNotFire()
    {
        var (d, c) = CreateDetector();

        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyDown(VK_S);
        d.OnKeyUp(VK_S);
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LCONTROL);

        Assert.Equal(0, c.Count);
    }

    [Fact] // Additional: Ctrl+Shift+T
    public void CtrlShiftT_DoesNotFire()
    {
        var (d, c) = CreateDetector();

        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyDown(VK_T);
        d.OnKeyUp(VK_T);
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LCONTROL);

        Assert.Equal(0, c.Count);
    }

    [Fact] // Scenario 5
    public void CtrlShiftEsc_DoesNotFire()
    {
        var (d, c) = CreateDetector();

        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyDown(VK_ESCAPE);
        d.OnKeyUp(VK_ESCAPE);
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LCONTROL);

        Assert.Equal(0, c.Count);
    }

    [Fact] // The third key breaks the chord even if released before the modifiers
    public void ThirdKeyReleasedBeforeModifiers_StillCancels()
    {
        var (d, c) = CreateDetector();

        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_S);      // cancels
        d.OnKeyUp(VK_S);
        d.OnKeyDown(VK_LSHIFT); // both held now, but chord already cancelled
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LCONTROL);

        Assert.Equal(0, c.Count);
    }

    [Fact] // Scenario 6: Ctrl auto-repeat
    public void CtrlAutoRepeat_DoesNotCauseExtraFires()
    {
        var (d, c) = CreateDetector();

        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_LSHIFT);
        // Simulate auto-repeat: several extra key-down events without key-ups.
        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LCONTROL);

        Assert.Equal(1, c.Count);
    }

    [Fact] // Scenario 7
    public void CtrlAlone_DoesNotFire()
    {
        var (d, c) = CreateDetector();

        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyUp(VK_LCONTROL);

        Assert.Equal(0, c.Count);
    }

    [Fact] // Scenario 8
    public void ShiftAlone_DoesNotFire()
    {
        var (d, c) = CreateDetector();

        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyUp(VK_LSHIFT);

        Assert.Equal(0, c.Count);
    }

    [Fact] // Scenario 9
    public void MultipleConsecutiveChords_FireOncePerChord()
    {
        var (d, c) = CreateDetector();

        for (var i = 0; i < 3; i++)
        {
            d.OnKeyDown(VK_LCONTROL);
            d.OnKeyDown(VK_LSHIFT);
            d.OnKeyUp(VK_LSHIFT);
            d.OnKeyUp(VK_LCONTROL);
        }

        Assert.Equal(3, c.Count);
    }

    [Fact] // Scenario 10
    public void Reset_ClearsState_NoStuckModifiers()
    {
        var (d, c) = CreateDetector();

        // Press modifiers, then reset as if focus was lost (no key-up delivered).
        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_LSHIFT);
        d.Reset();

        // Stray key-ups after a reset must not fire anything.
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LCONTROL);
        Assert.Equal(0, c.Count);

        // A fresh, clean chord after reset still works exactly once.
        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LCONTROL);
        Assert.Equal(1, c.Count);
    }

    [Fact] // Releasing modifiers in any order still fires
    public void ReleaseOrderIndependent_FiresOnce()
    {
        var (d, c) = CreateDetector();

        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyUp(VK_LSHIFT);   // release shift first
        d.OnKeyUp(VK_LCONTROL);

        Assert.Equal(1, c.Count);
    }

    [Fact] // Lost key-ups (secure desktop): stale state is discarded, then recovery works
    public void StaleChord_AfterLostKeyUps_DoesNotFire_AndRecovers()
    {
        long now = 0;
        var d = new CtrlShiftGestureDetector(() => now);
        var count = 0;
        d.GestureRecognized += (_, _) => count++;

        // Chord starts, then a UAC prompt steals the desktop: key-ups never arrive.
        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_LSHIFT);

        // Much later, stray release events (or a fresh interaction) arrive.
        now = CtrlShiftGestureDetector.StaleChordTimeoutMs + 1_000;
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LCONTROL);
        Assert.Equal(0, count); // stale chord must not fire

        // A fresh clean chord right after still works exactly once.
        d.OnKeyDown(VK_LCONTROL);
        d.OnKeyDown(VK_LSHIFT);
        d.OnKeyUp(VK_LSHIFT);
        d.OnKeyUp(VK_LCONTROL);
        Assert.Equal(1, count);
    }

    [Fact] // A slow but continuous chord (each gap under the timeout) still fires
    public void SlowChord_WithGapsUnderTimeout_StillFires()
    {
        long now = 0;
        var d = new CtrlShiftGestureDetector(() => now);
        var count = 0;
        d.GestureRecognized += (_, _) => count++;

        var step = CtrlShiftGestureDetector.StaleChordTimeoutMs - 1_000;
        d.OnKeyDown(VK_LCONTROL);
        now += step;
        d.OnKeyDown(VK_LSHIFT);
        now += step;
        d.OnKeyUp(VK_LSHIFT);
        now += step;
        d.OnKeyUp(VK_LCONTROL);

        Assert.Equal(1, count);
    }
}
