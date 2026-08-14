using System;
using System.Globalization;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Models;
using Windows.UI;

namespace Phoenix.Controls.Hub.WinUI.Panels.SchedulingPanel;

/// <summary>
/// One row in the Scheduling list — a single <see cref="ScheduleItem"/> exposed as
/// its editable settings (name, enabled slider, message, interval-in-minutes,
/// min-chat-lines gate) plus a read-only "posted N×" runtime readout. Every
/// settings edit mutates the def on the working config and schedules a debounced
/// save through <c>changed</c>; the fire count is driven by the ViewModel (which
/// owns the service) and pushed in via <see cref="SetFireCount"/>. Mirrors
/// <c>CounterRowVm</c> exactly (numeric fields are guarded string wrappers over the
/// backing int, like CounterRowVm.StepText / CooldownText).
/// </summary>
public sealed class ScheduleRowVm : ObservableObject
{
    // Selected-row tint — the DEFAULT-badge fill the house uses for a picked row
    // (#14 alpha over EmberPrimary). Literal because it is an alpha ramp with no
    // theme key of its own; the unselected state paints nothing at all so the
    // roster keeps the column's own ground.
    private static readonly Brush SelectedRowFill =
        new SolidColorBrush(Color.FromArgb(0x14, 0xE5, 0xA2, 0x4E));
    private static readonly Brush UnselectedRowFill =
        new SolidColorBrush(Color.FromArgb(0x00, 0x00, 0x00, 0x00));

    private readonly ScheduleItem _def;
    private readonly Action _changed;
    private int _fireCount;
    private bool _isSelected;

    public ScheduleRowVm(ScheduleItem def, Action changed)
    {
        _def = def ?? throw new ArgumentNullException(nameof(def));
        _changed = changed ?? (static () => { });
    }

    /// <summary>The backing model row — used by the ViewModel for add/remove and snapshotting.</summary>
    internal ScheduleItem Def => _def;

    /// <summary>Stable identity — the key the ViewModel uses to read this schedule's
    /// ephemeral runtime state (GetFireCount). Never edited in the UI.</summary>
    internal string Id => _def.Id ?? "";

    // ── Settings (mutate the def + schedule a debounced save) ────────────
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

    public bool Enabled
    {
        get => _def.Enabled;
        set { if (_def.Enabled == value) return; _def.Enabled = value; Raise(nameof(Enabled)); _changed(); }
    }

    public string Message
    {
        get => _def.Message ?? "";
        set
        {
            var v = value ?? "";
            if ((_def.Message ?? "") == v) return;
            _def.Message = v;
            Raise(nameof(Message));
            Raise(nameof(TitleEyebrow));
            _changed();
        }
    }

    /// <summary>Fire cadence in whole minutes. Guarded non-negative; the runtime clamps
    /// to a floor of 1, so we let the user type freely (including 0) and keep it an int.</summary>
    public int IntervalMinutes
    {
        get => _def.IntervalMinutes;
        set
        {
            int v = Math.Max(0, value);
            if (_def.IntervalMinutes == v) { Raise(nameof(IntervalMinutes)); Raise(nameof(IntervalMinutesText)); return; }
            _def.IntervalMinutes = v;
            Raise(nameof(IntervalMinutes));
            Raise(nameof(IntervalMinutesText));
            _changed();
        }
    }

    /// <summary>String view over <see cref="IntervalMinutes"/> for the row TextBox
    /// (mirrors CounterRowVm.StepText). Unparseable / negative input snaps back via
    /// the int setter's guard + PropertyChanged.</summary>
    public string IntervalMinutesText
    {
        get => _def.IntervalMinutes.ToString(CultureInfo.InvariantCulture);
        set => IntervalMinutes = ParseInt(value, _def.IntervalMinutes);
    }

    /// <summary>StreamElements-style dead-chat gate (0 = off). Guarded &gt;= 0.</summary>
    public int MinChatLines
    {
        get => _def.MinChatLines;
        set
        {
            int v = Math.Max(0, value);
            if (_def.MinChatLines == v) { Raise(nameof(MinChatLines)); Raise(nameof(MinChatLinesText)); return; }
            _def.MinChatLines = v;
            Raise(nameof(MinChatLines));
            Raise(nameof(MinChatLinesText));
            _changed();
        }
    }

    public string MinChatLinesText
    {
        get => _def.MinChatLines.ToString(CultureInfo.InvariantCulture);
        set => MinChatLines = ParseInt(value, _def.MinChatLines);
    }

    // ── Header-band projection (display-only; the editable Name TextBox stays) ──
    public string TitleEyebrow
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_def.Name)) return _def.Name.Trim().ToUpperInvariant();
            var msg = (_def.Message ?? "").Trim();
            if (msg.Length == 0) return "UNNAMED SCHEDULE";
            return msg.Length <= 32
                ? msg.ToUpperInvariant()
                : msg.Substring(0, 32).ToUpperInvariant() + "…";
        }
    }

    // ── Roster selection (owned by the ViewModel; ItemsRepeater has no selection
    //    model of its own, so the picked row is host state, never list state) ──
    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            Raise(nameof(IsSelected));
            Raise(nameof(RowBackground));
        }
    }

    public Brush RowBackground => _isSelected ? SelectedRowFill : UnselectedRowFill;

    // ── Runtime readout (driven by the ViewModel via the service) ────────
    public int FireCount => _fireCount;
    public string FireCountText => "posted " + _fireCount.ToString(CultureInfo.InvariantCulture) + "×";

    /// <summary>Pushes a fresh fire count from the service into the readout (UI thread only).</summary>
    internal void SetFireCount(int value)
    {
        if (_fireCount == value) return;
        _fireCount = value;
        Raise(nameof(FireCount));
        Raise(nameof(FireCountText));
    }

    /// <summary>A fresh, detached <see cref="ScheduleItem"/> snapshot for the save path —
    /// UpdateConfigAsync deep-owns whatever it is handed, so the working defs must not
    /// be shared with the pushed config.</summary>
    internal ScheduleItem ToSnapshot() => new ScheduleItem
    {
        Id = _def.Id ?? "",
        Name = _def.Name ?? "",
        Enabled = _def.Enabled,
        Message = _def.Message ?? "",
        IntervalMinutes = _def.IntervalMinutes,
        MinChatLines = _def.MinChatLines,
    };

    private static int ParseInt(string? s, int fallback)
        => int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
