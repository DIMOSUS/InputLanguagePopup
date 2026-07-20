using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;
using InputLanguagePopup.Positioning;
using static InputLanguagePopup.Interop.NativeMethods;

namespace InputLanguagePopup.Ui;

/// <summary>
/// Borderless, click-through, non-activating layered popup that shows the current
/// layout code. Display-only: it contains no hook or language-detection logic.
/// The single instance is created once and reused for every show.
/// </summary>
public sealed class LanguagePopupForm : Form
{
    /// <summary>Popup size at 96 DPI (physical size is scaled per monitor).</summary>
    public static readonly Size LogicalSize = new(44, 32);

    private readonly System.Windows.Forms.Timer _hideTimer = new();
    private readonly System.Windows.Forms.Timer _fadeTimer = new();

    private Bitmap? _currentBitmap;
    private Point _currentLocation;
    private Size _currentSize;
    private byte _currentAlpha = 255;
    private int _fadeStep;

    private const int FadeSteps = 6;
    private const int FadeIntervalMs = 22;

    public LanguagePopupForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = string.Empty;

        _hideTimer.Tick += OnHideTimerTick;
        _fadeTimer.Interval = FadeIntervalMs;
        _fadeTimer.Tick += OnFadeTimerTick;

        // Force native handle creation up front so the first real show has no lag.
        _ = Handle;
    }

    // Never steal activation from the foreground application when shown.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW |
                          WS_EX_TOPMOST | WS_EX_TRANSPARENT;
            return cp;
        }
    }

    /// <summary>
    /// Show (or refresh) the popup with the given text and placement. Restarts the
    /// hide timer. Must be called on the UI thread.
    /// </summary>
    public void ShowPopup(string text, PopupPlacement placement, int durationMs)
    {
        _fadeTimer.Stop();
        _hideTimer.Stop();

        _currentAlpha = 255;
        _currentLocation = placement.Location;
        _currentSize = placement.Size;

        _currentBitmap?.Dispose();
        _currentBitmap = Render(text, placement);

        ApplyBitmap(_currentBitmap, _currentLocation, _currentSize, _currentAlpha);

        // Bring topmost and make visible without activating.
        SetWindowPos(Handle, HWND_TOPMOST, _currentLocation.X, _currentLocation.Y,
            _currentSize.Width, _currentSize.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);

        _hideTimer.Interval = Math.Max(50, durationMs);
        _hideTimer.Start();
    }

    public void HidePopup()
    {
        _hideTimer.Stop();
        _fadeTimer.Stop();
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);
    }

    private void OnHideTimerTick(object? sender, EventArgs e)
    {
        _hideTimer.Stop();
        // Begin a short fade-out (kept cheap: re-blits the same bitmap at lower
        // constant alpha, no re-rendering).
        _fadeStep = 0;
        _fadeTimer.Start();
    }

    private void OnFadeTimerTick(object? sender, EventArgs e)
    {
        _fadeStep++;
        if (_fadeStep >= FadeSteps || _currentBitmap is null)
        {
            _fadeTimer.Stop();
            HidePopup();
            return;
        }

        var alpha = (byte)(255 - (255 * _fadeStep / FadeSteps));
        ApplyBitmap(_currentBitmap, _currentLocation, _currentSize, alpha);
    }

    private static Bitmap Render(string text, PopupPlacement placement)
    {
        var size = placement.Size;
        var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);

        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        var radius = Math.Max(4, (int)Math.Round(8 * placement.DpiScale));
        var rect = new Rectangle(0, 0, size.Width - 1, size.Height - 1);

        using (var path = RoundedRect(rect, radius))
        using (var back = new SolidBrush(Color.FromArgb(224, 30, 30, 32)))
        using (var border = new Pen(Color.FromArgb(70, 255, 255, 255), 1f))
        {
            g.FillPath(back, path);
            g.DrawPath(border, path);
        }

        var emPixels = Math.Max(9f, size.Height * 0.52f);
        using var font = CreateFont(emPixels);
        using var textBrush = new SolidBrush(Color.White);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString(text, font, textBrush, rect, format);

        return bmp;
    }

    private static Font CreateFont(float emPixels)
    {
        // The string-based Font constructor owns its family internally (no
        // FontFamily to dispose) and silently substitutes a default family when
        // the requested one is missing — detect that via Name and fall back to
        // bold Segoe UI (which GDI+ again substitutes silently if absent).
        var font = new Font("Segoe UI Semibold", emPixels, FontStyle.Regular, GraphicsUnit.Pixel);
        if (font.Name.StartsWith("Segoe UI", StringComparison.OrdinalIgnoreCase))
        {
            return font;
        }

        font.Dispose();
        return new Font("Segoe UI", emPixels, FontStyle.Bold, GraphicsUnit.Pixel);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        if (d <= 0)
        {
            path.AddRectangle(bounds);
            path.CloseFigure();
            return path;
        }

        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void ApplyBitmap(Bitmap bmp, Point location, Size size, byte constAlpha)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = IntPtr.Zero;
        var oldBitmap = IntPtr.Zero;

        try
        {
            hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memDc, hBitmap);

            var srcSize = new SIZE(size.Width, size.Height);
            var srcPoint = new POINT(0, 0);
            var dstPoint = new POINT(location.X, location.Y);

            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = constAlpha,
                AlphaFormat = AC_SRC_ALPHA,
            };

            UpdateLayeredWindow(Handle, screenDc, ref dstPoint, ref srcSize,
                memDc, ref srcPoint, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero)
            {
                SelectObject(memDc, oldBitmap);
            }

            if (hBitmap != IntPtr.Zero)
            {
                DeleteObject(hBitmap);
            }

            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hideTimer.Dispose();
            _fadeTimer.Dispose();
            _currentBitmap?.Dispose();
        }

        base.Dispose(disposing);
    }
}
