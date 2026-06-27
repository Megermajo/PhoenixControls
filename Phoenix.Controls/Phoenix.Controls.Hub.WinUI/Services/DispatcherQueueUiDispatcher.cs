using System;
using Microsoft.UI.Dispatching;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.WinUI.Services;

// IUiDispatcher implementation that marshals Action callbacks onto a
// WinAppSDK DispatcherQueue. Bus / WS / log subscriptions fire on background
// threads; the source services use this to fan their contract events back
// to the UI thread before raising bindings.
public sealed class DispatcherQueueUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue;

    public DispatcherQueueUiDispatcher(DispatcherQueue queue)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    public void Post(Action work)
    {
        if (work is null) return;
        // Wrap the dispatched callback in try/catch — every panel-side event
        // bridge funnels through this Post (bus broadcasts, log fan-out,
        // chat / livefeed / scripthost rows). If any one of those callbacks
        // throws and we don't catch it here, the exception escapes onto the
        // dispatcher thread's unhandled-exception path and tears Hub down.
        if (!_queue.TryEnqueue(() => SafeRun(work)))
        {
            // Enqueue can fail if the dispatcher has shut down. Run inline
            // on the calling thread as a best-effort fallback so test seams
            // and shutdown-time callers still see the work happen.
            SafeRun(work);
        }
    }

    private static void SafeRun(Action work)
    {
        try { work(); }
        catch (Exception ex)
        {
            GlobalLogger.Error("DispatcherQueueUiDispatcher", "UI callback faulted", ex);
        }
    }
}
