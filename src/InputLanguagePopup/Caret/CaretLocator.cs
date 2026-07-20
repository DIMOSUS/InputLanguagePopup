using System;
using System.Drawing;
using InputLanguagePopup.Diagnostics;
using InputLanguagePopup.Settings;
using static InputLanguagePopup.Interop.NativeMethods;

namespace InputLanguagePopup.Caret;

/// <summary>
/// Cascades the caret-location strategies: system caret → MSAA → UI Automation →
/// cursor fallback. Returns the screen position and the source that produced it.
/// </summary>
public sealed class CaretLocator : IDisposable
{
    private readonly Logger _logger;
    private readonly Win32CaretLocator _win32;
    private readonly MsaaCaretLocator _msaa;
    private readonly UiAutomationCaretLocator _uiAutomation;
    private readonly CursorFallbackLocator _cursor;
    private readonly AppSettings _settings;

    public CaretLocator(Logger logger, AppSettings settings)
    {
        _logger = logger;
        _settings = settings;
        _win32 = new Win32CaretLocator(logger);
        _msaa = new MsaaCaretLocator(logger);
        _uiAutomation = new UiAutomationCaretLocator(logger);
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
        // skip the Win32 strategy for them and rely on MSAA / UI Automation.
        if (!IsUnreliableWin32Caret(className))
        {
            var win32 = _win32.TryLocate(foregroundWindow, threadId);
            if (TryAccept(win32, "Win32", out var accepted))
            {
                return accepted;
            }
        }

        var msaa = _msaa.TryLocate(foregroundWindow, threadId);
        if (TryAccept(msaa, "MSAA", out var msaaAccepted))
        {
            return msaaAccepted;
        }

        if (_settings.UseUiAutomation)
        {
            var uia = _uiAutomation.TryLocate(foregroundWindow, threadId);
            if (TryAccept(uia, "UI Automation", out var uiaAccepted))
            {
                return uiaAccepted;
            }
        }

        var cursor = _cursor.TryLocate(foregroundWindow, threadId);
        if (!cursor.IsValid)
        {
            _logger.Warn("All caret strategies failed, including cursor fallback.");
        }

        return cursor;
    }

    private bool TryAccept(CaretResult result, string strategy, out CaretResult accepted)
    {
        accepted = result;
        if (!result.IsValid)
        {
            return false;
        }

        if (IsOnScreen(result.Bounds))
        {
            return true;
        }

        _logger.Warn($"{strategy} caret rectangle {result.Bounds} is outside the virtual screen; trying next strategy.");
        return false;
    }

    /// <summary>
    /// True for window classes whose system (GetGUIThreadInfo) caret is known to be
    /// unreliable — currently Qt windows (Telegram Desktop and similar).
    /// </summary>
    private static bool IsUnreliableWin32Caret(string className)
        => className.StartsWith("Qt", StringComparison.Ordinal) &&
           className.Contains("QWindowIcon", StringComparison.Ordinal);

    /// <summary>
    /// A caret rectangle is only trusted if it actually lies on some monitor
    /// (spec 6.4: coordinates far outside the virtual screen are invalid).
    /// </summary>
    private static bool IsOnScreen(Rectangle bounds)
    {
        var bottomRight = new POINT(bounds.Right, bounds.Bottom);
        if (MonitorFromPoint(bottomRight, MONITOR_DEFAULTTONULL) != IntPtr.Zero)
        {
            return true;
        }

        var topLeft = new POINT(bounds.Left, bounds.Top);
        return MonitorFromPoint(topLeft, MONITOR_DEFAULTTONULL) != IntPtr.Zero;
    }

    public void Dispose()
    {
        _uiAutomation.Dispose();
    }
}
