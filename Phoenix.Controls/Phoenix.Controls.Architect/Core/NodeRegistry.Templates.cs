using System;
using System.Collections.Generic;
using System.Drawing;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Architect.Core
{
    // Partial split: this file owns RegisterDefaults (the ~132 default node
    // templates) and the small helpers that drive the registration call sites
    // (AddTemplate, AddBinaryOp, AddUnaryOp, BubbleKeyFor, SetCompactSymbol,
    // SetKeywords, SetSocketDescriptions). The other partial in NodeRegistry.cs
    // holds shared state (the templates dict, color tokens, type conversion,
    // wildcard cascade, public lookups, CreateNode + variants). Split keeps
    // editing surfaces small without changing any byte of emitted graph data.
    //
    // Per-band carves: each registration band is being moved
    // into its own NodeRegistry.Templates.<Band>.cs partial with a sibling
    // RegisterXTemplates() helper. RegisterDefaults() then becomes a
    // declarative orchestrator — same pattern ScriptManager uses for its
    // RegisterXCommands() helpers (per the project conventions).
    public static partial class NodeRegistry
    {
        private static void RegisterDefaults()
        {
            RegisterEventsTemplates();        // NodeRegistry.Templates.Events.cs
            RegisterFlowControlTemplates();   // NodeRegistry.Templates.FlowControl.cs
            RegisterLogicAndAsyncTemplates(); // NodeRegistry.Templates.LogicAsync.cs
            RegisterConnectorTemplates();     // NodeRegistry.Templates.Connectors.cs
            RegisterDatabankTemplates();      // NodeRegistry.Templates.Databank.cs
            RegisterDataUtilityTemplates();   // NodeRegistry.Templates.DataUtilities.cs
            RegisterRemainingTemplates();     // NodeRegistry.Templates.RemainingBands.cs
            RegisterGiveawayTemplates();      // NodeRegistry.Templates.Giveaway.cs
            RegisterTimerTemplates();         // NodeRegistry.Templates.Timer.cs
            RegisterLoyaltyTemplates();       // NodeRegistry.Templates.Loyalty.cs
            RegisterCountersTemplates();      // NodeRegistry.Templates.Counters.cs
            RegisterAutomodTemplates();       // NodeRegistry.Templates.Automod.cs
            RegisterQuotesTemplates();        // NodeRegistry.Templates.Quotes.cs
            RegisterCustomCommandsTemplates();// NodeRegistry.Templates.CustomCommands.cs
            RegisterUsersTemplates();         // NodeRegistry.Templates.Users.cs
            RegisterSongRequestTemplates();   // NodeRegistry.Templates.SongRequest.cs
            RegisterPollsTemplates();         // NodeRegistry.Templates.Polls.cs
            RegisterRanksTemplates();         // NodeRegistry.Templates.Ranks.cs
            RegisterSoundboardTemplates();    // NodeRegistry.Templates.Soundboard.cs

            // Logic.Gate is fully retired — template AND exporter handler are
            // both gone (the template was identical to Logic.Branch; the
            // GateHandler removal is recorded in ExporterRegistry.Handlers2.cs).
            // Zero .phxg in the repo contains a Logic.Gate node. Should one ever
            // arrive from outside: the final-era shape (Category "Flow Control",
            // shipped 2026-04-21, removed 04-24) HARD-FAILS export at
            // ScriptExporter's no-handler throw ("Flow Control" is not in
            // _pureDataCategories, and AllowPlaceholderFallback defaults false —
            // pinned by ExporterRegistryTests); a node authored in the earlier
            // Category="Logic" window would instead take the pure-data inline
            // arm and be SILENTLY dropped from the flow. Restore template +
            // handler from git history if a use case ever resurfaces.

            // (SYSTEM / MATH & RNG / TEXT & STRINGS / COLLECTIONS / VALUES / TYPE CONVERSION
            //  bands carved to NodeRegistry.Templates.DataUtilities.cs.)


            // (TWITCH DATA / VISUALS / PLATFORM ACTIONS / Twitch moderation /
            //  OBS proxy / Discord / HTTP / File / Audio / Twitch Polls /
            //  Twitch Reward Mgmt / STATE MACHINE / AI / MACROS / VARIABLES
            //  bands carved to NodeRegistry.Templates.RemainingBands.cs.
            //  The orchestrator now adds them in source order
            //  via the call list above; the parent file keeps only the
            //  post-template helper calls below — SetSocketDescriptions /
            //  SetCompactSymbol / SetKeywords / fuzzy-search aliases.)

            // ──────────────────────────────────────────────────────────────
            // Socket-level descriptions for non-obvious cases. The contract
            // for backfilling: only when the socket name alone doesn't convey
            // the meaning (per-iteration semantics, wildcard mirroring,
            // multi-output result branches, etc.). Obvious sockets (Flow,
            // plain "Username", "Message", "Url") stay empty so the doc form
            // doesn't drown in tautologies.
            // ──────────────────────────────────────────────────────────────
            SetSocketDescriptions("Flow.ForEach", new()
            {
                { "List",      "List to walk through. Wire from Array.Make, Array.Literal, DB.GetColumn, etc." },
                { "Loop Body", "Fires once per item in List with the current value on Item. Chain your per-item work here." },
                { "Item",      "Current item for this iteration. Only valid inside the Loop Body branch — empty before the loop starts and after Done." },
            });
            SetSocketDescriptions("Flow.ForLoop", new()
            {
                { "Index", "Current loop counter, refreshed each iteration. Also available downstream as {loop.index}." },
            });
            SetSocketDescriptions("Logic.If", new()
            {
                { "A", "Left side of the comparison. Wildcard — once you wire either A or B, the other pin matches its type." },
                { "B", "Right side of the comparison. Wildcard — once you wire either A or B, the other pin matches its type." },
            });
            SetSocketDescriptions("Logic.Equals", new()
            {
                { "A", "Left side of the comparison. Wildcard — wire either A or B and the other pin matches its type." },
                { "B", "Right side of the comparison. Wildcard — wire either A or B and the other pin matches its type." },
            });
            SetSocketDescriptions("Logic.NotEquals", new()
            {
                { "A", "Left side of the comparison. Wildcard — wire either A or B and the other pin matches its type." },
                { "B", "Right side of the comparison. Wildcard — wire either A or B and the other pin matches its type." },
            });
            SetSocketDescriptions("Logic.Select", new()
            {
                { "A",     "Returned when Cond is true. Wildcard — A, B and Value all share the same type." },
                { "B",     "Returned when Cond is false. Wildcard — A, B and Value all share the same type." },
                { "Value", "The chosen value (A or B). Wildcard — matches the type of A and B." },
            });
            SetSocketDescriptions("Flow.Reroute", new()
            {
                { "In",  "Pass-through input. Wildcard — once either side is wired, both pins take that type. Visual-only routing helper, no effect on data." },
                { "Out", "Pass-through output. Wildcard — once either side is wired, both pins take that type." },
            });
            SetSocketDescriptions("Chat.Message", new()
            {
                { "User",      "Packed 7-element array [name, is_mod, is_sub, is_vip, is_broadcaster, color_hex, sub_months]. Feed through Array.Get or Array.Unpack to read individual fields." },
                { "Command",   "The first word of the message, lowercased, with a leading ! stripped — equals the matched alias inside a Commands-filtered flow. Never empty for a non-empty message." },
                { "Args",      "The text after the command, as a single space-separated string. Split on space with Text.Split before Flow.ForEach / Array.Get (Array nodes expect comma-separated lists)." },
                { "IsCommand", "True when the message starts with ! (the Commands filter separately gates whether the flow fires at all)." },
                { "Platform",  "Which platform sent the line: twitch, youtube or kick. Branch on it with Logic.Switch to answer per platform." },
            });
            SetSocketDescriptions("Chat.Send", new()
            {
                { "Message",   "The chat line to send. Platform limits apply per target (Twitch/Kick 500 chars, YouTube 200) — an over-limit message is dropped for that platform with a log entry." },
                { "Platforms", "Optional runtime OVERRIDE of the checkmarks: a single platform name or a comma list — twitch, youtube, kick (case doesn't matter). Leave empty to use the checked platforms below." },
                { "Done",      "Fires after the send was handed to every targeted platform." },
            });
            SetSocketDescriptions("Chat.MessageCount", new()
            {
                { "Count", "Total inbound chat lines (all platforms, bot messages excluded) the Hub has seen since it started. Monotonic — it never resets mid-run, so store it and compare deltas to gate on chat activity." },
            });
            SetSocketDescriptions("Public.Get", new()
            {
                { "Value", "Reads {public.<KeyName>}. Lives for the script run; visible across Async.Parallel branches and called macros — see Public.Set." },
            });
            SetSocketDescriptions("Public.Set", new()
            {
                { "Value", "Stored as {public.<KeyName>}. Writes inside an Async.Parallel branch DO merge back to the parent script when the branch ends. Vanishes when the script ends (not saved to the database)." },
            });
            SetSocketDescriptions("Var.Get", new()
            {
                { "Value", "Reads {var.<VariableName>}. Lives for the script run, but branch-local — values written inside an Async.Parallel branch do NOT leak back. Use Public.Get when a value needs to cross branches." },
            });
            SetSocketDescriptions("Var.Set", new()
            {
                // One name-keyed entry covers BOTH the Value input and the Value
                // output (socket descriptions are keyed by name), so the text is
                // written direction-neutral.
                { "Value", "Stored as {var.<VariableName>} and relayed on the Value output, so downstream nodes can use the freshly-set value immediately. Writes inside an Async.Parallel branch stay inside that branch — they do NOT merge back to the parent. Use Public.Set when a value needs to cross branches." },
            });
            SetSocketDescriptions("Chat.WaitForNext", new()
            {
                { "User",      "Optional username filter — exact match, case-insensitive. Leave blank to match any user." },
                { "Command",   "Optional command filter — matches messages starting with !{command}. The leading ! is optional in the filter. Leave blank to match any message." },
                { "TimeoutMS", "Maximum time to wait, in milliseconds. Defaults to 30000 (30 seconds). TimedOut fires once this runs out." },
                { "Got",       "Fires when a matching chat line arrives. Username and Message are filled in for the matched line only." },
                { "TimedOut",  "Fires once the timeout expires with no matching line. Username and Message are empty on this branch." },
            });
            SetSocketDescriptions("Chat.PeekRecent", new()
            {
                { "N",         "How many of the most-recent messages to return. 0 or less returns the entire buffer (up to 50 lines)." },
                { "Usernames", "List of usernames in oldest-to-newest order. Same length as Messages so they line up by index." },
                { "Messages",  "List of message bodies in oldest-to-newest order. Same length as Usernames so they line up by index." },
            });
            SetSocketDescriptions("Async.WaitForEvent", new()
            {
                { "Received", "Fires when a bus message with the matching EventType arrives." },
                { "Payload",  "Body of the matched bus message. Only valid on the Received branch — refreshed on every wait." },
                { "Timeout",  "Fires once TimeoutMS milliseconds pass with no matching message." },
            });
            SetSocketDescriptions("Twitch.LastActive", new()
            {
                { "MinutesAgo", "How many minutes ago the user last chatted. -1 means they haven't chatted this Hub session." },
                { "Inactive",   "Fires when the user has been quiet for longer than the Minutes threshold." },
                { "Active",     "Fires when the user has chatted within the Minutes threshold." },
            });

            // Compact display glyphs for the math/logic operators (UE
            // Blueprints "compact node" idiom). Toggled per-node from the canvas
            // right-click menu; the symbol is what the node draws in compact mode.
            SetCompactSymbol("Math.Add",        "+");
            SetCompactSymbol("Math.Subtract",   "−");
            SetCompactSymbol("Math.Multiply",   "×");
            SetCompactSymbol("Math.Divide",     "÷");
            SetCompactSymbol("Math.Modulo",     "%");
            SetCompactSymbol("Math.Min",        "min");
            SetCompactSymbol("Math.Max",        "max");
            SetCompactSymbol("Math.Abs",        "abs");
            SetCompactSymbol("Logic.Equals",    "==");
            SetCompactSymbol("Logic.NotEquals", "≠");
            SetCompactSymbol("Logic.GreaterThan", ">");
            SetCompactSymbol("Logic.LessThan",  "<");
            SetCompactSymbol("Logic.And",       "AND");
            SetCompactSymbol("Logic.Or",        "OR");
            SetCompactSymbol("Logic.Not",       "!");
            SetCompactSymbol("Logic.If",        "?");

            // Fuzzy-search aliases. Targets the gap between what the user types
            // ("if", "wait", "loop") and the canonical node title ("Logic.If",
            // "Async.Delay", "Flow.ForEach"). Tokens are matched as additional terms
            // by the spawn-search prefix matcher in Canvas.
            SetKeywords("Logic.If",            "if", "branch", "condition");
            SetKeywords("Logic.Branch",        "if", "branch", "condition");
            SetKeywords("Logic.Switch",        "switch", "case", "match");
            SetKeywords("Logic.Sequence",      "seq", "sequence", "then");
            SetKeywords("Logic.Equals",        "eq", "equal", "compare");
            SetKeywords("Logic.NotEquals",     "neq", "different", "compare");
            SetKeywords("Logic.GreaterThan",   "gt", "compare");
            SetKeywords("Logic.LessThan",      "lt", "compare");
            SetKeywords("Logic.And",           "and");
            SetKeywords("Logic.Or",            "or");
            SetKeywords("Logic.Not",           "not", "invert");
            SetKeywords("Logic.Select",        "ternary", "pick");
            SetKeywords("Flow.ForEach",        "loop", "iterate", "each");
            SetKeywords("Flow.ForLoop",        "loop", "for", "iterate", "count");
            SetKeywords("Flow.FlipFlop",       "alternate", "toggle");
            SetKeywords("Flow.DoOnce",         "once", "first");
            SetKeywords("Flow.DoN",            "limit", "count");
            SetKeywords("Flow.Cooldown",       "cooldown", "ratelimit", "throttle");
            SetKeywords("Flow.Reroute",        "reroute", "knot", "pin");
            SetKeywords("Flow.Select",         "select", "mux", "pick");
            SetKeywords("Async.Delay",         "wait", "delay", "sleep", "pause");
            SetKeywords("Async.WaitForEvent",  "wait", "await", "listen");
            SetKeywords("Async.Parallel",      "parallel", "fork", "concurrent");
            SetKeywords("Async.Join",          "join", "await", "barrier");
            SetKeywords("Math.Random",         "rng", "random", "dice", "roll");
            SetKeywords("Math.Chance",         "rng", "probability", "dice");
            SetKeywords("Math.Add",            "plus", "sum", "add");
            SetKeywords("Math.Subtract",       "minus", "diff");
            SetKeywords("Math.Multiply",       "times", "mul");
            SetKeywords("Math.Divide",         "div", "quotient");
            SetKeywords("Math.Modulo",         "mod", "remainder");
            SetKeywords("Math.Clamp",          "limit", "constrain");
            SetKeywords("Math.Min",            "minimum", "smallest");
            SetKeywords("Math.Max",            "maximum", "largest");
            SetKeywords("Math.Abs",            "absolute", "positive");
            SetKeywords("Math.Floor",          "round", "down");
            SetKeywords("Math.Ceil",           "round", "up", "ceiling");
            SetKeywords("Chat.Message",        "chat", "message", "command", "twitch", "youtube", "kick");
            SetKeywords("Chat.Send",           "say", "respond", "reply", "send", "twitch", "youtube", "kick");
            SetKeywords("Chat.MessageCount",   "count", "messages", "activity", "chat", "traffic", "gate");
            SetKeywords("Twitch.SendChat",     "say", "respond", "reply");
            SetKeywords("Twitch.LastActive",   "lastseen", "active", "presence");
            SetKeywords("Twitch.GetViewers",   "viewers", "audience", "list");
            SetKeywords("Twitch.IsOnline",     "live", "online", "offline", "stream", "broadcasting");
            SetKeywords("Var.Get",             "read", "load", "var");
            SetKeywords("Var.Set",             "write", "store", "var", "assign");
            SetKeywords("Public.Get",          "read", "shared");
            SetKeywords("Public.Set",          "write", "shared");
            SetKeywords("System.GetTime",      "time", "clock", "now");
            SetKeywords("System.GetDate",      "date", "today", "calendar");
            SetKeywords("System.Log",          "log", "print", "debug");
            SetKeywords("Time.SecondsSinceLastFire", "cooldown", "rate", "uptime", "since");
            SetKeywords("Event.Trigger",       "fire", "raise", "dispatch");
            SetKeywords("Event.Executor",      "listen", "handler", "on");
            SetKeywords("Event.Return",        "return", "result");
            SetKeywords("Macro.Call",          "invoke", "call");
            SetKeywords("HTTP.Get",            "fetch", "url", "request");
            SetKeywords("HTTP.Post",           "send", "url", "request");
            SetKeywords("HTTP.WebhookListener","webhook", "callback");
            SetKeywords("Schedule.Cron",       "cron", "schedule", "recurring");
            SetKeywords("Schedule.RunAt",      "at", "schedule", "specific");
            SetKeywords("Schedule.Recurring",  "every", "schedule", "interval");

            // ──────────────────────────────────────────────────────────────
            // IconGlyph — per-template 16x16 Segoe Fluent Icons /
            // Segoe MDL2 Assets codepoints rendered left of the node header
            // title. Half-built before this sweep: NodeViewModel.IconGlyph +
            // NodeView.xaml's FontIcon read the slot, but no template ever
            // populated it. Codepoints chosen from the intersection of both
            // fonts so the FontFamily fallback chain
            // ("Segoe Fluent Icons,Segoe MDL2 Assets") always renders. Only
            // the high-traffic platform / databank families opt in — the rest
            // stay icon-less so the canvas chrome doesn't drown in glyphs.
            // ──────────────────────────────────────────────────────────────
            // Databank —  is the "Storage" / database glyph (matches the
            // "DB" mental model on the Architect palette).
            const string DatabankGlyph = "";
            SetIconGlyph("DB.GetVariable",   DatabankGlyph);
            SetIconGlyph("DB.SetVariable",   DatabankGlyph);
            SetIconGlyph("DB.Increment",     DatabankGlyph);
            SetIconGlyph("DB.CheckExists",   DatabankGlyph);
            SetIconGlyph("DB.DeleteVar",     DatabankGlyph);
            SetIconGlyph("DB.RowCount",      DatabankGlyph);
            SetIconGlyph("DB.FindRow",       DatabankGlyph);
            SetIconGlyph("DB.GetCell",       DatabankGlyph);
            SetIconGlyph("DB.SetCell",       DatabankGlyph);
            SetIconGlyph("DB.InsertRow",     DatabankGlyph);
            SetIconGlyph("DB.DeleteRow",     DatabankGlyph);
            SetIconGlyph("DB.ClearTable",    DatabankGlyph);
            SetIconGlyph("DB.GetColumn",     DatabankGlyph);
            SetIconGlyph("DB.Top",           DatabankGlyph);
            SetIconGlyph("DB.FetchRow",      DatabankGlyph);

            // HTTP —  is the "Globe" / world glyph (universal "network" cue).
            const string HttpGlyph = "";
            SetIconGlyph("HTTP.Get",         HttpGlyph);
            SetIconGlyph("HTTP.Post",        HttpGlyph);
            SetIconGlyph("HTTP.Put",         HttpGlyph);
            SetIconGlyph("HTTP.Patch",       HttpGlyph);
            SetIconGlyph("HTTP.Delete",      HttpGlyph);
            SetIconGlyph("HTTP.ParseJson",   HttpGlyph);
            SetIconGlyph("API.Call",         HttpGlyph);

            // Twitch —  is the "Microphone" / broadcasting glyph (matches
            // the streamer-tools framing; Twitch has no dedicated brand glyph in
            // either system font).
            const string TwitchGlyph = "";
            SetIconGlyph("Twitch.SendChat",       TwitchGlyph);
            SetIconGlyph("Twitch.Timeout",        TwitchGlyph);
            SetIconGlyph("Twitch.Ban",            TwitchGlyph);
            SetIconGlyph("Twitch.CreateClip",     TwitchGlyph);
            SetIconGlyph("Twitch.Shoutout",       TwitchGlyph);
            SetIconGlyph("Twitch.Announcement",   TwitchGlyph);
            SetIconGlyph("Twitch.Unban",          TwitchGlyph);
            SetIconGlyph("Twitch.Mod",            TwitchGlyph);
            SetIconGlyph("Twitch.Unmod",          TwitchGlyph);
            SetIconGlyph("Twitch.Vip",            TwitchGlyph);
            SetIconGlyph("Twitch.Unvip",          TwitchGlyph);
            SetIconGlyph("Twitch.DeleteMessage",  TwitchGlyph);
            SetIconGlyph("Twitch.SlowMode",       TwitchGlyph);
            SetIconGlyph("Twitch.FollowerMode",   TwitchGlyph);
            SetIconGlyph("Twitch.SubOnlyMode",    TwitchGlyph);
            SetIconGlyph("Twitch.Marker",         TwitchGlyph);
            SetIconGlyph("Twitch.Whisper",        TwitchGlyph);
            SetIconGlyph("Twitch.UpdateChannel",  TwitchGlyph);
            SetIconGlyph("Twitch.CreatePoll",         TwitchGlyph);
            SetIconGlyph("Twitch.EndPoll",            TwitchGlyph);
            SetIconGlyph("Twitch.CreatePrediction",   TwitchGlyph);
            SetIconGlyph("Twitch.ResolvePrediction",  TwitchGlyph);
            SetIconGlyph("Twitch.UpdateRewardCost",   TwitchGlyph);
            SetIconGlyph("Twitch.SetRewardEnabled",   TwitchGlyph);
            SetIconGlyph("Twitch.FulfillRedemption",  TwitchGlyph);
            SetIconGlyph("Twitch.RejectRedemption",   TwitchGlyph);
            // Twitch Data read-side
            SetIconGlyph("Twitch.GetUser",       TwitchGlyph);
            SetIconGlyph("Twitch.GetStream",     TwitchGlyph);
            SetIconGlyph("Twitch.CheckRole",     TwitchGlyph);
            SetIconGlyph("Twitch.IsOnline",      TwitchGlyph);
            SetIconGlyph("Twitch.GetFollowAge",  TwitchGlyph);
            SetIconGlyph("Twitch.LastActive",    TwitchGlyph);
            SetIconGlyph("Twitch.GetViewers",    TwitchGlyph);

            // Discord —  is the "People" / community glyph (closest neutral
            // analogue in both Fluent + MDL2; neither ships a Discord-brand glyph).
            const string DiscordGlyph = "";
            SetIconGlyph("Discord.Webhook",      DiscordGlyph);
            SetIconGlyph("Discord.SendMessage",  DiscordGlyph);
            SetIconGlyph("Discord.SendEmbed",    DiscordGlyph);
            SetIconGlyph("Discord.AddRole",      DiscordGlyph);
            SetIconGlyph("Discord.RemoveRole",   DiscordGlyph);
            SetIconGlyph("Discord.React",        DiscordGlyph);
            SetIconGlyph("Discord.GetUser",      DiscordGlyph);

            // AI —  is the "Light bulb" glyph (universal "AI / inference" cue
            // in both system fonts).
            const string AiGlyph = "";
            SetIconGlyph("AI.Prompt",         AiGlyph);
            SetIconGlyph("AI.Moderate",       AiGlyph);
            SetIconGlyph("AI.GenerateImage",  AiGlyph);
            SetIconGlyph("AI.VisionDescribe", AiGlyph);
            SetIconGlyph("AI.WithTools",      AiGlyph);
            SetIconGlyph("AI.StreamText",     AiGlyph);

            // Audio —  is the "Volume3" / speaker glyph.
            const string AudioGlyph = "";
            SetIconGlyph("Audio.Play",       AudioGlyph);
            SetIconGlyph("Audio.PlayTts",    AudioGlyph);
            SetIconGlyph("Audio.SetVolume",  AudioGlyph);

            // File —  is the "OpenFile" / document-with-arrow glyph.
            const string FileGlyph = "";
            SetIconGlyph("File.ReadText",    FileGlyph);
            SetIconGlyph("File.WriteText",   FileGlyph);
            SetIconGlyph("File.ReadJSON",    FileGlyph);
            SetIconGlyph("File.WriteJSON",   FileGlyph);

            // Visual.* —  is the "Home" / overlay-host glyph (closest match
            // in both fonts to the "HUD overlay" surface this node fans out to).
            const string VisualGlyph = "";
            SetIconGlyph("Visual.Trigger",   VisualGlyph);

            // ──────────────────────────────────────────────────────────────
            // RequiredInputs — inputs that the node won't function
            // without. Canvas paints a yellow halo on each named input that has
            // no incoming link, so authoring slips surface before the script
            // runs. Half-built before this sweep: SocketViewModel.IsRequired
            // read the slot, but no template ever populated it.
            //
            // Scope kept conservative: only inputs that are clearly load-bearing
            // (Url on HTTP.*, Message on Twitch.SendChat, TableName on every
            // DB.*, Path on every File.*). Optional / sensible-default inputs
            // (Headers, Body for POST when ContentType implies empty, Volume
            // for Audio when the attribute default seeds 1.0, etc.) stay
            // un-halo'd so a vanilla drop doesn't paint a wall of yellow.
            // ──────────────────────────────────────────────────────────────
            // HTTP — Url is non-negotiable on every method.
            SetRequiredInputs("HTTP.Get",    "Url");
            SetRequiredInputs("HTTP.Post",   "Url");
            SetRequiredInputs("HTTP.Put",    "Url");
            SetRequiredInputs("HTTP.Patch",  "Url");
            SetRequiredInputs("HTTP.Delete", "Url");

            // Chat — Send without Message is a no-op (same rule as the legacy
            // per-platform senders it replaced).
            SetRequiredInputs("Chat.Send", "Message");

            // Twitch — SendChat without Message is a no-op.
            SetRequiredInputs("Twitch.SendChat", "Message");

            // DB — TableName is the singular invariant across the CRUD surface.
            // GetVariable / SetVariable / Increment / CheckExists / DeleteVar
            // hit the shared `Vars` table by Key; the others target a User_*
            // table the script names via TableName.
            SetRequiredInputs("DB.RowCount",   "TableName");
            SetRequiredInputs("DB.FindRow",    "TableName");
            SetRequiredInputs("DB.GetCell",    "TableName");
            SetRequiredInputs("DB.SetCell",    "TableName");
            SetRequiredInputs("DB.InsertRow",  "TableName");
            SetRequiredInputs("DB.DeleteRow",  "TableName");
            SetRequiredInputs("DB.ClearTable", "TableName");
            SetRequiredInputs("DB.GetColumn",  "TableName");
            SetRequiredInputs("DB.Top",        "TableName");
            SetRequiredInputs("DB.FetchRow",   "TableName");

            // File — Path is mandatory for every Read/Write.
            SetRequiredInputs("File.ReadText",  "Path");
            SetRequiredInputs("File.WriteText", "Path");
            SetRequiredInputs("File.ReadJSON",  "Path");
            SetRequiredInputs("File.WriteJSON", "Path");

            // Extend the required-input coverage to the remaining
            // "won't work without an identifier" surface. Same opt-in shape:
            // the canvas paints a
            // yellow halo when the socket is unwired and there's no inline
            // pill, and the pre-export validator (GraphValidator
            // .CheckRequiredInputs) emits a header comment.
            //
            // API.* — Url is the only meaningful invariant.
            SetRequiredInputs("API.Call", "Url");

            // Discord.* (bot REST + webhook) — channel/guild/user ids and the
            // payload itself are non-negotiable. Reactions additionally need
            // a target message and emoji.
            SetRequiredInputs("Discord.Webhook",      "URL", "Msg");
            SetRequiredInputs("Discord.SendMessage",  "ChannelId", "Content");
            SetRequiredInputs("Discord.SendEmbed",    "ChannelId");
            SetRequiredInputs("Discord.AddRole",      "GuildId", "UserId", "RoleId");
            SetRequiredInputs("Discord.RemoveRole",   "GuildId", "UserId", "RoleId");
            SetRequiredInputs("Discord.React",        "ChannelId", "MessageId", "Emoji");
            SetRequiredInputs("Discord.GetUser",      "UserId");

            // Twitch lookups — Username is the lookup key; missing means the
            // node has nothing to query against.
            SetRequiredInputs("Twitch.GetUser",      "Username");
            SetRequiredInputs("Twitch.GetStream",    "Username");
            SetRequiredInputs("Twitch.CheckRole",    "Username");
            SetRequiredInputs("Twitch.GetFollowAge", "Username");
            SetRequiredInputs("Twitch.LastActive",   "Username");

            // Audio — Path on Play / Text on PlayTts. Volume has a sensible
            // default and is intentionally not flagged so a vanilla drop
            // stays playable.
            SetRequiredInputs("Audio.Play",    "Path");
            SetRequiredInputs("Audio.PlayTts", "Text");

            // Second-level sub-group tags for the crowded right-click "Spawn ►"
            // categories. Runs last: every template must already exist so the
            // tags land and the (Category, SubGroup) display order is captured.
            RegisterSubGroupTags();

            GlobalLogger.Log($"NodeRegistry: {_templates.Count} templates registered across {GetCategoryCount()} categories.",
                "NodeRegistry", LogLevel.System);
        }

        /// <summary>
        /// Backfills <see cref="NodeTemplate.SocketDescriptions"/> after a template is
        /// registered. Used to attach per-socket help text without growing the dense
        /// AddTemplate signature for every existing call site. Silently no-ops when the
        /// title isn't registered (caller typo) so an unrelated template registration
        /// failure doesn't cascade into the static init failing for everything.
        /// </summary>
        private static void SetSocketDescriptions(string title, Dictionary<string, string> map)
        {
            if (!_templates.TryGetValue(title, out var t)) return;
            foreach (var kv in map)
            {
                if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                t.SocketDescriptions[kv.Key] = kv.Value;
            }
        }

        private static int GetCategoryCount()
        {
            var cats = new HashSet<string>();
            foreach (var t in _templates.Values) cats.Add(t.Category);
            return cats.Count;
        }

        // Bubble-key convention used by every Math/Logic/Text node template:
        // title "Foo.Bar" maps to localization key "architect.node.bubble.foo_bar".
        // Centralized so AddBinaryOp / AddUnaryOp can derive it from the title
        // and keep operator registrations to one line apiece.
        private static string BubbleKeyFor(string title)
            => "architect.node.bubble." + title.Replace(".", "_").ToLowerInvariant();

        // One-line registration for the (A,B → Result) operators that fill
        // Math + Logic. Replaces the byte-identical 4-line AddTemplate blocks
        // for Add/Subtract/Multiply/Divide/Modulo/Min/Max/Equals/NotEquals/
        // GreaterThan/LessThan/And/Or.
        // aDefault / bDefault opt the operator into the inline-bubble pattern
        // so users can hardcode a literal into either input without dropping a
        // Value.* node first. Pass the same string the ScriptExporter uses as
        // its fallback ("0", "1", "false") so existing graphs that omit the
        // attribute keep emitting identical output.
        private static void AddBinaryOp(string title, string category, Color color,
            Color aType, Color bType, Color resultType,
            string? displayName = null,
            string? aDefault = null, string? bDefault = null)
        {
            Dictionary<string, string>? props = null;
            if (aDefault != null || bDefault != null)
            {
                props = new Dictionary<string, string>();
                if (aDefault != null) props["A"] = aDefault;
                if (bDefault != null) props["B"] = bDefault;
            }
            AddTemplate(title, category, color,
                Localizer.T(BubbleKeyFor(title)),
                new[] { ("A", aType), ("B", bType) },
                new[] { ("Result", resultType) },
                properties: props,
                displayName: displayName);
        }

        // One-line registration for the (Val → Result) operators — Math.Abs/
        // Floor/Ceil and Logic.Not. valDefault opts in to the inline-bubble
        // pattern (see AddBinaryOp).
        private static void AddUnaryOp(string title, string category, Color color,
            Color valType, Color resultType,
            string? valDefault = null)
        {
            Dictionary<string, string>? props = valDefault == null
                ? null
                : new Dictionary<string, string> { { "Val", valDefault } };
            AddTemplate(title, category, color,
                Localizer.T(BubbleKeyFor(title)),
                new[] { ("Val", valType) },
                new[] { ("Result", resultType) },
                properties: props);
        }

        private static void AddTemplate(
            string title, string cat, Color color,
            string description,
            (string, Color)[]? inputs, (string, Color)[]? outputs,
            Dictionary<string, string>? properties = null,
            string? displayName = null,
            string? compactSymbol = null)
        {
            var template = new NodeTemplate
            {
                Title       = title,
                Category    = cat,
                HeaderColor = color,
                Description = description,
                DefaultProperties = properties ?? new Dictionary<string, string>(),
                // DisplayName falls back to Title when no override is supplied,
                // so every untouched template renders identically to before this change.
                DisplayName   = string.IsNullOrEmpty(displayName) ? title : displayName,
                CompactSymbol = compactSymbol ?? string.Empty,
            };
            if (inputs  != null) foreach (var i in inputs)  template.Inputs.Add(i);
            if (outputs != null) foreach (var o in outputs) template.Outputs.Add(o);
            // Socket lists are final at this point (nothing mutates them after
            // registration), so the derived name/order caches freeze here.
            template.RebuildSocketLookups();
            _templates[title] = template;
        }

        /// <summary>
        /// Backfills CompactSymbol on already-registered templates. Mirrors the
        /// SetSocketDescriptions pattern so the dense AddTemplate call sites for the
        /// Math/Logic operators don't need to grow a new positional argument each.
        /// </summary>
        private static void SetCompactSymbol(string title, string symbol)
        {
            if (_templates.TryGetValue(title, out var t))
                t.CompactSymbol = symbol ?? string.Empty;
        }

        /// <summary>
        /// Backfills <see cref="NodeTemplate.Keywords"/> on a registered template
        /// (fuzzy spawn search). Aliases are case-insensitive and stored
        /// lower-cased so the spawn-search matcher can prefix-compare without
        /// re-folding case per query.
        /// </summary>
        private static void SetKeywords(string title, params string[] keywords)
        {
            if (!_templates.TryGetValue(title, out var t)) return;
            foreach (var k in keywords)
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                string lower = k.Trim().ToLowerInvariant();
                if (!t.Keywords.Contains(lower, StringComparer.OrdinalIgnoreCase))
                    t.Keywords.Add(lower);
            }
        }

        /// <summary>
        /// Backfills <see cref="NodeTemplate.IconGlyph"/> on a registered
        /// template. The string is a single Segoe Fluent Icons / Segoe MDL2 Assets
        /// codepoint (e.g. "") rendered as a 16x16 FontIcon left of the node
        /// header title — see <c>NodeView.xaml</c>'s title-row FontIcon binding and
        /// <c>NodeViewModel.IconGlyph</c>. Silently no-ops when the title isn't
        /// registered (typo in the post-template helper block) so an unrelated
        /// template registration failure doesn't cascade into a static-init failure
        /// for the rest of the registry.
        /// </summary>
        private static void SetIconGlyph(string title, string codepoint)
        {
            if (string.IsNullOrEmpty(codepoint)) return;
            if (_templates.TryGetValue(title, out var t))
                t.IconGlyph = codepoint;
        }

        /// <summary>
        /// Backfills <see cref="NodeTemplate.RequiredInputs"/> on a
        /// registered template. Each entry is the canonical socket name of an
        /// input that must be wired (or have a non-empty inline value) for the
        /// node to function — the canvas paints a yellow "required-but-empty"
        /// halo on any matching input that has no incoming link. Used for
        /// "won't work without a value" inputs like <c>Url</c> on HTTP.*,
        /// <c>TableName</c> on DB.*, <c>Path</c> on File.*. Silently no-ops
        /// when the title isn't registered.
        /// </summary>
        private static void SetRequiredInputs(string title, params string[] inputNames)
        {
            if (inputNames is null || inputNames.Length == 0) return;
            if (!_templates.TryGetValue(title, out var t)) return;
            foreach (var name in inputNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                t.RequiredInputs.Add(name.Trim());
            }
        }

        /// <summary>
        /// Tags a registered template with a second-level <see
        /// cref="NodeTemplate.SubGroup"/> for the right-click "Spawn ►" cascade
        /// (see <c>LogicCanvasView.BuildSpawnCategoryCascade</c>). The FIRST time
        /// a given (Category, group) pair is seen, its display order is recorded
        /// in <see cref="_subGroupOrder"/> — so authoring the SetSubGroup calls
        /// in the order you want the sub-menus to appear (Twitch before YouTube,
        /// Branch before Loops) is all that's needed; the UI never re-lists them.
        /// Silently no-ops on an unknown title (typo in the tag block) so one bad
        /// line doesn't cascade into a static-init failure for the whole registry.
        /// </summary>
        private static void SetSubGroup(string title, string group)
        {
            if (string.IsNullOrWhiteSpace(group)) return;
            if (!_templates.TryGetValue(title, out var t)) return;
            string trimmed = group.Trim();
            t.SubGroup = trimmed;
            bool alreadySeen = false;
            foreach (var pair in _subGroupOrder)
            {
                if (string.Equals(pair.Category, t.Category, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(pair.SubGroup, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    alreadySeen = true;
                    break;
                }
            }
            if (!alreadySeen) _subGroupOrder.Add((t.Category, trimmed));
        }

        /// <summary>
        /// Assigns the second-level sub-group tags that turn the six crowded
        /// right-click "Spawn ►" category flyouts into themed sub-menus. Order of
        /// the calls IS the display order of the sub-menus (see
        /// <see cref="SetSubGroup"/>). Called once from <see
        /// cref="RegisterDefaults"/> after every template is registered. Every
        /// node in a tagged category is listed here; a node left untagged still
        /// appears — the cascade drops it into a trailing "Other" sub-menu so
        /// nothing is ever silently lost when a new node is added to one of these
        /// categories without a tag.
        /// </summary>
        private static void RegisterSubGroupTags()
        {
            // ── Platforms (grab-bag of every external target) ─────────────────
            // Cross-platform chat helpers sit above the per-platform action sets.
            SetSubGroup("Chat.Send",         "Chat (all platforms)");
            SetSubGroup("Chat.MessageCount", "Chat (all platforms)");

            // Twitch — the fat single-platform bucket (chat, moderation, channel,
            // polls/predictions, reward management all under one roof).
            SetSubGroup("Twitch.SendChat",          "Twitch");
            SetSubGroup("Twitch.Reply",             "Twitch");
            SetSubGroup("Twitch.Whisper",           "Twitch");
            SetSubGroup("Twitch.Announcement",      "Twitch");
            SetSubGroup("Twitch.Shoutout",          "Twitch");
            SetSubGroup("Twitch.Timeout",           "Twitch");
            SetSubGroup("Twitch.Ban",               "Twitch");
            SetSubGroup("Twitch.Unban",             "Twitch");
            SetSubGroup("Twitch.Mod",               "Twitch");
            SetSubGroup("Twitch.Unmod",             "Twitch");
            SetSubGroup("Twitch.Vip",               "Twitch");
            SetSubGroup("Twitch.Unvip",             "Twitch");
            SetSubGroup("Twitch.DeleteMessage",     "Twitch");
            SetSubGroup("Twitch.SlowMode",          "Twitch");
            SetSubGroup("Twitch.FollowerMode",      "Twitch");
            SetSubGroup("Twitch.SubOnlyMode",       "Twitch");
            SetSubGroup("Twitch.Marker",            "Twitch");
            SetSubGroup("Twitch.CreateClip",        "Twitch");
            SetSubGroup("Twitch.UpdateChannel",     "Twitch");
            SetSubGroup("Twitch.CreatePoll",        "Twitch");
            SetSubGroup("Twitch.EndPoll",           "Twitch");
            SetSubGroup("Twitch.CreatePrediction",  "Twitch");
            SetSubGroup("Twitch.ResolvePrediction", "Twitch");
            SetSubGroup("Twitch.UpdateRewardCost",  "Twitch");
            SetSubGroup("Twitch.SetRewardEnabled",  "Twitch");
            SetSubGroup("Twitch.FulfillRedemption", "Twitch");
            SetSubGroup("Twitch.RejectRedemption",  "Twitch");

            // YouTube.
            SetSubGroup("YouTube.SendChat",       "YouTube");
            SetSubGroup("YouTube.SetTitle",       "YouTube");
            SetSubGroup("YouTube.SetDescription", "YouTube");
            SetSubGroup("YouTube.Timeout",        "YouTube");
            SetSubGroup("YouTube.Ban",            "YouTube");
            SetSubGroup("YouTube.CreatePoll",     "YouTube");
            SetSubGroup("YouTube.EndPoll",        "YouTube");

            // Kick.
            SetSubGroup("Kick.SendChat",         "Kick");
            SetSubGroup("Kick.Reply",            "Kick");
            SetSubGroup("Kick.Timeout",          "Kick");
            SetSubGroup("Kick.Untimeout",        "Kick");
            SetSubGroup("Kick.Ban",              "Kick");
            SetSubGroup("Kick.Unban",            "Kick");
            SetSubGroup("Kick.DeleteMessage",    "Kick");
            SetSubGroup("Kick.SetTitle",         "Kick");
            SetSubGroup("Kick.SetCategory",      "Kick");
            SetSubGroup("Kick.SetRewardCost",    "Kick");
            SetSubGroup("Kick.SetRewardEnabled", "Kick");

            // Discord.
            SetSubGroup("Discord.Webhook",     "Discord");
            SetSubGroup("Discord.SendMessage", "Discord");
            SetSubGroup("Discord.SendEmbed",   "Discord");
            SetSubGroup("Discord.AddRole",     "Discord");
            SetSubGroup("Discord.RemoveRole",  "Discord");
            SetSubGroup("Discord.React",       "Discord");
            SetSubGroup("Discord.GetUser",     "Discord");

            // Web / HTTP (raw requests + the generic API.Call helper).
            SetSubGroup("HTTP.Get",       "Web / HTTP");
            SetSubGroup("HTTP.Post",      "Web / HTTP");
            SetSubGroup("HTTP.Put",       "Web / HTTP");
            SetSubGroup("HTTP.Patch",     "Web / HTTP");
            SetSubGroup("HTTP.Delete",    "Web / HTTP");
            SetSubGroup("HTTP.ParseJson", "Web / HTTP");
            SetSubGroup("API.Call",       "Web / HTTP");

            // Audio.
            SetSubGroup("Audio.Play",      "Audio");
            SetSubGroup("Audio.PlayTts",   "Audio");
            SetSubGroup("Audio.SetVolume", "Audio");

            // Files.
            SetSubGroup("File.ReadText",  "Files");
            SetSubGroup("File.WriteText", "Files");
            SetSubGroup("File.ReadJSON",  "Files");
            SetSubGroup("File.WriteJSON", "Files");

            // Streamer.bot passthrough.
            SetSubGroup("StreamerBot.DoAction", "Streamer.bot");
            SetSubGroup("StreamerBot.GetUser",  "Streamer.bot");

            // ── Events ────────────────────────────────────────────────────────
            SetSubGroup("Twitch.Subscription", "Twitch");
            SetSubGroup("Twitch.Resub",        "Twitch");
            SetSubGroup("Twitch.GiftSub",      "Twitch");
            SetSubGroup("Twitch.GiftBomb",     "Twitch");
            SetSubGroup("Twitch.Raid",         "Twitch");
            SetSubGroup("Twitch.Cheer",        "Twitch");
            SetSubGroup("Twitch.Follow",       "Twitch");
            SetSubGroup("Twitch.PointRedeem",  "Twitch");
            SetSubGroup("Twitch.InWhisper",    "Twitch");

            SetSubGroup("Stream.GoingLive",    "Stream & Schedule");
            SetSubGroup("Stream.SessionEnd",   "Stream & Schedule");
            SetSubGroup("Schedule.Cron",       "Stream & Schedule");
            SetSubGroup("Schedule.RunAt",      "Stream & Schedule");
            SetSubGroup("Schedule.Recurring",  "Stream & Schedule");

            SetSubGroup("System.Startup",      "System & Input");
            SetSubGroup("System.Hotkey",       "System & Input");
            SetSubGroup("System.Clipboard",    "System & Input");
            SetSubGroup("State.OnChange",      "System & Input");

            SetSubGroup("Chat.Message",         "Chat & Signals");
            SetSubGroup("Bus.OnMessage",        "Chat & Signals");
            SetSubGroup("HTTP.WebhookListener", "Chat & Signals");
            SetSubGroup("WS.Server",            "Chat & Signals");
            SetSubGroup("OBS.Event",            "Chat & Signals");

            SetSubGroup("Event.Trigger",  "Custom Events");
            SetSubGroup("Event.Executor", "Custom Events");
            SetSubGroup("Event.Return",   "Custom Events");

            // ── Flow Control ────────────────────────────────────────────────────
            SetSubGroup("Logic.Branch", "Branch & Select");
            SetSubGroup("Logic.If",     "Branch & Select");
            SetSubGroup("Logic.Switch", "Branch & Select");
            SetSubGroup("Flow.Select",  "Branch & Select");
            SetSubGroup("Flow.IsValid", "Branch & Select");

            SetSubGroup("Flow.ForEach",   "Loops & Sequence");
            SetSubGroup("Flow.ForLoop",   "Loops & Sequence");
            SetSubGroup("Flow.WhileLoop", "Loops & Sequence");
            SetSubGroup("Logic.Sequence", "Loops & Sequence");

            SetSubGroup("Flow.DoOnce",   "Gates & Timing");
            SetSubGroup("Flow.DoN",      "Gates & Timing");
            SetSubGroup("Flow.FlipFlop", "Gates & Timing");
            SetSubGroup("Flow.Cooldown", "Gates & Timing");
            SetSubGroup("Flow.Delay",    "Gates & Timing");

            // ── Databank ────────────────────────────────────────────────────────
            SetSubGroup("DB.GetVariable", "Variables");
            SetSubGroup("DB.SetVariable", "Variables");
            SetSubGroup("DB.Increment",   "Variables");
            SetSubGroup("DB.CheckExists", "Variables");
            SetSubGroup("DB.DeleteVar",   "Variables");

            SetSubGroup("DB.FindRow",   "Rows");
            SetSubGroup("DB.FetchRow",  "Rows");
            SetSubGroup("DB.GetCell",   "Rows");
            SetSubGroup("DB.SetCell",   "Rows");
            SetSubGroup("DB.InsertRow", "Rows");
            SetSubGroup("DB.DeleteRow", "Rows");
            SetSubGroup("DB.RowCount",  "Rows");

            SetSubGroup("DB.GetColumn",  "Table");
            SetSubGroup("DB.Top",        "Table");
            SetSubGroup("DB.ClearTable", "Table");

            // ── OBS ─────────────────────────────────────────────────────────────
            SetSubGroup("OBS.SetScene",             "Scenes & Sources");
            SetSubGroup("OBS.SetSourceVisible",     "Scenes & Sources");
            SetSubGroup("OBS.RefreshBrowserSource", "Scenes & Sources");
            SetSubGroup("OBS.SetSourcePosition",    "Scenes & Sources");
            SetSubGroup("OBS.SetSourceScale",       "Scenes & Sources");
            SetSubGroup("OBS.SetSourceRotation",    "Scenes & Sources");

            SetSubGroup("OBS.StartRecording",   "Recording & Streaming");
            SetSubGroup("OBS.StopRecording",    "Recording & Streaming");
            SetSubGroup("OBS.StartStreaming",   "Recording & Streaming");
            SetSubGroup("OBS.StopStreaming",    "Recording & Streaming");
            SetSubGroup("OBS.SaveReplayBuffer", "Recording & Streaming");

            SetSubGroup("OBS.SetFilterVisible", "Filters & Capture");
            SetSubGroup("OBS.TakeScreenshot",   "Filters & Capture");

            // ── Collections ─────────────────────────────────────────────────────
            SetSubGroup("Array.Make",    "Build");
            SetSubGroup("Array.Literal", "Build");

            SetSubGroup("Array.Get",      "Access");
            SetSubGroup("Array.Length",   "Access");
            SetSubGroup("Array.Contains", "Access");
            SetSubGroup("Array.Unpack",   "Access");

            SetSubGroup("Array.Push",    "Transform");
            SetSubGroup("Array.Filter",  "Transform");
            SetSubGroup("Array.Sort",    "Transform");
            SetSubGroup("Array.Shuffle", "Transform");
            SetSubGroup("Array.Reverse", "Transform");
            SetSubGroup("Array.Unique",  "Transform");
        }
    }
}
