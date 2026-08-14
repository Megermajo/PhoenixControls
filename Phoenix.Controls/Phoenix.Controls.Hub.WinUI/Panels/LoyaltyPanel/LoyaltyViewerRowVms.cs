using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.WinUI.Panels.LoyaltyPanel;

/// <summary>
/// One editable balance row on the Viewers ▸ Balances list. Shows the standing
/// (rank / name / balance) and carries a small "set to" draft + apply hook so a
/// broadcaster can override any wallet in place. The apply callback routes to
/// <c>LoyaltyService.SetAsync</c> on the owning VM.
/// </summary>
public sealed class LoyaltyBalanceRowVm : ObservableObject
{
    // 2% floor mirrors the Giveaway entrant weight-bar so a small wallet against
    // a whale still shows a sliver (GiveawayEntrantRowVm.MinBarPercent).
    private const double MinBarPercent = 2.0;

    private readonly Func<string, long, Task> _apply;
    private string _setDraft = string.Empty;

    public LoyaltyBalanceRowVm(LoyaltyStanding standing, Func<string, long, Task> apply, long maxBalance = 0)
    {
        Name = standing.Name;
        Balance = standing.Balance;
        Rank = standing.Rank;
        _apply = apply;

        double pct = maxBalance <= 0 ? MinBarPercent : (double)Balance / maxBalance * 100.0;
        BarWeight = Math.Max(MinBarPercent, pct);
        RestWeight = Math.Max(0.0, 100.0 - BarWeight);
        BarBrush = LoyaltyBrushes.HorizontalEmberFade(0x10);
    }

    public string Name { get; }
    public long Balance { get; }
    public int Rank { get; }

    public string RankLabel => "#" + Rank.ToString(CultureInfo.InvariantCulture);
    public string BalanceLabel => Balance.ToString("N0", CultureInfo.InvariantCulture);

    // Per-row ember weight bar (balance / maxBalance) — a two-column overlay grid
    // (bar* + rest*) mirroring the Giveaway entrant list's design-family bar.
    public double BarWeight { get; }
    public double RestWeight { get; }
    public GridLength BarColumnLength => new(BarWeight, GridUnitType.Star);
    public GridLength RestColumnLength => new(RestWeight, GridUnitType.Star);
    public Brush BarBrush { get; }

    public string SetDraft
    {
        get => _setDraft;
        set => Set(ref _setDraft, value ?? string.Empty);
    }

    /// <summary>Parse the draft and push an absolute Set through the service.
    /// An unparseable / blank draft is a no-op (nothing is clobbered).</summary>
    public async Task ApplyAsync()
    {
        if (!long.TryParse((_setDraft ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v))
            return;
        await _apply(Name, Math.Max(0, v)).ConfigureAwait(false);
    }
}

/// <summary>One row on the Viewers ▸ Ledger list (display-only banking audit).</summary>
public sealed class LoyaltyLedgerRowVm
{
    public LoyaltyLedgerRowVm(LoyaltyLedgerEntry e)
    {
        Time = e.Time;
        Recipient = e.Recipient;
        Sender = string.IsNullOrEmpty(e.Sender) ? "—" : e.Sender;
        Amount = e.Amount;
        Reason = e.Reason;
    }

    public string Time { get; }
    public string Recipient { get; }
    public string Sender { get; }
    public long Amount { get; }
    public string Reason { get; }

    public string AmountLabel => (Amount >= 0 ? "+" : "") + Amount.ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>One row on the Viewers ▸ Leaderboard list (display-only, overlay preview).</summary>
public sealed class LoyaltyStandingRowVm
{
    private const double MinBarPercent = 2.0;

    public LoyaltyStandingRowVm(LoyaltyStanding s, long maxBalance = 0)
    {
        Rank = s.Rank;
        Name = s.Name;
        Balance = s.Balance;

        double pct = maxBalance <= 0 ? MinBarPercent : (double)Balance / maxBalance * 100.0;
        BarWeight = Math.Max(MinBarPercent, pct);
        RestWeight = Math.Max(0.0, 100.0 - BarWeight);
        BarBrush = LoyaltyBrushes.HorizontalEmberFade(0x10);
    }

    public int Rank { get; }
    public string Name { get; }
    public long Balance { get; }

    public string RankLabel => "#" + Rank.ToString(CultureInfo.InvariantCulture);
    public string BalanceLabel => Balance.ToString("N0", CultureInfo.InvariantCulture);

    // Per-row ember weight bar (balance / maxBalance) — mirrors the Giveaway
    // entrant list's proportional design-family bar.
    public double BarWeight { get; }
    public double RestWeight { get; }
    public GridLength BarColumnLength => new(BarWeight, GridUnitType.Star);
    public GridLength RestColumnLength => new(RestWeight, GridUnitType.Star);
    public Brush BarBrush { get; }
}
