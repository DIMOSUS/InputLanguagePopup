using System.Drawing;
using InputLanguagePopup.Interop;
using static InputLanguagePopup.Interop.NativeMethods;

namespace InputLanguagePopup.Caret;

/// <summary>
/// Strategy 3: the mouse cursor position. Always succeeds and is used when no
/// text caret can be resolved. Returns a zero-size rectangle at the cursor; the
/// position service applies the cursor-specific offset.
/// </summary>
public sealed class CursorFallbackLocator : ICaretPositionSource
{
    public CaretResult TryLocate(IntPtr foregroundWindow, uint threadId)
    {
        if (!GetCursorPos(out var pt))
        {
            return CaretResult.NotFound;
        }

        return new CaretResult(CaretSource.CursorFallback, new Rectangle(pt.X, pt.Y, 0, 0));
    }
}
