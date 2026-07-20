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
/// Strategy 2: the caret via COM UI Automation TextPattern2.GetCaretRange.
///
/// UI Automation calls can block if the target provider is slow or hung, and must
/// never run on the keyboard-hook thread. Work is marshalled to a single
/// long-lived MTA worker thread. Because an in-process COM call cannot be forcibly
/// aborted, robustness is achieved by containment rather than cancellation:
///
///  * <b>Single-flight</b> — at most one request is ever in flight. If a request
///    arrives while the worker is busy, the caller gets <see cref="CaretResult.NotFound"/>
///    immediately (and falls back to the cursor). The queue therefore never grows.
///  * <b>Timeout</b> — the caller waits at most <c>timeoutMs</c>. A hung provider
///    keeps the worker (and the single-flight slot) occupied; it is simply never
///    freed, so UI Automation stays disabled until the call returns — instead of
///    piling up work and leaking events.
///  * <b>Cooldown</b> — after a timeout, UI Automation is skipped entirely for a
///    short window, so a transient stall does not cause repeated 150 ms waits.
/// </summary>
public sealed class UiAutomationCaretLocator : ICaretPositionSource, IDisposable
{
    private const int CooldownMs = 5000;

    private readonly Logger _logger;
    private readonly int _timeoutMs;
    private readonly Func<long> _ticks;
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _worker;
    private volatile bool _disposed;

    // Single-flight gate: 0 = free, 1 = a request is in flight. Released by the
    // worker when (if) it finishes the current request.
    private int _busy;

    // Ticks until which UI Automation is skipped after a timeout.
    private long _cooldownUntil;

    // The in-flight request's completion source (only one at a time).
    private TaskCompletionSource<CaretResult>? _current;

    // Owned by the worker thread only.
    private IUIAutomation? _automation;

    public UiAutomationCaretLocator(Logger logger, int timeoutMs = 150, Func<long>? tickProvider = null)
    {
        _logger = logger;
        _timeoutMs = timeoutMs;
        _ticks = tickProvider ?? (static () => Environment.TickCount64);

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "UIAutomationCaretWorker",
        };
        // MTA: the UIA COM client does not require a message pump there, and a hung
        // call can safely be abandoned by the caller.
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    public CaretResult TryLocate(IntPtr foregroundWindow, uint threadId)
    {
        if (_disposed)
        {
            return CaretResult.NotFound;
        }

        var now = _ticks();
        if (Volatile.Read(ref _cooldownUntil) > now)
        {
            // Still recovering from a recent timeout — skip UI Automation.
            return CaretResult.NotFound;
        }

        // Single-flight: bail out immediately if the worker is already busy.
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return CaretResult.NotFound;
        }

        var tcs = new TaskCompletionSource<CaretResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _current = tcs;

        try
        {
            _queue.Add(RunCurrent);
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to enqueue UI Automation work.", ex);
            Volatile.Write(ref _busy, 0);
            return CaretResult.NotFound;
        }

        if (tcs.Task.Wait(_timeoutMs))
        {
            // The worker completed and has already released the single-flight slot.
            return tcs.Task.Result;
        }

        // Timed out: the worker still owns the slot (it will release it if/when the
        // COM call returns). Put UI Automation on cooldown so we do not keep waiting.
        Volatile.Write(ref _cooldownUntil, _ticks() + CooldownMs);
        _logger.Warn($"UI Automation caret lookup timed out after {_timeoutMs} ms; cooling down for {CooldownMs} ms.");
        return CaretResult.NotFound;
    }

    private void RunCurrent()
    {
        var tcs = _current;
        var result = CaretResult.NotFound;
        try
        {
            result = LocateOnWorker();
        }
        catch (Exception ex)
        {
            _logger.Error("UI Automation caret lookup threw.", ex);
        }
        finally
        {
            // Release the slot *before* completing, so a waiter that already timed
            // out (or a fresh request) can proceed immediately.
            Volatile.Write(ref _busy, 0);
            tcs?.TrySetResult(result);
        }
    }

    private CaretResult LocateOnWorker()
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

    private const int TextUnit_Character = 0;

    private static CaretResult RectsFromRange(IUIAutomationTextRange range)
    {
        if (range.GetBoundingRectangles(out var rects) < 0 || rects is null)
        {
            return CaretResult.NotFound;
        }

        return ParseFirstRect(rects);
    }

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

            if (double.IsNaN(left) || double.IsNaN(top) ||
                double.IsInfinity(left) || double.IsInfinity(top) ||
                height <= 0)
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
                // Isolate item failures: an exception escaping a single work item
                // must not kill the loop, or every later request would silently
                // time out for the rest of the session.
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    _logger.Error("UI Automation work item failed.", ex);
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Queue disposed during shutdown.
        }
        catch (Exception ex)
        {
            _logger.Error("UI Automation worker loop terminated unexpectedly.", ex);
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

        // The worker may be stuck in a hung COM call; it is a background thread and
        // will not block process exit regardless.
        _worker.Join(TimeSpan.FromMilliseconds(500));
        _queue.Dispose();
    }
}
