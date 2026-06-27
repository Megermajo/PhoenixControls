using System;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Hub.WinUI.Dialogs;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.WinUI.Panels.StatusStrip;

public sealed partial class StatusStripView : UserControl, IDisposable
{
    public StatusStripViewModel ViewModel { get; }
    private bool _disposed;
    // Reentrancy guard — a second click while the dialog's still resolving
    // ShowAsync would stack a second dialog onto the same XamlRoot, which
    // WinUI rejects with InvalidOperationException ("only one ContentDialog
    // open at a time"). Drop the second click silently.
    private bool _settingsDialogInFlight;

    public StatusStripView(StatusStripViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ViewModel.Dispose();
    }

    // Per-dot click handlers map each service to the relevant Settings tab.
    // Bot/HUD/Bus → Connection (URL, port, password); SQLite → Logic (data
    // dir + script timeouts). The Visualist/Architect dots were removed
    // post-T15: both are embedded pillars of the same Hub process now and
    // never produce a meaningful connection state.
    private void OnStreamerBotDotClicked(object? sender, EventArgs e)
        => OpenSettingsAt(SettingsDialog.Tab.Connection);

    private void OnHudOverlayDotClicked(object? sender, EventArgs e)
        => OpenSettingsAt(SettingsDialog.Tab.Connection);

    private void OnIpcBusDotClicked(object? sender, EventArgs e)
        => OpenSettingsAt(SettingsDialog.Tab.Connection);

    private async void OpenSettingsAt(SettingsDialog.Tab tab)
    {
        if (_settingsDialogInFlight) return;
        if (XamlRoot is null) return;
        _settingsDialogInFlight = true;
        try
        {
            var dlg = new SettingsDialog((int)tab) { XamlRoot = XamlRoot };
            await dlg.ShowAsync();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("StatusStripView", $"OpenSettingsAt({tab}) failed", ex);
        }
        finally
        {
            _settingsDialogInFlight = false;
        }
    }
}
