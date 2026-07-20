using System.Drawing;
using InputLanguagePopup.Caret;
using InputLanguagePopup.Positioning;
using Xunit;

namespace InputLanguagePopup.Tests;

public class PopupPositionServiceTests
{
    private static readonly PopupOffsets Offsets = new(CaretX: 8, CaretY: 8, CursorX: 16, CursorY: 20);
    private static readonly Size Size = new(44, 32);

    private static CaretResult Caret(int x, int y, int w, int h)
        => new(CaretSource.Win32Caret, new Rectangle(x, y, w, h));

    private static CaretResult Cursor(int x, int y)
        => new(CaretSource.CursorFallback, new Rectangle(x, y, 0, 0));

    [Fact]
    public void Caret_PlacedBelowRight_WithOffset()
    {
        var work = new Rectangle(0, 0, 1920, 1080);
        var caret = Caret(100, 100, 2, 16); // right=102, bottom=116

        var p = PopupPositionService.ComputeLocation(caret, Size, 1.0, work, Offsets);

        Assert.Equal(new Point(102 + 8, 116 + 8), p);
    }

    [Fact]
    public void Caret_NearBottom_FlipsAbove()
    {
        var work = new Rectangle(0, 0, 1920, 1080);
        var caret = Caret(100, 1060, 2, 16); // bottom=1076; below would overflow 1080

        var p = PopupPositionService.ComputeLocation(caret, Size, 1.0, work, Offsets);

        // Flipped above the caret top (1060): y = 1060 - 8 - 32 = 1020
        Assert.Equal(1020, p.Y);
    }

    [Fact]
    public void Caret_NearRight_FlipsLeft()
    {
        var work = new Rectangle(0, 0, 1920, 1080);
        var caret = Caret(1910, 100, 2, 16); // right=1912; right+8+44 overflows 1920

        var p = PopupPositionService.ComputeLocation(caret, Size, 1.0, work, Offsets);

        // Flipped to the left of the caret left (1910): x = 1910 - 8 - 44 = 1858
        Assert.Equal(1858, p.X);
    }

    [Fact]
    public void Caret_OnMonitorLeftOfPrimary_NegativeCoordinates()
    {
        // A monitor entirely to the left of the primary: x from -1920..0.
        var work = new Rectangle(-1920, 0, 1920, 1080);
        var caret = Caret(-1800, 200, 2, 16);

        var p = PopupPositionService.ComputeLocation(caret, Size, 1.0, work, Offsets);

        Assert.Equal(new Point(-1798 + 8, 216 + 8), p);
        Assert.True(p.X >= work.Left && p.X + Size.Width <= work.Right);
    }

    [Fact]
    public void Caret_ScaledByDpi_150Percent()
    {
        var work = new Rectangle(0, 0, 3840, 2160);
        var caret = Caret(200, 200, 3, 24);

        // At 1.5x the offset is scaled: 8 * 1.5 = 12.
        var p = PopupPositionService.ComputeLocation(caret, new Size(66, 48), 1.5, work, Offsets);

        Assert.Equal(new Point(203 + 12, 224 + 12), p);
    }

    [Fact]
    public void Cursor_PlacedDownRight_WithOffset()
    {
        var work = new Rectangle(0, 0, 1920, 1080);

        var p = PopupPositionService.ComputeLocation(Cursor(500, 400), Size, 1.0, work, Offsets);

        Assert.Equal(new Point(500 + 16, 400 + 20), p);
    }

    [Fact]
    public void Cursor_AtBottomRightCorner_DoesNotCoverPointer()
    {
        var work = new Rectangle(0, 0, 1920, 1080);
        var cursor = Cursor(1919, 1079);

        var p = PopupPositionService.ComputeLocation(cursor, Size, 1.0, work, Offsets);

        // The popup rectangle must not contain the pointer.
        Assert.False(new Rectangle(p, Size).Contains(new Point(1919, 1079)));
        // ...and must stay within the work area.
        Assert.True(p.X >= work.Left && p.X + Size.Width <= work.Right);
        Assert.True(p.Y >= work.Top && p.Y + Size.Height <= work.Bottom);
    }

    [Fact]
    public void Placement_AlwaysWithinWorkArea()
    {
        var work = new Rectangle(-100, -50, 800, 600); // top-left monitor, negative origin
        var caret = Caret(-100, -50, 1, 16);

        var p = PopupPositionService.ComputeLocation(caret, Size, 1.0, work, Offsets);

        Assert.True(p.X >= work.Left);
        Assert.True(p.Y >= work.Top);
        Assert.True(p.X + Size.Width <= work.Right);
        Assert.True(p.Y + Size.Height <= work.Bottom);
    }
}
