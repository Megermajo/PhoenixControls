using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;

namespace Phoenix.Controls.Hub.WinUI.Panels.UserManagementPanel;

/// <summary>
/// One viewer's accrued watch time, in the WATCH TIME section's TOP WATCHERS list and
/// in its searchable browser.
///
/// <para>★ ROW IDENTITY IS LOAD-BEARING. The background sampler raises
/// <c>PresenceSampled</c> on every SUCCESSFUL poll (a failed sweep keeps the
/// previous sample and stays silent), and the panel refreshes off it. If a refresh
/// rebuilt these rows, a streamer half-way through typing a correction into the
/// hours/minutes boxes would lose it — silently, and at a cadence they cannot see
/// coming. So the ViewModel keeps ONE instance per login for the panel's life and
/// only updates <see cref="Minutes"/> on it (see UserManagementViewModel's watch-time
/// reconcile). Everything the sampler can change therefore raises change
/// notification; the two edit fields are owned by the user and are never written by a
/// refresh.</para>
///
/// <para>The edit fields are seeded from the stored minutes on construction and again
/// only when the streamer applies or resets — so a row shows what they last typed,
/// not what the sampler has since added, which is the honest reading of an input box
/// somebody is standing in.</para>
/// </summary>
public sealed class WatchTimeRowVm : ObservableObject
{
    private long _minutes;
    private string _hoursEdit = "";
    private string _minutesEdit = "";

    public WatchTimeRowVm(string login, string display, long minutes)
    {
        Login = login ?? "";
        // The presence cache answers with the login itself when it has never seen a
        // display name; a secondary line repeating the primary one is noise, so the
        // template hides it via DisplayHintVisibility rather than printing twice.
        Display = string.IsNullOrWhiteSpace(display) ? Login : display;
        _minutes = Math.Max(0, minutes);
        SeedEdits();
    }

    /// <summary>The normalized login — the key the WatchTime table is written under,
    /// and what every write goes back through.</summary>
    public string Login { get; }

    /// <summary>The name chat shows, where the platform reported one — otherwise the
    /// login. Not settled at construction: a row can be created for a viewer the role
    /// cache has never seen (their hours came out of the table, not out of a chat
    /// line), and the real name arrives the moment they speak or the next sweep
    /// reports them.</summary>
    public string Display { get; private set; }

    /// <summary>Adopts a newly learned display name. Ignores the login-as-fallback
    /// answer once a real name is known, so a cache eviction cannot demote a row that
    /// is already showing the right thing.</summary>
    internal void UpdateDisplay(string? display)
    {
        string v = string.IsNullOrWhiteSpace(display) ? Login : display!;
        if (string.Equals(v, Login, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Display, Login, StringComparison.OrdinalIgnoreCase)) return;
        if (string.Equals(Display, v, StringComparison.Ordinal)) return;
        Display = v;
        Raise(nameof(Display));
        Raise(nameof(LoginHintVisibility));
    }

    /// <summary>The login, shown as a second line only when it differs from
    /// <see cref="Display"/>.</summary>
    public string LoginHint => Login;

    public Visibility LoginHintVisibility
        => string.Equals(Login, Display, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Collapsed
            : Visibility.Visible;

    /// <summary>Accrued minutes as stored. Set by the ViewModel's reconcile when a
    /// sample credits this viewer — never by the edit fields, which only take effect
    /// once the streamer applies them.</summary>
    public long Minutes
    {
        get => _minutes;
        internal set
        {
            long v = Math.Max(0, value);
            if (_minutes == v) return;
            _minutes = v;
            Raise(nameof(Minutes));
            Raise(nameof(WatchedText));
        }
    }

    /// <summary>The stored time, rendered readably ("12h 30m").</summary>
    public string WatchedText => FormatMinutes(_minutes);

    // ── The manual adjust ────────────────────────────────────────────────
    // Two plain TextBoxes rather than one "12h 30m" field: hours and minutes are what
    // the streamer thinks in, and a free-form duration string would need a parser
    // whose failure mode ("I typed 1:30 and it did nothing") is worse than two boxes.
    // Both reject garbage back to the last good value, the page's numeric idiom.

    public string HoursEdit
    {
        get => _hoursEdit;
        set
        {
            string v = (value ?? "").Trim();
            if (_hoursEdit == v) return;
            _hoursEdit = v;
            Raise(nameof(HoursEdit));
        }
    }

    public string MinutesEdit
    {
        get => _minutesEdit;
        set
        {
            string v = (value ?? "").Trim();
            if (_minutesEdit == v) return;
            _minutesEdit = v;
            Raise(nameof(MinutesEdit));
        }
    }

    /// <summary>
    /// The total the two edit boxes describe, or null when they do not describe a
    /// number at all. Null is the caller's cue to leave the stored value alone and
    /// re-seed the boxes — an unparseable box must never be read as "set it to zero",
    /// which is the one value that silently destroys a viewer's history.
    /// </summary>
    internal long? EditedMinutes()
    {
        if (!TryParse(_hoursEdit, out long hours)) return null;
        if (!TryParse(_minutesEdit, out long minutes)) return null;
        if (hours < 0 || minutes < 0) return null;
        return (hours * 60) + minutes;
    }

    /// <summary>Re-seeds the edit boxes from the stored minutes. Called after an
    /// applied write and after a rejected one, so the fields always end up agreeing
    /// with what is actually stored.</summary>
    internal void SeedEdits()
    {
        _hoursEdit = (_minutes / 60).ToString(CultureInfo.InvariantCulture);
        _minutesEdit = (_minutes % 60).ToString(CultureInfo.InvariantCulture);
        Raise(nameof(HoursEdit));
        Raise(nameof(MinutesEdit));
    }

    // An EMPTY box is deliberately parsed as zero: clearing the minutes field to type
    // "5 hours flat" is the obvious gesture, and refusing it would make the common
    // edit feel broken. A box with something unparseable in it is still rejected.
    private static bool TryParse(string raw, out long value)
    {
        string s = (raw ?? "").Trim();
        if (s.Length == 0) { value = 0; return true; }
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Minutes as "12h 30m" — the two units a streamer reasons about. Under an hour
    /// drops the hours part rather than printing "0h 7m", and exactly-zero is spelled
    /// out, because a bare "0m" in a list of durations reads as a rendering failure.
    /// </summary>
    public static string FormatMinutes(long minutes)
    {
        if (minutes <= 0) return Localizer.T("panel.usermgmt.watchtime.none", "none yet");
        long h = minutes / 60;
        long m = minutes % 60;
        // Three keys rather than one, because the unit letters are what a
        // translation changes and the under-an-hour / exact-hour shapes drop a
        // unit entirely — a single "{0}h {1}m" key could not express that.
        if (h == 0)
            return string.Format(CultureInfo.InvariantCulture,
                Localizer.T("panel.usermgmt.watchtime.dur_minutes", "{0}m"), m);
        if (m == 0)
            return string.Format(CultureInfo.InvariantCulture,
                Localizer.T("panel.usermgmt.watchtime.dur_hours", "{0}h"), h);
        return string.Format(CultureInfo.InvariantCulture,
            Localizer.T("panel.usermgmt.watchtime.dur_hours_minutes", "{0}h {1}m"), h, m);
    }
}
