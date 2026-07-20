using System;

namespace InputLanguagePopup.Input;

/// <summary>
/// Tracks the CapsLock toggle state by observing physical key events from the
/// global hook, rather than reading <c>GetKeyState</c> at show time.
///
/// <c>GetKeyState(VK_CAPITAL)</c> reflects the *calling thread's* keyboard input
/// queue; this tray app's UI thread does not process the foreground app's keyboard
/// input, so a CapsLock toggled inside a browser/editor/game would not be visible
/// there. Watching the hook's physical `VK_CAPITAL` transitions is reliable.
///
/// Consistent with the app's policy of ignoring injected input, a CapsLock toggled
/// programmatically (which the hook filters as injected) is not tracked — a
/// documented limitation. Pure and unit-testable.
/// </summary>
public sealed class CapsLockTracker
{
    /// <summary>
    /// If a key-down arrives more than this long after the previous one while the
    /// press latch is still set, the earlier key-up was lost (e.g. to the secure
    /// desktop) — treat the new key-down as a fresh physical press so the state
    /// self-heals instead of ignoring every future toggle.
    /// </summary>
    public const int StaleLatchTimeoutMs = 10_000;

    private const int VK_CAPITAL = 0x14;

    private readonly Func<long> _ticks;
    private bool _on;
    private bool _physicallyDown; // guards against auto-repeat re-toggling
    private long _lastDownTicks;

    public CapsLockTracker(bool initialOn, Func<long>? tickProvider = null)
    {
        _on = initialOn;
        _ticks = tickProvider ?? (static () => Environment.TickCount64);
    }

    public bool IsOn => _on;

    public void OnKeyDown(int vk)
    {
        if (vk != VK_CAPITAL)
        {
            return;
        }

        var now = _ticks();

        // A long gap while still "down" means the key-up was lost; re-arm the latch
        // so this counts as a new press. Genuine auto-repeat fires far faster than
        // the timeout, so it never re-arms and never double-toggles.
        if (_physicallyDown && now - _lastDownTicks > StaleLatchTimeoutMs)
        {
            _physicallyDown = false;
        }

        _lastDownTicks = now;

        // CapsLock toggles on the initial press; auto-repeat key-downs must not.
        if (!_physicallyDown)
        {
            _on = !_on;
            _physicallyDown = true;
        }
    }

    public void OnKeyUp(int vk)
    {
        if (vk == VK_CAPITAL)
        {
            _physicallyDown = false;
        }
    }

    /// <summary>Re-baseline the state (e.g. after a session switch) and clear the press latch.</summary>
    public void Resync(bool on)
    {
        _on = on;
        _physicallyDown = false;
    }
}
