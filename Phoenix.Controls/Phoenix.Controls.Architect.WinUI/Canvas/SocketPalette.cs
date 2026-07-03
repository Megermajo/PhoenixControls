using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

// Single source of truth for socket-data-type → render colour and pin kind,
// used by every NodeView socket row and every WireLayer link path.
//
// The colour values are the same hex strings the design package uses
// (redesign-plan/design/project/architect.jsx:SOCKET) and that
// Phoenix.Controls.Shared.WinUI/Themes/PhoenixDark.xaml exposes through
// EmberPrimaryBrush / OkBrush / etc. We don't read from PhoenixDark.xaml
// directly here — the design package's per-pin colours are tighter than
// the broad palette (e.g. number is a desaturated cyan #7FBED1 not the
// full InfoBrush #6F94B0). When the view layer hosts these VMs inside
// actual XAML, the view-side bindings can reach for the design palette via
// {StaticResource} for the surrounding chrome and use these hex values
// for the pins themselves.
//
// CRITICAL: per the project conventions "Three-way contract", the *runtime* command-side
// palette of socket colours lives in
// Phoenix.Controls.Architect.Core.NodeRegistry (ColExec / ColString / ...).
// The palette here is the *render-side* mirror used by the WinUI
// canvas; both must stay in sync. NodeRegistry's mapping uses
// System.Drawing.Color (WinForms) — this file's palette may later be swapped
// over to read NodeRegistry directly once colour-conversion helpers exist
// on its side. For now they are decoupled.
public static class SocketPalette
{
    /// <summary>Hex-RGB colour string ("#RRGGBB") for a socket of the given data type.</summary>
    public static string HexFor(SocketDataType dataType) => dataType switch
    {
        SocketDataType.Flow       => "#F5EFE3",   // coal-paper (chevron stroke + fill)
        SocketDataType.String     => "#E5A24E",   // ember-300
        // 0.11.5 canvas-polish — Int + Float collapse to the rev-2 design's
        // single "number" palette token (#7FBED1 sky cyan, circle shape) per
        // redesign-plan/design/project/architect.jsx. The prior Int=square
        // (#7FBED1) / Float=diamond (#9CFF9C) split predated the rev-2 plan;
        // Majo flagged the shape-palette as "not according to the plan" in
        // the 0.11.5 audit. Wire-colour widening (Int↔Float compatibility
        // via NodeRegistry.AreCompatible) is unaffected — the two now share
        // a colour AND a shape, the way the rev-2 mock intends.
        SocketDataType.Int        => "#7FBED1",   // number (sky cyan)
        SocketDataType.Float      => "#7FBED1",   // number (sky cyan)
        SocketDataType.Bool       => "#9CC97A",   // sage green
        // 0.11.x legend-alignment — Majo's published socket legend pins these
        // two to colours that the rev-2 architect.jsx mock did not carry:
        //   ■ object (Collection) → teal  (was ember-warm #FFAA64)
        //   ◆ user   (Any)        → role-vip lavender #C893BC (was coal-8 grey)
        // The legend ("see reference") is the contract. Teal is the closest
        // match to the published swatch; it's a single constant — nudge if a
        // shade off. Any==#C893BC matches the design source's `user` token.
        SocketDataType.Collection => "#5FB8A6",   // object (teal)
        SocketDataType.Any        => "#C893BC",   // user (role-vip lavender)
        SocketDataType.Image      => "#C893BC",   // role-vip lavender (visualist-only)
        SocketDataType.Color      => "#E0A23A",   // warn (visualist-only)
        SocketDataType.Scalar     => "#7FBED1",   // sky cyan (visualist-only)
        SocketDataType.Vector2    => "#9C8AC4",   // role-sub (visualist-only)
        SocketDataType.Vector3    => "#9C8AC4",
        SocketDataType.Vector4    => "#9C8AC4",
        SocketDataType.Audio      => "#6FA46B",   // ok (visualist-only)
        _                         => "#A89683",
    };

    /// <summary>
    /// Pin-shape kind for a socket of the given data type.
    ///
    /// Realigned to Majo's published legend:
    ///   ▶ flow      → Chevron
    ///   ● string    → Circle
    ///   ● number    → Circle  (Int + Float share)
    ///   ▲ bool      → Triangle
    ///   ◆ user      → Diamond (mapped to <c>Any</c> + Vector* — "user data")
    ///   ▢ object    → RoundedSquare (mapped to <c>Collection</c>)
    ///
    /// The 0.11.5 collapse to "everything that isn't Flow/Bool/Vector is
    /// a Circle" lost two of the legend's six shapes (square + diamond)
    /// because it interpreted "object" / "user" loosely. The legend Majo
    /// published is the contract — restoring Collection→RoundedSquare and
    /// Any→Diamond keeps Visualist-only types (Image / Color / Scalar /
    /// Audio) on Circle since those have no legend equivalent.
    ///
    /// Collection now maps to <see cref="SocketPinKind.RoundedSquare"/>
    /// (not <c>Square</c>): SocketKind.cs documents RoundedSquare as the
    /// "Collection (slightly inset rounded square)" shape, and
    /// PinPathGeometry.cs / NodeView.BuildPinShape both wire a 2px-corner
    /// rounded rectangle for it. The prior <c>Square</c> mapping left the
    /// RoundedSquare enum member + its geometry as dead code and rendered
    /// Collection identically to Int (now both Circle anyway). The rounded
    /// square keeps Collection visually distinct from the plain square pins.
    /// </summary>
    public static SocketPinKind KindFor(SocketDataType dataType) => dataType switch
    {
        SocketDataType.Flow       => SocketPinKind.Chevron,
        SocketDataType.Bool       => SocketPinKind.Triangle,
        SocketDataType.Collection => SocketPinKind.RoundedSquare,  // ▢ object
        SocketDataType.Any        => SocketPinKind.Diamond,   // ◆ user (wildcard)
        SocketDataType.Vector2    => SocketPinKind.Diamond,
        SocketDataType.Vector3    => SocketPinKind.Diamond,
        SocketDataType.Vector4    => SocketPinKind.Diamond,
        // Int / Float / String / Image / Color / Scalar / Audio → Circle.
        // Number (Int+Float) explicitly Circle per the legend's `● number`.
        _                         => SocketPinKind.Circle,
    };

    /// <summary>
    /// Numeric component count for a Vector* socket — 2, 3, or 4. Zero for
    /// every non-Vector type. NodeView renders this as a small badge inside
    /// the pin so Vector2 / Vector3 / Vector4 read at a glance instead of
    /// sharing the same lavender diamond.
    /// </summary>
    public static int ArityFor(SocketDataType dataType) => dataType switch
    {
        SocketDataType.Vector2 => 2,
        SocketDataType.Vector3 => 3,
        SocketDataType.Vector4 => 4,
        _                      => 0,
    };
}
