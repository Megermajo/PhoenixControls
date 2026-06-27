using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Phoenix.Controls.Shared.Services;
using Windows.System;
using WinRT.Interop;

namespace Phoenix.Controls.Visualist.WinUI.Hosting;

/// <summary>
/// B26 (audit/winui-regressions-2026-05-24) — Visualist sibling window.
/// Each instance hosts its own <see cref="MainView"/> and therefore its
/// own <c>VisualistViewModel</c>, which gives per-window isolation for:
///   • The active <c>LayerDocument</c> (the document the user is editing).
///   • Selection state (selected widget / trigger / keyframe).
///   • The undo / redo stack (per-document inside the VM).
///   • The dirty flag (each MainView's IsDirty tracks only its own VM).
///
/// Cross-window file routing is owned by <see cref="VisualistWindowRegistry"/>
/// — opening the same .phxlayer twice activates the existing window
/// instead of spawning a duplicate. Untitled (not-yet-saved) siblings are
/// not deduped: there's no path to coalesce on until the user saves.
///
/// Per <c>feedback_visualist_architect_chrome_independence.md</c> this
/// window's chrome is Visualist-owned — no Architect primitives are
/// imported (the MenuBar layout mirrors the Architect sibling in spirit
/// but is copied, not referenced). The sibling carries a File / Edit /
/// View / Window menu strip (restored from the pre-T15
/// <c>MainForm.BuildMenu</c>) plus window-wide Ctrl+Z / Ctrl+Y
/// accelerators, all routed through the hosted MainView's public command
/// surface (NewLayer / OpenLayerDialog / SaveLayer / SaveLayerAs /
/// ImportMediaAsync / Undo / Redo / ShowMediaLibraryAsync /
/// OpenLayerThroughRegistry). Recent and Window are rebuilt on submenu-open
/// from <see cref="Services.RecentFiles"/> and
/// <see cref="VisualistWindowRegistry"/> respectively.
///
/// File-menu note: the WinForms baseline ended with "Exit (Alt+F4)" which
/// closed the whole MDI shell; siblings are not top-level app windows, so
/// that item is replaced by "Close Window (Ctrl+W)" which closes only this
/// sibling. (Lane N audit-fix 2026-06-08 added the missing New Layer +
/// Import Media items dropped by the Lane E restore.)
/// </summary>
public sealed partial class VisualistSiblingWindow : Window
{
    private MainView? _mainView;
    private AppWindow? _appWindow;
    private bool _confirmedClose;
    private bool _promptInFlight;

    public VisualistSiblingWindow()
    {
        InitializeComponent();

        Title = "Phoenix Controls — Visualist";

        try
        {
            // MainView takes the host Window via its ctor so picker HWNDs +
            // dialog parent windows route correctly. layerSource is null
            // for siblings — they don't have access to Hub.IHubServices.Layers
            // (that's owned by the embedded MainView in Hub.MainWindow).
            // Live-presence dots on the rail will be inactive in sibling
            // windows; the full B26 follow-up sprint can plumb a shared
            // ILayerRegistrySource through the registry.
            _mainView = new MainView(this, layerSource: null);
            RootGrid.Children.Add(_mainView);

            // Mirror the MainView's IsDirty into the title caption so users
            // can tell which sibling has unsaved work at a glance. We
            // subscribe to ShellStateChanged (raised by MainView whenever
            // its VM's IsDirty / ActiveLayerFileName change) and update
            // both Title and the registry key.
            _mainView.ShellStateChanged += OnShellStateChanged;
            UpdateTitleFromShell();

            // Wire menu chrome. WinUI 3's MenuFlyoutSubItem / MenuBarItem do
            // NOT expose an Opening event (only FlyoutBase does, and the
            // MenuBarItemFlyout isn't publicly reachable), so the pre-T15
            // DropDownOpening rebuild can't be mirrored 1:1. Instead the
            // dynamic submenus (Recent, Window → Switch To) are rebuilt on
            // every signal that could change them before the user can open a
            // menu: window Activated (always precedes a menu interaction) and
            // ShellStateChanged (save / open mutate the MRU). They're also
            // built once here so the first open is correct even before the
            // window has been re-activated.
            RebuildRecentMenu();
            RebuildWindowSwitchMenu();

            // Edit-menu enable/disable mirrors the active document's undo
            // stack. Re-poll whenever the document's CanExecute state changes
            // (push / undo / redo / document swap).
            _mainView.CanExecuteChanged += OnUndoRedoCanExecuteChanged;
            RefreshEditMenuEnabled();

            // Window-wide Ctrl+Z / Ctrl+Y. The leaf MenuFlyoutItem
            // accelerators only register after their flyout opens once
            // (WinUI 3 doesn't realize flyout items until first open), so a
            // sibling could be unable to undo via keyboard before the user
            // ever opened the Edit menu. Mirror the chords onto the window
            // root so they fire window-wide regardless of focus / menu
            // state — same fix HubChrome applies for the embedded view.
            MirrorUndoRedoAccelerators();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistSiblingWindow",
                "Failed to construct embedded MainView", ex);
        }

        Activated += OnActivatedOnce;
        // Persistent — rebuilds the dynamic submenus each time the window
        // gains focus (always precedes the user opening a menu). Deactivation
        // also fires Activated with WindowActivationState.Deactivated; the
        // rebuild is cheap (≤10 MRU entries / open windows) so we don't gate
        // on the state.
        Activated += OnActivatedRebuildMenus;
        Closed   += OnClosed;
    }

    private void OnActivatedRebuildMenus(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;
        RebuildRecentMenu();
        RebuildWindowSwitchMenu();
    }

    private void OnUndoRedoCanExecuteChanged(object? sender, EventArgs e)
    {
        if (DispatcherQueue is null) { RefreshEditMenuEnabled(); return; }
        DispatcherQueue.TryEnqueue(RefreshEditMenuEnabled);
    }

    private void RefreshEditMenuEnabled()
    {
        try
        {
            if (MenuEditUndo is not null) MenuEditUndo.IsEnabled = _mainView?.CanUndo ?? false;
            if (MenuEditRedo is not null) MenuEditRedo.IsEnabled = _mainView?.CanRedo ?? false;
        }
        catch { /* best-effort */ }
    }

    // ─── Menu handlers — route to the hosted MainView ───────────────────────

    private void OnFileNewClicked(object sender, RoutedEventArgs e)
    {
        // Lane N audit-fix (2026-06-08): File → New Layer was missing from the
        // Lane E menu restore. MainView.NewLayer() is async void (its inner
        // preset-picker is fire-and-forget with faults routed through
        // GlobalLogger), so no Task to discard here — same entry point Hub's
        // MenuDefinition.Visualist dispatches (token visualist.file.newLayer).
        try { _mainView?.NewLayer(); }
        catch (Exception ex) { GlobalLogger.Error("VisualistSiblingWindow", "File → New Layer", ex); }
    }

    private void OnFileOpenClicked(object sender, RoutedEventArgs e)
    {
        try { _mainView?.OpenLayerDialog(); }
        catch (Exception ex) { GlobalLogger.Error("VisualistSiblingWindow", "File → Open", ex); }
    }

    private void OnFileImportMediaClicked(object sender, RoutedEventArgs e)
    {
        // Lane N audit-fix (2026-06-08): File → Import Media was missing from
        // the Lane E menu restore. MainView.ImportMediaAsync() returns Task —
        // discard it (fire-and-forget) the same way OnViewMediaLibraryClicked
        // handles ShowMediaLibraryAsync. Token visualist.file.importMedia.
        try { _ = _mainView?.ImportMediaAsync(); }
        catch (Exception ex) { GlobalLogger.Error("VisualistSiblingWindow", "File → Import Media", ex); }
    }

    private void OnFileSaveClicked(object sender, RoutedEventArgs e)
    {
        try { _mainView?.SaveLayer(); }
        catch (Exception ex) { GlobalLogger.Error("VisualistSiblingWindow", "File → Save", ex); }
    }

    private void OnFileSaveAsClicked(object sender, RoutedEventArgs e)
    {
        try { _mainView?.SaveLayerAs(); }
        catch (Exception ex) { GlobalLogger.Error("VisualistSiblingWindow", "File → Save As", ex); }
    }

    private void OnFileCloseClicked(object sender, RoutedEventArgs e)
    {
        try { Close(); } catch { /* best-effort */ }
    }

    private void OnEditUndoClicked(object sender, RoutedEventArgs e)
    {
        try { _mainView?.Undo(); }
        catch (Exception ex) { GlobalLogger.Error("VisualistSiblingWindow", "Edit → Undo", ex); }
    }

    private void OnEditRedoClicked(object sender, RoutedEventArgs e)
    {
        try { _mainView?.Redo(); }
        catch (Exception ex) { GlobalLogger.Error("VisualistSiblingWindow", "Edit → Redo", ex); }
    }

    private void OnViewMediaLibraryClicked(object sender, RoutedEventArgs e)
    {
        try { _ = _mainView?.ShowMediaLibraryAsync(); }
        catch (Exception ex) { GlobalLogger.Error("VisualistSiblingWindow", "View → Media Library", ex); }
    }

    // ─── Window-wide undo/redo chords ───────────────────────────────────────

    private void MirrorUndoRedoAccelerators()
    {
        try
        {
            if (Content is not UIElement root) return;
            AddRootAccelerator(root, VirtualKey.Z, VirtualKeyModifiers.Control, OnRootUndoInvoked);
            AddRootAccelerator(root, VirtualKey.Y, VirtualKeyModifiers.Control, OnRootRedoInvoked);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistSiblingWindow", "MirrorUndoRedoAccelerators", ex);
        }
    }

    private static void AddRootAccelerator(
        UIElement root, VirtualKey key, VirtualKeyModifiers modifiers,
        Windows.Foundation.TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> handler)
    {
        var accel = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accel.Invoked += handler;
        root.KeyboardAccelerators.Add(accel);
    }

    private void OnRootUndoInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        try { _mainView?.Undo(); }
        catch (Exception ex) { GlobalLogger.Error("VisualistSiblingWindow", "Ctrl+Z", ex); }
    }

    private void OnRootRedoInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        try { _mainView?.Redo(); }
        catch (Exception ex) { GlobalLogger.Error("VisualistSiblingWindow", "Ctrl+Y", ex); }
    }

    // ─── Recent submenu (rebuilt on Activated / ShellStateChanged) ──────────

    private void RebuildRecentMenu()
    {
        try
        {
            MenuFileRecent.Items.Clear();
            var recent = Services.RecentFiles.Load();
            if (recent.Count == 0)
            {
                MenuFileRecent.Items.Add(new MenuFlyoutItem
                {
                    Text = "(no recent layers)",
                    IsEnabled = false,
                });
                return;
            }

            foreach (var path in recent)
            {
                string captured = path;
                var item = new MenuFlyoutItem
                {
                    // Show the file name; the full path goes on the tooltip
                    // so a long folder chain doesn't blow out the flyout.
                    Text = Path.GetFileName(captured),
                };
                ToolTipService.SetToolTip(item, captured);
                item.Click += (_, _) => OpenRecent(captured);
                MenuFileRecent.Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistSiblingWindow", "Recent submenu rebuild", ex);
        }
    }

    private void OpenRecent(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return;
            if (!File.Exists(path))
            {
                // Prune the stale entry and surface a log line rather than a
                // modal (feedback_no_modal_dialogs_for_repeatable_rejections).
                Services.RecentFiles.Remove(path);
                StatusLeft.Text = $"Recent entry missing: {Path.GetFileName(path)}";
                GlobalLogger.Log(
                    $"Recent layer no longer exists: {path}",
                    "VisualistSiblingWindow",
                    Phoenix.Controls.Shared.Models.LogLevel.System);
                return;
            }
            // Route through the registry so opening a layer already loaded
            // in another sibling focuses that window instead of duplicate-
            // loading it here.
            _mainView?.OpenLayerThroughRegistry(path);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistSiblingWindow", $"OpenRecent '{path}'", ex);
        }
    }

    // ─── Window switcher (rebuilt on Activated / ShellStateChanged) ─────────

    private void OnWindowNewClicked(object sender, RoutedEventArgs e)
    {
        _ = Phoenix.Controls.Shared.Core.AsyncErrorBoundary.SafeRunAsync(
            VisualistWindowRegistry.OpenNewWindowAsync,
            "VisualistSiblingWindow",
            "Window → New Visualist Window");
    }

    private void RebuildWindowSwitchMenu()
    {
        try
        {
            MenuWindowSwitch.Items.Clear();

            var open = VisualistWindowRegistry.Snapshot();
            if (open.Count == 0)
            {
                MenuWindowSwitch.Items.Add(new MenuFlyoutItem { Text = "(no open windows)", IsEnabled = false });
                return;
            }

            foreach (var win in open)
            {
                var captured = win;
                var item = new MenuFlyoutItem
                {
                    Text = string.IsNullOrEmpty(win.Title) ? "(untitled)" : win.Title,
                };
                // Mark the active (this) window with a leading dot so the
                // user can see which sibling they're switching from.
                if (ReferenceEquals(captured, this))
                    item.Text = "• " + item.Text;
                item.Click += (_, _) =>
                {
                    try { captured.Activate(); }
                    catch (Exception ex)
                    {
                        GlobalLogger.Error("VisualistSiblingWindow", "Window switch", ex);
                    }
                };
                MenuWindowSwitch.Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistSiblingWindow", "Window switch rebuild", ex);
        }
    }

    private void OnActivatedOnce(object sender, WindowActivatedEventArgs args)
    {
        // Bind AppWindow once we have an HWND. Doing this in the ctor is
        // racy — WindowNative.GetWindowHandle returns 0 before the first
        // Activated. Unhook after the first fire so subsequent focus
        // changes don't re-bind.
        Activated -= OnActivatedOnce;
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            if (_appWindow is not null)
            {
                _appWindow.Closing += OnAppWindowClosing;
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistSiblingWindow", "AppWindow resolve", ex);
        }
    }

    /// <summary>
    /// B26 — Load <paramref name="absolutePath"/> into the hosted MainView.
    /// Returns true on success. Called by <see cref="VisualistWindowRegistry.OpenFileAsync"/>
    /// after constructing the empty window so the load + registry-track
    /// can fail-close cleanly when the file is invalid.
    /// </summary>
    public bool LoadLayer(string absolutePath)
    {
        if (_mainView is null) return false;
        if (string.IsNullOrWhiteSpace(absolutePath)) return false;
        if (!File.Exists(absolutePath)) return false;
        try
        {
            _mainView.Open(absolutePath);
            // Recent-files MRU bookkeeping — same as the in-Hub MainView
            // does in its drag-drop / picker paths.
            try { Services.RecentFiles.Touch(absolutePath); } catch { /* best-effort */ }
            UpdateTitleFromShell();
            return true;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistSiblingWindow",
                $"LoadLayer '{absolutePath}'", ex);
            return false;
        }
    }

    /// <summary>
    /// True when the hosted MainView has unsaved edits. Mirrors
    /// <see cref="MainView.IsDirty"/>; used by the close prompt.
    /// </summary>
    public bool IsDirty => _mainView?.IsDirty ?? false;

    /// <summary>
    /// B26 — accessor for the hosted MainView so the registry's cross-
    /// window file-routing path can rebind the window's key after a Save
    /// As. Kept narrow (returns the MainView, not its VM) so future
    /// callers don't accidentally grow new cross-pillar dependencies.
    /// </summary>
    internal MainView? HostedMainView => _mainView;

    private void OnShellStateChanged(object? sender, EventArgs e)
    {
        // Dispatcher hop — MainView raises ShellStateChanged from its VM's
        // PropertyChanged path, which fires on the UI thread already, but
        // future producers (autosave, IPC) might not.
        if (DispatcherQueue is null) { OnShellStateChangedCore(); return; }
        DispatcherQueue.TryEnqueue(OnShellStateChangedCore);
    }

    private void OnShellStateChangedCore()
    {
        UpdateTitleFromShell();
        // A save / open changes the MRU and the window's own title — refresh
        // the Recent + Window switcher lists so they stay current even if the
        // window never loses focus between edits.
        RebuildRecentMenu();
        RebuildWindowSwitchMenu();
    }

    private void UpdateTitleFromShell()
    {
        if (_mainView is null) return;
        string display = _mainView.CurrentDocumentDisplayName ?? "(unsaved)";
        string marker  = _mainView.IsDirty ? "• " : string.Empty;
        Title = $"{marker}{display} — Visualist";
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        try { VisualistWindowRegistry.Unregister(this); } catch { /* best-effort */ }

        try
        {
            if (_appWindow is not null)
            {
                _appWindow.Closing -= OnAppWindowClosing;
                _appWindow = null;
            }
        }
        catch { /* best-effort */ }

        try { Activated -= OnActivatedRebuildMenus; } catch { /* best-effort */ }

        if (_mainView is not null)
        {
            try { _mainView.ShellStateChanged -= OnShellStateChanged; }
            catch { /* best-effort */ }
            try { _mainView.CanExecuteChanged -= OnUndoRedoCanExecuteChanged; }
            catch { /* best-effort */ }
            _mainView = null;
        }
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_confirmedClose) return;
        if (_mainView is null || !_mainView.IsDirty) return;
        if (_promptInFlight) { args.Cancel = true; return; }

        args.Cancel = true;
        _promptInFlight = true;
        try
        {
            bool proceed = await _mainView.PromptSaveBeforeCloseAsync().ConfigureAwait(true);
            if (proceed)
            {
                _confirmedClose = true;
                DispatcherQueue?.TryEnqueue(Close);
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistSiblingWindow", "OnAppWindowClosing", ex);
        }
        finally
        {
            _promptInFlight = false;
        }
    }
}
