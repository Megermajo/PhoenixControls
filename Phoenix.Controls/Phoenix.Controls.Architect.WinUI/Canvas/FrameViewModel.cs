using Phoenix.Controls.Architect.WinUI.ViewModels;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

/// <summary>
/// Which edge / corner of a frame the pointer is dragging
/// during a FrameResize gesture. Earlier the canvas only honoured the
/// bottom-right corner handle; pre-T15 WinForms Architect allowed all four
/// edges + all four corners, so this enum re-introduces the missing affordances
/// and lets the pointer code branch on a single value instead of N booleans.
/// </summary>
public enum FrameResizeEdge
{
    None,
    Left,
    Right,
    Top,
    Bottom,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

// Comment-frame view-model. Wraps a Frame; exposes binding-friendly
// X/Y/Width/Height plus a hex color string for the WinUI brushes. Selection
// halo lives on the parent canvas selection like nodes/links.
public sealed class FrameViewModel : ObservableObject
{
    private readonly Frame _frame;
    private bool _isSelected;

    public FrameViewModel(Frame frame) { _frame = frame; }

    public Frame Model => _frame;

    public string Id    => _frame.Id;
    public string Label
    {
        // Placeholder frames render their label with a "// " prefix
        // to visually distinguish them from comment frames — matches the
        // WinForms baseline Canvas.PaintBackground.cs placeholder rendering.
        get => _frame.IsPlaceholder ? $"// {_frame.Label ?? string.Empty}" : (_frame.Label ?? string.Empty);
        set
        {
            // The getter prepends "// " for placeholder frames as a display-only
            // marker. A rename UI that pre-populates its textbox from this getter
            // (or any other round-trip) could feed the prefixed string back in;
            // strip a single leading "// " for placeholder frames so the stored
            // model Label never accumulates the marker.
            var incoming = value ?? string.Empty;
            if (_frame.IsPlaceholder && incoming.StartsWith("// ", System.StringComparison.Ordinal))
                incoming = incoming.Substring(3);
            if ((_frame.Label ?? string.Empty) == incoming) return;
            _frame.Label = incoming;
            OnPropertyChanged();
        }
    }

    public double X      => _frame.Bounds.X;
    public double Y      => _frame.Bounds.Y;
    public double Width  => _frame.Bounds.Width;
    public double Height => _frame.Bounds.Height;

    public string FrameColorHex => "#" +
        _frame.FrameColor.R.ToString("X2") +
        _frame.FrameColor.G.ToString("X2") +
        _frame.FrameColor.B.ToString("X2");

    /// <summary>Soft fill — same hex as the border but at 24/255 opacity.</summary>
    public string FrameFillHex => "#18" +
        _frame.FrameColor.R.ToString("X2") +
        _frame.FrameColor.G.ToString("X2") +
        _frame.FrameColor.B.ToString("X2");

    public bool IsPlaceholder => _frame.IsPlaceholder;

    /// <summary>
    /// Per-frame z-order within the FrameLayer. Wraps
    /// <see cref="Frame.ZOrder"/> so changes raise PropertyChanged and the
    /// canvas can re-apply Canvas.ZIndex without rebuilding the frame view.
    /// </summary>
    public int ZOrder
    {
        get => _frame.ZOrder;
        set
        {
            if (_frame.ZOrder == value) return;
            _frame.ZOrder = value;
            OnPropertyChanged();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    /// <summary>
    /// Restore exact bounds — used by the deferred
    /// frame-undo commit/revert to roll a resize gesture back to its captured
    /// pre-drag rectangle (Translate only moves; a resize needs X/Y/W/H set).
    /// </summary>
    public void SetBounds(double x, double y, double w, double h)
    {
        var nb = new System.Drawing.Rectangle(
            (int)System.Math.Round(x),
            (int)System.Math.Round(y),
            (int)System.Math.Round(w),
            (int)System.Math.Round(h));
        if (_frame.Bounds == nb) return;
        _frame.Bounds = nb;
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(Height));
    }

    /// <summary>Move the frame by (dx,dy) in canvas-space pixels.</summary>
    public void Translate(double dx, double dy)
    {
        var b = _frame.Bounds;
        _frame.Bounds = new System.Drawing.Rectangle(
            (int)System.Math.Round(b.X + dx),
            (int)System.Math.Round(b.Y + dy),
            b.Width, b.Height);
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
    }

    /// <summary>
    /// Re-fire PropertyChanged for every binding-relevant property — used by
    /// the frame menu's "Convert to Comment / Placeholder" path which mutates
    /// <see cref="Frame.IsPlaceholder"/> + <see cref="Frame.FrameColor"/>
    /// outside the dedicated setters.
    /// </summary>
    public void RaiseAllChanged()
    {
        OnPropertyChanged(nameof(IsPlaceholder));
        OnPropertyChanged(nameof(FrameColorHex));
        OnPropertyChanged(nameof(FrameFillHex));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(ZOrder));
    }

    /// <summary>Resize the frame's right + bottom edges by (dw, dh).</summary>
    public void ResizeRightBottom(double dw, double dh)
    {
        var b = _frame.Bounds;
        int newW = System.Math.Max(40, b.Width  + (int)System.Math.Round(dw));
        int newH = System.Math.Max(40, b.Height + (int)System.Math.Round(dh));
        _frame.Bounds = new System.Drawing.Rectangle(b.X, b.Y, newW, newH);
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(Height));
    }

    /// <summary>
    /// Apply a per-edge resize given the captured pre-drag
    /// bounds (<paramref name="startX"/>, <paramref name="startY"/>,
    /// <paramref name="startW"/>, <paramref name="startH"/>) and the cumulative
    /// cursor delta (<paramref name="dx"/>, <paramref name="dy"/>) from the
    /// drag-start canvas point. Computing against the captured bounds (rather
    /// than the current bounds) keeps the math stable across multiple
    /// PointerMoved events — pre-fix the bottom-right-only path stored the
    /// delta against `_dragFrame.Width/Height` which created a feedback loop
    /// as the frame mutated mid-drag.
    ///
    /// Minimum size is 40×40 (matches <see cref="ResizeRightBottom"/>). Top /
    /// Left edges move the origin as they resize, so a min-size clamp on the
    /// width / height also clamps where the origin can land.
    /// </summary>
    public void ResizeByEdge(
        FrameResizeEdge edge,
        double dx, double dy,
        double startX, double startY,
        double startW, double startH)
    {
        if (edge == FrameResizeEdge.None) return;
        const int Min = 40;

        double newX = startX, newY = startY;
        double newW = startW, newH = startH;

        bool affectsLeft   = edge is FrameResizeEdge.Left   or FrameResizeEdge.TopLeft    or FrameResizeEdge.BottomLeft;
        bool affectsRight  = edge is FrameResizeEdge.Right  or FrameResizeEdge.TopRight   or FrameResizeEdge.BottomRight;
        bool affectsTop    = edge is FrameResizeEdge.Top    or FrameResizeEdge.TopLeft    or FrameResizeEdge.TopRight;
        bool affectsBottom = edge is FrameResizeEdge.Bottom or FrameResizeEdge.BottomLeft or FrameResizeEdge.BottomRight;

        if (affectsLeft)
        {
            // Clamp dx so width never drops below Min.
            double clampedDx = System.Math.Min(dx, startW - Min);
            newX = startX + clampedDx;
            newW = startW - clampedDx;
        }
        else if (affectsRight)
        {
            newW = System.Math.Max(Min, startW + dx);
        }

        if (affectsTop)
        {
            double clampedDy = System.Math.Min(dy, startH - Min);
            newY = startY + clampedDy;
            newH = startH - clampedDy;
        }
        else if (affectsBottom)
        {
            newH = System.Math.Max(Min, startH + dy);
        }

        _frame.Bounds = new System.Drawing.Rectangle(
            (int)System.Math.Round(newX),
            (int)System.Math.Round(newY),
            (int)System.Math.Round(newW),
            (int)System.Math.Round(newH));
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(Height));
    }
}
