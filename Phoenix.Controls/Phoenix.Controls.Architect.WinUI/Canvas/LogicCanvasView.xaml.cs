using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Phoenix.Controls.Architect.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Windows.Foundation;
using Windows.System;

// Phoenix.Controls.Shared.Models.Frame (was SovereignFrame, the
// script-graph comment-region model) collides with WinUI's
// Microsoft.UI.Xaml.Controls.Frame (navigation control). Alias the model
// type so call sites in this file unambiguously construct the script-graph
// frame.
using GraphFrame = Phoenix.Controls.Shared.Models.Frame;

// The enclosing namespace `Phoenix.Controls.Architect.WinUI.Canvas` shadows
// `Microsoft.UI.Xaml.Controls.Canvas` (this file's manual children code
// targets the latter for Canvas.SetLeft/SetTop/Children). Alias to keep the
// call sites readable.
using XamlCanvas = Microsoft.UI.Xaml.Controls.Canvas;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

// The actual canvas surface that hosts node + wire rendering and
// owns the input state machine (pan / zoom / select / drag / wire-drop /
// context menus / keyboard shortcuts).
//
// Input model (mirrors Canvas.Mouse + .Keyboard from the WinForms
// project):
//
//   * left-click on node body  → select (Ctrl adds to multi-select; future)
//   * left-drag on selected node → translate node, ripple wire recompute
//   * left-press on socket pin → start wire-drop, release on compatible
//     target socket creates a Link; release elsewhere cancels
//   * right-click empty   → spawn palette (NodeRegistry templates)
//   * right-click node    → node context menu (delete / duplicate)
//   * right-click link    → link context menu (delete)
//   * middle-drag / right-drag-on-empty → pan the view
//   * mouse wheel         → zoom around cursor (clamped to LogicCanvasViewModel.Zoom)
//   * DEL                 → delete selection
//   * Esc                 → cancel in-flight wire / marquee
//   * Ctrl+O / Ctrl+S     → bubbled to ArchitectViewModel.Open / Save (handled in MainWindow)
//
// Multi-select via marquee, quick-key spawn, copy/paste, undo/redo and frame
// drag/resize remain on the deferred list — landing them is a follow-up
// once the .phxg → .phx round-trip closes.
public sealed partial class LogicCanvasView : UserControl, Phoenix.Controls.Architect.WinUI.Services.EventPairLiveSync.IPeer
{
    private LogicCanvasViewModel? _vm;
    private UndoRedoController? _history;

    // Node + frame views are mounted directly into NodeLayer / FrameLayer
    // (plain Canvases, no ItemsControl) and tracked here so CollectionChanged
    // can incrementally add/remove without rebuilding the whole layer.
    // ItemsControl.ItemContainerStyle Setter Value="{Binding X/Y}" is
    // broken in WinUI 3 — Setter bindings don't resolve against the
    // container's data context, so every node piled at (0,0). Manual
    // Canvas.SetLeft/Top is the proven pattern (Visualist's
    // WidgetGraphCanvas uses the same).
    // LAZILY-populated map of full NodeViews. A node only gets a
    // (heavy ~48-149-element) NodeView built once it is first realized FULL (zoomed
    // in + on-screen) — see EnsureFullView in LogicCanvasView.Lod.cs. Nodes that
    // are only ever viewed zoomed-out live as proxies (_nodeProxies) and never
    // appear here, so a big zoomed-out graph never pays the per-node construction
    // cost. Once built, a NodeView is kept alive across cull/regime transitions
    // (keep-alive) for cheap re-mount. This is NO LONGER "every node" — the
    // authoritative "node is known" set is _subscribedNodes (Lod.cs).
    private readonly Dictionary<NodeViewModel, NodeView>          _nodeViews  = new();
    // Subset of _nodeViews currently mounted FULL in NodeLayer.Children. With
    // _enableNodeVirtualization == false every node is here (legacy mount-all,
    // byte-identical). With it true, only on-screen + zoomed-in nodes are; the
    // zoomed-out on-screen ones are proxies (_realizedProxy) and off-screen ones
    // are nothing — so the framework measure/arrange walk is O(visible-full)
    // instead of O(all). See LogicCanvasView.Lod.cs for the realization machinery.
    private readonly HashSet<NodeViewModel> _realizedNodes = new();
    private bool _enableNodeVirtualization = true;    // DEFAULT ON (0.12.41); Ctrl+Alt+F6 toggles OFF as a kill-switch
    private bool _cullDirty;                           // a pan/zoom/load asked for a re-cull
    private int _cullSettleFrames;                     // ticks the viewport has been stable since the last change
    private const int CullSettleThreshold = 3;         // re-cull only after the gesture has been still this many ticks
    private Windows.Foundation.Rect _cullBand;         // visible rect inflated by margin (hysteresis band)
    private const double CullMarginFactor = 1.0;       // band = viewport ± 1× viewport; tune empirically
    private readonly Dictionary<FrameViewModel, FrameworkElement> _frameViews = new();

    public LogicCanvasView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // Each NodeView repaints its own border on
        // ActualThemeChanged; wires can't (LinkViewModel isn't a FrameworkElement),
        // so the canvas nudges every link to repaint its theme-derived stroke
        // when the OS theme / high-contrast mode switches at runtime.
        ActualThemeChanged += OnCanvasActualThemeChanged;
    }

    private void OnCanvasActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_vm is null) return;
        foreach (var link in _vm.Links) link.RefreshThemeBrushes();
        // Full NodeViews repaint their own borders on theme change
        // (NodeView.OnActualThemeChanged); zoomed-out proxies aren't NodeViews, so
        // re-resolve the proxy brushes + repaint the mounted proxies here too.
        RefreshProxyThemeBrushes();
        // Re-resolve the immediate renderer's palette
        // + repaint so the GPU canvas flips theme in lockstep with the retained path.
        RefreshImmediateThemeColors();
    }

    /// <summary>The active canvas VM (or null before DataContext binds).</summary>
    public LogicCanvasViewModel? ViewModel => _vm;

    /// <summary>
    /// Optional reference to the top-level <see cref="ViewModels.ArchitectViewModel"/>
    /// for this canvas. The canvas itself doesn't need it for rendering, but the
    /// Macro.Call / Process.Spawn context-menu items use it to pass the AVM
    /// through to <c>SubGraphWindow.Open*Editor</c> so the open editor can
    /// subscribe to MacroRenamed / ProcessRenamed events.
    /// </summary>
    public ViewModels.ArchitectViewModel? ArchitectVm { get; set; }

    /// <summary>
    /// Snapshot the current graph onto the undo stack. Call before any
    /// mutation (add/remove/translate/wire-drop). Drag-style mutations push
    /// once at drag-start, not on every PointerMoved.
    /// </summary>
    private void PushUndo() => _history?.Push();

    /// <summary>
    /// Public undo push used by the node-body inline editors (pill value
    /// edit, socket rename) so a typing session creates exactly one
    /// undo entry on the edit-start transition. Pre-fix the TwoWay+
    /// PropertyChanged binding wrote each keystroke directly into
    /// <c>Node.Attributes</c> with no snapshot, so Ctrl+Z after pill edit
    /// silently rewound past the edit into a prior structural change.
    /// </summary>
    public void PushUndoForInlineEdit() => _history?.Push();

    /// <summary>
    /// Called by <see cref="NodeView"/> when an inline <c>EventName</c> pill commit
    /// renames an Event.Trigger / Event.Executor / Event.Return node. Renaming an
    /// event changes its pairing identity, so — exactly like the context-menu retype
    /// path (<c>SetDynamicSocketType</c>) — re-run the Event-pair sync so the node's
    /// payload-socket shape reaches its now-matching peers in-graph, in other open
    /// windows, and on disk, then refresh the unpaired red-border error state. The
    /// event NAME itself never cascades to peers (signal/slot decoupling); only
    /// socket shape follows the pairing.
    /// </summary>
    internal void NotifyEventNameChangedFromNodeView(Node nodeModel)
    {
        if (_vm is null || nodeModel is null) return;
        if (nodeModel.Title is not ("Event.Trigger" or "Event.Executor" or "Event.Return")) return;

        // ADOPT-ON-JOIN: assigning an EventName makes this node JOIN an event. Before
        // pushing its shape onto peers, let it adopt the event's existing canonical
        // (per-channel richest) shape — in-graph AND from sibling .phxg files — so a
        // new/empty node fits the existing definition instead of wiping the defined
        // peers with its empty shape (the "assign a new executor to an existing event
        // RESETS everything" bug). After adopting, this node matches the group, so the
        // pushes below are a no-op for the already-defined peers.
        //
        // Only pay the synchronous sibling-.phxg scan when the IN-GRAPH group has no
        // definer yet — the common same-file case (a peer already defines the event in
        // this graph) costs zero disk IO, so a large project can't freeze the UI on an
        // EventName commit. When the definer lives only in another file, scan for it.
        var crossFilePeers = InGraphEventGroupHasPayload(nodeModel)
            ? new System.Collections.Generic.List<Node>()
            : LoadCrossFileEventPeers(nodeModel);
        PlaceholderActivator.AdoptEventShapeOnJoin(_vm.Graph, nodeModel, crossFilePeers);

        // In-graph: propagate the (now-canonical) socket shape across same-graph peers
        // that match the EventName (a no-op when the rename orphaned every peer — the
        // intended signal/slot behaviour). Rebuild this node's OWN view too — unlike a
        // plain push, adopt can have changed the node's own sockets.
        PlaceholderActivator.SyncEventPair(_vm.Graph, nodeModel);
        RebuildSocketsForMutatedNode(nodeModel);

        // Cross-file + live-to-open-windows, then re-evaluate which nodes are now
        // unpaired (a rename can pair or orphan both this node and its former peer).
        ScheduleCrossFileEventPairSync();
        RefreshEventPairErrorState();
        _vm.OnGraphMutated();
    }

    /// <summary>
    /// Called by <see cref="NodeView"/> when an inline socket-label rename commits on an
    /// Event.Trigger / Event.Executor / Event.Return payload bubble. The bubble NAME is
    /// part of the pair's wire-format contract (the exporter emits kv args keyed by
    /// socket name), so the rename must reach the in-graph peers immediately — pre-fix
    /// the commit only scheduled the debounced CROSS-FILE sync, the in-graph peers kept
    /// the old name, and the next pair-sync sourced from ANY of them (slot activation,
    /// context-menu retype, graph load) copied the stale name straight back over the
    /// user's rename ("bubble names are constantly resetted"). Runs the same tail as the
    /// context-menu retype (<c>SetDynamicSocketType</c>): in-graph sync → peer view
    /// rebuild → cross-file sync → unpaired-state refresh → graph-mutated.
    /// </summary>
    internal void NotifyEventSocketRenamedFromNodeView(Node nodeModel)
    {
        if (_vm is null || nodeModel is null) return;
        if (nodeModel.Title is not ("Event.Trigger" or "Event.Executor" or "Event.Return")) return;
        PlaceholderActivator.SyncEventPair(_vm.Graph, nodeModel);
        RebuildSocketsForMutatedNode(nodeModel);
        ScheduleCrossFileEventPairSync();
        RefreshEventPairErrorState();
        _vm.OnGraphMutated();
    }

    /// <summary>
    /// True when an IN-GRAPH Event.* node OTHER than <paramref name="nodeModel"/> shares
    /// its EventName and already carries at least one payload (arg/return) socket — i.e.
    /// the event is already defined within this graph, so the join can adopt from a
    /// same-graph peer and the sibling-file scan can be skipped.
    /// </summary>
    private bool InGraphEventGroupHasPayload(Node nodeModel)
    {
        if (_vm is null || nodeModel?.Attributes is null
            || !nodeModel.Attributes.TryGetValue("EventName", out var ev)
            || string.IsNullOrWhiteSpace(ev))
            return false;
        foreach (var n in _vm.Graph.Nodes)
        {
            if (ReferenceEquals(n, nodeModel)) continue;
            if (n.Title is not ("Event.Trigger" or "Event.Executor" or "Event.Return")) continue;
            if (n.Attributes is null || !n.Attributes.TryGetValue("EventName", out var e2)) continue;
            if (!e2.Equals(ev, StringComparison.OrdinalIgnoreCase)) continue;
            // Sockets guard mirrors the Attributes guard above — safe today only
            // via the load-time `Sockets ??= new` heal; keep it explicit so a
            // future non-load insertion path can't NRE here.
            if (n.Sockets is not null
                && n.Sockets.Any(s => !s.IsPlaceholder && s.Name != "Flow" && s.Name != "EventName"))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Load Event.Trigger / Event.Executor / Event.Return snapshots sharing
    /// <paramref name="nodeModel"/>'s EventName from every sibling <c>*.phxg</c> in the
    /// current file's directory (falling back to the canonical Hub logic folder),
    /// excluding the current file. Feeds
    /// <see cref="PlaceholderActivator.AdoptEventShapeOnJoin"/> so a node joining an
    /// event whose definer lives in ANOTHER file adopts that file's canonical socket
    /// shape rather than pushing its own (empty) shape onto it. Best-effort + synchronous
    /// — only invoked (see caller) when the in-graph group has no definer, so the common
    /// same-file case pays nothing and this one-time scan stays off the hot path.
    /// </summary>
    private System.Collections.Generic.List<Node> LoadCrossFileEventPeers(Node nodeModel)
    {
        var peers = new System.Collections.Generic.List<Node>();
        if (nodeModel?.Attributes is null
            || !nodeModel.Attributes.TryGetValue("EventName", out var ev)
            || string.IsNullOrWhiteSpace(ev))
            return peers;

        string projectDir = ResolveEventPeerScanDirectory();
        if (string.IsNullOrEmpty(projectDir) || !System.IO.Directory.Exists(projectDir)) return peers;

        static string? SafeFull(string p) { try { return System.IO.Path.GetFullPath(p); } catch { return null; } }
        string? selfFull = string.IsNullOrEmpty(_vm?.LoadedFilePath) ? null : SafeFull(_vm!.LoadedFilePath!);

        string[] files;
        try { files = System.IO.Directory.GetFiles(projectDir, "*.phxg"); }
        catch { return peers; }

        foreach (var f in files)
        {
            if (selfFull is not null && string.Equals(SafeFull(f), selfFull, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var g = GraphSerializer.LoadGraph(f);
                foreach (var n in g.Nodes)
                {
                    if (n.Title is not ("Event.Trigger" or "Event.Executor" or "Event.Return")) continue;
                    if (n.Attributes is null || !n.Attributes.TryGetValue("EventName", out var e2)) continue;
                    if (!e2.Equals(ev, StringComparison.OrdinalIgnoreCase)) continue;
                    peers.Add(n);
                }
            }
            catch { /* skip malformed / locked peer */ }
        }
        return peers;
    }

    /// <summary>
    /// The directory scanned for sibling <c>.phxg</c> event peers — by BOTH the
    /// adopt-on-join peer load (<see cref="LoadCrossFileEventPeers"/>) and the
    /// debounced cross-file push (<see cref="OnCrossFileSyncTimerTick"/>). The two
    /// MUST resolve identically: the adopt makes the group's shapes equal so the
    /// push is a no-op for already-defined peers. Historically the adopt preferred
    /// the open file's directory while the push always hit <c>Paths.HubLogic</c> —
    /// for a file living elsewhere, a just-named (still empty) node was pushed
    /// onto HubLogic peers the adopt never consulted, and the excess-drop wiped
    /// their authored bubbles on disk. Prefer the open file's own directory (its
    /// true siblings); fall back to the canonical Hub logic folder for unsaved
    /// graphs.
    /// </summary>
    private string ResolveEventPeerScanDirectory()
    {
        string? selfDir = string.IsNullOrEmpty(_vm?.LoadedFilePath)
            ? null
            : System.IO.Path.GetDirectoryName(_vm!.LoadedFilePath);
        return !string.IsNullOrEmpty(selfDir) && System.IO.Directory.Exists(selfDir)
            ? selfDir!
            : Phoenix.Controls.Shared.Core.Paths.HubLogic;
    }

    /// <summary>Public Undo entry point — used by Hub.MainWindow's menu dispatch
    /// (architect.edit.undo) to drive the same undo controller the canvas's
    /// Ctrl+Z keyboard handler uses.</summary>
    public void RequestUndoFromShell() { _history?.Undo(); }

    /// <summary>Public Redo entry point — paired with <see cref="RequestUndoFromShell"/>.</summary>
    public void RequestRedoFromShell() { _history?.Redo(); }

    /// <summary>
    /// Snapshot of the undo controller's CanUndo/CanRedo flags — surfaced so
    /// the Architect MainView can implement <c>ICanExecuteUndoRedo</c>
    /// without leaking the controller reference. Both false when no graph
    /// has been bound yet (DataContextChanged hasn't run).
    /// </summary>
    public bool CanUndo => _history?.CanUndo ?? false;

    /// <inheritdoc cref="CanUndo"/>
    public bool CanRedo => _history?.CanRedo ?? false;

    /// <summary>
    /// Re-broadcasts <see cref="UndoRedoController.CanExecuteChanged"/> so
    /// the parent MainView can forward to its own <c>ICanExecuteUndoRedo</c>
    /// subscribers. Resubscribed inside <see cref="OnDataContextChanged"/>
    /// each time the canvas binds to a new VM (which builds a fresh
    /// controller); a final synthetic raise on rebind tells listeners to
    /// re-poll <see cref="CanUndo"/> / <see cref="CanRedo"/>.
    /// </summary>
    public event EventHandler? UndoRedoChanged;

    private void OnHistoryCanExecuteChanged(object? sender, EventArgs e)
        => UndoRedoChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Public Frame-Selection entry point — used by Hub.MainWindow's menu dispatch
    /// (architect.view.frameSelection) to drive the same fit logic the canvas's F keyboard
    /// handler uses.</summary>
    public void RequestFrameSelectionFromShell() { FrameSelection(); }

    /// <summary>
    /// 0.10.0 — public focus-restore hook. MainView calls this after every
    /// chrome menu action / inspector / databank surface closes so the
    /// canvas-side keyboard accelerators (DEL / Ctrl+S / Ctrl+Space / etc.)
    /// keep working without an extra click on the canvas. Mirrors the
    /// SpawnPaletteFlyout.Closed pattern in LogicCanvasView.Palette.cs.
    /// Wrapped because Focus throws when the control isn't in the visual
    /// tree (tab swap mid-flight, headless tests).
    /// </summary>
    public void RestoreCanvasFocus()
    {
        try { Focus(FocusState.Programmatic); }
        catch { /* never break the caller on a focus failure */ }
    }

    /// <summary>0.10.0 — Edit → Group selection (chrome menu) / Ctrl+G
    /// keyboard chord entry point. No-op when fewer than two nodes are
    /// selected (CollapseSelectionToMacro guards on count).</summary>
    public void RequestGroupFromShell() => CollapseSelectionToMacro();

    /// <summary>Edit → Add comment frame (chrome menu) / bare-C keyboard chord
    /// entry point. Wraps the current node selection in a comment frame when
    /// one exists; otherwise drops a 240×160 gold-ember frame at the visible
    /// viewport centre. Kept in lockstep with the bare-C hotkey via
    /// <see cref="AddCommentFrameSmart"/>.</summary>
    public void RequestAddCommentFrameFromShell() => AddCommentFrameSmart();

    /// <summary>0.10.0 — View → Spawn node… (chrome menu) entry point. The
    /// canvas-side keyboard chord is Ctrl+Space; the chrome menu reuses
    /// the same view-centre anchor.</summary>
    public void RequestSpawnAtViewCenterFromShell() => ShowSpawnPaletteAtViewCenter();

    /// <summary>Public Find-Node entry point — used by Hub.MainWindow's menu dispatch
    /// (architect.edit.find / architect.help.nodeReference) and by the canvas Ctrl+F
    /// keyboard accelerator (LogicCanvasView.Keyboard.cs). 0.10.0 — opens a dedicated
    /// in-graph node finder Flyout (filters by Title against the active graph). The
    /// pre-0.10.0 placeholder reused ShowSpawnPaletteAtViewCenter, which surfaced the
    /// template catalog instead of the user's actual nodes.</summary>
    public void RequestFindNodeFromShell() { ShowNodeFinderFlyout(); }

    /// <summary>
    /// File → Welcome (architect.file.welcome) chrome entry point. Force-shows
    /// the always-available Welcome card overlay regardless of the current
    /// file / graph state. Architect has no cross-session graph persistence —
    /// it always starts on an empty canvas — so the Welcome card is the
    /// empty-start state, and re-showing it on demand is non-destructive: the
    /// open graph stays loaded behind the overlay (this method never mutates
    /// or closes it). Mirrors UpdateEmptyHint's WelcomeCard surfacing but
    /// bypasses the no-file + empty-graph gate so it works on demand even with
    /// a populated graph loaded. The card's own New / Open / Recent buttons
    /// (OnWelcomeNewClicked etc.) drive any subsequent state change; until the
    /// user picks one — or mutates the graph, which re-runs UpdateEmptyHint —
    /// the overlay simply sits on top.
    /// </summary>
    public void RequestShowWelcomeFromShell()
    {
        try
        {
            // File → Welcome is an explicit request for the card, so clear any
            // "blank canvas from New" suppression (set by BeginBlankCanvasFromNew)
            // — otherwise UpdateEmptyHint's next pass would hide the card again.
            _suppressWelcomeForNewGraph = false;
            WelcomeCard.Visibility = Visibility.Visible;
            // Match UpdateEmptyHint: keep the Samples picker current so a
            // between-session file removal / restore reflects on this surface.
            PopulateWelcomeSamples();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Architect.LogicCanvasView",
                "RequestShowWelcomeFromShell", ex);
        }
    }

    // "New Graph" on the Welcome card used to spawn a fresh
    // sibling window (which was itself empty → showed the Welcome card again,
    // "the same issue continues"). The hosts now clear the graph IN PLACE when
    // the canvas is blank (see MainView.OnFileNewRequested /
    // ArchitectSiblingWindow.OnFileNewClicked). But a fresh empty + no-file
    // graph is exactly the state UpdateEmptyHint shows the full Welcome card
    // for — so without this flag the card would just re-render in place and the
    // welcoming message would never "give way to the canvas". When set, an
    // empty no-file canvas shows the lightweight EmptyHint instead of the heavy
    // Welcome card. It self-clears the moment the canvas gains content or a
    // file (UpdateEmptyHint's `!noFile` reset) and on File → Welcome (above),
    // so Close-X / first-launch still land on the Welcome card as documented.
    private bool _suppressWelcomeForNewGraph;

    /// <summary>
    /// Host entry point for an in-place "New Graph": hide the Welcome card and
    /// show the blank editable canvas (lightweight EmptyHint) instead of
    /// re-asserting the full card. Call AFTER the host has reset the graph
    /// (ArchitectViewModel.NewGraph). Non-destructive — it only flips the
    /// empty-state surface and re-grabs canvas focus.
    /// </summary>
    public void BeginBlankCanvasFromNew()
    {
        _suppressWelcomeForNewGraph = true;
        UpdateEmptyHint();
        RestoreCanvasFocus();
    }

    /// <summary>
    /// Script → Sync Event Peers chrome entry point.
    /// Kicks the existing debounced cross-file Event-pair sync (the same
    /// path wire-drop and event-rename edits use). Manual-trigger surface
    /// for the user when they suspect a peer .phxg is out of sync (e.g.
    /// after an external edit landed via Architect's FileSystemWatcher).
    /// </summary>
    public void RequestSyncEventPeersFromShell() => ScheduleCrossFileEventPairSync();

    /// <summary>
    /// Sub-graph editor window callback — frame + flash the Macro.Call /
    /// Process.Spawn node identified by <paramref name="nodeId"/> on this
    /// canvas. Wired through <see cref="LogicCanvasViewModel.RequestRevealNode"/>
    /// so the SubGraphWindow's "Reveal call-site" button drives the parent
    /// canvas without holding a direct view reference. No-op when the
    /// canvas isn't bound, the node isn't in this graph, or the canvas
    /// isn't yet measured (Activate before first frame).
    /// </summary>
    public void RevealNodeFromShell(string nodeId)
    {
        if (_vm is null || string.IsNullOrEmpty(nodeId)) return;
        var match = _vm.FindNode(nodeId);
        if (match is null) return;
        try
        {
            _vm.SetMultiSelection(new[] { match });
            // Frame uses the current selection; SetMultiSelection just made
            // our match the selection so this zooms to it. ZoomToFit is the
            // fallback when the canvas isn't measured yet.
            FrameSelection();
            FlashNode(nodeId);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Architect.Canvas", $"RevealNodeFromShell '{nodeId}'", ex);
        }
    }

    /// <summary>
    /// Macro reference "Find All" with visual feedback — restores the
    /// WinForms baseline Canvas.DebugFlash.cs:HighlightMacroCallSites. Walks the
    /// graph for every Macro.Call node whose MacroId matches
    /// <paramref name="macroId"/>, flashes each one, replaces the selection with
    /// the matches, and frames the viewport to fit them. The WinUI port had only
    /// the bare select (FindMacroReferences in LeftRail) — no flash, no framing,
    /// no count. Returns the match count so the caller can surface "N references"
    /// in the rail. No-op (returns 0) when the canvas isn't bound or no Call node
    /// references the macro.
    /// </summary>
    public int HighlightMacroCallSites(string macroId)
    {
        if (_vm is null || string.IsNullOrEmpty(macroId)) return 0;

        var matches = new System.Collections.Generic.List<NodeViewModel>();
        foreach (var nvm in _vm.Nodes)
        {
            if (nvm.Title != "Macro.Call") continue;
            var attrs = nvm.Model.Attributes;
            if (attrs is null) continue;
            if (!attrs.TryGetValue("MacroId", out var mid) || mid != macroId) continue;
            matches.Add(nvm);
        }

        if (matches.Count == 0)
        {
            GlobalLogger.Log(
                $"Macro references: no Macro.Call sites found for the selected macro.",
                source: "Architect.Canvas",
                level: LogLevel.System);
            return 0;
        }

        try
        {
            // Replace selection with the matches, flash each so the eye catches
            // them mid-graph, then frame the viewport to the new selection.
            _vm.SetMultiSelection(matches);
            foreach (var m in matches) FlashNode(m.Id);
            FrameSelection();

            GlobalLogger.Log(
                matches.Count == 1
                    ? "Macro references: 1 call site found and framed."
                    : $"Macro references: {matches.Count} call sites found and framed.",
                source: "Architect.Canvas",
                level: LogLevel.System);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Architect.Canvas", $"HighlightMacroCallSites '{macroId}'", ex);
        }
        return matches.Count;
    }

    /// <summary>
    /// Canvas-level macro-edit hook — re-syncs every Macro.Call node on
    /// this canvas that references <paramref name="macro"/> to the macro's
    /// current Entry/Exit signature, then rebuilds the socket VMs of the call
    /// nodes whose model sockets actually changed and repaints. This is the
    /// entry point macro-sync paths (MACRO_SYNC arrival in ArchitectViewModel,
    /// SubGraphWindow.OnClosed after a macro edit) call so open Macro.Call nodes
    /// in sibling windows stop showing a stale signature without a manual
    /// re-open. Pre-fix no such path existed in WinUI; only the rail [G] prefix
    /// updated. No-op when the canvas isn't bound.
    /// </summary>
    public void RefreshMacroCallSitesForMacro(Macro macro)
    {
        if (_vm is null || macro is null) return;
        try
        {
            var mutated = _vm.RefreshAllCallNodesForMacro(macro);
            if (mutated.Count == 0) return;
            foreach (var id in mutated)
                _vm.FindNode(id)?.RebuildSockets();
            // Recompute wires so any dropped parameter-socket link clears and
            // the connectivity halo tracks the new socket set.
            _vm.OnGraphMutated();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Architect.Canvas", $"RefreshMacroCallSitesForMacro '{macro.MacroId}'", ex);
        }
    }

    /// <summary>Raised when the user requests Save (Ctrl+S). The shell wires this.</summary>
    public event EventHandler? SaveRequested;

    /// <summary>Raised when the user requests Open (Ctrl+O). The shell wires this.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>
    /// Raised when F1 fires with no node selected.
    /// MainView / ArchitectSiblingWindow forward to
    /// <c>KeyboardShortcutsDialog.ShowAsync(XamlRoot)</c>. Hosts that
    /// don't subscribe silently no-op (e.g. the sub-graph editor window),
    /// matching pre-fix behaviour for those surfaces.
    /// </summary>
    public event EventHandler? KeyboardShortcutsRequested;

    /// <summary>
    /// Raised when the user
    /// presses F4 (or picks View → Toggle Inspector) on the canvas. The
    /// canvas itself doesn't own the inspector column; MainView /
    /// ArchitectSiblingWindow subscribe and flip the visibility via the
    /// same plumbing the InspectorChevronButton uses. Hosts that don't
    /// subscribe (e.g. the SubGraphWindow sub-graph editor canvas, which
    /// has no inspector column) silently no-op.
    /// </summary>
    public event EventHandler? InspectorToggleRequested;

    /// <summary>
    /// 0.10.0 — left-button double-tap on a Macro.Call /
    /// Process.Spawn node opens the matching sub-graph editor window. Other
    /// hit targets (regular nodes, wires, frames, empty canvas) are no-ops
    /// so the double-tap doesn't accidentally trigger a node action.
    ///
    /// Double-tap on a frame label opens the same rename
    /// flyout the right-click context menu uses. The label-hit detection
    /// happens before the node branch so a double-tap that lands on a frame
    /// label inside a node-shaped sub-region (none exists today, but the
    /// canonical hit-test order matters) gets routed correctly.
    /// </summary>
    private void OnHostDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (_vm is null) return;
        // Resolve the double-tap target from the
        // model in immediate mode (no per-node visual elements to walk).
        var canvasPt = HostToCanvas(e.GetPosition(HostRoot));
        var hit = _useImmediateMode
            ? ResolveModelHit(canvasPt)
            : HitTagFrom(e.OriginalSource);

        // Frame header double-tap → rename. The retained path surfaces the
        // FrameViewModel from the Border's Tag and checks the inner TextBlock's
        // "label" tag; on the Win2D GPU canvas there is no such element, so
        // e.OriginalSource is the bare CanvasControl and IsFrameLabelHit always
        // returned false — the double-tap rename was silently dead there (same
        // visual-tree trap that broke frame move/resize). In immediate mode use
        // the canvas-space header band (IsFrameHeaderHitWin2D); ResolveModelHit
        // already only yields a frame for a header/edge hit, so a resize-edge
        // double-tap won't spuriously rename.
        if (hit is FrameViewModel frameHit
            && (_useImmediateMode
                    ? IsFrameHeaderHitWin2D(frameHit, canvasPt.X, canvasPt.Y)
                    : IsFrameLabelHit(e.OriginalSource)))
        {
            try
            {
                var hostPoint = e.GetPosition(HostRoot);
                ShowFrameRenameFlyout(frameHit, hostPoint);
                e.Handled = true;
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("Architect.LogicCanvasView", "OnHostDoubleTapped frame rename", ex);
            }
            return;
        }

        if (hit is not NodeViewModel n) return;
        try
        {
            if (n.Title == "Macro.Call"
                && n.Model.Attributes is not null
                && n.Model.Attributes.TryGetValue("MacroId", out var mid)
                && !string.IsNullOrEmpty(mid))
            {
                var macro = _vm.Graph.Macros.FirstOrDefault(m => m.MacroId == mid);
                // AVM required for shared undo + rename sync.
                if (macro is not null && ArchitectVm is not null)
                {
                    SubGraphWindow.OpenMacroEditor(macro, ArchitectVm, this);
                    e.Handled = true;
                }
            }
            else if (n.Title == "Process.Start"
                  && n.Model.Attributes is not null
                  && n.Model.Attributes.TryGetValue("ProcessId", out var pid)
                  && !string.IsNullOrEmpty(pid))
            {
                var proc = _vm.Graph.Processes.FirstOrDefault(p => p.ProcessId == pid);
                // AVM required for shared undo + rename sync.
                if (proc is not null && ArchitectVm is not null)
                {
                    SubGraphWindow.OpenProcessEditor(proc, ArchitectVm, this);
                    e.Handled = true;
                }
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Architect.LogicCanvasView", "OnHostDoubleTapped open editor", ex);
        }

        // In immediate mode, double-tapping a
        // plain node (not a Macro.Call / Process.Spawn, handled above) materializes
        // its real, editable NodeView over the GPU canvas so inline pill / socket-
        // rename editing works exactly as in the retained path.
        if (_useImmediateMode && !e.Handled)
        {
            EnterImmediateEdit(n);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 0.10.0 — raised by the Welcome card's "New Graph"
    /// button. MainView / ArchitectSiblingWindow subscribe and route to
    /// their existing File → New handler (which, in MainView, spawns a
    /// sibling Architect window per the multi-window spec; in the sibling
    /// window it just opens a fresh AVM).
    /// </summary>
    public event EventHandler? NewRequested;

    /// <summary>
    /// 0.10.0 — raised by the Welcome card's "Recent"
    /// button. Routes to the same RecentFilesDialog the chrome's
    /// File → Open Recent menu item uses.
    /// </summary>
    public event EventHandler? OpenRecentRequested;

    private void OnWelcomeNewClicked(object sender, RoutedEventArgs e)
        => NewRequested?.Invoke(this, EventArgs.Empty);

    private void OnWelcomeOpenClicked(object sender, RoutedEventArgs e)
        => OpenRequested?.Invoke(this, EventArgs.Empty);

    private void OnWelcomeRecentClicked(object sender, RoutedEventArgs e)
        => OpenRecentRequested?.Invoke(this, EventArgs.Empty);

    // ── Welcome card sample picker ─────────────────────────────────────────
    //
    // Pre-T15 WelcomeDialog.cs:63-80 surfaced a Starter / Intermediate /
    // Advanced browser of seed `.phxg` files shipped under
    // `Phoenix.Controls.Hub/data/logic/examples/`. The retirement to WinUI
    // collapsed the welcome card down to New / Open / Recent buttons and
    // dropped the sample picker — restore it here against the shipped seed
    // files. Tiering follows the file-number prefix conventions in the
    // examples folder (01 = starter, 02–04 = intermediate, 05–07 = advanced).
    //
    // The registry is static — Phoenix Controls ships a fixed set of samples
    // per release. Stale-entry pruning grays a row whose `.phxg` is missing
    // from disk at runtime (mirroring `RecentFilesDialog.Row.Opacity`), so
    // a user who deleted or moved a sample sees the row dimmed rather than
    // a silently-broken click.

    /// <summary>
    /// Tier classification for the welcome-card sample picker. Maps onto
    /// the three column lists in the XAML (<see cref="WelcomeStarterList"/>,
    /// <see cref="WelcomeIntermediateList"/>, <see cref="WelcomeAdvancedList"/>).
    /// </summary>
    private enum WelcomeSampleTier { Starter, Intermediate, Advanced }

    /// <summary>
    /// Seed sample row — a tier classification plus the `.phxg` filename
    /// (resolved against <c>Paths.HubLogic/examples</c> at click time) plus
    /// a one-line description for the sub-text under the row title.
    /// </summary>
    private sealed record WelcomeSampleSeed(WelcomeSampleTier Tier, string FileName, string Description);

    /// <summary>
    /// Static registry of bundled sample graphs. Source of truth for the
    /// welcome-card sample picker. Order in each tier is the surface order
    /// in the column. Add / remove entries here when seed files change in
    /// <c>Phoenix.Controls.Hub/data/logic/examples/</c>.
    /// </summary>
    private static readonly WelcomeSampleSeed[] WelcomeSamples = new[]
    {
        // Starter — single-trigger Hello-World-style command.
        new WelcomeSampleSeed(WelcomeSampleTier.Starter,      "01_starter_command.phxg",       "Chat command that echoes a reply"),

        // Intermediate — multi-node flow with one Twitch event + side effects.
        new WelcomeSampleSeed(WelcomeSampleTier.Intermediate, "02_raid_alert.phxg",            "Raid event → on-screen alert"),
        new WelcomeSampleSeed(WelcomeSampleTier.Intermediate, "03_ai_assistant.phxg",          "Chat command routed through AI.ChatComplete"),
        new WelcomeSampleSeed(WelcomeSampleTier.Intermediate, "04_scheduled_reminder.phxg",    "Cron schedule posts a recurring reminder"),

        // Advanced — branching logic, databank reads, webhooks.
        new WelcomeSampleSeed(WelcomeSampleTier.Advanced,     "05_channel_point_redeem.phxg",  "Channel-point redeem with databank loyalty tally"),
        new WelcomeSampleSeed(WelcomeSampleTier.Advanced,     "06_subscription_alert.phxg",    "Sub / resub / gift-sub branching alerts"),
        new WelcomeSampleSeed(WelcomeSampleTier.Advanced,     "07_webhook_handler.phxg",       "Webhook payload → parse JSON → route by field"),
    };

    /// <summary>
    /// Row view-model for the welcome-card sample lists. Mirrors
    /// <c>RecentFilesDialog.Row</c>: an <see cref="Opacity"/> property feeds
    /// the row's XAML binding so missing files render at 0.45 opacity — the
    /// same "grayed-stale" pattern Majo signed off on for the Recent dialog.
    /// </summary>
    private sealed record WelcomeSampleRow(string DisplayName, string Description, string FullPath, bool IsMissing)
    {
        /// <summary>0.45 for stale entries (file missing on disk), 1.0 otherwise.</summary>
        public double Opacity => IsMissing ? 0.45 : 1.0;

        /// <summary>Tooltip text — full path, with a "(missing)" suffix when stale.</summary>
        public string ToolTip => IsMissing ? $"{FullPath}  (missing)" : FullPath;
    }

    /// <summary>
    /// Convert a seed's filename (e.g. <c>02_raid_alert.phxg</c>) into a
    /// human-readable title (<c>Raid alert</c>): strip the numeric prefix,
    /// drop the extension, replace underscores with spaces, and capitalise
    /// the first letter only. Pre-T15 WelcomeDialog used the same display
    /// transform so the picker reads as prose rather than as filenames.
    /// </summary>
    private static string PrettifySampleName(string fileName)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(fileName);
        // Strip leading "NN_" prefix when present.
        int us = name.IndexOf('_');
        if (us > 0 && us < name.Length - 1 && int.TryParse(name.AsSpan(0, us), out _))
        {
            name = name.Substring(us + 1);
        }
        name = name.Replace('_', ' ').Trim();
        if (name.Length == 0) return fileName;
        return char.ToUpperInvariant(name[0]) + name.Substring(1);
    }

    // Re-entrancy guard +
    // signature cache for the welcome-card samples picker.
    //
    // The crash: setting WelcomeStarterList.ItemsSource (line 470 in the
    // pre-fix code) was throwing
    //   E_FAIL "Cannot complete a collection modification while another
    //   modification is in progress"
    // — a known WinUI 3 ItemsControl quirk where assigning ItemsSource while
    // an ancestor layout / measure pass is mid-flight tears the collection
    // bookkeeping. UpdateEmptyHint runs `WelcomeCard.Visibility = Visible`
    // and *then* calls PopulateWelcomeSamples synchronously — the
    // visibility flip starts a measure pass, the ListView's first
    // realization is mid-pass, and the ItemsSource write lands in the
    // middle of the layout's internal collection-change bookkeeping.
    //
    // Two defenses:
    //   (a) _isPopulatingWelcomeSamples re-entrancy guard so a back-fire
    //       (a binding update from setting ItemsSource fires a property
    //       changed that ends up calling UpdateEmptyHint → us again)
    //       can't recurse the assignment.
    //   (b) _lastWelcomeSamplesSignature short-circuits when the
    //       resolved (filename, missing) set hasn't actually changed —
    //       the typical UpdateEmptyHint cascade ends up calling us with
    //       identical data several times in a row (Loaded + DataContext +
    //       LoadedFilePath PropChanged + Nodes Reset all fire close
    //       together on first surface). Re-assigning ItemsSource with the
    //       same logical content is what races; gating on the signature
    //       turns the redundant calls into no-ops without breaking the
    //       "between-session file removal reflects on next surface"
    //       guarantee (the signature includes the missing-flag).
    private bool _isPopulatingWelcomeSamples;
    private string? _lastWelcomeSamplesSignature;

    /// <summary>
    /// Populate / refresh the three welcome-card sample lists from the
    /// static <see cref="WelcomeSamples"/> registry, resolving each seed
    /// against <c>Paths.HubLogic/examples</c> and graying rows whose target
    /// `.phxg` is missing on disk. Cheap to call repeatedly (≤10 stat calls
    /// today); invoked from <see cref="UpdateEmptyHint"/> when the welcome
    /// card becomes visible so a between-session file-removal reflects on
    /// next surface.
    /// </summary>
    private void PopulateWelcomeSamples()
    {
        if (_isPopulatingWelcomeSamples) return;
        _isPopulatingWelcomeSamples = true;
        try
        {
            string examplesDir = System.IO.Path.Combine(
                Phoenix.Controls.Shared.Core.Paths.HubLogic, "examples");

            var starter      = new List<WelcomeSampleRow>();
            var intermediate = new List<WelcomeSampleRow>();
            var advanced     = new List<WelcomeSampleRow>();
            var sig          = new System.Text.StringBuilder();

            foreach (var seed in WelcomeSamples)
            {
                string fullPath = System.IO.Path.Combine(examplesDir, seed.FileName);
                bool   missing  = !System.IO.File.Exists(fullPath);
                var    row      = new WelcomeSampleRow(
                    DisplayName: PrettifySampleName(seed.FileName),
                    Description: seed.Description,
                    FullPath:    fullPath,
                    IsMissing:   missing);

                switch (seed.Tier)
                {
                    case WelcomeSampleTier.Starter:      starter.Add(row);      break;
                    case WelcomeSampleTier.Intermediate: intermediate.Add(row); break;
                    case WelcomeSampleTier.Advanced:     advanced.Add(row);     break;
                }

                sig.Append(seed.Tier).Append('|')
                   .Append(seed.FileName).Append('|')
                   .Append(missing ? '0' : '1').Append(';');
            }

            string signature = sig.ToString();
            if (_lastWelcomeSamplesSignature == signature)
            {
                // Identical content as the last successful population — the
                // cascade of UpdateEmptyHint triggers (Loaded + DataContext +
                // LoadedFilePath + Nodes Reset) all resolve to the same
                // (filename, missing) set, so re-assigning ItemsSource here
                // would just race the ItemsControl bookkeeping with no
                // user-visible effect.
                return;
            }

            // Defer the actual ItemsSource swap to the next dispatcher tick.
            // WinUI 3 throws E_FAIL "Cannot complete a collection
            // modification while another modification is in progress" when
            // ItemsSource is assigned mid-layout-pass — and PopulateWelcomeSamples
            // is reachable from UpdateEmptyHint which itself runs from
            // measure-pass-adjacent callbacks (DataContextChanged, OnLoaded,
            // CollectionChanged). The DispatcherQueue hop guarantees the
            // assignment lands AFTER the in-flight pass completes.
            var starterRef      = starter;
            var intermediateRef = intermediate;
            var advancedRef     = advanced;
            var signatureRef    = signature;
            void Apply()
            {
                try
                {
                    WelcomeStarterList.ItemsSource      = starterRef;
                    WelcomeIntermediateList.ItemsSource = intermediateRef;
                    WelcomeAdvancedList.ItemsSource     = advancedRef;
                    _lastWelcomeSamplesSignature        = signatureRef;
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error("Architect.LogicCanvasView",
                        "PopulateWelcomeSamples deferred apply", ex);
                }
            }

            var dq = DispatcherQueue;
            if (dq is null || dq.HasThreadAccess)
            {
                // Out-of-tree / detached case — fall back to synchronous
                // assignment. This path doesn't race the layout pass
                // because the canvas isn't visible.
                if (dq is null) Apply();
                else dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, Apply);
            }
            else
            {
                dq.TryEnqueue(Apply);
            }
        }
        catch (Exception ex)
        {
            // Never let the welcome card brick on a path-resolution glitch
            // — the buttons above still work, and the user can open files
            // by hand. Logging the fault is enough.
            GlobalLogger.Error("Architect.LogicCanvasView", "PopulateWelcomeSamples", ex);
        }
        finally
        {
            _isPopulatingWelcomeSamples = false;
        }
    }

    /// <summary>
    /// Click handler shared by all three sample lists. Resolves the clicked
    /// row to its absolute path, swallows the click silently if the seed
    /// file went missing between population and click (grayed entry, edge
    /// case), and otherwise raises the existing
    /// <see cref="FileOpenRequested"/> event so MainView's sibling-spawn
    /// logic opens the sample like any Explorer drop.
    /// </summary>
    private async void OnWelcomeSampleItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not WelcomeSampleRow row) return;
        if (row.IsMissing)
        {
            // Grayed row — log + ignore. Repeatable rejection: do NOT
            // surface a ContentDialog telling the user the file is gone;
            // the dimmed row + tooltip already communicate it.
            GlobalLogger.Log(
                $"Welcome sample '{row.DisplayName}' is missing on disk — '{row.FullPath}'",
                "Architect", Phoenix.Controls.Shared.Models.LogLevel.System);
            return;
        }

        // Copy-to-editable flow. Pre-fix the click raised FileOpenRequested
        // straight at the read-only examples/ source, so editing + saving a
        // sample either failed silently or clobbered the shipped example. Now
        // the sample is copied into an editable samples/ directory first, with a
        // ContentDialog overwrite prompt (a deliberate, infrequent decision — not
        // a repeatable guardrail rejection — so a modal is appropriate here), and
        // FileOpenRequested fires on the COPIED destination. Copy failures are
        // surfaced via GlobalLogger (the canonical non-modal channel) and abort
        // the open rather than half-opening a non-existent file.
        try
        {
            if (!System.IO.File.Exists(row.FullPath))
            {
                GlobalLogger.Log(
                    $"Welcome sample '{row.DisplayName}' vanished before it could be opened — '{row.FullPath}'",
                    "Architect", Phoenix.Controls.Shared.Models.LogLevel.System);
                return;
            }

            string fileName = System.IO.Path.GetFileName(row.FullPath);
            string samplesDir = System.IO.Path.Combine(
                Phoenix.Controls.Shared.Core.Paths.HubLogic, "samples");
            string destPath = System.IO.Path.Combine(samplesDir, fileName);

            // If the editable copy already exists, ask before overwriting. The
            // user may have made local edits to a prior copy of this sample.
            if (System.IO.File.Exists(destPath))
            {
                bool overwrite = await PromptSampleOverwriteAsync(row.DisplayName, fileName);
                if (!overwrite)
                {
                    // Keep the existing editable copy — just open it.
                    FileOpenRequested?.Invoke(this, destPath);
                    return;
                }
            }

            try
            {
                System.IO.Directory.CreateDirectory(samplesDir);
                System.IO.File.Copy(row.FullPath, destPath, overwrite: true);
            }
            catch (Exception copyEx)
            {
                GlobalLogger.Error("Architect.LogicCanvasView",
                    $"Failed to copy welcome sample '{row.DisplayName}' to '{destPath}'", copyEx);
                return;
            }

            // Route the COPIED, editable file through the existing Explorer-drop
            // seam — MainView / ArchitectSiblingWindow handles the sibling-window
            // spawn semantics for the multi-window paradigm.
            FileOpenRequested?.Invoke(this, destPath);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Architect.LogicCanvasView",
                $"OnWelcomeSampleItemClick '{row.FullPath}'", ex);
        }
    }

    /// <summary>
    /// Ask the user whether to overwrite an existing editable copy of a
    /// welcome sample. Returns true to overwrite, false to keep the existing
    /// copy. A bare modal here is acceptable (a deliberate, infrequent decision,
    /// not a repeatable rejection); if no XamlRoot is available (canvas not yet
    /// in the visual tree) the safe default is "don't overwrite".
    /// </summary>
    private async System.Threading.Tasks.Task<bool> PromptSampleOverwriteAsync(string displayName, string fileName)
    {
        if (XamlRoot is null) return false;
        try
        {
            var dialog = new ContentDialog
            {
                Title             = "Sample already copied",
                Content           = $"An editable copy of \"{displayName}\" already exists as \"{fileName}\". "
                                  + "Overwrite it with the original sample, or open your existing copy?",
                PrimaryButtonText = "Overwrite",
                CloseButtonText   = "Open existing copy",
                DefaultButton     = ContentDialogButton.Close,
                XamlRoot          = XamlRoot,
            };
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Architect.LogicCanvasView",
                $"PromptSampleOverwriteAsync '{fileName}'", ex);
            return false;
        }
    }

    // 0.10.0 — single CompositionTarget.Rendering subscription
    // for the frame-coalesced pan + wire-recompute pipeline. Subscribed on
    // Loaded; disposed on Unloaded so a recycled canvas doesn't leak the
    // handler. Tracked here so the unhook is symmetrical with the hook.
    private bool _renderingHooked;
    // Pan coalescer state — pointer-pan handlers stash their target PanX/Y
    // here and flag dirty; the Rendering tick applies + clears.
    private bool _panDirty;
    private double _pendingPanX;
    private double _pendingPanY;

    // Wheel coalescer state — wheel handler accumulates pan deltas
    // and zoom intent here; the rendering tick resolves the cursor-anchored
    // zoom against the LIVE _vm.Zoom/PanX/PanY at apply time so a 120 Hz
    // wheel burst collapses into one transform write per displayed frame.
    // Accumulating the wheel delta (rather than overwriting like _pendingPanX)
    // is what lets a multi-detent burst still produce the full pan/zoom range
    // — only the application gets coalesced, not the intent.
    private bool _wheelPanDirty;
    private double _pendingWheelPanDx;
    private double _pendingWheelPanDy;
    private bool _wheelZoomDirty;
    private double _pendingWheelZoomDelta;       // accumulated MouseWheelDelta units
    private double _pendingWheelZoomStepBase;    // 1.1 normal / 1.025 Shift-fine
    private Point _pendingWheelZoomCursor;       // cursor anchor (host-space)

    // 0.11.x lag-fix — frame overlap-tint coalescer. Pre-fix every
    // FrameViewModel.X/Y/Width/Height/Color PropertyChanged AND every
    // AddFrameView / RemoveFrameView ran RefreshFrameOverlapTints()
    // synchronously. The refresh is O(F²) (each frame tested against every
    // other frame), and during node/frame drags it fired at 120 Hz × M
    // dragged frames × 2 PropertyChanged events per move. Multi-frame
    // graphs also paid O(F²) on every reroute insert / undo restore
    // (LoadGraph fans out an Add per frame, each calling refresh — N×O(F²)
    // = O(F³)). Now the mutation paths flip _frameOverlapTintsDirty and
    // OnRenderingTick drains it once per displayed frame. Idempotent
    // dirty-sets collapse bulk loads to a single O(F²) pass on the next
    // tick. Idle canvas pays one bool check per tick.
    private bool _frameOverlapTintsDirty;

    // Per-tick synchronous budget (ms) for the wire-recompute
    // drain. A clean link is a bool no-op, but a large dirty backlog (a relayout
    // wave that dirtied many wires, or a cold text-measure storm on first
    // realization) could run for seconds in one tick and trip the UI-hang
    // watchdog — the freeze symptom captured in the rolling log. When the budget
    // is spent, the remaining dirty links stay flagged and drain on the next
    // tick(s): a pathological backlog costs a few extra frames of wire-lag
    // instead of an 8s freeze. 6 ms leaves the rest of a 60 Hz frame (~16 ms)
    // for layout + render; a normal drag (a few hundred incident links,
    // microseconds each) never approaches it, so steady-state is unchanged.
    private const double WireDrainBudgetMs = 6.0;
    // Throttle the spill diagnostic so a sustained backlog can't spam the log.
    private long _lastWireDrainSpillLogTicks;

    /// <summary>
    /// 0.10.0 — pointer-move handlers call this to defer
    /// applying a freshly-computed pan target to the next render frame.
    /// Calling repeatedly within a frame just overwrites the target — the
    /// last pre-tick value wins, which is what the user actually sees on
    /// screen anyway.
    /// </summary>
    internal void QueuePan(double targetX, double targetY)
    {
        _pendingPanX = targetX;
        _pendingPanY = targetY;
        _panDirty = true;
    }

    /// <summary>
    /// Queue an incremental wheel-pan delta (dx, dy in pixels) for
    /// the next CompositionTarget.Rendering tick. Multiple calls within the
    /// same frame accumulate so a smooth-scroll burst still sums to the
    /// expected total pan; the rendering tick applies the accumulated delta
    /// + clears. Mirrors the drag-pan coalescer pattern.
    /// </summary>
    internal void QueueWheelPanDelta(double dx, double dy)
    {
        _pendingWheelPanDx += dx;
        _pendingWheelPanDy += dy;
        _wheelPanDirty = true;
    }

    /// <summary>
    /// Queue a wheel-zoom step for the next rendering tick. The
    /// cursor anchor is the wheel-event position so the canvas point under
    /// the cursor stays put through the zoom. Accumulated delta lets a fast
    /// 5-detent burst still produce the full zoom envelope; the rendering
    /// tick resolves the final factor against the LIVE _vm.Zoom so the
    /// transform matches what the user sees on screen.
    /// </summary>
    internal void QueueWheelZoom(int delta, double stepBase, Point cursorHostPoint)
    {
        // If a prior queued zoom used a different step (mid-burst Shift
        // press/release), apply its stepBase via the accumulator — the user
        // perceives the modifier flip as "from this moment on use the fine
        // step". Cleanest is to apply the accumulated delta NOW under the
        // old step base and start fresh with the new one. Cheap and correct.
        if (_wheelZoomDirty
            && Math.Abs(stepBase - _pendingWheelZoomStepBase) > 1e-9
            && _pendingWheelZoomDelta != 0)
        {
            FlushPendingWheelZoom();
        }

        _pendingWheelZoomDelta   += delta;
        _pendingWheelZoomStepBase = stepBase;
        _pendingWheelZoomCursor   = cursorHostPoint;
        _wheelZoomDirty           = true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Make the canvas focusable so KeyDown fires when it has logical focus.
        IsTabStop = true;
        Focus(FocusState.Programmatic);
        HostRoot.PointerPressed       += OnHostPointerPressed;
        HostRoot.PointerMoved         += OnHostPointerMoved;
        HostRoot.PointerReleased      += OnHostPointerReleased;
        HostRoot.PointerCanceled      += OnHostPointerCancelled;
        HostRoot.PointerCaptureLost   += OnHostPointerCancelled;
        HostRoot.PointerWheelChanged  += OnHostPointerWheelChanged;
        // Pointer-over-host tracking so keyboard Ctrl+0/+/- can
        // anchor on the cursor (matching wheel-zoom math at :520-524) when
        // the pointer is over the canvas, and fall back to viewport centre
        // when it isn't. Handlers live in LogicCanvasView.Keyboard.cs.
        HostRoot.PointerEntered       += OnHostPointerEntered;
        HostRoot.PointerExited        += OnHostPointerExited;
        // KeyDown/KeyUp wire on the UserControl, not HostRoot. KeyDown is a
        // routed event that bubbles UP from the focused element — and the
        // canvas's `Focus(FocusState.Programmatic)` call lands on this
        // UserControl (because IsTabStop=true on it), not on the HostRoot
        // child. With the old wiring on HostRoot, every DEL/Esc/Ctrl+S press
        // fired on the UserControl, never reached the child handler, and
        // looked like the keyboard was dead. Hooking on `this` catches the
        // event regardless of which descendant has focus — TextBox edits
        // still keep DEL local because the textbox marks it Handled.
        KeyDown                       += OnHostKeyDown;
        KeyUp                         += OnHostKeyUp;
        // Join the process-wide Event-pair live-sync fan-out so a paired Event
        // node edited in ANOTHER open window updates this canvas in-memory
        // (cross-file bubble sync while both files are open — the disk sync
        // alone only refreshes CLOSED peers).
        Phoenix.Controls.Architect.WinUI.Services.EventPairLiveSync.Register(this);
        // Suppress the framework's auto bring-into-view when
        // an inline pill editor grows (value wraps onto a new line) — otherwise
        // a host ScrollViewer up the tree yanks/zooms the viewport toward the
        // pill. See OnBringIntoViewRequested. Bubbles up from the editor TextBox
        // through this UserControl before reaching any ancestor scroller.
        BringIntoViewRequested        += OnBringIntoViewRequested;
        HostRoot.LostFocus            += OnHostLostFocus;
        HostRoot.RightTapped          += OnHostRightTapped;
        // 0.10.0 — double-tap on a Macro.Call /
        // Process.Spawn node opens the matching SubGraphWindow editor.
        // Pre-0.10.0 the only way in was the right-click menu's
        // "Edit macro graph" / "Edit process graph" item; doubling-up the
        // affordance with double-tap matches UE Blueprints / DaVinci
        // muscle memory. Handled on HostRoot so frame / node body /
        // socket double-taps all bubble through the same hit-test path.
        HostRoot.DoubleTapped         += OnHostDoubleTapped;
        HookDragDrop();

        // Endless dot grid — first build, then keep it covering the viewport as
        // the canvas resizes (initial layout, splitter drags, window resize).
        SizeChanged += OnCanvasSizeChangedForGrid;
        RefreshDotGrid();
        UpdateGridVisibility();
        UpdateEmptyHint();

        // 0.10.0 — single CompositionTarget.Rendering
        // subscription drives the frame-coalesced pan + wire-recompute
        // pipeline. Idempotent so a re-Loaded canvas (tab swap) doesn't
        // double-subscribe; OnUnloaded tears it down.
        if (!_renderingHooked)
        {
            CompositionTarget.Rendering += OnRenderingTick;
            _renderingHooked = true;
        }

        // The GPU canvas is the default. Activate it
        // now (collapse the retained paint layers, hook the Win2D draw); auto-falls
        // back to the retained path if Win2D can't initialise on this machine/build.
        ActivateImmediateModeDefault();
    }

    // When the user types into an inline pill editor and the
    // value wraps to a new line, the TextBox grows and WinUI raises
    // BringIntoViewRequested to pull the now-taller focused control fully into
    // view. With the canvas hosted inside a ScrollViewer up the tree (Hub
    // shell), that auto-scroll yanks/zooms the viewport toward the pill — Majo
    // flagged it as the camera "artificially zooming into the text field". The
    // canvas owns its own pan/zoom via the ViewTransform and never wants the
    // framework to move the viewport on focus/resize, so swallow requests that
    // originate inside a node. (Programmatic reveal-node framing drives the
    // ViewTransform directly, not BringIntoView, so it is unaffected.)
    private void OnBringIntoViewRequested(UIElement sender, BringIntoViewRequestedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && IsInsideNodeView(src))
            e.Handled = true;
    }

    private static bool IsInsideNodeView(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is NodeView) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Close any open GPU-canvas edit session BEFORE the detach settles.
        // A canvas unloaded mid-edit (tab-swap / window switch) never routes
        // through the pointer-path exit, so _editNode survived the detach and
        // the mounted edit NodeView ran its FULL Unloaded teardown
        // (_isCulling was false while editing) — on re-load the stale state
        // handed EnterImmediateEdit a dead, torn-down view. Exiting here sets
        // _isCulling before the removal so the view survives for a clean
        // remount, and the renderer resumes drawing the node.
        ExitImmediateEdit();

        if (_renderingHooked)
        {
            CompositionTarget.Rendering -= OnRenderingTick;
            _renderingHooked = false;
        }

        // Symmetric teardown for every HostRoot /
        // UserControl handler subscribed in OnLoaded. Pre-fix these were never
        // removed, so a recycled canvas (SubGraphWindow / tab-swap / 0.10.0
        // sibling window) leaked the handlers and, through them, this canvas.
        // Mirrors the OnLoaded subscription block 1:1.
        try
        {
            HostRoot.PointerPressed       -= OnHostPointerPressed;
            HostRoot.PointerMoved         -= OnHostPointerMoved;
            HostRoot.PointerReleased      -= OnHostPointerReleased;
            HostRoot.PointerCanceled      -= OnHostPointerCancelled;
            HostRoot.PointerCaptureLost   -= OnHostPointerCancelled;
            HostRoot.PointerWheelChanged  -= OnHostPointerWheelChanged;
            HostRoot.PointerEntered        -= OnHostPointerEntered;
            HostRoot.PointerExited         -= OnHostPointerExited;
            KeyDown                       -= OnHostKeyDown;
            KeyUp                         -= OnHostKeyUp;
            Phoenix.Controls.Architect.WinUI.Services.EventPairLiveSync.Unregister(this);
            BringIntoViewRequested        -= OnBringIntoViewRequested;
            HostRoot.LostFocus            -= OnHostLostFocus;
            HostRoot.RightTapped          -= OnHostRightTapped;
            HostRoot.DoubleTapped         -= OnHostDoubleTapped;
            SizeChanged                   -= OnCanvasSizeChangedForGrid;
        }
        catch { /* shutdown best-effort */ }

        // Drag-drop handlers (HostRoot.DragOver /
        // Drop) were hooked in OnLoaded via HookDragDrop with no unhook —
        // release them here.
        UnhookDragDrop();

        // Edge-pan + drop-status-banner
        // DispatcherTimers — stop, unsubscribe their Tick, and null them so a
        // recycled canvas doesn't leak the timer + its subscription.
        StopEdgePanTimer();
        UnhookDropStatusDismissTimer();

        // Drop the cross-file sync debounce timer on tear-down so
        // a SubGraphWindow / tab-swap doesn't leak the DispatcherTimer +
        // its Tick subscription. If a Schedule() call is still in flight,
        // the user closing the canvas is consent enough to skip the sync —
        // the next open will pick up whatever the disk has.
        if (_crossFileSyncTimer is not null)
        {
            try { _crossFileSyncTimer.Stop(); _crossFileSyncTimer.Tick -= OnCrossFileSyncTimerTick; }
            catch { /* shutdown best-effort */ }
            _crossFileSyncTimer = null;
        }

        // Drop the shared flash auto-clear sweep timer on tear-down so a
        // SubGraphWindow / tab-swap doesn't leak the DispatcherTimer + its Tick
        // subscription. Any in-flight flash expiry is abandoned — the canvas is
        // going away, so clearing IsExecutingFlash is moot.
        if (_flashSweepTimer is not null)
        {
            try { _flashSweepTimer.Stop(); _flashSweepTimer.Tick -= OnFlashSweepTick; }
            catch { /* shutdown best-effort */ }
            _flashSweepTimer = null;
        }
        _flashExpiries.Clear();

        // Also drop the marquee overlay — kept parented into
        // OverlayLayer.Children across the canvas's lifetime, would leak
        // per SubGraphWindow once 0.10.0 multi-window restored.
        DetachMarqueeOverlayOnUnload();
    }

    /// <summary>
    /// 0.10.0 — the per-frame work both for canvas pan and
    /// for wire-bezier recompute. Pointer-move handlers queue intent (pan
    /// target via <see cref="QueuePan"/>; per-link dirty flags via
    /// <see cref="LinkViewModel.MarkPathDirty"/>) and this tick applies the
    /// queued work exactly once per displayed frame. Cheap when nothing
    /// changed (one bool check + an O(L) walk of clean dirty flags) so the
    /// permanent subscription doesn't waste cycles on an idle canvas.
    /// </summary>
    private void OnRenderingTick(object? sender, object e)
    {
        if (_vm is null) return;

        // When the immediate-mode GPU canvas is active, request a repaint —
        // but only while something actually changed. MarkSceneDirty sources
        // (view transform, node/frame/link property changes, graph mutation /
        // load, selection, theme, resize, flash) keep the grace window live;
        // the union below adds the in-flight gesture state and the coalescer
        // flags whose apply runs LATER in this same tick, so their first
        // frame paints without a one-tick lag. A fully idle canvas skips the
        // whole Draw pass instead of re-rendering at frame cadence. Placed
        // before the pan fast-path return so the GPU canvas keeps repainting
        // mid-pan. _cullDirty is deliberately NOT in the union — the retained
        // cull never drains it in immediate mode, so it would pin the scene
        // permanently dirty.
        if (_drag != DragState.Idle
            || _panDirty || _wheelPanDirty || _wheelZoomDirty
            || _snapGuidesDirty || _frameContentDragDirty || _groupDragDirty
            || _frameOverlapTintsDirty || _marqueeApplyDirty || _wireDropHitTestDirty
            || _vm.AnyLinkDirty || _pendingFlashNodeIds.Count > 0)
            MarkSceneDirty();
        if (ConsumeSceneDirtyFrame())
            InvalidateImmediate();

        // 0.11.x polish — trace the per-frame drain. The render tick runs at
        // displayed-frame cadence on the UI thread and drains several
        // potentially-heavy paths (snap guides, wire recompute, marquee
        // apply, wire-drop hit-test). When the UI freezes mid-edit the
        // watchdog reads the last-traced activity; an "Architect.RenderTick"
        // entry there narrows the suspect to one of the drains below.
        using var _trace = Phoenix.Controls.Shared.Services.UiActivityTrace
            .Begin("Architect.RenderTick");

        // Drain coalesced debug-trace flashes before the
        // pan fast-path return so execution pulses still appear while panning.
        // Cheap when nothing is pending (one count check).
        DrainPendingFlashes();

        // Apply wheel-zoom first so cursor-anchored zoom resolves
        // against pre-pan canvas coordinates, then bake the wheel-pan delta
        // on top. Drag-pan (_panDirty) wins last because it's a hard target,
        // not a delta — a user actively click-pan-dragging while the wheel
        // is mid-burst should see the click-drag pose.
        if (_wheelZoomDirty) FlushPendingWheelZoom();
        if (_wheelPanDirty)
        {
            _wheelPanDirty = false;
            double dx = _pendingWheelPanDx;
            double dy = _pendingWheelPanDy;
            _pendingWheelPanDx = 0;
            _pendingWheelPanDy = 0;
            _vm.PanX += dx;
            _vm.PanY += dy;
            ApplyViewTransform();
        }

        if (_panDirty)
        {
            _panDirty = false;
            _vm.PanX = _pendingPanX;
            _vm.PanY = _pendingPanY;
            ApplyViewTransform();
        }

        // Fast path while a click-drag PAN gesture is
        // active. Panning only writes the canvas RenderTransform; it never
        // changes node / frame / wire geometry in canvas-space, so NONE of the
        // per-frame drains below are relevant during a pan. Skipping them keeps
        // a pan tick to just the transform write.
        //
        // Pre-fix, a pan that started while snap-guides / dirty wires / frame-
        // overlap tints / frame-content drags were still pending dragged the
        // full O(N²)/O(F²)/O(links) drain set into EVERY displayed frame of the
        // pan — on a large graph that blew the frame budget for the whole
        // gesture, which is the "panning around still leads to freeze / no-ops"
        // class Majo reported (the watchdog caught 'Architect.RenderTick' mid-
        // stall). Any flag left dirty here is drained on the first non-pan tick
        // once the gesture ends, so nothing is lost — only deferred past the pan.
        if (_drag == DragState.Pan) return;

        // Rebuild the dot grid on pan/zoom SETTLE, not
        // mid-pan. ApplyViewTransform only marks it dirty; the expensive O(dots)
        // build runs here on the first non-pan tick. Placed BEFORE the idle-skip
        // gate so a settle frame (otherwise "idle") still rebuilds the grid.
        // Cheap (one bool check) when not dirty.
        FlushDotGridRebuild();

        // Idle-skip — when no drain below has pending work, the
        // entire per-frame drain (including the O(L) wire walk) is wasted motion
        // on the shared UI thread. Skip it. Every flag here is set by an event
        // handler and read on the next tick (WinUI never splits a frame), so
        // nothing is lost — the work is only collapsed away when there is none.
        // DrainPendingFlashes already ran above the pan fast-path, so debug-trace
        // pulses still animate while idle. _vm.AnyLinkDirty is the canvas-level
        // aggregator (set at the two wire-dirty chokepoints, cleared after a full
        // wire drain below) so this gate skips the O(L) walk without scanning it.
        if (_drag == DragState.Idle
            && !_snapGuidesDirty && !_frameContentDragDirty && !_frameOverlapTintsDirty
            && !_marqueeApplyDirty && !_wireDropHitTestDirty
            && !_vm.AnyLinkDirty && !_cullDirty)   // _cullDirty is real work — don't idle past a pending cull
            return;

        // Drain any pending marquee apply at frame cadence. Cheap
        // when no marquee is active (one bool check) — the marquee partial
        // owns the dirty flag + cursor cache.
        DrainMarqueeApply();

        // Flush coalesced snap-guide
        // rebuild. PointerMoved sets _snapGuidesDirty rather than rebuilding
        // inline so a high-Hz pointer drag doesn't tear down + recreate the
        // overlay Shapes at 120 Hz. Read-and-clear so a quiet frame is one
        // bool check. Breadcrumb scope is entered only when there's work, so
        // an idle frame pays nothing extra.
        if (_snapGuidesDirty)
        {
            _snapGuidesDirty = false;
            using (Phoenix.Controls.Shared.Services.UiActivityTrace.Begin("Architect.RenderTick.SnapGuides"))
                UpdateSnapGuidesForDrag();
        }

        // 0.11.x polish — flush coalesced wire-drop hit-test. PointerMoved
        // on a WireDrop drag flips the dirty flag instead of running
        // FindElementsInHostCoordinates inline; this drain resolves the
        // hover socket + preview brush at displayed-frame cadence. Cheap
        // when no wire drag is active (one bool check inside).
        DrainWireDropHitTest();

        // Flush coalesced frame-content drag. PointerMoved
        // on a FrameMove now sets the dirty flag instead of calling
        // UpdateFrameContentDrag(N nodes) inline at pointer-burst cadence;
        // this drain runs the N-node translate pass at most once per
        // displayed frame. Cheap when no frame drag is active (one bool
        // check). Runs BEFORE the wire-drain loop so the contained-node
        // PropertyChanged dispatches have already marked link paths dirty
        // by the time we reach RecomputeIfDirty.
        if (_frameContentDragDirty)
        {
            using (Phoenix.Controls.Shared.Services.UiActivityTrace.Begin("Architect.RenderTick.FrameContentDrag"))
                FlushFrameContentDragIfDirty();
        }

        // Drain the coalesced group-drag (multi-node selection
        // move). Runs before the wire-recompute loop below so the moved nodes'
        // X/Y PropertyChanged have marked their incident links dirty by the time
        // RecomputeIfDirty walks them. Cheap when no group drag is in flight.
        if (_groupDragDirty)
        {
            using (Phoenix.Controls.Shared.Services.UiActivityTrace.Begin("Architect.RenderTick.GroupDrag"))
                FlushGroupDragIfDirty();
        }

        // 0.11.x lag-fix — drain coalesced frame-overlap-tint refresh.
        // RefreshFrameOverlapTints is O(F²) and used to fire from every
        // FrameViewModel.X/Y/Width/Height/Color PropertyChanged AND every
        // AddFrameView; under a multi-frame group drag at 120 Hz that
        // meant 100s of intersect tests per second on the UI thread. Now
        // the mutation paths flip the dirty flag and this runs once per
        // displayed frame. Idle canvas pays one bool check.
        if (_frameOverlapTintsDirty)
        {
            using (Phoenix.Controls.Shared.Services.UiActivityTrace.Begin("Architect.RenderTick.FrameOverlapTint"))
                FlushFrameOverlapTintsIfDirty();
        }

        // Drain dirty wires. Walks every link but each clean link is a
        // cheap bool-check; for a typical drag this is bounded by the
        // number of links touching the moved nodes. Wrapped so a stall here
        // (a pathological recompute on a huge link set) names itself in the
        // watchdog log instead of hiding under the generic 'RenderTick'.
        // Time-sliced: caps the synchronous cost per tick at
        // WireDrainBudgetMs so a large dirty backlog can never hold the UI
        // thread long enough to trip the watchdog. Links left dirty when the
        // budget is spent keep _pathDirty=true and drain on the next tick(s) —
        // starting the scan from 0 again is fine because already-recomputed
        // links are bool no-ops. Only actual recomputes count toward the budget.
        using (Phoenix.Controls.Shared.Services.UiActivityTrace.Begin("Architect.RenderTick.WireRecompute"))
        {
            // Clear the idle-skip aggregator up front: a full
            // drain below leaves every link clean, so the next idle tick can
            // early-out. A MarkPathDirty landing DURING this drain re-sets it,
            // and a budget spill re-sets it too (below) — so the next tick still
            // runs whenever work remains. This ordering can never drop a recompute.
            _vm.ClearAnyLinkDirty();
            var links = _vm.Links;
            int n = links.Count;
            int recomputed = 0;
            bool budgetSpent = false;
            long startTs = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < n; i++)
            {
                if (!links[i].RecomputeIfDirty()) continue; // clean link — free
                recomputed++;
                // Check the clock only after real work, and only every 16th
                // recompute, to keep Stopwatch overhead off the hot path.
                if ((recomputed & 15) == 0
                    && System.Diagnostics.Stopwatch.GetElapsedTime(startTs).TotalMilliseconds >= WireDrainBudgetMs)
                {
                    budgetSpent = true;
                    break; // remaining dirty links drain on subsequent ticks
                }
            }
            if (budgetSpent) { LogWireDrainSpill(recomputed); _vm.SetAnyLinkDirty(); }
        }

        // Node cull at the tail of the tick (after wire-recompute, so a
        // remounted node's position + its links are already applied). Runs only on a
        // SETTLE tick (_drag == Idle) when virtualization marked it dirty — never
        // mid-gesture. _cullDirty is in the idle-skip gate above so a settle frame
        // reaches here. No-op when the flag is OFF.
        //
        // SETTLE DEBOUNCE: a wheel-zoom keeps _drag == Idle and re-sets _cullDirty
        // every tick (ApplyViewTransform), which previously re-culled EVERY frame of
        // the zoom — mounting/unmounting edge nodes per frame = the "zoom is laggy"
        // symptom. ApplyViewTransform now zeroes _cullSettleFrames on each viewport
        // change, so during a continuous zoom/pan the counter never reaches the
        // threshold and the cull is skipped; once the gesture STOPS, the counter
        // climbs and the cull runs ONCE. (Mounted set is briefly stale mid-zoom —
        // the same settle tradeoff the dot grid already makes.)
        if (_enableNodeVirtualization && !_useImmediateMode && _cullDirty && _drag == DragState.Idle)
        {
            if (_cullSettleFrames >= CullSettleThreshold)
            {
                _cullDirty = false;
                _cullSettleFrames = 0;
                RefreshNodeRealization();   // cull + proxy/full swap
            }
            else
            {
                _cullSettleFrames++;
            }
        }
    }

    // Diagnostic for a time-sliced wire-drain spill — a strong
    // signal that a relayout/measure wave produced an outsized dirty-link
    // backlog (the freeze precursor). Throttled to at most once per ~2s so a
    // sustained backlog can't flood the rolling log.
    private void LogWireDrainSpill(int recomputedThisTick)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastWireDrainSpillLogTicks != 0
            && System.Diagnostics.Stopwatch.GetElapsedTime(_lastWireDrainSpillLogTicks).TotalSeconds < 2.0)
            return;
        _lastWireDrainSpillLogTicks = now;
        Phoenix.Controls.Shared.Services.GlobalLogger.Log(
            $"Wire-recompute drain time-sliced at {WireDrainBudgetMs:F0}ms — recomputed {recomputedThisTick} dirty wire(s) this frame, remainder deferred to keep the UI responsive. A repeated burst here points at a relayout/measure storm dirtying wires en masse.",
            "Architect.Canvas", Phoenix.Controls.Shared.Models.LogLevel.System);
    }

    /// <summary>
    /// Drain the queued wheel-zoom delta against the live
    /// _vm.Zoom/PanX/PanY so the cursor-anchored zoom resolves against the
    /// canvas pose the user actually sees on screen. Idempotent — clears the
    /// dirty flag + accumulator on entry.
    /// </summary>
    private void FlushPendingWheelZoom()
    {
        if (_vm is null) return;
        _wheelZoomDirty = false;
        double delta = _pendingWheelZoomDelta;
        double stepBase = _pendingWheelZoomStepBase;
        var cursor = _pendingWheelZoomCursor;
        _pendingWheelZoomDelta = 0;
        if (delta == 0 || stepBase <= 0) return;

        // Floor the divisor. A Zoom of 0 (deserialisation
        // glitch / setter race) makes cx/cy below NaN and poisons PanX/PanY,
        // NaN-ing the entire viewport transform — hit-testing, pan and zoom all
        // break until restart. The keyboard zoom path already floors the same
        // divisor via Math.Max(_vm.Zoom, 0.0001); mirror it here.
        double oldZoom = Math.Max(_vm.Zoom, 0.0001);
        double factor  = Math.Pow(stepBase, delta / 120.0);
        // Match the WinForms canvas zoom envelope [0.2, 4.0] — pre-fix this
        // wheel path clamped to 0.25, tighter than the baseline and the VM's own
        // Zoom-setter clamp, so the wheel couldn't reach the documented minimum.
        double newZoom = Math.Clamp(oldZoom * factor, 0.2, 4.0);
        if (Math.Abs(newZoom - oldZoom) < 1e-6) return;

        double cx = (cursor.X - _vm.PanX) / oldZoom;
        double cy = (cursor.Y - _vm.PanY) / oldZoom;
        _vm.Zoom = newZoom;
        _vm.PanX = cursor.X - cx * newZoom;
        _vm.PanY = cursor.Y - cy * newZoom;
        ApplyViewTransform();
    }

    private const double GridStep = 40.0;

    // ─── Endless dot grid ────────────────────────────────────────────────
    // The background is a field of EllipseGeometry dots at GridStep lattice
    // points, rendered as a single Path in ViewSurface (canvas space). It is
    // ENDLESS: rather than a fixed 8000×8000 mesh (which ran off-screen the
    // moment you panned past it — "the background is mostly out of view"), we
    // build only the dots that cover the current viewport plus a one-viewport
    // margin, and REBUILD only when the view pans outside that built region or
    // the zoom changes enough to matter. Panning WITHIN the built region needs
    // no geometry work at all — ViewSurface's CompositeTransform moves the dots
    // for free, so there is no per-frame churn (honours the UI-thread frame
    // budget).

    private const double DotScreenSpacingMin = 40.0;  // on-screen dot pitch floor (px) → LOD coarsen below this
    private const double DotScreenRadius     = 1.35;  // on-screen dot radius (px), kept ~constant via /zoom
    private const double GridMarginFactor    = 1.0;   // viewports of dots to build beyond the visible rect, each side
    private const int    GridMaxDots         = 14000; // hard cap → coarsen LOD further rather than build a huge field

    // Snapshot of the field currently in GridLayer.Data so RefreshDotGrid can
    // early-out when the live view is still covered by it.
    private double _gridBuiltStep = -1;
    private double _gridBuiltZoom = -1;
    private double _gridBuiltMinX, _gridBuiltMinY, _gridBuiltMaxX, _gridBuiltMaxY;
    // Decouple the O(dots) dot-grid rebuild from the
    // pan critical path. ApplyViewTransform / SizeChanged now only flip
    // _dotGridDirty; the actual rebuild runs from OnRenderingTick on the first
    // non-pan (settle) tick. _dotGridRebuilding guards against re-entrancy.
    private bool _dotGridDirty;
    private bool _dotGridRebuilding;

    /// <summary>
    /// Rebuild the endless dot field when (and only when) the visible canvas
    /// rect has moved outside the currently-built region, the zoom LOD bucket
    /// changed, or the zoom drifted enough that the dots would visibly mis-size.
    /// Cheap to call every frame: the common case is a handful of comparisons
    /// and an early return. Driven from <see cref="ApplyViewTransform"/>, the
    /// canvas <c>SizeChanged</c>, and first <c>Loaded</c>.
    /// </summary>
    private void RefreshDotGrid()
    {
        if (GridLayer is null) return;

        double zoom = Math.Max(_vm?.Zoom ?? 1.0, 0.0001);
        double panX = _vm?.PanX ?? 0.0;
        double panY = _vm?.PanY ?? 0.0;
        double viewW = HostRoot?.ActualWidth  ?? 0.0;
        double viewH = HostRoot?.ActualHeight ?? 0.0;
        // Pre-measure fallback so something paints before the first layout pass;
        // the SizeChanged hook refines it once real dimensions arrive.
        if (viewW <= 0 || viewH <= 0) { viewW = 1920.0; viewH = 1080.0; }

        // Visible rect in canvas space (inverse of pan + zoom).
        double visMinX = (0.0   - panX) / zoom;
        double visMinY = (0.0   - panY) / zoom;
        double visMaxX = (viewW - panX) / zoom;
        double visMaxY = (viewH - panY) / zoom;
        double visW = visMaxX - visMinX;
        double visH = visMaxY - visMinY;

        // LOD: coarsen the canvas-space pitch until dots are at least
        // DotScreenSpacingMin px apart on screen (keeps the field from turning
        // into a solid wash when zoomed out).
        double step = GridStep;
        while (step * zoom < DotScreenSpacingMin) step *= 2.0;

        // Coverage rect = visible rect + margin, snapped to the lattice.
        double marginX = visW * GridMarginFactor;
        double marginY = visH * GridMarginFactor;
        double minX = Math.Floor((visMinX - marginX) / step) * step;
        double minY = Math.Floor((visMinY - marginY) / step) * step;
        double maxX = Math.Ceiling((visMaxX + marginX) / step) * step;
        double maxY = Math.Ceiling((visMaxY + marginY) / step) * step;

        // Hard cap on dot count — coarsen further (e.g. 4K viewport) rather than
        // allocate a pathological field.
        while ((Math.Floor((maxX - minX) / step) + 1) * (Math.Floor((maxY - minY) / step) + 1) > GridMaxDots)
        {
            step *= 2.0;
            minX = Math.Floor((visMinX - marginX) / step) * step;
            minY = Math.Floor((visMinY - marginY) / step) * step;
            maxX = Math.Ceiling((visMaxX + marginX) / step) * step;
            maxY = Math.Ceiling((visMaxY + marginY) / step) * step;
        }

        // Early-out: same LOD, the visible rect is still inside the built field,
        // and the zoom hasn't drifted enough to mis-size the dots (±15%). Pan
        // within the field is handled by the transform, so nothing to do.
        bool zoomStable = _gridBuiltZoom > 0 && Math.Abs(zoom / _gridBuiltZoom - 1.0) <= 0.15;
        if (step == _gridBuiltStep && zoomStable
            && visMinX >= _gridBuiltMinX && visMinY >= _gridBuiltMinY
            && visMaxX <= _gridBuiltMaxX && visMaxY <= _gridBuiltMaxY)
            return;

        double r = DotScreenRadius / zoom; // canvas-space radius → ~constant px on screen
        // The endless dot field rebuilds here on every pan/zoom that leaves the
        // built region — i.e. exactly during "panning around", the freeze repro.
        // Trace it so a
        // pan-time stall in this O(dots) build names itself in the watchdog
        // instead of hiding under the generic render tick. Bounded by GridMaxDots;
        // the early-out above keeps the common pan frame from entering here.
        using (Phoenix.Controls.Shared.Services.UiActivityTrace.Begin("Architect.DotGridRebuild"))
        {
            var group = new GeometryGroup();
            for (double x = minX; x <= maxX; x += step)
                for (double y = minY; y <= maxY; y += step)
                    group.Children.Add(new EllipseGeometry { Center = new Point(x, y), RadiusX = r, RadiusY = r });
            GridLayer.Data = group;
        }

        _gridBuiltStep = step;
        _gridBuiltZoom = zoom;
        _gridBuiltMinX = minX; _gridBuiltMinY = minY;
        _gridBuiltMaxX = maxX; _gridBuiltMaxY = maxY;
    }

    // Mark the dot grid for a rebuild on the next
    // settle tick instead of rebuilding inline on the pan critical path. Dead
    // cheap; called every pan/zoom frame from ApplyViewTransform + on resize.
    private void MarkDotGridDirty() => _dotGridDirty = true;

    // Drained from OnRenderingTick after the pan fast-path return (so it runs
    // when a pan/zoom gesture settles, never mid-pan). Clears _dotGridDirty
    // only once RefreshDotGrid has actually run — the
    // GridLayer-null guard returns WITHOUT clearing, so a not-yet-ready layer is
    // retried on a later tick rather than silently left stale. RefreshDotGrid's
    // own coverage early-out is a legitimate "done, still covered" outcome, so
    // clearing after it returns is correct.
    private void FlushDotGridRebuild()
    {
        if (!_dotGridDirty || _dotGridRebuilding) return;
        if (GridLayer is null) return; // not ready — stay dirty, retry next tick
        _dotGridRebuilding = true;
        try { RefreshDotGrid(); }
        finally { _dotGridRebuilding = false; }
        _dotGridDirty = false;
    }

    private void UpdateGridVisibility()
    {
        bool show = _vm?.ShowGrid ?? true;
        GridLayer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Canvas resized (initial layout, splitter drag, window resize) — the
    /// visible canvas rect changed, so re-evaluate the endless dot field's
    /// coverage. RefreshDotGrid early-returns when the existing field still
    /// covers the new viewport.
    /// </summary>
    private void OnCanvasSizeChangedForGrid(object sender, SizeChangedEventArgs e)
    {
        MarkDotGridDirty();
        // Resize reshapes the visible rect — repaint the GPU canvas so wires /
        // nodes straddling the new edges re-render without waiting on a gesture.
        MarkSceneDirty();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        MarkSceneDirty();   // VM-level state feeds the GPU draw; changes are rare
        if (e.PropertyName == nameof(LogicCanvasViewModel.ShowGrid))
            UpdateGridVisibility();
        // 0.10.0 — re-fire Welcome-card / EmptyHint cascade when the file
        // path flips. Loading a graph with no nodes (post-strip / fresh
        // .phxg) would otherwise still show the heavy Welcome card.
        if (e.PropertyName == nameof(LogicCanvasViewModel.LoadedFilePath))
            UpdateEmptyHint();
        // Keep ViewSurface's CompositeTransform glued to the VM's pan/zoom for
        // EVERY writer, not just the gesture paths that call ApplyViewTransform
        // themselves. LoadGraph (File → Open into the same VM, undo/redo replay,
        // recovery restore) writes PanX/PanY/Zoom straight on the VM and nothing
        // re-applied the transform afterwards. The Win2D canvas hid the drift —
        // it renders AND hit-tests from the live VM values — so the stale
        // transform surfaced only when EnterImmediateEdit mounted the editable
        // NodeView into NodeLayer (which renders through ViewSurface): the node
        // materialized at un-panned/un-zoomed coordinates — off-screen ("node
        // disappears") or mid-canvas ("pops to the center") — while the GPU
        // renderer skipped it as _editNode. Redundant during a drag-pan flush
        // (which already applies) — ApplyViewTransform is a few field writes,
        // and the dot-grid/cull work it marks is deferred + band-guarded.
        if (e.PropertyName is nameof(LogicCanvasViewModel.PanX)
                            or nameof(LogicCanvasViewModel.PanY)
                            or nameof(LogicCanvasViewModel.Zoom))
            ApplyViewTransform();
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_vm is not null)
        {
            UnhookWireDecorations();
            _vm.Nodes .CollectionChanged -= OnNodesChanged;
            _vm.Links .CollectionChanged -= OnLinksChanged;
            _vm.Frames.CollectionChanged -= OnFramesChanged;
            _vm.GraphLoaded              -= OnVmGraphLoaded;
            _vm.GraphMutatedAny          -= OnVmGraphMutatedForRender;
            _vm.PropertyChanged          -= OnVmPropertyChanged;
            _vm.SelectionChanged         -= OnVmSelectionChangedForHotkeys;
            _vm.RequestRevealNode         = null;
            ClearNodeViews();
            ClearFrameViews();
            // Detach the minimap from the prior VM before the
            // new one binds. Attach() (called below) re-hooks against the
            // incoming VM; without the explicit Detach here the minimap
            // would keep listening to the old VM's node/frame
            // PropertyChanged events and leak the subscription past the
            // VM swap.
            try { MiniMap?.Detach(); } catch { /* best-effort */ }
        }

        _vm = args.NewValue as LogicCanvasViewModel;

        // LinkLayer is still an ItemsControl in XAML — its DataTemplate's
        // Path Data uses absolute canvas coords from PathData, so the
        // ContentPresenter at Canvas.Left=0/Top=0 doesn't break wire
        // rendering (the wire draws where the model says, not where the
        // container is). Frames/Nodes need real positioning; LinkLayer is
        // fine as-is.
        LinkLayer.ItemsSource = _vm?.Links;

        if (_vm is not null)
        {
            // Compute the cull band + LOD regime BEFORE the bulk
            // add so AddNodeView realizes each node at the right level of detail on
            // load — proxies (cheap) when the graph binds zoomed-out, full views
            // only for the on-screen-and-zoomed-in set (the load-freeze win).
            // _cullDirty schedules a refining pass once node W/H are measured.
            if (_enableNodeVirtualization) { UpdateCullBand(); UpdateLodRegime(); _cullDirty = true; }
            foreach (var f in _vm.Frames) AddFrameView(f);
            foreach (var n in _vm.Nodes)  AddNodeView(n);
            CapturePerfBaselineSnapshot("graph-bind");

            _vm.Nodes .CollectionChanged += OnNodesChanged;
            _vm.Links .CollectionChanged += OnLinksChanged;
            _vm.Frames.CollectionChanged += OnFramesChanged;
            _vm.GraphLoaded              += OnVmGraphLoaded;
            _vm.GraphMutatedAny          += OnVmGraphMutatedForRender;
            _vm.PropertyChanged          += OnVmPropertyChanged;
            _vm.SelectionChanged         += OnVmSelectionChangedForHotkeys;
            // Seed the cheatsheet context from the fresh VM's selection
            // shape so a graph-load doesn't leave the overlay stuck on
            // whatever the prior graph last reported.
            RecomputeHotkeyContext();
            // Sub-graph editor windows fire RequestRevealNode on their parent
            // canvas's VM (0.10.0 multi-window — SubGraphWindow "Reveal
            // call-site" button). Frame + flash the matching Macro.Call /
            // Process.Spawn node so the user gets a visible anchor back to
            // where the macro/process is invoked from.
            _vm.RequestRevealNode         = RevealNodeFromShell;
            HookWireDecorations();
            ApplyViewTransform();
            UpdateGridVisibility();

            // 0.10.0 — when an ArchitectViewModel is wired up (MainView or
            // SubGraphWindow always sets it), defer to its shared History
            // so the parent canvas + every macro/process window write into
            // ONE undo stack. Falling back to a per-canvas controller only
            // when ArchitectVm is null preserves the headless / test path
            // that constructs a bare canvas without an AVM.
            if (_history is not null)
                _history.CanExecuteChanged -= OnHistoryCanExecuteChanged;
            _history = ArchitectVm?.History
                ?? new UndoRedoController(
                       capture: () => _vm.Graph,
                       apply:   g => _vm.LoadGraph(g));
            _history.CanExecuteChanged += OnHistoryCanExecuteChanged;

            // Bind the minimap to the new VM. Idempotent
            // Attach() re-hooks Nodes/Frames collections + Pan/Zoom/Pan
            // PropertyChanged. The CenterRequested handler routes a tap
            // on the minimap surface into CenterViewportOn(canvasX,
            // canvasY); the HideRequested handler routes the × glyph
            // into SetMinimapVisible(false) which also persists the
            // visibility flip into AppConfig.
            try
            {
                MiniMap?.Attach(this, _vm);
                if (MiniMap is not null)
                {
                    MiniMap.CenterRequested -= OnMinimapCenterRequested;
                    MiniMap.CenterRequested += OnMinimapCenterRequested;
                    MiniMap.HideRequested   -= OnMinimapHideRequested;
                    MiniMap.HideRequested   += OnMinimapHideRequested;
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("Architect.LogicCanvasView", "OnDataContextChanged minimap attach", ex);
            }
        }
        else
        {
            if (_history is not null)
                _history.CanExecuteChanged -= OnHistoryCanExecuteChanged;
            _history = null;
        }

        // Synthetic raise so listeners (HubChrome's Edit-menu enable bridge)
        // re-poll CanUndo/CanRedo against the freshly-bound controller — the
        // new history starts empty so both flags should now read false.
        UndoRedoChanged?.Invoke(this, EventArgs.Empty);

        UpdateEmptyHint();
    }

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        MarkSceneDirty();
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems is not null)
                    foreach (NodeViewModel vm in e.NewItems) AddNodeView(vm);
                break;
            case NotifyCollectionChangedAction.Remove:
                if (e.OldItems is not null)
                    foreach (NodeViewModel vm in e.OldItems) RemoveNodeView(vm);
                break;
            case NotifyCollectionChangedAction.Reset:
                ClearNodeViews();
                if (_vm is not null)
                {
                    if (_enableNodeVirtualization) { UpdateCullBand(); UpdateLodRegime(); _cullDirty = true; } // realize-at-LOD on reset
                    foreach (var vm in _vm.Nodes) AddNodeView(vm);
                }
                CapturePerfBaselineSnapshot("graph-reset");
                break;
            case NotifyCollectionChangedAction.Replace:
                if (e.OldItems is not null)
                    foreach (NodeViewModel vm in e.OldItems) RemoveNodeView(vm);
                if (e.NewItems is not null)
                    foreach (NodeViewModel vm in e.NewItems) AddNodeView(vm);
                break;
        }
        UpdateEmptyHint();
    }

    private void OnLinksChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => MarkSceneDirty();   // LinkLayer is ItemsControl-bound — no manual sync needed; the GPU canvas still repaints.

    // Catch-all repaint + row-cache drop on every committed graph mutation
    // (pill commits, socket activation, wire create/delete, undo restore …).
    // OnGraphMutated fires at commit granularity — never per pointer-move —
    // so the blanket cache clear is cheap and guarantees the GPU row layout
    // can never go stale against an edit that reshaped a node's rows.
    private void OnVmGraphMutatedForRender(object? sender, System.EventArgs e)
    {
        MarkSceneDirty();
        _nodeRowHeightsCache.Clear();
        NodeGeometry.MarkAllGeometryDirty(); // static geometry gate — same commit-granularity invalidation
    }

    /// <summary>
    /// Reset the per-VM undo history when LoadGraph swaps the underlying
    /// Graph, then run a one-shot Event.Trigger / Event.Executor /
    /// Event.Return socket-mirror so any pre-load drift between paired
    /// nodes heals before the user sees the canvas. Without history reset,
    /// opening a second file via File→Open carries the first file's
    /// snapshots forward — Ctrl+Z would walk back into the previous graph.
    /// </summary>
    private void OnVmGraphLoaded(object? sender, System.EventArgs e)
    {
        MarkSceneDirty();
        _nodeRowHeightsCache.Clear();
        // New graph → the cached CanvasTextLayouts belong to the OLD graph's
        // texts (content-addressed, so never stale — but native objects worth
        // freeing eagerly during rapid graph switching instead of waiting for
        // the entry-cap backstop).
        ClearTextLayoutCache();
        // Only reset when WE own the history (standalone canvas, no AVM).
        // The shared-AVM path owns the reset lifecycle itself (Open() /
        // NewGraph()) so the canvas-side reset would just race the AVM —
        // and worse, during Undo.Apply the AVM-shared controller is mid-
        // restore and resetting the stack would wipe the redo half. The
        // IsApplying guard makes the standalone path safe too.
        bool isUndoRestore = _history?.IsApplying == true;
        if (ArchitectVm is null && _history is not null && !isUndoRestore)
            _history.Reset();
        // Fresh graph → fresh orphan-warning dedupe so a previously-logged
        // orphan name in file A doesn't suppress its sibling in file B.
        _loggedOrphanNames.Clear();
        // 0.11.x — reset the per-node body-size
        // cache for the new graph (node ids can collide across .phxg files). The
        // text-width cache is deliberately NOT cleared anymore (content-stable,
        // process-scoped fonts) to avoid a cold-measure storm on reload — see
        // NodeGeometry.ResetTextWidthCache.
        NodeGeometry.ResetTextWidthCache();
        if (_vm is not null)
        {
            // 0.11.x lag-fix — during an undo/redo restore the graph is being
            // replaced with a previously-known-good snapshot whose
            // Event.Trigger / Event.Executor payload shape is already
            // consistent (it was authored under the same sync rules and
            // serialized with sockets intact). Re-running
            // PlaceholderActivator.SyncAllEventPairs (O(n²) — every event
            // node × every peer node), the per-node RebuildSockets fan-out
            // (each rebuilds two ObservableCollections + fires Width/Height
            // PropertyChanged), and RefreshEventPairErrorState (O(n²)
            // unpaired-detect + potential disk walk of every sibling .phxg)
            // on top of the restore is the dominant cost Majo flagged as
            // "Ctrl+Z with several nodes lags". Skip the trio during
            // IsApplying — the snapshot already has the right sockets, the
            // node VMs were re-bound by LoadGraph's collection swap, and the
            // error-state will refresh on the next user-driven structural
            // change.
            if (isUndoRestore) return;

            PlaceholderActivator.SyncAllEventPairs(_vm.Graph);
            // Re-rebuild every node VM in case SyncAllEventPairs mutated
            // socket lists; otherwise the load-time peer-sync stays
            // invisible until the user wires another link.
            foreach (var nvm in _vm.Nodes) nvm.RebuildSockets();

            // Macro.Call parameter-socket refresh on load / spawn. Pre-fix
            // OnVmGraphLoaded synced Event.Trigger/Executor pairs but had no
            // equivalent for Macro.Call nodes — a Call node loaded from disk (or
            // dropped via spawn) kept only its bare Flow in/out pair and could
            // not accept/provide data connections until the macro was manually
            // re-opened. Pull each referenced macro's current Entry/Exit
            // signature onto its Call nodes; the rebuild above already covered
            // the nodes whose sockets RefreshAllCallNodesForMacro mutates, but
            // run a scoped rebuild for the mutated set so a refresh that happens
            // AFTER the blanket rebuild (idempotent on the common path) still
            // repaints. SyncAllMacroCallSockets walks the macro list once.
            SyncAllMacroCallSockets();

            // Re-derive socket connectivity AFTER the blanket
            // RebuildSockets above. LoadGraph already ran SyncSocketConnectivity,
            // but the per-node RebuildSockets here (and SyncAllMacroCallSockets)
            // discard the old SocketViewModel instances and build fresh ones whose
            // IsConnected defaults to false — so without this re-sync every pin on
            // a freshly loaded graph reads PinFilled=false. Harmless in the retained
            // path (a body-filled "hollow" pin looked filled), the regression became
            // visible on the GPU canvas where a hollow pin is a true open ring:
            // connected bubbles stopped rendering as solid. Idempotent + O(sockets+
            // links); the live wire-add path already re-syncs via OnGraphMutated.
            _vm.SyncSocketConnectivity();

            RefreshEventPairErrorState();
        }
    }

    /// <summary>
    /// Sync every Macro.Call node in the loaded graph to its referenced
    /// macro's current Entry/Exit signature, then rebuild the socket VMs of any
    /// Call node whose model sockets actually changed. Macros are resolved from
    /// the graph's local <see cref="Graph.Macros"/> list (the in-document
    /// definitions); a Call node referencing a macro not present locally is left
    /// untouched (its signature can't be derived without the definition).
    /// </summary>
    private void SyncAllMacroCallSockets()
    {
        if (_vm is null) return;
        var macros = _vm.Graph.Macros;
        if (macros is null || macros.Count == 0) return;

        var mutatedNodeIds = new System.Collections.Generic.HashSet<string>();
        foreach (var macro in macros)
        {
            if (macro is null) continue;
            foreach (var id in _vm.RefreshAllCallNodesForMacro(macro))
                mutatedNodeIds.Add(id);
        }

        foreach (var id in mutatedNodeIds)
            _vm.FindNode(id)?.RebuildSockets();
    }

    private void OnFramesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        MarkSceneDirty();
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems is not null)
                    foreach (FrameViewModel vm in e.NewItems) AddFrameView(vm);
                break;
            case NotifyCollectionChangedAction.Remove:
                if (e.OldItems is not null)
                    foreach (FrameViewModel vm in e.OldItems) RemoveFrameView(vm);
                break;
            case NotifyCollectionChangedAction.Reset:
                ClearFrameViews();
                if (_vm is not null)
                    foreach (var vm in _vm.Frames) AddFrameView(vm);
                break;
            case NotifyCollectionChangedAction.Replace:
                if (e.OldItems is not null)
                    foreach (FrameViewModel vm in e.OldItems) RemoveFrameView(vm);
                if (e.NewItems is not null)
                    foreach (FrameViewModel vm in e.NewItems) AddFrameView(vm);
                break;
        }
    }

    // ─── NodeLayer manual children ──────────────────────────────────────

    private void AddNodeView(NodeViewModel vm)
    {
        if (!_subscribedNodes.Add(vm)) return;   // already tracked (the single source of "node is known")
        vm.PropertyChanged += OnNodeVmPropChanged;
        // In immediate mode the GPU canvas draws
        // every node; don't realize a retained NodeView (the whole point — no
        // per-node visual tree). The VM is already in _vm.Nodes for the renderer.
        if (_useImmediateMode) return;
        // Realize at the current level of detail: a full NodeView
        // (zoomed in + on-screen), a lightweight proxy Border (zoomed out + on-
        // screen), or nothing (off-screen). The full NodeView is built LAZILY
        // inside RealizeNode → EnsureFullView, so a bulk load of a zoomed-out graph
        // never constructs the heavy ~48-149-element node tree — that is the load-
        // freeze win. Flag OFF → RealizeNode always mounts a full view = legacy.
        RealizeNode(vm);
    }

    // Add the (already-created) NodeView into the visual tree. Resyncs
    // Canvas.Left/Top from the LIVE vm.X/Y FIRST — a node culled off-screen can have
    // its X/Y mutated (undo / paste / frame-drag) while it was unmounted, so the
    // cached attached-property value is stale; reading vm.X/Y here lands it right.
    private void MountNodeView(NodeViewModel vm, NodeView view)
    {
        if (!_realizedNodes.Add(vm)) return;   // already mounted
        view._isCulling = false;                // a remount must permit a future real teardown
        view.StampOwnerCanvas(this);            // commit-tail canvas fallback for detached LostFocus
        XamlCanvas.SetLeft(view, vm.X);
        XamlCanvas.SetTop (view, vm.Y);
        NodeLayer.Children.Add(view);
    }

    // Remove the NodeView from the visual tree for a viewport cull. The
    // _isCulling flag makes NodeView.OnUnloaded skip its teardown so the view is
    // reused unchanged on remount (keep-alive). The object stays in _nodeViews.
    private void CullUnmountNodeView(NodeViewModel vm, NodeView view)
    {
        if (!_realizedNodes.Remove(vm)) return; // already unmounted
        view._isCulling = true;
        NodeLayer.Children.Remove(view);
    }

    private void RemoveNodeView(NodeViewModel vm)
    {
        // A node may exist only as a proxy (never zoomed into), so
        // its presence is tracked by _subscribedNodes, not _nodeViews.
        if (!_subscribedNodes.Remove(vm)) return;
        vm.PropertyChanged -= OnNodeVmPropChanged;
        _nodeRowHeightsCache.Remove(vm);

        // Full view (if one was ever lazily built).
        if (_nodeViews.Remove(vm, out var view))
        {
            bool wasRealized = _realizedNodes.Remove(vm);
            view._isCulling = false;            // real removal → full teardown
            if (wasRealized)
                NodeLayer.Children.Remove(view); // fires OnUnloaded → RunUnloadTeardown
            else
                view.RunUnloadTeardown();        // culled (not in tree) → tear down directly so listeners don't leak
        }

        // Proxy (if one was ever built).
        if (_nodeProxies.Remove(vm, out var proxy) && _realizedProxy.Remove(vm))
            NodeLayer.Children.Remove(proxy);
    }

    private void ClearNodeViews()
    {
        // A graph reset / DataContext rebind ends any open GPU-canvas edit
        // session. Exit BEFORE the wholesale clear so _editNode is nulled and
        // the renderer resumes drawing that node — a stale _editNode poisoned
        // EnterImmediateEdit's same-node early-return (the node vanished and
        // the editor focused a detached subtree on the next pill edit).
        ExitImmediateEdit();
        // Clearing every node view closes every inline editor (they live inside
        // NodeViews). Force the accelerator gate inactive so a view-model
        // discarded mid-edit can't leave InlineEditGate stuck "on" (which would
        // wedge the menu chords disabled until restart). See InlineEditGate.
        InlineEditGate.Reset();
        // Unsubscribe via the authoritative tracking set —
        // _nodeViews no longer holds proxy-only nodes (lazy full views), so
        // iterating it alone would leak their PropertyChanged subscriptions.
        foreach (var vm in _subscribedNodes)
            vm.PropertyChanged -= OnNodeVmPropChanged;
        _subscribedNodes.Clear();
        _nodeRowHeightsCache.Clear();

        foreach (var (vm, view) in _nodeViews)
        {
            view._isCulling = false;            // full teardown for all
            // Mounted views tear down via Children.Clear() below; culled (out-of-
            // tree) views get no Unloaded, so tear them down directly here.
            if (!_realizedNodes.Contains(vm))
                view.RunUnloadTeardown();
        }
        _nodeViews.Clear();
        _realizedNodes.Clear();
        _nodeProxies.Clear();
        _realizedProxy.Clear();
        NodeLayer.Children.Clear();             // removes mounted full views + proxies
    }

    private void OnNodeVmPropChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not NodeViewModel vm) return;
        // Any node-VM property can affect the GPU-drawn body (position, size,
        // selection, flash, error, title, …) — repaint, and drop the row-height
        // cache entry since socket/attribute-shape changes surface here too
        // (RebuildSockets raises Width/Height).
        MarkSceneDirty();
        _nodeRowHeightsCache.Remove(vm);
        NodeGeometry.MarkGeometryDirty(vm.Id); // static geometry gate — mirror the per-VM row-cache drop
        // Reposition whichever representation is mounted — the
        // full NodeView and/or the proxy Border. A node may have only a proxy
        // (zoomed out), only a full view, neither (off-screen), and a full view
        // can briefly coexist with a kept-alive proxy across a regime transition.
        switch (e.PropertyName)
        {
            case nameof(NodeViewModel.X):
                if (_nodeViews  .TryGetValue(vm, out var vx)) XamlCanvas.SetLeft(vx, vm.X);
                if (_nodeProxies.TryGetValue(vm, out var px)) XamlCanvas.SetLeft(px, vm.X);
                MarkNodeMovedForCull();
                break;
            case nameof(NodeViewModel.Y):
                if (_nodeViews  .TryGetValue(vm, out var vy)) XamlCanvas.SetTop(vy, vm.Y);
                if (_nodeProxies.TryGetValue(vm, out var py)) XamlCanvas.SetTop(py, vm.Y);
                MarkNodeMovedForCull();
                break;
            // The full NodeView paints its own selection ring (NodeView.ApplyBorder,
            // driven by NodeView's own VM subscription). A proxy has no such logic,
            // so mirror selection onto the mounted proxy here.
            case nameof(NodeViewModel.IsSelected):
                if (_realizedProxy.Contains(vm) && _nodeProxies.TryGetValue(vm, out var ps))
                    ApplyProxySelection(ps, vm);
                break;
            // A node that grew / shrank to fit its content moved
            // its pins to new edge coordinates. The GPU canvas repaints on the
            // MarkSceneDirty above, so the node body + pins follow automatically
            // — but the incident wires render
            // from cached anchors that only recompute when a link is flagged dirty.
            // TranslateNode flags them on MOVE; resize had no equivalent, so wires
            // detached from resized pins ("bubbles outside the node") until the next
            // drag. Flag the node's wires for re-anchor on the next tick.
            case nameof(NodeViewModel.Width):
            case nameof(NodeViewModel.Height):
                // A node that grew / shrank to fit a newly-activated bubble must also
                // resize a mounted LOD proxy Border, or the zoomed-out proxy keeps
                // its old dimensions until the node is re-realized.
                if (_realizedProxy.Contains(vm) && _nodeProxies.TryGetValue(vm, out var pr))
                    ConfigureProxy(pr, vm);
                _vm?.MarkNodeLinksDirty(vm.Id);
                break;
        }
    }

    // ─── Viewport node cull ─────────────────────────────────────────────

    // Visible canvas-space rect (inverse of pan+zoom) — same math as
    // RefreshDotGrid. Drives which NodeViews the cull mounts.
    private Windows.Foundation.Rect ComputeVisibleRect()
    {
        double zoom = Math.Max(_vm?.Zoom ?? 1.0, 0.0001);
        double panX = _vm?.PanX ?? 0.0;
        double panY = _vm?.PanY ?? 0.0;
        double viewW = HostRoot?.ActualWidth  ?? 0.0;
        double viewH = HostRoot?.ActualHeight ?? 0.0;
        if (viewW <= 0 || viewH <= 0) { viewW = 1920.0; viewH = 1080.0; } // pre-measure fallback
        double minX = (0.0   - panX) / zoom;
        double minY = (0.0   - panY) / zoom;
        double maxX = (viewW - panX) / zoom;
        double maxY = (viewH - panY) / zoom;
        return new Windows.Foundation.Rect(minX, minY, Math.Max(0, maxX - minX), Math.Max(0, maxY - minY));
    }

    // Recompute the cull band (visible rect inflated by CullMarginFactor). Returns
    // true only when the NEW visible rect left the OLD band — the hysteresis that
    // stops small pans from re-culling every settle (reuses the DotGrid idiom).
    private bool UpdateCullBand()
    {
        var vis = ComputeVisibleRect();
        bool insideOldBand = _cullBand.Width > 0
            && vis.X >= _cullBand.X && vis.Y >= _cullBand.Y
            && vis.X + vis.Width  <= _cullBand.X + _cullBand.Width
            && vis.Y + vis.Height <= _cullBand.Y + _cullBand.Height;
        if (insideOldBand) return false;
        double mx = vis.Width  * CullMarginFactor;
        double my = vis.Height * CullMarginFactor;
        _cullBand = new Windows.Foundation.Rect(vis.X - mx, vis.Y - my, vis.Width + 2 * mx, vis.Height + 2 * my);
        return true;
    }

    // Does the node's MODEL rect intersect the current cull band? Model-based so it
    // works for never-realized nodes (W/H fall back to a template estimate).
    private bool NodeIntersectsCullBand(NodeViewModel vm)
    {
        if (_cullBand.Width <= 0) return true; // no band yet (first load) → mount; first cull refines
        double w = vm.Width  > 0 ? vm.Width  : 200.0;
        double h = vm.Height > 0 ? vm.Height : 60.0;
        return vm.X < _cullBand.X + _cullBand.Width
            && vm.X + w > _cullBand.X
            && vm.Y < _cullBand.Y + _cullBand.Height
            && vm.Y + h > _cullBand.Y;
    }

    // NOTE: the old cull pass (RefreshNodeCull) was replaced by RefreshNodeRealization
    // in LogicCanvasView.Lod.cs — it diffs every node against the settled viewport +
    // zoom and transitions it between None / Proxy / Full (the cull is now the
    // None<->Proxy/Full boundary; the LOD is the Proxy<->Full boundary).

    // Ctrl+Alt+F6 KILL-SWITCH for node virtualization + LOD, which is
    // ON by default. Flip it OFF to fall back to the legacy "every node is a full,
    // always-mounted NodeView" path if a pathological graph or an off-screen
    // regression ever appears — the LOD machinery costs nothing when off. Off-
    // screen wire-drop is redirected model-side (ResolveSocketAtCanvasPoint); right-
    // clicking / wiring a node that is currently a zoomed-out proxy or culled off-
    // screen is an accepted limitation (zoom/pan it into view first). The
    // perf-baseline snapshot logs the resulting element count for comparison.
    internal void ToggleNodeVirtualization()
    {
        _enableNodeVirtualization = !_enableNodeVirtualization;
        if (_vm is not null)
        {
            if (_enableNodeVirtualization)
            {
                _cullBand = default;          // clear hysteresis → reconcile from scratch
                ForceFullRealization();       // proxy / full / none per the current viewport + zoom
            }
            else
            {
                // Legacy: every node becomes a full, mounted NodeView; drop proxies.
                foreach (var vm in _vm.Nodes) ApplyNodeReal(vm, NodeReal.Full);
                // And every wire visible — the settle-pass link cull no
                // longer runs, so any wire collapsed by an earlier cull must be restored.
                foreach (var lvm in _vm.Links) lvm.IsViewportVisible = true;
            }
            _cullDirty = false;
            _cullSettleFrames = 0;
        }
        GlobalLogger.Log(
            $"[Tranche-2b] Node virtualization {(_enableNodeVirtualization ? "ENABLED (default, LOD)" : "DISABLED — legacy mount-all path")} — "
            + $"{_realizedNodes.Count} full + {_realizedProxy.Count} proxy / {(_vm?.Nodes.Count ?? 0)} nodes.",
            "Architect.Canvas", LogLevel.System);
    }

    // ─── Perf-baseline measurement harness ──────────────────────────────
    // Turns the EXTRAPOLATED per-node element count into a MEASURED one
    // and gives the virtualization work a before/after gate. NEVER per-frame:
    // each call enqueues a SINGLE Low-priority BFS over the realized NodeLayer
    // subtree once layout has settled, then logs one perf-baseline line to the
    // rolling %AppData%/PhoenixControls/logs file. Strip with the rest of the
    // perf instrumentation once the numbers are captured.
    private void CapturePerfBaselineSnapshot(string trigger)
    {
        var queue = DispatcherQueue;
        if (queue is null) return;
        queue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            int total    = _vm?.Nodes.Count ?? 0;
            int full     = _realizedNodes.Count;
            int proxy    = _realizedProxy.Count;
            int links    = _vm?.Links.Count ?? 0;
            int elements = CountRealizedUiElements(NodeLayer);
            int shown    = full + proxy;
            // Publish the pillar status for the freeze diagnostics — a hang
            // report can then state WHAT Architect was holding when the UI
            // wedged (the streaming-PC freezes cluster around graph bind/reset).
            PillarDiagnostics.SetArchitectStatus(
                $"{trigger}: {total} nodes, {links} links ({(_useImmediateMode ? "GPU canvas" : "retained canvas")})");
            GlobalLogger.Log(
                $" {trigger}: {total} nodes → {full} full + {proxy} proxy mounted "
                + $"(+ {links} links) = {elements} UIElements in NodeLayer (~{(shown > 0 ? elements / shown : 0)}/mounted). "
                + "[Tranche-2b LOD] this is the O(visible) the framework measure/arrange walk pays — "
                + "proxies collapse a zoomed-out node from ~48-149 elements to ~1-2.",
                "Architect.Canvas", LogLevel.System);
        });
    }

    // On-demand recursive UIElement count over a realized subtree. O(tree) — call
    // ONLY from the deferred CapturePerfBaselineSnapshot, never on the hot path.
    private static int CountRealizedUiElements(Microsoft.UI.Xaml.DependencyObject? root)
    {
        if (root is null) return 0;
        int count = 1;
        int children = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < children; i++)
            count += CountRealizedUiElements(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i));
        return count;
    }

    // ─── FrameLayer manual children ─────────────────────────────────────

    private void AddFrameView(FrameViewModel vm)
    {
        if (_frameViews.ContainsKey(vm)) return;
        var view = BuildFrameElement(vm);
        XamlCanvas.SetLeft(view, vm.X);
        XamlCanvas.SetTop (view, vm.Y);
        // Canvas.ZIndex per-frame so "Bring to Front" /
        // "Send to Back" in the frame context menu honors Frame.ZOrder
        // without rebuilding the children list. Insertion order still
        // breaks ties at ZOrder=0 (matches pre-fix behaviour).
        XamlCanvas.SetZIndex(view, vm.ZOrder);
        FrameLayer.Children.Add(view);
        _frameViews[vm] = view;
        vm.PropertyChanged += OnFrameVmPropChanged;
        // 0.11.x polish — recompute overlap tint depths now that a new
        // frame entered the set. Lag-fix: dirty-flag + render-tick drain
        // so an N-frame LoadGraph collapses N redundant requests into
        // a single O(F²) pass on the next render tick.
        _frameOverlapTintsDirty = true;
    }

    private void RemoveFrameView(FrameViewModel vm)
    {
        if (!_frameViews.Remove(vm, out var view)) return;
        vm.PropertyChanged -= OnFrameVmPropChanged;
        FrameLayer.Children.Remove(view);
        // 0.11.x polish — depth drops for everyone the removed frame used
        // to intersect; re-run. Coalesced via the render-tick drain.
        _frameOverlapTintsDirty = true;
    }

    private void ClearFrameViews()
    {
        foreach (var (vm, _) in _frameViews) vm.PropertyChanged -= OnFrameVmPropChanged;
        _frameViews.Clear();
        FrameLayer.Children.Clear();
    }

    private void OnFrameVmPropChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not FrameViewModel vm) return;
        MarkSceneDirty();   // frames render on the GPU canvas too
        if (!_frameViews.TryGetValue(vm, out var view) || view is not Border border) return;
        switch (e.PropertyName)
        {
            case nameof(FrameViewModel.X):
                XamlCanvas.SetLeft(view, vm.X);
                _frameOverlapTintsDirty = true;
                break;
            case nameof(FrameViewModel.Y):
                XamlCanvas.SetTop (view, vm.Y);
                _frameOverlapTintsDirty = true;
                break;
            case nameof(FrameViewModel.Width):
                border.Width  = vm.Width;
                _frameOverlapTintsDirty = true;
                break;
            case nameof(FrameViewModel.Height):
                border.Height = vm.Height;
                _frameOverlapTintsDirty = true;
                break;
            case nameof(FrameViewModel.Label):
                if (border.Child is Grid g && g.Children.OfType<TextBlock>().FirstOrDefault() is { } tb)
                    tb.Text = vm.Label;
                break;
            case nameof(FrameViewModel.IsSelected):
                ApplyFrameSelectionHalo(border, vm);
                break;
            // Frame color picker fires RaiseAllChanged which
            // bumps FrameColorHex / FrameFillHex; re-tint the border + fill in
            // place rather than rebuilding the whole frame view. 0.11.x polish
            // — the overlap-tint blue is re-blended on top of the new colour
            // automatically via the depth pass.
            case nameof(FrameViewModel.FrameColorHex):
            case nameof(FrameViewModel.FrameFillHex):
                _frameOverlapTintsDirty = true;
                break;
            // Bring to Front / Send to Back mutates ZOrder
            // on the model and the canvas re-applies Canvas.ZIndex so the
            // FrameLayer paints with the new stacking.
            case nameof(FrameViewModel.ZOrder):
                XamlCanvas.SetZIndex(view, vm.ZOrder);
                break;
            // "Convert to Comment / Placeholder" flips IsPlaceholder
            // (via RaiseAllChanged) and the placeholder visuals — hatch overlay,
            // dashed border, transparent Border, "// " title — are built once in
            // BuildFrameElement. Rebuild the frame element in place so the
            // placeholder distinction appears (or clears) immediately rather
            // than only on the next graph reload.
            case nameof(FrameViewModel.IsPlaceholder):
                RebuildFrameView(vm);
                break;
        }
    }

    /// <summary>
    /// Rebuild a frame's visual element in place — used when a
    /// state that BuildFrameElement bakes at construction time changes at
    /// runtime (currently IsPlaceholder, which gates the hatch/dashed/border
    /// treatment). Preserves position + z-order + selection halo.
    /// </summary>
    private void RebuildFrameView(FrameViewModel vm)
    {
        if (!_frameViews.TryGetValue(vm, out var old)) return;

        int idx = FrameLayer.Children.IndexOf(old);
        FrameLayer.Children.Remove(old);

        var view = BuildFrameElement(vm);
        XamlCanvas.SetLeft(view, vm.X);
        XamlCanvas.SetTop (view, vm.Y);
        XamlCanvas.SetZIndex(view, vm.ZOrder);
        if (idx >= 0 && idx <= FrameLayer.Children.Count)
            FrameLayer.Children.Insert(idx, view);
        else
            FrameLayer.Children.Add(view);
        _frameViews[vm] = view;

        // Re-apply the selection halo + overlap tint against the fresh element.
        if (view is Border b) ApplyFrameSelectionHalo(b, vm);
        _frameOverlapTintsDirty = true;
    }

    // ─── 0.11.x polish — frame overlap tint ─────────────────────────────
    //
    // Each frame whose Bounds intersect one or more sibling frames gets a
    // blue overlay alpha-blended on top of its own fill. The alpha scales
    // with the overlap depth (count of OTHER frames this one intersects)
    // so up to ~12 layers of nested / overlapping frames remain visually
    // distinguishable. Tint colour is sourced from PinIntColorBrush
    // (#7FBED1) — the brightest existing blue in the Phoenix palette, used
    // elsewhere for Int data-type sockets — so the overlap signal lands
    // on-theme without a new palette entry.
    //
    // Two channels move per overlap level so a SINGLE overlap is already
    // clearly visible (Majo: the previous 9.4%-RGB-only blend at the frame's
    // 25% surface alpha was imperceptible — "frame multi-layer color change
    // does not work"):
    //   • RGB shift toward the blue overlay — OverlapAlphaPerLevel (0x40 ≈
    //     25% per level, capped at 0xD8 ≈ 85%).
    //   • Surface opacity bump — OverlapSurfaceAlphaPerLevel (0x2A ≈ 16% per
    //     level on top of the frame's native ~25% fill alpha, capped at 0xCC
    //     ≈ 80%) so the overlapped region reads denser, not just hue-shifted.
    // Frames paint in the background FrameLayer (below the node layer), so the
    // denser fill never obscures the nodes grouped inside the frame.
    // depth=0 means "no intersection" → no tint at all and the frame paints
    // its native colour + native alpha unchanged.

    private const byte OverlapTintR = 0x7F;
    private const byte OverlapTintG = 0xBE;
    private const byte OverlapTintB = 0xD1;
    private const int  OverlapAlphaPerLevel        = 0x40;
    private const int  OverlapAlphaCap             = 0xD8;
    private const int  OverlapSurfaceAlphaPerLevel = 0x2A;
    private const int  OverlapSurfaceAlphaCap      = 0xCC;

    private void RefreshFrameOverlapTints()
    {
        if (_vm is null || _frameViews.Count == 0) return;

        // Snapshot to avoid mutating the dictionary during enumeration (a
        // re-entrant frame-mutation propagating an X/Y change inside this
        // loop would otherwise race the iterator).
        var pairs = _frameViews.ToArray();

        foreach (var (vm, view) in pairs)
        {
            if (view is not Border border) continue;
            int depth = ComputeOverlapDepth(vm, pairs);
            ApplyFrameTint(border, vm, depth);
        }
    }

    /// <summary>
    /// 0.11.x lag-fix drain — runs the O(F²) tint pass at most once per
    /// displayed frame. Cheap no-op when the dirty flag is clear, so the
    /// permanent <see cref="OnRenderingTick"/> subscription doesn't tax
    /// the canvas when nothing changed.
    /// </summary>
    private void FlushFrameOverlapTintsIfDirty()
    {
        if (!_frameOverlapTintsDirty) return;
        _frameOverlapTintsDirty = false;
        RefreshFrameOverlapTints();
    }

    private static int ComputeOverlapDepth(FrameViewModel vm,
        System.Collections.Generic.KeyValuePair<FrameViewModel, FrameworkElement>[] all)
    {
        int depth = 0;
        var a = vm.Model.Bounds;
        foreach (var (other, _) in all)
        {
            if (ReferenceEquals(other, vm)) continue;
            var b = other.Model.Bounds;
            if (a.IntersectsWith(b)) depth++;
        }
        return depth;
    }

    /// <summary>
    /// Repaint the frame border + fill from the VM's current
    /// FrameColorHex / FrameFillHex. Honors the IsSelected halo (selection
    /// gold wins) by deferring to <see cref="ApplyFrameSelectionHalo"/> when
    /// the frame is the active selection. Mirrors BuildFrameElement's initial
    /// brush assignment but operates on an existing tree so the color-picker
    /// commit doesn't tear down + rebuild the whole frame view.
    ///
    /// 0.11.x polish — <paramref name="overlapDepth"/> is the count of other
    /// frames this one intersects. When non-zero the fill is alpha-blended
    /// against the Phoenix Int-blue (#7FBED1) so a stack of overlapping
    /// frames reads as a progressive blue gradient instead of a wall of
    /// identical-colour rectangles. Depth=0 paints the native fill.
    /// </summary>
    private static void ApplyFrameTint(Border border, FrameViewModel vm, int overlapDepth = 0)
    {
        var fill = ParseHex(vm.FrameFillHex);
        if (overlapDepth > 0)
        {
            fill = BlendOverlapTint(fill, overlapDepth);
        }
        border.Background = new SolidColorBrush(fill);
        if (vm.IsSelected)
        {
            // Selection halo overrides the per-frame border colour; keep the
            // gold ring while selected and the new colour reappears on
            // deselect via ApplyFrameSelectionHalo's else-branch.
            ApplyFrameSelectionHalo(border, vm);
        }
        else
        {
            // Placeholder frames keep a transparent Border — their
            // visible border is the dashed overlay Rectangle built in
            // BuildFrameElement, so a solid colour border here would double the
            // outline. Comment frames restore their solid colour border.
            var c = ParseHex(vm.FrameColorHex);
            border.BorderBrush = vm.IsPlaceholder
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0))
                : new SolidColorBrush(c);
        }
        // Tint only the tagged elements that actually carry the
        // frame colour: the header strip (Tag="header") and the BR resize
        // corner glyph (Tag="resize:bottomright"). The title block now stays
        // bright paper-white regardless of frame color (see BuildFrameElement);
        // pre-fix this loop also re-tinted every Rectangle in the grid, which
        // would have flipped the previously-transparent resize edge handles
        // visible after a colour change.
        if (border.Child is Grid g)
        {
            var c = ParseHex(vm.FrameColorHex);
            var headerBrush = new SolidColorBrush(
                Windows.UI.Color.FromArgb(0xB0, c.R, c.G, c.B));
            var cornerBrush = new SolidColorBrush(c);
            foreach (var fe in g.Children.OfType<FrameworkElement>())
            {
                if (fe is Rectangle r && fe.Tag is string tag)
                {
                    if (tag == "header") r.Fill = headerBrush;
                    else if (tag == "resize:bottomright") r.Fill = cornerBrush;
                }
            }
        }
    }

    /// <summary>
    /// Alpha-blend the overlap-tint blue onto <paramref name="fill"/> for the
    /// given <paramref name="depth"/>, AND raise the surface alpha so the
    /// overlapped region reads denser. The RGB channels shift toward the blue
    /// overlay; the alpha climbs from the frame's native fill alpha toward
    /// <see cref="OverlapSurfaceAlphaCap"/>. depth must be &gt; 0 (callers gate
    /// on that) — depth 0 would be a no-op shift but still bump alpha, so it is
    /// never invoked in that case.
    /// </summary>
    private static Windows.UI.Color BlendOverlapTint(Windows.UI.Color fill, int depth)
    {
        int blendI = System.Math.Min(OverlapAlphaPerLevel * depth, OverlapAlphaCap);
        double a = blendI / 255.0;
        double inv = 1.0 - a;
        byte r = (byte)System.Math.Round(fill.R * inv + OverlapTintR * a);
        byte g = (byte)System.Math.Round(fill.G * inv + OverlapTintG * a);
        byte b = (byte)System.Math.Round(fill.B * inv + OverlapTintB * a);
        byte surfaceA = (byte)System.Math.Min(
            fill.A + OverlapSurfaceAlphaPerLevel * depth, OverlapSurfaceAlphaCap);
        return Windows.UI.Color.FromArgb(surfaceA, r, g, b);
    }

    // Cached bright foreground for the frame title (decoupled
    // from the frame's border colour so the label stays legible regardless
    // of the user-picked frame tint). Mirrors the cache pattern on
    // ResolveOkBrush / ResolveErrBrush.
    private static Brush? s_frameTitleBrushCached;
    private static readonly SolidColorBrush s_frameTitleFallback =
        new(Windows.UI.Color.FromArgb(0xFF, 0xF2, 0xEE, 0xE6));
    private static Brush ResolveFrameTitleBrush()
        => s_frameTitleBrushCached
           ??= (Application.Current?.Resources["CoalPaperBrush"] as Brush)
               ?? s_frameTitleFallback;

    /// <summary>
    /// Repaint the frame's border in the selection gold (#FFD700) when
    /// <see cref="FrameViewModel.IsSelected"/> flips true; revert to the
    /// frame's own color otherwise. Width also bumps slightly so the
    /// halo reads at a glance against the existing 1.5 px frame border.
    /// </summary>
    // Tag used to find / manage the dashed selection-outline overlay
    // inside a frame's Grid. WinUI Border can't carry a dashed stroke, so the
    // selection outline is a dashed Rectangle overlaid on selection and removed
    // on deselect — making the gold selection ring visually distinct from the
    // frame's own solid (or, for placeholders, dashed-colour) perimeter.
    private const string FrameSelectionOutlineTag = "frameSelectionOutline";

    private static void ApplyFrameSelectionHalo(Border border, FrameViewModel vm)
    {
        var grid = border.Child as Grid;

        if (vm.IsSelected)
        {
            var selection = (Application.Current?.Resources?["SelectionBrush"] as Brush)
                ?? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00));
            // Keep a thicker solid Border ring underneath for the filled-edge
            // read, but layer a dashed gold Rectangle on top so the selection
            // outline is clearly dashed (distinct from the frame's own border).
            border.BorderBrush     = selection;
            border.BorderThickness = new Thickness(2.5);

            if (grid is not null && FindFrameSelectionOutline(grid) is null)
            {
                grid.Children.Add(new Rectangle
                {
                    Stroke              = selection,
                    StrokeThickness     = 2.0,
                    StrokeDashArray     = new DoubleCollection { 4, 2 },
                    Fill                = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                    RadiusX             = 4,
                    RadiusY             = 4,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment   = VerticalAlignment.Stretch,
                    IsHitTestVisible    = false,
                    Tag                 = FrameSelectionOutlineTag,
                });
            }
        }
        else
        {
            var c = ParseHex(vm.FrameColorHex);
            // Placeholder frames keep a transparent Border (their visible border
            // is the dashed overlay built in BuildFrameElement); comment frames
            // restore their solid colour border.
            border.BorderBrush     = new SolidColorBrush(vm.IsPlaceholder ? Windows.UI.Color.FromArgb(0, 0, 0, 0) : c);
            border.BorderThickness = new Thickness(1.5);

            if (grid is not null && FindFrameSelectionOutline(grid) is Rectangle outline)
                grid.Children.Remove(outline);
        }
    }

    private static Rectangle? FindFrameSelectionOutline(Grid grid)
    {
        foreach (var child in grid.Children)
            if (child is Rectangle r && (r.Tag as string) == FrameSelectionOutlineTag)
                return r;
        return null;
    }

    // Frame resize hit-zone thicknesses. The 10 px corner
    // boxes match the earlier single-corner Rectangle; the 6 px edge
    // strips are slim enough to leave the bulk of the border free for body-
    // drag, but thick enough to be a stable hit target on a high-DPI display.
    private const double FrameEdgeHandleThickness = 6.0;
    private const double FrameCornerHandleSize    = 10.0;

    // Frames are now ONLY draggable
    // from a visible header strip across the top of the frame. Body clicks
    // select the frame but do not move it. This matches the pre-T15 idiom
    // and prevents accidental drags from corner-of-eye clicks on a large
    // comment region. The header is sized to comfortably hold the 14 pt
    // title (FrameHeaderHeight) with a tinted background of the frame's
    // color so the grab affordance reads at a glance.
    private const double FrameHeaderHeight = 26.0;

    private static FrameworkElement BuildFrameElement(FrameViewModel vm)
    {
        // Mirrors the original DataTemplate from LogicCanvasView.xaml: a
        // titled Border with resize handles. The
        // single bottom-right 10×10 corner is expanded into a full set of 4 edge strips
        // + 4 corner boxes so all eight resize affordances are reachable —
        // matching pre-T15 WinForms Architect. Tag values use the convention
        // `resize:<edge>` so `ResolveFrameResizeEdge` can map a hit element
        // back to the `FrameResizeEdge` enum without losing direction.
        // Tag = the FrameViewModel on the Border so HitTagFrom resolves on hit-test.
        var color = ParseHex(vm.FrameColorHex);
        var fill  = ParseHex(vm.FrameFillHex);

        var border = new Border
        {
            Width            = vm.Width,
            Height           = vm.Height,
            BorderBrush      = new SolidColorBrush(color),
            Background       = new SolidColorBrush(fill),
            BorderThickness  = new Thickness(1.5),
            CornerRadius     = new CornerRadius(4),
            Tag              = vm,
        };

        var grid = new Grid();

        // Placeholder frame visual indicators — restore the three
        // baseline distinctions (Canvas.PaintBackground.cs) so a placeholder
        // section (nodes skipped on export) reads differently from a comment
        // frame: (1) a ForwardDiagonal hatch overlay, (2) a dashed border, and
        // (3) the "// " title prefix (the prefix arrives via FrameViewModel.Label
        // so it is already in titleBlock.Text below). The Border itself can't
        // carry a dashed stroke in WinUI, so the solid Border border is dropped
        // to transparent and a dashed Rectangle stroke is overlaid in its place.
        if (vm.IsPlaceholder)
        {
            border.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

            // Diagonal-hatch overlay (forward diagonal), faint so the body fill
            // and node bodies underneath stay legible. Hit-test-invisible so it
            // never eats a body click / resize grab.
            grid.Children.Add(BuildPlaceholderHatch(vm.Width, vm.Height, color));

            // Dashed border overlay matching the frame's corner radius. Inset by
            // half the stroke so the dashes sit on the frame perimeter like the
            // Border's solid border would.
            grid.Children.Add(new Rectangle
            {
                Stroke              = new SolidColorBrush(color),
                StrokeThickness     = 1.5,
                StrokeDashArray     = new DoubleCollection { 4, 2 },
                Fill                = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                RadiusX             = 4,
                RadiusY             = 4,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment   = VerticalAlignment.Stretch,
                IsHitTestVisible    = false,
            });
        }

        // Visible header strip
        // anchored to the top of the frame. Painted ABOVE the soft fill but
        // BELOW the resize edges so the top 6px stays grabable for resize.
        // The header carries Tag="header" so OnHostPointerPressed can
        // distinguish a header-drag (move) from a body-click (select only)
        // — pre-fix the whole frame body translated on left-drag and Majo
        // flagged it as accidental moves. Background uses the frame's color
        // at higher alpha (0xB0) so the strip reads visibly tinted against
        // the body's soft fill (#40<rgb>). The strip and the title share a
        // header-group tag so a click on either fires a drag.
        var headerFill = Windows.UI.Color.FromArgb(0xB0, color.R, color.G, color.B);
        var headerStrip = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Height              = FrameHeaderHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Top,
            Margin              = new Thickness(FrameCornerHandleSize, 0, FrameCornerHandleSize, 0),
            Fill                = new SolidColorBrush(headerFill),
            RadiusX             = 3,
            RadiusY             = 3,
            Tag                 = "header",
        };
        grid.Children.Add(headerStrip);

        // The title used to
        // render in the same color as the frame border at FontSize=11 (the
        // border color IS the foreground brush, so it visually melted into
        // the border). Majo flagged it explicitly. Bumped to 14 + a bright
        // off-white foreground (CoalPaperBrush, or a pre-resolved fallback)
        // so the title pops over the tinted header strip regardless of the
        // user-chosen frame color.
        var titleBlock = new TextBlock
        {
            Text                 = vm.Label,
            // Centre-align the title vertically inside the header strip —
            // (FrameHeaderHeight - 16) / 2 gives a tight inset for a 14pt
            // face. Horizontal margin matches the header strip's corner-
            // reserved area so the text aligns with the strip's left edge.
            Margin               = new Thickness(12, (FrameHeaderHeight - 16) / 2.0, 12, 0),
            VerticalAlignment    = VerticalAlignment.Top,
            FontSize             = 14,
            FontWeight           = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing     = 40,
            Foreground           = ResolveFrameTitleBrush(),
            TextTrimming         = TextTrimming.CharacterEllipsis,
            // Tag the label so a host-level double-tap can
            // identify a "clicked the label" gesture and route to the rename
            // flyout. The pointer-pressed handler also treats the label as
            // part of the header so a click on the title text starts a drag.
            Tag                  = "label",
        };
        try
        {
            // "SansFont" is an <x:String> resource — a direct
            // cast to FontFamily always throws InvalidCastException (silently
            // swallowed here, so the title never actually got SansFont). Build
            // the FontFamily from the string the way the XAML converter does.
            if (Application.Current.Resources["SansFont"] is string sansFamily)
                titleBlock.FontFamily = new FontFamily(sansFamily);
        }
        catch { /* leave default if the resource isn't loaded yet */ }

        grid.Children.Add(titleBlock);

        // Edge strips — 6 px thick, span the full edge minus the corner boxes.
        // Background is null/transparent so they don't visibly tint the frame;
        // hit-testing still works because IsHitTestVisible defaults to true.
        // Margins reserve the corner boxes' area so the corners aren't shadowed.
        const double e = FrameEdgeHandleThickness;
        const double c = FrameCornerHandleSize;

        grid.Children.Add(new Rectangle
        {
            Width               = e,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Stretch,
            Margin              = new Thickness(0, c, 0, c),
            Fill                = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Tag                 = "resize:left",
        });
        grid.Children.Add(new Rectangle
        {
            Width               = e,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Stretch,
            Margin              = new Thickness(0, c, 0, c),
            Fill                = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Tag                 = "resize:right",
        });
        grid.Children.Add(new Rectangle
        {
            Height              = e,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Top,
            Margin              = new Thickness(c, 0, c, 0),
            Fill                = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Tag                 = "resize:top",
        });
        grid.Children.Add(new Rectangle
        {
            Height              = e,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Bottom,
            Margin              = new Thickness(c, 0, c, 0),
            Fill                = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Tag                 = "resize:bottom",
        });

        // Corner boxes — TL / TR / BL transparent (purely hit-test); BR keeps
        // the original visible 0.6-opacity color swatch so the historical
        // "this frame is resizable" affordance still reads at a glance.
        grid.Children.Add(new Rectangle
        {
            Width               = c,
            Height              = c,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Top,
            Fill                = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Tag                 = "resize:topleft",
        });
        grid.Children.Add(new Rectangle
        {
            Width               = c,
            Height              = c,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Top,
            Fill                = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Tag                 = "resize:topright",
        });
        grid.Children.Add(new Rectangle
        {
            Width               = c,
            Height              = c,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Bottom,
            Fill                = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Tag                 = "resize:bottomleft",
        });
        grid.Children.Add(new Rectangle
        {
            Width               = c,
            Height              = c,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Bottom,
            Margin              = new Thickness(0, 0, 2, 2),
            Fill                = new SolidColorBrush(color),
            Opacity             = 0.6,
            Tag                 = "resize:bottomright",
        });

        border.Child = grid;
        return border;
    }

    /// <summary>
    /// Build a faint forward-diagonal hatch overlay sized to a
    /// placeholder frame's interior. Mirrors the WinForms HatchBrush
    /// (ForwardDiagonal) the baseline painted behind placeholder frames so the
    /// "skipped on export" state reads at a glance. Hit-test-invisible and
    /// clipped to the frame rect so it never spills past the body or eats a
    /// pointer gesture. Lines are spaced <c>step</c> px apart; a moderate step
    /// keeps the figure count bounded even on a large frame.
    /// </summary>
    private static FrameworkElement BuildPlaceholderHatch(double width, double height, Windows.UI.Color color)
    {
        const double step = 12.0;
        var geo = new PathGeometry();

        double w = width  > 0 ? width  : 1;
        double h = height > 0 ? height : 1;

        // Forward-diagonal lines (top-left → bottom-right slope). Sweep the line
        // intercept from -h..w so every diagonal that crosses the rect is drawn;
        // each segment is clamped to the rect edges.
        for (double x0 = -h; x0 < w; x0 += step)
        {
            // Line: from (x0, 0) heading down-right at 45°. Compute its entry/
            // exit against the [0,w]×[0,h] box.
            double sx = x0, sy = 0;
            double ex = x0 + h, ey = h;

            // Clamp the start to the left edge when it begins off the left side.
            if (sx < 0) { sy = -sx; sx = 0; }
            // Clamp the end to the right edge when it runs off the right side.
            if (ex > w) { ey = h - (ex - w); ex = w; }
            if (sy > h || ey < 0) continue;

            var fig = new PathFigure { StartPoint = new Windows.Foundation.Point(sx, sy) };
            fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(ex, ey) });
            geo.Figures.Add(fig);
        }

        var path = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data             = geo,
            Stroke           = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, color.R, color.G, color.B)),
            StrokeThickness  = 1.0,
            IsHitTestVisible = false,
            // Clip so a diagonal that would otherwise overshoot the rounded
            // corners stays inside the frame body.
            Clip             = new Microsoft.UI.Xaml.Media.RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, w, h),
            },
        };
        return path;
    }

    private static Windows.UI.Color ParseHex(string hex)
    {
        // Accepts "#RRGGBB" and "#AARRGGBB" — the FrameViewModel emits the
        // first for the border and the second for the fill (with hardcoded
        // alpha "40" prefix). Any parse failure falls back to opaque white
        // so the frame at least renders.
        if (string.IsNullOrEmpty(hex) || hex[0] != '#') return Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        try
        {
            string s = hex.Substring(1);
            byte a = 0xFF, r, g, b;
            if (s.Length == 8)
            {
                a = Convert.ToByte(s.Substring(0, 2), 16);
                r = Convert.ToByte(s.Substring(2, 2), 16);
                g = Convert.ToByte(s.Substring(4, 2), 16);
                b = Convert.ToByte(s.Substring(6, 2), 16);
            }
            else if (s.Length == 6)
            {
                r = Convert.ToByte(s.Substring(0, 2), 16);
                g = Convert.ToByte(s.Substring(2, 2), 16);
                b = Convert.ToByte(s.Substring(4, 2), 16);
            }
            else return Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
            return Windows.UI.Color.FromArgb(a, r, g, b);
        }
        catch { return Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF); }
    }

    private void UpdateEmptyHint()
    {
        bool empty = _vm is null || _vm.Nodes.Count == 0;
        // 0.10.0 — when no graph is loaded AND the canvas
        // is empty, swap to the Welcome card (New / Open / Recent action
        // buttons). The bare "LOGIC CANVAS / right-click to spawn" hint
        // still fires for the in-memory empty-graph case (e.g. just hit
        // File → New) so the user gets the lightweight hint after a
        // deliberate New rather than the heavy Welcome card again.
        bool noFile = empty && (_vm?.LoadedFilePath is null);

        // The "blank canvas from New" suppression only applies to
        // the fresh-empty-no-file state. The moment the canvas gains content or
        // a file (!noFile), the suppression is stale: clear it so a later return
        // to empty (e.g. File → Close-X, which is gated on HasOpenDocument and
        // so can only fire once content existed) lands on the Welcome card as
        // documented, not on the lightweight hint.
        if (!noFile) _suppressWelcomeForNewGraph = false;

        // When the user deliberately started a blank graph via "New", show the
        // lightweight EmptyHint instead of re-asserting the full Welcome card.
        bool showWelcome = noFile && !_suppressWelcomeForNewGraph;
        EmptyHint.Visibility   = empty && !showWelcome ? Visibility.Visible : Visibility.Collapsed;
        WelcomeCard.Visibility = showWelcome           ? Visibility.Visible : Visibility.Collapsed;

        // Refresh the Samples picker every time the
        // welcome card becomes visible so a between-session file removal /
        // restore reflects on next surface. Cheap (≤10 stat calls); kept
        // gated to showWelcome so we don't pay for it on every node mutation.
        if (showWelcome) PopulateWelcomeSamples();
    }

    // ─── Coordinate transforms ───────────────────────────────────────────

    /// <summary>Convert a Host-relative point into canvas-space (undoing pan + zoom).</summary>
    private Point HostToCanvas(Point hostPoint)
    {
        if (_vm is null) return hostPoint;
        double zoom = Math.Max(_vm.Zoom, 0.0001);
        return new Point(
            (hostPoint.X - _vm.PanX) / zoom,
            (hostPoint.Y - _vm.PanY) / zoom);
    }

    private void ApplyViewTransform()
    {
        if (_vm is null) return;
        MarkSceneDirty();   // the GPU draw bakes this pose into every frame
        ViewTransform.ScaleX     = _vm.Zoom;
        ViewTransform.ScaleY     = _vm.Zoom;
        ViewTransform.TranslateX = _vm.PanX;
        ViewTransform.TranslateY = _vm.PanY;
        // Only MARK the dot grid dirty here; the actual
        // O(dots) rebuild is deferred to OnRenderingTick's settle drain so a
        // sustained pan that keeps leaving the built region never rebuilds
        // mid-gesture. The grid rides this transform within the built field.
        MarkDotGridDirty();
        // Pan/zoom moved the viewport — ask for a re-cull on settle.
        if (_enableNodeVirtualization) { _cullDirty = true; _cullSettleFrames = 0; } // viewport moved → restart the cull settle-debounce
    }

    /// <summary>
    /// Public ApplyViewTransform proxy invoked by
    /// <see cref="MiniMapOverlay"/> after its drag-pan gesture writes
    /// PanX/Y to the shared VM. The minimap can't reach the private
    /// ApplyViewTransform directly; this single-line forward keeps the
    /// transform write co-located with the per-frame pan/zoom flush logic
    /// while still letting the minimap nudge the surface immediately
    /// (drag responsiveness > 1-frame deferral).
    /// </summary>
    internal void ApplyViewTransformForMinimap() => ApplyViewTransform();

    /// <summary>
    /// Centre the host's pan/zoom on the canvas-space point
    /// <paramref name="canvasPoint"/>. Used by <see cref="MiniMapOverlay"/>'s
    /// tap-to-jump gesture; keeps the current zoom and writes PanX/Y so
    /// the host viewport's centre lands on the requested canvas coord.
    /// No-op when the canvas hasn't been measured yet.
    /// </summary>
    public void CenterViewportOn(double canvasX, double canvasY)
    {
        if (_vm is null) return;
        double viewW = HostRoot.ActualWidth;
        double viewH = HostRoot.ActualHeight;
        if (viewW <= 0 || viewH <= 0) return;
        _vm.PanX = viewW / 2.0 - canvasX * _vm.Zoom;
        _vm.PanY = viewH / 2.0 - canvasY * _vm.Zoom;
        ApplyViewTransform();
    }

    /// <summary>
    /// Flip the minimap overlay's Visibility and persist the
    /// new state into <see cref="Phoenix.Controls.Shared.Models.AppConfig.ArchitectMinimapVisible"/>.
    /// Both the chrome menu's "View → Minimap" toggle and the in-overlay
    /// × glyph route through here so the two surfaces stay in sync.
    /// </summary>
    public void SetMinimapVisible(bool visible)
    {
        if (MiniMap is null) return;
        MiniMap.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            Phoenix.Controls.Shared.Services.ConfigManager.Current.ArchitectMinimapVisible = visible;
            Phoenix.Controls.Shared.Services.ConfigManager.Save(
                Phoenix.Controls.Shared.Core.Paths.AppConfigJson);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Architect.LogicCanvasView", "SetMinimapVisible persistence", ex);
        }
        MinimapVisibilityChanged?.Invoke(this, visible);
    }

    /// <summary>
    /// True when the minimap overlay is currently visible.
    /// Chrome surfaces poll this on construction to sync their toggle
    /// glyph (e.g. ToggleMenuFlyoutItem.IsChecked).
    /// </summary>
    public bool IsMinimapVisible => MiniMap?.Visibility == Visibility.Visible;

    /// <summary>
    /// Raised whenever the minimap's visibility flips (chrome
    /// menu toggle, in-overlay × glyph, programmatic restore from
    /// AppConfig). Chrome menu items subscribe so their IsChecked stays
    /// in sync when the user dismisses via the × glyph.
    /// </summary>
    public event EventHandler<bool>? MinimapVisibilityChanged;

    /// <summary>
    /// Sync the minimap overlay's Visibility with whatever the
    /// persisted state says. Called from the chrome shell's layout-restore
    /// path so first-launch + restart paths land at the same surface.
    /// </summary>
    public void ApplyMinimapVisibilityFromConfig()
    {
        if (MiniMap is null) return;
        bool visible = Phoenix.Controls.Shared.Services.ConfigManager.Current.ArchitectMinimapVisible;
        MiniMap.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Minimap CenterRequested handler. Centres the canvas
    /// viewport on the requested canvas-space point.
    /// </summary>
    private void OnMinimapCenterRequested(object? sender, Point canvasPoint)
    {
        CenterViewportOn(canvasPoint.X, canvasPoint.Y);
    }

    /// <summary>
    /// Minimap HideRequested handler. Hides the overlay and
    /// persists the visibility flip into AppConfig so the next launch
    /// honours the user's choice.
    /// </summary>
    private void OnMinimapHideRequested(object? sender, EventArgs e)
    {
        SetMinimapVisible(false);
    }

    // ─── Hit testing ─────────────────────────────────────────────────────

    /// <summary>
    /// True when <paramref name="source"/> sits inside a <see cref="Button"/>
    /// (or any descendant of one) in the visual tree. Used by the wire-drop
    /// branch in <c>OnHostPointerPressed</c> to skip the drag when the
    /// press actually lands on an inline button child of a tagged row —
    /// e.g. the DB-picker chevron on input rows of DB.* nodes, whose row
    /// Grid carries <c>Tag={Binding}</c> so HitTagFrom would otherwise
    /// resolve the chevron click as a socket-pin press.
    /// </summary>
    private static bool IsInsideButton(object? source)
    {
        DependencyObject? n = source as DependencyObject;
        while (n is not null)
        {
            if (n is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase) return true;
            n = VisualTreeHelper.GetParent(n);
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="source"/> sits inside an inline EDIT affordance
    /// on a node body — a value pill / socket-label / middle-attr element tagged
    /// with <see cref="CanvasInteraction.IsEditAffordanceProperty"/>, an active
    /// inline-edit TextBox, or a ButtonBase / ComboBox / Selector (the DB-picker
    /// chevron and any future dropdown on a node). Used by
    /// <c>OnHostPointerPressed</c> to let the element's own Tapped / DoubleTapped
    /// / Click gesture run instead of swallowing the press into a node-drag /
    /// wire-drop pointer capture — the capture is what killed every in-body
    /// interaction (pill edit, DB dropdown, rename, popups). The socket PIN
    /// hit-targets (Ellipses) and the node header / title are deliberately NOT
    /// affordances, so wire-drag and node-drag still start on a press there.
    /// </summary>
    private static bool IsInsideInteractiveChild(object? source)
    {
        DependencyObject? n = source as DependencyObject;
        while (n is not null)
        {
            if (n is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase
                  or Microsoft.UI.Xaml.Controls.TextBox
                  or Microsoft.UI.Xaml.Controls.ComboBox
                  or Microsoft.UI.Xaml.Controls.Primitives.Selector)
                return true;
            if (n is FrameworkElement fe && CanvasInteraction.GetIsEditAffordance(fe))
                return true;
            // Stop at the node container — a press on the bare node body / header
            // (outside any affordance) must still start a node drag.
            if (n is NodeView) break;
            n = VisualTreeHelper.GetParent(n);
        }
        return false;
    }

    /// <summary>
    /// Robust "the press landed on a node-body edit affordance" test used by the
    /// pointer-capture guard in <c>OnHostPointerPressed</c>.
    /// <see cref="IsInsideInteractiveChild"/> (walking UP from
    /// <c>e.OriginalSource</c>) is the fast path, but WinUI can report a press
    /// inside a templated <c>ItemsControl</c> row with <c>OriginalSource</c> set
    /// to the item CONTAINER (a ContentPresenter) rather than the deep pill /
    /// label element — and the <c>IsEditAffordance</c> markers are that
    /// container's DESCENDANTS, so the upward walk never reaches them, the guard
    /// returns false, and the canvas captures the press into a node-drag
    /// (Majo: "click the pill, it only selects the node" — the failure a dozen
    /// guard-level fixes never closed because the code is correct on paper). The
    /// geometry fallback hit-tests the ACTUAL cursor point via
    /// <see cref="VisualTreeHelper.FindElementsInHostCoordinates(Windows.Foundation.Point, UIElement)"/>
    /// and asks whether any element genuinely under the cursor is (or sits
    /// inside) an affordance — independent of how OriginalSource resolved. It
    /// runs ONLY when the fast path already failed, so working presses are
    /// untouched, and is wrapped so a hit-test failure degrades to today's
    /// behaviour (no regression).
    /// </summary>
    private bool IsPressOnEditAffordance(object? originalSource, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (IsInsideInteractiveChild(originalSource)) return true;
        try
        {
            var hostPoint = e.GetCurrentPoint(null).Position;
            foreach (var el in VisualTreeHelper.FindElementsInHostCoordinates(hostPoint, this))
            {
                // Honour z-order. FindElementsInHostCoordinates
                // returns the hit stack topmost-first. A socket PIN hit-target
                // (marked CanvasInteraction.IsSocketPin) on top means this press
                // STARTS A WIRE-DRAG, not an inline edit — so bail BEFORE the
                // affordance test below. Match on the precise IsSocketPin marker,
                // NOT Tag: the socket ROW grid AND the value pill also carry
                // Tag=SocketViewModel, so keying on Tag would wrongly bail the veto
                // for a pill / label press whenever its container resolved topmost.
                // Without this, the 24×24 pin hit-box (Margin=-5) overhangs the
                // socket-row label Grid (canvas:CanvasInteraction.IsEditAffordance="True"),
                // so a press squarely on a pin matched the affordance sitting behind it,
                // the early-return in OnHostPointerPressed fired, and StartWirePreview
                // never ran — the in-flight wire "pipe" never appeared (regression from
                // the geometry-fallback guard added for node-body clicks). A genuine pill
                // press still wins: the pill renders ABOVE the pin at its own location,
                // so the affordance is the topmost element there and the loop returns true.
                if (el is FrameworkElement pinFe && CanvasInteraction.GetIsSocketPin(pinFe)) return false;
                if (IsInsideInteractiveChild(el)) return true;
            }
        }
        catch { /* best-effort geometry probe — fall back to the OriginalSource walk */ }
        return false;
    }

    /// <summary>
    /// Resolve a genuine socket-PIN press. Hit-tests the actual
    /// cursor point and returns the <see cref="SocketViewModel"/> whose pin
    /// hit-target (<see cref="CanvasInteraction.IsSocketPinProperty"/>, set in
    /// <c>NodeView.AddPinElement</c>) is the TOPMOST hit element — i.e. the press
    /// is squarely on a pin. Returns null when an inline-edit affordance / Button
    /// is topmost BEFORE any pin (so label rename, pill edit and the DB-picker
    /// chevron keep their own gestures), or when no pin is under the cursor.
    /// <para>
    /// Precise by design: the socket ROW grid shares the SocketViewModel Tag, so
    /// matching on Tag alone (the prior z-order bail) couldn't disambiguate the
    /// pin from the row. The IsSocketPin marker does. This is AUTHORITATIVE over
    /// <c>e.OriginalSource</c>, which WinUI can report as the row container BEHIND
    /// the pin (the templated-ItemsControl quirk) — that mis-resolution let the
    /// edit-affordance fast-path veto (and the wire-drop branch's own
    /// IsInsideInteractiveChild gate) eat a real pin press, so the in-flight
    /// "pipe" never appeared on those sockets ("no pipe on some sockets").
    /// </para>
    /// </summary>
    private SocketViewModel? ResolvePinUnderCursor(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        try
        {
            var hostPoint = e.GetCurrentPoint(null).Position;
            foreach (var el in VisualTreeHelper.FindElementsInHostCoordinates(hostPoint, this))
            {
                if (el is FrameworkElement fe && CanvasInteraction.GetIsSocketPin(fe)
                    && fe.Tag is SocketViewModel pinSock)
                    return pinSock;
                // A Button / inline-edit affordance topmost BEFORE any pin owns
                // this press (DB-picker chevron, pill edit, label rename) — don't
                // hijack it for a wire-drag.
                if (IsInsideButton(el) || IsInsideInteractiveChild(el))
                    return null;
            }
        }
        catch { /* best-effort geometry probe — fall back to the OriginalSource path */ }
        return null;
    }

    // Temporary lifecycle trace for the dynamic-bubble (placeholder)
    // system — drop resolution → activation → call-site sync. Gated by a const so it
    // is a one-line flip to silence/remove once the residual "+ not visible" / no-sync
    // cases are confirmed from a single repro run. Logs to the Hub System Log + rolling
    // log, the same channel the earlier diagnostic probes used.
    internal static bool BubbleDiagEnabled = true;
    internal static void BubbleDiag(string msg)
    {
        if (!BubbleDiagEnabled) return;
        GlobalLogger.Log(" " + msg, "Architect.Canvas", LogLevel.System);
    }

    // Pin hit radius for the MODEL-side wire-drop fallback below.
    private const double WireDropModelHitRadius = 14.0;

    // Slack added on top of a placeholder's measured label width to get the
    // PRESS reach from its pin anchor (canvas units): label x-pad from the node
    // edge (14 input / 16 output, DrawNodeBody) + the pin's overhang past the
    // edge (±7) + a couple px of font-estimate drift. The reach itself is
    // computed per socket from the SAME NodeGeometry.EstimateTextWidth(label,
    // 12.0) the renderer right/left-aligns the label with, so the pressable
    // region always covers exactly the drawn pin + label chrome — no guessed
    // constant that a longer label or measure drift can outgrow. The DROP band
    // stays full-row-generous (see ResolveSocketAtCanvasPoint), but at press
    // time the drawn chrome is the only visible affordance — a full-row press
    // band let a press anywhere across the node body at a slot row's height
    // silently start a wire-drag from the placeholder, eating clicks meant for
    // node select / drag (and, worse, would turn stray body clicks into slot
    // activations now that a click on the slot adds a bubble).
    private const double PlaceholderPressSlack = 26.0;

    // Model-side socket hit-test — the off-screen counterpart to
    // ResolvePinUnderCursor. When node virtualization has culled the target node
    // out of the visual tree (it scrolled into view via edge-pan during a wire-drag,
    // so its pin Ellipse isn't realized), the visual probe misses. This resolves the
    // socket purely from the model: a socket's canvas anchor is node.X/Y +
    // SocketViewModel.Anchor() — the SAME math LinkViewModel.RecomputePath uses every
    // frame without a realized view. Returns the nearest socket within radius, else
    // null. O(N·sockets), but only fires on a visual MISS (the mounted fast-path runs
    // first) and only once per drop / per coalesced hover-frame, with a cheap per-node
    // bounds reject up front.
    // fullPlaceholderRowBand: true (default) keeps the generous full-row
    // second pass for DROP / hover resolution; false narrows the placeholder
    // band to the drawn pin + label chrome (PlaceholderPressReach) for PRESS
    // resolution, so pressing the node body at a slot row's height selects /
    // drags the node instead of hijacking the press into a placeholder
    // wire-drag (the "Event nodes feel dead / possessed" 1.0.14–1.0.16
    // regression — every body press in the two slot-row stripes became a
    // phantom "+ return" drag that ended in a silent self-loop rejection).
    private SocketViewModel? ResolveSocketAtCanvasPoint(
        Point canvasPoint, double radius, bool fullPlaceholderRowBand = true)
    {
        if (_vm is null) return null;
        double best2 = radius * radius;
        SocketViewModel? hit = null;
        foreach (var node in _vm.Nodes)
        {
            if (canvasPoint.X < node.X - radius || canvasPoint.X > node.X + node.Width  + radius
                || canvasPoint.Y < node.Y - radius || canvasPoint.Y > node.Y + node.Height + radius)
                continue;
            foreach (var sock in node.Inputs)
            {
                var (ax, ay) = sock.Anchor();
                double dx = canvasPoint.X - (node.X + ax), dy = canvasPoint.Y - (node.Y + ay);
                double d2 = dx * dx + dy * dy;
                if (d2 <= best2) { best2 = d2; hit = sock; }
            }
            foreach (var sock in node.Outputs)
            {
                var (ax, ay) = sock.Anchor();
                double dx = canvasPoint.X - (node.X + ax), dy = canvasPoint.Y - (node.Y + ay);
                double d2 = dx * dx + dy * dy;
                if (d2 <= best2) { best2 = d2; hit = sock; }
            }
        }
        if (hit is not null) return hit;

        // Managed dynamic placeholders ("+ variable" / "+ input"
        // / "+ output" / "+ return") render their label across the WHOLE row, but their
        // pin is a tiny edge target. On the Win2D canvas there is no Ellipse hit-target,
        // so a natural drop aimed at the "+ variable" LABEL lands >radius from the edge
        // pin, misses the nearest-pin pass above, and falls through to the body-drop
        // fallback (FindFirstCompatibleInput) — which historically skipped placeholders,
        // so the dynamic bubble never grew. Restore the retained canvas's generous
        // target: accept a drop anywhere along a placeholder ROW as a SECOND pass — only
        // when no real pin was hit, so this can never steal a drop meant for a real pin.
        double bandHalf = NodeGeometry.SocketRowHeight / 2.0;
        foreach (var node in _vm.Nodes)
        {
            if (canvasPoint.X < node.X - radius || canvasPoint.X > node.X + node.Width + radius
                || canvasPoint.Y < node.Y - radius || canvasPoint.Y > node.Y + node.Height + radius)
                continue;
            // Pick the CLOSEST managed-placeholder row to the drop across BOTH pools,
            // not the first Input in declaration order. The row bands are
            // SocketRowHeight tall and adjacent placeholder rows sit one row apart, so
            // their bands meet at the exact Y boundary between them. An Inputs-first
            // "?? Outputs" resolve broke the tie by pool-check order, so a pixel-exact
            // drop on the boundary between an Input placeholder ("+ return" on an
            // Event.Executor) and the adjacent Output placeholder ("+ variable") wired
            // the wrong channel. Nearest-anchor is unambiguous and only differs from
            // the old behaviour within ~1px of the boundary.
            var rowHit = NearestPlaceholderRow(node, canvasPoint, bandHalf,
                pressNarrowed: !fullPlaceholderRowBand);
            if (rowHit is not null) return rowHit;
        }
        return null;

        static SocketViewModel? NearestPlaceholderRow(
            NodeViewModel node, Point pt, double bandHalf, bool pressNarrowed)
        {
            SocketViewModel? best = null;
            double bestDy = double.MaxValue;
            void Consider(System.Collections.Generic.IEnumerable<SocketViewModel> pool)
            {
                foreach (var sock in pool)
                {
                    if (!sock.IsDynamicPlaceholder) continue;
                    var (ax, ay) = sock.Anchor();
                    if (pressNarrowed)
                    {
                        // Press must land on the drawn pin + label chrome —
                        // measured with the renderer's own label estimate so
                        // the hit-target tracks the visuals exactly.
                        double reach = NodeGeometry.EstimateTextWidth(sock.Label, 12.0)
                                     + PlaceholderPressSlack;
                        if (System.Math.Abs(pt.X - (node.X + ax)) > reach) continue;
                    }
                    double dy = System.Math.Abs(pt.Y - (node.Y + ay));
                    if (dy <= bandHalf && dy < bestDy) { bestDy = dy; best = sock; }
                }
            }
            Consider(node.Inputs);
            Consider(node.Outputs);
            return best;
        }
    }

    /// <summary>
    /// Walk the visual tree from <paramref name="source"/> upward looking for
    /// the first FrameworkElement whose Tag is a SocketViewModel / NodeViewModel /
    /// LinkViewModel. Returns null on a hit against bare canvas / chrome.
    /// </summary>
    private static object? HitTagFrom(object? source)
    {
        if (source is not DependencyObject node) return null;
        while (node is not null)
        {
            if (node is FrameworkElement fe && fe.Tag is SocketViewModel or NodeViewModel or LinkViewModel or FrameViewModel)
                return fe.Tag;
            // Also accept NodeView as the Container — its DataContext is the NodeViewModel.
            if (node is NodeView nv && nv.DataContext is NodeViewModel nvm) return nvm;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    /// <summary>
    /// Map the pointer's <paramref name="source"/> to a
    /// <see cref="FrameResizeEdge"/> by walking the visual tree looking for a
    /// FrameworkElement whose Tag is one of the `resize:<edge>` strings emitted
    /// by <see cref="BuildFrameElement"/>. Returns <see cref="FrameResizeEdge.None"/>
    /// on a body / label / non-frame hit so the pointer code can branch
    /// cleanly between body-drag and edge-drag.
    /// </summary>
    private static FrameResizeEdge ResolveFrameResizeEdge(object? source)
    {
        if (source is not DependencyObject n) return FrameResizeEdge.None;
        while (n is not null)
        {
            if (n is FrameworkElement fe && fe.Tag is string s && s.StartsWith("resize", System.StringComparison.Ordinal))
            {
                return s switch
                {
                    "resize:left"        => FrameResizeEdge.Left,
                    "resize:right"       => FrameResizeEdge.Right,
                    "resize:top"         => FrameResizeEdge.Top,
                    "resize:bottom"      => FrameResizeEdge.Bottom,
                    "resize:topleft"     => FrameResizeEdge.TopLeft,
                    "resize:topright"    => FrameResizeEdge.TopRight,
                    "resize:bottomleft"  => FrameResizeEdge.BottomLeft,
                    "resize:bottomright" => FrameResizeEdge.BottomRight,
                    // Plain "resize" stayed in earlier callers — treat as
                    // the historical bottom-right corner so any external test
                    // / mock relying on the bare tag still resizes correctly.
                    "resize"             => FrameResizeEdge.BottomRight,
                    _                    => FrameResizeEdge.None,
                };
            }
            n = VisualTreeHelper.GetParent(n);
        }
        return FrameResizeEdge.None;
    }

    // Frame edge-resize
    // discoverability. The eight resize hit-zones around a frame are invisible
    // (only the bottom-right corner carries a visible 0.6-opacity swatch);
    // without a hover cursor change a user can't tell where the edges live.
    // Track the last cursor shape we set so we don't churn ProtectedCursor on
    // every PointerMoved tick (Create() does a native CreateCursor call).
    private InputSystemCursorShape? _frameEdgeCursorShape;

    /// <summary>
    /// Repaint the host cursor based
    /// on whether <paramref name="source"/> resolves to a frame edge / corner
    /// resize handle. Called from <c>OnHostPointerMoved</c> while in
    /// <c>DragState.Idle</c> (active drags own the cursor); also called from
    /// <c>OnHostPointerExited</c> with a null source to clear the override
    /// when the pointer leaves the canvas.
    /// </summary>
    /// <remarks>
    /// N/S/E/W edges use the bidirectional resize cursors; the four corners
    /// pick the matching diagonal. Off any handle the cursor is reset to the
    /// default arrow by clearing <see cref="UIElement.ProtectedCursor"/>.
    /// Tracks the last applied shape so a quiet 120 Hz pointer burst inside
    /// a single zone collapses to zero <c>InputSystemCursor.Create</c> calls.
    /// Wraps the cursor write in try/catch — the cursor API throws during
    /// shutdown / pre-realised trees, matching the
    /// <c>CursorThumb</c> precedent.
    /// </remarks>
    private void UpdateFrameEdgeHoverCursor(object? source)
        => ApplyFrameEdgeCursor(ResolveFrameResizeEdge(source));

    /// <summary>
    /// Win2D counterpart to <see cref="UpdateFrameEdgeHoverCursor"/>. The GPU
    /// canvas has no tagged Border children, so the hovered frame edge is
    /// resolved from the canvas point (<see cref="ResolveModelHit"/> +
    /// <c>FrameEdgeAt</c>) rather than by walking the visual tree.
    /// </summary>
    private void UpdateFrameEdgeHoverCursorWin2D(Windows.Foundation.Point canvasPoint)
    {
        var edge = ResolveModelHit(canvasPoint) is FrameViewModel f
            ? FrameEdgeAt(f, canvasPoint.X, canvasPoint.Y, FrameResizeBand())
            : FrameResizeEdge.None;
        ApplyFrameEdgeCursor(edge);
    }

    /// <summary>
    /// Map a resolved <see cref="FrameResizeEdge"/> to the matching host cursor
    /// shape, deduped against the last applied shape so a 120 Hz pointer burst
    /// inside one zone collapses to zero native cursor creations. Shared by the
    /// retained (<see cref="UpdateFrameEdgeHoverCursor"/>) and Win2D
    /// (<see cref="UpdateFrameEdgeHoverCursorWin2D"/>) hover paths.
    /// </summary>
    private void ApplyFrameEdgeCursor(FrameResizeEdge edge)
    {
        InputSystemCursorShape? wanted = edge switch
        {
            FrameResizeEdge.Top or FrameResizeEdge.Bottom
                => InputSystemCursorShape.SizeNorthSouth,
            FrameResizeEdge.Left or FrameResizeEdge.Right
                => InputSystemCursorShape.SizeWestEast,
            FrameResizeEdge.TopLeft or FrameResizeEdge.BottomRight
                => InputSystemCursorShape.SizeNorthwestSoutheast,
            FrameResizeEdge.TopRight or FrameResizeEdge.BottomLeft
                => InputSystemCursorShape.SizeNortheastSouthwest,
            _ => (InputSystemCursorShape?)null,
        };

        if (wanted == _frameEdgeCursorShape) return;
        _frameEdgeCursorShape = wanted;
        try
        {
            // ProtectedCursor is protected on UIElement — it's only accessible
            // through `this` (the LogicCanvasView), not via HostRoot. The
            // UserControl itself fills the canvas region so setting it here
            // is functionally equivalent to setting it on HostRoot.
            ProtectedCursor = wanted is { } shape
                ? InputSystemCursor.Create(shape)
                : null;
        }
        catch
        {
            // Cursor APIs throw on shutdown / pre-realised trees; swallow so a
            // canvas tear-down mid-hover doesn't crash the pillar. Default
            // arrow stays in effect.
        }
    }

    /// <summary>
    /// True when <paramref name="source"/> sits inside a
    /// frame label TextBlock (Tag="label"). Used by the double-tap handler to
    /// route a label hit into <see cref="ShowFrameRenameFlyout"/>, mirroring
    /// the rename idiom socket / pre-existing inline rename surfaces use.
    /// Walks the visual tree because the hit element under the cursor is
    /// usually the inner TextBlock layout, not the tagged element itself.
    /// </summary>
    private static bool IsFrameLabelHit(object? source)
    {
        if (source is not DependencyObject n) return false;
        while (n is not null)
        {
            if (n is FrameworkElement fe && (fe.Tag as string) == "label") return true;
            n = VisualTreeHelper.GetParent(n);
        }
        return false;
    }

    /// <summary>
    /// True when the pointer source
    /// resolves to either the frame's header strip (Tag="header") or its
    /// label TextBlock (Tag="label"). OnHostPointerPressed gates the
    /// FrameMove drag origin on this so a click on the body just selects;
    /// only the header / label starts a translation gesture. Resize handles
    /// (Tag="resize:*") are excluded because <see cref="ResolveFrameResizeEdge"/>
    /// already routes those to FrameResize.
    /// </summary>
    private static bool IsFrameHeaderHit(object? source)
    {
        if (source is not DependencyObject n) return false;
        while (n is not null)
        {
            if (n is FrameworkElement fe && fe.Tag is string s
                && (s == "header" || s == "label"))
                return true;
            n = VisualTreeHelper.GetParent(n);
        }
        return false;
    }

    // ─── Public mutators (used by spawn palette, context menus) ──────────

    /// <summary>
    /// Add <paramref name="node"/> to the active graph at canvas-space
    /// (<paramref name="x"/>, <paramref name="y"/>) and select it. Used by
    /// the spawn palette + paste handlers.
    /// </summary>
    /// <summary>
    /// Flash a node briefly to indicate it just executed (DEBUG_NODE_EXEC bus event).
    /// Auto-clears after ~800ms via the dispatcher queue — matches the full
    /// 160 + 440 + 200 ms storyboard ramp in <see cref="NodeView.xaml.cs"/>
    /// so the IsExecutingFlash flag stays true until the visible animation
    /// has actually completed. Pre-fix the delay was 400ms and the back half
    /// of the ramp painted against a brush whose Opacity timeline had
    /// already been torn down, so the flash visually truncated.
    /// </summary>
    // Pending flash node-ids, drained once per displayed
    // frame in OnRenderingTick. A tight DEBUG_NODE_EXEC loop firing N times for
    // the same node within one frame collapses to a single TriggerFlash (the
    // HashSet dedupes) instead of allocating a fresh Storyboard + brush per
    // event. Different nodes still each flash.
    private readonly System.Collections.Generic.HashSet<string> _pendingFlashNodeIds = new();

    public void FlashNode(string nodeId)
    {
        if (_vm is null || string.IsNullOrEmpty(nodeId)) return;
        // Coalesce: just record the request; the render tick drains it. Multiple
        // calls for the same id within a frame dedupe to one flash.
        _pendingFlashNodeIds.Add(nodeId);
        MarkSceneDirty();   // paint the pulse on the same tick the drain runs
    }

    // Flash pulse window — matches the ~420ms FlashOverlay storyboard in
    // NodeView (a touch longer so the storyboard completes before the flag
    // clears).
    private const int FlashHoldMs = 440;

    /// <summary>
    /// Drain the pending-flash set from OnRenderingTick.
    /// Snapshot + clear first so a re-entrant FlashNode during the drain queues
    /// for the NEXT tick. One TriggerFlash + one auto-clear per node per drained
    /// frame.
    /// </summary>
    // Shared flash auto-clear model. Pre-fix every drained flash enqueued
    // its own `async () => { await Task.Delay(FlashHoldMs); ... }` lambda — under
    // a busy debug session (rapid DEBUG_NODE_EXEC) that allocates an unbounded
    // number of Task + state-machine instances per second. Now a single
    // DispatcherTimer sweeps a dictionary of pending expirations at a fixed
    // cadence and clears IsExecutingFlash once a node's hold window elapses —
    // O(1) timer overhead regardless of flash rate. The recorded FlashTick lets
    // an overlapping re-flash extend the window without clearing an unrelated
    // in-flight pulse.
    private readonly System.Collections.Generic.Dictionary<NodeViewModel, (int Tick, long ExpiresAtMs)> _flashExpiries = new();
    private Microsoft.UI.Xaml.DispatcherTimer? _flashSweepTimer;
    private const int FlashSweepIntervalMs = 50;

    private void DrainPendingFlashes()
    {
        if (_pendingFlashNodeIds.Count == 0 || _vm is null) return;
        var ids = new string[_pendingFlashNodeIds.Count];
        _pendingFlashNodeIds.CopyTo(ids);
        _pendingFlashNodeIds.Clear();

        long nowMs = System.Environment.TickCount64;
        foreach (var nodeId in ids)
        {
            var n = _vm.FindNode(nodeId);
            if (n is null) continue;
            // TriggerFlash bumps a monotonic counter that always raises
            // PropertyChanged, so back-to-back flashes on the same node restart
            // the pulse instead of being eaten by SetField bool-equality.
            n.TriggerFlash();
            // Record (or extend) the auto-clear deadline. The sweep timer clears
            // IsExecutingFlash once now >= ExpiresAtMs AND the recorded tick
            // still matches (an overlapping re-flash bumps both).
            _flashExpiries[n] = (n.FlashTick, nowMs + FlashHoldMs);
        }

        EnsureFlashSweepTimer();
    }

    private void EnsureFlashSweepTimer()
    {
        if (_flashSweepTimer is not null)
        {
            if (!_flashSweepTimer.IsEnabled) _flashSweepTimer.Start();
            return;
        }
        _flashSweepTimer = new Microsoft.UI.Xaml.DispatcherTimer
        {
            Interval = System.TimeSpan.FromMilliseconds(FlashSweepIntervalMs),
        };
        _flashSweepTimer.Tick += OnFlashSweepTick;
        _flashSweepTimer.Start();
    }

    private void OnFlashSweepTick(object? sender, object e)
    {
        if (_flashExpiries.Count == 0)
        {
            // Idle — stop the timer so we don't burn a tick every 50ms on a
            // canvas with no active flashes. DrainPendingFlashes restarts it.
            _flashSweepTimer?.Stop();
            return;
        }

        long nowMs = System.Environment.TickCount64;
        System.Collections.Generic.List<NodeViewModel>? expired = null;
        foreach (var kvp in _flashExpiries)
        {
            if (nowMs < kvp.Value.ExpiresAtMs) continue;
            (expired ??= new System.Collections.Generic.List<NodeViewModel>()).Add(kvp.Key);
        }
        if (expired is null) return;

        foreach (var n in expired)
        {
            // Only clear if the recorded tick still matches — an overlapping
            // re-flash since recording bumped FlashTick and updated the entry,
            // so this stale expiry must not clear the fresh pulse.
            if (_flashExpiries.TryGetValue(n, out var entry) && n.FlashTick == entry.Tick)
                n.IsExecutingFlash = false;
            _flashExpiries.Remove(n);
        }
    }

    /// <summary>Add a comment frame at canvas-space (x,y) and select it.</summary>
    public FrameViewModel? AddFrame(double x, double y, double width = 240, double height = 160)
        => AddFrame(x, y, width, height, label: "Comment", color: ArchitectCanvasPalette.CommentFrameDefault);

    /// <summary>
    /// Add a comment frame with explicit label + color (used by the
    /// "Wrap selection in Frame" path — see <see cref="LogicCanvasView.Menus"/>).
    /// </summary>
    public FrameViewModel? AddFrame(double x, double y, double width, double height, string label, System.Drawing.Color color)
    {
        if (_vm is null) return null;
        PushUndo();
        var frame = new GraphFrame
        {
            Label = label,
            FrameColor = color,
            Bounds = new System.Drawing.Rectangle(
                (int)Math.Round(x), (int)Math.Round(y),
                (int)Math.Round(width), (int)Math.Round(height)),
        };
        _vm.Graph.Frames.Add(frame);
        var fvm = new FrameViewModel(frame);
        _vm.Frames.Add(fvm);
        // Flip dirty-marker — see AddNode for the rationale.
        _vm.OnGraphMutated();
        return fvm;
    }

    /// <summary>Remove a comment frame from the graph.</summary>
    public void RemoveFrame(FrameViewModel fvm)
    {
        if (_vm is null || fvm is null) return;
        PushUndo();
        _vm.Graph.Frames.Remove(fvm.Model);
        _vm.Frames.Remove(fvm);
        // Flip dirty-marker — see AddNode for the rationale.
        _vm.OnGraphMutated();
    }

    public NodeViewModel? AddNode(Node node, double x, double y)
    {
        if (_vm is null || node is null) return null;
        // Refuse to add a duplicate Macro/Process Entry/Exit singleton to
        // the live graph (the first is allowed; a second would be silently dropped
        // at export and orphan its wiring). Logged inside the guard — never a modal.
        if (WouldDuplicateSubGraphSingleton(_vm.Graph, node.Title)) return null;
        PushUndo();
        node.Location = new System.Drawing.Point((int)Math.Round(x), (int)Math.Round(y));
        _vm.Graph.Nodes.Add(node);
        _vm.Graph.MarkStructuralChange();
        var vm = new NodeViewModel(node);
        _vm.Nodes.Add(vm);
        _vm.Selection = vm;
        // Fire GraphMutatedAny so ArchitectViewModel.IsDirty flips
        // true. Pre-fix every direct-graph-mutator (AddNode / RemoveNode /
        // AddFrame / RemoveFrame / RemoveLink) was a silent dirty-marker
        // hole: only TryCreateLink + a handful of menu paths called
        // OnGraphMutated, so a user adding a node and closing got no
        // unsaved-work prompt.
        _vm.OnGraphMutated();
        return vm;
    }

    /// <summary>Remove a node from the graph and the canvas, plus any links touching it.</summary>
    /// <param name="pushUndo">
    /// When false, callers are expected to have already pushed a single undo
    /// snapshot for the whole batch (multi-delete, drag drop, etc.). Default
    /// true preserves the single-call invariant for the rest of the codebase.
    /// </param>
    public void RemoveNode(NodeViewModel vm, bool pushUndo = true)
    {
        if (_vm is null || vm is null) return;
        if (pushUndo) PushUndo();
        // Drop links connected to this node first.
        var doomed = _vm.Links.Where(l => l.Model.FromNodeId == vm.Id || l.Model.ToNodeId == vm.Id).ToList();
        var doomedModels = new System.Collections.Generic.List<Link>(doomed.Count);
        foreach (var l in doomed)
        {
            _vm.Graph.Links.Remove(l.Model);
            _vm.Links.Remove(l);
            doomedModels.Add(l.Model);
        }
        _vm.Graph.Nodes.Remove(vm.Model);
        _vm.Graph.MarkStructuralChange();
        _vm.Nodes.Remove(vm);
        if (ReferenceEquals(_vm.Selection, vm)) _vm.Selection = null;
        // Activated placeholders on the OTHER endpoint of each dropped wire
        // are now potentially orphaned. RevertIfOrphaned no-ops on the
        // dead node side because Graph.FindNodeById returns null after
        // the Nodes.Remove above.
        foreach (var dm in doomedModels) RevertActivatedPlaceholdersForLink(dm);
        // Flip dirty-marker — see AddNode for the rationale.
        _vm.OnGraphMutated();
    }

    /// <summary>
    /// Remove a link from the graph and the canvas.
    /// </summary>
    /// <param name="pushUndo">Pass <c>false</c> from batch callers that already
    /// snapshotted the graph — pre-fix this method unconditionally pushed an
    /// undo entry, so <c>SeverAllWiresOnSocket</c> and <c>RemoveDynamicSocket</c>
    /// produced N+1 entries (one for the batch + one per removed link). The
    /// default <c>true</c> preserves all single-link callers (Alt+click on a
    /// wire, link-menu Delete, etc.).</param>
    public void RemoveLink(LinkViewModel vm, bool pushUndo = true)
    {
        if (_vm is null || vm is null) return;
        if (pushUndo) PushUndo();
        var doomedModel = vm.Model;
        _vm.Graph.Links.Remove(doomedModel);
        _vm.Links.Remove(vm);
        // Revert any activated placeholder sockets on the link's endpoints.
        // No-op if the socket isn't a managed placeholder or is still wired
        // (eviction in TryCreateLink calls this for the same reason).
        RevertActivatedPlaceholdersForLink(doomedModel);

        // Re-resolve wildcard sockets across the graph after the
        // disconnect. Pre-fix a wire dropped from a wildcard group (e.g.
        // Logic.If's A/B pair) left the group stuck at its resolved type +
        // colour even once it was fully orphaned; the group should fall back
        // to SocketDataType.Any (lavender) when nothing wires it anymore.
        // Mirrors TryCreateLink's cascade + scoped notify, wrapped in the same
        // UiActivityTrace so the watchdog can attribute a large-graph stall.
        using (Phoenix.Controls.Shared.Services.UiActivityTrace
            .Begin("Architect.WildcardCascade.Disconnect"))
        {
            try { NodeRegistry.ResolveWildcardCascade(_vm.Graph); } catch { /* best effort */ }
        }
        // Scope the pin-colour refresh to the disconnected wire's endpoint
        // nodes (when still resolvable) so a wildcard revert repaints without
        // a full-graph socket sweep. Endpoints can be missing if the wire was
        // dangling — fall back to refreshing whatever endpoint survives.
        var rmFrom = _vm.FindNode(doomedModel.FromNodeId);
        var rmTo   = _vm.FindNode(doomedModel.ToNodeId);
        if (rmFrom is not null && rmTo is not null)
        {
            NotifyDataTypeChangedScoped(rmFrom, rmTo, null);
        }
        else if (rmFrom is not null)
        {
            NotifyDataTypeChangedScoped(rmFrom, rmFrom, null);
        }
        else if (rmTo is not null)
        {
            NotifyDataTypeChangedScoped(rmTo, rmTo, null);
        }

        if (ReferenceEquals(_vm.Selection, vm)) _vm.Selection = null;
        // If the dropped wire touched an Event.Trigger / Event.Executor /
        // Event.Return, the peer's socket list now needs to shrink in
        // step. Fire-and-forget cross-file sync so paired .phxg files
        // stay aligned, then re-paint the unpaired-error border set.
        if (LinkTouchesEventPair(doomedModel))
        {
            ScheduleCrossFileEventPairSync();
            RefreshEventPairErrorState();
        }
        // Flip dirty-marker — wire-delete used to skip this so a
        // user deleting a wire didn't get an unsaved-work prompt on close.
        _vm.OnGraphMutated();
    }

    private bool LinkTouchesEventPair(Link link)
    {
        if (_vm is null || link is null) return false;
        var from = _vm.Nodes.FirstOrDefault(n => n.Id == link.FromNodeId);
        var to   = _vm.Nodes.FirstOrDefault(n => n.Id == link.ToNodeId);
        return TouchesEventPair(from) || TouchesEventPair(to);
    }

    /// <summary>
    /// If <paramref name="endpoint"/> on <paramref name="endpointNode"/> is
    /// a managed-placeholder socket (Macro.Entry / Macro.Exit / Event.* /
    /// Process.* / Visual.Trigger), promote it to a typed active socket
    /// inheriting from <paramref name="otherEnd"/>'s color and append a
    /// fresh placeholder underneath. Then rebuild the node's socket VMs
    /// so the canvas picks up both the rename and the new placeholder.
    /// </summary>
    private void ActivateIfPlaceholder(NodeViewModel endpointNode, SocketViewModel endpoint, SocketViewModel otherEnd)
    {
        if (_vm is null) return;
        if (!PlaceholderActivator.IsManagedPlaceholder(endpointNode.Model, endpoint.Model)) return;

        BubbleDiag($"activate \"{endpoint.Model.Name}\" on \"{endpointNode.Title}\" " +
            $"← wire from \"{otherEnd.Model.Name}\" ({otherEnd.DataType})");
        PlaceholderActivator.Activate(_vm.Graph, endpointNode.Model, endpoint.Model, otherEnd.Model);
        // Activate may have synced sockets onto paired Event.* nodes too,
        // so rebuild the endpoint's VM AND its Event-pair peers — but NOT every
        // node in the graph. Pre-fix the unconditional full-graph
        // `foreach (var nvm in _vm.Nodes) nvm.RebuildSockets()` cleared two
        // ObservableCollections + fired PropertyChanged on every node on each
        // wire-drop, the same lag the undo path's isUndoRestore guard was added
        // to avoid. Scope the rebuild to the nodes that could have mutated.
        RebuildSocketsForMutatedNode(endpointNode.Model);
        _vm.Graph.MarkStructuralChange();
    }

    /// <summary>
    /// Wire-less slot activation — a press+release on the same managed
    /// "+ variable" / "+ return" / "+ input" / "+ output" slot adds the bubble
    /// in place with the slot's default type (String for variables, the return
    /// colour for returns — <see cref="PlaceholderActivator.Activate"/> with no
    /// source socket). Runs the SAME post-activation tail as the wire path
    /// (<see cref="ActivateIfPlaceholder"/> + <see cref="TryCreateLink"/>'s
    /// Event epilogue): own-undo snapshot, socket-VM rebuild for the node and
    /// its in-graph Event peers, graph-mutated (drives autosave, the dirty
    /// marker AND the sub-graph call-site sync debounce), then the cross-file
    /// Event-pair sync + unpaired-error refresh. No-op for clicks on real pins
    /// or unmanaged placeholders. Called from TryCreateLink's same-socket
    /// short-circuit so both render paths (GPU press-band and retained pin
    /// hit-target) and the capture-loss salvage all share it.
    /// </summary>
    private void TryActivatePlaceholderOnClick(SocketViewModel sock)
    {
        if (_vm is null || sock is null) return;
        var node = FindNodeOwning(sock);
        if (node is null) return;
        if (!PlaceholderActivator.IsManagedPlaceholder(node.Model, sock.Model)) return;

        PushUndo();
        BubbleDiag($"activate \"{sock.Model.Name}\" on \"{node.Title}\" ← click (no wire, default type)");
        PlaceholderActivator.Activate(_vm.Graph, node.Model, sock.Model, null);
        RebuildSocketsForMutatedNode(node.Model);
        _vm.Graph.MarkStructuralChange();
        _vm.OnGraphMutated();
        if (TouchesEventPair(node))
        {
            ScheduleCrossFileEventPairSync();
            RefreshEventPairErrorState();
        }
    }

    /// <summary>
    /// Rebuild the socket VMs for exactly the node that
    /// PlaceholderActivator.Activate / RevertIfOrphaned mutated, plus any
    /// Event.Trigger / Event.Executor / Event.Return peers sharing the same
    /// EventName (the activator mirrors socket shape across the pair). Avoids
    /// the full-graph RebuildSockets fan-out on every wire-drop / disconnect.
    /// </summary>
    private void RebuildSocketsForMutatedNode(Node mutated)
    {
        if (_vm is null || mutated is null) return;

        var endpointVm = _vm.FindNode(mutated.Id);
        endpointVm?.RebuildSockets();

        // Event-pair peers: same title family + matching (case-insensitive)
        // EventName. Mirrors PlaceholderActivator.SyncEventPair's peer match.
        if (IsEventPairTitleLocal(mutated.Title)
            && mutated.Attributes is not null
            && mutated.Attributes.TryGetValue("EventName", out var ev)
            && !string.IsNullOrWhiteSpace(ev))
        {
            int syncedPeers = 0;
            foreach (var nvm in _vm.Nodes)
            {
                if (ReferenceEquals(nvm, endpointVm)) continue;
                var m = nvm.Model;
                if (!IsEventPairTitleLocal(m.Title)) continue;
                if (m.Attributes is null) continue;
                if (!m.Attributes.TryGetValue("EventName", out var otherEv)) continue;
                if (!string.Equals(otherEv, ev, System.StringComparison.OrdinalIgnoreCase)) continue;
                nvm.RebuildSockets();
                syncedPeers++;
            }
            // Fingerprint for the "dynamic-bubble SYNC doesn't work" report — if a
            // drop activates but this logs "synced 0 in-graph peers" while an
            // Event.Executor("<ev>") is on the canvas, the peer match (title family
            // / EventName equality) is the failing link, not the activation.
            BubbleDiag($"pair-sync \"{mutated.Title}\"(EventName=\"{ev}\") → synced {syncedPeers} in-graph peer(s)");
        }
    }

    // Self-contained mirror of PlaceholderActivator.IsEventPairTitle (private
    // there) — the three Event-pair node titles whose socket shapes are synced.
    private static bool IsEventPairTitleLocal(string? title)
        => title is "Event.Trigger" or "Event.Executor" or "Event.Return";

    /// <summary>
    /// For each endpoint of <paramref name="link"/>, if the socket is a
    /// previously-activated dynamic placeholder (Macro.Entry/Exit /
    /// Event.Trigger / Event.Executor / Event.Return / Process.Entry/Exit /
    /// Visual.Trigger) and no other link still references it, reset it
    /// back to a placeholder row and rebuild the affected node's socket VMs.
    /// </summary>
    private void RevertActivatedPlaceholdersForLink(Link link)
    {
        if (_vm is null || link is null) return;
        TryRevert(link.FromNodeId, link.FromSocketId);
        TryRevert(link.ToNodeId,   link.ToSocketId);

        void TryRevert(string nodeId, string socketId)
        {
            var node = _vm.Graph.FindNodeById(nodeId);
            if (node is null) return;
            var socket = node.Sockets.Find(s => s.Id == socketId);
            if (socket is null) return;
            if (!PlaceholderActivator.HasManagedPlaceholders(node)) return;

            int beforeCount = node.Sockets.Count;
            string beforeName = socket.Name ?? string.Empty;
            PlaceholderActivator.RevertIfOrphaned(_vm.Graph, node, socket);
            // No mutation? Don't churn the view.
            if (node.Sockets.Count == beforeCount && (socket.Name ?? string.Empty) == beforeName) return;

            // Rebuild only the reverted node + its Event-pair peers (the
            // shrink may have synced onto paired Event.* nodes), not every node
            // in the graph — same scoping as ActivateIfPlaceholder.
            RebuildSocketsForMutatedNode(node);
            _vm.Graph.MarkStructuralChange();
        }
    }

    private static bool TouchesEventPair(NodeViewModel? n)
        => n is not null && n.Title is "Event.Trigger" or "Event.Executor" or "Event.Return";

    /// <summary>
    /// Schedule a fire-and-forget cross-file Event.Trigger / Event.Executor
    /// socket-list sync. Walks every <c>*.phxg</c> in the canonical Hub
    /// logic folder and propagates the current graph's named-event socket
    /// shape onto matching peers in those files. Best-effort — exceptions
    /// log via GlobalLogger and never bubble back into the canvas.
    /// </summary>
    /// <remarks>
    /// Debounced at <see cref="CrossFileEventPairSyncDebounceMs"/>
    /// so a burst of wire drops / removals on an Event.Trigger / Event.Executor
    /// node coalesces into one disk walk + JSON parse of every sibling
    /// .phxg. Pre-fix every wire-drop fired SyncAsync immediately — for a
    /// rapid edit session on a paired event node that's many redundant
    /// project-wide passes per second. The debounce window is the standard
    /// "edit-stop-edit" silence interval (350ms) so a user who clicks
    /// somewhere else immediately still sees their sibling files refreshed
    /// within a half-second of stopping.
    /// </remarks>
    private void ScheduleCrossFileEventPairSync()
    {
        if (_vm is null) return;
        EnsureCrossFileSyncTimer();
        // Start (or restart) the timer — each call rolls the wall-clock
        // forward by another debounce window, so a burst that never quiets
        // down for the full window keeps coalescing.
        _crossFileSyncTimer!.Stop();
        _crossFileSyncTimer.Start();
    }

    // Debounce window for cross-file event-pair sync. 350ms is the
    // standard "stopped editing" silence interval — short enough that a user
    // who pauses sees their sibling files refreshed within the perceptible
    // "I just made a change" beat, long enough that a paste-then-undo /
    // wire-drop-then-rewire burst is one disk walk instead of many.
    private const int CrossFileEventPairSyncDebounceMs = 350;
    private Microsoft.UI.Xaml.DispatcherTimer? _crossFileSyncTimer;

    private void EnsureCrossFileSyncTimer()
    {
        if (_crossFileSyncTimer is not null) return;
        _crossFileSyncTimer = new Microsoft.UI.Xaml.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(CrossFileEventPairSyncDebounceMs),
        };
        _crossFileSyncTimer.Tick += OnCrossFileSyncTimerTick;
    }

    private void OnCrossFileSyncTimerTick(object? sender, object e)
    {
        // DispatcherTimer fires repeatedly until Stop() — stop on first tick
        // so a single Schedule() call produces exactly one SyncAsync once
        // the debounce window quiets down. The next ScheduleCrossFileEventPairSync
        // call restarts the timer.
        _crossFileSyncTimer?.Stop();

        if (_vm is null) return;
        // Snapshot the source graph's event nodes HERE on the UI thread —
        // detached clones (cheap, a handful of nodes), taken at apply time so
        // the sync sees the LATEST state after a burst of edits. Then run the
        // sibling-.phxg enumerate / parse / write on a thread-pool thread.
        // SafeRunAsync runs its delegate on the *calling* thread until the
        // first await, and SyncAsync has no await in the common no-change
        // case, so without the Task.Run hop the whole peer-parse loop ran on
        // the UI thread ~350 ms after every edit to an event-bearing graph
        // (the "Architect freezes at random" report). The clone severs the
        // shared-mutable graph reference so the background run can't race a
        // concurrent canvas edit.
        var eventSnapshot = EventPairCrossFileSync.SnapshotEventNodes(_vm.Graph);
        // Live in-memory fan-out to every OTHER open Architect canvas so a paired
        // Event node open in another window updates IMMEDIATELY (no disk round-
        // trip / reload). Runs on the UI thread — all canvases share Hub's
        // dispatcher; cheap (a handful of event-node clones + a per-peer no-op
        // when the peer holds no matching EventName). The disk sync below still
        // runs for CLOSED peer files; both apply the same idempotent algorithm.
        Phoenix.Controls.Architect.WinUI.Services.EventPairLiveSync.Broadcast(this, eventSnapshot);
        // Same directory the adopt-on-join scan reads — see
        // ResolveEventPeerScanDirectory for why the two must never diverge.
        string projectDir = ResolveEventPeerScanDirectory();
        string? currentFile = _vm.LoadedFilePath;
        // Snapshot the open-peer paths on the UI thread; the disk sync SKIPS them
        // (they were just handled live in-memory by Broadcast and persist via their
        // own save) so the background write can't race their save.
        var openPeers = Phoenix.Controls.Architect.WinUI.Services.EventPairLiveSync.GetOpenPeerFilePaths();
        Phoenix.Controls.Shared.Core.AsyncErrorBoundary.SafeRunAsync(
            () => System.Threading.Tasks.Task.Run(
                () => EventPairCrossFileSync.SyncAsync(projectDir, currentFile, eventSnapshot, openPeers)),
            "Architect.Canvas",
            "EventPairCrossFileSync");
    }

    /// <summary><see cref="Services.EventPairLiveSync.IPeer"/> — the .phxg this
    /// canvas currently shows, so the disk sync can skip it while it's open.</summary>
    string? Services.EventPairLiveSync.IPeer.LiveSyncFilePath => _vm?.LoadedFilePath;

    /// <summary>
    /// <see cref="Services.EventPairLiveSync.IPeer"/> — apply an incoming
    /// cross-window Event-pair socket delta to THIS canvas's live graph. Invoked
    /// on the UI thread from another canvas's debounced sync tick. No-op unless
    /// this graph holds an Event.Trigger / Event.Executor / Event.Return whose
    /// (Title, EventName) matches the source; otherwise applies the socket
    /// add / remove / retype and refreshes the affected node + wire views WITHOUT
    /// resetting this window's pan / zoom / selection. Uses the SAME algorithm as
    /// the on-disk peer sync so an open peer and a closed one can't diverge.
    /// </summary>
    void Services.EventPairLiveSync.IPeer.ApplyIncomingEventPairSync(
        System.Collections.Generic.IReadOnlyList<Phoenix.Controls.Shared.Models.Node> sourceEventNodes)
    {
        if (_vm is null) return;
        bool changed;
        try
        {
            changed = EventPairCrossFileSync.ApplyEventPairSyncToGraph(sourceEventNodes, _vm.Graph);
        }
        catch (System.Exception ex)
        {
            GlobalLogger.Error("Architect.Canvas", "ApplyIncomingEventPairSync", ex);
            return;
        }
        if (!changed) return;
        _vm.RefreshAfterExternalGraphMutation();
        BubbleDiag($"live cross-window sync applied to \"{_vm.LoadedFilePath ?? "(unsaved)"}\"");
        if (_useImmediateMode) ImmediateCanvas?.Invalidate();
    }

    /// <summary>
    /// Names of orphan-event nodes already logged this session — keeps
    /// the soft warning to one line per name even if RefreshEventPairErrorState
    /// fires on every wire-drop / load. Cleared when the bound Graph changes.
    /// </summary>
    private readonly System.Collections.Generic.HashSet<string> _loggedOrphanNames
        = new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Monotonic generation stamp for <see cref="RefreshEventPairErrorState"/>'s
    /// off-thread peer-index load. Each refresh bumps it; an in-flight load
    /// whose stamp no longer matches has been superseded by a newer refresh and
    /// drops its (now-stale) result instead of applying it.
    /// </summary>
    private int _eventErrorRefreshGen;

    /// <summary>
    /// Recompute the unpaired-Event.Trigger/Executor set and propagate it
    /// onto every NodeViewModel as IsErrorState. The canvas's NodeView
    /// XAML observes the flag and paints a red border so the authoring
    /// slip (peer typo, peer deleted) is visible at a glance.
    /// 0.10.0 — paired with a soft GlobalLogger warning (one per
    /// orphan name, per session) so the streamer notices the missing
    /// peer even when their canvas is scrolled past the offending node.
    /// No modal — the rejection is silently visible in the log window.
    /// </summary>
    private void RefreshEventPairErrorState()
    {
        if (_vm is null) return;
        var unpaired = PlaceholderActivator.ComputeUnpairedEventNodeIds(_vm.Graph);

        // Decide whether any unpaired node would consult the cross-file index.
        // Pure intra-graph pairs need no disk hit, so the common case stays
        // entirely on the UI thread with no I/O.
        bool needsIndex = false;
        foreach (var n in _vm.Nodes)
        {
            if (!unpaired.Contains(n.Id)) continue;
            if (n.Model.Title is "Event.Trigger" or "Event.Executor"
                && n.Model.Attributes is not null
                && n.Model.Attributes.TryGetValue("EventName", out var ev)
                && !string.IsNullOrWhiteSpace(ev))
            {
                needsIndex = true;
                break;
            }
        }

        if (!needsIndex)
        {
            ApplyEventPairErrorState(unpaired, null);
            return;
        }

        // PERF (freeze sweep): LoadPeerEventIndex walks the project directory
        // (Directory.GetFiles + a stat per sibling .phxg, and a full JSON parse
        // of each on a cache-miss). It used to run on the UI thread on every
        // wire-drop / load while an unpaired event node existed — a
        // disk-latency-dependent stall (worse under OneDrive / AV scan) that
        // matched the "Architect freezes at random" report. Load it on a
        // thread-pool thread, then marshal the flag-apply back to the UI
        // thread. The generation stamp drops a stale result if a newer refresh
        // superseded this one before the index returned.
        int gen = ++_eventErrorRefreshGen;
        string projectDir = Phoenix.Controls.Shared.Core.Paths.HubLogic;
        string? loadedFile = _vm.LoadedFilePath;
        var dq = DispatcherQueue;
        Phoenix.Controls.Shared.Core.AsyncErrorBoundary.SafeRunAsync(
            () => System.Threading.Tasks.Task.Run(() =>
            {
                // Bail early if a newer refresh has
                // already superseded this one before we pay for the (potentially
                // disk-touching) peer-index load. Correctness is still
                // guaranteed by the apply-time re-check below — this just avoids
                // wasted work during rapid wire-drop bursts. Volatile.Read for a
                // fresh cross-thread peek at the UI-thread-owned counter.
                if (gen != System.Threading.Volatile.Read(ref _eventErrorRefreshGen)) return;
                var peerIndex = EventPairCrossFileSync.LoadPeerEventIndex(projectDir, loadedFile);
                // Apply on the UI thread (NodeViewModel state is bound). If
                // there's no dispatcher (teardown) the cosmetic red border just
                // doesn't update — nothing to paint anyway.
                dq?.TryEnqueue(() =>
                {
                    if (gen != _eventErrorRefreshGen) return;
                    ApplyEventPairErrorState(unpaired, peerIndex);
                });
            }),
            "Architect.Canvas",
            "RefreshEventPairErrorState");
    }

    /// <summary>
    /// Apply the unpaired-event error state (+ one-shot soft orphan log) to
    /// every node. Split out of <see cref="RefreshEventPairErrorState"/> so the
    /// apply can run either inline (no cross-file index needed) or as the
    /// UI-thread continuation of an off-thread <c>LoadPeerEventIndex</c>.
    /// Reads / writes NodeViewModel state, so it MUST run on the UI thread.
    /// </summary>
    private void ApplyEventPairErrorState(
        System.Collections.Generic.HashSet<string> unpaired,
        System.Collections.Generic.HashSet<(string Title, string EventName)>? peerIndex)
    {
        if (_vm is null) return;
        foreach (var n in _vm.Nodes)
        {
            bool wantsError = unpaired.Contains(n.Id);
            // Cross-file allowance: a peer in a sibling .phxg counts.
            if (wantsError && peerIndex is not null)
            {
                if (n.Model.Title is "Event.Trigger" or "Event.Executor"
                    && n.Model.Attributes is not null
                    && n.Model.Attributes.TryGetValue("EventName", out var ev)
                    && !string.IsNullOrWhiteSpace(ev))
                {
                    string peerTitle = n.Model.Title == "Event.Trigger" ? "Event.Executor" : "Event.Trigger";
                    if (peerIndex.Contains((peerTitle, ev)))
                    {
                        wantsError = false;
                    }
                }
            }
            // Per-node opt-out: the "Disable Connection Warnings" context-menu
            // toggle sets this attribute on Event.Trigger/Executor/Return. It
            // already suppresses the save-time validator warning
            // (ScriptExporter.CheckUnmatchedEventPairs); honour it here too so
            // toggling it also clears the live red-outline (IsErrorState) and
            // the soft orphan log. Pre-fix the toggle only muted the save
            // dialog, leaving the red border on — which read as "the toggle
            // does nothing / is missing" (Majo's report).
            bool warningsDisabled =
                n.Model.Attributes is not null
                && n.Model.Attributes.TryGetValue("DisableConnectionWarnings", out var dcw)
                && string.Equals(dcw, "true", StringComparison.OrdinalIgnoreCase);
            if (warningsDisabled) wantsError = false;

            n.IsErrorState = wantsError;

            // Populate ErrorReason so hovering
            // the red node explains WHY it's flagged. The red-triangle badge +
            // tooltip mechanism already exists on NodeViewModel/NodeView; pre-fix
            // it was never fed for unpaired-event nodes, so the red border had no
            // hover-discoverable explanation. Cleared when the node isn't errored.
            if (wantsError
                && n.Model.Title is "Event.Trigger" or "Event.Executor"
                && n.Model.Attributes is not null
                && n.Model.Attributes.TryGetValue("EventName", out var reasonEv)
                && !string.IsNullOrWhiteSpace(reasonEv))
            {
                string peerTitle = n.Model.Title == "Event.Trigger" ? "Event.Executor" : "Event.Trigger";
                n.ErrorReason =
                    $"Unpaired {n.Model.Title} “{reasonEv}” — no matching {peerTitle} in this graph or any sibling .phxg.";
            }
            else
            {
                n.ErrorReason = null;
            }

            // Soft warning — one log line per (title, name) pair per session
            // so wire-drops on a known-orphan node don't spam the log.
            if (wantsError
                && n.Model.Title is "Event.Trigger" or "Event.Executor"
                && n.Model.Attributes is not null
                && n.Model.Attributes.TryGetValue("EventName", out var orphanEv)
                && !string.IsNullOrWhiteSpace(orphanEv))
            {
                string logKey = n.Model.Title + ":" + orphanEv;
                if (_loggedOrphanNames.Add(logKey))
                {
                    string peerTitle = n.Model.Title == "Event.Trigger"
                        ? "Event.Executor" : "Event.Trigger";
                    GlobalLogger.Log(
                        $"Orphan event node: {n.Model.Title}(\"{orphanEv}\") has no matching {peerTitle} in this graph or in any sibling .phxg.",
                        "Architect.EventPair",
                        LogLevel.System);
                }
            }
        }
    }

    /// <summary>
    /// Try to create a wire between two sockets if they're compatible per
    /// <see cref="NodeRegistry.AreCompatible"/>. Direction is normalised:
    /// outputs always become From, inputs become To. Returns the new link
    /// VM on success, null on type mismatch / same-direction / self-loop.
    /// </summary>
    public LinkViewModel? TryCreateLink(SocketViewModel a, SocketViewModel b)
    {
        if (_vm is null || a is null || b is null) return null;

        // Each rejection logs via GlobalLogger rather than popping a MessageBox
        // (repeatable rejection — log, don't modal).
        //
        // Same-socket first: a click on a socket row (press+release without
        // leaving it) arrives here as a zero-length drag with a == b. That is
        // not an authoring mistake worth logging — and it is also trivially
        // "same side", so it must short-circuit BEFORE the same-direction log
        // or every click on a "+ variable" / "+ return" row spams the log.
        if (ReferenceEquals(a.Model, b.Model))
        {
            // On a managed "+ variable" / "+ return" / "+ input" / "+ output"
            // slot, that press+release IS the gesture: activate the slot in
            // place (no wire, default type) so the add-slot behaves like the
            // button it visually is. Everywhere else the click stays a silent
            // no-op. Wire-less adds are the only way to author an event's
            // payload shape on the DEFINING side before any consumer exists —
            // pre-fix the user's natural gesture (click the "+" bubble) was
            // discarded without feedback and the trigger/executor pair could
            // only grow via a completed wire to another node's pin.
            TryActivatePlaceholderOnClick(a);
            return null;
        }

        // Placeholder→placeholder on the SAME node is a fumbled click on a
        // "+" slot, not authoring intent: the drop band is a full-width row
        // stripe and adjacent "+ variable"/"+ return" bands meet at the row
        // boundary, so a few pixels of mouse drift between press and release
        // resolved the release into the NEIGHBOURING slot — which then died
        // in the same-direction / self-loop rejections and the user's click
        // silently did nothing (SystemHistory: repeated "self-loop on node
        // 'Event.Executor'" rejects while trying to add a bubble). Treat it
        // as the click it was and activate the PRESSED slot (a = the drag
        // source). Cross-node placeholder pairs still fall through to the
        // normal rejection paths below.
        if (a.Model.IsPlaceholder && b.Model.IsPlaceholder)
        {
            var na = FindNodeOwning(a);
            if (na is not null && ReferenceEquals(na, FindNodeOwning(b)))
            {
                TryActivatePlaceholderOnClick(a);
                return null;
            }
        }

        if (a.Direction == b.Direction)
        {
            // A drag between two managed placeholders on the same side of two
            // DIFFERENT nodes is never an authoring intent — reject silently,
            // same rationale as the same-socket case. (Same-node pairs were
            // already converted to a slot activation above.)
            if (a.Model.IsPlaceholder && b.Model.IsPlaceholder) return null;
            GlobalLogger.Log(
                $"Wire rejected: both endpoints on the same side ({a.Direction})",
                source: "Architect.Canvas",
                level: LogLevel.System);
            return null;
        }

        var (src, dst) = a.Direction == SocketType.Output ? (a, b) : (b, a);
        // Managed dynamic placeholders
        // ("+ variable" / "+ return" / "+ input" / "+ output" on Event.* / Macro.* /
        // Process.* / Visual.Trigger) ADOPT the dropped wire's type when they activate
        // (PlaceholderActivator.Activate inherits the source socket's colour/type), so
        // the specific-type compatibility check must NOT gate a drop onto them — in the
        // pre-WinUI canvas you could drop ANY data wire onto a "+ variable" and it
        // became that type. The regression: a placeholder loaded from a .phxg carries
        // DataType=String (GraphSerializer.MigrateNodes derives it from the placeholder's
        // ColString colour), so AreCompatible rejected every non-String drop and silently
        // killed dynamic-bubble creation AND the cross-node sync that follows activation.
        // It "didn't surface" in tests because PlaceholderActivatorTests call Activate
        // directly, bypassing this canvas gate. We still enforce the structural
        // Flow-vs-data rule: a data placeholder can't accept a Flow wire (and vice versa).
        bool eitherPlaceholder = src.Model.IsPlaceholder || dst.Model.IsPlaceholder;
        if (eitherPlaceholder)
        {
            bool srcFlow = SocketTypeHelper.IsFlowPin(src.Model);
            bool dstFlow = SocketTypeHelper.IsFlowPin(dst.Model);
            if (srcFlow != dstFlow)
            {
                GlobalLogger.Log(
                    $"Wire rejected: a {(srcFlow ? "flow" : "data")} pin cannot connect to a {(dstFlow ? "flow" : "data")} placeholder",
                    source: "Architect.Canvas",
                    level: LogLevel.System);
                return null;
            }
        }
        else if (!NodeRegistry.AreCompatible(src.DataType, dst.DataType))
        {
            GlobalLogger.Log(
                $"Wire rejected: {src.DataType} → {dst.DataType} is not type-compatible",
                source: "Architect.Canvas",
                level: LogLevel.System);
            return null;
        }

        // Look up the parent nodes via Graph.FindSocketById (O(1)
        // dict-backed in Graph) instead of the previous LINQ scan over every
        // node × every socket. We still need the NodeViewModel (not the raw
        // Node model) for downstream NotifyDataTypeChanged scoping + the
        // self-loop check, so resolve VM via _vm.FindNode against the model's
        // NodeId — that lookup walks _vm.Nodes once and is bounded by graph
        // size, but it's a single string compare per node rather than a
        // FindSocket-per-node scan.
        var srcSocketModel = _vm.Graph.FindSocketById(src.Id);
        var dstSocketModel = _vm.Graph.FindSocketById(dst.Id);
        NodeViewModel? fromNode = null;
        NodeViewModel? toNode   = null;
        if (srcSocketModel is not null || dstSocketModel is not null)
        {
            // Single-pass walk: we need the parent Node for each of the two
            // sockets. Resolve via the node owning the socket id (one scan
            // over node.Sockets per node), then map model→VM.
            foreach (var nvm in _vm.Nodes)
            {
                if (fromNode is null && nvm.FindSocket(src.Id) is not null) fromNode = nvm;
                if (toNode   is null && nvm.FindSocket(dst.Id) is not null) toNode   = nvm;
                if (fromNode is not null && toNode is not null) break;
            }
        }
        if (fromNode is null || toNode is null) return null;
        if (ReferenceEquals(fromNode, toNode))
        {
            GlobalLogger.Log(
                $"Wire rejected: self-loop on node \"{fromNode.Title}\" not allowed",
                source: "Architect.Canvas",
                level: LogLevel.System);
            return null;
        }

        // Exact-duplicate guard. Re-dropping a wire between the
        // SAME source output and the SAME destination input is a no-op — never
        // create a twin. Checked BEFORE PushUndo so a redundant drop doesn't
        // leave a wasted undo step. This is also what keeps the flow-merge path
        // below safe: a Flow input legitimately holds MANY incoming wires, so we
        // can no longer lean on single-wire eviction to collapse a re-drop.
        for (int i = 0; i < _vm.Links.Count; i++)
        {
            var p = _vm.Links[i];
            if (p.Model.FromSocketId == src.Id && p.Model.ToSocketId == dst.Id)
                return p; // identical wire already present — nothing to do
        }

        // Snapshot once for the whole wire-create operation — including any prior
        // wire we're about to evict from the destination input. We bypass
        // RemoveLink for the eviction path so it doesn't push a second snapshot
        // for the same user action.
        PushUndo();

        // Destination-input capacity rule, split by pin kind:
        //
        //   * DATA inputs hold at most ONE wire — a node reads a single value per
        //     input, so a new connection REPLACES (evicts) any prior one.
        //   * FLOW (execution) inputs accept UNLIMITED incoming wires — that is
        //     how branches MERGE / re-join (UE-Blueprints exec semantics). The
        //     exporter is built for exactly this (ScriptExporter.CountIncomingFlows
        //     / FindMergePoint / ExporterBranchMergeTests), and real graphs ship it
        //     (banking.phxg merges two arms into Twitch.SendChat's "Flow" input).
        //
        // The prior code (comment and all) treated EVERY input as single-capacity
        // and so silently wiped a working merge the instant a third arm was added —
        // the critical regression Majo flagged ("Wire replaced: input Flow ... had 2
        // existing connections — all were disconnected"). We therefore evict ONLY
        // for data inputs; flow inputs accumulate.
        bool dstIsFlow = SocketTypeHelper.IsFlowPin(dst.Model);
        System.Collections.Generic.List<Link>? evictedModels = null;
        if (!dstIsFlow)
        {
            // Manual single-pass collect of prior wires that terminate
            // on the destination socket (the ones we're about to evict). Pre-fix
            // built `_vm.Links.Where(...).ToList()` per wire-create — a List<T>
            // allocation + LINQ enumerator on the interactive hot path. The
            // typical eviction count is 0 or 1, so default-sized List<Link> is
            // enough; only the rare multi-evict case grows.
            for (int i = _vm.Links.Count - 1; i >= 0; i--)
            {
                var p = _vm.Links[i];
                if (p.Model.ToSocketId != dst.Id) continue;
                _vm.Graph.Links.Remove(p.Model);
                _vm.Links.RemoveAt(i);
                (evictedModels ??= new System.Collections.Generic.List<Link>(1)).Add(p.Model);
            }
            // A data input socket holds at most one
            // wire, so the new connection silently replaced any prior one. Log the
            // eviction so the user (and the System Log) can see a wire was
            // displaced rather than silently vanishing — same non-modal channel
            // TryCreateLink's rejections use. A multi-evict (>1) is unusual and
            // worth flagging explicitly.
            if (evictedModels is not null)
            {
                GlobalLogger.Log(
                    evictedModels.Count == 1
                        ? $"Wire replaced: input \"{dst.Label}\" on \"{toNode.Title}\" had an existing connection — it was disconnected."
                        : $"Wire replaced: input \"{dst.Label}\" on \"{toNode.Title}\" had {evictedModels.Count} existing connections — all were disconnected.",
                    source: "Architect.Canvas",
                    level: LogLevel.System);
            }
        }

        // Single-outgoing-flow-pin enforcement. A Flow OUTPUT socket
        // may drive at most one downstream wire — execution flow is a single
        // chain, not a fan-out. The destination-eviction loop above only handles
        // the input side; the WinForms baseline also disconnected any prior wire
        // leaving the same Flow output before committing the new one. Without
        // this a user could create multiple wires from one Flow pin, breaking
        // the execution-flow graph invariant. (Data outputs legitimately fan out
        // and are intentionally excluded.)
        if (src.Direction == SocketType.Output && SocketTypeHelper.IsFlowPin(src.Model))
        {
            // Raw single-pass removal mirroring the destination-eviction loop
            // above: drop the model from Graph.Links + the VM collection, and
            // record it in evictedModels so the shared revert loop below folds
            // back any orphaned placeholder it leaves behind. Bypassing
            // RemoveLink keeps the single undo snapshot (PushUndo already ran).
            int flowEvicted = 0;
            for (int i = _vm.Links.Count - 1; i >= 0; i--)
            {
                var p = _vm.Links[i];
                if (p.Model.FromSocketId != src.Id) continue;
                _vm.Graph.Links.Remove(p.Model);
                _vm.Links.RemoveAt(i);
                (evictedModels ??= new System.Collections.Generic.List<Link>(1)).Add(p.Model);
                flowEvicted++;
            }
            if (flowEvicted > 0)
            {
                GlobalLogger.Log(
                    flowEvicted == 1
                        ? $"Flow rerouted: output \"{src.Label}\" on \"{fromNode.Title}\" already drove a downstream node — the prior flow wire was disconnected."
                        : $"Flow rerouted: output \"{src.Label}\" on \"{fromNode.Title}\" drove {flowEvicted} downstream wires — all were disconnected (a Flow pin drives one chain).",
                    source: "Architect.Canvas",
                    level: LogLevel.System);
            }
        }

        var link = new Link
        {
            FromNodeId   = fromNode.Id,
            FromSocketId = src.Id,
            ToNodeId     = toNode.Id,
            ToSocketId   = dst.Id,
        };
        _vm.Graph.Links.Add(link);
        // Graph.Links mutation invalidates the id caches' link side; the
        // node/socket dicts are still fresh because we didn't touch nodes
        // here, but MarkStructuralChange is the safe default if a future
        // PlaceholderActivator path mutates sockets below.
        var lvm = new LinkViewModel(link, _vm.Graph);
        _vm.Links.Add(lvm);

        // Dynamic placeholder activation — Macro.Entry/Exit, Event.Trigger,
        // Event.Executor, Event.Return, Process.Entry/Exit, Visual.Trigger.
        // If either endpoint is sitting on a managed "+ variable" / "+ input"
        // / "+ output" / "+ return" placeholder, promote it to a named, typed
        // socket (Var1, In1, etc.) and append a fresh placeholder underneath.
        // The link's FromSocketId/ToSocketId stay valid because Activate
        // mutates the socket in place and only adds NEW sockets after it.
        ActivateIfPlaceholder(fromNode, src, dst);
        ActivateIfPlaceholder(toNode,   dst, src);

        // Evicted prior wires may have left their source/dest sockets
        // orphaned — if those were activated placeholders, fold them back.
        if (evictedModels is not null)
        {
            foreach (var ev in evictedModels) RevertActivatedPlaceholdersForLink(ev);
        }

        // Resolve wildcard sockets across the graph (Logic.If's A/B group, etc.)
        // — same call Canvas runs after every wire mutation. Then nudge
        // every SocketViewModel so pin colours reflect the new types in the view.
        // 0.11.x — wrapped in UiActivityTrace so the watchdog's stall
        // message identifies this path when a large graph cascade hangs the
        // UI thread (suspect for the reported 8s freeze).
        using (Phoenix.Controls.Shared.Services.UiActivityTrace
            .Begin("Architect.WildcardCascade"))
        {
            try { NodeRegistry.ResolveWildcardCascade(_vm.Graph); } catch { /* best effort */ }
        }
        // Scope the SocketViewModel pin-colour nudge to the nodes
        // that actually participate in the new link (fromNode + toNode) plus,
        // for any evicted prior wire, the nodes its endpoints sat on. The
        // full-graph sweep was correct but wasteful — wildcard cascade
        // changes only propagate along link chains, and a single new wire
        // doesn't re-resolve every unrelated socket. RetypeNotify-by-id walks
        // a tiny set typically; falling back to the global sweep only when
        // we can't resolve the endpoint VMs preserves correctness for any
        // edge case where the eviction model lost its NodeViewModel mapping.
        NotifyDataTypeChangedScoped(fromNode, toNode, evictedModels);
        // Wire colours derive from source-socket type; recompute paths so the
        // resolved type's hex flows through to LinkViewModel.StrokeColorHex.
        _vm.OnGraphMutated();

        // If either endpoint is on an Event.Trigger / Event.Executor /
        // Event.Return, propagate the new socket shape to every paired
        // .phxg in the project directory and refresh the unpaired-error
        // border set.
        if (TouchesEventPair(fromNode) || TouchesEventPair(toNode))
        {
            ScheduleCrossFileEventPairSync();
            RefreshEventPairErrorState();
        }

        return lvm;
    }

    /// <summary>
    /// Notify pin-colour bindings for the nodes touched by a wire
    /// create / evict — fromNode, toNode, plus the node-VM owner of each
    /// endpoint of any evicted prior link. The wildcard cascade is graph-
    /// wide but only mutates sockets along link chains; the full-graph
    /// foreach (var n in _vm.Nodes) sweep that used to run after every wire
    /// drop was wasteful for graphs with many unrelated subgraphs.
    /// </summary>
    private void NotifyDataTypeChangedScoped(
        NodeViewModel fromNode,
        NodeViewModel toNode,
        System.Collections.Generic.List<Link>? evictedModels)
    {
        if (_vm is null) return;
        NotifyNodeSocketTypes(fromNode);
        if (!ReferenceEquals(fromNode, toNode)) NotifyNodeSocketTypes(toNode);
        if (evictedModels is null) return;
        foreach (var ev in evictedModels)
        {
            var owner1 = _vm.FindNode(ev.FromNodeId);
            var owner2 = _vm.FindNode(ev.ToNodeId);
            if (owner1 is not null
                && !ReferenceEquals(owner1, fromNode)
                && !ReferenceEquals(owner1, toNode))
            {
                NotifyNodeSocketTypes(owner1);
            }
            if (owner2 is not null
                && !ReferenceEquals(owner2, fromNode)
                && !ReferenceEquals(owner2, toNode)
                && !ReferenceEquals(owner2, owner1))
            {
                NotifyNodeSocketTypes(owner2);
            }
        }

        static void NotifyNodeSocketTypes(NodeViewModel n)
        {
            foreach (var s in n.Inputs)  s.NotifyDataTypeChanged();
            foreach (var s in n.Outputs) s.NotifyDataTypeChanged();
        }
    }
}
