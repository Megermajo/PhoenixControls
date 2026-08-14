using System;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.WinUI.Panels.UserManagementPanel;

/// <summary>
/// One personalized-welcome row — a single <see cref="WelcomeEntry"/> exposed as its
/// editable settings (username, enabled slider, message, auto-shoutout) for the
/// PERSONALIZED WELCOMES list. Every edit mutates the def on the working config and
/// schedules a debounced save through <c>changed</c>. Mirrors <c>ScheduleRowVm</c>
/// (def + changed-callback discipline; <see cref="ToSnapshot"/> for the save path).
/// </summary>
public sealed class WelcomeRowVm : ObservableObject
{
    private readonly WelcomeEntry _def;
    private readonly Action _changed;

    public WelcomeRowVm(WelcomeEntry def, Action changed)
    {
        _def = def ?? throw new ArgumentNullException(nameof(def));
        _changed = changed ?? (static () => { });
    }

    /// <summary>Stable identity (GUID string) — never edited in the UI.</summary>
    internal string Id => _def.Id ?? "";

    // ── Settings (mutate the def + schedule a debounced save) ────────────
    public bool Enabled
    {
        get => _def.Enabled;
        set
        {
            if (_def.Enabled == value) return;
            _def.Enabled = value;
            Raise(nameof(Enabled));
            Raise(nameof(EnabledLabel));
            _changed();
        }
    }

    /// <summary>Caption beside the row's TogglePill. The pill replaced a
    /// ToggleSwitch whose On/Off content carried this word for free.</summary>
    public string EnabledLabel
        => _def.Enabled ? UserManagementViewModel.PillOn : UserManagementViewModel.PillOff;

    public string Username
    {
        get => _def.Username ?? "";
        set
        {
            var v = value ?? "";
            if ((_def.Username ?? "") == v) return;
            _def.Username = v;
            Raise(nameof(Username));
            Raise(nameof(TitleEyebrow));
            _changed();
        }
    }

    public string Message
    {
        get => _def.Message ?? "";
        set
        {
            var v = value ?? "";
            if ((_def.Message ?? "") == v) return;
            _def.Message = v;
            Raise(nameof(Message));
            _changed();
        }
    }

    /// <summary>Twitch-only auto-shoutout on the user's arrival. Still <c>bool?</c>
    /// with its null→false coercion (LoyaltyBoolField discipline) — the CheckBox
    /// that needed the nullable is gone, but the coercion is what keeps a config
    /// blob with a missing value from turning into an exception.</summary>
    public bool? AutoShoutout
    {
        get => _def.AutoShoutout;
        set
        {
            bool v = value == true;
            if (_def.AutoShoutout == v) { Raise(nameof(AutoShoutout)); return; }
            _def.AutoShoutout = v;
            Raise(nameof(AutoShoutout));
            Raise(nameof(AutoShoutoutOn));
            Raise(nameof(AutoShoutoutLabel));
            _changed();
        }
    }

    /// <summary>The plain bool the TogglePill binds — <see cref="TogglePill.IsOn"/>
    /// is a <c>bool</c> DP and will not take a <c>bool?</c>.</summary>
    public bool AutoShoutoutOn => _def.AutoShoutout;

    public string AutoShoutoutLabel
        => _def.AutoShoutout ? UserManagementViewModel.PillOn : UserManagementViewModel.PillOff;

    /// <summary>Flip for the pill's host Button. Routes through the nullable
    /// setter so the coercion, the PropertyChanged fan-out and the debounced save
    /// all stay in exactly one place.</summary>
    public void ToggleAutoShoutout() => AutoShoutout = !_def.AutoShoutout;

    // ── Header-band projection (display-only; the editable Username TextBox stays) ──
    public string TitleEyebrow
    {
        get
        {
            string n = (_def.Username ?? "").Trim();
            return n.Length == 0
                ? Localizer.T("panel.usermgmt.welcome.row.unnamed", "(UNNAMED)")
                : n.ToUpperInvariant();
        }
    }

    /// <summary>A fresh, detached <see cref="WelcomeEntry"/> snapshot for the save path —
    /// UpdateConfigAsync deep-owns whatever it is handed, so the working defs must not
    /// be shared with the pushed config.</summary>
    internal WelcomeEntry ToSnapshot() => new WelcomeEntry
    {
        Id = _def.Id ?? "",
        Enabled = _def.Enabled,
        Username = _def.Username ?? "",
        Message = _def.Message ?? "",
        AutoShoutout = _def.AutoShoutout,
    };
}
