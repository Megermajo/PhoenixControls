// ExporterRegistrations methods carved from ExporterRegistry.cs ().
// Owns: all Register* methods that populate the built-in handler registry —
//   RegisterAllBuiltIns (orchestrator), RegisterObs, RegisterAsyncSimple,
//   RegisterProcessSession, RegisterDatabankSimple, RegisterImperative,
//   RegisterBus, RegisterTwitchSimple, RegisterCollectionsValuesVars,
//   RegisterPlatformsRest, RegisterVisualsDatabankAi, RegisterTwitchData,
//   RegisterYouTubeKick, RegisterSystem.

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
            RegisterYouTubeKick(registry);
            RegisterSystem(registry);
            RegisterAsyncSimple(registry);
            RegisterProcessSession(registry);
            RegisterDatabankSimple(registry);
            RegisterImperative(registry);
            RegisterGiveaway(registry);
            RegisterTimer(registry);
            RegisterSongRequest(registry);
            RegisterPolls(registry);
            RegisterRanks(registry);          // ExporterRegistry.Ranks.cs
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

            // Macros — Call inlines the body; Entry/Exit are the boundary
            // markers (Exit binds the return slots + terminates the body,
            // parallel to ProcessEntryHandler / ProcessExitHandler).
            r.Register(new MacroCallHandler());
            r.Register(new MacroEntryHandler());
            r.Register(new MacroExitHandler());

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
            // The unified outbound chat node (Chat.Message's send-side mirror).
            // Emission contract lives in ChatSendHandler: a single checked
            // platform with no override collapses onto the legacy per-platform
            // command byte-identically; everything else emits chat.send(...).
            r.Register(new ChatSendHandler());

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
                    new SocketArg("WinningOutcome", "0"),
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

            // Fulfill/Reject take no visible inputs — both ids auto-source from the
            // ambient Twitch.PointRedeem event (the fallback tokens below resolve at
            // runtime when the graph is redemption-triggered; empty otherwise).
            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.FulfillRedemption", "twitch.fulfill_redemption",
                new[] { new SocketArg("RewardId", "{event.reward_id}"), new SocketArg("RedemptionId", "{event.redemption_id}") },
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.RejectRedemption", "twitch.reject_redemption",
                new[] { new SocketArg("RewardId", "{event.reward_id}"), new SocketArg("RedemptionId", "{event.redemption_id}") },
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
                new[] { new SocketArg("MessageId", "{event.message_id}") },
                FollowNamedOutput: "Done"));

            // twitch.reply(messageId, message) — mirrors Kick.Reply. The unwired
            // MessageId default is {event.message_id} (the Chat.Message trigger's
            // own id), so a bare Twitch.Reply replies to the triggering message.
            r.RegisterSimple(new SimpleEmitDescriptor(
                "Twitch.Reply", "twitch.reply",
                new[]
                {
                    new SocketArg("MessageId", "{event.message_id}"),
                    new SocketArg("Message",   "\"\""),
                },
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

            // Queue.Push / Queue.Clear / Queue.Remove are HANDLERS, not descriptors, and
            // that is load-bearing rather than stylistic: each carries an optional
            // trailing Name (selecting a NAMED queue over the legacy unnamed pipe-string)
            // and SimpleEmitHandler.Emit writes every descriptor arg positionally with no
            // omit-at-default. As descriptors they would have rewritten queue.push(a, b)
            // into queue.push(a, b, "") in every shipped graph and churned every affected
            // golden. See the band header in ExporterRegistry.Handlers2.cs.
            r.Register(new QueuePushHandler());
            r.Register(new QueueClearHandler());
            r.Register(new QueueRemoveHandler());

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

            // File.ReadJSON / File.WriteJSON. Same shape as the
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

            // AI.GenerateImage. Calls OpenAI's
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

            // AI.VisionDescribe. Calls OpenAI's chat completions
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

            // AI.WithTools. Single-shot OpenAI chat completion
            // with a tools array. Result is either result.ai_response
            // (plain answer) or result.ai_tool_calls (JSON array of
            // {id,name,arguments} when the model chose a tool).
            // ToolChoice / ParallelToolCalls are optional. Empty-string
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

            // AI.StreamText. Hub-side handler streams Server-
            // Sent Events from the routed provider (claude* → Anthropic;
            // ollama/<name> → local; cerebras/<name> → Cerebras; else
            // OpenAI) and accumulates result.ai_response cumulatively
            // while broadcasting AI_CHUNK Bus events per delta.
            // MemoryVar (optional) names a Var key
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

            // Overlay.Publish — the author-facing write into the Overlay Live Channel.
            // Fixed two-arg shape, so a plain SimpleEmitDescriptor covers it; the
            // matching template lives in NodeRegistry.Templates.RemainingBands.cs's
            // VISUALS band and the handler in ScriptManager.Overlay.cs.
            //
            // Its sibling Overlay.Get is deliberately NOT a descriptor: it emits no
            // flow line at all. "Visuals" is not one of ScriptExporter's pure-data
            // categories (this band's other nodes must keep emitting flow), so the
            // value node is routed by TITLE instead — see the inline-title list in
            // ScriptExporter.ResolveOutputFromNode plus its ComputeInlineValue arm.
            r.RegisterSimple(new SimpleEmitDescriptor(
                "Overlay.Publish", "overlay.publish",
                new[]
                {
                    new SocketArg("Key",   "\"\""),
                    new SocketArg("Value", "\"\""),
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

            // User.GetGroups (category "Users") — the User-Management group lookup.
            // Same shape as Twitch.GetUser: one emitted line, results come back as
            // bare result vars (group.moderator/vip/subscriber/regular + one
            // group.<sanitized> per custom-group socket — see the "Users" arm in
            // ScriptExporter.ResolveOutputFromNode).
            r.RegisterSimple(new SimpleEmitDescriptor(
                "User.GetGroups", "usermgmt.get_groups", userArg,
                FollowNamedOutput: "Flow"));
        }

        // ── YouTube / Kick platform nodes ─────────────────────────────
        // Outbound actions ("Platforms" category, DarkViolet) plus the two
        // "Platform Data" lookups. Templates live in
        // NodeRegistry.Templates.RemainingBands.cs; runtime handlers in
        // ScriptManager.YouTube.cs / ScriptManager.Kick.cs; the manifest
        // entries in CommandManifest.cs — command names + arg order here
        // are the locked contract between all three.
        //
        // Action nodes follow the Twitch.* action pattern (flow continues
        // through the "Done" output). The *.GetUser data nodes mirror the
        // Twitch.GetUser descriptor shape exactly (emit + FollowNamedOutput
        // "Flow"); their output sockets (Id / DisplayName / IsMod / ...)
        // resolve to result vars in ScriptExporter.ResolveOutputFromNode's
        // "Platform Data" arm.
        private static void RegisterYouTubeKick(ExporterRegistry r)
        {
            // ── YouTube actions ────────────────────────────────────────────
            r.RegisterSimple(new SimpleEmitDescriptor(
                "YouTube.SendChat", "youtube.send_chat",
                new[] { new SocketArg("Message", "\"Hello chat!\"") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "YouTube.SetTitle", "youtube.set_title",
                new[] { new SocketArg("Title", "\"\"") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "YouTube.SetDescription", "youtube.set_description",
                new[] { new SocketArg("Description", "\"\"") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "YouTube.Timeout", "youtube.timeout",
                new[]
                {
                    new SocketArg("User", "{user.name}"),
                    new SocketArg("Sec",  "300"),
                },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "YouTube.Ban", "youtube.ban",
                new[] { new SocketArg("User", "{user.name}") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "YouTube.CreatePoll", "youtube.create_poll",
                new[]
                {
                    new SocketArg("Title",   "\"\""),
                    new SocketArg("Choices", "\"\""),
                },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "YouTube.EndPoll", "youtube.end_poll",
                System.Array.Empty<SocketArg>(),
                FollowNamedOutput: "Done"));

            // ── Kick actions ───────────────────────────────────────────────
            r.RegisterSimple(new SimpleEmitDescriptor(
                "Kick.SendChat", "kick.send_chat",
                new[] { new SocketArg("Message", "\"Hello chat!\"") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Kick.Reply", "kick.reply",
                new[]
                {
                    new SocketArg("MessageId", "{event.message_id}"),
                    new SocketArg("Message",   "\"\""),
                },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Kick.Timeout", "kick.timeout",
                new[]
                {
                    new SocketArg("User", "{user.name}"),
                    new SocketArg("Sec",  "300"),
                },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Kick.Ban", "kick.ban",
                new[]
                {
                    new SocketArg("User",   "{user.name}"),
                    new SocketArg("Reason", "\"no reason\""),
                },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Kick.Unban", "kick.unban",
                new[] { new SocketArg("User", "{user.name}") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Kick.Untimeout", "kick.untimeout",
                new[] { new SocketArg("User", "{user.name}") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Kick.SetTitle", "kick.set_title",
                new[] { new SocketArg("Title", "\"\"") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Kick.SetCategory", "kick.set_category",
                new[] { new SocketArg("Category", "\"\"") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Kick.DeleteMessage", "kick.delete_message",
                new[] { new SocketArg("MessageId", "{event.message_id}") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Kick.SetRewardCost", "kick.set_reward_cost",
                new[]
                {
                    new SocketArg("RewardId", "\"\""),
                    new SocketArg("Cost",     "0"),
                },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Kick.SetRewardEnabled", "kick.set_reward_enabled",
                new[]
                {
                    new SocketArg("RewardId", "\"\""),
                    new SocketArg("Enabled",  "true"),
                },
                FollowNamedOutput: "Done"));

            // ── Platform Data lookups ──────────────────────────────────────
            // Same shape as Twitch.GetUser above: emit the command, then flow
            // continues through the exec "Flow" output; data sockets resolve
            // via ScriptExporter.ResolveOutputFromNode ("Platform Data" arm).
            var platformUserArg = new[] { new SocketArg("Username", "{user.name}") };

            r.RegisterSimple(new SimpleEmitDescriptor(
                "YouTube.GetUser", "youtube.get_user", platformUserArg,
                FollowNamedOutput: "Flow"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Kick.GetUser", "kick.get_user", platformUserArg,
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

        // ── Timer (subathon countdown — void control nodes) ────────────────
        // One SimpleEmitDescriptor per void node, flow continuing through the
        // "Done" output. Mirrors the State.Set / Queue.Push descriptor shape.
        // SocketArg order == CommandManifest arg order (the locked three-way
        // contract). An empty Name resolves to the default timer at runtime
        // (TimerService.ResolveSlug) — same "empty selector = default" convention
        // Giveaway uses. Duration/Amount/Multiplier/Scope are STRING duration
        // args parsed by the Hub's ParseDurationToMs, so their fallbacks are
        // quoted literals. The Timer.Get* value nodes are inline pure-data and
        // are handled in ScriptExporter.ComputeInlineValue instead — no
        // descriptor here (and "Timer" is intentionally NOT a _pureDataCategory,
        // so these void nodes still emit flow).
        private static void RegisterTimer(ExporterRegistry r)
        {
            r.RegisterSimple(new SimpleEmitDescriptor(
                "Timer.Start", "timer.start",
                new[]
                {
                    new SocketArg("Name",     "\"\""),
                    new SocketArg("Duration", "\"\""),
                },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Timer.Stop", "timer.stop",
                new[] { new SocketArg("Name", "\"\"") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Timer.Pause", "timer.pause",
                new[] { new SocketArg("Name", "\"\"") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Timer.Resume", "timer.resume",
                new[] { new SocketArg("Name", "\"\"") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Timer.Toggle", "timer.toggle",
                new[] { new SocketArg("Name", "\"\"") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Timer.Reset", "timer.reset",
                new[] { new SocketArg("Name", "\"\"") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Timer.Add", "timer.add",
                new[]
                {
                    new SocketArg("Name",   "\"\""),
                    new SocketArg("Amount", "\"5m\""),
                },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Timer.SetTime", "timer.set_time",
                new[]
                {
                    new SocketArg("Name",   "\"\""),
                    new SocketArg("Amount", "\"1h\""),
                },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Timer.SetHappyHour", "timer.set_happy_hour",
                new[]
                {
                    new SocketArg("Name",       "\"\""),
                    new SocketArg("Multiplier", "\"2\""),
                    new SocketArg("Duration",   "\"10m\""),
                    new SocketArg("Scope",      "\"all\""),
                },
                FollowNamedOutput: "Done"));
        }

        // ── Counters / Quotes — RETIRED (2026-08 tool-node cut) ────────────
        // The Counter.* and Quote.* void control nodes were removed from the
        // palette: both bands wrap OPEN tables, so graphs use the generic DB.*
        // band instead. Their descriptors went with them; loaded graphs shed the
        // titles via GraphSerializer.DropRetiredToolNodes and old .phx lines land
        // on ScriptManager.RetiredCommands shims. The surviving Counter.OnChanged
        // / Quote.OnAdded roots are Events nodes (generic on_event fallback) and
        // never had descriptors here.

        // ── Song Request (YouTube request queue — void control nodes) ──────
        // One SimpleEmitDescriptor per void node, flow continuing through the "Done"
        // output. SocketArg order == CommandManifest arg order (the locked three-way
        // contract). The four value nodes — Song.Current / Song.UpNext /
        // Song.QueueLength / Song.QueuePosition — are inline pure-data and are handled in
        // ScriptExporter instead (no descriptor here); "Song Requests" is intentionally
        // NOT a _pureDataCategory, so these void nodes still emit flow. Song.QueueLength
        // and Song.QueuePosition resolve via ComputeInlineValue; Song.Current and
        // Song.UpNext are multi-output so they hoist one read through their own arms in
        // ResolveOutputFromNode. The three Song.On* roots are Events, also no descriptor.
        //
        // Song.Request's empty-User fallback is "" rather than a name: the Hub handler
        // resolves an empty User to the triggering chatter, which is the same convention
        // points.* and the Twitch action nodes use.
        private static void RegisterSongRequest(ExporterRegistry r)
        {
            r.RegisterSimple(new SimpleEmitDescriptor(
                "Song.Request", "song.request",
                new[]
                {
                    new SocketArg("Query", "\"\""),
                    new SocketArg("User",  "\"\""),
                },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Song.Skip", "song.skip",
                System.Array.Empty<SocketArg>(),
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Song.Pause", "song.pause",
                System.Array.Empty<SocketArg>(),
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Song.Resume", "song.resume",
                System.Array.Empty<SocketArg>(),
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Song.Remove", "song.remove",
                new[] { new SocketArg("Position", "0") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Song.RemoveLast", "song.remove_last",
                new[] { new SocketArg("User", "\"\"") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Song.Clear", "song.clear",
                System.Array.Empty<SocketArg>(),
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Song.SetVolume", "song.set_volume",
                new[] { new SocketArg("Volume", "50") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Song.VoteSkip", "song.vote_skip",
                new[] { new SocketArg("User", "\"\"") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Song.Approve", "song.approve",
                new[] { new SocketArg("Position", "0") },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Song.Deny", "song.deny",
                new[] { new SocketArg("Position", "0") },
                FollowNamedOutput: "Done"));
        }

        // ── Polls & Betting (chat poll + points side-bet — void control nodes) ──
        // One SimpleEmitDescriptor per void node, flow continuing through the "Done"
        // output. SocketArg order == CommandManifest arg order (the locked three-way
        // contract). The two value nodes — Poll.Status and Poll.GetVotes — are inline
        // pure-data and are handled in ScriptExporter instead (no descriptor here); "Polls"
        // is intentionally NOT a _pureDataCategory, so these void nodes still emit flow.
        // Poll.GetVotes resolves via ComputeInlineValue; Poll.Status is multi-output so it
        // hoists one read through its own arm in ResolveOutputFromNode. The three Poll.On*
        // roots are Events, also no descriptor.
        //
        // Poll.Vote / Poll.Bet emit their empty User rather than omitting it: the Hub
        // handler reads an empty one as "the triggering chatter", which is the same
        // convention points.* / song.* and the Twitch action nodes use. Poll.Open's five
        // args are all emitted positionally because SimpleEmitHandler has no
        // omit-at-default branch — the trailing "0"/"false" fallbacks are the identity
        // values, so an unwired node behaves exactly as if they were absent.
        private static void RegisterPolls(ExporterRegistry r)
        {
            r.RegisterSimple(new SimpleEmitDescriptor(
                "Poll.Open", "poll.open",
                new[]
                {
                    new SocketArg("Title",           "\"\""),
                    new SocketArg("Options",         "\"\""),
                    new SocketArg("DurationSeconds", "0"),
                    new SocketArg("Betting",         "false"),
                    new SocketArg("Mirror",          "false"),
                },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Poll.Close", "poll.close",
                System.Array.Empty<SocketArg>(),
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Poll.Cancel", "poll.cancel",
                System.Array.Empty<SocketArg>(),
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Poll.Vote", "poll.vote",
                new[]
                {
                    new SocketArg("Option", "\"\""),
                    new SocketArg("User",   "\"\""),
                },
                FollowNamedOutput: "Done"));

            r.RegisterSimple(new SimpleEmitDescriptor(
                "Poll.Bet", "poll.bet",
                new[]
                {
                    new SocketArg("Option", "\"\""),
                    new SocketArg("Amount", "0"),
                    new SocketArg("User",   "\"\""),
                },
                FollowNamedOutput: "Done"));
        }
    }

    /// <summary>
    /// Chat.Send — the unified outbound chat node. Platform selection:
    ///   1. The Platforms input, when wired or carrying a non-empty pill,
    ///      OVERRIDES everything at runtime (single name or comma list) —
    ///      emitted as chat.send(msg, &lt;override&gt;, "&lt;checkmark csv&gt;") so an
    ///      override that resolves EMPTY at runtime falls back to the
    ///      checkmarks instead of silently sending nowhere.
    ///   2. No override + exactly ONE platform checked → the legacy
    ///      per-platform command (twitch.send_chat / youtube.send_chat /
    ///      kick.send_chat), resolved through the same ctx.Resolve path the
    ///      old SimpleEmitDescriptors used — migrated single-platform graphs
    ///      (and every golden) export byte-identically.
    ///   3. No override + several checked → chat.send(msg, "a, b") with the
    ///      baked CSV quoted (a bare comma would split the arg list).
    ///   4. Nothing checked, no override → warning comment, nothing sent.
    /// </summary>
    internal sealed class ChatSendHandler : IExporterHandler
    {
        public string NodeTitle => "Chat.Send";

        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            bool twitch  = ParseBoolAttr(node.GetAttr("Twitch",  "true"));
            bool youtube = ParseBoolAttr(node.GetAttr("YouTube", "true"));
            bool kick    = ParseBoolAttr(node.GetAttr("Kick",    "true"));

            // Wired upstream → Materialize hoists the expression; a non-empty
            // pill → its literal. Both count as "override present".
            string overrideExpr = ctx.Materialize(node, "Platforms", "\"\"");
            bool hasOverride = !IsEmptyLiteral(overrideExpr);

            var check = new System.Collections.Generic.List<string>(3);
            if (twitch)  check.Add(Phoenix.Controls.Shared.Core.ChatPlatforms.Twitch);
            if (youtube) check.Add(Phoenix.Controls.Shared.Core.ChatPlatforms.YouTube);
            if (kick)    check.Add(Phoenix.Controls.Shared.Core.ChatPlatforms.Kick);

            if (!hasOverride)
            {
                if (check.Count == 0)
                {
                    ctx.Emit($"{prefix}# WARNING: Chat.Send has no platform selected — nothing sent");
                    ctx.FollowNamed(node, "Done", indent);
                    return;
                }
                if (check.Count == 1)
                {
                    // Legacy single-platform collapse. The Message fallbacks
                    // mirror the retired per-platform descriptors exactly
                    // (Twitch's was "Hello!", YouTube/Kick's "Hello chat!") so
                    // the migrated emission stays byte-identical.
                    (string cmd, string fb) = check[0] switch
                    {
                        Phoenix.Controls.Shared.Core.ChatPlatforms.Twitch  => ("twitch.send_chat", "\"Hello!\""),
                        Phoenix.Controls.Shared.Core.ChatPlatforms.YouTube => ("youtube.send_chat", "\"Hello chat!\""),
                        _                                                  => ("kick.send_chat", "\"Hello chat!\""),
                    };
                    ctx.Emit($"{prefix}{cmd}({ctx.Resolve(node, "Message", fb)})");
                    ctx.FollowNamed(node, "Done", indent);
                    return;
                }

                string csv = string.Join(", ", check);
                ctx.Emit($"{prefix}chat.send({ctx.Resolve(node, "Message", "\"Hello chat!\"")}, \"{csv}\")");
                ctx.FollowNamed(node, "Done", indent);
                return;
            }

            string fallbackCsv = string.Join(", ", check);
            ctx.Emit($"{prefix}chat.send({ctx.Resolve(node, "Message", "\"Hello chat!\"")}, {overrideExpr}, \"{fallbackCsv}\")");
            ctx.FollowNamed(node, "Done", indent);
        }

        private static bool ParseBoolAttr(string v)
            => v.Trim().ToLowerInvariant() is "true" or "1" or "yes";

        private static bool IsEmptyLiteral(string v)
            => string.IsNullOrWhiteSpace(v) || v == "\"\"";
    }
}