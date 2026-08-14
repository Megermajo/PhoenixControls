using System;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.WinUI.Panels.LoyaltyPanel;

/// <summary>
/// Wraps a <see cref="LoyaltyRoles"/> checkmark set as six two-way
/// <c>bool?</c> properties so a command / game row can render the exact SIX
/// checkboxes the model calls for (Everyone / Subscriber / VIP / Moderator /
/// Broadcaster / Regular). Each toggle mutates the underlying roles object on the
/// working config and schedules a debounced save through <c>_changed</c>.
/// </summary>
public sealed class LoyaltyRolesVm : ObservableObject
{
    private readonly LoyaltyRoles _roles;
    private readonly Action _changed;

    public LoyaltyRolesVm(LoyaltyRoles roles, Action changed)
    {
        _roles = roles ?? new LoyaltyRoles();
        _changed = changed;
    }

    public bool? Everyone
    {
        get => _roles.Everyone;
        set { _roles.Everyone = value == true; Raise(nameof(Everyone)); _changed(); }
    }

    public bool? Subscriber
    {
        get => _roles.Subscriber;
        set { _roles.Subscriber = value == true; Raise(nameof(Subscriber)); _changed(); }
    }

    public bool? Vip
    {
        get => _roles.Vip;
        set { _roles.Vip = value == true; Raise(nameof(Vip)); _changed(); }
    }

    public bool? Moderator
    {
        get => _roles.Moderator;
        set { _roles.Moderator = value == true; Raise(nameof(Moderator)); _changed(); }
    }

    public bool? Broadcaster
    {
        get => _roles.Broadcaster;
        set { _roles.Broadcaster = value == true; Raise(nameof(Broadcaster)); _changed(); }
    }

    public bool? Regular
    {
        get => _roles.Regular;
        set { _roles.Regular = value == true; Raise(nameof(Regular)); _changed(); }
    }
}
