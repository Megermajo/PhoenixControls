using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Phoenix.Controls.Hub.WinUI.Panels.LoyaltyPanel;

/// <summary>
/// Gradient-brush factory for the Loyalty page row VMs. The horizontal ember
/// fade builds the thin per-row "weight bar" the leaderboard / balance lists
/// draw behind each row proportional to balance / topBalance — WinUI has no
/// inline CSS gradient equivalent, so the gradient lives here as a reusable
/// brush (mirrors GiveawayBrushes).
///
/// Unlike Timer/GiveawayBrushes there is no Application.Resources lookup helper
/// here: the Loyalty row VMs build no flat theme brushes in code, so every other
/// colour on the page comes straight from the merged PhoenixDark dictionary at
/// the XAML binding site.
/// </summary>
internal static class LoyaltyBrushes
{
    // Ember primary (#E5A24E) RGB — the fade is an alpha ramp over this hue.
    private const byte EmberR = 0xE5;
    private const byte EmberG = 0xA2;
    private const byte EmberB = 0x4E;

    /// <summary>
    /// Left→right ember fade from <paramref name="startAlpha"/> to fully
    /// transparent. Used for the per-row balance / leaderboard weight bar.
    /// </summary>
    public static Brush HorizontalEmberFade(byte startAlpha)
    {
        return new LinearGradientBrush
        {
            StartPoint = new global::Windows.Foundation.Point(0, 0),
            EndPoint   = new global::Windows.Foundation.Point(1, 0),
            GradientStops =
            {
                new GradientStop { Offset = 0, Color = Color.FromArgb(startAlpha, EmberR, EmberG, EmberB) },
                new GradientStop { Offset = 1, Color = Color.FromArgb(0x00,       EmberR, EmberG, EmberB) },
            },
        };
    }
}
