using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Visualist.WinUI.Dialogs;

/// <summary>
/// Preset picker shown when the
/// user invokes File → New Layer. Pre-fix the new document came in at the
/// engine's hard-coded FullHD default; now the dialog surfaces the
/// preset → resolution mapping at the explicit creation event so the user
/// can pick FullHD / QHD / UHD / Vertical / Square or supply a custom W/H
/// upfront. Cancel returns a null <see cref="Choice"/> and the caller is
/// expected to abort the New action.
///
/// This is a modal at a single explicit creation event (not a repeatable
/// rejection), so a <see cref="ContentDialog"/> is the appropriate
/// affordance. The visual
/// treatment authors its own copy of the Architect KeyboardShortcutsDialog
/// chrome rather than lifting the resource dictionary.
///
/// This dialog has NO .xaml / InitializeComponent.
///   A code-constructed ContentDialog defined in a LIBRARY assembly
///   (Visualist.WinUI) throws XamlParseException at Application.LoadComponent when
///   `new`'d while detached — proven by the runtime stack trace, which still
///   crashed AFTER the resource markup was stripped. The resource theory was wrong;
///   the throw is in the XAML parse itself, before any resource or DialogTheme code
///   runs. Building the content in code removes LoadComponent entirely, so the parse
///   can't fail by construction (the default ContentDialog template still resolves
///   at ShowAsync against Hub's app scope, which merges XamlControlsResources — the
///   show path that already works for Hub's dialogs). See DialogTheme.cs.
/// </summary>
public sealed class NewLayerDialog : ContentDialog
{
    /// <summary>
    /// User's selection. <see cref="Name"/> defaults to "untitled" when the
    /// name box is left blank. Width/Height reflect the chosen preset's
    /// canonical resolution, or the Custom NumberBox values when
    /// <see cref="Preset"/> is <see cref="LayerPreset.Custom"/>.
    /// </summary>
    public sealed record Choice(string Name, LayerPreset Preset, int Width, int Height);

    private static readonly LayerPreset[] s_presets =
        (LayerPreset[])Enum.GetValues(typeof(LayerPreset));

    /// <summary>The user's confirmed selection, or null when the dialog was cancelled.</summary>
    public Choice? Result { get; private set; }

    // Named elements that were x:Name'd in the old XAML — now plain fields built
    // in the ctor.
    private readonly TextBlock EyebrowLabel;
    private readonly TextBlock SubtitleLabel;
    private readonly Rectangle HairlineRule;
    private readonly TextBlock NameLabel;
    private readonly TextBox NameBox;
    private readonly TextBlock PresetLabel;
    private readonly ComboBox PresetBox;
    private readonly Grid CustomSizeRow;
    private readonly TextBlock WidthLabel;
    private readonly TextBlock HeightLabel;
    private readonly NumberBox CustomWidthBox;
    private readonly NumberBox CustomHeightBox;
    private readonly TextBlock PresetHint;

    public NewLayerDialog()
    {
        // Root properties that were attributes on <ContentDialog …>.
        Title = Localizer.T("visualist.rail.new_layer", "New Layer");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        PrimaryButtonText = Localizer.T("common.button.create", "Create");
        CloseButtonText = Localizer.T("common.button.cancel", "Cancel");
        DefaultButton = ContentDialogButton.Primary;

        // ── Visual tree (1:1 with the old XAML) ───────────────────────────
        // Keyed styles that lived in <ContentDialog.Resources> are applied as
        // direct setters on each element here (NewLayerEyebrow / NewLayerFieldLabel
        // / NewLayerHint) — literal setters only; font/brush
        // theming is re-applied in code below via DialogTheme.

        var root = new Grid { Width = 420 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Eyebrow header — matches the Architect KeyboardShortcutsDialog
        // treatment so the dialog reads as part of the same shell.
        var headerPanel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(headerPanel, 0);

        EyebrowLabel = new TextBlock
        {
            Text = "VISUALIST",
            // NewLayerEyebrow style setters.
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 180,
            Margin = new Thickness(0, 0, 0, 4),
        };
        SubtitleLabel = new TextBlock
        {
            FontSize = 13,
            Text = Localizer.T("visualist.dialog.new_layer.subtitle",
                "Pick the resolution preset for the new layer."),
        };
        headerPanel.Children.Add(EyebrowLabel);
        headerPanel.Children.Add(SubtitleLabel);

        HairlineRule = new Rectangle();
        Grid.SetRow(HairlineRule, 1);

        var fieldsPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        Grid.SetRow(fieldsPanel, 2);

        NameLabel = new TextBlock
        {
            Text = Localizer.T("visualist.dialog.new_layer.name.label", "NAME"),
            // NewLayerFieldLabel style setters.
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 80,
            Margin = new Thickness(0, 8, 0, 4),
        };
        NameBox = new TextBox { PlaceholderText = "untitled" };

        PresetLabel = new TextBlock
        {
            Text = Localizer.T("visualist.dialog.new_layer.preset.label", "PRESET"),
            // NewLayerFieldLabel style setters.
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 80,
            Margin = new Thickness(0, 8, 0, 4),
        };
        PresetBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        PresetBox.SelectionChanged += OnPresetChanged;

        CustomSizeRow = new Grid { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        CustomSizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        CustomSizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        CustomSizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        CustomSizeRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        CustomSizeRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        WidthLabel = new TextBlock
        {
            Text = Localizer.T("visualist.dialog.new_layer.width.label", "WIDTH"),
            // NewLayerFieldLabel style setters.
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 80,
            Margin = new Thickness(0, 8, 0, 4),
        };
        Grid.SetRow(WidthLabel, 0);
        Grid.SetColumn(WidthLabel, 0);

        HeightLabel = new TextBlock
        {
            Text = Localizer.T("visualist.dialog.new_layer.height.label", "HEIGHT"),
            // NewLayerFieldLabel style setters.
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 80,
            Margin = new Thickness(0, 8, 0, 4),
        };
        Grid.SetRow(HeightLabel, 0);
        Grid.SetColumn(HeightLabel, 2);

        CustomWidthBox = new NumberBox { Value = 1920, Minimum = 1, Maximum = 16384 };
        Grid.SetRow(CustomWidthBox, 1);
        Grid.SetColumn(CustomWidthBox, 0);

        CustomHeightBox = new NumberBox { Value = 1080, Minimum = 1, Maximum = 16384 };
        Grid.SetRow(CustomHeightBox, 1);
        Grid.SetColumn(CustomHeightBox, 2);

        CustomSizeRow.Children.Add(WidthLabel);
        CustomSizeRow.Children.Add(HeightLabel);
        CustomSizeRow.Children.Add(CustomWidthBox);
        CustomSizeRow.Children.Add(CustomHeightBox);

        PresetHint = new TextBlock
        {
            Text = "1920 × 1080",
            // NewLayerHint style setters.
            FontSize = 10,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        fieldsPanel.Children.Add(NameLabel);
        fieldsPanel.Children.Add(NameBox);
        fieldsPanel.Children.Add(PresetLabel);
        fieldsPanel.Children.Add(PresetBox);
        fieldsPanel.Children.Add(CustomSizeRow);
        fieldsPanel.Children.Add(PresetHint);

        root.Children.Add(headerPanel);
        root.Children.Add(HairlineRule);
        root.Children.Add(fieldsPanel);
        Content = root;

        // Code-constructed library dialog — theme applied in code via DialogTheme;
        // no directly-resolved resource markup (DataTemplate refs are deferred and
        // safe). See Architect NameTypeDialog / DialogTheme.cs. Everything below
        // re-applies the brushes/fonts that were stripped from the XAML root, the
        // keyed styles, and the direct elements.

        // Root chrome.
        if (DialogTheme.Brush("CoalShellBrush") is { } shellBg) Background = shellBg;
        if (DialogTheme.Brush("CoalCardBrush")  is { } cardBd) BorderBrush = cardBd;

        // Brass hairline rule.
        if (DialogTheme.Brush("BrassGradientBrush") is { } brass) HairlineRule.Fill = brass;

        // Eyebrow (was NewLayerEyebrow style: DisplayFont + EmberPrimaryBrush).
        if (DialogTheme.Font("DisplayFont") is { } displayFont) EyebrowLabel.FontFamily = displayFont;
        if (DialogTheme.Brush("EmberPrimaryBrush") is { } ember) EyebrowLabel.Foreground = ember;

        // Subtitle (direct element: SansFont + CoalSecondaryTextBrush).
        if (DialogTheme.Font("SansFont") is { } sansFont) SubtitleLabel.FontFamily = sansFont;
        if (DialogTheme.Brush("CoalSecondaryTextBrush") is { } secondary)
            SubtitleLabel.Foreground = secondary;

        // Field labels (was NewLayerFieldLabel style: SansFont + CoalSecondaryTextBrush).
        if (DialogTheme.Font("SansFont") is { } labelFont)
        {
            NameLabel.FontFamily = labelFont;
            PresetLabel.FontFamily = labelFont;
            WidthLabel.FontFamily = labelFont;
            HeightLabel.FontFamily = labelFont;
        }
        if (DialogTheme.Brush("CoalSecondaryTextBrush") is { } labelBrush)
        {
            NameLabel.Foreground = labelBrush;
            PresetLabel.Foreground = labelBrush;
            WidthLabel.Foreground = labelBrush;
            HeightLabel.Foreground = labelBrush;
        }

        // Name box (direct element: MonoFont).
        if (DialogTheme.Font("MonoFont") is { } monoFont) NameBox.FontFamily = monoFont;

        // Preset hint (was NewLayerHint style: MonoFont + CoalMutedTextBrush).
        if (DialogTheme.Font("MonoFont") is { } hintFont) PresetHint.FontFamily = hintFont;
        if (DialogTheme.Brush("CoalMutedTextBrush") is { } muted) PresetHint.Foreground = muted;

        // Populate the preset combo from the enum so a future preset addition
        // shows up here without touching this file. Default to FullHD — the
        // most common OBS browser-source size and the engine's prior hidden
        // default — so a user who just hits Enter ends up where they used to.
        foreach (LayerPreset p in s_presets)
            PresetBox.Items.Add(p.ToString());
        PresetBox.SelectedIndex = Array.IndexOf(s_presets, LayerPreset.FullHD);

        PrimaryButtonClick += OnPrimaryButtonClick;

        // Default focus on the name field so the user can type the layer name
        // immediately without clicking. Opened (not Loaded) is used so the
        // XamlRoot/visual tree is fully realized before we move focus —
        // mirrors NameTypeDialog. SelectAll() lets a quick rename overwrite
        // any seeded text. Wrapped in try/catch because Focus() can throw if
        // the dialog is torn down before Opened completes.
        Opened += (_, _) =>
        {
            try
            {
                NameBox.Focus(FocusState.Programmatic);
                NameBox.SelectAll();
            }
            catch { /* best-effort focus — never crash the New Layer flow */ }
        };

        // Enter inside the (single-line) name field commits the dialog as if
        // the Create button was pressed. ContentDialog's DefaultButton="Primary"
        // does NOT translate Enter-in-a-TextBox into a Primary click on its
        // own, so we wire it explicitly. Esc still routes to the Close button
        // via the built-in ContentDialog behavior.
        NameBox.KeyDown += OnNameBoxKeyDown;
    }

    private void OnNameBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;

        // Run the same commit path the Create button uses, then close with a
        // Primary result so ShowAsync returns the Choice rather than null.
        OnPrimaryButtonClick(this, null!);
        if (Result is not null)
            Hide();
    }

    /// <summary>
    /// Async show wrapper — sets <see cref="FrameworkElement.XamlRoot"/> and
    /// surfaces the user's choice (or null on cancel). Routes failures
    /// through <see cref="GlobalLogger.Error"/> so a teardown-time XamlRoot
    /// fault doesn't crash the New Layer flow.
    /// </summary>
    public static async System.Threading.Tasks.Task<Choice?> ShowAsync(XamlRoot xamlRoot)
    {
        if (xamlRoot is null)
        {
            GlobalLogger.Log(
                "NewLayerDialog: no XamlRoot available — skipping show.",
                "Visualist.Dialogs",
                LogLevel.System);
            return null;
        }
        try
        {
            var dlg = new NewLayerDialog { XamlRoot = xamlRoot };
            await dlg.ShowAsync();
            // Honor a populated Result regardless of the closing button code.
            // The Create button (Primary) and the Enter-to-commit path both set
            // Result before the dialog closes; the Enter path closes via Hide()
            // which reports ContentDialogResult.None, so we cannot key off the
            // button result alone. Cancel / Esc never run the commit path, so
            // Result stays null there.
            return dlg.Result;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist.Dialogs", "NewLayerDialog.ShowAsync", ex);
            return null;
        }
    }

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        int idx = PresetBox.SelectedIndex;
        if (idx < 0 || idx >= s_presets.Length) return;
        LayerPreset p = s_presets[idx];

        // Custom unlocks the W/H NumberBoxes; named presets show the
        // canonical resolution as a hint so the user can confirm at a glance.
        if (p == LayerPreset.Custom)
        {
            CustomSizeRow.Visibility = Visibility.Visible;
            PresetHint.Text = Localizer.T("visualist.dialog.new_layer.custom.hint",
                "custom — pick width and height");
        }
        else
        {
            CustomSizeRow.Visibility = Visibility.Collapsed;
            var (w, h) = p.ToResolution();
            PresetHint.Text = $"{w} × {h}";
        }
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        int idx = PresetBox.SelectedIndex;
        if (idx < 0 || idx >= s_presets.Length)
        {
            // No selection — defer to FullHD so a stray empty selection
            // doesn't drop the user into a Custom W/H = 0 invalid state.
            idx = Array.IndexOf(s_presets, LayerPreset.FullHD);
        }
        LayerPreset preset = s_presets[idx];

        int w, h;
        if (preset == LayerPreset.Custom)
        {
            // NumberBox.Value is double; clamp to the int domain before the
            // cast so an out-of-range edit doesn't silently overflow.
            double rawW = double.IsNaN(CustomWidthBox.Value)  ? 1920 : CustomWidthBox.Value;
            double rawH = double.IsNaN(CustomHeightBox.Value) ? 1080 : CustomHeightBox.Value;
            w = Math.Max(1, (int)Math.Round(rawW));
            h = Math.Max(1, (int)Math.Round(rawH));
        }
        else
        {
            var (pw, ph) = preset.ToResolution();
            w = pw;
            h = ph;
        }

        string name = (NameBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name)) name = "untitled";

        Result = new Choice(name, preset, w, h);
    }
}
