using System;
using System.Runtime.InteropServices;
using InputLanguagePopup.Interop;
using static InputLanguagePopup.Interop.Win32Ui;

namespace InputLanguagePopup.Ui;

/// <summary>
/// Minimal owner of a registered window class + HWND, replacing WinForms'
/// <c>NativeWindow</c>/<c>Form</c> so the app is Native-AOT friendly.
/// The window procedure delegate is held in a field for the window's lifetime so
/// the GC cannot collect the native thunk.
/// </summary>
public abstract class Win32Window : IDisposable
{
    private readonly WndProc _wndProc; // must stay alive while the window exists
    private readonly string _className;
    private readonly IntPtr _hInstance;
    private IntPtr _handle;
    private bool _disposed;

    protected Win32Window(string name, int exStyle, int style, int width = 0, int height = 0)
    {
        _wndProc = WindowProc;
        // Unique per instance so repeated runs / multiple windows never collide.
        _className = $"InputLanguagePopup.{name}.{Guid.NewGuid():N}";
        _hInstance = NativeMethods.GetModuleHandle(null);

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _hInstance,
            lpszClassName = _className,
        };

        if (RegisterClassExW(ref wc) == 0)
        {
            throw new InvalidOperationException(
                $"RegisterClassEx failed for '{name}' (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        _handle = CreateWindowExW(exStyle, _className, null, style,
            0, 0, width, height, IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);

        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateWindowEx failed for '{name}' (Win32 error {Marshal.GetLastWin32Error()}).");
        }
    }

    public IntPtr Handle => _handle;

    /// <summary>Override to handle messages; call <see cref="Default"/> for the rest.</summary>
    protected virtual IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        => Default(hWnd, msg, wParam, lParam);

    protected static IntPtr Default(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        => DefWindowProcW(hWnd, msg, wParam, lParam);

    protected virtual void ReleaseResources()
    {
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseResources();

        if (_handle != IntPtr.Zero)
        {
            DestroyWindow(_handle);
            _handle = IntPtr.Zero;
        }

        GC.KeepAlive(_wndProc);
    }
}
