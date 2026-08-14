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

    // The old per-view _settingsDialogInFlight guard only knew about clicks on
    // THIS strip — it couldn't see the Tools → Settings path, so a dot click
    // racing the menu still produced two live Settings windows that clobbered
    // each other's saves on Save. The single-instance gate now lives on
    // SettingsDialog itself (OpenOrFocus), shared by both entry points; it
    // re-routes an already-open window to `tab` so the deep link still lands
    // on the right category, and logs its own failures.
    private void OpenSettingsAt(SettingsDialog.Tab tab)
        => SettingsDialog.OpenOrFocus(tab);
}
