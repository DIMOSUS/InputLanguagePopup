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
    /// Pick the window whose thread actually owns the keyboard layout.
    ///
    /// The layout is per *thread*, and the thread that owns the top-level window is
    /// not always the one receiving keystrokes. In hosted content — WebView2 (new
    /// Microsoft Teams), UWP frames, Steam popups — the focused window belongs to a
    /// different thread, often a different process, and only that thread tracks the
    /// layout; the outer window's thread never sees keyboard input and keeps
    /// reporting the default layout forever. So prefer the focus window reported by
    /// GUITHREADINFO, which is where keystrokes actually go.
    ///
    /// Consoles are the one case that needs a different answer: there the layout
    /// lives on the default IME window.
    /// </summary>
    private IntPtr ResolveLayoutWindow(IntPtr foregroundWindow)
    {
        if (GetClassName(foregroundWindow) == "ConsoleWindowClass")
        {
            var ime = ImmGetDefaultIMEWnd(foregroundWindow);
            if (ime != IntPtr.Zero)
            {
                return ime;
            }
        }

        var tid = GetWindowThreadProcessId(foregroundWindow, out _);
        if (tid != 0)
        {
            var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (GetGUIThreadInfo(tid, ref gti) && gti.hwndFocus != IntPtr.Zero)
            {
                return gti.hwndFocus;
            }
        }

        return foregroundWindow;
    }

    /// <summary>Build the popup text: the layout code, plus " CAPS" when CapsLock is on.</summary>
    public static string ComposeDisplayText(string code, bool capsLockOn)
        => capsLockOn ? code + " CAPS" : code;

    /// <summary>
    /// One line describing every way of reading the current layout, for
    /// <c>--diag</c>. If the popup shows the wrong language on some machine, this
    /// tells us which source is actually correct there.
    /// </summary>
    public string DescribeLayoutState()
    {
        try
        {
            var fg = GetForegroundWindow();
            var fgClass = fg == IntPtr.Zero ? "<none>" : GetClassName(fg);
            var fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);

            var layoutWindow = fg == IntPtr.Zero ? IntPtr.Zero : ResolveLayoutWindow(fg);
            var layoutThread = layoutWindow == IntPtr.Zero ? 0 : GetWindowThreadProcessId(layoutWindow, out _);

            // What we currently use.
            var chosen = layoutThread == 0 ? IntPtr.Zero : GetKeyboardLayout(layoutThread);

            // Alternatives to compare against.
            var byForegroundThread = fgThread == 0 ? IntPtr.Zero : GetKeyboardLayout(fgThread);
            var ownThread = GetKeyboardLayout(0);

            var focusWindow = IntPtr.Zero;
            var byFocusThread = IntPtr.Zero;
            if (fgThread != 0)
            {
                var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                if (GetGUIThreadInfo(fgThread, ref gti) && gti.hwndFocus != IntPtr.Zero)
                {
                    focusWindow = gti.hwndFocus;
                    var focusThread = GetWindowThreadProcessId(focusWindow, out _);
                    if (focusThread != 0)
                    {
                        byFocusThread = GetKeyboardLayout(focusThread);
                    }
                }
            }

            var installed = string.Empty;
            var count = GetKeyboardLayoutList(0, null);
            if (count > 0)
            {
                var list = new IntPtr[count];
                GetKeyboardLayoutList((int)count, list);
                installed = string.Join(",", Array.ConvertAll(list, h => $"0x{h.ToInt64():X}"));
            }

            return $"fg=0x{fg:X} class='{fgClass}' fgThread={fgThread} " +
                   $"layoutWnd=0x{layoutWindow:X} layoutThread={layoutThread} | " +
                   $"CHOSEN hkl=0x{chosen.ToInt64():X} code={GetDisplayCode(chosen) ?? "<null>"} | " +
                   $"fgThread hkl=0x{byForegroundThread.ToInt64():X} code={GetDisplayCode(byForegroundThread) ?? "<null>"} | " +
                   $"focusWnd=0x{focusWindow:X} hkl=0x{byFocusThread.ToInt64():X} code={GetDisplayCode(byFocusThread) ?? "<null>"} | " +
                   $"ownThread hkl=0x{ownThread.ToInt64():X} | installed=[{installed}]";
        }
        catch (Exception ex)
        {
            return "DescribeLayoutState failed: " + ex.Message;
        }
    }

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
