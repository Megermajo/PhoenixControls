using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Visualist.Core;

namespace Phoenix.Controls.Visualist.WinUI.Dialogs;

/// <summary>
/// Pop-up editor for a <see cref="WebOverlaySinkNode"/> (WebOverlay.Custom) node —
/// reached from the node's right-click "Edit HTML / CSS…" item. Edits the author's
/// HTML fragment + CSS text (stored raw in the node's <c>Html</c> / <c>Css</c>
/// attributes) and the names of the 8 String input slots (each injected by
/// compositor.js as a named CSS custom property <c>--&lt;name&gt;</c>).
///
/// On <see cref="ContentDialogResult.Primary"/> the caller invokes <see cref="ApplyTo"/>
/// (after PushUndo) to write the edits back onto the node; Cancel leaves it untouched.
///
/// No .xaml / InitializeComponent — a code-constructed library ContentDialog throws
/// XamlParseException at Application.LoadComponent, so the whole tree is built in code
/// and the theme brushes/fonts are resolved at runtime via <see cref="DialogTheme"/>.
/// Same rationale as <see cref="CurveEditorDialog"/>. The dialog stays Visualist-local
/// (per-pillar chrome isolation) — no Shared lift.
/// </summary>
public sealed class WebOverlayEditorDialog : ContentDialog
{
    private readonly TextBlock _eyebrow;
    private readonly TextBlock _subtitle;
    private readonly TextBlock _htmlLabel;
    private readonly TextBlock _cssLabel;
    private readonly TextBlock _varsLabel;
    private readonly TextBlock _hint;
    private readonly TextBox _htmlBox;
    private readonly TextBox _cssBox;
    private readonly TextBox[] _slotBoxes = new TextBox[WebOverlaySinkNode.SlotCount];

    public WebOverlayEditorDialog(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);

        Title = "Edit Web Overlay";
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        PrimaryButtonText = Localizer.T("common.button.save", "Save");
        CloseButtonText = Localizer.T("common.button.cancel", "Cancel");
        DefaultButton = ContentDialogButton.Primary;

        var root = new StackPanel { Width = 560, Spacing = 0 };

        // ── header ──────────────────────────────────────────────────────────
        _eyebrow = new TextBlock
        {
            Text = "VISUALIST · WEB OVERLAY",
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 180,
            Margin = new Thickness(0, 0, 0, 4),
        };
        _subtitle = new TextBlock
        {
            FontSize = 13,
            Text = "Author HTML + CSS rendered live over this widget. The 8 String inputs inject as CSS variables.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        root.Children.Add(_eyebrow);
        root.Children.Add(_subtitle);

        // ── body (scrolls if the fields overflow the dialog) ────────────────
        var body = new StackPanel { Spacing = 4 };

        _htmlLabel = MakeFieldLabel("HTML");
        body.Children.Add(_htmlLabel);
        _htmlBox = MakeCodeBox(120);
        _htmlBox.Text = node.Attributes.TryGetValue(WebOverlaySinkNode.HtmlAttr, out var h) ? h : "";
        body.Children.Add(_htmlBox);

        _cssLabel = MakeFieldLabel("CSS");
        body.Children.Add(_cssLabel);
        _cssBox = MakeCodeBox(180);
        _cssBox.Text = node.Attributes.TryGetValue(WebOverlaySinkNode.CssAttr, out var c) ? c : "";
        body.Children.Add(_cssBox);

        _varsLabel = MakeFieldLabel("VARIABLE NAMES  ( --name )");
        body.Children.Add(_varsLabel);
        body.Children.Add(BuildSlotGrid(node));

        _hint = new TextBlock
        {
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Text = "Each String input is injected as --name on the widget. Reference it in CSS, e.g. "
                 + "color: var(--slot1);  — wire the inputs from an Architect script via Visual.Trigger. "
                 + "Names are scoped to this widget (Shadow DOM), so they never clash with other overlays.",
        };
        body.Children.Add(_hint);

        var scroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 460,
        };
        root.Children.Add(scroll);

        Content = root;

        ApplyDialogTheme();
        ApplyLocalization();
    }

    // ── model write-back ────────────────────────────────────────────────────

    /// <summary>
    /// Write the edited HTML/CSS + slot names onto <paramref name="target"/>. Slot names
    /// are sanitised to valid CSS custom-property identifiers and de-duplicated (a blank /
    /// colliding name falls back to the default "slotN") so each input maps to a distinct
    /// <c>--name</c> and compositor.js's by-name socket lookup stays unambiguous. Call after
    /// PushUndo; the caller then MarkDirty + Rebuild to refresh the node view + preview.
    /// </summary>
    public void ApplyTo(Node target)
    {
        ArgumentNullException.ThrowIfNull(target);

        target.Attributes[WebOverlaySinkNode.HtmlAttr] = _htmlBox.Text ?? "";
        target.Attributes[WebOverlaySinkNode.CssAttr] = _cssBox.Text ?? "";

        var inputs = target.Sockets.Where(s => s.Type == SocketType.Input).ToList();
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < inputs.Count && i < _slotBoxes.Length; i++)
        {
            inputs[i].Name = SanitizeVarName(_slotBoxes[i].Text, i + 1, used);
        }
    }

    private static string SanitizeVarName(string? raw, int oneBasedSlot, HashSet<string> used)
    {
        var chars = (raw ?? "").Trim()
            .Select(ch => (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_') ? ch : '-')
            .ToArray();
        var s = new string(chars).TrimStart('-');
        if (s.Length == 0) s = WebOverlaySinkNode.DefaultSlotName(oneBasedSlot);

        // Ensure uniqueness deterministically so no two sockets share a name.
        var baseName = s;
        int n = 2;
        while (!used.Add(s)) { s = baseName + "-" + n; n++; }
        return s;
    }

    // ── builders ─────────────────────────────────────────────────────────────

    private static TextBlock MakeFieldLabel(string text) => new()
    {
        Text = text,
        FontSize = 11,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        CharacterSpacing = 80,
        Margin = new Thickness(0, 8, 0, 4),
    };

    private static TextBox MakeCodeBox(double height) => new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        IsSpellCheckEnabled = false,
        FontSize = 12,
        MinHeight = height,
        Height = height,
        VerticalContentAlignment = VerticalAlignment.Top,
    };

    private Grid BuildSlotGrid(Node node)
    {
        var inputs = node.Sockets.Where(s => s.Type == SocketType.Input).ToList();

        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int rows = (WebOverlaySinkNode.SlotCount + 1) / 2;
        for (int r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < WebOverlaySinkNode.SlotCount; i++)
        {
            var cell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            cell.Children.Add(new TextBlock
            {
                Text = (i + 1).ToString(),
                Width = 16,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right,
                Opacity = 0.6,
            });
            var box = new TextBox
            {
                FontSize = 12,
                MinWidth = 120,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Text = i < inputs.Count ? (inputs[i].Name ?? WebOverlaySinkNode.DefaultSlotName(i + 1))
                                        : WebOverlaySinkNode.DefaultSlotName(i + 1),
            };
            _slotBoxes[i] = box;
            cell.Children.Add(box);

            Grid.SetRow(cell, i / 2);
            Grid.SetColumn(cell, i % 2);
            grid.Children.Add(cell);
        }
        return grid;
    }

    // ── theme + localization (runtime-resolved; see CurveEditorDialog) ───────

    private void ApplyDialogTheme()
    {
        if (DialogTheme.Brush("CoalShellBrush") is { } shell) Background = shell;
        if (DialogTheme.Brush("CoalCardBrush") is { } card) BorderBrush = card;

        if (DialogTheme.Font("DisplayFont") is { } displayFont) _eyebrow.FontFamily = displayFont;
        if (DialogTheme.Brush("EmberPrimaryBrush") is { } ember) _eyebrow.Foreground = ember;

        if (DialogTheme.Font("SansFont") is { } sansFont) _subtitle.FontFamily = sansFont;
        if (DialogTheme.Brush("CoalSecondaryTextBrush") is { } secText) _subtitle.Foreground = secText;

        var fieldFont = DialogTheme.Font("SansFont");
        var fieldBrush = DialogTheme.Brush("CoalSecondaryTextBrush");
        foreach (var label in new[] { _htmlLabel, _cssLabel, _varsLabel })
        {
            if (fieldFont is { } ff) label.FontFamily = ff;
            if (fieldBrush is { } fb) label.Foreground = fb;
        }

        if (DialogTheme.Brush("CoalMutedTextBrush") is { } muted) _hint.Foreground = muted;
        if (DialogTheme.Font("MonoFont") is { } monoFont)
        {
            _htmlBox.FontFamily = monoFont;
            _cssBox.FontFamily = monoFont;
            _hint.FontFamily = monoFont;
            foreach (var box in _slotBoxes) if (box is not null) box.FontFamily = monoFont;
        }
    }

    private void ApplyLocalization()
    {
        Title = Localizer.T("visualist.weboverlay.title", "Edit Web Overlay");
        _eyebrow.Text = Localizer.T("visualist.dialog.weboverlay.eyebrow", "VISUALIST · WEB OVERLAY");
        _subtitle.Text = Localizer.T("visualist.weboverlay.subtitle",
            "Author HTML + CSS rendered live over this widget. The 8 String inputs inject as CSS variables.");
        _htmlLabel.Text = Localizer.T("visualist.weboverlay.section.html", "HTML");
        _cssLabel.Text = Localizer.T("visualist.weboverlay.section.css", "CSS");
        _varsLabel.Text = Localizer.T("visualist.weboverlay.section.vars", "VARIABLE NAMES  ( --name )");
        _hint.Text = Localizer.T("visualist.weboverlay.hint",
            "Each String input is injected as --name on the widget. Reference it in CSS, e.g. "
            + "color: var(--slot1);  — wire the inputs from an Architect script via Visual.Trigger. "
            + "Names are scoped to this widget (Shadow DOM), so they never clash with other overlays.");
    }
}
