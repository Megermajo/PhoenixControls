using Microsoft.UI.Xaml.Media;

namespace Phoenix.Controls.Hub.WinUI.Panels.GiveawayPanel;

/// <summary>
/// Row-level VM for the per-giveaway activity log (left column — giveaway.jsx
/// <c>GIVEAWAY_LOG</c> rows). Each row is time + level (INF / WIN) + message;
/// WIN rows render in ember per the design, INF rows use the info / body
/// brushes.
/// </summary>
public sealed class GiveawayActivityRowVm
{
    public GiveawayActivityRowVm(Phoenix.Controls.Shared.WinUI.Contracts.GiveawayActivityEntry entry)
    {
        Time = entry.Time;
        Level = entry.Kind;
        Message = entry.Message;

        bool isWin = string.Equals(entry.Kind, "WIN", System.StringComparison.OrdinalIgnoreCase);
        // WIN → ember level + ember message; INF (and anything else) → info
        // level + body-text message. Matches the JSX ternary on l.lvl === "WIN".
        LevelBrush   = isWin ? GiveawayBrushes.Lookup("EmberPrimaryBrush",    0xE5, 0xA2, 0x4E)
                             : GiveawayBrushes.Lookup("InfoBrush",            0x6F, 0x94, 0xB0);
        MessageBrush = isWin ? GiveawayBrushes.Lookup("Ember200Brush",        0xF2, 0xC7, 0x7F)
                             : GiveawayBrushes.Lookup("CoalPrimaryTextBrush", 0xE6, 0xDD, 0xD0);
    }

    public string Time { get; }
    public string Level { get; }
    public string Message { get; }
    public Brush LevelBrush { get; }
    public Brush MessageBrush { get; }
}
