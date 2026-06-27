using System.Collections.Generic;

namespace Phoenix.Controls.Visualist.WinUI.Canvas;

/// <summary>
/// One row in the Visualist hotkey reference. Mirrors
/// <c>Architect.WinUI.Canvas.HotkeyEntry</c> in shape but lives in
/// Visualist per the chrome-independence rule — the two pillars never
/// share canvas paint or hotkey wiring.
/// </summary>
public sealed record VisualistHotkeyEntry(
    string Combo,
    string Description,
    VisualistHotkeyContext[] Contexts);

/// <summary>
/// Visualist canvas hotkey catalog — single source of truth for the
/// chord vocabulary surfaced inside both <c>LayerCanvasView</c> (widget
/// composition) and <c>WidgetGraphCanvas</c> / <c>WidgetEditorView</c>
/// (per-trigger node graphs). Per the chrome-independence rule
/// (<c>feedback_visualist_architect_chrome_independence.md</c>) this is
/// a deliberately separate copy from Architect's
/// <c>ArchitectHotkeyCatalog</c>, even though the data shape is the same.
/// </summary>
public static class VisualistHotkeyCatalog
{
    private static readonly VisualistHotkeyEntry[] _all = BuildEntries();

    private static VisualistHotkeyEntry[] BuildEntries()
    {
        VisualistHotkeyContext[] AnyLayer =
        {
            VisualistHotkeyContext.LayerCanvas,
            VisualistHotkeyContext.LayerWidgetSelected,
            VisualistHotkeyContext.LayerMultiSelection,
        };
        VisualistHotkeyContext[] LayerWithSelection =
        {
            VisualistHotkeyContext.LayerWidgetSelected,
            VisualistHotkeyContext.LayerMultiSelection,
        };
        VisualistHotkeyContext[] AnyGraph =
        {
            VisualistHotkeyContext.WidgetGraph,
            VisualistHotkeyContext.WidgetGraphNodeSelected,
            VisualistHotkeyContext.WidgetGraphMultiSelection,
        };
        VisualistHotkeyContext[] GraphWithSelection =
        {
            VisualistHotkeyContext.WidgetGraphNodeSelected,
            VisualistHotkeyContext.WidgetGraphMultiSelection,
        };
        VisualistHotkeyContext[] LayerIdle  = { VisualistHotkeyContext.LayerCanvas };
        VisualistHotkeyContext[] GraphIdle  = { VisualistHotkeyContext.WidgetGraph };
        VisualistHotkeyContext[] DragWire   = { VisualistHotkeyContext.WidgetGraphDraggingWire };
        VisualistHotkeyContext[] PanOnly    = { VisualistHotkeyContext.WidgetGraphPanning };

        return new VisualistHotkeyEntry[]
        {
            // ── Layer canvas vocabulary ──────────────────────────────────
            new("Ctrl+Z",       "Undo",                                  AnyLayer),
            new("Ctrl+Y",       "Redo",                                  AnyLayer),
            new("Ctrl+Shift+Z", "Redo (alt)",                            AnyLayer),
            new("Ctrl+S",       "Save layer",                            AnyLayer),
            new("Ctrl+Shift+S", "Save layer as…",                        AnyLayer),
            new("Ctrl+A",       "Select all widgets",                    AnyLayer),
            new("Ctrl+C",       "Copy selection",                        LayerWithSelection),
            new("Ctrl+V",       "Paste",                                 AnyLayer),
            new("Ctrl+X",       "Cut selection",                         LayerWithSelection),
            new("Del",          "Delete selection",                      LayerWithSelection),

            // ── Widget-graph vocabulary ──────────────────────────────────
            new("Ctrl+Z",       "Undo",                                  AnyGraph),
            new("Ctrl+Shift+Z", "Redo",                                  AnyGraph),
            new("Ctrl+S",       "Save layer",                            AnyGraph),
            new("Ctrl+Shift+S", "Save layer as…",                        AnyGraph),
            new("Ctrl+A",       "Select all nodes",                      AnyGraph),
            new("Ctrl+C",       "Copy node selection",                   GraphWithSelection),
            new("Ctrl+V",       "Paste node selection",                  AnyGraph),
            new("Ctrl+X",       "Cut node selection",                    GraphWithSelection),
            new("Del",          "Delete selection",                      GraphWithSelection),
            new("Ctrl+0",       "Reset zoom to 100%",                    AnyGraph),
            new("Ctrl++ / Ctrl+=", "Zoom in",                            AnyGraph),
            new("Ctrl+-",       "Zoom out",                              AnyGraph),
            new("MMB drag",     "Pan canvas",                            AnyGraph),
            new("Mouse wheel",  "Cursor-anchored zoom",                  AnyGraph),
            new("Alt + drag",   "Suspend grid snap while dragging",      GraphWithSelection),

            // ── Mode-specific pointer affordances ────────────────────────
            new("LMB drag",     "Move selected widget",                  LayerWithSelection),
            new("Shift+LMB",    "Add / remove from multi-selection",     AnyLayer),
            new("RMB",          "Open context menu",                     AnyLayer),
            new("Shift+LMB",    "Add / remove node from selection",      AnyGraph),
            new("RMB",          "Open context menu",                     AnyGraph),

            // ── In-flight (drag / pan) ───────────────────────────────────
            new("LMB release",  "Drop wire onto target socket",          DragWire),
            new("Esc",          "Cancel wire drag",                      DragWire),
            new("Release MMB",  "Stop panning",                          PanOnly),
        };
    }

    /// <summary>
    /// Chords relevant to the supplied context, ordered as authored above.
    /// The cheatsheet caps how many rows it renders; this preserves order
    /// so the cap is deterministic.
    /// </summary>
    public static IReadOnlyList<VisualistHotkeyEntry> GetForContext(VisualistHotkeyContext context)
    {
        var result = new List<VisualistHotkeyEntry>(16);
        foreach (var entry in _all)
        {
            foreach (var c in entry.Contexts)
            {
                if (c == context) { result.Add(entry); break; }
            }
        }
        return result;
    }
}
