using System;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

/// <summary>
/// LogicCanvasView partial — owns the wiring between the canvas's
/// selection / drag / inline-edit state and the bottom-left
/// <see cref="HotkeyCheatsheet"/> overlay. Keeping the bookkeeping in its
/// own file means the main canvas code-behind doesn't grow another
/// concern; the cheatsheet itself is a passive UserControl that just
/// renders whichever <see cref="HotkeyContext"/> we hand it.
/// </summary>
public sealed partial class LogicCanvasView
{
    /// <summary>
    /// Optional override the in-flight pointer pipeline pushes in for
    /// transient contexts (DraggingWire / Panning). Selection-derived
    /// contexts (Canvas / NodeSelected / etc.) are still computed from
    /// <see cref="_vm"/> when this is null. Cleared on drag end.
    /// </summary>
    private HotkeyContext? _transientHotkeyContext;

    /// <summary>
    /// Latest context we pushed into the cheatsheet — guards against
    /// noisy re-renders when SelectionChanged fires for an unchanged
    /// selection shape.
    /// </summary>
    private HotkeyContext _lastPushedHotkeyContext = HotkeyContext.Canvas;

    /// <summary>
    /// Compute the current <see cref="HotkeyContext"/> from the canvas's
    /// selection + drag state and forward it to the overlay. Cheap to call
    /// (no allocations on the stable-context path). Safe to invoke before
    /// the cheatsheet child has been realised (no-ops if the field is
    /// still null — XAML build order materialises it on the first
    /// arrange).
    /// </summary>
    internal void RecomputeHotkeyContext()
    {
        if (HotkeyCheatsheetOverlay is null) return;

        HotkeyContext next;
        if (_transientHotkeyContext is HotkeyContext transient)
        {
            next = transient;
        }
        else if (_vm is null)
        {
            next = HotkeyContext.Canvas;
        }
        else
        {
            int nodes  = _vm.SelectedNodes .Count;
            int wires  = _vm.SelectedLinks .Count;
            int frames = _vm.SelectedFrames.Count;
            int total  = nodes + wires + frames;
            if (total > 1)
            {
                next = HotkeyContext.MultiSelection;
            }
            else if (nodes == 1)
            {
                next = HotkeyContext.NodeSelected;
            }
            else if (wires == 1)
            {
                next = HotkeyContext.WireSelected;
            }
            else if (frames == 1)
            {
                next = HotkeyContext.FrameSelected;
            }
            else if (_vm.Selection is Phoenix.Controls.Architect.WinUI.Canvas.NodeViewModel)
            {
                // Selection set to a single primary node without the multi-
                // collection being populated yet (happens immediately on
                // single-click before SetMultiSelection runs). Treat as
                // NodeSelected so the cheatsheet doesn't blink Canvas.
                next = HotkeyContext.NodeSelected;
            }
            else if (_vm.Selection is Phoenix.Controls.Architect.WinUI.Canvas.LinkViewModel)
            {
                next = HotkeyContext.WireSelected;
            }
            else
            {
                next = HotkeyContext.Canvas;
            }
        }

        if (next == _lastPushedHotkeyContext) return;
        _lastPushedHotkeyContext = next;
        HotkeyCheatsheetOverlay.SetContext(next);
    }

    /// <summary>
    /// Push a transient context (DraggingWire / Panning) from the pointer
    /// pipeline. Pair every call with <see cref="ClearTransientHotkeyContext"/>
    /// on drag end so the cheatsheet falls back to the selection-derived
    /// context.
    /// </summary>
    internal void SetTransientHotkeyContext(HotkeyContext context)
    {
        _transientHotkeyContext = context;
        RecomputeHotkeyContext();
    }

    /// <summary>
    /// Drop the in-flight context override and let the next recompute
    /// derive the context from the selection state instead.
    /// </summary>
    internal void ClearTransientHotkeyContext()
    {
        if (_transientHotkeyContext is null) return;
        _transientHotkeyContext = null;
        RecomputeHotkeyContext();
    }

    /// <summary>
    /// SelectionChanged handler — fired by the VM whenever any of the
    /// Selection / SelectedNodes / SelectedLinks / SelectedFrames surface
    /// mutates. Drives the cheatsheet from the "what's selected" half of
    /// the context model.
    /// </summary>
    internal void OnVmSelectionChangedForHotkeys(object? sender, EventArgs e)
        => RecomputeHotkeyContext();
}
