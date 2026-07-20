using System;
using System.Runtime.InteropServices;
using InputLanguagePopup.Diagnostics;
using InputLanguagePopup.Interop;
using static InputLanguagePopup.Interop.NativeMethods;

namespace InputLanguagePopup.Hooking;

/// <summary>
/// Installs a global WH_KEYBOARD_LL hook and converts raw Win32 keyboard events
/// into <see cref="KeyDown"/> / <see cref="KeyUp"/> events. Contains no gesture
/// logic. The hook callback never suppresses keystrokes: it always forwards to
/// CallNextHookEx and does no heavy work.
///
/// Threading note: WH_KEYBOARD_LL callbacks are dispatched on the thread that
/// installed the hook (this application's UI thread, which pumps messages), not
/// on a separate system thread. Handlers still must stay cheap — the OS drops
/// or removes hooks whose callbacks exceed the low-level hook timeout.
/// </summary>
public sealed class GlobalKeyboardHook : IDisposable
{
    private readonly Logger _logger;

    // The delegate must be kept alive for the lifetime of the hook, otherwise the
    // GC may collect it and the callback address becomes invalid.
    private readonly LowLevelKeyboardProc _proc;

    private IntPtr _hookHandle = IntPtr.Zero;
    private bool _disposed;

    /// <summary>Raised inside the hook callback (installing/UI thread) for a key-down.</summary>
    public event Action<int>? KeyDown;

    /// <summary>Raised inside the hook callback (installing/UI thread) for a key-up.</summary>
    public event Action<int>? KeyUp;

    public GlobalKeyboardHook(Logger logger)
    {
        _logger = logger;
        _proc = HookCallback;
    }

    public bool IsInstalled => _hookHandle != IntPtr.Zero;

    public void Install()
    {
        if (IsInstalled)
        {
            return;
        }

        // WH_KEYBOARD_LL is a global hook and does not require a module handle in
        // the traditional sense, but passing the current module handle is the
        // documented, well-behaved approach.
        var hMod = GetModuleHandle(null);
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0);

        if (_hookHandle == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            _logger.Error($"SetWindowsHookEx(WH_KEYBOARD_LL) failed. Win32 error {err}.");
            throw new InvalidOperationException($"Failed to install keyboard hook (Win32 error {err}).");
        }

        _logger.Info("Global keyboard hook installed.");
    }

    public void Uninstall()
    {
        if (!IsInstalled)
        {
            return;
        }

        if (!UnhookWindowsHookEx(_hookHandle))
        {
            var err = Marshal.GetLastWin32Error();
            _logger.Warn($"UnhookWindowsHookEx failed. Win32 error {err}.");
        }
        else
        {
            _logger.Info("Global keyboard hook removed.");
        }

        _hookHandle = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Only inspect valid actions (nCode >= 0); everything else is forwarded untouched.
        if (nCode >= 0)
        {
            try
            {
                var msg = (int)wParam;
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var vk = (int)data.vkCode;

                // Ignore synthetic (injected) events so programmatic key presses do
                // not trigger the indicator.
                if ((data.flags & LLKHF_INJECTED) == 0)
                {
                    switch (msg)
                    {
                        case WM_KEYDOWN:
                        case WM_SYSKEYDOWN:
                            KeyDown?.Invoke(vk);
                            break;
                        case WM_KEYUP:
                        case WM_SYSKEYUP:
                            KeyUp?.Invoke(vk);
                            break;
                    }
                }
            }
            catch
            {
                // The callback must never throw across the native boundary.
            }
        }

        // Never suppress the event — the OS must still process Ctrl+Shift itself.
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Uninstall();
    }
}
