using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.WinUI.Panels.CustomCommandsPanel;

/// <summary>
/// One row in the Custom Commands list — a single <see cref="CustomCommand"/> exposed
/// as its editable settings (trigger, response template, aliases, counter name, the
/// two cooldowns, enabled toggle) plus the 5-way role checkmark set. Every edit mutates
/// the command on the working config and schedules a debounced save through
/// <c>_changed</c>. Verbatim/trimmed on the text fields (no input validation); only the
/// cooldown fields get numeric-range clamping. Delete is driven by the ViewModel.
///
/// Since the house-shell rebuild the row is rendered TWICE off the same instance: as a
/// compact picker row in the 0.9* roster column (<see cref="TriggerDisplay"/> /
/// <see cref="ResponsePreview"/> / <see cref="EnabledAccentBrush"/> /
/// <see cref="RowBackground"/> plus its enable pill) and, while it is the selected row,
/// as the full editor in the 1.4* detail column. A keystroke in the editor therefore
/// moves the roster preview live.
/// </summary>
public sealed class CommandRowVm : ObservableObject
{
    private readonly CustomCommand _cmd;
    private readonly Action _changed;

    public CommandRowVm(CustomCommand cmd, Action changed)
    {
        _cmd = cmd ?? throw new ArgumentNullException(nameof(cmd));
        _changed = changed ?? (static () => { });
        Roles = new CommandRolesVm(_cmd.Roles ??= CommandRoles.All(), _changed);
    }

    /// <summary>The backing model row — used by the ViewModel for remove.</summary>
    internal CustomCommand Command => _cmd;

    public string Trigger
    {
        get => _cmd.Trigger ?? "";
        set
        {
            var v = value ?? "";
            if ((_cmd.Trigger ?? "") == v) return;
            _cmd.Trigger = v;
            Raise(nameof(Trigger));
            Raise(nameof(TriggerDisplay));
            Raise(nameof(CounterHint));
            _changed();
        }
    }

    public string Response
    {
        get => _cmd.Response ?? "";
        set { var v = value ?? ""; if ((_cmd.Response ?? "") == v) return; _cmd.Response = v; Raise(nameof(Response)); Raise(nameof(ResponsePreview)); _changed(); }
    }

    /// <summary>Comma-separated alias editor over the model's <c>List&lt;string&gt;</c>.</summary>
    public string AliasesText
    {
        get => _cmd.Aliases == null ? "" : string.Join(", ", _cmd.Aliases);
        set
        {
            var parts = (value ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            _cmd.Aliases = parts;
            Raise(nameof(AliasesText));
            _changed();
        }
    }

    public string CounterName
    {
        get => _cmd.CounterName ?? "";
        set { var v = value ?? ""; if ((_cmd.CounterName ?? "") == v) return; _cmd.CounterName = v; Raise(nameof(CounterName)); Raise(nameof(CounterHint)); _changed(); }
    }

    public string UserCooldownText
    {
        get => _cmd.UserCooldownSeconds.ToString(CultureInfo.InvariantCulture);
        set
        {
            int v = Math.Max(0, ParseInt(value, _cmd.UserCooldownSeconds));
            if (_cmd.UserCooldownSeconds == v) { Raise(nameof(UserCooldownText)); return; }
            _cmd.UserCooldownSeconds = v;
            Raise(nameof(UserCooldownText));
            _changed();
        }
    }

    public string GlobalCooldownText
    {
        get => _cmd.GlobalCooldownSeconds.ToString(CultureInfo.InvariantCulture);
        set
        {
            int v = Math.Max(0, ParseInt(value, _cmd.GlobalCooldownSeconds));
            if (_cmd.GlobalCooldownSeconds == v) { Raise(nameof(GlobalCooldownText)); return; }
            _cmd.GlobalCooldownSeconds = v;
            Raise(nameof(GlobalCooldownText));
            _changed();
        }
    }

    public bool Enabled
    {
        get => _cmd.Enabled;
        set { if (_cmd.Enabled == value) return; _cmd.Enabled = value; Raise(nameof(Enabled)); Raise(nameof(EnabledAccentBrush)); _changed(); }
    }

    /// <summary>Card left accent rail — ember when the command is live, divider-coal when off.</summary>
    public Brush EnabledAccentBrush => _cmd.Enabled
        ? (s_accentOn ??= ResolveBrush("EmberPrimaryBrush"))
        : (s_accentOff ??= ResolveBrush("CoalDividerBrush"));

    private static Brush? s_accentOn, s_accentOff;

    // Theme-key lookup with the house CoalFallbackBrush idiom (mirrors
    // LiveFeedRowVm.ResolveBrush) so a missing token degrades quietly.
    private static Brush ResolveBrush(string key)
    {
        if (Application.Current?.Resources is { } res)
        {
            if (res.TryGetValue(key, out var resource) && resource is Brush b) return b;
            if (res.TryGetValue("CoalFallbackBrush", out var fb) && fb is Brush f) return f;
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public CommandRolesVm Roles { get; }

    /// <summary>Which counter {count}/{count.next} target for this command.</summary>
    /// <remarks>A .NET format string, so the literal <c>{count}</c> token is
    /// brace-doubled — the bundle entry carries it doubled too, exactly like
    /// <c>architect.dialog.var_chain.*_header_format</c>. The token itself is engine
    /// syntax and must survive translation verbatim.</remarks>
    public string CounterHint
        => string.Format(
            CultureInfo.CurrentCulture,
            Localizer.T("panel.customcommands.editor.counter_hint", "{{count}} tracks the \"{0}\" counter"),
            _cmd.EffectiveCounterName);

    // ── Roster projections (0.9* picker column) ─────────────────────────

    /// <summary>The chat word as it is typed, "!" included. A command with a blank
    /// trigger is named as such rather than rendered as a bare "!": the service's Find
    /// can never match it, so it is genuinely dead until a word is filled in.</summary>
    public string TriggerDisplay
    {
        get
        {
            string t = (_cmd.Trigger ?? "").Trim();
            return t.Length == 0
                ? Localizer.T("panel.customcommands.roster.no_trigger", "(no command word)")
                : "!" + t;
        }
    }

    /// <summary>Single-line preview of the response for the narrow roster row. The row
    /// trims with an ellipsis, but a multi-line response (which the service sends as
    /// one chat line per line) would otherwise grow the row, so breaks fold to spaces
    /// here.</summary>
    public string ResponsePreview
    {
        get
        {
            string r = (_cmd.Response ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return r.Length == 0
                ? Localizer.T("panel.customcommands.roster.no_response", "(no response yet)")
                : r;
        }
    }

    private bool _isSelected;

    /// <summary>True while this row is the one the detail column is editing.</summary>
    public bool IsSelected => _isSelected;

    internal void SetSelected(bool value)
    {
        if (_isSelected == value) return;
        _isSelected = value;
        Raise(nameof(IsSelected));
        Raise(nameof(RowBackground));
    }

    /// <summary>Roster row ground — an ember wash on the selected row, Transparent
    /// otherwise. Transparent and NOT null: a null Background is not hit-testable in
    /// WinUI, which would make the un-selected rows unclickable.</summary>
    public Brush RowBackground => _isSelected
        ? (s_selectedWash ??= ToolBrushes.HorizontalEmberFade(0x22))
        : (s_transparent ??= new SolidColorBrush(Colors.Transparent));

    private static Brush? s_selectedWash, s_transparent;

    private static int ParseInt(string? s, int fallback)
        => int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
