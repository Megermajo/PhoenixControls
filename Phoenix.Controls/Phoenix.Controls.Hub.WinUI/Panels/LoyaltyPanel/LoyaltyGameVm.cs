using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
// Aliased rather than a namespace using: this file needs exactly one type out of
// Shared.Core, and the alias keeps that namespace's ~dozen engine types out of
// the lookup here. Same idiom the main VM uses for its service imports.
using ChatVerb = Phoenix.Controls.Shared.Core.ChatVerb;

namespace Phoenix.Controls.Hub.WinUI.Panels.LoyaltyPanel;

/// <summary>
/// One expandable minigame section on the Minigames tab. Exposes the enable
/// toggle + the five <see cref="LoyaltyRolesVm"/> "who can play" checkboxes as
/// structured members (they render distinctly), and every other tunable — the
/// shared bet/cooldown fields plus each game's own fields — as a flat
/// <see cref="LoyaltyLabeledField"/> list driven through the shared template.
/// The main VM builds one of these per game from the working config. Raffle
/// additionally carries a second "who can start" role set. Carries the
/// per-row expand state for the hand-rolled collapsible card (the WinUI
/// Expander was removed after the Minigames first-realization crash).
///
/// <para><b>2026-08-14 — the verbs came home.</b> The page used to print a
/// 19-row read-only CHAT COMMANDS block above the section gate, ~700 px that
/// showed on every section because no single section could honestly own it.
/// It is gone, and a game's own words now live on the game: this VM splits the
/// incoming flat field list into <see cref="VerbFields"/> (the command and any
/// sub-verbs — the raffle's join / draw / cancel, the duel's accept / decline)
/// and everything else, so the view can render the editable verb boxes FIRST in
/// the expansion and a read-only <see cref="VerbSummary"/> on the collapsed
/// row. Nothing is dropped and no field is duplicated: the two lists partition
/// the one the caller passed.</para>
/// </summary>
public sealed class LoyaltyGameVm : ObservableObject
{
    public LoyaltyGameVm(string title, LoyaltyBoolField enabled, LoyaltyRolesVm whoCanPlay,
                         IReadOnlyList<LoyaltyLabeledField> fields, LoyaltyRolesVm? whoCanStart = null)
    {
        Title = title;
        EnabledField = enabled;
        WhoCanPlay = whoCanPlay;
        WhoCanStart = whoCanStart;

        // Partition, preserving order. BaseFields puts "Command" first, so the
        // primary verb heads VerbFields and the sub-verbs follow in the order
        // the game's own builder appended them.
        var verbs = new List<LoyaltyLabeledField>();
        var rest = new List<LoyaltyLabeledField>();
        foreach (var f in fields)
        {
            if (IsVerbField(f)) verbs.Add(f);
            else rest.Add(f);
        }
        VerbFields = verbs;
        Fields = rest;

        // The collapsed row shows what the boxes hold, so it must follow them.
        // LoyaltyDraftField raises on Text, and these fields live exactly as long
        // as this VM does (the main VM rebuilds both wholesale on a foreign config
        // change), so there is nothing to detach from.
        foreach (var v in verbs)
            if (v.Entry is { } entry) entry.PropertyChanged += OnVerbFieldChanged;
    }

    public string Title { get; }
    public LoyaltyBoolField EnabledField { get; }
    public LoyaltyRolesVm WhoCanPlay { get; }

    /// <summary>Every tunable EXCEPT the verbs — bets, cooldowns, payouts, messages.</summary>
    public IReadOnlyList<LoyaltyLabeledField> Fields { get; }

    /// <summary>The game's chat words, in declaration order, primary verb first.</summary>
    public IReadOnlyList<LoyaltyLabeledField> VerbFields { get; }

    public string TitleEyebrow => Title.ToUpperInvariant();

    // A verb field is a TEXT field the builder DECLARED as a chat word. That is
    // exactly the six the builders mint — "Command", "Join command", "Draw
    // sub-command", "Cancel sub-command", "Accept command", "Decline command".
    //
    // ★ The flag replaced a Label.Contains("command") sniff. The wording is
    // localized now, so the English word is no longer in the label of a
    // translated build and the sniff would partition nothing — see
    // LoyaltyLabeledField.IsVerb for the full note.
    private static bool IsVerbField(LoyaltyLabeledField f)
        => f.Entry is not null && f.IsVerb;

    // A sub-verb is a SECOND word typed after the parent command ("!raffle
    // draw"), never a command of its own — the same distinction
    // BuiltInCommandCatalog draws between Add and AddSub. Declared by the
    // builder for exactly the two raffle words the dispatcher treats that way.
    private static bool IsSubVerbField(LoyaltyLabeledField f)
        => f.IsSubVerb;

    private void OnVerbFieldChanged(object? sender, PropertyChangedEventArgs e)
        => Raise(nameof(VerbSummary));

    /// <summary>
    /// The collapsed row's read-only verb list — "!gamble", or
    /// "!raffle · !raffle draw · !raffle cancel · !join". Rendered the way the
    /// catalogue renders it: canonicalized (a leading "!" the streamer typed is
    /// not doubled) and sub-verbs printed as the full phrase, because that is
    /// what a viewer actually types.
    /// </summary>
    public string VerbSummary
    {
        get
        {
            string parent = VerbFields.Count > 0 && !IsSubVerbField(VerbFields[0])
                ? ChatVerb.Canonical(VerbFields[0].Entry?.Text)
                : string.Empty;

            var sb = new StringBuilder();
            foreach (var f in VerbFields)
            {
                string word = ChatVerb.Canonical(f.Entry?.Text);
                // A blank word matches nothing, so the catalogue drops the row.
                // Printing it here would advertise a verb that does not exist.
                if (word.Length == 0) continue;

                string phrase = IsSubVerbField(f)
                    ? (parent.Length == 0 ? string.Empty : parent + " " + word)
                    : word;
                if (phrase.Length == 0) continue;

                if (sb.Length > 0) sb.Append("  ·  ");
                sb.Append('!').Append(phrase);
            }
            return sb.Length == 0
                ? Localizer.T("panel.loyalty.game.no_verb", "no verb set")
                : sb.ToString();
        }
    }

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
    // Segoe MDL2 ChevronUp (expanded) / ChevronDown (collapsed) - the exact
    // pair the Giveaway SETTINGS collapsible uses.
    public string ChevronGlyph => _isExpanded ? "" : "";

    /// <summary>Raffle-only second role set (who may START a raffle); null elsewhere.</summary>
    public LoyaltyRolesVm? WhoCanStart { get; }
    public bool HasWhoCanStart => WhoCanStart is not null;
    public Visibility WhoCanStartVisibility => HasWhoCanStart ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Builds the shared bet / cooldown / online fields every game carries.</summary>
    public static List<LoyaltyLabeledField> BaseFields(LoyaltyGameBase g, Action changed)
        => new()
        {
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.command.label", "Command"),
                LoyaltyField.Text(() => g.Command, s => g.Command = s, changed), null, isVerb: true),
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.min_bet.label", "Min bet"),
                LoyaltyField.Int(() => g.MinBet, v => g.MinBet = v, changed)),
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.max_bet.label", "Max bet (0 = none)"),
                LoyaltyField.Int(() => g.MaxBet, v => g.MaxBet = v, changed)),
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.user_cd.label", "User cooldown (s)"),
                LoyaltyField.Int(() => g.UserCooldownSeconds, v => g.UserCooldownSeconds = v, changed)),
            LoyaltyLabeledField.Field(Localizer.T("panel.loyalty.game.global_cd.label", "Global cooldown (s)"),
                LoyaltyField.Int(() => g.GlobalCooldownSeconds, v => g.GlobalCooldownSeconds = v, changed)),
            LoyaltyLabeledField.Check(Localizer.T("panel.loyalty.game.online_only.label", "Online only"),
                LoyaltyField.Bool(() => g.OnlineOnly, v => g.OnlineOnly = v, changed)),
        };
}
