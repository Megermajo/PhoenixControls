// ExporterRegistrations methods carved from ExporterRegistry.cs ().
// Owns: all Register* methods that populate the built-in handler registry —
//   RegisterAllBuiltIns (orchestrator), RegisterObs, RegisterAsyncSimple,
//   RegisterProcessSession, RegisterDatabankSimple, RegisterImperative,
//   RegisterBus, RegisterTwitchSimple, RegisterCollectionsValuesVars,
//   RegisterPlatformsRest, RegisterVisualsDatabankAi, RegisterTwitchData,
//   RegisterSystem.

using System;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Architect.Core
{    public static partial class ExporterRegistrations
    {
        // Populated incrementally as cases migrate out of ScriptExporter's
        // per-category switches. Each call here represents one node whose
        // exporter behaviour is now declarative or imperatively encapsulated.
        //
        // L21 — Sovereign.Webhook node template + dead `on_event(Sovereign.Webhook)`
        // export arm have both been removed from NodeRegistry.cs and ScriptExporter.cs.
        // Absence is pinned by BugFixSweep8_ExporterRegistry_Tests.L21_*.
        public static void RegisterAllBuiltIns(ExporterRegistry registry)
        {
            RegisterBus(registry);
            RegisterTwitchSimple(registry);
            RegisterObs(registry);
            RegisterCollectionsValuesVars(registry);
            RegisterPlatformsRest(registry);
            RegisterVisualsDatabankAi(registry);
            RegisterTwitchData(registry);
            RegisterSystem(registry);
            RegisterAsyncSimple(registry);
            RegisterProcessSession(registry);
            RegisterDatabankSimple(registry);
            RegisterImperative(registry);
            RegisterGiveaway(registry);
        }

        // ── OBS proxy nodes ────────────────────────────────────────────────
        // Routed via Streamer.bot's DoAction relay until the Hub gains direct
        // OBS-WebSocket access. Every node is a flow-bearing exec node with a
        // single "Done" continuation socket, matching the established pattern
        // for Twitch.* moderator-action nodes (Timeout / Ban / Whisper / etc.).
        private static void RegisterObs(ExporterRegistry r)
        {
            r.RegisterSimple(new SimpleEmitDescriptor(
                "OBS.SetScene", "obs.set_scene",
                new[] { new SocketArg("Scene", "\"\"") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "OBS.SetSourceVisible", "obs.set_source_visible",
                new[]
                {
                    new SocketArg("Scene",   "\"\""),
                    new SocketArg("Source",  "\"\""),
                    new SocketArg("Visible", "\"true\""),
                },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "OBS.RefreshBrowserSource", "obs.refresh_browser_source",
                new[]
                {
                    new SocketArg("Scene",  "\"\""),
                    new SocketArg("Source", "\"\""),
                    new SocketArg("Url",    "\"\""),
                },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "OBS.StartRecording", "obs.start_recording",
                System.Array.Empty<SocketArg>(),
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "OBS.StopRecording", "obs.stop_recording",
                System.Array.Empty<SocketArg>(),
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "OBS.StartStreaming", "obs.start_streaming",
                System.Array.Empty<SocketArg>(),
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "OBS.StopStreaming", "obs.stop_streaming",
                System.Array.Empty<SocketArg>(),
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "OBS.SaveReplayBuffer", "obs.save_replay_buffer",
                System.Array.Empty<SocketArg>(),
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "OBS.SetSourcePosition", "obs.set_source_position",
                new[]
                {
                    new SocketArg("Scene",  "\"\""),
                    new SocketArg("Source", "\"\""),
                    new SocketArg("X",      "0"),
                    new SocketArg("Y",      "0"),
                },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "OBS.SetSourceScale", "obs.set_source_scale",
                new[]
                {
                    new SocketArg("Scene",  "\"\""),
                    new SocketArg("Source", "\"\""),
                    new SocketArg("ScaleX", "1"),
                    new SocketArg("ScaleY", "1"),
                },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "OBS.SetSourceRotation", "obs.set_source_rotation",
                new[]
                {
                    new SocketArg("Scene",   "\"\""),
                    new SocketArg("Source",  "\"\""),
                    new SocketArg("Degrees", "0"),
                },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "OBS.SetFilterVisible", "obs.set_filter_visible",
                new[]
                {
                    new SocketArg("Scene",   "\"\""),
                    new SocketArg("Source",  "\"\""),
                    new SocketArg("Filter",  "\"\""),
                    new SocketArg("Visible", "\"true\""),
                },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "OBS.TakeScreenshot", "obs.take_screenshot",
                new[]
                {
                    new SocketArg("Scene",  "\"\""),
                    new SocketArg("Source", "\"\""),
                    new SocketArg("Path",   "\"\""),
                },
                FollowNamedOutput: "Done"));
        }

        // ── Async (Async.Delay only — others are imperative) ───────────────
        private static void RegisterAsyncSimple(ExporterRegistry r)
        {
            r.RegisterSimple(new SimpleEmitDescriptor(
                "Async.Delay", "delay",
                new[] { new SocketArg("MS", "1000") },
                FollowNamedOutput: "Done"));
        }

        // ── Process — unified async-spawn primitive ────────────────────────
        // Process.Host / Session.* retired in the unification sweep:
        //   - Process.Spawn rides ProcessSpawnHandler (imperative, registered
        //     elsewhere) so it can inline-expand the process body inside a
        //     `process_spawn:` block the engine recognizes natively.
        //   - Process.Entry / Process.Exit are authoring-time markers used by
        //     the exporter to surface var-in/var-out shapes; they emit no
        //     runtime calls (parallel to Macro.Entry / Macro.Exit).
        //   - Process.Terminate stays a SimpleEmit — fixed (id) shape.
        private static void RegisterProcessSession(ExporterRegistry r)
        {
            // Process.Stop — fixed (id) shape. Stops a live instance by id.
            r.RegisterSimple(new SimpleEmitDescriptor(
                "Process.Stop", "process.stop",
                new[] { new SocketArg("InstanceId", "\"\"") },
                FollowNamedOutput: "Done"));

            // Deprecated alias — legacy Process.Terminate nodes / hand-written .phx.
            r.RegisterSimple(new SimpleEmitDescriptor(
                "Process.Terminate", "process.terminate",
                new[] { new SocketArg("InstanceId", "\"\"") },
                FollowNamedOutput: "Done"));
        }

        // ── Databank simple emits ──────────────────────────────────────────
        private static void RegisterDatabankSimple(ExporterRegistry r)
        {
            r.RegisterSimple(new SimpleEmitDescriptor(
                "DB.ClearTable", "db.clear_table",
                new[] { new SocketArg("TableName", "\"\"") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "DB.DeleteVar", "db.delete_var",
                new[] { new SocketArg("Key", "\"key\"") },
                FollowNamedOutput: "Done"));
        }

        // ── Imperative handlers (branching / loop / event-trigger / multi-emit) ──
        private static void RegisterImperative(ExporterRegistry r)
        {
            // Flow Control
            r.Register(new BranchHandler());
            r.Register(new IfHandler());
            r.Register(new SwitchHandler());
            r.Register(new SequenceHandler());
            r.Register(new FlipFlopHandler());
            r.Register(new DoOnceHandler());
            r.Register(new DoNHandler());
            r.Register(new ForLoopHandler());
            r.Register(new WhileLoopHandler());
            r.Register(new CooldownHandler());
            r.Register(new IsValidHandler());
            r.Register(new RerouteHandler());
            r.Register(new DelayHandler());
            r.Register(new EnumMatchHandler());
            r.Register(new ForEachHandler());

            // Async
            r.Register(new WaitForVisualHandler());
            r.Register(new WaitForEventHandler());
            r.Register(new ChatWaitForNextHandler());
            r.Register(new ChatPeekRecentHandler());
            r.Register(new TimeoutHandler());
            r.Register(new ParallelHandler());
            r.Register(new JoinHandler());

            // Queue
            r.Register(new QueuePopHandler());
            // Queue.Length is pure-data; handled via ComputeInlineValue instead.

            // Process/Collections
            r.Register(new ArrayUnpackHandler());

            // Databank (multi-emit / branching)
            r.Register(new DbGetVariableHandler());
            r.Register(new DbSetVariableHandler());
            r.Register(new DbIncrementHandler());
            r.Register(new DbCheckExistsHandler());
            r.Register(new DbFindRowHandler());
            r.Register(new DbSetCellHandler());
            r.Register(new DbInsertRowHandler());
            r.Register(new DbDeleteRowHandler());
            r.Register(new DbFetchRowHandler());

            // Visual / State / Variables / Math.Chance
            r.Register(new VisualTriggerHandler());
            r.Register(new StateSwitchHandler());
            r.Register(new VarSetHandler());
            r.Register(new PublicSetHandler());
            r.Register(new MathChanceHandler());

            // Twitch Data (id-derived state)
            r.Register(new TwitchLastActiveHandler());
            r.Register(new TwitchGetViewersHandler());

            // Inline events
            r.Register(new EventTriggerHandler());
            r.Register(new EventReturnHandler());

            // Macros
            r.Register(new MacroCallHandler());

            // Processes — live instances (Process.Start) + the deprecated
            // fire-and-forget Process.Spawn (kept for back-compat / coverage).
            r.Register(new ProcessStartHandler());
            r.Register(new ProcessSpawnHandler());
            r.Register(new ProcessEntryHandler());
            r.Register(new ProcessExitHandler());

            // HTTP.ParseJson is pure-data; resolved inline via ComputeInlineValue
            // and the dedicated hoist branch in ResolveOutputFromNode. No flow
            // handler needed.
        }

        // ── Bus ────────────────────────────────────────────────────────────
        private static void RegisterBus(ExporterRegistry r)
        {
            r.RegisterSimple(new SimpleEmitDescriptor(
                NodeTitle: "Bus.Send",
                CommandName: "bus.send",
                Args: new[]
                {
                    new SocketArg("Target",  "\"Visualist\""),
                    new SocketArg("Type",    "\"HUB_EVENT\""),
                    new SocketArg("Payload", "\"{}\""),
                },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                NodeTitle: "Bus.Broadcast",
                CommandName: "bus.broadcast",
                Args: new[]
                {
                    new SocketArg("Type",    "\"HUB_EVENT\""),
                    new SocketArg("Payload", "\"{}\""),
                },
                FollowNamedOutput: "Done"));
        }

        // ── Twitch (simple emits in the Platforms category) ────────────────
        private static void RegisterTwitchSimple(ExporterRegistry r)
        {
            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.SendChat", "twitch.send_chat",
                new[] { new SocketArg("Message", "\"Hello!\"") },
                FollowNamedOutput: "Done"));

            // M33 (sweep 8) — sweep 7 added a "Done" Flow output socket to the
            // NodeRegistry templates for Twitch.Timeout / Twitch.Ban /
            // Twitch.Announcement so downstream work can chain off these calls
            // (matches the pattern set by Twitch.SendChat's "Done" output and
            // Twitch.Shoutout's "Flow" output). The exporter side was left as a
            // follow-up: without FollowNamedOutput here, the descriptor falls
            // through to the default fallback (FollowFlow), which looks up an
            // output literally named "Flow" on the node — these three nodes don't
            // have one, so flow terminated silently and any downstream nodes
            // wired to Done were dropped from the .phx output.
            //
            // Wire each handler through the "Done" socket explicitly so flow
            // continues from the new Done port, mirroring the established Twitch.*
            // pattern (e.g. Twitch.SendChat â†’ "Sent", Process.Host â†’ "Done").
            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.Timeout", "twitch.timeout",
                new[]
                {
                    new SocketArg("User", "{user.name}"),
                    new SocketArg("Sec",  "60"),
                },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.Ban", "twitch.ban",
                new[]
                {
                    new SocketArg("User",   "{user.name}"),
                    new SocketArg("Reason", "\"no reason\""),
                },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.Shoutout", "twitch.shoutout",
                new[] { new SocketArg("User", "{user.name}") }));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.Announcement", "twitch.announcement",
                new[] { new SocketArg("Message", "\"\"") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.CreatePoll", "twitch.create_poll",
                new[]
                {
                    new SocketArg("Title",       "\"\""),
                    new SocketArg("Choices",     "\"\""),
                    new SocketArg("DurationSec", "60"),
                },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.EndPoll", "twitch.end_poll",
                new[] { new SocketArg("PollId", "\"\"") },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.CreatePrediction", "twitch.create_prediction",
                new[]
                {
                    new SocketArg("Title",       "\"\""),
                    new SocketArg("OutcomeA",    "\"\""),
                    new SocketArg("OutcomeB",    "\"\""),
                    new SocketArg("OutcomeC",    "\"\""),
                    new SocketArg("OutcomeD",    "\"\""),
                    new SocketArg("OutcomeE",    "\"\""),
                    new SocketArg("DurationSec", "120"),
                },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.ResolvePrediction", "twitch.resolve_prediction",
                new[]
                {
                    new SocketArg("PredictionId",     "\"\""),
                    new SocketArg("WinningOutcomeId", "\"\""),
                },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.UpdateRewardCost", "twitch.update_reward_cost",
                new[]
                {
                    new SocketArg("RewardId", "\"\""),
                    new SocketArg("Cost",     "0"),
                },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.SetRewardEnabled", "twitch.set_reward_enabled",
                new[]
                {
                    new SocketArg("RewardId", "\"\""),
                    new SocketArg("Enabled",  "true"),
                },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.FulfillRedemption", "twitch.fulfill_redemption",
                new[] { new SocketArg("RedemptionId", "\"\"") },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.RejectRedemption", "twitch.reject_redemption",
                new[] { new SocketArg("RedemptionId", "\"\"") },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.CreateClip", "twitch.create_clip",
                new[]
                {
                    new SocketArg("Duration", "30"),
                    new SocketArg("Title",    "\"\""),
                },
                FollowNamedOutput: "Done"));

            // ── Twitch moderation / channel control (P3) ──────────────────
            // Single-arg user-targeted moderation.
            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.Unban", "twitch.unban",
                new[] { new SocketArg("User", "{user.name}") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.Mod", "twitch.mod",
                new[] { new SocketArg("User", "{user.name}") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.Unmod", "twitch.unmod",
                new[] { new SocketArg("User", "{user.name}") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.Vip", "twitch.vip",
                new[] { new SocketArg("User", "{user.name}") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.Unvip", "twitch.unvip",
                new[] { new SocketArg("User", "{user.name}") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.DeleteMessage", "twitch.delete_message",
                new[] { new SocketArg("MessageId", "{message.id}") },
                FollowNamedOutput: "Done"));

            // Channel-mode toggles. Sentinels: SlowMode 0 = off, FollowerMode -1 = off.
            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.SlowMode", "twitch.slow_mode",
                new[] { new SocketArg("Seconds", "30") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.FollowerMode", "twitch.follower_mode",
                new[] { new SocketArg("Minutes", "10") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.SubOnlyMode", "twitch.sub_only_mode",
                new[] { new SocketArg("Enabled", "true") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.Marker", "twitch.marker",
                new[] { new SocketArg("Description", "\"\"") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.Whisper", "twitch.whisper",
                new[]
                {
                    new SocketArg("User",    "{user.name}"),
                    new SocketArg("Message", "\"\""),
                },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.UpdateChannel", "twitch.update_channel",
                new[]
                {
                    new SocketArg("Title",  "\"\""),
                    new SocketArg("GameId", "\"\""),
                },
                FollowNamedOutput: "Done"));
        }

        // ── Collections / Queue / State (simple emits only) ────────────────
        // Note: Var.Set stays imperative — it emits an assignment statement
        // (`var.{name} = value`) using a node attribute as part of the LHS,
        // not a call.
        private static void RegisterCollectionsValuesVars(ExporterRegistry r)
        {
            // Array.Push moved to an imperative handler so it can capture the
            // engine's returned list (the input list with Value appended) into
            // a per-node global, exposed to downstream nodes via the List output.
            r.Register(new ArrayPushHandler());

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Queue.Push", "queue.push",
                new[]
                {
                    new SocketArg("EventID", "\"event\""),
                    new SocketArg("Payload", "\"{}\""),
                },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Queue.Clear", "queue.clear",
                System.Array.Empty<SocketArg>(),
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "State.Set", "state.set",
                new[]
                {
                    new SocketArg("Name",  "\"\""),
                    new SocketArg("Value", "\"\""),
                },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "State.Delete", "state.delete",
                new[] { new SocketArg("Name", "\"\"") },
                FollowNamedOutput: "Done"));
        }

        // ── Platforms remainder (Discord, HTTP, StreamerBot) ───────────────
        private static void RegisterPlatformsRest(ExporterRegistry r)
        {
            r.RegisterSimple(new SimpleEmitDescriptor(
                "Discord.Webhook", "discord.webhook",
                new[]
                {
                    new SocketArg("URL", "\"\""),
                    new SocketArg("Msg", "\"\""),
                }));

            // P4 — Discord bot REST. Mirrors the http.* shape (FollowNamedOutput =
            // "Flow") so result.discord_* round-trips through ResolveOutputFromNode
            // for the MessageId / Error sockets. Token comes from
            // AppConfig.DiscordBotToken at handler time, not from a graph input.
            r.RegisterSimple(new SimpleEmitDescriptor(
                "Discord.SendMessage", "discord.send_message",
                new[]
                {
                    new SocketArg("ChannelId", "\"\""),
                    new SocketArg("Content",   "\"\""),
                },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Discord.SendEmbed", "discord.send_embed",
                new[]
                {
                    new SocketArg("ChannelId",   "\"\""),
                    new SocketArg("Title",       "\"\""),
                    new SocketArg("Description", "\"\""),
                    new SocketArg("Color",       "\"\""),
                    new SocketArg("Url",         "\"\""),
                },
                FollowNamedOutput: "Flow"));

            // P4 slice 2 — role + reaction + user-lookup nodes.
            r.RegisterSimple(new SimpleEmitDescriptor(
                "Discord.AddRole", "discord.add_role",
                new[]
                {
                    new SocketArg("GuildId", "\"\""),
                    new SocketArg("UserId",  "\"\""),
                    new SocketArg("RoleId",  "\"\""),
                },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Discord.RemoveRole", "discord.remove_role",
                new[]
                {
                    new SocketArg("GuildId", "\"\""),
                    new SocketArg("UserId",  "\"\""),
                    new SocketArg("RoleId",  "\"\""),
                },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Discord.React", "discord.react",
                new[]
                {
                    new SocketArg("ChannelId", "\"\""),
                    new SocketArg("MessageId", "\"\""),
                    new SocketArg("Emoji",     "\"\""),
                },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Discord.GetUser", "discord.get_user",
                new[]
                {
                    new SocketArg("UserId", "\"\""),
                },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "HTTP.Get", "http.get",
                new[]
                {
                    new SocketArg("Url",     "\"\""),
                    new SocketArg("Headers", "\"\""),
                },
                FollowNamedOutput: "Flow"));

            // P3 — File I/O exec emitters. Mirrors the http.* shape so result.file_*
            // round-trips through ResolveOutputFromNode for the Content / Error sockets.
            r.RegisterSimple(new SimpleEmitDescriptor(
                "File.ReadText", "file.read_text",
                new[]
                {
                    new SocketArg("Path", "\"\""),
                },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "File.WriteText", "file.write_text",
                new[]
                {
                    new SocketArg("Path",    "\"\""),
                    new SocketArg("Content", "\"\""),
                    new SocketArg("Append",  "\"false\""),
                },
                FollowNamedOutput: "Flow"));

            //  — File.ReadJSON / File.WriteJSON. Same shape as the
            // text variants; the JSON-validity gate lives Hub-side in
            // ScriptManager.File so the exporter doesn't need extra knobs.
            r.RegisterSimple(new SimpleEmitDescriptor(
                "File.ReadJSON", "file.read_json",
                new[]
                {
                    new SocketArg("Path", "\"\""),
                },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "File.WriteJSON", "file.write_json",
                new[]
                {
                    new SocketArg("Path",    "\"\""),
                    new SocketArg("Content", "\"\""),
                    new SocketArg("Append",  "\"false\""),
                },
                FollowNamedOutput: "Flow"));

            // P3 — Audio.* exec emitters. Mirrors the file.* shape so result.audio_error
            // round-trips through ResolveOutputFromNode for the Error socket. Playback is
            // fire-and-forget on the Hub side (handler returns once decode starts).
            r.RegisterSimple(new SimpleEmitDescriptor(
                "Audio.Play", "audio.play",
                new[]
                {
                    new SocketArg("Path",   "\"\""),
                    new SocketArg("Volume", "\"1.0\""),
                },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Audio.PlayTts", "audio.play_tts",
                new[]
                {
                    new SocketArg("Text",   "\"\""),
                    new SocketArg("Voice",  "\"\""),
                    new SocketArg("Rate",   "\"0\""),
                    new SocketArg("Volume", "\"1.0\""),
                },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Audio.SetVolume", "audio.set_volume",
                new[]
                {
                    new SocketArg("Volume", "\"1.0\""),
                },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "API.Call", "api.call",
                new[]
                {
                    new SocketArg("Url", "\"\""),
                },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "HTTP.Post", "http.post",
                new[]
                {
                    new SocketArg("Url",         "\"\""),
                    new SocketArg("Body",        "\"\""),
                    new SocketArg("ContentType", "\"application/json\""),
                    new SocketArg("Headers",     "\"\""),
                },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "HTTP.Put", "http.put",
                new[]
                {
                    new SocketArg("Url",         "\"\""),
                    new SocketArg("Body",        "\"\""),
                    new SocketArg("ContentType", "\"application/json\""),
                    new SocketArg("Headers",     "\"\""),
                },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "HTTP.Patch", "http.patch",
                new[]
                {
                    new SocketArg("Url",         "\"\""),
                    new SocketArg("Body",        "\"\""),
                    new SocketArg("ContentType", "\"application/json\""),
                    new SocketArg("Headers",     "\"\""),
                },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "HTTP.Delete", "http.delete",
                new[] { new SocketArg("Url", "\"\"") },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "StreamerBot.DoAction", "streamerbot.do_action",
                new[] { new SocketArg("ActionId", "\"\"") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "StreamerBot.GetUser", "streamerbot.get_user",
                new[] { new SocketArg("User", "\"\"") },
                FollowNamedOutput: "Done"));
        }

        // ── Visuals + Databank + AI (simple emits only) ────────────────────
        private static void RegisterVisualsDatabankAi(ExporterRegistry r)
        {
            // Note on AI.Prompt Model fallback: the original passes
            //   $"\"{node.GetAttr("Model", "gpt-4o-mini")}\""
            // into ResolveInputValue's `fallback` parameter. ResolveInputValue
            // routes through node.GetAttr(socketName, fallback), so the same
            // code path is taken when the descriptor passes "\"gpt-4o-mini\"".
            r.RegisterSimple(new SimpleEmitDescriptor(
                "AI.Prompt", "ai.prompt",
                new[]
                {
                    new SocketArg("SystemPrompt", "\"\""),
                    new SocketArg("UserPrompt",   "\"\""),
                    new SocketArg("Model",        "\"gpt-4o-mini\""),
                },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "AI.Moderate", "ai.moderate",
                new[] { new SocketArg("Text", "\"\"") },
                FollowNamedOutput: "Flow"));

            //  — AI.GenerateImage. Calls OpenAI's
            // /v1/images/generations and writes result.ai_image_url +
            // result.ai_image_error + result.ai_image_done. Defaults
            // mirror the manifest: dall-e-3 at 1024x1024.
            r.RegisterSimple(new SimpleEmitDescriptor(
                "AI.GenerateImage", "ai.generate_image",
                new[]
                {
                    new SocketArg("Prompt", "\"\""),
                    new SocketArg("Model",  "\"dall-e-3\""),
                    new SocketArg("Size",   "\"1024x1024\""),
                },
                FollowNamedOutput: "Flow"));

            //  — AI.VisionDescribe. Calls OpenAI's chat completions
            // with a multi-modal user message and writes result.ai_response +
            // result.ai_error. Default Model gpt-4o-mini.
            r.RegisterSimple(new SimpleEmitDescriptor(
                "AI.VisionDescribe", "ai.vision_describe",
                new[]
                {
                    new SocketArg("Prompt",   "\"\""),
                    new SocketArg("ImageUrl", "\"\""),
                    new SocketArg("Model",    "\"gpt-4o-mini\""),
                },
                FollowNamedOutput: "Flow"));

            //  — AI.WithTools. Single-shot OpenAI chat completion
            // with a tools array. Result is either result.ai_response
            // (plain answer) or result.ai_tool_calls (JSON array of
            // {id,name,arguments} when the model chose a tool).
            // QC37-05 — ToolChoice / ParallelToolCalls are optional. Empty-string
            // fallbacks emit "" for unwired sockets; the handler reads args[4]/[5]
            // and treats "" as "omit so the API default applies". Order matches
            // the manifest + handler (args[4]=ToolChoice, args[5]=ParallelToolCalls).
            r.RegisterSimple(new SimpleEmitDescriptor(
                "AI.WithTools", "ai.with_tools",
                new[]
                {
                    new SocketArg("SystemPrompt",      "\"\""),
                    new SocketArg("UserPrompt",        "\"\""),
                    new SocketArg("Tools",             "\"[]\""),
                    new SocketArg("Model",             "\"gpt-4o-mini\""),
                    new SocketArg("ToolChoice",        "\"\""),
                    new SocketArg("ParallelToolCalls", "\"\""),
                },
                FollowNamedOutput: "Flow"));

            //  — AI.StreamText. Hub-side handler streams Server-
            // Sent Events from the routed provider (claude* → Anthropic;
            // ollama/<name> → local; cerebras/<name> → Cerebras; else
            // OpenAI) and accumulates result.ai_response cumulatively
            // while broadcasting AI_CHUNK Bus events per delta.
            //  — MemoryVar (optional) names a Var key
            // whose JSON-array-of-{role,content} payload is prepended
            // to the request as prior turns and updated after completion.
            r.RegisterSimple(new SimpleEmitDescriptor(
                "AI.StreamText", "ai.stream_text",
                new[]
                {
                    new SocketArg("SystemPrompt", "\"\""),
                    new SocketArg("UserPrompt",   "\"\""),
                    new SocketArg("Model",        "\"gpt-4o-mini\""),
                    new SocketArg("MemoryVar",    "\"\""),
                },
                FollowNamedOutput: "Flow"));

            // Sweep 25 — chat-overlay broadcast pair. Mirrors sweep-19's
            // ScriptManager.RegisterCommand("chat.overlay.push") /
            // RegisterCommand("chat.overlay.clear") signatures so the .phxg-
            // authored emit matches the runtime command exactly. Color
            // fallback "" lets ScriptManager apply its own default ("#7fff7f").
            r.RegisterSimple(new SimpleEmitDescriptor(
                "Chat.Overlay.Push", "chat.overlay.push",
                new[]
                {
                    new SocketArg("WidgetID", "\"\""),
                    new SocketArg("Username", "\"\""),
                    new SocketArg("Message",  "\"\""),
                    new SocketArg("Color",    "\"\""),
                },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Chat.Overlay.Clear", "chat.overlay.clear",
                new[] { new SocketArg("WidgetID", "\"\"") },
                FollowNamedOutput: "Done"));

            // D2 — low-level HUD mutation emits. Mirror the ScriptManager.Visual.cs
            // RegisterCommand("visual.set_text" / "visual.set_visible" /
            // "visual.set_property") signatures so the .phxg-authored emit matches
            // the runtime command (and CommandManifest.cs arg order) exactly. The
            // matching templates live in NodeRegistry.Templates.RemainingBands.cs.
            // Visible defaults to "true" — the bool's literal fallback when the
            // socket is left unwired, matching the template's inline pill.
            r.RegisterSimple(new SimpleEmitDescriptor(
                "Visual.SetText", "visual.set_text",
                new[]
                {
                    new SocketArg("Id",    "\"\""),
                    new SocketArg("Value", "\"\""),
                },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Visual.SetVisible", "visual.set_visible",
                new[]
                {
                    new SocketArg("Widget",  "\"\""),
                    new SocketArg("Visible", "\"true\""),
                },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Visual.SetProperty", "visual.set_property",
                new[]
                {
                    new SocketArg("Widget", "\"\""),
                    new SocketArg("Key",    "\"\""),
                    new SocketArg("Value",  "\"\""),
                },
                FollowNamedOutput: "Done"));
        }

        // ── Twitch Data (Username-only simple emits) ───────────────────────
        // Twitch.LastActive and Twitch.GetViewers stay imperative — they
        // generate id-derived global temp vars and Inactive/Active branches.
        private static void RegisterTwitchData(ExporterRegistry r)
        {
            var userArg = new[] { new SocketArg("Username", "{user.name}") };

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.GetUser", "twitch.get_user", userArg,
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.GetStream", "twitch.get_stream", userArg,
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.CheckRole", "twitch.check_role", userArg,
                FollowNamedOutput: "Flow"));

            // Twitch.IsOnline — optional Channel (blank → broadcaster). The IsLive
            // output resolves to {stream.is_live} via ResolveOutputFromNode.
            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.IsOnline", "twitch.is_online",
                new[] { new SocketArg("Channel", "\"\"") },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.GetFollowAge", "twitch.get_follow_age", userArg,
                FollowNamedOutput: "Flow"));
        }

        // ── System ─────────────────────────────────────────────────────────
        private static void RegisterSystem(ExporterRegistry r)
        {
            // System.Log Level fallback: original passes node.GetAttr("Level",
            // "LogicExecution") as ResolveInputValue's fallback. Since
            // ResolveInputValue's no-link path routes through
            // node.GetAttr(socketName, fallback), passing "LogicExecution"
            // directly produces identical output (attr if set, fallback else).
            r.RegisterSimple(new SimpleEmitDescriptor(
                "System.Log", "system.log",
                new[]
                {
                    new SocketArg("Message", "\"log\""),
                    new SocketArg("Level",   "LogicExecution"),
                },
                FollowNamedOutput: "Flow"));
        }
    }
}