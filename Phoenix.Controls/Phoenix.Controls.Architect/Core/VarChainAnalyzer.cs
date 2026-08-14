using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Architect.Core
{
    /// <summary>
    /// First half of Priority-1 var-chain visualization.
    /// Pure-data analyzer: given a graph and a variable name (with or
    /// without the {curly}-brace), returns the set of nodes that
    /// <em>write</em> the var (Var.Set / Public.Set / DB.SetVariable
    /// / DB.Increment / nodes whose ResultKey/ResultVar/Row attribute
    /// matches / nodes that emit a result.* alias matching the name)
    /// and the set of nodes that <em>read</em> the var (any node
    /// attribute or socket-default value that contains a literal
    /// <c>{varname}</c> token).
    ///
    /// Scope:
    ///   * Pure analysis — no canvas paint, no UI state. The Architect
    ///     Trace-Variable context menu uses the result to surface
    ///     writers + readers in a popup.
    ///   * The fuller Priority-1 UX (hover-pill auto-highlight, dim
    ///     non-chain mode) is the deferred follow-up; the analyzer
    ///     here is the load-bearing piece both modes share, so the
    ///     paint-side work lands without re-implementing this.
    ///
    /// Names matched case-insensitively. Namespaced refs match by
    /// exact key — a query for <c>user.points</c> only matches
    /// <c>{user.points}</c>, not <c>{points}</c>.
    /// </summary>
    public static class VarChainAnalyzer
    {
        /// <summary>Result of a single trace.</summary>
        public sealed class Trace
        {
            public string VarName { get; init; } = "";
            public List<Node> Writers { get; } = new();
            public List<Node> Readers { get; } = new();
        }

        // {key} — same shape SubstituteVars matches at runtime. Matches
        // a single curly-braced token whose body is a non-empty run of
        // identifier chars / dots; conservative on purpose so we don't
        // match braces that are part of literal text.
        private static readonly Regex VarRefRegex = new(
            @"\{([A-Za-z_][A-Za-z0-9_\.]*)\}",
            RegexOptions.Compiled);

        public static Trace Analyze(Graph graph, string varName)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            if (string.IsNullOrWhiteSpace(varName)) return new Trace { VarName = "" };

            string trimmed = varName.Trim();
            // Strip wrapping braces so callers can pass "{user.points}" or "user.points".
            if (trimmed.Length >= 2 && trimmed[0] == '{' && trimmed[^1] == '}')
                trimmed = trimmed.Substring(1, trimmed.Length - 2);
            if (string.IsNullOrEmpty(trimmed)) return new Trace { VarName = "" };

            var trace = new Trace { VarName = trimmed };
            var addedWriters = new HashSet<string>(StringComparer.Ordinal);
            var addedReaders = new HashSet<string>(StringComparer.Ordinal);

            foreach (var n in graph.Nodes)
            {
                if (n == null) continue; // guard null node before deref
                if (IsWriter(n, trimmed) && addedWriters.Add(n.Id))
                    trace.Writers.Add(n);
                if (IsReader(n, trimmed) && addedReaders.Add(n.Id))
                    trace.Readers.Add(n);
            }

            return trace;
        }

        private static bool IsWriter(Node n, string varName)
        {
            if (n == null) return false; // guard null node before deref
            if (n.Attributes == null) return false; // Attributes can be null after JSON deserialization — guard before every TryGetValue/foreach deref
            // Direct producers — Var.Set / Var.Inc / Var.Toggle / Public.Set
            // bind by attribute.
            if (n.Title is "Var.Set" or "Var.Inc" or "Var.Toggle"
                && n.Attributes.TryGetValue("VariableName", out var vn)
                && string.Equals(vn?.Trim(), varName, StringComparison.OrdinalIgnoreCase))
                return true;
            if (n.Title is "Public.Set"
                && n.Attributes.TryGetValue("KeyName", out var kn)
                && !string.IsNullOrWhiteSpace(kn))
            {
                if (string.Equals("public." + kn.Trim(), varName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // DB.SetVariable / DB.Increment — bind by Key socket-default attr.
            if (n.Title is "DB.SetVariable" or "DB.Increment"
                && n.Attributes.TryGetValue("Key", out var dbk)
                && string.Equals(dbk?.Trim().Trim('"'), varName, StringComparison.OrdinalIgnoreCase))
                return true;

            // DB.FetchRow / DB.* — Row / NewRowId / ResultKey / *ResultVar attrs.
            foreach (var kv in n.Attributes)
            {
                string k = kv.Key;
                bool isResultAttr =
                    string.Equals(k, "ResultKey", StringComparison.Ordinal)
                    || string.Equals(k, "ResultVar", StringComparison.Ordinal)
                    || string.Equals(k, "Row",       StringComparison.Ordinal)
                    || string.Equals(k, "NewRowId",  StringComparison.Ordinal)
                    || k.EndsWith("ResultKey", StringComparison.Ordinal)
                    || k.EndsWith("ResultVar", StringComparison.Ordinal);
                if (!isResultAttr) continue;
                if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                string val = kv.Value.Trim().Trim('"');
                if (string.Equals(val, varName, StringComparison.OrdinalIgnoreCase))
                    return true;
                // Cover the case where a script reads {ResultKey.col} downstream:
                // mark the FetchRow / lookup as a writer of the dotted form too.
                if (varName.StartsWith(val + ".", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Curated result-emitter map — matches the AutocompleteScopeBuilder
            // contributor table. Keep the two in sync when adding a new node.
            return WrittenByResultEmitter(n.Title, varName);
        }

        /// <summary>
        /// Stable, non-attribute-driven result-emitter contributions per node
        /// title. Centralised here so <see cref="WrittenByResultEmitter"/>
        /// (the writer-membership check) and <see cref="EnumerateAllVars"/>
        /// (the trace-picker dropdown union) read from the same source of
        /// truth — pre-fix the picker missed every <c>user.*</c> /
        /// <c>result.*</c> / <c>loop.*</c> name unless the user had already
        /// typed it into a <c>{...}</c> reference. Loop bindings are stored
        /// here too (Flow.ForLoop → loop.index, Flow.ForEach → loop.item) so
        /// the picker surfaces them on graphs that contain the source node
        /// but no consumer yet.
        /// </summary>
        // Keep alphabetised within each section so additions land at predictable spots.
        private static readonly IReadOnlyDictionary<string, string[]> ResultEmitterMap = BuildResultEmitterMap();

        // Literal entries first, then the PlatformEventCatalog fold — a plain
        // collection initializer can't merge the 50 catalog entries, so the
        // map is built here instead.
        private static IReadOnlyDictionary<string, string[]> BuildResultEmitterMap()
        {
            var map = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                // Chat / Twitch event sources.
                // Chat.Message — unified multi-platform chat trigger:
                // Twitch.ChatMessage's set + the platform discriminator. The
                // legacy titles below ("Twitch.ChatMessage" / "YouTube.Message")
                // stay listed for graphs not yet re-saved through migration:
                // they match no NodeRegistry template and therefore LOOK like
                // phantom keys, but GraphSerializer.MigrateNodes only retitles
                // them to Chat.Message on load, so an in-memory node still
                // carries the legacy title before migration runs, and the
                // exporter still honours both. DO NOT delete them —
                // AnalyzerNodeKeyIntegrityTests allow-lists exactly these two.
                ["Chat.Message"]            = new[] { "user.message", "user.name", "user.command", "user.args", "user.is_mod", "user.is_sub", "user.is_vip", "user.is_broadcaster", "user.is_regular", "user.color_hex", "user.sub_months", "event.iscommand", "user.platform", "event.message_id" },
                ["Twitch.ChatMessage"]      = new[] { "user.message", "user.name", "user.command", "user.args", "user.is_mod", "user.is_sub", "user.is_vip", "user.is_broadcaster", "user.is_regular", "user.color_hex", "user.sub_months", "event.iscommand", "event.message_id" },
                ["Twitch.Subscription"]     = new[] { "user.name", "user.sub_months", "user.tier" },
                // user.tier added: Twitch.Resub's template exposes a Tier output
                // (maps to {user.tier}); the analyzer was missing it.
                ["Twitch.Resub"]            = new[] { "user.name", "user.sub_months", "user.message", "user.tier" },
                // user.is_anonymous added to GiftSub / GiftBomb: both templates
                // expose an IsAnonymous output (maps to {user.is_anonymous}) for the
                // "thank the gifter" gate; the analyzer was missing it.
                ["Twitch.GiftSub"]          = new[] { "user.gifter", "user.recipient", "user.tier", "user.is_anonymous" },
                ["Twitch.GiftBomb"]         = new[] { "user.gifter", "user.count", "user.is_anonymous" },
                ["Twitch.Raid"]             = new[] { "user.name", "user.viewers" },
                ["Twitch.Cheer"]            = new[] { "user.name", "user.bits", "user.message" },
                ["Twitch.Follow"]           = new[] { "user.name" },
                ["Twitch.PointRedeem"]      = new[] { "user.name", "user.reward", "user.input" },
                ["Twitch.InWhisper"]        = new[] { "user.name", "user.message", "user.id" },
                ["YouTube.Message"]         = new[] { "user.name", "user.message" },
                // Unified stream-lifecycle triggers — the firing platform + the
                // best-effort stream Title / Category (Hub dispatch binds
                // user.platform / event.title / event.category).
                ["Stream.GoingLive"]        = new[] { "user.platform", "event.title", "event.category" },
                ["Stream.SessionEnd"]       = new[] { "user.platform", "event.title", "event.category" },
                // Generic events / scheduler / state.
                // bus.* (not event.*) is the intentional, runtime-correct naming:
                // Hub's Bus.cs binds bus.type / bus.source / bus.target / bus.payload
                // into the script vars dict, and ScriptExporter emits {bus.source} /
                // {bus.target} guards. AutocompleteScopeBuilder["Bus.OnMessage"] is
                // synced to this same set — keep both on bus.* if you touch either.
                ["Bus.OnMessage"]           = new[] { "bus.type", "bus.payload", "bus.source", "bus.target" },
                ["HTTP.WebhookListener"]    = new[] { "event.payload", "event.body", "event.method", "event.path" },
                // WS.Server entry added, mirroring HTTP.WebhookListener. The engine
                // binds event.body / event.payload / event.path (no method — WS frames
                // carry no HTTP verb) in ScriptManager.ExecuteOnWebSocketScriptsAsync.
                ["WS.Server"]               = new[] { "event.payload", "event.body", "event.path" },
                ["Schedule.Cron"]           = new[] { "event.timestamp" },
                ["Schedule.RunAt"]          = new[] { "event.timestamp" },
                ["Schedule.Recurring"]      = new[] { "event.timestamp", "event.count" },
                ["State.OnChange"]          = new[] { "event.name", "event.oldvalue", "event.newvalue" },
                // Counter.OnChanged — synced with AutocompleteScopeBuilder and the
                // Counter.OnChanged arm in ScriptExporter.ResolveOutputFromNode
                // (CountersService binds event.counter / event.count).
                ["Counter.OnChanged"]       = new[] { "event.counter", "event.count" },
                // Automod.OnViolation — synced with AutocompleteScopeBuilder and the
                // Automod.OnViolation arm in ScriptExporter.ResolveOutputFromNode
                // (AutomodService binds event.user / rule / action / reason / message).
                ["Automod.OnViolation"]     = new[] { "event.user", "event.rule", "event.action", "event.reason", "event.message" },
                // Quote.OnAdded — synced with AutocompleteScopeBuilder and the
                // Quote.OnAdded arm in ScriptExporter.ResolveOutputFromNode
                // (QuotesService binds event.number / event.text / event.name).
                ["Quote.OnAdded"]           = new[] { "event.number", "event.text", "event.name" },
                // Command.OnCustom — synced with AutocompleteScopeBuilder and the
                // Command.OnCustom arm in ScriptExporter.ResolveOutputFromNode
                // (CustomCommandsService binds event.command / event.user / event.args).
                ["Command.OnCustom"]        = new[] { "event.command", "event.user", "event.args" },
                // Queue.OnChanged — synced with AutocompleteScopeBuilder and the
                // Queue.OnChanged arm in ScriptExporter.ResolveOutputFromNode
                // (NamedQueueService binds event.queue / entry / action / length; a legacy
                // unnamed-queue mutation raises the same set with an empty event.queue).
                ["Queue.OnChanged"]         = new[] { "event.queue", "event.entry", "event.action", "event.length" },
                // Song.On* — synced with AutocompleteScopeBuilder and the shared Song.On*
                // arm in ScriptExporter.ResolveOutputFromNode (SongRequestService's
                // RaiseSongEvent binds event.title / requester / video_id plus one extra
                // per root). The snake_case tokens are the runtime's spelling, which is
                // exactly why that exporter arm has to exist.
                ["Song.OnQueued"]           = new[] { "event.title", "event.requester", "event.video_id", "event.position" },
                ["Song.OnPlay"]             = new[] { "event.title", "event.requester", "event.video_id", "event.duration_seconds" },
                ["Song.OnSkip"]             = new[] { "event.title", "event.requester", "event.video_id", "event.skipped_by" },
                // Poll.On* — synced with AutocompleteScopeBuilder and the shared Poll.On*
                // arm in ScriptExporter.ResolveOutputFromNode (PollsService's RaiseOpened /
                // RaiseClosed / RaiseSettled). The snake_case tokens are the runtime's
                // spelling, which is exactly why that exporter arm has to exist.
                // event.option_count has no output socket but is a live var on the run, so
                // the trace picker lists it — same rule the Timer roots follow.
                ["Poll.OnOpened"]           = new[] { "event.title", "event.options", "event.option_count", "event.duration_seconds", "event.betting" },
                ["Poll.OnClosed"]           = new[] { "event.title", "event.winner", "event.winner_votes", "event.total_votes", "event.options" },
                ["Poll.OnSettled"]          = new[] { "event.title", "event.winner", "event.outcome", "event.pot", "event.winners", "event.winner_count", "event.currency" },
                // Rank.OnRankUp — synced with AutocompleteScopeBuilder and the
                // Rank.OnRankUp arm in ScriptExporter.ResolveOutputFromNode (RanksService's
                // RaiseRankUp). event.login has its own Login output socket; event.user_login
                // (the same login under the suite-wide spelling every Rank.* node's empty-User
                // fallback resolves through) and event.unit have none but are live vars on the
                // run, so the trace picker lists them — same rule the Timer and
                // User-Management roots follow.
                ["Rank.OnRankUp"]           = new[] { "event.user", "event.login", "event.user_login", "event.rankname", "event.value", "event.unit", "event.next" },
                // Soundboard.OnPlay — synced with AutocompleteScopeBuilder and the
                // Soundboard.OnPlay arm in ScriptExporter.ResolveOutputFromNode
                // (SoundboardService's RaisePlayed). user.name has no output socket but is
                // a live var on the run, so the trace picker lists it — same rule the Timer
                // and User-Management roots follow.
                ["Soundboard.OnPlay"]       = new[] { "event.command", "event.user", "event.clip", "user.name" },
                // User.OnFirstMessage — synced with AutocompleteScopeBuilder and the
                // User.OnFirstMessage arm in ScriptExporter.ResolveOutputFromNode
                // (UserManagementService binds event.user / login / message / platform /
                // first_ever). event.login has no output socket but is a live var on the
                // run, so the trace picker lists it — same rule the Timer roots follow.
                ["User.OnFirstMessage"]     = new[] { "event.user", "event.login", "event.message", "event.platform", "event.first_ever" },
                // Timer.On* — synced with AutocompleteScopeBuilder. TimerService's
                // Fire*Async raise sites bind the socket-derived event.* keys plus a
                // slug / remaining pair and the raw timer.* aliases; all are live vars
                // on the run, so the trace picker lists them all.
                ["Timer.OnZero"]            = new[] { "event.timername", "event.slug", "timer.name", "timer.slug" },
                ["Timer.OnMilestone"]       = new[] { "event.timername", "event.milestoneid", "event.label", "event.slug", "timer.name", "timer.slug", "timer.milestone_id", "timer.label" },
                ["Timer.OnAdd"]             = new[] { "event.timername", "event.source", "event.seconds", "event.slug", "event.remaining", "timer.name", "timer.slug", "timer.source", "timer.seconds", "timer.remaining" },
                // Loyalty.On* — synced with AutocompleteScopeBuilder and the Loyalty
                // arm in ScriptExporter.ResolveOutputFromNode (LoyaltyService raises
                // them from Earn.cs / LoyaltyService.cs / Games.cs). The un-namespaced
                // reward / cost / balance aliases the redeem raise also sets are
                // deliberately omitted — the event.* form is the documented one.
                ["Loyalty.OnEarn"]          = new[] { "event.user", "event.amount", "event.reason", "event.balance", "event.currency", "user.name" },
                ["Loyalty.OnPayout"]        = new[] { "event.count", "event.total", "event.amount", "event.currency" },
                ["Loyalty.OnRedeem"]        = new[] { "event.user", "event.reward", "event.cost", "event.balance", "event.currency", "user.name" },
                ["Loyalty.OnRaffle"]        = new[] { "event.winners", "event.count", "event.pot", "event.entrants", "event.currency" },
                // Twitch read-side queries.
                ["Twitch.GetUser"]          = new[] { "user.id", "user.display_name", "user.login", "user.profile_image", "user.account_created", "user.game", "user.channel_title", "user.is_mod", "user.is_sub", "user.is_vip", "user.is_regular" },
                ["Twitch.GetStream"]        = new[] { "stream.title", "stream.game", "stream.viewers", "stream.is_live", "stream.uptime" },
                ["Twitch.CheckRole"]        = new[] { "role.is_mod", "role.is_sub", "role.is_vip", "role.is_broadcaster", "role.is_regular" },
                // User.GetGroups — the User-Management group lookup. The four
                // standard keys are fixed; custom-group keys (group.<sanitized>)
                // are per-node dynamic (from the Groups attribute) and therefore
                // not listable in a static map — AutocompleteScopeBuilder derives
                // them per node instead.
                ["User.GetGroups"]          = new[] { "group.moderator", "group.vip", "group.subscriber", "group.regular" },
                ["Twitch.GetFollowAge"]     = new[] { "follow.days", "follow.formatted", "follow.date", "follow.is_following" },
                ["Twitch.CreateClip"]       = new[] { "clip.url", "clip.ok" },
                // Widened to Twitch.GetUser's full set: streamerbot.get_user is an
                // exact by-name mirror of twitch.get_user in ScriptManager.Twitch.cs
                // — both call the shared ApplyUserGlobals, which binds all eleven
                // user.* slots. The four-key entry under-reported the node.
                ["StreamerBot.GetUser"]     = new[] { "user.id", "user.display_name", "user.login", "user.profile_image", "user.account_created", "user.game", "user.channel_title", "user.is_mod", "user.is_sub", "user.is_vip", "user.is_regular" },
                // File / HTTP. The registered File.* templates are the ReadText /
                // ReadJSON / WriteText / WriteJSON quartet; the bare "File.Read" /
                // "File.ReadAll" / "File.Write" / "File.Append" keys that used to
                // sit here matched no template and were unreachable — dropped.
                ["File.ReadText"]  = new[] { "result.file_content", "result.file_error" },
                ["File.ReadJSON"]  = new[] { "result.file_content", "result.file_error" },
                ["File.WriteText"] = new[] { "result.file_error" },
                ["File.WriteJSON"] = new[] { "result.file_error" },
                ["HTTP.Get"]       = new[] { "result.http_status", "result.http_body", "result.http_error" },
                ["HTTP.Post"]      = new[] { "result.http_status", "result.http_body", "result.http_error" },
                ["HTTP.Put"]       = new[] { "result.http_status", "result.http_body", "result.http_error" },
                ["HTTP.Patch"]     = new[] { "result.http_status", "result.http_body", "result.http_error" },
                ["HTTP.Delete"]    = new[] { "result.http_status", "result.http_body", "result.http_error" },
                // Registered title is "API.Call" (Platforms band); the old
                // "HTTP.Api" key matched no template, so the api.call node's two
                // result vars never reached the Trace-Variable picker.
                ["API.Call"]       = new[] { "result.api_response", "result.api_error" },
                ["HTTP.ParseJson"] = new[] { "result.json_value" },
                // AI / Audio. (Keyed by the real node titles; the
                // prior "AI.GenerateText" key matched no node, so var-chain
                // analysis never saw any AI result var.)
                ["AI.Prompt"]          = new[] { "result.ai_response", "result.ai_error" },
                ["AI.VisionDescribe"]  = new[] { "result.ai_response", "result.ai_error" },
                // AI.StreamText also writes the stream-close / failure
                // sentinels (result.ai_done / result.ai_error_kind /
                // result.ai_retry_after), now surfaced as wireable outputs.
                ["AI.StreamText"]      = new[] { "result.ai_response", "result.ai_error", "result.ai_done", "result.ai_error_kind", "result.ai_retry_after" },
                // result.ai_done flips when the tool call completes.
                ["AI.WithTools"]       = new[] { "result.ai_response", "result.ai_tool_calls", "result.ai_error", "result.ai_done" },
                ["AI.Moderate"]        = new[] { "result.ai_flagged",  "result.ai_category", "result.ai_error" },
                // result.ai_image_done added: ScriptManager.AI.cs's
                // ai.generate_image handler seeds it "false" up front and flips it
                // "true" on every exit path (success and failure), so scripts get a
                // clean completion edge. AutocompleteScopeBuilder already listed it;
                // this map did not, so the Trace-Variable picker missed it.
                ["AI.GenerateImage"]   = new[] { "result.ai_image_url", "result.ai_image_error", "result.ai_image_done" },
                // "Audio.Stop" replaced by "Audio.PlayTts": there is no Audio.Stop
                // template, while Audio.PlayTts is fully wired (exporter handler +
                // ScriptManager.Audio.cs command) and writes the same contract.
                ["Audio.Play"]      = new[] { "result.audio_error" },
                ["Audio.PlayTts"]   = new[] { "result.audio_error" },
                ["Audio.SetVolume"] = new[] { "result.audio_error" },
                // Discord.
                ["Discord.SendMessage"] = new[] { "result.discord_message_id", "result.discord_error" },
                ["Discord.SendEmbed"]   = new[] { "result.discord_message_id", "result.discord_error" },
                ["Discord.AddRole"]     = new[] { "result.discord_error" },
                ["Discord.RemoveRole"]  = new[] { "result.discord_error" },
                ["Discord.React"]       = new[] { "result.discord_error" },
                ["Discord.GetUser"]     = new[] { "result.discord_user_id", "result.discord_user_name", "result.discord_user_global_name", "result.discord_user_avatar", "result.discord_error" },
                // Streamer.bot dispatch. LIVE BUG, not dead weight: the registered
                // template is "StreamerBot.DoAction" with a CAPITAL B. This map is
                // Ordinal-keyed, so the old lowercase-b key never matched a real
                // node — meaning result.sb_dispatched has never been offered in the
                // Trace-Variable picker for the one node that produces it, even
                // though NodeProse documents the variable to users
                // ("{result.sb_dispatched} reports whether it went out"). The
                // sibling "System.DoAction" key matched no template at all and is
                // dropped.
                ["StreamerBot.DoAction"] = new[] { "result.sb_dispatched" },
                // Loop bindings — per-loop-id form mirrors the engine's
                // Flow.ForLoop / Flow.ForEach substitution.
                ["Flow.ForLoop"] = new[] { "loop.index" },
                ["Flow.ForEach"] = new[] { "loop.item" },
            };

            // Catalog-driven platform events (YouTube/Kick) — folded in from
            // the single source in PlatformEventCatalog instead of hand-listing
            // 50 entries. The Hub runtime injects user.platform + event.payload
            // for every catalog event on top of the per-socket tokens; literal
            // entries above win on a title collision. Distinct guards the
            // "Payload" socket, whose VarToken is already event.payload.
            foreach (var def in PlatformEventCatalog.Events)
            {
                if (map.ContainsKey(def.Title)) continue;
                map[def.Title] = def.Sockets.Select(s => s.VarToken)
                    .Append("user.platform")
                    .Append("event.payload")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            return map;
        }

        /// <summary>
        /// Test seam: every dispatch key in <see cref="ResultEmitterMap"/>,
        /// including the PlatformEventCatalog entries folded in at build time.
        /// Consumed by <c>AnalyzerNodeKeyIntegrityTests</c>, which asserts each
        /// key resolves to a real NodeRegistry template (or catalog event) so a
        /// misspelled title can't silently become an unreachable arm again.
        /// Internal rather than public — Phoenix.Controls.Architect already
        /// grants <c>InternalsVisibleTo Phoenix.Controls.Tests</c> in its csproj,
        /// which keeps this off the shipped API surface.
        /// </summary>
        internal static IEnumerable<string> DispatchKeysForTests => ResultEmitterMap.Keys;

        private static bool WrittenByResultEmitter(string title, string varName)
        {
            if (string.IsNullOrEmpty(title)) return false;
            if (!ResultEmitterMap.TryGetValue(title, out var stable)) return false;
            foreach (var s in stable)
                if (string.Equals(s, varName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool IsReader(Node n, string varName)
        {
            if (n.Attributes == null) return false; // Attributes can be null after JSON deserialization — match IsWriter's guard before every foreach/TryGetValue deref
            // A node "reads" the var if any of its attribute values contains
            // {varname} (case-insensitive). Catches inline pill content,
            // multi-line Templates, etc.
            foreach (var kv in n.Attributes)
            {
                if (string.IsNullOrEmpty(kv.Value)) continue;
                if (ContainsVarRef(kv.Value, varName)) return true;
            }
            // Var.Get reads a var via VariableName attribute.
            if (n.Title is "Var.Get"
                && n.Attributes.TryGetValue("VariableName", out var vn)
                && string.Equals(vn?.Trim(), varName, StringComparison.OrdinalIgnoreCase))
                return true;
            // Public.Get reads via KeyName attribute → public.<KeyName>.
            if (n.Title is "Public.Get"
                && n.Attributes.TryGetValue("KeyName", out var kn)
                && !string.IsNullOrWhiteSpace(kn)
                && string.Equals("public." + kn.Trim(), varName, StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        private static bool ContainsVarRef(string text, string varName)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (Match m in VarRefRegex.Matches(text))
            {
                string key = m.Groups[1].Value;
                if (string.Equals(key, varName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Helper for the Architect Trace-Variable popup: returns a flat
        /// list of every var-name token referenced anywhere in the graph
        /// (writes + reads). Useful for a "pick a variable to trace"
        /// dropdown when no pill is under the cursor.
        /// </summary>
        public static IReadOnlyList<string> EnumerateAllVars(Graph graph)
        {
            if (graph == null) return Array.Empty<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in graph.Nodes)
            {
                if (n == null) continue; // guard null node before deref
                if (n.Attributes == null) continue; // Attributes can be null after JSON deserialization — guard before every foreach/TryGetValue deref in this loop body
                // Read side — every {var} reference in any attribute.
                foreach (var kv in n.Attributes)
                {
                    if (string.IsNullOrEmpty(kv.Value)) continue;
                    foreach (Match m in VarRefRegex.Matches(kv.Value))
                        seen.Add(m.Groups[1].Value);
                }
                // Write side — bound names from per-node attribute conventions.
                if (n.Title is "Var.Get" or "Var.Set" or "Var.Inc" or "Var.Toggle"
                    && n.Attributes.TryGetValue("VariableName", out var vn)
                    && !string.IsNullOrWhiteSpace(vn))
                    seen.Add(vn.Trim());
                if (n.Title is "Public.Get" or "Public.Set"
                    && n.Attributes.TryGetValue("KeyName", out var kn)
                    && !string.IsNullOrWhiteSpace(kn))
                    seen.Add("public." + kn.Trim());

                // Curated result-emitter names — union in for every
                // event / source node actually present in the graph so the
                // Trace-Variable picker surfaces user.message / user.name /
                // result.http_status / loop.index / etc. without forcing the
                // user to type the name first. Conservative on purpose: we
                // only contribute when the emitter node is in the graph
                // (so we don't pollute a no-Twitch graph with user.* keys).
                if (!string.IsNullOrEmpty(n.Title)
                    && ResultEmitterMap.TryGetValue(n.Title, out var emitted))
                {
                    foreach (var name in emitted) seen.Add(name);
                }

                // DB.* / result-attr writers (FetchRow, ResultKey, *ResultVar,
                // NewRowId, Row, Key on DB.SetVariable/DB.Increment, Public.Set)
                // surface their bound names too. Mirrors the IsWriter attr scan
                // — pre-fix these only appeared when something downstream
                // already referenced the bound name in {...} form.
                if (n.Title is "DB.SetVariable" or "DB.Increment"
                    && n.Attributes.TryGetValue("Key", out var dbk)
                    && !string.IsNullOrWhiteSpace(dbk))
                {
                    seen.Add(dbk.Trim().Trim('"'));
                }
                foreach (var kv in n.Attributes)
                {
                    string k = kv.Key;
                    bool isResultAttr =
                        string.Equals(k, "ResultKey", StringComparison.Ordinal)
                        || string.Equals(k, "ResultVar", StringComparison.Ordinal)
                        || string.Equals(k, "Row",       StringComparison.Ordinal)
                        || string.Equals(k, "NewRowId",  StringComparison.Ordinal)
                        || k.EndsWith("ResultKey", StringComparison.Ordinal)
                        || k.EndsWith("ResultVar", StringComparison.Ordinal);
                    if (!isResultAttr) continue;
                    if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                    seen.Add(kv.Value.Trim().Trim('"'));
                }
            }
            return seen.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
