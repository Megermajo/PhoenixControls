using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.WinUI.Panels.PollsPanel;

/// <summary>
/// One row of the live result list — a read-only projection of a
/// <see cref="PollOptionView"/>. Read-only on purpose: the poll's options are fixed when it
/// opens, so there is nothing here to edit, and the row exists to be REPLACED as the
/// snapshot moves rather than to hold in-progress input. That is why it carries no
/// <c>ScheduleSave</c> delegate, unlike every other <c>*RowVm</c> in the tool family.
/// </summary>
public sealed class PollOptionRowVm : ObservableObject
{
    // The result rail is a two-column star Grid behind the row, not a ProgressBar: it is
    // the house meter (GiveawayEntrantRowVm's weight bar) and it is the only meter shape
    // this suite draws. The 2% floor is that row's MinBarPercent verbatim — an option with
    // one vote against a landslide still shows a sliver rather than nothing, because "no
    // bar at all" and "no votes at all" must not render identically.
    private const double MinBarPercent = 2.0;

    // Allocated once for the whole page rather than per row: a poll is rebuilt every time
    // its label:votes:stake signature moves, so a per-row brush would churn a gradient
    // allocation for every vote that lands.
    private static readonly Brush SharedBarBrush = ToolBrushes.HorizontalEmberFade(0x2E);

    public PollOptionRowVm(PollOptionView option, bool betting)
    {
        Index = option.Index;
        Label = option.Label;
        Votes = option.Votes;
        Stake = option.Stake;
        Percent = option.Percent;
        ShowStake = betting;

        BarWeight = Math.Max(MinBarPercent, Percent);
        RestWeight = Math.Max(0.0, 100.0 - BarWeight);
    }

    public int Index { get; }
    public string Label { get; }
    public int Votes { get; }
    public long Stake { get; }
    public double Percent { get; }
    public bool ShowStake { get; }

    /// <summary>The number a viewer types — options are 1-based in chat because viewers
    /// count from one, while every index in the code is 0-based.</summary>
    public string NumberText => (Index + 1).ToString(CultureInfo.InvariantCulture);

    public string VotesText => Votes.ToString(CultureInfo.InvariantCulture);
    public string PercentText => Percent.ToString("0.#", CultureInfo.InvariantCulture) + "%";

    /// <summary>Blank rather than "0" when this poll takes no bets, so a plain poll's row
    /// does not carry a money column that means nothing.</summary>
    public string StakeText => ShowStake ? Stake.ToString(CultureInfo.InvariantCulture) : "";

    // ── Result rail (the converted ProgressBar) ──────────────────────────
    /// <summary>Star weight of the filled part of the rail, floored at 2%.</summary>
    public double BarWeight { get; }

    /// <summary>Star weight of the unfilled remainder.</summary>
    public double RestWeight { get; }

    public GridLength BarColumnLength => new(BarWeight, GridUnitType.Star);

    public GridLength RestColumnLength => new(RestWeight, GridUnitType.Star);

    /// <summary>Left-to-right ember fade painted into the filled column.</summary>
    public Brush BarBrush => SharedBarBrush;
}
