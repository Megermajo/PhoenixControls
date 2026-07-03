using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Phoenix.Controls.Architect.WinUI.Canvas;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Architect.WinUI.Dialogs;

/// <summary>
/// Architect "Keyboard Shortcuts" reference dialog.
/// <para/>
/// Restores the pre-T15 <c>MainForm.ShowKeyboardShortcuts()</c> surface
/// that listed every chord the WinForms canvas + menu bar honoured. The
/// WinUI rewrite carried the chord set forward (and extended it with
/// Dvorak scancode coverage and F1-node-documentation) but lost
/// the discoverability surface. The new dialog is reached from:
/// <list type="bullet">
///   <item>Help → Keyboard Shortcuts… in <c>ArchitectChrome</c> (embedded view).</item>
///   <item>Help → Keyboard Shortcuts… in <c>ArchitectSiblingWindow</c> (multi-window).</item>
///   <item>F1 on an empty canvas (fallback).</item>
/// </list>
/// </summary>
/// <remarks>
/// The chord catalog is hand-curated in this file (not parsed from XAML
/// attributes / source comments) — declarative listings drift quickly
/// when handlers are extended without touching the chrome XAML, and a
/// runtime XAML parse would couple the dialog to a brittle string-walk.
/// Edits to either column should mirror the canvas / chrome edits in the
/// same sprint so the dialog stays trustworthy as a contract.
/// </remarks>
//
// This dialog has NO .xaml / InitializeComponent.
//   A code-constructed ContentDialog defined in a LIBRARY assembly
//   (Architect.WinUI) throws XamlParseException at Application.LoadComponent when
//   `new`'d while detached — proven by the 1.0.6 runtime stack trace, which still
//   crashed AFTER the resource markup was stripped. The throw is in the XAML
//   parse itself, before any resource or DialogTheme code runs. Building the
//   content in code removes LoadComponent entirely, so the parse can't fail by
//   construction (the default ContentDialog template still resolves at ShowAsync
//   against Hub's app scope, which merges XamlControlsResources). See DialogTheme.cs.
//
//   The four keyed TextBlock styles that lived in <ContentDialog.Resources> are
//   rebuilt here as a CODE-BUILT ResourceDictionary with literal-only setters and
//   stored in this.Resources. A code-built dictionary is not parse-time markup, so
//   it cannot throw at construction — and keeping the styles keyed lets the ctor's
//   `(Style)Resources["…"]` lookups and BuildColumn run completely unchanged.
public sealed class KeyboardShortcutsDialog : ContentDialog
{
    // Named elements that were x:Name'd in the old XAML — now plain fields built
    // in the ctor.
    private readonly TextBlock ArchitectEyebrow;
    private readonly TextBlock SubtitleText;
    private readonly Rectangle BrassHairline;
    private readonly StackPanel CanvasColumn;
    private readonly TextBlock CanvasEyebrow;
    private readonly Rectangle ColumnDivider;
    private readonly StackPanel ChromeColumn;
    private readonly TextBlock ChromeEyebrow;

    /// <summary>
    /// Canvas column sections — chord vocabulary surfaced inside the
    /// <see cref="Canvas.LogicCanvasView"/> Keyboard / QuickKeys / Pointer
    /// / DragDrop partials. Scancode-anchored chords
    /// (Ctrl+Z/Y/C/V/X/D/A/S/F/G) read by their QWERTY position regardless
    /// of active layout — the catalog lists the QWERTY label since that's
    /// the physical key Majo canonicalised as the chord identity.
    /// </summary>
    private static readonly string[] CanvasSections =
    {
        ArchitectHotkeyCatalog.SectionEdit,
        ArchitectHotkeyCatalog.SectionNavigateView,
        ArchitectHotkeyCatalog.SectionSpawnPalette,
        ArchitectHotkeyCatalog.SectionFind,
        ArchitectHotkeyCatalog.SectionQuickKeySpawn,
        ArchitectHotkeyCatalog.SectionBookmarks,
        ArchitectHotkeyCatalog.SectionDocumentation,
        ArchitectHotkeyCatalog.SectionRetired,
    };

    /// <summary>
    /// Chrome column sections — chords routed through
    /// <see cref="Controls.ArchitectChrome"/> and
    /// <c>ArchitectSiblingWindow</c>'s <c>KeyboardAccelerators</c>. The
    /// canvas list duplicates several entries (Ctrl+S / Ctrl+O / Ctrl+Shift+S
    /// etc.) because the user's mental model splits between "canvas action"
    /// and "menu action" — the chrome accelerators route through the
    /// chrome event chain (MainView.OnChrome*), the canvas accelerators
    /// short-circuit inside the canvas itself. Functionally identical for
    /// the user; useful when diagnosing why a chord stopped firing.
    /// </summary>
    private static readonly string[] ChromeSections =
    {
        ArchitectHotkeyCatalog.SectionFile,
        ArchitectHotkeyCatalog.SectionEdit,
        ArchitectHotkeyCatalog.SectionView,
        ArchitectHotkeyCatalog.SectionHelp,
    };

    public KeyboardShortcutsDialog()
    {
        Title = "Keyboard Shortcuts";
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        CloseButtonText = "Close";
        DefaultButton = ContentDialogButton.Close;

        // The four keyed TextBlock styles that lived in
        // <ContentDialog.Resources> — rebuilt in code with ONLY literal setters
        // (no {StaticResource}/{ThemeResource}), so the dictionary cannot throw
        // at construction. FontFamily + Foreground are applied in code below
        // (eyebrows/subtitle/hairlines) and in BuildColumn (rows). Keeping the
        // styles keyed in this.Resources lets the ctor lookups + BuildColumn run
        // unchanged. See DialogTheme.cs.
        Resources["ShortcutSectionEyebrow"] = BuildStyle(
            (TextBlock.FontSizeProperty, 10d),
            (TextBlock.FontWeightProperty, Microsoft.UI.Text.FontWeights.SemiBold),
            (TextBlock.CharacterSpacingProperty, 180),
            (FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 4)));
        Resources["ShortcutChord"] = BuildStyle(
            (TextBlock.FontSizeProperty, 11d),
            (TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
            (TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
        Resources["ShortcutDescription"] = BuildStyle(
            (TextBlock.FontSizeProperty, 12d),
            (TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
            (TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        Resources["ShortcutGroupHeader"] = BuildStyle(
            (TextBlock.FontSizeProperty, 11d),
            (TextBlock.FontWeightProperty, Microsoft.UI.Text.FontWeights.SemiBold),
            (TextBlock.CharacterSpacingProperty, 80),
            (FrameworkElement.MarginProperty, new Thickness(0, 10, 0, 4)));

        var eyebrowStyle = (Style)Resources["ShortcutSectionEyebrow"];

        // ── Root grid (780×560, three rows: header / hairline / body) ──
        var root = new Grid { Width = 780, Height = 560 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Eyebrow header — matches the chrome's wordmark band so the dialog
        // reads as a continuation of the same shell.
        var header = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 8) };
        ArchitectEyebrow = new TextBlock { Text = "ARCHITECT", Style = eyebrowStyle };
        SubtitleText = new TextBlock
        {
            FontSize = 13,
            Text = "Canvas chords on the left, Chrome chords on the right.",
        };
        header.Children.Add(ArchitectEyebrow);
        header.Children.Add(SubtitleText);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Brass hairline, mirrors the chrome's TitleAccentBar.
        BrassHairline = new Rectangle();
        Grid.SetRow(BrassHairline, 1);
        root.Children.Add(BrassHairline);

        // Two-column body. Each column is its own ScrollViewer so a long Canvas
        // list doesn't push the Chrome list out of view.
        var body = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ─── Canvas column ───────────────────────────────────────────
        var canvasScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 12, 0),
        };
        CanvasColumn = new StackPanel { Spacing = 2 };
        CanvasEyebrow = new TextBlock { Style = eyebrowStyle, Text = "CANVAS" };
        CanvasColumn.Children.Add(CanvasEyebrow);
        canvasScroll.Content = CanvasColumn;
        Grid.SetColumn(canvasScroll, 0);
        body.Children.Add(canvasScroll);

        // Vertical divider between columns.
        ColumnDivider = new Rectangle
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Width = 1,
        };
        Grid.SetColumn(ColumnDivider, 1);
        body.Children.Add(ColumnDivider);

        // ─── Chrome column ───────────────────────────────────────────
        var chromeScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(12, 0, 0, 0),
        };
        ChromeColumn = new StackPanel { Spacing = 2 };
        ChromeEyebrow = new TextBlock { Style = eyebrowStyle, Text = "CHROME" };
        ChromeColumn.Children.Add(ChromeEyebrow);
        chromeScroll.Content = ChromeColumn;
        Grid.SetColumn(chromeScroll, 2);
        body.Children.Add(chromeScroll);

        Grid.SetRow(body, 2);
        root.Children.Add(body);

        Content = root;

        // Theme is applied in code (NOT via XAML resource markup) — a
        // code-constructed library dialog can't resolve {StaticResource}/
        // {ThemeResource} at parse. See DialogTheme. The keyed styles above
        // carry only literal setters; FontFamily + Foreground are set here
        // (eyebrows/subtitle/hairlines) and in BuildColumn (rows).
        if (DialogTheme.Brush("CoalShellBrush") is { } shell) Background  = shell;
        if (DialogTheme.Brush("CoalCardBrush")  is { } card)  BorderBrush = card;

        var displayFont = DialogTheme.Font("DisplayFont");
        var ember       = DialogTheme.Brush("EmberPrimaryBrush");
        foreach (var eyebrow in new[] { ArchitectEyebrow, CanvasEyebrow, ChromeEyebrow })
        {
            if (displayFont is not null) eyebrow.FontFamily = displayFont;
            if (ember is not null)       eyebrow.Foreground = ember;
        }
        if (DialogTheme.Font("SansFont")                is { } subFont) SubtitleText.FontFamily = subFont;
        if (DialogTheme.Brush("CoalSecondaryTextBrush") is { } subFg)   SubtitleText.Foreground = subFg;
        if (DialogTheme.Brush("BrassGradientBrush")     is { } brass)   BrassHairline.Fill = brass;
        if (DialogTheme.Brush("CoalHoverBrush")         is { } hover)   ColumnDivider.Fill = hover;

        // Pre-resolve the dialog-local (literal-only) styles once. ContentDialog's
        // content sub-tree doesn't reliably inherit the dialog's own Resources via
        // implicit FindResource walks (the popup host re-parents the content), so
        // resolving against `this.Resources` up-front sidesteps the "row uses an
        // empty default Style" failure mode.
        var headerStyle = (Style)Resources["ShortcutGroupHeader"];
        var chordStyle  = (Style)Resources["ShortcutChord"];
        var descStyle   = (Style)Resources["ShortcutDescription"];
        // Single source of truth lives in ArchitectHotkeyCatalog — the
        // dialog and the bottom-left HotkeyCheatsheet overlay both render
        // off the same entries so chord-set changes stay in lockstep.
        BuildColumn(CanvasColumn, ArchitectHotkeyCatalog.GroupedBySection(CanvasSections),
                    headerStyle, chordStyle, descStyle);
        BuildColumn(ChromeColumn, ArchitectHotkeyCatalog.GroupedBySection(ChromeSections),
                    headerStyle, chordStyle, descStyle);
    }

    /// <summary>
    /// Build a literal-only <see cref="Style"/> targeting <see cref="TextBlock"/>.
    /// Equivalent to the keyed <c>&lt;Style TargetType="TextBlock"&gt;</c> blocks
    /// that lived in <c>&lt;ContentDialog.Resources&gt;</c> — every setter value is
    /// a literal (no resource markup), so this is safe to build at construction.
    /// </summary>
    private static Style BuildStyle(params (DependencyProperty Property, object Value)[] setters)
    {
        var style = new Style(typeof(TextBlock));
        foreach (var (property, value) in setters)
            style.Setters.Add(new Setter(property, value));
        return style;
    }

    /// <summary>
    /// Show the dialog on <paramref name="xamlRoot"/>. Wrapped so the
    /// caller doesn't have to set <see cref="ContentDialog.XamlRoot"/>
    /// manually and so failures (e.g. XamlRoot null because the host
    /// window is mid-teardown) route through GlobalLogger consistently.
    /// </summary>
    public static async System.Threading.Tasks.Task ShowAsync(XamlRoot xamlRoot)
    {
        if (xamlRoot is null)
        {
            GlobalLogger.Log(
                "KeyboardShortcutsDialog: no XamlRoot available — skipping show.",
                "Architect.Dialogs",
                Phoenix.Controls.Shared.Models.LogLevel.System);
            return;
        }

        try
        {
            var dlg = new KeyboardShortcutsDialog { XamlRoot = xamlRoot };
            await dlg.ShowAsync();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error(
                "Architect.Dialogs",
                "KeyboardShortcutsDialog.ShowAsync",
                ex);
        }
    }

    /// <summary>
    /// Populate <paramref name="host"/> with section headers + chord rows.
    /// Chord cells are fixed-width (Grid.Column 0 ⇒ 180 DIP) so chord
    /// glyphs align across rows; descriptions wrap into the remaining
    /// column.
    /// </summary>
    private static void BuildColumn(
        StackPanel host,
        IReadOnlyList<(string Section, IReadOnlyList<HotkeyEntry> Entries)> groups,
        Style headerStyle,
        Style chordStyle,
        Style descStyle)
    {
        // FontFamily + Foreground come from the app theme in code (the keyed
        // styles carry only literal setters — see ctor / DialogTheme).
        var sans     = DialogTheme.Font("SansFont");
        var mono     = DialogTheme.Font("MonoFont");
        var headerFg = DialogTheme.Brush("CoalSecondaryTextBrush");
        var chordFg  = DialogTheme.Brush("CoalPaperBrush");
        var descFg   = DialogTheme.Brush("CoalBodyTextBrush");

        foreach (var (section, entries) in groups)
        {
            var headerTb = new TextBlock { Text = section, Style = headerStyle };
            if (sans is not null)     headerTb.FontFamily = sans;
            if (headerFg is not null) headerTb.Foreground = headerFg;
            host.Children.Add(headerTb);

            foreach (var entry in entries)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var chordText = new TextBlock { Text = entry.Combo, Style = chordStyle };
                if (mono is not null)    chordText.FontFamily = mono;
                if (chordFg is not null) chordText.Foreground = chordFg;
                Grid.SetColumn(chordText, 0);

                var descText = new TextBlock { Text = entry.Description, Style = descStyle };
                if (sans is not null)   descText.FontFamily = sans;
                if (descFg is not null) descText.Foreground = descFg;
                Grid.SetColumn(descText, 1);

                row.Children.Add(chordText);
                row.Children.Add(descText);
                host.Children.Add(row);
            }
        }
    }
}
