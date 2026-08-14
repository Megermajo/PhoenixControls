using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.WinUI.Panels.QuotesPanel;

/// <summary>
/// The Hub Quotes page — a databank-backed quote store surfaced as a closable
/// in-Hub tab via the chrome's Pre-Builds tab (see MainWindow.OpenToolTab
/// → the Pre-Builds rail). UserControl + ViewModel pattern matching the Counters /
/// Loyalty pages; IDisposable so the Pre-Builds rail disposes the VM (which flushes
/// pending edits and unsubscribes from the QuotesService events) when the tab
/// is closed.
///
/// No page entrance animation: the Pre-Builds rail already runs FadeSlideIn on the whole
/// view on EVERY tab activation, so the staggered per-card entrance this page used
/// to run was a second animation layered on the first. It was removed with the
/// house-shell rebuild rather than ported.
/// </summary>
public sealed partial class QuotesView : UserControl, IDisposable
{
    public QuotesViewModel ViewModel { get; }
    private bool _disposed;

    public QuotesView(QuotesViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        // ── Header band ─────────────────────────────────────────────────
        // Wired here, where the deleted action strip's Click and the deleted state
        // banner's binding used to live. Seed order matters only in that IsOn does
        // NOT raise MasterToggled — restoring the saved state can never look like a
        // gesture and write back a change the streamer never made.
        PageHeader.Title = Localizer.T("panel.quotes.header.title", "Quotes");
        PageHeader.IsOn = ViewModel.MasterEnabled;
        SyncHeaderState();
        PageHeader.MasterToggled += OnHeaderMasterToggled;

        // No EnableRefresh and no EnablePrimaryCta, deliberately: REFRESH already
        // sits in the roster's own column band, beside the list it refreshes, and
        // the page's one brass CTA is ADD — a two-field form that has to stay with
        // its two fields. A second CTA up here would break the one-per-surface rule
        // and would be a button that cannot do its job from where it stands.

        // The header is a plain control, not a binding target, so it needs the
        // refresh points the deleted pill got for free from x:Bind. These names are
        // exactly the pill's own raise sites — the MasterEnabled setter,
        // BuildSettings on a foreign ConfigChanged, and the tail of
        // RefreshQuotesAsync. More than one name on purpose: the phrase must not
        // hang on a single VM property surviving a later cleanup pass.
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += OnLoaded;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case null:
            case "":
            case nameof(QuotesViewModel.MasterEnabled):
            case nameof(QuotesViewModel.HasQuotes):
            case nameof(QuotesViewModel.CountText):
            case nameof(QuotesViewModel.StatusPillText):
                // The switch is re-seeded too, not just the phrase: a foreign
                // ConfigChanged rebuilds the working copy and can flip Enabled with
                // no gesture on this page at all.
                PageHeader.IsOn = ViewModel.MasterEnabled;
                SyncHeaderState();
                break;
        }
    }

    /// <summary>
    /// The header's one state phrase — <see cref="QuotesViewModel.StatusPillText"/>'s
    /// predicate branch for branch (master flag first, then whether the store holds
    /// anything), said shorter. No new state is invented here and nothing service-side
    /// is touched: <c>ToolStatusPill</c>, the QuotesService state inputs and
    /// PreBuildStatusPillTests all stand exactly as they were. Only the render site
    /// moved off the strip and into the band.
    ///
    /// <para>The pill's empty-store tier was WARN, which <see cref="ToolStateKind"/>
    /// does not carry. It maps to Live rather than Dormant because the tool really is
    /// answering chat — the get verb simply has nothing to return yet, and the phrase
    /// says that rather than implying the tool is asleep.</para>
    /// </summary>
    private void SyncHeaderState()
    {
        if (!ViewModel.MasterEnabled)
        {
            // "off" scopes to the CHAT COMMANDS only, so the consequence half has to
            // say what still works: the Quotes table stays writable from this panel
            // and from the Quote.* nodes while the tool is off.
            PageHeader.SetState(
                Localizer.T("panel.quotes.state.off", "off · store writable"),
                ToolStateKind.Dormant);
            return;
        }

        PageHeader.SetState(
            ViewModel.Quotes.Count == 0
                ? Localizer.T("panel.quotes.state.no_quotes", "live · no quotes")
                : Localizer.T("panel.quotes.state.answering", "live · answering chat"),
            ToolStateKind.Live);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.LoadAsync(); }
        catch (Exception ex) { GlobalLogger.Error("QuotesView", "LoadAsync failed", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= OnLoaded;
        PageHeader.MasterToggled -= OnHeaderMasterToggled;
        // Before the VM's own Dispose: it flushes a pending config write, and a
        // still-attached handler would be re-entering a view that is going away.
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Dispose();
    }

    // ── Header band ─────────────────────────────────────────────────────
    // Assigns the property rather than calling ToggleMaster(): the switch reports the
    // state it landed on, and the setter is the same single place the flip is applied,
    // bypassing the 400 ms debounce (SaveNow) exactly as the old chip Button did.
    private void OnHeaderMasterToggled(object? sender, bool isOn)
    {
        try { ViewModel.MasterEnabled = isOn; }
        catch (Exception ex) { GlobalLogger.Error("QuotesView", "Master toggle failed", ex); }
    }

    // ── Manual add ──────────────────────────────────────────────────────
    private async void OnAddQuoteClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.AddQuoteAsync(); }
        catch (Exception ex) { GlobalLogger.Error("QuotesView", "Add failed", ex); }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.RefreshAsync(); }
        catch (Exception ex) { GlobalLogger.Error("QuotesView", "Refresh failed", ex); }
    }

    // ── Roster selection ────────────────────────────────────────────────
    // The row Button carries the QuoteRowVm as its DataContext (ItemsRepeater sets
    // it on every realized element), which is the house selection idiom — there is no
    // ListView and no SelectedItem in this grammar.
    private void OnQuoteRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: QuoteRowVm row }) return;
        try { ViewModel.SelectedQuote = row; }
        catch (Exception ex) { GlobalLogger.Error("QuotesView", "Select failed", ex); }
    }

    // ── Selected-quote editor ───────────────────────────────────────────
    // Both act on the selection rather than on a row DataContext: the editor moved
    // out of the roster rows and into the detail column, so these buttons are page
    // elements, not template elements.
    private async void OnSaveQuoteClick(object sender, RoutedEventArgs e)
    {
        var row = ViewModel.SelectedQuote;
        if (row is null) return;
        try { await ViewModel.UpdateQuoteAsync(row); }
        catch (Exception ex) { GlobalLogger.Error("QuotesView", "Save failed", ex); }
    }

    private async void OnDeleteQuoteClick(object sender, RoutedEventArgs e)
    {
        var row = ViewModel.SelectedQuote;
        if (row is null) return;
        try { await ViewModel.DeleteQuoteAsync(row); }
        catch (Exception ex) { GlobalLogger.Error("QuotesView", "Delete failed", ex); }
    }
}
