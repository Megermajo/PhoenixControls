using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.WinUI.Panels.AlertsPanel;

/// <summary>
/// One size tier inside an alert event card — a single <see cref="AlertTier"/>
/// exposed as its editable settings (enabled slider, "From size" threshold,
/// chat message, optional overlay layer + trigger hookup) plus the mini-band
/// eyebrow projection ("FROM SIZE 5"). Every edit mutates the def on the
/// working config and schedules a debounced save through <c>changed</c>;
/// <see cref="Delete"/> asks the owning event card to drop this row. Numeric
/// input is a guarded string wrapper over the backing int, mirroring
/// <c>ScheduleRowVm.IntervalMinutesText</c>.
/// </summary>
public sealed class AlertTierRowVm : ObservableObject
{
    private readonly AlertTier _def;
    private readonly Action _changed;
    private readonly Action<AlertTierRowVm> _delete;
    private readonly Func<string, IReadOnlyList<string>> _triggersFor;

    public AlertTierRowVm(
        AlertTier def,
        Action changed,
        Action<AlertTierRowVm> delete,
        Func<string, IReadOnlyList<string>> triggersFor,
        ObservableCollection<string> layerOptions)
    {
        _def = def ?? throw new ArgumentNullException(nameof(def));
        _changed = changed ?? (static () => { });
        _delete = delete ?? (static _ => { });
        _triggersFor = triggersFor ?? (static _ => Array.Empty<string>());
        LayerOptions = layerOptions ?? new ObservableCollection<string>();
    }

    /// <summary>The backing model row — used by the owning card for snapshotting.</summary>
    internal AlertTier Def => _def;

    // ── Picker item sources ──────────────────────────────────────────────
    // LayerOptions is the SHARED instance owned by AlertsViewModel — one
    // RefreshLayers() updates every open layer picker on the page.
    public ObservableCollection<string> LayerOptions { get; }

    // TriggerOptions is per-row (it depends on this row's selected layer);
    // refreshed lazily on drop-down open / layer change / the ↻ button.
    public ObservableCollection<string> TriggerOptions { get; } = new();

    // ── Settings (mutate the def + schedule a debounced save) ────────────
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

    /// <summary>Caption beside the tier's TogglePill. The pill replaced a
    /// ToggleSwitch whose On/Off content carried this word for free.</summary>
    public string EnabledLabel => _def.Enabled
        ? Localizer.T("panel.alerts.tier.toggle.on", "ON")
        : Localizer.T("panel.alerts.tier.toggle.off", "OFF");

    /// <summary>Minimum event size for this tier (inclusive; 0 = catch-all).
    /// Guarded non-negative — the highest enabled MinValue &lt;= the event size wins.</summary>
    public int MinValue
    {
        get => _def.MinValue;
        set
        {
            int v = Math.Max(0, value);
            if (_def.MinValue == v) { Raise(nameof(MinValue)); Raise(nameof(MinValueText)); return; }
            _def.MinValue = v;
            Raise(nameof(MinValue));
            Raise(nameof(MinValueText));
            Raise(nameof(TitleEyebrow));
            _changed();
        }
    }

    /// <summary>String view over <see cref="MinValue"/> for the row TextBox
    /// (mirrors ScheduleRowVm.IntervalMinutesText). Unparseable / negative input
    /// snaps back via the int setter's guard + PropertyChanged.</summary>
    public string MinValueText
    {
        get => _def.MinValue.ToString(CultureInfo.InvariantCulture);
        set => MinValue = ParseInt(value, _def.MinValue);
    }

    /// <summary>Mini-band eyebrow — "FROM SIZE 5".</summary>
    public string TitleEyebrow => string.Format(
        CultureInfo.InvariantCulture,
        Localizer.T("panel.alerts.tier.eyebrow", "FROM SIZE {0}"),
        _def.MinValue.ToString(CultureInfo.InvariantCulture));

    // ── Reachability cue (computed by the owning card, never by this row) ─
    private bool _isUnreachable;

    /// <summary>True when an earlier ENABLED tier already claims this "From size".
    /// AlertsService.PickTier resolves ties to the first tier in list order, so such
    /// a row can never fire and the streamer gets no other signal. Purely a PASSIVE
    /// inline caption (<see cref="UnreachableVisibility"/>): the value is never
    /// rejected, the save is never blocked — mid-edit collisions are normal while
    /// typing a new threshold.</summary>
    public bool IsUnreachable => _isUnreachable;

    public Visibility UnreachableVisibility => _isUnreachable ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Set by <c>AlertEventVm.RefreshTierReachability</c> after any change
    /// to the sibling set. UI thread only.</summary>
    internal void SetUnreachable(bool value)
    {
        if (_isUnreachable == value) return;
        _isUnreachable = value;
        Raise(nameof(IsUnreachable));
        Raise(nameof(UnreachableVisibility));
    }

    /// <summary>The chat line for this tier. Empty = the tier posts nothing
    /// (visual-only tier).</summary>
    public string Message
    {
        get => _def.Message ?? "";
        set
        {
            var v = value ?? "";
            if ((_def.Message ?? "") == v) return;
            _def.Message = v;
            Raise(nameof(Message));
            _changed();
        }
    }

    /// <summary>Optional overlay hookup — the layer id (free text or picked from
    /// the registered-layer list). Changing it re-derives the trigger options.</summary>
    public string LayerId
    {
        get => _def.LayerId ?? "";
        set
        {
            var v = value ?? "";
            if ((_def.LayerId ?? "") == v) return;
            _def.LayerId = v;
            Raise(nameof(LayerId));
            RefreshTriggerOptions();
            _changed();
        }
    }

    /// <summary>Optional overlay hookup — the trigger name on the selected layer.</summary>
    public string TriggerName
    {
        get => _def.TriggerName ?? "";
        set
        {
            var v = value ?? "";
            if ((_def.TriggerName ?? "") == v) return;
            _def.TriggerName = v;
            Raise(nameof(TriggerName));
            _changed();
        }
    }

    /// <summary>Re-derives <see cref="TriggerOptions"/> from the currently selected
    /// layer (deduped union across the layer's widgets, provided by the page VM).
    /// No-ops when the list is already current so an open drop-down isn't churned.
    /// UI thread only.</summary>
    public void RefreshTriggerOptions()
    {
        IReadOnlyList<string> names = _triggersFor(_def.LayerId ?? "");
        bool same = names.Count == TriggerOptions.Count;
        if (same)
        {
            for (int i = 0; i < names.Count; i++)
            {
                if (!string.Equals(names[i], TriggerOptions[i], StringComparison.Ordinal)) { same = false; break; }
            }
        }
        if (same) return;
        TriggerOptions.Clear();
        foreach (var n in names) TriggerOptions.Add(n);
    }

    public void Delete() => _delete(this);

    /// <summary>A fresh, detached <see cref="AlertTier"/> snapshot for the save path —
    /// UpdateConfigAsync deep-owns whatever it is handed, so the working defs must
    /// never be shared with the pushed config.</summary>
    internal AlertTier ToSnapshot() => new AlertTier
    {
        Id = _def.Id ?? "",
        Enabled = _def.Enabled,
        MinValue = _def.MinValue,
        Message = _def.Message ?? "",
        LayerId = _def.LayerId ?? "",
        TriggerName = _def.TriggerName ?? "",
    };

    private static int ParseInt(string? s, int fallback)
        => int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
