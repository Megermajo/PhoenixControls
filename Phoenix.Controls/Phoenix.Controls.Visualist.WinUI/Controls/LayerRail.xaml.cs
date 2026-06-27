using System.Collections;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Phoenix.Controls.Visualist.WinUI.Controls;

public sealed partial class LayerRail : UserControl
{
    // Finding #22 — empty-state hint. We toggle EmptyHint.Visibility off the
    // bound Layers collection (mirrors MediaLibraryPanel's EmptyHint pattern).
    // The ItemsSource is set via XAML binding ({Binding Layers}), so it only
    // resolves once DataContext is wired and Loaded fires — subscribe there,
    // and unsubscribe on Unloaded so the handle doesn't outlive the control.
    private INotifyCollectionChanged? _observed;

    public LayerRail()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HookCollection();
        UpdateEmptyHint();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnhookCollection();
    }

    private void HookCollection()
    {
        // Re-resolve in case the DataContext (and thus the bound collection)
        // changed while detached, then re-hook only if it actually moved.
        var incc = LayersList.ItemsSource as INotifyCollectionChanged;
        if (ReferenceEquals(incc, _observed))
            return;

        UnhookCollection();

        if (incc is not null)
        {
            incc.CollectionChanged += OnLayersChanged;
            _observed = incc;
        }
    }

    private void UnhookCollection()
    {
        if (_observed is not null)
        {
            _observed.CollectionChanged -= OnLayersChanged;
            _observed = null;
        }
    }

    private void OnLayersChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => UpdateEmptyHint();

    private void UpdateEmptyHint()
    {
        bool empty = (LayersList.ItemsSource as ICollection)?.Count is null or 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }
}
