using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Architect.WinUI.ViewModels;
using Windows.UI;

namespace Phoenix.Controls.Architect.WinUI.Controls;

/// <summary>
/// VM the rail's ItemsRepeater binds to. Now also carries the graph-side
/// kind ("macro" / "process" / "variable") and the underlying id so the
/// item can drag-drop onto the canvas to spawn a Macro.Call / Process.Spawn /
/// Var.Get node bound to the real entity. Inherits ObservableObject so the
/// row visual updates when IsSelected flips (Architect UX review P1-39 —
/// pre-fix rail items had no selection state and the toolbar silently acted
/// on Variables[^1] / Macros.LastOrDefault()).
/// </summary>
public sealed class RailItemViewModel : ObservableObject
{
    private bool _isSelected;

    public string Name { get; init; } = string.Empty;

    /// <summary>"macro" / "process" / "variable" / "" (placeholder).</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Macro.MacroId / Process.ProcessId / VariableDefinition.Name.</summary>
    public string Id   { get; init; } = string.Empty;

    /// <summary>
    /// Free-form right-side label: "int" / "string" / "●running" / "GLOBAL" / "local".
    /// The design uses this slot for both type names and status pills.
    /// </summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// Hex color for the 8x8 left-edge square. "#RRGGBB".
    /// </summary>
    public string Color { get; init; } = "#A89683";

    /// <summary>
    /// True when this row is the active selection in its rail section. The
    /// row template binds <see cref="SelectionBrush"/> off this flag so the
    /// background highlights — and the toolbar buttons on the parent
    /// <c>LeftRail</c> use the selection to decide which item to act on
    /// instead of guessing via LastOrDefault().
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetField(ref _isSelected, value))
                OnPropertyChanged(nameof(SelectionBrush));
        }
    }

    //  Shared, allocation-free selection brushes. The
    // SelectionBrush getter is hit on every binding re-read (selection toggles,
    // rail rebuilds) — pre-fix each read allocated a fresh SolidColorBrush.
    //  Selected opacity raised from 0x55
    // (33% — too faint to scan, risking rename/delete of the wrong row) to 0xB0
    // (~69%) so the active row is unmistakable. Brushes are read-only after
    // creation (bound as Background, never mutated) so one shared instance is safe.
    private static readonly SolidColorBrush s_selectedBrush =
        new(Windows.UI.Color.FromArgb(0xB0, 0xE5, 0xA2, 0x4E));
    private static readonly SolidColorBrush s_unselectedBrush =
        new(Microsoft.UI.Colors.Transparent);

    /// <summary>Background brush — Ember-tinted when selected, transparent otherwise.</summary>
    public Brush SelectionBrush => _isSelected ? s_selectedBrush : s_unselectedBrush;

    //  Color is init-only, so the swatch brush is
    // computed once and cached instead of re-parsed + allocated per binding read.
    private Brush? _colorBrush;
    public Brush ColorBrush => _colorBrush ??= new SolidColorBrush(ParseHex(Color));

    private static Color ParseHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex[0] != '#' || hex.Length != 7)
            return Microsoft.UI.Colors.Gray;
        try
        {
            byte r = Convert.ToByte(hex.Substring(1, 2), 16);
            byte g = Convert.ToByte(hex.Substring(3, 2), 16);
            byte b = Convert.ToByte(hex.Substring(5, 2), 16);
            return Windows.UI.Color.FromArgb(0xFF, r, g, b);
        }
        catch
        {
            return Microsoft.UI.Colors.Gray;
        }
    }
}
