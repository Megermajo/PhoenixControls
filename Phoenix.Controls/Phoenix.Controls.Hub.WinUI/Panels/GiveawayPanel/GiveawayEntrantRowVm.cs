using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.WinUI.Contracts;

namespace Phoenix.Controls.Hub.WinUI.Panels.GiveawayPanel;

/// <summary>
/// Row-level VM for one entrant in the live entrant list (right column of the
/// Giveaway page — giveaway.jsx <c>EntrantRow</c>). Carries the pre-computed
/// rank, the role badge label + brush (BR / M / V / S using the chat role
/// colors; viewers render blank), and the ember "weight bar" width that the
/// design draws behind each row proportional to tickets / maxTickets.
///
/// Role coloring mirrors the chat panel — the label is the short BR/M/V/S
/// monogram from the design's <c>ROLE_BADGE_GV</c> table, the brush comes from
/// the same ChatRole* theme tokens RoleColorBrush resolves against. Brushes are
/// resolved off Application.Resources with a neutral fallback so a design-time
/// host that hasn't merged PhoenixDark still renders.
/// </summary>
public sealed class GiveawayEntrantRowVm : ObservableObject
{
    // Bar width is expressed as a Grid star weight: the row hosts a two-column
    // overlay grid (bar* + rest*). 2% floor mirrors the JSX `Math.max(2, …)`
    // so a 1-ticket entrant against a huge leader still shows a sliver.
    private const double MinBarPercent = 2.0;

    public GiveawayEntrantRowVm(GiveawayEntrantInfo info, int rank, int maxTickets, bool isWinnerHighlight)
    {
        Username = info.Username;
        Tickets = info.Tickets;
        Rank = rank;
        // "#01".."#NN" — zero-padded two-digit rank, matching the JSX
        // String(rank).padStart(2, "0").
        RankText = "#" + rank.ToString("D2");
        IsTopThree = rank <= 3;
        IsWinnerHighlight = isWinnerHighlight;

        RoleLabel = ResolveRoleLabel(info.Role);
        HasRoleBadge = !string.IsNullOrEmpty(RoleLabel);
        RoleBrush = ResolveRoleBrush(info.Role);

        double pct = maxTickets <= 0 ? MinBarPercent : (double)info.Tickets / maxTickets * 100.0;
        BarWeight = Math.Max(MinBarPercent, pct);
        RestWeight = Math.Max(0.0, 100.0 - BarWeight);

        RankBrush = IsTopThree ? Brushes.EmberPrimary : Brushes.CoalSecondary;
        TicketsBrush = isWinnerHighlight ? Brushes.Ember200 : Brushes.CoalPrimary;
        RowBackground = isWinnerHighlight ? Brushes.WinnerHighlight : Brushes.Transparent;
        BarBrush = isWinnerHighlight ? Brushes.WinnerBar : Brushes.WeightBar;
    }

    public string Username { get; }
    public int Tickets { get; }
    // String projection for x:Bind to TextBlock.Text (x:Bind doesn't
    // implicitly convert int → string for Text).
    public string TicketsText => Tickets.ToString();
    public int Rank { get; }
    public string RankText { get; }
    public bool IsTopThree { get; }
    public bool IsWinnerHighlight { get; }

    public string RoleLabel { get; }
    public bool HasRoleBadge { get; }
    public Visibility RoleBadgeVisibility => HasRoleBadge ? Visibility.Visible : Visibility.Collapsed;

    // Grid star weights for the per-row weight bar overlay (bar + remainder).
    public double BarWeight { get; }
    public double RestWeight { get; }
    public GridLength BarColumnLength => new(BarWeight, GridUnitType.Star);
    public GridLength RestColumnLength => new(RestWeight, GridUnitType.Star);

    public Brush RoleBrush { get; }
    public Brush RankBrush { get; }
    public Brush TicketsBrush { get; }
    public Brush RowBackground { get; }
    public Brush BarBrush { get; }

    private static string ResolveRoleLabel(ChatRole role) => role switch
    {
        ChatRole.Broadcaster => "BR",
        ChatRole.Mod         => "M",
        ChatRole.Vip         => "V",
        ChatRole.Sub         => "S",
        _                    => string.Empty, // Viewer / Bot — no badge (matches ROLE_BADGE_GV)
    };

    private static Brush ResolveRoleBrush(ChatRole role)
    {
        string key = role switch
        {
            ChatRole.Broadcaster => "ChatRoleBroadcasterBrush",
            ChatRole.Mod         => "ChatRoleModBrush",
            ChatRole.Vip         => "ChatRoleVipBrush",
            ChatRole.Sub         => "ChatRoleSubBrush",
            _                    => "ChatRoleRegularBrush",
        };
        return GiveawayBrushes.Lookup(key, GiveawayBrushes.NeutralFallbackColor);
    }

    private static class Brushes
    {
        public static readonly Brush EmberPrimary  = GiveawayBrushes.Lookup("EmberPrimaryBrush",       0xE5, 0xA2, 0x4E);
        public static readonly Brush Ember200      = GiveawayBrushes.Lookup("Ember200Brush",           0xF2, 0xC7, 0x7F);
        public static readonly Brush CoalSecondary = GiveawayBrushes.Lookup("CoalSecondaryTextBrush",  0x9C, 0x8A, 0x72);
        public static readonly Brush CoalPrimary   = GiveawayBrushes.Lookup("CoalPrimaryTextBrush",    0xE6, 0xDD, 0xD0);
        public static readonly Brush Transparent   = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
        // Per-row ember weight bar — 6% ember left→transparent; winner row
        // 20% (matches the JSX linear-gradient stops).
        public static readonly Brush WeightBar       = GiveawayBrushes.HorizontalEmberFade(0x10);
        public static readonly Brush WinnerBar        = GiveawayBrushes.HorizontalEmberFade(0x33);
        // Winner row body wash — ember 18%→3% (JSX highlight background).
        public static readonly Brush WinnerHighlight  = GiveawayBrushes.HorizontalEmberFade(0x2E, 0x08);
    }
}
