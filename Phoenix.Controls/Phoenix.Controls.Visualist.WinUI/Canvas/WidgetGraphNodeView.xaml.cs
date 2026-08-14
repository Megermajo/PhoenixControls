using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Visualist.Core;
using Windows.Foundation;
using Windows.UI;

namespace Phoenix.Controls.Visualist.WinUI.Canvas;

// Single-node renderer — UE-Blueprints style title strip + per-side socket
// pin column. The graph canvas (WidgetGraphCanvas) hosts one of these per
// Node at the node's Location, hooks pointer events for drag, and
// invalidates back through the VisualistViewModel's PushUndo/MarkDirty
// surface on commit.
//
// Live previews: the third grid row hosts a ThumbnailHost
// UserControl (Controls/ThumbnailHost.xaml). The canvas-side hook
// (WidgetGraphCanvas.RefreshPreviews) computes per-node snapshots via
// NodeEvaluator.EvaluatePreviews and pushes them in via SetPreview here.
// All image / colour / placeholder / error chrome lives inside
// ThumbnailHost; this view only decides whether the wrapping row is
// visible (collapsed when the template didn't opt into a preview).
public sealed partial class WidgetGraphNodeView : UserControl
{
    public Node Node { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            ApplySelectionVisual();
        }
    }
    private bool _isSelected;

    // Sprint-B seam: the canvas resolves pin world-coords by looking each
    // socket Id up here, then asking the resulting pin element for its
    // TransformToVisual(WorldCanvas). The dictionary is rebuilt on every
    // Render so socket add/remove (sprint C+) shows up cleanly.
    private readonly Dictionary<string, FrameworkElement> _pinElements =
        new(System.StringComparer.Ordinal);

    public IReadOnlyDictionary<string, FrameworkElement> PinElements => _pinElements;

    public WidgetGraphNodeView(Node node)
    {
        Node = node;
        InitializeComponent();
        Render();
        // Wire the initial preview to the freshly-mounted host. The canvas
        // also calls RefreshPreviews() after Rebuild(), but Loaded fires
        // independently of canvas-driven mutations (e.g. when a node view
        // is re-created mid-session) so we cover both paths.
        Loaded += OnLoaded;
        // V13 trace flash — the bus subscription is paired to the visual-tree
        // lifetime, NOT the constructor, so a view the canvas discards on Rebuild()
        // stops holding the bus singleton. See HookTraceFlash.
        Unloaded += OnUnloaded;
    }

    /// <summary>Re-runs full layout against the current Node — call after
    /// the underlying Node is mutated (rename, socket add/remove).</summary>
    public void Refresh() => Render();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Initial preview pass — uses whatever snapshot the canvas
        // already supplied via SetPreview (cached in _lastSnapshot). On a
        // brand-new node where SetPreview hasn't been called yet the
        // strip stays collapsed, which matches "no preview yet" semantics.
        if (_lastSnapshot is not null)
        {
            PreviewHost.SetSnapshot(_lastSnapshot);
        }
        HookTraceFlash();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => UnhookTraceFlash();

    private void Render()
    {
        TitleText.Text    = string.IsNullOrEmpty(Node.Title)
            ? Localizer.T("visualist.widget.node.untitled", "(untitled)")
            : Node.Title;
        CategoryText.Text = Node.Category ?? "";
        ApplyHeaderColor();

        InputPinStack.Children.Clear();
        OutputPinStack.Children.Clear();
        _pinElements.Clear();

        if (Node.Sockets is { } sockets)
        {
            foreach (var socket in sockets.Where(s => s.Type == SocketType.Input))
                InputPinStack.Children.Add(BuildPinRow(socket, isInput: true));

            foreach (var socket in sockets.Where(s => s.Type == SocketType.Output))
                OutputPinStack.Children.Add(BuildPinRow(socket, isInput: false));
        }

        // Attribute-only params get an inline TEXT
        // pill even when no input socket matches them. Constant nodes
        // (Scalar/Color/Vector*.Constant, Vector.Rect4) author their values as
        // attributes with NO input sockets, so the input-pin path above never
        // renders a pill for them; this adds one per real attribute key. The
        // rich controls (sliders / pickers) live ONLY in the Inspector — pills
        // stay text entry here. Renders into the left body column under the
        // (typically empty) input-pin stack.
        BuildAttributeOnlyPills();

        ApplySelectionVisual();
    }

    // ─── Attribute-only inline value pills ──────────────────────
    //
    // Companion-meta suffixes per the NodeTemplates convention: "<Key>__Range"
    // (slider band hint) and "<Key>__KnownValues" (enum CSV). These are meta,
    // never editable params — skip them (the Inspector consumes them to shape
    // its controls; the node body never shows them).
    private const string RangeSuffix       = "__Range";
    private const string KnownValuesSuffix = "__KnownValues";

    // Pure runtime metadata with no "__" marker — see the matching arm in
    // VisualistViewModel.IsCompanionKey. Without it the template back-fill's copy of
    // this key renders a bogus pill on every Display node's body.
    private const string AutoInjectedKey   = "IsAutoInjected";

    private static bool IsCompanionMetaKey(string key)
        => key.EndsWith(RangeSuffix, StringComparison.Ordinal)
        || key.EndsWith(KnownValuesSuffix, StringComparison.Ordinal)
        || string.Equals(key, AutoInjectedKey, StringComparison.Ordinal);

    /// <summary>
    /// True when an INPUT socket already covers <paramref name="attrKey"/> — that
    /// param's pill is rendered by the input-pin path (BuildPinRow → TryGetPillForSocket)
    /// so the attribute-only path must skip it to avoid a duplicate pill. Output
    /// sockets don't carry editable pills, so they don't count as coverage.
    /// Case-insensitive, matching ResolveAttrKey / TryGetPillForSocket.
    /// </summary>
    private bool HasMatchingInputSocket(string attrKey)
    {
        if (Node.Sockets is not { } sockets) return false;
        foreach (var s in sockets)
        {
            if (s.Type != SocketType.Input) continue;
            if (string.Equals(s.Name, attrKey, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private void BuildAttributeOnlyPills()
    {
        if (Node.Attributes is null || Node.Attributes.Count == 0) return;

        // How many String.Select rows this BODY shows (int.MaxValue = no capping, i.e. every
        // other node). See visibleStringSelectRows for the rule.
        int rowCap = VisibleStringSelectRows();

        foreach (var kv in Node.Attributes)
        {
            string key = kv.Key;
            if (string.IsNullOrEmpty(key)) continue;
            // Skip companion-meta keys (__Range / __KnownValues) — Inspector-only.
            if (IsCompanionMetaKey(key)) continue;
            // Trailing unconfigured String.Select rows are suppressed on the body.
            if (IsSuppressedStringSelectRow(key, rowCap)) continue;
            // Skip params already pilled via their matching input socket. (A Flow
            // input socket would carry the key too, but Flow sockets never pill —
            // TryGetPillForSocket bails on Flow — so an attribute keyed to a Flow
            // input would still be skipped here by the name match, which is the
            // intended behaviour: Flow params are never editable as text.)
            if (HasMatchingInputSocket(key)) continue;

            InputPinStack.Children.Add(BuildAttributePillRow(key, kv.Value ?? string.Empty));
        }
    }

    // ─── String.Select body-row capping ─────────────────────────────
    //
    // String.Select ships StringSelectRows (12) Case/Value attribute PAIRS plus When and
    // Default, and the attribute-only pill path renders one row per key: 26 pill rows, about
    // 600 px of node body, uncapped and inside a canvas with no scroll. In the ordinary
    // three-kind mapping 20 of those rows are blank — a node body six times taller than the
    // graph it sits in, mostly empty.
    //
    // The template row count is NOT reduced (changing it once graphs exist would strand rows
    // an author has already filled in — the unsafe option). Instead the BODY hides TRAILING
    // all-blank pairs and keeps exactly one blank pair as the add-the-next-row affordance, so
    // a fresh node shows Case1/Value1 and a three-kind node shows rows 1..4. Every row stays
    // reachable: the Inspector builds its own rows from Node.Attributes and is untouched, and
    // filling row N here immediately reveals row N+1 on the next Rebuild.
    //
    // Deliberately NOT generalised to "hide any blank attribute": a blank Text.Render Content
    // or a blank PreviewText must stay visible, because its pill is the only place to type
    // one. This applies to a node whose rows are a numbered SEQUENCE, where "the next empty
    // one" is unambiguous.

    private const string StringSelectTitle = "String.Select";

    private int VisibleStringSelectRows() => VisibleStringSelectRows(Node.Title, Node.Attributes);

    /// <summary>
    /// Number of <c>Case&lt;i&gt;</c>/<c>Value&lt;i&gt;</c> pairs to render on the body:
    /// the highest configured row plus one blank, clamped to the template's row count.
    /// <see cref="int.MaxValue"/> for every node that is not a String.Select, so the caller's
    /// per-key test is a no-op there.
    ///
    /// Static + attribute-dictionary-driven so <c>DynamicMediaSourceV7Tests</c> can pin the
    /// rule: a WinUI <c>UserControl</c> cannot be constructed on a headless test host, and an
    /// instance-only helper would have been untestable.
    /// </summary>
    internal static int VisibleStringSelectRows(string? title, IReadOnlyDictionary<string, string>? attrs)
    {
        if (!string.Equals(title, StringSelectTitle, StringComparison.OrdinalIgnoreCase))
            return int.MaxValue;
        if (attrs is null) return int.MaxValue;

        int highestUsed = 0;
        for (int row = 1; row <= NodeTemplates.StringSelectRows; row++)
        {
            if (HasRowText(attrs, $"Case{row}") || HasRowText(attrs, $"Value{row}")) highestUsed = row;
        }
        // +1 = the add-next affordance; the clamp keeps a fully-populated node at 12.
        int visible = highestUsed + 1;
        return visible > NodeTemplates.StringSelectRows ? NodeTemplates.StringSelectRows : visible;
    }

    /// True when the attribute holds a non-empty value. Values are stored JSON-quoted by the
    /// Inspector ("" for empty), so the quotes come off before the emptiness test — without
    /// that, every row reads as configured and nothing is ever suppressed.
    private static bool HasRowText(IReadOnlyDictionary<string, string> attrs, string key)
        => attrs.TryGetValue(key, out var raw)
        && (raw ?? string.Empty).Trim().Trim('"').Length > 0;

    /// True for a <c>Case&lt;i&gt;</c>/<c>Value&lt;i&gt;</c> key past the visible row count.
    /// Any other key — When, Default, or a row index that fails to parse — is never
    /// suppressed: an unparsable key is author/hand-edit data and hiding it would make it
    /// uneditable.
    internal static bool IsSuppressedStringSelectRow(string key, int rowCap)
    {
        if (rowCap == int.MaxValue) return false;
        string digits;
        if (key.StartsWith("Case", StringComparison.Ordinal))       digits = key.Substring(4);
        else if (key.StartsWith("Value", StringComparison.Ordinal)) digits = key.Substring(5);
        else return false;
        if (!int.TryParse(digits, System.Globalization.NumberStyles.None,
                          System.Globalization.CultureInfo.InvariantCulture, out int row)) return false;
        return row > rowCap;
    }

    /// <summary>
    /// A labeled inline TEXT pill for an attribute key that has no matching input
    /// socket. Reuses <see cref="BuildValuePill"/> (and therefore the existing
    /// commit → PushUndo → MarkDirty → Rebuild path via <see cref="_onAttrCommit"/>)
    /// by handing it a synthetic render-only Socket carrying the attribute key as
    /// its Name. The synthetic socket is NEVER added to <c>Node.Sockets</c> — it
    /// exists only so the pill builder can resolve the key, tooltip, and value.
    /// Its <c>Type</c> is Output so the R26 arm/seek cluster (input-scalar only)
    /// never attaches: attribute-only constant pills stay pure text entry, with
    /// keyframing owned by the Inspector.
    /// </summary>
    private FrameworkElement BuildAttributePillRow(string attrKey, string rawValue)
    {
        // Synthetic socket — render-only, not persisted. DataType is inferred so
        // the palette/tooltip read sensibly; Type=Output keeps the pill text-only.
        var synthetic = new Socket
        {
            Name     = attrKey,
            Type     = SocketType.Output,
            DataType = InferAttributeDataType(attrKey, rawValue),
        };

        var label = new TextBlock
        {
            Text       = attrKey,
            FontFamily = SansFontFamily(), // attribute label = chrome text, Sans (matches socket labels)
            FontSize   = 12,
            Foreground = (Brush)Application.Current.Resources["CoalSecondaryTextBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin     = new Thickness(6, 0, 0, 0),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        row.Children.Add(label);
        row.Children.Add(BuildValuePill(synthetic, rawValue));
        return row;
    }

    // Best-effort DataType inference for an attribute-only param, mirroring the
    // Inspector's build-logic special-cases (Path → media; #hex → Color;
    // true/false → Bool; numeric → Scalar; else String). Used only to colour the
    // synthetic socket's tooltip context — the pill renders identically regardless.
    private static SocketDataType InferAttributeDataType(string key, string value)
    {
        if (string.Equals(key, "Path", StringComparison.OrdinalIgnoreCase))
            return SocketDataType.String;

        string trimmed = (value ?? string.Empty).Trim().Trim('"');
        if (trimmed.StartsWith("#", StringComparison.Ordinal)
            && (trimmed.Length == 7 || trimmed.Length == 9 || trimmed.Length == 4 || trimmed.Length == 5))
            return SocketDataType.Color;
        if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase))
            return SocketDataType.Bool;
        if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out _))
            return SocketDataType.Scalar;
        return SocketDataType.String;
    }

    private void ApplySelectionVisual()
    {
        // Selection paints a dedicated 2px gold ring (§2 SelectionBrush #FFD700,
        // wired in XAML) rather than thickening NodeChrome's border. Mirrors
        // Architect NodeView's AccentRing: the body border stays a constant 1px
        // CoalDividerBrush and only this overlay toggles, so selecting a node
        // never re-measures the body and the gold ring reads clearly at zoom.
        // Gold is the canonical selection colour per Design_Orders §2
        // (yellow=editable, green=connected, red=error, gold=selection).
        AccentRing.Visibility = _isSelected ? Visibility.Visible : Visibility.Collapsed;
    }

    // Per-node header colour. The pre-WinUI baseline coloured node header
    // strips by category/role (the Display + Audio sinks ship green / purple
    // HeaderColor); the WinUI rework flattened every header to one brass
    // EmberShadow band, erasing the distinction AND painting off-brand ember on
    // every node. Every header now paints an ENGRAVED vertical gradient
    // (top lightened ~5% / bottom darkened ~25%, Design_Orders §4.3) — a subtle
    // per-category tint when Node.HeaderColor is assigned, else a neutral warm-
    // coal band. Mirrors Architect's HeaderGradientBrush idiom, duplicated
    // locally per the per-pillar paint rule (never reference Architect types).
    private void ApplyHeaderColor()
    {
        var hc = Node.HeaderColor;
        if (hc.A != 0)
            TitleBar.Background = MakeVerticalGradient(
                ControlPaintLight(hc, 0.05),
                ControlPaintDark(hc, 0.25));
        else
            // Neutral engraved header (no category colour) — warm-coal graphite
            // gradient, lighter top / darker bottom, matching the suite's
            // PanelHeaderGradientBrush cadence. NOT the reserved ember band.
            TitleBar.Background = MakeVerticalGradient(
                Color.FromArgb(0xFF, 0x33, 0x2B, 0x22),
                Color.FromArgb(0xFF, 0x1E, 0x19, 0x15));
    }

    // ─── Engraved-header gradient helpers ───────────────────────────
    //
    // Duplicated from Architect's NodeViewModel.HeaderGradientBrush technique
    // (ControlPaint.Light/Dark fade → vertical LinearGradientBrush) per the
    // per-pillar paint rule — the idiom is copied, not lifted, and no Architect
    // type is referenced. Node.HeaderColor is a System.Drawing.Color (shared
    // model), so the fade helpers take that and return Windows.UI.Color.
    private static LinearGradientBrush MakeVerticalGradient(Color top, Color bottom)
    {
        var gb = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint   = new Point(0, 1),
        };
        gb.GradientStops.Add(new GradientStop { Color = top,    Offset = 0.0 });
        gb.GradientStops.Add(new GradientStop { Color = bottom, Offset = 1.0 });
        return gb;
    }

    // Mirror of System.Windows.Forms.ControlPaint.Light — blend toward white.
    private static Color ControlPaintLight(System.Drawing.Color c, double factor)
    {
        byte r = (byte)Math.Min(255.0, c.R + (255 - c.R) * factor);
        byte g = (byte)Math.Min(255.0, c.G + (255 - c.G) * factor);
        byte b = (byte)Math.Min(255.0, c.B + (255 - c.B) * factor);
        return Color.FromArgb(0xFF, r, g, b);
    }

    // Mirror of System.Windows.Forms.ControlPaint.Dark — blend toward black.
    private static Color ControlPaintDark(System.Drawing.Color c, double factor)
    {
        byte r = (byte)Math.Max(0.0, c.R * (1 - factor));
        byte g = (byte)Math.Max(0.0, c.G * (1 - factor));
        byte b = (byte)Math.Max(0.0, c.B * (1 - factor));
        return Color.FromArgb(0xFF, r, g, b);
    }

    // Resolve a theme brush by resource key without a hard cast — a missing,
    // null, or non-Brush resource (e.g. an unloaded merged dictionary) would
    // otherwise throw InvalidCastException on every selection toggle. Falls
    // back to a solid brush so the node still renders chrome.
    private static Brush ResolveBrush(string key, Color fallback)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var value) is true
            && value is Brush brush)
        {
            return brush;
        }
        return new SolidColorBrush(fallback);
    }

    // Pin glyph dispatched per SocketDataType via
    // WidgetSocketPalette + WidgetPinPathGeometry, mirroring Architect's
    // pin renderer per Majo's published legend. The pin is built as a
    // Microsoft.UI.Xaml.Shapes.Path inside a 14×14 hit-target Grid so
    // (a) the pin glyph reads at canvas zoom and (b) the wire-drop
    // hit-test surface stays at the previous 14×14 footprint regardless
    // of the actual visible shape. Vector2/3/4 carry an arity badge —
    // a tiny TextBlock "2"/"3"/"4" centred inside the diamond — so the
    // three vector widths are distinguishable at a glance.
    private FrameworkElement BuildPinRow(Socket socket, bool isInput)
    {
        var kind = WidgetSocketPalette.KindFor(socket.DataType);
        var pin = BuildPinPath(socket, kind);
        // Accessibility — narrate the pin as "<socket name> (<data type>)" so
        // screen readers expose pin identity + type. Mirrors the inline-socket
        // label the user sees per the UE-Blueprints node-UI rule.
        AutomationProperties.SetName(pin, $"{socket.Name} ({socket.DataType})");
        _pinElements[socket.Id] = pin;

        var label = new TextBlock
        {
            Text       = socket.Name,
            FontFamily = SansFontFamily(), // socket label = chrome text, Sans (matches Architect)
            FontSize   = 12,
            Foreground = (Brush)Application.Current.Resources["CoalSecondaryTextBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin     = isInput ? new Thickness(6, 0, 0, 0) : new Thickness(0, 0, 6, 0),
            Opacity    = socket.IsPlaceholder ? 0.45 : 1.0,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        if (isInput)
        {
            row.Children.Add(pin);
            row.Children.Add(label);
            // Inline value pill. UE-Blueprints style: an input socket whose name matches
            // a Node.Attributes key gets an editable value pill on the node body
            // (NOT a detail-panel field). This is the only way to type caption
            // strings / numeric Scale·Opacity / font sizes — the WinUI rework
            // dropped the whole pill editor (the node body rendered socket glyphs
            // only). Faithful port of the pre-WinUI WidgetGraphCanvas.Pills.cs
            // contract: pill iff non-Flow socket with a matching attribute key;
            // commit stores the raw text back (no JSON wrapping).
            if (TryGetPillForSocket(Node, socket, out string pillValue))
                row.Children.Add(BuildValuePill(socket, pillValue));
        }
        else
        {
            row.Children.Add(label);
            row.Children.Add(pin);
        }
        return row;
    }

    // ─── Inline value-pill editor ───────────────────────────────────
    //
    // Ported from the pre-WinUI WidgetGraphCanvas.Pills.cs.
    // The canvas wires SetAttributeCommit so a committed edit routes through
    // PushUndo + MarkDirty + Rebuild on the host (matching every other
    // attribute-mutating gesture). Visualist-local — the swap/commit shape
    // mirrors WidgetView's geometry pills, copied not lifted.

    private Action<Node, string, string>? _onAttrCommit;

    /// <summary>Wire the inline value-pill commit. <paramref name="onCommit"/>
    /// receives (node, attributeKey, newRawValue); the canvas pushes undo,
    /// writes the attribute, marks dirty, and rebuilds.</summary>
    public void SetAttributeCommit(Action<Node, string, string> onCommit) => _onAttrCommit = onCommit;

    // ─── DaVinci keyframe arm + record-on-change ───────────────────
    //
    // Restores the pre-WinUI inline ◇ arm + ◀▶ seek cluster on animatable
    // value pills. The canvas owns the armed-parameter set + the record /
    // seek logic (it holds the trigger timeline + playhead); the node view
    // just renders the cluster and routes the gestures back by attribute key.
    // Set BEFORE the pin rows are built, so SetPillAnimation re-renders.
    private Func<Socket, string, bool>? _pillAnimShow;   // (socket, attrKey) → render cluster?
    private Func<string, bool>?         _pillArmed;      // attrKey → armed for record?
    private Action<string>?             _pillToggleArm;  // attrKey → toggle arm
    private Action<string>?             _pillSeekPrev;   // attrKey → playhead to prev keyframe
    private Action<string>?             _pillSeekNext;   // attrKey → playhead to next keyframe

    /// <summary>Wire the R26 arm/seek cluster callbacks. Triggers a re-render so
    /// the clusters appear on pills the canvas marks animatable.</summary>
    public void SetPillAnimation(
        Func<Socket, string, bool>? show,
        Func<string, bool>?         armed,
        Action<string>?             toggleArm,
        Action<string>?             seekPrev,
        Action<string>?             seekNext)
    {
        _pillAnimShow  = show;
        _pillArmed     = armed;
        _pillToggleArm = toggleArm;
        _pillSeekPrev  = seekPrev;
        _pillSeekNext  = seekNext;
        Render();
    }

    private FrameworkElement BuildArmSeekCluster(string attrKey)
    {
        bool armed = _pillArmed?.Invoke(attrKey) == true;

        var prev = MakeMicroButton("◀", Localizer.T(
            "visualist.widget.node.seek_prev.tip", "Seek to the previous keyframe on this parameter"));
        prev.Click += (_, __) => _pillSeekPrev?.Invoke(attrKey);

        var arm = MakeMicroButton(armed ? "◆" : "◇",
            armed ? Localizer.T("visualist.widget.node.record.armed.tip",
                                "Recording — value edits drop a keyframe at the playhead. Click to disarm.")
                  : Localizer.T("visualist.widget.node.record.disarmed.tip",
                                "Arm record — value edits drop a keyframe at the playhead."));
        arm.Foreground = armed
            ? ResolveBrush("SelectionBrush", Microsoft.UI.Colors.Gold)
            : ResolveBrush("CoalSecondaryTextBrush", Microsoft.UI.Colors.Gray);
        arm.Click += (_, __) => _pillToggleArm?.Invoke(attrKey);

        var next = MakeMicroButton("▶", Localizer.T(
            "visualist.widget.node.seek_next.tip", "Seek to the next keyframe on this parameter"));
        next.Click += (_, __) => _pillSeekNext?.Invoke(attrKey);

        var row = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            Spacing           = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(3, 0, 0, 0),
        };
        row.Children.Add(prev);
        row.Children.Add(arm);
        row.Children.Add(next);
        return row;
    }

    private static Button MakeMicroButton(string glyph, string tip)
    {
        var b = new Button
        {
            Content = new TextBlock
            {
                Text              = glyph,
                FontSize          = 8,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Padding         = new Thickness(2, 0, 2, 0),
            MinWidth        = 0,
            MinHeight       = 0,
            Background      = ResolveBrush("BgPillBrush", Microsoft.UI.Colors.Black),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(b, tip);
        return b;
    }

    /// <summary>
    /// True when <paramref name="socket"/> has a matching defaulted attribute on
    /// its parent node — i.e. an editable value pill should render next to it.
    /// Flow sockets never carry editable values. Case-insensitive key match.
    /// Faithful port of the WinForms TryGetPillForSocket.
    /// </summary>
    public static bool TryGetPillForSocket(Node node, Socket socket, out string value)
    {
        value = string.Empty;
        if (node?.Attributes is null || node.Attributes.Count == 0 || socket is null) return false;
        if (socket.DataType == SocketDataType.Flow) return false;
        if (node.Attributes.TryGetValue(socket.Name, out var v)) { value = v ?? string.Empty; return true; }
        foreach (var kv in node.Attributes)
        {
            if (string.Equals(kv.Key, socket.Name, StringComparison.OrdinalIgnoreCase))
            {
                value = kv.Value ?? string.Empty;
                return true;
            }
        }
        return false;
    }

    // The case-correct dictionary key for a socket (preserves mixed-case keys).
    private string ResolveAttrKey(Socket socket)
    {
        if (Node.Attributes.ContainsKey(socket.Name)) return socket.Name;
        foreach (var kv in Node.Attributes)
            if (string.Equals(kv.Key, socket.Name, StringComparison.OrdinalIgnoreCase)) return kv.Key;
        return socket.Name;
    }

    private static FontFamily MonoFontFamily()
        => new FontFamily(Application.Current.Resources["MonoFont"] as string ?? "Consolas");

    // Sans companion to MonoFontFamily() — node title / category / socket +
    // attribute labels use Sans (chrome text), only VALUE pills stay Mono.
    // [FONTCAST] SansFont is an <x:String> resource; a direct cast throws.
    private static FontFamily SansFontFamily()
        => new FontFamily(Application.Current.Resources["SansFont"] as string ?? "Segoe UI");

    private FrameworkElement BuildValuePill(Socket socket, string rawValue)
    {
        string attrKey = ResolveAttrKey(socket);

        var read = new TextBlock
        {
            Text         = string.IsNullOrEmpty(rawValue)
                ? Localizer.T("visualist.widget.node.pill.empty", "(empty)")
                : rawValue,
            FontFamily   = MonoFontFamily(), // VALUE pill stays Mono (values are code-like)
            FontSize     = 11,               // bumped 9→11 to match Architect's pill
            Foreground   = ResolveBrush("AccentValueBrush", Microsoft.UI.Colors.Goldenrod),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth     = 130,
        };
        var edit = new TextBox
        {
            Visibility = Visibility.Collapsed,
            FontFamily = MonoFontFamily(),
            FontSize   = 11, // match the read pill above
            MinWidth   = 48,
            Padding    = new Thickness(2, 0, 2, 0),
        };
        var grid = new Grid();
        grid.Children.Add(read);
        grid.Children.Add(edit);

        var border = new Border
        {
            Background      = ResolveBrush("BgPillBrush", Microsoft.UI.Colors.Black),
            BorderBrush     = ResolveBrush("BorderPillBrush", Microsoft.UI.Colors.DimGray),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(3),
            Padding         = new Thickness(3, 0, 3, 0),
            Margin          = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            // Marks this as an inline edit affordance so the canvas node-press
            // handler (OnNodePointerPressed) leaves the press to the pill's own
            // Tapped→TextBox edit instead of capturing the pointer for a node-drag,
            // which otherwise opens the edit box but blocks every keystroke.
            Tag             = "editpill",
            Child           = grid,
        };
        // The socket NAME is an identifier and stays English (see the Visualist
        // localization decision on socket / node-type names); only the sentence
        // around it is translated.
        ToolTipService.SetToolTip(border, string.Format(
            Localizer.T("visualist.widget.node.pill.tip_format", "{0} — click to edit"),
            socket.Name));

        string baseline = rawValue;
        bool finished = false;

        void Commit(bool commit)
        {
            if (finished) return;
            finished = true;
            edit.Visibility = Visibility.Collapsed;
            read.Visibility = Visibility.Visible;
            if (!commit) return;
            string newVal = edit.Text ?? "";
            // Re-read the live node state at commit time rather than trusting the
            // edit-start snapshot: an external mutation (keyframe seek, undo, a
            // sibling commit) could have moved the attribute since the pill opened,
            // and comparing against a stale baseline would either drop a genuine
            // edit or replay a phantom one. Live value = the only correct datum.
            string liveBaseline = Node.Attributes.TryGetValue(attrKey, out var live) ? (live ?? "") : "";
            if (string.Equals(newVal, liveBaseline, StringComparison.Ordinal)) return;
            read.Text = string.IsNullOrEmpty(newVal)
                ? Localizer.T("visualist.widget.node.pill.empty", "(empty)")
                : newVal;
            // Routes through the canvas → PushUndo + write + MarkDirty + Rebuild
            // (which recreates this view, so post-commit local state is moot).
            _onAttrCommit?.Invoke(Node, attrKey, newVal);
        }

        read.Tapped += (_, ev) =>
        {
            finished = false;
            baseline = Node.Attributes.TryGetValue(attrKey, out var cur) ? cur : "";
            edit.Text = baseline;
            read.Visibility = Visibility.Collapsed;
            edit.Visibility = Visibility.Visible;
            // Close the click→type focus-timing race (mirrors Architect's
            // TryFocusInlineEditorNow): the TextBox Visibility flips synchronously
            // above, but until layout realises it Focus() silently no-ops and the
            // first keystroke lands on the still-focused canvas — losing it. Force
            // a layout pass, then only SelectAll once Focus actually took.
            try
            {
                edit.UpdateLayout();
                if (edit.Visibility == Visibility.Visible && edit.Focus(FocusState.Programmatic))
                    edit.SelectAll();
            }
            catch { /* pre-realised tree — nothing typed can be lost yet */ }
            ev.Handled = true;
        };
        edit.KeyDown += (_, ev) =>
        {
            if (ev.Key == Windows.System.VirtualKey.Enter)       { Commit(true);  ev.Handled = true; }
            else if (ev.Key == Windows.System.VirtualKey.Escape) { Commit(false); ev.Handled = true; }
        };
        edit.LostFocus += (_, _) => Commit(true);

        // Animatable scalar pills carry the inline ◀ ◇ ▶ arm/seek cluster.
        // The canvas decides eligibility (animatable input socket whose attribute
        // key matches the socket) via the injected predicate.
        if (_pillAnimShow?.Invoke(socket, attrKey) == true)
        {
            var wrap = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            wrap.Children.Add(border);
            wrap.Children.Add(BuildArmSeekCluster(attrKey));
            return wrap;
        }

        return border;
    }

    // Map SocketDataType → fill brush. Mirrors the WinForms WidgetGraphCanvas
    // colour key without lifting any helpers across pillars (Visualist owns its
    // own palette). Keys land back to the engine's NodeRegistry.AreCompatible
    // type table when wire-drop comes in for sprint B.
    private static SolidColorBrush SocketFillFor(Socket socket)
    {
        // Back-fill the fill colour from the DataType when the socket
        // carries the default white (regular widget sockets are created with
        // only Name/Type/DataType, so Socket.Color stays white and every pin
        // rendered the same neutral). WidgetSocketPalette.EffectiveColor keeps
        // an explicitly-coloured socket (the sinks) and Flow's canonical white.
        return new SolidColorBrush(WidgetSocketPalette.EffectiveColor(socket));
    }

    // Builds the per-socket pin glyph as a 14×14 Grid hosting a Path
    // (the actual shape) plus optional arity badge for Vector* types. The
    // surrounding Grid keeps the hit-target stable at 14×14 regardless of
    // shape, so wire-drop hit-tests behave identically across types.
    // Tag=socket on the OUTER container so PinElements still resolves to a
    // single FrameworkElement and downstream code paths (canvas wire-drop,
    // pin-position lookups) keep working unchanged.
    private FrameworkElement BuildPinPath(Socket socket, WidgetSocketPinKind kind)
    {
        var path = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data            = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper
                .ConvertValue(typeof(Geometry), WidgetPinPathGeometry.PathFor(kind)),
            Fill            = SocketFillFor(socket),
            Stroke          = (Brush)Application.Current.Resources["CoalPaperBrush"],
            StrokeThickness = 0.5,
            Width           = 14,
            Height          = 14,
            Stretch         = Microsoft.UI.Xaml.Media.Stretch.None,
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var host = new Grid
        {
            Width  = 14,
            Height = 14,
            VerticalAlignment = VerticalAlignment.Center,
            // Background is load-bearing, not cosmetic: a WinUI Grid with a null
            // Background is NOT hit-test-visible in its empty regions, so a press
            // landing inside the 14×14 box but off the (smaller) glyph shape would
            // miss the pin entirely. Transparent makes the whole 14×14 footprint a
            // pointer target without painting over the glyph. (Recurring
            // null-Background gotcha.)
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Tag = socket,
            // Scale the pin from its centre on hover (14→18 ≈ 1.286×). The
            // transform is the identity at rest so the resting footprint stays 14×14.
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform       = new ScaleTransform { ScaleX = 1.0, ScaleY = 1.0 },
        };

        // Subtle hover glow — a soft type-coloured ring behind the glyph,
        // collapsed at rest. Cheaper + more controllable than a DropShadow (and
        // sidesteps the drop-shadow perf red-herring flagged in the perf audit);
        // it just fades in on PointerEntered. Tinted with the socket's effective
        // colour so each pin glows in its own type colour.
        var glowColor = WidgetSocketPalette.EffectiveColor(socket);
        var glow = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width  = 18,
            Height = 18,
            Fill   = new SolidColorBrush(Color.FromArgb(0x55, glowColor.R, glowColor.G, glowColor.B)),
            IsHitTestVisible    = false,
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility          = Visibility.Collapsed,
        };
        host.Children.Add(glow);   // behind the glyph
        host.Children.Add(path);

        // ─── Pin hover affordance + deterministic press hand-off ─────────
        //
        // Deterministic pin-press hand-off (ported from Architect's
        // NodeView.Pins.cs OnPinHitTargetPointerPressedForDrag → NotePinPress).
        // The pin stamps itself on the owning WidgetGraphCanvas BEFORE the press
        // bubbles up to the canvas-level OnNodePointerPressed, so a press routed to
        // the node body (instead of this small glyph) still resolves the exact
        // socket and starts a wire-drag — not a node-drag. We do NOT mark the
        // event Handled: the canvas's own OnPinPointerPressed hook (also on this
        // element) must still fire to begin the drag + capture the pointer; the
        // stamp is harmlessly consumed-and-cleared by the canvas in the same pass.
        host.PointerPressed += OnPinHostPointerPressedForHandoff;
        // Hover affordance — scale the pin 14→18 with a subtle glow + a
        // "Name (DataType)" tooltip so pins are discoverable wire endpoints
        // (Architect surfaces a hover tooltip on every pin; Visualist had none).
        host.PointerEntered += (_, _) =>
        {
            if (host.RenderTransform is ScaleTransform st) { st.ScaleX = 18.0 / 14.0; st.ScaleY = 18.0 / 14.0; }
            glow.Visibility = Visibility.Visible;
        };
        host.PointerExited += (_, _) =>
        {
            if (host.RenderTransform is ScaleTransform st) { st.ScaleX = 1.0; st.ScaleY = 1.0; }
            glow.Visibility = Visibility.Collapsed;
        };
        ToolTipService.SetToolTip(host, $"{socket.Name} ({socket.DataType})");

        // Vector2/3/4 arity badge — small "2"/"3"/"4" centred inside the
        // diamond so the three vector widths read at a glance instead of
        // three identical lavender diamonds.
        int arity = WidgetSocketPalette.ArityFor(socket.DataType);
        if (arity > 0)
        {
            var badge = new TextBlock
            {
                Text       = arity.ToString(),
                FontSize   = 7,
                Foreground = (Brush)Application.Current.Resources["CoalPaperBrush"],
                FontFamily = new FontFamily(Application.Current.Resources["MonoFont"] as string ?? "Consolas"), // [FONTCAST] MonoFont is an <x:String>; a direct cast throws
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false,
            };
            host.Children.Add(badge);
        }

        return host;
    }

    // ─── Deterministic pin-press hand-off to the owning canvas ──────────
    //
    // Ported from Architect's NodeView.Pins.cs (GetCanvasCached +
    // OnPinHitTargetPointerPressedForDrag). Walks up the visual tree to the
    // owning WidgetGraphCanvas (cached after first hit) and stamps the pressed
    // socket via NotePinPress so the canvas's bubbling OnNodePointerPressed
    // resolves the exact socket and starts a wire-drag. Left-button only — a
    // right-press on a pin keeps routing to the pin context menu.
    private WidgetGraphCanvas? _cachedCanvas;

    private WidgetGraphCanvas? GetOwningCanvas()
    {
        if (_cachedCanvas is not null) return _cachedCanvas;
        DependencyObject? walker = this;
        while (walker is not null)
        {
            walker = VisualTreeHelper.GetParent(walker);
            if (walker is WidgetGraphCanvas c) { _cachedCanvas = c; return c; }
        }
        return null;
    }

    private void OnPinHostPointerPressedForHandoff(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not Socket sock) return;
        if (!e.GetCurrentPoint(fe).Properties.IsLeftButtonPressed) return;
        GetOwningCanvas()?.NotePinPress(sock, Node);
        // Do NOT set e.Handled — the press must still bubble so the canvas's own
        // pin hook (OnPinPointerPressed) begins the drag + captures the pointer.
    }

    // Cached snapshot so the OnLoaded re-apply path can hand the
    // ThumbnailHost the same data the canvas pushed in earlier — useful
    // when a node view is recreated after a Rebuild() and the canvas's
    // RefreshPreviews() ran before the new view attached to the visual
    // tree.
    private NodeEvaluator.PreviewSnapshot? _lastSnapshot;

    /// <summary>
    /// Push a per-node preview snapshot into the body's
    /// preview strip. Called by <c>WidgetGraphCanvas.RefreshPreviews()</c>
    /// after every graph mutation (load / wire add-remove / attribute
    /// commit).
    ///
    /// Passing <c>null</c> or a snapshot whose
    /// <see cref="NodeEvaluator.PreviewKind"/> is <c>Empty</c> collapses
    /// the strip — that's the canvas's signal that the template did not
    /// opt into a preview (engine seam:
    /// <c>NodeTemplates.GetPreviewSource(title) == PreviewSource.None</c>).
    ///
    /// All bitmap / swatch / hint rendering lives inside
    /// <see cref="Phoenix.Controls.Visualist.WinUI.Controls.ThumbnailHost"/>;
    /// this method only decides row visibility and forwards the snapshot.
    /// </summary>
    public void SetPreview(NodeEvaluator.PreviewSnapshot? snap)
    {
        try
        {
            _lastSnapshot = snap;

            if (snap is null || snap.Kind == NodeEvaluator.PreviewKind.Empty)
            {
                PreviewStrip.Visibility = Visibility.Collapsed;
                PreviewHost.SetSnapshot(null);
                return;
            }

            PreviewStrip.Visibility = Visibility.Visible;
            PreviewHost.SetSnapshot(snap);
        }
        catch (Exception ex)
        {
            // Degrade gracefully on any unexpected failure — collapse the
            // strip and log via GlobalLogger so the trail lands in the
            // System log without a modal.
            try { PreviewStrip.Visibility = Visibility.Collapsed; } catch { }
            GlobalLogger.Error("WidgetGraphNodeView", "SetPreview", ex);
        }
    }

    // ─── V13 — live-trace flash (DEBUG_WIDGET_NODE) ──────────────────────────
    //
    // Architect's flash DISCIPLINE, re-implemented here rather than shared. The
    // standing rule (feedback_visualist_architect_chrome_independence) keeps the two
    // pillars' chrome independent, so this references no Architect type and adds no
    // Shared/UI dependency — the idiom is copied from
    // Architect.WinUI/Canvas/NodeView.xaml.cs (storyboard cache + Stop-before-Begin)
    // and LogicCanvasView.xaml.cs (per-frame dedupe), read and re-derived.
    //
    // Four discipline items and how each lands here — items 1-3 are carried over as-is,
    // item 4 deliberately DEVIATES and says why:
    //
    //  1. ONE storyboard per view, built on first flash and re-Begun per pulse.
    //     Architect's own comment records why: allocating a Storyboard + keyframes +
    //     easing functions per trace event churned the animation pool. The timeline
    //     is a constant, so Stop()+Begin() on a cached instance replays it exactly.
    //  2. Stop BEFORE Begin. Re-firing mid-ramp without stopping stacks two Opacity
    //     timelines on one property and the overlay sticks at full opacity — the node
    //     stays lit amber for the rest of the session.
    //  3. Coalesce a burst. Architect drains its pending set once per rendered frame;
    //     with no render tick here the equivalent is a short monotonic-clock gate, so
    //     a burst inside one frame interval reads as ONE pulse instead of a restart
    //     per message.
    //  4. NO completion callback, and that is a deliberate difference from Architect
    //     rather than a missing item. Architect needs its FlashTick generation guard
    //     because its expiries live in a DICTIONARY swept by one shared timer: N
    //     expirations are pending at once, so a stale one must be able to recognise
    //     that a re-flash superseded it. Here the pulse is ONE cached storyboard per
    //     view whose last keyframe already returns Opacity to 0, and every pulse
    //     re-zeroes the overlay before Begin — so the overlay always ends transparent
    //     with no callback at all. A generation guard on a SHARED Completed handler
    //     would in fact be decorative: the handler cannot tell which pulse it is the
    //     completion of, so any field it compared would have been written by the newest
    //     Begin and would always match. (An earlier draft of this file shipped exactly
    //     that always-true guard.) The storyboard is also Stop()ed on unload so a
    //     detached element never leaves an animation running.
    //
    // The overlay Border is built IN CODE rather than declared in the .xaml because
    // this sprint's write fence covers the code-behind only. Functionally identical:
    // it is appended last to NodeRoot.Children (topmost, so it tints the whole body
    // the way Architect's Grid.RowSpan overlay does) and IsHitTestVisible=false so it
    // can never eat a pointer event the canvas needs for drag / selection.
    //
    // Batching: the frame carries a LIST of node ids for one whole trigger activation
    // (never one id per message — renderWidgetTrigger re-runs per animation frame, so
    // a per-node trace would push the bus at frame rate). Each view tests membership
    // of its own id, which also makes a duplicate id inside one frame free: the same
    // list yields one Contains hit and one pulse.

    // Architect's live-trace amber. Same value, same meaning (execution pulse) —
    // duplicated locally per the per-pillar paint rule rather than referenced.
    private static readonly Color TraceFlashColor = Color.FromArgb(0xFF, 0xFF, 0xB3, 0x00);
    private const double TraceFlashPeakOpacity = 0.42;
    private const int TraceFlashRampInMs  = 120;
    private const int TraceFlashHoldToMs  = 300;
    private const int TraceFlashRampOutMs = 420;

    // Burst gate (discipline item 3). One display frame at 60 Hz.
    private const long TraceFlashCoalesceMs = 16;

    private Border? _traceFlashOverlay;
    private Storyboard? _traceFlashStoryboard;
    private long _traceFlashStartedAtMs;
    private bool _traceFlashEverStarted;
    private bool _traceHooked;
    private Action<Core.VisualistBusClient.WidgetNodeTrace>? _onWidgetNodeTrace;

    /// <summary>
    /// Subscribe to the design-time trace feed. Idempotent — WinUI can raise Loaded
    /// more than once for the same element (re-parent, tab re-entry), and a second
    /// subscription would double-fire every pulse.
    ///
    /// Subscription is PER VIEW, which is the honest tradeoff of keeping this inside
    /// the node view: Architect subscribes ONCE per canvas and dispatches through its
    /// node dictionary, which is strictly cheaper. It is affordable here because the
    /// feed is design-time only and fires once per trigger activation (not per frame),
    /// so the invocation list is walked at human speed. If this ever moves to a
    /// hotter cadence, consolidate onto WidgetGraphCanvas — it already keys every live
    /// view by node id in its own _nodeViews map.
    /// </summary>
    private void HookTraceFlash()
    {
        if (_traceHooked) return;
        try
        {
            _onWidgetNodeTrace ??= OnWidgetNodeTrace;
            Core.VisualistBusClient.Instance.OnWidgetNodeTrace += _onWidgetNodeTrace;
            _traceHooked = true;
        }
        catch (Exception ex)
        {
            // Never let a bus-singleton construction failure take the node view down —
            // the graph must still render without the trace feed.
            GlobalLogger.Error("WidgetGraphNodeView", "trace-flash hook failed", ex);
        }
    }

    private void UnhookTraceFlash()
    {
        // Stop an in-flight pulse regardless of the hook state: an animation left
        // running against a detached element is pure waste, and the next Load re-zeroes
        // the overlay anyway.
        if (_traceFlashStoryboard is not null)
        {
            try { _traceFlashStoryboard.Stop(); } catch { }
            if (_traceFlashOverlay is not null) _traceFlashOverlay.Opacity = 0;
        }

        if (!_traceHooked) return;
        _traceHooked = false;
        if (_onWidgetNodeTrace is null) return;
        try { Core.VisualistBusClient.Instance.OnWidgetNodeTrace -= _onWidgetNodeTrace; } catch { }
    }

    /// <summary>
    /// Raised on the bus receive-loop thread, so the UI work is marshalled. The
    /// membership test runs BEFORE the hop: with N node views subscribed, only the
    /// nodes actually named in the batch enqueue anything, so an activation that
    /// touched 3 nodes in a 40-node graph costs 3 dispatcher items, not 40.
    /// </summary>
    private void OnWidgetNodeTrace(Core.VisualistBusClient.WidgetNodeTrace trace)
    {
        if (trace is null) return;

        string myId = Node?.Id ?? "";
        if (string.IsNullOrEmpty(myId)) return;

        bool mine = false;
        // Ordinal, matching how node ids are keyed everywhere else (GUID strings).
        for (int i = 0; i < trace.NodeIds.Count; i++)
        {
            if (string.Equals(trace.NodeIds[i], myId, StringComparison.Ordinal)) { mine = true; break; }
        }
        if (!mine) return;

        var queue = DispatcherQueue;
        if (queue is null) return;
        if (!queue.TryEnqueue(TriggerTraceFlash))
        {
            // Queue shut down (window closing) — nothing to draw into, and this is a
            // routine race on teardown, not an error worth a log line.
        }
    }

    /// <summary>
    /// Play one pulse. UI thread only. Public-adjacent (internal) so a future
    /// canvas-level dispatcher can drive it directly without going through the bus.
    /// </summary>
    internal void TriggerTraceFlash()
    {
        try
        {
            long now = Environment.TickCount64;

            // Discipline item 3 — collapse a burst arriving inside one frame interval
            // into the pulse already running, instead of restarting the ramp per
            // message. Gated on a pulse having EVER started, so the very first flash of
            // the session is never swallowed by a zero start stamp (TickCount64 is time
            // since boot, so `now - 0` is huge in practice — but relying on that would
            // be relying on uptime).
            if (_traceFlashEverStarted && now - _traceFlashStartedAtMs < TraceFlashCoalesceMs)
                return;

            var overlay = EnsureTraceFlashOverlay();
            if (overlay is null) return;
            var sb = EnsureTraceFlashStoryboard(overlay);
            if (sb is null) return;

            _traceFlashStartedAtMs = now;
            _traceFlashEverStarted = true;

            // Discipline item 2 — Stop before Begin so opacity timelines never stack.
            // The explicit re-zero is what makes the no-completion-callback design (item
            // 4) safe: whatever value a stopped or held animation left behind, the next
            // pulse starts from transparent.
            try { sb.Stop(); } catch { /* element left the tree mid-pulse */ }
            overlay.Opacity = 0;
            try { sb.Begin(); }
            catch (Exception ex)
            {
                // Pre-realised / detached tree: leave the overlay transparent rather
                // than stranding it lit.
                overlay.Opacity = 0;
                GlobalLogger.Error("WidgetGraphNodeView", "trace-flash begin failed", ex);
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetGraphNodeView", "TriggerTraceFlash", ex);
        }
    }

    // Built on FIRST flash only — a node that never appears in a trace adds zero
    // elements and zero animation objects to the tree.
    private Border? EnsureTraceFlashOverlay()
    {
        if (_traceFlashOverlay is not null) return _traceFlashOverlay;
        if (NodeRoot is null) return null;

        var overlay = new Border
        {
            Background       = new SolidColorBrush(TraceFlashColor),
            CornerRadius     = new CornerRadius(6),   // matches NodeChrome
            Opacity          = 0,
            IsHitTestVisible = false,
        };
        // Appended last ⇒ topmost within NodeRoot, so the tint reads across the
        // header + body the way Architect's RowSpan overlay does.
        NodeRoot.Children.Add(overlay);
        _traceFlashOverlay = overlay;
        return overlay;
    }

    private Storyboard? EnsureTraceFlashStoryboard(Border overlay)
    {
        if (_traceFlashStoryboard is not null) return _traceFlashStoryboard;

        var anim = new DoubleAnimationUsingKeyFrames();
        Storyboard.SetTarget(anim, overlay);
        Storyboard.SetTargetProperty(anim, "Opacity");
        anim.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value   = 0,
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime        = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(TraceFlashRampInMs)),
            Value          = TraceFlashPeakOpacity,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        anim.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(TraceFlashHoldToMs)),
            Value   = TraceFlashPeakOpacity,
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime        = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(TraceFlashRampOutMs)),
            Value          = 0.0,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        });

        // No Completed handler — see discipline item 4. The last keyframe holds 0 and
        // every pulse re-zeroes before Begin, so the overlay is transparent at rest in
        // both the completed and the stopped case.
        var sb = new Storyboard();
        sb.Children.Add(anim);
        _traceFlashStoryboard = sb;
        return sb;
    }
}
