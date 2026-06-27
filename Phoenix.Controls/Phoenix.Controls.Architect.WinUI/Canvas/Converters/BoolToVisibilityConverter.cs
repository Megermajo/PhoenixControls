using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Phoenix.Controls.Architect.WinUI.Canvas.Converters;

// Standard bool → Visibility. WinUI 3 doesn't ship one in-box. Pass parameter="invert"
// (or "Invert") to flip — useful for toggling a placeholder / loaded-state pair.
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool flag = value is bool b && b;
        bool invert = parameter is string s && string.Equals(s, "invert", StringComparison.OrdinalIgnoreCase);
        if (invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
