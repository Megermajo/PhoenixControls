using System;
using System.Collections.Concurrent;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Architect.Core;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

// Single source of truth for "where on this node does each socket live?"
// so wire-endpoints, hit-testing and the future drag-to-create-wire
// preview all agree on the same anchor coordinates.
//
// Coordinates are in canvas-space, with (0,0) = node top-left. The XAML
// <Canvas> positions a NodeView at Node.Location, then each socket's
// anchor sits at NodeView-relative (Anchor.X, Anchor.Y). Add the canvas-
// to-node offset and you have the wire endpoint.
//
// Header section auto-sizes for the optional italic "definer" subtitle
// (EventName / VariableName / Commands / etc.) — 24px without definer,
// 38px with — mirroring pre-T15 GetHeaderH. Anchor math reads HasDefiner
// from the model so wires stay glued to socket pins as the header grows.
public static class NodeGeometry
{
    public const double DefaultWidth          = 240.0;
    // 0.11.x readability floor — Majo's standing rule "all nodes need a
    // minimum size to include at least 'Hello chat!' in a single line".
    // An 11-char monospaced pill (JetBrains Mono / Cascadia at 11pt ≈ 73 px
    // glyph + 12 px Border padding ≈ 85 px) inside a typical input row
    // (pin 14 + pad 6 + label "Message" ≈ 50 + gap 6 + pill 85 = 161 px)
    // paired with a "Done"-class output column (~44 px) plus the 16 px
    // inter-column gap lands at ~221 px. Round up to 240 (matches
    // <see cref="MultilinePillCap"/>) so single-line pills always fit and
    // multi-line pills cap exactly at the body's narrowest state.
    // Pre-fix 160 was the pre-T15 EnsureNodeBodySize floor.
    public const double NodeMinWidth          = 240.0;
    // 0.11.x — raised 520→1200 so DB.* nodes whose TableName / Column pills
    // carry long identifiers grow the body to fit instead of cropping with
    // CharacterEllipsis (Majo: "DB node not wide enough; needs an enormous
    // size before cropping"). Still capped so a pasted blob can't size a node
    // off-screen — single-line pills render at true text width UP TO this
    // body ceiling, then trim.
    public const double NodeMaxWidth          = 1200.0; // was 520 (pre-T15 EnsureNodeBodySize ceiling)
    // DB.* TableName/Column pills must render single-line AND
    // fully visible (no ellipsis cut-off) — Majo's hard rule. A node carrying such a
    // pill widens to fit the whole identifier on ONE line, up to this much larger
    // ceiling (≈never reached by real table/column names) before the last-resort
    // CharacterEllipsis even applies. Separate from NodeMaxWidth (the plain-node
    // ceiling) so a long DB name grows the body well past 1200 without un-capping
    // every node. Mirrored by SocketViewModel.PillMaxWidth's DB branch.
    public const double DbSingleLineCap       = 2500.0;
    // Header + row heights bumped 2px alongside the +1pt font bump in
    // NodeView.xaml (sockets 11→12, title 11→12, definer 9→10). The XAML
    // MinHeight values mirror these constants so wire anchors stay glued to
    // the painted socket pin centres.
    public const double HeaderBandHeightPlain = 26.0;  // node without italic definer subtitle
    public const double HeaderBandHeightDef   = 40.0;  // node with italic definer subtitle
    public const double SocketRowHeight       = 24.0;
    // 0.11.5 — pin centres now sit ON the body's outer
    // edge (50/50 overhang, half outside the body, half inside) per Majo's
    // "bubbles for input/output should be on the border of the node (50/50
    // in/out) to allow a better overlap with the pipe" feedback from the
    // 0.11.5 audit. Wire endpoints land at x=0 (inputs) and x=width
    // (outputs); the pin Ellipse is rendered as a sibling of NodeRoot
    // (NodeView.PinOverlay) so the Border's CornerRadius clip doesn't
    // truncate the overhanging half.
    //
    // Vertical anchor (RowCenterY) still uses the static SocketRowHeight=24
    // estimate. Multi-line pill rows that grow tall shift subsequent rows'
    // painted pin centres without updating the wire anchor; NodeViewModel
    // now exposes a per-socket Y override populated from a measure-pass in
    // NodeView so SocketAnchor's caller (LogicCanvasView.Wires) can prefer
    // the measured value over this static estimate. Single-line nodes are
    // unaffected.
    public const double SocketColumnInset     = 0.0;
    public const double PinRadius             = 7.0;   // pin shape is centred in 14×14
    public const double SocketGapAfterHeader  = 8.0;   // matches NodeView XAML row 1 (gap row)
    // NodeView.xaml's NodeRoot Border carries
    // BorderThickness=1; the inner content area is inset 1px from the
    // node body's OUTER-top-left corner. SocketColumnInset already bakes
    // this into the horizontal anchor; FirstSocketRowY uses this constant
    // to do the same on the vertical anchor so pin endpoints land on the
    // painted centre on both axes. Pre-fix the wires landed 1px above the
    // painted pin centre on every row of every node, in addition to the
    // 1px horizontal miss the round-1 fix addressed.
    public const double NodeBorderInset       = 1.0;
    // Design_Orders §6 — middle-attribute row height + top gap.
    public const double MiddleAttrRowHeight   = 22.0;  // §6 "Middle-attribute row height: 18px" + 4px row padding
    public const double MiddleAttrAreaTopPad  = 6.0;   // §6 "Gap above middle-attribute area"
    public const double MiddleAttrDividerH    = 2.0;   // 1px dotted divider + 1px bottom margin

    /// <summary>
    /// Header band height for the given node — 38 if a definer subtitle
    /// renders, 24 otherwise. Mirrors pre-T15 GetHeaderH so wire anchors
    /// track the actual painted band as it grows.
    /// </summary>
    public static double HeaderHeight(Node node)
        => HasDefiner(node) ? HeaderBandHeightDef : HeaderBandHeightPlain;

    /// <summary>
    /// Y coordinate (node-relative, measured from the node body's INNER-top
    /// edge — i.e. just below the NodeRoot Border) where socket row 0
    /// begins. Subsequent rows are <see cref="SocketRowHeight"/>px below.
    /// <para>
    /// Note: this returns an INNER-relative offset, not OUTER. For wire
    /// endpoints + hit tests the caller adds <see cref="NodeBorderInset"/>
    /// (the painted pin centre sits 1px below this). Done inside
    /// <see cref="SocketAnchor"/> rather than baked in here so consumers
    /// like <see cref="IntrinsicHeight"/> (which sums up the inner content
    /// height) stay consistent with the painted body's intrinsic content
    /// budget.
    /// </para>
    /// </summary>
    public static double FirstSocketRowY(Node node)
        => HeaderHeight(node) + SocketGapAfterHeader;

    /// <summary>
    /// Y coordinate (node-relative, INNER-top reference) of the centre of
    /// socket row at <paramref name="rowIndex"/>. Row 0 = first row beneath
    /// the header. Used by <see cref="SocketAnchor"/> (wire endpoint, which
    /// adds <see cref="NodeBorderInset"/> to convert to outer-top) and
    /// <c>LogicCanvasView.Menus.StraightenLink</c> (relative-Y delta — the
    /// border-inset cancels out across the two operands, so either reference
    /// produces the same delta).
    /// </summary>
    public static double RowCenterY(Node node, int rowIndex)
        => FirstSocketRowY(node) + rowIndex * SocketRowHeight + SocketRowHeight / 2.0;

    /// <summary>
    /// Anchor point on a node where a wire should attach to the given socket.
    /// Inputs anchor on the left edge, outputs on the right edge. The
    /// returned coordinates are measured from the node body's OUTER-top-left
    /// corner so callers can add <c>node.Location</c> directly to land on
    /// the painted pin centre.
    /// </summary>
    public static (double X, double Y) SocketAnchor(Node node, int rowIndex, SocketType socketType)
    {
        // Flow.Reroute: pin In at the diamond's west point (x=0, y=10)
        // and pin Out at the east point (x=20, y=10). The header / row-Y
        // arithmetic below would land both pins ~48px below the diamond
        // top, well below the painted footprint.
        if (IsReroute(node))
        {
            double rx = socketType == SocketType.Input ? 0.0 : 20.0;
            return (rx, 10.0);
        }

        // Use the same effective width the NodeViewModel renders with, NOT the
        // persisted Node.Size.Width. The WinUI canvas has no resize gesture, so
        // Node.Size.Width is a legacy artifact of pre-T15 EnsureNodeBodySize
        // saves — often clamped at NodeMaxWidth=520. Reading it here left wire
        // right-edges anchored at 520 while the painted body had shrunk to its
        // intrinsic width, so output pipes started past the node body in mid-air.
        double width = EffectiveWidth(node);
        // 0.11.5 — pin centres sit ON the body's outer edge
        // (SocketColumnInset=0). NodeBorderInset still applies on the vertical
        // axis (RowCenterY is inner-top-relative; pin centres are outer-top-
        // relative). On the horizontal axis, the pin Ellipse is rendered as a
        // sibling of NodeRoot (NodeView.PinOverlay) and centred on x=0 / x=width
        // — the Border's CornerRadius clip can't reach an element that isn't a
        // descendant of NodeRoot.
        // Use the DYNAMIC row centre (accounts for rows above this
        // one that grew tall to fit a wrapped multi-line pill). Identical to the
        // static RowCenterY when no row wraps, so single-line nodes are unchanged.
        // This keeps wire endpoints glued to the painted pin centre on the GPU
        // canvas (where there is no retained measure pass to set MeasuredRowCenterY).
        double y = NodeBorderInset + RowCenterYDynamic(node, rowIndex);
        double x = socketType == SocketType.Input
            ? SocketColumnInset                 // 0 → outer-left edge
            : width - SocketColumnInset;        // width → outer-right edge
        return (x, y);
    }

    /// <summary>
    /// Effective rendered width for <paramref name="node"/>. Compact-mode
    /// honours the persisted <see cref="Node.Size"/> (the right-click
    /// "Convert to Compact" gesture is the only surface that writes Size on
    /// the WinUI canvas). Otherwise the value is the live shrink-to-fit
    /// <see cref="IntrinsicWidth"/>. Single source of truth — both
    /// <c>NodeViewModel.Width</c> (body render) and <see cref="SocketAnchor"/>
    /// (wire endpoints) call this so wires stay glued to the painted body.
    /// </summary>
    public static double EffectiveWidth(Node node)
    {
        // Flow.Reroute is a fixed-footprint diamond
        // "wire knot", not a normal node. Without this carve-out IntrinsicWidth
        // measured the "Flow.Reroute" title string and grew the body to ~140px,
        // breaking the visual idiom Majo named. 20px matches the pre-T15
        // NodeRegistry Size = (20, 20).
        if (IsReroute(node)) return 20.0;
        if (IsCompactMode(node) && node.Size.Width > 0) return node.Size.Width;
        return IntrinsicWidth(node);
    }

    /// <summary>
    /// True when the node is the Flow.Reroute passthrough — a fixed 20×20
    /// diamond. Centralised here so EffectiveWidth, IntrinsicHeight,
    /// SocketAnchor and the view all agree.
    /// </summary>
    public static bool IsReroute(Node node)
        => node != null
        && string.Equals(node.Title, "Flow.Reroute", StringComparison.Ordinal);

    /// <summary>
    /// True when the node persists an opt-in compact-mode flag. Compact mode
    /// is the only WinUI surface that writes back into <see cref="Node.Size"/>;
    /// in normal mode the size is driven by the intrinsic content.
    /// </summary>
    public static bool IsCompactMode(Node node)
        => node?.Attributes != null
        && node.Attributes.TryGetValue("Compact", out var cv)
        && string.Equals(cv, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Shrink-to-fit width for the node — max of (header content + chrome)
    /// and per-row (input + output) socket widths, clamped to
    /// [<see cref="NodeMinWidth"/>, <see cref="NodeMaxWidth"/>]. Pure function
    /// of <paramref name="node"/> + the registry's display-name + the running
    /// socket attribute values, so the wire-anchor and body-render paths can
    /// share a single deterministic answer.
    /// </summary>
    public static double IntrinsicWidth(Node node)
    {
        // Per-node body-size cache. XAML binds Width/Height on every layout pass and every
        // PropertyChanged storm re-evaluates the bound geometry, so without a
        // cache IntrinsicWidth/IntrinsicHeight walked every socket + every
        // DefaultProperties entry on each call (~500 MeasureString/frame on a
        // 50-node graph). Key on node.Id; the content hash (Title + each
        // socket's Name/Type/Offset.Y + each attribute key/value) is the
        // invalidation — when the inputs are unchanged the hash matches and we
        // return the cached width without re-walking. The cache stores Width +
        // Height together (NaN = "this dimension not computed for the current
        // hash yet") so IntrinsicHeight reuses the same entry. The per-node
        // GeometryGate additionally skips the hash walk itself for settled
        // nodes (see the gate's comment block) so per-frame wire-path probes
        // are O(1) between mutations.
        if (node is not null && !string.IsNullOrEmpty(node.Id))
        {
            var gate = GetGeometryGate(node.Id);
            bool hasCached = s_nodeSizeCache.TryGetValue(node.Id, out var cached);
            // Settled fast path — no mutation flagged since the last
            // verified probe, so skip the O(sockets+attrs) hash walk entirely.
            if (hasCached && !double.IsNaN(cached.Width) && TryFastGeometryProbe(gate, cached.Hash))
                return cached.Width;
            gate.Dirty = false; // clear BEFORE hashing — a concurrent mutation re-marks
            int hash = ComputeNodeContentHash(node);
            MarkGeometryVerified(gate, hash);
            if (hasCached && cached.Hash == hash && !double.IsNaN(cached.Width))
                return cached.Width;
            double w = ComputeIntrinsicWidth(node);
            StoreNodeSize(node.Id, hash, width: w, height: double.NaN);
            return w;
        }
        return ComputeIntrinsicWidth(node!);
    }

    private static double ComputeIntrinsicWidth(Node node)
    {
        // Header row content — title (13pt Bold sans-serif) and optional
        // definer (10pt italic). Title font bumped from 12pt SemiBold serif
        // (Cormorant Garamond / DisplayFont) to 13pt Bold sans (Inter /
        // Segoe UI Variable Display / SansFont) for better readability under
        // the canvas RenderTransform zoom. Width budget kept in lockstep
        // with NodeView.xaml — under-estimating here would clip long titles
        // with CharacterEllipsis even when the body would happily grow.
        double titleW = EstimateTextWidth(DisplayTitle(node), 13.0, bold: true) + 20.0; // 10px L + 10px R header padding
        var def = TruncatedDefiner(node);
        if (!string.IsNullOrEmpty(def))
        {
            double defW = EstimateTextWidth(def, 10.0) + 20.0;
            titleW = Math.Max(titleW, defW);
        }

        // Socket rows — sum max input column width + max output column
        // width. Pre-fix this used the per-row paired-sum max, which under-
        // estimated the body when a long input pill paired with an empty
        // output (or vice versa): row 0 might want 250 of input + 0 of
        // output → 266; row 1 might want 0 + 80 → 96. Old math returned 266
        // and the layout's 50/50 column split gave row 0 only 133 of input
        // space (260-pad), squeezing the pill into ~3-letter wraps.
        // Summing the per-side maxes gives 250 + 80 + 16 = 346 — the body
        // grows so EVERY input row has its full pill width budget. Paired
        // with the NodeView.xaml input-* / output-Auto column split, every
        // single-line pill renders at its true text width up to NodeMaxWidth.
        const double interColumnGap = 16.0;

        double maxInputW  = 0;
        double maxOutputW = 0;
        if (node?.Sockets != null)
        {
            foreach (var s in node.Sockets)
            {
                double w = SocketRowContentWidth(node, s);
                if (s.Type == SocketType.Input)
                {
                    if (w > maxInputW)  maxInputW  = w;
                }
                else
                {
                    if (w > maxOutputW) maxOutputW = w;
                }
            }
        }

        double rowsW = (maxInputW > 0 || maxOutputW > 0)
            ? maxInputW + maxOutputW + interColumnGap
            : 0;

        // Middle-attribute rows (Design_Orders §5.1) — `Key …… [pill]` / `Key …… ☑/☐`
        // for DefaultProperties keys with no matching socket. Each row's width
        // budget is (key label + gap + pill content), capped by the multi-line
        // pill rule to mirror SocketRowContentWidth. Keys that match an existing
        // socket name (case-insensitive) are already covered by the socket row.
        double middleW = 0;
        try
        {
            var tmpl = NodeRegistry.GetTemplate(node?.Title ?? string.Empty);
            if (tmpl?.DefaultProperties != null && tmpl.DefaultProperties.Count > 0 && node?.Sockets != null)
            {
                // Same input-only filter as NodeViewModel.RebuildMiddleAttributes —
                // a DP key that shares a name with an output socket (Value.* literals)
                // SHOULD still consume a middle-attr row's width budget.
                var socketNames = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                foreach (var s in node.Sockets)
                    if (s.Type == SocketType.Input && !string.IsNullOrEmpty(s.Name))
                        socketNames.Add(s.Name);

                foreach (var kv in tmpl.DefaultProperties)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    if (socketNames.Contains(kv.Key)) continue;

                    string? liveValue = null;
                    if (node.Attributes != null && node.Attributes.TryGetValue(kv.Key, out var v))
                        liveValue = v;
                    var effective = liveValue ?? kv.Value;

                    double keyW   = EstimateTextWidth(kv.Key, 12.0) + 12.0; // 6px L + 6px R padding
                    bool isBool   = string.Equals(kv.Value, "true",  System.StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(kv.Value, "false", System.StringComparison.OrdinalIgnoreCase);
                    double rowW;
                    if (isBool)
                    {
                        // Bool checkmark row renders LEFT-aligned `☑ Key`
                        // (18px glyph + 6px gap + key label) with NO dotted
                        // spacer or pill column — keep in lockstep with
                        // NodeView.xaml's bool StackPanel and the Win2D
                        // DrawMiddleAttributes / toggle hit-test bool branch.
                        // + 16 row padding (8L+8R).
                        rowW = 18.0 + 6.0 + keyW + 16.0;
                    }
                    else
                    {
                        double pillW;
                        if (!string.IsNullOrEmpty(effective))
                        {
                            // Middle-attribute pill renders in MonoFont like socket-row
                            // pills (NodeView.xaml MiddleAttributeRowTemplate pill TextBlock).
                            // Non-DB by construction, so the plain-pill cap (WrapPillCap)
                            // applies: widen the body to WrapPillCap, then the pill wraps
                            // (matching MiddleAttributeViewModel.PillMaxWidth).
                            double rawPillW = EstimateTextWidth(effective, 11.0, mono: true) + 12.0;
                            pillW = IsMultilinePill(kv.Key, effective)
                                  ? Math.Min(rawPillW, MultilinePillCap)
                                  : Math.Min(rawPillW, WrapPillCap);
                        }
                        else
                        {
                            // Empty pill still reserves "—" placeholder width.
                            pillW = 16.0;
                        }

                        // + 16 row padding (8L+8R) so the body grows
                        // wide enough to hold the pill + its row chrome; mirrors
                        // MiddleAttrPillLead so width-grow and PillWrapWidth agree.
                        rowW = keyW + 16.0 + pillW + 16.0 // 16px dotted-spacer + 16px row padding
                             + (IsOptionsPickerAttr(node, kv.Key) ? MiddleAttrPickerChevronWidth : 0.0);
                    }
                    if (rowW > middleW) middleW = rowW;
                }
            }
        }
        catch { /* registry miss / instance-without-template — fall through to socket-only width */ }

        double needed = Math.Max(NodeMinWidth, Math.Max(titleW, Math.Max(rowsW, middleW)));
        // A node with a DB.* TableName/Column pill gets the
        // larger single-line ceiling so a long identifier shows in full on one line
        // (no cut-off); every other node keeps the standard NodeMaxWidth cap.
        return Math.Min(needed, HasDbPill(node) ? DbSingleLineCap : NodeMaxWidth);
    }

    /// <summary>
    /// Width contribution of a single socket row (pin + label + optional
    /// inline value pill). Single-line pills contribute their full text
    /// width so the node body grows toward <see cref="NodeMaxWidth"/> to
    /// accommodate long values (e.g. DB.* TableName); multi-line pills
    /// (Template / Script / Payload, or anything containing a newline)
    /// stay capped at <see cref="MultilinePillCap"/> so a wrapped multi-
    /// line editor doesn't push the body past the ceiling and leave the
    /// other column visually empty.
    /// <para>
    /// 0.11.x re-sync fix — when the socket has no stored literal but its
    /// pill is still rendered (every named, non-flow, non-placeholder
    /// INPUT row paints a pill via <c>SocketViewModel.IsPillVisible</c>),
    /// the displayed text is one of <c>PillDisplayText</c>'s placeholders:
    /// <c>"(wired)"</c> when a wire is attached or <c>"—"</c> otherwise.
    /// Pre-fix the width budget treated the empty-literal case as
    /// <c>pillW = 0</c>, so Event.Trigger / DB.DeleteRow / Logic.EnumMatch
    /// nodes that re-synced their socket payloads (cross-file sync, wire
    /// drop) shrank to NodeMinWidth and the ghost pill wrapped into
    /// 3-letter chunks. Now the path reserves the wider of <c>"(wired)"</c>
    /// vs <c>"—"</c> so the body always has room for the painted pill.
    /// </para>
    /// </summary>
    private static double SocketRowContentWidth(Node node, Socket socket)
    {
        const double pinAndPad = 14.0 + 6.0;
        // Label is SansFont — proportional. Pills are MonoFont — pass mono=true
        // so EstimateTextWidth picks JetBrains Mono / Cascadia / Consolas and
        // reports the actual painted glyph width. Mixing the two faces in one
        // budget under-counts the pill side by 30-40%.
        double labelW = EstimateTextWidth(socket.Name, 12.0);
        double pillW  = 0.0;
        string? pill = ResolvePillValue(node, socket);
        if (!string.IsNullOrEmpty(pill))
        {
            double rawPillW = EstimateTextWidth(pill, 11.0, mono: true) + 12.0;
            // Per-category budget, in lock-step with SocketViewModel.PillMaxWidth:
            //  • DB pills (TableName / Column) — uncapped (single-line widen to
            //    NodeMaxWidth so the whole name shows);
            //  • multi-line pills — capped at MultilinePillCap (240) block;
            //  • plain pills — capped at WrapPillCap (480): the body grows to
            //    480, then the pill wraps (no cut-off) instead of stretching the
            //    node to the full ceiling.
            pillW = IsDbPill(node, socket)            ? Math.Min(rawPillW, DbSingleLineCap)
                  : IsMultilinePill(socket.Name, pill) ? Math.Min(rawPillW, MultilinePillCap)
                  :                                      Math.Min(rawPillW, WrapPillCap);
        }
        else if (RendersGhostPill(socket))
        {
            // Mirrors SocketViewModel.PillDisplayText: an empty-literal pill
            // displays "(wired)" when connected, "—" when editable. We can't
            // see connectivity from here, so budget the widest of the two so
            // either render fits without forcing TextWrapping="Wrap" to break
            // mid-glyph. Mono measure so a 7-glyph "(wired)" in JetBrains Mono
            // / Cascadia (≈12 px advance per char at 11pt) gets the full ~84 px
            // budget instead of the ~50 px the proportional SansFont measure
            // returned pre-fix.
            pillW = EstimateTextWidth("(wired)", 11.0, mono: true) + 12.0;
        }
        // + 14 (pill Border margin 6 + row right padding 8) so the
        // body grows wide enough to hold the pill + its row chrome; mirrors
        // SocketPillLead so width-grow and PillWrapWidth agree.
        return pinAndPad + labelW + (pillW > 0 ? 6.0 + pillW + 14.0 : 0.0);
    }

    /// <summary>
    /// Mirror of <see cref="SocketViewModel.IsPillVisible"/> — true when
    /// the row paints an inline pill regardless of stored-literal state.
    /// Kept here (not lifted to a shared helper) so NodeGeometry stays a
    /// pure-data dependency without taking on a SocketViewModel reference.
    /// </summary>
    private static bool RendersGhostPill(Socket socket)
    {
        if (socket == null) return false;
        if (socket.Type != SocketType.Input) return false;
        if (string.IsNullOrEmpty(socket.Name)) return false;
        if (socket.IsPlaceholder) return false;
        if (Phoenix.Controls.Shared.Models.SocketTypeHelper.IsFlowPin(socket)) return false;
        return true;
    }

    /// <summary>Multi-line value pills (Template / Script / Payload, or any
    /// value containing a newline) cap at this width — both in the intrinsic-
    /// width math and in the XAML (<see cref="SocketViewModel.PillMaxWidth"/>).
    /// Single-line pills are uncapped so the node grows to fit the actual text.</summary>
    public const double MultilinePillCap = 240.0;

    /// <summary>Plain (non-DB, non-multiline) value pills — e.g. Value.String
    /// literals — widen to this cap and then WRAP rather than truncate (no
    /// cut-offs). "Widen first" keeps short values on one line;
    /// only genuinely long values wrap. Wider than <see cref="MultilinePillCap"/>
    /// (which keeps Template/Script/Payload block editors tight) but well under
    /// <see cref="NodeMaxWidth"/> so a long literal breaks onto a new line
    /// instead of stretching the node body to the full ceiling. Mirrors the
    /// plain-pill branch of <see cref="SocketViewModel.PillMaxWidth"/>.</summary>
    public const double WrapPillCap = 480.0;

    /// <summary>
    /// Mirror of <c>SocketViewModel.HasDatabankPicker</c> — true for the
    /// TableName / Column input pill on a DB.* node. DB pills widen single-line
    /// (no wrap, body grows to <see cref="NodeMaxWidth"/>) so the whole
    /// table/column name shows on one line; every other pill wraps. Kept here
    /// (not lifted to a shared helper) so the intrinsic-width path applies the
    /// same per-category budget as the view-model without taking a
    /// SocketViewModel reference.
    /// </summary>
    public static bool IsDbPill(Node? node, Socket? socket)
    {
        if (socket == null || socket.Type != SocketType.Input) return false;
        if (string.IsNullOrEmpty(socket.Name)) return false;
        if (node?.Title == null || !node.Title.StartsWith("DB.", StringComparison.Ordinal)) return false;
        return socket.Name.Equals("TableName", StringComparison.Ordinal)
            || socket.Name.Equals("Column",    StringComparison.Ordinal);
    }

    /// <summary>
    /// True when <paramref name="node"/> carries at least one
    /// DB.* TableName/Column pill (an <see cref="IsDbPill"/> input socket). Drives the
    /// larger <see cref="DbSingleLineCap"/> body ceiling in
    /// <see cref="ComputeIntrinsicWidth"/> so such a node can grow wide enough to show
    /// the whole identifier on a single line without an ellipsis cut-off.
    /// </summary>
    public static bool HasDbPill(Node? node)
    {
        if (node?.Sockets == null) return false;
        foreach (var s in node.Sockets)
            if (IsDbPill(node, s)) return true;
        return false;
    }

    // ─── Shared pill-wrap budget ────────────────────────────
    // The single source of truth for "how wide may an inline value pill be on its
    // row before it must WRAP". Used by THREE consumers that previously diverged
    // and let the pill overflow + clip:
    //   1. the rendered pill — NodeView.xaml binds the pill's MaxWidth to the VMs'
    //      PillConstrainedWidth, which returns PillWrapWidth(...). MaxWidth is a
    //      MEASURE-time constraint, so it is what actually forces TextWrapping=Wrap
    //      to break the text WITHIN the node body instead of running off the edge
    //      (which the rounded NodeRoot border then clips);
    //   2. the height measure — MeasureMiddleAttributesHeight + the socket-row
    //      measure wrap text at THIS SAME width so the reserved height matches the
    //      rendered wrap;
    //   3. the width grow — ComputeIntrinsicWidth caps each pill at the same
    //      WrapPillCap / MultilinePillCap so the node grows wide-first toward
    //      lead+cap, then the pill wraps and the node grows tall.
    // DB.* pills never wrap (single line) — they keep DbSingleLineCap.
    public const double PillMinWrapWidth = 60.0; // matches the editor TextBox MinWidth

    /// <summary>
    /// Fixed left-of-pill chrome on a middle-attribute row (key label + its
    /// padding + dotted-spacer minimum + row horizontal padding + pill Border
    /// padding). The node body grows to lead + pill, so available pill width =
    /// EffectiveWidth − lead. Slightly generous so the pill wraps a hair early
    /// rather than ever clipping. Mirrors the rowW budget in ComputeIntrinsicWidth.
    /// </summary>
    public static double MiddleAttrPillLead(string? key)
        => EstimateTextWidth(key, 12.0) + 12.0 /* key padding */
         + 16.0 /* dotted-spacer minimum */ + 16.0 /* row padding 8L+8R */ + 12.0 /* pill Border padding */;

    /// <summary>
    /// Width (px) the inline target-picker ▾ chevron claims on a middle-attribute
    /// row — the Button (16px) + its 4px left margin, rounded up for breathing
    /// room. Reserved on Process.Spawn / Macro.Call picker rows so the node body
    /// grows for the chevron AND the pill wraps within (body − chevron), keeping
    /// IntrinsicWidth, PillMaxWidth, and MeasureMiddleAttributesHeight consistent
    /// (the chevron lives in NodeView.xaml's MiddleAttributeRowTemplate col 3).
    /// </summary>
    public const double MiddleAttrPickerChevronWidth = 22.0;

    /// <summary>
    /// True when a middle-attribute row carries the inline target picker (the
    /// Process.Spawn process name / Macro.Call macro name). Single source of
    /// truth shared by <see cref="MiddleAttributeViewModel.HasOptionsPicker"/>
    /// (the chevron's visibility) and the width/height budgets here, so the
    /// reserved space and the rendered chevron never disagree.
    /// </summary>
    public static bool IsOptionsPickerAttr(Node? node, string? key)
    {
        var title = node?.Title ?? string.Empty;
        return (title == "Process.Start" && string.Equals(key, "ProcessName", System.StringComparison.Ordinal))
            || (title == "Macro.Call"    && string.Equals(key, "MacroName",   System.StringComparison.Ordinal));
    }

    /// <summary>
    /// Fixed left-of-pill chrome on a socket (input) row (pin overhang + label +
    /// label→pill gap + row padding + pill Border margin/padding). Counterpart to
    /// <see cref="MiddleAttrPillLead"/>; mirrors <see cref="SocketRowContentWidth"/>.
    /// </summary>
    public static double SocketPillLead(string? socketName)
        => 20.0 /* pin + pad */ + EstimateTextWidth(socketName, 12.0)
         + 6.0 /* label→pill gap */ + 6.0 /* pill margin */ + 12.0 /* pill padding */ + 8.0 /* row right pad */;

    /// <summary>
    /// The finite width (px) a value pill may occupy on its row before it wraps —
    /// the node's EffectiveWidth minus the fixed <paramref name="leadWidth"/>,
    /// clamped to [<see cref="PillMinWrapWidth"/>, cap]. DB pills return
    /// <see cref="DbSingleLineCap"/> (single line). The cap is
    /// <see cref="MultilinePillCap"/> for multi-line pills, else
    /// <see cref="WrapPillCap"/>. Bound to the pill's MaxWidth (via the VMs) and
    /// reused by the height-measure paths so render and measure can't diverge.
    /// </summary>
    public static double PillWrapWidth(Node? node, double leadWidth, bool isMultiline, bool isDb)
    {
        if (isDb) return DbSingleLineCap;
        double cap = isMultiline ? MultilinePillCap : WrapPillCap;
        double avail = (node is null ? NodeMinWidth : EffectiveWidth(node)) - leadWidth;
        return Math.Max(PillMinWrapWidth, Math.Min(cap, avail));
    }

    /// <summary>
    /// True when the inline pill on socket <paramref name="socketName"/> with
    /// content <paramref name="pillValue"/> renders as a wrapped multi-line
    /// editor. Mirrors <c>SocketViewModel.IsMultilineAttr</c> so the static
    /// width path can apply the same cap as the view-model.
    /// </summary>
    public static bool IsMultilinePill(string? socketName, string? pillValue)
    {
        if (!string.IsNullOrEmpty(socketName))
        {
            if (socketName!.Equals("Template", StringComparison.OrdinalIgnoreCase) ||
                socketName.Equals("Script",   StringComparison.OrdinalIgnoreCase) ||
                socketName.Equals("Payload",  StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return !string.IsNullOrEmpty(pillValue) && pillValue!.Contains('\n');
    }

    /// <summary>
    /// Resolves the inline-pill value for an input socket — null for outputs
    /// and for inputs without a stored attribute. Mirrors
    /// <c>SocketViewModel.ResolveInitialValuePill</c>.
    /// </summary>
    private static string? ResolvePillValue(Node node, Socket socket)
    {
        if (socket.Type != SocketType.Input) return null;
        if (string.IsNullOrEmpty(socket.Name)) return null;
        if (node?.Attributes == null) return null;
        return node.Attributes.TryGetValue(socket.Name!, out var v) && !string.IsNullOrEmpty(v)
            ? v
            : null;
    }

    /// <summary>
    /// Display title rendered in the node header. Mirrors
    /// <c>NodeViewModel.Title</c> — Event.Return with EventName attr →
    /// "↩ {EventName}", otherwise NodeRegistry.Template.DisplayName when
    /// set, falling back to canonical Title.
    /// <para>
    /// A user-typed inline rename is stored in
    /// <c>node.Attributes["__displayNameOverride"]</c> and wins over every
    /// template-driven rendering branch. The override is display-only —
    /// <see cref="Node.Title"/> stays the registry key
    /// (<see cref="NodeRegistry.GetTemplate"/>) and the
    /// <c>CommandManifest.Resolve</c> lookup key, so the exporter / engine
    /// continue to bind by identity even when the canvas shows a custom
    /// label. Empty / whitespace overrides are ignored so a node whose
    /// attribute was cleared returns to its registry-derived title.
    /// </para>
    /// </summary>
    public static string DisplayTitle(Node node)
    {
        var raw = node?.Title ?? string.Empty;
        // Display-name override. Checked FIRST so the user-typed
        // label wins over Event.Return's "↩ {EventName}" synthesis and the
        // template DisplayName. The attribute is non-load-bearing — exporter
        // and registry consult Node.Title, never this attribute — so an
        // override never affects script identity.
        if (node?.Attributes != null
            && node.Attributes.TryGetValue("__displayNameOverride", out var ov)
            && !string.IsNullOrWhiteSpace(ov))
            return ov;
        if (raw == "Event.Return"
            && node!.Attributes != null
            && node.Attributes.TryGetValue("EventName", out var en)
            && !string.IsNullOrWhiteSpace(en))
            return "↩ " + en;
        try
        {
            var tmpl = NodeRegistry.GetTemplate(raw);
            var display = tmpl?.DisplayName;
            return string.IsNullOrEmpty(display) ? raw : display!;
        }
        catch { return raw; }
    }

    /// <summary>
    /// Definer subtitle truncated to 22 chars + ellipsis (matching
    /// <c>NodeViewModel.Definer</c>). Returns null/empty when the node has
    /// no definer.
    /// </summary>
    public static string? TruncatedDefiner(Node node)
    {
        var raw = GetNodeDefiner(node);
        if (raw == null) return null;
        return raw.Length > 22 ? raw[..22] + "…" : raw;
    }

    /// <summary>
    /// Per-(text,size,weight) measure cache. The Architect canvas has a few
    /// hundred distinct strings on a large graph and the same string is hit
    /// from multiple call sites (intrinsic width, wire anchor, middle-attr
    /// width) — caching avoids re-running TextBlock.Measure 4-5× per node
    /// per layout pass. Concurrent because measures can occur from
    /// background threads as well (e.g. exporter snapshot paths that touch
    /// width estimates for clipping calculations); the dictionary lookup is
    /// thread-safe but TextBlock.Measure is NOT — guarded below.
    /// </summary>
    private static readonly ConcurrentDictionary<(string Text, double Size, bool Bold, bool Mono), double> s_textWidthCache
        = new();

    /// <summary>
    /// Per-node body-size cache.
    /// Keyed on <see cref="Node.Id"/>; the stored <c>Hash</c> is a content
    /// hash over Title + every socket's Name/Type/DataType/IsPlaceholder + every attribute
    /// key/value. A cache hit (id present AND hash matches) skips the
    /// socket / attribute walk in <see cref="ComputeIntrinsicWidth"/> /
    /// <see cref="ComputeIntrinsicHeight"/>. <c>Width</c> / <c>Height</c> are
    /// filled lazily (NaN = "not computed for this hash yet") because the two
    /// dimensions are requested through independent getters. Concurrent
    /// because the same off-UI-thread callers that touch
    /// <see cref="s_textWidthCache"/> (exporter snapshot width estimates) can
    /// reach the intrinsic paths.
    /// </summary>
    private static readonly ConcurrentDictionary<string, (int Hash, double Width, double Height)> s_nodeSizeCache
        = new();

    private static void StoreNodeSize(string id, int hash, double width, double height)
    {
        s_nodeSizeCache[id] = (hash, width, height);
    }

    // ─── Per-node geometry dirty gate ──────────────────────────
    // The content hash stays the STORE key of s_nodeSizeCache / s_rowHeightsCache,
    // but recomputing it on every probe made each cache hit O(sockets+attrs) —
    // and SocketAnchor probes both caches per wire endpoint on the per-frame
    // CompositionTarget.Rendering wire path. The gate makes a SETTLED node's
    // probe O(1): mutation sites (NodeViewModel.InvalidateGeometryCache, the
    // socket / middle-attr PropertyChanged handlers, the canvas's node-VM
    // PropertyChanged hook, OnGraphMutated, edit-mode exit) mark the node
    // dirty; a probe only re-runs the hash walk when the gate is dirty, when
    // the cache entry's stored hash isn't the last VERIFIED hash (so a sibling
    // cache can never serve rows/size computed for older content), or when the
    // probe budget runs out. The budget is the conservative fallback for any
    // mutation path that bypasses every hook (silent model writes): a stale
    // value self-heals within GeometryVerifyBudget probes.
    private sealed class GeometryGate
    {
        public volatile bool Dirty = true;   // default-dirty — first probe always verifies
        public int VerifiedHash;
        public int Budget;
    }

    private static readonly ConcurrentDictionary<string, GeometryGate> s_geometryGates = new();

    // Clean probes allowed between forced hash re-verifications. On the wire
    // path a dragged node is probed several times per frame, so a missed
    // mutation site heals within a few frames while settled probes stay ~O(1)
    // (1 hash walk per 64 probes ≈ 1.5% residual cost).
    private const int GeometryVerifyBudget = 64;

    /// <summary>
    /// Mark a node's geometry inputs as possibly changed so the next
    /// <see cref="IntrinsicWidth"/> / <see cref="IntrinsicHeight"/> /
    /// <see cref="SocketRowHeights"/> probe re-verifies the content hash.
    /// Cheap (one flag store) — call liberally from any mutation path;
    /// a spurious mark costs one hash walk, a missed mark risks stale wires.
    /// </summary>
    public static void MarkGeometryDirty(string? nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return;
        s_geometryGates.GetOrAdd(nodeId!, static _ => new GeometryGate()).Dirty = true;
    }

    /// <summary>
    /// Mark every known node dirty — the commit-granularity catch-all wired
    /// to <c>LogicCanvasViewModel.OnGraphMutated</c> (mirrors the Win2D
    /// per-canvas row-height cache clear on the same event).
    /// </summary>
    public static void MarkAllGeometryDirty()
    {
        foreach (var gate in s_geometryGates.Values) gate.Dirty = true;
    }

    private static GeometryGate GetGeometryGate(string nodeId)
        => s_geometryGates.GetOrAdd(nodeId, static _ => new GeometryGate());

    // True when the cached entry may be returned WITHOUT re-hashing: gate is
    // clean, the entry was stored under the last verified hash, and the
    // self-heal budget hasn't run out. Interlocked keeps the budget sane under
    // the (rare) off-UI-thread probes; a lost decrement only delays a re-verify.
    private static bool TryFastGeometryProbe(GeometryGate gate, int entryHash)
    {
        if (gate.Dirty || entryHash != gate.VerifiedHash) return false;
        return System.Threading.Interlocked.Decrement(ref gate.Budget) > 0;
    }

    private static void MarkGeometryVerified(GeometryGate gate, int hash)
    {
        gate.VerifiedHash = hash;
        gate.Budget = GeometryVerifyBudget;
    }

    /// <summary>
    /// Content hash over the inputs that drive a node's intrinsic
    /// geometry — Title, each socket's Name/Type/DataType/IsPlaceholder, and each
    /// attribute key/value. A layout-pass re-read with unchanged content hits the
    /// cache, while any rename / wire-drop / pill-edit / placeholder-activation
    /// flips the hash and forces a recompute.
    /// Offset.Y was REMOVED from this key — it is a
    /// computed layout POSITION (a size output, never a size input), so including
    /// it caused spurious cache misses + cold re-measure storms once layout
    /// settled and the measure pass wrote new Offset.Y values back.
    /// </summary>
    private static int ComputeNodeContentHash(Node node)
    {
        var hc = new System.HashCode();
        hc.Add(node.Title ?? string.Empty);
        if (node.Sockets != null)
        {
            foreach (var s in node.Sockets)
            {
                if (s is null) continue;
                hc.Add(s.Name ?? string.Empty);
                hc.Add((int)s.Type);
                hc.Add((int)s.DataType);
                // Offset.Y intentionally NOT hashed (see
                // method summary): layout-position output, not a size input.
                // IsPlaceholder STAYS: a placeholder renders no row, so it changes
                // the node's intrinsic HEIGHT and must invalidate the cache.
                hc.Add(s.IsPlaceholder);
            }
        }
        // Attributes folded in order-independently (XOR of per-pair hashes)
        // because Dictionary enumeration order can shift after Remove/Add and
        // an order-sensitive fold would then spuriously miss the cache. XOR is
        // commutative, so the same key/value set hashes identically regardless
        // of enumeration order; any key or value change still flips the fold.
        int attrFold = 0;
        if (node.Attributes != null)
        {
            foreach (var kv in node.Attributes)
                attrFold ^= System.HashCode.Combine(kv.Key ?? string.Empty, kv.Value ?? string.Empty);
        }
        hc.Add(attrFold);
        return hc.ToHashCode();
    }

    /// <summary>
    /// Lazy singleton TextBlock used to materialise a real WinUI text
    /// measure. <c>Measure(infinity, infinity)</c> gives the natural
    /// desired-width for the given string + font + weight; this matches
    /// the on-screen layout the Canvas applies. Lives forever (process-
    /// scoped) so each measure costs only the WinRT call, not an object
    /// allocation. WinUI requires UI-thread access to <see cref="UIElement"/>
    /// so the measure path falls back to the legacy estimate on background
    /// callers.
    /// </summary>
    [ThreadStatic] private static TextBlock? s_measureBlock;

    /// <summary>
    /// Real text-width measurement (px) replacing the pre-0.11.x coarse
    /// 0.55-em estimate that systematically undershot widths for wide
    /// glyphs (W, M, m, w) on the proportional Inter / Segoe UI face. The
    /// undershoot let TextWrapping="Wrap" pills break before the actual
    /// glyph budget was hit — Majo flagged this as "text pill rows wrap
    /// unnecessarily early" and "synced event nodes get too narrow after
    /// the sync rewrites their socket payloads".
    ///
    /// Implementation: a per-thread TextBlock measures the string via
    /// WinUI's native layout pipeline; the measured DesiredSize.Width is
    /// the same the on-screen layout will use. Results cache by
    /// (text, fontSize, bold) so a graph with N nodes touching repeated
    /// labels (e.g. "Flow") costs one measure per unique label. A small
    /// safety pad (1 px) absorbs sub-pixel font-render rounding so a one-
    /// glyph wrap can't sneak in.
    ///
    /// Background-thread fallback: when called off the UI thread (or
    /// before WinUI bootstrap), the per-thread TextBlock can't be
    /// instantiated and the function falls back to the legacy 0.55-em
    /// estimate. That's intentional: the only known off-UI callers are
    /// the exporter (which doesn't care about pixel-perfect width).
    /// </summary>
    public static double EstimateTextWidth(string? text, double fontSize, bool bold = false, bool mono = false)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var key = (text!, fontSize, bold, mono);
        if (s_textWidthCache.TryGetValue(key, out var cached)) return cached;

        double measured;
        try
        {
            var tb = s_measureBlock ??= new TextBlock();
            tb.Text       = text;
            tb.FontSize   = fontSize;
            tb.FontWeight = bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
            // Use the canvas's primary font where available so the measure
            // matches what NodeView renders. Application.Current?.Resources
            // returns null during early bootstrap; fall through to the
            // TextBlock's default font in that case.
            //
            // 0.11.x pill-width fix — the inline value pill in NodeView.xaml
            // is rendered in MonoFont (JetBrains Mono / Cascadia / Consolas);
            // monospaced faces have systematically wider average advance than
            // the proportional SansFont (Inter / Segoe UI Variable). Measuring
            // pill content against SansFont under-estimated the painted pill
            // width by 30-40% — "Hello chat!" and "(wired)" routinely wrapped
            // into multi-line pills on Twitch.SendChat / Array.Get / DB.* /
            // Logic.EnumMatch nodes even though the body had room. Callers
            // measuring pill text MUST pass mono=true so the body grows to
            // fit the actual painted glyph width.
            try
            {
                string fontKey = mono ? "MonoFont" : "SansFont";
                if (Application.Current?.Resources is { } res
                    && res.TryGetValue(fontKey, out var fontObj)
                    && fontObj is FontFamily ff)
                {
                    tb.FontFamily = ff;
                }
            }
            catch { /* font lookup is best-effort */ }
            tb.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            measured = Math.Ceiling(tb.DesiredSize.Width) + 1.0; // +1 px safety pad
        }
        catch
        {
            // Off the UI thread or pre-bootstrap — fall back to the legacy
            // em-ratio estimate. Mono fonts have ~0.62 em advance per char
            // (vs ~0.55 for proportional); use the higher number when the
            // caller knows the render target is monospaced so the budget
            // doesn't undershoot in the fallback path either.
            double emRatio = mono ? 0.62 : (bold ? 0.60 : 0.55);
            measured = Math.Ceiling(text.Length * fontSize * emRatio);
        }

        // The GPU canvas draws with Win2D/DirectWrite, whose advance widths
        // drift a few px from the XAML TextBlock measure above — enough for the
        // renderer's CharacterEllipsis to trim the last letters off a pill that
        // was budgeted from the XAML number ("text pills cut off the last
        // couple letters"). Take the MAX of both engines so every consumer of
        // this one function (intrinsic node width, pill rect, wrap decision)
        // reserves what the Win2D draw will actually need; the retained XAML
        // path just gains a few px of harmless headroom. Cached below, so the
        // double measure only costs on the first sight of a string.
        double win2d = Win2DTextMeasure.MeasureWidth(text!, fontSize, bold, mono);
        if (win2d > 0)
        {
            measured = Math.Max(measured, Math.Ceiling(win2d) + 1.0); // same +1 px safety pad
        }
        else if (!Win2DTextMeasure.IsPermanentlyUnavailable)
        {
            // Oracle temporarily unavailable (device not created yet at early
            // bootstrap, or transient device loss). Do NOT cache — a first-
            // writer-wins entry would freeze this string at the smaller
            // XAML-only width for the whole session and the trim would come
            // back for exactly that string. Return the estimate for now; the
            // next call re-measures with the oracle up. Headless hosts latch
            // IsPermanentlyUnavailable and fall through to normal caching.
            return measured;
        }

        s_textWidthCache[key] = measured;
        return measured;
    }

    /// <summary>
    /// Clear the text-width cache AND the per-node body-size cache. Called
    /// when the bound Graph swaps so a new project's strings / node ids don't
    /// bloat the caches with stale entries from the previous one. Cheap O(n)
    /// clear; both caches rebuild lazily as the new graph paints.
    /// <para>
    /// The node-size cache is keyed on Node.Id — graph reloads
    /// re-Guid sockets but node ids can collide across sessions of the same
    /// .phxg, so clearing here together with the text cache keeps a stale
    /// (id, hash) entry from masking a recomputed body on the next load.
    /// </para>
    /// </summary>
    public static void ResetTextWidthCache()
    {
        // Do NOT clear s_textWidthCache on graph load.
        // Its key is (text, size, bold, mono) and glyph metrics are process-scoped
        // (Phoenix has no runtime theme/font switch), so a measured width is valid
        // for the whole session regardless of which graph is open. Clearing it
        // forced a cold TextBlock.Measure storm on every reload; the only cost
        // of keeping it is a few KB of string→width
        // tuples. Only s_nodeSizeCache MUST clear here: it is keyed on Node.Id,
        // which CAN collide across .phxg files, so a stale (id, hash) entry could
        // otherwise mask a recomputed body on the next load.
        s_nodeSizeCache.Clear();
        // Per-row height cache is also Node.Id-keyed — clear in lockstep.
        s_rowHeightsCache.Clear();
        // Dirty gates are Node.Id-keyed too; a fresh gate is default-dirty so
        // the new graph's first probes always verify.
        s_geometryGates.Clear();
    }

    /// <summary>
    /// Intrinsic body height for the given node — header band + post-header
    /// gap + max(input rows, output rows) * row height + 8px bottom padding.
    /// Used by <c>NodeViewModel.Height</c> in non-compact mode.
    /// </summary>
    public static double IntrinsicHeight(Node node)
    {
        // Same per-node cache as IntrinsicWidth —
        // shares the s_nodeSizeCache entry keyed on node.Id + content hash.
        if (node is not null && !string.IsNullOrEmpty(node.Id))
        {
            var gate = GetGeometryGate(node.Id);
            bool hasCached = s_nodeSizeCache.TryGetValue(node.Id, out var cached);
            // Settled fast path — see IntrinsicWidth.
            if (hasCached && !double.IsNaN(cached.Height) && TryFastGeometryProbe(gate, cached.Hash))
                return cached.Height;
            gate.Dirty = false; // clear BEFORE hashing — a concurrent mutation re-marks
            int hash = ComputeNodeContentHash(node);
            MarkGeometryVerified(gate, hash);
            if (hasCached && cached.Hash == hash && !double.IsNaN(cached.Height))
                return cached.Height;
            double h = ComputeIntrinsicHeight(node);
            // Preserve any already-cached width for this same hash so the two
            // dimensions don't evict each other when computed independently.
            double w = (s_nodeSizeCache.TryGetValue(node.Id, out var prev) && prev.Hash == hash)
                ? prev.Width
                : double.NaN;
            StoreNodeSize(node.Id, hash, width: w, height: h);
            return h;
        }
        return ComputeIntrinsicHeight(node!);
    }

    private static double ComputeIntrinsicHeight(Node node)
    {
        // Flow.Reroute is a fixed 20×20 diamond; the standard
        // header + socket-row arithmetic doesn't apply.
        if (IsReroute(node)) return 20.0;

        int inputCount  = 0;
        int outputCount = 0;
        if (node?.Sockets != null)
        {
            foreach (var s in node.Sockets)
            {
                if (s.Type == SocketType.Input)  inputCount++;
                else                              outputCount++;
            }
        }
        int rows = Math.Max(inputCount, outputCount);
        double sockH = MeasureSocketRowsHeight(node!, rows);

        // Middle-attribute rows push the body taller. The
        // one-shot top pad + dotted divider sits above the area; each row's
        // height is measured DYNAMICALLY rather than assumed to be a
        // fixed MiddleAttrRowHeight=22. Multi-line middle-attribute pills
        // (Template / Script / Payload, or any newline-containing value) wrap
        // and grow past 22px, so the fixed-count budget clipped the read-only
        // display vertically; MeasureMiddleAttributesHeight sums the actual
        // wrapped row heights against the same body-width column the painted
        // pill lays out in.
        double middleH = MeasureMiddleAttributesHeight(node!);

        return FirstSocketRowY(node!) + sockH + middleH + 8.0;
    }

    /// <summary>
    /// Socket-row area height. Pre-fix this was
    /// rows * <see cref="SocketRowHeight"/> (a fixed 24px/row), which CLIPPED a
    /// wrapped socket-row pill (e.g. a long <c>Twitch.SendChat</c> Message)
    /// vertically — only middle-attribute rows were ever measured. Now each input
    /// row whose pill wraps is measured at the SAME <see cref="PillWrapWidth"/> the
    /// pill renders at, so the node grows tall enough to show the whole value. DB
    /// pills (single-line) and empty / single-line rows keep the SocketRowHeight
    /// floor, so simple nodes are unchanged.
    /// </summary>
    private static double MeasureSocketRowsHeight(Node node, int rows)
    {
        if (rows <= 0 || node is null) return rows <= 0 ? 0 : rows * SocketRowHeight;
        // Sum the per-row heights from the shared SocketRowHeights
        // helper so the body-height budget, the GPU renderer, and the wire-anchor
        // row centre all derive from ONE computation and can never diverge.
        var heights = SocketRowHeights(node);
        double total = 0;
        for (int r = 0; r < rows; r++)
            total += r < heights.Length ? heights[r] : SocketRowHeight;
        return total;
    }

    // ─── Per-row socket heights + dynamic row centre ──────────
    // The Win2D GPU renderer and the wire anchor (SocketAnchor) must position
    // each socket row at the SAME Y the body measures to. Before this, the GPU
    // path used a static SocketRowHeight=24/row estimate, so a multi-line pill
    // that grew its row left every row below it (and their pins/wires) floating
    // at the old positions. SocketRowHeights returns the per-row height (row r =
    // max(SocketRowHeight, wrapped input[r] pill height)); RowCenterYDynamic sums
    // them. For a node with no wrapped pill every row is SocketRowHeight, so
    // RowCenterYDynamic == RowCenterY and single-line nodes are byte-identical to
    // the pre-fix layout.
    private static readonly ConcurrentDictionary<string, (int Hash, double[] Rows)> s_rowHeightsCache = new();

    /// <summary>
    /// Per-row heights for the node's socket grid, indexed 0..max(inputs,outputs)-1.
    /// Row r is the floor <see cref="SocketRowHeight"/> unless input socket r carries
    /// a non-DB wrapped pill, in which case it grows to the measured wrapped height
    /// (same measure <see cref="MeasureSocketRowsHeight"/> sums for the body height).
    /// Cached by content hash (mirrors the IntrinsicWidth/Height cache) so the hot
    /// SocketAnchor / GPU-draw callers don't re-measure every frame.
    /// </summary>
    public static double[] SocketRowHeights(Node node)
    {
        if (node?.Sockets == null) return System.Array.Empty<double>();
        int inN = 0, outN = 0;
        foreach (var s in node.Sockets) { if (s.Type == SocketType.Input) inN++; else outN++; }
        int rows = Math.Max(inN, outN);
        if (rows == 0) return System.Array.Empty<double>();
        if (!string.IsNullOrEmpty(node.Id))
        {
            var gate = GetGeometryGate(node.Id);
            bool hasCached = s_rowHeightsCache.TryGetValue(node.Id, out var cached);
            // Settled fast path — see IntrinsicWidth. The verified-hash
            // comparison also protects against a sibling cache having advanced
            // the gate to newer content while this entry still holds old rows.
            if (hasCached && TryFastGeometryProbe(gate, cached.Hash))
                return cached.Rows;
            gate.Dirty = false; // clear BEFORE hashing — a concurrent mutation re-marks
            int hash = ComputeNodeContentHash(node);
            MarkGeometryVerified(gate, hash);
            if (hasCached && cached.Hash == hash)
                return cached.Rows;
            var arr = ComputeSocketRowHeightsArray(node, rows);
            s_rowHeightsCache[node.Id] = (hash, arr);
            return arr;
        }
        return ComputeSocketRowHeightsArray(node, rows);
    }

    private static double[] ComputeSocketRowHeightsArray(Node node, int rows)
    {
        var arr = new double[rows];
        System.Collections.Generic.List<Socket>? inputs = null;
        if (node.Sockets != null)
        {
            inputs = new System.Collections.Generic.List<Socket>();
            foreach (var s in node.Sockets)
                if (s.Type == SocketType.Input) inputs.Add(s);
        }
        for (int r = 0; r < rows; r++)
        {
            double rowH = SocketRowHeight; // floor — covers the paired output row + single-line inputs
            if (inputs != null && r < inputs.Count)
            {
                var s = inputs[r];
                string? pill = ResolvePillValue(node, s);
                if (!string.IsNullOrEmpty(pill) && !IsDbPill(node, s))
                {
                    double w = PillWrapWidth(node, SocketPillLead(s.Name), IsMultilinePill(s.Name, pill), isDb: false);
                    double measured = MeasureWrappedTextHeight(pill, 11.0, w, mono: true);
                    rowH = Math.Max(rowH, measured + 4.0); // +4 for the pill Border vertical margin/padding
                }
            }
            arr[r] = rowH;
        }
        return arr;
    }

    /// <summary>
    /// Y centre (node-relative, INNER-top reference) of socket row
    /// <paramref name="rowIndex"/>, accounting for rows above it that grew tall to
    /// fit a wrapped multi-line pill. Identical to <see cref="RowCenterY"/> when no
    /// row wraps. Used by <see cref="SocketAnchor"/> (wire endpoints) and the Win2D
    /// GPU renderer + pill hit-test so all three agree on each row's painted centre.
    /// </summary>
    public static double RowCenterYDynamic(Node node, int rowIndex)
    {
        if (node is null || rowIndex < 0) return RowCenterY(node!, rowIndex);
        var heights = SocketRowHeights(node);
        if (heights.Length == 0) return RowCenterY(node, rowIndex);
        double y = FirstSocketRowY(node);
        int upto = Math.Min(rowIndex, heights.Length);
        for (int r = 0; r < upto; r++) y += heights[r];
        double h = rowIndex < heights.Length ? heights[rowIndex] : SocketRowHeight;
        return y + h / 2.0;
    }

    /// <summary>
    /// Dynamic middle-attribute area height. Walks the same
    /// DefaultProperties keys <see cref="CountMiddleAttributes"/> /
    /// <see cref="NodeViewModel.RebuildMiddleAttributes"/> surface and measures
    /// each row's height with the pill text wrapped to the body's pill-column
    /// width. Returns 0 when the node has no middle-attribute rows (so the
    /// area + divider collapse). The per-row floor is
    /// <see cref="MiddleAttrRowHeight"/> so single-line rows keep their prior
    /// budget; multi-line / wrapped rows grow beyond it.
    /// </summary>
    private static double MeasureMiddleAttributesHeight(Node node)
    {
        try
        {
            var tmpl = NodeRegistry.GetTemplate(node?.Title ?? string.Empty);
            if (tmpl?.DefaultProperties == null || tmpl.DefaultProperties.Count == 0 || node?.Sockets == null)
                return 0;

            var socketNames = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var s in node.Sockets)
                if (s.Type == SocketType.Input && !string.IsNullOrEmpty(s.Name))
                    socketNames.Add(s.Name);

            // Each row's wrapped height is measured at the SAME
            // PillWrapWidth the pill actually renders at (MiddleAttrPillLead), so
            // the reserved height matches the on-screen wrap exactly. Pre-fix the
            // measure used a bespoke pillBudget that diverged from the rendered
            // MaxWidth, so a wrapped middle-attr pill was clipped vertically.
            double total = 0;
            int rowCount = 0;
            foreach (var kv in tmpl.DefaultProperties)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                if (socketNames.Contains(kv.Key)) continue;
                rowCount++;

                string? liveValue = null;
                if (node.Attributes != null && node.Attributes.TryGetValue(kv.Key, out var v))
                    liveValue = v;
                var effective = liveValue ?? kv.Value ?? string.Empty;

                // ONE shared per-row formula, also used by the Win2D draw + click
                // hit-test, so the reserved height, the painted wrap, and the
                // clickable target can never drift (the fixed-height GPU draw
                // overflowed a long Template pill over the output labels).
                total += MiddleAttrRowHeightFor(node, kv.Key, effective);
            }

            if (rowCount == 0) return 0;
            return MiddleAttrAreaTopPad + MiddleAttrDividerH + total;
        }
        catch
        {
            // Registry miss / instance-without-template — fall back to the
            // fixed-count estimate so the body never collapses to header-only.
            int middleCount = CountMiddleAttributes(node);
            return middleCount > 0
                ? MiddleAttrAreaTopPad + MiddleAttrDividerH + middleCount * MiddleAttrRowHeight
                : 0;
        }
    }

    /// <summary>
    /// Pill-wrap width for a middle-attribute row — the finite width its value
    /// pill wraps within. Declared multi-line keys (Template / Script / Payload,
    /// or a newline value) cap at <see cref="MultilinePillCap"/>; every other key
    /// widens to <see cref="WrapPillCap"/> before wrapping. Matches the width
    /// <see cref="MiddleAttrRowHeightFor"/> measures the wrapped height at, so the
    /// GPU render fills the reserved height exactly.
    /// </summary>
    public static double MiddleAttrPillWrapWidth(Node node, string key, string? value)
    {
        double pickerLead = IsOptionsPickerAttr(node, key) ? MiddleAttrPickerChevronWidth : 0.0;
        return PillWrapWidth(node, MiddleAttrPillLead(key) + pickerLead,
                             IsMultilinePill(key, value ?? string.Empty), isDb: false);
    }

    /// <summary>
    /// Rendered height (px) of ONE middle-attribute row for <paramref name="value"/>.
    /// Bool (☑/☐) and empty rows are the fixed <see cref="MiddleAttrRowHeight"/>
    /// floor; a text row grows to its wrapped height (measured at the SAME
    /// <see cref="MiddleAttrPillWrapWidth"/> the pill renders within) so a long
    /// Template / Script / Payload wraps and grows the row instead of the GPU
    /// drawing it single-line over the node's output labels ("dismembered" pill).
    /// This is the single per-row formula shared by the height budget
    /// (<see cref="MeasureMiddleAttributesHeight"/>) and the Win2D draw + click
    /// hit-test, so they can never drift.
    /// </summary>
    public static double MiddleAttrRowHeightFor(Node node, string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return MiddleAttrRowHeight;
        bool isBool = string.Equals(value, "true",  System.StringComparison.OrdinalIgnoreCase)
                   || string.Equals(value, "false", System.StringComparison.OrdinalIgnoreCase);
        if (isBool) return MiddleAttrRowHeight;

        double budget   = MiddleAttrPillWrapWidth(node, key, value);
        double measured = MeasureWrappedTextHeight(value, 11.0, budget, mono: true);
        // + 2px row padding (NodeView.xaml Border Margin 0,1,0,1); floored at the
        // single-row height so short rows keep their prior budget.
        return Math.Max(MiddleAttrRowHeight, measured + 2.0);
    }

    /// <summary>
    /// Measure the wrapped height (px) of <paramref name="text"/>
    /// laid out against <paramref name="widthConstraint"/> in the canvas font
    /// at <paramref name="fontSize"/>. Mirrors <see cref="EstimateTextWidth"/>'s
    /// per-thread TextBlock measure pipeline + the same background-thread
    /// fallback (estimate the wrap-line count from the natural single-line
    /// width). Used by <see cref="MeasureMiddleAttributesHeight"/> so the
    /// read-only middle-attribute display reserves the full wrapped height
    /// instead of clipping at the fixed 22px row.
    /// </summary>
    // Public so the Win2D GPU renderer's per-row height helper can
    // reuse the EXACT wrapped-height measure the body-height budget uses.
    public static double MeasureWrappedTextHeight(string? text, double fontSize, double widthConstraint, bool mono)
    {
        if (string.IsNullOrEmpty(text)) return fontSize + 4.0;
        try
        {
            var tb = s_measureBlock ??= new TextBlock();
            tb.Text         = text;
            tb.FontSize     = fontSize;
            tb.FontWeight   = Microsoft.UI.Text.FontWeights.Normal;
            tb.TextWrapping = TextWrapping.Wrap;
            try
            {
                string fontKey = mono ? "MonoFont" : "SansFont";
                if (Application.Current?.Resources is { } res
                    && res.TryGetValue(fontKey, out var fontObj)
                    && fontObj is FontFamily ff)
                {
                    tb.FontFamily = ff;
                }
            }
            catch { /* font lookup is best-effort */ }
            double w = widthConstraint > 0 ? widthConstraint : double.PositiveInfinity;
            tb.Measure(new Windows.Foundation.Size(w, double.PositiveInfinity));
            double h = Math.Ceiling(tb.DesiredSize.Height);
            return h;
        }
        catch
        {
            // Off the UI thread / pre-bootstrap — estimate line count from the
            // natural single-line width vs the constraint.
            double naturalW = EstimateTextWidth(text, fontSize, mono: mono);
            double lineH = Math.Ceiling(fontSize * 1.35);
            if (widthConstraint <= 0) return lineH;
            int lines = Math.Max(1, (int)Math.Ceiling(naturalW / widthConstraint));
            // Add one line per explicit newline as a floor.
            int explicitLines = 1;
            foreach (var c in text!) if (c == '\n') explicitLines++;
            return Math.Max(lines, explicitLines) * lineH;
        }
        finally
        {
            // Always restore the shared measure block to NoWrap — even if Measure()
            // or DesiredSize threw — so EstimateTextWidth (which reuses
            // s_measureBlock at infinite width) never inherits a stale Wrap state.
            if (s_measureBlock is not null) s_measureBlock.TextWrapping = TextWrapping.NoWrap;
        }
    }

    /// <summary>
    /// Count of middle-attribute rows for <paramref name="node"/> — every
    /// template <c>DefaultProperties</c> key whose name doesn't match an
    /// existing socket on the node (case-insensitive). Pulled out of
    /// <see cref="IntrinsicHeight"/> so future callers (debug overlays /
    /// minimap) can read the same count without recomputing.
    /// </summary>
    public static int CountMiddleAttributes(Node? node)
    {
        if (node == null) return 0;
        try
        {
            var tmpl = NodeRegistry.GetTemplate(node.Title ?? string.Empty);
            if (tmpl?.DefaultProperties == null || tmpl.DefaultProperties.Count == 0)
                return 0;

            var socketNames = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            if (node.Sockets != null)
                foreach (var s in node.Sockets)
                    if (s.Type == SocketType.Input && !string.IsNullOrEmpty(s.Name))
                        socketNames.Add(s.Name);

            int n = 0;
            foreach (var kv in tmpl.DefaultProperties)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                if (socketNames.Contains(kv.Key)) continue;
                n++;
            }
            return n;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Returns true when the node renders an italic definer subtitle in
    /// its header (EventName / VariableName / Commands / MacroName / etc.).
    /// Kept in sync with <see cref="NodeViewModel.Definer"/>.
    /// </summary>
    public static bool HasDefiner(Node node)
        => !string.IsNullOrWhiteSpace(GetNodeDefiner(node));

    /// <summary>
    /// Per-Title attribute lookup that surfaces the distinguishing value for
    /// nodes that look identical at the title level. Mirrors pre-T15
    /// GetNodeDefiner. Returns null when the node has no distinguishing
    /// attribute or the attribute is blank. Untruncated — callers should
    /// truncate for display (NodeViewModel.Definer caps at 22 chars).
    /// </summary>
    public static string? GetNodeDefiner(Node node)
    {
        if (node?.Attributes == null) return null;
        var raw = node.Title ?? string.Empty;
        string? v = null;
        switch (raw)
        {
            case "Event.Trigger":
            case "Event.Executor":
            case "Event.Return":
                node.Attributes.TryGetValue("EventName", out v); break;
            case "Var.Get":
            case "Var.Set":
            case "Var.Inc":
            case "Var.Toggle":
                node.Attributes.TryGetValue("VariableName", out v); break;
            case "Public.Get":
            case "Public.Set":
                node.Attributes.TryGetValue("KeyName", out v); break;
            case "Twitch.ChatMessage": // legacy title — retitled to Chat.Message on load, kept for pre-migration renders
            case "Chat.Message":
                node.Attributes.TryGetValue("Commands", out v); break;
            case "Macro.Call":
                node.Attributes.TryGetValue("MacroName", out v); break;
            case "Process.Start":
                node.Attributes.TryGetValue("ProcessName", out v); break;
            case "Value.String":
            case "Value.Int":
            case "Value.Float":
            case "Value.Bool":
                node.Attributes.TryGetValue("Value", out v); break;
            case "State.OnChange":
                node.Attributes.TryGetValue("StateName", out v); break;
            case "Bus.OnMessage":
                node.Attributes.TryGetValue("EventType", out v); break;
            case "Schedule.Cron":
                node.Attributes.TryGetValue("CronExpression", out v); break;
            case "Visual.Trigger":
            {
                // Surface the routing target
                // so the node reads "which trigger in which layer it goes" at a
                // glance (Majo). The previous code read a stale "CompositionId"
                // attribute the Visualist copy-snippet never writes — it writes
                // LayerID / WidgetID / TriggerName (LogicCanvasView.Clipboard.cs)
                // — so the subtitle was ALWAYS blank ("no information is given").
                // Build a "<layer>/<widget>:<trigger>" address from whatever
                // addressing attributes are present (TruncatedDefiner caps the
                // displayed length; the node header grows to fit it).
                node.Attributes.TryGetValue("LayerID", out var vtLayer);
                node.Attributes.TryGetValue("WidgetID", out var vtWidget);
                node.Attributes.TryGetValue("TriggerName", out var vtTrigger);
                string loc = vtLayer ?? "";
                if (!string.IsNullOrWhiteSpace(vtWidget))
                    loc = string.IsNullOrWhiteSpace(loc) ? vtWidget! : loc + "/" + vtWidget;
                if (!string.IsNullOrWhiteSpace(vtTrigger))
                    v = string.IsNullOrWhiteSpace(loc) ? vtTrigger : loc + ":" + vtTrigger;
                else
                    v = string.IsNullOrWhiteSpace(loc) ? null : loc;
                break;
            }
        }
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}
