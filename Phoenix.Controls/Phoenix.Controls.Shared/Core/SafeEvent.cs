using System;
using System.ComponentModel;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Shared.Core
{
    /// <summary>
    /// Centralised crash-proof multicast-event raising.
    ///
    /// A bare <c>SomeEvent?.Invoke(...)</c> stops at the first subscriber that
    /// throws and propagates the exception to the publisher — which is frequently
    /// a WebSocket receive pump, a <see cref="System.IO.FileSystemWatcher"/>
    /// callback, or the UI dispatcher. One faulty subscriber therefore kills the
    /// pump / dispatcher / process. The audit (2026-06-03) found this pattern
    /// ~20× across every pillar and identified it as the single biggest cause of
    /// the "regular crashes".
    ///
    /// <see cref="SafeEvent"/> invokes each subscriber under its own try/catch so
    /// one faulting handler cannot unwind into the publisher; faults route to
    /// <see cref="GlobalLogger.Error"/>. This standardises the per-handler pattern
    /// already used by <c>LayerDocument.RaiseCanExecuteChanged</c> and
    /// <c>GlobalLogger.DispatchOnError</c> so call sites stop re-implementing it.
    ///
    /// Typed overloads (no reflection / DynamicInvoke) keep hot paths fast and
    /// preserve normal exception semantics. Deliberately NO process-wide re-entry
    /// guard — that is specific to GlobalLogger's own Error→handler→Error recursion
    /// and would wrongly suppress legitimate nested raises of unrelated events.
    ///
    /// Pass the event field directly (legal only from inside the declaring type):
    /// <code>SafeEvent.Raise(OnChanged, "ScriptRegistry", "OnChanged");</code>
    /// </summary>
    public static class SafeEvent
    {
        public static void Raise(Action? handlers, string source, string label)
        {
            if (handlers is null) return;
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((Action)d)(); }
                catch (Exception ex) { GlobalLogger.Error(source, label, ex); }
            }
        }

        public static void Raise<T>(Action<T>? handlers, T arg, string source, string label)
        {
            if (handlers is null) return;
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((Action<T>)d)(arg); }
                catch (Exception ex) { GlobalLogger.Error(source, label, ex); }
            }
        }

        public static void Raise<T1, T2>(Action<T1, T2>? handlers, T1 a, T2 b, string source, string label)
        {
            if (handlers is null) return;
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((Action<T1, T2>)d)(a, b); }
                catch (Exception ex) { GlobalLogger.Error(source, label, ex); }
            }
        }

        public static void Raise(EventHandler? handlers, object? sender, EventArgs args, string source, string label)
        {
            if (handlers is null) return;
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((EventHandler)d)(sender, args); }
                catch (Exception ex) { GlobalLogger.Error(source, label, ex); }
            }
        }

        public static void Raise<TArgs>(EventHandler<TArgs>? handlers, object? sender, TArgs args, string source, string label)
        {
            if (handlers is null) return;
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((EventHandler<TArgs>)d)(sender, args); }
                catch (Exception ex) { GlobalLogger.Error(source, label, ex); }
            }
        }

        public static void RaisePropertyChanged(PropertyChangedEventHandler? handlers, object? sender, PropertyChangedEventArgs args, string source, string label)
        {
            if (handlers is null) return;
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((PropertyChangedEventHandler)d)(sender, args); }
                catch (Exception ex) { GlobalLogger.Error(source, label, ex); }
            }
        }
    }
}
