using System;
using System.Drawing;
using System.Runtime.InteropServices;
using InputLanguagePopup.Diagnostics;
using InputLanguagePopup.Interop;
using static InputLanguagePopup.Interop.NativeMethods;

namespace InputLanguagePopup.Caret;

/// <summary>
/// Strategy 1b: the caret via MSAA (oleacc) <c>OBJID_CARET</c> +
/// <c>IAccessible::accLocation</c>. Lightweight and covers many apps where the
/// system caret (GetGUIThreadInfo) is absent but a full UI Automation query would
/// be heavier — e.g. Chromium/Electron editors. Runs between the Win32 caret and
/// the UI Automation strategies.
/// </summary>
public sealed class MsaaCaretLocator : ICaretPositionSource
{
    private readonly Logger _logger;

    public MsaaCaretLocator(Logger logger)
    {
        _logger = logger;
    }

    public CaretResult TryLocate(IntPtr foregroundWindow, uint threadId)
    {
        if (foregroundWindow == IntPtr.Zero)
        {
            return CaretResult.NotFound;
        }

        // OBJID_CARET is bound to the window that actually owns the caret, not the
        // top-level foreground window — query the caret/focus window of the thread.
        var target = ResolveCaretWindow(foregroundWindow, threadId);

        var caret = IntPtr.Zero;
        try
        {
            caret = Msaa.GetCaretObject(target);
            if (caret == IntPtr.Zero)
            {
                return CaretResult.NotFound;
            }

            // S_FALSE (1) means "no caret location", so require S_OK exactly.
            if (Msaa.AccLocation(caret, out var x, out var y, out var w, out var h) != 0)
            {
                return CaretResult.NotFound;
            }

            // A zero-width result is a valid vertical caret; force a thin bar. A
            // rect at the origin usually means "no caret".
            if (w < 1)
            {
                w = 0;
            }

            if (h <= 0 || (x == 0 && y == 0))
            {
                return CaretResult.NotFound;
            }

            return new CaretResult(CaretSource.Msaa, new Rectangle(x, y, w, h));
        }
        catch (COMException ex)
        {
            _logger.Warn($"MSAA caret lookup failed: 0x{ex.HResult:X8} {ex.Message}");
            return CaretResult.NotFound;
        }
        catch (Exception ex)
        {
            _logger.Warn($"MSAA caret lookup threw: {ex.Message}");
            return CaretResult.NotFound;
        }
        finally
        {
            Com.Release(caret);
        }
    }

    private static IntPtr ResolveCaretWindow(IntPtr foregroundWindow, uint threadId)
    {
        if (threadId == 0)
        {
            return foregroundWindow;
        }

        var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (GetGUIThreadInfo(threadId, ref gti))
        {
            if (gti.hwndCaret != IntPtr.Zero)
            {
                return gti.hwndCaret;
            }

            if (gti.hwndFocus != IntPtr.Zero)
            {
                return gti.hwndFocus;
            }
        }

        return foregroundWindow;
    }
}

