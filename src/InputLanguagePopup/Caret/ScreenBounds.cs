using System.Drawing;
using static InputLanguagePopup.Interop.NativeMethods;

namespace InputLanguagePopup.Caret;

internal static class ScreenBounds
{
    /// <summary>
    /// A caret rectangle is only trusted if it actually lies on some monitor
    /// (spec 6.4: coordinates far outside the virtual screen are invalid).
    /// </summary>
    public static bool IsOnScreen(Rectangle bounds)
    {
        var bottomRight = new POINT(bounds.Right, bounds.Bottom);
        if (MonitorFromPoint(bottomRight, MONITOR_DEFAULTTONULL) != System.IntPtr.Zero)
        {
            return true;
        }

        var topLeft = new POINT(bounds.Left, bounds.Top);
        return MonitorFromPoint(topLeft, MONITOR_DEFAULTTONULL) != System.IntPtr.Zero;
    }
}
