using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Phoenix.Controls.Shared.Core
{
    /// <summary>
    /// Metadata for one registered Script command.
    ///
    /// <see cref="Args"/> is the typed schema. When a manifest entry
    /// is registered via <see cref="CommandManifest.AddTyped"/>, both <see cref="ArgNames"/>
    /// and <see cref="Args"/> are populated; <see cref="ArgNames"/> stays the simple
    /// `string[]` projection so existing call sites that only need the names (e.g.
    /// <c>VerifyAllHubCommandsRegistered</c>) keep working unchanged.
    ///
    /// <see cref="Args"/> is now non-nullable. Zero-arg commands
    /// still produce a valid spec via <see cref="System.Array.Empty{T}"/>. Callers using
    /// the public <see cref="CommandManifest.Register"/> API must pass a typed
    /// <see cref="ArgSpec"/> list (or an empty array). The previous nullable contract
    /// existed only for the legacy untyped <c>Add</c> path inside <c>BuildManifest</c>,
    /// which was removed; keeping the parameter nullable was dead optionality.
    /// </summary>
    public sealed record CommandSpec(
        string Name,
        int MinArgs,
        int MaxArgs,
        IReadOnlyList<string> ArgNames,
        IReadOnlyList<ArgSpec> Args);

    public static class CommandManifest
    {
        // Canonical command-name table shared between the Architect-side exporter
        // (writes calls into .phx) and the Hub-side ScriptManager (registers
        // handlers). Each command listed here MUST:
        //   * have a matching SimpleEmitDescriptor or imperative handler in
        //     Phoenix.Controls.Architect.Core.ExporterRegistrations, AND
        //   * be registered via _engine.RegisterCommand in ScriptManager.
        // CommandManifestTests in Phoenix.Controls.Tests cross-checks both
        // directions; ScriptManager validates the Hub side at startup.
        //
        // Promoted to ConcurrentDictionary.
        // Register / TryRemove / AddTyped all mutate this map at runtime
        // (test fixtures inject + tear down entries via AddTyped/TryRemove;
        // the CommandBinder tests do this concurrently with the
        // Hub-side VerifyCommandManifest reading All.Keys). The previous
        // Dictionary<,> was an unsynchronized read/write seam; switching to
        // ConcurrentDictionary makes the writes atomic without forcing every
        // reader through a lock. TryGetValue / ContainsKey / iterator access
        // (foreach, Keys, Values) are all concurrent-safe on
        // ConcurrentDictionary; consumers that care about a stable snapshot
        // already call .ToList()/.ToArray() (see SuggestNearMatch loop and
        // ScriptManager.VerifyCommandManifest's .OrderBy().ToList()).
        private static readonly ConcurrentDictionary<string, CommandSpec> _all =
            BuildManifest();

        public static IReadOnlyDictionary<string, CommandSpec> All => _all;

        public static void Register(CommandSpec spec) => _all[spec.Name] = spec;

        /// <summary>
        /// Removes a manifest entry. Used by tests that inject fixture
        /// entries (e.g. via <see cref="AddTyped"/>) and need to tear them down
        /// so a later <c>ScriptManager.Instance</c> initialization doesn't trip
        /// <c>VerifyCommandManifest()</c> on the leaked entry. Returns true if
        /// the entry existed and was removed.
        ///
        /// ConcurrentDictionary's TryRemove takes (key, out value); we
        /// discard the removed spec because callers only care about presence.
        /// </summary>
        public static bool TryRemove(string name) => _all.TryRemove(name, out _);

        /// <summary>
        /// Typed registration. Use this for new commands so the
        /// schema travels with the manifest entry and <see cref="CommandBinder.BindArgs"/>
        /// can produce a typed dict for the handler. Existing entries registered via
        /// <see cref="BuildManifest"/>'s untyped <c>Add</c> overload keep working —
        /// their <see cref="CommandSpec.Args"/> stays null and handlers continue to
        /// take raw <c>string[]</c>. Migration is per-command, not big-bang.
        /// </summary>
        public static void AddTyped(string name, params ArgSpec[] args)
        {
            if (args is null) args = System.Array.Empty<ArgSpec>();
            int min = args.Count(a => !a.Optional);
            int max = args.Length;
            var argNames = args.Select(a => a.Name).ToArray();
            _all[name] = new CommandSpec(name, min, max, argNames, args);
        }

        // Script command names are case-sensitive (`db.SetVariable`
        // and `DB.SetVariable` would be distinct commands by the script grammar).
        // The previous comparer here was OrdinalIgnoreCase, which silently collapsed
        // any pair colliding only on case (last-wins). Switching to Ordinal makes the
        // map case-sensitive; the Add-helper below additionally throws on an
        // exact-case duplicate so the registration table can't quietly overwrite a
        // descriptor at startup. Duplicates are a manifest authoring bug, not a
        // runtime hot-swap path — failing loudly is the correct posture.
        // Return type widened to ConcurrentDictionary alongside the
        // field promotion. We still BUILD in a plain Dictionary because the
        // local AddT helper relies on ContainsKey + assignment for the
        // duplicate-throw contract (and a single-threaded constructor doesn't
        // need the concurrent overhead). The final dict is copied into a
        // ConcurrentDictionary with the same Ordinal comparer so case-sensitivity
        // is preserved post-promotion.
        private static ConcurrentDictionary<string, CommandSpec> BuildManifest()
        {
            var dict = new Dictionary<string, CommandSpec>(System.StringComparer.Ordinal);

            // Local typed-spec helper. Writes typed ArgSpecs into
            // the local `dict` (we can't call the public static AddTyped here
            // because it writes to `_all`, which is being initialized by this
            // very method's return — `_all` is null until BuildManifest
            // returns). The bare-`Add` overload was removed once
            // the migration ensured every entry uses `AddT`. New
            // registrations must declare their typed schema via ArgSpec[].
            void AddT(string name, params ArgSpec[] specs)
            {
                if (dict.ContainsKey(name))
                {
                    throw new System.InvalidOperationException(
                        $"CommandManifest: duplicate registration for command '{name}'.");
                }
                int min = specs.Count(a => !a.Optional);
                int max = specs.Length;
                var argNames = specs.Select(a => a.Name).ToArray();
                dict[name] = new CommandSpec(name, min, max, argNames, specs);
            }

            // Bus  — typed
            AddT("bus.send",      new ArgSpec("Target", ArgType.String), new ArgSpec("Type", ArgType.String), new ArgSpec("Payload", ArgType.String));
            AddT("bus.broadcast", new ArgSpec("Type",   ArgType.String), new ArgSpec("Payload", ArgType.String));

            // Chat — the unified multi-platform sender behind the Chat.Send
            // node. Platforms = the runtime override (single name or comma
            // list); Fallback = the exporter-baked checkmark CSV applied when
            // the override resolves empty. Imperative Hub handler (fans out to
            // the per-platform send cores) — no SimpleEmitDescriptor.
            AddT("chat.send", new ArgSpec("Message", ArgType.String), new ArgSpec("Platforms", ArgType.String, Optional: true, Default: ""), new ArgSpec("Fallback", ArgType.String, Optional: true, Default: ""));

            // Twitch — simple emits
            AddT("twitch.send_chat",            new ArgSpec("Message", ArgType.String));
            AddT("twitch.reply",                new ArgSpec("MessageId", ArgType.String), new ArgSpec("Message", ArgType.String));
            AddT("twitch.timeout",              new ArgSpec("User", ArgType.String), new ArgSpec("Sec", ArgType.Int, Optional: true, Default: "60"));
            AddT("twitch.ban",                  new ArgSpec("User", ArgType.String), new ArgSpec("Reason", ArgType.String, Optional: true, Default: ""));
            AddT("twitch.shoutout",             new ArgSpec("User", ArgType.String));
            AddT("twitch.announcement",         new ArgSpec("Message", ArgType.String));
            AddT("twitch.create_poll",          new ArgSpec("Title", ArgType.String), new ArgSpec("Choices", ArgType.String), new ArgSpec("DurationSec", ArgType.Int));
            AddT("twitch.end_poll",             new ArgSpec("PollId", ArgType.String));
            AddT("twitch.create_prediction",    new ArgSpec("Title", ArgType.String), new ArgSpec("OutcomeA", ArgType.String), new ArgSpec("OutcomeB", ArgType.String), new ArgSpec("OutcomeC", ArgType.String, Optional: true, Default: ""), new ArgSpec("OutcomeD", ArgType.String, Optional: true, Default: ""), new ArgSpec("OutcomeE", ArgType.String, Optional: true, Default: ""), new ArgSpec("DurationSec", ArgType.Int, Optional: true, Default: "120"));
            AddT("twitch.resolve_prediction",   new ArgSpec("PredictionId", ArgType.String), new ArgSpec("WinningOutcomeId", ArgType.String));
            AddT("twitch.update_reward_cost",   new ArgSpec("RewardId", ArgType.String), new ArgSpec("Cost", ArgType.Int));
            AddT("twitch.set_reward_enabled",   new ArgSpec("RewardId", ArgType.String), new ArgSpec("Enabled", ArgType.Bool));
            AddT("twitch.fulfill_redemption",   new ArgSpec("RedemptionId", ArgType.String));
            AddT("twitch.reject_redemption",    new ArgSpec("RedemptionId", ArgType.String));
            AddT("twitch.create_clip",          new ArgSpec("Duration", ArgType.Int, Optional: true, Default: "30"), new ArgSpec("Title", ArgType.String, Optional: true, Default: ""));

            // Moderation / channel-control proxies. All currently
            // route through Streamer.bot DoAction (action name = the command's PascalCase
            // form, e.g. `TwitchUnban`); they'll get direct Helix calls once Hub gains
            // first-class Twitch API access.
            AddT("twitch.unban",            new ArgSpec("User", ArgType.String));
            AddT("twitch.mod",              new ArgSpec("User", ArgType.String));
            AddT("twitch.unmod",            new ArgSpec("User", ArgType.String));
            AddT("twitch.vip",              new ArgSpec("User", ArgType.String));
            AddT("twitch.unvip",            new ArgSpec("User", ArgType.String));
            AddT("twitch.delete_message",   new ArgSpec("MessageId", ArgType.String));
            AddT("twitch.slow_mode",        new ArgSpec("Seconds", ArgType.Int));        // 0 = off
            AddT("twitch.follower_mode",    new ArgSpec("Minutes", ArgType.Int));        // -1 = off
            AddT("twitch.sub_only_mode",    new ArgSpec("Enabled", ArgType.Bool));
            AddT("twitch.marker",           new ArgSpec("Description", ArgType.String, Optional: true, Default: ""));
            AddT("twitch.whisper",          new ArgSpec("User", ArgType.String), new ArgSpec("Message", ArgType.String));
            AddT("twitch.update_channel",   new ArgSpec("Title", ArgType.String), new ArgSpec("GameId", ArgType.String, Optional: true, Default: ""));

            // OBS — proxy commands routed through Streamer.bot's DoAction relay (action
            // name = the command's PascalCase form, e.g. `ObsSetScene`). Handlers live
            // in ScriptManager.Obs.cs. A direct OBS-WebSocket client in the Hub is the
            // planned future swap; it'll change the dispatch transport without changing
            // the manifest / descriptor / node-template / script-facing contract here.
            AddT("obs.set_scene",              new ArgSpec("Scene", ArgType.String));
            AddT("obs.set_source_visible",     new ArgSpec("Scene", ArgType.String), new ArgSpec("Source", ArgType.String), new ArgSpec("Visible", ArgType.Bool));
            AddT("obs.refresh_browser_source", new ArgSpec("Scene", ArgType.String), new ArgSpec("Source", ArgType.String), new ArgSpec("Url", ArgType.String));
            AddT("obs.start_recording");
            AddT("obs.stop_recording");
            AddT("obs.start_streaming");
            AddT("obs.stop_streaming");
            AddT("obs.save_replay_buffer");
            AddT("obs.set_source_position",    new ArgSpec("Scene", ArgType.String), new ArgSpec("Source", ArgType.String), new ArgSpec("X", ArgType.Float), new ArgSpec("Y", ArgType.Float));
            AddT("obs.set_source_scale",       new ArgSpec("Scene", ArgType.String), new ArgSpec("Source", ArgType.String), new ArgSpec("ScaleX", ArgType.Float), new ArgSpec("ScaleY", ArgType.Float));
            AddT("obs.set_source_rotation",    new ArgSpec("Scene", ArgType.String), new ArgSpec("Source", ArgType.String), new ArgSpec("Degrees", ArgType.Float));
            AddT("obs.set_filter_visible",     new ArgSpec("Scene", ArgType.String), new ArgSpec("Source", ArgType.String), new ArgSpec("Filter", ArgType.String), new ArgSpec("Visible", ArgType.Bool));
            AddT("obs.take_screenshot",        new ArgSpec("Scene", ArgType.String), new ArgSpec("Source", ArgType.String), new ArgSpec("Path", ArgType.String));

            // Collections / Queue / State
            AddT("array.push", new ArgSpec("List", ArgType.String), new ArgSpec("Value", ArgType.String));
            AddT("queue.push", new ArgSpec("EventID", ArgType.String), new ArgSpec("Payload", ArgType.String));
            AddT("queue.clear");
            AddT("state.set",  new ArgSpec("Name", ArgType.String), new ArgSpec("Value", ArgType.String));

            // Public.Set — script-run-wide, no DB, parallel-branch merge-back.
            // Routes through SetLocalResultVar so _branchResultKeysLocal tags the
            // key for parallel_begin's merge step.
            AddT("public.set", new ArgSpec("Key", ArgType.String), new ArgSpec("Value", ArgType.String));

            // Platforms remainder — typed
            AddT("discord.webhook",        new ArgSpec("URL", ArgType.String), new ArgSpec("Msg", ArgType.String));

            // Discord bot REST path (alongside the legacy
            // webhook command, which stays). Bot token sourced from
            // AppConfig.DiscordBotToken; result.* contract mirrors http.* (empty
            // result.discord_error on success, message on failure;
            // result.discord_message_id populated on send_message success).
            // Send_embed's optional fields default to empty strings — Discord's
            // API tolerates a partial embed as long as one of title/description/
            // url is provided.
            AddT("discord.send_message",
                new ArgSpec("ChannelId", ArgType.String),
                new ArgSpec("Content",   ArgType.String));
            AddT("discord.send_embed",
                new ArgSpec("ChannelId",   ArgType.String),
                new ArgSpec("Title",       ArgType.String, Optional: true, Default: ""),
                new ArgSpec("Description", ArgType.String, Optional: true, Default: ""),
                new ArgSpec("Color",       ArgType.String, Optional: true, Default: ""),
                new ArgSpec("Url",         ArgType.String, Optional: true, Default: ""));

            // Role + reaction + user-lookup. Same bot-token path,
            // same result.discord_error contract. discord.get_user surfaces the
            // user object as result.discord_user_id / _name / _global_name /
            // _avatar; AddRole/RemoveRole/React only touch result.discord_error.
            AddT("discord.add_role",
                new ArgSpec("GuildId", ArgType.String),
                new ArgSpec("UserId",  ArgType.String),
                new ArgSpec("RoleId",  ArgType.String));
            AddT("discord.remove_role",
                new ArgSpec("GuildId", ArgType.String),
                new ArgSpec("UserId",  ArgType.String),
                new ArgSpec("RoleId",  ArgType.String));
            AddT("discord.react",
                new ArgSpec("ChannelId", ArgType.String),
                new ArgSpec("MessageId", ArgType.String),
                new ArgSpec("Emoji",     ArgType.String));
            AddT("discord.get_user",
                new ArgSpec("UserId", ArgType.String));
            AddT("http.get",               new ArgSpec("Url", ArgType.String), new ArgSpec("Headers", ArgType.String, Optional: true, Default: ""));
            // File I/O. Append uses ArgType.Bool so the binder coerces "true"/"1"/etc.
            // (matches flow.do_once.Reset's contract).
            AddT("file.read_text",         new ArgSpec("Path", ArgType.String));
            AddT("file.write_text",        new ArgSpec("Path", ArgType.String), new ArgSpec("Content", ArgType.String), new ArgSpec("Append", ArgType.Bool, Optional: true, Default: "false"));
            // file.{read,write}_json. Mirror file.{read,write}_text but
            // gate on JSON-validity. read_json: parses to surface a parse error in
            // result.file_error; write_json: rejects malformed payloads BEFORE the
            // disk write so a partial / corrupt JSON file can't be left behind.
            AddT("file.read_json",         new ArgSpec("Path", ArgType.String));
            AddT("file.write_json",        new ArgSpec("Path", ArgType.String), new ArgSpec("Content", ArgType.String), new ArgSpec("Append", ArgType.Bool, Optional: true, Default: "false"));

            // Audio. Fire-and-forget local playback; result.audio_error mirrors
            // the file.* contract (empty on success, message on failure). audio.set_volume
            // updates an in-process global multiplier — no DB write.
            AddT("audio.play",       new ArgSpec("Path", ArgType.String), new ArgSpec("Volume", ArgType.Float, Optional: true, Default: "1.0"));
            AddT("audio.play_tts",   new ArgSpec("Text", ArgType.String), new ArgSpec("Voice", ArgType.String, Optional: true, Default: ""), new ArgSpec("Rate", ArgType.Int, Optional: true, Default: "0"), new ArgSpec("Volume", ArgType.Float, Optional: true, Default: "1.0"));
            AddT("audio.set_volume", new ArgSpec("Volume", ArgType.Float));
            AddT("http.post",              new ArgSpec("Url", ArgType.String), new ArgSpec("Body", ArgType.String, Optional: true, Default: ""), new ArgSpec("ContentType", ArgType.String, Optional: true, Default: "application/json"), new ArgSpec("Headers", ArgType.String, Optional: true, Default: ""));
            AddT("http.put",               new ArgSpec("Url", ArgType.String), new ArgSpec("Body", ArgType.String, Optional: true, Default: ""), new ArgSpec("ContentType", ArgType.String, Optional: true, Default: "application/json"), new ArgSpec("Headers", ArgType.String, Optional: true, Default: ""));
            AddT("http.patch",             new ArgSpec("Url", ArgType.String), new ArgSpec("Body", ArgType.String, Optional: true, Default: ""), new ArgSpec("ContentType", ArgType.String, Optional: true, Default: "application/json"), new ArgSpec("Headers", ArgType.String, Optional: true, Default: ""));
            AddT("http.delete",            new ArgSpec("Url", ArgType.String));
            AddT("api.call",               new ArgSpec("Url", ArgType.String));
            AddT("streamerbot.do_action",  new ArgSpec("ActionId", ArgType.String));
            AddT("streamerbot.get_user",   new ArgSpec("User", ArgType.String));

            // AI
            AddT("ai.prompt",   new ArgSpec("SystemPrompt", ArgType.String), new ArgSpec("UserPrompt", ArgType.String), new ArgSpec("Model", ArgType.String, Optional: true, Default: ""));
            AddT("ai.moderate", new ArgSpec("Text", ArgType.String));
            // ai.generate_image. Calls OpenAI's
            // /v1/images/generations endpoint. Single-shot (no streaming
            // surface — the API returns the URL once generation completes).
            // Result vars: result.ai_image_url / result.ai_image_error /
            // result.ai_image_done. Defaults: model=dall-e-3, size=1024x1024.
            AddT("ai.generate_image", new ArgSpec("Prompt", ArgType.String), new ArgSpec("Model", ArgType.String, Optional: true, Default: "dall-e-3"), new ArgSpec("Size", ArgType.String, Optional: true, Default: "1024x1024"));
            // ai.vision_describe. Calls OpenAI's chat completions
            // with a multi-modal user message (text + image_url content
            // parts). Single-shot. Result vars: result.ai_response /
            // result.ai_error. Default model gpt-4o-mini (cheapest vision-
            // capable model); pass gpt-4o for stronger visual reasoning.
            AddT("ai.vision_describe", new ArgSpec("Prompt", ArgType.String), new ArgSpec("ImageUrl", ArgType.String), new ArgSpec("Model", ArgType.String, Optional: true, Default: "gpt-4o-mini"));
            // ai.with_tools. Single-shot OpenAI chat completions
            // with the `tools` field populated. Tools is a JSON-array string
            // of OpenAI tool definitions. Result vars are mutually exclusive:
            //   * result.ai_response   — plain text (model answered directly)
            //   * result.ai_tool_calls — JSON array of {id, name, arguments}
            //                            (model decided to call a tool)
            // Plus result.ai_error / result.ai_done. Default model gpt-4o-mini.
            // ToolChoice / ParallelToolCalls are optional; empty =
            // omit so the API default applies. ToolChoice accepts
            // "auto" | "none" | "any" | "required" | a JSON object that names a
            // tool; ParallelToolCalls accepts "true" / "false" / "" (omit).
            // Arg order must match the handler (args[4]=ToolChoice, args[5]=ParallelToolCalls).
            AddT("ai.with_tools", new ArgSpec("SystemPrompt", ArgType.String), new ArgSpec("UserPrompt", ArgType.String), new ArgSpec("Tools", ArgType.String), new ArgSpec("Model", ArgType.String, Optional: true, Default: "gpt-4o-mini"), new ArgSpec("ToolChoice", ArgType.String, Optional: true, Default: ""), new ArgSpec("ParallelToolCalls", ArgType.String, Optional: true, Default: ""));
            // ai.stream_text. Hub streams SSE from the
            // selected provider, accumulates result.ai_response cumulatively,
            // broadcasts AI_CHUNK on Bus per delta, and flips
            // result.ai_done on close.
            // MemoryVar opt-in: when non-empty, names a
            // Var key whose JSON-array-of-{role,content} payload
            // is prepended to the request as prior turns and updated with
            // the new exchange after completion. Empty / missing memory
            // var = no history (single-shot, prior behaviour).
            AddT("ai.stream_text", new ArgSpec("SystemPrompt", ArgType.String), new ArgSpec("UserPrompt", ArgType.String), new ArgSpec("Model", ArgType.String, Optional: true, Default: ""), new ArgSpec("MemoryVar", ArgType.String, Optional: true, Default: ""));

            // Twitch Data
            AddT("twitch.get_user",       new ArgSpec("Username", ArgType.String));
            AddT("twitch.get_stream",     new ArgSpec("Username", ArgType.String));
            AddT("twitch.check_role",     new ArgSpec("Username", ArgType.String));
            AddT("twitch.is_online",      new ArgSpec("Channel", ArgType.String));
            AddT("twitch.get_follow_age", new ArgSpec("Username", ArgType.String));

            // YouTube — outbound platform commands. Proxy commands routed
            // through Streamer.bot DoAction (action name = "Phoenix: YT <Verb>");
            // handlers live in ScriptManager.YouTube.cs. youtube.get_user is a
            // data round-trip (result vars user.* fetched via
            // FetchActionGlobalsAsync with the phx_yt_* prefix).
            AddT("youtube.send_chat",       new ArgSpec("Message", ArgType.String));
            AddT("youtube.set_title",       new ArgSpec("Title", ArgType.String));
            AddT("youtube.set_description", new ArgSpec("Description", ArgType.String));
            AddT("youtube.timeout",         new ArgSpec("User", ArgType.String), new ArgSpec("Sec", ArgType.Int, Optional: true, Default: "300"));
            AddT("youtube.ban",             new ArgSpec("User", ArgType.String));
            // No DurationSec — the "Phoenix: YT Create Poll" action binds only
            // %question% + %choice1%..%choice4% (Hub splits Choices into 4); YouTube
            // polls take no duration over Streamer.bot.
            AddT("youtube.create_poll",     new ArgSpec("Title", ArgType.String), new ArgSpec("Choices", ArgType.String));
            AddT("youtube.end_poll");
            AddT("youtube.get_user",        new ArgSpec("Username", ArgType.String));

            // Kick — outbound platform commands. Proxy commands routed through
            // Streamer.bot DoAction (action name = "Phoenix: Kick <Verb>");
            // handlers live in ScriptManager.Kick.cs. kick.get_user is a data
            // round-trip (result vars user.* fetched via FetchActionGlobalsAsync
            // with the phx_kick_* prefix).
            AddT("kick.send_chat",          new ArgSpec("Message", ArgType.String));
            AddT("kick.reply",              new ArgSpec("MessageId", ArgType.String), new ArgSpec("Message", ArgType.String));
            AddT("kick.timeout",            new ArgSpec("User", ArgType.String), new ArgSpec("Sec", ArgType.Int, Optional: true, Default: "300"));
            AddT("kick.ban",                new ArgSpec("User", ArgType.String), new ArgSpec("Reason", ArgType.String, Optional: true, Default: ""));
            AddT("kick.unban",              new ArgSpec("User", ArgType.String));
            AddT("kick.untimeout",          new ArgSpec("User", ArgType.String));
            AddT("kick.set_title",          new ArgSpec("Title", ArgType.String));
            AddT("kick.set_category",       new ArgSpec("Category", ArgType.String));
            AddT("kick.delete_message",     new ArgSpec("MessageId", ArgType.String));
            AddT("kick.set_reward_cost",    new ArgSpec("RewardId", ArgType.String), new ArgSpec("Cost", ArgType.Int));
            AddT("kick.set_reward_enabled", new ArgSpec("RewardId", ArgType.String), new ArgSpec("Enabled", ArgType.Bool));
            AddT("kick.get_user",           new ArgSpec("Username", ArgType.String));

            // System
            AddT("system.log", new ArgSpec("Message", ArgType.String), new ArgSpec("Level", ArgType.String, Optional: true, Default: "LogicExecution"));

            // Async
            AddT("delay", new ArgSpec("MS", ArgType.Int));

            // Visual (Hub LayerRuntime → /hud/<id> WS → compositor.js)
            // visual.trigger_queued: fire-and-forget; appends key=val pairs after the fixed args.
            // wait_for_visual: blocks the script until VISUAL_COMPLETE arrives (or hard timeout).
            // EventData is ArgType.KvPairs + Variadic = the binder collects each
            // remaining raw arg as one "k=v" entry into IReadOnlyDictionary<string,string>.
            // Replaces the legacy in-handler split loop. For wait_for_visual the variadic
            // comes AFTER TimeoutMS:Int.
            AddT("visual.trigger_queued",
                new ArgSpec("LayerID", ArgType.String),
                new ArgSpec("WidgetID", ArgType.String),
                new ArgSpec("TriggerName", ArgType.String),
                new ArgSpec("EventData", ArgType.KvPairs, Variadic: true));
            AddT("wait_for_visual",
                new ArgSpec("LayerID", ArgType.String),
                new ArgSpec("WidgetID", ArgType.String),
                new ArgSpec("TriggerName", ArgType.String),
                new ArgSpec("TimeoutMS", ArgType.Int),
                new ArgSpec("EventData", ArgType.KvPairs, Variadic: true));

            // Process — live-instance lifecycle. process.start launches a new
            // instance of a process template (data/logic/processes/<ProcessId>.phx),
            // returns its minted instance id, and passes start params as instance-
            // scoped vars (read in the template as {param.<name>}). process.stop
            // tears one down by id. Both replace the retired fire-and-forget
            // `process_spawn(...)` block + process.terminate (kept as a deprecated
            // shim below so already-exported .phx still load).
            AddT("process.start",
                new ArgSpec("ProcessId", ArgType.String),
                new ArgSpec("Name", ArgType.String, Optional: true, Default: "Process"),
                new ArgSpec("StartParams", ArgType.KvPairs, Variadic: true));
            AddT("process.stop", new ArgSpec("InstanceId", ArgType.String));
            // Deprecated alias — old Process.Terminate nodes / hand-written .phx.
            // The Hub handler routes it to StopInstance as a fallback.
            AddT("process.terminate", new ArgSpec("InstanceId", ArgType.String));

            // Databank simple
            AddT("db.clear_table", new ArgSpec("TableName", ArgType.String));
            AddT("db.delete_var",  new ArgSpec("Key", ArgType.String));
            // NodeKey (arg 4) is the exporter-supplied per-node id that lets the
            // handler gate a channel-wide GLOBAL cooldown (always) plus a per-user
            // cooldown (when User is wired) without two Cooldown nodes colliding.
            // Optional so legacy 3-arg exports still bind (they keep the old
            // single-bucket behavior). See ScriptManager.Cooldown.cs.
            AddT("cooldown.check", new ArgSpec("User", ArgType.String), new ArgSpec("GlobalCooldownMs", ArgType.Int), new ArgSpec("UserCooldownMs", ArgType.Int), new ArgSpec("NodeKey", ArgType.String, Optional: true, Default: ""));
            AddT("db.check",       new ArgSpec("Key", ArgType.String));

            // DB-family commands that the exporter emits and the engine handles
            // but were never declared. Lookups still worked (the engine doesn't require
            // a manifest entry to dispatch), but VerifyCommandManifest's reverse-direction
            // audit is one-way; missing entries here mean no schema documentation either.
            // RowId is ArgType.Int — handlers historically used long.TryParse
            // for SQLite ROWIDs but practical row counts stay well under 2^31. Promote
            // to long once a real overflow case arrives.
            // db.insert_row was previously the only DB handler missing a
            // manifest entry. Schema: Table:String + variadic positional list of
            // col,val,col,val,...,resultVar. The variadic stays String (raw pass-
            // through) — KvPairs doesn't fit because the script grammar pairs by
            // POSITION (col then val) rather than by `=`. Handler runs the legacy
            // odd/even parity check on the rest list to detect the optional trailing
            // result-var name.
            AddT("db.insert_row",
                new ArgSpec("Table", ArgType.String),
                new ArgSpec("Pairs", ArgType.String, Variadic: true));
            AddT("db.find_row",       new ArgSpec("Table", ArgType.String), new ArgSpec("Column", ArgType.String), new ArgSpec("SearchValue", ArgType.String), new ArgSpec("ResultKey", ArgType.String));
            AddT("db.get_cell",       new ArgSpec("Table", ArgType.String), new ArgSpec("RowId", ArgType.Int), new ArgSpec("Column", ArgType.String));
            AddT("db.set_cell",       new ArgSpec("Table", ArgType.String), new ArgSpec("RowId", ArgType.Int), new ArgSpec("Column", ArgType.String), new ArgSpec("Value", ArgType.String));
            AddT("db.increment_cell", new ArgSpec("Table", ArgType.String), new ArgSpec("RowId", ArgType.Int), new ArgSpec("Column", ArgType.String), new ArgSpec("Amount", ArgType.Int, Optional: true, Default: "1"));
            AddT("db.fetch_row",      new ArgSpec("Table", ArgType.String), new ArgSpec("RowId", ArgType.Int), new ArgSpec("ResultKey", ArgType.String));
            AddT("db.delete_row",     new ArgSpec("Table", ArgType.String), new ArgSpec("RowId", ArgType.Int));
            AddT("db.row_count",      new ArgSpec("Table", ArgType.String));
            AddT("db.get_column",     new ArgSpec("Table", ArgType.String), new ArgSpec("Column", ArgType.String));

            // Giveaway — create is a simple emit (no value output); close/ticket/
            // winner are imperative handlers that ALSO emit a result-var base
            // (the trailing "ResultBase" arg the exporter injects) so their value
            // output sockets resolve to {<base>_<socket>}. See
            // ExporterRegistry giveaway handlers + ScriptManager.Giveaway.cs.
            // ticket's price trio (PriceTable/Price/IsAll) trails ResultBase so
            // pre-price 6-arg exports keep binding positionally — never reorder.
            // The trio is Optional+defaulted: the exporter only emits it when
            // PriceTable is set, so every legacy/non-priced 6-arg call would
            // otherwise log three missing-required-arg binder complaints per
            // ticket entry.
            AddT("giveaway.create", new ArgSpec("Title", ArgType.String), new ArgSpec("SetDefault", ArgType.Bool));
            AddT("giveaway.close",  new ArgSpec("Giveaway", ArgType.String), new ArgSpec("Public", ArgType.Bool), new ArgSpec("ResultBase", ArgType.String));
            // ticket's Role STRING was replaced by the IsSub/IsMod pair at the
            // same positions (4-5). Both are typed String (not Bool) and
            // Optional, and ResultBase turned Optional, so a LEGACY 6-arg call
            // — role badge at 4, base at 5, nothing after — still binds with
            // zero missing-required/coercion complaints; the Hub handler
            // detects the old shape from the raw args and translates it.
            AddT("giveaway.ticket", new ArgSpec("Giveaway", ArgType.String), new ArgSpec("Public", ArgType.Bool), new ArgSpec("User", ArgType.String), new ArgSpec("Increment", ArgType.Int), new ArgSpec("IsSub", ArgType.String, Optional: true, Default: "false"), new ArgSpec("IsMod", ArgType.String, Optional: true, Default: "false"), new ArgSpec("ResultBase", ArgType.String, Optional: true, Default: ""), new ArgSpec("PriceTable", ArgType.String, Optional: true, Default: ""), new ArgSpec("Price", ArgType.Int, Optional: true, Default: "0"), new ArgSpec("IsAll", ArgType.Bool, Optional: true, Default: "false"));
            AddT("giveaway.winner", new ArgSpec("Giveaway", ArgType.String), new ArgSpec("Public", ArgType.Bool), new ArgSpec("ResultBase", ArgType.String));
            // giveaway.default_id() — no-arg inline pure-data probe backing the
            // Giveaway.Id node; resolved inline by ScriptExporter, no descriptor.
            AddT("giveaway.default_id");
            // giveaway.is_active(<selector>) — inline pure-data probe backing the
            // Giveaway.IsActive node. Returns "true" while the targeted giveaway
            // (empty selector = the default one) is open; "false" otherwise.
            AddT("giveaway.is_active", new ArgSpec("Giveaway", ArgType.String, Optional: true, Default: ""));

            // text.* command family (was bypassing the three-way contract).
            // text.format gains ArgSpec.Variadic on the placeholder list — the
            // legacy 3-arg shape (Template, A, B) only ever covered the first two
            // placeholders; variadic lets {0}..{N} all bind into the typed dict.
            AddT("text.format",   new ArgSpec("Template", ArgType.String), new ArgSpec("Args", ArgType.String, Variadic: true));
            AddT("text.replace",  new ArgSpec("Source", ArgType.String), new ArgSpec("Find", ArgType.String), new ArgSpec("ReplaceWith", ArgType.String));
            AddT("text.split",    new ArgSpec("Source", ArgType.String), new ArgSpec("Delimiter", ArgType.String));
            AddT("text.length",   new ArgSpec("Source", ArgType.String));
            AddT("text.contains", new ArgSpec("Source", ArgType.String), new ArgSpec("Search", ArgType.String));
            AddT("text.to_upper", new ArgSpec("Source", ArgType.String));
            AddT("text.to_lower", new ArgSpec("Source", ArgType.String));
            AddT("text.join_list",new ArgSpec("List", ArgType.String), new ArgSpec("Separator", ArgType.String));

            // Convert.* family.
            AddT("convert.to_int",    new ArgSpec("Value", ArgType.String));
            AddT("convert.to_string", new ArgSpec("Value", ArgType.String));
            AddT("convert.to_bool",   new ArgSpec("Value", ArgType.String));
            AddT("convert.to_float",  new ArgSpec("Value", ArgType.String));

            // http.parse_json (exporter emits, engine handles, manifest missed).
            AddT("http.parse_json", new ArgSpec("Json", ArgType.String), new ArgSpec("Path", ArgType.String));

            // on_obs(EventType) trigger registration. The manifest entry exists
            // purely to satisfy the three-way contract (CommandManifest ↔ exporter ↔
            // Hub-side handler) and the audit test in CommandManifestTests; the
            // string is never actually invoked from a .phx body (it's a header
            // prefix matched by ScriptEngine, identical to on_event /
            // on_webhook / on_websocket). The Hub-side RegisterCommand call in
            // ScriptManager.Obs.cs is therefore a no-op handler — present to
            // pass VerifyCommandManifest, never reached at runtime because the
            // dispatcher (DispatchObsEvent) runs the script file directly.
            AddT("on_obs", new ArgSpec("EventType", ArgType.String));

            // event.trigger / event.executor pair. event.trigger is emitted by
            // ScriptExporter for the Event.Trigger node and executes nested
            // on_event(<name>): handlers via ExecuteInternalEventAsync.
            // EventArgs is variadic KvPairs (each remaining raw arg is one
            // "k=v") so the handler can pull a typed dict via CurrentBoundArgs.
            AddT("event.trigger",
                new ArgSpec("Name",      ArgType.String),
                new ArgSpec("EventArgs", ArgType.KvPairs, Variadic: true));

            // Misc — math.chance / math.random / chat overlay / state.get etc.
            AddT("math.chance",        new ArgSpec("Percent", ArgType.Float), new ArgSpec("ResultVar", ArgType.String));
            AddT("math.random",        new ArgSpec("Min", ArgType.Int), new ArgSpec("Max", ArgType.Int));

            // math.* arithmetic family. These handlers were registered in
            // ScriptManager but missing manifest entries (coverage gaps).
            // They use double-precision arithmetic; ArgType.Double
            // preserves the legacy 64-bit semantics through the binder.
            AddT("math.add",      new ArgSpec("A", ArgType.Double), new ArgSpec("B", ArgType.Double));
            AddT("math.subtract", new ArgSpec("A", ArgType.Double), new ArgSpec("B", ArgType.Double));
            AddT("math.multiply", new ArgSpec("A", ArgType.Double), new ArgSpec("B", ArgType.Double));
            AddT("math.divide",   new ArgSpec("A", ArgType.Double), new ArgSpec("B", ArgType.Double));
            AddT("math.modulo",   new ArgSpec("A", ArgType.Int),    new ArgSpec("B", ArgType.Int));
            AddT("math.clamp",    new ArgSpec("Value", ArgType.Double), new ArgSpec("Min", ArgType.Double), new ArgSpec("Max", ArgType.Double));
            AddT("math.abs",      new ArgSpec("Value", ArgType.Double));
            AddT("math.min",      new ArgSpec("A", ArgType.Double), new ArgSpec("B", ArgType.Double));
            AddT("math.max",      new ArgSpec("A", ArgType.Double), new ArgSpec("B", ArgType.Double));
            AddT("math.floor",    new ArgSpec("Value", ArgType.Double));
            AddT("math.ceil",     new ArgSpec("Value", ArgType.Double));

            // flow.* handlers. Reset is Optional Bool (truthy via
            // the engine's ParseTruthy in the legacy fallback path; the typed binder
            // honors true/false/1/0/yes/no/on/off identically).
            AddT("flow.do_once", new ArgSpec("NodeId", ArgType.String), new ArgSpec("Reset", ArgType.Bool, Optional: true, Default: "false"));
            AddT("flow.do_n",    new ArgSpec("NodeId", ArgType.String), new ArgSpec("N", ArgType.Int), new ArgSpec("Reset", ArgType.Bool, Optional: true, Default: "false"));

            // array.* family (was missing from manifest; engine had handlers).
            // array.make is variadic positional (each arg is an element) — Variadic String.
            // array.contains / array.filter take an Optional ignore-case flag (3rd arg).
            AddT("array.make",     new ArgSpec("Items", ArgType.String, Variadic: true));
            AddT("array.get",      new ArgSpec("List", ArgType.String), new ArgSpec("Index", ArgType.Int));
            AddT("array.length",   new ArgSpec("List", ArgType.String));
            AddT("array.contains", new ArgSpec("List", ArgType.String), new ArgSpec("Search", ArgType.String), new ArgSpec("IgnoreCase", ArgType.String, Optional: true, Default: ""));
            AddT("array.filter",   new ArgSpec("List", ArgType.String), new ArgSpec("Search", ArgType.String), new ArgSpec("IgnoreCase", ArgType.String, Optional: true, Default: ""));
            AddT("array.slice",    new ArgSpec("List", ArgType.String), new ArgSpec("Start", ArgType.Int));
            AddT("array.sort",     new ArgSpec("List", ArgType.String), new ArgSpec("Numeric", ArgType.String, Optional: true, Default: "false"));
            AddT("array.shuffle",  new ArgSpec("List", ArgType.String));
            AddT("array.reverse",  new ArgSpec("List", ArgType.String));
            AddT("array.unique",   new ArgSpec("List", ArgType.String));

            // text.parse_command (was missing from manifest).
            // Second arg named "Segments" to match the Text.ParseCommand node
            // template socket (NodeRegistry.Templates.DataUtilities.cs) and the handler's
            // bound.Get key (ScriptManager.Text.cs); previously "N", which never bound the
            // wired/inline Segments value through the typed binder.
            AddT("text.parse_command", new ArgSpec("Message", ArgType.String), new ArgSpec("Segments", ArgType.Int));

            // wait_for_event (was missing from manifest).
            AddT("wait_for_event", new ArgSpec("EventName", ArgType.String), new ArgSpec("TimeoutMS", ArgType.Int, Optional: true, Default: "10000"));

            // Chat.WaitForNext / Chat.PeekRecent — non-event chat input. Both are
            // imperative-style with per-node result-var names so two instances can
            // coexist in one script. Filters and N are positional inputs; the
            // trailing var-name args are emitted by the dedicated handlers.
            AddT("chat.wait_for_next",
                new ArgSpec("UserFilter",    ArgType.String),
                new ArgSpec("CommandFilter", ArgType.String),
                new ArgSpec("TimeoutMS",     ArgType.Int, Optional: true, Default: "30000"),
                new ArgSpec("OkVar",         ArgType.String, Optional: true, Default: ""),
                new ArgSpec("UserVar",       ArgType.String, Optional: true, Default: ""),
                new ArgSpec("MsgVar",        ArgType.String, Optional: true, Default: ""));
            AddT("chat.peek_recent",
                new ArgSpec("N",        ArgType.Int),
                new ArgSpec("UsersVar", ArgType.String, Optional: true, Default: ""),
                new ArgSpec("MsgsVar",  ArgType.String, Optional: true, Default: ""));

            // twitch.get_viewers (was missing from manifest).
            AddT("twitch.get_viewers", new ArgSpec("ResultVar", ArgType.String));

            // Log (legacy single-arg log; distinct from system.log which has Level).
            AddT("log", new ArgSpec("Message", ArgType.String));
            AddT("delay_seconds",      new ArgSpec("Seconds", ArgType.Float));
            AddT("timeout_check",      new ArgSpec("MS", ArgType.Int));
            // Time.SecondsSinceLastFire(key) is a side-effecting pure-data
            // probe: returns elapsed seconds since the last call against `key`,
            // then updates the per-key timestamp. Engine handler in ScriptManager.
            AddT("time.seconds_since_last_fire", new ArgSpec("Key", ArgType.String));
            AddT("state.get",          new ArgSpec("Name", ArgType.String));
            AddT("state.exists",       new ArgSpec("Name", ArgType.String));
            AddT("state.delete",       new ArgSpec("Name", ArgType.String));
            AddT("state.list_keys");
            AddT("queue.pop",          new ArgSpec("EventIDVar", ArgType.String), new ArgSpec("PayloadVar", ArgType.String));
            AddT("queue.length",       new ArgSpec("ResultVar", ArgType.String));
            AddT("chat.overlay.push",  new ArgSpec("WidgetID", ArgType.String), new ArgSpec("Username", ArgType.String), new ArgSpec("Message", ArgType.String), new ArgSpec("Color", ArgType.String));
            AddT("chat.overlay.clear", new ArgSpec("WidgetID", ArgType.String));
            // visual.trigger is the manifest's hand-authored alias for
            // visual.trigger_queued; it accepts the same trailing key=val pairs so
            // a script (and the in-handler dict-build loop, prior to migration)
            // could surface event data on the addressed pipeline. Variadic KvPairs
            // matches visual.trigger_queued's schema exactly.
            AddT("visual.trigger",
                new ArgSpec("LayerID", ArgType.String),
                new ArgSpec("WidgetID", ArgType.String),
                new ArgSpec("TriggerName", ArgType.String),
                new ArgSpec("EventData", ArgType.KvPairs, Variadic: true));
            AddT("visual.set_text",    new ArgSpec("Id", ArgType.String), new ArgSpec("Value", ArgType.String));
            AddT("visual.set_visible", new ArgSpec("Widget", ArgType.String), new ArgSpec("Visible", ArgType.Bool));
            AddT("visual.set_property",new ArgSpec("Widget", ArgType.String), new ArgSpec("Key", ArgType.String), new ArgSpec("Value", ArgType.String));
            AddT("twitch.last_active", new ArgSpec("User", ArgType.String), new ArgSpec("ThresholdMins", ArgType.Int), new ArgSpec("InactiveVar", ArgType.String), new ArgSpec("MinutesAgoVar", ArgType.String));

            // Copy into a ConcurrentDictionary preserving the Ordinal comparer.
            return new ConcurrentDictionary<string, CommandSpec>(dict, System.StringComparer.Ordinal);
        }

        /// <summary>
        /// Bidirectional manifest audit (Hub → Manifest direction).
        ///
        /// The Hub-side <c>VerifyCommandManifest</c> in <c>ScriptManager</c> only
        /// asks "is every manifest entry implemented by the engine?". The reverse
        /// question — "does every command the Hub registers have a manifest entry
        /// (so the schema is documented and the Architect-side cross-checks can
        /// see it)?" — was never answered. This method answers it.
        ///
        /// Shared cannot reference Hub, so this takes the registered names as a
        /// parameter. A Hub-side test or startup hook should pass
        /// <c>_engine.RegisteredCommandNames</c> (or equivalent) here. Returns
        /// the set of Hub-registered command names that have no exact-case entry
        /// in <see cref="All"/> — empty result means the manifest fully documents
        /// the Hub's command surface.
        /// </summary>
        public static IReadOnlyCollection<string> VerifyAllHubCommandsRegistered(
            IEnumerable<string> hubCommandNames)
        {
            if (hubCommandNames is null)
                throw new System.ArgumentNullException(nameof(hubCommandNames));

            var missing = new List<string>();
            foreach (var name in hubCommandNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                // Exact-case match — mirrors the case-sensitive comparer the
                // dictionary itself uses. Case-only differences are
                // distinct commands per the script grammar, so a Hub-registered
                // name that differs only in casing is still a missing entry.
                if (!_all.ContainsKey(name))
                    missing.Add(name);
            }
            return missing;
        }

        /// <summary>
        /// Produce a near-match suggestion for a command name miss. Used by
        /// VerifyCommandManifest's error message and at the script-engine fallback
        /// to help users recognise typos like `streamerbot.do_action` vs the legacy
        /// `streamer_bot.do_action`.
        /// </summary>
        public static string? SuggestNearMatch(string missingName)
        {
            if (string.IsNullOrWhiteSpace(missingName)) return null;
            string best = "";
            int bestDist = int.MaxValue;
            foreach (var name in _all.Keys)
            {
                int d = Levenshtein(missingName, name);
                if (d < bestDist) { bestDist = d; best = name; }
            }
            // Only suggest within a reasonable edit distance to avoid noise.
            return bestDist <= System.Math.Max(2, missingName.Length / 3) ? best : null;
        }

        private static int Levenshtein(string a, string b)
        {
            if (a == b) return 0;
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;
            var prev = new int[b.Length + 1];
            var curr = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                    curr[j] = System.Math.Min(System.Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                System.Array.Copy(curr, prev, prev.Length);
            }
            return prev[b.Length];
        }
    }
}
