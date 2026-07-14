using System;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.WinUI.Contracts;

namespace Phoenix.Controls.Architect.WinUI.Controls;

public sealed partial class ArchitectChrome : UserControl
{
    public static readonly DependencyProperty FileNameProperty =
        DependencyProperty.Register(
            nameof(FileName),
            typeof(string),
            typeof(ArchitectChrome),
            new PropertyMetadata(string.Empty, OnFileNameChanged));

    public string FileName
    {
        get => (string)GetValue(FileNameProperty);
        set => SetValue(FileNameProperty, value);
    }

    /// <summary>
    /// Raised when an inactive pillar tab is clicked. Hub's MainWindow
    /// handles it by swapping the embedded pillar view in-process
    /// (the LauncherService indirection was retired in T15).
    /// </summary>
    public event EventHandler<PillarKind>? LaunchPillarRequested;

    /// <summary>Raised by File → New Graph... — MainWindow loads an empty graph.</summary>
    public event EventHandler? FileNewRequested;

    /// <summary>Raised by File → Open... or Ctrl+O — MainWindow runs the file picker.</summary>
    public event EventHandler? FileOpenRequested;

    /// <summary>Raised by File → Save or Ctrl+S — MainWindow runs GraphSerializer.SaveGraphAsync.</summary>
    public event EventHandler? FileSaveRequested;

    /// <summary>Raised by File → Save As... or Ctrl+Shift+S — MainView always
    /// shows the picker even when a path is already loaded.</summary>
    public event EventHandler? FileSaveAsRequested;

    /// <summary>Raised by File → Export to .phx — MainWindow runs ScriptExporter.Export.</summary>
    public event EventHandler? FileExportRequested;

    /// <summary>
    /// 0.10.0 — Raised by File → Restore previous version…
    /// MainView shows a ContentDialog listing the existing
    /// <c>.phxg.bak[1-3]</c> rolling backups by timestamp; picking one
    /// restores it as the active file and reloads.
    /// </summary>
    public event EventHandler? FileRestoreRequested;

    /// <summary>
    /// TODO §2 resolution — Raised by File → Welcome. MainView routes to
    /// <c>LogicCanvasView.RequestShowWelcomeFromShell()</c>, which force-shows
    /// the always-available Welcome card overlay. Non-destructive: Architect
    /// has no cross-session graph persistence, so the Welcome card is the
    /// empty-start state; re-showing it leaves the current graph open behind
    /// the overlay (it does NOT clear or close it).
    /// </summary>
    public event EventHandler? FileWelcomeRequested;

    /// <summary>Raised when the View → Live Debug Trace toggle is clicked. Carries the new state.</summary>
    public event EventHandler<bool>? LiveDebugToggled;

    /// <summary>Raised by Edit → Undo or Ctrl+Z (chrome-side fallback when running standalone).</summary>
    public event EventHandler? EditUndoRequested;
    /// <summary>Raised by Edit → Redo or Ctrl+Y.</summary>
    public event EventHandler? EditRedoRequested;
    /// <summary>Raised by Edit → Find Node... or Ctrl+F.</summary>
    public event EventHandler? EditFindRequested;
    /// <summary>Raised by Edit → Group selection or Ctrl+G — wraps the
    /// current multi-selection in a Macro shell. 0.10.0.</summary>
    public event EventHandler? EditGroupRequested;
    /// <summary>Raised by Edit → Add comment frame or bare C — drops a
    /// comment frame at the canvas viewport centre. 0.10.0.</summary>
    public event EventHandler? EditCommentFrameRequested;
    /// <summary>Raised by View → Spawn node… (the chord is Ctrl+Space) —
    /// opens the spawn palette at the canvas viewport centre. 0.10.0.</summary>
    public event EventHandler? ViewSpawnRequested;
    /// <summary>Raised by View → Frame Selection or F (UE-Blueprints idiom).</summary>
    public event EventHandler? ViewFrameSelectionRequested;
    /// <summary>Raised by View → Show Grid toggle. Carries the new IsChecked state.</summary>
    public event EventHandler<bool>? ViewShowGridToggled;
    /// <summary>
    /// Raised by View → Minimap toggle. Carries the new
    /// IsChecked state; MainView routes to LogicCanvasView.SetMinimapVisible
    /// which flips the overlay's Visibility AND persists into
    /// AppConfig.ArchitectMinimapVisible.
    /// </summary>
    public event EventHandler<bool>? ViewMinimapToggled;
    /// <summary>Raised by Help → Node Reference or F1.</summary>
    public event EventHandler? HelpNodeRefRequested;

    /// <summary>
    /// Raised by Script → Sync Event Peers. MainView
    /// routes to <c>LogicCanvasView.RequestSyncEventPeersFromShell()</c>,
    /// which kicks the existing debounced cross-file sync timer (the same
    /// path used by wire-drop and event-rename edits).
    /// </summary>
    public event EventHandler? ScriptSyncEventPeersRequested;

    /// <summary>
    /// Raised by Help → Keyboard Shortcuts…. MainView
    /// pops <see cref="Dialogs.KeyboardShortcutsDialog"/> as a
    /// ContentDialog; also reachable from F1 when no node is selected.
    /// </summary>
    public event EventHandler? HelpKeyboardShortcutsRequested;

    /// <summary>Raised by View → Bookmarks legend… — MainView opens the
    /// bookmark legend flyout on the canvas so users can discover the
    /// Ctrl+1..9 / Alt+1..9 chords. 0.10.0.</summary>
    public event EventHandler? ViewBookmarksRequested;

    /// <summary>
    /// Raised by View →
    /// Toggle Inspector (and by the F4 canvas chord, which MainView
    /// bridges into the same handler). MainView flips the inspector
    /// column's expanded / rolled-up state through the same plumbing
    /// the InspectorChevronButton uses.
    /// </summary>
    public event EventHandler? InspectorToggleRequested;

    /// <summary>
    /// 0.11.x polish — Raised by the small × glyph next to the filename in
    /// the chrome's main bar. MainView listens and routes through the
    /// existing dirty-prompt path before clearing the graph (NewGraph()).
    /// Sibling Architect windows close themselves instead.
    /// </summary>
    public event EventHandler? FileCloseRequested;

    // Disables the conflicting menu chords (bare F, Ctrl+Z, …) while a text
    // input has focus so typing can never trigger canvas actions or lose the
    // keystroke to accelerator matching. Attached on Loaded / detached on
    // Unloaded because the gate hooks the static FocusManager.GotFocus event.
    private readonly Canvas.MenuAcceleratorFocusGate _menuAccelGate = new();

    public ArchitectChrome()
    {
        InitializeComponent();

        // "v0.X.Y — DEV" footer pulled from assembly version so the chrome
        // tracks Directory.Build.props automatically.
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        VersionLabel.Text = ver is null
            ? "v?.?.? — DEV"
            : $"v{ver.Major}.{ver.Minor}.{ver.Build} — DEV";

        LocalizeMenu();

        Loaded   += (_, _) => _menuAccelGate.Attach(ChromeMenuBar);
        Unloaded += (_, _) => _menuAccelGate.Detach();
    }

    // Routes every menu item / menu name through Localizer.T after
    // InitializeComponent. Each chrome owns its own LocalizeMenu impl —
    // Architect's keys are namespaced under chrome.architect.*; Visualist's under
    // chrome.visualist.*; common menu titles share chrome.menu.*.
    private void LocalizeMenu()
    {
        MenuFile.Title = Localizer.T("chrome.menu.file", "File");
        MenuEdit.Title = Localizer.T("chrome.menu.edit", "Edit");
        MenuView.Title = Localizer.T("chrome.menu.view", "View");
        // Script menu localization. Mirrors the
        // pre-T15 "architect.mainform.menu.script" key family so any
        // translation work that already covered the WinForms suite still
        // resolves the same way under the WinUI chrome.
        MenuScript.Title = Localizer.T("chrome.menu.script", "Script");
        MenuHelp.Title = Localizer.T("chrome.menu.help", "Help");

        MenuFileNew.Text    = Localizer.T("chrome.architect.file.newGraph",    "New Graph...");
        MenuFileOpen.Text   = Localizer.T("chrome.architect.file.open",        "Open...");
        MenuFileSave.Text   = Localizer.T("chrome.architect.file.save",        "Save");
        MenuFileSaveAs.Text = Localizer.T("chrome.architect.file.saveAs",      "Save As...");
        MenuFileExport.Text = Localizer.T("chrome.architect.file.exportPhx",   "Export to .phx");
        MenuFileRestore.Text = Localizer.T("chrome.architect.file.restoreBackup", "Restore previous version…");
        MenuFileWelcome.Text = Localizer.T("chrome.architect.file.welcome",    "Welcome");

        MenuEditUndo.Text         = Localizer.T("chrome.architect.edit.undo",        "Undo");
        MenuEditRedo.Text         = Localizer.T("chrome.architect.edit.redo",        "Redo");
        MenuEditFind.Text         = Localizer.T("chrome.architect.edit.findNode",    "Find Node...");
        MenuEditGroup.Text        = Localizer.T("chrome.architect.edit.group",        "Group selection");
        MenuEditCommentFrame.Text = Localizer.T("chrome.architect.edit.commentFrame", "Add comment frame");
        MenuViewSpawn.Text        = Localizer.T("chrome.architect.view.spawnNode",    "Spawn node…");

        MenuViewFrame.Text    = Localizer.T("chrome.architect.view.frameSelection", "Frame Selection");
        MenuViewShowGrid.Text = Localizer.T("chrome.architect.view.showGrid",       "Show Grid");
        // Minimap toggle. Namespaced under chrome.architect.view.*
        // alongside Show Grid / Live Debug so the localization workflow groups
        // canvas-view toggles together.
        MenuViewMinimap.Text  = Localizer.T("chrome.architect.view.minimap",        "Minimap");
        LiveDebugToggle.Text  = Localizer.T("chrome.architect.view.liveDebug",      "Live Debug Trace");
        // Toggle Inspector.
        MenuViewToggleInspector.Text = Localizer.T("chrome.architect.view.toggleInspector", "Toggle Inspector");

        MenuHelpNodeRef.Text = Localizer.T("chrome.architect.help.nodeReference", "Node Reference");
        // Script menu + Help → Keyboard Shortcuts…
        // localization. Pre-T15 keys: "architect.mainform.menu.script.sync_event_peers"
        // and "architect.mainform.menu.help.shortcuts".
        MenuScriptSyncEventPeers.Text = Localizer.T("chrome.architect.script.syncEventPeers", "Sync Event Peers");
        MenuHelpShortcuts.Text        = Localizer.T("chrome.architect.help.shortcuts",        "Keyboard Shortcuts…");
    }

    private static void OnFileNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ArchitectChrome c)
        {
            var name = e.NewValue as string ?? string.Empty;
            c.FileNameText.Text = name;
            // 0.11.x polish — the × glyph next to the filename only appears
            // when a graph is actually loaded; an empty filename means the
            // Welcome card is up and there's nothing to close. MainView can
            // still force the visibility via SetCloseFileButtonVisible to
            // cover the autosave-only-untitled case.
            if (c.CloseFileButton is not null)
                c.CloseFileButton.Visibility = string.IsNullOrEmpty(name)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
        }
    }

    /// <summary>
    /// 0.11.x polish — explicit visibility setter for the close-X glyph. Used
    /// by MainView when the loaded-file state differs from the displayed
    /// filename string (e.g. the Welcome card hides while the AVM still
    /// carries a LoadedFilePath; or the autosave path that leaves
    /// FileName="" but should still show the close affordance).
    /// </summary>
    public void SetCloseFileButtonVisible(bool visible)
    {
        if (CloseFileButton is null) return;
        CloseFileButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCloseFileClicked(object sender, RoutedEventArgs e)
    {
        FileCloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 0.10.0 — sync the "Show Grid" toggle's IsChecked
    /// without firing the menu-flyout Click handler. Used by
    /// MainView.ApplyPersistedLayoutAndState to restore the persisted state
    /// silently (no toggle event roundtrip).
    /// </summary>
    public void SetShowGridChecked(bool value)
    {
        if (MenuViewShowGrid is null) return;
        MenuViewShowGrid.IsChecked = value;
    }

    /// <summary>
    /// Sync the "Minimap" toggle's IsChecked without firing the
    /// menu Click handler. Used by MainView's layout-restore path so the
    /// persisted state lands silently AND by the in-overlay × glyph path
    /// (LogicCanvasView raises MinimapVisibilityChanged; MainView listens
    /// and calls this so the toggle reflects the new state).
    /// </summary>
    public void SetMinimapChecked(bool value)
    {
        if (MenuViewMinimap is null) return;
        MenuViewMinimap.IsChecked = value;
    }

    /// <summary>
    /// 0.10.0 — sync the "Live Debug Trace" toggle's
    /// IsChecked silently. Routed through SetField-less for parity with the
    /// ShowGrid sister.
    /// </summary>
    public void SetLiveDebugChecked(bool value)
    {
        if (LiveDebugToggle is null) return;
        LiveDebugToggle.IsChecked = value;
    }

    /// <summary>
    /// Focus gate for the menu accelerators. A WinUI
    /// <c>KeyboardAccelerator</c> is window-scoped and fires regardless of
    /// which control holds keyboard focus — so typing plain letters like "f"
    /// into a value pill / rename box / databank cell also framed the
    /// viewport, and Ctrl+Z inside a pill ran the GRAPH undo instead of the
    /// text box's own undo (user report: "hotkeys still affect the canvas when typed
    /// into a pill or other text-panel"). While a text-input control has
    /// focus, <c>args.Handled = true</c> suppresses the menu item's default
    /// invoke; the keystroke itself still reaches the focused control through
    /// the normal text-input pipeline. Ctrl+S / Ctrl+Shift+S / Ctrl+O are
    /// deliberately NOT gated (their accelerators don't wire this handler) —
    /// saving/opening mid-edit is harmless and mirrors the canvas guard in
    /// <c>LogicCanvasView.OnHostKeyDown</c>, which lets exactly those two
    /// chords through while an inline editor is active.
    /// </summary>
    private void OnMenuAcceleratorInvoked(
        Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Canvas.TextInputFocusGuard.IsTextInputFocused(XamlRoot))
            args.Handled = true;
    }

    private void OnHubTabClicked(object sender, RoutedEventArgs e)
        => LaunchPillarRequested?.Invoke(this, PillarKind.Hub);

    private void OnVisualistTabClicked(object sender, RoutedEventArgs e)
        => LaunchPillarRequested?.Invoke(this, PillarKind.Visualist);

    private void OnFileNewClicked(object sender, RoutedEventArgs e)
        => FileNewRequested?.Invoke(this, EventArgs.Empty);

    private void OnFileOpenClicked(object sender, RoutedEventArgs e)
        => FileOpenRequested?.Invoke(this, EventArgs.Empty);

    private void OnFileSaveClicked(object sender, RoutedEventArgs e)
        => FileSaveRequested?.Invoke(this, EventArgs.Empty);

    private void OnFileSaveAsClicked(object sender, RoutedEventArgs e)
        => FileSaveAsRequested?.Invoke(this, EventArgs.Empty);

    private void OnFileExportClicked(object sender, RoutedEventArgs e)
        => FileExportRequested?.Invoke(this, EventArgs.Empty);

    private void OnFileRestoreClicked(object sender, RoutedEventArgs e)
        => FileRestoreRequested?.Invoke(this, EventArgs.Empty);

    private void OnFileWelcomeClicked(object sender, RoutedEventArgs e)
        => FileWelcomeRequested?.Invoke(this, EventArgs.Empty);

    private void OnLiveDebugToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem t)
            LiveDebugToggled?.Invoke(this, t.IsChecked);
    }

    /// <summary>
    /// Reflect the undo/redo stack depth onto the
    /// Edit-menu items so they grey out when there's nothing to undo/redo
    /// (pre-fix they stayed enabled and an empty-stack undo silently no-op'd).
    /// Pushed by MainView from the canvas's UndoRedoChanged signal.
    /// </summary>
    public void SetUndoRedoEnabled(bool canUndo, bool canRedo)
    {
        if (MenuEditUndo is not null) MenuEditUndo.IsEnabled = canUndo;
        if (MenuEditRedo is not null) MenuEditRedo.IsEnabled = canRedo;
    }

    private void OnEditUndoClicked(object sender, RoutedEventArgs e)
        => EditUndoRequested?.Invoke(this, EventArgs.Empty);

    private void OnEditRedoClicked(object sender, RoutedEventArgs e)
        => EditRedoRequested?.Invoke(this, EventArgs.Empty);

    private void OnEditFindClicked(object sender, RoutedEventArgs e)
        => EditFindRequested?.Invoke(this, EventArgs.Empty);

    private void OnEditGroupClicked(object sender, RoutedEventArgs e)
        => EditGroupRequested?.Invoke(this, EventArgs.Empty);

    private void OnEditCommentFrameClicked(object sender, RoutedEventArgs e)
        => EditCommentFrameRequested?.Invoke(this, EventArgs.Empty);

    private void OnViewSpawnClicked(object sender, RoutedEventArgs e)
        => ViewSpawnRequested?.Invoke(this, EventArgs.Empty);

    private void OnViewFrameSelectionClicked(object sender, RoutedEventArgs e)
        => ViewFrameSelectionRequested?.Invoke(this, EventArgs.Empty);

    private void OnViewShowGridClicked(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem t)
            ViewShowGridToggled?.Invoke(this, t.IsChecked);
    }

    // View → Minimap toggle. Routes to MainView →
    // LogicCanvasView.SetMinimapVisible which flips Visibility + persists.
    private void OnViewMinimapClicked(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem t)
            ViewMinimapToggled?.Invoke(this, t.IsChecked);
    }

    private void OnHelpNodeRefClicked(object sender, RoutedEventArgs e)
        => HelpNodeRefRequested?.Invoke(this, EventArgs.Empty);

    private void OnViewBookmarksClicked(object sender, RoutedEventArgs e)
        => ViewBookmarksRequested?.Invoke(this, EventArgs.Empty);

    // View → Toggle Inspector.
    private void OnViewToggleInspectorClicked(object sender, RoutedEventArgs e)
        => InspectorToggleRequested?.Invoke(this, EventArgs.Empty);

    // Script → Sync Event Peers.
    private void OnScriptSyncEventPeersClicked(object sender, RoutedEventArgs e)
        => ScriptSyncEventPeersRequested?.Invoke(this, EventArgs.Empty);

    // Help → Keyboard Shortcuts….
    private void OnHelpShortcutsClicked(object sender, RoutedEventArgs e)
        => HelpKeyboardShortcutsRequested?.Invoke(this, EventArgs.Empty);

    // 0.11.x polish — Help → Diagnose freeze…. Opens today's rolling
    // diagnostic log file in whatever app the OS has registered for .log
    // (Notepad on a default Windows install). The log mirrors the
    // GlobalLogger Critical / System tier — UiHangWatchdog emits
    // "UI thread unresponsive for ~Ns" CriticalError entries, which is
    // the breadcrumb we want users to find when the freeze recurs.
    private void OnHelpDiagnoseFreezeClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Phoenix.Controls.Shared.Services.DiagnosticFileLog.TodayLogPath();
            // Open via ShellExecute — handles missing files (target opens
            // the parent folder via /select), launches the registered .log
            // viewer (typically Notepad) otherwise.
            if (System.IO.File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = path,
                    UseShellExecute = true,
                });
            }
            else
            {
                // No log file yet — the writer creates it lazily on the
                // first mirrored entry. Open the parent folder so the
                // user can see whether older session logs are present.
                var parent = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent) && System.IO.Directory.Exists(parent))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName        = parent,
                        UseShellExecute = true,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "ArchitectChrome", "OnHelpDiagnoseFreezeClicked", ex);
        }
    }
}
