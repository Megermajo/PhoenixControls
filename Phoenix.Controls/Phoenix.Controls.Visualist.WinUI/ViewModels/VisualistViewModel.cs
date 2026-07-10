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
using System.Windows.Input;
using Microsoft.UI.Dispatching;
using Phoenix.Controls.Shared.Core;
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
                OnPropertyChanged(nameof(WidgetEditorTabLabel));
                OnPropertyChanged(nameof(ActiveTriggerObject));
                OnPropertyChanged(nameof(ActiveTriggerDurationLabel));
            }
        }
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
                OnPropertyChanged(nameof(WidgetEditorTabLabel));
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
    // per-param keyframe toggle route back through this VM's single
    // attribute-persist / animate helpers (CommitNodeAttribute /
    // ToggleNodeParamKeyframe) so there is exactly ONE undo+dirty path —
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
    /// Short description for the Inspector NODE header — the template tooltip
    /// when one is registered, else the node's instance description, else empty.
    /// </summary>
    public string SelectedNodeDescription
    {
        get
        {
            if (_selectedNode is null) return string.Empty;
            string? tip = WidgetNodeRegistry.GetTooltip(_selectedNode.Title);
            if (!string.IsNullOrWhiteSpace(tip)) return tip!;
            return _selectedNode.Description ?? string.Empty;
        }
    }

    /// <summary>
    /// Raised after a NodeParamVm commits an attribute (or toggles a keyframe)
    /// so the host can rebuild the canvas node body + refresh its preview
    /// thumbnail. Carries the mutated node. The persistence (PushUndo / write /
    /// MarkDirty) already happened in <see cref="CommitNodeAttribute"/> /
    /// <see cref="ToggleNodeParamKeyframe"/>; this is the visual-refresh signal
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

    /// <summary>
    /// Toggle keyframe animation on a node parameter at the active trigger's
    /// playhead. Reuses the exact methods the canvas right-click "Animate"
    /// gesture calls: <see cref="AnimatedPinRegistry.SeedAnimation"/> /
    /// <see cref="AnimatedPinRegistry.RemoveAnimation"/> for socket-backed
    /// params, and the attribute-path seed (single keyframe at the playhead,
    /// matching the canvas <c>AnimateParameter</c>) for socket-less params.
    /// PushUndo + MarkDirty wrap the mutation; NotifyActiveTriggerChanged
    /// refreshes the timeline strip.
    /// </summary>
    public void ToggleNodeParamKeyframe(NodeParamVm param)
    {
        if (_document is null || param is null) return;
        Node? node = _selectedNode;
        if (node is null) return;
        WidgetTimeline? timeline = ActiveTriggerObject?.Timeline;
        if (timeline is null) return;

        try
        {
            Socket? socket = FindInputSocket(node, param.SocketName);
            if (socket is not null && AnimatedPinRegistry.IsAnimatablePinType(socket.DataType))
            {
                if (AnimatedPinRegistry.IsPinAnimated(timeline, node, socket))
                {
                    _document.PushUndo();
                    int removed = AnimatedPinRegistry.RemoveAnimation(timeline, node, socket);
                    if (removed > 0) { _document.MarkDirty(); NotifyActiveTriggerChanged(); }
                    param.IsAnimated = false;
                }
                else
                {
                    _document.PushUndo();
                    int added = AnimatedPinRegistry.SeedAnimation(timeline, node, socket, Math.Max(0, PlayheadMs));
                    if (added > 0) { _document.MarkDirty(); NotifyActiveTriggerChanged(); }
                    param.IsAnimated = true;
                }
            }
            else
            {
                // Socket-less (attribute-path) animate — mirrors the canvas
                // AnimateParameter gesture: one keyframe per component seeded at
                // the playhead, with a matching toggle-off that strips the paths.
                // A collapsed Vector*.Constant keyframes per X/Y/Z/W component
                // (matching the AnimatedPinRegistry per-component contract); a
                // plain scalar keyframes its single Key.
                string[] keys = param.VectorComponentKeys is { Length: > 0 }
                    ? param.VectorComponentKeys
                    : new[] { param.Key };

                bool animated = keys.Any(c =>
                {
                    string p = AnimatedPinRegistry.MakeParameterPath(node, c);
                    return timeline.Keyframes.Any(k =>
                        k != null && string.Equals(k.ParameterPath, p, StringComparison.Ordinal));
                });

                _document.PushUndo();
                if (animated)
                {
                    foreach (var c in keys)
                    {
                        string p = AnimatedPinRegistry.MakeParameterPath(node, c);
                        timeline.Keyframes.RemoveAll(k =>
                            k != null && string.Equals(k.ParameterPath, p, StringComparison.Ordinal));
                    }
                    param.IsAnimated = false;
                }
                else
                {
                    double seedMs = Math.Max(0, PlayheadMs);
                    if (timeline.DurationMs < seedMs) timeline.DurationMs = seedMs;
                    if (timeline.DurationMs <= 0) timeline.DurationMs = 1000;
                    foreach (var c in keys)
                    {
                        double initial = AnimatedPinRegistry.ReadComponentLiteral(node, c);
                        timeline.Keyframes.Add(new Keyframe
                        {
                            ParameterPath = AnimatedPinRegistry.MakeParameterPath(node, c),
                            TimeMs        = seedMs,
                            Value         = JsonSerializer.SerializeToElement(initial),
                            Curve         = KeyframeCurve.Linear,
                        });
                    }
                    param.IsAnimated = true;
                }
                _document.MarkDirty();
                NotifyActiveTriggerChanged();
            }
            RaiseNodeParamCommitted(node);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel", $"ToggleNodeParamKeyframe('{param?.Key}')", ex);
        }
    }

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

    /// <summary>True when the parameter has any keyframe on any of its component paths.</summary>
    public bool ParamHasKeyframes(NodeParamVm p)
    {
        if (p is null || _selectedNode is not { } node) return false;
        WidgetTimeline? tl = ActiveTriggerObject?.Timeline;
        if (tl is null) return false;
        var paths = CachedParamPaths(node, p);
        return tl.Keyframes.Any(k => k != null && paths.Contains(k.ParameterPath));
    }

    /// <summary>True when the playhead sits exactly on a keyframe for this parameter.</summary>
    public bool IsParamOnKeyframe(NodeParamVm p)
    {
        if (p is null || _selectedNode is not { } node) return false;
        WidgetTimeline? tl = ActiveTriggerObject?.Timeline;
        if (tl is null) return false;
        var paths = CachedParamPaths(node, p);
        double t = Math.Max(0, PlayheadMs);
        return tl.Keyframes.Any(k => k != null && paths.Contains(k.ParameterPath)
            && Math.Abs(k.TimeMs - t) <= KeyframeTimeTolerance);
    }

    /// <summary>
    /// Single-pass has-keyframes / on-keyframe probe for the Inspector diamond —
    /// one walk over the timeline instead of the two separate
    /// <see cref="ParamHasKeyframes"/> + <see cref="IsParamOnKeyframe"/> scans.
    /// <c>On</c> implies <c>Has</c>.
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
            NodeParamKind kind;
            SocketDataType dataType = socket?.DataType ?? SocketDataType.Any;

            if (options is { Length: > 0 })
            {
                kind = NodeParamKind.Enum;
            }
            else if (socket is not null)
            {
                kind = MapSocketKind(socket.DataType);
            }
            else
            {
                kind = InferKindFromValue(key, rawValue);
            }

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
                        // Sensible default range that still keeps the slider usable
                        // for out-of-band values; the paired numeric box keeps any
                        // value editable regardless.
                        param.Min = 0;
                        param.Max = cur > 1 ? Math.Max(1, cur * 2) : 1;
                    }
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
                        // Default slider band 0..1; widen for out-of-band seeds so
                        // the slider stays usable, the numeric box stays unbounded.
                        param.Min = 0;
                        param.Max = cur > 1 ? Math.Max(1, cur * 2) : 1;
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
        || key.EndsWith("__KnownValues", StringComparison.Ordinal);

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
        if (string.Equals(key, "Path", StringComparison.OrdinalIgnoreCase)) return NodeParamKind.MediaPath;
        string s = Unquote(rawValue).Trim();
        if (LooksLikeHexColor(s)) return NodeParamKind.Color;
        if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return NodeParamKind.Bool;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return NodeParamKind.Scalar;
        return NodeParamKind.String;
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
            return $"TIMELINE · {triggerLabel} · {seconds:0.0}S";
        }
    }

    public string ActiveLayerFileName
    {
        get
        {
            if (_document?.FilePath is { } p) return Path.GetFileName(p);
            if (_selectedLayerItem is { } item) return item.FileName;
            return "(no layer)";
        }
    }

    public bool IsDirty => _document?.IsDirty ?? false;

    /// <summary>
    /// External invalidation hook — when an inspector mutates the in-memory
    /// Layer (rect, preset, etc.) without going through a property on
    /// this VM, call this to rebroadcast SelectedLayer so the canvas re-renders.
    /// </summary>
    public void RaiseSelectedLayerChanged() => OnPropertyChanged(nameof(SelectedLayer));

    public string WidgetEditorTabLabel
        => _selectedWidget is null
            ? $"widget · {_activeTrigger}"
            : $"{_selectedWidget.Name} · {_activeTrigger}";

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

        // No open document (or its file vanished) → default to the first row.
        SelectedLayerItem = Layers.FirstOrDefault();
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
    /// the preset's chain. Returns the widget that was mutated/spawned, or
    /// <c>null</c> on failure.
    /// </para>
    /// </summary>
    public LayerWidget? ApplyPreset(WidgetPreset preset, string? targetWidgetId = null)
    {
        if (_document is null) return null;
        try
        {
            LayerWidget? target = null;
            if (!string.IsNullOrEmpty(targetWidgetId))
            {
                target = _document.Layer.Widgets.FirstOrDefault(w =>
                    string.Equals(w.Id, targetWidgetId, StringComparison.Ordinal));
            }

            // No target → spawn a fresh widget seeded with the preset.
            // Document.AddWidget already calls PushUndo + MarkDirty.
            if (target is null)
            {
                LayerWidget spawned = _document.AddWidget(preset.ToString(), preset);
                OnPropertyChanged(nameof(SelectedLayer));
                SelectedWidget = spawned;
                return spawned;
            }

            // Existing widget → replace preset + onStartup graph in-place.
            _document.PushUndo();
            target.Preset = preset;
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
            return target;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistViewModel",
                $"ApplyPreset({preset}, target='{targetWidgetId}') failed", ex);
            return null;
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
/// undo entry per edit gesture; the keyframe toggle routes through the VM's
/// <see cref="VisualistViewModel.ToggleNodeParamKeyframe"/> (which reuses
/// AnimatedPinRegistry / the canvas Animate path). No parallel persistence.
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
        ToggleKeyframeCommand = new RelayCommand(_ => _vm.ToggleNodeParamKeyframe(this));
    }

    // ── identity / metadata ──────────────────────────────────────────────
    public string Key   { get; }
    public string Label { get; }
    public NodeParamKind Kind { get; }

    /// <summary>Range bounds for Scalar / vector components ("&lt;Key&gt;__Range" = "min..max"); default 0..1.</summary>
    public double Min { get; set; }
    public double Max { get; set; } = 1;
    public bool   HasRange { get; set; }

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

    /// <summary>Set a single vector component (used by the per-component NumberBox/Slider) and commit.</summary>
    public void SetVectorComponent(int index, double value)
    {
        if (_vectorValues is null || index < 0 || index >= _vectorValues.Length) return;
        if (Math.Abs(_vectorValues[index] - value) < double.Epsilon) return;
        _vectorValues[index] = value;
        OnPropertyChanged(nameof(VectorValues));
        CommitVector();
    }

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

    /// <summary>Seed / remove a keyframe for this param at the active trigger's playhead.</summary>
    public ICommand ToggleKeyframeCommand { get; }

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
/// Minimal ICommand for the Inspector keyframe-diamond binding. No CanExecute
/// gating today — the diamond is only shown for animatable params, so the
/// command is always executable when surfaced.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute    = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
