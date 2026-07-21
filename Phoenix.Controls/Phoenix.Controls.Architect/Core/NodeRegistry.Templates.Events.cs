using System;
using System.Collections.Generic;
using System.Drawing;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Architect.Core
{
    // First per-band carve from NodeRegistry.Templates.cs.
    // Owns the EVENTS band: every node template that *triggers* a script
    // (Chat.Message, Twitch.* events, the PlatformEventCatalog-driven
    // YouTube.* / Kick.* events, System.Startup, Bus.OnMessage,
    // Event.Trigger / Event.Executor / Event.Return, HTTP.WebhookListener,
    // Schedule.* timers, State.OnChange). RegisterDefaults() in the parent
    // partial calls RegisterEventsTemplates() declaratively — no behaviour
    // change.
    public static partial class NodeRegistry
    {
        private static void RegisterEventsTemplates()
        {
            // Unified multi-platform chat trigger — replaces
            // Twitch.ChatMessage. The Twitch / YouTube /
            // Kick attributes are the per-platform checkmarks consumed by
            // ScriptExporter.ProcessChatMessageEventNode (selection == {twitch}
            // emits the legacy bare `on_chat:`; anything else emits
            // `on_chat(<platforms>):`); new nodes default all-on.
            // GraphSerializer.MigrateNodes retitles legacy Twitch.ChatMessage /
            // YouTube.Message nodes onto this template on load with only their
            // own platform checked, so migration is a zero-behavior-change
            // rename and the old titles never survive a load.
            //
            // IsMod / IsSub / IsBroadcaster / IsVip / SubMonths / ColorHex
            // outputs appended 2026-06-08. The engine already binds these chatter
            // fields (ScriptManager BuildChatVars: user.is_mod / user.is_sub /
            // user.is_broadcaster / user.is_vip / user.sub_months / user.color_hex)
            // but pre-fix they were reachable only by raw {user.is_mod}-style token
            // typing. Appended after Args so existing graphs keep their socket order
            // (additive — no prune). The ChatMessage-specific exporter block maps
            // each socket name to its {user.*} var. Platform (→ {user.platform})
            // is appended LAST for the same order-preservation reason: migrated
            // nodes must keep their existing socket order, with Platform
            // back-filling additively at the end.
            AddTemplate("Chat.Message",        "Events", Color.Teal,
                Localizer.T("architect.node.bubble.chat_message"),
                null,
                new[] {
                    ("Flow",          ColExec),
                    ("Message",       ColString),
                    ("User",          ColList),
                    ("IsCommand",     ColBool),
                    ("Command",       ColString),
                    ("Args",          ColList),
                    ("IsMod",         ColBool),
                    ("IsSub",         ColBool),
                    ("IsBroadcaster", ColBool),
                    ("IsVip",         ColBool),
                    ("SubMonths",     ColNumber),
                    ("ColorHex",      ColString),
                    ("Platform",      ColString),
                    // MessageId (→ {event.message_id}) appended LAST 2026-07-14 for
                    // the same order-preservation reason as Platform: migrated /
                    // already-saved graphs keep their existing socket order and
                    // MigrateNodes back-fills this output additively. Feeds the
                    // reply / delete-message nodes with the triggering message's id.
                    ("MessageId",     ColString)
                },
                new Dictionary<string, string>
                {
                    { "Commands", "" },
                    { "Twitch",   "true" },
                    { "YouTube",  "true" },
                    { "Kick",     "true" }
                });

            // Unified multi-platform stream-lifecycle triggers — one node fires
            // whenever the broadcaster goes live / ends the session on ANY of the
            // checked platforms. Same checkmark pattern as Chat.Message: the
            // Twitch / YouTube / Kick bool attrs render as ☑/☐ rows on the node
            // body (the generic MiddleAttributeViewModel path — no per-node UI
            // code) and default all-on. ScriptExporter.ProcessStreamLifecycleEventNode
            // emits on_go_live(<platforms>): / on_session_end(<platforms>):; the
            // engine gates the block by the firing event's platform and applies a
            // per-instance 10s cooldown so a simultaneous multi-stream go-live
            // fires each node once (StreamLifecycle is the single-source map).
            // Platform → {user.platform}, Title → {event.title},
            // Category → {event.category}; all populated by the Hub dispatch.
            // Additive to the existing per-platform stream events (Kick.StreamOnline,
            // YouTube.BroadcastStarted/Ended, …), which keep their own on_event path.
            AddTemplate("Stream.GoingLive",    "Events", Color.OrangeRed,
                Localizer.T("architect.node.bubble.stream_goinglive"),
                null,
                new[] { ("Flow", ColExec), ("Platform", ColString), ("Title", ColString), ("Category", ColString) },
                new Dictionary<string, string>
                {
                    { "Twitch",  "true" },
                    { "YouTube", "true" },
                    { "Kick",    "true" }
                });

            AddTemplate("Stream.SessionEnd",   "Events", Color.SlateGray,
                Localizer.T("architect.node.bubble.stream_sessionend"),
                null,
                new[] { ("Flow", ColExec), ("Platform", ColString), ("Title", ColString), ("Category", ColString) },
                new Dictionary<string, string>
                {
                    { "Twitch",  "true" },
                    { "YouTube", "true" },
                    { "Kick",    "true" }
                });

            AddTemplate("Twitch.Subscription", "Events", Color.ForestGreen,
                Localizer.T("architect.node.bubble.twitch_subscription"),
                null,
                new[] { ("Flow", ColExec), ("User", ColString), ("Months", ColNumber), ("Tier", ColString) });

            // Tier output added 2026-06-03 — a resub carries its tier just like a fresh
            // sub; maps to {user.tier} (normalized 1/2/3/prime). Appended so existing
            // graphs keep their socket order.
            AddTemplate("Twitch.Resub",        "Events", Color.ForestGreen,
                Localizer.T("architect.node.bubble.twitch_resub"),
                null,
                new[] { ("Flow", ColExec), ("User", ColString), ("Months", ColNumber), ("Message", ColString), ("Tier", ColString) });

            // IsAnonymous output added 2026-06-03 — Twitch gift subs can be anonymous;
            // maps to {user.is_anonymous} (already populated at runtime). Gate "thank the
            // gifter" logic on it (an anonymous gift surfaces the placeholder gifter).
            AddTemplate("Twitch.GiftSub",      "Events", Color.ForestGreen,
                Localizer.T("architect.node.bubble.twitch_giftsub"),
                null,
                new[] { ("Flow", ColExec), ("Gifter", ColString), ("Recipient", ColString), ("Tier", ColString), ("IsAnonymous", ColBool) });

            AddTemplate("Twitch.GiftBomb",     "Events", Color.ForestGreen,
                Localizer.T("architect.node.bubble.twitch_giftbomb"),
                null,
                new[] { ("Flow", ColExec), ("Gifter", ColString), ("Count", ColNumber), ("IsAnonymous", ColBool) });

            // The Viewers output is forwarded VERBATIM from
            // Streamer.bot's raid envelope. SB ingests the raid event from
            // Twitch IRC USERNOTICE / EventSub, where the viewer-count tag
            // (`msg-param-viewerCount`) is broadcaster-controlled at raid-start
            // AND can be edited inside SB's "Test Trigger" UI. Scripts that
            // gate a reward on Viewers > N are spoofable by anyone with SB
            // access (the broadcaster themselves during testing, mods with SB
            // open, an attacker who can fake a USERNOTICE on the wire). Hub
            // logs a Communication-tier warning when the claimed count exceeds
            // 10000 viewers (see WS.WarnIfRaidViewerCountSuspicious) so the
            // operator sees obviously-fake values; integrators wanting a
            // verified count should add a Helix cross-check in the script
            // body. A first-class twitch.verify_raid() command is a
            // follow-up and not in this sweep.
            AddTemplate("Twitch.Raid",         "Events", Color.ForestGreen,
                Localizer.T("architect.node.bubble.twitch_raid"),
                null,
                new[] { ("Flow", ColExec), ("User", ColString), ("Viewers", ColNumber) });

            AddTemplate("Twitch.Cheer",        "Events", Color.ForestGreen,
                Localizer.T("architect.node.bubble.twitch_cheer"),
                null,
                new[] { ("Flow", ColExec), ("User", ColString), ("Bits", ColNumber), ("Message", ColString) });

            AddTemplate("Twitch.Follow",       "Events", Color.ForestGreen,
                Localizer.T("architect.node.bubble.twitch_follow"),
                null,
                new[] { ("Flow", ColExec), ("User", ColString) });

            AddTemplate("Twitch.PointRedeem",  "Events", Color.ForestGreen,
                Localizer.T("architect.node.bubble.twitch_pointredeem"),
                null,
                new[] { ("Flow", ColExec), ("User", ColString), ("Reward", ColString), ("Input", ColString) });

            // Inbound whisper (private DM) to the bot account. Distinct title
            // from the OUTBOUND "Twitch.Whisper" action node (RemainingBands) —
            // both can't share a template key. The exporter maps this title to
            // on_event(Twitch.Whisper) (the real SB event type). Reply with the
            // existing twitch.whisper action wired from User. Requires the bot
            // account connected in Streamer.bot with whisper-read authorized.
            AddTemplate("Twitch.InWhisper",    "Events", Color.ForestGreen,
                Localizer.T("architect.node.bubble.twitch_inwhisper"),
                null,
                new[] { ("Flow", ColExec), ("User", ColString), ("Message", ColString), ("UserId", ColString) });

            AddTemplate("System.Startup",      "Events", Color.DimGray,
                Localizer.T("architect.node.bubble.system_startup"),
                null,
                new[] { ("Flow", ColExec) });

            AddTemplate("Bus.OnMessage",       "Events", Color.SteelBlue,
                Localizer.T("architect.node.bubble.bus_onmessage"),
                null,
                new[] { ("Flow", ColExec), ("Type", ColString), ("Payload", ColString) },
                new Dictionary<string, string>
                {
                    { "EventType", "VISUAL_COMPLETE" },
                    { "Source",    "*" },
                    { "Target",    "*" }
                });

            AddTemplate("Event.Trigger",       "Events", Color.MediumOrchid,
                Localizer.T("architect.node.bubble.event_trigger"),
                new[] { ("Flow", ColExec) },
                new[] { ("Flow", ColExec) },
                new Dictionary<string, string> { { "EventName", "MyEvent" } });

            AddTemplate("Event.Executor",      "Events", Color.DarkOrchid,
                Localizer.T("architect.node.bubble.event_executor"),
                null,
                new[] { ("Flow", ColExec) },
                new Dictionary<string, string> { { "EventName", "MyEvent" } });

            AddTemplate("Event.Return",         "Events", Color.FromArgb(180, 90, 180),
                Localizer.T("architect.node.bubble.event_return"),
                new[] { ("Flow", ColExec) },
                null,
                new Dictionary<string, string> { { "EventName", "MyEvent" } });

            // Payload output appended 2026-06-08. The engine already binds
            // event.payload alongside event.body (ScriptManager.ExecuteOnWebhookScriptsAsync:
            // vars["event.payload"] = rawBody ?? data.GetRawText()). Payload is the
            // raw request body just like Body, exposed as a distinct output so a
            // graph can wire it without raw {event.payload} token typing. Additive
            // (appended last) — no prune. Maps via the generic event-output switch
            // ("Payload" => "{event.payload}").
            AddTemplate("HTTP.WebhookListener", "Events", Color.DarkSlateBlue,
                Localizer.T("architect.node.bubble.http_webhooklistener"),
                null,
                new[] { ("Flow", ColExec), ("Body", ColString), ("Method", ColString), ("Path", ColString), ("Payload", ColString) },
                new Dictionary<string, string> { { "Name", "default" } });

            AddTemplate("Schedule.Cron",       "Events", Color.DimGray,
                Localizer.T("architect.node.bubble.schedule_cron"),
                null,
                new[] { ("Flow", ColExec), ("Timestamp", ColString) },
                new Dictionary<string, string> { { "CronExpression", "*/5 * * * *" } });

            AddTemplate("Schedule.RunAt",      "Events", Color.DimGray,
                Localizer.T("architect.node.bubble.schedule_runat"),
                null,
                new[] { ("Flow", ColExec), ("Timestamp", ColString) },
                new Dictionary<string, string> { { "DateTime", "2026-01-01T20:00:00" } });

            AddTemplate("Schedule.Recurring",  "Events", Color.DimGray,
                Localizer.T("architect.node.bubble.schedule_recurring"),
                null,
                new[] { ("Flow", ColExec), ("Count", ColNumber) },
                new Dictionary<string, string> { { "IntervalSeconds", "60" }, { "MaxCount", "0" } });

            AddTemplate("State.OnChange",      "Events", Color.DarkOrange,
                Localizer.T("architect.node.bubble.state_onchange"),
                null,
                new[] { ("Flow", ColExec), ("Name", ColString), ("OldValue", ColString), ("NewValue", ColString) },
                new Dictionary<string, string> { { "StateName", "stream_phase" } });

            // WS.Server external WebSocket listener.
            // Hub's WebSocketServerService binds /ws/<Name> when
            // AppConfig.WebSocketServerEnabled is on and fires the matching
            // on_websocket("Name"): block on every text frame a client sends.
            // Body carries the raw frame text; Path mirrors the WS path the
            // message arrived on. Streamer-authored panels / dashboards /
            // external bridges connect to ws://<host>:<port>/ws/<Name>.
            //
            // Multi-handler semantics changed in this sweep:
            // multiple scripts declaring on_websocket("Name") now ALL fire
            // (parallel fan-out, consistent with on_chat / on_event /
            // on_clipboard). Pre-fix the first-match-wins loop silently
            // dropped the second+ subscribers. Colliding names also surface
            // a Communication-tier warning at registry-load time via
            // the generalized duplicate detector.
            //
            // Incoming frame aggregation is capped at
            // AppConfig.WebSocketMaxMessageBytes (default 1 MiB). Oversized
            // streams close the socket with WebSocket close 1009 MessageTooBig.
            AddTemplate("WS.Server", "Events", Color.DarkSlateBlue,
                "Fires when an external WebSocket client sends a text frame to the Hub at ws://<host>:<port>/ws/<Name>. Same shape as HTTP.WebhookListener but for live two-way clients (panels / dashboards / bridges). Hub-side server is off by default — turn on WebSocketServerEnabled in config to expose the port. All scripts declaring the same Name fire in parallel; aggregate frame size is capped at WebSocketMaxMessageBytes (default 1 MiB).",
                null,
                // Payload output appended 2026-06-08 (additive, last). The engine
                // already binds event.payload alongside event.body
                // (ScriptManager.ExecuteOnWebSocketScriptsAsync: sharedVars["event.payload"]
                // = rawPayload), mirroring HTTP.WebhookListener. Maps via the generic
                // event-output switch ("Payload" => "{event.payload}").
                new[] { ("Flow", ColExec), ("Body", ColString), ("Path", ColString), ("Payload", ColString) },
                new Dictionary<string, string> { { "Name", "default" } });

            // System.Hotkey event-trigger. Hub's HotkeyService
            // binds the Combination keystroke via Win32 RegisterHotKey when
            // HotkeysEnabled is on and the containing script is enabled,
            // then fires the matching on_hotkey block on press. Combo socket
            // mirrors the bound combination so scripts can branch on which
            // hotkey fired when a single script declares multiple. Hub-side
            // service is off by default.
            AddTemplate("System.Hotkey", "Events", Color.DimGray,
                "Fires when the user presses a system-wide keystroke (e.g. Ctrl+Shift+P). Combination uses '+' separators. Modifiers: Ctrl / Control, Alt, Shift, Win / Windows. Plus exactly one non-modifier: A-Z, 0-9, F1-F24, Space, Enter, Tab, Esc, arrow keys, Insert, Delete, Home, End, PageUp, PageDown. Hub-side HotkeyService is off by default — turn on HotkeysEnabled in config.",
                null,
                new[] { ("Flow", ColExec), ("Combo", ColString) },
                new Dictionary<string, string> { { "Combination", "Ctrl+Shift+P" } });

            // System.Clipboard event-trigger. Hub's
            // ClipboardService subscribes to WM_CLIPBOARDUPDATE on a hidden
            // NativeWindow and fires every script with an on_clipboard:
            // block. Off by default for privacy — Hub never silently
            // observes clipboard contents. Text socket carries the new
            // clipboard text (empty when the format isn't text).
            //
            // Sensitive-content gates the engine enforces BEFORE
            // dispatching to scripts:
            //   * The "Clipboard Viewer Ignore" clipboard format (KeePass /
            //     1Password / Office / VS opt-out convention) — payload is
            //     dropped silently and logged at System tier.
            //   * Any format in the CF_PRIVATEFIRST..CF_PRIVATELAST (0x0200..
            //     0x02FF) Win32-reserved range — same drop+log behavior.
            //   * Text length cap: AppConfig.ClipboardMaxLengthBytes (default
            //     4096 chars). Longer payloads are truncated and the truncation
            //     is logged. Set higher only if a real on_clipboard use case
            //     needs more than a URL / snippet (the limit also reduces the
            //     blast-radius of an accidental large-blob leak).
            // Regex credential-shape detection (JWT / Stripe / Slack token
            // patterns) is deliberately deferred to a later sweep.
            AddTemplate("System.Clipboard", "Events", Color.DimGray,
                "Fires when the system clipboard changes (any copy / cut from any application). Text carries the new text content; non-text formats (image / file paths) yield an empty Text. Hub-side ClipboardService is off by default — turn on ClipboardWatchEnabled in config (privacy default). Sensitive-content opt-out is honored: payloads marked 'Clipboard Viewer Ignore' (KeePass etc.) or carrying any CF_PRIVATEFIRST..CF_PRIVATELAST format are silently dropped. Text is truncated at ClipboardMaxLengthBytes (default 4096) to bound leak blast-radius.",
                null,
                new[] { ("Flow", ColExec), ("Text", ColString) });

            // OBS.Event trigger.
            // Hub's ObsWebSocketClient subscribes to OBS WS v5 events when
            // AppConfig.ObsWebSocketEnabled is true and routes each inbound
            // event to every script declaring on_obs("<EventType>"). The
            // EventType attribute is the bare OBS event name (e.g.
            // "CurrentProgramSceneChanged", "RecordStateChanged",
            // "SceneItemEnableStateChanged"); EventData is the raw JSON
            // string of the OBS eventData object (script-side parse via
            // http.parse_json or text-substring matching).
            //
            // Hub-side service is off by default (privacy + no spurious
            // localhost binds against a non-running OBS).
            AddTemplate("OBS.Event", "Events", Color.MediumPurple,
                "Fires when OBS Studio emits a WebSocket v5 event whose eventType matches the EventType attribute (e.g. CurrentProgramSceneChanged, RecordStateChanged, SceneItemEnableStateChanged). EventData carries the raw JSON of OBS's eventData object — parse with http.parse_json or substring-match against it. Hub-side ObsWebSocketClient is off by default — turn on ObsWebSocketEnabled in config; set ObsEventSubscriptionMask to scope down from the default 1023 (all non-high-frequency categories). Event catalog: github.com/obsproject/obs-websocket protocol docs.",
                null,
                new[] { ("Flow", ColExec), ("EventData", ColString) },
                new Dictionary<string, string> { { "EventType", "CurrentProgramSceneChanged" } });

            // ──────────────────────────────────────────────────────────────
            // YouTube / Kick platform events.
            // PlatformEventCatalog
            // (Phoenix.Controls.Shared/Core/PlatformEventCatalog.cs) is the
            // single source of truth for this surface — sockets, exporter
            // var-tokens, payload probes, and bubble loc-keys all live on the
            // catalog entry; this loop only turns each entry into a palette
            // template (Flow output prepended, catalog sockets after, in
            // catalog order). ScriptExporter, autocomplete/var-chain, and
            // ScriptManager.BuildGenericEventVars consume the same entries, so
            // adding an event to the catalog lights it up everywhere at once.
            // Chat events (YouTube.Message / Kick.ChatMessage) are deliberately
            // absent from the catalog — the unified Chat.Message node above
            // covers them.
            // ──────────────────────────────────────────────────────────────
            foreach (var def in PlatformEventCatalog.Events)
            {
                // Header colors: YouTube keeps the old YouTube.Message red;
                // Kick uses the brand green (#53FC18).
                Color platformColor = def.Platform switch
                {
                    "youtube" => Color.Red,
                    "kick"    => Color.FromArgb(83, 252, 24),
                    _ => throw new InvalidOperationException(
                        $"PlatformEventCatalog entry '{def.Title}' has unmapped platform '{def.Platform}' — add a header color to the RegisterEventsTemplates catalog loop."),
                };

                var outputs = new (string, Color)[def.Sockets.Count + 1];
                outputs[0] = ("Flow", ColExec);
                for (int i = 0; i < def.Sockets.Count; i++)
                {
                    var socket = def.Sockets[i];
                    // Explicit type→color map. CreateNode derives
                    // Socket.DataType back from this color (DataTypeFromColor),
                    // so an unmapped catalog type would silently mint Any ◆
                    // pins that refuse wires (recurring class bug) — throw
                    // instead so a future SocketDataType extension in the
                    // catalog is caught at startup, not on the canvas.
                    Color socketColor = socket.Type switch
                    {
                        SocketDataType.String     => ColString,
                        SocketDataType.Int        => ColNumber,
                        SocketDataType.Bool       => ColBool,
                        SocketDataType.Collection => ColList,
                        _ => throw new InvalidOperationException(
                            $"PlatformEventCatalog entry '{def.Title}' socket '{socket.Name}' has unmapped SocketDataType.{socket.Type} — extend the color map in the RegisterEventsTemplates catalog loop."),
                    };
                    outputs[i + 1] = (socket.Name, socketColor);
                }

                AddTemplate(def.Title, "Events", platformColor,
                    Localizer.T(def.BubbleLocKey),
                    null,
                    outputs);
            }
        }
    }
}
