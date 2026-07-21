using System;
using System.Runtime.InteropServices;

namespace InputLanguagePopup.Interop;

/// <summary>
/// Minimal MSAA (oleacc) interop for reading the system caret via
/// <c>AccessibleObjectFromWindow(OBJID_CARET)</c> + <c>IAccessible::accLocation</c>.
///
/// Like the UI Automation interop, this uses raw vtable calls rather than an RCW,
/// because Native AOT has no built-in COM interop.
/// </summary>
internal static unsafe class Msaa
{
    public const uint OBJID_CARET = 0xFFFFFFF8;

    // IAccessible vtable: IUnknown (0-2), IDispatch (3-6), then the IAccessible
    // members; accLocation is the 16th IAccessible member → slot 22.
    private const int SlotAccLocation = 22;

    private const ushort VT_I4 = 3;

    private static Guid _iidIAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    /// <summary>VARIANT (24 bytes on x64) — only VT_I4 is needed for CHILDID_SELF.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct VARIANT
    {
        public ushort vt;
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public long value0;
        public long value1;
    }

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(
        IntPtr hwnd, uint dwId, ref Guid riid, out IntPtr ppvObject);

    /// <summary>
    /// Get the caret object for <paramref name="hwnd"/>; returns a null pointer if
    /// unavailable. The caller must <see cref="Com.Release"/> the result.
    /// </summary>
    public static IntPtr GetCaretObject(IntPtr hwnd)
    {
        var hr = AccessibleObjectFromWindow(hwnd, OBJID_CARET, ref _iidIAccessible, out var acc);
        return hr >= 0 ? acc : IntPtr.Zero;
    }

    /// <summary>
    /// IAccessible::accLocation for CHILDID_SELF. Returns the HRESULT; note that
    /// S_FALSE (1) means "no location", so only S_OK (0) yields usable values.
    /// </summary>
    public static int AccLocation(IntPtr accessible, out int left, out int top, out int width, out int height)
    {
        var child = new VARIANT { vt = VT_I4, value0 = 0 };

        int l, t, w, h;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, int*, int*, int*, int*, VARIANT, int>)
            Com.Slot(accessible, SlotAccLocation))(accessible, &l, &t, &w, &h, child);

        left = l;
        top = t;
        width = w;
        height = h;
        return hr;
    }
}
