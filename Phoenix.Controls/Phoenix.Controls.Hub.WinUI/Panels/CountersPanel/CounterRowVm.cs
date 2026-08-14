using System;
using System.Globalization;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.WinUI.Panels.CountersPanel;

/// <summary>
/// One row in the Counters list — a single <see cref="CounterDef"/> exposed as
/// its editable settings (name, floor-at-zero, step, chat command, cooldown,
/// reply template) plus the two role checkmark sets (view / modify) and a LIVE
/// count readout with set / reset / step controls. Every settings edit mutates
/// the def on the working config and schedules a debounced save through
/// <c>changed</c>; the count controls are driven by the ViewModel (which owns the
/// service) and write the resulting value back via <see cref="SetCount"/>.
///
/// Since the master-detail rebuild the same instance backs BOTH surfaces: the
/// compact roster row in the 0.9* column (index / name / !cmd / count, plus the
/// <see cref="IsSelected"/> wash) and the full editor in the 1.4* detail column,
/// which x:Binds through the ViewModel's SelectedCounter. There is deliberately
/// only one instance per counter — the editor must stay bound to the very object
/// the roster row highlights, or a mid-edit draft would live on a different VM
/// than the one the list shows.
/// </summary>
public sealed class CounterRowVm : ObservableObject
{
    private readonly CounterDef _def;
    private readonly Action _changed;
    private long _count;
    private string _setValueText = "0";
    private bool _isSelected;
    private string _indexText = "";

    public CounterRowVm(CounterDef def, Action changed)
    {
        _def = def ?? throw new ArgumentNullException(nameof(def));
        _changed = changed ?? (static () => { });
        ViewRoles   = new CounterRolesVm(_def.ViewRoles   ??= CounterRoles.All(),  _changed);
        ModifyRoles = new CounterRolesVm(_def.ModifyRoles ??= CounterRoles.Mods(), _changed);
    }

    /// <summary>The backing model row — used by the ViewModel for add/remove and count ops.</summary>
    internal CounterDef Def => _def;

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
            Raise(nameof(NameEyebrow));
            Raise(nameof(CommandBadgeText));
            Raise(nameof(EffectiveCommandHint));
            _changed();
        }
    }

    // ── Header-band projections (display-only; the editable Name TextBox stays) ──
    public string NameEyebrow
        => string.IsNullOrWhiteSpace(_def.Name)
            ? Localizer.T("panel.counters.roster.row.unnamed", "UNNAMED COUNTER")
            : _def.Name.Trim().ToUpperInvariant();

    /// <summary>Compact "!cmd" badge — EffectiveCommand falls back to Name.</summary>
    public string CommandBadgeText
    {
        get
        {
            string cmd = _def.EffectiveCommand ?? "";
            return cmd.Length == 0 ? "!—" : "!" + cmd;
        }
    }

    // ── Roster-row state (0.9* column only) ─────────────────────────────
    /// <summary>1-based position in the roster, assigned by the ViewModel.</summary>
    public string IndexText
    {
        get => _indexText;
        private set => Set(ref _indexText, value);
    }

    internal void SetIndex(int oneBased)
        => IndexText = oneBased.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether this row is the one the detail column is editing. Set only by the
    /// ViewModel's SelectedCounter setter, so exactly one row can carry it.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set { if (Set(ref _isSelected, value)) Raise(nameof(RowBackground)); }
    }

    /// <summary>
    /// The selected row's wash. Null when unselected — legal here because the row
    /// template's hit-testable host is the enclosing Button, not this Grid, so a
    /// null Background costs no pointer input (the null-Background hit-test trap
    /// applies to elements that ARE the click target).
    /// </summary>
    public Brush? RowBackground
        => _isSelected ? ToolBrushes.HorizontalEmberFade(0x24, 0x06) : null;

    public bool FloorAtZero
    {
        get => _def.FloorAtZero;
        set
        {
            if (_def.FloorAtZero == value) return;
            _def.FloorAtZero = value;
            Raise(nameof(FloorAtZero));
            Raise(nameof(FloorAtZeroLabel));
            _changed();
        }
    }

    /// <summary>
    /// The FLOOR pill's own label. A TogglePill is display-only and carries no
    /// On/Off content of its own, so the state word has to come from the VM —
    /// the field label ("Floor at zero") is beside it and says what it governs.
    /// </summary>
    public string FloorAtZeroLabel => _def.FloorAtZero
        ? Localizer.T("panel.counters.settings.floor.state.on", "ON")
        : Localizer.T("panel.counters.settings.floor.state.off", "OFF");

    public string StepText
    {
        get => _def.Step.ToString(CultureInfo.InvariantCulture);
        set
        {
            int v = ParseInt(value, _def.Step);
            if (v == 0) v = 1;                 // 0 step is meaningless; snap to 1
            if (_def.Step == v) { Raise(nameof(StepText)); return; }
            _def.Step = v;
            Raise(nameof(StepText));
            _changed();
        }
    }

    public bool ChatEnabled
    {
        get => _def.ChatEnabled;
        set
        {
            if (_def.ChatEnabled == value) return;
            _def.ChatEnabled = value;
            Raise(nameof(ChatEnabled));
            Raise(nameof(ChatEnabledLabel));
            _changed();
        }
    }

    /// <summary>The chat-command pill's state word (see <see cref="FloorAtZeroLabel"/>).</summary>
    public string ChatEnabledLabel => _def.ChatEnabled
        ? Localizer.T("panel.counters.settings.chat_enabled.state.on", "ON")
        : Localizer.T("panel.counters.settings.chat_enabled.state.off", "OFF");

    public string Command
    {
        get => _def.Command ?? "";
        set
        {
            var v = value ?? "";
            if ((_def.Command ?? "") == v) return;
            _def.Command = v;
            Raise(nameof(Command));
            Raise(nameof(CommandBadgeText));
            Raise(nameof(EffectiveCommandHint));
            _changed();
        }
    }

    public string CooldownText
    {
        get => _def.CooldownSeconds.ToString(CultureInfo.InvariantCulture);
        set
        {
            int v = Math.Max(0, ParseInt(value, _def.CooldownSeconds));
            if (_def.CooldownSeconds == v) { Raise(nameof(CooldownText)); return; }
            _def.CooldownSeconds = v;
            Raise(nameof(CooldownText));
            _changed();
        }
    }

    public string ReplyTemplate
    {
        get => _def.ReplyTemplate ?? "";
        set
        {
            var v = value ?? "";
            if ((_def.ReplyTemplate ?? "") == v) return;
            _def.ReplyTemplate = v;
            Raise(nameof(ReplyTemplate));
            _changed();
        }
    }

    public CounterRolesVm ViewRoles { get; }
    public CounterRolesVm ModifyRoles { get; }

    /// <summary>The command word the chat parser matches (falls back to Name).</summary>
    public string EffectiveCommandHint
    {
        get
        {
            string cmd = _def.EffectiveCommand ?? "";
            // NOT localized, deliberately: the "set" / "reset" here are the exact
            // literals ScriptManager.Counters concatenates onto the command word
            // (Eq(token, "set" + cmd)), so a translated fragment would advertise a
            // verb chat does not answer to. Only the unnamed branch is prose.
            return cmd.Length == 0
                ? Localizer.T("panel.counters.settings.command.unnamed_hint", "(name a counter)")
                : $"!{cmd}  ·  !{cmd}+  ·  !{cmd}-  ·  !set{cmd} N  ·  !reset{cmd}";
        }
    }

    // ── Live count (driven by the ViewModel via the service) ─────────────
    public long Count => _count;
    public string CountText => _count.ToString(CultureInfo.InvariantCulture);

    public string SetValueText
    {
        get => _setValueText;
        set { _setValueText = value ?? ""; Raise(nameof(SetValueText)); }
    }

    /// <summary>Pushes a fresh count from the service into the readout (UI thread).</summary>
    internal void SetCount(long value)
    {
        if (_count == value) return;
        _count = value;
        Raise(nameof(Count));
        Raise(nameof(CountText));
    }

    internal long ParsedSetValue => ParseLong(_setValueText, 0);
    internal int ParsedStep => _def.Step == 0 ? 1 : _def.Step;

    private static int ParseInt(string? s, int fallback)
        => int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static long ParseLong(string? s, long fallback)
        => long.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
