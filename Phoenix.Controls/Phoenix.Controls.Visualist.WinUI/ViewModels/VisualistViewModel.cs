using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.WinUI.Contracts;
using Phoenix.Controls.Visualist.Core;
using Phoenix.Controls.Visualist.WinUI.Models;

namespace Phoenix.Controls.Visualist.WinUI.ViewModels;

/// <summary>
/// VisualistViewModel — single source of truth for the WinUI Visualist shell.
/// Owns a <see cref="LayerDocument"/> backed by the engine library, surfaces
/// the Layer / widget / trigger selection state to the chrome,
/// LayerRail, LayerCanvasView, and WidgetEditorView via PropertyChanged.
///
/// File commands (New/Open/Save) live here as plain methods — the chrome
/// raises events that MainWindow translates into picker calls and forwards
/// here, so this VM has no direct WinUI dependency on Window/HWND.
/// </summary>
public sealed class VisualistViewModel : INotifyPropertyChanged, IDisposable
{
    public ObservableCollection<LayerListItem> Layers { get; } = new();

    private LayerDocument? _document;

    // Per-layer document cache so the undo / redo stack survives a layer
    // round-trip (undo was dropped on every layer switch otherwise). Keyed by
    // absolute path. LRU-bounded at 5 entries — past that,
    // the oldest layer's in-memory document is dropped (any saved file on
    // disk is unaffected; a re-select re-loads from disk into a fresh
    // document with empty undo).
    private const int LayerCacheCap = 5;
    private readonly Dictionary<string, LayerDocument> _layerCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _layerCacheLru = new();

    // Auto-save-on-edit preference bridge. Subscribed in the ctor so a
    // runtime flip of VisualistUserConfig.AutoSyncOnEdit re-applies to the live
    // document + every cached document; dropped in Dispose.
    private readonly Action _onUserConfigChanged;

    // Re-entrancy guard for the synthetic "(unsaved)" rail row.
    // Selecting that row sets SelectedLayerItem, whose setter calls
    // LoadSelectedLayer → BindDocumentForPath. For a real file that's correct,
    // but the synthetic row has NO file (empty Path) and stands in for the
    // already-bound in-memory document — reloading it would either clobber the
    // doc or (worse) loop. We set the flag while programmatically selecting the
    // synthetic row so LoadSelectedLayer no-ops, and LoadSelectedLayer also
    // independently no-ops on any IsUnsaved row (belt-and-suspenders).
    private bool _settingSyntheticRow;

    public VisualistViewModel()
    {
        _onUserConfigChanged = OnUserConfigChanged;
        try { Phoenix.Controls.Visualist.WinUI.Core.VisualistUserConfig.Instance.OnChanged += _onUserConfigChanged; }
        catch (Exception ex) { GlobalLogger.Error("VisualistViewModel", "ctor config subscribe", ex); }
    }

    // Push the auto-save preference onto a document as it's bound.
    private static void ApplyUserPrefs(LayerDocument doc)
    {
        try { doc.AutoSyncEnabled = Phoenix.Controls.Visualist.WinUI.Core.VisualistUserConfig.Instance.AutoSyncOnEdit; }
        catch (Exception ex) { GlobalLogger.Error("VisualistViewModel", "ApplyUserPrefs", ex); }
    }

    // Re-apply the flag when the user toggles it at runtime. Touches only
    // a bool on each document (no UI), so it's safe off the UI thread.
    private void OnUserConfigChanged()
    {
        bool flag;
        try { flag = Phoenix.Controls.Visualist.WinUI.Core.VisualistUserConfig.Instance.AutoSyncOnEdit; }
        catch { return; }
        if (_document is { } d) { try { d.AutoSyncEnabled = flag; } catch { } }
        foreach (var cached in _layerCache.Values)
        {
            if (ReferenceEquals(cached, _document)) continue;
            try { cached.AutoSyncEnabled = flag; } catch { }
        }
    }

    /// <summary>The mutable document for the currently-loaded layer (null when nothing is open).</summary>
    public LayerDocument? Document
    {
        get => _document;
        private set
        {
            // Same-value race guard. Unsubscribing BEFORE Set() decided
            // whether the value changed meant a duplicate-value assignment
            // (Document = sameDoc) dropped the OnChanged subscription and never
            // re-added it (Set returns false → the re-subscribe block is skipped),
            // leaving the live document silently un-wired. Only touch the
            // subscription when the reference actually changes.
            if (ReferenceEquals(_document, value)) return;

            if (_document is { } old) old.OnChanged -= OnDocumentChanged;
            if (Set(ref _document, value))
            {
                if (value is { } d)
                {
                    d.OnChanged += OnDocumentChanged;
                    // Apply the auto-save-on-edit preference to every document
                    // as it's bound (the single chokepoint covering New / Open / rail
                    // select / SaveAs paths). Pre-fix nothing ever set AutoSyncEnabled,
                    // so the engine's tested auto-save never fired from the UI.
                    ApplyUserPrefs(d);
                    // Remember the last-open saved layer so the rail can restore it
                    // on next startup instead of always opening the alphabetically
                    // first file. Only real (path-backed) documents update the memo;
                    // a path-less scratch doc leaves the prior value intact. SaveAs
                    // path-assignment on the SAME instance doesn't re-enter this
                    // setter, so SaveLayerAs / RenameLayer persist explicitly.
                    if (d.FilePath is { } dp) PersistLastLayerPath(dp);
                }
                OnPropertyChanged(nameof(SelectedLayer));
                OnPropertyChanged(nameof(IsDirty));
                OnPropertyChanged(nameof(ActiveLayerFileName));
                // A graph rebind invalidates any inspector node selection;
                // the new document owns different node instances.
                SelectedNode = null;
                SelectedWidget = value?.Layer.Widgets.FirstOrDefault();

                // Keep the rail in sync with the bound document's
                // saved/unsaved state. A document with no FilePath has no row in
                // Layers (which only enumerates disk files), so the rail would be
                // empty on an installed build whose data dir holds no .phxlayer —
                // even though a default layer is open. Synthesize a "(unsaved)" row
                // at the top and select it. A document WITH a FilePath already has
                // (or will have, via RefreshLayers) a real disk row, so drop any
                // leftover synthetic row.
                SyncSyntheticRow(value);
            }
        }
    }

    // Ensure exactly one synthetic "(unsaved)" row exists at the
    // top of Layers iff the bound document is in-memory only (no FilePath).
    // Selecting it is done under _settingSyntheticRow so LoadSelectedLayer
    // doesn't try to reload the (path-less) row from disk.
    private void SyncSyntheticRow(LayerDocument? doc)
    {
        // Remove any prior synthetic rows first — there must never be more than one,
        // and a doc that now has a FilePath should leave NO synthetic row behind.
        for (int i = Layers.Count - 1; i >= 0; i--)
            if (Layers[i].IsUnsaved)
                Layers.RemoveAt(i);

        if (doc is null || doc.FilePath is not null) return;

        var row = LayerListItem.CreateUnsaved(
            doc.Layer.Name,
            doc.Layer.Resolution?.Width  ?? 0,
            doc.Layer.Resolution?.Height ?? 0);
        Layers.Insert(0, row);

        // Programmatic select via the backing field (Set) so the public
        // SelectedLayerItem setter's LoadSelectedLayer side effect doesn't run —
        // the document is already bound and the row has no file to reload from.
        _settingSyntheticRow = true;
        try { Set(ref _selectedLayerItem, row, nameof(SelectedLayerItem)); }
        finally { _settingSyntheticRow = false; }
        OnPropertyChanged(nameof(ActiveLayerFileName));
    }

    private void OnDocumentChanged()
    {
        OnPropertyChanged(nameof(SelectedLayer));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(ActiveLayerFileName));
    }

    private LayerListItem? _selectedLayerItem;
    public LayerListItem? SelectedLayerItem
    {
        get => _selectedLayerItem;
        set
        {
            if (Set(ref _selectedLayerItem, value))
                LoadSelectedLayer();
        }
    }

    /// <summary>The current layer (or null). Backed by <see cref="Document"/>.</summary>
    public Layer? SelectedLayer => _document?.Layer;

    private LayerWidget? _selectedWidget;
    public LayerWidget? SelectedWidget
    {
        get => _selectedWidget;
        set
        {
            if (Set(ref _selectedWidget, value))
            {
                // Switching widgets switches graphs; drop the stale
                // inspector node selection so it can't bind to a node from the
                // previous widget's graph.
                SelectedNode = null;
                RebuildTriggerNames();
                OnPropertyChanged(nameof(ActiveTriggerObject));
                OnPropertyChanged(nameof(ActiveTriggerDurationLabel));
            }
        }
    }

    // ─── multi-selection bridge (canvas → inspector) ─────────────────────
    //
    // The layer canvas owns the authoritative multi-select set (a view-side
    // HashSet); it publishes the current visually-selected widgets here so the
    // inspector can drive mixed-value / apply-to-all editing. SelectedWidget
    // stays the single "primary/anchor" (editor title, node graph, single-edit);
    // SelectedWidgets mirrors what's highlighted on the canvas, in layer
    // Z-order, and ALWAYS includes the anchor (matches BuildView's IsSelected
    // rule). Count <= 1 → the inspector stays in single-edit mode.
    private IReadOnlyList<LayerWidget> _selectedWidgets = System.Array.Empty<LayerWidget>();
    public IReadOnlyList<LayerWidget> SelectedWidgets => _selectedWidgets;

    /// <summary>True when 2+ widgets are selected — the inspector switches to
    /// mixed-value / apply-to-all mode.</summary>
    public bool HasMultiSelection => _selectedWidgets.Count > 1;

    /// <summary>Canvas hook: replace the published multi-selection. Idempotent —
    /// a sequence-equal set is a no-op so republishing on every render / selection
    /// change never spams the inspector's refresh.</summary>
    public void SetSelectedWidgets(IReadOnlyList<LayerWidget>? widgets)
    {
        widgets ??= System.Array.Empty<LayerWidget>();
        if (SelectedWidgetsEqual(_selectedWidgets, widgets)) return;
        _selectedWidgets = widgets;
        OnPropertyChanged(nameof(SelectedWidgets));
        OnPropertyChanged(nameof(HasMultiSelection));
    }

    private static bool SelectedWidgetsEqual(IReadOnlyList<LayerWidget> a, IReadOnlyList<LayerWidget> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!ReferenceEquals(a[i], b[i])) return false;
        return true;
    }

    public ObservableCollection<string> TriggerNames { get; } = new();

    private string _activeTrigger = "onStartup";
    public string ActiveTrigger
    {
        get => _activeTrigger;
        set
        {
            if (Set(ref _activeTrigger, value))
            {
                OnPropertyChanged(nameof(ActiveTriggerObject));
                OnPropertyChanged(nameof(ActiveTriggerDurationLabel));
                // The per-param keyframe state is read off the active
                // trigger's timeline; re-evaluate it against the new trigger so
                // the inspector diamonds reflect the right track set.
                RefreshNodeParamAnimatedState();
            }
        }
    }

    public WidgetTrigger? ActiveTriggerObject =>
        _selectedWidget?.Triggers.FirstOrDefault(t =>
            string.Equals(t.Name, _activeTrigger, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Re-fires the property-changed signals that drive the timeline editor's
    /// repaint. Called by callers that mutate the active trigger's
    /// <see cref="WidgetTimeline"/> in place — e.g. the right-click pin →
    /// Animate gesture in WidgetGraphCanvas — so the keyframe
    /// strip and duration label refresh without forcing a full Document
    /// rebind.
    /// </summary>
    public void NotifyActiveTriggerChanged()
    {
        OnPropertyChanged(nameof(ActiveTriggerObject));
        OnPropertyChanged(nameof(ActiveTriggerDurationLabel));
    }

    // ─── Typed per-node inspector ────────────────────────────────────────
    //
    // The widget-graph canvas raises OnSelectedNodeChanged; WidgetEditorView
    // routes that here. Setting SelectedNode rebuilds the typed
    // parameter list (SelectedNodeParams) the Inspector's NODE section binds
    // to. Selecting nothing clears the list. Every NodeParamVm.Commit and the
    // per-param keyframe cluster route back through this VM's single
    // attribute-persist / animate helpers (CommitNodeAttribute /
    // ToggleParamKeyframeAtPlayhead) so there is exactly ONE undo+dirty path —
    // mirroring the canvas inline-pill commit (OnNodeAttributeCommitted) and
    // the canvas right-click Animate gesture (AnimatedPinRegistry +
    // AnimateParameter). No parallel persistence path.

    private Node? _selectedNode;

    /// <summary>
    /// The widget-graph node currently selected on the canvas (null when none).
    /// Setting it rebuilds <see cref="SelectedNodeParams"/> for the Inspector's
    /// NODE section. Purely an editor-session value — not persisted.
    /// </summary>
    public Node? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (Equals(_selectedNode, value)) return;
            _selectedNode = value;
            // Order matters. The Inspector's SelectedNode handler
            // (InspectorPanel.RefreshNodeForm) reads SelectedNodeParams
            // SYNCHRONOUSLY the instant PropertyChanged fires. The old
            // `Set(...) ? BuildNodeParams` shape notified FIRST, so the
            // Inspector saw a still-empty/stale list and rendered "no editable
            // parameters" on every node — then BuildNodeParams populated the
            // list a beat later with nothing listening. Build FIRST, notify
            // AFTER. (Majo's recurring "Inspector shows no params" report.)
            BuildNodeParams(value);
            OnPropertyChanged(nameof(SelectedNode));
        }
    }

    /// <summary>
    /// Typed, two-way-bindable parameters for <see cref="SelectedNode"/>.
    /// Empty when no node is selected. Rebuilt on every SelectedNode change.
    /// </summary>
    public ObservableCollection<NodeParamVm> SelectedNodeParams { get; } = new();

    /// <summary>
    /// Short description for the Inspector NODE header. Three rungs, in order:
    /// the curated <see cref="WidgetNodeRegistry"/> tooltip when the template has
    /// one, else the node's per-instance Description override, else its Category.
    ///
    /// <para>V14 wired this getter into the header. It had zero readers, which is why
    /// the ~60 curated tooltips — including Caption.LiveCaption's single-stream
    /// conflict WARNING and String.Select's "When is a VALUE here, not a KEY" —
    /// reached no INSPECTOR surface. Not "no surface at all": the node-documentation
    /// window resolves its summary as <c>WidgetNodeProse.Summary ??
    /// WidgetNodeRegistry.GetTooltip(title) ?? title</c>
    /// (<c>WidgetNodeReferenceData.BuildNode</c>), so a curated template with no prose
    /// entry already showed its tooltip there. The Inspector header is the surface that
    /// showed none of them, and for the templates that DO have a prose entry
    /// (Caption.LiveCaption among them) it is the only outlet the tooltip wording has.</para>
    ///
    /// <para>★ The Category rung is load-bearing and must not be dropped as
    /// "redundant". It is what the header rendered BEFORE V14, and it is all an
    /// uncurated template has: <c>WidgetNodeRegistry.Instantiate</c> copies Title /
    /// Category / sockets / DefaultAttributes but NOT the template Description, so
    /// a freshly placed node's Description is always empty. Without this rung the
    /// header would go blank for every node outside the curated set — a regression
    /// dressed as a feature. The tooltip is an ADDITION on top, not a swap.</para>
    /// </summary>
    public string SelectedNodeDescription
    {
        get
        {
            if (_selectedNode is null) return string.Empty;
            // Keyed by canonical template Title — the same key WidgetNodeRegistry
            // stores tooltips under, and the same value Instantiate stamps onto
            // Node.Title (both lookups are OrdinalIgnoreCase). A key mismatch here
            // would be invisible: every node would silently fall through to its
            // Category and the wire would look like it worked.
            string? tip = WidgetNodeRegistry.GetTooltip(_selectedNode.Title);
            if (!string.IsNullOrWhiteSpace(tip)) return tip!;
            if (!string.IsNullOrWhiteSpace(_selectedNode.Description)) return _selectedNode.Description;
            return _selectedNode.Category ?? string.Empty;
        }
    }

    /// <summary>
    /// Raised after a NodeParamVm commits an attribute (or toggles a keyframe)
    /// so the host can rebuild the canvas node body + refresh its preview
    /// thumbnail. Carries the mutated node. The persistence (PushUndo / write /
    /// MarkDirty) already happened in <see cref="CommitNodeAttribute"/> /
    /// <see cref="ToggleParamKeyframeAtPlayhead"/>; this is the visual-refresh signal
    /// only — wired by WidgetEditorView, kept Visualist-local.
    /// </summary>
    public event Action<Node>? NodeParamCommitted;

    /// <summary>
    /// Single attribute-persist chokepoint shared by every NodeParamVm.Commit.
    /// Mirrors the canvas inline-pill commit (<c>OnNodeAttributeCommitted</c>):
    /// PushUndo (one entry per edit gesture) → write the raw value → MarkDirty,
    /// then signals a visual refresh. No-ops when the value is unchanged so a
    /// focus-out with no edit doesn't spend an undo slot. Returns true when a
    /// write actually happened. MarkDirty's OnChanged already re-raises
    /// SelectedLayer, so no explicit re-raise is needed here.
    /// </summary>
    public bool CommitNodeAttribute(Node node, string attrKey, string newValue)
    {
        if (_document is null || node is null || string.IsNullOrEmpty(attrKey)) return false;
        try
        {
            node.Attributes ??= new Dictionary<string, string>();
            string original = node.Attributes.TryGetValue(attrKey, out var v) ? (v ?? "") : "";
            if (string.Equals(original, newValue, StringComparison.Ordinal)) return false;
            // Inside a slider-drag gesture only the FIRST change snapshots undo
            // (PushUndo is a whole-layer serialize) so the drag lands as ONE
            // undo entry; the MarkDirty is deferred to EndNodeAttributeGesture.
            if (!_attrGestureActive || !_attrGestureUndoPushed)
            {
                _document.PushUndo();
                _attrGestureUndoPushed = _attrGestureActive;
            }
            node.Attributes[attrKey] = newValue;
            MarkDirtyOrDefer();
            RaiseNodeParamCommitted(node);
            return true;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", $"CommitNodeAttribute('{node?.Title}'.'{attrKey}')", ex);
            return false;
        }
    }

    // ── slider-drag gesture batching (Inspector scalar / vector editors) ──
    // A slider thumb drag streams ValueChanged → CommitNodeAttribute once per
    // frame; each PushUndo is a whole-layer serialize and each MarkDirty
    // re-raises SelectedLayer (full roster/triggers rebuild in the Inspector).
    // The Inspector brackets the pointer gesture so the stream costs one undo
    // snapshot (taken on the first actual change) and one MarkDirty (at
    // gesture end); per-tick writes still update the attribute and raise
    // NodeParamCommitted so the canvas preview tracks the drag live. Keyboard
    // nudges / NumberBox edits never enter a gesture and commit as before.
    private bool _attrGestureActive;
    private bool _attrGestureUndoPushed;
    private bool _attrGestureDirty;

    public void BeginNodeAttributeGesture()
    {
        // Flush any dangling gesture (missed release) first so its deferred
        // dirty-mark isn't lost.
        EndNodeAttributeGesture();
        _attrGestureActive = true;
    }

    public void EndNodeAttributeGesture()
    {
        if (!_attrGestureActive) return;
        _attrGestureActive = false;
        _attrGestureUndoPushed = false;
        if (_attrGestureDirty)
        {
            _attrGestureDirty = false;
            _document?.MarkDirty();
        }
    }

    // MarkDirty gate: while a slider-drag gesture is active the dirty-mark is
    // deferred to EndNodeAttributeGesture so the OnChanged → SelectedLayer
    // rebuild fires once per gesture, not once per tick.
    private void MarkDirtyOrDefer()
    {
        if (_attrGestureActive) { _attrGestureDirty = true; return; }
        _document?.MarkDirty();
    }

    // V14 removed the superseded all-or-nothing ToggleNodeParamKeyframe (and its
    // NodeParamVm.ToggleKeyframeCommand / RelayCommand wrapper): it had no caller
    // left once the DaVinci-style cluster below took over the Inspector's keyframe
    // affordance. ★ The two are NOT equivalent, so do not "restore parity" by
    // re-adding it: the old path seeded socket-backed pins through
    // AnimatedPinRegistry.SeedAnimation (two keyframes, and it grew the timeline's
    // DurationMs to reach the playhead), whereas ToggleParamKeyframeAtPlayhead is
    // strictly add/remove-one-at-the-playhead. The canvas right-click "Animate"
    // gesture (WidgetGraphCanvas) still owns the SeedAnimation/RemoveAnimation
    // path, so that behaviour is not lost — only the duplicate Inspector route is.

    // ── DaVinci-style per-parameter keyframe API (Inspector ◀ ◇/◆ ▶ cluster) ──
    // The diamond + arrows operate on a single parameter at the playhead. A param
    // resolves to one or more keyframe component paths: a socket-backed animatable
    // pin uses AnimatedPinRegistry.GetPinComponents (scalar → 1, vector → 2-4,
    // colour → 4 channels); a socket-less param uses its channel/component/single
    // key. All four channels of a colour move together so the diamond reads/writes
    // a whole colour at once.

    /// <summary>Playhead-match tolerance (ms) — mirrors the timeline strip's.</summary>
    public const double KeyframeTimeTolerance = 0.5;

    private string[] ParamComponentKeys(Node node, NodeParamVm p)
    {
        Socket? socket = FindInputSocket(node, p.SocketName);
        if (socket is not null && AnimatedPinRegistry.IsAnimatablePinType(socket.DataType))
            return AnimatedPinRegistry.GetPinComponents(socket).Select(c => c.ComponentName).ToArray();
        if (p.Kind == NodeParamKind.Color)
            return AnimatedPinRegistry.GetColorChannelKeys(p.Key);
        if (p.VectorComponentKeys is { Length: > 0 })
            return p.VectorComponentKeys;
        return new[] { p.Key };
    }

    private HashSet<string> ParamPathSet(Node node, NodeParamVm p)
        => new(ParamComponentKeys(node, p).Select(c => AnimatedPinRegistry.MakeParameterPath(node, c)),
               StringComparer.Ordinal);

    // Cached variant for the playback-rate probes (the diamond refreshers poll
    // on every PlayheadMs tick): the path set is deterministic per param, and
    // the NodeParamVm set is rebuilt together with its node, so the seed from
    // BuildNodeParams can't outlive the sockets it was derived from. The
    // click-driven keyframe mutations keep computing fresh sets.
    private HashSet<string> CachedParamPaths(Node node, NodeParamVm p)
        => p.KeyframePaths ??= ParamPathSet(node, p);

    /// <summary>
    /// Single-pass has-keyframes / on-keyframe probe for the Inspector diamond.
    /// <c>On</c> implies <c>Has</c>.
    ///
    /// <para>V14 removed the two separate <c>ParamHasKeyframes</c> /
    /// <c>IsParamOnKeyframe</c> predicates this replaced — they each walked the
    /// timeline once and both had zero callers after the diamond refreshers moved
    /// to this single probe (InspectorPanel polls it on every PlayheadMs tick, so
    /// halving the walks is the whole point). The shared private helpers
    /// (<see cref="ParamComponentKeys"/> / <see cref="ParamPathSet"/> /
    /// <see cref="CachedParamPaths"/>) are still used here and by the mutators.</para>
    /// </summary>
    public (bool Has, bool On) ParamKeyframeState(NodeParamVm p)
    {
        if (p is null || _selectedNode is not { } node) return (false, false);
        WidgetTimeline? tl = ActiveTriggerObject?.Timeline;
        if (tl is null) return (false, false);
        var paths = CachedParamPaths(node, p);
        double t = Math.Max(0, PlayheadMs);
        bool has = false;
        foreach (Keyframe? k in tl.Keyframes)
        {
            if (k is null || !paths.Contains(k.ParameterPath)) continue;
            has = true;
            if (Math.Abs(k.TimeMs - t) <= KeyframeTimeTolerance) return (true, true);
        }
        return (has, false);
    }

    /// <summary>Move the playhead to the prev (dir&lt;0) / next (dir&gt;0) keyframe on THIS parameter.</summary>
    public void StepParamKeyframe(NodeParamVm p, int dir)
    {
        if (p is null || _selectedNode is not { } node) return;
        WidgetTimeline? tl = ActiveTriggerObject?.Timeline;
        if (tl is null) return;
        var paths = ParamPathSet(node, p);
        double cur = PlayheadMs;
        double best = double.NaN;
        foreach (var k in tl.Keyframes)
        {
            if (k is null || !paths.Contains(k.ParameterPath)) continue;
            double t = k.TimeMs;
            if (double.IsNaN(t) || double.IsInfinity(t)) continue;
            if (dir < 0) { if (t < cur - KeyframeTimeTolerance && (double.IsNaN(best) || t > best)) best = t; }
            else          { if (t > cur + KeyframeTimeTolerance && (double.IsNaN(best) || t < best)) best = t; }
        }
        if (!double.IsNaN(best)) PlayheadMs = best;
    }

    /// <summary>
    /// DaVinci diamond click: if the playhead is ON a keyframe for this parameter,
    /// remove that keyframe (every channel); otherwise add a keyframe at the
    /// playhead capturing the parameter's current value (every channel). The first
    /// click on an un-animated parameter adds the first keyframe. Use
    /// <see cref="RemoveAllParamKeyframes"/> for "stop animating entirely".
    /// </summary>
    public void ToggleParamKeyframeAtPlayhead(NodeParamVm p)
    {
        if (_document is null || p is null || _selectedNode is not { } node) return;
        WidgetTimeline? tl = ActiveTriggerObject?.Timeline;
        if (tl is null) return;
        try
        {
            string[] comps = ParamComponentKeys(node, p);
            string[] paths = comps.Select(c => AnimatedPinRegistry.MakeParameterPath(node, c)).ToArray();
            var pathSet = new HashSet<string>(paths, StringComparer.Ordinal);
            double t = Math.Max(0, PlayheadMs);
            bool onKf = tl.Keyframes.Any(k => k != null && pathSet.Contains(k.ParameterPath)
                && Math.Abs(k.TimeMs - t) <= KeyframeTimeTolerance);

            _document.PushUndo();
            if (onKf)
            {
                tl.Keyframes.RemoveAll(k => k != null && pathSet.Contains(k.ParameterPath)
                    && Math.Abs(k.TimeMs - t) <= KeyframeTimeTolerance);
            }
            else
            {
                if (tl.DurationMs < t) tl.DurationMs = t;
                if (tl.DurationMs <= 0) tl.DurationMs = 1000;
                for (int i = 0; i < comps.Length; i++)
                {
                    string path = paths[i];
                    // Replace any pre-existing keyframe at this exact time (idempotent re-key).
                    tl.Keyframes.RemoveAll(k => k != null
                        && string.Equals(k.ParameterPath, path, StringComparison.Ordinal)
                        && Math.Abs(k.TimeMs - t) <= KeyframeTimeTolerance);
                    double literal = AnimatedPinRegistry.ReadComponentLiteral(node, comps[i]);
                    tl.Keyframes.Add(new Keyframe
                    {
                        ParameterPath = path,
                        TimeMs        = t,
                        Value         = JsonSerializer.SerializeToElement(literal),
                        Curve         = KeyframeCurve.Linear,
                    });
                }
            }
            _document.MarkDirty();
            NotifyActiveTriggerChanged();
            RefreshNodeParamAnimatedState();
            RaiseNodeParamCommitted(node);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", $"ToggleParamKeyframeAtPlayhead('{p?.Key}')", ex);
        }
    }

    /// <summary>Remove every keyframe on this parameter (right-click → "Remove animation").</summary>
    public void RemoveAllParamKeyframes(NodeParamVm p)
    {
        if (_document is null || p is null || _selectedNode is not { } node) return;
        WidgetTimeline? tl = ActiveTriggerObject?.Timeline;
        if (tl is null) return;
        try
        {
            var paths = ParamPathSet(node, p);
            if (!tl.Keyframes.Any(k => k != null && paths.Contains(k.ParameterPath))) return;
            _document.PushUndo();
            tl.Keyframes.RemoveAll(k => k != null && paths.Contains(k.ParameterPath));
            _document.MarkDirty();
            NotifyActiveTriggerChanged();
            RefreshNodeParamAnimatedState();
            RaiseNodeParamCommitted(node);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", $"RemoveAllParamKeyframes('{p?.Key}')", ex);
        }
    }

    /// <summary>
    /// Set the easing curve on this parameter's keyframe(s) at the playhead
    /// (right-click → Ease In / Ease Out / Linear). Clears any custom bezier handles
    /// so the named curve takes effect. No-op when the playhead isn't on a keyframe.
    /// </summary>
    public void SetParamKeyframeCurveAtPlayhead(NodeParamVm p, KeyframeCurve curve)
    {
        if (_document is null || p is null || _selectedNode is not { } node) return;
        WidgetTimeline? tl = ActiveTriggerObject?.Timeline;
        if (tl is null) return;
        try
        {
            var paths = ParamPathSet(node, p);
            double t = Math.Max(0, PlayheadMs);
            var hits = tl.Keyframes.Where(k => k != null && paths.Contains(k.ParameterPath)
                && Math.Abs(k.TimeMs - t) <= KeyframeTimeTolerance).ToList();
            if (hits.Count == 0) return;
            _document.PushUndo();
            foreach (var k in hits)
            {
                k.Curve = curve;
                k.BezierP1X = k.BezierP1Y = k.BezierP2X = k.BezierP2Y = null;
            }
            _document.MarkDirty();
            NotifyActiveTriggerChanged();
            RaiseNodeParamCommitted(node);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", $"SetParamKeyframeCurveAtPlayhead('{p?.Key}')", ex);
        }
    }

    /// <summary>
    /// If the playhead sits on a keyframe for this parameter, rewrite that
    /// keyframe's value(s) from the parameter's current literal (DaVinci "edit the
    /// value at the playhead"). Called right after an inspector value commit on an
    /// animated parameter — that commit already pushed undo, so this rides the same
    /// undo step (NO second PushUndo). No-op when the playhead isn't on a keyframe.
    /// </summary>
    public void UpdateParamKeyframeValueAtPlayhead(NodeParamVm p)
    {
        if (_document is null || p is null || _selectedNode is not { } node) return;
        WidgetTimeline? tl = ActiveTriggerObject?.Timeline;
        if (tl is null) return;
        try
        {
            string[] comps = ParamComponentKeys(node, p);
            double t = Math.Max(0, PlayheadMs);
            bool changed = false;
            foreach (string comp in comps)
            {
                string path = AnimatedPinRegistry.MakeParameterPath(node, comp);
                Keyframe? hit = tl.Keyframes.FirstOrDefault(k => k != null
                    && string.Equals(k.ParameterPath, path, StringComparison.Ordinal)
                    && Math.Abs(k.TimeMs - t) <= KeyframeTimeTolerance);
                if (hit is null) continue;
                double literal = AnimatedPinRegistry.ReadComponentLiteral(node, comp);
                hit.Value = JsonSerializer.SerializeToElement(literal);
                changed = true;
            }
            // Rides the value commit's undo entry AND its gesture batching — a
            // slider drag on an on-keyframe param defers the dirty-mark too.
            if (changed) { MarkDirtyOrDefer(); NotifyActiveTriggerChanged(); }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", $"UpdateParamKeyframeValueAtPlayhead('{p?.Key}')", ex);
        }
    }

    private void RaiseNodeParamCommitted(Node node)
    {
        try { NodeParamCommitted?.Invoke(node); }
        catch (Exception ex) { GlobalLogger.Error("VisualistViewModel", "NodeParamCommitted", ex); }
    }

    /// <summary>
    /// Raised when a node-BODY inline pill commits a value
    /// (WidgetGraphCanvas.OnNodeAttributeCommitted). Distinct from
    /// <see cref="NodeParamCommitted"/> (which fires for right-pane INSPECTOR
    /// edits) so the two directions never cross-fire — the Inspector listens to
    /// THIS to mirror a node-body edit into its NODE form, while the graph listens
    /// to NodeParamCommitted to mirror an Inspector edit onto the node body.
    /// Keeping them separate stops an Inspector slider drag from rebuilding (and
    /// breaking) its own form.
    /// </summary>
    public event Action<Node>? NodeBodyCommitted;

    public void NotifyNodeBodyCommitted(Node node)
    {
        if (node is null) return;
        try { NodeBodyCommitted?.Invoke(node); }
        catch (Exception ex) { GlobalLogger.Error("VisualistViewModel", "NodeBodyCommitted", ex); }
    }

    // The case-correct input socket whose Name matches the param's socket name
    // (mirrors the canvas's ResolveAttrKey case-insensitive match).
    private static Socket? FindInputSocket(Node node, string? socketName)
    {
        if (node?.Sockets is null || string.IsNullOrEmpty(socketName)) return null;
        return node.Sockets.FirstOrDefault(s =>
            s.Type == SocketType.Input &&
            string.Equals(s.Name, socketName, StringComparison.OrdinalIgnoreCase));
    }

    // Re-evaluate every current param's IsAnimated flag against the active
    // trigger's timeline (cheap, no rebuild) so the inspector diamonds track a
    // trigger switch without re-running the full BuildNodeParams pass.
    private void RefreshNodeParamAnimatedState()
    {
        if (_selectedNode is not { } node || SelectedNodeParams.Count == 0) return;
        WidgetTimeline? timeline = ActiveTriggerObject?.Timeline;
        foreach (var p in SelectedNodeParams)
        {
            if (!p.IsAnimatable) continue;
            bool animated;
            Socket? socket = FindInputSocket(node, p.SocketName);
            if (socket is not null && AnimatedPinRegistry.IsAnimatablePinType(socket.DataType))
            {
                animated = AnimatedPinRegistry.IsPinAnimated(timeline, node, socket);
            }
            else
            {
                // Socket-less scalar / collapsed vector constant / colour — probe the
                // attribute-path keyframes (per-component for vectors, per-channel for
                // colour <key>.R/.G/.B/.A).
                IEnumerable<string> keys = p.Kind == NodeParamKind.Color
                    ? AnimatedPinRegistry.GetColorChannelKeys(p.Key)
                    : (p.VectorComponentKeys is { Length: > 0 }
                        ? p.VectorComponentKeys
                        : new[] { p.Key });
                animated = timeline is not null && keys.Any(c =>
                {
                    string path = AnimatedPinRegistry.MakeParameterPath(node, c);
                    return timeline.Keyframes.Any(k =>
                        k != null && string.Equals(k.ParameterPath, path, StringComparison.Ordinal));
                });
            }
            p.IsAnimated = animated;
        }
    }

    /// <summary>
    /// BUILD LOGIC — turn the selected node's template + live attributes into a
    /// set of typed <see cref="NodeParamVm"/>: skip the
    /// <c>__Range</c>/<c>__KnownValues</c> companion keys; map each real key to
    /// a control kind via the template input socket (falling back to
    /// value/special-case inference); collapse <c>Vector*.Constant</c>'s
    /// separate X/Y/Z/W keys into one vector param.
    /// </summary>
    private void BuildNodeParams(Node? node)
    {
        SelectedNodeParams.Clear();
        OnPropertyChanged(nameof(SelectedNodeDescription));
        if (node is null || _document is null) return;

        // The node's own Sockets are template-derived at Instantiate time, so they
        // are the authoritative input-socket set for kind-mapping AND the real
        // Socket objects the AnimatedPinRegistry reuse path needs. (The template is
        // WidgetNodeRegistry.Get(node.Title); we read its sockets through the node.)
        WidgetTimeline? timeline = ActiveTriggerObject?.Timeline;

        var attrs = node.Attributes ?? new Dictionary<string, string>();

        // Vector*.Constant special-case: the node has NO matching X/Y/Z/W input
        // sockets — its components live as separate scalar attribute keys. Collapse
        // them into ONE vector param and skip the per-component scalar handling.
        if (TryBuildVectorConstantParam(node, attrs, out NodeParamVm? vectorParam) && vectorParam is not null)
        {
            SelectedNodeParams.Add(vectorParam);
            return;
        }

        // PASS 1 — every real attribute key becomes a param. Track the param keys
        // (case-insensitive) so PASS 2 can skip any input socket already surfaced
        // here (an attribute-backed socket like Image.Crop "Rect" shows once).
        var pass1Keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in attrs)
        {
            string key = kv.Key;
            // Skip companion-meta keys — they describe a real param, they aren't one.
            if (IsCompanionKey(key)) continue;

            pass1Keys.Add(key);

            string rawValue = kv.Value ?? string.Empty;

            // Enum first: a "<Key>__KnownValues" companion always wins.
            string[]? options = null;
            if (attrs.TryGetValue(key + "__KnownValues", out var kvCsv) && !string.IsNullOrWhiteSpace(kvCsv))
            {
                options = kvCsv.Split(',')
                    .Select(o => o.Trim())
                    .Where(o => o.Length > 0)
                    .ToArray();
            }

            Socket? socket = FindInputSocket(node, key);
            SocketDataType dataType = socket?.DataType ?? SocketDataType.Any;

            // Control kind — the precedence lives in ResolveParamKind so it has one
            // definition and a test; see the ★ note there for why MediaPath must outrank the
            // socket type.
            NodeParamKind kind = ResolveParamKind(key, socket, rawValue, options is { Length: > 0 });

            // Range companion (Scalar / vector components).
            (double min, double max, bool hasRange) = ParseRange(attrs, key);

            var param = new NodeParamVm(this, node, key, kind)
            {
                SocketName = socket?.Name ?? key,
                DataType   = dataType,
                Options    = options ?? Array.Empty<string>(),
                HasRange   = hasRange,
            };

            switch (kind)
            {
                case NodeParamKind.Scalar:
                {
                    double cur = ParseDouble(rawValue, 0);
                    if (hasRange) { param.Min = min; param.Max = max; }
                    else
                    {
                        // Name/type-derived default band (rotation → degrees, opacity
                        // → 0..1, etc.) instead of a flat 0..1; the paired numeric box
                        // keeps any value editable regardless of the slider band.
                        (param.Min, param.Max) = DefaultScalarRange(key, dataType, cur);
                    }
                    // Int sockets snap the slider + round commits (MapSocketKind folds
                    // Int into the Scalar control kind, so the type is carried here).
                    param.IsInteger = dataType == SocketDataType.Int;
                    param.InitNumber(cur);
                    break;
                }
                case NodeParamKind.Bool:
                    param.InitBool(ParseBool(rawValue));
                    break;
                case NodeParamKind.Color:
                    param.InitColor(NormalizeHex(Unquote(rawValue)));
                    break;
                case NodeParamKind.Enum:
                    param.InitText(Unquote(rawValue));
                    break;
                case NodeParamKind.MediaPath:
                    param.InitText(Unquote(rawValue));
                    break;
                case NodeParamKind.Vector2:
                case NodeParamKind.Vector3:
                case NodeParamKind.Vector4:
                {
                    // Socket-backed vector attribute (e.g. Image.Crop "Rect" =
                    // "0,0,1,1"): comma-joined scalars. Commit re-joins with a
                    // comma (VectorComponentKeys stays null so it's NOT split into
                    // X/Y/Z/W keys — that's the Vector*.Constant collapse case).
                    int n = kind == NodeParamKind.Vector2 ? 2 : kind == NodeParamKind.Vector3 ? 3 : 4;
                    param.InitVector(ParseVector(rawValue, n));
                    if (hasRange) { param.Min = min; param.Max = max; }
                    break;
                }
                default: // String
                    param.InitText(Unquote(rawValue));
                    break;
            }

            // Animatability + current animated state.
            param.IsAnimatable = socket is not null && AnimatedPinRegistry.IsAnimatablePinType(socket.DataType);
            if (param.IsAnimatable && socket is not null)
                param.IsAnimated = AnimatedPinRegistry.IsPinAnimated(timeline, node, socket);
            else if (kind == NodeParamKind.Scalar)
            {
                // Socket-less scalar (e.g. Scalar.Constant "Value"): still
                // animatable via the attribute-path seed.
                param.IsAnimatable = true;
                string path = AnimatedPinRegistry.MakeParameterPath(node, key);
                param.IsAnimated = timeline?.Keyframes.Any(k =>
                    k != null && string.Equals(k.ParameterPath, path, StringComparison.Ordinal)) == true;
            }
            else if (kind == NodeParamKind.Color)
            {
                // Socket-less colour (e.g. Color.Constant "Value"): animatable via
                // the four-channel attribute path (<id>.<key>.R/.G/.B/.A). Mirrors
                // the socket-backed colour path (GetPinComponents handles Color).
                param.IsAnimatable = true;
                param.IsAnimated = timeline is not null && AnimatedPinRegistry
                    .GetColorChannelKeys(key)
                    .Select(c => AnimatedPinRegistry.MakeParameterPath(node, c))
                    .Any(path => timeline.Keyframes.Any(k =>
                        k != null && string.Equals(k.ParameterPath, path, StringComparison.Ordinal)));
            }

            // Seed the component-path cache the playback-rate diamond probes reuse.
            param.KeyframePaths = ParamPathSet(node, param);
            SelectedNodeParams.Add(param);
        }

        // PASS 2 — socket-only inputs. Math.Add/Sub/Mul/Div, Vector.*, etc. carry
        // their editable values entirely on input sockets and have ZERO attributes,
        // so PASS 1 emits nothing and the inspector reads "no values". Synthesize a
        // param for every non-Flow Input socket NOT already surfaced by PASS 1. The
        // committed value is written to node.Attributes[socket.Name] (via NodeParamVm's
        // existing CommitNodeAttribute chokepoint) — the compositor reads that
        // attribute as the unwired-input fallback, so a slider value flows end-to-end
        // exactly like a wired-but-absent input. (Wired inputs still take their
        // upstream value at evaluation; the attr is only the fallback the compositor
        // uses when the pin is unwired.)
        //
        // PASS 2 used to surface a param for EVERY
        // unmatched input socket — which produced the "no-op duplicate" rows Majo
        // reported across the Fusion image-op nodes (not just Image.Transform). Two
        // classes leaked:
        //   (a) WIRE-ONLY sockets — Image / Audio / Any / Collection inputs (the "In"
        //       on every Image.* op, A/B on Blend/Combine, Image/Mask on Image.Mask,
        //       In on Result.If/Viewer/Visual.Complete). You cannot inline-edit an
        //       image/audio/list value, so the row did nothing.
        //   (b) MIRROR sockets on attribute-authored nodes — a typed input whose
        //       value actually lives in differently-named attributes already shown by
        //       PASS 1: Image.Transform's Translate/Rotate/Scale (→ TranslateX/Y,
        //       Rotation, ScaleX/Y), Image.Tile's Repeat (→ RepeatX/Y), Particles.Emit's
        //       Position/Velocity (→ PositionX/Y, VelocityX/Y). The evaluator's
        //       attribute fallbacks are the real editable surface; the socket-named
        //       attribute the PASS 2 row wrote was never read.
        // Same-name numeric inputs (Image.Blur "Radius", Math.Mod "A"/"B", …) are
        // already skipped by the pass1Keys check, and pure socket-only nodes (no real
        // attrs) still get full PASS 2 coverage. Non-numeric typed inputs that AREN'T
        // mirrored (e.g. Text.Render's "Text" String) stay editable.
        bool nodeIsAttrAuthored = attrs.Keys.Any(k =>
            !IsCompanionKey(k) && !k.StartsWith("__", StringComparison.Ordinal));
        if (node.Sockets is { Count: > 0 })
        {
            foreach (var socket in node.Sockets)
            {
                if (socket is null) continue;
                if (socket.Type != SocketType.Input) continue;
                if (socket.DataType == SocketDataType.Flow) continue;
                if (string.IsNullOrEmpty(socket.Name)) continue;
                if (pass1Keys.Contains(socket.Name)) continue;

                // (a) Wire-only / structural types are never inline-editable.
                if (socket.DataType is SocketDataType.Image
                                    or SocketDataType.Audio
                                    or SocketDataType.Any
                                    or SocketDataType.Collection) continue;

                // (b) On an attribute-authored node, a numeric / vector input whose
                // name did NOT already match an attribute (same-name ones are skipped
                // by the pass1Keys check above) is a MIRROR of attributes PASS 1
                // surfaced under component / alias names — Transform Translate/Scale →
                // TranslateX/Y, ScaleX/Y; Transform Rotate → Rotation; Tile Repeat →
                // RepeatX/Y; Particles Position/Velocity → …X/Y. Skip it so the
                // inspector shows the real (attribute) control once, not twice. This
                // gate is deliberately broad: it holds for the ENTIRE current catalog
                // (every attr-authored node's numeric inputs are either same-name or
                // mirrors — verified node-by-node). If a future template ever pairs
                // real attributes with a genuinely INDEPENDENT numeric socket-input
                // (whose unwired fallback is read from Attributes[socketName]), refine
                // this into a per-socket mirror check; VisualistInspectorDuplicateParamTests
                // is the guard that will flag the need.
                if (nodeIsAttrAuthored && socket.DataType is SocketDataType.Scalar
                                                          or SocketDataType.Float
                                                          or SocketDataType.Int
                                                          or SocketDataType.Vector2
                                                          or SocketDataType.Vector3
                                                          or SocketDataType.Vector4) continue;

                pass1Keys.Add(socket.Name);

                NodeParamKind kind = MapSocketKind(socket.DataType);
                // The committed value is stored under the socket Name; reuse any
                // existing attribute (covers case-insensitive matches PASS 1's
                // exact-key skip wouldn't have caught) else fall back per kind.
                bool hasAttr = attrs.TryGetValue(socket.Name, out var rawValue);
                rawValue ??= string.Empty;

                var param = new NodeParamVm(this, node, socket.Name, kind)
                {
                    SocketName = socket.Name,
                    DataType   = socket.DataType,
                    HasRange   = false,
                };

                switch (kind)
                {
                    case NodeParamKind.Scalar:
                    {
                        double cur = hasAttr ? ParseDouble(rawValue, 0) : 0;
                        // Name/type-derived default band (see PASS 1); the numeric box
                        // stays unbounded for out-of-band values.
                        (param.Min, param.Max) = DefaultScalarRange(socket.Name, socket.DataType, cur);
                        param.IsInteger = socket.DataType == SocketDataType.Int;
                        param.InitNumber(cur);
                        break;
                    }
                    case NodeParamKind.Bool:
                        param.InitBool(hasAttr ? ParseBool(rawValue) : false);
                        break;
                    case NodeParamKind.Color:
                        param.InitColor(hasAttr ? NormalizeHex(Unquote(rawValue)) : "#000000");
                        break;
                    case NodeParamKind.Vector2:
                    case NodeParamKind.Vector3:
                    case NodeParamKind.Vector4:
                    {
                        int n = kind == NodeParamKind.Vector2 ? 2 : kind == NodeParamKind.Vector3 ? 3 : 4;
                        param.InitVector(hasAttr ? ParseVector(rawValue, n) : new double[n]);
                        break;
                    }
                    default: // String
                        param.InitText(hasAttr ? Unquote(rawValue) : string.Empty);
                        break;
                }

                // Pin-side animatability (Scalar / Vector via the real Socket).
                param.IsAnimatable = AnimatedPinRegistry.IsAnimatablePinType(socket.DataType);
                if (param.IsAnimatable)
                    param.IsAnimated = AnimatedPinRegistry.IsPinAnimated(timeline, node, socket);

                // Seed the component-path cache the playback-rate diamond probes reuse.
                param.KeyframePaths = ParamPathSet(node, param);
                SelectedNodeParams.Add(param);
            }
        }
    }

    // Vector*.Constant — collapse its X/Y[/Z[/W]] scalar attribute keys into a
    // single vector param. Returns false for any other node so the normal loop
    // handles it. The component count is keyed off the title so Vector.Rect4
    // (X/Y/W/H, not a *.Constant) is NOT collapsed here.
    private bool TryBuildVectorConstantParam(Node node, IReadOnlyDictionary<string, string> attrs, out NodeParamVm? param)
    {
        param = null;
        string[] comps;
        NodeParamKind kind;
        switch (node.Title)
        {
            case "Vector2.Constant": comps = new[] { "X", "Y" };           kind = NodeParamKind.Vector2; break;
            case "Vector3.Constant": comps = new[] { "X", "Y", "Z" };      kind = NodeParamKind.Vector3; break;
            case "Vector4.Constant": comps = new[] { "X", "Y", "Z", "W" }; kind = NodeParamKind.Vector4; break;
            default: return false;
        }

        var values = new double[comps.Length];
        for (int i = 0; i < comps.Length; i++)
            values[i] = attrs.TryGetValue(comps[i], out var raw) ? ParseDouble(raw, 0) : 0;

        var p = new NodeParamVm(this, node, "Value", kind)
        {
            SocketName       = "Value",
            DataType         = node.Title switch
            {
                "Vector2.Constant" => SocketDataType.Vector2,
                "Vector3.Constant" => SocketDataType.Vector3,
                _                   => SocketDataType.Vector4,
            },
            VectorComponentKeys = comps,
            HasRange            = false,
            Min                 = 0,
            Max                 = 1,
        };
        p.InitVector(values);

        // Vector constants animate per-component through the attribute path
        // (no matching input socket exists). The toggle seeds/strips the X/Y/Z/W
        // paths via the same AnimatedPinRegistry component contract the canvas
        // pin-Animate gesture would use if a socket existed.
        p.IsAnimatable = true;
        WidgetTimeline? timeline = ActiveTriggerObject?.Timeline;
        if (timeline is not null)
        {
            p.IsAnimated = comps.Any(c =>
            {
                string path = AnimatedPinRegistry.MakeParameterPath(node, c);
                return timeline.Keyframes.Any(k =>
                    k != null && string.Equals(k.ParameterPath, path, StringComparison.Ordinal));
            });
        }
        // Seed the component-path cache the playback-rate diamond probes reuse.
        p.KeyframePaths = ParamPathSet(node, p);
        param = p;
        return true;
    }

    // ─── value parsing / serialization helpers ───────────────────────────
    //
    // These mirror the attribute-value conventions the canvas + NodeTemplates
    // use: scalars / vector components are stored BARE ("1.0", "0"); Color and
    // media Path are stored as JSON-quoted string literals ("#ffffff",
    // "images/foo.png"). Commit must round-trip in the same shape so the
    // browser-side evaluator (compositor.js) reads identical values.

    private static bool IsCompanionKey(string key)
        => key.EndsWith("__Range", StringComparison.Ordinal)
        || key.EndsWith("__KnownValues", StringComparison.Ordinal)
        // Pure runtime metadata, not an authored param. It reaches saved layers only
        // through LayerGraphMigrator's template back-fill: the auto-injection path
        // (DisplaySinkNode.Build) creates the Display node with NO attributes, so
        // before that back-fill no saved graph carried this key at all. Without this
        // arm every Display node — the one node every trigger has — grows an
        // "Is Auto Injected" checkbox in the Inspector (InferKindFromValue reads
        // "true" as Bool) that writes a value nothing ever reads. Matched by NAME
        // because it carries no "__" convention marker.
        || string.Equals(key, "IsAutoInjected", StringComparison.Ordinal);

    /// <summary>
    /// The attribute key that carries a local media file path. One definition, consumed by
    /// <see cref="ResolveParamKind"/> and <see cref="InferKindFromValue"/>; the canvas keeps
    /// its own copy for the drag-drop path (WidgetGraphCanvas.MediaPathAttributeKey) because
    /// the two pillars do not share UI code.
    /// </summary>
    private const string MediaPathAttrKey = "Path";

    private static bool IsMediaPathKey(string key)
        => string.Equals(key, MediaPathAttrKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// THE control-kind precedence for one Inspector row. Extracted from
    /// <c>BuildNodeParams</c> so the ordering has a name, one definition, and a test
    /// (<c>DynamicMediaSourceV7Tests</c>) — building the real view-model needs a WinUI
    /// dispatcher, so an inlined chain was untestable and that is exactly how the regression
    /// below shipped invisibly.
    ///
    /// Order, highest priority first:
    /// <list type="number">
    /// <item><b>Enum</b> — a <c>&lt;Key&gt;__KnownValues</c> companion means the template
    /// deliberately constrained the value to a list, which always wins.</item>
    /// <item><b>MediaPath</b> — the media-path key, REGARDLESS of whether the node also
    /// declares a matching input socket.</item>
    /// <item>the input socket's data type, when there is one.</item>
    /// <item>value-shape inference, for a socket-less attribute.</item>
    /// </list>
    ///
    /// ★ Rule 2 sits above rule 3 because of a regression V7 introduced and no test could
    /// see. <see cref="NodeParamKind.MediaPath"/> is what gives the row its Browse… media
    /// picker (<c>InspectorPanel.BuildMediaPathEditor</c>) and its backslash normalisation
    /// (<c>NodeParamVm.CommitText</c>), and <see cref="InferKindFromValue"/> is its ONLY
    /// producer — reachable only when a key has no matching input socket. The moment V7
    /// appended a String <c>Path</c> INPUT to Image.Load / Video.Load / Audio.Load, rule 3
    /// started winning and every freshly spawned loader silently lost the picker, because
    /// <see cref="MapSocketKind"/> maps String to a plain TextBox. The row still looked
    /// editable, so it read as "the picker moved" rather than "the picker is gone".
    ///
    /// Hoisting the test changes nothing else: a socket-less <c>Path</c> resolved to MediaPath
    /// before (through rule 4) and still does; a wirable one now does too.
    /// </summary>
    internal static NodeParamKind ResolveParamKind(
        string key, Socket? socket, string rawValue, bool hasEnumOptions)
    {
        if (hasEnumOptions)      return NodeParamKind.Enum;
        if (IsMediaPathKey(key)) return NodeParamKind.MediaPath;
        if (socket is not null)  return MapSocketKind(socket.DataType);
        return InferKindFromValue(key, rawValue);
    }

    private static NodeParamKind MapSocketKind(SocketDataType dt) => dt switch
    {
        SocketDataType.Scalar or SocketDataType.Float or SocketDataType.Int => NodeParamKind.Scalar,
        SocketDataType.Bool    => NodeParamKind.Bool,
        SocketDataType.String  => NodeParamKind.String,
        SocketDataType.Color   => NodeParamKind.Color,
        SocketDataType.Vector2 => NodeParamKind.Vector2,
        SocketDataType.Vector3 => NodeParamKind.Vector3,
        SocketDataType.Vector4 => NodeParamKind.Vector4,
        _                       => NodeParamKind.String,
    };

    private static NodeParamKind InferKindFromValue(string key, string rawValue)
    {
        // Kept even though ResolveParamKind now tests the media-path key ahead of the socket
        // arm: this method is the socket-less fallback and must stay correct on its own, and
        // it is the shape every other Path row in the catalog (Image.LoadUrl-style
        // attribute-only loaders, WebSource) still arrives through.
        if (IsMediaPathKey(key)) return NodeParamKind.MediaPath;
        string s = Unquote(rawValue).Trim();
        if (LooksLikeHexColor(s)) return NodeParamKind.Color;
        if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return NodeParamKind.Bool;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return NodeParamKind.Scalar;
        return NodeParamKind.String;
    }

    // Sensible default slider band for a scalar param that has NO "<Key>__Range"
    // companion. Name-based heuristics cover the common Fusion-style parameters
    // (rotation → degrees, opacity → 0..1, radius/blur → pixels, count → small
    // integers, …); everything else keeps the prior behaviour (0..1, widening to
    // keep an out-of-band seed reachable). The paired NumberBox stays unbounded
    // regardless, so this only affects slider ergonomics — never the committed
    // value. Where a range IS declared, callers use it and never reach here.
    private static (double min, double max) DefaultScalarRange(string? key, SocketDataType dt, double cur)
    {
        string k = (key ?? string.Empty).ToLowerInvariant();
        double lo, hi;

        if (Contains(k, "rotation", "angle", "degree", "heading", "azimuth", "hue"))
        {
            // Degrees. Bias to 0..360 for a positive seed, -180..180 when the
            // current value is negative (both are natural for rotation editors).
            lo = cur < 0 ? -180 : 0;
            hi = 360;
        }
        else if (Contains(k, "opacity", "alpha", "mix", "blend", "amount", "strength",
                              "intensity", "weight", "gain", "wet", "dry", "fade"))
        {
            lo = 0; hi = 1;
        }
        else if (Contains(k, "scale", "zoom", "gamma", "contrast"))
        {
            lo = 0; hi = 2;
        }
        else if (Contains(k, "radius", "blur", "thickness", "spread", "feather",
                              "size", "distance", "border", "corner"))
        {
            lo = 0; hi = 100;
        }
        else if (Contains(k, "count", "segments", "sides", "steps", "iterations",
                              "copies", "columns", "column", "rows", "row", "index"))
        {
            lo = 0; hi = 10;
        }
        else if (Contains(k, "speed", "rate", "frequency", "fps", "duration"))
        {
            lo = 0; hi = 10;
        }
        else
        {
            lo = 0; hi = 1;
        }

        // Never clip the current value out of the slider band.
        if (cur < lo) lo = Math.Floor(cur);
        if (cur > hi) hi = Math.Max(hi, Math.Ceiling(cur * 2));
        if (hi <= lo) hi = lo + 1;
        return (lo, hi);
    }

    // Ordinal "contains any of" over an already-lowercased haystack.
    private static bool Contains(string haystack, params string[] needles)
    {
        foreach (string n in needles)
            if (haystack.Contains(n, StringComparison.Ordinal)) return true;
        return false;
    }

    private static (double min, double max, bool hasRange) ParseRange(IReadOnlyDictionary<string, string> attrs, string key)
    {
        if (!attrs.TryGetValue(key + "__Range", out var raw) || string.IsNullOrWhiteSpace(raw))
            return (0, 1, false);
        int sep = raw.IndexOf("..", StringComparison.Ordinal);
        if (sep < 0) return (0, 1, false);
        string a = raw.Substring(0, sep);
        string b = raw.Substring(sep + 2);
        if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var lo)
            && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var hi)
            && hi > lo)
            return (lo, hi, true);
        return (0, 1, false);
    }

    private static double ParseDouble(string raw, double fallback)
        => double.TryParse(Unquote(raw), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    // Parse a comma-joined vector attribute ("0,0,1,1") into a fixed-length
    // array, zero-padding / truncating to <paramref name="count"/> components.
    private static double[] ParseVector(string raw, int count)
    {
        var result = new double[count];
        string s = Unquote(raw);
        if (string.IsNullOrWhiteSpace(s)) return result;
        var parts = s.Split(',');
        for (int i = 0; i < count && i < parts.Length; i++)
            result[i] = double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return result;
    }

    private static bool ParseBool(string raw)
        => string.Equals(Unquote(raw).Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeHexColor(string s)
    {
        if (string.IsNullOrEmpty(s) || s[0] != '#') return false;
        int n = s.Length - 1;
        if (n != 3 && n != 4 && n != 6 && n != 8) return false;
        for (int i = 1; i < s.Length; i++)
            if (!Uri.IsHexDigit(s[i])) return false;
        return true;
    }

    // Normalise a hex value into #rrggbb / #rrggbbaa for the picker. Falls back
    // to the trimmed input when it isn't a recognisable hex triple/quad.
    private static string NormalizeHex(string s)
    {
        s = (s ?? string.Empty).Trim();
        if (!LooksLikeHexColor(s)) return s;
        string body = s.Substring(1);
        if (body.Length == 3) // #rgb → #rrggbb
            body = string.Concat(body[0], body[0], body[1], body[1], body[2], body[2]);
        else if (body.Length == 4) // #rgba → #rrggbbaa
            body = string.Concat(body[0], body[0], body[1], body[1], body[2], body[2], body[3], body[3]);
        return "#" + body.ToLowerInvariant();
    }

    // Strip a single matching pair of outer double-quotes (the JSON-literal
    // shape Color / media Path attributes are stored in). Mirrors
    // MediaLibrary.UnquoteAttribute's read side.
    private static string Unquote(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        string s = raw.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            s = s.Substring(1, s.Length - 2);
        return s;
    }

    // Re-quote a string value as a JSON literal for storage (Color / MediaPath).
    private static string Quote(string s) => "\"" + (s ?? string.Empty) + "\"";

    // Format a double the way the bare scalar/vector attributes are stored —
    // invariant culture, no thousands separators, integral values stay clean.
    private static string FormatScalar(double v)
        => v.ToString("0.######", CultureInfo.InvariantCulture);

    // Public serialization shims so NodeParamVm.Commit* round-trips values in
    // the exact attribute shape this VM (and the canvas) reads — centralised so
    // there's a single quote/scalar convention, not a parallel one in the VM.
    internal static string PublicQuote(string s) => Quote(s);
    internal static string PublicFormatScalar(double v) => FormatScalar(v);

    private double _playheadMs;
    /// <summary>
    /// Current scrub position in milliseconds for the active trigger's
    /// timeline. Driven by the WidgetEditorView scrubber; read by
    /// any future preview surface that wants to render at the scrub point.
    /// Not persisted to .phxlayer — purely an editor-session value.
    /// </summary>
    public double PlayheadMs
    {
        get => _playheadMs;
        set => Set(ref _playheadMs, value);
    }

    /// <summary>Header text for the timeline strip — "TIMELINE · {trigger} · {duration}S".</summary>
    public string ActiveTriggerDurationLabel
    {
        get
        {
            WidgetTrigger? t = ActiveTriggerObject;
            double seconds = (t?.Timeline?.DurationMs ?? 0.0) / 1000.0;
            string triggerLabel = string.IsNullOrEmpty(_activeTrigger) ? "—" : _activeTrigger.ToUpperInvariant();
            return string.Format(
                Localizer.T("visualist.main.timeline.header_format", "TIMELINE · {0} · {1:0.0}S"),
                triggerLabel, seconds);
        }
    }

    public string ActiveLayerFileName
    {
        get
        {
            if (_document?.FilePath is { } p) return Path.GetFileName(p);
            if (_selectedLayerItem is { } item) return item.FileName;
            return Localizer.T("visualist.main.layer.none", "(no layer)");
        }
    }

    public bool IsDirty => _document?.IsDirty ?? false;

    /// <summary>
    /// External invalidation hook — when an inspector mutates the in-memory
    /// Layer (rect, preset, etc.) without going through a property on
    /// this VM, call this to rebroadcast SelectedLayer so the canvas re-renders.
    /// </summary>
    public void RaiseSelectedLayerChanged() => OnPropertyChanged(nameof(SelectedLayer));

    // V14 removed WidgetEditorTabLabel ("<widget> · <trigger>") — nothing read it.
    // The widget-editor surface titles itself from the widget + ActiveTrigger
    // directly, so this formatted mirror was a second, silently drifting source
    // of the same string. Its two OnPropertyChanged notifications (in the
    // SelectedWidget and ActiveTrigger setters) went with it; both setters still
    // notify ActiveTriggerObject / ActiveTriggerDurationLabel, which are live.

    // ─── live-presence wiring ────────────────────────────────────────────
    //
    // Hub's LayerRegistry tracks per-layer browser-source connection presence.
    // The MainView attaches an ILayerRegistrySource here at construction so
    // the LayerRail's per-row dot reflects which layers are actually being
    // rendered by an OBS browser instead of a hardcoded green.
    //
    // Threading: LayerRegistry.LiveLayerChanged fires from the WS accept /
    // close path, NOT the UI thread. We capture the UI DispatcherQueue at
    // Initialize() time (caller must be on the UI thread) and marshal each
    // event onto it before mutating LayerListItem.Active so the bound
    // x:Bind OneWay update happens on the dispatcher.

    private ILayerRegistrySource? _layersSource;
    private DispatcherQueue?      _uiQueue;
    private Action<string, bool>? _liveLayerHandler;

    /// <summary>
    /// Attach a live-presence source. Re-seeds Active for every existing
    /// rail row from <paramref name="layers"/> and subscribes to future
    /// transitions. Must be called from the UI thread (the dispatcher
    /// queue is captured here).
    /// </summary>
    public void Initialize(ILayerRegistrySource? layers)
    {
        DetachLayerSource();
        if (layers is null) return;

        _layersSource = layers;
        _uiQueue      = DispatcherQueue.GetForCurrentThread();

        SeedActiveFromSource();

        _liveLayerHandler = OnLiveLayerChanged;
        _layersSource.LiveLayerChanged += _liveLayerHandler;
    }

    /// <summary>
    /// Symmetric to <see cref="Initialize"/> — drops the subscription so a
    /// pillar tear-down (Hub.MainView Unloaded) doesn't leak the VM through
    /// the registry's event list.
    /// </summary>
    public void DetachLayerSource()
    {
        if (_layersSource is not null && _liveLayerHandler is not null)
        {
            try { _layersSource.LiveLayerChanged -= _liveLayerHandler; }
            catch (Exception ex)
            {
                GlobalLogger.Error("VisualistViewModel", "DetachLayerSource", ex);
            }
        }
        _layersSource    = null;
        _liveLayerHandler = null;
        _uiQueue         = null;
    }

    private void SeedActiveFromSource()
    {
        if (_layersSource is null) return;
        foreach (LayerListItem row in Layers)
        {
            try { row.Active = _layersSource.IsLayerActive(LayerIdFor(row)); }
            catch (Exception ex)
            {
                GlobalLogger.Error("VisualistViewModel",
                    $"SeedActiveFromSource('{row.FileName}')", ex);
            }
        }
    }

    private void OnLiveLayerChanged(string layerId, bool isActive)
    {
        // Marshal to UI thread; the ListView DataTemplate's x:Bind OneWay on
        // Active expects the change-notification on the dispatcher.
        var queue = _uiQueue;
        if (queue is null) return;
        queue.TryEnqueue(() =>
        {
            try
            {
                LayerListItem? row = Layers.FirstOrDefault(l =>
                    string.Equals(LayerIdFor(l), layerId, StringComparison.OrdinalIgnoreCase));
                if (row is null) return;
                row.Active = isActive;
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("VisualistViewModel",
                    $"OnLiveLayerChanged('{layerId}', {isActive})", ex);
            }
        });
    }

    // Layer ID = filename without extension. LayerListItem.FileName is the
    // full filename ("main.phxlayer"); strip to "main" so it matches the
    // Hub-side LayerRegistry keying.
    private static string LayerIdFor(LayerListItem row)
        => Path.GetFileNameWithoutExtension(row.FileName);

    // ─── enumeration ─────────────────────────────────────────────────────

    public void RefreshLayers()
    {
        Layers.Clear();

        string folder = Paths.HubLayers;
        // Folder-missing fallback: mirror Hub's behaviour and create
        // the layers folder if it's absent rather than leaving the rail silently
        // empty with no recovery path. Directory.CreateDirectory is a no-op when
        // the folder already exists. Paths is Shared.Core, so this stays inside
        // the pillar-isolation boundary. After this, the enumeration below simply
        // finds zero files — which the synthetic-row sync still surfaces if a
        // doc is open.
        try
        {
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", $"RefreshLayers: could not create layers folder '{folder}'", ex);
            // Couldn't create it — preserve the active doc's synthetic row so the
            // rail isn't blank, then bail.
            ReselectAfterEnumeration();
            return;
        }

        foreach (string path in Directory.EnumerateFiles(folder, "*.phxlayer").OrderBy(p => p))
        {
            LayerListItem? item = TryLoadListItem(path);
            if (item is not null) Layers.Add(item);
        }

        // Fresh rows come in with Active=false; if a live source
        // is attached, seed each row's true state before the rail repaints.
        SeedActiveFromSource();

        ReselectAfterEnumeration();
    }

    // Re-establish the rail selection after Layers was
    // rebuilt by enumeration. Keeps an unsaved in-memory document visible (its
    // saved row would not exist on disk) by re-synthesizing the "(unsaved)" row;
    // otherwise selects the disk row matching the open document, falling back to
    // the first row. Without this, a refresh while an unsaved layer is open
    // would null the selection → LoadSelectedLayer → Document = null, silently
    // discarding the in-memory work.
    private void ReselectAfterEnumeration()
    {
        // Unsaved active document → keep it on the rail and selected.
        if (_document is { FilePath: null })
        {
            SyncSyntheticRow(_document);
            return;
        }

        // Saved active document → reselect its disk row if present.
        if (_document?.FilePath is { } fp)
        {
            LayerListItem? match = Layers.FirstOrDefault(l =>
                !l.IsUnsaved && string.Equals(l.Path, fp, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                if (!ReferenceEquals(match, _selectedLayerItem))
                {
                    // Reflect the open doc without re-triggering a disk reload —
                    // the document is already bound.
                    _settingSyntheticRow = true;
                    try { Set(ref _selectedLayerItem, match, nameof(SelectedLayerItem)); }
                    finally { _settingSyntheticRow = false; }
                }
                OnPropertyChanged(nameof(ActiveLayerFileName));
                return;
            }
        }

        // No open document (or its file vanished) → restore the last-open
        // saved layer if it still exists on disk, else fall back to the first
        // row. This branch is what runs on the initial startup enumeration
        // (before any document is bound), so it's the restore point for the
        // persisted LastLayerPath. When the memo is empty / stale the behaviour
        // is identical to the prior FirstOrDefault default.
        SelectedLayerItem = ResolveStartupSelection() ?? Layers.FirstOrDefault();
    }

    // Match the persisted last-open layer path (VisualistUserConfig.LastLayerPath)
    // to an enumerated saved row, or null when there is no memo, it points at a
    // file no longer on disk / outside the layers folder, or the config read
    // faults. Callers fall back to FirstOrDefault so the rail is never left blank.
    private LayerListItem? ResolveStartupSelection()
    {
        try
        {
            string? last = Phoenix.Controls.Visualist.WinUI.Core.VisualistUserConfig.Instance.LastLayerPath;
            if (string.IsNullOrEmpty(last)) return null;
            return Layers.FirstOrDefault(l => !l.IsUnsaved && LayerPathEquals(l.Path, last));
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", "ResolveStartupSelection", ex);
            return null;
        }
    }

    // Two-phase enumeration split so the .phxlayer disk
    // scan can run on a worker thread (PrefetchLayerListItems) while the
    // ObservableCollection mutation stays on the UI thread (ApplyPrefetched).
    // Tied to MainView.InitializeAsync — non-WinUI consumers should keep
    // calling the synchronous RefreshLayers above.
    private IReadOnlyList<LayerListItem>? _prefetchedItems;

    /// <summary>
    /// Prefetch step — enumerates <c>data/layers/</c> and deserialises
    /// each .phxlayer on the calling thread (typically a Task.Run worker).
    /// Pure I/O; touches no WinUI dispatcher state.
    /// </summary>
    public void PrefetchLayerListItems()
    {
        var list = new List<LayerListItem>();
        string folder = Paths.HubLayers;
        if (Directory.Exists(folder))
        {
            foreach (string path in Directory.EnumerateFiles(folder, "*.phxlayer").OrderBy(p => p))
            {
                LayerListItem? item = TryLoadListItem(path);
                if (item is not null) list.Add(item);
            }
        }
        _prefetchedItems = list;
    }

    /// <summary>
    /// Apply step — moves the prefetched list into <see cref="Layers"/>
    /// on the UI thread, then runs the SeedActiveFromSource + initial
    /// selection that <see cref="RefreshLayers"/> would have. Falls back to
    /// the synchronous RefreshLayers when no prefetch is staged.
    /// </summary>
    public void ApplyPrefetchedLayers()
    {
        var items = _prefetchedItems;
        _prefetchedItems = null;
        if (items is null)
        {
            RefreshLayers();
            return;
        }
        Layers.Clear();
        foreach (var item in items) Layers.Add(item);
        SeedActiveFromSource();
        // Same selection logic as RefreshLayers: keep an unsaved
        // active document's synthetic row, otherwise reselect the open doc's disk
        // row or fall back to the first.
        ReselectAfterEnumeration();
    }

    private static LayerListItem? TryLoadListItem(string path)
    {
        try
        {
            Layer layer = LayerSerializer.Read(path);
            // Active seeds to false; if an ILayerRegistrySource is
            // attached it will overwrite via Initialize/seed. Without one
            // (design-time / fakes) the dot stays inactive, which is the
            // honest answer when there is no live Hub presence to query.
            return new LayerListItem(
                path:     path,
                fileName: Path.GetFileName(path),
                width:    layer.Resolution.Width,
                height:   layer.Resolution.Height,
                active:   false,
                fps:      60);
        }
        catch
        {
            return null;
        }
    }

    private void LoadSelectedLayer()
    {
        // Selecting the synthetic "(unsaved)" row must NOT reload
        // from disk: it has no file and stands in for the already-bound in-memory
        // document. Re-binding from its empty Path would throw / clobber the doc.
        // The flag covers programmatic selection; the IsUnsaved check covers a
        // user click on the row in the ListView.
        if (_settingSyntheticRow) return;
        if (_selectedLayerItem is { IsUnsaved: true }) return;

        if (_selectedLayerItem is null)
        {
            Document = null;
            return;
        }

        try
        {
            BindDocumentForPath(_selectedLayerItem.Path);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", $"failed to open '{_selectedLayerItem.Path}'", ex);
            Document = null;
        }
    }

    /// <summary>
    /// Cache-aware bind for an absolute layer path. A cache hit re-binds
    /// the in-memory document (preserving its undo/redo stacks and any unsaved
    /// edits); a miss opens from disk, seeds the LRU, and evicts past the cap.
    /// <para>
    /// Both <see cref="LoadSelectedLayer"/> and <see cref="OpenLayer"/> route
    /// through here. Pre-fix, OpenLayer did <c>Document = LayerDocument.Open(path)</c>
    /// unconditionally — the ONE load route that bypassed the cache — so
    /// re-opening an already-cached dirty layer (Open Recent / drag-drop / Hub
    /// open) discarded its in-memory edits (auto-save is off by default) and
    /// always dropped the cached undo history. Path-keyed so it works for paths
    /// outside the layers folder.
    /// </para>
    /// </summary>
    private void BindDocumentForPath(string path)
    {
        if (_layerCache.TryGetValue(path, out var cached))
        {
            _layerCacheLru.Remove(path);
            _layerCacheLru.AddFirst(path);
            Document = cached;
            return;
        }

        var fresh = LayerDocument.Open(path);
        _layerCache[path] = fresh;
        _layerCacheLru.AddFirst(path);
        EvictBeyondCap();
        Document = fresh;
    }

    private void EvictBeyondCap()
    {
        while (_layerCacheLru.Count > LayerCacheCap)
        {
            string drop = _layerCacheLru.Last!.Value;
            _layerCacheLru.RemoveLast();
            // Dispose the evicted document so its auto-save timer
            // doesn't leak. The current Document is always LRU-first, never the evicted
            // Last — the ReferenceEquals guard is belt-and-suspenders.
            if (_layerCache.TryGetValue(drop, out var evicted))
            {
                _layerCache.Remove(drop);
                if (!ReferenceEquals(evicted, _document))
                {
                    FlushAndDispose(evicted, "evict");
                }
            }
        }
    }

    // Never silently lose unsaved work when a cached document is dropped.
    // LRU eviction (editing >5 distinct layers in a session) and pillar-tab
    // teardown previously Dispose()d cached documents outright — so a dirty layer
    // the user had switched away from lost its edits with NO prompt (auto-save
    // defaults off, and PromptSaveBeforeCloseAsync only guards the ACTIVE doc).
    // Flush a dirty SAVED doc to disk first; an unsaved-dirty scratch doc (no
    // FilePath) can't be auto-saved, so log loudly instead of dropping silently.
    private static void FlushAndDispose(LayerDocument doc, string ctx)
    {
        try
        {
            if (doc.IsDirty)
            {
                if (doc.FilePath is not null) doc.Save();
                else GlobalLogger.Log(
                    $"VisualistViewModel: dropping an unsaved layer with pending edits ({ctx}) — no file path to flush to.",
                    "VisualistViewModel", LogLevel.System);
            }
        }
        catch (Exception ex) { GlobalLogger.Error("VisualistViewModel", $"{ctx} flush", ex); }
        try { doc.Dispose(); }
        catch (Exception ex) { GlobalLogger.Error("VisualistViewModel", $"{ctx} dispose", ex); }
    }

    private void RebuildTriggerNames()
    {
        TriggerNames.Clear();
        if (_selectedWidget is null) return;

        foreach (WidgetTrigger t in _selectedWidget.Triggers)
            if (!string.IsNullOrEmpty(t.Name))
                TriggerNames.Add(t.Name);

        // Snap ActiveTrigger to a valid name for the new selection — preserve
        // when possible (same trigger name across widgets is common in
        // single-trigger libraries), otherwise fall back to the first.
        if (TriggerNames.Count == 0)
        {
            ActiveTrigger = "";
        }
        else if (!TriggerNames.Contains(_activeTrigger, StringComparer.OrdinalIgnoreCase))
        {
            ActiveTrigger = TriggerNames[0];
        }
        else
        {
            // Same name still valid — re-fire so the editor refreshes against
            // the new widget's instance of the trigger.
            OnPropertyChanged(nameof(ActiveTriggerObject));
            OnPropertyChanged(nameof(ActiveTriggerDurationLabel));
        }
    }

    // ─── file commands (called from MainWindow after picker) ─────────────

    /// <summary>
    /// New empty layer with the engine's FullHD default. Lives in-memory
    /// until SaveAs. Kept as the no-arg sync entrypoint so legacy / test
    /// callers don't break — UI callers should prefer the preset-aware
    /// overload below.
    /// </summary>
    public void NewLayer()
    {
        // The Document setter synthesizes the "(unsaved)" rail
        // row and selects it (the new doc has no FilePath), so the rail shows the
        // freshly-created layer immediately instead of going blank. Do NOT null
        // out _selectedLayerItem afterwards — that would clobber the synthetic
        // selection the setter just made.
        Document = new LayerDocument();
        OnPropertyChanged(nameof(ActiveLayerFileName));
    }

    /// <summary>
    /// New empty layer with the
    /// caller-supplied <paramref name="name"/> / <paramref name="preset"/> /
    /// resolution. Used by <c>MainView.OnNewLayer</c> after the
    /// <see cref="Dialogs.NewLayerDialog"/> resolves; lets the user pick
    /// FullHD / QHD / UHD / Vertical / Square or a custom W/H upfront
    /// instead of defaulting to FullHD and forcing an Inspector edit.
    /// </summary>
    public void NewLayer(string name, LayerPreset preset, int width, int height)
    {
        var layer = new Layer
        {
            Name       = string.IsNullOrWhiteSpace(name) ? "untitled" : name.Trim(),
            Resolution = new LayerResolution { Width = Math.Max(1, width), Height = Math.Max(1, height) },
            Preset     = preset,
        };
        // As with the no-arg overload, the Document setter
        // synthesizes + selects the "(unsaved)" rail row for this path-less doc,
        // so the new layer is visible in the rail right away. Don't reset the
        // selection here.
        Document = new LayerDocument(layer);
        OnPropertyChanged(nameof(ActiveLayerFileName));
    }

    /// <summary>Open the layer at the given absolute path; updates the rail selection if it's listed.</summary>
    public void OpenLayer(string path)
    {
        try
        {
            // Route through the LRU cache (was a bare Open that dropped
            // undo history + risked discarding unsaved edits on re-open).
            BindDocumentForPath(path);
            // Reflect into the rail if the path is one we enumerated.
            LayerListItem? match = Layers.FirstOrDefault(l =>
                string.Equals(l.Path, path, StringComparison.OrdinalIgnoreCase));
            if (match is not null && !ReferenceEquals(match, _selectedLayerItem))
            {
                // Avoid re-loading via setter side effect.
                _selectedLayerItem = match;
                OnPropertyChanged(nameof(SelectedLayerItem));
            }
            else if (match is null)
            {
                // Not in our standard layers folder — clear any rail selection so
                // the chrome filename label doesn't lie about which file is loaded.
                _selectedLayerItem = null;
                OnPropertyChanged(nameof(SelectedLayerItem));
            }
            OnPropertyChanged(nameof(ActiveLayerFileName));
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", $"OpenLayer('{path}') failed", ex);
        }
    }

    /// <summary>Save the current document to its existing FilePath. Caller should fall back to SaveAs when null.</summary>
    public bool SaveLayer()
    {
        if (_document is null || _document.FilePath is null) return false;
        try
        {
            _document.Save();
            OnPropertyChanged(nameof(IsDirty));
            return true;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", $"SaveLayer to '{_document.FilePath}' failed", ex);
            return false;
        }
    }

    public bool SaveLayerAs(string path)
    {
        if (_document is null) return false;
        // Snapshot the pre-save path so we can re-key the cache below — by the
        // time we'd want to read _document.FilePath again, SaveAs has already
        // overwritten it with the new location.
        string? oldPath = _document.FilePath;
        try
        {
            _document.SaveAs(path);
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(ActiveLayerFileName));
            // SaveAs assigns FilePath on the already-bound instance, so the
            // Document setter's last-open memo doesn't re-fire — persist here.
            PersistLastLayerPath(path);

            // Re-key the LRU cache entry from the old path → new path. Without
            // this the cache stays bound to the original filename: a later
            // LoadSelectedLayer for the new path would miss the cache and read
            // a fresh copy from disk, dropping any undo history the original
            // doc carried (and orphaning the cache entry under the old key
            // until eviction). When the previous path is unknown (NewLayer →
            // SaveAs path) just seed the cache under the new path so the
            // freshly-named document is hot in the cache going forward.
            if (!string.IsNullOrEmpty(oldPath)
                && _layerCache.TryGetValue(oldPath, out var cached))
            {
                _layerCache.Remove(oldPath);
                _layerCacheLru.Remove(oldPath);
                _layerCache[path] = cached;
                _layerCacheLru.AddFirst(path);
            }
            else if (string.IsNullOrEmpty(oldPath))
            {
                _layerCache[path] = _document;
                _layerCacheLru.AddFirst(path);
                EvictBeyondCap();
            }

            // If the new path lands inside the layers folder, refresh so the
            // rail picks it up.
            if (Path.GetDirectoryName(path) is { } dir
                && string.Equals(Path.GetFullPath(dir), Path.GetFullPath(Paths.HubLayers), StringComparison.OrdinalIgnoreCase))
            {
                // RefreshLayers re-enumerates (the new file is now on disk) and
                // ReselectAfterEnumeration reselects the matching disk row — the
                // document now has a FilePath, so the real row replaces the
                // synthetic "(unsaved)" one with no leftover duplicate.
                //
                // Defensive: if the just-saved file somehow isn't in
                // the enumeration (case-sensitivity on a future *nix port, a
                // racing external delete, etc.), construct the row from disk and
                // select it through the BACKING FIELD set so the property's
                // LoadSelectedLayer side effect doesn't reload (and clobber) the
                // already-bound document. Pre-fix this used a raw field write that
                // also skipped change-notification consistency.
                RefreshLayers();
                bool enumerated = Layers.Any(l =>
                    !l.IsUnsaved && string.Equals(l.Path, path, StringComparison.OrdinalIgnoreCase));
                if (!enumerated)
                {
                    LayerListItem? recovered = TryLoadListItem(path);
                    if (recovered is not null)
                    {
                        Layers.Add(recovered);
                        SeedActiveFromSource();
                        _settingSyntheticRow = true;
                        try { Set(ref _selectedLayerItem, recovered, nameof(SelectedLayerItem)); }
                        finally { _settingSyntheticRow = false; }
                        OnPropertyChanged(nameof(ActiveLayerFileName));
                    }
                }
            }
            else
            {
                // Saved outside the layers folder — the file won't appear in the
                // rail. Drop the now-stale synthetic "(unsaved)" row (the doc has
                // a FilePath) and clear the rail selection so the chrome label
                // tracks the real file via ActiveLayerFileName, mirroring
                // OpenLayer's out-of-folder handling.
                for (int i = Layers.Count - 1; i >= 0; i--)
                    if (Layers[i].IsUnsaved)
                        Layers.RemoveAt(i);
                _settingSyntheticRow = true;
                try { Set(ref _selectedLayerItem, null, nameof(SelectedLayerItem)); }
                finally { _settingSyntheticRow = false; }
                OnPropertyChanged(nameof(ActiveLayerFileName));
            }
            return true;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", $"SaveLayerAs('{path}') failed", ex);
            return false;
        }
    }

    // ─── rail layer management (rename / duplicate / delete) ─────────────
    //
    // These are real file operations on user content (.phxlayer). They route
    // through the SAME services the existing save / enumerate / cache / recent
    // plumbing uses — LayerSerializer, the LRU document cache, RecentFiles,
    // VisualistWindowRegistry, and RefreshLayers — rather than ad-hoc IO, so
    // the open-document guard, undo-cache preservation, and rail selection all
    // stay consistent with New / Open / Save. Driven by LayerRail's context
    // menu (LayerRail.xaml.cs), which owns the dialogs (rename prompt / delete
    // confirm) and surfaces the returned status.

    /// <summary>Outcome of a rail-driven layer file operation. The LayerRail maps
    /// a non-Ok result to a user-facing message; the detail (exception) is already
    /// in the System Log via GlobalLogger.</summary>
    public enum LayerFileOpResult
    {
        Ok,
        NotFound,
        NameInvalid,
        NameTaken,
        OpenInSiblingWindow,
        IoError,
    }

    /// <summary>
    /// Rename the <c>.phxlayer</c> at <paramref name="sourcePath"/> to
    /// <paramref name="newName"/> (sanitized to a safe filename). When the layer
    /// is the currently-open document its unsaved edits are preserved: the live
    /// document is written to the new path (SaveAs) and the old file removed, so
    /// the rename never loses work and the document stays open under the new
    /// path. A cached-but-not-active layer keeps its in-memory document (and undo
    /// history) via a cache re-key. Re-enumerates and keeps the renamed layer
    /// selected.
    /// </summary>
    public LayerFileOpResult RenameLayer(string sourcePath, string newName)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return LayerFileOpResult.NotFound;

        string? safe = SanitizeLayerBaseName(newName);
        if (safe is null) return LayerFileOpResult.NameInvalid;

        string dir = Path.GetDirectoryName(sourcePath) ?? Paths.HubLayers;
        string target = Path.Combine(dir, safe + ".phxlayer");

        // No-op rename (identical name / case-only on a case-insensitive FS).
        if (LayerPathEquals(target, sourcePath)) return LayerFileOpResult.Ok;
        if (File.Exists(target)) return LayerFileOpResult.NameTaken;

        // Refuse when a sibling Visualist window owns the file — renaming it out
        // from under that window would strand its live document.
        if (Phoenix.Controls.Visualist.WinUI.Hosting.VisualistWindowRegistry.FindByPath(sourcePath) is not null)
            return LayerFileOpResult.OpenInSiblingWindow;

        bool isActive = _document?.FilePath is { } fp && LayerPathEquals(fp, sourcePath);
        try
        {
            if (isActive)
            {
                // Pre-flight the serialize before mutating FilePath / deleting the
                // old file: SaveAs sets FilePath BEFORE writing, so a serialize
                // failure (e.g. a non-finite keyframe) would otherwise strand the
                // document on a path with no file.
                _ = LayerSerializer.Serialize(_document!.Layer);
                _document!.SaveAs(target);
                try { File.Delete(sourcePath); }
                catch (Exception ex)
                {
                    GlobalLogger.Error("VisualistViewModel", $"RenameLayer delete old '{sourcePath}'", ex);
                }
            }
            else
            {
                File.Move(sourcePath, target);
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", $"RenameLayer('{sourcePath}'→'{target}')", ex);
            return LayerFileOpResult.IoError;
        }

        // Preserve the cached in-memory document (and its undo/redo stacks) under
        // the new key — mirrors the SaveLayerAs re-key so a re-select after rename
        // doesn't drop history or discard unsaved edits.
        RekeyLayerCache(sourcePath, target);
        Phoenix.Controls.Visualist.WinUI.Services.RecentFiles.Remove(sourcePath);
        Phoenix.Controls.Visualist.WinUI.Services.RecentFiles.Touch(target);

        RefreshLayers();
        SelectSavedRowByPath(target);
        PersistLastLayerPath(target);
        return LayerFileOpResult.Ok;
    }

    /// <summary>
    /// Copy the layer at <paramref name="sourcePath"/> to a collision-safe
    /// "&lt;name&gt; copy" file, re-enumerate, and select the copy. When the source
    /// is the live (or a cached) document with unsaved edits, the CURRENT
    /// in-memory state is serialized into the copy (without side-effecting the
    /// original) so "duplicate" copies what the user sees, not the stale bytes on
    /// disk.
    /// </summary>
    public LayerFileOpResult DuplicateLayer(string sourcePath, out string? newPath)
    {
        newPath = null;
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return LayerFileOpResult.NotFound;

        string dir = Path.GetDirectoryName(sourcePath) ?? Paths.HubLayers;
        string baseName = Path.GetFileNameWithoutExtension(sourcePath);
        string target = ResolveCollisionSafeCopyPath(dir, baseName);

        try
        {
            LayerDocument? live = ResolveLiveDoc(sourcePath);
            if (live is not null)
                LayerSerializer.Write(target, live.Layer);
            else
                File.Copy(sourcePath, target, overwrite: false);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", $"DuplicateLayer('{sourcePath}'→'{target}')", ex);
            return LayerFileOpResult.IoError;
        }

        Phoenix.Controls.Visualist.WinUI.Services.RecentFiles.Touch(target);
        RefreshLayers();
        SelectSavedRowByPath(target);
        newPath = target;
        return LayerFileOpResult.Ok;
    }

    /// <summary>
    /// Delete the <c>.phxlayer</c> at <paramref name="sourcePath"/> from disk.
    /// The caller MUST confirm first (LayerRail shows the confirm dialog). Guards
    /// the currently-open document by closing it (which cancels its auto-save
    /// timer so it can't recreate the file) and dropping it from the LRU cache /
    /// recent list before the delete, then re-enumerates and selects a sensible
    /// neighbour — or seeds a fresh scratch layer when no saved layers remain, so
    /// the canvas never dead-ends. Deleting a non-open layer leaves the current
    /// document/selection untouched.
    /// </summary>
    public LayerFileOpResult DeleteLayer(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return LayerFileOpResult.NotFound;

        if (Phoenix.Controls.Visualist.WinUI.Hosting.VisualistWindowRegistry.FindByPath(sourcePath) is not null)
            return LayerFileOpResult.OpenInSiblingWindow;

        bool deletingOpen = (_document?.FilePath is { } fp && LayerPathEquals(fp, sourcePath))
                         || (_selectedLayerItem is { IsUnsaved: false } sel && LayerPathEquals(sel.Path, sourcePath));

        // Neighbour computed against the pre-delete roster so selection lands on
        // an adjacent layer rather than jumping to the top of the list.
        string? neighbour = ComputeNeighbourSavedPath(sourcePath);

        // Close the live document for this path first: dropping it cancels the
        // auto-save timer (Dispose) so a debounced write can't resurrect the file
        // after we delete it, and we don't keep a document pointing at a ghost.
        if (deletingOpen) Document = null;

        if (_layerCache.TryGetValue(sourcePath, out var cachedDoc))
        {
            _layerCache.Remove(sourcePath);
            _layerCacheLru.Remove(sourcePath);
            try { cachedDoc.Dispose(); }
            catch (Exception ex) { GlobalLogger.Error("VisualistViewModel", "DeleteLayer dispose cache", ex); }
        }

        try
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", $"DeleteLayer('{sourcePath}')", ex);
            return LayerFileOpResult.IoError;
        }

        Phoenix.Controls.Visualist.WinUI.Services.RecentFiles.Remove(sourcePath);
        RefreshLayers();

        if (deletingOpen)
        {
            if (neighbour is not null)
                SelectSavedRowByPath(neighbour);
            else if (!Layers.Any(l => !l.IsUnsaved))
                NewLayer();
        }
        return LayerFileOpResult.Ok;
    }

    // ── rail-management helpers ──────────────────────────────────────────

    /// <summary>Sanitize a user-supplied layer name into a safe base filename
    /// (no extension), or null when nothing usable remains. Invalid filename
    /// characters become underscores; a typed <c>.phxlayer</c> suffix and illegal
    /// trailing dots / spaces are stripped.</summary>
    public static string? SanitizeLayerBaseName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string s = name.Trim();
        if (s.EndsWith(".phxlayer", StringComparison.OrdinalIgnoreCase))
            s = s.Substring(0, s.Length - ".phxlayer".Length);

        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

        string cleaned = sb.ToString().Trim().TrimEnd('.', ' ');
        return cleaned.Length == 0 ? null : cleaned;
    }

    // "<base> copy.phxlayer", then "<base> copy 2", "<base> copy 3", … until an
    // unused name is found in <paramref name="dir"/>.
    private static string ResolveCollisionSafeCopyPath(string dir, string baseName)
    {
        string candidate = Path.Combine(dir, $"{baseName} copy.phxlayer");
        if (!File.Exists(candidate)) return candidate;
        for (int n = 2; n < 10000; n++)
        {
            candidate = Path.Combine(dir, $"{baseName} copy {n}.phxlayer");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{baseName} copy {Guid.NewGuid():N}.phxlayer");
    }

    // Case-insensitive full-path equality (Windows FS). Null / empty never match.
    private static bool LayerPathEquals(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    // The live LayerDocument for a path: the active document, or a cached one.
    private LayerDocument? ResolveLiveDoc(string path)
    {
        if (_document?.FilePath is { } fp && LayerPathEquals(fp, path)) return _document;
        return _layerCache.TryGetValue(path, out var d) ? d : null;
    }

    // Move an LRU cache entry (and its document + undo stacks) from old → new key.
    private void RekeyLayerCache(string oldPath, string newPath)
    {
        if (!_layerCache.TryGetValue(oldPath, out var doc)) return;
        _layerCache.Remove(oldPath);
        _layerCacheLru.Remove(oldPath);
        _layerCache[newPath] = doc;
        _layerCacheLru.AddFirst(newPath);
    }

    // Path of the saved row adjacent to <paramref name="sourcePath"/> (next, else
    // previous), or null when it's the only saved row / not found.
    private string? ComputeNeighbourSavedPath(string sourcePath)
    {
        var saved = Layers.Where(l => !l.IsUnsaved).ToList();
        int idx = saved.FindIndex(l => LayerPathEquals(l.Path, sourcePath));
        if (idx < 0) return null;
        if (idx + 1 < saved.Count) return saved[idx + 1].Path;
        if (idx - 1 >= 0) return saved[idx - 1].Path;
        return null;
    }

    // Select (and load) the saved rail row for a path. Idempotent when it's
    // already the selection: re-binds through LoadSelectedLayer, which is a
    // no-op cache hit for the already-open document.
    private void SelectSavedRowByPath(string path)
    {
        LayerListItem? row = Layers.FirstOrDefault(l => !l.IsUnsaved && LayerPathEquals(l.Path, path));
        if (row is null) return;
        if (ReferenceEquals(row, _selectedLayerItem)) { LoadSelectedLayer(); return; }
        SelectedLayerItem = row;
    }

    // Persist the last-open saved layer path (guarded against redundant writes).
    private void PersistLastLayerPath(string? path)
    {
        try
        {
            var cfg = Phoenix.Controls.Visualist.WinUI.Core.VisualistUserConfig.Instance;
            if (string.Equals(cfg.LastLayerPath ?? string.Empty, path ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                return;
            cfg.Update(c => c.LastLayerPath = path);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", "PersistLastLayerPath", ex);
        }
    }

    public LayerWidget? AddWidget()
    {
        if (_document is null) return null;
        try
        {
            LayerWidget w = _document.AddWidget("New Widget");
            // Document.OnChanged will fire — but SelectedLayer is the same instance,
            // so explicitly notify Widgets-changed via SelectedLayer re-fire below.
            OnPropertyChanged(nameof(SelectedLayer));
            SelectedWidget = w;
            return w;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", "AddWidget failed", ex);
            return null;
        }
    }

    /// <summary>
    /// Apply a built-in widget
    /// preset to either an existing target widget or a newly-spawned one.
    /// <para>
    /// The "preset gallery" surface currently exposes the built-in
    /// <see cref="WidgetPreset"/> enum (Image / Video / Text / Audio /
    /// WebSource / Particles / Chat / CC). User-authored "save as preset"
    /// is out of scope for now — when that ships, this method's signature
    /// shifts to take a preset id string and resolves through a disk
    /// catalogue instead.
    /// </para>
    /// <para>
    /// When <paramref name="targetWidgetId"/> is supplied and matches a
    /// widget in the current layer, that widget's preset + onStartup graph
    /// are replaced (via PushUndo + MarkDirty). When <c>null</c>, a fresh
    /// widget spawns with the preset's starting graph — mirrors the
    /// LayerRail "Add Widget" + "spawn from palette" flow but seeded with
    /// the preset's chain.
    /// </para>
    /// <para>
    /// Returns the widget that was mutated/spawned. <c>null</c> means only "there is no widget to
    /// talk about" — an atomic refusal, or a throw caught before the first mutation. It is NOT a
    /// "nothing happened" signal: a throw caught AFTER the change hands the half-applied widget
    /// back, so a non-null return is not proof the apply completed either. A caller that needs to
    /// know what actually happened must use the four-argument overload's
    /// <see cref="WidgetPresets.PresetApplyOutcome"/>.
    /// </para>
    /// </summary>
    public LayerWidget? ApplyPreset(WidgetPreset preset, string? targetWidgetId = null)
        => ApplyPreset(preset, targetWidgetId, out _);

    /// <summary>
    /// <see cref="ApplyPreset(WidgetPreset, string?)"/> with the refusal made legible to the
    /// caller. <paramref name="refusal"/> is <c>null</c> when the preset was applied with nothing
    /// left undone; otherwise it names why, using the same
    /// <see cref="WidgetPresets.AlertBoxRegenResult"/> vocabulary the Inspector already renders,
    /// so the two surfaces cannot describe the same outcome differently.
    ///
    /// <para><b>★ Why this overload had to exist.</b> Every Alert Box refusal used to be
    /// indistinguishable from success: the two all-or-nothing skips below returned the untouched
    /// target, and a compile refusal returned the mutated one, so the preset gallery's
    /// <c>spawned is null</c> test printed "Applied &lt;label&gt; to &lt;name&gt;" for all three.
    /// A streamer who detached a widget and re-picked Alert Box to reset it was told it
    /// worked while nothing had happened.</para>
    ///
    /// <para>The return value and <paramref name="refusal"/> carry DIFFERENT facts and both are
    /// needed. A non-null return with a non-null <paramref name="refusal"/> means the preset
    /// really was applied but the alert chain was not generated (an invalid trigger id, or the
    /// node registry not being populated — both only decidable after the preset flip). Collapsing
    /// that case into <c>null</c> would tell the author nothing happened when their idle graph had
    /// in fact just been replaced.</para>
    ///
    /// <para><b>★ The return value is NOT a sufficient "did it happen" test</b> — use the
    /// four-argument overload's <c>outcome</c> for that. <c>null</c> means exactly "there is no
    /// widget to talk about": a clean refusal, or a throw caught BEFORE the first mutation. A throw
    /// caught AFTER the destructive half returns the half-applied widget instead (see
    /// <see cref="WidgetPresets.PresetApplyOutcome.FailedAfterChange"/> /
    /// <see cref="WidgetPresets.PresetApplyOutcome.FailedAfterSpawn"/>), because a surface has to be
    /// able to name it. So non-null does not mean "applied", and the three post-change states —
    /// applied, half-applied by a throw, half-built spawn — need three different messages that this
    /// overload cannot express: they differ only in the outcome.</para>
    /// </summary>
    public LayerWidget? ApplyPreset(
        WidgetPreset preset,
        string? targetWidgetId,
        out WidgetPresets.AlertBoxRegenResult? refusal)
        => ApplyPreset(preset, targetWidgetId, out refusal, out _);

    /// <summary>
    /// <see cref="ApplyPreset(WidgetPreset, string?, out WidgetPresets.AlertBoxRegenResult?)"/>
    /// plus <paramref name="outcome"/> — <b>what actually happened</b>, which no surface can
    /// correctly re-derive from the other two values.
    ///
    /// <para><b>★ Why the outcome is computed here and not at the call site.</b> Two bugs came
    /// out of re-derivation, and both were invisible to a reader of the call site:</para>
    /// <list type="number">
    /// <item><b>Spawn reported as an apply.</b> The preset gallery decided between
    /// "Spawned …" and "Applied … to …" from whether it had been handed a
    /// <paramref name="targetWidgetId"/> when it OPENED. It is a non-modal window, so an author
    /// can delete that widget on the canvas and then press Apply: the lookup below finds nothing,
    /// the spawn branch runs, and the strip says a widget was converted.</item>
    /// <item><b>A post-change throw reported as "not applied".</b> The catch arm returns
    /// <c>null</c> + <see cref="WidgetPresets.AlertBoxRegenResult.Failed"/> whether the throw
    /// happened before the first mutation or after the destructive half — undo pushed, preset
    /// flipped, <c>onStartup.Graph</c> replaced. The tail of the existing-widget branch is
    /// exactly where such a throw is reachable: <c>MarkDirty</c> fans out to every document
    /// subscriber, and the <c>SelectedWidget</c> setter re-enters inspector / canvas rebuild
    /// code. The author read "not applied", did not press Ctrl+Z, and their authored idle graph
    /// was gone. <see cref="WidgetPresets.PresetApplyOutcome.FailedAfterChange"/> is that case,
    /// and the undo entry taken at the top of the branch is what makes the recovery it advertises
    /// real.</item>
    /// <item><b>A failed SPAWN reported as a failed apply.</b> The post-change tracker was a bool,
    /// so a throw on the spawn branch — the compile, the notify, or the <c>SelectedWidget</c>
    /// setter, all of which run after <c>AddWidget</c> has pushed undo and added the widget —
    /// surfaced as <see cref="WidgetPresets.PresetApplyOutcome.FailedAfterChange"/> too, whose
    /// wording tells the author Ctrl+Z restores the widget. It does not: the widget was CREATED by
    /// this call, so Ctrl+Z removes it and there is no earlier state.
    /// <see cref="WidgetPresets.PresetApplyOutcome.FailedAfterSpawn"/> is that case, and it exists
    /// for the same reason <see cref="WidgetPresets.PresetApplyOutcome.Spawned"/> and
    /// <see cref="WidgetPresets.PresetApplyOutcome.Applied"/> are separate on the success side.</item>
    /// </list>
    ///
    /// <para><see cref="WidgetPresets.PresetApplyOutcome.RefusedNoChange"/> keeps its old meaning
    /// EXACTLY — refused before any change, model untouched, no undo entry consumed. It is still
    /// the ONLY outcome a surface may describe as "not applied".</para>
    /// </summary>
    public LayerWidget? ApplyPreset(
        WidgetPreset preset,
        string? targetWidgetId,
        out WidgetPresets.AlertBoxRegenResult? refusal,
        out WidgetPresets.PresetApplyOutcome outcome)
    {
        refusal = null;
        outcome = WidgetPresets.PresetApplyOutcome.RefusedNoChange;
        if (_document is null)
        {
            // Its own outcome, and it LOGS — see AlertBoxRegenResult.NoDocument. Reported as
            // Failed this arm sent the author to a System Log it had never written to.
            GlobalLogger.Log(
                $"ApplyPreset({preset}) dropped: no Visualist layer is open (target='{targetWidgetId}'). "
                + "Nothing was changed — open or create a layer first.",
                source: "VisualistViewModel",
                level: Phoenix.Controls.Shared.Models.LogLevel.System);
            refusal = WidgetPresets.AlertBoxRegenResult.NoDocument;
            return null;
        }

        // ── the post-change tracker ───────────────────────────────────────────
        // Set only once BOTH halves of "the author's work is now at risk" hold: the undo snapshot
        // exists AND the model has been written. Null before that point, which is the honest
        // "nothing changed"; a throw after it must never be reported as one.
        //
        // ★ It carries WHICH change happened, not merely THAT one did, and that is the whole
        // reason it is an outcome rather than a bool. The recovery gesture is inverted between the
        // two branches: on the spawn branch Ctrl+Z REMOVES the widget this apply created, on the
        // existing-widget branch it RESTORES the state the apply replaced. A single bool collapsed
        // both into FailedAfterChange, so a failed spawn told the author to press Ctrl+Z "to
        // restore" a widget that had just been created — a real gesture with an inverted
        // description, which sends them looking for work that was never lost.
        WidgetPresets.PresetApplyOutcome? failure = null;
        LayerWidget? touched = null;
        try
        {
            LayerWidget? target = null;
            if (!string.IsNullOrEmpty(targetWidgetId))
            {
                target = _document.Layer.Widgets.FirstOrDefault(w =>
                    string.Equals(w.Id, targetWidgetId, StringComparison.Ordinal));
            }

            // ★ V8 — a DETACHED Alert Box is off-limits to the whole method, not just to
            // regeneration. Re-picking "AlertBox" in the gallery on a widget whose graph
            // the author took ownership of would otherwise replace its onStartup with the
            // preset's bare Display sink (AlertBox's starting graph) and wipe whatever idle
            // state they built — a silent overwrite of exactly the work the one-way detach
            // exists to protect. Detach is one-way, so the only correct answer is to do
            // nothing and say so.
            //
            // Returns NULL, not the target: the caller has to be able to tell a refusal from an
            // apply, and "nothing changed" is exactly what null means on this method.
            if (preset == WidgetPreset.AlertBox && target?.AlertBox is { Detached: true })
            {
                GlobalLogger.Log(
                    $"ApplyPreset(AlertBox) skipped: widget '{target.Name}' ({target.Id}) was detached "
                    + "to a hand-owned graph. Detach is one-way — create a new Alert Box widget instead.",
                    source: "VisualistViewModel",
                    level: Phoenix.Controls.Shared.Models.LogLevel.System);
                refusal = WidgetPresets.AlertBoxRegenResult.Detached;
                return null;
            }

            // ★ V8 — and the same all-or-nothing answer when the settings' trigger name is
            // already taken by a hand-authored trigger. The compiler refuses on its own, but a
            // refusal on its own is not enough HERE: this method replaces the target's onStartup
            // graph on the way to the compile, so continuing would wipe the idle graph and then
            // install no chain — a half-applied preset from one gallery click. Pre-flighting the
            // collision keeps the whole apply atomic.
            //
            // ★ The PRESET is handed to the pre-flight, and that is load-bearing rather than
            // cosmetic. This check runs BEFORE `target.Preset = preset` (it has to — after that
            // write the onStartup graph is already gone), so on the conversion this guard was
            // added for — a Text / Image widget being turned INTO an Alert Box from the gallery —
            // the widget is still tagged Text with a null AlertBox. The one-argument overload
            // early-outs on both of those and answers "no collision" for a widget that is about
            // to collide, which made the guard inert on its only real path.
            if (preset == WidgetPreset.AlertBox
                && WidgetPresets.AlertBoxTriggerNameTaken(target, preset))
            {
                GlobalLogger.Log(
                    $"ApplyPreset(AlertBox) skipped: widget '{target!.Name}' ({target.Id}) already has a "
                    + $"hand-authored '{target.AlertBox?.ResolvedTriggerName ?? "onTrigger:" + AlertBoxSettings.DefaultTriggerId}' "
                    + "trigger that these settings did not generate. Nothing was changed — rename that "
                    + "trigger, or give the Alert Box a different trigger id first.",
                    source: "VisualistViewModel",
                    level: Phoenix.Controls.Shared.Models.LogLevel.System);
                refusal = WidgetPresets.AlertBoxRegenResult.TriggerNameTaken;
                return null;
            }

            // No target → spawn a fresh widget seeded with the preset.
            // Document.AddWidget already calls PushUndo + MarkDirty.
            if (target is null)
            {
                LayerWidget spawned = _document.AddWidget(preset.ToString(), preset);
                // AddWidget only seeds onStartup, which for an Alert Box is deliberately a
                // bare Display sink. The alert chain lives on its own onTrigger:<id>, so
                // the spawn is only half a widget until the compiler runs. Regenerating
                // here (rather than inside LayerDocument.AddWidget) keeps the engine's
                // widget factory preset-agnostic and puts the one compiled preset's extra
                // step on the surface that already owns "apply a preset".
                //
                // A refusal here does NOT unspawn the widget (the spawn is a legitimate,
                // already-undoable act), so the widget is returned and the reason is reported
                // alongside it: the author gets an Alert Box with no chain and is told exactly
                // that, instead of "Spawned 'AlertBox' on canvas" over a dead widget.
                //
                // ★ The spawn is itself the change, so the tracker is armed BEFORE anything else
                // on this branch can throw: AddWidget has already pushed undo and added the widget
                // to the layer. A throw from the compile, the notify or the SelectedWidget setter
                // must therefore report FailedAfterSpawn — there is a new widget on the canvas and
                // Ctrl+Z REMOVES it. Not FailedAfterChange: that outcome's wording promises Ctrl+Z
                // restores a previous state, and a widget this call created has none.
                failure = WidgetPresets.PresetApplyOutcome.FailedAfterSpawn;
                touched = spawned;
                if (preset == WidgetPreset.AlertBox)
                {
                    var spawnResult = RegenerateAlertBoxTrigger(spawned, "spawn");
                    if (spawnResult != WidgetPresets.AlertBoxRegenResult.Regenerated)
                        refusal = spawnResult;
                }
                OnPropertyChanged(nameof(SelectedLayer));
                SelectedWidget = spawned;
                outcome = WidgetPresets.PresetApplyOutcome.Spawned;
                return spawned;
            }

            // Existing widget → replace preset + onStartup graph in-place.
            _document.PushUndo();
            target.Preset = preset;
            // ★ Armed HERE, between the two writes, and the position is deliberate:
            //  * before PushUndo → a PushUndo throw would claim a change that never happened AND
            //    advertise a Ctrl+Z that has no entry to consume;
            //  * after the onStartup replacement → the graph would already be gone while the
            //    tracker still said "nothing changed", which is the exact lie being fixed.
            // At this line the snapshot exists and the model has been written, so every remaining
            // statement in this branch — the graph replacement, the compile, MarkDirty, the
            // notifies, the SelectedWidget setter — is inside the post-change window. This branch
            // mutates a widget that already existed, so its recovery really is a RESTORE.
            failure = WidgetPresets.PresetApplyOutcome.FailedAfterChange;
            touched = target;
            // Find / replace the onStartup trigger so the new preset's
            // starter chain takes effect. Keep any other authored triggers
            // intact — they were named by the user.
            var startup = target.Triggers.FirstOrDefault(t =>
                string.Equals(t.Name, "onStartup", StringComparison.OrdinalIgnoreCase));
            if (startup is null)
            {
                startup = new WidgetTrigger
                {
                    Name = "onStartup",
                    Graph = WidgetPresets.GetStartingGraph(preset),
                    Timeline = new WidgetTimeline(),
                };
                target.Triggers.Insert(0, startup);
            }
            else
            {
                startup.Graph = WidgetPresets.GetStartingGraph(preset);
            }
            // Same reasoning as the spawn branch: onStartup is the alert's IDLE state and
            // the chain is a separate trigger, so applying the preset has to compile too.
            //
            // The two atomic refusals were pre-flighted above, so what can still surface here is
            // a value the preset flip itself made decidable: an invalid TriggerId carried by
            // settings the widget already had, or an unpopulated node registry. The preset HAS
            // been applied at this point, so the widget is returned — but the missing chain is
            // reported, because a returned widget with an empty CompiledTriggerName otherwise
            // reads as a complete apply.
            if (preset == WidgetPreset.AlertBox)
            {
                var applyResult = RegenerateAlertBoxTrigger(target, "apply-preset");
                if (applyResult != WidgetPresets.AlertBoxRegenResult.Regenerated)
                    refusal = applyResult;
            }
            _document.MarkDirty();
            OnPropertyChanged(nameof(SelectedLayer));
            // Re-fire SelectedWidget if it's the target so the inspector /
            // canvas re-bind to the updated triggers.
            if (ReferenceEquals(target, _selectedWidget))
            {
                OnPropertyChanged(nameof(SelectedWidget));
            }
            else
            {
                SelectedWidget = target;
            }
            outcome = WidgetPresets.PresetApplyOutcome.Applied;
            return target;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel",
                $"ApplyPreset({preset}, target='{targetWidgetId}') failed", ex);
            refusal = WidgetPresets.AlertBoxRegenResult.Failed;
            // ★ The whole point of the tracker. This arm used to report every throw as the
            // "nothing changed" shape, including a throw from the TAIL of the existing-widget
            // branch — by which point undo had been pushed, the preset flipped and the authored
            // onStartup graph replaced by the new preset's starter. The gallery printed "not
            // applied", the author did not press Ctrl+Z, and the idle graph was gone.
            outcome = failure ?? WidgetPresets.PresetApplyOutcome.RefusedNoChange;
            // Post-change failures hand the widget back so a surface can name it; a genuine
            // refusal keeps returning null, which is the contract every caller already reads.
            if (failure is null) return null;
            // ★ And the two post-change shapes get OPPOSITE recovery wording. Collapsed into one
            // line, a failed spawn read "press Ctrl+Z to restore the previous state" over a widget
            // that had just been created — Ctrl+Z removes it, there is nothing to restore, and the
            // author goes hunting for work that was never lost.
            GlobalLogger.Log(
                failure == WidgetPresets.PresetApplyOutcome.FailedAfterSpawn
                    ? $"ApplyPreset({preset}) failed AFTER the new widget had already been spawned "
                      + $"('{touched?.Name}' / {touched?.Id}). The widget exists but is only "
                      + "half-built — press Ctrl+Z to REMOVE it (it was created by this apply, so "
                      + "there is no earlier state to go back to); the undo entry exists."
                    : $"ApplyPreset({preset}) failed AFTER the widget had already been changed "
                      + $"('{touched?.Name}' / {touched?.Id}). The preset is half-applied — press "
                      + "Ctrl+Z to restore the previous state; the undo entry for this apply exists.",
                source: "VisualistViewModel",
                level: Phoenix.Controls.Shared.Models.LogLevel.System);
            return touched;
        }
    }

    // ─── V8: Alert Box (the one COMPILED preset) ─────────────────────────
    //
    // Ownership, per Majo's D-V8: while AlertBoxSettings.Detached is false the settings
    // ALWAYS regenerate the widget's onTrigger:<id> graph — a hand edit to that graph is
    // lost on the next settings commit. That is a data-loss shape, so it is stated to the
    // author in a persistent Inspector banner AND logged on every regeneration; it is
    // never discovered. The one-way Detach below is the sanctioned exit.
    //
    // Every mutation here goes through WidgetPresets.RegenerateAlertBox, which is the
    // single place the Detached guard lives — see its remarks for why there is no `force`
    // override.

    /// <summary>
    /// Commit an Alert Box widget's settings: regenerate its compiled trigger, push one
    /// undo entry, mark the document dirty and re-broadcast so the Inspector's TRIGGERS
    /// list and the canvas pick the change up. Returns what actually happened so the
    /// caller can surface an accurate message instead of a generic failure.
    ///
    /// <para><b>★ Every refusal that does not depend on the pending edit is pre-flighted
    /// BEFORE the undo push</b>, and not merely for tidiness: <c>LayerDocument.Undo()</c>
    /// replaces <c>Layer</c> with a freshly DESERIALIZED instance, so the obvious "push, try,
    /// undo on refusal" shape would swap the whole object graph out from under
    /// <c>_selectedWidget</c> and leave the Inspector editing an orphaned widget that no save
    /// would ever see. When a <paramref name="mutate"/> callback IS supplied the two
    /// value-dependent refusals (invalid trigger id, trigger name taken) can only be judged
    /// after it has run — the author's edit has then really happened, so the entry taken ahead
    /// of it is exactly what makes the refused edit recoverable with Ctrl+Z.</para>
    ///
    /// <para><b>★ <paramref name="mutate"/> is how the setting itself gets written, and it is
    /// not optional plumbing.</b> <c>PushUndo</c> serialises the CURRENT layer, so a caller
    /// that writes the setting first and commits second puts the NEW value inside the "before"
    /// snapshot: Ctrl+Z then restores the graph the commit destroyed but leaves the setting
    /// that destroyed it in place, and the author — reasonably concluding undo failed — edits
    /// again and loses the graph a second time. Handing the write in as a callback makes
    /// snapshot-then-mutate the only possible order, at the choke point, for every caller.</para>
    ///
    /// <para><paramref name="pushUndo"/> = false when the CALLER already snapshotted for
    /// the same gesture — the preset-picker path, where flipping a widget to AlertBox and
    /// compiling its trigger must be ONE undo entry, not two (an author pressing Ctrl+Z
    /// once and getting a preset with no chain would be worse than either end state).</para>
    /// </summary>
    public WidgetPresets.AlertBoxRegenResult CommitAlertBoxSettings(
        LayerWidget? widget,
        Action<AlertBoxSettings>? mutate = null,
        bool pushUndo = true)
    {
        if (widget is null) return WidgetPresets.AlertBoxRegenResult.NotAnAlertBox;
        // ★ Split out of the guard above, where it was TWO defects on one line.
        //
        // It returned NotAnAlertBox, which every surface renders as "Not an Alert Box widget." —
        // a false statement about the author's selection on a path where the selection is fine.
        // And it is genuinely reachable: a media row's Browse… chip awaits a MediaPickerDialog,
        // so its commit closure can resolve after the layer it belongs to has been closed
        // (InspectorPanel.BuildAlertMediaRow's browse handler). The gallery meanwhile mapped it to
        // Failed, whose wording is "check the System Log" — over a path that logged NOTHING, so
        // the one instruction the author was given led to an empty log.
        //
        // Its own outcome, an honest message, and a log line.
        if (_document is null)
        {
            GlobalLogger.Log(
                $"Alert Box '{widget.Name}' ({widget.Id}): settings commit dropped — no Visualist "
                + "layer is open any more. An edit that was still in flight when the layer closed "
                + "(a media picker being awaited is the usual way) cannot be saved. Nothing was "
                + "changed; re-open the layer and make the edit again.",
                source: "VisualistViewModel",
                level: Phoenix.Controls.Shared.Models.LogLevel.System);
            return WidgetPresets.AlertBoxRegenResult.NoDocument;
        }
        try
        {
            if (widget.Preset != WidgetPreset.AlertBox)
                return WidgetPresets.AlertBoxRegenResult.NotAnAlertBox;
            if (widget.AlertBox is { Detached: true })
                return WidgetPresets.AlertBoxRegenResult.Detached;

            // Pre-flight the refusals the compiler can still raise. With no pending edit both
            // value-dependent ones are already decidable, so they cost no undo entry; with one,
            // they are left to the compiler, which re-checks them after the write. A widget with
            // no settings yet is fine — the compiler attaches valid defaults.
            if (mutate is null && widget.AlertBox is { } pre)
            {
                if (pre.ResolvedTriggerName is null)
                {
                    LogAlertBoxOutcome(widget, WidgetPresets.AlertBoxRegenResult.InvalidTriggerId, "settings-commit");
                    return WidgetPresets.AlertBoxRegenResult.InvalidTriggerId;
                }
                if (WidgetPresets.AlertBoxTriggerNameTaken(widget))
                {
                    // Logged here rather than through LogAlertBoxOutcome: the compiler never runs
                    // on this path, and it is the compiler that owns the TriggerNameTaken line
                    // (adding an arm there too would double-log the path that DOES reach it).
                    GlobalLogger.Log(
                        $"Alert Box '{widget.Name}' ({widget.Id}): '{pre.ResolvedTriggerName}' is a "
                        + "hand-authored trigger these settings did not generate — nothing compiled. "
                        + "Change the Alert Box trigger id, or rename that trigger.",
                        source: "VisualistViewModel",
                        level: Phoenix.Controls.Shared.Models.LogLevel.System);
                    return WidgetPresets.AlertBoxRegenResult.TriggerNameTaken;
                }
            }
            if (!WidgetPresets.AlertBoxTemplatesAvailable())
            {
                LogAlertBoxOutcome(widget, WidgetPresets.AlertBoxRegenResult.RegistryUnavailable, "settings-commit");
                return WidgetPresets.AlertBoxRegenResult.RegistryUnavailable;
            }

            if (pushUndo) _document.PushUndo();
            // AFTER the snapshot, never before — see the remarks. The settings object is
            // materialised here too (a tag-only widget has none) so the callback always gets a
            // live instance to write into, and that materialisation is inside the same entry.
            if (mutate is not null)
            {
                AlertBoxSettings settings = widget.AlertBox ??= AlertBoxSettings.CreateDefault();
                mutate(settings);
            }
            var result = RegenerateAlertBoxTrigger(widget, "settings-commit");
            // Dirty + rebroadcast on a success, and ALSO on a refusal that followed a real write:
            // the author's setting landed on the model even though nothing compiled, so it still
            // has to be saveable (and the undo entry above is what takes it back).
            if (result == WidgetPresets.AlertBoxRegenResult.Regenerated || mutate is not null)
            {
                _document.MarkDirty();
                RaiseSelectedLayerChanged();
            }
            return result;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", "CommitAlertBoxSettings failed", ex);
            // Failed, NEVER NotAnAlertBox. This arm used to return NotAnAlertBox, which every
            // surface renders as "Not an Alert Box widget." — so a genuine exception, the one
            // outcome where the author most needs pointing at the System Log, read as a
            // (false) statement about which widget they had selected.
            //
            // ★ Corrected: the round-2 version of this note claimed "NotAnAlertBox now means only
            // the real guard above", which was not true — the no-document arm still returned it,
            // so a closed layer still produced a false claim about the selection. That arm is now
            // AlertBoxRegenResult.NoDocument. NotAnAlertBox is left with exactly two cases, and
            // the rendered sentence is true of both: no widget at all, or a widget whose preset is
            // not AlertBox.
            return WidgetPresets.AlertBoxRegenResult.Failed;
        }
    }

    /// <summary>
    /// ONE-WAY: hand the compiled graph over to the author permanently. After this the
    /// settings never regenerate again, so whatever they build in the widget-graph editor
    /// survives every later settings edit.
    ///
    /// <para>Returns false when there is nothing to detach (not an Alert Box, no settings)
    /// or it is already detached — an idempotent no-op rather than a second undo entry.
    /// There is deliberately NO re-attach: re-attaching would overwrite the hand edits the
    /// detach existed to protect, and "undo" is the honest way back (it is one undo entry,
    /// like every other Inspector commit).</para>
    /// </summary>
    public bool DetachAlertBox(LayerWidget? widget)
    {
        if (widget is null || _document is null) return false;
        try
        {
            if (widget.Preset != WidgetPreset.AlertBox) return false;
            if (widget.AlertBox is not { } settings) return false;
            if (settings.Detached) return false;

            _document.PushUndo();
            settings.Detached = true;
            _document.MarkDirty();
            RaiseSelectedLayerChanged();
            GlobalLogger.Log(
                $"Alert Box '{widget.Name}' ({widget.Id}) DETACHED to a hand-owned graph "
                + $"(trigger '{settings.CompiledTriggerName}'). Its settings will never regenerate "
                + "the graph again; this is one-way (undo reverts it).",
                source: "VisualistViewModel",
                level: Phoenix.Controls.Shared.Models.LogLevel.System);
            return true;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", "DetachAlertBox failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Run the compiler and log the outcome. The success log is NOT chatter: this is the
    /// audit trail for a destructive operation — every regeneration throws away whatever
    /// the previous compiled graph contained, and the log plus the Inspector banner are
    /// together what keep that from being a surprise. Kept at System tier so it lands in
    /// the SystemLog panel the same way a trigger rename does.
    /// </summary>
    private WidgetPresets.AlertBoxRegenResult RegenerateAlertBoxTrigger(LayerWidget widget, string cause)
    {
        var result = WidgetPresets.RegenerateAlertBox(widget);
        LogAlertBoxOutcome(widget, result, cause);
        return result;
    }

    private static void LogAlertBoxOutcome(
        LayerWidget widget, WidgetPresets.AlertBoxRegenResult result, string cause)
    {
        var level = Phoenix.Controls.Shared.Models.LogLevel.System;
        switch (result)
        {
            case WidgetPresets.AlertBoxRegenResult.Regenerated:
                GlobalLogger.Log(
                    $"Alert Box '{widget.Name}' ({widget.Id}): regenerated trigger "
                    + $"'{widget.AlertBox?.CompiledTriggerName}' from settings ({cause}). "
                    + "The compiled graph is settings-owned — any hand edits to it were replaced.",
                    source: "VisualistViewModel", level: level);
                break;
            case WidgetPresets.AlertBoxRegenResult.InvalidTriggerId:
                GlobalLogger.Log(
                    $"Alert Box '{widget.Name}' ({widget.Id}): trigger name "
                    + $"'{widget.AlertBox?.TriggerId}' is not a valid identifier "
                    + "(letter first, then letters / digits / underscores) — nothing compiled.",
                    source: "VisualistViewModel", level: level);
                break;
            case WidgetPresets.AlertBoxRegenResult.RegistryUnavailable:
                GlobalLogger.Log(
                    $"Alert Box '{widget.Name}' ({widget.Id}): a node template the alert chain "
                    + "needs is not registered — nothing compiled.",
                    source: "VisualistViewModel", level: level);
                break;
            default:
                // Detached / NotAnAlertBox / TriggerNameTaken are normal, expected refusals and
                // are logged (or deliberately not) by their own call sites — no duplicate line
                // here. Failed likewise: the catch that produces it already wrote the exception
                // through GlobalLogger.Error, which carries the stack this could not. NoDocument
                // cannot arrive here at all — RegenerateAlertBox has no document to be missing —
                // and its own guard in CommitAlertBoxSettings / ApplyPreset does the logging.
                break;
        }
    }

    // ─── trigger lifecycle ──────────────────────────────────────────────

    /// <summary>Why an <see cref="AddTrigger(string, out AddTriggerStatus)"/> call
    /// did or didn't add a trigger, so the caller can show an accurate, non-generic
    /// message (the old single "already exists or isn't valid" line read as "always
    /// taken" to users who simply hadn't typed the <c>onTrigger:</c> prefix).</summary>
    public enum AddTriggerStatus { Added, NoWidget, Empty, Invalid, Duplicate }

    /// <summary>
    /// Canonicalize a user-typed trigger name into the shape
    /// <see cref="WidgetTrigger.IsValidName"/> accepts. The trigger-create prompt
    /// shows "onTrigger:new" only as a placeholder hint, but the validator requires
    /// the literal <c>onTrigger:</c> prefix — so a natural entry like "raid" was
    /// rejected and surfaced as "name already exists or isn't valid", which users
    /// read as "always taken". This makes the common cases just work:
    /// <list type="bullet">
    /// <item>a bare identifier ("raid") → "onTrigger:raid";</item>
    /// <item>internal spaces collapse to underscores ("Sub Alert" → "onTrigger:Sub_Alert");</item>
    /// <item>any-cased prefix is canonicalized ("ONTRIGGER:fire" → "onTrigger:fire", "onstartup" → "onStartup").</item>
    /// </list>
    /// Names still outside the identifier shape (stray punctuation, etc.) are left
    /// for <see cref="WidgetTrigger.IsValidName"/> to reject with a clear message
    /// rather than being silently mangled.
    /// </summary>
    public static string NormalizeTriggerName(string? raw)
    {
        string s = (raw ?? string.Empty).Trim();
        if (s.Length == 0) return string.Empty;

        // The implicit lifecycle trigger — accept any casing, canonicalize.
        if (string.Equals(s, "onStartup", StringComparison.OrdinalIgnoreCase)) return "onStartup";

        // Already prefixed (any casing): canonicalize the prefix, keep the tail as typed.
        const string prefix = "onTrigger:";
        if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return prefix + s.Substring(prefix.Length);

        // Bare name: collapse internal whitespace runs to single underscores and prefix.
        string ident = string.Join("_", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return prefix + ident;
    }

    public WidgetTrigger? AddTrigger(string name) => AddTrigger(name, out _);

    public WidgetTrigger? AddTrigger(string name, out AddTriggerStatus status)
    {
        status = AddTriggerStatus.Added;
        if (_document is null || _selectedWidget is null) { status = AddTriggerStatus.NoWidget; return null; }
        if (string.IsNullOrWhiteSpace(name)) { status = AddTriggerStatus.Empty; return null; }

        // Be forgiving about the input shape (bare names, casing, spaces) before
        // validating, so the create path accepts what a user naturally types.
        string normalized = NormalizeTriggerName(name);

        // Reject names the WidgetTrigger.Name setter would silently drop, so the
        // caller learns the create failed instead of getting a default-named trigger.
        if (!WidgetTrigger.IsValidName(normalized)) { status = AddTriggerStatus.Invalid; return null; }
        if (_selectedWidget.Triggers.Any(t => string.Equals(t.Name, normalized, StringComparison.OrdinalIgnoreCase)))
        { status = AddTriggerStatus.Duplicate; return null; }
        try
        {
            _document.PushUndo();
            var t = new WidgetTrigger
            {
                Name     = normalized,
                Graph    = new Graph(),
                Timeline = new WidgetTimeline(),
            };
            _selectedWidget.Triggers.Add(t);
            _document.MarkDirty();
            RebuildTriggerNames();
            ActiveTrigger = t.Name;
            return t;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", $"AddTrigger('{name}') failed", ex);
            status = AddTriggerStatus.Invalid;
            return null;
        }
    }

    public bool RenameTrigger(string oldName, string newName)
    {
        if (_document is null || _selectedWidget is null) return false;
        if (string.IsNullOrWhiteSpace(newName)) return false;
        WidgetTrigger? t = _selectedWidget.Triggers.FirstOrDefault(x =>
            string.Equals(x.Name, oldName, StringComparison.OrdinalIgnoreCase));
        if (t is null) return false;
        if (string.Equals(t.Name, newName, StringComparison.Ordinal)) return false;
        // Reject duplicate target name (case-insensitive).
        if (_selectedWidget.Triggers.Any(x =>
                !ReferenceEquals(x, t) &&
                string.Equals(x.Name, newName, StringComparison.OrdinalIgnoreCase)))
            return false;
        // Reject names the WidgetTrigger.Name setter would silently drop, so the
        // caller learns the rename failed instead of falsely reporting success.
        if (!WidgetTrigger.IsValidName(newName.Trim())) return false;
        try
        {
            _document.PushUndo();
            t.Name = newName.Trim();
            _document.MarkDirty();
            RebuildTriggerNames();
            ActiveTrigger = t.Name;
            return true;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", $"RenameTrigger('{oldName}'→'{newName}') failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Deep-clone the named trigger (graph + timeline)
    /// and append it to the current widget. The clone is named
    /// <c>onTrigger:&lt;identifier&gt;_copy</c> (or _copy2 / _copy3 / … if
    /// collisions exist) — underscore rather than a hyphen,
    /// because <see cref="WidgetTrigger.Name"/> rejects identifiers that don't
    /// match <c>^(onStartup|onTrigger:[A-Za-z][A-Za-z0-9_]*)$</c> and would
    /// silently drop "trigger-copy". <see cref="ActiveTrigger"/> snaps to the
    /// new trigger so the editor opens on the duplicate.
    /// </summary>
    public WidgetTrigger? DuplicateTrigger(string triggerName)
    {
        if (_document is null || _selectedWidget is null) return null;
        WidgetTrigger? src = _selectedWidget.Triggers.FirstOrDefault(x =>
            string.Equals(x.Name, triggerName, StringComparison.OrdinalIgnoreCase));
        if (src is null) return null;
        try
        {
            // Deep-clone via LayerSerializer round-trip — same pattern as
            // DuplicateSelectedWidget above. Wrap the source trigger in a
            // single-trigger throwaway widget on a throwaway layer so all the
            // existing converters (Color, KeyframeCurve, etc.) participate.
            var stubWidget = new LayerWidget { Name = "_dup_trig_stub" };
            stubWidget.Triggers.Add(src);
            var stubLayer = new Layer
            {
                Name       = "_dup_trig_stub",
                Resolution = new LayerResolution { Width = 1, Height = 1 },
            };
            stubLayer.Widgets.Add(stubWidget);
            string json = LayerSerializer.Serialize(stubLayer);
            Layer round = LayerSerializer.Deserialize(json);
            if (round.Widgets.Count == 0 || round.Widgets[0].Triggers.Count == 0)
                return null;
            WidgetTrigger clone = round.Widgets[0].Triggers[0];

            // Derive the new name. "onStartup" duplicates to
            // "onTrigger:onStartup_copy" (preserves uniqueness) — the regex
            // rejects bare "onStartup_copy" because that would collide with the
            // sentinel name. For onTrigger:foo we suffix with _copy / _copy2 /
            // _copy3 until we land an unused name; fall back to MakeFallbackName
            // if every candidate is taken (extreme edge case).
            string baseName;
            if (string.Equals(src.Name, "onStartup", StringComparison.Ordinal))
                baseName = "onTrigger:onStartup_copy";
            else if (src.Name.StartsWith("onTrigger:", StringComparison.Ordinal))
                baseName = src.Name + "_copy";
            else
                baseName = "onTrigger:" + WidgetTrigger.MakeFallbackName(src.Name);

            string candidate = baseName;
            int n = 2;
            while (_selectedWidget.Triggers.Any(t =>
                       string.Equals(t.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                candidate = baseName + n.ToString();
                n++;
                if (n > 9999)
                {
                    candidate = "onTrigger:" + WidgetTrigger.MakeFallbackName(src.Name + Guid.NewGuid());
                    break;
                }
            }
            // If the candidate doesn't satisfy the validator (shouldn't happen
            // given how it's assembled, but defense in depth), route through
            // SetForLoad so the clone keeps the literal name instead of being
            // silently rejected.
            if (WidgetTrigger.IsValidName(candidate))
                clone.Name = candidate;
            else
                clone.SetForLoad(candidate);

            _document.PushUndo();
            _selectedWidget.Triggers.Add(clone);
            _document.MarkDirty();
            RebuildTriggerNames();
            ActiveTrigger = clone.Name;
            return clone;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", $"DuplicateTrigger('{triggerName}') failed", ex);
            return null;
        }
    }

    /// <summary>
    /// Shift the named trigger one slot toward the start of the tab
    /// strip. No-op when the trigger is already first or not found.
    /// </summary>
    public bool MoveTriggerLeft(string triggerName) => MoveTrigger(triggerName, -1);

    /// <summary>
    /// Shift the named trigger one slot toward the end of the tab
    /// strip. No-op when the trigger is already last or not found.
    /// </summary>
    public bool MoveTriggerRight(string triggerName) => MoveTrigger(triggerName, +1);

    private bool MoveTrigger(string triggerName, int delta)
    {
        if (_document is null || _selectedWidget is null) return false;
        int idx = _selectedWidget.Triggers.FindIndex(t =>
            string.Equals(t.Name, triggerName, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return false;
        int target = idx + delta;
        if (target < 0 || target >= _selectedWidget.Triggers.Count) return false;
        try
        {
            _document.PushUndo();
            WidgetTrigger t = _selectedWidget.Triggers[idx];
            _selectedWidget.Triggers.RemoveAt(idx);
            _selectedWidget.Triggers.Insert(target, t);
            _document.MarkDirty();
            // Re-fire the observable collection that the tab strip binds to.
            // ActiveTrigger stays pinned to the moved trigger so the editor
            // doesn't jump to a different graph after a reorder.
            RebuildTriggerNames();
            ActiveTrigger = t.Name;
            return true;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", $"MoveTrigger('{triggerName}', {delta}) failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Reorder via direct index. Used by drag-to-reorder in the trigger
    /// tab strip when the source tab is dropped on a different position.
    /// No-op when either index is out of range or they're equal.
    /// </summary>
    public bool ReorderTrigger(int fromIndex, int toIndex)
    {
        if (_document is null || _selectedWidget is null) return false;
        if (fromIndex == toIndex) return false;
        if (fromIndex < 0 || fromIndex >= _selectedWidget.Triggers.Count) return false;
        if (toIndex   < 0 || toIndex   >= _selectedWidget.Triggers.Count) return false;
        try
        {
            _document.PushUndo();
            WidgetTrigger t = _selectedWidget.Triggers[fromIndex];
            _selectedWidget.Triggers.RemoveAt(fromIndex);
            _selectedWidget.Triggers.Insert(toIndex, t);
            _document.MarkDirty();
            RebuildTriggerNames();
            ActiveTrigger = t.Name;
            return true;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", $"ReorderTrigger({fromIndex}→{toIndex}) failed", ex);
            return false;
        }
    }

    public bool RemoveTrigger(string name)
    {
        if (_document is null || _selectedWidget is null) return false;
        WidgetTrigger? t = _selectedWidget.Triggers.FirstOrDefault(x =>
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (t is null) return false;
        try
        {
            _document.PushUndo();
            _selectedWidget.Triggers.Remove(t);
            _document.MarkDirty();
            RebuildTriggerNames();
            // Snap ActiveTrigger to the next-best survivor.
            ActiveTrigger = _selectedWidget.Triggers.FirstOrDefault()?.Name ?? "";
            return true;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", $"RemoveTrigger('{name}') failed", ex);
            return false;
        }
    }

    public bool DeleteSelectedWidget()
    {
        if (_document is null || _selectedWidget is null) return false;
        try
        {
            bool removed = _document.RemoveWidget(_selectedWidget);
            if (removed)
            {
                OnPropertyChanged(nameof(SelectedLayer));
                SelectedWidget = _document.Layer.Widgets.FirstOrDefault();
            }
            return removed;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", "DeleteSelectedWidget failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Delete every widget in the current multi-selection in a SINGLE undo entry
    /// (one whole-layer snapshot covers the whole batch), then re-anchor onto the
    /// first remaining widget. Returns true when at least one was removed. Mirrors
    /// LayerCanvasView.DeleteSelectedWidgets so the inspector's Delete button is
    /// symmetric with the canvas Del key when 2+ widgets are selected.
    /// </summary>
    public bool DeleteSelectedWidgets()
    {
        if (_document is null) return false;
        var targets = _selectedWidgets.Count > 1
            ? _selectedWidgets.ToList()
            : (_selectedWidget is { } w ? new List<LayerWidget> { w } : new List<LayerWidget>());
        if (targets.Count == 0) return false;
        try
        {
            // Decide what will actually be removed BEFORE snapshotting. A target
            // set holding nothing the layer still owns (stale selection after an
            // external removal / a document swap) used to push undo and then bail,
            // stranding a no-op entry the user has to Ctrl+Z twice through.
            // LayerCanvasView.DeleteSelectedWidgets gets this right by returning
            // ahead of its PushUndo; match it.
            var present = new List<LayerWidget>(targets.Count);
            foreach (var t in targets)
                if (_document.Layer.Widgets.Contains(t)) present.Add(t);
            if (present.Count == 0) return false;

            _document.PushUndo();
            foreach (var t in present)
                _document.Layer.Widgets.Remove(t);
            _document.MarkDirty();
            SetSelectedWidgets(System.Array.Empty<LayerWidget>());
            SelectedWidget = _document.Layer.Widgets.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedLayer));
            return true;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", "DeleteSelectedWidgets failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Shallow-clone the selected widget through LayerSerializer (round-trip
    /// the JSON to deep-copy nested Triggers/Graph/Timeline) and append it to
    /// the layer with a "(copy)" name suffix and a +20px offset so it doesn't
    /// stack invisibly on the original.
    /// </summary>
    public LayerWidget? DuplicateSelectedWidget()
    {
        if (_document is null || _selectedWidget is null) return null;
        try
        {
            // Cheapest deep-clone: serialize-then-deserialize a single-widget
            // throwaway layer through the canonical LayerSerializer so every
            // converter (Color, KeyframeCurve, etc.) round-trips identically.
            var stub = new Layer
            {
                Name       = "_dup_stub",
                Resolution = new LayerResolution { Width = 1, Height = 1 },
            };
            stub.Widgets.Add(_selectedWidget);
            string json = LayerSerializer.Serialize(stub);
            Layer round = LayerSerializer.Deserialize(json);
            if (round.Widgets.Count == 0) return null;
            LayerWidget clone = round.Widgets[0];
            clone.Id   = Guid.NewGuid().ToString();
            clone.Name = string.IsNullOrEmpty(_selectedWidget.Name)
                ? "(copy)"
                : $"{_selectedWidget.Name} (copy)";
            clone.Rect.X += 20;
            clone.Rect.Y += 20;
            clone.ZIndex = _document.Layer.Widgets.Count;

            _document.PushUndo();
            _document.Layer.Widgets.Add(clone);
            _document.MarkDirty();
            OnPropertyChanged(nameof(SelectedLayer));
            SelectedWidget = clone;
            return clone;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", "DuplicateSelectedWidget failed", ex);
            return null;
        }
    }

    public void BringSelectedToFront()
    {
        if (_document is null || _selectedWidget is null) return;
        if (_document.Layer.Widgets.Count == 0) return;
        int max = _document.Layer.Widgets.Max(w => w.ZIndex);
        if (_selectedWidget.ZIndex >= max) return;
        _document.PushUndo();
        // Clamp to a safe upper bound so repeated bring-to-front can't overflow
        // int.MaxValue and wrap to a negative ZIndex (corrupting paint order).
        _selectedWidget.ZIndex = Math.Min(max + 1, 1_000_000);
        _document.MarkDirty();
        OnPropertyChanged(nameof(SelectedLayer));
    }

    public void SendSelectedToBack()
    {
        if (_document is null || _selectedWidget is null) return;
        if (_document.Layer.Widgets.Count == 0) return;
        int min = _document.Layer.Widgets.Min(w => w.ZIndex);
        if (_selectedWidget.ZIndex <= min) return;
        _document.PushUndo();
        // Clamp to a safe lower bound so repeated send-to-back can't underflow
        // int.MinValue and wrap to a positive ZIndex (corrupting paint order).
        _selectedWidget.ZIndex = Math.Max(min - 1, -1_000_000);
        _document.MarkDirty();
        OnPropertyChanged(nameof(SelectedLayer));
    }

    /// <summary>
    /// Move the selected widget one slot toward the front (<paramref
    /// name="dir"/> = +1) or back (-1) in z-order, swapping ZIndex with its
    /// immediate neighbour. No-op at the extremes. Single-step parity with the
    /// pre-WinUI Ctrl+] / Ctrl+[ chords.
    /// </summary>
    public void NudgeSelectedZOrder(int dir)
    {
        if (_document is null || _selectedWidget is null || dir == 0) return;
        var all = _document.Layer.Widgets;
        if (all.Count < 2) return;
        // Stable order so ties don't shuffle unpredictably (ZIndex, then current
        // list position).
        var ordered = all
            .Select((w, i) => (w, i))
            .OrderBy(t => t.w.ZIndex).ThenBy(t => t.i)
            .Select(t => t.w)
            .ToList();
        int idx = ordered.IndexOf(_selectedWidget);
        if (idx < 0) return;
        int swapIdx = idx + (dir > 0 ? 1 : -1);
        if (swapIdx < 0 || swapIdx >= ordered.Count) return;
        LayerWidget other = ordered[swapIdx];
        _document.PushUndo();
        int tmp = _selectedWidget.ZIndex;
        _selectedWidget.ZIndex = other.ZIndex;
        other.ZIndex = tmp;
        // Break a tie so the swap is actually visible in paint order.
        if (_selectedWidget.ZIndex == other.ZIndex)
            _selectedWidget.ZIndex = Math.Clamp(_selectedWidget.ZIndex + (dir > 0 ? 1 : -1), -1_000_000, 1_000_000);
        _document.MarkDirty();
        OnPropertyChanged(nameof(SelectedLayer));
    }

    /// <summary>
    /// Bring a set of widgets to the front as a group, preserving their
    /// relative order. Used by the layer-canvas To-Front command when more than
    /// one widget is multi-selected.
    /// </summary>
    public void BringWidgetsToFront(IReadOnlyCollection<LayerWidget> widgets)
        => RestackGroup(widgets, toFront: true);

    /// <summary>Send a set of widgets to the back as a group.</summary>
    public void SendWidgetsToBack(IReadOnlyCollection<LayerWidget> widgets)
        => RestackGroup(widgets, toFront: false);

    private void RestackGroup(IReadOnlyCollection<LayerWidget> widgets, bool toFront)
    {
        if (_document is null || widgets is null || widgets.Count == 0) return;
        var all = _document.Layer.Widgets;
        if (all.Count == 0) return;
        var moving = widgets.Where(all.Contains).OrderBy(w => w.ZIndex).ToList();
        if (moving.Count == 0) return;
        _document.PushUndo();
        if (toFront)
        {
            int z = all.Max(w => w.ZIndex) + 1;
            foreach (var w in moving) w.ZIndex = Math.Min(z++, 1_000_000);
        }
        else
        {
            int z = all.Min(w => w.ZIndex) - moving.Count;
            foreach (var w in moving) w.ZIndex = Math.Max(z++, -1_000_000);
        }
        _document.MarkDirty();
        OnPropertyChanged(nameof(SelectedLayer));
    }

    // ─── plumbing ────────────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        // Whole-invoke guard (NOT per-handler GetInvocationList) — this fires on
        // every property change (40+ sites), so we must not allocate per raise.
        // A throwing binding subscriber would otherwise unwind into and kill the
        // UI dispatcher.
        try { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
        catch (Exception ex) { GlobalLogger.Error("VisualistViewModel", "PropertyChanged", ex); }
    }

    // The Document setter already
    // unsubscribes the OLD document's OnChanged before binding the new one, but
    // the LAST-held document and the layer-presence source were never detached on
    // teardown. Dispose drops both so a pillar-tab close (MainView.Unloaded)
    // doesn't leak the VM through LayerDocument.OnChanged /
    // ILayerRegistrySource.LiveLayerChanged.
    public void Dispose()
    {
        // Drop the user-config subscription so a pillar-tab close doesn't
        // leak the VM through VisualistUserConfig.OnChanged.
        try { Phoenix.Controls.Visualist.WinUI.Core.VisualistUserConfig.Instance.OnChanged -= _onUserConfigChanged; }
        catch (Exception ex) { GlobalLogger.Error("VisualistViewModel", "Dispose config unsubscribe", ex); }

        if (_document is { } doc)
        {
            try { doc.OnChanged -= OnDocumentChanged; }
            catch (Exception ex)
            {
                GlobalLogger.Error("VisualistViewModel", "Dispose document detach", ex);
            }
        }
        DetachLayerSource();
        // Dispose every cached LayerDocument so their auto-save
        // timers don't leak when the pillar tab closes. Previously only the live
        // document was detached; the LRU document cache leaked its timers.
        // Flush dirty cached docs to disk before disposing so a pillar-tab
        // close doesn't silently drop edits on layers the user switched away from.
        // The active _document's close prompt is handled by MainView; dispose it
        // plainly here (don't double-flush it).
        foreach (var cached in _layerCache.Values)
        {
            if (ReferenceEquals(cached, _document))
            {
                try { cached.Dispose(); }
                catch (Exception ex) { GlobalLogger.Error("VisualistViewModel", "Dispose cache", ex); }
            }
            else
            {
                FlushAndDispose(cached, "pillar-close");
            }
        }
        _layerCache.Clear();
        _layerCacheLru.Clear();
    }
}

// ─── Typed per-node inspector contract ───────────────────────────────────
//
// MVVM CONTRACT (must stay byte-identical with the InspectorPanel XAML
// bindings — cross-file binding-name drift is the #1 WinUI bug source).

/// <summary>The control kind the Inspector renders for a single node parameter.</summary>
public enum NodeParamKind { Scalar, Bool, String, Color, Enum, Vector2, Vector3, Vector4, MediaPath }

/// <summary>
/// One editable parameter of <see cref="VisualistViewModel.SelectedNode"/>,
/// surfaced as a typed control in the Inspector NODE section. Two-way value
/// properties commit through the owning VM's single attribute-persist
/// chokepoint (<see cref="VisualistViewModel.CommitNodeAttribute"/>) — one
/// undo entry per edit gesture; the keyframe cluster routes through the VM's
/// <see cref="VisualistViewModel.ToggleParamKeyframeAtPlayhead"/> and its
/// siblings. No parallel persistence.
/// </summary>
public sealed class NodeParamVm : INotifyPropertyChanged
{
    private readonly VisualistViewModel _vm;
    private readonly Node _node;

    public NodeParamVm(VisualistViewModel vm, Node node, string key, NodeParamKind kind)
    {
        _vm   = vm;
        _node = node;
        Key   = key;
        Kind  = kind;
        Label = Humanize(key);
    }

    // ── identity / metadata ──────────────────────────────────────────────
    public string Key   { get; }
    public string Label { get; }
    public NodeParamKind Kind { get; }

    /// <summary>Range bounds for Scalar / vector components ("&lt;Key&gt;__Range" = "min..max"); default 0..1.</summary>
    public double Min { get; set; }
    public double Max { get; set; } = 1;
    public bool   HasRange { get; set; }

    /// <summary>True for an Int-typed scalar socket — the Inspector snaps the slider to
    /// whole steps and rounds every committed value.</summary>
    public bool   IsInteger { get; set; }

    /// <summary>Allowed values for Enum ("&lt;Key&gt;__KnownValues" CSV); empty otherwise.</summary>
    public string[] Options { get; set; } = Array.Empty<string>();

    // ── reuse plumbing (not bound) ───────────────────────────────────────
    /// <summary>Case-correct input-socket name backing this param (used for pin-based animate); = Key when socket-less.</summary>
    public string SocketName { get; set; } = string.Empty;
    public SocketDataType DataType { get; set; } = SocketDataType.Any;
    /// <summary>Set for a collapsed Vector*.Constant — the per-component attribute keys (X/Y[/Z[/W]]).</summary>
    public string[]? VectorComponentKeys { get; set; }
    /// <summary>Precomputed keyframe component-path set (seeded by BuildNodeParams).
    /// The param list is rebuilt together with its node, so the cache can't
    /// outlive the socket/key shape it was derived from.</summary>
    internal HashSet<string>? KeyframePaths { get; set; }

    // ── live values (two-way) ────────────────────────────────────────────
    private string  _textValue   = string.Empty;
    private double  _numberValue;
    private bool    _boolValue;
    private string  _colorHex     = string.Empty;
    private double[] _vectorValues = Array.Empty<double>();
    private bool    _isAnimatable;
    private bool    _isAnimated;

    /// <summary>For String / Enum / MediaPath.</summary>
    public string TextValue
    {
        get => _textValue;
        set { if (SetField(ref _textValue, value ?? string.Empty)) CommitText(); }
    }

    /// <summary>For Scalar — bound to the slider + numeric box.</summary>
    public double NumberValue
    {
        get => _numberValue;
        set { if (SetField(ref _numberValue, value)) CommitScalar(); }
    }

    /// <summary>For Bool.</summary>
    public bool BoolValue
    {
        get => _boolValue;
        set { if (SetField(ref _boolValue, value)) CommitBool(); }
    }

    /// <summary>For Color — #rrggbb or #rrggbbaa.</summary>
    public string ColorHex
    {
        get => _colorHex;
        set { if (SetField(ref _colorHex, value ?? string.Empty)) CommitColor(); }
    }

    /// <summary>For Vector2/3/4 — length 2/3/4. Mutate a copy + reassign to trigger commit.</summary>
    public double[] VectorValues
    {
        get => _vectorValues;
        set { if (SetField(ref _vectorValues, value ?? Array.Empty<double>())) CommitVector(); }
    }

    // V14 removed SetVectorComponent(index, value). Its doc claimed the
    // per-component NumberBox/Slider used it; they do not — InspectorPanel's
    // vector rows mutate a COPY of VectorValues and reassign the property (the
    // documented "mutate a copy + reassign to trigger commit" contract above),
    // which is the only path that fires SetField and therefore the only one that
    // notifies. The in-place variant notified VectorValues by hand and would have
    // silently skipped SetField's change detection.

    public bool IsAnimatable
    {
        get => _isAnimatable;
        set => SetField(ref _isAnimatable, value);
    }

    public bool IsAnimated
    {
        get => _isAnimated;
        set => SetField(ref _isAnimated, value);
    }

    // ── seed (no commit) ─────────────────────────────────────────────────
    // Called by BuildNodeParams to set the initial value WITHOUT triggering a
    // write-back (which would burn an undo entry on selection).
    internal void InitText(string v)   => _textValue   = v ?? string.Empty;
    internal void InitNumber(double v) => _numberValue = v;
    internal void InitBool(bool v)     => _boolValue   = v;
    internal void InitColor(string v)  => _colorHex    = v ?? string.Empty;
    internal void InitVector(double[] v) => _vectorValues = v ?? Array.Empty<double>();

    // ── commit (mirrors the canvas inline-pill attribute conventions) ────
    // Scalars are stored BARE; Color / MediaPath as JSON-quoted literals; Enum
    // as a bare token; vector components written back to their X/Y/Z/W keys.
    private void CommitScalar()
        => _vm.CommitNodeAttribute(_node, Key, VisualistViewModel.PublicFormatScalar(_numberValue));

    private void CommitBool()
        => _vm.CommitNodeAttribute(_node, Key, _boolValue ? "true" : "false");

    private void CommitText()
    {
        // String / Enum / MediaPath are ALL stored as JSON-quoted string literals
        // in the attribute bag (NodeTemplates: ["Mode"]="\"alpha\"",
        // ["Name"]="\"\"", media Path="\"images/foo.png\""). The runtime parser
        // treats a quoted attribute as a string expression — committing the bare
        // text would change its type. MediaPath additionally normalises slashes.
        string raw = _textValue ?? string.Empty;
        if (Kind == NodeParamKind.MediaPath) raw = raw.Replace("\\", "/");
        _vm.CommitNodeAttribute(_node, Key, VisualistViewModel.PublicQuote(raw));
    }

    private void CommitColor()
        => _vm.CommitNodeAttribute(_node, Key, VisualistViewModel.PublicQuote(_colorHex ?? string.Empty));

    private void CommitVector()
    {
        string[]? keys = VectorComponentKeys;
        if (keys is { Length: > 0 } && _vectorValues is not null)
        {
            // Collapsed Vector*.Constant — write each component back to its key.
            int n = Math.Min(keys.Length, _vectorValues.Length);
            for (int i = 0; i < n; i++)
                _vm.CommitNodeAttribute(_node, keys[i], VisualistViewModel.PublicFormatScalar(_vectorValues[i]));
        }
        else if (_vectorValues is not null)
        {
            // Socket-backed vector attribute — comma-joined (matches the
            // "0,0,1,1" Rect convention used by Image.Crop etc.).
            _vm.CommitNodeAttribute(_node, Key,
                string.Join(",", _vectorValues.Select(VisualistViewModel.PublicFormatScalar)));
        }
    }

    private static string Humanize(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        var sb = new StringBuilder(key.Length + 4);
        for (int i = 0; i < key.Length; i++)
        {
            char c = key[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(key[i - 1]))
                sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        try { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
        catch (Exception ex) { GlobalLogger.Error("NodeParamVm", "PropertyChanged", ex); }
    }
}

/// <summary>
/// The ONE author-facing spelling of a <see cref="WidgetPreset"/>. Every Visualist surface
/// that shows a preset name reads it from here.
///
/// <para><b>Why this is centralised rather than "just a switch".</b> The enum members are
/// single PascalCase tokens (<c>WebSource</c>, <c>AlertBox</c>, <c>CC</c>) and the surfaces
/// that formatted them with <c>ToString()</c> printed exactly that, while the surfaces with
/// their own switch printed "Web Source" / "Alert Box" / "Captions". The same preset was
/// spelled three different ways across the Inspector picker, the canvas context menu and the
/// preset gallery — so a streamer looking for the thing they picked in the gallery could not
/// find it in the Inspector's list. A shared helper is the only shape where adding a preset
/// cannot reintroduce the split.</para>
///
/// <para>Per-pillar by design (Visualist chrome does not live in Shared). The three
/// <c>visualist.preset.*</c> keys below ship in all four <c>lang/*.json</c> bundles; the literal
/// second argument is the pre-<c>Localizer.Init</c> / missing-bundle fallback, so this is safe to
/// call before localization has booted. (It said "the existing keys" while none of them existed
/// in any bundle, which meant all three labels silently rendered English for every language.)</para>
/// </summary>
public static class WidgetPresetLabels
{
    /// <summary>The label shown for a preset. Never empty.</summary>
    public static string For(WidgetPreset preset) => preset switch
    {
        // Named because the enum token is not what the author should read. The
        // single-word presets below read correctly as their enum name in English,
        // but they are still CHROME (the gallery tile caption, the Inspector's
        // picker row), so each one carries a key too — otherwise a German author
        // reads a translated gallery with English tile names in it. The literal
        // stays the fallback, so the English rendering is byte-identical.
        WidgetPreset.WebSource => Phoenix.Controls.Shared.Localization.Localizer.T("visualist.preset.websource", "Web Source"),
        WidgetPreset.CC        => Phoenix.Controls.Shared.Localization.Localizer.T("visualist.preset.captions",  "Captions"),
        WidgetPreset.AlertBox  => Phoenix.Controls.Shared.Localization.Localizer.T("visualist.preset.alertbox",  "Alert Box"),
        WidgetPreset.Image     => Phoenix.Controls.Shared.Localization.Localizer.T("visualist.preset.image",     "Image"),
        WidgetPreset.Video     => Phoenix.Controls.Shared.Localization.Localizer.T("visualist.preset.video",     "Video"),
        WidgetPreset.Text      => Phoenix.Controls.Shared.Localization.Localizer.T("visualist.preset.text",      "Text"),
        WidgetPreset.Audio     => Phoenix.Controls.Shared.Localization.Localizer.T("visualist.preset.audio",     "Audio"),
        WidgetPreset.Particles => Phoenix.Controls.Shared.Localization.Localizer.T("visualist.preset.particles", "Particles"),
        WidgetPreset.Chat      => Phoenix.Controls.Shared.Localization.Localizer.T("visualist.preset.chat",      "Chat"),
        WidgetPreset.Player    => Phoenix.Controls.Shared.Localization.Localizer.T("visualist.preset.player",    "Player"),
        _                      => preset.ToString(),
    };

    /// <summary>
    /// The label for a nullable preset — <paramref name="noneLabel"/> when there is none.
    /// <c>WidgetPreset?</c> is what the model actually holds, so the "(none)" / "—" slot is
    /// part of the same contract rather than each caller's own null check.
    /// </summary>
    public static string For(WidgetPreset? preset, string noneLabel)
        => preset is { } p ? For(p) : noneLabel;
}

// V14 removed the RelayCommand ICommand shim that lived here. Its single
// construction site was NodeParamVm.ToggleKeyframeCommand, and that whole chain
// went with the superseded all-or-nothing keyframe toggle. Nothing in
// Visualist.WinUI binds a command: the Inspector is built imperatively (a
// repo-wide search for SetBinding / new Binding / PropertyPath in this project
// returns zero hits), so every affordance wires a CLR event handler that calls
// the VM directly. If a future surface genuinely needs ICommand, take the
// established shared one rather than reviving a Visualist-local copy.
