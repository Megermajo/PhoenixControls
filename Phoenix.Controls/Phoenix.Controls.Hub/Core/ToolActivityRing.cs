using System;
using System.Collections.Generic;
using System.Globalization;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // ToolActivityRing — the in-memory activity source the Pre-Build tool pages
    // render in their ACTIVITY card. Services record into it from wherever the
    // thing actually happened; the panel VMs read it on the dispatcher and
    // project the rows into their feed.
    //
    // ══════════════════ SESSION-SCOPED AND IN-MEMORY. NOT HISTORY. ══════════════════
    //
    // Nothing here is persisted. The ring is EMPTY after every Hub restart, it is
    // bounded at RingCapacity entries per tool, and the oldest entry is dropped
    // silently once that cap is reached. There is no file, no table, no replay
    // and no recovery.
    //
    // That is a deliberate design point, not a gap to be papered over: it is why
    // every feed built on this type must label its scope "this session". Timer
    // and Giveaway are different — their feeds replay from the TimerActivity /
    // GiveawayActivity SQLite tables and legitimately survive a restart. Automod
    // is different again: it has its own persisted AutomodLog system table and
    // MUST NOT be migrated onto this ring. A feed reading from here that presents
    // itself as durable history is lying to the streamer.
    //
    // WHY IT LIVES IN Hub/Core, not in Hub.WinUI: Phoenix.Controls.Tests cannot
    // construct any Hub.WinUI ViewModel — even `typeof(SomeViewModel)` crashes
    // the test host — so a feed source declared next to the UI has no route to
    // coverage. Declared here, both the recording side and the read side are
    // ordinary library calls the existing suite can exercise directly.
    //
    // THREADING: services record from background threads (chat handlers, timers,
    // sweeps, HTTP callbacks) while the UI reads on the dispatcher, so every
    // access to the backing store is taken under one lock. Changed is raised
    // OUTSIDE that lock, and each subscriber is invoked in its own try/catch, so
    // one faulting handler neither deadlocks a recorder nor starves the other
    // subscribers — the same posture Bus.OnMessageReceived uses.
    //
    // ⚠ Changed is a STATIC event. A ViewModel that subscribes and never
    // unsubscribes is rooted for the life of the process, which keeps a disposed
    // panel — and everything it closes over — alive. Every subscriber MUST
    // detach in Dispose:
    //
    //     ToolActivityRing.Changed += OnRingChanged;      // ctor
    //     ToolActivityRing.Changed -= OnRingChanged;      // Dispose
    public static class ToolActivityRing
    {
        /// <summary>
        /// Entries retained per tool. Matches the default page size of the
        /// persisted Timer/Giveaway activity reads, so a session feed and a
        /// durable feed show a comparable depth.
        /// </summary>
        public const int RingCapacity = 100;

        private readonly struct Entry
        {
            public Entry(DateTime utc, string kind, string message)
            {
                Utc = utc;
                Kind = kind;
                Message = message;
            }

            public DateTime Utc { get; }
            public string Kind { get; }
            public string Message { get; }
        }

        private static readonly object Gate = new();

        // Tool keys are compared case-insensitively so "Loyalty" and "loyalty"
        // cannot end up as two separate rings.
        private static readonly Dictionary<string, Queue<Entry>> Rings =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Raised after a successful <see cref="Record"/>. The payload is the
        /// tool key that changed — compare it with
        /// <see cref="StringComparer.OrdinalIgnoreCase"/>, since the ring itself
        /// treats keys that way.
        ///
        /// Handlers are called on the RECORDING thread, which is usually a
        /// background thread. A ViewModel handler must marshal to the dispatcher
        /// before touching an observable collection.
        ///
        /// See the type remarks: subscribers must unsubscribe in Dispose.
        /// </summary>
        public static event EventHandler<string>? Changed;

        /// <summary>
        /// Append one line to <paramref name="tool"/>'s ring, stamped
        /// <see cref="DateTime.UtcNow"/>, and notify subscribers.
        ///
        /// <paramref name="kind"/> is the short token the feed renders in its
        /// 38px column (ADD / PLAY / DENY / FIRE / …); a blank kind is stored as
        /// "INF". A blank <paramref name="tool"/> is a no-op — there is no ring
        /// to route it to and no subscriber could match it.
        /// </summary>
        /// <param name="tool">Tool key, e.g. "Counters". Case-insensitive.</param>
        /// <param name="kind">Short kind token. Blank becomes "INF".</param>
        /// <param name="message">The finished, human-readable sentence.</param>
        public static void Record(string tool, string kind, string message)
        {
            string key = (tool ?? string.Empty).Trim();
            if (key.Length == 0) return;

            string k = (kind ?? string.Empty).Trim();
            if (k.Length == 0) k = "INF";

            var entry = new Entry(DateTime.UtcNow, k, message ?? string.Empty);

            lock (Gate)
            {
                if (!Rings.TryGetValue(key, out var ring))
                {
                    ring = new Queue<Entry>(RingCapacity);
                    Rings[key] = ring;
                }

                ring.Enqueue(entry);

                // Bounded: drop oldest. A while-loop rather than a single
                // Dequeue so a lowered capacity would still drain correctly.
                while (ring.Count > RingCapacity) ring.Dequeue();
            }

            RaiseChanged(key);
        }

        /// <summary>
        /// The most recent entries for <paramref name="tool"/>, NEWEST FIRST —
        /// the same order the persisted Timer/Giveaway activity reads return, so
        /// a feed can render either source without re-sorting.
        ///
        /// Times are formatted <c>HH:mm:ss</c> in LOCAL time with the invariant
        /// culture, matching the Giveaway feed. Returns an empty list for an
        /// unknown tool, a blank tool key, or a non-positive limit.
        /// </summary>
        /// <param name="tool">Tool key. Case-insensitive.</param>
        /// <param name="limit">Maximum rows to return.</param>
        public static IReadOnlyList<(string Time, string Kind, string Message)> Recent(
            string tool, int limit = RingCapacity)
        {
            string key = (tool ?? string.Empty).Trim();
            if (key.Length == 0 || limit <= 0)
                return Array.Empty<(string, string, string)>();

            Entry[] snapshot;
            lock (Gate)
            {
                if (!Rings.TryGetValue(key, out var ring) || ring.Count == 0)
                    return Array.Empty<(string, string, string)>();

                // Copy inside the lock, format outside it: formatting is pure
                // and there is no reason to hold recorders off while it runs.
                snapshot = ring.ToArray();   // oldest → newest
            }

            int take = Math.Min(limit, snapshot.Length);
            var result = new List<(string, string, string)>(take);

            // Walk backwards to emit newest first.
            for (int i = snapshot.Length - 1; i >= snapshot.Length - take; i--)
            {
                Entry e = snapshot[i];
                result.Add((
                    e.Utc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    e.Kind,
                    e.Message));
            }

            return result;
        }

        // Raised outside the lock. Per-handler try/catch so one bad subscriber
        // cannot break the others or fault the recording service.
        private static void RaiseChanged(string toolKey)
        {
            var handler = Changed;
            if (handler is null) return;

            foreach (Delegate d in handler.GetInvocationList())
            {
                try
                {
                    ((EventHandler<string>)d)(null, toolKey);
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error("ToolActivityRing",
                        $"Changed handler threw for tool '{toolKey}'", ex);
                }
            }
        }
    }
}
