using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Visualist.WinUI.Canvas;

// Per-pillar twin of Architect's PinPathGeometry.
//
// The standing rule is that each pillar owns its own paint helpers.
// This file is Visualist's
// copy of the path data; Architect's lives at
// Phoenix.Controls.Architect.WinUI/Canvas/PinPathGeometry.cs.
//
// The geometries themselves match Architect's so Majo's published legend
// reads identically across both canvases — but a future Visualist visual
// (e.g. Image / Audio glyphs that have no Architect equivalent) is free
// to diverge here without cross-pillar coupling.
//
// Path data is authored against a 14×14 viewbox (matching Architect's
// pin chrome). Consumers (WidgetGraphNodeView.BuildPinRow) instantiate
// a Microsoft.UI.Xaml.Shapes.Path with Width=Height=14 and
// Stretch=Uniform so the geometry fits.
internal static class WidgetPinPathGeometry
{
    public const string Chevron       = "M 2 2 L 11 7 L 2 12 Z";
    public const string Circle        = "M 11.5 7 A 4.5 4.5 0 1 1 2.5 7 A 4.5 4.5 0 1 1 11.5 7 Z";
    public const string Triangle      = "M 7 2 L 12 12 L 2 12 Z";   // upward triangle (Bool)
    public const string Diamond       = "M 7 2 L 12 7 L 7 12 L 2 7 Z";
    public const string Square        = "M 2 2 L 12 2 L 12 12 L 2 12 Z";
    public const string RoundedSquare = "M 4 2 L 10 2 Q 12 2 12 4 L 12 10 Q 12 12 10 12 L 4 12 Q 2 12 2 10 L 2 4 Q 2 2 4 2 Z";

    public static string PathFor(WidgetSocketPinKind kind) => kind switch
    {
        WidgetSocketPinKind.Chevron       => Chevron,
        WidgetSocketPinKind.Triangle      => Triangle,
        WidgetSocketPinKind.Diamond       => Diamond,
        WidgetSocketPinKind.Square        => Square,
        WidgetSocketPinKind.RoundedSquare => RoundedSquare,
        _                                 => Circle,
    };
}

internal enum WidgetSocketPinKind
{
    Circle = 0,
    Chevron,
    Triangle,
    Diamond,
    Square,
    RoundedSquare,
}
