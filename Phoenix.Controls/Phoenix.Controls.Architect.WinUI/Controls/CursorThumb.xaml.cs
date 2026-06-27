using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Phoenix.Controls.Architect.WinUI.Controls;

/// <summary>
/// Splitter Thumb wrapper that forces a directional resize cursor on hover.
///
/// WinUI 3's <see cref="Thumb"/> primitive (and GridSplitter) does not expose
/// a Cursor property; without an override the splitter strip presents as the
/// default arrow even though it accepts a drag. This wrapper swaps the
/// system cursor via <see cref="UIElement.ProtectedCursor"/> (set inside
/// PointerEntered, cleared on PointerExited) so the affordance reads
/// "I can drag this" the moment the cursor hovers the strip.
///
/// Pre-fix: every splitter usage in MainView / SubGraphWindow had to drop
/// a bare prim:Thumb and got the bad cursor. Wrapping it here lets the
/// XAML stay declarative — every splitter just instantiates a CursorThumb
/// and supplies an <see cref="Orientation"/>.
///
/// Default orientation is <see cref="Orientation.Vertical"/> (i.e. the
/// splitter sits vertically between two stacked rows and the cursor reads
/// SizeNorthSouth). Horizontal splitters (between two side-by-side columns)
/// set Orientation="Horizontal" and get SizeWestEast.
/// </summary>
public sealed partial class CursorThumb : UserControl
{
    public CursorThumb()
    {
        InitializeComponent();
        // ProtectedCursor lives on UIElement; flipping it inside the hover
        // pair is the canonical WinUI 3 approach (FrameworkElement.Cursor
        // doesn't exist in WinUI 3 the way it did in WPF / WinForms).
        PointerEntered += OnPointerEnteredCursorThumb;
        PointerExited  += OnPointerExitedCursorThumb;
        // Re-paint the cursor when Orientation changes mid-hover (rare —
        // most consumers set it once at construction).
        RegisterPropertyChangedCallback(OrientationProperty, OnOrientationChanged);
    }

    /// <summary>
    /// Splitter axis. <c>Vertical</c> = the strip is wide and short, sitting
    /// between two stacked rows (cursor = SizeNorthSouth).
    /// <c>Horizontal</c> = the strip is tall and narrow, between two
    /// side-by-side columns (cursor = SizeWestEast).
    /// </summary>
    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public static readonly DependencyProperty OrientationProperty =
        DependencyProperty.Register(
            nameof(Orientation),
            typeof(Orientation),
            typeof(CursorThumb),
            new PropertyMetadata(Orientation.Vertical));

    /// <summary>
    /// Forwarded <see cref="Thumb.DragDelta"/> so consumers can hook the
    /// drag handler from XAML without needing to reach into the inner
    /// Thumb directly — preserves the existing MainView.xaml shape:
    /// <c>DragDelta="OnInspectorThumbDragDelta"</c>.
    /// </summary>
    public event Microsoft.UI.Xaml.Controls.Primitives.DragDeltaEventHandler? DragDelta
    {
        add    => InnerThumb.DragDelta += value;
        remove => InnerThumb.DragDelta -= value;
    }

    /// <summary>Forwarded <see cref="Thumb.DragStarted"/>.</summary>
    public event Microsoft.UI.Xaml.Controls.Primitives.DragStartedEventHandler? DragStarted
    {
        add    => InnerThumb.DragStarted += value;
        remove => InnerThumb.DragStarted -= value;
    }

    /// <summary>Forwarded <see cref="Thumb.DragCompleted"/>.</summary>
    public event Microsoft.UI.Xaml.Controls.Primitives.DragCompletedEventHandler? DragCompleted
    {
        add    => InnerThumb.DragCompleted += value;
        remove => InnerThumb.DragCompleted -= value;
    }

    private void OnOrientationChanged(DependencyObject sender, DependencyProperty dp)
    {
        // Pointer is rarely actually inside while orientation changes — but
        // if it is, refresh the cursor so the new axis is reflected mid-hover.
        if (IsLoaded) ApplyCursorIfHovered();
    }

    private bool _hovered;

    private void OnPointerEnteredCursorThumb(object sender, PointerRoutedEventArgs e)
    {
        _hovered = true;
        ApplyCursorIfHovered();
    }

    private void OnPointerExitedCursorThumb(object sender, PointerRoutedEventArgs e)
    {
        _hovered = false;
        ProtectedCursor = null;
    }

    private void ApplyCursorIfHovered()
    {
        if (!_hovered) return;
        try
        {
            var shape = Orientation == Orientation.Horizontal
                ? InputSystemCursorShape.SizeWestEast
                : InputSystemCursorShape.SizeNorthSouth;
            ProtectedCursor = InputSystemCursor.Create(shape);
        }
        catch
        {
            // Cursor APIs throw on shutdown / pre-realised trees; swallow so
            // the splitter still functions, just with the default arrow.
        }
    }
}
