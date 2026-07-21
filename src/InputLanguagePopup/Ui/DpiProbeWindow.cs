using System.Drawing;
using static InputLanguagePopup.Interop.NativeMethods;
using static InputLanguagePopup.Interop.Win32Ui;

namespace InputLanguagePopup.Ui;

/// <summary>
/// A tiny, never-shown Per-Monitor-V2 helper window used solely to read a monitor's
/// effective DPI. The window is moved (never made visible) onto the target monitor,
/// then <c>GetDpiForWindow</c> on our own handle returns that monitor's true DPI —
/// correct regardless of the foreground app's DPI awareness, and without ever
/// touching the visible popup (moving that during a second layout check would
/// relocate it away from its computed position).
/// </summary>
public sealed class DpiProbeWindow : Win32Window
{
    public DpiProbeWindow()
        : base("DpiProbe", WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, WS_POPUP, 1, 1)
    {
    }

    /// <summary>DPI scale (1.0 = 96 DPI) of the monitor containing <paramref name="anchor"/>.</summary>
    public double GetScaleForPoint(Point anchor)
    {
        SetWindowPos(Handle, System.IntPtr.Zero, anchor.X, anchor.Y, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        var dpi = GetDpiForWindow(Handle);
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }
}
