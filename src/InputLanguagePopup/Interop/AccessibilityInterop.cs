using System;
using System.Runtime.InteropServices;

namespace InputLanguagePopup.Interop;

/// <summary>
/// Minimal MSAA (oleacc) interop for reading the system caret via
/// <c>AccessibleObjectFromWindow(OBJID_CARET)</c> + <c>IAccessible::accLocation</c>.
///
/// Only <c>accLocation</c> is needed; the preceding vtable slots (IDispatch's four
/// plus the IAccessible getters before <c>accLocation</c>) are reserved with
/// placeholders so the index lines up — those placeholders must never be called.
/// </summary>
internal static class Msaa
{
    public const uint OBJID_CARET = 0xFFFFFFF8;
    public const int CHILDID_SELF = 0;

    public static readonly Guid IID_IAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    [DllImport("oleacc.dll")]
    public static extern int AccessibleObjectFromWindow(
        IntPtr hwnd,
        uint dwId,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible? ppvObject);
}

/// <summary>
/// IAccessible reduced to <c>accLocation</c> (vtable slot 22: IUnknown 0-2,
/// IDispatch 3-6, IAccessible getters 7-21, accLocation 22).
/// </summary>
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("618736E0-3C3D-11CF-810C-00AA00389B71")]
internal interface IAccessible
{
    // IDispatch (slots 3-6).
    [PreserveSig] int _d0();
    [PreserveSig] int _d1();
    [PreserveSig] int _d2();
    [PreserveSig] int _d3();

    // IAccessible getters before accLocation (slots 7-21):
    // get_accParent, get_accChildCount, get_accChild, get_accName, get_accValue,
    // get_accDescription, get_accRole, get_accState, get_accHelp, get_accHelpTopic,
    // get_accKeyboardShortcut, get_accFocus, get_accSelection, get_accDefaultAction,
    // accSelect.
    [PreserveSig] int _a00();
    [PreserveSig] int _a01();
    [PreserveSig] int _a02();
    [PreserveSig] int _a03();
    [PreserveSig] int _a04();
    [PreserveSig] int _a05();
    [PreserveSig] int _a06();
    [PreserveSig] int _a07();
    [PreserveSig] int _a08();
    [PreserveSig] int _a09();
    [PreserveSig] int _a10();
    [PreserveSig] int _a11();
    [PreserveSig] int _a12();
    [PreserveSig] int _a13();
    [PreserveSig] int _a14();

    // Slot 22: accLocation(out left, out top, out width, out height, varChild).
    [PreserveSig]
    int accLocation(
        out int pxLeft,
        out int pyTop,
        out int pcxWidth,
        out int pcyHeight,
        [MarshalAs(UnmanagedType.Struct)] object varChild);
}
