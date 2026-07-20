using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using InputLanguagePopup.Diagnostics;
using InputLanguagePopup.Interop;

namespace InputLanguagePopup.Caret;

/// <summary>
/// The accessibility caret strategies — MSAA (oleacc) then, if enabled, COM UI
/// Automation — run together on <b>one</b> long-lived MTA worker thread.
///
/// Both cross into the target application's process (MSAA via <c>WM_GETOBJECT</c>,
/// UIA via COM) and can block if a provider hangs; neither can be cancelled once
/// in flight. Since a hung provider hangs both anyway, they share a single
/// containment wrapper:
///
///  * <b>Single-flight</b> — at most one request runs at a time; while busy, callers
///    get <see cref="CaretResult.NotFound"/> immediately (→ cursor fallback), so the
///    thread-pool never accumulates blocked probes and the queue never grows.
///  * <b>Timeout</b> — the caller waits at most <c>timeoutMs</c>. A hung provider
///    keeps the single worker occupied but leaks nothing.
///  * <b>Cooldown</b> — after a timeout, the whole accessibility chain is skipped
///    for a short window so a transient stall does not cause repeated waits.
///
/// The on-screen check is applied inside the chain so an off-screen MSAA rectangle
/// still lets UI Automation be tried before giving up.
/// </summary>
public sealed class AccessibilityCaretLocator : IDisposable
{
    private const int CooldownMs = 5000;
    private const int TextUnit_Character = 0;

    private readonly Logger _logger;
    private readonly MsaaCaretLocator _msaa;
    private readonly int _timeoutMs;
    private readonly Func<long> _ticks;
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _worker;
    private volatile bool _disposed;

    private int _busy;              // 0 = free, 1 = in flight
    private long _cooldownUntil;    // ticks until which the chain is skipped
    private TaskCompletionSource<CaretResult>? _current;

    // Request parameters (set under the single-flight gate; read on the worker).
    private IntPtr _fg;
    private uint _tid;
    private bool _useUia;

    // UIA COM object — worker-thread only.
    private IUIAutomation? _automation;

    public AccessibilityCaretLocator(Logger logger, int timeoutMs = 150, Func<long>? tickProvider = null)
    {
        _logger = logger;
        _msaa = new MsaaCaretLocator(logger);
        _timeoutMs = timeoutMs;
        _ticks = tickProvider ?? (static () => Environment.TickCount64);

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "AccessibilityCaretWorker",
        };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    /// <summary>
    /// Locate the caret via MSAA and (if <paramref name="useUia"/>) UI Automation,
    /// bounded by the single-flight/timeout/cooldown wrapper. Returns
    /// <see cref="CaretResult.NotFound"/> immediately if busy or in cooldown.
    /// </summary>
    public CaretResult TryLocate(IntPtr foregroundWindow, uint threadId, bool useUia)
    {
        if (_disposed)
        {
            return CaretResult.NotFound;
        }

        var now = _ticks();
        if (Volatile.Read(ref _cooldownUntil) > now)
        {
            return CaretResult.NotFound;
        }

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return CaretResult.NotFound;
        }

        _fg = foregroundWindow;
        _tid = threadId;
        _useUia = useUia;

        var tcs = new TaskCompletionSource<CaretResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _current = tcs;

        try
        {
            _queue.Add(RunCurrent);
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to enqueue accessibility work.", ex);
            Volatile.Write(ref _busy, 0);
            return CaretResult.NotFound;
        }

        if (tcs.Task.Wait(_timeoutMs))
        {
            return tcs.Task.Result;
        }

        Volatile.Write(ref _cooldownUntil, _ticks() + CooldownMs);
        _logger.Warn($"Accessibility caret lookup timed out after {_timeoutMs} ms; cooling down for {CooldownMs} ms.");
        return CaretResult.NotFound;
    }

    private void RunCurrent()
    {
        var tcs = _current;
        var result = CaretResult.NotFound;
        try
        {
            result = LocateChain(_fg, _tid, _useUia);
        }
        catch (Exception ex)
        {
            _logger.Error("Accessibility caret lookup threw.", ex);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
            tcs?.TrySetResult(result);
        }
    }

    private CaretResult LocateChain(IntPtr fg, uint tid, bool useUia)
    {
        var msaa = _msaa.TryLocate(fg, tid);
        if (msaa.IsValid && ScreenBounds.IsOnScreen(msaa.Bounds))
        {
            return msaa;
        }

        if (!useUia)
        {
            return CaretResult.NotFound;
        }

        var uia = LocateViaUia();
        if (uia.IsValid && ScreenBounds.IsOnScreen(uia.Bounds))
        {
            return uia;
        }

        return CaretResult.NotFound;
    }

    private CaretResult LocateViaUia()
    {
        IUIAutomationElement? element = null;
        IUIAutomationTextPattern2? textPattern = null;
        IUIAutomationTextRange? range = null;

        try
        {
            _automation ??= (IUIAutomation)new CUIAutomation();

            if (_automation.GetFocusedElement(out element) < 0 || element is null)
            {
                return CaretResult.NotFound;
            }

            var iid = Uia.IID_IUIAutomationTextPattern2;
            if (element.GetCurrentPatternAs(Uia.UIA_TextPattern2Id, ref iid, out textPattern) < 0 ||
                textPattern is null)
            {
                return CaretResult.NotFound;
            }

            // isActive == 0 means the range is a last-known / inactive caret, which
            // could place the popup at a stale position — prefer the cursor fallback.
            if (textPattern.GetCaretRange(out var isActive, out range) < 0 ||
                isActive == 0 ||
                range is null)
            {
                return CaretResult.NotFound;
            }

            var result = RectsFromRange(range);
            if (result.IsValid)
            {
                return result;
            }

            // A collapsed caret range can produce no bounding rectangle in some
            // providers. Expand it to a single character and try again.
            if (range.ExpandToEnclosingUnit(TextUnit_Character) >= 0)
            {
                result = RectsFromRange(range);
            }

            return result;
        }
        catch (COMException ex)
        {
            _logger.Warn($"UI Automation COM call failed: 0x{ex.HResult:X8} {ex.Message}");
            return CaretResult.NotFound;
        }
        finally
        {
            Release(range);
            Release(textPattern);
            Release(element);
        }
    }

    private static CaretResult RectsFromRange(IUIAutomationTextRange range)
    {
        if (range.GetBoundingRectangles(out var rects) < 0 || rects is null)
        {
            return CaretResult.NotFound;
        }

        return ParseFirstRect(rects);
    }

    // No real screen coordinate approaches this; larger values are a broken provider
    // and would overflow the int conversion below.
    private const double MaxCoord = 1_000_000;

    private static CaretResult ParseFirstRect(double[] rects)
    {
        // The SAFEARRAY holds groups of four doubles: left, top, width, height,
        // in physical screen pixels.
        for (var i = 0; i + 3 < rects.Length; i += 4)
        {
            var left = rects[i];
            var top = rects[i + 1];
            var width = rects[i + 2];
            var height = rects[i + 3];

            // Validate all four as finite and in range before rounding — NaN/Infinity
            // height would slip past a bare "height <= 0", and a huge finite value
            // (e.g. 1e100) would overflow the int conversion.
            if (!double.IsFinite(left) || !double.IsFinite(top) ||
                !double.IsFinite(width) || !double.IsFinite(height) ||
                width < 0 || height <= 0 ||
                Math.Abs(left) > MaxCoord || Math.Abs(top) > MaxCoord ||
                width > MaxCoord || height > MaxCoord)
            {
                continue;
            }

            var bounds = new Rectangle(
                (int)Math.Round(left),
                (int)Math.Round(top),
                (int)Math.Round(Math.Max(0, width)),
                (int)Math.Round(height));

            return new CaretResult(CaretSource.UiAutomation, bounds);
        }

        return CaretResult.NotFound;
    }

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }

    private void WorkerLoop()
    {
        try
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                // Isolate item failures so one exception cannot kill the loop and
                // silently time out every later request for the rest of the session.
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    _logger.Error("Accessibility work item failed.", ex);
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Queue disposed during shutdown.
        }
        catch (Exception ex)
        {
            _logger.Error("Accessibility worker loop terminated unexpectedly.", ex);
        }
        finally
        {
            Release(_automation);
            _automation = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _queue.CompleteAdding();
        }
        catch
        {
            // ignore
        }

        // The worker may be stuck in a hung provider call; it is a background thread
        // and will not block process exit regardless.
        _worker.Join(TimeSpan.FromMilliseconds(500));
        _queue.Dispose();
    }
}
