using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using SoundboardSvc = Phoenix.Controls.Hub.Core.SoundboardService;

namespace Phoenix.Controls.Hub.WinUI.Panels.SoundboardPanel;

/// <summary>
/// One row in the Soundboard list — a single <see cref="SoundDef"/> exposed as its
/// editable settings (command word, clip, aliases, the two cooldowns, enabled toggle)
/// plus the six-way role checkmark set. Every edit mutates the sound on the working
/// config and schedules a debounced save through <c>_changed</c>. Delete is driven by the
/// ViewModel.
///
/// <para>The row carries TWO warnings, and both exist because the failure they describe
/// is otherwise completely silent:</para>
/// <list type="bullet">
/// <item><b>Clip warning</b> — a clip that will not play, in either of the two ways that
/// can be true. <b>Shape</b>: a path the overlay will not fetch, worst case a full Windows
/// path, which no rule downstream rejects OR diagnoses — it 404s and the streamer hears
/// nothing. <see cref="SoundboardSvc.TryValidateClipPath"/> is the same predicate that
/// gates the actual fire, so a shape warning is exactly what will happen. <b>Existence</b>:
/// a perfectly well-formed path to a file that has been renamed, deleted or mistyped by one
/// character — by far the commonest reason a mapped sound goes quiet, and invisible to
/// shape validation. That half is ADVICE rather than a refusal: it is read off the same
/// enumeration the clip picker offers, and the fire path logs it but still fires, because a
/// library scan that is only mostly right must not be able to mute a working board.</item>
/// <item><b>Shadow warning</b> — a command word something that answers FIRST already owns.
/// Two sources: an earlier built-in tool (the chat dispatch is first-handled-wins and logs
/// nothing, so a row named <c>!top</c> would simply never fire while Loyalty is on), and
/// another row on this same board, since nothing stops a word being mapped twice and only
/// one of the two can win.</item>
/// </list>
/// </summary>
public sealed class SoundRowVm : ObservableObject
{
    private readonly SoundDef _sound;
    private readonly Action _changed;
    private readonly Func<string, string?> _reservedVerbOwner;
    private readonly Func<SoundRowVm, string, string?> _boardConflictOwner;
    private readonly Func<string, bool?> _clipInLibrary;

    public SoundRowVm(SoundDef sound, ObservableCollection<string> clipOptions,
                      Func<string, bool?> clipInLibrary,
                      Func<string, string?> reservedVerbOwner,
                      Func<SoundRowVm, string, string?> boardConflictOwner,
                      Action changed)
    {
        _sound = sound ?? throw new ArgumentNullException(nameof(sound));
        _changed = changed ?? (static () => { });
        _reservedVerbOwner = reservedVerbOwner ?? (static _ => null);
        _boardConflictOwner = boardConflictOwner ?? (static (_, _) => null);
        _clipInLibrary = clipInLibrary ?? (static _ => null);
        ClipOptions = clipOptions ?? new ObservableCollection<string>();
        Roles = new SoundboardRolesVm(_sound.Roles ??= SoundboardRoles.All(), _changed);
    }

    /// <summary>The backing model row — used by the ViewModel for remove.</summary>
    internal SoundDef Sound => _sound;

    /// <summary>The page's SHARED clip list (same instance handed to every row), so one
    /// refresh repopulates every picker.</summary>
    public ObservableCollection<string> ClipOptions { get; }

    public string Command
    {
        get => _sound.Command ?? "";
        set
        {
            var v = value ?? "";
            if ((_sound.Command ?? "") == v) return;
            _sound.Command = v;
            Raise(nameof(Command));
            RaiseShadowWarning();
            _changed();
        }
    }

    /// <summary>
    /// The clip, stored in the ONLY form the overlay accepts: relative to the media
    /// library, forward-slashed. Normalised on the way in so a pasted Windows path at
    /// least becomes recognisable (<c>"C:\a\b.mp3"</c> → <c>C:/a/b.mp3</c>, quotes and all
    /// — that is what Explorer's "Copy as path" hands you) and the warning below can name
    /// it, rather than sitting there looking plausible.
    ///
    /// <para>★ The no-change branch still raises. The comparison is between the NORMALISED
    /// input and the stored value, so typing a backslash form over an already-stored slash
    /// form is a no-op on the model while the textbox goes on showing a string the model
    /// does not hold — until some unrelated rebuild snapped it back. Raising on the way out
    /// makes the box re-read the stored value immediately, which is the same
    /// reject-and-snap-back idiom the two cooldown fields use.</para>
    /// </summary>
    public string ClipPath
    {
        get => _sound.ClipPath ?? "";
        set
        {
            var v = SoundboardSvc.NormalizeClipPath(value);
            if ((_sound.ClipPath ?? "") == v) { Raise(nameof(ClipPath)); return; }
            _sound.ClipPath = v;
            Raise(nameof(ClipPath));
            RaiseClipWarning();
            _changed();
        }
    }

    /// <summary>Comma-separated alias editor over the model's <c>List&lt;string&gt;</c>.</summary>
    public string AliasesText
    {
        get => _sound.Aliases == null ? "" : string.Join(", ", _sound.Aliases);
        set
        {
            var parts = (value ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            _sound.Aliases = parts;
            Raise(nameof(AliasesText));
            RaiseShadowWarning();
            _changed();
        }
    }

    public string UserCooldownText
    {
        get => _sound.UserCooldownSeconds.ToString(CultureInfo.InvariantCulture);
        set
        {
            int v = Math.Max(0, ParseInt(value, _sound.UserCooldownSeconds));
            if (_sound.UserCooldownSeconds == v) { Raise(nameof(UserCooldownText)); return; }
            _sound.UserCooldownSeconds = v;
            Raise(nameof(UserCooldownText));
            _changed();
        }
    }

    public string GlobalCooldownText
    {
        get => _sound.GlobalCooldownSeconds.ToString(CultureInfo.InvariantCulture);
        set
        {
            int v = Math.Max(0, ParseInt(value, _sound.GlobalCooldownSeconds));
            if (_sound.GlobalCooldownSeconds == v) { Raise(nameof(GlobalCooldownText)); return; }
            _sound.GlobalCooldownSeconds = v;
            Raise(nameof(GlobalCooldownText));
            _changed();
        }
    }

    public bool Enabled
    {
        get => _sound.Enabled;
        set
        {
            if (_sound.Enabled == value) return;
            _sound.Enabled = value;
            Raise(nameof(Enabled));
            Raise(nameof(EnabledToggleLabel));
            Raise(nameof(EnabledAccentBrush));
            _changed();
        }
    }

    /// <summary>Caption beside the row's toggle pill. The pill replaced a ToggleSwitch
    /// whose OnContent/OffContent carried this text; the pill is display-only, so the
    /// label is a bound property rather than two content slots.</summary>
    public string EnabledToggleLabel => _sound.Enabled
        ? Localizer.T("panel.soundboard.row.enabled.on", "ON")
        : Localizer.T("panel.soundboard.row.enabled.off", "OFF");

    /// <summary>Card left accent rail — ember when the sound is live, divider-coal when
    /// off.</summary>
    public Brush EnabledAccentBrush => _sound.Enabled
        ? (s_accentOn ??= ResolveBrush("EmberPrimaryBrush"))
        : (s_accentOff ??= ResolveBrush("CoalDividerBrush"));

    private static Brush? s_accentOn, s_accentOff;

    // Theme-key lookup with the house CoalFallbackBrush idiom (mirrors CommandRowVm /
    // LiveFeedRowVm.ResolveBrush) so a missing token degrades quietly.
    private static Brush ResolveBrush(string key)
    {
        if (Application.Current?.Resources is { } res)
        {
            if (res.TryGetValue(key, out var resource) && resource is Brush b) return b;
            if (res.TryGetValue("CoalFallbackBrush", out var fb) && fb is Brush f) return f;
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public SoundboardRolesVm Roles { get; }

    // ── Clip warning ────────────────────────────────────────────────────
    /// <summary>
    /// Empty when the clip is playable; otherwise the reason, in the streamer's words.
    ///
    /// <para>Two questions, in order. <b>Shape</b> comes from the SAME predicate the fire
    /// path uses, so a shape warning can never promise something the service will then
    /// refuse. <b>Existence</b> is asked only once the shape is fine, off the clip list the
    /// picker is filled from — which is the same enumeration, over the same media root and
    /// the same audio extensions, that the overlay will later fetch through. It answers
    /// "cannot tell" (and warns about nothing) until that list has actually been
    /// scanned.</para>
    ///
    /// <para>The existence half is deliberately worded as advice, not refusal: the service
    /// logs it and fires anyway, because a probe that is only mostly right must not be able
    /// to mute a working board.</para>
    /// </summary>
    public string ClipWarningText
    {
        get
        {
            if (!SoundboardSvc.TryValidateClipPath(_sound.ClipPath, out string clip, out string reason))
                return string.Format(
                    Localizer.T("panel.soundboard.row.clip.warn.shape", "This clip {0}."), reason);
            return _clipInLibrary(clip) == false
                ? Localizer.T("panel.soundboard.row.clip.warn.missing",
                              "No such clip in the media library — pick one from the list, or add the file and refresh.")
                : "";
        }
    }

    public Visibility ClipWarningVisibility
        => ClipWarningText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Re-evaluates the clip warning. Called by the page after the media library
    /// is re-scanned — the answer can change without this row changing.</summary>
    public void RaiseClipWarning()
    {
        Raise(nameof(ClipWarningText));
        Raise(nameof(ClipWarningVisibility));
    }

    // ── Shadow warning ──────────────────────────────────────────────────
    /// <summary>
    /// Empty unless one of this row's words is already claimed by something that answers
    /// FIRST. Two sources, checked in dispatch order:
    /// <list type="number">
    /// <item>A built-in tool that runs EARLIER in the chat dispatch. Those answer first and
    /// this row never fires — silently, because the dispatcher logs nothing on a
    /// first-handled win. ★ That holds only WHILE the owning tool is on:
    /// <c>DispatchBuiltInChatAsync</c> skips an inactive provider outright, and
    /// <c>ReservedVerbOwner</c> deliberately does not consult enabled state so the advice
    /// arrives before the streamer commits to a word. Hence the qualifier in the text —
    /// without it the warning tells them to rename a command that plays right now.</item>
    /// <item>Another row on THIS board. The collision scan used to look outward at the
    /// other tools and never at the board itself, so mapping the same word twice was
    /// invisible from both rows: the service picks one of them and the other is simply
    /// dead. The page's <c>BoardConflictOwner</c> resolves the winner by the same rule the
    /// service does, so this names the row that actually plays.</item>
    /// </list>
    /// </summary>
    public string ShadowWarningText
    {
        get
        {
            // Both sentences add the BANG themselves, so every word they interpolate goes
            // through ChatVerb.Canonical first. A row saved as "!airhorn" fires perfectly
            // well — SoundboardService compares through the same canonicalizer — and would
            // otherwise be reported to the streamer as "!!airhorn", a token no parser will
            // ever see.
            foreach (var word in Words())
            {
                string? owner = _reservedVerbOwner(word);
                if (owner is null) continue;
                return string.Format(
                    Localizer.T("panel.soundboard.row.shadow.warn.builtin",
                                "!{0} belongs to {1} while it is on — pick another word."),
                    ChatVerb.Canonical(word), owner);
            }
            foreach (var word in Words())
            {
                string? owner = _boardConflictOwner(this, word);
                if (owner is null) continue;
                return string.Format(
                    Localizer.T("panel.soundboard.row.shadow.warn.board",
                                "!{0} on this board already answers !{1} — rename one."),
                    ChatVerb.Canonical(owner), ChatVerb.Canonical(word));
            }
            return "";
        }
    }

    public Visibility ShadowWarningVisibility
        => ShadowWarningText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Re-evaluates the shadow warning. Called by the page after anything that
    /// can change the answer without changing THIS row — another tool's config landing,
    /// or the streamer hitting refresh.</summary>
    public void RaiseShadowWarning()
    {
        Raise(nameof(ShadowWarningText));
        Raise(nameof(ShadowWarningVisibility));
    }

    /// <summary>True when this row answers to <paramref name="word"/> — its command or any
    /// of its aliases, matched the way the service matches them.</summary>
    /// <remarks>
    /// "The way the service matches them" is <see cref="ChatVerb"/>, which is what
    /// <c>SoundboardService.Claims</c> uses — not a trim-and-compare. The difference is a
    /// live under-report: a row saved as "!airhorn" and one saved as "airhorn" are the same
    /// word to <c>Find</c>, so only one of the two can ever play, and a plain
    /// <c>OrdinalIgnoreCase</c> compare called them different and left the losing row
    /// looking healthy.
    /// </remarks>
    internal bool ClaimsWord(string word)
    {
        foreach (var mine in Words())
            if (ChatVerb.Matches(mine, word)) return true;
        return false;
    }

    /// <summary>Every word this row would answer to: the command plus its aliases.</summary>
    private IEnumerable<string> Words()
    {
        string cmd = (_sound.Command ?? "").Trim();
        if (cmd.Length > 0) yield return cmd;
        if (_sound.Aliases == null) yield break;
        foreach (var a in _sound.Aliases)
        {
            string t = (a ?? "").Trim();
            if (t.Length > 0) yield return t;
        }
    }

    private static int ParseInt(string? s, int fallback)
        => int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
