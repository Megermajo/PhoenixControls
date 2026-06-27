using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Phoenix.Controls.Hub.WinUI.Panels.GiveawayPanel;

/// <summary>
/// Theme-brush resolver for the Giveaway page row VMs. Mirrors the lookup
/// posture <see cref="ChatPanel.RoleColorBrush"/> uses: pull the brush from
/// Application.Resources (Hub.WinUI merges PhoenixDark at startup) and fall
/// back to a static literal so a design-time / test host that hasn't merged
/// the theme still renders something readable rather than throwing.
///
/// The horizontal ember-fade helpers build the per-row "weight bar" and the
/// winner-row body wash the design draws as CSS linear-gradients — WinUI has
/// no inline CSS equivalent, so the gradient lives here as a reusable brush.
/// </summary>
internal static class GiveawayBrushes
{
    // Ember primary (#E5A24E) RGB — the fade gradients are all alpha ramps
    // over this hue, matching the JSX rgba(229,162,78,…) literals.
    private const byte EmberR = 0xE5;
    private const byte EmberG = 0xA2;
    private const byte EmberB = 0x4E;

    // Neutral fallback for role-brush misses (coal-secondary tier).
    public static readonly (byte R, byte G, byte B) NeutralFallbackColor = (0x9C, 0x8A, 0x72);

    public static Brush Lookup(string key, byte r, byte g, byte b)
    {
        if (Application.Current?.Resources is { } res
            && res.TryGetValue(key, out var resource)
            && resource is Brush brush)
        {
            return brush;
        }
        return new SolidColorBrush(Color.FromArgb(0xFF, r, g, b));
    }

    public static Brush Lookup(string key, (byte R, byte G, byte B) fallback)
        => Lookup(key, fallback.R, fallback.G, fallback.B);

    /// <summary>
    /// Left→right ember fade from <paramref name="startAlpha"/> to fully
    /// transparent. Used for the per-row ticket-weight bar.
    /// </summary>
    public static Brush HorizontalEmberFade(byte startAlpha)
        => HorizontalEmberFade(startAlpha, 0x00);

    /// <summary>
    /// Left→right ember fade from <paramref name="startAlpha"/> to
    /// <paramref name="endAlpha"/>. Used for the winner-row body wash.
    /// </summary>
    public static Brush HorizontalEmberFade(byte startAlpha, byte endAlpha)
    {
        return new LinearGradientBrush
        {
            StartPoint = new global::Windows.Foundation.Point(0, 0),
            EndPoint   = new global::Windows.Foundation.Point(1, 0),
            GradientStops =
            {
                new GradientStop { Offset = 0,   Color = Color.FromArgb(startAlpha, EmberR, EmberG, EmberB) },
                new GradientStop { Offset = 1,   Color = Color.FromArgb(endAlpha,   EmberR, EmberG, EmberB) },
            },
        };
    }
}
