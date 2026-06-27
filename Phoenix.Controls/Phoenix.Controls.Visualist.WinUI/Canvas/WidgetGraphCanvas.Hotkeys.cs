namespace Phoenix.Controls.Visualist.WinUI.Canvas;

/// <summary>
/// WidgetGraphCanvas partial — feeds canvas selection / drag / pan state
/// into the bottom-left <see cref="HotkeyCheatsheet"/> overlay.
/// Independent of the LayerCanvasView wiring per the chrome-independence
/// rule.
/// </summary>
public sealed partial class WidgetGraphCanvas
{
    private VisualistHotkeyContext? _transientHotkeyContext;
    private VisualistHotkeyContext _lastPushedHotkeyContext = VisualistHotkeyContext.WidgetGraph;

    /// <summary>
    /// Re-derive the canvas's context from selection + transient drag /
    /// pan state and push it to the overlay.
    /// </summary>
    internal void RecomputeHotkeyContext()
    {
        if (HotkeyCheatsheetOverlay is null) return;

        VisualistHotkeyContext next;
        if (_transientHotkeyContext is VisualistHotkeyContext transient)
        {
            next = transient;
        }
        else
        {
            int count = _selectedNodes.Count;
            if (count > 1)
                next = VisualistHotkeyContext.WidgetGraphMultiSelection;
            else if (count == 1 || _selectedNode is not null)
                next = VisualistHotkeyContext.WidgetGraphNodeSelected;
            else
                next = VisualistHotkeyContext.WidgetGraph;
        }

        if (next == _lastPushedHotkeyContext) return;
        _lastPushedHotkeyContext = next;
        HotkeyCheatsheetOverlay.SetContext(next);
    }

    /// <summary>
    /// Push a transient context (DraggingWire / Panning) from the pointer
    /// pipeline. Pair every call with <see cref="ClearTransientHotkeyContext"/>
    /// on drag end so the cheatsheet falls back to selection-derived state.
    /// </summary>
    internal void SetTransientHotkeyContext(VisualistHotkeyContext context)
    {
        _transientHotkeyContext = context;
        RecomputeHotkeyContext();
    }

    internal void ClearTransientHotkeyContext()
    {
        if (_transientHotkeyContext is null) return;
        _transientHotkeyContext = null;
        RecomputeHotkeyContext();
    }
}
