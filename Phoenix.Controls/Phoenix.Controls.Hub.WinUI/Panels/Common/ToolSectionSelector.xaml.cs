using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Phoenix.Controls.Hub.WinUI.Panels.Common;

/// <summary>
/// Segmented section selector (see ToolSectionSelector.xaml for the visual
/// recipe and for why it is not a Pivot).
///
/// Usage: give it <see cref="Sections"/> and bind <see cref="SelectedIndex"/>
/// TwoWay to an int on the VM; drive each section body's Visibility off that
/// same int. This control owns no content.
///
/// <code>
/// &lt;cmn:ToolSectionSelector Sections="{x:Bind ViewModel.SectionNames}"
///                             SelectedIndex="{x:Bind ViewModel.SelectedSection, Mode=TwoWay}" /&gt;
/// </code>
///
/// <see cref="SelectionChanged"/> fires on every change of SelectedIndex,
/// whether it came from a chip click or from the VM — it reports the property,
/// not the gesture. A binding alone is enough for most pages; the event is for
/// pages that must run work on a switch.
///
/// An out-of-range SelectedIndex is left alone rather than coerced, so no chip
/// renders selected. Coercing would fight a TwoWay binding whose source is
/// mid-initialization.
///
/// CASING IS THE CONTROL'S, NOT THE CALLER'S. Chips render uppercase whatever
/// casing <see cref="Sections"/> arrives in, because the chip is drawn in the
/// band's eyebrow tier (11px bold, +100 tracking) and Title Case reads as a
/// different tier next to it. Four pages feed this control and they had drifted
/// into two conventions — Automod and Loyalty pass Title Case, SongRequest and
/// UserManagement pass caps — so the transform lives here instead of in four
/// call sites. UserManagement is the reason it cannot live in the callers at
/// all: several of its labels come back from <c>Localizer.T</c>, so their casing
/// is a translator's choice, not the page's.
/// </summary>
public sealed partial class ToolSectionSelector : UserControl
{
    // One collection instance for the control's lifetime. The ItemsRepeater's
    // source is assigned once in the constructor and only ever mutated in place;
    // reassigning it tears down virtualization and loses scroll position.
    private readonly ObservableCollection<ToolSectionChipVm> _chips = new();

    public ToolSectionSelector()
    {
        InitializeComponent();
        Chips.ItemsSource = _chips;

        // The item template binds AutomationProperties.Name to the same Label the
        // TextBlock draws, and that Label is now shouted. Narration is restored
        // per-element here rather than by re-pointing the binding, so the template
        // stays untouched. No unsubscribe: the repeater is this control's own child.
        Chips.ElementPrepared += OnElementPrepared;
    }

    public static readonly DependencyProperty SectionsProperty =
        DependencyProperty.Register(nameof(Sections), typeof(IList<string>), typeof(ToolSectionSelector),
            new PropertyMetadata(null, OnSectionsChanged));

    public static readonly DependencyProperty SelectedIndexProperty =
        DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(ToolSectionSelector),
            new PropertyMetadata(0, OnSelectedIndexChanged));

    /// <summary>
    /// The section labels, in order. Read once per assignment — this control
    /// does not observe the list for later mutation, because the sections of a
    /// tool page are fixed at design time.
    /// </summary>
    public IList<string>? Sections
    {
        get => (IList<string>?)GetValue(SectionsProperty);
        set => SetValue(SectionsProperty, value);
    }

    /// <summary>The selected section's zero-based index. Bind TwoWay.</summary>
    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>Raised after <see cref="SelectedIndex"/> changes. Payload is the new index.</summary>
    public event EventHandler<int>? SelectionChanged;

    private static void OnSectionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ToolSectionSelector s) s.RebuildChips();
    }

    private static void OnSelectedIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ToolSectionSelector s) return;
        s.ApplySelection();
        s.SelectionChanged?.Invoke(s, e.NewValue is int i ? i : s.SelectedIndex);
    }

    private void RebuildChips()
    {
        _chips.Clear();
        if (Sections is { } src)
        {
            for (int i = 0; i < src.Count; i++)
                _chips.Add(new ToolSectionChipVm(src[i] ?? "", i));
        }
        ApplySelection();
    }

    private void ApplySelection()
    {
        int selected = SelectedIndex;
        foreach (var chip in _chips)
            chip.IsSelected = chip.Index == selected;
    }

    // The house row-click idiom: pattern-match the realized element's
    // DataContext. ItemsRepeater has no selection model, so selection is always
    // the host's state, never the list's.
    //
    // ★ The DataContext this reads is supplied by RowDataContext.Supply on the
    // repeater — WinUI leaves it NULL for any template that uses x:Bind, which is
    // what made every chip in this strip inert on 1.1.7. See RowDataContext.
    private void OnChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ToolSectionChipVm chip)
            SelectedIndex = chip.Index;
    }

    // Hands assistive tech the label as the caller wrote it — "Rules", not
    // "RULES". Only the pixels are shouted; a screen reader reading caps can
    // spell them out or shift emphasis.
    //
    // ElementPrepared runs after the element's bindings have been processed, so
    // this local value replaces the one the template's OneTime x:Bind set, and it
    // is re-applied every time a recycled chip is bound to another section. Same
    // realization idiom ScriptView's row selection uses.
    //
    // ★ Resolved from args.Index, NOT from DataContext. This is the one place
    // DataContext must not be trusted: RowDataContext supplies it from its own
    // ElementPrepared handler, and two handlers on the same event have no
    // guaranteed order. The index is authoritative at any point in realization.
    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not FrameworkElement fe) return;
        int index = args.Index;
        if (index < 0 || index >= _chips.Count) return;
        AutomationProperties.SetName(fe, _chips[index].AutomationLabel);
    }
}

/// <summary>
/// One chip in a <see cref="ToolSectionSelector"/>. Observable only because the
/// three brushes flip when selection moves; the label and index are fixed at
/// construction.
/// </summary>
public sealed class ToolSectionChipVm : ObservableObject
{
    // Selected-chip tints — the DEFAULT-badge pair used across the house tool
    // pages. Literal alpha ramps over EmberPrimary, so they have no theme key.
    private static readonly Brush SelectedFill =
        new SolidColorBrush(Color.FromArgb(0x14, 0xE5, 0xA2, 0x4E));
    private static readonly Brush SelectedBorder =
        new SolidColorBrush(Color.FromArgb(0x73, 0xE5, 0xA2, 0x4E));

    internal ToolSectionChipVm(string label, int index)
    {
        AutomationLabel = label;
        Label = label.ToUpperInvariant();
        Index = index;
    }

    /// <summary>
    /// The drawn label, uppercased. Invariant rather than current-culture: the
    /// casing is a typographic tier, and a culture-sensitive upcast can turn a
    /// label into a different string (the Turkish dotless-i pair being the usual
    /// one). The original is kept on <see cref="AutomationLabel"/>.
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// The label exactly as the caller wrote it, for narration. Read by
    /// ToolSectionSelector.OnElementPrepared, not bound from the template.
    /// </summary>
    public string AutomationLabel { get; }

    public int Index { get; }

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!Set(ref _isSelected, value)) return;
            Raise(nameof(ChipBackground));
            Raise(nameof(ChipBorderBrush));
            Raise(nameof(ChipForeground));
            Raise(nameof(NavUnderlineBrush));
        }
    }

    // ChipBackground / ChipBorderBrush are retained but UNUSED by the
    // 2026-08-14 text-nav template, which paints a transparent ground and no
    // border. Kept because they are the house DEFAULT-badge tint pair and
    // removing public members that a future chip rendering (or an external
    // binding) may want buys nothing.
    public Brush ChipBackground => _isSelected
        ? SelectedFill
        : ToolBrushes.Lookup("CoalCardBrush", 0x22, 0x1C, 0x16);

    public Brush ChipBorderBrush => _isSelected
        ? SelectedBorder
        : ToolBrushes.Lookup("CoalDividerBrush", 0x3A, 0x31, 0x27);

    /// <summary>
    /// Nav label colour. Paper when selected, so the active section reads at the
    /// page's own title tier. (Was ember-on-chip; with the chip ground gone,
    /// ember at 11 px on bare coal reads as a hyperlink rather than a selected
    /// tab, so the ember moved to the underline.)
    /// </summary>
    public Brush ChipForeground => _isSelected
        ? ToolBrushes.Lookup("CoalPaperBrush", 0xF5, 0xEF, 0xE3)
        : ToolBrushes.Lookup("CoalSecondaryTextBrush", 0x9C, 0x8A, 0x72);

    /// <summary>
    /// The 2 px bottom edge marking the active section. Explicitly transparent
    /// rather than null when unselected: the Border reserves its 2 px either
    /// way, so an explicit brush keeps every label on one baseline instead of
    /// letting the selected one shift up.
    /// </summary>
    public Brush NavUnderlineBrush => _isSelected
        ? ToolBrushes.Lookup("EmberPrimaryBrush", 0xE5, 0xA2, 0x4E)
        : new SolidColorBrush(Color.FromArgb(0x00, 0x00, 0x00, 0x00));
}
