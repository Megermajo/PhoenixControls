using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: twitch.* polls / predictions / channel-point rewards
    // and chat-moderation / channel-control proxies.
    // Lifts (a) the 8 polls/predictions/rewards/redemptions handlers and
    // (b) the 5 user-only Twitch moderation proxies (unban/mod/unmod/vip/
    // unvip) plus the 7 dedicated mod/control handlers (delete_message,
    // slow_mode, follower_mode, sub_only_mode, marker, whisper,
    // update_channel) out of RegisterHubCommands. The remaining twitch.*
    // surface (send_chat, timeout, shoutout, ban, create_clip, announcement,
    // and the data lookups: get_user/get_stream/check_role/get_follow_age/
    // last_active/get_viewers) is still inline in ScriptManager.cs and will
    // move into sibling partials in future sweeps.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        // ── Phoenix Controls action pack — the named Streamer.bot actions every
        //    live twitch.* action node dispatches against ─────────────────────
        // Slash-commands-as-chat do NOT execute on Twitch (Twitch deprecated IRC
        // chat-commands in Feb 2023; Streamer.bot's chat send is Helix Send Chat
        // Message = literal text only). The sole live path over SB's WS API is
        // DoAction against a user-configured action that wraps SB's native
        // sub-action (Ban User, Send Shoutout, Chat Modes, …). Users import these
        // once via the Phoenix action pack; Hub dispatches them by these EXACT
        // names. The args each handler passes surface as %name% variables inside
        // the wrapped SB action, so the pack's sub-action fields bind to them.
        internal static class PhxSbActions
        {
            public const string Shoutout          = "Phoenix: Shoutout";
            public const string Timeout           = "Phoenix: Timeout";
            public const string Ban               = "Phoenix: Ban";
            public const string Unban             = "Phoenix: Unban";
            public const string Mod               = "Phoenix: Mod";
            public const string Unmod             = "Phoenix: Unmod";
            public const string Vip               = "Phoenix: VIP";
            public const string Unvip             = "Phoenix: Unvip";
            public const string DeleteMessage     = "Phoenix: Delete Message";
            // twitch.reply — threaded reply-to-message (SB's Send Chat Message with
            // a reply target). Binds %messageId% + %message%. Added 2026-07-14 with
            // Majo's re-exported pack (which now ships "Phoenix: Reply").
            public const string Reply             = "Phoenix: Reply";
            public const string SlowMode          = "Phoenix: Slow Mode";
            public const string FollowerMode      = "Phoenix: Follower Mode";
            public const string SubOnlyMode       = "Phoenix: Sub-Only Mode";
            public const string Marker            = "Phoenix: Marker";
            public const string Whisper           = "Phoenix: Whisper";
            public const string UpdateChannel     = "Phoenix: Update Channel";
            public const string Announcement      = "Phoenix: Announcement";
            public const string CreateClip        = "Phoenix: Create Clip";
            public const string CreatePoll        = "Phoenix: Create Poll";
            public const string EndPoll           = "Phoenix: End Poll";
            public const string CreatePrediction  = "Phoenix: Create Prediction";
            public const string UpdateRewardCost  = "Phoenix: Update Reward Cost";
            public const string SetRewardEnabled  = "Phoenix: Set Reward Enabled";
            public const string FulfillRedemption = "Phoenix: Fulfill Redemption";
            public const string RejectRedemption  = "Phoenix: Reject Redemption";
            public const string ResolvePrediction = "Phoenix: Resolve Prediction";

            // Data actions — unlike the fire-and-forget actions above, these write
            // their result into shared phx_* globals that Hub reads back via the
            // DoAction → poll GetGlobals(persisted:false) round-trip (see
            // FetchActionGlobalsAsync). Get User is reused by check_role and by
            // get_stream / is_online as their FALLBACK once Get Stream Status
            // can't answer (it carries the channel's mod/sub/vip flags + last
            // game/title). CreateClip (above) is also a data action — its C#
            // sub-action writes phx_clip_url / phx_clip_ok.
            public const string GetUser           = "Phoenix: Get User";
            public const string GetFollowAge      = "Phoenix: Get Follow Age";

            // Get Stream Status — a custom C# (Execute-Code) data action that asks
            // Twitch's Helix /streams endpoint directly using Streamer.bot's OWN
            // credentials (CPH.TwitchOAuthToken + CPH.TwitchClientId). It exists
            // because Streamer.bot exposes no stream-liveness surface at all: no CPH
            // stream-info method, and no WS request that returns live state
            // (GetBroadcaster / GetInfo / GetActiveViewers are identity and
            // chat-tracking only). Args: `user` (login to ask about; blank falls back
            // to SB's connected broadcaster) + `req`.
            //
            // Writes phx_stream_known / _live / _login / _viewers / _started /
            // _title / _game / _err, then phx_req LAST like every other data action.
            // phx_stream_known == "1" is the ONLY thing that makes the rest
            // trustworthy — "we could not ask" (no credentials, HTTP failure) and
            // "the channel is offline" must never collapse into the same answer, or
            // a failed call would switch the live-gated tools off mid-stream.
            public const string GetStreamStatus   = "Phoenix: Get Stream Status";

            // OBS control — same model: the obs.* nodes dispatch these against an
            // SB action wrapping SB's native OBS sub-action (which talks to OBS over
            // OBS-WebSocket). The three source-transform commands prefer Hub's own
            // ObsWebSocketClient when it is connected and fall back to this relay;
            // the remaining obs.* commands route through Streamer.bot unconditionally.
            public const string ObsSetScene       = "Phoenix: OBS Set Scene";
            public const string ObsSourceVisible  = "Phoenix: OBS Source Visible";
            public const string ObsRefreshBrowser = "Phoenix: OBS Refresh Browser";
            public const string ObsStartRecording = "Phoenix: OBS Start Recording";
            public const string ObsStopRecording  = "Phoenix: OBS Stop Recording";
            public const string ObsStartStreaming = "Phoenix: OBS Start Streaming";
            public const string ObsStopStreaming  = "Phoenix: OBS Stop Streaming";
            public const string ObsSaveReplay     = "Phoenix: OBS Save Replay";
            public const string ObsSourcePosition = "Phoenix: OBS Source Position";
            public const string ObsSourceScale    = "Phoenix: OBS Source Scale";
            public const string ObsSourceRotation = "Phoenix: OBS Source Rotation";
            public const string ObsFilterVisible  = "Phoenix: OBS Filter Visible";
            public const string ObsScreenshot     = "Phoenix: OBS Screenshot";

            // YouTube platform actions — same wrapper model as the Twitch set:
            // Hub dispatches DoAction against these EXACT names; each pack action
            // wraps SB's native YouTube sub-action (Send Message to Channel /
            // Set Title / Ban+Timeout User / Create+End Poll / …). YT Get User is
            // a data action — it writes phx_yt_* globals that Hub reads back via
            // FetchActionGlobalsAsync.
            public const string YtSendChat        = "Phoenix: YT Send Chat";
            public const string YtSetTitle        = "Phoenix: YT Set Title";
            public const string YtSetDescription  = "Phoenix: YT Set Description";
            public const string YtTimeout         = "Phoenix: YT Timeout";
            public const string YtBan             = "Phoenix: YT Ban";
            public const string YtCreatePoll      = "Phoenix: YT Create Poll";
            public const string YtEndPoll         = "Phoenix: YT End Poll";
            public const string YtGetUser         = "Phoenix: YT Get User";

            // Kick platform actions — same wrapper model. Kick Get User is a data
            // action (phx_kick_* globals via FetchActionGlobalsAsync).
            public const string KickSendChat         = "Phoenix: Kick Send Chat";
            public const string KickReply            = "Phoenix: Kick Reply";
            public const string KickTimeout          = "Phoenix: Kick Timeout";
            public const string KickBan              = "Phoenix: Kick Ban";
            public const string KickUnban            = "Phoenix: Kick Unban";
            public const string KickUntimeout        = "Phoenix: Kick Untimeout";
            public const string KickSetTitle         = "Phoenix: Kick Set Title";
            public const string KickSetCategory      = "Phoenix: Kick Set Category";
            public const string KickDeleteMessage    = "Phoenix: Kick Delete Message";
            public const string KickSetRewardCost    = "Phoenix: Kick Set Reward Cost";
            public const string KickSetRewardEnabled = "Phoenix: Kick Set Reward Enabled";
            public const string KickGetUser          = "Phoenix: Kick Get User";

            // Authoritative set the connect-probe checks against — the actions the
            // shipped PhoenixActionPack.sb actually DEFINES, so a correct install
            // probes clean (no false "re-import the pack" nag).
            //   • 2026-07-22: the pack gained custom C# (Execute-Code) sub-actions
            //     for the formerly-dead set — DeleteMessage / Whisper / CreatePoll /
            //     SubOnlyMode / UpdateRewardCost / SetRewardEnabled / Fulfill+Reject
            //     Redemption / ResolvePrediction — so those are now listed here and
            //     their nodes are un-hidden (see NodeRegistry.HiddenFromPalette).
            //   • Still absent by design: nothing on the Twitch side — every Twitch
            //     action now has a real path. (YouTube.GetUser + Kick reward mgmt
            //     remain absent from YouTubeAll/KickAll — no SB path even via C#.)
            //   • 2026-08-09: GetStreamStatus joined the list. A name is only safe
            //     to add here once the exported pack actually SHIPS it — otherwise
            //     every correct install logs a permanent false "missing action".
            //     Verified present in PhoenixActionPack.sb (57 actions).
            // (OBS Source Position/Scale/Rotation stay listed: their nodes are
            //  VISIBLE and use the Streamer.bot relay as a fallback to Hub's direct
            //  OBS-WebSocket path.)
            public static readonly string[] All =
            {
                Shoutout, Timeout, Ban, Unban, Mod, Unmod, Vip, Unvip,
                Reply, SlowMode, FollowerMode, Marker,
                UpdateChannel, Announcement, CreateClip,
                EndPoll, CreatePoll, CreatePrediction, ResolvePrediction,
                DeleteMessage, Whisper, SubOnlyMode,
                UpdateRewardCost, SetRewardEnabled, FulfillRedemption, RejectRedemption,
                GetUser, GetFollowAge, GetStreamStatus,
                ObsSetScene, ObsSourceVisible, ObsRefreshBrowser, ObsStartRecording,
                ObsStopRecording, ObsStartStreaming, ObsStopStreaming, ObsSaveReplay,
                ObsSourcePosition, ObsSourceScale, ObsSourceRotation, ObsFilterVisible,
                ObsScreenshot,
            };

            // Platform packs — deliberately NOT part of All. The probe checks them
            // SEPARATELY so the report stays one grouped Communication-tier line
            // per platform: a Twitch-only setup lacking every YT/Kick wrapper is
            // normal, not a CriticalError, and must not be spammed per action.
            // YtGetUser is intentionally absent: SB 1.0.x exposes no YouTube
            // user-info sub-action, so "Phoenix: YT Get User" can never exist —
            // probing for it would spam a permanent false "missing action" line.
            // The YouTube.GetUser node is hidden from the palette for the same
            // reason (NodeRegistry.HiddenFromPalette).
            public static readonly string[] YouTubeAll =
            {
                YtSendChat, YtSetTitle, YtSetDescription, YtTimeout, YtBan,
                YtCreatePoll, YtEndPoll,
            };

            // KickDeleteMessage gained a custom C# sub-action (CPH.KickDeleteChatMessage)
            // in the 2026-07-22 pack, so it is now probed + its node un-hidden.
            // KickSetRewardCost / KickSetRewardEnabled stay intentionally absent: Kick
            // rewards are fixed at SB config time (no %rewardId% binding) and there is
            // no Kick reward-management C# method, so those wrappers can't do real work —
            // probing for them would spam a permanent false "missing action" line. Their
            // nodes stay hidden from the palette (NodeRegistry.HiddenFromPalette).
            public static readonly string[] KickAll =
            {
                KickSendChat, KickReply, KickTimeout, KickBan, KickUnban,
                KickUntimeout, KickSetTitle, KickSetCategory, KickGetUser,
                KickDeleteMessage,
            };
        }

        // Names of the SB actions present on the connected Streamer.bot, captured
        // by the connect-probe. null until the first successful GetActions; once
        // populated, a Phoenix name absent from it ⇒ that wrapper action is missing.
        private volatile HashSet<string>? _knownSbActions;

        // One missing-action warning per action name per connection (reset on each
        // probe) so a hot script firing a missing node doesn't spam the System Log.
        private readonly ConcurrentDictionary<string, byte> _missingSbActionWarned =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Whether a Phoenix action-pack wrapper action is CONFIRMED present on the
        /// connected Streamer.bot. This is the capability gate a feature consults BEFORE
        /// committing to a platform round trip it cannot finish.
        ///
        /// Deliberately conservative, and that is the difference between it and
        /// <see cref="DispatchNamedAction"/>. Dispatch is optimistic on purpose — it sends
        /// anyway, because the probe can lag an action the user just added and an unknown
        /// name is a harmless Streamer.bot no-op. A gate cannot be optimistic: a caller
        /// asking "can I do this?" is deciding whether to START something (open a native
        /// prediction it must later resolve), so an UNCONFIRMED capability has to read
        /// false. Hence both an unanswered probe (<c>_knownSbActions == null</c>) and a
        /// disconnected socket are "no".
        /// </summary>
        internal bool SbActionAvailable(string actionName)
        {
            if (string.IsNullOrEmpty(actionName)) return false;
            if (!WS.Instance.IsConnected) return false;
            var known = _knownSbActions;
            return known is not null && known.Contains(actionName);
        }

        // Non-zero while a connect-probe is running, i.e. between the SB connect
        // event and GetActions answering (or giving up). Two readers depend on it:
        // the probe kicks the connect-time reconcile itself, and the periodic
        // reconcile tick skips while it is set so the two can't both burn a
        // data-fetch on the same connect. Released in the probe's finally — NOT
        // on _knownSbActions being populated — so a GetActions that never answers
        // still releases the timer path instead of muting it for the session.
        //
        // A COUNT, not a flag: a flapping socket can start a second probe before
        // the first returns, and a plain flag would then be cleared by whichever
        // finished first while the other was still mid-round-trip.
        private int _actionProbesInFlight;

        // Subscribe the action-pack probe to SB (re)connects. Called once from the
        // singleton ctor (process-lifetime → no handler leak); also fires
        // immediately if SB is already connected when ScriptManager initialises.
        //
        // The handler is never unsubscribed (the subscription outlives BeginStop),
        // so it gates on _stopped: a Streamer.bot reconnect landing after teardown
        // must not re-probe and must not kick a reconcile from a dead manager.
        private void HookStreamerBotActionProbe()
        {
            WS.Instance.OnConnectionStatusChanged += connected =>
            {
                if (!connected) return;
                if (Volatile.Read(ref _stopped) != 0 || GlobalShutdownToken.IsCancellationRequested) return;
                Interlocked.Increment(ref _actionProbesInFlight);
                _ = AsyncErrorBoundary.SafeRunAsync(
                    ProbeStreamerBotActionsAsync, "ScriptManager", "ProbeStreamerBotActions");
            };
            if (WS.Instance.IsConnected)
            {
                Interlocked.Increment(ref _actionProbesInFlight);
                _ = AsyncErrorBoundary.SafeRunAsync(
                    ProbeStreamerBotActionsAsync, "ScriptManager", "ProbeStreamerBotActions");
            }
        }

        // Clears the in-flight marker on EVERY exit path (including the
        // no-response early return and any fault SafeRunAsync would swallow), so
        // the periodic reconcile is only ever held for the duration of a real
        // probe.
        private async Task ProbeStreamerBotActionsAsync()
        {
            try { await ProbeStreamerBotActionsCoreAsync().ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref _actionProbesInFlight); }
        }

        // Enumerate Streamer.bot's actions (GetActions is one of SB's real WS
        // requests) and surface which Phoenix action-pack actions are missing, so
        // the operator sees a clear "import the pack" message at connect instead of
        // silent no-ops when a node fires. On timeout / no response we leave
        // _knownSbActions null so no false "missing" warnings are raised.
        private async Task ProbeStreamerBotActionsCoreAsync()
        {
            string reqId = WS.NewRequestId("get-actions");
            var root = await WS.Instance.SendAndWaitJsonAsync(
                JsonSerializer.Serialize(new { request = "GetActions", id = reqId }), reqId)
                .ConfigureAwait(false);
            if (root is not { } r) return;

            var found = new HashSet<string>(StringComparer.Ordinal);
            if (r.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
                foreach (var a in actions.EnumerateArray())
                    if (a.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                        found.Add(name);

            _knownSbActions = found;
            _missingSbActionWarned.Clear();

            var missing = PhxSbActions.All.Where(x => !found.Contains(x)).ToArray();
            if (missing.Length == PhxSbActions.All.Length)
                GlobalLogger.Log(
                    "Streamer.bot connected, but none of the Phoenix Controls action-pack actions were found. " +
                    "Twitch action nodes (shoutout / ban / timeout / …) won't run until you import the action pack.",
                    "Script", LogLevel.CriticalError);
            else if (missing.Length > 0)
            {
                // Cap the enumerated names so a partial setup (e.g. Twitch actions
                // imported but not the OBS ones) doesn't dump a wall of names.
                const int cap = 8;
                string names = string.Join(", ", missing.Take(cap))
                    + (missing.Length > cap ? $" (+{missing.Length - cap} more)" : "");
                GlobalLogger.Log(
                    $"Streamer.bot connected: {PhxSbActions.All.Length - missing.Length}/{PhxSbActions.All.Length} " +
                    $"Phoenix action-pack actions found. Missing: {names}. " +
                    "Import/re-import the Phoenix Controls action pack to enable those nodes.",
                    "Script", LogLevel.Communication);
            }
            else
                GlobalLogger.Log(
                    $"Streamer.bot connected: all {PhxSbActions.All.Length} Phoenix action-pack actions present.",
                    "Script", LogLevel.Communication);

            // YouTube / Kick platform packs — checked separately from the core
            // set: ONE grouped Communication-tier line per platform. A Twitch-only
            // setup is expected to lack all of these, so they never get the
            // per-action CriticalError treatment the Twitch/OBS set uses above.
            ReportPlatformActionPack("YouTube", "youtube.*", PhxSbActions.YouTubeAll, found);
            ReportPlatformActionPack("Kick",    "kick.*",    PhxSbActions.KickAll,    found);

            // Stream-state reconcile — LAST, and deliberately after _knownSbActions
            // has been assigned above. FetchActionGlobalsAsync fast-returns on a
            // definitively-absent action but stays permissive while the probe is
            // unanswered (known == null), so running this before the assignment
            // would burn the full 4s poll on every pack that lacks the action.
            // This is the connect-time half of the reconcile; the 60s timer covers
            // the rest of the session.
            _ = AsyncErrorBoundary.SafeRunAsync(
                ReconcileStreamStateAsync, "ScriptManager", "stream-state reconcile (connect)");
        }

        // Grouped per-platform action-pack report (YouTube / Kick). Missing
        // wrapper actions collapse into a single Communication line naming the
        // gap — the platform may simply not be in use on this setup, so the tier
        // stays Communication regardless of how many names are absent. (A node
        // that actually FIRES against a missing action still gets its loud
        // CriticalError from DispatchNamedAction / FetchActionGlobalsAsync.)
        private static void ReportPlatformActionPack(
            string platform, string commandPrefix, string[] pack, HashSet<string> found)
        {
            var missing = pack.Where(x => !found.Contains(x)).ToArray();
            if (missing.Length == 0)
            {
                GlobalLogger.Log(
                    $"Streamer.bot connected: all {pack.Length} Phoenix {platform} action-pack actions present.",
                    "Script", LogLevel.Communication);
                return;
            }
            // Same enumeration cap as the core-set report — a fully absent pack
            // shouldn't dump a wall of names.
            const int cap = 8;
            string names = string.Join(", ", missing.Take(cap))
                + (missing.Length > cap ? $" (+{missing.Length - cap} more)" : "");
            GlobalLogger.Log(
                $"{missing.Length} Phoenix {platform} action(s) missing from Streamer.bot — {commandPrefix} " +
                $"commands will no-op: {names}. Import the extended Phoenix action pack to enable them.",
                "Script", LogLevel.Communication);
        }

        // Dispatch a Twitch action node against its Phoenix action-pack wrapper.
        // Replaces the old bare-string DoAction / fictional-request sends that only
        // ever resolved under the StreamSimulator. LOUD (not silent) on the two
        // failure modes: SB disconnected, or the wrapper action missing.
        private void DispatchNamedAction(string command, string actionName, object args)
        {
            if (!WS.Instance.IsConnected)
            {
                GlobalLogger.Log(
                    $"{command} → DROPPED: Streamer.bot is not connected.",
                    "Script", LogLevel.CriticalError);
                return;
            }
            var known = _knownSbActions;
            if (known != null && !known.Contains(actionName)
                && _missingSbActionWarned.TryAdd(actionName, 0))
            {
                GlobalLogger.Log(
                    $"{command} → Streamer.bot action \"{actionName}\" not found. " +
                    "Import the Phoenix Controls action pack so this node can run.",
                    "Script", LogLevel.CriticalError);
            }
            // Send regardless of the probe verdict — the probe can lag an action
            // the user just added, and an unknown action name is a harmless SB no-op.
            WS.Instance.Send(JsonSerializer.Serialize(NamedActionPayload(actionName, args)));
        }

        // ── Data-action round-trip (DoAction → poll GetGlobals → phx_* map) ──────
        // Streamer.bot's DoAction is fire-and-forget: the WS reply is just an ack,
        // it does NOT return the action's output. The Phoenix data actions (Get
        // User / Get Follow Age / Get Stream Status / Create Clip on the Twitch
        // side, plus the YT Get User / Kick Get User mirrors) instead write their
        // result into shared, NON-persisted globals named phx_* and echo a
        // per-call token into phx_req as their LAST sub-action. Hub fires the action, then polls
        // GetGlobals(persisted:false) until phx_req == its token — at which point
        // every sibling phx_* value is guaranteed present (it was written before
        // phx_req) and read from the SAME response. The phx_* globals are shared
        // scratch, so fetches are serialized through _dataFetchLane: only one
        // action's output is in the globals at a time.

        private readonly SemaphoreSlim _dataFetchLane = new(1, 1);

        // Broadcaster live-state. Two writers, both for OUR channel: the
        // stream-lifecycle events Hub already subscribes to (WS.TwitchEvents →
        // ExecuteGenericEventAsync → MarkBroadcasterLive), and
        // ReconcileStreamStateAsync, which re-derives the same state from
        // "Phoenix: Get Stream Status" so a Hub that missed the go-live edge (a
        // mid-stream start) still answers truthfully. An ARBITRARY channel is NOT
        // served from this latch at all — it goes through that same action per
        // call. Volatile: written on the WS event / timer threads, read on
        // script-execution threads.
        private volatile bool _broadcasterLive;
        // StreamOnline time, as ticks. DateTime can't be `volatile`, and a 64-bit
        // field can tear / reorder relative to the volatile flag across threads, so
        // it's written/read via Interlocked (atomic 64-bit + full fence) — paired
        // with the volatile _broadcasterLive read in ResolveBroadcasterUptime.
        private long _broadcasterLiveSinceTicks;
        private int _isOnlineArbitraryWarned;

        // Called from ExecuteGenericEventAsync when a stream-lifecycle event
        // arrives for the broadcaster's channel — ANY platform's go-live /
        // session-end per the canonical StreamLifecycle six-event map (was
        // Twitch-only, which left {uptime} permanently blank for a Kick/YouTube
        // streamer). First platform to go live stamps the timestamp; the first
        // session-end flips the flag off (the same single-flag semantics the
        // tool live-gates always had).
        internal void MarkBroadcasterLive(bool live)
        {
            if (live && !_broadcasterLive)
                Interlocked.Exchange(ref _broadcasterLiveSinceTicks, DateTime.UtcNow.Ticks);
            _broadcasterLive = live;
        }

        /// <summary>
        /// Same latch, but with the stream's REAL start instant instead of "now".
        /// The bool-only overload stamps <c>DateTime.UtcNow</c> on the offline→live
        /// edge, which is correct only when the edge and the go-live coincide — i.e.
        /// when a pushed StreamOnline arrived. A reconcile that discovers an
        /// ALREADY-RUNNING stream (Hub started or restarted mid-stream) would report
        /// 0s of uptime for a stream that began three hours ago, so it hands the
        /// authoritative <c>started_at</c> in here instead.
        ///
        /// Unlike the bool overload this re-stamps on EVERY live call, not just on
        /// the edge: the reconcile re-asks Twitch on a slow timer and the answer it
        /// gets back is strictly better than whatever is currently latched. Twitch
        /// reports the same started_at for the whole session, so the repeat writes
        /// are idempotent rather than drifting.
        /// </summary>
        internal void MarkBroadcasterLive(bool live, DateTime startedAtUtc)
        {
            if (live)
            {
                DateTime u = startedAtUtc.Kind == DateTimeKind.Utc
                    ? startedAtUtc
                    : startedAtUtc.ToUniversalTime();
                Interlocked.Exchange(ref _broadcasterLiveSinceTicks, u.Ticks);
            }
            _broadcasterLive = live;
        }

        // ── Stream-state reconciliation ─────────────────────────────────────
        // Every live gate in the suite is driven by the go-live EDGE alone. Start
        // (or restart) the Hub while already live and that edge has already
        // happened, so nothing ever arms: "Live for {uptime}" posts "Live for !",
        // Scheduling never posts, and the tools disagree with each other because
        // their defaults differ (Timer and Scheduling default live, Ranks and
        // User-Management default offline). "Phoenix: Get Stream Status" is the
        // first thing in the suite that can answer "are you live, and since when?"
        // without an edge, so this asks it on connect and on a slow timer.
        //
        // 60s cadence: Helix allows ~800 points/min per client id — shared with
        // Streamer.bot's own traffic, since the action borrows SB's credentials —
        // and one reconcile costs 1 call live, up to 3 offline (streams, then users
        // + channels for the last-set title/category). 60s is far inside that
        // budget and keeps a chat-command viewer count fresh enough to be worth
        // printing.
        private const int StreamReconcileIntervalMs = 60_000;
        private System.Threading.Timer? _streamReconcileTimer;
        // Skips a tick that lands while the previous one is still in flight: the
        // fetch is serialized behind _dataFetchLane and can sit there for the full
        // 4s poll, so a slow Streamer.bot must not build a queue of reconciles.
        private int _streamReconcileRunning;

        private void StartStreamReconcileTimer()
        {
            // Fires on a period only — the FIRST reconcile is kicked from the
            // action-probe hook instead, because it must not run until the probe
            // has populated _knownSbActions (an unprobed fetch would burn the full
            // 4s timeout on a pack that simply lacks the action).
            //
            // The tick honours that same rule: a connect landing just before a due
            // tick would otherwise race the probe and burn exactly the fetch the
            // ordering above exists to avoid. Skipping is free — the probe kicks a
            // reconcile of its own the moment it finishes, so nothing is lost.
            _streamReconcileTimer = new System.Threading.Timer(
                _ =>
                {
                    if (Volatile.Read(ref _actionProbesInFlight) != 0) return;
                    _ = AsyncErrorBoundary.SafeRunAsync(
                        ReconcileStreamStateAsync, "ScriptManager", "stream-state reconcile");
                },
                null, StreamReconcileIntervalMs, StreamReconcileIntervalMs);
        }

        // One "could not ask Twitch" line per UNKNOWN EPISODE, not per 60s tick:
        // set on the first known == "0" answer, cleared again by the next
        // authoritative one. Without it a permanently mis-configured Streamer.bot
        // (a bot account connected but no broadcaster account) would either spam
        // the System Log once a minute or — as it did before — say nothing at all
        // while the suite quietly believed a state it had never been told.
        private int _streamUnknownWarned;

        /// <summary>
        /// What one reconcile round-trip is allowed to conclude. The three cases
        /// are deliberately NOT collapsed: only <see cref="Authoritative"/> may
        /// touch a live surface, and the other two differ in whether the operator
        /// needs telling.
        /// </summary>
        internal enum ReconcileAnswer
        {
            /// <summary>The round-trip itself never completed — socket down, action
            /// absent from the pack, or no phx_req echo inside the timeout.
            /// FetchActionGlobalsAsync has already logged whichever it was.</summary>
            NoAnswer,
            /// <summary>The action ran and reported <c>known == "0"</c>: it could not
            /// ask Twitch. NOT a report that the channel is offline — nothing may be
            /// published, and the reason is worth one log line.</summary>
            CouldNotAsk,
            /// <summary>A trustworthy answer (<c>known == "1"</c>).</summary>
            Authoritative,
        }

        /// <summary>Pure classifier for a fetched status — the split the reconcile
        /// and the twitch.is_online fallback both branch on.</summary>
        internal static ReconcileAnswer Classify(StreamStatus? st) =>
            st is null           ? ReconcileAnswer.NoAnswer
            : st.Known           ? ReconcileAnswer.Authoritative
                                 : ReconcileAnswer.CouldNotAsk;

        /// <summary>
        /// The operator-facing wording for a <c>known == "0"</c> answer. Pure so the
        /// suite can pin the one property that matters: it reports that we could not
        /// ASK, never that the channel is offline — and it carries the action's own
        /// reason, which had no reader at all before.
        /// </summary>
        internal static string BuildCouldNotAskNote(string? reason, string context)
        {
            // Deliberately says nothing about what the CALLER then did with the
            // non-answer — the reconcile publishes nothing, while twitch.is_online
            // still has to hand the script a value. Each appends its own sentence.
            string why = string.IsNullOrWhiteSpace(reason) ? "no reason reported" : reason!.Trim();
            return $"{context}: \"{PhxSbActions.GetStreamStatus}\" ran but could not ask Twitch " +
                   $"(reason: {why}). \"Could not ask\" is NOT \"the channel is offline\" — the live " +
                   "state is simply unknown. Check Streamer.bot's Twitch account connection.";
        }

        /// <summary>
        /// Asks Streamer.bot for the configured broadcaster's real stream state and
        /// republishes it into the Hub's live surfaces. No-ops on every failure
        /// path: teardown, a disconnected socket, a blank broadcaster name, an
        /// absent action, or <c>known == "0"</c> all leave every gate exactly as it
        /// was — the last of those now says so once instead of silently.
        /// </summary>
        internal async Task ReconcileStreamStateAsync()
        {
            // Teardown guard, same shape as ExecuteOnChatScriptsAsync's. The probe
            // hook is never unsubscribed, so a Streamer.bot reconnect after
            // shutdown can still reach here; and BeginStop disposing the timer only
            // bars FUTURE ticks, never one already inside this method.
            if (Volatile.Read(ref _stopped) != 0 || GlobalShutdownToken.IsCancellationRequested) return;
            if (!WS.Instance.IsConnected) return;
            string bcast = ConfigManager.Current?.BroadcasterUsername ?? "";
            if (string.IsNullOrWhiteSpace(bcast)) return;
            if (Interlocked.Exchange(ref _streamReconcileRunning, 1) != 0) return;
            try
            {
                var st = await FetchStreamStatusAsync("stream.reconcile", bcast.Trim())
                    .ConfigureAwait(false);

                // Re-check AFTER the await: the fetch can sit in _dataFetchLane for
                // the full 4s poll, so a teardown that began while it was in flight
                // would otherwise have this callback re-anchor {stream.uptime} and
                // drive tool gates from a stopped ScriptManager.
                if (Volatile.Read(ref _stopped) != 0 || GlobalShutdownToken.IsCancellationRequested) return;

                switch (Classify(st))
                {
                    case ReconcileAnswer.NoAnswer:
                        // Already logged by FetchActionGlobalsAsync (disconnect /
                        // missing action / timeout) — adding a second line here
                        // would double every failure.
                        return;

                    case ReconcileAnswer.CouldNotAsk:
                        // The round trip SUCCEEDED, so nothing else logs anything:
                        // this is the only place the operator can learn that the
                        // live state is unknown rather than offline.
                        if (Interlocked.Exchange(ref _streamUnknownWarned, 1) == 0)
                            GlobalLogger.Log(
                                BuildCouldNotAskNote(st!.Error, "Stream-state reconcile") +
                                " Nothing was published: uptime, the live latch and the live-gated tools " +
                                "are left exactly as they were. (Logged once — the next trustworthy answer " +
                                "re-arms this note.)",
                                "ScriptManager", LogLevel.Communication);
                        return;

                    default:
                        // Trustworthy again: re-arm the note so a LATER unknown
                        // episode is reported instead of being swallowed by the
                        // first one's flag.
                        Interlocked.Exchange(ref _streamUnknownWarned, 0);
                        ApplyReconciledStreamState(st!);
                        return;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _streamReconcileRunning, 0);
            }
        }

        // ── Who owns the current live episode ───────────────────────────────
        // Non-zero while a REAL platform go-live edge is open: set when an inbound
        // lifecycle event produced +1, cleared when one produces -1 (see
        // NoteRealLiveEdge, called from ExecuteGenericEventAsync). It is the
        // reconcile's only way to tell "the live set holds an episode a real
        // StreamOnline opened — and therefore armed User-Management and Loyalty"
        // from "the live set holds an entry I put there myself, or a provisional
        // one restored from the last session's snapshot".
        private int _realGoLiveEdgeOpen;

        /// <summary>
        /// Records the ownership of the current live episode from a REAL inbound
        /// lifecycle event's edge. Only the non-zero edges matter: +1 is a genuine
        /// offline→live transition (the one that arms the two destructive
        /// per-stream resets), -1 drained the set again. Edge 0 changed nothing, so
        /// it changes nothing here either.
        /// </summary>
        internal void NoteRealLiveEdge(int edge) =>
            Volatile.Write(ref _realGoLiveEdgeOpen,
                RealGoLiveEdgeOpenAfter(Volatile.Read(ref _realGoLiveEdgeOpen) != 0, edge) ? 1 : 0);

        /// <summary>
        /// The transition rule above, as a pure function. The load-bearing case is
        /// <c>edge == 0</c>: a second platform going live during a restream, or a
        /// platform re-emitting its go-live, both report 0 — and neither ends the
        /// episode, so ownership must CARRY, not reset.
        /// </summary>
        internal static bool RealGoLiveEdgeOpenAfter(bool current, int edge) =>
            edge > 0 ? true
            : edge < 0 ? false
                       : current;

        /// <summary>What the reconcile may do when Twitch answers "not live".</summary>
        internal enum ReconcileOfflineStep
        {
            /// <summary>Nothing is live — publish the offline state directly.</summary>
            ApplyOffline,
            /// <summary>Something is live, but nothing a real go-live opened: retract
            /// Twitch first, and publish only if that empties the set.</summary>
            DrainThenApply,
            /// <summary>A real go-live owns the episode — leave the live set alone.</summary>
            LeaveArmed,
        }

        /// <summary>
        /// The arm/disarm ownership rule, factored out as a pure function because it
        /// is the whole correctness of the offline branch and cannot otherwise be
        /// exercised without a Streamer.bot socket and the live tool singletons.
        ///
        /// <para><b>Why LeaveArmed exists.</b> Consuming the offline edge makes the
        /// real StreamOffline that follows a no-edge. This method never fans out to
        /// User-Management or Loyalty (see the ★★ block below), so if a real
        /// go-live had armed those two, their own <c>_streamLive</c> would stay
        /// stuck true — and the NEXT go-live would find <c>wasLive == true</c> and
        /// skip the welcomed-set reset, silently greeting nobody for a whole
        /// stream.</para>
        ///
        /// <para><b>Why DrainThenApply is safe.</b> Without an open real go-live
        /// edge those two were never armed in this process — the reconcile does not
        /// arm them, and a platform merely RESTORED from the snapshot never
        /// produced the +1 that would have. So there is no armed state to strand,
        /// and the go-live edge itself is unaffected either way: the tracker
        /// measures it over the OBSERVED set, which a restored entry is not part
        /// of.</para>
        /// </summary>
        internal static ReconcileOfflineStep DecideOfflineStep(bool anyLive, bool realGoLiveEdgeOpen) =>
            !anyLive            ? ReconcileOfflineStep.ApplyOffline
            : realGoLiveEdgeOpen ? ReconcileOfflineStep.LeaveArmed
                                 : ReconcileOfflineStep.DrainThenApply;

        // Publishes an AUTHORITATIVE (known == "1") status into the live surfaces.
        //
        // ★★ Read this before adding a consumer here. A reconcile that flips the
        // live latch offline→live produces the SAME edge a real go-live does, and
        // two of the six SetStreamLive consumers run DESTRUCTIVE per-stream resets
        // on that edge:
        //
        //   • UserManagementService.SetStreamLive(true) clears the in-memory
        //     welcomed-set AND fires DB.ClearUserMgmtSeenAsync(). Arming it from a
        //     reconcile would re-greet the entire chat in the middle of a stream —
        //     far worse than the gap being closed here, and it is the exact reason
        //     LivePlatformEdgeTracker's authors declined to re-arm at startup.
        //   • LoyaltyService.SetStreamLive(true) clears the per-stream follow
        //     dedupe and the per-stream reward quantities, so a viewer could farm a
        //     follow payout or a once-per-stream reward again.
        //
        // So only the four NON-destructive consumers are driven from here: Timer
        // (unfreezes offline-paused timers), Scheduling (re-arms schedules), Ranks
        // (one-shot ladder-chip notification) and ViewerPresence (resumes
        // watch-minute sampling — without it a Hub brought up mid-stream, where no
        // go-live event will ever arrive, records no watch time for the whole
        // session, a regression against the Ranks-gated accrual this service
        // replaced). The destructive two keep waiting for a genuine platform edge.
        // That omission is deliberate — please do not "fix" it.
        // It applies in BOTH directions: the offline branch below drives the same
        // four and no others. The greeter could never re-arm after a wrongly-read
        // offline (a transient Helix gap); the four driven here are self-healing —
        // the next ~60s tick flips them back — and the only cost is ViewerPresence
        // dropping its carried sub-minute remainders (≤59s per viewer), the price
        // of NOT handing an entire offline night to everybody sitting in chat when
        // the stream ends while Streamer.bot is down.
        private void ApplyReconciledStreamState(StreamStatus st)
        {
            if (st.Live)
            {
                // The latch that answers twitch.is_online / get_stream for our own
                // channel, now anchored to the REAL start rather than "now".
                if (st.StartedAtUtc is { } started)
                {
                    MarkBroadcasterLive(true, started.UtcDateTime);
                    // …and the engine's {stream.uptime} / {stream.started_at}
                    // anchor. This call site is the reason SetStreamStartedAtUtc
                    // exists: until now it had no production caller at all, so the
                    // whole {stream.*} family measured HUB PROCESS uptime and a
                    // "!uptime" command answered with how long the app had been
                    // open. started_at rides on every poll, not just the
                    // transition, so this is also the only route that can recover a
                    // correct anchor after a mid-stream start.
                    ScriptEngine.SetStreamStartedAtUtc(started.UtcDateTime);
                }
                else
                {
                    // Live, but Twitch's stamp was missing or unparseable. Latch
                    // live anyway (that part IS known) and leave the anchor alone
                    // rather than replacing a possibly-correct instant with a wrong
                    // one.
                    MarkBroadcasterLive(true);
                }

                // Authoritative title/category — Set(), not SetIfEmpty(): unlike the
                // background seed this is a fresh answer about the CURRENT stream,
                // so it should win over a stale cached pair. Blank fields are
                // ignored inside Set, so an offline-only field can't blank a good one.
                StreamInfoTracker.Set(st.Title, st.Game, "stream.reconcile");

                // Feed the per-platform edge tracker so this reconcile and the real
                // StreamOnline cannot both count. Without it, a Hub that reconciled
                // its way to "live" would see the NEXT genuine go-live as a fresh
                // offline→live edge and re-run the destructive resets mid-stream.
                // Noting it here makes that later event a benign no-edge instead.
                // The returned edge decides only whether the three safe consumers
                // still need arming (0 = they already are).
                int edge = _liveEdges.Note(StreamLifecycle.Kind.GoingLive, ChatPlatforms.Twitch);
                if (edge > 0)
                {
                    GlobalLogger.Log(
                        "Stream state reconciled: you are already live" +
                        (st.StartedAtUtc is { } s
                            ? $" (since {s.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}, up {FormatUptimeSince(s.UtcDateTime)})"
                            : "") +
                        ". The uptime anchor is set to the real start, and the Timer / Scheduling / Ranks / " +
                        "watch-time live gates are set live — each of those only changes anything if it had " +
                        "been flipped offline first. The welcomed-set reset and the Loyalty per-stream reset are " +
                        "deliberately NOT re-run — they would re-greet chat and reopen per-stream payouts " +
                        "mid-stream.",
                        "ScriptManager", LogLevel.Communication);

                    ApplyToolStreamLive(TimerService.Instance.SetStreamLive, edge, "TimerService", "stream.reconcile");
                    ApplyToolStreamLive(SchedulingService.Instance.SetStreamLive, edge, "SchedulingService", "stream.reconcile");
                    ApplyToolStreamLive(RanksService.Instance.SetStreamLive, edge, "RanksService", "stream.reconcile");
                    ApplyToolStreamLive(ViewerPresenceService.Instance.SetStreamLive, edge, "ViewerPresenceService", "stream.reconcile");
                }
                return;
            }

            // ── Authoritatively NOT live ────────────────────────────────────
            // Twitch is one platform of several: a restreamer whose Twitch leg is
            // down while YouTube is still running must not have every tool frozen
            // by a Twitch-only answer. So the live SET, not this one answer,
            // decides whether the suite goes offline.
            //
            // The subtlety that used to make this branch unreachable: the live
            // branch above Notes GoingLive for Twitch on every live tick, so once
            // the reconcile had armed the state, AnyLive was true for ever and the
            // guard muted the only thing that could authoritatively take Twitch
            // back out. A stream that ended while Streamer.bot was closed (no
            // StreamOffline ever delivered) then left {uptime} climbing all night
            // and Twitch.IsOnline contradicting Twitch.GetStream. The same held for
            // a phantom restored from the previous session's snapshot, which the
            // edge tracker's heartbeat keeps fresh for as long as the Hub is up.
            //
            // So the guard is NARROWED, not removed: it still protects a live
            // episode a REAL platform go-live opened (see DecideOfflineStep for the
            // hazard that case carries), and otherwise the reconcile retracts the
            // entry it is responsible for.
            var step = DecideOfflineStep(_liveEdges.AnyLive, Volatile.Read(ref _realGoLiveEdgeOpen) != 0);
            if (step == ReconcileOfflineStep.LeaveArmed) return;
            if (step == ReconcileOfflineStep.DrainThenApply)
            {
                // Retract Twitch. Note() ignores a platform that is not in the set
                // (edge 0), and returns 0 while OTHER platforms remain live — in
                // both cases the suite stays armed and only the stale Twitch entry
                // is cleaned up. -1 means the set is now genuinely empty.
                if (_liveEdges.Note(StreamLifecycle.Kind.SessionEnd, ChatPlatforms.Twitch) >= 0) return;
            }

            MarkBroadcasterLive(false);

            // Uptime after the stream ends. ResolveStreamVar has no live gate — it
            // always renders (now - anchor) — so an anchor left at the finished
            // stream's start would make {stream.uptime} grow for ever, and a
            // "!uptime" run the next morning would proudly report 14h. Re-anchoring
            // to now makes it read "0s" instead, climbing at most one reconcile
            // interval (~60s) before the next tick pulls it back. Rejected
            // alternatives: leaving the anchor (actively wrong, unbounded), and
            // anchoring into the FUTURE to pin the negative-elapsed clamp at "0s"
            // (abuses a clamp meant for a different case and makes
            // {stream.started_at} render an instant that has not happened).
            // Rendering "" instead would need a live-state flag inside the Shared
            // engine's {stream.*} family, changing a contract several scripts
            // already compare against — out of scope for this change.
            ScriptEngine.SetStreamStartedAtUtc(DateTime.UtcNow);

            // Correct the optimistic defaults. Timer and Scheduling both start
            // _streamLive = true so that a setup with NO live detection still runs;
            // now that there IS live detection, an authoritative offline answer is
            // what those gates were always meant to receive.
            //
            // Repeating this every 60s while offline costs nothing, but not by one
            // shared mechanism: Timer and Ranks early-return when the value is
            // unchanged, while Scheduling assigns unconditionally and guards only
            // its re-arm (`live && !wasLive && OnlyWhenLive`) — so a repeated
            // `false` reaches the assignment and stops there, with no side effect.
            // ViewerPresence likewise assigns unconditionally but clears its carried
            // remainders only on the true→false EDGE, so a repeated `false` is inert.
            ApplyToolStreamLive(TimerService.Instance.SetStreamLive, -1, "TimerService", "stream.reconcile");
            ApplyToolStreamLive(SchedulingService.Instance.SetStreamLive, -1, "SchedulingService", "stream.reconcile");
            ApplyToolStreamLive(RanksService.Instance.SetStreamLive, -1, "RanksService", "stream.reconcile");
            ApplyToolStreamLive(ViewerPresenceService.Instance.SetStreamLive, -1, "ViewerPresenceService", "stream.reconcile");
        }

        // ── Ambient stream-info seed ────────────────────────────────────────
        // The pushed StreamUpdate/ChannelUpdate events only fire on CHANGES, so
        // without a seed the {game}/{title} cache stays empty until the streamer
        // edits their title. Two triggers, both funneling into the same
        // "Phoenix: Get User" fetch for the configured broadcaster: a lazy,
        // throttled kick from the token resolvers (EnsureStreamInfoSeeded) and a
        // refresh on each TWITCH go-live (ExecuteGenericEventAsync — Twitch-only
        // because "Phoenix: Get User" answers with the TWITCH channel's data,
        // which must not race a fresher Kick/YouTube payload; the fill-if-empty
        // write makes even the Twitch case clobber-safe). Serialized with every
        // other data fetch via _dataFetchLane; never blocks a caller.
        //
        // Retry model: NOT a burn-once gate — a failed fetch (SB action missing,
        // timeout) leaves the cache empty, so the next token render may try
        // again, throttled to one attempt per 5 minutes to keep a permanently
        // missing action from burning the 4s poll on every chat command.
        private long _streamInfoSeedLastAttemptTicks;
        private const long StreamInfoSeedRetryTicks = 5L * 60 * TimeSpan.TicksPerSecond;

        internal void EnsureStreamInfoSeeded()
        {
            if (!StreamInfoTracker.NeedsSeed) return;
            if (!WS.Instance.IsConnected) return;
            long now = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref _streamInfoSeedLastAttemptTicks);
            if (now - last < StreamInfoSeedRetryTicks) return;
            if (Interlocked.CompareExchange(ref _streamInfoSeedLastAttemptTicks, now, last) != last)
                return;   // another caller claimed this window
            _ = AsyncErrorBoundary.SafeRunAsync(
                SeedStreamInfoAsync, "ScriptManager", "stream-info seed");
        }

        internal async Task SeedStreamInfoAsync()
        {
            string bcast = ConfigManager.Current?.BroadcasterUsername ?? "";
            if (string.IsNullOrWhiteSpace(bcast)) return;
            var g = await FetchActionGlobalsAsync("streaminfo.seed", PhxSbActions.GetUser,
                new Dictionary<string, string> { ["user"] = bcast.Trim() }).ConfigureAwait(false);
            if (g is null) return;
            string G(string k) => g.TryGetValue(k, out var v) ? v : "";
            // Fill-if-empty: a pushed event that arrived while this fetch was in
            // flight is fresher than the fetched snapshot — never overwrite it.
            StreamInfoTracker.SetIfEmpty(G("phx_user_title"), G("phx_user_game"), "streaminfo.seed");
        }

        // Fires a Phoenix data action and reads back the phx_* globals it writes.
        // Returns a name→value map of all non-persisted globals on success, or null
        // on disconnect / missing action / timeout. Serialized via _dataFetchLane.
        private async Task<Dictionary<string, string>?> FetchActionGlobalsAsync(
            string command, string actionName, IReadOnlyDictionary<string, string> actionArgs,
            int timeoutMs = 4000)
        {
            if (!WS.Instance.IsConnected)
            {
                GlobalLogger.Log($"{command} → DROPPED: Streamer.bot is not connected.",
                    "Script", LogLevel.CriticalError);
                return null;
            }
            var known = _knownSbActions;
            if (known != null && !known.Contains(actionName))
            {
                if (_missingSbActionWarned.TryAdd(actionName, 0))
                    GlobalLogger.Log(
                        $"{command} → Streamer.bot action \"{actionName}\" not found. " +
                        "Import the Phoenix Controls action pack so this node can run.",
                        "Script", LogLevel.CriticalError);
                // Fast-return: the probe has run and this data action is definitively
                // absent (e.g. youtube.get_user against a pack without "Phoenix: YT
                // Get User"), so skip the DoAction + full GetGlobals poll that would
                // only ever burn timeoutMs before returning null anyway. When the
                // probe hasn't run yet (known == null) we stay permissive and try —
                // the action may exist but not be enumerated yet.
                return null;
            }

            string token = WS.NewRequestId("phxq");
            await _dataFetchLane.WaitAsync().ConfigureAwait(false);
            try
            {
                // Fire the action. args = caller args + the freshness token; the
                // action echoes it into phx_req LAST. DoAction here is fire-only —
                // we don't await the ack; the action runs async and writes globals.
                var sendArgs = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in actionArgs) sendArgs[kv.Key] = kv.Value;
                sendArgs["req"] = token;
                WS.Instance.Send(JsonSerializer.Serialize(new
                {
                    request = "DoAction",
                    id      = WS.NewRequestId("do-action"),
                    action  = new { name = actionName },
                    args    = sendArgs,
                }));

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                    string pollId = WS.NewRequestId("get-globals");
                    var root = await WS.Instance.SendAndWaitJsonAsync(
                        JsonSerializer.Serialize(new { request = "GetGlobals", id = pollId, persisted = false }),
                        pollId, 2000).ConfigureAwait(false);
                    if (root is not { } r) continue;
                    var map = ParseGlobals(r);
                    // Trim guards a stray leading/trailing space in the action's
                    // phx_req sub-action value (a common hand-build slip).
                    if (map.TryGetValue("phx_req", out var echoed) && echoed.Trim() == token)
                        return map;
                }

                GlobalLogger.Log(
                    $"{command} → no reply from Streamer.bot action \"{actionName}\" within {timeoutMs}ms " +
                    "(action missing, disabled, or not writing phx_req last). Outputs left empty.",
                    "Script", LogLevel.Communication);
                return null;
            }
            finally
            {
                _dataFetchLane.Release();
            }
        }

        // ── "Phoenix: Get Stream Status" readback ────────────────────────────
        /// <summary>
        /// The parsed shape of the <c>phx_stream_*</c> globals.
        ///
        /// <see cref="Known"/> is the gate on everything else: false means the
        /// action ran but could not get an answer out of Twitch (no broadcaster
        /// account connected in Streamer.bot, HTTP failure, a thrown exception),
        /// and every other field is then meaningless. Callers must fall back to
        /// whatever they did before rather than publishing the zero-valued fields —
        /// rendering "we could not ask" as "you are offline with no viewers" is the
        /// specific failure this flag exists to prevent.
        /// </summary>
        internal sealed class StreamStatus
        {
            public bool Known { get; init; }
            /// <summary>Live right now. Only meaningful while <see cref="Known"/>.</summary>
            public bool Live { get; init; }
            /// <summary>Lowercase login that actually answered ("" when unknown).</summary>
            public string Login { get; init; } = "";
            /// <summary>Current viewer count; 0 when offline, which is the action's
            /// own contract rather than a Hub-side guess.</summary>
            public int Viewers { get; init; }
            /// <summary>Twitch's <c>started_at</c>, normalised to UTC. Null when
            /// offline, absent, or unparseable — never a fabricated instant.</summary>
            public DateTimeOffset? StartedAtUtc { get; init; }
            /// <summary>Live title, or the channel's last-set title when offline.</summary>
            public string Title { get; init; } = "";
            /// <summary>Live category, or the last-set category when offline.</summary>
            public string Game { get; init; } = "";
            /// <summary>Short failure reason; "" on success.</summary>
            public string Error { get; init; } = "";
        }

        /// <summary>
        /// Fires "Phoenix: Get Stream Status" for one channel and parses the globals
        /// it writes. Returns null when the round-trip itself never completed
        /// (Streamer.bot disconnected, the action absent from the pack, or no
        /// <c>phx_req</c> echo inside the timeout) — the caller treats that exactly
        /// like <c>Known == false</c>; the split exists only so a caller can tell a
        /// missing action from a call that ran and failed.
        /// </summary>
        private async Task<StreamStatus?> FetchStreamStatusAsync(string command, string channel)
        {
            var g = await FetchActionGlobalsAsync(command, PhxSbActions.GetStreamStatus,
                new Dictionary<string, string> { ["user"] = channel }).ConfigureAwait(false);
            return g is null ? null : ParseStreamStatus(g);
        }

        /// <summary>
        /// Pure parse of a GetGlobals map into <see cref="StreamStatus"/>. Static and
        /// separate from the fetch so the parsing rules are testable without a
        /// Streamer.bot socket.
        /// </summary>
        internal static StreamStatus ParseStreamStatus(IReadOnlyDictionary<string, string> g)
        {
            string G(string k) => g.TryGetValue(k, out var v) ? (v ?? "") : "";

            // The action writes literal "1"/"0" strings, but Streamer.bot's per-
            // sub-action "Auto Type" toggle can store them as a number or a bool
            // instead — NormBool accepts every spelling those produce.
            bool known = NormBool(G("phx_stream_known")) == "true";
            if (!known)
                return new StreamStatus { Known = false, Error = G("phx_stream_err") };

            bool live = NormBool(G("phx_stream_live")) == "true";

            // Invariant in AND out: the action stringifies the count with
            // CultureInfo.InvariantCulture, so parsing it under the machine's
            // culture is what turns a large count into a parse failure on a locale
            // that groups digits differently. A negative or unparseable value stays
            // 0 rather than being invented.
            int viewers = 0;
            if (int.TryParse(G("phx_stream_viewers"), NumberStyles.Integer,
                             CultureInfo.InvariantCulture, out int v) && v >= 0)
                viewers = v;

            // started_at is Twitch's RFC3339 UTC stamp, forwarded verbatim.
            // AssumeUniversal is the load-bearing flag: without it a stamp that
            // arrived without an explicit offset would be read as machine-LOCAL
            // time, shifting every uptime by the local UTC offset. A malformed or
            // empty value yields null — the callers render no uptime rather than
            // throwing or guessing.
            DateTimeOffset? startedAt = null;
            string startedRaw = G("phx_stream_started");
            if (startedRaw.Length > 0
                && DateTimeOffset.TryParse(startedRaw, CultureInfo.InvariantCulture,
                       DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out var parsed))
                startedAt = parsed.ToUniversalTime();

            return new StreamStatus
            {
                Known        = true,
                Live         = live,
                Login        = G("phx_stream_login"),
                Viewers      = viewers,
                StartedAtUtc = startedAt,
                Title        = G("phx_stream_title"),
                Game         = G("phx_stream_game"),
                Error        = G("phx_stream_err"),
            };
        }

        // Flattens a GetGlobals response { variables:[ {name,value,lastWrite}, … ] }
        // into a name→string map. `value` may be a JSON string OR a number/bool
        // (the SB "Auto Type" toggle decides which); both collapse to string form.
        private static Dictionary<string, string> ParseGlobals(JsonElement root)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("variables", out var vars) && vars.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in vars.EnumerateArray())
                {
                    if (!v.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                        continue;
                    string name = nameEl.GetString() ?? "";
                    if (name.Length == 0) continue;
                    map[name] = v.TryGetProperty("value", out var valEl) ? JsonValueToString(valEl) : "";
                }
            }
            return map;
        }

        private static string JsonValueToString(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.True   => "true",
            JsonValueKind.False  => "false",
            JsonValueKind.Null   => "",
            _                    => v.GetRawText(),
        };

        // Normalises SB's bool-ish strings ("True"/"true"/"1"/"yes") to "true"/"false"
        // so downstream {if {role.is_mod}} comparisons (which test == "true") work
        // regardless of how the native sub-action stringified the flag.
        private static string NormBool(string s) =>
            (s.Equals("true", StringComparison.OrdinalIgnoreCase)
             || s == "1"
             || s.Equals("yes", StringComparison.OrdinalIgnoreCase))
                ? "true" : "false";

        private static bool IsConfiguredBroadcaster(string login)
        {
            if (string.IsNullOrWhiteSpace(login)) return false;
            string bcast = ConfigManager.Current?.BroadcasterUsername ?? "";
            return bcast.Length > 0 && string.Equals(login, bcast, StringComparison.OrdinalIgnoreCase);
        }

        // Human-readable uptime since the broadcaster's StreamOnline, in the same
        // Ns / Nm / Hh Mm shape the {stream.uptime} token uses. "" when offline.
        private string ResolveBroadcasterUptime()
        {
            if (!_broadcasterLive) return "";
            long ticks = Interlocked.Read(ref _broadcasterLiveSinceTicks);
            if (ticks == 0) return "";
            return FormatUptimeSince(new DateTime(ticks, DateTimeKind.Utc));
        }

        /// <summary>
        /// Elapsed-since formatter shared by the broadcaster latch and the
        /// arbitrary-channel answer built from a Get-Stream-Status
        /// <c>started_at</c>, so both render the same Ns / Nm / Hh Mm shape as the
        /// <c>{stream.uptime}</c> token (ScriptEngine.FormatStreamUptime). A start
        /// instant in the future clamps to zero rather than going negative.
        /// </summary>
        // internal, not private: the "a stream that started three hours ago must
        // not report 0s" case is the whole point of the MarkBroadcasterLive
        // overload beside it, so the suite covers this formatter directly rather
        // than through a Streamer.bot socket it cannot stand up.
        internal static string FormatUptimeSince(DateTime startedAtUtc)
        {
            var inv = CultureInfo.InvariantCulture;
            var elapsed = DateTime.UtcNow - startedAtUtc;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            if (elapsed.TotalMinutes < 1) return ((int)elapsed.TotalSeconds).ToString(inv) + "s";
            if (elapsed.TotalHours   < 1) return ((int)elapsed.TotalMinutes).ToString(inv) + "m";
            return ((int)elapsed.TotalHours).ToString(inv) + "h " + elapsed.Minutes.ToString(inv) + "m";
        }

        // Maps the phx_user_* globals from a Get User fetch onto the user.* result
        // slots. Shared by twitch.get_user and streamerbot.get_user.
        //
        // ★ is_mod / is_sub / is_vip are the PLATFORM's answer, full stop. They used to
        // be OR-ed with the User-Management group overlay ("a user granted Moderator via
        // a group reports true here even without the platform rank"), which is no longer
        // a thing that can happen: Moderator / VIP / Subscriber stopped being
        // hand-maintained member lists — their membership IS the platform's, so the
        // overlay had nothing of its own left to add. What the override could still do
        // was harm: this fetch just asked Twitch and got a live answer, and OR-ing a
        // cached group flag over it can only ever turn a fresh "no" into a stale "yes".
        //
        // is_regular KEEPS its group lookup — Regular is a group-only concept with no
        // platform source, so the overlay is the only thing that can answer it. The
        // lookup gets BOTH spellings (login first, display as fallback) exactly like the
        // chat overlay: group membership is stored however the streamer typed it, so
        // passing the login alone would resolve a display-spelled entry on the overlay
        // but NOT here — the same viewer's Regular status would differ between chat and
        // twitch.get_user. Passthrough while that tool is dormant.
        private void ApplyUserGlobals(Dictionary<string, string> g, string fallbackUser)
        {
            string G(string k) => g.TryGetValue(k, out var v) ? v : "";
            string login   = G("phx_user_login");
            string display = G("phx_user_display");
            string lookupName = login.Length > 0 ? login : fallbackUser;
            var (_, _, _, gRegular, _) = UserManagementService.Instance.LookupGroups(lookupName, display);
            _engine.SetLocalResultVar("user.id",              G("phx_user_id"));
            _engine.SetLocalResultVar("user.login",           login.Length   > 0 ? login   : fallbackUser);
            _engine.SetLocalResultVar("user.display_name",    display.Length > 0 ? display : fallbackUser);
            _engine.SetLocalResultVar("user.profile_image",   G("phx_user_avatar"));
            _engine.SetLocalResultVar("user.account_created", G("phx_user_created"));
            _engine.SetLocalResultVar("user.game",            G("phx_user_game"));
            _engine.SetLocalResultVar("user.channel_title",   G("phx_user_title"));
            _engine.SetLocalResultVar("user.is_mod",          NormBool(G("phx_user_mod")));
            _engine.SetLocalResultVar("user.is_sub",          NormBool(G("phx_user_sub")));
            _engine.SetLocalResultVar("user.is_vip",          NormBool(G("phx_user_vip")));
            _engine.SetLocalResultVar("user.is_regular",      gRegular ? "true" : "false");
            // Free ambient write-through when the fetched user IS the broadcaster —
            // the globals carry the channel's current title/game either way.
            if (IsConfiguredBroadcaster(login) || IsConfiguredBroadcaster(fallbackUser))
                StreamInfoTracker.Set(G("phx_user_title"), G("phx_user_game"), "twitch.get_user");
        }

        private void RegisterTwitchCommands()
        {
            // ── Twitch Polls & Predictions ────────────────────────────────
            // Polls/predictions/rewards/redemptions are NOT among Streamer.bot's
            // real WS requests (the old TwitchCreatePoll / UpdateReward / … only
            // answered under the StreamSimulator). Each now dispatches a Phoenix
            // action-pack wrapper via DispatchNamedAction. NOTE: DoAction cannot
            // return a value, so the create_poll / create_prediction id outputs
            // (result.poll_id / result.prediction_id) are no longer populated —
            // the poll/prediction still fires; the id just isn't retrievable over
            // SB. Scripts that need the id must wait for direct Twitch Helix.
            _engine.RegisterCommand("twitch.create_poll", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string title    = bound?.GetOrDefault<string>("Title", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string choices  = bound?.GetOrDefault<string>("Choices", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                int    dur      = (bound != null && bound.ContainsKey("DurationSec"))
                    ? bound.Get<int>("DurationSec")
                    : (int.TryParse(ArgOrEmpty(args, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int d) ? d : 60);
                if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(choices)) return null;
                // Pass the raw comma-separated choices through; the SB wrapper
                // action's Create Poll sub-action splits them.
                DispatchNamedAction("twitch.create_poll", PhxSbActions.CreatePoll, new
                {
                    title,
                    choices,
                    duration = dur.ToString(CultureInfo.InvariantCulture)
                });
                GlobalLogger.Log($"Twitch Poll: {title}", "Script", LogLevel.Communication);
                return null;
            });

            _engine.RegisterCommand("twitch.end_poll", async (args) =>
            {
                string pollId = _engine.CurrentBoundArgs?.GetOrDefault<string>("PollId", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                // SB's End Poll sub-action acts on the active poll; pollId is passed
                // through for SB forks that accept it but is otherwise advisory.
                DispatchNamedAction("twitch.end_poll", PhxSbActions.EndPoll, new { pollId });
                return null;
            });

            _engine.RegisterCommand("twitch.create_prediction", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string title    = bound?.GetOrDefault<string>("Title", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string outcomeA = bound?.GetOrDefault<string>("OutcomeA", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string outcomeB = bound?.GetOrDefault<string>("OutcomeB", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                // C/D/E come ONLY from named bound args (never positional) so the
                // handler stays correct both before and after the manifest gains
                // the OutcomeC/D/E ArgSpecs — a positional read would otherwise
                // grab the duration arg while the manifest still ends in DurationSec.
                string outcomeC = bound?.GetOrDefault<string>("OutcomeC", "") ?? "";
                string outcomeD = bound?.GetOrDefault<string>("OutcomeD", "") ?? "";
                string outcomeE = bound?.GetOrDefault<string>("OutcomeE", "") ?? "";
                // Engine path uses the named bound arg. For a direct positional
                // invoke (tests) DurationSec is always the LAST manifest arg, so read
                // the last positional value — correct for both the legacy 4-arg
                // (Title,A,B,Dur) and the new 7-arg (Title,A,B,C,D,E,Dur) forms.
                int    dur      = (bound != null && bound.ContainsKey("DurationSec"))
                    ? bound.Get<int>("DurationSec")
                    : (args.Length > 0 && int.TryParse(args[args.Length - 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int d) ? d : 120);
                if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(outcomeA) || string.IsNullOrEmpty(outcomeB))
                    return null;
                // "Phoenix: Create Prediction" wires %outcomeA%..%outcomeE% (up to
                // 5). A/B are required; C/D/E are optional and sent empty when not
                // wired. NOTE: if the SB action passes the empty C/D/E slots
                // straight to Twitch they'd be rejected — the action should only add
                // non-empty outcomes (see PhoenixActionPack.md).
                DispatchNamedAction("twitch.create_prediction", PhxSbActions.CreatePrediction, new
                {
                    title,
                    outcomeA,
                    outcomeB,
                    outcomeC,
                    outcomeD,
                    outcomeE,
                    duration = dur.ToString(CultureInfo.InvariantCulture)
                });
                GlobalLogger.Log($"Twitch Prediction: {title}", "Script", LogLevel.Communication);
                return null;
            });

            // twitch.resolve_prediction(WinningOutcome) — resolves the LAST prediction
            // by winning-outcome index (0 = first outcome) via the "Phoenix: Resolve
            // Prediction" action (SB's native Resolve Last Prediction sub-action,
            // winningIndex = %outcome%). The pack ships a custom C# path since 2026-07-22.
            _engine.RegisterCommand("twitch.resolve_prediction", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                int outcome = (bound != null && bound.ContainsKey("WinningOutcome"))
                    ? bound.Get<int>("WinningOutcome")
                    : (int.TryParse(ArgOrEmpty(args, 0), NumberStyles.Integer, CultureInfo.InvariantCulture, out int o) ? o : 0);
                DispatchNamedAction("twitch.resolve_prediction", PhxSbActions.ResolvePrediction, new
                {
                    outcome = outcome.ToString(CultureInfo.InvariantCulture)
                });
                return null;
            });

            // ── Twitch Reward Management ──────────────────────────────────
            _engine.RegisterCommand("twitch.update_reward_cost", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string rewardId = bound?.GetOrDefault<string>("RewardId", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                int    cost     = (bound != null && bound.ContainsKey("Cost"))
                    ? bound.Get<int>("Cost")
                    : (int.TryParse(ArgOrEmpty(args, 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int c) ? c : 0);
                if (string.IsNullOrEmpty(rewardId)) return null;
                DispatchNamedAction("twitch.update_reward_cost", PhxSbActions.UpdateRewardCost, new
                {
                    rewardId,
                    cost = cost.ToString(CultureInfo.InvariantCulture)
                });
                return null;
            });

            // twitch.set_reward_enabled(rewardId, true/false) — matches Twitch.SetRewardEnabled node.
            // R19 (sweep 14) — typed Bool reference. Binder accepts true/false/1/0/yes/no/on/off
            // case-insensitively; legacy fallback retains the old `Equals("true", ...)` semantic.
            _engine.RegisterCommand("twitch.set_reward_enabled", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string rewardId = bound?.GetOrDefault<string>("RewardId", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                bool   enabled  = (bound != null && bound.ContainsKey("Enabled"))
                    ? bound.Get<bool>("Enabled")
                    : ArgOrEmpty(args, 1).Equals("true", StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrEmpty(rewardId)) return null;
                DispatchNamedAction("twitch.set_reward_enabled", PhxSbActions.SetRewardEnabled, new
                {
                    rewardId,
                    enabled = enabled ? "true" : "false"
                });
                return null;
            });

            // Fulfill/Reject auto-source BOTH ids from the ambient Twitch.PointRedeem
            // event (the exporter defaults RewardId={event.reward_id} + RedemptionId=
            // {event.redemption_id}). CPH.TwitchRedemptionFulfill/Cancel need both.
            // Empty ⇒ the graph wasn't redemption-triggered — skip, don't fire a
            // half-populated request.
            _engine.RegisterCommand("twitch.fulfill_redemption", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string rewardId     = bound?.GetOrDefault<string>("RewardId", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string redemptionId = bound?.GetOrDefault<string>("RedemptionId", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                if (string.IsNullOrEmpty(rewardId) || string.IsNullOrEmpty(redemptionId))
                {
                    GlobalLogger.Log("twitch.fulfill_redemption: no reward/redemption id — fire this from a Twitch.PointRedeem-triggered script.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                DispatchNamedAction("twitch.fulfill_redemption", PhxSbActions.FulfillRedemption, new { rewardId, redemptionId });
                return null;
            });

            _engine.RegisterCommand("twitch.reject_redemption", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string rewardId     = bound?.GetOrDefault<string>("RewardId", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string redemptionId = bound?.GetOrDefault<string>("RedemptionId", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                if (string.IsNullOrEmpty(rewardId) || string.IsNullOrEmpty(redemptionId))
                {
                    GlobalLogger.Log("twitch.reject_redemption: no reward/redemption id — fire this from a Twitch.PointRedeem-triggered script.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                DispatchNamedAction("twitch.reject_redemption", PhxSbActions.RejectRedemption, new { rewardId, redemptionId });
                return null;
            });
        }

        // ── Twitch moderation / channel control proxies (P3) ─────────────
        // Each routes through a Phoenix action-pack wrapper action via
        // DispatchNamedAction (DoAction with action:{ name }). The old bare-string
        // DoAction("TwitchUnban", …) / fictional requests only resolved under the
        // StreamSimulator. When Hub gains direct Twitch Helix access these can swap
        // to an HTTP call without changing the script-facing contract.
        //
        // Conventions:
        //   * Empty required string args → log + return (no dispatch).
        //   * DispatchNamedAction logs LOUDLY when SB is disconnected or the
        //     wrapper action is missing (no silent no-op).
        //   * Done flow continues regardless (mirrors twitch.shoutout).
        private void RegisterUserOnlyTwitchProxy(string command, string phxAction)
        {
            _engine.RegisterCommand(command, async (args) =>
            {
                string user = _engine.CurrentBoundArgs?.GetOrDefault<string>("User", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrWhiteSpace(user))
                {
                    GlobalLogger.Log($"{command}: empty user — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                DispatchNamedAction(command, phxAction, new { user });
                await Task.CompletedTask;
                return null;
            });
        }

        private void RegisterTwitchModerationCommands()
        {
            RegisterUserOnlyTwitchProxy("twitch.unban", PhxSbActions.Unban);
            RegisterUserOnlyTwitchProxy("twitch.mod",   PhxSbActions.Mod);
            RegisterUserOnlyTwitchProxy("twitch.unmod", PhxSbActions.Unmod);
            RegisterUserOnlyTwitchProxy("twitch.vip",   PhxSbActions.Vip);
            RegisterUserOnlyTwitchProxy("twitch.unvip", PhxSbActions.Unvip);

            // twitch.delete_message(messageId) — single-message moderation.
            _engine.RegisterCommand("twitch.delete_message", async (args) =>
            {
                string messageId = _engine.CurrentBoundArgs?.GetOrDefault<string>("MessageId", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrWhiteSpace(messageId))
                {
                    GlobalLogger.Log("twitch.delete_message: empty messageId — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                DispatchNamedAction("twitch.delete_message", PhxSbActions.DeleteMessage, new { messageId });
                await Task.CompletedTask;
                return null;
            });

            // twitch.slow_mode(seconds) — 0 = off (sentinel matches Helix semantics).
            _engine.RegisterCommand("twitch.slow_mode", async (args) =>
            {
                int seconds;
                var bound = _engine.CurrentBoundArgs;
                if (bound != null && bound.ContainsKey("Seconds")) seconds = bound.Get<int>("Seconds");
                else seconds = int.TryParse(ArgOrEmpty(args, 0), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
                DispatchNamedAction("twitch.slow_mode", PhxSbActions.SlowMode, new
                {
                    seconds = seconds.ToString(CultureInfo.InvariantCulture)
                });
                await Task.CompletedTask;
                return null;
            });

            // twitch.follower_mode(minutes) — -1 = off (sentinel matches Helix semantics).
            _engine.RegisterCommand("twitch.follower_mode", async (args) =>
            {
                int minutes;
                var bound = _engine.CurrentBoundArgs;
                if (bound != null && bound.ContainsKey("Minutes")) minutes = bound.Get<int>("Minutes");
                else minutes = int.TryParse(ArgOrEmpty(args, 0), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 0;
                DispatchNamedAction("twitch.follower_mode", PhxSbActions.FollowerMode, new
                {
                    minutes = minutes.ToString(CultureInfo.InvariantCulture)
                });
                await Task.CompletedTask;
                return null;
            });

            // twitch.sub_only_mode(enabled) — toggle.
            _engine.RegisterCommand("twitch.sub_only_mode", async (args) =>
            {
                bool enabled = (_engine.CurrentBoundArgs != null && _engine.CurrentBoundArgs.ContainsKey("Enabled"))
                    ? _engine.CurrentBoundArgs.Get<bool>("Enabled")
                    : (bool.TryParse(ArgOrEmpty(args, 0), out var b) && b);
                DispatchNamedAction("twitch.sub_only_mode", PhxSbActions.SubOnlyMode, new
                {
                    enabled = enabled ? "true" : "false"
                });
                await Task.CompletedTask;
                return null;
            });

            // twitch.marker(description?) — drops a stream marker. Description optional.
            _engine.RegisterCommand("twitch.marker", async (args) =>
            {
                string description = _engine.CurrentBoundArgs?.GetOrDefault<string>("Description", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                DispatchNamedAction("twitch.marker", PhxSbActions.Marker, new { description });
                await Task.CompletedTask;
                return null;
            });

            // twitch.whisper(user, message) — private DM. Same 500-char cap as chat.
            // Heads-up: Twitch's Send Whisper requires the bot account to have a
            // verified phone number and enforces strict rate limits, so even a
            // correctly-wired wrapper action can be rejected at Twitch.
            _engine.RegisterCommand("twitch.whisper", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string user    = bound?.GetOrDefault<string>("User", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string message = bound?.GetOrDefault<string>("Message", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                if (string.IsNullOrWhiteSpace(user))
                {
                    GlobalLogger.Log("twitch.whisper: empty user — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                if (string.IsNullOrEmpty(message))
                {
                    GlobalLogger.Log("twitch.whisper: empty message — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                if (message.Length > 500)
                {
                    GlobalLogger.Log($"twitch.whisper: message length {message.Length} exceeds 500-char cap — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                DispatchNamedAction("twitch.whisper", PhxSbActions.Whisper, new { user, message });
                await Task.CompletedTask;
                return null;
            });

            // twitch.update_channel(title, gameId?) — updates stream metadata.
            // Empty Title still dispatches so callers can update GameId alone if they pass "".
            _engine.RegisterCommand("twitch.update_channel", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string title  = bound?.GetOrDefault<string>("Title", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string gameId = bound?.GetOrDefault<string>("GameId", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(gameId))
                {
                    GlobalLogger.Log("twitch.update_channel: both Title and GameId empty — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                DispatchNamedAction("twitch.update_channel", PhxSbActions.UpdateChannel, new { title, gameId });
                // Write-through: the new title is known right here. The gameId is a
                // numeric category ID, NOT the game NAME the {game}/stream.game
                // surface carries — never cache it as one (the next pushed
                // StreamUpdate / seed fetch supplies the name). Connected-gated:
                // a dropped dispatch changed nothing, so cache nothing.
                if (WS.Instance.IsConnected)
                    StreamInfoTracker.Set(title, null, "twitch.update_channel");
                await Task.CompletedTask;
                return null;
            });
        }

        // ── Twitch Data queries ─────────────────────────────────────────
        // Read-only lookups. The OLD handlers sent fictional SB WS requests
        // (GetUserInfo / GetStreamInfo / GetUserRoles / GetFollowAge) that real
        // Streamer.bot never answers — they only resolved under the external
        // StreamSimulator, so live they returned nothing ("no last game played").
        // The live path is the Phoenix data-action round-trip: DoAction against a
        // user-configured action that fetches the data with SB's NATIVE
        // sub-actions ("Get User Info for Target", "Get Follow Age Info for
        // Target") and writes it into phx_* globals; Hub reads them back via
        // FetchActionGlobalsAsync (DoAction → poll GetGlobals → phx_req token).
        // get_user/check_role/get_stream all reuse "Phoenix: Get User" (it carries
        // id/login/display/avatar/created + last game/title + mod/sub/vip).
        // streamerbot.get_user is an exact mirror some scripts expect by name.
        private void RegisterTwitchDataCommands()
        {
            // twitch.get_user(username) — fetches via "Phoenix: Get User" and
            // writes user.id / user.login / user.display_name / user.profile_image
            // / user.account_created / user.game / user.channel_title /
            // user.is_mod / user.is_sub / user.is_vip. (game/title = the channel's
            // last set game + title, offline-capable — this is the "last game
            // played" the old fictional GetUserInfo never returned.)
            _engine.RegisterCommand("twitch.get_user", async (args) => {
                string user = StripBareQuotes(_engine.CurrentBoundArgs?.GetOrDefault<string>("Username", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                if (string.IsNullOrEmpty(user)) return null;
                var g = await FetchActionGlobalsAsync("twitch.get_user", PhxSbActions.GetUser,
                    new Dictionary<string, string> { ["user"] = user }).ConfigureAwait(false);
                if (g != null) ApplyUserGlobals(g, user);
                return null;
            });

            // streamerbot.get_user(username) — mirrors twitch.get_user
            _engine.RegisterCommand("streamerbot.get_user", async (args) => {
                string user = StripBareQuotes(_engine.CurrentBoundArgs?.GetOrDefault<string>("User", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                if (string.IsNullOrEmpty(user)) return null;
                var g = await FetchActionGlobalsAsync("streamerbot.get_user", PhxSbActions.GetUser,
                    new Dictionary<string, string> { ["user"] = user }).ConfigureAwait(false);
                if (g != null) ApplyUserGlobals(g, user);
                return null;
            });

            // twitch.get_stream(username) — live metrics now come from "Phoenix: Get
            // Stream Status" (Twitch Helix /streams through Streamer.bot's own
            // credentials), which answers for ANY channel and carries the real
            // viewer count and start time. The old shape could only ever report the
            // broadcaster's own liveness from the StreamOnline/Offline latch and
            // hardcoded viewers to "0" — a literal zero on a live channel with an
            // audience.
            //
            // When the status action can't answer — absent from the pack, or
            // known == "0" meaning "we could not ask Twitch" — this falls back to
            // the previous "Phoenix: Get User" behaviour instead of publishing the
            // zeroed fields. The one deliberate difference in the fallback is the
            // viewer count: it is left EMPTY rather than "0", because "we could not
            // ask" must not render as "you have no viewers".
            _engine.RegisterCommand("twitch.get_stream", async (args) => {
                string user = StripBareQuotes(_engine.CurrentBoundArgs?.GetOrDefault<string>("Username", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                if (string.IsNullOrEmpty(user)) return null;

                var st = await FetchStreamStatusAsync("twitch.get_stream", user).ConfigureAwait(false);
                if (st is { Known: true })
                {
                    _engine.SetLocalResultVar("stream.is_live", st.Live ? "true" : "false");
                    _engine.SetLocalResultVar("stream.viewers", st.Viewers.ToString(CultureInfo.InvariantCulture));
                    _engine.SetLocalResultVar("stream.title",   st.Title);
                    _engine.SetLocalResultVar("stream.game",    st.Game);
                    // Uptime only while live — an offline channel's started_at is ""
                    // by the action's contract, and a stale one would read as though
                    // the finished stream were still running.
                    _engine.SetLocalResultVar("stream.uptime",
                        st.Live && st.StartedAtUtc is { } started ? FormatUptimeSince(started.UtcDateTime) : "");
                    // Ambient write-through for our own channel, same as the
                    // fallback path below. The broadcaster LATCH and the engine's
                    // {stream.*} anchor are deliberately NOT written here: they are
                    // owned by ReconcileStreamStateAsync, which is also the only
                    // place that decides which live-edge consumers may be armed. A
                    // script node must not be able to fire a per-stream reset.
                    if (IsConfiguredBroadcaster(st.Login) || IsConfiguredBroadcaster(user))
                        StreamInfoTracker.Set(st.Title, st.Game, "twitch.get_stream");
                    return null;
                }

                // Fallback — the pre-Get-Stream-Status behaviour, unchanged except
                // for the viewer count (see the note above).
                var g = await FetchActionGlobalsAsync("twitch.get_stream", PhxSbActions.GetUser,
                    new Dictionary<string, string> { ["user"] = user }).ConfigureAwait(false);
                if (g != null)
                {
                    string G(string k) => g.TryGetValue(k, out var v) ? v : "";
                    bool ownChannel = IsConfiguredBroadcaster(G("phx_user_login")) || IsConfiguredBroadcaster(user);
                    _engine.SetLocalResultVar("stream.title",   G("phx_user_title"));
                    _engine.SetLocalResultVar("stream.game",    G("phx_user_game"));
                    _engine.SetLocalResultVar("stream.is_live", (ownChannel && _broadcasterLive) ? "true" : "false");
                    // Written (as "") rather than skipped: an unset {stream.viewers}
                    // is not in the engine's stream.* resolver, so it would leak the
                    // literal token "{stream.viewers}" into chat.
                    _engine.SetLocalResultVar("stream.viewers", "");
                    _engine.SetLocalResultVar("stream.uptime",  ownChannel ? ResolveBroadcasterUptime() : "");
                    // Free ambient write-through — an own-channel fetch just told us
                    // the current title/game; keep the {game}/{title} cache warm.
                    if (ownChannel)
                        StreamInfoTracker.Set(G("phx_user_title"), G("phx_user_game"), "twitch.get_stream");
                }
                return null;
            });

            // twitch.is_online(Channel) — backs Twitch.IsOnline's IsLive output.
            //
            // The broadcaster's OWN channel (blank Channel = broadcaster) keeps
            // answering from the in-process live latch: it is instant, and it is now
            // kept honest by ReconcileStreamStateAsync, which re-asks Twitch every
            // 60s and re-anchors the latch. Routing this branch through the action
            // instead would put a round-trip of up to 4s on what is typically a hot
            // chat command, for an answer the latch already holds.
            //
            // An ARBITRARY channel goes through "Phoenix: Get Stream Status" — the
            // hardcoded not-live that used to live here was simply wrong once that
            // action existed.
            _engine.RegisterCommand("twitch.is_online", async (args) => {
                string channel = StripBareQuotes(_engine.CurrentBoundArgs?.GetOrDefault<string>("Channel", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                if (string.IsNullOrWhiteSpace(channel))
                    channel = ConfigManager.Current?.BroadcasterUsername ?? "";
                if (string.IsNullOrWhiteSpace(channel))
                {
                    _engine.SetLocalResultVar("stream.is_live", "false");
                    return null;
                }
                if (IsConfiguredBroadcaster(channel))
                {
                    _engine.SetLocalResultVar("stream.is_live", _broadcasterLive ? "true" : "false");
                    _engine.SetLocalResultVar("stream.uptime",  ResolveBroadcasterUptime());
                    return null;
                }
                // Arbitrary channel — answered from the SAME source as get_stream.
                var st = await FetchStreamStatusAsync("twitch.is_online", channel).ConfigureAwait(false);
                if (st is { Known: true })
                {
                    _engine.SetLocalResultVar("stream.is_live", st.Live ? "true" : "false");
                    _engine.SetLocalResultVar("stream.viewers", st.Viewers.ToString(CultureInfo.InvariantCulture));
                    _engine.SetLocalResultVar("stream.title",   st.Title);
                    _engine.SetLocalResultVar("stream.game",    st.Game);
                    _engine.SetLocalResultVar("stream.uptime",
                        st.Live && st.StartedAtUtc is { } started ? FormatUptimeSince(started.UtcDateTime) : "");
                    return null;
                }

                // No usable answer — and the two ways to get here need DIFFERENT
                // words, because only one of them is the user's to fix by importing
                // anything:
                //
                //   • The action is definitively ABSENT (the probe ran and did not
                //     find it) → the import nag. Note it is gated on the probe
                //     verdict, not on merely reaching this branch: the pack now
                //     ships "Phoenix: Get Stream Status", so a correct install must
                //     stop emitting the old claim that arbitrary-channel liveness
                //     "cannot be retrieved over Streamer.bot" — no longer true.
                //   • The action ANSWERED known == "0" → it is installed and it ran;
                //     it just could not ask Twitch. That reason had no reader at
                //     all before, so a mis-configured Streamer.bot produced a
                //     permanent, unexplained "not live".
                //
                // A transient no-answer on an install that HAS the action stays
                // quiet here; FetchActionGlobalsAsync already logs its own timeout.
                // Both messages share the one-time gate — whichever condition is
                // hit first is the one this node reports for the session.
                var probed = _knownSbActions;
                bool actionAbsent = probed is not null && !probed.Contains(PhxSbActions.GetStreamStatus);
                bool couldNotAsk  = Classify(st) == ReconcileAnswer.CouldNotAsk;
                if ((actionAbsent || couldNotAsk)
                    && Interlocked.Exchange(ref _isOnlineArbitraryWarned, 1) == 0)
                    GlobalLogger.Log(
                        actionAbsent
                            ? "twitch.is_online: live status for a channel other than the broadcaster's own needs the " +
                              "\"Phoenix: Get Stream Status\" action, which this Streamer.bot doesn't have — reporting " +
                              "not-live. Re-import the Phoenix Controls action pack to enable it. (One-time note.)"
                            : BuildCouldNotAskNote(st!.Error, "twitch.is_online") +
                              " The node reports not-live because it has to answer something. (One-time note.)",
                        "Script", LogLevel.Communication);
                _engine.SetLocalResultVar("stream.is_live", "false");
                var g = await FetchActionGlobalsAsync("twitch.is_online", PhxSbActions.GetUser,
                    new Dictionary<string, string> { ["user"] = channel }).ConfigureAwait(false);
                if (g != null)
                {
                    string G(string k) => g.TryGetValue(k, out var v) ? v : "";
                    _engine.SetLocalResultVar("stream.title", G("phx_user_title"));
                    _engine.SetLocalResultVar("stream.game",  G("phx_user_game"));
                }
                return null;
            });

            // twitch.check_role(username) — reuses "Phoenix: Get User" (it carries
            // the mod/sub/vip flags). role.is_broadcaster is derived Hub-side by
            // comparing the returned login to the configured broadcaster.
            _engine.RegisterCommand("twitch.check_role", async (args) => {
                string user = StripBareQuotes(_engine.CurrentBoundArgs?.GetOrDefault<string>("Username", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                if (string.IsNullOrEmpty(user)) return null;
                var g = await FetchActionGlobalsAsync("twitch.check_role", PhxSbActions.GetUser,
                    new Dictionary<string, string> { ["user"] = user }).ConfigureAwait(false);
                if (g != null)
                {
                    string G(string k) => g.TryGetValue(k, out var v) ? v : "";
                    string login = G("phx_user_login");
                    // ★ mod / sub / vip are reported exactly as the platform answered
                    // this fetch. They are no longer OR-ed with the User-Management
                    // group overlay: those three groups ARE the platform now (no
                    // hand-maintained member list exists for them), so the override
                    // could only ever replace the live answer this node just paid a
                    // round-trip for with an older cached one. Regular keeps the group
                    // lookup — it is the one role with no platform source.
                    var (_, _, _, gRegular, _) =
                        UserManagementService.Instance.LookupGroups(login.Length > 0 ? login : user);
                    _engine.SetLocalResultVar("role.is_mod", NormBool(G("phx_user_mod")));
                    _engine.SetLocalResultVar("role.is_sub", NormBool(G("phx_user_sub")));
                    _engine.SetLocalResultVar("role.is_vip", NormBool(G("phx_user_vip")));
                    _engine.SetLocalResultVar("role.is_regular", gRegular ? "true" : "false");
                    _engine.SetLocalResultVar("role.is_broadcaster",
                        IsConfiguredBroadcaster(login.Length > 0 ? login : user) ? "true" : "false");
                }
                return null;
            });

            // twitch.get_follow_age(username) — fetches via "Phoenix: Get Follow
            // Age" (native "Get Follow Age Info for Target"); writes follow.days /
            // follow.formatted / follow.date / follow.is_following.
            _engine.RegisterCommand("twitch.get_follow_age", async (args) => {
                string user = StripBareQuotes(_engine.CurrentBoundArgs?.GetOrDefault<string>("Username", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                if (string.IsNullOrEmpty(user)) return null;
                var g = await FetchActionGlobalsAsync("twitch.get_follow_age", PhxSbActions.GetFollowAge,
                    new Dictionary<string, string> { ["user"] = user }).ConfigureAwait(false);
                if (g != null)
                {
                    string G(string k) => g.TryGetValue(k, out var v) ? v : "";
                    string raw = G("phx_follow_days");
                    if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int days))
                    {
                        days = 0;
                        if (DateTime.TryParse(G("phx_follow_date"), CultureInfo.InvariantCulture,
                                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out DateTime followedAt))
                            days = Math.Max(0, (int)(DateTime.UtcNow - followedAt.ToUniversalTime()).TotalDays);
                    }
                    _engine.SetLocalResultVar("follow.days",         days.ToString(CultureInfo.InvariantCulture));
                    _engine.SetLocalResultVar("follow.formatted",    days >= 365 ? $"{days / 365}y {days % 365}d" : $"{days}d");
                    _engine.SetLocalResultVar("follow.date",         G("phx_follow_date"));
                    _engine.SetLocalResultVar("follow.is_following", NormBool(G("phx_follow_is")));
                }
                return null;
            });

            // twitch.last_active(username, thresholdMinutes, inactiveVar, minutesAgoVar)
            _engine.RegisterCommand("twitch.last_active", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string user           = bound?.GetOrDefault<string>("User", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                // Float (single-precision) is plenty for "minutes threshold" — the
                // fractional component is converted to double once we compare.
                double threshold;
                if (bound != null && bound.ContainsKey("ThresholdMins")) threshold = bound.Get<float>("ThresholdMins");
                else threshold = double.TryParse(ArgOrEmpty(args, 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var tx) ? tx : 5d;
                string inactiveVar    = bound?.GetOrDefault<string>("InactiveVar", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                string minutesAgoVar  = bound?.GetOrDefault<string>("MinutesAgoVar", ArgOrEmpty(args, 3)) ?? ArgOrEmpty(args, 3);
                if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(inactiveVar) || string.IsNullOrEmpty(minutesAgoVar))
                    return null;
                if (_lastActiveMap.TryGetValue(user, out DateTime lastSeen))
                {
                    double minsAgo = (DateTime.UtcNow - lastSeen).TotalMinutes;
                    _engine.SetLocalResultVar(inactiveVar,   minsAgo > threshold ? "true" : "false");
                    _engine.SetLocalResultVar(minutesAgoVar, ((int)minsAgo).ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    _engine.SetLocalResultVar(inactiveVar,   "true");
                    _engine.SetLocalResultVar(minutesAgoVar, "-1");
                }
                return null;
            });

            // twitch.get_viewers(resultVar) — the active-chatter list, comma-joined
            // into the result var. The request/parse itself is the suite's ONE shared
            // sweep (FetchActiveViewersAsync, ScriptManager.ViewerPresence.cs), which
            // hands this node three things its own hand-rolled copy never had: a
            // connectivity guard (a disconnected socket answered by burning the full
            // 5s timeout on every cron tick), a ValueKind == Array check before
            // enumerating (a `viewers` value that was not an array threw into the
            // catch and returned nobody), and lowercased logins — so the names a graph
            // gets here key the same rows the wallet and watch-time tables use.
            //
            // Fresh, not the cached presence sample: a graph that calls this is asking
            // "who is watching right now", and the caller has no way to say how stale
            // an answer it can live with.
            //
            // NOTE: `GetUsers` (the request this used to send) is NOT a real
            // Streamer.bot request — it was only ever answered by the external
            // StreamSimulator, which is why the cron worked under the sim but
            // awarded nobody points against a live Streamer.bot. The simulator
            // must mirror GetActiveViewers/`viewers[]` to match real SB.
            _engine.RegisterCommand("twitch.get_viewers", async (args) => {
                string resultVar = _engine.CurrentBoundArgs?.GetOrDefault<string>("ResultVar", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(resultVar)) return null;
                // NULL = the sweep FAILED (timeout, malformed payload) under the
                // nullable contract; this script surface coalesces it to empty so
                // the result var is written even then — a downstream text.split /
                // array.length reads an empty list rather than an unresolved
                // {token}, and the script run never aborts on a stalled socket.
                var viewers = await FetchActiveViewersAsync().ConfigureAwait(false)
                              ?? Array.Empty<ActiveViewer>();
                _engine.SetLocalResultVar(resultVar, string.Join(",", viewers.Select(v => v.Login)));
                return null;
            });
        }

        // ── Twitch chat / channel actions ───────────────────────────────────
        // The remaining direct chat + announcement-tier handlers that route
        // through Streamer.bot's named DoActions (vs the Helix-style requests
        // used by polls/predictions). All six enforce Twitch's 500-char body
        // cap up-front (M32) so the streamer sees a Communication log instead
        // of debugging a silent backend rejection.
        //   * twitch.send_chat (named StreamerBotChatAction in config)
        //   * twitch.timeout (M32 1..1209600s bounds-check)
        //   * twitch.shoutout
        //   * twitch.ban (H28 JSON-safe payload, M32 reason cap)
        //   * twitch.create_clip
        //   * twitch.announcement (M32 cap)
        /// <summary>
        /// The Twitch chat-send core — twitch.send_chat and the unified
        /// chat.send dispatcher both route here so the 500-char cap, chat-action
        /// config guard and WS-link guard live once. <paramref name="commandLabel"/>
        /// names the calling command in the log lines.
        /// </summary>
        internal void SendTwitchChatCore(string message, string commandLabel)
        {
            if (string.IsNullOrEmpty(message)) return;
            try
            {
                // Twitch chat messages are capped at 500 chars by the IRC
                // backend; anything longer is rejected upstream silently.
                if (message.Length > 500)
                {
                    GlobalLogger.Log($"{commandLabel} → DROPPED: message length {message.Length} exceeds 500-char Twitch cap.",
                        "Script", LogLevel.CriticalError);
                    return;
                }
                // Same explicit guards as ChatSource.SendAsBotAsync
                // (Hub.WinUI/Services/ChatSource.cs). A blank chat action or
                // a dropped WS link previously failed silently from a
                // user-visible perspective; surface them at CriticalError
                // tagged "Chat" so the System Log shows the failure beside
                // the panel's own sends.
                string actionName = (ConfigManager.Current.StreamerBotChatAction ?? string.Empty).Trim();
                if (actionName.Length == 0)
                {
                    GlobalLogger.Log(
                        $"{commandLabel} → DROPPED: Streamer.bot Chat Action name is not configured. " +
                        "Set it in Hub Settings → Connection → Chat Action Name.",
                        "Chat", LogLevel.CriticalError);
                    return;
                }
                if (!WS.Instance.IsConnected)
                {
                    GlobalLogger.Log(
                        $"{commandLabel} → DROPPED: Streamer.bot WebSocket is not connected (action='{actionName}', msg=\"{message}\").",
                        "Chat", LogLevel.CriticalError);
                    return;
                }
                string reqId = WS.NewRequestId("chat");
                WS.Instance.Send(JsonSerializer.Serialize(new
                {
                    request = "DoAction",
                    id      = reqId,
                    action  = new { name = actionName },
                    args    = new { message }
                }));
            }
            catch (Exception ex)
            {
                GlobalLogger.Log($"{commandLabel} failed: {ex.Message}", "ScriptEngine", LogLevel.CriticalError);
            }
        }

        /// <summary>
        /// Normalizes chat.send's platform list — a single name or a comma
        /// list, case-insensitive, quotes tolerated, "yt" accepted for
        /// youtube — into the canonical twitch/youtube/kick dispatch order
        /// with duplicates collapsed. Unrecognized tokens come back in
        /// <paramref name="unknownTokens"/> so the caller can log them.
        /// </summary>
        internal static IReadOnlyList<string> ParseChatPlatformTargets(
            string? csv, out IReadOnlyList<string> unknownTokens)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unknown = new List<string>();
            if (!string.IsNullOrWhiteSpace(csv))
            {
                foreach (var raw in csv.Split(','))
                {
                    string token = raw.Trim().Trim('"').Trim();
                    if (token.Length == 0) continue;
                    switch (token.ToLowerInvariant())
                    {
                        case Phoenix.Controls.Shared.Core.ChatPlatforms.Twitch:
                            seen.Add(Phoenix.Controls.Shared.Core.ChatPlatforms.Twitch);
                            break;
                        case Phoenix.Controls.Shared.Core.ChatPlatforms.YouTube:
                        case "yt":
                            seen.Add(Phoenix.Controls.Shared.Core.ChatPlatforms.YouTube);
                            break;
                        case Phoenix.Controls.Shared.Core.ChatPlatforms.Kick:
                            seen.Add(Phoenix.Controls.Shared.Core.ChatPlatforms.Kick);
                            break;
                        default:
                            unknown.Add(token);
                            break;
                    }
                }
            }
            unknownTokens = unknown;
            return Phoenix.Controls.Shared.Core.ChatPlatforms.All.Where(seen.Contains).ToList();
        }

        private void RegisterTwitchChatCommands()
        {
            // twitch.send_chat("message")
            // Twitch chat messages are capped at 500 chars by the IRC backend.
            // Anything longer is rejected by Streamer.bot/Twitch silently and the
            // script never sees the failure. Reject up-front so the streamer sees
            // a clear log instead of debugging a no-op.
            _engine.RegisterCommand("twitch.send_chat", async (args) => {
                string message = _engine.CurrentBoundArgs?.GetOrDefault<string>("Message", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                SendTwitchChatCore(message, "twitch.send_chat");
                return null;
            });

            // twitch.reply(messageId, message) — threaded reply to a specific chat
            // line via "Phoenix: Reply" (SB's Send Chat Message with a reply
            // target). MessageId comes from the Chat.Message trigger's MessageId
            // output ({event.message_id}). A reply is still a chat message, so the
            // same 500-char cap as twitch.send_chat applies. Mirrors kick.reply.
            _engine.RegisterCommand("twitch.reply", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string messageId = bound?.GetOrDefault<string>("MessageId", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string message   = bound?.GetOrDefault<string>("Message", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                if (string.IsNullOrWhiteSpace(messageId))
                {
                    GlobalLogger.Log("twitch.reply: empty messageId — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                if (string.IsNullOrEmpty(message))
                {
                    GlobalLogger.Log("twitch.reply: empty message — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                if (message.Length > 500)
                {
                    GlobalLogger.Log($"twitch.reply → DROPPED: message length {message.Length} exceeds 500-char Twitch chat cap.",
                        "Script", LogLevel.CriticalError);
                    return null;
                }
                DispatchNamedAction("twitch.reply", PhxSbActions.Reply, new { messageId, message });
                return null;
            });

            // chat.send(Message, Platforms, Fallback) — the unified Chat.Send
            // node's runtime dispatcher. Platforms is the node's override input
            // (single name or comma list); when it resolves EMPTY at runtime the
            // exporter-baked Fallback (the node's checkmark CSV) applies, so a
            // blank upstream value never silently sends nowhere. Each resolved
            // platform routes through the SAME core the per-platform command
            // uses — the caps and guards (500/200/500 chars, action config,
            // WS link) live exactly once.
            _engine.RegisterCommand("chat.send", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string message = bound?.GetOrDefault<string>("Message", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(message)) return null;
                string platforms = StripBareQuotes(bound?.GetOrDefault<string>("Platforms", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1));
                string fallback  = StripBareQuotes(bound?.GetOrDefault<string>("Fallback", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2));
                string list = string.IsNullOrWhiteSpace(platforms) ? fallback : platforms;

                var targets = ParseChatPlatformTargets(list, out var unknown);
                if (unknown.Count > 0)
                {
                    GlobalLogger.Log(
                        $"chat.send: unknown platform token(s) '{string.Join(", ", unknown)}' ignored — valid: twitch, youtube, kick.",
                        "Script", LogLevel.CriticalError);
                }
                if (targets.Count == 0)
                {
                    GlobalLogger.Log(
                        "chat.send → DROPPED: no valid platform resolved (override and checkmark fallback both empty).",
                        "Script", LogLevel.CriticalError);
                    return null;
                }

                foreach (var platform in targets)
                {
                    switch (platform)
                    {
                        case Phoenix.Controls.Shared.Core.ChatPlatforms.Twitch:
                            SendTwitchChatCore(message, "chat.send(twitch)");
                            break;
                        case Phoenix.Controls.Shared.Core.ChatPlatforms.YouTube:
                            SendYouTubeChatCore(message, "chat.send(youtube)");
                            break;
                        case Phoenix.Controls.Shared.Core.ChatPlatforms.Kick:
                            SendKickChatCore(message, "chat.send(kick)");
                            break;
                    }
                }
                return null;
            });

            // twitch.timeout(user, seconds)
            // Twitch's timeout duration is bounded: minimum 1 second, maximum
            // 1,209,600 seconds (14 days). Out-of-range durations are rejected by
            // Helix silently. Reject pre-flight so the streamer sees a Communication
            // log instead of a phantom no-op.
            // R19 (sweep 14) — reference handler for the typed-bind pattern:
            // pull strongly-typed args from _engine.CurrentBoundArgs (populated
            // by the engine dispatch site for AddTyped commands), with a
            // defensive fallback to the raw string[] if the bound dict is null.
            // Same observable behavior as the legacy parsing path; the typed
            // pull replaces the inline `int.TryParse(args[1], ...)` block.
            _engine.RegisterCommand("twitch.timeout", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string user = bound?.GetOrDefault<string>("User", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                // Sec is Optional Default "60". When invoked through the engine, the
                // binder applies that for missing args and coerces malformed values
                // to 0 (then the bounds check below rejects). The fallback below
                // (used only when called directly, bypassing engine dispatch — e.g.
                // test InvokeCommandAsync) mirrors that exactly: missing → 60,
                // present-but-unparseable → 0 → rejected by the bounds check.
                int sec;
                if (bound != null && bound.ContainsKey("Sec")) sec = bound.Get<int>("Sec");
                else
                {
                    string secRaw = ArgOrEmpty(args, 1);
                    sec = string.IsNullOrEmpty(secRaw) ? 60
                        : (int.TryParse(secRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0);
                }

                if (string.IsNullOrWhiteSpace(user))
                {
                    GlobalLogger.Log("twitch.timeout: empty user — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                if (sec < 1 || sec > 1209600)
                {
                    GlobalLogger.Log($"twitch.timeout: duration {sec}s out of range [1..1209600] — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                DispatchNamedAction("twitch.timeout", PhxSbActions.Timeout, new
                {
                    user,
                    duration = sec.ToString(CultureInfo.InvariantCulture)
                });
                GlobalLogger.Log($"Timeout: {user} for {sec}s", "Script", LogLevel.LogicExecution);
                return null;
            });

            // twitch.shoutout(user)
            _engine.RegisterCommand("twitch.shoutout", async (args) => {
                string user = _engine.CurrentBoundArgs?.GetOrDefault<string>("User", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(user)) return null;
                DispatchNamedAction("twitch.shoutout", PhxSbActions.Shoutout, new { user });
                GlobalLogger.Log($"Shoutout: {user}", "Script", LogLevel.LogicExecution);
                return null;
            });

            // twitch.ban — H28 JSON-safe payload, M32 reason cap.
            // H28 — raw string interpolation of user-supplied args produced invalid JSON
            // when the args contained `"` or `\`. Route through JsonSerializer like the
            // sibling twitch.* commands.
            // Twitch ban-reason field is capped at 500 chars. Truncated reasons
            // confuse mod logs; reject up-front and log so the streamer can shorten it.
            _engine.RegisterCommand("twitch.ban", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string user = bound?.GetOrDefault<string>("User", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string reason;
                if (bound != null && bound.ContainsKey("Reason")) reason = bound.Get<string>("Reason");
                else reason = args.Length >= 2 ? args[1] : "no reason";
                if (string.IsNullOrEmpty(reason)) reason = "no reason";  // Optional Default "" → fallback prose.
                if (string.IsNullOrWhiteSpace(user))
                {
                    GlobalLogger.Log("twitch.ban: empty user — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                if (reason.Length > 500)
                {
                    GlobalLogger.Log($"twitch.ban: reason length {reason.Length} exceeds 500-char cap — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                DispatchNamedAction("twitch.ban", PhxSbActions.Ban, new { user, reason });
                GlobalLogger.Log($"Banned {user}: {reason}", "Script", LogLevel.LogicExecution);
                return null;
            });
            // twitch.create_clip(duration?, title?) — data action. The "Phoenix:
            // Create Clip" action's C# sub-action calls CPH.CreateClip(title,
            // duration) and writes the resulting URL into phx_clip_url (phx_clip_ok
            // = "1"/"0"). Hub reads it back and exposes clip.url / clip.ok. (Clips
            // only succeed while the broadcaster is live; an offline clip returns
            // ok=false, url="".) The old Delay bool was dropped — CPH.CreateClip
            // takes a clip-length duration (5..60s), not a delay flag.
            _engine.RegisterCommand("twitch.create_clip", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string title = bound?.GetOrDefault<string>("Title", "") ?? "";
                int duration = (bound != null && bound.ContainsKey("Duration"))
                    ? bound.Get<int>("Duration")
                    : (int.TryParse(ArgOrEmpty(args, 0), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : 30);
                if (duration < 5)  duration = 5;
                if (duration > 60) duration = 60;
                var actionArgs = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["duration"] = duration.ToString(CultureInfo.InvariantCulture),
                };
                if (!string.IsNullOrEmpty(title)) actionArgs["title"] = title;
                var g = await FetchActionGlobalsAsync("twitch.create_clip", PhxSbActions.CreateClip, actionArgs)
                    .ConfigureAwait(false);
                if (g != null)
                {
                    string url = g.TryGetValue("phx_clip_url", out var u) ? u : "";
                    string ok  = g.TryGetValue("phx_clip_ok",  out var o) ? o : "0";
                    _engine.SetLocalResultVar("clip.url", url);
                    _engine.SetLocalResultVar("clip.ok",
                        (ok == "1" || ok.Equals("true", StringComparison.OrdinalIgnoreCase)) ? "true" : "false");
                    GlobalLogger.Log(url.Length > 0
                        ? $"Clip created: {url}"
                        : "Clip returned no URL (stream offline, or Phoenix: Create Clip has no C# sub-action yet).",
                        "Script", LogLevel.LogicExecution);
                }
                return null;
            });
            // Twitch announcement message body capped at 500 chars per Helix.
            _engine.RegisterCommand("twitch.announcement", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string message = bound?.GetOrDefault<string>("Message", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(message))
                {
                    GlobalLogger.Log("twitch.announcement: empty message — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                if (message.Length > 500)
                {
                    GlobalLogger.Log($"twitch.announcement: message length {message.Length} exceeds 500-char cap — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                // The "Phoenix: Announcement" action's Send Announcement sub-action
                // uses a FIXED colour (SB can't bind announcement colour to a
                // variable), so the node's old Color input was dropped — it never
                // had any effect. Only the message is forwarded.
                DispatchNamedAction("twitch.announcement", PhxSbActions.Announcement, new { message });
                return null;
            });
        }
    }
#pragma warning restore CS1998
}
