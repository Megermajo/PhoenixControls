namespace Phoenix.Controls.Architect.WinUI.Canvas;

// Visual classification of a socket pin. Each kind maps to a small SVG/Path
// geometry the design package specifies in
// redesign-plan/design/project/architect.jsx (PinShape function).
//
// Produces this classification from a Socket so that the
// view layer on top can render the correct shape with no
// further branching: NodeView binds a PinShape control to the Kind enum
// and to PinPathGeometry.For(kind) for the SVG path string.
//
// Mismatch is reserved for the type-check-at-draw-time slice that this
// commit defers (CanvasNotes.md). It exists in the enum so future code
// that surfaces type-check failures has a slot already wired up.
public enum SocketPinKind
{
    Chevron,        // Flow
    Circle,         // String / Any / Object / Return / generic data
    Triangle,       // Bool (upward triangle)
    Diamond,        // Float (and Vector* visualist palette)
    Square,         // Int (filled square with black outline)
    RoundedSquare,  // Collection (slightly inset rounded square)
    Mismatch,       // type-check failure (deferred — leave as a slot)
}
