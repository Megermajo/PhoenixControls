using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Phoenix.Controls.Architect.Core;
using Phoenix.Controls.Architect.WinUI.Services;
using Phoenix.Controls.Architect.WinUI.ViewModels;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using WinRT.Interop;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

// Detached editor window for a macro or process sub-graph. The window owns
// its own LogicCanvasViewModel bound to the inner Graph; mutations
// happen in-place on the shared Graph reference, so saving the
// parent .phxg automatically picks up macro / process edits via the
// Graph.Macros / Graph.Processes list serialisation.
//
// Lookup table — macro/process id → already-open SubGraphWindow — keeps a
// second double-click on the same Macro.Call from spawning a duplicate.
//
// Sprint G — three lifecycle additions:
//   1. Title rename sync. The window subscribes to ArchitectViewModel's
//      MacroRenamed / ProcessRenamed events so an in-place rename via the
//      left rail flips the open editor's eyebrow + title without any
//      INPC plumbing on the underlying POCO.
//   2. Dirty-warning prompt on close. The window snapshots the macro /
//      process JSON shape at open and tracks GraphMutatedAny while open;
//      on AppWindow.Closing with edits, a 3-button ContentDialog offers
//      Keep / Discard / Cancel. Discard restores the snapshot in place.
//   3. Per-sub-graph window-state persistence. Restore + persist via
//      SubGraphWindowStateStore so each macro/process editor remembers
//      its own position + size across sessions.
public sealed partial class SubGraphWindow : Window
{
    //  ConcurrentDictionary so any future dispatcher-hop callers
    // (e.g. a bus-driven open from a non-UI thread) don't race on the
    // single shared open-window registry.
    private static readonly ConcurrentDictionary<string, SubGraphWindow> s_open = new();

    // ── Identity for this open instance ────────────────────────────────
    private string  _trackingKey  = string.Empty;
    private string? _macroId;
    private string? _processId;
    private bool    _isMacro;

    // ── Display ────────────────────────────────────────────────────────
    private string _stateKey = string.Empty;
    private ArchitectViewModel? _architectVm;

    // ── Edit tracking ──────────────────────────────────────────────────
    // Snapshot of the macro/process JSON shape captured on open. Used by
    // the dirty-prompt's Discard branch to restore the inner Graph (nodes,
    // links, sockets, attributes) in place when the user backs out. We
    // serialise the wrapper object — Macro or Process — so all
    // surface-level fields ride along, not just the inner Graph.
    private string? _openSnapshotJson;
    private bool _hasEdits;
    private bool _suppressEditTracking;
    private bool _confirmedClose;
    //  Base OS-window title without the dirty
    // bullet; RefreshWindowTitleDirty prepends "• " when _hasEdits so an
    // unsaved sub-graph editor is identifiable from the TASKBAR (the in-window
    // DirtyMarker dot only shows inside the window). Mirrors ArchitectSiblingWindow.
    private string _baseWindowTitle = "Architect";
    private bool _promptInFlight;
    private LogicCanvasViewModel? _innerVm;

    // 0.11.x polish — per-window LogicInspector wired to the inner
    // canvas's selection. Replaces the floating-singleton InspectorWindow
    // for sub-graph editors so each open macro/process gets its own
    // inspector card embedded in the InspectorColumn the XAML now hosts.
    private LogicInspectorViewModel? _logicInspectorVm;
    private bool _inspectorExpanded = true;
    private const double InspectorDockedDefaultWidth = 320.0;
    private const double InspectorDockedMinWidth     = 240.0;
    private const double InspectorRolledUpWidth      = 32.0;

    // PERF (perf/architect-blockers, HIGH): cached so OnClosed can unsubscribe
    // the AppWindow.Closing handler. Pre-cache, the subscription was wired in
    // WireWindowStateAndCloseGate but never torn down — opening + closing five
    // sub-graphs left five zombie handler subscriptions pinning the closed
    // windows alive through AppWindow's event list.
    private Microsoft.UI.Windowing.AppWindow? _appWindow;

    //  Optional back-reference to the canvas that spawned this
    // sub-graph editor (typically the LeftRail's variables / processes /
    // macros pop-out launch site on the originating LogicCanvasView).
    // When set, OnClosed re-focuses the canvas before tearing down inner
    // state so keyboard focus doesn't fall through to the desktop after a
    // LeftRail-launched pop-out closes — symmetric mirror of the
    // NodeDocumentationWindow close-regrab hook in
    // LogicCanvasView.Menus.HookCanvasFocusRegrabOnNodeDocsClose. Defaults
    // to null so existing call sites (which don't pass a canvas) compile
    // unchanged; wiring the LeftRail to supply it is out of scope here.
    private LogicCanvasView? _originCanvas;

    /// <summary>
    ///  Caller-supplied back-reference to the canvas that
    /// launched this sub-graph editor. Set immediately after construction
    /// (before <c>Activate</c>) so the Closed handler can hand keyboard
    /// focus back to the originating canvas. Optional — leaving it null
    /// preserves the pre-fix behaviour.
    /// </summary>
    public LogicCanvasView? OriginCanvas
    {
        get => _originCanvas;
        set => _originCanvas = value;
    }

    public SubGraphWindow()
    {
        InitializeComponent();
        Closed += OnClosed;
        // Bind the custom chrome to this window once we have an HWND.
        // ExtendsContentIntoTitleBar + SetTitleBar both need the AppWindow
        // to be resolvable, which only works after the first Activated tick.
        Activated += OnChromeBindOnce;
    }

    private void OnChromeBindOnce(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnChromeBindOnce;
        try
        {
            Chrome.Bind(this);
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"SubGraphWindow: chrome bind failed: {ex.Message}",
                "SubGraphWindow", LogLevel.Debug);
        }
    }

    /// <summary>
    /// Reveal call-site — find the Macro.Call / Process.Spawn nodes in the
    /// parent canvas that reference this sub-graph's id, select the first
    /// match on the parent canvas, frame it, and pulse the flash highlight.
    /// Drives the parent's <see cref="LogicCanvasView"/> through the
    /// <see cref="LogicCanvasViewModel.RequestRevealNode"/> delegate that
    /// the canvas wires on DataContextChanged.
    ///
    /// No-op when the SubGraphWindow was opened without a parent AVM (e.g.
    /// a sub-graph editor spawned during a test scenario) or when no
    /// matching call-site exists.
    /// </summary>
    private void OnRevealCallSiteClicked(object sender, RoutedEventArgs e)
    {
        if (_architectVm is null) return;

        string callSiteId = FindCallSiteNodeId();
        if (string.IsNullOrEmpty(callSiteId))
        {
            GlobalLogger.Log(
                $"SubGraphWindow: no {(_isMacro ? "Macro.Call" : "Process.Spawn")} call-site " +
                $"found in parent graph for {(_isMacro ? _macroId : _processId)}.",
                "SubGraphWindow", LogLevel.System);
            return;
        }

        try
        {
            // B18 (audit/winui-regressions-2026-05-24) — bring the parent
            // Architect window to front before driving the reveal so the
            // user sees the framing + flash. Best-effort: when no sibling
            // window owns this AVM (the embedded-in-Hub MainView path),
            // the reveal still runs and the user gets the canvas-side
            // visual feedback; only the activate step is skipped.
            try
            {
                Phoenix.Controls.Architect.WinUI.Services.ArchitectWindowRegistry
                    .ActivateOwnerOf(_architectVm);
            }
            catch (Exception activateEx)
            {
                GlobalLogger.Log(
                    $"SubGraphWindow: reveal call-site activate-owner best-effort failed: {activateEx.Message}",
                    "SubGraphWindow", LogLevel.Debug);
            }

            _architectVm.LogicCanvas.RequestRevealNode?.Invoke(callSiteId);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("SubGraphWindow",
                $"reveal call-site '{callSiteId}'", ex);
        }
    }

    /// <summary>
    /// Scan the parent graph for the first Macro.Call / Process.Spawn node
    /// referencing this sub-graph's id. Returns the node id, or empty when
    /// no call site exists. Multi-site reveal (frame all callers, not just
    /// the first) is captured as a follow-up; one-by-one cycling needs an
    /// additional state field and isn't required for the 0.10.0 milestone.
    /// </summary>
    private string FindCallSiteNodeId()
    {
        if (_architectVm is null) return string.Empty;
        try
        {
            string targetTitle = _isMacro ? "Macro.Call" : "Process.Spawn";
            string attrKey     = _isMacro ? "MacroId" : "ProcessId";
            string? targetId   = _isMacro ? _macroId  : _processId;
            if (string.IsNullOrEmpty(targetId)) return string.Empty;

            foreach (var n in _architectVm.LogicCanvas.Graph.Nodes)
            {
                if (!string.Equals(n.Title, targetTitle, StringComparison.Ordinal)) continue;
                if (n.Attributes is null) continue;
                if (n.Attributes.TryGetValue(attrKey, out var id)
                    && string.Equals(id, targetId, StringComparison.Ordinal))
                {
                    return n.Id;
                }
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("SubGraphWindow", "FindCallSiteNodeId", ex);
        }
        return string.Empty;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        //  Re-grab focus on the originating canvas BEFORE the
        // rest of the teardown runs. Mirrors LogicCanvasView's
        // RestoreCanvasFocus() pattern (the same call used by the
        // NodeDocumentationWindow close-regrab hook in
        // LogicCanvasView.Menus.HookCanvasFocusRegrabOnNodeDocsClose) so
        // a LeftRail-launched pop-out doesn't leave focus on the dying
        // sub-graph window. Best-effort — RestoreCanvasFocus already
        // swallows Focus() faults; we still wrap in try/catch in case the
        // canvas has been disposed in a rare shutdown ordering.
        try { _originCanvas?.RestoreCanvasFocus(); }
        catch { /* shutdown best-effort */ }

        //  Final geometry write synchronously on
        // the terminal close. OnAppWindowClosing already persisted (async); this
        // sync flush guarantees the write lands even if the host process
        // Environment.Exit(0)s immediately after, killing the queued Task.Run.
        try
        {
            if (!string.IsNullOrEmpty(_stateKey))
                SubGraphWindowStateStore.Persist(this, _stateKey, flushSync: true);
        }
        catch { /* shutdown best-effort */ }

        // Drop the open-window registry entry first so a same-id reopen
        // doesn't refocus a corpse window.
        if (!string.IsNullOrEmpty(_trackingKey)) s_open.TryRemove(_trackingKey, out _);

        // Tear down the inner canvas's VM so any per-VM subscriptions
        // get unhooked (TODO 2026-05-07 round 2 P0 #4). Clearing the
        // DataContext also lets the LogicCanvasView release its
        // template-bound child VMs for GC; without it, opening + closing
        // five sub-graphs in a session leaves five orphan VM trees
        // pinned through the closed Window's element subtree.
        try
        {
            if (_innerVm is not null)
            {
                _innerVm.GraphMutatedAny -= OnInnerGraphMutated;
                _innerVm.SelectionChanged -= OnInnerSelectionChanged;
            }
            if (Canvas?.DataContext is IDisposable disposable) disposable.Dispose();
            if (Canvas is not null) Canvas.DataContext = null;
        }
        catch { /* shutdown best-effort */ }

        // [S8 P1 BOTH-RUNS] Sub-graph editor close → re-sync the parent
        // canvas's call-site nodes to the just-edited signature. While this
        // window was open the user may have added / removed / renamed
        // Entry/Exit sockets on the macro / process; the inner VM mutated the
        // SAME macro.Graph / process.Graph reference that lives on the parent
        // graph, so the canonical signature is already in place — we just have
        // to push it out to the Macro.Call / Process.Spawn nodes so their
        // socket displays, names, and types match (external wires preserved by
        // socket name; sockets that no longer exist are dropped). The baseline
        // WinForms Canvas.Macros.cs wired this as the OpenMacroEditor /
        // OpenProcessEditor onClosed callback (RefreshAllCallNodesForMacro /
        // RefreshAllSpawnNodesForProcess); the WinUI port lives on the AVM
        // (RefreshMacroCallSockets / RefreshProcessSpawnSockets) and is invoked
        // here. Runs BEFORE the event-unsubscribe below so the AVM reference is
        // still live. Best-effort — a refresh fault must not block teardown.
        try
        {
            if (_architectVm is not null)
            {
                if (_isMacro && !string.IsNullOrEmpty(_macroId))
                    _architectVm.RefreshMacroCallSockets(_macroId!);
                else if (!_isMacro && !string.IsNullOrEmpty(_processId))
                    _architectVm.RefreshProcessSpawnSockets(_processId!);
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("SubGraphWindow",
                "OnClosed call-site socket refresh", ex);
        }

        // Unhook rename + undo-rebind event subscriptions so the AVM
        // doesn't keep this closed window alive via its event handler list.
        try
        {
            if (_architectVm is not null)
            {
                _architectVm.MacroRenamed    -= OnMacroRenamed;
                _architectVm.ProcessRenamed  -= OnProcessRenamed;
                _architectVm.GraphReplaced   -= OnArchitectGraphReplaced;
            }
        }
        catch { /* best-effort */ }

        // Drop the AppWindow.Closing subscription so this window's handler
        // doesn't outlive the closed Window via AppWindow's event list.
        try
        {
            if (_appWindow is not null)
            {
                _appWindow.Closing -= OnAppWindowClosing;
                _appWindow = null;
            }
        }
        catch { /* best-effort */ }

        // Unbind the custom chrome so the AppWindow.Changed subscription
        // doesn't keep the closed window alive via the chrome's handler list.
        try { Chrome.Unbind(); } catch { /* best-effort */ }
    }

    /// <summary>
    /// 0.10.0 (arch-ux-state #9) — design-time cycle detection. Walks the
    /// inner graph's Macro.Call nodes and surfaces a warning when any
    /// MacroId equals the current editor's _macroId OR a macro id already
    /// in the open editor's call-stack (every other SubGraphWindow whose
    /// _macroId/_processId is on the path leading here). The result is
    /// logged via GlobalLogger and the dirty marker pulses red — no modal
    /// per <c>feedback_no_modal_dialogs_for_repeatable_rejections.md</c>.
    /// </summary>
    private void DetectAndReportCycles()
    {
        if (_innerVm is null) return;
        try
        {
            // Build the ancestor call-stack: every macro / process editor
            // currently open contributes its id. For a 3-deep "Macro A
            // edits Macro B edits Macro C" chain, opening C carries A+B
            // as ancestors so a Macro.Call(A) inside C trips the detector.
            var ancestorIds = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (var (_, openWin) in s_open)
            {
                if (ReferenceEquals(openWin, this)) continue;
                if (openWin._isMacro && openWin._macroId is not null)
                    ancestorIds.Add(openWin._macroId);
                if (!openWin._isMacro && openWin._processId is not null)
                    ancestorIds.Add(openWin._processId);
            }
            // Always include THIS editor's own id (self-recursion).
            if (_isMacro && _macroId is not null)         ancestorIds.Add(_macroId);
            else if (!_isMacro && _processId is not null) ancestorIds.Add(_processId);

            string subjectLabel = _isMacro ? "macro" : "process";
            string subjectId = (_isMacro ? _macroId : _processId) ?? string.Empty;

            int hitCount = 0;
            foreach (var n in _innerVm.Graph.Nodes)
            {
                // Macro.Call → MacroId attribute. Process.Spawn → ProcessId.
                string? targetId = null;
                if (n.Title == "Macro.Call"
                    && n.Attributes != null
                    && n.Attributes.TryGetValue("MacroId", out var mid)
                    && !string.IsNullOrEmpty(mid))
                {
                    targetId = mid;
                }
                else if (n.Title == "Process.Spawn"
                      && n.Attributes != null
                      && n.Attributes.TryGetValue("ProcessId", out var pid)
                      && !string.IsNullOrEmpty(pid))
                {
                    targetId = pid;
                }
                if (string.IsNullOrEmpty(targetId)) continue;
                if (ancestorIds.Contains(targetId!))
                {
                    hitCount++;
                    string kindLabel = n.Title == "Macro.Call" ? "Macro.Call" : "Process.Spawn";
                    string targetLabel = string.Equals(targetId, subjectId, StringComparison.Ordinal)
                        ? "self (recursive)"
                        : "ancestor-on-call-stack";
                    GlobalLogger.Log(
                        $"Cycle in {subjectLabel} editor: {kindLabel} → {targetLabel} (id {targetId}).",
                        "Architect.SubGraphWindow.Cycle", LogLevel.System);
                }
            }

            ApplyCycleBadge(hitCount);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("SubGraphWindow", "DetectAndReportCycles", ex);
        }
    }

    /// <summary>
    /// Flip the dirty-marker dot red + tooltip when cycle hits exist; revert
    /// to the default yellow / hidden state when zero. The dot is the
    /// existing affordance per task spec; no extra chrome required.
    /// </summary>
    private void ApplyCycleBadge(int hitCount)
    {
        void Apply()
        {
            try
            {
                if (DirtyMarker is null) return;
                if (hitCount > 0)
                {
                    DirtyMarker.Visibility = Visibility.Visible;
                    DirtyMarker.Fill = (Microsoft.UI.Xaml.Media.Brush?)Application.Current.Resources["ErrBrush"]
                                       ?? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xC9, 0x53, 0x3C));
                    Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(DirtyMarker,
                        $"Cycle detected: {hitCount} call site(s) reference this editor or an ancestor.");
                }
                else
                {
                    // Hand back to the dirty-marker state machine (yellow
                    // when _hasEdits, hidden otherwise).
                    DirtyMarker.Fill = (Microsoft.UI.Xaml.Media.Brush?)Application.Current.Resources["StatusYellowBrush"]
                                       ?? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xE5, 0xA2, 0x4E));
                    Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(DirtyMarker, null);
                    ApplyDirtyMarker();
                }
            }
            catch { /* shutdown best-effort */ }
        }
        var dq = DispatcherQueue;
        if (dq is null || dq.HasThreadAccess) Apply();
        else dq.TryEnqueue(Apply);
    }

    /// <summary>
    /// Show (or focus) a macro editor for the given <paramref name="macro"/>.
    /// Idempotent — re-opening the same macro brings the existing window front.
    ///  <paramref name="architectVm"/> is required — passing null
    /// would silently break shared-undo (the canvas would build a per-window
    /// History controller instead of the AVM-shared one) and disable rename
    /// sync / graph-replaced rebind. Every real call site already has the
    /// AVM in hand; tests construct one in front of the call.
    /// </summary>
    public static void OpenMacroEditor(Macro macro, ArchitectViewModel architectVm, LogicCanvasView? originCanvas = null)
    {
        if (macro is null) return;
        if (architectVm is null) throw new ArgumentNullException(nameof(architectVm));
        if (s_open.TryGetValue("macro:" + macro.MacroId, out var existing))
        {
            //  Refresh the origin canvas on a re-open hit too — a second
            // open from a different canvas (multi-window Architect) should refocus
            // that canvas on close, not the older one.
            if (originCanvas is not null) existing.OriginCanvas = originCanvas;
            existing.Activate();
            return;
        }
        var win = new SubGraphWindow();
        win._trackingKey  = "macro:" + macro.MacroId;
        win._macroId      = macro.MacroId;
        win._isMacro      = true;
        win._architectVm  = architectVm;
        //  Carry the origin canvas through so the Closed handler
        // can re-grab focus on the canvas that launched this editor.
        win.OriginCanvas  = originCanvas;
        // Guid-keyed state per 0.10.0 task spec — pre-T15 keys by name drifted
        // when the macro/process got renamed (each new name created a fresh
        // empty state file, losing the user's careful window placement).
        // Guid is stable across renames + filesystem-safe by construction.
        win._stateKey     = "macro-" + macro.MacroId;
        win._baseWindowTitle   = $"Architect — Macro: {macro.Name}";
        win.Title              = win._baseWindowTitle;
        win.EyebrowText.Text   = "MACRO EDITOR";
        win.TitleDisplay.Text  = macro.Name ?? string.Empty;
        win.UpdateBreadcrumbFromParent();

        var vm = new LogicCanvasViewModel();
        //  Wildcard cascade now runs inside GraphSerializer.LoadGraph
        // (recursive walk over nested macros/processes), so by the time the
        // .phxg has been opened every embedded macro.Graph has already been
        // resolved. The pre-fix per-window call here was redundant for parent-
        // loaded graphs and also wouldn't help an in-memory new-macro
        // (its Graph is empty at create time and gains shape via canvas mutation,
        // which has its own cascade call sites). Removed.
        // 0.10.0 — wire ArchitectVm BEFORE DataContext so the canvas's
        // OnDataContextChanged picks up the AVM's shared History controller
        // on its first pass. Without this order the macro window would
        // create a per-canvas controller, defeating the cross-window
        // undo-stack share.
        win.Canvas.ArchitectVm = architectVm;
        vm.LoadGraph(macro.Graph);
        win.Canvas.DataContext = vm;
        win._innerVm = vm;

        win.StartSnapshotAsync(macro);
        win.WireEditTracking(vm);
        win.WireRenameSync();
        win.WireGraphReplacedRebind();
        win.WireWindowStateAndCloseGate();
        win.WireCanvasDropToSibling();
        win.WireDockedInspector(vm);

        s_open[win._trackingKey] = win;
        win.Activate();
    }

    /// <summary>Show (or focus) a process editor for the given <paramref name="process"/>.
    ///  <paramref name="architectVm"/> is required — see
    /// <see cref="OpenMacroEditor"/> for the shared-undo / rename-sync reasoning.</summary>
    public static void OpenProcessEditor(Process process, ArchitectViewModel architectVm, LogicCanvasView? originCanvas = null)
    {
        if (process is null) return;
        if (architectVm is null) throw new ArgumentNullException(nameof(architectVm));
        if (s_open.TryGetValue("process:" + process.ProcessId, out var existing))
        {
            //  See OpenMacroEditor's origin-canvas refresh comment.
            if (originCanvas is not null) existing.OriginCanvas = originCanvas;
            existing.Activate();
            return;
        }
        var win = new SubGraphWindow();
        win._trackingKey  = "process:" + process.ProcessId;
        win._processId    = process.ProcessId;
        win._isMacro      = false;
        win._architectVm  = architectVm;
        //  Carry the origin canvas through so the Closed handler
        // can re-grab focus on the canvas that launched this editor.
        win.OriginCanvas  = originCanvas;
        // Guid-keyed state per 0.10.0 task spec — see OpenMacroEditor comment.
        win._stateKey     = "process-" + process.ProcessId;
        win._baseWindowTitle   = $"Architect — Process: {process.Name}";
        win.Title              = win._baseWindowTitle;
        win.EyebrowText.Text   = "PROCESS EDITOR";
        win.TitleDisplay.Text  = process.Name ?? string.Empty;
        win.UpdateBreadcrumbFromParent();

        var vm = new LogicCanvasViewModel();
        //  See macro-editor comment above — wildcard cascade is now
        // handled inside GraphSerializer.LoadGraph for the entire .phxg tree.
        // 0.10.0 — same ArchitectVm-before-DataContext order as macro editor.
        win.Canvas.ArchitectVm = architectVm;
        vm.LoadGraph(process.Graph);
        win.Canvas.DataContext = vm;
        win._innerVm = vm;

        win.StartSnapshotAsync(process);
        win.WireEditTracking(vm);
        win.WireRenameSync();
        win.WireGraphReplacedRebind();
        win.WireWindowStateAndCloseGate();
        win.WireCanvasDropToSibling();
        win.WireDockedInspector(vm);

        s_open[win._trackingKey] = win;
        win.Activate();
    }

    // ─── 0.11.x polish — per-window docked LogicInspector ───────────────

    /// <summary>
    /// Build a per-window <see cref="LogicInspectorViewModel"/> and wire it
    /// to the inner canvas's selection feed. The inspector is mounted
    /// into the right-edge card declared in SubGraphWindow.xaml and opens
    /// at the persisted-or-default width when
    /// <c>AppConfig.ArchitectInspectorVisible</c> is true.
    /// </summary>
    private void WireDockedInspector(LogicCanvasViewModel innerVm)
    {
        _logicInspectorVm = new LogicInspectorViewModel();
        if (InspectorRegion is not null)
        {
            InspectorRegion.Content = new Controls.LogicInspector
            {
                DataContext = _logicInspectorVm,
            };
        }
        innerVm.SelectionChanged += OnInnerSelectionChanged;
        // Seed the empty-selection state.
        UpdateInspectorFromInnerSelection(innerVm);

        var cfg = Phoenix.Controls.Shared.Services.ConfigManager.Current;
        ApplyInspectorVisibleToColumn(cfg.ArchitectInspectorVisible, animate: false);
    }

    private void OnInnerSelectionChanged(object? sender, EventArgs e)
    {
        if (_innerVm is null || _logicInspectorVm is null) return;
        UpdateInspectorFromInnerSelection(_innerVm);
    }

    private void UpdateInspectorFromInnerSelection(LogicCanvasViewModel vm)
    {
        if (_logicInspectorVm is null) return;
        if (vm.SelectedNodes.Count > 1)
        {
            _logicInspectorVm.SetMultiNodes(vm.SelectedNodes);
            return;
        }
        switch (vm.Selection)
        {
            case NodeViewModel n:
                _logicInspectorVm.SetNode(n.Model);
                break;
            case LinkViewModel l:
                _logicInspectorVm.SetLink(l.Model, vm);
                break;
            default:
                _logicInspectorVm.SetNode(null);
                break;
        }
    }

    // ─── arch-ux: short panel open/close width animation ───────────────────
    // Per-window copy (chrome helpers stay per-window per
    // feedback_visualist_architect_chrome_independence.md). Width-only tween of
    // the inspector column over ~170 ms (ease-out cubic).
    private const double PanelAnimMs = 170.0;
    private Microsoft.UI.Xaml.DispatcherTimer? _inspectorWidthTween;

    private static Microsoft.UI.Xaml.DispatcherTimer? AnimatePanelColumnWidth(
        Microsoft.UI.Xaml.Controls.ColumnDefinition? col,
        double toPx,
        Microsoft.UI.Xaml.DispatcherTimer? cancel,
        System.Action? onComplete = null)
    {
        cancel?.Stop();
        if (col is null) { onComplete?.Invoke(); return null; }
        double from = col.ActualWidth > 0 ? col.ActualWidth : col.Width.Value;
        if (System.Math.Abs(from - toPx) < 0.5)
        {
            col.Width = new GridLength(toPx, GridUnitType.Pixel);
            onComplete?.Invoke();
            return null;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timer = new Microsoft.UI.Xaml.DispatcherTimer
        {
            Interval = System.TimeSpan.FromMilliseconds(15),
        };
        timer.Tick += (_, _) =>
        {
            try
            {
                double t = System.Math.Clamp(sw.Elapsed.TotalMilliseconds / PanelAnimMs, 0.0, 1.0);
                double eased = 1.0 - System.Math.Pow(1.0 - t, 3); // ease-out cubic
                col.Width = new GridLength(from + (toPx - from) * eased, GridUnitType.Pixel);
                if (t >= 1.0)
                {
                    timer.Stop();
                    col.Width = new GridLength(toPx, GridUnitType.Pixel);
                    onComplete?.Invoke();
                }
            }
            catch
            {
                // The column / window was torn down mid-animation — stop the
                // timer so it can't keep firing into a disposed visual tree.
                timer.Stop();
            }
        };
        timer.Start();
        return timer;
    }

    private void ApplyInspectorVisibleToColumn(bool visible, bool animate = true)
    {
        if (InspectorColumn is null) return;
        _inspectorExpanded = visible;
        double target;
        if (visible)
        {
            var cfg = Phoenix.Controls.Shared.Services.ConfigManager.Current;
            double w = cfg.ArchitectInspectorColumnWidth > 0
                ? cfg.ArchitectInspectorColumnWidth
                : InspectorDockedDefaultWidth;
            double maxW = double.IsInfinity(InspectorColumn.MaxWidth) ? 9999.0 : InspectorColumn.MaxWidth;
            target = Math.Clamp(w, InspectorDockedMinWidth, maxW);
        }
        else
        {
            target = InspectorRolledUpWidth;
        }
        UpdateInspectorChevronGlyph(visible);
        if (visible)
        {
            if (InspectorRegion is not null)
                InspectorRegion.Visibility = Visibility.Visible;
            if (animate)
                _inspectorWidthTween = AnimatePanelColumnWidth(InspectorColumn, target, _inspectorWidthTween);
            else
            {
                _inspectorWidthTween?.Stop();
                InspectorColumn.Width = new GridLength(target, GridUnitType.Pixel);
            }
        }
        else
        {
            if (animate)
            {
                _inspectorWidthTween = AnimatePanelColumnWidth(
                    InspectorColumn, target, _inspectorWidthTween,
                    () =>
                    {
                        if (InspectorRegion is not null)
                            InspectorRegion.Visibility = Visibility.Collapsed;
                    });
            }
            else
            {
                _inspectorWidthTween?.Stop();
                InspectorColumn.Width = new GridLength(target, GridUnitType.Pixel);
                if (InspectorRegion is not null)
                    InspectorRegion.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void UpdateInspectorChevronGlyph(bool expanded)
    {
        if (InspectorChevronButton is null) return;
        try
        {
            InspectorChevronButton.Content = expanded ? "" : "";
            ToolTipService.SetToolTip(InspectorChevronButton,
                expanded ? "Roll up Inspector" : "Show Inspector");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                InspectorChevronButton,
                expanded ? "Roll up Inspector" : "Show Inspector");
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("SubGraphWindow", "UpdateInspectorChevronGlyph", ex);
        }
    }

    private void OnInspectorChevronClicked(object sender, RoutedEventArgs e)
    {
        ApplyInspectorVisibleToColumn(!_inspectorExpanded);
        // Sub-graph window deliberately does NOT persist its toggle to
        // AppConfig — the global ArchitectInspectorVisible flag is owned by
        // MainView / ArchitectSiblingWindow. A sub-graph editor's chevron
        // is a per-instance affordance; closing + re-opening the editor
        // restores from the global default.
    }

    // ── Edit tracking ──────────────────────────────────────────────────

    /// <summary>
    /// 0.10.0 (arch-ux-state #13) — when the user drops a .phxg onto a
    /// SubGraphWindow's canvas, open it in a NEW sibling Architect window
    /// instead of swallowing the drop. Per the multi-window paradigm,
    /// drag-drop never clobbers the current canvas. Extra files in the
    /// payload get a Communication log line (the canvas-side first-wins
    /// rule already drops them; the note explains why).
    /// </summary>
    private void WireCanvasDropToSibling()
    {
        if (Canvas is null) return;
        Canvas.FileOpenRequested -= OnCanvasFileOpenRequested;
        Canvas.FileOpenRequested += OnCanvasFileOpenRequested;
    }

    private void OnCanvasFileOpenRequested(object? sender, string path)
    {
        //  OpenFile is async; the canvas drop-handler contract is
        // sync void, so fire-and-forget through AsyncErrorBoundary to keep
        // load faults visible in the log without changing the event shape.
        _ = Phoenix.Controls.Shared.Core.AsyncErrorBoundary.SafeRunAsync(
            async () =>
            {
                await Phoenix.Controls.Architect.WinUI.Services.ArchitectWindowRegistry
                    .OpenFileAsync(path).ConfigureAwait(true);
                GlobalLogger.Log(
                    $"SubGraphWindow drop → sibling Architect window for '{System.IO.Path.GetFileName(path)}'.",
                    "SubGraphWindow", LogLevel.Communication);
            },
            "SubGraphWindow",
            $"FileOpenRequested drop '{path}' → sibling spawn");
    }

    private void WireEditTracking(LogicCanvasViewModel vm)
    {
        vm.GraphMutatedAny -= OnInnerGraphMutated;
        vm.GraphMutatedAny += OnInnerGraphMutated;
        // 0.10.0 (arch-ux-state #9) — run a cycle check on every mutation
        // so the badge reacts to a user dropping a Macro.Call(self) on
        // the canvas in real time. Plus a one-shot on Open below.
        DetectAndReportCycles();
    }

    private void OnInnerGraphMutated(object? sender, EventArgs e)
    {
        if (_suppressEditTracking) return;
        bool wasDirty = _hasEdits;
        _hasEdits = true;
        if (!wasDirty)
        {
            // Reflect first-edit on the chrome's dirty dot. The marker is
            // collapsed by default in XAML; we flip it visible on the UI
            // thread once we've gathered the first mutation.
            var dq = DispatcherQueue;
            if (dq is null || dq.HasThreadAccess) ApplyDirtyMarker();
            else dq.TryEnqueue(ApplyDirtyMarker);
        }
        // 0.10.0 (arch-ux-state #9) — re-run cycle detection after every
        // inner-graph mutation so a freshly-added Macro.Call surfaces
        // immediately. DetectAndReportCycles is best-effort + cheap (O(N)).
        DetectAndReportCycles();
    }

    private void ApplyDirtyMarker()
    {
        try
        {
            if (DirtyMarker is null) return;
            DirtyMarker.Visibility = _hasEdits
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch { /* shutdown best-effort */ }
        //  keep the taskbar title's bullet in sync.
        RefreshWindowTitleDirty();
    }

    //  Apply the OS-window title with a leading
    // "• " when the sub-graph has unsaved edits.
    private void RefreshWindowTitleDirty()
    {
        try { Title = (_hasEdits ? "• " : string.Empty) + _baseWindowTitle; }
        catch { /* shutdown best-effort */ }
    }

    // ── Rename sync ────────────────────────────────────────────────────

    private void WireRenameSync()
    {
        if (_architectVm is null) return;
        _architectVm.MacroRenamed   -= OnMacroRenamed;
        _architectVm.ProcessRenamed -= OnProcessRenamed;
        _architectVm.MacroRenamed   += OnMacroRenamed;
        _architectVm.ProcessRenamed += OnProcessRenamed;
    }

    private void OnMacroRenamed(string macroId, string newName)
    {
        if (!_isMacro) return;
        if (!string.Equals(macroId, _macroId, StringComparison.Ordinal)) return;
        ApplyRenameToTitle("Macro", newName, "macro");
    }

    private void OnProcessRenamed(string processId, string newName)
    {
        if (_isMacro) return;
        if (!string.Equals(processId, _processId, StringComparison.Ordinal)) return;
        ApplyRenameToTitle("Process", newName, "process");
    }

    // ── Cross-window undo rebind ───────────────────────────────────────
    // The AVM's shared UndoRedoController snapshots / restores the whole
    // parent Graph. After Apply, the Macro / Process instances inside
    // Graph.Macros / Graph.Processes are fresh objects — our _innerVm is
    // still bound to the OLD reference and silently de-syncs. Subscribe
    // to ArchitectViewModel.GraphReplaced and re-resolve our subject by
    // Id from the fresh graph, then re-load the inner canvas.

    private void WireGraphReplacedRebind()
    {
        if (_architectVm is null) return;
        _architectVm.GraphReplaced -= OnArchitectGraphReplaced;
        _architectVm.GraphReplaced += OnArchitectGraphReplaced;
    }

    private void OnArchitectGraphReplaced(object? sender, EventArgs e)
    {
        if (_architectVm is null || _innerVm is null) return;
        try
        {
            var freshGraph = _architectVm.LogicCanvas.Graph;
            if (_isMacro && _macroId is not null)
            {
                var freshMacro = freshGraph.Macros.Find(m => m.MacroId == _macroId);
                if (freshMacro is null)
                {
                    // The undo landed on a state where this macro hadn't been
                    // created yet — close the window rather than dangle a
                    // detached editor.
                    GlobalLogger.Log(
                        $"SubGraphWindow: macro {_macroId} no longer exists after undo; closing editor.",
                        "SubGraphWindow", LogLevel.System);
                    DispatcherQueue?.TryEnqueue(Close);
                    return;
                }
                _suppressEditTracking = true;
                try { _innerVm.LoadGraph(freshMacro.Graph); }
                finally { _suppressEditTracking = false; }
            }
            else if (!_isMacro && _processId is not null)
            {
                var freshProc = freshGraph.Processes.Find(p => p.ProcessId == _processId);
                if (freshProc is null)
                {
                    GlobalLogger.Log(
                        $"SubGraphWindow: process {_processId} no longer exists after undo; closing editor.",
                        "SubGraphWindow", LogLevel.System);
                    DispatcherQueue?.TryEnqueue(Close);
                    return;
                }
                _suppressEditTracking = true;
                try { _innerVm.LoadGraph(freshProc.Graph); }
                finally { _suppressEditTracking = false; }
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("SubGraphWindow", "GraphReplaced rebind failed", ex);
        }
    }

    private void ApplyRenameToTitle(string label, string newName, string keyKind)
    {
        // The Title / TextBlock writes must happen on the window's UI
        // thread. AVM events come from inside the rail's await, which is
        // already on the UI thread — but route through DispatcherQueue
        // anyway so a future bus-driven rename call doesn't crash.
        //
        // _stateKey is intentionally NOT touched on rename — it's a Guid-
        // based key (macro/process id) so persisted geometry survives
        // renames. Pre-0.10.0 the key was name-derived, which lost state
        // on each rename. Per task spec the state store keys by id Guid.
        _ = keyKind;
        void Apply()
        {
            try
            {
                _baseWindowTitle   = $"Architect — {label}: {newName}";
                RefreshWindowTitleDirty(); //  keep the bullet on rename
                TitleDisplay.Text  = newName ?? string.Empty;
            }
            catch (Exception ex)
            {
                GlobalLogger.Log(
                    $"SubGraphWindow: title rename failed: {ex.Message}",
                    "SubGraphWindow", LogLevel.Debug);
            }
        }

        var dq = DispatcherQueue;
        if (dq is null || dq.HasThreadAccess) Apply();
        else dq.TryEnqueue(Apply);
    }

    /// <summary>
    /// Refresh the breadcrumb's parent-graph segment from the owning
    /// <see cref="ArchitectViewModel"/>'s loaded file path. Falls back
    /// to "(unsaved)" when the parent graph hasn't been saved yet so
    /// the user knows the context window is anchored on an unsaved
    /// graph and a Save of the parent is what persists their edits.
    /// </summary>
    private void UpdateBreadcrumbFromParent()
    {
        try
        {
            string? parentPath = _architectVm?.LoadedFilePath;
            BreadcrumbParent.Text = string.IsNullOrEmpty(parentPath)
                ? "(unsaved)"
                : System.IO.Path.GetFileName(parentPath);
        }
        catch
        {
            // Best-effort — leave the breadcrumb blank rather than
            // letting a path-format quirk crash window construction.
            BreadcrumbParent.Text = string.Empty;
        }
    }

    // ── Window state + close gate ──────────────────────────────────────

    private void WireWindowStateAndCloseGate()
    {
        // Restore last position/size for this sub-graph identity. Restore
        // happens before Activate already runs (caller activates after we
        // return), so the window paints in its remembered footprint on
        // first frame. Best-effort — falls back to centred default-size.
        try
        {
            SubGraphWindowStateStore.Restore(this, _stateKey);
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"SubGraphWindow: state restore failed: {ex.Message}",
                "SubGraphWindow", LogLevel.Debug);
        }

        // Close gate: AppWindow.Closing is the only event in WinUI 3 that
        // exposes Cancel. Attach to that and run the dirty prompt; honour
        // the close once the user resolves it. Pattern parallels Hub's
        // MainWindow.WireSavePromptOnClose.
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            if (appWindow is not null)
            {
                _appWindow = appWindow;
                appWindow.Closing += OnAppWindowClosing;
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"SubGraphWindow: AppWindow.Closing wiring failed: {ex.Message}",
                "SubGraphWindow", LogLevel.Debug);
        }

        // 0.10.0 (arch-ux-state #12) — same 720×400 hard floor as the sibling
        // window. The sub-graph editor's chrome / breadcrumb / inspector pane
        // all need the same minimum legibility as the main canvas.
        try
        {
            Services.WindowMinSize.Apply(this, 720, 400);
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"SubGraphWindow: MinSize floor wiring failed: {ex.Message}",
                "SubGraphWindow", LogLevel.Debug);
        }
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // Persist the geometry on every close attempt — even cancelled
        // ones — so a user who resized then accidentally hit X gets to
        // keep the resize next session. Cheap enough, no need to gate.
        try { SubGraphWindowStateStore.Persist(this, _stateKey); }
        catch { /* best-effort */ }

        if (_confirmedClose) return;          // second pass — let it close.
        if (!_hasEdits)      return;          // no edits — nothing to confirm.
        if (_promptInFlight) { args.Cancel = true; return; }

        args.Cancel = true;
        _promptInFlight = true;

        try
        {
            var choice = await ShowDirtyPromptAsync();
            switch (choice)
            {
                case DirtyChoice.Keep:
                    _confirmedClose = true;
                    DispatcherQueue?.TryEnqueue(Close);
                    break;
                case DirtyChoice.Discard:
                    RestoreFromSnapshot();
                    _confirmedClose = true;
                    DispatcherQueue?.TryEnqueue(Close);
                    break;
                case DirtyChoice.Cancel:
                default:
                    // Stay open; user picked Cancel.
                    break;
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("SubGraphWindow", "OnAppWindowClosing", ex);
        }
        finally
        {
            _promptInFlight = false;
        }
    }

    private enum DirtyChoice { Keep, Discard, Cancel }

    private async System.Threading.Tasks.Task<DirtyChoice> ShowDirtyPromptAsync()
    {
        if (Content is not FrameworkElement root || root.XamlRoot is null)
        {
            // Without a XamlRoot a ContentDialog can't show. Fail safe by
            // treating it as Keep (user gets the close they asked for; the
            // edits stayed on the parent graph and are still recoverable
            // via the parent dirty marker / Save button).
            return DirtyChoice.Keep;
        }

        var label = _isMacro ? "macro" : "process";
        var dlg = new ContentDialog
        {
            XamlRoot              = root.XamlRoot,
            Title                 = $"Unsaved {label} edits",
            Content               = $"You have edits in this {label} editor. " +
                                    "Keep them on the parent graph (save in main window), " +
                                    "discard them, or cancel and stay here?",
            PrimaryButtonText     = "Keep changes",
            SecondaryButtonText   = "Discard changes",
            CloseButtonText       = "Cancel",
            DefaultButton         = ContentDialogButton.Primary,
        };

        var result = await dlg.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary   => DirtyChoice.Keep,
            ContentDialogResult.Secondary => DirtyChoice.Discard,
            _                             => DirtyChoice.Cancel,
        };
    }

    private void RestoreFromSnapshot()
    {
        if (string.IsNullOrEmpty(_openSnapshotJson) || _architectVm is null) return;

        try
        {
            // Re-deserialise the snapshot into the parent graph's macro /
            // process slot. We do an in-place mutation on the existing
            // Macro / Process reference so any external aliases (Macro.Call
            // sites looking up by id) keep pointing at the live object.
            _suppressEditTracking = true;

            var graph = _architectVm.LogicCanvas.Graph;
            if (_isMacro && _macroId is not null)
            {
                var macro = graph.Macros.Find(m => m.MacroId == _macroId);
                if (macro is null) return;
                var fresh = JsonSerializer.Deserialize<Macro>(_openSnapshotJson);
                if (fresh is null) return;
                CopyMacro(fresh, macro);
            }
            else if (!_isMacro && _processId is not null)
            {
                var proc = graph.Processes.Find(p => p.ProcessId == _processId);
                if (proc is null) return;
                var fresh = JsonSerializer.Deserialize<Process>(_openSnapshotJson);
                if (fresh is null) return;
                CopyProcess(fresh, proc);
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("SubGraphWindow", "RestoreFromSnapshot", ex);
        }
        finally
        {
            _suppressEditTracking = false;
        }
    }

    private static void CopyMacro(Macro src, Macro dst)
    {
        dst.Name        = src.Name;
        dst.Graph       = src.Graph;
        dst.InputNames  = src.InputNames;
        dst.OutputNames = src.OutputNames;
    }

    private static void CopyProcess(Process src, Process dst)
    {
        dst.Name        = src.Name;
        dst.Graph       = src.Graph;
        dst.InputNames  = src.InputNames;
        dst.OutputNames = src.OutputNames;
    }

    private static string? SafeSnapshot(object subject)
    {
        try
        {
            return JsonSerializer.Serialize(subject, subject.GetType());
        }
        catch (Exception ex)
        {
            // Snapshot failure means the Discard branch can't restore —
            // log and proceed; the user just loses Discard, not Keep /
            // Cancel.
            GlobalLogger.Log(
                $"SubGraphWindow: snapshot failed ({ex.Message}); Discard will be unavailable.",
                "SubGraphWindow", LogLevel.Debug);
            return null;
        }
    }

    /// <summary>
    /// Fire-and-forget background snapshot. Kicks SafeSnapshot onto the
    /// ThreadPool so window open doesn't block while JsonSerializer walks
    /// the macro/process graph. The snapshot is only consumed by the
    /// Discard branch of the close gate; in the (very narrow) window
    /// between Activate and snapshot completion, Discard behaves the same
    /// as if the snapshot had failed — same fallback the existing code
    /// already had for serialise faults.
    ///
    /// PERF (perf/architect-blockers, HIGH ride-along, zone-05): pre-cache
    /// SafeSnapshot ran synchronously inside the canvas hot-path before
    /// the window content rendered.
    /// </summary>
    private void StartSnapshotAsync(object subject)
    {
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            string? json = SafeSnapshot(subject);
            // _openSnapshotJson is read on the UI thread (Discard path).
            // Writes to a reference-type field are atomic on .NET; no
            // dispatcher marshal needed for the assignment itself.
            _openSnapshotJson = json;
        });
    }
}
