using System;
using System.Collections.Generic;

namespace Phoenix.Controls.Architect.Core
{
    /// <summary>
    /// Subscription-scope helper for Architect windows that wire handlers to
    /// long-lived publishers (typically <see cref="ArchitectBusClient.Instance"/>).
    /// Owners declare a scope alongside the form, call <see cref="Subscribe"/>
    /// for every event they hook, and dispose the scope on close — every
    /// recorded unsubscribe runs in reverse order, no per-handler manual
    /// detach needed.
    ///
    /// Without a scope, secondary windows that subscribe to the singleton
    /// bus client would accumulate stale handlers as they're opened and
    /// closed: closing one window leaves its lambda in the multicast list,
    /// and every future Invoke from the bus thread raises
    /// ObjectDisposedException, masking the next subscriber. The
    /// MainForm-only world dodged this because it lives for the process
    /// lifetime, but anything else that subscribes (debug windows,
    /// sub-graph editors, etc.) needs a clean detach path.
    ///
    /// Scope is light by design — no timer registry, no UI-thread marshal
    /// helper. Architect's call sites already do those steps inline; this
    /// only owns the subscribe/unsubscribe pair so a single Dispose call
    /// detaches everything in LIFO order. Idempotent: a second Dispose is
    /// a no-op. Late subscriptions after Dispose are silently dropped (the
    /// publisher never sees the +=) so a closing window racing a background
    /// event-fire path can't reattach.
    ///
    /// Architect-local on purpose — Hub has the more comprehensive
    /// <c>WindowDispatcher</c> for its dock-content lifetime; per the
    /// pillar-isolation rule the two evolve independently.
    /// </summary>
    public sealed class BusSubscriptionScope : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<Action> _unsubscribers = new();
        private bool _disposed;

        /// <summary>
        /// Wires <paramref name="subscribe"/> immediately and remembers
        /// <paramref name="unsubscribe"/> to fire on <see cref="Dispose"/>.
        /// Returns a handle so callers can detach the single subscription
        /// early; disposing the handle removes its unsubscribe from the
        /// scope's tracked list before invoking it, so a later
        /// scope-Dispose doesn't double-fire it. If the scope has already
        /// been disposed the call is a no-op and a no-op handle is returned.
        /// </summary>
        public IDisposable Subscribe(Action subscribe, Action unsubscribe)
        {
            if (subscribe == null)   throw new ArgumentNullException(nameof(subscribe));
            if (unsubscribe == null) throw new ArgumentNullException(nameof(unsubscribe));

            lock (_gate)
            {
                if (_disposed) return NoopHandle.Instance;
                // Subscribe inside the lock so a concurrent Dispose can't
                // race past the _disposed check and orphan a handler.
                subscribe();
                _unsubscribers.Add(unsubscribe);
                return new SubHandle(this, unsubscribe);
            }
        }

        /// <summary>
        /// Number of live (still-scoped) subscriptions. Test-friendly
        /// accessor — production code shouldn't gate on this.
        /// </summary>
        public int LiveCount
        {
            get { lock (_gate) return _unsubscribers.Count; }
        }

        public void Dispose()
        {
            List<Action> toRun;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                toRun = new List<Action>(_unsubscribers);
                _unsubscribers.Clear();
            }
            // LIFO so a "subscribe-A, subscribe-B-which-depends-on-A"
            // shape detaches B before A. Unsubscribe handlers are not
            // expected to throw, but if one does we still want the rest
            // to run so the chain is fully cleared.
            for (int i = toRun.Count - 1; i >= 0; i--)
            {
                try { toRun[i](); } catch { /* unsubscribers must not throw */ }
            }
        }

        private sealed class SubHandle : IDisposable
        {
            private readonly BusSubscriptionScope _owner;
            private Action? _unsubscribe;

            public SubHandle(BusSubscriptionScope owner, Action unsubscribe)
            {
                _owner = owner;
                _unsubscribe = unsubscribe;
            }

            public void Dispose()
            {
                var u = _unsubscribe;
                if (u == null) return;
                _unsubscribe = null;

                lock (_owner._gate)
                {
                    if (_owner._disposed) return; // scope already swept it
                    _owner._unsubscribers.Remove(u);
                }
                try { u(); } catch { }
            }
        }

        private sealed class NoopHandle : IDisposable
        {
            public static readonly NoopHandle Instance = new NoopHandle();
            public void Dispose() { }
        }
    }
}
