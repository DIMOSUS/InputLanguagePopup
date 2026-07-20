using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;
using InputLanguagePopup.Autostart;
using InputLanguagePopup.Caret;
using InputLanguagePopup.Diagnostics;
using InputLanguagePopup.Hooking;
using InputLanguagePopup.Input;
using InputLanguagePopup.Positioning;
using InputLanguagePopup.Settings;
using InputLanguagePopup.Ui;
using static InputLanguagePopup.Interop.NativeMethods;

namespace InputLanguagePopup;

/// <summary>
/// The tray-hosted application root. Wires the services together, owns the
/// timers and the popup lifecycle, and cleans everything up on exit. There is no
/// main window.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
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
    private readonly LanguagePopupForm _popupForm;
    private readonly DpiProbeWindow _dpiProbe;

    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _startupItem;
    private IntPtr _trayIconHandle = IntPtr.Zero;

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

    public TrayApplicationContext(Logger logger, SettingsService settingsService, AppSettings settings)
    {
        _logger = logger;
        _settingsService = settingsService;
        _settings = settings;

        _startupService = new StartupService(logger);
        _systemHotkeys = new SystemHotkeyService(logger);
        _languageService = new InputLanguageService(logger);
        _caretLocator = new CaretLocator(logger, settings);
        _positionService = new PopupPositionService(settings);
        _popupForm = new LanguagePopupForm(logger);
        _dpiProbe = new DpiProbeWindow();

        _detector = new LayoutHotkeyGestureDetector();
        _detector.GestureRecognized += OnGestureRecognized;

        _hook = new GlobalKeyboardHook(logger);
        _hook.KeyDown += _detector.OnKeyDown;
        _hook.KeyUp += _detector.OnKeyUp;

        // --- Tray icon + menu ---
        _enabledItem = new ToolStripMenuItem("Enabled", null, OnToggleEnabled)
        {
            CheckOnClick = true,
            Checked = _settings.Enabled,
        };
        _startupItem = new ToolStripMenuItem("Start with Windows", null, OnToggleStartup)
        {
            CheckOnClick = true,
            Checked = _settings.StartWithWindows,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_enabledItem);
        menu.Items.Add(new ToolStripMenuItem("Show test popup", null, OnShowTestPopup));
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, OnExit));

        _trayIcon = new NotifyIcon
        {
            Icon = CreateTrayIcon(out _trayIconHandle),
            Text = "Input Language Popup",
            Visible = true,
            ContextMenuStrip = menu,
        };

        // Keep the autostart registry entry in sync with the persisted setting.
        SyncStartupWithSetting();

        // Lock / unlock, RDP connect/disconnect and fast-user-switch can swallow
        // key-up events; clear gesture state so modifiers cannot get stuck.
        SystemEvents.SessionSwitch += OnSessionSwitch;

        try
        {
            _hook.Install();
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to install the keyboard hook at startup.", ex);
            MessageBox.Show(
                "Failed to install the global keyboard hook. The indicator will not work.\n\n" + ex.Message,
                "Input Language Popup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        _logger.Info("Application started.");
    }

    // ---- Gesture handling ------------------------------------------------

    // May run on a SystemEvents thread; marshal the reset onto the UI thread so it
    // does not race the hook callback that also mutates the detector.
    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!_popupForm.IsDisposed)
            {
                _popupForm.BeginInvoke(new Action(_detector.Reset));
            }
        }
        catch (InvalidOperationException)
        {
            // Form handle gone (shutting down) — ignore.
        }
    }

    // Raised from inside the WH_KEYBOARD_LL callback. Low-level hook callbacks run
    // on the installing thread (our UI thread), so this is technically already the
    // UI thread — but it is still *inside* the hook callback, which must return to
    // CallNextHookEx immediately. BeginInvoke defers the real work to a later
    // message-loop iteration.
    private void OnGestureRecognized(LayoutGesture gesture)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!_popupForm.IsDisposed)
            {
                _popupForm.BeginInvoke(new Action(() => StartDetection(gesture)));
            }
        }
        catch (InvalidOperationException)
        {
            // The form handle is gone (shutting down) — ignore.
        }
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

    // Runs on the UI thread (deferred out of the hook callback by BeginInvoke,
    // so the registry read in ShouldHandle never runs inside the hook).
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
            await ProbeAndShowAsync(id, token, isSecond: false).ConfigureAwait(false);

            var remaining = _settings.SecondLayoutCheckDelayMs - _settings.FirstLayoutCheckDelayMs;
            if (remaining > 0)
            {
                await Task.Delay(remaining, token).ConfigureAwait(false);
            }

            await ProbeAndShowAsync(id, token, isSecond: true).ConfigureAwait(false);
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
    private Task ProbeAndShowAsync(int id, CancellationToken token, bool isSecond)
    {
        token.ThrowIfCancellationRequested();

        var probe = Probe();
        if (probe is null || token.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        if (_popupForm.IsDisposed)
        {
            return Task.CompletedTask;
        }

        try
        {
            _popupForm.BeginInvoke(new Action(() => ShowOnUi(id, probe.Value, isSecond)));
        }
        catch (InvalidOperationException)
        {
            // Form handle gone.
        }

        return Task.CompletedTask;
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
    // CapsLock is read via GetKeyState: on this message-pumping, hook-owning thread it
    // reliably reflects the global toggle state (verified empirically), and the gesture
    // that triggered this was just processed by our hook, so the state is fresh.
    private void ShowOnUi(int id, ProbeResult probe, bool isSecond)
    {
        if (_disposed || id != _requestId || !_settings.Enabled)
        {
            return; // stale request or disabled
        }

        var text = InputLanguageService.ComposeDisplayText(probe.Code, IsCapsLockOn());
        var logicalSize = LanguagePopupForm.MeasureLogicalSize(text);
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

        _popupForm.ShowPopup(text, placement, _settings.PopupDurationMs);
        _lastShownRequestId = id;
        _lastShownText = text;
        _lastPlacement = placement;
    }

    // ---- Tray menu handlers ---------------------------------------------

    private void OnToggleEnabled(object? sender, EventArgs e)
    {
        _settings.Enabled = _enabledItem.Checked;
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

            _popupForm.HidePopup();
        }
    }

    private void OnToggleStartup(object? sender, EventArgs e)
    {
        var desired = _startupItem.Checked;

        if (_startupService.SetEnabled(desired))
        {
            _settings.StartWithWindows = desired;
        }
        else
        {
            // The registry could not be updated (e.g. dev run under dotnet host).
            // Reflect the real state instead of claiming success.
            var actual = _startupService.IsEnabled();
            _startupItem.Checked = actual;
            _settings.StartWithWindows = actual;
            MessageBox.Show(
                "Could not update the \"Start with Windows\" setting. See the log for details.",
                "Input Language Popup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        _settingsService.Save(_settings);
    }

    private void OnShowTestPopup(object? sender, EventArgs e)
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
            var placement = _positionService.Compute(caret, LanguagePopupForm.MeasureLogicalSize(text), dpiScale);
            _popupForm.ShowPopup(text, placement, _settings.PopupDurationMs);
        }
        catch (Exception ex)
        {
            _logger.Error("Test popup failed.", ex);
        }
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _logger.Info("Exit requested from tray menu.");
        ExitThread();
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
                    // intent, but do not claim success either.
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
        finally
        {
            // Show the tray checkbox as the *actual* registry state, so the UI never
            // claims autostart is on when the entry is absent.
            try
            {
                _startupItem.Checked = _startupService.IsEnabled();
            }
            catch
            {
                // ignore
            }
        }
    }

    // ---- Tray icon rendering --------------------------------------------

    private static Icon CreateTrayIcon(out IntPtr iconHandle)
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

        iconHandle = bmp.GetHicon();
        // Clone into a managed icon so we can free the native handle immediately... but
        // Icon.FromHandle does not own the handle. Keep the handle and free it on dispose.
        return (Icon)Icon.FromHandle(iconHandle).Clone();
    }

    // ---- Cleanup ---------------------------------------------------------

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            _logger.Info("Application shutting down.");

            SystemEvents.SessionSwitch -= OnSessionSwitch;

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
            _dpiProbe.Dispose();

            _trayIcon.Visible = false;
            _trayIcon.Icon?.Dispose();
            _trayIcon.ContextMenuStrip?.Dispose();
            _trayIcon.Dispose();

            if (_trayIconHandle != IntPtr.Zero)
            {
                DestroyIcon(_trayIconHandle);
                _trayIconHandle = IntPtr.Zero;
            }

            _popupForm.Dispose();
            _logger.Info("Application stopped.");
            _logger.Dispose();
        }

        base.Dispose(disposing);
    }
}
