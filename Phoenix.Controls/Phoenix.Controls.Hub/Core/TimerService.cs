using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Timer / subathon engine — the Hub-runtime brain of the countdown system.
    // Owns the rules (slug generation, event→seconds mapping, Happy-Hour, the
    // 1 Hz monotonic tick, milestone/zero detection) on top of the pure
    // persistence in DB.Timer.cs. Mirrors GiveawayService: a singleton over
    // DB.Instance, SafeEvent change events, one in-memory dictionary as the live
    // source of truth, checkpointed to SQLite so a subathon survives a Hub
    // restart.
    //
    // Two front-ends drive THIS one service: the timer.* script commands
    // (ScriptManager.Timer.cs) and the Hub Timer page (which reaches
    // TimerService.Instance directly). "One implementation, two front-ends."
    //
    // Pillar rule: this lives in the Hub runtime — the only process that touches
    // the DB and executes logic. Architect/Visualist never reference it.
    public sealed class TimerService
    {
        private readonly DB _db;

        public TimerService(DB db) => _db = db ?? throw new ArgumentNullException(nameof(db));

        // Shared instance over the singleton DB. Both front-ends resolve THIS
        // instance so they observe the same change events. Mirrors the
        // DB.Instance / GiveawayService.Instance double-checked-locking pattern.
        private static TimerService? _instance;
        private static readonly object _instanceGate = new();
        public static TimerService Instance
        {
            get
            {
                var i = _instance;
                if (i != null) return i;
                lock (_instanceGate) return _instance ??= new TimerService(DB.Instance);
            }
        }

        // ── Seams (wired from HubBootstrapper / ScriptManager; null-safe) ──
        /// <summary>
        /// The Overlay Live Channel this service publishes its <c>timer.*</c> keys into.
        /// Defaults to the process-wide store so production needs no wiring at all — the
        /// bespoke TIMER_UPDATE broadcast seam this replaced had to be assigned from
        /// HubBootstrapper before a single tick could reach an overlay.
        ///
        /// Public get / internal set mirrors <c>LayerRuntime.Registry</c>: production code
        /// cannot swap the channel at runtime, while the test assembly
        /// (<c>InternalsVisibleTo</c>) gives each test its OWN store. Sharing one store
        /// across tests is exactly the cross-test coupling the DB.Instance-backed suites
        /// already pay for.
        /// </summary>
        public OverlayLiveStore LiveStore { get; internal set; } = OverlayLiveStore.Instance;

        /// <summary>→ ScriptManager generic-event dispatch (Timer.OnZero/OnMilestone/OnAdd).</summary>
        public Func<string, IReadOnlyDictionary<string, string>, Task>? RaiseScriptEvent { get; set; }
        /// <summary>(busType, payloadJson) → Bus.Instance broadcast (TIMER_ADD / TIMER_ZERO / TIMER_MILESTONE).</summary>
        public Func<string, string, Task>? BusEmit { get; set; }
        /// <summary>Post one already-clipped line through the blessed Twitch chat send
        /// (ScriptManager.SendTwitchChatCore, which owns the connectivity + chat-action
        /// guards). Null-safe: unwired means the feedback layer posts nothing.</summary>
        public Func<string, Task>? SendChat { get; set; }
        /// <summary>Fire a visual trigger on every widget of a layer that owns the
        /// trigger — ScriptManager's SHARED fan-out, the same one Alerts / Loyalty /
        /// Soundboard ride. Args: (layerId, triggerName, eventData).</summary>
        public Func<string, string, Dictionary<string, string>, Task>? FireVisual { get; set; }

        // ── Change events (UI) ──
        /// <summary>Raised when the timer list / settings / run-state changes.</summary>
        public event EventHandler? TimersChanged;
        /// <summary>Raised with the affected slug every 1 Hz tick while a timer runs.</summary>
        public event EventHandler<string>? TimerTicked;

        private void RaiseTimers() => SafeEvent.Raise(TimersChanged, this, EventArgs.Empty, "TimerService", "TimersChanged");
        private void RaiseTicked(string slug) => SafeEvent.Raise(TimerTicked, this, slug, "TimerService", "TimerTicked");

        // ── Live state ──
        // In-memory dictionary keyed by slug (case-insensitive) is the authoritative
        // live source; the DB is a checkpoint. All mutation happens under _gate;
        // async DB writes + event/overlay fan-out happen OUTSIDE the lock.
        private readonly Dictionary<string, StreamTimer> _timers = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();
        private string? _defaultSlug;

        // Broadcaster-live tracking (StreamOnline/Offline). Default true so a
        // manually-started subathon counts immediately even if no StreamOnline
        // event was observed (streamer testing off-air); PauseWhenOffline only
        // freezes once we actually see a StreamOffline.
        private volatile bool _streamLive = true;

        // Monotonic clock for the tick delta — never wall clock, so an NTP step
        // or DST change can't jump the countdown.
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastTickMs;
        private long _lastCheckpointMs;
        private const long CheckpointIntervalMs = 5_000;

        // Tick loop lifecycle.
        private readonly object _loopGate = new();
        private bool _loopStarted;
        private CancellationTokenSource? _loopCts;
        private Task? _loopTask;

        private static string NowIso() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        private static long NowUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // JSON round-trip clone — self-contained so snapshots handed to the UI /
        // DB serializer are never the live mutable instance the tick is editing.
        private static readonly JsonSerializerOptions CloneOpts = new();
        private static StreamTimer CloneTimer(StreamTimer t)
            => JsonSerializer.Deserialize<StreamTimer>(JsonSerializer.Serialize(t, CloneOpts), CloneOpts)!;

        // ── Lifecycle ─────────────────────────────────────────────────────────
        /// <summary>Loads all persisted timers from the DB into memory.</summary>
        public async Task InitializeAsync()
        {
            List<StreamTimer> list;
            try { list = await _db.GetTimersAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                GlobalLogger.Error("TimerService", "InitializeAsync: load failed", ex);
                list = new List<StreamTimer>();
            }
            string? def = null;
            try { def = await _db.GetDefaultTimerSlugAsync().ConfigureAwait(false); }
            catch (Exception ex) { GlobalLogger.Error("TimerService", "InitializeAsync: default lookup failed", ex); }

            lock (_gate)
            {
                _timers.Clear();
                foreach (var t in list)
                    if (!string.IsNullOrEmpty(t.Slug))
                        _timers[t.Slug] = t;
                _defaultSlug = def is not null && _timers.ContainsKey(def) ? def : null;
                foreach (var t in _timers.Values)
                    t.IsDefault = _defaultSlug is not null && t.Slug.Equals(_defaultSlug, StringComparison.OrdinalIgnoreCase);
                _lastTickMs = _clock.ElapsedMilliseconds;
                _lastCheckpointMs = _clock.ElapsedMilliseconds;
            }
            GlobalLogger.Log($"TimerService initialised — {list.Count} timer(s) loaded.", "TimerService", LogLevel.System);
            RaiseTimers();
        }

        /// <summary>Starts the 1 Hz background tick loop. Idempotent.</summary>
        public void StartTicking()
        {
            lock (_loopGate)
            {
                if (_loopStarted) return;
                _loopStarted = true;
                _loopCts = new CancellationTokenSource();
                _lastTickMs = _clock.ElapsedMilliseconds;
                _lastCheckpointMs = _clock.ElapsedMilliseconds;
                var ct = _loopCts.Token;
                _loopTask = Task.Run(() => TickLoopAsync(ct));
            }
            GlobalLogger.Log("TimerService tick loop started (1 Hz).", "TimerService", LogLevel.System);
        }

        /// <summary>Stops the tick loop and checkpoints every timer to the DB.</summary>
        public async Task ShutdownAsync()
        {
            CancellationTokenSource? cts;
            Task? loop;
            lock (_loopGate)
            {
                cts = _loopCts; loop = _loopTask;
                _loopStarted = false; _loopCts = null; _loopTask = null;
            }
            try { cts?.Cancel(); } catch { /* best-effort */ }
            if (loop is not null)
            {
                try { await loop.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected */ }
                catch (Exception ex) { GlobalLogger.Error("TimerService", "tick loop drain failed", ex); }
            }
            cts?.Dispose();

            List<StreamTimer> all;
            lock (_gate) all = _timers.Values.Select(CloneTimer).ToList();
            foreach (var t in all)
            {
                try { await _db.UpsertTimerAsync(t).ConfigureAwait(false); }
                catch (Exception ex) { GlobalLogger.Error("TimerService", $"checkpoint '{t.Slug}' on shutdown failed", ex); }
            }
            GlobalLogger.Log("TimerService shut down — all timers checkpointed.", "TimerService", LogLevel.System);
        }

        /// <summary>Broadcaster live state, from *.StreamOnline / *.StreamOffline.</summary>
        public void SetStreamLive(bool live)
        {
            if (_streamLive == live) return;
            _streamLive = live;
            GlobalLogger.Log($"TimerService: stream {(live ? "ONLINE" : "OFFLINE")} — offline-pause timers {(live ? "resume" : "freeze")}.",
                "TimerService", LogLevel.System);
            RaiseTimers();
        }

        // ── The 1 Hz tick ─────────────────────────────────────────────────────
        private async Task TickLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                try { await TickOnceAsync().ConfigureAwait(false); }
                catch (Exception ex) { GlobalLogger.Error("TimerService", "tick failed", ex); }
            }
        }

        // internal, not private: TimerRunState.Ended is reachable ONLY from here, so
        // the test suite drives a single tick by hand to cover the post-zero paths
        // without starting the 1 Hz loop (Phoenix.Controls.Hub grants
        // InternalsVisibleTo to Phoenix.Controls.Tests — the same seam
        // LayerRegistry / ScriptManager already use). Production still calls it only
        // from TickLoopAsync.
        internal async Task TickOnceAsync()
        {
            // Beat counter for the feedback coalescer — a pending burst flushes on the
            // first beat that adds nothing new to it. Bumped here, drained at the very
            // bottom, so anything this tick appends waits for the NEXT beat.
            Interlocked.Increment(ref _tickSeq);

            long now = _clock.ElapsedMilliseconds;
            long delta = now - _lastTickMs;
            _lastTickMs = now;
            if (delta < 0) delta = 0;
            long nowUnix = NowUnixMs();
            bool doCheckpoint = now - _lastCheckpointMs >= CheckpointIntervalMs;
            if (doCheckpoint) _lastCheckpointMs = now;

            var tickedSlugs = new List<string>();
            var zeroFired = new List<StreamTimer>();
            var milestoneFired = new List<(StreamTimer Timer, TimerMilestone Milestone)>();
            var happyExpired = new List<StreamTimer>();
            var toCheckpoint = new List<StreamTimer>();
            bool anyStateChange = false;

            lock (_gate)
            {
                foreach (var t in _timers.Values)
                {
                    // Happy-Hour expiry (regardless of run state).
                    if (t.HappyHourEndsAtUnixMs != 0 && nowUnix >= t.HappyHourEndsAtUnixMs)
                    {
                        t.HappyHourEndsAtUnixMs = 0;
                        t.HappyHourMultiplier = 1.0;
                        t.UpdatedAtUnixMs = nowUnix;
                        happyExpired.Add(t);
                        anyStateChange = true;
                    }

                    if (t.State == TimerRunState.Running)
                    {
                        bool offlineGated = t.PauseWhenOffline && !_streamLive;
                        if (!offlineGated)
                        {
                            if (t.Mode == TimerMode.Stopwatch)
                            {
                                // Count UP — no zero, no cap. ElapsedMs is the
                                // authoritative readout for a stopwatch.
                                t.ElapsedMs += delta;
                                t.UpdatedAtUnixMs = nowUnix;
                            }
                            else if (t.RemainingMs > 0)
                            {
                                // Subathon / Countdown — count DOWN toward zero.
                                t.RemainingMs -= delta;
                                if (t.RemainingMs <= 0)
                                {
                                    t.RemainingMs = 0;
                                    t.State = TimerRunState.Ended;
                                    t.UpdatedAtUnixMs = nowUnix;
                                    zeroFired.Add(t);
                                    anyStateChange = true;
                                }
                            }
                            tickedSlugs.Add(t.Slug);
                        }
                    }

                    // Milestone crossings. Reached is the idempotency guard. Routed
                    // through the shared collector so the one rule — "the value this
                    // mode DISPLAYS has reached the target" — lives in exactly one
                    // place. A Stopwatch crosses here as its elapsed time grows; a
                    // Subathon/Countdown crosses when added time pushes the clock up.
                    foreach (var m in CollectMilestoneCrossingsLocked(t))
                    {
                        milestoneFired.Add((t, m));
                        anyStateChange = true;
                    }

                    // Periodic checkpoint of running timers (bounds crash loss to
                    // one interval); ended/expired timers are checkpointed too.
                    if (doCheckpoint && (t.State == TimerRunState.Running || zeroFired.Contains(t) || happyExpired.Contains(t)))
                        toCheckpoint.Add(t);
                }

                // Ensure just-ended / expired timers persist even off a checkpoint beat.
                foreach (var t in zeroFired) if (!toCheckpoint.Contains(t)) toCheckpoint.Add(t);
                foreach (var t in happyExpired) if (!toCheckpoint.Contains(t)) toCheckpoint.Add(t);
            }

            // ── Fan-out (outside the lock) ──
            // The 1 Hz live-channel publish is what drives the overlay countdown
            // visually. Unconditional even on an empty snapshot: the only key that
            // publishes with no timers is timer.__default.slug, and the contract wants
            // it PRESENT-and-empty ("no default timer") rather than Missing. Coalescing
            // makes the repeat cost zero wire bytes.
            //
            // Routed through PublishLiveChannelNow — which snapshots under the publish
            // gate — rather than carrying a snapshot taken above out of the tick lock:
            // a mutator publishing concurrently would otherwise be able to land its
            // NEWER snapshot first and leave this older one painted for a whole second.
            PublishLiveChannelNow();

            foreach (var slug in tickedSlugs) RaiseTicked(slug);

            foreach (var t in happyExpired)
            {
                await LogActivityAsync(t.Slug, "INF", "happy hour ended — multiplier reset to 1.0").ConfigureAwait(false);
            }

            foreach (var (t, m) in milestoneFired)
                await FireMilestoneAsync(t, m).ConfigureAwait(false);

            foreach (var t in zeroFired)
                await FireZeroAsync(t).ConfigureAwait(false);

            foreach (var t in toCheckpoint)
                await CheckpointAsync(t.Slug).ConfigureAwait(false);

            await DrainFeedbackAsync().ConfigureAwait(false);

            if (anyStateChange) RaiseTimers();
        }

        // ── CRUD ──────────────────────────────────────────────────────────────
        public async Task<StreamTimer> CreateAsync(string name, TimerMode mode = TimerMode.Subathon)
        {
            name = string.IsNullOrWhiteSpace(name) ? "Untitled timer" : name.Trim();
            StreamTimer t;
            bool firstTimer;
            lock (_gate)
            {
                string slug = GenerateSlugLocked();
                t = new StreamTimer
                {
                    Slug = slug,
                    Name = name,
                    Mode = mode,
                    State = TimerRunState.Stopped,
                    UpdatedAtUnixMs = NowUnixMs(),
                };
                // Seed the display to a sensible pre-Start value: a stopwatch reads
                // 0 elapsed; a countdown/subathon reads its configured start duration.
                if (mode == TimerMode.Stopwatch) t.ElapsedMs = 0;
                else t.RemainingMs = t.StartDurationMs;
                _timers[slug] = t;
                firstTimer = _timers.Count == 1;
                if (firstTimer) { _defaultSlug = slug; t.IsDefault = true; }
            }
            await CheckpointAsync(t.Slug).ConfigureAwait(false);
            if (firstTimer)
            {
                try { await _db.SetDefaultTimerAsync(t.Slug).ConfigureAwait(false); }
                catch (Exception ex) { GlobalLogger.Error("TimerService", "CreateAsync: set-default failed", ex); }
            }
            await LogActivityAsync(t.Slug, "INF", $"timer created — \"{name}\"").ConfigureAwait(false);
            RaiseTimers();
            return CloneTimerFor(t.Slug) ?? CloneTimer(t);
        }

        public async Task DeleteAsync(string slug)
        {
            string? resolved = ResolveSlug(slug);
            if (resolved is null) return;
            bool wasDefault;
            lock (_gate)
            {
                _timers.Remove(resolved);
                wasDefault = string.Equals(_defaultSlug, resolved, StringComparison.OrdinalIgnoreCase);
                if (wasDefault) _defaultSlug = _timers.Keys.FirstOrDefault();
                if (_defaultSlug is not null && _timers.TryGetValue(_defaultSlug, out var nd)) nd.IsDefault = true;
            }
            try { await _db.DeleteTimerAsync(resolved).ConfigureAwait(false); }
            catch (Exception ex) { GlobalLogger.Error("TimerService", $"DeleteAsync('{resolved}') failed", ex); }
            if (wasDefault && _defaultSlug is not null)
            {
                try { await _db.SetDefaultTimerAsync(_defaultSlug).ConfigureAwait(false); } catch { /* best-effort */ }
            }
            RaiseTimers();
        }

        public async Task SetDefaultAsync(string slug)
        {
            string? resolved = ResolveSlug(slug);
            if (resolved is null) return;
            lock (_gate)
            {
                _defaultSlug = resolved;
                foreach (var t in _timers.Values)
                    t.IsDefault = t.Slug.Equals(resolved, StringComparison.OrdinalIgnoreCase);
            }
            try { await _db.SetDefaultTimerAsync(resolved).ConfigureAwait(false); }
            catch (Exception ex) { GlobalLogger.Error("TimerService", $"SetDefaultAsync('{resolved}') failed", ex); }
            await LogActivityAsync(resolved, "INF", "marked as default timer").ConfigureAwait(false);
            RaiseTimers();
        }

        public Task<List<StreamTimer>> ListAsync()
        {
            lock (_gate) return Task.FromResult(_timers.Values.Select(CloneTimer).ToList());
        }

        public Task<StreamTimer?> GetAsync(string slug)
            => Task.FromResult(CloneTimerFor(ResolveSlug(slug) ?? ""));

        public string? GetDefaultSlug()
        {
            lock (_gate)
            {
                if (_defaultSlug is not null && _timers.ContainsKey(_defaultSlug)) return _defaultSlug;
                return _timers.Keys.FirstOrDefault();
            }
        }

        public async Task<List<(string Time, string Kind, string Message)>> GetActivityAsync(string slug)
        {
            string? resolved = ResolveSlug(slug);
            if (resolved is null) return new List<(string, string, string)>();
            try { return await _db.GetTimerActivityAsync(resolved).ConfigureAwait(false); }
            catch (Exception ex)
            {
                GlobalLogger.Error("TimerService", $"GetActivityAsync('{resolved}') failed", ex);
                return new List<(string, string, string)>();
            }
        }

        // ── Control ─────────────────────────────────────────────────────────
        public async Task StartAsync(string slug, long? durationMs = null)
        {
            string? resolved;
            long displayMs = 0;
            bool stopwatch = false;
            lock (_gate)
            {
                resolved = ResolveSlugLocked(slug);
                if (resolved is null) return;
                var t = _timers[resolved];
                if (t.Mode == TimerMode.Stopwatch)
                {
                    // Start (or restart) counting UP from zero. Duration is N/A.
                    // Re-seed like the countdown arm below: milestones evaluate
                    // against the mode-aware DisplayMs, so every ElapsedMs mutator
                    // owes the same re-seed the RemainingMs mutators carry — here
                    // it is what gives a stopwatch per-RUN goals (a fresh Start
                    // re-arms previously-fired ones instead of leaving them lit
                    // and unable to ever fire again).
                    t.ElapsedMs = 0;
                    ReseedMilestonesLocked(t);
                    stopwatch = true;
                }
                else
                {
                    // Countdown / Subathon — (re)start the down-count. Empty
                    // duration falls back to the configured StartDurationMs (the
                    // "Default Time"); the Architect Timer.Start Duration socket
                    // supplies durationMs when the graph provides one.
                    t.RemainingMs = durationMs ?? t.StartDurationMs;
                    if (t.MaxCapMs > 0 && t.RemainingMs > t.MaxCapMs) t.RemainingMs = t.MaxCapMs;
                    ReseedMilestonesLocked(t);
                }
                t.State = TimerRunState.Running;
                t.TotalAddedMs = 0;
                t.UpdatedAtUnixMs = NowUnixMs();
                displayMs = DisplayMs(t);
            }
            await CheckpointAsync(resolved).ConfigureAwait(false);
            await LogActivityAsync(resolved, "CTRL",
                stopwatch ? "stopwatch started" : $"started at {FormatDuration(displayMs, "clock")}").ConfigureAwait(false);
            RaiseTimers();
            PublishLiveChannelNow();
        }

        /// <summary>
        /// Starts a timer FROM THE CLOCK IT CURRENTLY SHOWS — the Hub Timer panel's
        /// START button.
        /// </summary>
        /// <remarks>
        /// <para><see cref="StartAsync"/> keeps its unconditional re-arm on purpose: a
        /// null duration there means "use the timer's configured start duration", which
        /// is the documented contract of the Architect <c>Timer.Start</c> node's empty
        /// Duration socket and what the canonical "on go-live, restart the timer" graph
        /// relies on. Re-meaning it would silently change every shipped graph.</para>
        /// <para>The PANEL button carries no such contract, and routing it through that
        /// re-arm made the ADJUST strip's <c>= SET</c> unusable: the value SET had just
        /// written was destroyed by the very next click, so a fresh Countdown snapped
        /// back to its 4 h default. It also zeroed <c>TotalAddedMs</c> — and the panel
        /// enables START while PAUSED, one button-width from RESUME, so a mis-click
        /// erased a whole subathon's accrual with no confirm and an immediate
        /// checkpoint.</para>
        /// <para>The rule here is <see cref="ToggleAsync"/>'s: re-arm to
        /// <c>StartDurationMs</c> only when a count-DOWN timer has run dry; otherwise
        /// continue from whatever is on the clock. A Stopwatch's "current clock" is
        /// <c>ElapsedMs</c>, so it continues too — RESET is the button that puts a
        /// stopwatch back to zero.</para>
        /// </remarks>
        public async Task StartFromCurrentAsync(string slug)
        {
            string? resolved;
            long displayMs = 0;
            bool stopwatch;
            bool reArmed = false;
            StreamTimer? zeroed;
            lock (_gate)
            {
                resolved = ResolveSlugLocked(slug);
                if (resolved is null) return;
                var t = _timers[resolved];
                stopwatch = t.Mode == TimerMode.Stopwatch;

                if (!stopwatch && t.RemainingMs <= 0)
                {
                    // The ONE re-arm branch: a count-down timer with nothing left to
                    // count. This is a fresh run, so it performs the same three writes
                    // ToggleAsync's re-arm branch and ResetAsync do — TotalAddedMs
                    // measures THIS run's accrual and the badges belong to it, so both
                    // reset with the clock.
                    t.RemainingMs = t.StartDurationMs;
                    if (t.MaxCapMs > 0 && t.RemainingMs > t.MaxCapMs) t.RemainingMs = t.MaxCapMs;
                    t.TotalAddedMs = 0;
                    ReseedMilestonesLocked(t);
                    reArmed = true;
                }
                else if (!stopwatch && t.MaxCapMs > 0 && t.RemainingMs > t.MaxCapMs)
                {
                    // The continue branch still owes StartAsync's cap clamp: a cap
                    // edited below the clock (or a Toggle re-arm, which does not clamp)
                    // must bite on the next start rather than only on the next add.
                    // This LOWERS the display value, so it carries the re-seed every
                    // other downward mutator carries.
                    t.RemainingMs = t.MaxCapMs;
                    ReseedMilestonesLocked(t);
                }

                // DELIBERATELY absent from the continue branch: TotalAddedMs = 0 and the
                // milestone re-seed. Continuing means resuming the run already on the
                // clock — wiping TOTAL ADDED and darkening badges the stream has already
                // earned would be a new instance of exactly the surprise this method
                // exists to remove. A re-seed is owed only by a mutator that MOVES the
                // displayed value, and this branch does not move it.

                t.State = TimerRunState.Running;
                t.UpdatedAtUnixMs = NowUnixMs();

                // A zero clock must never park as Running-at-00:00:00: the tick's
                // decrement is gated on RemainingMs > 0, so nothing would ever move it
                // and Timer.OnZero could never fire (the hole StartAsync still has).
                // Reachable here when StartDurationMs is itself 0.
                zeroed = EndIfZeroLocked(t) ? t : null;
                displayMs = DisplayMs(t);
            }
            await CheckpointAsync(resolved).ConfigureAwait(false);
            await LogActivityAsync(resolved, "CTRL",
                reArmed || stopwatch
                    ? $"started at {FormatDuration(displayMs, "clock")}"
                    : $"started from {FormatDuration(displayMs, "clock")}").ConfigureAwait(false);
            if (zeroed is not null) await FireZeroAsync(zeroed).ConfigureAwait(false);
            RaiseTimers();
            PublishLiveChannelNow();
        }

        public Task StopAsync(string slug) => SetStateAsync(slug, TimerRunState.Stopped, "stopped");
        public Task PauseAsync(string slug) => SetStateAsync(slug, TimerRunState.Paused, "paused", onlyFrom: TimerRunState.Running);

        public async Task ResumeAsync(string slug)
        {
            string? resolved;
            lock (_gate)
            {
                resolved = ResolveSlugLocked(slug);
                if (resolved is null) return;
                var t = _timers[resolved];
                // A stopwatch has no "remaining" — it resumes counting up from
                // wherever ElapsedMs stands; a countdown resumes only if it still
                // has time left on the clock. Ended belongs in that set: once an
                // +ADD (or a manual set) has put time back on the clock the timer is
                // runnable again, and excluding it meant Resume silently no-opped on
                // a subathon that had run dry — the tick gate skips Ended forever, so
                // only Toggle could recover it.
                bool resumable = t.State is TimerRunState.Paused or TimerRunState.Stopped or TimerRunState.Ended
                                 && (t.Mode == TimerMode.Stopwatch || t.RemainingMs > 0);
                if (resumable)
                {
                    t.State = TimerRunState.Running;
                    t.UpdatedAtUnixMs = NowUnixMs();
                }
                else return;
            }
            await CheckpointAsync(resolved).ConfigureAwait(false);
            await LogActivityAsync(resolved, "CTRL", "resumed").ConfigureAwait(false);
            RaiseTimers();
            PublishLiveChannelNow();
        }

        public async Task ToggleAsync(string slug)
        {
            string? resolved;
            bool nowRunning;
            lock (_gate)
            {
                resolved = ResolveSlugLocked(slug);
                if (resolved is null) return;
                var t = _timers[resolved];
                if (t.State == TimerRunState.Running)
                {
                    t.State = TimerRunState.Paused;
                    nowRunning = false;
                }
                else
                {
                    // Any non-Running state (Stopped / Paused / Ended) runs again.
                    // A countdown that has run dry re-arms to its start duration;
                    // one that still has time — an Ended timer someone added to —
                    // simply continues, which is exactly what ResumeAsync now does
                    // for the same state. A stopwatch resumes counting up from ElapsedMs.
                    if (t.Mode != TimerMode.Stopwatch && t.RemainingMs <= 0)
                    {
                        t.RemainingMs = t.StartDurationMs;
                        t.TotalAddedMs = 0;
                        ReseedMilestonesLocked(t);
                    }
                    t.State = TimerRunState.Running;
                    nowRunning = true;
                }
                t.UpdatedAtUnixMs = NowUnixMs();
            }
            await CheckpointAsync(resolved).ConfigureAwait(false);
            await LogActivityAsync(resolved, "CTRL", nowRunning ? "resumed" : "paused").ConfigureAwait(false);
            RaiseTimers();
            PublishLiveChannelNow();
        }

        public async Task ResetAsync(string slug)
        {
            string? resolved;
            long displayMs = 0;
            lock (_gate)
            {
                resolved = ResolveSlugLocked(slug);
                if (resolved is null) return;
                var t = _timers[resolved];
                if (t.Mode == TimerMode.Stopwatch)
                {
                    t.ElapsedMs = 0;   // stopwatch resets its count-up to zero
                    ReseedMilestonesLocked(t);   // reached badges re-arm with the run
                }
                else
                {
                    t.RemainingMs = t.StartDurationMs;
                    ReseedMilestonesLocked(t);
                }
                t.TotalAddedMs = 0;
                t.State = TimerRunState.Stopped;
                t.UpdatedAtUnixMs = NowUnixMs();
                displayMs = DisplayMs(t);
            }
            await CheckpointAsync(resolved).ConfigureAwait(false);
            await LogActivityAsync(resolved, "CTRL", $"reset to {FormatDuration(displayMs, "clock")}").ConfigureAwait(false);
            RaiseTimers();
            PublishLiveChannelNow();
        }

        public async Task AddMsAsync(string slug, long ms, string source = "manual")
        {
            if (ms == 0) return;
            if (ms < 0)
            {
                // A negative add IS a subtraction — route it through SubtractMsAsync
                // so the two are indistinguishable (the signed timer.add script
                // command already sends its negative amounts there). The inline
                // negative path this replaces was a half-copy: it clamped and fired
                // the zero, but skipped the milestone re-seed and the TotalAddedMs
                // wind-back SubtractMsAsync performs, so a direct negative add and
                // the panel's -SUB left different state behind. (long.MinValue has
                // no positive twin — saturate rather than overflow on negation.)
                await SubtractMsAsync(slug, ms == long.MinValue ? long.MaxValue : -ms).ConfigureAwait(false);
                return;
            }
            bool manual = string.Equals(source, "manual", StringComparison.OrdinalIgnoreCase);
            string? resolved;
            long actual;
            bool reArmed;
            List<TimerMilestone> crossed;
            lock (_gate)
            {
                resolved = ResolveSlugLocked(slug);
                if (resolved is null) return;
                var t = _timers[resolved];
                long add = ms;
                if (!manual && t.PerAddCapMs > 0 && add > t.PerAddCapMs) add = t.PerAddCapMs;
                if (t.Mode == TimerMode.Stopwatch)
                {
                    // A stopwatch counts UP, so "add time" moves ElapsedMs — the same
                    // field SetTimeMsAsync edits. RemainingMs is dead state here: the
                    // pre-fix unconditional write changed nothing on screen while
                    // poisoning every RemainingMs reader (progress, milestones,
                    // {timer.remaining}). Max cap is a countdown ceiling and never
                    // applies to a stopwatch.
                    long beforeElapsed = t.ElapsedMs;
                    t.ElapsedMs += add;
                    if (t.ElapsedMs < 0) t.ElapsedMs = 0;
                    actual = t.ElapsedMs - beforeElapsed;
                }
                else
                {
                    long before = t.RemainingMs;
                    t.RemainingMs += add;
                    if (t.RemainingMs < 0) t.RemainingMs = 0;
                    if (t.MaxCapMs > 0 && t.RemainingMs > t.MaxCapMs) t.RemainingMs = t.MaxCapMs;
                    actual = t.RemainingMs - before;
                }
                if (actual > 0) t.TotalAddedMs += actual;
                t.UpdatedAtUnixMs = NowUnixMs();
                reArmed = actual > 0 && ReArmIfEndedLocked(t);
                crossed = CollectMilestoneCrossingsLocked(t);
                // A POSITIVE add can still move the display DOWN — a max cap
                // edited below the running clock clamps RemainingMs past the
                // starting value (see the sign note below). That is a downward
                // mutation like SUB, so the same stale-badge rule applies:
                // re-seed so goals above the clamped clock read unreached again.
                if (actual < 0) ReseedMilestonesLocked(t);
            }
            if (actual != 0)
            {
                await CheckpointAsync(resolved).ConfigureAwait(false);
                // actual can still come back negative on a POSITIVE add: a max cap
                // lowered below the current clock clamps RemainingMs down past the
                // starting value. The clamp target is the (positive) cap, never
                // zero, so the zero transition stays SubtractMsAsync's alone.
                string sign = actual > 0 ? "+" : "-";
                await LogActivityAsync(resolved, "ADD",
                    $"{sign}{FormatDuration(Math.Abs(actual), "clock")} · {source}").ConfigureAwait(false);
                if (reArmed)
                    await LogActivityAsync(resolved, "CTRL", "re-armed — running again after time was added past zero").ConfigureAwait(false);
                if (actual > 0) await FireAddAsync(resolved, actual, source).ConfigureAwait(false);
                foreach (var m in crossed) await FireMilestoneAsync(GetLive(resolved), m).ConfigureAwait(false);
                RaiseTimers();
                PublishLiveChannelNow();
            }
        }

        public async Task SubtractMsAsync(string slug, long ms)
        {
            if (ms <= 0) return;
            string? resolved;
            long actual;
            StreamTimer? zeroed;
            lock (_gate)
            {
                resolved = ResolveSlugLocked(slug);
                if (resolved is null) return;
                var t = _timers[resolved];
                if (t.Mode == TimerMode.Stopwatch)
                {
                    // Count-UP mirror of the add path: -SUB winds ElapsedMs back,
                    // clamped at zero. Writing RemainingMs here was invisible on the
                    // display and polluted every RemainingMs reader.
                    long beforeElapsed = t.ElapsedMs;
                    t.ElapsedMs -= ms;
                    if (t.ElapsedMs < 0) t.ElapsedMs = 0;
                    actual = beforeElapsed - t.ElapsedMs;

                    // SUB lowers the DISPLAY value in this mode too, so a goal
                    // above the new level is no longer reached — same rule the
                    // countdown arm below applies. Without it a wound-back
                    // stopwatch kept its fired badges lit at a level it never
                    // stood at any more.
                    ReseedMilestonesLocked(t);
                }
                else
                {
                    long before = t.RemainingMs;
                    t.RemainingMs -= ms;
                    if (t.RemainingMs < 0) t.RemainingMs = 0;
                    actual = before - t.RemainingMs;

                    // SUB lowers the clock, so a milestone above the new level is no
                    // longer reached. SetTimeMsAsync re-seeds for exactly this reason;
                    // SUB was the only RemainingMs mutator that did not, which left
                    // badges and the MILESTONES x/y tile reading stale-high depending
                    // on which button happened to be clicked last.
                    ReseedMilestonesLocked(t);

                    // Symmetry with AddMsAsync, which does `t.TotalAddedMs += actual`.
                    // Without this the TOTAL ADDED tile only ever grows: ADD 5m then
                    // SUB 5m left the panel claiming 5m had been added. Floored at
                    // zero so a subtract can never drive the stat negative.
                    if (actual > 0)
                        t.TotalAddedMs = Math.Max(0, t.TotalAddedMs - actual);
                }
                t.UpdatedAtUnixMs = NowUnixMs();
                zeroed = EndIfZeroLocked(t) ? t : null;
            }
            if (actual != 0 || zeroed is not null)
            {
                await CheckpointAsync(resolved).ConfigureAwait(false);
                if (actual != 0)
                    await LogActivityAsync(resolved, "ADD", $"-{FormatDuration(actual, "clock")} · manual").ConfigureAwait(false);
                if (zeroed is not null) await FireZeroAsync(zeroed).ConfigureAwait(false);
                RaiseTimers();
                PublishLiveChannelNow();
            }
            else
            {
                // Never silently swallow the click. A no-op subtract is legitimate
                // (already at zero), but the streamer pressed a button and deserves
                // to see why nothing moved. GlobalLogger rather than a dialog: this
                // is a repeatable rejection, not an irreversible action.
                GlobalLogger.Log(
                    $"Subtract had no effect on '{resolved}' — already at zero.",
                    "TimerService", LogLevel.System);
            }
        }

        public async Task SetTimeMsAsync(string slug, long ms)
        {
            if (ms < 0) ms = 0;
            string? resolved;
            long val;
            StreamTimer? zeroed;
            lock (_gate)
            {
                resolved = ResolveSlugLocked(slug);
                if (resolved is null) return;
                var t = _timers[resolved];
                if (t.Mode == TimerMode.Stopwatch)
                {
                    // "Edit" a stopwatch = set its elapsed count-up value. Re-seed,
                    // never collect: a manual set is not a crossing here either, so
                    // a set PAST an unreached goal marks it reached WITHOUT raising
                    // Timer.OnMilestone, and a set below a fired one re-arms it.
                    t.ElapsedMs = ms;
                    ReseedMilestonesLocked(t);
                }
                else
                {
                    t.RemainingMs = (t.MaxCapMs > 0 && ms > t.MaxCapMs) ? t.MaxCapMs : ms;

                    // (The park-at-zero end-transition is handled uniformly below by
                    // EndIfZeroLocked. Reaching zero FIRES Timer.OnZero no matter how
                    // the clock got there — the decided rule is one sentence, and a
                    // manual SET-0 counts exactly like a tick crossing or a SUB
                    // landing on zero.)
                    // Authoritative set: re-seed the milestone Reached flags to the new
                    // level without firing (a manual set is not a subathon "crossing").
                    ReseedMilestonesLocked(t);
                }
                t.UpdatedAtUnixMs = NowUnixMs();
                // SET deliberately does NOT re-arm an Ended countdown, even though
                // AddMsAsync does. The asymmetry is the design, not an oversight: a set is
                // authoritative about the CLOCK and says nothing about whether the timer
                // should be running, so the state is left alone and Resume (or START) is
                // what picks it back up — pinned with that rationale by
                // TimerServiceTests.Ended_Timer_Is_Resumable_Once_It_Has_Time_Again.
                // An add is the opposite: time arriving from a sub or a cheer is exactly
                // the event that should revive a subathon that just ran dry.
                // The complaint that made this look like a bug — "SET then START threw my
                // value away" — was never SET's half. It was StartAsync clobbering the
                // clock, and StartFromCurrentAsync fixes it there.
                zeroed = EndIfZeroLocked(t) ? t : null;
                val = DisplayMs(t);
            }
            await CheckpointAsync(resolved).ConfigureAwait(false);
            await LogActivityAsync(resolved, "CTRL", $"set to {FormatDuration(val, "clock")}").ConfigureAwait(false);
            if (zeroed is not null) await FireZeroAsync(zeroed).ConfigureAwait(false);
            RaiseTimers();
            PublishLiveChannelNow();
        }

        public async Task SetHappyHourAsync(string slug, double multiplier, long durationMs, string scope)
        {
            if (double.IsNaN(multiplier) || double.IsInfinity(multiplier) || multiplier <= 0) multiplier = 1.0;
            scope = NormalizeScope(scope);
            string? resolved;
            lock (_gate)
            {
                resolved = ResolveSlugLocked(slug);
                if (resolved is null) return;
                var t = _timers[resolved];
                t.HappyHourMultiplier = multiplier;
                t.HappyHourScope = scope;
                t.HappyHourEndsAtUnixMs = (multiplier > 1.0 && durationMs > 0) ? NowUnixMs() + durationMs : 0;
                if (t.HappyHourEndsAtUnixMs == 0) t.HappyHourMultiplier = 1.0;
                t.UpdatedAtUnixMs = NowUnixMs();
            }
            await CheckpointAsync(resolved).ConfigureAwait(false);
            await LogActivityAsync(resolved, "INF",
                multiplier > 1.0 && durationMs > 0
                    ? $"happy hour ×{multiplier.ToString("0.##", CultureInfo.InvariantCulture)} ({scope}) for {FormatDuration(durationMs, "clock")}"
                    : "happy hour cleared").ConfigureAwait(false);
            RaiseTimers();
        }

        // ── Config setters ────────────────────────────────────────────────────
        public async Task SetActionConfigAsync(string slug, TimerActionConfig cfg)
        {
            if (cfg is null) return;
            // Numeric-range sanity only (no unrequested validation): negative
            // "seconds added" is nonsensical — clamp to 0 (= that action off).
            cfg.SubT1Seconds = Math.Max(0, cfg.SubT1Seconds);
            cfg.SubT2Seconds = Math.Max(0, cfg.SubT2Seconds);
            cfg.SubT3Seconds = Math.Max(0, cfg.SubT3Seconds);
            cfg.SubPrimeSeconds = Math.Max(0, cfg.SubPrimeSeconds);
            cfg.BitsPer100Seconds = Math.Max(0, cfg.BitsPer100Seconds);
            cfg.TipPerUnitSeconds = Math.Max(0, cfg.TipPerUnitSeconds);
            cfg.FollowSeconds = Math.Max(0, cfg.FollowSeconds);
            cfg.RaidPerViewerSeconds = Math.Max(0, cfg.RaidPerViewerSeconds);
            string? resolved;
            lock (_gate)
            {
                resolved = ResolveSlugLocked(slug);
                if (resolved is null) return;
                _timers[resolved].Actions = cfg;
                _timers[resolved].UpdatedAtUnixMs = NowUnixMs();
            }
            await CheckpointAsync(resolved).ConfigureAwait(false);
            RaiseTimers();
        }

        /// <summary>
        /// Replaces the whole per-timer feedback config — the panel's single write path,
        /// mirroring <see cref="SetActionConfigAsync"/>. The caller owns the object it
        /// hands over (the panel builds a fresh one per commit).
        /// </summary>
        public async Task SetFeedbackAsync(string slug, TimerFeedbackSettings cfg)
        {
            if (cfg is null) return;
            // Numeric-range sanity only (no unrequested validation): a negative
            // "minimum seconds" is nonsensical — clamp to 0 (= announce every add).
            cfg.AddMinSeconds = Math.Max(0, cfg.AddMinSeconds);
            cfg.Zero ??= new TimerFeedbackConfig();
            cfg.Milestone ??= new TimerFeedbackConfig();
            cfg.Add ??= new TimerFeedbackConfig();
            string? resolved;
            lock (_gate)
            {
                resolved = ResolveSlugLocked(slug);
                if (resolved is null) return;
                _timers[resolved].Feedback = cfg;
                _timers[resolved].UpdatedAtUnixMs = NowUnixMs();
            }
            await CheckpointAsync(resolved).ConfigureAwait(false);
            RaiseTimers();
        }

        /// <summary>
        /// Sets one milestone's feedback overrides. Deliberately touches neither Label,
        /// Target nor Reached, so — unlike <see cref="SetMilestoneAsync"/> — it cannot
        /// re-seed or fire anything: editing the text a goal announces is not a crossing.
        /// </summary>
        public async Task SetMilestoneFeedbackAsync(
            string slug, string milestoneId, string message, string layerId, string triggerName)
        {
            if (string.IsNullOrEmpty(milestoneId)) return;
            string? resolved;
            lock (_gate)
            {
                resolved = ResolveSlugLocked(slug);
                if (resolved is null) return;
                var m = _timers[resolved].Milestones.Find(x => x.Id == milestoneId);
                if (m is null) return;
                m.Message = message ?? "";
                m.LayerId = layerId ?? "";
                m.TriggerName = triggerName ?? "";
                _timers[resolved].UpdatedAtUnixMs = NowUnixMs();
            }
            await CheckpointAsync(resolved).ConfigureAwait(false);
            RaiseTimers();
        }

        public Task SetStartDurationAsync(string slug, long ms) => MutateFieldAsync(slug, t => t.StartDurationMs = Math.Max(0, ms));
        public Task SetMaxCapAsync(string slug, long ms) => MutateFieldAsync(slug, t =>
        {
            t.MaxCapMs = Math.Max(0, ms);
            if (t.MaxCapMs > 0 && t.RemainingMs > t.MaxCapMs) t.RemainingMs = t.MaxCapMs;
        });
        public Task SetPerAddCapAsync(string slug, long ms) => MutateFieldAsync(slug, t => t.PerAddCapMs = Math.Max(0, ms));
        public Task SetPauseWhenOfflineAsync(string slug, bool value) => MutateFieldAsync(slug, t => t.PauseWhenOffline = value);

        public async Task<TimerMilestone> AddMilestoneAsync(string slug, string label, long targetSeconds)
        {
            var milestone = new TimerMilestone
            {
                Id = Guid.NewGuid().ToString("N"),
                Label = label ?? "",
                TargetSeconds = Math.Max(0, targetSeconds),
            };
            string? resolved;
            lock (_gate)
            {
                resolved = ResolveSlugLocked(slug);
                if (resolved is null) return milestone;
                var t = _timers[resolved];
                // Pre-satisfy against the value this timer actually DISPLAYS so a
                // goal added below the current reading doesn't instantly fire.
                // Mode-aware: RemainingMs is 0 on a Stopwatch, so the old form marked
                // every stopwatch goal pre-satisfied at creation — born lit, and then
                // unable to fire because the collector bailed on Stopwatch too.
                milestone.Reached = DisplayMs(t) >= milestone.TargetSeconds * 1000L;
                t.Milestones.Add(milestone);
                t.UpdatedAtUnixMs = NowUnixMs();
            }
            await CheckpointAsync(resolved).ConfigureAwait(false);
            RaiseTimers();
            return CloneMilestone(milestone);
        }

        public async Task RemoveMilestoneAsync(string slug, string milestoneId)
        {
            if (string.IsNullOrEmpty(milestoneId)) return;
            string? resolved;
            lock (_gate)
            {
                resolved = ResolveSlugLocked(slug);
                if (resolved is null) return;
                var t = _timers[resolved];
                t.Milestones.RemoveAll(m => m.Id == milestoneId);
                t.UpdatedAtUnixMs = NowUnixMs();
            }
            await CheckpointAsync(resolved).ConfigureAwait(false);
            RaiseTimers();
        }

        /// <summary>
        /// Edits an existing milestone's label and target. Returns the updated
        /// milestone, or null when the timer or the milestone is gone.
        ///
        /// Until this existed there was NO way to change a goal once created: the row
        /// rendered its target as a read-only TextBlock and the service exposed only
        /// Add and Remove, so a mistyped time meant deleting and re-adding. That is
        /// what "the textpill can not be edited" was — a missing capability, not a
        /// broken control.
        /// </summary>
        /// <remarks>
        /// Reached is RE-SEEDED against the new target and deliberately does NOT fire.
        /// Raising a target above the current reading would otherwise leave a goal
        /// marked reached; lowering one under it would let the next tick raise
        /// Timer.OnMilestone for a goal the streamer merely re-typed — an event their
        /// scripts would treat as a real crossing. Same pre-satisfy rule as
        /// AddMilestoneAsync and ReseedMilestonesLocked, and the same reasoning
        /// SetTimeMsAsync already applies ("a manual set is not a subathon crossing").
        /// </remarks>
        public async Task<TimerMilestone?> SetMilestoneAsync(
            string slug, string milestoneId, string label, long targetSeconds)
        {
            if (string.IsNullOrEmpty(milestoneId)) return null;

            string? resolved;
            TimerMilestone snapshot;
            lock (_gate)
            {
                resolved = ResolveSlugLocked(slug);
                if (resolved is null) return null;
                var t = _timers[resolved];

                var m = t.Milestones.Find(x => x.Id == milestoneId);
                if (m is null) return null;

                m.Label = label ?? "";
                m.TargetSeconds = Math.Max(0, targetSeconds);
                m.Reached = DisplayMs(t) >= m.TargetSeconds * 1000L;

                t.UpdatedAtUnixMs = NowUnixMs();
                snapshot = CloneMilestone(m);
            }

            await CheckpointAsync(resolved).ConfigureAwait(false);
            RaiseTimers();
            return snapshot;
        }

        // ── Reads (in-memory) ──────────────────────────────────────────────────
        /// <summary>
        /// The mode-aware "current time" of a timer in ms: elapsed for a Stopwatch,
        /// remaining for a Countdown/Subathon. This is the value the overlay and the
        /// Architect Timer.Get* nodes read, so a stopwatch reads its count-up and a
        /// countdown reads its count-down through the same accessors.
        /// </summary>
        private static long DisplayMs(StreamTimer t)
            => t.Mode == TimerMode.Stopwatch ? t.ElapsedMs : t.RemainingMs;

        /// <summary>
        /// Mode-aware progress 0..1 — the DISPLAY value (elapsed for a Stopwatch,
        /// remaining for a Countdown/Subathon) measured against the max cap; 0 when
        /// no cap is configured. Single source for both the Timer.GetProgress node
        /// and the <c>timer.&lt;root&gt;.progress</c> live-channel key; both read raw
        /// RemainingMs before, so a progress widget bound to a Stopwatch tracked a
        /// field the stopwatch never displays.
        /// </summary>
        private static double ProgressOf(StreamTimer t)
            => t.MaxCapMs > 0 ? Math.Clamp((double)DisplayMs(t) / t.MaxCapMs, 0d, 1d) : 0d;

        public long GetRemainingMs(string slug)
        {
            lock (_gate) { var t = GetLocked(slug); return t is null ? 0 : DisplayMs(t); }
        }

        public TimerRunState GetState(string slug)
        {
            lock (_gate) { var t = GetLocked(slug); return t?.State ?? TimerRunState.Stopped; }
        }

        public bool GetPaused(string slug)
        {
            lock (_gate) { var t = GetLocked(slug); return t?.State == TimerRunState.Paused; }
        }

        public double GetProgress(string slug)
        {
            lock (_gate)
            {
                var t = GetLocked(slug);
                return t is null ? 0 : ProgressOf(t);
            }
        }

        public string GetFormatted(string slug, string style)
        {
            long ms;
            lock (_gate) { var t = GetLocked(slug); ms = t is null ? 0 : DisplayMs(t); }
            return FormatDuration(ms, style);
        }

        // ── Event ingestion (from ScriptManager generic-event path) ─────────────
        public async Task ApplyStreamEventAsync(string phoenixEvent, IReadOnlyDictionary<string, string> vars)
        {
            if (string.IsNullOrEmpty(phoenixEvent)) return;
            var kind = ClassifyEvent(phoenixEvent);
            if (kind == EventKind.None) return;

            long nowUnix = NowUnixMs();
            var adds = new List<(string Slug, long Ms)>();
            var mileFires = new List<(string Slug, TimerMilestone Milestone)>();

            lock (_gate)
            {
                foreach (var t in _timers.Values)
                {
                    if (t.State != TimerRunState.Running) continue;
                    // Only subathon timers accrue time from stream events. A plain
                    // Countdown and a Stopwatch are driven by their own clock/logic,
                    // never by subs/bits/tips.
                    if (t.Mode != TimerMode.Subathon) continue;
                    long baseSeconds = ComputeSecondsForEvent(kind, t.Actions, vars, out string scope);
                    if (baseSeconds <= 0) continue;

                    long ms = baseSeconds * 1000L;
                    if (HappyHourApplies(t, scope, nowUnix))
                        ms = (long)Math.Round(ms * t.HappyHourMultiplier);
                    // Per-event cap (stream events are never "manual").
                    if (t.PerAddCapMs > 0 && ms > t.PerAddCapMs) ms = t.PerAddCapMs;

                    long before = t.RemainingMs;
                    t.RemainingMs += ms;
                    if (t.MaxCapMs > 0 && t.RemainingMs > t.MaxCapMs) t.RemainingMs = t.MaxCapMs;
                    long actual = t.RemainingMs - before;
                    if (actual <= 0) continue;

                    t.TotalAddedMs += actual;
                    t.UpdatedAtUnixMs = nowUnix;
                    adds.Add((t.Slug, actual));
                    foreach (var m in CollectMilestoneCrossingsLocked(t))
                        mileFires.Add((t.Slug, m));
                }
            }

            if (adds.Count == 0) return;

            foreach (var (slug, ms) in adds)
            {
                await CheckpointAsync(slug).ConfigureAwait(false);
                await LogActivityAsync(slug, "ADD", $"+{FormatDuration(ms, "clock")} · {phoenixEvent}").ConfigureAwait(false);
                await FireAddAsync(slug, ms, phoenixEvent).ConfigureAwait(false);
            }
            foreach (var (slug, m) in mileFires)
                await FireMilestoneAsync(GetLive(slug), m).ConfigureAwait(false);

            RaiseTimers();
            PublishLiveChannelNow();
        }

        /// <summary>""/null → default; else match slug then name (case-insensitive); unknown → default.</summary>
        public string? ResolveSlug(string selector)
        {
            lock (_gate) return ResolveSlugLocked(selector);
        }

        // ── Static duration grammar (formatter + reader) ───────────────────────
        // The READER lives beside the formatter on purpose: the milestone pill
        // pre-fills what FormatDuration renders, so the two halves must never
        // drift — and it lives HERE rather than on TimerViewModel because that
        // class is WinUI-coupled (Microsoft.UI.Xaml.Media fields) and merely
        // reflecting into it crashes a bare test host (no Windows App SDK
        // bootstrap). Internal + InternalsVisibleTo lets the suite pin the
        // grammar directly with no UI assembly load.

        /// <summary>
        /// Parses a human duration into milliseconds, −1 on anything malformed.
        /// Accepts: a bare number (seconds, "," tolerated as decimal point);
        /// the colon clock forms the pill pre-fills (MM:SS and HH:MM:SS — the
        /// LEADING segment unbounded, later segments 0-59, digits only, no
        /// 4-segment D:HH:MM:SS fold); and &lt;n&gt;&lt;d|h|m|s&gt; sequences ("1h30m").
        /// </summary>
        internal static long ParseDurationToMs(string? raw)
        {
            if (raw is null) return -1;
            string s = raw.Trim().ToLowerInvariant();
            if (s.Length == 0) return -1;

            // Whole string is a plain number → seconds.
            string numeric = s.Replace(',', '.');
            if (double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out double bareSec))
            {
                if (bareSec < 0 || double.IsNaN(bareSec) || double.IsInfinity(bareSec)) return -1;
                return (long)Math.Round(bareSec * 1000.0);
            }

            // Colon clock form — the shape FormatDuration renders, so the reader
            // MUST accept what the formatter emits or editing a displayed value
            // can only fail. Two segments read MM:SS, three read HH:MM:SS; the
            // LEADING segment is unbounded ("25:00:00" — clock-style big hours —
            // and "90:30" = 90 minutes) while every later one runs 0-59. Four
            // segments (the "short" style's D:HH:MM:SS fold) are rejected —
            // spell a day out as "1d…" instead. Digits only per segment: no
            // sign, no decimals, no embedded spaces.
            if (s.Contains(':'))
            {
                string[] parts = s.Split(':');
                if (parts.Length is < 2 or > 3) return -1;
                long clockSec = 0;
                for (int p = 0; p < parts.Length; p++)
                {
                    if (!long.TryParse(parts[p], NumberStyles.None, CultureInfo.InvariantCulture, out long seg))
                        return -1;   // empty, signed, fractional, or non-numeric segment
                    if (p > 0 && seg > 59) return -1;                // minutes/seconds run 0-59
                    if (p == 0 && seg > 9_999_999_999L) return -1;   // numeric-range sanity: keeps the ms math clear of overflow
                    clockSec = clockSec * 60 + seg;
                }
                return clockSec * 1000L;
            }

            // Otherwise a <number><unit> sequence.
            long totalMs = 0;
            int i = 0;
            bool any = false;
            while (i < s.Length)
            {
                int start = i;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                if (i == start) return -1;
                if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
                    return -1;
                if (i >= s.Length) return -1;
                long unitMs = s[i] switch
                {
                    'd' => 86_400_000L,
                    'h' => 3_600_000L,
                    'm' => 60_000L,
                    's' => 1_000L,
                    _   => -1,
                };
                if (unitMs < 0) return -1;
                i++;
                totalMs += (long)Math.Round(val * unitMs);
                any = true;
            }
            return any ? totalMs : -1;
        }

        /// <summary>
        /// The one milestone-target gate the Timer panel's add row and pill
        /// commit share: parse with the grammar above, refuse anything under
        /// one second (malformed and ≤ 0 parse to −1; 1–999 ms would truncate
        /// to a zero-second goal — born reached, meaningless at a 1 Hz tick),
        /// hand back whole seconds.
        /// </summary>
        internal static bool TryReadMilestoneTargetSeconds(string? draft, out long targetSeconds)
        {
            long targetMs = ParseDurationToMs(draft);
            if (targetMs < 1000) { targetSeconds = 0; return false; }
            targetSeconds = targetMs / 1000L;
            return true;
        }

        /// <summary>
        /// short: HH:MM:SS, folds to D:HH:MM:SS once ≥ 24h.
        /// long: DD:HH:MM:SS. clock: HH:MM:SS with hours allowed to exceed 24.
        /// </summary>
        public static string FormatDuration(long ms, string style)
        {
            if (ms < 0) ms = 0;
            long totalSec = ms / 1000;
            long days = totalSec / 86400;
            long hours = (totalSec % 86400) / 3600;
            long minutes = (totalSec % 3600) / 60;
            long seconds = totalSec % 60;
            switch ((style ?? "short").Trim().ToLowerInvariant())
            {
                case "long":
                    return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}:{3:00}", days, hours, minutes, seconds);
                case "clock":
                    long clockHours = totalSec / 3600;
                    return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", clockHours, minutes, seconds);
                case "short":
                default:
                    return days >= 1
                        ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}:{3:00}", days, hours, minutes, seconds)
                        : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", hours, minutes, seconds);
            }
        }

        // ── Internals ───────────────────────────────────────────────────────────
        private enum EventKind { None, Sub, Resub, Gift, Cheer, Follow, Raid, Tip }

        private static EventKind ClassifyEvent(string phoenixEvent)
        {
            var kind = ClassifyByLeaf(phoenixEvent);

            // A third-party money broker may reuse a PLATFORM word: Ko-Fi's paid
            // membership arrives as Kofi.Subscription / Kofi.Resubscription, whose leaves
            // are exactly the Twitch sub leaves — so it matched the Sub arm and added
            // SubT1Seconds (300 s, on by default) to a live subathon, bypassing the tip
            // seconds the C1 opt-out migration had just zeroed.
            //
            // ★ Scoped to the ENGAGEMENT kinds on purpose. A blanket "broker ⇒ Tip" is
            // wrong twice over, because IsDonationEvent matches a SOURCE PREFIX and so
            // covers every event those ten brokers send: it would turn
            // Patreon.PledgeDeleted (a patron CANCELLING) into a payout, and it would
            // promote both halves of Shopify's OrderCreated/OrderPaid pair to Tip, which
            // the layer-1 de-dupe cannot collapse because its key includes the event type.
            // Leaving the None cases None preserves their existing behaviour exactly.
            if (kind is EventKind.Sub or EventKind.Resub or EventKind.Gift
                && DonationIngest.IsDonationEvent(phoenixEvent))
                return EventKind.Tip;

            return kind;
        }

        private static EventKind ClassifyByLeaf(string phoenixEvent)
        {
            int dot = phoenixEvent.LastIndexOf('.');
            string leaf = dot >= 0 ? phoenixEvent[(dot + 1)..] : phoenixEvent;

            // Gift variants first (leaf is exact, so ordering is only for clarity).
            if (Eq(leaf, "GiftSub") || Eq(leaf, "GiftBomb") || Eq(leaf, "MassGiftSubscription") || Eq(leaf, "MembershipGift"))
                return EventKind.Gift;
            if (Eq(leaf, "Resub") || Eq(leaf, "Resubscription"))
                return EventKind.Resub;
            if (Eq(leaf, "Sub") || Eq(leaf, "Subscription") || Eq(leaf, "NewSubscriber") || Eq(leaf, "NewSponsor"))
                return EventKind.Sub;
            if (Eq(leaf, "Cheer"))
                return EventKind.Cheer;
            if (Eq(leaf, "Follow"))
                return EventKind.Follow;
            if (Eq(leaf, "Raid"))
                return EventKind.Raid;
            if (Eq(leaf, "SuperChat") || Eq(leaf, "SuperSticker") || Eq(leaf, "KicksGifted") || Eq(leaf, "JewelsGifted"))
                return EventKind.Tip;
            // Real-money tips from the third-party brokers, plus Twitch charity.
            // These leaves were missing here while LoyaltyService.Earn already
            // classified them, so a StreamElements tip credited points but added
            // ZERO subathon seconds — the two tools disagreed about what a tip is.
            // ("Tip" is StreamElements' leaf, "Donation" is the shared leaf of
            // Streamlabs / Ko-Fi / TipeeeStream / DonorDrive / Fourthwall,
            // "CampaignTip" is Pally.gg's, "CharityDonation" is Twitch's.)
            if (Eq(leaf, "Tip") || Eq(leaf, "Donation") || Eq(leaf, "CampaignTip") || Eq(leaf, "CharityDonation"))
                return EventKind.Tip;
            return EventKind.None;
        }

        private static bool Eq(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

        // Computes "seconds added" for one timer's Actions given the event. Also
        // reports the Happy-Hour scope this event belongs to.
        private static long ComputeSecondsForEvent(EventKind kind, TimerActionConfig a,
            IReadOnlyDictionary<string, string> vars, out string scope)
        {
            switch (kind)
            {
                case EventKind.Sub:
                case EventKind.Resub:
                    scope = "subs";
                    return TierSeconds(a, GetVar(vars, "user.tier"));
                case EventKind.Gift:
                {
                    scope = "subs";
                    long count = ParseLong(GetVar(vars, "user.count"), 1);
                    if (count < 1) count = 1;
                    return count * TierSeconds(a, GetVar(vars, "user.tier"));
                }
                case EventKind.Cheer:
                {
                    scope = "bits";
                    long bits = ParseLong(GetVar(vars, "user.bits"), 0);
                    if (bits <= 0 || a.BitsPer100Seconds <= 0) return 0;
                    return bits * a.BitsPer100Seconds / 100;
                }
                case EventKind.Follow:
                    scope = "follows";
                    return a.FollowSeconds; // 0 = off
                case EventKind.Raid:
                {
                    scope = "raids";
                    // user.count first (the raid payload's own count), then
                    // user.viewers — the two keys ScriptManager actually binds for a
                    // raid. A "raid.viewers" probe used to sit between them; nothing
                    // in the Hub ever wrote that key, so it was pure dead weight.
                    long viewers = ParseLong(GetVar(vars, "user.count"), -1);
                    if (viewers < 0) viewers = ParseLong(GetVar(vars, "user.viewers"), 0);
                    if (viewers <= 0 || a.RaidPerViewerSeconds <= 0) return 0;
                    return viewers * a.RaidPerViewerSeconds;
                }
                case EventKind.Tip:
                {
                    scope = "tips";
                    double amount = ParseDouble(GetVar(vars, "event.amount"));
                    if (amount <= 0) amount = ParseDouble(GetVar(vars, "user.amount"));
                    if (amount <= 0 || a.TipPerUnitSeconds <= 0) return 0;
                    return (long)Math.Round(amount * a.TipPerUnitSeconds);
                }
                default:
                    scope = "all";
                    return 0;
            }
        }

        private static long TierSeconds(TimerActionConfig a, string? tier)
        {
            string t = (tier ?? "").Trim();
            if (t.Equals("prime", StringComparison.OrdinalIgnoreCase)) return a.SubPrimeSeconds;
            return t switch
            {
                "2" => a.SubT2Seconds,
                "3" => a.SubT3Seconds,
                _ => a.SubT1Seconds, // "1" and unknowns default to T1
            };
        }

        private bool HappyHourApplies(StreamTimer t, string eventScope, long nowUnix)
        {
            if (t.HappyHourMultiplier <= 1.0) return false;
            if (t.HappyHourEndsAtUnixMs != 0 && nowUnix >= t.HappyHourEndsAtUnixMs) return false;
            string s = (t.HappyHourScope ?? "all").Trim().ToLowerInvariant();
            return s == "all" || s == eventScope;
        }

        private static string NormalizeScope(string? scope)
        {
            string s = (scope ?? "all").Trim().ToLowerInvariant();
            return s is "all" or "subs" or "bits" or "tips" or "follows" or "raids" ? s : "all";
        }

        private static string GetVar(IReadOnlyDictionary<string, string> vars, string key)
            => vars.TryGetValue(key, out var v) ? v : "";

        private static long ParseLong(string s, long fallback)
            => long.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

        private static double ParseDouble(string s)
            => double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

        // Marks + returns milestones newly crossed; call under _gate.
        //
        // Milestones fire in EVERY mode. The rule is one sentence — "the value the
        // timer displays has reached the target" — and DisplayMs is what makes it
        // mode-agnostic: ElapsedMs for a Stopwatch (counts UP, so a goal is met by
        // running long enough), RemainingMs for Subathon/Countdown (counts DOWN, so
        // a goal is met by the clock being RAISED past it, which is the subathon
        // semantic these were built for).
        //
        // Stopwatch used to bail out here, so a stopwatch goal could be added from
        // the panel and then sit inert for ever — added, listed, never fired.
        private static List<TimerMilestone> CollectMilestoneCrossingsLocked(StreamTimer t)
        {
            var fired = new List<TimerMilestone>();
            long value = DisplayMs(t);
            foreach (var m in t.Milestones)
            {
                if (!m.Reached && value >= m.TargetSeconds * 1000L)
                {
                    m.Reached = true;
                    fired.Add(m);
                }
            }
            return fired;
        }

        // Re-seeds Reached flags to the current level without firing; call under
        // _gate. Mode-aware for the same reason CollectMilestoneCrossingsLocked is —
        // seeding a Stopwatch against RemainingMs (a field it never displays, and
        // which stays 0) marked every goal UNreached no matter how long it had run.
        private static void ReseedMilestonesLocked(StreamTimer t)
        {
            long value = DisplayMs(t);
            foreach (var m in t.Milestones)
                m.Reached = value >= m.TargetSeconds * 1000L;
        }

        // Un-ends a countdown that has time back on the clock; call under _gate,
        // returns true when the state actually flipped.
        //
        // TickOnceAsync parks a timer in Ended the moment it hits zero (so OnZero
        // fires exactly once) and the tick gate then skips it forever. Adding time
        // afterwards therefore has to re-arm the run state as well, or the subathon
        // sits frozen at its new value still labelled "ended" — the streamer clicks
        // +ADD, the number goes up, and nothing counts down again. A Stopwatch never
        // reaches Ended, so it is never a candidate.
        private static bool ReArmIfEndedLocked(StreamTimer t)
        {
            if (t.State != TimerRunState.Ended || t.Mode == TimerMode.Stopwatch || t.RemainingMs <= 0)
                return false;
            t.State = TimerRunState.Running;
            return true;
        }

        // Ends a countdown that an out-of-band edit has landed exactly on zero; call
        // under _gate, returns true when the state actually flipped. The caller then
        // awaits FireZeroAsync OUTSIDE the lock, exactly as the tick does.
        //
        // The mirror image of ReArmIfEndedLocked, and load-bearing for the same reason.
        // Zero detection otherwise lives ONLY inside the tick's decrement branch, which
        // is itself gated on RemainingMs > 0 — so a timer that SetTimeMsAsync,
        // SubtractMsAsync or a negative AddMsAsync puts on zero is never looked at
        // again: it sits at 00:00:00 still labelled Running, Timer.OnZero never fires,
        // no TIMER_ZERO reaches the bus, and the overlay publishes state="Running" next
        // to display_seconds=0. Only Toggle or Stop could ever get it out of that state
        // (ResumeAsync refuses it), so the subathon-end graph simply never runs. A
        // Stopwatch counts UP and has no zero, so it is never a candidate.
        private static bool EndIfZeroLocked(StreamTimer t)
        {
            if (t.State != TimerRunState.Running || t.Mode == TimerMode.Stopwatch || t.RemainingMs > 0)
                return false;
            t.State = TimerRunState.Ended;
            return true;
        }

        private async Task SetStateAsync(string slug, TimerRunState state, string verb, TimerRunState? onlyFrom = null)
        {
            string? resolved;
            lock (_gate)
            {
                resolved = ResolveSlugLocked(slug);
                if (resolved is null) return;
                var t = _timers[resolved];
                if (onlyFrom.HasValue && t.State != onlyFrom.Value) return;
                if (t.State == state) return;
                t.State = state;
                t.UpdatedAtUnixMs = NowUnixMs();
            }
            await CheckpointAsync(resolved).ConfigureAwait(false);
            await LogActivityAsync(resolved, "CTRL", verb).ConfigureAwait(false);
            RaiseTimers();
            PublishLiveChannelNow();
        }

        private async Task MutateFieldAsync(string slug, Action<StreamTimer> mutate)
        {
            string? resolved;
            lock (_gate)
            {
                resolved = ResolveSlugLocked(slug);
                if (resolved is null) return;
                var t = _timers[resolved];
                mutate(t);
                t.UpdatedAtUnixMs = NowUnixMs();
            }
            await CheckpointAsync(resolved).ConfigureAwait(false);
            RaiseTimers();
        }

        private string GenerateSlugLocked()
        {
            string baseKey = $"t-{DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
            var used = new HashSet<string>(_timers.Keys, StringComparer.OrdinalIgnoreCase);
            for (char c = 'a'; c <= 'z'; c++)
            {
                string candidate = $"{baseKey}-{c}";
                if (!used.Contains(candidate)) return candidate;
            }
            return $"{baseKey}-{Guid.NewGuid():N}".Substring(0, baseKey.Length + 7);
        }

        private string? ResolveSlugLocked(string? selector)
        {
            if (string.IsNullOrWhiteSpace(selector))
                return DefaultSlugLocked();
            string s = selector.Trim();
            if (_timers.TryGetValue(s, out var direct)) return direct.Slug;
            var byName = _timers.Values.FirstOrDefault(t => t.Name.Equals(s, StringComparison.OrdinalIgnoreCase));
            // Unknown selector → default (see the Control-section contract).
            return byName?.Slug ?? DefaultSlugLocked();
        }

        private string? DefaultSlugLocked()
        {
            if (_defaultSlug is not null && _timers.ContainsKey(_defaultSlug)) return _defaultSlug;
            return _timers.Keys.FirstOrDefault();
        }

        private StreamTimer? GetLocked(string slug)
        {
            var resolved = ResolveSlugLocked(slug);
            return resolved is not null && _timers.TryGetValue(resolved, out var t) ? t : null;
        }

        // Live (non-cloned) accessor for the fire helpers, which only READ.
        private StreamTimer? GetLive(string slug)
        {
            lock (_gate) return _timers.TryGetValue(slug, out var t) ? t : null;
        }

        private StreamTimer? CloneTimerFor(string slug)
        {
            lock (_gate) return _timers.TryGetValue(slug, out var t) ? CloneTimer(t) : null;
        }

        private static TimerMilestone CloneMilestone(TimerMilestone m) => new()
        {
            Id = m.Id, Label = m.Label, TargetSeconds = m.TargetSeconds, Reached = m.Reached,
            Message = m.Message, LayerId = m.LayerId, TriggerName = m.TriggerName,
        };

        // ── DB + fan-out helpers ────────────────────────────────────────────────
        private async Task CheckpointAsync(string? slug)
        {
            if (string.IsNullOrEmpty(slug)) return;
            StreamTimer? snap;
            lock (_gate) snap = _timers.TryGetValue(slug, out var t) ? CloneTimer(t) : null;
            if (snap is null) return;
            try { await _db.UpsertTimerAsync(snap).ConfigureAwait(false); }
            catch (Exception ex) { GlobalLogger.Error("TimerService", $"checkpoint '{slug}' failed", ex); }
        }

        private async Task LogActivityAsync(string slug, string kind, string message)
        {
            try { await _db.LogTimerActivityAsync(slug, kind, message, NowIso()).ConfigureAwait(false); }
            catch (Exception ex) { GlobalLogger.Error("TimerService", $"activity log '{slug}' failed", ex); }
        }

        // ── Overlay Live Channel publish ────────────────────────────────────────
        // Provenance tag on every timer key. Identical on EVERY publish by design:
        // the store inherits a declared ExpectedInterval across writes only while the
        // Source string matches, so a second spelling here would let a state-change
        // publish silently drop the tick cadence and the keys could never report Stale.
        private const string LiveSource = "tool:Timer";

        // The declared publish cadence = the tick cadence. GetState reports Stale after
        // StaleIntervalMultiplier missed intervals, so an overlay can tell "the timer is
        // paused at 00:10:00" (Active) from "Hub stopped ticking / this timer was deleted"
        // (Stale) instead of painting a frozen number as live. It is also what makes a
        // deleted timer's leftover keys decay honestly — the store has no remove API, and
        // it needs none: nothing republishes them, so they go Stale within 3 s.
        private static readonly TimeSpan LiveInterval = TimeSpan.FromSeconds(1);

        // The 1 Hz tick and every mutator publish through here, so both the snapshot and
        // the write to the store happen under ONE gate — otherwise two overlapping
        // publishes could land out of order and leave the channel holding the OLDER
        // state until the next tick corrected it (the OBS countdown momentarily ticking
        // backwards, or a just-added +300s vanishing for a second). The siblings carry
        // the same gate for the same reason: PollsService._publishGate,
        // SongRequestService._publishGate. Taking the snapshot INSIDE the gate rather
        // than before it is what actually orders the two — a gate around the store write
        // alone still lets a snapshot taken first be published last.
        //
        // Lock order is _publishGate → _gate, and it is the only place the two are held
        // together.
        private readonly object _publishGate = new();

        private void PublishLiveChannelNow()
        {
            lock (_publishGate)
            {
                List<StreamTimer> snapshot;
                lock (_gate) snapshot = _timers.Values.Select(CloneTimer).ToList();
                PublishLiveChannel(snapshot);
            }
        }

        /// <summary>
        /// Publishes every timer's 13 live-channel fields under THREE roots: the real
        /// <c>timer.&lt;slug&gt;.*</c>, the <c>timer.__default.*</c> mirror, and a
        /// <c>timer.&lt;display-name&gt;.*</c> alias.
        ///
        /// The <c>__default</c> mirror is load-bearing, not convenience: a widget's
        /// <c>TimerName</c> attribute is empty for "the default timer", and the browser
        /// derives its subscription from literal attribute values while scanning the graph
        /// — before any frame could have told it which slug is default. With slug keys
        /// alone that is a deadlock (no subscription ⇒ no frame ⇒ no slug ⇒ no
        /// subscription), so the default timer's fields are published TWICE, once under its
        /// real slug and once under the fixed <c>__default</c> alias, and
        /// <c>timer.__default.slug</c> carries the real slug for diagnostics. When the
        /// default timer changes, the mirror's values change and one patch carries them —
        /// the widget never re-subscribes.
        ///
        /// The display-NAME alias is the same argument with a worse consequence, and it is
        /// documented at <see cref="PublishNameAliasRoots"/>.
        ///
        /// All three roots are free on the wire, which is why the duplication is not a
        /// waste to be optimised away: coalescing ships nothing for an unchanged value, and
        /// the store's publisher-side gate means an alias root never leaves the process
        /// unless a widget actually subscribes to it.
        ///
        /// Field names are the channel keys in the campaign contract §1; they are NOT the
        /// old TIMER_UPDATE payload's camelCase names. V4 has landed, so the browser's
        /// readers are on these keys and the tree is consistent again — the V3-era caveat
        /// this paragraph used to carry, about the two halves being out of step until V4
        /// arrived, is spent and has been removed rather than left to read as current.
        ///
        /// ★ V14 — WHICH FIELDS THE BROWSER ACTUALLY READS, verified against
        /// <c>compositor.js</c> rather than asserted. This comment's predecessor declared
        /// that the payload's field names had to agree with <c>evalTimerRemaining</c>'s
        /// reader and then listed names that reader never touched — an unverifiable claim
        /// about another file, which is the shape to avoid, not merely the wrong list.
        /// <c>V14RuntimeSweepTests</c> now DERIVES the read set from <c>compositor.js</c>
        /// and fails here if the two drift again. The truth today:
        ///
        /// <c>liveKeysForNode</c> subscribes the WHOLE family as one prefix
        /// (<c>timer.&lt;root&gt;.*</c>), so all 13 fields reach the browser — but
        /// <c>evalTimerRemaining</c> names only EIGHT of them, one per output socket:
        /// <c>state</c> (twice over — its VALUE feeds the State pin, its provenance feeds
        /// the Live pin), <c>progress</c>, <c>display_seconds</c> (NOT
        /// <c>remaining_seconds</c> — that would count the wrong way for a stopwatch),
        /// <c>paused</c>, <c>mode</c>, and <c>short</c> / <c>long</c> / <c>clock</c>, of
        /// which the Text socket reads exactly one per render, chosen by the node's Format
        /// attribute.
        ///
        /// The remaining five — <c>name</c>, <c>remaining_ms</c>, <c>remaining_seconds</c>,
        /// <c>elapsed_ms</c>, <c>display_ms</c> — plus the separate
        /// <c>timer.__default.slug</c> have NO reader in the timer trio. They are not
        /// dead: <c>Var.Live</c> binds an arbitrary literal key, so an author can point one
        /// at <c>timer.main.elapsed_ms</c> and get an exact JSON number, and
        /// <c>overlay.get</c> reaches them from a script. That is the reason to keep
        /// publishing them, and it is the ONLY reason — no widget node resolves them
        /// implicitly. Note the Live pin deliberately judges presence on <c>state</c> and
        /// not on <c>slug</c>, because <c>timer.__default.slug</c> is published even when no
        /// default timer exists and would report Active for nothing.
        /// </summary>
        private void PublishLiveChannel(List<StreamTimer> snapshot)
        {
            var store = LiveStore;
            string defaultRoot = KeyRoot(GetDefaultSlug());

            // Published even when there is no default timer (empty value): "present and
            // empty" is a readable answer, a Missing key is indistinguishable from a Hub
            // that never spoke.
            store.PublishString("timer.__default.slug", defaultRoot, LiveSource, LiveInterval);

            // Slug roots are collected up front because the name-alias pass has to know the
            // WHOLE set before it can refuse to shadow one — a timer named after another
            // timer's slug must not silently overwrite that timer's keys.
            var slugRoots = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in snapshot)
            {
                string root = KeyRoot(t.Slug);
                if (root.Length > 0) slugRoots.Add(root);
            }

            foreach (var t in snapshot)
            {
                string root = KeyRoot(t.Slug);
                if (root.Length == 0) continue;   // slug-less timer can have no key root
                PublishTimerFields(store, "timer." + root + ".", t);
                if (string.Equals(root, defaultRoot, StringComparison.Ordinal))
                    PublishTimerFields(store, "timer.__default.", t);
            }

            PublishNameAliasRoots(store, snapshot, slugRoots);
        }

        /// <summary>
        /// Publishes each timer's 13 fields a THIRD time under its lower-cased display name.
        ///
        /// This is not redundancy — without it every existing <c>.phxlayer</c> widget
        /// authored with a display name would have gone permanently blank the moment V4's
        /// readers landed (they have, and it did not).
        /// A slug is machine-generated (<c>t-&lt;yyyy-MM-dd&gt;-&lt;letter&gt;</c>) and nobody
        /// types it; <c>TimerName</c> means the DISPLAY NAME everywhere else in the product —
        /// <c>ResolveSlugLocked</c> matches slug then name case-insensitively,
        /// <c>event.timername</c> carries the name, and the Visualist template text documents
        /// the attribute as "selects by slug then display name". The retired TIMER_UPDATE
        /// payload shipped BOTH <c>slug</c> and <c>name</c> per timer, which is what let the
        /// browser's pre-channel reader fall back from slug to name on its own.
        ///
        /// ★ V14 — that browser-side fallback no longer exists and must not be claimed:
        /// <c>liveTimerRoot</c> performs ONE <c>trim().toLowerCase()</c> lookup and has no
        /// second attempt, which is precisely why the alias published here has to exist. The
        /// name-vs-slug ambiguity is resolved on THIS side, by publishing both roots, not on
        /// the reader's side by trying two keys.
        ///
        /// And the failure would be unrecoverable by the streamer: the browser derives its
        /// subscription from the LITERAL attribute text at graph-scan time, so no OBS refresh
        /// and no re-save changes the key it asks for. The symptom is a blank widget, no
        /// error, a running timer and a valid graph.
        ///
        /// Cost: none, for the same two reasons the <c>__default</c> mirror is free —
        /// coalescing suppresses unchanged values, and the store's publisher-side gate keeps
        /// the alias out of every frame unless a widget subscribes to it. Do NOT "optimise"
        /// the duplicate away.
        ///
        /// Normalisation matches the reader exactly: contract §1 has the browser subscribe
        /// <c>"timer." + TimerName.Trim().ToLowerInvariant() + ".*"</c>, and
        /// <see cref="KeyRoot"/> is that same trim-then-lower rule, applied here to the NAME
        /// as well as to the slug. Publisher and reader normalise identically or the binding
        /// silently misses.
        ///
        /// Three skips, each of which would otherwise make one timer's keys lie about another:
        /// an empty name (no alias to form), a name whose root equals this timer's own slug
        /// root (already published), and a name that collides with ANY slug root or with the
        /// reserved <c>__default</c> alias. Duplicate display names resolve first-wins, which
        /// is precisely what <c>ResolveSlugLocked</c>'s <c>FirstOrDefault</c> does over the
        /// same enumeration order — Hub's two name lookups must not disagree.
        /// </summary>
        private static void PublishNameAliasRoots(
            OverlayLiveStore store, List<StreamTimer> snapshot, HashSet<string> slugRoots)
        {
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in snapshot)
            {
                string nameRoot = KeyRoot(t.Name);
                if (nameRoot.Length == 0) continue;                  // unnamed → no alias
                if (nameRoot == DefaultAliasRoot) continue;          // must not shadow the mirror
                if (slugRoots.Contains(nameRoot)) continue;          // own slug, or another timer's
                if (!claimed.Add(nameRoot)) continue;                // duplicate name → first wins

                PublishTimerFields(store, "timer." + nameRoot + ".", t);
            }
        }

        // The fixed alias root the "empty TimerName means the default timer" mirror lives
        // under. Named so the name-alias pass can refuse to shadow it: a timer a streamer
        // literally called "__default" would otherwise overwrite the mirror and make every
        // default-bound widget in the layer follow the wrong timer.
        private const string DefaultAliasRoot = "__default";

        // Slug / display name → key root. Trim-then-lower is the SHARED normalisation rule:
        // live-channel keys are literal and lowercase by contract, and the browser derives
        // its subscription with the identical Trim().ToLowerInvariant() on the widget's
        // TimerName attribute (contract §1). One rule on both halves, or a binding misses
        // with no diagnostic. For slugs the lower-casing is identity in practice (generated
        // slugs are already lowercase) and only stops a hand-edited DB row from producing a
        // key no browser can name; for display names it is what makes "Box A Subathon" and
        // "box a subathon" the same binding.
        private static string KeyRoot(string? slug) => (slug ?? string.Empty).Trim().ToLowerInvariant();

        // The 13 fields, published under a "timer.<root>." prefix. Numerics go through
        // PublishNumber and stay JSON numbers — a widget's Scalar pin then reads an exact
        // value instead of re-parsing text (that stringify-then-reparse round trip is what
        // the old payload forced on every progress bar).
        private static void PublishTimerFields(OverlayLiveStore store, string prefix, StreamTimer t)
        {
            // short/long/clock and display_* carry the mode-aware DISPLAY value (elapsed
            // for a Stopwatch, remaining otherwise) so Timer.Remaining / Countdown.Remaining
            // / Stopwatch.Elapsed all resolve correctly off the same field — which is why
            // the browser's Seconds socket reads display_seconds and never
            // remaining_seconds.
            //
            // ★ V14 — name / remaining_ms / remaining_seconds / elapsed_ms / display_ms are
            // published for AUTHOR access only. No widget node resolves them: the timer trio
            // reads the eight fields listed on PublishLiveChannel, and these five are
            // reachable solely through a Var.Live pointed at the literal key, or overlay.get
            // from a script. Stated concretely because the vaguer "raw for anything that
            // needs one specific direction" this replaces read as though a reader existed.
            long displayMs = DisplayMs(t);

            store.PublishString(prefix + "name",              t.Name,                             LiveSource, LiveInterval);
            store.PublishString(prefix + "mode",              t.Mode.ToString(),                  LiveSource, LiveInterval);
            store.PublishString(prefix + "state",             t.State.ToString(),                 LiveSource, LiveInterval);
            store.PublishBool  (prefix + "paused",            t.State == TimerRunState.Paused,    LiveSource, LiveInterval);
            store.PublishNumber(prefix + "progress",          ProgressOf(t),                      LiveSource, LiveInterval);
            store.PublishNumber(prefix + "remaining_ms",      t.RemainingMs,                      LiveSource, LiveInterval);
            store.PublishNumber(prefix + "remaining_seconds", t.RemainingMs / 1000,               LiveSource, LiveInterval);
            store.PublishNumber(prefix + "elapsed_ms",        t.ElapsedMs,                        LiveSource, LiveInterval);
            store.PublishNumber(prefix + "display_ms",        displayMs,                          LiveSource, LiveInterval);
            store.PublishNumber(prefix + "display_seconds",   displayMs / 1000,                   LiveSource, LiveInterval);
            store.PublishString(prefix + "short",             FormatDuration(displayMs, "short"), LiveSource, LiveInterval);
            store.PublishString(prefix + "long",              FormatDuration(displayMs, "long"),  LiveSource, LiveInterval);
            store.PublishString(prefix + "clock",             FormatDuration(displayMs, "clock"), LiveSource, LiveInterval);
        }

        private async Task FireAddAsync(string slug, long addedMs, string source)
        {
            var t = GetLive(slug);
            if (t is null) return;
            long seconds = addedMs / 1000;
            // The *.remaining tokens carry the mode-aware DISPLAY value — the same
            // number timer.get_remaining() answers with — so a Stopwatch reports its
            // elapsed count-up instead of the RemainingMs field it never displays.
            string remainingSeconds = (DisplayMs(t) / 1000).ToString(CultureInfo.InvariantCulture);
            // Keys mirror the Architect Timer.OnAdd node's output sockets, which
            // emit {event.<socketname-lowercased>} tokens (TimerName/Source/Seconds).
            // The timer.* aliases are kept as extra raw tokens scripts may also use.
            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["event.timername"] = t.Name,
                ["event.source"] = source,
                ["event.seconds"] = seconds.ToString(CultureInfo.InvariantCulture),
                ["event.slug"] = t.Slug,
                ["event.remaining"] = remainingSeconds,
                ["timer.name"] = t.Name,
                ["timer.slug"] = t.Slug,
                ["timer.source"] = source,
                ["timer.seconds"] = seconds.ToString(CultureInfo.InvariantCulture),
                ["timer.remaining"] = remainingSeconds,
            };
            RaiseScript("Timer.OnAdd", vars);
            await BusEmitAsync("TIMER_ADD", new
            {
                type = "TIMER_ADD", slug = t.Slug, name = t.Name, source,
                seconds, remainingMs = t.RemainingMs,
            }).ConfigureAwait(false);
            QueueAddFeedback(t, addedMs, source);
        }

        private async Task FireMilestoneAsync(StreamTimer? t, TimerMilestone m)
        {
            if (t is null) return;
            await LogActivityAsync(t.Slug, "MILE", $"milestone reached — {m.Label}").ConfigureAwait(false);
            // Keys mirror the Architect Timer.OnMilestone node sockets
            // (TimerName/MilestoneId/Label → {event.timername}/{event.milestoneid}/{event.label}).
            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["event.timername"] = t.Name,
                ["event.milestoneid"] = m.Id,
                ["event.label"] = m.Label,
                ["event.slug"] = t.Slug,
                ["timer.name"] = t.Name,
                ["timer.slug"] = t.Slug,
                ["timer.milestone_id"] = m.Id,
                ["timer.label"] = m.Label,
            };
            RaiseScript("Timer.OnMilestone", vars);
            await BusEmitAsync("TIMER_MILESTONE", new
            {
                type = "TIMER_MILESTONE", slug = t.Slug, name = t.Name,
                milestoneId = m.Id, label = m.Label,
            }).ConfigureAwait(false);
            await FireMilestoneFeedbackAsync(t, m).ConfigureAwait(false);
        }

        private async Task FireZeroAsync(StreamTimer t)
        {
            await LogActivityAsync(t.Slug, "ZERO", "reached zero").ConfigureAwait(false);
            // Keys mirror the Architect Timer.OnZero node socket (TimerName → {event.timername}).
            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["event.timername"] = t.Name,
                ["event.slug"] = t.Slug,
                ["timer.name"] = t.Name,
                ["timer.slug"] = t.Slug,
            };
            RaiseScript("Timer.OnZero", vars);
            await BusEmitAsync("TIMER_ZERO", new { type = "TIMER_ZERO", slug = t.Slug, name = t.Name }).ConfigureAwait(false);
            await FireZeroFeedbackAsync(t).ConfigureAwait(false);
        }

        // ── Feedback layer (chat + visual for OnZero / OnMilestone / OnAdd) ─────
        //
        // The tool-side half of the three events that ALREADY have an Architect event
        // node, so it stays a no-code shortcut over a graph a streamer could hand-build
        // (Timer.On* → Chat.Send + Visual.Trigger) rather than a capability only the
        // tool can reach. Everything is gated OFF by default per timer.
        //
        // Both effects leave the process through SEAMS wired by ScriptManager.Timer.cs
        // (SendChat / FireVisual): this class must not reach the Bus or the chat core
        // itself, exactly as RaiseScriptEvent / BusEmit already avoid.
        //
        // ★ ANTI-SPAM IS THE LOAD-BEARING PART, not a nicety. ApplyStreamEventAsync
        // fans an inbound event out over EVERY running subathon timer, and a gift bomb
        // arrives as one aggregate plus one tail per recipient — a 50-sub bomb with two
        // timers is ~102 FireAddAsync calls in seconds. PerAddCapMs / MaxCapMs bound
        // SECONDS, never event COUNT, so neither of them helps here. Adds are therefore
        // buffered per timer and drained as ONE summary line on the tick (see
        // DrainFeedbackAsync); milestone lines share that buffer so one bomb crossing
        // five goals also posts once.

        // Twitch caps a chat message at 500 characters and SendTwitchChatCore DROPS an
        // over-length line rather than truncating it, so every composed line is clipped
        // here first — a summary that grows past the cap must still say something.
        internal const int ChatLineMaxChars = 500;

        // A count-down timer that zeroes, takes a straggler cheer and zeroes again is
        // arithmetically legitimate (ReArmIfEndedLocked is ungated, and the wind-down is
        // exactly when the event rate spikes), but two "subathon over" lines seconds
        // apart read as a bug. Keyed per (slug, kind) — a single service-wide stamp
        // would let one timer's zero swallow another timer's.
        private const long ZeroFeedbackCooldownMs = 30_000;

        // How long a pending burst may be held before it is summarised anyway. Without
        // the cap a sustained flood (adds arriving every beat) would never see a quiet
        // beat and the streamer would hear nothing at all.
        private const long FeedbackMaxHoldMs = 10_000;

        private readonly object _feedbackGate = new();
        private readonly Dictionary<string, PendingFeedback> _pendingFeedback =
            new(StringComparer.OrdinalIgnoreCase);
        // ValueTuple key, deliberately not a concatenated string: no separator character
        // means no separator collision between a slug and a kind.
        private readonly Dictionary<(string Slug, string Kind), long> _feedbackCooldown = new();
        private long _tickSeq;

        /// <summary>One timer's un-announced feedback, awaiting a quiet beat.</summary>
        private sealed class PendingFeedback
        {
            public long AddedMs;
            public int AddCount;
            /// <summary>The one source every buffered add came from, or null once two
            /// disagree (rendered as "multiple").</summary>
            public string? AddSource;
            public bool AddSourcesDiffer;
            public readonly List<string> MilestoneLines = new();
            public long OpenedAtMs;
            public long LastTickSeq;
        }

        // One compiled scan of the whole template, MatchEvaluator-resolved — the
        // AlertsService idiom. Chained string.Replace calls each re-scan the ENTIRE
        // string, so a value substituted by an earlier call is fair game for a later one
        // (a timer literally named "{remaining}" would get rewritten); this pass never
        // re-scans what it substituted. AlertsService.ResolveTokens is deliberately NOT
        // called: its switch has no timer arm and its default returns the token verbatim,
        // so {timer}/{remaining} would render as literal braces.
        private static readonly Regex FeedbackTokenRx = new(@"\{([^{}]+)\}", RegexOptions.Compiled);

        /// <summary>Single-pass token substitution. An unknown token is left exactly as
        /// the streamer authored it, so a line carrying braces for other reasons survives.</summary>
        internal static string RenderFeedback(string? template, IReadOnlyDictionary<string, string> tokens)
        {
            if (string.IsNullOrEmpty(template)) return "";
            return FeedbackTokenRx.Replace(template, m =>
                tokens.TryGetValue(m.Groups[1].Value.Trim(), out var v) ? v : m.Value);
        }

        /// <summary>Clips a composed line to the Twitch cap. Truncating is strictly
        /// better than the send path's silent drop: a clipped line still tells the
        /// channel what happened.</summary>
        internal static string ClipToChatCap(string? line)
        {
            if (string.IsNullOrEmpty(line)) return "";
            return line.Length <= ChatLineMaxChars ? line : line.Substring(0, ChatLineMaxChars - 1) + "…";
        }

        // Monotonic clock, never NowUnixMs — an NTP step or a DST change must not open or
        // close a suppression window (the same rule the tick delta follows).
        private bool FeedbackCooldownElapsed(string slug, string kind, long windowMs)
        {
            long now = _clock.ElapsedMilliseconds;
            lock (_feedbackGate)
            {
                if (_feedbackCooldown.TryGetValue((slug, kind), out long last) && now - last < windowMs)
                    return false;
                _feedbackCooldown[(slug, kind)] = now;
                return true;
            }
        }

        /// <summary>Posts one line through the chat seam, clipped, and only while the
        /// broadcaster is seen live. <c>_streamLive</c> starts TRUE and only flips on an
        /// observed StreamOffline, so a streamer testing off-air still gets their line;
        /// what this suppresses is a subathon announcing itself to an empty channel hours
        /// after the stream ended. The send path itself owns the connectivity and
        /// chat-action guards and logs its own drops.</summary>
        private async Task PostFeedbackLineAsync(string slug, string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            var send = SendChat;
            if (send is null) return;
            if (!_streamLive)
            {
                GlobalLogger.Log($"Timer '{slug}' feedback line withheld — the stream is offline.",
                    "TimerService", LogLevel.System);
                return;
            }
            try { await send(ClipToChatCap(line)).ConfigureAwait(false); }
            catch (Exception ex) { GlobalLogger.Error("TimerService", $"feedback chat send '{slug}' failed", ex); }
        }

        /// <summary>Fires one visual through the shared fan-out seam.
        /// ★ That fan-out does NOT run ExpandArgsList (unlike visual.trigger), so the
        /// three Args keys are written out individually — a single "Args=a,b,c" would
        /// arrive unsplit and every {Args1} in the widget graph would render empty.
        /// Args1=KIND / Args2=NAME / Args3=VALUE mirrors the AlertsService contract.</summary>
        private async Task FireFeedbackVisualAsync(
            string slug, string layerId, string triggerName, string kind, string name, string value)
        {
            if (string.IsNullOrWhiteSpace(layerId) || string.IsNullOrWhiteSpace(triggerName)) return;
            var fire = FireVisual;
            if (fire is null) return;
            var eventData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Args1"] = kind,
                ["Args2"] = name,
                ["Args3"] = value,
            };
            // Trim — the pickers offer registered ids but both fields accept free text,
            // and a padded id fails the registry lookup.
            try { await fire(layerId.Trim(), triggerName.Trim(), eventData).ConfigureAwait(false); }
            catch (Exception ex) { GlobalLogger.Error("TimerService", $"feedback visual '{slug}' failed", ex); }
        }

        // Tokens offered per event are exactly the ones the matching Fire*Async binds —
        // offering one the raise site never binds would render empty and would advertise
        // a value no Architect graph could reach.
        private static Dictionary<string, string> BaseTokens(StreamTimer t) =>
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["timer"] = t.Name,
                ["slug"] = t.Slug,
            };

        private async Task FireZeroFeedbackAsync(StreamTimer t)
        {
            var cfg = t.Feedback?.Zero;
            if (cfg is null || !cfg.Enabled) return;
            if (!FeedbackCooldownElapsed(t.Slug, "zero", ZeroFeedbackCooldownMs)) return;

            var tokens = BaseTokens(t);   // {timer} / {slug} — FireZeroAsync binds no more
            await PostFeedbackLineAsync(t.Slug, RenderFeedback(cfg.Message, tokens)).ConfigureAwait(false);
            await FireFeedbackVisualAsync(t.Slug, cfg.LayerId, cfg.TriggerName,
                "TIMER ZERO", t.Name, "0").ConfigureAwait(false);
        }

        private async Task FireMilestoneFeedbackAsync(StreamTimer t, TimerMilestone m)
        {
            var cfg = t.Feedback?.Milestone;
            if (cfg is null || !cfg.Enabled) return;

            var tokens = BaseTokens(t);
            tokens["label"] = m.Label;   // FireMilestoneAsync binds event.label / timer.label

            // Per-goal override, timer-wide default underneath — so one line with {label}
            // covers every goal while a headline goal can still have its own text.
            string template = string.IsNullOrWhiteSpace(m.Message) ? cfg.Message : m.Message;
            string line = RenderFeedback(template, tokens);
            if (!string.IsNullOrWhiteSpace(line))
            {
                // Buffered, not posted: one gift bomb can cross several goals in the same
                // instant and both crossing call sites loop the WHOLE crossed set, so the
                // lines are joined into one post by the drain below.
                lock (_feedbackGate)
                {
                    var p = OpenPendingLocked(t.Slug);
                    p.MilestoneLines.Add(line);
                }
            }

            // The visual is NOT coalesced: goals legitimately point at different layers
            // and triggers, so there is nothing to merge — joining them would mean
            // dropping all but one.
            string layerId = string.IsNullOrWhiteSpace(m.LayerId) ? cfg.LayerId : m.LayerId;
            string triggerName = string.IsNullOrWhiteSpace(m.TriggerName) ? cfg.TriggerName : m.TriggerName;
            await FireFeedbackVisualAsync(t.Slug, layerId, triggerName, "TIMER MILESTONE",
                string.IsNullOrWhiteSpace(m.Label) ? t.Name : m.Label,
                FormatDuration(m.TargetSeconds * 1000L, "clock")).ConfigureAwait(false);
        }

        private void QueueAddFeedback(StreamTimer t, long addedMs, string source)
        {
            var cfg = t.Feedback?.Add;
            if (cfg is null || !cfg.Enabled) return;
            lock (_feedbackGate)
            {
                var p = OpenPendingLocked(t.Slug);
                p.AddedMs += addedMs;
                p.AddCount++;
                if (p.AddSource is null) p.AddSource = source;
                else if (!string.Equals(p.AddSource, source, StringComparison.OrdinalIgnoreCase))
                    p.AddSourcesDiffer = true;
            }
        }

        // Call under _feedbackGate. OpenedAtMs is stamped once (the hold cap measures the
        // whole burst); LastTickSeq is refreshed on every append, and that is what makes
        // "no quiet beat yet" mean "the burst is still running".
        private PendingFeedback OpenPendingLocked(string slug)
        {
            if (!_pendingFeedback.TryGetValue(slug, out var p))
            {
                p = new PendingFeedback { OpenedAtMs = _clock.ElapsedMilliseconds };
                _pendingFeedback[slug] = p;
            }
            p.LastTickSeq = Interlocked.Read(ref _tickSeq);
            return p;
        }

        /// <summary>
        /// Drains every timer whose pending burst is settled — called at the bottom of
        /// the 1 Hz tick, which is why the coalescing window costs no extra timer.
        /// A buffer is due once a whole beat has passed without appending to it (the
        /// burst is over) or once it has been held for <see cref="FeedbackMaxHoldMs"/>.
        /// </summary>
        private async Task DrainFeedbackAsync()
        {
            List<(string Slug, PendingFeedback Pending)> due;
            long now = _clock.ElapsedMilliseconds;
            long seq = Interlocked.Read(ref _tickSeq);
            lock (_feedbackGate)
            {
                if (_pendingFeedback.Count == 0) return;
                due = new List<(string, PendingFeedback)>();
                foreach (var kv in _pendingFeedback)
                    if (seq > kv.Value.LastTickSeq || now - kv.Value.OpenedAtMs >= FeedbackMaxHoldMs)
                        due.Add((kv.Key, kv.Value));
                foreach (var (slug, _) in due) _pendingFeedback.Remove(slug);
            }
            foreach (var (slug, pending) in due)
                await FlushFeedbackAsync(slug, pending).ConfigureAwait(false);
        }

        private async Task FlushFeedbackAsync(string slug, PendingFeedback p)
        {
            var t = GetLive(slug);
            if (t is null) return;   // deleted mid-burst — nothing left to announce about

            // Milestones first: the goal is the headline, the added time is context.
            if (p.MilestoneLines.Count > 0)
                await PostFeedbackLineAsync(slug, string.Join(" · ", p.MilestoneLines)).ConfigureAwait(false);

            if (p.AddCount == 0) return;
            var cfg = t.Feedback?.Add;
            if (cfg is null || !cfg.Enabled) return;   // switched off mid-burst

            long seconds = p.AddedMs / 1000;
            // The threshold gates BOTH halves: an add not worth a chat line is not worth
            // an overlay pop either, and the compositor holds every invocation for two
            // seconds per widget, so a trickle of tiny adds would queue up for minutes.
            long minSeconds = Math.Max(0, t.Feedback?.AddMinSeconds ?? 0);
            if (seconds < minSeconds) return;

            long displayMs = 0;
            lock (_gate) { if (_timers.TryGetValue(slug, out var live)) displayMs = DisplayMs(live); }

            var tokens = BaseTokens(t);
            // {source} / {seconds} / {remaining} are FireAddAsync's own keys. {clock} is
            // the same remaining value through FormatDuration — the identical string the
            // timer.<name>.clock live-channel key publishes, so overlay.get reaches it
            // from a graph too. {count} is the coalescer's own aggregate; a graph reaches
            // the same number by counting Timer.OnAdd fires.
            tokens["source"] = p.AddSourcesDiffer ? "multiple" : (p.AddSource ?? "");
            tokens["seconds"] = seconds.ToString(CultureInfo.InvariantCulture);
            tokens["count"] = p.AddCount.ToString(CultureInfo.InvariantCulture);
            tokens["remaining"] = (displayMs / 1000).ToString(CultureInfo.InvariantCulture);
            tokens["clock"] = FormatDuration(displayMs, "clock");

            await PostFeedbackLineAsync(slug, RenderFeedback(cfg.Message, tokens)).ConfigureAwait(false);
            await FireFeedbackVisualAsync(slug, cfg.LayerId, cfg.TriggerName,
                "TIMER ADD", t.Name, FormatDuration(p.AddedMs, "clock")).ConfigureAwait(false);
        }

        // Fire-and-forget: on_event(Timer.On*) handlers must never stall the 1 Hz
        // tick (or the calling event dispatch) behind a slow script. SafeRunAsync
        // routes faults to the log and swallows shutdown cancellation.
        private void RaiseScript(string phoenixEvent, IReadOnlyDictionary<string, string> vars)
        {
            var rse = RaiseScriptEvent;
            if (rse is null) return;
            _ = AsyncErrorBoundary.SafeRunAsync(() => rse(phoenixEvent, vars),
                "TimerService", $"RaiseScriptEvent({phoenixEvent})");
        }

        private async Task BusEmitAsync(string busType, object payload)
        {
            var be = BusEmit;
            if (be is null) return;
            try { await be(busType, JsonSerializer.Serialize(payload, CloneOpts)).ConfigureAwait(false); }
            catch (Exception ex) { GlobalLogger.Error("TimerService", $"BusEmit({busType}) failed", ex); }
        }
    }
}
