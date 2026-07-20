using System;
using InputLanguagePopup.Diagnostics;
using InputLanguagePopup.Settings;
using static InputLanguagePopup.Interop.NativeMethods;

namespace InputLanguagePopup.Caret;

/// <summary>
/// Cascades the caret-location strategies: system caret (fast, synchronous) →
/// accessibility (MSAA + UI Automation, on a single bounded worker) → cursor
/// fallback. Returns the screen position and the source that produced it.
/// </summary>
public sealed class CaretLocator : IDisposable
{
    private readonly Logger _logger;
    private readonly Win32CaretLocator _win32;
    private readonly AccessibilityCaretLocator _accessibility;
    private readonly CursorFallbackLocator _cursor;
    private readonly AppSettings _settings;

    public CaretLocator(Logger logger, AppSettings settings)
    {
        _logger = logger;
        _settings = settings;
        _win32 = new Win32CaretLocator(logger);
        _accessibility = new AccessibilityCaretLocator(logger);
        _cursor = new CursorFallbackLocator();
    }

    /// <summary>
    /// Resolve the caret / anchor position for the given foreground window. Always
    /// returns a valid result (falls back to the cursor).
    /// </summary>
    public CaretResult Locate(IntPtr foregroundWindow)
    {
        var threadId = foregroundWindow == IntPtr.Zero
            ? 0u
            : GetWindowThreadProcessId(foregroundWindow, out _);

        var className = foregroundWindow == IntPtr.Zero ? string.Empty : GetClassName(foregroundWindow);

        // Some toolkits report a bogus system caret. Telegram / other Qt windows
        // (class like "Qt51515QWindowIcon") give a wrong GetGUIThreadInfo caret, so
        // skip the Win32 strategy for them and rely on the accessibility chain.
        if (!IsUnreliableWin32Caret(className))
        {
            var win32 = _win32.TryLocate(foregroundWindow, threadId);
            if (win32.IsValid)
            {
                if (ScreenBounds.IsOnScreen(win32.Bounds))
                {
                    return win32;
                }

                _logger.Warn($"Win32 caret rectangle {win32.Bounds} is outside the virtual screen; trying next strategy.");
            }
        }

        // MSAA + UI Automation share one bounded worker; the on-screen check is
        // applied inside the chain.
        var accessibility = _accessibility.TryLocate(foregroundWindow, threadId, _settings.UseUiAutomation);
        if (accessibility.IsValid)
        {
            return accessibility;
        }

        var cursor = _cursor.TryLocate(foregroundWindow, threadId);
        if (!cursor.IsValid)
        {
            _logger.Warn("All caret strategies failed, including cursor fallback.");
        }

        return cursor;
    }

    /// <summary>
    /// True for window classes whose system (GetGUIThreadInfo) caret is known to be
    /// unreliable — currently Qt windows (Telegram Desktop and similar).
    /// </summary>
    private static bool IsUnreliableWin32Caret(string className)
        => className.StartsWith("Qt", StringComparison.Ordinal) &&
           className.Contains("QWindowIcon", StringComparison.Ordinal);

    public void Dispose()
    {
        _accessibility.Dispose();
    }
}
