using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.WinUI.Contracts;
using System;
using Windows.System;
using Windows.UI;

namespace Phoenix.Controls.Hub.WinUI.Controls;

public sealed partial class PillarTab : UserControl
{
    // Canonical colors mirrored from PhoenixDark.xaml — ColorAnimation
    // needs raw Color values, and SolidColorBrush resources in the theme
    // dictionary are frozen instances that can't be Color-animated in
    // place (animating their Color would affect every consumer). The
    // tab owns its own per-instance SolidColorBrushes (declared in
    // PillarTab.xaml) and animates THEIR Color over 120 ms on state
    // changes — chrome.jsx "transition: background 120ms, color 120ms"
    // idiom. Token values stay in sync with the theme dictionary; this
    // file is the per-pillar paint code that the visualist-architect
    // chrome-independence rule says lives here, not in Shared.
    private static readonly Color ColorTransparent       = Color.FromArgb(0x00, 0x00, 0x00, 0x00);
    private static readonly Color ColorCoalCard          = Color.FromArgb(0xFF, 0x22, 0x1C, 0x16);
    private static readonly Color ColorCoalHover         = Color.FromArgb(0xFF, 0x2C, 0x25, 0x1D);
    private static readonly Color ColorEmberDeep         = Color.FromArgb(0xFF, 0xC8, 0x82, 0x2B);
    private static readonly Color ColorCoalPaper        = Color.FromArgb(0xFF, 0xF5, 0xEF, 0xE3);
    private static readonly Color ColorCoalBody          = Color.FromArgb(0xFF, 0xA8, 0x96, 0x83);
    private static readonly Color ColorCoalSecondary     = Color.FromArgb(0xFF, 0x9C, 0x8A, 0x72);

    private static readonly Duration TransitionDuration  = new(TimeSpan.FromMilliseconds(120));
    private static readonly CubicEase TransitionEase     = new() { EasingMode = EasingMode.EaseOut };

    private static readonly LinearGradientBrush s_blueSigilBrush   = BuildBlueSigil();
    private static readonly LinearGradientBrush s_purpleSigilBrush = BuildPurpleSigil();

    public event EventHandler? Clicked;

    public PillarTab()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty PillarProperty =
        DependencyProperty.Register(nameof(Pillar), typeof(PillarKind), typeof(PillarTab),
            new PropertyMetadata(PillarKind.Hub, OnPillarChanged));

    public PillarKind Pillar
    {
        get => (PillarKind)GetValue(PillarProperty);
        set => SetValue(PillarProperty, value);
    }

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(PillarTab),
            new PropertyMetadata(false, OnActiveChanged));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private static void OnPillarChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PillarTab tab) tab.ApplyPillar();
    }

    private static void OnActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PillarTab tab) tab.ApplyActiveState(animate: true);
    }

    private void ApplyPillar()
    {
        // Sigil glyphs (H / A / V) are typographic affordances inside a 18×18
        // brand square — they're not translation targets. The localized
        // surfaces are the visible LabelText and the screen-reader name.
        switch (Pillar)
        {
            case PillarKind.Hub:
                SigilGlyph.Text = "H";
                LabelText.Text = Localizer.T("pillartab.hub.label", "HUB");
                SigilBox.Background = s_blueSigilBrush;
                break;
            case PillarKind.Architect:
                SigilGlyph.Text = "A";
                LabelText.Text = Localizer.T("pillartab.architect.label", "ARCHITECT");
                SigilBox.Background = s_blueSigilBrush;
                break;
            case PillarKind.Visualist:
                SigilGlyph.Text = "V";
                LabelText.Text = Localizer.T("pillartab.visualist.label", "VISUALIST");
                SigilBox.Background = s_purpleSigilBrush;
                break;
        }
        // Screen-reader name follows the visible label. Without this the
        // tab announces as "PillarTab" (the class name) or the generic
        // "pane". Tab key now also reaches us because the UserControl is
        // IsTabStop=True; Enter/Space fire Clicked via OnRootKeyDown.
        string ariaFmt = Localizer.T("pillartab.aria.name_format", "{0} pillar tab");
        AutomationProperties.SetName(this, string.Format(ariaFmt, LabelText.Text));
        // Tooltip — surfaces the localized pillar label on hover for mouse/keyboard users.
        ToolTipService.SetToolTip(this, LabelText.Text);
        // Snap to current state on first paint (animate=false) so the tab
        // doesn't visibly fade in from Transparent on construction.
        ApplyActiveState(animate: false);
    }

    private static LinearGradientBrush BuildBlueSigil() => new()
    {
        StartPoint = new global::Windows.Foundation.Point(0, 0),
        EndPoint   = new global::Windows.Foundation.Point(0, 1),
        GradientStops =
        {
            new GradientStop { Offset = 0, Color = Color.FromArgb(0xFF, 0x1F, 0x33, 0x57) },
            new GradientStop { Offset = 1, Color = Color.FromArgb(0xFF, 0x14, 0x22, 0x43) },
        },
    };

    private static LinearGradientBrush BuildPurpleSigil() => new()
    {
        StartPoint = new global::Windows.Foundation.Point(0, 0),
        EndPoint   = new global::Windows.Foundation.Point(0, 1),
        GradientStops =
        {
            new GradientStop { Offset = 0, Color = Color.FromArgb(0xFF, 0x3A, 0x1F, 0x4E) },
            new GradientStop { Offset = 1, Color = Color.FromArgb(0xFF, 0x23, 0x13, 0x3A) },
        },
    };

    /// <summary>
    /// Drives the tab to its target state. When the active flag is the
    /// dominant input, fall back to that for the colors; otherwise route
    /// through the per-state brush colors. <paramref name="animate"/>
    /// controls whether the change happens instantly (initial paint) or
    /// over the 120 ms easing curve (interactive hover / click).
    /// </summary>
    private void ApplyActiveState(bool animate)
    {
        if (IsActive)
        {
            TransitionTo(ColorCoalCard, ColorEmberDeep, ColorCoalPaper, new Thickness(0, 1, 0, 0), animate);
        }
        else
        {
            TransitionTo(ColorTransparent, ColorTransparent, ColorCoalSecondary, new Thickness(0), animate);
        }
    }

    private void TransitionTo(Color background, Color border, Color foreground, Thickness borderThickness, bool animate)
    {
        // BorderThickness can't be animated as a Color, so swap it instantly.
        // The 120 ms transition is mostly about the background + foreground
        // colors; the 1 px top edge on the active tab is a binary affordance
        // that doesn't need an ease curve.
        Root.BorderThickness = borderThickness;

        if (!animate)
        {
            RootBackgroundBrush.Color = background;
            RootBorderBrush.Color     = border;
            LabelForegroundBrush.Color = foreground;
            return;
        }
        AnimateBrush(RootBackgroundBrush, background);
        AnimateBrush(RootBorderBrush, border);
        AnimateBrush(LabelForegroundBrush, foreground);
    }

    private static void AnimateBrush(SolidColorBrush brush, Color to)
    {
        var sb = new Storyboard();
        var anim = new ColorAnimation
        {
            To = to,
            Duration = TransitionDuration,
            EasingFunction = TransitionEase,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(anim, brush);
        Storyboard.SetTargetProperty(anim, "Color");
        sb.Children.Add(anim);
        sb.Begin();
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (IsActive) return;
        // Hub UI sweep motion budget + redesign — subtle hover fill +
        // brighter label, both eased over 120 ms via per-instance
        // SolidColorBrushes (RootBackgroundBrush / LabelForegroundBrush).
        AnimateBrush(RootBackgroundBrush, ColorCoalHover);
        AnimateBrush(LabelForegroundBrush, ColorCoalBody);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (IsActive) return;
        AnimateBrush(RootBackgroundBrush, ColorTransparent);
        AnimateBrush(LabelForegroundBrush, ColorCoalSecondary);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        => Clicked?.Invoke(this, EventArgs.Empty);

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter || e.Key == VirtualKey.Space)
        {
            e.Handled = true;
            Clicked?.Invoke(this, EventArgs.Empty);
        }
    }
}
