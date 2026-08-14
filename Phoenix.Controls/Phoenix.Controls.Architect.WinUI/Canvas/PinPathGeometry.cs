namespace Phoenix.Controls.Architect.WinUI.Canvas;

// SVG-style path strings for the pin shapes, in a 14×14 viewBox.
//
// All shapes fit centred in a 14×14 box around (7,7) so a single PinShape
// control reserves the same hit-test square regardless of kind. Mirrors
// pre-T15 DrawSocket: chevron (Flow), diamond (Float), square (Int),
// triangle (Bool, point-up), rounded square (Collection), circle
// (String / Any / Object / Return / default).
public static class PinPathGeometry
{
    public const string Chevron       = "M 2 2 L 11 7 L 2 12 Z";
    public const string Circle        = "M 11.5 7 A 4.5 4.5 0 1 1 2.5 7 A 4.5 4.5 0 1 1 11.5 7 Z";
    public const string Triangle      = "M 7 2 L 12 12 L 2 12 Z";   // upward triangle (Bool)
    public const string Diamond       = "M 7 2 L 12 7 L 7 12 L 2 7 Z";
    public const string Square        = "M 2 2 L 12 2 L 12 12 L 2 12 Z";        // 10×10 filled (Int)
    public const string RoundedSquare = "M 4 2 L 10 2 Q 12 2 12 4 L 12 10 Q 12 12 10 12 L 4 12 Q 2 12 2 10 L 2 4 Q 2 2 4 2 Z"; // 2px corner radius (Collection)

    public static string PathFor(SocketPinKind kind) => kind switch
    {
        SocketPinKind.Chevron       => Chevron,
        SocketPinKind.Triangle      => Triangle,
        SocketPinKind.Diamond       => Diamond,
        SocketPinKind.Square        => Square,
        SocketPinKind.RoundedSquare => RoundedSquare,
        SocketPinKind.Mismatch      => Circle,   // unfilled circle when type-check fails
        _                           => Circle,
    };
}
