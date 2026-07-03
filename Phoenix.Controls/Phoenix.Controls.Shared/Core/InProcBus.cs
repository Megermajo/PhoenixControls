using System;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Shared.Core
{
    /// <summary>
    /// Process-local bridge to the Hub's Bus, so Architect (which
    /// can't ProjectReference Hub) can publish + subscribe to bus messages
    /// without paying the localhost-WebSocket round trip — post-T15 the
    /// Architect runtime is hosted inside Hub.WinUI's process, yet every
    /// MACRO_REQUEST / LOGIC_RELOAD / DEBUG_NODE_EXEC round-trip flows
    /// in-process-JSON → localhost TCP → Hub bus → JSON → in-process
    /// callback. The in-proc bridge keeps the same wire schema
    /// (<see cref="BusMessage"/>) but skips serialization + the loopback hop.
    /// </summary>
    public interface IInProcBusBridge
    {
        /// <summary>Publish a message into the bus. Same semantics as
        /// the WebSocket SendAsync — broadcast routing happens inside the
        /// Bus implementation; this method just hands the envelope in.</summary>
        Task PublishAsync(BusMessage msg);

        /// <summary>Subscribe to bus messages. Handlers are dispatched on
        /// the bus's existing thread-pool worker — Architect-side wrapping
        /// (e.g. SynchronizationContext.Post) is the caller's job, mirroring
        /// the WebSocket DispatchToUi path.</summary>
        void Subscribe(Action<BusMessage> handler);

        /// <summary>Drop a previously-registered handler.</summary>
        void Unsubscribe(Action<BusMessage> handler);
    }

    /// <summary>
    /// Static registry slot for the in-process bus bridge. Hub registers its
    /// implementation when <c>Bus.Instance</c> is first accessed; Architect
    /// (and any future in-process consumer) reads through <see cref="Instance"/>.
    /// Null until Hub has been touched — callers must null-check and fall
    /// back to their out-of-process transport for the rare case (headless
    /// tests, standalone Architect future variant) where Hub isn't loaded.
    /// </summary>
    public static class InProcBus
    {
        private static IInProcBusBridge? _instance;

        /// <summary>Current bridge, or null when Hub hasn't registered yet.</summary>
        public static IInProcBusBridge? Instance => _instance;

        /// <summary>True iff a Hub bridge is registered.</summary>
        public static bool IsRegistered => _instance is not null;

        /// <summary>Hub-side hook. Replaces any prior registration.</summary>
        public static void Register(IInProcBusBridge bridge) => _instance = bridge;

        /// <summary>Test / shutdown hook. Clears the slot.</summary>
        public static void Unregister(IInProcBusBridge bridge)
        {
            if (ReferenceEquals(_instance, bridge)) _instance = null;
        }
    }
}
