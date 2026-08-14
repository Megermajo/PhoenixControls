using System;
using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.WinUI.Panels.RanksPanel;

/// <summary>
/// One rung of the rank ladder — a single <see cref="RankDef"/> exposed as its editable
/// settings (enabled toggle, name, threshold, optional User-Management group grant) plus
/// the mini-band eyebrow projection. Every edit mutates the def on the working config and
/// schedules a debounced save through <c>changed</c>; <see cref="Delete"/> asks the page to
/// drop this row. Numeric input is a guarded string wrapper over the backing long,
/// mirroring <c>AlertTierRowVm.MinValueText</c>.
/// </summary>
public sealed class RankRowVm : ObservableObject
{
    private readonly RankDef _def;
    private readonly Action _changed;
    private readonly Action<RankRowVm> _delete;
    private readonly Func<string> _unit;

    public RankRowVm(
        RankDef def,
        Action changed,
        Action<RankRowVm> delete,
        Func<string> unitProvider,
        ObservableCollection<string> groupOptions)
    {
        _def = def ?? throw new ArgumentNullException(nameof(def));
        _changed = changed ?? (static () => { });
        _delete = delete ?? (static _ => { });
        _unit = unitProvider ?? (static () => "");
        GroupOptions = groupOptions ?? new ObservableCollection<string>();
    }

    /// <summary>The backing model row — used by the page for snapshotting.</summary>
    internal RankDef Def => _def;

    // The SHARED instance owned by RanksViewModel: one refresh updates every open picker
    // on the page. The picker is EDITABLE (the AlertsView layer-id idiom) so the list is a
    // convenience rather than a cage — but it is filled from the live User-Management group
    // list, so the ordinary path is picking a group that exists. A name that matches
    // nothing is a logged no-op on the grant side, never a silently invented group.
    public ObservableCollection<string> GroupOptions { get; }

    public bool Enabled
    {
        get => _def.Enabled;
        set
        {
            if (_def.Enabled == value) return;
            _def.Enabled = value;
            Raise(nameof(Enabled));
            Raise(nameof(EnabledLabel));
            _changed();
        }
    }

    /// <summary>The word beside the rung's toggle pill. The pill is display-only, so the
    /// ON / OFF word the stock ToggleSwitch used to render is the row's to supply — without
    /// it a disabled rung reads only as an unlit track.</summary>
    public string EnabledLabel => _def.Enabled
        ? Localizer.T("panel.ranks.row.enabled.on", "ON")
        : Localizer.T("panel.ranks.row.enabled.off", "OFF");

    public string Name
    {
        get => _def.Name ?? "";
        set
        {
            var v = value ?? "";
            if ((_def.Name ?? "") == v) return;
            _def.Name = v;
            Raise(nameof(Name));
            Raise(nameof(TitleEyebrow));
            _changed();
        }
    }

    /// <summary>Minimum metric value for this rung (inclusive). Guarded non-negative — the
    /// highest enabled Threshold &lt;= the viewer's value wins.</summary>
    public long Threshold
    {
        get => _def.Threshold;
        set
        {
            long v = Math.Max(0, value);
            if (_def.Threshold == v) { Raise(nameof(ThresholdText)); Raise(nameof(ThresholdHint)); return; }
            _def.Threshold = v;
            Raise(nameof(Threshold));
            Raise(nameof(ThresholdText));
            Raise(nameof(ThresholdHint));
            Raise(nameof(TitleEyebrow));
            _changed();
        }
    }

    /// <summary>String view over <see cref="Threshold"/> for the row TextBox (mirrors
    /// AlertTierRowVm.MinValueText). Unparseable / negative input snaps back via the long
    /// setter's guard + PropertyChanged.</summary>
    public string ThresholdText
    {
        get => _def.Threshold.ToString(CultureInfo.InvariantCulture);
        set => Threshold = ParseLong(value, _def.Threshold);
    }

    /// <summary>The trailing unit word beside the threshold box — "minutes" or the
    /// streamer's currency noun, re-raised by the page when the metric changes so the
    /// number is never shown without saying what it counts.</summary>
    public string UnitLabel => _unit();

    /// <summary>Hours equivalent, shown only on the watch-time metric. Streamers think in
    /// hours and the store counts minutes; rather than hiding a conversion inside the
    /// number, the row keeps ONE honest field and says what it works out to.</summary>
    public string ThresholdHint
    {
        get
        {
            // ★ "minutes" here is a MATCH against RankMetrics.UnitFor's watch-time
            // constant, not a display string — it must never be routed through
            // Localizer or this hint silently stops appearing in every language.
            if (!string.Equals(_unit(), "minutes", StringComparison.OrdinalIgnoreCase)) return "";
            double hours = _def.Threshold / 60.0;
            return string.Format(
                Localizer.T("panel.ranks.row.threshold.hours", "= {0} h"),
                hours.ToString("0.#", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Mini-band eyebrow — "SILVER · FROM 600".</summary>
    public string TitleEyebrow
    {
        get
        {
            // The rank's own name is the streamer's word — upper-cased, never translated.
            // Only the "not named yet" stand-in and the joining phrase are ours.
            string name = string.IsNullOrWhiteSpace(_def.Name)
                ? Localizer.T("panel.ranks.row.eyebrow.unnamed", "UNNAMED")
                : _def.Name.ToUpperInvariant();
            return string.Format(
                Localizer.T("panel.ranks.row.eyebrow.format", "{0} · FROM {1}"),
                name, _def.Threshold.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// The User-Management group this rung grants, or "" for none. Bound to a picker
    /// filled from the live group list, so the value is always a group that exists.
    ///
    /// The grant is additive only — reaching the rung adds the viewer, and nothing here
    /// ever takes the group back. That is deliberate: silently stripping a Moderator right
    /// the moment a viewer spent points is a far worse surprise than a right that outlives
    /// its rank, and the streamer can always remove a member by hand.
    /// </summary>
    public string GrantGroup
    {
        get => _def.GrantGroup ?? "";
        set
        {
            var v = (value ?? "").Trim();
            if ((_def.GrantGroup ?? "") == v) return;
            _def.GrantGroup = v;
            Raise(nameof(GrantGroup));
            _changed();
        }
    }

    // ── Reachability cue (computed by the page, never by this row) ────────
    private string _unreachableText = "";

    /// <summary>True when this rung can never be awarded. TWO causes, and the caption says
    /// which (<see cref="UnreachableText"/>): an earlier ENABLED rung already claims this
    /// threshold — <c>RanksService.Resolve</c> keeps the FIRST rung on a tie — or the
    /// threshold is 0, which no viewer's value can clear because a value of 0 means "nothing
    /// recorded" and is unranked by rule (<c>RanksService.ResolveStanding</c>).
    ///
    /// Purely a PASSIVE inline caption: the value is never rejected and the save is never
    /// blocked — mid-edit collisions are normal while typing a new threshold.</summary>
    public bool IsUnreachable => _unreachableText.Length > 0;

    /// <summary>Why this rung can never be awarded; empty when it can.</summary>
    public string UnreachableText => _unreachableText;

    public Visibility UnreachableVisibility => IsUnreachable ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Set by the page after any change to the sibling set. Empty / null clears
    /// the caption. UI thread only.</summary>
    internal void SetUnreachable(string? reason)
    {
        string v = reason ?? "";
        if (string.Equals(_unreachableText, v, StringComparison.Ordinal)) return;
        _unreachableText = v;
        Raise(nameof(IsUnreachable));
        Raise(nameof(UnreachableText));
        Raise(nameof(UnreachableVisibility));
    }

    /// <summary>Re-raises the unit-dependent projections after the page's metric changed.</summary>
    internal void RaiseUnitChanged()
    {
        Raise(nameof(UnitLabel));
        Raise(nameof(ThresholdHint));
    }

    public void Delete() => _delete(this);

    /// <summary>A fresh, detached <see cref="RankDef"/> snapshot for the save path.</summary>
    internal RankDef ToSnapshot() => new()
    {
        Name = _def.Name ?? "",
        Threshold = _def.Threshold,
        Enabled = _def.Enabled,
        GrantGroup = _def.GrantGroup ?? "",
    };

    private static long ParseLong(string? s, long fallback)
        => long.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
