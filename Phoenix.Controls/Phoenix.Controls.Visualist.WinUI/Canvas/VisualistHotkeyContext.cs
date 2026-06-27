namespace Phoenix.Controls.Visualist.WinUI.Canvas;

/// <summary>
/// Visualist canvas interaction modes that drive the bottom-left
/// <see cref="HotkeyCheatsheet"/> overlay's content. Spans both the
/// LayerCanvasView (widget composition) and the WidgetGraphCanvas
/// (per-trigger node graph) since the two surfaces never display at
/// the same time — the active canvas pushes its own context in via
/// <c>HotkeyCheatsheet.SetContext</c>.
/// </summary>
/// <remarks>
/// Per <c>feedback_visualist_architect_chrome_independence.md</c>, the
/// Architect analogue (<c>Architect.WinUI.Canvas.HotkeyContext</c>) is a
/// deliberately separate enum even though the shape is similar — the two
/// pillars never share canvas paint code.
/// </remarks>
public enum VisualistHotkeyContext
{
    // ── Layer canvas (composing widgets onto a stage) ──
    LayerCanvas,
    LayerWidgetSelected,
    LayerMultiSelection,

    // ── Widget-graph canvas (per-trigger node graph) ──
    WidgetGraph,
    WidgetGraphNodeSelected,
    WidgetGraphMultiSelection,
    WidgetGraphDraggingWire,
    WidgetGraphPanning,
}
