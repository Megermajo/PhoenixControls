using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Phoenix.Controls.Hub.WinUI.Panels.TimerPanel;

/// <summary>
/// Theme-brush resolver for the Timer page row VMs — the exact posture the
/// Giveaway page's <c>GiveawayBrushes</c> uses: pull the brush from
/// Application.Resources (Hub.WinUI merges PhoenixDark at startup) and fall
/// back to a static literal so a design-time / test host that hasn't merged
/// the theme still renders something readable instead of throwing.
///
/// (The countdown progress bar in TimerView.xaml fills with the flat
/// <c>EmberPrimaryBrush</c> StaticResource, not a gradient from here.)
/// </summary>
internal static class TimerBrushes
{
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
}
