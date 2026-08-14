using System.Collections.Generic;
using Microsoft.UI.Xaml;

namespace Phoenix.Controls.Hub.WinUI.Panels.LoyaltyPanel;

/// <summary>
/// One labelled input on a Loyalty config tab — either a boolean
/// <see cref="LoyaltyBoolField"/> (rendered as a CheckBox) or a
/// <see cref="LoyaltyDraftField"/> (rendered as a TextBox). Wrapping the two
/// field kinds under one row VM lets the Currency / Earn / Minigames tabs drive
/// dozens of inputs through a single ItemsControl DataTemplate instead of
/// hand-authoring every field in XAML — the same "generalised field" posture
/// the Timer settings card uses, scaled to the far larger Loyalty surface.
/// </summary>
public sealed class LoyaltyLabeledField
{
    private LoyaltyLabeledField(string label, string? hint, LoyaltyBoolField? toggle, LoyaltyDraftField? entry,
                                bool isVerb, bool isSubVerb)
    {
        Label = label;
        Hint = hint;
        Toggle = toggle;
        Entry = entry;
        IsVerb = isVerb;
        IsSubVerb = isSubVerb;
    }

    public string Label { get; }
    /// <summary>Optional slim note rendered under the input (e.g. table-openness reminder).</summary>
    public string? Hint { get; }
    public LoyaltyBoolField? Toggle { get; }
    public LoyaltyDraftField? Entry { get; }

    /// <summary>
    /// True when this field holds a CHAT WORD — the game's command or one of its
    /// sub-verbs. <see cref="LoyaltyGameVm"/> partitions on it to render the verb
    /// boxes first in a game's expansion and summarise them on the collapsed row.
    ///
    /// <para>★ Declared by the builder, never sniffed from <see cref="Label"/>.
    /// The partition used to test <c>Label.Contains("command")</c>, which is a
    /// property of the ENGLISH wording: the moment the labels resolve through
    /// <c>Localizer</c> that test matches nothing, every verb falls into the
    /// ordinary field list, and the collapsed row reads "no verb set" in every
    /// translated build — silently, with the app still green.</para>
    /// </summary>
    public bool IsVerb { get; }

    /// <summary>
    /// True for a SECOND word typed after the parent command ("!raffle draw"),
    /// never a command of its own — the distinction
    /// <c>BuiltInCommandCatalog</c> draws between Add and AddSub. Declared for
    /// the same reason as <see cref="IsVerb"/>.
    /// </summary>
    public bool IsSubVerb { get; }

    public bool IsToggle => Toggle is not null;
    public Visibility ToggleVisibility => IsToggle ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EntryVisibility => IsToggle ? Visibility.Collapsed : Visibility.Visible;
    public Visibility HintVisibility => string.IsNullOrEmpty(Hint) ? Visibility.Collapsed : Visibility.Visible;

    public static LoyaltyLabeledField Check(string label, LoyaltyBoolField field, string? hint = null)
        => new(label, hint, field, null, false, false);

    public static LoyaltyLabeledField Field(string label, LoyaltyDraftField field, string? hint = null,
                                            bool isVerb = false, bool isSubVerb = false)
        => new(label, hint, null, field, isVerb, isSubVerb);
}

/// <summary>A titled cluster of <see cref="LoyaltyLabeledField"/>s — one card
/// section on a config tab (e.g. "Watch-time", "Events", "Multipliers").</summary>
public sealed class LoyaltyFieldGroup
{
    public LoyaltyFieldGroup(string title, IReadOnlyList<LoyaltyLabeledField> fields)
    {
        Title = title;
        Fields = fields;
    }

    public string Title { get; }
    public IReadOnlyList<LoyaltyLabeledField> Fields { get; }

    /// <summary>Uppercased title for the house card-header eyebrow.</summary>
    public string TitleEyebrow => Title.ToUpperInvariant();
}
