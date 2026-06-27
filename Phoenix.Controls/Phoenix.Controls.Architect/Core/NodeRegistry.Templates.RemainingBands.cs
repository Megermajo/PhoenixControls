using System.Collections.Generic;
using System.Drawing;
using Phoenix.Controls.Shared.Localization;

namespace Phoenix.Controls.Architect.Core
{
    //  — finishing sweep. Carves the remaining ~13 bands of
    // RegisterDefaults() into one big partial so the parent file is
    // left holding only the orchestrator + post-template helper calls
    // (SetSocketDescriptions / SetCompactSymbol / SetKeywords /
    // fuzzy-search aliases, etc.). Each band is preserved as a
    // // ── Header ── block in the source order it had in the parent
    // partial; no behaviour change, no template added or removed.
    //
    // Bands moved here (in source order):
    //   * TWITCH DATA       — Twitch.GetUser / GetStream / CheckRole /
    //     GetFollowAge / LastActive / GetViewers.
    //   * VISUALS           — Visual.Trigger, Chat.Overlay.Push / Clear.
    //   * PLATFORM ACTIONS  — Twitch.SendChat / Timeout / Ban /
    //     CreateClip / Shoutout / Announcement.
    //   * Twitch moderation — Unban / Mod / Unmod / Vip / Unvip /
    //     DeleteMessage / SlowMode / FollowerMode / SubOnlyMode /
    //     Marker / Whisper / UpdateChannel.
    //   * OBS               — proxy-via-Streamer.bot DoAction nodes.
    //   * Discord           — bot REST nodes (P4 slice 1 + 2).
    //   * HTTP              — Get / Post / Put / Patch / Delete /
    //     ParseJson with the L27 ContentType-as-enum convention.
    //   * File              — Read / Write / Append.
    //   * Audio             — PlaySound / TextToSpeech / etc.
    //   * Twitch Polls / Predictions — Create / End / Resolve.
    //   * Twitch Reward Management — UpdateRewardCost / SetRewardEnabled
    //     / FulfillRedemption.
    //   * STATE MACHINE     — State.Set / Get / Equals.
    //   * AI                — AI.Ask, etc.
    //   * MACROS            — Macro.Call / Entry / Exit.
    //   * VARIABLES         — Var.Get / Set / Inc / Toggle, Public.Get / Set.
    //
    // Tests: NodeCoverage / CommandManifest / ExporterGolden contract
    // suites validate every template kept verbatim; build is clean.
    public static partial class NodeRegistry
    {
        private static void RegisterRemainingTemplates()
        {
            // ─────────────────────────────────────────────────────────────
            // TWITCH DATA
            // ─────────────────────────────────────────────────────────────
            // B22 (audit/winui-regressions-2026-05-24) — Twitch lookup nodes
            // surface a Username fallback pill so authors can sketch a static
            // username (e.g. for a one-off mod-status check) without first
            // wiring an event payload. SocketViewModel.IsFallbackPillNode
            // gates the wired-but-still-editable behaviour; the literal here
            // seeds the pill so new nodes render with "{user}" rather than a
            // bare "—" emptiness. Exporters fall back to the same literal
            // when the Username socket has no wired source.
            // 0.13.9 — Twitch.GetUser reads back the full "Get User Info for
            // Target" payload via the Phoenix data-action round-trip (DoAction →
            // poll GetGlobals; see ScriptManager.FetchActionGlobalsAsync). This
            // reverses the D1 trim (which had cut the outputs to DisplayName /
            // AccountCreated because the OLD fictional GetUserInfo request returned
            // nothing live): id / login / display / avatar / created + the
            // channel's last game/title + the mod/sub/vip flags now all come from
            // that ONE native sub-action. SubTier / SubMonths / FollowDate are NOT
            // in the payload, so they stay off the node — FollowDate lives on
            // Twitch.GetFollowAge. Adding outputs is non-destructive (MigrateNodes
            // back-fills the new sockets; no existing graph wired them).
            AddTemplate("Twitch.GetUser",       "Twitch Data", Color.FromArgb(100, 65, 165),
                Localizer.T("architect.node.bubble.twitch_getuser"),
                new[] { ("Flow", ColExec), ("Username", ColString) },
                new[] { ("Flow", ColExec), ("Id", ColString), ("Login", ColString),
                        ("DisplayName", ColString), ("ProfileImage", ColString),
                        ("AccountCreated", ColString), ("Game", ColString),
                        ("ChannelTitle", ColString), ("IsMod", ColBool),
                        ("IsSub", ColBool), ("IsVip", ColBool) },
                new Dictionary<string, string> { { "Username", "{user}" } });

            AddTemplate("Twitch.GetStream",     "Twitch Data", Color.FromArgb(100, 65, 165),
                Localizer.T("architect.node.bubble.twitch_getstream"),
                new[] { ("Flow", ColExec), ("Username", ColString) },
                new[] { ("Flow", ColExec), ("Title", ColString), ("Category", ColString), ("ViewerCount", ColNumber), ("Uptime", ColString) },
                new Dictionary<string, string> { { "Username", "{user}" } });

            AddTemplate("Twitch.CheckRole",     "Twitch Data", Color.FromArgb(100, 65, 165),
                Localizer.T("architect.node.bubble.twitch_checkrole"),
                new[] { ("Flow", ColExec), ("Username", ColString) },
                new[] { ("Flow", ColExec), ("IsMod", ColBool), ("IsSub", ColBool), ("IsVip", ColBool), ("IsBroadcaster", ColBool) },
                new Dictionary<string, string> { { "Username", "{user}" } });

            // Twitch.IsOnline — flow probe: is the stream live? Channel is
            // optional; blank checks the configured broadcaster's own channel.
            // The IsLive output resolves to {stream.is_live} (see the Twitch Data
            // arm in ScriptExporter.ResolveOutputFromNode); the Hub command sends
            // GetStreamInfo and writes that var. Modelled on Twitch.CheckRole.
            AddTemplate("Twitch.IsOnline",      "Twitch Data", Color.FromArgb(100, 65, 165),
                "Check whether the stream is currently live. Leave Channel empty to check your own (broadcaster) channel.",
                new[] { ("Flow", ColExec), ("Channel", ColString) },
                new[] { ("Flow", ColExec), ("IsLive", ColBool) },
                new Dictionary<string, string> { { "Channel", "" } });

            AddTemplate("Twitch.GetFollowAge",  "Twitch Data", Color.FromArgb(100, 65, 165),
                Localizer.T("architect.node.bubble.twitch_getfollowage"),
                new[] { ("Flow", ColExec), ("Username", ColString) },
                new[] { ("Flow", ColExec), ("Days", ColNumber), ("Formatted", ColString),
                        ("FollowDate", ColString), ("IsFollowing", ColBool) },
                new Dictionary<string, string> { { "Username", "{user}" } });

            AddTemplate("Twitch.LastActive", "Twitch Data", Color.FromArgb(100, 65, 165),
                Localizer.T("architect.node.bubble.twitch_lastactive"),
                new[] { ("Flow", ColExec), ("Username", ColString), ("Minutes", ColNumber) },
                new[] { ("Inactive", ColExec), ("Active", ColExec), ("MinutesAgo", ColNumber) },
                new Dictionary<string, string> { { "Username", "{user}" }, { "Minutes", "5" } });

            AddTemplate("Twitch.GetViewers", "Twitch Data", Color.FromArgb(100, 65, 165),
                Localizer.T("architect.node.bubble.twitch_getviewers"),
                new[] { ("Flow", ColExec) },
                new[] { ("Done", ColExec), ("Viewers", ColList) });

            // ─────────────────────────────────────────────────────────────
            // VISUALS
            // ─────────────────────────────────────────────────────────────
            AddTemplate("Visual.Trigger", "Visuals", Color.DarkMagenta,
                Localizer.T("architect.node.bubble.visual_trigger"),
                new[] { ("Flow", ColExec), ("LayerID", ColString), ("WidgetID", ColString), ("TriggerName", ColString), ("Args", ColList) },
                new[] { ("Done", ColExec) });

            // Sweep 25 — surface the chat-overlay broadcast (sweep 19's silent-no-op
            // fix made this work end-to-end at runtime; this template + the matching
            // SimpleEmitDescriptor in ExporterRegistry.cs make it authorable from a
            // .phxg graph instead of only from hand-written .phx.
            //
            // WidgetID targets a chat-overlay widget on any active layer (HUDServer
            // fans out to every connected /hud/<id> WS; compositor.js's chat_push
            // STEP renders into the matching widget). Color is a hex string; the
            // ScriptManager handler defaults to "#7fff7f" when blank.
            AddTemplate("Chat.Overlay.Push", "Visuals", Color.DarkMagenta,
                Localizer.T("architect.node.bubble.chat_overlay_push"),
                new[] { ("Flow", ColExec), ("WidgetID", ColString), ("Username", ColString), ("Message", ColString), ("Color", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("Chat.Overlay.Clear", "Visuals", Color.DarkMagenta,
                Localizer.T("architect.node.bubble.chat_overlay_clear"),
                new[] { ("Flow", ColExec), ("WidgetID", ColString) },
                new[] { ("Done", ColExec) });

            // D2 — low-level HUD mutation nodes. The runtime handlers
            // (ScriptManager.Visual.cs visual.set_text / set_visible /
            // set_property) and CommandManifest entries already exist; these
            // templates + the matching SimpleEmitDescriptors in
            // ExporterRegistry.Handlers1.cs make them authorable from a .phxg
            // graph instead of only from hand-written .phx. Each broadcasts a
            // single HUDServer message (SET_TEXT / VISUAL_SET_VISIBLE /
            // VISUAL_SET_PROPERTY) that compositor.js applies to the addressed
            // widget; unlike Visual.Trigger they don't run a trigger graph, so
            // the "Id"/"Widget" socket is the compositor element id, not a
            // (layer, widget, trigger) triple. Sockets/fallbacks mirror the
            // manifest arg order exactly (CommandManifest.cs visual.set_*).
            AddTemplate("Visual.SetText", "Visuals", Color.DarkMagenta,
                Localizer.T("architect.node.bubble.visual_set_text",
                    "Sets the text of a HUD element. Id is the compositor element id; Value is the new text. Broadcast live to every connected OBS browser source."),
                new[] { ("Flow", ColExec), ("Id", ColString), ("Value", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("Visual.SetVisible", "Visuals", Color.DarkMagenta,
                Localizer.T("architect.node.bubble.visual_set_visible",
                    "Shows or hides a HUD widget. Widget is the compositor element id; Visible toggles display. Broadcast live to every connected OBS browser source."),
                new[] { ("Flow", ColExec), ("Widget", ColString), ("Visible", ColBool) },
                new[] { ("Done", ColExec) },
                new Dictionary<string, string> { { "Visible", "true" } });

            AddTemplate("Visual.SetProperty", "Visuals", Color.DarkMagenta,
                Localizer.T("architect.node.bubble.visual_set_property",
                    "Sets an arbitrary property on a HUD widget. Widget is the compositor element id; Key is the property name; Value is the new value. Broadcast live to every connected OBS browser source."),
                new[] { ("Flow", ColExec), ("Widget", ColString), ("Key", ColString), ("Value", ColString) },
                new[] { ("Done", ColExec) });

            // ─────────────────────────────────────────────────────────────
            // PLATFORM ACTIONS
            // ─────────────────────────────────────────────────────────────
            // B22 — Twitch.SendChat Message fallback pill. Same fallback
            // semantics as B21 (wired wins at export; literal is the runtime
            // fallback for null upstream values). Seeds with "Hello chat!"
            // so a fresh node reads as authorable rather than blank.
            AddTemplate("Twitch.SendChat",       "Platforms", Color.DarkViolet,
                Localizer.T("architect.node.bubble.twitch_sendchat"),
                new[] { ("Flow", ColExec), ("Message", ColString) },
                new[] { ("Done", ColExec) },
                new Dictionary<string, string> { { "Message", "Hello chat!" } });

            // M33 — Twitch.Timeout / Twitch.Ban / Twitch.Announcement previously had no Flow output.
            // Sibling Twitch.* nodes (e.g. Twitch.SendChat with "Sent", Twitch.Shoutout with "Flow")
            // expose a Done flow so downstream work can chain off them. Adding the "Done" output
            // surfaces the connection point in the editor; whether the exporter follows it is a
            // separate ExporterRegistry concern (out of scope for this NodeRegistry-only sweep).
            // L24 — Twitch.Timeout Sec attribute gets an explicit "60" default and a tooltip note
            // about downstream behaviour with 0/negative values (downstream validation in the Hub
            // is a separate fix; here we just document + default).
            AddTemplate("Twitch.Timeout",        "Platforms", Color.DarkViolet,
                Localizer.T("architect.node.bubble.twitch_timeout"),
                new[] { ("Flow", ColExec), ("User", ColString), ("Sec", ColNumber) },
                new[] { ("Done", ColExec) },
                new Dictionary<string, string> { { "Sec", "60" } });

            AddTemplate("Twitch.Ban",            "Platforms", Color.DarkViolet,
                Localizer.T("architect.node.bubble.twitch_ban"),
                new[] { ("Flow", ColExec), ("User", ColString), ("Reason", ColString) },
                new[] { ("Done", ColExec) });

            // 0.13.9 — CreateClip is now a data action. The "Phoenix: Create Clip"
            // C# sub-action calls CPH.CreateClip(title, duration) and writes the
            // resulting URL into phx_clip_url; Hub reads it back (clip.url/clip.ok).
            // Delay (a bool) was dropped — CPH.CreateClip takes a clip-LENGTH
            // duration (5..60s), not a delay flag. ClipUrl/ClipOk are output sockets
            // (also reachable as the {clip.url}/{clip.ok} tokens downstream).
            AddTemplate("Twitch.CreateClip",     "Platforms", Color.DarkViolet,
                Localizer.T("architect.node.bubble.twitch_createclip"),
                new[] { ("Flow", ColExec), ("Duration", ColNumber), ("Title", ColString) },
                new[] { ("Done", ColExec), ("ClipUrl", ColString), ("ClipOk", ColBool) },
                new Dictionary<string, string> { { "Duration", "30" } });

            // NOTE (audit 2026-06-01): this output socket is named "Flow" while
            // every sibling Twitch.* action uses "Done" — a real naming
            // inconsistency. It is INTENTIONALLY left as-is: renaming it to
            // "Done" prunes the outbound link in existing graphs (MigrateNodes
            // removes the old-named socket and the dangling-link sweep drops its
            // wire — verified: it broke alerts.phxg / CommandStructure.phxg
            // golden exports into a "no flow output" warning). A safe rename
            // needs a socket-migration step (map old "Flow" → "Done" by position
            // on load). Deferred to Majo. See audit/architect-p0-p1-issues-2026-06-01.md.
            AddTemplate("Twitch.Shoutout",       "Platforms", Color.DarkViolet,
                Localizer.T("architect.node.bubble.twitch_shoutout"),
                new[] { ("Flow", ColExec), ("User", ColString) },
                new[] { ("Flow", ColExec) });

            // 0.13.9 — Color dropped: the "Phoenix: Announcement" action's Send
            // Announcement sub-action uses a FIXED colour (SB can't bind it to a
            // variable), so a Color input never had any effect.
            AddTemplate("Twitch.Announcement",   "Platforms", Color.DarkViolet,
                Localizer.T("architect.node.bubble.twitch_announcement"),
                new[] { ("Flow", ColExec), ("Message", ColString) },
                new[] { ("Done", ColExec) });

            // ── Twitch moderation / channel control (P3) ──────────────────
            // All currently proxy through Streamer.bot DoAction; will move to
            // direct Helix once Hub gains first-class Twitch API access.
            AddTemplate("Twitch.Unban",          "Platforms", Color.DarkViolet,
                Localizer.T("architect.node.bubble.twitch_unban"),
                new[] { ("Flow", ColExec), ("User", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("Twitch.Mod",            "Platforms", Color.DarkViolet,
                Localizer.T("architect.node.bubble.twitch_mod"),
                new[] { ("Flow", ColExec), ("User", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("Twitch.Unmod",          "Platforms", Color.DarkViolet,
                Localizer.T("architect.node.bubble.twitch_unmod"),
                new[] { ("Flow", ColExec), ("User", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("Twitch.Vip",            "Platforms", Color.DarkViolet,
                Localizer.T("architect.node.bubble.twitch_vip"),
                new[] { ("Flow", ColExec), ("User", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("Twitch.Unvip",          "Platforms", Color.DarkViolet,
                Localizer.T("architect.node.bubble.twitch_unvip"),
                new[] { ("Flow", ColExec), ("User", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("Twitch.DeleteMessage",  "Platforms", Color.DarkViolet,
                Localizer.T("architect.node.bubble.twitch_deletemessage"),
                new[] { ("Flow", ColExec), ("MessageId", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("Twitch.SlowMode",       "Platforms", Color.DarkViolet,
                Localizer.T("architect.node.bubble.twitch_slowmode"),
                new[] { ("Flow", ColExec), ("Seconds", ColNumber) },
                new[] { ("Done", ColExec) },
                new Dictionary<string, string> { { "Seconds", "30" } });

            AddTemplate("Twitch.FollowerMode",   "Platforms", Color.DarkViolet,
                Localizer.T("architect.node.bubble.twitch_followermode"),
                new[] { ("Flow", ColExec), ("Minutes", ColNumber) },
                new[] { ("Done", ColExec) },
                new Dictionary<string, string> { { "Minutes", "10" } });

            AddTemplate("Twitch.SubOnlyMode",    "Platforms", Color.DarkViolet,
                Localizer.T("architect.node.bubble.twitch_subonlymode"),
                new[] { ("Flow", ColExec), ("Enabled", ColBool) },
                new[] { ("Done", ColExec) },
                new Dictionary<string, string> { { "Enabled", "true" } });

            AddTemplate("Twitch.Marker",         "Platforms", Color.DarkViolet,
                Localizer.T("architect.node.bubble.twitch_marker"),
                new[] { ("Flow", ColExec), ("Description", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("Twitch.Whisper",        "Platforms", Color.DarkViolet,
                Localizer.T("architect.node.bubble.twitch_whisper"),
                new[] { ("Flow", ColExec), ("User", ColString), ("Message", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("Twitch.UpdateChannel",  "Platforms", Color.DarkViolet,
                Localizer.T("architect.node.bubble.twitch_updatechannel"),
                new[] { ("Flow", ColExec), ("Title", ColString), ("GameId", ColString) },
                new[] { ("Done", ColExec) });

            // ── OBS (proxy via Streamer.bot DoAction) ─────────────────────
            // P3 (TODO #61) — first-pass OBS control surface. Each node is a flow-bearing
            // exec node with a single Done continuation; the engine handler currently logs
            // a Communication-tier stub and returns. The Streamer.bot DoAction relay (and
            // eventually a direct OBS-WebSocket client in the Hub) lands in a follow-up.
            // Shared "OBS" category; OBS brand-ish blue (80,130,200) keeps them visually
            // distinct from the Twitch (DarkViolet) and HTTP (DarkSlateBlue) families.
            var ObsBlue = Color.FromArgb(80, 130, 200);

            AddTemplate("OBS.SetScene",             "OBS", ObsBlue,
                Localizer.T("architect.node.bubble.obs_setscene"),
                new[] { ("Flow", ColExec), ("Scene", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("OBS.SetSourceVisible",     "OBS", ObsBlue,
                Localizer.T("architect.node.bubble.obs_setsourcevisible"),
                new[] { ("Flow", ColExec), ("Scene", ColString), ("Source", ColString), ("Visible", ColBool) },
                new[] { ("Done", ColExec) });

            // 0.13.9 — the "Phoenix: OBS Refresh Browser" action SETS the source's
            // URL (to %link%) then reloads it, so the node needs a Url input. Without
            // it the action received an empty %link% and blanked the browser source.
            AddTemplate("OBS.RefreshBrowserSource", "OBS", ObsBlue,
                Localizer.T("architect.node.bubble.obs_refreshbrowsersource"),
                new[] { ("Flow", ColExec), ("Scene", ColString), ("Source", ColString), ("Url", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("OBS.StartRecording",       "OBS", ObsBlue,
                Localizer.T("architect.node.bubble.obs_startrecording"),
                new[] { ("Flow", ColExec) },
                new[] { ("Done", ColExec) });

            AddTemplate("OBS.StopRecording",        "OBS", ObsBlue,
                Localizer.T("architect.node.bubble.obs_stoprecording"),
                new[] { ("Flow", ColExec) },
                new[] { ("Done", ColExec) });

            AddTemplate("OBS.StartStreaming",       "OBS", ObsBlue,
                Localizer.T("architect.node.bubble.obs_startstreaming"),
                new[] { ("Flow", ColExec) },
                new[] { ("Done", ColExec) });

            AddTemplate("OBS.StopStreaming",        "OBS", ObsBlue,
                Localizer.T("architect.node.bubble.obs_stopstreaming"),
                new[] { ("Flow", ColExec) },
                new[] { ("Done", ColExec) });

            AddTemplate("OBS.SaveReplayBuffer",     "OBS", ObsBlue,
                Localizer.T("architect.node.bubble.obs_savereplaybuffer"),
                new[] { ("Flow", ColExec) },
                new[] { ("Done", ColExec) });

            AddTemplate("OBS.SetSourcePosition",    "OBS", ObsBlue,
                Localizer.T("architect.node.bubble.obs_setsourceposition"),
                new[] { ("Flow", ColExec), ("Scene", ColString), ("Source", ColString), ("X", ColNumber), ("Y", ColNumber) },
                new[] { ("Done", ColExec) });

            AddTemplate("OBS.SetSourceScale",       "OBS", ObsBlue,
                Localizer.T("architect.node.bubble.obs_setsourcescale"),
                new[] { ("Flow", ColExec), ("Scene", ColString), ("Source", ColString), ("ScaleX", ColNumber), ("ScaleY", ColNumber) },
                new[] { ("Done", ColExec) });

            AddTemplate("OBS.SetSourceRotation",    "OBS", ObsBlue,
                Localizer.T("architect.node.bubble.obs_setsourcerotation"),
                new[] { ("Flow", ColExec), ("Scene", ColString), ("Source", ColString), ("Degrees", ColNumber) },
                new[] { ("Done", ColExec) });

            AddTemplate("OBS.SetFilterVisible",     "OBS", ObsBlue,
                Localizer.T("architect.node.bubble.obs_setfiltervisible"),
                new[] { ("Flow", ColExec), ("Scene", ColString), ("Source", ColString), ("Filter", ColString), ("Visible", ColBool) },
                new[] { ("Done", ColExec) });

            AddTemplate("OBS.TakeScreenshot",       "OBS", ObsBlue,
                Localizer.T("architect.node.bubble.obs_takescreenshot"),
                new[] { ("Flow", ColExec), ("Scene", ColString), ("Source", ColString), ("Path", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("Discord.Webhook",       "Platforms", Color.Indigo,
                Localizer.T("architect.node.bubble.discord_webhook"),
                new[] { ("Flow", ColExec), ("URL", ColString), ("Msg", ColString) },
                null);

            // P4 — Discord bot REST nodes. Color mirrors the HTTP cluster
            // (DarkSlateBlue) so the bot-token path visually groups with the
            // generic networking nodes rather than the webhook entry above.
            // English bubble strings inline this sprint — en.json updates are
            // owned by a parallel agent.
            // B22 (audit/winui-regressions-2026-05-24) — Discord.* nodes get
            // fallback pills on every id-shaped param so authors see the
            // expected snowflake shape inline. The defaults use the
            // documented "0" placeholder so a fresh node reads as "type a
            // snowflake here" without leaving a blank pill.
            AddTemplate("Discord.SendMessage",  "Platforms", Color.DarkSlateBlue,
                "Discord — Send Message (Bot)\nPosts content to a channel using the configured bot token. Channel id is the numeric snowflake.",
                new[] { ("Flow", ColExec), ("ChannelId", ColString), ("Content", ColString) },
                new[] { ("Flow", ColExec), ("MessageId", ColString), ("Error", ColString) },
                new Dictionary<string, string> { { "ChannelId", "0" }, { "Content", "Hello" } });

            AddTemplate("Discord.SendEmbed",    "Platforms", Color.DarkSlateBlue,
                "Discord — Send Embed (Bot)\nPosts a rich embed to a channel using the configured bot token. Color accepts a hex string (e.g. #5865F2) or a decimal int. Title, Description, and URL are all optional individually but at least one must be set.",
                new[] { ("Flow", ColExec), ("ChannelId", ColString), ("Title", ColString), ("Description", ColString), ("Color", ColString), ("Url", ColString) },
                new[] { ("Flow", ColExec), ("Error", ColString) },
                new Dictionary<string, string> { { "ChannelId", "0" } });

            // P4 slice 2 — role + reaction + user-lookup nodes. Same bot-token
            // path; surface only the Error socket for the side-effect commands,
            // GetUser additionally exposes the user's primary fields.
            AddTemplate("Discord.AddRole",      "Platforms", Color.DarkSlateBlue,
                "Discord — Add Role (Bot)\nGrants a role to a guild member. All three ids are numeric snowflakes; the bot must have Manage Roles in the guild and a higher role than the one being assigned.",
                new[] { ("Flow", ColExec), ("GuildId", ColString), ("UserId", ColString), ("RoleId", ColString) },
                new[] { ("Flow", ColExec), ("Error", ColString) },
                new Dictionary<string, string> { { "GuildId", "0" }, { "UserId", "0" }, { "RoleId", "0" } });

            AddTemplate("Discord.RemoveRole",   "Platforms", Color.DarkSlateBlue,
                "Discord — Remove Role (Bot)\nRevokes a role from a guild member. Same permission/hierarchy rules as Add Role apply.",
                new[] { ("Flow", ColExec), ("GuildId", ColString), ("UserId", ColString), ("RoleId", ColString) },
                new[] { ("Flow", ColExec), ("Error", ColString) },
                new Dictionary<string, string> { { "GuildId", "0" }, { "UserId", "0" }, { "RoleId", "0" } });

            AddTemplate("Discord.React",        "Platforms", Color.DarkSlateBlue,
                "Discord — Add Reaction (Bot)\nAdds a reaction from the bot account to an existing message. Emoji accepts a unicode character (e.g. 😀) or a custom-emoji handle in name:id form (e.g. mygif:123456789012345678).",
                new[] { ("Flow", ColExec), ("ChannelId", ColString), ("MessageId", ColString), ("Emoji", ColString) },
                new[] { ("Flow", ColExec), ("Error", ColString) },
                new Dictionary<string, string> { { "ChannelId", "0" }, { "MessageId", "0" } });

            AddTemplate("Discord.GetUser",      "Platforms", Color.DarkSlateBlue,
                "Discord — Get User (Bot)\nLooks up a Discord user by id and surfaces username, display name, and avatar URL. Avatar is empty when the user has no custom avatar set.",
                new[] { ("Flow", ColExec), ("UserId", ColString) },
                new[] { ("Flow", ColExec), ("Username", ColString), ("GlobalName", ColString), ("AvatarUrl", ColString), ("Error", ColString) },
                new Dictionary<string, string> { { "UserId", "0" } });

            // ── HTTP ──────────────────────────────────────────────────────
            // L27 — ContentType is now an enum attribute on every HTTP.* template (the four
            // existing methods: Get, Post, Put, Delete). Decision: keep the existing per-method
            // node API (don't collapse to a single HTTP.Request node — too disruptive), but add
            // ContentType as a uniform enum attribute. Allowed values:
            //   application/json (default), application/x-www-form-urlencoded,
            //   text/plain, multipart/form-data
            // For Get/Delete the attribute is informational (no request body) — kept for
            // uniformity so the user always knows where the setting lives.
            // HTTP.Patch landed in sweep 11: SimpleEmitDescriptor in ExporterRegistry,
            // CommandManifest entry, and ScriptManager handler all wired alongside the
            // template below.
            // (ContentTypeEnumNote const removed — descriptions are now sourced from
            // the localization bundle, so the inline-concat helper is no longer used.)

            AddTemplate("HTTP.Get",    "Platforms", Color.DarkSlateBlue,
                Localizer.T("architect.node.bubble.http_get"),
                new[] { ("Flow", ColExec), ("Url", ColString), ("Headers", ColString) },
                new[] { ("Flow", ColExec), ("StatusCode", ColNumber), ("Body", ColString), ("Error", ColString) },
                new Dictionary<string, string> { { "ContentType", "application/json" } });

            AddTemplate("HTTP.Post",   "Platforms", Color.DarkSlateBlue,
                Localizer.T("architect.node.bubble.http_post"),
                new[] { ("Flow", ColExec), ("Url", ColString), ("Body", ColString), ("ContentType", ColString), ("Headers", ColString) },
                new[] { ("Flow", ColExec), ("StatusCode", ColNumber), ("Response", ColString), ("Error", ColString) },
                new Dictionary<string, string> { { "ContentType", "application/json" } });

            AddTemplate("HTTP.Put",    "Platforms", Color.DarkSlateBlue,
                Localizer.T("architect.node.bubble.http_put"),
                new[] { ("Flow", ColExec), ("Url", ColString), ("Body", ColString), ("ContentType", ColString), ("Headers", ColString) },
                new[] { ("Flow", ColExec), ("StatusCode", ColNumber), ("Response", ColString), ("Error", ColString) },
                new Dictionary<string, string> { { "ContentType", "application/json" } });

            AddTemplate("HTTP.Patch",  "Platforms", Color.DarkSlateBlue,
                Localizer.T("architect.node.bubble.http_patch"),
                new[] { ("Flow", ColExec), ("Url", ColString), ("Body", ColString), ("ContentType", ColString), ("Headers", ColString) },
                new[] { ("Flow", ColExec), ("StatusCode", ColNumber), ("Response", ColString), ("Error", ColString) },
                new Dictionary<string, string> { { "ContentType", "application/json" } });

            AddTemplate("HTTP.Delete", "Platforms", Color.DarkSlateBlue,
                Localizer.T("architect.node.bubble.http_delete"),
                new[] { ("Flow", ColExec), ("Url", ColString) },
                new[] { ("Flow", ColExec), ("StatusCode", ColNumber), ("Response", ColString), ("Error", ColString) },
                new Dictionary<string, string> { { "ContentType", "application/json" } });

            AddTemplate("HTTP.ParseJson", "Platforms", Color.DarkSlateBlue,
                Localizer.T("architect.node.bubble.http_parsejson"),
                new[] { ("Json", ColString), ("Path", ColString) },
                new[] { ("Value", ColString), ("Error", ColString) });

            // P3 — File I/O. Path is taken as-is — relative paths are resolved against
            // the Hub's working directory; absolute paths are honoured. Engine writes
            // result.file_content / result.file_error so downstream sockets can resolve
            // them via the existing result.* substitution pattern (mirrors http.* /
            // api.* result-var contracts).
            // B22 — File.ReadText Path fallback pill. Seeds with a sample
            // relative path so the user can see the expected shape inline.
            AddTemplate("File.ReadText",  "Platforms", Color.DarkSlateBlue,
                Localizer.T("architect.node.bubble.file_readtext"),
                new[] { ("Flow", ColExec), ("Path", ColString) },
                new[] { ("Flow", ColExec), ("Content", ColString), ("Error", ColString) },
                new Dictionary<string, string> { { "Path", "data/example.txt" } });

            AddTemplate("File.WriteText", "Platforms", Color.DarkSlateBlue,
                Localizer.T("architect.node.bubble.file_writetext"),
                new[] { ("Flow", ColExec), ("Path", ColString), ("Content", ColString) },
                new[] { ("Flow", ColExec), ("Error", ColString) },
                new Dictionary<string, string> { { "Append", "false" } });

            //  — File.ReadJSON / File.WriteJSON. Same socket shape as
            // ReadText / WriteText so mixed graphs are visually consistent;
            // additional contract is "JSON validity is checked". On read: a
            // parse error appears in Error with Content still carrying the
            // raw bytes so the script can introspect. On write: a malformed
            // payload returns an Error and the file is NOT touched.
            // B22 — File.ReadJSON Path fallback pill, same idiom as ReadText.
            AddTemplate("File.ReadJSON",  "Platforms", Color.DarkSlateBlue,
                "Reads a UTF-8 file and validates the contents are parseable JSON. Content carries the raw text either way; on parse failure Error carries the parser message (e.g. 'invalid token at line 3') so the script can decide whether to fall back. Pair with HTTP.ParseJson when extracting individual values from the loaded JSON.",
                new[] { ("Flow", ColExec), ("Path", ColString) },
                new[] { ("Flow", ColExec), ("Content", ColString), ("Error", ColString) },
                new Dictionary<string, string> { { "Path", "data/example.json" } });

            AddTemplate("File.WriteJSON", "Platforms", Color.DarkSlateBlue,
                "Writes Content to Path only after confirming Content is parseable JSON — a malformed payload returns a parser message in Error and leaves the file untouched, so a buggy script can't half-write a corrupt JSON file. Append=true appends Content + newline (canonical JSON Lines shape — one record per line). Set Append=false to overwrite.",
                new[] { ("Flow", ColExec), ("Path", ColString), ("Content", ColString) },
                new[] { ("Flow", ColExec), ("Error", ColString) },
                new Dictionary<string, string> { { "Append", "false" } });

            // P3 — Audio.* (Platforms category, DarkSlateBlue alongside File.* / HTTP.*).
            // Path/Text are required; the optional sockets carry sensible defaults so a
            // bare drop on the canvas is playable. Error mirrors the file.* contract.
            AddTemplate("Audio.Play", "Platforms", Color.DarkSlateBlue,
                Localizer.T("architect.node.bubble.audio_play"),
                new[] { ("Flow", ColExec), ("Path", ColString), ("Volume", ColNumber) },
                new[] { ("Flow", ColExec), ("Error", ColString) },
                new Dictionary<string, string> { { "Volume", "1.0" } });

            AddTemplate("Audio.PlayTts", "Platforms", Color.DarkSlateBlue,
                Localizer.T("architect.node.bubble.audio_play_tts"),
                new[] { ("Flow", ColExec), ("Text", ColString), ("Voice", ColString), ("Rate", ColNumber), ("Volume", ColNumber) },
                new[] { ("Flow", ColExec), ("Error", ColString) },
                new Dictionary<string, string> { { "Voice", "" }, { "Rate", "0" }, { "Volume", "1.0" } });

            AddTemplate("Audio.SetVolume", "Platforms", Color.DarkSlateBlue,
                Localizer.T("architect.node.bubble.audio_set_volume"),
                new[] { ("Flow", ColExec), ("Volume", ColNumber) },
                new[] { ("Flow", ColExec), ("Error", ColString) },
                new Dictionary<string, string> { { "Volume", "1.0" } });

            // B22 — API.Call URL fallback pill. Sample URL seeds the pill so
            // a fresh node reads as authorable; same fallback semantics as
            // the other B22 templates (wired wins at export, literal is the
            // runtime fallback).
            AddTemplate("API.Call",       "Platforms", Color.DarkSlateBlue,
                Localizer.T("architect.node.bubble.api_call"),
                new[] { ("Flow", ColExec), ("Url", ColString) },
                new[] { ("Flow", ColExec), ("Response", ColString) },
                new Dictionary<string, string> { { "Url", "https://api.example.com/" } });

            // ── Twitch Polls & Predictions ────────────────────────────────
            AddTemplate("Twitch.CreatePoll",       "Platforms", Color.MediumOrchid,
                Localizer.T("architect.node.bubble.twitch_createpoll"),
                new[] { ("Flow", ColExec), ("Title", ColString), ("Choices", ColString), ("DurationSec", ColNumber) },
                // No PollId output — Streamer.bot's DoAction can't return the created
                // poll id, so the runtime no longer sets result.poll_id (see ScriptManager.Twitch).
                new[] { ("Flow", ColExec) },
                new Dictionary<string, string> { { "DurationSec", "60" } });

            AddTemplate("Twitch.EndPoll",          "Platforms", Color.MediumOrchid,
                Localizer.T("architect.node.bubble.twitch_endpoll"),
                new[] { ("Flow", ColExec), ("PollId", ColString) },
                new[] { ("Flow", ColExec) });

            AddTemplate("Twitch.CreatePrediction", "Platforms", Color.MediumOrchid,
                Localizer.T("architect.node.bubble.twitch_createprediction"),
                new[] { ("Flow", ColExec), ("Title", ColString), ("OutcomeA", ColString), ("OutcomeB", ColString),
                        ("OutcomeC", ColString), ("OutcomeD", ColString), ("OutcomeE", ColString), ("DurationSec", ColNumber) },
                // No PredictionId output — DoAction can't return the created prediction id
                // (see ScriptManager.Twitch); resolve_prediction is deferred.
                new[] { ("Flow", ColExec) },
                new Dictionary<string, string> { { "DurationSec", "120" } });

            AddTemplate("Twitch.ResolvePrediction","Platforms", Color.MediumOrchid,
                Localizer.T("architect.node.bubble.twitch_resolveprediction"),
                new[] { ("Flow", ColExec), ("PredictionId", ColString), ("WinningOutcomeId", ColString) },
                new[] { ("Flow", ColExec) });

            // ── Twitch Reward Management ──────────────────────────────────
            AddTemplate("Twitch.UpdateRewardCost", "Platforms", Color.DarkViolet,
                Localizer.T("architect.node.bubble.twitch_updaterewardcost"),
                new[] { ("Flow", ColExec), ("RewardId", ColString), ("Cost", ColNumber) },
                new[] { ("Flow", ColExec) });

            AddTemplate("Twitch.SetRewardEnabled",  "Platforms", Color.DarkViolet,
                Localizer.T("architect.node.bubble.twitch_setrewardenabled"),
                new[] { ("Flow", ColExec), ("RewardId", ColString), ("Enabled", ColBool) },
                new[] { ("Flow", ColExec) });

            AddTemplate("Twitch.FulfillRedemption","Platforms", Color.DarkViolet,
                Localizer.T("architect.node.bubble.twitch_fulfillredemption"),
                new[] { ("Flow", ColExec), ("RedemptionId", ColString) },
                new[] { ("Flow", ColExec) });

            AddTemplate("Twitch.RejectRedemption", "Platforms", Color.DarkViolet,
                Localizer.T("architect.node.bubble.twitch_rejectredemption"),
                new[] { ("Flow", ColExec), ("RedemptionId", ColString) },
                new[] { ("Flow", ColExec) });

            AddTemplate("StreamerBot.DoAction",  "Platforms", Color.Crimson,
                Localizer.T("architect.node.bubble.streamerbot_doaction"),
                new[] { ("Flow", ColExec), ("ActionId", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("StreamerBot.GetUser",   "Platforms", Color.Crimson,
                Localizer.T("architect.node.bubble.streamerbot_getuser"),
                new[] { ("Flow", ColExec), ("User", ColString) },
                new[] { ("Data", ColObject) });


            // ─────────────────────────────────────────────────────────────
            // STATE MACHINE
            // ─────────────────────────────────────────────────────────────
            AddTemplate("State.Set",    "State", Color.DarkOrange,
                Localizer.T("architect.node.bubble.state_set"),
                new[] { ("Flow", ColExec), ("Name", ColString), ("Value", ColString) },
                new[] { ("Flow", ColExec) });

            AddTemplate("State.Get",    "State", Color.DarkOrange,
                Localizer.T("architect.node.bubble.state_get"),
                new[] { ("Name", ColString) },
                new[] { ("Value", ColString) },
                // Inline default pill for Name — matches State.Set / State.Switch
                // ("phase"); the exporter falls back to this when Name is unwired.
                new Dictionary<string, string> { { "Name", "phase" } });

            AddTemplate("State.Switch", "State", Color.DarkOrange,
                Localizer.T("architect.node.bubble.state_switch"),
                new[] { ("Flow", ColExec), ("Name", ColString) },
                new[] { ("Case A", ColExec), ("Case B", ColExec), ("Default", ColExec) },
                // Keys must match the socket names verbatim — StateSwitchHandler reads
                // node.GetAttr("Case A", …) (spaced). Legacy graphs that picked up the
                // old "CaseA"/"CaseB" keys are healed in GraphSerializer.MigrateNodes.
                new Dictionary<string, string> { { "Case A", "intro" }, { "Case B", "live" } });

            AddTemplate("State.Exists",   "State", Color.DarkOrange,
                Localizer.T("architect.node.bubble.state_exists"),
                new[] { ("Name", ColString) },
                new[] { ("Result", ColBool) });

            AddTemplate("State.Delete",   "State", Color.DarkOrange,
                Localizer.T("architect.node.bubble.state_delete"),
                new[] { ("Flow", ColExec), ("Name", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("State.ListKeys", "State", Color.DarkOrange,
                Localizer.T("architect.node.bubble.state_listkeys"),
                null,
                new[] { ("Keys", ColList) });

            // ─────────────────────────────────────────────────────────────
            // AI
            // ─────────────────────────────────────────────────────────────
            AddTemplate("AI.Prompt",    "AI", Color.MediumSlateBlue,
                Localizer.T("architect.node.bubble.ai_prompt"),
                new[] { ("Flow", ColExec), ("SystemPrompt", ColString), ("UserPrompt", ColString), ("Model", ColString) },
                new[] { ("Flow", ColExec), ("Response", ColString), ("Error", ColString) },
                new Dictionary<string, string> { { "Model", "gpt-4o-mini" } });

            // QC37 — Error output is additive: the handler now sets
            // result.ai_error="" on success and a redacted detail on failure
            // (mirroring AI.Prompt). ScriptExporter maps the "Error" socket to
            // {result.ai_error}, so wiring it surfaces moderation failures
            // inline instead of only logging them.
            AddTemplate("AI.Moderate",  "AI", Color.MediumSlateBlue,
                Localizer.T("architect.node.bubble.ai_moderate"),
                new[] { ("Flow", ColExec), ("Text", ColString) },
                new[] { ("Flow", ColExec), ("Flagged", ColBool), ("Category", ColString), ("Error", ColString) });

            //  — AI.GenerateImage. Calls OpenAI's images/generations
            // endpoint and stores the resulting URL in result.ai_image_url.
            // Single-shot (no streaming) — OpenAI returns the URL once
            // generation completes. Defaults to DALL-E 3 at 1024x1024.
            // Authors who want a different size pass it via the Size socket
            // ("1024x1792" / "1792x1024" for DALL-E 3 portrait/landscape;
            // "256x256" / "512x512" / "1024x1024" for DALL-E 2).
            AddTemplate("AI.GenerateImage", "AI", Color.MediumSlateBlue,
                Localizer.T("architect.node.bubble.ai_generate_image"),
                new[] { ("Flow", ColExec), ("Prompt", ColString), ("Model", ColString), ("Size", ColString) },
                new[] { ("Flow", ColExec), ("ImageUrl", ColString), ("Error", ColString) },
                new Dictionary<string, string> { { "Model", "dall-e-3" }, { "Size", "1024x1024" } });

            //  — AI.VisionDescribe. Calls OpenAI's chat completions
            // with a multi-modal user message (text + image_url). Single-
            // shot — the response is small enough that streaming buys
            // nothing. Default model gpt-4o-mini (cheapest vision-capable
            // model); pass gpt-4o for stronger visual reasoning.
            AddTemplate("AI.VisionDescribe", "AI", Color.MediumSlateBlue,
                Localizer.T("architect.node.bubble.ai_vision_describe"),
                new[] { ("Flow", ColExec), ("Prompt", ColString), ("ImageUrl", ColString), ("Model", ColString) },
                new[] { ("Flow", ColExec), ("Response", ColString), ("Error", ColString) },
                new Dictionary<string, string> { { "Model", "gpt-4o-mini" } });

            //  — AI.WithTools. Single-shot chat completion with
            // a `tools` array. The model picks: plain answer (Response
            // socket) OR a tool call (ToolCalls socket carries a JSON
            // array of {id, name, arguments}). Tools input takes a
            // JSON-array string of OpenAI tool definitions; pass-through
            // to the API verbatim.
            // QC37-05 — ToolChoice / ParallelToolCalls are additive optional
            // inputs. ToolChoice accepts "auto" | "none" | "any" | "required"
            // | a JSON object that names a tool; ParallelToolCalls accepts
            // "true" / "false". Both default to "" (omit → API default).
            // Order trails Model so the emitted args line up with the handler
            // (args[4]=ToolChoice, args[5]=ParallelToolCalls).
            AddTemplate("AI.WithTools", "AI", Color.MediumSlateBlue,
                Localizer.T("architect.node.bubble.ai_with_tools"),
                new[] { ("Flow", ColExec), ("SystemPrompt", ColString), ("UserPrompt", ColString), ("Tools", ColString), ("Model", ColString), ("ToolChoice", ColString), ("ParallelToolCalls", ColString) },
                new[] { ("Flow", ColExec), ("Response", ColString), ("ToolCalls", ColString), ("Error", ColString) },
                new Dictionary<string, string> { { "Model", "gpt-4o-mini" }, { "Tools", "[]" }, { "ToolChoice", "" }, { "ParallelToolCalls", "" } });

            //  — AI.StreamText. Streaming variant of AI.Prompt.
            // Hub streams provider Server-Sent Events; result.ai_response
            // is updated cumulatively per chunk and AI_CHUNK Bus
            // events fire per delta so HUD overlays can render the live
            // typing effect. result.ai_done flips on close.
            // Sprints 81+84+85 — provider routing by Model prefix:
            // claude* → Anthropic; ollama/<name> → local Ollama daemon;
            // cerebras/<name> → Cerebras hosted; else → OpenAI.
            //  — MemoryVar opt-in: name a Var key
            // (e.g. "MyChat") whose JSON-array-of-{role,content} payload
            // gets prepended to each call as prior turns and updated
            // with the new exchange after completion. Empty = no history.
            // QC37 — Done / ErrorKind / RetryAfter are additive outputs that
            // surface the handler's result.ai_done / result.ai_error_kind /
            // result.ai_retry_after sentinels (set on stream close / failure)
            // so scripts can branch on completion and classify failures (e.g.
            // a rate_limit kind plus a cool-down hint) without reading the raw
            // vars. ScriptExporter maps these socket names to the result vars.
            AddTemplate("AI.StreamText", "AI", Color.MediumSlateBlue,
                Localizer.T("architect.node.bubble.ai_stream_text"),
                new[] { ("Flow", ColExec), ("SystemPrompt", ColString), ("UserPrompt", ColString), ("Model", ColString), ("MemoryVar", ColString) },
                new[] { ("Flow", ColExec), ("Response", ColString), ("Error", ColString), ("Done", ColBool), ("ErrorKind", ColString), ("RetryAfter", ColString) },
                new Dictionary<string, string> { { "Model", "gpt-4o-mini" }, { "MemoryVar", "" } });

            // ──────────────────────────────────────────────────────────────
            // MACROS
            // ──────────────────────────────────────────────────────────────
            AddTemplate("Macro.Call", "Macros", Color.FromArgb(80, 80, 130),
                Localizer.T("architect.node.bubble.macro_call"),
                new[] { ("Flow", ColExec) },
                new[] { ("Flow", ColExec) },
                new Dictionary<string, string> { { "MacroId", "" }, { "MacroName", "NewMacro" } });

            AddTemplate("Macro.Entry", "Macros", Color.FromArgb(70, 70, 110),
                Localizer.T("architect.node.bubble.macro_entry"),
                null,
                new[] { ("Flow", ColExec) });

            AddTemplate("Macro.Exit", "Macros", Color.FromArgb(55, 55, 90),
                Localizer.T("architect.node.bubble.macro_exit"),
                new[] { ("Flow", ColExec) },
                null);

            // ──────────────────────────────────────────────────────────────
            // VARIABLES (graph-scoped, backed by DB var.{name} namespace)
            // ──────────────────────────────────────────────────────────────
            // Var.Get / Var.Set are attribute-driven: the variable name lives on the inline
            // VariableName editor on the node body, not on a wired socket. The legacy "Name"
            // socket on Var.Get was dead — neither the exporter nor the runtime read it — so
            // it's gone (BH-052). Existing graphs with a stale Name socket continue to load;
            // GraphSerializer.MigrateNodes does not delete extra sockets, but the export path
            // ignores them, so they're harmless visual cruft until the user re-saves.
            AddTemplate("Var.Get", "Variables", Color.FromArgb(60, 110, 160),
                Localizer.T("architect.node.bubble.var_get"),
                null,
                new[] { ("Value", ColObject) },
                new Dictionary<string, string> { { "VariableName", "myVar" } });

            AddTemplate("Var.Set", "Variables", Color.FromArgb(60, 110, 160),
                Localizer.T("architect.node.bubble.var_set"),
                new[] { ("Flow", ColExec), ("Value", ColObject) },
                new[] { ("Flow", ColExec) },
                new Dictionary<string, string> { { "VariableName", "myVar" } });

            // Public.Get / Public.Set — script-run-wide variable tier. Same lifetime
            // as Var.* (vanishes at the end of the script run, no DB persistence)
            // but with cross-branch merge-back: a write inside parallel_begin's branch
            // surfaces to the parent and to sibling reads after the branch joins,
            // because the underlying command goes through SetLocalResultVar (which
            // tags the key in _branchResultKeysLocal). Var.Set's bare-assignment path
            // stays branch-local by design; Public.Set is the explicit "share across
            // branches" alternative without paying SQLite round-trips like global.*.
            AddTemplate("Public.Get", "Variables", Color.FromArgb(60, 110, 160),
                Localizer.T("architect.node.bubble.public_get"),
                null,
                new[] { ("Value", ColObject) },
                new Dictionary<string, string> { { "KeyName", "myKey" } });

            AddTemplate("Public.Set", "Variables", Color.FromArgb(60, 110, 160),
                Localizer.T("architect.node.bubble.public_set"),
                new[] { ("Flow", ColExec), ("Value", ColObject) },
                new[] { ("Flow", ColExec) },
                new Dictionary<string, string> { { "KeyName", "myKey" } });

        }
    }
}
