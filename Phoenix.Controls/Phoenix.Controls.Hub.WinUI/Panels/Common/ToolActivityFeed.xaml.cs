using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Shared.Localization;

namespace Phoenix.Controls.Hub.WinUI.Panels.Common;

/// <summary>
/// Shared activity card for the Pre-Build tool pages (see ToolActivityFeed.xaml
/// for the geometry rationale and for why there is no empty state).
///
/// Typical use — the last child of a page's detail scroller:
/// <code>
/// &lt;cmn:ToolActivityFeed ItemsSource="{x:Bind ViewModel.Activity, Mode=OneWay}"
///                          Scope="this session"
///                          AutomationProperties.Name="Counters activity log" /&gt;
/// </code>
///
/// Give <see cref="ItemsSource"/> a single ObservableCollection&lt;ToolActivityRowVm&gt;
/// created in the VM constructor and mutate it in place. Replacing the
/// collection tears down the ItemsRepeater's virtualization and loses scroll
/// position, which is a regression this codebase has already paid for once.
///
/// Set the accessible name on this control, not inside it — the inner repeater
/// deliberately carries none so the host's name governs.
/// </summary>
public sealed partial class ToolActivityFeed : UserControl
{
    public ToolActivityFeed()
    {
        InitializeComponent();

        // The DEFAULT eyebrow is localized HERE rather than with a
        // loc:Localize.Key on TitleText, and the difference is load-bearing: the
        // attached property resolves at Loaded, which is AFTER a host has applied
        // its own Title attribute, so it would silently overwrite an override
        // (Quotes ships "RECENTLY ADDED") with the generic translation. A
        // constructor set runs BEFORE the host's attribute, so an override still
        // wins — and the host localizes it through its own key.
        Title = Localizer.T("panel.common.activity.title", Title);
    }

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(object), typeof(ToolActivityFeed),
            new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(ToolActivityFeed),
            new PropertyMetadata("ACTIVITY", OnTitleChanged));

    public static readonly DependencyProperty ScopeProperty =
        DependencyProperty.Register(nameof(Scope), typeof(string), typeof(ToolActivityFeed),
            new PropertyMetadata("", OnScopeChanged));

    public static readonly DependencyProperty MaxRowsHeightProperty =
        DependencyProperty.Register(nameof(MaxRowsHeight), typeof(double), typeof(ToolActivityFeed),
            new PropertyMetadata(240d, OnMaxRowsHeightChanged));

    /// <summary>
    /// The row collection, normally an ObservableCollection&lt;ToolActivityRowVm&gt;.
    /// Typed as object so a page can bind whatever collection type it holds.
    /// </summary>
    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>
    /// Header eyebrow. Defaults to "ACTIVITY"; override it where the feed does
    /// not actually carry general activity — Quotes ships "RECENTLY ADDED"
    /// because deletes, edits and reads leave no trace.
    /// </summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>
    /// The lowercase subtitle naming what the feed covers — "this session" for
    /// an in-memory ring, "this tool" for a durable tool-wide table,
    /// "additions only" where the source is partial. Empty collapses it, but
    /// leaving it empty is a review finding, not a style choice: it is the only
    /// thing that stops a session ring from reading as durable history.
    /// </summary>
    public string Scope
    {
        get => (string)GetValue(ScopeProperty);
        set => SetValue(ScopeProperty, value);
    }

    /// <summary>Height cap on the scrolling row region. Default 240.</summary>
    public double MaxRowsHeight
    {
        get => (double)GetValue(MaxRowsHeightProperty);
        set => SetValue(MaxRowsHeightProperty, value);
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ToolActivityFeed f) f.Rows.ItemsSource = e.NewValue;
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ToolActivityFeed f) f.TitleText.Text = e.NewValue as string ?? "";
    }

    private static void OnScopeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ToolActivityFeed f) return;
        string scope = e.NewValue as string ?? "";
        f.ScopeText.Text = scope;
        f.ScopeText.Visibility = scope.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void OnMaxRowsHeightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ToolActivityFeed f && e.NewValue is double h) f.RowsScroll.MaxHeight = h;
    }
}
