using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Phoenix.Controls.Architect.WinUI.Canvas.Converters;

// SocketViewModel.DropState → Visibility for the compat-halo overlay around
// a pin. None → Collapsed (no drag in progress, or this socket is not a
// candidate target); Valid / Invalid → Visible (a wire is being dragged
// and the halo paints the green / red feedback).
public sealed class DropStateToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is DropState ds && ds != DropState.None
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
