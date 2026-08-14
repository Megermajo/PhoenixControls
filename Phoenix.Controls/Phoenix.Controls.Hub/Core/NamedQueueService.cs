using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // NamedQueueService — the Hub runtime of a NAMED queue, i.e. the persistent half
    // of the generalised Queue.* node band.
    //
    // WHY IT EXISTS AS ITS OWN SERVICE. Two completely different callers mutate the
    // same line and both must produce identical observable effects:
    //
    //   * the script commands in ScriptManager.Queue.cs (queue.push / pop / length /
    //     clear / position / remove / list with a Name), and
    //   * the User-Management viewer queue's chat verbs (!join / !leave / !position /
    //     !<queue> next|pick|remove|clear).
    //
    // The viewer queue is not "like" a named queue — it IS one, so !join and
    // queue.push(login, display, "viewers", weight) have to be the same write, raise
    // the same Queue.OnChanged event, and leave the same overlay state. Putting the
    // mutation, the event raise and the change notification here is what makes that
    // true by construction rather than by two code paths agreeing today.
    //
    // Stateless over the DB, exactly like CountersService: the open "Queues" table is
    // the single source of truth and nothing is cached, because db.* scripts write the
    // same table. The only in-memory state is the once-ever table-ensure latch.
    //
    // The LEGACY unnamed queue is NOT here. Queue.* with a blank Name stays on the
    // pipe-delimited Var global._event_queue in ScriptManager.Queue.cs, because live
    // scripts read that var directly and moving it would regress them.
    public sealed class NamedQueueService
    {
        private readonly DB _db;

        public NamedQueueService(DB db) => _db = db ?? throw new ArgumentNullException(nameof(db));

        private static NamedQueueService? _instance;
        private static readonly object _instanceGate = new();
        public static NamedQueueService Instance
        {
            get
            {
                var i = _instance;
                if (i != null) return i;
                lock (_instanceGate) return _instance ??= new NamedQueueService(DB.Instance);
            }
        }

        // ── Table ensure (lazy, once) ───────────────────────────────────────
        // NOT wired into HubBootstrapper on purpose: the Queue.* nodes work with every
        // pre-build tool switched off, so the store has to come up on first use rather
        // than on a tool's InitializeAsync. The gate is a SemaphoreSlim (not a lock)
        // because the work inside it is async, and the flag is re-checked inside so a
        // burst of concurrent first pushes issues exactly one CREATE TABLE.
        private readonly SemaphoreSlim _ensureGate = new(1, 1);
        private volatile bool _ensured;

        private async Task EnsureTableAsync()
        {
            if (_ensured) return;
            await _ensureGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_ensured) return;
                await _db.EnsureQueuesTableAsync().ConfigureAwait(false);
                _ensured = true;
            }
            finally { _ensureGate.Release(); }
        }

        // ── Seams ───────────────────────────────────────────────────────────
        /// <summary>Raises a Queue.OnChanged script event (wired in RegisterQueueCommands,
        /// which is the only place that can reach the script dispatcher).</summary>
        public Action<string, IReadOnlyDictionary<string, string>>? RaiseScriptEvent { get; set; }

        /// <summary>Fires after any mutation, carrying the queue name. The
        /// User-Management panel and the queue's overlay publish both ride this, so a
        /// script-driven queue.push shows up on the panel and on stream exactly like a
        /// chat !join does.</summary>
        public event EventHandler<string>? QueueChanged;

        // ── Mutations ───────────────────────────────────────────────────────

        /// <summary>Appends an entry and returns the queue's new length. Duplicates are
        /// NOT rejected — dedup is the caller's policy (the viewer queue refuses a second
        /// !join; a script work-queue may legitimately push the same id twice).</summary>
        public async Task<int> PushAsync(string queue, string entry, string payload, long priority)
        {
            await EnsureTableAsync().ConfigureAwait(false);
            int len = await _db.QueuePushAsync(queue ?? "", entry ?? "", payload ?? "", priority).ConfigureAwait(false);
            AfterChange(queue ?? "", entry ?? "", "push", len);
            return len;
        }

        /// <summary>Removes and returns the front entry, or null when the queue is empty.</summary>
        public async Task<QueueEntry?> PopAsync(string queue)
        {
            await EnsureTableAsync().ConfigureAwait(false);
            var head = await _db.QueuePopAsync(queue ?? "").ConfigureAwait(false);
            if (head is null) return null;
            int len = await _db.QueueLengthAsync(queue ?? "").ConfigureAwait(false);
            AfterChange(queue ?? "", head.Entry, "pop", len);
            return head;
        }

        /// <summary>Removes the front-most row matching <paramref name="entry"/> and
        /// returns it, or null when it was not queued.</summary>
        public async Task<QueueEntry?> RemoveAsync(string queue, string entry)
        {
            await EnsureTableAsync().ConfigureAwait(false);
            var hit = await _db.QueueRemoveAsync(queue ?? "", entry ?? "").ConfigureAwait(false);
            if (hit is null) return null;
            int len = await _db.QueueLengthAsync(queue ?? "").ConfigureAwait(false);
            AfterChange(queue ?? "", hit.Entry, "remove", len);
            return hit;
        }

        /// <summary>Empties the queue. Always fires the change side-effects, even when it
        /// was already empty: a widget or a graph watching for "the line is gone" should
        /// not have to care whether the last entry left a moment earlier.</summary>
        public async Task ClearAsync(string queue)
        {
            await EnsureTableAsync().ConfigureAwait(false);
            await _db.QueueClearAsync(queue ?? "").ConfigureAwait(false);
            AfterChange(queue ?? "", "", "clear", 0);
        }

        // ── Reads (no events — a read changes nothing) ──────────────────────

        public async Task<int> LengthAsync(string queue)
        {
            await EnsureTableAsync().ConfigureAwait(false);
            return await _db.QueueLengthAsync(queue ?? "").ConfigureAwait(false);
        }

        /// <summary>1-based place of an entry, or 0 when it is not queued.</summary>
        public async Task<int> PositionAsync(string queue, string entry)
        {
            await EnsureTableAsync().ConfigureAwait(false);
            return await _db.QueuePositionAsync(queue ?? "", entry ?? "").ConfigureAwait(false);
        }

        public async Task<List<QueueEntry>> ListAsync(string queue)
        {
            await EnsureTableAsync().ConfigureAwait(false);
            return await _db.QueueListAsync(queue ?? "").ConfigureAwait(false);
        }

        // ── Change side-effects ─────────────────────────────────────────────

        // Synchronous: the panel notification is an event raise and the script-event
        // raise is fire-and-forget through its own seam, so nothing here awaits.
        private void AfterChange(string queue, string entry, string action, int length)
        {
            RaiseQueueOnChanged(queue, entry, action, length);
            SafeEvent.Raise(QueueChanged, this, queue, "NamedQueueService", "QueueChanged");
        }

        /// <summary>
        /// Builds and raises the Queue.OnChanged vars. Shared by every mutation above and
        /// exposed to ScriptManager.Queue.cs so the LEGACY unnamed queue raises the exact
        /// same event shape with an empty Queue name — one token set, one place, which is
        /// what keeps the exporter arm, AutocompleteScopeBuilder and
        /// VarChainAnalyzer.ResultEmitterMap honest about what a run actually binds.
        /// </summary>
        internal void RaiseQueueOnChanged(string queue, string entry, string action, int length)
        {
            var raise = RaiseScriptEvent;
            if (raise == null) return;
            try
            {
                var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["event.queue"] = queue ?? "",
                    ["event.entry"] = entry ?? "",
                    ["event.action"] = action ?? "",
                    ["event.length"] = length.ToString(CultureInfo.InvariantCulture),
                };
                raise("Queue.OnChanged", vars);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("NamedQueueService", "RaiseScriptEvent(Queue.OnChanged) failed", ex);
            }
        }
    }
}
