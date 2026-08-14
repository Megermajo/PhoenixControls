using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using HubCore = Phoenix.Controls.Hub.Core;
using SoundboardSvc = Phoenix.Controls.Hub.Core.SoundboardService;
using LayerRt = Phoenix.Controls.Hub.Core.LayerRuntime;

namespace Phoenix.Controls.Hub.WinUI.Panels.SoundboardPanel;

/// <summary>
/// ViewModel for the Hub Soundboard page — the button-side front-end onto the same row
/// list the built-in chat provider drives. Reaches <see cref="SoundboardSvc.Instance"/>
/// DIRECTLY and subscribes to its ConfigChanged event.
///
/// Config editing model (clones CustomCommandsViewModel): the whole
/// <see cref="SoundboardConfig"/> is deep-cloned into a private working copy. Every field
/// edits that clone and schedules a debounced <c>SaveConfigAsync</c>. Because
/// SaveConfigAsync deep-clones the incoming object (so the panel can't alias the hot-path
/// config), a self-triggered ConfigChanged is detected by comparing serialized content
/// (not reference) and skipped; a foreign change rebuilds the rows.
///
/// <para>Three of this page's jobs exist purely to make silent failures visible, which is
/// the whole reason a soundboard needs a panel richer than "a list of words":</para>
/// <list type="bullet">
/// <item>The clip picker is fed from the media library the OVERLAY reads, so the paths it
/// offers are already in the relative form a wired Audio.Load.Path requires — and that same
/// list is what <see cref="ClipInLibrary"/> answers from, so a row whose file has been
/// renamed away says so instead of 404ing in silence.</item>
/// <item><see cref="ReservedVerbOwner"/> names any earlier built-in that already owns a
/// row's word, and <see cref="BoardConflictOwner"/> names any row of THIS board that does —
/// the chat dispatch is first-handled-wins and logs nothing, in both directions.</item>
/// <item>The overlay hookup warns when it is unset, because a board with no layer/trigger
/// consumes its words and plays nothing.</item>
/// </list>
/// </summary>
public sealed class SoundboardViewModel : ObservableObject, IDisposable
{
    private readonly SoundboardSvc _svc = SoundboardSvc.Instance;
    private readonly UiDispatcherPump _ui;
    private readonly DispatcherQueueTimer? _saveTimer;

    private SoundboardConfig _working;
    private bool _disposed;
    private bool _dirty;
    private bool _loaded;

    // True only when the LiveLayerChanged subscription actually took, so Dispose
    // detaches exactly what it attached (LayerRuntime is unreachable in early boot /
    // design contexts and the hook is allowed to fail there).
    private bool _layerEventsHooked;

    // True once RefreshClips has completed a scan without faulting — INCLUDING a scan that
    // found nothing. That distinction is the whole point: an empty ClipOptions before the
    // first scan means "not asked yet" (warn about nothing), while an empty one after a
    // successful scan means the media library genuinely holds no audio, which is the case
    // where every mapped row is broken and most deserves to say so.
    private bool _clipsScanned;

    public SoundboardViewModel(DispatcherQueue? dispatcher)
    {
        _ui = new UiDispatcherPump(dispatcher);
        _working = Clone(_svc.Config);

        if (dispatcher is not null)
        {
            _saveTimer = dispatcher.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => _ = SaveWorkingAsync();
        }

        BuildRows();

        _svc.ConfigChanged += OnConfigChanged;

        // The overlay either has a browser source attached or it does not, and that is
        // half of what the status pill reports. The registry raises the transition, so
        // the pill tracks OBS opening and closing the source without polling.
        try
        {
            LayerRt.Instance.Registry.LiveLayerChanged += OnLiveLayerChanged;
            _layerEventsHooked = true;
        }
        catch (Exception ex)
        {
            // Same posture as RefreshLayers: LayerRuntime may not be initialized yet.
            // The pill then updates on save / refresh instead of on the transition.
            GlobalLogger.Error("SoundboardViewModel", "layer liveness subscription failed", ex);
        }

        // Seed the header-band state before the first paint.
        RefreshPill();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _svc.ConfigChanged -= OnConfigChanged;
        if (_layerEventsHooked)
        {
            _layerEventsHooked = false;
            try { LayerRt.Instance.Registry.LiveLayerChanged -= OnLiveLayerChanged; }
            catch (Exception ex)
            { GlobalLogger.Error("SoundboardViewModel", "layer liveness unsubscribe failed", ex); }
        }
        foreach (var row in Sounds) row.PropertyChanged -= OnRowPropertyChanged;
        _saveTimer?.Stop();
        if (_dirty)
        {
            _dirty = false;
            // Parked, not dropped: at shutdown MainWindow's coordinator ends in
            // Environment.Exit(0), which killed this write mid-flight. The tracker lets
            // PreBuildsHostView.DisposeAllTools hand it to the coordinator as a tracked step;
            // mid-session tab closes behave exactly as before.
            Phoenix.Controls.Hub.WinUI.Controls.ToolConfigFlushTracker.Register(
                Phoenix.Controls.Hub.Core.AsyncErrorBoundary.SafeRunAsync(
                    () => _svc.SaveConfigAsync(_working), "SoundboardViewModel", "final config flush"));
        }
    }

    // ── Bound collections ───────────────────────────────────────────────
    public ObservableCollection<SoundRowVm> Sounds { get; } = new();

    /// <summary>Registered overlay-layer ids, filled lazily from the layer registry.</summary>
    public ObservableCollection<string> LayerIds { get; } = new();

    /// <summary>Trigger names on the selected layer.</summary>
    public ObservableCollection<string> TriggerNames { get; } = new();

    /// <summary>Every audio file in the media library, as the relative forward-slashed
    /// path the overlay will fetch. SHARED across all rows — one refresh repopulates every
    /// picker.</summary>
    public ObservableCollection<string> ClipOptions { get; } = new();

    /// <summary>
    /// Every chat verb this board answers to — each row's command word AND each of its
    /// aliases, which the rows themselves never show side by side — as the CHAT COMMANDS
    /// block renders it. Derived from the WORKING config, so an edit shows immediately
    /// rather than 400 ms later when the debounced save lands.
    /// </summary>
    /// <remarks>
    /// A fresh list on every read, deliberately: <c>ToolCommandList.Commands</c> is a
    /// dependency property and repaints on reference inequality, so a cached instance
    /// edited in place would update nothing. Every <c>BuiltInCommandCatalog.For*</c> call
    /// allocates, so the natural shape is the correct one. Raised from
    /// <see cref="RaiseListCounters"/> — see the note there for why that is the single site.
    /// </remarks>
    public IReadOnlyList<HubCore.ToolCommandInfo> ChatCommands
        => HubCore.BuiltInCommandCatalog.ForSoundboard(_working);

    public Visibility EmptyVisibility => Sounds.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    public string CountText => Sounds.Count == 1
        ? Localizer.T("panel.soundboard.count.one", "1 sound")
        : string.Format(Localizer.T("panel.soundboard.count.many", "{0} sounds"),
                        Sounds.Count.ToString(CultureInfo.InvariantCulture));

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
        }
    }

    // ── Overlay hookup (ONE pair for the whole board) ───────────────────
    public string LayerId
    {
        get => _working.LayerId ?? "";
        set
        {
            var v = value ?? "";
            if ((_working.LayerId ?? "") == v) return;
            _working.LayerId = v;
            Raise(nameof(LayerId));
            RaiseOverlayState();
            // A new layer means a different trigger set; drop the stale list so the next
            // drop-down open cannot offer a trigger that layer does not have.
            TriggerNames.Clear();
            ScheduleSave();
        }
    }

    public string TriggerName
    {
        get => _working.TriggerName ?? "";
        set
        {
            var v = value ?? "";
            if ((_working.TriggerName ?? "") == v) return;
            _working.TriggerName = v;
            Raise(nameof(TriggerName));
            RaiseOverlayState();
            ScheduleSave();
        }
    }

    /// <summary>Shown while the board has no output. Without it, every row would look
    /// correctly configured and nothing would ever play.</summary>
    public Visibility OverlayMissingVisibility
        => (_working.LayerId ?? "").Trim().Length == 0 || (_working.TriggerName ?? "").Trim().Length == 0
            ? Visibility.Visible : Visibility.Collapsed;

    private void RaiseOverlayState()
    {
        Raise(nameof(OverlayMissingVisibility));
    }

    // ── Header-band projection (the pill's predicate, rendered in the band) ──
    // The 48px ToolPageHeader replaced the action strip, the pinned state band and
    // the status pill, so the page's one state line is now this pair. It switches
    // over the SAME SoundboardService.PillState — the state is the SERVICE's,
    // answered from the live config and the layer registry with the exact
    // blank-layer/blank-trigger test TryPrepareFire uses to refuse a fire, and
    // deliberately not recomputed from the working copy (a state that described the
    // panel's unsaved draft would say the board can play while the board still
    // refuses every row). No new predicate, no service-side change:
    // PreBuildStatusPillTests still pins ComputePillState.
    //
    // House grammar for the phrase: lowercase, three words or fewer, state AND
    // consequence.
    //   CannotPlay keeps the pill's RED. The tier reads "enabled but unable to
    //   work", which is exactly this state: the words still answer in chat and are
    //   consumed, and not one of them makes a sound. It is the failure this whole
    //   page exists to make visible.
    //   OverlayNotRendering reads grey, not red — the board is configured and the
    //   fix is in OBS, not on this page. The old pill's amber has no tier here, and
    //   grey at least keeps it distinct from the red above.
    private SoundboardSvc.SoundboardPillState _pillState = SoundboardSvc.SoundboardPillState.Dormant;

    /// <summary>The header band's state phrase.</summary>
    public string HeaderStateText => _pillState switch
    {
        SoundboardSvc.SoundboardPillState.Dormant             => Localizer.T("panel.soundboard.state.off", "off"),
        SoundboardSvc.SoundboardPillState.CannotPlay          => Localizer.T("panel.soundboard.state.cannot_play", "on · nothing plays"),
        SoundboardSvc.SoundboardPillState.OverlayNotRendering => Localizer.T("panel.soundboard.state.overlay_closed", "ready · overlay closed"),
        _                                                     => Localizer.T("panel.soundboard.state.ready", "live · sounds armed"),
    };

    /// <summary>The foreground tier for <see cref="HeaderStateText"/>.</summary>
    public ToolStateKind HeaderStateKind => _pillState switch
    {
        SoundboardSvc.SoundboardPillState.CannotPlay => ToolStateKind.Error,
        SoundboardSvc.SoundboardPillState.Ready      => ToolStateKind.Live,
        _                                            => ToolStateKind.Dormant,
    };

    /// <summary>Re-reads the service's pill state. Called on load, after every save
    /// lands, on a foreign config change, on the page refresh, and when the overlay
    /// layer attaches or detaches.</summary>
    private void RefreshPill()
    {
        SoundboardSvc.SoundboardPillState state;
        try { state = _svc.PillState; }
        catch (Exception ex)
        {
            GlobalLogger.Error("SoundboardViewModel", "pill state read failed", ex);
            return;
        }

        // Raised only on a real transition — every phrase here is constant, so an
        // unchanged state has nothing new to say and an idle tab raises nothing.
        if (_pillState != state)
        {
            _pillState = state;
            Raise(nameof(HeaderStateText));
            Raise(nameof(HeaderStateKind));
        }
    }

    /// <summary>Layer-registry subscriber: an OBS browser source attaching to (or
    /// dropping off) THIS board's layer is the difference between "ready" and "ready ·
    /// overlay not rendering". Raised from the socket's thread.</summary>
    private void OnLiveLayerChanged(string layerId, bool isActive)
    {
        _ui.Post(() =>
        {
            if (_disposed) return;
            if (!string.Equals((layerId ?? "").Trim(), (_working.LayerId ?? "").Trim(),
                               StringComparison.OrdinalIgnoreCase))
                return;
            RefreshPill();
        });
    }

    // ── Load ────────────────────────────────────────────────────────────
    public Task LoadAsync()
    {
        if (_loaded) return Task.CompletedTask;
        _loaded = true;
        // OnLoaded runs on the UI thread — first lazy fill of both pickers.
        RefreshLayers();
        RefreshClips();
        RaiseListCounters();
        RefreshPill();
        return Task.CompletedTask;
    }

    private void BuildRows()
    {
        foreach (var row in Sounds) row.PropertyChanged -= OnRowPropertyChanged;
        Sounds.Clear();
        _working.Sounds ??= new List<SoundDef>();
        foreach (var s in _working.Sounds)
            Sounds.Add(HookRow(NewRow(s)));
        RaiseListCounters();
        RaiseAllShadowWarnings();
    }

    private SoundRowVm NewRow(SoundDef sound)
        => new(sound, ClipOptions, ClipInLibrary, ReservedVerbOwner, BoardConflictOwner, ScheduleSave);

    // ── Add / remove sounds ─────────────────────────────────────────────
    public void AddSound()
    {
        var sound = new SoundDef
        {
            Command = NextCommand(),
            ClipPath = "",
            Roles = SoundboardRoles.All(),
            Enabled = true,
        };
        _working.Sounds ??= new List<SoundDef>();
        _working.Sounds.Add(sound);
        Sounds.Add(HookRow(NewRow(sound)));
        RaiseListCounters();
        // A new row can only ever shadow, never be shadowed by, the rows already there —
        // but the reverse is true the moment its word is edited, so keep the whole board's
        // answers in step from the start.
        RaiseAllShadowWarnings();
        ScheduleSave();
    }

    public void RemoveSound(SoundRowVm row)
    {
        if (row is null) return;
        row.PropertyChanged -= OnRowPropertyChanged;
        _working.Sounds?.Remove(row.Sound);
        Sounds.Remove(row);
        RaiseListCounters();
        // Deleting the row that WAS winning a duplicated word hands the word to whoever is
        // left, so the survivor's warning has to be re-asked.
        RaiseAllShadowWarnings();
        ScheduleSave();
    }

    private SoundRowVm HookRow(SoundRowVm row)
    {
        // Row edits feed the shadow-warning re-ask below (unhooked on remove /
        // rebuild / dispose).
        row.PropertyChanged += OnRowPropertyChanged;
        return row;
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A word, an alias or an enable toggle changes which row WINS a duplicated word,
        // and that answer lives on the other rows as much as on this one. Re-ask the whole
        // board. (Safe against recursion: the raise only touches the two shadow-warning
        // properties, which this handler ignores.)
        if (e.PropertyName == nameof(SoundRowVm.Command) ||
            e.PropertyName == nameof(SoundRowVm.AliasesText) ||
            e.PropertyName == nameof(SoundRowVm.Enabled))
            RaiseAllShadowWarnings();
    }

    private void RaiseAllShadowWarnings()
    {
        foreach (var row in Sounds) row.RaiseShadowWarning();
    }

    private void RaiseListCounters()
    {
        Raise(nameof(EmptyVisibility));
        Raise(nameof(CountText));
        // The command block rides this raise rather than owning a second bookkeeping site:
        // every path that can change a word, an alias, a role tick or an enable already
        // funnels through here (ScheduleSave / SaveNow from a row edit, AddSound,
        // RemoveSound, BuildRows on a foreign reload, LoadAsync on first paint).
        Raise(nameof(ChatCommands));
    }

    private string NextCommand()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _working.Sounds ?? new List<SoundDef>())
            if (!string.IsNullOrWhiteSpace(s.Command)) taken.Add(s.Command.Trim());
        for (int i = 1; ; i++)
        {
            string candidate = "sound" + i.ToString(CultureInfo.InvariantCulture);
            // Skip a name an earlier built-in already owns: a freshly-added row that
            // arrives pre-shadowed would show its warning before the streamer has typed
            // anything, which reads as a bug in the tool rather than advice.
            if (!taken.Contains(candidate) && ReservedVerbOwner(candidate) is null) return candidate;
        }
    }

    // ── Layer / trigger / clip enumeration (guarded) ────────────────────
    /// <summary>Re-reads the registered layer ids. No-ops when unchanged so an open
    /// drop-down isn't churned; keeps the last-known-good list when the registry is
    /// unreachable. UI thread only. Mirrors AlertsViewModel.RefreshLayers.</summary>
    public void RefreshLayers()
    {
        var ids = new List<string>();
        try
        {
            foreach (var id in LayerRt.Instance.Registry.GetRegisteredLayerIds())
                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
        }
        catch (Exception ex)
        {
            // LayerRuntime may not be initialized (design contexts / early boot) — keep
            // whatever list we last resolved.
            GlobalLogger.Error("SoundboardViewModel", "layer enumeration failed", ex);
            return;
        }
        ids.Sort(StringComparer.OrdinalIgnoreCase);
        if (SameSequence(ids, LayerIds)) return;
        LayerIds.Clear();
        foreach (var id in ids) LayerIds.Add(id);
    }

    /// <summary>Deduped union of the trigger names across every widget of the selected
    /// layer ("onStartup" / "onTrigger:&lt;x&gt;" / "trigger_&lt;8hex&gt;"). Empty on a
    /// blank/unknown layer or when the registry is unreachable. UI thread only.</summary>
    public void RefreshTriggerNames()
    {
        var names = new List<string>();
        string layerId = (_working.LayerId ?? "").Trim();
        if (layerId.Length > 0)
        {
            try
            {
                var layer = LayerRt.Instance.Registry.GetLayer(layerId);
                if (layer?.Widgets is not null)
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var widget in layer.Widgets)
                    {
                        if (widget?.Triggers is null) continue;
                        foreach (var trigger in widget.Triggers)
                        {
                            string name = trigger?.Name ?? "";
                            if (name.Length == 0) continue;
                            if (seen.Add(name)) names.Add(name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("SoundboardViewModel", "trigger enumeration failed", ex);
                return;
            }
        }
        if (SameSequence(names, TriggerNames)) return;
        TriggerNames.Clear();
        foreach (var n in names) TriggerNames.Add(n);
    }

    /// <summary>Re-reads the media library's audio files. UI thread only.</summary>
    public void RefreshClips()
    {
        IReadOnlyList<string> clips;
        try { clips = SoundboardSvc.EnumerateClipCandidates(); }
        catch (Exception ex)
        {
            // EnumerateClipCandidates already logs and degrades, so reaching here means
            // something outside it failed — keep the last-known-good list either way, and
            // leave _clipsScanned alone so the rows keep their "cannot tell" silence
            // rather than accusing every clip of being missing.
            GlobalLogger.Error("SoundboardViewModel", "clip enumeration failed", ex);
            return;
        }
        bool firstScan = !_clipsScanned;
        _clipsScanned = true;
        if (SameSequence(clips, ClipOptions))
        {
            // The list is unchanged, but the FIRST scan still flips every row from "cannot
            // tell" to a real answer — including the empty-library case, where the list was
            // empty before the scan and is empty after it.
            if (firstScan) RaiseAllClipWarnings();
            return;
        }
        ClipOptions.Clear();
        foreach (var c in clips) ClipOptions.Add(c);
        RaiseAllClipWarnings();
    }

    private void RaiseAllClipWarnings()
    {
        foreach (var row in Sounds) row.RaiseClipWarning();
    }

    /// <summary>
    /// Whether <paramref name="clip"/> is a file the overlay will actually find: true /
    /// false / null for "not scanned yet". Answered from <see cref="ClipOptions"/> — the
    /// picker's own list, enumerated from the media root the overlay serves and filtered to
    /// the extensions it treats as audio — so the row's warning and the clip drop-down can
    /// never disagree, and asking costs no disk access on the UI thread.
    /// </summary>
    private bool? ClipInLibrary(string clip)
    {
        if (!_clipsScanned) return null;
        string c = (clip ?? "").Trim();
        if (c.Length == 0) return null;   // blank is the shape warning's business
        foreach (var candidate in ClipOptions)
            if (string.Equals(candidate, c, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// The command word of the row on THIS board that already answers
    /// <paramref name="word"/> when it is not <paramref name="row"/> itself; null when the
    /// word is this row's to keep.
    ///
    /// <para>The winner is resolved by exactly the rule <c>SoundboardService.Find</c> uses —
    /// first ENABLED claimant, falling back to the first claimant at all — so the warning
    /// names the row that really plays rather than merely the one listed first. Get that
    /// wrong and the advice sends the streamer to rename the row that was working.</para>
    /// </summary>
    private string? BoardConflictOwner(SoundRowVm row, string word)
    {
        SoundRowVm? firstEnabled = null, firstAny = null;
        foreach (var other in Sounds)
        {
            if (!other.ClaimsWord(word)) continue;
            firstAny ??= other;
            if (other.Enabled) { firstEnabled = other; break; }
        }
        var winner = firstEnabled ?? firstAny;
        if (winner is null || ReferenceEquals(winner, row)) return null;
        // Canonical, not trimmed: the caller prints this with a bang in front of it, and a
        // winning row saved as "!airhorn" would otherwise be named "!!airhorn". It also
        // makes the empty test right — a command of "!" alone is a word no parser can match,
        // so it is nobody's owner.
        string owner = ChatVerb.Canonical(winner.Command);
        return owner.Length == 0 ? null : owner;
    }

    private static bool SameSequence(IReadOnlyList<string> fresh, ObservableCollection<string> current)
    {
        if (fresh.Count != current.Count) return false;
        for (int i = 0; i < fresh.Count; i++)
            if (!string.Equals(fresh[i], current[i], StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>Page-level refresh button: re-reads both overlay lists and the clip
    /// library, and re-evaluates every row's shadow warning (another tool's command may
    /// have been renamed in a different tab since this page opened). RefreshClips raises the
    /// clip warnings itself, since it is the only thing that can change that answer.</summary>
    public void RefreshEverything()
    {
        RefreshLayers();
        RefreshTriggerNames();
        RefreshClips();
        RaiseAllShadowWarnings();
        // The layer may have gone live (or dark) while the tab sat idle and, on a Hub
        // that came up before the registry did, this is the first chance the pill has to
        // read a real answer.
        RefreshPill();
    }

    // ── Chat-verb shadowing ─────────────────────────────────────────────
    /// <summary>
    /// The display name of the built-in tool that already answers <paramref name="word"/>,
    /// or null when the word is free.
    ///
    /// <para>Only the providers that run BEFORE Soundboard are consulted, because only
    /// those can shadow it: the dispatch is first-handled-wins, in the fixed order
    /// Automod → UserManagement → Loyalty → UserQueue → Counters → Quotes → SongRequest →
    /// Polls → Ranks → <b>Soundboard</b> → CustomCommands. Custom Commands sits AFTER and
    /// is therefore the one that loses, which is the intended direction — a built-in must
    /// never be shadowed by a streamer-authored trigger — so it is deliberately not
    /// listed here.</para>
    ///
    /// <para>Still read from the LIVE service configs rather than a table of defaults, so
    /// renaming another tool's command changes this answer instead of quietly making it
    /// wrong; and enabled state is still NOT consulted: a tool that is off today shadows
    /// nothing today, but the streamer is choosing a word they will keep, and a warning
    /// that appears the moment an unrelated tool is switched on is worse than one that is
    /// there from the start. Both rules now live in
    /// <see cref="Phoenix.Controls.Hub.Core.BuiltInCommandOrder"/> — passing this page's
    /// own slot is the whole of the prefix rule, and this method is the page's name for
    /// it.</para>
    ///
    /// <para>★ What re-pointing FIXED, beyond the duplication. The table this replaced
    /// spelled Automod's permit verb and Loyalty's two duel replies as hard-coded literals
    /// (they are configurable now, so a renamed one went unreported), and it compared with
    /// a local helper that only trimmed — so a Loyalty trigger saved as "!points" matched
    /// in chat, where every provider goes through <c>ChatVerb</c>, while being invisible
    /// here. The catalogue's rows are canonical and so is the word on the way in.</para>
    /// </summary>
    public string? ReservedVerbOwner(string word)
        => Phoenix.Controls.Hub.Core.BuiltInCommandOrder.OwnerAhead(
               Phoenix.Controls.Hub.Core.BuiltInChatSlot.Soundboard, word);

    // ── Persistence ─────────────────────────────────────────────────────
    private void ScheduleSave()
    {
        _dirty = true;
        RaiseListCounters();
        if (_saveTimer is not null) { _saveTimer.Stop(); _saveTimer.Start(); }
        else _ = SaveWorkingAsync();
    }

    private void SaveNow()
    {
        _dirty = true;
        RaiseListCounters();
        _saveTimer?.Stop();
        _ = SaveWorkingAsync();
    }

    private async Task SaveWorkingAsync()
    {
        if (!_dirty) return;
        _dirty = false;
        try { await _svc.SaveConfigAsync(_working).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("SoundboardViewModel", "SaveConfigAsync failed", ex); }
        // The pill reads the SERVICE's config, which only advances once the save lands —
        // and our own save is skipped by the ConfigChanged echo guard below, so this is
        // the one place a master flip or a hookup edit can reach the pill.
        _ui.Post(() => { if (!_disposed) RefreshPill(); });
    }

    // ── Service events ──────────────────────────────────────────────────
    private void OnConfigChanged(object? sender, EventArgs e)
    {
        // Never rebuild while the user has unsaved local edits in flight: a self-save's
        // event can arrive AFTER the user typed more (so _working has advanced past the
        // saved snapshot), and rebuilding from _svc.Config would silently drop that edit.
        if (_dirty) return;
        // SaveConfigAsync deep-clones our working copy, so reference-equality can't detect
        // a self-save. Compare serialized content instead: a self-save is content-identical
        // → skip the rebuild; a foreign change differs.
        if (SameContent(_svc.Config, _working)) return;
        _ui.Post(() =>
        {
            // Re-check on the UI thread (same thread as the edit handlers) — an edit may
            // have landed between the background-thread check above and this dispatch.
            if (_dirty) return;
            _working = Clone(_svc.Config);
            BuildRows();
            Raise(nameof(MasterEnabled));
            Raise(nameof(LayerId));
            Raise(nameof(TriggerName));
            RaiseOverlayState();
            RefreshPill();
        });
    }

    private static bool SameContent(SoundboardConfig a, SoundboardConfig b)
    {
        try { return JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b); }
        catch { return false; }
    }

    private static SoundboardConfig Clone(SoundboardConfig src)
    {
        try
        {
            string json = JsonSerializer.Serialize(src ?? new SoundboardConfig());
            return JsonSerializer.Deserialize<SoundboardConfig>(json) ?? new SoundboardConfig();
        }
        catch { return new SoundboardConfig(); }
    }
}
