using System.Drawing;

namespace InputLanguagePopup.Caret;

/// <summary>Which strategy produced a position (for diagnostics / offset choice).</summary>
public enum CaretSource
{
    None,
    Win32Caret,
    UiAutomation,
    CursorFallback,
}

/// <summary>
/// A resolved position for the indicator. <see cref="Bounds"/> is the caret /
/// anchor rectangle in physical screen pixels; the popup is placed relative to
/// its bottom-right corner.
/// </summary>
public readonly record struct CaretResult(CaretSource Source, Rectangle Bounds)
{
    public bool IsValid => Source != CaretSource.None;

    public static readonly CaretResult NotFound = new(CaretSource.None, Rectangle.Empty);
}

/// <summary>A caret-location strategy. Implementations must be non-blocking or bounded.</summary>
public interface ICaretPositionSource
{
    /// <summary>
    /// Try to find the caret for <paramref name="foregroundWindow"/> / its thread.
    /// Returns <see cref="CaretResult.NotFound"/> if unavailable.
    /// </summary>
    CaretResult TryLocate(IntPtr foregroundWindow, uint threadId);
}
