using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.WinUI.Panels.SoundboardPanel;

/// <summary>
/// The Hub Soundboard page — chat commands mapped to sound clips, surfaced as a closable
/// in-Hub tab via the chrome's Pre-Builds tab (see MainWindow.OpenToolTab →
/// the Pre-Builds rail). UserControl + ViewModel pattern matching the Custom Commands / Counters
/// pages; IDisposable so the Pre-Builds rail disposes the VM (which flushes pending edits and
/// unsubscribes from the SoundboardService / layer-registry events) when
/// the tab is closed.
///
/// <para>NO PAGE-LOAD ENTRANCE ANIMATION. The previous version fade-slid its four
/// sections on first realization; the Pre-Builds rail already runs FadeSlideIn over the whole view
/// on every tab activation, so that was a second animation on top of the host's. It was
/// removed with the shell rebuild rather than ported.</para>
/// </summary>
public sealed partial class SoundboardView : UserControl, IDisposable
{
    public SoundboardViewModel ViewModel { get; }
    private bool _disposed;

    public SoundboardView(SoundboardViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        // ── Header band ─────────────────────────────────────────────────
        // Wired here because the four controls it replaces were wired here: the
        // action strip's master chip, its brass CTA, its ↻ and the status pill.
        // Every route below is the SAME call the old control made.
        PageHeader.Title = Localizer.T("panel.soundboard.header.title", "Soundboard");
        PageHeader.IsOn = ViewModel.MasterEnabled;   // seeds without raising MasterToggled
        PageHeader.EnableRefresh(Localizer.T("panel.soundboard.header.refresh.tip",
                                             "Reload the layer, trigger and clip lists"));
        PageHeader.EnablePrimaryCta(Localizer.T("panel.soundboard.header.add_cta", "+ ADD SOUND"));
        PageHeader.MasterToggled += OnHeaderMasterToggled;
        PageHeader.RefreshRequested += OnHeaderRefreshRequested;
        PageHeader.PrimaryCtaInvoked += OnHeaderPrimaryCtaInvoked;

        // The VM raises the properties below on the UI thread — RefreshPill is only
        // ever reached from a UI-thread path or through UiDispatcherPump.Post — so the
        // header can be touched straight from the handler, with no marshalling here.
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        SyncHeaderState();

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.LoadAsync(); }
        catch (Exception ex) { GlobalLogger.Error("SoundboardView", "LoadAsync failed", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= OnLoaded;
        PageHeader.MasterToggled -= OnHeaderMasterToggled;
        PageHeader.RefreshRequested -= OnHeaderRefreshRequested;
        PageHeader.PrimaryCtaInvoked -= OnHeaderPrimaryCtaInvoked;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Dispose();
    }

    // ── Header band ─────────────────────────────────────────────────────
    /// <summary>The master switch. Setting the VM property bypasses the 400 ms debounce
    /// (SaveNow) exactly as the old strip chip did.</summary>
    private void OnHeaderMasterToggled(object? sender, bool isOn)
    {
        try { ViewModel.MasterEnabled = isOn; }
        catch (Exception ex) { GlobalLogger.Error("SoundboardView", "master toggle failed", ex); }
    }

    /// <summary>The band's ↻. Same page-wide refresh as the row and overlay-card
    /// buttons — re-reading one list without the others leaves the page half-stale.</summary>
    private void OnHeaderRefreshRequested(object? sender, EventArgs e)
    {
        try { ViewModel.RefreshEverything(); }
        catch (Exception ex) { GlobalLogger.Error("SoundboardView", "refresh failed", ex); }
    }

    private void OnHeaderPrimaryCtaInvoked(object? sender, EventArgs e)
    {
        try { ViewModel.AddSound(); }
        catch (Exception ex) { GlobalLogger.Error("SoundboardView", "Add failed", ex); }
    }

    /// <summary>Keeps the band's switch and state phrase on the VM. Only two things
    /// matter here — the master flag (a foreign config change, or another Hub surface,
    /// can flip it under us) and the header-state pair.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        try
        {
            switch (e.PropertyName)
            {
                case nameof(SoundboardViewModel.MasterEnabled):
                    PageHeader.IsOn = ViewModel.MasterEnabled;
                    break;
                case nameof(SoundboardViewModel.HeaderStateText):
                case nameof(SoundboardViewModel.HeaderStateKind):
                    SyncHeaderState();
                    break;
            }
        }
        catch (Exception ex) { GlobalLogger.Error("SoundboardView", "header sync failed", ex); }
    }

    /// <summary>Renders the EXISTING pill result as the band's one lowercase phrase.
    /// The predicate is untouched — SoundboardService.ComputePillState still decides,
    /// the ViewModel's RefreshPill still reads it, PreBuildStatusPillTests still pins
    /// it — and the VM's HeaderState pair is only a second render of that same state.
    /// The kind picks the foreground, so both come from one call.</summary>
    private void SyncHeaderState()
        => PageHeader.SetState(ViewModel.HeaderStateText, ViewModel.HeaderStateKind);

    // ── Add / delete / enable sounds ────────────────────────────────────

    private void OnDeleteSoundClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SoundRowVm row }) return;
        try { ViewModel.RemoveSound(row); }
        catch (Exception ex) { GlobalLogger.Error("SoundboardView", "Delete failed", ex); }
    }

    /// <summary>Per-row enabled pill. Same pattern as delete — the row is resolved from
    /// the realized element's DataContext, which the repeater sets on every recycle.</summary>
    private void OnToggleSoundEnabledClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SoundRowVm row }) return;
        try { row.Enabled = !row.Enabled; }
        catch (Exception ex) { GlobalLogger.Error("SoundboardView", "enable toggle failed", ex); }
    }

    // ── Picker population (lazy, on open) ───────────────────────────────
    // The lists are read from live registries / the filesystem, so they are refreshed when
    // the streamer actually looks at them rather than polled.

    private void OnLayerComboOpened(object sender, object e)
    {
        try { ViewModel.RefreshLayers(); }
        catch (Exception ex) { GlobalLogger.Error("SoundboardView", "layer refresh failed", ex); }
    }

    private void OnTriggerComboOpened(object sender, object e)
    {
        try { ViewModel.RefreshTriggerNames(); }
        catch (Exception ex) { GlobalLogger.Error("SoundboardView", "trigger refresh failed", ex); }
    }

    private void OnClipComboOpened(object sender, object e)
    {
        try { ViewModel.RefreshClips(); }
        catch (Exception ex) { GlobalLogger.Error("SoundboardView", "clip refresh failed", ex); }
    }

    /// <summary>The in-body refresh buttons (the overlay card's and each row's) route
    /// here, and the header band's ↻ makes the same call: re-reading one list without
    /// the others leaves the page half-stale, and the streamer pressing ↻ means "show
    /// me what is actually there".</summary>
    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        try { ViewModel.RefreshEverything(); }
        catch (Exception ex) { GlobalLogger.Error("SoundboardView", "refresh failed", ex); }
    }
}
