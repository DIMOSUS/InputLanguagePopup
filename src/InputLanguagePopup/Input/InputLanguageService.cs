using System;
using System.Globalization;
using System.Runtime.InteropServices;
using InputLanguagePopup.Diagnostics;
using InputLanguagePopup.Interop;
using static InputLanguagePopup.Interop.NativeMethods;

namespace InputLanguagePopup.Input;

/// <summary>
/// Determines the keyboard layout actually active in the foreground window's
/// thread and converts it to a short display code (RU / EN / LT / ISO code).
/// Never switches or loads layouts.
/// </summary>
public sealed class InputLanguageService
{
    private readonly Logger _logger;

    // Primary-language overrides for the codes explicitly required by the spec.
    // Keyed by the primary language id (low 10 bits of the LANGID).
    private const int LANG_ENGLISH = 0x09;
    private const int LANG_RUSSIAN = 0x19;
    private const int LANG_LITHUANIAN = 0x27;

    public InputLanguageService(Logger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns the HKL of the foreground thread, or <see cref="IntPtr.Zero"/> if
    /// the foreground window / thread could not be resolved.
    /// </summary>
    public IntPtr GetForegroundLayout(out IntPtr foregroundWindow)
    {
        foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            _logger.Warn("GetForegroundWindow returned NULL.");
            return IntPtr.Zero;
        }

        // Consoles and UWP/host windows do not carry the layout on the foreground
        // window's own thread; resolve the window that actually owns the input.
        var layoutWindow = ResolveLayoutWindow(foregroundWindow);

        var threadId = GetWindowThreadProcessId(layoutWindow, out _);
        if (threadId == 0)
        {
            _logger.Warn("GetWindowThreadProcessId returned 0.");
            return IntPtr.Zero;
        }

        // Must pass the foreground thread id — GetKeyboardLayout(0) would return
        // our own thread's layout instead of the target application's.
        return GetKeyboardLayout(threadId);
    }

    /// <summary>
    /// Pick the window whose thread actually owns the keyboard layout: for consoles
    /// (CMD/PowerShell) the default IME window; for UWP hosts / Steam popups the
    /// focused child control; otherwise the foreground window itself.
    /// </summary>
    private IntPtr ResolveLayoutWindow(IntPtr foregroundWindow)
    {
        var className = GetClassName(foregroundWindow);

        if (className == "ConsoleWindowClass")
        {
            var ime = ImmGetDefaultIMEWnd(foregroundWindow);
            if (ime != IntPtr.Zero)
            {
                return ime;
            }
        }
        else if (className is "ApplicationFrameWindow" or "vguiPopupWindow")
        {
            // The real input lives in a focused child; GUITHREADINFO.hwndFocus of the
            // foreground thread points at it.
            var tid = GetWindowThreadProcessId(foregroundWindow, out _);
            if (tid != 0)
            {
                var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                if (GetGUIThreadInfo(tid, ref gti) && gti.hwndFocus != IntPtr.Zero)
                {
                    return gti.hwndFocus;
                }
            }
        }

        return foregroundWindow;
    }

    /// <summary>Build the popup text: the layout code, plus " CAPS" when CapsLock is on.</summary>
    public static string ComposeDisplayText(string code, bool capsLockOn)
        => capsLockOn ? code + " CAPS" : code;

    /// <summary>
    /// Convert an HKL into a short uppercase display code, or <c>null</c> if it
    /// cannot be resolved (caller should then skip showing the indicator).
    /// </summary>
    public string? GetDisplayCode(IntPtr hkl)
    {
        if (hkl == IntPtr.Zero)
        {
            _logger.Warn("Cannot resolve layout: HKL is NULL.");
            return null;
        }

        var langId = GetLangIdFromHkl(hkl);
        var primaryLang = langId & 0x3FF; // PRIMARYLANGID

        // Explicit overrides for the three languages named in the spec keep the
        // codes stable regardless of CultureInfo naming quirks.
        switch (primaryLang)
        {
            case LANG_RUSSIAN:
                return "RU";
            case LANG_ENGLISH:
                return "EN";
            case LANG_LITHUANIAN:
                return "LT";
        }

        try
        {
            var culture = new CultureInfo(langId);

            // TwoLetterISOLanguageName is a lowercase ISO 639-1 code; upper-case it.
            var iso = culture.TwoLetterISOLanguageName;
            if (!string.IsNullOrWhiteSpace(iso) && iso.Length == 2)
            {
                return iso.ToUpperInvariant();
            }

            _logger.Warn($"Layout {hkl.ToInt64():X} (LANGID {langId:X}) has no 2-letter ISO code.");
            return null;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to resolve CultureInfo for LANGID {langId:X}.", ex);
            return null;
        }
    }
}
