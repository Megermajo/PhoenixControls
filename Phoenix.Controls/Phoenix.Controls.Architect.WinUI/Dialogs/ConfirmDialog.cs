using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Shared.Localization;

namespace Phoenix.Controls.Architect.WinUI.Dialogs;

public enum ConfirmDialogButton
{
    /// <summary>Default focus on the primary (destructive / commit) button.</summary>
    Primary,
    /// <summary>Default focus on the close button — safer for genuinely destructive prompts.</summary>
    Close,
}

// [DIALOG-NO-XAML-FIX 2026-06-29] No .xaml / InitializeComponent — a
// code-constructed library ContentDialog throws XamlParseException at
// Application.LoadComponent when `new`'d detached (proven by the 1.0.6 runtime
// stack trace; resource stripping never helped because the throw is in the XAML
// parse itself). Content is built in code; the default template still resolves
// at ShowAsync. See NameTypeDialog / DialogTheme.cs for the full rationale.
public sealed class ConfirmDialog : ContentDialog
{
    private readonly Grid DangerHeader;
    private readonly Border DangerBar;
    private readonly FontIcon DangerGlyph;
    private readonly TextBlock MessageText;

    public ConfirmDialog()
    {
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        MaxWidth = 480;
        PrimaryButtonText = Localizer.T("architect.dialog.confirm.yes", "Yes");
        CloseButtonText = Localizer.T("common.button.cancel", "Cancel");
        DefaultButton = ContentDialogButton.Primary;

        // Danger header row — warning glyph + accent bar. Visibility is flipped
        // on at runtime via ForDanger. 0.10.0 a11y / UX P2.
        DangerHeader = new Grid { Visibility = Visibility.Collapsed };
        DangerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        DangerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        DangerBar = new Border
        {
            Width = 3,
            Margin = new Thickness(0, 0, 8, 0),
            CornerRadius = new CornerRadius(1.5),
        };
        Grid.SetColumn(DangerBar, 0);

        DangerGlyph = new FontIcon
        {
            Glyph = "",
            FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(DangerGlyph, 1);

        DangerHeader.Children.Add(DangerBar);
        DangerHeader.Children.Add(DangerGlyph);

        MessageText = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };

        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        panel.Children.Add(DangerHeader);
        panel.Children.Add(MessageText);
        Content = panel;

        // Theme applied in code — see DialogTheme (a code-constructed library
        // dialog can't resolve {StaticResource}/{ThemeResource} at parse).
        if (DialogTheme.Brush("BgPanelBrush")    is { } bg) Background  = bg;
        if (DialogTheme.Brush("BorderSoftBrush") is { } bd) BorderBrush = bd;
        if (DialogTheme.Brush("ErrBrush")        is { } er) { DangerBar.Background = er; DangerGlyph.Foreground = er; }
        if (DialogTheme.Brush("TextLabelBrush")  is { } tl) MessageText.Foreground = tl;
    }

    // The button-label defaults are `null` rather than the literals they used to
    // be: a default parameter value must be a compile-time constant, so it can
    // never carry a Localizer lookup. Null means "no caller preference" and the
    // factory resolves the localized default below — same rendered English,
    // source-compatible for every existing call site.
    public static ConfirmDialog ForMessage(XamlRoot root, string title, string message,
                                           string? yesText = null, string? noText = null)
        => ForMessage(root, title, message, ConfirmDialogButton.Primary, yesText, noText);

    /// <summary>
    /// Factory with explicit default-button selection. Use
    /// <see cref="ConfirmDialogButton.Close"/> for irreversible actions
    /// (delete, overwrite) so the safer choice is what Enter triggers.
    /// </summary>
    public static ConfirmDialog ForMessage(XamlRoot root, string title, string message,
                                           ConfirmDialogButton defaultButton,
                                           string? yesText = null, string? noText = null)
    {
        var d = new ConfirmDialog
        {
            XamlRoot = root,
            Title = title,
            PrimaryButtonText = yesText ?? Localizer.T("architect.dialog.confirm.yes", "Yes"),
            CloseButtonText = noText ?? Localizer.T("common.button.cancel", "Cancel"),
            DefaultButton = defaultButton == ConfirmDialogButton.Close
                ? Microsoft.UI.Xaml.Controls.ContentDialogButton.Close
                : Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
        };
        d.MessageText.Text = message;
        return d;
    }

    /// <summary>
    /// 0.10.0 UX P2 — danger variant. Flips the warning header on, defaults
    /// the close button (safer choice on Enter), and reads
    /// <paramref name="destructiveVerb"/> as the primary button label so the
    /// dialog never defaults to a generic "Yes" for destructive intent.
    /// </summary>
    public static ConfirmDialog ForDanger(XamlRoot root, string title, string message,
                                          string destructiveVerb, string? cancelText = null)
    {
        var d = new ConfirmDialog
        {
            XamlRoot = root,
            Title = title,
            PrimaryButtonText = destructiveVerb,
            CloseButtonText = cancelText ?? Localizer.T("common.button.cancel", "Cancel"),
            DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Close,
        };
        d.MessageText.Text = message;
        d.DangerHeader.Visibility = Visibility.Visible;
        return d;
    }
}
