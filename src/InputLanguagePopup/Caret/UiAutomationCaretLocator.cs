using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using InputLanguagePopup.Diagnostics;
using InputLanguagePopup.Interop;

namespace InputLanguagePopup.Caret;

/// <summary>
/// Strategy 2: the caret via COM UI Automation TextPattern2.GetCaretRange.
///
/// UI Automation calls can block if the target provider is slow or hung, and
/// must never run on the keyboard-hook thread. All work is marshalled to a single
/// long-lived MTA worker thread and every request is bounded by a timeout — if a
/// provider hangs, the result is abandoned rather than blocking the indicator.
/// </summary>
public sealed class UiAutomationCaretLocator : ICaretPositionSource, IDisposable
{
    private readonly Logger _logger;
    private readonly int _timeoutMs;
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _worker;
    private volatile bool _disposed;

    // Owned by the worker thread only.
    private IUIAutomation? _automation;

    public UiAutomationCaretLocator(Logger logger, int timeoutMs = 150)
    {
        _logger = logger;
        _timeoutMs = timeoutMs;

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "UIAutomationCaretWorker",
        };
        // MTA: the UIA COM client does not require a message pump there, and our
        // timeout can safely abandon a hung call.
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    public CaretResult TryLocate(IntPtr foregroundWindow, uint threadId)
    {
        if (_disposed)
        {
            return CaretResult.NotFound;
        }

        var done = new ManualResetEventSlim(false);
        var result = CaretResult.NotFound;

        try
        {
            _queue.Add(() =>
            {
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
                    done.Set();
                }
            });
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to enqueue UI Automation work.", ex);
            done.Dispose();
            return CaretResult.NotFound;
        }

        if (!done.Wait(_timeoutMs))
        {
            _logger.Warn($"UI Automation caret lookup timed out after {_timeoutMs} ms.");
            return CaretResult.NotFound;
        }

        // NB: 'done' is intentionally not disposed on either outcome.
        // ManualResetEventSlim.Dispose is not thread-safe against a concurrent
        // Set(): even after Wait returns, the worker may still be inside Set().
        // The GC finalizes the (rarely allocated) kernel handle.
        return result;
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

            if (textPattern.GetCaretRange(out _, out range) < 0 || range is null)
            {
                return CaretResult.NotFound;
            }

            if (range.GetBoundingRectangles(out var rects) < 0 || rects is null)
            {
                return CaretResult.NotFound;
            }

            return ParseFirstRect(rects);
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

        _worker.Join(TimeSpan.FromMilliseconds(500));
        _queue.Dispose();
    }
}
