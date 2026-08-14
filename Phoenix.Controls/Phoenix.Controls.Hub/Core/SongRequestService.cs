using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // SongRequestService — the Hub-runtime brain of the Song Request pre-build tool: a
    // YouTube-backed viewer request queue with caps, an optional points price, an optional
    // pending-approval lane, vote-skipping, and the transport state an overlay player
    // obeys.
    //
    // ── What this service does and does NOT do ──────────────────────────────────
    // It owns the QUEUE and the INTENDED transport state (which track is selected,
    // playing/paused, at what volume) and publishes both onto the Overlay Live Channel
    // under `songrequest.*`. It does not play audio and it never pretends to: the actual
    // player is an iframe in the OBS overlay — V15's `Player.Embed` widget sink — which
    // subscribes to those keys. With no such widget running the tool is still completely
    // useful: requests are validated, charged, ordered and moderated, and the state is
    // published for whatever picks it up; nothing makes sound, and the panel says so
    // rather than showing a dead transport bar.
    //
    // ── The ONE thing the player tells us back ──────────────────────────────────
    // NotifyMediaEndedAsync, and nothing else. Hub cannot observe playback — it has no
    // position, no buffering state and no elapsed time, because those live inside a
    // cross-origin iframe. So the queue auto-advances on exactly one signal: the widget
    // reporting that the track it was playing finished. Everything else still requires an
    // explicit !play / !skip / panel action. In particular the queue is NEVER advanced on
    // a duration timer — that would move it as though something were playing, which is
    // exactly the lie this tool must not tell, and a track's real length is not knowable
    // here (DurationSeconds is metadata, and it is 0 whenever the lookup was unavailable).
    //
    // ── Two siblings are cloned here, not one ───────────────────────────────────
    //   * QuotesService — the singleton / volatile-config / Active / SaveConfigAsync
    //     (deep-clone) / Normalize / InitializeAsync shape, and the chat-command parser's
    //     contract (strip '!', split on the first space, ordinal-ignore-case compare, role
    //     gate through the User-Management overlay, and the load-bearing convention that a
    //     role-denied command is CONSUMED but silent).
    //   * UserManagementService's queue overlay pump — the honest live-channel heartbeat.
    //     A key published once with no cadence reports Active forever, so a finished queue
    //     would keep painting as live; every key here declares LiveInterval AND is
    //     republished on that cadence, and the store coalesces the unchanged writes so an
    //     idle tool costs one dictionary write per key per tick and nothing on the wire.
    //
    // Hub-state-agnostic like SchedulingService: the YouTube resolve and the points
    // charge/refund are SEAMS wired by ScriptManager.SongRequest.cs, so the parse / gate /
    // cap / queue core is unit-testable with no ScriptManager, no network and no databank.
    public sealed class SongRequestService
    {
        private readonly DB _db;
        public SongRequestService(DB db) => _db = db ?? throw new ArgumentNullException(nameof(db));

        private static SongRequestService? _instance;
        private static readonly object _instanceGate = new();
        public static SongRequestService Instance
        {
            get
            {
                var i = _instance;
                if (i != null) return i;
                lock (_instanceGate) return _instance ??= new SongRequestService(DB.Instance);
            }
        }

        // ── Config (swapped wholesale; volatile ⇒ visible on the pump thread) ─
        private volatile SongRequestConfig _config = new();
        public SongRequestConfig Config => _config;

        /// <summary>Master gate — false makes the chat commands, the queue mutations, the
        /// live-channel publish and the Song.On* events a total no-op. The Architect
        /// Song.* nodes go quiet with it too: unlike a databank read there is no
        /// tool-independent store underneath, so "the queue" simply does not exist while
        /// the tool is off.</summary>
        public bool Active => _config.Enabled;

        // ── Injected Hub-side seams ─────────────────────────────────────────
        /// <summary>
        /// The Overlay Live Channel this service publishes <c>songrequest.*</c> into.
        /// Defaults to the process-wide store. Public get / internal set mirrors
        /// CountersService.LiveStore: production cannot swap the channel at runtime, while
        /// the test assembly gives each test its OWN store instead of sharing one.
        /// </summary>
        public OverlayLiveStore LiveStore { get; internal set; } = OverlayLiveStore.Instance;

        /// <summary>Raises a Song.On* script event (wired in RegisterSongRequestCommands).</summary>
        public Action<string, IReadOnlyDictionary<string, string>>? RaiseScriptEvent { get; set; }

        /// <summary>
        /// Resolves a YouTube video id or a search phrase. Wired by
        /// ScriptManager.SongRequest.cs to the Data API v3 call, which goes through the
        /// SSRF-validated outbound path and reads the OPTIONAL streamer key from
        /// AppConfig. Null (or an unconfigured key) is a supported state, not a failure:
        /// links and bare ids keep working and only search is unavailable.
        /// </summary>
        public Func<SongLookupRequest, CancellationToken, Task<SongLookupResult>>? YouTubeLookup { get; set; }

        /// <summary>Charges a viewer the request price. Wired to LoyaltyService. Null while
        /// unwired, which the caller treats as EconomyOff — a configured price that cannot
        /// be collected refuses the request instead of quietly giving it away.</summary>
        public Func<string, long, Task<SongChargeResult>>? ChargePoints { get; set; }

        /// <summary>
        /// Gives a charged price back (removal / denial / clear). Wired to LoyaltyService.
        /// Returns TRUE only when the points genuinely landed back on the viewer's balance.
        ///
        /// ★ The result is the whole point of the signature. The wired implementation
        /// reports a refused refund — the Loyalty master toggle flipped off mid-session, its
        /// currency table renamed — as a RESULT and never as a throw, so a seam that
        /// returned bare <c>Task</c> turned "the viewer is 100 points out of pocket" into
        /// silence in every log and every chat line. Null while unwired counts as a failed
        /// refund for exactly the same reason.
        /// </summary>
        public Func<string, long, Task<bool>>? RefundPoints { get; set; }

        // ── Change notifications (UI) ───────────────────────────────────────
        public event EventHandler? ConfigChanged;
        /// <summary>Raised after any queue or transport change (chat, node or panel), so an
        /// open Song Request page stays live.</summary>
        public event EventHandler? QueueChanged;

        private void RaiseConfigChanged() => SafeEvent.Raise(ConfigChanged, this, EventArgs.Empty, "SongRequestService", "ConfigChanged");
        private void RaiseQueueChanged() => SafeEvent.Raise(QueueChanged, this, EventArgs.Empty, "SongRequestService", "QueueChanged");

        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
        private static long NowUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // ── Session state (NEVER persisted — see SongRequestModels.cs) ──────
        private readonly object _gate = new();
        private readonly List<SongRequestEntry> _queue = new();   // Pending + Queued, in order
        private SongRequestEntry? _current;
        private SongPlayerState _state = SongPlayerState.Idle;
        private int _volume = 50;
        private long _playToken;
        // V15 — the highest play_token an accepted MEDIA_ENDED has already been spent on.
        // -1 rather than 0 because 0 is the token of a session that has selected nothing:
        // no report can legitimately carry it, and starting AT a reachable value would
        // burn the very first selection. See NotifyMediaEndedAsync for why one long is the
        // whole dedupe and no set is needed.
        private long _mediaEndClaimedToken = -1;
        // Distinct voters for the CURRENT track only; cleared whenever the track changes.
        private readonly HashSet<string> _skipVotes = new(StringComparer.OrdinalIgnoreCase);
        // Per-viewer request cooldown, on a monotonic clock so a system clock change can't
        // strand a viewer for hours.
        private readonly Dictionary<string, long> _cooldownUntilMs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        // ── Lifecycle ───────────────────────────────────────────────────────
        public async Task InitializeAsync()
        {
            try
            {
                string? raw = await _db.LoadSongRequestConfigAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var cfg = JsonSerializer.Deserialize<SongRequestConfig>(raw!, JsonOpts);
                    if (cfg != null) _config = Normalize(cfg);
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("SongRequestService", "InitializeAsync: config load failed", ex);
            }

            lock (_gate) _volume = _config.Volume;

            // The heartbeat is started unconditionally (the TimerService/Scheduling
            // always-on family): the master toggle gates the publish BEHAVIOUR inside the
            // tick, not the loop's existence, so enabling the tool mid-stream starts
            // painting within one interval with no restart.
            StartOverlayPump();

            GlobalLogger.Log(
                $"SongRequestService online — tool {(Active ? "ENABLED" : "disabled")}.",
                "SongRequestService", LogLevel.System);
        }

        /// <summary>
        /// REFUNDS every still-waiting request's charge, then stops the overlay heartbeat
        /// and drains it.
        ///
        /// ★ The refund is the load-bearing half, exactly as in PollsService.ShutdownAsync.
        /// A price is debited the moment the request lands (RequestAsync), so a queue
        /// waiting at shutdown is holding real points; the queue itself is session state
        /// that is never restored, so dropping it silently would keep those points with no
        /// track ever played and — unlike every other exit path from the queue — no System
        /// Log line the streamer could grant them back from. Refunding is the only honest
        /// ending. Deliberately NOT gated on <see cref="Active"/>: switching the tool off
        /// does not empty the queue, so a charge made while it was on is still owed. The
        /// config is persisted on every edit, so there is nothing else to flush. A HARD
        /// kill still loses the charges.
        ///
        /// Nothing is published afterwards: the shutdown coordinator drains the Overlay
        /// Live Channel just before this runs, and the pump teardown below exists precisely
        /// so nothing re-dirties it.
        /// </summary>
        public async Task ShutdownAsync()
        {
            try
            {
                List<SongRequestEntry> waiting;
                lock (_gate)
                {
                    waiting = new List<SongRequestEntry>(_queue);
                    _queue.Clear();
                }

                int refunded = 0, unrefunded = 0;
                foreach (var e in waiting)
                {
                    if (e.PointsPaid <= 0) continue;
                    if (await RefundAsync(e).ConfigureAwait(false)) refunded++;
                    else unrefunded++;
                }

                if (refunded > 0 || unrefunded > 0)
                    GlobalLogger.Log(
                        $"Song Request: the waiting queue was refunded at shutdown — " +
                        $"{refunded.ToString(CultureInfo.InvariantCulture)} charge(s) returned, " +
                        $"{unrefunded.ToString(CultureInfo.InvariantCulture)} still owed.",
                        "SongRequestService",
                        unrefunded > 0 ? LogLevel.CriticalError : LogLevel.System);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("SongRequestService", "shutdown queue refund failed", ex);
            }

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
                catch (Exception ex) { GlobalLogger.Error("SongRequestService", "overlay pump drain failed", ex); }
            }
            cts?.Dispose();
        }

        private static SongRequestConfig Normalize(SongRequestConfig cfg)
        {
            static string Word(string? v, string fallback)
                => string.IsNullOrWhiteSpace(v) ? fallback : v.Trim().TrimStart('!');

            cfg.RequestCommand   = Word(cfg.RequestCommand,   "sr");
            cfg.CurrentCommand   = Word(cfg.CurrentCommand,   "song");
            cfg.NextCommand      = Word(cfg.NextCommand,      "next");
            cfg.WhenCommand      = Word(cfg.WhenCommand,      "when");
            cfg.WrongSongCommand = Word(cfg.WrongSongCommand, "wrongsong");
            cfg.VoteSkipCommand  = Word(cfg.VoteSkipCommand,  "voteskip");
            cfg.VolumeCommand    = Word(cfg.VolumeCommand,    "volume");
            cfg.SkipCommand      = Word(cfg.SkipCommand,      "skip");
            cfg.PauseCommand     = Word(cfg.PauseCommand,     "pause");
            cfg.PlayCommand      = Word(cfg.PlayCommand,      "play");
            cfg.RemoveCommand    = Word(cfg.RemoveCommand,    "removesong");
            cfg.ClearCommand     = Word(cfg.ClearCommand,     "srclear");

            cfg.RequestRoles  ??= SongRoles.All();
            cfg.ViewRoles     ??= SongRoles.All();
            cfg.VoteSkipRoles ??= SongRoles.All();
            cfg.ModRoles      ??= SongRoles.Mods();

            if (cfg.SubDiscountPercent < 0) cfg.SubDiscountPercent = 0;
            if (cfg.SubDiscountPercent > 100) cfg.SubDiscountPercent = 100;
            if (cfg.PointCost < 0) cfg.PointCost = 0;
            if (cfg.RequestCooldownSeconds < 0) cfg.RequestCooldownSeconds = 0;
            cfg.Volume = Math.Clamp(cfg.Volume, 0, 100);
            return cfg;
        }

        // ── Config edits (panel) ────────────────────────────────────────────
        /// <summary>Replaces the whole config, persists it, and notifies the UI. The
        /// incoming object is deep-CLONED (JSON round-trip) so the panel VM can't alias the
        /// hot-path config instance (the Counters/Automod lesson).</summary>
        public async Task SaveConfigAsync(SongRequestConfig cfg)
        {
            var normalized = Normalize(Clone(cfg ?? new SongRequestConfig()));
            _config = normalized;
            // The panel's volume box and the !volume verb write the same number, so the
            // live transport volume follows a config save. Done under the lock because the
            // publish below reads it.
            lock (_gate) _volume = normalized.Volume;
            try
            {
                string json = JsonSerializer.Serialize(normalized, JsonOpts);
                await _db.SaveSongRequestConfigAsync(json, NowUnixMs()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("SongRequestService", "SaveConfigAsync failed", ex);
            }
            RaiseConfigChanged();
            PublishOverlay();
        }

        private static SongRequestConfig Clone(SongRequestConfig src)
        {
            try
            {
                string json = JsonSerializer.Serialize(src ?? new SongRequestConfig());
                return JsonSerializer.Deserialize<SongRequestConfig>(json) ?? new SongRequestConfig();
            }
            catch { return new SongRequestConfig(); }
        }

        private static SongRequestEntry CloneEntry(SongRequestEntry e) => new()
        {
            Id = e.Id,
            VideoId = e.VideoId,
            Title = e.Title,
            DurationSeconds = e.DurationSeconds,
            RequestedBy = e.RequestedBy,
            RequestedByLogin = e.RequestedByLogin,
            RequestedAtUnixMs = e.RequestedAtUnixMs,
            Status = e.Status,
            PointsPaid = e.PointsPaid,
        };

        // ── YouTube reference parsing (pure, keyless, testable) ─────────────

        // A YouTube video id is exactly 11 characters of the URL-safe base64 alphabet.
        // Anchored, so "please play something" can never be mistaken for an id.
        private static readonly Regex VideoIdRx =
            new(@"^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Extracts a YouTube video id from a link or a bare id. Returns false for a search
        /// phrase, for a link on any other host, and for a YouTube link with no id in it.
        ///
        /// Accepted shapes: <c>youtu.be/&lt;id&gt;</c>, <c>/watch?v=&lt;id&gt;</c>,
        /// <c>/shorts/&lt;id&gt;</c>, <c>/embed/&lt;id&gt;</c>, <c>/live/&lt;id&gt;</c>,
        /// <c>/v/&lt;id&gt;</c>, with or without a scheme, on youtube.com (any subdomain,
        /// including m. and music.), youtu.be and youtube-nocookie.com. A bare 11-character
        /// id is accepted directly — that is the one shape that needs no network call at
        /// all, which is why the keyless path exists.
        ///
        /// The host check is a real check, not a Contains: "youtube.com.evil.tld" must not
        /// pass, because this same predicate is what exempts a request line from the
        /// Automod link rule.
        /// </summary>
        public static bool TryParseVideoId(string? raw, out string videoId)
            => TryParseVideoId(raw, out videoId, out _);

        /// <summary>
        /// <see cref="TryParseVideoId(string?, out string)"/> plus the link's lower-cased
        /// host. <paramref name="host"/> is "" for a bare 11-character id — there is no link
        /// in that shape, so the Automod waiver has nothing to name and needs nothing: with
        /// no host in the line, the link rule cannot fire on it in the first place.
        /// Normalized identically to AutomodRules.ExtractHosts so the two sides compare
        /// equal without either guessing at the other's casing or trailing dot.
        /// </summary>
        public static bool TryParseVideoId(string? raw, out string videoId, out string host)
        {
            videoId = "";
            host = "";
            string s = (raw ?? string.Empty).Trim();
            if (s.Length == 0) return false;

            // Chat clients (and Discord-trained viewers) wrap links in angle brackets to
            // suppress the embed; strip one layer so <https://youtu.be/x> still resolves.
            if (s.Length > 2 && s[0] == '<' && s[^1] == '>') s = s[1..^1].Trim();

            if (VideoIdRx.IsMatch(s)) { videoId = s; return true; }

            // A scheme-less "youtu.be/xyz" is by far the commonest paste shape; Uri needs
            // one, so add it rather than hand-rolling a second parser. Only ever prefixed
            // when there is no scheme at all, so "ftp://…" is still rejected below.
            string candidate = s;
            if (candidate.IndexOf("://", StringComparison.Ordinal) < 0)
                candidate = "https://" + candidate;

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return false;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
             && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!IsYouTubeHost(uri.Host)) return false;
            // Only published once an id is actually found — a YouTube URL with no id in it
            // is not a request and must not hand its host to the Automod waiver.
            string linkHost = NormalizeHost(uri.Host);

            // youtu.be/<id> — the id is the first path segment.
            string path = uri.AbsolutePath.Trim('/');
            if (uri.Host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase))
            {
                if (!TakeFirstSegmentAsId(path, out videoId)) return false;
                host = linkHost;
                return true;
            }

            // /watch?v=<id> (and the /watch_popup, /attribution_link shapes that also
            // carry v=) — read the query.
            string? v = QueryValue(uri.Query, "v");
            if (v is not null && VideoIdRx.IsMatch(v)) { videoId = v; host = linkHost; return true; }

            // /shorts/<id>, /embed/<id>, /live/<id>, /v/<id>.
            int slash = path.IndexOf('/');
            if (slash > 0)
            {
                string head = path[..slash];
                if (head.Equals("shorts", StringComparison.OrdinalIgnoreCase)
                 || head.Equals("embed", StringComparison.OrdinalIgnoreCase)
                 || head.Equals("live", StringComparison.OrdinalIgnoreCase)
                 || head.Equals("v", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TakeFirstSegmentAsId(path[(slash + 1)..], out videoId)) return false;
                    host = linkHost;
                    return true;
                }
            }
            return false;
        }

        private static bool TakeFirstSegmentAsId(string path, out string videoId)
        {
            videoId = "";
            if (path.Length == 0) return false;
            int cut = path.IndexOf('/');
            string seg = cut < 0 ? path : path[..cut];
            if (!VideoIdRx.IsMatch(seg)) return false;
            videoId = seg;
            return true;
        }

        // Lower-cased, trailing dot stripped — the same normalization
        // AutomodRules.ExtractHosts / HostOf apply, so a host produced here can be compared
        // ordinally against one extracted there.
        private static string NormalizeHost(string? host)
            => (host ?? "").Trim().TrimEnd('.').ToLowerInvariant();

        private static bool IsYouTubeHost(string host)
        {
            host = NormalizeHost(host);
            return host is "youtube.com" or "youtu.be" or "youtube-nocookie.com"
                || host.EndsWith(".youtube.com", StringComparison.Ordinal)
                || host.EndsWith(".youtu.be", StringComparison.Ordinal)
                || host.EndsWith(".youtube-nocookie.com", StringComparison.Ordinal);
        }

        // Minimal query reader — System.Web isn't referenced here and one key is all we
        // need. Values are percent-decoded because a shared "watch?v=abc%2Ddef" is legal.
        private static string? QueryValue(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return null;
            foreach (string part in query.TrimStart('?').Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                if (!part[..eq].Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                try { return Uri.UnescapeDataString(part[(eq + 1)..]); }
                catch { return part[(eq + 1)..]; }
            }
            return null;
        }

        /// <summary>
        /// The lower-cased host of the YouTube link in <paramref name="message"/> when — and
        /// only when — the line is this tool's request command carrying a recognisable video
        /// reference and the tool is enabled. "" in every other case, a bare 11-character id
        /// included (that shape has no link, so there is nothing to waive and nothing the
        /// link rule could fire on).
        ///
        /// This is the Automod link-rule exemption (AutomodService.TryDetect). Automod runs
        /// at index 0 of the built-in chat dispatch and its Links rule with BlockAll on
        /// treats <c>youtu.be</c> as a link like any other — so with both tools enabled a
        /// viewer's <c>!sr https://youtu.be/…</c> was moderated before the Song Request
        /// provider was ever consulted.
        ///
        /// ★ It returns a HOST rather than a "skip the Links rule" flag, and that is the
        /// whole safety property. Waiving the RULE also waived the streamer's explicit
        /// UrlBlockList, so <c>!sr &lt;blocked-domain-url&gt;</c> walked straight through a
        /// block the streamer had set deliberately. Naming one host instead lets
        /// AutomodRules.LinksFired waive only the generic "links are not allowed" heuristic,
        /// only for the request's own URL, and never for a block-listed host. Two narrowings
        /// stack on top of that: the FIRST token must be the configured request word, and
        /// the remainder must parse as a real YouTube reference — so
        /// <c>!sr https://spam.tld</c> is moderated exactly as before, and with the tool off
        /// this is one field read and an empty string.
        /// </summary>
        public string ExemptRequestLinkHost(string? message)
        {
            var cfg = _config;
            if (!cfg.Enabled) return "";
            string text = (message ?? string.Empty).Trim();
            if (text.Length < 2 || text[0] != '!') return "";

            string body = text[1..];
            int sp = body.IndexOf(' ');
            if (sp < 0) return "";                          // no argument ⇒ no link to exempt
            string token = body[..sp].Trim();
            if (!string.Equals(token, cfg.RequestCommand, StringComparison.OrdinalIgnoreCase)) return "";
            return TryParseVideoId(body[(sp + 1)..].Trim(), out _, out string host) ? host : "";
        }

        // ── Reads ───────────────────────────────────────────────────────────

        /// <summary>One consistent snapshot of the player + queue, taken under the lock so
        /// the panel, the read nodes and the live-channel publish all describe the SAME
        /// instant instead of three interleaved ones. Entries are clones — a caller can
        /// never mutate live queue state through a read.</summary>
        public SongPlayerSnapshot Snapshot()
        {
            lock (_gate)
            {
                var queue = new List<SongRequestEntry>(_queue.Count);
                foreach (var e in _queue) queue.Add(CloneEntry(e));
                return new SongPlayerSnapshot
                {
                    State = _state,
                    Current = _current is null ? null : CloneEntry(_current),
                    Volume = _volume,
                    PlayToken = _playToken,
                    Queue = queue,
                    SkipVotes = _skipVotes.Count,
                };
            }
        }

        /// <summary>The next entry that would actually play — the first APPROVED one, so a
        /// pending request awaiting a verdict never advertises itself as "up next".</summary>
        public SongRequestEntry? UpNext()
        {
            lock (_gate)
            {
                var e = FirstPlayableLocked();
                return e is null ? null : CloneEntry(e);
            }
        }

        /// <summary>1-based position of a viewer's first waiting request, or 0 when they
        /// have none. Counts every waiting entry, pending ones included — the viewer asked
        /// where their song is, and "behind three others, still awaiting approval" is the
        /// honest answer.</summary>
        public int PositionOf(string login)
        {
            string u = NormalizeLogin(login);
            if (u.Length == 0) return 0;
            lock (_gate)
            {
                for (int i = 0; i < _queue.Count; i++)
                    if (string.Equals(_queue[i].RequestedByLogin, u, StringComparison.OrdinalIgnoreCase))
                        return i + 1;
            }
            return 0;
        }

        /// <summary>Number of waiting requests (the playing track is not counted).</summary>
        public int QueueLength { get { lock (_gate) return _queue.Count; } }

        // ── Request ─────────────────────────────────────────────────────────

        /// <summary>
        /// The whole accept-or-refuse pipeline for one request, in the order that makes the
        /// refusals honest: resolve first (so a bad link is never charged for), then the
        /// free caps, then the money. Charging before the caps would take points for a
        /// request the queue was always going to reject.
        ///
        /// <paramref name="rawArg"/> is whatever followed the command word: a link, a bare
        /// 11-character id, or a search phrase. Links and ids resolve with no API key;
        /// a phrase needs one and is refused (politely, never scraped) without it.
        /// </summary>
        public async Task<SongRequestResult> RequestAsync(
            string login, string displayName, bool isSub, string rawArg, CancellationToken ct = default)
        {
            if (!Active) return SongRequestResult.Fail(SongRequestOutcome.Disabled);

            var cfg = _config;
            string user = NormalizeLogin(login);
            string display = string.IsNullOrWhiteSpace(displayName) ? user : displayName.Trim();
            string arg = (rawArg ?? string.Empty).Trim();
            if (arg.Length == 0 || user.Length == 0)
                return SongRequestResult.Fail(SongRequestOutcome.EmptyRequest);

            // Cooldown is checked BEFORE the network round trip: a viewer spamming !sr
            // must not be able to spend the streamer's API quota on lookups that were
            // never going to be queued.
            if (cfg.RequestCooldownSeconds > 0)
            {
                int remaining = CooldownRemaining(user);
                if (remaining > 0)
                    return SongRequestResult.Fail(SongRequestOutcome.OnCooldown, remaining);
            }

            // ── Resolve ────────────────────────────────────────────────────
            var lookup = YouTubeLookup;
            string videoId;
            string title = "";
            int duration = 0;

            if (TryParseVideoId(arg, out videoId))
            {
                // A link or id already gives us everything the PLAYER needs. The lookup is
                // only for the metadata a cap and a chat line want, so every failure here
                // degrades to id-only rather than refusing — except the two verdicts that
                // are about the video itself and would otherwise queue something
                // unplayable.
                if (lookup is not null)
                {
                    SongLookupResult meta;
                    try { meta = await lookup(new SongLookupRequest(videoId, ""), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        GlobalLogger.Error("SongRequestService", "YouTube metadata lookup failed", ex);
                        meta = SongLookupResult.Fail(SongLookupOutcome.Error);
                    }
                    switch (meta.Outcome)
                    {
                        case SongLookupOutcome.Ok:
                            title = meta.Title;
                            duration = meta.DurationSeconds;
                            break;
                        case SongLookupOutcome.NotFound:
                            return SongRequestResult.Fail(SongRequestOutcome.NotFound);
                        case SongLookupOutcome.NotEmbeddable:
                            return SongRequestResult.Fail(SongRequestOutcome.NotEmbeddable);
                        // NoApiKey / Error: keep the id, leave title+duration unknown.
                    }
                }
            }
            else
            {
                // A search phrase. Without a key there is nothing honest to do but ask for
                // a link — Phoenix does not scrape YouTube, ever.
                if (lookup is null) return SongRequestResult.Fail(SongRequestOutcome.SearchNeedsApiKey);

                SongLookupResult hit;
                try { hit = await lookup(new SongLookupRequest("", arg), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    GlobalLogger.Error("SongRequestService", "YouTube search failed", ex);
                    hit = SongLookupResult.Fail(SongLookupOutcome.Error);
                }
                switch (hit.Outcome)
                {
                    case SongLookupOutcome.Ok:
                        videoId = hit.VideoId;
                        title = hit.Title;
                        duration = hit.DurationSeconds;
                        break;
                    case SongLookupOutcome.NoApiKey:
                        return SongRequestResult.Fail(SongRequestOutcome.SearchNeedsApiKey);
                    case SongLookupOutcome.NotEmbeddable:
                        return SongRequestResult.Fail(SongRequestOutcome.NotEmbeddable);
                    default:
                        return SongRequestResult.Fail(SongRequestOutcome.NotFound);
                }
                if (!VideoIdRx.IsMatch(videoId)) return SongRequestResult.Fail(SongRequestOutcome.NotFound);
            }

            // ── Free caps ──────────────────────────────────────────────────
            // A 0 duration means "unknown" (no API key), not "zero seconds", so the cap is
            // skipped rather than applied to a number we never read. The panel says so.
            if (cfg.MaxDurationSeconds > 0 && duration > 0 && duration > cfg.MaxDurationSeconds)
                return SongRequestResult.Fail(SongRequestOutcome.TooLong);

            lock (_gate)
            {
                if (cfg.MaxQueueLength > 0 && _queue.Count >= cfg.MaxQueueLength)
                    return SongRequestResult.Fail(SongRequestOutcome.QueueFull);

                if (cfg.MaxPerUser > 0)
                {
                    int mine = 0;
                    foreach (var e in _queue)
                        if (string.Equals(e.RequestedByLogin, user, StringComparison.OrdinalIgnoreCase)) mine++;
                    if (mine >= cfg.MaxPerUser)
                        return SongRequestResult.Fail(SongRequestOutcome.UserLimit);
                }

                if (IsAlreadyQueuedLocked(videoId))
                    return SongRequestResult.Fail(SongRequestOutcome.Duplicate);
            }

            // ── Money, last ────────────────────────────────────────────────
            long price = EffectivePrice(cfg, isSub);
            long charged = 0;
            if (price > 0)
            {
                var charge = ChargePoints;
                if (charge is null) return SongRequestResult.Fail(SongRequestOutcome.EconomyOff);

                SongChargeResult res;
                try { res = await charge(user, price).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    GlobalLogger.Error("SongRequestService", "point charge failed", ex);
                    res = SongChargeResult.Fail(SongChargeOutcome.Error);
                }
                switch (res.Outcome)
                {
                    case SongChargeOutcome.Ok: charged = res.Amount; break;
                    case SongChargeOutcome.EconomyOff: return SongRequestResult.Fail(SongRequestOutcome.EconomyOff);
                    default: return SongRequestResult.Fail(SongRequestOutcome.NoFunds);
                }
            }

            var entry = new SongRequestEntry
            {
                VideoId = videoId,
                Title = title,
                DurationSeconds = duration,
                RequestedBy = display,
                RequestedByLogin = user,
                RequestedAtUnixMs = NowUnixMs(),
                Status = cfg.RequireApproval ? SongRequestStatus.Pending : SongRequestStatus.Queued,
                PointsPaid = charged,
            };

            int position = 0;
            // Non-null once the under-lock re-check refuses. Carried rather than collapsed
            // to a single "queue full" so a late refusal reads the same as an early one —
            // telling a viewer the queue is full when they actually hit their own limit is
            // the kind of small lie that makes a tool feel broken.
            SongRequestOutcome? lateRefusal = null;
            int lateCooldownRemaining = 0;
            lock (_gate)
            {
                // Re-check EVERY gate under the lock we are about to mutate: the resolve and
                // the charge above both awaited, so two of the same viewer's messages — or
                // two viewers racing for the last slot — can both have passed the first
                // check. Without this, MaxPerUser=1 lets a fast double-!sr through and a
                // full queue grows past its ceiling. The refund happens outside the lock.
                int mineNow = 0;
                if (cfg.MaxPerUser > 0)
                    foreach (var e in _queue)
                        if (string.Equals(e.RequestedByLogin, user, StringComparison.OrdinalIgnoreCase)) mineNow++;

                // The cooldown belongs in this re-check more than any of its siblings do,
                // because it is STAMPED right here, inside this lock. A viewer's second !sr
                // that overtook the first one's await found no stamp at the early check and,
                // while this rung was missing, none here either — so both queued and the
                // cooldown bound nothing at all on exactly the spam it exists to stop.
                int cooldownLeft = cfg.RequestCooldownSeconds > 0 ? CooldownRemainingLocked(user) : 0;

                if (cooldownLeft > 0)
                {
                    lateRefusal = SongRequestOutcome.OnCooldown;
                    lateCooldownRemaining = cooldownLeft;
                }
                else if (cfg.MaxQueueLength > 0 && _queue.Count >= cfg.MaxQueueLength)
                    lateRefusal = SongRequestOutcome.QueueFull;
                else if (cfg.MaxPerUser > 0 && mineNow >= cfg.MaxPerUser)
                    lateRefusal = SongRequestOutcome.UserLimit;
                else if (IsAlreadyQueuedLocked(videoId))
                    lateRefusal = SongRequestOutcome.Duplicate;
                else
                {
                    _queue.Add(entry);
                    position = _queue.Count;
                    if (cfg.RequestCooldownSeconds > 0)
                        StampCooldownLocked(user, cfg.RequestCooldownSeconds);
                }
            }

            if (lateRefusal is not null)
            {
                // Log-only on failure: this path is a race nobody asked for by name, so
                // there is no chat line already addressed to this viewer (DescribeRequest
                // renders the refusal, not a refund verdict) — see UnrefundedNote.
                await RefundAsync(entry).ConfigureAwait(false);
                return SongRequestResult.Fail(lateRefusal.Value, lateCooldownRemaining);
            }

            RecordActivity("REQ", entry.Status == SongRequestStatus.Pending
                ? $"{ClipName(display)} requested {ClipTitle(title, videoId)} — waiting for approval."
                : $"{ClipName(display)} requested {ClipTitle(title, videoId)} — #{position.ToString(CultureInfo.InvariantCulture)} in the queue.");

            RaiseQueueChanged();
            PublishOverlay();

            // Song.OnQueued fires when the request actually JOINS the playable line. With
            // approval on, that is the moderator's approve — not the request — so a graph
            // announcing "queued!" never announces something a mod is about to deny.
            if (entry.Status == SongRequestStatus.Queued)
                RaiseSongEvent("Song.OnQueued", entry, extraKey: "event.position",
                    extraValue: position.ToString(CultureInfo.InvariantCulture));

            return new SongRequestResult
            {
                Outcome = SongRequestOutcome.Accepted,
                Entry = CloneEntry(entry),
                Position = position,
                Charged = charged,
            };
        }

        /// <summary>The price this viewer actually pays, sub discount applied. Rounded UP,
        /// so a 33% discount on 10 costs 7 rather than 6 — a discount never rounds in the
        /// house's favour by accident, and it never reaches 0 unless it is a full 100%.</summary>
        internal static long EffectivePrice(SongRequestConfig cfg, bool isSub)
        {
            long price = cfg.PointCost;
            if (price <= 0) return 0;
            if (!isSub || cfg.SubDiscountPercent <= 0) return price;
            if (cfg.SubDiscountPercent >= 100) return 0;
            long keep = 100L - cfg.SubDiscountPercent;
            return (price * keep + 99) / 100;
        }

        private bool IsAlreadyQueuedLocked(string videoId)
        {
            if (_current is not null && string.Equals(_current.VideoId, videoId, StringComparison.Ordinal))
                return true;
            foreach (var e in _queue)
                if (string.Equals(e.VideoId, videoId, StringComparison.Ordinal)) return true;
            return false;
        }

        private int CooldownRemaining(string user)
        {
            lock (_gate) return CooldownRemainingLocked(user);
        }

        // Caller holds _gate. Evict-on-read keeps the common case free of any sweep.
        private int CooldownRemainingLocked(string user)
        {
            if (!_cooldownUntilMs.TryGetValue(user, out long until)) return 0;
            long now = _clock.ElapsedMilliseconds;
            if (now >= until) { _cooldownUntilMs.Remove(user); return 0; }
            return (int)Math.Ceiling((until - now) / 1000.0);
        }

        // Caller holds _gate. Evict-on-read above only reclaims a viewer who requests AGAIN,
        // so a raid's worth of one-time requesters would sit in this map for the rest of the
        // session. Same opportunistic bulk sweep AutomodService.GrantPermit runs on its
        // permit map: past a small ceiling, drop everything already expired. Bounded on the
        // write path because that is the only path that can grow the map.
        private void StampCooldownLocked(string user, int cooldownSeconds)
        {
            long now = _clock.ElapsedMilliseconds;
            _cooldownUntilMs[user] = now + cooldownSeconds * 1000L;
            if (_cooldownUntilMs.Count <= CooldownMapSweepThreshold) return;

            List<string>? stale = null;
            foreach (var kv in _cooldownUntilMs)
                if (kv.Value <= now) (stale ??= new List<string>()).Add(kv.Key);
            if (stale is null) return;
            foreach (var k in stale) _cooldownUntilMs.Remove(k);
        }

        // Deliberately above a plausible concurrent-requester count so a busy chat never
        // pays for the sweep, and far below anything that matters for memory: an entry is
        // one login plus a long.
        private const int CooldownMapSweepThreshold = 256;

        // ── Transport + queue mutation ──────────────────────────────────────

        /// <summary>
        /// Selects the next approved request and marks it Playing; when the queue holds
        /// nothing playable the player goes Idle. <paramref name="skippedBy"/> non-empty
        /// means the current track was cut short rather than handed over, which is what
        /// raises Song.OnSkip.
        /// </summary>
        public Task<SongRequestEntry?> AdvanceAsync(string skippedBy = "")
            // Task-returning without being `async`: nothing here awaits (the queue is in
            // memory and the two raises are synchronous), and every caller — the chat verb,
            // the panel button, ResumeAsync, VoteSkipAsync — awaits it as one of the
            // mutators. A real `async` body with a filler await would only add a state
            // machine to advertise work that does not exist.
            => Task.FromResult(AdvanceCore(skippedBy, requirePlayToken: null).Next);

        /// <summary>
        /// The one advance. <paramref name="requirePlayToken"/> is the CONDITIONAL half, and
        /// it exists for exactly one caller — <see cref="NotifyMediaEndedAsync"/>.
        ///
        /// <para><b>★ Why the token is re-checked HERE and not at the call site.</b> The
        /// caller's claim latch is taken under <c>_gate</c> and then released, because
        /// advancing is a separate operation; between those two moments a mod's <c>!skip</c>
        /// can land, take the lock, and move the queue. The claim latch does not stop it —
        /// the latch only refuses a SECOND media-end report of the same token — so the
        /// media-end's own advance then consumed a second track and the streamer lost a song
        /// nobody skipped. Re-reading <c>_playToken</c> inside the same lock that mutates it
        /// closes the window without widening any lock over an <c>await</c>: whichever of the
        /// two got the lock first wins, and the loser sees a token that has moved and does
        /// nothing.</para>
        ///
        /// <para><b>The bool is the honest half.</b> "Did the queue actually move" cannot be
        /// read off <c>Next</c>: null is also the perfectly ordinary outcome of advancing a
        /// drained queue to Idle. A caller that needs to log why a queue stopped needs the
        /// two apart.</para>
        /// </summary>
        private (bool Advanced, SongRequestEntry? Next) AdvanceCore(string skippedBy, long? requirePlayToken)
        {
            // Re-read rather than trusted from the caller: the master toggle can be flipped
            // between a caller's own guard and this point, and a dormant tool must not move.
            if (!Active) return (false, null);

            SongRequestEntry? skipped, next;
            lock (_gate)
            {
                if (requirePlayToken is long expected && expected != _playToken)
                    return (false, null);

                skipped = _current;
                next = FirstPlayableLocked();
                if (next is not null)
                {
                    _queue.Remove(next);
                    next.Status = SongRequestStatus.Playing;
                }
                _current = next;
                _state = next is null ? SongPlayerState.Idle : SongPlayerState.Playing;
                // A new selection is a new load for the player, even when it happens to be
                // the same video id as the one that just finished.
                _playToken++;
                _skipVotes.Clear();
            }

            if (skipped is not null && skippedBy.Length > 0)
            {
                RecordActivity("SKIP", $"{ClipTitle(skipped.Title, skipped.VideoId)} skipped by {ClipName(skippedBy)}.");
                RaiseSongEvent("Song.OnSkip", skipped, "event.skipped_by", skippedBy);
            }

            // The two ends of one advance are separate rows on purpose: "nothing left to
            // play" is the state a streamer is looking for when the music stops, and it is
            // invisible if only selections are recorded.
            RecordActivity("PLAY", next is null
                ? "Queue empty — the player went idle."
                : $"Now playing {ClipTitle(next.Title, next.VideoId)} (requested by {ClipName(next.RequestedBy)}).");

            RaiseQueueChanged();
            PublishOverlay();

            if (next is not null)
                RaiseSongEvent("Song.OnPlay", next, "event.duration_seconds",
                    next.DurationSeconds.ToString(CultureInfo.InvariantCulture));

            return (true, next is null ? null : CloneEntry(next));
        }

        /// <summary>
        /// V15 — the overlay player reports that the track it was playing has finished, so
        /// the queue may advance. THE ONLY upward signal this service accepts, and the only
        /// thing in the product that auto-advances the queue.
        ///
        /// <para><b>Returns true only when it actually advanced.</b> False is the ordinary
        /// outcome for every report that should be ignored, and the caller logs which one
        /// happened — a queue that mysteriously stops moving has to be diagnosable. That
        /// promise covers the LATE bails too, not only the validations below: the tool being
        /// switched off, or a <c>!skip</c> winning the race, between the claim and the advance
        /// both report false rather than a true that never moved anything.</para>
        ///
        /// <para><b>★ Why <paramref name="playToken"/> is the dedupe and no set is needed.</b>
        /// Two OBS Browser Sources pointed at the same layer both mount a player, both play
        /// the same track and both report it ending. They report the SAME pair, because
        /// <paramref name="playToken"/> is not a browser-minted counter — it is
        /// <c>songrequest.play_token</c>, read off the live channel both of them subscribe
        /// to. So the second report is refused three times over: the claim latch already
        /// holds that token, the token no longer equals <c>_playToken</c> (the advance bumped
        /// it), and the video id no longer matches the current track. The latch is what makes
        /// it safe under genuine CONCURRENCY, where the other two would both still pass:
        /// claiming happens inside the same lock that validates, so exactly one caller can
        /// win. One <c>long</c>, no list, nothing to bound.</para>
        ///
        /// <para><b>★ And the claim is not the whole of it.</b> The latch collapses two
        /// MEDIA_ENDED reports; it does nothing about a mod's <c>!skip</c> landing between the
        /// claim and the advance, which used to consume two tracks — the skip's and this
        /// one's. So the advance is CONDITIONAL on the same token, re-read inside the lock it
        /// mutates in (<c>AdvanceCore</c>'s <c>requirePlayToken</c>). Nothing here holds a
        /// lock across an <c>await</c>; the two operations simply agree on what they are
        /// advancing past.</para>
        ///
        /// <para><b>Why the state must be Playing.</b> A paused track has not ended. Without
        /// that check a widget whose iframe was torn down during a pause — an OBS scene
        /// switch, a source toggle — could report an end and skip the track the streamer
        /// deliberately held.</para>
        ///
        /// <para>Advances with an EMPTY <c>skippedBy</c>: a track that finished was handed
        /// over, not cut short, so this raises <c>Song.OnPlay</c> for the next entry and
        /// deliberately does NOT raise <c>Song.OnSkip</c> for the one that ended.</para>
        /// </summary>
        /// <param name="videoId">The video the reporting widget was playing.</param>
        /// <param name="playToken">The <c>songrequest.play_token</c> that selection carried.</param>
        public Task<bool> NotifyMediaEndedAsync(string videoId, long playToken)
        {
            if (!Active) return Task.FromResult(false);

            lock (_gate)
            {
                if (_current is null) return Task.FromResult(false);
                if (_state != SongPlayerState.Playing) return Task.FromResult(false);
                if (playToken != _playToken) return Task.FromResult(false);
                if (!string.Equals(videoId, _current.VideoId, StringComparison.Ordinal)) return Task.FromResult(false);
                if (_mediaEndClaimedToken == playToken) return Task.FromResult(false);
                _mediaEndClaimedToken = playToken;
            }

            // Empty skippedBy — a finished track was handed over, not cut short. The token is
            // handed on so the advance can refuse a queue that moved under it; its bool is
            // returned verbatim, because "I claimed it" and "it advanced" are not the same
            // fact and only the second one is what the caller logs against.
            //
            // Task-returning without being `async`, now that the advance is synchronous too:
            // there is nothing to await, and the signature stays a Task so every existing
            // caller keeps awaiting the one upward signal exactly as before.
            bool advanced = AdvanceCore(string.Empty, requirePlayToken: playToken).Advanced;

            if (!advanced)
            {
                // The claim was NOT spent, so release it. Otherwise a report refused by a late
                // bail — the tool switched off mid-flight, a skip winning the race — would burn
                // the latch for a token that may still be current, and no later report of that
                // same selection could ever move the queue again: a wedged queue produced by
                // the very mechanism that exists to keep the queue honest. Compare-and-clear
                // under the lock so a newer claim is never clobbered.
                lock (_gate)
                {
                    if (_mediaEndClaimedToken == playToken) _mediaEndClaimedToken = -1;
                }
            }

            return Task.FromResult(advanced);
        }

        /// <summary>Holds the current track. False when nothing is playing.</summary>
        public bool Pause()
        {
            if (!Active) return false;
            string held;
            lock (_gate)
            {
                if (_current is null || _state != SongPlayerState.Playing) return false;
                _state = SongPlayerState.Paused;
                held = ClipTitle(_current.Title, _current.VideoId);
            }
            RecordActivity("HOLD", $"Paused {held}.");
            RaiseQueueChanged();
            PublishOverlay();
            return true;
        }

        /// <summary>
        /// Resumes a held track, or — when nothing is selected — starts the queue. That
        /// double duty is what makes !play the natural "start the music" verb rather than a
        /// button that does nothing on an idle player with ten songs waiting.
        /// Returns false only when there is genuinely nothing to do.
        /// </summary>
        public async Task<bool> ResumeAsync()
        {
            if (!Active) return false;
            bool resumed = false;
            bool needsAdvance = false;
            string resumedTitle = "";
            lock (_gate)
            {
                if (_current is not null)
                {
                    if (_state == SongPlayerState.Playing) return false;   // already playing
                    _state = SongPlayerState.Playing;
                    resumed = true;
                    resumedTitle = ClipTitle(_current.Title, _current.VideoId);
                }
                else if (FirstPlayableLocked() is not null)
                {
                    needsAdvance = true;
                }
            }
            // The advance branch records its own PLAY row (AdvanceCore) — recording here as
            // well would show one start as two.
            if (needsAdvance) return await AdvanceAsync().ConfigureAwait(false) is not null;
            if (resumed)
            {
                RecordActivity("PLAY", $"Resumed {resumedTitle}.");
                RaiseQueueChanged();
                PublishOverlay();
            }
            return resumed;
        }

        /// <summary>Sets the player volume (clamped 0-100) and persists it, so it survives
        /// a restart the way the panel's own box implies it does.
        ///
        /// A no-op while the tool is switched off, like every other mutator here, returning
        /// the volume as it already stands. Without that guard a Song.SetVolume node in a
        /// running graph rewrote the persisted config blob of a DORMANT tool — the one thing
        /// <see cref="Active"/> promises cannot happen. The panel is unaffected: it edits
        /// Volume through its own working config and SaveConfigAsync, which is exactly how a
        /// streamer is meant to configure a tool before enabling it.</summary>
        public async Task<int> SetVolumeAsync(int volume)
        {
            if (!Active) { lock (_gate) return _volume; }

            int v = Math.Clamp(volume, 0, 100);
            lock (_gate) _volume = v;

            var cfg = Clone(_config);
            cfg.Volume = v;
            await SaveConfigAsync(cfg).ConfigureAwait(false);   // publishes + raises ConfigChanged
            return v;
        }

        /// <summary>Removes the waiting request at 1-based <paramref name="position"/> and
        /// refunds whatever it was charged. Null when the position is out of range.
        /// A returned entry whose <see cref="SongRequestEntry.PointsPaid"/> is still above
        /// zero is one whose refund was REFUSED — see <see cref="RefundAsync"/>.</summary>
        public async Task<SongRequestEntry?> RemoveAtAsync(int position)
        {
            if (!Active) return null;
            SongRequestEntry? removed = null;
            lock (_gate)
            {
                if (position >= 1 && position <= _queue.Count)
                {
                    removed = _queue[position - 1];
                    _queue.RemoveAt(position - 1);
                }
            }
            if (removed is null) return null;
            await RefundAsync(removed).ConfigureAwait(false);
            RaiseQueueChanged();
            PublishOverlay();
            return CloneEntry(removed);
        }

        /// <summary>Removes a request by its stable id (the panel's per-row delete, which
        /// must not race a position that shifted while the streamer was reading it). A
        /// returned entry still carrying <see cref="SongRequestEntry.PointsPaid"/> is one
        /// whose refund was refused; the panel has no chat line to say so on, so that case
        /// is reported through the System Log by <see cref="RefundAsync"/>.</summary>
        public async Task<SongRequestEntry?> RemoveByIdAsync(string id)
        {
            if (!Active || string.IsNullOrEmpty(id)) return null;
            SongRequestEntry? removed = null;
            lock (_gate)
            {
                for (int i = 0; i < _queue.Count; i++)
                {
                    if (!string.Equals(_queue[i].Id, id, StringComparison.Ordinal)) continue;
                    removed = _queue[i];
                    _queue.RemoveAt(i);
                    break;
                }
            }
            if (removed is null) return null;
            await RefundAsync(removed).ConfigureAwait(false);
            RaiseQueueChanged();
            PublishOverlay();
            return CloneEntry(removed);
        }

        /// <summary>Removes a viewer's most recent still-waiting request — the !wrongsong
        /// verb, for the mis-pasted link. Their LAST one, not their first: the viewer just
        /// realised the thing they typed a moment ago was wrong. A returned entry still
        /// carrying <see cref="SongRequestEntry.PointsPaid"/> is one whose refund was
        /// refused, which the chat reply says out loud.</summary>
        public async Task<SongRequestEntry?> RemoveLastForAsync(string login)
        {
            if (!Active) return null;
            string u = NormalizeLogin(login);
            if (u.Length == 0) return null;

            SongRequestEntry? removed = null;
            lock (_gate)
            {
                for (int i = _queue.Count - 1; i >= 0; i--)
                {
                    if (!string.Equals(_queue[i].RequestedByLogin, u, StringComparison.OrdinalIgnoreCase)) continue;
                    removed = _queue[i];
                    _queue.RemoveAt(i);
                    break;
                }
            }
            if (removed is null) return null;
            await RefundAsync(removed).ConfigureAwait(false);
            RaiseQueueChanged();
            PublishOverlay();
            return CloneEntry(removed);
        }

        /// <summary>Empties the waiting queue (the playing track is untouched) and refunds
        /// every charged request in it. <c>Dropped</c> is how many entries left the queue;
        /// <c>Unrefunded</c> is how many of those still owe their charge because the points
        /// economy refused to give it back. The second number is returned rather than only
        /// logged because ONE !srclear can strand a whole queue's worth of charges at once —
        /// the caller has a chat line going out anyway and can say so on it.</summary>
        public async Task<(int Dropped, int Unrefunded)> ClearAsync()
        {
            if (!Active) return (0, 0);
            List<SongRequestEntry> dropped;
            lock (_gate)
            {
                if (_queue.Count == 0) return (0, 0);
                dropped = new List<SongRequestEntry>(_queue);
                _queue.Clear();
            }
            int unrefunded = 0;
            foreach (var e in dropped)
                if (!await RefundAsync(e).ConfigureAwait(false)) unrefunded++;
            RaiseQueueChanged();
            PublishOverlay();
            return (dropped.Count, unrefunded);
        }

        /// <summary>Approves the pending request at 1-based <paramref name="position"/>,
        /// letting it join the playable line. Null when the position is out of range or
        /// that entry was never pending.</summary>
        public SongRequestEntry? Approve(int position)
        {
            if (!Active) return null;
            SongRequestEntry? approved = null;
            int place = 0;
            lock (_gate)
            {
                if (position >= 1 && position <= _queue.Count
                    && _queue[position - 1].Status == SongRequestStatus.Pending)
                {
                    approved = _queue[position - 1];
                    approved.Status = SongRequestStatus.Queued;
                    place = position;
                }
            }
            if (approved is null) return null;
            RaiseQueueChanged();
            PublishOverlay();
            RaiseSongEvent("Song.OnQueued", approved, "event.position",
                place.ToString(CultureInfo.InvariantCulture));
            return CloneEntry(approved);
        }

        /// <summary>Approves by stable id (the panel's per-row button). Looks the entry up
        /// and flips it under ONE lock rather than resolving a position and then acting on
        /// it: a chat request landing in between would shift every position, and the
        /// two-step version would approve whatever moved into that slot.</summary>
        public SongRequestEntry? ApproveById(string id)
        {
            if (!Active || string.IsNullOrEmpty(id)) return null;
            SongRequestEntry? approved = null;
            int place = 0;
            lock (_gate)
            {
                for (int i = 0; i < _queue.Count; i++)
                {
                    if (!string.Equals(_queue[i].Id, id, StringComparison.Ordinal)) continue;
                    if (_queue[i].Status != SongRequestStatus.Pending) break;
                    approved = _queue[i];
                    approved.Status = SongRequestStatus.Queued;
                    place = i + 1;
                    break;
                }
            }
            if (approved is null) return null;
            RaiseQueueChanged();
            PublishOverlay();
            RaiseSongEvent("Song.OnQueued", approved, "event.position",
                place.ToString(CultureInfo.InvariantCulture));
            return CloneEntry(approved);
        }

        /// <summary>Denies the pending request at 1-based <paramref name="position"/>,
        /// removing it and refunding whatever it was charged. As with the removals, a
        /// returned entry whose <see cref="SongRequestEntry.PointsPaid"/> survived is one
        /// whose refund the points economy refused.</summary>
        public async Task<SongRequestEntry?> DenyAsync(int position)
        {
            if (!Active) return null;
            SongRequestEntry? denied = null;
            lock (_gate)
            {
                if (position >= 1 && position <= _queue.Count
                    && _queue[position - 1].Status == SongRequestStatus.Pending)
                {
                    denied = _queue[position - 1];
                    _queue.RemoveAt(position - 1);
                }
            }
            if (denied is null) return null;
            await RefundAsync(denied).ConfigureAwait(false);
            RecordDenied(denied);
            RaiseQueueChanged();
            PublishOverlay();
            return CloneEntry(denied);
        }

        /// <summary>Denies by stable id (the panel's per-row button). Single-lock lookup +
        /// removal, for the same reason <see cref="ApproveById"/> is.</summary>
        public async Task<SongRequestEntry?> DenyByIdAsync(string id)
        {
            if (!Active || string.IsNullOrEmpty(id)) return null;
            SongRequestEntry? denied = null;
            lock (_gate)
            {
                for (int i = 0; i < _queue.Count; i++)
                {
                    if (!string.Equals(_queue[i].Id, id, StringComparison.Ordinal)) continue;
                    if (_queue[i].Status != SongRequestStatus.Pending) break;
                    denied = _queue[i];
                    _queue.RemoveAt(i);
                    break;
                }
            }
            if (denied is null) return null;
            await RefundAsync(denied).ConfigureAwait(false);
            RecordDenied(denied);
            RaiseQueueChanged();
            PublishOverlay();
            return CloneEntry(denied);
        }

        // A denial row states the refund verdict, because a surviving PointsPaid is exactly
        // the evidence that the viewer is out of pocket (RefundAsync clears the field only
        // on success) and the feed is where the streamer would notice it.
        private void RecordDenied(SongRequestEntry denied)
            => RecordActivity("DENY", denied.PointsPaid > 0
                ? $"Denied {ClipTitle(denied.Title, denied.VideoId)} from {ClipName(denied.RequestedBy)} — {denied.PointsPaid.ToString(CultureInfo.InvariantCulture)} points could NOT be returned."
                : $"Denied {ClipTitle(denied.Title, denied.VideoId)} from {ClipName(denied.RequestedBy)}.");

        /// <summary>
        /// Records one viewer's vote to skip the current track and skips it once the
        /// threshold is met. Votes are per TRACK — cleared on every advance — and one
        /// viewer counts once no matter how often they type it, which is the only shape in
        /// which a threshold means anything.
        /// </summary>
        public async Task<SongVoteSkipResult> VoteSkipAsync(string login)
        {
            var cfg = _config;
            if (!Active || cfg.VoteSkipThreshold <= 0) return new SongVoteSkipResult(false, false, 0, 0);
            string u = NormalizeLogin(login);
            if (u.Length == 0) return new SongVoteSkipResult(false, false, 0, cfg.VoteSkipThreshold);

            int votes;
            bool counted;
            bool reached;
            lock (_gate)
            {
                if (_current is null) return new SongVoteSkipResult(false, false, 0, cfg.VoteSkipThreshold);
                counted = _skipVotes.Add(u);
                votes = _skipVotes.Count;
                reached = votes >= cfg.VoteSkipThreshold;
            }

            if (reached)
            {
                await AdvanceAsync("voteskip").ConfigureAwait(false);
                return new SongVoteSkipResult(counted, true, votes, cfg.VoteSkipThreshold);
            }
            if (counted)
            {
                RaiseQueueChanged();
                PublishOverlay();
            }
            return new SongVoteSkipResult(counted, false, votes, cfg.VoteSkipThreshold);
        }

        private SongRequestEntry? FirstPlayableLocked()
        {
            foreach (var e in _queue)
                if (e.Status == SongRequestStatus.Queued) return e;
            return null;
        }

        // ── Panel activity feed ─────────────────────────────────────────────
        /// <summary>The key this tool's rows carry in <see cref="ToolActivityRing"/>.</summary>
        public const string ActivityTool = "SongRequest";

        // Both of these are viewer-supplied and unbounded — a YouTube title and a platform
        // display name — so every one that reaches a row goes through here.
        private const int ActivityTitleMaxChars = 60;
        private const int ActivityNameMaxChars = 32;

        /// <summary>The track as a row names it: its title, or the bare video id when the
        /// title was never read (no API key). Never a blank pair of quotes.</summary>
        internal static string ClipTitle(string? title, string? videoId)
        {
            string t = (title ?? string.Empty).Trim();
            if (t.Length == 0)
            {
                string id = (videoId ?? string.Empty).Trim();
                return id.Length > 0 ? id : "an unknown track";
            }
            return t.Length <= ActivityTitleMaxChars ? t : t[..ActivityTitleMaxChars].TrimEnd() + "...";
        }

        internal static string ClipName(string? name)
        {
            string n = (name ?? string.Empty).Trim();
            if (n.Length == 0) return "someone";
            return n.Length <= ActivityNameMaxChars ? n : n[..ActivityNameMaxChars].TrimEnd() + "...";
        }

        // Observation only: recording sits on the request / transport paths, so a fault in
        // it must never become a fault in the queue.
        private static void RecordActivity(string kind, string message)
        {
            try { ToolActivityRing.Record(ActivityTool, kind, message); }
            catch (Exception ex) { GlobalLogger.Error("SongRequestService", "activity record failed", ex); }
        }

        // ── Status pill ─────────────────────────────────────────────────────
        /// <summary>
        /// What the strip's status pill says.
        ///
        /// <para><see cref="QueueingNothingRenders"/> is the state the master switch cannot
        /// say, and it is this tool's own documented honest case: the player is an iframe in
        /// an OBS overlay, so with no browser surface attached to ANY layer there is no
        /// player and a queue simply fills up in silence.</para>
        ///
        /// <para>Deliberately NOT claimed: "the layer that hosts the player is not
        /// rendering". This service holds no layer id — the player is a Player.Embed widget
        /// in whichever layer the streamer put it in, and nothing here knows which — so the
        /// only honest overlay statement available is "no browser surface is attached at
        /// all". While one IS attached, the pill says nothing about the player.</para>
        /// </summary>
        public enum SongRequestPillState
        {
            /// <summary>The tool is switched off.</summary>
            Dormant,
            /// <summary>On, but no browser surface is attached to any layer, so nothing can
            /// play whatever the queue does.</summary>
            QueueingNothingRenders,
            /// <summary>A track is selected and running.</summary>
            Playing,
            /// <summary>A track is selected and held.</summary>
            Paused,
            /// <summary>Nothing is selected.</summary>
            Idle,
        }

        /// <summary>Pure state machine behind <see cref="PillState"/>.</summary>
        internal static SongRequestPillState ComputePillState(
            bool enabled, SongPlayerState state, bool anyOverlaySurfaceAttached)
        {
            if (!enabled) return SongRequestPillState.Dormant;
            // Ranks above the transport states on purpose: "playing" while nothing can
            // render is the lie this state exists to prevent.
            if (!anyOverlaySurfaceAttached) return SongRequestPillState.QueueingNothingRenders;
            return state switch
            {
                SongPlayerState.Playing => SongRequestPillState.Playing,
                SongPlayerState.Paused => SongRequestPillState.Paused,
                _ => SongRequestPillState.Idle,
            };
        }

        /// <summary>The live pill state. The overlay half asks the registry for ANY attached
        /// browser surface — kind-blind, and the widest of the three presence arms — so the
        /// "nothing renders" claim is only made when there is definitively not a single
        /// browser attached to any layer.</summary>
        public SongRequestPillState PillState
        {
            get
            {
                SongPlayerState state;
                lock (_gate) state = _state;
                bool anySurface;
                try { anySurface = LayerRegistry.Instance.GetLayerIdsWithAnyConnection().Count > 0; }
                catch (Exception ex)
                {
                    GlobalLogger.Error("SongRequestService", "overlay presence read failed", ex);
                    // Unknown ⇒ do not accuse the overlay.
                    anySurface = true;
                }
                return ComputePillState(Active, state, anySurface);
            }
        }

        /// <summary>
        /// Returns a charged request's points. True when they actually landed back on the
        /// balance — which is also the ONLY case that clears <see cref="SongRequestEntry.PointsPaid"/>.
        /// That field is the retry record: zeroing it on a failed refund would destroy the
        /// one piece of evidence that the viewer is out of pocket, so a caller holding the
        /// entry afterwards (the chat verbs, the panel, a node) can read a surviving
        /// PointsPaid as "this charge was never given back". Every failure path logs at
        /// CriticalError naming the viewer and the amount, because the fix is a manual
        /// grant and the streamer cannot make it without those two facts.
        /// </summary>
        private async Task<bool> RefundAsync(SongRequestEntry entry)
        {
            if (entry.PointsPaid <= 0) return true;          // nothing was ever charged
            long owed = entry.PointsPaid;
            string who = entry.RequestedByLogin;

            var refund = RefundPoints;
            if (refund is null)
            {
                GlobalLogger.Log(
                    $"Song Request: {owed.ToString(CultureInfo.InvariantCulture)} points charged to {who} could NOT be returned — the points economy is not wired. Grant them back by hand.",
                    "SongRequestService", LogLevel.CriticalError);
                return false;
            }

            bool ok;
            try { ok = await refund(who, owed).ConfigureAwait(false); }
            catch (Exception ex)
            {
                GlobalLogger.Error("SongRequestService",
                    $"point refund threw — {owed.ToString(CultureInfo.InvariantCulture)} points still owed to {who}", ex);
                return false;
            }

            if (!ok)
            {
                GlobalLogger.Log(
                    $"Song Request: the points economy refused to return {owed.ToString(CultureInfo.InvariantCulture)} points to {who} — the Loyalty tool is switched off or its currency table is missing. Grant them back by hand.",
                    "SongRequestService", LogLevel.CriticalError);
                return false;
            }

            entry.PointsPaid = 0;
            return true;
        }

        /// <summary>The clause appended to a removal's chat line when the points did NOT go
        /// back. A surviving <see cref="SongRequestEntry.PointsPaid"/> is exactly that
        /// signal, since <see cref="RefundAsync"/> clears the field only on success.
        ///
        /// ★ The deliberate call on who gets told: chat IS told, but only on a line that was
        /// already going out to the viewer who asked for the removal (!wrongsong, !removesong,
        /// !srclear). They paid for the request, and staying quiet would leave them believing
        /// the refund happened. Removals nobody asked for in chat — a panel button, the
        /// late-refusal race inside <see cref="RequestAsync"/> — stay log-only, because there
        /// is no line already addressed to that viewer to hang the note on and a bare
        /// unprompted message about the streamer's Loyalty config helps nobody.</summary>
        internal static string UnrefundedNote(SongRequestEntry? entry)
            => entry is not null && entry.PointsPaid > 0
                ? $" NOTE: {entry.PointsPaid.ToString(CultureInfo.InvariantCulture)} points could not be returned — the points economy is unavailable."
                : "";

        internal static string NormalizeLogin(string? s) => (s ?? string.Empty).Trim().ToLowerInvariant();

        // ── Script events ───────────────────────────────────────────────────
        // Every Song.On* raise binds the SAME four base tokens plus one event-specific
        // extra, and the token spellings here are the source of truth the three Architect
        // analyzer sites (ScriptExporter's Song.On* arm, AutocompleteScopeBuilder,
        // VarChainAnalyzer.ResultEmitterMap) are synced to. video_id / duration_seconds /
        // skipped_by are snake_case: the exporter's generic tail would lower-case the
        // socket name to "videoid", so those sockets get an explicit mapping arm.
        private void RaiseSongEvent(string phoenixEvent, SongRequestEntry entry,
                                    string extraKey, string extraValue)
        {
            var raise = RaiseScriptEvent;
            if (raise is null) return;
            try
            {
                var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["event.title"] = entry.DisplayTitle,
                    ["event.requester"] = entry.RequestedBy,
                    ["event.video_id"] = entry.VideoId,
                    [extraKey] = extraValue,
                };
                raise(phoenixEvent, vars);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("SongRequestService", $"RaiseScriptEvent({phoenixEvent}) failed", ex);
            }
        }

        // ── Overlay Live Channel ────────────────────────────────────────────

        // Provenance tag on every songrequest key. Identical on EVERY publish by design:
        // the store inherits a declared ExpectedInterval across writes only while the
        // Source string matches, so a second spelling here would let one publish silently
        // drop the cadence and the keys could never report Stale.
        private const string LiveSource = "tool:SongRequest";

        /// <summary>
        /// The publish cadence, and the ExpectedInterval every <c>songrequest.*</c> key
        /// declares.
        ///
        /// ★ A live-channel key MUST declare a cadence to be honest: the store has no
        /// remove API and no TTL, so a key published once reports Active for the rest of
        /// the session — a cleared queue or a switched-off tool would keep painting the
        /// last track as live in OBS. Declaring the interval is only half of it; something
        /// has to keep publishing, or a perfectly current idle player would report Stale
        /// within seconds. Hence the heartbeat. The store COALESCES an identical value
        /// (refreshes LastWriteUtc, does not dirty the key, ships no frame), so a quiet
        /// tool costs one dictionary write per key per tick and nothing on the wire.
        ///
        /// 2 s (⇒ a 6 s stale window at StaleIntervalMultiplier=3) rather than
        /// User-Management's 5 s: this is transport state driving audio, and six seconds of
        /// a wedged Hub still painting "playing" is already at the edge of tolerable.
        /// Every real change publishes immediately anyway — the cadence only bounds how
        /// long a DEAD publisher can keep lying.
        /// </summary>
        private static readonly TimeSpan LiveInterval = TimeSpan.FromSeconds(2);

        /// <summary>How many queue entries the <c>songrequest.queue</c> array carries. A
        /// bound, not a preference: the array goes out on the wire whenever it changes.</summary>
        private const int LiveQueueMax = 25;

        private readonly object _loopGate = new();
        private CancellationTokenSource? _loopCts;
        private Task? _loopTask;
        private bool _loopStarted;
        // Tracks whether the last publish was the ON shape. The off-transition publishes
        // ONE explicit idle snapshot: decaying to Stale alone would leave a widget painting
        // its last track for the whole stale window, and "the tool was switched off" is
        // knowable right now. After that one write the keys go quiet and decay honestly.
        private bool _overlayWasOn;
        // The heartbeat and every mutation publish, so they are serialized — otherwise two
        // overlapping snapshots could land out of order and leave the channel holding the
        // OLDER state until the next tick corrected it.
        private readonly object _publishGate = new();

        private void StartOverlayPump()
        {
            lock (_loopGate)
            {
                if (_loopStarted) return;
                _loopStarted = true;
                _loopCts = new CancellationTokenSource();
                var ct = _loopCts.Token;
                _loopTask = Task.Run(() => OverlayLoopAsync(ct));
            }
        }

        private async Task OverlayLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(LiveInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                try { PublishOverlay(); }
                catch (Exception ex) { GlobalLogger.Error("SongRequestService", "overlay publish failed", ex); }
            }
        }

        /// <summary>
        /// Publishes the player + queue under the <c>songrequest.</c> root. THIS KEY LIST IS
        /// THE CONTRACT the overlay player (V15's <c>Player.Embed</c> widget sink) binds
        /// against — the browser derives its subscription from literal key text, so a rename
        /// here is a silently blank widget with no error anywhere:
        ///
        ///   songrequest.state             "idle" | "playing" | "paused"
        ///   songrequest.video_id          the 11-char YouTube id ("" while idle)
        ///   songrequest.title             the video title, or the id when unknown
        ///   songrequest.requester         display name of who asked for it
        ///   songrequest.duration_seconds  length, or 0 when unknown
        ///   songrequest.volume            0-100
        ///   songrequest.play_token        increments on every new selection
        ///   songrequest.queue_length      waiting requests (the playing one excluded)
        ///   songrequest.next_title        the next APPROVED entry, or ""
        ///   songrequest.next_requester    ditto
        ///   songrequest.queue             array of { position, title, requester, video_id,
        ///                                   duration_seconds }, capped at LiveQueueMax
        ///
        /// play_token exists because a video-id compare cannot distinguish "load this" from
        /// "resume what you have" when the same song is requested twice in a row: the
        /// player reloads the iframe when the token moves and obeys `state` otherwise.
        ///
        /// It carries a SECOND load-bearing job, and the two are the same fact seen from
        /// both ends of the wire: the token the player was given is the token it quotes back
        /// in <c>MEDIA_ENDED</c>, which is what lets two OBS sources report one ended track
        /// without double-advancing. See <see cref="NotifyMediaEndedAsync"/>. That is why
        /// this key is published even while idle-ish state churns around it — a player that
        /// never received a token could not report an end that would be accepted.
        /// </summary>
        private void PublishOverlay()
        {
            lock (_publishGate)
            {
                var store = LiveStore;
                if (!Active)
                {
                    if (!_overlayWasOn) return;
                    _overlayWasOn = false;
                    // ★ THE RETRACTION MUST COVER EVERY KEY THE LIVE BRANCH BELOW PUBLISHES.
                    // This branch fires ONCE on the transition to off and then returns early
                    // forever, so a key omitted here is never republished and never corrected
                    // — it sits in every bound widget at its last live value while its ten
                    // siblings read empty. `play_token` and `volume` were exactly that until
                    // 2026-08-03: a switched-off tool published an idle queue next to a stale
                    // token and a stale volume. (PollsService and RanksService avoid the whole
                    // class by walking their own LiveKeys list here instead of a second
                    // hand-written one; this list stays hand-written only because its
                    // retraction values are per-key typed neutrals rather than a blanket null.)
                    store.PublishString("songrequest.state", "idle", LiveSource, LiveInterval);
                    store.PublishString("songrequest.video_id", "", LiveSource, LiveInterval);
                    store.PublishString("songrequest.title", "", LiveSource, LiveInterval);
                    store.PublishString("songrequest.requester", "", LiveSource, LiveInterval);
                    store.PublishNumber("songrequest.duration_seconds", 0, LiveSource, LiveInterval);
                    // 0 is the documented never-matchable token — the value a session that has
                    // selected nothing carries, which NotifyMediaEndedAsync can never accept.
                    // Retracting to it is therefore also the correct SAFE value, not just a
                    // tidy one: no stale report can be honoured against it.
                    store.PublishNumber("songrequest.play_token", 0, LiveSource, LiveInterval);
                    // null rather than 0, and it is the one key here with no typed neutral:
                    // volume is a persisted SETTING, not a runtime observation, so publishing
                    // 0 would read as "muted" — a claim about the streamer's config that a
                    // switched-off tool has no business making. Absent is the honest answer,
                    // and it is what PollsService retracts every one of its keys to.
                    store.Publish("songrequest.volume", null, LiveSource, LiveInterval);
                    store.PublishNumber("songrequest.queue_length", 0, LiveSource, LiveInterval);
                    store.PublishString("songrequest.next_title", "", LiveSource, LiveInterval);
                    store.PublishString("songrequest.next_requester", "", LiveSource, LiveInterval);
                    store.Publish("songrequest.queue", new JsonArray(), LiveSource, LiveInterval);
                    return;
                }

                var snap = Snapshot();
                var next = snap.Queue.Count > 0 ? FirstApproved(snap.Queue) : null;

                store.PublishString("songrequest.state", StateToken(snap.State), LiveSource, LiveInterval);
                store.PublishString("songrequest.video_id", snap.Current?.VideoId ?? "", LiveSource, LiveInterval);
                store.PublishString("songrequest.title", snap.Current?.DisplayTitle ?? "", LiveSource, LiveInterval);
                store.PublishString("songrequest.requester", snap.Current?.RequestedBy ?? "", LiveSource, LiveInterval);
                store.PublishNumber("songrequest.duration_seconds", snap.Current?.DurationSeconds ?? 0, LiveSource, LiveInterval);
                store.PublishNumber("songrequest.volume", snap.Volume, LiveSource, LiveInterval);
                store.PublishNumber("songrequest.play_token", snap.PlayToken, LiveSource, LiveInterval);
                store.PublishNumber("songrequest.queue_length", snap.Queue.Count, LiveSource, LiveInterval);
                store.PublishString("songrequest.next_title", next?.DisplayTitle ?? "", LiveSource, LiveInterval);
                store.PublishString("songrequest.next_requester", next?.RequestedBy ?? "", LiveSource, LiveInterval);

                var board = new JsonArray();
                for (int i = 0; i < snap.Queue.Count && i < LiveQueueMax; i++)
                {
                    var e = snap.Queue[i];
                    board.Add(new JsonObject
                    {
                        ["position"] = i + 1,
                        ["title"] = e.DisplayTitle,
                        ["requester"] = e.RequestedBy,
                        ["video_id"] = e.VideoId,
                        ["duration_seconds"] = e.DurationSeconds,
                    });
                }
                store.Publish("songrequest.queue", board, LiveSource, LiveInterval);
                _overlayWasOn = true;
            }
        }

        private static SongRequestEntry? FirstApproved(IReadOnlyList<SongRequestEntry> queue)
        {
            foreach (var e in queue)
                if (e.Status == SongRequestStatus.Queued) return e;
            return null;
        }

        // Lowercase literals, matching the live-channel key convention. Not
        // State.ToString() — the enum names are C#-cased and a rename would silently move
        // the value the browser branches on.
        internal static string StateToken(SongPlayerState state) => state switch
        {
            SongPlayerState.Playing => "playing",
            SongPlayerState.Paused => "paused",
            _ => "idle",
        };

        // ── Built-in chat-command core (testable; ScriptManager supplies the send) ──
        /// <summary>
        /// The built-in Song Request chat commands, factored here so they can be unit-tested
        /// without a full ScriptManager. Twelve verbs: seven viewer-facing
        /// (<c>!sr</c> / <c>!song</c> / <c>!next</c> / <c>!when</c> / <c>!wrongsong</c> /
        /// <c>!voteskip</c> / <c>!volume</c>) and five moderator ones (<c>!skip</c> /
        /// <c>!pause</c> / <c>!play</c> / <c>!removesong</c> / <c>!srclear</c>).
        ///
        /// <c>!volume</c> straddles both sets on purpose: with no argument it is a read and
        /// answers to the view gate, with a number it is a write and answers to the mod
        /// gate. Every op is role-gated, and a role-denied command is still CONSUMED
        /// (returns true) but silent — the same convention the Counters/Quotes parsers use,
        /// so a denied viewer's line never falls through to an authored on_chat script that
        /// would answer it anyway.
        ///
        /// Returns true when a Song Request command was recognized (so the caller
        /// suppresses the author on_chat fan-out). DEFAULT-OFF IS A TOTAL NO-OP. Bot
        /// self-messages are already dropped upstream.
        /// </summary>
        public async Task<bool> TryHandleChatCommandAsync(ChatMessage msg, Func<string, Task> replyAsync)
        {
            if (!Active || msg is null) return false;
            replyAsync ??= static _ => Task.CompletedTask;

            var cfg = _config;
            string text = (msg.Message ?? string.Empty).Trim();
            if (text.Length < 2 || text[0] != '!') return false;

            string body = text[1..];
            int sp = body.IndexOf(' ');
            string token = (sp < 0 ? body : body[..sp]).Trim();
            if (token.Length == 0) return false;
            string rest = sp < 0 ? string.Empty : body[(sp + 1)..].Trim();

            // This tool's Normalize already strips a configured '!' at load, so the
            // comparison was never broken here — it routes through the shared
            // canonicalizer anyway so all eleven providers answer one rule, and a
            // config pushed straight from the panel without a load pass behaves.
            static bool Eq(string a, string b) => ChatVerb.Matches(b, a);

            // Role checks consult the User-Management group overlay (a group-granted
            // Mod/VIP/Sub passes like the platform rank, and the Regular tick resolves off
            // the same overlay; passthrough while dormant).
            var eff = UserManagementService.Instance.Effective(msg);
            bool RoleOk(SongRoles? r) =>
                r != null && r.Allows(eff.IsSub, eff.IsVip, eff.IsMod, msg.IsBroadcaster, eff.IsRegular);

            // The login is what every per-user cap keys on; Twitch fills Username with the
            // DISPLAY name, so fall back to it only when no login came through.
            string login = NormalizeLogin(string.IsNullOrWhiteSpace(msg.Login) ? msg.Username : msg.Login);
            string display = string.IsNullOrWhiteSpace(msg.Username) ? login : msg.Username;

            // ── !sr <link | id | search phrase> ────────────────────────────
            if (Eq(token, cfg.RequestCommand))
            {
                if (!RoleOk(cfg.RequestRoles)) return true;
                if (rest.Length == 0)
                {
                    await replyAsync($"Usage: !{cfg.RequestCommand} <YouTube link, video id, or search words>").ConfigureAwait(false);
                    return true;
                }
                var res = await RequestAsync(login, display, eff.IsSub, rest).ConfigureAwait(false);
                await replyAsync(DescribeRequest(cfg, res, display)).ConfigureAwait(false);
                return true;
            }

            // ── !song ──────────────────────────────────────────────────────
            if (Eq(token, cfg.CurrentCommand))
            {
                if (!RoleOk(cfg.ViewRoles)) return true;
                var snap = Snapshot();
                if (snap.Current is null)
                    await replyAsync("Nothing is playing right now.").ConfigureAwait(false);
                else
                    await replyAsync(
                        $"{(snap.State == SongPlayerState.Paused ? "Paused" : "Now playing")}: " +
                        $"{snap.Current.DisplayTitle}{FormatLength(snap.Current.DurationSeconds)} — requested by {snap.Current.RequestedBy}")
                        .ConfigureAwait(false);
                return true;
            }

            // ── !next ──────────────────────────────────────────────────────
            if (Eq(token, cfg.NextCommand))
            {
                if (!RoleOk(cfg.ViewRoles)) return true;
                var up = UpNext();
                await replyAsync(up is null
                    ? "Nothing is queued up next."
                    : $"Up next: {up.DisplayTitle}{FormatLength(up.DurationSeconds)} — requested by {up.RequestedBy}")
                    .ConfigureAwait(false);
                return true;
            }

            // ── !when ──────────────────────────────────────────────────────
            if (Eq(token, cfg.WhenCommand))
            {
                if (!RoleOk(cfg.ViewRoles)) return true;
                int pos = PositionOf(login);
                await replyAsync(pos == 0
                    ? $"{display}, you have nothing in the queue."
                    : $"{display}, your song is #{pos.ToString(CultureInfo.InvariantCulture)} in the queue.")
                    .ConfigureAwait(false);
                return true;
            }

            // ── !wrongsong ─────────────────────────────────────────────────
            if (Eq(token, cfg.WrongSongCommand))
            {
                if (!RoleOk(cfg.RequestRoles)) return true;
                var gone = await RemoveLastForAsync(login).ConfigureAwait(false);
                await replyAsync(gone is null
                    ? $"{display}, you have nothing waiting to remove."
                    : $"Removed {gone.DisplayTitle} from the queue.{UnrefundedNote(gone)}")
                    .ConfigureAwait(false);
                return true;
            }

            // ── !voteskip ──────────────────────────────────────────────────
            if (Eq(token, cfg.VoteSkipCommand))
            {
                if (!RoleOk(cfg.VoteSkipRoles)) return true;
                if (cfg.VoteSkipThreshold <= 0)
                {
                    await replyAsync("Vote-skipping is switched off.").ConfigureAwait(false);
                    return true;
                }
                var vote = await VoteSkipAsync(login).ConfigureAwait(false);
                if (vote.Skipped)
                    await replyAsync("Vote passed — skipping.").ConfigureAwait(false);
                else if (vote.Counted)
                    await replyAsync($"Skip vote {vote.Votes.ToString(CultureInfo.InvariantCulture)}/{vote.Needed.ToString(CultureInfo.InvariantCulture)}.").ConfigureAwait(false);
                else if (Snapshot().Current is null)
                    await replyAsync("Nothing is playing to skip.").ConfigureAwait(false);
                else
                    await replyAsync($"{display}, you already voted.").ConfigureAwait(false);
                return true;
            }

            // ── !volume [0-100] ────────────────────────────────────────────
            if (Eq(token, cfg.VolumeCommand))
            {
                if (rest.Length == 0)
                {
                    if (!RoleOk(cfg.ViewRoles)) return true;
                    await replyAsync($"Player volume is {Snapshot().Volume.ToString(CultureInfo.InvariantCulture)}%.").ConfigureAwait(false);
                    return true;
                }
                if (!RoleOk(cfg.ModRoles)) return true;
                if (!int.TryParse(rest.TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int vol))
                {
                    await replyAsync($"Usage: !{cfg.VolumeCommand} <0-100>").ConfigureAwait(false);
                    return true;
                }
                int applied = await SetVolumeAsync(vol).ConfigureAwait(false);
                await replyAsync($"Player volume set to {applied.ToString(CultureInfo.InvariantCulture)}%.").ConfigureAwait(false);
                return true;
            }

            // ── !skip ──────────────────────────────────────────────────────
            if (Eq(token, cfg.SkipCommand))
            {
                if (!RoleOk(cfg.ModRoles)) return true;
                if (Snapshot().Current is null)
                {
                    await replyAsync("Nothing is playing to skip.").ConfigureAwait(false);
                    return true;
                }
                var nxt = await AdvanceAsync(display).ConfigureAwait(false);
                await replyAsync(nxt is null
                    ? "Skipped — the queue is empty."
                    : $"Skipped. Now playing: {nxt.DisplayTitle} — requested by {nxt.RequestedBy}")
                    .ConfigureAwait(false);
                return true;
            }

            // ── !pause ─────────────────────────────────────────────────────
            if (Eq(token, cfg.PauseCommand))
            {
                if (!RoleOk(cfg.ModRoles)) return true;
                await replyAsync(Pause() ? "Paused." : "Nothing is playing.").ConfigureAwait(false);
                return true;
            }

            // ── !play ──────────────────────────────────────────────────────
            if (Eq(token, cfg.PlayCommand))
            {
                if (!RoleOk(cfg.ModRoles)) return true;
                bool started = await ResumeAsync().ConfigureAwait(false);
                if (!started)
                {
                    await replyAsync(Snapshot().Current is null
                        ? "Nothing is queued."
                        : "Already playing.").ConfigureAwait(false);
                    return true;
                }
                var snap = Snapshot();
                await replyAsync(snap.Current is null
                    ? "Playing."
                    : $"Playing: {snap.Current.DisplayTitle} — requested by {snap.Current.RequestedBy}")
                    .ConfigureAwait(false);
                return true;
            }

            // ── !removesong N ──────────────────────────────────────────────
            if (Eq(token, cfg.RemoveCommand))
            {
                if (!RoleOk(cfg.ModRoles)) return true;
                if (!int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pos))
                {
                    await replyAsync($"Usage: !{cfg.RemoveCommand} <queue position>").ConfigureAwait(false);
                    return true;
                }
                var gone = await RemoveAtAsync(pos).ConfigureAwait(false);
                await replyAsync(gone is null
                    ? $"No request at #{pos.ToString(CultureInfo.InvariantCulture)}."
                    : $"Removed {gone.DisplayTitle} (requested by {gone.RequestedBy}).{UnrefundedNote(gone)}")
                    .ConfigureAwait(false);
                return true;
            }

            // ── !srclear ───────────────────────────────────────────────────
            if (Eq(token, cfg.ClearCommand))
            {
                if (!RoleOk(cfg.ModRoles)) return true;
                var cleared = await ClearAsync().ConfigureAwait(false);
                // One line, not one per stranded viewer: !srclear can drop a whole queue,
                // and N apology messages would be worse than the count plus a log to read.
                string strandedNote = cleared.Unrefunded > 0
                    ? $" NOTE: {cleared.Unrefunded.ToString(CultureInfo.InvariantCulture)} of them could not be refunded — the points economy is unavailable."
                    : "";
                await replyAsync(cleared.Dropped == 0
                    ? "The queue was already empty."
                    : $"Cleared {cleared.Dropped.ToString(CultureInfo.InvariantCulture)} request(s).{strandedNote}")
                    .ConfigureAwait(false);
                return true;
            }

            return false;   // not a Song Request command — author scripts handle it normally
        }

        /// <summary>The chat sentence for one request verdict. Every refusal says WHY in
        /// terms the viewer can act on; "no" with no reason is what makes a request tool
        /// feel broken.</summary>
        internal static string DescribeRequest(SongRequestConfig cfg, SongRequestResult res, string display)
        {
            switch (res.Outcome)
            {
                case SongRequestOutcome.Accepted:
                    string where = res.Entry?.Status == SongRequestStatus.Pending
                        ? "waiting for a moderator"
                        : $"#{res.Position.ToString(CultureInfo.InvariantCulture)} in the queue";
                    string cost = res.Charged > 0
                        ? $" ({res.Charged.ToString(CultureInfo.InvariantCulture)} points)"
                        : "";
                    return $"Added {res.Entry?.DisplayTitle} — {where}{cost}.";

                case SongRequestOutcome.SearchNeedsApiKey:
                    return $"{display}, searching by name needs a YouTube API key the streamer hasn't set — post a YouTube link or the video id instead.";
                case SongRequestOutcome.NotFound:
                    return $"{display}, I couldn't find that on YouTube.";
                case SongRequestOutcome.NotEmbeddable:
                    return $"{display}, that video can't be played in an overlay — the uploader disabled embedding.";
                case SongRequestOutcome.TooLong:
                    return $"{display}, that one is longer than the {FormatDuration(cfg.MaxDurationSeconds)} limit.";
                case SongRequestOutcome.QueueFull:
                    return $"{display}, the queue is full right now.";
                case SongRequestOutcome.UserLimit:
                    return $"{display}, you already have {cfg.MaxPerUser.ToString(CultureInfo.InvariantCulture)} request(s) waiting.";
                case SongRequestOutcome.OnCooldown:
                    return $"{display}, you can request again in {res.CooldownRemaining.ToString(CultureInfo.InvariantCulture)}s.";
                case SongRequestOutcome.Duplicate:
                    return $"{display}, that song is already in the queue.";
                case SongRequestOutcome.EconomyOff:
                    return $"{display}, song requests cost {cfg.PointCost.ToString(CultureInfo.InvariantCulture)} points, but the points economy is switched off.";
                case SongRequestOutcome.NoFunds:
                    return $"{display}, you don't have enough points for that.";
                case SongRequestOutcome.EmptyRequest:
                    return $"Usage: !{cfg.RequestCommand} <YouTube link, video id, or search words>";
                default:
                    return $"{display}, song requests are switched off.";
            }
        }

        /// <summary>" (4:13)" for a known length, "" for an unknown one — never "(0:00)",
        /// which would read as a zero-length video rather than "no API key, no length".</summary>
        internal static string FormatLength(int seconds)
            => seconds > 0 ? $" ({FormatDuration(seconds)})" : "";

        internal static string FormatDuration(int seconds)
        {
            if (seconds <= 0) return "0:00";
            int h = seconds / 3600, m = seconds % 3600 / 60, s = seconds % 60;
            return h > 0
                ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", h, m, s)
                : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", m, s);
        }
    }
}
