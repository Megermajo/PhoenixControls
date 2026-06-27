using System;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Phoenix.Controls.Architect.WinUI.Canvas.Converters;

// SocketViewModel.DropState → SolidColorBrush for the compat halo around a
// pin during a wire-drag. None → transparent (the surrounding halo
// visibility converter collapses the layer anyway), Valid → green,
// Invalid → red. Brushes are static singletons so the per-pin halo binding
// doesn't churn brushes on every drag-frame.
public sealed class DropStateToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush s_transparent = new(Colors.Transparent);
    // Same green / red the design package uses for the Ok / Err theme tokens
    // (#6FA46B / #CB4D3F) — kept in sync visually with the canvas's existing
    // wire-drop preview path so the halo and the in-flight wire stroke read
    // as the same affordance.
    private static readonly SolidColorBrush s_valid       = new(Color.FromArgb(0xFF, 0x6F, 0xA4, 0x6B));
    private static readonly SolidColorBrush s_invalid     = new(Color.FromArgb(0xFF, 0xCB, 0x4D, 0x3F));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is DropState ds)
        {
            return ds switch
            {
                DropState.Valid   => s_valid,
                DropState.Invalid => s_invalid,
                _                 => s_transparent,
            };
        }
        return s_transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
