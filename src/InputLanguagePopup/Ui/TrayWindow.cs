using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using InputLanguagePopup.Diagnostics;
using static InputLanguagePopup.Interop.Win32Ui;

namespace InputLanguagePopup.Ui;

/// <summary>A context-menu entry; <see cref="Id"/> is returned by TrackPopupMenu.</summary>
public readonly record struct TrayMenuItem(int Id, string Text, bool? IsChecked = null, bool IsSeparator = false)
{
    public static TrayMenuItem Separator() => new(0, string.Empty, null, true);
}

/// <summary>
/// The hidden window that owns the tray icon and drives the app's UI thread:
/// it receives the tray callback, shows the context menu, relays session-change
/// notifications, and runs work posted from other threads (the replacement for
/// WinForms' <c>Control.BeginInvoke</c>). Pure Win32 — no WinForms.
/// </summary>
public sealed class TrayWindow : Win32Window
{
    private const uint TrayIconId = 1;

    private readonly Logger _logger;
    private readonly ConcurrentQueue<Action> _posted = new();
    private IntPtr _iconHandle;
    private bool _iconAdded;

    /// <summary>Raised on the UI thread when the user asks for the tray context menu.</summary>
    public event Action? ContextMenuRequested;

    /// <summary>Raised on the UI thread for lock/unlock, RDP and fast-user-switch.</summary>
    public event Action? SessionChanged;

    public TrayWindow(Logger logger, IntPtr iconHandle, string tooltip)
        : base("Tray", 0, WS_OVERLAPPED)
    {
        _logger = logger;
        _iconHandle = iconHandle;

        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = Handle,
            uID = TrayIconId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_APP_TRAY,
            hIcon = iconHandle,
            szTip = tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };

        if (!Shell_NotifyIconW(NIM_ADD, ref data))
        {
            _logger.Error($"Shell_NotifyIcon(NIM_ADD) failed. Win32 error {Marshal.GetLastWin32Error()}.");
        }
        else
        {
            _iconAdded = true;
        }

        if (!WTSRegisterSessionNotification(Handle, NOTIFY_FOR_THIS_SESSION))
        {
            _logger.Warn($"WTSRegisterSessionNotification failed. Win32 error {Marshal.GetLastWin32Error()}.");
        }
    }

    /// <summary>
    /// Queue an action to run on the UI thread. Safe to call from any thread —
    /// this is the AOT-friendly replacement for <c>Control.BeginInvoke</c>.
    /// </summary>
    public void Post(Action action)
    {
        _posted.Enqueue(action);
        PostMessageW(Handle, WM_APP_INVOKE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Show the tray context menu at the cursor and return the chosen item id
    /// (0 if dismissed). Must be called on the UI thread.
    /// </summary>
    public int ShowMenu(TrayMenuItem[] items)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            _logger.Warn("CreatePopupMenu failed.");
            return 0;
        }

        try
        {
            foreach (var item in items)
            {
                if (item.IsSeparator)
                {
                    AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                    continue;
                }

                var flags = MF_STRING | (item.IsChecked == true ? MF_CHECKED : MF_UNCHECKED);
                AppendMenuW(menu, flags, (UIntPtr)(uint)item.Id, item.Text);
            }

            Interop.NativeMethods.GetCursorPos(out var pt);

            // Required so the menu closes when the user clicks elsewhere.
            SetForegroundWindow(Handle);
            var chosen = TrackPopupMenuEx(menu,
                TPM_LEFTALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
                pt.X, pt.Y, Handle, IntPtr.Zero);
            PostMessageW(Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            return chosen;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    protected override IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_APP_INVOKE:
                while (_posted.TryDequeue(out var action))
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("Posted UI action failed.", ex);
                    }
                }

                return IntPtr.Zero;

            case WM_APP_TRAY:
            {
                var trayMsg = (uint)(lParam.ToInt64() & 0xFFFF);
                if (trayMsg is WM_RBUTTONUP or WM_CONTEXTMENU or WM_LBUTTONUP)
                {
                    try
                    {
                        ContextMenuRequested?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("Tray menu handler failed.", ex);
                    }
                }

                return IntPtr.Zero;
            }

            case WM_WTSSESSION_CHANGE:
                try
                {
                    SessionChanged?.Invoke();
                }
                catch (Exception ex)
                {
                    _logger.Error("Session-change handler failed.", ex);
                }

                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return Default(hWnd, msg, wParam, lParam);
    }

    protected override void ReleaseResources()
    {
        WTSUnRegisterSessionNotification(Handle);

        if (_iconAdded)
        {
            var data = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = Handle,
                uID = TrayIconId,
                szTip = string.Empty,
                szInfo = string.Empty,
                szInfoTitle = string.Empty,
            };
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _iconAdded = false;
        }

        if (_iconHandle != IntPtr.Zero)
        {
            Interop.NativeMethods.DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }
    }
}
