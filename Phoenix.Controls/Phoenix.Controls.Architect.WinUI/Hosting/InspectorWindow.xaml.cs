using System;
using System.ComponentModel;
using System.IO;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Architect.WinUI.Controls;
using Phoenix.Controls.Architect.WinUI.Services;
using Phoenix.Controls.Architect.WinUI.ViewModels;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;
using WinRT.Interop;

// Namespace deliberately avoids "Phoenix.Controls.Architect.WinUI.Windows"
// — that name would shadow the platform `Windows.*` namespace from any
// file under Phoenix.Controls.Architect.WinUI.* and break references like
// Windows.UI.Colors / Windows.Storage / Windows.ApplicationModel.
namespace Phoenix.Controls.Architect.WinUI.Hosting;

/// <summary>
/// Floating top-level Inspector window. Hosts a
/// <see cref="LogicInspector"/> bound to an <see cref="ArchitectViewModel"/>'s
/// LogicInspector VM; replaces the right-column docked card MainView /
/// ArchitectSiblingWindow used to host.
///
/// Lifecycle: singleton-ish — at most one inspector window per process.
/// <see cref="OpenFor"/> opens or focuses the window for a given AVM;
/// <see cref="CloseIfOpen"/> closes it (no-op when not open). The
/// underlying AVM swap rebuilds the inner <see cref="LogicInspector"/>
/// so DataContext changes don't smash a mid-render binding.
///
/// Persistence: per-restart geometry through
/// <see cref="InspectorWindowStateStore"/>; visibility through
/// <see cref="Phoenix.Controls.Shared.Models.AppConfig.ArchitectInspectorVisible"/>.
/// </summary>
public sealed partial class InspectorWindow : Window
{
    private ArchitectViewModel? _viewModel;
    private LogicInspector? _inspectorView;
    private AppWindow? _appWindow;
    private static InspectorWindow? s_instance;
    // Guards the check-then-act on s_instance so a
    // concurrent OpenFor (multi-window Architect) can't construct two windows.
    private static readonly object s_instanceGate = new();

    /// <summary>
    /// Fires when the user closes the window (clicks the [X], hits Alt+F4,
    /// etc.). The host (MainView / ArchitectSiblingWindow) listens so it
    /// can flip <c>ConfigManager.Current.ArchitectInspectorVisible</c>
    /// back to false and update its "Show Inspector" toggle glyph.
    /// </summary>
    public event EventHandler? UserClosed;

    public InspectorWindow()
    {
        InitializeComponent();
        Title = Localizer.T("architect.window.inspector.title", "Inspector — Architect");

        // No menu strip — the chrome's SecondaryContent slot is left empty
        // so its RowDefinition (Auto) collapses. The maximize button is
        // visible by default; a floating inspector benefits from the same
        // affordances any other Phoenix top-level window has.
        Activated += OnActivatedOnce;
        Closed += OnClosed;
    }

    /// <summary>
    /// Open (or focus + rebind) the singleton inspector window for the
    /// given <see cref="ArchitectViewModel"/>. Re-opening with the same
    /// AVM is a focus-only operation; re-opening with a different AVM
    /// swaps the bound LogicInspector instance.
    /// </summary>
    public static InspectorWindow OpenFor(ArchitectViewModel viewModel)
    {
        if (viewModel is null) throw new ArgumentNullException(nameof(viewModel));

        // Lock the check-then-construct so two
        // concurrent opens can't each see s_instance == null and spawn a
        // duplicate window. (Window construction must run on the UI thread, so
        // contention is rare, but the guard makes the singleton contract sound.)
        lock (s_instanceGate)
        {
            if (s_instance is null)
            {
                s_instance = new InspectorWindow();
                s_instance.BindTo(viewModel);
                s_instance.Activate();
                return s_instance;
            }

            // Rebind if the requesting AVM differs from the currently-bound one.
            if (!ReferenceEquals(s_instance._viewModel, viewModel))
                s_instance.BindTo(viewModel);

            try { s_instance.Activate(); }
            catch (Exception ex)
            {
                GlobalLogger.Error("InspectorWindow", "OpenFor → Activate", ex);
            }
            return s_instance;
        }
    }

    /// <summary>
    /// Close the singleton inspector window if it is open. No-op when
    /// no instance exists. The Closed handler clears <c>s_instance</c>.
    /// </summary>
    public static void CloseIfOpen()
    {
        if (s_instance is null) return;
        try { s_instance.Close(); }
        catch (Exception ex)
        {
            GlobalLogger.Error("InspectorWindow", "CloseIfOpen", ex);
        }
    }

    /// <summary>The currently-open singleton instance, or null when no
    /// inspector window is up. Exposed so callers (MainView /
    /// ArchitectSiblingWindow) can hook focus-change handlers without
    /// reaching into the static field.</summary>
    public static InspectorWindow? CurrentInstance => s_instance;

    private void BindTo(ArchitectViewModel viewModel)
    {
        // Detach from any previous AVM so its caption-name PropertyChanged
        // signal doesn't keep this window alive in the GC.
        if (_viewModel is not null)
        {
            try { _viewModel.PropertyChanged -= OnViewModelPropertyChanged; }
            catch { /* best-effort */ }
        }

        _viewModel = viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Rebuild the LogicInspector child so DataContext switches happen
        // on a fresh visual rather than mutating an already-rendered view.
        _inspectorView = new LogicInspector
        {
            DataContext = viewModel.LogicInspector,
        };
        InspectorHost.Content = _inspectorView;

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
            GlobalLogger.Error("InspectorWindow", "Chrome.Bind", ex);
        }

        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            if (_appWindow is not null)
                _appWindow.Closing += OnAppWindowClosing;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("InspectorWindow", "AppWindow resolve", ex);
        }

        // Restore per-restart geometry. The store handles "no record yet"
        // by positioning the window at the top-right of the primary display
        // so a first-launch inspector lands where users expect a docked
        // inspector to be.
        try { InspectorWindowStateStore.Restore(this); }
        catch (Exception ex)
        {
            GlobalLogger.Error("InspectorWindow", "state restore", ex);
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // Persist geometry before the OS destroys the window. Reading
        // appWindow.Size / Position after Close is unreliable, so capture
        // here regardless of whether the close ends up cancelled (the
        // inspector window has no cancellable-close semantics today).
        try { InspectorWindowStateStore.Persist(this); }
        catch { /* best-effort */ }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // Persist final geometry on real close (covers the Alt+F4 path
        // where OnAppWindowClosing may not fire on every Windows build).
        try { InspectorWindowStateStore.Persist(this); }
        catch { /* best-effort */ }

        try
        {
            if (_appWindow is not null)
            {
                _appWindow.Closing -= OnAppWindowClosing;
                _appWindow = null;
            }
        }
        catch { /* best-effort */ }

        try
        {
            if (_viewModel is not null)
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        catch { /* best-effort */ }

        try { Chrome.Unbind(); } catch { /* best-effort */ }

        // Drop the singleton so a subsequent OpenFor spawns a fresh window
        // rather than poking a torn-down one.
        // Under the same gate as OpenFor's check-then-construct.
        lock (s_instanceGate)
        {
            if (ReferenceEquals(s_instance, this)) s_instance = null;
        }

        // Fire the user-close signal so the host (MainView /
        // ArchitectSiblingWindow) can flip its "Show Inspector" toggle and
        // persist the visibility flag.
        try { UserClosed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex)
        {
            GlobalLogger.Error("InspectorWindow", "UserClosed dispatch", ex);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ArchitectViewModel.LoadedFilePath)
            || e.PropertyName == nameof(ArchitectViewModel.IsDirty))
        {
            UpdateCaptionFromState();
        }
    }

    private void UpdateCaptionFromState()
    {
        if (_viewModel is null) return;
        string? path = _viewModel.LoadedFilePath;
        string fname = string.IsNullOrEmpty(path)
            ? "(unsaved)"
            : Path.GetFileName(path) ?? path;
        if (_viewModel.IsDirty) fname = "• " + fname;
        CaptionFileName.Text = fname;
        Title = $"{fname} — Inspector";
    }
}
