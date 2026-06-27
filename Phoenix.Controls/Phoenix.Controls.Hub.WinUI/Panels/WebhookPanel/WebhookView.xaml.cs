using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Hub.WinUI.Panels.Common;

namespace Phoenix.Controls.Hub.WinUI.Panels.WebhookPanel;

/// <summary>
/// C16 (audit/winui-regressions-2026-05-24) — view for the Hub
/// "Recent Webhooks" tail panel.
///
/// Mirrors <see cref="EventLogPanel.EventLogView"/> at smaller scope:
/// search-only filtering, no level chips, no per-row context menu.
/// Pop-out goes through the same <see cref="IPopOutSource"/> /
/// <see cref="IPopOutAware"/> contracts the four headline panels use,
/// so HubWorkspaceView can host a pop-out without bespoke plumbing.
/// </summary>
public sealed partial class WebhookView : UserControl, IDisposable, IPopOutSource, IPopOutAware
{
    public WebhookPanelViewModel ViewModel { get; }
    private bool _disposed;
    private DispatcherTimer? _searchDebounce;
    private string _pendingSearchText = string.Empty;

    public event EventHandler? PopOutRequested;

    public WebhookView(WebhookPanelViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_searchDebounce is not null)
        {
            _searchDebounce.Stop();
            _searchDebounce.Tick -= OnSearchDebounceTick;
            _searchDebounce = null;
        }
        ViewModel.Dispose();
    }

    public void MarkAsPopOutChild() => PopOutButton.Visibility = Visibility.Collapsed;

    private void OnPopOutClick(object sender, RoutedEventArgs e)
        => PopOutRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 250ms debounce so each keystroke doesn't force a buffer rebuild —
    /// the same shape as EventLogView's search debounce. The buffer is
    /// at most 100 rows so each rebuild is cheap, but the debounce keeps
    /// the visible flicker down when the user is typing a long substring.
    /// </summary>
    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        _pendingSearchText = tb.Text ?? string.Empty;
        if (_searchDebounce is null)
        {
            _searchDebounce = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250),
            };
            _searchDebounce.Tick += OnSearchDebounceTick;
        }
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void OnSearchDebounceTick(object? sender, object e)
    {
        _searchDebounce?.Stop();
        ViewModel.SearchText = _pendingSearchText;
    }
}
