using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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

            // Data actions — unlike the fire-and-forget actions above, these write
            // their result into shared phx_* globals that Hub reads back via the
            // DoAction → poll GetGlobals(persisted:false) round-trip (see
            // FetchActionGlobalsAsync). Get User is reused by check_role and
            // get_stream (it carries the channel's mod/sub/vip flags + last
            // game/title). CreateClip (above) is also a data action — its C#
            // sub-action writes phx_clip_url / phx_clip_ok.
            public const string GetUser           = "Phoenix: Get User";
            public const string GetFollowAge      = "Phoenix: Get Follow Age";

            // OBS control — same model: the obs.* nodes dispatch these against an
            // SB action wrapping SB's native OBS sub-action (which talks to OBS over
            // OBS-WebSocket). Hub's own ObsWebSocketClient is receive-only today, so
            // outbound OBS control routes through Streamer.bot like the Twitch set.
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

            // Authoritative set the connect-probe checks against (and that the
            // action pack must define). twitch.resolve_prediction is intentionally
            // absent — it has no live path (see its handler).
            public static readonly string[] All =
            {
                Shoutout, Timeout, Ban, Unban, Mod, Unmod, Vip, Unvip,
                DeleteMessage, SlowMode, FollowerMode, SubOnlyMode, Marker,
                Whisper, UpdateChannel, Announcement, CreateClip, CreatePoll,
                EndPoll, CreatePrediction, UpdateRewardCost, SetRewardEnabled,
                FulfillRedemption, RejectRedemption,
                GetUser, GetFollowAge,
                ObsSetScene, ObsSourceVisible, ObsRefreshBrowser, ObsStartRecording,
                ObsStopRecording, ObsStartStreaming, ObsStopStreaming, ObsSaveReplay,
                ObsSourcePosition, ObsSourceScale, ObsSourceRotation, ObsFilterVisible,
                ObsScreenshot,
            };

            // Platform packs — deliberately NOT part of All. The probe checks them
            // SEPARATELY so the report stays one grouped Communication-tier line
            // per platform: a Twitch-only setup lacking every YT/Kick wrapper is
            // normal, not a CriticalError, and must not be spammed per action.
            public static readonly string[] YouTubeAll =
            {
                YtSendChat, YtSetTitle, YtSetDescription, YtTimeout, YtBan,
                YtCreatePoll, YtEndPoll, YtGetUser,
            };

            public static readonly string[] KickAll =
            {
                KickSendChat, KickReply, KickTimeout, KickBan, KickUnban,
                KickUntimeout, KickSetTitle, KickSetCategory, KickDeleteMessage,
                KickSetRewardCost, KickSetRewardEnabled, KickGetUser,
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

        // Subscribe the action-pack probe to SB (re)connects. Called once from the
        // singleton ctor (process-lifetime → no handler leak); also fires
        // immediately if SB is already connected when ScriptManager initialises.
        private void HookStreamerBotActionProbe()
        {
            WS.Instance.OnConnectionStatusChanged += connected =>
            {
                if (connected)
                    _ = AsyncErrorBoundary.SafeRunAsync(
                        ProbeStreamerBotActionsAsync, "ScriptManager", "ProbeStreamerBotActions");
            };
            if (WS.Instance.IsConnected)
                _ = AsyncErrorBoundary.SafeRunAsync(
                    ProbeStreamerBotActionsAsync, "ScriptManager", "ProbeStreamerBotActions");
        }

        // Enumerate Streamer.bot's actions (GetActions is one of SB's real WS
        // requests) and surface which Phoenix action-pack actions are missing, so
        // the operator sees a clear "import the pack" message at connect instead of
        // silent no-ops when a node fires. On timeout / no response we leave
        // _knownSbActions null so no false "missing" warnings are raised.
        private async Task ProbeStreamerBotActionsAsync()
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
        // User / Get Follow Age / Create Clip) instead write their result into
        // shared, NON-persisted globals named phx_* and echo a per-call token into
        // phx_req as their LAST sub-action. Hub fires the action, then polls
        // GetGlobals(persisted:false) until phx_req == its token — at which point
        // every sibling phx_* value is guaranteed present (it was written before
        // phx_req) and read from the SAME response. The phx_* globals are shared
        // scratch, so fetches are serialized through _dataFetchLane: only one
        // action's output is in the globals at a time.

        private readonly SemaphoreSlim _dataFetchLane = new(1, 1);

        // Broadcaster live-state, tracked from the Twitch.StreamOnline /
        // StreamOffline events Hub already subscribes to (WS.TwitchEvents). SB
        // exposes NO live-stream metrics for an arbitrary user, but it tells us
        // when OUR channel goes on/offline — so is_online / get_stream answer
        // truthfully for the broadcaster's own channel. Volatile: written on the
        // WS event thread, read on script-execution threads.
        private volatile bool _broadcasterLive;
        // StreamOnline time, as ticks. DateTime can't be `volatile`, and a 64-bit
        // field can tear / reorder relative to the volatile flag across threads, so
        // it's written/read via Interlocked (atomic 64-bit + full fence) — paired
        // with the volatile _broadcasterLive read in ResolveBroadcasterUptime.
        private long _broadcasterLiveSinceTicks;
        private int _isOnlineArbitraryWarned;

        // Called from ExecuteGenericEventAsync when a StreamOnline/StreamOffline
        // event arrives for the broadcaster's channel.
        internal void MarkBroadcasterLive(bool live)
        {
            if (live && !_broadcasterLive)
                Interlocked.Exchange(ref _broadcasterLiveSinceTicks, DateTime.UtcNow.Ticks);
            _broadcasterLive = live;
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
            if (known != null && !known.Contains(actionName)
                && _missingSbActionWarned.TryAdd(actionName, 0))
            {
                GlobalLogger.Log(
                    $"{command} → Streamer.bot action \"{actionName}\" not found. " +
                    "Import the Phoenix Controls action pack so this node can run.",
                    "Script", LogLevel.CriticalError);
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
            var elapsed = DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            if (elapsed.TotalMinutes < 1) return $"{(int)elapsed.TotalSeconds}s";
            if (elapsed.TotalHours   < 1) return $"{(int)elapsed.TotalMinutes}m";
            return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
        }

        // Maps the phx_user_* globals from a Get User fetch onto the user.* result
        // slots. Shared by twitch.get_user and streamerbot.get_user.
        private void ApplyUserGlobals(Dictionary<string, string> g, string fallbackUser)
        {
            string G(string k) => g.TryGetValue(k, out var v) ? v : "";
            string login   = G("phx_user_login");
            string display = G("phx_user_display");
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

            // twitch.resolve_prediction — DEFERRED (no live path). Resolving needs
            // the prediction id + winning-outcome id that only the create call can
            // mint, and DoAction can't return them; SB's resolve sub-action support
            // is also version-dependent. Log loudly instead of firing a fictional
            // request that silently no-ops. Revisit when Hub gains direct Helix.
            _engine.RegisterCommand("twitch.resolve_prediction", async (args) =>
            {
                GlobalLogger.Log(
                    "twitch.resolve_prediction is not available over Streamer.bot (no native resolve path; the " +
                    "prediction/outcome ids can't be retrieved over DoAction). No action taken — this node is " +
                    "deferred until Hub gains direct Twitch access.",
                    "Script", LogLevel.CriticalError);
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

            _engine.RegisterCommand("twitch.fulfill_redemption", async (args) =>
            {
                string redemptionId = _engine.CurrentBoundArgs?.GetOrDefault<string>("RedemptionId", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(redemptionId)) return null;
                DispatchNamedAction("twitch.fulfill_redemption", PhxSbActions.FulfillRedemption, new { redemptionId });
                return null;
            });

            _engine.RegisterCommand("twitch.reject_redemption", async (args) =>
            {
                string redemptionId = _engine.CurrentBoundArgs?.GetOrDefault<string>("RedemptionId", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(redemptionId)) return null;
                DispatchNamedAction("twitch.reject_redemption", PhxSbActions.RejectRedemption, new { redemptionId });
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

            // twitch.get_stream(username) — game/title come from "Phoenix: Get
            // User" (the channel's last set game + title, offline-capable — the
            // "last game played" the old fictional GetStreamInfo never returned).
            // Live metrics (is_live / viewers / uptime) are NOT retrievable for an
            // arbitrary channel over Streamer.bot; for the broadcaster's OWN
            // channel we answer is_live/uptime from the StreamOnline/Offline-tracked
            // flag. viewers stays "0" (no SB path for it yet — Helix-followup).
            _engine.RegisterCommand("twitch.get_stream", async (args) => {
                string user = StripBareQuotes(_engine.CurrentBoundArgs?.GetOrDefault<string>("Username", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                if (string.IsNullOrEmpty(user)) return null;
                var g = await FetchActionGlobalsAsync("twitch.get_stream", PhxSbActions.GetUser,
                    new Dictionary<string, string> { ["user"] = user }).ConfigureAwait(false);
                if (g != null)
                {
                    string G(string k) => g.TryGetValue(k, out var v) ? v : "";
                    bool ownChannel = IsConfiguredBroadcaster(G("phx_user_login")) || IsConfiguredBroadcaster(user);
                    _engine.SetLocalResultVar("stream.title",   G("phx_user_title"));
                    _engine.SetLocalResultVar("stream.game",    G("phx_user_game"));
                    _engine.SetLocalResultVar("stream.is_live", (ownChannel && _broadcasterLive) ? "true" : "false");
                    _engine.SetLocalResultVar("stream.viewers", "0");
                    _engine.SetLocalResultVar("stream.uptime",  ownChannel ? ResolveBroadcasterUptime() : "");
                }
                return null;
            });

            // twitch.is_online(Channel) — backs Twitch.IsOnline's IsLive output.
            // For the broadcaster's OWN channel (blank Channel = broadcaster) we
            // answer from the StreamOnline/Offline-tracked live flag. Streamer.bot
            // exposes NO live-status path for an ARBITRARY channel, so those report
            // not-live (honest), but still surface game/title from "Phoenix: Get User".
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
                // Arbitrary channel — no SB live path. Report offline honestly,
                // but fetch game/title so the node isn't fully empty.
                if (Interlocked.Exchange(ref _isOnlineArbitraryWarned, 1) == 0)
                    GlobalLogger.Log(
                        "twitch.is_online: live status for a channel other than the broadcaster's own cannot be " +
                        "retrieved over Streamer.bot — reporting not-live. (One-time note.)",
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
                    _engine.SetLocalResultVar("role.is_mod", NormBool(G("phx_user_mod")));
                    _engine.SetLocalResultVar("role.is_sub", NormBool(G("phx_user_sub")));
                    _engine.SetLocalResultVar("role.is_vip", NormBool(G("phx_user_vip")));
                    string login = G("phx_user_login");
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

            // twitch.get_viewers(resultVar) — fetches the active-chatter list via
            // Streamer.bot's GetActiveViewers request. The response carries a
            // `viewers[]` array (NOT `users[]`); each entry exposes `login`,
            // `display`, `role`, `subscribed`, etc. We collect the `login` handles.
            //
            // NOTE: `GetUsers` (the request this used to send) is NOT a real
            // Streamer.bot request — it was only ever answered by the external
            // StreamSimulator, which is why the cron worked under the sim but
            // awarded nobody points against a live Streamer.bot. The simulator
            // must mirror GetActiveViewers/`viewers[]` to match real SB.
            _engine.RegisterCommand("twitch.get_viewers", async (args) => {
                string resultVar = _engine.CurrentBoundArgs?.GetOrDefault<string>("ResultVar", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(resultVar)) return null;
                // Brand sweep — was "sovereign-get-viewers-…".
                string reqId = $"phx-get-viewers-{Guid.NewGuid():N}";
                string response = await WS.Instance.SendAndWaitAsync(
                    $@"{{""request"":""GetActiveViewers"",""id"":""{reqId}""}}",
                    reqId, 5000).ConfigureAwait(false);
                var names = new List<string>();
                if (!string.IsNullOrEmpty(response))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(response);
                        if (doc.RootElement.TryGetProperty("viewers", out var viewers))
                            foreach (var v in viewers.EnumerateArray())
                                if (v.TryGetProperty("login", out var login)
                                    && login.GetString() is { Length: > 0 } name)
                                    names.Add(name);
                    }
                    catch (Exception ex)
                    {
                        // Bare catch hid JSON-parse failures of
                        // the Streamer.bot GetActiveViewers response, masking malformed payloads.
                        GlobalLogger.Log($"twitch.get_viewers: JSON parse failed: {ex.Message}", "Script", LogLevel.CriticalError);
                    }
                }
                _engine.SetLocalResultVar(resultVar, string.Join(",", names));
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
        private void RegisterTwitchChatCommands()
        {
            // twitch.send_chat("message")
            // Twitch chat messages are capped at 500 chars by the IRC backend.
            // Anything longer is rejected by Streamer.bot/Twitch silently and the
            // script never sees the failure. Reject up-front so the streamer sees
            // a clear log instead of debugging a no-op.
            _engine.RegisterCommand("twitch.send_chat", async (args) => {
                string message = _engine.CurrentBoundArgs?.GetOrDefault<string>("Message", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(message)) return null;
                try
                {
                    if (message.Length > 500)
                    {
                        GlobalLogger.Log($"twitch.send_chat → DROPPED: message length {message.Length} exceeds 500-char Twitch cap.",
                            "Script", LogLevel.CriticalError);
                        return null;
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
                            "twitch.send_chat → DROPPED: Streamer.bot Chat Action name is not configured. " +
                            "Set it in Hub Settings → Connection → Chat Action Name.",
                            "Chat", LogLevel.CriticalError);
                        return null;
                    }
                    if (!WS.Instance.IsConnected)
                    {
                        GlobalLogger.Log(
                            $"twitch.send_chat → DROPPED: Streamer.bot WebSocket is not connected (action='{actionName}', msg=\"{message}\").",
                            "Chat", LogLevel.CriticalError);
                        return null;
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
                    GlobalLogger.Log($"twitch.send_chat failed: {ex.Message}", "ScriptEngine", LogLevel.CriticalError);
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
