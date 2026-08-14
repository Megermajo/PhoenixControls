using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.WinUI.Panels.CustomCommandsPanel;

/// <summary>
/// The Hub Custom Commands page — a text/variable-only chat-command manager
/// surfaced as a page on the Pre-Builds rail via the chrome's Pre-Builds tab (see
/// MainWindow.CreateToolPage → PreBuildsHostView). UserControl + ViewModel pattern
/// matching the Counters / Quotes pages; IDisposable so the Pre-Builds rail disposes
/// the VM (which flushes pending edits and unsubscribes from the
/// CustomCommandsService events) when the tab is closed.
///
/// No page entrance animation: the Pre-Builds rail already runs FadeSlideIn on the whole
/// view on EVERY tab activation, so the staggered per-card entrance this page used
/// to run was a second animation layered on the first. It was removed with the
/// house-shell rebuild rather than ported.
/// </summary>
public sealed partial class CustomCommandsView : UserControl, IDisposable
{
    public CustomCommandsViewModel ViewModel { get; }
    private bool _disposed;

    public CustomCommandsView(CustomCommandsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        // ── Header band ────────────────────────────────────────────────
        // The 48 px band replaces three rows of markup (action strip, pinned
        // state sentence, status pill), and with them their x:Bind bindings —
        // ToolPageHeader is plain properties set from here, by design. The
        // PropertyChanged subscription below is what those OneWay bindings
        // were: without it a FOREIGN config change (another Hub surface, or
        // the service reloading config.json) would move the tool without
        // moving the switch.
        PageHeader.Title = Localizer.T("panel.customcommands.header.title", "Custom Commands");
        PageHeader.IsOn = ViewModel.MasterEnabled;   // seeds; raises nothing
        PageHeader.EnablePrimaryCta(Localizer.T("panel.customcommands.header.add_cta", "+ ADD COMMAND"));
        PageHeader.MasterToggled += OnHeaderMasterToggled;
        PageHeader.PrimaryCtaInvoked += OnHeaderAddCommand;
        ApplyHeaderState();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += OnLoaded;
    }

    /// <summary>
    /// Renders the tool's state phrase into the header band.
    ///
    /// <para>The PREDICATE is not recomputed here and must not be: the source is
    /// <c>ViewModel.StatusPillText</c>, which is a straight projection of
    /// <c>CustomCommandsService.PillState</c> — the three-state machine
    /// PreBuildStatusPillTests guards, and the one that deliberately refuses a
    /// "degraded because Counters is off" state as untrue. This method only maps
    /// those three answers onto the band's phrasing (lowercase, state AND
    /// consequence) and its foreground tier.</para>
    ///
    /// <para>"nothing configured" takes the Dormant tier rather than Error: the
    /// tool is enabled and working, there is simply nothing for it to answer
    /// with. Painting that red would claim a fault the streamer has not got.</para>
    /// </summary>
    private void ApplyHeaderState()
    {
        // ViewModel.StatusPillText is deliberately NOT localized: it is an internal
        // projection of the service's three-state enum that this method matches on by
        // string, and it is rendered nowhere. Translating it would silently collapse
        // every non-English UI into the final else branch. Only the phrases handed to
        // SetState below reach the screen, so only those carry keys.
        string pill = ViewModel.StatusPillText;

        if (string.Equals(pill, "dormant", StringComparison.Ordinal))
        {
            PageHeader.SetState(Localizer.T("panel.customcommands.state.off", "off"), ToolStateKind.Dormant);
        }
        else if (pill.Contains("nothing configured", StringComparison.Ordinal))
        {
            PageHeader.SetState(
                Localizer.T("panel.customcommands.state.nothing_configured", "live · nothing configured"),
                ToolStateKind.Dormant);
        }
        else
        {
            PageHeader.SetState(
                Localizer.T("panel.customcommands.state.answering", "live · answering"),
                ToolStateKind.Live);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CustomCommandsViewModel.MasterEnabled):
                // Setting IsOn is suppressed inside ToolPageHeader, so seeding the
                // switch from a config change cannot echo back as a user gesture.
                PageHeader.IsOn = ViewModel.MasterEnabled;
                ApplyHeaderState();
                break;
            case nameof(CustomCommandsViewModel.StatusPillText):
                ApplyHeaderState();
                break;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.LoadAsync(); }
        catch (Exception ex) { GlobalLogger.Error("CustomCommandsView", "LoadAsync failed", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= OnLoaded;
        PageHeader.MasterToggled -= OnHeaderMasterToggled;
        PageHeader.PrimaryCtaInvoked -= OnHeaderAddCommand;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Dispose();
    }

    // ── Header band ─────────────────────────────────────────────────────
    // The master flip still goes through the ViewModel setter, which is where the
    // 400 ms debounce is bypassed (SaveNow) — that path is unchanged, only its
    // caller moved off the old strip's chip Button.
    private void OnHeaderMasterToggled(object? sender, bool isOn)
    {
        try { ViewModel.MasterEnabled = isOn; }
        catch (Exception ex) { GlobalLogger.Error("CustomCommandsView", "Master toggle failed", ex); }
    }

    private void OnHeaderAddCommand(object? sender, EventArgs e)
    {
        try { ViewModel.AddCommand(); }
        catch (Exception ex) { GlobalLogger.Error("CustomCommandsView", "Add failed", ex); }
    }

    // The danger verb acts on the selection. It sits in the selected-command card
    // now rather than the page strip, but the handler and the call are the same.
    private void OnDeleteCommandClick(object sender, RoutedEventArgs e)
    {
        try { ViewModel.RemoveSelectedCommand(); }
        catch (Exception ex) { GlobalLogger.Error("CustomCommandsView", "Delete failed", ex); }
    }

    // ── Roster ──────────────────────────────────────────────────────────
    // The row Button carries the CommandRowVm as its DataContext (ItemsRepeater sets
    // it on every realized element), which is the house selection idiom — there is no
    // ListView and no SelectedItem in this grammar.
    private void OnCommandRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CommandRowVm row }) return;
        try { ViewModel.SelectedCommand = row; }
        catch (Exception ex) { GlobalLogger.Error("CustomCommandsView", "Select failed", ex); }
    }

    // The enable pill is display-only, so its host Button owns the flip. It is nested
    // inside the row Button and handles the press itself, so toggling a command does
    // not also move the selection.
    private void OnRowEnabledClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CommandRowVm row }) return;
        try { ViewModel.ToggleRowEnabled(row); }
        catch (Exception ex) { GlobalLogger.Error("CustomCommandsView", "Enable toggle failed", ex); }
    }

    // ── Selected-command editor ─────────────────────────────────────────
    private void OnEditorEnabledClick(object sender, RoutedEventArgs e)
    {
        var row = ViewModel.SelectedCommand;
        if (row is null) return;
        try { ViewModel.ToggleRowEnabled(row); }
        catch (Exception ex) { GlobalLogger.Error("CustomCommandsView", "Enable toggle failed", ex); }
    }
}
