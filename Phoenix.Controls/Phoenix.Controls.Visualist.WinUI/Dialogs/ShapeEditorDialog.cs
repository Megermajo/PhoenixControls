using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Visualist.Core;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace Phoenix.Controls.Visualist.WinUI.Dialogs;

/// <summary>
/// Port of the
/// pre-T15 WinForms <c>ShapeEditor</c> (Phoenix.Controls.Visualist/Controls/
/// ShapeEditor.cs). Modal <see cref="ContentDialog"/> that authors the
/// <c>Vertices</c> JSON list on a <c>Mask.Polygon</c> / <c>Mask.Bezier</c>
/// node.
///
/// <para>
/// Coordinate space inside the editor is normalised 0..1 to match the kernel
/// contract; the canvas paints at a fixed surface (≈600×400) but every drag
/// clamps the underlying vertex coords to the unit square. Bezier mode is
/// auto-detected from the node title — <c>Mask.Bezier</c> shows the cp1/cp2
/// handles for the selected vertex; <c>Mask.Polygon</c> hides them.
/// </para>
///
/// <para>
/// API surface mirrors the baseline + <see cref="CurveEditorDialog"/>: the
/// dialog mutates the passed node only on the OK (Primary) button, leaves it
/// untouched on Cancel, and exposes <see cref="OnRequestAnimateVertex"/> so
/// the caller (graph canvas / editor view) can append timeline keyframes —
/// the editor itself never touches the timeline (same separation as
/// <c>WidgetGraphCanvas.OnRequestAnimateParameter</c>).
/// </para>
///
/// <para>
/// Visualist-local — no Shared lift. Guardrails (min/max vertex limits) route
/// through <see cref="GlobalLogger"/> rather than nested modals for these
/// repeatable rejections.
/// </para>
///
/// <para>
/// This dialog has NO .xaml /
/// InitializeComponent. A code-constructed ContentDialog defined in a LIBRARY
/// assembly (Visualist.WinUI) throws XamlParseException at
/// Application.LoadComponent when <c>new</c>'d while detached — proven by the
/// 1.0.6 runtime stack trace, which still crashed AFTER all resource markup was
/// stripped. The throw is in the XAML parse itself, before any resource /
/// DialogTheme code runs. Building the content in code removes LoadComponent
/// entirely, so the parse can't fail by construction (the default ContentDialog
/// template still resolves at ShowAsync against Hub's app scope, which merges
/// XamlControlsResources — the show path that already works for Hub's dialogs).
/// See NameTypeDialog / DialogTheme.cs for the full rationale.
/// </para>
/// </summary>
public sealed class ShapeEditorDialog : ContentDialog
{
    private readonly Node   _node;
    private readonly bool   _isBezier;
    private readonly string _origVerticesJson;
    private readonly string _origClosed;

    private List<ShapeData.Vertex> _verts;
    private bool _closed;
    private int  _selected = -1;

    // Editor-local undo / redo — each snapshot captures (vertices JSON, closed).
    private readonly Stack<(string Json, bool Closed)> _undo = new();
    private readonly Stack<(string Json, bool Closed)> _redo = new();

    // Re-entrancy guard so programmatic ClosedToggle / numeric-box writes don't
    // re-fire their own change handlers back into a snapshot.
    private bool _suppressEvents;

    // Drag state on the surface. -1 / null = no drag.
    private int _dragVertex = -1;
    private string? _dragHandle; // "cp1" | "cp2" | null

    private const double VertexRadius = 6.0;
    private const double HandleRadius = 4.0;
    private const double HitSlop      = 4.0;
    private const double SurfacePad   = 8.0;

    // The x:Name'd elements from the retired XAML are now `*Field` backing
    // fields, declared below near BuildContent, and aliased to their original
    // identifiers (EyebrowText, ShapeSurface, …) via the property accessors so
    // the rest of this class keeps referencing the SAME names verbatim.

    /// <summary>
    /// Fires when the user clicks "Animate Vertex" (or the per-vertex
    /// right-click "Animate" item). Args: (vertexIndex, currentX, currentY,
    /// isBezier). The caller appends keyframes at <c>TimeMs=0</c> with the
    /// <see cref="ShapeData.FormatVertexPath"/> grammar.
    /// </summary>
    public event Action<int, double, double, bool>? OnRequestAnimateVertex;

    public ShapeEditorDialog(Node node)
    {
        BuildContent();
        // Code-constructed library dialog — theme applied in code via DialogTheme;
        // no directly-resolved resource markup in the content tree (the dialog has
        // no XAML at all). See Architect NameTypeDialog / DialogTheme.cs.
        ApplyDialogTheme();
        _node = node ?? throw new ArgumentNullException(nameof(node));

        _isBezier         = string.Equals(node.Title, "Mask.Bezier", StringComparison.Ordinal);
        _origVerticesJson = node.Attributes.TryGetValue("Vertices", out var vj) ? vj : "[]";
        _origClosed       = node.Attributes.TryGetValue("Closed",   out var cl) ? cl : "true";

        _verts = ShapeData.Parse(_origVerticesJson);
        if (_verts.Count == 0)
            _verts = _isBezier ? ShapeData.DefaultBezier() : ShapeData.DefaultPolygon();
        _closed = !string.Equals(_origClosed, "false", StringComparison.OrdinalIgnoreCase);

        Title = _isBezier
            ? Localizer.T("visualist.shape.title.bezier", "Edit Bezier Shape")
            : Localizer.T("visualist.shape.title.polygon", "Edit Polygon Shape");
        EyebrowText.Text  = _isBezier
            ? Localizer.T("visualist.dialog.shape.eyebrow.bezier",  "VISUALIST · BEZIER")
            : Localizer.T("visualist.dialog.shape.eyebrow.polygon", "VISUALIST · POLYGON");
        SubtitleText.Text = _isBezier
            ? Localizer.T("visualist.dialog.shape.subtitle.bezier",
                "Author the bezier mask. Drag a vertex to move; drag the green/yellow handles to shape the curve.")
            : Localizer.T("visualist.dialog.shape.subtitle.polygon",
                "Author the polygon mask. Click empty space to add, drag a vertex to move.");

        // Bezier-only rows.
        BezierHandlePanel.Visibility = _isBezier ? Visibility.Visible : Visibility.Collapsed;

        // Route the remaining user-facing strings through the Localizer
        // (the WinUI port had left the toggle / buttons / section headers / hint
        // hardcoded). Fallbacks preserve the current English text.
        ApplyLocalization();

        _suppressEvents = true;
        ClosedToggle.IsChecked = _closed;
        _suppressEvents = false;

        PrimaryButtonClick += OnOkClick;
        CloseButtonClick   += OnCancelClick;
        KeyDown            += OnDialogKeyDown;

        SyncSelectedFields();
        // First paint happens after the surface gets its size via
        // OnSurfaceSizeChanged; RenderSurface is also safe to call before that
        // (it early-returns on a zero-size surface).
    }

    // ── content construction (replaces the retired XAML + InitializeComponent) ──
    //
    // Faithful 1:1 rebuild of ShapeEditorDialog.xaml. The root attributes, the
    // <ContentDialog.Resources> MaxWidth/MaxHeight overrides, the five keyed
    // Styles (applied inline as setters), every x:Name'd element, every literal
    // property, and every event-handler wiring are reproduced exactly. Theme
    // brushes / fonts are applied separately in ApplyDialogTheme (as before).
    private void BuildContent()
    {
        // Root ContentDialog attributes that were on <ContentDialog …>.
        Title = "Edit Shape";
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        PrimaryButtonText = Localizer.T("common.ok", "OK");
        CloseButtonText = Localizer.T("common.button.cancel", "Cancel");
        DefaultButton = ContentDialogButton.Primary;

        // <ContentDialog.Resources> — size overrides read by the default
        // ContentDialog template. Keep them.
        Resources["ContentDialogMaxWidth"]  = 980.0;
        Resources["ContentDialogMaxHeight"] = 820.0;

        // ── eyebrow header (Grid.Row 0) ──
        // ShapeDialogEyebrow style applied inline.
        EyebrowTextField.FontSize = 10;
        EyebrowTextField.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        EyebrowTextField.CharacterSpacing = 180;
        EyebrowTextField.Margin = new Thickness(0, 0, 0, 4);
        EyebrowTextField.Text = "VISUALIST · SHAPE";

        SubtitleTextField.FontSize = 13;
        SubtitleTextField.Text = "Author the mask vertices. Click empty space to add, drag a vertex to move.";

        var headerPanel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 8) };
        headerPanel.Children.Add(EyebrowTextField);
        headerPanel.Children.Add(SubtitleTextField);
        Grid.SetRow(headerPanel, 0);

        // ── hairline rule (Grid.Row 1) ──
        Grid.SetRow(HairlineRuleField, 1);

        // ── content row (Grid.Row 2) ──
        var contentGrid = new Grid { Margin = new Thickness(0, 10, 0, 0), ColumnSpacing = 14 };
        // Vertex canvas column stretches; right column fixed 220px.
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 420 });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        Grid.SetRow(contentGrid, 2);

        // ── left: vertex canvas ──
        var leftColumn = new StackPanel { Spacing = 6 };
        Grid.SetColumn(leftColumn, 0);

        var toggleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        ClosedToggleField.Content = "Closed path";
        ClosedToggleField.FontSize = 12;
        ClosedToggleField.Checked += OnClosedToggled;
        ClosedToggleField.Unchecked += OnClosedToggled;

        VertexCountTextField.VerticalAlignment = VerticalAlignment.Center;
        VertexCountTextField.FontSize = 10;
        VertexCountTextField.Text = FormatVertexCount(0);

        toggleRow.Children.Add(ClosedToggleField);
        toggleRow.Children.Add(VertexCountTextField);
        leftColumn.Children.Add(toggleRow);

        // Canvas fills this frame.
        ShapeSurfaceFrameField.Height = 440;
        ShapeSurfaceFrameField.HorizontalAlignment = HorizontalAlignment.Stretch;
        ShapeSurfaceFrameField.MinWidth = 420;
        ShapeSurfaceFrameField.BorderThickness = new Thickness(1);
        ShapeSurfaceFrameField.CornerRadius = new CornerRadius(4);
        ShapeSurfaceFrameField.SizeChanged += OnSurfaceFrameSizeChanged;

        ShapeSurfaceField.SizeChanged += OnSurfaceSizeChanged;
        ShapeSurfaceField.PointerPressed += OnSurfacePointerPressed;
        ShapeSurfaceField.PointerMoved += OnSurfacePointerMoved;
        ShapeSurfaceField.PointerReleased += OnSurfacePointerReleased;
        ShapeSurfaceField.RightTapped += OnSurfaceRightTapped;

        ShapeSurfaceFrameField.Child = ShapeSurfaceField;
        leftColumn.Children.Add(ShapeSurfaceFrameField);

        // ── right: numeric + actions ──
        var rightColumn = new StackPanel { Spacing = 2 };
        Grid.SetColumn(rightColumn, 1);

        // ShapeDialogFieldLabel style applied inline to the three section labels.
        ApplyFieldLabelStyle(SelectedVertexLabelField);
        SelectedVertexLabelField.Text = "SELECTED VERTEX";
        rightColumn.Children.Add(SelectedVertexLabelField);

        // X coord row.
        var xRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
        ApplyCoordLabelStyle(XCoordLabelField);
        XCoordLabelField.Text = "X";
        ApplyCoordBoxStyle(XBoxField);
        XBoxField.KeyDown += OnCoordKeyDown;
        XBoxField.LostFocus += OnCoordLostFocus;
        XBoxField.Tag = "x";
        xRow.Children.Add(XCoordLabelField);
        xRow.Children.Add(XBoxField);
        rightColumn.Children.Add(xRow);

        // Y coord row.
        var yRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
        ApplyCoordLabelStyle(YCoordLabelField);
        YCoordLabelField.Text = "Y";
        ApplyCoordBoxStyle(YBoxField);
        YBoxField.KeyDown += OnCoordKeyDown;
        YBoxField.LostFocus += OnCoordLostFocus;
        YBoxField.Tag = "y";
        yRow.Children.Add(YCoordLabelField);
        yRow.Children.Add(YBoxField);
        rightColumn.Children.Add(yRow);

        // Bezier handle panel.
        ApplyFieldLabelStyle(BezierHandlesLabelField);
        BezierHandlesLabelField.Text = "BEZIER HANDLES";
        BezierHandlePanelField.Children.Add(BezierHandlesLabelField);

        BezierHandlePanelField.Children.Add(BuildCpRow(Cp1XCoordLabelField, "Cp1 X", Cp1XBoxField, "cp1x"));
        BezierHandlePanelField.Children.Add(BuildCpRow(Cp1YCoordLabelField, "Cp1 Y", Cp1YBoxField, "cp1y"));
        BezierHandlePanelField.Children.Add(BuildCpRow(Cp2XCoordLabelField, "Cp2 X", Cp2XBoxField, "cp2x"));
        BezierHandlePanelField.Children.Add(BuildCpRow(Cp2YCoordLabelField, "Cp2 Y", Cp2YBoxField, "cp2y"));
        rightColumn.Children.Add(BezierHandlePanelField);

        // Actions.
        ApplyFieldLabelStyle(ActionsLabelField);
        ActionsLabelField.Text = "ACTIONS";
        rightColumn.Children.Add(ActionsLabelField);

        AddVertexButtonField.Content = "Add Vertex";
        AddVertexButtonField.HorizontalAlignment = HorizontalAlignment.Stretch;
        AddVertexButtonField.Margin = new Thickness(0, 2, 0, 0);
        AddVertexButtonField.Click += OnAddVertexClick;
        rightColumn.Children.Add(AddVertexButtonField);

        DeleteVertexButtonField.Content = "Delete Vertex";
        DeleteVertexButtonField.HorizontalAlignment = HorizontalAlignment.Stretch;
        DeleteVertexButtonField.Margin = new Thickness(0, 4, 0, 0);
        DeleteVertexButtonField.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        DeleteVertexButtonField.BorderThickness = new Thickness(1);
        DeleteVertexButtonField.IsEnabled = false;
        DeleteVertexButtonField.Click += OnDeleteVertexClick;
        rightColumn.Children.Add(DeleteVertexButtonField);

        AnimateVertexButtonField.Content = "Animate Vertex";
        AnimateVertexButtonField.HorizontalAlignment = HorizontalAlignment.Stretch;
        AnimateVertexButtonField.Margin = new Thickness(0, 4, 0, 0);
        AnimateVertexButtonField.IsEnabled = false;
        AnimateVertexButtonField.Click += OnAnimateVertexClick;
        rightColumn.Children.Add(AnimateVertexButtonField);

        // ShapeDialogHint style applied inline.
        HintTextField.FontSize = 10;
        HintTextField.Margin = new Thickness(0, 6, 0, 0);
        HintTextField.TextWrapping = TextWrapping.Wrap;
        HintTextField.Text = "Ctrl+Z / Ctrl+Y undo-redo · Del removes the selected vertex (min 2).";
        rightColumn.Children.Add(HintTextField);

        contentGrid.Children.Add(leftColumn);
        contentGrid.Children.Add(rightColumn);

        // Outer Grid (3 rows: header / 2px rule / content).
        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2) });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.Children.Add(headerPanel);
        rootGrid.Children.Add(HairlineRuleField);
        rootGrid.Children.Add(contentGrid);

        // Scroll fallback so the dialog stays usable at high DPI.
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollMode          = ScrollMode.Auto,
            VerticalScrollMode            = ScrollMode.Auto,
            Content                       = rootGrid,
        };
        Content = scroll;
    }

    // The fields are created here (used by BuildContent above). They are the
    // x:Name'd elements from the retired XAML, now created in code; the public
    // property names below alias them so the rest of the class (handlers, theme,
    // logic) keeps referencing the SAME identifiers as the old code-behind.
    private readonly TextBlock  EyebrowTextField        = new();
    private readonly TextBlock  SubtitleTextField       = new();
    private readonly Rectangle  HairlineRuleField       = new();
    private readonly CheckBox   ClosedToggleField       = new();
    private readonly TextBlock  VertexCountTextField    = new();
    private readonly Border     ShapeSurfaceFrameField  = new();
    private readonly Microsoft.UI.Xaml.Controls.Canvas ShapeSurfaceField = new();
    private readonly TextBlock  SelectedVertexLabelField= new();
    private readonly TextBlock  XCoordLabelField        = new();
    private readonly TextBox    XBoxField               = new();
    private readonly TextBlock  YCoordLabelField        = new();
    private readonly TextBox    YBoxField               = new();
    private readonly StackPanel BezierHandlePanelField  = new();
    private readonly TextBlock  BezierHandlesLabelField = new();
    private readonly TextBlock  Cp1XCoordLabelField     = new();
    private readonly TextBox    Cp1XBoxField            = new();
    private readonly TextBlock  Cp1YCoordLabelField     = new();
    private readonly TextBox    Cp1YBoxField            = new();
    private readonly TextBlock  Cp2XCoordLabelField     = new();
    private readonly TextBox    Cp2XBoxField            = new();
    private readonly TextBlock  Cp2YCoordLabelField     = new();
    private readonly TextBox    Cp2YBoxField            = new();
    private readonly TextBlock  ActionsLabelField       = new();
    private readonly Button     AddVertexButtonField    = new();
    private readonly Button     DeleteVertexButtonField = new();
    private readonly Button     AnimateVertexButtonField= new();
    private readonly TextBlock  HintTextField           = new();

    // One Cp coordinate row: a label + a coord TextBox carrying its axis Tag.
    private StackPanel BuildCpRow(TextBlock label, string labelText, TextBox box, string tag)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
        ApplyCoordLabelStyle(label);
        label.Text = labelText;
        ApplyCoordBoxStyle(box);
        box.KeyDown += OnCoordKeyDown;
        box.LostFocus += OnCoordLostFocus;
        box.Tag = tag;
        row.Children.Add(label);
        row.Children.Add(box);
        return row;
    }

    // ── keyed-style setters applied inline ──

    // ShapeDialogFieldLabel: FontSize 11, SemiBold, CharacterSpacing 80, Margin 0,8,0,2.
    private static void ApplyFieldLabelStyle(TextBlock t)
    {
        t.FontSize = 11;
        t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        t.CharacterSpacing = 80;
        t.Margin = new Thickness(0, 8, 0, 2);
    }

    // ShapeCoordLabel: FontSize 10, VerticalAlignment Center, Width 38.
    private static void ApplyCoordLabelStyle(TextBlock t)
    {
        t.FontSize = 10;
        t.VerticalAlignment = VerticalAlignment.Center;
        t.Width = 38;
    }

    // ShapeCoordBox: FontSize 11, Width 120, MinHeight 28.
    private static void ApplyCoordBoxStyle(TextBox b)
    {
        b.FontSize = 11;
        b.Width = 120;
        b.MinHeight = 28;
    }

    // ── named-element accessors ───────────────────────────────────────────────
    // Alias the *Field backing fields to the x:Name identifiers the rest of this
    // class uses verbatim from the original code-behind.
    private TextBlock  EyebrowText         => EyebrowTextField;
    private TextBlock  SubtitleText        => SubtitleTextField;
    private Rectangle  HairlineRule        => HairlineRuleField;
    private CheckBox   ClosedToggle        => ClosedToggleField;
    private TextBlock  VertexCountText     => VertexCountTextField;
    private Border     ShapeSurfaceFrame   => ShapeSurfaceFrameField;
    private Microsoft.UI.Xaml.Controls.Canvas ShapeSurface => ShapeSurfaceField;
    private TextBlock  SelectedVertexLabel => SelectedVertexLabelField;
    private TextBlock  XCoordLabel         => XCoordLabelField;
    private TextBox    XBox                => XBoxField;
    private TextBlock  YCoordLabel         => YCoordLabelField;
    private TextBox    YBox                => YBoxField;
    private StackPanel BezierHandlePanel   => BezierHandlePanelField;
    private TextBlock  BezierHandlesLabel  => BezierHandlesLabelField;
    private TextBlock  Cp1XCoordLabel      => Cp1XCoordLabelField;
    private TextBox    Cp1XBox             => Cp1XBoxField;
    private TextBlock  Cp1YCoordLabel      => Cp1YCoordLabelField;
    private TextBox    Cp1YBox             => Cp1YBoxField;
    private TextBlock  Cp2XCoordLabel      => Cp2XCoordLabelField;
    private TextBox    Cp2XBox             => Cp2XBoxField;
    private TextBlock  Cp2YCoordLabel      => Cp2YCoordLabelField;
    private TextBox    Cp2YBox             => Cp2YBoxField;
    private TextBlock  ActionsLabel        => ActionsLabelField;
    private Button     AddVertexButton     => AddVertexButtonField;
    private Button     DeleteVertexButton  => DeleteVertexButtonField;
    private Button     AnimateVertexButton => AnimateVertexButtonField;
    private TextBlock  HintText            => HintTextField;

    // Localizer routing for the strings the XAML had hardcoded.
    private string _defaultHint = "";
    private void ApplyLocalization()
    {
        ClosedToggle.Content        = Localizer.T("visualist.shape.closed_path",    "Closed path");
        AddVertexButton.Content     = Localizer.T("visualist.shape.add_vertex",     "Add Vertex");
        DeleteVertexButton.Content  = Localizer.T("visualist.shape.delete_vertex",  "Delete Vertex");
        AnimateVertexButton.Content = Localizer.T("visualist.shape.animate_vertex", "Animate Vertex");
        SelectedVertexLabel.Text    = Localizer.T("visualist.shape.section.selected", "SELECTED VERTEX");
        BezierHandlesLabel.Text     = Localizer.T("visualist.shape.section.handles",  "BEZIER HANDLES");
        ActionsLabel.Text           = Localizer.T("visualist.shape.section.actions",  "ACTIONS");
        _defaultHint                = Localizer.T("visualist.shape.hint",
            "Ctrl+Z / Ctrl+Y undo-redo · Del removes the selected vertex (min 2).");
        HintText.Text               = _defaultHint;
    }

    // Surface a guard rejection (vertex min/max) as an INLINE hint in the
    // dialog instead of only a System log line the author can't see. Non-modal
    // for this repeatable rejection; auto-reverts.
    private DispatcherTimer? _hintTimer;
    private void ShowGuardHint(string message)
    {
        if (HintText is null) return;
        HintText.Text       = message;
        HintText.Foreground = Token("ErrBrush", Color.FromArgb(0xFF, 0xC9, 0x53, 0x3C));
        _hintTimer ??= CreateHintTimer();
        _hintTimer.Stop();
        _hintTimer.Start();
    }
    private DispatcherTimer CreateHintTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (HintText is null) return;
            HintText.Text       = _defaultHint;
            HintText.Foreground = Token("CoalMutedTextBrush", Color.FromArgb(0xFF, 0x9E, 0x96, 0x8B));
        };
        return t;
    }

    // ── surface coord mapping ────────────────────────────────────────────

    private Point UnitToSurface(double ux, double uy)
    {
        double w = Math.Max(1.0, ShapeSurface.ActualWidth  - SurfacePad * 2);
        double h = Math.Max(1.0, ShapeSurface.ActualHeight - SurfacePad * 2);
        return new Point(SurfacePad + ux * w, SurfacePad + uy * h);
    }

    private (double ux, double uy) SurfaceToUnit(double sx, double sy)
    {
        double w = Math.Max(1.0, ShapeSurface.ActualWidth  - SurfacePad * 2);
        double h = Math.Max(1.0, ShapeSurface.ActualHeight - SurfacePad * 2);
        double ux = Math.Clamp((sx - SurfacePad) / w, 0, 1);
        double uy = Math.Clamp((sy - SurfacePad) / h, 0, 1);
        return (ux, uy);
    }

    // ── rendering ────────────────────────────────────────────────────────

    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e) => RenderSurface();

    // Drive the Canvas size from its (now-stretchy) frame so the
    // normalised-coord painting re-lays out when the dialog is resized. The
    // frame paints a 1px border, so the Canvas fills the interior.
    private void OnSurfaceFrameSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ShapeSurface is null) return;
        ShapeSurface.Width  = Math.Max(1.0, e.NewSize.Width  - 2);
        ShapeSurface.Height = Math.Max(1.0, e.NewSize.Height - 2);
        // The Canvas SizeChanged (OnSurfaceSizeChanged) repaints.
    }

    private void RenderSurface()
    {
        if (ShapeSurface is null) return;
        ShapeSurface.Children.Clear();

        double sw = ShapeSurface.ActualWidth;
        double sh = ShapeSurface.ActualHeight;
        if (sw <= 0 || sh <= 0) return;

        Brush borderBrush = Token("CoalCardBrush", Color.FromArgb(0xFF, 0x22, 0x1C, 0x16));
        Brush gridBrush   = Token("CoalMutedTextBrush", Color.FromArgb(0x40, 0x9E, 0x96, 0x8B), 0x40);
        Brush strokeBrush = Token("EmberPrimaryBrush", Color.FromArgb(0xFF, 0xE5, 0xA2, 0x4E));
        Brush fillBrush   = Token("EmberPrimaryBrush", Color.FromArgb(0x28, 0xE5, 0xA2, 0x4E), 0x28);
        Brush selBrush    = Token("Ember200Brush", Color.FromArgb(0xFF, 0xF2, 0xC7, 0x7F));
        Brush vertBrush   = Token("OkBrush", Color.FromArgb(0xFF, 0x6F, 0xA4, 0x6B));
        Brush vertEdge    = Token("CoalPaperBrush", Color.FromArgb(0xFF, 0xF5, 0xEF, 0xE3));

        // Bounding rectangle of the unit square.
        var tl = UnitToSurface(0, 0);
        var br = UnitToSurface(1, 1);
        ShapeSurface.Children.Add(new Rectangle
        {
            Width  = Math.Max(0, br.X - tl.X),
            Height = Math.Max(0, br.Y - tl.Y),
            Stroke = borderBrush,
            StrokeThickness = 1,
            IsHitTestVisible = false,
        }.At(tl.X, tl.Y));

        // Quarter grid lines.
        for (int i = 1; i < 4; i++)
        {
            var hA = UnitToSurface(i / 4.0, 0);
            var hB = UnitToSurface(i / 4.0, 1);
            ShapeSurface.Children.Add(new Line { X1 = hA.X, Y1 = hA.Y, X2 = hB.X, Y2 = hB.Y, Stroke = gridBrush, StrokeThickness = 0.5, IsHitTestVisible = false });
            var vA = UnitToSurface(0, i / 4.0);
            var vB = UnitToSurface(1, i / 4.0);
            ShapeSurface.Children.Add(new Line { X1 = vA.X, Y1 = vA.Y, X2 = vB.X, Y2 = vB.Y, Stroke = gridBrush, StrokeThickness = 0.5, IsHitTestVisible = false });
        }

        // The path preview (polyline or bezier), mirroring the kernel paint.
        if (_verts.Count >= 2)
        {
            var geom = BuildPathGeometry();
            ShapeSurface.Children.Add(new XamlPath
            {
                Data = geom,
                Stroke = strokeBrush,
                StrokeThickness = 2,
                Fill = fillBrush,
                IsHitTestVisible = false,
            });
        }

        // Bezier handles for the selected vertex (drawn under the dots).
        if (_isBezier && _selected >= 0 && _selected < _verts.Count)
        {
            var v = _verts[_selected];
            var anchor = UnitToSurface(v.X, v.Y);
            if (v.Cp1X.HasValue && v.Cp1Y.HasValue)
                DrawHandle(anchor, UnitToSurface(v.Cp1X.Value, v.Cp1Y.Value), Color.FromArgb(0xFF, 0x6F, 0xC8, 0x6B));
            if (v.Cp2X.HasValue && v.Cp2Y.HasValue)
                DrawHandle(anchor, UnitToSurface(v.Cp2X.Value, v.Cp2Y.Value), Color.FromArgb(0xFF, 0xF2, 0xC7, 0x7F));
        }

        // Vertex dots.
        for (int i = 0; i < _verts.Count; i++)
        {
            var p = UnitToSurface(_verts[i].X, _verts[i].Y);
            ShapeSurface.Children.Add(new Ellipse
            {
                Width  = VertexRadius * 2,
                Height = VertexRadius * 2,
                Fill   = i == _selected ? selBrush : vertBrush,
                Stroke = vertEdge,
                StrokeThickness = 1.5,
                IsHitTestVisible = false,
            }.At(p.X - VertexRadius, p.Y - VertexRadius));
        }

        VertexCountText.Text = FormatVertexCount(_verts.Count);
    }

    // The singular / plural split was an inline "vert" + "ex"/"ices" splice, which
    // has no translatable shape at all — a language whose plural is not a suffix
    // swap cannot be expressed by it. Two whole-sentence keys instead; the English
    // rendering is byte-identical to the splice.
    private static string FormatVertexCount(int count) => string.Format(
        count == 1
            ? Localizer.T("visualist.dialog.shape.vertex_count.one",   "{0} vertex")
            : Localizer.T("visualist.dialog.shape.vertex_count.other", "{0} vertices"),
        count);

    private Geometry BuildPathGeometry()
    {
        var figure = new PathFigure
        {
            StartPoint = UnitToSurface(_verts[0].X, _verts[0].Y),
            IsClosed   = _closed,
            IsFilled   = _closed,
        };

        if (_isBezier)
        {
            for (int i = 1; i < _verts.Count; i++)
                AddBezierSegment(figure, _verts[i - 1], _verts[i]);
            if (_closed && _verts.Count >= 2)
                AddBezierSegment(figure, _verts[_verts.Count - 1], _verts[0]);
        }
        else
        {
            for (int i = 1; i < _verts.Count; i++)
                figure.Segments.Add(new LineSegment { Point = UnitToSurface(_verts[i].X, _verts[i].Y) });
            // Closing edge is implied by IsClosed for the polyline case.
        }

        var geom = new PathGeometry();
        geom.Figures.Add(figure);
        return geom;
    }

    private void AddBezierSegment(PathFigure figure, ShapeData.Vertex a, ShapeData.Vertex b)
    {
        // Outgoing handle of A (cp2) → incoming handle of B (cp1), matching the
        // baseline ShapeCanvas.OnPaint contract.
        var c1 = UnitToSurface(a.Cp2X ?? a.X, a.Cp2Y ?? a.Y);
        var c2 = UnitToSurface(b.Cp1X ?? b.X, b.Cp1Y ?? b.Y);
        var end = UnitToSurface(b.X, b.Y);
        figure.Segments.Add(new BezierSegment { Point1 = c1, Point2 = c2, Point3 = end });
    }

    private void DrawHandle(Point anchor, Point handle, Color color)
    {
        ShapeSurface.Children.Add(new Line
        {
            X1 = anchor.X, Y1 = anchor.Y, X2 = handle.X, Y2 = handle.Y,
            Stroke = new SolidColorBrush(Color.FromArgb(0x80, color.R, color.G, color.B)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 2, 3 },
            IsHitTestVisible = false,
        });
        ShapeSurface.Children.Add(new Ellipse
        {
            Width  = HandleRadius * 2,
            Height = HandleRadius * 2,
            Fill   = new SolidColorBrush(color),
            Stroke = Token("CoalPaperBrush", Color.FromArgb(0xFF, 0xF5, 0xEF, 0xE3)),
            StrokeThickness = 1,
            IsHitTestVisible = false,
        }.At(handle.X - HandleRadius, handle.Y - HandleRadius));
    }

    // ── hit testing ──────────────────────────────────────────────────────

    private int HitVertex(double sx, double sy)
    {
        double r2 = (VertexRadius + HitSlop) * (VertexRadius + HitSlop);
        for (int i = _verts.Count - 1; i >= 0; i--)
        {
            var p = UnitToSurface(_verts[i].X, _verts[i].Y);
            double dx = sx - p.X, dy = sy - p.Y;
            if (dx * dx + dy * dy <= r2) return i;
        }
        return -1;
    }

    private string? HitHandle(double sx, double sy)
    {
        if (!_isBezier || _selected < 0 || _selected >= _verts.Count) return null;
        var v = _verts[_selected];
        double r2 = (HandleRadius + HitSlop) * (HandleRadius + HitSlop);
        if (v.Cp1X.HasValue && v.Cp1Y.HasValue)
        {
            var p = UnitToSurface(v.Cp1X.Value, v.Cp1Y.Value);
            double dx = sx - p.X, dy = sy - p.Y;
            if (dx * dx + dy * dy <= r2) return "cp1";
        }
        if (v.Cp2X.HasValue && v.Cp2Y.HasValue)
        {
            var p = UnitToSurface(v.Cp2X.Value, v.Cp2Y.Value);
            double dx = sx - p.X, dy = sy - p.Y;
            if (dx * dx + dy * dy <= r2) return "cp2";
        }
        return null;
    }

    // ── pointer ──────────────────────────────────────────────────────────

    private void OnSurfacePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pp = e.GetCurrentPoint(ShapeSurface);
        if (!pp.Properties.IsLeftButtonPressed) return;
        double sx = pp.Position.X, sy = pp.Position.Y;

        // Handle first — handles overlap the vertex hit zone on the selected vertex.
        var handle = HitHandle(sx, sy);
        if (handle is not null)
        {
            PushUndo();
            _dragHandle = handle;
            try { ShapeSurface.CapturePointer(e.Pointer); } catch { }
            e.Handled = true;
            return;
        }

        int hit = HitVertex(sx, sy);
        if (hit >= 0)
        {
            SetSelected(hit);
            PushUndo();
            _dragVertex = hit;
            try { ShapeSurface.CapturePointer(e.Pointer); } catch { }
            e.Handled = true;
            return;
        }

        // Empty space → append a vertex at the click position.
        var (ux, uy) = SurfaceToUnit(sx, sy);
        AppendVertexAt(ux, uy);
        e.Handled = true;
    }

    private void OnSurfacePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragVertex < 0 && _dragHandle is null) return;
        var pos = e.GetCurrentPoint(ShapeSurface).Position;
        var (ux, uy) = SurfaceToUnit(pos.X, pos.Y);
        if (_dragVertex >= 0)
        {
            MoveVertex(_dragVertex, ux, uy);
        }
        else if (_dragHandle is not null)
        {
            MoveHandle(_selected, _dragHandle, ux, uy);
        }
        e.Handled = true;
    }

    private void OnSurfacePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragVertex < 0 && _dragHandle is null) return;
        _dragVertex = -1;
        _dragHandle = null;
        try { ShapeSurface.ReleasePointerCapture(e.Pointer); } catch { }
        e.Handled = true;
    }

    private void OnSurfaceRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var pos = e.GetPosition(ShapeSurface);
        int hit = HitVertex(pos.X, pos.Y);
        if (hit < 0) return;
        SetSelected(hit);

        var flyout = new MenuFlyout();
        var anim = new MenuFlyoutItem { Text = Localizer.T("visualist.shape.menu.animate_vertex", "Animate this vertex") };
        anim.Click += (_, _) => RaiseAnimateVertex(hit);
        var del = new MenuFlyoutItem { Text = Localizer.T("visualist.shape.menu.delete_vertex", "Delete vertex") };
        del.Click += (_, _) => DeleteSelected();
        flyout.Items.Add(anim);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(del);
        flyout.ShowAt(ShapeSurface, pos);
        e.Handled = true;
    }

    // ── mutation ─────────────────────────────────────────────────────────

    private void SetSelected(int idx)
    {
        if (idx < 0 || idx >= _verts.Count) idx = -1;
        _selected = idx;
        SyncSelectedFields();
        RenderSurface();
    }

    private void MoveVertex(int idx, double x, double y)
    {
        if (idx < 0 || idx >= _verts.Count) return;
        _verts[idx].X = Math.Clamp(x, 0, 1);
        _verts[idx].Y = Math.Clamp(y, 0, 1);
        if (idx == _selected) SyncSelectedFields();
        RenderSurface();
    }

    private void MoveHandle(int idx, string handle, double ux, double uy)
    {
        if (!_isBezier || idx < 0 || idx >= _verts.Count) return;
        double cx = Math.Clamp(ux, 0, 1);
        double cy = Math.Clamp(uy, 0, 1);
        var v = _verts[idx];
        if (handle == "cp1") { v.Cp1X = cx; v.Cp1Y = cy; }
        else                 { v.Cp2X = cx; v.Cp2Y = cy; }
        if (idx == _selected) SyncSelectedFields();
        RenderSurface();
    }

    private void AppendVertexAt(double x, double y)
    {
        if (_verts.Count >= ShapeData.MaxVertices)
        {
            GlobalLogger.Log(
                $"Shape editor: vertex limit ({ShapeData.MaxVertices}) reached on '{_node.Title}' — add ignored.",
                "ShapeEditorDialog", LogLevel.System);
            ShowGuardHint(string.Format(Localizer.T("visualist.shape.guard.max",
                "Vertex limit reached ({0}) — can't add more."), ShapeData.MaxVertices));
            return;
        }
        PushUndo();
        var nv = _isBezier
            ? new ShapeData.Vertex { X = x, Y = y, Cp1X = x, Cp1Y = y, Cp2X = x, Cp2Y = y }
            : new ShapeData.Vertex { X = x, Y = y };
        _verts.Add(nv);
        _selected = _verts.Count - 1;
        SyncSelectedFields();
        RenderSurface();
    }

    private void DeleteSelected()
    {
        if (_selected < 0 || _selected >= _verts.Count) return;
        if (_verts.Count <= 2)
        {
            GlobalLogger.Log(
                $"Shape editor: minimum 2 vertices required on '{_node.Title}' — delete ignored.",
                "ShapeEditorDialog", LogLevel.System);
            ShowGuardHint(Localizer.T("visualist.shape.guard.min",
                "A shape needs at least 2 vertices — can't delete."));
            return;
        }
        PushUndo();
        _verts.RemoveAt(_selected);
        _selected = Math.Min(_selected, _verts.Count - 1);
        SyncSelectedFields();
        RenderSurface();
    }

    private void RaiseAnimateVertex(int idx)
    {
        if (idx < 0 || idx >= _verts.Count) return;
        var v = _verts[idx];
        OnRequestAnimateVertex?.Invoke(idx, v.X, v.Y, _isBezier);
    }

    // ── button / toggle handlers ──────────────────────────────────────────

    private void OnAddVertexClick(object sender, RoutedEventArgs e) => AppendVertexAt(0.5, 0.5);

    private void OnDeleteVertexClick(object sender, RoutedEventArgs e) => DeleteSelected();

    private void OnAnimateVertexClick(object sender, RoutedEventArgs e) => RaiseAnimateVertex(_selected);

    private void OnClosedToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        PushUndo();
        _closed = ClosedToggle.IsChecked == true;
        RenderSurface();
    }

    // ── numeric fields ─────────────────────────────────────────────────────

    private void SyncSelectedFields()
    {
        bool en = _selected >= 0 && _selected < _verts.Count;
        DeleteVertexButton.IsEnabled  = en && _verts.Count > 2;
        AnimateVertexButton.IsEnabled = en;

        _suppressEvents = true;
        try
        {
            if (!en)
            {
                foreach (var b in new[] { XBox, YBox, Cp1XBox, Cp1YBox, Cp2XBox, Cp2YBox })
                {
                    b.Text = "";
                    b.IsEnabled = false;
                }
                return;
            }
            var v = _verts[_selected];
            SetBox(XBox, v.X);
            SetBox(YBox, v.Y);
            if (_isBezier)
            {
                SetBox(Cp1XBox, v.Cp1X ?? v.X);
                SetBox(Cp1YBox, v.Cp1Y ?? v.Y);
                SetBox(Cp2XBox, v.Cp2X ?? v.X);
                SetBox(Cp2YBox, v.Cp2Y ?? v.Y);
            }
        }
        finally { _suppressEvents = false; }
    }

    private static void SetBox(TextBox box, double value)
    {
        box.Text = value.ToString("F3", CultureInfo.InvariantCulture);
        box.IsEnabled = true;
    }

    private void OnCoordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && sender is TextBox box)
        {
            CommitCoord(box);
            e.Handled = true;
        }
    }

    private void OnCoordLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) CommitCoord(box);
    }

    private void CommitCoord(TextBox box)
    {
        if (_suppressEvents) return;
        if (_selected < 0 || _selected >= _verts.Count) return;
        string axis = (box.Tag as string) ?? "";
        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
        {
            SyncSelectedFields(); // restore the previous display
            return;
        }
        val = Math.Clamp(val, 0, 1);
        PushUndo();
        var v = _verts[_selected];
        switch (axis)
        {
            case "x":    v.X    = val; break;
            case "y":    v.Y    = val; break;
            case "cp1x": v.Cp1X = val; break;
            case "cp1y": v.Cp1Y = val; break;
            case "cp2x": v.Cp2X = val; break;
            case "cp2y": v.Cp2Y = val; break;
        }
        RenderSurface();
    }

    // ── undo / redo ──────────────────────────────────────────────────────

    private void PushUndo()
    {
        _undo.Push((ShapeData.Serialize(_verts), _closed));
        _redo.Clear();
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        var snap = _undo.Pop();
        _redo.Push((ShapeData.Serialize(_verts), _closed));
        ApplySnapshot(snap);
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        var snap = _redo.Pop();
        _undo.Push((ShapeData.Serialize(_verts), _closed));
        ApplySnapshot(snap);
    }

    private void ApplySnapshot((string Json, bool Closed) snap)
    {
        _verts = ShapeData.Parse(snap.Json);
        _closed = snap.Closed;
        _suppressEvents = true;
        ClosedToggle.IsChecked = _closed;
        _suppressEvents = false;
        _selected = Math.Min(_selected, _verts.Count - 1);
        SyncSelectedFields();
        RenderSurface();
    }

    // ── dialog-level keys ──────────────────────────────────────────────────

    private void OnDialogKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = (InputKeyboardSourceState(VirtualKey.Control));
        if (ctrl && e.Key == VirtualKey.Z) { Undo(); e.Handled = true; return; }
        if (ctrl && e.Key == VirtualKey.Y) { Redo(); e.Handled = true; return; }
        // Del removes the selected vertex — but only when focus isn't inside a
        // numeric box (so the user can still delete a digit while typing).
        if (e.Key == VirtualKey.Delete && _selected >= 0 && FocusManager.GetFocusedElement(XamlRoot) is not TextBox)
        {
            DeleteSelected();
            e.Handled = true;
        }
    }

    private static bool InputKeyboardSourceState(VirtualKey key)
        => (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

    // ── OK / Cancel ──────────────────────────────────────────────────────

    private void OnOkClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        _node.Attributes["Vertices"] = ShapeData.Serialize(_verts);
        _node.Attributes["Closed"]   = _closed ? "true" : "false";
    }

    private void OnCancelClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Restore originals — defensive even though we only mutate on OK; the
        // caller may have inspected the live node mid-edit.
        _node.Attributes["Vertices"] = _origVerticesJson;
        _node.Attributes["Closed"]   = _origClosed;
    }

    // ── theme application (code-constructed library dialog) ──────────────────

    // Re-applies every brush / font that used to live as directly-resolved
    // resource markup on the root, the keyed styles, and the non-template
    // elements. Those resolve at InitializeComponent on a disconnected library
    // dialog and throw XamlParseException, so they're set here from the running
    // app's resources instead.
    private void ApplyDialogTheme()
    {
        // Root: was Background="{ThemeResource CoalShellBrush}" /
        //            BorderBrush="{ThemeResource CoalCardBrush}".
        if (DialogTheme.Brush("CoalShellBrush") is { } shell) Background = shell;
        if (DialogTheme.Brush("CoalCardBrush")  is { } card)  BorderBrush = card;

        // Eyebrow header (was ShapeDialogEyebrow style: DisplayFont / EmberPrimaryBrush).
        if (DialogTheme.Font("DisplayFont")        is { } disp) EyebrowText.FontFamily  = disp;
        if (DialogTheme.Brush("EmberPrimaryBrush") is { } ember) EyebrowText.Foreground = ember;

        // Subtitle (was inline SansFont / CoalSecondaryTextBrush).
        if (DialogTheme.Font("SansFont")                is { } sans) SubtitleText.FontFamily  = sans;
        if (DialogTheme.Brush("CoalSecondaryTextBrush") is { } sec)  SubtitleText.Foreground  = sec;

        // Hairline rule (was Fill="{ThemeResource BrassGradientBrush}").
        if (DialogTheme.Brush("BrassGradientBrush") is { } brass) HairlineRule.Fill = brass;

        // Closed-path toggle (was inline SansFont).
        if (DialogTheme.Font("SansFont") is { } sans2) ClosedToggle.FontFamily = sans2;

        // Vertex count (was inline MonoFont / CoalMutedTextBrush).
        if (DialogTheme.Font("MonoFont")            is { } mono) VertexCountText.FontFamily  = mono;
        if (DialogTheme.Brush("CoalMutedTextBrush") is { } muted) VertexCountText.Foreground = muted;

        // Surface frame (was Background CoalShellBrush / BorderBrush CoalCardBrush).
        if (DialogTheme.Brush("CoalShellBrush") is { } frameBg) ShapeSurfaceFrame.Background  = frameBg;
        if (DialogTheme.Brush("CoalCardBrush")  is { } frameBd) ShapeSurfaceFrame.BorderBrush = frameBd;

        // Vertex canvas (was Background="{ThemeResource CoalRaisedBrush}").
        if (DialogTheme.Brush("CoalRaisedBrush") is { } raised) ShapeSurface.Background = raised;

        // Section field-labels (was ShapeDialogFieldLabel style: SansFont / CoalSecondaryTextBrush).
        var fieldFont  = DialogTheme.Font("SansFont");
        var fieldBrush = DialogTheme.Brush("CoalSecondaryTextBrush");
        foreach (var lbl in new[] { SelectedVertexLabel, BezierHandlesLabel, ActionsLabel })
        {
            if (fieldFont  is { } ff) lbl.FontFamily = ff;
            if (fieldBrush is { } fb) lbl.Foreground = fb;
        }

        // Coord labels (was ShapeCoordLabel style: MonoFont / CoalMutedTextBrush).
        var coordLabelFont  = DialogTheme.Font("MonoFont");
        var coordLabelBrush = DialogTheme.Brush("CoalMutedTextBrush");
        foreach (var lbl in new[] { XCoordLabel, YCoordLabel, Cp1XCoordLabel, Cp1YCoordLabel, Cp2XCoordLabel, Cp2YCoordLabel })
        {
            if (coordLabelFont  is { } cf) lbl.FontFamily = cf;
            if (coordLabelBrush is { } cb) lbl.Foreground = cb;
        }

        // Coord boxes (was ShapeCoordBox style: MonoFont).
        if (DialogTheme.Font("MonoFont") is { } boxFont)
            foreach (var box in new[] { XBox, YBox, Cp1XBox, Cp1YBox, Cp2XBox, Cp2YBox })
                box.FontFamily = boxFont;

        // Delete button (was Foreground / BorderBrush ="{ThemeResource ErrBrush}").
        if (DialogTheme.Brush("ErrBrush") is { } err)
        {
            DeleteVertexButton.Foreground  = err;
            DeleteVertexButton.BorderBrush = err;
        }

        // Hint text (was ShapeDialogHint style: MonoFont / CoalMutedTextBrush).
        if (DialogTheme.Font("MonoFont")            is { } hintFont)  HintText.FontFamily  = hintFont;
        if (DialogTheme.Brush("CoalMutedTextBrush") is { } hintBrush) HintText.Foreground  = hintBrush;
    }

    // ── theme helpers ──────────────────────────────────────────────────────

    private static Brush Token(string key, Color fallback) => Token(key, fallback, 0xFF);

    private static Brush Token(string key, Color fallback, byte alpha)
    {
        try
        {
            if (Application.Current?.Resources is { } res
                && res.TryGetValue(key, out var found)
                && found is SolidColorBrush sb)
            {
                return alpha == 0xFF
                    ? sb
                    : new SolidColorBrush(Color.FromArgb(alpha, sb.Color.R, sb.Color.G, sb.Color.B));
            }
        }
        catch { /* designer / pre-app — fall through */ }
        return new SolidColorBrush(fallback);
    }
}

// Small fluent helper so Canvas.SetLeft/SetTop reads inline at the call site.
internal static class ShapeEditorCanvasExtensions
{
    public static T At<T>(this T element, double left, double top) where T : FrameworkElement
    {
        // Fully qualified: the sibling namespace Phoenix.Controls.Visualist.WinUI.Canvas
        // shadows the unqualified `Canvas`, so spell out the WinUI control here.
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(element, left);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(element, top);
        return element;
    }
}
