using System.Drawing;
using InputLanguagePopup.Caret;
using InputLanguagePopup.Settings;
using static InputLanguagePopup.Interop.NativeMethods;

namespace InputLanguagePopup.Positioning;

/// <summary>Final physical-pixel placement for the popup on a specific monitor.</summary>
public readonly record struct PopupPlacement(Point Location, Size Size, double DpiScale);

/// <summary>
/// Turns a <see cref="CaretResult"/> into an on-screen physical-pixel rectangle
/// for the popup, honouring per-monitor DPI, configured offsets and the work
/// area of the monitor the anchor sits on (handles negative coordinates for
/// monitors left of / above the primary).
/// </summary>
public sealed class PopupPositionService
{
    private readonly AppSettings _settings;

    public PopupPositionService(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Compute placement for the given caret result. <paramref name="logicalSize"/>
    /// is the popup size at 96 DPI; the returned size is scaled to the target
    /// monitor's DPI.
    /// </summary>
    public PopupPlacement Compute(CaretResult caret, Size logicalSize)
    {
        // Reference point used to pick the monitor.
        var reference = caret.Source == CaretSource.CursorFallback
            ? new POINT(caret.Bounds.X, caret.Bounds.Y)
            : new POINT(caret.Bounds.Right, caret.Bounds.Bottom);

        var (workArea, dpiScale) = GetMonitorMetrics(reference);

        var size = new Size(
            Math.Max(1, (int)Math.Round(logicalSize.Width * dpiScale)),
            Math.Max(1, (int)Math.Round(logicalSize.Height * dpiScale)));

        Point location;
        if (caret.Source == CaretSource.CursorFallback)
        {
            location = ComputeForCursor(caret.Bounds, size, dpiScale);
            location = ClampToWorkArea(location, size, workArea);

            // Never cover the pointer itself: near the bottom/right screen edge
            // the clamp above can drag the popup onto the cursor — mirror it to
            // the upper-left of the pointer in that case.
            var cursor = new Point(caret.Bounds.X, caret.Bounds.Y);
            if (new Rectangle(location, size).Contains(cursor))
            {
                var offX = (int)Math.Round(_settings.CursorOffsetX * dpiScale);
                var offY = (int)Math.Round(_settings.CursorOffsetY * dpiScale);
                location = new Point(cursor.X - offX - size.Width, cursor.Y - offY - size.Height);
                location = ClampToWorkArea(location, size, workArea);
            }
        }
        else
        {
            location = ComputeForCaret(caret.Bounds, size, dpiScale, workArea);
            location = ClampToWorkArea(location, size, workArea);
        }

        return new PopupPlacement(location, size, dpiScale);
    }

    private Point ComputeForCaret(Rectangle caret, Size size, double dpiScale, Rectangle work)
    {
        var offX = (int)Math.Round(_settings.CaretOffsetX * dpiScale);
        var offY = (int)Math.Round(_settings.CaretOffsetY * dpiScale);

        var x = caret.Right + offX;
        var y = caret.Bottom + offY;

        // If the popup would fall off the bottom, place it above the caret.
        if (y + size.Height > work.Bottom)
        {
            var above = caret.Top - offY - size.Height;
            if (above >= work.Top)
            {
                y = above;
            }
        }

        // If it would fall off the right edge, place it to the left of the caret.
        if (x + size.Width > work.Right)
        {
            var left = caret.Left - offX - size.Width;
            if (left >= work.Left)
            {
                x = left;
            }
        }

        return new Point(x, y);
    }

    private Point ComputeForCursor(Rectangle cursor, Size size, double dpiScale)
    {
        var offX = (int)Math.Round(_settings.CursorOffsetX * dpiScale);
        var offY = (int)Math.Round(_settings.CursorOffsetY * dpiScale);
        // Offset down-right so the popup does not sit under the pointer itself.
        return new Point(cursor.X + offX, cursor.Y + offY);
    }

    private static Point ClampToWorkArea(Point location, Size size, Rectangle work)
    {
        var maxX = Math.Max(work.Left, work.Right - size.Width);
        var maxY = Math.Max(work.Top, work.Bottom - size.Height);

        var x = Math.Clamp(location.X, work.Left, maxX);
        var y = Math.Clamp(location.Y, work.Top, maxY);
        return new Point(x, y);
    }

    private static (Rectangle WorkArea, double DpiScale) GetMonitorMetrics(POINT reference)
    {
        var hMonitor = MonitorFromPoint(reference, MONITOR_DEFAULTTONEAREST);

        var work = new Rectangle(0, 0, 1920, 1080); // reasonable fallback
        var scale = 1.0;

        if (hMonitor != IntPtr.Zero)
        {
            var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                work = Rectangle.FromLTRB(
                    mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom);
            }

            if (GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
            {
                scale = dpiX / 96.0;
            }
        }

        return (work, scale);
    }
}
