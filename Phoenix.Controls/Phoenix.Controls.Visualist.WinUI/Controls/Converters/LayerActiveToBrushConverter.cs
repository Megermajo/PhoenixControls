using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Phoenix.Controls.Visualist.WinUI.Controls.Converters;

/// <summary>
/// Maps <see cref="Phoenix.Controls.Visualist.WinUI.Models.LayerListItem.Active"/>
/// to the LayerRail's per-row dot brush — OkBrush (green) when at least one
/// OBS browser source is currently connected for the layer, otherwise the
/// neutral CoalDividerBrush. Sprint K replaced the hardcoded green.
///
/// Per-pillar paint independence — this converter lives in Visualist.WinUI
/// (not Shared.WinUI) per
/// feedback_visualist_architect_chrome_independence: Architect has no
/// analogous per-row presence concept and shouldn't pull a converter that
/// only Visualist needs.
/// </summary>
public sealed class LayerActiveToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool active = value is bool b && b;
        string key = active ? "OkBrush" : "CoalDividerBrush";
        if (Application.Current?.Resources.TryGetValue(key, out var found) is true && found is Brush brush)
            return brush;
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
