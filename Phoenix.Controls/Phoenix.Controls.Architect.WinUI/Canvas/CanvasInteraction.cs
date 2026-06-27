using Microsoft.UI.Xaml;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

/// <summary>
/// Attached property marking a node-body element as an inline EDIT affordance —
/// a value pill, a socket-label rename target, or a middle-attribute pill /
/// bool toggle.
/// <para>
/// Why it exists: every node-body edit interaction (pill click-to-edit, label /
/// title double-tap rename, middle-attr pill, DB-picker popup) is driven by a
/// <c>Tapped</c> / <c>DoubleTapped</c> / <c>Click</c> gesture on a child element.
/// The canvas-level <c>LogicCanvasView.OnHostPointerPressed</c> resolves a press
/// to a node / socket and immediately <c>CapturePointer</c>s it to start a
/// node-drag / wire-drop — and a captured pointer never delivers the child's
/// gesture, so EVERY in-body interaction died at once (Majo: "any interaction on
/// the node bodies are dead"). The handler's only escape hatch was
/// <c>IsInsideButton</c> (added so the DB-picker chevron works); there was no
/// equivalent for non-button affordances.
/// </para>
/// <para>
/// Elements carrying <c>IsEditAffordance="True"</c> are recognised by
/// <c>LogicCanvasView.IsInsideInteractiveChild</c>, which makes the press fall
/// through WITHOUT capturing — so the element's own gesture fires and inline
/// editing works. The socket PIN hit-targets (Ellipses) are deliberately NOT
/// marked, so dragging a wire from a pin still starts on press. The node header
/// / title is likewise unmarked, so it remains the node's drag handle.
/// </para>
/// </summary>
public static class CanvasInteraction
{
    /// <summary>
    /// Marks a node-body element (and its descendants) as an inline edit
    /// affordance. See the class remarks for the full rationale.
    /// </summary>
    public static readonly DependencyProperty IsEditAffordanceProperty =
        DependencyProperty.RegisterAttached(
            "IsEditAffordance",
            typeof(bool),
            typeof(CanvasInteraction),
            new PropertyMetadata(false));

    public static bool GetIsEditAffordance(DependencyObject obj)
        => (bool)obj.GetValue(IsEditAffordanceProperty);

    public static void SetIsEditAffordance(DependencyObject obj, bool value)
        => obj.SetValue(IsEditAffordanceProperty, value);

    /// <summary>
    /// Marks an element as a socket PIN hit-target — the 24×24 transparent
    /// ellipse mounted in <c>NodeView.PinOverlay</c> (see
    /// <c>NodeView.AddPinElement</c>). It exists because the socket ROW grid
    /// ALSO carries <c>Tag="{Binding}"</c> (a SocketViewModel), so matching on
    /// the Tag alone can't tell a genuine pin press (start a wire-drag) apart
    /// from a row / label press (inline edit). <c>LogicCanvasView.ResolvePinUnderCursor</c>
    /// keys on this marker to start a wire-drag authoritatively — even when
    /// WinUI reports <c>e.OriginalSource</c> as the row container BEHIND the
    /// pin, which previously let the edit-affordance guard veto a real pin press
    /// ("no pipe on some sockets"). Deliberately set ONLY on the pin hit-target,
    /// never on the row grid.
    /// </summary>
    public static readonly DependencyProperty IsSocketPinProperty =
        DependencyProperty.RegisterAttached(
            "IsSocketPin",
            typeof(bool),
            typeof(CanvasInteraction),
            new PropertyMetadata(false));

    public static bool GetIsSocketPin(DependencyObject obj)
        => (bool)obj.GetValue(IsSocketPinProperty);

    public static void SetIsSocketPin(DependencyObject obj, bool value)
        => obj.SetValue(IsSocketPinProperty, value);
}
