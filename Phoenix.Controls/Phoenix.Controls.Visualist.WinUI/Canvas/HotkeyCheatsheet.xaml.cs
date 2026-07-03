using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Visualist.WinUI.Canvas;

/// <summary>
/// Bottom-left context-aware hotkey overlay for Visualist's canvases.
/// Sibling of <c>Architect.WinUI.Canvas.HotkeyCheatsheet</c> — same shape,
/// independent code per the chrome-independence rule.
/// </summary>
public sealed partial class HotkeyCheatsheet : UserControl
{
    private const int MaxRows = 10;

    private VisualistHotkeyContext _context = VisualistHotkeyContext.LayerCanvas;
    private bool _expanded;

    public HotkeyCheatsheet()
    {
        InitializeComponent();
        _expanded = ConfigManager.Current.VisualistHotkeyCheatsheetExpanded;
        ApplyExpandedVisual();
        RebuildBody();
    }

    /// <summary>
    /// Push the active canvas / selection mode in. Cheap; the body only
    /// re-renders when the context actually changes.
    /// </summary>
    public void SetContext(VisualistHotkeyContext context)
    {
        if (_context == context) return;
        _context = context;
        ContextLabel.Text = LabelFor(context);
        RebuildBody();
    }

    private static string LabelFor(VisualistHotkeyContext context) => context switch
    {
        VisualistHotkeyContext.LayerCanvas                => "LAYER CANVAS",
        VisualistHotkeyContext.LayerWidgetSelected        => "WIDGET SELECTED",
        VisualistHotkeyContext.LayerMultiSelection        => "MULTI-SELECTION",
        VisualistHotkeyContext.WidgetGraph                => "WIDGET GRAPH",
        VisualistHotkeyContext.WidgetGraphNodeSelected    => "NODE SELECTED",
        VisualistHotkeyContext.WidgetGraphMultiSelection  => "MULTI-SELECTION",
        VisualistHotkeyContext.WidgetGraphDraggingWire    => "DRAGGING WIRE",
        VisualistHotkeyContext.WidgetGraphPanning         => "PANNING",
        _                                                 => "CANVAS",
    };

    // ── Toggle wiring ────────────────────────────────────────────────────

    private void OnHeaderTapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && fe.Name == nameof(ToggleButton))
            return;
        FlipExpanded();
        e.Handled = true;
    }

    private void OnToggleClicked(object sender, RoutedEventArgs e) => FlipExpanded();

    private void FlipExpanded()
    {
        _expanded = !_expanded;
        ApplyExpandedVisual();
        ConfigManager.Current.VisualistHotkeyCheatsheetExpanded = _expanded;
        ConfigManager.SaveDeferred(Paths.AppConfigJson);
    }

    private void ApplyExpandedVisual()
    {
        BodyHairline.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        BodyBorder  .Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        ToggleButton.Content    = _expanded ? "—" : "+";
        ToolTipService.SetToolTip(ToggleButton, _expanded ? "Collapse" : "Expand");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            ToggleButton, _expanded ? "Collapse hotkey cheatsheet" : "Expand hotkey cheatsheet");
    }

    // ── Hover affordance ─────────────────────────────────────────────────

    private void OnHeaderPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b
            && Application.Current?.Resources["CoalHoverBrush"] is Brush brush)
        {
            b.Background = brush;
        }
    }

    private void OnHeaderPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Background = null;
    }

    // ── Body rendering ───────────────────────────────────────────────────

    private void RebuildBody()
    {
        EntryGrid.Children.Clear();
        EntryGrid.RowDefinitions.Clear();

        IReadOnlyList<VisualistHotkeyEntry> entries = VisualistHotkeyCatalog.GetForContext(_context);
        int rendered = 0;
        for (int i = 0; i < entries.Count && rendered < MaxRows; i++)
        {
            var entry = entries[i];
            AddRow(entry.Combo, entry.Description, rendered);
            rendered++;
        }

        if (rendered == 0)
        {
            EntryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var hint = new TextBlock
            {
                Text = "No context-specific chords.",
                Style = (Style)Resources["CheatsheetDescription"],
                Opacity = 0.7,
                Margin = new Thickness(0, 2, 0, 2),
            };
            Grid.SetColumn(hint, 0);
            Grid.SetColumnSpan(hint, 2);
            EntryGrid.Children.Add(hint);
        }
    }

    private void AddRow(string combo, string description, int row)
    {
        EntryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var comboText = new TextBlock
        {
            Text  = combo,
            Style = (Style)Resources["CheatsheetCombo"],
            Margin = new Thickness(0, 2, 12, 2),
        };
        Grid.SetColumn(comboText, 0);
        Grid.SetRow(comboText, row);

        var descText = new TextBlock
        {
            Text  = description,
            Style = (Style)Resources["CheatsheetDescription"],
            Margin = new Thickness(0, 2, 0, 2),
        };
        Grid.SetColumn(descText, 1);
        Grid.SetRow(descText, row);

        EntryGrid.Children.Add(comboText);
        EntryGrid.Children.Add(descText);
    }
}
