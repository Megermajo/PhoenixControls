using System.Collections.Generic;
using System.Linq;
using Phoenix.Controls.Shared.Localization;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

/// <summary>
/// One row in the Architect hotkey reference. Carries the chord glyph (the
/// thing the user types), a short verb-led description, and the
/// <see cref="HotkeyContext"/> set the chord is most relevant in. The
/// <see cref="ReferenceSection"/> string drives grouping in the full
/// <c>KeyboardShortcutsDialog</c> reference view.
/// </summary>
public sealed record HotkeyEntry(
    string Combo,
    string Description,
    string ReferenceSection,
    HotkeyContext[] Contexts);

/// <summary>
/// Single source of truth for the Architect chord vocabulary. Drives BOTH
/// the always-visible bottom-left <c>HotkeyCheatsheet</c> overlay AND the
/// full-reference <c>KeyboardShortcutsDialog</c> — keeping them in lockstep
/// so future chord changes can't drift between the two surfaces.
/// <para/>
/// Edits to canvas keyboard handlers should mirror here in the same
/// commit; <c>KeyboardShortcutsDialogTests</c> only checks that the dialog
/// renders, not that each entry corresponds to a live binding, so the
/// catalog stays a hand-curated contract.
/// </summary>
public static class ArchitectHotkeyCatalog
{
    // ─── Section names (mirrored in the modal reference view) ──────────────

    // `static readonly` (not `const`): the display string resolves through
    // Localizer at first touch. Every consumer — the cheatsheet overlay, the
    // KeyboardShortcutsDialog allow-lists, GroupedBySection's bucket keys —
    // reads these same fields, so the grouping/allow-list identity holds
    // whatever language the bundle resolves to.
    public static readonly string SectionEdit                 = Localizer.T("architect.canvas.hotkey.section.edit", "Edit");
    public static readonly string SectionFile                 = Localizer.T("architect.canvas.hotkey.section.file", "File");
    public static readonly string SectionNavigateView         = Localizer.T("architect.canvas.hotkey.section.navigate_view", "Navigate / View");
    public static readonly string SectionSpawnPalette         = Localizer.T("architect.canvas.hotkey.section.spawn_palette", "Spawn palette");
    public static readonly string SectionFind                 = Localizer.T("architect.canvas.hotkey.section.find", "Find");
    public static readonly string SectionQuickKeySpawn        = Localizer.T("architect.canvas.hotkey.section.quick_key_spawn", "Quick-key spawn (hold + click empty canvas)");
    public static readonly string SectionBookmarks            = Localizer.T("architect.canvas.hotkey.section.bookmarks", "Bookmarks");
    public static readonly string SectionDocumentation        = Localizer.T("architect.canvas.hotkey.section.documentation", "Documentation");
    public static readonly string SectionRetired              = Localizer.T("architect.canvas.hotkey.section.retired", "Retired (intentional)");
    public static readonly string SectionView                 = Localizer.T("architect.canvas.hotkey.section.view", "View");
    public static readonly string SectionHelp                 = Localizer.T("architect.canvas.hotkey.section.help", "Help");

    /// <summary>
    /// The full chord catalog. Order here is the order the modal reference
    /// view renders each section; the cheatsheet overlay further filters
    /// by <see cref="HotkeyContext"/> at render time.
    /// </summary>
    private static readonly HotkeyEntry[] _all = BuildEntries();

    /// <summary>
    /// Localized chord DESCRIPTION. Short local name so the table below stays
    /// readable at one entry per line; <paramref name="english"/> is the
    /// authoritative fallback whenever the bundle has no entry.
    /// </summary>
    private static string D(string slug, string english)
        => Localizer.T("architect.canvas.hotkey." + slug, english);

    private static HotkeyEntry[] BuildEntries()
    {
        // Convenience locals for the most common context tuples — keeps
        // the table readable and the array allocations cached.
        HotkeyContext[] AnyCanvas   = { HotkeyContext.Canvas, HotkeyContext.NodeSelected, HotkeyContext.MultiSelection, HotkeyContext.WireSelected, HotkeyContext.FrameSelected };
        HotkeyContext[] AnySelected = { HotkeyContext.NodeSelected, HotkeyContext.MultiSelection, HotkeyContext.WireSelected, HotkeyContext.FrameSelected };
        HotkeyContext[] NodesOnly   = { HotkeyContext.NodeSelected, HotkeyContext.MultiSelection };
        HotkeyContext[] CanvasOnly  = { HotkeyContext.Canvas };
        HotkeyContext[] DragWire    = { HotkeyContext.DraggingWire };
        HotkeyContext[] PanOnly     = { HotkeyContext.Panning };
        HotkeyContext[] EditOnly    = { HotkeyContext.TextEditing };
        HotkeyContext[] FrameOnly   = { HotkeyContext.FrameSelected };

        return new HotkeyEntry[]
        {
            // ── Edit ──────────────────────────────────────────────────────
            // The Combo column (chords) is NEVER localized — a chord is what
            // the keyboard emits. The Description column is.
            new("Ctrl+Z",        D("undo", "Undo"),                       SectionEdit, AnyCanvas),
            new("Ctrl+Y",        D("redo", "Redo"),                       SectionEdit, AnyCanvas),
            new("Ctrl+Shift+Z",  D("redo_alt", "Redo (alt)"),             SectionEdit, AnyCanvas),
            new("Ctrl+C",        D("copy", "Copy selection"),             SectionEdit, NodesOnly),
            new("Ctrl+V",        D("paste", "Paste"),                     SectionEdit, AnyCanvas),
            new("Ctrl+X",        D("cut", "Cut selection"),               SectionEdit, NodesOnly),
            new("Ctrl+D",        D("duplicate", "Duplicate selection"),   SectionEdit, NodesOnly),
            new("Ctrl+A",        D("select_all", "Select all nodes"),     SectionEdit, AnyCanvas),
            new("Ctrl+G",        D("group_macro", "Group selection into Macro"), SectionEdit, new[] { HotkeyContext.MultiSelection }),
            new("Ctrl+L",        D("auto_format", "Auto-format graph"),   SectionEdit, AnyCanvas),
            new("Del",           D("delete", "Delete selection"),         SectionEdit, AnySelected),
            new("F2",            D("rename_frame", "Rename frame"),       SectionEdit, FrameOnly),
            new("C",             D("comment_frame", "Comment frame — wraps selection, else empty at view centre"), SectionEdit, CanvasOnly),
            new("Arrow keys",    D("nudge", "Nudge selection 1 px (Shift = 10 px)"), SectionEdit, NodesOnly),

            // ── File ──────────────────────────────────────────────────────
            new("Ctrl+N",        D("new_graph", "New Graph…"),            SectionFile, AnyCanvas),
            new("Ctrl+O",        D("open", "Open…"),                      SectionFile, AnyCanvas),
            new("Ctrl+S",        D("save", "Save"),                       SectionFile, AnyCanvas),
            new("Ctrl+Shift+S",  D("save_as", "Save As…"),                SectionFile, AnyCanvas),
            new("Ctrl+W",        D("close_window", "Close window (sibling windows only)"), SectionFile, AnyCanvas),

            // ── Navigate / View ───────────────────────────────────────────
            new("F",             D("frame_selection", "Frame selection (UE-Blueprints idiom)"), SectionNavigateView, AnyCanvas),
            // the wheel chords were undocumented.
            new("Ctrl+Wheel",    D("zoom_wheel", "Zoom (cursor-anchored)"), SectionNavigateView, AnyCanvas),
            new("Wheel",         D("pan_vertical", "Pan vertically"),     SectionNavigateView, AnyCanvas),
            new("Shift+Wheel",   D("pan_horizontal", "Pan horizontally"), SectionNavigateView, AnyCanvas),
            new("Home",          D("zoom_fit", "Zoom to fit graph"),      SectionNavigateView, AnyCanvas),
            new("Ctrl+0",        D("zoom_reset", "Reset zoom to 100%"),   SectionNavigateView, AnyCanvas),
            new("Ctrl++ / Ctrl+=", D("zoom_in", "Zoom in"),               SectionNavigateView, AnyCanvas),
            new("Ctrl+-",        D("zoom_out", "Zoom out"),               SectionNavigateView, AnyCanvas),
            new("Ctrl+Shift+= / Ctrl+Shift+-", D("zoom_fine", "Fine-step zoom (×1.025)"), SectionNavigateView, AnyCanvas),
            new("MMB drag",      D("pan_canvas", "Pan canvas"),           SectionNavigateView, AnyCanvas),
            new("Esc",           D("escape_cancel", "Cancel drag / disarm quick-key / clear selection"), SectionNavigateView, new[] { HotkeyContext.NodeSelected, HotkeyContext.MultiSelection, HotkeyContext.WireSelected, HotkeyContext.FrameSelected, HotkeyContext.DraggingWire, HotkeyContext.Panning }),

            // ── Spawn palette ─────────────────────────────────────────────
            new("Space",         D("spawn_palette", "Open spawn palette at view centre"), SectionSpawnPalette, CanvasOnly),
            new("Ctrl+Space",    D("spawn_palette_alias", "Open spawn palette (Blueprints alias)"), SectionSpawnPalette, CanvasOnly),

            // ── Find ──────────────────────────────────────────────────────
            new("Ctrl+F",        D("find_open", "Open Find Node flyout"), SectionFind, AnyCanvas),
            new("F3",            D("find_next", "Jump to next Find Node match"), SectionFind, AnyCanvas),
            new("Shift+F3",      D("find_prev", "Jump to previous Find Node match"), SectionFind, AnyCanvas),

            // ── Quick-key spawn (catalog-only; cheatsheet skips these to stay compact) ──
            // Bare node type names stay English everywhere — they are the .phxg
            // vocabulary, not prose. Only the parenthesised gloss on the digit
            // row carries translatable text.
            new("B",             "Logic.If",                              SectionQuickKeySpawn, CanvasOnly),
            new("S",             "Logic.Sequence",                        SectionQuickKeySpawn, CanvasOnly),
            new("D",             "Flow.Delay",                            SectionQuickKeySpawn, CanvasOnly),
            new("O",             "Flow.DoOnce",                           SectionQuickKeySpawn, CanvasOnly),
            new("N",             "Flow.DoN",                              SectionQuickKeySpawn, CanvasOnly),
            new("0 … 9",         D("quickkey_value_int", "Value.Int (digit pre-filled)"), SectionQuickKeySpawn, CanvasOnly),

            // ── Bookmarks ─────────────────────────────────────────────────
            new("Ctrl+1 … Ctrl+9", D("bookmark_store", "Store current pan + zoom in slot"), SectionBookmarks, AnyCanvas),
            new("Alt+1 … Alt+9",   D("bookmark_recall", "Recall pan + zoom from slot"), SectionBookmarks, AnyCanvas),

            // ── Documentation ─────────────────────────────────────────────
            new("F1",            D("docs_node", "Open documentation for selected node"), SectionDocumentation, NodesOnly),
            new("F1 (no selection)", D("docs_shortcuts", "Open Keyboard Shortcuts dialog"), SectionDocumentation, CanvasOnly),
            new("F4",            D("toggle_inspector", "Toggle Inspector panel"), SectionDocumentation, AnyCanvas),

            // ── In-flight (drag / pan / text-edit) ────────────────────────
            new("LMB release",   D("wire_drop", "Drop wire onto target socket"), SectionEdit, DragWire),
            new("Y",             D("wire_reroute", "Drop reroute knot at cursor + continue"), SectionEdit, DragWire),
            new("Esc",           D("wire_cancel", "Cancel wire drag"),    SectionEdit, DragWire),
            new("Release MMB",   D("pan_stop", "Stop panning"),           SectionEdit, PanOnly),
            new("Enter",         D("edit_commit", "Commit value"),        SectionEdit, EditOnly),
            new("Esc",           D("edit_cancel", "Cancel inline edit"),  SectionEdit, EditOnly),

            // ── Retired (intentional — keep visible in the full reference) ──
            new("Space (pan)",   D("retired_space", "Removed — Space now opens the spawn palette"), SectionRetired, System.Array.Empty<HotkeyContext>()),
            new("Tab (spawn)",   D("retired_tab", "Removed — Tab now traverses focus for accessibility"), SectionRetired, System.Array.Empty<HotkeyContext>()),
        };
    }

    /// <summary>
    /// Chords relevant to the supplied context, ordered as authored in the
    /// master table. The cheatsheet caps how many it renders; this method
    /// preserves order so the cap is deterministic.
    /// </summary>
    public static IReadOnlyList<HotkeyEntry> GetForContext(HotkeyContext context)
    {
        var result = new List<HotkeyEntry>(16);
        foreach (var entry in _all)
        {
            foreach (var c in entry.Contexts)
            {
                if (c == context) { result.Add(entry); break; }
            }
        }
        return result;
    }

    /// <summary>
    /// All entries grouped by their <see cref="HotkeyEntry.ReferenceSection"/>,
    /// in authored order. Used by <see cref="Dialogs.KeyboardShortcutsDialog"/>
    /// to render the full reference. Sections appear in the order they first
    /// occur in the master table, mirroring the legacy hand-curated layout.
    /// </summary>
    public static IReadOnlyList<(string Section, IReadOnlyList<HotkeyEntry> Entries)> GroupedBySection(
        IEnumerable<string>? sectionAllowList = null)
    {
        var allow = sectionAllowList is null
            ? null
            : new HashSet<string>(sectionAllowList);

        var order  = new List<string>(8);
        var bucket = new Dictionary<string, List<HotkeyEntry>>();
        foreach (var entry in _all)
        {
            if (allow is not null && !allow.Contains(entry.ReferenceSection)) continue;
            if (!bucket.TryGetValue(entry.ReferenceSection, out var list))
            {
                list = new List<HotkeyEntry>(8);
                bucket[entry.ReferenceSection] = list;
                order.Add(entry.ReferenceSection);
            }
            list.Add(entry);
        }
        return order
            .Select(s => (s, (IReadOnlyList<HotkeyEntry>)bucket[s]))
            .ToList();
    }
}
