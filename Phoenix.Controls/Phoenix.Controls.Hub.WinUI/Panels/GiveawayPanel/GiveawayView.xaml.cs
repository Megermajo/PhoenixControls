using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Phoenix.Controls.Hub.WinUI.Animation;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;
using Windows.System;

namespace Phoenix.Controls.Hub.WinUI.Panels.GiveawayPanel;

/// <summary>
/// The Hub Giveaway page (giveaway.jsx — GiveawayScreen). Hosted as an
/// independent pop-out window via the Giveaway chrome tab (see
/// MainWindow.OnGiveawayTabClicked). UserControl + ViewModel pattern matching
/// the other Hub panels; IDisposable so the host window's Closed handler can
/// dispose the VM (which unsubscribes from the GiveawaySource events).
/// </summary>
public sealed partial class GiveawayView : UserControl, IDisposable
{
    public GiveawayViewModel ViewModel { get; }
    private bool _disposed;

    // T11-10 — gate the empty→populated detail crossfade so it fires only on a
    // genuine user-driven selection, never on the initial LoadAsync (which may
    // already have a default giveaway selected, and the pop-out window itself
    // already fades in). Set true once the first load settles.
    private bool _contentPrimed;
    // Tracks the detail column's last visibility so the crossfade fires only on
    // the true empty→populated edge (Collapsed→Visible), not on every
    // giveaway-switch (Visible→Visible) — those show change via the tile pulses.
    private bool _detailVisible;

    public GiveawayView(GiveawayViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        // The band is write-once for identity; state + switch are seeded from
        // the VM by ApplyHeaderState on load and on every detail refresh.
        PageHeader.Title = Localizer.T("popout.title.giveaway", "Giveaway");
        PageHeader.MasterToggled += OnHeaderMasterToggled;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Seed the band (switch + state phrase) and settle the one-brass rule
        // before anything can open an overlay.
        ApplyHeaderState();
        ApplyBrassGate();
        try { await ViewModel.LoadAsync(); }
        catch (Exception ex) { GlobalLogger.Error("GiveawayView", "LoadAsync failed", ex); }
        // Arm change-animations only after the initial load has settled so the
        // empty→populated crossfade never fires for a default selection on open.
        _contentPrimed = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        PageHeader.MasterToggled -= OnHeaderMasterToggled;
        Loaded -= OnLoaded;
        ViewModel.Dispose();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // All three are raised together by RaiseDetailProperties; the band
            // reads the same predicates the pill and the toggle pill used to.
            case nameof(GiveawayViewModel.IsDefault):
            case nameof(GiveawayViewModel.HasSelection):
            case nameof(GiveawayViewModel.IsOpen):
                ApplyHeaderState();
                break;
            case nameof(GiveawayViewModel.CreateDialogOpen):
                ApplyCreateOverlay();
                break;
            case nameof(GiveawayViewModel.DetailVisibility):
                // Empty-state → populated: crossfade the detail column in only on
                // the Collapsed→Visible edge (guarded past initial load).
                {
                    bool nowVisible = ViewModel.DetailVisibility == Visibility.Visible;
                    if (_contentPrimed && nowVisible && !_detailVisible)
                        AnimateExtensions.FadeIn(DetailScroll, 180);
                    _detailVisible = nowVisible;
                }
                break;
            case nameof(GiveawayViewModel.SettingsBodyVisibility):
                // Collapsible SETTINGS card: fade + short slide the body in on
                // expand (layout still reflows instantly; only the content
                // settles — no forbidden height animation).
                if (ViewModel.SettingsBodyVisibility == Visibility.Visible)
                    AnimateExtensions.FadeSlideIn(SettingsBody, slideY: 6, durationMs: 150);
                break;
            case nameof(GiveawayViewModel.WinnerOverlayVisibility):
                ApplyBrassGate();
                if (ViewModel.WinnerOverlayVisibility == Visibility.Visible)
                    PlayWinnerReveal();
                break;
        }
    }

    // ── Header band ─────────────────────────────────────────────────────
    // The band renders what three separate controls used to: the set-default
    // pill (a hand-rolled Border + Ellipse + label with its own colour table),
    // the status pill (a tinted capsule with a 2s pulse storyboard) and the
    // "entries accepted from chat" hint beside it. All three are gone; none of
    // the PREDICATES behind them changed.
    private void ApplyHeaderState()
    {
        PageHeader.IsOn = ViewModel.IsDefault;

        if (!ViewModel.HasSelection)
        {
            PageHeader.SetState(
                Localizer.T("panel.giveaway.state.none_selected", "no giveaway selected"),
                ToolStateKind.Dormant);
            return;
        }

        // VM.StatusPillText verbatim — "open · accepting entries" / "closed" /
        // "drawn · winner picked". IsOpen is the same predicate that gated the
        // pill's ok-green and its pulsing dot.
        PageHeader.SetState(
            ViewModel.StatusPillText,
            ViewModel.IsOpen ? ToolStateKind.Live : ToolStateKind.Dormant);
    }

    private async void OnHeaderMasterToggled(object? sender, bool isOn)
    {
        // The band's master switch IS the old set-default toggle: same VM call,
        // same store write. ToggleDefaultAsync flips off !IsDefault and no-ops
        // when nothing is selected, so re-seed from the VM afterwards rather
        // than leaving the switch asserting a state the store never took.
        try { await ViewModel.ToggleDefaultAsync(); }
        catch (Exception ex) { GlobalLogger.Error("GiveawayView", "Toggle default failed", ex); }
        PageHeader.IsOn = ViewModel.IsDefault;
    }

    // ── One brass CTA at a time ─────────────────────────────────────────
    // Both overlays are full-page 0.78-alpha scrims drawn over the verb strip,
    // so the strip's brass DRAW WINNER used to bleed through beside the
    // overlay's own brass CTA — two brass CTAs on screen, one of them muddy.
    // Opacity rather than Visibility: collapsing the strip's last column slides
    // CLOSE GIVEAWAY across, and that reflow is legible through the scrim.
    private void ApplyBrassGate()
    {
        bool overlayOpen = ViewModel.WinnerOverlayVisibility == Visibility.Visible
                           || ViewModel.CreateDialogOpen;
        DrawButton.Opacity = overlayOpen ? 0 : 1;
        // A faded button is still a tab stop. The scrim already eats the pointer
        // (it carries a Background), but Tab would otherwise land focus on an
        // invisible control behind a modal. IsEnabled stays bound to CanDraw.
        DrawButton.IsTabStop = !overlayOpen;
    }

    // ── Winner-reveal (marquee) ─────────────────────────────────────────
    // The winner overlay used to just pop in. Give it a short, intentional
    // reveal: scrim fade, card scale-up + fade, and a one-shot name pop —
    // all composited (Opacity / Scale), ~200 ms, self-clearing.
    private void PlayWinnerReveal()
    {
        try
        {
            AnimateExtensions.FadeIn(WinnerOverlay, 140);                       // scrim
            AnimateExtensions.RevealScaleFade(WinnerCard, fromScale: 0.96, durationMs: 200);
            AnimateExtensions.PulseScale(WinnerNameText, peak: 1.06, durationMs: 240, delayMs: 110);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("GiveawayView", "Winner reveal animation failed", ex);
        }
    }

    // ── Top action strip ────────────────────────────────────────────────

    private void OnCreateClick(object sender, RoutedEventArgs e)
        => ViewModel.BeginCreate();

    private void OnPickerItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: long id })
        {
            ViewModel.SelectGiveaway(id);
            PickerFlyout.Hide();
        }
    }

    private void OnPickerCreateClick(object sender, RoutedEventArgs e)
    {
        PickerFlyout.Hide();
        ViewModel.BeginCreate();
    }

    private async void OnCloseGiveawayClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.CloseSelectedAsync(); }
        catch (Exception ex) { GlobalLogger.Error("GiveawayView", "Close giveaway failed", ex); }
    }

    private async void OnDrawWinnerClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.DrawWinnerAsync(); }
        catch (Exception ex) { GlobalLogger.Error("GiveawayView", "Draw winner failed", ex); }
    }

    // ── Settings card ───────────────────────────────────────────────────
    // Header click collapses/expands the card body; the two editors commit on
    // Enter / focus loss and revert on Escape. Validation is numeric-range
    // only (VM side): non-numeric reverts, out-of-range clamps.

    private void OnToggleSettingsClick(object sender, RoutedEventArgs e)
        => ViewModel.ToggleSettingsExpanded();

    private async void OnCapPerUserLostFocus(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.CommitCapPerUserAsync(); }
        catch (Exception ex) { GlobalLogger.Error("GiveawayView", "Save max tickets per user failed", ex); }
    }

    private async void OnCapPerUserKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            try { await ViewModel.CommitCapPerUserAsync(); }
            catch (Exception ex) { GlobalLogger.Error("GiveawayView", "Save max tickets per user failed", ex); }
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            ViewModel.RevertCapPerUserDraft();
        }
    }

    private async void OnSubBonusLostFocus(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.CommitSubBonusAsync(); }
        catch (Exception ex) { GlobalLogger.Error("GiveawayView", "Save subscriber bonus failed", ex); }
    }

    private async void OnSubBonusKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            try { await ViewModel.CommitSubBonusAsync(); }
            catch (Exception ex) { GlobalLogger.Error("GiveawayView", "Save subscriber bonus failed", ex); }
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            ViewModel.RevertSubBonusDraft();
        }
    }

    private async void OnModBonusLostFocus(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.CommitModBonusAsync(); }
        catch (Exception ex) { GlobalLogger.Error("GiveawayView", "Save moderator bonus failed", ex); }
    }

    private async void OnModBonusKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            try { await ViewModel.CommitModBonusAsync(); }
            catch (Exception ex) { GlobalLogger.Error("GiveawayView", "Save moderator bonus failed", ex); }
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            ViewModel.RevertModBonusDraft();
        }
    }

    private async void OnTicketPriceLostFocus(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.CommitTicketPriceAsync(); }
        catch (Exception ex) { GlobalLogger.Error("GiveawayView", "Save ticket price failed", ex); }
    }

    private async void OnTicketPriceKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            try { await ViewModel.CommitTicketPriceAsync(); }
            catch (Exception ex) { GlobalLogger.Error("GiveawayView", "Save ticket price failed", ex); }
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            ViewModel.RevertTicketPriceDraft();
        }
    }

    // ── Draw-winner overlay ─────────────────────────────────────────────

    private async void OnReDrawClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.ReDrawAsync(); }
        catch (Exception ex) { GlobalLogger.Error("GiveawayView", "Re-draw failed", ex); }
    }

    private void OnCopyNameClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string name = ViewModel.WinnerNameForCopy;
            if (string.IsNullOrEmpty(name)) return;
            var pkg = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(name);
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        }
        catch (Exception ex) { GlobalLogger.Error("GiveawayView", "Copy name failed", ex); }
    }

    private async void OnAnnounceClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.AnnounceWinnerAsync(); }
        catch (Exception ex) { GlobalLogger.Error("GiveawayView", "Announce in chat failed", ex); }
    }

    private void OnWinnerCloseClick(object sender, RoutedEventArgs e)
        => ViewModel.CloseWinnerOverlay();

    // ── Create overlay ──────────────────────────────────────────────────

    private void ApplyCreateOverlay()
    {
        bool open = ViewModel.CreateDialogOpen;
        CreateOverlay.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ApplyBrassGate();
        if (open)
        {
            // Focus the title box once the overlay shows.
            DispatcherQueue.TryEnqueue(() => CreateTitleBox.Focus(FocusState.Programmatic));
        }
    }

    private void OnCreateCancelClick(object sender, RoutedEventArgs e)
        => ViewModel.CancelCreate();

    private async void OnCreateConfirmClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.ConfirmCreateAsync(); }
        catch (Exception ex) { GlobalLogger.Error("GiveawayView", "Create giveaway failed", ex); }
    }

    private async void OnCreateTitleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            try { await ViewModel.ConfirmCreateAsync(); }
            catch (Exception ex) { GlobalLogger.Error("GiveawayView", "Create (Enter) failed", ex); }
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            ViewModel.CancelCreate();
        }
    }

    // The hand-rolled set-default toggle (Border + Ellipse knob + a four-Color
    // table) and the status pill's 2 s pulse storyboard used to live here. Both
    // controls are gone with the band; their state now renders as the band's
    // switch and its one lowercase phrase, off the same VM predicates.
}
