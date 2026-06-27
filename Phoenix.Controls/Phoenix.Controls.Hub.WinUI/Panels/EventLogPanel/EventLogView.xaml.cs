using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Hub.WinUI.Panels.Common;

namespace Phoenix.Controls.Hub.WinUI.Panels.EventLogPanel;

/// <summary>
/// View for the EventLog tail panel (audit B43). Mirrors the shape of
/// <see cref="SystemLogPanel.SystemLogView"/> at a smaller scope —
/// search-only filtering, no level chips (EventLog rows aren't tiered),
/// no per-row right-click clipboard menu yet. Pop-out wiring goes through
/// the same <see cref="IPopOutSource"/> + <see cref="IPopOutAware"/>
/// contracts the four headline panels use, so the Hub workspace's
/// PopOutWindowFactory + PopOutStateStore plumbing can host an EventLog
/// pop-out with no new infrastructure.
/// </summary>
public sealed partial class EventLogView : UserControl, IDisposable, IPopOutSource, IPopOutAware
{
    public EventLogPanelViewModel ViewModel { get; }
    private bool _disposed;
    private DispatcherTimer? _searchDebounce;
    private string _pendingSearchText = string.Empty;

    public event EventHandler? PopOutRequested;

    public EventLogView(EventLogPanelViewModel viewModel)
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
