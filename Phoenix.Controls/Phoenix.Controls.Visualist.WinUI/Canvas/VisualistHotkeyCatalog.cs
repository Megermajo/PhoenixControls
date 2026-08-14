using System.Collections.Generic;
using Phoenix.Controls.Shared.Localization;

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
/// (per-trigger node graphs). Per the chrome-independence rule this is
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

        // Chords are NEVER translated — "Ctrl+Shift+S" reads the same in every
        // language and the keyboard does not remap. Only the action description
        // beside the chord goes through Localizer. Layer-canvas rows carry the
        // visualist.canvas.hotkey.* family, widget-graph rows visualist.widget.hotkey.*,
        // so the two vocabularies stay independently translatable even where the
        // English happens to coincide ("Undo", "Delete selection", …).
        return new VisualistHotkeyEntry[]
        {
            // ── Layer canvas vocabulary ──────────────────────────────────
            new("Ctrl+Z",       Localizer.T("visualist.canvas.hotkey.undo", "Undo"),                                  AnyLayer),
            new("Ctrl+Y",       Localizer.T("visualist.canvas.hotkey.redo", "Redo"),                                  AnyLayer),
            new("Ctrl+Shift+Z", Localizer.T("visualist.canvas.hotkey.redo_alt", "Redo (alt)"),                        AnyLayer),
            new("Ctrl+S",       Localizer.T("visualist.canvas.hotkey.save", "Save layer"),                            AnyLayer),
            new("Ctrl+Shift+S", Localizer.T("visualist.canvas.hotkey.save_as", "Save layer as…"),                     AnyLayer),
            new("Ctrl+A",       Localizer.T("visualist.canvas.hotkey.select_all", "Select all widgets"),              AnyLayer),
            new("Ctrl+C",       Localizer.T("visualist.canvas.hotkey.copy", "Copy selection"),                        LayerWithSelection),
            new("Ctrl+V",       Localizer.T("visualist.canvas.hotkey.paste", "Paste"),                                AnyLayer),
            new("Ctrl+X",       Localizer.T("visualist.canvas.hotkey.cut", "Cut selection"),                          LayerWithSelection),
            new("Del",          Localizer.T("visualist.canvas.hotkey.delete", "Delete selection"),                    LayerWithSelection),
            new("Enter",        Localizer.T("visualist.canvas.hotkey.edit_widget", "Edit selected widget"),           LayerWithSelection),
            new("Arrows",       Localizer.T("visualist.canvas.hotkey.nudge_1px", "Nudge selection 1px"),              LayerWithSelection),
            new("Shift+Arrows", Localizer.T("visualist.canvas.hotkey.nudge_8px", "Nudge selection 8px"),              LayerWithSelection),
            new("Ctrl+] / Ctrl+[",   Localizer.T("visualist.canvas.hotkey.reorder_step", "Bring forward / send backward"),  LayerWithSelection),
            new("Ctrl+Shift+] / [",  Localizer.T("visualist.canvas.hotkey.reorder_ends", "Bring to front / send to back"),  LayerWithSelection),
            new("Ctrl+I",       Localizer.T("visualist.canvas.hotkey.import_media", "Import media…"),                 AnyLayer),
            new("Esc",          Localizer.T("visualist.canvas.hotkey.clear_selection", "Clear selection"),            LayerWithSelection),
            new("Ctrl+wheel",   Localizer.T("visualist.canvas.hotkey.zoom_cursor", "Cursor-anchored zoom"),           AnyLayer),
            new("MMB drag",     Localizer.T("visualist.canvas.hotkey.pan", "Pan canvas"),                             AnyLayer),

            // ── Widget-graph vocabulary ──────────────────────────────────
            new("Ctrl+Z",       Localizer.T("visualist.widget.hotkey.undo", "Undo"),                                  AnyGraph),
            new("Ctrl+Shift+Z", Localizer.T("visualist.widget.hotkey.redo", "Redo"),                                  AnyGraph),
            new("Ctrl+S",       Localizer.T("visualist.widget.hotkey.save", "Save layer"),                            AnyGraph),
            new("Ctrl+Shift+S", Localizer.T("visualist.widget.hotkey.save_as", "Save layer as…"),                     AnyGraph),
            new("Ctrl+A",       Localizer.T("visualist.widget.hotkey.select_all", "Select all nodes"),                AnyGraph),
            new("Ctrl+C",       Localizer.T("visualist.widget.hotkey.copy", "Copy node selection"),                   GraphWithSelection),
            new("Ctrl+V",       Localizer.T("visualist.widget.hotkey.paste", "Paste node selection"),                 AnyGraph),
            new("Ctrl+X",       Localizer.T("visualist.widget.hotkey.cut", "Cut node selection"),                     GraphWithSelection),
            new("Del",          Localizer.T("visualist.widget.hotkey.delete", "Delete selection"),                    GraphWithSelection),
            new("Ctrl+0",       Localizer.T("visualist.widget.hotkey.zoom_reset", "Reset zoom to 100%"),              AnyGraph),
            new("Ctrl++ / Ctrl+=", Localizer.T("visualist.widget.hotkey.zoom_in", "Zoom in"),                         AnyGraph),
            new("Ctrl+-",       Localizer.T("visualist.widget.hotkey.zoom_out", "Zoom out"),                          AnyGraph),
            new("MMB drag",     Localizer.T("visualist.widget.hotkey.pan", "Pan canvas"),                             AnyGraph),
            new("Mouse wheel",  Localizer.T("visualist.widget.hotkey.zoom_cursor", "Cursor-anchored zoom"),           AnyGraph),
            new("Alt + drag",   Localizer.T("visualist.widget.hotkey.suspend_snap", "Suspend grid snap while dragging"), GraphWithSelection),

            // ── Mode-specific pointer affordances ────────────────────────
            new("LMB drag",     Localizer.T("visualist.canvas.hotkey.move_widget", "Move selected widget"),           LayerWithSelection),
            new("Shift+LMB",    Localizer.T("visualist.canvas.hotkey.multi_toggle", "Add / remove from multi-selection"), AnyLayer),
            new("RMB",          Localizer.T("visualist.canvas.hotkey.context_menu", "Open context menu"),             AnyLayer),
            new("Shift+LMB",    Localizer.T("visualist.widget.hotkey.multi_toggle", "Add / remove node from selection"), AnyGraph),
            new("RMB",          Localizer.T("visualist.widget.hotkey.context_menu", "Open context menu"),             AnyGraph),

            // ── In-flight (drag / pan) ───────────────────────────────────
            new("LMB release",  Localizer.T("visualist.widget.hotkey.wire_drop", "Drop wire onto target socket"),     DragWire),
            new("Esc",          Localizer.T("visualist.widget.hotkey.wire_cancel", "Cancel wire drag"),               DragWire),
            new("Release MMB",  Localizer.T("visualist.widget.hotkey.stop_pan", "Stop panning"),                      PanOnly),
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
