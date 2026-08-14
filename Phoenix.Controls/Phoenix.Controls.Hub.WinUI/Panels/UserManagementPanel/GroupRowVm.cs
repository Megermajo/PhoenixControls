using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.WinUI.Panels.UserManagementPanel;

/// <summary>
/// One collapsible group card on the GROUPS section — either the standard REGULAR
/// group (a permanent instance, name read-only, no delete) or a user-defined custom
/// group (editable name + delete).
///
/// <para>★ Moderator / VIP / Subscriber do NOT come through here any more. They are
/// still groups you can pick everywhere a group is picked, but their membership is
/// the platform's answer, so they carry no member list to edit and are rendered by
/// <see cref="PlatformGroupRowVm"/> instead. This type is only for the groups that
/// genuinely have a membership Phoenix owns.</para>
///
/// <para>Membership has TWO independent ways in, and the card shows both. The manual
/// list is edited as one multiline string (<see cref="MembersText"/>, one username per
/// line) and projected back to a clean <c>List&lt;string&gt;</c> on save (trim, drop
/// empties, case-insensitive dedupe preserving order). The watch-hour rule
/// (<see cref="WatchHoursText"/>) is a threshold the SERVICE evaluates live on every
/// check — it is never written into the member list, which is what makes it
/// reversible. So <see cref="MemberCount"/> counts the hand-picked names only, and
/// deliberately does not try to guess how many people the rule currently promotes:
/// that number changes with every accrued minute and is not this card's to claim.</para>
///
/// <para>Every edit schedules a debounced save through <c>changed</c>. Carries the
/// per-row expand state for the hand-rolled collapsible card (the WinUI Expander was
/// removed after the Minigames first-realization crash — see LoyaltyRewardRowVm).</para>
/// </summary>
public sealed class GroupRowVm : ObservableObject
{
    private readonly Action _changed;
    private readonly bool _isCustom;
    private readonly string _id;
    private string _name;
    private string _membersText = "";

    private GroupRowVm(bool isCustom, string id, string name, Action changed)
    {
        _isCustom = isCustom;
        _id = id ?? "";
        _name = name ?? "";
        _changed = changed ?? (static () => { });
    }

    /// <summary>The permanent REGULAR row — the one standard group Phoenix still owns
    /// the membership of. Members and the watch-hour threshold are seeded by the
    /// ViewModel via <see cref="LoadMembers"/> / <see cref="LoadWatchHours"/> (the
    /// instance is never rebuilt — it survives foreign config reloads).</summary>
    public static GroupRowVm Regular(string name, Action changed)
        => new GroupRowVm(isCustom: false, id: "", name: name, changed: changed);

    /// <summary>A custom-group row seeded from the working config's <see cref="UserGroup"/>.</summary>
    public static GroupRowVm Custom(UserGroup def, Action changed)
    {
        if (def is null) throw new ArgumentNullException(nameof(def));
        var row = new GroupRowVm(isCustom: true, id: def.Id ?? "", name: def.Name ?? "", changed: changed);
        row.LoadMembers(def.Members);
        row.LoadWatchHours(def.AutoWatchHours);
        return row;
    }

    /// <summary>Stable identity (GUID string; empty for the four fixed rows).</summary>
    internal string Id => _id;
    internal bool IsCustom => _isCustom;

    // ── Name (editable on custom rows only — the fixed template never binds TwoWay) ─
    public string Name
    {
        get => _name;
        set
        {
            var v = value ?? "";
            if (_name == v) return;
            _name = v;
            Raise(nameof(Name));
            Raise(nameof(NameEyebrow));
            _changed();
        }
    }

    /// <summary>Uppercased group name for the card-header eyebrow.</summary>
    public string NameEyebrow
    {
        get
        {
            string n = _name.Trim();
            return n.Length == 0
                ? Localizer.T("panel.usermgmt.groups.unnamed", "(UNNAMED GROUP)")
                : n.ToUpperInvariant();
        }
    }

    // ── Members (one username per line; count badge tracks the parsed list) ─
    public string MembersText
    {
        get => _membersText;
        set
        {
            var v = value ?? "";
            if (_membersText == v) return;
            _membersText = v;
            Raise(nameof(MembersText));
            Raise(nameof(MemberCountText));
            _changed();
        }
    }

    /// <summary>Parsed member count — the hand-picked names only. Drives the header
    /// badge and the LISTED MEMBERS stat tile; people the watch-hour rule promotes are
    /// deliberately not in it, because that number changes with every accrued minute
    /// and is not this row's to claim.</summary>
    public int MemberCount => ToMembers().Count;

    public string MemberCountText
    {
        get
        {
            int n = MemberCount;
            return n == 1
                ? Localizer.T("panel.usermgmt.groups.member_one", "1 member")
                : string.Format(CultureInfo.InvariantCulture,
                    Localizer.T("panel.usermgmt.groups.member_many", "{0} members"), n);
        }
    }

    // ── The watch-hour rule (0 = off) ────────────────────────────────────
    // Rides a string property with a clamping parse, the same idiom as every other
    // numeric field on this page (see UserManagementViewModel.QueueMaxSizeText):
    // garbage is REJECTED back to the last good value rather than zeroing the field,
    // because zero here is a meaningful setting — it switches the rule off — and a
    // typo must never silently do that.
    public string WatchHoursText
    {
        get => _watchHours.ToString(CultureInfo.InvariantCulture);
        set
        {
            int v = Math.Max(0, ParseInt(value, _watchHours));
            if (_watchHours == v) { Raise(nameof(WatchHoursText)); return; }
            _watchHours = v;
            Raise(nameof(WatchHoursText));
            Raise(nameof(WatchHoursStateText));
            _changed();
        }
    }

    private int _watchHours;

    /// <summary>The threshold for the save path (0 = no rule).</summary>
    internal int WatchHours => _watchHours;

    /// <summary>The field's caption. An INSTANCE property, and localized here rather
    /// than in the View: a card inside a DataTemplate cannot be x:Name'd, so this
    /// page's code-behind-at-Loaded localization idiom cannot reach it, and x:Bind
    /// only reaches a static through the fully-qualified type name.</summary>
    public string WatchHoursLabel => Localizer.T("panel.usermgmt.groups.watch_hours_label",
        "AUTO-JOIN AT (HOURS WATCHED · 0 = OFF)");

    /// <summary>Says what the current threshold actually does, in words. Worth a line
    /// because the rule's two surprising properties — it is evaluated live, and it
    /// never touches the list above it — are invisible in a number field.</summary>
    public string WatchHoursStateText => _watchHours <= 0
        ? Localizer.T("panel.usermgmt.groups.watch_rule_off",
            "No watch-hour rule — only the names above are members.")
        : string.Format(CultureInfo.InvariantCulture,
            Localizer.T("panel.usermgmt.groups.watch_rule_on",
                "Anyone with {0}+ hours watched counts as a member, list or not. Checked live — the list is never rewritten."),
            _watchHours);

    /// <summary>Seeds the threshold from config WITHOUT scheduling a save (load /
    /// foreign-reload path). UI thread only.</summary>
    internal void LoadWatchHours(int hours)
    {
        _watchHours = Math.Max(0, hours);
        Raise(nameof(WatchHoursText));
        Raise(nameof(WatchHoursStateText));
    }

    private static int ParseInt(string? raw, int fallback)
        => int.TryParse((raw ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v : fallback;

    /// <summary>The clean member list for the save path: split on newlines (the WinUI
    /// TextBox reports line breaks as '\r'; pastes may carry '\n' or CRLF), trim,
    /// drop empties, case-insensitive dedupe preserving first-seen order. Always a
    /// fresh list — never shared with the pushed config.</summary>
    internal List<string> ToMembers()
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in _membersText.Split('\r', '\n'))
        {
            string name = raw.Trim();
            if (name.Length == 0) continue;
            if (!seen.Add(name)) continue;
            list.Add(name);
        }
        return list;
    }

    /// <summary>Seeds the editor text from a config member list WITHOUT scheduling a
    /// save (load / foreign-reload path). UI thread only.</summary>
    internal void LoadMembers(IEnumerable<string>? members)
    {
        // Join with '\r' — WinUI TextBox normalizes line breaks to CR internally,
        // so a '\n' join makes the first focus+blur rewrite the text and fire a
        // spurious dirty/save cycle without any user edit.
        _membersText = members is null ? "" : string.Join("\r", members);
        Raise(nameof(MembersText));
        Raise(nameof(MemberCountText));
    }

    /// <summary>A fresh, detached <see cref="UserGroup"/> snapshot for the save path
    /// (custom rows only — the fixed rows map to the config's flat lists).</summary>
    internal UserGroup ToSnapshot() => new UserGroup
    {
        Id = _id,
        Name = _name,
        Members = ToMembers(),
        AutoWatchHours = _watchHours,
    };

    // Collapsible-card state — collapsed by default (the old Expander default).
    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value))
            {
                Raise(nameof(BodyVisibility));
                Raise(nameof(ChevronGlyph));
            }
        }
    }

    public Visibility BodyVisibility => _isExpanded ? Visibility.Visible : Visibility.Collapsed;
    // Segoe MDL2 ChevronUp (expanded) / ChevronDown (collapsed) — the exact
    // pair the Loyalty / Giveaway collapsibles use.
    public string ChevronGlyph => _isExpanded ? "" : "";
}
