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
using Windows.UI;
using AlertsSvc = Phoenix.Controls.Hub.Core.AlertsService;
using LayerRt = Phoenix.Controls.Hub.Core.LayerRuntime;
using SbClient = Phoenix.Controls.Hub.Core.WS;

namespace Phoenix.Controls.Hub.WinUI.Panels.AlertsPanel;

/// <summary>
/// ViewModel for the Hub Alerts page — StreamElements-style event responses:
/// TEN fixed event families (Follow / Sub / Gift Sub / Gift Bomb / Bits / Raid /
/// Charity / Shoutout Received / Watch Streak / Tip), each with size tiers that
/// post a templated chat line and can fire an overlay trigger. Like the
/// Scheduling / Timer VMs it reaches <see cref="AlertsSvc.Instance"/> DIRECTLY
/// for reads AND writes and subscribes to its ConfigChanged / RuntimeChanged
/// events.
///
/// Since the master-detail rebuild the page is a 1.4* detail column driven by a
/// 0.9* family roster: <see cref="SelectedEvent"/> is the one open family and
/// <see cref="DetailVisibility"/> / <see cref="EmptyVisibility"/> switch the
/// detail pane against its empty state. <see cref="HeaderStateText"/> /
/// <see cref="HeaderStateKind"/> project AlertsService.PillState into the 48 px
/// ToolPageHeader band (the <see cref="StatusPillText"/> / <see cref="StatusPillColor"/>
/// pair beside them is the SAME projection for ToolStatusPill, which the page no
/// longer renders but which stays for watch surfaces); the Streamer.bot
/// connection event subscription is detached in <see cref="Dispose"/>.
///
/// Config editing model (clones SchedulingViewModel): the whole
/// <see cref="AlertsConfig"/> is deep-cloned into the six card VMs on
/// construction. Every edit mutates a working def and schedules a debounced
/// save (400 ms) — toggles (master / family enable / raid auto-shoutout) save
/// immediately; the save REBUILDS a fresh detached AlertsConfig from the live
/// cards and hands it to <c>UpdateConfigAsync</c> (which deep-owns it, persists,
/// and raises ConfigChanged). A self-triggered ConfigChanged is detected by
/// reference-equality against the last pushed instance and skipped; a foreign
/// ConfigChanged rebuilds the cards wholesale (expand state resets, same
/// tradeoff Scheduling accepts for its rows).
///
/// Layer / trigger pickers: <see cref="LayerIds"/> is the single shared item
/// source every tier row's layer ComboBox binds; it is populated LAZILY (first
/// LoadAsync, drop-down open, the per-row ↻ button) — never in the ctor — and
/// every LayerRuntime registry touch is wrapped in try/catch because the
/// runtime may not be initialized in design contexts.
/// </summary>
public sealed class AlertsViewModel : ObservableObject, IDisposable
{
    private readonly AlertsSvc _svc = AlertsSvc.Instance;
    private readonly UiDispatcherPump _ui;
    private readonly DispatcherQueueTimer? _saveTimer;

    private bool _masterEnabled;
    private AlertsConfig? _lastPushed;   // reference-equality self-trigger guard
    private bool _disposed;
    private bool _dirty;
    private bool _loaded;
    private AlertEventVm? _selected;

    public AlertsViewModel(DispatcherQueue? dispatcher)
    {
        _ui = new UiDispatcherPump(dispatcher);
        var cfg = Clone(_svc.Config);
        _masterEnabled = cfg.Enabled;

        if (dispatcher is not null)
        {
            _saveTimer = dispatcher.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => _ = SaveWorkingAsync();
        }

        BuildCards(cfg);

        _svc.ConfigChanged += OnConfigChanged;
        _svc.RuntimeChanged += OnRuntimeChanged;
        // The pill's "degraded · chat lines and shoutouts down" state is a live
        // Streamer.bot fact, not a config fact, so it needs the connection event.
        // Without it the page would only notice a dropped socket on the next save
        // or the next fired alert.
        SbClient.Instance.OnConnectionStatusChanged += OnSbConnectionChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _svc.ConfigChanged -= OnConfigChanged;
        _svc.RuntimeChanged -= OnRuntimeChanged;
        SbClient.Instance.OnConnectionStatusChanged -= OnSbConnectionChanged;
        _saveTimer?.Stop();
        // Flush any pending edit so the last keystroke isn't lost on close.
        if (_dirty)
        {
            _dirty = false;
            var cfg = BuildConfig();
            _lastPushed = cfg;
            // Parked, not dropped: at shutdown MainWindow's coordinator ends in
            // Environment.Exit(0), which killed this write mid-flight. The tracker
            // lets PreBuildsHostView.DisposeAllTools hand it to the coordinator as a
            // tracked step; mid-session tab closes behave exactly as before.
            Phoenix.Controls.Hub.WinUI.Controls.ToolConfigFlushTracker.Register(
                Phoenix.Controls.Hub.Core.AsyncErrorBoundary.SafeRunAsync(
                    () => _svc.UpdateConfigAsync(cfg), "AlertsViewModel", "final config flush"));
        }
    }

    // ── Bound collections ────────────────────────────────────────────────
    /// <summary>The TEN event families, fixed order — rebuilt wholesale on a
    /// foreign config reload, never reordered. (The class doc said "six" long
    /// after the code added Charity / Shoutout Received / Watch Streak / Tip.)</summary>
    public ObservableCollection<AlertEventVm> Events { get; } = new();

    /// <summary>Registered overlay-layer ids — the SHARED item source behind
    /// every tier row's layer picker (same instance handed to each row).</summary>
    public ObservableCollection<string> LayerIds { get; } = new();

    // ── Selection (the 0.9* family roster drives the 1.4* detail column) ──
    // The page's ten families used to be ten collapsible cards in one column;
    // the roster row's click is now the selection, because ItemsRepeater has no
    // selection model of its own.
    public AlertEventVm? SelectedEvent
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            if (_selected is not null) _selected.IsSelected = false;
            _selected = value;
            if (_selected is not null) _selected.IsSelected = true;
            Raise(nameof(SelectedEvent));
            Raise(nameof(HasSelection));
            Raise(nameof(DetailVisibility));
            Raise(nameof(EmptyVisibility));
        }
    }

    public bool HasSelection => _selected is not null;
    public Visibility DetailVisibility => HasSelection ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVisibility  => HasSelection ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Roster header count — "3 of 10 on". Cards default OFF, so a fresh
    /// install honestly reads "0 of 10 on".</summary>
    public string RosterCountText
    {
        get
        {
            int on = 0;
            foreach (var card in Events) if (card.Enabled) on++;
            return string.Format(
                CultureInfo.InvariantCulture,
                Localizer.T("panel.alerts.roster.count", "{0} of {1} on"),
                on.ToString(CultureInfo.InvariantCulture),
                Events.Count.ToString(CultureInfo.InvariantCulture));
        }
    }

    // ── Master switch (immediate save, like SchedulingViewModel.MasterEnabled) ─
    public bool MasterEnabled
    {
        get => _masterEnabled;
        set
        {
            if (_masterEnabled == value) return;
            _masterEnabled = value;
            Raise(nameof(MasterEnabled));
            // SaveNow, never ScheduleSave: the master flip bypasses the 400 ms
            // debounce on every one of the twelve tools.
            SaveNow();
        }
    }

    // UI-thread only — callers are card/tier edits (ScheduleSave / SaveNow),
    // BuildCards, and the _ui.Post continuations of the service event handlers.
    private void RaiseStats()
    {
        Raise(nameof(RosterCountText));
    }

    // ── Status pill (strip column 1) ────────────────────────────────────
    // The state machine itself lives in AlertsService.PillState so it can be
    // unit-tested without a ViewModel; this VM only maps it to text and colour.
    //
    // COLOURS are the PhoenixDark palette values named in the build spec. They
    // are literals rather than resource lookups because ToolStatusPill takes a
    // Windows.UI.Color and the theme exposes brushes.
    private static readonly Color SecondaryColor = Color.FromArgb(0xFF, 0x9C, 0x8A, 0x72);
    private static readonly Color WarnColor      = Color.FromArgb(0xFF, 0xE0, 0xA2, 0x3A);
    private static readonly Color OkColor        = Color.FromArgb(0xFF, 0x6F, 0xA4, 0x6B);

    // ★ The Degraded caption names the seams the PREDICATE actually gates. That
    // predicate is only "the Streamer.bot socket is down" (AlertsService
    // .ComputePillState), and the two effects that ride that socket are the chat
    // line — ScriptManager.Alerts.cs wires SendChat to the Twitch/YouTube/Kick
    // send cores, all of which end in DispatchNamedAction, which drops the send
    // outright when WS.Instance.IsConnected is false — and the raid auto-shoutout,
    // which is a DispatchNamedAction call itself. It says "chat lines", plural and
    // unqualified, because the loss is EVERY family's line, not one card's.
    // It deliberately does NOT name tips: Tip is an INBOUND event family here
    // (Streamlabs.Donation / StreamElements.Tip / Ko-fi / … in TryMapEvent), not an
    // outbound effect, so a dead socket does not single it out — with the socket
    // down no family's events arrive at all. The old wording ("shoutouts and tips
    // down") read as a two-item casualty list that let a streamer conclude their
    // follow / sub / bits alerts were still posting.
    public string StatusPillText => _svc.PillState switch
    {
        AlertsSvc.AlertsPillState.Dormant      => Localizer.T("panel.alerts.pill.dormant", "dormant"),
        AlertsSvc.AlertsPillState.NoCardsArmed => Localizer.T("panel.alerts.pill.no_cards", "live · no cards armed"),
        AlertsSvc.AlertsPillState.Degraded     => Localizer.T("panel.alerts.pill.degraded", "degraded · chat lines and shoutouts down"),
        _                                      => Localizer.T("panel.alerts.pill.armed", "armed"),
    };

    public Color StatusPillColor => _svc.PillState switch
    {
        AlertsSvc.AlertsPillState.Dormant      => SecondaryColor,
        AlertsSvc.AlertsPillState.NoCardsArmed => WarnColor,
        AlertsSvc.AlertsPillState.Degraded     => WarnColor,
        _                                      => OkColor,
    };

    /// <summary>Only the fully armed state pulses — the beat claims "this tool is
    /// live and reachable", which is exactly what the other three states deny.</summary>
    public bool StatusPulsing => _svc.PillState == AlertsSvc.AlertsPillState.Armed;

    // ── Header band projection (the same predicate, a second render site) ─
    // The 48 px ToolPageHeader replaced the action strip / state band / status
    // pill, so the page's one state line is now this pair rather than
    // StatusPillText + StatusPillColor. It is the SAME switch over the SAME
    // AlertsService.PillState — no new predicate, no service-side change, and
    // the pill members above stay because ToolStatusPill is re-scoped to watch
    // surfaces, not deleted.
    //
    // House grammar for the phrase: lowercase, three words or fewer, and it
    // carries state AND consequence.
    //   NoCardsArmed reads grey, not red. The tool is on and nothing will fire —
    //   true, and worth saying — but it is an unfinished CONFIG, not a fault, and
    //   it is the state every fresh install starts in. Red is reserved for
    //   Degraded, which is the one state the streamer cannot fix on this page.
    public string HeaderStateText => _svc.PillState switch
    {
        AlertsSvc.AlertsPillState.Dormant      => Localizer.T("panel.alerts.state.off", "off"),
        AlertsSvc.AlertsPillState.NoCardsArmed => Localizer.T("panel.alerts.state.none_armed", "on · none armed"),
        AlertsSvc.AlertsPillState.Degraded     => Localizer.T("panel.alerts.state.degraded", "error · bot offline"),
        _                                      => Localizer.T("panel.alerts.state.live", "live · answering events"),
    };

    public ToolStateKind HeaderStateKind => _svc.PillState switch
    {
        AlertsSvc.AlertsPillState.Dormant      => ToolStateKind.Dormant,
        AlertsSvc.AlertsPillState.NoCardsArmed => ToolStateKind.Dormant,
        AlertsSvc.AlertsPillState.Degraded     => ToolStateKind.Error,
        _                                      => ToolStateKind.Live,
    };

    private void RaisePillState()
    {
        Raise(nameof(StatusPillText));
        Raise(nameof(StatusPillColor));
        Raise(nameof(StatusPulsing));
        Raise(nameof(HeaderStateText));
        Raise(nameof(HeaderStateKind));
    }

    // ── Load ─────────────────────────────────────────────────────────────
    public Task LoadAsync()
    {
        if (_loaded) return Task.CompletedTask;
        _loaded = true;
        RefreshLayers();   // OnLoaded runs on the UI thread — first lazy picker fill
        RaiseStats();
        RaisePillState();
        return Task.CompletedTask;
    }

    // ── Card construction (fixed six-family order) ───────────────────────
    private void BuildCards(AlertsConfig cfg)
    {
        cfg.Follow   ??= new AlertEventConfig();
        cfg.Sub      ??= new AlertEventConfig();
        cfg.GiftSub  ??= new AlertEventConfig();
        cfg.GiftBomb ??= new AlertEventConfig();
        cfg.Cheer    ??= new AlertEventConfig();
        cfg.Raid     ??= new AlertEventConfig();
        cfg.Charity          ??= new AlertEventConfig();
        cfg.ShoutoutReceived ??= new AlertEventConfig();
        cfg.WatchStreak      ??= new AlertEventConfig();
        cfg.Tip              ??= new AlertEventConfig();

        // A foreign ConfigChanged replaces every card instance, so the selection
        // has to be re-established by FAMILY KEY afterwards — otherwise an edit
        // made in a second window would dump the streamer back to the page's
        // empty state mid-edit. FamilyKey is the only stable identity here (the
        // ten families are fixed and never reordered).
        string? keep = _selected?.FamilyKey;

        SelectedEvent = null;
        Events.Clear();
        // ARGUMENT 1 IS NOT A LABEL. It is the FamilyKey — the identity BuildConfig
        // switches on and the config persists, and the key the selection is restored
        // by above. It stays English in every language. Only argument 2 (the roster
        // title) and argument 4 (the {amount} hint) are read by a human, so only
        // those two go through Localizer; argument 3 is a Segoe MDL2 glyph.
        Events.Add(new AlertEventVm(
            "Follow", Localizer.T("panel.alerts.family.follow.title", "FOLLOW"), "",
            Localizer.T("panel.alerts.family.follow.hint", "Every follow is size 0 — one tier at From size 0 catches them all"),
            showAutoShoutout: false, cfg.Follow, LayerIds, GetTriggersFor, ScheduleSave, SaveNow));
        Events.Add(new AlertEventVm(
            "Sub", Localizer.T("panel.alerts.family.sub.title", "SUBSCRIPTION / RESUB"), "",
            Localizer.T("panel.alerts.family.sub.hint", "{amount} = months subscribed · a brand-new sub reads 0 or 1"),
            showAutoShoutout: false, cfg.Sub, LayerIds, GetTriggersFor, ScheduleSave, SaveNow));
        Events.Add(new AlertEventVm(
            "GiftSub", Localizer.T("panel.alerts.family.gift_sub.title", "GIFT SUB"), "",
            Localizer.T("panel.alerts.family.gift_sub.hint", "{amount} = 1 · use {gifter} / {recipient}, not {user} — {user} varies by platform"),
            showAutoShoutout: false, cfg.GiftSub, LayerIds, GetTriggersFor, ScheduleSave, SaveNow));
        Events.Add(new AlertEventVm(
            "GiftBomb", Localizer.T("panel.alerts.family.gift_bomb.title", "GIFT BOMB"), "",
            Localizer.T("panel.alerts.family.gift_bomb.hint", "{amount} = subs gifted at once · use {gifter}, not {user} · with this card on, GIFT SUB is muted for 10s after a bomb"),
            showAutoShoutout: false, cfg.GiftBomb, LayerIds, GetTriggersFor, ScheduleSave, SaveNow));
        Events.Add(new AlertEventVm(
            "Cheer", Localizer.T("panel.alerts.family.cheer.title", "BITS & CHEERS"), "",
            Localizer.T("panel.alerts.family.cheer.hint", "{amount} = bits cheered (Kick Kicks / YouTube Super Chat elsewhere)"),
            showAutoShoutout: false, cfg.Cheer, LayerIds, GetTriggersFor, ScheduleSave, SaveNow));
        Events.Add(new AlertEventVm(
            "Raid", Localizer.T("panel.alerts.family.raid.title", "RAID"), "",
            Localizer.T("panel.alerts.family.raid.hint", "{amount} = raid viewer count"),
            showAutoShoutout: true, cfg.Raid, LayerIds, GetTriggersFor, ScheduleSave, SaveNow));
        Events.Add(new AlertEventVm(
            "Charity", Localizer.T("panel.alerts.family.charity.title", "CHARITY DONATION"), "",
            Localizer.T("panel.alerts.family.charity.hint", "{amount} = the donation with cents · {currency} = the code · thresholds stay whole, so From size 5 catches 5.00 up"),
            showAutoShoutout: false, cfg.Charity, LayerIds, GetTriggersFor, ScheduleSave, SaveNow));
        Events.Add(new AlertEventVm(
            "ShoutoutReceived", Localizer.T("panel.alerts.family.shoutout_received.title", "SHOUTOUT RECEIVED"), "",
            Localizer.T("panel.alerts.family.shoutout_received.hint", "Fires when another channel shouts yours out · {amount} = their viewer count"),
            showAutoShoutout: false, cfg.ShoutoutReceived, LayerIds, GetTriggersFor, ScheduleSave, SaveNow));
        Events.Add(new AlertEventVm(
            "WatchStreak", Localizer.T("panel.alerts.family.watch_streak.title", "WATCH STREAK"), "",
            Localizer.T("panel.alerts.family.watch_streak.hint", "{amount} = broadcasts watched in a row"),
            showAutoShoutout: false, cfg.WatchStreak, LayerIds, GetTriggersFor, ScheduleSave, SaveNow));
        Events.Add(new AlertEventVm(
            "Tip", Localizer.T("panel.alerts.family.tip.title", "TIP / DONATION"), "",
            Localizer.T("panel.alerts.family.tip.hint", "{source} names the service · {amount} keeps its cents · {currency} carries the code · needs a donation service in Streamer.bot"),
            showAutoShoutout: false, cfg.Tip, LayerIds, GetTriggersFor, ScheduleSave, SaveNow));

        if (!string.IsNullOrEmpty(keep))
        {
            foreach (var card in Events)
            {
                if (string.Equals(card.FamilyKey, keep, StringComparison.Ordinal))
                {
                    SelectedEvent = card;
                    break;
                }
            }
        }

        // Land on the first family when there was nothing to restore — this is
        // also the tab's first build, and a master-detail page that opens on its
        // empty state reads as "nothing configured" to someone who has ten cards.
        if (_selected is null && Events.Count > 0) SelectedEvent = Events[0];

        RaiseStats();
    }

    // ── Layer / trigger enumeration (guarded registry access) ────────────
    /// <summary>Re-reads the registered layer ids into <see cref="LayerIds"/>.
    /// No-ops when unchanged so an open drop-down isn't churned; keeps the
    /// last-known-good list when the registry is unreachable. UI thread only.</summary>
    public void RefreshLayers()
    {
        var ids = new List<string>();
        try
        {
            foreach (var id in LayerRt.Instance.Registry.GetRegisteredLayerIds())
            {
                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
            }
        }
        catch (Exception ex)
        {
            // LayerRuntime may not be initialized (design contexts / early boot) —
            // keep whatever list we last resolved.
            GlobalLogger.Error("AlertsViewModel", "layer enumeration failed", ex);
            return;
        }
        ids.Sort(StringComparer.OrdinalIgnoreCase);

        bool same = ids.Count == LayerIds.Count;
        if (same)
        {
            for (int i = 0; i < ids.Count; i++)
            {
                if (!string.Equals(ids[i], LayerIds[i], StringComparison.Ordinal)) { same = false; break; }
            }
        }
        if (same) return;
        LayerIds.Clear();
        foreach (var id in ids) LayerIds.Add(id);
    }

    /// <summary>Deduped union of the trigger names across every widget of the
    /// given layer ("onStartup" / "onTrigger:&lt;x&gt;" / "trigger_&lt;8hex&gt;").
    /// Empty on a blank/unknown layer or when the registry is unreachable.</summary>
    public IReadOnlyList<string> GetTriggersFor(string layerId)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(layerId)) return result;
        try
        {
            var layer = LayerRt.Instance.Registry.GetLayer(layerId.Trim());
            if (layer?.Widgets is null) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var widget in layer.Widgets)
            {
                if (widget?.Triggers is null) continue;
                foreach (var trigger in widget.Triggers)
                {
                    string name = trigger?.Name ?? "";
                    if (name.Length == 0) continue;
                    if (seen.Add(name)) result.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("AlertsViewModel", "trigger enumeration failed", ex);
        }
        return result;
    }

    // ── Persistence ──────────────────────────────────────────────────────
    // Every debounced edit funnels through here (card/tier _changed) on the UI
    // thread — cheapest single hook to keep the roster count live.
    private void ScheduleSave()
    {
        _dirty = true;
        RaiseStats();
        if (_saveTimer is not null) { _saveTimer.Stop(); _saveTimer.Start(); }
        else _ = SaveWorkingAsync();
    }

    // Toggles (master / family enable / auto-shoutout) save immediately.
    private void SaveNow()
    {
        _dirty = true;
        RaiseStats();
        _saveTimer?.Stop();
        _ = SaveWorkingAsync();
        // AlertsService.UpdateConfigAsync installs the new config BEFORE its first
        // await, so by the time SaveWorkingAsync has returned to us the service's
        // PillState already reflects the flip. Raising here is what makes the strip
        // pill move on the same frame as the chip (and on the same frame as a
        // family enable, which is what decides the "no cards armed" state).
        RaisePillState();
    }

    private async Task SaveWorkingAsync()
    {
        if (!_dirty) return;
        _dirty = false;
        // Build the fresh config on the current (UI) thread BEFORE awaiting so the
        // snapshot is taken from a stable, non-mutating view of the cards.
        var cfg = BuildConfig();
        _lastPushed = cfg;
        try { await _svc.UpdateConfigAsync(cfg).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("AlertsViewModel", "UpdateConfigAsync failed", ex); }
    }

    // The single place the UI's editable state is projected back into a config
    // blob — a FRESH detached AlertsConfig, never sharing row objects.
    private AlertsConfig BuildConfig()
    {
        var cfg = new AlertsConfig { Enabled = _masterEnabled };
        foreach (var card in Events)
        {
            var snap = card.ToSnapshot();
            switch (card.FamilyKey)
            {
                case "Follow":   cfg.Follow   = snap; break;
                case "Sub":      cfg.Sub      = snap; break;
                case "GiftSub":  cfg.GiftSub  = snap; break;
                case "GiftBomb": cfg.GiftBomb = snap; break;
                case "Cheer":    cfg.Cheer    = snap; break;
                case "Raid":     cfg.Raid     = snap; break;
                case "Charity":          cfg.Charity          = snap; break;
                case "ShoutoutReceived": cfg.ShoutoutReceived = snap; break;
                case "WatchStreak":      cfg.WatchStreak      = snap; break;
                case "Tip":              cfg.Tip              = snap; break;
                // A card whose FamilyKey has no arm here would be silently
                // dropped on every save — the streamer's edits would look
                // applied until the page reloaded. Fail loudly instead.
                default:
                    GlobalLogger.Log(
                        $"Alerts card '{card.FamilyKey}' has no save mapping — its settings were NOT persisted. " +
                        "Add a case to AlertsViewModel.BuildConfig.",
                        "AlertsViewModel", LogLevel.CriticalError);
                    break;
            }
        }
        return cfg;
    }

    // ── Service events (SafeEvent — raised on a background thread; MUST marshal) ─
    private void OnConfigChanged(object? sender, EventArgs e)
    {
        // Self-triggered save assigns our pushed instance as the live config → skip.
        if (ReferenceEquals(_svc.Config, _lastPushed)) return;
        _ui.Post(() =>
        {
            if (_disposed) return;
            var cfg = Clone(_svc.Config);
            _masterEnabled = cfg.Enabled;
            BuildCards(cfg);   // raises the roster count too
            Raise(nameof(MasterEnabled));
            RaisePillState();
        });
    }

    private void OnRuntimeChanged(object? sender, EventArgs e)
        => _ui.Post(() =>
        {
            if (_disposed) return;
            RaisePillState();
        });

    // Streamer.bot connect / disconnect. Raised from the socket's own thread, so
    // marshal before touching anything bound.
    private void OnSbConnectionChanged(bool connected)
        => _ui.Post(() => { if (!_disposed) RaisePillState(); });

    // Deep clone through JSON round-trip (mirrors the Scheduling / Timer VMs) so
    // the working copy is fully detached from the live service config.
    private static AlertsConfig Clone(AlertsConfig src)
    {
        try
        {
            string json = JsonSerializer.Serialize(src ?? new AlertsConfig());
            return JsonSerializer.Deserialize<AlertsConfig>(json) ?? new AlertsConfig();
        }
        catch { return new AlertsConfig(); }
    }
}
