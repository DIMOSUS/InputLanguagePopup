using System;
using System.Drawing;
using System.Runtime.InteropServices;
using InputLanguagePopup.Diagnostics;
using InputLanguagePopup.Interop;
using static InputLanguagePopup.Interop.NativeMethods;

namespace InputLanguagePopup.Caret;

/// <summary>
/// Strategy 1: the system caret via GetGUIThreadInfo. Fast and works in Notepad,
/// classic Win32 edit controls, Explorer, etc.
/// </summary>
public sealed class Win32CaretLocator : ICaretPositionSource
{
    private readonly Logger _logger;

    public Win32CaretLocator(Logger logger)
    {
        _logger = logger;
    }

    public CaretResult TryLocate(IntPtr foregroundWindow, uint threadId)
    {
        if (threadId == 0)
        {
            return CaretResult.NotFound;
        }

        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(threadId, ref info))
        {
            var err = Marshal.GetLastWin32Error();
            _logger.Warn($"GetGUIThreadInfo failed for thread {threadId}. Win32 error {err}.");
            return CaretResult.NotFound;
        }

        if (info.hwndCaret == IntPtr.Zero)
        {
            return CaretResult.NotFound;
        }

        var rc = info.rcCaret;
        var width = rc.Width;
        var height = rc.Height;

        // A zero-width caret is normal (blinking bar). Treat it as a thin vertical
        // caret. A zero-height rectangle, however, is not usable.
        if (height <= 0)
        {
            return CaretResult.NotFound;
        }

        if (width < 0)
        {
            return CaretResult.NotFound;
        }

        // rcCaret is in client coordinates of hwndCaret — convert both corners.
        var topLeft = new POINT(rc.Left, rc.Top);
        var bottomRight = new POINT(rc.Right, rc.Bottom);
        if (!ClientToScreen(info.hwndCaret, ref topLeft) ||
            !ClientToScreen(info.hwndCaret, ref bottomRight))
        {
            _logger.Warn("ClientToScreen failed for caret rectangle.");
            return CaretResult.NotFound;
        }

        var bounds = Rectangle.FromLTRB(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
        if (bounds.Width < 0)
        {
            bounds = new Rectangle(bounds.X, bounds.Y, 0, bounds.Height);
        }

        return new CaretResult(CaretSource.Win32Caret, bounds);
    }
}
