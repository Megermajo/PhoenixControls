using System.Collections.Generic;
using System.Linq;

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

    public const string SectionEdit                 = "Edit";
    public const string SectionFile                 = "File";
    public const string SectionNavigateView         = "Navigate / View";
    public const string SectionSpawnPalette         = "Spawn palette";
    public const string SectionFind                 = "Find";
    public const string SectionQuickKeySpawn        = "Quick-key spawn (hold + click empty canvas)";
    public const string SectionBookmarks            = "Bookmarks";
    public const string SectionDocumentation        = "Documentation";
    public const string SectionRetired              = "Retired (intentional)";
    public const string SectionView                 = "View";
    public const string SectionHelp                 = "Help";

    /// <summary>
    /// The full chord catalog. Order here is the order the modal reference
    /// view renders each section; the cheatsheet overlay further filters
    /// by <see cref="HotkeyContext"/> at render time.
    /// </summary>
    private static readonly HotkeyEntry[] _all = BuildEntries();

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
            new("Ctrl+Z",        "Undo",                                  SectionEdit, AnyCanvas),
            new("Ctrl+Y",        "Redo",                                  SectionEdit, AnyCanvas),
            new("Ctrl+Shift+Z",  "Redo (alt)",                            SectionEdit, AnyCanvas),
            new("Ctrl+C",        "Copy selection",                        SectionEdit, NodesOnly),
            new("Ctrl+V",        "Paste",                                 SectionEdit, AnyCanvas),
            new("Ctrl+X",        "Cut selection",                         SectionEdit, NodesOnly),
            new("Ctrl+D",        "Duplicate selection",                   SectionEdit, NodesOnly),
            new("Ctrl+A",        "Select all nodes",                      SectionEdit, AnyCanvas),
            new("Ctrl+G",        "Group selection into Macro",            SectionEdit, new[] { HotkeyContext.MultiSelection }),
            new("Ctrl+L",        "Auto-format graph",                     SectionEdit, AnyCanvas),
            new("Del",           "Delete selection",                      SectionEdit, AnySelected),
            new("F2",            "Rename frame",                          SectionEdit, FrameOnly),
            new("C",             "Comment frame — wraps selection, else empty at view centre", SectionEdit, CanvasOnly),
            new("Arrow keys",    "Nudge selection 1 px (Shift = 10 px)",  SectionEdit, NodesOnly),

            // ── File ──────────────────────────────────────────────────────
            new("Ctrl+N",        "New Graph…",                            SectionFile, AnyCanvas),
            new("Ctrl+O",        "Open…",                                 SectionFile, AnyCanvas),
            new("Ctrl+S",        "Save",                                  SectionFile, AnyCanvas),
            new("Ctrl+Shift+S",  "Save As…",                              SectionFile, AnyCanvas),
            new("Ctrl+W",        "Close window (sibling windows only)",   SectionFile, AnyCanvas),

            // ── Navigate / View ───────────────────────────────────────────
            new("F",             "Frame selection (UE-Blueprints idiom)", SectionNavigateView, AnyCanvas),
            // the wheel chords were undocumented.
            new("Ctrl+Wheel",    "Zoom (cursor-anchored)",                SectionNavigateView, AnyCanvas),
            new("Wheel",         "Pan vertically",                        SectionNavigateView, AnyCanvas),
            new("Shift+Wheel",   "Pan horizontally",                      SectionNavigateView, AnyCanvas),
            new("Home",          "Zoom to fit graph",                     SectionNavigateView, AnyCanvas),
            new("Ctrl+0",        "Reset zoom to 100%",                    SectionNavigateView, AnyCanvas),
            new("Ctrl++ / Ctrl+=", "Zoom in",                             SectionNavigateView, AnyCanvas),
            new("Ctrl+-",        "Zoom out",                              SectionNavigateView, AnyCanvas),
            new("Ctrl+Shift+= / Ctrl+Shift+-", "Fine-step zoom (×1.025)", SectionNavigateView, AnyCanvas),
            new("MMB drag",      "Pan canvas",                            SectionNavigateView, AnyCanvas),
            new("Esc",           "Cancel drag / disarm quick-key / clear selection", SectionNavigateView, new[] { HotkeyContext.NodeSelected, HotkeyContext.MultiSelection, HotkeyContext.WireSelected, HotkeyContext.FrameSelected, HotkeyContext.DraggingWire, HotkeyContext.Panning }),

            // ── Spawn palette ─────────────────────────────────────────────
            new("Space",         "Open spawn palette at view centre",     SectionSpawnPalette, CanvasOnly),
            new("Ctrl+Space",    "Open spawn palette (Blueprints alias)", SectionSpawnPalette, CanvasOnly),

            // ── Find ──────────────────────────────────────────────────────
            new("Ctrl+F",        "Open Find Node flyout",                 SectionFind, AnyCanvas),
            new("F3",            "Jump to next Find Node match",          SectionFind, AnyCanvas),
            new("Shift+F3",      "Jump to previous Find Node match",      SectionFind, AnyCanvas),

            // ── Quick-key spawn (catalog-only; cheatsheet skips these to stay compact) ──
            new("B",             "Logic.If",                              SectionQuickKeySpawn, CanvasOnly),
            new("S",             "Logic.Sequence",                        SectionQuickKeySpawn, CanvasOnly),
            new("D",             "Flow.Delay",                            SectionQuickKeySpawn, CanvasOnly),
            new("O",             "Flow.DoOnce",                           SectionQuickKeySpawn, CanvasOnly),
            new("N",             "Flow.DoN",                              SectionQuickKeySpawn, CanvasOnly),
            new("0 … 9",         "Value.Int (digit pre-filled)",          SectionQuickKeySpawn, CanvasOnly),

            // ── Bookmarks ─────────────────────────────────────────────────
            new("Ctrl+1 … Ctrl+9", "Store current pan + zoom in slot",    SectionBookmarks, AnyCanvas),
            new("Alt+1 … Alt+9",   "Recall pan + zoom from slot",         SectionBookmarks, AnyCanvas),

            // ── Documentation ─────────────────────────────────────────────
            new("F1",            "Open documentation for selected node",  SectionDocumentation, NodesOnly),
            new("F1 (no selection)", "Open Keyboard Shortcuts dialog",    SectionDocumentation, CanvasOnly),
            new("F4",            "Toggle Inspector panel",                SectionDocumentation, AnyCanvas),

            // ── In-flight (drag / pan / text-edit) ────────────────────────
            new("LMB release",   "Drop wire onto target socket",          SectionEdit, DragWire),
            new("Y",             "Drop reroute knot at cursor + continue", SectionEdit, DragWire),
            new("Esc",           "Cancel wire drag",                      SectionEdit, DragWire),
            new("Release MMB",   "Stop panning",                          SectionEdit, PanOnly),
            new("Enter",         "Commit value",                          SectionEdit, EditOnly),
            new("Esc",           "Cancel inline edit",                    SectionEdit, EditOnly),

            // ── Retired (intentional — keep visible in the full reference) ──
            new("Space (pan)",   "Removed — Space now opens the spawn palette", SectionRetired, System.Array.Empty<HotkeyContext>()),
            new("Tab (spawn)",   "Removed — Tab now traverses focus for accessibility", SectionRetired, System.Array.Empty<HotkeyContext>()),
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
