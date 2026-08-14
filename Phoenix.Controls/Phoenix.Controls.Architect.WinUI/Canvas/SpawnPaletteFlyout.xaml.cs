using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Architect.Core;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Windows.UI;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

// Non-modal flyout for spawning nodes. Opened on right-click empty canvas
// (LogicCanvasView.Menus.cs) or on a wire-drop onto empty canvas (Pointer).
//
// 0.10.0 additions over the pre-0.10.0 surface:
//   * SetContext(SocketDataType?, isMacroGraph, hasMacroEntry, hasMacroExit)
//     replaces the SetCompatibilityFilter single-knob; macro-graph context
//     suppresses Macro.Entry / Macro.Exit when one already exists, and
//     wire-drop-from prepends a COMPATIBLE section keyed on the source type.
//   * Matched-substring highlight on the filter results — the matching
//     ranges of the title render in a brighter brush via TextHighlighter.
//   * Full keyboard navigation: Up/Down arrows in SearchBox traverse rows,
//     Enter spawns the focused row, Esc dismisses.
//   * Closed event nulls out _pendingWireDropSource on the canvas via a
//     ClosedCleanup callback so a dismiss-without-pick doesn't leak the
//     pending wire onto the next palette open.
public sealed partial class SpawnPaletteFlyout : Microsoft.UI.Xaml.Controls.Flyout
{
    // BulkObservableCollection so ApplyFilter commits the
    // ~150-row catalogue in ONE Reset notification instead of _rows.Clear()
    // followed by ~150 individual Add notifications on every open/keystroke —
    // the "big lag when opening the space (search) menu". ReplaceAll(list).
    private readonly BulkObservableCollection<SpawnRow> _rows = new();
    private List<SpawnRow> _all = new();

    // The per-category "ALL NODES" tree (header
    // rows + template rows, in category order) only depends on _all and the
    // macro-suppression context — neither changes between empty-query renders
    // within one open. Cache it so opening the palette / clearing the search
    // doesn't regroup ~132 templates each time. Invalidated when _all is rebuilt
    // or SetContext changes the suppression flags.
    private List<SpawnRow>? _categoryTreeCache;

    // Context — set by LogicCanvasView.Palette before showing the flyout.
    private SocketDataType? _compatibilityFilter;
    // Source-side direction lets the COMPATIBLE filter walk the
    // template's OUTPUT sockets when the wire-drop originated from an Input
    // socket (Ctrl+left-on-input rewire). Pre-fix the filter only walked
    // template Inputs which silently dropped templates whose matching pin
    // sat on the output side. Null defaults to the Output-source path so
    // existing call sites that don't thread direction stay correct.
    private SocketType? _compatibilitySourceDirection;
    private bool _isMacroGraph;
    private bool _hasMacroEntry;
    private bool _hasMacroExit;

    // Latest query — passed into the row-realization handler so it can paint
    // matched-substring highlights as the ListView lazily materialises rows.
    private string _currentQuery = string.Empty;

    // UI-thread debounce so a typing burst in the
    // SearchBox triggers ApplyFilter exactly once after the user pauses,
    // instead of running the per-keystroke filter pass over the ~132-template
    // catalog. 200 ms is the standard "search-as-you-type" budget that feels
    // immediate without re-filtering on every chord. The timer is a
    // DispatcherTimer (not Threading.Timer) so the tick lands on the UI
    // thread directly — ApplyFilter mutates the bound ObservableCollection
    // and would crash on a worker.
    private readonly DispatcherTimer _filterDebounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(200),
    };

    /// <summary>
    /// Optional clean-up hook fired after Closed. The canvas hands in a
    /// closure that nulls <c>_pendingWireDropSource</c> so a dismiss-without-pick
    /// doesn't leak the pending wire onto the next palette open. 0.10.0.
    /// </summary>
    public Action? ClosedCleanup { get; set; }

    // Per-flyout recent list, capped at 5. INSTANCE field (was static):
    // each LogicCanvasView constructs its own SpawnPaletteFlyout
    // and re-uses it across opens, so the per-canvas scoping survives
    // long-running sessions. Pre-fix s_recent was process-global, which
    // leaked recent-spawn history across multi-window Architect: spawning
    // Var.Set in window A would surface it in window B's Recent list even
    // though that user-session is operating on a different .phxg. The
    // suite supports multi-window where it makes sense — a flyout that
    // ignores the window separation contradicts the rule.
    private readonly LinkedList<string> _recent = new();
    private const int RecentCap = 5;

    // s_pinnedTitles and s_newTemplateTitles are process-wide
    // read-only static configuration — NOT per-session state. Both are
    // safe across multi-window Architect because no instance ever mutates
    // them; they describe "what node templates exist in the catalogue" and
    // "which of those count as new this release", which is identical for
    // every window. The recent-spawn history (which IS per-session) lives
    // on _recent above, instance-scoped.
    private static readonly string[] s_pinnedTitles = { "Event.Trigger", "Event.Executor" };

    /// <summary>
    /// Template titles that should render an ember NEW badge in the spawn
    /// palette. The badge marks templates introduced in
    /// the current release window so a returning user can spot them in the
    /// catalog without grep'ing the release notes. Curated in code (not on
    /// NodeTemplate) so the ~140 AddTemplate call sites in NodeRegistry stay
    /// untouched and the marker can be advanced from release to release with
    /// a single-file edit. Empty entries are silently ignored.
    /// Read-only static: shared safely across windows.
    /// </summary>
    private static readonly HashSet<string> s_newTemplateTitles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Populated as 0.10.0 adds new templates. Intentionally empty at
            // release start so the badge surface is wired without claiming
            // anything as "new" prematurely.
        };

    // Matched-substring highlight foreground resolved from
    // the theme dictionary's Ember200Brush (#F2C77F) so a brass-palette
    // retune flows through the search-highlight automatically. Fallback
    // preserves the ember-gold literal for designer-time lookups.
    private static readonly Brush s_highlightForeground = ResolveBrush(
        "Ember200Brush",
        ArchitectCanvasPalette.EmberDefaultPreview);

    private static Brush ResolveBrush(string key, Color fallback)
    {
        try
        {
            if (Microsoft.UI.Xaml.Application.Current?.Resources is { } res
                && res.TryGetValue(key, out var found)
                && found is Brush brush) return brush;
        }
        catch { /* designer-time / pre-app construction — fall through */ }
        return new SolidColorBrush(fallback);
    }

    public SpawnPaletteFlyout()
    {
        InitializeComponent();
        TemplateList.ItemsSource = _rows;
        _filterDebounce.Tick += OnFilterDebounceTick;
        SearchBox.TextChanged += OnSearchTextChanged;
        TemplateList.ItemClick += OnItemClicked;
        TemplateList.IsItemClickEnabled = true;
        TemplateList.ContainerContentChanging += OnContainerContentChanging;
        Opened += (_, _) =>
        {
            EnsureLoaded();
            SearchBox.Text = string.Empty;
            _currentQuery = string.Empty;
            // Open with the unfiltered catalog visible immediately — no
            // debounce on the synthetic empty-query reset. Stop any in-flight
            // timer so a residual tick from the previous open doesn't run
            // ApplyFilter again on stale state.
            _filterDebounce.Stop();
            ApplyFilter(string.Empty);
            SearchBox.Focus(FocusState.Programmatic);
        };
        Closed += (_, _) =>
        {
            // Stop the debounce so a tick scheduled during the last typing
            // burst can't fire after the palette is gone (TemplateList /
            // _rows would still be valid, but the user's intent to filter is
            // moot once the flyout closes).
            _filterDebounce.Stop();
            // Drop the wire-drop source so the next open starts clean even
            // when the user dismisses the palette by clicking elsewhere.
            try { ClosedCleanup?.Invoke(); }
            catch { /* never block dismissal */ }
        };
        SearchBox.KeyDown += OnSearchBoxKeyDown;
    }

    /// <summary>Fires with the chosen template title when the user picks a node row.</summary>
    public event EventHandler<string>? Selected;

    /// <summary>Fires with "comment" or "placeholder" when the user picks a frame entry.</summary>
    public event EventHandler<string>? FrameActionRequested;

    /// <summary>
    /// Configure the flyout for the current canvas context. 0.10.0 replaces
    /// the older single-knob <c>SetCompatibilityFilter</c>.
    ///
    /// <para><paramref name="sourceType"/> — non-null when opening from a wire
    /// drop on empty canvas; the flyout prepends a COMPATIBLE section listing
    /// templates whose first compatible input matches the source type.</para>
    ///
    /// <para><paramref name="isMacroGraph"/> — true when the canvas is editing
    /// a Macro sub-graph (SubGraphWindow). Together with the next two flags
    /// gates Macro.Entry / Macro.Exit out of the template list when one
    /// already exists in the graph.</para>
    ///
    /// <para><paramref name="sourceDirection"/> — direction of the
    /// wire-drop source socket. Output (default / null) walks template
    /// inputs for compat; Input walks template outputs so a wire dragged
    /// from a node's input pin still surfaces relevant spawn candidates.</para>
    /// </summary>
    public void SetContext(SocketDataType? sourceType,
                           bool isMacroGraph,
                           bool hasMacroEntry,
                           bool hasMacroExit,
                           SocketType? sourceDirection = null)
    {
        _compatibilityFilter           = sourceType;
        _compatibilitySourceDirection  = sourceDirection;
        _isMacroGraph                  = isMacroGraph;
        _hasMacroEntry                 = hasMacroEntry;
        _hasMacroExit                  = hasMacroExit;
        _categoryTreeCache = null; // suppression context changed
    }

    /// <summary>
    /// Legacy single-knob filter retained as a thin shim so any caller still
    /// reaching for "just a compatibility hint" doesn't have to thread the
    /// full SetContext args. New code should call <see cref="SetContext"/>.
    /// </summary>
    public void SetCompatibilityFilter(SocketDataType? sourceType)
        => SetContext(sourceType, isMacroGraph: false, hasMacroEntry: false, hasMacroExit: false);

    /// <summary>
    /// Records that <paramref name="title"/> was just spawned. Pre-T15 the
    /// canvas itself owned Recent; here the flyout owns the list so the
    /// header re-renders without round-tripping through the canvas.
    /// Instance-scoped (was static) so multi-window Architect
    /// doesn't bleed history across windows.
    /// </summary>
    public void TrackRecent(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        _recent.Remove(title);
        _recent.AddFirst(title);
        while (_recent.Count > RecentCap) _recent.RemoveLast();
    }

    private void EnsureLoaded()
    {
        // _all caches across the flyout's lifetime — if a hot-reload
        // or test-only path ever starts mutating NodeRegistry.GetAllTemplates()
        // after the first open, the new templates won't surface until the
        // flyout is recreated. Production code never mutates the registry at
        // runtime today, so the cache is fine. If that ever changes, gate
        // this on a NodeRegistry.Version stamp (or hook a NodeRegistry
        // OnTemplatesChanged event) and clear _all on the change.
        if (_all.Count > 0) return;
        // Majo flagged: Databank nodes were filtered out here on the
        // assumption they spawn via a Databank-panel drag, but that surface isn't
        // reachable, leaving DB nodes uncreatable ("they exist, but are not
        // call-able"). DB nodes are now listed in the search palette like any
        // other category. Macros stays excluded — Macro.Call has the Macros sidebar.
        var raw = NodeRegistry.GetPaletteTemplates()
            .Where(t => !string.Equals(t.Category, "Macros", StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Title,    StringComparer.OrdinalIgnoreCase)
            .Select(t => SpawnRow.Template(t))
            .ToList();
        _all = raw;
        _categoryTreeCache = null; // catalogue changed
    }

    // Templates that should render as a STANDALONE row at the very top of the
    // palette (above COMPATIBLE / MY BLUEPRINT / RECENT), with no category
    // header above them — and be suppressed from the per-category tree
    // further down so they don't double-show. Currently only Flow.Reroute,
    // a wire-utility "independent point" rather than a member of any
    // functional bucket. Driven off NodeTemplate.Category="Reroute" so a
    // single registry edit decides what gets hoisted — no second list to
    // keep in sync.
    private const string TopLevelStandaloneCategory = "Reroute";

    private static bool IsTopLevelStandalone(string? category)
        => !string.IsNullOrEmpty(category)
        && string.Equals(category, TopLevelStandaloneCategory, StringComparison.OrdinalIgnoreCase);

    private void ApplyFilter(string query)
    {
        // Build the full row set in a local list, then commit
        // it in ONE BulkObservableCollection.ReplaceAll (single Reset) instead of
        // Clear()+N×Add — the ~150 individual CollectionChanged(Add) notifications
        // per open/keystroke were the "big lag when opening the space (search)
        // menu". The ListView already virtualises rows; the cost was the
        // notification storm, not row materialisation.
        var rows = new List<SpawnRow>(_all.Count + 16);
        bool searching = !string.IsNullOrWhiteSpace(query);

        if (!searching)
        {
            // Top-level standalone entries (currently just Flow.Reroute, in
            // its own "Reroute" category — see NodeRegistry.Templates.FlowControl).
            // Surfaced as ungrouped rows above every header so the wire-knot
            // reads as an independent point rather than a member of any
            // functional bucket, and suppressed from the per-category tree
            // below so each entry shows exactly once.
            foreach (var r in _all)
            {
                if (!IsTopLevelStandalone(r.Category)) continue;
                if (IsSuppressedByContext(r.Title)) continue;
                rows.Add(r);
            }

            // Compatibility section — when the flyout was opened by a wire-drop
            // we surface only the templates whose first compatible input
            // matches the source type, so the muscle-memory UE behaviour
            // ("drag a wire, get the right nodes") actually works.
            // Direction-aware: when the wire originated from an
            // INPUT socket, walk the template's OUTPUT pins for compat.
            if (_compatibilityFilter is { } src)
            {
                rows.Add(SpawnRow.Header(Localizer.T("architect.canvas.spawn.section.compatible", "COMPATIBLE")));
                bool sourceWasInput = _compatibilitySourceDirection == SocketType.Input;
                foreach (var r in _all)
                {
                    if (IsTopLevelStandalone(r.Category)) continue;
                    if (IsSuppressedByContext(r.Title)) continue;
                    if (!FirstPinAcceptsType(r.Title, src, sourceWasInput)) continue;
                    rows.Add(r);
                }
            }

            // Pinned "My Blueprint" header — visible only when search is empty.
            rows.Add(SpawnRow.Header(Localizer.T("architect.canvas.spawn.section.my_blueprint", "MY BLUEPRINT")));
            foreach (var pinned in s_pinnedTitles)
            {
                if (IsSuppressedByContext(pinned)) continue;
                var match = _all.FirstOrDefault(r => string.Equals(r.Title, pinned, StringComparison.OrdinalIgnoreCase));
                if (match is not null) rows.Add(SpawnRow.Pinned(match));
            }

            if (_recent.Count > 0)
            {
                rows.Add(SpawnRow.Header(Localizer.T("architect.canvas.spawn.section.recent", "RECENT")));
                foreach (var t in _recent)
                {
                    if (IsSuppressedByContext(t)) continue;
                    var match = _all.FirstOrDefault(r => string.Equals(r.Title, t, StringComparison.OrdinalIgnoreCase));
                    if (match is not null) rows.Add(SpawnRow.Recent(match));
                }
            }

            // Per-category sub-headers replace the flat
            // "ALL NODES" list. The tree
            // depends only on _all + suppression context, so it's built once and
            // reused instead of regrouped on every open / search-clear.
            rows.AddRange(GetCategoryTreeRows());

            rows.Add(SpawnRow.Header(Localizer.T("architect.canvas.spawn.section.frames", "FRAMES")));
            // "frames" (3rd arg) is the pseudo-CATEGORY tag rendered in the dim
            // right-hand column beside real NodeRegistry category names — it
            // stays English with them by the category-naming decision.
            rows.Add(SpawnRow.FrameAction("comment",     Localizer.T("architect.canvas.spawn.frame.comment",     "Add Comment Frame"),     "frames"));
            rows.Add(SpawnRow.FrameAction("placeholder", Localizer.T("architect.canvas.spawn.frame.placeholder", "Add Placeholder Frame"), "frames"));
            _rows.ReplaceAll(rows);
            return;
        }

        var tokens = TokenizeQuery(query);
        // Score every candidate, then emit best-first. OrderByDescending is a
        // STABLE sort in LINQ, so rows with equal scores keep their catalog
        // (Category/Title) order — the old behaviour for the tail of the list.
        var scored = new List<(SpawnRow Row, int Score)>(_all.Count);
        foreach (var r in _all)
        {
            if (IsSuppressedByContext(r.Title)) continue;
            if (ScoreMatch(r, tokens, query) is int s) scored.Add((r, s));
        }
        foreach (var hit in scored.OrderByDescending(x => x.Score))
            rows.Add(hit.Row);
        // A zero-result search left a blank ListView
        // with no feedback. Append a non-selectable message row (Header kind, so
        // arrow/Enter nav skips it) so the user knows the query matched nothing.
        if (rows.Count == 0)
            rows.Add(SpawnRow.Header(string.Format(
                Localizer.T("architect.canvas.spawn.no_matches", "No matches for “{0}”"), query.Trim())));
        _rows.ReplaceAll(rows);
    }

    /// <summary>
    /// Build (once) the per-category "ALL NODES"
    /// tree: header rows at each category transition plus the template rows, in
    /// the catalogue's category/title sort order, excluding top-level standalone
    /// entries (shown separately) and context-suppressed rows. Cached until _all
    /// or the suppression context changes.
    /// </summary>
    private List<SpawnRow> GetCategoryTreeRows()
    {
        if (_categoryTreeCache is not null) return _categoryTreeCache;
        var rows = new List<SpawnRow>(_all.Count + 16);
        string? lastCategory = null;
        foreach (var r in _all)
        {
            if (IsTopLevelStandalone(r.Category)) continue;
            if (IsSuppressedByContext(r.Title)) continue;
            var category = string.IsNullOrEmpty(r.Category) ? "OTHER" : r.Category.ToUpperInvariant();
            if (!string.Equals(category, lastCategory, StringComparison.Ordinal))
            {
                rows.Add(SpawnRow.Header(category));
                lastCategory = category;
            }
            rows.Add(r);
        }
        _categoryTreeCache = rows;
        return rows;
    }

    /// <summary>
    /// Context gate — when authoring a macro graph and Macro.Entry already
    /// exists, suppress Macro.Entry from the catalogue (likewise Macro.Exit).
    /// Mirrors the no-duplicate guardrail without dropping a modal dialog
    /// when the user dragged the second one out — filter the
    /// affordance out instead of rejecting after the fact.
    /// </summary>
    private bool IsSuppressedByContext(string title)
    {
        if (!_isMacroGraph) return false;
        if (string.Equals(title, "Macro.Entry", StringComparison.OrdinalIgnoreCase) && _hasMacroEntry) return true;
        if (string.Equals(title, "Macro.Exit",  StringComparison.OrdinalIgnoreCase) && _hasMacroExit)  return true;
        return false;
    }

    /// <summary>
    /// Relevance score for a row against the query, or <c>null</c> when the row
    /// does not match at all. Replaces the prior boolean prefix matcher so the
    /// results can be RANKED instead of returned in catalog order — pre-fix a
    /// short query like "se" prefix-matched dozens of pool tokens and the exact
    /// node was buried among them. Ranking (highest first):
    ///   • whole-query EXACT on the visible name / registry title  (1000)
    ///   • whole-query PREFIX on either                            ( 800)
    ///   • whole-query SUBSTRING on either                         ( 600)
    ///   • per-token prefix over the pooled fields                 ( 200 + 50/token)
    ///   • description-only substring fallback                     ( +10/token)
    /// Every query token must prefix-match a POOL field (title / display /
    /// category / keyword) or the row is rejected; description text is only a
    /// tie-break nudge, never an inclusion reason (see the in-body note), so
    /// the best title candidate surfaces first and unrelated nodes drop out.
    /// </summary>
    private static int? ScoreMatch(SpawnRow row, IReadOnlyList<string> queryTokens, string rawQuery)
    {
        if (queryTokens.Count == 0) return 0;
        string q       = rawQuery.Trim();
        string title   = row.Title ?? string.Empty;
        string display = row.DisplayTitle ?? string.Empty;

        if (title.Equals(q, StringComparison.OrdinalIgnoreCase)
            || display.Equals(q, StringComparison.OrdinalIgnoreCase))
            return 1000;
        if (title.StartsWith(q, StringComparison.OrdinalIgnoreCase)
            || display.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            return 800;
        if (q.Length > 1
            && (title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || display.Contains(q, StringComparison.OrdinalIgnoreCase)))
            return 600;

        // Use the row's cached, pre-tokenized pool
        // (title + display + category + keywords) instead of re-tokenizing per pass.
        //
        // A query token must PREFIX-MATCH a
        // pool token or the row is rejected. The prior build also accepted a
        // token that merely appeared as a SUBSTRING of the node's DESCRIPTION,
        // which flooded results: typing "bran" matched every node whose
        // description mentions "branch / branches / branch-local" (Async.Parallel,
        // DB.CheckExists, Var.Get, Public.Get/Set, Twitch.LastActive, …) even
        // though their TITLES have nothing to do with "bran" (Majo: "search bar
        // still buggy as hell"). Description text is now only a small ranking
        // nudge for a token that ALREADY hit the pool — never an inclusion reason
        // on its own. Intentional synonyms belong in the template Keywords (which
        // ARE in the pool), so no legitimate match is lost.
        var pool = row.SearchPool;
        int tokenScore = 0;
        foreach (var qt in queryTokens)
        {
            bool prefix = false;
            foreach (var p in pool)
                if (p.StartsWith(qt, StringComparison.OrdinalIgnoreCase)) { prefix = true; break; }
            if (!prefix) return null; // a query token hit no pool field → not a result
            tokenScore += 50;
            // Tie-break nudge only — the token also appears in the description.
            if (!string.IsNullOrEmpty(row.Description)
                && row.Description.Contains(qt, StringComparison.OrdinalIgnoreCase))
                tokenScore += 5;
        }
        return 200 + tokenScore;
    }

    /// <summary>
    /// Direction-aware compat check. When
    /// <paramref name="sourceWasInput"/> is true the wire originated from an
    /// input socket (Ctrl+left-on-input rewire path) and the compat lookup
    /// must walk template <see cref="NodeTemplate.Outputs"/>; otherwise the
    /// pre-fix behaviour of walking <see cref="NodeTemplate.Inputs"/>
    /// applies.
    /// </summary>
    private static bool FirstPinAcceptsType(string title, SocketDataType sourceType, bool sourceWasInput)
    {
        var t = NodeRegistry.GetTemplate(title);
        if (t is null) return false;
        var pool = sourceWasInput ? t.Outputs : t.Inputs;
        foreach (var (_, color) in pool)
        {
            var dt = NodeRegistry.DataTypeFromColorPublic(color);
            if (NodeRegistry.AreCompatible(sourceType, dt)) return true;
        }
        return false;
    }

    private static List<string> TokenizeQuery(string s)
    {
        if (string.IsNullOrEmpty(s)) return new List<string>();
        var tokens = new List<string>();
        var sb = new System.Text.StringBuilder(s.Length);
        char prev = '\0';
        foreach (var c in s)
        {
            bool boundary = !char.IsLetterOrDigit(c) ||
                            (char.IsUpper(c) && char.IsLower(prev));
            if (boundary)
            {
                if (sb.Length > 0) { tokens.Add(sb.ToString().ToLowerInvariant()); sb.Clear(); }
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            }
            else sb.Append(c);
            prev = c;
        }
        if (sb.Length > 0) tokens.Add(sb.ToString().ToLowerInvariant());
        return tokens;
    }

    // ── Filter debounce ────────────────────────────────────────────────

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _currentQuery = SearchBox.Text ?? string.Empty;
        // Reset the debounce window — the filter runs once, ~200 ms after
        // the last keystroke, instead of on every TextChanged. Stop+Start
        // restarts the timer's interval; a no-op when it's not running.
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    private void OnFilterDebounceTick(object? sender, object e)
    {
        _filterDebounce.Stop();
        ApplyFilter(_currentQuery);
    }

    /// <summary>
    /// Cancel the debounce window and apply the current query immediately.
    /// Called from keyboard handlers where the user signals "I'm done typing,
    /// act on what I see now" — Enter / Up / Down.
    /// </summary>
    private void FlushPendingFilter()
    {
        if (!_filterDebounce.IsEnabled) return;
        _filterDebounce.Stop();
        ApplyFilter(_currentQuery);
    }

    // ── Keyboard navigation ────────────────────────────────────────────

    private void OnSearchBoxKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter:
            {
                // Flush any pending debounce so the row dispatch sees the
                // results for the actual current query, not the previous
                // tick's snapshot.
                FlushPendingFilter();
                // Commit the focused row when one is selected, otherwise the
                // first non-header match in the current view.
                if (TemplateList.SelectedItem is SpawnRow focus)
                {
                    Commit(focus);
                    e.Handled = true;
                    return;
                }
                var first = _rows.FirstOrDefault(r => r.Kind != SpawnRowKind.Header);
                if (first is not null) { Commit(first); e.Handled = true; }
                return;
            }
            case Windows.System.VirtualKey.Escape:
                Hide();
                e.Handled = true;
                return;
            case Windows.System.VirtualKey.Down:
                FlushPendingFilter();
                MoveSelection(+1);
                e.Handled = true;
                return;
            case Windows.System.VirtualKey.Up:
                FlushPendingFilter();
                MoveSelection(-1);
                e.Handled = true;
                return;
        }
    }

    private void MoveSelection(int direction)
    {
        if (_rows.Count == 0) return;
        int idx = TemplateList.SelectedIndex;
        if (idx < 0) idx = direction > 0 ? -1 : _rows.Count;
        for (int step = 0; step < _rows.Count; step++)
        {
            idx += direction;
            if (idx < 0) idx = _rows.Count - 1;
            if (idx >= _rows.Count) idx = 0;
            if (_rows[idx].Kind != SpawnRowKind.Header)
            {
                TemplateList.SelectedIndex = idx;
                ((ListViewItem?)TemplateList.ContainerFromIndex(idx))?.Focus(FocusState.Keyboard);
                TemplateList.ScrollIntoView(_rows[idx]);
                return;
            }
        }
    }

    private void OnItemClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SpawnRow row) Commit(row);
    }

    private void Commit(SpawnRow row)
    {
        if (row.Kind == SpawnRowKind.Header) return;
        if (row.Kind == SpawnRowKind.FrameAction)
        {
            FrameActionRequested?.Invoke(this, row.FrameKind ?? "comment");
            Hide();
            return;
        }
        TrackRecent(row.Title);
        Selected?.Invoke(this, row.Title);
        Hide();
    }

    // ── Matched-substring highlight ────────────────────────────────────

    /// <summary>
    /// Apply (or clear) TextHighlighters on each realized title TextBlock so
    /// the matching parts of the query stand out. WinUI's ListView realizes
    /// containers lazily — this is the canonical event to walk into the
    /// row's visual tree after the bindings settled.
    /// </summary>
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        if (args.Item is not SpawnRow row || row.Kind == SpawnRowKind.Header) return;

        // ContainerContentChanging fires before the visual tree fully
        // realizes; register a one-shot phase to apply after layout. Phase 1
        // is enough — the TitleText element lives in the first DataTemplate
        // visual root.
        args.RegisterUpdateCallback((_, lateArgs) =>
        {
            if (lateArgs.ItemContainer is null) return;
            var tb = FindByName(lateArgs.ItemContainer, "TitleText") as TextBlock;
            if (tb is null) return;
            // Highlight against the visual title (DisplayTitle) since that's
            // what the TextBlock actually paints; falls back to Title when
            // DisplayTitle is empty (every row's factory seeds it).
            var visualTitle = !string.IsNullOrEmpty(row.DisplayTitle) ? row.DisplayTitle : row.Title;
            ApplyHighlighters(tb, visualTitle ?? string.Empty, _currentQuery);
        });
    }

    private static void ApplyHighlighters(TextBlock tb, string title, string query)
    {
        tb.TextHighlighters.Clear();
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(title)) return;
        var ranges = FindMatchRanges(title, query);
        if (ranges.Count == 0) return;
        var highlight = new TextHighlighter
        {
            Foreground = s_highlightForeground,
            Background = new SolidColorBrush(Color.FromArgb(0x00, 0, 0, 0)),  // transparent — foreground-only
        };
        foreach (var (start, length) in ranges)
        {
            highlight.Ranges.Add(new TextRange { StartIndex = start, Length = length });
        }
        tb.TextHighlighters.Add(highlight);
    }

    /// <summary>
    /// Find every case-insensitive match of any query token inside the title.
    /// Tokens shorter than 2 chars are ignored so single-letter queries
    /// don't paint half the catalogue gold. Returns (start, length) pairs.
    /// </summary>
    private static List<(int Start, int Length)> FindMatchRanges(string title, string query)
    {
        var ranges = new List<(int, int)>();
        var tokens = TokenizeQuery(query);
        foreach (var tok in tokens)
        {
            if (tok.Length < 2) continue;
            int from = 0;
            while (from <= title.Length - tok.Length)
            {
                int idx = title.IndexOf(tok, from, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) break;
                ranges.Add((idx, tok.Length));
                from = idx + tok.Length;
            }
        }
        return ranges;
    }

    private static DependencyObject? FindByName(DependencyObject root, string name)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name) return child;
            var nested = FindByName(child, name);
            if (nested is not null) return nested;
        }
        return null;
    }

    // ── Row VM ─────────────────────────────────────────────────────────

    /// <summary>
    /// Row-shape kinds the ListView dispatches on. <see cref="SpawnRowKind.Header"/>
    /// renders a non-clickable section divider; the others spawn either a
    /// node template or a frame action.
    /// </summary>
    public enum SpawnRowKind { Header, Pinned, Recent, Template, FrameAction }

    /// <summary>Row VM exposed to the ListView's DataTemplate.</summary>
    public sealed class SpawnRow
    {
        public SpawnRowKind Kind        { get; init; }
        public string Title             { get; init; } = string.Empty;
        /// <summary>
        /// Visual-only title rendered in the row template. Defaults to
        /// <see cref="Title"/> so untouched call sites keep showing the
        /// registry key. Standalone rows surface NodeTemplate.DisplayName
        /// here (e.g. "Reroute" instead of "Flow.Reroute") while Title
        /// remains the registry key for the spawn / commit path.
        /// </summary>
        public string DisplayTitle      { get; init; } = string.Empty;
        public string Category          { get; init; } = string.Empty;
        public string Description       { get; init; } = string.Empty;
        public List<string> Keywords    { get; init; } = new();
        // Default header brush resolved from
        // CoalDividerBrush so the coal-3 tier flows through palette
        // dividers consistently. Fallback preserves the ARGB.
        public Brush HeaderBrush        { get; init; } =
            SpawnPaletteFlyout.ResolveBrush("CoalDividerBrush",
                Color.FromArgb(0xFF, 0x3A, 0x31, 0x27));
        public string? FrameKind        { get; init; }
        /// <summary>True when the template is in <see cref="s_newTemplateTitles"/>
        /// — drives the ember NEW pill in the row template.</summary>
        public bool IsNew               { get; init; }
        public bool IsHeaderRow => Kind == SpawnRowKind.Header;
        public bool IsClickable => Kind != SpawnRowKind.Header;
        public Visibility HeaderVis    => IsHeaderRow ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ClickableVis => IsClickable ? Visibility.Visible : Visibility.Collapsed;
        public Visibility NewBadgeVis  => IsNew ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// Visibility for the per-row Category subtitle in the picker's row
        /// template. Default Visible (normal templates show their category
        /// under the title). Collapsed on standalone top-level entries
        /// (currently just Flow.Reroute) so they read as INDEPENDENT points
        /// — no category text above OR below the title.
        /// </summary>
        public bool ShowCategoryLabel { get; init; } = true;
        public Visibility CategoryLabelVis
            => ShowCategoryLabel ? Visibility.Visible : Visibility.Collapsed;

        // Pre-tokenized search pool (title +
        // category + keywords), computed once per row and cached. Pre-fix
        // MatchesTokens re-tokenized every row's static text on EVERY keystroke
        // (~150 redundant TokenizeQuery calls per filter pass over ~132 rows).
        // Lazily built so the row factories stay unchanged; the same SpawnRow
        // instances persist in _all across filter passes, so this runs once per
        // row for the flyout's lifetime.
        private HashSet<string>? _searchPool;
        public HashSet<string> SearchPool
        {
            get
            {
                if (_searchPool is not null) return _searchPool;
                var pool = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var tk in TokenizeQuery(Title))        pool.Add(tk);
                // DisplayTitle is the user-VISIBLE name (template DisplayName
                // override, e.g. "Reroute" for "Flow.Reroute"); index it too so a
                // user typing what they SEE actually matches. Pre-fix only the
                // registry Title was pooled, so DisplayName-only words never hit.
                foreach (var tk in TokenizeQuery(DisplayTitle)) pool.Add(tk);
                foreach (var tk in TokenizeQuery(Category))     pool.Add(tk);
                foreach (var k in Keywords)
                    foreach (var tk in TokenizeQuery(k)) pool.Add(tk);
                _searchPool = pool;
                return _searchPool;
            }
        }

        public static SpawnRow Header(string text) => new()
        {
            Kind         = SpawnRowKind.Header,
            Title        = text,
            DisplayTitle = text,
            Category     = string.Empty,
        };

        public static SpawnRow Template(NodeTemplate t)
        {
            bool standalone = !string.IsNullOrEmpty(t.Category)
                && string.Equals(t.Category, TopLevelStandaloneCategory, StringComparison.OrdinalIgnoreCase);
            // Title stays the registry key ("Flow.Reroute") so Commit /
            // TrackRecent / s_pinnedTitles / token-search all keep working.
            // DisplayTitle is the visual-only override the row template binds
            // its title TextBlock to — falls back to Title when no
            // DisplayName is set so untouched templates render identically.
            string displayTitle = !string.IsNullOrEmpty(t.DisplayName) ? t.DisplayName : t.Title;
            return new()
            {
                Kind         = SpawnRowKind.Template,
                Title        = t.Title,
                DisplayTitle = displayTitle,
                Category     = t.Category,
                Description  = t.Description ?? string.Empty,
                Keywords     = t.Keywords ?? new List<string>(),
                HeaderBrush  = new SolidColorBrush(Color.FromArgb(
                    t.HeaderColor.A, t.HeaderColor.R, t.HeaderColor.G, t.HeaderColor.B)),
                IsNew        = s_newTemplateTitles.Contains(t.Title),
                // Hide the category subtitle on standalone rows so they read
                // as INDEPENDENT — no "REROUTE" tag below the title.
                ShowCategoryLabel = !standalone,
            };
        }

        public static SpawnRow Pinned(SpawnRow t) => new()
        {
            Kind              = SpawnRowKind.Pinned,
            Title             = t.Title,
            DisplayTitle      = t.DisplayTitle,
            Category          = t.Category,
            Description       = t.Description,
            Keywords          = t.Keywords,
            HeaderBrush       = t.HeaderBrush,
            IsNew             = t.IsNew,
            ShowCategoryLabel = t.ShowCategoryLabel,
        };

        public static SpawnRow Recent(SpawnRow t) => new()
        {
            Kind              = SpawnRowKind.Recent,
            Title             = t.Title,
            DisplayTitle      = t.DisplayTitle,
            Category          = t.Category,
            Description       = t.Description,
            Keywords          = t.Keywords,
            HeaderBrush       = t.HeaderBrush,
            IsNew             = t.IsNew,
            ShowCategoryLabel = t.ShowCategoryLabel,
        };

        public static SpawnRow FrameAction(string frameKind, string title, string category) => new()
        {
            Kind         = SpawnRowKind.FrameAction,
            Title        = title,
            DisplayTitle = title,
            Category     = category,
            FrameKind    = frameKind,
            // Placeholder = ErrBrush tier (dark-red marker
            // for "this frame is a TODO / blocker"), comment = Ember200 tier
            // (warm amber for a benign annotation). Fallbacks match
            // the ARGB so designer-time renders stay identical.
            HeaderBrush = frameKind == "placeholder"
                ? SpawnPaletteFlyout.ResolveBrush("ErrBrush",     ArchitectCanvasPalette.SpawnPalettePlaceholderFallback)
                : SpawnPaletteFlyout.ResolveBrush("Ember200Brush", ArchitectCanvasPalette.SpawnPaletteCommentFallback),
        };
    }
}
