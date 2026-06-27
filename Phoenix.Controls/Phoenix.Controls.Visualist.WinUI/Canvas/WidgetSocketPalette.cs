using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Visualist.WinUI.Canvas;

// A2 (audit 2026-05-24) — Visualist's per-pillar SocketDataType → pin
// shape mapping. Architect has its own copy at
// Phoenix.Controls.Architect.WinUI/Canvas/SocketPalette.cs; per the
// chrome-independence rule the two are kept in sync by design (Majo's
// published legend is the single contract) but may diverge if a
// Visualist-only data type gets its own glyph.
//
// Pre-A2 every Visualist socket rendered as an 8×8 Rectangle regardless
// of type — Manifesto §4.6's "6 distinct shapes" was unmet and Majo
// flagged it as a recurring regression in the 2026-05-24 audit.
internal static class WidgetSocketPalette
{
    /// <summary>
    /// Pin-shape kind for a Visualist socket. Mirrors Architect's mapping
    /// from the legend:
    ///   ▶ flow      → Chevron
    ///   ● string    → Circle
    ///   ● number    → Circle  (Int + Float)
    ///   ▲ bool      → Triangle
    ///   ◆ user      → Diamond (Any + Vector*)
    ///   ■ object    → Square  (Collection)
    /// Visualist-only types fall through to Circle (Image / Color /
    /// Scalar / Audio) — these have no glyph in the legend; matching
    /// Architect's choice of Circle keeps cross-canvas reading consistent.
    /// </summary>
    public static WidgetSocketPinKind KindFor(SocketDataType dataType) => dataType switch
    {
        SocketDataType.Flow       => WidgetSocketPinKind.Chevron,
        SocketDataType.Bool       => WidgetSocketPinKind.Triangle,
        SocketDataType.Collection => WidgetSocketPinKind.Square,
        SocketDataType.Any        => WidgetSocketPinKind.Diamond,
        SocketDataType.Vector2    => WidgetSocketPinKind.Diamond,
        SocketDataType.Vector3    => WidgetSocketPinKind.Diamond,
        SocketDataType.Vector4    => WidgetSocketPinKind.Diamond,
        _                         => WidgetSocketPinKind.Circle,
    };

    /// <summary>
    /// Numeric component count for a Vector* socket — 2, 3, or 4. Zero for
    /// every non-Vector type. Mirrors Architect's ArityFor — the small
    /// badge inside the pin disambiguates Vector2 / Vector3 / Vector4 at a
    /// glance instead of three identical diamonds.
    /// </summary>
    public static int ArityFor(SocketDataType dataType) => dataType switch
    {
        SocketDataType.Vector2 => 2,
        SocketDataType.Vector3 => 3,
        SocketDataType.Vector4 => 4,
        _                      => 0,
    };

    // R42 — SocketDataType → fill colour. The shared scalar/flow/bool/etc.
    // values MIRROR Architect's NodeRegistry colour constants (Flow=white exec,
    // String=#FFDC64, Int=#82C8FF, Float=#64D4C8, Bool=#8CDC8C, Collection=
    // #FFAA64, Any/object=#DCA0FF). Copied — not lifted to Shared — per
    // feedback_visualist_architect_chrome_independence; the Visualist-only
    // visual types (Image / Audio / Color / Vector*) extend the table using the
    // colours the sink sockets already ship (Image #EB50AA = DisplaySink,
    // Audio #BE8CF0 = AudioSink). Used to back-fill the wire + pin colour when a
    // socket carries the default white (regular widget sockets are created with
    // only Name/Type/DataType, so Socket.Color stays white).
    public static global::Windows.UI.Color ColorFor(SocketDataType dataType) => dataType switch
    {
        SocketDataType.Flow       => global::Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
        SocketDataType.String     => global::Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xDC, 0x64),
        SocketDataType.Int        => global::Windows.UI.Color.FromArgb(0xFF, 0x82, 0xC8, 0xFF),
        SocketDataType.Float      => global::Windows.UI.Color.FromArgb(0xFF, 0x64, 0xD4, 0xC8),
        SocketDataType.Scalar     => global::Windows.UI.Color.FromArgb(0xFF, 0x64, 0xD4, 0xC8),
        SocketDataType.Bool       => global::Windows.UI.Color.FromArgb(0xFF, 0x8C, 0xDC, 0x8C),
        SocketDataType.Collection => global::Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xAA, 0x64),
        SocketDataType.Image      => global::Windows.UI.Color.FromArgb(0xFF, 0xEB, 0x50, 0xAA),
        SocketDataType.Audio      => global::Windows.UI.Color.FromArgb(0xFF, 0xBE, 0x8C, 0xF0),
        SocketDataType.Color      => global::Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xC8, 0x50),
        SocketDataType.Vector2    => global::Windows.UI.Color.FromArgb(0xFF, 0xDC, 0xA0, 0xFF),
        SocketDataType.Vector3    => global::Windows.UI.Color.FromArgb(0xFF, 0xDC, 0xA0, 0xFF),
        SocketDataType.Vector4    => global::Windows.UI.Color.FromArgb(0xFF, 0xDC, 0xA0, 0xFF),
        _                         => global::Windows.UI.Color.FromArgb(0xFF, 0xDC, 0xA0, 0xFF),
    };

    // The colour a socket should actually paint with: its serialized Color when
    // set, otherwise the DataType back-fill. Flow keeps white (its canonical
    // colour) even at the default. Resolves the "all pins/wires render neutral"
    // regression — regular widget sockets ship Socket.Color == white.
    public static global::Windows.UI.Color EffectiveColor(Socket socket)
    {
        var c = socket.Color; // System.Drawing.Color
        bool isDefaultWhite = c.A == 255 && c.R == 255 && c.G == 255 && c.B == 255;
        if (isDefaultWhite && socket.DataType != SocketDataType.Flow)
            return ColorFor(socket.DataType);
        return global::Windows.UI.Color.FromArgb(c.A, c.R, c.G, c.B);
    }
}
