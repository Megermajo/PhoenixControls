using System.Collections.Generic;

namespace Phoenix.Controls.Architect.WinUI.Hosting;

/// <summary>
/// Authored content layer for the in-app Node Reference. The structure of every
/// node card (category, sockets, types, default chips) is generated live from
/// <c>NodeRegistry</c> by <see cref="NodeReferenceData"/> so it can never drift
/// from the editor; this file supplies the human-written layer on top:
///
///   • <see cref="Categories"/> — the display order, accent colour and one-line
///     blurb per real registry category (the registry has no colour/blurb of its
///     own; the design provides the palette).
///   • <see cref="Nodes"/> — rich summary / description / "typical use" prose for
///     EVERY node the palette can spawn, keyed by the REAL registry title. A node
///     without an entry falls back to its registry <c>Description</c>; the goal is
///     full coverage so no node renders bare.
///   • the primer data — token families, persistent stores and clock catalogues
///     grounded in the script engine's resolvers (ScriptEngine.Variables.cs). The
///     keyboard reference is generated from the live ArchitectHotkeyCatalog.
///
/// Prose strings are HTML fragments (rendered via innerHTML by node-reference.js)
/// — keep them to the small inline vocabulary the design uses: &lt;p&gt;,
/// &lt;b&gt;/&lt;strong&gt;, &lt;em&gt;, &lt;code&gt;.
///
/// Accuracy contract: every claim here is checked against the live registry
/// templates, the exporter handlers and the Hub ScriptManager commands. When a
/// node's behaviour changes, update its prose here — the socket SHAPE updates
/// itself (it's generated), but the words do not.
/// </summary>
internal static class NodeProse
{
    internal readonly record struct CategoryMeta(string Category, string Color, string Blurb, string? Badge = null);

    /// <summary>
    /// Ordered category presentation. Categories present in the registry but
    /// absent here are appended after these in alphabetical order with a neutral
    /// accent (see <see cref="NodeReferenceData"/>). Colours are drawn from the
    /// forged palette in tokens.css / Design_Orders.md.
    /// </summary>
    internal static readonly IReadOnlyList<CategoryMeta> Categories = new[]
    {
        new CategoryMeta("Events", "#7A2E2E",
            "Stream-time triggers relayed from Streamer.bot, plus system, schedule, webhook, hotkey and clipboard sources. Every flow starts with one of these — dispatch is async and in arrival order, capped at a few concurrent runs (sequential by default), and each binds the event payload into the <code>{user.*}</code> / <code>{event.*}</code> tokens for everything downstream."),
        new CategoryMeta("Flow Control", "#3A4257",
            "Branches, switches, loops, gates and delays — the glue between an event and an action. Decide what matters, repeat work, fork the flow. None of these touch the network. The aggregate loop budget is 500 iterations per script run."),
        new CategoryMeta("Logic", "#46506B",
            "Pure comparisons and boolean math — <code>==</code>, <code>!=</code>, <code>&gt;</code>, <code>and</code>, <code>or</code>, <code>not</code>, ternary select. Inlined into whatever reads them; no execution edge of their own — the exception is <code>Logic.EnumMatch</code>, a flow node with its own <code>Match</code> / <code>NoMatch</code> edges."),
        new CategoryMeta("Async", "#1F5F6B",
            "Time and waiting — delays, timeouts, parallel forks, and chat / visual awaits. Non-blocking: other flows keep running while one waits. Chat and visual awaits resolve through the Streamer.bot feed and the Hub bus; delay nodes <em>extend</em> the script deadline rather than being cut short by it."),
        new CategoryMeta("Math", "#4A5A2E",
            "Arithmetic, rounding, clamping, random and percentage-chance. Pure data: wire the result straight into another socket — no flow pin (except <code>Math.Chance</code>, which branches)."),
        new CategoryMeta("Text", "#2E5A4A",
            "Build, format, split, join, search and case-shift strings. The workhorses behind every chat message and overlay caption. All pure data — they read upstream and hand a value down."),
        new CategoryMeta("Collections", "#5A4A2E",
            "Make, query and reshape lists — get, filter, sort, shuffle, unique, push. Accepts the comma-string and JSON-array forms Phoenix passes around; pair with <code>Array.Unpack</code> to read items into named slots."),
        new CategoryMeta("Values", "#3A3F57",
            "Literal value nodes — drop a fixed string, int, float or bool onto the canvas and wire it in. Edit the value inline; wire the input to override it at run-time. The simplest inputs there are."),
        new CategoryMeta("Convert", "#4A3F57",
            "Coerce a value from one type to another — to int, float, string or bool. Cheap, pure, no flow. Each carries a <code>Default</code> that fills the input only when the socket is <em>unwired</em>; a wired value that won't parse yields <code>0</code> / <code>0.0</code> / <code>false</code>, not the Default."),
        new CategoryMeta("Variables", "#2E4A5A",
            "Read and write named values by scope. <code>Var.*</code> is run-local (per-execution) — <em>not</em> persisted to the databank; it's gone when the run ends and stays branch-local inside a parallel fork. <code>Public.*</code> is run-local scratch too, but crosses parallel branches. For values that must survive the run, use <code>global.*</code> / <code>user.*</code> (the DB.* nodes) or <code>state.*</code>."),
        new CategoryMeta("State", "#3F4860",
            "A named state-machine store, persisted in the databank under the reserved <code>state.*</code> namespace. Survives restarts and is visible across every script — perfect for \"stream phase\", cooldown flags and last-winner. Changing a value fires <code>State.OnChange</code> listeners."),
        new CategoryMeta("Databank", "#6B5414",
            "Persistent SQLite key/value variables and <code>User_*</code> tables. Survives restarts; the home of points, counters, quotes and per-user data. Keyed and row/column access — no SQL to write."),
        new CategoryMeta("Queue", "#2E4A5A",
            "A durable FIFO queue backed by a databank variable (pipe-separated under the hood). Push work, pop the oldest, measure depth, clear it. Cross-script visible — drives raid trains, shoutout queues and rate-limited dispatch."),
        new CategoryMeta("Twitch Data", "#7A5414",
            "Read-only Twitch lookups — user info, stream state, roles, follow age, active viewers, last-active. Resolved through your Streamer.bot bot account; the result lands on the output sockets and in the <code>{user.*}</code> / <code>{stream.*}</code> / <code>{role.*}</code> / <code>{follow.*}</code> tokens."),
        new CategoryMeta("Platform Data", "#7A5414",
            "Read-only YouTube and Kick lookups — the cross-platform siblings of the Twitch Data band. Resolved through the <code>Phoenix:</code> platform actions in Streamer.bot; the result lands on the output sockets and in the <code>{user.*}</code> tokens, just like the Twitch lookups."),
        new CategoryMeta("Platforms", "#7A4710",
            "Outbound actions to the outside world — Twitch chat &amp; moderation, Discord, HTTP, the filesystem and audio. All Twitch traffic is routed through your Streamer.bot bot account (the <code>Phoenix:</code> action pack); Phoenix never talks to Twitch directly."),
        new CategoryMeta("OBS", "#1F4E3A",
            "Drive OBS Studio — switch scenes, toggle sources and filters, record, stream, screenshot, save the replay buffer. Most OBS nodes relay through the <code>Phoenix:</code> OBS actions in Streamer.bot, but the scene-item transforms (<code>OBS.SetSourcePosition</code> / <code>OBS.SetSourceScale</code> / <code>OBS.SetSourceRotation</code>) and <code>OBS.Event</code> use Hub's own OBS WebSocket connection when it's enabled in config."),
        new CategoryMeta("Visuals", "#3A1F4E",
            "Drive the Visualist overlay compositor — fire triggers, push chat overlays, set text, visibility and widget properties. Fire-and-forget over the local HUD bus; non-blocking. Pair with <code>Async.WaitForVisual</code> when a flow needs to wait for an animation to finish."),
        new CategoryMeta("Bus", "#1F4E5A",
            "Send and broadcast messages on Hub's internal IPC bus — talk to Visualist, the Architect debug client, or your own WebSocket listeners. Fire-and-forget; pair with <code>Bus.OnMessage</code> to receive."),
        new CategoryMeta("Process", "#46466E",
            "Long-running background loops — <code>Process.Start</code> launches a live instance whose own triggers (schedule, chat, …) keep running until <code>Process.Stop</code> ends it, not a fire-and-forget spawn. Start it more than once for several concurrent instances; pass start params read in the body as <code>{param.&lt;name&gt;}</code>. Kick them off from <code>System.Startup</code>."),
        new CategoryMeta("Macros", "#5A3A1F",
            "Reusable subgraphs called like a single node. A macro is inline-expanded into the graph at export time; build once, reuse everywhere. <code>Macro.Entry</code> / <code>Macro.Exit</code> declare its parameters and return values."),
        new CategoryMeta("Giveaway", "#5A2E3A",
            "Run a giveaway straight from a graph — open it, take weighted tickets per viewer, draw a winner. The same engine the Hub Giveaway panel drives, so chat commands and the panel stay in sync."),
        new CategoryMeta("System", "#3A4257",
            "Logging and the always-on clock / calendar / uptime. <code>System.Log</code> writes to the Hub System Log; the time probes feed the same values as the <code>{system.*}</code> and <code>{stream.*}</code> tokens."),
        new CategoryMeta("AI", "#5A2E5A",
            "LLM and vision nodes — prompt, moderate, generate and describe images, tool-calling. Provider is chosen by the model name. Currently hidden from the palette while the runtime is verified."),
        new CategoryMeta("Reroute", "#2C251D",
            "A bare wire knot — drag a reroute onto a wire to bend it cleanly around the canvas. Carries its type straight through, with no effect on data or flow."),
    };

    // ── node prose (keyed by REAL registry title) ─────────────────────────
    // Full coverage: an entry for every node the palette can spawn. Grounded in
    // the live registry templates, the exporter handlers and the Hub
    // ScriptManager commands.
    internal readonly record struct NodeInfo(
        string? Summary = null,
        string? Description = null,
        string? Example = null,
        string? Since = null,
        string? Badge = null);

    internal static readonly IReadOnlyDictionary<string, NodeInfo> Nodes = new Dictionary<string, NodeInfo>
    {
        // ═══════════════════════════════ EVENTS ═══════════════════════════════
        ["Chat.Message"] = new(
            Summary: "Fires when any chatter — including the broadcaster and bots — sends a message on Twitch, YouTube Live or Kick, whichever platforms are ticked on the node.",
            Description: "<p>The workhorse trigger, now for every platform at once. The three platform checkmarks on the node body choose which chats fire it (new nodes listen to all three), and <code>Platform</code> reports <code>twitch</code>, <code>youtube</code> or <code>kick</code> so one flow can branch per platform with <code>Logic.Switch</code>. <code>Message</code> is the raw line; <code>IsCommand</code> is true whenever it starts with <code>!</code> (the <code>Commands</code> filter separately gates whether the flow fires). <code>Command</code> is the first word lowercased with <code>!</code> stripped (the matched alias inside a filtered flow), and <code>Args</code> is the text after it as one space-separated string — <code>Text.Split</code> it on space before <code>Array.Get</code>/<code>Flow.ForEach</code>. The <code>User</code> output is a packed array — read individual fields with <code>Array.Get</code>/<code>Array.Unpack</code>, or use the broken-out <code>IsMod</code>/<code>IsSub</code>/<code>IsBroadcaster</code>/<code>IsVip</code> pins plus <code>SubMonths</code> and <code>ColorHex</code>. The final <code>MessageId</code> pin carries the triggering message's id (also available as <code>{event.message_id}</code>) — wire it into <code>Twitch.Reply</code> to answer as a threaded reply.</p><p>Role pins bind best-effort off Twitch: YouTube maps owner / moderator / member onto <code>IsBroadcaster</code> / <code>IsMod</code> / <code>IsSub</code>, Kick maps moderator / subscriber onto <code>IsMod</code> / <code>IsSub</code>, and <code>IsVip</code> is Twitch-only. Old <code>Twitch.ChatMessage</code> and <code>YouTube.Message</code> nodes upgrade to this node automatically on load with only their original platform ticked, so existing graphs behave exactly as before. Bots and the broadcaster fire this too — set the Bot Username in Settings if you don't want feedback loops. Chat scripts are capped at a few concurrent runs, so a flood queues rather than piling up. The <code>{user.name}</code> token is always lowercased — a stable identity key you can use for databank lookups.</p>",
            Example: "Set <code>Commands</code> to <b>ping</b> &rarr; reply with <code>Chat.Send</code>, wiring the <code>Platform</code> output into its <code>Platforms</code> override so the answer goes back to whichever platform asked — no per-platform branch needed."),
        ["Twitch.Subscription"] = new(
            Summary: "Fires on a subscription — including resubs, which additively fire this node too. Gift subs have their own dedicated nodes.",
            Description: "<p><code>Tier</code> is the normalised tier — <code>1</code>, <code>2</code>, <code>3</code> or <code>prime</code>. A renewal fires this node <em>and</em> <code>Twitch.Resub</code>, so use <code>Twitch.Resub</code> when you need renewal-specific fields (streak months, resub message); for gifted subs use <code>Twitch.GiftSub</code> / <code>Twitch.GiftBomb</code>.</p>",
            Example: "New tier-3 sub &rarr; <code>Twitch.Announcement</code> a thank-you and a Visualist burst."),
        ["Twitch.Resub"] = new(
            Summary: "Fires when a viewer renews an existing subscription, with their streak and resub message.",
            Description: "<p><code>Months</code> is the cumulative streak; <code>Message</code> is the optional resub note the viewer typed. <code>Tier</code> is normalised (<code>1</code>/<code>2</code>/<code>3</code>/<code>prime</code>).</p>",
            Example: "Resub at 12+ months &rarr; read their <code>Message</code> aloud with <code>Audio.PlayTts</code>."),
        ["Twitch.GiftSub"] = new(
            Summary: "Fires once per recipient when a viewer gifts a single subscription.",
            Description: "<p><code>Gifter</code> is who paid, <code>Recipient</code> is who received it. <code>IsAnonymous</code> is true for anonymous gifts — gate any \"thank the gifter\" branch on it, because the gifter name is a placeholder when anonymous.</p>",
            Example: "Gift sub &rarr; if not <code>IsAnonymous</code>, thank the <code>Gifter</code> in chat."),
        ["Twitch.GiftBomb"] = new(
            Summary: "Fires when a viewer gifts a batch of subs in one action (a community gift).",
            Description: "<p><code>Count</code> is how many subs were gifted. <code>IsAnonymous</code> hides the gifter the same way <code>Twitch.GiftSub</code> does. Fires once for the bomb itself; individual recipient events arrive separately.</p>",
            Example: "Gift bomb of 10+ &rarr; a big Visualist celebration sized to <code>Count</code>."),
        ["Twitch.Raid"] = new(
            Summary: "Fires when another channel raids yours, with the raider and their viewer count.",
            Description: "<p><code>Viewers</code> is the count Twitch reported — note it's broadcaster-controlled and editable in Streamer.bot, so treat reward gating on it as spoofable. Hub logs a warning when a claimed count looks implausible.</p>",
            Example: "Raid of &ge; 25 &rarr; play a raid-fanfare clip and <code>Twitch.Shoutout</code> the raider."),
        ["Twitch.Cheer"] = new(
            Summary: "Fires when a viewer cheers Bits in chat.",
            Description: "<p><code>Bits</code> is the amount; <code>Message</code> is the raw chat line, cheermote codes included (Phoenix doesn't strip them). Bit-only cheers can have an empty message.</p>",
            Example: "Cheer &ge; 500 &rarr; a Visualist particle burst sized to <code>Bits</code>."),
        ["Twitch.Follow"] = new(
            Summary: "Fires on a new follow. Re-follows do not re-fire.",
            Description: "<p>Twitch debounces follows hard — this fires once per identity, ever. For \"welcome back\" loops, key off <code>Chat.Message</code> with a databank lookup instead.</p>",
            Example: "Any follow &rarr; a tiny Visualist chime overlay (no spam risk)."),
        ["Twitch.PointRedeem"] = new(
            Summary: "Fires when a viewer redeems a channel-points reward.",
            Description: "<p><code>Reward</code> is the reward title from your Twitch dashboard; <code>Input</code> is the viewer's text (empty when the reward asks for none). Branch on the title with <code>Logic.Switch</code> — Phoenix matches on the title, not the reward ID, because IDs regenerate on rename.</p>",
            Example: "<b>\"Hydrate!\"</b> redeem &rarr; a Visualist trigger and a chat nudge to drink water."),
        ["Twitch.InWhisper"] = new(
            Summary: "Fires when someone sends a private whisper (DM) to the bot account.",
            Description: "<p><code>User</code> is the sender's display name, <code>UserId</code> their numeric Twitch id, and <code>Message</code> the whisper text. React however you like — answer in chat with <code>Chat.Send</code>, fire an overlay, or store the message. Requires the bot account connected in Streamer.bot with whisper-read authorized. The whisper body shows live in the Hub's Live Feed but is never written to the on-disk event log.</p>",
            Example: "Whisper <b>!uptime</b> &rarr; post how long the stream has been live in chat."),
        ["Stream.GoingLive"] = new(
            Summary: "Fires once when the broadcaster goes live, on whichever of Twitch / YouTube / Kick are ticked on the node.",
            Description: "<p>One unified trigger for the start of a stream. The three platform checkmarks on the node body choose which go-live events fire it (new nodes listen to all three), and <code>Platform</code> reports <code>twitch</code>, <code>youtube</code> or <code>kick</code>. <code>Title</code> and <code>Category</code> carry the stream title and game/category where the platform supplies them — Kick and YouTube populate both; Twitch leaves them empty (an empty string, never a raw token). Each node instance has its own 10-second cooldown, so a simultaneous multi-platform go-live fires the node once rather than three times in a burst. This is additive to the per-platform stream events (<code>Kick.StreamOnline</code>, <code>YouTube.BroadcastStarted</code>, …), which keep firing on their own.</p>",
            Example: "Going live &rarr; post a &ldquo;we're live!&rdquo; announcement in chat and fire a Visualist intro sting."),
        ["Stream.SessionEnd"] = new(
            Summary: "Fires when the broadcaster ends the stream, on whichever of Twitch / YouTube / Kick are ticked on the node.",
            Description: "<p>The bookend to <code>Stream.GoingLive</code> — same platform checkmarks, same per-instance 10-second cooldown so a multi-platform stream-end fires the node once. <code>Platform</code> names the platform that ended; <code>Title</code> and <code>Category</code> carry the last-known values where the platform supplies them (empty on Twitch). Additive to the per-platform offline events (<code>Kick.StreamOffline</code>, <code>YouTube.BroadcastEnded</code>, …).</p>",
            Example: "Session end &rarr; post an &ldquo;until next time&rdquo; message and save the replay buffer with <code>OBS.SaveReplayBuffer</code>."),

        // ── YouTube Live events ────────────────────────────────────────────
        ["YouTube.SuperChat"] = new(
            Summary: "Fires when a viewer sends a Super Chat in your YouTube live chat.",
            Description: "<p><code>User</code> is the sender and <code>Message</code> their highlighted text. <code>Amount</code> and <code>Currency</code> carry the purchase as YouTube reports it (e.g. <code>5.00</code> + <code>EUR</code>); <code>Tier</code> is the Super Chat colour tier.</p>",
            Example: "Thank the sender in chat — <code>\"{user.name} dropped a {event.amount} {event.currency} Super Chat!\"</code> — and fire a Visualist alert."),
        ["YouTube.SuperSticker"] = new(
            Summary: "Fires when a viewer sends a Super Sticker in your YouTube live chat.",
            Description: "<p><code>User</code> is the sender; <code>Amount</code> / <code>Currency</code> / <code>Tier</code> carry the purchase. <code>StickerAlt</code> is the sticker's alt text and <code>StickerUrl</code> its image URL — wire the URL straight into an overlay image widget.</p>",
            Example: "Show the sticker on stream by passing <code>StickerUrl</code> to a Visualist image trigger."),
        ["YouTube.NewSponsor"] = new(
            Summary: "Fires when a viewer becomes a channel member, or upgrades their membership level.",
            Description: "<p><code>User</code> is the member and <code>Level</code> the membership level name. <code>IsUpgrade</code> is true when an existing member moved up a level rather than joining fresh.</p>",
            Example: "New member &rarr; welcome them by <code>Level</code>; branch on <code>IsUpgrade</code> for a \"levelled up!\" variant."),
        ["YouTube.MemberMileStone"] = new(
            Summary: "Fires when a channel member hits a membership milestone — their anniversary message.",
            Description: "<p><code>User</code> and <code>Level</code> identify the member; <code>Months</code> is their total membership length and <code>Message</code> the note they attached — the YouTube cousin of a Twitch resub message.</p>",
            Example: "Milestone at 12+ <code>Months</code> &rarr; read their <code>Message</code> aloud with <code>Audio.PlayTts</code>."),
        ["YouTube.MembershipGift"] = new(
            Summary: "Fires when a viewer gifts channel memberships — once for the gift purchase itself.",
            Description: "<p><code>User</code> is the <em>gifter</em>; <code>Count</code> is how many memberships they gifted and <code>Tier</code> the level. Each recipient arrives separately on <code>YouTube.GiftMembershipReceived</code>.</p>",
            Example: "Gift of 10+ memberships &rarr; a celebration overlay sized to <code>Count</code>."),
        ["YouTube.GiftMembershipReceived"] = new(
            Summary: "Fires once per viewer who receives a gifted channel membership.",
            Description: "<p><code>User</code> is the <em>recipient</em>; <code>Gifter</code> names who paid and <code>Tier</code> the level. The bulk purchase itself fires <code>YouTube.MembershipGift</code>.</p>",
            Example: "Welcome each recipient: <code>\"{user.name} was gifted a membership by {user.gifter}!\"</code>"),
        ["YouTube.FirstWords"] = new(
            Summary: "Fires the first time a viewer speaks in your YouTube live chat this stream.",
            Description: "<p><code>User</code> and <code>Message</code> carry the debut line. Like every platform event it also binds <code>{user.platform}</code> (<code>youtube</code> here), so a shared greeter flow can tell platforms apart.</p>",
            Example: "First-time chatter &rarr; greet them by name with a welcome overlay."),
        ["YouTube.NewSubscriber"] = new(
            Summary: "Fires when someone subscribes to your YouTube channel — the free bell, not a paid membership.",
            Description: "<p><code>User</code> is the new subscriber where YouTube reports one. <code>Payload</code> carries the raw event JSON (also readable as <code>{event.payload}</code>) — the exact field set depends on the connected Streamer.bot build, so parse it with <code>HTTP.ParseJson</code> if you need more.</p>",
            Example: "New subscriber &rarr; a small chime overlay; keep it light, these can come in bursts."),
        ["YouTube.UserBanned"] = new(
            Summary: "Fires when a user is banned from your YouTube live chat.",
            Description: "<p><code>User</code> is who got banned; <code>BanType</code> is the kind of ban YouTube reports and <code>Duration</code> its length in seconds for temporary bans.</p>",
            Example: "Log every ban — who and what kind — to a databank audit table."),
        ["YouTube.UserTimedout"] = new(
            Summary: "Fires when a user is timed out in your YouTube live chat. Requires Streamer.bot 1.0.5 or newer.",
            Description: "<p><code>User</code> is who was timed out and <code>Duration</code> the timeout length in seconds. Older Streamer.bot builds never send this event — Hub warns at connect when it's missing.</p>",
            Example: "Post <code>\"{user.name} is taking a {event.duration}s break\"</code> to your mod log."),
        ["YouTube.MessageDeleted"] = new(
            Summary: "Fires when a message is deleted from your YouTube live chat.",
            Description: "<p><code>User</code> is the message's author and <code>MessageId</code> the deleted message's id. <code>Payload</code> holds the raw event JSON (<code>{event.payload}</code>) if your Streamer.bot build sends more.</p>",
            Example: "Mirror deletions into an overlay chat widget so the on-screen chat stays clean."),
        ["YouTube.JewelsGifted"] = new(
            Summary: "Fires when a viewer sends Jewels, YouTube's gifting currency. Requires Streamer.bot 1.0.5 or newer.",
            Description: "<p><code>User</code> is the sender and <code>Amount</code> the jewel amount as reported. The payload shape for this event isn't published, so <code>Payload</code> / <code>{event.payload}</code> carries the raw JSON for anything beyond that.</p>",
            Example: "Thank jewel gifts in chat and stack the <code>Amount</code> into a databank counter."),
        ["YouTube.StatisticsUpdated"] = new(
            Summary: "Fires periodically while live with refreshed YouTube stream statistics.",
            Description: "<p><code>Viewers</code> is the current concurrent-viewer count, <code>Views</code> the broadcast's total views and <code>Likes</code> its like count. It fires on YouTube's own refresh cadence — treat it as a ticker, not an alert.</p>",
            Example: "Push <code>Likes</code> into a like-goal overlay on every update."),
        ["YouTube.PresentViewers"] = new(
            Summary: "Fires when Streamer.bot reports the viewers currently present in your YouTube chat.",
            Description: "<p><code>Users</code> is a comma-joined list of viewer names and <code>IsLive</code> whether the broadcast is live. Exact shape and cadence depend on the connected Streamer.bot build — <code>Payload</code> / <code>{event.payload}</code> has the raw JSON.</p>",
            Example: "Snapshot who's around into the databank for a lurker raffle."),
        ["YouTube.BroadcastStarted"] = new(
            Summary: "Fires when your YouTube broadcast goes live.",
            Description: "<p><code>Title</code>, <code>BroadcastId</code>, <code>Privacy</code> and <code>Status</code> describe the broadcast as YouTube reports it; how much is filled in depends on the connected Streamer.bot build.</p>",
            Example: "Going live &rarr; reset the session counters and post the go-live announcement."),
        ["YouTube.BroadcastEnded"] = new(
            Summary: "Fires when your YouTube broadcast ends.",
            Description: "<p><code>Title</code>, <code>BroadcastId</code>, <code>Privacy</code> and <code>Status</code> describe the broadcast that ended; how much is filled in depends on the connected Streamer.bot build.</p>",
            Example: "Stream over &rarr; stop live loops and log the session summary."),
        ["YouTube.BroadcastAdded"] = new(
            Summary: "Fires when a new broadcast is created or scheduled on your channel.",
            Description: "<p><code>Title</code>, <code>BroadcastId</code>, <code>Privacy</code> and <code>Status</code> describe the new broadcast; how much is filled in depends on the connected Streamer.bot build.</p>",
            Example: "Announce the newly scheduled stream in your community chat."),
        ["YouTube.BroadcastRemoved"] = new(
            Summary: "Fires when a broadcast is removed from your channel.",
            Description: "<p><code>Title</code>, <code>BroadcastId</code>, <code>Privacy</code> and <code>Status</code> describe the removed broadcast; how much is filled in depends on the connected Streamer.bot build.</p>",
            Example: "Clear any countdown overlay pointing at the removed broadcast."),
        ["YouTube.BroadcastUpdated"] = new(
            Summary: "Fires when a broadcast's details change — title, privacy or status.",
            Description: "<p><code>Title</code>, <code>BroadcastId</code>, <code>Privacy</code> and <code>Status</code> carry the updated values; how much is filled in depends on the connected Streamer.bot build.</p>",
            Example: "Title changed &rarr; refresh the on-screen title lower-third."),
        ["YouTube.BroadcastMonitoringStarted"] = new(
            Summary: "Fires when Streamer.bot starts monitoring a YouTube broadcast.",
            Description: "<p><code>Title</code> and <code>BroadcastId</code> identify the broadcast. This is about Streamer.bot's monitoring session, not the stream itself; <code>Payload</code> / <code>{event.payload}</code> carries whatever else the connected build sends.</p>",
            Example: "Use it as the \"YouTube pipeline is up\" signal in a status overlay."),
        ["YouTube.BroadcastMonitoringEnded"] = new(
            Summary: "Fires when Streamer.bot stops monitoring a YouTube broadcast.",
            Description: "<p><code>Title</code> and <code>BroadcastId</code> identify the broadcast. This is about Streamer.bot's monitoring session, not the stream itself; <code>Payload</code> / <code>{event.payload}</code> carries whatever else the connected build sends.</p>",
            Example: "Show a \"YouTube events paused\" badge so you notice mid-stream."),
        ["YouTube.NewSponsorOnlyStarted"] = new(
            Summary: "Fires when members-only mode is turned on in your YouTube chat.",
            Description: "<p>No dedicated fields — the payload shape depends on the connected Streamer.bot build, so read <code>Payload</code> / <code>{event.payload}</code> for the raw JSON.</p>",
            Example: "Flip a \"members-only chat\" badge onto your overlay."),
        ["YouTube.NewSponsorOnlyEnded"] = new(
            Summary: "Fires when members-only mode is turned off in your YouTube chat.",
            Description: "<p>No dedicated fields — the payload shape depends on the connected Streamer.bot build, so read <code>Payload</code> / <code>{event.payload}</code> for the raw JSON.</p>",
            Example: "Clear the \"members-only chat\" badge from your overlay."),
        ["YouTube.PollStarted"] = new(
            Summary: "Fires when a poll starts in your YouTube live chat.",
            Description: "<p><code>Title</code> carries the poll question where the connected Streamer.bot build provides it; the rest of the payload shape is build-dependent — read <code>Payload</code> / <code>{event.payload}</code> for choices and counts.</p>",
            Example: "Mirror the poll question onto a Visualist overlay."),
        ["YouTube.PollUpdated"] = new(
            Summary: "Fires as votes come in on a running YouTube poll.",
            Description: "<p><code>Title</code> carries the poll question where the connected Streamer.bot build provides it; vote data is build-dependent — read <code>Payload</code> / <code>{event.payload}</code> for the raw JSON.</p>",
            Example: "Keep an on-screen vote tally fresh while the poll runs."),
        ["YouTube.PollClosed"] = new(
            Summary: "Fires when a YouTube poll closes.",
            Description: "<p><code>Title</code> carries the poll question where the connected Streamer.bot build provides it; the final results are build-dependent — read <code>Payload</code> / <code>{event.payload}</code> for the raw JSON.</p>",
            Example: "Announce the winning option in chat."),
        ["YouTube.SevenTVEmoteAdded"] = new(
            Summary: "Fires when a 7TV emote is added to your YouTube channel set.",
            Description: "<p><code>EmoteName</code> is the emote's name where the connected Streamer.bot build provides it; the rest of the payload is build-dependent — <code>Payload</code> / <code>{event.payload}</code> has the raw JSON.</p>",
            Example: "Announce new emotes in chat so viewers try them straight away."),
        ["YouTube.SevenTVEmoteRemoved"] = new(
            Summary: "Fires when a 7TV emote is removed from your YouTube channel set.",
            Description: "<p><code>EmoteName</code> is the emote's name where the connected Streamer.bot build provides it; the rest of the payload is build-dependent — <code>Payload</code> / <code>{event.payload}</code> has the raw JSON.</p>",
            Example: "Note removed emotes in your mod log."),
        ["YouTube.BetterTTVEmoteAdded"] = new(
            Summary: "Fires when a BetterTTV emote is added to your YouTube channel set.",
            Description: "<p><code>EmoteName</code> is the emote's name where the connected Streamer.bot build provides it; the rest of the payload is build-dependent — <code>Payload</code> / <code>{event.payload}</code> has the raw JSON.</p>",
            Example: "Announce new emotes in chat so viewers try them straight away."),
        ["YouTube.BetterTTVEmoteRemoved"] = new(
            Summary: "Fires when a BetterTTV emote is removed from your YouTube channel set.",
            Description: "<p><code>EmoteName</code> is the emote's name where the connected Streamer.bot build provides it; the rest of the payload is build-dependent — <code>Payload</code> / <code>{event.payload}</code> has the raw JSON.</p>",
            Example: "Note removed emotes in your mod log."),

        // ── Kick events ────────────────────────────────────────────────────
        ["Kick.FirstWords"] = new(
            Summary: "Fires the first time a viewer speaks in your Kick chat this stream.",
            Description: "<p><code>User</code> and <code>Message</code> carry the debut line. Like every platform event it also binds <code>{user.platform}</code> (<code>kick</code> here), so a shared greeter flow can tell platforms apart.</p>",
            Example: "First-time chatter &rarr; greet them by name with a welcome overlay."),
        ["Kick.Follow"] = new(
            Summary: "Fires when someone follows your Kick channel.",
            Description: "<p><code>User</code> is the new follower — that's the whole payload. Keep the reaction light; follows can come in bursts.</p>",
            Example: "Any follow &rarr; a small chime and a thank-you in chat."),
        ["Kick.Subscription"] = new(
            Summary: "Fires on a new paid subscription to your Kick channel.",
            Description: "<p><code>User</code> is the subscriber; <code>Months</code> is their total subscribed months and <code>Duration</code> the length of the purchased term.</p>",
            Example: "New sub &rarr; thank them in chat and fire a Visualist sub alert."),
        ["Kick.Resubscription"] = new(
            Summary: "Fires when a Kick viewer renews an existing subscription.",
            Description: "<p><code>User</code> is the subscriber, <code>Months</code> their cumulative streak and <code>Duration</code> the renewed term — the Kick twin of <code>Twitch.Resub</code>.</p>",
            Example: "Resub at 12+ <code>Months</code> &rarr; a loyalty shout-out sized to the streak."),
        ["Kick.GiftSubscription"] = new(
            Summary: "Fires when a viewer gifts a single Kick subscription.",
            Description: "<p><code>Gifter</code> is who paid, <code>Recipient</code> who received it.</p>",
            Example: "Thank the <code>Gifter</code> and welcome the <code>Recipient</code> in one chat line."),
        ["Kick.MassGiftSubscription"] = new(
            Summary: "Fires when a viewer gifts a batch of Kick subscriptions in one go.",
            Description: "<p><code>Gifter</code> is who paid, <code>Count</code> how many subs, and <code>Recipients</code> a comma-joined list of who got them — walk it with <code>Flow.ForEach</code> to greet each one.</p>",
            Example: "Gift bomb of 10+ &rarr; a celebration overlay sized to <code>Count</code>."),
        ["Kick.RewardRedemption"] = new(
            Summary: "Fires when a viewer redeems a Kick channel reward. Requires Streamer.bot 1.0.2 or newer.",
            Description: "<p><code>User</code> is the redeemer, <code>Reward</code> the reward's title, <code>Input</code> the text they typed (empty when the reward asks for none), <code>Cost</code> its price and <code>RewardId</code> its id. Branch on the title with <code>Logic.Switch</code>, just like <code>Twitch.PointRedeem</code>.</p>",
            Example: "<b>\"Hydrate!\"</b> redeem &rarr; the same water-break flow you run for Twitch redeems."),
        ["Kick.KicksGifted"] = new(
            Summary: "Fires when a viewer gifts Kicks — Kick's on-platform tipping currency. Requires Streamer.bot 1.0.2 or newer.",
            Description: "<p><code>User</code> is the sender; <code>Amount</code> is how many Kicks, <code>KickName</code> the gift's name and <code>Tier</code> its tier — the closest Kick equivalent of a Twitch cheer.</p>",
            Example: "Kicks gift of &ge; 100 &rarr; a Visualist alert scaled to <code>Amount</code>."),
        ["Kick.StreamOnline"] = new(
            Summary: "Fires when your Kick channel goes live.",
            Description: "<p><code>Title</code> is the stream title and <code>Category</code> the category you're live in.</p>",
            Example: "Going live &rarr; post the go-live announcement with title and category."),
        ["Kick.StreamOffline"] = new(
            Summary: "Fires when your Kick stream ends.",
            Description: "<p>No dedicated fields — the payload shape depends on the connected Streamer.bot build; <code>Payload</code> / <code>{event.payload}</code> carries the raw JSON.</p>",
            Example: "Stream over &rarr; stop live loops and log the session length."),
        ["Kick.ViewerCountUpdate"] = new(
            Summary: "Fires periodically while live with the current Kick viewer count.",
            Description: "<p><code>Viewers</code> is the live count. A ticker, not an alert — it fires on Kick's own refresh cadence.</p>",
            Example: "Feed <code>Viewers</code> into an on-stream counter or track the session peak."),
        ["Kick.ChannelUpdate"] = new(
            Summary: "Fires when your Kick channel's title or category changes.",
            Description: "<p><code>Title</code> / <code>Category</code> are the new values, <code>OldTitle</code> / <code>OldCategory</code> what they replaced — compare them to react only to what actually changed.</p>",
            Example: "Category changed &rarr; announce the game switch in chat."),
        ["Kick.UserBanned"] = new(
            Summary: "Fires when a user is permanently banned from your Kick chat.",
            Description: "<p><code>User</code> is who was banned, <code>By</code> the moderator who did it and <code>Reason</code> the note they gave.</p>",
            Example: "Write every ban with mod and reason to a databank audit table."),
        ["Kick.UserTimedOut"] = new(
            Summary: "Fires when a user is timed out in your Kick chat.",
            Description: "<p>Same fields as <code>Kick.UserBanned</code> plus <code>Duration</code> — the timeout length in seconds.</p>",
            Example: "Log timeouts and clear the user's lines from your overlay chat."),
        ["Kick.PresentViewers"] = new(
            Summary: "Fires when Streamer.bot reports the viewers currently present in your Kick chat.",
            Description: "<p><code>Users</code> is a comma-joined list of names. Exact shape and cadence depend on the connected Streamer.bot build — <code>Payload</code> / <code>{event.payload}</code> has the raw JSON.</p>",
            Example: "Snapshot who's around into the databank for a lurker raffle."),
        ["Kick.BroadcasterAuthenticated"] = new(
            Summary: "Fires when Streamer.bot completes Kick broadcaster authentication.",
            Description: "<p>A pipeline-status signal, not a stream event. The payload shape depends on the connected Streamer.bot build — read <code>Payload</code> / <code>{event.payload}</code> if you need details.</p>",
            Example: "Flip a \"Kick connected\" badge in a status overlay."),
        ["Kick.BroadcasterChatConnected"] = new(
            Summary: "Fires when Streamer.bot's connection to your Kick chat comes up.",
            Description: "<p>Flow only — no data pins. Pair with <code>Kick.BroadcasterChatDisconnected</code> to track chat-link health.</p>",
            Example: "Chat link up &rarr; clear the \"Kick chat offline\" warning from your overlay."),
        ["Kick.BroadcasterChatDisconnected"] = new(
            Summary: "Fires when Streamer.bot's connection to your Kick chat drops.",
            Description: "<p>Flow only — no data pins.</p>",
            Example: "Chat link down &rarr; show a \"Kick chat offline\" badge so you notice mid-stream."),
        ["Kick.SevenTVEmoteAdded"] = new(
            Summary: "Fires when a 7TV emote is added to your Kick channel set.",
            Description: "<p><code>EmoteName</code> is the emote's name where the connected Streamer.bot build provides it; the rest of the payload is build-dependent — <code>Payload</code> / <code>{event.payload}</code> has the raw JSON.</p>",
            Example: "Announce new emotes in chat so viewers try them straight away."),
        ["Kick.SevenTVEmoteRemoved"] = new(
            Summary: "Fires when a 7TV emote is removed from your Kick channel set.",
            Description: "<p><code>EmoteName</code> is the emote's name where the connected Streamer.bot build provides it; the rest of the payload is build-dependent — <code>Payload</code> / <code>{event.payload}</code> has the raw JSON.</p>",
            Example: "Note removed emotes in your mod log."),
        ["System.Startup"] = new(
            Summary: "Fires once when Hub finishes starting and the script registry has loaded.",
            Description: "<p>The place to spin up long-running <b>Processes</b> — chat collectors, rotating-tip loops, hourly digests — and to reset per-session scratch state. No event payload; the global clock tokens are available.</p>",
            Example: "On startup &rarr; <code>Process.Start</code> the rotating-tips loop and clear the session counters."),
        ["Bus.OnMessage"] = new(
            Summary: "Fires when an internal bus message matches the Type / Source / Target filters.",
            Description: "<p>The receive side of <code>Bus.Send</code> / <code>Bus.Broadcast</code>. <code>Type</code> and <code>Payload</code> carry the matched message; <code>*</code> in a filter matches anything. All matching handlers fire (fan-out).</p>",
            Example: "Listen for a <code>VISUAL_COMPLETE</code> message and continue a multi-step alert."),
        ["Event.Trigger"] = new(
            Summary: "Fires a named internal event and waits for it — the in-graph \"call\" side.",
            Description: "<p>Raises the event named by <code>EventName</code> (or the wired <code>EventName</code> input) and runs the matching <code>Event.Executor</code>(s) to completion, binding their return values before flow continues out of <code>Flow</code> — that's why the <code>+ return</code> pins work. Drag <code>+ variable</code> pins to pass arguments and <code>+ return</code> pins to read values back.</p>",
            Example: "Centralise \"give points\" once in an Executor, then <code>Event.Trigger</code> it from raid, cheer and sub flows."),
        ["Event.Executor"] = new(
            Summary: "Runs the handler body for a named internal event when an Event.Trigger fires it.",
            Description: "<p>The handler side of the event pair. Drag <code>+ variable</code> pins to receive arguments and <code>+ return</code> pins to hand values back. Fan-out: <em>every</em> enabled Executor with that name runs when the event fires, and a later Executor's returns overwrite an earlier one's.</p>",
            Example: "One <code>give_points</code> Executor that every points flow triggers — single source of truth."),
        ["Event.Return"] = new(
            Summary: "Ends an Event.Executor branch and hands return values back to the trigger.",
            Description: "<p>A terminal node — flow in, no flow out. Drag <code>+ return</code> pins to set the values the calling <code>Event.Trigger</code> reads on its own <code>+ return</code> outputs.</p>",
            Example: "Inside <code>lookup_points</code> &rarr; <code>Event.Return</code> the total back to the caller."),
        ["HTTP.WebhookListener"] = new(
            Summary: "Fires when Hub receives an HTTP POST to /webhook/<Name>.",
            Description: "<p>Turns any external service that can POST a webhook into a Phoenix trigger. <code>Body</code> / <code>Payload</code> carry the raw request body; <code>Method</code> and <code>Path</code> describe the request. First-match-wins per <code>Name</code>; parse structured bodies with <code>HTTP.ParseJson</code>.</p>",
            Example: "A Ko-fi / StreamElements webhook &rarr; parse the donation and trigger an alert."),
        ["Schedule.Cron"] = new(
            Summary: "Fires on a cron schedule (minute hour day month weekday).",
            Description: "<p>Standard 5-field cron in <code>CronExpression</code>. <code>Timestamp</code> is the firing time. Good for hourly reminders, scheduled shoutouts and digests.</p>",
            Example: "<code>*/15 * * * *</code> &rarr; post a \"follow for updates\" reminder every 15 minutes."),
        ["Schedule.RunAt"] = new(
            Summary: "Fires once at a specific date and time, then is done.",
            Description: "<p><code>DateTime</code> is ISO-8601. A one-shot timer — it never repeats. Use it for a countdown to a scheduled event.</p>",
            Example: "Fire at <code>2026-01-01T20:00:00</code> &rarr; kick off the New Year overlay."),
        ["Schedule.Recurring"] = new(
            Summary: "Fires repeatedly on a fixed interval, with a running count and an optional chat-activity gate.",
            Description: "<p><code>IntervalSeconds</code> sets the cadence; <code>Count</code> starts at 1 and increments each fire. The <code>MaxCount</code> field is <em>not yet wired</em> — the timer currently runs unbounded, so stop it yourself (gate the body on <code>Count</code> and a <code>Process.Stop</code>, or a state flag) when you need a limit.</p><p><code>MinChatLines</code> gates the interval on chat activity: when it's above 0, a scheduled fire is SKIPPED unless at least that many inbound chat lines arrived since the previous fire — so a message-every-N-minutes timer stays quiet while chat is dead and only speaks up once the room is talking. Leave it at 0 (the default) to fire every interval unconditionally. Only the interval counts here; skipped intervals don't advance <code>Count</code>.</p>",
            Example: "Every 300s with <code>MinChatLines</code> = 10 &rarr; drop a hype line only when at least 10 people chatted since the last one; otherwise wait out another interval."),
        ["State.OnChange"] = new(
            Summary: "Fires when a named state variable changes value.",
            Description: "<p>Watches the <code>state.*</code> namespace (written by <code>State.Set</code>). <code>OldValue</code> and <code>NewValue</code> bracket the change; it only fires when the value actually differs. All matching listeners fire.</p>",
            Example: "When <code>stream_phase</code> flips to <b>intermission</b> &rarr; switch the OBS scene."),
        ["WS.Server"] = new(
            Summary: "Fires when an external WebSocket client sends a text frame to Hub.",
            Description: "<p>Live two-way sibling of <code>HTTP.WebhookListener</code> — clients connect to <code>ws://host:port/ws/&lt;Name&gt;</code> and push frames. <code>Body</code> / <code>Payload</code> carry the frame. The Hub-side server is off by default; enable it in config. All scripts on the same <code>Name</code> fire.</p>",
            Example: "A stream-deck bridge connects &rarr; route its button frames into a <code>Logic.Switch</code>."),
        ["System.Hotkey"] = new(
            Summary: "Fires when a system-wide keystroke is pressed, even when the app isn't focused.",
            Description: "<p><code>Combination</code> is plus-separated — modifiers (<code>Ctrl</code>/<code>Alt</code>/<code>Shift</code>/<code>Win</code>) plus one key. <code>Combo</code> mirrors which combination fired so one script can branch across several. The Hub hotkey service is off by default; enable it in config.</p>",
            Example: "<b>Ctrl+Shift+P</b> &rarr; switch OBS to the \"BRB\" scene from anywhere."),
        ["System.Clipboard"] = new(
            Summary: "Fires when the system clipboard text changes.",
            Description: "<p><code>Text</code> carries the new clipboard text (empty for images / files). Sensitive content (password-manager \"clipboard viewer ignore\" payloads) is dropped, and text is length-capped. Off by default in config for privacy.</p>",
            Example: "Copy a clip URL &rarr; auto-post it in chat with a \"▶ check this\" prefix."),
        ["OBS.Event"] = new(
            Summary: "Fires when OBS emits a WebSocket event whose type matches EventType.",
            Description: "<p>Subscribes to OBS WebSocket v5 events (e.g. <code>CurrentProgramSceneChanged</code>, <code>RecordStateChanged</code>). <code>EventData</code> is the raw JSON of the event — parse it with <code>HTTP.ParseJson</code>. Off by default; enable the OBS client in config.</p>",
            Example: "On <code>RecordStateChanged</code> &rarr; flash a \"now recording\" badge in the overlay."),

        // ═══════════════════════════ FLOW CONTROL ═══════════════════════════
        ["Logic.Branch"] = new(
            Summary: "Two-way branch on a boolean. Exactly one of True / False fires.",
            Description: "<p>The simplest fork. An unwired <code>Condition</code> is treated as <code>true</code>, so it fires the <code>True</code> branch by default. For comparing two values inline, reach for <code>Logic.If</code> instead.</p>",
            Example: "If the chatter <code>IsMod</code> &rarr; run the mod-only path; else nothing."),
        ["Logic.If"] = new(
            Summary: "Compares A against B with a chosen operator and branches True / False.",
            Description: "<p>Operators: <code>==</code>, <code>!=</code>, <code>&gt;</code>, <code>&lt;</code>, <code>&gt;=</code>, <code>&lt;=</code>. The numeric ones only fire True when both sides parse as numbers. <code>A</code> and <code>B</code> share a wildcard type — wire either side first and the other matches it.</p>",
            Example: "If <code>{user.bits}</code> <code>&gt;=</code> <b>1000</b> &rarr; the big-cheer path."),
        ["Logic.Switch"] = new(
            Summary: "Routes flow to the first case whose configured string equals Value; Default catches the rest.",
            Description: "<p>Up to eight case arms (A–H) plus <code>Default</code>. Set each case's expected string inline on the node body. Matching is exact and case-sensitive. The clean way to dispatch on a command token.</p>",
            Example: "Switch on the command: <b>ping</b>, <b>quote</b>, <b>so</b>, <i>default</i>."),
        ["Logic.Sequence"] = new(
            Summary: "Fires every wired output in order, 1 → 8. Each branch runs to completion before the next.",
            Description: "<p>\"Then, then, then.\" Unwired arms are skipped. Each branch's downstream graph runs to <em>full completion</em> — including any delays or awaits — before the next sequence step fires, so a <code>Flow.Delay</code> in arm 1 holds up arm 2.</p>",
            Example: "Raid alert: 1) trigger the overlay, 2) post the shoutout, 3) log to the databank."),
        ["Logic.EnumMatch"] = new(
            Summary: "Checks whether Value matches any entry in an allowed set; branches Match / NoMatch.",
            Description: "<p>Wire a list into <code>List</code>, or fall back to the comma-separated <code>Entries</code> attribute. On a hit, <code>Match</code> fires with the matched entry on <code>MatchedKey</code>; otherwise <code>NoMatch</code>. A wired <code>List</code> matches case-<em>insensitively</em>; the inline <code>Entries</code> attribute is case-<em>sensitive</em>.</p>",
            Example: "Only run if the redeemed reward is one of your alert rewards."),
        ["Flow.ForLoop"] = new(
            Summary: "Counted loop from First to Last inclusive, with the counter on Index.",
            Description: "<p><code>Loop Body</code> fires once per iteration; <code>Index</code> is the current counter, also readable downstream as <code>{loop.index}</code>. <code>Completed</code> fires after the last pass. The engine caps total loop iterations at 500 per script run.</p>",
            Example: "Count down 5 → 1, sending a chat line each iteration."),
        ["Flow.WhileLoop"] = new(
            Summary: "Repeats Loop Body while Condition is true; Completed fires when it goes false.",
            Description: "<p>Re-evaluate the condition somewhere inside the body so the loop can end. As a runaway guard the engine caps total loop iterations at 500 per script run — blowing the budget logs a <code>CriticalError</code> and halts the <em>loop</em> only; the rest of the script continues after it.</p>",
            Example: "Pop the queue until it's empty, handling one entry per pass."),
        ["Flow.ForEach"] = new(
            Summary: "Walks every item in a list, firing Loop Body once per item.",
            Description: "<p><code>Item</code> is the current value; <code>Done</code> fires once after the last item (immediately for an empty list). Accepts the comma-string and JSON-array list forms. Shares the 500-iteration aggregate budget.</p>",
            Example: "<code>Text.Split</code> a comma-list of mod names &rarr; <code>Flow.ForEach</code> &rarr; whisper each."),
        ["Flow.Delay"] = new(
            Summary: "Pauses this flow for a number of seconds, then continues out of Then.",
            Description: "<p>Fractional seconds allowed (e.g. <code>0.5</code>). Non-blocking — other flows keep running — and it honours the script's cancellation, so a shutdown or stop interrupts the wait.</p>",
            Example: "Greet a new sub &rarr; wait 4s &rarr; ask them to introduce themselves."),
        ["Flow.Cooldown"] = new(
            Summary: "Rate-limits a flow with a single per-User cooldown timer; branches Ready / Blocked.",
            Description: "<p>There is one timer, keyed on the <code>User</code> value and armed for <code>max(GlobalCooldown, UserCooldown)</code> seconds — there is no separate channel-wide timer. <code>Ready</code> fires when that long has passed since this user's last Ready; <code>Blocked</code> fires while it holds. <code>User</code> must resolve to a non-empty value (wire it from the chatter) or the node is permanently <code>Blocked</code>.</p>",
            Example: "<b>!hug</b> with a 30s cooldown per chatter so nobody can spam it."),
        ["Flow.FlipFlop"] = new(
            Summary: "Alternates between A and B on every call; the toggle is saved in the databank and survives restarts.",
            Description: "<p>First call fires <code>A</code>, second <code>B</code>, third <code>A</code> again. The toggle is persisted in the databank, so it survives Hub restarts — it only resets when the script is re-saved (re-exported) from Architect.</p>",
            Example: "Toggle a webcam source on / off with one repeated hotkey."),
        ["Flow.DoOnce"] = new(
            Summary: "Fires Out the first time it's reached, then swallows every later call.",
            Description: "<p>A one-shot guard. Its fired state is persisted in the databank — it survives Hub restarts and re-arms only when the script is re-saved (re-exported) from Architect.</p>",
            Example: "Post a \"stream starting\" banner only the first time ever — it stays fired across restarts until you re-save the graph."),
        ["Flow.DoN"] = new(
            Summary: "Fires Loop Body for the first N calls, then Completed on every call after.",
            Description: "<p>A counted gate (not a loop) — it counts across separate invocations. The counter is persisted in the databank, so it survives Hub restarts and only resets when the script is re-saved (re-exported) from Architect.</p>",
            Example: "Allow the first 3 redeems of a limited reward, then point everyone to next time."),
        ["Flow.Select"] = new(
            Summary: "Returns one of A/B/C/D based on Index (0=A … 3=D). Pure data, no flow.",
            Description: "<p>A multiplexer. <code>A</code>–<code>D</code> and <code>Value</code> share a wildcard type — wire any one and the rest match. An out-of-range index returns empty.</p>",
            Example: "Pick a greeting by the day-of-week index."),
        ["Flow.IsValid"] = new(
            Summary: "Branches on whether a value is filled in — True when present, False when empty / null.",
            Description: "<p>Treats empty, <code>0</code>, <code>false</code> and <code>null</code> as \"not valid\". Handy for guarding optional inputs — e.g. only proceed if a <code>Twitch.GetUser</code> lookup actually returned something.</p>",
            Example: "If <code>Twitch.GetUser</code> found the account &rarr; use it; else say \"unknown user\"."),

        // ═══════════════════════════════ LOGIC ════════════════════════════════
        ["Logic.Equals"] = new(
            Summary: "True when A equals B. Pure data — wire it into a boolean input.",
            Description: "<p><code>A</code> and <code>B</code> share a wildcard type; wire either side and the other matches. Compares as text.</p>",
            Example: "Feed <code>{user.name} == {global.host}</code> into a <code>Logic.Branch</code>."),
        ["Logic.NotEquals"] = new(
            Summary: "True when A does not equal B. Pure data.",
            Description: "<p>The inverse of <code>Logic.Equals</code>; same wildcard pairing on <code>A</code> / <code>B</code>.</p>",
            Example: "Only react when the new value differs from the stored one."),
        ["Logic.GreaterThan"] = new(
            Summary: "True when A is numerically greater than B. Pure data.",
            Description: "<p>Both sides must parse as numbers. For an inline branch, <code>Logic.If</code> with the <code>&gt;</code> operator does the comparison and the fork in one node.</p>",
            Example: "Gate a hype alert on <code>viewers &gt; 100</code>."),
        ["Logic.LessThan"] = new(
            Summary: "True when A is numerically less than B. Pure data.",
            Description: "<p>Both sides must parse as numbers.</p>",
            Example: "Warn when a countdown drops below 10."),
        ["Logic.And"] = new(
            Summary: "Boolean AND — true only when both A and B are true.",
            Description: "<p>Inputs are read as booleans (<code>true</code>/<code>1</code>/<code>yes</code> count as true).</p>",
            Example: "Run a perk only when the chatter <code>IsSub</code> AND it's a stream day."),
        ["Logic.Or"] = new(
            Summary: "Boolean OR — true when either A or B is true.",
            Description: "<p>Inputs are read as booleans.</p>",
            Example: "Allow a command for mods OR VIPs."),
        ["Logic.Not"] = new(
            Summary: "Boolean NOT — inverts its input.",
            Description: "<p>Turns true into false and vice-versa. Read as a boolean.</p>",
            Example: "Run the branch only when the user is <b>not</b> a bot."),
        ["Logic.Select"] = new(
            Summary: "Ternary pick — returns A when Cond is true, B when false. Pure data.",
            Description: "<p><code>A</code>, <code>B</code> and <code>Value</code> share a wildcard type — wire one and the rest match. The value version of <code>Logic.Branch</code>.</p>",
            Example: "Choose the alert colour: purple if a sub, else green."),

        // ═══════════════════════════════ ASYNC ════════════════════════════════
        ["Async.Delay"] = new(
            Summary: "Waits a number of milliseconds, then continues out of Done.",
            Description: "<p>Millisecond sibling of <code>Flow.Delay</code>. Non-blocking and cancellation-aware.</p>",
            Example: "Stagger three chat lines 800ms apart."),
        ["Async.Parallel"] = new(
            Summary: "Forks the flow into up to eight branches that run concurrently.",
            Description: "<p>Each branch gets its own isolated copy of the script's local variables, so <code>Var.*</code> writes stay branch-local; use <code>Public.Set</code> when a value must cross branches. Pair with <code>Async.Join</code> to wait for all branches.</p>",
            Example: "Fire the overlay, post chat, and write the databank row all at once."),
        ["Async.Join"] = new(
            Summary: "A cosmetic marker for where parallel branches rejoin — the wait happens with or without it.",
            Description: "<p>An <code>Async.Parallel</code> block <em>always</em> waits for all its branches to finish at its end; <code>Async.Join</code> does not create that barrier, it just marks where the branches visually rejoin into a single sequential flow. Placing one (or not) doesn't change when execution continues.</p>",
            Example: "Run three lookups in parallel, then post one combined summary."),
        ["Async.Timeout"] = new(
            Summary: "Branches on whether the current handler is still within a time budget.",
            Description: "<p>Checks elapsed time since the script started: <code>OnTime</code> fires if under <code>MS</code> milliseconds, <code>Late</code> if it has run long. A guard for flows that must not drag.</p>",
            Example: "If a slow API path has eaten 5s already &rarr; skip to a fallback."),
        ["Async.WaitForEvent"] = new(
            Summary: "Pauses until a bus message of EventType arrives, or the timeout expires.",
            Description: "<p><code>Received</code> fires with the message body on <code>Payload</code>; <code>Timeout</code> fires after <code>TimeoutMS</code> with no match. Bridges an async bus reply into a waiting flow.</p>",
            Example: "Ask Visualist to run an interaction, then wait for its result event."),
        ["Async.WaitForVisual"] = new(
            Summary: "Fires a Visualist trigger and waits for it to report complete, or times out.",
            Description: "<p>The blocking partner to <code>Visual.Trigger</code>: <code>Done</code> fires when the widget animation finishes (the browser sends back a completion), <code>Timeout</code> after <code>TimeoutMS</code>. Drag extra pins to pass event data with the trigger.</p>",
            Example: "Play a 4s entrance animation, wait for <code>Done</code>, then post the welcome line."),
        ["Chat.WaitForNext"] = new(
            Summary: "Waits for the next chat message, optionally filtered by user and / or command.",
            Description: "<p><code>Got</code> fires with <code>Username</code> / <code>Message</code> on a match; <code>TimedOut</code> fires after <code>TimeoutMS</code> (default 30s). The basis of \"reply to confirm\" and quick chat mini-games.</p>",
            Example: "Ask \"type !yes to confirm\" &rarr; wait for that user's <b>!yes</b>."),
        ["Chat.PeekRecent"] = new(
            Summary: "Snapshots the most recent chat lines without waiting. Non-blocking.",
            Description: "<p>Returns up to 50 buffered lines as parallel <code>Usernames</code> / <code>Messages</code> lists (same length, oldest-first). <code>N</code> ≤ 0 returns the whole buffer.</p>",
            Example: "Pick a random recent chatter for a giveaway from the last 50 lines."),

        // ═══════════════════════════════ MATH ═════════════════════════════════
        ["Math.Add"] = new(Summary: "Adds A + B. Pure data.", Example: "Add a bonus to a points payout."),
        ["Math.Subtract"] = new(Summary: "Subtracts A − B. Pure data.", Example: "Deduct a cost from a balance."),
        ["Math.Multiply"] = new(Summary: "Multiplies A × B. Pure data.", Example: "Scale a particle count by a multiplier."),
        ["Math.Divide"] = new(
            Summary: "Divides A ÷ B and returns a float. Pure data.",
            Description: "<p><code>B</code> defaults to <code>1</code> so a fresh node never divides by zero. The result is a float — keep it on a float wire if you need the decimals.</p>",
            Example: "Turn a bits total into a dollar estimate."),
        ["Math.Modulo"] = new(Summary: "Remainder of A ÷ B. Pure data.", Description: "<p><code>B</code> defaults to <code>1</code> to avoid mod-by-zero.</p>", Example: "Every 5th redeem (<code>count % 5 == 0</code>) &rarr; a bonus."),
        ["Math.Min"] = new(Summary: "Returns the smaller of A and B. Pure data.", Example: "Cap a payout at the user's balance."),
        ["Math.Max"] = new(Summary: "Returns the larger of A and B. Pure data.", Example: "Floor a multiplier at 1."),
        ["Math.Clamp"] = new(Summary: "Limits Val to the range [Min, Max]. Pure data.", Example: "Keep an overlay opacity between 0 and 1."),
        ["Math.Abs"] = new(Summary: "Returns Val with any negative sign removed. Pure data.", Example: "Turn a signed delta into a magnitude."),
        ["Math.Floor"] = new(Summary: "Rounds Val down to the nearest whole number. Pure data.", Example: "Round a fractional dollar figure down."),
        ["Math.Ceil"] = new(Summary: "Rounds Val up to the nearest whole number. Pure data.", Example: "Round a progress percentage up."),
        ["Math.Random"] = new(
            Summary: "Returns a random integer in [Min, Max) — Min possible, Max never.",
            Description: "<p>Half-open like a dice roll: <code>Min=1, Max=7</code> gives 1–6. One node's output is evaluated once and shared by every consumer wired to it — it only re-rolls per node instance and per loop iteration, so drop a second <code>Math.Random</code> (or loop) when you want an independent roll.</p>",
            Example: "Roll a d20 for a chat command."),
        ["Math.Chance"] = new(
            Summary: "Rolls a percentage and branches Success / Fail.",
            Description: "<p><code>% Rate</code> is the percent chance of <code>Success</code> (0–100); the rest fires <code>Fail</code>. The one Math node with a flow pin.</p>",
            Example: "25% chance &rarr; a rare alert; 75% &rarr; the normal one."),

        // ═══════════════════════════════ TEXT ═════════════════════════════════
        ["Text.Builder"] = new(
            Summary: "Fills {Arg1}/{Arg2}/{Arg3} placeholders in a template string. Pure data.",
            Description: "<p>Edit <code>Template</code> inline on the node body — a wired <code>Template</code> input is <em>ignored</em> by the exporter. If you need a wired (run-time) template, use <code>Text.Format</code> instead. Named-slot substitution — wire the slots you use.</p>",
            Example: "Build <code>\"{Arg1} just followed!\"</code> from the follower name."),
        ["Text.Format"] = new(
            Summary: "Fills {A}/{B}/{C}/{D} placeholders in a template string. Pure data.",
            Description: "<p>Like <code>Text.Builder</code> with four slots. <code>Template</code> is inline-editable or wired.</p>",
            Example: "Format <code>\"{A} raided with {B} viewers\"</code>."),
        ["Text.Contains"] = new(Summary: "True when Source contains Search anywhere. Case-insensitive, pure data.", Example: "Flag a message that mentions a banned word."),
        ["Text.Replace"] = new(Summary: "Replaces every Find with With in Source. Case-sensitive, pure data.", Example: "Swap a placeholder for the live value."),
        ["Text.Split"] = new(Summary: "Splits Source into a list on a delimiter. Pure data.", Description: "<p><code>Delimiter</code> defaults to a comma; edit inline or wire it.</p>", Example: "Split <code>\"a,b,c\"</code> into a list to <code>Flow.ForEach</code> over."),
        ["Text.JoinList"] = new(Summary: "Joins a list into one string with a separator between items. Pure data.", Description: "<p><code>Separator</code> defaults to <code>\", \"</code>.</p>", Example: "Turn a winners list into <code>\"alice, bob, cara\"</code>."),
        ["Text.Length"] = new(Summary: "Returns the character count of a string. Pure data.", Example: "Reject an over-long custom title."),
        ["Text.ToUpper"] = new(Summary: "Converts a string to UPPER CASE. Pure data.", Example: "Shout a HYPE word."),
        ["Text.ToLower"] = new(Summary: "Converts a string to lower case. Pure data.", Example: "Normalise a username before a lookup."),
        ["Text.ParseCommand"] = new(
            Summary: "Splits a chat command into N parts on spaces, stripping the leading !.",
            Description: "<p>The last segment soaks up the remainder. <code>\"!cmd add Test hi there\"</code> with <code>Segments=3</code> gives <code>[cmd, add, \"Test hi there\"]</code>. Pair with <code>Array.Unpack</code> to read the parts.</p>",
            Example: "Parse <b>!so @user a nice message</b> into command / target / message."),

        // ════════════════════════════ COLLECTIONS ═════════════════════════════
        ["Array.Make"] = new(Summary: "Builds a list from up to eight wired values. Pure data.", Description: "<p>Unwired slots fall back to their inline default (empty). Pair with <code>Array.Get</code> / <code>Array.Unpack</code> on the other side.</p>", Example: "Assemble three option strings into a pickable list."),
        ["Array.Literal"] = new(Summary: "Outputs a hardcoded list from a comma-separated inline value. Pure data.", Description: "<p>Edit <code>Items</code> inline (e.g. <code>\"alpha, beta, gamma\"</code>). Use <code>Array.Make</code> when the values come from wires.</p>", Example: "A fixed list of tip messages to rotate through."),
        ["Array.Get"] = new(Summary: "Returns the element at a zero-based Index. Pure data.", Description: "<p>Out-of-range indices <em>clamp</em> to the nearest valid element (first or last); only an empty list yields empty.</p>", Example: "Read item 0 — the command word — from a parsed line."),
        ["Array.Length"] = new(Summary: "Returns the number of elements in a list. Pure data.", Example: "Count the entrants in a giveaway list."),
        ["Array.Contains"] = new(Summary: "True when a list contains an element equal to Value. Case-sensitive, pure data.", Example: "Check whether a user is in your VIP list."),
        ["Array.Unpack"] = new(
            Summary: "Splits a list into Item 0–4 plus Rest (everything from index 5 on).",
            Description: "<p>Wire only the slots you need; chain another <code>Array.Unpack</code> off <code>Rest</code> for more. Pairs with <code>Text.ParseCommand</code> for argument parsing.</p>",
            Example: "Unpack a parsed command into command / target / message slots."),
        ["Array.Push"] = new(Summary: "Appends Value to a list and outputs the new list.", Description: "<p>A flow node — <code>Done</code> fires after the append and the <code>List</code> output carries the extended list for chaining.</p>", Example: "Add the latest winner to a running winners list."),
        ["Array.Filter"] = new(Summary: "Keeps only the elements that contain a substring. Case-sensitive, pure data.", Example: "Filter a chatter list down to those whose name contains \"bot\"."),
        ["Array.Sort"] = new(Summary: "Returns the list sorted ascending. Pure data.", Description: "<p>Alphabetical by default; turn on <code>Numeric</code> to sort as numbers (non-numeric items go last).</p>", Example: "Sort a leaderboard of scores."),
        ["Array.Shuffle"] = new(Summary: "Returns the list in a fresh random order. Pure data.", Description: "<p>One node's output is evaluated once and shared by every consumer wired to it — it only re-shuffles per node instance and per loop iteration. Great for raffles.</p>", Example: "Shuffle entrants before drawing."),
        ["Array.Reverse"] = new(Summary: "Returns the list with element order flipped. Pure data.", Example: "Show most-recent-first."),
        ["Array.Unique"] = new(Summary: "Removes duplicate values, keeping the first of each. Case-sensitive, pure data.", Example: "Dedupe a chatter list before counting."),

        // ══════════════════════════════ VALUES ════════════════════════════════
        ["Value.String"] = new(Summary: "Outputs a fixed string. Edit inline; wire Input to override. Pure data.", Example: "A reusable channel name constant."),
        ["Value.Int"] = new(Summary: "Outputs a fixed whole number. Edit inline; wire Input to override. Pure data.", Example: "A bits threshold of 1000."),
        ["Value.Float"] = new(Summary: "Outputs a fixed decimal number. Edit inline; wire Input to override. Pure data.", Example: "A 0.5 opacity constant."),
        ["Value.Bool"] = new(Summary: "Outputs a fixed boolean. Edit inline; wire Input to override. Pure data.", Example: "A feature flag you flip in one place."),

        // ═════════════════════════════ CONVERT ════════════════════════════════
        ["Convert.ToInt"] = new(Summary: "Parses a value as a whole number. Pure data.", Description: "<p>The <code>Default</code> fills the input only when the socket is <em>unwired</em>; a wired value that won't parse yields <code>0</code>, not the Default.</p>", Example: "Turn a parsed command argument into a number."),
        ["Convert.ToFloat"] = new(Summary: "Parses a value as a decimal number. Pure data.", Description: "<p>The <code>Default</code> fills the input only when the socket is <em>unwired</em>; a wired value that won't parse yields <code>0.0</code>, not the Default.</p>", Example: "Read a fractional amount from text."),
        ["Convert.ToString"] = new(
            Summary: "Converts any value to its string form. Pure data.",
            Description: "<p>Accepts int, float, bool, list or string. To keep a float's decimals, wire it from a float source — routing through an int-typed wire rounds it.</p>",
            Example: "Stringify a computed total for a chat message."),
        ["Convert.ToBool"] = new(Summary: "Converts a string to a boolean. Pure data.", Description: "<p>The truthy set is <code>true</code> / <code>1</code> / <code>yes</code> / <code>on</code> / <code>y</code> / <code>t</code> (case-insensitive and trimmed); anything else is <code>false</code>. The <code>Default</code> fills the input only when the socket is <em>unwired</em> — a wired value that isn't truthy yields <code>false</code>, not the Default.</p>", Example: "Read a yes/no setting into a branch."),

        // ════════════════════════════ VARIABLES ═══════════════════════════════
        ["Var.Get"] = new(
            Summary: "Reads a run-local variable by name. Pure data.",
            Description: "<p>The variable name lives on the inline editor, not a socket. <code>Var.*</code> is run-local (per-execution) — it's <em>not</em> persisted to the databank and is gone when the run ends; inside an <code>Async.Parallel</code> fork it stays branch-local. Use <code>Public.Get</code> to cross branches, and <code>global.*</code> / <code>user.*</code> (the DB.* nodes) or <code>state.*</code> for anything that must survive the run.</p>",
            Example: "Read this graph's running combo counter."),
        ["Var.Set"] = new(
            Summary: "Writes a run-local variable by name.",
            Description: "<p>Stores under <code>var.&lt;name&gt;</code> for the rest of this run only — <code>Var.*</code> is run-local, not saved to the databank, and gone when the run ends (use <code>global.*</code> / <code>user.*</code> or <code>state.*</code> to persist). The freshly-set value is relayed on the <code>Value</code> output socket (readable downstream as <code>{var.&lt;name&gt;}</code>). Writes inside a parallel branch do <b>not</b> merge back to the parent — that's <code>Public.Set</code>'s job.</p>",
            Example: "Cache a <code>Math.Random</code> roll so several nodes see the same value."),
        ["Public.Get"] = new(
            Summary: "Reads a run-wide variable by key. Pure data.",
            Description: "<p><code>public.*</code> is script-run scratch — fast, visible across parallel branches, and gone when the script ends (not saved to the databank).</p>",
            Example: "Read a value a sibling parallel branch published."),
        ["Public.Set"] = new(
            Summary: "Writes a run-wide variable by key, visible across parallel branches.",
            Description: "<p>The explicit \"share across branches\" lever: a <code>Public.Set</code> inside an <code>Async.Parallel</code> branch merges back to the parent on join. Vanishes at script end.</p>",
            Example: "Have one branch compute a total the join step then posts."),

        // ══════════════════════════════ STATE ═════════════════════════════════
        ["State.Set"] = new(
            Summary: "Writes a value into the persistent state-machine store under a name.",
            Description: "<p>Stored in the databank's reserved <code>state.*</code> namespace — survives restarts and is visible to every script. Always writes; only fires <code>State.OnChange</code> when the value actually changed.</p>",
            Example: "Set <code>stream_phase</code> to <b>live</b> at the top of the stream."),
        ["State.Get"] = new(Summary: "Reads a state value by name. Pure data.", Description: "<p>Inlines the raw <code>{state.&lt;name&gt;}</code> token — an unset state passes the literal token through rather than resolving to empty, so use <code>State.Exists</code> to detect a never-set key.</p>", Example: "Read the current <code>stream_phase</code> to decide an overlay."),
        ["State.Switch"] = new(
            Summary: "Branches on the current value of a state variable — Case A / Case B / Default.",
            Description: "<p>Reads the named state and routes to the matching case (set the case strings inline), or <code>Default</code> if none match. A state-machine dispatcher in one node.</p>",
            Example: "Route by <code>stream_phase</code>: <b>starting</b>, <b>live</b>, <i>default</i>."),
        ["State.Exists"] = new(Summary: "True when a state key has been set. Pure data.", Description: "<p>Distinguishes a set-but-empty value from a never-set one.</p>", Example: "Initialise defaults only the first time a phase is missing."),
        ["State.Delete"] = new(Summary: "Removes a state key from the store.", Example: "Clear a temporary phase flag at the end of a segment."),
        ["State.ListKeys"] = new(Summary: "Returns a list of all currently-set state keys. Pure data.", Example: "Audit which state flags are live."),

        // ════════════════════════════ DATABANK ════════════════════════════════
        ["DB.GetVariable"] = new(
            Summary: "Reads a databank variable by key, with a Default fallback. Pure data.",
            Description: "<p>The persistent key/value store under <code>%AppData%/PhoenixControls</code>. Returns <code>Default</code> (0 by default) when the key is unset.</p>",
            Example: "Read <code>global.raid_count</code> to show the running total."),
        ["DB.SetVariable"] = new(
            Summary: "Writes a value to a databank variable. Survives restarts.",
            Description: "<p>Keys are yours to name. Pair with <code>DB.GetVariable</code> and <code>DB.Increment</code> for counters and per-user data (e.g. <code>user.&lt;name&gt;.points</code>).</p>",
            Example: "Store the last winner's name for a <b>!lastwinner</b> command."),
        ["DB.Increment"] = new(
            Summary: "Atomically adds Amount to a numeric cell and returns the new value.",
            Description: "<p>With a <code>RowId</code> wired it targets that table cell and is race-safe — concurrent scripts incrementing the same cell never lose a write. With no <code>RowId</code> it increments a databank variable instead, which is <em>not</em> atomic and ignores <code>TableName</code>. Either way a blank cell counts from <code>0</code>, and a non-numeric cell refuses the write. <code>Amount</code> defaults to 1.</p>",
            Example: "On cheer &rarr; add <code>{user.bits}</code> to the chatter's points row and announce the total."),
        ["DB.CheckExists"] = new(Summary: "Branches True / False on whether a databank key exists.", Example: "First-seen detection — greet a chatter only if they have no row yet."),
        ["DB.DeleteVar"] = new(Summary: "Deletes a databank variable by key.", Description: "<p>Reserved namespaces (<code>state.*</code>, engine-internal keys) are protected — use <code>State.Delete</code> for state.</p>", Example: "Reset a per-session counter."),
        ["DB.RowCount"] = new(Summary: "Returns the number of rows in a User_ table. Pure data.", Example: "Count how many viewers have a points row."),
        ["DB.FindRow"] = new(Summary: "Finds the first row where Column equals Value; branches Found / NotFound with the RowId.", Description: "<p>The lookup primitive for <code>User_*</code> tables — find a viewer's row by name, then read or update cells on it.</p>", Example: "Find a user's row by name, then <code>DB.SetCell</code> their points."),
        ["DB.GetCell"] = new(Summary: "Reads a single cell (Column) from a row (RowId) in a table. Pure data.", Example: "Read a viewer's stored points."),
        ["DB.SetCell"] = new(Summary: "Writes a single cell (Column) on a row (RowId) in a table.", Example: "Update a viewer's win count."),
        ["DB.InsertRow"] = new(Summary: "Inserts a new row and returns its NewRowId.", Description: "<p>Does <em>not</em> create the table — the <code>User_*</code> table must already exist (create it yourself first). Inserting into a missing table is a logged no-op and <code>NewRowId</code> reads <code>0</code>. Two InsertRow nodes don't clobber each other's <code>NewRowId</code>.</p>", Example: "Add a new entry to a quotes table and keep its id."),
        ["DB.DeleteRow"] = new(Summary: "Deletes a row by RowId from a table.", Example: "Remove a quote by its id."),
        ["DB.ClearTable"] = new(Summary: "Removes every row from a User_ table.", Example: "Wipe a per-stream leaderboard at the start of a stream."),
        ["DB.GetColumn"] = new(Summary: "Returns every value in a column as a list. Pure data.", Description: "<p>Wire the result straight into <code>Flow.ForEach</code> or <code>Array.*</code>. Unlike the other databank reads, a missing or mistyped table here (and on <code>DB.FetchRow</code>) <em>throws</em> and aborts the whole script run rather than degrading quietly — make sure the table exists first.</p>", Example: "Pull all entrant names for a giveaway draw."),
        ["DB.FetchRow"] = new(
            Summary: "Fetches a whole row by RowId; branches Found / NotFound with the Row object.",
            Description: "<p>The <code>Row</code> output is the full record. Set the optional <code>KnownColumns</code> hint to surface <code>&lt;Row&gt;.&lt;column&gt;</code> suggestions in the inline-value autocomplete — a design-time aid only; the live row carries whatever columns it actually has.</p>",
            Example: "Fetch a viewer's row, then read several of its cells inline."),

        // ══════════════════════════════ QUEUE ═════════════════════════════════
        ["Queue.Push"] = new(Summary: "Enqueues an EventID + Payload pair onto a named FIFO queue.", Description: "<p>Backed by a databank variable, so the queue persists and is visible across scripts until consumed.</p>", Example: "Queue up shoutouts so they play one at a time."),
        ["Queue.Pop"] = new(Summary: "Dequeues the oldest entry; branches to its EventID / Payload or to Empty.", Description: "<p>Atomic — pops one entry and fires <code>Done</code>, or fires <code>Empty</code> when nothing's waiting.</p>", Example: "Pop the next shoutout every 60s until the queue drains."),
        ["Queue.Length"] = new(Summary: "Returns how many entries are in the queue. Pure data.", Example: "Show \"3 shoutouts pending\" on the overlay."),
        ["Queue.Clear"] = new(Summary: "Empties a queue.", Example: "Clear the shoutout queue when the stream ends."),

        // ═══════════════════════════ TWITCH DATA ══════════════════════════════
        ["Twitch.GetUser"] = new(
            Summary: "Looks up a Twitch user's profile and the channel's last game / title.",
            Description: "<p>Fetched through your Streamer.bot bot account. Returns id / login / display name / avatar / account-created plus the channel's last <code>Game</code> and <code>ChannelTitle</code> and the mod / sub / vip flags. <code>Username</code> defaults to <code>{user.name}</code> — the current chatter.</p>",
            Example: "On <b>!so</b> &rarr; GetUser the target &rarr; shout their last <code>Game</code>."),
        ["Twitch.GetStream"] = new(
            Summary: "Reads a channel's live state, title and last category on demand.",
            Description: "<p>Through Streamer.bot. A request-time companion to the Stream.GoingLive / Stream.SessionEnd events — ask for the current state whenever you need it. For your own channel <code>IsLive</code> and <code>Uptime</code> report from the live on/offline tracking; for an arbitrary channel there's no Streamer.bot live path so <code>IsLive</code> is false and <code>ViewerCount</code> stays 0.</p>",
            Example: "Show the raider's last category in the raid alert."),
        ["Twitch.CheckRole"] = new(
            Summary: "Resolves a user's mod / sub / vip / broadcaster flags.",
            Description: "<p>Looks up the roles via your bot account. <code>IsBroadcaster</code> is derived by comparing the login to your configured broadcaster name. <code>Username</code> defaults to <code>{user.name}</code>.</p>",
            Example: "Gate a command on <code>IsMod</code> OR <code>IsVip</code>."),
        ["Twitch.IsOnline"] = new(
            Summary: "Checks whether a channel is currently live. Leave Channel empty for your own.",
            Description: "<p>For your own channel it answers from Hub's stream-online tracking. Arbitrary other channels report not-live honestly (Streamer.bot has no live-status lookup) but still resolve their last game / title.</p>",
            Example: "Only run the auto-host flow if the target is live."),
        ["Twitch.GetFollowAge"] = new(
            Summary: "Returns how long a user has followed — days, a formatted string, and the follow date.",
            Description: "<p>Through your bot account. <code>IsFollowing</code> is false when they don't follow. <code>Formatted</code> reads like \"1y 12d\" / \"45d\". <code>Username</code> defaults to <code>{user.name}</code>.</p>",
            Example: "On <b>!followage</b> &rarr; reply with the <code>Formatted</code> age."),
        ["Twitch.LastActive"] = new(
            Summary: "Branches on whether a user chatted within the last N minutes.",
            Description: "<p>Backed by Hub's in-memory chat tracking (no API call). <code>Active</code> fires if they chatted within <code>Minutes</code>, <code>Inactive</code> otherwise, with <code>MinutesAgo</code> on the side.</p>",
            Example: "Only auto-shout a raider if they've been <code>Inactive</code> in your chat."),
        ["Twitch.GetViewers"] = new(
            Summary: "Fetches the list of active chatters as a list.",
            Description: "<p>Uses Streamer.bot's active-viewers request. Returns the logins as a list — iterate with <code>Flow.ForEach</code> or pick one with <code>Array.Shuffle</code> + <code>Array.Get</code>.</p>",
            Example: "Award loyalty points to everyone currently chatting."),

        // ═══════════════════════════ PLATFORM DATA ════════════════════════════
        ["YouTube.GetUser"] = new(
            Summary: "Looks up a YouTube user's profile and channel-role flags.",
            Description: "<p><b>No live data yet:</b> Streamer.bot 1.0.x has no YouTube user-info path, so this node's outputs currently stay empty — treat it as a placeholder until Streamer.bot adds the lookup. When it lands it will fetch through the <code>Phoenix: YT Get User</code> action and return <code>Id</code>, <code>DisplayName</code> and <code>ProfileImage</code> plus the role flags: <code>IsMod</code>, <code>IsSub</code> (channel member / sponsor) and <code>IsBroadcaster</code> (channel owner). <code>Username</code> defaults to <code>{user.name}</code> — the current chatter.</p>",
            Example: "Gate a members-only command on <code>IsSub</code> in YouTube chat."),
        ["Kick.GetUser"] = new(
            Summary: "Looks up a Kick user's profile and role flags.",
            Description: "<p>Fetched through the <code>Phoenix: Kick Get User</code> action — connect Streamer.bot 1.0.2 or newer and import the Kick set from the action pack. <code>Id</code>, <code>Login</code>, <code>DisplayName</code> and <code>ProfileImage</code> fill in, but Streamer.bot 1.0.x's Kick lookup carries no role facts, so <code>IsMod</code> and <code>IsSub</code> <em>always read false</em> — don't gate on them here. <code>Username</code> defaults to <code>{user.name}</code> — the current chatter.</p>",
            Example: "On <b>!profile</b> &rarr; GetUser the chatter &rarr; show their <code>ProfileImage</code> on an overlay card."),

        // ═══════════════════════════ PLATFORMS · CHAT ═════════════════════════
        ["Chat.Send"] = new(
            Summary: "Posts a chat message to the checked platforms — Twitch, YouTube and/or Kick — in one node.",
            Description: "<p>The send-side mirror of the unified <code>Chat.Message</code> trigger. The Twitch/YouTube/Kick checkmarks on the node body pick the default targets; the optional <code>Platforms</code> input OVERRIDES them at runtime with a single name or a comma list (<code>twitch, kick</code>), so a script can answer on whichever platform a message came from — wire <code>Chat.Message</code>'s <code>Platform</code> output straight in. Text between <code>{</code> and <code>}</code> resolves against the flow context. Each platform's own limit applies (Twitch/Kick 500 characters, YouTube 200) — an over-limit message is dropped for that platform with a log line. Twitch sends through your configured Streamer.bot chat action; YouTube/Kick through their Phoenix action-pack actions.</p>",
            Example: "Reply to <b>!ping</b> everywhere; or wire <code>Platform</code> &rarr; <code>Platforms</code> to answer only where it was asked."),
        ["Chat.MessageCount"] = new(
            Summary: "Outputs how many chat messages the Hub has counted since it started, across all platforms. Pure data.",
            Description: "<p>A single <code>Count</code> value — every inbound chat line on Twitch, YouTube and Kick since Hub launch, with your bot's own messages excluded. It's monotonic: it never resets mid-run, so the useful pattern is DELTAS. Store it in a <code>State.Set</code> / <code>Var.Set</code>, then next time compare the new count against the saved one to answer \"how many new messages since?\" and gate on chat activity. It counts every message the Hub sees, whether or not any script matches it.</p>",
            Example: "On a 60s <code>Schedule.Recurring</code>, only fire a hype message when <code>Chat.MessageCount</code> minus the value you stored last tick is &ge; 10 — quiet chat stays quiet."),

        // ═══════════════════════════ PLATFORMS · TWITCH ═══════════════════════
        ["Twitch.Reply"] = new(
            Summary: "Posts a threaded reply to a specific Twitch chat message.",
            Description: "<p><code>MessageId</code> names the chat line to reply under — leave it unwired to reply to the message that triggered the script (it defaults to the <code>Chat.Message</code> trigger's own id, <code>{event.message_id}</code>). <code>Message</code> is the reply body. A reply is still a chat message, so the same <b>500-character</b> cap as <code>Chat.Send</code> applies. Dispatched through <code>Phoenix: Reply</code> (Twitch action pack imported).</p>",
            Example: "Answer <b>!ping</b> as a threaded reply with <code>\"@{user.name} pong\"</code>."),
        ["Twitch.Timeout"] = new(
            Summary: "Times a user out for a number of seconds.",
            Description: "<p>Your bot account must hold moderator privileges. Duration is bounded to Twitch's 1s–2-week range.</p>",
            Example: "Auto-timeout 30s on a banned-words match."),
        ["Twitch.Ban"] = new(
            Summary: "Permanently bans a user. There is no undo from inside Phoenix.",
            Description: "<p>Use sparingly; requires mod privileges. Pair with <code>Twitch.Unban</code> for reversible, tested mod flows.</p>",
            Example: "A spam-bot detector &rarr; ban with reason <b>\"link spam\"</b>."),
        ["Twitch.Unban"] = new(Summary: "Lifts a ban on a user.", Description: "<p>Requires mod privileges.</p>", Example: "An appeal command that unbans on a mod's approval."),
        ["Twitch.Mod"] = new(Summary: "Grants a user moderator status.", Description: "<p>Broadcaster-level action.</p>", Example: "Promote a trusted regular with one command."),
        ["Twitch.Unmod"] = new(Summary: "Removes a user's moderator status.", Example: "Step a temporary mod back down."),
        ["Twitch.Vip"] = new(Summary: "Grants a user VIP status.", Example: "Reward a top supporter with VIP."),
        ["Twitch.Unvip"] = new(Summary: "Removes a user's VIP status.", Example: "Rotate VIPs at the end of a month."),
        ["Twitch.SlowMode"] = new(Summary: "Sets chat slow-mode to N seconds; 0 turns it off.", Description: "<p>Requires mod privileges.</p>", Example: "Switch on 10s slow-mode during a raid spike."),
        ["Twitch.FollowerMode"] = new(Summary: "Sets follower-only chat to N minutes; -1 turns it off.", Description: "<p>Requires mod privileges.</p>", Example: "Lock chat to followers during heavy spam."),
        ["Twitch.Marker"] = new(Summary: "Drops a stream marker for the VOD, with an optional description.", Example: "Mark a highlight moment from a hotkey."),
        ["Twitch.UpdateChannel"] = new(Summary: "Updates the stream title and / or game.", Description: "<p>At least one of <code>Title</code> / <code>GameId</code> must be set; broadcaster-level.</p>", Example: "Change the category from a <b>!game</b> command."),
        ["Twitch.CreateClip"] = new(
            Summary: "Creates a clip of the last ~30 seconds and emits the URL.",
            Description: "<p>Twitch needs a beat to finish the clip — the flow continues once <code>ClipUrl</code> is available, with <code>ClipOk</code> telling you if it worked. Only succeeds while you're live. <code>Duration</code> is clamped to 5–60s.</p>",
            Example: "<b>!clip</b> &rarr; CreateClip &rarr; post the <code>ClipUrl</code> in chat."),
        ["Twitch.Announcement"] = new(
            Summary: "Posts a Twitch /announce — the highlighted chat banner.",
            Description: "<p>Goes out through your bot account. Limited to 500 characters; the banner colour is set by the Streamer.bot action.</p>",
            Example: "Big raid (&ge; 50) &rarr; announce the raider to the channel."),
        ["Twitch.Shoutout"] = new(
            Summary: "Sends a Twitch shoutout for a user — the native /shoutout.",
            Description: "<p>Dispatched through the Phoenix shoutout action in Streamer.bot. An empty <code>User</code> is skipped silently and the flow continues.</p>",
            Example: "On <b>!so @name</b> &rarr; shout them out, then post their last category."),
        ["Twitch.CreatePrediction"] = new(
            Summary: "Opens a channel-points prediction with up to five outcomes.",
            Description: "<p><code>Title</code> plus <code>OutcomeA</code>–<code>OutcomeE</code> (wire only the ones you use) and a <code>DurationSec</code> window. Resolving a prediction needs an id Streamer.bot can't hand back, so close it from the Twitch UI or your own action.</p>",
            Example: "On a hype moment &rarr; open a \"will they clutch it?\" prediction."),
        ["Twitch.EndPoll"] = new(
            Summary: "Ends the currently-active Twitch poll.",
            Description: "<p>Streamer.bot ends whichever poll is currently running — the <code>PollId</code> input is advisory only, and no Phoenix node can hand you a real PollId, so leave it empty and rely on there being one active poll.</p>",
            Example: "Close a quick poll once chat has clearly decided."),

        // ═══════════════════════════ PLATFORMS · YOUTUBE ══════════════════════
        ["YouTube.SetTitle"] = new(
            Summary: "Updates the running YouTube broadcast's title.",
            Description: "<p>Sets the live broadcast's title via <code>Phoenix: YT Set Title</code> (Streamer.bot 1.0 or newer, YouTube action pack imported). An empty <code>Title</code> is skipped with a log line.</p>",
            Example: "Change the broadcast title from a <b>!title</b> command."),
        ["YouTube.SetDescription"] = new(
            Summary: "Updates the running YouTube broadcast's description.",
            Description: "<p>Sets the live broadcast's description via <code>Phoenix: YT Set Description</code> (Streamer.bot 1.0 or newer, YouTube action pack imported). An empty <code>Description</code> is skipped with a log line.</p>",
            Example: "At go-live &rarr; write today's schedule into the description."),
        ["YouTube.Timeout"] = new(
            Summary: "Times a user out of your YouTube live chat for a number of seconds.",
            Description: "<p>Dispatched through <code>Phoenix: YT Timeout</code> (Streamer.bot 1.0 or newer, YouTube action pack imported). <code>Sec</code> defaults to 300; an empty <code>User</code> or a duration below 1 second is skipped with a log line.</p>",
            Example: "Auto-timeout 300s on a banned-words match in YouTube chat."),
        ["YouTube.Ban"] = new(
            Summary: "Permanently hides a user from your YouTube live chat. There is no undo from inside Phoenix.",
            Description: "<p>YouTube's permanent moderation action, dispatched through <code>Phoenix: YT Ban</code> (Streamer.bot 1.0 or newer, YouTube action pack imported). Lifting it again means YouTube Studio — use <code>YouTube.Timeout</code> for anything reversible.</p>",
            Example: "A spam-bot detector &rarr; ban the account on sight."),
        ["YouTube.CreatePoll"] = new(
            Summary: "Opens a poll in your YouTube live chat.",
            Description: "<p><code>Title</code> is the poll question; <code>Choices</code> is a comma-separated list that the Hub splits into up to <b>4</b> options (YouTube's max). YouTube polls take no duration over Streamer.bot. Dispatched through <code>Phoenix: YT Create Poll</code> (Streamer.bot 1.0 or newer, YouTube action pack imported). DoAction can't hand a poll id back, so pair it with <code>YouTube.EndPoll</code>, which acts on the active poll.</p>",
            Example: "<b>!poll</b> &rarr; open a \"which game next?\" poll from chat suggestions."),
        ["YouTube.EndPoll"] = new(
            Summary: "Ends the active YouTube poll.",
            Description: "<p>Acts on the currently running poll — Streamer.bot's YT End Poll sub-action takes no arguments, so there is no id to wire. Dispatched through <code>Phoenix: YT End Poll</code> (Streamer.bot 1.0 or newer, YouTube action pack imported).</p>",
            Example: "Close the poll once chat has clearly decided."),

        // ═══════════════════════════ PLATFORMS · KICK ═════════════════════════
        ["Kick.Reply"] = new(
            Summary: "Posts a threaded reply to a specific Kick chat message.",
            Description: "<p><code>MessageId</code> names the chat line to reply under; <code>Message</code> is the reply body. A reply is still a chat message, so the same <b>500-character</b> Kick chat cap as <code>Chat.Send</code> applies. Dispatched through <code>Phoenix: Kick Reply</code> (Streamer.bot 1.0.2 or newer, Kick action pack imported).</p>",
            Example: "Answer a <b>!so</b> request inline, right under the requester's message."),
        ["Kick.Timeout"] = new(
            Summary: "Times a user out of your Kick chat for a number of seconds.",
            Description: "<p>Dispatched through <code>Phoenix: Kick Timeout</code> (Streamer.bot 1.0.2 or newer, Kick action pack imported). <code>Sec</code> defaults to 300; an empty <code>User</code> or a duration below 1 second is skipped with a log line. Lift one early with <code>Kick.Untimeout</code>.</p>",
            Example: "Auto-timeout 300s on a banned-words match in Kick chat."),
        ["Kick.Ban"] = new(
            Summary: "Permanently bans a user from your Kick channel.",
            Description: "<p><code>Reason</code> is an optional note (Kick's ban action carries it — YouTube's does not). Dispatched through <code>Phoenix: Kick Ban</code> (Streamer.bot 1.0.2 or newer, Kick action pack imported). Unlike the YouTube side there is a reverse path — pair it with <code>Kick.Unban</code> for reversible, tested mod flows.</p>",
            Example: "A spam-bot detector &rarr; ban, with <code>Kick.Unban</code> wired to an appeal command."),
        ["Kick.Unban"] = new(
            Summary: "Lifts a ban on a Kick user.",
            Description: "<p>Dispatched through <code>Phoenix: Kick Unban</code> (Streamer.bot 1.0.2 or newer, Kick action pack imported). An empty <code>User</code> is skipped with a log line.</p>",
            Example: "An appeal command that unbans on a mod's approval."),
        ["Kick.Untimeout"] = new(
            Summary: "Lifts an active timeout on a Kick user early.",
            Description: "<p>Dispatched through <code>Phoenix: Kick Untimeout</code> (Streamer.bot 1.0.2 or newer, Kick action pack imported). An empty <code>User</code> is skipped with a log line.</p>",
            Example: "A mod's <b>!forgive</b> &rarr; end the timeout ahead of schedule."),
        ["Kick.SetTitle"] = new(
            Summary: "Updates the Kick channel's stream title.",
            Description: "<p>Dispatched through <code>Phoenix: Kick Set Title</code> (Streamer.bot 1.0.2 or newer, Kick action pack imported). An empty <code>Title</code> is skipped with a log line.</p>",
            Example: "Change the title from a <b>!title</b> command."),
        ["Kick.SetCategory"] = new(
            Summary: "Sets the Kick channel's category by name.",
            Description: "<p><code>Category</code> is the category's name — the wrapper's Set Channel Category sub-action resolves it to the matching Kick category. Dispatched through <code>Phoenix: Kick Set Category</code> (Streamer.bot 1.0.2 or newer, Kick action pack imported). An empty <code>Category</code> is skipped with a log line.</p>",
            Example: "Switch the category from a <b>!game</b> command."),
        ["Kick.DeleteMessage"] = new(
            Summary: "Removes one message from Kick chat by its MessageId.",
            Description: "<p><b>Not available yet:</b> Streamer.bot 1.0.x has no Kick delete-message sub-action, so this node currently does nothing — keep it out of live moderation flows until Streamer.bot adds it. When available it will remove one message via <code>Phoenix: Kick Delete Message</code>; an empty <code>MessageId</code> is skipped with a log line.</p>",
            Example: "A link-filter flow &rarr; delete the offending line, then warn the chatter."),
        ["Kick.SetRewardCost"] = new(
            Summary: "Changes the price of a Kick channel reward.",
            Description: "<p><b>Not available yet:</b> Streamer.bot 1.0.x has no Kick set-reward-cost sub-action, so this node currently does nothing. When available it will set a reward's price via <code>Phoenix: Kick Set Reward Cost</code> — <code>RewardId</code> identifies the reward and <code>Cost</code> the new price.</p>",
            Example: "Double a reward's cost during a hype segment, restore it after."),
        ["Kick.SetRewardEnabled"] = new(
            Summary: "Turns a Kick channel reward on or off.",
            Description: "<p><b>Not available yet:</b> Streamer.bot 1.0.x has no Kick set-reward-enabled sub-action, so this node currently does nothing. When available it will flip a reward on or off via <code>Phoenix: Kick Set Reward Enabled</code> — <code>RewardId</code> identifies the reward and <code>Enabled</code> the new state.</p>",
            Example: "Disable a \"pick my game\" reward while a fixed schedule runs."),

        // ═══════════════════════════ PLATFORMS · DISCORD ══════════════════════
        ["Discord.SendMessage"] = new(
            Summary: "Posts a plain message to a Discord channel via your bot token.",
            Description: "<p><code>ChannelId</code> is the numeric channel snowflake. On success the message id lands on <code>MessageId</code>; failures land on <code>Error</code>. Configure the bot token in Hub settings.</p>",
            Example: "Stream goes live &rarr; post the title to <b>#stream-log</b>."),
        ["Discord.SendEmbed"] = new(
            Summary: "Posts a rich embed (title, description, colour, URL) to a channel.",
            Description: "<p>At least one of title / description / URL must be set. <code>Color</code> accepts a hex string (<code>#5865F2</code>) or a decimal int. Errors surface on <code>Error</code>.</p>",
            Example: "New clip &rarr; embed it in <b>#highlights</b> with the clip link."),
        ["Discord.AddRole"] = new(Summary: "Grants a Discord role to a guild member.", Description: "<p>All three ids are snowflakes; the bot needs Manage Roles and a higher role than the one it assigns. Errors surface on <code>Error</code>.</p>", Example: "New tier-3 sub (linked accounts) &rarr; grant the Subscribers role."),
        ["Discord.RemoveRole"] = new(Summary: "Revokes a Discord role from a guild member.", Description: "<p>Same permission and hierarchy rules as <code>Discord.AddRole</code>.</p>", Example: "\"VIP for a day\" &rarr; AddRole &rarr; Delay 24h &rarr; RemoveRole."),
        ["Discord.React"] = new(Summary: "Adds a reaction from the bot to an existing message.", Description: "<p><code>Emoji</code> is a unicode character or a custom-emoji handle in <code>name:id</code> form.</p>", Example: "React ✅ to a submission post."),
        ["Discord.GetUser"] = new(Summary: "Looks up a Discord user by id — username, display name, avatar URL.", Description: "<p>Avatar is empty when the user has no custom avatar. Errors surface on <code>Error</code>.</p>", Example: "Resolve a linked Discord profile for a welcome embed."),
        ["Discord.Webhook"] = new(
            Summary: "Posts a message to a Discord channel through a webhook URL — no bot token needed.",
            Description: "<p>The simplest Discord path: paste a channel webhook URL into <code>URL</code> and send <code>Msg</code>. Great for a quick stream-log post without setting up a bot. Use <code>Discord.SendMessage</code> when you need bot features like embeds or roles.</p>",
            Example: "Stream goes live &rarr; webhook a \"now live\" line to your announcements channel."),

        // ═══════════════════════════ PLATFORMS · HTTP ═════════════════════════
        ["HTTP.Get"] = new(
            Summary: "Performs an HTTP GET and returns the status code and body.",
            Description: "<p><code>Headers</code> is an optional raw header block. For structured replies, follow with <code>HTTP.ParseJson</code>; failures land on <code>Error</code> (secrets redacted). Wrap risky calls so a 5xx doesn't halt the script.</p>",
            Example: "<b>!weather</b> &rarr; GET an API &rarr; ParseJson &rarr; post the temperature."),
        ["HTTP.Post"] = new(Summary: "Sends an HTTP POST with a body and content type.", Description: "<p><code>ContentType</code> defaults to <code>application/json</code>. Status, response and error come back on their sockets.</p>", Example: "POST a JSON payload to a webhook endpoint."),
        ["HTTP.Put"] = new(Summary: "Sends an HTTP PUT with a body.", Description: "<p>Same shape as <code>HTTP.Post</code>; <code>ContentType</code> defaults to JSON.</p>", Example: "Update a record on a REST API."),
        ["HTTP.Patch"] = new(Summary: "Sends an HTTP PATCH with a body.", Description: "<p>Same shape as <code>HTTP.Put</code>.</p>", Example: "Partially update a remote resource."),
        ["HTTP.Delete"] = new(Summary: "Sends an HTTP DELETE.", Description: "<p>Returns status / response / error.</p>", Example: "Remove a queued item from an external service."),
        ["HTTP.ParseJson"] = new(
            Summary: "Extracts a value from JSON by a dot-path. Pure data.",
            Description: "<p>Dot-and-bracket path (e.g. <code>data.items.0.name</code>). The matched leaf comes back on <code>Value</code> (objects / arrays stringified); a parse miss surfaces on <code>Error</code>.</p>",
            Example: "Pull <code>main.temp</code> out of a weather API response."),

        // ═══════════════════════════ PLATFORMS · FILE ═════════════════════════
        ["File.ReadText"] = new(
            Summary: "Reads a UTF-8 text file from the sandboxed data folder.",
            Description: "<p>Paths are sandboxed under Hub's data folder — absolute paths and <code>..</code> escapes are rejected. Content comes back on <code>Content</code>; failures on <code>Error</code>.</p>",
            Example: "Read a one-quote-per-line file &rarr; <code>Text.Split</code> &rarr; pick a random quote."),
        ["File.WriteText"] = new(Summary: "Writes UTF-8 text to a sandboxed file (append or overwrite).", Description: "<p>Parent folders are created on demand; <code>Append</code> controls overwrite vs append.</p>", Example: "Log every <b>!so</b> to <code>shoutouts.log</code> in append mode."),
        ["File.ReadJSON"] = new(Summary: "Reads a file and validates it parses as JSON.", Description: "<p><code>Content</code> carries the raw text either way; a parse error is reported on <code>Error</code> so the script can fall back. Pair with <code>HTTP.ParseJson</code> to pull values.</p>", Example: "Load a goals file at startup and set overlay vars from it."),
        ["File.WriteJSON"] = new(Summary: "Writes Content to a file only after confirming it's valid JSON.", Description: "<p>A malformed payload returns a parser message on <code>Error</code> and leaves the file untouched — no half-written corrupt JSON. <code>Append</code> writes JSON-Lines (one record per line).</p>", Example: "On stop &rarr; write a stream-summary JSON record."),

        // ═══════════════════════════ PLATFORMS · AUDIO / WEB ══════════════════
        ["Audio.Play"] = new(Summary: "Plays a local audio file. Fire-and-forget.", Description: "<p><code>Volume</code> (0–1) multiplies the global base volume. Errors surface on <code>Error</code>.</p>", Example: "Play an alert sting on a big cheer."),
        ["Audio.PlayTts"] = new(Summary: "Speaks text aloud with Windows text-to-speech.", Description: "<p>Optional <code>Voice</code> and <code>Rate</code>; unknown voices fall back to the system default. Overlapping calls don't cut each other off.</p>", Example: "Read a resub message aloud."),
        ["Audio.SetVolume"] = new(Summary: "Sets the global base volume applied to all later audio.", Description: "<p>In-process only — resets on Hub restart.</p>", Example: "Duck overlay audio during a serious moment."),
        ["API.Call"] = new(Summary: "A minimal HTTP GET that returns just the response body.", Description: "<p>The quick-and-simple sibling of <code>HTTP.Get</code> — no status code or headers, body on <code>Response</code> (in <code>{result.api_response}</code>).</p>", Example: "Fetch a one-line random-fact API for a command."),
        ["StreamerBot.DoAction"] = new(
            Summary: "Fires a Streamer.bot action directly by id or name, bypassing the Phoenix action pack.",
            Description: "<p>For actions you've built yourself in Streamer.bot. <code>ActionId</code> is a GUID or an action name (auto-detected). <code>{result.sb_dispatched}</code> reports whether it went out.</p>",
            Example: "Trigger one of your own custom Streamer.bot actions from a redeem."),
        ["StreamerBot.GetUser"] = new(Summary: "An alias of Twitch.GetUser — looks a user up through Streamer.bot.", Description: "<p>An alias of <code>Twitch.GetUser</code>. The <code>Data</code> output carries the <em>display name only</em>, not a full JSON record — don't <code>HTTP.ParseJson</code> it. The rest of the profile lands in the <code>{user.*}</code> tokens, same as <code>Twitch.GetUser</code>.</p>", Example: "Fetch a viewer's display name for a custom profile card."),

        // ══════════════════════════════ OBS ═══════════════════════════════════
        ["OBS.SetScene"] = new(Summary: "Switches OBS to a named scene.", Description: "<p>Dispatched through the Phoenix OBS action in Streamer.bot. The scene is matched by exact name.</p>", Example: "A \"BRB\" hotkey &rarr; switch to the break scene."),
        ["OBS.SetSourceVisible"] = new(Summary: "Shows or hides a source in a scene.", Description: "<p><code>Scene</code> and <code>Source</code> are both required; <code>Visible</code> toggles it.</p>", Example: "\"Show my face\" redeem &rarr; reveal the webcam for 30s."),
        ["OBS.SetFilterVisible"] = new(Summary: "Enables or disables a filter on a source.", Description: "<p>Names a scene, source and filter; <code>Visible</code> toggles the filter.</p>", Example: "Flip on a colour-grade filter during a hype moment."),
        ["OBS.RefreshBrowserSource"] = new(Summary: "Reloads a browser source, optionally at a new URL.", Description: "<p>Sets the source's URL and reloads it — handy for resetting an overlay.</p>", Example: "Reload your alerts overlay after editing it in Visualist."),
        ["OBS.TakeScreenshot"] = new(Summary: "Saves a screenshot of a source to a file path.", Description: "<p>All three of scene, source and path are required.</p>", Example: "Capture a frame for a \"caught in 4k\" bit."),
        ["OBS.StartRecording"] = new(Summary: "Tells OBS to start recording.", Example: "Auto-record when you go live."),
        ["OBS.StopRecording"] = new(Summary: "Tells OBS to stop recording.", Example: "Stop recording when the stream ends."),
        ["OBS.StartStreaming"] = new(Summary: "Tells OBS to start streaming.", Example: "A master \"go live\" hotkey."),
        ["OBS.StopStreaming"] = new(Summary: "Tells OBS to stop streaming.", Example: "An \"end stream\" hotkey after a goodbye."),
        ["OBS.SaveReplayBuffer"] = new(Summary: "Saves the OBS replay buffer to disk.", Description: "<p>The replay buffer must be running in OBS.</p>", Example: "<b>!replay</b> &rarr; save the last 30s clip locally."),
        ["OBS.SetSourcePosition"] = new(
            Summary: "Moves a scene item to an X / Y position in pixels from the top-left.",
            Description: "<p><code>Scene</code> and <code>Source</code> name the scene item; <code>X</code> / <code>Y</code> are its new position in pixels from the scene's top-left corner. Fires <code>Done</code> when applied. Driven directly through Hub's own OBS WebSocket connection when it's enabled in config, falling back to the <code>Phoenix: OBS Source …</code> Streamer.bot action otherwise; <code>{result.obs_error}</code> is empty on success or a diagnostic on failure.</p>",
            Example: "Slide the webcam into the corner for a segment, then move it back."),
        ["OBS.SetSourceScale"] = new(
            Summary: "Scales a scene item — 1.0 is original size, 2.0 double, 0.5 half.",
            Description: "<p><code>Scene</code> and <code>Source</code> name the scene item; <code>ScaleX</code> / <code>ScaleY</code> are its scale factors (<code>1.0</code> = original, <code>2.0</code> = double, <code>0.5</code> = half). Fires <code>Done</code> when applied. Driven directly through Hub's own OBS WebSocket connection when it's enabled in config, falling back to the <code>Phoenix: OBS Source …</code> Streamer.bot action otherwise; <code>{result.obs_error}</code> is empty on success or a diagnostic on failure.</p>",
            Example: "Punch a source up to 1.5&times; on a hype moment, then back to 1.0."),
        ["OBS.SetSourceRotation"] = new(
            Summary: "Rotates a scene item — 0 upright, 90 a quarter-turn clockwise.",
            Description: "<p><code>Scene</code> and <code>Source</code> name the scene item; <code>Degrees</code> is the rotation (<code>0</code> upright, <code>90</code> a quarter-turn clockwise). Fires <code>Done</code> when applied. Driven directly through Hub's own OBS WebSocket connection when it's enabled in config, falling back to the <code>Phoenix: OBS Source …</code> Streamer.bot action otherwise; <code>{result.obs_error}</code> is empty on success or a diagnostic on failure.</p>",
            Example: "Spin a prop 90&deg; as a gag on a redeem."),

        // ═════════════════════════════ VISUALS ════════════════════════════════
        ["Visual.Trigger"] = new(
            Summary: "Fires a named trigger on a Visualist widget — the most-used overlay node.",
            Description: "<p>Targets a widget on a layer by <code>LayerID</code> / <code>WidgetID</code> / <code>TriggerName</code>. Wire a comma-list into <code>Args</code> (read widget-side as <code>{Args1}</code>, <code>{Args2}</code>…), and drag <code>+ variable</code> pins for named event data. Fire-and-forget; use <code>Async.WaitForVisual</code> to wait for it to finish.</p>",
            Example: "<b>!hype</b> &rarr; fire the <code>burst</code> trigger on the alerts widget, passing the username."),
        ["Visual.SetText"] = new(Summary: "Updates a text element on the overlay live.", Description: "<p>Broadcast to every connected browser source. <code>Id</code> is the compositor element id.</p>", Example: "Push a live sub-goal count to the overlay."),
        ["Visual.SetVisible"] = new(Summary: "Shows or hides an overlay widget live.", Description: "<p>Broadcast to all connected browser sources.</p>", Example: "Hide the \"starting soon\" panel when you go live."),
        ["Visual.SetProperty"] = new(Summary: "Sets an arbitrary CSS / DOM property on an overlay widget.", Description: "<p><code>Key</code> is the property (e.g. <code>opacity</code>, <code>transform</code>), <code>Value</code> the new value. Broadcast live.</p>", Example: "Fade a widget by setting its <code>opacity</code>."),
        ["Chat.Overlay.Push"] = new(Summary: "Pushes a chat line into an on-overlay chat widget.", Description: "<p>Addresses a chat widget by <code>WidgetID</code>; <code>Color</code> tints the name (defaults to green). Broadcast to all browser sources.</p>", Example: "Mirror highlighted chat onto a stream overlay."),
        ["Chat.Overlay.Clear"] = new(Summary: "Clears all messages from an overlay chat widget.", Example: "Wipe the overlay chat between segments."),

        // ══════════════════════════════ BUS ═══════════════════════════════════
        ["Bus.Send"] = new(Summary: "Sends a targeted IPC message to a named endpoint.", Description: "<p>Fire-and-forget to a named target (e.g. <code>Visualist</code>) with a <code>Type</code> and JSON <code>Payload</code>. Receive it with <code>Bus.OnMessage</code>.</p>", Example: "Tell Visualist to switch its active scene."),
        ["Bus.Broadcast"] = new(Summary: "Broadcasts an IPC message to every listener on the bus.", Description: "<p>Fan-out sibling of <code>Bus.Send</code> — no target, everyone matching the <code>Type</code> hears it.</p>", Example: "Announce a global \"raid incoming\" event several scripts react to."),

        // ════════════════════════════ PROCESS ═════════════════════════════════
        ["Process.Start"] = new(
            Summary: "Launches a live instance of a process — its triggers run until stopped.",
            Description: "<p>Starts a new instance of the linked process: its event triggers (Schedule, <code>on_chat</code>, …) go live and keep running — like a normal script, with no run-duration cap — until <code>Process.Stop</code> ends <em>this</em> instance. The node's extra pins mirror the process's <code>Process.Entry</code> parameters and are read in the body as <code>{param.&lt;name&gt;}</code>. <code>InstanceId</code> outputs the new instance's id; wire it into a <code>Process.Stop</code>. Start again to run a second concurrent instance.</p>",
            Example: "On <code>System.Startup</code> &rarr; start a rotating-tips process that posts every 10 minutes."),
        ["Process.Stop"] = new(
            Summary: "Stops a running process instance by InstanceId.",
            Description: "<p>Ends the instance whose id is wired into <code>InstanceId</code> (from <code>Process.Start</code>): its live triggers go dormant and its schedule timers are cancelled. Unknown / already-stopped ids are a no-op.</p>",
            Example: "On stream-offline &rarr; stop the rotating-tips instance you started at go-live."),
        ["Process.Entry"] = new(Summary: "The 'on start' trigger of a process; declares its start params.", Description: "<p>Its flow output runs once when an instance starts. Drag <code>+ output</code> pins to declare start parameters — passed by <code>Process.Start</code> and read anywhere in the body as <code>{param.&lt;name&gt;}</code>.</p>", Example: "Declare a <code>message</code> param the loop posts each pass."),
        ["Process.Exit"] = new(Summary: "The 'on stop' trigger of a process (cleanup hook).", Description: "<p>Its <code>On Stop</code> output runs once when the instance is stopped — use it for cleanup. A live process never 'returns'.</p>", Example: "Post a goodbye line when the process stops."),

        // ══════════════════════════════ MACROS ════════════════════════════════
        ["Macro.Call"] = new(
            Summary: "Calls a macro like a single node — its body is inline-expanded at export.",
            Description: "<p>The call site's pins mirror the macro's <code>Macro.Entry</code> parameters (in) and <code>Macro.Exit</code> returns (out). Build a flow once as a macro and reuse it everywhere; edits to the macro flow through to every call.</p>",
            Example: "A <code>give_points</code> macro called from raid, cheer and sub flows."),
        ["Macro.Entry"] = new(Summary: "The parameter inlet of a macro (authoring marker).", Description: "<p>Drag <code>+ input</code> pins to declare the macro's parameters; they appear as inputs on every <code>Macro.Call</code>.</p>", Example: "Declare <code>user</code> and <code>amount</code> inputs for a points macro."),
        ["Macro.Exit"] = new(Summary: "The return surface of a macro (authoring marker).", Description: "<p>Drag <code>+ output</code> pins to declare return values; they appear as outputs on every <code>Macro.Call</code>.</p>", Example: "Return the new total from a points macro."),

        // ═════════════════════════════ GIVEAWAY ═══════════════════════════════
        ["Giveaway.Create"] = new(
            Summary: "Opens a new giveaway. The default one is what every public node targets.",
            Description: "<p>With <code>SetDefault</code> on, this becomes the active giveaway the other giveaway nodes (and the Hub panel) point at. The same engine the Hub Giveaway panel drives.</p>",
            Example: "<b>!giveaway start</b> &rarr; create \"Stream Deck\" as the active giveaway."),
        ["Giveaway.Ticket"] = new(
            Summary: "Adds tickets for a user (or reads their count with Increment 0) — optionally charging channel points per ticket.",
            Description: "<p>More tickets means better odds at the draw. <code>IsSub</code> / <code>IsMod</code> record the entrant's badges (wire them from Chat.Message's outputs) — they feed the Subscriber/Moderator bonus weighting at draw time. Adding is ignored once the giveaway is closed, but reads still work. When the giveaway's per-user cap stops the entry from landing in full, the <code>Limit</code> flow output fires instead of <code>Done</code> — wire it to react (e.g. tell the user they already hold the maximum).</p><p><b>Price:</b> pick a currency table on <code>PriceTable</code> (columns <code>name</code> / <code>currency</code>; the ▾ picker offers a one-click <i>Create 'ChannelPoints' table</i>) and each ticket costs channel points. With <code>Public</code> on, the price comes from the targeted giveaway's <i>Ticket price</i> setting in the Hub panel; with <code>Public</code> off, the node's own <code>Price</code> pill applies. A user who can't afford the entry triggers the <code>NoFunds</code> flow output — nothing is charged or granted. <code>IsAll</code> ignores <code>Increment</code> and buys as many tickets as the user's points and the per-user cap allow. <code>Purchased</code> outputs how many tickets this call actually bought. Leave <code>PriceTable</code> empty and the node behaves exactly as before (free entry).</p>",
            Example: "<b>!ticket</b> &rarr; buy 1 ticket for 100 points; <b>!ticket all</b> (IsAll) &rarr; convert the whole balance; broke &rarr; <code>NoFunds</code> replies \"you need 100 points\"."),
        ["Giveaway.Close"] = new(Summary: "Closes a giveaway to new entries and reports the totals.", Description: "<p>Outputs the combined <code>TotalTickets</code> and the number of unique <code>EntrantCount</code>.</p>", Example: "<b>!giveaway close</b> &rarr; announce \"42 entrants, 60 tickets\"."),
        ["Giveaway.Winner"] = new(Summary: "Draws a winner weighted by ticket count.", Description: "<p>More tickets, higher chance. With a subscriber bonus set on the giveaway (Hub panel), each entrant's current sub status is checked via Streamer.bot right before the draw and a subscriber's tickets weigh &times;factor. A Moderator bonus factor applies the same way — a moderator's tickets weigh &times;factor from the recorded <code>IsMod</code> flag, multiplicative with the subscriber factor. Returns the <code>WinnerName</code> and their <code>WinnerTickets</code>.</p>", Example: "<b>!giveaway draw</b> &rarr; announce the weighted winner."),
        ["Giveaway.Id"] = new(Summary: "Outputs the id of the current default giveaway. Pure data.", Description: "<p>Empty when no default is set — wire it into a giveaway node's selector to target it explicitly.</p>", Example: "Pass the active giveaway id into a logging branch."),
        ["Giveaway.IsActive"] = new(
            Summary: "Outputs true while a giveaway is open and accepting entries. Pure data.",
            Description: "<p>The source of truth for a giveaway's live state. Leave the <code>Giveaway</code> selector empty to follow the app-wide default giveaway; the ▾ picker lists your giveaways (matched by id, key or title). Reads <code>false</code> once the giveaway is closed or drawn — or when nothing matches / no default is set.</p>",
            Example: "Gate <b>!ticket</b> behind a Branch: IsActive &rarr; enter; else reply \"no giveaway running\"."),

        // ══════════════════════════════ SYSTEM ════════════════════════════════
        ["System.Log"] = new(
            Summary: "Writes a line to the Hub System Log at a chosen level.",
            Description: "<p>Levels: <code>System</code>, <code>Communication</code>, <code>StreamEvent</code>, <code>LogicExecution</code> (default), <code>VisualEvent</code>, <code>CriticalError</code>, <code>Debug</code>. The first reach-for tool when tracing a flow while testing.</p>",
            Example: "Log <code>\"reached the sub branch with tier {user.tier}\"</code> while debugging."),
        ["System.GetTime"] = new(Summary: "Returns the current time broken into pieces. Pure data.", Description: "<p>Full <code>Time</code> string plus hours / minutes / seconds and a Unix timestamp. Same values as the <code>{system.*}</code> tokens.</p>", Example: "Greet differently before noon vs evening."),
        ["System.GetDate"] = new(Summary: "Returns the current date broken into pieces. Pure data.", Description: "<p>Full <code>Date</code> string plus day / month / year and the month name.</p>", Example: "Post a special message on a stream anniversary."),
        ["Time.SecondsSinceLastFire"] = new(
            Summary: "Seconds since this Key was last checked — and stamps it fresh. Pure data.",
            Description: "<p>A lightweight throttle keyed by any string. The first read for a key returns a huge value (\"never fired\"), and evaluating it stamps the key fresh. One node's output is evaluated once and shared by every consumer wired to it (a single stamp) — it only recomputes per node instance and per loop iteration, so drop a second node for an independent check.</p>",
            Example: "Throttle an alert to at most once per 60s with a per-alert key."),
        ["Time.StreamUptime"] = new(
            Summary: "How long the stream has been live, in several forms. Pure data.",
            Description: "<p>Anchored at the configured stream-start (Hub startup by default). Human <code>Uptime</code> (\"1h 23m\") plus raw seconds / minutes / hours and the ISO <code>StartedAt</code>. Same values as the <code>{stream.*}</code> tokens.</p>",
            Example: "<b>!uptime</b> &rarr; reply with the live <code>Uptime</code>."),

        // ═════════════════════════════ REROUTE ════════════════════════════════
        ["Flow.Reroute"] = new(
            Summary: "A tidy wire knot — routes a wire through a midpoint with no effect on data or flow.",
            Description: "<p>Drag it onto a wire to bend cabling cleanly around the canvas. Carries its type straight through; set <code>Label</code> to leave yourself a note on the wire.</p>",
            Example: "Tuck a long flow wire around a busy cluster of nodes."),
    };

    // ════════════════════════════════════════════════════════════════════
    //  Primer data — grounded in the engine's {token} resolvers
    //  (ScriptEngine.Variables.cs). The keyboard reference is built live from
    //  ArchitectHotkeyCatalog instead.
    // ════════════════════════════════════════════════════════════════════
    internal readonly record struct TokenFamily(string Root, string Dot, string Label, string Note, string[] Tokens);
    internal readonly record struct StorageRoot(string Root, string Desc, string Example);

    internal static readonly IReadOnlyList<TokenFamily> TokenFamilies = new[]
    {
        new TokenFamily("user.", "#C893BC", "Identity & event payload",
            "Bound by the trigger at the top of the flow — the field set varies per event (chat, sub, raid, cheer, redeem).",
            new[] { "user.name", "user.id", "user.platform", "user.message", "user.command", "user.args", "user.is_mod", "user.is_sub", "user.is_vip", "user.is_broadcaster", "user.is_anonymous", "user.color_hex", "user.sub_months", "user.tier", "user.bits", "user.points", "user.viewers", "user.count", "user.total_gifts", "user.gifter", "user.recipient", "user.reward", "user.input" }),
        new TokenFamily("event.", "#7FBED1", "Event metadata",
            "The shape of the firing event — webhook body, schedule tick, state change, bus message, and the fields platform events carry (a Super Chat's amount / currency, a stream's title / category, a chat message's id). <code>event.ret.*</code> / <code>event.arg.*</code> carry values across Event nodes.",
            new[] { "event.iscommand", "event.payload", "event.body", "event.method", "event.path", "event.timestamp", "event.count", "event.type", "event.message", "event.message_id", "event.title", "event.category", "event.amount", "event.currency", "event.duration", "event.combo", "event.text", "event.data", "state.name", "state.new_value", "state.old_value" }),
        new TokenFamily("result.", "#9CC97A", "Command outputs",
            "Written by the node that just ran — HTTP, file, Discord, JSON, clip, OBS and state lookups drop their return values here.",
            new[] { "result.http_status", "result.http_body", "result.http_error", "result.api_response", "result.json_value", "result.file_content", "result.discord_message_id", "result.obs_error", "clip.url", "clip.ok", "result.state_value", "result.sb_dispatched" }),
        new TokenFamily("loop.", "#E5A24E", "Iterator",
            "Inside <code>Flow.ForLoop</code> / <code>Flow.ForEach</code> bodies — the current counter and item. Nested loops also expose a per-id <code>loop.index_xxxxxx</code> form.",
            new[] { "loop.index", "loop.item" }),
        new TokenFamily("role.", "#C893BC", "Role check",
            "After a <code>Twitch.CheckRole</code> node resolves the chatter's permissions.",
            new[] { "role.is_mod", "role.is_sub", "role.is_vip", "role.is_broadcaster" }),
        new TokenFamily("follow.", "#7FBED1", "Follow age",
            "After <code>Twitch.GetFollowAge</code> resolves the chatter's follow relationship.",
            new[] { "follow.days", "follow.formatted", "follow.date", "follow.is_following" }),
        new TokenFamily("param.", "#E5A24E", "Process start params",
            "Values passed to <code>Process.Start</code> and read inside the process body. Declared as pins on <code>Process.Entry</code>.",
            new[] { "param.<name>" }),
        new TokenFamily("bus.", "#7FBED1", "Bus message",
            "Bound on <code>Bus.OnMessage</code> when an internal bus message matches the filters.",
            new[] { "bus.type", "bus.payload", "bus.source", "bus.target" }),
    };

    internal static readonly IReadOnlyList<StorageRoot> StorageRoots = new[]
    {
        new StorageRoot("global.", "Shared across every script — the databank. Survives restarts. Read / write with the DB.* nodes.", "global.raid_count"),
        new StorageRoot("user.", "Persistent databank keys. A write persists under the literal key you give it — one shared cell — so include {user.name} in the key for per-viewer data.", "user.{user.name}.points"),
        new StorageRoot("state.", "The state-machine namespace, persisted in the databank. Cross-script and survives restarts; written by the State.* nodes, watched by State.OnChange.", "state.stream_phase"),
    };

    internal static readonly string[] SystemTokens =
    {
        "system.time", "system.hours", "system.minutes", "system.seconds", "system.unix",
        "system.date", "system.day", "system.month", "system.monthname", "system.dayname", "system.year",
        "system.local_time", "system.local_hours", "system.local_minutes", "system.local_seconds",
        "system.local_date", "system.local_day", "system.local_month", "system.local_monthname",
        "system.local_dayname", "system.local_year",
    };

    internal static readonly string[] StreamTokens =
    {
        "stream.uptime", "stream.uptime_seconds", "stream.uptime_minutes", "stream.uptime_hours",
        "stream.uptime_formatted", "stream.started_at",
    };

    /// <summary>
    /// Socket names that denote a Twitch identity — rendered with the diamond
    /// "user" pin and the standard <c>{user.*}</c> field struct, regardless of
    /// the registry colour the template assigned them.
    /// </summary>
    internal static readonly HashSet<string> UserSocketNames =
        new(System.StringComparer.OrdinalIgnoreCase) { "User", "Gifter", "Recipient" };
}
