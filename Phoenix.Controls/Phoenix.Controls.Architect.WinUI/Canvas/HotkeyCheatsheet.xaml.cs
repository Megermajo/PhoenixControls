using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

/// <summary>
/// Bottom-left context-aware hotkey overlay for the Architect canvas.
/// <para/>
/// Visual contract:
/// <list type="bullet">
///   <item>Header strip is always visible (chip-style) and toggleable.</item>
///   <item>Body lists the chords relevant to the current
///         <see cref="HotkeyContext"/>, capped at <see cref="MaxRows"/> rows
///         so the overlay never grows past a comfortable footprint at the
///         bottom-left of the canvas.</item>
///   <item>Open/closed state persists across restarts via
///         <c>AppConfig.ArchitectHotkeyCheatsheetExpanded</c> with the
///         off-thread write path
///         (<see cref="ConfigManager.SaveDeferred"/>) to avoid the
///         OneDrive-latency UI-thread freeze class flagged in
///         <c>project_freeze_diagnostics</c>.</item>
/// </list>
/// </summary>
public sealed partial class HotkeyCheatsheet : UserControl
{
    /// <summary>
    /// How many entries the body renders. The full reference lives in
    /// <c>KeyboardShortcutsDialog</c> (F1-on-empty-canvas, Help menu) — this
    /// overlay is the compact context summary, not a replacement.
    /// </summary>
    private const int MaxRows = 10;

    private HotkeyContext _context = HotkeyContext.Canvas;
    private bool _expanded;

    public HotkeyCheatsheet()
    {
        InitializeComponent();
        // Default-true on first launch per the design call. The setter
        // sync handles XAML state; the config write is gated to user-driven
        // toggles only (no write during construction).
        _expanded = ConfigManager.Current.ArchitectHotkeyCheatsheetExpanded;
        ApplyExpandedVisual();
        RebuildBody();
    }

    /// <summary>
    /// Push the current canvas interaction mode in from
    /// <c>LogicCanvasView</c>. Cheap to call; the body only re-renders when
    /// the context actually changes.
    /// </summary>
    public void SetContext(HotkeyContext context)
    {
        if (_context == context) return;
        _context = context;
        ContextLabel.Text = LabelFor(context);
        RebuildBody();
    }

    private static string LabelFor(HotkeyContext context) => context switch
    {
        HotkeyContext.Canvas         => "CANVAS",
        HotkeyContext.NodeSelected   => "NODE SELECTED",
        HotkeyContext.MultiSelection => "MULTI-SELECTION",
        HotkeyContext.WireSelected   => "WIRE SELECTED",
        HotkeyContext.FrameSelected  => "FRAME SELECTED",
        HotkeyContext.DraggingWire   => "DRAGGING WIRE",
        HotkeyContext.Panning        => "PANNING",
        HotkeyContext.TextEditing    => "EDITING",
        _                            => "CANVAS",
    };

    // ── Toggle wiring ────────────────────────────────────────────────────

    private void OnHeaderTapped(object sender, TappedRoutedEventArgs e)
    {
        // Tapping the chip body toggles expand/collapse, mirroring the
        // explicit toggle button. The button's Click ALSO bubbles a Tapped
        // up to this Border, so both handlers fire on a single click — pre-
        // fix the guard only matched when OriginalSource was the literal
        // ToggleButton element, but a click on the button's content
        // template makes OriginalSource an inner ContentPresenter / Run
        // and the guard silently failed → FlipExpanded ran twice → the
        // visual state ended where it started, which Majo reported as
        // "needs double click to toggle". Walking up the visual tree to
        // detect ANY Button ancestor catches all the inner template
        // surfaces uniformly.
        if (IsInsideToggleButton(e.OriginalSource)) return;
        FlipExpanded();
        e.Handled = true;
    }

    /// <summary>
    /// True when <paramref name="source"/> sits inside the
    /// <see cref="ToggleButton"/> (or any descendant of it) in the visual
    /// tree. Used by <see cref="OnHeaderTapped"/> to skip the double-flip
    /// described in its comment block.
    /// </summary>
    private bool IsInsideToggleButton(object? source)
    {
        DependencyObject? n = source as DependencyObject;
        while (n is not null)
        {
            if (ReferenceEquals(n, ToggleButton)) return true;
            n = VisualTreeHelper.GetParent(n);
        }
        return false;
    }

    private void OnToggleClicked(object sender, RoutedEventArgs e) => FlipExpanded();

    private void FlipExpanded()
    {
        _expanded = !_expanded;
        ApplyExpandedVisual();
        // Persist the choice off the UI thread per
        // project_freeze_diagnostics — the deferred path serialises in-
        // memory on the caller (sub-ms) and offloads the disk write to
        // the thread pool.
        ConfigManager.Current.ArchitectHotkeyCheatsheetExpanded = _expanded;
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

    // ── Hover affordance — subtle background lift on the header chip ─────

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

        IReadOnlyList<HotkeyEntry> entries = ArchitectHotkeyCatalog.GetForContext(_context);
        int rendered = 0;
        for (int i = 0; i < entries.Count && rendered < MaxRows; i++)
        {
            var entry = entries[i];
            AddRow(entry.Combo, entry.Description, rendered);
            rendered++;
        }

        //  When the context has more chords than
        // fit, append a "+N more — F1 for full list" row instead of silently
        // dropping the overflow (pre-fix the surplus just vanished).
        if (entries.Count > MaxRows)
        {
            int more = entries.Count - MaxRows;
            EntryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var overflow = new TextBlock
            {
                Text = $"+{more} more — F1 for the full shortcut list",
                Style = (Style)Resources["CheatsheetDescription"],
                Opacity = 0.7,
                Margin = new Thickness(0, 4, 0, 2),
            };
            Grid.SetRow(overflow, EntryGrid.RowDefinitions.Count - 1);
            Grid.SetColumn(overflow, 0);
            Grid.SetColumnSpan(overflow, 2);
            EntryGrid.Children.Add(overflow);
        }

        // When the body is empty (no chord targets the current context —
        // rare; mostly catches transitional states like DraggingWire) leave
        // a single muted hint rather than a collapsed-looking blank panel.
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
