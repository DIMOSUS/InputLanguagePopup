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
    private const int VK_CAPITAL = 0x14;

    private bool _on;
    private bool _physicallyDown; // guards against auto-repeat re-toggling

    public CapsLockTracker(bool initialOn)
    {
        _on = initialOn;
    }

    public bool IsOn => _on;

    public void OnKeyDown(int vk)
    {
        // CapsLock toggles on the initial press; auto-repeat key-downs must not.
        if (vk == VK_CAPITAL && !_physicallyDown)
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
