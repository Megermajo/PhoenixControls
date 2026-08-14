using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Phoenix.Controls.Hub.WinUI.Panels.Common;

/// <summary>
/// Display-only boolean pill (see TogglePill.xaml for the geometry rationale).
/// Bind <see cref="IsOn"/> OneWay; put the Click, the accessible name and any
/// IsEnabled gate on the host Button.
///
/// The four brushes are resolved from the app theme by key rather than declared
/// as literals. The Timer reference hard-codes the same four Colors
/// (TimerView.xaml.cs:451-454) and they are byte-for-byte the PhoenixDark
/// tokens used here — EmberPrimary #E5A24E, CoalDivider #3A3127, CoalShell
/// #0B0907, CoalSecondaryText #9C8A72 — so the rendered pixels are identical
/// while a palette edit in PhoenixDark.xaml now reaches the pill.
/// </summary>
public sealed partial class TogglePill : UserControl
{
    public TogglePill()
    {
        InitializeComponent();

        // Repaint on Loaded as well as on change: the tool tabs are lazily
        // realized and recycled (the Pre-Builds rail sets ContentHost.Content = null on
        // close), so a pill can be constructed with IsOn already bound — no
        // property-changed callback ever fires for the initial value.
        Loaded += OnLoaded;

        Apply();
    }

    public static readonly DependencyProperty IsOnProperty =
        DependencyProperty.Register(nameof(IsOn), typeof(bool), typeof(TogglePill),
            new PropertyMetadata(false, OnIsOnChanged));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(TogglePill),
            new PropertyMetadata("", OnLabelChanged));

    /// <summary>The boolean this pill displays. Bind OneWay from the VM.</summary>
    public bool IsOn
    {
        get => (bool)GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    /// <summary>
    /// Optional caption to the right of the track. It takes the ON / OFF accent
    /// with the track. Empty (the default) collapses the TextBlock so the pill
    /// contributes no trailing gap.
    /// </summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    private static void OnIsOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TogglePill p) p.Apply();
    }

    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TogglePill p) p.ApplyLabel();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Apply();

    private void Apply()
    {
        ApplyToggle();
        ApplyLabel();
    }

    // The extracted form of TimerView.xaml.cs:466-472 (ApplyPillToggle).
    private void ApplyToggle()
    {
        bool on = IsOn;

        Knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        Knob.Margin = on ? new Thickness(0, 0, 1, 0) : new Thickness(1, 0, 0, 0);
        Knob.Fill = on
            ? ToolBrushes.Lookup("CoalShellBrush", 0x0B, 0x09, 0x07)
            : ToolBrushes.Lookup("CoalSecondaryTextBrush", ToolBrushes.NeutralFallbackColor);
        Track.Background = on
            ? ToolBrushes.Lookup("EmberPrimaryBrush", 0xE5, 0xA2, 0x4E)
            : ToolBrushes.Lookup("CoalDividerBrush", 0x3A, 0x31, 0x27);
    }

    // Matches TimerView.xaml.cs:460 — the label tracks the pill's accent.
    private void ApplyLabel()
    {
        string text = Label ?? "";
        LabelText.Text = text;
        LabelText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        LabelText.Foreground = IsOn
            ? ToolBrushes.Lookup("EmberPrimaryBrush", 0xE5, 0xA2, 0x4E)
            : ToolBrushes.Lookup("CoalSecondaryTextBrush", ToolBrushes.NeutralFallbackColor);
    }
}
