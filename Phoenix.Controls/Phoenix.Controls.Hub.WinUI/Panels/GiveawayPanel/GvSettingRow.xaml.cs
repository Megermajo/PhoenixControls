using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Phoenix.Controls.Hub.WinUI.Panels.GiveawayPanel;

/// <summary>
/// One label/value settings row for the Giveaway detail Settings card
/// (giveaway.jsx CodePath). <see cref="IsLast"/> drops the bottom divider on
/// the final row of the card.
/// </summary>
public sealed partial class GvSettingRow : UserControl
{
    public GvSettingRow()
    {
        InitializeComponent();
        ApplyIsLast();
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(GvSettingRow),
            new PropertyMetadata(string.Empty));

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(GvSettingRow),
            new PropertyMetadata(string.Empty));

    public bool IsLast
    {
        get => (bool)GetValue(IsLastProperty);
        set => SetValue(IsLastProperty, value);
    }

    public static readonly DependencyProperty IsLastProperty =
        DependencyProperty.Register(nameof(IsLast), typeof(bool), typeof(GvSettingRow),
            new PropertyMetadata(false, OnIsLastChanged));

    private static void OnIsLastChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GvSettingRow row) row.ApplyIsLast();
    }

    private void ApplyIsLast()
    {
        RowBorder.BorderThickness = IsLast ? new Thickness(0) : new Thickness(0, 0, 0, 1);
    }
}
