using System;
using System.Drawing;
using System.IO;
using System.Threading;
using InputLanguagePopup.Caret;
using InputLanguagePopup.Input;
using InputLanguagePopup.Interop;
using InputLanguagePopup.Positioning;
using InputLanguagePopup.Settings;
using InputLanguagePopup.Ui;
using static InputLanguagePopup.Interop.NativeMethods;
using static InputLanguagePopup.Interop.Win32Ui;

namespace InputLanguagePopup.Diagnostics;

/// <summary>
/// <c>InputLanguagePopup.exe --selftest</c> — exercises every runtime-sensitive
/// subsystem once and writes the outcome to <c>selftest.log</c>. Used as a release
/// gate, so it is strictly fail-fast: any failure suppresses "SELFTEST PASSED" and
/// sets a non-zero exit code.
///
/// The UI Automation check deliberately drives the *whole* COM chain against a real
/// focused rich-edit control, because a wrong vtable slot can still return S_OK from
/// a different method with a compatible signature.
/// </summary>
internal static class SelfTest
{
    private static int _failures;
    private static StreamWriter? _writer;

    /// <summary>
    /// Keeps a timed-out host window (and therefore its window-procedure delegate)
    /// reachable for the rest of the process. Without this managed root the object
    /// graph is only host → delegate → host, which the GC can collect while the
    /// native HWND still holds a pointer to the thunk.
    /// </summary>
    private static SelfTestHostWindow? _timedOutHost;

    public static void Run()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InputLanguagePopup");
        Directory.CreateDirectory(dir);

        using var writer = new StreamWriter(Path.Combine(dir, "selftest.log"), append: false);
        _writer = writer;
        _failures = 0;

        var logger = new Logger(dir);

        try
        {
            var settingsService = new SettingsService(dir, logger);
            var settings = settingsService.Load();
            Ok($"settings: enabled={settings.Enabled} duration={settings.PopupDurationMs}");

            var languageService = new InputLanguageService(logger);
            var hkl = languageService.GetForegroundLayout(out var hwnd);
            var code = languageService.GetDisplayCode(hkl);
            if (code is null)
            {
                Fail($"layout: could not resolve a display code (hkl=0x{hkl:X})");
            }
            else
            {
                Ok($"layout: hwnd=0x{hwnd:X} hkl=0x{hkl:X} code={code}");
            }

            var (ctrlShift, altShift) = new SystemHotkeyService(logger).GetConfiguredChords();
            Ok($"hotkeys: ctrlShift={ctrlShift} altShift={altShift}");
            Ok($"capslock: {IsCapsLockOn()}");

            UiAutomationEndToEnd(logger);

            // Win32 windows + DPI probe + System.Drawing + layered popup.
            using var probe = new DpiProbeWindow();
            using var popup = new LanguagePopupWindow(logger);

            GetCursorPos(out var pt);
            var anchor = new CaretResult(CaretSource.CursorFallback, new Rectangle(pt.X, pt.Y, 0, 0));
            var scale = probe.GetScaleForPoint(PopupPositionService.GetAnchorPoint(anchor));
            Ok($"dpi probe: scale={scale}");

            var text = InputLanguageService.ComposeDisplayText(code ?? "EN", IsCapsLockOn());
            var logicalSize = LanguagePopupWindow.MeasureLogicalSize(text);
            var placement = new PopupPositionService(settings).Compute(anchor, logicalSize, scale);
            Ok($"measure/placement: text='{text}' logical={logicalSize} at={placement.Location}");

            popup.ShowPopup(text, placement, 400);
            Ok("popup shown (layered window + System.Drawing render)");

            using (var icon = MakeIcon())
            {
                using var tray = new TrayWindow(logger, icon.GetHicon(), "Self-test");
                var ran = false;
                tray.Post(() => ran = true);
                PumpFor(250);
                if (!ran)
                {
                    Fail("tray: posted action was not dispatched");
                }
                else
                {
                    Ok($"tray: hwnd=0x{tray.Handle:X}, Post/WM_APP_INVOKE dispatched");
                }
            }

            PumpFor(700);
            Ok("message loop + timers");
        }
        catch (Exception ex)
        {
            Fail("unhandled exception: " + ex);
        }
        finally
        {
            logger.Dispose();
        }

        if (_failures == 0)
        {
            Log("SELFTEST PASSED");
        }
        else
        {
            Log($"SELFTEST FAILED ({_failures} failure(s))");
            Environment.ExitCode = 1;
        }

        writer.Flush();
        _writer = null;
    }

    /// <summary>
    /// Drive GetFocusedElement → GetCurrentPatternAs → GetCaretRange →
    /// GetBoundingRectangles against a real focused RichEdit control. Any wrong
    /// vtable slot shows up here as a failure (or a crash), instead of silently
    /// degrading to the cursor fallback.
    /// </summary>
    private static void UiAutomationEndToEnd(Logger logger)
    {
        if (LoadLibraryW("Msftedit.dll") == IntPtr.Zero)
        {
            Fail("uia: could not load Msftedit.dll");
            return;
        }

        // Not disposed with `using`: if the UIA worker times out below it may still be
        // blocked inside a COM call, and destroying the window under it would leave it
        // poking at a dead HWND. On the happy path it is disposed explicitly.
        var host = new SelfTestHostWindow();
        var keepHostAlive = false;
        try
        {
            var edit = CreateWindowExW(0, "RICHEDIT50W", null,
                WS_CHILD | WS_VISIBLE | 0x0004 /* ES_MULTILINE */,
                0, 0, 300, 80, host.Handle, IntPtr.Zero, NativeMethods.GetModuleHandle(null), IntPtr.Zero);

            if (edit == IntPtr.Zero)
            {
                Fail("uia: could not create the RichEdit control");
                return;
            }

            SetWindowTextW(edit, "self test caret");
            ShowWindow(host.Handle, SW_SHOW);
            ForceForeground(host.Handle);
            SetFocus(edit);
            PumpFor(300);

            // Make sure we are actually probing *our* control: otherwise UIA reports
            // whatever else happens to be focused and the test proves nothing.
            var foreground = GetForegroundWindow();
            var focused = GetFocus();
            if (foreground != host.Handle || focused != edit)
            {
                Fail($"uia: could not focus the test control (foreground=0x{foreground:X} " +
                     $"expected 0x{host.Handle:X}, focus=0x{focused:X} expected 0x{edit:X})");
                return;
            }

            GetWindowRect(edit, out var editRect);
            keepHostAlive = RunUiaChain(editRect);
        }
        finally
        {
            if (keepHostAlive)
            {
                // Root it so the GC cannot collect the window procedure delegate
                // while a still-blocked worker (and the live HWND) may use it.
                _timedOutHost = host;
            }
            else
            {
                host.Dispose();
            }
        }
    }

    /// <summary>
    /// Runs the UIA chain on a worker thread (the provider lives on this thread, so
    /// the calls must not run here) and returns true if the worker is still stuck,
    /// meaning the host window must be left alive.
    /// </summary>
    /// <summary>
    /// Take the foreground by briefly attaching to the current foreground thread's
    /// input queue — a bare SetForegroundWindow is refused for a process that does
    /// not already own the foreground.
    /// </summary>
    private static void ForceForeground(IntPtr hWnd)
    {
        var foreground = GetForegroundWindow();
        if (foreground == hWnd)
        {
            return;
        }

        var us = GetCurrentThreadId();
        var them = foreground == IntPtr.Zero ? us : NativeMethods.GetWindowThreadProcessId(foreground, out _);

        var attached = them != us && AttachThreadInput(us, them, true);
        try
        {
            SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(us, them, false);
            }
        }
    }

    private static bool RunUiaChain(NativeMethods.RECT editRect)
    {

        // The UIA provider lives on this (window-owning) thread, so the COM calls
        // must run elsewhere while we keep pumping — otherwise they deadlock.
        double[]? rects = null;
        string? failure = null;
        var worker = new Thread(() =>
        {
            var automation = IntPtr.Zero;
            var element = IntPtr.Zero;
            var pattern = IntPtr.Zero;
            var range = IntPtr.Zero;
            try
            {
                automation = Uia.CreateAutomation();
                if (automation == IntPtr.Zero)
                {
                    failure = "CoCreateInstance(CUIAutomation) failed";
                    return;
                }

                if (Uia.GetFocusedElement(automation, out element) < 0 || element == IntPtr.Zero)
                {
                    failure = "GetFocusedElement returned nothing";
                    return;
                }

                var iid = Uia.IID_IUIAutomationTextPattern2;
                if (Uia.GetCurrentPatternAs(element, Uia.UIA_TextPattern2Id, ref iid, out pattern) < 0 ||
                    pattern == IntPtr.Zero)
                {
                    failure = "GetCurrentPatternAs(TextPattern2) returned nothing";
                    return;
                }

                // Match the app: an inactive range is a last-known caret and is
                // rejected there, so accepting it here would weaken the gate.
                if (Uia.GetCaretRange(pattern, out var isActive, out range) < 0 || range == IntPtr.Zero)
                {
                    failure = "GetCaretRange returned nothing";
                    return;
                }

                if (!isActive)
                {
                    failure = "GetCaretRange reported an inactive caret";
                    return;
                }

                rects = Uia.GetBoundingRectangles(range);
                if (rects is null || rects.Length < 4)
                {
                    if (Uia.ExpandToEnclosingUnit(range, 0) >= 0)
                    {
                        rects = Uia.GetBoundingRectangles(range);
                    }
                }

                if (rects is null || rects.Length < 4)
                {
                    failure = $"GetBoundingRectangles returned no rectangle (isActive={isActive})";
                }
            }
            catch (Exception ex)
            {
                failure = ex.ToString();
            }
            finally
            {
                Com.Release(range);
                Com.Release(pattern);
                Com.Release(element);
                Com.Release(automation);
            }
        });

        worker.IsBackground = true;
        worker.Start();

        // Bounded, for the same reason the app bounds its accessibility calls: a hung
        // provider must not hang the release job. Keep pumping so the provider (which
        // lives on this thread) can answer, but give up after the deadline.
        var deadline = Environment.TickCount64 + UiaTimeoutMs;
        while (worker.IsAlive && Environment.TickCount64 < deadline)
        {
            PumpFor(50);
        }

        if (worker.IsAlive)
        {
            Fail($"uia end-to-end: timed out after {UiaTimeoutMs} ms");
            return true; // still running — leave the host window alive for it
        }

        if (failure is not null)
        {
            Fail("uia end-to-end: " + failure);
            return false;
        }

        // Validate before comparing: every comparison with NaN is false, so a NaN
        // would silently sail through the bounds check below.
        var left = rects![0];
        var top = rects[1];
        var width = rects[2];
        var height = rects[3];
        if (!double.IsFinite(left) || !double.IsFinite(top) ||
            !double.IsFinite(width) || !double.IsFinite(height) ||
            width < 0 || height <= 0)
        {
            Fail($"uia end-to-end: implausible caret rect L={left} T={top} W={width} H={height}");
            return false;
        }

        // Final proof that the chain reported *our* control and not some other
        // focused window: the caret must sit inside the RichEdit's screen rectangle.
        if (left < editRect.Left || left > editRect.Right ||
            top < editRect.Top || top > editRect.Bottom)
        {
            Fail($"uia end-to-end: caret ({left:F0},{top:F0}) is outside the test control " +
                 $"[{editRect.Left},{editRect.Top}..{editRect.Right},{editRect.Bottom}] — " +
                 "the chain reported a different window");
            return false;
        }

        Ok($"uia end-to-end: caret rect L={left:F0} T={top:F0} W={width:F0} H={height:F0} " +
           $"(inside the test control)");
        return false;
    }

    private const int UiaTimeoutMs = 10_000;

    private sealed class SelfTestHostWindow : Win32Window
    {
        public SelfTestHostWindow()
            : base("SelfTestHost", WS_EX_TOOLWINDOW, WS_POPUP | WS_VISIBLE, 320, 100)
        {
        }
    }

    private static Bitmap MakeIcon()
    {
        var icon = new Bitmap(16, 16);
        using var g = Graphics.FromImage(icon);
        g.Clear(Color.Transparent);
        using var b = new SolidBrush(Color.White);
        g.FillEllipse(b, 0, 0, 15, 15);
        return icon;
    }

    private static void Ok(string message) => Log("OK " + message);

    private static void Fail(string message)
    {
        _failures++;
        Log("FAIL " + message);
    }

    private static void Log(string line)
        => _writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");

    private static void PumpFor(int milliseconds)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < end)
        {
            while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, 1))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }

            Thread.Sleep(5);
        }
    }
}
