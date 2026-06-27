using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Shared.Localization;
using System;
using Windows.System;
using Windows.UI;

namespace Phoenix.Controls.Hub.WinUI.Controls;

/// <summary>
/// Custom min / max / close button — redesign R2 / chrome.jsx TrafficLight.
/// 38 × 30 fixed footprint; the glyph is drawn from a Path geometry rather
/// than a Segoe Fluent codepoint so the stroke weight matches the mock
/// exactly. The hover background is per-kind (coal hover for min/max, err
/// red for close) and only the close button shifts the glyph foreground to
/// paper on hover for contrast.
/// </summary>
public sealed partial class TrafficLightButton : UserControl
{
    private static readonly Color ColorTransparent      = Color.FromArgb(0x00, 0, 0, 0);
    private static readonly Color ColorCoalHover        = Color.FromArgb(0xFF, 0x2C, 0x25, 0x1D);
    private static readonly Color ColorErr              = Color.FromArgb(0xFF, 0xC9, 0x53, 0x3C);
    private static readonly Color ColorCoalBody         = Color.FromArgb(0xFF, 0xA8, 0x96, 0x83);
    private static readonly Color ColorCoalPaper        = Color.FromArgb(0xFF, 0xF5, 0xEF, 0xE3);
    private static readonly Color ColorErrPressed       = Color.FromArgb(0xFF, 0xD1, 0x66, 0x4F);
    private static readonly Color ColorCoalDivider      = Color.FromArgb(0xFF, 0x3A, 0x31, 0x27);

    private readonly SolidColorBrush _backgroundBrush = new(ColorTransparent);
    private readonly SolidColorBrush _glyphBrush      = new(ColorCoalBody);

    public event EventHandler? Clicked;

    public TrafficLightButton()
    {
        InitializeComponent();
        Root.Background = _backgroundBrush;
        Glyph.Stroke    = _glyphBrush;
        ApplyKind();
    }

    public enum TrafficLightKind { Minimize, MaximizeRestore, Close }

    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(TrafficLightKind),
            typeof(TrafficLightButton),
            new PropertyMetadata(TrafficLightKind.Close, OnKindChanged));

    public TrafficLightKind Kind
    {
        get => (TrafficLightKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrafficLightButton btn) btn.ApplyKind();
    }

    private void ApplyKind()
    {
        // Geometry mirrors chrome.jsx SVG paths (10 × 10 viewbox):
        //   min   → "M2,5 L8,5"          (horizontal line)
        //   max   → "M2,2 L8,2 L8,8 L2,8 Z" (box)
        //   close → "M2,2 L8,8 M8,2 L2,8" (X)
        string data = Kind switch
        {
            TrafficLightKind.Minimize        => "M2,5 L8,5",
            TrafficLightKind.MaximizeRestore => "M2,2 L8,2 L8,8 L2,8 Z",
            TrafficLightKind.Close           => "M2,2 L8,8 M8,2 L2,8",
            _ => string.Empty,
        };
        Glyph.Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data);

        //  i18n labels resolve at ApplyKind time. Localizer has no
        // LanguageChanged event yet — language change is restart-required
        // across the suite, so these labels won't repaint mid-session.
        string label = Kind switch
        {
            TrafficLightKind.Minimize        => Localizer.T("controls.traffic_light.minimize", "Minimize"),
            TrafficLightKind.MaximizeRestore => Localizer.T("controls.traffic_light.maximize_restore", "Maximize or Restore"),
            TrafficLightKind.Close           => Localizer.T("controls.traffic_light.close", "Close"),
            _ => string.Empty,
        };
        AutomationProperties.SetName(this, label);
        ToolTipService.SetToolTip(this, label);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _backgroundBrush.Color = Kind == TrafficLightKind.Close ? ColorErr : ColorCoalHover;
        _glyphBrush.Color      = Kind == TrafficLightKind.Close ? ColorCoalPaper : ColorCoalPaper;
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _backgroundBrush.Color = ColorTransparent;
        _glyphBrush.Color      = ColorCoalBody;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _backgroundBrush.Color = Kind == TrafficLightKind.Close ? ColorErrPressed : ColorCoalDivider;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        // Settle back to hover paint (pointer is still over the button) and
        // raise Clicked. PointerExited will take over if the pointer leaves
        // before the next move.
        _backgroundBrush.Color = Kind == TrafficLightKind.Close ? ColorErr : ColorCoalHover;
        Clicked?.Invoke(this, EventArgs.Empty);
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter || e.Key == VirtualKey.Space)
        {
            e.Handled = true;
            Clicked?.Invoke(this, EventArgs.Empty);
        }
    }
}
