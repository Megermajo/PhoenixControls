using System;
using System.Linq;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Architect.Core;
using Phoenix.Controls.Architect.WinUI.Hosting;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Windows.Foundation;
using Windows.UI;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

// Context-menu partial — non-modal MenuFlyouts only (per the standing rule
// against MessageBox for repeatable rejections). Right-click empty space
// shows a small action menu plus the SpawnPaletteFlyout entry-point;
// right-click on a node/link/frame/socket/pill shows the matching action
// menu here.
public sealed partial class LogicCanvasView
{
    // ── Fluent icon font + glyph constants ──────────────────────────────
    // Centralised so every menu item is consistent and a Win10-19041- fallback
    // is one knob away (Segoe Fluent Icons → Segoe MDL2 Assets).
    private const string IconFontFamily = "Segoe Fluent Icons,Segoe MDL2 Assets";

    // Menu glyph cheatsheet (taken from Design_Orders.md §9 + the task brief):
    //   E70F edit             E74D delete           E7A7 undo
    //   E7A6 redo             E710 new / add        E711 cross / close
    //   E74E save             E8E5 open             E713 settings
    //   E897 help / docs      E721 search / palette E77F paste (clipboard)
    //   E70D chevron down     E76C chevron right    E946 info
    private const string GlyphEdit       = "";
    private const string GlyphDelete     = "";
    private const string GlyphUndo       = "";
    private const string GlyphRedo       = "";
    private const string GlyphNew        = "";
    private const string GlyphCross      = "";
    private const string GlyphDocs       = "";
    private const string GlyphSearch     = "";
    private const string GlyphPaste      = "";
    private const string GlyphInfo       = "";
    private const string GlyphRefresh    = "";
    private const string GlyphWarning    = "";
    private const string GlyphPin        = "";

    /// <summary>
    /// Build a MenuFlyoutItem with a Fluent glyph icon + click handler.
    /// 0.10.0 — every menu item across the canvas surface routes through
    /// this helper so the icon column is uniform. <paramref name="acceleratorHint"/>
    /// (e.g. "Ctrl+G", "C") sets KeyboardAcceleratorTextOverride so the
    /// shortcut renders in the trailing menu column without forcing the
    /// caller to also wire a KeyboardAccelerator (those live on the canvas
    /// keyboard partial; the menu just advertises them).
    /// </summary>
    private static MenuFlyoutItem NewMenuItem(string text, string glyph, Action onClick, string? acceleratorHint = null,
        string? foregroundBrushKey = null)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            Icon = string.IsNullOrEmpty(glyph)
                ? null
                : new FontIcon { Glyph = glyph, FontFamily = new FontFamily(IconFontFamily) },
        };
        if (!string.IsNullOrEmpty(acceleratorHint))
            item.KeyboardAcceleratorTextOverride = acceleratorHint;
        // Semantic color coding — the pre-T15 ContextMenus set explicit
        // ForeColor (docs = light blue/ember, delete = red, frames = gold) for
        // glance recognition. Resolve the theme brush defensively; on miss the
        // item keeps the default MenuFlyoutItem foreground so the menu still
        // surfaces. Tinting BOTH the item text and its FontIcon keeps the icon
        // column reading the same semantic as the label.
        if (!string.IsNullOrEmpty(foregroundBrushKey)
            && TryResolveBrush(foregroundBrushKey) is { } fg)
        {
            item.Foreground = fg;
            if (item.Icon is FontIcon fi) fi.Foreground = fg;
        }
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>
    /// 0.11.x polish — build a MenuFlyout pre-styled to match the spawn
    /// palette's "labelled lid" identity: Radius3 rounded card, coal-card
    /// background, divider hairline, plus an eyebrow header at the top so
    /// the menu reads as a Phoenix surface rather than a system-grey
    /// MenuFlyout. Mirrors <see cref="SpawnPaletteFlyout"/>'s header band
    /// styling so the right-click menu and the Space palette share the
    /// same visual rhythm.
    /// </summary>
    private static MenuFlyout NewStyledMenuFlyout(string eyebrowText)
    {
        var flyout = new MenuFlyout();

        // Pickup the existing implicit MenuFlyoutPresenter (CoalCardBrush /
        // CoalDivider / SansFont 12) and bump the corner radius to Radius3
        // so the card matches the spawn palette's roundedness. The other
        // properties are inherited from the implicit style via BasedOn so
        // a future global tweak still cascades.
        var presenterStyle = new Microsoft.UI.Xaml.Style(typeof(MenuFlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(MenuFlyoutPresenter.CornerRadiusProperty,
            (CornerRadius)Application.Current.Resources["Radius3Corner"]));
        presenterStyle.Setters.Add(new Setter(MenuFlyoutPresenter.PaddingProperty,
            new Thickness(0)));
        flyout.MenuFlyoutPresenterStyle = presenterStyle;

        if (!string.IsNullOrEmpty(eyebrowText))
        {
            AddEyebrowHeader(flyout, eyebrowText);
        }

        return flyout;
    }

    /// <summary>
    /// 0.11.x polish — emit a non-clickable eyebrow header into the flyout.
    /// MenuFlyout doesn't have a native header item type, so this uses a
    /// disabled MenuFlyoutItem whose Text styling is overridden to mirror
    /// the spawn palette's eyebrow band: SansFont SemiBold 9 pt, ember
    /// foreground, wide character spacing, sitting on a slightly-brighter
    /// CoalHoverBrush band so it reads as a labelled lid. Followed by a
    /// MenuFlyoutSeparator so the header visually separates from the
    /// clickable items below.
    /// </summary>
    private static void AddEyebrowHeader(MenuFlyout flyout, string text)
    {
        var header = new MenuFlyoutItem
        {
            Text         = text,
            IsHitTestVisible = false,
            FocusVisualPrimaryThickness = new Thickness(0),
            FontSize         = 9,
            FontWeight       = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 180,
            Padding          = new Thickness(14, 8, 14, 6),
        };
        // Theme tokens — resolve defensively. `DisplayFont` is authored as
        // an x:String in PhoenixDark.xaml (XAML's markup converter promotes
        // it to a FontFamily at setter time); the C# resource lookup
        // returns the raw string, so a hard (FontFamily) cast throws
        // InvalidCastException — which silently killed OnHostRightTapped
        // before ShowAt could fire (Majo: "right-click menu stopped
        // showing entirely"). TryResolveBrush / TryResolveFontFamily wrap
        // the lookup in a try/catch and accept both shapes; on miss the
        // property stays at its default so the menu still surfaces.
        if (TryResolveBrush("Ember200Brush") is { } emberBrush)
            header.Foreground = emberBrush;
        if (TryResolveFontFamily("DisplayFont") is { } displayFont)
            header.FontFamily = displayFont;
        if (TryResolveBrush("CoalHoverBrush") is { } hoverBrush)
            header.Background = hoverBrush;
        flyout.Items.Add(header);
        flyout.Items.Add(new MenuFlyoutSeparator());
    }

    /// <summary>
    /// Resolve a <see cref="Brush"/> from the app resource dictionary.
    /// Returns the brush when the key resolves to one; returns null on
    /// miss / wrong type / pre-app construction. Never throws.
    /// </summary>
    private static Brush? TryResolveBrush(string key)
    {
        try
        {
            if (Application.Current?.Resources is { } res
                && res.TryGetValue(key, out var found)
                && found is Brush brush) return brush;
        }
        catch { /* designer-time / pre-app construction */ }
        return null;
    }

    /// <summary>
    /// Resolve a <see cref="FontFamily"/> from the app resource dictionary.
    /// Accepts both <c>FontFamily</c> and <c>string</c> resource shapes —
    /// PhoenixDark.xaml authors fonts as <c>x:String</c> so the C# lookup
    /// returns the raw string; this wrapper constructs a FontFamily from
    /// it in that case. Never throws.
    /// </summary>
    private static FontFamily? TryResolveFontFamily(string key)
    {
        try
        {
            if (Application.Current?.Resources is { } res
                && res.TryGetValue(key, out var found))
            {
                if (found is FontFamily ff) return ff;
                if (found is string s && !string.IsNullOrEmpty(s))
                    return new FontFamily(s);
            }
        }
        catch { /* designer-time / pre-app construction */ }
        return null;
    }

    /// <summary>Toggle variant (preserves IsChecked state) with the glyph helper.</summary>
    private static ToggleMenuFlyoutItem NewToggleItem(string text, string glyph, bool isChecked, Action onClick)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = text,
            IsChecked = isChecked,
            Icon = string.IsNullOrEmpty(glyph)
                ? null
                : new FontIcon { Glyph = glyph, FontFamily = new FontFamily(IconFontFamily) },
        };
        item.Click += (_, _) => onClick();
        return item;
    }

    private void OnHostRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var hostPoint = e.GetPosition(HostRoot);

        // Pill right-click — the inline value pill in NodeView.xaml is the
        // only Border tagged with a SocketViewModel.
        if (TryHitPill(e.OriginalSource) is SocketViewModel pillSock)
        {
            ShowPillMenu(pillSock, hostPoint);
            e.Handled = true;
            return;
        }

        // Resolve the right-click target from the
        // model in immediate mode (no per-node visual elements to walk).
        // Sockets FIRST: ResolveModelHit only returns node / wire / frame, so
        // on the GPU canvas the `case SocketViewModel` below was dead code —
        // the per-socket menu (the ONLY home of "Set Type" / "Remove Socket"
        // for dynamic event bubbles) was unreachable and every right-click on
        // a bubble fell through to the node menu. Same GPU-vs-retained
        // divergence class as the frame-header fix below: the retained path
        // resolved sockets through tagged pin elements that don't exist on
        // the Win2D canvas, so the socket arm silently died with them.
        object? hit;
        if (_useImmediateMode)
        {
            var canvasPt = HostToCanvas(hostPoint);
            hit = (object?)ResolveSocketForMenuAtCanvasPoint(canvasPt)
                  ?? ResolveModelHit(canvasPt);
        }
        else
        {
            hit = HitTagFrom(e.OriginalSource);
        }

        switch (hit)
        {
            case NodeViewModel n:
                if (_vm is not null)
                {
                    bool inMulti = _vm.SelectedNodes.Count > 1 && _vm.SelectedNodes.Contains(n);
                    if (!inMulti) _vm.Selection = n;
                }
                ShowNodeMenu(n, hostPoint);
                break;
            case LinkViewModel l:
                if (_vm is not null) _vm.Selection = l;
                ShowLinkMenu(l, hostPoint);
                break;
            case FrameViewModel f:
                // 0.11.x polish — only the frame's header strip (or label)
                // surfaces the frame-context menu; right-clicking inside the
                // frame body falls through to the standard empty-canvas
                // menu so the user can spawn nodes / paste inside a frame
                // without first dismissing a frame-only menu. Mirrors the
                // pointer-press header-vs-body split that already gates
                // the move-drag gesture.
                //
                // On the Win2D GPU canvas IsFrameHeaderHit walks a visual tree
                // that doesn't exist (e.OriginalSource is the bare CanvasControl),
                // so it always returned false and the frame menu — the only place
                // "Rename…" lives — never appeared. In immediate mode a frame hit
                // from ResolveModelHit is already a deliberate header/edge click
                // (body clicks resolve to null → the empty-canvas menu via
                // default), so route it straight to the frame menu.
                if (_useImmediateMode || IsFrameHeaderHit(e.OriginalSource))
                {
                    ShowFrameMenu(f, hostPoint);
                }
                else
                {
                    ShowEmptyCanvasMenu(hostPoint);
                }
                break;
            case SocketViewModel sv:
                if (FindNodeOwning(sv) is { } parent)
                {
                    if (_vm is not null) _vm.Selection = parent;
                    // Socket dispatch — every non-flow, non-placeholder pin
                    // gets the per-socket menu (Break / Reset / Promote /
                    // Set Type on dynamic-event hosts). Flow and placeholder
                    // pins fall through to the node menu since they have no
                    // per-pin value semantics.
                    if (sv.IsPlaceholder
                        || string.Equals(sv.Label, "Flow", StringComparison.OrdinalIgnoreCase))
                    {
                        ShowNodeMenu(parent, hostPoint);
                    }
                    else
                    {
                        ShowSocketMenu(parent, sv, hostPoint);
                    }
                }
                break;
            default:
                ShowEmptyCanvasMenu(hostPoint);
                break;
        }
        e.Handled = true;
    }

    /// <summary>
    /// Returns the SocketViewModel when <paramref name="originalSource"/> is
    /// inside the inline value-pill Border in NodeView.xaml. Walks up the
    /// visual tree looking for a Border whose Tag is a SocketViewModel.
    /// </summary>
    private static SocketViewModel? TryHitPill(object originalSource)
    {
        DependencyObject? cur = originalSource as DependencyObject;
        while (cur is not null)
        {
            if (cur is Border b && b.Tag is SocketViewModel s) return s;
            if (cur is NodeView) return null;
            cur = VisualTreeHelper.GetParent(cur);
        }
        return null;
    }

    /// <summary>
    /// Model-side socket resolution for the RIGHT-CLICK menu on the Win2D
    /// canvas. Two passes: the exact pin hit (same resolver the wire paths
    /// use, with the placeholder band narrowed to the drawn chrome so a body
    /// right-click at a slot row's height doesn't read as a placeholder), then
    /// the LABEL chrome of the row under the cursor on the topmost node — a
    /// bubble's natural right-click target is its label, which sits inboard of
    /// the 14px pin radius, so a pin-only pass left the socket menu reachable
    /// only by pixel-hunting the pin. Reach mirrors the renderer's own label
    /// math (<see cref="NodeGeometry.EstimateTextWidth"/> + the placeholder
    /// press slack) so the target tracks the visuals. Returns null when the
    /// press is on no row chrome — the caller falls back to
    /// <see cref="ResolveModelHit"/> (node / wire / frame / empty canvas).
    /// </summary>
    private SocketViewModel? ResolveSocketForMenuAtCanvasPoint(Point canvasPoint)
    {
        var sock = ResolveSocketAtCanvasPoint(canvasPoint, WireDropModelHitRadius,
            fullPlaceholderRowBand: false);
        if (sock is not null) return sock;
        if (_vm is null) return null;

        for (int i = _vm.Nodes.Count - 1; i >= 0; i--)
        {
            var n = _vm.Nodes[i];
            if (canvasPoint.X < n.X || canvasPoint.X > n.X + n.Width
                || canvasPoint.Y < n.Y || canvasPoint.Y > n.Y + n.Height)
                continue;

            double bandHalf = NodeGeometry.SocketRowHeight / 2.0;
            SocketViewModel? best = null;
            double bestDy = double.MaxValue;
            void Consider(System.Collections.Generic.IEnumerable<SocketViewModel> pool)
            {
                foreach (var s in pool)
                {
                    var (ax, ay) = s.Anchor();
                    double reach = NodeGeometry.EstimateTextWidth(s.Label, 12.0)
                                 + PlaceholderPressSlack;
                    if (Math.Abs(canvasPoint.X - (n.X + ax)) > reach) continue;
                    double dy = Math.Abs(canvasPoint.Y - (n.Y + ay));
                    if (dy <= bandHalf && dy < bestDy) { bestDy = dy; best = s; }
                }
            }
            Consider(n.Inputs);
            Consider(n.Outputs);
            // Topmost node containing the point decides — null falls back to
            // the node menu via ResolveModelHit, never to a node underneath.
            return best;
        }
        return null;
    }

    // Cache the grouped+ordered template list so the spawn
    // cascade isn't recomputed (GetAllTemplates → GroupBy → OrderBy over 100+
    // templates) on every empty-canvas right-click. NodeRegistry's template set
    // is process-static (built once at type init), so a one-time snapshot is
    // safe. The MenuFlyoutSubItem/MenuFlyoutItem objects themselves still get
    // rebuilt per-open — a MenuFlyoutItem can only live in one MenuFlyout at a
    // time, so they can't be cached and reattached across flyouts — but the
    // expensive LINQ shaping is now amortised to one pass.
    private System.Collections.Generic.List<System.Linq.IGrouping<string, NodeTemplate>>? _spawnCascadeGroupsCache;

    private System.Collections.Generic.List<System.Linq.IGrouping<string, NodeTemplate>> GetSpawnCascadeGroups()
    {
        if (_spawnCascadeGroupsCache is not null) return _spawnCascadeGroupsCache;
        _spawnCascadeGroupsCache = NodeRegistry.GetPaletteTemplates()
            // Majo — Databank no longer excluded: the intended
            // Databank-panel drag spawn isn't reachable, so DB nodes were
            // uncreatable. They now appear in the right-click "Spawn ►" cascade
            // like any category. Macros stays excluded (Macro.Call has the Macros
            // sidebar). Mirrors the search palette's filter (SpawnPaletteFlyout).
            .Where(t => !string.IsNullOrEmpty(t.Title)
                     && !string.Equals(t.Category, "Macros", StringComparison.OrdinalIgnoreCase))
            .GroupBy(t => string.IsNullOrEmpty(t.Category) ? "Other" : t.Category,
                     StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return _spawnCascadeGroupsCache;
    }

    /// <summary>
    /// 0.10.8 — emit the cascading "Spawn ►" submenu
    /// hierarchy into <paramref name="flyout"/>. Restores the pre-WinUI
    /// right-click idiom: one MenuFlyoutSubItem per NodeRegistry category,
    /// each containing one MenuFlyoutItem per template. Click on a leaf
    /// spawns the corresponding node at the captured pointer location.
    ///
    /// A crowded category (Platforms had 70 nodes in one flat wall) whose
    /// templates carry a <see cref="NodeTemplate.SubGroup"/> tag gains a
    /// SECOND level: one nested MenuFlyoutSubItem per sub-group (Platforms →
    /// Twitch / YouTube / Kick / …; Flow Control → Branch & Select / Loops &
    /// Sequence / Gates & Timing), ordered by the authored
    /// <see cref="NodeRegistry.SubGroupOrder"/>. Categories with no tagged
    /// nodes render exactly as before — a flat leaf list — so only the six
    /// opted-in categories pick up the nesting.
    ///
    /// The "All nodes…" type-to-filter palette stays available through the
    /// "Spawn Node… (search)" entry below the cascade for users who'd
    /// rather type than click through.
    /// </summary>
    private void BuildSpawnCategoryCascade(MenuFlyout flyout, Point hostPoint)
    {
        foreach (var group in GetSpawnCascadeGroups())
        {
            var sub = new MenuFlyoutSubItem
            {
                Text = group.Key,
                Icon = new FontIcon { Glyph = GlyphNew, FontFamily = new FontFamily(IconFontFamily) },
            };

            // Leaf factory shared by the flat and the sub-grouped paths so the
            // spawn wiring stays byte-identical either way. A MenuFlyoutItem can
            // only live in one parent, so each leaf is minted fresh.
            MenuFlyoutItem MakeLeaf(NodeTemplate template)
            {
                string title = template.Title;
                var item = new MenuFlyoutItem { Text = title };
                item.Click += (_, _) => SpawnNodeAtHostPoint(title, hostPoint);
                return item;
            }

            if (group.Any(t => !string.IsNullOrEmpty(t.SubGroup)))
            {
                BuildSubGroupedCategory(sub, group, MakeLeaf);
            }
            else
            {
                foreach (var template in group.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase))
                    sub.Items.Add(MakeLeaf(template));
            }

            if (sub.Items.Count > 0)
                flyout.Items.Add(sub);
        }
    }

    /// <summary>
    /// Emit the second-level sub-group cascade for a category whose templates
    /// carry <see cref="NodeTemplate.SubGroup"/> tags. Sub-groups render in the
    /// authored order captured at registration
    /// (<see cref="NodeRegistry.SubGroupOrder"/>); any bucket present in the
    /// data but absent from that order (defensive — most notably the "Other"
    /// catch-all that collects a newly-added node someone forgot to tag) is
    /// appended alphabetically, with "Other" pinned last so nothing is ever
    /// silently dropped. Leaves inside each sub-group stay Title-sorted, matching
    /// the flat path.
    /// </summary>
    private static void BuildSubGroupedCategory(MenuFlyoutSubItem parent,
        System.Linq.IGrouping<string, NodeTemplate> category,
        Func<NodeTemplate, MenuFlyoutItem> makeLeaf)
    {
        const string OtherBucket = "Other";

        var buckets = category
            .GroupBy(t => string.IsNullOrEmpty(t.SubGroup) ? OtherBucket : t.SubGroup,
                     StringComparer.OrdinalIgnoreCase)
            .ToList();

        var authored = NodeRegistry.SubGroupOrder
            .Where(p => string.Equals(p.Category, category.Key, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.SubGroup)
            .ToList();

        int Rank(string groupName)
        {
            if (string.Equals(groupName, OtherBucket, StringComparison.OrdinalIgnoreCase))
                return int.MaxValue;               // untagged catch-all always last
            int idx = authored.FindIndex(a => string.Equals(a, groupName, StringComparison.OrdinalIgnoreCase));
            return idx >= 0 ? idx : int.MaxValue - 1; // tagged-but-unordered: just before Other
        }

        foreach (var bucket in buckets
                     .OrderBy(b => Rank(b.Key))
                     .ThenBy(b => b.Key, StringComparer.OrdinalIgnoreCase))
        {
            var groupSub = new MenuFlyoutSubItem { Text = bucket.Key };
            foreach (var template in bucket.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase))
                groupSub.Items.Add(makeLeaf(template));
            if (groupSub.Items.Count > 0)
                parent.Items.Add(groupSub);
        }
    }

    /// <summary>
    /// Spawn helper for the cascading category menu — converts the host-
    /// space click position to canvas-space and routes through
    /// NodeRegistry.CreateNode + AddNode so the node lands at the original
    /// right-click location (not the menu's auto-shifted position).
    /// </summary>
    private void SpawnNodeAtHostPoint(string templateTitle, Point hostPoint)
    {
        var canvas = HostToCanvas(hostPoint);
        var node = NodeRegistry.CreateNode(
            templateTitle,
            new System.Drawing.Point((int)canvas.X, (int)canvas.Y));
        if (node is null) return;
        AddNode(node, canvas.X, canvas.Y);
        // Track this template as a recent spawn so the empty-canvas
        // menu's RECENT section surfaces it next time.
        TrackRecentNode(templateTitle);
    }

    // ─── Recent-nodes section (empty-canvas spawn menu) ─────────────────────
    // Restores the pre-T15 right-click "Recent" affordance the WinUI
    // port only kept in the Space-bar palette. Bounded MRU list of template
    // titles; ShowEmptyCanvasMenu renders up to RecentMax of them under a
    // RECENT header so frequently-spawned node types are one click away from
    // the primary right-click menu.
    private const int RecentMax = 5;
    private readonly System.Collections.Generic.List<string> _recentNodeTitles = new();

    private void TrackRecentNode(string templateTitle)
    {
        if (string.IsNullOrEmpty(templateTitle)) return;
        // Most-recent-first; de-dupe so a repeated spawn just bubbles to the top.
        _recentNodeTitles.RemoveAll(t => string.Equals(t, templateTitle, StringComparison.OrdinalIgnoreCase));
        _recentNodeTitles.Insert(0, templateTitle);
        if (_recentNodeTitles.Count > RecentMax)
            _recentNodeTitles.RemoveRange(RecentMax, _recentNodeTitles.Count - RecentMax);
    }

    /// <summary>
    /// Emit a RECENT header + up to <see cref="RecentMax"/> recent
    /// template entries at the top of the empty-canvas spawn menu. No-op (emits
    /// nothing) when no node has been spawned this session so the menu stays
    /// compact. Each leaf spawns at the captured right-click point, same as the
    /// cascade leaves.
    /// </summary>
    private void BuildRecentNodesSection(MenuFlyout flyout, Point hostPoint)
    {
        if (_recentNodeTitles.Count == 0) return;
        AddEyebrowHeader(flyout, "RECENT");
        foreach (var title in _recentNodeTitles)
        {
            string captured = title;
            flyout.Items.Add(NewMenuItem(captured, GlyphNew, () => SpawnNodeAtHostPoint(captured, hostPoint)));
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
    }

    /// <summary>
    /// Empty-canvas right-click menu. Pre-0.10.0 this jumped straight to the
    /// spawn palette which meant the user couldn't paste, undo, or add a
    /// frame without a keyboard shortcut. The menu surfaces those actions
    /// alongside a "Spawn Node…" entry that still launches the palette.
    /// Ctrl+RightClick remains a fast-path for "Add Comment Frame here".
    ///
    /// 0.11.x polish — Majo trimmed Undo / Redo and the Placeholder-Frame
    /// entry out of the no-selection branch (the keyboard chord set
    /// already covers undo/redo, and Placeholder Frame is niche enough that
    /// the Comment Frame entry is the only frame-affordance worth glance-
    /// discovering). The spawn category cascade stays — it's the primary
    /// "I know which kind of node I want, just let me click through" path.
    /// Right-clicking empty canvas ALWAYS shows this spawn menu — even with an
    /// active selection — and leaves the selection untouched (Majo: blank-space
    /// right-click should give the default add-node menu, not the selected
    /// node's actions). Selection operations (Delete / Duplicate / Group /
    /// Align / …) stay reachable by right-clicking ON a selected node
    /// (<see cref="ShowNodeMenu"/>), where the target is unambiguous. A prior
    /// 0.11.x branch surfaced the selection-operation set here on any active
    /// selection; that was removed per the feedback above.
    /// </summary>
    private void ShowEmptyCanvasMenu(Point hostPoint)
    {
        bool ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down)
            == Windows.UI.Core.CoreVirtualKeyStates.Down;
        if (ctrl)
        {
            var canvas = HostToCanvas(hostPoint);
            AddFrame(canvas.X, canvas.Y);
            return;
        }

        var capturedPoint = hostPoint;

        var flyout = NewStyledMenuFlyout("SPAWN NODE");

        // Recent-nodes section first (when non-empty) so frequently-
        // spawned templates are one click away ahead of the category cascade.
        BuildRecentNodesSection(flyout, capturedPoint);

        // No selection — spawn cascade + search + find + comment frame.
        // The cascade is the pre-WinUI "browse by category" affordance the
        // 0.10.8 release restored; it stays the primary spawn
        // entry-point for users who'd rather click through than type.
        BuildSpawnCategoryCascade(flyout, capturedPoint);
        flyout.Items.Add(new MenuFlyoutSeparator());

        flyout.Items.Add(NewMenuItem("Spawn Node… (search)", GlyphSearch, () => ShowSpawnPalette(capturedPoint), "Space"));
        flyout.Items.Add(NewMenuItem("Find Node…",  GlyphSearch, () => ShowNodeFinderFlyout(),         "Ctrl+F"));

        flyout.Items.Add(new MenuFlyoutSeparator());

        // Frames = gold (semantic glance color, per pre-T15 ForeColor).
        flyout.Items.Add(NewMenuItem("Add Comment Frame", GlyphNew, () =>
        {
            var canvas = HostToCanvas(capturedPoint);
            AddFrame(canvas.X, canvas.Y, 240, 160, "Comment", ArchitectCanvasPalette.CommentFrameDefault);
        }, "C", foregroundBrushKey: "SelectionBrush"));

        // Paste — only meaningful when the system clipboard carries a
        // PhoenixControls.SubGraph payload from a prior Cut/Copy. Paste()
        // already pastes at the live _lastHostPoint, which was updated on
        // the right-click pointer event a beat ago.
        if (HasSubGraphOnClipboard())
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(NewMenuItem("Paste here", GlyphPaste, () => Paste(), "Ctrl+V"));
        }

        flyout.ShowAt(HostRoot, hostPoint);
    }

    /// <summary>
    /// True when the system clipboard currently carries a sub-graph payload
    /// authored on a prior Ctrl+C / Ctrl+X. Falls back to the in-process
    /// snapshot when the OS clipboard probe throws (locked / virtualised).
    /// Was `static`; now instance because the in-process fallback
    /// is scoped per-Window (see <see cref="LogicCanvasView.GetFallbackSnapshot"/>)
    /// so the resolution needs <c>this.XamlRoot</c> to find the right slot.
    /// </summary>
    private bool HasSubGraphOnClipboard()
    {
        try
        {
            var view = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (view is not null && view.Contains("PhoenixControls.SubGraph")) return true;
        }
        catch { /* fall through to in-process fallback below */ }
        return GetFallbackSnapshot() is not null;
    }

    // ─── Frame menu ────────────────────────────────────────────────────

    private void ShowFrameMenu(FrameViewModel frame, Point hostPoint)
    {
        var flyout = NewStyledMenuFlyout(frame.IsPlaceholder ? "PLACEHOLDER FRAME" : "COMMENT FRAME");

        // Rename + Convert are per-frame
        // affordances; hide on multi. Delete options stay but the
        // labels / handlers reflect the cross-kind total selection count.
        int total = (_vm?.SelectedNodes.Count ?? 0)
                  + (_vm?.SelectedLinks.Count ?? 0)
                  + (_vm?.SelectedFrames.Count ?? 0);
        bool multi = total > 1;

        if (!multi)
        {
            flyout.Items.Add(NewMenuItem("Rename…", GlyphEdit, () => ShowFrameRenameFlyout(frame, hostPoint)));
            flyout.Items.Add(NewMenuItem(
                frame.IsPlaceholder ? "Convert to Comment Frame" : "Convert to Placeholder Frame",
                GlyphRefresh,
                () => ToggleFramePlaceholder(frame)));

            // Frame color picker. Pre-T15's WinForms canvas
            // exposed a "Color…" entry that opened a swatch dialog; the WinUI
            // rewrite shipped without it (frames could only inherit the
            // Comment / Placeholder defaults via the toggle above). This
            // entry opens a ColorPicker flyout anchored at the cursor and
            // commits the user's pick with one undo entry. Palette swatches
            // come from PhoenixDark.xaml; the picker UI
            // lives entirely inside Architect — no Shared/UI lift.
            flyout.Items.Add(NewMenuItem("Color…", GlyphEdit, () => ShowFrameColorPicker(frame, hostPoint)));

            // Bring to Front / Send to Back. Mutates
            // Frame.ZOrder; the FrameLayer applies Canvas.ZIndex on the
            // matching frame view so the stacking takes effect without a
            // rebuild. "Front" / "Back" reckon against the OTHER frames'
            // current ZOrder values rather than a fixed +/- delta so
            // repeated clicks keep promoting / demoting predictably.
            flyout.Items.Add(NewMenuItem("Bring to Front", GlyphNew,    () => BringFrameToFront(frame)));
            flyout.Items.Add(NewMenuItem("Send to Back",   GlyphRefresh, () => SendFrameToBack(frame)));

            flyout.Items.Add(new MenuFlyoutSeparator());
        }

        if (multi)
        {
            flyout.Items.Add(NewMenuItem($"Delete {total} item(s)", GlyphDelete,
                () => DeleteSelection(), "Del"));
        }
        else
        {
            flyout.Items.Add(NewMenuItem("Delete frame and contents", GlyphDelete, () => DeleteFrameWithContents(frame)));
            flyout.Items.Add(NewMenuItem("Delete frame only",         GlyphCross,  () => RemoveFrame(frame)));
        }

        flyout.ShowAt(HostRoot, hostPoint);
    }

    private void ShowFrameRenameFlyout(FrameViewModel frame, Point hostPoint)
    {
        var box = new TextBox { Text = frame.Label, MinWidth = 200 };
        var flyout = new Flyout { Content = box };
        // Mirror the `committed` sentinel pattern from
        // PromotePillToVariable so Esc rolls back the typed change instead of
        // committing it on Closed. Enter sets committed=false (i.e. we DO want
        // to commit, but mark "intent recorded" so the Closed event doesn't
        // double-commit); Esc sets committed=true to suppress the commit.
        // Using a single bool keeps the read site at the Closed handler
        // trivial — "committed=true means already-handled, do nothing".
        bool handled = false;
        bool wantsCommit = false;
        bool changedAtCommit = false;
        flyout.Closed += (_, _) =>
        {
            if (!handled)
            {
                // Dismissed by clicking outside the flyout — treat as commit
                // (matches the spawn-palette / pill-promote tradition that
                // an off-flyout click finalises the typed value).
                wantsCommit = true;
            }
            if (wantsCommit)
            {
                var typed = box.Text ?? string.Empty;
                if (!string.Equals(typed, frame.Label, StringComparison.Ordinal))
                {
                    // Single undo per real commit only — Esc + no-op commits
                    // skip the snapshot so the History stack stays focused
                    // on actual user changes.
                    PushUndo();
                    frame.Label = typed;
                    changedAtCommit = true;
                }
            }
            // 0.10.0 — return keyboard focus to the canvas so DEL / arrows /
            // Ctrl+S keep working without an extra click. Mirrors the
            // SpawnPaletteFlyout pattern in LogicCanvasView.Palette.cs.
            try { Focus(Microsoft.UI.Xaml.FocusState.Programmatic); }
            catch { /* never break dismissal on a focus failure */ }
            // Suppress compiler warning if changedAtCommit isn't read later.
            _ = changedAtCommit;
        };
        box.KeyDown += (_, ke) =>
        {
            if (ke.Key == Windows.System.VirtualKey.Enter)
            {
                ke.Handled = true;
                handled = true;
                wantsCommit = true;
                flyout.Hide();
            }
            else if (ke.Key == Windows.System.VirtualKey.Escape)
            {
                ke.Handled = true;
                handled = true;
                wantsCommit = false;
                flyout.Hide();
            }
        };
        flyout.ShowAt(HostRoot, new FlyoutShowOptions { Position = hostPoint });
        box.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        box.SelectAll();
    }

    /// <summary>
    /// Flip a comment frame to a placeholder (or vice versa). Pushes a
    /// single undo snapshot.
    /// </summary>
    private void ToggleFramePlaceholder(FrameViewModel frame)
    {
        if (_vm is null) return;
        PushUndo();
        bool nextPlaceholder = !frame.IsPlaceholder;
        frame.Model.IsPlaceholder = nextPlaceholder;
        frame.Model.FrameColor = nextPlaceholder
            ? ArchitectCanvasPalette.PlaceholderFrameDefault
            : ArchitectCanvasPalette.CommentFrameDefault;
        frame.RaiseAllChanged();
        _vm.OnGraphMutated();
    }

    /// <summary>
    /// Frame Color picker. Opens a ColorPicker hosted in a
    /// Flyout anchored at <paramref name="hostPoint"/>. Commits the picked
    /// colour into <see cref="FrameViewModel.Model"/>.FrameColor with a
    /// single undo entry on the flyout's Closed event. The picker shows
    /// alpha-disabled + saturated-color sliders by default; palette swatches
    /// are populated from PhoenixDark.xaml-derived constants so the seed
    /// colours read on-theme. The picker UI stays
    /// inside Architect.WinUI — Visualist owns its own picker if/when it
    /// needs one.
    /// </summary>
    private void ShowFrameColorPicker(FrameViewModel frame, Point hostPoint)
    {
        // Seed the picker at the frame's current FrameColor so the chosen
        // colour reads as a *relative* tweak rather than a reset.
        var seed = frame.Model.FrameColor;
        var picker = new ColorPicker
        {
            Color                  = Color.FromArgb(seed.A, seed.R, seed.G, seed.B),
            IsAlphaEnabled         = false,
            IsMoreButtonVisible    = true,
            IsColorChannelTextInputVisible = true,
            IsHexInputVisible      = true,
            ColorSpectrumShape     = ColorSpectrumShape.Box,
        };

        var flyout = new Flyout { Content = picker };

        // Single-commit pattern (mirrors PromotePillToVariable): the Closed
        // handler fires exactly one PushUndo + model write so multiple
        // intermediate slider drags don't pollute the history stack. The
        // committed sentinel guards a re-entry (some Closed fires we've
        // seen on dismiss-after-confirm).
        bool committed = false;
        flyout.Closed += (_, _) =>
        {
            if (committed) return;
            committed = true;
            var picked = picker.Color;
            // Skip the undo / model write when the user dismissed without
            // changing the colour (off-flyout click on the original swatch).
            if (picked.A == seed.A && picked.R == seed.R
                && picked.G == seed.G && picked.B == seed.B)
            {
                try { Focus(Microsoft.UI.Xaml.FocusState.Programmatic); } catch { }
                return;
            }
            if (_vm is null) { return; }
            PushUndo();
            frame.Model.FrameColor = System.Drawing.Color.FromArgb(
                picked.A, picked.R, picked.G, picked.B);
            // RaiseAllChanged bumps FrameColorHex + FrameFillHex; the canvas
            // listens for those and repaints the border + fill in place via
            // ApplyFrameTint (no rebuild).
            frame.RaiseAllChanged();
            _vm.OnGraphMutated();
            try { Focus(Microsoft.UI.Xaml.FocusState.Programmatic); } catch { }
        };

        flyout.ShowAt(HostRoot, new FlyoutShowOptions
        {
            Position  = hostPoint,
            Placement = FlyoutPlacementMode.Bottom,
        });
    }

    /// <summary>
    /// Promote <paramref name="frame"/> above every other
    /// frame on the canvas by setting its ZOrder to max(otherZ) + 1. Single
    /// undo entry. No-op when there are no other frames or the frame is
    /// already on top.
    /// </summary>
    private void BringFrameToFront(FrameViewModel frame)
    {
        if (_vm is null) return;
        int maxOther = int.MinValue;
        foreach (var f in _vm.Frames)
        {
            if (ReferenceEquals(f, frame)) continue;
            if (f.ZOrder > maxOther) maxOther = f.ZOrder;
        }
        // Nothing to demote against, OR already strictly above every peer.
        if (maxOther == int.MinValue || frame.ZOrder > maxOther) return;
        PushUndo();
        frame.ZOrder = maxOther + 1;
        _vm.OnGraphMutated();
    }

    /// <summary>
    /// Demote <paramref name="frame"/> below every other
    /// frame by setting its ZOrder to min(otherZ) - 1. Single undo entry.
    /// </summary>
    private void SendFrameToBack(FrameViewModel frame)
    {
        if (_vm is null) return;
        int minOther = int.MaxValue;
        foreach (var f in _vm.Frames)
        {
            if (ReferenceEquals(f, frame)) continue;
            if (f.ZOrder < minOther) minOther = f.ZOrder;
        }
        if (minOther == int.MaxValue || frame.ZOrder < minOther) return;
        PushUndo();
        frame.ZOrder = minOther - 1;
        _vm.OnGraphMutated();
    }

    /// <summary>
    /// "Delete frame and contents" — drops every node whose origin is
    /// inside the frame's bounds plus every link touching those nodes,
    /// nukes nested sub-frames, then removes the frame itself.
    /// </summary>
    private void DeleteFrameWithContents(FrameViewModel frame)
    {
        if (_vm is null) return;
        var bounds = frame.Model.Bounds;
        var contained = _vm.Nodes
            .Where(n => bounds.Contains(new System.Drawing.Point((int)n.X, (int)n.Y)))
            .ToArray();
        var subFrames = _vm.Frames
            .Where(f => f != frame && bounds.Contains(f.Model.Bounds))
            .ToArray();

        PushUndo();
        foreach (var n in contained) RemoveNode(n, pushUndo: false);
        foreach (var f in subFrames) RemoveFrame(f);
        RemoveFrame(frame);
    }

    // ─── Node menu ─────────────────────────────────────────────────────

    private void ShowNodeMenu(NodeViewModel node, Point hostPoint)
    {
        // Multi-selection labels itself "SELECTION · N"; single uses the
        // node title (truncated) so the user knows which node the menu
        // was opened on without scanning the items.
        bool multiNodeEyebrow = _vm is not null && _vm.SelectedNodes.Count >= 2 && _vm.SelectedNodes.Contains(node);
        string eyebrow = multiNodeEyebrow
            ? $"SELECTION · {(_vm?.SelectedNodes.Count ?? 0)}"
            : (string.IsNullOrEmpty(node.Title) ? "NODE" : node.Title.ToUpperInvariant());
        if (eyebrow.Length > 24) eyebrow = eyebrow[..23] + "…";
        var flyout = NewStyledMenuFlyout(eyebrow);

        // Multi-selection awareness. Every
        // action's label / behaviour branches on the selection count:
        //   * Single-only actions (Edit subgraph, Documentation, Convert
        //     to Compact, Disable Connection Warnings) hide when count ≥ 2.
        //   * Bulk actions (Delete, Duplicate, Align, Group, Wrap) flip
        //     their labels to reflect the count.
        // TotalSelectedCount is the cross-kind count (nodes+links+frames);
        // multiNode is the node-only branch the existing align/group code
        // already keyed off.
        bool multiNode = _vm is not null && _vm.SelectedNodes.Count >= 2 && _vm.SelectedNodes.Contains(node);
        int  selCount  = _vm?.SelectedNodes.Count ?? 1;

        // Edit Macro / Edit Process — single-only.
        if (!multiNode && TryAddSubGraphEdit(flyout, node))
            flyout.Items.Add(new MenuFlyoutSeparator());

        // Documentation — single-only (a multi-set spans templates so
        // "open docs for which?" is ambiguous).
        if (!multiNode)
        {
            var docTitle = string.IsNullOrEmpty(node.Title) ? "Documentation" : $"{node.Title} documentation";
            // Docs = ember (semantic glance color, per pre-T15 ForeColor).
            flyout.Items.Add(NewMenuItem(docTitle, GlyphDocs, () => OpenNodeDocumentationFor(node.Title),
                foregroundBrushKey: "Ember200Brush"));
        }

        // Rename. Single-only;
        // a multi-set rename has no clear target. Reuses NodeViewModel's
        // BeginTitleEdit so commit / rollback semantics line up with the
        // double-tap and F2 rename entries (the EditableTitle TwoWay
        // binding + IsTitleRenaming visibility flag + Esc/Enter handlers
        // in NodeView.xaml.cs are the same machinery either way).
        if (!multiNode)
        {
            flyout.Items.Add(NewMenuItem("Rename", GlyphEdit, () => node.BeginTitleEdit(), "F2"));
        }

        // Duplicate — label scales with the selection size; the handler
        // iterates every selected node when multi.
        var dupText = multiNode ? $"Duplicate {selCount} nodes" : "Duplicate";
        flyout.Items.Add(NewMenuItem(dupText, GlyphNew, () =>
        {
            if (multiNode && _vm is not null)
            {
                // DuplicateSelection clones the entire multi-set as a single
                // undo entry. Pre-0.10.0 the right-click "Duplicate" fired
                // a single-node DuplicateNode on the right-clicked node and
                // dropped the rest of the selection on the floor.
                DuplicateSelection();
            }
            else
            {
                DuplicateNode(node);
            }
        }, "Ctrl+D"));

        // Align actions appear when there's a multi-selection.
        // Center (horizontal midpoint) + Middle (vertical
        // midpoint) added to the pre-existing Left / Right / Top / Bottom
        // set so the WinUI canvas matches the pre-T15 six-option submenu.
        if (multiNode)
        {
            var alignSub = new MenuFlyoutSubItem { Text = $"Align {selCount} nodes" };
            void AddAlign(string label, Action action) => alignSub.Items.Add(NewMenuItem(label, string.Empty, action));
            AddAlign("Left",   () => AlignSelected(AlignAxis.Left));
            AddAlign("Center", () => AlignSelected(AlignAxis.Center));
            AddAlign("Right",  () => AlignSelected(AlignAxis.Right));
            AddAlign("Top",    () => AlignSelected(AlignAxis.Top));
            AddAlign("Middle", () => AlignSelected(AlignAxis.Middle));
            AddAlign("Bottom", () => AlignSelected(AlignAxis.Bottom));
            flyout.Items.Add(alignSub);

            // Distribute Horizontally / Vertically. Only
            // meaningful with 3+ nodes (with 2 there's nothing to distribute
            // between the endpoints); hide the submenu when selCount < 3.
            if (selCount >= 3)
            {
                var distSub = new MenuFlyoutSubItem { Text = $"Distribute {selCount} nodes" };
                void AddDist(string label, Action action) => distSub.Items.Add(NewMenuItem(label, string.Empty, action));
                AddDist("Horizontally", () => DistributeSelected(DistributeAxis.Horizontal));
                AddDist("Vertically",   () => DistributeSelected(DistributeAxis.Vertical));
                flyout.Items.Add(distSub);
            }
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        if (multiNode)
        {
            flyout.Items.Add(NewMenuItem($"Group {selCount} nodes (Collapse to Macro)",
                GlyphNew, () => CollapseSelectionToMacro(), "Ctrl+G"));
            flyout.Items.Add(NewMenuItem($"Wrap {selCount} nodes in Frame",
                GlyphNew, () => WrapSelectionInFrame()));
            flyout.Items.Add(new MenuFlyoutSeparator());
        }

        // Convert to Compact / Full — single-only (the toggle is per-node).
        if (!multiNode)
        {
            var template = NodeRegistry.GetTemplate(node.Title);
            if (template is not null && !string.IsNullOrEmpty(template.CompactSymbol))
            {
                bool isCompact = node.Model.Attributes.TryGetValue("Compact", out var cv)
                              && string.Equals(cv, "true", StringComparison.OrdinalIgnoreCase);
                flyout.Items.Add(NewMenuItem(
                    isCompact ? "Convert to Full" : "Convert to Compact",
                    GlyphRefresh,
                    () => ToggleCompactMode(node)));
            }

            // Disable Connection Warnings — Event.Trigger / Executor / Return only,
            // single-only (the toggle is a per-node attribute).
            if (node.Title is "Event.Trigger" or "Event.Executor" or "Event.Return")
            {
                bool disabled = node.Model.Attributes.TryGetValue("DisableConnectionWarnings", out var dv)
                             && string.Equals(dv, "true", StringComparison.OrdinalIgnoreCase);
                flyout.Items.Add(NewToggleItem("Disable Connection Warnings", GlyphWarning, disabled,
                    () => ToggleDisableConnectionWarnings(node)));
            }
        }

        // Disable / Enable Node toggle. Sits above the
        // Delete separator on both single + multi paths. Label flips to
        // "Enable Node" when already disabled. Multi-selection branch: the
        // ToggleNodeDisabled helper applies "make these match" semantics
        // against the right-clicked node's state.
        //
        // The flag is consumed at EXPORT time (ScriptExporter), not by the
        // runtime: a disabled flow node emits only a trace comment and the
        // walk splices through its linear flow output; a disabled event root
        // suppresses its whole handler; a disabled data provider reads
        // as-if-unwired so consumers fall back to their socket defaults.
        {
            bool currentlyDisabled = node.IsDisabled;
            string disableText;
            if (multiNode)
            {
                disableText = currentlyDisabled
                    ? $"Enable {selCount} nodes"
                    : $"Disable {selCount} nodes";
            }
            else
            {
                disableText = currentlyDisabled ? "Enable Node" : "Disable Node";
            }
            flyout.Items.Add(NewMenuItem(disableText, GlyphWarning,
                () => ToggleNodeDisabled(node, multiNode)));
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        // 0.10.0 — DEL routes through the canvas's DeleteSelection so any
        // SelectedLinks / SelectedFrames in the multi-set come with the nodes.
        // The label reflects the total cross-kind count.
        int total = (_vm?.SelectedNodes.Count ?? 0)
                  + (_vm?.SelectedLinks.Count ?? 0)
                  + (_vm?.SelectedFrames.Count ?? 0);
        string delText = total > 1
            ? $"Delete {total} item(s)"
            : "Delete";
        // Delete = red (semantic glance color, per pre-T15 ForeColor).
        flyout.Items.Add(NewMenuItem(delText, GlyphDelete, () =>
        {
            if (total > 1) DeleteSelection();
            else            RemoveNode(node);
        }, "Del", foregroundBrushKey: "StatusRedBrush"));

        flyout.ShowAt(HostRoot, hostPoint);
    }

    /// <summary>
    /// If <paramref name="node"/> is a Macro.Call / Process.Spawn, add an
    /// "Edit Macro" / "Edit Process" item that opens a SubGraphWindow.
    /// </summary>
    private bool TryAddSubGraphEdit(MenuFlyout flyout, NodeViewModel node)
    {
        if (_vm is null) return false;
        if (node.Title == "Macro.Call")
        {
            flyout.Items.Add(NewMenuItem("Edit macro graph", GlyphEdit, () =>
            {
                if (!node.Model.Attributes.TryGetValue("MacroId", out var mid) || string.IsNullOrEmpty(mid)) return;
                var macro = _vm.Graph.Macros.FirstOrDefault(m => m.MacroId == mid);
                // AVM required for shared undo + rename sync.
                if (macro is not null && ArchitectVm is not null)
                    SubGraphWindow.OpenMacroEditor(macro, ArchitectVm, this);
            }));
            // Find References — discovers every Macro.Call carrying the
            // same MacroId, selects + flashes them, and frames them into view.
            // Restores the pre-T15 HighlightMacroCallSites right-click action.
            flyout.Items.Add(NewMenuItem("Find references", GlyphSearch, () =>
            {
                if (node.Model.Attributes.TryGetValue("MacroId", out var mid) && !string.IsNullOrEmpty(mid))
                    HighlightMacroCallSites(mid);
            }));
            return true;
        }
        if (node.Title == "Process.Start")
        {
            flyout.Items.Add(NewMenuItem("Edit process graph", GlyphEdit, () =>
            {
                if (!node.Model.Attributes.TryGetValue("ProcessId", out var pid) || string.IsNullOrEmpty(pid)) return;
                var proc = _vm.Graph.Processes.FirstOrDefault(p => p.ProcessId == pid);
                // AVM required for shared undo + rename sync.
                if (proc is not null && ArchitectVm is not null)
                    SubGraphWindow.OpenProcessEditor(proc, ArchitectVm, this);
            }));
            return true;
        }
        return false;
    }

    // Center / Middle added to the pre-existing
    // Left / Right / Top / Bottom set. Center = horizontal midpoint of the
    // selection's bounding box; Middle = vertical midpoint. Both anchor at
    // the midpoint between the min-edge and the max-edge of the selection
    // and translate each node so its own midpoint lines up.
    private enum AlignAxis { Left, Right, Top, Bottom, Center, Middle }

    private void AlignSelected(AlignAxis axis)
    {
        if (_vm is null || _vm.SelectedNodes.Count < 2) return;
        PushUndo();
        double anchor = axis switch
        {
            AlignAxis.Left   => _vm.SelectedNodes.Min(n => n.X),
            AlignAxis.Right  => _vm.SelectedNodes.Max(n => n.X + n.Width),
            AlignAxis.Top    => _vm.SelectedNodes.Min(n => n.Y),
            AlignAxis.Bottom => _vm.SelectedNodes.Max(n => n.Y + n.Height),
            // Center / Middle: midpoint of the selection's bounding box on
            // the relevant axis. Each node then translates so its OWN
            // midpoint sits on that anchor (handled in the per-node switch
            // below).
            AlignAxis.Center => (_vm.SelectedNodes.Min(n => n.X)
                              +  _vm.SelectedNodes.Max(n => n.X + n.Width)) / 2.0,
            AlignAxis.Middle => (_vm.SelectedNodes.Min(n => n.Y)
                              +  _vm.SelectedNodes.Max(n => n.Y + n.Height)) / 2.0,
            _ => 0,
        };
        foreach (var n in _vm.SelectedNodes.ToArray())
        {
            double targetX = n.X, targetY = n.Y;
            switch (axis)
            {
                case AlignAxis.Left:   targetX = anchor; break;
                case AlignAxis.Right:  targetX = anchor - n.Width; break;
                case AlignAxis.Top:    targetY = anchor; break;
                case AlignAxis.Bottom: targetY = anchor - n.Height; break;
                case AlignAxis.Center: targetX = anchor - n.Width  / 2.0; break;
                case AlignAxis.Middle: targetY = anchor - n.Height / 2.0; break;
            }
            _vm.TranslateNode(n, targetX - n.X, targetY - n.Y);
        }
    }

    // Distribute Horizontally / Vertically. Sort the
    // selection by the axis, then space the inner nodes at equal gaps
    // between the min-edge and max-edge anchors. Single undo entry per call.
    private enum DistributeAxis { Horizontal, Vertical }

    private void DistributeSelected(DistributeAxis axis)
    {
        if (_vm is null || _vm.SelectedNodes.Count < 3) return;
        PushUndo();
        var ordered = axis == DistributeAxis.Horizontal
            ? _vm.SelectedNodes.OrderBy(n => n.X).ToArray()
            : _vm.SelectedNodes.OrderBy(n => n.Y).ToArray();

        // Total available span = max-anchor - min-anchor; total node extent
        // = sum of widths/heights; the remaining space splits into (count-1)
        // equal gaps. Endpoints stay put so the bounding box is preserved.
        double minAnchor, maxAnchor, totalExtent;
        if (axis == DistributeAxis.Horizontal)
        {
            minAnchor = ordered.First().X;
            maxAnchor = ordered.Last().X + ordered.Last().Width;
            totalExtent = ordered.Sum(n => n.Width);
        }
        else
        {
            minAnchor = ordered.First().Y;
            maxAnchor = ordered.Last().Y + ordered.Last().Height;
            totalExtent = ordered.Sum(n => n.Height);
        }
        double freeSpan = (maxAnchor - minAnchor) - totalExtent;
        double gap = freeSpan / (ordered.Length - 1);
        // Negative gaps mean the selection overlaps; we still distribute
        // (lays nodes flush at the negative gap) rather than refusing —
        // matches the pre-T15 distribute idiom.
        double cursor = minAnchor;
        foreach (var n in ordered)
        {
            if (axis == DistributeAxis.Horizontal)
            {
                double dx = cursor - n.X;
                if (System.Math.Abs(dx) > 0.001) _vm.TranslateNode(n, dx, 0);
                cursor += n.Width + gap;
            }
            else
            {
                double dy = cursor - n.Y;
                if (System.Math.Abs(dy) > 0.001) _vm.TranslateNode(n, 0, dy);
                cursor += n.Height + gap;
            }
        }
    }

    // Disable / Enable Node toggle. Flips the
    // <c>__disabled</c> attribute on the node model; NodeView paints
    // disabled nodes at reduced opacity. Multi-selection: toggle every node
    // in the selection in a single undo entry. ScriptExporter consumes the
    // flag on export: disabled flow nodes are bypassed/spliced out of the
    // emitted .phx, disabled event roots emit no handler, and disabled data
    // providers resolve as-if-unwired (socket defaults).
    private void ToggleNodeDisabled(NodeViewModel node, bool multiNode)
    {
        if (_vm is null) return;
        PushUndo();
        if (multiNode)
        {
            // Apply the OPPOSITE of the right-clicked node's current state to
            // every selected node so multi-selection toggle reads as "make
            // these match" rather than per-node flip (the latter would split
            // a mixed selection into halves on every click).
            bool target = !node.IsDisabled;
            foreach (var n in _vm.SelectedNodes.ToArray())
                n.IsDisabled = target;
        }
        else
        {
            node.IsDisabled = !node.IsDisabled;
        }
        _vm.OnGraphMutated();
    }

    private void WrapSelectionInFrame()
    {
        // Right-click "Wrap N nodes in Frame" — only offered at >= 2 selected
        // nodes; keep that floor here. The bare-C / "Add comment frame" smart
        // path (AddCommentFrameSmart) shares WrapNodesInFrame at a >= 1 floor.
        if (_vm is null || _vm.SelectedNodes.Count < 2) return;
        WrapNodesInFrame(_vm.SelectedNodes);
    }

    /// <summary>
    /// Draw a comment frame that encloses <paramref name="nodes"/> (padding +
    /// header room) and push one undo entry via <see cref="AddFrame"/>. Shared
    /// by the right-click "Wrap N nodes in Frame" menu item (>= 2 nodes) and
    /// the bare-C / "Add comment frame" smart path (>= 1 node).
    /// </summary>
    private void WrapNodesInFrame(System.Collections.Generic.IEnumerable<NodeViewModel> nodes)
    {
        if (_vm is null) return;
        const int pad      = 20;
        const int headerPad = 28;
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        int count = 0;
        foreach (var n in nodes)
        {
            count++;
            if (n.X < minX) minX = n.X;
            if (n.Y < minY) minY = n.Y;
            if (n.X + n.Width  > maxX) maxX = n.X + n.Width;
            if (n.Y + n.Height > maxY) maxY = n.Y + n.Height;
        }
        if (count == 0) return;
        var x = (int)System.Math.Round(minX - pad);
        var y = (int)System.Math.Round(minY - pad - headerPad);
        var w = (int)System.Math.Round((maxX - minX) + pad * 2);
        var h = (int)System.Math.Round((maxY - minY) + pad * 2 + headerPad);
        AddFrame(x, y, w, h, "Comment", ArchitectCanvasPalette.CommentFrameDefault);
    }

    private void CollapseSelectionToMacro()
    {
        if (_vm is null || _vm.SelectedNodes.Count < 2) return;
        var graph = _vm.Graph;
        var selection = _vm.SelectedNodes.ToArray();
        var selIds = new System.Collections.Generic.HashSet<string>(selection.Select(n => n.Model.Id));

        PushUndo();

        var internalLinks = graph.Links.Where(l => selIds.Contains(l.FromNodeId) && selIds.Contains(l.ToNodeId)).ToList();
        var inboundLinks  = graph.Links.Where(l => !selIds.Contains(l.FromNodeId) && selIds.Contains(l.ToNodeId)).ToList();
        var outboundLinks = graph.Links.Where(l =>  selIds.Contains(l.FromNodeId) && !selIds.Contains(l.ToNodeId)).ToList();

        var macro = new Macro { Name = "NewMacro" };
        var entry = NodeRegistry.CreateNode("Macro.Entry", new System.Drawing.Point(80, 160));
        var exit  = NodeRegistry.CreateNode("Macro.Exit",  new System.Drawing.Point(520, 160));
        // Singleton guard — CollapseSelectionToMacro builds a FRESH
        // macro.Graph, so a duplicate Entry/Exit can only arise if the selected
        // set itself already contains one (collapsing a selection that spans a
        // Macro.Entry). Guard each add against the macro's own graph; reject the
        // duplicate via GlobalLogger (never a modal) and skip the add so the
        // exporter's FirstOrDefault binding stays unambiguous.
        if (entry is not null && !WouldDuplicateSubGraphSingleton(macro.Graph, "Macro.Entry"))
            macro.Graph.Nodes.Add(entry);
        if (exit  is not null && !WouldDuplicateSubGraphSingleton(macro.Graph, "Macro.Exit"))
            macro.Graph.Nodes.Add(exit);

        foreach (var n in selection)
        {
            // A selected node that is itself a Macro.Entry/Exit would
            // collide with the fresh pair just added; reject moving a duplicate
            // singleton into the new macro graph.
            if (WouldDuplicateSubGraphSingleton(macro.Graph, n.Model.Title))
                continue;
            macro.Graph.Nodes.Add(n.Model);
        }
        foreach (var lk in internalLinks)
        {
            macro.Graph.Links.Add(lk);
            graph.Links.Remove(lk);
        }
        foreach (var n in selection) graph.Nodes.Remove(n.Model);

        // Place the Macro.Call at the geometric CENTRE of what it
        // replaced (centroid of the selection's per-node midpoints), not the
        // top-left Min corner — the pre-T15 Canvas.Macros used centre-of-mass so
        // the collapsed node sits visually where the group was.
        double cx = selection.Average(n => n.X + n.Width  / 2.0);
        double cy = selection.Average(n => n.Y + n.Height / 2.0);
        var call = NodeRegistry.CreateNode("Macro.Call",
            new System.Drawing.Point((int)Math.Round(cx), (int)Math.Round(cy)));
        if (call is null) return;
        call.Attributes["MacroId"]   = macro.MacroId;
        call.Attributes["MacroName"] = macro.Name;

        // Promote external connections to named macro slots
        // AND re-wire the actual Link objects onto the Macro.Call's sockets.
        // Pre-fix the InputNames/OutputNames were populated but the inbound /
        // outbound Link objects still pointed at the now-deleted internal node
        // ids, orphaning every external connection the moment the selection was
        // removed from graph.Nodes. We track the promoted slot name PER LINK
        // (not by socket — several links may promote to the same slot when they
        // hit the same internal socket) so the re-wire below can map each link
        // to the correct Macro.Call socket.
        var inboundSlotForLink  = new System.Collections.Generic.List<(Link Link, string Slot)>();
        var outboundSlotForLink = new System.Collections.Generic.List<(Link Link, string Slot)>();

        // The inbound link's downstream end (ToNodeId) and the outbound link's
        // upstream end (FromNodeId) are SELECTED nodes — by this point they have
        // already been moved into macro.Graph (and removed from `graph`), so the
        // socket-name lookup must resolve against macro.Graph, not the parent.
        // Resolving against `graph` here (as the pre-fix code did) always missed
        // and fell back to the generic "input"/"output" names.
        var usedIn = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lk in inboundLinks)
        {
            string baseName = ResolveSocketName(macro.Graph, lk.ToNodeId, lk.ToSocketId) ?? "input";
            string unique = UniquifyName(baseName, usedIn);
            macro.InputNames.Add(unique);
            usedIn.Add(unique);
            inboundSlotForLink.Add((lk, unique));
        }
        var usedOut = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lk in outboundLinks)
        {
            string baseName = ResolveSocketName(macro.Graph, lk.FromNodeId, lk.FromSocketId) ?? "output";
            string unique = UniquifyName(baseName, usedOut);
            macro.OutputNames.Add(unique);
            usedOut.Add(unique);
            outboundSlotForLink.Add((lk, unique));
        }

        // Build the Macro.Call's data sockets from the
        // promoted slot names so the re-wire below has real socket ids to point
        // at. Inputs (left edge) = InputNames, outputs (right edge) = OutputNames.
        // The names are unique per direction (UniquifyName above), so a single
        // pass yields a name→socketId map per side.
        var callInputSocketIdByName  = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var callOutputSocketIdByName = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        BuildCollapsedMacroCallSockets(call, macro.InputNames, macro.OutputNames,
            callInputSocketIdByName, callOutputSocketIdByName);

        // Re-wire inbound links: their downstream end (To*) moves from the
        // now-internal node onto the Macro.Call's matching input socket.
        foreach (var (lk, slot) in inboundSlotForLink)
        {
            if (callInputSocketIdByName.TryGetValue(slot, out var sid))
            {
                lk.ToNodeId   = call.Id;
                lk.ToSocketId = sid;
            }
        }
        // Re-wire outbound links: their upstream end (From*) moves from the
        // now-internal node onto the Macro.Call's matching output socket.
        foreach (var (lk, slot) in outboundSlotForLink)
        {
            if (callOutputSocketIdByName.TryGetValue(slot, out var sid))
            {
                lk.FromNodeId   = call.Id;
                lk.FromSocketId = sid;
            }
        }

        graph.Macros.Add(macro);
        graph.Nodes.Add(call);
        _vm.LoadGraph(graph);
        _vm.Graph.MarkStructuralChange();
        _vm.OnGraphMutated();

        // Re-select the newly-created Macro.Call so a follow-up
        // Ctrl+D / Del / drag operates on the just-collapsed group. LoadGraph
        // rebuilds every VM, so we have to look it up by id rather than
        // re-using a pre-LoadGraph reference.
        NodeViewModel? collapsedCallVm = null;
        foreach (var vm in _vm.Nodes)
        {
            if (vm.Model.Id == call.Id) { collapsedCallVm = vm; break; }
        }
        if (collapsedCallVm is not null)
            _vm.SetMultiSelection(new[] { collapsedCallVm });
    }

    /// <summary>
    /// Build the data sockets on a freshly-collapsed Macro.Call from
    /// the promoted slot names and fill the name→socketId maps the link re-wire
    /// uses. Preserves any template-default Flow sockets (the Macro.Call
    /// template ships a Flow in + Flow out) and appends one Input socket per
    /// <paramref name="inputNames"/> and one Output socket per
    /// <paramref name="outputNames"/>. Offsets/size mirror the spacing
    /// NodeGeometry paints with so the node lays out sensibly before the next
    /// intrinsic-size pass.
    /// </summary>
    private static void BuildCollapsedMacroCallSockets(
        Node call,
        System.Collections.Generic.List<string> inputNames,
        System.Collections.Generic.List<string> outputNames,
        System.Collections.Generic.Dictionary<string, string> inputIdByName,
        System.Collections.Generic.Dictionary<string, string> outputIdByName)
    {
        const int headerH = 26;
        const int spacing = 22;
        int width = call.Size.Width > 0 ? call.Size.Width : 200;
        var stringColor = NodeRegistry.ColString;

        for (int i = 0; i < inputNames.Count; i++)
        {
            string name = inputNames[i];
            var sock = new Socket
            {
                Id       = Guid.NewGuid().ToString(),
                Name     = name,
                Type     = SocketType.Input,
                Color    = stringColor,
                DataType = NodeRegistry.DataTypeFromColorPublic(stringColor),
                Offset   = new System.Drawing.Point(-6, headerH + 6 + (i + 1) * spacing),
            };
            call.Sockets.Add(sock);
            inputIdByName[name] = sock.Id;
        }
        for (int i = 0; i < outputNames.Count; i++)
        {
            string name = outputNames[i];
            var sock = new Socket
            {
                Id       = Guid.NewGuid().ToString(),
                Name     = name,
                Type     = SocketType.Output,
                Color    = stringColor,
                DataType = NodeRegistry.DataTypeFromColorPublic(stringColor),
                Offset   = new System.Drawing.Point(width - 14, headerH + 6 + (i + 1) * spacing),
            };
            call.Sockets.Add(sock);
            outputIdByName[name] = sock.Id;
        }

        int totalRows = Math.Max(1 + inputNames.Count, 1 + outputNames.Count);
        call.Size = new System.Drawing.Size(width, headerH + 14 + totalRows * spacing);
    }

    private static string? ResolveSocketName(Graph graph, string nodeId, string socketId)
    {
        var n = graph.Nodes.Find(x => x.Id == nodeId);
        return n?.Sockets.Find(s => s.Id == socketId)?.Name;
    }

    private static string UniquifyName(string baseName, System.Collections.Generic.HashSet<string> taken)
    {
        if (!taken.Contains(baseName)) return baseName;
        for (int i = 2; i < 1000; i++)
        {
            var candidate = baseName + "_" + i;
            if (!taken.Contains(candidate)) return candidate;
        }
        return baseName + "_" + System.Guid.NewGuid().ToString("N")[..6];
    }

    private void ToggleCompactMode(NodeViewModel node)
    {
        if (_vm is null) return;
        PushUndo();
        bool isCompact = node.Model.Attributes.TryGetValue("Compact", out var cv)
                      && string.Equals(cv, "true", StringComparison.OrdinalIgnoreCase);
        if (!isCompact)
        {
            node.Model.Attributes["Compact"]       = "true";
            node.Model.Attributes["Compact.PrevW"] = node.Model.Size.Width.ToString();
            node.Model.Attributes["Compact.PrevH"] = node.Model.Size.Height.ToString();
            node.Model.Size = new System.Drawing.Size(56, 36);
        }
        else
        {
            node.Model.Attributes.Remove("Compact");
            int w = 220, h = 60;
            if (node.Model.Attributes.TryGetValue("Compact.PrevW", out var pw)) int.TryParse(pw, out w);
            if (node.Model.Attributes.TryGetValue("Compact.PrevH", out var ph)) int.TryParse(ph, out h);
            node.Model.Attributes.Remove("Compact.PrevW");
            node.Model.Attributes.Remove("Compact.PrevH");
            node.Model.Size = new System.Drawing.Size(w, h);
        }
        node.RaiseHeaderChanged();
        node.RebuildSockets();
        _vm.OnGraphMutated();
    }

    private void ToggleDisableConnectionWarnings(NodeViewModel node)
    {
        if (_vm is null) return;
        bool disabled = node.Model.Attributes.TryGetValue("DisableConnectionWarnings", out var dv)
                     && string.Equals(dv, "true", StringComparison.OrdinalIgnoreCase);
        PushUndo();
        if (disabled) node.Model.Attributes.Remove("DisableConnectionWarnings");
        else          node.Model.Attributes["DisableConnectionWarnings"] = "true";
        // Repaint the unpaired-event red-outline set immediately so toggling
        // the flag visibly clears (or restores) the red border on this node.
        // OnGraphMutated alone does NOT re-run the error-state pass.
        RefreshEventPairErrorState();
        _vm.OnGraphMutated();
    }

    /// <summary>
    /// Open the HTML Node Reference deep-linked to <paramref name="nodeTitle"/>.
    /// 0.13.x — the native singleton TreeView window was retired in favour of
    /// the shared WebView2 DocViewer (Hub-hosted) reached via
    /// <c>NodeDocumentationWindow.OpenOrFocus</c> → <c>DocViewerHost</c>. The
    /// old canvas-focus-regrab-on-close hook went with it: the viewer
    /// is a separate Hub window Architect no longer owns the lifetime of, and
    /// clicking back onto the canvas restores focus naturally.
    /// </summary>
    private void OpenNodeDocumentationFor(string nodeTitle)
    {
        try
        {
            NodeDocumentationWindow.OpenOrFocus(nodeTitle);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Architect.LogicCanvasView", "OpenNodeDocumentationFor failed", ex);
        }
    }

    // ─── Link menu ─────────────────────────────────────────────────────

    private void ShowLinkMenu(LinkViewModel link, Point hostPoint)
    {
        var flyout = NewStyledMenuFlyout("WIRE");

        // Multi-aware. Insert / Straighten are
        // per-wire affordances and only make sense single-selected; hide on
        // multi. Delete branches its label to reflect the cross-kind total
        // and routes to DeleteSelection when multi.
        int total = (_vm?.SelectedNodes.Count ?? 0)
                  + (_vm?.SelectedLinks.Count ?? 0)
                  + (_vm?.SelectedFrames.Count ?? 0);
        bool multi = total > 1;
        string delText = multi ? $"Delete {total} item(s)" : "Delete wire";
        flyout.Items.Add(NewMenuItem(delText, GlyphDelete, () =>
        {
            if (multi) DeleteSelection();
            else        RemoveLink(link);
        }, "Del"));

        if (!multi)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            // Thread the right-click hostPoint through so the
            // reroute lands under the cursor instead of at the geometric
            // midpoint between the two endpoint nodes.
            var capturedHostPoint = hostPoint;
            flyout.Items.Add(NewMenuItem("Insert Reroute",        GlyphNew,     () => InsertReroute(link, capturedHostPoint)));
            flyout.Items.Add(NewMenuItem("Straighten Connection", GlyphRefresh, () => StraightenLink(link)));
        }

        flyout.ShowAt(HostRoot, hostPoint);
    }

    private void InsertReroute(LinkViewModel link, Point hostPoint)
    {
        if (_vm is null) return;
        var graph = _vm.Graph;
        var fromNode = graph.Nodes.Find(n => n.Id == link.Model.FromNodeId);
        var toNode   = graph.Nodes.Find(n => n.Id == link.Model.ToNodeId);
        if (fromNode is null || toNode is null) return;
        var fromSock = fromNode.Sockets.Find(s => s.Id == link.Model.FromSocketId);
        var toSock   = toNode  .Sockets.Find(s => s.Id == link.Model.ToSocketId);
        if (fromSock is null || toSock is null) return;

        // Anchor the reroute at the right-click point in canvas
        // space; pre-fix the reroute landed at the midpoint between the two
        // endpoint NODE locations, which on a long wire whose bezier was
        // already routed through a different region could be far from where
        // the user actually clicked.
        var canvasPos = HostToCanvas(hostPoint);
        int midX = (int)System.Math.Round(canvasPos.X);
        int midY = (int)System.Math.Round(canvasPos.Y);
        var reroute = NodeRegistry.CreateNode("Flow.Reroute", new System.Drawing.Point(midX, midY));
        if (reroute is null) return;

        // Resolve + validate + type the reroute's sockets BEFORE any model
        // mutation or undo checkpoint — so the (template-guaranteed, defensive)
        // missing-socket early-return can't leave the graph half-spliced (link
        // removed + node added) with no VM sync behind it.
        var rerouteIn  = reroute.Sockets.FirstOrDefault(s => s.Type == SocketType.Input);
        var rerouteOut = reroute.Sockets.FirstOrDefault(s => s.Type == SocketType.Output);
        if (rerouteIn is null || rerouteOut is null) return;
        rerouteIn.DataType  = fromSock.DataType;
        rerouteIn.Color     = fromSock.Color;
        rerouteOut.DataType = fromSock.DataType;
        rerouteOut.Color    = fromSock.Color;

        PushUndo();
        graph.Links.Remove(link.Model);
        graph.Nodes.Add(reroute);

        var inLink = new Link
        {
            FromNodeId = fromNode.Id, FromSocketId = fromSock.Id,
            ToNodeId   = reroute.Id, ToSocketId   = rerouteIn.Id,
        };
        var outLink = new Link
        {
            FromNodeId = reroute.Id, FromSocketId = rerouteOut.Id,
            ToNodeId   = toNode.Id,   ToSocketId   = toSock.Id,
        };
        graph.Links.Add(inLink);
        graph.Links.Add(outLink);

        // Incremental splice (drop the spliced wire's VM, add
        // the reroute node + its two new links) instead of LoadGraph rebuilding
        // every VM + a double socket walk — the reroute-insert lag. The model
        // link was removed above (graph.Links.Remove); removedLinks drops its VM.
        var rerouteVm = _vm.ApplyIncrementalReroute(
            reroute, new[] { inLink, outLink }, new[] { link.Model });

        // Re-select the new reroute VM so the user can drag it / wire
        // from it without hunting for it.
        if (rerouteVm is not null)
            _vm.SetMultiSelection(new[] { rerouteVm });
    }

    private void StraightenLink(LinkViewModel link)
    {
        if (_vm is null) return;
        var fromNode = _vm.Nodes.FirstOrDefault(n => n.Model.Id == link.Model.FromNodeId);
        var toNode   = _vm.Nodes.FirstOrDefault(n => n.Model.Id == link.Model.ToNodeId);
        if (fromNode is null || toNode is null) return;
        int fromRow = SocketRowIndexOf(fromNode, link.Model.FromSocketId);
        int toRow   = SocketRowIndexOf(toNode,   link.Model.ToSocketId);
        if (fromRow < 0 || toRow < 0) return;
        // [win2d-layout] Align to the EXACT wire-endpoint Y: prefer the measured
        // row-centre (set when the node was mounted to edit) over the computed
        // dynamic estimate, matching SocketViewModel.Anchor / LinkViewModel. Both
        // branches are outer-top relative (measured already includes NodeBorderInset;
        // the dynamic branch adds it) so the delta below aligns the painted pins.
        var fromCenter = SocketRenderState.TryGetMeasuredRowCenterY(link.Model.FromSocketId)
                         ?? (NodeGeometry.NodeBorderInset + NodeGeometry.RowCenterYDynamic(fromNode.Model, fromRow));
        var toCenter   = SocketRenderState.TryGetMeasuredRowCenterY(link.Model.ToSocketId)
                         ?? (NodeGeometry.NodeBorderInset + NodeGeometry.RowCenterYDynamic(toNode.Model,   toRow));
        double dy = (fromNode.Y + fromCenter) - (toNode.Y + toCenter);
        if (System.Math.Abs(dy) < 0.5) return;
        PushUndo();
        toNode.Translate(0, dy);
        _vm.OnGraphMutated();
    }

    private static int SocketRowIndexOf(NodeViewModel n, string socketId)
    {
        for (int i = 0; i < n.Inputs.Count;  i++) if (n.Inputs[i].Id  == socketId) return i;
        for (int i = 0; i < n.Outputs.Count; i++) if (n.Outputs[i].Id == socketId) return i;
        return -1;
    }

    // ─── Pill menu ─────────────────────────────────────────────────────

    private void ShowPillMenu(SocketViewModel sock, Point hostPoint)
    {
        if (_vm is null) return;
        string pillLabel = string.IsNullOrEmpty(sock.Label) ? "VALUE" : $"VALUE · {sock.Label.ToUpperInvariant()}";
        if (pillLabel.Length > 24) pillLabel = pillLabel[..23] + "…";
        var flyout = NewStyledMenuFlyout(pillLabel);

        flyout.Items.Add(NewMenuItem("Promote to Local Variable", GlyphNew, () => PromotePillToVariable(sock, addToGraphPanel: false, hostPoint)));
        flyout.Items.Add(NewMenuItem("Promote to Graph Variable", GlyphNew, () => PromotePillToVariable(sock, addToGraphPanel: true,  hostPoint)));

        if (TryGetPillVarToken(sock, out var varName))
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(NewMenuItem("Trace Variable…",       GlyphSearch, () => TraceVariable(varName!)));
            flyout.Items.Add(NewMenuItem("Pin Variable to Canvas", GlyphPin,   () => PinVariableToCanvas(varName!)));
        }

        flyout.ShowAt(HostRoot, hostPoint);
    }

    private static bool TryGetPillVarToken(SocketViewModel sock, out string? varName)
    {
        varName = null;
        var v = sock.ValuePill;
        if (string.IsNullOrEmpty(v)) return false;
        var m = System.Text.RegularExpressions.Regex.Match(v,
            @"\{([A-Za-z_][A-Za-z0-9_\.]*)\}");
        if (!m.Success) return false;
        varName = m.Groups[1].Value;
        return true;
    }

    /// <summary>
    /// "Promote to Local / Graph Variable" — prompts for a name (defaulting
    /// to the socket's current label-cased identifier), replaces the pill
    /// content with <c>{name}</c>, and (when <paramref name="addToGraphPanel"/>
    /// is true) inserts a <see cref="VariableDefinition"/> into
    /// <c>Graph.Variables</c>. The rename Flyout anchors at the cursor.
    /// 0.10.0 — anchored at <paramref name="hostPoint"/> rather than (0,0).
    /// </summary>
    private void PromotePillToVariable(SocketViewModel sock, bool addToGraphPanel, Point hostPoint)
    {
        if (_vm is null) return;
        var seedName = SanitizeVariableName(sock.Label);
        var box = new TextBox { Text = seedName, MinWidth = 220 };
        var flyout = new Flyout { Content = box };
        bool committed = false;
        flyout.Closed += (_, _) =>
        {
            if (!committed)
            {
                committed = true;
                CommitPromote(sock, box.Text, addToGraphPanel);
            }
            // 0.10.0 — return keyboard focus to the canvas (mirror the
            // SpawnPaletteFlyout pattern in LogicCanvasView.Palette.cs).
            try { Focus(Microsoft.UI.Xaml.FocusState.Programmatic); }
            catch { /* never break dismissal on a focus failure */ }
        };
        box.KeyDown += (_, ke) =>
        {
            if (ke.Key == Windows.System.VirtualKey.Enter)
            {
                ke.Handled = true;
                flyout.Hide();
            }
            else if (ke.Key == Windows.System.VirtualKey.Escape)
            {
                ke.Handled = true;
                committed = true;
                flyout.Hide();
            }
        };
        // Anchor on the cursor so the textbox lands where the menu was —
        // pre-0.10.0 the flyout opened at (0,0) and the user had to find it
        // after every promote attempt.
        flyout.ShowAt(HostRoot, new FlyoutShowOptions
        {
            Position  = hostPoint,
            Placement = FlyoutPlacementMode.Bottom,
        });
        box.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        box.SelectAll();
    }

    private void CommitPromote(SocketViewModel sock, string rawName, bool addToGraphPanel)
    {
        if (_vm is null) return;
        var name = SanitizeVariableName(rawName);
        if (string.IsNullOrEmpty(name)) return;
        var oldValue = sock.ValuePill ?? string.Empty;
        PushUndo();
        sock.ValuePill = "{" + name + "}";
        if (addToGraphPanel)
        {
            var existing = _vm.Graph.Variables.FirstOrDefault(v =>
                string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                _vm.Graph.Variables.Add(new VariableDefinition
                {
                    Name = name,
                    Type = GuessVariableType(oldValue),
                });
            }
        }
        _vm.OnGraphMutated();
    }

    private static string SanitizeVariableName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '.') sb.Append(c);
        }
        return sb.ToString();
    }

    private static string GuessVariableType(string value)
    {
        var v = (value ?? string.Empty).Trim();
        if (v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("false", StringComparison.OrdinalIgnoreCase))
            return "Bool";
        if (double.TryParse(v, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _))
            return "Number";
        return "String";
    }

    private async void TraceVariable(string varName)
    {
        if (_vm is null) return;
        try
        {
            var dlg = Phoenix.Controls.Architect.WinUI.Dialogs.VarChainTraceDialog
                .ForGraph(XamlRoot, _vm.Graph, varName);
            // Clicking a writer/reader row reveals (selects
            // + frames + flashes) that node on this canvas; the dialog hides
            // itself so the node is visible.
            dlg.NavigateToNode = id =>
            {
                try { RevealNodeFromShell(id); }
                catch (Exception navEx) { GlobalLogger.Error("Architect.LogicCanvasView", "VarChain navigate", navEx); }
            };
            await dlg.ShowAsync();
            if (!string.IsNullOrEmpty(dlg.PinnedVar))
                SetVarChainPicker(dlg.PinnedVar);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Architect.LogicCanvasView", "TraceVariable failed", ex);
        }
        finally
        {
            // 0.10.0 — return keyboard focus to the canvas after the dialog
            // dismisses (mirror the SpawnPaletteFlyout focus pattern).
            try { Focus(Microsoft.UI.Xaml.FocusState.Programmatic); }
            catch { /* never break dismissal on a focus failure */ }
        }
    }

    private void PinVariableToCanvas(string varName) => SetVarChainPicker(varName);

    /// <summary>
    /// Sets the canvas-wide var-chain picker so writers / readers of
    /// <paramref name="varName"/> get highlighted. Pass null to clear.
    /// </summary>
    public void SetVarChainPicker(string? varName)
    {
        if (_vm is null) return;
        _vm.PickerVarChainName = varName;
    }

    // ─── Socket menu ───────────────────────────────────────────────────

    private static bool IsDynamicEventNode(string title)
        => title is "Event.Trigger" or "Event.Executor" or "Event.Return"
                or "Macro.Entry"   or "Macro.Exit"
                or "Process.Entry" or "Process.Exit"
                or "Visual.Trigger";

    private void ShowSocketMenu(NodeViewModel node, SocketViewModel sock, Point hostPoint)
    {
        if (_vm is null) return;
        string socketEyebrow = string.IsNullOrEmpty(sock.Label) ? "SOCKET" : $"SOCKET · {sock.Label.ToUpperInvariant()}";
        if (socketEyebrow.Length > 24) socketEyebrow = socketEyebrow[..23] + "…";
        var flyout = NewStyledMenuFlyout(socketEyebrow);

        // Universal socket actions (every non-flow, non-placeholder pin).
        var touching = _vm.Links
            .Where(l => l.Model.FromSocketId == sock.Id || l.Model.ToSocketId == sock.Id)
            .ToArray();
        var breakLabel = touching.Length switch
        {
            0 => "Break link",
            1 => "Break link",
            _ => $"Break {touching.Length} links",
        };
        var breakItem = NewMenuItem(breakLabel, GlyphCross, () =>
        {
            if (touching.Length == 0) return;
            PushUndo();
            foreach (var lk in touching) RemoveLink(lk, pushUndo: false);
        });
        breakItem.IsEnabled = touching.Length > 0;
        flyout.Items.Add(breakItem);

        if (sock.Direction == SocketType.Input && sock.HasValuePill)
        {
            flyout.Items.Add(NewMenuItem("Reset value", GlyphRefresh, () =>
            {
                PushUndo();
                sock.ValuePill = null;
            }));
        }

        if (sock.Direction == SocketType.Input
            && sock.HasValuePill
            && !string.IsNullOrEmpty(sock.ValuePill)
            && !sock.ValuePill!.TrimStart().StartsWith("{"))
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(NewMenuItem("Promote to local variable", GlyphNew, () => PromotePillToVariable(sock, addToGraphPanel: false, hostPoint)));
            flyout.Items.Add(NewMenuItem("Promote to graph variable", GlyphNew, () => PromotePillToVariable(sock, addToGraphPanel: true,  hostPoint)));
        }

        // Set Type + Remove Socket are universal socket actions — the
        // pre-T15 Canvas.ContextMenus added them to EVERY socket right-click on
        // ANY node type (String.Append, Math.Add, conditionals, etc.), not just
        // dynamic-event hosts. Only Disable Connection Warnings stays gated to
        // Event.Trigger/Executor/Return below. RemoveDynamicSocket /
        // SetDynamicSocketType already drive a SyncEventPair for the event-node
        // case, so they're safe to invoke on any node (the sync is a no-op for
        // non-event titles).
        flyout.Items.Add(new MenuFlyoutSeparator());

        var setType = new MenuFlyoutSubItem
        {
            Text = "Set Type",
            Icon = new FontIcon { Glyph = GlyphRefresh, FontFamily = new FontFamily(IconFontFamily) },
        };
        AddType("String", SocketDataType.String);
        AddType("Number", SocketDataType.Int);
        AddType("Float",  SocketDataType.Float);
        AddType("Bool",   SocketDataType.Bool);
        AddType("Array",  SocketDataType.Collection);
        AddType("Any",    SocketDataType.Any);
        flyout.Items.Add(setType);

        flyout.Items.Add(NewMenuItem("Remove Socket", GlyphDelete, () => RemoveDynamicSocket(node, sock),
            foregroundBrushKey: "StatusRedBrush"));

        // Disable Connection Warnings — stays conditional on the event-pair
        // titles (only those nodes carry the connection-warning model).
        if (node.Title is "Event.Trigger" or "Event.Executor" or "Event.Return")
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            bool disabled = node.Model.Attributes.TryGetValue("DisableConnectionWarnings", out var dv)
                         && string.Equals(dv, "true", StringComparison.OrdinalIgnoreCase);
            flyout.Items.Add(NewToggleItem("Disable Connection Warnings", GlyphWarning, disabled,
                () => ToggleDisableConnectionWarnings(node)));
        }

        void AddType(string label, SocketDataType t) => setType.Items.Add(NewMenuItem(label, string.Empty, () => SetDynamicSocketType(node, sock, t)));

        flyout.ShowAt(HostRoot, hostPoint);
    }

    private void SetDynamicSocketType(NodeViewModel node, SocketViewModel sock, SocketDataType t)
    {
        if (_vm is null) return;
        PushUndo();
        sock.Model.DataType = t;
        sock.Model.Color    = NodeRegistry.ColorFromDataType(t);
        sock.NotifyDataTypeChanged();
        // Mirror the type/color change onto the paired Event.* node's
        // corresponding socket. The pre-T15 Canvas.ContextMenus called
        // SyncEventPair after every socket DataType/Color edit; without it the
        // trigger and executor diverge on data type when changed via the menu.
        // No-op for non-event titles (the helper guards on title internally).
        PlaceholderActivator.SyncEventPair(_vm.Graph, node.Model);
        try { NodeRegistry.ResolveWildcardCascade(_vm.Graph); }
        catch { /* best-effort */ }
        // Refresh the paired node's view if the sync mutated its sockets — the
        // peer's NodeViewModel needs to rebuild to reflect the new type/name.
        RefreshPairedEventNodeViews(node);
        // Propagate the retype to paired Event nodes in OTHER .phxg files too
        // (live to open windows + on disk for closed ones). The wire-drop /
        // wire-remove paths already do this (TryCreateLink / RemoveLink); this
        // context-menu retype path was the gap that left cross-file peers stale.
        if (TouchesEventPair(node))
        {
            ScheduleCrossFileEventPairSync();
            RefreshEventPairErrorState();
        }
        _vm.OnGraphMutated();
    }

    private void RemoveDynamicSocket(NodeViewModel node, SocketViewModel sock)
    {
        if (_vm is null) return;
        var doomed = _vm.Links.Where(l => l.Model.FromSocketId == sock.Id || l.Model.ToSocketId == sock.Id).ToArray();
        PushUndo();
        foreach (var lk in doomed) RemoveLink(lk, pushUndo: false);
        node.Model.Sockets.Remove(sock.Model);
        node.RebuildSockets();
        // Sync the paired Event.* node so removing a socket on one half
        // shrinks the other half's socket list too. The pre-T15
        // Canvas.ContextMenus called SyncEventPair after node.Sockets.Remove.
        // No-op for non-event titles.
        PlaceholderActivator.SyncEventPair(_vm.Graph, node.Model);
        RefreshPairedEventNodeViews(node);
        // Propagate the socket removal to paired Event nodes in OTHER .phxg
        // files (live to open windows + on disk for closed ones). RemoveLink
        // above only schedules the cross-file sync when the removed socket had
        // a wire; an unwired bubble removed via the context menu would leave
        // cross-file peers stale without this.
        if (TouchesEventPair(node))
        {
            ScheduleCrossFileEventPairSync();
            RefreshEventPairErrorState();
        }
        _vm.Graph.MarkStructuralChange();
        _vm.OnGraphMutated();
    }

    /// <summary>
    /// After a SyncEventPair mutation rooted at <paramref name="source"/>,
    /// rebuild the NodeView socket lists of every OTHER Event.Trigger /
    /// Event.Executor / Event.Return node sharing the same EventName so the
    /// peer's rendered sockets reflect the synced model. The source node's own
    /// view is rebuilt by its caller (RebuildSockets / NotifyDataTypeChanged).
    /// </summary>
    private void RefreshPairedEventNodeViews(NodeViewModel source)
    {
        if (_vm is null) return;
        string srcTitle = source.Model.Title;
        if (srcTitle is not ("Event.Trigger" or "Event.Executor" or "Event.Return")) return;
        if (!source.Model.Attributes.TryGetValue("EventName", out var ev) || string.IsNullOrWhiteSpace(ev))
            return;
        foreach (var vm in _vm.Nodes)
        {
            if (ReferenceEquals(vm, source)) continue;
            if (vm.Model.Title is not ("Event.Trigger" or "Event.Executor" or "Event.Return")) continue;
            if (!vm.Model.Attributes.TryGetValue("EventName", out var pv)) continue;
            if (!pv.Equals(ev, StringComparison.OrdinalIgnoreCase)) continue;
            vm.RebuildSockets();
        }
    }

    /// <summary>
    /// Place a clone of <paramref name="src"/> shifted by 24×24 canvas-space
    /// pixels so it doesn't sit exactly under the original.
    /// </summary>
    private void DuplicateNode(NodeViewModel src)
    {
        if (_vm is null) return;
        var json  = JsonSerializer.Serialize(src.Model);
        var clone = JsonSerializer.Deserialize<Node>(json);
        if (clone is null) return;

        clone.Id = Guid.NewGuid().ToString();
        foreach (var s in clone.Sockets) s.Id = Guid.NewGuid().ToString();

        AddNode(clone, src.X + 24, src.Y + 24);
    }

    // ─── Node finder Flyout (Ctrl+F) ────────────────────────────────────

    /// <summary>
    /// Strongly-typed ItemsSource record for the Find Node flyout
    /// — pre-fix the binding shape was a flat anonymous type which under
    /// PublishAot stops surfacing properties through
    /// <c>GetProperty("Vm")</c> reflection (anonymous-type metadata is
    /// trimmed). Architect.WinUI ships JIT today so the pre-fix shape worked
    /// in practice, but moving to a private record future-proofs the
    /// Updater AOT story Majo flagged in the TODO.
    /// </summary>
    private sealed record FinderItem(string Label, NodeViewModel Vm);

    // F3 / Shift+F3 iteration state.
    // The Find flyout populates these on every TextChanged so the keyboard
    // chord can walk forward / backward through the live filtered set even
    // after the flyout has dismissed. Cleared on graph swap by the same
    // VM-rebind path that nukes _nodeViews / _frameViews — see OnDataContextChanged.
    private System.Collections.Generic.List<NodeViewModel> _findMatches = new();
    private int _findCursor = -1;

    /// <summary>
    /// F3 advance (next match).
    /// Called from <c>LogicCanvasView.Keyboard.cs</c>'s F3 / Shift+F3 handler.
    /// When the match set is empty (cold canvas, search box was never typed
    /// into, last search produced no hits) we re-open the Find flyout so the
    /// chord is discoverable even from a clean state. When non-empty we
    /// advance the cursor (wrapping), frame + flash the next match.
    /// </summary>
    /// <param name="reverse">true for Shift+F3 (previous match), false for F3.</param>
    internal void StepFindCursor(bool reverse)
    {
        if (_vm is null) return;
        if (_findMatches.Count == 0)
        {
            // Cold path — re-open the flyout. The user can start typing
            // immediately; the search box's TextChanged handler will populate
            // _findMatches and reset _findCursor so a subsequent F3 advances
            // through the freshly-built list.
            ShowNodeFinderFlyout();
            return;
        }

        int n = _findMatches.Count;
        if (_findCursor < 0) _findCursor = reverse ? n - 1 : 0;
        else                 _findCursor = ((_findCursor + (reverse ? -1 : 1)) % n + n) % n;

        var target = _findMatches[_findCursor];
        try
        {
            _vm.SetMultiSelection(new[] { target });
            FrameSelection();
            FlashNode(target.Model.Id);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Architect.LogicCanvasView", "StepFindCursor reveal failed", ex);
        }
    }

    /// <summary>
    /// 0.10.0 — Ctrl+F / Edit → Find Node... opens a small AutoSuggestBox
    /// flyout anchored at the host top-centre that filters the ACTIVE
    /// graph's nodes by Title (substring, case-insensitive). Picking a
    /// suggestion frames + flashes that node so the user can locate it on
    /// even a deeply scrolled/zoomed canvas. Pre-0.10.0 the Ctrl+F path
    /// recycled the spawn palette which lists templates, not the user's
    /// actual nodes — useless when the question is "where did I put that
    /// Twitch.SendMessage" instead of "what's the syntax for it".
    ///
    /// Closed → refocus the canvas (mirrors the SpawnPaletteFlyout pattern
    /// in LogicCanvasView.Palette.cs) so DEL / arrow / Ctrl+S keep working.
    ///
    /// Also writes the filtered
    /// node set into <see cref="_findMatches"/> on every TextChanged so the
    /// F3 / Shift+F3 chord can iterate the same list after the flyout closes.
    /// </summary>
    private void ShowNodeFinderFlyout()
    {
        if (_vm is null) return;

        // Build FinderItem records: prefer "Title — pillName" when the node
        // carries an editable name (e.g. Macro.Call's MacroName) so
        // duplicate-titled nodes are distinguishable in the list.
        // Strongly-typed records replace the pre-fix anonymous
        // tuples; ItemsSource binds against FinderItem directly.
        var entries = _vm.Nodes
            .Select(n => new FinderItem(BuildFinderLabel(n), n))
            .OrderBy(t => t.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (entries.Count == 0)
        {
            GlobalLogger.Log("Find Node: graph is empty.", "Architect.LogicCanvasView", LogLevel.System);
            return;
        }

        var box = new AutoSuggestBox
        {
            PlaceholderText  = "Find node by title…",
            QueryIcon        = new SymbolIcon(Symbol.Find),
            Width            = 320,
            TextMemberPath   = "Label",
        };

        // Initial suggestion list — full set; AutoSuggestBox shows the
        // dropdown the moment the user starts typing.
        box.ItemsSource = entries;

        // Seed the F3 iteration
        // state with the full unfiltered set so a fresh F3 immediately after
        // opening the flyout (no typing yet) still has somewhere to go.
        _findMatches = entries.Select(t => t.Vm).ToList();
        _findCursor  = -1;

        box.TextChanged += (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var query = (box.Text ?? string.Empty).Trim();
            System.Collections.Generic.List<FinderItem> filtered;
            if (string.IsNullOrEmpty(query))
            {
                filtered = entries;
            }
            else
            {
                filtered = entries
                    .Where(t => t.Label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
            }
            box.ItemsSource = filtered;
            // Keep the F3 walker pointed at the current filtered set.
            // Reset the cursor on every keystroke so the next F3 starts from
            // the head of the new result list rather than an index that may
            // no longer exist.
            _findMatches = filtered.Select(t => t.Vm).ToList();
            _findCursor  = -1;
        };

        var flyout = new Flyout
        {
            Content   = box,
            Placement = FlyoutPlacementMode.Bottom,
        };

        void RevealAndClose(NodeViewModel target)
        {
            try
            {
                _vm.SetMultiSelection(new[] { target });
                FrameSelection();
                FlashNode(target.Model.Id);
                // Sync the iteration cursor to the picked match so a
                // post-pick F3 / Shift+F3 walks from here rather than the
                // head of the list.
                int idx = _findMatches.IndexOf(target);
                if (idx >= 0) _findCursor = idx;
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("Architect.LogicCanvasView", "Finder reveal failed", ex);
            }
            finally
            {
                flyout.Hide();
            }
        }

        box.SuggestionChosen += (_, args) =>
        {
            // Direct cast to FinderItem — no reflection lookup,
            // AOT-safe.
            if (args.SelectedItem is FinderItem chosen)
                RevealAndClose(chosen.Vm);
        };

        box.QuerySubmitted += (_, args) =>
        {
            // Enter without picking a suggestion — fall back to the first
            // matching node.
            if (args.ChosenSuggestion is FinderItem chosen)
            {
                RevealAndClose(chosen.Vm);
                return;
            }
            var query = (args.QueryText ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(query)) return;
            var match = entries.FirstOrDefault(t =>
                t.Label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
            if (match is not null) RevealAndClose(match.Vm);
        };

        // Refocus the canvas on dismiss so DEL / arrows / Ctrl+S keep
        // working without an extra click — mirrors the SpawnPaletteFlyout
        // pattern in LogicCanvasView.Palette.cs.
        flyout.Closed += (_, _) =>
        {
            try { Focus(Microsoft.UI.Xaml.FocusState.Programmatic); }
            catch { /* never break dismissal on a focus failure */ }
        };

        // Anchor at the top-centre of the host so the dropdown extends
        // downward into the visible canvas instead of off the bottom edge.
        var anchor = new Point(HostRoot.ActualWidth / 2, 16);
        flyout.ShowAt(HostRoot, new FlyoutShowOptions
        {
            Position           = anchor,
            ShowMode           = FlyoutShowMode.Standard,
            Placement          = FlyoutPlacementMode.Bottom,
        });
        box.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }

    /// <summary>
    /// Finder label builder — prefers "Title — UserName" when the node
    /// carries an editable name attribute (Macro.Call.MacroName,
    /// Process.Spawn.ProcessName, Event.Trigger.EventName), so two
    /// Macro.Calls don't collapse to indistinguishable "Macro.Call" rows.
    /// </summary>
    private static string BuildFinderLabel(NodeViewModel n)
    {
        string title = n.Title ?? string.Empty;
        if (n.Model.Attributes.TryGetValue("MacroName", out var mn) && !string.IsNullOrWhiteSpace(mn))
            return $"{title} — {mn}";
        if (n.Model.Attributes.TryGetValue("ProcessName", out var pn) && !string.IsNullOrWhiteSpace(pn))
            return $"{title} — {pn}";
        if (n.Model.Attributes.TryGetValue("EventName", out var en) && !string.IsNullOrWhiteSpace(en))
            return $"{title} — \"{en}\"";
        return title;
    }
}
