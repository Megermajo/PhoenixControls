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
///     the headline nodes, ported from the Claude Design prototype
///     (arch-nodes-data.jsx) and keyed by the REAL node title. Any node without
///     an entry falls back to its registry <c>Description</c>.
///   • the primer data — token families, persistent stores and clock catalogues
///     ported from the design's arch-primer.jsx (grounded in the script engine's
///     resolvers). The keyboard reference is generated from the live
///     ArchitectHotkeyCatalog, not authored here.
///
/// Prose strings are HTML fragments (rendered via innerHTML by node-reference.js)
/// — keep them to the small inline vocabulary the design uses: &lt;p&gt;,
/// &lt;b&gt;/&lt;strong&gt;, &lt;em&gt;, &lt;code&gt;.
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
            "Stream-time triggers relayed from Streamer.bot, plus system, schedule, webhook, hotkey and clipboard sources. Every flow starts with one of these — they fire on the main thread, in order received."),
        new CategoryMeta("Flow Control", "#3A4257",
            "Branches, switches, loops and gates. The glue between an event and an action: decide what matters, repeat work, fork the flow. None of these touch the network."),
        new CategoryMeta("Logic", "#46506B",
            "Pure comparisons and boolean math — <code>==</code>, <code>&gt;</code>, <code>and</code>, <code>or</code>, <code>not</code>. Inlined into whatever reads them; no execution edge of their own."),
        new CategoryMeta("Async", "#1F5F6B",
            "Time and waiting — delays, timeouts, parallel forks, and chat/visual awaits. Non-blocking: other flows keep running while one waits."),
        new CategoryMeta("Platforms", "#7A4710",
            "Outbound actions to the outside world — Twitch, OBS, Discord, HTTP, the filesystem and audio. All Twitch traffic is routed through your Streamer.bot bot account; Phoenix never talks to Twitch directly."),
        new CategoryMeta("Visuals", "#3A1F4E",
            "Drive the Visualist overlay compositor — fire triggers, push chat overlays, set text and widget properties. Fire-and-forget over the local IPC bus; non-blocking."),
        new CategoryMeta("AI", "#5A2E5A",
            "LLM and vision nodes — prompt, moderate, generate images, describe images, tool-calling. The provider is configured once in Settings → AI; these nodes don't care which model is behind the curtain."),
        new CategoryMeta("Twitch Data", "#7A5414",
            "Read-only Twitch lookups — user info, stream state, roles, follow age, active viewers. Inlined where they're read; resolved through Streamer.bot."),
        new CategoryMeta("Databank", "#6B5414",
            "Persistent SQLite key/value and table storage. Survives restarts; the home of points, counters, quotes and per-user data. Keyed access — no SQL to write."),
        new CategoryMeta("State", "#3F4860",
            "In-memory session state. Fast, and cleared when Hub restarts — perfect for \"last winner\", cooldown flags and per-stream scratch values."),
        new CategoryMeta("Variables", "#2E4A5A",
            "Read and write named values by scope — Local (this graph), Public (this file) and the databank-backed global store."),
        new CategoryMeta("Text", "#2E5A4A",
            "Build, format, split, join and transform strings. The workhorses behind every chat message and overlay caption."),
        new CategoryMeta("Math", "#4A5A2E",
            "Arithmetic, rounding, clamping, random and percentage-chance. Pure-data: wire the result straight into another socket."),
        new CategoryMeta("Collections", "#5A4A2E",
            "Make, query and reshape lists — get, filter, sort, shuffle, unique. Accepts JSON arrays, comma strings and databank query results."),
        new CategoryMeta("Values", "#3A3F57",
            "Literal value nodes — drop a string, int, float or bool onto the canvas and wire it in. The simplest inputs there are."),
        new CategoryMeta("Convert", "#4A3F57",
            "Coerce a value from one type to another — to int, float, string or bool. Cheap, pure, no flow."),
        new CategoryMeta("System", "#3A4257",
            "Logging and the always-on clock / calendar. <code>System.Log</code> writes to the System Log; the time nodes feed <code>{system.*}</code>-style values."),
        new CategoryMeta("Bus", "#1F4E5A",
            "Send and broadcast messages on Hub's internal IPC bus — talk to Visualist, Architect debug, or your own listeners."),
        new CategoryMeta("Queue", "#2E4A5A",
            "A durable FIFO event queue — push work, pop it later, measure depth. Drives raid trains, shoutout queues and rate-limited dispatch."),
        new CategoryMeta("Process", "#46466E",
            "Long-running background loops — spawn, terminate, and the entry/exit anchors of a process graph. Kick them off from <code>System.Startup</code>."),
        new CategoryMeta("Macros", "#5A3A1F",
            "Reusable subgraphs called like a single node. Build once, reuse everywhere; publish globally to share across files."),
        new CategoryMeta("Giveaway", "#5A2E3A",
            "Run a giveaway from the graph — create, take tickets, draw a weighted winner. The same engine the Hub Giveaway panel drives."),
        new CategoryMeta("Reroute", "#2C251D",
            "A bare wire knot — drag a reroute onto a wire to bend it cleanly around the canvas. Carries its type through untouched."),
    };

    // ── headline node prose (keyed by REAL registry title) ────────────────
    // Ported from arch-nodes-data.jsx and adapted to the real node names /
    // sockets. Every other node renders from its registry Description.
    internal readonly record struct NodeInfo(
        string? Summary = null,
        string? Description = null,
        string? Example = null,
        string? Since = null,
        string? Badge = null);

    internal static readonly IReadOnlyDictionary<string, NodeInfo> Nodes = new Dictionary<string, NodeInfo>
    {
        ["Twitch.ChatMessage"] = new(
            Summary: "Fires when any chatter — including the broadcaster and bots — sends a message in your channel.",
            Description: "<p>The workhorse trigger. Use the <code>IsCommand</code> pin to early-out on plain chatter, then branch on the leading token with <code>Logic.Switch</code>.</p><p>Bot accounts and the broadcaster fire this node too — gate on the bot username (Settings) if you don't want feedback loops.</p>",
            Example: "Listen for <b>!ping</b> &rarr; reply <b>\"@{user} pong\"</b>."),

        ["Twitch.Cheer"] = new(
            Summary: "Fires on a Bits cheer. The whole message is given verbatim — cheermote codes are not stripped.",
            Description: "<p>Use <code>Bits</code> to size a Visualist particle burst to the donation. <code>Message</code> is the raw chat line, cheermotes included.</p>",
            Example: "&ge; 500 bits &rarr; a particle burst centered on screen."),

        ["Twitch.Subscription"] = new(
            Summary: "Fires on a new sub. Resubs and gift subs have their own dedicated event nodes.",
            Description: "<p><code>Tier</code> is the raw Twitch tier code (<code>1000</code>, <code>2000</code>, <code>3000</code>, <code>Prime</code>). Pair with <code>Twitch.Resub</code> and <code>Twitch.GiftSub</code> to cover every sub path.</p>",
            Example: "New tier-3 sub &rarr; announce in purple."),

        ["Twitch.Raid"] = new(
            Summary: "Fires when another channel raids yours.",
            Description: "<p>Fires once on raid arrival. Use it to trigger a Visualist raid overlay and (optionally) a shoutout via <code>Twitch.Shoutout</code>.</p>",
            Example: "&ge; 25 viewers &rarr; play a raid-fanfare clip and shout the raider out."),

        ["Twitch.Follow"] = new(
            Summary: "Fires on a new follow. Re-follows do not re-fire.",
            Description: "<p>Twitch debounces follows aggressively — this fires once per identity. For \"welcome back\" loops, use <code>Twitch.ChatMessage</code> with a databank lookup.</p>",
            Example: "Any follow &rarr; a tiny Visualist chime overlay."),

        ["Twitch.PointRedeem"] = new(
            Summary: "Fires when a viewer redeems a channel-points reward.",
            Description: "<p><code>Reward</code> is the reward title from your Twitch dashboard; <code>Input</code> is the optional viewer text (empty if the reward doesn't request any). Branch on the title with <code>Logic.Switch</code> — Phoenix doesn't bind reward IDs because they regenerate on rename.</p>",
            Example: "<b>\"Hydrate!\"</b> redeem &rarr; Visualist trigger + a chat thank-you."),

        ["System.Startup"] = new(
            Summary: "Fires once when Hub finishes starting up.",
            Description: "<p>The place to kick off long-running <b>Processes</b> — chat collectors, rotating-tip loops, hourly digests — and to reset per-session state.</p>",
            Example: "On startup &rarr; spawn the rotating-tips process and clear the session counters."),

        ["Twitch.SendChat"] = new(
            Summary: "Posts a message to chat through your configured Streamer.bot bot account.",
            Description: "<p>The most-used action node. Text between <code>{</code> and <code>}</code> is resolved against the current flow context — anything an upstream node bound is fair game.</p><p>Rate-limiting is owned by Streamer.bot, not Phoenix; hammering this node just queues messages there.</p>",
            Example: "Reply to <b>!ping</b> with <code>\"@{user} pong\"</code>."),

        ["Twitch.Whisper"] = new(
            Summary: "Sends a private whisper to a single user.",
            Description: "<p>Subject to Twitch's whisper restrictions — your bot account must be verified or whispers silently drop.</p>",
            Example: "Whisper a new sub a thank-you."),

        ["Twitch.Timeout"] = new(
            Summary: "Times a user out for a number of seconds.",
            Description: "<p>Requires your bot account to hold moderator privileges in the channel. The optional reason shows in the mod log.</p>",
            Example: "Auto-timeout 30s on a banned-words match."),

        ["Twitch.Ban"] = new(
            Summary: "Permanently bans a user. There is no undo from inside Phoenix.",
            Description: "<p>Use sparingly. Pair with <code>Twitch.Unban</code> for reversible mod flows you've tested.</p>",
            Example: "Spam-bot detector &rarr; ban with reason <b>\"link spam\"</b>."),

        ["Twitch.CreateClip"] = new(
            Summary: "Creates a clip of the last ~30 seconds and emits the URL.",
            Description: "<p>Twitch needs a beat to finish the clip — the flow continues once the URL is available (usually 2–4 seconds). Wire it into a Discord post or a chat shoutout.</p>",
            Example: "<b>!clip</b> in chat &rarr; CreateClip &rarr; SendChat the resulting URL."),

        ["Twitch.Announcement"] = new(
            Summary: "Posts a Twitch /announce — the highlighted-colour chat banner.",
            Description: "<p><code>Color</code> is one of <code>primary</code>, <code>blue</code>, <code>green</code>, <code>orange</code>, <code>purple</code>. Anything else falls back to <code>primary</code>.</p>",
            Example: "Big raid (&ge; 50 viewers) &rarr; /announce in purple."),

        ["Logic.Branch"] = new(
            Summary: "Two-way branch on a boolean condition.",
            Description: "<p>Exactly one of <code>True</code> / <code>False</code> fires per invocation. If <code>Condition</code> is left unwired it's treated as <code>false</code>.</p>",
            Example: "If the chatter is a mod &rarr; run the mod-only path; else nothing."),

        ["Logic.Switch"] = new(
            Summary: "Multi-way branch on a string value, with a default case.",
            Description: "<p>Matching is case-insensitive by default. The clean way to dispatch on a command token — wire a chat command into <code>Value</code> and give each command its own case.</p>",
            Example: "Switch on the first chat token: <b>!ping</b>, <b>!quote</b>, <b>!so</b>, <i>default</i>."),

        ["Flow.Delay"] = new(
            Summary: "Pauses the flow for a number of seconds. Non-blocking — other flows keep running.",
            Description: "<p>Each delay holds its own timer; chain them, fan them out, or stack hundreds without thread contention. Cancelled if the surrounding graph is unloaded.</p>",
            Example: "Greet a new sub &rarr; wait 4s &rarr; ask them to introduce themselves."),

        ["Flow.ForEach"] = new(
            Summary: "Iterates a list — JSON array, comma string, or a databank query result.",
            Description: "<p>The body runs to completion for each item before the next iteration starts. For arbitrary delimiters, pre-split with <code>Text.Split</code>.</p>",
            Example: "Iterate a comma-list of mod names &rarr; whisper each one."),

        ["Visual.Trigger"] = new(
            Summary: "Fires a named trigger on a Visualist layer. The most-used overlay node.",
            Description: "<p>The trigger name is matched against the keyframes placed in the Visualist editor on that layer. If nothing matches, Visualist logs a soft warning and stays idle.</p>",
            Example: "<b>!ping</b> in chat &rarr; fire the <code>greet</code> trigger on the <b>main</b> layer."),

        ["AI.Prompt"] = new(
            Summary: "Runs a system + user prompt through the configured chat model and returns the text.",
            Description: "<p>Synchronous-feeling, async under the hood — the flow waits for completion. If the provider errors or rate-limits, the flow halts here and emits an <code>Error</code>. Wrap in a try-style flow for resilience.</p>",
            Example: "Channel-point \"ask the bot\" &rarr; AI.Prompt &rarr; SendChat the result."),

        ["DB.SetVariable"] = new(
            Summary: "Writes a value to the persistent databank under a key. Survives restarts.",
            Description: "<p>The databank is plain SQLite under <code>%AppData%/PhoenixControls</code>. Keys are yours to name; pair with <code>DB.GetVariable</code> and <code>DB.Increment</code> for counters.</p>",
            Example: "On follow &rarr; increment <code>global.follow_count</code> and announce the new total."),

        ["DB.Increment"] = new(
            Summary: "Atomically adds an amount to a numeric cell and returns the new value.",
            Description: "<p>Race-safe — concurrent scripts incrementing the same cell never lose a write. The canonical points / counter primitive.</p>",
            Example: "On cheer &rarr; add <code>Bits</code> to the chatter's points row, announce the total."),

        ["HTTP.Get"] = new(
            Summary: "Performs an HTTP GET and returns the status code and body.",
            Description: "<p>For structured replies, follow with <code>HTTP.ParseJson</code>. Wrap in a try-style flow so a 5xx doesn't halt the whole script.</p>",
            Example: "<b>!weather</b> &rarr; GET an API &rarr; ParseJson &rarr; SendChat the temperature."),
    };

    // ════════════════════════════════════════════════════════════════════
    //  Primer data — ported from arch-primer.jsx (grounded in the engine's
    //  {token} resolvers). The keyboard reference is built live from
    //  ArchitectHotkeyCatalog instead.
    // ════════════════════════════════════════════════════════════════════
    internal readonly record struct TokenFamily(string Root, string Dot, string Label, string Note, string[] Tokens);
    internal readonly record struct StorageRoot(string Root, string Desc, string Example);

    internal static readonly IReadOnlyList<TokenFamily> TokenFamilies = new[]
    {
        new TokenFamily("user.", "#C893BC", "Identity & event payload",
            "Bound by the trigger at the top of the flow — the field set varies per event (chat, sub, raid, cheer, redeem).",
            new[] { "user.name", "user.message", "user.command", "user.args", "user.is_mod", "user.is_sub", "user.is_vip", "user.is_broadcaster", "user.color_hex", "user.sub_months", "user.tier", "user.bits", "user.viewers", "user.gifter", "user.recipient", "user.reward", "user.input" }),
        new TokenFamily("event.", "#7FBED1", "Event metadata",
            "The shape of the firing event — webhook body, schedule tick, state change. <code>event.ret.*</code> / <code>event.arg.*</code> come from Event nodes.",
            new[] { "event.iscommand", "event.payload", "event.body", "event.method", "event.path", "event.timestamp", "event.count", "event.name", "event.oldvalue", "event.newvalue" }),
        new TokenFamily("result.", "#9CC97A", "Command outputs",
            "Written by the node that just ran — HTTP, AI, file, and Discord calls drop their return values here.",
            new[] { "result.http_status", "result.http_body", "result.api_response", "result.json_value", "result.ai_response", "result.ai_error", "result.ai_tool_calls", "result.ai_image_url", "result.file_content", "result.discord_message_id", "result.sb_dispatched" }),
        new TokenFamily("loop.", "#E5A24E", "Iterator",
            "Inside <code>Flow.ForLoop</code> / <code>Flow.ForEach</code> bodies. Nested loops also expose a per-id <code>loop.index_xxxxxx</code> form.",
            new[] { "loop.index", "loop.item" }),
        new TokenFamily("role.", "#C893BC", "Role check",
            "After a <code>Twitch.CheckRole</code> node resolves the chatter's permissions.",
            new[] { "role.is_mod", "role.is_sub", "role.is_vip", "role.is_broadcaster" }),
        new TokenFamily("follow.", "#7FBED1", "Follow age",
            "After <code>Twitch.GetFollowAge</code>. <code>bus.*</code> (type / payload / source / target) binds the same way on <code>Bus.OnMessage</code>.",
            new[] { "follow.days", "follow.formatted" }),
    };

    internal static readonly IReadOnlyList<StorageRoot> StorageRoots = new[]
    {
        new StorageRoot("global.", "Shared across every .phx — the databank. Survives restarts.", "global.raid_count"),
        new StorageRoot("user.", "Per-Twitch-user and persistent. Custom keys live in the databank, keyed to the chatter on top of their live identity fields.", "user.points"),
        new StorageRoot("state.", "In-memory session state. Fast, and cleared when Hub restarts.", "state.last_winner"),
    };

    internal static readonly string[] SystemTokens =
    {
        "system.time", "system.hours", "system.minutes", "system.seconds", "system.unix",
        "system.date", "system.day", "system.month", "system.monthname", "system.dayname", "system.year",
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
        new(System.StringComparer.OrdinalIgnoreCase) { "User", "Gifter", "Recipient", "Raider", "To" };
}
