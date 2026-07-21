using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;
using InputLanguagePopup.Autostart;
using InputLanguagePopup.Caret;
using InputLanguagePopup.Diagnostics;
using InputLanguagePopup.Hooking;
using InputLanguagePopup.Input;
using InputLanguagePopup.Positioning;
using InputLanguagePopup.Settings;
using InputLanguagePopup.Ui;
using static InputLanguagePopup.Interop.NativeMethods;
using static InputLanguagePopup.Interop.Win32Ui;

namespace InputLanguagePopup;

/// <summary>
/// The tray-hosted application root. Wires the services together, owns the
/// timers and the popup lifecycle, and cleans everything up on exit. There is no
/// main window. Pure Win32 (no WinForms) so the app can be published Native AOT.
/// </summary>
public sealed class TrayApplication : IDisposable
{
    private const int MenuEnabled = 1;
    private const int MenuTestPopup = 2;
    private const int MenuStartup = 3;
    private const int MenuExit = 4;

    private readonly Logger _logger;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly StartupService _startupService;

    private readonly GlobalKeyboardHook _hook;
    private readonly LayoutHotkeyGestureDetector _detector;
    private readonly SystemHotkeyService _systemHotkeys;
    private readonly InputLanguageService _languageService;
    private readonly CaretLocator _caretLocator;
    private readonly PopupPositionService _positionService;
    private readonly LanguagePopupWindow _popup;
    private readonly DpiProbeWindow _dpiProbe;
    private readonly TrayWindow _tray;

    // Detection state (mutated only on the UI thread).
    private volatile int _requestId;
    private CancellationTokenSource? _cts;
    private int _lastShownRequestId = -1;
    private string? _lastShownText;
    private PopupPlacement _lastPlacement;

    private bool _disposed;

    // Detection result. Presentation (CapsLock text, size, placement) is computed
    // later on the UI thread, so the popup can size itself to the final text.
    private readonly record struct ProbeResult(string Code, CaretResult Caret, IntPtr Hwnd);

    public TrayApplication(Logger logger, SettingsService settingsService, AppSettings settings)
    {
        _logger = logger;
        _settingsService = settingsService;
        _settings = settings;

        _startupService = new StartupService(logger);
        _systemHotkeys = new SystemHotkeyService(logger);
        _languageService = new InputLanguageService(logger);
        _caretLocator = new CaretLocator(logger, settings);
        _positionService = new PopupPositionService(settings);
        _popup = new LanguagePopupWindow(logger);
        _dpiProbe = new DpiProbeWindow();

        _detector = new LayoutHotkeyGestureDetector();
        _detector.GestureRecognized += OnGestureRecognized;

        _tray = new TrayWindow(logger, CreateTrayIcon(), "Input Language Popup");
        _tray.ContextMenuRequested += OnContextMenuRequested;
        // Lock / unlock, RDP connect/disconnect and fast-user-switch can swallow
        // key-up events; clear gesture state so modifiers cannot get stuck. This
        // arrives on the UI thread (window message), same as the hook callback.
        _tray.SessionChanged += _detector.Reset;

        _hook = new GlobalKeyboardHook(logger);
        _hook.KeyDown += _detector.OnKeyDown;
        _hook.KeyUp += _detector.OnKeyUp;

        // Keep the autostart registry entry in sync with the persisted setting.
        SyncStartupWithSetting();

        try
        {
            _hook.Install();
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to install the keyboard hook at startup.", ex);
            MessageBoxW(IntPtr.Zero,
                "Failed to install the global keyboard hook. The indicator will not work.\n\n" + ex.Message,
                "Input Language Popup", MB_OK | MB_ICONWARNING);
        }

        _logger.Info("Application started.");
    }

    // ---- Tray menu -------------------------------------------------------

    private void OnContextMenuRequested()
    {
        // Built fresh each time, so the check marks always reflect the real state
        // (including the actual autostart registry entry).
        var items = new[]
        {
            new TrayMenuItem(MenuEnabled, "Enabled", _settings.Enabled),
            new TrayMenuItem(MenuTestPopup, "Show test popup"),
            new TrayMenuItem(MenuStartup, "Start with Windows", _startupService.IsEnabled()),
            TrayMenuItem.Separator(),
            new TrayMenuItem(MenuExit, "Exit"),
        };

        switch (_tray.ShowMenu(items))
        {
            case MenuEnabled:
                ToggleEnabled();
                break;
            case MenuTestPopup:
                ShowTestPopup();
                break;
            case MenuStartup:
                ToggleStartup();
                break;
            case MenuExit:
                _logger.Info("Exit requested from tray menu.");
                PostQuitMessage(0);
                break;
        }
    }

    private void ToggleEnabled()
    {
        _settings.Enabled = !_settings.Enabled;
        _settingsService.Save(_settings);
        _logger.Info($"Enabled set to {_settings.Enabled}.");

        if (!_settings.Enabled)
        {
            // Cancel any probe already in flight so a delayed layout check cannot
            // pop the indicator up after the user just turned it off.
            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed by a superseding request — nothing to cancel.
            }

            _popup.HidePopup();
        }
    }

    private void ToggleStartup()
    {
        var desired = !_startupService.IsEnabled();

        if (_startupService.SetEnabled(desired))
        {
            _settings.StartWithWindows = desired;
        }
        else
        {
            // The registry could not be updated (e.g. dev run under dotnet host).
            // Reflect the real state instead of claiming success.
            _settings.StartWithWindows = _startupService.IsEnabled();
            MessageBoxW(IntPtr.Zero,
                "Could not update the \"Start with Windows\" setting. See the log for details.",
                "Input Language Popup", MB_OK | MB_ICONWARNING);
        }

        _settingsService.Save(_settings);
    }

    private void ShowTestPopup()
    {
        try
        {
            var hkl = _languageService.GetForegroundLayout(out _);
            var code = _languageService.GetDisplayCode(hkl) ?? "EN";
            var text = InputLanguageService.ComposeDisplayText(code, IsCapsLockOn());

            if (!GetCursorPos(out var pt))
            {
                return;
            }

            var caret = new CaretResult(CaretSource.CursorFallback, new Rectangle(pt.X, pt.Y, 0, 0));
            var dpiScale = _dpiProbe.GetScaleForPoint(PopupPositionService.GetAnchorPoint(caret));
            var placement = _positionService.Compute(caret, LanguagePopupWindow.MeasureLogicalSize(text), dpiScale);
            _popup.ShowPopup(text, placement, _settings.PopupDurationMs);
        }
        catch (Exception ex)
        {
            _logger.Error("Test popup failed.", ex);
        }
    }

    // ---- Gesture handling ------------------------------------------------

    // Raised from inside the WH_KEYBOARD_LL callback, which runs on the installing
    // (UI) thread — but still *inside* the hook callback, which must return to
    // CallNextHookEx immediately. Post defers the real work (including the registry
    // read in ShouldHandle) to a later message-loop iteration.
    private void OnGestureRecognized(LayoutGesture gesture)
    {
        if (_disposed)
        {
            return;
        }

        _tray.Post(() => StartDetection(gesture));
    }

    /// <summary>
    /// Does the completed gesture match how this system actually switches
    /// layouts right now? Chords are gated by the live registry setting (read
    /// per gesture — cheap, and picks up Settings changes without a restart);
    /// Win+Space is hardwired in Windows and gated only by our own setting.
    /// </summary>
    private bool ShouldHandle(LayoutGesture gesture)
    {
        if (gesture == LayoutGesture.WinSpace)
        {
            return _settings.HandleWinSpace;
        }

        var (ctrlShift, altShift) = _systemHotkeys.GetConfiguredChords();
        return gesture switch
        {
            LayoutGesture.CtrlShift => ctrlShift,
            LayoutGesture.AltShift => altShift,
            _ => false,
        };
    }

    // Runs on the UI thread.
    private void StartDetection(LayoutGesture gesture)
    {
        if (_disposed || !_settings.Enabled || !ShouldHandle(gesture))
        {
            return;
        }

        // Supersede the previous request. Ownership of a CancellationTokenSource
        // stays with this method: cancel (which runs the awaiting Task.Delay's
        // cancellation synchronously) and then dispose the previous source here.
        // The sequence itself must not dispose it, otherwise a later Cancel() would
        // hit an already-disposed source (ObjectDisposedException).
        var previous = _cts;
        var cts = new CancellationTokenSource();
        _cts = cts;
        var id = ++_requestId;

        if (previous is not null)
        {
            try
            {
                previous.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed elsewhere — nothing to cancel.
            }

            previous.Dispose();
        }

        _ = DetectionSequenceAsync(id, cts);
    }

    private async Task DetectionSequenceAsync(int id, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            // First check shortly after the chord (Windows may still be switching).
            await Task.Delay(_settings.FirstLayoutCheckDelayMs, token).ConfigureAwait(false);
            ProbeAndShow(id, token, isSecond: false);

            var remaining = _settings.SecondLayoutCheckDelayMs - _settings.FirstLayoutCheckDelayMs;
            if (remaining > 0)
            {
                await Task.Delay(remaining, token).ConfigureAwait(false);
            }

            ProbeAndShow(id, token, isSecond: true);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer chord.
        }
        catch (Exception ex)
        {
            _logger.Error("Layout detection sequence failed.", ex);
        }

        // NB: 'cts' is disposed by the next StartDetection (which supersedes this
        // request) or by the application's Dispose — never here.
    }

    // Runs on a thread-pool thread (ConfigureAwait(false) above).
    private void ProbeAndShow(int id, CancellationToken token, bool isSecond)
    {
        token.ThrowIfCancellationRequested();

        var probe = Probe();
        if (probe is null || token.IsCancellationRequested || _disposed)
        {
            return;
        }

        var result = probe.Value;
        _tray.Post(() => ShowOnUi(id, result, isSecond));
    }

    private ProbeResult? Probe()
    {
        var hkl = _languageService.GetForegroundLayout(out var hwnd);
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        var code = _languageService.GetDisplayCode(hkl);
        if (code is null)
        {
            // Could not resolve the layout — prefer not showing anything.
            return null;
        }

        var caret = _caretLocator.Locate(hwnd);
        if (!caret.IsValid)
        {
            return null;
        }

        // Spec 6.4: if the foreground window changed while we were resolving the
        // layout/caret, both may describe the wrong window — discard the stale
        // result rather than show it. (The second scheduled check, or the next
        // chord, will probe the new window.)
        if (GetForegroundWindow() != hwnd)
        {
            _logger.Info("Foreground window changed during probe; discarding stale result.");
            return null;
        }

        return new ProbeResult(code, caret, hwnd);
    }

    // Runs on the UI thread — Win32 positioning and the DPI probe are safe here.
    // CapsLock is read via GetKeyState: empirically, on the message-pumping thread that
    // owns the WH_KEYBOARD_LL hook, it reflects the global toggle state (this is stable
    // Windows 10/11 behaviour, not a documented contract), and the gesture that
    // triggered this was just processed by our hook, so the state is fresh.
    private void ShowOnUi(int id, ProbeResult probe, bool isSecond)
    {
        // Also re-check the foreground window: the user may have switched apps between
        // the probe and this deferred show, in which case the caret/layout are stale.
        if (_disposed || id != _requestId || !_settings.Enabled ||
            GetForegroundWindow() != probe.Hwnd)
        {
            return; // stale request, disabled, or foreground changed
        }

        var text = InputLanguageService.ComposeDisplayText(probe.Code, IsCapsLockOn());
        var logicalSize = LanguagePopupWindow.MeasureLogicalSize(text);
        var dpiScale = _dpiProbe.GetScaleForPoint(PopupPositionService.GetAnchorPoint(probe.Caret));
        var placement = _positionService.Compute(probe.Caret, logicalSize, dpiScale);

        // On the second check, avoid re-showing (and restarting the timer) if
        // nothing changed since the first check.
        if (isSecond &&
            _lastShownRequestId == id &&
            _lastShownText == text &&
            _lastPlacement.Equals(placement))
        {
            return;
        }

        _popup.ShowPopup(text, placement, _settings.PopupDurationMs);
        _lastShownRequestId = id;
        _lastShownText = text;
        _lastPlacement = placement;
    }

    // ---- Startup sync ----------------------------------------------------

    private void SyncStartupWithSetting()
    {
        try
        {
            // When enabled, call SetEnabled unconditionally: it rewrites the value
            // only if missing or pointing elsewhere, repairing a stale path left by
            // moving the portable executable.
            if (_settings.StartWithWindows)
            {
                if (!_startupService.SetEnabled(true))
                {
                    // Could not register (e.g. running under the dotnet host during
                    // development, or a registry error). Do not clobber the persisted
                    // intent, but do not claim success either — the tray menu reads
                    // the real registry state when it is built.
                    _logger.Warn("Autostart is requested but could not be registered right now.");
                }
            }
            else if (_startupService.IsEnabled())
            {
                _startupService.SetEnabled(false);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to reconcile autostart setting.", ex);
        }
    }

    // ---- Tray icon rendering --------------------------------------------

    /// <summary>Draw the tray icon and return a raw HICON (owned by the tray window).</summary>
    private static IntPtr CreateTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var back = new SolidBrush(Color.FromArgb(230, 30, 30, 32));
            g.FillEllipse(back, 1, 1, 30, 30);
            using var pen = new Pen(Color.FromArgb(120, 255, 255, 255));
            g.DrawEllipse(pen, 1, 1, 30, 30);
            using var font = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            using var fmt = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("AЯ", font, textBrush, new RectangleF(0, 0, 32, 32), fmt);
        }

        return bmp.GetHicon();
    }

    // ---- Cleanup ---------------------------------------------------------

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _logger.Info("Application shutting down.");

        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch
        {
            // ignore
        }

        _hook.KeyDown -= _detector.OnKeyDown;
        _hook.KeyUp -= _detector.OnKeyUp;
        _hook.Dispose();

        _caretLocator.Dispose();

        _tray.Dispose();   // removes the tray icon and destroys the HICON
        _popup.Dispose();
        _dpiProbe.Dispose();

        _logger.Info("Application stopped.");
        _logger.Dispose();
    }
}
