using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Phoenix.Controls.Architect.Core;
using Phoenix.Controls.Architect.WinUI.ViewModels;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

// Top-level view-model for the Logic Canvas pane. Owns the loaded
// Graph, the visual node/link VM collections, the pan/zoom
// state, and the current selection. Track 5's LogicCanvasView XAML
// binds:
//
//   <ItemsControl ItemsSource="{Binding Nodes}" ... NodeView template>
//   <ItemsControl ItemsSource="{Binding Links}" ... Path-bound template>
//   <Slider       Value="{Binding Zoom, Mode=TwoWay}" ... >
//
// Selection is single-target for this commit (matches the briefing's
// "basic select" scope). Multi-select via marquee is in the deferred
// list inside CanvasNotes.md.
//
// Implements IDisposable so SubGraphWindow can hand the VM a tear-down
// signal on Window.Closed (TODO 2026-05-07 round 2 P0 #4). Today there
// are no managed event subscriptions to break here, but the seam is the
// canonical hook for any future per-VM source-event subscription so
// future additions don't accidentally leak through detached SubGraphWindows.
public sealed class LogicCanvasViewModel : ObservableObject, IDisposable
{
    private Graph _graph = new();
    private double _zoom = 1.0;
    private double _panX;
    private double _panY;
    private object? _selection;

    //  Live id→VM index kept in lockstep with the
    // Nodes collection via CollectionChanged. Replaces (a) the per-call dict
    // rebuild inside SyncSocketConnectivity and (b) the O(n) linear scan in
    // FindNode. Subscribing to CollectionChanged means EVERY mutation site
    // (LoadGraph clear+add, spawn, paste, delete, undo-replay) stays correct
    // without each call site remembering to invalidate. Built incrementally:
    // Nodes mutations are infrequent relative to the per-link / per-wire
    // lookups that read this.
    private readonly Dictionary<string, NodeViewModel> _nodesById = new();

    //  Per-node incident-link index kept in
    // lockstep with the Links collection via CollectionChanged. TranslateNode
    // (called per moved node at pointer cadence, x N for a group drag) used to
    // scan the ENTIRE Links collection to find wires touching the moved node —
    // O(N x L) per group-drag move. This index makes it O(incident links).
    // A LinkViewModel's endpoints (From/ToNodeId) are immutable after
    // construction (rewire replaces the link), so indexing at add-time is safe.
    private readonly Dictionary<string, List<LinkViewModel>> _linksByNode = new();

    //  Per-socket incident-link index — mirrors _linksByNode but
    // keyed on socket id. NodeView.Pins.NudgeLinksForSocket fires per socket-row
    // SizeChanged/Loaded (≈770 rows on CommandStructure.phxg) and used to
    // full-scan the whole Links collection per row (O(rows × links) ≈ 200k
    // compares per relayout wave). Filing each link under both its endpoint
    // socket ids turns each nudge into O(incident). Socket ids are immutable
    // after link construction (rewire replaces the link), so add-time indexing
    // is safe, same invariant _linksByNode relies on.
    private readonly Dictionary<string, List<LinkViewModel>> _linksBySocket = new();

    public LogicCanvasViewModel()
    {
        Nodes  = new BulkObservableCollection<NodeViewModel>();
        Links  = new BulkObservableCollection<LinkViewModel>();
        Frames = new BulkObservableCollection<FrameViewModel>();
        Nodes.CollectionChanged += OnNodesCollectionChanged;
        Links.CollectionChanged += OnLinksCollectionChanged;
    }

    //  Maintain _nodesById alongside the Nodes
    // collection. Reset (Clear) rebuilds from scratch; Add / Remove / Replace
    // apply the delta. Empty / duplicate ids are guarded the same way the
    // prior per-call rebuild did (skip empty; last-write-wins on dup, which
    // matches the old Dictionary indexer assignment).
    private void OnNodesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems != null)
                    foreach (NodeViewModel n in e.NewItems)
                        if (!string.IsNullOrEmpty(n.Id)) _nodesById[n.Id] = n;
                break;
            case NotifyCollectionChangedAction.Remove:
                if (e.OldItems != null)
                    foreach (NodeViewModel n in e.OldItems)
                        if (!string.IsNullOrEmpty(n.Id)
                            && _nodesById.TryGetValue(n.Id, out var cur)
                            && ReferenceEquals(cur, n))
                            _nodesById.Remove(n.Id);
                break;
            case NotifyCollectionChangedAction.Replace:
                if (e.OldItems != null)
                    foreach (NodeViewModel n in e.OldItems)
                        if (!string.IsNullOrEmpty(n.Id)
                            && _nodesById.TryGetValue(n.Id, out var old)
                            && ReferenceEquals(old, n))
                            _nodesById.Remove(n.Id);
                if (e.NewItems != null)
                    foreach (NodeViewModel n in e.NewItems)
                        if (!string.IsNullOrEmpty(n.Id)) _nodesById[n.Id] = n;
                break;
            default: // Reset / Move — rebuild from the authoritative collection.
                _nodesById.Clear();
                foreach (var n in Nodes)
                    if (!string.IsNullOrEmpty(n.Id)) _nodesById[n.Id] = n;
                break;
        }
    }

    //  Maintain _linksByNode alongside Links.
    // Each link is filed under BOTH its endpoint node ids so TranslateNode can
    // find every wire touching a moved node in O(incident) instead of scanning
    // the whole collection. Reset rebuilds from scratch.
    private void OnLinksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems != null)
                    foreach (LinkViewModel l in e.NewItems) IndexLink(l);
                break;
            case NotifyCollectionChangedAction.Remove:
                if (e.OldItems != null)
                    foreach (LinkViewModel l in e.OldItems) DeindexLink(l);
                break;
            case NotifyCollectionChangedAction.Replace:
                if (e.OldItems != null)
                    foreach (LinkViewModel l in e.OldItems) DeindexLink(l);
                if (e.NewItems != null)
                    foreach (LinkViewModel l in e.NewItems) IndexLink(l);
                break;
            default: // Reset / Move — rebuild.
                _linksByNode.Clear();
                _linksBySocket.Clear();
                foreach (var l in Links) IndexLink(l);
                break;
        }
    }

    private void IndexLink(LinkViewModel l)
    {
        AddLinkToNode(l.Model.FromNodeId, l);
        AddLinkToNode(l.Model.ToNodeId, l);
        AddLinkToSocket(l.Model.FromSocketId, l);
        AddLinkToSocket(l.Model.ToSocketId, l);
    }

    private void DeindexLink(LinkViewModel l)
    {
        RemoveLinkFromNode(l.Model.FromNodeId, l);
        RemoveLinkFromNode(l.Model.ToNodeId, l);
        RemoveLinkFromSocket(l.Model.FromSocketId, l);
        RemoveLinkFromSocket(l.Model.ToSocketId, l);
    }

    private void AddLinkToNode(string? nodeId, LinkViewModel l)
    {
        if (string.IsNullOrEmpty(nodeId)) return;
        if (!_linksByNode.TryGetValue(nodeId, out var list))
            _linksByNode[nodeId] = list = new List<LinkViewModel>(2);
        list.Add(l);
    }

    private void RemoveLinkFromNode(string? nodeId, LinkViewModel l)
    {
        if (string.IsNullOrEmpty(nodeId)) return;
        if (_linksByNode.TryGetValue(nodeId, out var list))
        {
            list.Remove(l);
            if (list.Count == 0) _linksByNode.Remove(nodeId);
        }
    }

    private void AddLinkToSocket(string? socketId, LinkViewModel l)
    {
        if (string.IsNullOrEmpty(socketId)) return;
        if (!_linksBySocket.TryGetValue(socketId, out var list))
            _linksBySocket[socketId] = list = new List<LinkViewModel>(2);
        list.Add(l);
    }

    private void RemoveLinkFromSocket(string? socketId, LinkViewModel l)
    {
        if (string.IsNullOrEmpty(socketId)) return;
        if (_linksBySocket.TryGetValue(socketId, out var list))
        {
            list.Remove(l);
            if (list.Count == 0) _linksBySocket.Remove(socketId);
        }
    }

    /// <summary>
    ///  Mark every wire incident on <paramref name="socketId"/>
    /// dirty for the next render-tick recompute, via the per-socket index —
    /// O(incident) instead of NodeView.Pins' prior O(Links) full scan per
    /// socket-row SizeChanged. No-op when the socket carries no wires.
    /// </summary>
    public void MarkLinksDirtyForSocket(string? socketId)
    {
        if (string.IsNullOrEmpty(socketId)) return;
        if (_linksBySocket.TryGetValue(socketId, out var incident) && incident.Count > 0)
        {
            for (int i = 0; i < incident.Count; i++)
                incident[i].MarkPathDirty();
            AnyLinkDirty = true;
        }
    }

    /// <summary>
    /// [arch-perf P1-4] Canvas-level aggregator for per-link path-dirtiness so
    /// the render tick's idle-skip gate can early-out without an O(L) scan of
    /// the link set. Set at the ONLY two places wires are marked dirty for the
    /// render tick — <see cref="MarkLinksDirtyForSocket"/> and the node-translate
    /// link-scan (both route through <see cref="LinkViewModel.MarkPathDirty"/>);
    /// the render tick clears it after a full, non-spilled wire drain and re-sets
    /// it if the drain spills, so a skip never drops a pending recompute.
    /// </summary>
    public bool AnyLinkDirty { get; private set; }
    public void SetAnyLinkDirty() => AnyLinkDirty = true;
    public void ClearAnyLinkDirty() => AnyLinkDirty = false;

    public BulkObservableCollection<NodeViewModel> Nodes { get; }
    public BulkObservableCollection<LinkViewModel> Links { get; }
    public BulkObservableCollection<FrameViewModel> Frames { get; }

    /// <summary>
    /// The graph backing this canvas. Mutating the graph directly is allowed
    /// (e.g. moving Node.Location); call <see cref="OnGraphMutated"/>
    /// afterwards so dependent wires recompute.
    /// </summary>
    public Graph Graph => _graph;

    public string GraphName => _graph.Name ?? string.Empty;

    private string? _loadedFilePath;
    /// <summary>
    /// File path the current graph was loaded from, when applicable. Set
    /// by <c>ArchitectViewModel.Open</c>; null for new / unsaved graphs and
    /// for sub-graphs (macro / process inner graphs that have no
    /// independent file). The canvas uses this to skip the source file
    /// when running the cross-file Event.Trigger/Executor socket sync.
    /// 0.10.0 — TwoWay INPC so the Welcome card hint in LogicCanvasView
    /// re-evaluates when the path flips (Open → Welcome card collapses
    /// even on a graph that loaded with zero nodes).
    /// </summary>
    public string? LoadedFilePath
    {
        get => _loadedFilePath;
        set => SetField(ref _loadedFilePath, value);
    }

    private bool _showGrid = true;
    /// <summary>
    /// Toggle for the canvas background grid (40px world-space spacing,
    /// CoalDivider at low opacity). LogicCanvasView subscribes to PropertyChanged
    /// and flips <c>GridLayer.Visibility</c> to match. The architect.view.showGrid
    /// menu item flips this flag.
    /// </summary>
    public bool ShowGrid
    {
        get => _showGrid;
        set => SetField(ref _showGrid, value);
    }

    public double Zoom
    {
        get => _zoom;
        set
        {
            // Match the existing WinForms canvas's zoom envelope ([0.2, 4.0]).
            double clamped = Math.Clamp(value, 0.2, 4.0);
            SetField(ref _zoom, clamped);
        }
    }

    public double PanX
    {
        get => _panX;
        set => SetField(ref _panX, value);
    }

    public double PanY
    {
        get => _panY;
        set => SetField(ref _panY, value);
    }

    /// <summary>
    /// Currently focused canvas object — drives the right-hand inspector. Either
    /// a <see cref="NodeViewModel"/> or a <see cref="LinkViewModel"/>; null = nothing
    /// selected. For multi-node selection (marquee, Shift+click) inspect
    /// <see cref="SelectedNodes"/>; Selection always points at the *primary* (last
    /// added) node so the inspector has one canonical "what am I looking at".
    /// </summary>
    public object? Selection
    {
        get => _selection;
        set
        {
            if (ReferenceEquals(_selection, value)) return;
            switch (_selection)
            {
                case NodeViewModel oldN: oldN.IsSelected = false; break;
                case LinkViewModel oldL: oldL.IsSelected = false; break;
            }
            _selection = value;
            switch (_selection)
            {
                case NodeViewModel newN:
                    newN.IsSelected = true;
                    // Single-target select also resets the multi-set to just this node.
                    ClearMultiSelection();
                    SelectedNodes.Add(newN);
                    break;
                case LinkViewModel newL:
                    //  Single IsSelected = true. Pre-fix this branch
                    // had two identical assignments — the second was a stale
                    // copy-paste from a code review iteration. The mirror
                    // comment below stays because the COMMENT is what
                    // documents intent ("single-target select also resets the
                    // multi-set to just this link"); the duplicate assignment
                    // didn't.
                    newL.IsSelected = true;
                    ClearMultiSelection();
                    // 0.10.0 — mirror the parallel "single-target select →
                    // multi-set sync" path used for nodes so TotalSelectedCount
                    // and DEL / Cut see the wire even when a user is single-
                    // clicked on it (without going through marquee). The
                    // IsSelected flag was already set above; ClearMultiSelection
                    // never touched newL because it wasn't in SelectedLinks yet.
                    SelectedLinks.Add(newL);
                    break;
                default:
                    ClearMultiSelection();
                    break;
            }
            OnPropertyChanged();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// All currently selected nodes (1+ when multi-select is active, 0 when only
    /// a link or nothing is selected). Drives drag-as-group and DEL-as-many.
    /// </summary>
    public ObservableCollection<NodeViewModel> SelectedNodes { get; } = new();

    /// <summary>
    /// 0.10.0 — wires currently part of the multi-selection. Marquee +
    /// right-click + copy/paste/delete walk this alongside <see cref="SelectedNodes"/>
    /// so a marquee-selected wire (or a Shift+clicked one) participates in
    /// the same DEL-as-many / Ctrl+X / Cut codepath that nodes already do.
    /// Pre-0.10.0 the only way to delete a wire was to single-select it via
    /// <see cref="Selection"/>; multi-select dropped wire entries on the floor.
    /// </summary>
    public ObservableCollection<LinkViewModel> SelectedLinks { get; } = new();

    /// <summary>
    /// 0.10.0 — comment frames currently part of the multi-selection. Same
    /// rationale as <see cref="SelectedLinks"/> — extends DEL / Cut /
    /// right-click branching to frame entries.
    /// </summary>
    public ObservableCollection<FrameViewModel> SelectedFrames { get; } = new();

    /// <summary>
    /// 0.10.0 — true when more than one object (any kind: node / link /
    /// frame) is part of the current multi-selection. Used by the right-click
    /// menus to branch their labels / hide single-only actions when ≥ 2.
    /// </summary>
    public int TotalSelectedCount => SelectedNodes.Count + SelectedLinks.Count + SelectedFrames.Count;

    /// <summary>
    /// Replace the multi-selection with <paramref name="nodes"/>. Updates each
    /// node's IsSelected flag, sets <see cref="Selection"/> to the last entry,
    /// and raises SelectionChanged once.
    /// </summary>
    public void SetMultiSelection(System.Collections.Generic.IEnumerable<NodeViewModel> nodes)
    {
        ClearMultiSelection();
        NodeViewModel? last = null;
        foreach (var n in nodes)
        {
            if (n is null) continue;
            n.IsSelected = true;
            SelectedNodes.Add(n);
            last = n;
        }
        // Set _selection without re-running the multi-clear path.
        if (_selection is LinkViewModel oldL) oldL.IsSelected = false;
        _selection = last;
        OnPropertyChanged(nameof(Selection));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearMultiSelection()
    {
        foreach (var n in SelectedNodes) n.IsSelected = false;
        SelectedNodes.Clear();
        foreach (var l in SelectedLinks) l.IsSelected = false;
        SelectedLinks.Clear();
        foreach (var f in SelectedFrames) f.IsSelected = false;
        SelectedFrames.Clear();
    }

    /// <summary>
    /// 0.10.0 — replace the link half of the multi-selection. Marquee /
    /// Shift+click on a wire write here so the wire participates in the
    /// same DEL / Cut / right-click branching as nodes and frames.
    /// </summary>
    public void SetSelectedLinks(System.Collections.Generic.IEnumerable<LinkViewModel> links)
    {
        foreach (var l in SelectedLinks) l.IsSelected = false;
        SelectedLinks.Clear();
        foreach (var l in links)
        {
            if (l is null) continue;
            l.IsSelected = true;
            SelectedLinks.Add(l);
        }
        OnPropertyChanged(nameof(TotalSelectedCount));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>0.10.0 — replace the frame half of the multi-selection.</summary>
    public void SetSelectedFrames(System.Collections.Generic.IEnumerable<FrameViewModel> frames)
    {
        foreach (var f in SelectedFrames) f.IsSelected = false;
        SelectedFrames.Clear();
        foreach (var f in frames)
        {
            if (f is null) continue;
            f.IsSelected = true;
            SelectedFrames.Add(f);
        }
        OnPropertyChanged(nameof(TotalSelectedCount));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 0.10.0 — full canvas reset for ArchitectViewModel.Open / NewGraph.
    /// Clears every visual collection, drops both single + multi selection,
    /// and resets pan/zoom to identity. The caller is expected to follow
    /// with LoadGraph(newGraph) which restores ViewOffset / Zoom from the
    /// incoming graph's saved viewport. Cleaner than relying on LoadGraph's
    /// implicit clear path because the AVM-shared undo controller's Reset()
    /// and the inspector's SetNode(null) hook off the same boundary.
    /// </summary>
    public void Reset()
    {
        Selection = null;
        ClearMultiSelection();
        //  None of the VMs (NodeViewModel / LinkViewModel /
        // FrameViewModel) implement IDisposable today, so Clear is a clean
        // tear-down — the GC reclaims them once the canvas drops its last
        // reference. If a future per-VM event subscription is added (e.g.
        // a theme-brush PropertyChanged listener on LinkViewModel), this
        // Reset path would silently leak the subscription because nothing
        // here calls .Dispose() on the VMs before clearing. The pattern to
        // adopt is: foreach (var v in Links) (v as IDisposable)?.Dispose();
        // before each Clear(). Same applies to LoadGraph and to Dispose()
        // below. Captured here so the next IDisposable opt-in is caught.
        Nodes.Clear();
        Links.Clear();
        Frames.Clear();
        PanX = 0;
        PanY = 0;
        Zoom = 1.0;
        _pickerVarChainName  = null;
        _hoveredVarChainName = null;
        OnPropertyChanged(nameof(PickerVarChainName));
        OnPropertyChanged(nameof(HoveredVarChainName));
    }

    public event EventHandler? SelectionChanged;

    /// <summary>
    /// Hook the rail's "Publish globally" macro action invokes. The
    /// ArchitectViewModel binds this to its bus client's MACRO_SYNC publisher.
    /// Optional — null when the host (e.g. macro sub-graph editor) doesn't
    /// participate in the global library.
    /// </summary>
    public Action<Macro>? RequestPublishMacroGlobally { get; set; }

    /// <summary>
    /// Optional — sub-graph editor windows set this so their "Reveal call-site"
    /// button can frame the matching Macro.Call / Process.Spawn node back on
    /// the parent canvas. <see cref="LogicCanvasView"/> wires this delegate
    /// in its DataContextChanged handler to a select+frame helper. SubGraphWindow
    /// fires it through the parent ArchitectViewModel's LogicCanvas reference.
    /// </summary>
    public Action<string>? RequestRevealNode { get; set; }

    /// <summary>
    /// Fired at the end of <see cref="LoadGraph"/>. The canvas code-behind
    /// subscribes so it can reset the per-VM undo history — without this,
    /// opening file B after editing file A leaves A's snapshots on the stack
    /// and Ctrl+Z silently restores the previous file.
    /// </summary>
    public event EventHandler? GraphLoaded;

    /// <summary>
    /// Replace the current graph with <paramref name="graph"/> and rebuild the
    /// node/link VM collections. Pan/zoom state restored from
    /// Graph.View* fields if present (existing WinForms canvas does
    /// the same — saved viewport per graph).
    /// </summary>
    /// <param name="wildcardAlreadyResolved">
    ///  When true, skip the wildcard cascade below — the
    /// caller guarantees it has already run (e.g. <c>ArchitectViewModel.OpenAsync</c>,
    /// where <c>GraphSerializer.LoadGraph</c> runs the cascade off-thread after
    /// MigrateNodes). Pre-fix the open path ran the cascade twice: once
    /// off-thread in GraphSerializer, then again here on the UI thread after the
    /// load returned. Skipping the redundant UI-thread pass is the largest
    /// avoidable dispatcher cost on opening a large graph (ARCH-P0-LOADGRAPH-UITHREAD).
    /// Defaults false so NewGraph / undo-replay / SubGraphWindow / headless / test
    /// callers still get the cascade (idempotent + cheap).
    /// </param>
    public void LoadGraph(Graph graph, bool wildcardAlreadyResolved = false)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        Selection = null;

        //  (OWNER-OVERRIDE) Pre-fix every VM was
        // appended one-by-one to the ObservableCollections, firing N separate
        // CollectionChanged(Add) events — each of which the bound canvas turned
        // into a per-node NodeView mount + per-frame FrameView mount + an
        // ItemsControl layout pass for the LinkLayer. For a 100+ node graph that
        // is the largest avoidable dispatcher cost on opening a file (100–500ms
        // UI-thread stall).
        //
        // The VM objects themselves are plain POCOs (NodeViewModel /
        // LinkViewModel / FrameViewModel touch no UI controls in their ctors),
        // so we build the full list up-front in tight local loops, then commit
        // each collection in ONE batch. BulkObservableCollection.ReplaceAll
        // raises a single Reset notification instead of N×Add — the canvas's
        // OnNodesChanged / OnFramesChanged Reset arms (and the LinkLayer
        // ItemsControl) already rebuild from the authoritative collection on
        // Reset, so the end-to-end result is identical with one layout pass.
        var nodeVms = new List<NodeViewModel>(_graph.Nodes.Count);
        foreach (var n in _graph.Nodes)
            nodeVms.Add(new NodeViewModel(n));

        var linkVms = new List<LinkViewModel>(_graph.Links.Count);
        foreach (var link in _graph.Links)
            linkVms.Add(new LinkViewModel(link, _graph));

        var frameVms = new List<FrameViewModel>(_graph.Frames.Count);
        foreach (var f in _graph.Frames)
            frameVms.Add(new FrameViewModel(f));

        // Commit each collection in a single Reset. _nodesById / _linksByNode
        // are rebuilt from the authoritative collection on the Reset arm of
        // OnNodesCollectionChanged / OnLinksCollectionChanged, so the indices
        // are fully built by the time SyncSocketConnectivity reads them.
        Nodes.ReplaceAll(nodeVms);
        Links.ReplaceAll(linkVms);
        Frames.ReplaceAll(frameVms);

        Zoom = _graph.ViewZoom > 0 ? _graph.ViewZoom : 1.0;
        PanX = _graph.ViewOffsetX;
        PanY = _graph.ViewOffsetY;

        SyncSocketConnectivity();

        //  Resolve wildcard sockets (Logic.If A/B group, Reroute
        // chains, etc.) right after the connectivity flag sync. Pre-fix this
        // lived only in LogicCanvasView's GraphLoaded handler (the canvas
        // code-behind) so a headless / test consumer that constructs a
        // LogicCanvasViewModel without the LogicCanvasView never propagated
        // the resolved types — wires loaded from disk could keep an Any
        // wildcard the canvas would have collapsed. Running here makes the
        // VM correct on its own; the canvas's redundant call becomes a no-op
        // (idempotent per NodeRegistry.ResolveWildcardCascade docs).
        //  Skipped when the caller already ran it off-thread.
        if (!wildcardAlreadyResolved)
            try { NodeRegistry.ResolveWildcardCascade(_graph); } catch { /* best effort */ }

        OnPropertyChanged(nameof(Graph));
        OnPropertyChanged(nameof(GraphName));
        GraphLoaded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Recompute every wire's bezier path. Call after a node moves so links
    /// connected to it update in lockstep. O(links) — cheap for typical graphs.
    /// A future optimisation could maintain a per-node link index and recompute
    /// only the links touching the moved node; not needed yet.
    /// </summary>
    public void OnGraphMutated()
    {
        foreach (var l in Links) l.RecomputePath();
        SyncSocketConnectivity();
        GraphMutatedAny?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    ///  Incrementally reflect a small graph edit (a reroute
    /// insertion: one new node + one or two new links, optionally replacing a
    /// removed link) into the canvas VMs WITHOUT a full <see cref="LoadGraph"/>
    /// rebuild. The underlying <see cref="Graph"/> model must already be mutated
    /// by the caller (node + links added/removed). This builds only the new VMs,
    /// appends/removes them via single Add/Remove notifications (one NodeView
    /// mount, no ItemsControl Reset), syncs socket connectivity ONCE, recomputes
    /// only the new links' paths, and raises <c>GraphMutatedAny</c>.
    /// <para>
    /// Replaces the prior <c>LoadGraph(graph) + OnGraphMutated()</c> pair on the
    /// two reroute paths (Y-hotkey mid-drag + right-click-wire "Insert Reroute"),
    /// which rebuilt EVERY node/link/frame VM and walked every socket up to three
    /// times for a 1-node edit — a 100-500 ms UI-thread stall on large graphs
    /// (Majo: "big lag when placing reroute via hotkey").
    /// </para>
    /// </summary>
    /// <returns>The NodeViewModel built for <paramref name="addedNode"/>, or null.</returns>
    public NodeViewModel? ApplyIncrementalReroute(
        Node? addedNode,
        IReadOnlyList<Link>? addedLinks = null,
        IReadOnlyList<Link>? removedLinks = null)
    {
        // Drop VMs for any replaced links first (the wire the reroute splices).
        // The model link was already removed by the caller; this drops the VM.
        if (removedLinks is { Count: > 0 })
        {
            foreach (var rem in removedLinks)
            {
                for (int i = Links.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(Links[i].Model, rem)) { Links.RemoveAt(i); break; }
                }
            }
        }

        NodeViewModel? addedVm = null;
        if (addedNode is not null)
        {
            addedVm = new NodeViewModel(addedNode);
            Nodes.Add(addedVm); // OnNodesCollectionChanged(Add) files _nodesById
        }

        var addedLinkVms = new List<LinkViewModel>(addedLinks?.Count ?? 0);
        if (addedLinks is { Count: > 0 })
        {
            foreach (var link in addedLinks)
            {
                var lvm = new LinkViewModel(link, _graph);
                Links.Add(lvm); // OnLinksCollectionChanged(Add) indexes the link
                addedLinkVms.Add(lvm);
            }
        }

        // The single graph-wide pass — O(sockets + links), the same call
        // LoadGraph and OnGraphMutated each make, but now exactly ONCE instead
        // of three times. Required so the reroute's + source's IsConnected flags
        // (and any evicted wire's) are correct.
        SyncSocketConnectivity();

        // Match LoadGraph (line ~566): resolve wildcard sockets so a reroute
        // spliced onto an unresolved Any wire — or a chain of reroutes — narrows
        // types through the chain. Idempotent + cheap (O(passes×links)); a no-op
        // when the reroute's types are already concrete (the common case, since
        // both callers set the reroute sockets to the source type up front).
        try { NodeRegistry.ResolveWildcardCascade(_graph); } catch { /* best effort */ }

        // Only the new links need a path; nothing else moved.
        foreach (var lvm in addedLinkVms) lvm.RecomputePath();

        _graph.MarkStructuralChange();
        GraphMutatedAny?.Invoke(this, EventArgs.Empty);
        return addedVm;
    }

    /// <summary>
    /// Walk every link and flip each socket's
    /// <see cref="SocketViewModel.IsConnected"/> flag so the required-but-empty
    /// yellow halo (and any future "no connections" affordance) tracks the
    /// graph's actual wiring. Cheap — O(sockets + links) — and called from
    /// <see cref="LoadGraph"/> + every <see cref="OnGraphMutated"/> so the
    /// flag never drifts. Skips the halo allocation if the canvas hasn't
    /// added any nodes yet (post-Dispose / pre-LoadGraph).
    /// </summary>
    public void SyncSocketConnectivity()
    {
        if (Nodes.Count == 0) return;

        // First pass: clear every flag. The link walk only adds positives.
        foreach (var n in Nodes)
        {
            foreach (var s in n.Inputs)  s.IsConnected = false;
            foreach (var s in n.Outputs) s.IsConnected = false;
        }

        // [QC21-09 / ARCH-P1-NODESBYID-CACHE] Pre-fix this loop called FindNode
        // (a linear scan over Nodes) twice per link — O(L · N) total. QC21-09
        // replaced that with a per-call dict rebuild; this now reuses the
        // VM-level _nodesById index (maintained by OnNodesCollectionChanged)
        // so the dictionary isn't reallocated on every connectivity sync
        // (which fires on LoadGraph AND every OnGraphMutated / node drag).
        //
        // Second pass: mark every socket touched by a link as connected.
        foreach (var l in Links)
        {
            if (_nodesById.TryGetValue(l.Model.FromNodeId ?? string.Empty, out var fromNode))
            {
                var s = fromNode.FindSocket(l.Model.FromSocketId);
                if (s is not null) s.IsConnected = true;
            }
            if (_nodesById.TryGetValue(l.Model.ToNodeId ?? string.Empty, out var toNode))
            {
                var s = toNode.FindSocket(l.Model.ToSocketId);
                if (s is not null) s.IsConnected = true;
            }
        }
    }

    // ─── Macro.Call socket sync (WinUI port of Canvas.Macros.cs) ─────────
    //
    // [P1] The WinForms baseline Canvas.Macros.cs kept every Macro.Call node's
    // parameter sockets in lockstep with the referenced macro's Entry/Exit
    // signature via RefreshAllCallNodesForMacro / RefreshMacroCallSockets. The
    // WinUI port had no equivalent, so a Macro.Call node loaded from disk, or
    // left open while a peer edited the macro (MACRO_SYNC), kept showing the
    // stale signature — only the [G] rail prefix updated. These methods restore
    // that path. They are pure graph mutations (model-level); callers rebuild
    // the affected NodeViewModel socket VMs + repaint afterwards.

    private const int MacroCallHeaderH = 24;
    private const int MacroCallRowSpacing = 22;

    /// <summary>
    /// Re-sync every Macro.Call node in the current graph that references
    /// <paramref name="macro"/> (by MacroId attribute) to the macro's current
    /// Entry/Exit signature. Returns the set of mutated node ids so the caller
    /// can rebuild only those NodeViewModels rather than the whole graph.
    /// </summary>
    public HashSet<string> RefreshAllCallNodesForMacro(Macro macro)
    {
        var mutated = new HashSet<string>();
        if (macro is null || string.IsNullOrEmpty(macro.MacroId)) return mutated;
        foreach (var node in _graph.Nodes)
        {
            if (node.Title != "Macro.Call") continue;
            if (node.Attributes is null) continue;
            if (!node.Attributes.TryGetValue("MacroId", out var id) || id != macro.MacroId) continue;
            if (RefreshMacroCallSockets(node, macro)) mutated.Add(node.Id);
        }
        if (mutated.Count > 0) _graph.MarkStructuralChange();
        return mutated;
    }

    /// <summary>
    /// Re-sync a single Macro.Call <paramref name="callNode"/>'s sockets with
    /// <paramref name="macro"/>'s Entry/Exit signature. Sockets matched by
    /// (name, type) keep their Id (and any external links wired to them); only
    /// sockets whose name disappears from the signature are dropped, along with
    /// the links touching them. Returns true if anything changed.
    /// </summary>
    public bool RefreshMacroCallSockets(Node callNode, Macro macro)
    {
        if (callNode is null || macro is null) return false;

        bool changed = false;

        // Keep the displayed name in sync with the macro's current Name.
        callNode.Attributes ??= new Dictionary<string, string>();
        if (!callNode.Attributes.TryGetValue("MacroName", out var curName) || curName != macro.Name)
        {
            callNode.Attributes["MacroName"] = macro.Name;
            changed = true;
        }

        // Derive inputs from Macro.Entry outputs and outputs from Macro.Exit inputs.
        var entryNode = macro.Graph.Nodes.Find(n => n.Title == "Macro.Entry");
        var exitNode  = macro.Graph.Nodes.Find(n => n.Title == "Macro.Exit");

        var entrySockets = new List<Socket>();
        if (entryNode is not null)
            foreach (var s in entryNode.Sockets)
                if (s.Type == SocketType.Output && !s.IsPlaceholder && s.Name != "Flow")
                    entrySockets.Add(s);

        var exitSockets = new List<Socket>();
        if (exitNode is not null)
            foreach (var s in exitNode.Sockets)
                if (s.Type == SocketType.Input && !s.IsPlaceholder && s.Name != "Flow")
                    exitSockets.Add(s);

        var desiredInputNames  = new HashSet<string>();
        foreach (var s in entrySockets) desiredInputNames.Add(s.Name);
        var desiredOutputNames = new HashSet<string>();
        foreach (var s in exitSockets)  desiredOutputNames.Add(s.Name);

        // Step 1: drop ONLY the data sockets whose names left the signature,
        // plus the links touching them. Flow + still-present sockets survive
        // along with their external wires.
        var droppedSocketIds = new HashSet<string>();
        foreach (var s in callNode.Sockets)
        {
            if (s.Name == "Flow") continue;
            bool keep =
                (s.Type == SocketType.Input  && desiredInputNames.Contains(s.Name)) ||
                (s.Type == SocketType.Output && desiredOutputNames.Contains(s.Name));
            if (!keep) droppedSocketIds.Add(s.Id);
        }
        if (droppedSocketIds.Count > 0)
        {
            // [ARCH-FREEZE / ARCH-P1-TRANSLATENODE-LINKSCAN] Remove the doomed
            // wires through the VM Links collection FIRST so OnLinksCollectionChanged
            // fires per-link and keeps _linksByNode / _linksBySocket in lockstep —
            // a bare _graph.Links.RemoveAll bypasses the VM collection and leaves
            // those indices pointing at deleted socket ids, so a later
            // MarkLinksDirtyForSocket / TranslateNode would touch stale links.
            // Removing the VM (whose .Model is the same Link instance) does NOT
            // mutate _graph.Links, so the model RemoveAll below still runs to
            // prune any model-only links (defensive — should be the same set).
            var doomed = new List<LinkViewModel>();
            foreach (var lvm in Links)
                if (droppedSocketIds.Contains(lvm.Model.FromSocketId)
                    || droppedSocketIds.Contains(lvm.Model.ToSocketId))
                    doomed.Add(lvm);
            foreach (var lvm in doomed)
                Links.Remove(lvm);
            _graph.Links.RemoveAll(l =>
                droppedSocketIds.Contains(l.FromSocketId) || droppedSocketIds.Contains(l.ToSocketId));
            callNode.Sockets.RemoveAll(s => droppedSocketIds.Contains(s.Id));
            changed = true;
        }

        // Step 2: ensure every signature socket exists; matched ones keep Id,
        // missing ones are appended. Color/DataType/Offset are refreshed either
        // way so a type change on an Entry socket propagates to the Call node.
        for (int i = 0; i < entrySockets.Count; i++)
        {
            var src = entrySockets[i];
            var existing = callNode.Sockets.Find(s => s.Type == SocketType.Input && s.Name == src.Name);
            var off = new System.Drawing.Point(-6, MacroCallHeaderH + 6 + (i + 1) * MacroCallRowSpacing);
            if (existing is not null)
            {
                if (existing.Color != src.Color || existing.DataType != src.DataType || existing.Offset != off)
                    changed = true;
                existing.Color    = src.Color;
                existing.DataType = src.DataType;
                existing.Offset   = off;
            }
            else
            {
                callNode.Sockets.Add(new Socket
                {
                    Name     = src.Name,
                    Type     = SocketType.Input,
                    Color    = src.Color,
                    DataType = src.DataType,
                    Offset   = off,
                });
                changed = true;
            }
        }

        for (int i = 0; i < exitSockets.Count; i++)
        {
            var src = exitSockets[i];
            var existing = callNode.Sockets.Find(s => s.Type == SocketType.Output && s.Name == src.Name);
            var off = new System.Drawing.Point(callNode.Size.Width - 14, MacroCallHeaderH + 6 + (i + 1) * MacroCallRowSpacing);
            if (existing is not null)
            {
                if (existing.Color != src.Color || existing.DataType != src.DataType || existing.Offset != off)
                    changed = true;
                existing.Color    = src.Color;
                existing.DataType = src.DataType;
                existing.Offset   = off;
            }
            else
            {
                callNode.Sockets.Add(new Socket
                {
                    Name     = src.Name,
                    Type     = SocketType.Output,
                    Color    = src.Color,
                    DataType = src.DataType,
                    Offset   = off,
                });
                changed = true;
            }
        }

        int totalRows = Math.Max(1 + entrySockets.Count, 1 + exitSockets.Count);
        var newSize = new System.Drawing.Size(callNode.Size.Width, MacroCallHeaderH + 14 + totalRows * MacroCallRowSpacing);
        if (callNode.Size != newSize)
        {
            callNode.Size = newSize;
            changed = true;
        }

        if (changed) _graph.MarkStructuralChange();
        return changed;
    }

    // ─── Drop-target halo hinting ────────────────────────────────────────
    //
    // Driven by the canvas pointer / wire-drag layer: when a wire is being
    // dragged from a source socket, BeginDropHinting walks every socket VM
    // and flips its DropState to Valid / Invalid based on compat with the
    // source's data type + direction. EndDropHinting clears the lot when
    // the drag completes or is cancelled. The halo overlay in NodeView.xaml
    // binds to SocketViewModel.DropState through the DropStateToHaloBrush
    // / DropStateToHaloVisibility converters, so once these helpers fire
    // the visible feedback follows without any view-side coordination.

    /// <summary>
    /// Hint every socket on the canvas with whether it's a valid drop target
    /// for a wire being dragged from <paramref name="source"/>. Sets each
    /// socket's <see cref="SocketViewModel.DropState"/> to <c>Valid</c>,
    /// <c>Invalid</c>, or <c>None</c> per the compat rules:
    ///   * source's own socket: None (you can't drop on yourself)
    ///   * same direction as source (output ↔ output, input ↔ input): None
    ///   * same node as source: None
    ///   * type incompatible via <c>NodeRegistry.AreCompatible</c>: Invalid
    ///   * otherwise: Valid.
    /// Safe to call repeatedly during a drag (e.g. on socket-hover changes);
    /// the underlying socket VMs short-circuit no-op assignments.
    /// </summary>
    public void BeginDropHinting(SocketViewModel source)
    {
        if (source is null) { EndDropHinting(); return; }
        var sourceType = source.DataType;
        var sourceDir  = source.Direction;
        var sourceNode = source.ParentNode;
        foreach (var n in Nodes)
        {
            bool sameNode = ReferenceEquals(n.Model, sourceNode);
            foreach (var s in n.Inputs)  s.DropState = ResolveDropState(s, sourceType, sourceDir, sameNode);
            foreach (var s in n.Outputs) s.DropState = ResolveDropState(s, sourceType, sourceDir, sameNode);
        }
    }

    private static DropState ResolveDropState(
        SocketViewModel candidate,
        SocketDataType sourceType,
        SocketType sourceDir,
        bool sameNode)
    {
        // Same-node drops are not a wire — exclude even if types match.
        if (sameNode) return DropState.None;
        // Wires connect Output → Input only. Same-direction targets are not
        // candidates and should stay None (no halo).
        if (candidate.Direction == sourceDir) return DropState.None;
        // Type compat — NodeRegistry already handles Any-wildcard + Int↔Float widening.
        return NodeRegistry.AreCompatible(sourceType, candidate.DataType)
            ? DropState.Valid
            : DropState.Invalid;
    }

    /// <summary>
    /// Clear every socket's <see cref="SocketViewModel.DropState"/> back to
    /// <c>None</c>. Called at the end of a wire-drag (drop, cancel, or
    /// Esc) so the green / red halo affordance disappears.
    /// </summary>
    public void EndDropHinting()
    {
        foreach (var n in Nodes)
        {
            foreach (var s in n.Inputs)  s.DropState = DropState.None;
            foreach (var s in n.Outputs) s.DropState = DropState.None;
        }
    }

    /// <summary>
    /// Fires whenever <see cref="OnGraphMutated"/> is called. ArchitectViewModel
    /// subscribes to flip dirty state.
    /// </summary>
    public event EventHandler? GraphMutatedAny;

    /// <summary>
    /// Translate a node by (dx, dy) and ripple the change into every wire
    /// touching it. The single canonical entry point for drag handlers — keep
    /// callers off NodeViewModel.Translate so wire recompute can't be skipped.
    /// </summary>
    public void TranslateNode(NodeViewModel node, double dx, double dy)
    {
        if (node is null) return;
        node.Translate(dx, dy);
        // 0.10.0 (arch-perf P1) — frame-coalesce wire recompute. The actual
        // bezier rebuild happens on the canvas's CompositionTarget.Rendering
        // tick (LogicCanvasView.OnRenderingTick) so a 120 Hz pointer-move
        // burst collapses into one rebuild per displayed frame instead of
        // running RecomputePath per move event.
        //  Mark only the wires actually touching
        // this node dirty via the per-node incident-link index — O(incident)
        // instead of an O(L) scan of the whole Links collection per moved node
        // (the prior loop was O(N x L) for an N-node group drag at 120 Hz).
        if (_linksByNode.TryGetValue(node.Id, out var incident) && incident.Count > 0)
        {
            // Local copy guard: MarkPathDirty doesn't mutate the collection,
            // but a defensive snapshot-free foreach over the index list is safe
            // because no link add/remove happens inside this loop.
            for (int i = 0; i < incident.Count; i++)
                incident[i].MarkPathDirty();
            AnyLinkDirty = true; // [arch-perf P1-4] feed the render-tick idle-skip gate
        }
    }

    /// <summary>
    /// [resize-reanchor] Mark every wire incident on <paramref name="nodeId"/>
    /// dirty for the next render-tick recompute. A node that grows / shrinks to
    /// fit its content (pill edit, socket add/remove, dynamic event-pair grow)
    /// moves its pins to new edge coordinates; <see cref="TranslateNode"/> is the
    /// only other dirty trigger and it fires on MOVE, not RESIZE — so without
    /// this the cached wire anchors stayed at the pre-resize pin positions and
    /// the wires visibly detached from the pins ("bubbles outside the node")
    /// until the node was next dragged. Mirrors the per-node incident-link scan
    /// in <see cref="TranslateNode"/>; O(incident), no-op when the node is wireless.
    /// </summary>
    public void MarkNodeLinksDirty(string? nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return;
        if (_linksByNode.TryGetValue(nodeId, out var incident) && incident.Count > 0)
        {
            for (int i = 0; i < incident.Count; i++)
                incident[i].MarkPathDirty();
            AnyLinkDirty = true;
        }
    }

    /// <summary>Returns the node VM at id, or null if unknown.</summary>
    /// <remarks>
    ///  O(1) lookup against the VM-level id→VM index
    /// (maintained by <see cref="OnNodesCollectionChanged"/>) instead of the
    /// prior O(n) linear scan over <see cref="Nodes"/>. FindNode is called from
    /// the debug-flash dispatch (one lookup per DEBUG_NODE_EXEC) and reveal-node
    /// paths, so the linear scan showed up under tight debug-trace loops.
    /// </remarks>
    public NodeViewModel? FindNode(string nodeId)
        => string.IsNullOrEmpty(nodeId) ? null
         : _nodesById.TryGetValue(nodeId, out var vm) ? vm : null;

    private string? _pickerVarChainName;
    /// <summary>
    /// Sticky var-chain picker name. When set, the canvas dims every node
    /// that isn't a writer or reader of <c>{name}</c> and overlays the
    /// cyan / amber halo on the chain members. Pre-T15 SetVarChainPicker;
    /// fired by the pill menu's "Trace Variable…" / "Pin Variable to canvas"
    /// items and cleared on Esc.
    /// </summary>
    public string? PickerVarChainName
    {
        get => _pickerVarChainName;
        set
        {
            if (string.Equals(_pickerVarChainName, value, System.StringComparison.OrdinalIgnoreCase)) return;
            _pickerVarChainName = string.IsNullOrEmpty(value) ? null : value;
            ApplyVarChainHighlights();
            OnPropertyChanged();
        }
    }

    private string? _hoveredVarChainName;
    /// <summary>
    /// Transient var-chain hover name — set by NodeView when the cursor
    /// enters a value pill carrying a <c>{var}</c> token; cleared on
    /// PointerExited. Mirrors pre-T15 _hoveredVarChainName. Unions with
    /// <see cref="PickerVarChainName"/> so a hovered chain stays visible
    /// when the user has also pinned one.
    /// </summary>
    public string? HoveredVarChainName
    {
        get => _hoveredVarChainName;
        set
        {
            if (string.Equals(_hoveredVarChainName, value, System.StringComparison.OrdinalIgnoreCase)) return;
            _hoveredVarChainName = string.IsNullOrEmpty(value) ? null : value;
            ApplyVarChainHighlights();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Re-walk every node and flip its <see cref="NodeViewModel.IsVarChainWriter"/>
    /// / <see cref="NodeViewModel.IsVarChainReader"/> / <see cref="NodeViewModel.IsDimmedByPicker"/>
    /// flags so NodeView's border-priority cascade re-paints. Cheap — runs
    /// only when picker / hover names change.
    /// </summary>
    private void ApplyVarChainHighlights()
    {
        // [freeze-diagnostics] Untraced UI-thread hot path: fires on every
        // var-token pill hover-change and runs VarChainAnalyzer.Analyze
        // (O(nodes) + a regex per attribute) plus an O(nodes) flag sweep. On a
        // large graph this is a real per-hover cost during "clicking around", so
        // breadcrumb it — if the next freeze lands here, the watchdog log names
        // this instead of the generic render-tick tail.
        using var _trace = Phoenix.Controls.Shared.Services.UiActivityTrace
            .Begin("Architect.VarChainHighlight");
        var hover  = _hoveredVarChainName;
        var picker = _pickerVarChainName;

        System.Collections.Generic.HashSet<string>? writerIds = null;
        System.Collections.Generic.HashSet<string>? readerIds = null;
        System.Collections.Generic.HashSet<string>? pickerWriters = null;
        System.Collections.Generic.HashSet<string>? pickerReaders = null;

        if (!string.IsNullOrEmpty(hover))
        {
            var trace = Phoenix.Controls.Architect.Core.VarChainAnalyzer.Analyze(_graph, hover);
            writerIds = new(trace.Writers.Select(n => n.Id));
            readerIds = new(trace.Readers.Select(n => n.Id));
        }
        if (!string.IsNullOrEmpty(picker))
        {
            var trace = Phoenix.Controls.Architect.Core.VarChainAnalyzer.Analyze(_graph, picker);
            pickerWriters = new(trace.Writers.Select(n => n.Id));
            pickerReaders = new(trace.Readers.Select(n => n.Id));
        }

        foreach (var n in Nodes)
        {
            bool isWriter = (writerIds?.Contains(n.Id) ?? false)
                         || (pickerWriters?.Contains(n.Id) ?? false);
            bool isReader = (readerIds?.Contains(n.Id) ?? false)
                         || (pickerReaders?.Contains(n.Id) ?? false);
            bool dim = picker is not null
                    && !(pickerWriters?.Contains(n.Id) ?? false)
                    && !(pickerReaders?.Contains(n.Id) ?? false);
            n.IsVarChainWriter  = isWriter;
            n.IsVarChainReader  = isReader;
            n.IsDimmedByPicker  = dim;
        }
    }

    // ─── IDisposable ─────────────────────────────────────────────────────
    //
    // Today the VM owns no INBOUND managed event subscriptions, but it
    // exposes OUTBOUND events (SelectionChanged / GraphLoaded / GraphMutatedAny
    // / RequestPublishMacroGlobally / RequestRevealNode) that the canvas /
    // ArchitectViewModel / SubGraphWindow subscribe to. Even though both
    // Architect main + SubGraphWindow tear down the VM when the canvas
    // tears down,  nulling the event fields on Dispose breaks any
    // accidental retention path: a delegate subscriber that forgot to
    // unsubscribe would otherwise keep a reference back to this VM (and
    // through it, every visual VM / cached brush / etc.) alive past
    // Dispose. The opposite leak direction from QC20-04 — that's about
    // subscribers holding the VM; this is about the VM holding subscribers.
    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        //  Unhook the index maintainer before the
        // final Clear so the VM doesn't hold a live handler on its own
        // collection past disposal.
        Nodes.CollectionChanged -= OnNodesCollectionChanged;
        Links.CollectionChanged -= OnLinksCollectionChanged;
        Nodes.Clear();
        Links.Clear();
        Frames.Clear();
        _nodesById.Clear();
        _linksByNode.Clear();
        _linksBySocket.Clear();
        _selection = null;

        //  Null every outbound delegate field so a subscriber that
        // forgot to unhook can't keep this VM alive through its handler.
        // Setting an event field from inside the declaring class is the
        // canonical "unsubscribe everyone" idiom in .NET. Done after the
        // collection clears so no late SelectionChanged fires for a
        // half-disposed VM.
        SelectionChanged             = null;
        GraphLoaded                  = null;
        GraphMutatedAny              = null;
        RequestPublishMacroGlobally  = null;
        RequestRevealNode            = null;
    }
}

/// <summary>
///  ObservableCollection that can replace its
/// entire contents in a single batch, raising ONE Reset notification instead
/// of N×Add. Used by <see cref="LogicCanvasViewModel.LoadGraph"/> so opening a
/// large graph mounts the canvas in one layout pass rather than per-node /
/// per-frame. IS-A ObservableCollection so every binding / iteration / Reset
/// handler that already exists keeps working unchanged.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public BulkObservableCollection() { }

    /// <summary>
    /// Clear the collection and append every item in <paramref name="items"/>,
    /// raising exactly one CollectionChanged(Reset) + the Count/Item[] property
    /// changes at the end. Consumers that rebuild from the authoritative
    /// collection on Reset (the canvas node/frame layers, the LinkLayer
    /// ItemsControl) see the final state in a single pass.
    /// </summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        if (items is not null)
            foreach (var item in items)
                Items.Add(item);

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
