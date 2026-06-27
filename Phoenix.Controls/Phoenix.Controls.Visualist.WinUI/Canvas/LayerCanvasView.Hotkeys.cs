namespace Phoenix.Controls.Visualist.WinUI.Canvas;

/// <summary>
/// LayerCanvasView partial — feeds canvas selection state into the
/// bottom-left <see cref="HotkeyCheatsheet"/> overlay. Independent of the
/// Architect-side wiring per the chrome-independence rule.
/// </summary>
public sealed partial class LayerCanvasView
{
    private VisualistHotkeyContext _lastPushedHotkeyContext = VisualistHotkeyContext.LayerCanvas;

    /// <summary>
    /// Re-derive the canvas's context from the current selection state and
    /// push it to the overlay. Cheap to call (no allocations on the
    /// stable-context path). Safe before the cheatsheet child has been
    /// realised — null-guards on first paint.
    /// </summary>
    internal void RecomputeHotkeyContext()
    {
        if (HotkeyCheatsheetOverlay is null) return;

        VisualistHotkeyContext next;
        int multi = _multiSelected.Count;
        bool hasPrimary = _vm?.SelectedWidget is not null;
        if (multi > 1)
        {
            next = VisualistHotkeyContext.LayerMultiSelection;
        }
        else if (multi == 1 || hasPrimary)
        {
            next = VisualistHotkeyContext.LayerWidgetSelected;
        }
        else
        {
            next = VisualistHotkeyContext.LayerCanvas;
        }

        if (next == _lastPushedHotkeyContext) return;
        _lastPushedHotkeyContext = next;
        HotkeyCheatsheetOverlay.SetContext(next);
    }
}
