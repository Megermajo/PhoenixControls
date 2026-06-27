using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Phoenix.Controls.Architect.Core;
using Phoenix.Controls.Architect.WinUI.Canvas;
using Phoenix.Controls.Architect.WinUI.Databank;
using Phoenix.Controls.Architect.WinUI.Databank.Contracts;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.WinUI.Contracts;

namespace Phoenix.Controls.Architect.WinUI.ViewModels;

// Top-level Architect view-model. Lives for the lifetime of the window
// Track 5 builds; aggregates the Logic Canvas, the Databank Browser, and
// both inspectors, and orchestrates the cross-pane wiring (selection on
// canvas → logic inspector; selection on databank → databank inspector;
// tab change → swap which pane is active).
//
// Track 5's MainWindow code-behind is expected to:
//   1. Construct one ArchitectViewModel per window.
//   2. Bind the canvas pane to ArchitectViewModel.LogicCanvas.
//   3. Bind the databank pane to ArchitectViewModel.DatabankBrowser.
//   4. Bind the right inspector to whichever inspector VM the active tab
//      indicates — see ActiveInspector.
//   5. Forward TopTabBar.TabChanged to OnTabChanged.
//   6. Forward File → Open chosen path to Open(path).
//   7. Pass argv to CliBootstrap.HandleCli on launch (see CliBootstrap.cs).
public sealed class ArchitectViewModel : ObservableObject, IPillarShell, IDisposable
{
    public const string TabLogic    = "logic";
    public const string TabDatabank = "databank";

    private string _activeTab = TabLogic;
    private string? _loadedFilePath;
    private ArchitectBusClient? _busClient;
    private bool _liveDebugEnabled;
    private bool _busLiveDebugDropVisible;

    // ── MACRO_SYNC canonical-state cascade (S8 P0) ──────────────────────
    // Canonical Hub-broadcast macro library, id-keyed. Maintained by
    // OnBusMacroSync through MacroOps.ApplyCanonicalGlobalMacros so the open
    // canvas's Macro.Call nodes can be re-synced to the canonical signature
    // when Hub announces a rename / socket add / removal. The backing list is
    // what MacroOps mutates in place; _globalMacroIndex mirrors it id-keyed
    // for the per-id lookups the refresh path needs.
    private readonly List<Macro> _globalMacros = new();
    private readonly HashSet<string> _hubAckedMacroIds = new(StringComparer.Ordinal);

    // Per-MacroId last-applied signature hash. Gates the expensive
    // RefreshMacroCallSockets cascade on a genuine signature change via
    // MacroSyncHashGate so an unchanged macro re-broadcast is a no-op.
    private readonly Dictionary<string, ulong> _lastSyncHashByMacroId =
        new(StringComparer.Ordinal);

    // [P1 swarm-audit 2026-05-29] Single guard for the three macro-sync
    // collections above (_globalMacros / _hubAckedMacroIds /
    // _lastSyncHashByMacroId). They are mutated on the bus receive thread
    // (OnBusMacroSync → MacroOps.ApplyCanonicalGlobalMacros /
    // MacroSyncHashGate.Apply) AND read on the UI thread
    // (RefreshMacroCallSockets(string) → _globalMacros.Find), plus cleared in
    // Dispose — a textbook data race on plain List/HashSet/Dictionary. Every
    // read and write of those three collections (including the calls that mutate
    // them in place) is held under this lock. The lock is never held across an
    // await, and MacroSyncHashGate.Apply's callback only TryEnqueues onto the UI
    // thread (no synchronous re-entry into a lock-holding UI call), so holding it
    // across Apply can't deadlock.
    private readonly object _macroSyncLock = new();

    // Dispatcher that owns LogicCanvas (captured at construction — the AVM is
    // built on the window's UI thread per the Track-5 contract). MACRO_SYNC is
    // already marshalled to a UI SynchronizationContext inside ArchitectBusClient,
    // but the singleton captures only ONE context; in a multi-window session a
    // sibling could have started the bus on a different thread, so re-marshal
    // through the canvas's own queue to be correct regardless. Null in headless
    // / test construction (no dispatcher) — RunOnCanvasThread runs inline then.
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _canvasDispatcher
        = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

    public ArchitectViewModel() : this(new DbRelationalSource()) { }

    /// <summary>Test-friendly constructor — accepts an explicit relational source.</summary>
    public ArchitectViewModel(IRelationalSource relationalSource)
    {
        if (relationalSource is null) throw new ArgumentNullException(nameof(relationalSource));

        LogicCanvas       = new LogicCanvasViewModel();
        LogicInspector    = new LogicInspectorViewModel();
        DatabankInspector = new DatabankInspectorViewModel();

        // 0.10.0: one shared UndoRedoController per .phxg file lives at AVM
        // level so the main Architect canvas + every open SubGraphWindow
        // (macro / process editors) write into the same stack. Pre-0.10.0
        // each LogicCanvasView owned a per-canvas controller, so a macro
        // window's Ctrl+Z couldn't reach an edit made on the parent and
        // vice versa. Capture/Apply target the WHOLE parent Graph (which
        // serialises Macros / Processes inline) — sub-graph windows
        // re-resolve their macro/process by Id from GraphReplaced below.
        //
        //  (P1-A7) — the same controller is now also handed to
        // DatabankBrowserViewModel below so cell edits enter the same
        // Ctrl+Z timeline as graph edits. Construct History BEFORE the
        // browser so the latter can take a non-null reference.
        History = new UndoRedoController(
            capture: () => LogicCanvas.Graph,
            apply:   g =>
            {
                //  Undo / Redo replay LoadGraph to restore a
                // previously captured snapshot — that fires GraphLoaded
                // (which the AVM listens to to clear IsDirty on a real
                // file open). An undo/redo, however, should NOT mark
                // the canvas clean: the resulting graph generally
                // differs from what's on disk. Bracket the replay with
                // the _suppressGraphLoadedDirtyReset latch so the AVM's
                // OnCanvasGraphLoaded handler treats this as a structural
                // refresh, not a save-state reset.
                _suppressGraphLoadedDirtyReset = true;
                try { LogicCanvas.LoadGraph(g); }
                finally { _suppressGraphLoadedDirtyReset = false; }
                // History.Apply is its own mutation — flip IsDirty true
                // unless we landed back at the saved depth (the controller
                // doesn't surface that today, so be conservative and mark
                // dirty; a future enhancement could compare snapshot
                // identity to the on-disk last-saved snapshot).
                IsDirty = true;
                GraphReplaced?.Invoke(this, EventArgs.Empty);
            });

        //  (P1-A7) — hand the shared History to the databank
        // browser so cell edits enter the same undo stack as graph edits.
        // Must follow History construction; the field declarations are
        // before this method so the assignment lands in declared order.
        DatabankBrowser = new DatabankBrowserViewModel(relationalSource, History);

        LogicCanvas.SelectionChanged     += OnCanvasSelectionChanged;
        LogicCanvas.RequestPublishMacroGlobally = PublishMacroGlobally;
        LogicCanvas.GraphLoaded          += OnCanvasGraphLoaded;
        LogicCanvas.GraphMutatedAny      += OnCanvasGraphMutated;
        DatabankBrowser.SelectionChanged += OnDatabankSelectionChanged;
        DatabankBrowser.SchemaChanged    += OnDatabankSchemaChanged;
    }

    /// <summary>
    /// Shared per-AVM undo/redo stack. One stack per .phxg file — the main
    /// canvas and any open <c>SubGraphWindow</c> write into the same
    /// controller via <see cref="LogicCanvasView.ArchitectVm"/>. Snapshots
    /// serialise the whole parent Graph so sub-graph (macro / process)
    /// mutations are part of the same undo timeline as parent-graph edits.
    /// </summary>
    public UndoRedoController History { get; }

    /// <summary>
    /// Fired after <see cref="UndoRedoController"/> Apply re-loads the
    /// parent Graph. SubGraphWindow subscribes so it can re-resolve its
    /// macro / process by Id from the fresh Graph reference (the previous
    /// macro reference is now an orphan) and rebind its inner canvas.
    /// </summary>
    public event EventHandler? GraphReplaced;

    private bool _disposed;

    /// <summary>
    /// Unhook the child-VM event subscriptions wired in the constructor so
    /// the AVM doesn't pin LogicCanvas / DatabankBrowser via its handler
    /// references after a pillar-tab teardown.
    ///
    /// PERF (perf/architect-blockers, HIGH): pre-cache, ArchitectViewModel
    /// had no Dispose path, so the four `+=` from the constructor stayed
    /// live for the AVM's lifetime + the canvas/browser's lifetime, whichever
    /// was longer. In practice the AVM is constructed once per pillar tab
    /// activation, so re-activating the tab created a new AVM while the
    /// previous one still had handler refs into the same canvas/browser
    /// (it'd been re-keyed but the handlers still pointed at the dead AVM
    /// instance). Idempotent; safe to call from a parent dispose chain.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            LogicCanvas.SelectionChanged     -= OnCanvasSelectionChanged;
            LogicCanvas.GraphLoaded          -= OnCanvasGraphLoaded;
            LogicCanvas.GraphMutatedAny      -= OnCanvasGraphMutated;
            DatabankBrowser.SelectionChanged -= OnDatabankSelectionChanged;
            DatabankBrowser.SchemaChanged    -= OnDatabankSchemaChanged;
            LogicCanvas.RequestPublishMacroGlobally = null;
        }
        catch { /* best-effort teardown */ }

        try
        {
            if (_busClient is not null)
            {
                _busClient.OnNodeExecuted -= OnBusNodeExecuted;
                // S8 P0  — OnMacroSync was subscribed alongside
                // OnNodeExecuted in SetLiveDebugEnabled but NOT torn down here,
                // leaving a stale handler on the singleton ArchitectBusClient's
                // multicast list after the AVM was disposed (pillar-tab teardown).
                // A subsequent MACRO_SYNC then fired OnBusMacroSync on the dead
                // AVM, touching disposed fields. Mirror the disabled-path
                // unsubscribe so both events come off symmetrically.
                _busClient.OnMacroSync -= OnBusMacroSync;
            }
        }
        catch { /* best-effort */ }

        // QC20-04: null the publisher-side delegates. Sibling windows and the
        // embedded MainView subscribe to these via inline lambdas that capture
        // their `this`; if the AVM doesn't release the invocation list, the
        // delegate refs keep those windows rooted even after their Closed
        // handlers null their own references. QC20-01 fixes the lambda
        // unhooking on the subscriber side; this pairs the publisher side so
        // the cycle can't survive on either half.
        try
        {
            NodeFlashRequested    = null;
            MacroRenamed          = null;
            ProcessRenamed        = null;
            PhxExportFailed       = null;
            GraphReplaced         = null;
            GlobalMacroIdsChanged = null;
            // PropertyChanged lives on ObservableObject (field-like event in the
            // base type can only be cleared from inside that type).
            ClearPropertyChangedSubscribers();
        }
        catch { /* best-effort teardown */ }

        // S8 — drop the canonical-macro caches so a long-lived singleton bus
        // client doesn't keep deserialised Macro graphs rooted through the
        // disposed AVM's fields.
        try
        {
            // [P1 swarm-audit 2026-05-29] Clear the macro-sync collections under
            // _macroSyncLock so a late bus message racing disposal can't observe
            // a half-cleared state (same lock OnBusMacroSync / RefreshMacroCallSockets
            // take when reading/writing these three collections).
            lock (_macroSyncLock)
            {
                _globalMacros.Clear();
                _hubAckedMacroIds.Clear();
                _lastSyncHashByMacroId.Clear();
            }
        }
        catch { /* best-effort teardown */ }
    }

    public LogicCanvasViewModel       LogicCanvas       { get; }
    public DatabankBrowserViewModel   DatabankBrowser   { get; }
    public LogicInspectorViewModel    LogicInspector    { get; }
    public DatabankInspectorViewModel DatabankInspector { get; }

    /// <summary>Current top-tab — either <see cref="TabLogic"/> or <see cref="TabDatabank"/>.</summary>
    public string ActiveTab
    {
        get => _activeTab;
        // Setting ActiveTab also notifies the three derived flags and
        // ActiveInspector — they all read from _activeTab. Centralising the
        // cascade here keeps Open / NewGraph / OnTabChanged from each
        // having to re-fire the derived-property notifications by hand.
        private set
        {
            if (!SetField(ref _activeTab, value)) return;
            OnPropertyChanged(nameof(IsLogicActive));
            OnPropertyChanged(nameof(IsDatabankActive));
            OnPropertyChanged(nameof(ActiveInspector));
        }
    }

    /// <summary>True when the Logic Canvas tab is showing.</summary>
    public bool IsLogicActive => _activeTab == TabLogic;

    /// <summary>True when the Databank Browser tab is showing.</summary>
    public bool IsDatabankActive => _activeTab == TabDatabank;

    /// <summary>The active inspector — bind the right-hand inspector pane to this.</summary>
    public ObservableObject ActiveInspector => IsLogicActive ? LogicInspector : DatabankInspector;

    /// <summary>True when the canvas is subscribed to Hub's bus for DEBUG_NODE_EXEC pulses.</summary>
    public bool LiveDebugEnabled
    {
        get => _liveDebugEnabled;
        // SetField already raises PropertyChanged for "LiveDebugEnabled" via
        // CallerMemberName — the explicit OnPropertyChanged inside the if was
        // a duplicate notification.
        private set
        {
            if (SetField(ref _liveDebugEnabled, value))
                RecomputeBusLiveDebugDropVisible();
        }
    }

    /// <summary>
    /// True when live-debug is on AND the bus connection isn't healthy
    /// (disconnected or mid-reconnect). MainView binds an InfoBar to this
    /// so the 8×8 status dot gets promoted into a top-row notice with a
    /// Retry button — the dot alone isn't loud enough when the user is
    /// actively watching for debug pulses that aren't arriving.
    /// </summary>
    public bool BusLiveDebugDropVisible
    {
        get => _busLiveDebugDropVisible;
        private set => SetField(ref _busLiveDebugDropVisible, value);
    }

    /// <summary>
    /// Called from the bus client's connection-state subscription in
    /// MainView. Combines the current LiveDebugEnabled state with the
    /// reported <paramref name="state"/> to decide whether the InfoBar
    /// should be visible. Idempotent — repeated calls with the same
    /// inputs are no-ops.
    /// </summary>
    public void UpdateBusConnectionState(BusConnectionState state)
    {
        _lastBusState = state;
        RecomputeBusLiveDebugDropVisible();
    }

    private BusConnectionState _lastBusState = BusConnectionState.Disconnected;

    private void RecomputeBusLiveDebugDropVisible()
    {
        // Show the InfoBar only when the user has actively enabled
        // live-debug (so the chrome is loud only when the user cares about
        // the missing signal) AND the bus is NOT Connected. Reconnecting
        // counts as a drop — it's a transient state but the streamer is
        // still seeing no pulses.
        BusLiveDebugDropVisible = _liveDebugEnabled
            && _lastBusState != BusConnectionState.Connected;
    }

    /// <summary>
    /// Imperative retry hook for the InfoBar's [Retry] button. Stops + restarts
    /// the bus client when live-debug is still enabled; logs and bails when not.
    /// </summary>
    public void RetryBusConnection()
    {
        if (!_liveDebugEnabled)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                "Retry requested but live-debug is off — ignoring.",
                "Architect", LogLevel.System);
            return;
        }
        try
        {
            _busClient ??= ArchitectBusClient.Instance;
            _busClient.Stop();
            _busClient.Start();
            Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                "Bus retry requested.",
                "Architect", LogLevel.Communication);
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error("Architect",
                "Bus retry failed", ex);
        }
    }

    /// <summary>
    /// Toggle the live-debug-trace subscription. When on, the bus client connects
    /// to Hub on ws://127.0.0.1:18081/ as "Architect" and forwards DEBUG_NODE_EXEC
    /// payloads to <see cref="NodeFlashRequested"/> so the canvas can pulse the
    /// matching node id. Off-state silently disconnects.
    /// </summary>
    public void SetLiveDebugEnabled(bool enabled)
    {
        if (enabled == _liveDebugEnabled) return;
        if (enabled)
        {
            _busClient ??= ArchitectBusClient.Instance;
            _busClient.OnNodeExecuted -= OnBusNodeExecuted;
            _busClient.OnNodeExecuted += OnBusNodeExecuted;
            // B10 (audit 2026-05-24) — also subscribe to MACRO_SYNC so the
            // rail's [G] global-macro prefix lights up when Hub broadcasts
            // its canonical library. Pre-fix the AVM never subscribed, so
            // the LeftRail's _globalMacroIds set stayed empty regardless
            // of how many macros Hub announced.
            _busClient.OnMacroSync -= OnBusMacroSync;
            _busClient.OnMacroSync += OnBusMacroSync;
            _busClient.Start();
        }
        else if (_busClient is not null)
        {
            _busClient.OnNodeExecuted -= OnBusNodeExecuted;
            _busClient.OnMacroSync -= OnBusMacroSync;
            // [bus-persist 2026-06-10] Do NOT Stop() the connection here. The
            // bus link is started at Hub launch and runs for the whole session
            // (Majo: "the BUS MUST run", "fix both"). The live-debug toggle now
            // gates only the node-flash / macro-sync SUBSCRIPTIONS above — it no
            // longer tears down the underlying WebSocket. Stopping it would drop
            // the persistent link the moment a user turned live-debug off, which
            // is the same defect class as the Visualist tab-blur teardown.
        }
        LiveDebugEnabled = enabled;
    }

    /// <summary>
    /// B10 — raised after a Hub MACRO_SYNC broadcast has been deserialised.
    /// Payload is the set of macro ids that Hub considers globally available.
    /// MainView / ArchitectSiblingWindow subscribe and forward to
    /// <c>LeftRail.SetGlobalMacroIds</c> so the rail's `[G]` prefix lights up.
    /// </summary>
    public event System.Action<IReadOnlyCollection<string>>? GlobalMacroIdsChanged;

    private void OnBusMacroSync(string payload)
    {
        // S8 P0  — payload is a JSON array of full Macro objects
        // (see PublishMacroGlobally below). Pre-fix this handler only scraped the
        // Id field to light up the rail's [G] prefix; the open canvas's
        // Macro.Call nodes never re-synced to a renamed / re-socketed macro until
        // a manual reload. Now we (1) deserialise full Macro objects, (2) merge
        // them into the canonical _globalMacros via MacroOps (propagating remote
        // deletions + evicting stale hash entries), (3) gate the expensive
        // socket cascade on a signature change through MacroSyncHashGate, and
        // (4) still fire GlobalMacroIdsChanged so the rail prefix keeps working.
        //
        // Parse defensively — Hub's serialisation might evolve, and a malformed
        // payload mustn't crash the AVM. An empty / whitespace payload means
        // "Hub has no canonical macros" → apply an empty canonical set so any
        // previously-acked macros get treated as remotely deleted.
        if (_disposed) return;

        List<Macro> incoming;
        if (string.IsNullOrWhiteSpace(payload))
        {
            incoming = new List<Macro>();
        }
        else
        {
            try
            {
                incoming = System.Text.Json.JsonSerializer.Deserialize<List<Macro>>(payload)
                           ?? new List<Macro>();
            }
            catch (System.Text.Json.JsonException ex)
            {
                Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                    "Architect",
                    "MACRO_SYNC payload parse failed; canvas Macro.Call nodes will not refresh",
                    ex);
                return;
            }
        }

        // Drop entries with no usable id — they can't be id-keyed or refreshed.
        incoming.RemoveAll(m => m is null || string.IsNullOrEmpty(m.MacroId));

        try
        {
            // [P1 swarm-audit 2026-05-29] Hold _macroSyncLock across the whole
            // canonical-merge cascade: every mutation of _globalMacros /
            // _hubAckedMacroIds / _lastSyncHashByMacroId (ApplyCanonicalGlobalMacros,
            // the removed-id sweep, MacroSyncHashGate.Apply) runs under the same
            // lock that RefreshMacroCallSockets's read takes, so the bus-receive
            // thread and the UI thread never touch these collections concurrently.
            // ScrubDeletedMacroCallNodes / OnRefreshMacroSignature only TryEnqueue
            // onto the canvas thread (non-blocking off-thread), and the [G]-prefix
            // id list is snapshotted under the lock then raised AFTER releasing it,
            // so no foreign subscriber code runs while the lock is held.
            List<string> globalIdSnapshot;
            lock (_macroSyncLock)
            {
                // (2) Merge canonical state. ApplyCanonicalGlobalMacros mutates
                // _globalMacros in place (add/update, remote-deletion sweep) and
                // returns the ids it removed so we can evict their stale hash
                // entries — otherwise a re-add of the same MacroId would skip the
                // cascade against a stale hash hit.
                var removed = MacroOps.ApplyCanonicalGlobalMacros(
                    _globalMacros, _hubAckedMacroIds, incoming);
                foreach (var goneId in removed)
                {
                    _lastSyncHashByMacroId.Remove(goneId);
                    // A remotely-deleted macro leaves its open-document Macro.Call
                    // nodes pointing at a now-orphan MacroId. Scrub them to the
                    // bare Flow shape so dangling parameter sockets / wires don't
                    // linger (mirrors MacroOps.DeleteMacro's call-node contract).
                    ScrubDeletedMacroCallNodes(goneId);
                }

                // Snapshot the canonical id set under the lock for the [G]-prefix
                // raise below (the event is invoked after the lock is released).
                globalIdSnapshot = _globalMacros.Select(m => m.MacroId).ToList();

                // (3) Gate the per-macro socket cascade on a signature change.
                // Apply runs OnRefreshMacroSignature only for macros whose Entry/Exit
                // signature hash differs from the last applied value (or is new).
                MacroSyncHashGate.Apply(
                    _globalMacros, _lastSyncHashByMacroId, OnRefreshMacroSignature);
            }

            // (4) Light up the rail's [G] prefix from the canonical id set.
            // Raised outside the lock so subscriber code never runs under it.
            GlobalMacroIdsChanged?.Invoke(globalIdSnapshot);
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "Architect", "MACRO_SYNC canonical-merge cascade failed", ex);
        }
    }

    /// <summary>
    /// S8 P0 — gated refresh callback fired by <see cref="MacroSyncHashGate"/>
    /// once per macro whose canonical signature changed. Re-syncs every open
    /// Macro.Call node referencing this macro to the canonical Entry/Exit
    /// signature (socket count / names / types) and the canonical display name.
    /// Marshalled onto the canvas's UI thread because MACRO_SYNC arrives on the
    /// bus client's receive loop, not the dispatcher that owns LogicCanvas.
    /// </summary>
    private void OnRefreshMacroSignature(Macro macro)
    {
        if (macro is null || string.IsNullOrEmpty(macro.MacroId)) return;
        RunOnCanvasThread(() => RefreshMacroCallSockets(macro));
    }

    /// <summary>
    /// Run <paramref name="action"/> on the dispatcher that owns
    /// <see cref="LogicCanvas"/>. When already on that thread (or no dispatcher
    /// was captured — headless / tests), runs inline. Faults are logged, never
    /// thrown, so a stale-window refresh can't tear down the bus receive loop.
    /// </summary>
    private void RunOnCanvasThread(Action action)
    {
        if (action is null) return;
        var dq = _canvasDispatcher;
        if (dq is null || dq.HasThreadAccess)
        {
            try { action(); }
            catch (Exception ex)
            {
                GlobalLogger.Error("ArchitectViewModel", "RunOnCanvasThread (inline)", ex);
            }
            return;
        }
        dq.TryEnqueue(() =>
        {
            try { action(); }
            catch (Exception ex)
            {
                GlobalLogger.Error("ArchitectViewModel", "RunOnCanvasThread (posted)", ex);
            }
        });
    }

    // ── Macro.Call / Process.Spawn call-site socket sync (S8) ───────────
    //
    // Self-contained WinUI port of the baseline WinForms Canvas.Macros.cs
    // RefreshMacroCallSockets / RefreshProcessSpawnSockets pair. Lives here
    // (not on LogicCanvasViewModel) because S8 owns ArchitectViewModel + the
    // SubGraphWindow that calls into it; LogicCanvasViewModel is owned by other
    // sprints, so per the collision rule the helper is implemented self-
    // contained in an owned file. Both the SubGraphWindow.OnClosed path (the
    // just-edited inner graph) and the MACRO_SYNC cascade (canonical payload)
    // funnel through these so the parent canvas's call-site nodes always match
    // the macro/process Entry/Exit signature.
    //
    // Preserve-by-name semantics: a socket whose (name, direction) survives the
    // new signature keeps its Id and every external wire wired to it; only
    // sockets that genuinely disappear from the signature — and their links —
    // are dropped. Color / DataType are refreshed from the definition so a type
    // change on an Entry/Exit socket propagates to the call site. After the
    // model mutation the matching NodeViewModel's socket VMs are rebuilt and the
    // canvas is told to recompute wires + connectivity.

    private const int CallSiteHeaderH = 24;
    private const int CallSiteRowSpacing = 22;

    /// <summary>
    /// Re-sync every open <c>Macro.Call</c> node referencing
    /// <paramref name="macroId"/> to the macro's current Entry/Exit signature.
    /// Resolves the canonical macro from the open graph first, falling back to
    /// the Hub-canonical global library. No-op when neither has it.
    /// </summary>
    public void RefreshMacroCallSockets(string macroId)
    {
        if (string.IsNullOrEmpty(macroId)) return;
        // [P1 swarm-audit 2026-05-29] Read _globalMacros under _macroSyncLock —
        // the bus-receive thread mutates this list in OnBusMacroSync. The graph-
        // local Macros list is UI-thread-owned, so only the _globalMacros
        // fallback needs the guard; snapshot the resolved macro under the lock
        // then release before the (UI-thread) socket-sync work.
        Macro? macro = LogicCanvas.Graph.Macros.Find(m => m.MacroId == macroId);
        if (macro is null)
        {
            lock (_macroSyncLock)
            {
                macro = _globalMacros.Find(m => m.MacroId == macroId);
            }
        }
        if (macro is null) return;
        RefreshMacroCallSockets(macro);
    }

    /// <summary>
    /// Re-sync every open <c>Macro.Call</c> node referencing
    /// <paramref name="macro"/>'s id to its current signature. Walks the open
    /// graph's nodes (idempotent + diff-based) and rebuilds the affected node
    /// VMs so the canvas reflects the new socket shape immediately.
    /// </summary>
    public void RefreshMacroCallSockets(Macro macro)
    {
        if (macro is null || string.IsNullOrEmpty(macro.MacroId)) return;

        var graph = LogicCanvas.Graph;
        bool any = false;
        foreach (var node in graph.Nodes)
        {
            if (!string.Equals(node.Title, "Macro.Call", StringComparison.Ordinal)) continue;
            if (node.Attributes is null) continue;
            if (!node.Attributes.TryGetValue("MacroId", out var id)
                || !string.Equals(id, macro.MacroId, StringComparison.Ordinal)) continue;

            SyncCallSiteSockets(
                graph, node, macro.Graph,
                entryTitle: "Macro.Entry", exitTitle: "Macro.Exit",
                nameAttrKey: "MacroName", nameValue: macro.Name,
                fixedOutputNames: System.Array.Empty<string>());
            any = true;
        }

        if (any) FinishCallSiteRefresh();
    }

    /// <summary>
    /// Re-sync every open <c>Process.Spawn</c> node referencing
    /// <paramref name="processId"/> to the process's current Entry/Exit
    /// signature. Resolves the canonical process from the open graph. No-op
    /// when absent. Process.Spawn keeps its fixed Done + InstanceId outputs.
    /// </summary>
    public void RefreshProcessSpawnSockets(string processId)
    {
        if (string.IsNullOrEmpty(processId)) return;
        var process = LogicCanvas.Graph.Processes.Find(p => p.ProcessId == processId);
        if (process is null) return;
        RefreshProcessSpawnSockets(process);
    }

    /// <summary>
    /// Re-sync every open <c>Process.Spawn</c> node referencing
    /// <paramref name="process"/>'s id to its current signature.
    /// </summary>
    public void RefreshProcessSpawnSockets(Process process)
    {
        if (process is null || string.IsNullOrEmpty(process.ProcessId)) return;

        var graph = LogicCanvas.Graph;
        bool any = false;
        foreach (var node in graph.Nodes)
        {
            if (!string.Equals(node.Title, "Process.Spawn", StringComparison.Ordinal)) continue;
            if (node.Attributes is null) continue;
            if (!node.Attributes.TryGetValue("ProcessId", out var id)
                || !string.Equals(id, process.ProcessId, StringComparison.Ordinal)) continue;

            SyncCallSiteSockets(
                graph, node, process.Graph,
                entryTitle: "Process.Entry", exitTitle: "Process.Exit",
                nameAttrKey: "ProcessName", nameValue: process.Name,
                fixedOutputNames: new[] { "Done", "InstanceId" });
            any = true;
        }

        if (any) FinishCallSiteRefresh();
    }

    /// <summary>
    /// Core preserve-by-name socket sync shared by macro + process call sites.
    /// Reads the sub-graph's Entry output sockets (→ call-site inputs) and Exit
    /// input sockets (→ call-site outputs), then mutates <paramref name="callNode"/>'s
    /// socket list in place: drop sockets (and their links) no longer in the
    /// signature, refresh / append the rest. <paramref name="fixedOutputNames"/>
    /// (e.g. Process.Spawn's Done + InstanceId) are never dropped and lead the
    /// output column.
    /// </summary>
    private static void SyncCallSiteSockets(
        Graph parentGraph,
        Node callNode,
        Graph subGraph,
        string entryTitle,
        string exitTitle,
        string nameAttrKey,
        string nameValue,
        string[] fixedOutputNames)
    {
        callNode.Attributes ??= new Dictionary<string, string>();
        callNode.Attributes[nameAttrKey] = nameValue ?? string.Empty;

        var entryNode = subGraph?.Nodes?.FirstOrDefault(n => n.Title == entryTitle);
        var exitNode  = subGraph?.Nodes?.FirstOrDefault(n => n.Title == exitTitle);

        var entrySockets = entryNode?.Sockets
            .Where(s => s.Type == SocketType.Output && !s.IsPlaceholder && s.Name != "Flow")
            .ToList() ?? new List<Socket>();
        var exitSockets = exitNode?.Sockets
            .Where(s => s.Type == SocketType.Input && !s.IsPlaceholder && s.Name != "Flow")
            .ToList() ?? new List<Socket>();

        var fixedOut = new HashSet<string>(fixedOutputNames, StringComparer.Ordinal);
        var desiredInputNames  = new HashSet<string>(entrySockets.Select(s => s.Name), StringComparer.Ordinal);
        var desiredOutputNames = new HashSet<string>(exitSockets.Select(s => s.Name), StringComparer.Ordinal);

        // Step 1 — drop only the data sockets whose names are no longer in the
        // signature (Flow + fixed outputs always survive), and the links
        // touching them. Surviving sockets keep their Id + external wires.
        var droppedSockets = callNode.Sockets.Where(s =>
            s.Name != "Flow" &&
            !fixedOut.Contains(s.Name) &&
            !((s.Type == SocketType.Input  && desiredInputNames.Contains(s.Name))
           || (s.Type == SocketType.Output && desiredOutputNames.Contains(s.Name)))).ToList();
        if (droppedSockets.Count > 0)
        {
            var droppedIds = new HashSet<string>(droppedSockets.Select(s => s.Id), StringComparer.Ordinal);
            parentGraph.Links.RemoveAll(l =>
                droppedIds.Contains(l.FromSocketId) || droppedIds.Contains(l.ToSocketId));
            callNode.Sockets.RemoveAll(s => droppedIds.Contains(s.Id));
        }

        double width = callNode.Size.Width;

        // Step 2 — inputs derived from Entry outputs. Existing (by name+dir)
        // keep their Id; refresh Color / DataType / Offset either way.
        for (int i = 0; i < entrySockets.Count; i++)
        {
            var src = entrySockets[i];
            var existing = callNode.Sockets.FirstOrDefault(s =>
                s.Type == SocketType.Input && s.Name == src.Name);
            var offset = new System.Drawing.Point(-6, CallSiteHeaderH + 6 + (i + 1) * CallSiteRowSpacing);
            if (existing is not null)
            {
                existing.Color    = src.Color;
                existing.DataType = src.DataType;
                existing.Offset   = offset;
            }
            else
            {
                callNode.Sockets.Add(new Socket
                {
                    Name     = src.Name,
                    Type     = SocketType.Input,
                    Color    = src.Color,
                    DataType = src.DataType,
                    Offset   = offset,
                });
            }
        }

        // Step 3 — outputs: fixed names first (refresh offset only), then the
        // exit-derived var-out sockets after them.
        int outputRow = 0;
        foreach (var fixedName in fixedOutputNames)
        {
            var existing = callNode.Sockets.FirstOrDefault(s =>
                s.Type == SocketType.Output && s.Name == fixedName);
            var offset = new System.Drawing.Point(
                (int)(width - 14), CallSiteHeaderH + 6 + outputRow * CallSiteRowSpacing);
            if (existing is not null)
                existing.Offset = offset;
            else
                // Create-if-missing (mirrors the input + var-output passes above/
                // below). A call node deserialized without its template fixed
                // output would otherwise never regain it, leaving the macro/process
                // result pin unwireable. No-op for the normal case (socket present).
                callNode.Sockets.Add(new Socket
                {
                    Name   = fixedName,
                    Type   = SocketType.Output,
                    Offset = offset,
                });
            outputRow++;
        }
        for (int i = 0; i < exitSockets.Count; i++)
        {
            var src = exitSockets[i];
            var existing = callNode.Sockets.FirstOrDefault(s =>
                s.Type == SocketType.Output && s.Name == src.Name);
            var offset = new System.Drawing.Point(
                (int)(width - 14), CallSiteHeaderH + 6 + (outputRow + i) * CallSiteRowSpacing);
            if (existing is not null)
            {
                existing.Color    = src.Color;
                existing.DataType = src.DataType;
                existing.Offset   = offset;
            }
            else
            {
                callNode.Sockets.Add(new Socket
                {
                    Name     = src.Name,
                    Type     = SocketType.Output,
                    Color    = src.Color,
                    DataType = src.DataType,
                    Offset   = offset,
                });
            }
        }

        // Always-present output rows: the macro call site carries one Flow
        // output (not in fixedOutputNames, which only lists named fixed outputs
        // like Process.Spawn's Done + InstanceId), so floor the count at 1.
        // Mirrors the baseline WinForms height math — macro: Max(1+entry, 1+exit);
        // process: Max(1+entry, 2+exit).
        int alwaysPresentOutputRows = Math.Max(1, fixedOut.Count);
        int totalRows = Math.Max(
            1 + entrySockets.Count,
            alwaysPresentOutputRows + exitSockets.Count);
        if (totalRows < 1) totalRows = 1;
        callNode.Size = new System.Drawing.Size(
            (int)width, CallSiteHeaderH + 14 + totalRows * CallSiteRowSpacing);
    }

    /// <summary>
    /// Scrub every open <c>Macro.Call</c> node that still references a
    /// remotely-deleted <paramref name="macroId"/> back to the bare Flow shape,
    /// dropping its parameter sockets + their links and the now-orphan MacroId
    /// attribute. Mirrors <see cref="MacroOps.DeleteMacro"/>'s call-node contract
    /// for the MACRO_SYNC remote-deletion path. Marshalled onto the canvas thread.
    /// </summary>
    private void ScrubDeletedMacroCallNodes(string macroId)
    {
        if (string.IsNullOrEmpty(macroId)) return;
        RunOnCanvasThread(() =>
        {
            var graph = LogicCanvas.Graph;
            bool any = false;
            foreach (var node in graph.Nodes)
            {
                if (!string.Equals(node.Title, "Macro.Call", StringComparison.Ordinal)) continue;
                if (node.Attributes is null) continue;
                if (!node.Attributes.TryGetValue("MacroId", out var id)
                    || !string.Equals(id, macroId, StringComparison.Ordinal)) continue;

                node.Attributes.Remove("MacroId");
                var droppedIds = node.Sockets
                    .Where(s => s.Name != "Flow" && !s.IsPlaceholder)
                    .Select(s => s.Id)
                    .ToHashSet();
                if (droppedIds.Count == 0) { any = true; continue; }
                node.Sockets.RemoveAll(s => droppedIds.Contains(s.Id));
                graph.Links.RemoveAll(l =>
                    droppedIds.Contains(l.FromSocketId) || droppedIds.Contains(l.ToSocketId));
                any = true;
            }
            if (any) FinishCallSiteRefresh();
        });
    }

    /// <summary>
    /// Common tail for the call-site refresh paths: invalidate the graph's
    /// id-cache, re-snapshot the affected node VMs' socket rows, and tell the
    /// canvas to recompute wires + connectivity. Must run on the canvas thread.
    /// </summary>
    private void FinishCallSiteRefresh()
    {
        LogicCanvas.Graph.MarkStructuralChange();
        // Re-snapshot every node VM's socket rows. Targeting only the touched
        // nodes would need a node-id list threaded through; the VM count is
        // small (tens) and RebuildSockets is cheap, so a full pass keeps the
        // helper simple and guarantees no stale row VM survives.
        foreach (var nvm in LogicCanvas.Nodes)
            nvm.RebuildSockets();
        // Recompute wire beziers + IsConnected flags, and flag the graph dirty
        // (a call-site signature change is a real graph mutation the user will
        // want to save). OnGraphMutated raises GraphMutatedAny → IsDirty=true.
        LogicCanvas.OnGraphMutated();
    }

    /// <summary>
    /// Raised when Hub broadcasts a DEBUG_NODE_EXEC for a node id. The canvas
    /// listens and flashes that NodeViewModel briefly.
    /// </summary>
    public event System.Action<string>? NodeFlashRequested;

    /// <summary>
    /// Raised after a macro has been renamed via the inspector / left-rail.
    /// Payload is <c>(macroId, newName)</c>. Sub-graph editor windows already
    /// open against that macro subscribe so their title can re-sync without
    /// having to add INPC to the <see cref="Macro"/> POCO (that change would
    /// ripple through GraphSerializer and ParentGraph machinery).
    /// </summary>
    public event System.Action<string, string>? MacroRenamed;

    /// <summary>
    /// Raised after a process has been renamed via the inspector / left-rail.
    /// Payload is <c>(processId, newName)</c>. Same rationale as
    /// <see cref="MacroRenamed"/> — POCO stays event-free; the open sub-graph
    /// window subscribes for its title sync.
    /// </summary>
    public event System.Action<string, string>? ProcessRenamed;

    /// <summary>
    /// Fired by the left-rail / inspector after a successful macro rename.
    /// Public so the rename code path (LeftRail.RenameMacroAsync) can notify
    /// any open SubGraphWindow without taking a hard dependency on it.
    /// </summary>
    public void RaiseMacroRenamed(string macroId, string newName)
        => MacroRenamed?.Invoke(macroId, newName);

    /// <summary>
    /// Fired by the left-rail / inspector after a successful process rename.
    /// </summary>
    public void RaiseProcessRenamed(string processId, string newName)
        => ProcessRenamed?.Invoke(processId, newName);

    private void OnBusNodeExecuted(string nodeId, string scriptFile)
    {
        // B17 (audit/winui-regressions-2026-05-24) — per-script filter so
        // siblings don't flash node ids from scripts they didn't open.
        //
        // Hub emits DEBUG_NODE_EXEC with `scriptFile` set to the .phx
        // filename WITHOUT extension (see ScriptEngine.OnNodeExecuted →
        // ScriptManager DEBUG_NODE_EXEC marshal). The Architect window's
        // LoadedFilePath points at the .phxg. The two share the same
        // stem; matching on stem keeps this independent of any future
        // ".phx" / ".phxg" tweaks.
        //
        // Empty / null scriptFile: pre-fix every fire flashed, so play it
        // safe and pass through (a broadcast without a target shouldn't
        // get silently dropped). Empty LoadedFilePath (the New / unsaved
        // window): there's nothing to match against, so we also pass
        // through — flashing on the only Architect window the user has
        // open is what they'd expect.
        if (!string.IsNullOrEmpty(scriptFile) && !string.IsNullOrEmpty(_loadedFilePath))
        {
            string loadedStem = System.IO.Path.GetFileNameWithoutExtension(_loadedFilePath);
            if (!string.Equals(loadedStem, scriptFile, StringComparison.OrdinalIgnoreCase))
                return;
        }
        NodeFlashRequested?.Invoke(nodeId);
    }

    /// <summary>
    /// Publish a macro to Hub's canonical global library via MACRO_SYNC. Fires
    /// the bus client up if it isn't running yet (publishing implies the user
    /// wants the cross-file library round-trip — Hub will ack and broadcast
    /// MACRO_SYNC back, which a future iteration will subscribe to).
    /// </summary>
    private void PublishMacroGlobally(Macro macro)
    {
        if (macro is null) return;
        try
        {
            _busClient ??= ArchitectBusClient.Instance;
            _busClient.Start();
            var json = System.Text.Json.JsonSerializer.Serialize(new[] { macro });
            _ = _busClient.SendAsync(new BusMessage
            {
                Type    = "MACRO_SYNC",
                Source  = "Architect",
                Target  = "Hub",
                Payload = json,
            });
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                $"MACRO_SYNC publish failed: {ex.Message}",
                "Architect", LogLevel.Communication);
        }
    }

    /// <summary>Absolute path of the currently loaded .phxg, or null when no file is open.</summary>
    public string? LoadedFilePath
    {
        get => _loadedFilePath;
        private set
        {
            if (SetField(ref _loadedFilePath, value))
                OnPropertyChanged(nameof(WindowTitleSuffix));
        }
    }

    /// <summary>Suffix Track 5's chrome appends to the window title (e.g. "— hello.phxg").</summary>
    public string WindowTitleSuffix =>
        string.IsNullOrEmpty(_loadedFilePath) ? string.Empty : $"— {Path.GetFileName(_loadedFilePath)}";

    private bool _isDirty;
    /// <summary>
    /// True when the in-memory graph has unsaved changes. Flipped to true by
    /// the canvas's mutation hook (LogicCanvasViewModel.GraphMutated) and
    /// cleared by SaveAsync / NewGraph / Open. Drives the chrome's bullet
    /// dirty marker per pre-T15 parity.
    /// </summary>
    public bool IsDirty
    {
        get => _isDirty;
        set => SetField(ref _isDirty, value);
    }

    //  Latch flipped true around UndoRedoController.Apply's
    // LoadGraph call so the resulting GraphLoaded notification does NOT
    // reset IsDirty (undo/redo replays differ from the on-disk snapshot
    // and should keep the dirty marker on).
    private bool _suppressGraphLoadedDirtyReset;

    private void OnCanvasGraphMutated(object? sender, EventArgs e) { IsDirty = true; }
    private void OnCanvasGraphLoaded(object? sender, EventArgs e)
    {
        if (_suppressGraphLoadedDirtyReset) return;
        IsDirty = false;
    }

    // ─── IPillarShell ─────────────────────────────────────────────────────
    public string     Title => "Phoenix Controls — Architect";
    public PillarKind Kind  => PillarKind.Architect;

    /// <summary>
    /// Load a .phxg from disk into the Logic Canvas. Returns true on success;
    /// on failure the existing graph stays loaded (matches the WinForms
    /// behaviour — File → Open shows a MessageBox via GraphSerializer's
    /// OnLoadFailed event, then the user keeps editing whatever was open).
    /// Routes through the canonical GraphSerializer so the WinUI Architect
    /// reads exactly the same wire format the WinForms Architect writes.
    ///
    /// Offloads GraphSerializer.LoadGraph onto the ThreadPool (BlockerF) and
    /// resumes on the captured context to mutate the canvas. Cancels nothing;
    /// load is short enough that adding a CT surface area before there's a UI
    /// to wire it to would be premature.
    ///
    ///  Previously paired with a synchronous Open(phxgPath) shim that
    /// called .GetAwaiter().GetResult() on this task; that shim was eliminated
    /// because subscribers to GraphSerializer.OnLoadFailed that dispatch back
    /// to the UI thread broke the "no SyncContext to wait for" guarantee the
    /// shim relied on, risking a deadlock on the very first failed-load path.
    /// All call sites now await this method (or route through
    /// AsyncErrorBoundary.SafeRunAsync when the surrounding API is sync void).
    /// </summary>
    public async System.Threading.Tasks.Task<bool> OpenAsync(string phxgPath)
    {
        if (string.IsNullOrEmpty(phxgPath)) return false;
        if (!File.Exists(phxgPath)) return false;

        // Hub UI sweep 2026-05-22 — detect a parse failure surfaced through
        // GraphSerializer.OnLoadFailed. GraphSerializer.LoadGraph swallows
        // every parse / IO error internally and returns `new Graph()` (so
        // the catch (Exception) above is unreachable), then fires
        // OnLoadFailed for any subscribed listener. Pre-fix, OpenAsync
        // happily returned true for that empty graph — the canvas
        // rendered blank, ArchitectWindowRegistry.OpenFileAsync read the
        // true and kept the sibling window open, and the user saw
        // "I opened a .phxg, got a blank canvas, no error" — exactly
        // Majo's "Loading .phxg in Architect does not work" report.
        //
        // Wire a scoped one-shot listener around the load call. If
        // OnLoadFailed fires for this exact path, treat the load as
        // failed so the registry closes the empty shell AND we forward
        // the original exception through GlobalLogger for the user.
        Exception? captured = null;
        string normalizedPath = phxgPath;
        void OnLoadFailedScoped(string failedPath, Exception ex)
        {
            if (!string.Equals(failedPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
                return;
            captured = ex;
        }
        GraphSerializer.OnLoadFailed += OnLoadFailedScoped;

        Graph graph;
        try
        {
            // Off-UI-thread: read file, normalise JSON, deserialize, migrate.
            graph = await GraphSerializer.LoadGraphAsync(phxgPath).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // GraphSerializer.LoadGraph normally swallows exceptions, but
            // catch defensively in case a future refactor surfaces one
            // (or Task.Run wrapping itself faults).
            GraphSerializer.OnLoadFailed -= OnLoadFailedScoped;
            GlobalLogger.Error("ArchitectViewModel", $"OpenAsync('{phxgPath}') threw", ex);
            return false;
        }
        finally
        {
            GraphSerializer.OnLoadFailed -= OnLoadFailedScoped;
        }

        if (captured is not null)
        {
            // Parse failure inside GraphSerializer.LoadGraph — refuse the
            // load so the sibling window factory can close the empty
            // shell and the user sees the failure (via the
            // ArchitectSiblingWindow.OnLoadFailed status-bar handler).
            // Pre-fix this path silently returned true and Majo got a
            // blank canvas with no error.
            GlobalLogger.Log(
                $"ArchitectViewModel: refusing to load '{Path.GetFileName(phxgPath)}' — " +
                $"GraphSerializer parse failure ({captured.GetType().Name}: {captured.Message}).",
                "ArchitectViewModel", LogLevel.CriticalError);
            return false;
        }

        //  Wildcard cascade is now invoked once inside
        // GraphSerializer.LoadGraph after MigrateNodes, covering every freshly
        // loaded graph (including nested macros / processes via the recursive
        // migration walk). The pre-fix call here was a duplicate of that
        // post-load cascade and ran on the UI thread after the off-thread
        // load returned — removed to keep one source of truth.

        // 0.10.0 (arch-ux-state #2) — explicit canvas reset before LoadGraph so
        // pan/zoom/selection/inspector all clear before the incoming graph's
        // saved ViewOffset / Zoom paint on top. LoadGraph itself sets pan/zoom
        // from Graph.View* fields; Reset() guarantees a clean slate even when
        // the incoming graph has no saved viewport (e.g. legacy .phxg files).
        LogicCanvas.Reset();
        LogicInspector.SetNode(null);
        // [ARCH-P2-DUP-CASCADE / ARCH-P0-LOADGRAPH-UITHREAD] GraphSerializer.LoadGraph
        // already ran ResolveWildcardCascade off-thread (after MigrateNodes) inside
        // the awaited LoadGraphAsync above, so tell LoadGraph to skip the redundant
        // UI-thread cascade pass — it was the largest avoidable dispatcher cost on
        // opening a large graph.
        LogicCanvas.LoadGraph(graph, wildcardAlreadyResolved: true);
        LogicCanvas.LoadedFilePath = phxgPath;
        LoadedFilePath = phxgPath;
        // Reset the AVM-shared undo stack so the freshly-loaded file
        // starts at undo depth 0. The canvas's own GraphLoaded handler
        // skips Reset when ArchitectVm is set (we own the lifecycle).
        History.Reset();
        // ActiveTab setter cascades the IsLogicActive/IsDatabankActive/
        // ActiveInspector notifications — no manual fire needed here.
        ActiveTab = TabLogic;
        return true;
    }

    /// <summary>
    /// Reset to a fresh empty graph. Behaves like File → New: drops the loaded
    /// file path, clears the canvas, switches to the Logic tab.
    /// </summary>
    public void NewGraph()
    {
        // 0.10.0 — same Reset-then-LoadGraph pattern as Open() so a New
        // wipes pan/zoom/selection/inspector cleanly before the empty
        // graph paints.
        LogicCanvas.Reset();
        LogicInspector.SetNode(null);
        LogicCanvas.LoadGraph(new Graph());
        LogicCanvas.LoadedFilePath = null;
        LoadedFilePath = null;
        // Fresh file → fresh undo timeline (mirrors Open()).
        History.Reset();
        ActiveTab = TabLogic;
    }

    /// <summary>
    /// Save the active graph to <paramref name="phxgPath"/> (or <see cref="LoadedFilePath"/>
    /// when null) and re-export the matching .phx alongside it. The .phx run is
    /// fire-and-forget within the try/catch — a failed export logs to GlobalLogger
    /// but doesn't reject the .phxg save (which already succeeded on disk).
    /// </summary>
    public async Task<bool> SaveAsync(string? phxgPath = null)
    {
        var path = phxgPath ?? LoadedFilePath;
        if (string.IsNullOrEmpty(path)) return false;

        // Persist the canvas's current pan/zoom alongside the graph so reopening
        // restores the same viewport (matches the WinForms behaviour).
        LogicCanvas.Graph.ViewOffsetX = (int)LogicCanvas.PanX;
        LogicCanvas.Graph.ViewOffsetY = (int)LogicCanvas.PanY;
        LogicCanvas.Graph.ViewZoom    = (float)LogicCanvas.Zoom;

        try
        {
            await GraphSerializer.SaveGraphAsync(LogicCanvas.Graph, path);
        }
        catch
        {
            return false;
        }

        // Sister .phx export — only when we know the .phxg path so the .phx
        // lives next to it. Awaited so the exporter walk + disk write run
        // off the UI thread (freeze-sweep follow-up): pre-fix this path
        // ran a sync ScriptExporter.Export + File.WriteAllText on the
        // dispatcher.
        var phxPath = await ExportPhxBesideAsync(path).ConfigureAwait(true);

        LoadedFilePath = path;
        IsDirty = false;

        // Tell Hub to reload the script. Hub's LogicWatcher would pick this up
        // anyway when the .phx changes on disk, but the explicit broadcast
        // closes the round-trip faster (and surfaces a UI signal in
        // System Log when LogicWatcher is gated by debug-mode).
        if (!string.IsNullOrEmpty(phxPath))
        {
            try
            {
                _busClient ??= ArchitectBusClient.Instance;
                _busClient.Start();
                _ = _busClient.SendAsync(new BusMessage
                {
                    Type    = "LOGIC_RELOAD",
                    Source  = "Architect",
                    Target  = "Hub",
                    Payload = Path.GetFileName(phxPath),
                });
            }
            catch { /* fire-and-forget */ }
        }

        return true;
    }

    /// <summary>
    /// Re-run the Script exporter on the current graph and write the
    /// result to the same directory as <paramref name="phxgPath"/> with a `.phx`
    /// extension. Mirrors what the WinForms Architect's Save button does and
    /// matches the LogicWatcher contract (Hub picks up `.phx` siblings of `.phxg`
    /// files automatically — see the pipeline docs on the pipeline).
    ///
    /// 0.11.x freeze sweep follow-up — both the exporter walk
    /// (<see cref="ScriptExporter.Export"/>, hundreds of ms on a complex
    /// graph) and the disk write run on the thread pool. Pre-fix this
    /// method ran ScriptExporter + <c>File.WriteAllText</c> synchronously
    /// on the UI thread from Ctrl+S, MainView's File → Export Click handler
    /// and the sibling-window equivalent — combined with OneDrive / AV
    /// latency this produced the intermittent "Architect freezes on save"
    /// reports. The Async variant is the canonical entry-point; the legacy
    /// sync overload is preserved for call sites that haven't been
    /// converted yet but routes through Task.Run + GetAwaiter().GetResult()
    /// (used only on background threads or shutdown paths, never the UI).
    /// </summary>
    public Task<string?> ExportPhxBesideAsync(string phxgPath)
    {
        if (string.IsNullOrEmpty(phxgPath)) return Task.FromResult<string?>(null);

        var dir  = Path.GetDirectoryName(phxgPath);
        var stem = Path.GetFileNameWithoutExtension(phxgPath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem))
            return Task.FromResult<string?>(null);
        var phxPath = Path.Combine(dir, stem + ".phx");
        // Snapshot the Graph reference here on the UI thread so the
        // background exporter walks the same graph instance the user
        // just saved. A concurrent canvas edit during the export tick
        // would compose with the in-progress walk; mutations on
        // ScriptExporter inputs are not expected from the UI side
        // mid-save, but reading the reference here keeps the contract
        // explicit.
        var graph = LogicCanvas.Graph;

        return Task.Run<string?>(() =>
        {
            try
            {
                var exporter = new ScriptExporter(graph);
                System.IO.File.WriteAllText(phxPath, exporter.Export());
                return phxPath;
            }
            catch (Exception ex)
            {
                Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                    "Architect.Save", $"Sister .phx export failed for {phxgPath}", ex);
                PhxExportFailed?.Invoke(this, ex.Message);
                return null;
            }
        });
    }

    /// <summary>
    /// Legacy sync wrapper for back-compat. Avoid calling from the UI
    /// thread — prefer <see cref="ExportPhxBesideAsync"/>. Preserves the
    /// pre-0.11.x error-event semantics: failure logs through
    /// <see cref="GlobalLogger"/> + raises <see cref="PhxExportFailed"/>
    /// and returns null.
    /// </summary>
    public string? ExportPhxBeside(string phxgPath)
        => ExportPhxBesideAsync(phxgPath).GetAwaiter().GetResult();

    /// <summary>
    /// Raised when <see cref="ExportPhxBeside"/> fails. MainView subscribes
    /// and updates the status bar so the user knows the engine-facing .phx
    /// is stale even though the .phxg saved successfully. Architect UX
    /// review P1-32.
    /// </summary>
    public event EventHandler<string>? PhxExportFailed;

    /// <summary>Track 5's TopTabBar forwards click events here.</summary>
    public void OnTabChanged(string tab)
    {
        if (tab != TabLogic && tab != TabDatabank) return;
        if (tab == _activeTab) return;
        // ActiveTab setter cascades the derived-property notifications.
        ActiveTab = tab;

        // Reload the table list every time the user activates the Databank
        // tab. The previous "first-switch-only" gate (Tables.Count == 0)
        // cached an empty list when the user opened Architect before
        // populating the DB; the next switch never re-queried, so the
        // browser stayed empty even after Hub-side / external writes
        // landed. Routed through AsyncErrorBoundary so a faulted DB query
        // (locked file, schema mismatch) is logged instead of escaping as
        // an unobserved TaskScheduler.UnobservedTaskException — which
        // historically tore the dispatcher down on tab-switch.
        if (tab == TabDatabank)
        {
            _ = Phoenix.Controls.Shared.Core.AsyncErrorBoundary.SafeRunAsync(
                () => DatabankBrowser.ReloadTablesAsync(),
                "ArchitectViewModel", "OnTabChanged.ReloadTables");
        }
    }

    private void OnCanvasSelectionChanged(object? sender, EventArgs e)
    {
        // Multi-node selection (marquee, Shift/Ctrl click) takes priority
        // over the single-target Selection field — pre-fix the inspector
        // showed the most-recently-clicked node as if it were the only
        // selection, hiding the fact the user had a multi-set in flight.
        if (LogicCanvas.SelectedNodes.Count > 1)
        {
            LogicInspector.SetMultiNodes(LogicCanvas.SelectedNodes);
            return;
        }
        switch (LogicCanvas.Selection)
        {
            case NodeViewModel n:
                LogicInspector.SetNode(n.Model);
                break;
            case LinkViewModel l:
                LogicInspector.SetLink(l.Model, LogicCanvas);
                break;
            default:
                LogicInspector.SetNode(null);
                break;
        }
    }

    private void OnDatabankSelectionChanged(object? sender, EventArgs e)
    {
        // QC19-02 — pass UpdateCellAsync as the persistence delegate so
        // inspector field edits commit through the same IRelationalSource
        // path the row grid uses. Pre-fix this overload didn't exist and
        // the inspector silently dropped every user edit.
        DatabankInspector.SetRow(
            DatabankBrowser.SelectedTable?.Name,
            DatabankBrowser.SelectedRow,
            persistCell: (row, column, value) => DatabankBrowser.UpdateCellAsync(row, column, value),
            liveRows: DatabankBrowser.VisibleRows);
    }

    /// <summary>
    /// Forward DatabankBrowser.SchemaChanged into the inspector so the
    /// Schema tab mirrors whichever table the user is exploring. The browser
    /// owns the data fetch (it has the IRelationalSource); the inspector is a
    /// pure renderer.
    /// </summary>
    private void OnDatabankSchemaChanged(object? sender, EventArgs e)
    {
        DatabankInspector.SetSchema(DatabankBrowser.Schema);
    }
}
