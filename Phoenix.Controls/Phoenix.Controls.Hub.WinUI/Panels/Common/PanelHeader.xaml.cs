using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Shared.Localization;

namespace Phoenix.Controls.Hub.WinUI.Panels.Common;

// When Localizer gains a LanguageChanged event (live-switch milestone per
// Localizer.cs), subscribe on Loaded / unsubscribe on Unloaded so Title
// refreshes on the fly.
public sealed partial class PanelHeader : UserControl
{
    public PanelHeader()
    {
        InitializeComponent();
        Loaded += OnLoaded;
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

    /// <summary>
    /// Localizer key for <see cref="Eyebrow"/>, resolved at Loaded exactly like
    /// <see cref="TitleKey"/> and with <see cref="Eyebrow"/> as the English
    /// fallback.
    ///
    /// <para>Added by the localization retrofit. Until then the header had a
    /// <see cref="TitleKey"/> and no counterpart for the strap above it, so
    /// every "LIVE" / "ENTRANTS" / "PRE-BUILD TOOL" eyebrow sat above a
    /// translated title as permanent English — not an oversight at the call
    /// sites, simply not expressible.</para>
    /// </summary>
    public string EyebrowKey
    {
        get => (string)GetValue(EyebrowKeyProperty);
        set => SetValue(EyebrowKeyProperty, value);
    }

    public static readonly DependencyProperty EyebrowKeyProperty =
        DependencyProperty.Register(nameof(EyebrowKey), typeof(string), typeof(PanelHeader),
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

        if (!string.IsNullOrEmpty(EyebrowKey))
        {
            Eyebrow = Localizer.T(EyebrowKey, string.IsNullOrEmpty(Eyebrow) ? EyebrowKey : Eyebrow);
        }
    }
}
