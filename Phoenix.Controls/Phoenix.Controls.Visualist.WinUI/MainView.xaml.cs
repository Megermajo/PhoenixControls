using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Linq;
using System.Text;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.WinUI.Contracts;
using Phoenix.Controls.Shared.WinUI.Services;
using Phoenix.Controls.Visualist.Core;
using Phoenix.Controls.Visualist.WinUI.Canvas;
using Phoenix.Controls.Visualist.WinUI.ViewModels;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Phoenix.Controls.Visualist.WinUI;

// Single-HUB collapse: Visualist now ships as a UserControl
// embedded in Hub.MainWindow's pillar-tab region rather than a standalone
// Window. Host Hub passes its Window in via the ctor for picker HWNDs;
// the SystemBackdrop on the old Window moves up to Hub's MainWindow.
public sealed partial class MainView : UserControl, IPillarShellHost, ICanExecuteUndoRedo
{
    public VisualistViewModel ViewModel { get; } = new();

    /// <inheritdoc/>
    public bool IsDirty => ViewModel.IsDirty;

    /// <inheritdoc/>
    public string CurrentDocumentDisplayName =>
        string.IsNullOrEmpty(ViewModel.ActiveLayerFileName)
            ? "(unsaved)"
            : ViewModel.ActiveLayerFileName;

    /// <inheritdoc/>
    public event System.EventHandler? ShellStateChanged;

    /// <inheritdoc/>
    /// <remarks>
    /// Visualist's prompt: a three-button ContentDialog (Save / Don't Save /
    /// Cancel). Save routes through the same SaveLayer / SaveLayerAs path
    /// the chrome uses; Cancel returns false so MainWindow cancels the
    /// AppWindow.Closing.
    /// </remarks>
    public async Task<bool> PromptSaveBeforeCloseAsync()
    {
        if (!IsDirty || ViewModel.Document is null) return true;
        if (XamlRoot is null) return true;

        string fileName = ViewModel.ActiveLayerFileName;

        // The prompt only ever guards the ACTIVE document, but on close the VM
        // also flushes any *saved-dirty* cached layers from this session
        // (VisualistViewModel.Dispose → FlushAndDispose: a dirty doc that still
        // has a FilePath is written to disk, an unsaved scratch doc is logged
        // not lost). Pre-fix the dialog said only "Save changes to <active>?",
        // so a user who had edited and switched away from other layers had no
        // idea those were about to be auto-saved. We can't read the private
        // cache count from here (the cache lives in the VM) without crossing
        // the owned-file boundary, so we surface the
        // behaviour generically: a single extra line stating that other
        // unsaved layers from this session are auto-saved on close.
        string content = string.Format(
            Localizer.T("visualist.dialog.unsaved_layer.content_format",
                "Save changes to \"{0}\" before closing?"),
            fileName)
            + System.Environment.NewLine + System.Environment.NewLine
            + Localizer.T("visualist.dialog.unsaved_layer.cached_note",
                "Any other layers you edited this session are auto-saved on close.");

        var dlg = new ContentDialog
        {
            XamlRoot          = XamlRoot,
            Title             = Localizer.T("visualist.dialog.unsaved_layer.title", "Unsaved layer"),
            Content           = content,
            PrimaryButtonText = Localizer.T("common.button.save", "Save"),
            SecondaryButtonText = Localizer.T("common.button.dont_save", "Don't Save"),
            CloseButtonText   = Localizer.T("common.button.cancel", "Cancel"),
            DefaultButton     = ContentDialogButton.Primary,
        };
        var res = await dlg.ShowAsync();
        switch (res)
        {
            case ContentDialogResult.Primary:
                if (ViewModel.Document.FilePath is null)
                {
                    await ShowSaveAsDialogAndSave();
                    // If user cancelled the picker, the doc stays dirty —
                    // treat that as "abort the close".
                    return !ViewModel.IsDirty;
                }
                // Capture before save so the close-prompt path also
                // refreshes thumbnails.
                await CaptureLiveThumbnailsAsync();
                // Surface a failed save on the status
                // strip; the false return already aborts the close so the user
                // stays in the document, but pre-fix they got no visible reason.
                bool saved = ViewModel.SaveLayer();
                if (!saved) ShowSaveFailedStatus();
                return saved;
            case ContentDialogResult.Secondary:
                return true;
            default:
                return false;
        }
    }

    private readonly Window _hostWindow;
    private readonly LayerCanvasView _canvasView;
    private readonly WidgetEditorView _editorView;
    // Narrowed from IHubServices? to the single capability we actually
    // consume — live-layer presence dots on the LayerRail. Pre-fix the ctor
    // accepted the whole IHubServices bag (chat / livefeed / scripts / system
    // log) and threw away everything but .Layers; that gave Visualist visibility
    // into surfaces it has no business touching. Hub.MainWindow now passes
    // `services?.Layers` instead of the full IHubServices.
    private readonly ILayerRegistrySource? _layerSource;

    // stored chrome/status-bar PropertyChanged
    // handler so it can be unsubscribed on Unloaded (was an un-removable lambda).
    private System.ComponentModel.PropertyChangedEventHandler? _vmChromeBridge;

    public MainView(Window hostWindow, ILayerRegistrySource? layerSource = null)
    {
        _hostWindow  = hostWindow ?? throw new ArgumentNullException(nameof(hostWindow));
        _layerSource = layerSource;
        InitializeComponent();

        // Populate the widget-node catalog at pillar startup. Pre-fix the ONLY
        // non-test caller of RegisterAll() was Help → Compositor Reference, so on
        // a fresh Visualist session the WidgetNodeRegistry was EMPTY — the node
        // spawn palette had nothing in it and media drag-drop threw "Unknown node
        // template: 'Image.Load'" out of WidgetNodeRegistry.Instantiate (the
        // exception was swallowed, so the drop just silently no-op'd). RegisterAll
        // is idempotent (guards on its own _registered flag), so this is safe even
        // if something else already populated the catalog.
        try
        {
            if (WidgetNodeRegistry.Templates.Count == 0) NodeTemplates.RegisterAll();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist.MainView", "NodeTemplates.RegisterAll", ex);
        }

        // Hub.MainWindow's HubChrome owns the only visible chrome — there is
        // no pillar-local chrome control. The public action methods on this
        // MainView (NewLayer, OpenLayerDialog, …) are the only entry points;
        // Hub's MenuDefinition.Visualist dispatches into them via
        // OnMenuItemInvoked.

        _canvasView = new LayerCanvasView { DataContext = ViewModel };
        _editorView = new WidgetEditorView { DataContext = ViewModel };
        MainPaneRegion.Content = _canvasView;

        // Explorer file-drop on the layer canvas opens the dropped .phxlayer.
        // The canvas raises FileOpenRequested with the dropped path; we route
        // through OpenLayerRoutingThroughRegistry so a drop that targets a
        // file already open in another sibling window focuses that window
        // instead of duplicate-loading here.
        _canvasView.FileOpenRequested += (_, path) =>
        {
            try
            {
                OpenLayerRoutingThroughRegistry(path);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("Visualist", $"FileOpenRequested drop '{path}'", ex);
            }
        };

        // The canvas raises WidgetEnterRequested when
        // the user double-taps a widget body, presses Enter on the selection, or
        // picks "Edit Widget"; route it through the single EnterWidget chokepoint
        // so we swap to the Widget Editor sub-tab bound to that widget. Mirrors
        // the FileOpenRequested bubble pattern above.
        _canvasView.WidgetEnterRequested += (_, widget) => EnterWidget(widget);

        // Bridge VM property changes that affect the chrome filename / dirty
        // marker into the IPillarShellHost.ShellStateChanged event so Hub's
        // HubChrome.FileName tracks edits / loads / saves. Same VM signals
        // also repaint the local status bar (CHANGELOG 0.9.9 status-bar parity).
        // The chrome/status-bar bridge was once an anonymous lambda that could
        // never be unsubscribed, so a pillar-tab teardown leaked this MainView
        // through ViewModel.PropertyChanged. Store it in a field and drop it on
        // Unloaded (below).
        _vmChromeBridge = (_, e) =>
        {
            if (e.PropertyName == nameof(VisualistViewModel.IsDirty)
                || e.PropertyName == nameof(VisualistViewModel.ActiveLayerFileName))
            {
                ShellStateChanged?.Invoke(this, System.EventArgs.Empty);
                RefreshStatusBar();
            }
            else if (e.PropertyName == nameof(VisualistViewModel.Document)
                  || e.PropertyName == nameof(VisualistViewModel.SelectedLayer))
            {
                // Document swap / layer-instance re-fire — widget count + saved
                // state both depend on Document, so repaint the whole strip.
                RefreshStatusBar();
            }
        };
        ViewModel.PropertyChanged += _vmChromeBridge;

        // Attach the Hub-provided live-layer source so the
        // LayerRail's per-row dot reflects real OBS browser-source presence
        // instead of a hardcoded green. Must happen on the
        // UI thread (we are — ctor runs on the UI dispatcher) and before
        // RefreshLayers so the initial seed runs once against the freshly
        // populated rows. Falls through to a no-op when services is null
        // (design-time / fakes).
        ViewModel.Initialize(_layerSource);

        // Initial paint — VM is now wired, so reflect the empty/no-layer state
        // into the strip before the first VM PropertyChanged would fire.
        RefreshStatusBar();

        // The Visualist pillar MainView is created ONCE
        // and cached for the whole Hub session (MainWindow._visualistView).
        // Switching pillar tabs only removes it from the visual tree — that is a
        // tab-blur, NOT a pillar teardown. The old code subscribed Unloaded and
        // tore everything down on tab-blur: VisualistBusClient.Stop() +
        // ViewModel.Dispose(). Because the handler was one-shot, the FIRST time
        // the user left the Visualist tab the design-time bus link stopped (and
        // the VM was disposed) permanently for the rest of the session — exactly
        // Majo's "the BUS MUST run even when not in Visualist". The teardown now
        // lives in ShutdownPillar(), invoked once from Hub MainWindow's Closed
        // handler at real app shutdown, so the bus link, the VM, and the chrome /
        // undo-redo bridges all survive every tab round-trip. We deliberately do
        // NOT subscribe Unloaded.

        // The layer enumeration (Directory.EnumerateFiles
        // + N × LayerSerializer.Read) is deferred to InitializeAsync so the
        // synchronous MainView ctor return isn't gated on `data/layers/`
        // FS work. Loaded drives InitializeAsync below.
        Loaded += OnLoadedInit;

        // Seed the initial mode (Layer Canvas) through SelectSubTab — the single
        // source of truth for the MainPaneRegion swap, the LayerRail column
        // width/visibility, and the inspector's editor-context flag. (The shell
        // sub-tab ToggleButtons that used to mirror this were removed;
        // mode switching now routes through the View menu, the trigger strip's
        // ← Back, and double-click-to-enter.)
        SelectSubTab(showCanvas: true);
    }

    /// <summary>
    /// Real pillar teardown — invoked ONCE from Hub
    /// MainWindow's Closed handler at app shutdown, NOT on tab-blur. Symmetric to
    /// every subscription made in the ctor / EnsureUndoRedoForwarderSubscribed so
    /// app close releases the VM and its document graph cleanly. The
    /// VisualistBusClient stop lives here (not on Unloaded) so the design-time bus
    /// link runs for the whole session — Majo: "the BUS MUST run even when not in
    /// Visualist". Idempotent + guarded so a teardown fault can't crash app close.
    /// </summary>
    public void ShutdownPillar()
    {
        // Stop the design-time bus link only at real app shutdown.
        // Log the fault instead of a bare catch so a teardown failure is
        // visible for telemetry; the handler must not throw, so we log and proceed.
        try { Phoenix.Controls.Visualist.WinUI.Core.VisualistBusClient.Instance.Stop(); }
        catch (Exception ex) { GlobalLogger.Error("Visualist.MainView", "Shutdown bus stop", ex); }

        // Close the shared layer-preview popout so its WebView2 process
        // tears down with the pillar tab rather than lingering.
        try { _layerPreviewWindow?.Close(); }
        catch (Exception ex) { GlobalLogger.Error("Visualist.MainView", "Unloaded preview close", ex); }
        _layerPreviewWindow = null;

        if (_vmChromeBridge is not null)
        {
            ViewModel.PropertyChanged -= _vmChromeBridge;
            _vmChromeBridge = null;
        }

        // Undo/redo forwarder: drop the VM document-swap watcher and detach
        // from whatever LayerDocument we were last tracking.
        if (_undoRedoForwarderHooked)
        {
            ViewModel.PropertyChanged -= OnViewModelDocumentSwapped;
            _undoRedoForwarderHooked = false;
        }
        if (_undoRedoTrackedDocument is { } trackedDoc)
        {
            trackedDoc.CanExecuteChanged -= OnTrackedDocumentCanExecuteChanged;
            _undoRedoTrackedDocument = null;
        }

        // The VM teardown
        // (DetachLayerSource + Dispose, which flushes dirty cached docs and
        // tears down auto-save timers) is the heaviest step here. Pre-fix it
        // ran unguarded — a throw would propagate out of the Unloaded handler.
        // Guard + log so a teardown fault is captured for telemetry instead of
        // crashing the pillar-tab close, while still completing as much cleanup
        // as possible.
        try
        {
            ViewModel.DetachLayerSource();
            ViewModel.Dispose();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist.MainView", "Unloaded VM teardown", ex);
        }
    }

    private async void OnLoadedInit(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedInit;
        // Own the design-time bus link's
        // lifecycle at the pillar level so Test Run, live widget updates, and
        // LAYER_RELOADED preview refresh work whether or not the widget editor is
        // open. Start() is idempotent and is ALSO kicked
        // from Hub launch (MainWindow.StartPillarBusLinks) so the link is up
        // before Visualist is ever opened; this call stays as a defensive belt-
        // and-braces. The matching Stop() now lives in ShutdownPillar() (app
        // close only), NOT on tab-blur — the bus runs for the whole session.
        try { Phoenix.Controls.Visualist.WinUI.Core.VisualistBusClient.Instance.Start(); }
        catch (Exception ex) { GlobalLogger.Error("Visualist", "bus client start", ex); }
        // Proactively prune the Recent-Layers MRU of files that no
        // longer exist on disk, so the Open-Recent dialog / sibling menu never
        // list dead links (previously pruned only one-at-a-time on open).
        try { Phoenix.Controls.Visualist.WinUI.Services.RecentFiles.Prune(); }
        catch (Exception ex) { GlobalLogger.Error("Visualist", "RecentFiles prune", ex); }
        try { await InitializeAsync().ConfigureAwait(true); }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist", "OnLoadedInit", ex);
        }

        // Visualist must open onto an authorable canvas. When data/layers/ has
        // no files (or none auto-loaded), seed an untitled FullHD scratch layer
        // so the user can immediately right-click → Add Widget instead of
        // facing a dead "(no layer loaded)" stage with no way to create one.
        // NewLayer() leaves the doc un-dirty until first edit, so this doesn't
        // trigger a spurious save-on-close prompt.
        try
        {
            if (ViewModel.Document is null) ViewModel.NewLayer();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist", "OnLoadedInit default layer", ex);
        }
    }

    /// <summary>
    /// Async post-ctor init. The .phxlayer enumeration
    /// runs on a thread-pool worker so a cold-disk `data/layers/` doesn't
    /// stall the dispatcher during pillar-tab activation; the result
    /// (a LayerListItem list) is then applied back on the UI thread.
    /// Idempotent.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        // On an INSTALLED build Paths.HubLayers resolves
        // to a (frequently empty / not-yet-created) %AppData% subfolder. Make
        // sure the folder exists so the rail enumeration below has something to
        // read and the user's first Save/Save-As lands in a real directory
        // instead of faulting on a missing parent. Non-fatal: a failure here
        // (e.g. locked path) is logged and we proceed — enumeration just sees
        // no files and falls back to the seeded scratch layer.
        try
        {
            if (!Directory.Exists(Paths.HubLayers))
            {
                Directory.CreateDirectory(Paths.HubLayers);
                GlobalLogger.Log(
                    $"Created layers folder: {Paths.HubLayers}",
                    "Visualist", Phoenix.Controls.Shared.Models.LogLevel.System);
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist", "InitializeAsync ensure layers folder", ex);
        }

        try
        {
            await Task.Run(() => ViewModel.PrefetchLayerListItems()).ConfigureAwait(true);
            ViewModel.ApplyPrefetchedLayers();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist", "InitializeAsync RefreshLayers", ex);
            // Fall back to the synchronous path so the rail is never empty
            // due to a prefetch fault.
            try { ViewModel.RefreshLayers(); } catch { }
        }
    }

    private bool _initialized;

    /// <summary>
    /// Opens the given .phxlayer in the Layer Canvas. Called by Hub's
    /// <see cref="Phoenix.Controls.Shared.WinUI.Contracts.IPillarNavigator"/>
    /// after the user double-clicks a layer file in Explorer (Hub --open
    /// routing). Forwards to VisualistViewModel.OpenLayer which
    /// loads via LayerDocument and updates the rail selection.
    /// </summary>
    public void Open(string path) => ViewModel.OpenLayer(path);

    // ─── Public action surface — driven by Hub.MainWindow's menu dispatch ─

    /// <summary>
    /// File → New Layer. Hub menu token: visualist.file.newLayer.
    /// <para>
    /// Fires the preset picker
    /// dialog before constructing the layer so the user can pick FullHD /
    /// QHD / UHD / Vertical / Square / Custom at creation time. The public
    /// signature stays void to keep the Hub menu-dispatch contract (a void
    /// action) intact; the inner async handler is fire-and-forget with
    /// faults routed through <see cref="GlobalLogger"/>.
    /// </para>
    /// </summary>
    public async void NewLayer()
    {
        // async void error boundary: invoked from synchronous Hub menu dispatch
        // (MainWindow.OnMenuItemInvoked).
        // A dialog cancellation or file-I/O fault before the first await would
        // otherwise fault an unobserved Task. Route any fault to the log instead.
        try { await NewLayerWithPresetAsync(); }
        catch (Exception ex) { GlobalLogger.Error("Visualist.MainView", "NewLayer", ex); }
    }

    /// <summary>
    /// 0.11.x polish — public entry-point for "close the active layer"
    /// surfaced so Hub's HubChrome close-X (which dispatches through
    /// MainWindow) can reach the same dirty-prompt + new-layer sequence.
    /// Mirrors Architect's CloseCurrentDocumentAsync. Faults log to
    /// <see cref="GlobalLogger"/>; never throws.
    /// </summary>
    public async Task CloseCurrentDocumentAsync()
    {
        try
        {
            if (!await PromptSaveBeforeCloseAsync()) return;
            await NewLayerWithPresetAsync();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist.MainView", "CloseCurrentDocumentAsync", ex);
        }
    }

    /// <summary>
    /// 0.11.x polish — true when there's a real layer document to close.
    /// Mirrors Architect's HasOpenDocument; gates HubChrome's close-X
    /// visibility when Visualist is the active pillar.
    /// </summary>
    public bool HasOpenDocument => ViewModel.Document is not null;

    /// <summary>File → Open Layer (picker). Hub menu token: visualist.file.openLayer.</summary>
    public void OpenLayerDialog() => OnOpenLayer(this, EventArgs.Empty);

    /// <summary>
    /// File → Open Recent Layer. Shows a ContentDialog listing the 10-deep
    /// MRU backed by Visualist.WinUI.Services.RecentFiles. Picking a row
    /// opens the layer (if the file still exists on disk, otherwise prunes
    /// the row and surfaces a System Log entry). Hub menu token:
    /// visualist.file.openRecent.
    /// </summary>
    public async Task OpenRecentLayerAsync()
    {
        try
        {
            var dlg = new Phoenix.Controls.Visualist.WinUI.Dialogs.RecentFilesDialog
            { XamlRoot = XamlRoot };
            await dlg.ShowAsync();
            var picked = dlg.PickedPath;
            if (string.IsNullOrEmpty(picked)) return;
            if (!System.IO.File.Exists(picked))
            {
                GlobalLogger.Log(
                    $"Recent layer no longer exists: {picked}",
                    "Visualist", Phoenix.Controls.Shared.Models.LogLevel.System);
                Phoenix.Controls.Visualist.WinUI.Services.RecentFiles.Remove(picked);
                return;
            }
            OpenLayerRoutingThroughRegistry(picked);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist", "OpenRecentLayer failed", ex);
        }
    }

    /// <summary>
    /// Open a .phxlayer with
    /// cross-window dedup. Before loading the file into this MainView's
    /// VisualistViewModel, we ask the registry whether another sibling
    /// window already has the same path open. If so, we activate that
    /// sibling and skip the duplicate load here — same UX contract
    /// Architect uses for .phxg files.
    ///
    /// Used by the embedded MainView's drag-drop / open / open-recent
    /// surfaces. Sibling windows route through this helper too via the
    /// shared MainView code path, so dedup works from any open Visualist
    /// surface.
    /// </summary>
    private void OpenLayerRoutingThroughRegistry(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            // Already open in a sibling? Activate it and return — the
            // sibling owns the live document, so loading here would
            // create a parallel-edit hazard with no merge story.
            var existing = Phoenix.Controls.Visualist.WinUI.Hosting
                .VisualistWindowRegistry.FindByPath(path);
            if (existing is not null)
            {
                try { Phoenix.Controls.Visualist.WinUI.Hosting.WindowFront.Show(existing); } catch { /* best-effort */ }
                Phoenix.Controls.Visualist.WinUI.Services.RecentFiles.Touch(path);
                return;
            }

            ViewModel.OpenLayer(path);
            Phoenix.Controls.Visualist.WinUI.Services.RecentFiles.Touch(path);
            RememberVisualistDir(path);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist", $"OpenLayerRoutingThroughRegistry '{path}'", ex);
        }
    }

    /// <summary>
    /// Public open-through-registry entry. Routes <paramref name="path"/>
    /// through the same cross-window dedup helper the embedded MainView uses
    /// for its picker / drag-drop / open-recent surfaces: if another sibling
    /// already has the file open, that window is activated instead of
    /// duplicate-loading here. Used by the sibling-window Recent submenu so a
    /// recent-file pick respects the multi-window model. Distinct from
    /// <see cref="Open(string)"/>, which loads unconditionally into this VM
    /// (the registry path is the right default for user-initiated opens).
    /// </summary>
    public void OpenLayerThroughRegistry(string path) => OpenLayerRoutingThroughRegistry(path);

    /// <summary>File → Save (auto-routes to Save As when no path). Hub menu token: visualist.file.save.</summary>
    public void SaveLayer() => OnSaveLayer(this, EventArgs.Empty);

    /// <summary>File → Save As (picker). Hub menu token: visualist.file.saveAs.</summary>
    public void SaveLayerAs() => OnSaveLayerAs(this, EventArgs.Empty);

    /// <summary>Edit → Undo. Hub menu token: visualist.edit.undo.</summary>
    public void Undo() => OnUndo(this, EventArgs.Empty);

    /// <summary>Edit → Redo. Hub menu token: visualist.edit.redo.</summary>
    public void Redo() => OnRedo(this, EventArgs.Empty);

    /// <inheritdoc/>
    public bool CanUndo => ViewModel.Document?.CanUndo ?? false;

    /// <inheritdoc/>
    public bool CanRedo => ViewModel.Document?.CanRedo ?? false;

    /// <inheritdoc/>
    /// <remarks>
    /// Forwards two underlying signals: the active <c>LayerDocument.CanExecuteChanged</c>
    /// (raised on push / undo / redo), and the VM's <c>PropertyChanged</c> for
    /// <c>Document</c> (raised when the active layer swaps and a new document
    /// with empty stacks comes in). The VM's Document setter rewires
    /// LayerDocument.OnChanged each swap; we mirror that pattern for the
    /// CanExecuteChanged forwarder so subscribers stay attached to whichever
    /// document is currently active.
    /// </remarks>
    public event EventHandler? CanExecuteChanged
    {
        add
        {
            EnsureUndoRedoForwarderSubscribed();
            _undoRedoChanged += value;
        }
        remove
        {
            _undoRedoChanged -= value;
        }
    }
    private EventHandler? _undoRedoChanged;
    private bool _undoRedoForwarderHooked;
    private LayerDocument? _undoRedoTrackedDocument;
    private void EnsureUndoRedoForwarderSubscribed()
    {
        if (_undoRedoForwarderHooked) return;
        _undoRedoForwarderHooked = true;
        ViewModel.PropertyChanged += OnViewModelDocumentSwapped;
        AttachToActiveDocument();
    }

    private void OnViewModelDocumentSwapped(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(VisualistViewModel.Document)) return;
        AttachToActiveDocument();
        // Synthetic raise so subscribers re-poll CanUndo/CanRedo against the
        // freshly-swapped document — it'll come in with empty stacks.
        _undoRedoChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AttachToActiveDocument()
    {
        if (_undoRedoTrackedDocument is { } prev)
            prev.CanExecuteChanged -= OnTrackedDocumentCanExecuteChanged;
        _undoRedoTrackedDocument = ViewModel.Document;
        if (_undoRedoTrackedDocument is { } next)
            next.CanExecuteChanged += OnTrackedDocumentCanExecuteChanged;
    }

    private void OnTrackedDocumentCanExecuteChanged(object? sender, EventArgs e)
        => _undoRedoChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Edit → Add Widget. Hub menu token: visualist.edit.addWidget.</summary>
    public void AddWidget() => OnAddWidget(this, EventArgs.Empty);

    // Ctrl+N / Ctrl+O accelerators (the embedded Visualist File menu had
    // no shortcut keys). Route to the same NewLayer / OpenLayerDialog the Hub
    // menu invokes.
    private void OnNewLayerAccelerator(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        NewLayer();
        args.Handled = true;
    }

    private void OnOpenLayerAccelerator(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        OpenLayerDialog();
        args.Handled = true;
    }

    // Shared layer-preview popout, owned by the pillar shell so both the
    // Widget Editor toolbar and the Layer Canvas command bar open / re-summon
    // the SAME window. The Widget Editor's timeline transport bridges scrub /
    // play / stop into this window's Preview (see ActiveLayerPreviewWindow).
    private Phoenix.Controls.Visualist.WinUI.Hosting.LayerPreviewWindow? _layerPreviewWindow;

    /// <summary>The live shared layer-preview window (or null). Exposed so the
    /// Widget Editor can post scrub/play/stop frames into it.</summary>
    public Phoenix.Controls.Visualist.WinUI.Hosting.LayerPreviewWindow? ActiveLayerPreviewWindow => _layerPreviewWindow;

    /// <summary>
    /// Open (or re-summon) a windowed preview of the whole layer. Requires
    /// a saved layer (the LayerID is the .phxlayer file stem Hub serves under
    /// <c>/layer/&lt;id&gt;</c>). Returns false + logs when no LayerID is available.
    /// </summary>
    public bool PreviewLayer()
    {
        // Gate on the LayerID FIRST (it derives from Document.FilePath, the only
        // thing that determines whether a preview is even possible) before
        // touching any display data — so an unsaved layer fails fast with a
        // clear log instead of after queuing a window-open for a null id.
        string? layerId = ResolveLayerIdForPreview();
        if (layerId is null)
        {
            GlobalLogger.Log("Preview unavailable — save the layer first so it has a LayerID.",
                "Visualist.MainView", Phoenix.Controls.Shared.Models.LogLevel.System);
            return false;
        }
        if (_layerPreviewWindow is not null)
        {
            try { Phoenix.Controls.Visualist.WinUI.Hosting.WindowFront.Show(_layerPreviewWindow); return true; }
            catch { _layerPreviewWindow = null; }
        }
        try
        {
            // Read display data from the authoritative Document.Layer — when a
            // LayerID resolved, Document is non-null and Document.Layer is the
            // saved layer. SelectedLayer is the same instance in normal flow but
            // is nullable; pre-fix a null SelectedLayer silently fell back to a
            // bogus 1920×1080 "Layer" preview. Fall back to Document.Layer (and
            // log if SelectedLayer was unexpectedly null) so the preview always
            // reflects the real resolution/name.
            var layer = ViewModel.SelectedLayer ?? ViewModel.Document?.Layer;
            if (layer is null)
            {
                GlobalLogger.Log(
                    $"Preview unavailable — layer '{layerId}' has no in-memory instance to size the preview from.",
                    "Visualist.MainView", Phoenix.Controls.Shared.Models.LogLevel.System);
                return false;
            }
            if (ViewModel.SelectedLayer is null)
            {
                GlobalLogger.Log(
                    $"PreviewLayer: SelectedLayer was null for saved layer '{layerId}'; sized the preview from Document.Layer instead.",
                    "Visualist.MainView", Phoenix.Controls.Shared.Models.LogLevel.Debug);
            }

            int w = layer.Resolution.Width;
            int h = layer.Resolution.Height;
            string title = string.IsNullOrEmpty(layer.Name) ? "Layer" : layer.Name;
            var win = new Phoenix.Controls.Visualist.WinUI.Hosting.LayerPreviewWindow(title, layerId, w, h);
            win.Closed += (_, _) => _layerPreviewWindow = null;
            _layerPreviewWindow = win;
            Phoenix.Controls.Visualist.WinUI.Hosting.WindowFront.Show(win);
            return true;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist.MainView", "PreviewLayer", ex);
            return false;
        }
    }

    // LayerID = the .phxlayer file stem (Hub's LayerRegistry key); null for an
    // unsaved scratch document.
    private string? ResolveLayerIdForPreview()
    {
        string? path = ViewModel.Document?.FilePath;
        if (string.IsNullOrEmpty(path)) return null;
        return System.IO.Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>View → Layer Canvas. Hub menu token: visualist.view.layerCanvas.</summary>
    public void ShowLayerCanvas() => SelectSubTab(showCanvas: true);

    /// <summary>View → Widget Editor. Hub menu token: visualist.view.widgetEditor.</summary>
    public void ShowWidgetEditor() => SelectSubTab(showCanvas: false);

    /// <summary>
    /// Select <paramref name="widget"/> and swap to
    /// the Widget Editor sub-tab bound to it. The single chokepoint the canvas
    /// (body double-tap / Enter / "Edit Widget" menu) and the widget roster
    /// route through, restoring the pre-WinUI "double-click a widget opens its
    /// editor" flow. Order matters: SelectedWidget is set BEFORE the tab swap
    /// because the editor's trigger strip + "+" gate on it
    /// (WidgetEditorView.RebuildTriggerTabs).
    /// </summary>
    public void EnterWidget(Phoenix.Controls.Shared.Models.LayerWidget widget)
    {
        if (widget is null) return;
        ViewModel.SelectedWidget = widget;
        ShowWidgetEditor();
    }

    /// <summary>Help → Compositor Reference. Hub menu token: visualist.help.compositorReference.</summary>
    public void ShowCompositorReference() => OnCompositorReference(this, EventArgs.Empty);

    /// <summary>
    /// Window → Preset Gallery.
    /// Hub menu token: <c>visualist.window.presetGallery</c>. Opens a
    /// non-modal <see cref="Phoenix.Controls.Visualist.WinUI.Hosting.PresetGalleryWindow"/> that surfaces
    /// every built-in <see cref="Phoenix.Controls.Shared.Models.WidgetPreset"/>
    /// as a tile. Apply routes through
    /// <see cref="VisualistViewModel.ApplyPreset(Phoenix.Controls.Shared.Models.WidgetPreset, string?)"/>:
    /// when a widget is currently selected, the gallery seeds the target
    /// id so Apply mutates that widget; otherwise Apply spawns a fresh
    /// widget seeded with the preset's starter graph.
    /// </summary>
    public void OpenPresetGallery()
    {
        try
        {
            string? targetId = ViewModel.SelectedWidget?.Id;
            var win = new Phoenix.Controls.Visualist.WinUI.Hosting.PresetGalleryWindow(ViewModel, targetId);
            Phoenix.Controls.Visualist.WinUI.Hosting.WindowFront.Show(win);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist.WinUI", "OpenPresetGallery", ex);
        }
    }

    /// <summary>
    /// Window → New Visualist
    /// Window. Hub menu token: <c>visualist.window.new</c>. Spawns a fresh
    /// top-level Visualist sibling window through
    /// <see cref="Phoenix.Controls.Visualist.WinUI.Hosting.VisualistWindowRegistry.OpenNewWindowAsync"/>;
    /// the sibling hosts its own MainView (and therefore its own
    /// VisualistViewModel, selection state, undo stack, dirty flag), so
    /// edits in one window never leak into another. The Hub-embedded
    /// MainView keeps running unchanged — sibling windows are additive.
    /// </summary>
    public Task OpenNewWindowAsync()
        => Phoenix.Controls.Visualist.WinUI.Hosting.VisualistWindowRegistry.OpenNewWindowAsync();

    /// <summary>
    /// Window → Switch To… Hub menu token: <c>visualist.window.switch</c>.
    /// Restores the pre-T15 <c>MainForm</c> Window menu (which listed the open
    /// MDI documents) for the WinUI multi-window model: lists every open
    /// Visualist sibling window from
    /// <see cref="Phoenix.Controls.Visualist.WinUI.Hosting.VisualistWindowRegistry"/>
    /// in a ContentDialog (rebuilt from the live registry each open, so the
    /// list is always current) and activates the chosen window. No-op with a
    /// log line — never a modal rejection — when no sibling windows are open.
    /// </summary>
    public async Task ShowWindowSwitcherAsync()
    {
        try
        {
            if (XamlRoot is null) return;

            var open = Phoenix.Controls.Visualist.WinUI.Hosting.VisualistWindowRegistry.Snapshot();
            if (open.Count == 0)
            {
                GlobalLogger.Log(
                    "Window switcher: no Visualist sibling windows are open.",
                    "Visualist.WinUI", Phoenix.Controls.Shared.Models.LogLevel.System);
                return;
            }

            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                MinWidth = 360,
            };
            // Keep window references alongside the rows so the activation maps
            // back to the right sibling (the visible Title is reused as label).
            var windows = new List<Phoenix.Controls.Visualist.WinUI.Hosting.VisualistSiblingWindow>(open);
            foreach (var win in windows)
            {
                list.Items.Add(new ListViewItem
                {
                    Content = string.IsNullOrEmpty(win.Title) ? "(untitled)" : win.Title,
                });
            }
            if (list.Items.Count > 0) list.SelectedIndex = 0;

            var dlg = new ContentDialog
            {
                XamlRoot          = XamlRoot,
                Title             = Localizer.T("visualist.dialog.window_switcher.title", "Switch to window"),
                PrimaryButtonText = Localizer.T("visualist.dialog.window_switcher.activate", "Activate"),
                CloseButtonText   = Localizer.T("common.button.cancel", "Cancel"),
                DefaultButton     = ContentDialogButton.Primary,
                Content           = list,
            };
            var result = await dlg.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            int idx = list.SelectedIndex;
            if (idx < 0 || idx >= windows.Count) return;
            try { Phoenix.Controls.Visualist.WinUI.Hosting.WindowFront.Show(windows[idx]); }
            catch (Exception ex) { GlobalLogger.Error("Visualist.WinUI", "Window switch activate", ex); }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist.WinUI", "ShowWindowSwitcherAsync", ex);
        }
    }

    /// <summary>
    /// Tools → Media Library… Hub menu token: visualist.tools.mediaLibrary.
    /// <para>
    /// Reintroduces the pre-0.9.0 media-library delete affordance
    /// (CHANGELOG 0.6.4) as a single ContentDialog rather than a panel — the
    /// pre-redesign panel restructure is out of scope. Hands the dialog the
    /// currently-loaded layer so its reference-scan covers unsaved edits in
    /// addition to every <c>.phxlayer</c> on disk.
    /// </para>
    /// </summary>
    public async Task ShowMediaLibraryAsync()
    {
        try
        {
            var dlg = new Phoenix.Controls.Visualist.WinUI.Dialogs.MediaLibraryDialog
            {
                XamlRoot          = XamlRoot,
                LiveLayer         = ViewModel.SelectedLayer,
                LiveLayerFileName = string.IsNullOrEmpty(ViewModel.ActiveLayerFileName)
                                        ? null
                                        : ViewModel.ActiveLayerFileName,
            };
            await dlg.ShowAsync();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist.WinUI", "ShowMediaLibraryAsync", ex);
        }
    }

    // ─── File → Import Media (Ctrl+I parity) ───────────────────────────────
    //
    // Recognised media extensions, grouped to match the OBS-friendly
    // <video>/<img>/<audio> compositor pipeline. Mirrors the pre-T15
    // MediaLibrary.ImageExt / VideoExt / AudioExt sets (baseline
    // Phoenix.Controls.Visualist/Core/MediaLibrary.cs); alpha-capable formats
    // lead so the picker suggests OBS-friendly options first.
    private static readonly string[] s_imageExt = { ".png", ".webp", ".jpg", ".jpeg", ".gif", ".bmp" };
    private static readonly string[] s_videoExt = { ".webm", ".mp4", ".mov", ".m4v" };
    private static readonly string[] s_audioExt = { ".mp3", ".wav", ".ogg", ".m4a", ".flac" };

    /// <summary>
    /// File → Import Media (Ctrl+I). Hub menu token:
    /// <c>visualist.file.importMedia</c>. Restores the pre-T15
    /// <c>MainForm.ImportMediaQuick</c> fast path — pick a media file and copy
    /// it into Hub's <c>data/media/&lt;kind&gt;/</c> tree so it's available to
    /// <c>Image.Load</c> / <c>Video.Load</c> / <c>Audio.Load</c> nodes without
    /// opening the (read/delete-only) media library first.
    /// <para>
    /// The copy logic is a faithful port of the baseline
    /// <c>MediaLibrary.ImportAsync</c> (kind-by-extension subfolder routing,
    /// name sanitisation, collision <c>_2 / _3 / …</c> suffixing) implemented
    /// here because the WinUI <c>Services.MediaLibrary</c> is read/delete-only
    /// and is outside this lane's owned-file set. The single-file picker is the
    /// only one the suite ships (<c>CustomFilePicker.PickSingleFile</c>);
    /// multi-select would need a new picker overload, deferred to a follow-up.
    /// </para>
    /// </summary>
    public async Task ImportMediaAsync()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(_hostWindow);
            string startDir = ResolveStartDir(
                Phoenix.Controls.Shared.Services.ConfigManager.Current.LastVisualistOpenDir,
                Paths.HubMedia);

            // CustomFilePicker takes (label, ext) pairs; expand to every
            // recognised extension so the picker offers all media types.
            // Extension order mirrors the baseline's alpha-capable-first
            // convention (images before video before audio).
            var allExt = new List<(string, string)>();
            foreach (var e in s_imageExt) allExt.Add(("Image", e));
            foreach (var e in s_videoExt) allExt.Add(("Video", e));
            foreach (var e in s_audioExt) allExt.Add(("Audio", e));

            // Multi-select import. Each picked file is copied into the
            // kind-by-extension subfolder; unsupported types are skipped (logged)
            // rather than aborting the batch.
            var sources = CustomFilePicker.PickMultipleFiles(hwnd, startDir, allExt);
            if (sources is null || sources.Count == 0) return;

            int ok = 0, skipped = 0;
            string? lastSrc = null;
            foreach (string src in sources)
            {
                // Route through the
                // unified MediaLibrary.Import (same kind-by-extension subfolder
                // routing, name sanitisation, and _2/_3 collision suffixing the
                // local ImportMediaFile reimplemented) so File→Import Media and
                // the MediaPickerDialog import path converge on one impl. The
                // copy is synchronous + I/O-bound, so keep the off-dispatcher
                // Task.Run wrapper.
                string? imported = await Task.Run(() =>
                    Phoenix.Controls.Visualist.WinUI.Services.MediaLibrary.Import(src));
                if (imported is null)
                {
                    skipped++;
                    GlobalLogger.Log(
                        $"Import Media: unsupported file type '{Path.GetExtension(src)}' — skipped.",
                        "Visualist.WinUI", Phoenix.Controls.Shared.Models.LogLevel.System);
                }
                else
                {
                    ok++;
                    lastSrc = src;
                }
            }

            if (lastSrc is not null) RememberVisualistDir(lastSrc);
            GlobalLogger.Log(
                $"Import Media: imported {ok} file(s)" + (skipped > 0 ? $", skipped {skipped} unsupported" : "") + ".",
                "Visualist.WinUI", Phoenix.Controls.Shared.Models.LogLevel.System);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist.WinUI", "ImportMediaAsync", ex);
        }
    }

    // The local ImportMediaFile /
    // MediaSubfolderFor / SanitizeMediaName trio was a faithful port of the
    // pre-T15 MediaLibrary.ImportAsync, written here because the WinUI
    // Services.MediaLibrary was read/delete-only at the time. MediaLibrary.Import
    // was since added with the identical contract (forward-slash relative path
    // or null for unsupported/missing), so ImportMediaAsync now calls it
    // directly and the local copies are removed — File→Import Media and the
    // MediaPickerDialog import path converge on one implementation.

    /// <summary>
    /// Drives the Layer Canvas / Widget Editor pane swap. Reached via Hub's
    /// Visualist View menu (ShowLayerCanvas / ShowWidgetEditor, dispatched
    /// through OnMenuItemInvoked), the trigger strip's ← Back, and
    /// double-click-to-enter — single source of truth for the pane swap and
    /// the side-panel context regardless of entry point.
    /// </summary>
    private void SelectSubTab(bool showCanvas)
    {
        // The shell sub-tab ToggleButtons were removed (the trigger
        // strip's ← Back + double-click-to-enter cover navigation); SelectSubTab
        // stays the single source of truth for the pane swap + side-panel context.
        MainPaneRegion.Content = showCanvas ? (UIElement)_canvasView : _editorView;

        // Context-aware side panels. In Widget Editor mode hide the
        // layer-file rail (a stray click on it silently swaps the document/widget
        // out from under the editor) and tell the inspector to drop its LAYER
        // settings, leaving the widget/trigger context only. Layer Canvas mode
        // restores both. SelectSubTab is the single source of truth for the mode.
        LayerRailColumn.Width       = showCanvas ? new GridLength(200) : new GridLength(0);
        LayerRailControl.Visibility = showCanvas ? Visibility.Visible : Visibility.Collapsed;
        InspectorPanelControl.SetEditorContext(widgetEditing: !showCanvas);
    }

    // ─── file commands ──────────────────────────────────────────────────

    private async void OnNewLayer(object? sender, EventArgs e)
    {
        try { await NewLayerWithPresetAsync(); }
        catch (Exception ex) { GlobalLogger.Error("Visualist.WinUI", "OnNewLayer", ex); }
    }

    /// <summary>
    /// Show the preset picker
    /// dialog and forward the user's choice into
    /// <see cref="VisualistViewModel.NewLayer(string, Phoenix.Controls.Shared.Models.LayerPreset, int, int)"/>.
    /// Falls back to the sync no-arg NewLayer when XamlRoot isn't available
    /// (design-time / pre-realised host) so the chord still produces a
    /// usable document rather than silently no-op'ing.
    /// </summary>
    private async Task NewLayerWithPresetAsync()
    {
        XamlRoot? root = XamlRoot;
        if (root is null)
        {
            // No XamlRoot — host hasn't realised yet. Honour the legacy
            // FullHD default so the action still produces a document.
            ViewModel.NewLayer();
            return;
        }
        var choice = await Phoenix.Controls.Visualist.WinUI.Dialogs.NewLayerDialog.ShowAsync(root);
        if (choice is null)
        {
            // Cancelled — no document mutation. Mirrors the picker-cancel
            // semantics in OnOpenLayer.
            return;
        }
        ViewModel.NewLayer(choice.Name, choice.Preset, choice.Width, choice.Height);
    }

    private void OnOpenLayer(object? sender, EventArgs e)
    {
        try
        {
            // CustomFilePicker (IFileOpenDialog COM interop) seeds the picker
            // at the project's data/layers/ folder — WinUI 3's FileOpenPicker
            // can't take a custom default path (SuggestedStartLocation is
            // enum-only). Per-pillar last-used folder seed: if AppConfig has a
            // recent dir for Visualist, prefer it over Paths.HubLayers.
            var hwnd = WindowNative.GetWindowHandle(_hostWindow);
            string startDir = ResolveStartDir(
                Phoenix.Controls.Shared.Services.ConfigManager.Current.LastVisualistOpenDir,
                Paths.HubLayers);
            var path = CustomFilePicker.PickSingleFile(
                hwnd,
                startDir,
                new[] { ("Phoenix Layer", ".phxlayer") });
            if (string.IsNullOrEmpty(path)) return;
            // Route through the registry so opening a layer that's
            // already loaded in a sibling window focuses that window
            // instead of duplicate-loading here. MRU bookkeeping happens
            // inside the helper.
            OpenLayerRoutingThroughRegistry(path);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist.WinUI", "OnOpenLayer", ex);
        }
    }

    private async void OnSaveLayer(object? sender, EventArgs e)
    {
        try
        {
            if (ViewModel.Document is null) return;
            if (ViewModel.Document.FilePath is null)
            {
                await ShowSaveAsDialogAndSave();
                return;
            }
            // Capture each widget's rendered bitmap into
            // LayerWidget.Thumbnail before serialisation so the .phxlayer
            // carries a live thumb. Capture is best-effort: a failure
            // logs + falls through, never blocks the save.
            await CaptureLiveThumbnailsAsync();
            // Surface a failed save on the status strip
            // instead of swallowing the false return.
            if (!ViewModel.SaveLayer())
            {
                ShowSaveFailedStatus();
                return;
            }
            if (ViewModel.Document.FilePath is { } saved)
            {
                Phoenix.Controls.Visualist.WinUI.Services.RecentFiles.Touch(saved);
                RememberVisualistDir(saved);
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist.WinUI", "OnSaveLayer", ex);
        }
    }

    /// <summary>
    /// Drive <see cref="LayerCanvasView.CaptureAllWidgetThumbnailsAsync"/>
    /// on the active canvas. No-ops when the canvas isn't the visible sub-tab
    /// (capture requires the WidgetViews to be mounted in the visual tree) so
    /// a Save fired from the Widget Editor doesn't refresh thumbs. Future
    /// polish could mount widget views off-screen for capture, but that's
    /// out of scope here.
    /// </summary>
    private async Task CaptureLiveThumbnailsAsync()
    {
        try
        {
            // _canvasView is the layer-canvas surface; if the widget-editor
            // sub-tab is visible, the layer canvas is not in the visual
            // tree and RenderAsync would return 0×0 bitmaps. Detect via
            // MainPaneRegion's Content rather than a separate bool because
            // the sub-tab toggle is the single source of truth.
            if (!ReferenceEquals(MainPaneRegion.Content, _canvasView)) return;
            await _canvasView.CaptureAllWidgetThumbnailsAsync();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist.WinUI", "CaptureLiveThumbnailsAsync", ex);
        }
    }

    private async void OnSaveLayerAs(object? sender, EventArgs e)
    {
        try { await ShowSaveAsDialogAndSave(); }
        catch (Exception ex) { GlobalLogger.Error("Visualist.WinUI", "OnSaveLayerAs", ex); }
    }

    private async Task ShowSaveAsDialogAndSave()
    {
        if (ViewModel.Document is null) return;

        var hwnd = WindowNative.GetWindowHandle(_hostWindow);
        var suggestedName = string.IsNullOrEmpty(ViewModel.Document.Layer.Name)
            ? "untitled"
            : ViewModel.Document.Layer.Name;

        string startDir = ResolveStartDir(
            Phoenix.Controls.Shared.Services.ConfigManager.Current.LastVisualistOpenDir,
            Paths.HubLayers);
        var path = CustomFilePicker.PickSaveFile(
            hwnd,
            startDir,
            suggestedName,
            new[] { ("Phoenix Layer", ".phxlayer") });
        if (!string.IsNullOrEmpty(path))
        {
            // Refresh widget thumbnails before SaveAs writes the file
            // so a freshly-named layer captures the current canvas state.
            await CaptureLiveThumbnailsAsync();
            // Surface a failed SaveAs on the status strip
            // and skip the MRU/recent bookkeeping (which would imply success).
            if (!ViewModel.SaveLayerAs(path))
            {
                ShowSaveFailedStatus();
                return;
            }
            Phoenix.Controls.Visualist.WinUI.Services.RecentFiles.Touch(path);
            RememberVisualistDir(path);
        }
    }

    // Last-used folder per pillar — read on picker construction, write on
    // successful pick. Existence-checked so a
    // renamed/deleted recent folder doesn't strand the picker.
    private static string ResolveStartDir(string? recall, string fallback)
        => !string.IsNullOrWhiteSpace(recall)
           && System.IO.Directory.Exists(recall)
            ? recall
            : fallback;

    private static void RememberVisualistDir(string filePath)
    {
        try
        {
            string dir = Path.GetDirectoryName(filePath) ?? string.Empty;
            if (string.IsNullOrEmpty(dir)) return;
            var cfg = Phoenix.Controls.Shared.Services.ConfigManager.Current;
            if (string.Equals(cfg.LastVisualistOpenDir, dir, System.StringComparison.OrdinalIgnoreCase)) return;
            cfg.LastVisualistOpenDir = dir;
            Phoenix.Controls.Shared.Services.ConfigManager.Save(Paths.AppConfigJson);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist.WinUI", "RememberVisualistDir", ex);
        }
    }

    private void OnAddWidget(object? sender, EventArgs e) => ViewModel.AddWidget();

    private void OnUndo(object? sender, EventArgs e)
    {
        if (ViewModel.Document?.Undo() == true)
            ViewModel.SelectedWidget = null;
    }

    private void OnRedo(object? sender, EventArgs e)
    {
        if (ViewModel.Document?.Redo() == true)
            ViewModel.SelectedWidget = null;
    }

    private void OnCompositorReference(object? sender, EventArgs e)
    {
        // Opens the in-app HTML Widget Node Reference — the data-driven page
        // generated by WidgetNodeReferenceData from the live WidgetNodeRegistry +
        // authored prose, rendered through the shared DocViewer (WebView2). This
        // supersedes the earlier plain-text ContentDialog template dump: the
        // reference now carries an in-depth card per node, a Visualist primer and
        // the typed-socket legend, matching Architect's F1 Node Reference.
        try
        {
            Hosting.WidgetNodeDocumentationWindow.OpenOrFocus();
        }
        catch (System.Exception ex)
        {
            GlobalLogger.Error("Visualist.WinUI", "OnCompositorReference", ex);
        }
    }

    // ─── status bar ─────────────────────────────────────────────────────
    //
    // CHANGELOG 0.9.9 claimed "both pillars status bar" but
    // Visualist had no strip. Paint code is duplicated locally (mirrors
    // Architect's status strip) — Visualist owns its own chrome, so there is
    // no Shared/UI/ helper class. Driven from the VM.PropertyChanged
    // handler above (Document / IsDirty / ActiveLayerFileName / SelectedLayer)
    // plus an initial paint in the ctor.

    private void RefreshStatusBar()
    {
        if (StatusLight is null || StatusLeft is null
         || StatusCenter is null || StatusRight is null) return;

        var doc = ViewModel.Document;
        if (doc is null)
        {
            StatusLight.Fill = ResolveBrush("CoalDividerBrush");
            StatusLeft.Text  = Localizer.T("visualist.status.no_layer", "No layer loaded");
            StatusCenter.Text = string.Empty;
            StatusRight.Text  = string.Empty;
            return;
        }

        if (ViewModel.IsDirty)
        {
            StatusLight.Fill = ResolveBrush("StatusYellowBrush", "WarnBrush");
            StatusLeft.Text  = Localizer.T("visualist.status.modified", "Modified");
        }
        else
        {
            StatusLight.Fill = ResolveBrush("StatusGreenBrush", "OkBrush");
            StatusLeft.Text  = Localizer.T("visualist.status.saved", "Saved");
        }

        // Center: layer filename — ActiveLayerFileName returns "(no layer)"
        // when nothing is loaded, but we already returned above in that case.
        // The TextBlock ellipsis-trims; surface the full name on hover.
        StatusCenter.Text = ViewModel.ActiveLayerFileName;
        ToolTipService.SetToolTip(StatusCenter, ViewModel.ActiveLayerFileName);

        // Right: widget count. SelectedLayer is the Document.Layer alias —
        // prefer it so a future cross-layer document swap stays correct.
        int widgetCount = ViewModel.SelectedLayer?.Widgets.Count
                       ?? doc.Layer.Widgets.Count;
        StatusRight.Text = string.Format(
            Localizer.T("visualist.status.widget_count_format", "{0} widgets"),
            widgetCount);
    }

    /// <summary>
    /// Surface a failed save NON-modally by repainting
    /// the existing status strip (red light + "Save failed" text) rather than
    /// letting <see cref="VisualistViewModel.SaveLayer"/> /
    /// <see cref="VisualistViewModel.SaveLayerAs"/> fail silently. The detail
    /// (exception) is already routed to the System Log by the VM's catch, so
    /// the strip just points the user there. No new banner control per the
    /// campaign directive — this reuses the status bar. The next VM signal that
    /// drives <see cref="RefreshStatusBar"/> (Modified/Saved transition,
    /// document swap) repaints the strip back to its normal state.
    /// </summary>
    private void ShowSaveFailedStatus()
    {
        if (StatusLight is null || StatusLeft is null) return;
        StatusLight.Fill = ResolveBrush("ErrBrush", "StatusRedBrush");
        StatusLeft.Text  = Localizer.T(
            "visualist.status.save_failed",
            "Save failed — see System Log");
    }

    // Theme-brush lookup with graceful fallback — the status-bar spec requires
    // we don't crash when a token is missing in a stripped/forked theme.
    private static Microsoft.UI.Xaml.Media.Brush ResolveBrush(params string[] keys)
    {
        var res = Application.Current.Resources;
        foreach (var key in keys)
        {
            if (res.TryGetValue(key, out var v) && v is Microsoft.UI.Xaml.Media.Brush b)
                return b;
        }
        if (res.TryGetValue("CoalDividerBrush", out var fb) && fb is Microsoft.UI.Xaml.Media.Brush fallback)
            return fallback;
        return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DimGray);
    }
}
