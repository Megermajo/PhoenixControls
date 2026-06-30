using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Phoenix.Controls.Architect.WinUI.Controls;

public sealed partial class RailButton : UserControl
{
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(RailButton),
            new PropertyMetadata(string.Empty, (d, e) =>
            {
                if (d is RailButton b) b.HostButton.Content = e.NewValue as string ?? string.Empty;
            }));

    public static readonly DependencyProperty AccentBrushProperty =
        DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(RailButton),
            new PropertyMetadata(null, (d, e) =>
            {
                if (d is RailButton b && e.NewValue is Brush brush)
                    b.HostButton.Foreground = brush;
            }));

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public Brush? AccentBrush
    {
        get => (Brush?)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public event EventHandler? Clicked;

    public RailButton()
    {
        InitializeComponent();
        // 0.10.0 a11y P2: forward IsEnabled to the inner Button. The Button
        // itself owns IsTabStop / focus visuals / Enter+Space invoke +
        // ButtonAutomationPeer's IInvokeProvider — keyboard-only users
        // can reach and fire rail actions without code-side plumbing.
        IsEnabledChanged += (_, _) => HostButton.IsEnabled = IsEnabled;
        // Re-apply Glyph / AccentBrush after the template lands so the
        // initial DP values from XAML construction paint.
        HostButton.Loaded += (_, _) =>
        {
            HostButton.Content = Glyph;
            if (AccentBrush is not null) HostButton.Foreground = AccentBrush;
            // Forward any ToolTipService tooltip attached to THIS UserControl onto
            // the inner Button. The Button (custom ControlTemplate) is the real
            // hover target and fills the wrapper, so a tip set only on the wrapper
            // never surfaced on hover — the rail buttons' tooltips were dead. The
            // caller (LeftRail.AddButton) sets the tip before the control loads, so
            // it's present by the time this fires; harmless when none is set.
            var tip = ToolTipService.GetToolTip(this);
            if (tip is not null) ToolTipService.SetToolTip(HostButton, tip);
        };
    }

    private void OnHostButtonClick(object sender, RoutedEventArgs e)
    {
        Clicked?.Invoke(this, EventArgs.Empty);
    }
}
