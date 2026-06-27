using System.Collections.Generic;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// One <c>{token}</c> a Script can substitute into a string
    /// argument. <see cref="Scope"/> groups tokens for the in-Hub doc tree;
    /// <see cref="BoundBy"/> names the event handler / node / engine surface
    /// that puts the value into scope.
    /// </summary>
    public record ScriptToken(
        string Token,
        string Scope,
        string Description,
        string BoundBy);

    /// <summary>
    /// Catalogue of every documented <c>{token}</c> a Script can
    /// reference inline (Architect node attributes, Text.Format templates,
    /// chat replies, etc.). Surfaced by HubDocumentationForm's "Tokens" tab.
    ///
    /// Sources of truth (kept in sync by hand — re-scan when a node ships):
    ///   * Per-event <c>user.*</c> / <c>event.*</c> bindings — see
    ///     <c>AutocompleteScopeBuilder.Contribute</c> in Architect (which
    ///     mirrors the bindings ScriptManager applies at handler entry).
    ///   * <c>result.*</c> outputs — same file, per-node case branches.
    ///   * <c>system.*</c> — <c>ScriptEngine.Variables.ResolveSystemVar</c>.
    ///   * <c>stream.*</c> — same file, <c>ResolveStreamVar</c>.
    ///   * <c>{var.*}</c> / <c>{public.*}</c> / <c>{global.*}</c> — namespace
    ///     conventions documented in the project docs.
    /// </summary>
    public static class ScriptTokenRegistry
    {
        private static readonly List<ScriptToken> _tokens = new();

        static ScriptTokenRegistry()
        {
            RegisterDefaults();
        }

        public static IReadOnlyList<ScriptToken> GetAll() => _tokens;

        private static void Add(string token, string scope, string description, string boundBy)
            => _tokens.Add(new ScriptToken(token, scope, description, boundBy));

        // Same scope strings drive the tree's category headers in
        // HubDocumentationForm. Keep them stable; reorder via the
        // OrderedScopes list at the bottom of this file.
        private const string ScopeUser   = "User (event-bound)";
        private const string ScopeEvent  = "Event metadata";
        private const string ScopeLoop   = "Loop iterators";
        private const string ScopeSystem = "System time";
        private const string ScopeStream = "Stream uptime";
        private const string ScopeLookup = "Twitch lookup outputs";
        private const string ScopeResult = "Node result outputs";
        private const string ScopeVars   = "Variable namespaces";

        private static void RegisterDefaults()
        {
            // ─────────────────────────────────────────────────────────────
            // USER (event-bound) — set by ScriptManager when the matching
            // event fires. Each entry's BoundBy lists every event type that
            // populates it. Engine pre-populates these into the script's
            // var dictionary at handler entry; reference inline as
            // {user.<key>}.
            // ─────────────────────────────────────────────────────────────
            Add("user.name", ScopeUser,
                "The chatter, raider, subscriber, follower, gifter, or redeemer's display name. Cleaned and ready to use in chat replies and overlays.",
                "Twitch.ChatMessage, Twitch.Subscription, Twitch.Resub, Twitch.Raid, Twitch.Cheer, Twitch.Follow, Twitch.PointRedeem, YouTube.Message");
            Add("user.message", ScopeUser,
                "The full text of the chat message that triggered the script (everything after the command if it was a !command).",
                "Twitch.ChatMessage, Twitch.Resub, Twitch.Cheer, YouTube.Message");
            Add("user.command", ScopeUser,
                "The matched command word with the leading ! stripped. Empty when the message was not a command.",
                "Twitch.ChatMessage");
            Add("user.args", ScopeUser,
                "Whitespace-split list of arguments after the command word. Iterate with Flow.ForEach or pull positional ones with Array.Get.",
                "Twitch.ChatMessage");
            Add("user.is_mod", ScopeUser,
                "True when the chatter has Twitch moderator status. String literal \"true\"/\"false\" — wrap with Convert.ToBool to use as a Logic.Branch condition.",
                "Twitch.ChatMessage");
            Add("user.is_sub", ScopeUser,
                "True when the chatter is currently subscribed to the channel.",
                "Twitch.ChatMessage");
            Add("user.is_vip", ScopeUser,
                "True when the chatter holds a VIP role on the channel.",
                "Twitch.ChatMessage");
            Add("user.is_broadcaster", ScopeUser,
                "True when the chatter is the broadcaster (channel owner).",
                "Twitch.ChatMessage");
            Add("user.color_hex", ScopeUser,
                "The chatter's name color as a hex string (e.g. \"#7fff7f\"). Empty when Twitch reports no custom color.",
                "Twitch.ChatMessage");
            Add("user.sub_months", ScopeUser,
                "Months the user has been subscribed (0 when not currently a sub). On Twitch.Subscription / Twitch.Resub this is the freshly-reported sub length.",
                "Twitch.ChatMessage, Twitch.Subscription, Twitch.Resub");
            Add("user.tier", ScopeUser,
                "Subscription tier as a string — \"1\", \"2\", \"3\", or \"prime\" (normalized from Twitch's raw 1000/2000/3000 encoding).",
                "Twitch.Subscription, Twitch.Resub, Twitch.GiftSub");
            Add("user.gifter", ScopeUser,
                "Display name of the user who gifted the sub(s).",
                "Twitch.GiftSub, Twitch.GiftBomb");
            Add("user.recipient", ScopeUser,
                "Display name of the user receiving the gifted sub.",
                "Twitch.GiftSub");
            Add("user.count", ScopeUser,
                "Number of subs in the gift bomb.",
                "Twitch.GiftBomb");
            Add("user.is_anonymous", ScopeUser,
                "\"true\" / \"false\" — whether the gift was given anonymously. When true the gifter name is a placeholder, so gate any \"thank the gifter\" message on this.",
                "Twitch.GiftSub, Twitch.GiftBomb");
            Add("user.viewers", ScopeUser,
                "How many viewers came along with the raid.",
                "Twitch.Raid");
            Add("user.bits", ScopeUser,
                "Bits cheered.",
                "Twitch.Cheer");
            Add("user.reward", ScopeUser,
                "Title of the channel-point reward that was redeemed.",
                "Twitch.PointRedeem");
            Add("user.input", ScopeUser,
                "Free-text input the redeemer typed into the reward (when the reward is configured to require text).",
                "Twitch.PointRedeem");

            // ─────────────────────────────────────────────────────────────
            // EVENT METADATA — generic per-event payload tokens (not user.*).
            // ─────────────────────────────────────────────────────────────
            Add("event.iscommand", ScopeEvent,
                "True if the chat message was a recognised !command (matched the node's Commands filter), false otherwise.",
                "Twitch.ChatMessage");
            Add("event.type", ScopeEvent,
                "Type tag of the inbound bus message (e.g. VISUAL_COMPLETE, HUB_EVENT). Filter at the node via the EventType attribute or branch on this in-script.",
                "Bus.OnMessage");
            Add("event.payload", ScopeEvent,
                "Body of the matched bus message (Bus.OnMessage) or HTTP webhook payload string (HTTP.WebhookListener). For JSON, feed through HTTP.ParseJson.",
                "Bus.OnMessage, HTTP.WebhookListener");
            Add("event.body", ScopeEvent,
                "Raw HTTP request body delivered to the webhook listener.",
                "HTTP.WebhookListener");
            Add("event.method", ScopeEvent,
                "HTTP method (GET / POST / PUT / DELETE / …) of the inbound webhook request.",
                "HTTP.WebhookListener");
            Add("event.path", ScopeEvent,
                "URL path the webhook arrived on, relative to the configured Name.",
                "HTTP.WebhookListener");
            Add("event.timestamp", ScopeEvent,
                "ISO-8601 timestamp of the schedule firing.",
                "Schedule.Cron, Schedule.RunAt, Schedule.Recurring");
            Add("event.name", ScopeEvent,
                "Name of the State.* key whose change triggered this script.",
                "State.OnChange");
            Add("event.oldvalue", ScopeEvent,
                "Previous value the state key held before the change.",
                "State.OnChange");
            Add("event.newvalue", ScopeEvent,
                "Value the state key was just set to.",
                "State.OnChange");
            Add("event.arg.<name>", ScopeEvent,
                "Per-named-input value when an Event.Executor block is invoked from an Event.Trigger. Replace <name> with the placeholder socket name on the Executor node.",
                "Event.Executor");
            Add("event.ret.<name>", ScopeEvent,
                "Return value from a fired Event.Trigger, populated when the corresponding Event.Executor's Event.Return supplies it. Replace <name> with the matching return socket name on the Trigger.",
                "Event.Trigger");

            // ─────────────────────────────────────────────────────────────
            // LOOP ITERATORS — set inside the Loop Body branch only.
            // ─────────────────────────────────────────────────────────────
            Add("loop.index", ScopeLoop,
                "Current loop counter (First..Last, refreshed each iteration). Inside nested loops the inner loop wins.",
                "Flow.ForLoop");
            Add("loop.index_<id6>", ScopeLoop,
                "Disambiguated loop counter scoped to a specific Flow.ForLoop node — <id6> is the first 6 chars of the node id. Use when a nested loop body needs to reference the OUTER loop's index unambiguously.",
                "Flow.ForLoop");
            Add("loop.item", ScopeLoop,
                "Current item value for this iteration, refreshed each step. Only valid inside the Loop Body branch.",
                "Flow.ForEach");

            // ─────────────────────────────────────────────────────────────
            // SYSTEM TIME — lazy-resolved by the engine on every read so
            // each substitution sees the current wall clock. UTC by default;
            // *.local_* mirrors the host's local time.
            // ─────────────────────────────────────────────────────────────
            Add("system.time", ScopeSystem,
                "Current UTC time as HH:mm:ss.",
                "Engine (lazy)");
            Add("system.hours", ScopeSystem,
                "UTC hour of day, 0–23.",
                "Engine (lazy)");
            Add("system.minutes", ScopeSystem,
                "UTC minute of hour, 0–59.",
                "Engine (lazy)");
            Add("system.seconds", ScopeSystem,
                "UTC second of minute, 0–59.",
                "Engine (lazy)");
            Add("system.unix", ScopeSystem,
                "Current UTC time as Unix epoch seconds.",
                "Engine (lazy)");
            Add("system.date", ScopeSystem,
                "Current UTC date as \"D.MonthName.YYYY\" (e.g. \"5.May.2026\").",
                "Engine (lazy)");
            Add("system.day", ScopeSystem,
                "UTC day of month, 1–31.",
                "Engine (lazy)");
            Add("system.month", ScopeSystem,
                "UTC month number, 1–12.",
                "Engine (lazy)");
            Add("system.monthname", ScopeSystem,
                "UTC month name (English, locale-pinned).",
                "Engine (lazy)");
            Add("system.dayname", ScopeSystem,
                "UTC weekday name (English, locale-pinned).",
                "Engine (lazy)");
            Add("system.year", ScopeSystem,
                "UTC four-digit year.",
                "Engine (lazy)");
            Add("system.local_time", ScopeSystem,
                "Same as system.time but using the Hub host's local time. Use when scripts need wall-clock readings (e.g. \"Good morning\" banners).",
                "Engine (lazy)");
            Add("system.local_hours", ScopeSystem,
                "Local-time hour of day. system.local_minutes / local_seconds / local_date / local_day / local_month / local_monthname / local_dayname / local_year follow the same UTC→local mirror pattern.",
                "Engine (lazy)");

            // ─────────────────────────────────────────────────────────────
            // STREAM UPTIME — anchored at Hub startup by default; can be
            // re-anchored at runtime via SetStreamStartedAtUtc.
            // ─────────────────────────────────────────────────────────────
            Add("stream.uptime", ScopeStream,
                "Human-readable uptime — \"45s\" / \"23m\" / \"1h 23m\" depending on magnitude.",
                "Time.StreamUptime + engine (lazy)");
            Add("stream.uptime_seconds", ScopeStream,
                "Total uptime in whole seconds.",
                "Time.StreamUptime + engine (lazy)");
            Add("stream.uptime_minutes", ScopeStream,
                "Total uptime in whole minutes.",
                "Time.StreamUptime + engine (lazy)");
            Add("stream.uptime_hours", ScopeStream,
                "Total uptime in whole hours.",
                "Time.StreamUptime + engine (lazy)");
            Add("stream.uptime_formatted", ScopeStream,
                "Alias for stream.uptime kept for emit clarity.",
                "Engine (lazy)");
            Add("stream.started_at", ScopeStream,
                "ISO-8601 UTC timestamp of the configured stream-start anchor.",
                "Engine (lazy)");

            // ─────────────────────────────────────────────────────────────
            // TWITCH LOOKUP OUTPUTS — set by data-fetch nodes during
            // execution. Available downstream of the fetch node only.
            // ─────────────────────────────────────────────────────────────
            Add("user.id", ScopeLookup,
                "Twitch user id (numeric, as a string).",
                "Twitch.GetUser");
            Add("user.display_name", ScopeLookup,
                "Twitch display name (case-preserved form).",
                "Twitch.GetUser");
            Add("user.login", ScopeLookup,
                "Twitch login name (lowercase form used in URLs).",
                "Twitch.GetUser");
            Add("user.profile_image", ScopeLookup,
                "URL of the user's Twitch profile picture.",
                "Twitch.GetUser");
            Add("user.account_created", ScopeLookup,
                "Account-creation timestamp of the Twitch user.",
                "Twitch.GetUser");
            Add("user.game", ScopeLookup,
                "The channel's last set game / category (offline-capable).",
                "Twitch.GetUser");
            Add("user.channel_title", ScopeLookup,
                "The channel's last set stream title (offline-capable).",
                "Twitch.GetUser");
            Add("user.is_mod", ScopeLookup,
                "True when the user is a moderator of the channel. user.is_sub / user.is_vip mirror the same shape.",
                "Twitch.GetUser");
            Add("user.is_sub", ScopeLookup,
                "True when the user is a current subscriber.",
                "Twitch.GetUser");
            Add("user.is_vip", ScopeLookup,
                "True when the user holds the VIP role.",
                "Twitch.GetUser");
            Add("stream.title", ScopeLookup,
                "Title of the live stream.",
                "Twitch.GetStream");
            Add("stream.game", ScopeLookup,
                "Category / game the stream is in.",
                "Twitch.GetStream");
            Add("stream.viewers", ScopeLookup,
                "Current viewer count.",
                "Twitch.GetStream");
            Add("stream.is_live", ScopeLookup,
                "True when the channel is currently broadcasting.",
                "Twitch.GetStream");
            Add("stream.uptime", ScopeLookup,
                "Human-readable stream uptime (e.g. \"2h 14m\"); empty when offline.",
                "Twitch.GetStream");
            Add("role.is_mod", ScopeLookup,
                "True when the queried user is a moderator. role.is_sub / role.is_vip / role.is_broadcaster mirror the same shape for the other roles.",
                "Twitch.CheckRole");
            Add("role.is_sub", ScopeLookup,
                "True when the queried user is a current subscriber.",
                "Twitch.CheckRole");
            Add("role.is_vip", ScopeLookup,
                "True when the queried user holds the VIP role.",
                "Twitch.CheckRole");
            Add("role.is_broadcaster", ScopeLookup,
                "True when the queried user is the broadcaster.",
                "Twitch.CheckRole");
            Add("follow.days", ScopeLookup,
                "How many days since the user followed the channel.",
                "Twitch.GetFollowAge");
            Add("follow.formatted", ScopeLookup,
                "Human-readable follow-age string (e.g. \"2 years, 3 months\").",
                "Twitch.GetFollowAge");
            Add("follow.date", ScopeLookup,
                "The date the user followed the channel.",
                "Twitch.GetFollowAge");
            Add("follow.is_following", ScopeLookup,
                "True when the user currently follows the channel.",
                "Twitch.GetFollowAge");
            Add("clip.url", ScopeLookup,
                "URL of the clip created by Twitch.CreateClip (empty when the stream is offline).",
                "Twitch.CreateClip");
            Add("clip.ok", ScopeLookup,
                "True when the clip was created successfully.",
                "Twitch.CreateClip");

            // ─────────────────────────────────────────────────────────────
            // RESULT OUTPUTS — written by the engine after a node finishes;
            // available to every downstream node. One row per emitted token,
            // with BoundBy listing every node that writes it (frequently
            // shared — e.g. result.ai_response is set by AI.Prompt,
            // AI.StreamText, AI.VisionDescribe, and AI.WithTools).
            // ─────────────────────────────────────────────────────────────
            Add("result.file_content", ScopeResult,
                "Full text content read from disk. On parse failure for *.ReadJSON it still carries the raw bytes so the script can introspect.",
                "File.ReadText, File.ReadJSON");
            Add("result.file_error", ScopeResult,
                "Error message from the file operation (empty on success). For *.WriteJSON a malformed payload is reported here and the file is left untouched.",
                "File.ReadText, File.WriteText, File.ReadJSON, File.WriteJSON");
            Add("result.http_status", ScopeResult,
                "HTTP status code (200, 404, …). Available after Get/Post/Put/Delete completes.",
                "HTTP.Get, HTTP.Post, HTTP.Put, HTTP.Delete");
            Add("result.http_body", ScopeResult,
                "Response body as a string. Pair with HTTP.ParseJson for structured payloads.",
                "HTTP.Get, HTTP.Post, HTTP.Put, HTTP.Delete");
            Add("result.http_error", ScopeResult,
                "Error message from the HTTP call (empty on success).",
                "HTTP.Get, HTTP.Post, HTTP.Put, HTTP.Delete");
            Add("result.api_response", ScopeResult,
                "Response body from the high-level API.Call wrapper.",
                "API.Call");
            Add("result.api_error", ScopeResult,
                "Error message from API.Call (empty on success).",
                "API.Call");
            Add("result.json_value", ScopeResult,
                "Extracted value from the supplied JSON path. Empty when the path didn't match.",
                "HTTP.ParseJson");
            Add("result.ai_response", ScopeResult,
                "Generated text from the AI provider. AI.StreamText updates this cumulatively per chunk; AI.WithTools sets it only when the model returned plain text instead of a tool call.",
                "AI.Prompt, AI.StreamText, AI.VisionDescribe, AI.WithTools");
            Add("result.ai_error", ScopeResult,
                "Provider-side error message (empty on success).",
                "AI.Prompt, AI.StreamText, AI.VisionDescribe, AI.WithTools, AI.Moderate, AI.GenerateImage");
            Add("result.ai_done", ScopeResult,
                "Flips to true once a streaming AI call closes — use to detect completion when binding AI_CHUNK overlays.",
                "AI.StreamText, AI.WithTools");
            Add("result.ai_flagged", ScopeResult,
                "True when the moderation classifier flagged the input.",
                "AI.Moderate");
            Add("result.ai_category", ScopeResult,
                "Highest-confidence category the moderation classifier returned (e.g. \"hate\", \"sexual\", \"violence\").",
                "AI.Moderate");
            Add("result.ai_image_url", ScopeResult,
                "URL of the generated image. Provider hosts it; download or pass directly to a Visual.Trigger payload.",
                "AI.GenerateImage");
            Add("result.ai_image_error", ScopeResult,
                "Error message from the image-generation call (empty on success).",
                "AI.GenerateImage");
            Add("result.ai_image_done", ScopeResult,
                "Flips to true once the image-generation call completes (success or failure).",
                "AI.GenerateImage");
            Add("result.ai_tool_calls", ScopeResult,
                "JSON-array string of {id, name, arguments} when the model returned a tool call instead of plain text. Mutually exclusive with result.ai_response on a given call.",
                "AI.WithTools");
            Add("result.audio_error", ScopeResult,
                "Error message from the audio operation (empty on success).",
                "Audio.Play, Audio.Stop, Audio.SetVolume, Audio.PlayTts");
            Add("result.discord_message_id", ScopeResult,
                "Snowflake id of the message just posted by the bot.",
                "Discord.SendMessage, Discord.SendEmbed");
            Add("result.discord_error", ScopeResult,
                "Error message from the Discord REST call (empty on success).",
                "Discord.SendMessage, Discord.SendEmbed, Discord.AddRole, Discord.RemoveRole, Discord.React, Discord.GetUser");
            Add("result.discord_user_id", ScopeResult,
                "Looked-up Discord user's snowflake id.",
                "Discord.GetUser");
            Add("result.discord_user_name", ScopeResult,
                "Looked-up Discord user's primary username.",
                "Discord.GetUser");
            Add("result.discord_user_global_name", ScopeResult,
                "Looked-up Discord user's display / global name (the new account-wide name).",
                "Discord.GetUser");
            Add("result.discord_user_avatar", ScopeResult,
                "Looked-up Discord user's avatar URL. Empty when the user has no custom avatar.",
                "Discord.GetUser");
            Add("result.sb_dispatched", ScopeResult,
                "True when Streamer.bot accepted the action dispatch.",
                "StreamerBot.DoAction");

            // ─────────────────────────────────────────────────────────────
            // VARIABLE NAMESPACES — author-supplied keys; the namespace and
            // lifetime are what's documented here, not specific names.
            // ─────────────────────────────────────────────────────────────
            Add("{var.<name>}", ScopeVars,
                "Script-local variable. Lives for the duration of one script run; branch-local — values written inside an Async.Parallel branch do NOT leak back to the parent. Read with Var.Get, write with Var.Set, or use a bare {var.<name>} reference inline.",
                "Var.Get, Var.Set");
            Add("{public.<name>}", ScopeVars,
                "Script-run-wide variable. Same lifetime as {var.*} but writes inside an Async.Parallel branch DO merge back to the parent on join — use this when a value needs to cross branches without a SQLite round-trip.",
                "Public.Get, Public.Set");
            Add("{global.<name>}", ScopeVars,
                "Persistent variable backed by SQLite (Vars table). Survives Hub restarts. Use sparingly for true cross-session state — every read/write hits the DB.",
                "DB.GetVariable, DB.SetVariable, DB.Increment");
        }

        /// <summary>
        /// Stable category ordering so the doc tree reads top-to-bottom in
        /// roughly the order a streamer encounters tokens (event-bound
        /// first, namespace primitives last).
        /// </summary>
        public static IReadOnlyList<string> OrderedScopes { get; } = new[]
        {
            ScopeUser,
            ScopeEvent,
            ScopeLoop,
            ScopeLookup,
            ScopeResult,
            ScopeSystem,
            ScopeStream,
            ScopeVars,
        };
    }
}
