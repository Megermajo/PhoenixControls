using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: queue.* command registrations.
    //
    // TWO STORES, ONE BAND. Every queue.* command takes an OPTIONAL trailing Name:
    //
    //   * Name ABSENT  → the LEGACY queue: a pipe-delimited string in the Var
    //     global._event_queue holding "eventid:payload" entries. Untouched and
    //     unmigrated, byte-for-byte the behaviour it always had, because live scripts
    //     read that var directly and the pop/length contract (global.queue_empty +
    //     global._queue_head) is what lets them branch on emptiness without a second
    //     DB round-trip. H38 has queue.length return the count so the exporter's
    //     inline-expression form resolves to the value rather than "".
    //   * Name PRESENT → a NAMED queue: rows in the OPEN "Queues" table, ordered by
    //     priority then insertion, served by NamedQueueService. This is the store the
    //     User-Management viewer queue uses, which is what makes !join and
    //     queue.push(login, display, "viewers", weight) literally the same write.
    //
    // The optional args are emitted ONLY when set (custom IExporterHandlers, not
    // SimpleEmitDescriptors) precisely so an existing graph's queue.push(a, b) keeps
    // emitting queue.push(a, b) — SimpleEmitHandler.Emit writes every descriptor arg
    // positionally with no omit-at-default, so appending Name to the descriptor would
    // have rewritten every shipped graph's .phx and every affected golden.
    //
    // Both stores raise the same Queue.OnChanged event through
    // NamedQueueService.RaiseQueueOnChanged (the legacy one with an empty event.queue),
    // so a graph watching the band sees one token set regardless of which store the
    // author picked.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        // The Var backing the LEGACY unnamed queue, and the per-key RMW lock key that
        // serializes read-modify-write against it. Named once so the five legacy arms
        // below cannot drift onto different keys.
        private const string LegacyQueueVar = "global._event_queue";
        private const string LegacyQueueLock = "queue::global._event_queue";

        private void RegisterQueueCommands()
        {
            // ── Seam ─────────────────────────────────────────────────────────
            // Queue.OnChanged flows back through the generic-event dispatcher with
            // pre-built vars (event.queue / event.entry / event.action / event.length).
            // The seam is an Action (NamedQueueService can't await it), so the async
            // dispatch is fired-and-forgotten HERE through AsyncErrorBoundary.SafeRunAsync
            // — faults route to the log, expected shutdown cancellation is swallowed
            // (mirrors ScriptManager.Counters.cs).
            NamedQueueService.Instance.RaiseScriptEvent = (phoenixEvent, vars) =>
            {
                _ = AsyncErrorBoundary.SafeRunAsync(
                    () => ExecuteGenericEventAsync(phoenixEvent, default, vars),
                    "NamedQueueService", $"RaiseScriptEvent({phoenixEvent})");
            };

            _engine.RegisterCommand("queue.push", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string eventId = bound?.GetOrDefault<string>("EventID", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string payload = bound?.GetOrDefault<string>("Payload", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string name = QueueName(bound, args, 2);
                if (name.Length > 0)
                {
                    // A named push with both parts blank is still a real entry — unlike the
                    // legacy queue below, where a blank "…:…" pair is indistinguishable from
                    // a formatting accident on a shared pipe-string.
                    long priority = ReadQueuePriority(bound, args, 3);
                    await NamedQueueService.Instance
                        .PushAsync(name, eventId, payload, priority).ConfigureAwait(false);
                    return null;
                }
                if (string.IsNullOrEmpty(eventId) && string.IsNullOrEmpty(payload)) return null;
                // H39 — RMW race: two concurrent chat scripts both reading the
                // existing queue would write back overlapping pipe-strings,
                // dropping one entry. Per-key lock is the cheapest serialization.
                var rmwLock = GetRmwLock(LegacyQueueLock);
                await rmwLock.WaitAsync().ConfigureAwait(false);
                int legacyLen;
                try
                {
                    string existing = await DB.Instance.GetVariableAsync(LegacyQueueVar, "");
                    string entry = $"{eventId}:{payload}";
                    string updated = string.IsNullOrEmpty(existing) ? entry : $"{existing}|{entry}";
                    await _engine.SetScriptVarAsync(LegacyQueueVar, updated);
                    legacyLen = CountLegacyEntries(updated);
                }
                finally { rmwLock.Release(); }
                NamedQueueService.Instance.RaiseQueueOnChanged("", eventId, "push", legacyLen);
                return null;
            });

            // queue.pop(eventIdVar, payloadVar, [name])
            // Pops the head entry off the addressed queue and writes the parts into the
            // named output vars. ALWAYS sets global.queue_empty — for both stores — because
            // QueuePopHandler emits one shared `{global.queue_empty} != "true"` guard for
            // the node's Done/Empty branches and must keep doing so whether or not the
            // author filled in a Name. Empty/missing var args mean the caller doesn't want
            // that part.
            _engine.RegisterCommand("queue.pop", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string eventIdVar = bound?.GetOrDefault<string>("EventIDVar", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string payloadVar = bound?.GetOrDefault<string>("PayloadVar", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string name = QueueName(bound, args, 2);

                if (name.Length > 0)
                {
                    var head = await NamedQueueService.Instance.PopAsync(name).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(eventIdVar))
                        await _engine.SetScriptVarAsync(eventIdVar, head?.Entry ?? "");
                    if (!string.IsNullOrEmpty(payloadVar))
                        await _engine.SetScriptVarAsync(payloadVar, head?.Payload ?? "");
                    // global._queue_head keeps the legacy "eventid:payload" spelling for a
                    // named pop too, so a graph that reads it does not have to know which
                    // store answered.
                    await _engine.SetScriptVarAsync("global._queue_head",
                        head is null ? "" : $"{head.Entry}:{head.Payload}");
                    await _engine.SetScriptVarAsync("global.queue_empty", head is null ? "true" : "false");
                    return null;
                }

                // queue.pop did an UNGUARDED read-modify-write on
                // global._event_queue while queue.push serializes through the same per-key
                // RMW lock. A pop racing a push (or a concurrent pop) could drop or
                // duplicate an entry. Take the same lock around the read + the queue write.
                var rmwLock = GetRmwLock(LegacyQueueLock);
                await rmwLock.WaitAsync().ConfigureAwait(false);
                bool empty;
                string poppedId = "";
                int remaining = 0;
                try
                {
                    string q = await DB.Instance.GetVariableAsync(LegacyQueueVar, "");
                    empty = string.IsNullOrEmpty(q);
                    string eventid = "";
                    string payload = "";
                    if (!empty)
                    {
                        var parts = q.Split('|');
                        string head = parts[0];
                        int colonIdx = head.IndexOf(':');
                        if (colonIdx >= 0)
                        {
                            eventid = head.Substring(0, colonIdx);
                            payload = head.Substring(colonIdx + 1);
                        }
                        else
                        {
                            eventid = head;
                        }
                        string rest = string.Join("|", parts.Skip(1));
                        await _engine.SetScriptVarAsync("global._queue_head", head);
                        await _engine.SetScriptVarAsync(LegacyQueueVar, rest);
                        poppedId = eventid;
                        remaining = CountLegacyEntries(rest);
                    }
                    else
                    {
                        await _engine.SetScriptVarAsync("global._queue_head", "");
                    }

                    if (!string.IsNullOrEmpty(eventIdVar))
                        await _engine.SetScriptVarAsync(eventIdVar, eventid);
                    if (!string.IsNullOrEmpty(payloadVar))
                        await _engine.SetScriptVarAsync(payloadVar, payload);

                    await _engine.SetScriptVarAsync("global.queue_empty", empty ? "true" : "false");
                }
                finally { rmwLock.Release(); }
                // Raised OUTSIDE the RMW lock: a Queue.OnChanged handler that pushes back
                // onto the same queue would otherwise deadlock against the lock we still
                // hold. Nothing was raised at all when the queue was already empty — a pop
                // that popped nothing changed nothing.
                if (!empty) NamedQueueService.Instance.RaiseQueueOnChanged("", poppedId, "pop", remaining);
                return null;
            });

            _engine.RegisterCommand("queue.length", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string resultVar = bound?.GetOrDefault<string>("ResultVar", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string name = QueueName(bound, args, 1);
                int len;
                if (name.Length > 0)
                {
                    len = await NamedQueueService.Instance.LengthAsync(name).ConfigureAwait(false);
                }
                else
                {
                    string q = await DB.Instance.GetVariableAsync(LegacyQueueVar, "");
                    len = CountLegacyEntries(q);
                }
                if (!string.IsNullOrEmpty(resultVar))
                    await _engine.SetScriptVarAsync(resultVar, len.ToString(CultureInfo.InvariantCulture));
                // H38 — exporter inlines `queue.length()` as an expression. Returning
                // the length as the command result lets that path resolve to the actual
                // value instead of an empty string.
                return len.ToString(CultureInfo.InvariantCulture);
            });

            _engine.RegisterCommand("queue.clear", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string name = QueueName(bound, args, 0);
                if (name.Length > 0)
                {
                    await NamedQueueService.Instance.ClearAsync(name).ConfigureAwait(false);
                    return null;
                }
                await _engine.SetScriptVarAsync(LegacyQueueVar, "");
                NamedQueueService.Instance.RaiseQueueOnChanged("", "", "clear", 0);
                return null;
            });

            // queue.position(entry, [name]) — 1-based place, 0 when not queued. Pure-data:
            // the exporter emits the call inline (ComputeInlineValue) and the engine
            // round-trips the return string into the node's Position output.
            _engine.RegisterCommand("queue.position", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string entry = StripBareQuotes(
                    bound?.GetOrDefault<string>("Entry", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                string name = QueueName(bound, args, 1);
                int pos;
                if (name.Length > 0)
                {
                    pos = await NamedQueueService.Instance.PositionAsync(name, entry).ConfigureAwait(false);
                }
                else
                {
                    string q = await DB.Instance.GetVariableAsync(LegacyQueueVar, "");
                    pos = LegacyIndexOf(q, entry) + 1;   // -1 → 0 = not queued
                }
                return pos.ToString(CultureInfo.InvariantCulture);
            });

            // queue.remove(entry, [name]) — drops the FRONT-most matching entry. Void:
            // flow continues through the node's Done output whether or not anything
            // matched, exactly like counter.delete on an unknown counter. A graph that
            // needs to know first asks queue.position.
            _engine.RegisterCommand("queue.remove", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string entry = StripBareQuotes(
                    bound?.GetOrDefault<string>("Entry", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                string name = QueueName(bound, args, 1);
                if (name.Length > 0)
                {
                    await NamedQueueService.Instance.RemoveAsync(name, entry).ConfigureAwait(false);
                    return null;
                }
                var rmwLock = GetRmwLock(LegacyQueueLock);
                await rmwLock.WaitAsync().ConfigureAwait(false);
                bool removed = false;
                int remaining = 0;
                try
                {
                    string q = await DB.Instance.GetVariableAsync(LegacyQueueVar, "");
                    int idx = LegacyIndexOf(q, entry);
                    if (idx >= 0)
                    {
                        var parts = q.Split('|').ToList();
                        parts.RemoveAt(idx);
                        string rest = string.Join("|", parts);
                        await _engine.SetScriptVarAsync(LegacyQueueVar, rest);
                        removed = true;
                        remaining = CountLegacyEntries(rest);
                    }
                }
                finally { rmwLock.Release(); }
                if (removed) NamedQueueService.Instance.RaiseQueueOnChanged("", entry, "remove", remaining);
                return null;
            });

            // queue.list([name]) — the whole line as a comma-separated string, which is
            // the engine-wide wire format for "array of strings" (see
            // ScriptManager.Array.cs), so Flow.ForEach / Array.Get consume it unchanged.
            // Pure-data, inlined by the exporter like queue.length.
            _engine.RegisterCommand("queue.list", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string name = QueueName(bound, args, 0);
                if (name.Length > 0)
                {
                    var rows = await NamedQueueService.Instance.ListAsync(name).ConfigureAwait(false);
                    return string.Join(",", rows.Select(r => r.Entry));
                }
                string q = await DB.Instance.GetVariableAsync(LegacyQueueVar, "");
                return string.Join(",", LegacyEntryIds(q));
            });
        }

        // The optional trailing Name, stripped of the quotes the exporter wraps a literal
        // in. Blank (or absent) selects the LEGACY unnamed queue — that is the whole
        // switch, in one place, so no arm can decide it differently.
        private static string QueueName(BoundArgs? bound, string[] args, int idx)
            => StripBareQuotes(bound?.GetOrDefault<string>("Name", ArgOrEmpty(args, idx)) ?? ArgOrEmpty(args, idx)).Trim();

        // Priority is Int in the manifest, but an unwired numeric pin can still arrive as
        // a raw fractional upstream value; blank means "no weighting". Reuses the Loyalty
        // ParseLongToken, exactly like ReadCounterLong.
        private static long ReadQueuePriority(BoundArgs? bound, string[] args, int idx)
        {
            string raw = StripBareQuotes(
                bound?.GetOrDefault<string>("Priority", ArgOrEmpty(args, idx)) ?? ArgOrEmpty(args, idx)).Trim();
            return raw.Length == 0 ? 0L : ParseLongToken(raw);
        }

        // ── Legacy pipe-string helpers ──────────────────────────────────────
        // The legacy store has no schema: entries are '|'-separated and each is
        // "eventid:payload" (a missing colon means the whole entry is the id). These
        // three helpers are the only readers of that shape outside the pop arm, so the
        // new Position / Remove / List verbs answer about it exactly as Pop would.

        private static int CountLegacyEntries(string raw)
            => string.IsNullOrEmpty(raw) ? 0 : raw.Count(c => c == '|') + 1;

        private static IEnumerable<string> LegacyEntryIds(string raw)
        {
            if (string.IsNullOrEmpty(raw)) yield break;
            foreach (var part in raw.Split('|'))
            {
                int colon = part.IndexOf(':');
                yield return colon >= 0 ? part.Substring(0, colon) : part;
            }
        }

        /// <summary>0-based index of the first legacy entry whose eventid matches, or -1.
        /// Case-insensitive, matching the COLLATE NOCASE the named store uses — the two
        /// halves of one node must not disagree about whether "Alice" is "alice".</summary>
        private static int LegacyIndexOf(string raw, string entry)
        {
            if (string.IsNullOrEmpty(raw)) return -1;
            int i = 0;
            foreach (var id in LegacyEntryIds(raw))
            {
                if (string.Equals(id, entry ?? "", StringComparison.OrdinalIgnoreCase)) return i;
                i++;
            }
            return -1;
        }
    }
#pragma warning restore CS1998
}
