using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Architect.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Architect.WinUI.Canvas;
using Phoenix.Controls.Architect.WinUI.Controls;
using Phoenix.Controls.Architect.WinUI.Services;
using Phoenix.Controls.Architect.WinUI.ViewModels;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.WinUI.Services;
using Windows.Graphics;
using WinRT.Interop;

// Namespace deliberately avoids "Phoenix.Controls.Architect.WinUI.Windows"
// — that name would shadow the platform `Windows.*` namespace from any
// file under Phoenix.Controls.Architect.WinUI.* and break references like
// Windows.UI.Colors / Windows.Storage / Windows.ApplicationModel.
namespace Phoenix.Controls.Architect.WinUI.Hosting;

/// <summary>
/// Top-level Architect window hosting one <see cref="ArchitectViewModel"/>
/// for a single .phxg. Spawned by <see cref="ArchitectWindowRegistry"/>
/// in response to File → New / Open / Open Recent / drag-drop from the
/// Hub-embedded Architect view, restoring the pre-T15 multi-window
/// paradigm. Custom chrome lives in <see cref="ArchitectWindowChrome"/>;
/// per-path geometry persists via <see cref="SiblingWindowStateStore"/>;
/// cross-window copy/paste works for free through the existing
/// "PhoenixControls.SubGraph" system-clipboard format
/// (<see cref="LogicCanvasView.Clipboard"/>).
/// </summary>
public sealed partial class ArchitectSiblingWindow : Window
{
    private readonly ArchitectViewModel _viewModel;
    private readonly MenuAcceleratorFocusGate _menuAccelGate = new();
    private AppWindow? _appWindow;
    private bool _confirmedClose;
    private bool _promptInFlight;
    private string? _persistKey;
    private AutosaveService? _autosave;

    public ArchitectSiblingWindow()
    {
        InitializeComponent();
        Title = Localizer.T("architect.window.sibling.title", "Architect");

        _viewModel = new ArchitectViewModel();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Three-way wiring — canvas hosts the graph, rail mirrors it. The
        // docked inspector card is replaced by a floating
        // InspectorWindow opened from the rail header. Identical to
        // MainView's wiring so the sibling window has parity
        // with the embedded view (modulo the absent pillar tabs + databank).
        // Databank tab is intentionally NOT exposed on sibling windows: the
        // databank is a project-wide singleton and editing it from multiple
        // windows would be confusing.
        CanvasView.DataContext = _viewModel.LogicCanvas;
        CanvasView.ArchitectVm = _viewModel;
        Rail.SetCanvasContext(_viewModel.LogicCanvas);
        Rail.SetArchitectContext(_viewModel);
        // Hand the canvas view back to the rail so SubGraphWindow
        // editors launched from rail rows refocus this canvas on close.
        Rail.SetCanvasView(CanvasView);

        // Rail collapse + floating Inspector window. Both
        // toggles persist their state through ConfigManager so a user who
        // likes a tight canvas-first layout doesn't have to re-collapse on
        // every restart. ApplyPersistedRailAndInspectorState (run after the
        // AppWindow resolves in OnActivatedOnce) replays the saved flags
        // into the visible chrome on first paint.
        Rail.RailCollapseToggled       += OnRailCollapseToggled;
        Rail.InspectorToggleRequested  += OnInspectorToggleRequested;

        CanvasView.SaveRequested += async (_, _) => await OnFileSaveAsync();
        CanvasView.OpenRequested += async (_, _) => await OnFileOpenAsync();
        // Drag-drop on the canvas opens the file in ANOTHER sibling window
        // (per the spec — drag-drop spawns a SIBLING). Registry dedup means
        // dropping a .phxg already open elsewhere just focuses that window.
        CanvasView.FileOpenRequested += (_, path) => SpawnOrFocusSibling(path);

        // Welcome card buttons must work inside sibling windows
        // too — pre-fix the sibling subscribed only to OpenRequested, so the
        // empty-state "New Graph" / "Recent…" cards silently dropped. Both
        // route to the same handlers the menu uses; "New" always spawns a
        // sibling (matching MainView's contract).
        CanvasView.NewRequested        += (_, _) => OnFileNewClicked(this, new RoutedEventArgs());
        CanvasView.OpenRecentRequested += async (_, _) => await OpenRecentFromCanvasAsync();

        // F1-without-selection raises this event so the
        // shell can pop the Keyboard Shortcuts dialog.
        CanvasView.KeyboardShortcutsRequested += async (_, _) => await ShowKeyboardShortcutsDialogAsync();

        // F4 inspector toggle
        // on the canvas bridges into the rail-driven
        // OnInspectorToggleRequested handler so the sibling window picks
        // up the same floating-InspectorWindow open/close path the rail
        // "Inspector" toggle uses. The chrome here is the slim
        // ArchitectWindowChrome (gold title bar only, no File / View
        // menus) — the View → Toggle Inspector menu item lives on the
        // Hub-embedded ArchitectChrome consumed by MainView.
        CanvasView.InspectorToggleRequested += OnCanvasInspectorToggleRequested;

        _viewModel.NodeFlashRequested += nodeId => CanvasView.FlashNode(nodeId);

        // Grey out the Edit-menu undo/redo items when
        // the stack is empty (push-based via the canvas's UndoRedoChanged).
        CanvasView.UndoRedoChanged += OnSiblingUndoRedoChanged;
        RefreshEditMenuEnabled();
        _viewModel.LogicCanvas.GraphMutatedAny += (_, _) => RefreshStatusCounts();
        _viewModel.LogicCanvas.GraphLoaded     += (_, _) => RefreshStatusCounts();

        // .phx export failure surface — parity with MainView. Without this
        // a sibling window's "Save" silently succeeds for the .phxg even when
        // the .phx sister-file export fails, and Hub keeps running the old
        // script (same regression repaired in MainView).
        _viewModel.PhxExportFailed += (_, _) =>
        {
            if (DispatcherQueue is null) return;
            DispatcherQueue.TryEnqueue(() => SetStatus(
                ".phxg saved — .phx export failed (Hub will keep running the old script). See System Log.",
                ArchitectStatusLight.Yellow));
        };

        // .phxg load failures surface as a status-bar message; pre-T15 they
        // popped a MessageBox, but repeatable conditions should not pop a
        // modal. The InfoBar pattern from
        // MainView could ride on a future pass; for sibling windows the
        // status bar is the lightweight surface.
        GraphSerializer.OnLoadFailed += OnLoadFailed;

        Activated += OnActivatedOnce;
        Closed += OnClosed;

        // Disable the conflicting menu chords (bare C / F, Ctrl+Z, Ctrl+W, …)
        // while a text input has focus so typing in a pill can never trigger
        // canvas actions, close the window, or lose the keystroke to
        // accelerator matching. Detached in OnClosed (the gate hooks the
        // static FocusManager.GotFocus event, which would otherwise keep this
        // window alive).
        _menuAccelGate.Attach(WindowMenuBar);

        // Version label — mirrors ArchitectChrome's behavior (assembly version
        // pulled at construction so the chrome tracks Directory.Build.props
        // automatically).
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        CaptionVersion.Text = ver is null
            ? "v?.?.? — DEV"
            : $"v{ver.Major}.{ver.Minor}.{ver.Build} — DEV";

        UpdateCaptionFromState();
    }

    private void OnActivatedOnce(object sender, WindowActivatedEventArgs args)
    {
        // Bind the chrome once we have an HWND. Doing this in the ctor is
        // racy — WindowNative.GetWindowHandle returns 0 before the first
        // Activated. Unhook after the first fire so subsequent focus
        // changes don't re-bind.
        Activated -= OnActivatedOnce;

        try
        {
            Chrome.Bind(this);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ArchitectSiblingWindow", "Chrome.Bind", ex);
        }

        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            if (_appWindow is not null)
            {
                _appWindow.Closing += OnAppWindowClosing;
                // Apply a hard minimum-size floor via
                // the WM_GETMINMAXINFO subclass (the "follow-up sweep" tool that
                // already exists and SubGraphWindow uses). Pre-fix the sibling
                // window had no floor — the user could drag it down to an
                // unusable size and a restore could leave the title bar
                // unreachable. 720x400 matches the sub-graph editor floor.
                try { Services.WindowMinSize.Apply(this, 720, 400); }
                catch (Exception ex) { GlobalLogger.Error("ArchitectSiblingWindow", "WindowMinSize.Apply", ex); }
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ArchitectSiblingWindow", "AppWindow resolve", ex);
        }

        // Restore per-path geometry once we have a path. New (unsaved) windows
        // get a centred default-size launch from the store's fallback path.
        try { SiblingWindowStateStore.Restore(this, ResolvePersistKey()); }
        catch (Exception ex)
        {
            GlobalLogger.Error("ArchitectSiblingWindow", "state restore", ex);
        }

        // Per-window autosave — MainView starts one too. Each sibling needs
        // its own AutosaveService so the 60s tick captures THIS window's
        // graph snapshot (the service is per-AVM by ctor). Survivor scan is
        // intentionally NOT run here: MainView already runs it once at
        // process start, and re-running per sibling would double-report
        // every recoverable file. The original wiring covered only the
        // embedded MainView; the sibling-window restoration
        // surfaced this gap.
        if (_autosave is null && DispatcherQueue is not null)
        {
            try
            {
                _autosave = new AutosaveService(_viewModel, DispatcherQueue);
                _autosave.Start();
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("ArchitectSiblingWindow", "autosave start", ex);
            }
        }

        // Replay persisted rail-collapsed + inspector-visible
        // flags into the visible chrome. Runs after the AppWindow has settled
        // so any future inspector-window spawn has an XamlRoot to anchor on.
        ApplyPersistedRailAndInspectorState();

        // Replay persisted minimap visibility into the canvas
        // overlay AND mirror the sibling-window menu toggle so the chrome
        // glyph reflects the actual on-screen state. CanvasView
        // MinimapVisibilityChanged keeps the toggle in sync if the user
        // later flips visibility via the in-overlay × glyph.
        try
        {
            var cfgMinimap = ConfigManager.Current;
            CanvasView.ApplyMinimapVisibilityFromConfig();
            if (MenuViewMinimap is not null)
                MenuViewMinimap.IsChecked = cfgMinimap.ArchitectMinimapVisible;
            CanvasView.MinimapVisibilityChanged -= OnCanvasMinimapVisibilityChanged;
            CanvasView.MinimapVisibilityChanged += OnCanvasMinimapVisibilityChanged;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ArchitectSiblingWindow", "Minimap restore", ex);
        }

        RefreshStatusCounts();
    }

    // ─── Rail collapse + floating Inspector window ──────

    private const double RailExpandedWidth   = 220.0;
    private const double RailCollapsedWidth  = 32.0;

    // ─── short panel open/close width animation ───────────────────
    // Per-window copy (chrome helpers stay per-window). Width-only tween of
    // a Grid column over ~170 ms (ease-out cubic) — does not re-measure the
    // absolutely-positioned canvas nodes, so it stays cheap on large graphs.
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
            var cfg = ConfigManager.Current;
            double w = cfg.ArchitectRailColumnWidth > 0
                ? cfg.ArchitectRailColumnWidth
                : RailExpandedWidth;
            if (w < 100) w = RailExpandedWidth;
            target = w;
        }
        if (!animate)
        {
            _railWidthTween?.Stop();
            RailColumn.Width = new Microsoft.UI.Xaml.GridLength(
                target, Microsoft.UI.Xaml.GridUnitType.Pixel);
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

    /// <summary>
    /// 0.11.x polish — the sibling window now hosts its own docked
    /// LogicInspector inside the right-edge InspectorColumn (parity with
    /// MainView). The floating InspectorWindow path is no longer the
    /// default surface; calling OpenFor here would orphan the docked card
    /// AND the singleton would block sibling windows from each having
    /// their own inspector. The card defaults open per
    /// AppConfig.ArchitectInspectorVisible (true on a clean install).
    /// </summary>
    private void ApplyPersistedRailAndInspectorState()
    {
        try
        {
            var cfg = ConfigManager.Current;
            ApplyRailCollapsedToColumn(cfg.ArchitectRailCollapsed, animate: false);

            // 0.11.x polish — same one-shot migration MainView applies:
            // a pre-polish persisted `ArchitectInspectorVisible = false`
            // (from closing the old floating InspectorWindow) now gets
            // force-flipped true so the docked card on this sibling
            // opens on first launch with the new code. The flag itself
            // is process-shared (AppConfig is a singleton), so whichever
            // surface runs the migration first wins.
            if (!cfg.ArchitectInspectorDockedMigrated)
            {
                cfg.ArchitectInspectorVisible        = true;
                cfg.ArchitectInspectorDockedMigrated = true;
                ConfigManager.SaveDeferred(Paths.AppConfigJson);
            }

            // Mount the per-window LogicInspector into the right-edge card.
            try
            {
                if (InspectorRegion is not null && InspectorRegion.Content is null)
                {
                    InspectorRegion.Content = new LogicInspector
                    {
                        DataContext = _viewModel.LogicInspector,
                    };
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("ArchitectSiblingWindow",
                    "ApplyPersistedRailAndInspectorState → mount LogicInspector", ex);
            }

            ApplyInspectorVisibleToColumn(cfg.ArchitectInspectorVisible, animate: false);
            Rail.SetInspectorVisibleGlyph(cfg.ArchitectInspectorVisible);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ArchitectSiblingWindow",
                "ApplyPersistedRailAndInspectorState", ex);
        }
    }

    // ─── Docked inspector column controls ───────────────
    //
    // Mirrors MainView's ApplyInspectorVisibleToColumn / chevron handlers.
    // Width-default constants are duplicated rather than lifted to a shared
    // module (chrome helpers stay per-window). The InspectorRolledUpWidth
    // value is the same 32 DIP MainView uses for its chevron strip.

    private const double InspectorDockedDefaultWidth = 320.0;
    private const double InspectorDockedMinWidth     = 240.0;
    private const double InspectorRolledUpWidth      = 32.0;
    private bool _inspectorExpanded = true;

    private void ApplyInspectorVisibleToColumn(bool visible, bool animate = true)
    {
        if (InspectorColumn is null) return;
        _inspectorExpanded = visible;
        double target;
        if (visible)
        {
            var cfg = ConfigManager.Current;
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
                InspectorRegion.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            if (animate)
                _inspectorWidthTween = AnimatePanelColumnWidth(InspectorColumn, target, _inspectorWidthTween);
            else
            {
                _inspectorWidthTween?.Stop();
                InspectorColumn.Width = new Microsoft.UI.Xaml.GridLength(
                    target, Microsoft.UI.Xaml.GridUnitType.Pixel);
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
                InspectorColumn.Width = new Microsoft.UI.Xaml.GridLength(
                    target, Microsoft.UI.Xaml.GridUnitType.Pixel);
                if (InspectorRegion is not null)
                    InspectorRegion.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            }
        }
    }

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
        catch (Exception ex)
        {
            GlobalLogger.Error("ArchitectSiblingWindow", "UpdateInspectorChevronGlyph", ex);
        }
    }

    private void OnInspectorChevronClicked(object sender, RoutedEventArgs e)
    {
        OnInspectorToggleRequested(this, !_inspectorExpanded);
    }

    private void OnRailCollapseToggled(object? sender, bool desiredCollapsed)
    {
        ApplyRailCollapsedToColumn(desiredCollapsed);
        try
        {
            var cfg = ConfigManager.Current;
            cfg.ArchitectRailCollapsed = desiredCollapsed;
            // SaveDeferred offloads the config write to a
            // background thread — parity with MainView's deferred-write calls.
            ConfigManager.SaveDeferred(Paths.AppConfigJson);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ArchitectSiblingWindow",
                "OnRailCollapseToggled persist", ex);
        }
    }

    /// <summary>
    /// Bridge for the F4
    /// canvas chord and the chrome View → Toggle Inspector menu item.
    /// Reads the currently-persisted visibility (so a stale toggle from a
    /// previous session is consistent) and flips it, then funnels through
    /// the rail-driven <see cref="OnInspectorToggleRequested"/> handler
    /// that owns the floating-InspectorWindow open/close path.
    /// </summary>
    private void OnCanvasInspectorToggleRequested(object? sender, EventArgs e)
    {
        try
        {
            bool current = ConfigManager.Current.ArchitectInspectorVisible;
            OnInspectorToggleRequested(this, !current);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ArchitectSiblingWindow",
                "OnCanvasInspectorToggleRequested", ex);
        }
    }

    private void OnInspectorToggleRequested(object? sender, bool desiredVisible)
    {
        try
        {
            // 0.11.x polish — docked inspector path. The floating
            // InspectorWindow.OpenFor / CloseIfOpen calls retire here so
            // each sibling window owns its own inspector card and the
            // singleton no longer blocks parallel siblings.
            ApplyInspectorVisibleToColumn(desiredVisible);
            Rail.SetInspectorVisibleGlyph(desiredVisible);

            var cfg = ConfigManager.Current;
            cfg.ArchitectInspectorVisible = desiredVisible;
            ConfigManager.SaveDeferred(Paths.AppConfigJson);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ArchitectSiblingWindow",
                "OnInspectorToggleRequested", ex);
        }
    }

    /// <summary>
    /// Public load entry used by <see cref="ArchitectWindowRegistry.OpenFileAsync"/>
    /// — loads the graph and binds the window's identity for state
    /// persistence + registry dedup. Returns false on a parse / IO failure
    /// (ArchitectViewModel.OpenAsync already routes the cause through
    /// GraphSerializer.OnLoadFailed → status bar).
    ///
    /// Was synchronous; now awaits ArchitectViewModel.OpenAsync so
    /// the GraphSerializer load + wildcard cascade no longer block the UI
    /// thread (and the deadlock-prone sync shim could be deleted).
    /// </summary>
    public async System.Threading.Tasks.Task<bool> LoadGraphAsync(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return false;
        bool ok = await _viewModel.OpenAsync(absolutePath).ConfigureAwait(true);
        if (ok)
        {
            // TouchDeferred offloads the recent-files
            // disk write to a background thread — parity with MainView (which
            // already uses the deferred variant). Synchronous Touch() here ran
            // a blocking JSON read+write on the UI thread right after the load
            // and froze under OneDrive / AV latency.
            RecentFiles.TouchDeferred(absolutePath);
            // Multi-window restore — record this sibling in the
            // RecentSiblings store so Hub's MainWindow.Loaded can
            // replay it on next boot. Untitled / not-yet-saved siblings are
            // skipped (the store filters empty paths).
            RecentSiblingsStore.Touch(absolutePath);
        }
        UpdateCaptionFromState();
        return ok;
    }

    /// <summary>
    /// True once the window has unsaved edits. Mirrors
    /// <see cref="ArchitectViewModel.IsDirty"/>; used by the close prompt.
    /// </summary>
    public bool IsDirty => _viewModel.IsDirty;

    /// <summary>
    /// Internal-by-spirit
    /// accessor used by <see cref="ArchitectWindowRegistry.ActivateOwnerOf"/>
    /// to match a SubGraphWindow's parent AVM against its sibling Window.
    /// Public so a partial class / cross-namespace helper can read it
    /// without exposing the private field; not meant for general consumers.
    /// </summary>
    public ArchitectViewModel GetViewModelForRegistry() => _viewModel;

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // Unhook the accelerator focus gate FIRST — it subscribes the static
        // FocusManager.GotFocus event, which would keep the closed window
        // reachable (leak) and keep toggling dead accelerators.
        try { _menuAccelGate.Detach(); } catch { /* best-effort */ }

        // Persist final geometry on real close (cancelled closes already
        // persisted in OnAppWindowClosing).
        // flushSync so the write lands even if the host process exits
        // immediately after this terminal close (Environment.Exit would kill a
        // queued async write).
        try { SiblingWindowStateStore.Persist(this, ResolvePersistKey(), flushSync: true); }
        catch { /* best-effort */ }

        // Drop from the multi-window restore store so a
        // deliberately-closed sibling doesn't re-open on next Hub boot.
        // Crashes / forced exits leave the entry behind on purpose — that's
        // the "restore last session" behaviour the boot replay relies on.
        try
        {
            string? path = _viewModel.LoadedFilePath;
            if (!string.IsNullOrEmpty(path)) RecentSiblingsStore.Remove(path);
        }
        catch { /* best-effort */ }

        try { ArchitectWindowRegistry.Unregister(this); } catch { /* best-effort */ }
        try { GraphSerializer.OnLoadFailed -= OnLoadFailed; } catch { /* best-effort */ }

        try
        {
            if (_appWindow is not null)
            {
                _appWindow.Closing -= OnAppWindowClosing;
                _appWindow = null;
            }
        }
        catch { /* best-effort */ }

        try { Chrome.Unbind(); } catch { /* best-effort */ }

        // Unhook rail-driven toggles + close the floating
        // Inspector window (only when THIS sibling owned the open inspector;
        // a different sibling may have re-bound the singleton in the
        // meantime). The InspectorWindow.CloseIfOpen call is benign when no
        // window is up.
        try { Rail.RailCollapseToggled      -= OnRailCollapseToggled; }      catch { /* best-effort */ }
        try { Rail.InspectorToggleRequested -= OnInspectorToggleRequested; } catch { /* best-effort */ }
        // Unhook F4 canvas inspector toggle bridge.
        try { CanvasView.InspectorToggleRequested -= OnCanvasInspectorToggleRequested; } catch { /* best-effort */ }
        try { CanvasView.UndoRedoChanged -= OnSiblingUndoRedoChanged; } catch { /* best-effort */ }
        try
        {
            var inspectorWin =
                Phoenix.Controls.Architect.WinUI.Hosting.InspectorWindow.CurrentInstance;
            // Best-effort: close the inspector only when this sibling is the
            // last one to have bound it. The InspectorWindow itself owns one
            // singleton across the process, so closing it here is safe even
            // if another sibling later wants to re-open it — that will spawn
            // a fresh window via OpenFor.
            if (inspectorWin is not null)
            {
                Phoenix.Controls.Architect.WinUI.Hosting.InspectorWindow.CloseIfOpen();
            }
        }
        catch { /* best-effort */ }

        try { _autosave?.Dispose(); _autosave = null; } catch { /* best-effort */ }
        try { _viewModel.Dispose(); } catch { /* best-effort */ }
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        try { SiblingWindowStateStore.Persist(this, ResolvePersistKey()); }
        catch { /* best-effort */ }

        if (_confirmedClose) return;
        if (!_viewModel.IsDirty) return;
        if (_promptInFlight) { args.Cancel = true; return; }

        args.Cancel = true;
        _promptInFlight = true;

        try
        {
            bool proceed = await PromptSaveBeforeCloseAsync();
            if (proceed)
            {
                _confirmedClose = true;
                DispatcherQueue?.TryEnqueue(Close);
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ArchitectSiblingWindow", "OnAppWindowClosing", ex);
        }
        finally
        {
            _promptInFlight = false;
        }
    }

    private async Task<bool> PromptSaveBeforeCloseAsync()
    {
        if (Content is not FrameworkElement root || root.XamlRoot is null)
            return true;

        var dlg = new ContentDialog
        {
            Title = Localizer.T("architect.dialog.unsaved_changes.title", "Unsaved changes"),
            Content = Localizer.T("architect.dialog.unsaved_changes.content.window",
                "Save changes to this graph before closing the window?"),
            PrimaryButtonText = Localizer.T("common.button.save", "Save"),
            SecondaryButtonText = Localizer.T("common.button.discard", "Discard"),
            CloseButtonText = Localizer.T("common.button.cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root.XamlRoot,
        };
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await OnFileSaveAsync();
            return !_viewModel.IsDirty;
        }
        return result == ContentDialogResult.Secondary;
    }

    // ─── ViewModel ↔ chrome sync ────────────────────────────────────────

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ArchitectViewModel.LoadedFilePath)
            || e.PropertyName == nameof(ArchitectViewModel.IsDirty))
        {
            UpdateCaptionFromState();
        }
        // Keep the Live Debug toggle in sync with the VM so
        // chord-driven toggles (Ctrl+Shift+D via the menu accelerator) or
        // any other surface flipping the flag re-paint the checkbox.
        if (e.PropertyName == nameof(ArchitectViewModel.LiveDebugEnabled))
        {
            try { MenuViewLiveDebug.IsChecked = _viewModel.LiveDebugEnabled; }
            catch { /* control may not be loaded yet */ }
        }
    }

    private void UpdateCaptionFromState()
    {
        string? path = _viewModel.LoadedFilePath;
        if (string.IsNullOrEmpty(path))
        {
            CaptionFileName.Text = _viewModel.IsDirty ? "• (unsaved)" : "(unsaved)";
            CaptionFilePath.Text = string.Empty;
            Title = _viewModel.IsDirty ? "• (unsaved) — Architect" : "(unsaved) — Architect";
            return;
        }

        string fname = Path.GetFileName(path) ?? path;
        string folder = Path.GetDirectoryName(path) ?? string.Empty;
        if (_viewModel.IsDirty) fname = "• " + fname;
        CaptionFileName.Text = fname;
        CaptionFilePath.Text = folder;
        Title = $"{fname} — Architect";
    }

    private string ResolvePersistKey()
    {
        // Persist per loaded path; until the window has a path it just
        // round-trips the empty key and SiblingWindowStateStore returns
        // its default-size launch. Once saved, subsequent persist calls
        // cement the new key.
        string? path = _viewModel.LoadedFilePath;
        if (!string.IsNullOrEmpty(path))
        {
            _persistKey = path;
            return path!;
        }
        return _persistKey ?? string.Empty;
    }

    // ─── Status bar ─────────────────────────────────────────────────────

    private void RefreshStatusCounts()
    {
        try
        {
            var g = _viewModel.LogicCanvas.Graph;
            StatusRight.Text = $"{g.Nodes.Count} nodes · {g.Links.Count} links";
        }
        catch
        {
            StatusRight.Text = string.Empty;
        }
    }

    private void SetStatus(string text, ArchitectStatusLight light = ArchitectStatusLight.None)
    {
        StatusLeft.Text = text ?? string.Empty;
        string? brushKey = light switch
        {
            ArchitectStatusLight.Green  => "OkBrush",
            ArchitectStatusLight.Yellow => "WarnBrush",
            ArchitectStatusLight.Red    => "ErrBrush",
            _                           => null,
        };
        if (brushKey is not null
            && Application.Current?.Resources is { } res
            && res.TryGetValue(brushKey, out var resource)
            && resource is Microsoft.UI.Xaml.Media.Brush b)
        {
            StatusLight.Fill = b;
        }
    }

    private void OnLoadFailed(string filePath, Exception ex)
    {
        // Only this window's load attempts care; filter by current path so a
        // sister window's parse failure doesn't bleed into our status bar.
        if (!string.Equals(filePath, _viewModel.LoadedFilePath, StringComparison.OrdinalIgnoreCase))
            return;
        DispatcherQueue.TryEnqueue(() =>
        {
            string fname = string.IsNullOrEmpty(filePath) ? "(unknown)" : Path.GetFileName(filePath);
            SetStatus($"Failed to load {fname} — {ex.GetType().Name}", ArchitectStatusLight.Red);
        });
    }

    // ─── Menu handlers ──────────────────────────────────────────────────

    /// <summary>
    /// Focus gate for this window's menu accelerators — same contract as
    /// <c>ArchitectChrome.OnMenuAcceleratorInvoked</c>. A window-scoped
    /// <c>KeyboardAccelerator</c> fires regardless of keyboard focus, so
    /// typing "c" / "f" into a value pill dropped a comment frame / framed the
    /// viewport, Ctrl+Z ran the graph undo mid-edit, and Ctrl+W could close
    /// the window under the user's cursor. While a text-input control has
    /// focus the menu action is suppressed; the keystroke still reaches the
    /// focused control. Ctrl+S / Ctrl+Shift+S / Ctrl+O stay ungated (canvas-
    /// guard parity: saving/opening mid-edit is allowed).
    /// </summary>
    private void OnMenuAcceleratorInvoked(
        Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Canvas.TextInputFocusGuard.IsTextInputFocused(Content?.XamlRoot)
            || Canvas.InlineEditGate.IsActive)
            args.Handled = true;
    }

    private void OnFileNewClicked(object sender, RoutedEventArgs e)
    {
        // A POPULATED sibling canvas spawns another sibling
        // (never clobber it, per the 0.10.0 multi-window model). But a BLANK
        // sibling (Welcome card up) starts the new graph IN PLACE — otherwise
        // the Welcome card's "New Graph" button on a blank sibling spawns an
        // empty twin window that shows the same Welcome card ("the same issue
        // continues"). Symmetric with MainView.OnFileNewRequested and the
        // File → Open "fill the blank window" idiom.
        bool blank = string.IsNullOrEmpty(_viewModel.LoadedFilePath)
                  && (_viewModel.LogicCanvas?.Graph?.Nodes?.Count ?? 0) == 0;
        if (blank)
        {
            _viewModel.NewGraph();
            CanvasView.BeginBlankCanvasFromNew();
            return;
        }
        ArchitectWindowRegistry.OpenNew();
    }

    private async void OnFileOpenClicked(object sender, RoutedEventArgs e)
        => await OnFileOpenAsync();

    private Task OnFileOpenAsync()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            string startDir = ResolveStartDir();
            string? path = CustomFilePicker.PickSingleFile(
                hwnd,
                startDir,
                new[] { ("Phoenix Graph", ".phxg") });
            if (string.IsNullOrEmpty(path)) return Task.CompletedTask;
            SpawnOrFocusSibling(path);
            RememberArchitectDir(path);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ArchitectSiblingWindow", "File → Open", ex);
        }
        return Task.CompletedTask;
    }

    // Reusable helper invoked by both the File → Open Recent menu
    // and the WelcomeCard's "Recent…" button — same dialog + spawn flow.
    private async Task OpenRecentFromCanvasAsync()
    {
        try
        {
            var dlg = new Dialogs.RecentFilesDialog
            {
                XamlRoot = (Content as FrameworkElement)?.XamlRoot,
            };
            await dlg.ShowAsync();
            string? picked = dlg.PickedPath;
            if (string.IsNullOrEmpty(picked)) return;
            if (!File.Exists(picked))
            {
                RecentFiles.Remove(picked);
                SetStatus($"Recent entry missing: {Path.GetFileName(picked)}", ArchitectStatusLight.Yellow);
                return;
            }
            SpawnOrFocusSibling(picked);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ArchitectSiblingWindow", "WelcomeCard → Recent", ex);
        }
    }

    private async void OnFileOpenRecentClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Dialogs.RecentFilesDialog
            {
                XamlRoot = (Content as FrameworkElement)?.XamlRoot,
            };
            await dlg.ShowAsync();
            string? picked = dlg.PickedPath;
            if (string.IsNullOrEmpty(picked)) return;
            if (!File.Exists(picked))
            {
                RecentFiles.Remove(picked);
                SetStatus($"Recent entry missing: {Path.GetFileName(picked)}", ArchitectStatusLight.Yellow);
                return;
            }
            SpawnOrFocusSibling(picked);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ArchitectSiblingWindow", "File → Open Recent", ex);
        }
    }

    private void SpawnOrFocusSibling(string path)
    {
        // OpenFile is async; caller (recent-files menu, canvas drop)
        // is sync void, so fire-and-forget through AsyncErrorBoundary.
        // Route through SpawnOrFocusSiblingAsync (not OpenFileAsync
        // directly) so a parse / I/O / window-creation failure surfaces as a
        // red status line. Pre-fix the bare OpenFileAsync result was discarded
        // (_ =) and OnLoadFailed's LoadedFilePath-equality filter rejected
        // pre-load failures (LoadedFilePath is null until a load succeeds), so
        // a failed open in a NEW sibling produced no user-visible feedback.
        _ = Phoenix.Controls.Shared.Core.AsyncErrorBoundary.SafeRunAsync(
            () => SpawnOrFocusSiblingAsync(path),
            "ArchitectSiblingWindow",
            $"SpawnOrFocusSibling '{path}'");
    }

    /// <summary>
    /// Awaitable spawn-or-focus. Returns true when the target window
    /// is open + focused; false (with a red status line on THIS window) when
    /// the open failed. The status line lives on the originating window so the
    /// user gets feedback even though the failed sibling never displayed.
    /// </summary>
    private async Task<bool> SpawnOrFocusSiblingAsync(string path)
    {
        var spawned = await ArchitectWindowRegistry.OpenFileAsync(path).ConfigureAwait(true);
        if (spawned is null)
        {
            string fname = string.IsNullOrEmpty(path) ? "(unknown)" : Path.GetFileName(path);
            DispatcherQueue?.TryEnqueue(() => SetStatus(
                $"Failed to open {fname} — file unreadable or malformed. See System Log.",
                ArchitectStatusLight.Red));
            return false;
        }
        return true;
    }

    private async void OnFileSaveClicked(object sender, RoutedEventArgs e)
        => await OnFileSaveAsync();

    private async Task OnFileSaveAsync()
    {
        try
        {
            if (!await ConfirmSaveValidationAsync())
            {
                SetStatus("Save cancelled — validation rejected.", ArchitectStatusLight.Yellow);
                return;
            }

            if (!string.IsNullOrEmpty(_viewModel.LoadedFilePath))
            {
                await _viewModel.SaveAsync();
                // Deferred recent-files write — see LoadGraphAsync.
                RecentFiles.TouchDeferred(_viewModel.LoadedFilePath!);
                // Refresh the multi-window restore entry so its
                // LastOpenUtc timestamp tracks active editing — Hub's replay
                // walks newest-first.
                RecentSiblingsStore.Touch(_viewModel.LoadedFilePath!);
                SetStatus($"Saved {Path.GetFileName(_viewModel.LoadedFilePath)}.", ArchitectStatusLight.Green);
                UpdateCaptionFromState();
                ArchitectWindowRegistry.Rebind(this, _viewModel.LoadedFilePath);
                return;
            }

            await OnFileSaveAsAsync(validated: true);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ArchitectSiblingWindow", "File → Save", ex);
            SetStatus("Save failed — see System Log.", ArchitectStatusLight.Red);
        }
    }

    private async void OnFileSaveAsClicked(object sender, RoutedEventArgs e)
        => await OnFileSaveAsAsync(validated: false);

    private async Task OnFileSaveAsAsync(bool validated)
    {
        try
        {
            // OnFileSaveAsync already ran validation when it routed here on the
            // no-path-yet branch; the user-initiated Save As entry didn't.
            if (!validated && !await ConfirmSaveValidationAsync())
            {
                SetStatus("Save cancelled — validation rejected.", ArchitectStatusLight.Yellow);
                return;
            }

            var hwnd = WindowNative.GetWindowHandle(this);
            string startDir = ResolveStartDir();
            string suggested = string.IsNullOrEmpty(_viewModel.LoadedFilePath)
                ? "graph"
                : Path.GetFileNameWithoutExtension(_viewModel.LoadedFilePath!);
            string? path = CustomFilePicker.PickSaveFile(
                hwnd,
                startDir,
                suggested,
                new[] { ("Phoenix Graph", ".phxg") });
            if (string.IsNullOrEmpty(path)) return;
            await _viewModel.SaveAsync(path);
            // Deferred recent-files write — see LoadGraphAsync.
            RecentFiles.TouchDeferred(path);
            // Save As migrates the persisted-restore entry to the
            // new path so a Hub restart re-opens the file under its new name.
            RecentSiblingsStore.Touch(path);
            RememberArchitectDir(path);
            SetStatus($"Saved {Path.GetFileName(path)}.", ArchitectStatusLight.Green);
            UpdateCaptionFromState();
            ArchitectWindowRegistry.Rebind(this, path);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ArchitectSiblingWindow", "File → Save As", ex);
            SetStatus("Save As failed — see System Log.", ArchitectStatusLight.Red);
        }
    }

    /// <summary>
    /// Per-sibling save-validation flow. Mirrors MainView's behaviour:
    /// errors block save behind a modal; warnings flow into System Log
    /// and the status bar turns yellow but the save proceeds (warning-only
    /// modals on every save were a confirmed regression).
    /// </summary>
    private async Task<bool> ConfirmSaveValidationAsync()
    {
        try
        {
            var results = GraphValidator.Validate(_viewModel.LogicCanvas.Graph);
            if (results.Count == 0) return true;

            int errorCount   = 0;
            int warningCount = 0;
            foreach (var v in results)
            {
                bool isErr = v.Severity == ValidationSeverity.Error;
                if (isErr) errorCount++; else warningCount++;
                GlobalLogger.Log(
                    v.ToString(),
                    "Architect.SaveValidator",
                    isErr ? LogLevel.CriticalError : LogLevel.System);
            }

            if (errorCount > 0)
            {
                var root = (Content as FrameworkElement)?.XamlRoot;
                if (root is not null)
                {
                    // Dialog button contract: Primary="Save anyway",
                    // Secondary="Cancel" (matches WinUI convention — Primary =
                    // the affirmative action). Result==Primary means proceed.
                    var dlg = Dialogs.SaveValidationDialog.ForResults(root, results);
                    var result = await dlg.ShowAsync();
                    if (result != ContentDialogResult.Primary) return false;
                }
                // No XamlRoot — fall through to "save anyway" path; the
                // errors are in System Log so the user still has a paper
                // trail.
            }

            string summary = errorCount > 0
                ? $"Saved with {errorCount} error(s) + {warningCount} warning(s) — see System Log."
                : $"Saved with {warningCount} warning(s) — see System Log.";
            SetStatus(summary, ArchitectStatusLight.Yellow);
            return true;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ArchitectSiblingWindow",
                "validation failed; falling through to save", ex);
            return true;
        }
    }

    private async void OnFileExportClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(_viewModel.LoadedFilePath))
            {
                SetStatus("Save the graph first; export needs a path.", ArchitectStatusLight.Yellow);
                return;
            }
            // Async export path; pre-fix this ran
            // the exporter walk + disk write on the UI thread.
            await _viewModel.ExportPhxBesideAsync(_viewModel.LoadedFilePath!).ConfigureAwait(true);
            SetStatus($"Exported {Path.GetFileNameWithoutExtension(_viewModel.LoadedFilePath)}.phx.",
                ArchitectStatusLight.Green);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ArchitectSiblingWindow", "File → Export", ex);
            SetStatus("Export failed — see System Log.", ArchitectStatusLight.Red);
        }
    }

    private void OnFileCloseClicked(object sender, RoutedEventArgs e)
    {
        try { Close(); } catch { /* best-effort */ }
    }

    // File → Welcome parity with the embedded
    // ArchitectChrome. Force-shows the always-available Welcome card overlay on
    // the canvas. Non-destructive: the open graph stays loaded behind the card
    // (RequestShowWelcomeFromShell never mutates or closes it).
    private void OnFileWelcomeClicked(object sender, RoutedEventArgs e)
        => CanvasView.RequestShowWelcomeFromShell();

    /// <summary>
    /// 0.11.x polish — × glyph next to the sibling-window file caption.
    /// Closes the whole window (a sibling with no file is dead weight);
    /// the OnClosed cascade in the base class handles dirty-prompting.
    /// </summary>
    private void OnCaptionCloseFileClicked(object sender, RoutedEventArgs e)
    {
        try { Close(); } catch { /* best-effort */ }
    }

    // Reflect the stack depth onto this sibling
    // window's own Edit menu.
    private void OnSiblingUndoRedoChanged(object? sender, EventArgs e) => RefreshEditMenuEnabled();
    private void RefreshEditMenuEnabled()
    {
        try
        {
            if (MenuEditUndo is not null) MenuEditUndo.IsEnabled = CanvasView.CanUndo;
            if (MenuEditRedo is not null) MenuEditRedo.IsEnabled = CanvasView.CanRedo;
        }
        catch { /* best-effort */ }
    }

    private void OnEditUndoClicked(object sender, RoutedEventArgs e)
        => CanvasView.RequestUndoFromShell();

    private void OnEditRedoClicked(object sender, RoutedEventArgs e)
        => CanvasView.RequestRedoFromShell();

    private void OnEditFindClicked(object sender, RoutedEventArgs e)
        => CanvasView.RequestFindNodeFromShell();

    // Group + Comment Frame parity with ArchitectChrome's chords.
    private void OnEditGroupClicked(object sender, RoutedEventArgs e)
        => CanvasView.RequestGroupFromShell();

    private void OnEditCommentFrameClicked(object sender, RoutedEventArgs e)
        => CanvasView.RequestAddCommentFrameFromShell();

    private void OnViewFrameSelectionClicked(object sender, RoutedEventArgs e)
        => CanvasView.RequestFrameSelectionFromShell();

    private void OnViewShowGridClicked(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem t)
            _viewModel.LogicCanvas.ShowGrid = t.IsChecked;
    }

    // Minimap visibility toggle. Mirrors the MainView path:
    // SetMinimapVisible flips Visibility AND persists into AppConfig.
    // CanvasView.MinimapVisibilityChanged fires back when the in-overlay
    // × glyph is used, so we subscribe in OnActivatedOnce to keep the
    // toggle's IsChecked in sync.
    private void OnViewMinimapClicked(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem t)
            CanvasView.SetMinimapVisible(t.IsChecked);
    }

    private void OnCanvasMinimapVisibilityChanged(object? sender, bool visible)
    {
        try { if (MenuViewMinimap is not null) MenuViewMinimap.IsChecked = visible; }
        catch { /* best-effort */ }
    }

    // Live Debug toggle — flips ArchitectViewModel.LiveDebugEnabled,
    // which the canvas's NODE_EXEC subscriber and the bus client both watch.
    // The ToggleMenuFlyoutItem.IsChecked reflects the persisted state; keep
    // it in sync via the VM PropertyChanged path in OnViewModelPropertyChanged.
    private void OnViewLiveDebugClicked(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem t)
            _viewModel.SetLiveDebugEnabled(t.IsChecked);
    }

    // Spawn-palette parity — mirrors ArchitectChrome's Ctrl+Space.
    private void OnViewSpawnPaletteClicked(object sender, RoutedEventArgs e)
        => CanvasView.RequestSpawnAtViewCenterFromShell();

    private void OnHelpNodeRefClicked(object sender, RoutedEventArgs e)
    {
        // NodeDocumentationDialog (modal ContentDialog) was
        // replaced by NodeDocumentationWindow (top-level singleton Window) so it
        // doesn't block canvas interaction. Use the OpenOrFocus helper the rest
        // of the suite (MainView, LogicCanvasView.Menus) already routes through.
        try
        {
            Phoenix.Controls.Architect.WinUI.Hosting.NodeDocumentationWindow.OpenOrFocus();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ArchitectSiblingWindow", "Help → Node Reference", ex);
        }
    }

    // Script → Sync Event Peers. Routes through the
    // canvas's existing debounced cross-file sync timer; the same path
    // wire-drop / event-rename edits use.
    private void OnScriptSyncEventPeersClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            CanvasView.RequestSyncEventPeersFromShell();
            SetStatus("Sync Event Peers requested.", ArchitectStatusLight.Green);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ArchitectSiblingWindow", "Script → Sync Event Peers", ex);
            SetStatus("Sync Event Peers failed — see System Log.", ArchitectStatusLight.Red);
        }
    }

    // Help → Keyboard Shortcuts… opens the dialog.
    private async void OnHelpShortcutsClicked(object sender, RoutedEventArgs e)
        => await ShowKeyboardShortcutsDialogAsync();

    /// <summary>
    /// Shared dialog-launch helper — called by Help → Keyboard Shortcuts…
    /// and by the canvas's <c>KeyboardShortcutsRequested</c> event
    /// (raised by F1-without-selection).
    /// </summary>
    private async System.Threading.Tasks.Task ShowKeyboardShortcutsDialogAsync()
    {
        var root = Content?.XamlRoot;
        await Phoenix.Controls.Architect.WinUI.Dialogs.KeyboardShortcutsDialog.ShowAsync(root!);
    }

    // ─── Restore / Bookmarks parity with embedded ArchitectChrome ──

    /// <summary>
    /// File → Restore previous version… — picker over the rolling
    /// .phxg.bak[1-3] backups beside the loaded file. Mirrors
    /// MainView.RestoreFromBackupAsync (the embedded-chrome path); the body is
    /// duplicated here (chrome/window helpers stay per-window) rather than
    /// lifted to a shared
    /// module. No-backup / no-file cases log via GlobalLogger (not a modal,
    /// since the rejection is repeatable).
    /// </summary>
    private async void OnFileRestoreClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(_viewModel.LoadedFilePath))
            {
                GlobalLogger.Log(
                    "Restore previous version: no file is loaded (save once to seed backups).",
                    "ArchitectSiblingWindow", LogLevel.System);
                SetStatus("Restore: save the graph once to seed backups.", ArchitectStatusLight.Yellow);
                return;
            }

            var backups = GraphSerializer.ListBackups(_viewModel.LoadedFilePath!);
            if (backups.Count == 0)
            {
                GlobalLogger.Log(
                    $"Restore previous version: no .phxg.bak[1-3] entries beside '{Path.GetFileName(_viewModel.LoadedFilePath)}' yet.",
                    "ArchitectSiblingWindow", LogLevel.System);
                SetStatus("Restore: no backup versions found yet.", ArchitectStatusLight.Yellow);
                return;
            }

            var root = (Content as FrameworkElement)?.XamlRoot;
            if (root is null) return;

            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                Margin = new Thickness(0, 8, 0, 0),
                MinWidth = 360,
            };
            foreach (var b in backups)
            {
                var label = $"bak{b.Slot} — {b.LastWriteUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
                list.Items.Add(new ListViewItem
                {
                    Content = label,
                    Tag = b,
                    // "MonoFont" is an <x:String> resource — cast to
                    // FontFamily throws; build from the string like the XAML converter.
                    FontFamily = Application.Current.Resources["MonoFont"] is string monoFamily
                                 ? new Microsoft.UI.Xaml.Media.FontFamily(monoFamily)
                                 : new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    FontSize = 12,
                });
            }
            if (list.Items.Count > 0) list.SelectedIndex = 0;

            var dlg = new ContentDialog
            {
                Title = $"Restore '{Path.GetFileName(_viewModel.LoadedFilePath)}' from backup",
                PrimaryButtonText = "Restore",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = root,
                Content = list,
            };
            var result = await dlg.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            if (list.SelectedItem is not ListViewItem picked
                || picked.Tag is not GraphSerializer.BackupCandidate b2)
            {
                return;
            }

            bool ok = GraphSerializer.RestoreBackup(_viewModel.LoadedFilePath!, b2.Path);
            if (!ok)
            {
                SetStatus($"Restore from '{Path.GetFileName(b2.Path)}' failed — see System Log.",
                    ArchitectStatusLight.Red);
                return;
            }

            var reloaded = await _viewModel.OpenAsync(_viewModel.LoadedFilePath!).ConfigureAwait(true);
            SetStatus(reloaded
                ? $"Restored from {Path.GetFileName(b2.Path)} (bak{b2.Slot})."
                : $"Restored '{Path.GetFileName(b2.Path)}' but reload failed — see System Log.",
                reloaded ? ArchitectStatusLight.Green : ArchitectStatusLight.Yellow);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ArchitectSiblingWindow", "File → Restore previous version", ex);
            SetStatus("Restore failed — see System Log.", ArchitectStatusLight.Red);
        }
    }

    /// <summary>
    /// View → Bookmarks legend… — opens the 9-slot bookmark legend
    /// flyout on the canvas (Ctrl/Alt+1..9 chord hints). Same canvas method
    /// the embedded ArchitectChrome routes through, restoring sibling-window
    /// menu parity.
    /// </summary>
    private void OnViewBookmarksClicked(object sender, RoutedEventArgs e)
    {
        try { CanvasView.ShowBookmarkLegendFlyout(); }
        catch (Exception ex)
        {
            GlobalLogger.Error("ArchitectSiblingWindow", "View → Bookmarks legend", ex);
        }
    }

    // ─── Last-used directory ─────────────────────────────────────────────

    private static string ResolveStartDir()
    {
        string? recall = ConfigManager.Current.LastArchitectOpenDir;
        if (!string.IsNullOrWhiteSpace(recall) && Directory.Exists(recall))
            return recall;
        return Paths.HubLogic;
    }

    private static void RememberArchitectDir(string filePath)
    {
        try
        {
            string dir = Path.GetDirectoryName(filePath) ?? string.Empty;
            if (string.IsNullOrEmpty(dir)) return;
            var cfg = ConfigManager.Current;
            if (string.Equals(cfg.LastArchitectOpenDir, dir, StringComparison.OrdinalIgnoreCase)) return;
            cfg.LastArchitectOpenDir = dir;
            // Deferred config write — see OnRailCollapseToggled.
            ConfigManager.SaveDeferred(Paths.AppConfigJson);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ArchitectSiblingWindow", "RememberArchitectDir", ex);
        }
    }
}
