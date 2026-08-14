using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Hub.WinUI.Animation;

namespace Phoenix.Controls.Hub.WinUI.Panels.Common;

/// <summary>
/// Shared "stat tile" (big value + small caption) for the Pre-Build tool pages —
/// the GvStatTile / TmStatTile / AmStatTile recipe promoted into Common so every
/// config tool consumes one control. Caption + Value are dependency properties so
/// pages can x:Bind them OneWay to VM strings; <see cref="Accent"/> marks the one
/// tile per strip whose value renders in ember.
/// </summary>
public sealed partial class ToolStatTile : UserControl
{
    public ToolStatTile()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty CaptionProperty =
        DependencyProperty.Register(nameof(Caption), typeof(string), typeof(ToolStatTile),
            new PropertyMetadata("", OnCaptionChanged));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(ToolStatTile),
            new PropertyMetadata("", OnValueChanged));

    public static readonly DependencyProperty AccentProperty =
        DependencyProperty.Register(nameof(Accent), typeof(bool), typeof(ToolStatTile),
            new PropertyMetadata(false, OnAccentChanged));

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>True renders the value in ember instead of paper-white. The house
    /// stat strip anchors exactly ONE tile that way (GvStatTile's Tickets tile);
    /// leave it false on the rest.</summary>
    public bool Accent
    {
        get => (bool)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    private static void OnCaptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ToolStatTile t) t.CaptionText.Text = e.NewValue as string ?? "";
    }

    // Flash the value on change, but never on the FIRST assignment (initial
    // data-bind) or while off-screen (tab virtualization). Mirrors
    // GvStatTile.FlashOnChange / AmStatTile.OnValueChanged.
    private bool _valuePrimed;

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ToolStatTile t) return;
        string? newValue = e.NewValue as string;
        t.ValueText.Text = newValue ?? "";

        if (!t._valuePrimed) { t._valuePrimed = true; return; }
        if (!t.IsLoaded) return;
        if (string.Equals(e.OldValue as string, newValue, StringComparison.Ordinal)) return;
        AnimateExtensions.PulseScale(t.ValueText);
    }

    private static void OnAccentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ToolStatTile t) t.ApplyAccent();
    }

    // Only ever runs once a consumer opts in — the XAML already paints the
    // default CoalPaperBrush, so an untouched tile never reaches this lookup and
    // renders exactly as before. Brushes come from the app dictionary by key
    // because a StaticResource cannot be chosen at runtime; both keys are
    // resolved so flipping Accent back to false restores paper-white. Mirrors
    // GvStatTile.ApplyAccent.
    private void ApplyAccent()
    {
        string key = Accent ? "Ember200Brush" : "CoalPaperBrush";
        if (Application.Current?.Resources is { } res
            && res.TryGetValue(key, out var resource)
            && resource is Brush brush)
        {
            ValueText.Foreground = brush;
        }
    }
}
