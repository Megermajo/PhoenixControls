using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Shared.Models;
using Windows.UI;

namespace Phoenix.Controls.Hub.WinUI.Panels.AutomodPanel;

/// <summary>
/// One immutable row in the moderation column's AutomodLog feed. Plain get-only holder
/// (x:Bind OneTime) — the whole collection is rebuilt on load / on the Activity
/// event, so no change notification is needed per row. The action renders as a
/// tinted badge pill (the Giveaway status-pill recipe: token hue at ~10 % fill /
/// ~45 % border, full-strength mono label); the three pill brushes are resolved
/// once at construction so the DataTemplate binds them directly.
/// </summary>
public sealed class AutomodListRowVm
{
    public string Time { get; }
    public string Name { get; }
    public string Rule { get; }
    public string Action { get; }
    public string Reason { get; }

    /// <summary>Date half of <see cref="Time"/> ("yyyy-MM-dd"), empty when the stored
    /// stamp is not the two-part "yyyy-MM-dd HH:mm:ss" ScriptManager writes.</summary>
    public string DateText { get; }

    /// <summary>Clock half of <see cref="Time"/>, or the whole stamp verbatim when it
    /// does not split — the timestamp is never silently dropped.</summary>
    public string ClockText { get; }

    public Visibility DateVisibility => DateText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string ActionLabel { get; }
    public Brush ActionFillBrush { get; }
    public Brush ActionBorderBrush { get; }
    public Brush ActionTextBrush { get; }

    public AutomodListRowVm(AutomodLogEntry e)
    {
        Time = e.Time ?? "";
        Name = e.Name ?? "";
        Rule = e.Rule ?? "";
        Action = e.Action ?? "";
        Reason = e.Detail ?? "";

        // The 0.9* live column has no room for "yyyy-MM-dd HH:mm:ss" on one line, so
        // the stamp stacks: date above, clock below. Split on the FIRST space only, and
        // fall through to the raw string when there isn't one — an ISO "T"-separated or
        // otherwise unexpected stamp still renders in full rather than disappearing.
        int split = Time.IndexOf(' ');
        if (split > 0 && split < Time.Length - 1)
        {
            DateText = Time.Substring(0, split);
            ClockText = Time.Substring(split + 1);
        }
        else
        {
            DateText = "";
            ClockText = Time;
        }

        ActionLabel = Action.Length > 0 ? Action.ToUpperInvariant() : "—";
        var pill = ResolvePill(Action);
        ActionFillBrush = pill.Fill;
        ActionBorderBrush = pill.Border;
        ActionTextBrush = pill.Text;
    }

    // ── Action pill tints ───────────────────────────────────────────────
    // Alpha tints have no token form, so they're derived here from the
    // PhoenixDark hues exactly like GvStatusOpenFillBrush on the Giveaway
    // page: Warn → WarnBrush amber, Timeout → EmberPrimary, Ban → ErrBrush,
    // Delete / unknown → CoalSecondaryText neutral. Rows are only ever
    // constructed on the UI thread (RefreshActivityAsync posts), so the
    // lazily-initialised static brushes are UI-thread-affine.
    private static readonly (Brush Fill, Brush Border, Brush Text) WarnPill = MakePill(0xE0, 0xA2, 0x3A);
    private static readonly (Brush Fill, Brush Border, Brush Text) TimeoutPill = MakePill(0xE5, 0xA2, 0x4E);
    private static readonly (Brush Fill, Brush Border, Brush Text) BanPill = MakePill(0xC9, 0x53, 0x3C);
    private static readonly (Brush Fill, Brush Border, Brush Text) NeutralPill = MakePill(0x9C, 0x8A, 0x72);

    private static (Brush Fill, Brush Border, Brush Text) MakePill(byte r, byte g, byte b) => (
        new SolidColorBrush(Color.FromArgb(0x1A, r, g, b)),
        new SolidColorBrush(Color.FromArgb(0x73, r, g, b)),
        new SolidColorBrush(Color.FromArgb(0xFF, r, g, b)));

    // Prefix match so the dry-run suffix ("Timeout (dry-run)") keeps its hue.
    private static (Brush Fill, Brush Border, Brush Text) ResolvePill(string action)
    {
        if (action.StartsWith("Ban", StringComparison.OrdinalIgnoreCase)) return BanPill;
        if (action.StartsWith("Timeout", StringComparison.OrdinalIgnoreCase)) return TimeoutPill;
        if (action.StartsWith("Warn", StringComparison.OrdinalIgnoreCase)) return WarnPill;
        return NeutralPill;
    }
}
