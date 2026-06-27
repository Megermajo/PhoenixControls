using System;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Phoenix.Controls.Architect.WinUI.Canvas.Converters;

/// <summary>
/// B21 (audit/winui-regressions-2026-05-24) — pill BorderBrush switcher for
/// fallback-mode pills. When <see cref="SocketViewModel.IsPillFallback"/> is
/// true (the socket is wired AND the parent node belongs to the
/// IsFallbackPillNode set — Math operators / Twitch / Discord / API.Call /
/// File.* / Text.Builder), the pill's BorderBrush dims to the secondary
/// divider colour so the author sees at a glance that this pill is a
/// "default-if-upstream-is-null" fallback rather than the live edit surface.
/// <para>
/// Returns a SolidColorBrush either way; falls back to a frozen ARGB
/// literal when the requested theme resource (CoalDividerBrush /
/// BorderPillBrush) can't be resolved. The accent value remains identical
/// for an unwired pill — the converter only affects the wired-fallback
/// surface so unwired pills look unchanged.
/// </para>
/// </summary>
public sealed class FallbackPillBorderBrushConverter : IValueConverter
{
    // Frozen ARGB fallbacks — used when the WinUI 3 resource lookup fails
    // (designer-time / pre-app-construction). The dim brush mirrors
    // CoalDividerBrush at ~60 % alpha so the outline still reads against the
    // pill background but no longer competes with the editable pill.
    private static readonly SolidColorBrush s_normalBrush = ResolveBrush("BorderPillBrush", 0xFF, 0xE0, 0xA2, 0x3A);
    private static readonly SolidColorBrush s_fallbackBrush = ResolveBrush("CoalDividerBrush", 0x99, 0x3A, 0x31, 0x27);

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isFallback = value is bool b && b;
        return isFallback ? s_fallbackBrush : s_normalBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();

    private static SolidColorBrush ResolveBrush(string key, byte a, byte r, byte g, byte b)
    {
        try
        {
            if (Microsoft.UI.Xaml.Application.Current?.Resources is { } res
                && res.TryGetValue(key, out var found)
                && found is SolidColorBrush brush) return brush;
        }
        catch { /* designer-time / pre-app construction */ }
        return new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }
}
