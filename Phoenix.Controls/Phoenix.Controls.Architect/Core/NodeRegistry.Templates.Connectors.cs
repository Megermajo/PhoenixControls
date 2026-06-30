using System.Collections.Generic;
using System.Drawing;
using Phoenix.Controls.Shared.Localization;

namespace Phoenix.Controls.Architect.Core
{
    //  — connectors carve. Bundles three small cross-cutting
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
            // M21 — tooltip wording: this isn't an "internal event queue", it's a Var
            // pipe-string under the hood (one var per queue, '|' separated entries). Naming the
            // backing store helps users understand persistence + cross-script visibility.
            AddTemplate("Queue.Push",   "Queue", Color.DarkCyan,
                Localizer.T("architect.node.bubble.queue_push"),
                new[] { ("Flow", ColExec), ("EventID", ColString), ("Payload", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("Queue.Pop",    "Queue", Color.DarkCyan,
                Localizer.T("architect.node.bubble.queue_pop"),
                new[] { ("Flow", ColExec) },
                new[] { ("Done", ColExec), ("EventID", ColString), ("Payload", ColString), ("Empty", ColExec) });

            AddTemplate("Queue.Length", "Queue", Color.DarkCyan,
                Localizer.T("architect.node.bubble.queue_length"),
                null,
                new[] { ("Count", ColNumber) });

            AddTemplate("Queue.Clear",  "Queue", Color.DarkCyan,
                Localizer.T("architect.node.bubble.queue_clear"),
                new[] { ("Flow", ColExec) },
                new[] { ("Done", ColExec) });

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
        }
    }
}
