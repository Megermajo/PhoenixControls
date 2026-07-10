using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Architect.WinUI.Canvas;
using Phoenix.Controls.Architect.WinUI.Controls;
using Phoenix.Controls.Architect.WinUI.Databank;
using Phoenix.Controls.Architect.WinUI.Services;
using Phoenix.Controls.Architect.WinUI.ViewModels;
using Phoenix.Controls.Architect.WinUI.Hosting;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.WinUI.Contracts;
using Phoenix.Controls.Shared.WinUI.Services;
using Windows.Foundation;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Phoenix.Controls.Architect.WinUI;

// Single-HUB collapse (TODO.md P0 #3): Architect now ships as a UserControl
// embedded inside Hub.MainWindow's pillar-tab region rather than a
// standalone Window. The host Hub passes its own Window in via the ctor
// so file pickers can grab the HWND through WindowNative.GetWindowHandle.
// PillarSwitchRequested bubbles tab-clicks back up to Hub for in-process
// view swapping; LauncherService is gone.
//
// Implements IPillarShellHost so Hub.MainWindow can ask "any unsaved
// work?" before honouring AppWindow.Closing. Dirty tracking
// is now live: ArchitectViewModel.IsDirty flips true whenever
// LogicCanvasViewModel.GraphMutatedAny fires (any AddNode / RemoveNode /
// AddFrame / RemoveFrame / RemoveLink / TryCreateLink / multi-delete /
// drag-drop / paste path), and clears on Save / Open / NewGraph. The
// chrome bullet (UpdateChromeFilename) and the close-prompt
// (PromptSaveBeforeCloseAsync) both read it.
public sealed partial class MainView : UserControl, IPillarShellHost, ICanExecuteUndoRedo
{
    private readonly Window _hostWindow;
    private readonly ArchitectViewModel _viewModel;

    private readonly LogicCanvasView _logicCanvasView;
    private readonly LogicInspector _placeholderLogicInspector = new();
    private readonly DatabankInspector _placeholderDatabankInspector = new();
    private DatabankBrowserView? _databankBrowserView;
    // Cached failure surface so repeated clicks on a broken Databank tab
    // don't allocate a fresh placeholder each time. Built lazily inside
    // BuildDatabankFailurePlaceholder.
    private UserControl? _databankFailurePlaceholder;

    private Phoenix.Controls.Architect.WinUI.Services.AutosaveService? _autosave;

    // One-shot guards for Loaded / Unloaded handler bodies.
    // Hub.MainWindow caches `_architectView` and re-parents it on pillar-tab
    // swap; WinUI fires Loaded again on every reattach (and Unloaded on
    // every detach). Without these guards, every tab swap re-runs
    // StartAutosaveAndScanForSurvivors, InitializeAsync, ApplyPersistedLayout
    // AndState, and the symmetrical Unloaded body calls Dispose / StopAutosave
    // a second time — leaking timers, accumulating autosave instances, and
    // double-disposing the AVM. Flip the flag on first fire; ignore the rest.
    private bool _loadedOnceRan;
    private bool _unloadedOnceRan;
    private double _inspectorMaxHeight = 280.0;
    /// <summary>
    /// Right-rail inspector card vertical cap. The InspectorThumb splitter
    /// was removed (the chevron roll-up replaced it
    /// as the size affordance), but the cap is still persisted across
    /// sessions via ConfigManager.ArchitectInspectorHeight and pushed into
    /// InspectorRegion.MaxHeight on load so a long Description doesn't run
    /// off the bottom of the card. Reads / writes through the property keep
    /// the clamp envelope in place.
    /// </summary>
    public double InspectorMaxHeight
    {
        get => _inspectorMaxHeight;
        private set
        {
            double clamped = System.Math.Clamp(value, 120.0, 720.0);
            if (System.Math.Abs(_inspectorMaxHeight - clamped) < 0.5) return;
            _inspectorMaxHeight = clamped;
            if (InspectorRegion is not null)
                InspectorRegion.MaxHeight = clamped;
        }
    }

    // 0.10.0 — column splitters between Rail / Canvas
    // and Canvas / Inspector. HorizontalChange is delta px since last
    // DragDelta; we mutate the column Width inside the MinWidth/MaxWidth
    // envelope and persist via SchedulePersistLayout (debounced).

    private void OnRailSplitterDragDelta(object sender, Microsoft.UI.Xaml.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (RailColumn is null) return;
        // A live drag wins over any in-flight collapse/expand tween.
        _railWidthTween?.Stop();
        double next = RailColumn.ActualWidth + e.HorizontalChange;
        next = System.Math.Clamp(next, RailColumn.MinWidth,
            double.IsInfinity(RailColumn.MaxWidth) ? 9999.0 : RailColumn.MaxWidth);
        RailColumn.Width = new GridLength(next, GridUnitType.Pixel);
        SchedulePersistLayout();
    }

    private void OnRailSplitterKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (RailColumn is null) return;
        double delta = e.Key switch
        {
            Windows.System.VirtualKey.Left  => -16,
            Windows.System.VirtualKey.Right => +16,
            _ => 0,
        };
        if (delta == 0) return;
        double next = System.Math.Clamp(RailColumn.ActualWidth + delta, RailColumn.MinWidth,
            double.IsInfinity(RailColumn.MaxWidth) ? 9999.0 : RailColumn.MaxWidth);
        RailColumn.Width = new GridLength(next, GridUnitType.Pixel);
        SchedulePersistLayout();
        e.Handled = true;
    }

    // OnInspectorColumnSplitterDragDelta /
    // OnInspectorColumnSplitterKeyDown removed alongside the
    // InspectorSplitterCol column in MainView.xaml. The chevron toggle is
    // now the only inspector-visibility affordance and the inspector
    // width is fixed at the persisted ArchitectInspectorColumnWidth.

    // ─── Rail collapse / docked Inspector ──

    private const double RailExpandedWidth   = 220.0;
    private const double RailCollapsedWidth  = 32.0;

    /// <summary>
    /// Default docked-Inspector column width when a fresh
    /// install (or stale config) doesn't have a persisted width yet. Slim
    /// enough to honour the "slim panel" invariant while still fitting the
    /// Description paragraph at a
    /// readable wrap. LogicInspector caps its own content at MaxWidth=320
    /// internally so a wider drag still produces a readable layout.
    /// </summary>
    private const double InspectorDockedDefaultWidth = 320.0;

    /// <summary>
    /// Minimum splitter-drag width for the docked
    /// Inspector. Below this the column-width clamp snaps the inspector
    /// closed (sets visibility to false) so the user can dismiss it by
    /// dragging the splitter all the way right. The MinWidth on the
    /// ColumnDefinition itself stays at 0 so programmatic
    /// <c>Width = new GridLength(0)</c> still hides the column.
    /// </summary>
    private const double InspectorDockedMinWidth = 200.0;

    // ─── Short panel open/close width animation ────────────────────────────
    // Per-window copy (chrome helpers stay per-window). Tweens a Grid
    // column's pixel width to a target over ~170 ms (ease-out cubic) via a
    // DispatcherTimer. Width-only: a viewport-width change does NOT re-measure
    // the absolutely-positioned canvas nodes, so this stays cheap even on large
    // graphs (the freeze class was socket-row measure fan-out, not viewport
    // resize). The returned timer lets the next toggle cancel an in-flight tween.
    private const double PanelAnimMs = 170.0;
    private Microsoft.UI.Xaml.DispatcherTimer? _railWidthTween;
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
            col.Width = new Microsoft.UI.Xaml.GridLength(toPx, Microsoft.UI.Xaml.GridUnitType.Pixel);
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
                col.Width = new Microsoft.UI.Xaml.GridLength(
                    from + (toPx - from) * eased, Microsoft.UI.Xaml.GridUnitType.Pixel);
                if (t >= 1.0)
                {
                    timer.Stop();
                    col.Width = new Microsoft.UI.Xaml.GridLength(toPx, Microsoft.UI.Xaml.GridUnitType.Pixel);
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

    /// <summary>
    /// Apply the rail-collapsed flag to RailColumn.Width.
    /// Called from <see cref="ApplyPersistedLayoutAndState"/> on boot AND
    /// from the rail's <see cref="LeftRail.RailCollapseToggled"/> dispatch
    /// when the user clicks the chevron. The persisted column width takes
    /// precedence when the rail is NOT collapsed (so a user-dragged
    /// in-between width survives); when collapsed we snap to
    /// <see cref="RailCollapsedWidth"/>.
    /// </summary>
    private void ApplyRailCollapsedToColumn(bool collapsed, bool animate = true)
    {
        if (RailColumn is null) return;
        double target;
        if (collapsed)
        {
            target = RailCollapsedWidth;
        }
        else
        {
            var cfg = Phoenix.Controls.Shared.Services.ConfigManager.Current;
            double w = cfg.ArchitectRailColumnWidth > 0
                ? cfg.ArchitectRailColumnWidth
                : RailExpandedWidth;
            // Defensive clamp — a stale config might
            // hold a width below the new 32 px MinWidth; treat values <
            // RailExpandedWidth's lower envelope as "use the expanded default".
            if (w < 100) w = RailExpandedWidth;
            target = w;
        }
        // Animate the rail open/close. On expand, show the full rail
        // content BEFORE growing so it slides into view; on collapse, shrink
        // first then swap to the collapsed glyph strip on completion. Boot /
        // persisted-state restore passes animate:false for an instant layout.
        if (!animate)
        {
            _railWidthTween?.Stop();
            RailColumn.Width = new GridLength(target, GridUnitType.Pixel);
            Rail.SetRailCollapsedGlyph(collapsed);
            return;
        }
        // Set the rail glyph EAGERLY (both directions) before the width tween,
        // matching the expand path. If the tween is cancelled mid-flight (a
        // splitter drag calls _railWidthTween?.Stop()), a deferred onComplete
        // glyph swap would never fire — leaving a narrow rail showing the
        // expanded glyph.
        Rail.SetRailCollapsedGlyph(collapsed);
        _railWidthTween = AnimatePanelColumnWidth(RailColumn, target, _railWidthTween);
    }

    private void OnRailCollapseToggled(object? sender, bool desiredCollapsed)
    {
        ApplyRailCollapsedToColumn(desiredCollapsed);
        try
        {
            var cfg = Phoenix.Controls.Shared.Services.ConfigManager.Current;
            cfg.ArchitectRailCollapsed = desiredCollapsed;
            // Don't overwrite ArchitectRailColumnWidth here — keep the
            // user's pre-collapse drag preserved so re-expanding restores
            // the same width they last chose.
            // Deferred (background) write — the synchronous
            // Save here blocked the UI thread on every toggle.
            Phoenix.Controls.Shared.Services.ConfigManager.SaveDeferred(
                Phoenix.Controls.Shared.Core.Paths.AppConfigJson);
        }
        catch (System.Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "Architect.MainView", "OnRailCollapseToggled persist", ex);
        }
    }

    /// <summary>
    /// Width of the InspectorColumn when the
    /// inspector body is rolled up. Just enough for the chevron button to
    /// stay clickable so the user can re-expand. Matches the
    /// <see cref="RailCollapsedWidth"/> idiom on the LeftRail side.
    /// </summary>
    private const double InspectorRolledUpWidth = 32.0;

    /// <summary>Tracks whether the inspector body is currently expanded;
    /// the chevron toggle, the splitter drag, and the persisted-state path
    /// all read / write this so the column-width math stays consistent.</summary>
    private bool _inspectorExpanded = true;

    /// <summary>
    /// Apply the Inspector-visible (expanded)
    /// state to the right column. The chevron header strip ALWAYS stays
    /// painted; expanded vs rolled-up changes the column width (full vs
    /// 32 px), the inspector body's visibility, and the chevron glyph.
    /// InspectorSplitterCol references were dropped after the
    /// splitter column was removed from MainView.xaml.
    /// </summary>
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
            target = System.Math.Clamp(w, InspectorDockedMinWidth, maxW);
        }
        else
        {
            // Rolled up — column holds at chevron-strip width so the user
            // can re-expand.
            target = InspectorRolledUpWidth;
        }
        UpdateInspectorChevronGlyph(visible);
        // Animate the inspector roll-up/down. On expand, reveal the
        // body BEFORE growing so it slides in; on collapse, shrink first then
        // collapse the body on completion so it fades with the width. Boot /
        // persisted-state restore passes animate:false for an instant layout.
        if (visible)
        {
            if (InspectorRegion is not null)
                InspectorRegion.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
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
                            InspectorRegion.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    });
            }
            else
            {
                _inspectorWidthTween?.Stop();
                InspectorColumn.Width = new GridLength(target, GridUnitType.Pixel);
                if (InspectorRegion is not null)
                    InspectorRegion.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            }
        }
    }

    /// <summary>
    /// Swap the chevron button's glyph + tooltip to match the inspector
    /// expanded / rolled-up state. E70D = ChevronDownMed (body visible,
    /// click rolls up); E70E = ChevronUpMed (body rolled up, click expands).
    /// </summary>
    private void UpdateInspectorChevronGlyph(bool expanded)
    {
        if (InspectorChevronButton is null) return;
        try
        {
            InspectorChevronButton.Content = expanded ? "" : "";
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(InspectorChevronButton,
                expanded ? "Roll up Inspector" : "Show Inspector");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                InspectorChevronButton,
                expanded ? "Roll up Inspector" : "Show Inspector");
        }
        catch (System.Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "Architect.MainView", "UpdateInspectorChevronGlyph", ex);
        }
    }

    /// <summary>
    /// Chevron click handler. Toggles the
    /// expanded / rolled-up state via the same path
    /// <see cref="OnInspectorToggleRequested"/> uses for the (now-removed)
    /// LeftRail Inspector button, so the persist + glyph update flow stays
    /// shared.
    /// </summary>
    private void OnInspectorChevronClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        OnInspectorToggleRequested(this, !_inspectorExpanded);
    }

    /// <summary>
    /// F4 from the canvas and
    /// View → Toggle Inspector from the chrome both arrive here. Flips the
    /// expanded / rolled-up state through the same code path the chevron
    /// uses so the persist + glyph update flow stays shared.
    /// </summary>
    private void OnInspectorToggleRequestedFromCanvas(object? sender, System.EventArgs e)
    {
        OnInspectorToggleRequested(this, !_inspectorExpanded);
    }

    private void OnInspectorToggleRequested(object? sender, bool desiredVisible)
    {
        try
        {
            ApplyInspectorVisibleToColumn(desiredVisible);
            Rail.SetInspectorVisibleGlyph(desiredVisible);

            var cfg = Phoenix.Controls.Shared.Services.ConfigManager.Current;
            cfg.ArchitectInspectorVisible = desiredVisible;
            // Deferred (background) write — the synchronous
            // Save here blocked the UI thread on every inspector toggle.
            Phoenix.Controls.Shared.Services.ConfigManager.SaveDeferred(
                Phoenix.Controls.Shared.Core.Paths.AppConfigJson);
        }
        catch (System.Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "Architect.MainView", "OnInspectorToggleRequested", ex);
        }
    }

    /// <summary>
    /// 0.10.0 — debounced ConfigManager.Save for layout
    /// fields (inspector height, both column widths, show-grid, debug-trace).
    /// Each splitter drag fires DragDelta at ~60 Hz; ConfigManager.Save
    /// writes the whole AppConfig JSON on every call, so coalescing keeps
    /// the user-drag interaction off the disk hot path. 400 ms timer is
    /// long enough that a single drag flushes once; the Unloaded teardown
    /// also flushes any pending tick.
    /// </summary>
    private DispatcherTimer? _persistLayoutTimer;
    private void SchedulePersistLayout()
    {
        if (_persistLayoutTimer is null)
        {
            _persistLayoutTimer = new DispatcherTimer
            {
                Interval = System.TimeSpan.FromMilliseconds(400),
            };
            _persistLayoutTimer.Tick += (_, _) =>
            {
                _persistLayoutTimer!.Stop();
                FlushPersistLayout();
            };
        }
        _persistLayoutTimer.Stop();
        _persistLayoutTimer.Start();
    }

    /// <param name="synchronous">
    /// When false (the debounced-timer path) the write is deferred to a
    /// background thread so a slow disk never stalls the UI. When true (the
    /// Unloaded teardown path) the write runs synchronously so the final
    /// layout state is guaranteed to land before the window/VM goes away.
    /// </param>
    private void FlushPersistLayout(bool synchronous = false)
    {
        try
        {
            var cfg = Phoenix.Controls.Shared.Services.ConfigManager.Current;
            cfg.ArchitectInspectorHeight       = _inspectorMaxHeight;
            if (RailColumn      is not null) cfg.ArchitectRailColumnWidth      = RailColumn.ActualWidth;
            // Only persist the inspector column width when
            // the dock is actually visible. When hidden (Width=0), preserve
            // the previously persisted width so a hide → show cycle restores
            // the user's last-chosen size instead of forcing the default.
            if (InspectorColumn is not null && InspectorColumn.ActualWidth >= InspectorDockedMinWidth)
                cfg.ArchitectInspectorColumnWidth = InspectorColumn.ActualWidth;
            cfg.ArchitectShowGrid              = _viewModel.LogicCanvas.ShowGrid;
            cfg.ArchitectDebugTraceEnabled     = _viewModel.LiveDebugEnabled;
            cfg.ArchitectLastActiveTab         = _viewModel.IsLogicActive ? "logic" : "databank";
            if (synchronous)
                Phoenix.Controls.Shared.Services.ConfigManager.Save(
                    Phoenix.Controls.Shared.Core.Paths.AppConfigJson);
            else
                Phoenix.Controls.Shared.Services.ConfigManager.SaveDeferred(
                    Phoenix.Controls.Shared.Core.Paths.AppConfigJson);
        }
        catch (System.Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "Architect.MainView", "FlushPersistLayout", ex);
        }
    }

    /// <summary>
    /// KeyDown on the InspectorThumb — Left/Right arrows nudge the inspector
    /// column width by ±16px (clamped to MinWidth/MaxWidth). Mirrors the
    /// vertical drag affordance for keyboard users.
    /// </summary>
    private void OnInspectorThumbKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (InspectorColumn is null) return;
        double delta = 0;
        if (e.Key == Windows.System.VirtualKey.Left)  delta = -16;
        if (e.Key == Windows.System.VirtualKey.Right) delta = +16;
        if (delta == 0) return;
        double min = InspectorColumn.MinWidth > 0 ? InspectorColumn.MinWidth : 0;
        double max = InspectorColumn.MaxWidth > 0 && !double.IsInfinity(InspectorColumn.MaxWidth)
            ? InspectorColumn.MaxWidth
            : double.PositiveInfinity;
        double next = InspectorColumn.ActualWidth + delta;
        if (next < min) next = min;
        if (next > max) next = max;
        InspectorColumn.Width = new Microsoft.UI.Xaml.GridLength(next, Microsoft.UI.Xaml.GridUnitType.Pixel);
        e.Handled = true;
    }

    /// <summary>Raised when the embedded chrome's pillar tab is clicked.
    /// Hub.MainWindow swaps its MainPaneRegion content in response.</summary>
    public event EventHandler<PillarKind>? PillarSwitchRequested;

    public MainView(Window hostWindow)
    {
        _hostWindow = hostWindow ?? throw new ArgumentNullException(nameof(hostWindow));

        InitializeComponent();

        // First access to NodeRegistry trips its static ctor,
        // which JIT-compiles ~3,000 lines of template registration synchronously
        // on the calling thread. If that first touch is the user pressing SPACE
        // to open the spawn palette, it lands on the UI thread as a ~5s stall
        // before the menu is usable (Issue 4). Warm the registry off-thread now,
        // at Architect view construction — well before the first SPACE. The CLR
        // type-initializer lock makes this safe against a concurrent UI-thread
        // access (e.g. graph-load GetTemplate): one thread runs the init, the
        // other waits, no double work, no corruption. Registration builds plain
        // POCOs + System.Drawing.Color (no UI-thread affinity), so a worker
        // thread is fine. Best-effort — never fail startup over the warm.
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try { _ = Phoenix.Controls.Architect.Core.NodeRegistry.GetAllTemplates(); }
            catch { /* warm is best-effort */ }
        });

        // Single-HUB collapse — Hub.MainWindow embeds this view inside its
        // MainPaneRegion and owns the only visible chrome (HubChrome). The
        // pillar-local ArchitectChrome would stack a second wordmark + tab
        // strip below Hub's, so we hide it visually. Hub's chrome swaps
        // its menu strip to Architect-specific items (File / Edit / View /
        // Help with architect.* tokens) when this pillar is active and
        // dispatches the menu invocations into the Open/Save/Undo/etc.
        // public action methods below.
        //
        // Hide via Height=0 + ClipToBounds rather than
        // Visibility=Collapsed. WinUI ignores KeyboardAccelerator
        // declarations on subtrees with `Visibility=Collapsed`, so the
        // 11 chord declarations on ArchitectChrome (Ctrl+N/O/S, Ctrl+Shift+S,
        // Ctrl+Z/Y, Ctrl+F/G, Ctrl+Shift+D, F, F1) used to be dead under
        // Hub. Keeping Chrome `Visible` with `Height=0` leaves the
        // accelerators live in the visual tree while the band paints zero
        // pixels — the menus stay accessible via the chords even though
        // the chrome itself is invisible. The standalone sibling-window
        // path (ArchitectSiblingWindow) is unaffected: it owns its own
        // chrome instance with full visibility.
        Chrome.Height = 0;
        Chrome.MinHeight = 0;
        Chrome.IsHitTestVisible = false;
        // Defence-in-depth: if a hard-coded inner row ever leaks layout
        // past the 0-height envelope, the clip suppresses the paint.
        Chrome.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, 0, 0),
        };

        _viewModel = new ArchitectViewModel();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.LogicCanvas.PropertyChanged += OnCanvasVmPropertyChanged;

        // Pillar-tab clicks bubble up to Hub.MainWindow (no more sibling
        // process spawn). LiveDebug toggle stays local to this view.
        // 0.10.0 — every chrome menu action restores focus to the canvas
        // afterward via RestoreCanvasFocus so DEL / arrows / Ctrl+S /
        // Ctrl+Space keep firing without an extra click on the canvas.
        // Mirrors the SpawnPaletteFlyout.Closed pattern in
        // LogicCanvasView.Palette.cs (canvas-spawned flyouts already do
        // this; the chrome menus need the same hook).
        // 0.10.0 — ArchitectVm MUST be assigned before DataContext: when
        // DataContext is set the canvas's OnDataContextChanged builds the
        // _history reference, falling back to a per-canvas controller when
        // ArchitectVm is still null. C# object-initializers execute in
        // source order so set ArchitectVm here first via property; the
        // shared AVM-level History is picked up on first bind. Macro.Call /
        // Process.Spawn right-click → "Edit graph" still routes through
        // SubGraphWindow.Open*Editor.
        _logicCanvasView = new LogicCanvasView
        {
            ArchitectVm = _viewModel,
            DataContext = _viewModel.LogicCanvas,
        };

        // Chrome menu subscriptions — named handlers (was inline lambdas).
        // Named methods so HookChromeHandlers
        // / UnhookChromeHandlers can pair them symmetrically; pre-fix every
        // lambda captured `this` and so survived pillar-tab swaps, growing
        // by ~25 handlers per MainView reconstruction. MUST be called after
        // `_logicCanvasView` is constructed — the method also wires the
        // canvas's MinimapVisibilityChanged + KeyboardShortcutsRequested
        // events, which would NRE on the field if hooked first (regression
        // that surfaced
        // as a startup crash that prevented the Architect pillar from
        // opening — SystemHistory id 1276879 on 2026-05-22).
        HookChromeHandlers();
        _logicCanvasView.SaveRequested += async (_, _) => await OnFileSaveRequestedAsync();
        _logicCanvasView.OpenRequested += async (_, _) => await OnFileOpenRequestedAsync();
        // Explorer file-drop on the canvas spawns a sibling Architect window
        // per the 0.10.0 multi-window spec — drag-drop never clobbers the
        // current canvas. ArchitectWindowRegistry dedups by path so dropping
        // a .phxg that's already open in another sibling just focuses it.
        _logicCanvasView.FileOpenRequested += (_, path) =>
        {
            // OpenFile is async; fire-and-forget through
            // AsyncErrorBoundary so the drop handler stays sync void and
            // a faulted load gets logged instead of swallowed.
            _ = Phoenix.Controls.Shared.Core.AsyncErrorBoundary.SafeRunAsync(
                () => ArchitectWindowRegistry.OpenFileAsync(path),
                "Architect",
                $"FileOpenRequested drop '{path}' → sibling spawn");
        };

        // 0.10.0 — Welcome card buttons. The "New Graph" /
        // "Open .phxg…" / "Recent…" actions route through the same paths the
        // chrome menu uses (sibling-window spawn semantics for the multi-
        // window paradigm).
        _logicCanvasView.NewRequested        += (_, _) => OnFileNewRequested();
        _logicCanvasView.OpenRecentRequested += async (_, _) => { await OpenRecentAsync(); RestoreCanvasFocus(); };

        // LayerFileOpenRequested: Explorer-dropped .phxlayer files
        // need to land in Visualist, not Architect. Route through the
        // IPillarNavigator the host implements (Hub.MainWindow). Wrapped in
        // try/cast — the host Window need not implement the navigator (e.g.
        // headless tests), in which case we log and fall back to the canvas
        // System-log warning the pre-fix code already emitted.
        _logicCanvasView.LayerFileOpenRequested += (_, path) =>
        {
            // TODO — Hub.MainWindow.NavigateTo(PillarKind.Visualist,
            // openTarget: path) must actually drive a swap to the Visualist
            // tab and call its Open(path) entrypoint. This only
            // raises the call; the receiving navigator wiring is still owed.
            try
            {
                if (_hostWindow is Phoenix.Controls.Shared.WinUI.Contracts.IPillarNavigator nav)
                {
                    nav.NavigateTo(Phoenix.Controls.Shared.WinUI.Contracts.PillarKind.Visualist, openTarget: path);
                }
                else
                {
                    Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                        $"Architect: .phxlayer drop '{path}' — host does not implement IPillarNavigator; ignoring.",
                        "Architect", Phoenix.Controls.Shared.Models.LogLevel.System);
                }
            }
            catch (Exception ex)
            {
                Phoenix.Controls.Shared.Services.GlobalLogger.Error("Architect",
                    $"LayerFileOpenRequested '{path}' navigation failed", ex);
            }
        };

        _viewModel.NodeFlashRequested += nodeId => _logicCanvasView.FlashNode(nodeId);

        // Wire the Hub MACRO_SYNC stream into the
        // rail so the `[G]` global-macro prefix lights up. The AVM owns the
        // ArchitectBusClient subscription + JSON parse; we just forward the
        // resolved id collection into the rail's existing SetGlobalMacroIds
        // entry point. Pre-fix nobody subscribed; the rail's _globalMacroIds
        // stayed empty regardless of Hub broadcasts.
        _viewModel.GlobalMacroIdsChanged += ids =>
        {
            if (DispatcherQueue is null) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                Rail.SetGlobalMacroIds(ids);
                // The Macro.Call socket-refresh
                // cascade now lives in ArchitectViewModel.OnBusMacroSync ->
                // OnRefreshMacroSignature -> RefreshMacroCallSockets, which syncs
                // from the CANONICAL global-macro signature and re-renders via a
                // targeted NodeViewModel.RebuildSockets pass that preserves canvas
                // selection. Calling RefreshMacroCallNodesForSyncedMacros here as
                // well ran a SECOND, full LoadGraph re-render on every MACRO_SYNC
                // signature change (flicker + lost selection) off the possibly-
                // stale LOCAL signature. Removed; the now-unused helper below is
                // left for a dedicated dead-code cleanup pass.
            });
        };
        _viewModel.LogicCanvas.GraphMutatedAny += OnCanvasGraphMutatedForStatus;
        _viewModel.LogicCanvas.GraphLoaded     += OnCanvasGraphMutatedForStatus;

        // Surface .phx sister-file export failures on the status bar so a
        // user who saw "Saved foo.phxg." doesn't keep wondering why Hub is
        // still running the previous script. Pre-fix the catch in
        // ExportPhxBeside was empty and the status bar still read green
        // "Saved".
        _viewModel.PhxExportFailed += (_, reason) =>
        {
            if (DispatcherQueue is null) return;
            DispatcherQueue.TryEnqueue(() => SetStatus(
                ".phxg saved — .phx export failed (Hub will keep running the old script). See System Log.",
                ArchitectStatusLight.Yellow));
        };
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ArchitectViewModel.LiveDebugEnabled)
                || e.PropertyName == nameof(ArchitectViewModel.LoadedFilePath))
            {
                RefreshStatusCounts();
            }
        };

        Rail.SetCanvasContext(_viewModel.LogicCanvas);
        Rail.SetArchitectContext(_viewModel);
        // Hand the canvas view back to the rail so SubGraphWindow
        // editors launched from rail rows refocus this canvas on close.
        Rail.SetCanvasView(_logicCanvasView);

        // Rail collapse + floating Inspector window. Both
        // toggles persist their state through ConfigManager so a user who
        // likes a tight canvas-first layout doesn't have to re-collapse on
        // every restart. ApplyPersistedRailAndInspectorState (run from
        // ApplyPersistedLayoutAndState below) replays the saved flags into
        // the visible chrome on first paint.
        Rail.RailCollapseToggled       += OnRailCollapseToggled;
        Rail.InspectorToggleRequested  += OnInspectorToggleRequested;
        Loaded += async (_, _) =>
        {
            // One-shot: pillar-tab reattach fires Loaded again; we
            // only want the autosave / persisted-layout / CLI-bootstrap to
            // run on the FIRST Loaded of this MainView instance.
            if (_loadedOnceRan) return;
            _loadedOnceRan = true;
            RefreshStatusCounts();
            RegisterStatusZoneTooltips(); // live hover resolvers
            RefreshZoomBadge(); // seed the badge
            RefreshBusStatusDot(Phoenix.Controls.Architect.Core.ArchitectBusClient.Instance.IsConnected);
            StartAutosaveAndScanForSurvivors();
            // Drive deferred ctor work through InitializeAsync
            // so file I/O (--open routing via CliBootstrap) doesn't block the
            // synchronous MainView ctor return.
            // Now truly async — await so CLI bootstrap's --open path
            // settles _viewModel.LoadedFilePath before ApplyPersistedLayoutAndState
            // checks that field as the "CLI took priority" signal.
            try
            {
                await InitializeAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                    "Architect.MainView", "Loaded → InitializeAsync", ex);
            }
            // 0.10.0 — apply persisted layout / state
            // AFTER InitializeAsync's CLI bootstrap. CLI --open explicitly
            // overrides the persisted last-opened .phxg (the user typed the
            // path on the command line — that's a stronger signal than recall).
            ApplyPersistedLayoutAndState();
            // 0.10.0 — focus the canvas after Loaded so
            // the user's first keystroke (DEL / arrows / Ctrl+S / Ctrl+Space)
            // lands on the canvas instead of vanishing into the empty
            // pillar-tab strip. Mirrors the focus-restore pattern used in
            // every chrome-menu handler.
            RestoreCanvasFocus();
        };
        Unloaded += (_, _) =>
        {
            // One-shot: pillar-tab detach fires Unloaded again on
            // every swap; teardown (StopAutosave, AVM.Dispose, etc.) must
            // run exactly once. Skipping this guard caused Dispose-twice
            // and StopAutosave-twice on the FIRST tab swap away from Architect.
            if (_unloadedOnceRan) return;
            _unloadedOnceRan = true;
            // Flush any pending layout-persist tick on teardown so a fast
            // close right after a splitter drag still captures the move.
            try { _persistLayoutTimer?.Stop(); FlushPersistLayout(synchronous: true); } catch { /* best-effort */ }
            // Unhook the LogicCanvas PropertyChanged subscription so the
            // old VM can be collected if a new MainView is constructed
            // (pillar-tab swap). The AVM.Dispose chain handles the AVM's
            // own subscriptions; this is the MainView-owned hook.
            try { _viewModel.LogicCanvas.PropertyChanged -= OnCanvasVmPropertyChanged; }
            catch { /* best-effort */ }
            // — unhook the Chrome menu subscriptions so
            // a pillar-tab swap doesn't grow Chrome's invocation list by
            // ~18 handlers per MainView reconstruction.
            UnhookChromeHandlers();
            // Unhook the rail-driven toggles so a pillar-tab
            // swap doesn't accumulate handlers on the cached Rail.
            try { Rail.RailCollapseToggled       -= OnRailCollapseToggled; }      catch { /* best-effort */ }
            try { Rail.InspectorToggleRequested  -= OnInspectorToggleRequested; } catch { /* best-effort */ }
            // Unhook the ArchitectBusClient singleton subscriptions wired once
            // in the ctor below. These MUST live inside the _unloadedOnceRan
            // one-shot so they pair 1:1 with the single ctor-time subscribe —
            // a standalone (unguarded) Unloaded lambda fired on every
            // pillar-tab detach, letting the singleton's invocation list drift
            // (leaked handlers across reattach, ObjectDisposed when a stale
            // handler fires).
            try { Phoenix.Controls.Architect.Core.ArchitectBusClient.Instance.OnConnectionStatusChanged
                    -= OnBusConnectionStatusChanged; } catch { /* best-effort */ }
            try { Phoenix.Controls.Architect.Core.ArchitectBusClient.Instance.OnConnectionStateChanged
                    -= OnBusConnectionStateChanged; } catch { /* best-effort */ }
            // Close the floating Inspector window so it doesn't linger past
            // the host's lifetime (a pillar-tab swap or full Hub close).
            // The window persists its geometry on Closed regardless of
            // whether the user clicked X or we close it here.
            try { Phoenix.Controls.Architect.WinUI.Hosting.InspectorWindow.CloseIfOpen(); }
            catch { /* best-effort */ }
            StopAutosave();
        };

        Phoenix.Controls.Architect.Core.ArchitectBusClient.Instance.OnConnectionStatusChanged
            += OnBusConnectionStatusChanged;
        // Three-state signal — wires Reconnecting → yellow on the dot during
        // the 5s reconnect backoff. Also
        // pipes the same signal into the AVM so the live-debug bus-drop
        // InfoBar can auto-promote from the 8×8 dot when live-debug is on
        // and the bus drops.
        Phoenix.Controls.Architect.Core.ArchitectBusClient.Instance.OnConnectionStateChanged
            += OnBusConnectionStateChanged;
        // NOTE: the matching -= for these two bus subscriptions now lives in
        // the _unloadedOnceRan one-shot teardown block above (next to the
        // Chrome/Rail unhooks), so subscribe-in-ctor pairs with exactly one
        // unsubscribe. Previously a separate, unguarded Unloaded lambda here
        // ran on every pillar-tab detach, drifting the singleton's handler
        // count across reattach.

        // Surface .phxg load failures via the InfoBar at MainView Row 2 — the
        // user with a corrupt graph used to see only an empty canvas plus a
        // one-line System Log entry. The failure
        // is non-blocking. Subscribe + unsubscribe with a static event keeps
        // the wire idempotent across MainView reconstruction (sub-graph
        // editor windows reuse the same VM but not this UserControl).
        Phoenix.Controls.Architect.Core.GraphSerializer.OnLoadFailed -= ShowLoadFailureInfoBar;
        Phoenix.Controls.Architect.Core.GraphSerializer.OnLoadFailed += ShowLoadFailureInfoBar;
        Unloaded += (_, _) =>
            Phoenix.Controls.Architect.Core.GraphSerializer.OnLoadFailed -= ShowLoadFailureInfoBar;

        // ArchitectViewModel
        // implements IDisposable to unhook the canvas/databank/bus event
        // subscriptions it wired in its ctor. Without an Unloaded handler
        // invoking Dispose, those subscriptions kept the AVM alive past
        // MainView teardown (pillar-tab swap / window close).
        // Use the same _unloadedOnceRan one-shot as the autosave
        // teardown above so a pillar-tab detach doesn't Dispose the AVM
        // twice (subscriptions are already gone after the first pass).
        bool avmDisposed = false;
        Unloaded += (_, _) =>
        {
            if (avmDisposed) return;
            avmDisposed = true;
            try { _viewModel.Dispose(); }
            catch (Exception ex)
            {
                Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                    "MainView", "Architect ViewModel Dispose", ex);
            }
        };

        _placeholderLogicInspector.DataContext = _viewModel.LogicInspector;
        _placeholderDatabankInspector.DataContext = _viewModel.DatabankInspector;

        TopTabs.TabChanged += OnTabChanged;
        MainPaneRegion.Content   = _logicCanvasView;
        InspectorRegion.Content  = _placeholderLogicInspector;

        // CliBootstrap.HandleCli used to run here in the
        // ctor; deferred to InitializeAsync (driven from Loaded) so a
        // --open <path> doesn't hold up MainView construction with a
        // GraphSerializer load + wildcard cascade on the synchronous path.
    }

    /// <summary>
    /// Async post-ctor init. Today this hosts the
    /// CLI deep-link routing (--open) that used to run synchronously in
    /// the ctor. Future expensive work (recent-files preload, autosave
    /// scan, ArchitectBusClient warm-up) belongs here too. Idempotent — the
    /// <see cref="_initialized"/> gate makes repeat calls cheap no-ops.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            // Await the async CLI handler so --open <path> resolves
            // before the Loaded path falls through to ApplyPersistedLayoutAndState
            // (which uses _viewModel.LoadedFilePath as the "CLI took priority" signal).
            await CliBootstrap.HandleCliAsync(Environment.GetCommandLineArgs(), _viewModel)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "Architect", "InitializeAsync CliBootstrap", ex);
        }
    }

    private bool _initialized;

    // ─── Public action surface — driven by Hub.MainWindow's menu dispatch ─

    /// <summary>File → New Graph. Wraps the existing pillar-chrome handler so
    /// HubChrome's "architect.file.new" item routes to the same code path.</summary>
    public void NewGraph() => OnFileNewRequested();

    /// <summary>File → Open. Shows the picker; bound to "architect.file.open".</summary>
    public Task OpenFileAsync() => OnFileOpenRequestedAsync();

    /// <summary>File → Save. Auto-routes to Save As when the graph has no path. Bound to "architect.file.save".</summary>
    public Task SaveFileAsync() => OnFileSaveRequestedAsync();

    /// <summary>File → Export to .phx (writes the sibling .phx beside the loaded .phxg). Bound to "architect.file.export".</summary>
    public void ExportPhx() => OnFileExportRequested();

    /// <summary>Edit → Undo. Routes through the canvas's undo/redo controller.</summary>
    public void Undo() => _logicCanvasView.RequestUndoFromShell();

    /// <summary>Edit → Redo.</summary>
    public void Redo() => _logicCanvasView.RequestRedoFromShell();

    /// <inheritdoc/>
    public bool CanUndo => _logicCanvasView.CanUndo;

    /// <inheritdoc/>
    public bool CanRedo => _logicCanvasView.CanRedo;

    /// <inheritdoc/>
    public event EventHandler? CanExecuteChanged
    {
        add    => _logicCanvasView.UndoRedoChanged += value;
        remove => _logicCanvasView.UndoRedoChanged -= value;
    }

    /// <summary>View → Live Debug Trace toggle.</summary>
    public void ToggleLiveDebug() => _viewModel.SetLiveDebugEnabled(!_viewModel.LiveDebugEnabled);

    /// <summary>
    /// View → Show Grid toggle. Flips the canvas grid-visible flag; the
    /// LogicCanvasView listens on ShowGrid PropertyChanged and shows / hides
    /// its grid Path layer accordingly.
    /// </summary>
    public void ToggleShowGrid()
        => _viewModel.LogicCanvas.ShowGrid = !_viewModel.LogicCanvas.ShowGrid;

    /// <summary>
    /// View → Frame Selection (UE-Blueprints F idiom). Routes through the
    /// canvas's selection-aware fit. With no selection it falls back to
    /// zoom-to-fit; with a selection it pads by 60px and clamps zoom to
    /// [0.2, 2.0] per pre-T15 FrameSelection.
    /// </summary>
    public void FrameSelection() => _logicCanvasView.RequestFrameSelectionFromShell();

    /// <summary>Edit → Find Node — opens the in-graph node finder Flyout
    /// (filters by Title against the active graph). 0.10.0 — replaces the
    /// pre-0.10.0 placeholder that recycled the spawn-palette template
    /// catalog. The flyout's own Closed handler restores canvas focus.</summary>
    public void FindNode() => _logicCanvasView.RequestFindNodeFromShell();

    /// <summary>
    /// 0.10.0 — re-grab keyboard focus on the embedded canvas after a
    /// chrome menu / inspector / databank surface closes. Mirrors the
    /// SpawnPaletteFlyout.Closed pattern in LogicCanvasView.Palette.cs so
    /// DEL / arrows / Ctrl+S / Ctrl+Space keep firing without forcing the
    /// user to click on the canvas first. Wrapped because Focus throws
    /// when the control isn't in the visual tree (pillar-tab swap mid-flight).
    /// </summary>
    public void RestoreCanvasFocus() => _logicCanvasView.RestoreCanvasFocus();

    /// <summary>0.10.0 — Edit → Group selection / Ctrl+G. Wraps the
    /// current multi-selection in a Macro shell. No-op on &lt;2 selected
    /// nodes (CollapseSelectionToMacro guards on count).</summary>
    public void GroupSelection() => _logicCanvasView.RequestGroupFromShell();

    /// <summary>0.10.0 — Edit → Add comment frame / bare C. Drops a
    /// 240×160 gold-ember frame whose centre lands at the viewport
    /// centre.</summary>
    public void AddCommentFrame() => _logicCanvasView.RequestAddCommentFrameFromShell();

    /// <summary>0.10.0 — View → Spawn node…. Opens the spawn palette at
    /// the canvas viewport centre. The keyboard chord (Ctrl+Space) is
    /// canvas-side; this entry surfaces the action through the menu.</summary>
    public void SpawnAtViewCenter() => _logicCanvasView.RequestSpawnAtViewCenterFromShell();

    /// <summary>File → Welcome. Force-shows the always-available Welcome card
    /// overlay on the logic canvas. Bound to "architect.file.welcome".
    /// Non-destructive — Architect has no cross-session graph persistence, so
    /// the Welcome card is the empty-start state; re-showing it on demand
    /// leaves the current graph open behind the overlay (it does NOT clear or
    /// close it). Mirrors the Open-Recent / F5 shell pass-through pattern.</summary>
    public void ShowWelcome() => _logicCanvasView.RequestShowWelcomeFromShell();

    /// <summary>Help → Node Reference — opens the non-modal node-reference window.</summary>
    public void OpenNodeDocs()
    {
        try
        {
            Phoenix.Controls.Architect.WinUI.Hosting.NodeDocumentationWindow.OpenOrFocus();
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error("Architect",
                "OpenNodeDocs failed", ex);
        }
    }

    /// <summary>File → Open Recent. Shows a ContentDialog listing the 10-deep
    /// MRU; picks always spawn a sibling Architect window (0.10.0 multi-window
    /// spec).</summary>
    public async Task OpenRecentAsync()
    {
        try
        {
            var dlg = new Phoenix.Controls.Architect.WinUI.Dialogs.RecentFilesDialog
            { XamlRoot = XamlRoot };
            await dlg.ShowAsync();
            var picked = dlg.PickedPath;
            if (string.IsNullOrEmpty(picked)) return;
            if (!System.IO.File.Exists(picked))
            {
                Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                    $"Recent file no longer exists: {picked}",
                    "Architect", Phoenix.Controls.Shared.Models.LogLevel.System);
                Phoenix.Controls.Architect.WinUI.Services.RecentFiles.Remove(picked);
                return;
            }
            // First open fills the blank
            // default window; subsequent opens spawn siblings. See
            // OnFileOpenRequestedAsync for the same blank-detection
            // rationale.
            bool embeddedIsBlank =
                string.IsNullOrEmpty(_viewModel.LoadedFilePath)
                && _viewModel.LogicCanvas.Graph.Nodes.Count == 0;

            if (embeddedIsBlank)
            {
                bool ok = await _viewModel.OpenAsync(picked);
                if (!ok)
                {
                    SetStatus(
                        $"Failed to load {System.IO.Path.GetFileName(picked)} — file unreadable or malformed. See System Log.",
                        ArchitectStatusLight.Red);
                }
                else
                {
                    SetStatus(
                        $"Opened {System.IO.Path.GetFileName(picked)}.",
                        ArchitectStatusLight.Green);
                }
                return;
            }

            // Sibling spawn — registry dedup keeps "Open Recent" of an
            // already-open .phxg from creating a duplicate window.
            // Await the async load so a fault propagates into
            // the surrounding try/catch instead of escaping unobserved.
            // Surface parse failures (see
            // OnFileOpenRequestedAsync for rationale).
            var spawned = await ArchitectWindowRegistry.OpenFileAsync(picked);
            if (spawned is null)
            {
                SetStatus(
                    $"Failed to load {System.IO.Path.GetFileName(picked)} — file unreadable or malformed. See System Log.",
                    ArchitectStatusLight.Red);
            }
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error("Architect",
                "OpenRecent failed", ex);
        }
    }

    /// <summary>
    /// Opens the given .phxg / .phx in the Logic Canvas. Called by Hub's
    /// <see cref="IPillarNavigator"/> after the user double-clicks a graph
    /// in Explorer. Forwards to the
    /// underlying ArchitectViewModel.OpenAsync which handles the
    /// GraphSerializer load + wildcard cascade.
    ///
    /// Was sync Open; now async so the underlying VM no longer
    /// needs the deadlock-prone GetAwaiter().GetResult() shim.
    /// </summary>
    public async Task<bool> OpenAsync(string path)
    {
        bool ok = await _viewModel.OpenAsync(path).ConfigureAwait(true);
        if (ok)
        {
            // Loading a graph clears the autosave-recovery prompt — the
            // user has moved on, so a stale "recover unsaved work" banner is just
            // noise from here on.
            DismissAutosaveInfoBar();
            Phoenix.Controls.Architect.WinUI.Services.RecentFiles.TouchDeferred(path);
            DispatcherQueue?.TryEnqueue(RebuildChromeRecentMenu);
        }
        return ok;
    }

    /// <inheritdoc/>
    public bool IsDirty => _viewModel.IsDirty;

    /// <inheritdoc/>
    public string CurrentDocumentDisplayName =>
        string.IsNullOrEmpty(_viewModel.LoadedFilePath)
            ? "(unsaved)"
            : Path.GetFileName(_viewModel.LoadedFilePath);

    /// <inheritdoc/>
    public event System.EventHandler? ShellStateChanged;

    /// <inheritdoc/>
    /// <remarks>
    /// Pre-T15 prompt parity — when the graph has unsaved edits, ask "Save / Discard / Cancel".
    /// </remarks>
    public async Task<bool> PromptSaveBeforeCloseAsync()
    {
        if (!_viewModel.IsDirty) return true;
        var dlg = new ContentDialog
        {
            Title = Localizer.T("architect.dialog.unsaved_changes.title", "Unsaved changes"),
            Content = Localizer.T("architect.dialog.unsaved_changes.content.graph",
                "Save changes to the active graph before closing?"),
            PrimaryButtonText = Localizer.T("common.button.save", "Save"),
            SecondaryButtonText = Localizer.T("common.button.discard", "Discard"),
            CloseButtonText = Localizer.T("common.button.cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await OnFileSaveRequestedAsync();
            return !_viewModel.IsDirty;
        }
        return result == ContentDialogResult.Secondary;
    }

    public void SetCanvasViews(
        UIElement? logic,
        UIElement? databank,
        UIElement? logicInspector,
        UIElement? databankInspector)
    {
        if (logic is not null)              MainPaneRegion.Content   = logic;
        if (logicInspector is not null)     InspectorRegion.Content  = logicInspector;
        _ = databank;
        _ = databankInspector;
        ApplyTab(TopTabs.SelectedTab);
    }

    /// <summary>
    /// 0.10.2: inert fallback for when DatabankBrowserView's ctor or DataContext
    /// binding throws — the user sees a "Databank tab unavailable" surface with
    /// the underlying error instead of a crashed Architect pillar. Cached so
    /// repeated tab swaps don't reallocate.
    /// </summary>
    private UserControl BuildDatabankFailurePlaceholder(Exception ex)
    {
        if (_databankFailurePlaceholder is not null) return _databankFailurePlaceholder;

        var res = Application.Current?.Resources;
        Brush surface  = (res?["CoalSurfaceBrush"]        as Brush) ?? new SolidColorBrush(Microsoft.UI.Colors.Black);
        Brush divider  = (res?["CoalDividerBrush"]        as Brush) ?? new SolidColorBrush(Microsoft.UI.Colors.DimGray);
        Brush textErr  = (res?["ErrBrush"]                as Brush) ?? new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
        Brush textBody = (res?["CoalSecondaryTextBrush"]  as Brush) ?? new SolidColorBrush(Microsoft.UI.Colors.Silver);

        var heading = new TextBlock
        {
            Text       = "Databank tab unavailable",
            FontSize   = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = textErr,
            Margin     = new Thickness(0, 0, 0, 6),
            TextWrapping = TextWrapping.Wrap,
        };
        var body = new TextBlock
        {
            Text       = "Architect couldn't show the databank browser. See the System Log for the full exception — this is usually a missing theme token or a renamed binding after a recent merge.",
            FontSize   = 12,
            Foreground = textBody,
            TextWrapping = TextWrapping.Wrap,
            Margin     = new Thickness(0, 0, 0, 6),
        };
        var detail = new TextBox
        {
            Text       = ex?.ToString() ?? "(no exception captured)",
            FontSize   = 11,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(1),
            BorderBrush = divider,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Foreground = textBody,
            MaxHeight  = 240,
        };

        var stack = new StackPanel
        {
            MaxWidth = 720,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        stack.Children.Add(heading);
        stack.Children.Add(body);
        stack.Children.Add(detail);

        var border = new Border
        {
            Background = surface,
            BorderBrush = divider,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(24),
            Child = stack,
        };
        _databankFailurePlaceholder = new UserControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment   = VerticalAlignment.Stretch,
            Content = border,
        };
        return _databankFailurePlaceholder;
    }

    private void OnTabChanged(object? sender, ArchitectTab tab) => ApplyTab(tab);

    private void ApplyTab(ArchitectTab tab)
    {
        switch (tab)
        {
            case ArchitectTab.Logic:
                MainPaneRegion.Content = _logicCanvasView;
                InspectorRegion.Content = _placeholderLogicInspector;
                _viewModel.OnTabChanged(ArchitectViewModel.TabLogic);
                // 0.10.0 — return focus to the canvas on
                // every swap back to the Logic tab so the user's DEL /
                // Ctrl+Space / arrow keystrokes land on the graph without an
                // extra click. The bare canvas IsTabStop=true so Focus
                // succeeds even when the previous focus owner was the
                // databank table grid.
                RestoreCanvasFocus();
                break;
            case ArchitectTab.Databank:
                // 0.10.2: guard the Databank tab activation so a XAML parse
                // / theme-resource / converter fault in DatabankBrowserView
                // can't crash the whole Architect window. The fallback is an
                // inert placeholder that explains the failure and points to
                // the System Log for the actual exception.
                try
                {
                    _databankBrowserView ??= new DatabankBrowserView { DataContext = _viewModel.DatabankBrowser };
                    MainPaneRegion.Content = _databankBrowserView;
                }
                catch (Exception dbex)
                {
                    Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                        "Architect.MainView", "Databank tab activation failed — showing placeholder.", dbex);
                    MainPaneRegion.Content = BuildDatabankFailurePlaceholder(dbex);
                }
                InspectorRegion.Content = _placeholderDatabankInspector;
                _viewModel.OnTabChanged(ArchitectViewModel.TabDatabank);
                break;
        }
        // 0.10.0 — persist last-active tab on every swap. ApplyTab is the
        // only place the tab toggles (programmatic + user-clicked both go
        // through here via OnTabChanged).
        SchedulePersistLayout();
    }

    /// <summary>
    /// 0.10.0 — apply persisted layout (inspector height,
    /// column widths, show-grid, debug-trace toggle, last-active tab) after
    /// CliBootstrap.HandleCli has had a chance to set LoadedFilePath via
    /// <c>--open &lt;path&gt;</c>. If CLI did NOT seed a file, fall back to
    /// the recall of the last-opened .phxg.
    /// </summary>
    private void ApplyPersistedLayoutAndState()
    {
        try
        {
            var cfg = Phoenix.Controls.Shared.Services.ConfigManager.Current;

            // Inspector height cap — preserve the previous user-chosen drag.
            if (cfg.ArchitectInspectorHeight > 0)
                InspectorMaxHeight = cfg.ArchitectInspectorHeight;

            // Column widths — clamp to declared MinWidth/MaxWidth so a stale
            // config from a different layout doesn't wedge the columns out
            // of range. The docked Inspector column is
            // gone (Width=0), so InspectorColumnWidth recall is no longer
            // applied here; the floating InspectorWindow owns its own
            // geometry via InspectorWindowStateStore.
            if (cfg.ArchitectRailColumnWidth > 0 && RailColumn is not null && !cfg.ArchitectRailCollapsed)
            {
                double w = System.Math.Clamp(cfg.ArchitectRailColumnWidth,
                    RailColumn.MinWidth,
                    double.IsInfinity(RailColumn.MaxWidth) ? 9999.0 : RailColumn.MaxWidth);
                // Defensive — MinWidth was dropped to 32 px so the
                // collapse mode works; reject sub-100-px persisted expanded
                // widths to avoid a stale config defeating the readable mode.
                if (w < 100) w = RailExpandedWidth;
                RailColumn.Width = new GridLength(w, GridUnitType.Pixel);
            }

            // Apply persisted rail-collapsed state. Must run
            // BEFORE the inspector toggle so the visible chrome reaches
            // steady state before we spawn a floating window on top.
            ApplyRailCollapsedToColumn(cfg.ArchitectRailCollapsed, animate: false);

            // Restore the docked Inspector when
            // ArchitectInspectorVisible was true at last shutdown. Routes
            // through ApplyInspectorVisibleToColumn so the column-width
            // restore honours the persisted ArchitectInspectorColumnWidth.
            // Pre-fix this branch spawned a floating InspectorWindow — the
            // floating window infrastructure is still in the
            // codebase but no longer the default surface.
            //
            // One-shot migration. Pre-polish users who
            // closed the floating InspectorWindow persisted
            // ArchitectInspectorVisible = false; that state now hides the
            // new docked card by default, defeating the "inspector open by
            // default" spec. The migration flag forces the docked
            // inspector visible on the first launch where it's false, then
            // sets the flag true so the user's subsequent chevron clicks
            // are respected.
            if (!cfg.ArchitectInspectorDockedMigrated)
            {
                cfg.ArchitectInspectorVisible      = true;
                cfg.ArchitectInspectorDockedMigrated = true;
                Phoenix.Controls.Shared.Services.ConfigManager.SaveDeferred(
                    Phoenix.Controls.Shared.Core.Paths.AppConfigJson);
            }
            ApplyInspectorVisibleToColumn(cfg.ArchitectInspectorVisible, animate: false);
            Rail.SetInspectorVisibleGlyph(cfg.ArchitectInspectorVisible);

            // Show-grid toggle. The default (true) wins until the user has
            // explicitly flipped it — which is what cfg.ArchitectShowGrid
            // already records (default true on a clean install).
            _viewModel.LogicCanvas.ShowGrid = cfg.ArchitectShowGrid;
            Chrome.SetShowGridChecked(cfg.ArchitectShowGrid);

            // Minimap visibility. The overlay defaults visible
            // (true on a clean install); restore the user's preference here
            // so the surface lands at the persisted state on launch. The
            // chrome toggle's IsChecked is mirrored so the menu glyph
            // matches what the user actually sees on the canvas.
            _logicCanvasView.ApplyMinimapVisibilityFromConfig();
            Chrome.SetMinimapChecked(cfg.ArchitectMinimapVisible);

            // Live-debug toggle — defer the actual bus connect by routing
            // through SetLiveDebugEnabled so the bus client starts if true
            // and stays stopped if false (no-op when state already matches).
            if (cfg.ArchitectDebugTraceEnabled)
                _viewModel.SetLiveDebugEnabled(true);
            Chrome.SetLiveDebugChecked(cfg.ArchitectDebugTraceEnabled);

            // Last-active tab — Databank-only path; Logic is the default.
            if (string.Equals(cfg.ArchitectLastActiveTab, "databank", System.StringComparison.OrdinalIgnoreCase))
            {
                TopTabs.SelectTab(ArchitectTab.Databank);
            }

            // Last-opened .phxg recall was removed — Architect now
            // always starts at the welcome screen. CliBootstrap's `--open
            // <path>` is still honoured (handled upstream in InitializeAsync),
            // so explicit user intent still routes through. The recent-files
            // MRU (File → Recent) covers the "re-open what I had" use case
            // without ambushing the launch with a file the user didn't ask
            // for.
        }
        catch (System.Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "Architect.MainView", "ApplyPersistedLayoutAndState", ex);
        }
    }

    /// <summary>
    /// 0.10.0 — File → Restore previous version… handler.
    /// Lists every <c>.phxg.bak[1-3]</c> beside the active LoadedFilePath,
    /// shows them in a ContentDialog with timestamps, and restores the
    /// pick (rotating the chain so the current state slides into bak1 first).
    /// No-op when no file is loaded; logs when no backups exist.
    /// </summary>
    public async Task RestoreFromBackupAsync()
    {
        if (string.IsNullOrEmpty(_viewModel.LoadedFilePath))
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                "Restore previous version: no file is loaded (save once to seed backups).",
                "Architect.MainView", Phoenix.Controls.Shared.Models.LogLevel.System);
            return;
        }
        var backups = Phoenix.Controls.Architect.Core.GraphSerializer.ListBackups(_viewModel.LoadedFilePath!);
        if (backups.Count == 0)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                $"Restore previous version: no .phxg.bak[1-3] entries beside '{Path.GetFileName(_viewModel.LoadedFilePath)}' yet.",
                "Architect.MainView", Phoenix.Controls.Shared.Models.LogLevel.System);
            return;
        }

        // Build the picker dialog inline — keeps the surface contained in
        // this one path; a dedicated RestoreBackupDialog can be carved out
        // later if other surfaces need it.
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            Margin = new Thickness(0, 8, 0, 0),
            MinWidth = 360,
        };
        foreach (var b in backups)
        {
            var label = $"bak{b.Slot} — {b.LastWriteUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            var item = new ListViewItem
            {
                Content = label,
                Tag = b,
                // "MonoFont" is an <x:String> resource — cast to
                // FontFamily throws; build from the string like the XAML converter.
                FontFamily = Application.Current.Resources["MonoFont"] is string monoFamily
                             ? new Microsoft.UI.Xaml.Media.FontFamily(monoFamily)
                             : new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 12,
            };
            list.Items.Add(item);
        }
        if (list.Items.Count > 0) list.SelectedIndex = 0;

        var dlg = new ContentDialog
        {
            Title = $"Restore '{Path.GetFileName(_viewModel.LoadedFilePath)}' from backup",
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            Content = list,
        };
        var result = await dlg.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        if (list.SelectedItem is not ListViewItem picked
            || picked.Tag is not Phoenix.Controls.Architect.Core.GraphSerializer.BackupCandidate b2)
        {
            return;
        }

        // Restore + reload via the regular Open path so the AVM-shared undo
        // stack resets cleanly and the dirty marker clears.
        bool ok = Phoenix.Controls.Architect.Core.GraphSerializer.RestoreBackup(
            _viewModel.LoadedFilePath!, b2.Path);
        if (!ok)
        {
            SetStatus($"Restore from '{Path.GetFileName(b2.Path)}' failed — see System Log.",
                ArchitectStatusLight.Red);
            return;
        }
        var reloaded = await _viewModel.OpenAsync(_viewModel.LoadedFilePath!);
        if (reloaded)
        {
            SetStatus($"Restored from {Path.GetFileName(b2.Path)} (bak{b2.Slot}).",
                ArchitectStatusLight.Green);
        }
        else
        {
            SetStatus($"Restored '{Path.GetFileName(b2.Path)}' but reload failed — see System Log.",
                ArchitectStatusLight.Yellow);
        }
    }

    // Clip the canvas pane to its column so node geometry that drifts past
    // the rail edge (negative world-x, oversized macros) cannot overpaint
    // the LeftRail. LeftRail's Canvas.ZIndex=2 puts
    // it above this border in render order; the clip is defence-in-depth so
    // the rail can never lose pixels even if a future feature lifts the rail
    // back into the canvas's z-stratum.
    //
    // Pre-cache, every SizeChanged
    // allocated a fresh RectangleGeometry. SizeChanged fires at ~60 Hz
    // during window drag/snap, so the GC saw a steady drip of throwaway
    // geometry. Reuse one per pane and mutate its Rect in place.
    private RectangleGeometry? _canvasPaneClip;
    private void OnCanvasPaneHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        var rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        if (_canvasPaneClip is null)
        {
            _canvasPaneClip = new RectangleGeometry { Rect = rect };
            fe.Clip = _canvasPaneClip;
        }
        else
        {
            _canvasPaneClip.Rect = rect;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ArchitectViewModel.LoadedFilePath)
            || e.PropertyName == nameof(ArchitectViewModel.IsDirty))
        {
            UpdateChromeFilename();
            ShellStateChanged?.Invoke(this, System.EventArgs.Empty);
            if (e.PropertyName == nameof(ArchitectViewModel.LoadedFilePath))
                SchedulePersistLayout();
        }
        if (e.PropertyName == nameof(ArchitectViewModel.LiveDebugEnabled))
        {
            // 0.10.0 — persist + reflect on the chrome toggle. ApplyPersistedLayoutAndState
            // also sets the chrome's IsChecked, but the user can flip it
            // from elsewhere (keyboard chord, MenuBar click) so keep both
            // surfaces in sync.
            Chrome.SetLiveDebugChecked(_viewModel.LiveDebugEnabled);
            SchedulePersistLayout();
        }
        if (e.PropertyName == nameof(ArchitectViewModel.BusLiveDebugDropVisible))
        {
            // Code-behind binding so the InfoBar tracks the AVM flag without
            // pulling a converter into the XAML.
            if (LiveDebugDropInfoBar is not null)
                LiveDebugDropInfoBar.IsOpen = _viewModel.BusLiveDebugDropVisible;
        }
    }

    /// <summary>
    /// 0.10.0 — persist ShowGrid changes. Wired in the
    /// ctor below to LogicCanvas.PropertyChanged so the toggle survives
    /// whether the user flipped it via the menu (which mutates through
    /// ToggleShowGrid) or via a future keyboard chord.
    /// </summary>
    private void OnCanvasVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Phoenix.Controls.Architect.WinUI.Canvas.LogicCanvasViewModel.ShowGrid))
        {
            Chrome.SetShowGridChecked(_viewModel.LogicCanvas.ShowGrid);
            SchedulePersistLayout();
        }
        // reflect canvas zoom into the status badge.
        else if (e.PropertyName == nameof(Phoenix.Controls.Architect.WinUI.Canvas.LogicCanvasViewModel.Zoom))
        {
            RefreshZoomBadge();
        }
    }

    // Update the status-bar zoom-% badge from the
    // canvas VM. Called on Zoom change + once on load.
    private void RefreshZoomBadge()
    {
        if (ZoomBadge is null) return;
        double z = _viewModel.LogicCanvas?.Zoom ?? 1.0;
        ZoomBadge.Text = z.ToString("P0", System.Globalization.CultureInfo.CurrentCulture);
    }

    private void ShowLoadFailureInfoBar(string filePath, System.Exception ex)
    {
        // GraphSerializer fires from arbitrary threads — bounce onto the UI
        // thread before mutating the bound InfoBar.
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (LoadErrorInfoBar is null) return;
                string fileName = string.IsNullOrEmpty(filePath)
                    ? "(unknown file)"
                    : Path.GetFileName(filePath);
                LoadErrorInfoBar.Title   = $"Couldn't open {fileName}";
                LoadErrorInfoBar.Message = $"{ex.GetType().Name}: {ex.Message}";
                LoadErrorInfoBar.IsOpen  = true;
            }
            catch { /* InfoBar fields can race with MainView teardown; skip silently */ }
        });
    }

    private void UpdateChromeFilename()
    {
        string fname = string.IsNullOrEmpty(_viewModel.LoadedFilePath)
            ? "(unsaved)"
            : Path.GetFileName(_viewModel.LoadedFilePath);
        // Pre-T15 dirty marker — bullet prefix when graph has unsaved edits.
        if (_viewModel.IsDirty) fname = "• " + fname;
        Chrome.FileName = fname;

        // 0.11.x polish — the × glyph hides when the graph is in its clean
        // Welcome-card state (no LoadedFilePath AND the canvas is empty).
        // Otherwise show it so the user can drop back to Welcome without
        // routing through File → New.
        bool hasContent = !string.IsNullOrEmpty(_viewModel.LoadedFilePath)
                       || (_viewModel.LogicCanvas?.Graph?.Nodes?.Count ?? 0) > 0;
        Chrome.SetCloseFileButtonVisible(hasContent);
    }

    /// <summary>
    /// Push a status line into the bottom status bar. Pre-T15 SetStatus parity.
    ///
    /// Optional <paramref name="glyph"/> mirrors the baseline
    /// StatusZone.Glyph (char?): when supplied, the left-zone glyph TextBlock
    /// shows it (icon-font) tinted with the traffic-light colour; when null,
    /// the glyph slot collapses. Existing two-arg callers keep their behaviour
    /// (glyph defaults null → collapsed).
    /// </summary>
    public void SetStatus(string text, ArchitectStatusLight light = ArchitectStatusLight.None, char? glyph = null)
    {
        if (StatusLeft is null) return;
        StatusLeft.Text = text ?? string.Empty;

        // Left-zone glyph. Visibility tracks whether a glyph was
        // supplied; the glyph inherits the traffic-light brush so a red/yellow
        // status reads consistently across the dot + the glyph.
        if (StatusLeftGlyph is not null)
        {
            if (glyph is char g)
            {
                StatusLeftGlyph.Text = g.ToString();
                StatusLeftGlyph.Visibility = Visibility.Visible;
            }
            else
            {
                StatusLeftGlyph.Text = string.Empty;
                StatusLeftGlyph.Visibility = Visibility.Collapsed;
            }
        }
        // Resolve against the canonical Design_Orders traffic-light
        // tokens (StatusGreenBrush #FF5AC878 / StatusYellowBrush #FFE6C85A /
        // StatusRedBrush #FFE66464). Pre-fix this mapped to the older muted
        // OkBrush / WarnBrush / ErrBrush (#6FA46B / #E0A23A / #C9533C), which
        // rendered the status light darker than the baseline WinForms palette
        // and the design intent. RefreshBusStatusDot below is updated in the
        // same pass so both surfaces read from one palette.
        string? brushKey = light switch
        {
            ArchitectStatusLight.Green  => "StatusGreenBrush",
            ArchitectStatusLight.Yellow => "StatusYellowBrush",
            ArchitectStatusLight.Red    => "StatusRedBrush",
            _                           => null,
        };
        if (brushKey is not null
            && Application.Current?.Resources is { } res
            && res.TryGetValue(brushKey, out var resource)
            && resource is Microsoft.UI.Xaml.Media.Brush b)
        {
            StatusLight.Fill = b;
            // Tint the glyph with the same traffic-light brush so a
            // glyph-bearing status reads coherently with the dot.
            if (StatusLeftGlyph is not null && glyph is not null) StatusLeftGlyph.Foreground = b;
        }
        else
        {
            // Neutral fallback when no palette resource is in play.
            StatusLight.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DimGray);
        }
    }

    /// <summary>
    /// Center-zone glyph setter — the baseline supported a glyph in
    /// the center zone independent of the left-zone status line. Pass null to
    /// collapse it. Kept separate from <see cref="SetStatus"/> so callers that
    /// only want a center decoration (e.g. a debug-mode badge glyph) don't
    /// disturb the left status line.
    /// </summary>
    public void SetCenterZoneGlyph(char? glyph, ArchitectStatusLight tint = ArchitectStatusLight.None)
    {
        if (StatusCenterGlyph is null) return;
        if (glyph is char g)
        {
            StatusCenterGlyph.Text = g.ToString();
            StatusCenterGlyph.Visibility = Visibility.Visible;
            string? tintKey = tint switch
            {
                ArchitectStatusLight.Green  => "StatusGreenBrush",
                ArchitectStatusLight.Yellow => "StatusYellowBrush",
                ArchitectStatusLight.Red    => "StatusRedBrush",
                _                           => null,
            };
            if (tintKey is not null
                && Application.Current?.Resources is { } res
                && res.TryGetValue(tintKey, out var resource)
                && resource is Microsoft.UI.Xaml.Media.Brush b)
            {
                StatusCenterGlyph.Foreground = b;
            }
        }
        else
        {
            StatusCenterGlyph.Text = string.Empty;
            StatusCenterGlyph.Visibility = Visibility.Collapsed;
        }
    }

    // ─── Status-zone hover tooltips ─────────────────────────────────────────
    //
    // Done-differently from the WinForms baseline's OnMouseMove hit-testing:
    // instead of a manual ZoneAt(Point) loop over StatusZone rectangles, each
    // zone container carries a tooltip-resolver delegate and we set
    // ToolTipService.SetToolTip on PointerEntered so the content is resolved
    // lazily at hover time (the same lazy-resolve pattern RefreshBusStatusDot
    // uses for the bus dot). A null/empty resolver result clears the tooltip.

    private Func<string?>? _statusLeftTooltipResolver;
    private Func<string?>? _statusCenterTooltipResolver;
    private Func<string?>? _statusRightTooltipResolver;
    private bool _statusZoneHoverHooked;

    /// <summary>
    /// Register the dynamic tooltip resolver for a status zone.
    /// Resolvers run at hover time so the surfaced text reflects current state
    /// (node counts, debug mode, last save, etc.). Passing null removes the
    /// resolver and clears any tooltip on that zone.
    /// </summary>
    public void SetStatusZoneTooltip(ArchitectStatusZone zone, Func<string?>? resolver)
    {
        EnsureStatusZoneHoverHooked();
        switch (zone)
        {
            case ArchitectStatusZone.Left:   _statusLeftTooltipResolver   = resolver; break;
            case ArchitectStatusZone.Center: _statusCenterTooltipResolver = resolver; break;
            case ArchitectStatusZone.Right:  _statusRightTooltipResolver  = resolver; break;
        }
        if (resolver is null) ClearStatusZoneTooltip(zone);
    }

    private void EnsureStatusZoneHoverHooked()
    {
        if (_statusZoneHoverHooked) return;
        // The right zone is a StackPanel parent of ZoomBadge / BusStatusDot /
        // StatusRight; hovering anywhere in it resolves the right-zone tooltip.
        if (StatusLeftZone is not null)   StatusLeftZone.PointerEntered   += OnStatusZonePointerEntered;
        if (StatusCenterZone is not null) StatusCenterZone.PointerEntered += OnStatusZonePointerEntered;
        if (StatusRight is not null
            && StatusRight.Parent is FrameworkElement rightParent)
        {
            rightParent.PointerEntered += OnStatusZonePointerEntered;
        }
        _statusZoneHoverHooked = true;
    }

    private void OnStatusZonePointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        try
        {
            if (ReferenceEquals(sender, StatusLeftZone))
                ApplyStatusZoneTooltip(StatusLeftZone, _statusLeftTooltipResolver);
            else if (ReferenceEquals(sender, StatusCenterZone))
                ApplyStatusZoneTooltip(StatusCenterZone, _statusCenterTooltipResolver);
            else if (sender is FrameworkElement fe)
                ApplyStatusZoneTooltip(fe, _statusRightTooltipResolver);
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error("Architect",
                "Status-zone tooltip resolve", ex);
        }
    }

    private static void ApplyStatusZoneTooltip(FrameworkElement target, Func<string?>? resolver)
    {
        if (target is null) return;
        string? content = resolver?.Invoke();
        if (string.IsNullOrEmpty(content))
            ToolTipService.SetToolTip(target, null);
        else
            ToolTipService.SetToolTip(target, content);
    }

    private void ClearStatusZoneTooltip(ArchitectStatusZone zone)
    {
        FrameworkElement? target = zone switch
        {
            ArchitectStatusZone.Left   => StatusLeftZone,
            ArchitectStatusZone.Center => StatusCenterZone,
            ArchitectStatusZone.Right  => StatusRight?.Parent as FrameworkElement,
            _                          => null,
        };
        if (target is not null) ToolTipService.SetToolTip(target, null);
    }

    /// <summary>
    /// Seed the three status zones with live tooltip resolvers so the
    /// capability is wired end-to-end (not just plumbing). Left → the current
    /// status line + loaded file; Center → live-debug state; Right → node/link
    /// counts breakdown. Resolvers run at hover so they always reflect current
    /// state. Callers can override any zone via SetStatusZoneTooltip.
    /// </summary>
    private void RegisterStatusZoneTooltips()
    {
        SetStatusZoneTooltip(ArchitectStatusZone.Left, () =>
        {
            string status = StatusLeft?.Text ?? string.Empty;
            string file = string.IsNullOrEmpty(_viewModel.LoadedFilePath)
                ? "(unsaved graph)"
                : _viewModel.LoadedFilePath!;
            return string.IsNullOrEmpty(status) ? file : $"{status}\n{file}";
        });
        SetStatusZoneTooltip(ArchitectStatusZone.Center, () =>
            _viewModel.LiveDebugEnabled
                ? "Live Debug Trace is on — nodes pulse as Hub executes them."
                : "Live Debug Trace is off (View → Live Debug Trace / Ctrl+Shift+D).");
        SetStatusZoneTooltip(ArchitectStatusZone.Right, () =>
        {
            try
            {
                var g = _viewModel.LogicCanvas.Graph;
                return $"{g.Nodes.Count} node(s) · {g.Links.Count} link(s) · "
                     + $"{g.Frames.Count} frame(s) · {g.Macros.Count} macro(s)";
            }
            catch { return null; }
        });
    }

    private void RefreshStatusCounts()
    {
        if (StatusRight is null) return;
        try
        {
            var g = _viewModel.LogicCanvas.Graph;
            StatusRight.Text = $"{g.Nodes.Count} nodes · {g.Links.Count} links";
            StatusCenter.Text = _viewModel.LiveDebugEnabled ? "Debug Mode active" : string.Empty;
        }
        catch
        {
            StatusRight.Text = string.Empty;
        }
    }

    private void OnCanvasGraphMutatedForStatus(object? sender, EventArgs e) => RefreshStatusCounts();

    // Per-MainView signature-hash cache feeding MacroSyncHashGate so a
    // MACRO_SYNC broadcast whose macro signatures are unchanged skips the
    // Call-node rebuild (the cascade is correct-but-wasteful when nothing
    // changed — the inner refresh is diff-based + idempotent).
    private readonly Dictionary<string, ulong> _macroSyncHashCache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Rebuild the data sockets of every Macro.Call node whose MacroId
    /// is in <paramref name="ids"/> to match the macro's current Entry/Exit
    /// signature, preserving wires by socket name. Gated through
    /// <see cref="Phoenix.Controls.Architect.Core.MacroSyncHashGate"/> so an
    /// unchanged signature is a no-op.
    ///
    /// Signature source: the LOCAL graph's <c>Macros</c> list. A Hub
    /// MACRO_SYNC re-broadcast updates that list (macros referenced in this
    /// graph round-trip the canonical definition); we refresh the Call nodes
    /// to whatever signature the macro now carries. Self-contained here rather
    /// than calling a canvas method (the WinForms RefreshMacroCallSockets has
    /// no WinUI equivalent).
    /// </summary>
    private void RefreshMacroCallNodesForSyncedMacros(System.Collections.Generic.IReadOnlyCollection<string>? ids)
    {
        if (ids is null || ids.Count == 0) return;
        try
        {
            var graph = _viewModel.LogicCanvas.Graph;
            if (graph?.Macros is null || graph.Macros.Count == 0) return;

            var idSet = new System.Collections.Generic.HashSet<string>(ids, StringComparer.Ordinal);
            var candidates = new System.Collections.Generic.List<Phoenix.Controls.Shared.Models.Macro>();
            foreach (var m in graph.Macros)
            {
                if (m is null || string.IsNullOrEmpty(m.MacroId)) continue;
                if (idSet.Contains(m.MacroId)) candidates.Add(m);
            }
            if (candidates.Count == 0) return;

            bool anyChanged = false;
            int refreshed = Phoenix.Controls.Architect.Core.MacroSyncHashGate.Apply(
                candidates,
                _macroSyncHashCache,
                macro =>
                {
                    if (RebuildCallNodesForMacro(graph, macro)) anyChanged = true;
                });

            if (refreshed > 0 && anyChanged)
            {
                // Re-render the node/link VM collections so the rebuilt sockets
                // + preserved wires repaint. wildcardAlreadyResolved:false so
                // the cascade re-runs over the mutated graph (idempotent).
                _viewModel.LogicCanvas.LoadGraph(graph, wildcardAlreadyResolved: false);
                RefreshStatusCounts();
            }
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error("Architect.MainView",
                "RefreshMacroCallNodesForSyncedMacros", ex);
        }
    }

    /// <summary>
    /// Rebuild every Macro.Call node bound to <paramref name="macro"/>
    /// (via its <c>MacroId</c> attribute) so its data sockets mirror the
    /// macro's Entry (outputs → Call inputs) and Exit (inputs → Call outputs)
    /// signature. Wires are preserved by socket NAME: a link to a socket whose
    /// name survives the rebuild is re-pointed at the new socket id; links to
    /// dropped sockets are removed (those params no longer exist). The bare
    /// Flow in/out pair is always kept. Returns true when the node set changed.
    /// </summary>
    private static bool RebuildCallNodesForMacro(
        Phoenix.Controls.Shared.Models.Graph graph,
        Phoenix.Controls.Shared.Models.Macro macro)
    {
        if (graph?.Nodes is null || macro is null) return false;

        // Resolve the macro's promoted param signature from its Entry/Exit
        // markers (output sockets of Macro.Entry → the Call node's data INPUTS;
        // input sockets of Macro.Exit → the Call node's data OUTPUTS). Skip the
        // canonical Flow socket and any inactive placeholders.
        var entry = macro.Graph?.Nodes?.Find(n => n.Title == "Macro.Entry");
        var exit  = macro.Graph?.Nodes?.Find(n => n.Title == "Macro.Exit");

        var inParams = new System.Collections.Generic.List<(string Name, Phoenix.Controls.Shared.Models.SocketDataType Dt)>();
        if (entry?.Sockets is not null)
        {
            foreach (var s in entry.Sockets)
            {
                if (s.Type != Phoenix.Controls.Shared.Models.SocketType.Output) continue;
                if (s.IsPlaceholder || s.Name == "Flow") continue;
                inParams.Add((s.Name, s.DataType));
            }
        }
        var outParams = new System.Collections.Generic.List<(string Name, Phoenix.Controls.Shared.Models.SocketDataType Dt)>();
        if (exit?.Sockets is not null)
        {
            foreach (var s in exit.Sockets)
            {
                if (s.Type != Phoenix.Controls.Shared.Models.SocketType.Input) continue;
                if (s.IsPlaceholder || s.Name == "Flow") continue;
                outParams.Add((s.Name, s.DataType));
            }
        }

        bool changed = false;
        const int rowH = 22;
        const int headerH = 30;

        foreach (var node in graph.Nodes)
        {
            if (node.Title != "Macro.Call") continue;
            if (node.Attributes is null) continue;
            if (!node.Attributes.TryGetValue("MacroId", out var mid) || mid != macro.MacroId) continue;

            // Capture the surviving wires by (socketName, isInput) before we
            // mutate the socket list, so we can re-point them at the new ids.
            // Map oldSocketId → (name, isInput) for this node's data sockets.
            var oldDataSockets = node.Sockets
                .FindAll(s => s.Name != "Flow" && !s.IsPlaceholder);

            // Keep the Flow in/out pair untouched.
            var flowIn  = node.Sockets.Find(s => s.Name == "Flow" && s.Type == Phoenix.Controls.Shared.Models.SocketType.Input);
            var flowOut = node.Sockets.Find(s => s.Name == "Flow" && s.Type == Phoenix.Controls.Shared.Models.SocketType.Output);

            // Build the new data sockets.
            var rebuilt = new System.Collections.Generic.List<Phoenix.Controls.Shared.Models.Socket>();
            if (flowIn  is not null) rebuilt.Add(flowIn);
            if (flowOut is not null) rebuilt.Add(flowOut);

            int width = node.Size.Width > 0 ? node.Size.Width : 200;
            int row = 1;
            // name → new socket id, keyed per direction so we can re-point wires.
            var newInputIdByName  = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
            var newOutputIdByName = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var (name, dt) in inParams)
            {
                var sock = new Phoenix.Controls.Shared.Models.Socket
                {
                    Name = name,
                    Type = Phoenix.Controls.Shared.Models.SocketType.Input,
                    DataType = dt,
                    Color = Phoenix.Controls.Architect.Core.NodeRegistry.ColorFromDataType(dt),
                    Offset = new System.Drawing.Point(-6, headerH + row * rowH),
                };
                rebuilt.Add(sock);
                newInputIdByName[name] = sock.Id;
                row++;
            }
            row = 1;
            foreach (var (name, dt) in outParams)
            {
                var sock = new Phoenix.Controls.Shared.Models.Socket
                {
                    Name = name,
                    Type = Phoenix.Controls.Shared.Models.SocketType.Output,
                    DataType = dt,
                    Color = Phoenix.Controls.Architect.Core.NodeRegistry.ColorFromDataType(dt),
                    Offset = new System.Drawing.Point(width - 14, headerH + row * rowH),
                };
                rebuilt.Add(sock);
                newOutputIdByName[name] = sock.Id;
                row++;
            }

            // Re-point or drop the wires that referenced the OLD data sockets.
            foreach (var old in oldDataSockets)
            {
                bool isInput = old.Type == Phoenix.Controls.Shared.Models.SocketType.Input;
                string? newId = isInput
                    ? (newInputIdByName.TryGetValue(old.Name, out var i) ? i : null)
                    : (newOutputIdByName.TryGetValue(old.Name, out var o) ? o : null);

                for (int li = graph.Links.Count - 1; li >= 0; li--)
                {
                    var link = graph.Links[li];
                    bool touchesAsFrom = link.FromNodeId == node.Id && link.FromSocketId == old.Id;
                    bool touchesAsTo   = link.ToNodeId   == node.Id && link.ToSocketId   == old.Id;
                    if (!touchesAsFrom && !touchesAsTo) continue;

                    if (newId is null)
                    {
                        // Param removed by the peer — drop the now-dangling wire.
                        graph.Links.RemoveAt(li);
                        changed = true;
                    }
                    else
                    {
                        if (touchesAsFrom) link.FromSocketId = newId;
                        if (touchesAsTo)   link.ToSocketId   = newId;
                        changed = true;
                    }
                }
            }

            // Swap the node's socket list if it actually differs.
            bool socketsDiffer =
                rebuilt.Count != node.Sockets.Count
                || !SocketListsEquivalent(node.Sockets, rebuilt);
            if (socketsDiffer)
            {
                node.Sockets.Clear();
                node.Sockets.AddRange(rebuilt);
                int desiredHeight = headerH + System.Math.Max(inParams.Count, outParams.Count) * rowH + rowH;
                if (desiredHeight > node.Size.Height)
                    node.Size = new System.Drawing.Size(width, desiredHeight);
                changed = true;
            }
        }

        return changed;
    }

    private static bool SocketListsEquivalent(
        System.Collections.Generic.List<Phoenix.Controls.Shared.Models.Socket> a,
        System.Collections.Generic.List<Phoenix.Controls.Shared.Models.Socket> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Name != b[i].Name) return false;
            if (a[i].Type != b[i].Type) return false;
            if (a[i].DataType != b[i].DataType) return false;
        }
        return true;
    }

    /// <summary>
    /// Bus-connection chrome indicator. Repaints <see cref="BusStatusDot"/>
    /// with the OkBrush (connected) or ErrBrush (disconnected) and updates
    /// the tooltip so a hover tells the streamer *why* live-debug isn't
    /// flowing. Marshals to the UI thread because
    /// <see cref="Phoenix.Controls.Architect.Core.ArchitectBusClient.OnConnectionStatusChanged"/>
    /// fires from the connect-loop background task.
    /// </summary>
    private void OnBusConnectionStatusChanged(bool connected)
    {
        if (DispatcherQueue is null) { RefreshBusStatusDot(connected); return; }
        DispatcherQueue.TryEnqueue(() => RefreshBusStatusDot(connected));
    }

    /// <summary>
    /// Three-state companion to <see cref="OnBusConnectionStatusChanged"/>.
    /// Paints the dot yellow during the reconnect backoff window so users
    /// don't think Hub is down when the loop is mid-retry. Also forwards the
    /// state to the AVM so the live-debug InfoBar's
    /// visibility tracks the same signal.
    /// </summary>
    private void OnBusConnectionStateChanged(Phoenix.Controls.Architect.Core.BusConnectionState state)
    {
        if (DispatcherQueue is null)
        {
            RefreshBusStatusDot(state);
            _viewModel.UpdateBusConnectionState(state);
            return;
        }
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshBusStatusDot(state);
            _viewModel.UpdateBusConnectionState(state);
        });
    }

    /// <summary>
    /// Retry click for the LiveDebugDropInfoBar — routes through the AVM
    /// which stops + restarts the bus client when live-debug is still
    /// enabled. Logs a no-op when the user already turned live-debug off
    /// (the InfoBar would have closed but Retry might race).
    /// </summary>
    private void OnLiveDebugRetryClicked(object sender, RoutedEventArgs e)
        => _viewModel.RetryBusConnection();

    private void RefreshBusStatusDot(bool connected)
        => RefreshBusStatusDot(connected
            ? Phoenix.Controls.Architect.Core.BusConnectionState.Connected
            : Phoenix.Controls.Architect.Core.BusConnectionState.Disconnected);

    private void RefreshBusStatusDot(Phoenix.Controls.Architect.Core.BusConnectionState state)
    {
        try
        {
            if (BusStatusDot is null) return;
            // Three-state — pre-fix the dot flipped red the instant a socket
            // dropped and stayed red for the entire 5s backoff window, so
            // users assumed Hub was down even when the reconnect loop was
            // already mid-retry.
            // Canonical traffic-light tokens (StatusGreen/Yellow/Red),
            // matching SetStatus above so the status light + bus dot share the
            // one Design_Orders palette rather than the muted Ok/Warn/Err set.
            (string brushKey, string tooltip) = state switch
            {
                Phoenix.Controls.Architect.Core.BusConnectionState.Connected
                    => ("StatusGreenBrush",  "Hub bus connected"),
                Phoenix.Controls.Architect.Core.BusConnectionState.Reconnecting
                    => ("StatusYellowBrush", "Hub bus reconnecting…"),
                _   => ("StatusRedBrush",    "Hub bus disconnected"),
            };
            if (Application.Current?.Resources is { } res
                && res.TryGetValue(brushKey, out var resource)
                && resource is Microsoft.UI.Xaml.Media.Brush b)
            {
                BusStatusDot.Fill = b;
            }
            // Append the last bus-client error so the streamer hovering the
            // dot sees the underlying reason (ECONNREFUSED, handshake reject,
            // etc.) instead of just "disconnected". Connected state clears
            // LastErrorMessage on the client side so this stays a single
            // line during normal operation.
            string? lastErr = Phoenix.Controls.Architect.Core.ArchitectBusClient.Instance.LastErrorMessage;
            if (state != Phoenix.Controls.Architect.Core.BusConnectionState.Connected
                && !string.IsNullOrWhiteSpace(lastErr))
            {
                tooltip = tooltip + "\n" + lastErr;
            }
            ToolTipService.SetToolTip(BusStatusDot, tooltip);
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error("Architect",
                "RefreshBusStatusDot", ex);
        }
    }

    // ─── File pickers ────────────────────────────────────────────────────

    // 0.10.0 multi-window restoration — File → New / Open / Open Recent
    // never clobber a POPULATED canvas; each spawns a sibling Architect
    // window via ArchitectWindowRegistry.
    //
    // But when the embedded canvas is BLANK (no file, no
    // nodes — i.e. the Welcome card is up), spawning a sibling just leaves THIS
    // window stuck on the Welcome card with an unused empty sibling beside it
    // ("clicking Create New only opens a new sub-window where the same issue
    // continues"). So a blank canvas starts the new graph IN PLACE — the same
    // "fill the blank default window when empty" idiom File → Open already uses
    // (the embeddedIsBlank branch in OnFileOpenRequestedAsync). A populated
    // canvas still spawns a sibling, so the multi-window model is preserved.
    // ArchitectWindowRegistry dedups by absolute path so File → Open of an
    // already-open .phxg focuses the existing sibling rather than spawning
    // a duplicate.
    private void OnFileNewRequested()
    {
        if (!HasOpenDocument)
        {
            // Blank canvas → fresh graph in place. NewGraph() wipes
            // pan/zoom/selection/undo (it's the same reset File → Open uses);
            // BeginBlankCanvasFromNew hides the Welcome card so the welcoming
            // message "gives way to the canvas" instead of re-rendering.
            _viewModel.NewGraph();
            _logicCanvasView.BeginBlankCanvasFromNew();
            SetStatus("New graph.", ArchitectStatusLight.Green);
            return;
        }
        ArchitectWindowRegistry.OpenNew();
    }

    /// <summary>
    /// 0.11.x polish — close-X glyph next to the chrome filename. Clears the
    /// active graph back to the Welcome card on the same window (does NOT
    /// spawn a sibling, unlike File → New). Honours the dirty-prompt path
    /// so unsaved work isn't dropped silently.
    /// </summary>
    private async void OnChromeFileCloseRequested(object? sender, EventArgs e)
        => await CloseCurrentDocumentAsync();

    /// <summary>
    /// 0.11.x polish — public entry-point for "close the active graph"
    /// surfaced so Hub's HubChrome close-X (which dispatches through
    /// MainWindow) can reach the same dirty-prompt + NewGraph sequence
    /// the in-pillar ArchitectChrome close-X uses. Pre-fix only the
    /// per-pillar ArchitectChrome had the affordance — the Hub-hosted
    /// MainView hides ArchitectChrome (Chrome.IsHitTestVisible=false,
    /// empty Clip) so the close-X was missing entirely from the suite's
    /// single user-facing entry point. Faults log to GlobalLogger and
    /// the task completes; never throws.
    /// </summary>
    public async System.Threading.Tasks.Task CloseCurrentDocumentAsync()
    {
        try
        {
            if (!await PromptSaveBeforeCloseAsync()) return;
            _viewModel.NewGraph();
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "Architect.MainView", "CloseCurrentDocumentAsync", ex);
        }
    }

    /// <summary>
    /// 0.11.x polish — true when there's a real document to close
    /// (LoadedFilePath is set OR the canvas has authored nodes). The
    /// Welcome card is "no document" — closing while on it would be a
    /// no-op. Used by MainWindow to gate HubChrome's close-X visibility.
    /// </summary>
    public bool HasOpenDocument
        => !string.IsNullOrEmpty(_viewModel.LoadedFilePath)
        || (_viewModel.LogicCanvas?.Graph?.Nodes?.Count ?? 0) > 0;

    private async Task OnFileOpenRequestedAsync()
    {
        // CustomFilePicker (IFileOpenDialog COM interop) is used here instead
        // of WinUI 3's FileOpenPicker so the picker can open at the project's
        // data/logic/ folder by default — FileOpenPicker.SuggestedStartLocation
        // is enum-only and can't take a custom path. Per-pillar last-used
        // folder seed: if AppConfig has a recent
        // dir for Architect, prefer it over Paths.HubLogic.
        var hwnd = WindowNative.GetWindowHandle(_hostWindow);
        string startDir = ResolveStartDir(
            Phoenix.Controls.Shared.Services.ConfigManager.Current.LastArchitectOpenDir,
            Paths.HubLogic);
        var path = CustomFilePicker.PickSingleFile(
            hwnd,
            startDir,
            new[] { ("Phoenix Graph", ".phxg") });
        if (!string.IsNullOrEmpty(path))
        {
            // First open fills the blank
            // default window; subsequent opens spawn siblings. Pre-fix
            // every File → Open spawned a brand-new sibling even when the
            // embedded MainView's own canvas was empty, leaving the
            // user staring at an unused empty Architect tab while their
            // chosen .phxg appeared in a separate window. The pre-WinUI
            // shell did "load into the active form when empty"; this
            // restores that idiom for the embedded view.
            //
            // Detection: _viewModel.LoadedFilePath==null AND the graph
            // has no nodes. Either condition alone is insufficient — a
            // brand-new untitled graph the user has already started
            // editing has LoadedFilePath==null but real nodes, and a
            // freshly-loaded then cleared graph would have nodes
            // momentarily zero but LoadedFilePath set.
            bool embeddedIsBlank =
                string.IsNullOrEmpty(_viewModel.LoadedFilePath)
                && _viewModel.LogicCanvas.Graph.Nodes.Count == 0;

            if (embeddedIsBlank)
            {
                bool ok = await _viewModel.OpenAsync(path);
                if (!ok)
                {
                    SetStatus(
                        $"Failed to load {Path.GetFileName(path)} — file unreadable or malformed. See System Log.",
                        ArchitectStatusLight.Red);
                }
                else
                {
                    SetStatus(
                        $"Opened {Path.GetFileName(path)}.",
                        ArchitectStatusLight.Green);
                }
                RememberArchitectDir(path);
                return;
            }

            // Default window is in use — spawn a sibling Architect window.
            // The registry handles dedup-by-path so re-opening an already-
            // open .phxg focuses the existing sibling.
            // Await the async load so the deserialize + cascade
            // doesn't run on a fire-and-forget continuation.
            //
            // When OpenFileAsync returns null the
            // sibling window factory closed the empty shell because the
            // ArchitectViewModel.OpenAsync parse-failure gate refused the
            // load. Surface that as a status line on the embedded view so
            // the user sees WHY their open click did nothing visible — the
            // sibling window vanishing before it ever displayed was the
            // "Loading .phxg in Architect does not work" symptom Majo
            // flagged.
            var spawned = await ArchitectWindowRegistry.OpenFileAsync(path);
            if (spawned is null)
            {
                SetStatus(
                    $"Failed to load {Path.GetFileName(path)} — file unreadable or malformed. See System Log.",
                    ArchitectStatusLight.Red);
            }
            RememberArchitectDir(path);
        }
    }

    // Last-used folder per pillar — read on picker construction, write on
    // successful pick. Existence-checked because
    // a renamed/deleted recent folder shouldn't strand the picker.
    private static string ResolveStartDir(string? recall, string fallback)
        => !string.IsNullOrWhiteSpace(recall)
           && System.IO.Directory.Exists(recall)
            ? recall
            : fallback;

    private static void RememberArchitectDir(string filePath)
    {
        try
        {
            string dir = Path.GetDirectoryName(filePath) ?? string.Empty;
            if (string.IsNullOrEmpty(dir)) return;
            var cfg = Phoenix.Controls.Shared.Services.ConfigManager.Current;
            if (string.Equals(cfg.LastArchitectOpenDir, dir, System.StringComparison.OrdinalIgnoreCase)) return;
            cfg.LastArchitectOpenDir = dir;
            // Deferred write — open/save already does disk work;
            // don't add a synchronous config write to the UI thread on top.
            Phoenix.Controls.Shared.Services.ConfigManager.SaveDeferred(Paths.AppConfigJson);
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error("Architect", "RememberArchitectDir", ex);
        }
    }

    /// <summary>
    /// Run GraphValidator before save. Errors → modal with "Save anyway / Cancel"
    /// (a deliberate decision moment for a genuinely broken graph). Warnings
    /// only → no modal at all: every warning is logged via GlobalLogger and
    /// the status bar turns yellow with a "Saved with N warnings — see
    /// System Log" message so the user can review without being blocked.
    ///
    /// The prior implementation popped the modal
    /// on ANY warnings, which fired on virtually every save during normal
    /// work-in-progress editing. That violated the
    /// no-modal-dialogs-for-repeatable-rejections rule directly.
    /// </summary>
    private async Task<bool> ConfirmSaveValidationAsync()
    {
        try
        {
            var results = Phoenix.Controls.Architect.Core.GraphValidator.Validate(_viewModel.LogicCanvas.Graph);
            if (results.Count == 0) return true;

            int errorCount   = 0;
            int warningCount = 0;
            foreach (var v in results)
            {
                bool isErr = v.Severity == Phoenix.Controls.Architect.Core.ValidationSeverity.Error;
                if (isErr) errorCount++; else warningCount++;
                // Every result lands in System Log regardless of severity so
                // the user can review without re-opening the dialog.
                Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                    v.ToString(),
                    "Architect.SaveValidator",
                    isErr
                        ? Phoenix.Controls.Shared.Models.LogLevel.CriticalError
                        : Phoenix.Controls.Shared.Models.LogLevel.System);
            }

            // Errors are blocking — keep the modal for the genuine decision case.
            // Dialog button contract: Primary="Save anyway",
            // Secondary="Cancel" (matches WinUI convention — Primary = the
            // affirmative action). Returning true here means "proceed with save".
            if (errorCount > 0)
            {
                var dlg = Phoenix.Controls.Architect.WinUI.Dialogs.SaveValidationDialog.ForResults(XamlRoot, results);
                var result = await dlg.ShowAsync();
                if (result != ContentDialogResult.Primary) return false;
                // User chose Save Anyway — fall through. Status bar marker below
                // still flips yellow so they remember the graph wasn't clean.
            }

            // Non-blocking surface for warning-only saves (and the "Save anyway"
            // post-modal case). One yellow status line beats a modal in the hot
            // path — the underlying details are already in System Log.
            string summary = errorCount > 0
                ? $"Saved with {errorCount} error(s) + {warningCount} warning(s) — see System Log."
                : $"Saved with {warningCount} warning(s) — see System Log.";
            SetStatus(summary, ArchitectStatusLight.Yellow);
            return true;
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error("Architect",
                "validation failed; falling through to save", ex);
            return true;
        }
    }

    // Reentrancy guard for the Save command. Mashing Ctrl+S during a
    // validation modal or while the Save-As picker is open used to fan out
    // multiple concurrent SaveAsync calls, racing the file write. The flag is
    // set on entry and cleared at every exit (including the catch) so a second
    // Save while one is in flight is a no-op.
    private bool _savingInProgress;

    private async Task OnFileSaveRequestedAsync()
    {
        // Drop re-entrant Save invocations.
        if (_savingInProgress) return;
        _savingInProgress = true;
        try
        {
            if (!string.IsNullOrEmpty(_viewModel.LoadedFilePath))
            {
                // Known-path branch: no picker, so validation timing is safe
                // up front (no mutation window between gate and write).
                if (!await ConfirmSaveValidationAsync())
                {
                    SetStatus("Save cancelled — validation rejected.", ArchitectStatusLight.Yellow);
                    return;
                }

                // Honour SaveAsync's bool result — false means the
                // disk write failed (disk-full / permission / I/O). Pre-fix the
                // result was ignored and the UI showed green "Saved" + touched
                // RecentFiles even though the graph never hit disk.
                bool savedOk;
                try { savedOk = await _viewModel.SaveAsync(); }
                catch (Exception ex)
                {
                    Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                        "Architect.MainView", "Save (known path)", ex);
                    SetStatus($"Save failed: {ex.GetType().Name} — see System Log.",
                        ArchitectStatusLight.Red);
                    return;
                }
                if (!savedOk)
                {
                    SetStatus("Save failed — could not write the .phxg (disk full / permission / I-O). See System Log.",
                        ArchitectStatusLight.Red);
                    return;
                }
                Phoenix.Controls.Architect.WinUI.Services.RecentFiles.TouchDeferred(_viewModel.LoadedFilePath!);
                DispatcherQueue?.TryEnqueue(RebuildChromeRecentMenu);
                SetStatus($"Saved {Path.GetFileName(_viewModel.LoadedFilePath)}.", ArchitectStatusLight.Green);
                return;
            }

            // No-path-yet branch: open the picker FIRST, then validate, then
            // save. Pre-fix validation ran before the (non-blocking,
            // async) picker, so the user could press DEL / Ctrl+Z / Ctrl+Y
            // while the picker was open and mutate the graph after it had
            // passed validation — persisting an unvalidated structure. Moving
            // the gate to immediately before SaveAsync closes that window.
            var hwnd = WindowNative.GetWindowHandle(_hostWindow);
            string startDir = ResolveStartDir(
                Phoenix.Controls.Shared.Services.ConfigManager.Current.LastArchitectOpenDir,
                Paths.HubLogic);
            var path = CustomFilePicker.PickSaveFile(
                hwnd,
                startDir,
                "graph",
                new[] { ("Phoenix Graph", ".phxg") });
            if (string.IsNullOrEmpty(path))
            {
                // Picker dismissed without picking — surface to System Log so the
                // user has *some* signal the save didn't happen. An InfoBar in
                // MainView is captured as future work in TODO.
                Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                    "Save cancelled — work is still unsaved.",
                    "Architect", Phoenix.Controls.Shared.Models.LogLevel.System);
                return;
            }

            // Validate now (after the picker closed) so any mutation
            // made while the picker was open is caught.
            if (!await ConfirmSaveValidationAsync())
            {
                SetStatus("Save cancelled — validation rejected.", ArchitectStatusLight.Yellow);
                return;
            }

            // Check the SaveAsync result for the Save-As branch too.
            bool savedAsOk;
            try { savedAsOk = await _viewModel.SaveAsync(path); }
            catch (Exception ex)
            {
                Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                    "Architect.MainView", "Save As", ex);
                SetStatus($"Save failed: {ex.GetType().Name} — see System Log.",
                    ArchitectStatusLight.Red);
                return;
            }
            if (!savedAsOk)
            {
                SetStatus("Save failed — could not write the .phxg (disk full / permission / I-O). See System Log.",
                    ArchitectStatusLight.Red);
                return;
            }
            Phoenix.Controls.Architect.WinUI.Services.RecentFiles.TouchDeferred(path);
            DispatcherQueue?.TryEnqueue(RebuildChromeRecentMenu);
            RememberArchitectDir(path);
            SetStatus($"Saved {Path.GetFileName(path)}.", ArchitectStatusLight.Green);
        }
        finally
        {
            _savingInProgress = false;
        }
    }

    private async void OnFileExportRequested()
    {
        if (string.IsNullOrEmpty(_viewModel.LoadedFilePath)) return;
        try
        {
            await _viewModel.ExportPhxBesideAsync(_viewModel.LoadedFilePath!).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "Architect.MainView", "OnFileExportRequested", ex);
        }
    }

    /// <summary>
    /// Boot-time autosave wiring: start the 60s timer + scan for survivors
    /// from a prior crash / forced close. Survivors get logged loudly to
    /// System Log and surfaced on the LoadErrorInfoBar so the user knows
    /// to recover (open the .autosave file manually, or wait for the
    /// dedicated recovery UI).
    /// </summary>
    private void StartAutosaveAndScanForSurvivors()
    {
        if (_autosave is null && DispatcherQueue is not null)
        {
            _autosave = new Phoenix.Controls.Architect.WinUI.Services.AutosaveService(_viewModel, DispatcherQueue);
            // Surface autosave health: a brief
            // status-bar confirmation on success, and a persistent InfoBar after
            // repeated failures so a stuck autosave (read-only path / full disk)
            // isn't silent and the user doesn't trust a stale recovery file.
            _autosave.AutosaveSucceeded += OnAutosaveSucceeded;
            _autosave.AutosaveFailed    += OnAutosaveFailed;
            _autosave.Start();
        }

        // Run the survivor scan (recursive logic-folder enumeration +
        // per-file metadata reads) on the thread pool and marshal only the
        // result back — synchronous, it blocked the first Loaded tick,
        // scaling with the logic-folder size.
        int dismissGen = _autosaveDismissGen;
        _ = Phoenix.Controls.Shared.Core.AsyncErrorBoundary.SafeRunAsync(
            () => System.Threading.Tasks.Task.Run(() =>
            {
                var survivors = Phoenix.Controls.Architect.WinUI.Services.AutosaveScout.Scan();
                DispatcherQueue?.TryEnqueue(() => ApplyAutosaveSurvivorScan(survivors, dismissGen));
            }),
            "Architect.Recovery", "Survivor scan");
    }

    /// <summary>
    /// UI-thread half of the survivor scan — surfaces the InfoBar /
    /// Recover… button from the pre-scanned list.
    /// </summary>
    private void ApplyAutosaveSurvivorScan(
        System.Collections.Generic.IReadOnlyList<Phoenix.Controls.Architect.WinUI.Services.AutosaveScout.Survivor> survivors,
        int dismissGen)
    {
        try
        {
            if (_unloadedOnceRan) return;
            if (survivors.Count == 0)
            {
                _autosaveSurvivors = null;
                if (AutosaveRecoverButton is not null)
                    AutosaveRecoverButton.Visibility = Visibility.Collapsed;
                return;
            }

            foreach (var s in survivors)
            {
                Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                    $"Recovery candidate ({s.Kind}): {s.Path} — last write {s.LastWriteUtc:yyyy-MM-dd HH:mm}Z",
                    "Architect.Recovery",
                    Phoenix.Controls.Shared.Models.LogLevel.CriticalError);
            }

            // Stash the survivor list for the recovery dialog and
            // light up the InfoBar's Recover… button. Pre-fix the InfoBar told
            // the user to "rename a *.phxg.autosave to *.phxg" by hand in
            // Explorer; now the in-app dialog renames + reopens for them.
            _autosaveSurvivors = survivors;

            // A .phxg load can finish before this deferred scan result lands
            // (CLI --open in InitializeAsync); its DismissAutosaveInfoBar
            // wins, matching the pre-deferral order where the load's
            // dismissal ran after the scan. Survivors stay stashed above so
            // the recovery dialog still works if re-invoked.
            if (dismissGen != _autosaveDismissGen) return;

            if (LoadErrorInfoBar is not null)
            {
                LoadErrorInfoBar.Severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning;
                LoadErrorInfoBar.Title   = $"{survivors.Count} autosave / crash survivor(s) found";
                LoadErrorInfoBar.Message =
                    "A previous session left unsaved work. Click Recover… to restore or discard each file.";
                LoadErrorInfoBar.IsOpen  = true;
            }
            if (AutosaveRecoverButton is not null)
                AutosaveRecoverButton.Visibility = Visibility.Visible;

            // Arm the 30s auto-dismiss so the prompt clears itself even
            // if the user neither recovers/discards nor opens a file.
            StartAutosaveInfoBarAutoDismiss();
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "Architect.Recovery", "Survivor scan failed", ex);
        }
    }

    // Bumped by DismissAutosaveInfoBar (UI thread) so a deferred scan result
    // can tell whether a dismissal happened after it was scheduled.
    private int _autosaveDismissGen;

    // Survivors found at boot, surfaced through the recovery dialog.
    private System.Collections.Generic.IReadOnlyList<Phoenix.Controls.Architect.WinUI.Services.AutosaveScout.Survivor>? _autosaveSurvivors;

    private async void OnAutosaveRecoverClicked(object sender, RoutedEventArgs e)
    {
        try { await ShowAutosaveRecoveryDialogAsync(); }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "Architect.Recovery", "Recovery dialog", ex);
        }
    }

    /// <summary>
    /// Dedicated recovery dialog. Lists each autosave / crash
    /// survivor with a Recover and a Discard button. Recover renames the
    /// <c>.autosave</c> (or <c>.tmp</c>) to its target <c>.phxg</c> and opens
    /// it; Discard deletes the survivor. After all entries are handled the
    /// InfoBar auto-dismisses. The scan is naturally non-recurring once a
    /// survivor is recovered or discarded — the underlying file no longer
    /// exists, so the next boot's AutosaveScout.Scan won't re-surface it (this
    /// is why no extra AppConfig timestamp is needed to suppress re-prompting).
    /// </summary>
    private async System.Threading.Tasks.Task ShowAutosaveRecoveryDialogAsync()
    {
        if (_autosaveSurvivors is null || _autosaveSurvivors.Count == 0)
        {
            DismissAutosaveInfoBar();
            return;
        }

        var panel = new StackPanel { Spacing = 8, MinWidth = 460 };
        // Live mutable working set so per-row handling shrinks it.
        var remaining = new System.Collections.Generic.List<Phoenix.Controls.Architect.WinUI.Services.AutosaveScout.Survivor>(_autosaveSurvivors);

        var dlg = new ContentDialog
        {
            Title = "Recover unsaved work",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        void Rebuild()
        {
            panel.Children.Clear();
            if (remaining.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "All survivors handled.",
                    Foreground = (Brush?)Application.Current.Resources["CoalSecondaryTextBrush"],
                });
                return;
            }
            foreach (var s in remaining)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                string target = ResolveRecoveryTarget(s);
                var info = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock
                {
                    Text = Path.GetFileName(target),
                    // "MonoFont" is declared <x:String> in
                    // PhoenixDark.xaml (a font-uri+family-list string), NOT a
                    // FontFamily object. A raw C# cast of String→FontFamily
                    // throws InvalidCastException (XAML StaticResource works only
                    // via the FontFamily type converter; a code-behind cast does
                    // not), and the "?? Consolas" never runs because the cast
                    // throws before it. Construct the FontFamily from the string
                    // the way the converter would.
                    FontFamily = Application.Current.Resources["MonoFont"] is string monoFamily
                                 ? new Microsoft.UI.Xaml.Media.FontFamily(monoFamily)
                                 : new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    FontSize = 12,
                });
                info.Children.Add(new TextBlock
                {
                    Text = $"{s.Kind} · {s.LastWriteUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
                    FontSize = 10,
                    Foreground = (Brush?)Application.Current.Resources["CoalSecondaryTextBrush"],
                });
                Grid.SetColumn(info, 0);
                row.Children.Add(info);

                var recoverBtn = new Button { Content = "Recover", Margin = new Thickness(6, 0, 0, 0) };
                var captured = s;
                recoverBtn.Click += async (_, _) =>
                {
                    if (await RecoverSurvivorAsync(captured))
                    {
                        remaining.Remove(captured);
                        Rebuild();
                        if (remaining.Count == 0) { dlg.Hide(); DismissAutosaveInfoBar(); }
                    }
                };
                Grid.SetColumn(recoverBtn, 1);
                row.Children.Add(recoverBtn);

                var discardBtn = new Button { Content = "Discard", Margin = new Thickness(6, 0, 0, 0) };
                discardBtn.Click += (_, _) =>
                {
                    if (DiscardSurvivor(captured))
                    {
                        remaining.Remove(captured);
                        Rebuild();
                        if (remaining.Count == 0) { dlg.Hide(); DismissAutosaveInfoBar(); }
                    }
                };
                Grid.SetColumn(discardBtn, 2);
                row.Children.Add(discardBtn);

                panel.Children.Add(row);
            }
        }

        Rebuild();
        dlg.Content = panel;
        await dlg.ShowAsync();

        // Reflect whatever survived the session (user closed mid-way) so the
        // InfoBar count + the stashed list stay accurate.
        _autosaveSurvivors = remaining.Count == 0 ? null : remaining;
        if (remaining.Count == 0) DismissAutosaveInfoBar();
        else if (LoadErrorInfoBar is not null)
            LoadErrorInfoBar.Title = $"{remaining.Count} autosave / crash survivor(s) found";
    }

    private static string ResolveRecoveryTarget(
        Phoenix.Controls.Architect.WinUI.Services.AutosaveScout.Survivor s)
    {
        // Atomic-temp + sibling-autosave carry an OriginalPath; untitled
        // autosaves don't, so we strip the .autosave suffix from the file name
        // to land beside it (the user can Save As elsewhere afterwards).
        if (!string.IsNullOrEmpty(s.OriginalPath)) return s.OriginalPath!;
        string p = s.Path;
        const string autosaveExt = ".autosave";
        if (p.EndsWith(autosaveExt, StringComparison.OrdinalIgnoreCase))
            return p.Substring(0, p.Length - autosaveExt.Length);
        if (p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            return p.Substring(0, p.Length - 4);
        return p;
    }

    private async System.Threading.Tasks.Task<bool> RecoverSurvivorAsync(
        Phoenix.Controls.Architect.WinUI.Services.AutosaveScout.Survivor s)
    {
        try
        {
            string target = ResolveRecoveryTarget(s);
            if (!File.Exists(s.Path))
            {
                SetStatus($"Recovery source already gone: {Path.GetFileName(s.Path)}", ArchitectStatusLight.Yellow);
                return true; // treat as handled — nothing to recover.
            }

            // If a live file already occupies the target, side-step it by
            // back-stamping a .recovered copy so we never clobber existing work.
            string dest = target;
            if (File.Exists(dest))
            {
                string dir = Path.GetDirectoryName(dest) ?? string.Empty;
                string stem = Path.GetFileNameWithoutExtension(dest);
                string ext = Path.GetExtension(dest);
                dest = Path.Combine(dir, $"{stem}.recovered-{DateTime.Now:yyyyMMdd-HHmmss}{ext}");
            }

            File.Copy(s.Path, dest, overwrite: false);
            try { File.Delete(s.Path); } catch { /* leave source if locked; copy already landed */ }

            bool opened = await _viewModel.OpenAsync(dest).ConfigureAwait(true);
            SetStatus(opened
                ? $"Recovered {Path.GetFileName(dest)}."
                : $"Recovered {Path.GetFileName(dest)} but reload failed — open it manually.",
                opened ? ArchitectStatusLight.Green : ArchitectStatusLight.Yellow);
            if (opened) RememberArchitectDir(dest);
            return true;
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "Architect.Recovery", $"Recover '{s.Path}'", ex);
            SetStatus("Recovery failed — see System Log.", ArchitectStatusLight.Red);
            return false;
        }
    }

    private static bool DiscardSurvivor(
        Phoenix.Controls.Architect.WinUI.Services.AutosaveScout.Survivor s)
    {
        try
        {
            if (File.Exists(s.Path)) File.Delete(s.Path);
            Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                $"Discarded recovery survivor: {s.Path}",
                "Architect.Recovery", Phoenix.Controls.Shared.Models.LogLevel.System);
            return true;
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "Architect.Recovery", $"Discard '{s.Path}'", ex);
            return false;
        }
    }

    // Auto-dismiss the autosave-recovery InfoBar after 30s so a stale
    // recovery prompt (the same survivor re-surfacing every session) doesn't
    // linger on screen. Also dismissed immediately when a .phxg loads — see
    // OpenAsync. The recovery prompt should disappear when a .phxg file loads
    // or after 30 seconds.
    private Microsoft.UI.Xaml.DispatcherTimer? _autosaveInfoBarTimer;

    private void StartAutosaveInfoBarAutoDismiss()
    {
        if (DispatcherQueue is null) return;
        if (_autosaveInfoBarTimer is null)
        {
            _autosaveInfoBarTimer = new Microsoft.UI.Xaml.DispatcherTimer();
            _autosaveInfoBarTimer.Tick += (_, _) => DismissAutosaveInfoBar();
        }
        _autosaveInfoBarTimer.Stop();
        _autosaveInfoBarTimer.Interval = System.TimeSpan.FromSeconds(30);
        _autosaveInfoBarTimer.Start();
    }

    private void DismissAutosaveInfoBar()
    {
        _autosaveDismissGen++;
        _autosaveInfoBarTimer?.Stop();
        if (LoadErrorInfoBar is not null) LoadErrorInfoBar.IsOpen = false;
        if (AutosaveRecoverButton is not null) AutosaveRecoverButton.Visibility = Visibility.Collapsed;
    }

    private void StopAutosave()
    {
        try
        {
            if (_autosave is not null)
            {
                _autosave.AutosaveSucceeded -= OnAutosaveSucceeded;
                _autosave.AutosaveFailed    -= OnAutosaveFailed;
            }
            _autosave?.Dispose();
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "Architect.Autosave", "Dispose failed", ex);
        }
        finally { _autosave = null; }
    }

    // Fires on the dispatcher timer (UI thread), so
    // touching status-bar / InfoBar directly is safe.
    private void OnAutosaveSucceeded(string fileName)
    {
        try { SetStatus($"Autosaved {fileName}", ArchitectStatusLight.Green); }
        catch (Exception ex) { Phoenix.Controls.Shared.Services.GlobalLogger.Error("Architect.Autosave", "success status", ex); }
    }

    private void OnAutosaveFailed(int consecutiveFailures, Exception ex)
    {
        try
        {
            // Brief warning light immediately; a persistent InfoBar once a few
            // ticks in a row have failed (so a one-off transient blip doesn't
            // nag, but a genuinely-stuck path does).
            SetStatus("Autosave failed", ArchitectStatusLight.Red);
            if (consecutiveFailures >= 3 && LoadErrorInfoBar is not null)
            {
                LoadErrorInfoBar.Severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning;
                LoadErrorInfoBar.Title    = "Autosave is failing";
                LoadErrorInfoBar.Message  =
                    $"The last {consecutiveFailures} autosaves failed ({ex.GetType().Name}: {ex.Message}). " +
                    "Your recovery file may be stale — save manually to a writable location.";
                LoadErrorInfoBar.IsOpen   = true;
            }
        }
        catch (Exception logEx) { Phoenix.Controls.Shared.Services.GlobalLogger.Error("Architect.Autosave", "failure surface", logEx); }
    }

    /// <summary>
    /// File → Save As... / Ctrl+Shift+S. Always shows the picker even when
    /// a path is already loaded — pre-fix there was no path to write a
    /// graph to a different filename without leaving the app. Suggests the
    /// current filename when available.
    /// </summary>
    private async Task OnFileSaveAsRequestedAsync()
    {
        if (!await ConfirmSaveValidationAsync())
        {
            SetStatus("Save cancelled — validation rejected.", ArchitectStatusLight.Yellow);
            return;
        }

        var hwnd = WindowNative.GetWindowHandle(_hostWindow);
        string startDir = ResolveStartDir(
            Phoenix.Controls.Shared.Services.ConfigManager.Current.LastArchitectOpenDir,
            Paths.HubLogic);
        string suggested = string.IsNullOrEmpty(_viewModel.LoadedFilePath)
            ? "graph"
            : Path.GetFileNameWithoutExtension(_viewModel.LoadedFilePath!);
        var path = CustomFilePicker.PickSaveFile(
            hwnd,
            startDir,
            suggested,
            new[] { ("Phoenix Graph", ".phxg") });
        if (string.IsNullOrEmpty(path))
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                "Save As cancelled — work is still saved at its current path.",
                "Architect", Phoenix.Controls.Shared.Models.LogLevel.System);
            return;
        }
        await _viewModel.SaveAsync(path);
        Phoenix.Controls.Architect.WinUI.Services.RecentFiles.TouchDeferred(path);
        DispatcherQueue?.TryEnqueue(RebuildChromeRecentMenu);
        RememberArchitectDir(path);
        SetStatus($"Saved {Path.GetFileName(path)}.", ArchitectStatusLight.Green);
    }

    private void InitWithHostWindow(object picker)
    {
        var hwnd = WindowNative.GetWindowHandle(_hostWindow);
        InitializeWithWindow.Initialize(picker, hwnd);
    }

    // ─── Chrome handler hook / unhook ──────────────────
    // The chrome menu subscriptions used to be inline lambdas — every one
    // captured `this` and so kept the MainView alive past Unloaded. A
    // pillar-tab swap rebuilt MainView and added another ~25 handlers to
    // the same Chrome events, growing the invocation list without bound.
    // Naming the methods lets HookChromeHandlers / UnhookChromeHandlers pair
    // symmetrically; UnhookChromeHandlers is invoked from the Unloaded
    // handler below.

    private void HookChromeHandlers()
    {
        Chrome.LaunchPillarRequested          += OnChromeLaunchPillar;
        Chrome.FileNewRequested               += OnChromeFileNew;
        Chrome.FileOpenRequested              += OnChromeFileOpenAsync;
        Chrome.FileSaveRequested              += OnChromeFileSaveAsync;
        Chrome.FileSaveAsRequested            += OnChromeFileSaveAsAsync;
        Chrome.FileExportRequested            += OnChromeFileExport;
        // 0.10.0 — File → Restore previous version… surfaces
        // the rolling .phxg.bak[1-3] chain via a ContentDialog. The chrome here
        // is collapsed in the Hub-embedded MainView, so in practice the click
        // arrives through HubChrome's "architect.file.restoreBackup" dispatch
        // (which routes to MainView.RestoreFromBackupAsync). The Chrome
        // subscription stays wired so a future "show the architect chrome"
        // toggle would have parity with the embedded path.
        Chrome.FileRestoreRequested           += OnChromeFileRestoreAsync;
        // TODO §2 resolution — File → Welcome force-shows the always-available
        // Welcome card overlay. Same dual-path note as Restore above: the
        // embedded MainView's chrome is collapsed, so in practice the click
        // arrives through HubChrome's "architect.file.welcome" dispatch (which
        // routes to MainView.ShowWelcome); this subscription keeps parity for a
        // future "show the architect chrome" toggle. Non-destructive — the open
        // graph stays loaded behind the card.
        Chrome.FileWelcomeRequested           += OnChromeFileWelcome;
        Chrome.LiveDebugToggled               += OnChromeLiveDebugToggled;
        Chrome.EditUndoRequested              += OnChromeEditUndo;
        Chrome.EditRedoRequested              += OnChromeEditRedo;
        // keep the Edit-menu undo/redo enable state in
        // sync with the stack depth (push-based via the canvas's signal).
        _logicCanvasView.UndoRedoChanged      += OnUndoRedoChangedForMenu;
        Chrome.SetUndoRedoEnabled(CanUndo, CanRedo);
        Chrome.EditFindRequested              += OnChromeEditFind;
        Chrome.EditGroupRequested             += OnChromeEditGroup;
        Chrome.EditCommentFrameRequested      += OnChromeEditCommentFrame;
        Chrome.ViewSpawnRequested             += OnChromeViewSpawn;
        Chrome.ViewFrameSelectionRequested    += OnChromeViewFrameSelection;
        Chrome.ViewShowGridToggled            += OnChromeViewShowGridToggled;
        // View → Minimap toggle. Routes through SetMinimapVisible
        // (which also persists into AppConfig.ArchitectMinimapVisible). The
        // canvas raises MinimapVisibilityChanged on the in-overlay × glyph
        // path so the toggle reflects the new state without a roundtrip.
        Chrome.ViewMinimapToggled             += OnChromeViewMinimapToggled;
        _logicCanvasView.MinimapVisibilityChanged += OnCanvasMinimapVisibilityChanged;
        // 0.10.0 UX P2: View → Bookmarks legend… opens a Flyout on the
        // canvas; its own Closed handler restores focus.
        Chrome.ViewBookmarksRequested         += OnChromeViewBookmarks;
        Chrome.HelpNodeRefRequested           += OnChromeHelpNodeRef;
        // Script menu + Keyboard Shortcuts dialog.
        Chrome.ScriptSyncEventPeersRequested  += OnChromeScriptSyncEventPeers;
        Chrome.HelpKeyboardShortcutsRequested += OnChromeHelpKeyboardShortcutsAsync;
        // F1 fallback — raised by the canvas when F1
        // fires with no node selected. Routed to the same dialog the
        // chrome menu opens.
        _logicCanvasView.KeyboardShortcutsRequested += OnCanvasKeyboardShortcutsRequestedAsync;
        // F4 inspector toggle.
        // Canvas raises InspectorToggleRequested; we flip the inspector's
        // expanded / rolled-up state through the same path the chevron
        // and the View menu use. The chrome's "View → Toggle Inspector"
        // entry routes through this same bridge below.
        _logicCanvasView.InspectorToggleRequested += OnInspectorToggleRequestedFromCanvas;
        Chrome.InspectorToggleRequested            += OnInspectorToggleRequestedFromCanvas;
        Chrome.FileCloseRequested                  += OnChromeFileCloseRequested;

        // The Open Recent submenu + Refresh Script item live on
        // ArchitectChrome.xaml WITHOUT Click= attributes (adding handlers in
        // ArchitectChrome.xaml.cs would create an orphaned-handler build
        // break). Instead we wire their .Click
        // here — MainView owns the Chrome instance and the action plumbing.
        if (Chrome.MenuViewRefreshScript is not null)
            Chrome.MenuViewRefreshScript.Click += OnChromeViewRefreshScript;
        if (Chrome.MenuFileOpenRecent is not null)
        {
            // WinUI's MenuFlyoutSubItem (and its parent MenuBarItem) expose no
            // "opening" event, so the MRU can't be rebuilt lazily on dropdown-open
            // the way the WinForms menu was. Instead it is maintained eagerly: an
            // initial deferred build here (kept off the synchronous hookup path so
            // chrome attach never triggers a RecentFiles disk read — freeze-sweep
            // sensitivity), then a rebuild after each RecentFiles.TouchDeferred
            // mutation site (RefreshChromeRecentMenu) so it stays live in-session.
            DispatcherQueue?.TryEnqueue(RebuildChromeRecentMenu);
        }
    }

    private void UnhookChromeHandlers()
    {
        try { Chrome.LaunchPillarRequested          -= OnChromeLaunchPillar; } catch { /* best-effort */ }
        try { Chrome.FileNewRequested               -= OnChromeFileNew; } catch { /* best-effort */ }
        try { Chrome.FileOpenRequested              -= OnChromeFileOpenAsync; } catch { /* best-effort */ }
        try { Chrome.FileSaveRequested              -= OnChromeFileSaveAsync; } catch { /* best-effort */ }
        try { Chrome.FileSaveAsRequested            -= OnChromeFileSaveAsAsync; } catch { /* best-effort */ }
        try { Chrome.FileExportRequested            -= OnChromeFileExport; } catch { /* best-effort */ }
        try { Chrome.FileRestoreRequested           -= OnChromeFileRestoreAsync; } catch { /* best-effort */ }
        try { Chrome.LiveDebugToggled               -= OnChromeLiveDebugToggled; } catch { /* best-effort */ }
        try { Chrome.EditUndoRequested              -= OnChromeEditUndo; } catch { /* best-effort */ }
        try { Chrome.EditRedoRequested              -= OnChromeEditRedo; } catch { /* best-effort */ }
        try { _logicCanvasView.UndoRedoChanged      -= OnUndoRedoChangedForMenu; } catch { /* best-effort */ }
        try { Chrome.EditFindRequested              -= OnChromeEditFind; } catch { /* best-effort */ }
        try { Chrome.EditGroupRequested             -= OnChromeEditGroup; } catch { /* best-effort */ }
        try { Chrome.EditCommentFrameRequested      -= OnChromeEditCommentFrame; } catch { /* best-effort */ }
        try { Chrome.ViewSpawnRequested             -= OnChromeViewSpawn; } catch { /* best-effort */ }
        try { Chrome.ViewFrameSelectionRequested    -= OnChromeViewFrameSelection; } catch { /* best-effort */ }
        try { Chrome.ViewShowGridToggled            -= OnChromeViewShowGridToggled; } catch { /* best-effort */ }
        try { Chrome.ViewMinimapToggled             -= OnChromeViewMinimapToggled; } catch { /* best-effort */ }
        try { _logicCanvasView.MinimapVisibilityChanged -= OnCanvasMinimapVisibilityChanged; } catch { /* best-effort */ }
        try { Chrome.ViewBookmarksRequested         -= OnChromeViewBookmarks; } catch { /* best-effort */ }
        try { Chrome.HelpNodeRefRequested           -= OnChromeHelpNodeRef; } catch { /* best-effort */ }
        try { Chrome.ScriptSyncEventPeersRequested  -= OnChromeScriptSyncEventPeers; } catch { /* best-effort */ }
        try { Chrome.HelpKeyboardShortcutsRequested -= OnChromeHelpKeyboardShortcutsAsync; } catch { /* best-effort */ }
        try { _logicCanvasView.KeyboardShortcutsRequested -= OnCanvasKeyboardShortcutsRequestedAsync; } catch { /* best-effort */ }
        // Unhook F4 inspector toggle.
        try { _logicCanvasView.InspectorToggleRequested -= OnInspectorToggleRequestedFromCanvas; } catch { /* best-effort */ }
        try { Chrome.InspectorToggleRequested            -= OnInspectorToggleRequestedFromCanvas; } catch { /* best-effort */ }
        try { Chrome.FileCloseRequested                  -= OnChromeFileCloseRequested; } catch { /* best-effort */ }
        // Detach the directly-wired chrome menu items.
        try { if (Chrome.MenuViewRefreshScript is not null) Chrome.MenuViewRefreshScript.Click -= OnChromeViewRefreshScript; } catch { /* best-effort */ }
        // Open Recent MRU is maintained eagerly (no menu-open event exists to detach).
    }

    private void OnChromeLaunchPillar(object? s, Phoenix.Controls.Shared.WinUI.Contracts.PillarKind kind)
        => PillarSwitchRequested?.Invoke(this, kind);

    private void OnChromeFileNew(object? s, EventArgs e) => OnFileNewRequested();

    private async void OnChromeFileOpenAsync(object? s, EventArgs e)
    {
        await OnFileOpenRequestedAsync();
        RestoreCanvasFocus();
    }

    private async void OnChromeFileSaveAsync(object? s, EventArgs e)
    {
        await OnFileSaveRequestedAsync();
        RestoreCanvasFocus();
    }

    private async void OnChromeFileSaveAsAsync(object? s, EventArgs e)
    {
        await OnFileSaveAsRequestedAsync();
        RestoreCanvasFocus();
    }

    private void OnChromeFileExport(object? s, EventArgs e)
    {
        OnFileExportRequested();
        RestoreCanvasFocus();
    }

    // TODO §2 resolution — File → Welcome. Force-shows the always-available
    // Welcome card overlay regardless of file / graph state. Non-destructive:
    // the open graph stays loaded behind the card. Routes through the same
    // ShowWelcome pass-through the HubChrome "architect.file.welcome" dispatch
    // uses.
    private void OnChromeFileWelcome(object? s, EventArgs e)
    {
        ShowWelcome();
        RestoreCanvasFocus();
    }

    private async void OnChromeFileRestoreAsync(object? s, EventArgs e)
    {
        await RestoreFromBackupAsync();
        RestoreCanvasFocus();
    }

    private void OnChromeLiveDebugToggled(object? s, bool on)
    {
        _viewModel.SetLiveDebugEnabled(on);
        RestoreCanvasFocus();
    }

    // Push the current stack depth onto the Edit menu.
    private void OnUndoRedoChangedForMenu(object? s, EventArgs e) => Chrome.SetUndoRedoEnabled(CanUndo, CanRedo);

    private void OnChromeEditUndo(object? s, EventArgs e) { Undo(); RestoreCanvasFocus(); }
    private void OnChromeEditRedo(object? s, EventArgs e) { Redo(); RestoreCanvasFocus(); }
    // FindNode opens a flyout; its own Closed restores focus.
    private void OnChromeEditFind(object? s, EventArgs e) => FindNode();
    private void OnChromeEditGroup(object? s, EventArgs e) { GroupSelection(); RestoreCanvasFocus(); }
    private void OnChromeEditCommentFrame(object? s, EventArgs e) { AddCommentFrame(); RestoreCanvasFocus(); }
    // Spawn opens the palette flyout; its own Closed restores focus.
    private void OnChromeViewSpawn(object? s, EventArgs e) => SpawnAtViewCenter();
    private void OnChromeViewFrameSelection(object? s, EventArgs e) { FrameSelection(); RestoreCanvasFocus(); }
    private void OnChromeViewShowGridToggled(object? s, bool on) { ToggleShowGrid(); RestoreCanvasFocus(); }

    // Chrome View → Minimap toggle. Flips the overlay's
    // Visibility AND persists into AppConfig.ArchitectMinimapVisible.
    private void OnChromeViewMinimapToggled(object? s, bool on)
    {
        _logicCanvasView?.SetMinimapVisible(on);
        RestoreCanvasFocus();
    }

    // Canvas raises MinimapVisibilityChanged when the in-overlay
    // × glyph hides the minimap; sync the chrome toggle so its IsChecked
    // reflects the new state without a roundtrip.
    private void OnCanvasMinimapVisibilityChanged(object? s, bool visible)
    {
        try { Chrome.SetMinimapChecked(visible); } catch { /* best-effort */ }
    }

    private void OnChromeViewBookmarks(object? s, EventArgs e) => _logicCanvasView?.ShowBookmarkLegendFlyout();

    // View → Refresh Script Preview (F5). The in-app preview pane was
    // retired in T15 (export-to-.phx is the canonical workflow), so this
    // regenerates the .phx beside the loaded graph — the same exporter walk
    // File → Export runs, surfaced under a single F5 chord. No-op (with a
    // status line) when nothing is loaded yet.
    private async void OnChromeViewRefreshScript(object? s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_viewModel.LoadedFilePath))
        {
            SetStatus("Refresh Script: save the graph first (export needs a path).",
                ArchitectStatusLight.Yellow);
            RestoreCanvasFocus();
            return;
        }
        try
        {
            var phx = await _viewModel.ExportPhxBesideAsync(_viewModel.LoadedFilePath!).ConfigureAwait(true);
            SetStatus(string.IsNullOrEmpty(phx)
                ? "Refresh Script failed — see System Log."
                : $"Refreshed {Path.GetFileName(phx)}.",
                string.IsNullOrEmpty(phx) ? ArchitectStatusLight.Red : ArchitectStatusLight.Green);
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "Architect.MainView", "Refresh Script (F5)", ex);
            SetStatus("Refresh Script failed — see System Log.", ArchitectStatusLight.Red);
        }
        RestoreCanvasFocus();
    }

    // File → Open Recent. Rebuild the MRU children whenever the
    // submenu is about to open so it tracks the live recent-files history.
    /// <summary>
    /// Populate the chrome's Open Recent submenu from the recent-files
    /// history. Each entry routes through ArchitectWindowRegistry.OpenFileAsync
    /// (which dedups by path — re-opening an already-open .phxg focuses the
    /// existing sibling). A trailing "(no recent files)" placeholder keeps the
    /// submenu from rendering empty. Missing files are skipped + pruned.
    /// </summary>
    private void RebuildChromeRecentMenu()
    {
        if (Chrome.MenuFileOpenRecent is null) return;
        // Off-thread scan (MRU JSON read + per-entry File.Exists, which
        // stalls on networked / OneDrive-backed paths), then marshal only
        // the surviving path list back for menu construction.
        int gen = System.Threading.Interlocked.Increment(ref _recentMenuBuildGen);
        _ = Phoenix.Controls.Shared.Core.AsyncErrorBoundary.SafeRunAsync(
            () => System.Threading.Tasks.Task.Run(() =>
            {
                var entries = CollectRecentMenuEntries();
                DispatcherQueue?.TryEnqueue(() =>
                {
                    // Only the newest scheduled rebuild may repaint — an
                    // older scan landing late must not roll the menu back.
                    if (gen != _recentMenuBuildGen) return;
                    ApplyChromeRecentMenu(entries);
                });
            }),
            "Architect.MainView", "RebuildChromeRecentMenu");
    }

    // Monotonic rebuild stamp; incremented on the UI thread per scheduled
    // rebuild, compared on the UI thread before repainting.
    private int _recentMenuBuildGen;

    /// <summary>
    /// Thread-pool half of <see cref="RebuildChromeRecentMenu"/> — all the
    /// disk work (MRU load, existence checks, dead-entry pruning) with no
    /// UI access.
    /// </summary>
    private List<string> CollectRecentMenuEntries()
    {
        var kept = new List<string>();
        var recents = Phoenix.Controls.Architect.WinUI.Services.RecentFiles.LoadMerged();
        foreach (var path in recents)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!System.IO.File.Exists(path))
            {
                // Drop dead entries so the list self-heals.
                Phoenix.Controls.Architect.WinUI.Services.RecentFiles.Remove(path);
                continue;
            }
            kept.Add(path);
            if (kept.Count >= 12) break; // cap the dropdown length
        }
        return kept;
    }

    /// <summary>
    /// UI-thread half of <see cref="RebuildChromeRecentMenu"/> — builds the
    /// submenu items from the pre-scanned path list.
    /// </summary>
    private void ApplyChromeRecentMenu(List<string> paths)
    {
        var sub = Chrome.MenuFileOpenRecent;
        if (sub is null) return;
        try
        {
            sub.Items.Clear();
            foreach (var path in paths)
            {
                string captured = path;
                var item = new MenuFlyoutItem
                {
                    Text = Path.GetFileName(path),
                };
                ToolTipService.SetToolTip(item, path);
                item.Click += (_, _) => OpenRecentPathFromChrome(captured);
                sub.Items.Add(item);
            }
            if (sub.Items.Count == 0)
            {
                sub.Items.Add(new MenuFlyoutItem { Text = "(no recent files)", IsEnabled = false });
            }
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "Architect.MainView", "RebuildChromeRecentMenu", ex);
        }
    }

    /// <summary>
    /// Open a recent-file pick from the chrome MRU. Honours the same
    /// "load into the blank embedded view, else spawn a sibling" idiom as
    /// File → Open so the recent path doesn't always spawn a new window.
    /// </summary>
    private async void OpenRecentPathFromChrome(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return;
            if (!System.IO.File.Exists(path))
            {
                Phoenix.Controls.Architect.WinUI.Services.RecentFiles.Remove(path);
                SetStatus($"Recent entry missing: {Path.GetFileName(path)}", ArchitectStatusLight.Yellow);
                return;
            }

            bool embeddedIsBlank =
                string.IsNullOrEmpty(_viewModel.LoadedFilePath)
                && _viewModel.LogicCanvas.Graph.Nodes.Count == 0;

            if (embeddedIsBlank)
            {
                bool ok = await _viewModel.OpenAsync(path).ConfigureAwait(true);
                SetStatus(ok
                    ? $"Opened {Path.GetFileName(path)}."
                    : $"Failed to load {Path.GetFileName(path)} — see System Log.",
                    ok ? ArchitectStatusLight.Green : ArchitectStatusLight.Red);
                RememberArchitectDir(path);
            }
            else
            {
                var spawned = await ArchitectWindowRegistry.OpenFileAsync(path);
                if (spawned is null)
                {
                    SetStatus($"Failed to load {Path.GetFileName(path)} — see System Log.",
                        ArchitectStatusLight.Red);
                }
                RememberArchitectDir(path);
            }
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "Architect.MainView", "OpenRecentPathFromChrome", ex);
        }
        RestoreCanvasFocus();
    }
    // NodeDocs opens a separate Window; focus stays where it should.
    private void OnChromeHelpNodeRef(object? s, EventArgs e) => OpenNodeDocs();

    // Script → Sync Event Peers. Routes to the canvas's
    // debounced cross-file sync timer (the same path wire-drop / event-rename
    // edits use). Status feedback shows in the System Log; no modal (this
    // isn't a rejection, but Architect's status strip already surfaces sync
    // activity through the canvas's GlobalLogger taps).
    private void OnChromeScriptSyncEventPeers(object? s, EventArgs e)
    {
        _logicCanvasView.RequestSyncEventPeersFromShell();
        Phoenix.Controls.Shared.Services.GlobalLogger.Log(
            "Sync Event Peers requested.",
            "Architect",
            Phoenix.Controls.Shared.Models.LogLevel.System);
        RestoreCanvasFocus();
    }

    // Help → Keyboard Shortcuts… opens the dialog
    // populated with the canvas + chrome chord catalog.
    private async void OnChromeHelpKeyboardShortcutsAsync(object? s, EventArgs e)
    {
        await Phoenix.Controls.Architect.WinUI.Dialogs.KeyboardShortcutsDialog.ShowAsync(XamlRoot);
        RestoreCanvasFocus();
    }

    // F1 fallback — bare F1 on an
    // empty canvas opens the same dialog.
    private async void OnCanvasKeyboardShortcutsRequestedAsync(object? s, EventArgs e)
    {
        await Phoenix.Controls.Architect.WinUI.Dialogs.KeyboardShortcutsDialog.ShowAsync(XamlRoot);
        RestoreCanvasFocus();
    }
}

/// <summary>
/// The three status-strip zones, mirroring the pre-T15 StatusBar's
/// left / center / right StatusZone slots. Used by
/// <see cref="MainView.SetStatusZoneTooltip"/> to target a zone's dynamic
/// hover-tooltip resolver.
/// </summary>
public enum ArchitectStatusZone
{
    Left,
    Center,
    Right,
}
