namespace Phoenix.Controls.Hub.WinUI.Services;

// UI-thread marshalling abstraction. Track 4 services receive bus / WS / log
// callbacks on background threads and need to fan them onto the dispatcher
// before raising contract events that downstream panel bindings observe.
//
// Track 1 left Hub.WinUI as a placeholder net8.0-windows project without
// Microsoft.WindowsAppSDK, so DispatcherQueue is not yet referenceable here.
// Track 2's exe migration adds the SDK and implements this with
//   queue.TryEnqueue(() => work());
//
// Test seam: a synchronous "run on caller thread" implementation suffices
// for unit tests of LiveFeedSource / ChatSource / SystemLogSource.
public interface IUiDispatcher
{
    void Post(Action work);
}
