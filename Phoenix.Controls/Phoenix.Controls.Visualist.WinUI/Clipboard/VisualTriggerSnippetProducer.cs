using System;
using System.IO;
using System.Text.Json;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Phoenix.Controls.Visualist.WinUI.Clipboard;

/// <summary>
/// Visualist → Architect snippet producer. Reads the current widget + trigger
/// context out of the loaded LayerDocument and emits a
/// <see cref="VisualTriggerSnippet"/> onto the system clipboard so an Architect
/// canvas paste spawns a fully-attributed Visual.Trigger node referencing this
/// layer/widget/trigger.
///
/// Pillar separation: this producer touches the Windows clipboard and the
/// Shared DTO only; it never talks to Streamer.bot, Twitch, or anything on
/// Hub's runtime side. Architect interprets the payload independently inside
/// its own canvas (see LogicCanvasView.TryPasteVisualTriggerSnippetAsync).
///
/// Schema (locked by the consumer's default-options
/// <c>JsonSerializer.Deserialize&lt;VisualTriggerSnippet&gt;</c> call):
/// <code>
/// {
///   "LayerID":     "&lt;layer-file-stem&gt;",
///   "WidgetID":    "&lt;LayerWidget.Id&gt;",
///   "TriggerName": "&lt;WidgetTrigger.Name&gt;",
///   "Queued":      false
/// }
/// </code>
/// Layer ID is the .phxlayer filename without extension — matches Hub's
/// LayerRegistry keying and the consumer's provenance check, which globs
/// <c>data/layers/&lt;LayerID&gt;.phxlayer</c>.
/// </summary>
internal static class VisualTriggerSnippetProducer
{
    private const string LogSource = "Visualist.SnippetProducer";

    /// <summary>
    /// Build a snippet from the supplied layer/widget/trigger context and copy
    /// it to the system clipboard under both the structured
    /// <see cref="VisualTriggerSnippet.ClipboardFormat"/> channel (consumed by
    /// Architect.LogicCanvasView) and a readable plain-text channel (so a
    /// paste into a code editor lands on a self-explanatory line).
    /// </summary>
    /// <param name="layerFilePath">
    /// Absolute path to the <c>.phxlayer</c> file backing the widget. The file
    /// stem is used as the snippet's LayerID. Pass the loaded document's
    /// <see cref="LayerDocument.FilePath"/>; an unsaved (null/empty) document
    /// causes the operation to abort with a System-tier log entry per
    /// <c>feedback_no_modal_dialogs_for_repeatable_rejections</c>.
    /// </param>
    /// <param name="widget">The widget that owns the trigger.</param>
    /// <param name="trigger">The trigger to reference.</param>
    /// <returns>
    /// True when the clipboard write succeeded; false when context was missing
    /// or the Windows clipboard refused the write (logged, no exception).
    /// </returns>
    public static bool CopyToClipboard(string? layerFilePath, LayerWidget? widget, WidgetTrigger? trigger)
    {
        // Empty / invalid context — log and bail per the no-modal rule. A
        // missing widget or trigger is reachable via right-click on the "+" tab
        // before any trigger exists, so this is a routine guard, not an error.
        if (widget is null || trigger is null)
        {
            GlobalLogger.Log(
                "Copy-as-Architect-snippet skipped — no widget/trigger selected.",
                source: LogSource,
                level: LogLevel.System);
            return false;
        }
        if (string.IsNullOrWhiteSpace(widget.Id))
        {
            GlobalLogger.Log(
                "Copy-as-Architect-snippet skipped — widget has no Id.",
                source: LogSource,
                level: LogLevel.System);
            return false;
        }
        if (string.IsNullOrWhiteSpace(trigger.Name))
        {
            GlobalLogger.Log(
                "Copy-as-Architect-snippet skipped — trigger has no name.",
                source: LogSource,
                level: LogLevel.System);
            return false;
        }
        if (string.IsNullOrWhiteSpace(layerFilePath))
        {
            // Unsaved layer — the LayerID would be a phantom value Architect's
            // provenance check would reject anyway. Log + bail so the user
            // knows to save first.
            GlobalLogger.Log(
                "Copy-as-Architect-snippet skipped — save the layer first so it has a LayerID.",
                source: LogSource,
                level: LogLevel.System);
            return false;
        }

        // LayerID = filename stem ("main.phxlayer" → "main"), matching
        // LayerRegistry / consumer-side provenance keying.
        string layerId = Path.GetFileNameWithoutExtension(layerFilePath);
        if (string.IsNullOrWhiteSpace(layerId))
        {
            GlobalLogger.Log(
                $"Copy-as-Architect-snippet skipped — could not derive LayerID from '{layerFilePath}'.",
                source: LogSource,
                level: LogLevel.System);
            return false;
        }

        // Empty-graph guard. A snippet referencing a trigger whose graph has
        // zero nodes is technically valid (the Architect-side Visual.Trigger
        // node is just a reference and renders fine), but Majo's standing
        // feedback prefers we surface the no-op rather than silently emit a
        // payload pointing at nothing meaningful. Logged at System tier; the
        // copy still proceeds because the user may want the reference plumbed
        // before authoring the graph. (Mirrors how Architect's paste flow
        // tolerates an unresolved provenance check.)
        if (trigger.Graph is null || trigger.Graph.Nodes.Count == 0)
        {
            GlobalLogger.Log(
                $"Copy-as-Architect-snippet — '{layerId}/{widget.Id}/{trigger.Name}' has an empty graph; the Visual.Trigger node will still reference it.",
                source: LogSource,
                level: LogLevel.System);
        }

        // Queued defaults to false — the Architect-side Visual.Trigger node
        // exposes the flag inline (see LogicCanvasView.Clipboard.cs line 290)
        // so the user can flip it on the pasted node. The cross-pillar
        // contract has no source-of-truth for queued semantics in Visualist
        // (Visualist authors widget graphs; queueing is a Hub-side trigger
        // dispatch concern), so the safer default is non-queued.
        var snippet = new VisualTriggerSnippet(
            LayerID:     layerId,
            WidgetID:    widget.Id,
            TriggerName: trigger.Name,
            Queued:      false);

        string structuredJson;
        try
        {
            // Default JsonSerializerOptions to match the consumer's
            // default-options Deserialize call — property names must round-trip
            // exactly (PascalCase: LayerID/WidgetID/TriggerName/Queued).
            structuredJson = JsonSerializer.Serialize(snippet);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error(LogSource, "JSON serialization failed", ex);
            return false;
        }

        // Readable text channel — mirrors the legacy
        // visual.trigger("layer", "widget", "name", {}) form mentioned in the
        // DTO header so a paste into a code editor lands on something the
        // author can read. Uses single-line interpolation to keep the channel
        // useful as a quick "what did I just copy" affordance.
        //
        //  (P1-9) — escape backslash / double-quote / CR / LF so a
        // layer or widget id (or a trigger name that snuck past validation
        // via an old .phxlayer migration) can't break out of the string
        // literal. The structured JSON channel above goes through
        // JsonSerializer.Serialize, which escapes natively; only this
        // hand-built plain-text template needs the helper. Escape set
        // (\\, \", \n, \r) matches ScriptEngine.UnescapeStringLiteral /
        // ScriptExporter.EscapeStringLiteral so a paste-then-export cycle
        // is reversible.
        string plainText = BuildPlainText(layerId, widget.Id, trigger.Name);

        try
        {
            var dp = new DataPackage();
            dp.RequestedOperation = DataPackageOperation.Copy;
            dp.SetData(VisualTriggerSnippet.ClipboardFormat, structuredJson);
            dp.SetText(plainText);
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);

            GlobalLogger.Log(
                $"Copied Architect snippet for '{layerId}/{widget.Id}/{trigger.Name}'.",
                source: LogSource,
                level: LogLevel.System);
            return true;
        }
        catch (Exception ex)
        {
            // Windows clipboard sometimes refuses the write (locked by another
            // process, remote-desktop transition, etc.). Log and surrender —
            // no modal, no rethrow.
            GlobalLogger.Error(LogSource, "Clipboard.SetContent failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Build the readable <c>visual.trigger("layer", "widget", "name", {})</c>
    /// plain-text line with every interpolated field escaped via
    /// <see cref="EscapePlainText"/>. Exposed at internal visibility so unit
    /// tests can assert the escaping without spinning up a WinUI clipboard
    /// session.
    /// </summary>
    internal static string BuildPlainText(string layerId, string widgetId, string triggerName)
    {
        return "visual.trigger(\""
            + EscapePlainText(layerId)    + "\", \""
            + EscapePlainText(widgetId)   + "\", \""
            + EscapePlainText(triggerName) + "\", {})";
    }

    /// <summary>
    /// Escape a string for safe embedding inside a C-style double-quoted
    /// literal in the clipboard plain-text channel. The escape set
    /// (<c>\\</c>, <c>\"</c>, <c>\n</c>, <c>\r</c>) is the same one
    /// <see cref="Phoenix.Controls.Shared.Core.ScriptEngine"/> recognises
    /// in <c>UnescapeStringLiteral</c> and that
    /// <see cref="Phoenix.Controls.Architect.Core.ScriptExporter"/> emits
    /// from <c>EscapeStringLiteral</c>, so a paste-then-export round-trip
    /// is lossless.
    /// </summary>
    internal static string EscapePlainText(string s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";

        var sb = new System.Text.StringBuilder(s.Length + 4);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                default:   sb.Append(c);      break;
            }
        }
        return sb.ToString();
    }
}
