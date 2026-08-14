using System.Collections.Generic;
using System.Text.Json.Serialization;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Shared.Models
{
    /// <summary>
    /// Strongly-typed application configuration.
    /// Hub loads and exposes this via ConfigManager.
    /// Architect will own the UI for populating Webhooks and future credential fields.
    /// </summary>
    public class AppConfig
    {
        /// <summary>Streamer.bot WebSocket URL.</summary>
        public string StreamerBotUrl  { get; set; } = "ws://127.0.0.1:8080/";

        /// <summary>Port for the HUD overlay HTTP/WebSocket server.</summary>
        public int    HUDServerPort   { get; set; } = 18080;

        /// <summary>Directory containing .phx logic scripts.</summary>
        public string LogicDirectory  { get; set; } = "data/logic";

        /// <summary>Directory containing .phxl layout files (used by Visualist).</summary>
        public string LayoutDirectory { get; set; } = "data/layout";

        /// <summary>
        /// Last folder Architect's File-Open / Save-As picker landed in.
        /// Empty string means "no recall yet — fall back to Paths.HubLogic"
        /// (TODO 2026-05-07 round 1 P3 — CustomFilePicker doesn't remember
        /// last-used folder per pillar). Updated by MainView on a successful
        /// pick; the picker reads it on next Open / Save-As.
        /// </summary>
        public string LastArchitectOpenDir { get; set; } = "";

        /// <summary>
        /// Last folder Visualist's File-Open / Save-As picker landed in.
        /// Empty string means "no recall yet — fall back to Paths.HubLayers".
        /// Same recall pattern as <see cref="LastArchitectOpenDir"/>.
        /// </summary>
        public string LastVisualistOpenDir { get; set; } = "";

        /// <summary>Whether Hub auto-connects to Streamer.bot on startup.</summary>
        public bool   AutoStart       { get; set; } = true;

        /// <summary>
        /// Disable the Windows IME / Text Services Framework input path process-wide
        /// at startup (via <c>ImmDisableIME</c>). Default <c>true</c>. Closes a
        /// confirmed WinUI 3 UI-thread freeze: when an inline TextBox editor gains
        /// focus during canvas interaction (Architect node editing under pan/zoom),
        /// the <c>msctf</c> / <c>TextServicesHost</c> IME window procedure can enter a
        /// nested message loop that never exits, hanging the app 8–15s with no
        /// self-recovery (see <c>ImeGuard</c>; microsoft-ui-xaml #9216 / Chromium
        /// #328859185). Latin-script keyboards — including German QWERTZ (ä/ö/ü/ß are
        /// direct keys, not IME composition) — are unaffected; only CJK-style
        /// composition is turned off. Set <c>false</c> ONLY if you must type via an
        /// IME into node/value fields (the freeze risk returns). Read very early in
        /// App startup, so a change applies on next launch.
        /// </summary>
        public bool   DisableImeInput { get; set; } = true;

        /// <summary>
        /// Named webhook registry.
        /// Key = friendly name used in scripts (e.g. "discord_alerts").
        /// Value = full webhook URL.
        /// Populated by Architect in a future phase; scripts can reference by name or raw URL.
        /// </summary>
        public Dictionary<string, string> Webhooks { get; set; } = new();

        /// <summary>
        /// Discord bot token used by <c>discord.send_message</c> /
        /// <c>discord.send_embed</c> (https://discord.com/api/v10). Empty (default)
        /// disables the bot REST path; scripts get
        /// <c>result.discord_error = "No Discord bot token configured."</c> and no
        /// network call is attempted. The legacy <c>discord.webhook</c> command is
        /// independent of this field — webhooks are the no-token alternative.
        /// </summary>
        [JsonConverter(typeof(DpapiProtectedStringConverter))]
        public string DiscordBotToken { get; set; } = "";

        // ── Streamer.bot chat dispatch ────────────────────────────────────
        /// <summary>Name of the Streamer.bot action that sends a chat message. Must have a "Send Message" sub-action with message = %message%.</summary>
        public string StreamerBotChatAction { get; set; } = "PhoenixControlsChat";

        /// <summary>Twitch username of the bot account. Chat messages from this account are ignored by the script engine to prevent self-triggering.</summary>
        public string BotUsername { get; set; } = "";

        // ── Viewer presence + watch time (ViewerPresenceService) ──────────
        /// <summary>
        /// How often the Hub asks Streamer.bot who is currently in chat, in
        /// seconds. That one sample feeds every consumer that used to open its own
        /// <c>GetActiveViewers</c> round-trip, the platform-role cache, and the
        /// passive watch-time accrual. Default 60; clamped in-code to [15, 300] —
        /// under 15s is a socket round-trip every few seconds for data that changes
        /// at human speed, over 300s makes watch minutes too coarse to be fair.
        /// </summary>
        public int ViewerPresencePollSeconds { get; set; } = 60;

        /// <summary>
        /// When true (default), presence samples accrue watch MINUTES into the OPEN
        /// "WatchTime" table. This is a passive background data source, not a
        /// feature: no pre-build tool has to be enabled for it to record, and the
        /// numbers are what a User-Management group's watch-hour rule, a Ranks
        /// ladder and <c>db.top("WatchTime", …)</c> all read.
        /// </summary>
        public bool WatchTimeTrackingEnabled { get; set; } = true;

        /// <summary>
        /// When true (default), watch minutes accrue only while the stream is live.
        /// Turn it off to count viewers who sit in chat between streams — a Hub left
        /// running overnight will then hand everybody present the hours.
        /// </summary>
        public bool WatchTimeOnlyWhenLive { get; set; } = true;

        /// <summary>
        /// Twitch numeric user id of the bot account, preferred over
        /// <see cref="BotUsername"/> for the self-trigger guard because the
        /// numeric id is immutable and unambiguous (Twitch login is lowercase
        /// ASCII; displayName is operator-styled and may be non-ASCII). When
        /// configured, the WS guard short-circuits on userId match before
        /// falling back to login/displayName comparison.
        /// </summary>
        public string BotUserId { get; set; } = "";

        // ── AI Integration ────────────────────────────────────────────────
        /// <summary>OpenAI API key for AI.Prompt and AI.Moderate nodes.</summary>
        [JsonConverter(typeof(DpapiProtectedStringConverter))]
        public string OpenAIApiKey   { get; set; } = "";

        /// <summary>Anthropic API key for AI.Prompt nodes using claude-* models.</summary>
        [JsonConverter(typeof(DpapiProtectedStringConverter))]
        public string AnthropicKey   { get; set; } = "";

        /// <summary>
        /// Base URL for a local Ollama daemon. Reached when an AI script's Model is
        /// authored as <c>ollama/&lt;name&gt;</c> (e.g. <c>ollama/llama3</c>). The
        /// prefix is stripped before the model name is forwarded to the
        /// <c>/api/chat</c> endpoint. Loopback by default — Ollama exposes no
        /// authentication, so LAN exposure is opt-in by changing this URL.
        /// </summary>
        public string OllamaUrl      { get; set; } = "http://localhost:11434";

        /// <summary>
        /// Cerebras API key for AI scripts authored with <c>cerebras/&lt;model&gt;</c>.
        /// Cerebras's <c>/v1/chat/completions</c> is OpenAI-compatible (same SSE
        /// shape, same request body); only the host and bearer key differ. The
        /// <c>cerebras/</c> prefix is stripped before the bare model name is
        /// forwarded.
        /// </summary>
        [JsonConverter(typeof(DpapiProtectedStringConverter))]
        public string CerebrasApiKey { get; set; } = "";

        /// <summary>Default AI model used when the Model input is empty. e.g. "gpt-4o-mini" or "claude-haiku-4-5-20251001".</summary>
        public string DefaultAIModel { get; set; } = "gpt-4o-mini";

        /// <summary>
        /// Per-request <c>max_tokens</c> cap for AI prompt /
        /// streaming calls. Previously hardcoded to 1024 (single-shot) and
        /// 4096 (streaming). Surfaced to config so streamers can tune the
        /// cap against the model context window they're paying for. The
        /// final answer's <c>stop_reason</c> / <c>finish_reason</c> is
        /// bubbled to scripts as <c>result.stop_reason</c> so a script can
        /// branch on <c>length</c> (cap hit, continue with a follow-up
        /// turn) vs <c>stop</c> / <c>end_turn</c> (model completed cleanly).
        /// </summary>
        public int AiMaxTokens { get; set; } = 2048;

        // ── Script Engine ─────────────────────────────────────────────────
        /// <summary>Maximum seconds a single .phx script may run before being cancelled. 0 = no timeout.</summary>
        public int ScriptTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Retention window (days) for the unbounded persistence
        /// tables (<c>EventLog</c> / <c>SystemHistory</c>). On every
        /// <see cref="Services.DB.Initialize"/> the rows older than this cap
        /// are deleted in a single DELETE per table. Set to 0 (or negative) to
        /// disable the sweep entirely — useful for forensic captures where
        /// truncating history is unacceptable. Default 30 days keeps the
        /// long-running streamer DB bounded without losing recent activity.
        /// </summary>
        public int LogRetentionDays { get; set; } = 30;

        /// <summary>
        /// Row cap for the <c>EventLog</c> audit table, enforced by a
        /// startup + daily sweep that keeps only the newest N rows (rowid
        /// order). Complements <see cref="LogRetentionDays"/>: the day-based
        /// sweep bounds age, this bounds absolute row count — EventLog rows
        /// carry the full raw event JSON (multi-KB each), so a busy 24/7
        /// stream can outgrow the day window long before it expires. The
        /// default is deliberately small: EventLog is the recent-events
        /// diagnostic surface, while long-term history lives in the
        /// SystemHistory log database. Set to 0 (or negative) to keep every
        /// row forever.
        /// </summary>
        public int EventLogRetentionRows { get; set; } = 10_000;

        /// <summary>Maximum number of chat-triggered scripts running concurrently. 0 = unlimited.</summary>
        public int MaxConcurrentChatScripts { get; set; } = 3;

        /// <summary>
        /// Dispatch mode for the scripts matched by ONE chat message. Default
        /// false = they run one after another in registry order, so each script
        /// sees the previous one's variable/DB writes — the historical
        /// contract. True = they fan out concurrently up to
        /// <see cref="MaxConcurrentChatScripts"/>, which is faster when one
        /// script does slow I/O but requires scripts sharing persisted vars to
        /// use atomic operations (<c>db.increment</c>) instead of get-then-set.
        /// </summary>
        public bool ParallelChatScripts { get; set; } = false;

        /// <summary>
        /// Bounded wait (seconds) the chat consumer gives one message's script
        /// dispatch before detaching and moving on to the next queued message.
        /// Chat is a single sequential consumer, so a script that deliberately
        /// sleeps (<c>delay_seconds</c>) would otherwise block every subsequent
        /// chat message — including the very commands that script is waiting
        /// for — for its whole sleep. Ordering is preserved for scripts that
        /// finish within the window; a detached script keeps running in the
        /// background and holds its <see cref="MaxConcurrentChatScripts"/>
        /// slot until it finishes. 0 = disabled (legacy fully-sequential
        /// dispatch). Default 5.
        /// </summary>
        public int ChatDispatchDetachSeconds { get; set; } = 5;

        /// <summary>Maximum number of webhook-triggered scripts running concurrently.
        /// Shared with <c>on_websocket</c> dispatch — both flavors draw from the one
        /// ScriptManager webhook semaphore (on_websocket is additionally bounded
        /// upstream by <see cref="MaxConcurrentWebsocketScripts"/>). Hotkey and
        /// clipboard scripts have their own caps and no longer draw from this pool.
        /// 0 = unlimited.</summary>
        public int MaxConcurrentWebhookScripts { get; set; } = 5;

        /// <summary>
        /// Maximum number of event-triggered scripts running concurrently — Twitch
        /// sub/cheer/raid/follow, OBS/YouTube events, internal <c>event.trigger</c>,
        /// on_startup, on_bus, on_state_change, and scheduler fires all draw from this
        /// pool. Previously hard-coded to 5 in ScriptManager; surfaced here so a busy channel that legitimately fans many
        /// events at once isn't throttled into the 30s queue-then-drop cycle. Default 8
        /// gives headroom for sub-trains / raid+shoutout bursts while
        /// still bounding a runaway loop. 0 = unlimited (not recommended). Nested
        /// <c>event.trigger</c> calls re-enter the held slot, so they don't count against
        /// this cap. Excess invocations queue on the semaphore (30s) — then drop.
        /// </summary>
        public int MaxConcurrentEventScripts { get; set; } = 8;

        /// <summary>
        /// Maximum number of <c>on_websocket("name")</c>-triggered
        /// scripts running concurrently from the external WebSocket listener.
        /// Mirrors <see cref="MaxConcurrentWebhookScripts"/>; default 4 keeps a
        /// chatty external bridge from spawning unlimited script invocations
        /// when it floods <c>/ws/&lt;name&gt;</c>. 0 = unlimited (not
        /// recommended). Excess invocations queue on the semaphore — they are
        /// not dropped.
        /// </summary>
        public int MaxConcurrentWebsocketScripts { get; set; } = 4;

        /// <summary>
        /// Maximum number of hotkey-triggered scripts running
        /// concurrently from <see cref="HotkeysEnabled"/>'s HotkeyService.
        /// Drives ScriptManager's own hotkey semaphore rather than the shared
        /// webhook one, so a flurry of held / repeating chord fires can't
        /// starve on_webhook / on_websocket delivery. Default 5 matches the
        /// webhook cap; 0 = unlimited (not recommended). Excess invocations
        /// queue on the semaphore for up to 30s, then drop with a
        /// CriticalError log entry.
        /// </summary>
        public int MaxConcurrentHotkeyScripts { get; set; } = 5;

        /// <summary>
        /// Maximum number of clipboard-triggered scripts running
        /// concurrently from <see cref="ClipboardWatchEnabled"/>'s
        /// ClipboardService. Drives its own ScriptManager semaphore for the
        /// same reason as <see cref="MaxConcurrentHotkeyScripts"/>: a fast
        /// copy-paste loop (paste-detection panels, instant-share-link
        /// scripts) would otherwise wedge the shared webhook semaphore. The
        /// OS gives no per-handler discriminator for a clipboard update, so
        /// every on_clipboard subscriber fans out in parallel and this cap
        /// bounds the whole fan-out rather than a single script. Default 5
        /// matches the webhook cap; 0 = unlimited. Excess invocations queue on
        /// the semaphore for up to 30s, then drop with a CriticalError log
        /// entry.
        /// </summary>
        public int MaxConcurrentClipboardScripts { get; set; } = 5;

        /// <summary>
        /// Maximum entries retained in Hub's SystemLog panel buffer. The panel
        /// uses a virtualised ListView so the cap can comfortably exceed the
        /// upstream GlobalLogger ring (2000) without paint cost; default 10000
        /// preserves ~10 minutes of history at a sustained 16 logs/sec.
        /// Values &lt;= 0 clamp to the 10000 default.
        /// </summary>
        public int SystemLogMaxRows { get; set; } = 10000;

        /// <summary>
        /// Maximum messages retained in Hub's Chat panel buffer. The
        /// "X / N" count text in the panel header is derived from this value, so
        /// changing it here propagates to both the trim-oldest behaviour and the
        /// indicator string (which used to hard-code "/ 2000" in two places).
        /// Values &lt;= 0 clamp back to the 2000 default at the consumer.
        /// </summary>
        public int ChatMaxRows { get; set; } = 2000;

        /// <summary>
        /// Persisted LiveFeed filter chips. Each entry
        /// is the string name of a <c>LiveFeedFilter</c> enum value (e.g.
        /// "All", "Subs", "Raids", "Visual", "Redeem", "Follow", "Errors").
        /// The panel restores the active chip set on startup; an empty list
        /// means "default — All". String-keyed (rather than the int enum
        /// value) so a future enum reorder doesn't silently re-map persisted
        /// state.
        /// </summary>
        public List<string> LiveFeedActiveChips { get; set; } = new();

        /// <summary>
        /// Persisted SystemLog level chips. Entries
        /// are string names of <c>SystemLogLevel</c> enum values (e.g. "Debug",
        /// "Info", "Warn", "Error"). Empty list means "use default" — i.e.
        /// Info+Warn+Error visible, Debug off, matching the panel's first-run
        /// chip state.
        /// </summary>
        public List<string> SystemLogActiveLevels { get; set; } = new();

        /// <summary>
        /// Persisted SystemLog source filter. Null /
        /// empty means "no source filter" (every source surfaces); a non-empty
        /// string narrows the panel to rows whose <c>Source</c> matches
        /// (case-insensitive, exact). Set via the SystemLog row right-click
        /// "Filter to source" menu.
        /// </summary>
        public string? SystemLogSourceFilter { get; set; } = null;

        // ── Architect ─────────────────────────────────────────────────────
        /// <summary>Milliseconds to wait before retrying a lost Bus connection in Architect.</summary>
        public int ArchitectReconnectBackoffMs { get; set; } = 5000;

        // Architect UX state persistence.
        // Each field is restored on MainView.Loaded (after CliBootstrap.HandleCli)
        // and written back when the user mutates the corresponding affordance.
        // 0 / "" means "no recall yet — use the layout default".

        /// <summary>
        /// RETIRED 2026-08-14 — kept only so an existing config round-trips
        /// without losing the key. Nothing reads it. It held the inspector
        /// card's height cap, written solely by an InspectorThumb drag handler
        /// that was deleted on 2026-05-24; after that every layout flush
        /// re-persisted whatever the restore had just applied, so a legacy
        /// value could never age out and there was no affordance to change it.
        /// The card is capped from the live pane height now
        /// (MainView.ApplyInspectorCardHeightCap).
        /// </summary>
        public double ArchitectInspectorHeight { get; set; } = 0;

        /// <summary>Width of the LeftRail column in MainView (px). 0 = use XAML default 220.</summary>
        public double ArchitectRailColumnWidth { get; set; } = 0;

        /// <summary>
        /// RETIRED 2026-08-14 — see <see cref="ArchitectInspectorHeight"/>.
        /// Same echo-write shape: the splitter that wrote it went in the same
        /// 2026-05-24 commit. The inspector card's width is a markup constant
        /// now. Kept for config round-trip only; nothing reads it.
        /// </summary>
        public double ArchitectInspectorColumnWidth { get; set; } = 0;

        /// <summary>Last-active pillar tab ("logic" / "databank"). Empty = default to Logic.</summary>
        public string ArchitectLastActiveTab { get; set; } = "";

        /// <summary>Architect View → Live Debug Trace persisted state.</summary>
        public bool ArchitectDebugTraceEnabled { get; set; } = false;

        /// <summary>Architect View → Show Grid persisted state.</summary>
        public bool ArchitectShowGrid { get; set; } = true;

        /// <summary>
        /// Architect View → Minimap persisted state. Toggle for
        /// the bottom-right compact overview navigator (200×140 DIP panel)
        /// that mirrors NodeViewModel + FrameViewModel positions and lets the
        /// user click / drag-pan to jump the viewport. Default true so the
        /// minimap is visible on first launch; flipping the menu / corner-dock
        /// button persists here so the visibility survives restarts.
        /// </summary>
        public bool ArchitectMinimapVisible { get; set; } = true;

        // LeftRail collapse + floating Inspector window state.
        // Both surfaces persist across restarts so a user who likes a tight
        // canvas-first layout doesn't have to re-collapse on every relaunch.

        /// <summary>
        /// LeftRail collapsed to a 32 px strip (chevron-toggled).
        /// Default false = full 220 px rail. Persisted so the toggle survives restarts.
        /// </summary>
        public bool ArchitectRailCollapsed { get; set; } = false;

        /// <summary>
        /// Architect right-side Inspector card visibility. 0.11.5
        /// flipped the default from false → true (Majo's "Inspector must be
        /// active as default" feedback). The toggle moved from the LeftRail
        /// header onto the inspector card's own chevron header so the
        /// roll-up/roll-down affordance lives where the user expects.
        /// </summary>
        public bool ArchitectInspectorVisible { get; set; } = true;

        /// <summary>
        /// 0.11.x polish — one-shot migration flag. Pre-polish the inspector
        /// lived in a process-wide floating <c>InspectorWindow</c> singleton;
        /// users who closed that window persisted
        /// <see cref="ArchitectInspectorVisible"/> = false, which now would
        /// hide the new docked inspector by default. This flag flips true
        /// once the docked-card surface has been seeded; the migration code
        /// in MainView / ArchitectSiblingWindow / SubGraphWindow forces the
        /// inspector open on the first launch where this is false, then
        /// sets it true. Subsequent launches respect whatever the user has
        /// chosen via the chevron.
        /// </summary>
        public bool ArchitectInspectorDockedMigrated { get; set; } = false;

        /// <summary>
        /// 2026-08-14 — one-shot flag for the floating-card retirement. When
        /// false, the Architect hosts zero
        /// <see cref="ArchitectInspectorHeight"/> and
        /// <see cref="ArchitectInspectorColumnWidth"/> once and set this true.
        /// Both keys lost their write affordance on 2026-05-24 but kept being
        /// re-persisted by the layout flush, so a profile that predates that
        /// commit carries frozen geometry no in-app control can reach — on the
        /// reporting profile, a 588.8 px height cap that turned the inspector
        /// into a full-height side panel. Clearing them is what makes the
        /// retirement stick; without it the restore path would keep reading
        /// values the new layout no longer honours and the file would stay
        /// misleading.
        /// </summary>
        public bool ArchitectInspectorCardMigrated { get; set; } = false;

        /// <summary>
        /// Architect canvas hotkey cheatsheet expanded state — the bottom-left
        /// overlay that lists context-relevant chords. Default true so first
        /// launch shows the full panel; the chip header toggles it and persists
        /// here via ConfigManager.SaveDeferred so the choice survives restarts.
        /// </summary>
        public bool ArchitectHotkeyCheatsheetExpanded { get; set; } = true;

        /// <summary>
        /// Visualist canvas hotkey cheatsheet expanded state — covers both the
        /// LayerCanvasView and the WidgetGraphCanvas / WidgetEditorView since
        /// the two never display simultaneously. Default true (mirrors
        /// Architect). Persisted via ConfigManager.SaveDeferred.
        /// </summary>
        public bool VisualistHotkeyCheatsheetExpanded { get; set; } = true;

        // ── Closed Captions / Translation ─────────────────────────────────
        /// <summary>Enable Hub's LiveCaptionService UIA hook into Windows 11 LiveCaptions.</summary>
        public bool   LiveCaptionsEnabled { get; set; } = false;

        /// <summary>If true, Hub presses Win+Ctrl+L on startup to launch Windows LiveCaptions if it isn't running.</summary>
        public bool   LiveCaptionsAutoLaunch { get; set; } = false;

        /// <summary>
        /// Privacy gate. When false (default), captions are ONLY broadcast on the
        /// internal bus (Architect dashboards) and are NOT pushed to OBS browser sources
        /// or external HTTP translators. Setting this to true is an explicit opt-in to
        /// have system-audio captions reach connected overlays / third-party endpoints.
        /// </summary>
        public bool   LiveCaptionsBroadcastToOverlays { get; set; } = false;

        /// <summary>
        /// Explicit allowlist of layer ids permitted to receive CAPTION_UPDATE
        /// broadcasts. Empty list combined with LiveCaptionsBroadcastToOverlays=true
        /// means "all active layers" (legacy behavior). Add layer ids to scope captions
        /// to specific overlays (e.g. only the on-screen-captions widget).
        /// </summary>
        public List<string> LiveCaptionsAllowedLayers { get; set; } = new();

        /// <summary>
        /// Translation backend identifier. "passthrough" (default — returns input unchanged),
        /// "http" (calls a user-configured HTTP endpoint per <see cref="TranslationHttpEndpoint"/>).
        /// More backends can be plugged in via the ITranslator interface.
        /// </summary>
        public string TranslationProvider { get; set; } = "passthrough";

        /// <summary>
        /// Request/response shape the "http" translator speaks to
        /// <see cref="TranslationHttpEndpoint"/>. Accepted values:
        ///   "phoenix" (default) — POST {text, target} → {translated}; optional Bearer key.
        ///   "deepl"             — DeepL v2 REST shape (DeepL-Auth-Key header).
        ///   "google"            — Google Cloud Translation v2 shape (key as ?key= query param).
        ///   "libre"             — LibreTranslate shape (api_key in the JSON body).
        /// The value is persisted verbatim; unknown values fall back to "phoenix" at use-time.
        /// </summary>
        public string TranslationProviderShape { get; set; } = "phoenix";

        /// <summary>HTTP endpoint URL for the "http" translator. Receives the JSON body dictated
        /// by <see cref="TranslationProviderShape"/> (default "phoenix": {text, target}).</summary>
        public string TranslationHttpEndpoint { get; set; } = "";

        /// <summary>Optional API key for the "http" translator. How it travels depends on
        /// <see cref="TranslationProviderShape"/>: Bearer header (phoenix), DeepL-Auth-Key
        /// header (deepl), ?key= query parameter (google), api_key body field (libre).</summary>
        [JsonConverter(typeof(DpapiProtectedStringConverter))]
        public string TranslationApiKey { get; set; } = "";

        /// <summary>BCP-47 target language for the Hub-level caption fan-out
        /// (every <c>CAPTION_UPDATE</c> broadcast is translated into this
        /// language before reaching listeners). Per-layer overrides via
        /// <c>Caption.LiveCaption</c> / <c>Text.Translate</c> still apply
        /// client-side; this is only the default. Empty disables the
        /// translation pass entirely.</summary>
        public string CaptionTargetLanguage { get; set; } = "en";

        // ── Scheduler ─────────────────────────────────────────────────────
        /// <summary>Scheduled script entries. Each maps a cron expression or interval to a .phx script name.</summary>
        public List<ScheduleEntry> Schedules { get; set; } = new();

        // ── Webhook & Asset Hardening ─────────────────────────────────────
        /// <summary>Shared secret required in the X-PhoenixControls-Secret header for /webhook/{name} requests.
        /// If null/empty, all requests are accepted (back-compat) and a warning is logged once on first webhook hit.</summary>
        [JsonConverter(typeof(DpapiProtectedStringConverter))]
        public string WebhookSecret { get; set; } = "";

        /// <summary>
        /// Per-endpoint HMAC secret
        /// override map for <c>/webhook/&lt;path&gt;</c>. Key = the path
        /// trailing portion (e.g. <c>"github"</c> for <c>/webhook/github</c>);
        /// value = the secret that the request's <c>X-PhoenixControls-Secret</c>
        /// header must equal. Rotating a per-endpoint secret invalidates only
        /// that integration — the rest keep working, unlike rotating
        /// <see cref="WebhookSecret"/> which churns every connected integration
        /// at once.
        /// <para>
        /// Fallback contract: when a request arrives for <c>/webhook/&lt;path&gt;</c>
        /// and <see cref="WebhookSecrets"/> has no entry for <c>path</c> (or the
        /// entry is empty), <see cref="HUDServer"/> falls back to the legacy
        /// global <see cref="WebhookSecret"/>. This keeps existing integrations
        /// working after upgrade: streamers can opt in to per-endpoint rotation
        /// one webhook at a time without a flag-day cutover.
        /// </para>
        /// <para>
        /// If both the per-endpoint secret and the global <see cref="WebhookSecret"/>
        /// are empty, the request is rejected with 401 (no silent allow). Values
        /// are NOT individually DPAPI-wrapped on disk today — the existing
        /// DPAPI converter operates per-property, not per-dict-entry; a future
        /// pass can swap the value type for a wrapper if at-rest exposure
        /// becomes a concern.
        /// </para>
        /// </summary>
        public Dictionary<string, string> WebhookSecrets { get; set; } = new();

        /// <summary>Maximum bytes accepted for a /webhook/{name} body. Default 1 MiB.</summary>
        public int MaxWebhookBodyBytes { get; set; } = 1024 * 1024;

        /// <summary>Maximum bytes accepted for a /asset/url remote response. Default 5 MiB.</summary>
        public int MaxAssetSizeBytes { get; set; } = 5 * 1024 * 1024;

        // ── URL Image Cache TTL ───────────────────────
        /// <summary>
        /// Time-to-live (in hours) for entries in the Hub's URL image cache.
        /// Used to wire <c>UrlImageCache.Ttl</c> from configuration instead of
        /// the previous reflection-based discovery. Default 24 hours.
        /// </summary>
        public int UrlImageCacheTtlHours { get; set; } = 24;

        // ── Streamer.bot auth + broadcaster guard ───────────────
        /// <summary>
        /// Optional password used by the WS Streamer.bot auth
        /// handshake. Empty string disables the handshake (default — matches
        /// SB's default unauthenticated WS behavior).
        /// </summary>
        [JsonConverter(typeof(DpapiProtectedStringConverter))]
        public string StreamerBotPassword { get; set; } = "";

        /// <summary>
        /// Twitch broadcaster login name used by the WS broadcaster
        /// guard to suppress self-induced follow / channel-point redeem events.
        /// </summary>
        public string BroadcasterUsername { get; set; } = "";

        /// <summary>
        /// Twitch broadcaster numeric user id used by the WS
        /// broadcaster guard. Preferred over <see cref="BroadcasterUsername"/>
        /// when present because user ids are stable across renames.
        /// </summary>
        public string BroadcasterUserId { get; set; } = "";

        /// <summary>
        /// When true (default), drop follow events whose
        /// follower matches the broadcaster (some test-tools and rare SB
        /// scenarios surface a self-follow that would otherwise spam scripts).
        /// </summary>
        public bool SuppressBroadcasterFollow { get; set; } = true;

        /// <summary>
        /// When true (default), drop channel-point redemption
        /// events triggered by the broadcaster account (broadcasters often
        /// test-redeem their own rewards; suppressing keeps scripts honest).
        /// </summary>
        public bool SuppressBroadcasterRedeem { get; set; } = true;

        // ── ViewerServer v2 (Phoenix.Controls.ViewerServer) ────
        /// <summary>
        /// Master toggle for the v2 <c>ViewerServer</c> hosted in
        /// <c>Phoenix.Controls.ViewerServer</c>. Default <c>false</c> — when off,
        /// port <see cref="ViewerServerPort"/> is unbound and the v2 web-bundle
        /// surface area is not exposed. This is the Hub's single Viewer feature
        /// (the legacy RemoteBridge was retired).
        /// </summary>
        public bool ViewerServerEnabled { get; set; } = false;

        /// <summary>
        /// Port for the v2 <see cref="ViewerServer"/>. Default 18090 keeps the
        /// suite contiguous with HUDServer (18080) / Bus (18081) /
        /// WebSocketServer (18083). Matches the default the WebView2 shell in
        /// Phoenix.Controls.Viewer falls back to.
        /// </summary>
        public int ViewerServerPort { get; set; } = 18090;

        /// <summary>
        /// When true, the v2 ViewerServer binds the wildcard <c>+</c> prefix so
        /// other devices on the LAN can reach it; needs a urlacl reservation
        /// or admin rights. Default <c>false</c> — loopback-only
        /// (defence-in-depth).
        /// </summary>
        public bool ViewerServerLan { get; set; } = false;

        /// <summary>
        /// Channel slug exposed in the v2 viewer URL <c>/v/&lt;channel&gt;</c>
        /// and shown in the viewer's top bar. Default <c>"channel"</c> matches
        /// the WebView2 shell's fallback URL. Streamers typically set this to
        /// their Twitch handle.
        /// </summary>
        public string ViewerServerChannel { get; set; } = "channel";

        // ── WebSocket server (external listener for WS.Server nodes) ──
        /// <summary>
        /// Master toggle for the Hub's WebSocketServerService — fires
        /// <c>on_websocket("name")</c> handler blocks when a client message
        /// arrives at <c>/ws/&lt;name&gt;</c>. Default <c>false</c> so a
        /// fresh-install Hub never accidentally accepts external WS clients;
        /// streamers turn it on when they author a panel / dashboard / bridge
        /// that talks to the Hub over a raw WebSocket.
        /// </summary>
        public bool WebSocketServerEnabled { get; set; } = false;

        /// <summary>
        /// Listener bind address for the WebSocket server. Loopback by default
        /// (defence-in-depth).
        /// </summary>
        public string WebSocketServerBindHost { get; set; } = "127.0.0.1";

        /// <summary>
        /// Port for the WebSocket server. Default 18083 keeps the suite
        /// contiguous with HUDServer (18080) / Bus (18081).
        /// </summary>
        public int WebSocketServerPort { get; set; } = 18083;

        /// <summary>
        /// Shared-secret token required as a <c>?token=</c> query
        /// parameter on every <c>/ws/&lt;name&gt;</c> upgrade. Generated on
        /// first launch via <c>RandomNumberGenerator</c> (base64-url, 32 bytes)
        /// when empty; the server short-circuits the upgrade with WebSocket
        /// close 1008 (PolicyViolation) on mismatch so a misconfigured bridge
        /// surfaces a clear failure instead of binding silently. Treated as a
        /// DPAPI-protected secret on disk so a leaked <c>config.json</c> in a
        /// support bundle doesn't hand attackers wholesale script-execution
        /// rights.
        /// </summary>
        [JsonConverter(typeof(DpapiProtectedStringConverter))]
        public string WebSocketServerToken { get; set; } = "";

        /// <summary>
        /// Explicit opt-in for LAN-exposed WebSocket binds. Even
        /// when <see cref="WebSocketServerBindHost"/> is set to a LAN IP /
        /// <c>0.0.0.0</c>, the service refuses to bind unless this flag is
        /// true. Forces a streamer who flips the bind host (perhaps via a
        /// quick edit during a sound-test) to also tick a "yes, I really want
        /// LAN access" checkbox in Settings. Default
        /// false. LAN bind without this flag downgrades to loopback with a
        /// CriticalError log entry.
        /// </summary>
        public bool WebSocketServerLanModeEnabled { get; set; } = false;

        /// <summary>
        /// Master toggle for the Hub's HotkeyService. Off by
        /// default so a fresh-install Hub doesn't claim arbitrary keystrokes
        /// from the OS. Hotkey bindings live in each script's
        /// <c>on_hotkey("Ctrl+Shift+P"):</c> blocks; the service walks every
        /// enabled script's headers at start + on every script-set change
        /// and registers each combo via Win32 RegisterHotKey.
        /// </summary>
        public bool HotkeysEnabled { get; set; } = false;

        /// <summary>
        /// Master toggle for the Hub's ClipboardService. Off by
        /// default for privacy: a fresh-install Hub must not silently observe
        /// the streamer's clipboard contents. Streamers turn it on when a
        /// script with an <c>on_clipboard:</c> block needs to react to copy
        /// events (paste-detection, instant-share-link panels, etc.).
        /// </summary>
        public bool ClipboardWatchEnabled { get; set; } = false;

        /// <summary>
        /// Maximum length (in characters of the decoded text) that
        /// the ClipboardService will forward to <c>on_clipboard:</c> scripts.
        /// A multi-megabyte paste from an IDE / large document otherwise
        /// inflates the script var dictionary and can be sent verbatim to
        /// any third-party endpoint the script hits. Truncated content is
        /// flagged in a System-tier log line so the script author can see
        /// when the cap fired. 0 or negative clamps to the default 4096.
        /// Despite the byte suffix, the cap is measured in characters of the decoded text — not bytes. Renaming the config field would break existing config.json files in the wild.
        /// </summary>
        public int ClipboardMaxLengthBytes { get; set; } = 4096;

        /// <summary>
        /// Cumulative byte cap on a single message aggregated
        /// across continuation frames in <c>WebSocketServerService</c>. A
        /// malicious or buggy client streaming a never-ending fragment
        /// stream would otherwise grow the receive buffer unboundedly. On
        /// overshoot the socket is closed with WebSocketCloseStatus
        /// MessageTooBig (1009) and a Communication-tier log line records
        /// the abort. 0 or negative clamps to the default 1 MiB.
        /// </summary>
        public int WebSocketMaxMessageBytes { get; set; } = 1024 * 1024;

        /// <summary>
        /// When true, route Twitch.ChatMessage events through the
        /// shared <c>IsBroadcasterActor</c> guard so a chat line typed by
        /// the configured broadcaster account is dropped before reaching the
        /// script engine. Default false because chat-based testing is a
        /// common workflow (broadcasters type their own <c>!command</c>s to
        /// verify behavior). Pair with <see cref="SuppressBroadcasterFollow"/>
        /// and <see cref="SuppressBroadcasterRedeem"/> for a uniform self-fire
        /// model across event types.
        /// </summary>
        public bool SuppressBroadcasterChat { get; set; } = false;

        // ── Auto-updater (UpdateChecker / Phoenix.Controls.Updater) ──────
        /// <summary>
        /// When true, Hub kicks off a background <c>UpdateChecker.CheckAsync</c> at
        /// startup so the Settings panel and caption-bar indicator reflect "behind
        /// origin/master" status without the user having to click Check Now.
        /// Set false on machines that should never call out to GitHub on launch.
        /// </summary>
        public bool UpdateCheckOnStartup { get; set; } = true;

        /// <summary>
        /// HTTP timeout (seconds) for GitHub API calls in <c>UpdateChecker</c>.
        /// Default 5 — enough for a single commits/compare round trip on a
        /// healthy connection, short enough that a flaky network can't stall
        /// the Settings UI. 0 means use the HttpClient default (100s) — not
        /// recommended.
        /// </summary>
        public int UpdateCheckTimeoutSeconds { get; set; } = 5;

        // ── UI-hang auto-recovery (UiHangWatchdog / HangRecoveryLauncher) ─
        /// <summary>
        /// When true (default), a CONFIRMED permanent UI-thread freeze (still
        /// unresponsive for <see cref="HangAutoRecoveryStallSeconds"/> total)
        /// triggers an automatic self-relaunch: Hub captures a final diagnostic
        /// dump, spawns a fresh instance, and hard-kills the wedged one. A
        /// restart-loop guard (<c>HangRecoveryLauncher</c>) caps how many
        /// auto-relaunches may happen in a short window so a deterministic
        /// re-freeze can't fork-bomb — past the cap Hub leaves the frozen process
        /// up (with a loud log) for manual intervention. Set false to disable
        /// auto-relaunch and keep a freeze diagnostics-only (the .dmp / .txt
        /// captures still write regardless).
        /// </summary>
        public bool HangAutoRecoveryEnabled { get; set; } = true;

        /// <summary>
        /// Total seconds the UI thread must stay frozen before
        /// <see cref="HangAutoRecoveryEnabled"/> fires the self-relaunch. The
        /// watchdog CONFIRMS a freeze at ~8s; this is the total-freeze deadline,
        /// so 12 (default) relaunches ~4s after confirmation. Observed freezes
        /// never return to usable, so a low value heals faster without
        /// over-firing (a modal/nested loop still pumps the heartbeat and never
        /// trips the watchdog, so this only ever fires on a genuinely wedged UI
        /// thread). Read live (a Settings change applies without a restart) and
        /// clamped in-code to [~9s, 600s] so it always lands after a confirmed
        /// trip.
        /// </summary>
        public int HangAutoRecoveryStallSeconds { get; set; } = 12;

        /// <summary>
        /// OPT-IN deep-diagnostics dump. When true, the FIRST capture of a UI
        /// freeze writes a FULL-MEMORY minidump (<c>ui-hang-fulldump-*.dmp</c>,
        /// multi-GB — heap pages included, so WinDbg can inspect the
        /// DispatcherQueue / XAML-core state the standard stacks-only dump
        /// structurally cannot show). Follow-up captures of the same stall stay
        /// lightweight. Guardrails: only the 2 newest full dumps are retained,
        /// and the write silently falls back to the lightweight dump when the
        /// drive lacks the estimated free space. NOTE: while this dump is being
        /// written the freeze auto-relaunch WAITS for it (up to ~2 min) instead
        /// of killing the process mid-write — armed sessions trade relaunch
        /// speed for the diagnostic. Default false — arm this only while
        /// actively hunting a freeze (e.g. the 2026-07 streaming-PC
        /// Architect-open wedge), then turn it back off. Read live.
        /// </summary>
        public bool HangFullMemoryDump { get; set; } = false;

        // ── First-run UX ─────────────────────────────────────────────────
        /// <summary>
        /// TODO P0 #1 — set to true once the user has dismissed Hub's first-run
        /// WelcomeDialog (sample-graph picker / orientation). Default false so
        /// a fresh install or clean AppData triggers the dialog exactly once.
        /// Surveying-grade flag — we don't reset it on every restart because
        /// pestering returning users with the welcome screen is worse than
        /// missing it on a corner-case clean install.
        /// </summary>
        public bool SeenWelcomeDialog { get; set; } = false;

        /// <summary>
        /// 1.1 test-build cleanup — one-shot migration flag. Earlier 1.1 test
        /// builds could leave Pre-Build tools (Loyalty / Automod / Counters /
        /// Quotes / CustomCommands / Scheduling / UserManagement / Alerts /
        /// SongRequest) persisted as Enabled=true and timers persisted as Running in the DB,
        /// which then reload as "active" after an in-place upgrade. All
        /// Pre-Build tools are opt-in by design — nothing may be active until
        /// the streamer enables it — so the first boot where this is false
        /// force-disables every tool master toggle and pauses every running
        /// timer exactly once (HubBootstrapper), then sets this true.
        /// Subsequent patches never touch the user's choices again.
        /// The tool list above is the authoritative one — it must match
        /// <c>PreBuildOptInMigration.RunAsync</c>, because a tool missing there
        /// stays enabled forever on an upgraded DB.
        /// </summary>
        public bool PreBuildToolsForcedOffMigrated { get; set; } = false;

        /// <summary>
        /// Donation-ingestion cleanup — one-shot migration flag. The Loyalty tip
        /// earn and the Timer's tip seconds-per-unit both shipped ENABLED while
        /// Phoenix subscribed no donation source, so they could never fire and
        /// nobody noticed. Connecting a broker turns them into live points and
        /// live subathon time with no consent, and a model-default flip only
        /// protects a fresh install — an upgraded databank reloads the old
        /// enabled values. The first boot where this is false zeroes those
        /// settings on existing blobs exactly once (HubBootstrapper →
        /// <c>TipDefaultsMigration</c>), then sets this true. Subsequent patches
        /// never touch the streamer's choice again.
        /// </summary>
        public bool TipDefaultsForcedOffMigrated { get; set; } = false;

        /// <summary>
        /// Whether donation events flagged as TEST by their broker are processed.
        /// Default false: a test tip fired from a broker's dashboard (or from
        /// Streamer.bot's Test Trigger UI) must not move real points, extend a
        /// real subathon or fire a real alert. Enable it deliberately while
        /// wiring up a donation chain, then turn it back off.
        /// </summary>
        public bool DonationAcceptTestEvents { get; set; } = false;

        /// <summary>
        /// Highest Terms-of-Service version the user has accepted via the
        /// first-launch consent gate (<c>TermsOfServiceGate</c>). Default 0 means
        /// "never accepted" — a fresh install or clean AppData shows the ToS
        /// pop-up before any Hub service comes up. The gate compares this against
        /// <c>TermsOfServiceGate.CurrentVersion</c>: when the stored value is
        /// lower (fresh install, OR the terms were revised and the constant was
        /// bumped), the pop-up re-appears and the app stays non-functional until
        /// the user clicks Accept — declining closes the app. Persisted the
        /// moment Accept is clicked, so a crash before Hub finishes booting still
        /// counts the acceptance and doesn't re-prompt on the next launch.
        /// </summary>
        public int AcceptedTosVersion { get; set; } = 0;

        // ── OBS WebSocket direct subscription ────────────────
        // Hub opens a direct OBS WS v5 connection (separate from the
        // Streamer.bot DoAction proxy) so scripts can react to OBS state
        // changes via `on_obs("EventType")`. Default OFF — fresh installs
        // don't try to bind to OBS that isn't running.
        /// <summary>
        /// Master toggle for the direct OBS WebSocket v5+ connection in
        /// <c>ObsWebSocketClient</c>. Default false. When true, Hub
        /// constructs + starts the client at boot and fans inbound OBS
        /// events through <c>ScriptManager.DispatchObsEvent</c> (powering
        /// <c>on_obs("EventType")</c> handlers) plus a Bus <c>OBS_EVENT</c>
        /// broadcast for Architect's debug-trace surface.
        /// </summary>
        public bool ObsWebSocketEnabled { get; set; } = false;

        /// <summary>OBS WebSocket server host. Default loopback — OBS Studio's WS server binds <c>127.0.0.1</c> out of the box.</summary>
        public string ObsWebSocketHost { get; set; } = "127.0.0.1";

        /// <summary>OBS WebSocket server port. Default 4455 — OBS WebSocket v5's documented default.</summary>
        public int ObsWebSocketPort { get; set; } = 4455;

        /// <summary>
        /// OBS WebSocket server password (when OBS has authentication
        /// enabled). Empty string disables the auth challenge path. The
        /// handshake SHA256s this with the server-issued salt +
        /// challenge per the OBS WS v5 spec
        /// (authResponse = base64(SHA256(base64(SHA256(password+salt)) +
        /// challenge))). DPAPI-protected at rest (same posture as
        /// <see cref="WebSocketServerToken"/>) so a leaked config.json
        /// in a support bundle doesn't hand attackers wholesale OBS control.
        /// </summary>
        [JsonConverter(typeof(DpapiProtectedStringConverter))]
        public string ObsWebSocketPassword { get; set; } = "";

        // ── Song Request (YouTube) ────────────────────────────────────────
        /// <summary>
        /// OPTIONAL streamer-supplied YouTube Data API v3 key, used by the Song Request
        /// pre-build tool for the two things a bare link cannot answer: resolving a
        /// <c>!sr &lt;search phrase&gt;</c> to a video, and reading a video's title,
        /// length and embeddable flag.
        ///
        /// Empty (default) is a fully supported mode, not a broken one: links and bare
        /// video ids keep working with no key at all, a search politely asks for a link
        /// instead, and the max-duration cap is SKIPPED rather than guessed. Phoenix never
        /// scrapes YouTube as a fallback.
        ///
        /// DPAPI-protected at rest like every other streamer-supplied credential here, and
        /// read lazily per call (<c>ConfigManager.Current.YouTubeDataApiKey</c>) so a
        /// Settings edit takes effect on the very next request without a restart.
        ///
        /// Entered at Settings → Connection (<c>YouTubeDataApiKeyBox</c>). That box is the
        /// ONLY way to set it, and SettingsDialog is hand-wired per field rather than
        /// reflecting over this class — so a credential added here without its own box is
        /// not "defaulted off", it is permanently unreachable, and every capability behind
        /// it becomes dead code behind a switch nothing can flip. That is exactly what
        /// happened to this field before the box existed.
        /// </summary>
        [JsonConverter(typeof(DpapiProtectedStringConverter))]
        public string YouTubeDataApiKey { get; set; } = "";

        /// <summary>
        /// OBS WS v5 EventSubscription bitmask requested in the
        /// Identify (OpCode 1) payload. Default 1023 (0x3FF) covers every
        /// non-high-volume category:
        ///   General(1) | Config(2) | Scenes(4) | Inputs(8) | Transitions(16)
        ///   | Filters(32) | Outputs(64) | SceneItems(128) | MediaInputs(256)
        ///   | Vendors(512). Add 1024 to include InputVolumeMeters (high
        /// frequency — 50Hz volume updates), 2048 for InputActiveStateChanged,
        /// 4096 for InputShowStateChanged, 8192 for SceneItemTransformChanged.
        /// Scope down (e.g. 4 = Scenes-only) when a streamer only cares
        /// about a subset.
        /// </summary>
        public int ObsEventSubscriptionMask { get; set; } = 1023;
    }

    /// <summary>One scheduled trigger entry — maps a cron expression or fixed interval to a script file.</summary>
    public class ScheduleEntry
    {
        /// <summary>Script file name (without .phx extension) to execute on this schedule.</summary>
        public string Name { get; set; } = "";

        /// <summary>Cron expression (5-field, minute precision) e.g. "*/5 * * * *".</summary>
        public string CronExpression { get; set; } = "";

        /// <summary>Fire once at this ISO 8601 datetime. Used by Schedule.RunAt.</summary>
        public string RunAt { get; set; } = "";

        /// <summary>Fire every N seconds. Used by Schedule.Recurring. 0 = disabled.</summary>
        public int IntervalSeconds { get; set; } = 0;

        /// <summary>Optional chat-activity gate for interval schedules (Schedule.Recurring's
        /// second on_interval arg). When &gt; 0, an interval fire is SKIPPED unless at least
        /// this many inbound chat lines (see <c>ChatActivityCounter</c>) arrived since the
        /// previous fire. 0 (default) = no gate — every interval fires. Interval-only;
        /// cron / RunAt ignore it.</summary>
        public int MinChatLines { get; set; } = 0;

        /// <summary>Whether this schedule entry is active.</summary>
        public bool Enabled { get; set; } = true;
    }
}
