using System;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.WinUI.Panels.RanksPanel;

/// <summary>
/// Wraps a <see cref="RankRoles"/> checkmark set as six two-way <c>bool?</c> properties so
/// a settings row can render the exact SIX checkboxes the model calls for (Everyone /
/// Subscriber / VIP / Moderator / Broadcaster / Regular). Each toggle mutates the
/// underlying roles object on the working config and schedules a debounced save through
/// <c>_changed</c>. Mirrors PollRolesVm / QuoteRolesVm exactly.
///
/// All six properties are required: RoleCheckRow binds by NAME against an <c>object?</c>,
/// so omitting one renders a checkbox that ticks and never persists.
/// </summary>
public sealed class RankRolesVm : ObservableObject
{
    private RankRoles _roles;
    private readonly Action _changed;

    public RankRolesVm(RankRoles roles, Action changed)
    {
        _roles = roles ?? new RankRoles();
        _changed = changed ?? (static () => { });
    }

    /// <summary>
    /// Points the row at a different roles object and re-raises all six checkboxes.
    /// The page keeps ONE instance of this VM for the lifetime of the tab (the XAML binds
    /// it once), so a foreign config reload has to reseat it — otherwise the checkmarks go
    /// on describing the config that was replaced, and the streamer's next edit writes the
    /// stale set straight back over the change that arrived.
    /// </summary>
    internal void Reseat(RankRoles roles)
    {
        _roles = roles ?? new RankRoles();
        Raise(nameof(Everyone));
        Raise(nameof(Subscriber));
        Raise(nameof(Vip));
        Raise(nameof(Moderator));
        Raise(nameof(Broadcaster));
        Raise(nameof(Regular));
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

    /// <summary>A fresh, detached copy for the save path — UpdateConfigAsync deep-owns
    /// whatever it is handed, so the working roles object must never be shared with the
    /// pushed config.</summary>
    internal RankRoles ToSnapshot() => new()
    {
        Everyone = _roles.Everyone,
        Subscriber = _roles.Subscriber,
        Vip = _roles.Vip,
        Moderator = _roles.Moderator,
        Broadcaster = _roles.Broadcaster,
        Regular = _roles.Regular,
    };
}
