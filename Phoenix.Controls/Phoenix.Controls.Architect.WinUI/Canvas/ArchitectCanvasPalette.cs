using Windows.UI;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

// ─────────────────────────────────────────────────────────────────────────────
// Architect canvas palette — per-pillar (NOT lifted to Shared)
// ─────────────────────────────────────────────────────────────────────────────
//
// Constant-ifies the literal colour tokens that the canvas partials used to
// re-spell inline. Two motivations:
//
//   1) Single point of edit. Pre-fix the frame-comment colour (255, 200, 80)
//      appeared in 5+ partials (Menus, Palette, xaml.cs); a re-skin had to
//      hunt them down. Now there's one constant.
//
//   2) Self-documentation. A bare Color.FromArgb(255, 200, 80) at a call site
//      is opaque — naming it CommentFrameDefault makes the intent obvious.
//
// Per feedback_visualist_architect_chrome_independence this stays inside
// Architect.WinUI.Canvas. Visualist owns its own copy of any colour it
// needs from here; the two pillars are evolving independently (Visualist
// nodes will host live preview thumbnails Architect has no analogue for, so
// a shared palette would couple their visual evolution).
//
// Per feedback_no_root_doc_edits Design_Orders.md is read-only; this file
// records the values it sources from, but doesn't update Design_Orders.
//
// All values are bit-identical to the previously inlined literals — no
// "improvement" colour tweaks.
internal static class ArchitectCanvasPalette
{
    // ─── Frame defaults (the two seed colours Add Comment / Add Placeholder
    //     spawn with — System.Drawing.Color because FrameViewModel.Color is
    //     a System.Drawing.Color on the shared Frame model).
    public static System.Drawing.Color CommentFrameDefault     => System.Drawing.Color.FromArgb(255, 200, 80);
    public static System.Drawing.Color PlaceholderFrameDefault => System.Drawing.Color.FromArgb(160,  80, 80);

    // ─── Selection token (Design_Orders gold #FFD700). Used both as a
    //     SolidColorBrush fallback (when SelectionBrush isn't in the
    //     theme dictionary) and as a fill tint on the marquee overlay.
    public static Color SelectionGold => Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00);

    /// <summary>The same gold at 0x28 / ~16% alpha — marquee rubber-band fill.</summary>
    public static Color SelectionGoldFill16 => Color.FromArgb(40, 0xFF, 0xD7, 0x00);

    // ─── ErrBrush / OkBrush fallbacks. Pre-fix the canvas spelled these as
    //     Microsoft.UI.Colors.OrangeRed / LightGreen as the second arg to
    //     Resource() lookups. Those were *fallbacks* — when the theme
    //     dictionary contains ErrBrush/OkBrush (which it does), the named
    //     resource wins. Keeping the fallback values aligned with the
    //     dictionary entries (rust red + sage green) instead of WinUI's
    //     raw OrangeRed / LightGreen so a missing theme dictionary doesn't
    //     produce a jarring colour mismatch.
    public static Color ErrRustRed => Color.FromArgb(0xFF, 0xCB, 0x4D, 0x3F);
    public static Color OkSageGreen => Color.FromArgb(0xFF, 0x6F, 0xA4, 0x6B);
    public static Color EmberDefaultPreview => Color.FromArgb(0xFF, 0xFF, 0xC8, 0x50);

    // ─── SpawnPaletteFlyout header-brush fallbacks. Same numeric values as
    //     CommentFrameDefault / PlaceholderFrameDefault above (255/200/80
    //     and 160/80/80) but lifted into the Windows.UI.Color space because
    //     the spawn-palette consumes a Color (the frame-default constants
    //     are System.Drawing.Color for FrameViewModel's model contract).
    //     Keeping the values bit-identical to the pre-fix inlined literals.
    public static Color SpawnPaletteCommentFallback     => Color.FromArgb(0xFF, 0xFF, 0xC8, 0x50);
    public static Color SpawnPalettePlaceholderFallback => Color.FromArgb(0xFF, 0xA0, 0x50, 0x50);
}
