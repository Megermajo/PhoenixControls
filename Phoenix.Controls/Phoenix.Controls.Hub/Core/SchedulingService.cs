using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// Outcome of one <see cref="SchedulingService.ChatSend"/> call. The seam's real
    /// failure modes — no Streamer.bot chat action configured, the link dropping after
    /// the tick's connectivity pre-check, a body over Twitch's 500-char cap — all DROP
    /// the line after logging and NEVER throw, so an absent exception says nothing about
    /// whether chat saw the message. This result is the only honest signal, and the tick
    /// counts a fire only when <see cref="Sent"/> is true.
    /// </summary>
    public readonly record struct SchedulingSendResult(bool Sent, string Reason)
    {
        /// <summary>The line reached the send path.</summary>
        public static SchedulingSendResult Ok() => new(true, "");

        /// <summary>The line was dropped; <paramref name="reason"/> is folded into the
        /// sentence the panel shows and the System Log records — once per distinct failure
        /// PER SCHEDULE, so a second broken schedule neither silences nor repeats the first.</summary>
        public static SchedulingSendResult Failed(string reason) => new(false, reason ?? "");
    }

    // SchedulingService — the always-on runtime for the Scheduling pre-build tool
    // (StreamElements-style "Timers": post a recurring chat line every N minutes,
    // optionally gated on chat activity, only while live).
    //
    // Shape mirrors TimerService/LoyaltyService (the always-on-capable, self-gated
    // family — NOT the opt-in-service family): the 1 Hz tick loop is started
    // unconditionally at boot, and the master Config.Enabled toggle gates BEHAVIOUR
    // inside the tick, not the loop's existence. So the tool is fully dormant (zero
    // posts, zero side effects) until the streamer enables it.
    //
    // Hub-state-agnostic on purpose (like TimerService): it does NOT resolve message
    // tokens or touch WS directly beyond a connectivity pre-check — it hands the raw
    // message + fire count to the ChatSend seam, which ScriptManager.Scheduling.cs wires
    // to token-resolution + the blessed SendTwitchChatCore path, and reports back whether
    // the line actually reached chat (a dropped line is never counted as a fire).
    //
    // NOT the same thing as SchedulerService (the cron/on_interval SCRIPT-firing engine)
    // — different concern, different name deliberately.
    public sealed class SchedulingService
    {
        private readonly DB _db;
        public SchedulingService(DB db) => _db = db ?? throw new ArgumentNullException(nameof(db));

        private static SchedulingService? _instance;
        private static readonly object _instanceGate = new();
        public static SchedulingService Instance
        {
            get
            {
                var i = _instance;
                if (i != null) return i;
                lock (_instanceGate) return _instance ??= new SchedulingService(DB.Instance);
            }
        }

        // ── Seams (wired by ScriptManager.Scheduling.cs; null-safe) ──────────
        /// <summary>Post one resolved line to chat. Args: (rawMessage, fireCount);
        /// returns whether the line actually reached the send path. The seam owns token
        /// resolution ({count}/{random}/{uptime}/{channel}) + the actual send. Null until
        /// wired; a null seam makes the tick a no-op.</summary>
        public Func<string, int, Task<SchedulingSendResult>>? ChatSend { get; set; }

        // ── Config (swapped wholesale; volatile ⇒ visible on the tick thread) ─
        private volatile SchedulingConfig _config = new();
        public SchedulingConfig Config => _config;
        /// <summary>Master gate — true only when the streamer enabled the tool.</summary>
        public bool Active => _config.Enabled;

        // ── Change events (crash-safe SafeEvent; UI-side MUST marshal via UiDispatcherPump) ─
        /// <summary>Raised after the config is swapped (master toggle / schedule edits).</summary>
        public event EventHandler? ConfigChanged;
        /// <summary>Raised after a schedule posts (so the UI can refresh fire counts / stats).</summary>
        public event EventHandler? RuntimeChanged;

        private void RaiseConfigChanged()
            => SafeEvent.Raise(ConfigChanged, this, EventArgs.Empty, "SchedulingService", "ConfigChanged");
        private void RaiseRuntimeChanged()
            => SafeEvent.Raise(RuntimeChanged, this, EventArgs.Empty, "SchedulingService", "RuntimeChanged");

        // ── Live-state (mirrors TimerService: defaults live so no-detection ⇒ posts) ─
        private volatile bool _streamLive = true;
        /// <summary>Wired from ScriptManager StreamOnline/Offline (beside Timer/Loyalty).
        /// Defaults true, so a setup with no live-detection posts whenever connected.</summary>
        public void SetStreamLive(bool live)
        {
            bool wasLive = _streamLive;
            _streamLive = live;
            // Going live re-arms all schedules — but ONLY when OnlyWhenLive is on (i.e. the
            // live-gate is what froze posting while offline). Otherwise a schedule that came
            // due during a long offline stretch would burst on the first live tick. With
            // OnlyWhenLive off, posting was continuous, so a re-arm would wrongly reset it.
            if (live && !wasLive && _config.OnlyWhenLive)
                ReArmAllSchedules();
        }

        // ── Chat activity counter (feeds the per-schedule MinChatLines gate) ──
        // Monotonic count of inbound (non-bot) chat messages; incremented from
        // ScriptManager.ExecuteOnChatScriptsAsync. Counted unconditionally (cheap
        // Interlocked) so the gate is warm the instant the tool is enabled.
        private long _chatLineCount;
        public void NoteChatActivity() => Interlocked.Increment(ref _chatLineCount);

        // Monotonic all-session post total (drives the "messages sent" stat). Kept separate
        // from per-schedule FireCount so deleting a schedule doesn't drop the aggregate.
        // Written under _gate on a LANDED post; read under _gate in TotalFires.
        private int _totalPostsEver;

        // Aggregate panel hint: why posting is currently unhealthy ("" while every schedule
        // is landing). RECOMPUTED from the per-schedule runtimes on each outcome — never
        // carried over as "the last string written" — because with several schedules in
        // flight the last write is not the state of the tool: one healthy schedule would
        // otherwise clear a hint two broken ones still need. Written under _gate, read
        // lock-free by the panel (volatile ⇒ the UI thread sees the swap).
        private volatile string _lastSendFailure = "";

        /// <summary>Empty while EVERY schedule is posting; otherwise the reason one of the
        /// currently-failing schedules was dropped (chat action not configured, link down,
        /// over the 500-char cap) — most recent attempt wins when several are broken.
        /// Clears only once no schedule is left in a failed state. Purely informational —
        /// the panel shows it as a passive hint and nothing in the tool blocks on it.</summary>
        public string LastSendFailure => _lastSendFailure;

        // A schedule whose send was DROPPED keeps its interval clock un-advanced (a drop is
        // not a fire), so it stays overdue and would re-attempt on every 1 Hz tick. Back off
        // to one attempt per minute — matching the interval floor — so a permanently
        // misconfigured chat action can't turn the tick loop into a retry spinner, while a
        // fixed configuration still posts within a minute instead of after a full interval.
        internal const long FailedSendRetryMs = 60_000L;

        // ── Per-schedule ephemeral runtime state (keyed by ScheduleItem.Id) ───
        private sealed class ScheduleRuntime
        {
            public long LastPostMs;          // monotonic clock at arm / last LANDED post
            public long LineCountAtLastPost;  // _chatLineCount snapshot at arm / last landed post
            public int FireCount;             // times this schedule has posted ({count})
            public bool WasEnabled;           // to detect disabled→enabled re-arm
            public bool LastSendFailed;       // last attempt was dropped → retry back-off
            public long LastAttemptMs;        // monotonic clock at that dropped attempt
            // Why THIS schedule's last attempt was dropped ("" while healthy), and the
            // reason already written to the System Log for it. Both are PER SCHEDULE on
            // purpose: the de-dup used to hang off one service-wide string, so two broken
            // schedules produced two different sentences that each looked "new" to the
            // other and re-logged forever. See CommitSendOutcome.
            public string LastFailureReason = "";
            public string LastLoggedReason = "";

            // Re-arming (or switching the schedule off) restarts its clock, so an earlier
            // drop no longer describes the present: forget it, and let a genuinely new
            // outage report itself once more instead of being swallowed by the de-dup.
            public void ClearSendFailure()
            {
                LastSendFailed = false;
                LastFailureReason = "";
                LastLoggedReason = "";
            }
        }

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _gate = new();
        private readonly Dictionary<string, ScheduleRuntime> _state = new(StringComparer.Ordinal);
        // Last config instance the tick verified _state against (orphan prune). Guarded by _gate.
        private SchedulingConfig? _prunedAgainst;

        // ── Loop lifecycle (identical shape to TimerService) ─────────────────
        private readonly object _loopGate = new();
        private bool _loopStarted;
        private CancellationTokenSource? _loopCts;
        private Task? _loopTask;

        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        // ─────────────────────────────────────────────────────────────────────
        public async Task InitializeAsync()
        {
            try
            {
                string? json = await _db.LoadSchedulingConfigAsync().ConfigureAwait(false);
                _config = string.IsNullOrWhiteSpace(json)
                    ? new SchedulingConfig()
                    : (JsonSerializer.Deserialize<SchedulingConfig>(json!, JsonOpts) ?? new SchedulingConfig());
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("SchedulingService", "config load failed", ex);
                _config = new SchedulingConfig();
            }

            // Arm every schedule from boot so the first fire is one full interval away
            // (a fresh start never bursts catch-up posts).
            long now = _clock.ElapsedMilliseconds;
            long lines = Interlocked.Read(ref _chatLineCount);
            lock (_gate)
            {
                _state.Clear();
                foreach (var s in _config.Schedules)
                {
                    if (string.IsNullOrEmpty(s.Id)) continue;
                    _state[s.Id] = new ScheduleRuntime
                    {
                        LastPostMs = now,
                        LineCountAtLastPost = lines,
                        FireCount = 0,
                        WasEnabled = s.Enabled,
                    };
                }
                _prunedAgainst = _config;   // freshly built from this exact config
                // The hint is a projection of the runtimes we just replaced wholesale.
                _lastSendFailure = "";
            }
            RaiseConfigChanged();
        }

        public void StartTicking()
        {
            lock (_loopGate)
            {
                if (_loopStarted) return;
                _loopStarted = true;
                _loopCts = new CancellationTokenSource();
                var ct = _loopCts.Token;
                _loopTask = Task.Run(() => TickLoopAsync(ct));
            }
            GlobalLogger.Log("SchedulingService tick loop started (1 Hz).", "SchedulingService", LogLevel.System);
        }

        public async Task ShutdownAsync()
        {
            CancellationTokenSource? cts;
            Task? loop;
            lock (_loopGate)
            {
                cts = _loopCts;
                loop = _loopTask;
                _loopStarted = false;
                _loopCts = null;
                _loopTask = null;
            }
            try { cts?.Cancel(); } catch { }
            if (loop is not null)
            {
                try { await loop.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { GlobalLogger.Error("SchedulingService", "loop drain failed", ex); }
            }
            cts?.Dispose();
        }

        private async Task TickLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                try { await TickOnceAsync().ConfigureAwait(false); }
                catch (Exception ex) { GlobalLogger.Error("SchedulingService", "tick failed", ex); }
            }
        }

        private async Task TickOnceAsync()
        {
            var cfg = _config;                                   // snapshot the volatile ref
            if (!cfg.Enabled) return;                            // master gate = behaviour only
            if (cfg.OnlyWhenLive && !_streamLive) return;        // live gate

            // Connectivity gate. Read ONCE, and take the disconnected→connected edge before
            // applying it: this is the THIRD gate that freezes the free-running clock
            // relative to posting, so it needs the same re-arm the master and live gates
            // already have (ApplyConfig's masterTurningOn, SetStreamLive's go-live edge).
            // Without it every schedule whose interval elapsed during a Streamer.bot outage
            // is still overdue when the socket returns and they all drain back-to-back at
            // one per tick — eight promo lines in eight seconds, exactly the burst the
            // other two re-arms exist to prevent.
            //
            // Ordered AFTER the two gates above on purpose: while either of those is
            // closed the tick returns before this runs, so a reconnect that happens while
            // the tool is off or the stream is offline is covered by that gate's own
            // re-arm rather than being consumed here.
            bool connected = WS.Instance.IsConnected;
            NoteConnectivityForTick(connected, _clock.ElapsedMilliseconds, Interlocked.Read(ref _chatLineCount));
            if (!connected) return;                              // sends would silently drop; skip quietly
            if (ChatSend is null) return;                        // seam not wired yet — don't claim a fire before we can post it

            // _clock is monotonic — no wall-clock drift.
            await TickCoreAsync(cfg, _clock.ElapsedMilliseconds).ConfigureAwait(false);
        }

        /// <summary>The gate-free half of one tick (extracted for unit testing — the caller
        /// owns the master / live / connectivity gates and the clock). Picks at most ONE due
        /// schedule, sends it, and commits its post state only if the send actually landed.</summary>
        internal async Task TickCoreAsync(SchedulingConfig cfg, long now)
        {
            long lines = Interlocked.Read(ref _chatLineCount);

            // Find the FIRST schedule that is due, post it, and stop — one post per tick
            // gives a natural 1 s stagger so a burst of simultaneously-due schedules can't
            // trip Twitch's rate limit. The rest stay due (their LastPostMs is untouched)
            // and drain over the following ticks.
            string? dueId = null;
            string? dueMessage = null;
            string dueLabel = "";
            int dueFireCount = 0;
            lock (_gate)
            {
                // Is the snapshot we entered the tick with still the live config? A stale
                // snapshot may name schedules a concurrent delete already removed, so it
                // must not seed runtime state (see the arm-on-miss branch below).
                bool cfgIsLive = ReferenceEquals(cfg, _config);
                if (cfgIsLive && !ReferenceEquals(cfg, _prunedAgainst))
                {
                    // Once per config swap, re-assert the invariant "one _state entry per
                    // live schedule". ApplyConfig prunes against the config IT installs;
                    // this is the standing check that nothing else leaked an entry.
                    PruneOrphanState(cfg);
                    _prunedAgainst = cfg;
                }

                foreach (var s in cfg.Schedules)
                {
                    if (!s.Enabled || string.IsNullOrEmpty(s.Id) || string.IsNullOrWhiteSpace(s.Message)) continue;
                    if (!_state.TryGetValue(s.Id, out var rt))
                    {
                        // Defensive: a schedule present in config but not yet armed (e.g. a
                        // race between UpdateConfig and this tick) — arm it now, fire next
                        // time. Skipped on a stale snapshot: arming there would resurrect
                        // state for a just-deleted schedule that nothing prunes again.
                        if (cfgIsLive)
                            _state[s.Id] = new ScheduleRuntime { LastPostMs = now, LineCountAtLastPost = lines, FireCount = 0, WasEnabled = true };
                        continue;
                    }

                    if (!IsScheduleDue(now, rt.LastPostMs, s.IntervalMinutes, lines, rt.LineCountAtLastPost, s.MinChatLines))
                        continue;
                    // A schedule whose last send was dropped is still overdue (its clock was
                    // never advanced) — hold it for the back-off instead of retrying every tick.
                    if (rt.LastSendFailed && now - rt.LastAttemptMs < FailedSendRetryMs)
                        continue;

                    // Due — take it, but claim NOTHING yet: the counters move in
                    // CommitSendOutcome, and only if the line actually reaches chat.
                    dueId = s.Id;
                    dueMessage = s.Message;
                    dueLabel = DescribeSchedule(s);
                    dueFireCount = rt.FireCount + 1;   // provisional {count} for this attempt
                    break;
                }
            }

            if (dueId is null || dueMessage is null) return;

            var send = ChatSend;
            if (send is null) return;
            SchedulingSendResult result;
            try { result = await send(dueMessage, dueFireCount).ConfigureAwait(false); }
            catch (Exception ex)
            {
                GlobalLogger.Error("SchedulingService", "chat send failed", ex);
                result = SchedulingSendResult.Failed(ex.Message);
            }

            CommitSendOutcome(dueId, dueLabel, result, now, lines);
            RaiseRuntimeChanged();
        }

        // Apply one send's outcome. A LANDED post advances the interval clock, the chat-activity
        // baseline and both fire counters; a DROPPED post advances none of them — the tool must
        // never claim a fire nobody saw — and only records the back-off + the reason the panel
        // shows. The failure line is logged on a PER-SCHEDULE transition: a chat action left
        // unset would otherwise re-log the identical sentence every back-off cycle, forever.
        //
        // The de-dup MUST hang off the schedule, not off one service-wide string. It used to
        // diff against _lastSendFailure, and every schedule writes a DIFFERENT sentence (the
        // label is in it): two broken schedules each looked "new" to the other and both
        // re-logged on every cycle, while any healthy schedule wrote "" over the field and
        // announced a recovery the still-broken ones flatly contradicted. Per-schedule
        // LastLoggedReason gives one line per outage per schedule, and the aggregate panel
        // hint is derived from who is CURRENTLY failing (CurrentFailureHint).
        private void CommitSendOutcome(string id, string label, SchedulingSendResult result, long now, long lines)
        {
            string? failureToLog = null;
            string? recoveryToLog = null;
            // The activity row is BUILT under the gate (it needs the post-increment fire
            // count) and RECORDED after it. Nothing in the ring is worth holding this lock
            // one instruction longer than the state it protects.
            string? activityKind = null;
            string? activityMessage = null;
            lock (_gate)
            {
                string reason = result.Sent ? "" : $"{label} was not posted — {result.Reason}";
                // No runtime entry means the schedule was deleted while its send was in
                // flight — commit nothing and say nothing about a schedule that is gone.
                if (_state.TryGetValue(id, out var rt))
                {
                    if (result.Sent)
                    {
                        rt.LastPostMs = now;
                        rt.LineCountAtLastPost = lines;
                        rt.FireCount++;
                        rt.LastSendFailed = false;
                        _totalPostsEver++;
                        activityKind = "POST";
                        activityMessage = $"{label} posted (#{rt.FireCount.ToString(CultureInfo.InvariantCulture)} this session).";
                    }
                    else
                    {
                        rt.LastSendFailed = true;
                        rt.LastAttemptMs = now;
                        // The same sentence the System Log gets, clipped: result.Reason can
                        // be an exception message, which nothing upstream bounds.
                        activityKind = "DROP";
                        activityMessage = ClipForActivity(reason);
                    }
                    rt.LastFailureReason = reason;

                    if (!string.Equals(reason, rt.LastLoggedReason, StringComparison.Ordinal))
                    {
                        if (reason.Length > 0) failureToLog = reason;
                        else if (rt.LastLoggedReason.Length > 0) recoveryToLog = $"{label} is reaching chat again.";
                        rt.LastLoggedReason = reason;
                    }
                }

                _lastSendFailure = CurrentFailureHint();
            }

            if (activityKind is not null && activityMessage is not null)
                RecordActivity(activityKind, activityMessage);

            if (failureToLog is not null)
                GlobalLogger.Log(failureToLog, "Scheduling", LogLevel.CriticalError);
            if (recoveryToLog is not null)
                GlobalLogger.Log(recoveryToLog, "Scheduling", LogLevel.System);
        }

        // ── Panel activity feed ─────────────────────────────────────────────
        /// <summary>The key this tool's rows carry in <see cref="ToolActivityRing"/>. A
        /// const rather than a literal at each site so the panel reads the same string the
        /// service writes.</summary>
        public const string ActivityTool = "Scheduling";

        // Free text that reaches a row is clipped here and nowhere else. Nothing upstream
        // bounds a seam's failure message, and a row is rendered in a fixed-width column.
        private const int ActivityMessageMaxChars = 200;

        private static string ClipForActivity(string? text)
        {
            string t = (text ?? string.Empty).Trim();
            return t.Length <= ActivityMessageMaxChars ? t : t[..ActivityMessageMaxChars].TrimEnd() + "...";
        }

        // Recording is OBSERVATION. It runs on the tick thread, inside the commit path of a
        // real post, so a fault in it must not become a fault in the send: swallow to the
        // System Log exactly like the other best-effort side-channels in this file.
        private static void RecordActivity(string kind, string message)
        {
            try { ToolActivityRing.Record(ActivityTool, kind, message); }
            catch (Exception ex) { GlobalLogger.Error("SchedulingService", "activity record failed", ex); }
        }

        // ── Status pill ─────────────────────────────────────────────────────
        /// <summary>
        /// What the strip's status pill says, decided service-side so the panel reads ONE
        /// state instead of recomputing it from five fields.
        ///
        /// Every state below is something the master switch does not already say: that
        /// posting is FAILING (and why), that the tool is armed but held by its own
        /// live-gate, or that it is on with nothing enabled to post.
        /// </summary>
        public enum SchedulingPillState
        {
            /// <summary>The tool is switched off.</summary>
            Dormant,
            /// <summary>At least one schedule's last attempt was dropped —
            /// <see cref="LastSendFailure"/> is the reason.</summary>
            Failing,
            /// <summary>On, healthy, but the ONLY-WHILE-LIVE gate is holding every post
            /// because the stream is offline.</summary>
            ArmedWaitingForLive,
            /// <summary>On, but no enabled schedule can post (none enabled, or every one of
            /// them has an empty message).</summary>
            NothingConfigured,
            /// <summary>On, ungated, and at least one schedule is posting.</summary>
            Posting,
        }

        /// <summary>Pure state machine behind <see cref="PillState"/> — no clock, no WS, no
        /// config read, so it is testable directly.</summary>
        internal static SchedulingPillState ComputePillState(
            bool enabled, bool anyFailing, bool onlyWhenLive, bool streamLive, int postableScheduleCount)
        {
            if (!enabled) return SchedulingPillState.Dormant;
            // A failure outranks the two "nothing is happening" states: it is the only one
            // that means something the streamer set up has stopped working.
            if (anyFailing) return SchedulingPillState.Failing;
            if (onlyWhenLive && !streamLive) return SchedulingPillState.ArmedWaitingForLive;
            if (postableScheduleCount == 0) return SchedulingPillState.NothingConfigured;
            return SchedulingPillState.Posting;
        }

        /// <summary>The live pill state. Reads the config snapshot, the live latch and the
        /// aggregate failure hint — all fields this service already owns.</summary>
        public SchedulingPillState PillState
        {
            get
            {
                var cfg = _config;
                return ComputePillState(cfg.Enabled, _lastSendFailure.Length > 0,
                                        cfg.OnlyWhenLive, _streamLive, PostableScheduleCount(cfg));
            }
        }

        /// <summary>Schedules that could actually post: enabled, with an id and a non-blank
        /// message — the same three conditions the tick's due-scan applies before it will
        /// look at a schedule at all.</summary>
        internal static int PostableScheduleCount(SchedulingConfig cfg)
        {
            if (cfg?.Schedules is null) return 0;
            int n = 0;
            foreach (var s in cfg.Schedules)
                if (s is not null && s.Enabled && !string.IsNullOrEmpty(s.Id) && !string.IsNullOrWhiteSpace(s.Message)) n++;
            return n;
        }

        // The aggregate panel hint, derived from state rather than remembered: the reason of
        // whichever schedule is CURRENTLY failing (most recent attempt wins when several are),
        // "" when none is. Caller holds _gate.
        private string CurrentFailureHint()
        {
            string hint = "";
            long newest = long.MinValue;
            foreach (var rt in _state.Values)
            {
                if (!rt.LastSendFailed || rt.LastFailureReason.Length == 0) continue;
                if (rt.LastAttemptMs < newest) continue;
                newest = rt.LastAttemptMs;
                hint = rt.LastFailureReason;
            }
            return hint;
        }

        // How much of a schedule's message stands in for a missing name. Matches
        // ScheduleRowVm.TitleEyebrow so the log line and the row header agree.
        private const int LabelPreviewChars = 32;

        /// <summary>How a schedule is named in the System Log and the panel's failure hint.
        /// <see cref="ScheduleItem.Name"/> is OPTIONAL and defaults to "", so unnamed is the
        /// NORMAL state — never fall back to <see cref="ScheduleItem.Id"/>, which is a 32-char
        /// undashed GUID and tells a streamer nothing. Mirrors the row VM's fallback
        /// (ScheduleRowVm.TitleEyebrow): a trimmed message preview, else a plain phrase.
        /// A named / previewed label comes back QUOTED so it reads as a title inside the
        /// surrounding sentence; the last-resort phrase is deliberately unquoted.</summary>
        internal static string DescribeSchedule(ScheduleItem s)
        {
            if (s is null) return "an unnamed schedule";
            string name = (s.Name ?? "").Trim();
            if (name.Length > 0) return $"\"{name}\"";
            string msg = (s.Message ?? "").Trim();
            if (msg.Length == 0) return "an unnamed schedule";
            return msg.Length <= LabelPreviewChars
                ? $"\"{msg}\""
                : $"\"{msg.Substring(0, LabelPreviewChars)}…\"";
        }

        // Drop runtime state for schedules the config no longer has. Caller holds _gate.
        private void PruneOrphanState(SchedulingConfig cfg)
        {
            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in cfg.Schedules)
                if (!string.IsNullOrEmpty(s.Id)) live.Add(s.Id);

            List<string>? stale = null;
            foreach (var key in _state.Keys)
                if (!live.Contains(key)) (stale ??= new List<string>()).Add(key);
            if (stale is null) return;
            foreach (var key in stale) _state.Remove(key);
            // The hint is a projection of the surviving runtimes — deleting the one broken
            // schedule must retire its sentence, not leave the panel accusing a ghost.
            _lastSendFailure = CurrentFailureHint();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Config mutation (the UI's single write path)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Swaps in a new config (deep-owned by the caller), re-arms new /
        /// just-enabled / newly-made-due schedules so they wait a full interval before
        /// firing, drops runtime state for removed schedules, persists, and notifies.</summary>
        public async Task UpdateConfigAsync(SchedulingConfig newConfig)
        {
            if (newConfig is null) return;

            ApplyConfig(newConfig, _clock.ElapsedMilliseconds, Interlocked.Read(ref _chatLineCount));

            try
            {
                string json = JsonSerializer.Serialize(newConfig, JsonOpts);
                await _db.SaveSchedulingConfigAsync(json, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("SchedulingService", "config save failed", ex);
            }
            RaiseConfigChanged();
        }

        /// <summary>The in-memory half of <see cref="UpdateConfigAsync"/> — no DB, no events
        /// (extracted so the arm / re-arm / prune rules are unit-testable against an explicit
        /// clock). Arms new schedules, re-arms the three transitions that must never produce
        /// an instant post, prunes state for removed schedules, and swaps the live config.
        /// The swap happens INSIDE <c>_gate</c> so a concurrent tick can trust its
        /// reference-equality check against <c>_config</c>.</summary>
        internal void ApplyConfig(SchedulingConfig newConfig, long now, long lines)
        {
            if (newConfig is null) return;
            lock (_gate)
            {
                var old = _config;
                // Master OFF→ON: turning the tool on must re-arm ALL schedules so none is
                // treated as overdue from the time it spent gated off (the clock free-runs
                // while master-gated). Mirrors the go-live re-arm in SetStreamLive.
                bool masterTurningOn = !old.Enabled && newConfig.Enabled;

                // Previous defs by id — the third re-arm edge below weighs a schedule's
                // due-ness under its OLD settings against its NEW ones.
                var previous = new Dictionary<string, ScheduleItem>(StringComparer.Ordinal);
                foreach (var s in old.Schedules)
                    if (!string.IsNullOrEmpty(s.Id)) previous[s.Id] = s;

                var live = new HashSet<string>(StringComparer.Ordinal);
                foreach (var s in newConfig.Schedules)
                {
                    if (string.IsNullOrEmpty(s.Id)) continue;
                    live.Add(s.Id);
                    if (!_state.TryGetValue(s.Id, out var rt))
                    {
                        // New schedule — arm from now.
                        _state[s.Id] = new ScheduleRuntime { LastPostMs = now, LineCountAtLastPost = lines, FireCount = 0, WasEnabled = s.Enabled };
                        continue;
                    }

                    // Third edge: an edit that makes an ALREADY-overdue schedule due right
                    // now — the interval shortened past the time already elapsed, or the
                    // MinChatLines gate lowered/cleared — would otherwise post on the very
                    // next tick, a surprise message triggered by typing in a settings box.
                    // Compare the due-decision under the old def against the new one at the
                    // same instant, so ONLY edits that flipped it not-due→due re-arm.
                    bool madeDueByEdit = false;
                    if (s.Enabled && previous.TryGetValue(s.Id, out var was))
                        madeDueByEdit =
                            !IsScheduleDue(now, rt.LastPostMs, was.IntervalMinutes, lines, rt.LineCountAtLastPost, was.MinChatLines)
                          && IsScheduleDue(now, rt.LastPostMs, s.IntervalMinutes,   lines, rt.LineCountAtLastPost, s.MinChatLines);

                    // Re-arm on a disabled→enabled transition (or a master OFF→ON) so
                    // re-enabling never posts instantly; message-only edits keep the
                    // running clock.
                    if (masterTurningOn || (s.Enabled && !rt.WasEnabled) || madeDueByEdit)
                    {
                        rt.LastPostMs = now;
                        rt.LineCountAtLastPost = lines;
                        rt.ClearSendFailure();
                    }
                    else if (!s.Enabled && rt.WasEnabled)
                    {
                        // Switched off mid-outage: nothing will ever retry it, so without
                        // this its sentence would hold the panel hint for the rest of the
                        // session with no schedule left that could clear it.
                        rt.ClearSendFailure();
                    }
                    rt.WasEnabled = s.Enabled;
                }
                // Drop state for removed schedules.
                var stale = new List<string>();
                foreach (var key in _state.Keys) if (!live.Contains(key)) stale.Add(key);
                foreach (var key in stale) _state.Remove(key);
                // Re-derive the hint: this pass may have re-armed, disabled or deleted the
                // very schedule it was describing.
                _lastSendFailure = CurrentFailureHint();

                _config = newConfig;
                _prunedAgainst = newConfig;   // just verified — the tick needn't re-scan it
            }
        }

        /// <summary>Live count of per-schedule runtime entries. Test seam for the
        /// "one entry per live schedule, no orphans" invariant.</summary>
        internal int TrackedStateCount
        {
            get { lock (_gate) return _state.Count; }
        }

        /// <summary>Times this schedule has posted since Hub start (for the UI). 0 if unknown.</summary>
        public int GetFireCount(string id)
        {
            if (string.IsNullOrEmpty(id)) return 0;
            lock (_gate) return _state.TryGetValue(id, out var rt) ? rt.FireCount : 0;
        }

        /// <summary>Total posts since Hub start (for the stat strip). Monotonic — a deleted
        /// schedule's history is NOT subtracted (unlike a live sum over per-schedule state).</summary>
        public int TotalFires()
        {
            lock (_gate) return _totalPostsEver;
        }

        // Last connectivity verdict the tick acted on. Owned by the single tick loop
        // (TickOnceAsync is only ever reached from TickLoopAsync) plus the test seam, so a
        // plain field is enough. Starts FALSE so a Hub that came up before Streamer.bot
        // re-arms on the first connected tick instead of treating the pre-connection wait
        // as elapsed interval.
        private bool _wasConnected;

        /// <summary>The connectivity gate's edge half (extracted for unit testing — the
        /// caller owns the WS read and the clock). Records the verdict and, on the
        /// disconnected→connected edge, runs the overdue-only re-arm sweep. Returns true
        /// when that edge was detected — the sweep ran, though it may have reset zero
        /// schedules, since only ones already overdue are touched.</summary>
        internal bool NoteConnectivityForTick(bool connected, long now, long lines)
        {
            bool was = _wasConnected;
            _wasConnected = connected;
            if (!connected || was) return false;
            ReArmAllSchedules(now, lines);
            return true;
        }

        // Re-arm the schedules that are ALREADY OVERDUE: reset their interval + activity
        // baselines to "now" so each waits a full interval instead of bursting the moment
        // the gate opens. Schedules not yet due keep their running clocks untouched (the
        // starvation fix — rationale in the overload below); only their send-failure
        // hints are cleared. This parameterless form serves the offline→live gate
        // (SetStreamLive); the disconnected→connected edge calls the (now, lines)
        // overload via NoteConnectivityForTick, and the master OFF→ON re-arm lives
        // inline in ApplyConfig.
        private void ReArmAllSchedules()
            => ReArmAllSchedules(_clock.ElapsedMilliseconds, Interlocked.Read(ref _chatLineCount));

        private void ReArmAllSchedules(long now, long lines)
        {
            // ── Only re-arm what is ACTUALLY OVERDUE ─────────────────────────
            // The burst this exists to prevent is real, but resetting EVERY schedule
            // unconditionally starved them instead.
            //
            // The connectivity edge is the killer: Streamer.bot reconnects are routine,
            // and each one pushed LastPostMs to "now" for every schedule. IsScheduleDue
            // is purely `now - lastPostMs >= intervalMs` with no notion of accumulated
            // wait, so a 30- or 60-minute schedule on a link that blips more often than
            // its interval had its clock reset before the interval could ever elapse —
            // and never posted at all. Which is exactly the report: "Schedule does not
            // execute at all ... it does not post."
            //
            // A schedule that is NOT yet due has no backlog to burst, so touching it is
            // pure loss. One that IS overdue keeps the original treatment — reset to
            // now, wait a full interval — so the anti-burst behaviour is unchanged
            // wherever it was ever needed.
            //
            // The same reasoning covers LineCountAtLastPost. During a disconnect no chat
            // frames arrive at all (_chatLineCount only advances from the chat dispatch),
            // so `lines` at reconnect equals `lines` at disconnect: rewriting the
            // baseline banked nothing and simply discarded every line accrued BEFORE the
            // outage, starving any schedule with a MinChatLines gate on its own.
            lock (_gate)
            {
                foreach (var kv in _state)
                {
                    var rt = kv.Value;

                    long intervalMs = ScheduleIntervalMsLocked(kv.Key);
                    bool overdue = intervalMs > 0 && (now - rt.LastPostMs) >= intervalMs;

                    if (overdue)
                    {
                        rt.LastPostMs = now;
                        rt.LineCountAtLastPost = lines;
                    }

                    // Unconditional: the clock restarted, so a drop from before the gate
                    // closed no longer describes the present. (Same reasoning as the
                    // re-arm in ApplyConfig.) This never postpones a post.
                    rt.ClearSendFailure();
                }
                _lastSendFailure = CurrentFailureHint();
            }
        }

        /// <summary>
        /// Interval of one schedule in ms, or 0 when it is unknown / not configured.
        /// Call under <c>_gate</c>. Mirrors the 1-minute floor <see cref="IsScheduleDue"/>
        /// applies, so "overdue" means the same thing in both places.
        /// </summary>
        private long ScheduleIntervalMsLocked(string id)
        {
            var list = _config?.Schedules;
            if (list is null || string.IsNullOrEmpty(id)) return 0;
            foreach (var s in list)
                if (string.Equals(s.Id, id, StringComparison.Ordinal))
                    return Math.Max(1, s.IntervalMinutes) * 60_000L;
            return 0;
        }

        /// <summary>
        /// Pure due-decision for one schedule (extracted for unit testing). A schedule is
        /// due when the interval has elapsed AND — when a dead-chat gate is set — at least
        /// <paramref name="minChatLines"/> chat messages arrived since its last post. The
        /// interval is clamped to a 1-minute floor. When the gate isn't met the caller must
        /// NOT reset the schedule's clock, so it fires the first tick chat catches up.
        /// </summary>
        internal static bool IsScheduleDue(
            long nowMs, long lastPostMs, int intervalMinutes,
            long currentLines, long lineCountAtLastPost, int minChatLines)
        {
            long intervalMs = Math.Max(1, intervalMinutes) * 60_000L;
            if (nowMs - lastPostMs < intervalMs) return false;                       // interval not elapsed
            if (minChatLines > 0 && (currentLines - lineCountAtLastPost) < minChatLines) return false; // dead-chat gate
            return true;
        }
    }
}
