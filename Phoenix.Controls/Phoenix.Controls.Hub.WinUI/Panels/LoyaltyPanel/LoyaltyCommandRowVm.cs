using System;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.WinUI.Panels.LoyaltyPanel;

/// <summary>
/// One row in the Commands tab — a single <see cref="LoyaltyCommand"/> exposed
/// as an enable toggle, a trigger text box, a cooldown box, and the five role
/// checkboxes (<see cref="LoyaltyRolesVm"/>). Every edit mutates the command on
/// the working config and schedules a debounced save through <c>changed</c>.
/// </summary>
public sealed class LoyaltyCommandRowVm : ObservableObject
{
    public LoyaltyCommandRowVm(string label, string blurb, LoyaltyCommand cmd, Action changed,
                               bool isModerator = false)
    {
        Label = label;
        Blurb = blurb;
        IsModerator = isModerator;
        EnabledField  = LoyaltyField.Bool(() => cmd.Enabled, v => cmd.Enabled = v, changed);
        TriggerField  = LoyaltyField.Text(() => cmd.Trigger, s => cmd.Trigger = s, changed);
        CooldownField = LoyaltyField.Int(() => cmd.CooldownSeconds, v => cmd.CooldownSeconds = v, changed);
        Roles = new LoyaltyRolesVm(cmd.Roles ??= LoyaltyRoles.All(), changed);
    }

    public string Label { get; }
    public string Blurb { get; }

    /// <summary>
    /// True for the four moderator repair verbs — add / set / remove / wipe.
    /// <c>LoyaltyView.RebuildCommandSplit</c> folds exactly these behind the
    /// disclosure row and shows the rest under CURRENCY.
    ///
    /// <para>★ Declared by the builder, never read out of <see cref="Blurb"/>.
    /// The split used to test <c>Blurb.StartsWith("Admin:")</c>, which is a
    /// property of the ENGLISH blurb: once the blurbs resolve through
    /// <c>Localizer</c> that prefix is gone in a translated build, the fold
    /// comes up empty and all nine verbs stack under Currency — silently.</para>
    /// </summary>
    public bool IsModerator { get; }

    /// <summary>Uppercased label for the house card-header eyebrow.</summary>
    public string LabelEyebrow => Label.ToUpperInvariant();
    public LoyaltyBoolField EnabledField { get; }
    public LoyaltyDraftField TriggerField { get; }
    public LoyaltyDraftField CooldownField { get; }
    public LoyaltyRolesVm Roles { get; }
}
