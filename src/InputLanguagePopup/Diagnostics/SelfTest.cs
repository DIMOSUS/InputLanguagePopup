using System;
using System.Drawing;
using System.IO;
using InputLanguagePopup.Caret;
using InputLanguagePopup.Input;
using InputLanguagePopup.Positioning;
using InputLanguagePopup.Settings;
using InputLanguagePopup.Ui;
using static InputLanguagePopup.Interop.NativeMethods;

namespace InputLanguagePopup.Diagnostics;

/// <summary>
/// <c>InputLanguagePopup.exe --selftest</c> — exercises every runtime-sensitive
/// subsystem once and writes the outcome to <c>selftest.log</c> next to the normal
/// log. Useful for verifying a build (particularly the Native AOT one, where COM
/// interop, System.Drawing and source-generated JSON must all still work) without
/// needing to reproduce a real layout switch.
/// </summary>
internal static class SelfTest
{
    public static void Run()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InputLanguagePopup");
        Directory.CreateDirectory(dir);

        var logPath = Path.Combine(dir, "selftest.log");
        using var writer = new StreamWriter(logPath, append: false);
        void Log(string line) => writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");

        var logger = new Logger(dir);

        try
        {
            // 1. Settings round-trip (source-generated JSON under AOT).
            var settingsService = new SettingsService(dir, logger);
            var settings = settingsService.Load();
            Log($"OK settings: enabled={settings.Enabled} duration={settings.PopupDurationMs} caps={settings.HandleWinSpace}");

            // 2. Layout + CultureInfo (globalization must be intact).
            var languageService = new InputLanguageService(logger);
            var hkl = languageService.GetForegroundLayout(out var hwnd);
            var code = languageService.GetDisplayCode(hkl);
            Log($"OK layout: hwnd=0x{hwnd:X} hkl=0x{hkl:X} code={code ?? "<null>"}");

            // 3. System hotkey (registry read).
            var hotkeys = new SystemHotkeyService(logger);
            var (ctrlShift, altShift) = hotkeys.GetConfiguredChords();
            Log($"OK hotkeys: ctrlShift={ctrlShift} altShift={altShift}");

            // 4. CapsLock.
            Log($"OK capslock: {IsCapsLockOn()}");

            // 5a. UI Automation COM chain, exercised directly: the caret cascade may
            // stop at MSAA, and under Native AOT the COM path is the riskiest part.
            var automation = Interop.Uia.CreateAutomation();
            if (automation == IntPtr.Zero)
            {
                Log("FAIL uia: CoCreateInstance(CUIAutomation) returned null");
            }
            else
            {
                var hr = Interop.Uia.GetFocusedElement(automation, out var focused);
                Log($"OK uia: automation=0x{automation:X} GetFocusedElement hr=0x{hr:X8} element=0x{focused:X}");
                Interop.Com.Release(focused);
                Interop.Com.Release(automation);
            }

            // 5b. Caret cascade — MSAA then UI Automation, on the bounded worker.
            using (var caretLocator = new CaretLocator(logger, settings))
            {
                var caret = caretLocator.Locate(hwnd);
                Log($"OK caret: source={caret.Source} bounds={caret.Bounds}");
            }

            // 6. Win32 windows + DPI probe + System.Drawing rendering + layered window.
            using var probe = new DpiProbeWindow();
            using var popup = new LanguagePopupWindow(logger);

            GetCursorPos(out var pt);
            var anchor = new CaretResult(CaretSource.CursorFallback, new Rectangle(pt.X, pt.Y, 0, 0));
            var scale = probe.GetScaleForPoint(PopupPositionService.GetAnchorPoint(anchor));
            Log($"OK dpi probe: scale={scale}");

            var text = InputLanguageService.ComposeDisplayText(code ?? "EN", IsCapsLockOn());
            var logicalSize = LanguagePopupWindow.MeasureLogicalSize(text);
            var placement = new PopupPositionService(settings).Compute(anchor, logicalSize, scale);
            Log($"OK measure/placement: text='{text}' logical={logicalSize} placement={placement.Location} {placement.Size}");

            popup.ShowPopup(text, placement, 600);
            Log("OK popup shown (layered window + System.Drawing render)");

            // 7. Tray window: Shell_NotifyIcon add/remove + session registration.
            using (var icon = new Bitmap(16, 16))
            {
                using (var g = Graphics.FromImage(icon))
                {
                    g.Clear(Color.Transparent);
                    using var b = new SolidBrush(Color.White);
                    g.FillEllipse(b, 0, 0, 15, 15);
                }

                using var tray = new TrayWindow(logger, icon.GetHicon(), "Self-test");
                Log($"OK tray window: hwnd=0x{tray.Handle:X} (icon added)");

                var ran = false;
                tray.Post(() => ran = true);
                PumpFor(200);
                Log($"OK tray Post/WM_APP_INVOKE dispatched={ran}");
            }

            // Pump messages so the hide/fade timers actually run.
            PumpFor(1200);
            Log("OK message loop + timers");

            Log("SELFTEST PASSED");
        }
        catch (Exception ex)
        {
            Log("SELFTEST FAILED: " + ex);
        }
        finally
        {
            logger.Dispose();
        }
    }

    private static void PumpFor(int milliseconds)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < end)
        {
            while (Interop.Win32Ui.PeekMessageW(out var msg, IntPtr.Zero, 0, 0, 1))
            {
                Interop.Win32Ui.TranslateMessage(ref msg);
                Interop.Win32Ui.DispatchMessageW(ref msg);
            }

            System.Threading.Thread.Sleep(10);
        }
    }
}
