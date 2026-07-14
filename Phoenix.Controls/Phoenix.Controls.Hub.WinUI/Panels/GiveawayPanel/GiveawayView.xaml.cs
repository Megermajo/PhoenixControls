using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Phoenix.Controls.Shared.Services;
using Windows.System;
using Windows.UI;

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
    private Storyboard? _pulseStoryboard;

    public GiveawayView(GiveawayViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Snap the default-toggle visual to the current state and start the
        // status-pill pulse if the giveaway is open.
        ApplyDefaultToggleVisual();
        ApplyStatusPulse();
        try { await ViewModel.LoadAsync(); }
        catch (Exception ex) { GlobalLogger.Error("GiveawayView", "LoadAsync failed", ex); }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopPulse();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        StopPulse();
        ViewModel.Dispose();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(GiveawayViewModel.IsDefault):
            case nameof(GiveawayViewModel.HasSelection):
                ApplyDefaultToggleVisual();
                break;
            case nameof(GiveawayViewModel.IsOpen):
            case nameof(GiveawayViewModel.StatusDotPulseVisibility):
                ApplyStatusPulse();
                break;
            case nameof(GiveawayViewModel.CreateDialogOpen):
                ApplyCreateOverlay();
                break;
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

    private async void OnToggleDefaultClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.ToggleDefaultAsync(); }
        catch (Exception ex) { GlobalLogger.Error("GiveawayView", "Toggle default failed", ex); }
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

    // ── Default-toggle visual state ─────────────────────────────────────

    private static readonly Color EmberPrimary = Color.FromArgb(0xFF, 0xE5, 0xA2, 0x4E);
    private static readonly Color CoalSecondary = Color.FromArgb(0xFF, 0x9C, 0x8A, 0x72);
    private static readonly Color CoalShell = Color.FromArgb(0xFF, 0x0B, 0x09, 0x07);
    private static readonly Color CoalDivider = Color.FromArgb(0xFF, 0x3A, 0x31, 0x27);

    private void ApplyDefaultToggleVisual()
    {
        bool on = ViewModel.IsDefault;

        // Knob slides right + recolors; pill fill toggles ember.
        DefaultToggleKnob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        DefaultToggleKnob.Margin = on ? new Thickness(0, 0, 1, 0) : new Thickness(1, 0, 0, 0);
        DefaultToggleKnob.Fill = new SolidColorBrush(on ? CoalShell : CoalSecondary);
        DefaultTogglePill.Background = new SolidColorBrush(on ? EmberPrimary : CoalDivider);

        // Label color follows state (ember when default, secondary otherwise).
        DefaultToggleLabel.Foreground = new SolidColorBrush(on ? EmberPrimary : CoalSecondary);
    }

    // ── Status-pill pulsing dot ─────────────────────────────────────────

    private void ApplyStatusPulse()
    {
        if (ViewModel.IsOpen) StartPulse();
        else StopPulse();
    }

    private void StartPulse()
    {
        if (_pulseStoryboard is not null) return;
        if (StatusPulseDot is null) return;

        // Matches the CSS @keyframes phx-pulse: opacity 1→0.5→1 +
        // scale 1→0.85→1 over 2s, infinite.
        StatusPulseDot.RenderTransformOrigin = new global::Windows.Foundation.Point(0.5, 0.5);
        var scale = new ScaleTransform();
        StatusPulseDot.RenderTransform = scale;

        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

        var opacity = new DoubleAnimationUsingKeyFrames();
        opacity.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 1.0 });
        opacity.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1)), Value = 0.5 });
        opacity.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2)), Value = 1.0 });
        Storyboard.SetTarget(opacity, StatusPulseDot);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        sb.Children.Add(opacity);

        AddScaleTrack(sb, scale, "ScaleX");
        AddScaleTrack(sb, scale, "ScaleY");

        _pulseStoryboard = sb;
        try { sb.Begin(); } catch { _pulseStoryboard = null; }
    }

    private static void AddScaleTrack(Storyboard sb, ScaleTransform scale, string property)
    {
        var anim = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
        anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 1.0 });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1)), Value = 0.85 });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2)), Value = 1.0 });
        Storyboard.SetTarget(anim, scale);
        Storyboard.SetTargetProperty(anim, property);
        sb.Children.Add(anim);
    }

    private void StopPulse()
    {
        if (_pulseStoryboard is null) return;
        try { _pulseStoryboard.Stop(); } catch { /* best-effort */ }
        _pulseStoryboard = null;
        if (StatusPulseDot is not null)
        {
            StatusPulseDot.Opacity = 1.0;
            StatusPulseDot.RenderTransform = null;
        }
    }
}
