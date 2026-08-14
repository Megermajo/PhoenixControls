using Microsoft.UI.Xaml.Media;

namespace Phoenix.Controls.Hub.WinUI.Panels.TimerPanel;

/// <summary>
/// Row-level VM for the per-timer activity feed (right column). Each row is
/// time + kind + message. Kinds come from the TimerActivity table
/// (ADD | CTRL | MILE | ZERO | INF); the level / message brushes follow the
/// coal/ember palette the Giveaway activity log uses:
///   ZERO → error red · MILE → ember · ADD → ok green · CTRL/INF → info.
/// </summary>
public sealed class TimerActivityRowVm
{
    public TimerActivityRowVm(string time, string kind, string message)
    {
        Time = time;
        Level = kind;
        Message = message;

        string k = (kind ?? string.Empty).Trim().ToUpperInvariant();
        switch (k)
        {
            case "ZERO":
                LevelBrush   = TimerBrushes.Lookup("ErrBrush",             0xC8, 0x5C, 0x54);
                MessageBrush = TimerBrushes.Lookup("Ember200Brush",        0xF2, 0xC7, 0x7F);
                break;
            case "MILE":
                LevelBrush   = TimerBrushes.Lookup("EmberPrimaryBrush",    0xE5, 0xA2, 0x4E);
                MessageBrush = TimerBrushes.Lookup("Ember200Brush",        0xF2, 0xC7, 0x7F);
                break;
            case "ADD":
                LevelBrush   = TimerBrushes.Lookup("OkBrush",              0x6F, 0xA4, 0x6B);
                MessageBrush = TimerBrushes.Lookup("CoalPrimaryTextBrush", 0xE6, 0xDD, 0xD0);
                break;
            default: // CTRL / INF / anything else
                LevelBrush   = TimerBrushes.Lookup("InfoBrush",            0x6F, 0x94, 0xB0);
                MessageBrush = TimerBrushes.Lookup("CoalPrimaryTextBrush", 0xE6, 0xDD, 0xD0);
                break;
        }
    }

    public string Time { get; }
    public string Level { get; }
    public string Message { get; }
    public Brush LevelBrush { get; }
    public Brush MessageBrush { get; }
}
