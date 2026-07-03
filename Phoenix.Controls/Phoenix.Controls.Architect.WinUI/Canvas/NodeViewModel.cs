using System.Collections.Generic;
using System.Collections.ObjectModel;
using Phoenix.Controls.Architect.Core;
using Phoenix.Controls.Architect.WinUI.ViewModels;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

// View-model for one node on the canvas. Wraps a Node and exposes
// binding-friendly projections of its sockets split into Inputs/Outputs
// rows (the design's two-column layout in architect.jsx:Node).
//
// Position (X/Y) is mutable so a future drag-handler can update
// the model directly — the model still owns the Point via Node.Location.
// Selection lives on the parent LogicCanvasViewModel so multi-selection
// can be coordinated; here we just expose IsSelected as a derived flag.
public sealed class NodeViewModel : ObservableObject
{
    private readonly Node _node;
    private bool _isSelected;

    public NodeViewModel(Node node)
    {
        _node = node;
        Inputs           = new ObservableCollection<SocketViewModel>();
        Outputs          = new ObservableCollection<SocketViewModel>();
        MiddleAttributes = new ObservableCollection<MiddleAttributeViewModel>();
        // Resolve the template-derived caches on construct so the
        // per-binding hot getters (IconGlyph, NodeTooltip) don't round-trip
        // through NodeRegistry.GetTemplate on every PropertyChanged storm.
        // RebuildTemplateDerivedCaches re-fires on Title changes via
        // RaiseHeaderChanged so the cached glyph + tooltip stay in sync
        // with rare event/var rename paths.
        RebuildTemplateDerivedCaches();
        RebuildSockets();
    }

    public Node Model => _node;

    public string Id       => _node.Id;

    /// <summary>
    /// Display title shown in the node header. Pre-T15 cascade:
    /// Event.Return with EventName attr → "↩ {EventName}";
    /// otherwise NodeRegistry.Template.DisplayName when set
    /// (e.g. Math.Add → "Add  +"); falls back to canonical Title.
    /// Single source of truth lives in <see cref="NodeGeometry.DisplayTitle"/>
    /// so the static intrinsic-width path renders the same string the view does.
    /// </summary>
    public string Title => NodeGeometry.DisplayTitle(_node);

    // Attribute key holding the user-typed display-name override.
    // Read/written by EditableTitle + read by NodeGeometry.DisplayTitle. Kept
    // local to this class — the only other touch is the geometry helper which
    // hard-codes the same key string so this view-model and the static
    // measurement path agree without a shared constant. Identity (Node.Title)
    // is NEVER mutated by this attribute — the exporter, NodeRegistry, and
    // CommandManifest all continue to bind by Node.Title.
    private const string DisplayNameOverrideKey = "__displayNameOverride";

    /// <summary>
    /// TwoWay-bound proxy for the inline title TextBox.
    /// <para>
    /// Course correction: reads / writes
    /// <c>Node.Attributes["__displayNameOverride"]</c> instead of
    /// <see cref="Phoenix.Controls.Shared.Models.Node.Title"/>. The original
    /// setter wrote through to <c>Node.Title</c>, which is the lookup key
    /// for <c>NodeRegistry.GetTemplate(node.Title)</c> and
    /// <c>CommandManifest.Resolve(title)</c> — renaming a <c>Math.Add</c>
    /// instance to "MyMath" silently broke template binding and script export.
    /// The display override is non-load-bearing: empty / whitespace clears
    /// the attribute (so the node falls back to its registry title) and a
    /// value identical to <c>Node.Title</c> also clears it (so the node
    /// tracks its identity by default and an override is only stored when
    /// the user genuinely wants a custom label).
    /// </para>
    /// <para>
    /// Empty-title rejection is dropped from the commit path — empty now
    /// MEANS "clear the override", which is a legitimate operation.
    /// </para>
    /// </summary>
    public string EditableTitle
    {
        get
        {
            // Surface the override if set, else seed the editor with the
            // canonical Node.Title so the user can either type a new label or
            // delete the field to reset to the registry title.
            if (_node.Attributes != null
                && _node.Attributes.TryGetValue(DisplayNameOverrideKey, out var ov)
                && !string.IsNullOrWhiteSpace(ov))
                return ov;
            return _node.Title ?? string.Empty;
        }
        set
        {
            var v = value ?? string.Empty;
            var current = EditableTitle;
            if (string.Equals(current, v, System.StringComparison.Ordinal)) return;

            // Empty / whitespace, OR a value identical to the canonical
            // Node.Title — both clear the override so the node returns to
            // showing its registry-derived title. This keeps the attribute
            // dictionary tidy (no stale "MyAdd"==Title entries that would
            // round-trip through .phxg saves for no rendered effect).
            bool clear =
                string.IsNullOrWhiteSpace(v) ||
                string.Equals(v, _node.Title, System.StringComparison.Ordinal);

            if (clear)
                _node.Attributes.Remove(DisplayNameOverrideKey);
            else
                _node.Attributes[DisplayNameOverrideKey] = v;

            // DisplayTitle is the binding target via NodeGeometry; refresh
            // every template-derived projection so the rendered header and
            // tooltip pick up the new label. RaiseHeaderChanged also primes
            // the cached IconGlyph / NodeTooltip base — those still resolve
            // off Node.Title (unchanged), so the cache content doesn't shift,
            // but the bound property updates are what re-paint the header.
            RaiseHeaderChanged();
            OnPropertyChanged(nameof(EditableTitle));
        }
    }

    private bool _isTitleRenaming;
    /// <summary>
    /// True while the inline title TextBox is showing. Mirrors
    /// <see cref="SocketViewModel.IsRenaming"/> in shape so the XAML
    /// visibility converter can swap the read-only TextBlock for the editor.
    /// </summary>
    public bool IsTitleRenaming
    {
        get => _isTitleRenaming;
        set => SetField(ref _isTitleRenaming, value);
    }

    // Inline-title edit baseline — mirror of SocketViewModel's
    // _labelEditBaseline path: Esc rolls back to the value at edit-start,
    // and a no-op commit (old == new) skips the undo push so a typing-then-
    // deleting session doesn't accrete a stale snapshot.
    //
    // The baseline tracks the override attribute (not Node.Title).
    // When no override is set, the baseline is null and Esc restores the
    // node to its registry-derived title. When the user deletes the field
    // and commits, the override clears (empty=clear contract above) which
    // is a legitimate, intentional reset — no rejection / no log entry.
    private string? _titleEditBaseline;
    private bool    _titleEditActive;

    /// <summary>
    /// Snapshot the current display-name override as the rollback baseline.
    /// When no override is set, the baseline is null so Esc returns the node
    /// to its registry-derived title.
    /// </summary>
    public void BeginTitleEdit()
    {
        _titleEditBaseline = _node.Attributes != null
            && _node.Attributes.TryGetValue(DisplayNameOverrideKey, out var ov)
            && !string.IsNullOrWhiteSpace(ov)
                ? ov
                : null;
        _titleEditActive   = true;
        IsTitleRenaming    = true;
    }

    /// <summary>
    /// Close the title-rename session. Returns true when the commit produced
    /// a real change vs the baseline (so the view can gate <c>PushUndoForInlineEdit</c>).
    /// On <paramref name="commit"/>=false (Esc) the baseline is restored
    /// through the same setter the TwoWay binding uses — keeping the binding
    /// state coherent with the override attribute. An empty / cleared commit
    /// is treated as an intentional reset to the registry title (no
    /// rejection, no log).
    /// </summary>
    public bool EndTitleEdit(bool commit)
    {
        if (!_titleEditActive)
        {
            IsTitleRenaming = false;
            return false;
        }

        // Read the current override (null when absent / blank) to compare
        // against the snapshot baseline. The setter contract above means a
        // committed empty / Title-matching value has already cleared the
        // attribute, so the "current" value here reflects the post-commit state.
        string? currentOverride = _node.Attributes != null
            && _node.Attributes.TryGetValue(DisplayNameOverrideKey, out var cur)
            && !string.IsNullOrWhiteSpace(cur)
                ? cur
                : null;

        bool changed = !string.Equals(currentOverride, _titleEditBaseline, System.StringComparison.Ordinal);

        if (!commit && changed)
        {
            // Esc rollback — restore the baseline through the same setter
            // path. A null baseline routes through EditableTitle="" which
            // clears the attribute via the empty=clear contract.
            EditableTitle = _titleEditBaseline ?? string.Empty;
            changed = false;
        }

        IsTitleRenaming    = false;
        _titleEditBaseline = null;
        _titleEditActive   = false;
        return changed;
    }

    /// <summary>
    /// True when this node's <see cref="Phoenix.Controls.Shared.Models.Node.Title"/>
    /// matches one of the Event-pair role tokens (<c>Event.Trigger</c> /
    /// <c>Event.Executor</c>). Surfaced so the inline socket-rename commit
    /// path in <see cref="NodeView"/> can gate the cross-file payload-shape
    /// sync without re-reading the model field directly.
    /// </summary>
    public bool IsEventPairHost
        => _node.Title is "Event.Trigger" or "Event.Executor";

    public string Category => _node.Category ?? string.Empty;

    /// <summary>
    /// Italic subtitle ("definer") rendered beneath the title. Surfaces the
    /// distinguishing value for nodes that look identical at the title level
    /// (Event.Trigger.EventName, Var.Get/Set.VariableName, Macro.Call.MacroName,
    /// Twitch.ChatMessage.Commands, etc.). Truncates to 22 chars + horizontal
    /// ellipsis for display per pre-T15 parity — single source of truth lives
    /// in <see cref="NodeGeometry.TruncatedDefiner"/>.
    /// </summary>
    public string? Definer => NodeGeometry.TruncatedDefiner(_node);

    public bool HasDefiner => !string.IsNullOrEmpty(Definer);

    /// <summary>
    /// Screen-reader-friendly compact label combining Title and Definer
    /// (when present). Surfaced via NodeView's AutomationProperties.Name
    /// binding so a focus traversal announces "Schedule.Cron — */5 * * * *"
    /// or "Var.Set — counter" rather than just "node". 0.10.0 accessibility.
    /// </summary>
    public string AccessibleName => HasDefiner ? $"{Title} — {Definer}" : Title;

    /// <summary>
    /// Optional 16×16 Segoe Fluent Icons codepoint rendered left of the
    /// header title — resolved from the template's
    /// <see cref="NodeTemplate.IconGlyph"/> slot. Empty when the template
    /// doesn't opt in (most templates today).
    /// <para>
    /// Cached on construct + invalidated on Title change via
    /// <see cref="RebuildTemplateDerivedCaches"/> so per-binding reads
    /// don't round-trip through <see cref="NodeRegistry.GetTemplate"/>.
    /// PropertyChanged storms (selection toggles, var-chain highlight,
    /// flash bursts) all re-evaluate every bound property; the dictionary
    /// lookup is O(1) but the GC noise from try/catch + nullable chain
    /// adds up across N nodes × M re-evals per interaction.
    /// </para>
    /// </summary>
    public string IconGlyph => _iconGlyph;

    /// <summary>Visibility shortcut for the header FontIcon — true iff IconGlyph is set.</summary>
    public bool HasIconGlyph => !string.IsNullOrEmpty(_iconGlyph);

    /// <summary>
    /// Tooltip rendered on node-header hover: title + category + the template's
    /// description (when present). When <see cref="ErrorReason"/> is set the
    /// reason string is appended on its own line so the red-triangle badge
    /// ("Error-state node reason") has a hover-discoverable
    /// explanation alongside the visual marker. Mirrors pre-T15 canvas
    /// tooltip resolver output for nodes.
    /// <para>
    /// Base ("title · category[\ndescription]") is cached in
    /// <see cref="_nodeTooltipBase"/> on construct + Title change; the
    /// getter only re-formats when <see cref="ErrorReason"/> is set, which
    /// changes through its own PropertyChanged path.
    /// </para>
    /// </summary>
    public string NodeTooltip
        => string.IsNullOrEmpty(_errorReason)
            ? _nodeTooltipBase
            : $"{_nodeTooltipBase}\n⚠ {_errorReason}";

    // Backing fields for template-derived caches.
    private string _iconGlyph       = string.Empty;
    private string _nodeTooltipBase = string.Empty;

    /// <summary>
    /// Re-snapshot every template-derived hot getter into its
    /// backing cache. Called on construct (warm cache) and on
    /// <see cref="RaiseHeaderChanged"/> (Title change — rare, only Event /
    /// Var rename paths flip Title). Try/catch swallows a registry miss
    /// (e.g. a template was deleted between graph load and now) by leaving
    /// the cached values at their defaults.
    /// </summary>
    private void RebuildTemplateDerivedCaches()
    {
        try
        {
            var t = Phoenix.Controls.Architect.Core.NodeRegistry.GetTemplate(Title);
            _iconGlyph = t?.IconGlyph ?? string.Empty;
            var head = string.IsNullOrEmpty(Category) ? Title : $"{Title} · {Category}";
            if (t != null && !string.IsNullOrEmpty(t.Description))
                head = $"{head}\n{t.Description}";
            _nodeTooltipBase = head;
        }
        catch
        {
            _iconGlyph       = string.Empty;
            _nodeTooltipBase = string.IsNullOrEmpty(Category) ? Title : $"{Title} · {Category}";
        }
    }

    private string? _errorReason;
    /// <summary>
    /// Human-readable explanation for a static-validation failure on this
    /// node — e.g. "Macro target not found" or "Unpaired Event.Trigger".
    /// When non-empty, NodeView paints a red-triangle badge in the header's
    /// right edge and <see cref="NodeTooltip"/> appends the reason on its
    /// own line. Null / empty clears the badge.
    ///
    /// Distinct from <see cref="IsErrorState"/>: that flag drives the red
    /// border (already painted today for unpaired event nodes); the reason
    /// string surfaces *why*, which the border alone can't convey.
    /// </summary>
    public string? ErrorReason
    {
        get => _errorReason;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? null : value;
            if (SetField(ref _errorReason, v))
            {
                OnPropertyChanged(nameof(HasErrorReason));
                OnPropertyChanged(nameof(NodeTooltip));
            }
        }
    }

    /// <summary>
    /// Visibility flag for the red-triangle badge — true iff
    /// <see cref="ErrorReason"/> is set. The XAML BoolVis converter binds
    /// off this rather than the string itself.
    /// </summary>
    public bool HasErrorReason => !string.IsNullOrEmpty(_errorReason);

    /// <summary>
    /// Header section pixel height — 38 when a definer subtitle renders,
    /// 24 otherwise. Bound by NodeView.xaml so the visible band matches the
    /// wire-anchor math in <see cref="NodeGeometry.HeaderHeight"/>.
    /// </summary>
    public double HeaderSectionHeight =>
        HasDefiner ? NodeGeometry.HeaderBandHeightDef : NodeGeometry.HeaderBandHeightPlain;

    /// <summary>
    /// Re-fires PropertyChanged for Title / Definer / HasDefiner so the view
    /// re-binds when an attribute mutation (e.g. user edits EventName via the
    /// inline pill) changes the rendered header. Call from places that mutate
    /// Node.Attributes outside the SocketViewModel TwoWay binding path.
    /// <para>
    /// Also rebuilds the template-derived caches (IconGlyph,
    /// NodeTooltip base) since Title is the cache key. Definer changes
    /// don't invalidate these (definer is an attribute-driven subtitle,
    /// not a template lookup), but the rebuild is cheap and the call site
    /// is rare (only Event / Var rename paths fire it).
    /// </para>
    /// </summary>
    public void RaiseHeaderChanged()
    {
        RebuildTemplateDerivedCaches();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Definer));
        OnPropertyChanged(nameof(HasDefiner));
        OnPropertyChanged(nameof(IconGlyph));
        OnPropertyChanged(nameof(HasIconGlyph));
        OnPropertyChanged(nameof(NodeTooltip));
        // Compact-mode toggle (LogicCanvasView.ToggleCompactMode)
        // flips Attributes["Compact"] then calls this; nudge the compact
        // affordance bindings so the centred CompactSymbol glyph + the
        // compact-tint border (ApplyBorder reads IsCompactMode) refresh in
        // step rather than waiting for the next graph mutation.
        OnPropertyChanged(nameof(IsCompactMode));
        OnPropertyChanged(nameof(CompactSymbol));
        OnPropertyChanged(nameof(HasCompactSymbol));
    }

    public double X      => _node.Location.X;
    public double Y      => _node.Location.Y;

    /// <summary>
    /// True when this node renders as the diamond Reroute passthrough rather
    /// than a standard rectangular node. Pre-T15 the Reroute was a 20×20
    /// diamond with no header / chrome / middle attrs — the WinUI port
    /// initially rendered every node through the standard Border, which made
    /// the reroute look like an oversized rectangular node and lost the
    /// "wire knot" affordance Majo named.
    /// </summary>
    public bool IsReroute => string.Equals(_node.Title, "Flow.Reroute", System.StringComparison.Ordinal);

    /// <summary>Inverse of <see cref="IsReroute"/>, exposed for direct
    /// Visibility binding on the standard-node chrome.</summary>
    public bool IsStandardNode => !IsReroute;

    /// <summary>
    /// True when the node persists the opt-in compact-mode flag
    /// (right-click → Convert to Compact writes <c>Attributes["Compact"]="true"</c>).
    /// Mirrors <see cref="NodeGeometry.IsCompactMode"/>. Bound by NodeView.xaml
    /// to (1) tint NodeRoot's border so compact mode reads as a distinct state
    /// rather than a manually-shrunk node, and (2) reveal the centred
    /// <see cref="CompactSymbol"/> glyph over the body. Pre-fix every node
    /// rendered through the identical Border with no compact affordance, so a
    /// 56×36 compact Math.Add was indistinguishable from a small custom node.
    /// </summary>
    public bool IsCompactMode
        => _node.Attributes != null
        && _node.Attributes.TryGetValue("Compact", out var v)
        && string.Equals(v, "true", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The operator glyph the registry assigns to Math / Logic
    /// templates (<c>+ − × ÷ % == ≠ &gt; &lt; AND OR ! ?</c> …). Surfaced so
    /// compact-mode nodes can paint a large centred symbol that conveys the
    /// node's function at a glance when the title band is hidden. Empty for
    /// templates without a compact symbol.
    /// </summary>
    public string CompactSymbol
    {
        get
        {
            try
            {
                var t = Phoenix.Controls.Architect.Core.NodeRegistry.GetTemplate(_node.Title);
                return t?.CompactSymbol ?? string.Empty;
            }
            catch { return string.Empty; }
        }
    }

    /// <summary>Visibility shortcut — true iff compact mode is active AND the
    /// template assigns a <see cref="CompactSymbol"/>.</summary>
    public bool HasCompactSymbol => IsCompactMode && !string.IsNullOrEmpty(CompactSymbol);

    /// <summary>
    /// Stroke brush for the Flow.Reroute diamond. The reroute knot is
    /// a NEUTRAL passthrough connector, not a type-identifier. The stroke is
    /// selection-driven (Gold <c>EmberPrimaryBrush</c> tone when this node is
    /// selected, DimGray otherwise) mirroring the pre-T15 WinForms Canvas which
    /// rendered the reroute as a fixed neutral-grey diamond with a
    /// selection-driven border. Pre-fix this derived the colour from the wired
    /// input socket's <c>SocketPalette.HexFor(DataType)</c>, turning the bead
    /// into a type-coded dot that broke the neutral wire-knot idiom.
    /// Re-raised by <see cref="RaiseRerouteSelectionChanged"/> (from the
    /// <see cref="IsSelected"/> setter) only — no longer reacts to the
    /// wildcard cascade's type flips.
    /// </summary>
    public Microsoft.UI.Xaml.Media.SolidColorBrush RerouteStrokeBrush
        => _isSelected
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(ParseHexColor("#FFE5A24E")) // EmberPrimary gold
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(ParseHexColor("#FF696969")); // DimGray

    /// <summary>
    /// Fill brush for the Flow.Reroute diamond — fixed neutral grey
    /// (<c>#FF3C3C41</c>) matching the pre-T15 WinForms reroute body
    /// (<c>Color.FromArgb(60,60,65)</c>). The diamond reads as a neutral
    /// grey wire knot; the selection-driven <see cref="RerouteStrokeBrush"/>
    /// supplies the only colour change.
    /// </summary>
    public Microsoft.UI.Xaml.Media.SolidColorBrush RerouteFillBrush
        => new(ParseHexColor("#FF3C3C41"));

    /// <summary>
    /// Re-raise the reroute stroke binding when selection flips so
    /// the diamond's border tracks Gold-when-selected / DimGray-otherwise.
    /// Called from the IsSelected setter (reroute nodes only — cheap no-op
    /// nudge for standard nodes which don't bind these properties).
    /// </summary>
    private void RaiseRerouteSelectionChanged()
    {
        if (!IsReroute) return;
        OnPropertyChanged(nameof(RerouteStrokeBrush));
    }

    private static Windows.UI.Color ParseHexColor(string hex)
    {
        try
        {
            var s = hex.TrimStart('#');
            byte a = 0xFF, r = 0x80, g = 0x80, b = 0x80;
            if (s.Length == 8)
            {
                a = System.Convert.ToByte(s.Substring(0, 2), 16);
                r = System.Convert.ToByte(s.Substring(2, 2), 16);
                g = System.Convert.ToByte(s.Substring(4, 2), 16);
                b = System.Convert.ToByte(s.Substring(6, 2), 16);
            }
            else if (s.Length == 6)
            {
                r = System.Convert.ToByte(s.Substring(0, 2), 16);
                g = System.Convert.ToByte(s.Substring(2, 2), 16);
                b = System.Convert.ToByte(s.Substring(4, 2), 16);
            }
            return Windows.UI.Color.FromArgb(a, r, g, b);
        }
        catch { return Windows.UI.Color.FromArgb(0xFF, 0x80, 0x80, 0x80); }
    }

    // Per-node geometry memoisation. XAML binds Width in several
    // places (NodeRoot, PinOverlay, shadow Border via ActualWidth) and Height
    // (PinOverlay), and every NotifyPropertyChanged storm (selection toggle,
    // var-chain highlight, flash burst) re-evaluates every bound property —
    // so EffectiveWidth / IntrinsicHeight (which walk all sockets +
    // DefaultProperties) ran far more often than the underlying content
    // actually changed. The values only change through socket / attribute
    // mutations, which all funnel through the OnPropertyChanged(nameof(Width))
    // / (nameof(Height)) nudge sites; InvalidateGeometryCache() is called
    // immediately before each so the next getter recomputes once and re-caches.
    // NaN is the "not computed yet" sentinel.
    private double _cachedWidth  = double.NaN;
    private double _cachedHeight = double.NaN;

    /// <summary>
    /// Drop the memoised Width / Height so the next binding read
    /// recomputes from <see cref="NodeGeometry"/>. Call alongside every
    /// <c>OnPropertyChanged(nameof(Width))</c> / <c>(nameof(Height))</c> so
    /// the cache invalidates in lockstep with the change notification.
    /// </summary>
    private void InvalidateGeometryCache()
    {
        _cachedWidth  = double.NaN;
        _cachedHeight = double.NaN;
    }

    /// <summary>
    /// Rendered node width. Delegates to <see cref="NodeGeometry.EffectiveWidth"/>
    /// so wire endpoints (<see cref="NodeGeometry.SocketAnchor"/>) and the
    /// painted body always agree — pre-fix this property and the wire path
    /// diverged: Width returned the live shrink-to-fit, while SocketAnchor
    /// read the legacy persisted <see cref="Node.Size"/> and anchored output
    /// pipes at the old 520-px ceiling. Compact mode keeps the persisted
    /// 56-px footprint (toggled via right-click → Convert to Compact) intact.
    /// <para>
    /// Memoised in <see cref="_cachedWidth"/>; recomputed only
    /// after <see cref="InvalidateGeometryCache"/> fires (every socket /
    /// attribute mutation).
    /// </para>
    /// </summary>
    public double Width
    {
        get
        {
            if (!double.IsNaN(_cachedWidth)) return _cachedWidth;
            return _cachedWidth = NodeGeometry.EffectiveWidth(_node);
        }
    }

    /// <summary>
    /// Rendered node height. Compact mode honours the persisted
    /// <see cref="Node.Size"/>; otherwise the value comes from
    /// <see cref="NodeGeometry.IntrinsicHeight"/> so socket-row growth
    /// (placeholder activation, dynamic event-pair grow) reflects in the
    /// painted body without a manual resize.
    /// <para>
    /// Memoised in <see cref="_cachedHeight"/>; recomputed only
    /// after <see cref="InvalidateGeometryCache"/> fires.
    /// </para>
    /// </summary>
    public double Height
    {
        get
        {
            if (!double.IsNaN(_cachedHeight)) return _cachedHeight;
            double h = NodeGeometry.IsCompactMode(_node) && _node.Size.Height > 0
                ? _node.Size.Height
                : NodeGeometry.IntrinsicHeight(_node);
            return _cachedHeight = h;
        }
    }

    /// <summary>Sockets with Type=Input, in display order, with RowIndex assigned.</summary>
    public ObservableCollection<SocketViewModel> Inputs  { get; }

    /// <summary>Sockets with Type=Output, in display order, with RowIndex assigned.</summary>
    public ObservableCollection<SocketViewModel> Outputs { get; }

    /// <summary>
    /// Middle-attribute rows rendered between the socket grid and the bottom
    /// of the body — Design_Orders §5.1 `Key …… [pill]` / `Key …… ☑/☐` for
    /// every <see cref="Phoenix.Controls.Architect.Core.NodeTemplate.DefaultProperties"/>
    /// key that has no matching input socket on this node. Without this collection
    /// the ~20 templates whose distinguishing attribute lives on
    /// <c>DefaultProperties</c> (Bus.OnMessage.EventType, Logic.If.Operator,
    /// Schedule.RunAt.DateTime, …) were authorable only via the inspector — the
    /// regression this exists to close.
    /// </summary>
    public ObservableCollection<MiddleAttributeViewModel> MiddleAttributes { get; }

    /// <summary>Visibility flag for the middle-attribute area + dotted divider —
    /// true iff at least one middle-attribute row exists.</summary>
    public bool HasMiddleAttributes => MiddleAttributes.Count > 0;

    /// <summary>
    /// Re-snapshot the Inputs/Outputs row-VMs from the underlying
    /// <see cref="Node.Sockets"/> list. Call after mutating the model's
    /// socket list (placeholder activation, dynamic event-pair grow).
    /// Per architect.jsx:Node — sockets render in two columns with rows
    /// counted off Math.max(ins.length, outs.length); RowIndex is per-side
    /// so an input at row 1 and output at row 1 share the same y.
    /// </summary>
    public void RebuildSockets()
    {
        // Detach the prior set's listeners so a swap doesn't leak handlers.
        foreach (var s in Inputs)  s.PropertyChanged -= OnSocketPropertyChanged;
        foreach (var s in Outputs) s.PropertyChanged -= OnSocketPropertyChanged;

        Inputs.Clear();
        Outputs.Clear();
        int leftRow = 0, rightRow = 0;
        foreach (var s in _node.Sockets)
        {
            SocketViewModel vm = s.Type == SocketType.Input
                ? new SocketViewModel(_node, s, leftRow++)
                : new SocketViewModel(_node, s, rightRow++);
            vm.PropertyChanged += OnSocketPropertyChanged;
            (s.Type == SocketType.Input ? Inputs : Outputs).Add(vm);
        }
        RebuildMiddleAttributes();
        // Body geometry (rows + width) recomputes; nudge bound view.
        InvalidateGeometryCache();
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(Height));
    }

    /// <summary>
    /// Re-build the <see cref="MiddleAttributes"/> collection by filtering the
    /// node's template <c>DefaultProperties</c> against the current socket
    /// name set: every default-properties key WITHOUT a matching socket
    /// (case-insensitive) becomes a row.
    ///
    /// Bool detection is heuristic: a default value of literally "true" or
    /// "false" (case-insensitive) is treated as a boolean. ~10 templates
    /// use this shape — <c>NodeRegistry.Templates.RemainingBands.cs</c>
    /// (Append / Enabled / Numeric / Value / Default) — and the inspector
    /// likewise relies on the value-shape heuristic. Anything else renders
    /// as an editable pill.
    ///
    /// Hidden keys (Compact, DisableConnectionWarnings, …) are NEVER part of
    /// the template's DefaultProperties — they're written through other paths
    /// (right-click menu, exporter-only attrs) — so the filter naturally
    /// excludes them without a separate denylist.
    /// </summary>
    public void RebuildMiddleAttributes()
    {
        // Detach the prior set's listeners BEFORE Clear() — mirrors the
        // RebuildSockets detach-before-clear pattern. Clearing the collection
        // drops the references from MiddleAttributes but the old
        // MiddleAttributeViewModel instances stay subscribed to this VM's
        // OnMiddleAttributePropertyChanged handler, so a socket add/remove
        // that re-runs RebuildMiddleAttributes accreted a stale listener per
        // rebuild (a real handler leak under dynamic-event / placeholder grow).
        foreach (var old in MiddleAttributes)
            old.PropertyChanged -= OnMiddleAttributePropertyChanged;
        MiddleAttributes.Clear();

        Phoenix.Controls.Architect.Core.NodeTemplate? tmpl = null;
        try { tmpl = Phoenix.Controls.Architect.Core.NodeRegistry.GetTemplate(_node.Title); }
        catch { /* registry miss — node renders without middle-attr rows; same fallback the rest of the VM uses */ }

        if (tmpl?.DefaultProperties == null || tmpl.DefaultProperties.Count == 0)
        {
            OnPropertyChanged(nameof(HasMiddleAttributes));
            return;
        }

        // Build a case-insensitive set of every INPUT socket name on this
        // node — middle attrs only appear for keys WITHOUT a matching INPUT
        // socket (so the `Key [pill]` doesn't duplicate the existing
        // socket-side pill that already covers the affordance). Output
        // sockets don't compete: they're computed values, never editable
        // inline, so a DefaultProperty key that happens to share a name with
        // an output (e.g. Value.String.Value — DP literal vs output socket)
        // SHOULD still surface as a middle-attr row.
        var socketNames = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var s in _node.Sockets)
            if (s.Type == SocketType.Input && !string.IsNullOrEmpty(s.Name))
                socketNames.Add(s.Name);

        foreach (var kv in tmpl.DefaultProperties)
        {
            if (string.IsNullOrEmpty(kv.Key)) continue;
            if (socketNames.Contains(kv.Key)) continue;

            // Heuristic bool detection — value strings "true" / "false" (case-
            // insensitive) opt the row into the ☑/☐ toggle render. Anything
            // else is a pill. Mirrors the inspector's IsBool inference.
            bool isBool =
                string.Equals(kv.Value, "true",  System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kv.Value, "false", System.StringComparison.OrdinalIgnoreCase);

            var row = new MiddleAttributeViewModel(_node, kv.Key, kv.Value ?? string.Empty, isBool);
            row.PropertyChanged += OnMiddleAttributePropertyChanged;
            MiddleAttributes.Add(row);
        }

        OnPropertyChanged(nameof(HasMiddleAttributes));
    }

    /// <summary>
    /// Mirror of <see cref="OnSocketPropertyChanged"/> for middle-attribute
    /// rows: a value commit (or bool toggle) re-fires Width / Height and the
    /// header bindings so a definer-driving key (EventType / EventName /
    /// VariableName / …) refreshes the italic header subtitle in step.
    /// </summary>
    private void OnMiddleAttributePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        var m = sender as MiddleAttributeViewModel;
        if (e.PropertyName == nameof(MiddleAttributeViewModel.Value) ||
            e.PropertyName == nameof(MiddleAttributeViewModel.BoolValue))
        {
            // The inline pill TextBox binds UpdateSourceTrigger=PropertyChanged,
            // so Value fires on EVERY keystroke. Running the geometry/relayout cascade per
            // keystroke storms the single UI thread (the reported CRON-pill freeze). While the
            // pill is being edited we defer it: the value still writes through to
            // Node.Attributes on each keystroke (no data loss); the one expensive relayout runs
            // once when the edit ends (IsEditing -> false, handled below).
            if (m != null && m.IsEditing) return;
            RaiseInlineLayoutChanged();
        }
        else if (e.PropertyName == nameof(MiddleAttributeViewModel.IsEditing))
        {
            // Push the pill's MaxWidth on edit ENTRY too (see
            // OnSocketPropertyChanged) so the inline TextBox is sized correctly
            // while editing instead of snapping right only on commit.
            if (m != null)
            {
                if (m.IsEditing) m.RaisePillConstraint();
                else RaiseInlineLayoutChanged();
            }
        }
    }

    /// <summary>
    /// The Width / Height / header / pill-constraint relayout cascade fired when an inline value
    /// edit settles. Extracted so the per-keystroke storm can be deferred to a single pass on
    /// edit-commit (see <see cref="OnMiddleAttributePropertyChanged"/> /
    /// <see cref="OnSocketPropertyChanged"/>) instead of re-running on every keystroke.
    /// </summary>
    private void RaiseInlineLayoutChanged()
    {
        InvalidateGeometryCache();
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(Height));
        RaiseAllPillConstraints();
        RaiseHeaderChanged();
        OnPropertyChanged(nameof(HeaderSectionHeight));
    }

    /// <summary>
    /// Re-raise every child pill's <c>PillMaxWidth</c> so each
    /// re-evaluates the width it may wrap within after the node body width changed.
    /// Pills derive their wrap width from the node's EffectiveWidth, so a value edit
    /// on one row (which can grow / shrink the body) must nudge the siblings too.
    /// </summary>
    private void RaiseAllPillConstraints()
    {
        foreach (var s in Inputs)  s.RaisePillConstraint();
        foreach (var s in Outputs) s.RaisePillConstraint();
        foreach (var m in MiddleAttributes) m.RaisePillConstraint();
    }

    /// <summary>
    /// Re-fires layout-dependent properties when a socket's <see cref="SocketViewModel.Label"/>
    /// or <see cref="SocketViewModel.ValuePill"/> changes — width auto-grows to fit, and pill
    /// edits that mutate a "definer" attribute (EventName, VariableName, MacroName, …) also
    /// flip the italic header subtitle. Mirrors pre-T15 NotifyGraphChanged side-effects.
    /// </summary>
    private void OnSocketPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        var s = sender as SocketViewModel;
        if (e.PropertyName == nameof(SocketViewModel.Label) ||
            e.PropertyName == nameof(SocketViewModel.ValuePill) ||
            e.PropertyName == nameof(SocketViewModel.HasValuePill))
        {
            // The socket value-pill TextBox also binds
            // UpdateSourceTrigger=PropertyChanged, so ValuePill fires on every keystroke. Defer
            // the relayout cascade while the pill is being edited (same per-keystroke storm as
            // the middle-attr pill); it runs once on commit (IsEditing -> false, below). Label /
            // HasValuePill changes are not per-keystroke storms, so they re-fire normally.
            if (s != null && s.IsEditing && e.PropertyName == nameof(SocketViewModel.ValuePill)) return;
            InvalidateGeometryCache();
            OnPropertyChanged(nameof(Width));
            OnPropertyChanged(nameof(Height));
            RaiseAllPillConstraints();
            // Pill edits write through to Node.Attributes — if the changed key is one of the
            // definer-driving keys (EventName / VariableName / MacroName / Commands / …),
            // the header subtitle and band height need to re-bind too.
            RaiseHeaderChanged();
            OnPropertyChanged(nameof(HeaderSectionHeight));
        }
        else if (e.PropertyName == nameof(SocketViewModel.IsEditing))
        {
            // The inline editor's MaxWidth={Binding PillMaxWidth} is a
            // OneWay binding that only re-pulls when PillMaxWidth raises
            // PropertyChanged. Pre-fix nothing fired on edit ENTRY, so the TextBox
            // measured at a stale width and only snapped to the correct size on
            // commit (Bug: "text editing field does not scale correctly"). Re-push
            // the constraint on entry; keep the full relayout cascade on exit.
            if (s != null)
            {
                if (s.IsEditing) s.RaisePillConstraint();
                else RaiseInlineLayoutChanged();
            }
        }
        // The reroute diamond no longer recolours on the wildcard
        // cascade's DataType / ColorHex flips: it's a NEUTRAL grey wire knot
        // (fixed fill, selection-driven stroke), not a type-coded bead. The
        // stroke re-raise lives on the IsSelected setter
        // (RaiseRerouteSelectionChanged), so there's nothing to do here.
    }

    /// <summary>
    /// Header band hex colour, kept for non-gradient consumers (compact-mode
    /// glyph backgrounds, future minimap dots, etc.). The actual node header
    /// uses <see cref="HeaderGradientBrush"/> so the painted band matches the
    /// pre-T15 ControlPaint.Light / ControlPaint.Dark fade.
    /// </summary>
    public string HeaderColorHex => "#" +
        _node.HeaderColor.R.ToString("X2") +
        _node.HeaderColor.G.ToString("X2") +
        _node.HeaderColor.B.ToString("X2");

    /// <summary>
    /// Vertical gradient brush from <c>ControlPaint.Light(HeaderColor, 0.05)</c>
    /// (top) to <c>ControlPaint.Dark(HeaderColor, 0.25)</c> (bottom). Mirrors
    /// pre-T15 DrawNodes header fill so a node's title band has a subtle
    /// depth cue instead of a flat tint.
    ///
    /// PERF: pre-cache, the getter built a
    /// fresh LinearGradientBrush + 2 GradientStops on every binding read.
    /// Bindings re-evaluate on every NotifyPropertyChanged from a HeaderColor
    /// dependency (IsExecutingFlash, IsVarChainWriter, etc.), so the same
    /// gradient was reconstructed N times per node per interaction. Memoise
    /// keyed by HeaderColor; the <c>_cachedHeaderGradientKey == key</c>
    /// comparison IS the invalidation — a HeaderColor change makes the
    /// equality fail and the gradient rebuilds on the next read.
    /// Removed stale reference to a ResetHeaderGradientCache
    /// method that never existed.
    /// </summary>
    public Microsoft.UI.Xaml.Media.Brush HeaderGradientBrush
    {
        get
        {
            var key = _node.HeaderColor;
            if (_cachedHeaderGradient is { } cached && _cachedHeaderGradientKey == key)
                return cached;

            var top    = ControlPaintLight(key, 0.05);
            var bottom = ControlPaintDark(key, 0.25);
            var gb = new Microsoft.UI.Xaml.Media.LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint   = new Windows.Foundation.Point(0, 1),
            };
            gb.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = top,    Offset = 0.0 });
            gb.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = bottom, Offset = 1.0 });

            _cachedHeaderGradient    = gb;
            _cachedHeaderGradientKey = key;
            return gb;
        }
    }

    private Microsoft.UI.Xaml.Media.Brush? _cachedHeaderGradient;
    private System.Drawing.Color _cachedHeaderGradientKey;

    /// <summary>
    /// Mirror of <c>System.Windows.Forms.ControlPaint.Light(color, factor)</c> —
    /// blends toward white by <paramref name="factor"/> in [0,1].
    /// </summary>
    private static Windows.UI.Color ControlPaintLight(System.Drawing.Color c, double factor)
    {
        byte r = (byte)System.Math.Min(255.0, c.R + (255 - c.R) * factor);
        byte g = (byte)System.Math.Min(255.0, c.G + (255 - c.G) * factor);
        byte b = (byte)System.Math.Min(255.0, c.B + (255 - c.B) * factor);
        return Windows.UI.Color.FromArgb(0xFF, r, g, b);
    }

    /// <summary>
    /// Mirror of <c>System.Windows.Forms.ControlPaint.Dark(color, factor)</c> —
    /// blends toward black by <paramref name="factor"/> in [0,1].
    /// </summary>
    private static Windows.UI.Color ControlPaintDark(System.Drawing.Color c, double factor)
    {
        byte r = (byte)System.Math.Max(0.0, c.R * (1 - factor));
        byte g = (byte)System.Math.Max(0.0, c.G * (1 - factor));
        byte b = (byte)System.Math.Max(0.0, c.B * (1 - factor));
        return Windows.UI.Color.FromArgb(0xFF, r, g, b);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetField(ref _isSelected, value))
                // Reroute diamond's stroke is selection-driven
                // (Gold when selected, DimGray else). No-op for standard nodes.
                RaiseRerouteSelectionChanged();
        }
    }

    private bool _isExecutingFlash;
    /// <summary>
    /// Live-debug-trace flash flag — set briefly when DEBUG_NODE_EXEC arrives
    /// for this node id. NodeView reacts by tinting the border yellow until
    /// the canvas auto-clears the flag (~400ms later).
    /// </summary>
    public bool IsExecutingFlash
    {
        get => _isExecutingFlash;
        set => SetField(ref _isExecutingFlash, value);
    }

    private int _flashTick;
    /// <summary>
    /// Monotonic counter that increments every time a flash is requested.
    /// PropertyChanged always fires (unlike <see cref="IsExecutingFlash"/>
    /// which short-circuits in SetField when toggled <c>true→true</c>),
    /// so bursts on the same node — tight loops, rapid event chains — restart
    /// the storyboard instead of silently dropping the second pulse.
    /// </summary>
    public int FlashTick
    {
        get => _flashTick;
        private set => SetField(ref _flashTick, value);
    }

    /// <summary>
    /// Request a flash. Bumps <see cref="FlashTick"/> unconditionally so
    /// re-entrant flashes always re-fire the view's storyboard, and lifts
    /// <see cref="IsExecutingFlash"/> so the priority cascade in
    /// <c>NodeView.ApplyBorder</c> picks the flash branch.
    /// </summary>
    public void TriggerFlash()
    {
        FlashTick = unchecked(_flashTick + 1);
        IsExecutingFlash = true;
    }

    private bool _isVarChainWriter;
    /// <summary>True when this node writes the currently-hovered or picked
    /// variable. NodeView paints a cyan border + outer glow halo. Set by
    /// LogicCanvasViewModel.ApplyVarChainHighlights.</summary>
    public bool IsVarChainWriter
    {
        get => _isVarChainWriter;
        set => SetField(ref _isVarChainWriter, value);
    }

    private bool _isVarChainReader;
    /// <summary>True when this node reads the currently-hovered or picked
    /// variable. NodeView paints an amber border + outer glow halo.</summary>
    public bool IsVarChainReader
    {
        get => _isVarChainReader;
        set => SetField(ref _isVarChainReader, value);
    }

    private bool _isDimmedByPicker;
    /// <summary>True when a sticky var-chain picker is active and this node
    /// is NOT a writer or reader of the picked variable. NodeView paints a
    /// translucent grey overlay so the chain pops globally.</summary>
    public bool IsDimmedByPicker
    {
        get => _isDimmedByPicker;
        set => SetField(ref _isDimmedByPicker, value);
    }

    private bool _isErrorState;
    /// <summary>
    /// Set true when the node is in a static-validation error state — today
    /// driven by an unpaired Event.Trigger / Event.Executor (a non-empty
    /// EventName attribute with no peer of the opposite role in the graph
    /// nor any sibling .phxg file). NodeView observes the flag and paints a
    /// red border. Pre-fix this surface didn't exist on the WinUI canvas
    /// (the WinForms canvas computed a `_errorNodeIds` set and drew through
    /// custom OnPaint), so authoring slips were invisible until a script
    /// failed to fire at runtime.
    /// </summary>
    public bool IsErrorState
    {
        get => _isErrorState;
        set => SetField(ref _isErrorState, value);
    }

    /// <summary>
    /// Per-node "Disable Node" toggle. Wraps the
    /// <c>__disabled</c> entry on <see cref="Node.Attributes"/> so the flag
    /// round-trips through .phxg save/load via the existing attribute
    /// serializer. NodeView paints disabled nodes at reduced opacity so the
    /// authoring surface reads them as inert. Script-engine consumption of
    /// the flag lands in a follow-up sprint — see the
    /// <c>// TODO(skip-disabled-nodes)</c> comment in LogicCanvasView.Menus.cs.
    /// </summary>
    public bool IsDisabled
    {
        // Node.Attributes can be null after System.Text.Json
        // deserialisation of a `"Attributes": null` graph (the same edge
        // GraphSerializer.MigrateNodes documents + heals elsewhere). Guard
        // the read with the null-conditional + lazily initialise on first
        // write so a disabled-toggle on a node with a null bag doesn't throw
        // a NullReferenceException out of a binding-driven setter.
        get => _node.Attributes != null
            && _node.Attributes.TryGetValue("__disabled", out var v)
            && string.Equals(v, "true", System.StringComparison.OrdinalIgnoreCase);
        set
        {
            bool current = IsDisabled;
            if (current == value) return;
            _node.Attributes ??= new Dictionary<string, string>();
            if (value) _node.Attributes["__disabled"] = "true";
            else        _node.Attributes.Remove("__disabled");
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Move the underlying Node by the given canvas-space delta. Raises
    /// PropertyChanged for X/Y so a binding view repositions automatically. Used
    /// by drag handlers.
    /// </summary>
    public void Translate(double dx, double dy)
    {
        var loc = _node.Location;
        _node.Location = new System.Drawing.Point(
            (int)System.Math.Round(loc.X + dx),
            (int)System.Math.Round(loc.Y + dy));
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
    }

    /// <summary>
    /// Find the socket VM by id, searching both Inputs and Outputs. Used by
    /// LinkViewModel when resolving link endpoints to anchor coordinates.
    /// Returns null if the socket isn't part of this node.
    /// </summary>
    public SocketViewModel? FindSocket(string socketId)
    {
        foreach (var s in Inputs)  if (s.Id == socketId) return s;
        foreach (var s in Outputs) if (s.Id == socketId) return s;
        return null;
    }

}
