using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Visualist.Core;
using Windows.Foundation;
using Windows.UI;

// Disambiguate `Path` — Microsoft.UI.Xaml.Shapes.Path collides with
// System.IO.Path on this file because the project's GlobalUsings pull in
// System.IO. Alias the shape variant explicitly so the rest of the file
// can spell `XamlPath` without confusion.
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace Phoenix.Controls.Visualist.WinUI.Dialogs;

/// <summary>
/// B27 (audit/winui-regressions-2026-05-24) — curve editor reached from the
/// timeline scrubber's right-click "Edit Curve…" item on a keyframe marker.
///
/// On <see cref="ShowAsync"/>:
/// <list type="bullet">
/// <item>seeds the pill bar from <see cref="Keyframe.Curve"/></item>
/// <item>seeds the Bezier handle pair from
/// <see cref="Keyframe.BezierP1X"/>/<see cref="Keyframe.BezierP2X"/> etc.
/// when present, otherwise the engine's ease-like default (0.25, 0.1,
/// 0.25, 1.0)</item>
/// </list>
/// On "Save" (Primary), copies the picked curve + handles back to the
/// keyframe model. "Cancel" leaves the keyframe alone.
///
/// Per feedback_visualist_architect_chrome_independence.md the dialog stays
/// Visualist-local — no Shared lift.
/// </summary>
///
// [DIALOG-NO-XAML-FIX 2026-06-29] No .xaml / InitializeComponent — a
// code-constructed library ContentDialog throws XamlParseException at
// Application.LoadComponent when `new`'d detached (proven by the 1.0.6 runtime
// stack trace; resource stripping never helped because the throw is in the XAML
// parse itself). The whole visual tree is built in code here; the keyed Styles
// that lived in <ContentDialog.Resources> are applied as direct setters on each
// element, and the theme brushes/fonts are resolved at runtime via DialogTheme
// (Application.Current.Resources) — see NameTypeDialog / DialogTheme.cs for the
// full rationale. The default ContentDialog template still resolves at ShowAsync.
public sealed class CurveEditorDialog : ContentDialog
{
    private readonly Keyframe _keyframe;

    // Working state — committed to <see cref="_keyframe"/> only on Save.
    private KeyframeCurve _curve;
    private double _p1x, _p1y, _p2x, _p2y;

    // Bezier surface state.
    private const double HandleRadius = 7.0;     // px — visible
    private const double HandleHitRadius = 14.0; // px — hit-test slop
    private const double SurfacePad = 12.0;         // px inset for axis room
    private Ellipse?  _p1Handle;
    private Ellipse?  _p2Handle;
    private Line?     _p1Line;
    private Line?     _p2Line;
    private XamlPath? _curvePath;
    private int       _draggingHandle; // 0 none, 1 P1, 2 P2

    // Named elements that were x:Name'd in the old XAML — now plain fields built
    // in the ctor.
    private readonly TextBlock EyebrowText;
    private readonly TextBlock SubtitleText;
    private readonly Rectangle BrassHairline;
    private readonly TextBlock CurveTypeLabel;
    private readonly ToggleButton LinearPill;
    private readonly ToggleButton EaseInPill;
    private readonly ToggleButton EaseOutPill;
    private readonly ToggleButton EaseInOutPill;
    private readonly ToggleButton BezierPill;
    private readonly ToggleButton StepPill;
    private readonly TextBlock PreviewLabel;
    private readonly Border PreviewBorder;
    private readonly Grid BezierSurface;
    private readonly TextBlock BezierHandleReadout;
    private readonly TextBlock BezierHint;

    public CurveEditorDialog(Keyframe keyframe)
    {
        // Root properties that were attributes on <ContentDialog …>.
        Title = "Edit Curve";
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        PrimaryButtonText = "Save";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        // ── Visual tree (formerly the <Grid Width="460"> body) ────────────
        var root = new Grid { Width = 460 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Row 0 — eyebrow header + subtitle.
        var headerStack = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(headerStack, 0);

        // CurveDialogEyebrow style → FontSize 10, SemiBold, CharacterSpacing 180,
        // Margin 0,0,0,4 (applied directly per recipe §2d).
        EyebrowText = new TextBlock
        {
            Text = "VISUALIST · CURVE",
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 180,
            Margin = new Thickness(0, 0, 0, 4),
        };
        SubtitleText = new TextBlock
        {
            FontSize = 13,
            Text = "Pick an easing curve for this keyframe.",
        };
        headerStack.Children.Add(EyebrowText);
        headerStack.Children.Add(SubtitleText);
        root.Children.Add(headerStack);

        // Row 1 — brass hairline.
        BrassHairline = new Rectangle();
        Grid.SetRow(BrassHairline, 1);
        root.Children.Add(BrassHairline);

        // Row 2 — curve-type pills + preview.
        var bodyStack = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        Grid.SetRow(bodyStack, 2);

        // CurveDialogFieldLabel style → FontSize 11, SemiBold, CharacterSpacing 80,
        // Margin 0,8,0,4.
        CurveTypeLabel = MakeFieldLabel("CURVE TYPE");
        bodyStack.Children.Add(CurveTypeLabel);

        var pillRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        LinearPill    = MakePill("Linear");
        EaseInPill    = MakePill("Ease In");
        EaseOutPill   = MakePill("Ease Out");
        EaseInOutPill = MakePill("Ease In-Out");
        BezierPill    = MakePill("Bezier");
        StepPill      = MakePill("Step");
        pillRow.Children.Add(LinearPill);
        pillRow.Children.Add(EaseInPill);
        pillRow.Children.Add(EaseOutPill);
        pillRow.Children.Add(EaseInOutPill);
        pillRow.Children.Add(BezierPill);
        pillRow.Children.Add(StepPill);
        bodyStack.Children.Add(pillRow);

        PreviewLabel = MakeFieldLabel("PREVIEW");
        bodyStack.Children.Add(PreviewLabel);

        BezierSurface = new Grid
        {
            Width = 420,
            Height = 220,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        BezierSurface.SizeChanged    += OnBezierSurfaceSizeChanged;
        BezierSurface.PointerPressed  += OnBezierSurfacePointerPressed;
        BezierSurface.PointerMoved    += OnBezierSurfacePointerMoved;
        BezierSurface.PointerReleased += OnBezierSurfacePointerReleased;

        PreviewBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = BezierSurface,
        };
        bodyStack.Children.Add(PreviewBorder);

        // CurveDialogHint style → FontSize 10, Margin 0,4,0,0, TextWrapping Wrap.
        BezierHandleReadout = MakeHint("P1 (0.25, 0.10)  ·  P2 (0.25, 1.00)");
        bodyStack.Children.Add(BezierHandleReadout);

        // Hint carries an extra Margin="0,4,0,0" on the XAML element (on top of
        // the style's 0,4,0,0 — same value, so the override is a no-op visually).
        BezierHint = MakeHint(
            "Drag P1 and P2 inside the box. Non-Bezier curves use the preview only — the handles are inert.");
        BezierHint.Margin = new Thickness(0, 4, 0, 0);
        bodyStack.Children.Add(BezierHint);

        root.Children.Add(bodyStack);

        Content = root;

        // Code-constructed library dialog — theme applied in code via
        // DialogTheme; no directly-resolved resource markup. See Architect
        // NameTypeDialog / DialogTheme.cs.
        ApplyDialogTheme();
        _keyframe = keyframe ?? throw new ArgumentNullException(nameof(keyframe));

        _curve = _keyframe.Curve;
        // Seed from the keyframe's handles when present, otherwise the engine
        // default that <see cref="KeyframeInterpolation.ApplyCurve"/> falls back
        // to for Bezier without custom handles.
        _p1x = _keyframe.BezierP1X ?? 0.25;
        _p1y = _keyframe.BezierP1Y ?? 0.1;
        _p2x = _keyframe.BezierP2X ?? 0.25;
        _p2y = _keyframe.BezierP2Y ?? 1.0;

        PrimaryButtonClick += OnSaveClicked;

        // R49 — route the strings the WinUI port left hardcoded through the
        // Localizer (fallbacks preserve the current English text).
        ApplyLocalization();

        UpdatePillSelection();
    }

    // CurveDialogFieldLabel keyed style, applied directly (recipe §2d).
    private static TextBlock MakeFieldLabel(string text) => new()
    {
        Text = text,
        FontSize = 11,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        CharacterSpacing = 80,
        Margin = new Thickness(0, 8, 0, 4),
    };

    // CurveDialogHint keyed style, applied directly (recipe §2d).
    private static TextBlock MakeHint(string text) => new()
    {
        Text = text,
        FontSize = 10,
        Margin = new Thickness(0, 4, 0, 0),
        TextWrapping = TextWrapping.Wrap,
    };

    // CurvePillButtonStyle keyed style, applied directly (recipe §2d). The
    // Click handler matches the XAML's Click="OnPillClicked" wiring.
    private ToggleButton MakePill(string content)
    {
        var tb = new ToggleButton
        {
            Content = content,
            FontSize = 11,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 4, 0),
            MinHeight = 26,
            MinWidth = 60,
        };
        tb.Click += OnPillClicked;
        return tb;
    }

    // Re-applies every theme brush/font that was stripped from the XAML to keep
    // this code-constructed library dialog from throwing XamlParseException at
    // InitializeComponent. Resolved at runtime from Application.Current.Resources
    // via DialogTheme, which needs no visual-tree attachment.
    private void ApplyDialogTheme()
    {
        // Root chrome — was Background / BorderBrush on <ContentDialog>.
        if (DialogTheme.Brush("CoalShellBrush") is { } shell) Background  = shell;
        if (DialogTheme.Brush("CoalCardBrush")  is { } card)  BorderBrush = card;

        // Eyebrow (CurveDialogEyebrow style → FontFamily=DisplayFont, Foreground=EmberPrimaryBrush).
        if (DialogTheme.Font("DisplayFont")        is { } displayFont) EyebrowText.FontFamily  = displayFont;
        if (DialogTheme.Brush("EmberPrimaryBrush") is { } ember)       EyebrowText.Foreground  = ember;

        // Subtitle (was inline FontFamily=SansFont, Foreground=CoalSecondaryTextBrush).
        if (DialogTheme.Font("SansFont")                 is { } sansFont) SubtitleText.FontFamily = sansFont;
        if (DialogTheme.Brush("CoalSecondaryTextBrush")  is { } secText)  SubtitleText.Foreground = secText;

        // Brass hairline (was Fill=BrassGradientBrush on the Grid.Row=1 Rectangle).
        if (DialogTheme.Brush("BrassGradientBrush") is { } brass) BrassHairline.Fill = brass;

        // Field labels (CurveDialogFieldLabel style → FontFamily=SansFont, Foreground=CoalSecondaryTextBrush).
        var fieldFont  = DialogTheme.Font("SansFont");
        var fieldBrush = DialogTheme.Brush("CoalSecondaryTextBrush");
        foreach (var label in new[] { CurveTypeLabel, PreviewLabel })
        {
            if (fieldFont  is { } ff) label.FontFamily = ff;
            if (fieldBrush is { } fb) label.Foreground = fb;
        }

        // Hints (CurveDialogHint style → FontFamily=MonoFont, Foreground=CoalMutedTextBrush).
        var hintFont  = DialogTheme.Font("MonoFont");
        var hintBrush = DialogTheme.Brush("CoalMutedTextBrush");
        foreach (var hint in new[] { BezierHandleReadout, BezierHint })
        {
            if (hintFont  is { } hf) hint.FontFamily = hf;
            if (hintBrush is { } hb) hint.Foreground = hb;
        }

        // Pills (CurvePillButtonStyle style → FontFamily=MonoFont).
        if (DialogTheme.Font("MonoFont") is { } pillFont)
        {
            foreach (var pill in new[] { LinearPill, EaseInPill, EaseOutPill, EaseInOutPill, BezierPill, StepPill })
                pill.FontFamily = pillFont;
        }

        // Preview box (was Background=CoalShellBrush, BorderBrush=CoalCardBrush).
        if (DialogTheme.Brush("CoalShellBrush") is { } previewBg) PreviewBorder.Background  = previewBg;
        if (DialogTheme.Brush("CoalCardBrush")  is { } previewBd) PreviewBorder.BorderBrush = previewBd;
    }

    private void ApplyLocalization()
    {
        Title               = Localizer.T("visualist.curve.title",          "Edit Curve");
        SubtitleText.Text   = Localizer.T("visualist.curve.subtitle",       "Pick an easing curve for this keyframe.");
        CurveTypeLabel.Text = Localizer.T("visualist.curve.section.type",   "CURVE TYPE");
        PreviewLabel.Text   = Localizer.T("visualist.curve.section.preview","PREVIEW");
        LinearPill.Content    = Localizer.T("visualist.curve.linear",      "Linear");
        EaseInPill.Content    = Localizer.T("visualist.curve.ease_in",     "Ease In");
        EaseOutPill.Content   = Localizer.T("visualist.curve.ease_out",    "Ease Out");
        EaseInOutPill.Content = Localizer.T("visualist.curve.ease_in_out", "Ease In-Out");
        BezierPill.Content    = Localizer.T("visualist.curve.bezier",      "Bezier");
        StepPill.Content      = Localizer.T("visualist.curve.step",        "Step");
        BezierHint.Text       = Localizer.T("visualist.curve.hint",
            "Drag P1 and P2 inside the box. Non-Bezier curves use the preview only — the handles are inert.");
    }

    // ─── pill bar ──────────────────────────────────────────────────────

    private void OnPillClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb) return;
        KeyframeCurve picked = ReferenceEquals(tb, LinearPill)    ? KeyframeCurve.Linear
                             : ReferenceEquals(tb, EaseInPill)    ? KeyframeCurve.EaseIn
                             : ReferenceEquals(tb, EaseOutPill)   ? KeyframeCurve.EaseOut
                             : ReferenceEquals(tb, EaseInOutPill) ? KeyframeCurve.EaseInOut
                             : ReferenceEquals(tb, BezierPill)    ? KeyframeCurve.Bezier
                             : ReferenceEquals(tb, StepPill)      ? KeyframeCurve.Step
                             : _curve;
        _curve = picked;
        UpdatePillSelection();
        RenderBezierSurface();
    }

    private void UpdatePillSelection()
    {
        LinearPill   .IsChecked = _curve == KeyframeCurve.Linear;
        EaseInPill   .IsChecked = _curve == KeyframeCurve.EaseIn;
        EaseOutPill  .IsChecked = _curve == KeyframeCurve.EaseOut;
        EaseInOutPill.IsChecked = _curve == KeyframeCurve.EaseInOut;
        BezierPill   .IsChecked = _curve == KeyframeCurve.Bezier;
        StepPill     .IsChecked = _curve == KeyframeCurve.Step;
        // Inform the user when handles are inert — the preview still updates
        // so they can compare curves, but the drag handles are only writable
        // for Bezier. R59 (P3): spell out the asymmetric clamp so users know
        // why a handle dragged above/below the box "sticks" at the edge yet the
        // curve still bounces — X is constrained, Y may overshoot.
        BezierHint.Text = _curve == KeyframeCurve.Bezier
            ? Localizer.T("visualist.curve.hint.bezier",
                "Drag P1 and P2. X stays in [0, 1]; Y may exceed it for curves that overshoot (bounce / anticipation). The dot pins to the edge but the value is kept.")
            : Localizer.T("visualist.curve.hint.inert",
                "Drag handles only takes effect for Bezier. The preview still shows the picked curve.");
    }

    // ─── bezier surface ─────────────────────────────────────────────────
    //
    // Coordinate system: (0,0) is the curve START, (1,1) is the curve END.
    // The display maps logical (cx, cy) → surface (px, py) by:
    //   px = padding + cx * (W - 2*padding)
    //   py = padding + (1 - cy) * (H - 2*padding)
    // i.e. Y is inverted (curve "goes up" on the screen as cy increases).

    private void OnBezierSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
        => RenderBezierSurface();

    private void RenderBezierSurface()
    {
        BezierSurface.Children.Clear();
        _p1Handle = _p2Handle = null;
        _p1Line = _p2Line = null;
        _curvePath = null;

        double w = BezierSurface.ActualWidth;
        double h = BezierSurface.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Axis frame.
        var frame = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Stroke = ResolveBrush("CoalCardBrush"),
            StrokeThickness = 1,
            Margin = new Thickness(SurfacePad, SurfacePad, SurfacePad, SurfacePad),
            Fill   = ResolveBrush("CoalRaisedBrush"),
        };
        BezierSurface.Children.Add(frame);

        // Diagonal reference (Linear).
        var diag = new Line
        {
            Stroke = ResolveBrush("CoalMutedTextBrush"),
            StrokeThickness = 0.5,
            StrokeDashArray = new DoubleCollection { 2, 3 },
            X1 = LogicalToSurfaceX(0, w), Y1 = LogicalToSurfaceY(0, h),
            X2 = LogicalToSurfaceX(1, w), Y2 = LogicalToSurfaceY(1, h),
            IsHitTestVisible = false,
        };
        BezierSurface.Children.Add(diag);

        // Build the easing-curve preview path using the engine's CubicBezier
        // sampler for Bezier, otherwise <see cref="KeyframeInterpolation.ApplyCurve"/>
        // for the picked easing. 64 segments — smooth enough at 420px wide.
        var figure = new PathFigure
        {
            StartPoint = new Point(LogicalToSurfaceX(0, w), LogicalToSurfaceY(0, h)),
            IsClosed   = false,
        };
        const int segments = 64;
        for (int i = 1; i <= segments; i++)
        {
            double t  = (double)i / segments;
            double y  = EvalCurve(t);
            figure.Segments.Add(new LineSegment
            {
                Point = new Point(LogicalToSurfaceX(t, w), LogicalToSurfaceY(y, h)),
            });
        }
        var geom = new PathGeometry();
        geom.Figures.Add(figure);
        _curvePath = new XamlPath
        {
            Data            = geom,
            Stroke          = ResolveBrush("EmberPrimaryBrush"),
            StrokeThickness = 2,
            IsHitTestVisible = false,
        };
        BezierSurface.Children.Add(_curvePath);

        // Handle visuals only meaningful for Bezier. For non-Bezier curves
        // we render them muted so the user sees that they're inert. The
        // pointer-press path checks _curve == Bezier before initiating a drag,
        // so a stray click on a muted handle has no effect.
        bool isBez = _curve == KeyframeCurve.Bezier;
        Brush handleStroke = ResolveBrush(isBez ? "Ember200Brush" : "CoalMutedTextBrush");
        Brush handleFill   = isBez
            ? ResolveBrush("EmberPrimaryBrush")
            : new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));

        // Tangent lines from anchor to handle. p1 attaches to (0,0); p2 to (1,1).
        // The handle-side Y is visually clamped to the box (see
        // LogicalToSurfaceYClamped) so an overshooting handle's dot/line stays
        // boxed; the underlying _p1y/_p2y stay honest for Save + the readout.
        _p1Line = new Line
        {
            Stroke = handleStroke,
            StrokeThickness = 1,
            X1 = LogicalToSurfaceX(0, w), Y1 = LogicalToSurfaceY(0, h),
            X2 = LogicalToSurfaceX(_p1x, w), Y2 = LogicalToSurfaceYClamped(_p1y, h),
            IsHitTestVisible = false,
        };
        _p2Line = new Line
        {
            Stroke = handleStroke,
            StrokeThickness = 1,
            X1 = LogicalToSurfaceX(1, w), Y1 = LogicalToSurfaceY(1, h),
            X2 = LogicalToSurfaceX(_p2x, w), Y2 = LogicalToSurfaceYClamped(_p2y, h),
            IsHitTestVisible = false,
        };
        BezierSurface.Children.Add(_p1Line);
        BezierSurface.Children.Add(_p2Line);

        _p1Handle = MakeHandle(_p1x, _p1y, w, h, handleStroke, handleFill);
        _p2Handle = MakeHandle(_p2x, _p2y, w, h, handleStroke, handleFill);
        BezierSurface.Children.Add(_p1Handle);
        BezierSurface.Children.Add(_p2Handle);

        BezierHandleReadout.Text =
            $"P1 ({_p1x:0.00}, {_p1y:0.00})  ·  P2 ({_p2x:0.00}, {_p2y:0.00})";
    }

    private static Ellipse MakeHandle(double cx, double cy, double w, double h, Brush stroke, Brush fill)
    {
        // Y is visually clamped so an overshoot handle's dot stays boxed; the
        // logical cy (_p1y/_p2y) is honest and reflected in the readout. X is
        // already constrained to [0,1] by the pointer-move clamp.
        double px = LogicalToSurfaceX(cx, w);
        double py = LogicalToSurfaceYClamped(cy, h);
        return new Ellipse
        {
            Width  = HandleRadius * 2,
            Height = HandleRadius * 2,
            Stroke = stroke,
            StrokeThickness = 1.5,
            Fill   = fill,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Top,
            Margin = new Thickness(px - HandleRadius, py - HandleRadius, 0, 0),
            IsHitTestVisible = true,
        };
    }

    // Defensive theme-brush lookup. The bare `(Brush)Resources[key]` casts
    // this replaced threw KeyNotFoundException / InvalidCastException on the UI
    // thread if a token was missing or not a Brush — and RenderBezierSurface()
    // runs from the SizeChanged handler, so a single bad key crashed the whole
    // dialog. Mirrors the ResolveBrush pattern in MainView.xaml.cs.
    private static Brush ResolveBrush(string key)
    {
        var res = Application.Current?.Resources;
        if (res is not null && res.TryGetValue(key, out var v) && v is Brush b)
            return b;
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    private double EvalCurve(double t)
    {
        // Mirror the engine's evaluator so the preview matches what the
        // browser-side compositor.js will produce. For Bezier we walk the
        // cubic-bezier evaluator with the live handle values; for non-Bezier
        // we route through KeyframeInterpolation.ApplyCurve so any future
        // curve-easing tweak in the engine ripples here automatically.
        if (_curve == KeyframeCurve.Bezier)
            return KeyframeInterpolation.CubicBezier(t, _p1x, _p1y, _p2x, _p2y);
        return KeyframeInterpolation.ApplyCurve(t, _curve);
    }

    private static double LogicalToSurfaceX(double cx, double w)
        => SurfacePad + cx * Math.Max(1.0, w - 2 * SurfacePad);

    private static double LogicalToSurfaceY(double cy, double h)
        => SurfacePad + (1 - cy) * Math.Max(1.0, h - 2 * SurfacePad);

    // Visual-only Y mapping that keeps the rendered point inside the [0,1] box.
    // The logical handle value (_p1y/_p2y) is left UNCLAMPED so overshoot
    // curves (bounce / anticipation) keep their honest value and are written
    // back on Save; only the on-screen handle dot + tangent endpoint are
    // boxed so they don't escape the preview frame. The readout shows the true
    // (possibly out-of-range) value. This is what the lines 336-339 comment
    // always claimed but the prior code never actually did (no render clamp).
    private static double LogicalToSurfaceYClamped(double cy, double h)
        => LogicalToSurfaceY(Math.Clamp(cy, 0, 1), h);

    private static double SurfaceToLogicalX(double px, double w)
        => (px - SurfacePad) / Math.Max(1.0, w - 2 * SurfacePad);

    private static double SurfaceToLogicalY(double py, double h)
        => 1 - (py - SurfacePad) / Math.Max(1.0, h - 2 * SurfacePad);

    // ─── pointer ────────────────────────────────────────────────────────

    private void OnBezierSurfacePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_curve != KeyframeCurve.Bezier) return;
        var pp = e.GetCurrentPoint(BezierSurface).Position;
        double w = BezierSurface.ActualWidth;
        double h = BezierSurface.ActualHeight;
        // Hit-test against the CLAMPED (rendered) handle position so a handle
        // pushed to the box edge by Y overshoot can still be grabbed where it
        // is actually drawn.
        double p1Px = LogicalToSurfaceX(_p1x, w), p1Py = LogicalToSurfaceYClamped(_p1y, h);
        double p2Px = LogicalToSurfaceX(_p2x, w), p2Py = LogicalToSurfaceYClamped(_p2y, h);
        double d1 = Distance(pp.X, pp.Y, p1Px, p1Py);
        double d2 = Distance(pp.X, pp.Y, p2Px, p2Py);
        if (d1 <= HandleHitRadius && d1 <= d2)
        {
            _draggingHandle = 1;
        }
        else if (d2 <= HandleHitRadius)
        {
            _draggingHandle = 2;
        }
        else
        {
            return;
        }
        try { BezierSurface.CapturePointer(e.Pointer); } catch { /* best-effort */ }
        e.Handled = true;
    }

    private void OnBezierSurfacePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_draggingHandle == 0) return;
        var pp = e.GetCurrentPoint(BezierSurface).Position;
        double w = BezierSurface.ActualWidth;
        double h = BezierSurface.ActualHeight;
        double cx = Math.Clamp(SurfaceToLogicalX(pp.X, w), 0, 1);
        // X is clamped to [0,1] (a cubic-bezier easing must be a function of t).
        // Y is left unclamped so Bezier handles can overshoot vertically (e.g.
        // anticipation curves dip below 0 or bounce above 1). The visual handle
        // dot + tangent line ARE boxed on render (LogicalToSurfaceYClamped), so
        // the dot never escapes the frame, but the stored value + readout stay
        // honest.
        double cy = SurfaceToLogicalY(pp.Y, h);
        if (_draggingHandle == 1) { _p1x = cx; _p1y = cy; }
        else                       { _p2x = cx; _p2y = cy; }
        RenderBezierSurface();
        e.Handled = true;
    }

    private void OnBezierSurfacePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_draggingHandle == 0) return;
        try { BezierSurface.ReleasePointerCapture(e.Pointer); } catch { /* best-effort */ }
        _draggingHandle = 0;
        e.Handled = true;
    }

    private static double Distance(double ax, double ay, double bx, double by)
        => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

    // ─── commit ─────────────────────────────────────────────────────────

    private void OnSaveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        _keyframe.Curve = _curve;
        if (_curve == KeyframeCurve.Bezier)
        {
            _keyframe.BezierP1X = _p1x;
            _keyframe.BezierP1Y = _p1y;
            _keyframe.BezierP2X = _p2x;
            _keyframe.BezierP2Y = _p2y;
        }
        else
        {
            // Strip the bezier handles for non-bezier curves so the JSON
            // round-trip stays clean (JsonIgnoreCondition.WhenWritingNull on
            // the model drops the fields).
            _keyframe.BezierP1X = _keyframe.BezierP1Y = null;
            _keyframe.BezierP2X = _keyframe.BezierP2Y = null;
        }
    }
}
