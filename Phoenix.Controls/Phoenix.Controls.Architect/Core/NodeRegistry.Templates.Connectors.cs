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

            // ── PROCESS — unified async-spawn primitive ─────────────────
            //
            // Process.Spawn fires off the body of a Process as a
            // detached, named, asynchronous unit. The parent script continues
            // past the spawn point without waiting; the spawned body runs on
            // its own CTS owned by ProcessManager (Process.Terminate cancels).
            // Authoring lives in a macro-editor-style ProcessEditorForm — the
            // process owns its internal graph plus Process.Entry / Process.Exit
            // for var-in / var-out boundaries (parallel to Macro.Entry / Exit).
            //
            // Process.Host / Session.Start / Session.End were retired in the
            // sweep that introduced this — Process.Spawn is the unified
            // detached primitive, so the half-built session machinery and
            // the no-op process.host registration both went away.
            AddTemplate("Process.Spawn", "Process", Color.DimGray,
                Localizer.T("architect.node.bubble.process_spawn"),
                new[] { ("Flow", ColExec) },
                new[] { ("Done", ColExec), ("InstanceId", ColString) },
                new Dictionary<string, string> { { "ProcessId", "" }, { "ProcessName", "Process" } });

            AddTemplate("Process.Terminate",  "Process", Color.DimGray,
                Localizer.T("architect.node.bubble.process_terminate"),
                new[] { ("Flow", ColExec), ("InstanceId", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("Process.Entry", "Process", Color.FromArgb(70, 70, 110),
                Localizer.T("architect.node.bubble.process_entry"),
                null,
                new[] { ("Flow", ColExec) });

            AddTemplate("Process.Exit", "Process", Color.FromArgb(55, 55, 90),
                Localizer.T("architect.node.bubble.process_exit"),
                new[] { ("Flow", ColExec) },
                null);

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
