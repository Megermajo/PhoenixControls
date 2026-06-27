using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Phoenix.Controls.Hub.WinUI.Panels.GiveawayPanel;

/// <summary>
/// Single stat tile for the Giveaway detail card (giveaway.jsx StateStat) —
/// eyebrow + large display value. <see cref="Accent"/> flips the value brush
/// to ember (the design accents the Tickets tile).
/// </summary>
public sealed partial class GvStatTile : UserControl
{
    public GvStatTile()
    {
        InitializeComponent();
        ApplyAccent();
    }

    public string Eyebrow
    {
        get => (string)GetValue(EyebrowProperty);
        set => SetValue(EyebrowProperty, value);
    }

    public static readonly DependencyProperty EyebrowProperty =
        DependencyProperty.Register(nameof(Eyebrow), typeof(string), typeof(GvStatTile),
            new PropertyMetadata(string.Empty));

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(GvStatTile),
            new PropertyMetadata(string.Empty));

    public bool Accent
    {
        get => (bool)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public static readonly DependencyProperty AccentProperty =
        DependencyProperty.Register(nameof(Accent), typeof(bool), typeof(GvStatTile),
            new PropertyMetadata(false, OnAccentChanged));

    private static void OnAccentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GvStatTile tile) tile.ApplyAccent();
    }

    private void ApplyAccent()
    {
        // Accent tile (Tickets) → ember value; otherwise coal paper-white.
        string key = Accent ? "Ember200Brush" : "CoalPaperBrush";
        if (Application.Current?.Resources is { } res
            && res.TryGetValue(key, out var resource)
            && resource is Brush brush)
        {
            ValueText.Foreground = brush;
        }
    }
}
