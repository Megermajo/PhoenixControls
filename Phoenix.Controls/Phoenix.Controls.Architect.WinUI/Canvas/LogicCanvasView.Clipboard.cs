using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Architect.Core;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

// Clipboard partial — Ctrl+C / Ctrl+V / Ctrl+D copy/paste/duplicate of the
// current multi-selection. System-clipboard primary, in-process fallback for
// when the OS clipboard is locked. Detects VisualTriggerSnippet payloads
// (Visualist trigger references) so cross-pillar paste spawns a fully-
// attributed Visual.Trigger node at the cursor.
public sealed partial class LogicCanvasView
{
    /// <summary>
    /// Snapshot copied from a Ctrl+C — JSON-serialised Node list +
    /// the Links whose endpoints both lie inside that node set. Stored
    /// at the type level so paste survives panel switches inside a session.
    /// </summary>
    private sealed class ClipboardSnapshot
    {
        public List<Node> Nodes { get; set; } = new();
        public List<Link> Links { get; set; } = new();
        // 0.10.0 — comment frames are now part of the
        // multi-selection too, so they round-trip through copy / cut / paste
        // alongside nodes + wires. Older snapshots without this field
        // deserialize as Frames=new() (default-init), so a clipboard payload
        // from a pre-0.10.0 build pastes correctly with no frame entries.
        public List<Phoenix.Controls.Shared.Models.Frame> Frames { get; set; } = new();
    }

    // System-clipboard format identifier for sub-graph paste payloads.
    private const string SubGraphClipboardFormat = "PhoenixControls.SubGraph";

    // In-process fallback used when DataPackageView.GetDataAsync rejects access
    // (clipboard temporarily locked, virtualised desktop transition, etc.).
    //
    // Per-Window scoping. Was a process-global static
    // shared by every LogicCanvasView; with 0.10.0 multi-window restore that
    // meant a Copy in window A could be pulled by a Paste in window B
    // whenever the OS clipboard was locked / unreadable, even though the user
    // only ever meant the system clipboard. Fallback now hangs off a
    // ConditionalWeakTable keyed by the hosting Window so each Architect
    // sibling window owns its own slot and entries collect automatically when
    // a window closes (no manual unregister hook). WinUI 3 UserControls have
    // no direct Window reference — XamlRoot is the only stable per-Window
    // identity reachable from elements in the tree (same idiom as
    // WindowActivationTracker), and `XamlRoot.Content` is the root
    // FrameworkElement at the top of the ancestry walk from `this`. We key on
    // that root element: it is uniquely 1:1 with its hosting Window for the
    // window's full lifetime, and `ConditionalWeakTable` lets the entry die
    // with the window. A Paste from a canvas whose Window has no resolvable
    // root (XamlRoot null pre-Loaded, etc.) falls back to "no in-process
    // snapshot" — i.e. only the OS clipboard contributes, which is the
    // correct conservative default.
    private static readonly ConditionalWeakTable<FrameworkElement, ClipboardSnapshot> s_fallbackByWindow = new();

    /// <summary>
    /// Per-Window key for the in-process clipboard fallback. Walks from
    /// <c>this.XamlRoot</c> to its <c>Content</c> root — that root is the
    /// FrameworkElement set as <c>Window.Content</c>, unique per Window for
    /// the window's lifetime, and so functions as a stable Window proxy
    /// reachable from inside a UserControl. Returns null when the canvas
    /// hasn't materialised a XamlRoot yet (pre-Loaded) so callers can
    /// degrade gracefully.
    /// </summary>
    private FrameworkElement? ResolveWindowKey()
    {
        var root = XamlRoot;
        if (root is null) return null;
        return root.Content as FrameworkElement;
    }

    private ClipboardSnapshot? GetFallbackSnapshot()
    {
        var key = ResolveWindowKey();
        if (key is null) return null;
        return s_fallbackByWindow.TryGetValue(key, out var snap) ? snap : null;
    }

    private void SetFallbackSnapshot(ClipboardSnapshot snap)
    {
        var key = ResolveWindowKey();
        if (key is null) return;
        // AddOrUpdate replaces an existing entry for the same key — pre-fix
        // the static was just reassigned, so a second Copy in the same window
        // must keep that overwrite semantic instead of growing the table.
        s_fallbackByWindow.AddOrUpdate(key, snap);
    }

    // Clipboard JSON options that include Architect's
    // ColorJsonConverter equivalent, so Frame.FrameColor + any future
    // System.Drawing.Color slot round-trip through copy/paste the same way
    // GraphSerializer round-trips them on save/load. Pre-fix CloneNode /
    // CloneLink / CloneFrame used a stock JsonSerializer.Serialize that
    // ignored the converter, causing Frame.FrameColor to deserialize as
    // Color.Empty (Architect's pre-T15 {R,G,B,A} legacy form and the
    // post-T15 ARGB-integer form both rely on the converter to survive a
    // round-trip — System.Drawing.Color's public surface is all read-only
    // getters, so the default deserializer can't repopulate it).
    //
    // ColorJsonConverter itself lives `internal` to Architect.Core, so we
    // mirror its write path here (ARGB int) and accept both legacy {R,G,B,A}
    // and current ARGB-int forms on read — same lenient policy as the
    // source-of-truth converter.
    private static readonly JsonSerializerOptions s_cloneJson = BuildCloneOptions();

    private static JsonSerializerOptions BuildCloneOptions()
    {
        var o = new JsonSerializerOptions();
        o.Converters.Add(new ClipboardColorConverter());
        return o;
    }

    /// <summary>
    /// Local mirror of Architect.Core.ColorJsonConverter (internal there, not
    /// reachable across assemblies). Writes Color as a single ARGB int; reads
    /// either the ARGB-int form, the legacy {R,G,B,A} object form (so a
    /// payload copied from a pre-T15 graph still round-trips), or a hex
    /// string form ("#RRGGBB" / "#AARRGGBB"). Any unrecognised token returns
    /// Color.Empty rather than throwing so a malformed payload still pastes.
    /// </summary>
    private sealed class ClipboardColorConverter : JsonConverter<System.Drawing.Color>
    {
        public override System.Drawing.Color Read(ref Utf8JsonReader reader,
            Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
                return System.Drawing.Color.FromArgb(reader.GetInt32());

            if (reader.TokenType == JsonTokenType.String)
            {
                string s = reader.GetString() ?? string.Empty;
                if (TryParseHexColor(s, out var parsed)) return parsed;
                return System.Drawing.Color.Empty;
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                int a = 255, r = 0, g = 0, b = 0;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                        return System.Drawing.Color.FromArgb(a, r, g, b);
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    string? prop = reader.GetString();
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.Number) continue;
                    int v = reader.GetInt32();
                    switch (prop)
                    {
                        case "A": case "a": a = v; break;
                        case "R": case "r": r = v; break;
                        case "G": case "g": g = v; break;
                        case "B": case "b": b = v; break;
                    }
                }
                return System.Drawing.Color.FromArgb(a, r, g, b);
            }

            reader.Skip();
            return System.Drawing.Color.Empty;
        }

        public override void Write(Utf8JsonWriter writer, System.Drawing.Color value,
            JsonSerializerOptions options)
        {
            // ARGB integer matches the GraphSerializer write path so a graph
            // that round-trips through copy/paste produces the same on-disk
            // form as a save/load cycle.
            writer.WriteNumberValue(value.ToArgb());
        }

        private static bool TryParseHexColor(string s, out System.Drawing.Color color)
        {
            color = System.Drawing.Color.Empty;
            if (string.IsNullOrEmpty(s)) return false;
            // Strip optional leading '#' rather than requiring it, so
            // paste accepts both "#RRGGBB" and "RRGGBB" forms. Mirrors the
            // canonical GraphSerializer.ColorJsonConverter.TryParseHexColor which
            // is permissive about the prefix.
            var span = s[0] == '#' ? s.AsSpan(1) : s.AsSpan();
            if (span.Length != 6 && span.Length != 8) return false;
            int a = 255, r, g, b, idx = 0;
            if (span.Length == 8)
            {
                if (!TryHexByte(span.Slice(idx, 2), out a)) return false; idx += 2;
            }
            if (!TryHexByte(span.Slice(idx, 2), out r)) return false; idx += 2;
            if (!TryHexByte(span.Slice(idx, 2), out g)) return false; idx += 2;
            if (!TryHexByte(span.Slice(idx, 2), out b)) return false;
            color = System.Drawing.Color.FromArgb(a, r, g, b);
            return true;
        }

        private static bool TryHexByte(ReadOnlySpan<char> two, out int value)
            => int.TryParse(two, System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private void Copy()
    {
        var snap = CaptureSelectionSnapshot();
        if (snap is null) return;
        SetFallbackSnapshot(snap);
        try
        {
            var dp = new DataPackage();
            dp.RequestedOperation = DataPackageOperation.Copy;
            // s_cloneJson carries ClipboardColorConverter — without it System.Drawing.Color
            // fields (Node.HeaderColor, Frame.FrameColor, Socket.Color) serialize as the
            // default empty object and deserialize back to Color.Empty, so a pasted node lost
            // its header-bar color (Majo flagged). The in-process CloneNode/CloneLink/
            // CloneFrame helpers already use s_cloneJson; the clipboard channel had not.
            string json = JsonSerializer.Serialize(snap, s_cloneJson);
            dp.SetData(SubGraphClipboardFormat, json);
            // Plain-text channel keeps "paste into a code editor" useful.
            dp.SetText($"# Phoenix Controls sub-graph ({snap.Nodes.Count} nodes / {snap.Links.Count} links)");
            Clipboard.SetContent(dp);
        }
        catch
        {
            // Best effort — fallback already captured above.
        }
    }

    /// <summary>
    /// Build a clipboard snapshot from the current multi-selection without
    /// writing anywhere. Used by Copy (writes to OS clipboard) and by
    /// DuplicateSelection (in-process path — bypasses the OS clipboard so
    /// Ctrl+D doesn't clobber whatever the user had copied from another app).
    /// </summary>
    private ClipboardSnapshot? CaptureSelectionSnapshot()
    {
        if (_vm is null) return null;
        // 0.10.0 — accept any non-empty multi-selection so
        // copy/cut survive on a selection consisting only of wires or frames.
        bool anySelected = _vm.SelectedNodes.Count > 0
                        || _vm.SelectedLinks.Count > 0
                        || _vm.SelectedFrames.Count > 0;
        if (!anySelected) return null;

        var ids = new HashSet<string>(_vm.SelectedNodes.Select(n => n.Id));
        // Clone* now return null on a serialization failure;
        // for the capture path (Copy / Duplicate) a null falls back to the live
        // source model so the snapshot still carries the item — a deep-cloned
        // copy is preferable, but a shared reference is far better than dropping
        // the selection entry or crashing the copy.
        var snap = new ClipboardSnapshot
        {
            Nodes = _vm.SelectedNodes.Select(n => CloneNode(n.Model) ?? n.Model).ToList(),
            // Only capture wires whose BOTH endpoints
            // are in the copied node set — those are the only wires ApplyPaste
            // can actually re-create (its remap drops any link with an endpoint
            // outside the pasted set). Pre-fix the capture ALSO pulled in
            // explicitly-marquee-selected wires whose endpoints sat on
            // un-copied nodes; a comment claimed they'd paste as a dashed-red
            // "dangling" wire, but ApplyPaste silently `continue`s them — so
            // they only bloated the (JSON-serialized) clipboard snapshot and
            // were dropped on paste. Filtering here makes capture == paste-able.
            Links = _vm.Graph.Links
                .Where(l => ids.Contains(l.FromNodeId) && ids.Contains(l.ToNodeId))
                .Select(l => CloneLink(l) ?? l)
                .ToList(),
            Frames = _vm.SelectedFrames.Select(f => CloneFrame(f.Model) ?? f.Model).ToList(),
        };
        // Add explicitly-selected wires too, but only the paste-able ones
        // (both endpoints copied) and not already in the inferred set.
        foreach (var l in _vm.SelectedLinks)
        {
            if (!ids.Contains(l.Model.FromNodeId) || !ids.Contains(l.Model.ToNodeId)) continue;
            if (snap.Links.Any(x => x.Id == l.Model.Id)) continue;
            snap.Links.Add(CloneLink(l.Model) ?? l.Model);
        }
        return snap;
    }

    // The real paste body is an awaitable Task with a
    // top-level try-catch so a fault inside TryPasteVisualTriggerSnippetAsync /
    // TryReadSubGraphFromClipboardAsync (or their awaited clipboard reads) is
    // observed and logged rather than escaping as an unobserved-task crash.
    // Pre-fix the equivalent code was the body of an `async void Paste`, where
    // any thrown exception bypassed every caller and could tear down the app.
    private async System.Threading.Tasks.Task PasteAsync(double dx = 24, double dy = 24)
    {
        if (_vm is null) return;
        try
        {
            // Cross-pillar Visualist paste — try first so a copied trigger
            // reference always wins over an old in-graph clipboard.
            if (await TryPasteVisualTriggerSnippetAsync())
                return;

            var snap = await TryReadSubGraphFromClipboardAsync() ?? GetFallbackSnapshot();
            if (snap is null) return;
            ApplyPaste(snap, dx, dy);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Architect.LogicCanvasView", "Paste failed", ex);
        }
    }

    /// <summary>
    /// Fire-and-forget entry point for the synchronous
    /// keyboard / context-menu paste call sites (they call <c>Paste()</c> as a
    /// statement). Awaits <see cref="PasteAsync"/> on the UI dispatcher and
    /// routes any escaping fault to GlobalLogger so the await machinery can
    /// never surface a paste as an unobserved-task crash. Keeping this the
    /// <c>void</c> seam means the external call sites (LogicCanvasView.Keyboard
    /// / .Menus, not owned by this sprint) need no signature change while the
    /// awaited work moves into a properly observed Task.
    /// </summary>
    private async void Paste(double dx = 24, double dy = 24)
    {
        try { await PasteAsync(dx, dy); }
        catch (Exception ex)
        {
            GlobalLogger.Error("Architect.LogicCanvasView", "Paste (dispatch) failed", ex);
        }
    }

    private static async System.Threading.Tasks.Task<ClipboardSnapshot?> TryReadSubGraphFromClipboardAsync()
    {
        try
        {
            var view = Clipboard.GetContent();
            if (!view.Contains(SubGraphClipboardFormat)) return null;
            var raw = await view.GetDataAsync(SubGraphClipboardFormat) as string;
            if (string.IsNullOrEmpty(raw)) return null;
            // s_cloneJson (ClipboardColorConverter) so Color fields round-trip — see Copy().
            return JsonSerializer.Deserialize<ClipboardSnapshot>(raw!, s_cloneJson);
        }
        catch
        {
            return null;
        }
    }

    private async System.Threading.Tasks.Task<bool> TryPasteVisualTriggerSnippetAsync()
    {
        if (_vm is null) return false;
        try
        {
            var view = Clipboard.GetContent();
            if (!view.Contains(VisualTriggerSnippet.ClipboardFormat)) return false;
            var raw = await view.GetDataAsync(VisualTriggerSnippet.ClipboardFormat) as string;
            if (string.IsNullOrEmpty(raw)) return false;
            var snip = JsonSerializer.Deserialize<VisualTriggerSnippet>(raw!);
            if (snip is null) return false;
            if (string.IsNullOrEmpty(snip.LayerID) || string.IsNullOrEmpty(snip.WidgetID)) return false;

            // Validate LayerID/WidgetID against the local Hub
            // data/layers/ directory before committing the node. Architect
            // doesn't own the LayerRegistry runtime (that lives in Hub) so we
            // glob the file system directly — Paths.HubLayers resolves to the
            // same data/layers/ folder both Hub and Visualist write to. On a
            // miss we still spawn the node (per task spec: paste-anyway so the
            // user can fix the reference inline) but seed ErrorReason so the
            // red-triangle badge surfaces immediately. Log via GlobalLogger
            // System tier instead of a modal for this repeatable rejection.
            // The validation enumerates data/layers/ and reads +
            // deserializes a .phxlayer — synchronous disk I/O that blocked the
            // UI thread on every Visual.Trigger paste. Run it on the thread pool
            // and await; the continuation resumes on the UI thread for the node
            // mutations below.
            string? validationFailure =
                await System.Threading.Tasks.Task.Run(() => ValidateVisualTriggerProvenance(snip));

            PushUndo();
            var node = NodeRegistry.CreateNode("Visual.Trigger", new System.Drawing.Point(0, 0))
                       ?? new Node { Title = "Visual.Trigger" };
            if (node.Attributes is null) node.Attributes = new Dictionary<string, string>();
            node.Attributes["LayerID"]     = snip.LayerID;
            node.Attributes["WidgetID"]    = snip.WidgetID;
            node.Attributes["TriggerName"] = snip.TriggerName ?? string.Empty;
            node.Attributes["Queued"]      = snip.Queued ? "true" : "false";
            // V13 §8.4 input contract — the Visualist snippet carries the eventData keys
            // the copied widget graph actually reads; seed one variable pin per key so the
            // author wires values instead of rediscovering the names by reading the widget
            // graph. Must run BEFORE the NodeViewModel below, which builds its socket rows
            // from the model.
            VisualTriggerPinSeeder.SeedConsumedKeyPins(
                node, VisualTriggerPinSeeder.ReadConsumedKeys(raw!));
            // Off-canvas / stale-cursor fallback. The
            // WinForms baseline (Canvas.Clipboard.cs) checked
            // `!ClientRectangle.Contains(screen)` and fell back to canvas centre
            // when the cursor sat outside the canvas (over another window, off
            // the edge, or stale after a resize). Pre-fix this converted
            // _lastHostPoint unconditionally, so an unreliable cursor dropped the
            // node at an arbitrary spot. Mirror the DragDrop spawn idiom: when
            // HostRoot is measured AND _lastHostPoint lies inside it, paste at
            // the cursor; otherwise centre on the visible canvas.
            Windows.Foundation.Point pos;
            double hostW = HostRoot?.ActualWidth  ?? 0;
            double hostH = HostRoot?.ActualHeight ?? 0;
            bool measured = HostRoot is not null && hostW > 0 && hostH > 0;
            bool cursorInBounds = measured
                && _lastHostPoint.X >= 0 && _lastHostPoint.X <= hostW
                && _lastHostPoint.Y >= 0 && _lastHostPoint.Y <= hostH;
            if (cursorInBounds)
            {
                pos = HostToCanvas(_lastHostPoint);
            }
            else if (measured)
            {
                pos = HostToCanvas(new Windows.Foundation.Point(hostW / 2.0, hostH / 2.0));
            }
            else
            {
                // Canvas never measured this session — land at graph origin and
                // let the next zoom-to-fit reveal it (same fallback as DragDrop).
                pos = new Windows.Foundation.Point(0, 0);
            }
            node.Location = new System.Drawing.Point((int)pos.X, (int)pos.Y);
            _vm.Graph.Nodes.Add(node);
            var vm = new NodeViewModel(node);
            if (validationFailure is not null)
            {
                vm.ErrorReason = validationFailure;
                GlobalLogger.Log(
                    $"clipboard.visual-trigger.unresolved:{snip.LayerID}/{snip.WidgetID} — {validationFailure}",
                    source: "Architect.LogicCanvasView",
                    level: LogLevel.System);
            }
            _vm.Nodes.Add(vm);
            _vm.SetMultiSelection(new[] { vm });
            _vm.Graph.MarkStructuralChange();
            _vm.OnGraphMutated();
            return true;
        }
        catch (Exception ex)
        {
            // Was an empty `catch {}` that silently
            // swallowed every failure (clipboard read, JSON deserialize, node
            // mutation). Surface it via GlobalLogger so a broken Visual.Trigger
            // paste is diagnosable; still returns gracefully so PasteAsync can
            // fall through to the sub-graph clipboard path.
            GlobalLogger.Error("Architect", "paste-visual-trigger failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Confirm the snippet's LayerID + WidgetID exist on disk in
    /// the local Hub data/layers/ tree. Returns null when both resolve,
    /// otherwise a human-readable reason suitable for
    /// <see cref="NodeViewModel.ErrorReason"/>. Best-effort: any IO failure
    /// (folder missing, permission denied) is treated as a soft miss with
    /// the reason set so the badge still surfaces.
    /// </summary>
    private static string? ValidateVisualTriggerProvenance(VisualTriggerSnippet snip)
    {
        try
        {
            string layersDir = Paths.HubLayers;
            if (!Directory.Exists(layersDir))
                return "Visual.Trigger references unknown layer/widget (no local data/layers/ folder)";

            // Layer ID is the file stem: <LayerID>.phxlayer. Pre-T15 some
            // payloads stored the ID with the extension stripped; tolerate
            // both. EnumerateFiles short-circuits on the first match.
            string targetPattern = snip.LayerID.EndsWith(".phxlayer",
                StringComparison.OrdinalIgnoreCase)
                    ? snip.LayerID
                    : snip.LayerID + ".phxlayer";
            string? match = null;
            foreach (var f in Directory.EnumerateFiles(layersDir, "*.phxlayer",
                SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(Path.GetFileName(f), targetPattern,
                    StringComparison.OrdinalIgnoreCase))
                {
                    match = f;
                    break;
                }
            }
            if (match is null)
                return "Visual.Trigger references unknown layer/widget (layer not found)";

            // Deserialise the layer and confirm the widget id lives inside it.
            // MUST go through the house LayerSerializer: .phxlayer files are
            // written with its converters (Color as an ARGB number, lower-case
            // LayerPreset/WidgetPreset enum strings, NaN-tolerant keyframe
            // numbers). A bare JsonSerializer.Deserialize<Layer>() can read
            // none of those, so this check reported "layer parse failed" for
            // EVERY real layer carrying a widget graph and the badge marked
            // perfectly valid references as unresolved (observed live
            // 2026-08-19, deterministic 3-for-3). With the house reader, a
            // parse failure here again means a genuinely corrupt file.
            // Accepted side effect: LayerSerializer.Deserialize runs its
            // load-time migration sweep, so a layer that already warns on
            // Visualist load (legacy trigger name, >256-keyframe track) logs
            // the same Communication lines once per paste. Pastes are rare,
            // user-initiated actions and the affected population is exactly
            // the one those warnings exist for.
            try
            {
                var layer = LayerSerializer.Read(match);
                bool widgetFound = false;
                foreach (var w in layer.Widgets)
                {
                    // Null holes in the widgets array are a healed-around
                    // class (LayerSerializer skips them, LayerRuntime guards
                    // them, the layer loads fine everywhere else) — skip,
                    // don't let an NRE masquerade as a parse failure.
                    if (w is null) continue;
                    if (string.Equals(w.Id, snip.WidgetID, StringComparison.OrdinalIgnoreCase))
                    {
                        widgetFound = true;
                        break;
                    }
                }
                if (!widgetFound)
                    return "Visual.Trigger references unknown layer/widget (widget not in layer)";
            }
            catch
            {
                return "Visual.Trigger references unknown layer/widget (layer parse failed)";
            }
            return null;
        }
        catch
        {
            return "Visual.Trigger references unknown layer/widget (provenance check failed)";
        }
    }

    private void ApplyPaste(ClipboardSnapshot snap, double dx, double dy)
    {
        if (_vm is null) return;

        // Pillar-category validation on PASTE. The clipboard JSON shape
        // is generic Node serialization, so a Visualist copy of widget-only
        // nodes (Image.Scale, Audio.Play, Caption.LiveCaption, etc.) round-
        // trips into this method as a fully-formed Node with a Title that has
        // no Architect template. Reject those by template lookup; log a single
        // System-tier line summarizing how many nodes were skipped (no modal
        // for this repeatable rejection).
        //
        // Links whose endpoints reference a skipped node id are dropped in the
        // existing wire pass below (nodeIdMap lookup miss → continue).
        int skippedBeforeFilter = snap.Nodes.Count;
        if (skippedBeforeFilter > 0)
        {
            snap.Nodes = snap.Nodes
                .Where(n => NodeRegistry.GetTemplate(n.Title) is not null)
                .ToList();
            int skipped = skippedBeforeFilter - snap.Nodes.Count;
            if (skipped > 0)
            {
                GlobalLogger.Log(
                    $"clipboard.paste.skipped:{skipped} node(s) — title has no Architect template (likely Visualist-only widget nodes such as Image.Scale / Audio.Play).",
                    source: "Architect.LogicCanvasView",
                    level: LogLevel.Communication);
            }
            // Nothing survived the filter — bail before PushUndo so the user
            // doesn't end up with an empty undo entry.
            if (snap.Nodes.Count == 0 && snap.Frames.Count == 0) return;
        }

        // Macro reference validation + cleanup on paste.
        // The WinForms baseline (Canvas.Clipboard.cs PasteSelection) validated
        // every pasted Macro.Call node's MacroId against Graph.Macros and
        // auto-imported missing macros from the full-object GlobalMacros library
        // before re-IDing. The WinUI port has no full-object global library —
        // Hub only broadcasts macro *ids* over MACRO_SYNC (see
        // ArchitectViewModel.OnBusMacroSync), there is no body to import — so the
        // achievable robust behavior is: keep a MacroId that resolves against the
        // live Graph.Macros set (cross-canvas copy within the same graph keeps
        // its link), keep one whose id is known-global so the rail's existing
        // auto-import path can resolve it on edit, and STRIP any id that resolves
        // to neither so the node degrades to an unlinked Macro.Call rather than
        // exporting a dangling reference (ScriptExporter.CheckMacroCallOrphans
        // would otherwise flag it at export time). Runs before the ID remap loop
        // so the cloned nodes carry clean attributes. Repeatable, so it logs via
        // GlobalLogger rather than surfacing a modal.
        ValidatePastedMacroReferences(snap.Nodes);

        PushUndo();

        if (Math.Abs(dx - 24) < 0.5 && Math.Abs(dy - 24) < 0.5
            && snap.Nodes.Count > 0)
        {
            double cx = 0, cy = 0;
            foreach (var sn in snap.Nodes) { cx += sn.Location.X; cy += sn.Location.Y; }
            cx /= snap.Nodes.Count;
            cy /= snap.Nodes.Count;
            var cursor = HostToCanvas(_lastHostPoint);
            dx = cursor.X - cx;
            dy = cursor.Y - cy;
        }

        var nodeIdMap   = new Dictionary<string, string>();
        var socketIdMap = new Dictionary<string, string>();
        var newNodes = new List<NodeViewModel>();
        // Track pasted wire VMs so DEL right after paste reverses
        // the whole paste atomically. Pre-fix the wire half stayed unselected
        // and the user got dangling/orphan wires after a DEL on the pasted
        // selection.
        var newLinks = new List<LinkViewModel>();

        foreach (var srcNode in snap.Nodes)
        {
            // On the paste path a failed clone is SKIPPED
            // (logged inside CloneNode) — pasting the shared source instance
            // here would alias the clipboard payload into the live graph and
            // corrupt a subsequent paste. Links referencing the skipped node id
            // are dropped by the nodeIdMap lookup miss in the wire pass below.
            var clone = CloneNode(srcNode);
            if (clone is null) continue;
            // Reject a pasted Macro/Process Entry/Exit singleton when the
            // target graph already has one (or an earlier clone in this same paste
            // added it). Skipping before the id-map registration means any wire to
            // the skipped node is dropped by the nodeIdMap lookup miss below.
            if (WouldDuplicateSubGraphSingleton(_vm.Graph, clone.Title)) continue;
            string newId = Guid.NewGuid().ToString();
            nodeIdMap[clone.Id] = newId;
            clone.Id = newId;
            foreach (var s in clone.Sockets)
            {
                string newSid = Guid.NewGuid().ToString();
                socketIdMap[s.Id] = newSid;
                s.Id = newSid;
            }
            clone.Location = new System.Drawing.Point(
                clone.Location.X + (int)dx,
                clone.Location.Y + (int)dy);
            _vm.Graph.Nodes.Add(clone);
            var vm = new NodeViewModel(clone);
            _vm.Nodes.Add(vm);
            newNodes.Add(vm);
        }

        foreach (var srcLink in snap.Links)
        {
            if (!nodeIdMap.TryGetValue(srcLink.FromNodeId, out var fromN)) continue;
            if (!nodeIdMap.TryGetValue(srcLink.ToNodeId,   out var toN))   continue;
            if (!socketIdMap.TryGetValue(srcLink.FromSocketId, out var fromS)) continue;
            if (!socketIdMap.TryGetValue(srcLink.ToSocketId,   out var toS))   continue;
            var link = new Link
            {
                Id = Guid.NewGuid().ToString(),
                FromNodeId = fromN, ToNodeId = toN,
                FromSocketId = fromS, ToSocketId = toS,
            };
            _vm.Graph.Links.Add(link);
            var lvm = new LinkViewModel(link, _vm.Graph);
            _vm.Links.Add(lvm);
            newLinks.Add(lvm);
        }

        // 0.10.0 — also paste any captured frames, offset by the same (dx, dy).
        var newFrames = new List<FrameViewModel>();
        foreach (var srcFrame in snap.Frames)
        {
            // Skip a frame whose clone failed (logged in
            // CloneFrame) rather than aliasing the source frame into the graph.
            var frame = CloneFrame(srcFrame);
            if (frame is null) continue;
            // Re-uniquify id and offset bounds — Frame model uses a Rectangle Bounds.
            frame.Id = Guid.NewGuid().ToString();
            var b = frame.Bounds;
            frame.Bounds = new System.Drawing.Rectangle(
                b.X + (int)dx, b.Y + (int)dy, b.Width, b.Height);
            _vm.Graph.Frames.Add(frame);
            var fvm = new FrameViewModel(frame);
            _vm.Frames.Add(fvm);
            newFrames.Add(fvm);
        }

        _vm.Graph.MarkStructuralChange();
        try { NodeRegistry.ResolveWildcardCascade(_vm.Graph); } catch { /* best effort */ }
        // Notify only the freshly-pasted nodes, not
        // the entire canvas. Pasted links only ever connect pasted nodes (the
        // remap above drops any link whose endpoints aren't both in the pasted
        // set), so the cascade can only have re-typed pasted-node sockets —
        // existing nodes are untouched. Pre-fix this walked every node on the
        // canvas, firing ~4K redundant socket notifications on a 200-node graph.
        foreach (var n in newNodes)
        {
            foreach (var s in n.Inputs)  s.NotifyDataTypeChanged();
            foreach (var s in n.Outputs) s.NotifyDataTypeChanged();
        }
        _vm.OnGraphMutated();

        _vm.SetMultiSelection(newNodes);
        // SetMultiSelection clears the wire + frame halves of the multi-set
        // (ClearMultiSelection runs inside SetMultiSelection's first line);
        // restore the wire + frame selection with the freshly-pasted instances
        // so a single DEL right after paste reverses the whole paste
        // atomically. Pre-fix the wire half stayed unselected and a
        // DEL after paste dropped the pasted nodes + frames but silently
        // retained the pasted wires as dangling/orphan wires.
        if (newLinks.Count  > 0) _vm.SetSelectedLinks(newLinks);
        if (newFrames.Count > 0) _vm.SetSelectedFrames(newFrames);
    }

    /// <summary>
    /// Validate + clean the MacroId attribute on every
    /// pasted Macro.Call node. A MacroId is kept when it resolves against the
    /// live <c>_vm.Graph.Macros</c> set (intra-graph copy keeps its link) or
    /// against the host AVM's known-global macro id set (the rail's existing
    /// auto-import path resolves it on edit). An id that resolves to neither is
    /// stripped so the node degrades to an unlinked Macro.Call instead of
    /// exporting a dangling reference. Mutates the supplied node list in place;
    /// safe on a null/empty AVM. Logs a single summary line via GlobalLogger
    /// when anything was stripped (repeatable guardrail — never a modal).
    /// </summary>
    private void ValidatePastedMacroReferences(List<Node> nodes)
    {
        if (_vm is null || nodes.Count == 0) return;

        var liveMacroIds = new HashSet<string>(
            _vm.Graph.Macros.Select(m => m.MacroId),
            StringComparer.OrdinalIgnoreCase);
        // The AVM only retains macro ids (no full-object library in WinUI), so
        // a known-global id is resolvable-on-edit and must NOT be stripped.
        var globalMacroIds = GetKnownGlobalMacroIds();

        int stripped = 0;
        foreach (var n in nodes)
        {
            if (n.Attributes is null) continue;
            // Only Macro.Call nodes carry a meaningful MacroId; tolerate the
            // attribute appearing on any node by gating on its presence rather
            // than the title so a future macro-bearing node type is covered.
            if (!n.Attributes.TryGetValue("MacroId", out var mid)) continue;
            if (string.IsNullOrEmpty(mid)) continue;
            if (liveMacroIds.Contains(mid)) continue;
            if (globalMacroIds.Contains(mid)) continue;

            n.Attributes.Remove("MacroId");
            stripped++;
        }

        if (stripped > 0)
        {
            GlobalLogger.Log(
                $"clipboard.paste.macro-unresolved:{stripped} Macro.Call reference(s) stripped — target macro is neither in this graph nor a known global macro.",
                source: "Architect.LogicCanvasView",
                level: LogLevel.Communication);
        }
    }

    /// <summary>
    /// Best-effort snapshot of the macro ids the host AVM currently considers
    /// globally available. The AVM exposes global macros only as a transient
    /// MACRO_SYNC event (no retained queryable collection / no full-object
    /// library), so we mirror the latest broadcast into a per-canvas cache via
    /// <see cref="TrackKnownGlobalMacroIds"/> and read it back here. Returns an
    /// empty set when nothing has been broadcast yet — the conservative default
    /// that lets an unresolved id be stripped rather than left dangling.
    /// </summary>
    private HashSet<string> GetKnownGlobalMacroIds()
        => _knownGlobalMacroIds ?? s_emptyMacroIds;

    private static readonly HashSet<string> s_emptyMacroIds =
        new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>? _knownGlobalMacroIds;

    /// <summary>
    /// Public hook so the host (MainView /
    /// ArchitectSiblingWindow, which already subscribe to
    /// <c>ArchitectViewModel.GlobalMacroIdsChanged</c> and forward to the rail)
    /// can also feed the latest global macro id set into this canvas. Idempotent
    /// and null-tolerant; an empty / null set clears the cache. Kept narrow so
    /// paste-time validation can distinguish "resolvable on edit" from "truly
    /// orphaned" without holding a full-object macro library.
    /// </summary>
    public void TrackKnownGlobalMacroIds(IEnumerable<string>? ids)
    {
        if (ids is null)
        {
            _knownGlobalMacroIds = null;
            return;
        }
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
            if (!string.IsNullOrEmpty(id)) set.Add(id);
        _knownGlobalMacroIds = set.Count == 0 ? null : set;
    }

    /// <summary>
    /// Ctrl+D — duplicate the current selection without touching the OS
    /// clipboard. Pre-fix DuplicateSelection delegated to Copy()+Paste(),
    /// which wrote through Clipboard.SetContent and destroyed whatever the
    /// user had in their system clipboard from another app. The in-process
    /// path snapshots the selection and
    /// applies it directly; ApplyPaste does its own PushUndo so duplicate
    /// is still a single undo entry.
    /// </summary>
    private void DuplicateSelection()
    {
        var snap = CaptureSelectionSnapshot();
        if (snap is null) return;
        ApplyPaste(snap, 24, 24);
    }

    // All three clone helpers route through s_cloneJson so
    // System.Drawing.Color slots (Frame.FrameColor today; future
    // Node/Link color attributes) survive the copy/paste round-trip. The
    // stock JsonSerializer.Serialize path silently dropped them to
    // Color.Empty because System.Drawing.Color's public surface has no
    // settable properties — the deserializer needs ClipboardColorConverter
    // to repopulate from the ARGB int (or legacy {R,G,B,A} object form).
    //
    // The Serialize/Deserialize pair was previously
    // unguarded and used the null-forgiving (!) operator, so a malformed
    // Node/Link/Frame (recursion-cycle attribute, NaN double, oversized
    // collection, etc.) would throw JsonException / NullReferenceException
    // straight up through Copy / Paste / Duplicate and crash the operation.
    // Each clone now returns null on failure and logs once; the capture path
    // (Copy / Duplicate) falls back to the SOURCE object so a copy still
    // succeeds with the original instance, while ApplyPaste skips the failed
    // object (its callers below filter nulls). Repeatable, so it logs via
    // GlobalLogger rather than surfacing a modal.
    private static Node? CloneNode(Node src)
    {
        try
        {
            var json = JsonSerializer.Serialize(src, s_cloneJson);
            return JsonSerializer.Deserialize<Node>(json, s_cloneJson);
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"clipboard.clone.node-failed:{src.Title} ({src.Id}) — {ex.GetType().Name}: {ex.Message}",
                source: "Architect.LogicCanvasView",
                level: LogLevel.Communication);
            return null;
        }
    }

    private static Link? CloneLink(Link src)
    {
        try
        {
            var json = JsonSerializer.Serialize(src, s_cloneJson);
            return JsonSerializer.Deserialize<Link>(json, s_cloneJson);
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"clipboard.clone.link-failed:{src.Id} — {ex.GetType().Name}: {ex.Message}",
                source: "Architect.LogicCanvasView",
                level: LogLevel.Communication);
            return null;
        }
    }

    private static Phoenix.Controls.Shared.Models.Frame? CloneFrame(Phoenix.Controls.Shared.Models.Frame src)
    {
        try
        {
            var json = JsonSerializer.Serialize(src, s_cloneJson);
            return JsonSerializer.Deserialize<Phoenix.Controls.Shared.Models.Frame>(json, s_cloneJson);
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"clipboard.clone.frame-failed:{src.Id} — {ex.GetType().Name}: {ex.Message}",
                source: "Architect.LogicCanvasView",
                level: LogLevel.Communication);
            return null;
        }
    }
}

/// <summary>
/// V13 §8.4 — the CONSUMER half of the Visualist input contract: read the
/// <c>ConsumedKeys</c> member off a <c>VisualTriggerSnippet</c> clipboard payload and
/// seed one variable pin per key on the pasted <c>Visual.Trigger</c> node.
///
/// <para>
/// The producer (<c>Visualist.WinUI/Clipboard/VisualTriggerSnippetProducer</c>) scans the
/// copied widget graph for the eventData keys it actually reads and writes them onto the
/// clipboard beside the four addressing fields. Before this type existed the keys were
/// CARRIED and never read — <c>Deserialize&lt;VisualTriggerSnippet&gt;</c> skips the
/// unmapped member, so the whole contract terminated at the clipboard.
/// </para>
/// <para>
/// Split out of <see cref="LogicCanvasView"/> as a plain static type on purpose: the paste
/// path itself needs a XamlRoot, a live ViewModel and a clipboard, so the only way this
/// half gets real coverage is a seam that does not. It also keeps the diff inside the
/// canvas to a single call — another box may be working in Architect.
/// </para>
/// <para>
/// The member name is written out as a LITERAL here and as a record property on the
/// producer side. Deliberately NOT hoisted into a shared constant: two components that
/// must agree on a wire name have to be able to DISAGREE for a test to be worth
/// anything. V13's first pass hoisted exactly this into one constant, asserted the
/// constant against output built from the same constant, and shipped a dead feature past
/// a green suite.
/// </para>
/// </summary>
public static class VisualTriggerPinSeeder
{
    /// <summary>
    /// Upper bound on seeded pins. The real key set comes from a graph scan and runs to a
    /// handful; the cap exists because ANY process can put this clipboard format on the
    /// system clipboard, and a payload with ten thousand keys would otherwise build a node
    /// with ten thousand socket rows. Excess keys are dropped with one System-tier line
    /// (repeatable rejection ⇒ log, never a modal).
    ///
    /// <para>
    /// This bounds the key COUNT only. The key CONTENT is bounded by
    /// <see cref="IsLegalConsumedKey"/>, enforced in <see cref="ReadConsumedKeys"/> —
    /// which is the half that matters more, because the key becomes a socket NAME and the
    /// exporter emits that name unquoted into the generated <c>.phx</c>.
    /// </para>
    /// </summary>
    public const int MaxSeededPins = 32;

    // The fixed template pins on Visual.Trigger. LayerID / WidgetID / TriggerName are
    // attribute-backed addressing inputs and Args is the positional Collection input;
    // VisualTriggerHandler excludes exactly these four names (plus Flow) when it folds the
    // node's remaining inputs into trailing key=value args, so seeding a pin that collides
    // with one would either shadow an addressing pin or vanish from the export. Matched
    // case-INSENSITIVELY, which is stricter than the exporter's Ordinal compare on
    // purpose: "args" would slip past the exporter's filter and emit a bare `args=` arg.
    private static readonly HashSet<string> FixedPinNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Flow", "LayerID", "WidgetID", "TriggerName", "Args",
        };

    /// <summary>
    /// Longest key this will accept. An eventData key is <c>Args1..N</c> / <c>user</c> /
    /// <c>message</c> or a short author-typed name; 64 characters is generous for all of
    /// them and bounds the socket-row label a hostile payload can produce.
    /// </summary>
    public const int MaxKeyLength = 64;

    /// <summary>
    /// True when <paramref name="key"/> is a legal eventData key — i.e. one that can
    /// survive the whole pipeline it is about to enter. Charset is
    /// <c>A-Z a-z 0-9 _ . -</c>, non-empty, at most <see cref="MaxKeyLength"/> chars.
    ///
    /// <para>
    /// This is a SECURITY boundary, not tidiness. The key becomes a
    /// <c>Socket.Name</c>, and <c>VisualTriggerHandler</c> emits <c>{s.Name}={value}</c>
    /// <b>UNQUOTED</b> into the generated <c>.phx</c> — so a key holding a comma, a
    /// double quote, an <c>=</c>, a <c>|</c>, a paren or whitespace does not merely look
    /// wrong, it RE-SHAPES the emitted command line: <c>ScriptEngine.SplitArgs</c> is
    /// comma-/quote-/paren-aware, the KvPairs binder splits on the first <c>=</c> and
    /// trims, and <c>|</c> is the binder's own single-arg pair separator. The seeder's
    /// cap above bounds the COUNT of keys a hostile clipboard can inject; this bounds
    /// their CONTENT, which the cap never addressed.
    /// </para>
    /// <para>
    /// REJECT, never sanitise: a silently repaired key would seed a pin whose name the
    /// widget graph does not read, which is the same class of lie as dropping one. The
    /// author can still add the pin by hand from the <c>"+ variable"</c> slot.
    /// </para>
    /// <para>
    /// Two known, deliberate consequences. (1) The producer's <c>Message.Read</c> arm
    /// does NOT trim its key, so a widget really can read <c>" Args4 "</c> — that key is
    /// rejected here, correctly, because it could never round-trip anyway: both
    /// <c>SplitArgs</c> and the binder trim, so the pin would deliver <c>Args4</c> and
    /// never the spaced key the browser looks up. (2) Non-ASCII is rejected; Hub's
    /// delivered key space is ASCII and a Unicode socket name buys nothing.
    /// </para>
    /// </summary>
    public static bool IsLegalConsumedKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (key!.Length > MaxKeyLength) return false;
        foreach (char c in key)
        {
            bool ok = (c >= 'A' && c <= 'Z')
                   || (c >= 'a' && c <= 'z')
                   || (c >= '0' && c <= '9')
                   || c == '_' || c == '.' || c == '-';
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// Pull the <c>ConsumedKeys</c> string array out of a raw snippet payload. Returns an
    /// empty list for anything that is not a usable list — absent member, wrong JSON type,
    /// malformed document, a payload written by a Visualist build that predates the
    /// contract. Never throws: a paste must still spawn its node when the pre-fill can't
    /// be read.
    ///
    /// <para>
    /// This is also the CHARSET GATE (see <see cref="IsLegalConsumedKey"/>): it is the one
    /// place a clipboard string crosses into becoming a socket name, so it is the one
    /// place that has to validate. Illegal entries are dropped and reported once per
    /// payload at System tier — the legal keys in a mixed payload are still seeded, since
    /// they are still worth pre-filling.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> ReadConsumedKeys(string? rawSnippetJson)
    {
        if (string.IsNullOrWhiteSpace(rawSnippetJson)) return Array.Empty<string>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(rawSnippetJson!);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return Array.Empty<string>();
            // Exact-case probe, matching the strictness of the default-options
            // Deserialize<VisualTriggerSnippet> call this sits beside.
            if (!doc.RootElement.TryGetProperty("ConsumedKeys", out var arr)) return Array.Empty<string>();
            if (arr.ValueKind != System.Text.Json.JsonValueKind.Array) return Array.Empty<string>();

            var keys = new List<string>(arr.GetArrayLength());
            int rejected = 0;
            string? firstRejected = null;
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                string? k = el.GetString();
                if (string.IsNullOrWhiteSpace(k)) continue;
                if (!IsLegalConsumedKey(k))
                {
                    rejected++;
                    // Truncated + only the first one, so a hostile payload cannot use
                    // the log line as its own output channel.
                    firstRejected ??= k!.Length <= 32 ? k : k.Substring(0, 32) + "…";
                    continue;
                }
                keys.Add(k!);
            }

            if (rejected > 0)
            {
                GlobalLogger.Log(
                    $"clipboard.visual-trigger.key-rejected:{rejected} consumed key(s) dropped — "
                    + $"a key must be [A-Za-z0-9_.-] and at most {MaxKeyLength} chars (first: '{firstRejected}'). "
                    + "Add any missing pin by wiring the node's \"+ variable\" slot.",
                    source: "Architect.LogicCanvasView",
                    level: LogLevel.System);
            }
            return keys;
        }
        catch
        {
            // Malformed payload — the addressing half already deserialized fine or we
            // wouldn't be here, so degrade to "no pre-fill" rather than failing the paste.
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Append one String input socket per key to <paramref name="node"/>, immediately
    /// ABOVE the trailing <c>"+ variable"</c> placeholder so the placeholder stays last
    /// (the same insert-above-placeholder rule <c>PlaceholderActivator</c> follows).
    /// Returns the number of pins seeded.
    ///
    /// <para>
    /// Additive only: existing socket Ids are never touched, nothing is renamed and
    /// nothing is removed, so no <c>Link</c> can be left dangling. A key that duplicates
    /// a socket the node already carries is skipped — socket NAME is what
    /// <c>VisualTriggerHandler</c> emits as the arg key, so two same-named pins would be
    /// an ambiguous export rather than a richer node.
    /// </para>
    /// </summary>
    public static int SeedConsumedKeyPins(Node? node, IReadOnlyList<string>? consumedKeys)
    {
        if (node is null || consumedKeys is null || consumedKeys.Count == 0) return 0;
        if (node.Sockets is null) node.Sockets = new List<Socket>();

        // Insert above the trailing placeholder; append at the end when the node has none
        // (a hand-edited graph, or a template that stopped carrying one).
        int insertAt = node.Sockets.FindIndex(s => s is not null
                                              && s.IsPlaceholder
                                              && s.Type == SocketType.Input);
        if (insertAt < 0) insertAt = node.Sockets.Count;

        int seeded = 0;
        bool capped = false;
        foreach (string rawKey in consumedKeys)
        {
            if (string.IsNullOrWhiteSpace(rawKey)) continue;
            if (FixedPinNames.Contains(rawKey)) continue;
            if (node.Sockets.Exists(s => s is not null
                                    && string.Equals(s.Name, rawKey, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (seeded >= MaxSeededPins) { capped = true; break; }

            // DataType-at-creation drives the pin's shape and its wire compatibility
            // (project_winui_socket_datatype_at_creation) — a String pin minted at the
            // Any default renders as a diamond and refuses String wires until
            // save+reload. Same colour/type pair PlaceholderActivator.Activate uses when
            // no source socket types the new pin.
            node.Sockets.Insert(insertAt++, new Socket
            {
                Name     = rawKey,
                Type     = SocketType.Input,
                Color    = NodeRegistry.ColString,
                DataType = NodeRegistry.DataTypeFromColorPublic(NodeRegistry.ColString),
            });
            seeded++;
        }

        if (capped)
        {
            GlobalLogger.Log(
                $"clipboard.visual-trigger.pins-capped:{consumedKeys.Count} consumed key(s) offered, "
                + $"{MaxSeededPins} seeded — the rest were dropped. Add any missing pin by wiring the "
                + "node's \"+ variable\" slot.",
                source: "Architect.LogicCanvasView",
                level: LogLevel.System);
        }

        if (seeded > 0)
        {
            // Re-stripe the persisted socket Y offsets the way Activate does. The WinUI
            // canvas derives pin positions from row index rather than from Socket.Offset,
            // so this is for the serialized form and any offset-reading consumer.
            PlaceholderActivator.RecalculateSocketOffsets(node);
        }
        return seeded;
    }
}
