namespace Phoenix.Controls.Hub.WinUI.Services;

// UI-thread marshalling abstraction. Services receive bus / WS / log
// callbacks on background threads and need to fan them onto the dispatcher
// before raising contract events that downstream panel bindings observe.
//
// The exe migration adds Microsoft.WindowsAppSDK and implements this with
//   queue.TryEnqueue(() => work());
//
// Test seam: a synchronous "run on caller thread" implementation suffices
// for unit tests of LiveFeedSource / ChatSource / SystemLogSource.
public interface IUiDispatcher
{
    void Post(Action work);
}
