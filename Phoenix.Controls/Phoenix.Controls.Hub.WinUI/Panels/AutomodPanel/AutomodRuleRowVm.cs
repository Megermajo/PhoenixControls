using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.WinUI.Panels.AutomodPanel;

/// <summary>
/// One row in the Rules section — a single automod rule exposed as its enable
/// sub-toggle, its action mode (use the global ladder OR a fixed action), and up
/// to two numeric thresholds + one optional bool toggle, all supplied by the
/// ViewModel via accessor delegates so this one row VM serves every rule shape
/// (caps / length / repeat / symbols / links / rate). Words + Regex rules carry no
/// thresholds — they show a hint pointing at the Lists section. Every edit mutates the
/// underlying rule on the working config and schedules a debounced save.
/// </summary>
public sealed class AutomodRuleRowVm : ObservableObject
{
    private readonly AutomodRuleBase _rule;
    private readonly Action _changed;

    private readonly Func<int>? _getT1;
    private readonly Action<int>? _setT1;
    private readonly Func<int>? _getT2;
    private readonly Action<int>? _setT2;
    private readonly Func<bool>? _getToggle;
    private readonly Action<bool>? _setToggle;

    // Link-permit pass-through — supplied ONLY for the Links row (see the
    // HasPermit block below). Accessor delegates rather than a back-reference to
    // AutomodViewModel, matching how every other value on this row already
    // reaches the working config.
    private readonly Func<string>? _getPermitCommand;
    private readonly Action<string>? _setPermitCommand;
    private readonly Func<string>? _getPermitSeconds;
    private readonly Action<string>? _setPermitSeconds;

    public string Title { get; }

    /// <summary>Optional one-line "how this rule fires" note. Empty for the rules whose
    /// threshold labels already say it — the row collapses the TextBlock rather than
    /// leaving a blank line under the title.</summary>
    public string Description { get; }
    public bool HasDescription => Description.Length > 0;

    /// <summary>Segoe MDL2 codepoint shown as the rule's leading FontIcon.</summary>
    public string Glyph { get; }

    public bool HasT1 { get; }
    public string T1Label { get; }
    public bool HasT2 { get; }
    public string T2Label { get; }
    public bool HasToggle { get; }
    public string ToggleLabel { get; }
    public string ListsHint { get; }
    public bool HasListsHint => ListsHint.Length > 0;

    // Visibility-typed helpers (constant per row) so the DataTemplate binds directly
    // without a converter — the exact posture the Loyalty page row VMs use.
    public Visibility DescriptionVisibility => HasDescription ? Visibility.Visible : Visibility.Collapsed;
    public Visibility T1Visibility => HasT1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility T2Visibility => HasT2 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ToggleVisibility => HasToggle ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ListsHintVisibility => HasListsHint ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Gate for the row that HOLDS the two threshold boxes and the optional bool
    /// toggle.
    ///
    /// <para>★ 2026-08-14 — the dead-space fix. That StackPanel had no Visibility
    /// of its own while all three of its children carried one, so on the two rules
    /// that have neither a threshold nor a toggle (Blocklist words, Blocklist
    /// regex) it collapsed to zero content but still drew its share of the parent
    /// StackPanel's Spacing on both sides — ~20 px of nothing, in 2 of the 8
    /// rules. A parent whose every child can collapse needs a Visibility that
    /// follows them.</para>
    /// </summary>
    public Visibility FieldsVisibility =>
        (HasT1 || HasT2 || HasToggle) ? Visibility.Visible : Visibility.Collapsed;

    public AutomodRuleRowVm(
        string title, string description, AutomodRuleBase rule, Action changed,
        string? t1Label = null, Func<int>? getT1 = null, Action<int>? setT1 = null,
        string? t2Label = null, Func<int>? getT2 = null, Action<int>? setT2 = null,
        string? toggleLabel = null, Func<bool>? getToggle = null, Action<bool>? setToggle = null,
        string? listsHint = null, string? glyph = null,
        AutomodRolesVm? permitRoles = null,
        Func<string>? getPermitCommand = null, Action<string>? setPermitCommand = null,
        Func<string>? getPermitSeconds = null, Action<string>? setPermitSeconds = null)
    {
        Title = title ?? "";
        Description = description ?? "";
        Glyph = string.IsNullOrEmpty(glyph) ? "\uE71C" : glyph;   // Filter fallback
        _rule = rule ?? throw new ArgumentNullException(nameof(rule));
        _changed = changed ?? (static () => { });

        _getT1 = getT1; _setT1 = setT1;
        _getT2 = getT2; _setT2 = setT2;
        _getToggle = getToggle; _setToggle = setToggle;

        HasT1 = getT1 != null && setT1 != null; T1Label = t1Label ?? "";
        HasT2 = getT2 != null && setT2 != null; T2Label = t2Label ?? "";
        HasToggle = getToggle != null && setToggle != null; ToggleLabel = toggleLabel ?? "";
        ListsHint = listsHint ?? "";

        _getPermitCommand = getPermitCommand; _setPermitCommand = setPermitCommand;
        _getPermitSeconds = getPermitSeconds; _setPermitSeconds = setPermitSeconds;
        PermitRoles = permitRoles;
        HasPermit = getPermitCommand != null && setPermitCommand != null;
    }

    // ── Accordion state ─────────────────────────────────────────────────
    //
    // Eight rules at ~135-150 px each was ~1400 px of scroll for a section whose
    // whole job is eight booleans and a threshold or two. Collapsed the row is
    // 44 px; the page owns "one open at a time" (AutomodView.OnRuleFoldClick)
    // because only the page can see the siblings.
    //
    // Session-lived and deliberately NOT persisted: which rule you last read is a
    // reading position, not a preference.
    private bool _expanded;

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            Raise(nameof(IsExpanded));
            Raise(nameof(ExpandedVisibility));
            Raise(nameof(ChevronGlyph));
            Raise(nameof(FoldAutomationName));
        }
    }

    public Visibility ExpandedVisibility => _expanded ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>E70E ChevronUp (open) / E70D ChevronDown (closed).</summary>
    public string ChevronGlyph => _expanded ? "" : "";

    public string FoldAutomationName => _expanded
        ? string.Format(Localizer.T("panel.automod.rule.fold.collapse.a11y", "{0} — collapse"), Title)
        : string.Format(Localizer.T("panel.automod.rule.fold.expand.a11y", "{0} — expand"), Title);

    // ── Link permit (Links row only) ────────────────────────────────────
    //
    // The permit used to be its own card in the General section, two sections away
    // from the single rule it bypasses, with a one-row CHAT COMMANDS block at the
    // top of the page as the only place the verb was named. It is one setting, so
    // it lives in one place: the first field of the Links rule's expansion.

    /// <summary>The page's PermitRoles VM, handed in at BuildRules time so a foreign
    /// ConfigChanged (which replaces the roles VM and then rebuilds these rows) lands
    /// the fresh instance. Null on every row but Links.</summary>
    public AutomodRolesVm? PermitRoles { get; }

    public bool HasPermit { get; }
    public Visibility PermitVisibility => HasPermit ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Pass-through to <c>AutomodViewModel.PermitCommand</c> — that setter still
    /// owns the verbatim-save contract and the debounce; this only re-raises locally so a
    /// value the page normalizes reaches this row's TwoWay binding.</summary>
    public string PermitCommand
    {
        get => _getPermitCommand?.Invoke() ?? "";
        set
        {
            if (_setPermitCommand is null) return;
            _setPermitCommand(value ?? "");
            Raise(nameof(PermitCommand));
        }
    }

    /// <summary>Pass-through to <c>AutomodViewModel.PermitSecondsText</c>, which clamps to
    /// a minimum of 1 — hence the unconditional re-raise, so a rejected value snaps back.</summary>
    public string PermitSecondsText
    {
        get => _getPermitSeconds?.Invoke() ?? "";
        set
        {
            if (_setPermitSeconds is null) return;
            _setPermitSeconds(value ?? "");
            Raise(nameof(PermitSecondsText));
        }
    }

    public bool Enabled
    {
        get => _rule.Enabled;
        set { if (_rule.Enabled == value) return; _rule.Enabled = value; Raise(nameof(Enabled)); _changed(); }
    }

    /// <summary>True = follow the global escalation ladder; false = issue FixedAction.</summary>
    public bool UseLadder
    {
        get => _rule.ActionMode == AutomodActionMode.Ladder;
        set
        {
            var mode = value ? AutomodActionMode.Ladder : AutomodActionMode.Fixed;
            if (_rule.ActionMode == mode) return;
            _rule.ActionMode = mode;
            Raise(nameof(UseLadder));
            Raise(nameof(UseLadderLabel));
            Raise(nameof(FixedEnabled));
            Raise(nameof(FixedDurationEnabled));   // Fixed-mode gate also flips the duration box
            RaiseOverride();
            _changed();
        }
    }

    // ── Default vs override ─────────────────────────────────────────────
    //
    // AutomodRuleBase.ActionMode defaults to Ladder for every rule ("default: use
    // the global ladder", AutomodModels.cs:143), which is why the eight identical
    // "ACTION / Use ladder / Timeout / 600 sec" rows could collapse into ONE
    // "Default action" statement at the top of the section: on a stock config all
    // eight said the same thing. Fixed mode is now what it always was — an
    // override — and it is the only thing this row reports in its collapsed state.

    // The action WORDS are display text, not the persisted value — AutomodAction is
    // what the config stores, and it stays English there. These four are only ever
    // rendered, so they carry keys; the array below is their English fallback and the
    // key array is parallel to it. Resolved per read rather than at type-load, because
    // a static initializer can run before Localizer.Init.
    private static readonly string[] ActionWords = { "warn", "delete", "timeout", "ban" };

    private static readonly string[] ActionWordKeys =
    {
        "panel.automod.rule.summary.warn",
        "panel.automod.rule.summary.delete",
        "panel.automod.rule.summary.timeout",
        "panel.automod.rule.summary.ban",
    };

    /// <summary>Collapsed-row action note. EMPTY while the rule follows the default,
    /// so eight rules on a stock config carry no repeated text and a single override
    /// is the only thing on the strip that reads.</summary>
    public string ActionSummary
    {
        get
        {
            if (UseLadder) return "";
            int i = Math.Clamp(FixedActionIndex, 0, ActionWords.Length - 1);
            string word = Localizer.T(ActionWordKeys[i], ActionWords[i]);
            return _rule.FixedAction == AutomodAction.Timeout
                ? string.Format(Localizer.T("panel.automod.rule.summary.duration", "{0} {1}s"), word, _rule.FixedDurationSeconds)
                : word;
        }
    }

    /// <summary>Gate for the "reset to default" affordance — an override is the only
    /// state there is anything to reset from.</summary>
    public Visibility OverriddenVisibility => UseLadder ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Puts the rule back on the ladder. Routed through the
    /// <see cref="UseLadder"/> setter so the save schedule and every dependent raise
    /// stay in exactly one place.</summary>
    public void ResetActionToDefault() => UseLadder = true;

    private void RaiseOverride()
    {
        Raise(nameof(ActionSummary));
        Raise(nameof(OverriddenVisibility));
    }

    /// <summary>Caption beside the action-mode TogglePill. Carries the two strings the
    /// retired ToggleSwitch held in OnContent / OffContent — a plain ON/OFF here would
    /// lose the only label that says WHICH action a rule issues.</summary>
    public string UseLadderLabel => UseLadder
        ? Localizer.T("panel.automod.rule.action_mode.ladder", "Use ladder")
        : Localizer.T("panel.automod.rule.action_mode.fixed", "Fixed action");

    public bool FixedEnabled => !UseLadder;

    /// <summary>The Fixed-action duration box only applies to a Timeout — gate it so it
    /// isn't editable for Warn / Delete / Ban (which ignore the value). Requires Fixed
    /// mode too (the ladder owns the duration otherwise). Mirrors
    /// AutomodLadderStepVm.DurationEnabled.</summary>
    public bool FixedDurationEnabled => FixedEnabled && _rule.FixedAction == AutomodAction.Timeout;

    /// <summary>ComboBox index over {Warn, Delete, Timeout, Ban} ↔ AutomodAction.</summary>
    public int FixedActionIndex
    {
        get => Math.Max(0, (int)_rule.FixedAction - 1);
        set
        {
            var action = (AutomodAction)(Math.Clamp(value, 0, 3) + 1);
            if (_rule.FixedAction == action) return;
            _rule.FixedAction = action;
            Raise(nameof(FixedActionIndex));
            Raise(nameof(FixedDurationEnabled));   // Warn/Delete/Ban ↔ Timeout flips the gate
            Raise(nameof(ActionSummary));
            _changed();
        }
    }

    public string FixedDurationText
    {
        get => _rule.FixedDurationSeconds.ToString(CultureInfo.InvariantCulture);
        set
        {
            int v = Math.Max(1, ParseInt(value, _rule.FixedDurationSeconds));
            if (_rule.FixedDurationSeconds == v) { Raise(nameof(FixedDurationText)); return; }
            _rule.FixedDurationSeconds = v;
            Raise(nameof(FixedDurationText));
            Raise(nameof(ActionSummary));
            _changed();
        }
    }

    public string T1Text
    {
        get => (_getT1?.Invoke() ?? 0).ToString(CultureInfo.InvariantCulture);
        set
        {
            if (_setT1 is null || _getT1 is null) return;
            int v = Math.Max(0, ParseInt(value, _getT1()));
            if (_getT1() == v) { Raise(nameof(T1Text)); return; }
            _setT1(v);
            Raise(nameof(T1Text));
            _changed();
        }
    }

    public string T2Text
    {
        get => (_getT2?.Invoke() ?? 0).ToString(CultureInfo.InvariantCulture);
        set
        {
            if (_setT2 is null || _getT2 is null) return;
            int v = Math.Max(0, ParseInt(value, _getT2()));
            if (_getT2() == v) { Raise(nameof(T2Text)); return; }
            _setT2(v);
            Raise(nameof(T2Text));
            _changed();
        }
    }

    public bool ToggleValue
    {
        get => _getToggle?.Invoke() ?? false;
        set
        {
            if (_setToggle is null || _getToggle is null) return;
            if (_getToggle() == value) return;
            _setToggle(value);
            Raise(nameof(ToggleValue));
            _changed();
        }
    }

    private static int ParseInt(string? s, int fallback)
        => int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
