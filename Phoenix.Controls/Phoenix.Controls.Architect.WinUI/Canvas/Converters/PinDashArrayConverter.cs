using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Phoenix.Controls.Architect.WinUI.Canvas.Converters;

// bool → DoubleCollection for Path.StrokeDashArray. True → "2,2" dashed,
// False → empty (solid). Used by the pin Path to swap to a dashed outline
// when the socket is a placeholder or a wildcard — both visually distinct
// states that share the same "unresolved" treatment per the design package.
//
// DoubleCollection instances are shared singletons so the per-pin
// StrokeDashArray binding doesn't churn collections on every PropertyChanged.
public sealed class PinDashArrayConverter : IValueConverter
{
    private static readonly DoubleCollection s_dashed = new() { 2, 2 };
    private static readonly DoubleCollection s_solid  = new();

    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b && b ? s_dashed : s_solid;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
