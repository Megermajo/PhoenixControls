using System;
using System.Collections.Concurrent;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Phoenix.Controls.Architect.WinUI.Canvas.Converters;

// "#RRGGBB" / "#AARRGGBB" → SolidColorBrush. SocketPalette and LinkViewModel
// expose colour as a hex string so the model layer doesn't depend on
// Microsoft.UI; the canvas DataTemplate binds through this converter.
//
// PERF (perf/architect-blockers, BlockerA): every wire stroke + every socket
// pin Fill/Stroke binds through this converter. Pre-cache: every Convert
// call allocated a fresh SolidColorBrush, so a wire-drag with N wires +
// M sockets thrashed ~1,200+ throwaway brushes per layout pass. We now
// memoise on the parsed hex (case-insensitive, fallback short-circuits to
// a shared grey instance).
public sealed class HexStringToBrushConverter : IValueConverter
{
    // Shared cache — converters are typically used as a single static resource
    // but we don't rely on that. ConcurrentDictionary keeps it correct if a
    // theme dictionary instantiates multiple converters across windows.
    private static readonly ConcurrentDictionary<string, SolidColorBrush> s_cache
        = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SolidColorBrush s_fallback = new(Color.FromArgb(0xFF, 0x46, 0x46, 0x4C));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string hex || string.IsNullOrEmpty(hex)) return s_fallback;
        return s_cache.GetOrAdd(hex, static h =>
            TryParse(h, out var color) ? new SolidColorBrush(color) : s_fallback);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();

    private static bool TryParse(string hex, out Color color)
    {
        color = Color.FromArgb(0xFF, 0x46, 0x46, 0x4C);
        if (string.IsNullOrEmpty(hex)) return false;
        if (hex[0] != '#') return false;

        try
        {
            if (hex.Length == 7)
            {
                byte r = byte.Parse(hex.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber);
                byte g = byte.Parse(hex.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber);
                byte b = byte.Parse(hex.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber);
                color = Color.FromArgb(255, r, g, b);
                return true;
            }
            if (hex.Length == 9)
            {
                byte a = byte.Parse(hex.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber);
                byte r = byte.Parse(hex.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber);
                byte g = byte.Parse(hex.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber);
                byte b = byte.Parse(hex.AsSpan(7, 2), System.Globalization.NumberStyles.HexNumber);
                color = Color.FromArgb(a, r, g, b);
                return true;
            }
        }
        catch { /* fall through */ }
        return false;
    }
}
