using System;
using System.Collections.Generic;
using System.Linq;
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
                case "YouTube.Message":
                    AddRange(tokens, "user.name", "user.message");
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
                    AddRange(tokens, "user.id", "user.display_name", "user.login", "user.profile_image",
                        "user.account_created", "user.game", "user.channel_title",
                        "user.is_mod", "user.is_sub", "user.is_vip");
                    return;
                case "Twitch.GetStream":
                    AddRange(tokens, "stream.title", "stream.game", "stream.viewers", "stream.is_live", "stream.uptime");
                    return;
                case "Twitch.CheckRole":
                    AddRange(tokens, "role.is_mod", "role.is_sub", "role.is_vip", "role.is_broadcaster");
                    return;
                case "Twitch.GetFollowAge":
                    AddRange(tokens, "follow.days", "follow.formatted", "follow.date", "follow.is_following");
                    return;
                case "Twitch.CreateClip":
                    AddRange(tokens, "clip.url", "clip.ok");
                    return;

                // ── Hub-side result.* emitters with fixed keys.
                case "File.Read":
                case "File.ReadAll":
                case "File.ReadText":
                case "File.ReadJSON":
                    AddRange(tokens, "result.file_content", "result.file_error");
                    return;
                case "File.Write":
                case "File.Append":
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
                case "HTTP.Api":
                    AddRange(tokens, "result.api_response", "result.api_error");
                    return;
                case "HTTP.ParseJson":
                    tokens.Add("result.json_value");
                    return;
                case "AI.GenerateText":
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
                    // streaming-completion sentinel.
                    AddRange(tokens, "result.ai_response", "result.ai_error", "result.ai_done");
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
                case "Audio.Stop":
                case "Audio.SetVolume":
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
                case "Streamerbot.DoAction":
                case "System.DoAction":
                    tokens.Add("result.sb_dispatched");
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
