using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.WinUI.Panels.AlertsPanel;

/// <summary>
/// One alert event family (Follow / Sub / Gift Sub / Gift Bomb / Bits / Raid /
/// Charity / Shoutout Received / Watch Streak / Tip) — a single
/// <see cref="AlertEventConfig"/> exposed as its roster row (enable pill, title,
/// tier-count badge, selection wash) plus everything the 1.4* detail column
/// shows for the SELECTED family: the per-family token instruction strings, the
/// raid-only auto-shoutout, and the editable tier list. The family enable and
/// the raid auto-shoutout save immediately via <c>saveNow</c>; tier edits funnel
/// through the debounced <c>changed</c>.
/// </summary>
public sealed class AlertEventVm : ObservableObject
{
    private readonly AlertEventConfig _def;
    private readonly Action _changed;
    private readonly Action _saveNow;
    private readonly Func<string, IReadOnlyList<string>> _triggersFor;
    private readonly bool _showAutoShoutout;

    public AlertEventVm(
        string familyKey,
        string title,
        string iconGlyph,
        string amountHint,
        bool showAutoShoutout,
        AlertEventConfig def,
        ObservableCollection<string> layerOptions,
        Func<string, IReadOnlyList<string>> triggersFor,
        Action changed,
        Action saveNow)
    {
        FamilyKey = familyKey ?? throw new ArgumentNullException(nameof(familyKey));
        Title = title ?? "";
        IconGlyph = iconGlyph ?? "";
        AmountHint = amountHint ?? "";
        _showAutoShoutout = showAutoShoutout;
        _def = def ?? throw new ArgumentNullException(nameof(def));
        LayerOptions = layerOptions ?? new ObservableCollection<string>();
        _triggersFor = triggersFor ?? (static _ => Array.Empty<string>());
        _changed = changed ?? (static () => { });
        _saveNow = saveNow ?? (static () => { });

        _def.Tiers ??= new List<AlertTier>();
        foreach (var tier in _def.Tiers)
        {
            if (tier is null) continue;
            Tiers.Add(new AlertTierRowVm(tier, OnRowChanged, RemoveTier, _triggersFor, LayerOptions));
        }
        RefreshTierReachability();
    }

    /// <summary>Which AlertsConfig property this card projects ("Follow" …
    /// "Raid") — the key <c>AlertsViewModel.BuildConfig</c> switches on.</summary>
    internal string FamilyKey { get; }

    // ── Immutable card chrome (OneTime binds) ────────────────────────────
    public string Title { get; }
    public string IconGlyph { get; }

    /// <summary>Line (a) of the in-card instruction strip — the full token list.
    /// Keep in lockstep with AlertsService.ResolveTokens.</summary>
    public string TokenLine => Localizer.T(
        "panel.alerts.family.tokens_hint",
        "Tokens: {user} {amount} {currency} {source} {tier} {months} {gifter} {recipient} {count} {viewers} {bits} {message} {platform}");

    /// <summary>Line (b) of the instruction strip — the per-family explanation of
    /// what {amount} means for this event.</summary>
    public string AmountHint { get; }

    // ── Picker plumbing (shared with the tier rows) ──────────────────────
    public ObservableCollection<string> LayerOptions { get; }

    // ── Family settings ──────────────────────────────────────────────────
    /// <summary>Family on/off (the header-band checkbox). Saves immediately —
    /// a family enable is a toggle, not a keystroke.</summary>
    public bool Enabled
    {
        get => _def.Enabled;
        set
        {
            if (_def.Enabled == value) return;
            _def.Enabled = value;
            Raise(nameof(Enabled));
            Raise(nameof(EnabledLabel));
            _saveNow();
        }
    }

    /// <summary>Caption beside the detail column's family TogglePill. (The roster
    /// row's pill is label-less — the family name is already the row.)</summary>
    public string EnabledLabel => _def.Enabled
        ? Localizer.T("panel.alerts.family.toggle.on", "ON")
        : Localizer.T("panel.alerts.family.toggle.off", "OFF");

    /// <summary>Automatic Twitch shoutout for the raider — surfaced on the RAID
    /// card only (<see cref="AutoShoutoutVisibility"/>). Saves immediately.</summary>
    public bool AutoShoutout
    {
        get => _def.AutoShoutout;
        set
        {
            if (_def.AutoShoutout == value) return;
            _def.AutoShoutout = value;
            Raise(nameof(AutoShoutout));
            Raise(nameof(AutoShoutoutLabel));
            _saveNow();
        }
    }

    public string AutoShoutoutLabel => _def.AutoShoutout
        ? Localizer.T("panel.alerts.family.auto_shoutout.on", "ON")
        : Localizer.T("panel.alerts.family.auto_shoutout.off", "OFF");

    public Visibility AutoShoutoutVisibility => _showAutoShoutout ? Visibility.Visible : Visibility.Collapsed;

    // ── Tier list ────────────────────────────────────────────────────────
    public ObservableCollection<AlertTierRowVm> Tiers { get; } = new();

    public bool HasTiers => Tiers.Count > 0;
    public Visibility EmptyVisibility => HasTiers ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Header-band badge — "2 tiers" (total configured, enabled or not).</summary>
    public string TierCountText
        => string.Format(
            CultureInfo.InvariantCulture,
            Tiers.Count == 1
                ? Localizer.T("panel.alerts.roster.tier_count.one", "{0} tier")
                : Localizer.T("panel.alerts.roster.tier_count.other", "{0} tiers"),
            Tiers.Count.ToString(CultureInfo.InvariantCulture));

    /// <summary>Total tier count feeding the page's "TIERS CONFIGURED" stat tile —
    /// counts every configured tier (enabled or not), matching the caption and
    /// the per-card badge.</summary>
    internal int ConfiguredTierCount => Tiers.Count;

    /// <summary>Appends a fresh tier — MinValue 0 for the first, max existing + 1
    /// after that, Enabled true — and schedules a debounced save.</summary>
    public void AddTier()
    {
        int min = 0;
        foreach (var row in Tiers)
            if (row.MinValue >= min)
                min = row.MinValue == int.MaxValue ? int.MaxValue : row.MinValue + 1;
        var def = new AlertTier
        {
            Id = Guid.NewGuid().ToString("N"),
            Enabled = true,
            MinValue = min,
            Message = "",
            LayerId = "",
            TriggerName = "",
        };
        Tiers.Add(new AlertTierRowVm(def, OnRowChanged, RemoveTier, _triggersFor, LayerOptions));
        RaiseTierShape();
        OnRowChanged();
    }

    private void RemoveTier(AlertTierRowVm row)
    {
        if (row is null) return;
        if (!Tiers.Remove(row)) return;
        RaiseTierShape();
        OnRowChanged();
    }

    // Every row edit funnels through here so the card can re-run the reachability
    // pass (a row's own "From size" / enable change decides whether IT or a sibling
    // is now shadowed) before scheduling the page's debounced save.
    private void OnRowChanged()
    {
        RefreshTierReachability();
        _changed();
    }

    /// <summary>Marks the tiers that can never fire: AlertsService.PickTier keeps the
    /// FIRST enabled tier at a given MinValue, so any later row sharing that threshold
    /// is dead. Disabled rows neither claim a threshold nor get flagged — they are off
    /// for a reason. PASSIVE ONLY — this drives a muted inline caption; nothing here
    /// rewrites a value, rejects an edit or blocks the save.</summary>
    private void RefreshTierReachability()
    {
        var claimed = new HashSet<int>();
        foreach (var row in Tiers)
            row.SetUnreachable(row.Enabled && !claimed.Add(row.MinValue));
    }

    private void RaiseTierShape()
    {
        Raise(nameof(HasTiers));
        Raise(nameof(EmptyVisibility));
        Raise(nameof(TierCountText));
    }

    // ── Roster selection state ───────────────────────────────────────────
    // Replaces the per-card expand trio (IsExpanded / body visibility / chevron)
    // the page carried while every family was a collapsible card stacked in one
    // column. Since the master-detail rebuild the ten families are compact rows
    // in the 0.9* roster and exactly one of them is OPEN in the 1.4* detail
    // column, so "expanded" collapsed into "selected".
    //
    // ⚠ BEHAVIOUR CHANGE, flagged on delivery: two families can no longer be
    // opened side by side for comparison. Nothing else was lost — every control
    // that lived in the card body now lives in the detail column.
    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        internal set { if (Set(ref _isSelected, value)) Raise(nameof(RowBackground)); }
    }

    /// <summary>
    /// The selected roster row's wash. Null when unselected — legal because the
    /// row's hit-testable elements are the two Buttons inside it, not the Grid
    /// that carries this brush (the null-Background hit-test trap only applies
    /// to elements that ARE the click target).
    /// </summary>
    public Brush? RowBackground
        => _isSelected ? ToolBrushes.HorizontalEmberFade(0x24, 0x06) : null;

    /// <summary>A fresh, detached <see cref="AlertEventConfig"/> for the save path —
    /// the live rows are the source of truth, never shared with the pushed config.</summary>
    internal AlertEventConfig ToSnapshot()
    {
        var cfg = new AlertEventConfig
        {
            Enabled = _def.Enabled,
            AutoShoutout = _def.AutoShoutout,
            Tiers = new List<AlertTier>(Tiers.Count),
        };
        foreach (var row in Tiers)
            cfg.Tiers.Add(row.ToSnapshot());
        return cfg;
    }
}
