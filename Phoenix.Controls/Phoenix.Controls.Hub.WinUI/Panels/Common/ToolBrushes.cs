using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Phoenix.Controls.Hub.WinUI.Panels.Common;

/// <summary>
/// Theme-brush resolver shared by the Pre-Build tool pages — the promotion of
/// the identical per-page helpers <c>TimerBrushes</c> (Panels/TimerPanel) and
/// <c>GiveawayBrushes</c> (Panels/GiveawayPanel) into Panels/Common so the
/// rebuilt tool pages and the shared controls here resolve brushes one way.
///
/// Posture, unchanged from both originals: pull the brush from
/// Application.Resources (Hub.WinUI merges PhoenixDark at startup) and fall
/// back to a static literal, so a design-time / test host that has not merged
/// the theme renders something readable instead of throwing. The indexer form
/// (<c>Application.Current.Resources["Key"]</c>) is deliberately NOT used —
/// it throws KeyNotFoundException on a miss.
///
/// The two originals stay where they are; this file does not delete them.
/// </summary>
public static class ToolBrushes
{
    // Ember primary (#E5A24E) RGB — the fade gradients are all alpha ramps
    // over this hue, matching PhoenixDark's EmberPrimaryColor token.
    private const byte EmberR = 0xE5;
    private const byte EmberG = 0xA2;
    private const byte EmberB = 0x4E;

    /// <summary>Neutral fallback (coal-secondary tier, #9C8A72) for brush misses.</summary>
    public static readonly (byte R, byte G, byte B) NeutralFallbackColor = (0x9C, 0x8A, 0x72);

    /// <summary>
    /// Resolve <paramref name="key"/> from the app resource dictionary, or
    /// return an opaque SolidColorBrush over the supplied fallback channels.
    /// </summary>
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

    /// <summary>Tuple overload of <see cref="Lookup(string, byte, byte, byte)"/>.</summary>
    public static Brush Lookup(string key, (byte R, byte G, byte B) fallback)
        => Lookup(key, fallback.R, fallback.G, fallback.B);

    /// <summary>
    /// Left→right ember fade from <paramref name="startAlpha"/> to fully
    /// transparent. Used for per-row weight / meter bars.
    /// </summary>
    public static Brush HorizontalEmberFade(byte startAlpha)
        => HorizontalEmberFade(startAlpha, 0x00);

    /// <summary>
    /// Left→right ember fade from <paramref name="startAlpha"/> to
    /// <paramref name="endAlpha"/>. Used for highlighted row body washes.
    /// </summary>
    public static Brush HorizontalEmberFade(byte startAlpha, byte endAlpha)
    {
        return new LinearGradientBrush
        {
            StartPoint = new global::Windows.Foundation.Point(0, 0),
            EndPoint   = new global::Windows.Foundation.Point(1, 0),
            GradientStops =
            {
                new GradientStop { Offset = 0, Color = Color.FromArgb(startAlpha, EmberR, EmberG, EmberB) },
                new GradientStop { Offset = 1, Color = Color.FromArgb(endAlpha,   EmberR, EmberG, EmberB) },
            },
        };
    }
}
