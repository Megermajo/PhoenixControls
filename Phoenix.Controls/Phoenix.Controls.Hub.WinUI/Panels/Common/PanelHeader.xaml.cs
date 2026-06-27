using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Shared.Localization;

namespace Phoenix.Controls.Hub.WinUI.Panels.Common;

public sealed partial class PanelHeader : UserControl
{
    public PanelHeader()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Static title string — set by call-sites that already hold a resolved
    /// label (e.g. session-restore code that materialises a record without
    /// access to the Localizer pillar). Most XAML consumers prefer
    /// <see cref="TitleKey"/> instead so the visible text follows the
    /// active language.
    /// </summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PanelHeader),
            new PropertyMetadata(string.Empty));

    /// <summary>
    /// Localizer key. When set, Loaded resolves the key through
    /// <c>Localizer.T(key, Title)</c> — Title is used as the English
    /// fallback so a TitleKey miss still renders the original literal
    /// instead of `[panel.livefeed.title]`. (Hub UI sweep P1 — PanelHeader.Title
    /// localization hook.)
    /// </summary>
    public string TitleKey
    {
        get => (string)GetValue(TitleKeyProperty);
        set => SetValue(TitleKeyProperty, value);
    }

    public static readonly DependencyProperty TitleKeyProperty =
        DependencyProperty.Register(nameof(TitleKey), typeof(string), typeof(PanelHeader),
            new PropertyMetadata(string.Empty));

    public string Eyebrow
    {
        get => (string)GetValue(EyebrowProperty);
        set => SetValue(EyebrowProperty, value);
    }

    public static readonly DependencyProperty EyebrowProperty =
        DependencyProperty.Register(nameof(Eyebrow), typeof(string), typeof(PanelHeader),
            new PropertyMetadata(string.Empty));

    public object? RightContent
    {
        get => GetValue(RightContentProperty);
        set => SetValue(RightContentProperty, value);
    }

    public static readonly DependencyProperty RightContentProperty =
        DependencyProperty.Register(nameof(RightContent), typeof(object), typeof(PanelHeader),
            new PropertyMetadata(null));

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Resolve TitleKey at Loaded so Localizer.Init has guaranteed to have
        // run — the splash pre-boot pipeline does it before MainWindow ever
        // shows, but constructing PanelHeader earlier than that (test rigs,
        // future deferred init) would silently render [key] placeholders if
        // we resolved in the ctor.
        if (!string.IsNullOrEmpty(TitleKey))
        {
            Title = Localizer.T(TitleKey, string.IsNullOrEmpty(Title) ? TitleKey : Title);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // No active subscriptions today — Localizer doesn't yet expose a
        // LanguageChanged event (planned for the live-switch milestone per
        // Localizer.cs). When that lands, hook here and Title gets refreshed
        // on the fly.
    }
}
