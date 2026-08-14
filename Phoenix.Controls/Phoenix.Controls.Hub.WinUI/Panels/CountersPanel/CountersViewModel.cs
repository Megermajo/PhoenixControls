using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Windows.UI;
using Catalog = Phoenix.Controls.Hub.Core.BuiltInCommandCatalog;
using CountersSvc = Phoenix.Controls.Hub.Core.CountersService;
using ToolCommandInfo = Phoenix.Controls.Hub.Core.ToolCommandInfo;

namespace Phoenix.Controls.Hub.WinUI.Panels.CountersPanel;

/// <summary>
/// ViewModel for the Hub Counters page — the button-side front-end onto the same
/// named-counter store the counter.* script nodes and the !&lt;cmd&gt; chat commands
/// drive. Like the Timer / Loyalty VMs it reaches <see cref="CountersSvc.Instance"/>
/// DIRECTLY for reads AND writes and subscribes to its ConfigChanged /
/// CounterChanged events — Hub.WinUI already references the Hub runtime and the
/// service is always-on, so no cross-lane seam is needed.
///
/// Config editing model: the whole <see cref="CountersConfig"/> is deep-cloned into
/// a private working copy on construction. Every field edits that clone in place and
/// schedules a debounced <c>SaveConfigAsync</c> (which replaces + persists the
/// config). SaveConfigAsync deep-clones what we hand it — the live config is NEVER our
/// working instance — so a self-triggered ConfigChanged cannot be recognised by
/// reference-equality; it is suppressed by the <c>_selfSaving</c> flag instead (see
/// <see cref="SaveWorkingAsync"/> / <see cref="OnConfigChanged"/>). A foreign
/// ConfigChanged rebuilds the rows. Live counts are NEVER cached in the config — they
/// come from the OPEN "Counters" table via <c>ListAsync</c> and are pushed into the
/// rows on load / on CounterChanged.
///
/// SHELL (master-detail, since the tool-page rebuild). The 0.9* roster column binds
/// <see cref="Counters"/> and sets <see cref="SelectedCounter"/>; the 1.4* detail
/// column x:Binds through that one row. Three property families exist only for that
/// shell and are easy to confuse, so they are named apart:
///   * <see cref="DetailVisibility"/> / <see cref="EmptyVisibility"/> — is a counter
///     PICKED (the build spec's selection triple).
///   * <see cref="RosterListVisibility"/> / <see cref="RosterEmptyVisibility"/> —
///     does the tool have ANY counters.
///   * <see cref="StatusPillText"/> / <see cref="StatusPillColor"/> — the strip pill,
///     read from <c>CountersService.PillState</c> rather than recomputed here.
/// </summary>
public sealed class CountersViewModel : ObservableObject, IDisposable
{
    private readonly CountersSvc _svc = CountersSvc.Instance;
    private readonly UiDispatcherPump _ui;
    private readonly DispatcherQueueTimer? _saveTimer;

    private CountersConfig _working;
    private bool _disposed;
    private bool _dirty;
    private bool _loaded;
    // Set on the UI thread immediately before our own SaveConfigAsync and consumed on
    // the UI thread when the resulting ConfigChanged echoes back. CountersService clones
    // on save, so _svc.Config is never our _working instance and reference-equality can
    // never detect a self-save — without this flag every ~400 ms own-save ran BuildRows
    // (Counters.Clear + recreate every CounterRowVm), which destroys the row containers
    // mid-edit: focus and the not-yet-committed text in the TwoWay/LostFocus Name,
    // Command and ReplyTemplate boxes are lost, and a counter added while a save was in
    // flight is reverted by the rebuild and then persisted away by the pending save.
    // Mirrors AutomodViewModel._selfSaving.
    private bool _selfSaving;

    // The detail column (1.4*) edits exactly one counter at a time; the roster
    // (0.9*) picks it. Null = nothing picked, which is what the left column's
    // empty state renders.
    private CounterRowVm? _selected;

    public CountersViewModel(DispatcherQueue? dispatcher)
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
        _svc.CounterChanged += OnCounterChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _svc.ConfigChanged -= OnConfigChanged;
        _svc.CounterChanged -= OnCounterChanged;
        _saveTimer?.Stop();
        // Flush any pending edit so the last keystroke isn't lost on close.
        if (_dirty)
        {
            _dirty = false;
            // Parked, not dropped: at shutdown MainWindow's coordinator ends in
            // Environment.Exit(0), which killed this write mid-flight. The tracker
            // lets PreBuildsHostView.DisposeAllTools hand it to the coordinator as a
            // tracked step; mid-session tab closes behave exactly as before.
            Phoenix.Controls.Hub.WinUI.Controls.ToolConfigFlushTracker.Register(
                Phoenix.Controls.Hub.Core.AsyncErrorBoundary.SafeRunAsync(
                    () => _svc.SaveConfigAsync(_working), "CountersViewModel", "final config flush"));
        }
    }

    // ── Bound collection (rebuilt wholesale on external reload) ──────────
    public ObservableCollection<CounterRowVm> Counters { get; } = new();

    // ── Master switch ───────────────────────────────────────────────────
    public bool MasterEnabled
    {
        get => _working.Enabled;
        set
        {
            if (_working.Enabled == value) return;
            _working.Enabled = value;
            Raise(nameof(MasterEnabled));
            // The flip does not route through ScheduleSave, so the command list is
            // re-raised by hand. It carries the master flag into the catalogue (see
            // ChatCommands), and the raise is what keeps whether that flag reaches a
            // rendered row a question for the catalogue rather than a staleness bug
            // here.
            Raise(nameof(ChatCommands));
            // SaveNow, never ScheduleSave: the master flip bypasses the 400 ms
            // debounce on every one of the twelve tools.
            SaveNow();
            // CountersService.SaveConfigAsync installs the new config BEFORE its
            // first await, so by the time SaveNow has returned the service's
            // PillState already reflects the flip. Raising here is what makes the
            // strip pill move on the same frame as the chip.
            RaisePillState();
        }
    }

    // ── Selection (the 0.9* roster drives the 1.4* detail column) ────────
    // NOTE ON NAMES for the lanes copying this shape: DetailVisibility /
    // EmptyVisibility are the SELECTION pair from the build spec — "is a counter
    // picked". The roster's own "are there any counters at all" pair is named
    // Roster* below; the two are genuinely different states (a tool with three
    // counters and nothing picked shows the roster AND the empty detail).
    public CounterRowVm? SelectedCounter
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            if (_selected is not null) _selected.IsSelected = false;
            _selected = value;
            if (_selected is not null) _selected.IsSelected = true;
            Raise(nameof(SelectedCounter));
            Raise(nameof(HasSelection));
            Raise(nameof(DetailVisibility));
            Raise(nameof(EmptyVisibility));
            Raise(nameof(ChatCommands));
        }
    }

    public bool HasSelection => _selected is not null;
    public Visibility DetailVisibility => HasSelection ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVisibility  => HasSelection ? Visibility.Collapsed : Visibility.Visible;

    // ── Chat commands (read-only; the detail column's ToolCommandList) ───

    /// <summary>
    /// The five chat shapes the SELECTED counter answers to — the bare read plus the
    /// four the parser mints by concatenation (<c>!&lt;cmd&gt;+</c>,
    /// <c>!&lt;cmd&gt;-</c>, <c>!set&lt;cmd&gt;</c>, <c>!reset&lt;cmd&gt;</c>) — each
    /// with the role set that actually gates it (read is ViewRoles, the four modify
    /// shapes are ModifyRoles).
    /// </summary>
    /// <remarks>
    /// <para><b>Scoped to the SELECTION, not to the tool</b> — unlike the Quotes and
    /// Custom Commands pages, whose equivalent property covers their whole config.
    /// Two reasons, both specific to this tool. The four synthesized words are minted
    /// from ONE counter's command word, so they belong beside the field that mints
    /// them and have to move as it is retyped. And a tool-wide rendering is five rows
    /// PER counter: a roster of six would drop a thirty-row table into a column that
    /// is otherwise scoped to a single counter, above the count controls.</para>
    ///
    /// <para>The catalogue's question is phrased in whole configs, so the ask is
    /// phrased as one: the working master flag plus exactly the selected counter. The
    /// <c>CounterDef</c> handed over is the LIVE working instance the editor mutates,
    /// never a copy — that is what makes these the verbs the streamer is typing
    /// rather than the ones last saved. Nothing here mutates it.</para>
    ///
    /// <para>Empty with nothing selected, which the detail column never renders (it
    /// is collapsed in that state). The block's own empty-state sentence is therefore
    /// only reached by a counter with neither a command word nor a name — a counter
    /// that provably answers nothing, which is what the sentence says.</para>
    /// </remarks>
    public IReadOnlyList<ToolCommandInfo> ChatCommands
    {
        get
        {
            var def = _selected?.Def;
            if (def is null) return Array.Empty<ToolCommandInfo>();
            return Catalog.ForCounters(new CountersConfig
            {
                Enabled = _working.Enabled,
                Counters = new List<CounterDef>(1) { def },
            });
        }
    }

    // ── Roster (the 0.9* column) ────────────────────────────────────────
    public bool HasCounters => Counters.Count > 0;

    /// <summary>Roster list visibility — collapsed only when no counter exists at all.</summary>
    public Visibility RosterListVisibility => HasCounters ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>"No counters yet" empty state — the inverse of <see cref="RosterListVisibility"/>.</summary>
    public Visibility RosterEmptyVisibility => HasCounters ? Visibility.Collapsed : Visibility.Visible;

    public string RosterCountText
        => Counters.Count == 1
            ? Localizer.T("panel.counters.roster.count.one", "1 counter")
            : string.Format(
                Localizer.T("panel.counters.roster.count.many", "{0} counters"),
                Counters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

    // ── Status pill (strip column 1) ────────────────────────────────────
    // The state itself comes from CountersService.PillState — the service owns
    // the state machine so it can be unit-tested without a ViewModel; this VM
    // only maps it to text and colour.
    //
    // COLOURS are the PhoenixDark palette values named in the build spec:
    // EmberPrimary #E5A24E for the dormant case, Ok #6FA46B for the live one.
    // They are literals rather than resource lookups because ToolStatusPill takes
    // a Windows.UI.Color, not a Brush, and the theme exposes brushes here.
    private static readonly Color EmberPrimaryColor = Color.FromArgb(0xFF, 0xE5, 0xA2, 0x4E);
    private static readonly Color OkColor           = Color.FromArgb(0xFF, 0x6F, 0xA4, 0x6B);

    public string StatusPillText
        => _svc.PillState == CountersSvc.CountersPillState.CommandsAndOverlayLive
            ? Localizer.T("panel.counters.pill.live", "commands + overlay live")
            : Localizer.T("panel.counters.pill.dormant", "dormant · databank still writable");

    public Color StatusPillColor
        => _svc.PillState == CountersSvc.CountersPillState.CommandsAndOverlayLive
            ? OkColor
            : EmberPrimaryColor;

    /// <summary>
    /// Always false, and deliberately so. A pulsing dot claims a liveness beat;
    /// Counters has none — CountersService documents that a counter which has not
    /// moved in an hour is perfectly healthy, so "no traffic" says nothing about
    /// the tool's state. (The build spec's §2.3 table pencilled a pulse on the
    /// live row; §4.4 and the service's own PillState doc-comment both rule it
    /// out. Following the later ruling — flagged on delivery.)
    /// </summary>
    public bool StatusPulsing => false;

    private void RaisePillState()
    {
        Raise(nameof(StatusPillText));
        Raise(nameof(StatusPillColor));
        Raise(nameof(StatusPulsing));
    }

    // Roster-shape projections — raised wherever the collection's LENGTH moves.
    private void RaiseRoster()
    {
        Raise(nameof(HasCounters));
        Raise(nameof(RosterListVisibility));
        Raise(nameof(RosterEmptyVisibility));
        Raise(nameof(RosterCountText));
    }

    // The roster's leading "#" column. Re-run after every add / remove / rebuild
    // so the numbers stay contiguous.
    private void Reindex()
    {
        for (int i = 0; i < Counters.Count; i++) Counters[i].SetIndex(i + 1);
    }

    // ── Load / refresh live counts ──────────────────────────────────────
    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await RefreshCountsAsync().ConfigureAwait(false);
    }

    private void BuildRows()
    {
        // A foreign ConfigChanged replaces every row instance, so the selection
        // has to be re-established by NAME afterwards — otherwise an edit made in
        // a second window would dump the streamer back to "nothing selected"
        // mid-typing.
        string? keep = _selected?.Name;

        SelectedCounter = null;
        Counters.Clear();
        _working.Counters ??= new List<CounterDef>();
        foreach (var d in _working.Counters)
            Counters.Add(new CounterRowVm(d, ScheduleSave));
        Reindex();

        if (!string.IsNullOrWhiteSpace(keep))
        {
            foreach (var row in Counters)
            {
                if (string.Equals(row.Name, keep, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedCounter = row;
                    break;
                }
            }
        }

        // Land on the first counter when there was nothing to restore — this is
        // also the tab's first build, and a master-detail page that opens on its
        // empty state reads as "no counters" to someone who has three.
        if (_selected is null && Counters.Count > 0) SelectedCounter = Counters[0];

        RaiseRoster();
        // Redundant while this method rebuilds wholesale — every path above that
        // lands on a counter goes through the SelectedCounter setter, which raises.
        // Kept because this is also the only place _working (and with it every
        // CounterDef the list reads through) is swapped for a fresh clone: a future
        // reconcile that RE-USES row instances instead of clearing them would leave
        // the setter silent while every def underneath changed, and a command list
        // quietly rendering the previous config is the exact failure this block was
        // added to prevent.
        Raise(nameof(ChatCommands));
    }

    private async Task RefreshCountsAsync()
    {
        List<(string Name, long Count)> list;
        try { list = await _svc.ListAsync().ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("CountersViewModel", "ListAsync failed", ex); return; }

        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (n, c) in list) map[n ?? ""] = c;

        _ui.Post(() =>
        {
            foreach (var row in Counters)
                row.SetCount(map.TryGetValue(row.Name, out var c) ? c : 0);
        });
    }

    // ── Add / remove counters ───────────────────────────────────────────
    public void AddCounter()
    {
        var def = new CounterDef { Name = NextCounterName() };
        _working.Counters ??= new List<CounterDef>();
        _working.Counters.Add(def);
        var row = new CounterRowVm(def, ScheduleSave);
        Counters.Add(row);
        Reindex();
        // Select it: on a master-detail page an ADD that leaves the detail column
        // on its empty state looks like the button did nothing.
        SelectedCounter = row;
        RaiseRoster();
        ScheduleSave();
    }

    public void RemoveCounter(CounterRowVm? row)
    {
        if (row is null) return;
        string name = row.Name;               // capture before the row is detached
        // Remember where it sat so the detail column can land on a neighbour
        // instead of collapsing to the empty state after every delete.
        int index = Counters.IndexOf(row);
        _working.Counters?.Remove(row.Def);
        Counters.Remove(row);
        Reindex();
        if (ReferenceEquals(_selected, row))
        {
            SelectedCounter = Counters.Count == 0
                ? null
                : Counters[Math.Min(index < 0 ? 0 : index, Counters.Count - 1)];
        }
        RaiseRoster();
        ScheduleSave();
        // Also drop the counter's backing row from the open "Counters" table so a
        // deleted-then-re-added counter starts fresh instead of resurrecting the old
        // count (mirrors Timer/Quotes, which delete their backing row on remove).
        if (!string.IsNullOrWhiteSpace(name))
            _ = Phoenix.Controls.Hub.Core.AsyncErrorBoundary.SafeRunAsync(
                () => _svc.DeleteAsync(name), "CountersViewModel", "delete counter row");
    }

    private string NextCounterName()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in _working.Counters ?? new List<CounterDef>())
            if (!string.IsNullOrWhiteSpace(d.Name)) taken.Add(d.Name.Trim());
        for (int i = 1; ; i++)
        {
            string candidate = "counter" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    // ── Live count operations (delegate to the always-fresh service) ─────
    // The service's write → AfterChangeAsync → CounterChanged → RefreshCountsAsync
    // path already marshals the fresh count into the row via _ui.Post, so we must NOT
    // push it here: the continuation runs on a threadpool thread (ConfigureAwait(false)),
    // and a direct row.SetCount there raises PropertyChanged off the UI thread
    // (RPC_E_WRONG_THREAD, swallowed) so the readout silently fails to advance.
    public async Task SetCountAsync(CounterRowVm? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.Name)) return;
        try { await _svc.SetAsync(row.Name, row.ParsedSetValue).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("CountersViewModel", "SetCountAsync failed", ex); }
    }

    public async Task ResetCountAsync(CounterRowVm? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.Name)) return;
        try { await _svc.ResetAsync(row.Name).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("CountersViewModel", "ResetCountAsync failed", ex); }
    }

    public async Task StepCountAsync(CounterRowVm? row, int sign)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.Name)) return;
        try { await _svc.AddAsync(row.Name, sign * row.ParsedStep).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("CountersViewModel", "StepCountAsync failed", ex); }
    }

    // ── Persistence ─────────────────────────────────────────────────────
    private void ScheduleSave()
    {
        _dirty = true;
        // Every row edit funnels through here (row _changed) on the UI thread —
        // the hook that keeps the CHAT COMMANDS block live. It is the only one
        // that catches a role tick: CounterRolesVm raises on ITSELF and calls this
        // action, so nothing on CounterRowVm moves when a checkmark changes, and the
        // Roles column would otherwise stay on the value it was rendered with.
        Raise(nameof(ChatCommands));
        if (_saveTimer is not null) { _saveTimer.Stop(); _saveTimer.Start(); }
        else _ = SaveWorkingAsync();
    }

    private void SaveNow()
    {
        _dirty = true;
        _saveTimer?.Stop();
        _ = SaveWorkingAsync();
    }

    private async Task SaveWorkingAsync()
    {
        if (!_dirty) return;
        _dirty = false;
        _selfSaving = true;   // suppress the ConfigChanged this save will echo back (see OnConfigChanged)
        try { await _svc.SaveConfigAsync(_working).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _selfSaving = false;   // no echo will arrive if the save threw before RaiseConfigChanged
            GlobalLogger.Error("CountersViewModel", "SaveConfigAsync failed", ex);
        }
    }

    // ── Service events ──────────────────────────────────────────────────
    private void OnConfigChanged(object? sender, EventArgs e)
    {
        _ui.Post(() =>
        {
            if (_disposed) return;
            // Consume our own save's echo without a rebuild. The flag is both set (in
            // SaveWorkingAsync, off the debounce timer Tick or SaveNow — both UI thread)
            // and cleared here on the UI thread, so it is race-free even though the
            // service deep-clones on save, which is exactly why the ReferenceEquals check
            // this replaces could never fire. Those two are the only paths that reach
            // SaveWorkingAsync; Dispose's final flush calls the service directly and needs
            // no flag because it runs after the handler is already detached.
            // Nothing on this surface needs refreshing after our own save: _working IS
            // the saved state and the counts come from CounterChanged, not from here.
            // The ONE exception is the status pill, which reads the SERVICE's live
            // state rather than _working — so it is refreshed above the guard, on
            // both the self-save and the foreign branch.
            RaisePillState();
            if (_selfSaving) { _selfSaving = false; return; }
            _working = Clone(_svc.Config);
            BuildRows();
            Raise(nameof(MasterEnabled));
            _ = RefreshCountsAsync();
        });
    }

    private void OnCounterChanged(object? sender, string name) => _ = RefreshCountsAsync();

    // Deep clone through JSON round-trip (mirrors the Loyalty / Timer VMs) so the
    // working copy is fully detached from the live service config.
    private static CountersConfig Clone(CountersConfig src)
    {
        try
        {
            string json = JsonSerializer.Serialize(src ?? new CountersConfig());
            return JsonSerializer.Deserialize<CountersConfig>(json) ?? new CountersConfig();
        }
        catch { return new CountersConfig(); }
    }
}
