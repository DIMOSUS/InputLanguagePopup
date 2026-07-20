namespace InputLanguagePopup.Hooking;

/// <summary>Which layout-switch keyboard gesture completed.</summary>
public enum LayoutGesture
{
    CtrlShift,
    AltShift,
    WinSpace,
}

/// <summary>
/// Pure, Win32-independent state machine that recognises the keyboard gestures
/// Windows uses for layout switching:
///
///   * a "clean" Ctrl+Shift chord,
///   * a "clean" Alt+Shift chord,
///   * Win+Space (optionally with Shift for reverse cycling).
///
/// A modifier chord fires only when exactly its two modifier kinds were involved
/// (Ctrl+Alt+Shift fires nothing), both were held simultaneously at some point,
/// no non-modifier key was pressed while the chord was held, and all modifiers
/// have been released. Win+Space fires once per Win hold, when Win is released.
///
/// Which of the recognised gestures should actually show the indicator is decided
/// by the caller (against the system hotkey configuration) — this class only
/// reports what happened. Key auto-repeat produces no extra events. No UI or
/// Win32 dependencies, so it is unit-testable with plain virtual-key codes.
/// </summary>
public sealed class LayoutHotkeyGestureDetector
{
    /// <summary>
    /// If no key event arrives for this long while modifier state is tracked, the
    /// state is considered stale and discarded. This covers key-ups that were
    /// never delivered — e.g. a UAC prompt (secure desktop) appearing mid-chord —
    /// so modifiers cannot remain virtually held forever. Side effect: a chord
    /// deliberately held longer than this fires no event, which is acceptable.
    /// </summary>
    public const int StaleChordTimeoutMs = 10_000;

    private const int VK_SPACE = 0x20;

    [Flags]
    private enum Mods
    {
        None = 0,
        Ctrl = 1,
        Shift = 2,
        Alt = 4,
        Win = 8,
    }

    private readonly HashSet<int> _pressedKeys = new();          // held modifier keys
    private readonly HashSet<int> _pressedNonModifiers = new();  // held ordinary keys
    private readonly Func<long> _ticks;
    private long _lastEventTicks;

    private bool _chordActive;        // a chord is currently being built
    private bool _cancelled;          // a foreign non-modifier key broke the chord
    private bool _spaceSeen;          // Space was pressed while Win (± Shift) was held
    private Mods _union;              // all modifier kinds seen during the chord
    private bool _ctrlShiftTogether;  // Ctrl and Shift were held at the same time
    private bool _altShiftTogether;   // Alt and Shift were held at the same time

    /// <summary>Raised when a clean layout-switch gesture completes.</summary>
    public event Action<LayoutGesture>? GestureRecognized;

    /// <param name="tickProvider">
    /// Millisecond tick source; defaults to <see cref="Environment.TickCount64"/>.
    /// Injectable so staleness can be unit tested.
    /// </param>
    public LayoutHotkeyGestureDetector(Func<long>? tickProvider = null)
    {
        _ticks = tickProvider ?? (static () => Environment.TickCount64);
    }

    /// <summary>Classify a virtual key as a tracked modifier kind, or null.</summary>
    private static Mods? KindOf(int vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => Mods.Shift, // VK_SHIFT, VK_LSHIFT, VK_RSHIFT
        0x11 or 0xA2 or 0xA3 => Mods.Ctrl,  // VK_CONTROL, VK_LCONTROL, VK_RCONTROL
        0x12 or 0xA4 or 0xA5 => Mods.Alt,   // VK_MENU, VK_LMENU, VK_RMENU
        0x5B or 0x5C => Mods.Win,           // VK_LWIN, VK_RWIN
        _ => null,
    };

    /// <summary>Feed a key-down event (from the low-level hook or a test).</summary>
    public void OnKeyDown(int vk)
    {
        ExpireStaleState();

        var kind = KindOf(vk);
        if (kind is not null)
        {
            if (_pressedKeys.Count == 0)
            {
                // First modifier of a fresh chord. A non-modifier key that was
                // already physically held before the chord began (e.g. "S down,
                // then Ctrl+Shift") means this is not a clean chord.
                _chordActive = true;
                _cancelled = _pressedNonModifiers.Count != 0;
                _spaceSeen = false;
                _union = Mods.None;
                _ctrlShiftTogether = false;
                _altShiftTogether = false;
            }

            // HashSet.Add is a no-op for auto-repeat of an already-held key.
            _pressedKeys.Add(vk);
            _union |= kind.Value;

            var held = HeldKinds();
            if ((held & (Mods.Ctrl | Mods.Shift)) == (Mods.Ctrl | Mods.Shift))
            {
                _ctrlShiftTogether = true;
            }

            if ((held & (Mods.Alt | Mods.Shift)) == (Mods.Alt | Mods.Shift))
            {
                _altShiftTogether = true;
            }

            return;
        }

        // Track ordinary keys so a chord starting while one is held is rejected.
        _pressedNonModifiers.Add(vk);

        if (!_chordActive)
        {
            return;
        }

        // Space while Win is held (optionally with Shift — reverse cycling) is
        // part of the Win+Space gesture; any other non-modifier key breaks the
        // chord (Ctrl+Shift+S, Win+E, ...). Repeated Space presses within one
        // Win hold collapse into a single gesture.
        var heldNow = HeldKinds();
        if (vk == VK_SPACE &&
            (heldNow & Mods.Win) != 0 &&
            (heldNow & (Mods.Ctrl | Mods.Alt)) == 0)
        {
            _spaceSeen = true;
        }
        else
        {
            _cancelled = true;
        }
    }

    /// <summary>Feed a key-up event (from the low-level hook or a test).</summary>
    public void OnKeyUp(int vk)
    {
        ExpireStaleState();

        if (KindOf(vk) is null)
        {
            _pressedNonModifiers.Remove(vk);
            return;
        }

        _pressedKeys.Remove(vk);

        if (_pressedKeys.Count != 0 || !_chordActive)
        {
            return;
        }

        // All modifiers released — evaluate the outcome of this chord.
        var union = _union;
        var cancelled = _cancelled;
        var spaceSeen = _spaceSeen;
        var ctrlShift = _ctrlShiftTogether;
        var altShift = _altShiftTogether;

        _chordActive = false;
        _cancelled = false;
        _spaceSeen = false;
        _union = Mods.None;
        _ctrlShiftTogether = false;
        _altShiftTogether = false;

        if (cancelled)
        {
            return;
        }

        if (spaceSeen)
        {
            // Win+Space or Win+Shift+Space; Ctrl/Alt joining at any point voids it.
            if ((union & Mods.Win) != 0 && (union & (Mods.Ctrl | Mods.Alt)) == 0)
            {
                GestureRecognized?.Invoke(LayoutGesture.WinSpace);
            }

            return;
        }

        // Exactly the two chord modifiers — no extras (Ctrl+Alt+Shift is not a
        // layout chord, and AltGr arrives as LCtrl+RAlt, i.e. Ctrl|Alt).
        if (union == (Mods.Ctrl | Mods.Shift) && ctrlShift)
        {
            GestureRecognized?.Invoke(LayoutGesture.CtrlShift);
        }
        else if (union == (Mods.Alt | Mods.Shift) && altShift)
        {
            GestureRecognized?.Invoke(LayoutGesture.AltShift);
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
        if (_pressedKeys.Count > 0 && now - _lastEventTicks > StaleChordTimeoutMs)
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
        _pressedKeys.Clear();
        _pressedNonModifiers.Clear();
        _chordActive = false;
        _cancelled = false;
        _spaceSeen = false;
        _union = Mods.None;
        _ctrlShiftTogether = false;
        _altShiftTogether = false;
    }

    private Mods HeldKinds()
    {
        var held = Mods.None;
        foreach (var vk in _pressedKeys)
        {
            if (KindOf(vk) is { } kind)
            {
                held |= kind;
            }
        }

        return held;
    }
}
