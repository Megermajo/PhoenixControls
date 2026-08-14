using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
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
///   "LayerID":      "&lt;layer-file-stem&gt;",
///   "WidgetID":     "&lt;LayerWidget.Id&gt;",
///   "TriggerName":  "&lt;WidgetTrigger.Name&gt;",
///   "Queued":       false,
///   "ConsumedKeys": ["Args1", "Args2"]
/// }
/// </code>
/// Layer ID is the .phxlayer filename without extension — matches Hub's
/// LayerRegistry keying and the consumer's provenance check, which globs
/// <c>data/layers/&lt;LayerID&gt;.phxlayer</c>.
///
/// <para>
/// V13 — <c>ConsumedKeys</c> is the INPUT CONTRACT: every eventData key the
/// copied trigger's graph actually reads, so Architect can pre-fill the
/// matching variable pins on the pasted <c>Visual.Trigger</c> node instead of
/// leaving the author to rediscover them by reading the widget graph. It is an
/// ADDITIVE field carried outside the <see cref="VisualTriggerSnippet"/> record
/// (see <c>SnippetWire</c>): the consumer deserializes with default
/// <see cref="JsonSerializerOptions"/>, whose unmapped-member handling is Skip,
/// so a build of Architect that predates this field reads the payload exactly as
/// it always did. Widening the shared record instead would have required editing
/// <c>Phoenix.Controls.Shared</c>.
/// </para>
/// <para>
/// The consumer half is LANDED: Architect's <c>LogicCanvasView.Clipboard.cs</c>
/// reads the member back through <c>VisualTriggerPinSeeder</c> and seeds one
/// String input socket per key on the pasted node, so the contract is APPLIED and
/// not merely carried. Carried-but-unread was the first pass's defect — the keys
/// rode the clipboard and nothing on the far side ever looked at them.
/// </para>
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
    /// causes the operation to abort with a System-tier log entry.
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

        // V13 input contract — scan the graph for the eventData keys it actually
        // reads. Pre-fill only: the scan reports what is there and nothing else, so
        // the pasted node never grows a pin for a key the widget ignores, and never
        // omits one it reads (a missing pin is a silently EMPTY arg at run time —
        // no error, no log, just a blank overlay).
        var consumedKeys = CollectConsumedEventDataKeys(trigger.Graph);

        string structuredJson;
        try
        {
            structuredJson = BuildStructuredJson(snippet, consumedKeys);
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
        // Escape backslash / double-quote / CR / LF so a
        // layer or widget id (or a trigger name that snuck past validation
        // via an old .phxlayer migration) can't break out of the string
        // literal. The structured JSON channel above goes through
        // JsonSerializer.Serialize, which escapes natively; only this
        // hand-built plain-text template needs the helper. Escape set
        // (\\, \", \n, \r) matches ScriptEngine.UnescapeStringLiteral /
        // ScriptExporter.EscapeStringLiteral so a paste-then-export cycle
        // is reversible.
        string plainText = BuildPlainText(layerId, widget.Id, trigger.Name, consumedKeys);

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
    ///
    /// <para>
    /// <paramref name="consumedKeys"/> fills the trailing container with the NAMES of the
    /// eventData keys the widget graph reads — <c>{Args1 Args2}</c> — so a paste into a
    /// code editor shows WHICH values the overlay is waiting for. Null or empty keeps the
    /// historical bare <c>{}</c> byte-for-byte, which is what
    /// Sprint12TriggerNameEscapeTests and TriggerNameValidationTests pin; both call the
    /// three-argument form and are unaffected.
    /// </para>
    /// <para>
    /// ★ SPACE-separated names, and no <c>=""</c> filler. Both details are deliberate and
    /// each fixes a defect in the first draft's <c>{Args1="", Args2=""}</c>:
    /// <list type="number">
    ///   <item>The comma made the tail split MID-TOKEN.
    ///     <c>ScriptEngine.SplitArgs</c> is quote- and paren-aware but brace-BLIND, so a
    ///     depth-0 comma inside <c>{…}</c> is a real argument split: the pasted line
    ///     parsed as five args (<c>{Args1=""</c> / <c>Args2=""}</c>) where the legacy
    ///     <c>{}</c> parsed as four. A space keeps the whole tail one token, restoring
    ///     exact parse parity with the form it replaced.</item>
    ///   <item><c>Args1=""</c> advertises the present-but-empty kv shape that
    ///     <c>VisualTriggerHandler</c> no longer emits — an empty-but-PRESENT eventData
    ///     key pre-empts <c>ScriptManager.ExpandArgsList</c>'s positional expansion and
    ///     silences <c>compositor.js</c>'s missing-arg diagnostic. Showing it here as a
    ///     fill-in template would teach the defect. And it never even meant "empty": the
    ///     engine only strips quotes from a FULLY quoted arg, so a pasted
    ///     <c>Args1=""</c> delivers the two-character string <c>""</c>.</item>
    /// </list>
    /// The information this channel exists to carry is the key NAMES; that is what it now
    /// carries.
    /// </para>
    /// <para>
    /// The brace container is this channel's own legacy shape, NOT what the exporter
    /// emits — a real export is
    /// <c>visual.trigger_queued(layer, widget, name, Args1=…)</c> with trailing
    /// key=value args (VisualTriggerHandler). This channel has always been the
    /// "what did I just copy" affordance rather than a runnable line, so it keeps
    /// its container and simply stops being empty.
    /// </para>
    /// </summary>
    internal static string BuildPlainText(
        string layerId,
        string widgetId,
        string triggerName,
        IReadOnlyList<string>? consumedKeys = null)
    {
        string dict = "";
        if (consumedKeys is { Count: > 0 })
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < consumedKeys.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                // Escape the key too: it is author text off a node attribute, so a
                // quote in it would break out of the surrounding literal exactly the
                // way an unescaped trigger name would.
                sb.Append(EscapePlainText(consumedKeys[i]));
            }
            dict = sb.ToString();
        }

        return "visual.trigger(\""
            + EscapePlainText(layerId)    + "\", \""
            + EscapePlainText(widgetId)   + "\", \""
            + EscapePlainText(triggerName) + "\", {" + dict + "})";
    }

    // ─── V13 input contract — the consumed-key scan ───────────────────────────

    /// <summary>
    /// <c>{ArgsN}</c> token pattern for <c>Text.Render</c> bodies. Character-for-character
    /// the regex <c>compositor.js</c>'s <c>substituteArgs</c> uses
    /// (<c>/\{(Args\d+)\}/g</c>) — the scan must recognise exactly what the renderer
    /// substitutes, no more (a looser pattern would invent keys the overlay never reads)
    /// and no less.
    /// </summary>
    private static readonly Regex ArgsTokenPattern =
        new(@"\{(Args\d+)\}", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Every eventData key <paramref name="graph"/> CONSUMES, in first-seen node order,
    /// deduplicated. FIVE sources — the complete set of widget-palette nodes whose
    /// evaluator touches <c>triggerContext.eventData</c>:
    ///
    /// <list type="bullet">
    ///   <item><c>Result.If</c> — its <c>When</c> attribute names the key it gates on.</item>
    ///   <item><c>Text.Render</c> — <c>{ArgsN}</c> tokens in its <c>Text</c> attribute.</item>
    ///   <item><c>Visual.Arg</c> — its <c>Key</c> attribute.</item>
    ///   <item><c>Message.Read</c> — its <c>Key</c> attribute. §8.4 of the wire contract
    ///     named only the first three; this one was missed on the first pass and is just
    ///     as live: <c>evalMessageRead</c> does
    ///     <c>stripQuotes(attr(node,'Key','Args1'))</c> and reads
    ///     <c>triggerContext.eventData[key]</c>. It is a registered widget template
    ///     ("Triggers" band), has a C# mirror arm, and its registration block records
    ///     that it is deliberately KEPT because it lives in already-saved layers — so it
    ///     is exactly the node an old layer reads its args with.</item>
    ///   <item><c>Visual.OnTrigger</c> — the only one with no author-typed key.
    ///     <c>evalVisualOnTrigger</c> reads FIXED alias chains keyed off which OUTPUT is
    ///     pulled: <c>UserName</c> → <c>ed.user ?? ed.UserName ?? ed.userName</c>,
    ///     <c>Message</c> → <c>ed.message ?? ed.Message</c>. An earlier pass excluded it
    ///     on the grounds that picking one alias "would INVENT a key the graph may not
    ///     read" — which does not hold: the FIRST alias in a <c>??</c> chain is one the
    ///     reader definitely reads, and it is the spelling Hub delivers, so reporting
    ///     <c>user</c> / <c>message</c> invents nothing. Reported only when the matching
    ///     output is WIRED, which mirrors the runtime exactly — the evaluator is reached
    ///     only through a downstream <c>findLinkTo</c>, so an unwired output reads
    ///     nothing. That gate is what keeps the never-invent rule true, and it is why an
    ///     OnTrigger wired for <c>TriggerName</c> / <c>EventData</c> / <c>Flow</c> alone
    ///     contributes nothing: <c>EventData</c> hands over the whole payload as JSON
    ///     with no key, and <c>TriggerName</c> comes from <c>triggerContext</c>, so
    ///     neither is an eventData-key read at all.</item>
    /// </list>
    ///
    /// <para>
    /// Each arm mirrors its runtime reader EXACTLY rather than approximately, because a
    /// mismatch in either direction is silent. <c>Visual.Arg</c> trims and defaults a
    /// MISSING <c>Key</c> to <c>Args1</c> (<c>attr(node,'Key','Args1')</c>), so a
    /// hand-edited node with no Key attribute really does read Args1 and must be
    /// reported. <c>Message.Read</c> shares that absence-default but does NOT trim
    /// (<c>evalMessageRead</c> has no <c>.trim()</c> — the difference is real, and
    /// trimming here would report a key the browser never looks up).
    /// <c>Result.If</c> does NOT trim and defaults to empty
    /// (<c>attr(node,'When','""')</c>), so an unset <c>When</c> is unconfigured and
    /// contributes nothing. Title matching is Ordinal because the dispatch that decides
    /// whether a node reads anything is a JS <c>switch</c> on <c>node.Title</c> (===) —
    /// a differently-cased title is not dispatched, so it consumes nothing, and folding
    /// case here would invent a key.
    /// </para>
    /// <para>
    /// Whitespace-only keys are skipped: Hub delivers <c>Args1..N</c> / <c>user</c> /
    /// <c>message</c>, so a key of <c>" "</c> can never match anything the trigger
    /// carries. Emitting a pin named <c>" "</c> would be worse than omitting it, and no
    /// real consumption is lost.
    /// </para>
    /// <para>
    /// KNOWN LIMIT, stated rather than papered over: only LITERAL attribute text is
    /// visible to a design-time scan. A <c>{Args1}</c> token that reaches
    /// <c>Text.Render</c> through a WIRED upstream string node (e.g. a
    /// <c>String.Constant</c> feeding the <c>Text</c> socket) is substituted at render
    /// time but cannot be seen here, so that key is not pre-filled. The author can still
    /// add the pin by hand; the pre-fill is an accelerator, not a validator.
    /// </para>
    ///
    /// Internal + pure (no clipboard, no UI) so the contract is unit-testable directly.
    /// </summary>
    internal static List<string> CollectConsumedEventDataKeys(Graph? graph)
    {
        var keys = new List<string>();
        if (graph?.Nodes is null || graph.Nodes.Count == 0) return keys;

        // Ordinal dedupe set, separate from the ordered list: eventData lookup is
        // hasOwnProperty / dictionary-keyed, so "args1" and "Args1" are two different
        // keys and collapsing them would drop one.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Output-socket ids that something downstream pulls from. Only the
        // Visual.OnTrigger arm needs this (it is keyed off the wired OUTPUT rather than
        // an attribute), so it is built lazily — a graph with no OnTrigger node pays
        // nothing.
        HashSet<string>? pulledOutputSocketIds = null;

        foreach (var node in graph.Nodes)
        {
            if (node is null) continue;

            switch (node.Title)
            {
                case "Visual.OnTrigger":
                {
                    // evalVisualOnTrigger: reached only via a downstream findLinkTo, so an
                    // unwired output reads nothing and must contribute nothing. Walked in
                    // the node's own output-socket order so the seeded pins land in the
                    // order the author sees on the node.
                    pulledOutputSocketIds ??= BuildPulledOutputSocketIds(graph);
                    if (node.Sockets is null) break;
                    foreach (var s in node.Sockets)
                    {
                        if (s is null || s.Type != SocketType.Output) continue;
                        if (!pulledOutputSocketIds.Contains(s.Id)) continue;
                        // FIRST alias of each ?? chain — the one the reader definitely
                        // reads, and the spelling Hub delivers.
                        if (s.Name == "UserName")     Consider("user");
                        else if (s.Name == "Message") Consider("message");
                        // Flow / TriggerName / EventData read no eventData KEY.
                    }
                    break;
                }

                case "Result.If":
                    // evalResultIf: stripQuotes(attr(node, 'When', '""')) — no trim.
                    Consider(ReadAttr(node, "When", ""));
                    break;

                case "Visual.Arg":
                    // evalVisualArg: stripQuotes(attr(node, 'Key', 'Args1')).trim()
                    Consider(ReadAttr(node, "Key", "Args1").Trim());
                    break;

                case "Message.Read":
                    // evalMessageRead: stripQuotes(attr(node, 'Key', 'Args1')) — same
                    // absence-default as Visual.Arg above, and deliberately NO .Trim():
                    // the browser reader does not trim, so a key with surrounding
                    // whitespace really is the key it looks up.
                    Consider(ReadAttr(node, "Key", "Args1"));
                    break;

                case "Text.Render":
                    // evalTextRender resolves the Text socket first and falls back to
                    // the attribute; only the attribute is readable at design time.
                    string text = ReadAttr(node, "Text", "");
                    if (text.Length > 0 && text.IndexOf('{') >= 0)
                    {
                        foreach (Match m in ArgsTokenPattern.Matches(text))
                            Consider(m.Groups[1].Value);
                    }
                    break;
            }
        }

        return keys;

        void Consider(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (seen.Add(key)) keys.Add(key);
        }
    }

    /// <summary>
    /// Every output-socket id that at least one link pulls from. Ordinal, because socket
    /// ids are GUID strings. Null-tolerant on both <c>Links</c> and each entry — a
    /// hand-edited <c>.phxlayer</c> can carry <c>"links": null</c>.
    /// </summary>
    private static HashSet<string> BuildPulledOutputSocketIds(Graph graph)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (graph.Links is null) return set;
        foreach (var link in graph.Links)
        {
            if (link is null) continue;
            if (!string.IsNullOrEmpty(link.FromSocketId)) set.Add(link.FromSocketId);
        }
        return set;
    }

    /// <summary>
    /// Read a node attribute and strip one layer of JSON quoting, matching
    /// <c>compositor.js</c>'s <c>stripQuotes</c> and
    /// <c>NodeEvaluator.StripQuotes</c> (both private to their own pillar, hence the
    /// third copy rather than a shared helper). <paramref name="fallback"/> is returned
    /// when the key is ABSENT — mirroring <c>attr(node, name, fallback)</c>, whose
    /// fallback likewise applies only on absence, not on an empty stored value.
    /// </summary>
    private static string ReadAttr(Node node, string name, string fallback)
    {
        if (node.Attributes is null || !node.Attributes.TryGetValue(name, out var raw) || raw is null)
            return fallback;
        return raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' ? raw[1..^1] : raw;
    }

    /// <summary>
    /// Build the exact bytes that go onto the structured clipboard channel.
    ///
    /// <para>
    /// Default <see cref="JsonSerializerOptions"/> to match the consumer's
    /// default-options <c>Deserialize&lt;VisualTriggerSnippet&gt;</c> call — property
    /// names must round-trip exactly (PascalCase: LayerID/WidgetID/TriggerName/Queued),
    /// and the first four properties stay in the record's declaration order so the
    /// payload is a strict superset of the pre-V13 one.
    /// </para>
    /// <para>
    /// Split out of <see cref="CopyToClipboard"/> at internal visibility so the
    /// cross-pillar handshake is testable WITHOUT a WinUI clipboard session: the
    /// Architect consumer reads its own hard-coded <c>"ConsumedKeys"</c> literal, so the
    /// only way to catch a divergence between the two halves is to feed this method's
    /// REAL output into that reader. (The V13 first pass shipped exactly that class of
    /// bug on the token meta tag — two literals that had to agree, a test that asserted
    /// one constant against markup built from the same constant, and a feature that was
    /// dead on arrival while the suite stayed green.)
    /// </para>
    /// </summary>
    internal static string BuildStructuredJson(
        VisualTriggerSnippet snippet,
        IReadOnlyList<string>? consumedKeys)
        => JsonSerializer.Serialize(new SnippetWire(
            snippet.LayerID,
            snippet.WidgetID,
            snippet.TriggerName,
            snippet.Queued,
            consumedKeys ?? Array.Empty<string>()));

    /// <summary>
    /// Serialization shape for the structured clipboard channel: the four
    /// <see cref="VisualTriggerSnippet"/> fields in declaration order plus the additive
    /// V13 <c>ConsumedKeys</c>. Separate from the shared record because widening that
    /// record means editing <c>Phoenix.Controls.Shared</c>; the consumer's
    /// default-options <c>Deserialize&lt;VisualTriggerSnippet&gt;</c> skips the unmapped
    /// member (System.Text.Json defaults <c>UnmappedMemberHandling</c> to Skip), so old
    /// and new Architect builds both read this payload. That back-compat claim is pinned
    /// by <c>V13DesignTimeTests</c> — it is a silent-if-broken property, so it is not
    /// left resting on the sentence you are reading.
    /// </summary>
    private sealed record SnippetWire(
        string LayerID,
        string WidgetID,
        string TriggerName,
        bool   Queued,
        IReadOnlyList<string> ConsumedKeys);

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
