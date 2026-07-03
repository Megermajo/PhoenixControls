using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: queue.* command registrations (push/pop/length/clear).
    // Persistent, pipe-delimited string queue stored in Vars under
    // global._event_queue. The pop/length contract reads global.queue_empty
    // and global._queue_head to let scripts branch on emptiness without a
    // second DB round-trip; H38 has queue.length return the count so the
    // exporter's inline-expression form resolves to the value rather than "".
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        private void RegisterQueueCommands()
        {
            _engine.RegisterCommand("queue.push", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string eventId = bound?.GetOrDefault<string>("EventID", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string payload = bound?.GetOrDefault<string>("Payload", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                if (string.IsNullOrEmpty(eventId) && string.IsNullOrEmpty(payload)) return null;
                // H39 — RMW race: two concurrent chat scripts both reading the
                // existing queue would write back overlapping pipe-strings,
                // dropping one entry. Per-key lock is the cheapest serialization.
                var rmwLock = GetRmwLock("queue::global._event_queue");
                await rmwLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    string existing = await DB.Instance.GetVariableAsync("global._event_queue", "");
                    string entry = $"{eventId}:{payload}";
                    await _engine.SetScriptVarAsync("global._event_queue",
                        string.IsNullOrEmpty(existing) ? entry : $"{existing}|{entry}");
                }
                finally { rmwLock.Release(); }
                return null;
            });
            // queue.pop(eventIdVar, payloadVar)
            // Pops the head entry (format "eventid:payload") off the persisted
            // queue and writes the parts into the named output vars. Always sets
            // global.queue_empty so callers can branch on whether anything was
            // popped. Empty/missing args mean the caller doesn't want that part.
            _engine.RegisterCommand("queue.pop", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string eventIdVar = bound?.GetOrDefault<string>("EventIDVar", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string payloadVar = bound?.GetOrDefault<string>("PayloadVar", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);

                // queue.pop did an UNGUARDED read-modify-write on
                // global._event_queue while queue.push serializes through the same per-key
                // RMW lock. A pop racing a push (or a concurrent pop) could drop or
                // duplicate an entry. Take the same lock around the read + the queue write.
                var rmwLock = GetRmwLock("queue::global._event_queue");
                await rmwLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    string q = await DB.Instance.GetVariableAsync("global._event_queue", "");
                    bool empty = string.IsNullOrEmpty(q);
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
                        await _engine.SetScriptVarAsync("global._queue_head", head);
                        await _engine.SetScriptVarAsync("global._event_queue", string.Join("|", parts.Skip(1)));
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
                return null;
            });
            _engine.RegisterCommand("queue.length", async (args) => {
                string resultVar = _engine.CurrentBoundArgs?.GetOrDefault<string>("ResultVar", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string q = await DB.Instance.GetVariableAsync("global._event_queue", "");
                int len = string.IsNullOrEmpty(q) ? 0 : q.Split('|').Length;
                if (!string.IsNullOrEmpty(resultVar))
                    await _engine.SetScriptVarAsync(resultVar, len.ToString(CultureInfo.InvariantCulture));
                // H38 — exporter inlines `queue.length()` as an expression. Returning
                // the length as the command result lets that path resolve to the actual
                // value instead of an empty string.
                return len.ToString(CultureInfo.InvariantCulture);
            });
            _engine.RegisterCommand("queue.clear", async (args) => {
                await _engine.SetScriptVarAsync("global._event_queue", "");
                return null;
            });
        }
    }
#pragma warning restore CS1998
}
