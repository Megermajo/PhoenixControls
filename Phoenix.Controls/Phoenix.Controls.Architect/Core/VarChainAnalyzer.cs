using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
        private static readonly IReadOnlyDictionary<string, string[]> ResultEmitterMap =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                // Twitch event sources.
                ["Twitch.ChatMessage"]      = new[] { "user.message", "user.name", "user.command", "user.args", "user.is_mod", "user.is_sub", "user.is_vip", "user.is_broadcaster", "user.color_hex", "user.sub_months", "event.iscommand" },
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
                ["YouTube.Message"]         = new[] { "user.name", "user.message" },
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
                // Twitch read-side queries.
                ["Twitch.GetUser"]          = new[] { "user.id", "user.display_name", "user.login", "user.profile_image", "user.account_created", "user.game", "user.channel_title", "user.is_mod", "user.is_sub", "user.is_vip" },
                ["Twitch.GetStream"]        = new[] { "stream.title", "stream.game", "stream.viewers", "stream.is_live", "stream.uptime" },
                ["Twitch.CheckRole"]        = new[] { "role.is_mod", "role.is_sub", "role.is_vip", "role.is_broadcaster" },
                ["Twitch.GetFollowAge"]     = new[] { "follow.days", "follow.formatted", "follow.date", "follow.is_following" },
                ["Twitch.CreateClip"]       = new[] { "clip.url", "clip.ok" },
                ["StreamerBot.GetUser"]     = new[] { "user.id", "user.display_name", "user.login", "user.profile_image" },
                // File / HTTP.
                ["File.Read"]      = new[] { "result.file_content", "result.file_error" },
                ["File.ReadAll"]   = new[] { "result.file_content", "result.file_error" },
                ["File.ReadText"]  = new[] { "result.file_content", "result.file_error" },
                ["File.ReadJSON"]  = new[] { "result.file_content", "result.file_error" },
                ["File.Write"]     = new[] { "result.file_error" },
                ["File.Append"]    = new[] { "result.file_error" },
                ["File.WriteText"] = new[] { "result.file_error" },
                ["File.WriteJSON"] = new[] { "result.file_error" },
                ["HTTP.Get"]       = new[] { "result.http_status", "result.http_body", "result.http_error" },
                ["HTTP.Post"]      = new[] { "result.http_status", "result.http_body", "result.http_error" },
                ["HTTP.Put"]       = new[] { "result.http_status", "result.http_body", "result.http_error" },
                ["HTTP.Patch"]     = new[] { "result.http_status", "result.http_body", "result.http_error" },
                ["HTTP.Delete"]    = new[] { "result.http_status", "result.http_body", "result.http_error" },
                ["HTTP.Api"]       = new[] { "result.api_response", "result.api_error" },
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
                ["AI.GenerateImage"]   = new[] { "result.ai_image_url", "result.ai_image_error" },
                ["Audio.Play"]      = new[] { "result.audio_error" },
                ["Audio.Stop"]      = new[] { "result.audio_error" },
                ["Audio.SetVolume"] = new[] { "result.audio_error" },
                // Discord.
                ["Discord.SendMessage"] = new[] { "result.discord_message_id", "result.discord_error" },
                ["Discord.SendEmbed"]   = new[] { "result.discord_message_id", "result.discord_error" },
                ["Discord.AddRole"]     = new[] { "result.discord_error" },
                ["Discord.RemoveRole"]  = new[] { "result.discord_error" },
                ["Discord.React"]       = new[] { "result.discord_error" },
                ["Discord.GetUser"]     = new[] { "result.discord_user_id", "result.discord_user_name", "result.discord_user_global_name", "result.discord_user_avatar", "result.discord_error" },
                // Streamer.bot / system dispatch.
                ["Streamerbot.DoAction"] = new[] { "result.sb_dispatched" },
                ["System.DoAction"]      = new[] { "result.sb_dispatched" },
                // Loop bindings — per-loop-id form mirrors the engine's
                // Flow.ForLoop / Flow.ForEach substitution.
                ["Flow.ForLoop"] = new[] { "loop.index" },
                ["Flow.ForEach"] = new[] { "loop.item" },
            };

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
