using System;
using System.Drawing;
using System.Windows.Forms;
using static InputLanguagePopup.Interop.NativeMethods;

namespace InputLanguagePopup.Ui;

/// <summary>
/// A tiny, never-shown Per-Monitor-V2 helper window used solely to read a monitor's
/// effective DPI. The window is moved (never made visible) onto the target monitor,
/// then <c>GetDpiForWindow</c> on our own handle returns that monitor's true DPI —
/// correct regardless of the foreground app's DPI awareness, and without ever
/// touching the visible popup (moving that during a second layout check would
/// relocate it away from its computed position).
/// </summary>
public sealed class DpiProbeWindow : IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);

    private readonly NativeWindow _window;

    public DpiProbeWindow()
    {
        _window = new NativeWindow();
        var cp = new CreateParams
        {
            // A real (not message-only) top-level window so it has a monitor
            // association, but with no visible style — it is never shown.
            Style = WS_POPUP,
            ExStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            X = 0,
            Y = 0,
            Width = 1,
            Height = 1,
        };
        _window.CreateHandle(cp);
    }

    /// <summary>DPI scale (1.0 = 96 DPI) of the monitor containing <paramref name="anchor"/>.</summary>
    public double GetScaleForPoint(Point anchor)
    {
        SetWindowPos(_window.Handle, IntPtr.Zero, anchor.X, anchor.Y, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        var dpi = GetDpiForWindow(_window.Handle);
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    public void Dispose()
    {
        if (_window.Handle != IntPtr.Zero)
        {
            _window.DestroyHandle();
        }
    }
}
