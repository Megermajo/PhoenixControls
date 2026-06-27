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
        /// Named webhook registry.
        /// Key = friendly name used in scripts (e.g. "discord_alerts").
        /// Value = full webhook URL.
        /// Populated by Architect in a future phase; scripts can reference by name or raw URL.
        /// </summary>
        public Dictionary<string, string> Webhooks { get; set; } = new();

        /// <summary>
        /// P4 — Discord bot token used by <c>discord.send_message</c> /
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

        /// <summary>
        /// QC06-03 — Twitch numeric user id of the bot account, preferred over
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
        /// QC37-02 — per-request <c>max_tokens</c> cap for AI prompt /
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
        ///  — retention window (days) for the unbounded persistence
        /// tables (<c>EventLog</c> / <c>SystemHistory</c>). On every
        /// <see cref="Services.DB.Initialize"/> the rows older than this cap
        /// are deleted in a single DELETE per table. Set to 0 (or negative) to
        /// disable the sweep entirely — useful for forensic captures where
        /// truncating history is unacceptable. Default 30 days keeps the
        /// long-running streamer DB bounded without losing recent activity.
        /// </summary>
        public int LogRetentionDays { get; set; } = 30;

        /// <summary>Maximum number of chat-triggered scripts running concurrently. 0 = unlimited.</summary>
        public int MaxConcurrentChatScripts { get; set; } = 3;

        /// <summary>Maximum number of webhook-triggered scripts running concurrently. 0 = unlimited.</summary>
        public int MaxConcurrentWebhookScripts { get; set; } = 5;

        /// <summary>
        /// Maximum number of event-triggered scripts running concurrently — Twitch
        /// sub/cheer/raid/follow, OBS/YouTube events, internal <c>event.trigger</c>,
        /// on_startup, on_bus, on_state_change, and scheduler fires all draw from this
        /// pool. Previously hard-coded to 5 in ScriptManager (C16 "AppConfig knob to be
        /// added later"); surfaced here so a busy channel that legitimately fans many
        /// events at once isn't throttled into the 30s queue-then-drop cycle. Default 8
        /// (up from the old 5) gives headroom for sub-trains / raid+shoutout bursts while
        /// still bounding a runaway loop. 0 = unlimited (not recommended). Nested
        /// <c>event.trigger</c> calls re-enter the held slot, so they don't count against
        /// this cap. Excess invocations queue on the semaphore (30s) — then drop.
        /// </summary>
        public int MaxConcurrentEventScripts { get; set; } = 8;

        /// <summary>
        /// QC41-02 — maximum number of <c>on_websocket("name")</c>-triggered
        /// scripts running concurrently from the external WebSocket listener.
        /// Mirrors <see cref="MaxConcurrentWebhookScripts"/>; default 4 keeps a
        /// chatty external bridge from spawning unlimited script invocations
        /// when it floods <c>/ws/&lt;name&gt;</c>. 0 = unlimited (not
        /// recommended). Excess invocations queue on the semaphore — they are
        /// not dropped.
        /// </summary>
        public int MaxConcurrentWebsocketScripts { get; set; } = 4;

        /// <summary>
        ///  Maximum number of hotkey-triggered scripts running
        /// concurrently from <see cref="HotkeysEnabled"/>'s HotkeyService.
        /// Split out from the shared default so a flurry of held / repeating
        /// chord registrations can't starve chat/webhook concurrency. Default
        /// 5 matches the webhook cap; 0 = unlimited (not recommended).
        /// Excess invocations queue on the semaphore — they are not dropped.
        /// </summary>
        public int MaxConcurrentHotkeyScripts { get; set; } = 5;

        /// <summary>
        ///  Maximum number of clipboard-triggered scripts running
        /// concurrently from <see cref="ClipboardWatchEnabled"/>'s
        /// ClipboardService. Split out from the shared default for the same
        /// reason as <see cref="MaxConcurrentHotkeyScripts"/>: a fast
        /// copy-paste loop (paste-detection panels, instant-share-link
        /// scripts) can otherwise wedge the shared semaphore. Default 5
        /// matches the webhook cap; 0 = unlimited.
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
        /// QC10-09 — maximum messages retained in Hub's Chat panel buffer. The
        /// "X / N" count text in the panel header is derived from this value, so
        /// changing it here propagates to both the trim-oldest behaviour and the
        /// indicator string (which used to hard-code "/ 2000" in two places).
        /// Values &lt;= 0 clamp back to the 2000 default at the consumer.
        /// </summary>
        public int ChatMaxRows { get; set; } = 2000;

        /// <summary>
        /// C5 (audit 2026-05-24) — persisted LiveFeed filter chips. Each entry
        /// is the string name of a <c>LiveFeedFilter</c> enum value (e.g.
        /// "All", "Subs", "Raids", "Visual", "Redeem", "Follow", "Errors").
        /// The panel restores the active chip set on startup; an empty list
        /// means "default — All". String-keyed (rather than the int enum
        /// value) so a future enum reorder doesn't silently re-map persisted
        /// state.
        /// </summary>
        public List<string> LiveFeedActiveChips { get; set; } = new();

        /// <summary>
        /// C7 (audit 2026-05-24) — persisted SystemLog level chips. Entries
        /// are string names of <c>SystemLogLevel</c> enum values (e.g. "Debug",
        /// "Info", "Warn", "Error"). Empty list means "use default" — i.e.
        /// Info+Warn+Error visible, Debug off, matching the panel's first-run
        /// chip state.
        /// </summary>
        public List<string> SystemLogActiveLevels { get; set; } = new();

        /// <summary>
        /// C7 (audit 2026-05-24) — persisted SystemLog source filter. Null /
        /// empty means "no source filter" (every source surfaces); a non-empty
        /// string narrows the panel to rows whose <c>Source</c> matches
        /// (case-insensitive, exact). Set via the SystemLog row right-click
        /// "Filter to source" menu (B6).
        /// </summary>
        public string? SystemLogSourceFilter { get; set; } = null;

        // ── Architect ─────────────────────────────────────────────────────
        /// <summary>Milliseconds to wait before retrying a lost Bus connection in Architect.</summary>
        public int ArchitectReconnectBackoffMs { get; set; } = 5000;

        // 0.10.0 — Architect UX state persistence (arch-ux-state task #3).
        // Each field is restored on MainView.Loaded (after CliBootstrap.HandleCli)
        // and written back when the user mutates the corresponding affordance.
        // 0 / "" means "no recall yet — use the layout default".

        /// <summary>Last cap of the right-hand inspector card (px). 0 = use default 280.</summary>
        public double ArchitectInspectorHeight { get; set; } = 0;

        /// <summary>Width of the LeftRail column in MainView (px). 0 = use XAML default 220.</summary>
        public double ArchitectRailColumnWidth { get; set; } = 0;

        /// <summary>Width of the Inspector column in MainView (px). 0 = use XAML default 240.</summary>
        public double ArchitectInspectorColumnWidth { get; set; } = 0;

        /// <summary>Last-active pillar tab ("logic" / "databank"). Empty = default to Logic.</summary>
        public string ArchitectLastActiveTab { get; set; } = "";

        /// <summary>Architect View → Live Debug Trace persisted state.</summary>
        public bool ArchitectDebugTraceEnabled { get; set; } = false;

        /// <summary>Architect View → Show Grid persisted state.</summary>
        public bool ArchitectShowGrid { get; set; } = true;

        /// <summary>
        ///  — Architect View → Minimap persisted state. Toggle for
        /// the bottom-right compact overview navigator (200×140 DIP panel)
        /// that mirrors NodeViewModel + FrameViewModel positions and lets the
        /// user click / drag-pan to jump the viewport. Default true so the
        /// minimap is visible on first launch; flipping the menu / corner-dock
        /// button persists here so the visibility survives restarts.
        /// </summary>
        public bool ArchitectMinimapVisible { get; set; } = true;

        //  #10 — LeftRail collapse + floating Inspector window state.
        // Both surfaces persist across restarts so a user who likes a tight
        // canvas-first layout doesn't have to re-collapse on every relaunch.

        /// <summary>
        ///  #10 — LeftRail collapsed to a 32 px strip (chevron-toggled).
        /// Default false = full 220 px rail. Persisted so the toggle survives restarts.
        /// </summary>
        public bool ArchitectRailCollapsed { get; set; } = false;

        /// <summary>
        /// Architect right-side Inspector card visibility. 0.11.5 canvas-polish
        /// r3 flipped the default from false → true (Majo's "Inspector must be
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
        /// H58 — privacy gate. When false (default), captions are ONLY broadcast on the
        /// internal bus (Architect dashboards) and are NOT pushed to OBS browser sources
        /// or external HTTP translators. Setting this to true is an explicit opt-in to
        /// have system-audio captions reach connected overlays / third-party endpoints.
        /// </summary>
        public bool   LiveCaptionsBroadcastToOverlays { get; set; } = false;

        /// <summary>
        /// H58 — explicit allowlist of layer ids permitted to receive CAPTION_UPDATE
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

        /// <summary>HTTP endpoint URL template for the "http" translator. Receives JSON {text, target}.</summary>
        public string TranslationHttpEndpoint { get; set; } = "";

        /// <summary>Optional API key sent as Bearer token for the "http" translator.</summary>
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
        /// C17 (audit/winui-regressions-2026-05-24) — per-endpoint HMAC secret
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

        // ── Sweep 6 follow-up: URL Image Cache TTL ───────────────────────
        /// <summary>
        /// Time-to-live (in hours) for entries in the Hub's URL image cache.
        /// Used to wire <c>UrlImageCache.Ttl</c> from configuration instead of
        /// the previous reflection-based discovery. Default 24 hours.
        /// </summary>
        public int UrlImageCacheTtlHours { get; set; } = 24;

        // ── Sweep 7: Streamer.bot auth + broadcaster guard ───────────────
        /// <summary>
        /// Optional password used by the WS L64 Streamer.bot auth
        /// handshake. Empty string disables the handshake (default — matches
        /// SB's default unauthenticated WS behavior).
        /// </summary>
        [JsonConverter(typeof(DpapiProtectedStringConverter))]
        public string StreamerBotPassword { get; set; } = "";

        /// <summary>
        /// Twitch broadcaster login name used by the WS M31 broadcaster
        /// guard to suppress self-induced follow / channel-point redeem events.
        /// </summary>
        public string BroadcasterUsername { get; set; } = "";

        /// <summary>
        /// Twitch broadcaster numeric user id used by the WS M31
        /// broadcaster guard. Preferred over <see cref="BroadcasterUsername"/>
        /// when present because user ids are stable across renames.
        /// </summary>
        public string BroadcasterUserId { get; set; } = "";

        /// <summary>
        /// WS M31 — when true (default), drop follow events whose
        /// follower matches the broadcaster (some test-tools and rare SB
        /// scenarios surface a self-follow that would otherwise spam scripts).
        /// </summary>
        public bool SuppressBroadcasterFollow { get; set; } = true;

        /// <summary>
        /// WS M31 — when true (default), drop channel-point redemption
        /// events triggered by the broadcaster account (broadcasters often
        /// test-redeem their own rewards; suppressing keeps scripts honest).
        /// </summary>
        public bool SuppressBroadcasterRedeem { get; set; } = true;

        // ── Remote Bridge (Phoenix.Controls.Viewer) ──────────────────────
        /// <summary>
        /// Master toggle for the Hub's RemoteBridgeServer (Viewer-roadmap Slice 0).
        /// Default <c>false</c> — when off, port <see cref="RemotePort"/> is unbound
        /// and no remote-auth surface area is exposed. The Remote Devices panel
        /// flips this at runtime to start/stop the bridge in place.
        /// </summary>
        public bool RemoteEnabled { get; set; } = false;

        /// <summary>
        /// Listener bind address for <see cref="RemoteBridgeServer"/>. Defaults to
        /// loopback so a fresh-install Hub never accidentally exposes itself; LAN
        /// streamers set this to their LAN IP or <c>0.0.0.0</c>.
        /// </summary>
        public string RemoteBindHost { get; set; } = "127.0.0.1";

        /// <summary>
        /// Port for the <see cref="RemoteBridgeServer"/>. Default 18082 keeps the
        /// suite contiguous with HUDServer (18080) and Bus (18081).
        /// </summary>
        public int RemotePort { get; set; } = 18082;

        /// <summary>
        /// PFX/PEM cert path for HTTPS/WSS. Empty string = HTTP/WS. The Hub does
        /// NOT auto-elevate to register a netsh sslcert binding — see Viewer_Roadmap.md
        /// for the one-time admin command.
        /// </summary>
        public string RemoteTlsCertPath { get; set; } = "";

        /// <summary>
        /// Lifetime (seconds) of a pairing-code grant before it expires. Default 5 min.
        /// Single-use plus this TTL is the mitigation against LAN-mode pairing-code
        /// interception per the Viewer roadmap auth section.
        /// </summary>
        public int RemotePairingTtlSeconds { get; set; } = 300;

        /// <summary>
        /// [QC18-S2 P2] When <c>true</c> (default), <see cref="RemoteBridgeServer"/>
        /// refuses to start over plaintext HTTP on a non-loopback bind. Pairing
        /// codes and bearer tokens travel verbatim over this socket, so a LAN-
        /// segment sniffer can grab them outright. Set <see cref="RemoteTlsCertPath"/>
        /// to switch to HTTPS, OR restrict <see cref="RemoteBindHost"/> to loopback,
        /// OR (NOT recommended) flip this to <c>false</c> to allow plaintext-on-LAN.
        /// </summary>
        public bool RemotePairingRequiresHttps { get; set; } = true;

        // ── ViewerServer v2 (Phoenix.Controls.ViewerServer — QC27-01) ────
        /// <summary>
        /// Master toggle for the v2 <c>ViewerServer</c> hosted in
        /// <c>Phoenix.Controls.ViewerServer</c>. Default <c>false</c> — when off,
        /// port <see cref="ViewerServerPort"/> is unbound and the v2 web-bundle
        /// surface area is not exposed. Distinct from <see cref="RemoteEnabled"/>
        /// (the legacy RemoteBridge); a streamer cutting over to v2 flips this
        /// on and turns the legacy bridge off.
        /// </summary>
        public bool ViewerServerEnabled { get; set; } = false;

        /// <summary>
        /// Port for the v2 <see cref="ViewerServer"/>. Default 18090 keeps the
        /// suite contiguous with HUDServer (18080) / Bus (18081) /
        /// RemoteBridge (18082) / WebSocketServer (18083). Matches the default
        /// the WebView2 shell in Phoenix.Controls.Viewer falls back to.
        /// </summary>
        public int ViewerServerPort { get; set; } = 18090;

        /// <summary>
        /// When true, the v2 ViewerServer binds the wildcard <c>+</c> prefix so
        /// other devices on the LAN can reach it; needs a urlacl reservation
        /// or admin rights. Default <c>false</c> — loopback-only, the same
        /// defence-in-depth posture as <see cref="RemoteEnabled"/>.
        /// </summary>
        public bool ViewerServerLan { get; set; } = false;

        /// <summary>
        /// Channel slug exposed in the v2 viewer URL <c>/v/&lt;channel&gt;</c>
        /// and shown in the viewer's top bar. Default <c>"channel"</c> matches
        /// the WebView2 shell's fallback URL. Streamers typically set this to
        /// their Twitch handle.
        /// </summary>
        public string ViewerServerChannel { get; set; } = "channel";

        // ── WebSocket server ( — external listener for WS.Server nodes) ──
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
        /// — same rationale as <see cref="RemoteBindHost"/>.
        /// </summary>
        public string WebSocketServerBindHost { get; set; } = "127.0.0.1";

        /// <summary>
        /// Port for the WebSocket server. Default 18083 keeps the suite
        /// contiguous with HUDServer (18080) / Bus (18081) /
        /// RemoteBridge (18082).
        /// </summary>
        public int WebSocketServerPort { get; set; } = 18083;

        /// <summary>
        /// QC41-02 — shared-secret token required as a <c>?token=</c> query
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
        /// QC41-02 — explicit opt-in for LAN-exposed WebSocket binds. Even
        /// when <see cref="WebSocketServerBindHost"/> is set to a LAN IP /
        /// <c>0.0.0.0</c>, the service refuses to bind unless this flag is
        /// true. Forces a streamer who flips the bind host (perhaps via a
        /// quick edit during a sound-test) to also tick a "yes, I really want
        /// LAN access" checkbox in Settings — same defense-in-depth posture as
        /// <see cref="RemoteBridgeServer"/>'s plaintext-HTTP warning. Default
        /// false. LAN bind without this flag downgrades to loopback with a
        /// CriticalError log entry.
        /// </summary>
        public bool WebSocketServerLanModeEnabled { get; set; } = false;

        /// <summary>
        ///  — master toggle for the Hub's HotkeyService. Off by
        /// default so a fresh-install Hub doesn't claim arbitrary keystrokes
        /// from the OS. Hotkey bindings live in each script's
        /// <c>on_hotkey("Ctrl+Shift+P"):</c> blocks; the service walks every
        /// enabled script's headers at start + on every script-set change
        /// and registers each combo via Win32 RegisterHotKey.
        /// </summary>
        public bool HotkeysEnabled { get; set; } = false;

        /// <summary>
        ///  — master toggle for the Hub's ClipboardService. Off by
        /// default for privacy: a fresh-install Hub must not silently observe
        /// the streamer's clipboard contents. Streamers turn it on when a
        /// script with an <c>on_clipboard:</c> block needs to react to copy
        /// events (paste-detection, instant-share-link panels, etc.).
        /// </summary>
        public bool ClipboardWatchEnabled { get; set; } = false;

        /// <summary>
        /// QC36-04 — maximum length (in characters of the decoded text) that
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
        /// QC36-11 — cumulative byte cap on a single message aggregated
        /// across continuation frames in <c>WebSocketServerService</c>. A
        /// malicious or buggy client streaming a never-ending fragment
        /// stream would otherwise grow the receive buffer unboundedly. On
        /// overshoot the socket is closed with WebSocketCloseStatus
        /// MessageTooBig (1009) and a Communication-tier log line records
        /// the abort. 0 or negative clamps to the default 1 MiB.
        /// </summary>
        public int WebSocketMaxMessageBytes { get; set; } = 1024 * 1024;

        /// <summary>
        /// QC36-07 — when true, route Twitch.ChatMessage events through the
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

        // ── OBS WebSocket direct subscription (audit B38) ────────────────
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
        /// B38 handshake SHA256s this with the server-issued salt +
        /// challenge per the OBS WS v5 spec
        /// (authResponse = base64(SHA256(base64(SHA256(password+salt)) +
        /// challenge))). DPAPI-protected at rest (same posture as
        /// <see cref="WebSocketServerToken"/>) so a leaked config.json
        /// in a support bundle doesn't hand attackers wholesale OBS control.
        /// </summary>
        [JsonConverter(typeof(DpapiProtectedStringConverter))]
        public string ObsWebSocketPassword { get; set; } = "";

        /// <summary>
        /// B38 — OBS WS v5 EventSubscription bitmask requested in the
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

        /// <summary>Whether this schedule entry is active.</summary>
        public bool Enabled { get; set; } = true;
    }
}
