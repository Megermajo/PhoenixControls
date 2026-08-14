namespace Phoenix.Controls.Hub.WinUI.Panels.UserManagementPanel;

/// <summary>
/// One READ-ONLY standard group whose membership is the platform's answer —
/// Moderator, VIP, Subscriber.
///
/// <para>★ Why this type exists at all, rather than the section simply losing three
/// cards. These three are still groups: they are pickable on every role gate in the
/// suite, they are outputs of the User.GetGroups node, and <c>check_role</c> takes
/// them by name. What changed is only WHO IS IN THEM — Twitch / YouTube / Kick
/// publish that rank, so Phoenix reads it (off the viewer's own chat message where
/// there is one, off ViewerPresenceService's role cache where there is not) instead
/// of keeping a second list that could only ever disagree. A streamer who used to
/// type names into those boxes must be able to open this section and find out what
/// happened to them; a silently missing card teaches nothing.</para>
///
/// <para>Every property is settled at construction — the card has nothing to edit and
/// nothing that changes — so the row raises no change notification and the template
/// binds it with <c>x:Bind</c>'s default OneTime mode. The strings arrive already
/// localized from the ViewModel, because a row inside an items template cannot be
/// re-pointed by this page's code-behind-at-Loaded localization idiom.</para>
/// </summary>
public sealed class PlatformGroupRowVm
{
    public PlatformGroupRowVm(string nameEyebrow, string sourceBadge, string membershipText)
    {
        NameEyebrow = nameEyebrow ?? "";
        SourceBadge = sourceBadge ?? "";
        MembershipText = membershipText ?? "";
    }

    /// <summary>The group's name as every picker in the suite spells it, uppercased
    /// for the card-header eyebrow.</summary>
    public string NameEyebrow { get; }

    /// <summary>The badge in place of the member count the editable cards carry —
    /// "PLATFORM". It sits where the count sits so the difference between the two
    /// kinds of card is legible at a glance rather than only on reading.</summary>
    public string SourceBadge { get; }

    /// <summary>One sentence naming who is in this group and who decides.</summary>
    public string MembershipText { get; }
}
