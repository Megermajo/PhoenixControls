using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Architect.Core;
using Phoenix.Controls.Architect.WinUI.Canvas;
using Phoenix.Controls.Architect.WinUI.Dialogs;
using Phoenix.Controls.Architect.WinUI.ViewModels;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Windows.System;

namespace Phoenix.Controls.Architect.WinUI.Controls;

public sealed partial class LeftRail : UserControl
{
    private LogicCanvasViewModel? _vm;

    // Per-section selection (Architect UX review P1-39 — pre-fix toolbar
    // actions silently targeted LastOrDefault() with no visual feedback).
    // Stored by Id so a Refresh() can re-pick the same logical entry even
    // when the ObservableCollection is rebuilt from scratch.
    private string? _selectedVarId;
    private string? _selectedMacroId;
    private string? _selectedProcessId;

    //  [P0 OWNER-OVERRIDE] — active Variables-section scope, driven
    // by RailSection's scope dropdown (0 = Local / 1 = Global / 2 = Graph).
    // Mirrors the combo's SelectedIndex=0 default so the initial rail render
    // matches the visible dropdown selection. The DB capability that the old
    // left-rail "DB: Vars" mode offered was re-architected into the Databank
    // tab (see  [REFRAMED] item), so the three live scopes here are
    // Local / Global / Graph; live DB-debug values still surface as the
    // value tag via the OnVariableSet cache below.
    private int _variableScope;

    //  [P1] — live variable values cached from the Hub's
    // DEBUG_VAR_SET bus messages (ArchitectBusClient.OnVariableSet). Keyed by
    // the canonical variable token (e.g. "score", "public.kills",
    // "global.theme"); surfaced as the right-hand value tag on discovered
    // rows in Local / Global scope so the panel reads as a live scope map
    // rather than a static name list. Bounded implicitly by the variable
    // namespace of the running script.
    private readonly Dictionary<string, string> _liveVarValues =
        new(StringComparer.OrdinalIgnoreCase);
    private Action<string, string, string>? _onVariableSetHandler;

    // Architect VM held weakly so a rename in the rail can fire MacroRenamed /
    // ProcessRenamed without LeftRail having to re-resolve it. Optional: when
    // null (e.g. designer / boot stub) the rename still mutates the model and
    // refreshes the rail; the open SubGraphWindow just won't live-update its
    // title that turn (next save cycle picks it up).
    private ArchitectViewModel? _architectVm;

    //  P1-A2: Hub's canonical global-macro library, surfaced into
    // the rail so macros that exist in the global set get the "GLOBAL" tag +
    // [G] prefix in the section item template (pre-T15 parity — MainForm
    // prefixed [G] on global macros so the user could pull them into the
    // active file). MACRO_SYNC plumbing already loads the library into
    // ArchitectBusClient.OnMacroSync but the WinUI architect doesn't
    // subscribe yet — populating this set is a future wire-up at the AVM
    // layer (MainView/SiblingWindow can call SetGlobalMacroIds when the
    // subscription lands). Default-empty so the rail renders correctly
    // until that wiring is in place.
    private HashSet<string> _globalMacroIds =
        new(StringComparer.OrdinalIgnoreCase);

    //  #10 — rail-collapse + inspector-toggle hooks. The host
    // MainView / ArchitectSiblingWindow subscribes to these events and
    // drives RailColumn.Width / InspectorWindow.OpenFor(...) on receipt;
    // the rail itself only owns the user-input dispatch and the glyph
    // toggling. Persistence of the collapse / inspector flags lives at
    // the host via ConfigManager.

    /// <summary>
    ///  #10 — raised when the user clicks the rail-collapse
    /// chevron. The bool arg is the new desired collapsed state (true =
    /// collapsed). The host responds by resizing RailColumn.Width
    /// (220 ↔ 32) and persisting ArchitectRailCollapsed.
    /// </summary>
    public event EventHandler<bool>? RailCollapseToggled;

    /// <summary>
    ///  #10 — raised when the user clicks the "Inspector"
    /// toggle in the rail header. The bool arg is the new desired
    /// visibility (true = show / focus the floating inspector window).
    /// The host responds by spawning / closing the InspectorWindow
    /// singleton and persisting ArchitectInspectorVisible.
    /// </summary>
    public event EventHandler<bool>? InspectorToggleRequested;

    // Local mirror of the host-driven collapsed flag — kept in sync via
    // <see cref="SetRailCollapsedGlyph"/> so the chevron swaps to the
    // "expand" direction once the host has actually applied the column-
    // width change. The two-step "raise event, host applies, host calls
    // SetRailCollapsedGlyph back" keeps the rail dumb-but-responsive.
    private bool _isCollapsed;
    private bool _inspectorVisible;

    public LeftRail()
    {
        InitializeComponent();
        // Until SetCanvasContext binds a real graph, show a tiny stub set so
        // the rail isn't visually empty during designer / boot.
        VariablesSection.Items = new ObservableCollection<RailItemViewModel>();
        ProcessesSection.Items = new ObservableCollection<RailItemViewModel>();
        MacrosSection.Items    = new ObservableCollection<RailItemViewModel>();
        BuildButtonStrips();

        // Selection plumbing — each section raises ItemSelected on tap;
        // LeftRail stores the picked Id so the toolbar actions can act on
        // the user's choice rather than guessing via LastOrDefault().
        VariablesSection.ItemSelected += (_, item) => _selectedVarId    = item?.Id;
        MacrosSection.ItemSelected    += (_, item) => _selectedMacroId  = item?.Id;
        ProcessesSection.ItemSelected += (_, item) => _selectedProcessId = item?.Id;

        //  P1-A3: DoubleTapped → open editor. Dispatch by Kind so
        // a macro/process row opens its SubGraphWindow editor and a
        // variable row opens the rename dialog (variables don't have a
        // sub-graph editor — rename is the closest parity with pre-T15
        // MainForm's double-click open-editor idiom).
        VariablesSection.ItemDoubleTapped += OnRailItemDoubleTapped;
        MacrosSection.ItemDoubleTapped    += OnRailItemDoubleTapped;
        ProcessesSection.ItemDoubleTapped += OnRailItemDoubleTapped;

        // F2 inline rename — Architect UX P1 "Rail rename (variable / macro /
        // process) → inline TextBox on the rail row (F2 idiom). Keep dialog
        // for 'New …'." Captured at the section level so the user doesn't
        // need keyboard focus on the row itself.
        HookF2(VariablesSection, ResolveSelectedVariableId, RenameVariableAsync);
        HookF2(MacrosSection,    ResolveSelectedMacroId,    RenameMacroAsync);
        HookF2(ProcessesSection, ResolveSelectedProcessId,  RenameProcessAsync);

        //  [P0 OWNER-OVERRIDE] — wire the scope dropdown so a scope
        // change rebuilds the Variables list against the chosen scope. Before
        // this sprint the combo was visible + interactive with no handler, so
        // changing it had zero effect.
        VariablesSection.ScopeChanged += OnVariableScopeChanged;
        // Default the visible selection to Graph (index 2) so the dropdown
        // matches the declared-variable list the +/−/Aa CRUD buttons manage —
        // a freshly-added Graph variable surfaces immediately. Local (0) and
        // Global (1) are read-only discovery views the user can switch to.
        // SetScopeIndex fires SelectionChanged → OnVariableScopeChanged, which
        // sets _variableScope; the explicit assignment keeps it correct even
        // if the combo isn't realized yet (designer / headless).
        _variableScope = 2;
        VariablesSection.SetScopeIndex(2);

        //  [P1] — subscribe to the Hub's live DEBUG_VAR_SET feed so
        // discovered Local / Global rows can show their last runtime value.
        // ArchitectBusClient is a process-wide singleton; the handler caches
        // the value and refreshes the Variables list (only when the live
        // scope is showing discovered vars, to avoid churn on the Graph view).
        _onVariableSetHandler = OnBusVariableSet;
        Phoenix.Controls.Architect.Core.ArchitectBusClient.Instance.OnVariableSet
            += _onVariableSetHandler;

        // Unhook the bus handler when the control leaves the tree so a torn-
        // down rail doesn't keep the singleton's invocation list alive.
        Unloaded += OnLeftRailUnloaded;
    }

    private void OnLeftRailUnloaded(object sender, RoutedEventArgs e)
    {
        if (_onVariableSetHandler is not null)
        {
            Phoenix.Controls.Architect.Core.ArchitectBusClient.Instance.OnVariableSet
                -= _onVariableSetHandler;
            _onVariableSetHandler = null;
        }
    }

    //  [P1] — cache the live value and refresh if we're showing a
    // discovered scope. The bus fires on a background thread, so marshal the
    // refresh onto the rail's dispatcher.
    private void OnBusVariableSet(string varName, string value, string scriptFile)
    {
        if (string.IsNullOrEmpty(varName)) return;
        lock (_liveVarValues) _liveVarValues[varName.Trim()] = value ?? string.Empty;
        if (_variableScope == 2) return; // Graph view doesn't show live values.
        try
        {
            DispatcherQueue?.TryEnqueue(RebuildVariablesForScope);
        }
        catch { /* dispatcher torn down during shutdown — ignore */ }
    }

    private void OnVariableScopeChanged(object? sender, int scopeIndex)
    {
        _variableScope = scopeIndex;
        // A scope switch always forces a rebuild (the signature gate would
        // otherwise skip it because the underlying Graph.Variables didn't
        // change). Reset the cached signature so RebuildVariablesForScope
        // repopulates from the new scope.
        _varsSig = string.Empty;
        RebuildVariablesForScope();
    }

    /// <summary>
    /// Bind the rail's Variables / Processes / Macros sections to the loaded
    /// graph's live data. Re-runs whenever the canvas's graph reference
    /// changes (e.g. File → Open).
    /// </summary>
    public void SetCanvasContext(LogicCanvasViewModel vm)
    {
        _vm = vm;
        if (vm is null) return;
        Refresh();
        vm.PropertyChanged -= OnVmPropertyChanged;
        vm.PropertyChanged += OnVmPropertyChanged;
    }

    /// <summary>
    /// Optional companion to <see cref="SetCanvasContext"/> — supplies the
    /// top-level <see cref="ArchitectViewModel"/> so the rail's macro / process
    /// rename code paths can fire <see cref="ArchitectViewModel.MacroRenamed"/>
    /// / <see cref="ArchitectViewModel.ProcessRenamed"/> events. Open
    /// <see cref="SubGraphWindow"/> instances subscribe to those and live-sync
    /// their window title without needing INPC on the underlying POCO.
    /// </summary>
    public void SetArchitectContext(ArchitectViewModel architectVm)
    {
        _architectVm = architectVm;
    }

    /// <summary>
    ///  P1-A2: flow Hub's canonical global-macro id set into the
    /// rail. Macros in this set are tagged <c>Type="GLOBAL"</c> and render
    /// with a <c>[G]</c> prefix in the section item template (pre-T15
    /// MainForm parity). Set is replaced wholesale on each call — pass
    /// <c>null</c> or an empty enumerable to clear. Forces a rail refresh
    /// so the visual tags re-paint immediately. Safe to call from any
    /// thread that already dispatches to the UI (typical caller is the
    /// AVM's <c>ArchitectBusClient.OnMacroSync</c> handler when wired).
    /// </summary>
    public void SetGlobalMacroIds(IEnumerable<string>? ids)
    {
        _globalMacroIds = ids is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        // Force a Macros-section rebuild — signature includes the global
        // flag (see BuildMacrosSignature) so a global-set change invalidates
        // the cached signature and triggers the visual repaint.
        _macrosSig = string.Empty;
        Refresh();
    }

    //  The originating canvas view — supplied by MainView /
    // ArchitectSiblingWindow at construction so the rail's Open*Editor
    // calls can hand the SubGraphWindow a back-reference. SubGraphWindow's
    // Closed handler then re-focuses this canvas (mirror of the
    // NodeDocumentationWindow close-regrab pattern). Null-safe — the
    // refocus is a best-effort no-op when this isn't set.
    private LogicCanvasView? _canvasView;
    public void SetCanvasView(LogicCanvasView canvasView)
    {
        _canvasView = canvasView;
    }

    // ───  #10 — rail collapse + inspector toggle ────────────────

    /// <summary>
    ///  #10 — push the rail-collapsed state into the rail's chrome
    /// (swaps the chevron glyph between left-pointing E76B and right-pointing
    /// E76C, hides the centre title + inspector button when collapsed so
    /// the 32 px strip reads as chrome rather than truncated chrome).
    /// Idempotent: re-applying the same state is a cheap no-op.
    /// Host calls this:
    ///   - On boot, after reading ConfigManager.Current.ArchitectRailCollapsed
    ///     and applying the matching RailColumn.Width.
    ///   - In response to RailCollapseToggled, after the host has applied
    ///     the new RailColumn.Width.
    /// </summary>
    public void SetRailCollapsedGlyph(bool collapsed)
    {
        _isCollapsed = collapsed;
        try
        {
            // E76B = ChevronRightMed (rail is collapsed, click to expand);
            // E76C = ChevronLeftMed  (rail is expanded, click to collapse).
            if (ChevronToggleButton is not null)
            {
                ChevronToggleButton.Content = collapsed ? "" : "";
                ToolTipService.SetToolTip(ChevronToggleButton,
                    collapsed ? "Expand rail (chevron)" : "Collapse rail (chevron)");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                    ChevronToggleButton,
                    collapsed ? "Expand rail" : "Collapse rail");
            }
            // Title + inspector button hide when collapsed — at 32 px the
            // rail strip can't comfortably host wide chrome; the chevron
            // alone stays clickable so the user can always re-expand.
            // Section bodies hide too; the host narrows RailColumn to 32 px
            // which clips them visually, but explicit Collapsed avoids
            // their hit-testable footprint while collapsed.
            if (RailHeaderTitle is not null)
                RailHeaderTitle.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            // 0.11.5 canvas-polish r3 — InspectorToggleButton was removed
            // from the rail header; the roll-up chevron now lives on the
            // inspector card itself (see MainView.xaml column 4). The
            // collapse-rail visibility pass therefore has nothing to toggle
            // for the inspector toggle anymore.
            if (VariablesSection is not null)
                VariablesSection.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            if (ProcessesSection is not null)
                ProcessesSection.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            if (MacrosSection is not null)
                MacrosSection.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Architect.LeftRail", "SetRailCollapsedGlyph", ex);
        }
    }

    /// <summary>
    ///  #10 — push the inspector-visible state into the rail's
    /// chrome (swaps the trailing chevron in the Inspector button between
    /// E76C ▸ and E76A ▾ so a user glance at the rail header tells them
    /// whether the floating window is currently up). Idempotent.
    /// </summary>
    public void SetInspectorVisibleGlyph(bool visible)
    {
        // 0.11.5 canvas-polish r3 — the rail-header InspectorToggleButton
        // + chevron that this method used to drive were removed when the
        // roll-up affordance moved onto the inspector card itself
        // (MainView.xaml column 4). Method kept on the public surface so
        // MainView's existing call sites stay intact; updating
        // _inspectorVisible keeps OnInspectorToggleClicked's
        // "desired = !_inspectorVisible" math correct if the
        // InspectorToggleRequested event is ever fired programmatically
        // (ArchitectSiblingWindow floating-inspector hook).
        _inspectorVisible = visible;
    }

    private void OnChevronToggleClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // Optimistic local flip — the host's column-width handler is the
        // canonical state, so it will call SetRailCollapsedGlyph back to
        // reconcile. We toggle _isCollapsed eagerly so a double-click in
        // rapid succession doesn't dispatch two same-direction events.
        bool desired = !_isCollapsed;
        RailCollapseToggled?.Invoke(this, desired);
    }

    private void OnInspectorToggleClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        bool desired = !_inspectorVisible;
        InspectorToggleRequested?.Invoke(this, desired);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogicCanvasViewModel.Graph)) Refresh();
    }

    /// <summary>
    ///  P0-A1: one-shot undo push used by the three rail-driven
    /// rename paths (variable / macro / process). The host AVM owns the
    /// shared <see cref="UndoRedoController"/>; calling
    /// <see cref="LogicCanvasView.PushUndoForInlineEdit"/> via the back-
    /// reference routes through the same controller. Either path is fine
    /// because both end at <c>_history.Push()</c>, but going through the
    /// canvas view keeps the rail's behaviour identical to the inline
    /// socket-rename pattern at <c>NodeView.xaml.cs:580-581</c>.
    /// Falls back to the AVM's controller directly when the canvas view
    /// hasn't been wired (headless / test paths). No-op when neither is
    /// available — the headless path that builds a rail without an AVM
    /// has no undo stack to push to anyway.
    /// </summary>
    private void PushRailUndo()
    {
        if (_canvasView is not null)
        {
            _canvasView.PushUndoForInlineEdit();
            return;
        }
        _architectVm?.History?.Push();
    }

    // Last-built signature per section . The Graph model exposes
    // List<T> (not ObservableCollection<T>), so we can't subscribe to
    // CollectionChanged without changing the Shared contract. Instead, hash
    // the cheap identity fields (Name + Type + Id) and bail when nothing has
    // changed — Refresh fires on every Graph PropertyChanged (and on every
    // action handler that pessimistically pushes through), but most fires are
    // either renames-in-place that didn't add/remove rows, or graph swaps
    // that genuinely changed nothing about variables/processes/macros (e.g.
    // a graph load where Refresh runs before AND after MigrateNodes — second
    // run is a no-op once we have a signature). The hash is stable and
    // order-sensitive so a reorder is caught too.
    private string _varsSig = string.Empty;
    private string _procsSig = string.Empty;
    private string _macrosSig = string.Empty;

    public void Refresh()
    {
        if (_vm is null) return;

        // Variables — scope-aware (Local / Global / Graph). The signature
        // gate lives inside RebuildVariablesForScope so each scope can hash
        // its own source set.
        RebuildVariablesForScope();

        // Processes
        var newProcsSig = BuildProcsSignature(_vm.Graph.Processes);
        if (newProcsSig != _procsSig)
        {
            var procs = new ObservableCollection<RailItemViewModel>();
            foreach (var p in _vm.Graph.Processes)
                procs.Add(new RailItemViewModel
                {
                    Name  = p.Name,
                    Type  = "process",
                    // 0.10.0 theme P2: sage green resolved from OkBrush.
                    Color = ResolveHex("OkBrush", "#6FA46B"),
                    Kind  = "process",
                    Id    = p.ProcessId,
                });
            ProcessesSection.Items = procs;
            WireItemContextMenus(ProcessesSection);
            _procsSig = newProcsSig;
        }

        // Macros
        var newMacrosSig = BuildMacrosSignature(_vm.Graph.Macros, _globalMacroIds);
        if (newMacrosSig != _macrosSig)
        {
            var macros = new ObservableCollection<RailItemViewModel>();
            foreach (var m in _vm.Graph.Macros)
            {
                //  P1-A2: tag matching items GLOBAL when Hub's
                // canonical library contains the MacroId. The [G] prefix
                // on Name (rendered by RailSection's item template) keeps
                // the rail glanceable for users who maintain a shared
                // macro library across .phxg files.
                bool isGlobal = _globalMacroIds.Contains(m.MacroId);
                macros.Add(new RailItemViewModel
                {
                    Name  = isGlobal ? $"[G] {m.Name}" : m.Name,
                    Type  = isGlobal ? "GLOBAL" : "local",
                    // 0.10.0 theme P2: brass identity for macros.
                    Color = ResolveHex("EmberPrimaryBrush", "#E5A24E"),
                    Kind  = "macro",
                    Id    = m.MacroId,
                });
            }
            MacrosSection.Items = macros;
            WireItemContextMenus(MacrosSection);
            _macrosSig = newMacrosSig;
        }

        // Items rebind drops the section's SelectedItem (RailSection's
        // ItemsProperty changed-callback handles this); re-evaluate every
        // gated button so they reflect the cleared selection.
        ReevaluateGatedButtons();
    }

    private static string BuildVarsSignature(System.Collections.Generic.IList<VariableDefinition> vars)
    {
        if (vars.Count == 0) return string.Empty;
        var sb = new System.Text.StringBuilder(vars.Count * 24);
        foreach (var v in vars) { sb.Append(v.Name); sb.Append('|'); sb.Append(v.Type); sb.Append(';'); }
        return sb.ToString();
    }

    private static string BuildProcsSignature(System.Collections.Generic.IList<Process> procs)
    {
        if (procs.Count == 0) return string.Empty;
        var sb = new System.Text.StringBuilder(procs.Count * 32);
        foreach (var p in procs) { sb.Append(p.ProcessId); sb.Append('|'); sb.Append(p.Name); sb.Append(';'); }
        return sb.ToString();
    }

    private static string BuildMacrosSignature(
        System.Collections.Generic.IList<Macro> macros,
        HashSet<string> globalIds)
    {
        if (macros.Count == 0) return string.Empty;
        var sb = new System.Text.StringBuilder(macros.Count * 36);
        foreach (var m in macros)
        {
            sb.Append(m.MacroId); sb.Append('|');
            sb.Append(m.Name); sb.Append('|');
            //  P1-A2: flip on a global-state change so the
            // signature gate (Refresh's _macrosSig comparison) doesn't
            // skip the rebuild when only the GLOBAL tag changed.
            sb.Append(globalIds.Contains(m.MacroId) ? 'G' : 'L');
            sb.Append(';');
        }
        return sb.ToString();
    }

    // ──  [P0/P1] scope-aware Variables rebuild ──────────────────

    /// <summary>
    ///  [P0 OWNER-OVERRIDE] — rebuild the Variables section against
    /// the active scope picked in RailSection's dropdown:
    ///   0 = Local  — every local-scope variable *referenced* by the active
    ///                graph (Var.Get/Set/Inc/Toggle VariableName, the bare
    ///                {token} interpolation pills), deduped, with live
    ///                DEBUG_VAR_SET values when known.
    ///   1 = Global — cross-file persistent refs ({global.*} / {user.*}) plus
    ///                run-wide shared refs ({public.*} / Public.Get/Set
    ///                KeyName), deduped, with live values when known.
    ///   2 = Graph  — the explicit Graph.Variables (the pre-S6 behaviour).
    /// Each scope hashes its own source so the signature gate still skips
    /// no-op rebuilds.
    /// </summary>
    private void RebuildVariablesForScope()
    {
        if (_vm is null) return;
        switch (_variableScope)
        {
            case 0:  RefreshLocalVarsList();  break;
            case 1:  RefreshGlobalVarsList(); break;
            default: RefreshGraphVarsList();  break;
        }
    }

    /// <summary>Scope 2 — explicit graph variables (pre-S6 behaviour).</summary>
    private void RefreshGraphVarsList()
    {
        if (_vm is null) return;
        var newVarsSig = "G:" + BuildVarsSignature(_vm.Graph.Variables);
        if (newVarsSig == _varsSig) return;
        var vars = new ObservableCollection<RailItemViewModel>();
        foreach (var v in _vm.Graph.Variables)
            vars.Add(new RailItemViewModel
            {
                Name  = v.Name,
                Type  = v.Type ?? string.Empty,
                Color = ColorForVarType(v.Type),
                Kind  = "variable",
                Id    = v.Name,
            });
        VariablesSection.Items = vars;
        WireItemContextMenus(VariablesSection);
        _varsSig = newVarsSig;
    }

    /// <summary>
    ///  [P1] — scope 0. Walk every node in the active
    /// graph discovering local-scope variable references: Var.Get/Set/Inc/
    /// Toggle's VariableName attribute and every bare {token} interpolation
    /// pill that is NOT namespaced (no leading <c>global.</c> / <c>user.</c>
    /// / <c>public.</c> — those belong to the Global scope). Dedupe and
    /// surface with the live DEBUG_VAR_SET value as the value tag when known.
    /// This is "discovery only" parity with the baseline
    /// MainForm.Variables.RefreshLocalVarsList: the full referenced-var set,
    /// not just the ~handful of explicit Graph.Variables.
    /// </summary>
    private void RefreshLocalVarsList()
    {
        if (_vm is null) return;
        var discovered = DiscoverReferencedVars(globalScope: false);
        var newVarsSig = "L:" + BuildDiscoverySignature(discovered);
        if (newVarsSig == _varsSig) return; // names + live values unchanged
        VariablesSection.Items = BuildDiscoveredItems(discovered);
        WireItemContextMenus(VariablesSection);
        _varsSig = newVarsSig;
    }

    /// <summary>
    ///  — scope 1. Discover cross-file / run-wide shared references:
    /// namespaced {global.*} / {user.*} interpolation pills (DB-persisted
    /// cross-file vars) and {public.*} / Public.Get/Set KeyName (run-wide,
    /// branch-crossing). Deduped, with live values when known.
    /// </summary>
    private void RefreshGlobalVarsList()
    {
        if (_vm is null) return;
        var discovered = DiscoverReferencedVars(globalScope: true);
        var newVarsSig = "X:" + BuildDiscoverySignature(discovered);
        if (newVarsSig == _varsSig) return;
        VariablesSection.Items = BuildDiscoveredItems(discovered);
        WireItemContextMenus(VariablesSection);
        _varsSig = newVarsSig;
    }

    // {token} matcher — same shape ScriptEngine.SubstituteVars / the
    // VarChainAnalyzer regex use at runtime: a single curly-braced run of
    // identifier chars / dots, conservative so literal braces in text don't
    // match.
    private static readonly System.Text.RegularExpressions.Regex s_varTokenRegex =
        new(@"\{([A-Za-z_][A-Za-z0-9_\.]*)\}",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Walk the active graph collecting variable names. When
    /// <paramref name="globalScope"/> is false, returns the local-scope names
    /// (Var.* attrs + non-namespaced pills); when true, returns the
    /// cross-file / shared names (global.* / user.* / public.* pills +
    /// Public.* KeyName). Names are returned in stable first-seen order so
    /// the rail doesn't reshuffle between rebuilds.
    /// </summary>
    private List<string> DiscoverReferencedVars(bool globalScope)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var name = raw.Trim();
            if (name.Length == 0) return;
            bool namespaced =
                name.StartsWith("global.", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("user.",   StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("public.", StringComparison.OrdinalIgnoreCase);
            // Local scope keeps bare names; Global scope keeps namespaced ones.
            if (globalScope != namespaced) return;
            if (seen.Add(name)) ordered.Add(name);
        }

        if (_vm is null) return ordered;

        foreach (var n in _vm.Graph.Nodes)
        {
            if (n.Attributes is { } attrs)
            {
                // Direct binders — Var.* via VariableName, Public.* via KeyName.
                if (n.Title is "Var.Get" or "Var.Set" or "Var.Inc" or "Var.Toggle"
                    && attrs.TryGetValue("VariableName", out var vn))
                    TryAdd(vn);
                if (n.Title is "Public.Get" or "Public.Set"
                    && attrs.TryGetValue("KeyName", out var kn) && !string.IsNullOrWhiteSpace(kn))
                    TryAdd("public." + kn.Trim());

                // Interpolation pills inside any attribute value.
                foreach (var kv in attrs)
                {
                    if (string.IsNullOrEmpty(kv.Value)) continue;
                    foreach (System.Text.RegularExpressions.Match m in s_varTokenRegex.Matches(kv.Value))
                        TryAdd(m.Groups[1].Value);
                }
            }

            // Socket inline/editable values are persisted in Node.Attributes
            // (scanned above), not on the Socket model itself, so the attribute
            // sweep already covers any {token} pills authored on a socket.
        }

        return ordered;
    }

    private ObservableCollection<RailItemViewModel> BuildDiscoveredItems(List<string> names)
    {
        var items = new ObservableCollection<RailItemViewModel>();
        foreach (var name in names)
        {
            string valueTag;
            lock (_liveVarValues)
                valueTag = _liveVarValues.TryGetValue(name, out var v) ? v : string.Empty;
            items.Add(new RailItemViewModel
            {
                Name  = name,
                // Show the live runtime value (when known) in the right-hand
                // tag column; falls back to the empty string so the row reads
                // as "discovered, not yet observed".
                Type  = valueTag,
                Color = ColorForVarType(null),
                Kind  = "variable",
                Id    = name,
            });
        }
        return items;
    }

    // Fold the live value alongside each name so a DEBUG_VAR_SET-driven value
    // change (same name set) still invalidates the signature gate and repaints
    // the value tag. Reads the live cache under its lock.
    private string BuildDiscoverySignature(List<string> names)
    {
        if (names.Count == 0) return string.Empty;
        var sb = new System.Text.StringBuilder(names.Count * 24);
        lock (_liveVarValues)
        {
            foreach (var n in names)
            {
                sb.Append(n).Append('=');
                if (_liveVarValues.TryGetValue(n, out var v)) sb.Append(v);
                sb.Append(';');
            }
        }
        return sb.ToString();
    }

    // 0.10.0 theme P2: hex string outputs resolved from the live theme
    // dictionary so a token retune in PhoenixDark.xaml flows through to
    // every rail variable swatch.
    private static string ColorForVarType(string? type) => (type ?? "string").ToLowerInvariant() switch
    {
        "int" or "integer" or "number" => ResolveHex("PinIntColorBrush",  "#7FBED1"),
        "bool" or "boolean"            => ResolveHex("PinBoolColorBrush", "#9CC97A"),
        _                              => ResolveHex("EmberPrimaryBrush", "#E5A24E"),
    };

    //  [P2] — memoize resolved hex strings per brush key. ResolveHex
    // is hit once per variable/process/macro row on every Refresh(); a rebuild
    // with 50+ rows triggered O(N) redundant resource-dictionary lookups +
    // SolidColorBrush→hex conversions per cycle. Theme tokens are effectively
    // static at runtime (a retune is a restart-level event), so caching the
    // first successful resolution amortizes the cost to one lookup per key.
    // The cache only stores successful resolutions — designer/pre-app misses
    // fall through to the caller's fallback every time (cheap, and they stop
    // happening once Application.Current.Resources is live).
    private static readonly Dictionary<string, string> _hexColorCache =
        new(StringComparer.Ordinal);
    private static readonly object _hexCacheGate = new();

    /// <summary>
    ///  [P2] — drop every cached theme-color hex so the next
    /// <see cref="ResolveHex"/> re-reads the live dictionary. Call this from
    /// the host when the app theme changes (rare) so a token retune flows
    /// through to the rail swatches without a restart.
    /// </summary>
    public static void InvalidateThemeColorCache()
    {
        lock (_hexCacheGate) _hexColorCache.Clear();
    }

    private static string ResolveHex(string key, string fallback)
    {
        lock (_hexCacheGate)
        {
            if (_hexColorCache.TryGetValue(key, out var cached))
                return cached;
        }
        try
        {
            if (Application.Current?.Resources is { } res
                && res.TryGetValue(key, out var found)
                && found is SolidColorBrush brush)
            {
                var c = brush.Color;
                var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                lock (_hexCacheGate) _hexColorCache[key] = hex;
                return hex;
            }
        }
        catch { /* designer-time / pre-app construction — fall through */ }
        return fallback;
    }

    // ── Footer button strips ────────────────────────────────────────────────

    // Buttons whose enablement depends on the owning section's SelectedItem.
    // SelectionChanged on the section re-evaluates IsEnabled live so a freshly
    // tapped row lights its footer actions without a full Refresh cycle.
    private readonly List<(RailSection Section, RailButton Button, Func<bool> Predicate)> _gatedButtons = new();

    private void BuildButtonStrips()
    {
        AddButton(VariablesSection, "+",  Resource("OkBrush"),  "Add variable",     "add",    requiresSelection: false, () => AddVariableAsync());
        AddButton(VariablesSection, "−",  Resource("ErrBrush"), "Remove variable",  "remove", requiresSelection: true,  () => RemoveSelectedVariableAsync());
        AddButton(VariablesSection, "Aa", null,                 "Rename variable",  "rename", requiresSelection: true,  () => RenameSelectedVariableAsync());

        AddButton(ProcessesSection, "+",  Resource("OkBrush"),  "New process",      "add",    requiresSelection: false, () => AddProcessAsync());
        //  [P2] — "↗" (navigate / open editor) not "✎" (edit-in-place):
        // the rail opens a SubGraphWindow, it does not inline-edit the process.
        AddButton(ProcessesSection, "↗",  null,                 "Edit process",     "edit",   requiresSelection: true,  () => { OpenSelectedProcess(); return System.Threading.Tasks.Task.CompletedTask; });
        AddButton(ProcessesSection, "Aa", null,                 "Rename process",   "rename", requiresSelection: true,  () => RenameSelectedProcessAsync());
        AddButton(ProcessesSection, "×",  Resource("ErrBrush"), "Delete process",   "delete", requiresSelection: true,  () => DeleteSelectedProcessAsync());

        AddButton(MacrosSection, "+",  Resource("OkBrush"),           "New macro",       "add",     requiresSelection: false, () => AddMacroAsync());
        //  [P2] — "↗" navigate glyph (opens the macro's SubGraphWindow),
        // matching the baseline editMacroBtn affordance; "✎" wrongly implied
        // inline editing.
        AddButton(MacrosSection, "↗",  null,                          "Edit macro",      "edit",    requiresSelection: true,  () => { OpenSelectedMacro(); return System.Threading.Tasks.Task.CompletedTask; });
        AddButton(MacrosSection, "Aa", null,                          "Rename macro",    "rename",  requiresSelection: true,  () => RenameSelectedMacroAsync());
        AddButton(MacrosSection, "×",  Resource("ErrBrush"),          "Delete macro",    "delete",  requiresSelection: true,  () => DeleteSelectedMacroAsync());
        AddButton(MacrosSection, "+G", Resource("EmberPrimaryBrush"), "Publish globally", "publish", requiresSelection: true,  () => { PublishSelectedMacroGlobally(); return System.Threading.Tasks.Task.CompletedTask; });

        // Wire one SelectionChanged listener per section that re-evaluates
        // every gated button under it; previously each button had no way to
        // know its row was tapped, so disabled stayed disabled until the
        // next Refresh()/rebuild.
        VariablesSection.SelectionChanged += (_, _) => ReevaluateGatedButtons();
        ProcessesSection.SelectionChanged += (_, _) => ReevaluateGatedButtons();
        MacrosSection.SelectionChanged    += (_, _) => ReevaluateGatedButtons();

        ReevaluateGatedButtons();
    }

    private void AddButton(RailSection section, string glyph, Brush? accent, string tooltip,
                           string actionName, bool requiresSelection, Func<System.Threading.Tasks.Task> onClick)
    {
        var btn = new RailButton { Glyph = glyph };
        if (accent is not null) btn.AccentBrush = accent;
        ToolTipService.SetToolTip(btn, tooltip);
        //  [P1] — the click handler is intentionally async void
        // (event-handler contract), but the inner await is wrapped in
        // try-catch so a fault inside any CRUD lambda (Add/Delete/Rename
        // for variable/macro/process) is routed to GlobalLogger.Error
        // instead of crashing the process as an unobserved exception. The
        // previous signature took Action and invoked it fire-and-forget,
        // swallowing every async fault silently.
        btn.Clicked += async (_, _) =>
        {
            // Guardrail — per feedback_no_modal_dialogs_for_repeatable_rejections.md
            // and Architect UI P1 (rail.<action>.no-selection log). The button
            // shouldn't fire when disabled, but log defensively in case the gate
            // gets bypassed (keyboard shortcut, scripted invoke, etc.).
            if (requiresSelection && section.SelectedItem is null)
            {
                GlobalLogger.Log(
                    $"rail.{actionName}.no-selection",
                    source: "Architect.LeftRail",
                    level: LogLevel.System);
                return;
            }
            try
            {
                await onClick();
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("Architect.LeftRail", "button-action", ex);
            }
        };
        if (requiresSelection)
        {
            _gatedButtons.Add((section, btn, () => section.SelectedItem is not null));
            btn.IsEnabled = false;
        }
        section.ButtonStrip.Children.Add(btn);
    }

    private void ReevaluateGatedButtons()
    {
        foreach (var (_, btn, predicate) in _gatedButtons)
            btn.IsEnabled = predicate();
    }

    // ── F2 inline rename ────────────────────────────────────────────────

    private string? ResolveSelectedVariableId() => VariablesSection.SelectedItem?.Id ?? _selectedVarId;
    private string? ResolveSelectedMacroId()    => MacrosSection.SelectedItem?.Id    ?? _selectedMacroId;
    private string? ResolveSelectedProcessId()  => ProcessesSection.SelectedItem?.Id ?? _selectedProcessId;

    /// <summary>
    /// Inline F2 rename — Architect UX P1. Captures F2 at the section level
    /// (so the user doesn't need keyboard focus on the specific row) and
    /// opens a TextBox Flyout anchored on the items area. Enter commits via
    /// the section's existing rename pipeline; Esc cancels with no mutation.
    /// </summary>
    private void HookF2(RailSection section, Func<string?> resolveSelectedId,
                        Func<string, System.Threading.Tasks.Task> renameById)
    {
        section.KeyDown += (_, ke) =>
        {
            if (ke.Key != VirtualKey.F2) return;
            var id = resolveSelectedId();
            if (string.IsNullOrEmpty(id)) return;
            ke.Handled = true;
            ShowInlineRenameFlyout(section, id!, renameById);
        };
    }

    private void ShowInlineRenameFlyout(RailSection section, string id,
                                        Func<string, System.Threading.Tasks.Task> renameById)
    {
        var picked = section.SelectedItem;
        if (picked is null) return;

        var box = new TextBox
        {
            Text     = picked.Name,
            MinWidth = 200,
            FontSize = 12,
        };
        var flyout = new Flyout { Content = box };
        bool committed = false;
        box.KeyDown += async (_, ke) =>
        {
            if (ke.Key == VirtualKey.Enter)
            {
                ke.Handled = true;
                committed = true;
                flyout.Hide();
                var newName = (box.Text ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(newName) || string.Equals(newName, picked.Name, StringComparison.Ordinal))
                    return;
                //  [P1] — this KeyDown handler is async void
                // (fire-and-forget). Wrap the await so a fault inside
                // CommitInlineRenameAsync or any of its per-kind callees
                // (InlineRename{Variable,Macro,Process}Async) is logged
                // instead of escaping as an unobserved exception that
                // silently crashes the rename flow.
                try
                {
                    await CommitInlineRenameAsync(section, id, newName, renameById);
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error("Architect.LeftRail", "inline-rename", ex);
                }
            }
            else if (ke.Key == VirtualKey.Escape)
            {
                ke.Handled = true;
                committed = true;
                flyout.Hide();
            }
        };
        // Anchor on the section so the flyout sits over its items area.
        flyout.ShowAt(section, new FlyoutShowOptions
        {
            Placement = FlyoutPlacementMode.Right,
        });
        box.Focus(FocusState.Programmatic);
        box.SelectAll();
        flyout.Closed += (_, _) => { if (!committed) committed = true; };
    }

    /// <summary>
    /// Route the inline rename through the existing per-kind rename
    /// implementations without the modal NameTypeDialog round-trip.
    /// </summary>
    private async System.Threading.Tasks.Task CommitInlineRenameAsync(
        RailSection section, string id, string newName,
        Func<string, System.Threading.Tasks.Task> renameById)
    {
        // The shared rename helpers below already speak in (id, newName) form
        // via the InlineRename overloads — keeps the dialog-based path
        // intact for "Rename…" menu items while the F2 path skips the dialog.
        if (ReferenceEquals(section, VariablesSection))
            await InlineRenameVariableAsync(id, newName);
        else if (ReferenceEquals(section, MacrosSection))
            await InlineRenameMacroAsync(id, newName);
        else if (ReferenceEquals(section, ProcessesSection))
            await InlineRenameProcessAsync(id, newName);
        else
            await renameById(id);
    }

    //  P1-A3 — double-tap dispatcher ────────────────────────────

    /// <summary>
    /// Route a rail row's double-tap to the matching editor. Macro/Process
    /// open their <c>SubGraphWindow</c>; Variable opens the rename dialog
    /// (variables have no sub-graph editor — the dialog is the closest
    /// pre-T15 MainForm double-click parity for them).
    /// </summary>
    private void OnRailItemDoubleTapped(object? sender, RailItemViewModel? item)
    {
        if (item is null) return;
        switch (item.Kind)
        {
            case "macro":    OpenMacroById(item.Id); break;
            case "process":  OpenProcessById(item.Id); break;
            // [P1 swarm-audit 2026-05-29] Was a bare `_ = RenameVariableAsync(...)`
            // fire-and-forget — a fault inside the async rename (dialog, DB write)
            // would surface as an unobserved-task crash. Phoenix.Controls.Hub.Core's
            // AsyncErrorBoundary isn't referenceable from Architect.WinUI (no Hub
            // ProjectReference, pillar separation), so observe the task with an
            // async lambda + try/catch routing to GlobalLogger.
            case "variable": _ = ObserveRenameVariableAsync(item.Id); break;
        }
    }

    // [P1 swarm-audit 2026-05-29] Observed wrapper for the double-tap rename so
    // any fault is logged instead of crashing the app via an unobserved task.
    private async System.Threading.Tasks.Task ObserveRenameVariableAsync(string variableId)
    {
        try { await RenameVariableAsync(variableId); }
        catch (Exception ex)
        {
            GlobalLogger.Error("Architect.LeftRail", "rail variable double-tap rename", ex);
        }
    }

    // Right-click context menus on rail items ────────────────────────────

    private void WireItemContextMenus(RailSection section)
    {
        // Wire after items are bound; ItemsRepeater realizes containers
        // lazily. We use a single delegated handler on the section's host
        // grid that walks up to find the RailItemViewModel.
        section.RightTapped -= OnSectionRightTapped;
        section.RightTapped += OnSectionRightTapped;
    }

    private void OnSectionRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement) return;
        var src = e.OriginalSource as FrameworkElement;
        RailItemViewModel? item = null;
        var walker = src;
        while (walker is not null)
        {
            if (walker.Tag is RailItemViewModel ri) { item = ri; break; }
            walker = (walker.Parent as FrameworkElement) ?? walker.DataContext as FrameworkElement;
        }
        if (item is null) return;
        var menu = BuildContextMenu(item);
        menu.ShowAt(src, e.GetPosition(src));
        e.Handled = true;
    }

    private MenuFlyout BuildContextMenu(RailItemViewModel item)
    {
        var f = new MenuFlyout();
        switch (item.Kind)
        {
            case "variable":
                f.Items.Add(NewItem("Rename…",  "", async () => await RenameVariableAsync(item.Id)));
                f.Items.Add(NewItem("Delete",   "", async () => await DeleteVariableAsync(item.Id)));
                break;
            case "macro":
                f.Items.Add(NewItem("Edit…",            "", () => OpenMacroById(item.Id)));
                f.Items.Add(NewItem("Find references",  "", () => FindMacroReferences(item.Id)));
                f.Items.Add(NewItem("Rename…",          "", async () => await RenameMacroAsync(item.Id)));
                f.Items.Add(NewItem("Publish globally", "", () => PublishMacroGloballyById(item.Id)));
                f.Items.Add(NewItem("Delete",           "", async () => await DeleteMacroAsync(item.Id)));
                break;
            case "process":
                f.Items.Add(NewItem("Edit…",   "", () => OpenProcessById(item.Id)));
                f.Items.Add(NewItem("Rename…", "", async () => await RenameProcessAsync(item.Id)));
                f.Items.Add(NewItem("Delete",  "", async () => await DeleteProcessAsync(item.Id)));
                break;
        }
        return f;
    }

    private static MenuFlyoutItem NewItem(string text, string? glyph, Action handler)
    {
        var i = new MenuFlyoutItem { Text = text };
        if (!string.IsNullOrEmpty(glyph))
            i.Icon = new FontIcon { Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets") };
        i.Click += (_, _) => handler();
        return i;
    }

    // ── Variables CRUD ──────────────────────────────────────────────────

    private async System.Threading.Tasks.Task AddVariableAsync()
    {
        if (_vm is null || XamlRoot is null) return;
        var dlg = NameTypeDialog.ForVariable(XamlRoot, "New variable");
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        //  Duplicate-name guard previously returned silently — violates
        // feedback_no_modal_dialogs_for_repeatable_rejections.md (no modal, but
        // also no silent rejection). Log via GlobalLogger at System tier so the
        // user can see why the add did nothing in the SystemLog panel.
        if (_vm.Graph.Variables.Any(v => v.Name.Equals(dlg.EnteredName, StringComparison.OrdinalIgnoreCase)))
        {
            GlobalLogger.Log(
                $"LeftRail: variable name '{dlg.EnteredName}' already exists — add ignored.",
                "LeftRail",
                LogLevel.System);
            return;
        }
        _vm.Graph.Variables.Add(new VariableDefinition
        {
            Name = dlg.EnteredName,
            Type = dlg.EnteredType,
            DefaultValue = dlg.EnteredDefault,
        });
        //  — a declared variable lives in the Graph scope; switch the
        // dropdown there so the user sees the row they just added even if they
        // were browsing the read-only Local / Global discovery views. The
        // scope switch refreshes the list, so no extra Refresh() is needed
        // when the scope actually changed.
        if (EnsureGraphScopeForCrud()) return;
        Refresh();
    }

    /// <summary>
    ///  — flip the Variables dropdown to the Graph scope (index 2) so
    /// a declared-variable CRUD result is visible. Returns true when the scope
    /// actually changed (which itself triggered a rebuild via ScopeChanged),
    /// so the caller can skip its own Refresh(); returns false when already in
    /// Graph scope (caller should Refresh() as usual).
    /// </summary>
    private bool EnsureGraphScopeForCrud()
    {
        if (_variableScope == 2) return false;
        VariablesSection.SetScopeIndex(2);
        return true;
    }

    private System.Threading.Tasks.Task RemoveSelectedVariableAsync()
    {
        var target = ResolveSelectedVariable();
        return target is null ? System.Threading.Tasks.Task.CompletedTask : DeleteVariableAsync(target.Name);
    }

    private System.Threading.Tasks.Task RenameSelectedVariableAsync()
    {
        var target = ResolveSelectedVariable();
        return target is null ? System.Threading.Tasks.Task.CompletedTask : RenameVariableAsync(target.Name);
    }

    /// <summary>
    /// Resolve the currently-tapped variable. Prefer the section's live
    /// SelectedItem, fall back to the per-rail _selectedVarId for actions
    /// fired right after a Refresh() rebind.
    /// </summary>
    private VariableDefinition? ResolveSelectedVariable()
    {
        if (_vm is null || _vm.Graph.Variables.Count == 0) return null;
        var id = VariablesSection.SelectedItem?.Id ?? _selectedVarId;
        if (!string.IsNullOrEmpty(id))
        {
            var match = _vm.Graph.Variables.FirstOrDefault(v =>
                string.Equals(v.Name, id, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return null;
    }

    private async System.Threading.Tasks.Task DeleteVariableAsync(string name)
    {
        if (_vm is null || XamlRoot is null) return;
        // 0.10.0 UX P2: ForDanger flips warning header on, defaults the
        // close (safer) button, and labels the primary button "Delete".
        var dlg = ConfirmDialog.ForDanger(XamlRoot, "Delete variable",
            $"Remove variable '{name}' from this graph?",
            destructiveVerb: "Delete");
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        _vm.Graph.Variables.RemoveAll(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Refresh();
    }

    private async System.Threading.Tasks.Task RenameVariableAsync(string oldName)
    {
        if (_vm is null || XamlRoot is null) return;
        var existing = _vm.Graph.Variables.FirstOrDefault(v =>
            v.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return;
        var dlg = NameTypeDialog.ForVariable(XamlRoot, "Rename variable",
            existing.Name, existing.Type, existing.DefaultValue,
            existingNames: _vm.Graph.Variables
                .Where(v => !v.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase))
                .Select(v => v.Name));
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        //  P0-A1: one undo entry per rail-driven rename. Snapshot
        // the pre-rename graph BEFORE the mutation so Ctrl+Z restores the
        // old Name/Type/DefaultValue in a single step (mirrors the
        // PushUndoForInlineEdit pattern used by NodeView socket rename).
        PushRailUndo();
        existing.Name = dlg.EnteredName;
        existing.Type = dlg.EnteredType;
        existing.DefaultValue = dlg.EnteredDefault;
        Refresh();
    }

    /// <summary>Inline-rename path (F2) — same model mutation, no dialog.</summary>
    private System.Threading.Tasks.Task InlineRenameVariableAsync(string oldName, string newName)
    {
        if (_vm is null) return System.Threading.Tasks.Task.CompletedTask;
        if (_vm.Graph.Variables.Any(v =>
                !v.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase) &&
                 v.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
        {
            GlobalLogger.Log("rail.rename.variable.duplicate-name",
                source: "Architect.LeftRail", level: LogLevel.System);
            return System.Threading.Tasks.Task.CompletedTask;
        }
        var existing = _vm.Graph.Variables.FirstOrDefault(v =>
            v.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return System.Threading.Tasks.Task.CompletedTask;
        //  P0-A1: snapshot before the mutation so a single undo
        // entry covers the rename. See PushRailUndo for the contract.
        PushRailUndo();
        existing.Name = newName;
        Refresh();
        return System.Threading.Tasks.Task.CompletedTask;
    }

    // ── Macros CRUD ─────────────────────────────────────────────────────

    private async System.Threading.Tasks.Task AddMacroAsync()
    {
        if (_vm is null || XamlRoot is null) return;
        var dlg = NameTypeDialog.ForName(XamlRoot, "New macro", "MACRO");
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        var name = UniquifyMacroName(dlg.EnteredName);
        var macro = new Macro { Name = name };
        _vm.Graph.Macros.Add(macro);
        Refresh();
        //  AVM is required for shared undo + rename sync; bail if
        // the rail wasn't bound to one (only happens in degenerate
        // construction order — log so the missed binding is visible).
        if (_architectVm is null)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                "LeftRail.AddMacro: ArchitectViewModel not bound; skipping editor open.",
                "Architect.LeftRail", Phoenix.Controls.Shared.Models.LogLevel.Debug);
            return;
        }
        SubGraphWindow.OpenMacroEditor(macro, _architectVm, _canvasView);
    }

    private string UniquifyMacroName(string baseName)
    {
        if (_vm is null) return baseName;
        var taken = new HashSet<string>(_vm.Graph.Macros.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName)) return baseName;
        for (int i = 2; i < 10000; i++)
        {
            var n = baseName + "_" + i;
            if (!taken.Contains(n)) return n;
        }
        return baseName + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
    }

    private void OpenSelectedMacro()
    {
        var target = ResolveSelectedMacro();
        //  AVM required (shared undo + rename sync).
        if (target is null || _architectVm is null) return;
        SubGraphWindow.OpenMacroEditor(target, _architectVm, _canvasView);
    }

    /// <summary>
    /// Resolve the user-tapped Macro from the section's live selection,
    /// falling back to the cached id for actions fired right after a
    /// Refresh() rebind.
    /// </summary>
    private Macro? ResolveSelectedMacro()
    {
        if (_vm is null) return null;
        var id = MacrosSection.SelectedItem?.Id ?? _selectedMacroId;
        if (!string.IsNullOrEmpty(id))
        {
            var match = _vm.Graph.Macros.FirstOrDefault(m => m.MacroId == id);
            if (match is not null) return match;
        }
        return null;
    }

    private void OpenMacroById(string id)
    {
        var m = _vm?.Graph.Macros.FirstOrDefault(x => x.MacroId == id);
        //  AVM required (shared undo + rename sync).
        if (m is null || _architectVm is null) return;
        SubGraphWindow.OpenMacroEditor(m, _architectVm, _canvasView);
    }

    private System.Threading.Tasks.Task RenameSelectedMacroAsync()
    {
        var target = ResolveSelectedMacro();
        return target is null ? System.Threading.Tasks.Task.CompletedTask : RenameMacroAsync(target.MacroId);
    }

    private async System.Threading.Tasks.Task RenameMacroAsync(string id)
    {
        if (_vm is null || XamlRoot is null) return;
        var m = _vm.Graph.Macros.FirstOrDefault(x => x.MacroId == id);
        if (m is null) return;
        var dlg = NameTypeDialog.ForName(XamlRoot, "Rename macro", "MACRO", m.Name,
            existingNames: _vm.Graph.Macros
                .Where(x => x.MacroId != id)
                .Select(x => x.Name));
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        ApplyMacroRename(m, dlg.EnteredName);
    }

    private System.Threading.Tasks.Task InlineRenameMacroAsync(string id, string newName)
    {
        if (_vm is null) return System.Threading.Tasks.Task.CompletedTask;
        var m = _vm.Graph.Macros.FirstOrDefault(x => x.MacroId == id);
        if (m is null) return System.Threading.Tasks.Task.CompletedTask;
        if (_vm.Graph.Macros.Any(x => x.MacroId != id
                && string.Equals(x.Name, newName, StringComparison.OrdinalIgnoreCase)))
        {
            GlobalLogger.Log("rail.rename.macro.duplicate-name",
                source: "Architect.LeftRail", level: LogLevel.System);
            return System.Threading.Tasks.Task.CompletedTask;
        }
        ApplyMacroRename(m, newName);
        return System.Threading.Tasks.Task.CompletedTask;
    }

    private void ApplyMacroRename(Macro m, string newName)
    {
        if (_vm is null) return;
        //  P0-A1: snapshot the graph BEFORE the rename + dependent
        // Macro.Call.MacroName sync so the whole transaction collapses into
        // one undo entry. Without this, the user-typed rename plus N
        // dependent-attr writes entered undo history as ZERO entries
        // (Ctrl+Z did nothing). Mirrors the PushUndoForInlineEdit pattern
        // used by NodeView socket-label rename — see UndoRedoController.Push
        // for the snapshot semantics (whole-graph JSON capture, suppressed
        // during Apply, so the foreach loop below is safely inside one scope).
        PushRailUndo();
        m.Name = newName;
        // Sync Macro.Call display titles in-graph.
        foreach (var n in _vm.Graph.Nodes)
        {
            if (n.Title == "Macro.Call" && n.Attributes != null
                && n.Attributes.TryGetValue("MacroId", out var mid) && mid == m.MacroId)
            {
                n.Attributes["MacroName"] = m.Name;
            }
        }
        Refresh();
        _architectVm?.RaiseMacroRenamed(m.MacroId, m.Name ?? string.Empty);
    }

    private System.Threading.Tasks.Task DeleteSelectedMacroAsync()
    {
        var target = ResolveSelectedMacro();
        return target is null ? System.Threading.Tasks.Task.CompletedTask : DeleteMacroAsync(target.MacroId);
    }

    private async System.Threading.Tasks.Task DeleteMacroAsync(string id)
    {
        if (_vm is null || XamlRoot is null) return;
        var m = _vm.Graph.Macros.FirstOrDefault(x => x.MacroId == id);
        if (m is null) return;
        var dlg = ConfirmDialog.ForDanger(XamlRoot, "Delete macro",
            $"Remove macro '{m.Name}'? Macro.Call sites will be detached but kept on the canvas.",
            destructiveVerb: "Delete");
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        MacroOps.DeleteMacro(_vm.Graph, id);
        Refresh();
        _vm.OnGraphMutated();
    }

    private void FindMacroReferences(string id)
    {
        if (_vm is null) return;
        var refs = _vm.Graph.Nodes
            .Where(n => n.Title == "Macro.Call" && n.Attributes != null
                        && n.Attributes.TryGetValue("MacroId", out var mid) && mid == id)
            .ToList();
        if (refs.Count == 0) return;
        var nodeVms = _vm.Nodes.Where(vm => refs.Any(r => r.Id == vm.Id)).ToList();
        _vm.SetMultiSelection(nodeVms);

        //  [P0] — restore the baseline
        // HighlightMacroCallSites behaviour the WinUI rewrite dropped:
        // (1) pan/zoom the viewport to frame the matched nodes so a user
        //     scrolled off-canvas actually sees them, and
        // (2) flash each matched node gold so the matches read as a
        //     transient pulse rather than a silent selection halo.
        // Both run through the existing canvas-view infrastructure
        // (RequestFrameSelectionFromShell frames the current selection,
        // FlashNode pulses by node id) which the rail already holds a
        // back-reference to. FlashNode is the canonical flash path here —
        // LogicCanvasViewModel exposes no NodeFlashRequested (that event
        // lives on ArchitectViewModel); the canvas view's public FlashNode
        // is the same sink ArchitectViewModel.NodeFlashRequested fans into.
        if (_canvasView is not null)
        {
            _canvasView.RequestFrameSelectionFromShell();
            foreach (var nodeVm in nodeVms)
                _canvasView.FlashNode(nodeVm.Id);
        }
    }

    private void PublishSelectedMacroGlobally()
    {
        var target = ResolveSelectedMacro();
        if (target is not null) PublishMacroGloballyById(target.MacroId);
    }

    private void PublishMacroGloballyById(string id)
    {
        if (_vm is null) return;
        var m = _vm.Graph.Macros.FirstOrDefault(x => x.MacroId == id);
        if (m is null) return;
        _vm.RequestPublishMacroGlobally?.Invoke(m);
    }

    // ── Processes CRUD ──────────────────────────────────────────────────

    private async System.Threading.Tasks.Task AddProcessAsync()
    {
        if (_vm is null || XamlRoot is null) return;
        var dlg = NameTypeDialog.ForName(XamlRoot, "New process", "PROCESS");
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        var taken = new HashSet<string>(_vm.Graph.Processes.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var name = dlg.EnteredName;
        if (taken.Contains(name))
        {
            for (int i = 2; i < 10000; i++)
            {
                var n = dlg.EnteredName + "_" + i;
                if (!taken.Contains(n)) { name = n; break; }
            }
        }
        var proc = new Process { Name = name };
        //  [P0] — seed Process.Entry / Process.Exit into the new
        // process body. The baseline ProcessesPanel.AddProcess() did this;
        // the WinUI rewrite dropped it, so new processes opened with an
        // empty body and the user had to hand-add Entry/Exit before the
        // process could accept inputs/outputs (ExporterRegistry's process
        // input/output emit walks Process.Entry's output sockets — without
        // an Entry node the process exports nothing). CreateNode returns
        // null only for unknown titles, so the null-guards are defensive.
        var entryNode = NodeRegistry.CreateNode("Process.Entry", new System.Drawing.Point(80, 160));
        var exitNode  = NodeRegistry.CreateNode("Process.Exit",  new System.Drawing.Point(520, 160));
        if (entryNode != null) proc.Graph.Nodes.Add(entryNode);
        if (exitNode  != null) proc.Graph.Nodes.Add(exitNode);
        _vm.Graph.Processes.Add(proc);
        Refresh();
        //  AVM is required for shared undo + rename sync; bail if
        // the rail wasn't bound to one.
        if (_architectVm is null)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                "LeftRail.AddProcess: ArchitectViewModel not bound; skipping editor open.",
                "Architect.LeftRail", Phoenix.Controls.Shared.Models.LogLevel.Debug);
            return;
        }
        SubGraphWindow.OpenProcessEditor(proc, _architectVm, _canvasView);
    }

    private void OpenSelectedProcess()
    {
        var target = ResolveSelectedProcess();
        //  AVM required (shared undo + rename sync).
        if (target is null || _architectVm is null) return;
        SubGraphWindow.OpenProcessEditor(target, _architectVm, _canvasView);
    }

    /// <summary>
    /// Resolve the user-tapped Process from the section's live selection,
    /// falling back to the cached id for actions fired right after a
    /// Refresh() rebind.
    /// </summary>
    private Process? ResolveSelectedProcess()
    {
        if (_vm is null) return null;
        var id = ProcessesSection.SelectedItem?.Id ?? _selectedProcessId;
        if (!string.IsNullOrEmpty(id))
        {
            var match = _vm.Graph.Processes.FirstOrDefault(p => p.ProcessId == id);
            if (match is not null) return match;
        }
        return null;
    }

    private void OpenProcessById(string id)
    {
        var p = _vm?.Graph.Processes.FirstOrDefault(x => x.ProcessId == id);
        //  AVM required (shared undo + rename sync).
        if (p is null || _architectVm is null) return;
        SubGraphWindow.OpenProcessEditor(p, _architectVm, _canvasView);
    }

    private System.Threading.Tasks.Task RenameSelectedProcessAsync()
    {
        var target = ResolveSelectedProcess();
        return target is null ? System.Threading.Tasks.Task.CompletedTask : RenameProcessAsync(target.ProcessId);
    }

    private async System.Threading.Tasks.Task RenameProcessAsync(string id)
    {
        if (_vm is null || XamlRoot is null) return;
        var p = _vm.Graph.Processes.FirstOrDefault(x => x.ProcessId == id);
        if (p is null) return;
        var dlg = NameTypeDialog.ForName(XamlRoot, "Rename process", "PROCESS", p.Name,
            existingNames: _vm.Graph.Processes
                .Where(x => x.ProcessId != id)
                .Select(x => x.Name));
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        ApplyProcessRename(p, dlg.EnteredName);
    }

    private System.Threading.Tasks.Task InlineRenameProcessAsync(string id, string newName)
    {
        if (_vm is null) return System.Threading.Tasks.Task.CompletedTask;
        var p = _vm.Graph.Processes.FirstOrDefault(x => x.ProcessId == id);
        if (p is null) return System.Threading.Tasks.Task.CompletedTask;
        if (_vm.Graph.Processes.Any(x => x.ProcessId != id
                && string.Equals(x.Name, newName, StringComparison.OrdinalIgnoreCase)))
        {
            GlobalLogger.Log("rail.rename.process.duplicate-name",
                source: "Architect.LeftRail", level: LogLevel.System);
            return System.Threading.Tasks.Task.CompletedTask;
        }
        ApplyProcessRename(p, newName);
        return System.Threading.Tasks.Task.CompletedTask;
    }

    private void ApplyProcessRename(Process p, string newName)
    {
        if (_vm is null) return;
        //  P0-A1: snapshot the graph BEFORE the rename + dependent
        // Process.Spawn.ProcessName sync. See ApplyMacroRename for the
        // shared rationale — one undo entry covers rename + N sync writes.
        PushRailUndo();
        p.Name = newName;
        foreach (var n in _vm.Graph.Nodes)
        {
            if (n.Title == "Process.Spawn" && n.Attributes != null
                && n.Attributes.TryGetValue("ProcessId", out var pid) && pid == p.ProcessId)
            {
                n.Attributes["ProcessName"] = p.Name;
            }
        }
        Refresh();
        _architectVm?.RaiseProcessRenamed(p.ProcessId, p.Name ?? string.Empty);
    }

    private System.Threading.Tasks.Task DeleteSelectedProcessAsync()
    {
        var target = ResolveSelectedProcess();
        return target is null ? System.Threading.Tasks.Task.CompletedTask : DeleteProcessAsync(target.ProcessId);
    }

    private async System.Threading.Tasks.Task DeleteProcessAsync(string id)
    {
        if (_vm is null || XamlRoot is null) return;
        var p = _vm.Graph.Processes.FirstOrDefault(x => x.ProcessId == id);
        if (p is null) return;
        var dlg = ConfirmDialog.ForDanger(XamlRoot, "Delete process",
            $"Remove process '{p.Name}'? Process.Spawn sites will be detached but kept on the canvas.",
            destructiveVerb: "Delete");
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        _vm.Graph.Processes.RemoveAll(x => x.ProcessId == id);
        // Mirror MacroOps.DeleteMacro shape — strip ProcessId + non-Flow non-placeholder
        // sockets + their links from every Process.Spawn pointing at this process.
        foreach (var n in _vm.Graph.Nodes)
        {
            if (n.Title != "Process.Spawn" || n.Attributes is null) continue;
            if (!n.Attributes.TryGetValue("ProcessId", out var pid) || pid != id) continue;
            n.Attributes.Remove("ProcessId");
            var dropped = n.Sockets
                .Where(s => s.Name != "Flow" && !s.IsPlaceholder)
                .Select(s => s.Id)
                .ToHashSet();
            if (dropped.Count == 0) continue;
            n.Sockets.RemoveAll(s => dropped.Contains(s.Id));
            _vm.Graph.Links.RemoveAll(l =>
                (l.FromNodeId == n.Id && dropped.Contains(l.FromSocketId)) ||
                (l.ToNodeId   == n.Id && dropped.Contains(l.ToSocketId)));
        }
        Refresh();
        _vm.OnGraphMutated();
    }

    // ── Theme resource fallback ─────────────────────────────────────────

    private static readonly Brush s_resourceFallback =
        new SolidColorBrush(Microsoft.UI.Colors.Gray);

    private static Brush Resource(string key)
    {
        try { return (Application.Current.Resources[key] as Brush) ?? s_resourceFallback; }
        catch { return s_resourceFallback; }
    }
}
