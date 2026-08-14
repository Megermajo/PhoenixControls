using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Phoenix.Controls.Shared.Localization;

namespace Phoenix.Controls.Hub.WinUI.Panels.Common;

/// <summary>
/// A 38 px fold row for set-once settings.
///
/// <para><b>Not an Expander.</b> An implicit <c>Expander</c> style hard-crashed
/// the app on first tab realization (the framework default style materialized a
/// full ControlTemplate lazily, at the moment the tab was first built). Even a
/// keyed Expander carries that lazy-template shape. This is a Button plus a
/// ContentPresenter whose Visibility flips — nothing is materialized late.</para>
///
/// <para><b>What may be folded.</b> Set-once settings only: moderator sub-verbs,
/// sub/VIP priority, reply templates, per-rule overrides. Never a master switch,
/// a role gate or a cooldown — anything a streamer reaches for mid-stream stays
/// visible, or the fold has just hidden the tool.</para>
///
/// <para>Open state is session-lived and deliberately NOT persisted: it is a
/// reading position, not a preference, and restoring a page with three sections
/// pre-opened would defeat the calm the row buys.</para>
/// </summary>
[ContentProperty(Name = nameof(Body))]
public sealed partial class ToolDisclosureRow : UserControl
{
    public ToolDisclosureRow()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the row opens or closes. Hosts use it to enforce
    /// one-open-at-a-time within a section.</summary>
    public event EventHandler<bool>? OpenChanged;

    /// <summary>The label. Names the CONTENTS, never the mechanism.</summary>
    public string Label
    {
        get => LabelText.Text;
        set => LabelText.Text = value ?? string.Empty;
    }

    /// <summary>Optional count shown right of the label ("4"). Empty hides it.</summary>
    public string Count
    {
        get => CountText.Text;
        set
        {
            CountText.Text = value ?? string.Empty;
            CountText.Visibility = string.IsNullOrWhiteSpace(value)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    /// <summary>The folded content. This is the XAML content property, so a
    /// consumer just nests its panel inside the row.</summary>
    public object? Body
    {
        get => BodyHost.Content;
        set => BodyHost.Content = value;
    }

    /// <summary>Open state. Setting it does NOT raise <see cref="OpenChanged"/>,
    /// so a host closing its siblings cannot re-enter its own handler.</summary>
    public bool IsOpen
    {
        get => BodyHost.Visibility == Visibility.Visible;
        set => Apply(value);
    }

    private void Apply(bool open)
    {
        BodyHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        // E70D ChevronDown (closed) / E70E ChevronUp (open).
        Chevron.Text = open ? "" : "";
        // Only the verb translates. The label is the caller's and was localized at
        // its own call site, and the em-dash separator is punctuation.
        string verb = open
            ? Localizer.T("panel.common.disclosure.collapse", "collapse")
            : Localizer.T("panel.common.disclosure.expand", "expand");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            HeaderButton, $"{LabelText.Text} — {verb}");
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        bool next = !IsOpen;
        Apply(next);
        OpenChanged?.Invoke(this, next);
    }
}
