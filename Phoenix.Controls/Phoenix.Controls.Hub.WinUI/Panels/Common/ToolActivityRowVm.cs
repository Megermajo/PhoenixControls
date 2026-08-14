using Microsoft.UI.Xaml.Media;

namespace Phoenix.Controls.Hub.WinUI.Panels.Common;

/// <summary>
/// Row-level VM for <see cref="ToolActivityFeed"/> — time + kind + message, with
/// the two brushes the row template paints. Immutable, get-only and deliberately
/// NOT observable: rows are appended and dropped, never edited in place, which
/// is the same posture as the per-page TimerActivityRowVm / GiveawayActivityRowVm
/// this generalises.
///
/// Kind is matched case-insensitively and the switch has a default branch, so an
/// unrecognised kind renders as an INF-coloured row rather than a blank one. The
/// map below is the union of the per-tool kinds the build spec assigns:
///
///   ERR / DROP / DENY   error red    over ember-200
///   WARN                warn amber   over primary text
///   WIN / UP / FIRE / PAY   ember    over ember-200
///   ADD / POST / PLAY   ok green     over primary text
///   anything else       info blue    over primary text
///
/// The Level string is rendered verbatim, so callers keep whatever kind they
/// recorded (Automod resolves its action pills by prefix, for instance) even
/// when the colour falls through to the default branch.
/// </summary>
public sealed class ToolActivityRowVm
{
    public ToolActivityRowVm(string time, string kind, string message)
    {
        Time = time ?? "";
        Level = kind ?? "";
        Message = message ?? "";

        string k = Level.Trim().ToUpperInvariant();
        switch (k)
        {
            case "ERR":
            case "DROP":
            case "DENY":
                LevelBrush   = ToolBrushes.Lookup("ErrBrush",              0xC9, 0x53, 0x3C);
                MessageBrush = ToolBrushes.Lookup("Ember200Brush",         0xF2, 0xC7, 0x7F);
                break;

            case "WARN":
                LevelBrush   = ToolBrushes.Lookup("WarnBrush",             0xE0, 0xA2, 0x3A);
                MessageBrush = ToolBrushes.Lookup("CoalPrimaryTextBrush",  0xE6, 0xDD, 0xD0);
                break;

            case "WIN":
            case "UP":
            case "FIRE":
            case "PAY":
                LevelBrush   = ToolBrushes.Lookup("EmberPrimaryBrush",     0xE5, 0xA2, 0x4E);
                MessageBrush = ToolBrushes.Lookup("Ember200Brush",         0xF2, 0xC7, 0x7F);
                break;

            case "ADD":
            case "POST":
            case "PLAY":
                LevelBrush   = ToolBrushes.Lookup("OkBrush",               0x6F, 0xA4, 0x6B);
                MessageBrush = ToolBrushes.Lookup("CoalPrimaryTextBrush",  0xE6, 0xDD, 0xD0);
                break;

            default:
                LevelBrush   = ToolBrushes.Lookup("InfoBrush",             0x6F, 0x94, 0xB0);
                MessageBrush = ToolBrushes.Lookup("CoalPrimaryTextBrush",  0xE6, 0xDD, 0xD0);
                break;
        }
    }

    /// <summary>Pre-formatted clock text — the feed does no formatting of its own.</summary>
    public string Time { get; }

    /// <summary>The kind token, rendered verbatim in the 38px column.</summary>
    public string Level { get; }

    public string Message { get; }

    public Brush LevelBrush { get; }

    public Brush MessageBrush { get; }
}
