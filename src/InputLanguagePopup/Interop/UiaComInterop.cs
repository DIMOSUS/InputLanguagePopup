using System;
using System.Runtime.InteropServices;

namespace InputLanguagePopup.Interop;

/// <summary>
/// Minimal hand-written COM interop for the UI Automation client, exposing only
/// the vtable slots needed to reach TextPattern2.GetCaretRange. Unused preceding
/// slots are reserved with placeholder methods so the vtable indices line up with
/// the native interfaces; those placeholders must never be called.
/// </summary>
internal static class Uia
{
    // Pattern id for TextPattern2.
    public const int UIA_TextPattern2Id = 10024;

    public static readonly Guid IID_IUIAutomationTextPattern2 =
        new("506A921A-FCC9-409F-B23B-37EB74106872");
}

/// <summary>The CUIAutomation coclass — <c>new CUIAutomation()</c> issues CoCreateInstance.</summary>
[ComImport]
[Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
internal class CUIAutomation
{
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")]
internal interface IUIAutomation
{
    // Slots 0-4: CompareElements, CompareRuntimeIds, GetRootElement,
    //            ElementFromHandle, ElementFromPoint.
    [PreserveSig] int _slot00();
    [PreserveSig] int _slot01();
    [PreserveSig] int _slot02();
    [PreserveSig] int _slot03();
    [PreserveSig] int _slot04();

    // Slot 5: GetFocusedElement.
    [PreserveSig]
    int GetFocusedElement([MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? element);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")]
internal interface IUIAutomationElement
{
    // Slots 0-10: SetFocus, GetRuntimeId, FindFirst, FindAll, FindFirstBuildCache,
    //             FindAllBuildCache, BuildUpdatedCache, GetCurrentPropertyValue,
    //             GetCurrentPropertyValueEx, GetCachedPropertyValue,
    //             GetCachedPropertyValueEx.
    [PreserveSig] int _slot00();
    [PreserveSig] int _slot01();
    [PreserveSig] int _slot02();
    [PreserveSig] int _slot03();
    [PreserveSig] int _slot04();
    [PreserveSig] int _slot05();
    [PreserveSig] int _slot06();
    [PreserveSig] int _slot07();
    [PreserveSig] int _slot08();
    [PreserveSig] int _slot09();
    [PreserveSig] int _slot10();

    // Slot 11: GetCurrentPatternAs(patternId, riid, out pattern).
    [PreserveSig]
    int GetCurrentPatternAs(
        int patternId,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextPattern2? patternObject);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("506A921A-FCC9-409F-B23B-37EB74106872")]
internal interface IUIAutomationTextPattern2
{
    // Slots 0-6: (TextPattern) RangeFromPoint, RangeFromChild, GetSelection,
    //            GetVisibleRanges, get_DocumentRange, get_SupportedTextSelection;
    //            (TextPattern2) RangeFromAnnotation.
    [PreserveSig] int _slot00();
    [PreserveSig] int _slot01();
    [PreserveSig] int _slot02();
    [PreserveSig] int _slot03();
    [PreserveSig] int _slot04();
    [PreserveSig] int _slot05();
    [PreserveSig] int _slot06();

    // Slot 7: GetCaretRange(out isActive, out range).
    [PreserveSig]
    int GetCaretRange(out int isActive, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRange? range);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("A543CC6A-F4AE-494B-8239-C814481187A8")]
internal interface IUIAutomationTextRange
{
    // Slots 0-6: Clone, Compare, CompareEndpoints, ExpandToEnclosingUnit,
    //            FindAttribute, FindText, GetAttributeValue.
    [PreserveSig] int _slot00();
    [PreserveSig] int _slot01();
    [PreserveSig] int _slot02();
    [PreserveSig] int _slot03();
    [PreserveSig] int _slot04();
    [PreserveSig] int _slot05();
    [PreserveSig] int _slot06();

    // Slot 7: GetBoundingRectangles -> SAFEARRAY(double) [L,T,W,H, L,T,W,H, ...].
    [PreserveSig]
    int GetBoundingRectangles(
        [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_R8)] out double[]? rects);
}
