using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: Song Request — the Hub-side wiring of the YouTube request-queue
    // pre-build tool (sibling of Quotes / Counters / Loyalty). Four responsibilities:
    //
    //   1. Seam injection — SongRequestService is deliberately Hub-state-agnostic (the
    //      SchedulingService shape), so the three things that need Hub state are wired at
    //      the top of RegisterSongRequestCommands: the Song.On* script-event raise, the
    //      YouTube Data API resolve (which needs the streamer's key and the blessed
    //      outbound HTTP path), and the Loyalty charge/refund pair.
    //
    //   2. The fifteen song.* script commands backing the Architect Song.* nodes — eleven
    //      flow control commands and four inline value reads — the locked three-way
    //      manifest contract.
    //
    //   3. The BUILT-IN chat-command entry (TryHandleSongRequestChatCommandAsync),
    //      registered as the SongRequest provider in RegisterBuiltInChatProviders (AFTER
    //      Quotes, BEFORE CustomCommands). Thin wrapper: the parse/gate/queue logic lives
    //      on SongRequestService so it is unit-testable without a ScriptManager, and this
    //      wrapper supplies the per-platform reply send.
    //
    //   4. The YouTube Data API v3 resolver itself — the only net-new outbound integration
    //      in this tool, and the only place the optional API key is ever read.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        private void RegisterSongRequestCommands()
        {
            var svc = SongRequestService.Instance;

            // ── Seam: script events ──────────────────────────────────────────
            // Song.OnQueued / Song.OnPlay / Song.OnSkip flow back through the generic-event
            // dispatcher with pre-built vars, fired-and-forgotten through
            // AsyncErrorBoundary.SafeRunAsync so a faulting handler routes to the log
            // instead of becoming an unobserved Task.
            svc.RaiseScriptEvent = (phoenixEvent, vars) =>
            {
                _ = AsyncErrorBoundary.SafeRunAsync(
                    () => ExecuteGenericEventAsync(phoenixEvent, default, vars),
                    "SongRequestService", $"RaiseScriptEvent({phoenixEvent})");
            };

            // ── Seam: YouTube resolve ────────────────────────────────────────
            svc.YouTubeLookup = ResolveYouTubeAsync;

            // ── Seam: points charge / refund ─────────────────────────────────
            // A purchase, not an admin adjustment: LoyaltyService.ChargeAsync REFUSES an
            // unaffordable debit, where RemoveAsync would floor the viewer at zero and
            // report success. Both halves translate LoyaltyOutcome into the tool's own
            // vocabulary so the service never has to know the economy's types.
            svc.ChargePoints = async (login, amount) =>
            {
                var res = await LoyaltyService.Instance.ChargeAsync(login, amount, "song request").ConfigureAwait(false);
                return res.Outcome switch
                {
                    LoyaltyOutcome.Ok => SongChargeResult.Charged(amount),
                    // Disabled = the Loyalty master toggle is off; TableMissing = its
                    // currency table was never created. Both mean the streamer set a price
                    // the economy cannot collect, which is a configuration problem the
                    // viewer's reply names rather than a "you're broke".
                    LoyaltyOutcome.Disabled or LoyaltyOutcome.TableMissing => SongChargeResult.Fail(SongChargeOutcome.EconomyOff),
                    LoyaltyOutcome.NoFunds => SongChargeResult.Fail(SongChargeOutcome.NoFunds),
                    _ => SongChargeResult.Fail(SongChargeOutcome.Error),
                };
            };
            // RefundAsync never throws for a refused refund — it RETURNS Disabled (Loyalty
            // master toggle off), TableMissing (the currency table was renamed away) or
            // Invalid. Discarding that result is how a viewer's points used to vanish in
            // silence, so the seam carries the verdict back and the reason is named here,
            // where the economy's own vocabulary is still in scope; the service logs the
            // viewer and the amount on the other side of the boolean.
            svc.RefundPoints = async (login, amount) =>
            {
                var res = await LoyaltyService.Instance.RefundAsync(login, amount, "song request refund").ConfigureAwait(false);
                if (res.Ok) return true;
                GlobalLogger.Log(
                    $"Song Request refund refused by the points economy: {res.Outcome} for {amount.ToString(CultureInfo.InvariantCulture)} to {login}.",
                    "SongRequest", LogLevel.CriticalError);
                return false;
            };

            // ── song.* flow control commands ─────────────────────────────────
            // Void → return null; flow continues through the node's Done output.

            _engine.RegisterCommand("song.request", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string query = StripBareQuotes(bound?.GetOrDefault<string>("Query", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                var who = ResolveSongRequester(bound?.GetOrDefault<string>("User", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1));
                if (query.Trim().Length == 0 || who.Login.Length == 0) return null;
                // A graph-driven request is charged and capped exactly like a chat one: the
                // node is the SAME capability the !sr verb exposes, not a privileged
                // back door. isSub is unknowable from a bare name here, so the graph path
                // never gets the subscriber discount — a graph that wants it charges
                // through Loyalty itself.
                await SongRequestService.Instance
                    .RequestAsync(who.Login, who.Display, isSub: false, query, _engine.ExecutionToken)
                    .ConfigureAwait(false);
                return null;
            });

            // Skip means "cut the playing track short", so an idle player is a no-op — the
            // node's own description says so, and !skip guards the same way. Starting the
            // queue is Song.Resume's job, and letting Skip do it too would make the two
            // nodes silently interchangeable on an idle player.
            _engine.RegisterCommand("song.skip", async (args) =>
            {
                if (SongRequestService.Instance.Snapshot().Current is not null)
                    await SongRequestService.Instance.AdvanceAsync("script").ConfigureAwait(false);
                return null;
            });

            _engine.RegisterCommand("song.pause", async (args) =>
            {
                SongRequestService.Instance.Pause();
                return null;
            });

            _engine.RegisterCommand("song.resume", async (args) =>
            {
                await SongRequestService.Instance.ResumeAsync().ConfigureAwait(false);
                return null;
            });

            _engine.RegisterCommand("song.remove", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                int pos = ReadSongInt(bound, "Position", args, 0);
                if (pos > 0) await SongRequestService.Instance.RemoveAtAsync(pos).ConfigureAwait(false);
                return null;
            });

            _engine.RegisterCommand("song.remove_last", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string user = ResolveSongUser(StripBareQuotes(bound?.GetOrDefault<string>("User", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0)));
                if (user.Length > 0) await SongRequestService.Instance.RemoveLastForAsync(user).ConfigureAwait(false);
                return null;
            });

            _engine.RegisterCommand("song.clear", async (args) =>
            {
                await SongRequestService.Instance.ClearAsync().ConfigureAwait(false);
                return null;
            });

            _engine.RegisterCommand("song.set_volume", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                int vol = ReadSongInt(bound, "Volume", args, 0);
                await SongRequestService.Instance.SetVolumeAsync(vol).ConfigureAwait(false);
                return null;
            });

            _engine.RegisterCommand("song.vote_skip", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string user = ResolveSongUser(StripBareQuotes(bound?.GetOrDefault<string>("User", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0)));
                if (user.Length > 0) await SongRequestService.Instance.VoteSkipAsync(user).ConfigureAwait(false);
                return null;
            });

            _engine.RegisterCommand("song.approve", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                int pos = ReadSongInt(bound, "Position", args, 0);
                if (pos > 0) SongRequestService.Instance.Approve(pos);
                return null;
            });

            _engine.RegisterCommand("song.deny", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                int pos = ReadSongInt(bound, "Position", args, 0);
                if (pos > 0) await SongRequestService.Instance.DenyAsync(pos).ConfigureAwait(false);
                return null;
            });

            // ── song.* inline value reads ────────────────────────────────────
            // Pure-data: the exporter emits the call inline (ComputeInlineValue / the
            // Song.Current + Song.UpNext arms in ResolveOutputFromNode) and the engine
            // round-trips the return string into the node's primary value output.

            // Song.Current is multi-output — Title / Requester / VideoId / State / Volume
            // must all describe the SAME instant, so the node hoists ONE song.current(base)
            // call whose result vars back every socket. Same ResultBase idiom as
            // quote.get / the giveaway.* reads.
            _engine.RegisterCommand("song.current", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string baseVar = StripBareQuotes(bound?.GetOrDefault<string>("ResultBase", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                var snap = SongRequestService.Instance.Snapshot();
                if (!string.IsNullOrEmpty(baseVar))
                {
                    _engine.SetLocalResultVar($"{baseVar}_title", snap.Current?.DisplayTitle ?? "");
                    _engine.SetLocalResultVar($"{baseVar}_requester", snap.Current?.RequestedBy ?? "");
                    _engine.SetLocalResultVar($"{baseVar}_video_id", snap.Current?.VideoId ?? "");
                    _engine.SetLocalResultVar($"{baseVar}_state", SongRequestService.StateToken(snap.State));
                    _engine.SetLocalResultVar($"{baseVar}_volume", snap.Volume.ToString(CultureInfo.InvariantCulture));
                }
                return snap.Current?.DisplayTitle ?? "";
            });

            _engine.RegisterCommand("song.up_next", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string baseVar = StripBareQuotes(bound?.GetOrDefault<string>("ResultBase", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                var next = SongRequestService.Instance.UpNext();
                if (!string.IsNullOrEmpty(baseVar))
                {
                    _engine.SetLocalResultVar($"{baseVar}_title", next?.DisplayTitle ?? "");
                    _engine.SetLocalResultVar($"{baseVar}_requester", next?.RequestedBy ?? "");
                    _engine.SetLocalResultVar($"{baseVar}_video_id", next?.VideoId ?? "");
                }
                return next?.DisplayTitle ?? "";
            });

            _engine.RegisterCommand("song.queue_length", async (args) =>
                SongRequestService.Instance.QueueLength.ToString(CultureInfo.InvariantCulture));

            _engine.RegisterCommand("song.queue_position", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string user = ResolveSongUser(StripBareQuotes(bound?.GetOrDefault<string>("User", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0)));
                return SongRequestService.Instance.PositionOf(user).ToString(CultureInfo.InvariantCulture);
            });
        }

        // Empty User → the triggering chatter, resolved through the IMMUTABLE trigger
        // context: {event.user_login} is the platform LOGIN BuildChatVars parks there, where
        // {user.name} is the operator-styled DISPLAY name. That distinction is load-bearing:
        // the queue keys every per-user cap, !when lookup, !wrongsong removal and points
        // refund on the login, so resolving off the display name mis-keys every viewer whose
        // login is not simply lowercase(display) — localized and CJK display names — and
        // their own !when / !wrongsong then never match the entry they just paid for. The
        // chat path already prefers msg.Login for exactly this reason; the node path here
        // agrees with it. (Loyalty solves the same login-vs-display problem on its CHAT
        // side — userKey + the ResolveLoyaltyTarget identity map in ScriptManager.Loyalty —
        // its points.* script commands were retired in the 2026-08 tool-node cut.)
        private string ResolveSongUser(string raw)
        {
            string u = StripBareQuotes(raw ?? string.Empty).Trim();
            if (u.Length == 0) u = StripBareQuotes(_engine.GetExecutionVar("event.user_login")).Trim();
            // A non-chat trigger parks no event.user_login, so the display name is the only
            // identity in scope; lower-casing it is the same last resort the platform
            // mappers take when a platform sends no login.
            if (u.Length == 0) u = StripBareQuotes(_engine.GetExecutionVar("user.name")).Trim();
            return SongRequestService.NormalizeLogin(u);
        }

        // The requester's TWO spellings for a graph-queued request — the login everything is
        // keyed on, and the display name chat and the overlay print. Handing the lower-cased
        // login to both made every graph-queued handle render lower-case everywhere, next to
        // chat-queued rows that render properly. An explicit User pin is taken as the login
        // (a graph passes one string and cannot know two spellings) but is echoed back AS
        // TYPED for display rather than flattened.
        private (string Login, string Display) ResolveSongRequester(string raw)
        {
            string typed = StripBareQuotes(raw ?? string.Empty).Trim();
            if (typed.Length > 0) return (SongRequestService.NormalizeLogin(typed), typed);

            string login = ResolveSongUser(string.Empty);
            string display = StripBareQuotes(_engine.GetExecutionVar("user.name")).Trim();
            return (login, display.Length > 0 ? display : login);
        }

        // Whole-number read tolerant of the binder's Int coercion AND of a fractional
        // upstream value (math.divide hands back "2.5"), which floors — the same posture
        // TryParseQuoteNumber takes. Garbage reads 0, which every caller treats as
        // "unspecified" rather than acting on position zero.
        private static int ReadSongInt(BoundArgs? bound, string key, string[] args, int index)
        {
            string raw = StripBareQuotes(bound?.GetOrDefault<string>(key, ArgOrEmpty(args, index)) ?? ArgOrEmpty(args, index)).Trim();
            if (raw.Length == 0) return 0;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) return i;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d))
                return (int)Math.Floor(d);
            return 0;
        }

        // ── Built-in chat-command entry (thin wrapper over the testable core) ──
        /// <summary>
        /// The built-in Song Request chat commands (!sr / !song / !next / !when /
        /// !wrongsong / !voteskip / !volume / !skip / !pause / !play / !removesong /
        /// !srclear). Delegates the parse/gate/queue work to SongRequestService
        /// (unit-testable) and supplies the per-platform reply send. Returns true when a
        /// Song Request command was handled (so the caller suppresses the author on_chat
        /// fan-out). Default-OFF is a total no-op.
        /// </summary>
        public Task<bool> TryHandleSongRequestChatCommandAsync(ChatMessage msg)
            => SongRequestService.Instance.TryHandleChatCommandAsync(
                   msg,
                   reply => { SendSongRequestReply(msg, reply); return Task.CompletedTask; });

        // Route the reply on the platform the command arrived on, reusing the exact
        // per-platform chat-send cores chat.send / *.send_chat use — mirrors
        // SendQuotesReply, no new send path.
        private void SendSongRequestReply(ChatMessage msg, string reply)
        {
            if (string.IsNullOrEmpty(reply)) return;
            switch (msg.Platform)
            {
                case ChatPlatforms.YouTube: SendYouTubeChatCore(reply, "songrequest"); break;
                case ChatPlatforms.Kick:    SendKickChatCore(reply, "songrequest"); break;
                default:                    SendTwitchChatCore(reply, "songrequest"); break;
            }
        }

        // ── YouTube Data API v3 resolver ─────────────────────────────────────

        private const string YouTubeApiBase = "https://www.googleapis.com/youtube/v3/";

        /// <summary>
        /// Wall-clock budget for one resolve, search hop included.
        ///
        /// This is not belt-and-braces. The chat path AWAITS the Song Request provider
        /// inside DispatchBuiltInChatAsync, and the shared outbound HttpClient's own
        /// timeout is 100 s — so a googleapis endpoint that accepts the connection and
        /// then stalls would hold that message's dispatch for over a minute and a half.
        /// The resolver owns its own budget rather than trusting whatever CancellationToken
        /// the caller happened to pass (the chat verb passes none at all).
        /// </summary>
        private static readonly TimeSpan YouTubeLookupTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// The one place the optional YouTube Data API key is read, and the only outbound
        /// call this tool makes. Two shapes:
        ///
        ///   * <c>VideoId</c> set — a <c>videos</c> lookup for title / duration / the
        ///     embeddable flag. Its failures DEGRADE (the caller keeps the id and queues
        ///     with unknown metadata), because a link already carries everything the
        ///     player needs.
        ///   * <c>Query</c> set — a <c>search</c> for the phrase, followed by the same
        ///     <c>videos</c> lookup on the hit so a search result gets the same duration
        ///     cap and embeddable check a pasted link gets. Its failures REFUSE, because
        ///     without an answer there is no video to queue.
        ///
        /// ★ Phoenix NEVER scrapes YouTube as a fallback. No key means no search, full
        /// stop — the caller replies asking for a link.
        ///
        /// ★ Secret hygiene. The key travels in the query string (the documented v3 auth,
        /// and the shape guaranteed to work), which means the request URI is itself
        /// sensitive. So: the URI is never logged, and every message that could have been
        /// built from one — including SendWithManualRedirectAsync's SSRF-rejection text,
        /// which interpolates the full URL — goes through <see cref="RedactYouTubeKey"/>
        /// before it reaches GlobalLogger. Exceptions are logged by MESSAGE, redacted,
        /// rather than by object, because GlobalLogger.Error(source, label, ex) records the
        /// exception's own untouched text.
        ///
        /// Outbound safety is the house gate, not a private HttpClient: this goes through
        /// ScriptManager.SendWithManualRedirectAsync, so the initial URL and every redirect
        /// hop are SSRF-validated and the call is counted against the shared outbound
        /// concurrency cap like every other script HTTP call.
        /// </summary>
        private static async Task<SongLookupResult> ResolveYouTubeAsync(SongLookupRequest req, CancellationToken ct)
        {
            string key = ConfigManager.Current.YouTubeDataApiKey ?? "";
            if (key.Trim().Length == 0) return SongLookupResult.Fail(SongLookupOutcome.NoApiKey);

            // The caller's token is kept separate from the linked one so the catch below can
            // tell the two cancellations apart: a real shutdown / script cancel PROPAGATES,
            // while our own budget expiring is just a failed lookup and must degrade like
            // any other, not surface as an exception on the chat path.
            CancellationToken callerCt = ct;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(callerCt);
            budget.CancelAfter(YouTubeLookupTimeout);
            ct = budget.Token;

            try
            {
                string videoId = req.VideoId ?? "";
                if (videoId.Length == 0)
                {
                    string query = (req.Query ?? "").Trim();
                    if (query.Length == 0) return SongLookupResult.Fail(SongLookupOutcome.NotFound);

                    // videoEmbeddable=true asks YouTube to filter unplayable results out of
                    // the search itself, so a phrase whose top hit blocks embedding lands on
                    // the next usable one instead of being queued and then failing silently
                    // in the overlay.
                    string searchUrl = YouTubeApiBase +
                        "search?part=snippet&type=video&videoEmbeddable=true&maxResults=1&q=" +
                        Uri.EscapeDataString(query) + "&key=" + Uri.EscapeDataString(key);

                    using var searchDoc = await GetYouTubeJsonAsync(searchUrl, ct).ConfigureAwait(false);
                    if (searchDoc is null) return SongLookupResult.Fail(SongLookupOutcome.Error);
                    if (!searchDoc.RootElement.TryGetProperty("items", out var items)
                        || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
                        return SongLookupResult.Fail(SongLookupOutcome.NotFound);

                    var first = items[0];
                    if (!first.TryGetProperty("id", out var idObj)
                        || !idObj.TryGetProperty("videoId", out var vidEl)
                        || vidEl.ValueKind != JsonValueKind.String)
                        return SongLookupResult.Fail(SongLookupOutcome.NotFound);
                    videoId = vidEl.GetString() ?? "";
                    if (videoId.Length == 0) return SongLookupResult.Fail(SongLookupOutcome.NotFound);
                }

                string videosUrl = YouTubeApiBase +
                    "videos?part=snippet,contentDetails,status&id=" + Uri.EscapeDataString(videoId) +
                    "&key=" + Uri.EscapeDataString(key);

                using var doc = await GetYouTubeJsonAsync(videosUrl, ct).ConfigureAwait(false);
                if (doc is null) return SongLookupResult.Fail(SongLookupOutcome.Error);
                if (!doc.RootElement.TryGetProperty("items", out var videos)
                    || videos.ValueKind != JsonValueKind.Array || videos.GetArrayLength() == 0)
                    return SongLookupResult.Fail(SongLookupOutcome.NotFound);

                var v = videos[0];
                if (v.TryGetProperty("status", out var status)
                    && status.TryGetProperty("embeddable", out var emb)
                    && emb.ValueKind == JsonValueKind.False)
                    return SongLookupResult.Fail(SongLookupOutcome.NotEmbeddable);

                string title = v.TryGetProperty("snippet", out var snip)
                            && snip.TryGetProperty("title", out var t)
                            && t.ValueKind == JsonValueKind.String
                    ? (t.GetString() ?? "") : "";

                int seconds = v.TryGetProperty("contentDetails", out var cd)
                           && cd.TryGetProperty("duration", out var dur)
                           && dur.ValueKind == JsonValueKind.String
                    ? ParseIso8601Seconds(dur.GetString()) : 0;

                return SongLookupResult.Ok(videoId, title, seconds);
            }
            catch (OperationCanceledException) when (callerCt.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                GlobalLogger.Log(
                    $"YouTube Data API lookup gave up after {YouTubeLookupTimeout.TotalSeconds:0}s — the request falls back to the link's video id.",
                    "SongRequest", LogLevel.Communication);
                return SongLookupResult.Fail(SongLookupOutcome.Error);
            }
            catch (Exception ex)
            {
                // Message-only + redacted: the exception text can carry the full request
                // URI (SendWithManualRedirectAsync interpolates it), and the key lives in
                // that URI's query string.
                GlobalLogger.Log(
                    "YouTube Data API lookup failed: " + RedactYouTubeKey(ex.Message),
                    "SongRequest", LogLevel.CriticalError);
                return SongLookupResult.Fail(SongLookupOutcome.Error);
            }
        }

        /// <summary>GETs a googleapis URL through the SSRF-validated outbound path and
        /// parses the body. Null on any non-success status or unparseable body — the
        /// caller decides whether that degrades or refuses. The URL is never logged.</summary>
        private static async Task<JsonDocument?> GetYouTubeJsonAsync(string url, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var resp = await SendWithManualRedirectAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // Status + host only. A 403 here is almost always a quota/referrer problem
                // on the streamer's key, which is worth saying out loud — but the URL that
                // produced it carries the key, so it stays out of the log entirely.
                GlobalLogger.Log(
                    $"YouTube Data API answered {(int)resp.StatusCode} — check the API key's quota and restrictions in the Google console.",
                    "SongRequest", LogLevel.Communication);
                return null;
            }
            try { return JsonDocument.Parse(body); }
            catch (JsonException) { return null; }
        }

        /// <summary>
        /// Removes the API key from any string on its way to a log line. The key only ever
        /// appears as a <c>key=</c> query parameter, so the parameter is what gets blanked;
        /// matching on the configured VALUE as well covers a message that quoted it some
        /// other way. Returns the input unchanged when no key is configured.
        /// </summary>
        internal static string RedactYouTubeKey(string? message)
        {
            string s = message ?? "";
            if (s.Length == 0) return s;
            s = System.Text.RegularExpressions.Regex.Replace(
                s, @"([?&]key=)[^&\s""']+", "$1***",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            string key = ConfigManager.Current.YouTubeDataApiKey ?? "";
            if (key.Trim().Length > 0) s = s.Replace(key, "***", StringComparison.Ordinal);
            return s;
        }

        /// <summary>
        /// ISO-8601 duration (<c>PT4M13S</c>) to whole seconds. Hand-parsed rather than via
        /// XmlConvert.ToTimeSpan so a malformed or unexpected value (YouTube reports live
        /// streams as <c>P0D</c>) yields 0 — "unknown length" — instead of throwing inside
        /// the resolve. 0 is exactly what the caller needs: it means the max-duration cap
        /// is skipped rather than applied to a number nobody read.
        /// </summary>
        internal static int ParseIso8601Seconds(string? iso)
        {
            string s = (iso ?? "").Trim();
            if (s.Length < 3 || s[0] != 'P') return 0;
            int timeIdx = s.IndexOf('T');
            if (timeIdx < 0) return 0;                 // date-only (P0D) ⇒ no playable length

            long total = 0, acc = 0;
            bool sawDigit = false;
            for (int i = timeIdx + 1; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= '0' && c <= '9')
                {
                    acc = acc * 10 + (c - '0');
                    sawDigit = true;
                    if (acc > int.MaxValue) return 0;  // absurd value — treat as unknown
                    continue;
                }
                if (!sawDigit) return 0;
                switch (c)
                {
                    case 'H': total += acc * 3600; break;
                    case 'M': total += acc * 60; break;
                    case 'S': total += acc; break;
                    default: return 0;                 // fractional seconds etc. — unknown
                }
                acc = 0;
                sawDigit = false;
            }
            return total is > 0 and <= int.MaxValue ? (int)total : 0;
        }
    }
#pragma warning restore CS1998
}
