using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace Phoenix.Controls.Hub.WinUI.Panels.Common;

/// <summary>
/// Status pill for the rebuilt tool pages' action strip: a coloured dot + a
/// short mono label on an accent-tinted chip, with an optional trailing hint.
///
/// GROUND COLOUR — a deliberate, documented deviation from the reference.
/// Timer and Giveaway declare the chip's fill / border as a FIXED page-local
/// pair derived from the tool's healthy colour (TimerView.xaml:52-53 is a green
/// pair at alpha 0x1A / 0x80) and never recompute it, so the chip stays
/// green-tinted even when the label says something is wrong. Here the pair is
/// computed from <see cref="Accent"/> on every change, using the same two alpha
/// values, so the ground tracks the state. Raised for review in the Phase 0
/// report; if exact reference parity is wanted instead, the fix is a second
/// colour DP pinned to the healthy hue that feeds Chip.Background/BorderBrush
/// while Accent keeps feeding the dot and label. That DP is deliberately NOT
/// declared today because nothing would set it.
///
/// PULSE LIFECYCLE — this control owns it end to end. the Pre-Builds rail sets
/// ContentHost.Content = null when a tab closes and Loaded re-fires on every
/// re-attach, so a storyboard started by a panel and never stopped keeps
/// running on a tab nobody is looking at. Unloaded therefore ALWAYS stops the
/// pulse, and Loaded restarts it if the state still calls for one. No caller
/// has to remember anything.
/// </summary>
public sealed partial class ToolStatusPill : UserControl
{
    // Default accent = CoalSecondaryText #9C8A72, the palette's "dormant" tier.
    // A Color DP left at default(Color) would be fully transparent black, i.e.
    // an invisible pill, so the default is a real readable colour instead.
    private static readonly Color DefaultAccent = Color.FromArgb(0xFF, 0x9C, 0x8A, 0x72);

    // Alpha pair from TimerView.xaml:52-53 (#1A fill over #80 border).
    private const byte GroundFillAlpha   = 0x1A;
    private const byte GroundBorderAlpha = 0x80;

    private Storyboard? _pulseStoryboard;

    public ToolStatusPill()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        ApplyAccent();
        ApplyText();
        ApplyHint();
        ApplyDotVisibility();
    }

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(ToolStatusPill),
            new PropertyMetadata("", OnTextChanged));

    public static readonly DependencyProperty AccentProperty =
        DependencyProperty.Register(nameof(Accent), typeof(Color), typeof(ToolStatusPill),
            new PropertyMetadata(DefaultAccent, OnAccentChanged));

    public static readonly DependencyProperty IsPulsingProperty =
        DependencyProperty.Register(nameof(IsPulsing), typeof(bool), typeof(ToolStatusPill),
            new PropertyMetadata(false, OnIsPulsingChanged));

    public static readonly DependencyProperty HintProperty =
        DependencyProperty.Register(nameof(Hint), typeof(string), typeof(ToolStatusPill),
            new PropertyMetadata("", OnHintChanged));

    /// <summary>The pill label, e.g. "live · earning". Lowercase mono by design.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    /// The state colour. The dot and label take it opaque; the chip ground takes
    /// it at alpha 0x1A (fill) and 0x80 (border).
    /// </summary>
    public Color Accent
    {
        get => (Color)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    /// <summary>
    /// True shows the dot and runs the 0.5 Hz pulse; false hides the dot
    /// entirely. Reserve it for states with a genuine liveness beat.
    /// </summary>
    public bool IsPulsing
    {
        get => (bool)GetValue(IsPulsingProperty);
        set => SetValue(IsPulsingProperty, value);
    }

    /// <summary>Optional plain-text note rendered outside the chip. Empty collapses it.</summary>
    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ToolStatusPill p) p.ApplyText();
    }

    private static void OnHintChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ToolStatusPill p) p.ApplyHint();
    }

    private static void OnAccentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ToolStatusPill p) p.ApplyAccent();
    }

    private static void OnIsPulsingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ToolStatusPill p) return;
        p.ApplyDotVisibility();
        p.ApplyPulse();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Re-paint on realization for the same reason TogglePill does: a tab can
        // be built with every DP already at its bound value, so no changed
        // callback ever fires.
        ApplyAccent();
        ApplyText();
        ApplyHint();
        ApplyDotVisibility();
        ApplyPulse();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => StopPulse();

    private void ApplyText() => PillText.Text = Text ?? "";

    private void ApplyHint()
    {
        string hint = Hint ?? "";
        HintText.Text = hint;
        HintText.Visibility = hint.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyDotVisibility()
        => PulseDot.Visibility = IsPulsing ? Visibility.Visible : Visibility.Collapsed;

    private void ApplyAccent()
    {
        Color c = Accent;

        Chip.Background  = new SolidColorBrush(Color.FromArgb(GroundFillAlpha,   c.R, c.G, c.B));
        Chip.BorderBrush = new SolidColorBrush(Color.FromArgb(GroundBorderAlpha, c.R, c.G, c.B));

        // Opaque regardless of the alpha the caller supplied — the dot and label
        // must stay legible even if Accent arrives partially transparent.
        var opaque = new SolidColorBrush(Color.FromArgb(0xFF, c.R, c.G, c.B));
        PulseDot.Fill = opaque;
        PillText.Foreground = opaque;
    }

    // ── Pulse (the hand-built storyboard from TimerView.xaml.cs:482-529) ──────

    private void ApplyPulse()
    {
        // Never start while detached: a storyboard on an unrealized subtree is
        // the leak this control exists to prevent.
        if (IsPulsing && IsLoaded) StartPulse();
        else StopPulse();
    }

    private void StartPulse()
    {
        if (_pulseStoryboard is not null) return;   // idempotent
        if (PulseDot is null) return;

        PulseDot.RenderTransformOrigin = new global::Windows.Foundation.Point(0.5, 0.5);
        var scale = new ScaleTransform();
        PulseDot.RenderTransform = scale;

        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

        var opacity = new DoubleAnimationUsingKeyFrames();
        opacity.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 1.0 });
        opacity.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1)), Value = 0.5 });
        opacity.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2)), Value = 1.0 });
        Storyboard.SetTarget(opacity, PulseDot);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        sb.Children.Add(opacity);

        AddScaleTrack(sb, scale, "ScaleX");
        AddScaleTrack(sb, scale, "ScaleY");

        _pulseStoryboard = sb;
        try { sb.Begin(); } catch { _pulseStoryboard = null; }
    }

    // EnableDependentAnimation is required here: a ScaleTransform on a 6px
    // Ellipse is not a composition-thread property. This is the same trade the
    // Timer reference makes (TimerView.xaml.cs:510) and is scoped to one dot on
    // one visible tab. It is NOT the AnimateExtensions.PulseScale case, which
    // must stay independent because it fires on ordinary 1 Hz traffic.
    private static void AddScaleTrack(Storyboard sb, ScaleTransform scale, string property)
    {
        var anim = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
        anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 1.0 });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1)), Value = 0.85 });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2)), Value = 1.0 });
        Storyboard.SetTarget(anim, scale);
        Storyboard.SetTargetProperty(anim, property);
        sb.Children.Add(anim);
    }

    private void StopPulse()
    {
        if (_pulseStoryboard is null) return;
        try { _pulseStoryboard.Stop(); } catch { /* best-effort */ }
        _pulseStoryboard = null;
        if (PulseDot is not null)
        {
            PulseDot.Opacity = 1.0;
            PulseDot.RenderTransform = null;
        }
    }
}
