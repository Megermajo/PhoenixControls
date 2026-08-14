using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Shared.Localization;

namespace Phoenix.Controls.Hub.WinUI.Panels.Common;

/// <summary>
/// Shared six-checkbox role gate (Everyone / Sub / VIP / Mod / Broadcaster / Regular)
/// reused across the Automod / Counters / Quotes / Custom Commands / Loyalty tools. The
/// caller hands its own tool-specific <c>*RolesVm</c> (all structurally identical —
/// six two-way <c>bool?</c> props on an <c>ObservableObject</c>) to <see cref="Roles"/>;
/// the inner checkboxes bind against it by property name via classic
/// <c>{Binding}</c>, so no shared base type or generic refactor of the VMs is needed.
/// </summary>
public sealed partial class RoleCheckRow : UserControl
{
    public RoleCheckRow()
    {
        InitializeComponent();
        HookExclusivity();
    }

    /// <summary>The tool's role VM (AutomodRolesVm / CounterRolesVm / …). Typed as
    /// <see cref="object"/> so any of the five identical VMs binds without a shared
    /// base type; the checkboxes resolve
    /// Everyone/Subscriber/Vip/Moderator/Broadcaster/Regular off it by name.</summary>
    public object? Roles
    {
        get => GetValue(RolesProperty);
        set => SetValue(RolesProperty, value);
    }

    public static readonly DependencyProperty RolesProperty =
        DependencyProperty.Register(nameof(Roles), typeof(object), typeof(RoleCheckRow),
            new PropertyMetadata(null, OnRolesChanged));

    private static void OnRolesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Drive the checkbox subtree off the bound VM. Setting RootPanel's
        // DataContext (rather than the whole control's) leaves the control's own
        // inherited DataContext — the host row/page VM — untouched.
        if (d is RoleCheckRow row)
        {
            row.RootPanel.DataContext = e.NewValue;
            // The bindings resolve on the next layout pass, so read the applied
            // state rather than the VM: by the time this returns, EveryoneBox
            // may still hold the previous row's value.
            row.DispatcherQueue.TryEnqueue(row.ApplyEveryoneExclusivity);
        }
    }

    // ─── Everyone exclusivity ───────────────────────────────────────────────
    //
    // ★ 2026-08-14. A chatter passes a role gate if Everyone is ticked OR they
    // hold any ticked role — so while Everyone is on, the other five decide
    // nothing. Six equal checkboxes made that invisible: a page could show
    // Everyone ticked beside five live boxes that changed no behaviour, and the
    // only place the rule was ever legible was the command catalogue's
    // collapsed Roles summary.
    //
    // ★ The five are DIMMED AND DISABLED, deliberately NOT CLEARED. Clearing
    // would write through the TwoWay bindings and destroy the streamer's actual
    // selection, so unticking Everyone would leave an empty gate — a gate that
    // now blocks everyone, from a gesture that reads like "narrow this a bit".
    // Disabling preserves the stored roles and makes unticking Everyone restore
    // exactly what was there before. They stay VISIBLE so the model is legible.
    //
    // The subscription lives here rather than in each of the ten *RolesVm
    // classes because the VMs are structurally identical but share no base type
    // — this control is the one place the rule can be stated once.

    private const double DisabledRoleOpacity = 0.45;

    private void HookExclusivity()
    {
        EveryoneBox.Checked   += OnEveryoneToggled;
        EveryoneBox.Unchecked += OnEveryoneToggled;
        ApplyEveryoneExclusivity();
    }

    private void OnEveryoneToggled(object sender, RoutedEventArgs e)
        => ApplyEveryoneExclusivity();

    private void ApplyEveryoneExclusivity()
    {
        bool everyone = EveryoneBox.IsChecked == true;
        foreach (var box in new[] { SubBox, VipBox, ModBox, BroadcasterBox, RegularBox })
        {
            box.IsEnabled = !everyone;
            box.Opacity   = everyone ? DisabledRoleOpacity : 1.0;
        }
        ToolTipService.SetToolTip(RootPanel, everyone
            ? Localizer.T("panel.common.role.everyone.tip",
                "Everyone is on, so the other roles do not narrow anything. Untick it to gate by role.")
            : null);
    }
}
