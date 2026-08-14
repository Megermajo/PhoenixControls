using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using HubCore = Phoenix.Controls.Hub.Core;
using SongSvc = Phoenix.Controls.Hub.Core.SongRequestService;

namespace Phoenix.Controls.Hub.WinUI.Panels.SongRequestPanel;

/// <summary>
/// ViewModel for the Hub Song Request page — the button-side front-end onto the same queue
/// the song.* script nodes and the !sr / !skip / !play chat verbs drive. Reaches
/// <see cref="SongSvc.Instance"/> DIRECTLY for reads AND writes and subscribes to its
/// ConfigChanged / QueueChanged events.
///
/// Config editing model: the whole <see cref="SongRequestConfig"/> is deep-cloned into a
/// private working copy; every settings field edits that clone and schedules a debounced
/// SaveConfigAsync. Because SaveConfigAsync deep-clones AND Normalize()s the incoming
/// object, the persisted config differs both by reference and (after a trim / a blanked
/// command word) byte-for-byte, so a serialized-content compare would mis-read our own
/// save as foreign and rebuild the settings mid-edit. A one-shot <c>_selfSaving</c> flag
/// detects it deterministically instead — the QuotesViewModel flavour of the guard.
///
/// ★ WHAT THIS PAGE SHOWS, AND WHAT IT STILL DOES NOT. The audio player is an OBS overlay
/// widget (V15's <c>Player</c> preset), and it lives in the BROWSER, not here. So the page
/// still ships NO player affordance that would lie about it: no seek bar, no elapsed-time
/// readout, no embedded video area, no progress ring. Hub cannot know any of those — it
/// publishes the intent and the widget obeys it; the only thing the widget reports back is
/// "the track ended". What the page shows is real and observable right now — the queue, the
/// caps, the price, the moderation lane, and the transport STATE (which track is selected,
/// playing or held, at what volume) that Phoenix publishes on the Overlay Live Channel
/// under <c>songrequest.*</c>. The Skip / Pause / Play buttons move that state and the
/// queue, which the page itself reflects immediately; the banner at the top says plainly
/// that nothing inside Hub makes sound.
/// </summary>
public sealed class SongRequestViewModel : ObservableObject, IDisposable
{
    private readonly SongSvc _svc = SongSvc.Instance;
    private readonly UiDispatcherPump _ui;
    private readonly DispatcherQueueTimer? _saveTimer;
    private readonly DispatcherQueueTimer? _pillTimer;

    private SongRequestConfig _working;
    private bool _disposed;
    private bool _dirty;
    private bool _loaded;
    private bool _selfSaving;

    // Last pill state published, so the heartbeat below raises nothing while nothing moved.
    private SongSvc.SongRequestPillState _pillState;

    public SongRequestViewModel(DispatcherQueue? dispatcher)
    {
        _ui = new UiDispatcherPump(dispatcher);
        _working = Clone(_svc.Config);

        if (dispatcher is not null)
        {
            _saveTimer = dispatcher.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => _ = SaveWorkingAsync();

            // The "nothing will play" arm of the pill asks LayerRegistry whether ANY
            // browser surface is attached, and an OBS Browser Source opening or closing
            // raises no event this VM subscribes to — QueueChanged only fires when the
            // QUEUE moves. Without a heartbeat the chip would keep claiming "playing"
            // after the overlay window was closed. RefreshPill returns without raising
            // unless the state actually changed, so an idle tab pays a single enum read.
            _pillTimer = dispatcher.CreateTimer();
            _pillTimer.Interval = TimeSpan.FromSeconds(5);
            _pillTimer.IsRepeating = true;
            _pillTimer.Tick += (_, _) => RefreshPill();
            _pillTimer.Start();
        }

        _pillState = _svc.PillState;

        BuildSettings();

        _svc.ConfigChanged += OnConfigChanged;
        _svc.QueueChanged += OnQueueChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _svc.ConfigChanged -= OnConfigChanged;
        _svc.QueueChanged -= OnQueueChanged;
        _saveTimer?.Stop();
        _pillTimer?.Stop();
        if (_dirty)
        {
            _dirty = false;
            // Parked, not dropped: at shutdown MainWindow's coordinator ends in
            // Environment.Exit(0), which killed this write mid-flight. The tracker lets
            // PreBuildsHostView.DisposeAllTools hand it to the coordinator as a tracked step;
            // mid-session tab closes behave exactly as before.
            Phoenix.Controls.Hub.WinUI.Controls.ToolConfigFlushTracker.Register(
                Phoenix.Controls.Hub.Core.AsyncErrorBoundary.SafeRunAsync(
                    () => _svc.SaveConfigAsync(_working), "SongRequestViewModel", "final config flush"));
        }
    }

    // ── Bound queue ─────────────────────────────────────────────────────
    public ObservableCollection<SongRequestRowVm> Queue { get; } = new();
    public bool HasQueue => Queue.Count > 0;
    public Visibility EmptyVisibility => HasQueue ? Visibility.Collapsed : Visibility.Visible;
    public string CountText => string.Format(
        Localizer.T("panel.songrequest.queue.count", "{0} waiting"), Queue.Count);

    // ── Detail-column sections (the settings body is ~35 controls + 4 role rows, which
    //    does not read as one list at the shell's 1.4* width) ─────────────
    public IList<string> SectionNames { get; } = new List<string>
    {
        Localizer.T("panel.songrequest.section.player", "PLAYER"),
        Localizer.T("panel.songrequest.section.settings", "SETTINGS"),
    };

    private int _selectedSection;

    public int SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (!Set(ref _selectedSection, value)) return;
            Raise(nameof(PlayerSectionVisibility));
            Raise(nameof(SettingsSectionVisibility));
        }
    }

    public Visibility PlayerSectionVisibility
        => _selectedSection == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SettingsSectionVisibility
        => _selectedSection == 1 ? Visibility.Visible : Visibility.Collapsed;

    // ── Current selection (state, never a playback position) ────────────
    private string _currentTitle = "";
    private string _currentRequester = "";
    private string _currentVideoId = "";

    public string CurrentTitle => _currentTitle.Length > 0
        ? _currentTitle
        : Localizer.T("panel.songrequest.player.nothing_selected", "Nothing selected");

    public string CurrentRequesterHint => _currentRequester.Length > 0
        ? string.Format(Localizer.T("panel.songrequest.player.requested_by", "requested by {0}"), _currentRequester)
        : "";
    public string CurrentVideoHint => _currentVideoId.Length > 0 ? $"youtube:{_currentVideoId}" : "";

    /// <summary>The sentence that keeps this page honest about WHERE playback happens.
    /// Shown as a standing banner, not a dismissible toast — it is not a "not built yet"
    /// notice that expires, it is the permanent division of labour: Hub owns the queue and
    /// the intent, the browser owns the sound.</summary>
    public string PlayerAvailabilityText =>
        Localizer.T("panel.songrequest.player.availability",
            "Sound comes from a Player widget on a Visualist layer behind an OBS Browser Source, never " +
            "from Hub. Without one the queue still works but only moves on Skip / Play.");

    // ── Master switch ───────────────────────────────────────────────────
    public bool MasterEnabled
    {
        get => _working.Enabled;
        set
        {
            if (_working.Enabled == value) return;
            _working.Enabled = value;
            Raise(nameof(MasterEnabled));
            SaveNow();
            RefreshPill();
        }
    }

    // ── Status pill ─────────────────────────────────────────────────────
    // Decided service-side (SongRequestService.PillState) so the panel projects ONE enum
    // instead of recomputing a state machine, and so the state machine stays reachable
    // from the test suite, which cannot construct this ViewModel at all.
    public string StatusPillText => _pillState switch
    {
        SongSvc.SongRequestPillState.Dormant => "dormant",
        SongSvc.SongRequestPillState.QueueingNothingRenders => "queueing · nothing will play",
        SongSvc.SongRequestPillState.Playing => "playing",
        SongSvc.SongRequestPillState.Paused => "paused",
        _ => "idle",
    };

    // Re-reads the service state and raises ONLY when it moved. UI thread only.
    private void RefreshPill()
    {
        if (_disposed) return;
        var state = _svc.PillState;
        if (state == _pillState) return;
        _pillState = state;
        Raise(nameof(StatusPillText));
    }

    // ── Command words ───────────────────────────────────────────────────
    // Twelve near-identical properties rather than one indexer: x:Bind is compile-time and
    // needs a named property per box, which is the same trade every sibling tool panel
    // makes for its command words.
    public string RequestCommand
    {
        get => _working.RequestCommand ?? "";
        set { string v = value ?? ""; if ((_working.RequestCommand ?? "") == v) return; _working.RequestCommand = v; Raise(nameof(RequestCommand)); ScheduleSave(); }
    }
    public string CurrentCommand
    {
        get => _working.CurrentCommand ?? "";
        set { string v = value ?? ""; if ((_working.CurrentCommand ?? "") == v) return; _working.CurrentCommand = v; Raise(nameof(CurrentCommand)); ScheduleSave(); }
    }
    public string NextCommand
    {
        get => _working.NextCommand ?? "";
        set { string v = value ?? ""; if ((_working.NextCommand ?? "") == v) return; _working.NextCommand = v; Raise(nameof(NextCommand)); ScheduleSave(); }
    }
    public string WhenCommand
    {
        get => _working.WhenCommand ?? "";
        set { string v = value ?? ""; if ((_working.WhenCommand ?? "") == v) return; _working.WhenCommand = v; Raise(nameof(WhenCommand)); ScheduleSave(); }
    }
    public string WrongSongCommand
    {
        get => _working.WrongSongCommand ?? "";
        set { string v = value ?? ""; if ((_working.WrongSongCommand ?? "") == v) return; _working.WrongSongCommand = v; Raise(nameof(WrongSongCommand)); ScheduleSave(); }
    }
    public string VoteSkipCommand
    {
        get => _working.VoteSkipCommand ?? "";
        set { string v = value ?? ""; if ((_working.VoteSkipCommand ?? "") == v) return; _working.VoteSkipCommand = v; Raise(nameof(VoteSkipCommand)); ScheduleSave(); }
    }
    public string VolumeCommand
    {
        get => _working.VolumeCommand ?? "";
        set { string v = value ?? ""; if ((_working.VolumeCommand ?? "") == v) return; _working.VolumeCommand = v; Raise(nameof(VolumeCommand)); ScheduleSave(); }
    }
    public string SkipCommand
    {
        get => _working.SkipCommand ?? "";
        set { string v = value ?? ""; if ((_working.SkipCommand ?? "") == v) return; _working.SkipCommand = v; Raise(nameof(SkipCommand)); ScheduleSave(); }
    }
    public string PauseCommand
    {
        get => _working.PauseCommand ?? "";
        set { string v = value ?? ""; if ((_working.PauseCommand ?? "") == v) return; _working.PauseCommand = v; Raise(nameof(PauseCommand)); ScheduleSave(); }
    }
    public string PlayCommand
    {
        get => _working.PlayCommand ?? "";
        set { string v = value ?? ""; if ((_working.PlayCommand ?? "") == v) return; _working.PlayCommand = v; Raise(nameof(PlayCommand)); ScheduleSave(); }
    }
    public string RemoveCommand
    {
        get => _working.RemoveCommand ?? "";
        set { string v = value ?? ""; if ((_working.RemoveCommand ?? "") == v) return; _working.RemoveCommand = v; Raise(nameof(RemoveCommand)); ScheduleSave(); }
    }
    public string ClearCommand
    {
        get => _working.ClearCommand ?? "";
        set { string v = value ?? ""; if ((_working.ClearCommand ?? "") == v) return; _working.ClearCommand = v; Raise(nameof(ClearCommand)); ScheduleSave(); }
    }

    // ── The page's own chat verbs ───────────────────────────────────────
    /// <summary>
    /// Every chat verb this tool answers to, as the CHAT COMMANDS block renders it —
    /// derived from the WORKING config, so a word the streamer just typed into one of the
    /// twelve boxes above shows immediately rather than 400 ms later when the debounced
    /// save lands.
    /// </summary>
    /// <remarks>
    /// <para>Thirteen rows for twelve words: <c>!volume</c> is gated TWICE on the same word
    /// — reading the level is a ViewRoles question, setting it a ModRoles one — and the
    /// catalogue renders that as two rows because one row could only name one of the two
    /// gates. That is the single fact on this page the twelve boxes cannot show, and the
    /// reason the block earns its space here at all.</para>
    ///
    /// <para>A fresh list on every read, deliberately: <c>ToolCommandList.Commands</c> is a
    /// dependency property and repaints on reference inequality, so a cached instance
    /// edited in place would update nothing. Raised from <c>ScheduleSave</c> /
    /// <c>SaveNow</c> / <c>BuildSettings</c> — between them every path that can move a verb
    /// or a role tick, including a foreign reload.</para>
    /// </remarks>
    public IReadOnlyList<HubCore.ToolCommandInfo> ChatCommands
        => HubCore.BuiltInCommandCatalog.ForSongRequest(_working);

    // ── Caps / price / moderation ───────────────────────────────────────
    // Numeric boxes bind through string properties so a half-typed value ("1" on the way
    // to "15") never round-trips to 0 and back under the cursor — the same shape the other
    // tool panels use for their integer fields.
    public string MaxQueueLength
    {
        get => _working.MaxQueueLength.ToString(CultureInfo.InvariantCulture);
        set { _working.MaxQueueLength = ParseInt(value, _working.MaxQueueLength); Raise(nameof(MaxQueueLength)); ScheduleSave(); }
    }
    public string MaxPerUser
    {
        get => _working.MaxPerUser.ToString(CultureInfo.InvariantCulture);
        set { _working.MaxPerUser = ParseInt(value, _working.MaxPerUser); Raise(nameof(MaxPerUser)); ScheduleSave(); }
    }
    public string MaxDurationSeconds
    {
        get => _working.MaxDurationSeconds.ToString(CultureInfo.InvariantCulture);
        set { _working.MaxDurationSeconds = ParseInt(value, _working.MaxDurationSeconds); Raise(nameof(MaxDurationSeconds)); ScheduleSave(); }
    }
    public string RequestCooldownSeconds
    {
        get => _working.RequestCooldownSeconds.ToString(CultureInfo.InvariantCulture);
        set { _working.RequestCooldownSeconds = ParseInt(value, _working.RequestCooldownSeconds); Raise(nameof(RequestCooldownSeconds)); ScheduleSave(); }
    }
    public string PointCost
    {
        get => _working.PointCost.ToString(CultureInfo.InvariantCulture);
        set { _working.PointCost = ParseInt(value, (int)_working.PointCost); Raise(nameof(PointCost)); ScheduleSave(); }
    }
    public string SubDiscountPercent
    {
        get => _working.SubDiscountPercent.ToString(CultureInfo.InvariantCulture);
        set { _working.SubDiscountPercent = ParseInt(value, _working.SubDiscountPercent); Raise(nameof(SubDiscountPercent)); ScheduleSave(); }
    }
    public string VoteSkipThreshold
    {
        get => _working.VoteSkipThreshold.ToString(CultureInfo.InvariantCulture);
        set { _working.VoteSkipThreshold = ParseInt(value, _working.VoteSkipThreshold); Raise(nameof(VoteSkipThreshold)); ScheduleSave(); }
    }
    public string PlayerVolume
    {
        get => _working.Volume.ToString(CultureInfo.InvariantCulture);
        set { _working.Volume = Math.Clamp(ParseInt(value, _working.Volume), 0, 100); Raise(nameof(PlayerVolume)); ScheduleSave(); }
    }

    public bool RequireApproval
    {
        get => _working.RequireApproval;
        set { if (_working.RequireApproval == value) return; _working.RequireApproval = value; Raise(nameof(RequireApproval)); ScheduleSave(); }
    }

    /// <summary>Standing hint under the max-duration box. The cap is genuinely
    /// unenforceable without the API key — saying so beats letting a streamer believe a
    /// 10-minute limit is holding when nothing reads a video's length.</summary>
    public string DurationCapHint =>
        (ConfigManager.Current.YouTubeDataApiKey ?? "").Trim().Length > 0
            ? Localizer.T("panel.songrequest.limits.max_length.hint", "0 = no limit.")
            : Localizer.T("panel.songrequest.limits.max_length.hint_no_api",
                "0 = no limit · without a YouTube Data API key the length is unknown and the cap is skipped");

    /// <summary>Standing hint under the price box, for the same reason: a price the points
    /// economy cannot collect refuses every request, and the streamer should learn that
    /// here rather than from chat.</summary>
    public string PriceHint =>
        Phoenix.Controls.Hub.Core.LoyaltyService.Instance.Active
            ? Localizer.T("panel.songrequest.price.cost.hint",
                "0 = free. Charged when the request is accepted, refunded if it is removed or rejected.")
            : Localizer.T("panel.songrequest.price.cost.hint_no_loyalty",
                "0 = free · Loyalty is off, so any price above 0 refuses every request");

    // ── Role checkmark sets (rebuilt on foreign reload) ─────────────────
    public SongRolesVm RequestRoles { get; private set; } = null!;
    public SongRolesVm ViewRoles { get; private set; } = null!;
    public SongRolesVm VoteSkipRoles { get; private set; } = null!;
    public SongRolesVm ModRoles { get; private set; } = null!;

    private void BuildSettings()
    {
        _working.RequestRoles  ??= SongRoles.All();
        _working.ViewRoles     ??= SongRoles.All();
        _working.VoteSkipRoles ??= SongRoles.All();
        _working.ModRoles      ??= SongRoles.Mods();
        RequestRoles  = new SongRolesVm(_working.RequestRoles, ScheduleSave);
        ViewRoles     = new SongRolesVm(_working.ViewRoles, ScheduleSave);
        VoteSkipRoles = new SongRolesVm(_working.VoteSkipRoles, ScheduleSave);
        ModRoles      = new SongRolesVm(_working.ModRoles, ScheduleSave);

        Raise(nameof(RequestRoles));
        Raise(nameof(ViewRoles));
        Raise(nameof(VoteSkipRoles));
        Raise(nameof(ModRoles));
        Raise(nameof(RequestCommand));
        Raise(nameof(CurrentCommand));
        Raise(nameof(NextCommand));
        Raise(nameof(WhenCommand));
        Raise(nameof(WrongSongCommand));
        Raise(nameof(VoteSkipCommand));
        Raise(nameof(VolumeCommand));
        Raise(nameof(SkipCommand));
        Raise(nameof(PauseCommand));
        Raise(nameof(PlayCommand));
        Raise(nameof(RemoveCommand));
        Raise(nameof(ClearCommand));
        Raise(nameof(MaxQueueLength));
        Raise(nameof(MaxPerUser));
        Raise(nameof(MaxDurationSeconds));
        Raise(nameof(RequestCooldownSeconds));
        Raise(nameof(PointCost));
        Raise(nameof(SubDiscountPercent));
        Raise(nameof(VoteSkipThreshold));
        Raise(nameof(PlayerVolume));
        Raise(nameof(RequireApproval));
        Raise(nameof(DurationCapHint));
        Raise(nameof(PriceHint));
        Raise(nameof(MasterEnabled));
        Raise(nameof(ChatCommands));
    }

    // ── Load / refresh ──────────────────────────────────────────────────
    public Task LoadAsync()
    {
        if (_loaded) return Task.CompletedTask;
        _loaded = true;
        RefreshQueue();
        return Task.CompletedTask;
    }

    /// <summary>Rebuilds the bound rows from one service snapshot. A plain rebuild rather
    /// than the in-place reconcile QuotesViewModel needs: these rows carry no editable
    /// field, so there is no in-progress edit a rebuild could discard.</summary>
    private void RefreshQueue()
    {
        var snap = _svc.Snapshot();
        _ui.Post(() =>
        {
            Queue.Clear();
            for (int i = 0; i < snap.Queue.Count; i++)
                Queue.Add(new SongRequestRowVm(snap.Queue[i], i + 1));

            _currentTitle = snap.Current?.DisplayTitle ?? "";
            _currentRequester = snap.Current?.RequestedBy ?? "";
            _currentVideoId = snap.Current?.VideoId ?? "";

            Raise(nameof(HasQueue));
            Raise(nameof(EmptyVisibility));
            Raise(nameof(CountText));
            Raise(nameof(CurrentTitle));
            Raise(nameof(CurrentRequesterHint));
            Raise(nameof(CurrentVideoHint));

            // Both hints read state this VM does not own — the API key on AppConfig and the
            // Loyalty master toggle — and neither raises an event this page subscribes to.
            // Re-reading them on every refresh is what keeps "needs an API key" from
            // standing there after the streamer has just entered one in Settings.
            Raise(nameof(DurationCapHint));
            Raise(nameof(PriceHint));

            RefreshPill();
        });
    }

    public void Refresh() => RefreshQueue();

    // ── Queue / transport actions ───────────────────────────────────────
    /// <summary>SKIP means "cut the playing track short", so it is a no-op on an idle
    /// player — starting the queue is what PLAY does. The chat verb and the Song.Skip node
    /// guard identically; without the guard the two buttons would silently do the same
    /// thing whenever nothing was selected.</summary>
    public async Task SkipAsync()
    {
        try
        {
            if (_svc.Snapshot().Current is null) return;
            await _svc.AdvanceAsync("panel").ConfigureAwait(false);
        }
        catch (Exception ex) { GlobalLogger.Error("SongRequestViewModel", "SkipAsync failed", ex); }
    }

    public void PausePlayer()
    {
        try { _svc.Pause(); }
        catch (Exception ex) { GlobalLogger.Error("SongRequestViewModel", "Pause failed", ex); }
    }

    public async Task PlayAsync()
    {
        try { await _svc.ResumeAsync().ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("SongRequestViewModel", "PlayAsync failed", ex); }
    }

    /// <summary>
    /// Empties the waiting queue. ClearAsync returns <c>(Dropped, Unrefunded)</c> and the
    /// second number matters: the button's tooltip promises a refund, and ONE clear can
    /// strand a whole queue's worth of charges when the points economy refuses to give
    /// them back. That outcome used to be discarded here, so a streamer had no way to
    /// learn it happened. It is reported through GlobalLogger rather than a dialog — this
    /// is a repeatable outcome of an ordinary button, not a one-off confirmation — at
    /// CriticalError when points are still owed and System when everything came back.
    /// </summary>
    public async Task ClearQueueAsync()
    {
        try
        {
            var (dropped, unrefunded) = await _svc.ClearAsync().ConfigureAwait(false);
            if (dropped <= 0) return;
            GlobalLogger.Log(
                $"Song Request: the waiting queue was cleared from the panel — " +
                $"{dropped.ToString(CultureInfo.InvariantCulture)} request(s) dropped, " +
                $"{unrefunded.ToString(CultureInfo.InvariantCulture)} charge(s) still owed.",
                "SongRequestViewModel",
                unrefunded > 0 ? LogLevel.CriticalError : LogLevel.System);
        }
        catch (Exception ex) { GlobalLogger.Error("SongRequestViewModel", "ClearQueueAsync failed", ex); }
    }

    public async Task RemoveAsync(SongRequestRowVm row)
    {
        if (row is null) return;
        try { await _svc.RemoveByIdAsync(row.Id).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("SongRequestViewModel", "RemoveAsync failed", ex); }
    }

    public void ApproveRow(SongRequestRowVm row)
    {
        if (row is null) return;
        try { _svc.ApproveById(row.Id); }
        catch (Exception ex) { GlobalLogger.Error("SongRequestViewModel", "ApproveRow failed", ex); }
    }

    public async Task DenyAsync(SongRequestRowVm row)
    {
        if (row is null) return;
        try { await _svc.DenyByIdAsync(row.Id).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("SongRequestViewModel", "DenyAsync failed", ex); }
    }

    // ── Persistence ─────────────────────────────────────────────────────
    // Both entry points re-raise ChatCommands: they are the choke point every settings edit
    // funnels through (the twelve verb boxes, all four role rows), so the command block
    // repaints from the WORKING copy the moment the edit lands rather than when the
    // debounced save does.
    private void ScheduleSave()
    {
        _dirty = true;
        Raise(nameof(ChatCommands));
        if (_saveTimer is not null) { _saveTimer.Stop(); _saveTimer.Start(); }
        else _ = SaveWorkingAsync();
    }

    private void SaveNow()
    {
        _dirty = true;
        Raise(nameof(ChatCommands));
        _saveTimer?.Stop();
        _ = SaveWorkingAsync();
    }

    private async Task SaveWorkingAsync()
    {
        if (!_dirty) return;
        _dirty = false;
        // SaveConfigAsync raises ConfigChanged synchronously (before this await returns),
        // so hold the self-save flag across the whole call — OnConfigChanged fires while
        // it is set and bails out.
        _selfSaving = true;
        try { await _svc.SaveConfigAsync(_working).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("SongRequestViewModel", "SaveConfigAsync failed", ex); }
        finally { _selfSaving = false; }
        // The save can change what the queue projection shows (current-track
        // fields, availability), so refresh it. Live transport volume itself is
        // no longer displayed anywhere on this page — only the config-backed
        // volume box — since the stat tiles left with the config-surface rewrite.
        RefreshQueue();
    }

    // ── Service events ──────────────────────────────────────────────────
    private void OnConfigChanged(object? sender, EventArgs e)
    {
        if (_selfSaving) return;
        _ui.Post(() =>
        {
            _working = Clone(_svc.Config);
            BuildSettings();
        });
        RefreshQueue();
    }

    private void OnQueueChanged(object? sender, EventArgs e) => RefreshQueue();

    private static int ParseInt(string? raw, int fallback)
        => int.TryParse((raw ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v
            // A cleared box is 0 ("no limit" for every cap here); anything unparseable
            // keeps the previous value rather than silently zeroing a cap mid-keystroke.
            : ((raw ?? "").Trim().Length == 0 ? 0 : fallback);

    private static SongRequestConfig Clone(SongRequestConfig src)
    {
        try
        {
            string json = JsonSerializer.Serialize(src ?? new SongRequestConfig());
            return JsonSerializer.Deserialize<SongRequestConfig>(json) ?? new SongRequestConfig();
        }
        catch { return new SongRequestConfig(); }
    }
}
