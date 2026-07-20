using System;
using System.Drawing;
using InputLanguagePopup.Diagnostics;
using InputLanguagePopup.Settings;
using static InputLanguagePopup.Interop.NativeMethods;

namespace InputLanguagePopup.Caret;

/// <summary>
/// Cascades the caret-location strategies: system caret → UI Automation →
/// cursor fallback. Returns the screen position and the source that produced it.
/// </summary>
public sealed class CaretLocator : IDisposable
{
    private readonly Logger _logger;
    private readonly Win32CaretLocator _win32;
    private readonly UiAutomationCaretLocator _uiAutomation;
    private readonly CursorFallbackLocator _cursor;
    private readonly AppSettings _settings;

    public CaretLocator(Logger logger, AppSettings settings)
    {
        _logger = logger;
        _settings = settings;
        _win32 = new Win32CaretLocator(logger);
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

        var result = _win32.TryLocate(foregroundWindow, threadId);
        if (result.IsValid)
        {
            if (IsOnScreen(result.Bounds))
            {
                return result;
            }

            _logger.Warn($"Win32 caret rectangle {result.Bounds} is outside the virtual screen; trying next strategy.");
        }

        if (_settings.UseUiAutomation)
        {
            result = _uiAutomation.TryLocate(foregroundWindow, threadId);
            if (result.IsValid)
            {
                if (IsOnScreen(result.Bounds))
                {
                    return result;
                }

                _logger.Warn($"UI Automation caret rectangle {result.Bounds} is outside the virtual screen; trying next strategy.");
            }
        }

        result = _cursor.TryLocate(foregroundWindow, threadId);
        if (!result.IsValid)
        {
            _logger.Warn("All caret strategies failed, including cursor fallback.");
        }

        return result;
    }

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
