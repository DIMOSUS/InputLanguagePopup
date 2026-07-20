using System;
using System.Drawing;
using System.Runtime.InteropServices;
using InputLanguagePopup.Caret;
using InputLanguagePopup.Settings;
using static InputLanguagePopup.Interop.NativeMethods;

namespace InputLanguagePopup.Positioning;

/// <summary>Final physical-pixel placement for the popup on a specific monitor.</summary>
public readonly record struct PopupPlacement(Point Location, Size Size, double DpiScale);

/// <summary>Popup offsets (logical px) for the caret and cursor anchors.</summary>
public readonly record struct PopupOffsets(int CaretX, int CaretY, int CursorX, int CursorY);

/// <summary>
/// Turns a <see cref="CaretResult"/> into an on-screen physical-pixel rectangle
/// for the popup, honouring per-monitor DPI, configured offsets and the work
/// area of the monitor the anchor sits on (handles negative coordinates for
/// monitors left of / above the primary).
///
/// The geometry (<see cref="ComputeLocation"/>) is a pure function so it can be
/// unit tested without Win32; <see cref="Compute"/> only supplies the monitor
/// metrics around it.
/// </summary>
public sealed class PopupPositionService
{
    private readonly AppSettings _settings;

    public PopupPositionService(AppSettings settings)
    {
        _settings = settings;
    }

    private PopupOffsets Offsets => new(
        _settings.CaretOffsetX, _settings.CaretOffsetY,
        _settings.CursorOffsetX, _settings.CursorOffsetY);

    /// <summary>
    /// Compute placement for the given caret result. <paramref name="logicalSize"/>
    /// is the popup size at 96 DPI; the returned size is scaled to the target
    /// monitor's DPI. <paramref name="foregroundWindow"/> is used to obtain the DPI
    /// for caret sources via <c>GetDpiForWindow</c> (the API recommended for
    /// Per-Monitor-V2 processes); pass <see cref="IntPtr.Zero"/> when unknown.
    /// </summary>
    public PopupPlacement Compute(CaretResult caret, Size logicalSize, IntPtr foregroundWindow)
    {
        var reference = ReferencePoint(caret);
        var hMonitor = MonitorFromPoint(reference, MONITOR_DEFAULTTONEAREST);

        var workArea = GetWorkArea(hMonitor);
        var dpiScale = GetDpiScale(caret, foregroundWindow, hMonitor);

        var size = new Size(
            Math.Max(1, (int)Math.Round(logicalSize.Width * dpiScale)),
            Math.Max(1, (int)Math.Round(logicalSize.Height * dpiScale)));

        var location = ComputeLocation(caret, size, dpiScale, workArea, Offsets);
        return new PopupPlacement(location, size, dpiScale);
    }

    /// <summary>
    /// Pure placement geometry: given the anchor, the already-scaled popup size, the
    /// DPI scale, the target monitor work area and the offsets, return the popup's
    /// top-left in physical pixels. No Win32 — unit testable.
    /// </summary>
    internal static Point ComputeLocation(
        CaretResult caret, Size size, double dpiScale, Rectangle work, PopupOffsets offsets)
    {
        if (caret.Source == CaretSource.CursorFallback)
        {
            var offX = (int)Math.Round(offsets.CursorX * dpiScale);
            var offY = (int)Math.Round(offsets.CursorY * dpiScale);
            var cursor = new Point(caret.Bounds.X, caret.Bounds.Y);

            var location = ClampToWorkArea(new Point(cursor.X + offX, cursor.Y + offY), size, work);

            // Never cover the pointer itself: near the bottom/right screen edge the
            // clamp can drag the popup onto the cursor — mirror it up-left instead.
            if (new Rectangle(location, size).Contains(cursor))
            {
                location = ClampToWorkArea(
                    new Point(cursor.X - offX - size.Width, cursor.Y - offY - size.Height), size, work);
            }

            return location;
        }

        var cx = (int)Math.Round(offsets.CaretX * dpiScale);
        var cy = (int)Math.Round(offsets.CaretY * dpiScale);

        var x = caret.Bounds.Right + cx;
        var y = caret.Bounds.Bottom + cy;

        // If the popup would fall off the bottom, place it above the caret.
        if (y + size.Height > work.Bottom)
        {
            var above = caret.Bounds.Top - cy - size.Height;
            if (above >= work.Top)
            {
                y = above;
            }
        }

        // If it would fall off the right edge, place it to the left of the caret.
        if (x + size.Width > work.Right)
        {
            var left = caret.Bounds.Left - cx - size.Width;
            if (left >= work.Left)
            {
                x = left;
            }
        }

        return ClampToWorkArea(new Point(x, y), size, work);
    }

    private static Point ClampToWorkArea(Point location, Size size, Rectangle work)
    {
        var maxX = Math.Max(work.Left, work.Right - size.Width);
        var maxY = Math.Max(work.Top, work.Bottom - size.Height);

        var x = Math.Clamp(location.X, work.Left, maxX);
        var y = Math.Clamp(location.Y, work.Top, maxY);
        return new Point(x, y);
    }

    private static POINT ReferencePoint(CaretResult caret)
    {
        if (caret.Source == CaretSource.CursorFallback)
        {
            return new POINT(caret.Bounds.X, caret.Bounds.Y);
        }

        // rcCaret Right/Bottom are exclusive; step one pixel inside so a caret on the
        // last column/row of a monitor does not select the neighbouring monitor.
        var b = caret.Bounds;
        return new POINT(Math.Max(b.Left, b.Right - 1), Math.Max(b.Top, b.Bottom - 1));
    }

    private static Rectangle GetWorkArea(IntPtr hMonitor)
    {
        if (hMonitor != IntPtr.Zero)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                return Rectangle.FromLTRB(
                    mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom);
            }
        }

        return new Rectangle(0, 0, 1920, 1080); // reasonable fallback
    }

    private static double GetDpiScale(CaretResult caret, IntPtr foregroundWindow, IntPtr hMonitor)
    {
        // For caret sources prefer GetDpiForWindow on the foreground window — the
        // API Microsoft recommends for Per-Monitor-V2 processes (GetDpiForMonitor is
        // documented as not DPI-aware). Fall back to the monitor DPI otherwise.
        if (caret.Source != CaretSource.CursorFallback && foregroundWindow != IntPtr.Zero)
        {
            var dpi = GetDpiForWindow(foregroundWindow);
            if (dpi > 0)
            {
                return dpi / 96.0;
            }
        }

        if (hMonitor != IntPtr.Zero &&
            GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 &&
            dpiX > 0)
        {
            return dpiX / 96.0;
        }

        return 1.0;
    }
}
