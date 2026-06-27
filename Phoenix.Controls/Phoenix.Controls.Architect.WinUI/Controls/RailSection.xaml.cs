using System;
using System.Collections;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace Phoenix.Controls.Architect.WinUI.Controls;

public sealed partial class RailSection : UserControl
{
    /// <summary>
    /// Fires when the user taps a rail row. LeftRail subscribes and uses
    /// the selection to drive its Edit / Delete / Rename / Publish handlers
    /// instead of guessing via LastOrDefault() (Architect UX review P1-39).
    /// </summary>
    public event EventHandler<RailItemViewModel>? ItemSelected;

    /// <summary>
    ///  P1-A3: fires when the user double-taps a rail row.
    /// LeftRail subscribes and dispatches to <c>OpenMacroEditor</c> /
    /// <c>OpenProcessEditor</c> / variable-rename based on
    /// <see cref="RailItemViewModel.Kind"/>. Pre-T15 the WinForms
    /// MainForm wired equivalent <c>DoubleClick</c> handlers on the macro
    /// and process lists; the WinUI rewrite was tap-only until this
    /// sprint restored the double-click open-editor idiom.
    /// </summary>
    public event EventHandler<RailItemViewModel>? ItemDoubleTapped;

    /// <summary>
    /// Raised when <see cref="SelectedItem"/> transitions between null and
    /// non-null. Footer buttons (LeftRail.AddButton with requiresSelection)
    /// subscribe so they can flip <c>IsEnabled</c> live as the user taps
    /// around the rail. 0.10.0 Architect rail polish.
    /// </summary>
    public event EventHandler? SelectionChanged;

    /// <summary>
    ///   — raised when the user picks a different
    /// entry in the scope dropdown (Variables section only — the section
    /// ShowScope=true). The int arg is the new <c>ScopeCombo.SelectedIndex</c>
    /// (0 = Local / 1 = Global / 2 = Graph). <see cref="LeftRail"/> subscribes
    /// and rebuilds the Variables list against the chosen scope. Before this
    /// sprint the dropdown was visible + interactive but had no handler, so
    /// changing scope had zero effect.
    /// </summary>
    public event EventHandler<int>? ScopeChanged;

    /// <summary>
    ///  — the dropdown's current selection (0 = Local /
    /// 1 = Global / 2 = Graph), or 0 before realization. LeftRail reads this
    /// when it needs to re-evaluate the active scope outside a
    /// <see cref="ScopeChanged"/> turn (e.g. on a graph swap / Refresh()).
    /// </summary>
    public int ScopeIndex => ScopeCombo is null ? 0 : System.Math.Max(0, ScopeCombo.SelectedIndex);

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(RailSection),
            new PropertyMetadata(string.Empty, (d, e) =>
            {
                if (d is RailSection s) s.TitleText.Text = e.NewValue as string ?? string.Empty;
            }));

    public static readonly DependencyProperty HintProperty =
        DependencyProperty.Register(nameof(Hint), typeof(string), typeof(RailSection),
            new PropertyMetadata(string.Empty, (d, e) =>
            {
                if (d is RailSection s)
                {
                    s.HintText.Text = e.NewValue as string ?? string.Empty;
                    s.HintText.Visibility = string.IsNullOrEmpty(s.HintText.Text)
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                }
            }));

    public static readonly DependencyProperty ShowScopeProperty =
        DependencyProperty.Register(nameof(ShowScope), typeof(bool), typeof(RailSection),
            new PropertyMetadata(false, (d, e) =>
            {
                if (d is RailSection s)
                    s.ScopeCombo.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
            }));

    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(nameof(Items), typeof(IEnumerable), typeof(RailSection),
            new PropertyMetadata(null, (d, _) =>
            {
                // Items got rebound (e.g. LeftRail.Refresh() rebuilds the
                // ObservableCollection). Drop the stale selection so the
                // footer buttons don't keep acting on an item that no
                // longer exists.
                if (d is RailSection s)
                {
                    s.SetSelectedItem(null);
                    s.UpdateEmptyHint(); // 
                }
            }));

    //  Show the empty-section message when
    // the bound collection has no rows so an empty rail reads as intentional.
    private void UpdateEmptyHint()
    {
        if (EmptyHint is null) return;
        bool empty = Items switch
        {
            null => true,
            System.Collections.ICollection c => c.Count == 0,
            var e => !System.Linq.Enumerable.Any(System.Linq.Enumerable.Cast<object>(e)),
        };
        if (empty)
        {
            var t = Title;
            EmptyHint.Text = string.IsNullOrEmpty(t)
                ? "None yet"
                : $"No {t.ToLowerInvariant()} yet";
        }
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Currently-selected rail row (or null when nothing is tapped /
    /// after Items rebinds). Footer buttons on <c>LeftRail</c> gate their
    /// <c>IsEnabled</c> on this. Raises <see cref="SelectionChanged"/> on
    /// every transition so buttons can re-evaluate live. Architect UI P1.
    /// </summary>
    public static readonly DependencyProperty SelectedItemProperty =
        DependencyProperty.Register(nameof(SelectedItem), typeof(RailItemViewModel), typeof(RailSection),
            new PropertyMetadata(null, (d, e) =>
            {
                if (d is RailSection s) s.SelectionChanged?.Invoke(s, EventArgs.Empty);
            }));

    // Removes the 220px ScrollViewer cap when set true so a section can
    // grow into the parent rail's free vertical space (TODO 2026-05-07
    // P1 — LeftRail wastes vertical space). Default false keeps the
    // existing behaviour for sections that should stay short (Variables,
    // Processes); LeftRail flips this on for Macros.
    public static readonly DependencyProperty AllowGrowProperty =
        DependencyProperty.Register(nameof(AllowGrow), typeof(bool), typeof(RailSection),
            new PropertyMetadata(false, (d, e) =>
            {
                if (d is not RailSection s) return;
                bool allow = (bool)e.NewValue;
                s.ItemsRow.Height       = new GridLength(allow ? 1 : 0, allow ? GridUnitType.Star : GridUnitType.Auto);
                s.ItemsScroller.MaxHeight = allow ? double.PositiveInfinity : 220;
            }));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public bool ShowScope
    {
        get => (bool)GetValue(ShowScopeProperty);
        set => SetValue(ShowScopeProperty, value);
    }

    public IEnumerable? Items
    {
        get => (IEnumerable?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public RailItemViewModel? SelectedItem
    {
        get => (RailItemViewModel?)GetValue(SelectedItemProperty);
        private set => SetValue(SelectedItemProperty, value);
    }

    public bool AllowGrow
    {
        get => (bool)GetValue(AllowGrowProperty);
        set => SetValue(AllowGrowProperty, value);
    }

    public RailSection()
    {
        InitializeComponent();
        // Default to an empty observable collection so x:Bind has something
        // to anchor to before the parent (LeftRail) populates real items.
        Items = new ObservableCollection<RailItemViewModel>();

        //   — wire the scope dropdown so a scope
        // change actually surfaces to LeftRail. The combo is only meaningful
        // on the Variables section (ShowScope=true); on hidden sections the
        // user never interacts with it so the event effectively never fires.
        // Forwarding is unconditional — LeftRail's handler is idempotent and
        // safe for the synthetic SelectionChanged that fires while the combo
        // realizes its SelectedIndex=0 default (at that point LeftRail isn't
        // yet bound to a graph, so the rebuild is a no-op).
        ScopeCombo.SelectionChanged += OnScopeComboSelectionChanged;
    }

    private void OnScopeComboSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ScopeChanged?.Invoke(this, ScopeIndex);
    }

    /// <summary>
    ///  — programmatically set the scope dropdown selection (0 =
    /// Local / 1 = Global / 2 = Graph) so LeftRail can align the visible
    /// combo with its default scope. Clamps to the combo's item range and
    /// no-ops when the value is already current (so it doesn't fire a
    /// redundant <see cref="ScopeChanged"/>). Setting it here DOES raise
    /// ScopeChanged through the normal SelectionChanged path, which is what
    /// LeftRail wants — the rail rebuilds to match the new selection.
    /// </summary>
    public void SetScopeIndex(int index)
    {
        if (ScopeCombo is null) return;
        int max = ScopeCombo.Items.Count - 1;
        if (max < 0) return;
        int clamped = System.Math.Clamp(index, 0, max);
        if (ScopeCombo.SelectedIndex == clamped) return;
        ScopeCombo.SelectedIndex = clamped;
    }

    /// <summary>
    /// Drag-source handler — packages the rail item's Kind/Id/Name as a
    /// "phx:rail" data string so the canvas can spawn the right node type
    /// at the drop point.
    /// </summary>
    private void OnRailItemDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        //  [P2] — require Kind AND Id AND Name to be non-empty before
        // packaging the drag payload. RailItemViewModel.Id/Name default to
        // string.Empty, so a half-built row (e.g. a freshly-added variable
        // before Refresh() back-fills its Id) could otherwise be dragged into
        // an invalid "phx-rail" payload the canvas drop handler can't resolve.
        if (sender is FrameworkElement fe
            && fe.Tag is RailItemViewModel item
            && !string.IsNullOrEmpty(item.Kind)
            && !string.IsNullOrEmpty(item.Id)
            && !string.IsNullOrEmpty(item.Name))
        {
            args.Data.Properties["phx-rail-kind"] = item.Kind;
            args.Data.Properties["phx-rail-id"]   = item.Id;
            args.Data.Properties["phx-rail-name"] = item.Name;
            args.Data.RequestedOperation = DataPackageOperation.Copy;
            args.Data.SetText($"{item.Kind}:{item.Id}:{item.Name}");
        }
    }

    /// <summary>
    /// Mark the tapped row as IsSelected = true (and clear every sibling)
    /// so the row background highlights. Raises <see cref="ItemSelected"/>
    /// so the parent LeftRail can route toolbar actions to the choice.
    /// Architect UX review P1-39.
    /// </summary>
    private void OnRailItemTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.Tag is not RailItemViewModel item) return;
        SelectItem(item);
        ItemSelected?.Invoke(this, item);
        e.Handled = true;
    }

    /// <summary>
    ///  P1-A3: route the double-tapped row back to LeftRail via
    /// <see cref="ItemDoubleTapped"/> so the parent can open the appropriate
    /// editor (macro / process sub-graph window or variable rename dialog).
    /// Also flips the selection so a follow-up toolbar action picks the
    /// same row — matches the WinForms idiom where a DoubleClick implicitly
    /// raised SelectionChanged first.
    /// </summary>
    private void OnRailItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.Tag is not RailItemViewModel item) return;
        SelectItem(item);
        ItemDoubleTapped?.Invoke(this, item);
        e.Handled = true;
    }

    /// <summary>
    /// Clear every row's IsSelected then flip the picked item's true. Public
    /// so the parent can sync external selections (e.g. after Refresh()).
    /// Also updates the live <see cref="SelectedItem"/> DP so footer buttons
    /// can gate their enablement on selection state.
    /// </summary>
    public void SelectItem(RailItemViewModel? picked)
    {
        if (Items is IEnumerable list)
        {
            foreach (var entry in list)
                if (entry is RailItemViewModel v)
                    v.IsSelected = ReferenceEquals(v, picked);
        }
        SetSelectedItem(picked);
    }

    private void SetSelectedItem(RailItemViewModel? picked)
    {
        var current = SelectedItem;
        if (ReferenceEquals(current, picked)) return;
        SelectedItem = picked;
    }
}
