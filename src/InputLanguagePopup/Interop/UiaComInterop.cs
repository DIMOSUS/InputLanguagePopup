using System;
using System.Runtime.InteropServices;

namespace InputLanguagePopup.Interop;

/// <summary>
/// Raw COM helpers: call interface methods through the object's vtable with
/// function pointers instead of RCWs.
///
/// Native AOT has no built-in COM interop — creating an RCW throws
/// <c>InvalidOperation_ComInteropRequireComWrapperInstance</c> — so every COM call
/// here is a direct indirect call on the vtable slot. This is also immune to the
/// trimming hazard that <c>[ComImport]</c> placeholder slots had.
/// </summary>
internal static unsafe class Com
{
    /// <summary>Address of the given vtable slot of a COM object.</summary>
    public static IntPtr Slot(IntPtr obj, int index) => (*(IntPtr**)obj)[index];

    /// <summary>IUnknown::Release (vtable slot 2). Safe to call with a null pointer.</summary>
    public static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(obj, 2))(obj);
        }
    }
}

/// <summary>
/// UI Automation client, reached entirely through raw vtable calls.
/// Slot indices are 0-based including the three IUnknown entries.
/// </summary>
internal static unsafe class Uia
{
    public const int UIA_TextPattern2Id = 10024;

    public static readonly Guid IID_IUIAutomationTextPattern2 =
        new("506A921A-FCC9-409F-B23B-37EB74106872");

    private static Guid _clsidCUIAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");
    private static Guid _iidIUIAutomation = new("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");

    private const uint CLSCTX_INPROC_SERVER = 1;

    // Slot indices below are ABSOLUTE vtable positions, so every one starts after
    // the three IUnknown entries (QueryInterface=0, AddRef=1, Release=2). The
    // constants are written as "IUnknownSlots + n" where n is the method's position
    // within its own interface, to keep that offset impossible to forget.
    private const int IUnknownSlots = 3;

    // IUIAutomation: 0 CompareElements, 1 CompareRuntimeIds, 2 GetRootElement,
    // 3 ElementFromHandle, 4 ElementFromPoint, 5 GetFocusedElement.
    private const int SlotGetFocusedElement = IUnknownSlots + 5;   // 8

    // IUIAutomationElement: 0 SetFocus, 1 GetRuntimeId, 2 FindFirst, 3 FindAll,
    // 4 FindFirstBuildCache, 5 FindAllBuildCache, 6 BuildUpdatedCache,
    // 7 GetCurrentPropertyValue, 8 GetCurrentPropertyValueEx,
    // 9 GetCachedPropertyValue, 10 GetCachedPropertyValueEx, 11 GetCurrentPatternAs.
    private const int SlotGetCurrentPatternAs = IUnknownSlots + 11; // 14

    // IUIAutomationTextPattern2 (TextPattern's six methods, then RangeFromAnnotation):
    // 0 RangeFromPoint, 1 RangeFromChild, 2 GetSelection, 3 GetVisibleRanges,
    // 4 get_DocumentRange, 5 get_SupportedTextSelection, 6 RangeFromAnnotation,
    // 7 GetCaretRange.
    private const int SlotGetCaretRange = IUnknownSlots + 7;        // 10

    // IUIAutomationTextRange: 0 Clone, 1 Compare, 2 CompareEndpoints,
    // 3 ExpandToEnclosingUnit, 4 FindAttribute, 5 FindText, 6 GetAttributeValue,
    // 7 GetBoundingRectangles.
    private const int SlotExpandToEnclosingUnit = IUnknownSlots + 3; // 6
    private const int SlotGetBoundingRectangles = IUnknownSlots + 7; // 10

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    /// <summary>Create the UI Automation client; returns null pointer on failure.</summary>
    public static IntPtr CreateAutomation()
    {
        var hr = CoCreateInstance(ref _clsidCUIAutomation, IntPtr.Zero, CLSCTX_INPROC_SERVER,
            ref _iidIUIAutomation, out var automation);
        return hr >= 0 ? automation : IntPtr.Zero;
    }

    public static int GetFocusedElement(IntPtr automation, out IntPtr element)
    {
        IntPtr result;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)
            Com.Slot(automation, SlotGetFocusedElement))(automation, &result);
        element = hr >= 0 ? result : IntPtr.Zero;
        return hr;
    }

    public static int GetCurrentPatternAs(IntPtr element, int patternId, ref Guid riid, out IntPtr pattern)
    {
        IntPtr result;
        fixed (Guid* pIid = &riid)
        {
            var hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, Guid*, IntPtr*, int>)
                Com.Slot(element, SlotGetCurrentPatternAs))(element, patternId, pIid, &result);
            pattern = hr >= 0 ? result : IntPtr.Zero;
            return hr;
        }
    }

    public static int GetCaretRange(IntPtr textPattern, out bool isActive, out IntPtr range)
    {
        int active;
        IntPtr result;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, int*, IntPtr*, int>)
            Com.Slot(textPattern, SlotGetCaretRange))(textPattern, &active, &result);
        isActive = hr >= 0 && active != 0;
        range = hr >= 0 ? result : IntPtr.Zero;
        return hr;
    }

    public static int ExpandToEnclosingUnit(IntPtr range, int textUnit)
        => ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)
            Com.Slot(range, SlotExpandToEnclosingUnit))(range, textUnit);

    /// <summary>
    /// GetBoundingRectangles → SAFEARRAY of doubles [L,T,W,H, ...]. The SAFEARRAY is
    /// read and destroyed here; returns null when unavailable.
    /// </summary>
    public static double[]? GetBoundingRectangles(IntPtr range)
    {
        IntPtr psa;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)
            Com.Slot(range, SlotGetBoundingRectangles))(range, &psa);

        if (hr < 0 || psa == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (SafeArrayGetLBound(psa, 1, out var lbound) < 0 ||
                SafeArrayGetUBound(psa, 1, out var ubound) < 0)
            {
                return null;
            }

            var count = ubound - lbound + 1;
            if (count <= 0)
            {
                return null;
            }

            if (SafeArrayAccessData(psa, out var data) < 0 || data == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var values = new double[count];
                var src = (double*)data;
                for (var i = 0; i < count; i++)
                {
                    values[i] = src[i];
                }

                return values;
            }
            finally
            {
                SafeArrayUnaccessData(psa);
            }
        }
        finally
        {
            SafeArrayDestroy(psa);
        }
    }

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetLBound(IntPtr psa, uint nDim, out int lbound);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetUBound(IntPtr psa, uint nDim, out int ubound);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayAccessData(IntPtr psa, out IntPtr ppvData);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayUnaccessData(IntPtr psa);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayDestroy(IntPtr psa);
}
