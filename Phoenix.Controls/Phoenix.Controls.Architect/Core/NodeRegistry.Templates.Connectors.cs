using System.Collections.Generic;
using System.Drawing;
using Phoenix.Controls.Shared.Localization;

namespace Phoenix.Controls.Architect.Core
{
    // Connectors carve. Bundles three small cross-cutting
    // bands plus one stray Flow Control template that lives between
    // the Process band and the Databank band:
    //   * BUS / IPC      — Bus.Send / Bus.Broadcast (CadetBlue connectors).
    //   * QUEUE          — Queue.Push / Pop / Length / Clear (DarkCyan,
    //     pipe-string-backed under the hood per the M21 tooltip note).
    //   * PROCESS        — Process.Spawn / Terminate / Entry / Exit. The
    //     Process.Host / Session.* legacy notes preserved as the band
    //     header above the block; the runtime is unified on Spawn.
    //   * Flow.ForEach   — registered under category "Flow Control" but
    //     authored after the PROCESS band rather than inside the band
    //     block. Carved here for proximity-of-source rather than category.
    public static partial class NodeRegistry
    {
        private static void RegisterConnectorTemplates()
        {
            // ── BUS / IPC ────────────────────────────────────────────────
            AddTemplate("Bus.Send",      "Bus", Color.CadetBlue,
                Localizer.T("architect.node.bubble.bus_send"),
                new[] { ("Flow", ColExec), ("Target", ColString), ("Type", ColString), ("Payload", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("Bus.Broadcast", "Bus", Color.CadetBlue,
                Localizer.T("architect.node.bubble.bus_broadcast"),
                new[] { ("Flow", ColExec), ("Type", ColString), ("Payload", ColString) },
                new[] { ("Done", ColExec) });

            // ── QUEUE ────────────────────────────────────────────────────
            // TWO STORES, ONE BAND. Every node here carries an optional trailing
            // Name pin:
            //   * Name EMPTY → the legacy unnamed queue, a Var pipe-string under
            //     the hood (one var, '|' separated "eventid:payload" entries).
            //     Naming the backing store helps users understand persistence +
            //     cross-script visibility. Behaviour is exactly what it always was.
            //   * Name FILLED → a persistent NAMED queue: rows in the OPEN "Queues"
            //     databank table, ordered by Priority then arrival, readable from
            //     db.* like the Counters / Quotes tables.
            //
            // The User-Management viewer queue IS one of those named queues, so its
            // !join is the same write as Queue.Push with that queue's name — which is
            // what makes the tool reachable from a graph without a parallel node
            // namespace. Change nothing about the pins without reading the
            // omit-when-unset emission rule in ExporterRegistry.Handlers2.cs: the
            // trailing pins are emitted ONLY when set, which is what keeps every
            // pre-existing graph exporting byte-identically.
            AddTemplate("Queue.Push",   "Queue", Color.DarkCyan,
                Localizer.T("architect.node.bubble.queue_push"),
                new[] { ("Flow", ColExec), ("EventID", ColString), ("Payload", ColString),
                        ("Name", ColString), ("Priority", ColNumber) },
                new[] { ("Done", ColExec) });

            AddTemplate("Queue.Pop",    "Queue", Color.DarkCyan,
                Localizer.T("architect.node.bubble.queue_pop"),
                new[] { ("Flow", ColExec), ("Name", ColString) },
                new[] { ("Done", ColExec), ("EventID", ColString), ("Payload", ColString), ("Empty", ColExec) });

            AddTemplate("Queue.Length", "Queue", Color.DarkCyan,
                Localizer.T("architect.node.bubble.queue_length"),
                new[] { ("Name", ColString) },
                new[] { ("Count", ColNumber) });

            AddTemplate("Queue.Clear",  "Queue", Color.DarkCyan,
                Localizer.T("architect.node.bubble.queue_clear"),
                new[] { ("Flow", ColExec), ("Name", ColString) },
                new[] { ("Done", ColExec) });

            // ── QUEUE — the members the Name generalisation made possible ──
            // A single global pipe-string only ever needed push / pop / length /
            // clear. Once a queue can be addressed BY NAME and holds one row per
            // entry, "where am I in the line", "take me out of it" and "print the
            // line" become answerable — and they are exactly the three verbs the
            // viewer-queue tool needs, so they exist here rather than as a private
            // tool surface. All three serve BOTH stores.
            // Bubbles resolve through Localizer.T, matching the four members above:
            // the tooltip convention follows the BAND, and Connectors/Queue is a
            // localized band (the TOOL bands — Counters, Quotes, Loyalty, Users — are
            // hardcoded English instead).
            AddTemplate("Queue.Position", "Queue", Color.DarkCyan,
                Localizer.T("architect.node.bubble.queue_position"),
                new[] { ("Entry", ColString), ("Name", ColString) },
                new[] { ("Position", ColNumber) });

            AddTemplate("Queue.Remove", "Queue", Color.DarkCyan,
                Localizer.T("architect.node.bubble.queue_remove"),
                new[] { ("Flow", ColExec), ("Entry", ColString), ("Name", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("Queue.List", "Queue", Color.DarkCyan,
                Localizer.T("architect.node.bubble.queue_list"),
                new[] { ("Name", ColString) },
                new[] { ("List", ColList) });

            // ── QUEUE — event root (category "Events" — output-only) ──────
            // MIRRORS Counter.OnChanged: null inputs, Flow output first. Category
            // "Events" makes it a script entry point; the exporter emits
            // on_event(Queue.OnChanged) via ProcessEventNode's trigger-switch
            // fallback, matching the phoenixEvent string NamedQueueService raises.
            // Fires for BOTH stores — a legacy unnamed mutation reports an empty
            // Queue name — so a graph can watch a queue it did not itself write,
            // which is what closes the parity loop for the viewer-queue tool: a
            // chat !join and a script queue.push raise the identical event.
            AddTemplate("Queue.OnChanged", "Events", Color.DarkCyan,
                Localizer.T("architect.node.bubble.queue_onchanged"),
                null,
                new[] { ("Flow", ColExec), ("Queue", ColString), ("Entry", ColString),
                        ("Action", ColString), ("Length", ColNumber) });

            // ── PROCESS — live, self-contained mini-script ──────────────
            //
            // A Process is its own canvas of event triggers (Schedule, on_chat,
            // on_event, …). Process.Start launches a new INSTANCE whose triggers
            // go live and stay live until Process.Stop tears that instance down —
            // it runs like a normal .phx, for as long as the instance is started,
            // with no run-duration limit. Multiple instances of one process run
            // concurrently, each carrying its own start params (read in the body
            // as {param.<name>}). The process owns an internal graph with
            // Process.Entry (the "on start" trigger + param declarations) and
            // Process.Exit (the "on stop" trigger).
            //
            // The old fire-and-forget Process.Spawn / Process.Terminate are kept
            // registered for back-compat (so legacy .phxg still load + export and
            // the coverage tests stay green) but HiddenFromPalette — migration
            // (ProcessNodeMigration) rewrites placed Spawn→Start / Terminate→Stop.
            AddTemplate("Process.Start", "Process", Color.DimGray,
                "Launches a new instance of a Process. Its event triggers (Schedule, on_chat, on_event, etc.) go live and run until Process.Stop ends THIS instance - like a normal .phx, with no run-duration cap. Start params (synced from the process's Process.Entry) are passed in and read in the body as {param.<name>}. InstanceId outputs the new instance's id; wire it into a Process.Stop. Starting again launches a second concurrent instance.",
                new[] { ("Flow", ColExec) },
                new[] { ("Done", ColExec), ("InstanceId", ColString) },
                new Dictionary<string, string> { { "ProcessId", "" }, { "ProcessName", "Process" } });

            AddTemplate("Process.Stop", "Process", Color.DimGray,
                "Stops a running Process instance by InstanceId (the value returned from Process.Start). The instance's live triggers go dormant and its schedule timers are cancelled. Stopping an unknown / already-stopped id is a no-op.",
                new[] { ("Flow", ColExec), ("InstanceId", ColString) },
                new[] { ("Done", ColExec) });

            // Deprecated (hidden) — legacy fire-and-forget spawn primitive.
            AddTemplate("Process.Spawn", "Process", Color.DimGray,
                Localizer.T("architect.node.bubble.process_spawn"),
                new[] { ("Flow", ColExec) },
                new[] { ("Done", ColExec), ("InstanceId", ColString) },
                new Dictionary<string, string> { { "ProcessId", "" }, { "ProcessName", "Process" } });

            // Deprecated (hidden) — legacy terminate, superseded by Process.Stop.
            AddTemplate("Process.Terminate",  "Process", Color.DimGray,
                Localizer.T("architect.node.bubble.process_terminate"),
                new[] { ("Flow", ColExec), ("InstanceId", ColString) },
                new[] { ("Done", ColExec) });

            // Process.Entry — the "on start" trigger of a process. Its Flow output
            // fires once when an instance starts; its dynamic param outputs expose
            // the start params (read elsewhere in the body as {param.<name>}).
            AddTemplate("Process.Entry", "Process", Color.FromArgb(70, 70, 110),
                Localizer.T("architect.node.bubble.process_entry"),
                null,
                new[] { ("Flow", ColExec) });

            // Process.Exit — the "on stop" trigger of a process. Output-only: its
            // "On Stop" exec OUTPUT fires once when the instance is stopped (cleanup
            // hook). A live process never "returns", so there is no inbound flow —
            // it is an entry point like Process.Entry, walked from its output.
            AddTemplate("Process.Exit", "Process", Color.FromArgb(55, 55, 90),
                Localizer.T("architect.node.bubble.process_exit"),
                null,
                new[] { ("On Stop", ColExec) });

            // ── Stray Flow Control template (authored between PROCESS and
            // DATABANK in the legacy file). Category remains "Flow Control"
            // for palette grouping; the file location is inherited from the
            // pre-carve order so existing graphs keep their socket layout.
            AddTemplate("Flow.ForEach", "Flow Control", Color.DarkOrange,
                "Walks through every item in List, firing Loop Body once per item with the current value on Item. Done fires once after the last item is processed. Empty lists fire Done immediately without firing the body.",
                new[] { ("Flow", ColExec), ("List", ColList) },
                new[] { ("Loop Body", ColExec), ("Item", ColString), ("Done", ColExec) });

            // ── Queue socket-level hover help (canvas pin pop-ups + doc form) ──
            // The Name pin needs it most: "leave it empty" is a MODE, not a default
            // value, and nothing on the canvas would say so otherwise.
            const string queueNameHelp =
                "Which queue to use. Leave empty for the single unnamed queue every script shares. " +
                "Fill in a name for a persistent queue of its own — one row per entry in the databank's " +
                "\"Queues\" table, readable from the DB nodes. The User Management tool's viewer queue is one of these.";

            SetSocketDescriptions("Queue.Push", new()
            {
                { "EventID",  "The entry's identity — what Queue.Position and Queue.Remove match on later (case-insensitive)." },
                { "Payload",  "Data carried along with the entry. Comes back out of Queue.Pop." },
                { "Name",     queueNameHelp },
                { "Priority", "Higher numbers move to the front of a NAMED queue; equal numbers keep arrival order. Leave at 0 for a plain first-come line. Has no effect on the unnamed queue." },
            });
            SetSocketDescriptions("Queue.Pop", new()
            {
                { "Name",    queueNameHelp },
                { "EventID", "The identity of the entry that came off the front." },
                { "Payload", "The data that was stored with it." },
                { "Empty",   "Fires instead of Done when there was nothing to pop." },
            });
            SetSocketDescriptions("Queue.Length", new()
            {
                { "Name",  queueNameHelp },
                { "Count", "How many entries are waiting." },
            });
            SetSocketDescriptions("Queue.Clear", new()
            {
                { "Name", queueNameHelp },
            });
            SetSocketDescriptions("Queue.Position", new()
            {
                { "Entry",    "Which entry to look for — matched against the EventID it was pushed with (case-insensitive)." },
                { "Name",     queueNameHelp },
                { "Position", "Its place in the line counting from 1, or 0 when it is not queued." },
            });
            SetSocketDescriptions("Queue.Remove", new()
            {
                { "Entry", "Which entry to take out — matched against the EventID it was pushed with (case-insensitive)." },
                { "Name",  queueNameHelp },
            });
            SetSocketDescriptions("Queue.List", new()
            {
                { "Name", queueNameHelp },
                { "List", "The whole queue, front first, as a comma-separated list." },
            });
            SetSocketDescriptions("Queue.OnChanged", new()
            {
                { "Queue",  "Which queue changed. Empty means the unnamed queue." },
                { "Entry",  "The entry that was added, popped or removed. Empty for a clear." },
                { "Action", "What happened: push, pop, remove or clear." },
                { "Length", "How many entries are left afterwards." },
            });

            // Fuzzy spawn-search aliases — the gap between what a user types
            // ("line", "waiting list", "next up") and the Queue.* titles.
            SetKeywords("Queue.Push",      "queue", "line", "enqueue", "join", "add", "waitlist");
            SetKeywords("Queue.Pop",       "queue", "line", "dequeue", "next", "take", "waitlist");
            SetKeywords("Queue.Length",    "queue", "line", "length", "count", "waiting", "size");
            SetKeywords("Queue.Clear",     "queue", "line", "clear", "empty", "reset", "wipe");
            SetKeywords("Queue.Position",  "queue", "line", "position", "place", "where", "rank", "spot");
            SetKeywords("Queue.Remove",    "queue", "line", "remove", "leave", "drop", "kick");
            SetKeywords("Queue.List",      "queue", "line", "list", "show", "print", "who", "waiting");
            SetKeywords("Queue.OnChanged", "queue", "line", "changed", "onchange", "event", "trigger");
        }
    }
}
