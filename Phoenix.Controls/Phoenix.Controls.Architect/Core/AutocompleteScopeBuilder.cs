using System;
using System.Collections.Generic;
using System.Linq;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Architect.Core
{
    // Walks upstream flow ancestors from a given node and returns the
    // {var}-namespace tokens that are bound at the node's execution point —
    // event.* / event.arg.* / event.ret.* / loop.* / result.* / user.* / role.* /
    // stream.* / follow.*.
    //
    // Used by the inline-attr autocomplete popup in Canvas.Mouse.cs to
    // surface scoped suggestions in addition to the unscoped namespace roots
    // (`global.` / `user.` / `state.`) and the system.* catalogue.
    //
    // Scoping mirrors the runtime contract that ScriptManager.<Domain>.cs
    // commands set via SetLocalResultVar / SetScriptVarAsync after the node
    // executes. The walk is tolerant: an unfamiliar node simply contributes
    // nothing, so adding new commands does not regress the popup until the
    // contributor table here is updated.
    //
    // DB.FetchRow per-fetch row scope (): if the user filled the
    // optional KnownColumns hint on the FetchRow node, we surface the
    // <Row>.<col> tokens for each entry. Empty KnownColumns leaves only
    // the bare <Row> token. The visible-socket-split UI variant of the
    // same idea is the deferred follow-up TODO line.
    public static class AutocompleteScopeBuilder
    {
        /// <summary>
        /// Returns the upstream-bound variable tokens visible from
        /// <paramref name="currentNode"/>'s execution point. Empty when the
        /// node has no flow ancestors that bind anything.
        /// </summary>
        public static List<string> Build(Graph graph, Node currentNode)
        {
            if (graph == null || currentNode == null) return new List<string>();

            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string> { currentNode.Id };
            var queue = new Queue<Node>();
            queue.Enqueue(currentNode);

            // Build a ToSocketId → links index once
            // up front. Pre-fix the upstream BFS re-scanned the ENTIRE
            // graph.Links list for every flow-input socket of every dequeued
            // node — O(visited × flowInputs × links). The index drops the inner
            // scan to an O(1) dictionary hit, making the whole walk O(links +
            // nodes). Built per call (no cross-call cache, so no invalidation
            // contract to keep correct).
            var linksByToSocket = new Dictionary<string, List<Link>>(graph.Links.Count, StringComparer.Ordinal);
            foreach (var link in graph.Links)
            {
                if (string.IsNullOrEmpty(link.ToSocketId)) continue;
                if (!linksByToSocket.TryGetValue(link.ToSocketId, out var list))
                    linksByToSocket[link.ToSocketId] = list = new List<Link>(2);
                list.Add(link);
            }

            while (queue.Count > 0)
            {
                var n = queue.Dequeue();

                // Don't read tokens off the node-being-edited itself. A node
                // never consumes its own outputs, so contributing here would
                // pollute the popup with bindings that don't yet exist.
                if (!ReferenceEquals(n, currentNode))
                    Contribute(n, tokens);

                if (n.Sockets is null) continue;
                foreach (var inSocket in n.Sockets)
                {
                    if (inSocket.Type != SocketType.Input) continue;
                    if (!SocketTypeHelper.IsFlowPin(inSocket)) continue;

                    if (!linksByToSocket.TryGetValue(inSocket.Id, out var incident)) continue;
                    foreach (var link in incident)
                    {
                        var prev = graph.FindNodeById(link.FromNodeId);
                        if (prev == null) continue;
                        if (!visited.Add(prev.Id)) continue;
                        queue.Enqueue(prev);
                    }
                }
            }

            return tokens.ToList();
        }

        private static void Contribute(Node n, HashSet<string> tokens)
        {
            switch (n.Title)
            {
                // ── Twitch event triggers — bound user.* set varies per event shape.
                case "Twitch.ChatMessage":
                    AddRange(tokens,
                        "user.message", "user.name", "user.command", "user.args",
                        "user.is_mod", "user.is_sub", "user.is_vip", "user.is_broadcaster",
                        "user.is_regular",
                        "user.color_hex", "user.sub_months",
                        "event.iscommand");
                    return;
                case "Twitch.Subscription":
                    AddRange(tokens, "user.name", "user.sub_months", "user.tier");
                    return;
                case "Twitch.Resub":
                    AddRange(tokens, "user.name", "user.sub_months", "user.message");
                    return;
                case "Twitch.GiftSub":
                    AddRange(tokens, "user.gifter", "user.recipient", "user.tier");
                    return;
                case "Twitch.GiftBomb":
                    AddRange(tokens, "user.gifter", "user.count");
                    return;
                case "Twitch.Raid":
                    AddRange(tokens, "user.name", "user.viewers");
                    return;
                case "Twitch.Cheer":
                    AddRange(tokens, "user.name", "user.bits", "user.message");
                    return;
                case "Twitch.Follow":
                    tokens.Add("user.name");
                    return;
                case "Twitch.PointRedeem":
                    AddRange(tokens, "user.name", "user.reward", "user.input");
                    return;
                case "Twitch.InWhisper":
                    AddRange(tokens, "user.name", "user.message", "user.id");
                    return;
                case "YouTube.Message":
                    AddRange(tokens, "user.name", "user.message");
                    return;
                case "Chat.Message":
                    // Unified multi-platform chat trigger — Twitch.ChatMessage's
                    // set plus the platform discriminator. The legacy titles
                    // "Twitch.ChatMessage" / "YouTube.Message" above stay handled
                    // for graphs not yet re-saved through migration: they match no
                    // NodeRegistry template and therefore LOOK like phantom arms,
                    // but GraphSerializer.MigrateNodes only retitles them to
                    // Chat.Message on load, so an in-memory node still carries the
                    // legacy title before migration runs, and the exporter still
                    // honours both. DO NOT delete them —
                    // AnalyzerNodeKeyIntegrityTests allow-lists exactly these two.
                    AddRange(tokens,
                        "user.message", "user.name", "user.command", "user.args",
                        "user.is_mod", "user.is_sub", "user.is_vip", "user.is_broadcaster",
                        "user.is_regular",
                        "user.color_hex", "user.sub_months",
                        "event.iscommand", "user.platform", "event.message_id");
                    return;
                // Unified stream-lifecycle triggers — the platform that fired plus
                // the best-effort stream Title / Category (Hub dispatch binds
                // user.platform / event.title / event.category).
                case "Stream.GoingLive":
                case "Stream.SessionEnd":
                    AddRange(tokens, "user.platform", "event.title", "event.category");
                    return;

                // ── Other event sources.
                case "Bus.OnMessage":
                    // Synced with VarChainAnalyzer.ResultEmitterMap["Bus.OnMessage"]
                    // and the Hub runtime contract (Bus.cs binds bus.type /
                    // bus.source / bus.target / bus.payload into the vars dict).
                    AddRange(tokens, "bus.type", "bus.payload", "bus.source", "bus.target");
                    return;
                case "HTTP.WebhookListener":
                    AddRange(tokens, "event.payload", "event.body", "event.method", "event.path");
                    return;
                case "WS.Server":
                    // Synced with VarChainAnalyzer.ResultEmitterMap["WS.Server"].
                    // ScriptManager.ExecuteOnWebSocketScriptsAsync binds
                    // event.body / event.payload / event.path — no event.method,
                    // because a WS frame carries no HTTP verb. Pre-fix this arm
                    // was missing entirely, so the popup offered nothing on a
                    // WS.Server-rooted chain even though the trace picker did.
                    AddRange(tokens, "event.payload", "event.body", "event.path");
                    return;
                case "Schedule.Cron":
                case "Schedule.RunAt":
                    tokens.Add("event.timestamp");
                    return;
                case "Schedule.Recurring":
                    // Synced with VarChainAnalyzer.ResultEmitterMap["Schedule.Recurring"]
                    // — the recurring variant also emits the per-tick counter.
                    AddRange(tokens, "event.timestamp", "event.count");
                    return;
                case "State.OnChange":
                    AddRange(tokens, "event.name", "event.oldvalue", "event.newvalue");
                    return;
                case "Counter.OnChanged":
                    // Synced with the CountersService runtime vars (event.counter /
                    // event.count) and the Counter.OnChanged arm in
                    // ScriptExporter.ResolveOutputFromNode.
                    AddRange(tokens, "event.counter", "event.count");
                    return;
                case "Automod.OnViolation":
                    // Synced with the AutomodService runtime vars and the
                    // Automod.OnViolation arm in ScriptExporter.ResolveOutputFromNode.
                    AddRange(tokens, "event.user", "event.rule", "event.action", "event.reason", "event.message");
                    return;
                case "Quote.OnAdded":
                    // Synced with the QuotesService runtime vars (event.number /
                    // event.text / event.name) and the Quote.OnAdded arm in
                    // ScriptExporter.ResolveOutputFromNode.
                    AddRange(tokens, "event.number", "event.text", "event.name");
                    return;
                case "Command.OnCustom":
                    // Synced with the CustomCommandsService runtime vars (event.command /
                    // event.user / event.args) and the Command.OnCustom arm in
                    // ScriptExporter.ResolveOutputFromNode.
                    AddRange(tokens, "event.command", "event.user", "event.args");
                    return;
                case "Queue.OnChanged":
                    // Synced with the NamedQueueService runtime vars (event.queue /
                    // event.entry / event.action / event.length) and the Queue.OnChanged
                    // arm in ScriptExporter.ResolveOutputFromNode.
                    AddRange(tokens, "event.queue", "event.entry", "event.action", "event.length");
                    return;

                // ── Song Request event roots. Synced with the SongRequestService raise
                // sites (RaiseSongEvent binds three base tokens plus one extra per root)
                // and the shared Song.On* arm in ScriptExporter.ResolveOutputFromNode.
                // Note the snake_case spellings — event.video_id / event.duration_seconds
                // / event.skipped_by — which are the RUNTIME's, and deliberately not what
                // the exporter's generic tail would have produced from the socket names.
                case "Song.OnQueued":
                    AddRange(tokens, "event.title", "event.requester", "event.video_id", "event.position");
                    return;
                case "Song.OnPlay":
                    AddRange(tokens, "event.title", "event.requester", "event.video_id", "event.duration_seconds");
                    return;
                case "Song.OnSkip":
                    AddRange(tokens, "event.title", "event.requester", "event.video_id", "event.skipped_by");
                    return;

                // ── Polls & Betting event roots. Synced with the PollsService raise sites
                // (RaiseOpened / RaiseClosed / RaiseSettled) and the shared Poll.On* arm in
                // ScriptExporter.ResolveOutputFromNode. Note the snake_case spellings —
                // event.total_votes / event.winner_votes / event.winner_count /
                // event.duration_seconds — which are the RUNTIME's, and deliberately not
                // what the exporter's generic tail would have produced from the socket
                // names. event.option_count has no output socket (the Options string
                // carries the labels) but is a live var on the run, so the picker lists it
                // — the same rule the Timer roots follow.
                case "Poll.OnOpened":
                    AddRange(tokens, "event.title", "event.options", "event.option_count",
                        "event.duration_seconds", "event.betting");
                    return;
                case "Poll.OnClosed":
                    AddRange(tokens, "event.title", "event.winner", "event.winner_votes",
                        "event.total_votes", "event.options");
                    return;
                case "Poll.OnSettled":
                    AddRange(tokens, "event.title", "event.winner", "event.outcome", "event.pot",
                        "event.winners", "event.winner_count", "event.currency");
                    return;

                // ── Ranks ladder event root. Synced with the RanksService raise site
                // (RaiseRankUp) and the Rank.OnRankUp arm in
                // ScriptExporter.ResolveOutputFromNode. event.login DOES have an output
                // socket (Login) — the login is what the ladder, the watch-minute store and
                // the group grants are all keyed on, so it is wireable rather than hidden.
                // event.user_login is the same login under the suite-wide spelling every
                // Rank.* node's empty-User fallback resolves through, and event.unit is the
                // word a message wanting to say "minutes" needs; neither has a socket, and
                // the picker lists them for the same reason the Timer and User-Management
                // roots list theirs.
                case "Rank.OnRankUp":
                    AddRange(tokens, "event.user", "event.login", "event.user_login",
                        "event.rankname", "event.value", "event.unit", "event.next");
                    return;

                // ── Soundboard clip-playback event root. Synced with the SoundboardService
                // raise site (RaisePlayed) and the Soundboard.OnPlay arm in
                // ScriptExporter.ResolveOutputFromNode. user.name has no output socket but
                // is bound by the raise, because a graph moved here off an on_chat handler
                // (which is what this root exists for — the built-in consumes the word and
                // suppresses the author fan-out) is already reaching for that token.
                case "Soundboard.OnPlay":
                    AddRange(tokens, "event.command", "event.user", "event.clip", "user.name");
                    return;

                case "User.OnFirstMessage":
                    // Synced with the UserManagementService raise (event.user / event.login /
                    // event.message / event.platform / event.first_ever) and the
                    // User.OnFirstMessage arm in ScriptExporter.ResolveOutputFromNode.
                    // event.login has no output socket — the node exposes the display name,
                    // which is what a greeting prints — but the raise binds it because a
                    // group / databank lookup needs the stable login, so the picker lists it.
                    AddRange(tokens, "event.user", "event.login", "event.message",
                        "event.platform", "event.first_ever");
                    return;

                // ── Timer event roots. Sourced from TimerService's Fire*Async
                // raise sites, which bind the socket-derived event.* keys plus the
                // slug / remaining extras and the raw timer.* aliases — every one
                // of them is a live var on the run, so surface them all.
                case "Timer.OnZero":
                    AddRange(tokens, "event.timername", "event.slug", "timer.name", "timer.slug");
                    return;
                case "Timer.OnMilestone":
                    AddRange(tokens, "event.timername", "event.milestoneid", "event.label", "event.slug",
                        "timer.name", "timer.slug", "timer.milestone_id", "timer.label");
                    return;
                case "Timer.OnAdd":
                    AddRange(tokens, "event.timername", "event.source", "event.seconds", "event.slug", "event.remaining",
                        "timer.name", "timer.slug", "timer.source", "timer.seconds", "timer.remaining");
                    return;

                // ── Loyalty event roots. Sourced from LoyaltyService's RaiseScript
                // sites (Earn.cs OnEarn/OnPayout, LoyaltyService.cs OnRedeem,
                // Games.cs OnRaffle) and matching the Loyalty arm in
                // ScriptExporter.ResolveOutputFromNode. The un-namespaced
                // reward / cost / balance aliases the redeem raise also sets are
                // deliberately not surfaced — the event.* form is the documented one.
                case "Loyalty.OnEarn":
                    AddRange(tokens, "event.user", "event.amount", "event.reason", "event.balance",
                        "event.currency", "user.name");
                    return;
                case "Loyalty.OnPayout":
                    AddRange(tokens, "event.count", "event.total", "event.amount", "event.currency");
                    return;
                case "Loyalty.OnRedeem":
                    AddRange(tokens, "event.user", "event.reward", "event.cost", "event.balance",
                        "event.currency", "user.name");
                    return;
                case "Loyalty.OnRaffle":
                    AddRange(tokens, "event.winners", "event.count", "event.pot", "event.entrants", "event.currency");
                    return;

                // ── Event.Trigger / Event.Executor expose their non-placeholder
                // output sockets via event.ret.<name> and event.arg.<name>.
                case "Event.Trigger":
                    if (n.Sockets is null) return;
                    foreach (var s in n.Sockets)
                    {
                        if (s.Type != SocketType.Output) continue;
                        if (s.IsPlaceholder) continue;
                        if (string.Equals(s.Name, "Flow", StringComparison.OrdinalIgnoreCase)) continue;
                        tokens.Add($"event.ret.{s.Name}");
                    }
                    return;
                case "Event.Executor":
                    if (n.Sockets is null) return;
                    foreach (var s in n.Sockets)
                    {
                        if (s.Type != SocketType.Output) continue;
                        if (s.IsPlaceholder) continue;
                        if (string.Equals(s.Name, "Flow", StringComparison.OrdinalIgnoreCase)) continue;
                        tokens.Add($"event.arg.{s.Name}");
                    }
                    return;

                // ── Loop iterators. ScriptExporter emits both the legacy alias
                // (`loop.index` / `loop.item`) and the per-id form to disambiguate
                // nested ForLoops; surface both so users can pick either.
                case "Flow.ForLoop":
                    tokens.Add("loop.index");
                    if (!string.IsNullOrEmpty(n.Id) && n.Id.Length >= 6)
                        tokens.Add($"loop.index_{n.Id[..6]}");
                    return;
                case "Flow.ForEach":
                    tokens.Add("loop.item");
                    return;

                // ── Twitch lookup nodes — fixed result-key sets.
                case "Twitch.GetUser":
                case "StreamerBot.GetUser":
                    // Both nodes land on ScriptManager.Twitch.cs's ApplyUserGlobals
                    // (streamerbot.get_user is an exact by-name mirror of
                    // twitch.get_user), so they bind an identical user.* set.
                    // StreamerBot.GetUser was absent from this table entirely.
                    AddRange(tokens, "user.id", "user.display_name", "user.login", "user.profile_image",
                        "user.account_created", "user.game", "user.channel_title",
                        "user.is_mod", "user.is_sub", "user.is_vip", "user.is_regular");
                    return;
                case "Twitch.GetStream":
                    AddRange(tokens, "stream.title", "stream.game", "stream.viewers", "stream.is_live", "stream.uptime");
                    return;
                case "Twitch.CheckRole":
                    AddRange(tokens, "role.is_mod", "role.is_sub", "role.is_vip", "role.is_broadcaster", "role.is_regular");
                    return;
                case "User.GetGroups":
                    // Standard group keys; custom groups additionally surface as
                    // group.<sanitized> from the node's Groups attribute below.
                    AddRange(tokens, "group.moderator", "group.vip", "group.subscriber", "group.regular");
                    if (n.Attributes.TryGetValue("Groups", out var grpCsv) && !string.IsNullOrWhiteSpace(grpCsv))
                    {
                        foreach (var g in grpCsv.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries))
                        {
                            var key = Phoenix.Controls.Shared.Models.UserGroupKeys.VarKeyFor(g);
                            if (key.Length > "group.".Length) tokens.Add(key);
                        }
                    }
                    return;
                case "Twitch.GetFollowAge":
                    AddRange(tokens, "follow.days", "follow.formatted", "follow.date", "follow.is_following");
                    return;
                case "Twitch.CreateClip":
                    AddRange(tokens, "clip.url", "clip.ok");
                    return;

                // ── Hub-side result.* emitters with fixed keys.
                // The registered File.* templates are the ReadText / ReadJSON /
                // WriteText / WriteJSON quartet. The bare "File.Read" /
                // "File.ReadAll" / "File.Write" / "File.Append" labels that used
                // to sit here matched no NodeRegistry template, so they were
                // unreachable arms — dropped. AnalyzerNodeKeyIntegrityTests now
                // fails the build if a phantom title creeps back in.
                case "File.ReadText":
                case "File.ReadJSON":
                    AddRange(tokens, "result.file_content", "result.file_error");
                    return;
                case "File.WriteText":
                case "File.WriteJSON":
                    tokens.Add("result.file_error");
                    return;
                case "HTTP.Get":
                case "HTTP.Post":
                case "HTTP.Put":
                case "HTTP.Patch":
                case "HTTP.Delete":
                    AddRange(tokens, "result.http_status", "result.http_body", "result.http_error");
                    return;
                case "API.Call":
                    // Registered title is "API.Call" (Platforms band); the old
                    // "HTTP.Api" label matched no template, so the api.call
                    // node's two result vars never reached the popup.
                    // ScriptManager.Http.cs writes both.
                    AddRange(tokens, "result.api_response", "result.api_error");
                    return;
                case "HTTP.ParseJson":
                    tokens.Add("result.json_value");
                    return;
                // "AI.GenerateText" removed — no such template; ai.prompt's node
                // is titled AI.Prompt and already covered below.
                case "AI.Prompt":
                    AddRange(tokens, "result.ai_response", "result.ai_error");
                    return;
                case "AI.Moderate":
                    AddRange(tokens, "result.ai_flagged", "result.ai_category", "result.ai_error");
                    return;
                case "AI.StreamText":
                    // Streaming variant. result.ai_response holds
                    // the cumulative text (updated per chunk); result.ai_done
                    // flips on stream close; result.ai_error carries any
                    // error message. Same surface as AI.Prompt + the
                    // streaming-completion sentinel, plus the two failure
                    // classifiers ScriptManager.AI.cs's ai.stream_text handler
                    // writes (result.ai_error_kind / result.ai_retry_after) —
                    // both were listed in VarChainAnalyzer but missing here.
                    AddRange(tokens, "result.ai_response", "result.ai_error", "result.ai_done",
                        "result.ai_error_kind", "result.ai_retry_after");
                    return;
                case "AI.GenerateImage":
                    // Single-shot image generation. result.ai_image_url
                    // carries the generated URL on success; result.ai_image_error
                    // any failure detail; result.ai_image_done flips when the
                    // call completes (success or failure) so scripts that
                    // polled mid-call see a clean edge.
                    AddRange(tokens, "result.ai_image_url", "result.ai_image_error", "result.ai_image_done");
                    return;
                case "AI.VisionDescribe":
                    // Single-shot vision Q&A. Reuses ai.prompt's
                    // result vars: result.ai_response carries the answer;
                    // result.ai_error any failure detail.
                    AddRange(tokens, "result.ai_response", "result.ai_error");
                    return;
                case "AI.WithTools":
                    // Single-shot tool-calling. Mutually
                    // exclusive on a given call:
                    //   * result.ai_response   — plain text answer.
                    //   * result.ai_tool_calls — JSON array string of
                    //                            {id,name,arguments}.
                    // Plus result.ai_error / result.ai_done.
                    AddRange(tokens, "result.ai_response", "result.ai_tool_calls", "result.ai_error", "result.ai_done");
                    return;
                case "Audio.Play":
                case "Audio.PlayTts":
                case "Audio.SetVolume":
                    // "Audio.Stop" used to sit here and matched no template.
                    // Audio.PlayTts is the real, fully-wired third audio node
                    // (exporter handler + ScriptManager.Audio.cs command) and
                    // writes the same result.audio_error contract.
                    tokens.Add("result.audio_error");
                    return;
                case "Discord.SendMessage":
                case "Discord.SendEmbed":
                    AddRange(tokens, "result.discord_message_id", "result.discord_error");
                    return;
                case "Discord.AddRole":
                case "Discord.RemoveRole":
                case "Discord.React":
                    tokens.Add("result.discord_error");
                    return;
                case "Discord.GetUser":
                    AddRange(tokens,
                        "result.discord_user_id", "result.discord_user_name",
                        "result.discord_user_global_name", "result.discord_user_avatar",
                        "result.discord_error");
                    return;
                // LIVE BUG, not dead weight: the registered template is
                // "StreamerBot.DoAction" with a CAPITAL B. This arm keyed the
                // lowercase-b spelling and the switch compares ordinally, so it
                // never matched a real node — meaning result.sb_dispatched has
                // never been offered in autocomplete for the one node that
                // produces it, even though NodeProse documents the variable to
                // users ("{result.sb_dispatched} reports whether it went out").
                // The sibling "System.DoAction" label matched no template at all
                // and is dropped.
                case "StreamerBot.DoAction":
                    tokens.Add("result.sb_dispatched");
                    return;
            }

            // ── Catalog-driven platform events (YouTube/Kick) — single source
            // in PlatformEventCatalog. Runs after the switch so the explicit
            // cases above (incl. legacy YouTube.Message) are never shadowed.
            // The Hub runtime injects user.platform + event.payload for every
            // catalog event on top of the per-socket tokens.
            var platformEvent = PlatformEventCatalog.Find(n.Title);
            if (platformEvent != null)
            {
                foreach (var s in platformEvent.Sockets)
                    tokens.Add(s.VarToken);
                tokens.Add("user.platform");
                tokens.Add("event.payload");
                return;
            }

            // Everything past the switch reads n.Attributes. The Node model
            // permits a null Attributes dict, so guard before the FetchRow
            // special-case and the generic ResultKey/ResultVar scan to avoid
            // a NullReferenceException freezing the autocomplete popup (the
            // switch above only touches Title/Sockets/Id, so it's unaffected).
            if (n.Attributes == null) return;

            // ── DB.FetchRow — special-case the per-fetch row scope. The Hub
            // handler binds {<Row>} to "found"/"" and {<Row>}.<col> to each
            // column; the column list isn't known until runtime, so the
            // user fills in a comma-separated hint via the optional
            // KnownColumns attribute and we surface those as
            // <Row>.<col> tokens in the autocomplete popup. The Row attr
            // itself defaults to a node-id-derived global var (per
            // DbFetchRowHandler), so a default-attributed FetchRow still
            // contributes its base var via the generic scan below.
            if (n.Title == "DB.FetchRow")
            {
                string rowVar;
                if (n.Attributes.TryGetValue("Row", out var rv) && !string.IsNullOrWhiteSpace(rv))
                {
                    rowVar = rv.Trim().Trim('"');
                }
                else
                {
                    // Cache the hyphen-stripped id once — the slice and the
                    // Math.Min bound both need it, and Replace allocates a new
                    // string each call.
                    var idNoHyphens = (n.Id ?? "").Replace("-", "");
                    rowVar = $"global._row_{idNoHyphens[..Math.Min(6, idNoHyphens.Length)]}";
                }
                if (!string.IsNullOrEmpty(rowVar))
                    tokens.Add(rowVar);

                if (n.Attributes.TryGetValue("KnownColumns", out var cols) && !string.IsNullOrWhiteSpace(cols))
                {
                    foreach (var raw in cols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        // Tolerate quotes the user might have pasted in.
                        string col = raw.Trim('"');
                        if (string.IsNullOrEmpty(col)) continue;
                        tokens.Add($"{rowVar}.{col}");
                    }
                }
                return;
            }

            // ── Generic ResultKey / ResultVar attribute scan. Catches the
            // remaining DB.* commands (they all bind their result by the
            // ResultKey arg), Twitch.LastActive's InactiveResultVar /
            // MinutesAgoResultVar, Twitch.GetViewers' ResultVar, and any
            // future node that follows the same attribute-naming convention.
            foreach (var kv in n.Attributes)
            {
                string key = kv.Key;
                bool isResultAttr =
                    string.Equals(key, "ResultKey", StringComparison.Ordinal)
                    || string.Equals(key, "ResultVar", StringComparison.Ordinal)
                    || key.EndsWith("ResultKey", StringComparison.Ordinal)
                    || key.EndsWith("ResultVar", StringComparison.Ordinal);
                if (!isResultAttr) continue;
                if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                string token = kv.Value.Trim().Trim('"');
                if (!string.IsNullOrEmpty(token)) tokens.Add(token);
            }
        }

        private static void AddRange(HashSet<string> tokens, params string[] toks)
        {
            foreach (var t in toks) tokens.Add(t);
        }
    }
}
