using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
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

    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _startupItem;
    private IntPtr _trayIconHandle = IntPtr.Zero;

    // Detection state (mutated only on the UI thread).
    private volatile int _requestId;
    private CancellationTokenSource? _cts;
    private int _lastShownRequestId = -1;
    private string? _lastShownCode;
    private PopupPlacement _lastPlacement;

    private bool _disposed;

    private readonly record struct ProbeResult(string Code, PopupPlacement Placement);

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
        _popupForm = new LanguagePopupForm();

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

        var placement = _positionService.Compute(caret, LanguagePopupForm.LogicalSize);
        return new ProbeResult(code, placement);
    }

    // Runs on the UI thread.
    private void ShowOnUi(int id, ProbeResult probe, bool isSecond)
    {
        if (_disposed || id != _requestId || !_settings.Enabled)
        {
            return; // stale request or disabled
        }

        // On the second check, avoid re-showing (and restarting the timer) if
        // nothing changed since the first check.
        if (isSecond &&
            _lastShownRequestId == id &&
            _lastShownCode == probe.Code &&
            _lastPlacement.Equals(probe.Placement))
        {
            return;
        }

        _popupForm.ShowPopup(probe.Code, probe.Placement, _settings.PopupDurationMs);
        _lastShownRequestId = id;
        _lastShownCode = probe.Code;
        _lastPlacement = probe.Placement;
    }

    // ---- Tray menu handlers ---------------------------------------------

    private void OnToggleEnabled(object? sender, EventArgs e)
    {
        _settings.Enabled = _enabledItem.Checked;
        _settingsService.Save(_settings);
        _logger.Info($"Enabled set to {_settings.Enabled}.");

        if (!_settings.Enabled)
        {
            _popupForm.HidePopup();
        }
    }

    private void OnToggleStartup(object? sender, EventArgs e)
    {
        _settings.StartWithWindows = _startupItem.Checked;
        _startupService.SetEnabled(_settings.StartWithWindows);
        _settingsService.Save(_settings);
    }

    private void OnShowTestPopup(object? sender, EventArgs e)
    {
        try
        {
            var hkl = _languageService.GetForegroundLayout(out _);
            var code = _languageService.GetDisplayCode(hkl) ?? "EN";

            if (!GetCursorPos(out var pt))
            {
                return;
            }

            var caret = new CaretResult(CaretSource.CursorFallback, new Rectangle(pt.X, pt.Y, 0, 0));
            var placement = _positionService.Compute(caret, LanguagePopupForm.LogicalSize);
            _popupForm.ShowPopup(code, placement, _settings.PopupDurationMs);
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
            var actuallyEnabled = _startupService.IsEnabled();
            if (_settings.StartWithWindows && !actuallyEnabled)
            {
                _startupService.SetEnabled(true);
            }
            else if (!_settings.StartWithWindows && actuallyEnabled)
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
