namespace InputLanguagePopup.Hooking;

/// <summary>
/// Pure, Win32-independent state machine that recognises a "clean" Ctrl+Shift
/// chord (the standard Windows layout-switch gesture) and nothing else.
///
/// A chord fires only when:
///   * at least one Ctrl and at least one Shift were held simultaneously, and
///   * no non-modifier key was pressed while the modifiers were held, and
///   * all modifiers have been released.
///
/// Key auto-repeat produces no extra events. The class holds no UI or Win32
/// dependencies so it can be unit tested with plain virtual-key codes.
/// </summary>
public sealed class CtrlShiftGestureDetector
{
    // Virtual key codes (duplicated here so the detector stays independent of the
    // interop layer and can be referenced from tests without Win32).
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;

    /// <summary>
    /// If no key event arrives for this long while modifier state is tracked, the
    /// state is considered stale and discarded. This covers key-ups that were
    /// never delivered — e.g. a UAC prompt (secure desktop) appearing mid-chord —
    /// so modifiers cannot remain virtually held forever. Side effect: a chord
    /// deliberately held longer than this fires no event, which is acceptable.
    /// </summary>
    public const int StaleChordTimeoutMs = 10_000;

    private readonly HashSet<int> _pressedModifiers = new();
    private readonly Func<long> _ticks;
    private long _lastEventTicks;

    private bool _chordActive;   // a chord is currently being built
    private bool _bothSeen;      // Ctrl and Shift were held at the same time
    private bool _cancelled;     // a non-modifier key broke the chord

    /// <summary>Raised when a clean Ctrl+Shift chord completes.</summary>
    public event EventHandler? GestureRecognized;

    /// <param name="tickProvider">
    /// Millisecond tick source; defaults to <see cref="Environment.TickCount64"/>.
    /// Injectable so staleness can be unit tested.
    /// </param>
    public CtrlShiftGestureDetector(Func<long>? tickProvider = null)
    {
        _ticks = tickProvider ?? (static () => Environment.TickCount64);
    }

    public static bool IsCtrl(int vk) => vk is VK_CONTROL or VK_LCONTROL or VK_RCONTROL;

    public static bool IsShift(int vk) => vk is VK_SHIFT or VK_LSHIFT or VK_RSHIFT;

    public static bool IsModifier(int vk) => IsCtrl(vk) || IsShift(vk);

    /// <summary>Feed a key-down event (from the low-level hook or a test).</summary>
    public void OnKeyDown(int vk)
    {
        ExpireStaleState();

        if (IsModifier(vk))
        {
            if (_pressedModifiers.Count == 0)
            {
                // First modifier of a fresh chord.
                _chordActive = true;
                _bothSeen = false;
                _cancelled = false;
            }

            // HashSet.Add returns false for auto-repeat of an already-held key,
            // in which case there is nothing new to evaluate.
            _pressedModifiers.Add(vk);

            if (AnyCtrlHeld() && AnyShiftHeld())
            {
                _bothSeen = true;
            }
        }
        else
        {
            // Any non-modifier press while modifiers are held cancels the chord
            // (e.g. Ctrl+Shift+S, Ctrl+Shift+Esc).
            if (_chordActive)
            {
                _cancelled = true;
            }
        }
    }

    /// <summary>Feed a key-up event (from the low-level hook or a test).</summary>
    public void OnKeyUp(int vk)
    {
        ExpireStaleState();

        if (!IsModifier(vk))
        {
            return;
        }

        _pressedModifiers.Remove(vk);

        if (_pressedModifiers.Count != 0 || !_chordActive)
        {
            return;
        }

        // All modifiers released — decide the outcome of this chord.
        var recognized = _bothSeen && !_cancelled;
        _chordActive = false;
        _bothSeen = false;
        _cancelled = false;

        if (recognized)
        {
            GestureRecognized?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Discard tracked state that is older than <see cref="StaleChordTimeoutMs"/>.
    /// Key-ups lost to the secure desktop (UAC) or session switches would
    /// otherwise leave modifiers virtually held indefinitely.
    /// </summary>
    private void ExpireStaleState()
    {
        var now = _ticks();
        if (_pressedModifiers.Count > 0 && now - _lastEventTicks > StaleChordTimeoutMs)
        {
            Reset();
        }

        _lastEventTicks = now;
    }

    /// <summary>
    /// Reset all tracked state. Called on focus loss / session change so that
    /// modifiers cannot get "stuck" virtually held.
    /// </summary>
    public void Reset()
    {
        _pressedModifiers.Clear();
        _chordActive = false;
        _bothSeen = false;
        _cancelled = false;
    }

    private bool AnyCtrlHeld()
    {
        foreach (var vk in _pressedModifiers)
        {
            if (IsCtrl(vk))
            {
                return true;
            }
        }

        return false;
    }

    private bool AnyShiftHeld()
    {
        foreach (var vk in _pressedModifiers)
        {
            if (IsShift(vk))
            {
                return true;
            }
        }

        return false;
    }
}
