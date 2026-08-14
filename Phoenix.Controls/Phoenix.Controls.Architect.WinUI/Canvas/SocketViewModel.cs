using Phoenix.Controls.Architect.Core;
using Phoenix.Controls.Architect.WinUI.ViewModels;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

/// <summary>
/// Drag-time drop-target state for a socket pin. <see cref="None"/> is the
/// resting state — no wire is being dragged, or this socket is not a
/// candidate target. <see cref="Valid"/> / <see cref="Invalid"/> are set by
/// <c>LogicCanvasViewModel.BeginDropHinting</c> when the dragged source
/// passes / fails the compat check against this socket, and drive the
/// green / red halo NodeView paints around the pin.
/// </summary>
public enum DropState { None, Valid, Invalid }

// View-model for a single socket pin row. Wraps a Socket together
// with the node-relative row index and the inline-pill value (which lives
// on the parent Node.Attributes dictionary keyed by socket name).
//
// Inline value pill: hardcoded
// socket values render directly inside the node body — UE Blueprints
// style — not in a side panel. The view binds to ValuePill / IsEditing,
// shows a TextBox when IsEditing flips true, and commits via
// CommitValuePillEdit which writes back to Node.Attributes and persists
// on the next .phxg save.
public sealed class SocketViewModel : ObservableObject
{
    private readonly Socket _socket;
    private readonly Node _parentNode;
    private string? _valuePill;
    private bool _isEditing;

    // PERF: previously Kind/ColorHex/PinPathData/
    // PinFilled/FillColorHex were all property getters that re-evaluated the
    // SocketPalette / PinPathGeometry switch tables on every binding read. With
    // M input + N output sockets per node × K nodes × ~3 bindings per pin per
    // layout pass, plus wildcard-cascade fan-out, this was a measurable per-
    // render cost. The data they depend on (Socket.DataType + Socket.IsPlaceholder)
    // only changes through NotifyDataTypeChanged, so backing-field caching is
    // safe — refresh on construct + on NotifyDataTypeChanged.
    private SocketPinKind _kind;
    private string _colorHex = string.Empty;
    private string _pinPathData = string.Empty;
    private bool _pinFilled;
    private string _fillColorHex = "#00000000";

    public SocketViewModel(Node parentNode, Socket socket, int rowIndex)
    {
        _parentNode = parentNode;
        _socket = socket;
        RowIndex = rowIndex;
        _valuePill = ResolveInitialValuePill(parentNode, socket);
        // IsRequired must resolve BEFORE RebuildPinDerived — the fill rule
        // (solid=required / hollow=optional, per Majo's socket legend) reads it.
        IsRequired = ResolveIsRequired(parentNode, socket);
        RebuildPinDerived();
    }

    private static bool ResolveIsRequired(Node node, Socket socket)
    {
        // Only data-input pins can be "required-but-empty" — outputs are
        // computed values, flow pins are execution wires (their wiring rule
        // is enforced by the static-validation pass, not the halo).
        if (socket.Type != SocketType.Input) return false;
        if (string.IsNullOrEmpty(socket.Name)) return false;
        if (SocketTypeHelper.IsFlowPin(socket)) return false;
        try
        {
            var t = NodeRegistry.GetTemplate(node.Title);
            return t?.RequiredInputs?.Contains(socket.Name) ?? false;
        }
        catch
        {
            // Registry miss / shutdown race — treat as "not required" rather
            // than throw from a view-model getter.
            return false;
        }
    }

    private void RebuildPinDerived()
    {
        _kind         = SocketPalette.KindFor(_socket.DataType);
        _colorHex     = SocketPalette.HexFor(_socket.DataType);
        _pinPathData  = PinPathGeometry.PathFor(_kind);
        // 0.11.x polish — fill rule keyed on CONNECTIVITY, not on the prior
        // required/optional distinction. Majo's restated spec: "Filling out
        // of connected bubbles vs hollow ones for not connected bubbles" —
        // every pin renders SOLID when it carries at least one wire and
        // HOLLOW (colored outline only) when it doesn't, regardless of
        // whether the socket is required, optional, input or output. The
        // required-vs-empty signal still surfaces via the yellow
        // PinRequiredHaloBrush ring (IsRequiredEmpty) so the two affordances
        // don't fight for the fill channel.
        //
        // Placeholders ("+ add socket") and Mismatch pins continue to render
        // hollow regardless of connectivity — placeholders never carry real
        // wires, and a Mismatch pin marks a data-type mismatch we want the
        // user to notice and fix rather than colour-fill over.
        _pinFilled    = _isConnected
                        && !_socket.IsPlaceholder
                        && _kind != SocketPinKind.Mismatch;
        _fillColorHex = _pinFilled ? _colorHex : "#00000000";
    }

    public Socket Model => _socket;
    /// <summary>The Node that owns this socket — used by the inline pill autocomplete
    /// to seed <see cref="Phoenix.Controls.Architect.Core.AutocompleteScopeBuilder"/>'s
    /// upstream walk so event.* / loop.* / result.* tokens surface only when valid.</summary>
    public Node ParentNode => _parentNode;

    public string Id    => _socket.Id;

    /// <summary>
    /// Socket display name. TwoWay-bindable: writing back updates Socket.Name
    /// so inline rename on the node body persists into the .phxg on next save
    /// (UE-Blueprints rename-on-node idiom).
    /// <para>
    /// This setter performs the model-side rename + Attributes
    /// key migration but does NOT push an undo snapshot. The undo push is
    /// the CALLER's responsibility — specifically the canvas-side rename
    /// commit path (NodeView's EndLabelEdit handler) wraps a Label setter
    /// write in PushUndo when EndLabelEdit returns changed=true. Direct
    /// programmatic writes (graph migration, exporter back-fill, headless
    /// tests) bypass undo deliberately; UI rename paths must go through
    /// <see cref="BeginLabelEdit"/> + <see cref="EndLabelEdit"/> so the
    /// caller has a single commit point to push undo at.
    /// </para>
    /// </summary>
    public string Label
    {
        get => _socket.Name ?? string.Empty;
        set
        {
            var v = value ?? string.Empty;
            var oldName = _socket.Name ?? string.Empty;
            if (oldName == v) return;
            _socket.Name = v;

            // Migrate the inline-pill attribute key — pre-fix renaming a
            // socket silently orphaned its value because Node.Attributes
            // still keyed on the old name, and ResolveInitialValuePill
            // returned null on the next view rebuild.
            if (!string.IsNullOrEmpty(oldName)
                && _parentNode.Attributes != null
                && _parentNode.Attributes.TryGetValue(oldName, out var carried))
            {
                _parentNode.Attributes.Remove(oldName);
                if (!string.IsNullOrEmpty(v))
                    _parentNode.Attributes[v] = carried;
                // Pill content is semantically unchanged but its dictionary
                // key moved; nudge derived properties so the body redraws
                // with the same pill text under the new label.
                OnPropertyChanged(nameof(ValuePill));
                OnPropertyChanged(nameof(HasValuePill));
                OnPropertyChanged(nameof(IsMultilineAttr));
                OnPropertyChanged(nameof(PillMaxWidth));
                OnPropertyChanged(nameof(PillTextWrapping));
            }

            // Dynamic-placeholder detection keys off the leading "+" in the
            // socket name, so a rename can flip the pin from dashed
            // (dynamic) to solid-outline (static placeholder) or back. Always
            // nudge — cheap, and the binding short-circuits when the value
            // hasn't actually changed.
            OnPropertyChanged(nameof(IsDynamicPlaceholder));
            // Tooltip composes Label + DataType +
            // Description; renaming the socket changes the head line, so the
            // computed property needs an explicit nudge or the hover popup
            // stays stuck on the pre-rename string. Same shape as the
            // IsDynamicPlaceholder fire above — cheap, idempotent on
            // unchanged values.
            OnPropertyChanged(nameof(Tooltip));
            OnPropertyChanged(nameof(AccessibleName));
            OnPropertyChanged();
        }
    }

    public SocketType Direction => _socket.Type;
    public SocketDataType DataType => _socket.DataType;
    public bool IsPlaceholder => _socket.IsPlaceholder;
    public string? Description => _socket.Description;

    public int RowIndex { get; }

    public SocketPinKind Kind        => _kind;
    public string        ColorHex    => _colorHex;
    public string        PinPathData => _pinPathData;
    public bool          PinFilled   => _pinFilled;

    /// <summary>
    /// Hex string fed to NodeView.Pins' code-behind HexBrush helper for the pin's
    /// <c>Path.Fill</c>. When <see cref="PinFilled"/> is true, this is the
    /// socket's data-type colour (filled pin); otherwise <c>#00000000</c>
    /// renders as transparent so only the outline shows. Pre-TODO sweep
    /// the XAML hardcoded <c>Color="Transparent"</c>, so connected /
    /// non-placeholder pins always rendered as outlines (TODO.md P1 #4).
    /// </summary>
    public string FillColorHex => _fillColorHex;

    /// <summary>
    /// Multi-line tooltip rendered on socket-pin hover: "{Name} · {DataType}"
    /// header followed by the socket description when present. Mirrors pre-T15
    /// canvas tooltip resolver output for sockets.
    /// <para>
    /// The property reads <see cref="Label"/>,
    /// <see cref="DataType"/>, and <see cref="Description"/> directly off the
    /// underlying <see cref="Socket"/> POCO, so any code path that mutates
    /// those fields must explicitly fire <see cref="OnPropertyChanged"/>
    /// for <c>Tooltip</c> (or call <see cref="RefreshTooltip"/>) or the
    /// hover popup will stay stuck on the pre-mutation value. The Label
    /// setter, <see cref="NotifyDataTypeChanged"/>, and
    /// <see cref="NotifyDynamicPlaceholderChanged"/> all already do this;
    /// <see cref="RefreshTooltip"/> is exposed for any future mutation
    /// path that touches Socket.Description directly.
    /// </para>
    /// </summary>
    public string Tooltip
    {
        get
        {
            var head = $"{Label} · {DataType}";
            return string.IsNullOrEmpty(Description) ? head : $"{head}\n{Description}";
        }
    }

    /// <summary>
    /// Explicit re-raise hook for <see cref="Tooltip"/>.
    /// The computed property reads <see cref="Socket.Name"/>,
    /// <see cref="Socket.DataType"/>, and <see cref="Socket.Description"/>
    /// directly, so any mutation path that bypasses the <see cref="Label"/>
    /// setter / <see cref="NotifyDataTypeChanged"/> (e.g. an authoring tool
    /// that rewrites a socket's <c>Description</c> in place) must call this
    /// or hover will keep showing the pre-mutation string.
    /// </summary>
    public void RefreshTooltip() => OnPropertyChanged(nameof(Tooltip));

    /// <summary>
    /// Screen-reader label for the pin: "Input · String · username" /
    /// "Output · Flow". Surfaced via the NodeView pin's
    /// AutomationProperties.Name so a Narrator user knows direction +
    /// type + name without parsing the visual chrome.
    /// </summary>
    public string AccessibleName
    {
        get
        {
            var dir = Direction == SocketType.Input
                ? Localizer.T("architect.canvas.socket.direction.input",  "Input")
                : Localizer.T("architect.canvas.socket.direction.output", "Output");
            return $"{dir} · {DataType} · {Label}";
        }
    }

    /// <summary>
    /// Wildcard socket — type still unresolved (<c>SocketDataType.Any</c>).
    /// NodeView paints these as a hollow dashed outline so the unresolved
    /// state is visually distinct from a connected typed pin even before the
    /// wildcard cascade runs.
    /// </summary>
    public bool IsWildcard => _socket.DataType == SocketDataType.Any;

    /// <summary>
    /// Unactivated dynamic socket — the <c>+ variable</c> / <c>+ return</c>
    /// rows on <c>Event.Trigger</c> / <c>Event.Executor</c> / <c>Macro.Entry</c>
    /// / <c>Macro.Exit</c>. These render with a dashed outline so the
    /// "drop a wire here to grow another row" affordance reads at a glance,
    /// distinct from a static-placeholder pin (which gets a solid outline
    /// only — same unfilled treatment, no dash).
    ///
    /// Detected via the leading <c>+</c> marker on the socket name — every
    /// NodeRegistry factory that produces these rows seeds the name with
    /// that prefix (see <c>NodeRegistry.Templates.CreateDynamicEventNode</c>
    /// / <c>CreateMacroEntryExitNode</c>). The model-side
    /// <see cref="Phoenix.Controls.Shared.Models.Socket.IsPlaceholder"/>
    /// flag stays the gate for "this pin can't accept a wire yet"; the
    /// dynamic-vs-static distinction is a render-side concern only.
    /// </summary>
    public bool IsDynamicPlaceholder
        => _socket.IsPlaceholder
           && !string.IsNullOrEmpty(_socket.Name)
           && _socket.Name.TrimStart().StartsWith("+", System.StringComparison.Ordinal);

    /// <summary>
    /// Explicit re-raise hook for <see cref="IsDynamicPlaceholder"/> .
    /// The property is computed off the underlying <c>Socket</c> POCO so its
    /// value can change without a Label setter fire — namely when the engine
    /// flips <c>Socket.IsPlaceholder</c> on activation /
    /// <c>RevertActivatedPlaceholderIfOrphaned</c> (Architect/Core). The
    /// Label-setter path already nudges, but a model-side flip would otherwise
    /// leave the dashed-outline binding stale until the next graph mutation
    /// caused a wider rebind. Call this from the cascade that toggles
    /// <c>IsPlaceholder</c> so the dashed-vs-solid affordance refreshes
    /// immediately.
    /// </summary>
    public void NotifyDynamicPlaceholderChanged()
        => OnPropertyChanged(nameof(IsDynamicPlaceholder));

    /// <summary>
    /// Vector arity (2 / 3 / 4) for <c>Vector2 / Vector3 / Vector4</c>
    /// sockets, 0 otherwise. Drives the small "2" / "3" / "4" glyph
    /// NodeView paints inside the pin so the three vector kinds — which
    /// otherwise share the same lavender diamond — read at a glance.
    /// </summary>
    public int VectorArity => SocketPalette.ArityFor(_socket.DataType);

    /// <summary>
    /// Convenience visibility flag for the arity glyph — true iff
    /// <see cref="VectorArity"/> &gt; 0. Surfaced as a bool because XAML's
    /// <c>BoolVis</c> converter is already wired everywhere; XAML can also
    /// bind the badge's <c>Text</c> directly to <c>VectorArity</c>
    /// (Int-to-String is implicit).
    /// </summary>
    public bool HasVectorArity => VectorArity > 0;

    // ─── Required-input + drop-target halo + dash chrome ─────────────────
    //
    // Three independent state machines drive the halo / outline / dash
    // overlays NodeView paints around each pin:
    //
    //   1. IsRequired + IsConnected — static, derived from the NodeTemplate.
    //      A required input with no link gets a yellow halo so authoring
    //      gaps are visible at a glance.
    //   2. DropState — transient, set during a wire-drag by the canvas.
    //      Green halo on Valid drop targets, red on Invalid.
    //   3. IsPlaceholder + IsWildcard — static, derived from the Socket.
    //      Drives the StrokeDashArray on the pin path itself.
    //
    // The three states are mutually visible (an unwired required input can
    // also be a valid drop target during a drag), so each binds its own
    // overlay layer in NodeView.xaml rather than competing for a single
    // brush slot.

    /// <summary>
    /// True when the parent node's <see cref="NodeTemplate"/> lists this
    /// socket's name in <see cref="NodeTemplate.RequiredInputs"/>. Output
    /// sockets and flow inputs never count as required — only data-input
    /// pins. Cached on construct (template lookup is by node title, which
    /// doesn't change for the lifetime of the socket VM).
    /// <para>
    /// The cache assumes NodeTemplate.RequiredInputs is immutable
    /// at runtime, which holds today — templates are seeded once in
    /// NodeRegistry's static initialiser and never mutated. If a future
    /// template-hot-edit feature lands (think: per-node "Mark Required"
    /// inspector toggle), refresh this value on every template-change event
    /// by re-calling <see cref="ResolveIsRequired"/> and re-firing
    /// PropertyChanged for both <c>IsRequired</c> and <c>IsRequiredEmpty</c>.
    /// </para>
    /// </summary>
    public bool IsRequired { get; private set; }

    private bool _isConnected;
    /// <summary>
    /// True when at least one link is attached to this socket. Set by
    /// <c>LogicCanvasViewModel.SyncSocketConnectivity</c> on graph load and
    /// after every wire add / remove. Drives the
    /// required-but-empty yellow halo together with <see cref="IsRequired"/>.
    /// </summary>
    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (SetField(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(IsRequiredEmpty));
                // IsEditablePill is gated on
                // !_isConnected so the pill stops accepting taps the moment
                // a wire attaches; IsPillVisible stays true so the ghost-pill
                // renders for the wired-source intrinsic value. Both bindings
                // need a nudge whenever connectivity flips so the pill goes
                // editable ↔ ghost in step with the wire add/remove.
                // PillDisplayText also depends on _isConnected (shows "(wired)"
                // when wired + empty literal); fire it too so the read-only
                // text swaps in step with the opacity flip.
                OnPropertyChanged(nameof(IsEditablePill));
                OnPropertyChanged(nameof(IsPillVisible));
                OnPropertyChanged(nameof(PillOpacity));
                OnPropertyChanged(nameof(PillDisplayText));
                // Fallback pill (Math operators etc.) keeps IsEditablePill
                // true even while wired; the IsPillFallback flag drives the
                // dimmed-border visual indicator that distinguishes a fallback
                // pill from a normal editable pill.
                OnPropertyChanged(nameof(IsPillFallback));
                // 0.11.x polish — connectivity is now load-bearing for the
                // pin fill (solid when wired, hollow when not). Recompute the
                // cached _pinFilled / _fillColorHex pair and nudge the
                // NodeView-side bindings so the bubble repaints in lockstep
                // with the wire add/remove.
                RebuildPinDerived();
                OnPropertyChanged(nameof(PinFilled));
                OnPropertyChanged(nameof(FillColorHex));
            }
        }
    }

    /// <summary>
    /// True iff this is a required input that has no link attached. The
    /// canvas paints a yellow ring around the pin so the missing wire is
    /// visible at a glance during authoring.
    /// </summary>
    public bool IsRequiredEmpty => IsRequired && !_isConnected;

    private DropState _dropState;
    /// <summary>
    /// Drop-target state for the current wire-drag. None at rest; flipped
    /// to Valid / Invalid by <c>LogicCanvasViewModel.BeginDropHinting</c>
    /// while a wire is being dragged from a source socket. NodeView binds
    /// this through <c>DropStateToHaloBrush</c> + <c>DropStateToHaloVisibility</c>
    /// converters so the green / red halo appears around the pin without
    /// touching the pin's intrinsic stroke / fill.
    /// <para>
    /// Per-pin halo state lives on the Socket VM; the related
    /// per-NODE highlight flags (<see cref="NodeViewModel.IsVarChainWriter"/>,
    /// <see cref="NodeViewModel.IsVarChainReader"/>,
    /// <see cref="NodeViewModel.IsDimmedByPicker"/>) live on the parent
    /// NodeViewModel because they paint the whole node's chrome, not just
    /// a single pin. Two surfaces, two state machines — by design, not a
    /// missing-feature gap.
    /// </para>
    /// </summary>
    public DropState DropState
    {
        get => _dropState;
        set => SetField(ref _dropState, value);
    }

    /// <summary>
    /// Re-raise PropertyChanged for everything derived from the underlying
    /// Socket.DataType. Called by the canvas after NodeRegistry's
    /// wildcard cascade resolves new types on a wire-drop — the model field
    /// has changed but property getters are stateless, so the view-side
    /// bindings need an explicit nudge.
    ///
    /// IsDynamicPlaceholder is folded in defensively because the
    /// wildcard cascade and the placeholder-activation paths frequently
    /// fire in lockstep — the Label-setter notification handles the rename
    /// case, but a DataType-only flip (e.g. cascade resolving Any → String
    /// on a + placeholder pin) would otherwise leave the dashed-vs-solid
    /// affordance stale until the next graph mutation. Pinning the
    /// notification here costs nothing because the binding short-circuits
    /// when the value hasn't actually changed.
    /// </summary>
    public void NotifyDataTypeChanged()
    {
        RebuildPinDerived();
        OnPropertyChanged(nameof(DataType));
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(ColorHex));
        OnPropertyChanged(nameof(PinPathData));
        OnPropertyChanged(nameof(PinFilled));
        OnPropertyChanged(nameof(FillColorHex));
        OnPropertyChanged(nameof(IsWildcard));
        OnPropertyChanged(nameof(VectorArity));
        OnPropertyChanged(nameof(HasVectorArity));
        OnPropertyChanged(nameof(IsPlaceholder));
        OnPropertyChanged(nameof(IsDynamicPlaceholder));
        // Tooltip embeds DataType; cascade-driven
        // type flips would otherwise leave the hover popup stale until the
        // next Label rename forced a Tooltip re-raise.
        OnPropertyChanged(nameof(Tooltip));
    }

    /// <summary>
    /// Inline value (e.g. Logic.If's "!hello") rendered as a small mono pill
    /// after the label on input rows. Null when no inline value is set —
    /// in that case the row shows only the pin + label.
    /// </summary>
    public string? ValuePill
    {
        get => _valuePill;
        set
        {
            if (SetField(ref _valuePill, value))
            {
                _parentNode.Attributes ??= new();
                if (string.IsNullOrEmpty(value))
                    _parentNode.Attributes.Remove(_socket.Name ?? string.Empty);
                else
                    _parentNode.Attributes[_socket.Name ?? string.Empty] = value!;
                OnPropertyChanged(nameof(HasValuePill));
                OnPropertyChanged(nameof(PillDisplayText));
                // Newline content can flip the multi-line affordance on a Template/Script/Payload-
                // adjacent socket; re-fire so the editor swaps to the wrapped layout on commit
                // and the XAML MaxWidth binding flips between the multi-line cap and uncapped.
                OnPropertyChanged(nameof(IsMultilineAttr));
                OnPropertyChanged(nameof(PillMaxWidth));
                OnPropertyChanged(nameof(PillTextWrapping));
            }
        }
    }

    public bool HasValuePill => !string.IsNullOrEmpty(_valuePill);

    /// <summary>
    /// True when the inline pill should accept user clicks + edits — i.e. it
    /// renders as the normal yellow-on-coal editable Border, not the dimmed
    /// ghost-pill. This used to be the visibility gate too; the two concerns
    /// were split so wired sockets keep showing a pill (as a ghost) without
    /// allowing edits that would silently disagree with the wired source.
    ///
    /// Editable iff: input direction, has a name, not a placeholder, not a
    /// flow pin, AND no wire is attached. The connectivity gate is the
    /// addition — wired pills route through
    /// <see cref="IsPillVisible"/> for the ghost render but reject taps via
    /// the IsHitTestVisible XAML binding so a user clicking a ghost-pill
    /// gets no edit-mode flip (which would be confusing — the value the
    /// user typed would be silently overridden by the wired source at
    /// export time).
    ///
    /// Excludes outputs (computed values), placeholders, and flow pins
    /// (execution wires don't carry literal values).
    /// </summary>
    public bool IsEditablePill
    {
        get
        {
            if (_socket.Type != SocketType.Input) return false;
            if (string.IsNullOrEmpty(_socket.Name)) return false;
            if (_socket.IsPlaceholder) return false;
            if (SocketTypeHelper.IsFlowPin(_socket)) return false;
            // Math operators (and
            // other fallback-pill nodes) keep their
            // inline default editable even when wired, so the pill acts as
            // a runtime fallback: the wired upstream value wins at export
            // time, but the literal in the pill is what the runtime falls
            // back to when the upstream resolves to null/empty. The
            // IsFallbackPillNode predicate gates this widening so the rule
            // doesn't accidentally apply to wired Twitch.ChatMessage event
            // payloads or other "wire-only" sockets where keeping a stale
            // literal editable would confuse author intent.
            //
            // PillIsFallback paints a dimmer/lighter border on the pill
            // (via IsPillFallback) so the author sees at a glance that this
            // pill is the fallback rather than the live value.
            if (_isConnected && !IsFallbackPillNode()) return false;
            return true;
        }
    }

    /// <summary>
    /// 0.11.x polish — kind of databank picker this socket should surface.
    /// DB.* nodes whose pill drives a TableName / Column / RowId attribute
    /// get a dropdown affordance so the user can pick a live value from
    /// the SQLite databank without typing (and without typos). Restoring
    /// pre-WinUI parity with MainForm's column-header drag-to-canvas
    /// menu, but inline on the node body.
    /// </summary>
    public enum DatabankPickerKindValue
    {
        None,
        Tables,
        Columns,
        // Giveaway selector drop-down (Giveaway.IsActive): lists the giveaways
        // from the shared databank plus a "(default giveaway)" entry that
        // clears the pill — an empty selector means "follow the default".
        Giveaways,
    }

    /// <summary>
    /// Resolve the databank-picker affordance kind for this socket. Fires
    /// for input sockets on DB.* nodes whose name is one of the well-known
    /// databank attribute keys, plus Giveaway.Ticket's PriceTable (the
    /// currency table channel points are charged from — same Tables list,
    /// with an extra one-click "Create 'ChannelPoints' table" item appended
    /// by NodeView's picker population). The picker is independent of
    /// IsEditablePill — a wired socket still benefits from the dropdown
    /// so the author can correct a typo without disconnecting the wire.
    /// </summary>
    public DatabankPickerKindValue DatabankPickerKind
    {
        get
        {
            if (_socket.Type != SocketType.Input) return DatabankPickerKindValue.None;
            if (string.IsNullOrEmpty(_socket.Name)) return DatabankPickerKindValue.None;
            var title = _parentNode.Title ?? string.Empty;
            if (title == "Giveaway.Ticket")
                return _socket.Name == "PriceTable"
                    ? DatabankPickerKindValue.Tables
                    : DatabankPickerKindValue.None;
            if (title == "Giveaway.IsActive")
                return _socket.Name == "Giveaway"
                    ? DatabankPickerKindValue.Giveaways
                    : DatabankPickerKindValue.None;
            if (!title.StartsWith("DB.", System.StringComparison.Ordinal))
                return DatabankPickerKindValue.None;
            return _socket.Name switch
            {
                "TableName" => DatabankPickerKindValue.Tables,
                "Column"    => DatabankPickerKindValue.Columns,
                _           => DatabankPickerKindValue.None,
            };
        }
    }

    /// <summary>
    /// True when <see cref="DatabankPickerKind"/> resolves to anything
    /// other than None. Bound by NodeView.xaml's input-pill row to flip
    /// the ▾ chevron button visible / collapsed.
    /// </summary>
    public bool HasDatabankPicker => DatabankPickerKind != DatabankPickerKindValue.None;

    /// <summary>
    /// Parent node's current <c>TableName</c> attribute, or null when not
    /// set. Used by the Columns picker to scope the column list to a
    /// specific table; null means the picker shows columns from any table
    /// (rare — most DB nodes pair TableName + Column).
    /// </summary>
    public string? ParentTableNameAttr
        => _parentNode.Attributes is { } a && a.TryGetValue("TableName", out var v) ? v : null;

    /// <summary>
    /// The set
    /// of node templates whose input pills behave as "default-if-upstream-
    /// is-null" fallbacks. When a socket on one of these nodes is wired AND
    /// carries a literal, the literal stays editable (the pill renders with
    /// a dimmed-border indicator) so the author can refine the fallback
    /// without first having to disconnect the wire. At export time the
    /// wired upstream value still wins; the literal only fires when the
    /// upstream resolves to null/empty.
    /// </summary>
    private bool IsFallbackPillNode()
    {
        var title = _parentNode?.Title ?? string.Empty;
        return title switch
        {
            // Math operators + reducers + clampers.
            "Math.Add" or "Math.Subtract" or "Math.Multiply" or "Math.Divide"
                or "Math.Modulo" or "Math.Clamp" or "Math.Min" or "Math.Max"
                or "Math.Floor" or "Math.Ceil" or "Math.Abs" => true,
            // Math.Random / Math.Chance min/max bounds.
            "Math.Random" or "Math.Chance" => true,
            // Chat sender (unified) + Twitch lookup + legacy sender nodes
            // (Username / Message fallbacks).
            "Chat.Send"
                or "Twitch.SendChat" or "Twitch.GetUser" or "Twitch.GetStream"
                or "Twitch.CheckRole" or "Twitch.GetFollowAge"
                or "Twitch.LastActive" or "Twitch.GetViewers" => true,
            // Discord channel / guild / user / message fallbacks.
            "Discord.SendMessage" or "Discord.SendEmbed" or "Discord.AddRole"
                or "Discord.RemoveRole" or "Discord.React"
                or "Discord.GetUser" => true,
            // API.Call URL fallback.
            "API.Call" => true,
            // File.* Path fallback.
            "File.ReadText" or "File.ReadJSON" => true,
            // Text.Builder Template surfaces inline (still fallback-
            // editable when wired so authors can sketch the template before
            // hooking up the data sources).
            "Text.Builder" => true,
            _ => false,
        };
    }

    /// <summary>
    /// True iff the pill is
    /// currently rendering as a "fallback" (the socket is wired but the
    /// literal stays editable per the fallback-pill widening). NodeView
    /// XAML binds the pill's BorderBrush opacity to this so the author
    /// sees a dimmer outline on the fallback-mode pill, distinguishing it
    /// from the normal "no wire, this is the only value" editable state.
    /// </summary>
    public bool IsPillFallback => _isConnected && IsFallbackPillNode();

    /// <summary>
    /// Visibility gate for the inline pill. True for
    /// every input socket that could carry a literal value (input, named,
    /// non-placeholder, non-flow) whether or not a wire is currently
    /// attached. When the socket is unwired this overlaps exactly with
    /// <see cref="IsEditablePill"/> and the pill renders as the normal
    /// editable Border. When the socket is wired, <see cref="IsEditablePill"/>
    /// flips false so the Border becomes a read-only ghost-pill (dimmed via
    /// <see cref="PillOpacity"/>, IsHitTestVisible bound to IsEditablePill so
    /// it rejects taps).
    /// <para>
    /// Rationale: the pill used to disappear the instant a wire attached,
    /// which hid the design-time literal the user had typed and made the
    /// node look like it had no inline state at all. Math.Add A=5 with a
    /// wire on A would render as just "[A]" with no "5" anywhere — even
    /// though the literal was still saved in Node.Attributes and would
    /// re-surface if the wire was removed. The ghost-pill keeps the
    /// literal visible so the user can read what's being overridden.
    /// </para>
    /// </summary>
    public bool IsPillVisible
    {
        get
        {
            if (_socket.Type != SocketType.Input) return false;
            if (string.IsNullOrEmpty(_socket.Name)) return false;
            if (_socket.IsPlaceholder) return false;
            if (SocketTypeHelper.IsFlowPin(_socket)) return false;
            return true;
        }
    }

    /// <summary>
    /// Opacity for the inline pill Border. Full
    /// opacity (1.0) when the pill is editable (no wire attached); dimmed
    /// to 0.45 when the pill is rendering as a ghost (wire attached, the
    /// wired source's value wins at export time so the literal is
    /// informational only). Paired with IsHitTestVisible="{IsEditablePill}"
    /// in XAML so the ghost-pill doesn't accept taps — clicking the wired
    /// pill must not flip it into edit mode.
    /// </summary>
    public double PillOpacity
    {
        get
        {
            // Fallback pills (Math.* / Twitch.* / Discord.* / API.Call /
            // File.* / Text.Builder per IsFallbackPillNode) stay at full
            // opacity even while wired so the literal default reads as the
            // active edit surface. The dimmed BorderBrush from IsPillFallback
            // is the visual signal that the wire wins at export time, not the
            // pill opacity.
            if (_isConnected && IsFallbackPillNode()) return 1.0;
            return _isConnected ? 0.45 : 1.0;
        }
    }

    /// <summary>
    /// Display text for the inline value pill — the actual value when set,
    /// otherwise a faint "—" placeholder so the empty pill still reads as
    /// "click to type" rather than blank space.
    /// <para>
    /// When the socket is wired and the user has
    /// no literal stored (the wire is the only source of the value), the
    /// pill shows "(wired)" so the ghost-pill reads as intentional rather
    /// than a stale empty "—". A wired socket WITH a literal still shows the
    /// literal so the user can see what the wire is overriding — that's a
    /// useful design-time signal, the wired source wins at export time but
    /// the literal is the fallback if the wire is later removed.
    /// </para>
    /// </summary>
    public string PillDisplayText
    {
        get
        {
            // For fallback-pill nodes (Math operators / Twitch / Discord
            // / API.Call / File.* / Text.Builder), the wire is the active
            // value at runtime but the literal is the fallback if upstream
            // resolves to null. Show the literal so authors can keep editing
            // it (and hit "—" rather than "(wired)" when blank, so the empty
            // state still reads as "click to type a fallback").
            if (_isConnected && IsFallbackPillNode())
                return string.IsNullOrEmpty(_valuePill) ? "—" : _valuePill!;
            if (_isConnected && string.IsNullOrEmpty(_valuePill))
                return Localizer.T("architect.canvas.socket.pill.wired", "(wired)");
            return string.IsNullOrEmpty(_valuePill) ? "—" : _valuePill!;
        }
    }

    /// <summary>
    /// True when the inline pill should render as a multi-line editor — the
    /// attribute key is one of <c>Template / Script / Payload</c> (case-
    /// insensitive) or the current value contains a newline. Mirrors pre-T15
    /// IsMultilineAttr; the static-context twin lives in
    /// <see cref="NodeGeometry.IsMultilinePill"/> for the intrinsic-width
    /// path. Bound by NodeView.xaml so the TextBox's <c>AcceptsReturn</c>,
    /// <c>TextWrapping</c>, height, and KeyDown gating swap to the multi-line
    /// path (Enter inserts newline; Ctrl+Enter commits).
    /// </summary>
    public bool IsMultilineAttr => NodeGeometry.IsMultilinePill(_socket.Name, _valuePill);

    /// <summary>
    /// XAML pill MaxWidth binding — the width the pill grows to before it must
    /// wrap (or, for DB pills, the single-line ceiling). Three categories,
    /// matching <see cref="PillTextWrapping"/> and the intrinsic-width budget
    /// in <c>NodeGeometry.SocketRowContentWidth</c>:
    /// <list type="bullet">
    /// <item>DB pills (TableName / Column on a DB.* node) — widen single-line
    /// to <see cref="NodeGeometry.NodeMaxWidth"/> so the whole name shows on
    /// one line (Issue 1.1: DB pills must not wrap).</item>
    /// <item>Multi-line pills (Template / Script / Payload, or a value with a
    /// newline) — capped at <see cref="NodeGeometry.MultilinePillCap"/> so the
    /// wrapped block editor stays tight.</item>
    /// <item>Every other pill (Value.String literals, …) — widen to
    /// <see cref="NodeGeometry.WrapPillCap"/>, THEN wrap. "Widen first" keeps
    /// short values on one line; long values wrap rather than being clipped
    /// (Issue 1 / 1.2: no cut-offs).</item>
    /// </list>
    /// Re-raised from the <see cref="ValuePill"/> setter because the
    /// multi-line/plain split moves with the value (a newline flips the cap).
    /// <para>
    /// This is the node's ACTUAL available width on the row
    /// (<see cref="NodeGeometry.PillWrapWidth"/> with
    /// <see cref="NodeGeometry.SocketPillLead"/>), not a standalone fixed cap. Bound
    /// by NodeView.xaml as the pill's MaxWidth, it is the finite MEASURE-time
    /// constraint that forces a long value to wrap WITHIN the node body instead of
    /// overflowing + being clipped by the rounded border. DB pills (TableName /
    /// Column) return <see cref="NodeGeometry.DbSingleLineCap"/> and never wrap.
    /// </para>
    /// </summary>
    public double PillMaxWidth
        => NodeGeometry.PillWrapWidth(_parentNode, NodeGeometry.SocketPillLead(_socket.Name), IsMultilineAttr, HasDatabankPicker);

    /// <summary>Re-raise <see cref="PillMaxWidth"/> — called by
    /// NodeViewModel when the node body width changes (a sibling row's pill edit)
    /// so this pill re-evaluates the width it may wrap within.</summary>
    internal void RaisePillConstraint() => OnPropertyChanged(nameof(PillMaxWidth));

    /// <summary>
    /// Text-wrapping mode for the read-only inline pill TextBlock. DB pills
    /// (TableName / Column on a DB.* node) stay on ONE line and widen to fit
    /// the whole name — Majo flagged DB pills wrapping as wrong (Issue 1.1).
    /// EVERY other pill wraps (<see cref="Microsoft.UI.Xaml.TextWrapping.Wrap"/>)
    /// so a long value breaks onto a new line instead of being truncated — the
    /// node body grows taller, never cuts text off (Issue 1 / 1.2). Static per
    /// socket (keyed on <see cref="HasDatabankPicker"/>), so unlike the pre-fix
    /// IsMultilineAttr-based mode it doesn't change as the value is typed.
    /// </summary>
    public Microsoft.UI.Xaml.TextWrapping PillTextWrapping =>
        HasDatabankPicker
            ? Microsoft.UI.Xaml.TextWrapping.NoWrap
            : Microsoft.UI.Xaml.TextWrapping.Wrap;

    /// <summary>
    /// Text-trimming for the read-only inline pill TextBlock. Wrapping pills
    /// (everything non-DB) must NEVER ellipsis-truncate — they wrap to show the
    /// whole value (Issue 1.2: no cut-offs allowed). DB pills keep
    /// CharacterEllipsis purely as a last-resort guard for a name somehow wider
    /// than <see cref="NodeGeometry.NodeMaxWidth"/> (≈never — table/column names
    /// are short); in practice a DB pill widens to fit and shows in full.
    /// </summary>
    public Microsoft.UI.Xaml.TextTrimming PillTextTrimming =>
        HasDatabankPicker
            ? Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis
            : Microsoft.UI.Xaml.TextTrimming.None;

    /// <summary>
    /// True while the inline-pill TextBox is showing — set by the view when
    /// the user clicks the pill, cleared on Enter/blur via CommitValuePillEdit.
    /// </summary>
    public bool IsEditing
    {
        get => _isEditing;
        // Report the open/close transition to InlineEditGate so the window's
        // menu accelerators (bare F, Ctrl+Z, …) disable while this pill editor
        // is up — focus-independent, so it holds even mid-typing where
        // FocusManager doesn't report the pill's TextBox under XAML-Islands hosting.
        set { if (SetField(ref _isEditing, value)) { if (value) InlineEditGate.Enter(); else InlineEditGate.Exit(); } }
    }

    private bool _isRenaming;
    /// <summary>
    /// True while the inline label TextBox is showing — set by the view when the
    /// user double-taps a socket label, cleared on Enter/Esc/blur. Mode-exclusive
    /// with <see cref="IsEditing"/>: only one inline TextBox is visible per row.
    /// </summary>
    public bool IsRenaming
    {
        get => _isRenaming;
        // Same InlineEditGate reporting as IsEditing — a label rename box is
        // an inline editor too, so the canvas hotkeys must stay suppressed
        // while it is open (Esc/Enter both route through the setter to false).
        set { if (SetField(ref _isRenaming, value)) { if (value) InlineEditGate.Enter(); else InlineEditGate.Exit(); } }
    }

    // ─── 0.10.0 inline-edit baseline tracking ───────────────────────────
    // UE-Blueprints idiom — Esc rolls back to the value as it was when
    // edit started; Enter / focus-loss commits. No-op edits (old == new
    // at commit) DO NOT push an undo entry. Pre-0.10.0 OnPillTapped /
    // OnLabelDoubleTapped pushed undo eagerly and Esc just cleared
    // IsEditing without rolling back the TwoWay-bound mutation; a typed-
    // and-Esc'd edit lost the value AND the original was undo'd one
    // step too far. The baseline captured here lets the view restore
    // through the same setter the TwoWay binding uses, so Node.Attributes
    // also rolls back in step.

    private string? _valuePillEditBaseline;
    private bool    _valuePillEditActive;

    /// <summary>
    /// Snapshot the current pill value as the rollback baseline and flip into
    /// edit mode.
    /// <para>
    /// Focus + select-all are NOT done here (do not re-add them).
    /// The inline TextBox stays in the visual tree and only toggles
    /// Visibility on <see cref="IsEditing"/>, so a one-shot focus call from
    /// the VM would only land on the very first edit. Auto-focus + SelectAll
    /// are driven view-side by <c>NodeView.OnInlineEditorLoaded</c> +
    /// <c>FocusInlineEditor</c>, which register a PropertyChangedCallback on
    /// the editor's VisibilityProperty so every edit-entry (Visible flip)
    /// re-grabs focus and selects all — honouring the UE-Blueprints
    /// "first keypress replaces the whole pill value" contract across repeated
    /// edit cycles.
    /// </para>
    /// </summary>
    public void BeginValuePillEdit()
    {
        _valuePillEditBaseline = _valuePill;
        _valuePillEditActive   = true;
        IsEditing = true;
    }

    /// <summary>
    /// Close the pill-edit session. If <paramref name="commit"/> is false (Esc),
    /// restore the baseline through the <see cref="ValuePill"/> setter so the
    /// underlying <c>Node.Attributes</c> migrates back too. Returns true when
    /// the committed value differs from the baseline — the view uses that to
    /// gate the undo push so a typed-then-deleted-back session doesn't accrete
    /// a no-op snapshot on the shared History stack.
    /// </summary>
    public bool EndValuePillEdit(bool commit)
    {
        if (!_valuePillEditActive)
        {
            // Defensive: focus-loss may fire after Esc already ended the
            // session. Treat as no-op without rewriting anything.
            IsEditing = false;
            return false;
        }
        bool changed = !string.Equals(_valuePill, _valuePillEditBaseline, StringComparison.Ordinal);
        if (!commit && changed)
        {
            ValuePill = _valuePillEditBaseline;
            changed = false;
        }
        IsEditing = false;
        _valuePillEditBaseline = null;
        _valuePillEditActive   = false;
        return changed;
    }

    private string? _labelEditBaseline;
    private bool    _labelEditActive;

    /// <summary>Snapshot the current socket name as the rollback baseline.</summary>
    public void BeginLabelEdit()
    {
        _labelEditBaseline = _socket.Name;
        _labelEditActive   = true;
        IsRenaming = true;
    }

    /// <summary>
    /// Close the label-rename session. Esc restores the baseline name through
    /// the <see cref="Label"/> setter (which also migrates the Node.Attributes
    /// key the rename normally moves). Returns whether the committed name
    /// differs from baseline so the view can gate the undo push.
    /// <para>
    /// Commit-side validation. The TwoWay binding
    /// has already pushed every keystroke into <c>Socket.Name</c> via the
    /// <see cref="Label"/> setter, so by the time this runs we have to
    /// inspect the post-edit name and roll back if it's invalid. Three
    /// rejection cases:
    ///   1. empty / whitespace-only — would orphan the inline pill and
    ///      break every <c>Attributes[socket.Name]</c> lookup downstream;
    ///   2. duplicate within the same node + same direction — exporter
    ///      keys on socket name, so two inputs called "Value" become
    ///      ambiguous in the .phx;
    ///   3. characters outside <c>[A-Za-z0-9_]</c> — the exporter emits
    ///      socket names as Python identifiers; spaces / hyphens / dots
    ///      would produce a syntactically invalid .phx.
    /// On rejection we log via <c>GlobalLogger</c> at
    /// <see cref="LogLevel.Communication"/> (rename attempts can fire often,
    /// never pop a ContentDialog) and
    /// roll back through the <see cref="Label"/> setter so
    /// <c>Node.Attributes</c> migrates back in step.
    /// </para>
    /// </summary>
    public bool EndLabelEdit(bool commit)
    {
        if (!_labelEditActive)
        {
            IsRenaming = false;
            return false;
        }
        bool changed = !string.Equals(_socket.Name, _labelEditBaseline, StringComparison.Ordinal);
        if (!commit && changed)
        {
            Label = _labelEditBaseline ?? string.Empty;
            changed = false;
        }
        else if (commit && changed)
        {
            // Post-commit validation. Reject + roll back the rename
            // if the new name is empty, a duplicate on the same side of
            // the same node, or carries non-identifier characters.
            var candidate = _socket.Name ?? string.Empty;
            string? rejectReason = ValidateSocketName(candidate);
            if (rejectReason != null)
            {
                var baseline = _labelEditBaseline ?? string.Empty;
                GlobalLogger.Log(
                    $"Architect: socket rename rejected — {rejectReason}. " +
                    $"Reverted '{candidate}' → '{baseline}' on node '{_parentNode.Title}'.",
                    "Architect",
                    LogLevel.Communication);
                Label = baseline;
                changed = false;
            }
        }
        IsRenaming = false;
        _labelEditBaseline = null;
        _labelEditActive   = false;
        return changed;
    }

    /// <summary>
    /// Validate a candidate socket name against the
    /// three rename guards: non-empty, identifier-safe characters, and
    /// uniqueness within the parent node's same-direction socket list.
    /// Returns <c>null</c> when the name is acceptable, or a short
    /// human-readable rejection reason that the caller surfaces through
    /// <c>GlobalLogger</c>.
    /// </summary>
    private string? ValidateSocketName(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return "name is empty";

        // Dynamic-placeholder sockets carry a leading "+" sentinel
        // (see IsDynamicPlaceholder). Strip it before the identifier
        // check so the activation idiom isn't accidentally banned.
        var probe = candidate.TrimStart();
        if (probe.StartsWith("+", StringComparison.Ordinal))
            probe = probe.Substring(1);

        for (int i = 0; i < probe.Length; i++)
        {
            char c = probe[i];
            bool ok = (c >= 'A' && c <= 'Z')
                   || (c >= 'a' && c <= 'z')
                   || (c >= '0' && c <= '9')
                   || c == '_';
            if (!ok)
                return $"invalid character '{c}' (only A–Z, a–z, 0–9, _ allowed)";
        }

        // Duplicate detection: another socket on the SAME node with the
        // SAME direction already carries this name. Exclude self by
        // identity (same Socket ref) — the candidate is already written
        // into _socket.Name by the TwoWay binding by the time we run.
        var siblings = _parentNode.Sockets;
        if (siblings != null)
        {
            for (int i = 0; i < siblings.Count; i++)
            {
                var other = siblings[i];
                if (other == null) continue;
                if (ReferenceEquals(other, _socket)) continue;
                if (other.Type != _socket.Type) continue;
                if (string.Equals(other.Name, candidate, StringComparison.Ordinal))
                    return $"duplicate name on this node's {(_socket.Type == SocketType.Input ? "input" : "output")} side";
            }
        }

        return null;
    }

    /// <summary>
    /// 0.11.5 — measured row-centre Y (in NodeRoot outer-top
    /// coords) pushed back from NodeView's per-row Loaded / SizeChanged
    /// handler. Lets wire anchors track the actual painted centre on rows
    /// whose intrinsic height grows past the static SocketRowHeight=24
    /// estimate (multi-line Template / Script / Payload pills). Zero means
    /// "no measurement yet" — Anchor() falls back to the static estimate.
    /// </summary>
    private double _measuredRowCenterY;
    public void SetMeasuredRowCenterY(double yOuter)
    {
        // 0.5 px hysteresis — repeated identical layout passes shouldn't
        // dirty the wire-anchor cache.
        if (System.Math.Abs(_measuredRowCenterY - yOuter) < 0.5) return;
        _measuredRowCenterY = yOuter;
        OnPropertyChanged(nameof(MeasuredRowCenterY));
    }
    public double MeasuredRowCenterY => _measuredRowCenterY;

    /// <summary>
    /// Drop the measured row-centre back to the "no measurement" sentinel so
    /// <see cref="Anchor"/> reverts to the static <see cref="NodeGeometry"/>
    /// estimate. Called (together with <c>SocketRenderState.Forget</c>) when the
    /// GPU-canvas edit overlay tears down — the measured value is only
    /// trustworthy WHILE a NodeView is mounted (it is captured against the
    /// editor-TextBox layout, subject to commit/exit timing), so once the node
    /// goes back to being GPU-drawn the dynamic estimate — which the body
    /// outline also uses — is the authoritative, self-consistent source. Leaving
    /// the stale value behind is what left "bubbles"/wires drifting after a
    /// multi-line pill edit until an app restart re-GUID'd the sockets.
    /// </summary>
    public void ClearMeasuredRowCenterY()
    {
        if (_measuredRowCenterY == 0) return;
        _measuredRowCenterY = 0;
        OnPropertyChanged(nameof(MeasuredRowCenterY));
    }

    /// <summary>
    /// Anchor point (node-relative) where wires terminate against this socket's pin.
    /// Inputs anchor on the left edge of the node, outputs on the right edge.
    /// When NodeView has reported a measured row-centre Y (see
    /// <see cref="SetMeasuredRowCenterY"/>), the measured value wins over the
    /// static <see cref="NodeGeometry.RowCenterY"/> estimate so multi-line
    /// pill rows stay glued to the painted pin centre.
    /// </summary>
    public (double X, double Y) Anchor()
    {
        var (x, y) = NodeGeometry.SocketAnchor(_parentNode, RowIndex, _socket.Type);
        return (x, _measuredRowCenterY > 0 ? _measuredRowCenterY : y);
    }

    private static string? ResolveInitialValuePill(Node node, Socket socket)
    {
        // Inline pills only render on inputs — output sockets carry computed values, not literals.
        if (socket.Type != SocketType.Input) return null;
        if (string.IsNullOrEmpty(socket.Name)) return null;
        return node.Attributes is { } attrs && attrs.TryGetValue(socket.Name!, out var v) && !string.IsNullOrEmpty(v)
            ? v
            : null;
    }
}
