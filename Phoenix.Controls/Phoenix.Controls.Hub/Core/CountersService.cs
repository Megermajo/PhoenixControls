using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // CountersService — the Hub-runtime brain of the named-counters pre-build tool
    // (sibling of GiveawayService / TimerService / LoyaltyService). Stateless-over-DB:
    // the DB "Counters" open table is the single source of truth for counts, so the
    // service NEVER caches a count (a streamer db.* write to the open table must not be
    // clobbered) — only the tool CONFIG (which counters exist + their settings) is held
    // in memory, mirroring the LoyaltyConfig pattern. Every count mutation delegates to
    // one atomic DB transaction; floor-at-zero is applied inside that transaction.
    //
    // The service is Hub-side but cannot touch the script dispatcher directly, so the
    // RaiseScriptEvent (Counter.OnChanged) seam is injected from Hub
    // (RegisterCountersCommands), exactly like Timer/Loyalty. The overlay readout no
    // longer needs a seam at all: the count is published into the Overlay Live Channel
    // (counter.<name>.count), which the browser subscribes to per key.
    //
    // ★ Since the 2026-08 tool-node cut, GRAPHS write the open table with the generic
    // db.* nodes (the Counter.* wrapper nodes were retired), which bypasses every
    // service hook — so the service runs a 15-second databank SWEEP (the RanksService
    // heartbeat pattern) that diffs the table against the last swept state and fires
    // the same side-effects a service-routed change would have: the overlay publish
    // AND Counter.OnChanged. Without it a db-written counter would silently freeze
    // every widget and never fire its trigger — the exact gap that made the wrapper
    // nodes look irreplaceable.
    public sealed class CountersService
    {
        private readonly DB _db;

        public CountersService(DB db) => _db = db ?? throw new ArgumentNullException(nameof(db));

        private static CountersService? _instance;
        private static readonly object _instanceGate = new();
        public static CountersService Instance
        {
            get
            {
                var i = _instance;
                if (i != null) return i;
                lock (_instanceGate) return _instance ??= new CountersService(DB.Instance);
            }
        }

        // ── Config (cached; counts are NEVER cached) ────────────────────────
        private volatile CountersConfig _config = new();
        public CountersConfig Config => _config;

        // The derived live-channel KEYS of every counter the last save (or the boot load)
        // knew about — a COPY, never a reference into the config.
        //
        // ★ The copy is the whole mechanism, not a defensive habit. CountersViewModel edits
        // ONE CountersConfig instance in place and hands that SAME instance to
        // SaveConfigAsync on every debounce, so the caller's "before" and "after" are one
        // object and one List and any diff computed from THEM is empty by construction.
        // SaveConfigAsync deep-clones what it is given (see CloneConfig), so _config itself
        // is now a fresh object per save — but this snapshot is what the diff reads either
        // way, which is what makes the sweep independent of every caller's reference
        // identity rather than dependent on the current clone rule.
        //
        // Written only on the save/boot path (SnapshotCounterKeys), read only there; the
        // reference swap is atomic and readers take it into a local first.
        private HashSet<string> _knownCounterKeys = new(StringComparer.Ordinal);

        /// <summary>Master gate — false makes the built-in chat commands, the
        /// live-channel count publish, and the Counter.OnChanged event a total no-op.
        /// The Architect Counter.* nodes still perform the raw DB read/write regardless
        /// (like any script command) — only the reactive side-effects go dormant.</summary>
        public bool Active => _config.Enabled;

        // ── Injected Hub-side seams ─────────────────────────────────────────
        /// <summary>
        /// The Overlay Live Channel this service publishes <c>counter.&lt;name&gt;.count</c>
        /// into. Defaults to the process-wide store, so unlike the bespoke COUNTER_UPDATE
        /// broadcast seam it replaced there is nothing for the bootstrapper to wire.
        ///
        /// Public get / internal set mirrors <c>LayerRuntime.Registry</c>: production cannot
        /// swap the channel at runtime, while the test assembly (<c>InternalsVisibleTo</c>)
        /// gives each test its OWN store instead of sharing one — the same isolation the
        /// DB.Instance flake class taught us to build in from the start.
        /// </summary>
        public OverlayLiveStore LiveStore { get; internal set; } = OverlayLiveStore.Instance;

        /// <summary>Raises a Counter.OnChanged script event (wired in RegisterCountersCommands).</summary>
        public Action<string, IReadOnlyDictionary<string, string>>? RaiseScriptEvent { get; set; }

        // ── Change notifications (UI) ───────────────────────────────────────
        /// <summary>
        /// Raised after EVERY <see cref="SaveConfigAsync"/>, including the saver's own.
        ///
        /// ★ A subscriber CANNOT detect its own save by reference-identity
        /// (<c>ReferenceEquals(svc.Config, myWorkingCopy)</c>): <see cref="SaveConfigAsync"/>
        /// deep-clones what it is handed, so <see cref="Config"/> is a fresh object after
        /// every save and such a check is dead code that never fires. The panel therefore
        /// suppresses its own echo with a <c>_selfSaving</c> flag set around the save
        /// (CountersViewModel.SaveWorkingAsync), exactly like AutomodViewModel — without it
        /// each debounced keystroke save rebuilds the whole row list out from under the
        /// streamer's cursor. Any new subscriber that saves must do the same.
        /// </summary>
        public event EventHandler? ConfigChanged;
        public event EventHandler<string>? CounterChanged;

        private void RaiseConfigChanged() => SafeEvent.Raise(ConfigChanged, this, EventArgs.Empty, "CountersService", "ConfigChanged");
        private void RaiseCounterChanged(string name) => SafeEvent.Raise(CounterChanged, this, name, "CountersService", "CounterChanged");

        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
        private static long NowUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // ── Lifecycle ───────────────────────────────────────────────────────
        public async Task InitializeAsync()
        {
            try
            {
                string? raw = await _db.LoadCountersConfigAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var cfg = JsonSerializer.Deserialize<CountersConfig>(raw!, JsonOpts);
                    if (cfg != null) _config = Normalize(cfg);
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("CountersService", "InitializeAsync: config load failed", ex);
            }
            // The baseline the FIRST save diffs against. Without it, a counter that came out
            // of the DB at boot and was removed by the very first panel save would look like
            // it had never been in the config, and its key would never be retracted.
            SnapshotCounterKeys();
            try
            {
                await _db.EnsureCountersTableAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("CountersService", "InitializeAsync: counts-table ensure failed", ex);
            }
            // Baseline the sweep from the table UNCONDITIONALLY — even (especially) when
            // the tool boots disabled. The rows present at boot are the starting state,
            // never "changes": without this, a dormant boot left _sweepState empty and the
            // first sweep after a mid-session enable replayed the ENTIRE table as
            // Counter.OnChanged — one spurious trigger per pre-existing counter (the
            // adversarial review's headline catch; five lenses found it independently).
            // Genuine db.* changes made while dormant still announce on re-enable, which
            // is the honest catch-up the sweep tests pin.
            await BaselineSweepStateAsync().ConfigureAwait(false);
            // Seed the live channel with what the counters ALREADY are. Must run after the
            // table ensure (the list read below would otherwise hit a missing table) and
            // before the "online" log, so the channel is populated by the time anything
            // else in the boot sequence can observe it.
            await SeedLiveChannelAsync().ConfigureAwait(false);
            // The db-write sweep runs unconditionally and self-gates inside, mirroring
            // RanksService's overlay pump: the master toggle can flip at any time, and a
            // dormant tick costs one volatile read and returns before touching the databank.
            StartDatabankSweep();
            GlobalLogger.Log(
                $"CountersService online — {_config.Counters.Count} counter(s), tool {(Active ? "ENABLED" : "disabled")}.",
                "CountersService", LogLevel.System);
        }

        // ── Databank sweep (db.* writes → overlay + Counter.OnChanged) ──────
        /// <summary>15 s, matching RanksService.LiveInterval: a counter moves on human
        /// actions, so a faster cadence would buy databank traffic and nothing else. The
        /// store coalesces identical values, so a quiet table costs one list query and a
        /// handful of dictionary writes per tick, and nothing on the wire.</summary>
        internal static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

        private readonly object _sweepLoopGate = new();
        private CancellationTokenSource? _sweepCts;
        private Task? _sweepTask;
        private bool _sweepStarted;

        // The last swept (or service-routed) state per derived key. AfterChange /
        // AfterDelete keep it current for service-routed mutations so the sweep never
        // re-fires an event the service already raised; the sweep itself updates it for
        // db-written changes. Guarded by _sweepStateGate — the sweep runs on a worker
        // while chat / panel / script mutations land on other threads.
        private readonly object _sweepStateGate = new();
        private readonly Dictionary<string, (string Name, long Count)> _sweepState = new(StringComparer.Ordinal);

        private void StartDatabankSweep()
        {
            lock (_sweepLoopGate)
            {
                if (_sweepStarted) return;
                _sweepStarted = true;
                _sweepCts = new CancellationTokenSource();
                var ct = _sweepCts.Token;
                _sweepTask = Task.Run(() => SweepLoopAsync(ct));
            }
        }

        /// <summary>Records the open table's current rows as the sweep's starting state,
        /// gated by NOTHING — see the InitializeAsync call site for why a dormant boot
        /// must baseline too. Internal so the sweep tests can baseline deterministically.</summary>
        internal async Task BaselineSweepStateAsync()
        {
            try
            {
                var rows = await _db.CounterListAsync().ConfigureAwait(false);
                lock (_sweepStateGate)
                {
                    foreach (var row in rows)
                    {
                        string key = KeyName(row.Name);
                        if (key.Length == 0) continue;
                        _sweepState[key] = (row.Name, row.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("CountersService", "InitializeAsync: sweep baseline failed", ex);
            }
        }

        /// <summary>Cancels the databank sweep and drains it — the RanksService.ShutdownAsync
        /// shape. Counts live in the databank and config is persisted on every edit, so
        /// there is nothing to flush.</summary>
        public async Task ShutdownAsync()
        {
            CancellationTokenSource? cts;
            Task? loop;
            lock (_sweepLoopGate)
            {
                cts = _sweepCts;
                loop = _sweepTask;
                _sweepStarted = false;
                _sweepCts = null;
                _sweepTask = null;
            }
            try { cts?.Cancel(); } catch { /* best-effort */ }
            if (loop is not null)
            {
                try { await loop.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected */ }
                catch (Exception ex) { GlobalLogger.Error("CountersService", "databank sweep drain failed", ex); }
            }
            cts?.Dispose();
        }

        private async Task SweepLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(SweepInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                // A bg-thread throw must never kill the process.
                try { await SweepOnceAsync().ConfigureAwait(false); }
                catch (Exception ex) { GlobalLogger.Error("CountersService", "databank sweep failed", ex); }
            }
        }

        /// <summary>
        /// One sweep: reads the open table and fires the standard change side-effects
        /// (<see cref="AfterChange"/> / <see cref="AfterDelete"/>) for every counter a
        /// db.* write moved, created or deleted since the last look.
        ///
        /// Concurrency posture: a service-routed mutation that lands BETWEEN this sweep's
        /// table read and its diff has already updated <see cref="_sweepState"/> to a value
        /// the (older) read disagrees with. Re-firing from the older read would publish a
        /// stale count over a fresh one, so any key whose recorded state moved since the
        /// pre-read snapshot is SKIPPED this tick, and each emission RE-VERIFIES its claim
        /// under the gate immediately before firing — a service mutation landing in the
        /// diff→emit gap drops the sweep's older emission, and the master toggle flipping
        /// off in that gap rolls the stamp back so the change is re-detected by a later
        /// enabled sweep instead of being swallowed. The accepted residual is one LATE
        /// delivery (an emission racing a toggle-off by microseconds still fires for a
        /// change made while the tool was on), never a stale overwrite that outlives the
        /// next sweep and never a swallow.
        ///
        /// Internal (not private) so the test suite can drive a sweep deterministically
        /// without waiting out the interval.
        /// </summary>
        internal async Task SweepOnceAsync()
        {
            if (!Active) return;

            Dictionary<string, (string Name, long Count)> before;
            lock (_sweepStateGate) before = new(_sweepState, StringComparer.Ordinal);

            var rows = await _db.CounterListAsync().ConfigureAwait(false);
            var fresh = new Dictionary<string, (string Name, long Count)>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                string key = KeyName(row.Name);
                if (key.Length == 0) continue;
                fresh[key] = (row.Name, row.Count);
            }

            List<(string Key, string Name, long Count)>? changed = null;
            List<(string Key, string Name)>? gone = null;
            lock (_sweepStateGate)
            {
                foreach (var kv in fresh)
                {
                    bool hadBefore = before.TryGetValue(kv.Key, out var was);
                    bool hasNow = _sweepState.TryGetValue(kv.Key, out var cur);
                    // Touched by a service-routed mutation since the pre-read snapshot →
                    // the service already fired the side-effects for a NEWER value; skip.
                    if (hadBefore != hasNow || (hadBefore && was.Count != cur.Count)) continue;
                    if (hadBefore && was.Count == kv.Value.Count) continue;   // unchanged
                    _sweepState[kv.Key] = kv.Value;
                    (changed ??= new()).Add((kv.Key, kv.Value.Name, kv.Value.Count));
                }
                foreach (var kv in before)
                {
                    if (fresh.ContainsKey(kv.Key)) continue;
                    bool hasNow = _sweepState.TryGetValue(kv.Key, out var cur);
                    if (!hasNow || cur.Count != kv.Value.Count) continue;     // touched meanwhile
                    _sweepState.Remove(kv.Key);
                    (gone ??= new()).Add((kv.Key, kv.Value.Name));
                }
            }

            // Emissions OUTSIDE the state gate (the OnChanged raise fans out to the script
            // dispatcher and the publish takes the store's own locks) — but each one
            // re-verifies its claim under the gate first and emits through the shared
            // ACTIVE-half helpers rather than AfterChange/AfterDelete, whose unconditional
            // re-stamp + bail-on-dormant shape would turn a toggle-off in this gap into a
            // permanent swallow (stamp == DB value, event never fired, nothing left to diff).
            if (changed != null)
            {
                foreach (var (key, name, count) in changed)
                {
                    bool emit;
                    lock (_sweepStateGate)
                    {
                        bool current = _sweepState.TryGetValue(key, out var cur) && cur.Count == count;
                        if (current && !Active)
                        {
                            // Roll the stamp back so a later ENABLED sweep re-detects this
                            // change instead of finding it silently pre-recorded.
                            if (before.TryGetValue(key, out var prior)) _sweepState[key] = prior;
                            else _sweepState.Remove(key);
                            emit = false;
                        }
                        else emit = current;   // service touched it meanwhile → its newer emission won
                    }
                    if (emit)
                    {
                        RaiseCounterChanged(name);
                        // The sweep is the ONLY path a db.*-written change takes — it never
                        // passes AfterChange — so without this row the feed would silently
                        // omit every change a graph made. Timed at detection, which is up to
                        // one SweepInterval (15 s) after the write actually happened.
                        RecordActivity("SET", $"{ClipForActivity(name)} is now {count.ToString(CultureInfo.InvariantCulture)} (databank write).");
                        EmitChangeSideEffects(name, count);
                    }
                }
            }
            if (gone != null)
            {
                foreach (var (key, name) in gone)
                {
                    bool emit;
                    lock (_sweepStateGate)
                    {
                        bool current = !_sweepState.ContainsKey(key);
                        if (current && !Active)
                        {
                            if (before.TryGetValue(key, out var prior)) _sweepState[key] = prior;
                            emit = false;
                        }
                        else emit = current;
                    }
                    if (emit)
                    {
                        RaiseCounterChanged(name);
                        EmitDeleteSideEffects(name);
                    }
                }
            }
        }

        /// <summary>
        /// Publishes every known counter's CURRENT count into the Overlay Live Channel once,
        /// at startup.
        ///
        /// Without this the channel is empty from Hub start until the first value change,
        /// because <see cref="AfterChange"/> is the only publisher and it fires on change
        /// only. A deaths counter sitting at 47 would render BLANK for the whole session
        /// until the next death, and a counter that never moves would render blank forever —
        /// the browser's honest-data rule (no PreviewText mock in production) turned "no
        /// value yet" from a fake number into nothing at all, so the missing seed became
        /// user-visible instead of merely wrong. TimerService has no equivalent hole: its
        /// 1 Hz loop republishes every timer unconditionally, which seeds itself.
        ///
        /// Two sources, unioned:
        ///   * the open table's live rows — the authoritative counts, and the ONLY place an
        ///     ad-hoc counter written by a bare Counter.Add node (never declared in the tool
        ///     config) can be found;
        ///   * the configured counters, zero-filled. A declared-but-never-incremented
        ///     counter has no row yet, and <see cref="GetAsync"/> / Counter.Get / the chat
        ///     reply all answer 0 for it — so 0 is the honest value, and publishing it is
        ///     what makes a freshly-configured widget read 0 instead of blank.
        ///
        /// Costs nothing when no widget subscribes (the store's publisher-side gate stops at
        /// one dictionary write) and is idempotent under coalescing, so the first real change
        /// after boot ships one frame, not two.
        ///
        /// Gated by <see cref="Active"/>, exactly like <see cref="AfterChange"/>: a dormant
        /// tool publishes nothing. Deliberately NOT re-run from
        /// <see cref="SaveConfigAsync"/> when the streamer flips the tool on mid-session —
        /// that leaves a smaller version of the same gap (blank until the next change), but
        /// it is a user-initiated, immediately-visible action rather than a silent
        /// session-long hole, and a second seed site is a second thing to keep in step.
        /// </summary>
        private async Task SeedLiveChannelAsync()
        {
            if (!Active) return;
            try
            {
                // Ordinal, matching the store's own key comparer — the set holds derived
                // KEYS (already lower-cased by KeyName), not raw names, so two rows that
                // differ only in case collapse here exactly as they do in the channel.
                var seeded = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in await _db.CounterListAsync().ConfigureAwait(false))
                {
                    string key = KeyName(row.Name);
                    if (key.Length == 0) continue;
                    seeded.Add(key);
                    // (The sweep baseline is NOT taken here — this method is Active-gated
                    // and the baseline must run on a dormant boot too; see
                    // BaselineSweepStateAsync, which InitializeAsync runs first.)
                    PublishCount(row.Name, row.Count);
                }
                foreach (var def in _config.Counters)
                {
                    string key = KeyName(def.Name);
                    // seeded.Add returning false means a real row already published this
                    // key — never let the config's zero overwrite an actual count.
                    if (key.Length == 0 || !seeded.Add(key)) continue;
                    PublishCount(def.Name, 0);
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("CountersService", "InitializeAsync: live-channel seed failed", ex);
            }
        }

        private static CountersConfig Normalize(CountersConfig cfg)
        {
            cfg.Counters ??= new List<CounterDef>();
            foreach (var c in cfg.Counters)
            {
                c.Name ??= "";
                c.Command ??= "";
                c.ReplyTemplate ??= "";
                c.ViewRoles ??= CounterRoles.All();
                c.ModifyRoles ??= CounterRoles.Mods();
                if (c.Step == 0) c.Step = 1;
            }
            return cfg;
        }

        // Deep-clone via a JSON round-trip so _config is ALWAYS a private copy no caller
        // still holds a reference to — the same rule AutomodService / QuotesService /
        // CustomCommandsService already follow, and for the same reason.
        //
        // ★ Load-bearing, not defensive habit. CountersViewModel edits ONE CountersConfig
        // in place (AddCounter does _working.Counters.Add, RemoveCounter does
        // _working.Counters.Remove) and hands that same instance to SaveConfigAsync on
        // every debounced save. Adopting it aliased the panel's live List into
        // TryHandleCountersChatCommandAsync, which enumerates _config.Counters on the chat
        // dispatch thread with no lock: a click on add/remove while any '!' line was being
        // handled threw "Collection was modified" there (swallowed as "not handled", so the
        // viewer's counter command silently did nothing) and could equally abort the
        // RetractDroppedCounterKeysAsync sweep, leaving a deleted counter's overlay key
        // painting.
        private static CountersConfig CloneConfig(CountersConfig? cfg)
        {
            if (cfg == null) return new CountersConfig();
            try
            {
                return JsonSerializer.Deserialize<CountersConfig>(
                    JsonSerializer.Serialize(cfg, JsonOpts), JsonOpts) ?? new CountersConfig();
            }
            catch { return new CountersConfig(); }
        }

        // ── Config edits (panel) ────────────────────────────────────────────
        /// <summary>Replaces the whole config, persists it, notifies the UI, and retracts
        /// the live-channel key of any counter the edit removed outright. The instance the
        /// caller hands in is never adopted — see <see cref="CloneConfig"/>.</summary>
        public async Task SaveConfigAsync(CountersConfig cfg)
        {
            _config = Normalize(CloneConfig(cfg));
            try
            {
                string json = JsonSerializer.Serialize(_config, JsonOpts);
                await _db.SaveCountersConfigAsync(json, NowUnixMs()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("CountersService", "SaveConfigAsync failed", ex);
            }
            await RetractDroppedCounterKeysAsync().ConfigureAwait(false);
            RaiseConfigChanged();
        }

        // Re-derives _knownCounterKeys from the CURRENT config. Always a fresh set — see the
        // field comment for why a reference into the config would defeat the next save's
        // diff entirely.
        private void SnapshotCounterKeys()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var def in _config.Counters)
            {
                string k = KeyName(def.Name);
                if (k.Length > 0) keys.Add(k);
            }
            _knownCounterKeys = keys;
        }

        /// <summary>
        /// The CONFIG half of counter deletion: any counter that the PREVIOUS save knew
        /// about, is not in the config this save just installed, and has no surviving row
        /// gets its live-channel key tombstoned.
        ///
        /// "What the previous save knew about" is <see cref="_knownCounterKeys"/> — a key
        /// snapshot taken after every save and after the boot load — and NOT the config
        /// instance that was in <c>_config</c> a moment ago. That distinction is the entire
        /// reason this method works for the caller shape the panel actually has: it hands the
        /// same CountersConfig object back on every debounced save, having mutated it in
        /// place, so a before/after comparison of the objects IT passed compares one object
        /// with itself and finds nothing dropped, ever. See the field comment.
        ///
        /// It exists because the panel's remove button does two things in two places — it
        /// drops the def (this path, behind a 400 ms debounce) and calls
        /// <see cref="DeleteAsync"/> for the row (fire-and-forget) — so neither half sees
        /// the final state on its own. The two are deliberately complementary rather than
        /// ordered: if the row delete lands FIRST, this sweep finds no row and tombstones;
        /// if the config save lands first, <see cref="DeleteAsync"/> finds no def and
        /// tombstones. Either interleaving converges on the same key state, which is why
        /// no sequencing between them had to be invented.
        ///
        /// A surviving ROW means the counter did not go away — it became an ad-hoc counter
        /// (script-written, no def), or the streamer RENAMED it, which leaves the old row
        /// behind with its count intact. Its value is still real, so the key keeps painting
        /// it rather than being blanked.
        ///
        /// The snapshot is refreshed on EVERY save, including while the tool is dormant, so
        /// that re-enabling it does not resurrect a diff against a config two edits old. Only
        /// the retraction work is gated by <see cref="Active"/>, like every other publish
        /// here, and the DB is read only when something actually left the config — so an
        /// ordinary debounced keystroke save costs one set comparison over a handful of keys.
        /// </summary>
        private async Task RetractDroppedCounterKeysAsync()
        {
            // Compared as derived KEYS, not raw names, so a case-only edit ("deaths" →
            // "Deaths") is correctly seen as the same key and retracts nothing.
            var before = _knownCounterKeys;
            SnapshotCounterKeys();
            var kept = _knownCounterKeys;

            if (!Active || before.Count == 0) return;

            List<string>? dropped = null;
            foreach (string key in before)
                if (!kept.Contains(key)) (dropped ??= new List<string>()).Add(key);
            if (dropped == null) return;

            try
            {
                var live = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in await _db.CounterListAsync().ConfigureAwait(false))
                {
                    string k = KeyName(row.Name);
                    if (k.Length > 0) live.Add(k);
                }
                foreach (string key in dropped)
                    if (!live.Contains(key)) RetractCountKey(key);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("CountersService", "SaveConfigAsync: live-channel retraction failed", ex);
            }
        }

        /// <summary>The managed counter with this name, or null (case-insensitive).</summary>
        public CounterDef? Find(string name)
            => _config.Counters.FirstOrDefault(
                c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        // ── Count operations (nodes + chat + panel) ─────────────────────────
        /// <summary>Reads the current count (0 when unset). Always fresh from the DB.</summary>
        public Task<long> GetAsync(string name) => _db.CounterGetAsync(name ?? "");

        /// <summary>Atomically adds delta (floored at 0 for managed floor-at-zero
        /// counters). Returns the new value and fires the change side-effects.</summary>
        public async Task<long> AddAsync(string name, long delta)
        {
            name ??= "";
            bool floor = Find(name)?.FloorAtZero ?? false;
            long result = await _db.CounterAddAsync(name, delta, floor).ConfigureAwait(false);
            AfterChange(name, result);
            return result;
        }

        /// <summary>Sets the exact value (clamped at 0 for managed floor-at-zero
        /// counters). Returns the value and fires the change side-effects.</summary>
        public async Task<long> SetAsync(string name, long value)
        {
            name ??= "";
            if ((Find(name)?.FloorAtZero ?? false) && value < 0) value = 0;
            long result = await _db.CounterSetAsync(name, value).ConfigureAwait(false);
            AfterChange(name, result);
            return result;
        }

        /// <summary>Resets a counter to 0.</summary>
        public Task<long> ResetAsync(string name) => SetAsync(name, 0);

        // Synchronous: the live-channel publish is a dictionary write, and the script-event
        // raise always was sync. Nothing here awaits, so an async signature would only add
        // a state machine to every counter mutation.
        private void AfterChange(string name, long count)
        {
            // Record the state FIRST — dormant or not — so the databank sweep never
            // re-announces a mutation the service itself performed (and a change made
            // while the tool was dormant is not replayed as an event on re-enable).
            string sweepKey = KeyName(name);
            if (sweepKey.Length > 0)
                lock (_sweepStateGate) _sweepState[sweepKey] = (name, count);

            // The panel always reflects a change (chat / button / sweep-driven) so an open
            // Counters window stays live even while the tool's chat + overlay surfaces are off.
            RaiseCounterChanged(name);

            // Recorded on the SAME side of the master gate as RaiseCounterChanged, and for
            // the same reason: a service-routed write happens whether the tool is dormant or
            // not, so a feed that only recorded the enabled case would under-report exactly
            // when a streamer is trying to work out why their overlay is not moving.
            RecordActivity("SET", $"{ClipForActivity(name)} is now {count.ToString(CultureInfo.InvariantCulture)}.");

            // Counter.OnChanged + the live-channel publish are the tool's ACTIVE
            // surfaces — gated by the master toggle so a disabled ("dormant") tool raises no
            // tool event and pushes nothing to the overlay. A graph's db.* write to the open
            // table lands regardless (the databank capability never goes dormant); only the
            // reactive side-effects do, and the 15 s sweep picks the write up once the tool
            // is on.
            if (!Active) return;

            EmitChangeSideEffects(name, count);
        }

        // The ACTIVE half of a change, shared by AfterChange and the sweep's guarded
        // emission path (which must not route through AfterChange — its unconditional
        // re-stamp + bail-on-dormant shape is right for service-routed mutations and
        // wrong for a sweep emission that already stamped under its own guard).
        private void EmitChangeSideEffects(string name, long count)
        {
            RaiseCounterOnChanged(name, count);
            PublishCount(name, count);
        }

        /// <summary>
        /// The side-effects of a row DELETE, i.e. <see cref="AfterChange"/>'s sibling for the
        /// one mutation that removes a value instead of writing one. Same shape and same
        /// master-toggle gate; the only difference is what reaches the overlay.
        ///
        /// Only ever called when a row actually went — see <see cref="DeleteAsync"/>.
        ///
        /// The rule is: <b>leave the channel painting what the next boot's
        /// <see cref="SeedLiveChannelAsync"/> would paint.</b> That seed unions live rows
        /// with configured counters, so after a delete:
        ///   * a counter that still has a DEF is seeded 0 — and 0 is also what
        ///     <see cref="GetAsync"/>, Counter.Get and the chat reply all answer for it, so
        ///     publishing 0 here is the honest value, not a placeholder;
        ///   * a counter with no def is seeded NOTHING, so its key is tombstoned (see
        ///     <see cref="RetractCount"/>). Publishing 0 for it would leave a confident
        ///     "deaths: 0" on stream this session and a blank widget after the next
        ///     restart — the browser's own honest-data rule, applied inconsistently.
        ///
        /// ★ "What it would PAINT", not "the state it would rebuild", and the difference is
        /// real rather than pedantic. A widget's Value and Text do land exactly where a fresh
        /// boot would leave them. Its <b>State pin does not</b>: the seed leaves an
        /// undeclared counter's key MISSING, while the tombstone here publishes JSON null,
        /// which reads Active. <see cref="OverlayLiveStore"/> has no per-key remove and no
        /// TTL, so Missing is unreachable for any key that was ever published, and this
        /// method cannot promise otherwise. The user-visible consequence, worth knowing
        /// before wiring one: a widget gated on <c>Result.If Equals="Missing" → hide</c>
        /// stays mounted and blank after a delete instead of hiding. Gate on the VALUE
        /// (blank Text) to catch both cases.
        ///
        /// Counter.OnChanged fires with 0 in both cases, because the counter's effective
        /// value genuinely did become 0: every other read in the product now answers 0, and
        /// a script watching for changes should not have to poll to learn that.
        /// </summary>
        private void AfterDelete(string name)
        {
            // Same first-move as AfterChange: the sweep must see this row as already gone.
            string sweepKey = KeyName(name);
            if (sweepKey.Length > 0)
                lock (_sweepStateGate) _sweepState.Remove(sweepKey);

            RaiseCounterChanged(name);
            RecordActivity("DEL", $"{ClipForActivity(name)} was deleted.");
            if (!Active) return;

            EmitDeleteSideEffects(name);
        }

        // ── Panel activity feed ─────────────────────────────────────────────
        /// <summary>The key this tool's rows carry in <see cref="ToolActivityRing"/>.</summary>
        public const string ActivityTool = "Counters";

        // A counter name is streamer-supplied but unbounded — an ad-hoc Counter.Add node can
        // mint one of any length — so every name that reaches a row goes through here.
        private const int ActivityFieldMaxChars = 48;

        private static string ClipForActivity(string? text)
        {
            string t = (text ?? string.Empty).Trim();
            if (t.Length == 0) return "(unnamed)";
            return t.Length <= ActivityFieldMaxChars ? t : t[..ActivityFieldMaxChars].TrimEnd() + "...";
        }

        // Observation only: recording sits in the commit of a count mutation, so a fault in
        // it must never become a fault in the count.
        private static void RecordActivity(string kind, string message)
        {
            try { ToolActivityRing.Record(ActivityTool, kind, message); }
            catch (Exception ex) { GlobalLogger.Error("CountersService", "activity record failed", ex); }
        }

        // ── Status pill ─────────────────────────────────────────────────────
        /// <summary>
        /// What the strip's status pill says. Only two states, and that is the honest
        /// answer for this tool — but the dormant one is NOT a restatement of the switch:
        /// switching Counters off stops the chat commands, the live-channel publish and the
        /// <c>Counter.OnChanged</c> event, and stops nothing else. The Architect
        /// <c>Counter.*</c> nodes keep reading and writing the open table, so the numbers
        /// carry on moving while every surface that shows them is dark. That is the fact the
        /// pill exists to carry.
        ///
        /// <para>No third state, and no liveness beat: a counter that has not moved in an
        /// hour is perfectly healthy (see <c>PublishCount</c>), so staleness says nothing
        /// here.</para>
        /// </summary>
        public enum CountersPillState
        {
            /// <summary>Switched off: commands, overlay and events are dormant — the
            /// databank is still writable, and the counts still move.</summary>
            DormantDatabankStillWritable,
            /// <summary>Switched on: commands and overlay live.</summary>
            CommandsAndOverlayLive,
        }

        /// <summary>Pure state machine behind <see cref="PillState"/>.</summary>
        internal static CountersPillState ComputePillState(bool enabled)
            => enabled ? CountersPillState.CommandsAndOverlayLive : CountersPillState.DormantDatabankStillWritable;

        /// <summary>The live pill state.</summary>
        public CountersPillState PillState => ComputePillState(_config.Enabled);

        // The ACTIVE half of a delete — same split, same reason as EmitChangeSideEffects.
        private void EmitDeleteSideEffects(string name)
        {
            RaiseCounterOnChanged(name, 0);

            if (Find(name) != null) PublishCount(name, 0);
            else                    RetractCount(name);
        }

        // Counter.OnChanged script event — event.counter / event.count tokens. Shared by
        // the change and delete paths so the two can never drift on the token names, which
        // are pinned in three more places (the exporter arm, AutocompleteScopeBuilder and
        // VarChainAnalyzer.ResultEmitterMap).
        private void RaiseCounterOnChanged(string name, long count)
        {
            var raise = RaiseScriptEvent;
            if (raise == null) return;
            try
            {
                var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["event.counter"] = name,
                    ["event.count"] = count.ToString(CultureInfo.InvariantCulture),
                };
                raise("Counter.OnChanged", vars);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("CountersService", "RaiseScriptEvent(Counter.OnChanged) failed", ex);
            }
        }

        /// <summary>
        /// THE one place a count becomes a live-channel key: <c>counter.&lt;name&gt;.count</c>,
        /// the key a Counter.Value widget binds. Lower-cased — and deliberately NOT trimmed —
        /// exactly as the browser keys its counter lookup (see <see cref="KeyName"/>), so an
        /// author who types "Deaths" in the widget and "deaths" in chat still lands on one key.
        /// NO expected interval — a counter is event-driven, so a count that has not moved in
        /// an hour is perfectly Active and must not decay to Stale.
        ///
        /// Shared by <see cref="AfterChange"/> and <see cref="SeedLiveChannelAsync"/> rather
        /// than duplicated: the whole point of the trim rule below is that publisher and
        /// reader run the identical rule, and a seed that derived its key even slightly
        /// differently would publish keys no widget ever subscribes — blank readout, no error.
        /// </summary>
        private void PublishCount(string name, long count)
        {
            string key = KeyName(name);
            if (key.Length == 0) return;
            LiveStore.PublishNumber($"counter.{key}.count", count, LiveSource);
        }

        /// <summary>
        /// Tombstones <c>counter.&lt;name&gt;.count</c> — the closest thing to a retraction the
        /// channel offers, and an exact one for what a widget paints.
        ///
        /// The store has no per-key remove and no TTL, so a key published once reports Active
        /// for the rest of the session. Adding an ExpectedInterval to this family so it could
        /// decay to Stale instead is NOT the answer: counters are event-driven, three separate
        /// places (PublishCount above, the compositor's liveness-vocabulary note, and the
        /// Counter.Value reader) document them as never-Stale, and a cadence would make every
        /// counter nobody has touched for a minute report Stale while still being perfectly
        /// current.
        ///
        /// Publishing JSON null is the honest retraction instead: null is a legal published
        /// value, and the browser's liveRenderableValue maps it to "nothing to paint", so the
        /// Counter.Value widget's Text goes blank and its Value pin reads 0 — the same
        /// RENDERING as a counter that was never published.
        ///
        /// ★ The same rendering, NOT the same state. State still reports Active, because the
        /// key genuinely is being published — the store's documented split of liveness from
        /// paintability. So this is a retraction of the VALUE and not of the KEY, and it is
        /// as close to a retraction as the channel can get: a widget branching
        /// <c>Equals="Missing"</c> will not fire after a delete, only one branching on the
        /// blank Text (or a 0 Value) will. Both the Counter.Value reader in compositor.js and
        /// <see cref="AfterDelete"/> record the same limit; if the store ever grows a real
        /// remove API, all three change together.
        /// </summary>
        private void RetractCount(string name) => RetractCountKey(KeyName(name));

        // Retraction by an ALREADY-DERIVED key, for the config sweep — which diffs key
        // snapshots and so never holds the counter's original name. Splitting it here rather
        // than round-tripping a key back through KeyName keeps one rule ("the key is built in
        // exactly one place") honest at both call sites.
        private void RetractCountKey(string key)
        {
            if (key.Length == 0) return;
            LiveStore.Publish($"counter.{key}.count", null, LiveSource);
        }

        // Provenance tag for every counter key — one spelling, because the store scopes
        // ExpectedInterval inheritance by Source and a second tag would fragment provenance
        // for no gain.
        private const string LiveSource = "tool:Counters";

        // Counter name → the key segment. Live-channel keys are literal, so a name with a
        // dot or a space produces exactly that key: unusual, still readable, and the widget
        // binds the same literal.
        //
        // Lower-casing is the ONLY normalisation, deliberately, because the reader's is the
        // same: the browser derives its key with `String(name).toLowerCase()` and never
        // trims (`stripQuotes` strips a quote pair, nothing else). Trimming here — as this
        // did at first — would have made a counter named " Deaths" publish
        // counter.deaths.count while the widget subscribed counter. deaths.count: a
        // permanently blank readout with no error anywhere. Publisher and reader must run
        // the identical rule, so the rule has to be the weaker of the two.
        //
        // The emptiness check at the call site is likewise the browser's own: JS treats ""
        // as falsy and anything else — including a lone space — as a real name, so a
        // whitespace-only counter yields a key on both halves rather than silently vanishing
        // on one.
        private static string KeyName(string? name) => (name ?? string.Empty).ToLowerInvariant();

        // ── Panel helpers ───────────────────────────────────────────────────
        /// <summary>All live rows (name + count) from the open table.</summary>
        public Task<List<(string Name, long Count)>> ListAsync() => _db.CounterListAsync();

        /// <summary>
        /// Removes a counter's stored count — its row in the OPEN "Counters" table — and
        /// fires the delete side-effects (<see cref="AfterDelete"/>). Drives both the panel's
        /// remove button and the Architect Counter.Delete node. Returns whether a row went.
        ///
        /// An unknown name is a TOTAL no-op — no Counter.OnChanged, no live-channel write,
        /// not even the panel refresh — and that is enforced by the rows-affected count, not
        /// merely intended. Firing the side-effects unconditionally cost two real bugs: a
        /// graph ending every stream with Counter.Delete("x") raised a phantom
        /// Counter.OnChanged at 0 for a counter that never existed, and — worse —
        /// <see cref="RetractCount"/> published <c>counter.&lt;name&gt;.count</c> for that
        /// name, minting a channel key permanently. The store has no remove API, and its
        /// key-space cap is deliberately script:/process:-only so a <c>tool:</c> source can
        /// never be refused; a chat-triggered Counter.Delete("{user.name}_streak") therefore
        /// minted one key per viewer, for the life of the process.
        ///
        /// ★ IT DOES NOT TOUCH THE TOOL CONFIG, and that is the deliberate line between the
        /// two callers. The panel's remove button additionally drops the counter's
        /// <c>CounterDef</c> — its chat command, role checkmarks, reply template, step and
        /// cooldown — because the panel IS the config surface. A script node must not, for
        /// three reasons:
        ///
        ///   * Blast radius. A graph wired into a stream-end trigger runs every stream. If
        ///     Counter.Delete removed the definition, one such graph would permanently erase
        ///     a setup the streamer tuned by hand, with no undo and no prompt. The reverse
        ///     surprise — "I deleted it and the panel still lists it at 0" — is visible,
        ///     recoverable and one click from being finished.
        ///   * The config has exactly ONE writer by construction — the Counters panel.
        ///     CountersViewModel keeps its own <c>CountersConfig</c> working copy, mutates it
        ///     in place and re-saves that same instance behind a 400 ms debounce (the service
        ///     deep-clones it on the way in — see <see cref="CloneConfig"/> — so the live
        ///     <c>_config</c> is never the panel's object). A node writing the config would
        ///     therefore be silently undone by the panel's next save — the def reappears —
        ///     and mutating <c>_config.Counters</c> from a script thread would throw
        ///     "collection was modified" inside TryHandleCountersChatCommandAsync, which
        ///     enumerates that same list on the chat path with no lock.
        ///   * No other pre-build node writes tool config. Every Counter.* / Quote.* /
        ///     Timer.* / Loyalty.* command touches tool DATA; keeping that line intact is
        ///     what makes "a script cannot reconfigure your tools behind your back" true.
        /// </summary>
        public async Task<bool> DeleteAsync(string name)
        {
            name ??= "";
            int rows = await _db.CounterDeleteAsync(name).ConfigureAwait(false);
            if (rows == 0) return false;
            AfterDelete(name);
            return true;
        }
    }
}
