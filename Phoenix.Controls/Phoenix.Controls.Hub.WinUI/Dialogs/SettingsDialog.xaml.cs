using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Hub.WinUI.Services;

namespace Phoenix.Controls.Hub.WinUI.Dialogs;

// Settings dialog — tabbed editor for the cross-cutting
// AppConfig fields users would otherwise have to hand-edit in
// %AppData%/PhoenixControls/Hub/config.json. Bound to ConfigManager.Current
// on open; PrimaryButtonClick (Save) writes the changes back via
// ConfigManager.Save and reloads the in-memory config so dependent services
// pick up the new values on next access.
//
// Live validation is intentionally minimal — bad inputs roll back to
// defaults rather than blocking the dialog, so the user can never get
// stuck unable to save a typo. Hot-reload is selective: the log-retention
// cap and the translation stack re-apply on Save; language and bot
// connection changes prompt for a relaunch.
public sealed partial class SettingsDialog : UserControl
{
    // Not readonly — ShowCategory re-points it when a deep link arrives
    // before the rail has been built (see the open-or-focus block below).
    private int _initialPivotIndex;

    // Inline numeric validation.
    //
    // The pre-redesign pattern (ParseInt-and-revert silently) hid bad
    // inputs from the user: "I typed 65555 into HudPort, hit Save, and
    // the dialog closed cleanly — but the new port wasn't applied because
    // it parsed as out-of-range and fell back to my previous 18080." The
    // user has no way to find out their edit was discarded.
    //
    // _numericFields drives the live red-border + per-field error message
    // surface. Each entry binds a TextBox to its error TextBlock and a
    // validator predicate. TextChanged re-validates and updates the
    // chrome; Save short-circuits if any entry fails validation.
    private sealed record NumericField(
        TextBox Box,
        TextBlock ErrorText,
        Func<string, (bool ok, string? message)> Validator,
        // Returns the field's last-saved value as text, so an invalid edit can be rolled
        // back to it on Save instead of hard-blocking the whole dialog.
        Func<string> CurrentText);

    private readonly List<NumericField> _numericFields = new();

    // Cached canonical brushes so the border-flip TextChanged path doesn't
    // re-resolve through Application.Current.Resources on every keystroke.
    private Brush? _validBorderBrush;
    private Brush? _invalidBorderBrush;


    public SettingsDialog() : this(0) { }

    /// <summary>
    /// Open Settings on a specific Pivot tab. Used by status-strip dots
    /// so a click on "Streamer.bot disconnected" jumps the
    /// user straight to the Connection tab where the URL/password live.
    /// Out-of-range or negative values clamp to 0 silently — the dialog
    /// should never refuse to open just because a caller passed a bad
    /// index.
    /// </summary>
    public SettingsDialog(int initialPivotIndex)
    {
        _initialPivotIndex = initialPivotIndex < 0 ? 0 : initialPivotIndex;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ApplyLocalizedChrome();
            // Hydrate is the only step that touches user data on disk
            // (ConfigManager.Current) — guard it so a corrupt config can
            // never block the dialog from opening. Errors land in the
            // System Log rather than a modal dialog.
            try
            {
                HydrateFromConfig();
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("SettingsDialog",
                    "Hydrate from config failed — fields may show defaults", ex);
            }
            // Validator registration runs AFTER Hydrate so the initial
            // TextChanged refresh seeds the chrome from the loaded
            // config values (clean state, no spurious red borders).
            RegisterNumericValidators();
            BuildCategoryRail();
        };
    }

    /// <summary>
    /// On open the dialog used to land focus on the Pivot tab header,
    /// not the first input — keyboard users had to Tab past the tab
    /// strip to reach the URL / password fields. This nudges focus to
    /// the first sensible input on the currently-selected Pivot tab.
    /// </summary>
    private void FocusFirstInputOf(int tabIndex)
    {
        try
        {
            // Category id → first interesting input on that panel, so a
            // status-strip deep-link (e.g. Streamer.bot disconnected → Connection)
            // lands focus on the relevant field, not the rail.
            UIElement? target = tabIndex switch
            {
                (int)Tab.Connection   => StreamerBotUrlBox,
                (int)Tab.Updates      => UpdateCheckOnStartupBox,
                (int)Tab.Logic        => ScriptTimeoutBox,
                // Tab.AI's AI fields are commented out (2026-06-24, AI deferred);
                // the tab now hosts only the Discord token, so land focus there.
                (int)Tab.AI           => DiscordBotTokenBox,
                (int)Tab.Remote       => WebSocketServerEnabledBox,
                (int)Tab.Captions     => LiveCaptionsEnabledBox,
                (int)Tab.Features     => HotkeysEnabledBox,
                (int)Tab.Localization => LanguageCombo,
                // Diagnostics — land focus on the log-retention toggle.
                (int)Tab.Diagnostics  => LogRetentionEnabledBox,
                _ => StreamerBotUrlBox,
            };
            target?.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("SettingsDialog", "FocusFirstInputOf", ex);
        }
    }

    /// <summary>
    /// Stable category IDs for deep-links (StatusStripView's dot clicks,
    /// <see cref="OpenOrFocus"/>). The numeric values are historical Pivot
    /// indices and deliberately do NOT match the category rail's display
    /// order — <see cref="BuildCategoryRail"/> re-sorts the rail (day-to-day
    /// first, diagnostics last) and <see cref="ShowCategory"/> resolves a Tab
    /// to its rail position via FindIndex, so no ordering sync is required.
    /// </summary>
    public enum Tab
    {
        Connection   = 0,
        Updates      = 1,
        Logic        = 2,
        AI           = 3,
        Remote       = 4,
        Captions     = 5,
        Features     = 6,
        Localization = 7,
        // Diagnostics tab — log-history retention + future diagnostic
        // affordances (database integrity, log-tier sampling).
        Diagnostics  = 8,
    }

    // ── Single-instance open-or-focus ────────────────────────────────────
    //
    // Settings hydrates every field from ConfigManager.Current at open time
    // and TryCommitAndPersistAsync writes every field back on Save with no
    // per-field dirty check. Two windows open at once is therefore a
    // data-loss bug and not just clutter: the second window saves the values
    // it read at ITS open time, silently reverting whatever the first one
    // just changed. The pre-conversion ContentDialog got that exclusivity
    // for free (WinUI allows one ContentDialog per XamlRoot); the window
    // conversion dropped it and nothing replaced it, so BOTH entry points
    // (Tools → Settings and the status-strip dots) now funnel through here.
    // Same shape as DocumentationWindow.OpenOrFocus — one live window, a
    // second request re-routes and activates it.
    private static Window? s_window;
    private static SettingsDialog? s_view;

    /// <summary>
    /// Opens the single Settings window on <paramref name="tab"/>, or — when
    /// one is already up — re-routes that window to <paramref name="tab"/>
    /// and brings it forward. The deep-link contract is preserved either
    /// way: a status-strip dot always lands on its category.
    /// </summary>
    public static void OpenOrFocus(Tab tab = Tab.Connection)
    {
        try
        {
            if (s_window is not null && s_view is not null)
            {
                s_view.ShowCategory(tab);
                WindowFront.Show(s_window);
                return;
            }

            var view = new SettingsDialog((int)tab);
            var window = PopOutWindowFactory.Create(view, "popout.title.settings", "Settings");
            view.CloseRequested += (_, _) => { try { window.Close(); } catch { /* teardown race */ } };
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(s_window, window)) { s_window = null; s_view = null; }
            };
            s_window = window;
            s_view   = view;
            WindowFront.Show(window);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("SettingsDialog", $"OpenOrFocus({tab})", ex);
            // Belt + braces — if Create/Activate threw partway we must not
            // leave the statics pointing at a window that never came up, or
            // Settings would be unreachable for the rest of the session.
            s_window = null;
            s_view   = null;
        }
    }

    /// <summary>
    /// Re-routes this (already-open) surface to <paramref name="tab"/>.
    /// Called by <see cref="OpenOrFocus"/> when a deep link arrives after
    /// the constructor's seed index has already been consumed.
    /// </summary>
    public void ShowCategory(Tab tab)
    {
        try
        {
            if (_categories.Count == 0)
            {
                // Rail not built yet — Loaded hasn't fired on a just-opened
                // window (rapid double-click). Re-point the seed so
                // BuildCategoryRail lands on the newest request instead of
                // the one that happened to open the window.
                _initialPivotIndex = (int)tab;
                return;
            }
            int idx = _categories.FindIndex(c => c.TabId == tab);
            SelectCategory(idx < 0 ? 0 : idx);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("SettingsDialog", $"ShowCategory({tab})", ex);
        }
    }

    // ── Category rail (replaces the old Pivot header strip) ──────────────
    private sealed record CategoryRow(Tab TabId, ScrollViewer Panel, string Label);
    private readonly List<CategoryRow> _categories = new();
    private readonly List<Button> _railButtons = new();

    private void BuildCategoryRail()
    {
        _categories.Clear();
        _railButtons.Clear();
        RailPanel.Children.Clear();

        void Add(Tab t, ScrollViewer p, string key, string fallback)
            => _categories.Add(new CategoryRow(t, p, Localizer.T(key, fallback)));

        // Re-sorted, logical order — day-to-day first, diagnostics last. The
        // "Remote" panel now reads "Viewer & Relay" (WebSocket relay + the
        // viewer's advanced knobs).
        Add(Tab.Connection,   ConnectionPanel,   "dialog.settings.tab.connection",   "Connection");
        Add(Tab.Logic,        LogicPanel,        "dialog.settings.tab.logic",        "Logic");
        Add(Tab.Captions,     CaptionsPanel,     "dialog.settings.tab.captions",     "Captions");
        Add(Tab.Remote,       RemotePanel,       "dialog.settings.tab.viewer_relay", "Viewer & Relay");
        Add(Tab.AI,           AiPanel,           "dialog.settings.tab.discord",      "Discord");
        Add(Tab.Features,     FeaturesPanel,     "dialog.settings.tab.features",     "Features");
        Add(Tab.Localization, LocalizationPanel, "dialog.settings.tab.localization", "Localization");
        Add(Tab.Updates,      UpdatesPanel,      "dialog.settings.tab.updates",      "Updates");
        Add(Tab.Diagnostics,  DiagnosticsPanel,  "dialog.settings.tab.diagnostics",  "Diagnostics");

        var railFont = new Microsoft.UI.Xaml.Media.FontFamily(
            Application.Current.Resources["SansFont"] as string ?? "Segoe UI");
        foreach (var cat in _categories)
        {
            var btn = new Button
            {
                Content = cat.Label,
                Tag = cat,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Height = 34,
                Padding = new Thickness(12, 0, 12, 0),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                FontFamily = railFont,
                FontSize = 13,
            };
            btn.Click += OnRailButtonClick;
            _railButtons.Add(btn);
            RailPanel.Children.Add(btn);
        }

        int idx = _categories.FindIndex(c => (int)c.TabId == _initialPivotIndex);
        SelectCategory(idx < 0 ? 0 : idx);
    }

    private void OnRailButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is CategoryRow row)
            SelectCategory(_categories.IndexOf(row));
    }

    private void SelectCategory(int index)
    {
        if (index < 0 || index >= _categories.Count) return;
        var sel = _categories[index];
        foreach (var c in _categories)
            c.Panel.Visibility = ReferenceEquals(c, sel) ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < _railButtons.Count; i++)
            StyleRailButton(_railButtons[i], active: i == index);
        FocusFirstInputOf((int)sel.TabId);
    }

    private void StyleRailButton(Button b, bool active)
    {
        if (active)
        {
            b.Background  = ResolveBrushOrFallback("EmberShadowBrush", Microsoft.UI.Colors.Transparent);
            b.BorderBrush = ResolveBrushOrFallback("EmberPrimaryBrush", Microsoft.UI.Colors.Orange);
            b.Foreground  = ResolveBrushOrFallback("CoalPaperBrush", Microsoft.UI.Colors.White);
        }
        else
        {
            b.Background  = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            b.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            b.Foreground  = ResolveBrushOrFallback("CoalBodyTextBrush", Microsoft.UI.Colors.Gray);
        }
    }

    private void ApplyLocalizedChrome()
    {
        // Footer buttons (rail category labels are set in BuildCategoryRail).
        SaveButton.Content   = Localizer.T("dialog.settings.button.save", "Save");
        CancelButton.Content = Localizer.T("dialog.settings.button.cancel", "Cancel");

        // Connection tab labels.
        StreamerBotUrlLabel.Text       = Localizer.T("dialog.settings.label.streamerbot_url", "STREAMER.BOT URL");
        StreamerBotPasswordLabel.Text  = Localizer.T("dialog.settings.label.streamerbot_password", "STREAMER.BOT PASSWORD");
        BotUsernameLabel.Text          = Localizer.T("dialog.settings.label.bot_username", "BOT USERNAME");
        BroadcasterUsernameLabel.Text  = Localizer.T("dialog.settings.label.broadcaster_username", "BROADCASTER USERNAME");
        BroadcasterUserIdLabel.Text    = Localizer.T("dialog.settings.label.broadcaster_user_id", "BROADCASTER USER ID");
        SuppressBroadcasterFollowBox.Content = Localizer.T(
            "dialog.settings.checkbox.suppress_broadcaster_follow",
            "Suppress self-follow events from the broadcaster account");
        SuppressBroadcasterRedeemBox.Content = Localizer.T(
            "dialog.settings.checkbox.suppress_broadcaster_redeem",
            "Suppress channel-point redemptions triggered by the broadcaster");
        // ApplyLocalizedChrome for the
        // SuppressBroadcasterChat / WebSocket LAN+Token / ViewerServer rows.
        SuppressBroadcasterChatBox.Content = Localizer.T(
            "dialog.settings.checkbox.suppress_broadcaster_chat",
            "Suppress chat lines typed by the broadcaster account");
        SharedChatGuestsTriggerBox.Content = Localizer.T(
            "dialog.settings.checkbox.shared_chat_guests_trigger",
            "Allow shared-chat guest messages to trigger commands, tools & scripts");
        ChatActionLabel.Text           = Localizer.T("dialog.settings.label.streamerbot_chat_action", "STREAMER.BOT CHAT ACTION");
        AutoStartBox.Content           = Localizer.T(
            "dialog.settings.checkbox.auto_start",
            "Auto-connect to Streamer.bot on launch");
        YouTubeDataApiKeyLabel.Text    = Localizer.T(
            "dialog.settings.label.youtube_data_api_key",
            "YOUTUBE DATA API KEY (SONG REQUESTS)");
        HudPortLabel.Text              = Localizer.T("dialog.settings.label.hud_server_port", "HUD SERVER PORT");
        LayoutDirLabel.Text            = Localizer.T("dialog.settings.label.layout_directory", "LAYOUT DIRECTORY (Visualist)");

        // Updates tab.
        UpdateCheckOnStartupBox.Content = Localizer.T("dialog.settings.checkbox.update_check_on_startup", "Check for updates on startup");
        UpdateTimeoutLabel.Text         = Localizer.T("dialog.settings.label.update_check_timeout", "UPDATE CHECK TIMEOUT (seconds)");
        LatestStatusLabel.Text          = Localizer.T("dialog.settings.label.latest_status", "LATEST STATUS");
        UpdateStatusText.Text           = Localizer.T("dialog.settings.placeholder.update_status",
                                                     "No check yet — close Settings and pick Tools → Check for Updates.");
        ForceDownloadHeaderLabel.Text   = Localizer.T("dialog.settings.label.force_download_header", "FORCE DOWNLOAD MASTER RELEASE");
        ForceDownloadIntroText.Text     = Localizer.T("dialog.settings.label.force_download_intro",
                                                     "Bypasses the version check and pulls the latest published release zip from GitHub regardless of the local version stamp. Useful when running a Dev build with the same version stamp as master and you want to flip back to master content. The current install root will be replaced; a timestamped backup is kept.");
        ForceDownloadButton.Content     = Localizer.T("dialog.settings.button.force_download", "Download & install latest release");

        // Logic tab labels.
        LogicDirLabel.Text                  = Localizer.T("dialog.settings.label.logic_directory", "LOGIC DIRECTORY");
        ScriptTimeoutLabel.Text             = Localizer.T("dialog.settings.label.script_timeout", "SCRIPT TIMEOUT (seconds, 0 = unlimited)");
        MaxChatLabel.Text                   = Localizer.T("dialog.settings.label.max_concurrent_chat_scripts", "MAX CONCURRENT CHAT SCRIPTS");
        MaxWebhookLabel.Text                = Localizer.T("dialog.settings.label.max_concurrent_webhook_scripts", "MAX CONCURRENT WEBHOOK SCRIPTS");
        // Global WebhookSecret was
        // relabeled to "fallback" so the UI conveys precedence with the new
        // per-endpoint WebhookSecrets list below.
        WebhookSecretLabel.Text             = Localizer.T("dialog.settings.label.webhook_secret", "DEFAULT WEBHOOK SECRET (FALLBACK)");
        WebhookSecretsHeaderLabel.Text      = Localizer.T(
            "dialog.settings.label.webhook_secrets_header",
            "PER-WEBHOOK SECRETS");
        WebhookSecretsIntroText.Text        = Localizer.T(
            "dialog.settings.label.webhook_secrets_intro",
            "Per-endpoint HMAC secrets — keyed by the trailing path segment (e.g. \"github\" for /webhook/github). Set per row to rotate one integration without churning every other connected webhook. Empty rows fall back to the default secret above. If both are empty, the endpoint is rejected with 401.");
        WebhookSecretsAddButton.Content     = Localizer.T(
            "dialog.settings.button.webhook_secrets_add", "Add…");
        WebhookSecretsEditButton.Content    = Localizer.T(
            "dialog.settings.button.webhook_secrets_edit", "Edit…");
        WebhookSecretsRemoveButton.Content  = Localizer.T(
            "dialog.settings.button.webhook_secrets_remove", "Remove…");
        MaxWebhookBodyBytesLabel.Text       = Localizer.T("dialog.settings.label.max_webhook_body_bytes", "MAX WEBHOOK BODY BYTES");
        MaxAssetSizeBytesLabel.Text         = Localizer.T("dialog.settings.label.max_asset_size_bytes", "MAX ASSET SIZE BYTES");
        UrlImageCacheTtlLabel.Text          = Localizer.T("dialog.settings.label.url_image_cache_ttl", "URL IMAGE CACHE TTL (hours)");
        ArchitectReconnectBackoffLabel.Text = Localizer.T("dialog.settings.label.architect_reconnect_backoff", "ARCHITECT RECONNECT BACKOFF (ms)");
        SystemLogMaxRowsLabel.Text          = Localizer.T(
            "dialog.settings.label.system_log_max_rows",
            "SYSTEM LOG MAX ROWS");

        // AI tab labels — commented out 2026-06-24 (AI deferred; controls
        // commented out in SettingsDialog.xaml). Restore alongside the XAML.
        // DefaultAiModelLabel.Text = Localizer.T("dialog.settings.label.default_ai_model", "DEFAULT AI MODEL");
        // OpenAiApiKeyLabel.Text   = Localizer.T("dialog.settings.label.openai_api_key", "OPENAI API KEY");
        // AnthropicKeyLabel.Text   = Localizer.T("dialog.settings.label.anthropic_api_key", "ANTHROPIC API KEY");
        // CerebrasApiKeyLabel.Text = Localizer.T("dialog.settings.label.cerebras_api_key", "CEREBRAS API KEY");
        // OllamaUrlLabel.Text      = Localizer.T("dialog.settings.label.ollama_url", "OLLAMA URL");
        DiscordBotTokenLabel.Text = Localizer.T(
            "dialog.settings.label.discord_bot_token",
            "DISCORD BOT TOKEN");

        // Remote tab.
        WebSocketServerEnabledBox.Content   = Localizer.T("dialog.settings.checkbox.websocket_relay", "WebSocket relay enabled");
        WebSocketServerBindHostLabel.Text   = Localizer.T("dialog.settings.label.websocket_bind_host", "WEBSOCKET BIND HOST");
        WebSocketServerPortLabel.Text       = Localizer.T("dialog.settings.label.websocket_port", "WEBSOCKET PORT");
        // ApplyLocalizedChrome for the
        // SuppressBroadcasterChat / WebSocket LAN+Token / ViewerServer rows.
        WebSocketServerLanModeEnabledBox.Content = Localizer.T(
            "dialog.settings.checkbox.websocket_lan_mode",
            "WebSocket relay LAN mode (binds the configured host instead of loopback)");
        // Token is fully internal — generated at ConfigManager.Load,
        // never surfaced to the user. The Regenerate button is a panic
        // affordance that mints a fresh value and persists same-turn.
        WebSocketServerTokenLabel.Text = Localizer.T(
            "dialog.settings.label.websocket_token",
            "WEBSOCKET RELAY TOKEN (auto-generated)");
        WebSocketServerTokenRotateButton.Content = Localizer.T(
            "dialog.settings.button.websocket_token_rotate",
            "Regenerate token");
        WebSocketServerTokenHintText.Text = Localizer.T(
            "dialog.settings.hint.websocket_token",
            "Click Rotate to clear the token; a new one is generated automatically on next Hub launch. Treat this value like a password — it grants script-execution rights to anyone who can reach the WebSocket port.");

        ViewerServerEnabledBox.Content = Localizer.T(
            "dialog.settings.checkbox.viewer_server_enabled",
            "Viewer server v2 enabled (separate from the legacy Remote bridge)");
        ViewerServerPortLabel.Text = Localizer.T(
            "dialog.settings.label.viewer_server_port",
            "VIEWER SERVER PORT");
        ViewerServerLanBox.Content = Localizer.T(
            "dialog.settings.checkbox.viewer_server_lan",
            "Viewer server LAN mode (wildcard bind, urlacl reservation required)");
        ViewerServerChannelLabel.Text = Localizer.T(
            "dialog.settings.label.viewer_server_channel",
            "VIEWER CHANNEL SLUG (exposed in /v/<channel> URL)");

        // Captions tab.
        LiveCaptionsEnabledBox.Content      = Localizer.T("dialog.settings.checkbox.live_captions_enabled", "Live captions enabled");
        LiveCaptionsAutoLaunchBox.Content   = Localizer.T("dialog.settings.checkbox.live_captions_auto_launch", "Auto-launch captions on Hub start");
        LiveCaptionsBroadcastBox.Content    = Localizer.T("dialog.settings.checkbox.live_captions_broadcast", "Broadcast captions to overlays");
        LiveCaptionsAllowedLayersLabel.Text = Localizer.T("dialog.settings.label.live_captions_allowed_layers", "ALLOWED LAYERS (comma-separated layer ids; empty = all)");
        TranslationProviderLabel.Text       = Localizer.T("dialog.settings.label.translation_provider", "TRANSLATION PROVIDER (passthrough / http / ...)");
        TranslationProviderShapeLabel.Text  = Localizer.T("dialog.settings.label.translation_provider_shape", "TRANSLATION PROVIDER SHAPE (phoenix / deepl / google / libre)");
        TranslationHttpEndpointLabel.Text   = Localizer.T("dialog.settings.label.translation_http_endpoint", "TRANSLATION HTTP ENDPOINT");
        TranslationApiKeyLabel.Text         = Localizer.T("dialog.settings.label.translation_api_key", "TRANSLATION API KEY");
        CaptionTargetLanguageLabel.Text     = Localizer.T("dialog.settings.label.caption_target_language", "CAPTION TARGET LANGUAGE (BCP-47, e.g. en, de, es)");

        // Features tab.
        HotkeysEnabledBox.Content        = Localizer.T("dialog.settings.checkbox.hotkeys_enabled", "Global hotkeys enabled");
        ClipboardWatchEnabledBox.Content = Localizer.T("dialog.settings.checkbox.clipboard_watch", "Clipboard watch enabled");

        // Localization tab.
        LanguageLabel.Text             = Localizer.T("dialog.settings.label.ui_language", "UI LANGUAGE");
        LanguageRestartNoticeText.Text = Localizer.T("dialog.settings.label.language_restart_notice",
                                                    "Language change takes effect after the next launch.");

        // "↻ requires restart" badge — Style strips this out so each instance
        // can be localized via Localizer.T(). Set on every named badge.
        //
        // Badges 8/9/10/11 cover StreamerBotUrl / LogicDirectory /
        // LayoutDirectory / LiveCaptionsEnabled — all read at service init only.
        string restartBadge = Localizer.T("dialog.settings.badge.requires_restart", "↻ requires restart");
        RequiresRestartBadge1.Text  = restartBadge;
        RequiresRestartBadge2.Text  = restartBadge;
        RequiresRestartBadge3.Text  = restartBadge;
        RequiresRestartBadge5.Text  = restartBadge;
        RequiresRestartBadge6.Text  = restartBadge;
        RequiresRestartBadge7.Text  = restartBadge;
        RequiresRestartBadge8.Text  = restartBadge;
        RequiresRestartBadge9.Text  = restartBadge;
        RequiresRestartBadge10.Text = restartBadge;
        RequiresRestartBadge11.Text = restartBadge;
        // ApplyLocalizedChrome for the WebSocket LAN + ViewerServer rows.
        RequiresRestartBadgeWsLan.Text          = restartBadge;
        RequiresRestartBadgeViewerEnabled.Text  = restartBadge;
        RequiresRestartBadgeViewerLan.Text      = restartBadge;

        // Diagnostics → Log history section. Labels seed once on Loaded; the
        // toggle + day field are hydrated from AppConfig.LogRetentionDays.
        LogHistorySectionLabel.Text = Localizer.T(
            "dialog.settings.diagnostics.log_history_header",
            "LOG HISTORY");
        LogHistoryIntro.Text = Localizer.T(
            "dialog.settings.diagnostics.log_history_intro",
            "Automatically delete old entries from the System Log and Event Log databases so they don't grow without bound. Only those two log tables are pruned — variables and user data are never touched.");
        LogRetentionEnabledBox.Content = Localizer.T(
            "dialog.settings.diagnostics.log_retention_enabled",
            "Automatically delete old log entries");
        LogRetentionDaysLabel.Text = Localizer.T(
            "dialog.settings.diagnostics.log_retention_days_label",
            "DELETE ENTRIES OLDER THAN (days)");

        // Diagnostics → Freeze diagnostics. The wording states the two real costs
        // rather than burying them: the app stays frozen longer, and the dump holds
        // whatever was in memory. Someone turning this on is doing it to chase a
        // specific freeze and should know both before they do.
        FreezeDiagnosticsSectionLabel.Text = Localizer.T(
            "dialog.settings.diagnostics.freeze_header",
            "FREEZE DIAGNOSTICS");
        FreezeDiagnosticsIntro.Text = Localizer.T(
            "dialog.settings.diagnostics.freeze_intro",
            "If the interface ever freezes, Phoenix already saves a small diagnostic snapshot automatically. A full snapshot additionally captures the app's whole memory, which is what makes some freezes identifiable at all. Leave this off unless you are actively investigating one.");
        FullMemoryDumpBox.Content = Localizer.T(
            "dialog.settings.diagnostics.full_memory_dump",
            "Capture a full memory snapshot when the interface freezes");
        FullMemoryDumpWarning.Text = Localizer.T(
            "dialog.settings.diagnostics.full_memory_dump_warning",
            "Two things to know: the freeze lasts longer, because the automatic restart waits for the snapshot to finish writing (up to 2 minutes) instead of restarting after 12 seconds — avoid this while live. And the file contains everything the app held in memory at that moment, including connection details and chat, so treat it as private.");

        // Per-tab "Advanced" grouping (Logic tab; the same pattern extends to
        // other tabs). Rarely-changed knobs collapse under this expander so the
        // panel isn't a wall of fields.
        LogicAdvancedExpander.Header = Localizer.T(
            "dialog.settings.advanced_group", "Advanced (rarely changed)");

        ApplyFieldTooltips();
    }

    // Hover tooltips / small onboarding for the less-obvious fields (Majo:
    // "several settings need a small onboarding and on-hover tooltips").
    private void ApplyFieldTooltips()
    {
        void Tip(DependencyObject el, string key, string fallback)
            => ToolTipService.SetToolTip(el, Localizer.T(key, fallback));

        Tip(StreamerBotUrlBox, "dialog.settings.tip.streamerbot_url",
            "The ws://host:port of your Streamer.bot WebSocket server (default ws://127.0.0.1:8080/). Changing it reconnects on the next launch.");
        Tip(BotUsernameBox, "dialog.settings.tip.bot_username",
            "Your bot account name(s), comma-separated. Chat/events from these accounts are ignored by your scripts so the bot can't trigger itself.");
        Tip(BroadcasterUsernameBox, "dialog.settings.tip.broadcaster_username",
            "Your own Twitch login — used to recognise broadcaster-only events and the self-event guards below.");
        Tip(HudPortBox, "dialog.settings.tip.hud_port",
            "Port for the overlay server OBS connects to (the URL you paste into a Browser Source). Default 18080.");
        Tip(ScriptTimeoutBox, "dialog.settings.tip.script_timeout",
            "Maximum seconds a single script may run before it's cancelled. 0 = unlimited.");
        Tip(MaxChatBox, "dialog.settings.tip.max_chat",
            "How many chat-triggered scripts may run at once. 0 = unlimited.");
        Tip(WebhookSecretBox, "dialog.settings.tip.webhook_secret",
            "Fallback HMAC secret for inbound /webhook/* requests. Per-endpoint secrets below override it.");
        Tip(WebSocketServerEnabledBox, "dialog.settings.tip.websocket_server",
            "Lets external panels/dashboards talk to the Hub over a raw WebSocket (the on_websocket(\"name\") scripts). Off by default.");
        // The three network-exposure fields. LAN mode on either server turns a
        // loopback-only listener into one anything on your network can reach,
        // and the channel slug ends up in a URL you hand to other devices — so
        // each tooltip says plainly what the setting exposes.
        Tip(WebSocketServerLanModeEnabledBox, "dialog.settings.tip.websocket_lan_mode",
            "Off = the relay listens on 127.0.0.1 only (this PC). On = it binds the bind-host above, so any device on your network can reach it — the relay token is then the only thing protecting it. Leave off unless you actually connect from another machine.");
        Tip(ViewerServerPortBox, "dialog.settings.tip.viewer_port",
            "Port the Viewer (second-screen) server binds.");
        Tip(ViewerServerLanBox, "dialog.settings.tip.viewer_lan",
            "Off = the Viewer is reachable from this PC only. On = wildcard bind, so your phone/tablet on the same network can open it — needs a urlacl reservation (run Hub as admin once) and means anyone on that network who knows the URL and PIN can pair. Don't enable on public Wi-Fi.");
        Tip(ViewerServerChannelBox, "dialog.settings.tip.viewer_channel",
            "The slug in your Viewer URL (http://<host>:<port>/v/<channel>). It is not a secret — anyone who can reach the server can guess it, so pairing still goes through the PIN.");
        Tip(LiveCaptionsEnabledBox, "dialog.settings.tip.live_captions",
            "Turns on the Windows live-caption bridge so spoken audio can be shown/translated on an overlay.");
        Tip(TranslationProviderBox, "dialog.settings.tip.translation_provider",
            "passthrough = no translation; http = call an external translation endpoint (configured below).");
        Tip(LogRetentionDaysBox, "dialog.settings.tip.log_retention",
            "Automatically delete System Log / Event Log rows older than this many days. Only those two log tables are pruned.");
        Tip(DiscordBotTokenBox, "dialog.settings.tip.discord_token",
            "Bot token used by discord.send_message / discord.send_embed script nodes.");
        Tip(YouTubeDataApiKeyBox, "dialog.settings.tip.youtube_data_api_key",
            "Optional, free from the Google Cloud console (enable \"YouTube Data API v3\"). Only the Song Request tool uses it: without a key, !sr still accepts YouTube links and video ids, but searching by name is refused, titles show the raw video id, and the max-duration cap is skipped because nothing can read a video's length.");
    }

    private void HydrateFromConfig()
    {
        var cfg = ConfigManager.Current;

        // ── Connection
        StreamerBotUrlBox.Text   = cfg.StreamerBotUrl ?? "";
        StreamerBotPasswordBox.Password = cfg.StreamerBotPassword ?? "";
        BotUsernameBox.Text      = cfg.BotUsername ?? "";
        BroadcasterUsernameBox.Text = cfg.BroadcasterUsername ?? "";
        BroadcasterUserIdBox.Text   = cfg.BroadcasterUserId ?? "";
        SuppressBroadcasterFollowBox.IsChecked = cfg.SuppressBroadcasterFollow;
        SuppressBroadcasterChatBox.IsChecked   = cfg.SuppressBroadcasterChat;
        SuppressBroadcasterRedeemBox.IsChecked = cfg.SuppressBroadcasterRedeem;
        SharedChatGuestsTriggerBox.IsChecked   = cfg.SharedChatGuestsCanTrigger;
        ChatActionBox.Text       = cfg.StreamerBotChatAction ?? "";
        AutoStartBox.IsChecked   = cfg.AutoStart;
        YouTubeDataApiKeyBox.Password = cfg.YouTubeDataApiKey ?? "";
        HudPortBox.Text          = cfg.HUDServerPort.ToString(CultureInfo.InvariantCulture);
        LayoutDirBox.Text        = cfg.LayoutDirectory ?? "";

        UpdateCheckOnStartupBox.IsChecked = cfg.UpdateCheckOnStartup;
        UpdateTimeoutBox.Text             = cfg.UpdateCheckTimeoutSeconds.ToString(CultureInfo.InvariantCulture);

        // ── Logic
        LogicDirBox.Text         = cfg.LogicDirectory ?? "";
        ScriptTimeoutBox.Text    = cfg.ScriptTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        MaxChatBox.Text          = cfg.MaxConcurrentChatScripts.ToString(CultureInfo.InvariantCulture);
        MaxWebhookBox.Text       = cfg.MaxConcurrentWebhookScripts.ToString(CultureInfo.InvariantCulture);
        WebhookSecretBox.Password = cfg.WebhookSecret ?? "";
        // Hydrate the per-endpoint secrets list. The backing collection
        // is mutated in place so the ListView's bound rows reflect Add / Edit /
        // Remove without forcing a rebind round-trip.
        HydrateWebhookSecrets(cfg);
        MaxWebhookBodyBytesBox.Text = cfg.MaxWebhookBodyBytes.ToString(CultureInfo.InvariantCulture);
        MaxAssetSizeBytesBox.Text   = cfg.MaxAssetSizeBytes.ToString(CultureInfo.InvariantCulture);
        UrlImageCacheTtlBox.Text    = cfg.UrlImageCacheTtlHours.ToString(CultureInfo.InvariantCulture);
        ArchitectReconnectBackoffBox.Text = cfg.ArchitectReconnectBackoffMs.ToString(CultureInfo.InvariantCulture);
        SystemLogMaxRowsBox.Text          = cfg.SystemLogMaxRows.ToString(CultureInfo.InvariantCulture);

        // ── Diagnostics → log history (retention cap; 0 = disabled). The toggle
        // shows on whenever a positive cap is set; the day field seeds with the
        // live cap, or the 30-day default when retention is off so there's a
        // sensible value to re-enable from.
        LogRetentionEnabledBox.IsChecked = cfg.LogRetentionDays > 0;
        LogRetentionDaysBox.Text = (cfg.LogRetentionDays > 0 ? cfg.LogRetentionDays : 30)
            .ToString(CultureInfo.InvariantCulture);

        // Freeze diagnostics — a straight round-trip of the flag. The watchdog
        // live-reads it, so flipping it takes effect on the next freeze with no
        // restart, and turning it back off is equally immediate.
        FullMemoryDumpBox.IsChecked = cfg.HangFullMemoryDump;

        // ── AI (fields commented out 2026-06-24 — AI deferred. The cfg values
        //    are left untouched so any previously-saved key survives a round-trip.)
        // DefaultAiModelBox.Text   = cfg.DefaultAIModel ?? "";
        // OpenAiApiKeyBox.Password = cfg.OpenAIApiKey ?? "";
        // AnthropicKeyBox.Password = cfg.AnthropicKey ?? "";
        // CerebrasApiKeyBox.Password = cfg.CerebrasApiKey ?? "";
        // OllamaUrlBox.Text        = cfg.OllamaUrl ?? "";
        DiscordBotTokenBox.Password = cfg.DiscordBotToken ?? "";

        // ── Remote / WebSocket relay
        WebSocketServerEnabledBox.IsChecked = cfg.WebSocketServerEnabled;
        WebSocketServerBindHostBox.Text  = cfg.WebSocketServerBindHost ?? "";
        WebSocketServerPortBox.Text      = cfg.WebSocketServerPort.ToString(CultureInfo.InvariantCulture);
        // The LAN gate was write-only — committed in TryCommitAndPersistAsync but
        // never hydrated here, so the box rendered unchecked on every open and the
        // next Save wrote false back over the user's opt-in, silently downgrading
        // the relay bind to loopback (WebSocketServerService reads this flag to
        // decide loopback-vs-BindHost). Loads with the rest of its group now.
        WebSocketServerLanModeEnabledBox.IsChecked = cfg.WebSocketServerLanModeEnabled;

        // ViewerServer v2 group. ViewerServerPortBox MUST be seeded because
        // RegisterNumericValidators registers it (an empty port box otherwise
        // fails the "Required" rule and hard-blocks Force Download).
        ViewerServerEnabledBox.IsChecked = cfg.ViewerServerEnabled;
        ViewerServerPortBox.Text         = cfg.ViewerServerPort.ToString(CultureInfo.InvariantCulture);
        ViewerServerLanBox.IsChecked     = cfg.ViewerServerLan;
        ViewerServerChannelBox.Text      = cfg.ViewerServerChannel ?? "";

        // ── Captions
        LiveCaptionsEnabledBox.IsChecked    = cfg.LiveCaptionsEnabled;
        LiveCaptionsAutoLaunchBox.IsChecked = cfg.LiveCaptionsAutoLaunch;
        LiveCaptionsBroadcastBox.IsChecked  = cfg.LiveCaptionsBroadcastToOverlays;
        LiveCaptionsAllowedLayersBox.Text   = string.Join(", ", cfg.LiveCaptionsAllowedLayers ?? new System.Collections.Generic.List<string>());
        TranslationProviderBox.Text         = cfg.TranslationProvider ?? "";
        TranslationProviderShapeBox.Text    = cfg.TranslationProviderShape ?? "";
        TranslationHttpEndpointBox.Text     = cfg.TranslationHttpEndpoint ?? "";
        TranslationApiKeyBox.Password       = cfg.TranslationApiKey ?? "";
        CaptionTargetLanguageBox.Text       = cfg.CaptionTargetLanguage ?? "";

        // ── Features
        HotkeysEnabledBox.IsChecked         = cfg.HotkeysEnabled;
        ClipboardWatchEnabledBox.IsChecked  = cfg.ClipboardWatchEnabled;

        // Language — populate from Localizer.Available (set during
        // PillarBootstrap). Each entry is a (code, endonym) pair; the
        // ComboBox displays the endonym while we persist the ISO code. The
        // raw ISO-code fallback ("zh", "ja", etc.) is used when a bundle
        // ships without a hand-mapped endonym so the dropdown never goes
        // blank for a new language.
        LanguageCombo.Items.Clear();
        var available = Localizer.Available.Count == 0
            ? new[] { Localizer.FallbackLanguage }
            : (System.Collections.Generic.IEnumerable<string>)Localizer.Available;
        int selectedIndex = 0;
        int i = 0;
        foreach (var code in available)
        {
            LanguageCombo.Items.Add(new LanguageOption(code, EndonymFor(code)));
            if (string.Equals(code, Localizer.Current, StringComparison.OrdinalIgnoreCase))
                selectedIndex = i;
            i++;
        }
        LanguageCombo.DisplayMemberPath = nameof(LanguageOption.Display);
        LanguageCombo.SelectedIndex = selectedIndex;
    }

    /// <summary>
    /// Hard-coded endonym mapping for the shipped language bundles.
    /// Falls back to the raw ISO code for codes we haven't translated yet —
    /// better to show "zh" than a blank dropdown row, and the user can
    /// still pick it because the combo item still carries the code.
    /// </summary>
    private static string EndonymFor(string code) => (code ?? "").ToLowerInvariant() switch
    {
        "en" => "English",
        "de" => "Deutsch",
        "fr" => "Français",
        "es" => "Español",
        _    => code ?? "",
    };

    /// <summary>
    /// Combo entry for the Localization tab — shows the endonym in the
    /// dropdown but persists the ISO code through <see cref="LanguageConfig.Save"/>.
    /// </summary>
    private sealed record LanguageOption(string Code, string Display);

    /// <summary>
    /// Returns (true, null) on a valid integer in the inclusive range
    /// <paramref name="min"/>..<paramref name="max"/>. Otherwise returns
    /// (false, message). Empty input is rejected — there is no "use
    /// default" semantic from the dialog; callers must type a number.
    /// </summary>
    private static (bool ok, string? message) ValidateIntInRange(string raw, int min, int max)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (false, Localizer.T("dialog.settings.validation.required",
                "Required — enter a number."));

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            return (false, Localizer.T("dialog.settings.validation.not_an_integer",
                "Must be a whole number."));

        if (v < min || v > max)
        {
            string fmt = Localizer.T("dialog.settings.validation.range_format",
                "Must be between {0} and {1}.");
            return (false, string.Format(CultureInfo.InvariantCulture, fmt, min, max));
        }
        return (true, null);
    }

    /// <summary>
    /// Same as <see cref="ValidateIntInRange"/> but for <see cref="long"/>
    /// — used for byte-sized fields where the upper bound exceeds int.MaxValue
    /// (MaxAssetSizeBytes etc.).
    /// </summary>
    private static (bool ok, string? message) ValidateLongInRange(string raw, long min, long max)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (false, Localizer.T("dialog.settings.validation.required",
                "Required — enter a number."));

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            return (false, Localizer.T("dialog.settings.validation.not_an_integer",
                "Must be a whole number."));

        if (v < min || v > max)
        {
            string fmt = Localizer.T("dialog.settings.validation.range_format",
                "Must be between {0} and {1}.");
            return (false, string.Format(CultureInfo.InvariantCulture, fmt, min, max));
        }
        return (true, null);
    }

    private void RegisterNumericValidators()
    {
        _numericFields.Clear();

        // Resolve the canonical valid / invalid border brushes once. The
        // SettingsValueBox style sets BorderBrush to CoalDividerBrush —
        // that's the "valid" target the field reverts to once the user
        // corrects their input.
        var res = Application.Current.Resources;
        _validBorderBrush   = res.TryGetValue("CoalDividerBrush", out var ok) && ok is Brush vb ? vb : null;
        _invalidBorderBrush = res.TryGetValue("ErrBrush",         out var er) && er is Brush eb ? eb : null;

        // Range bounds chosen per AppConfig semantics:
        //   - Ports:          1..65535 (TCP port range; 0 = "any" not allowed
        //                     for the user-facing field).
        //   - Timeouts:       0..86400 (0 = unlimited per the project conventions;
        //                     86400 = 24 hours upper guard).
        // - Concurrency:    0..1000  (AppConfig docs both
        //                     MaxConcurrent*Scripts fields as "0 = unlimited";
        //                     the validator previously forbade 0 and silently
        //                     dropped the documented behavior. Accept 0
        //                     here — the engine handles the semaphore-skip.
        //                     1000 cap prevents typos like "9999999").
        // - UpdateTimeout:  0..600   (AppConfig docs 0 as "use
        //                     HttpClient default (100s)"; accept it.
        //                     600s upper guard.)
        //   - Body / asset:   1..2^30 bytes (1 GiB hard cap).
        //   - Cache TTL:      0..720   (hours; 30 days).
        //   - Backoff:        50..60000 (ms; 50ms floor avoids tight retry
        //                     loops, 60s ceiling matches Architect's
        //                     observed worst-case live-debug stall).
        //   - SystemLogRows:  100..200000 (lower bound = enough scroll-back
        //                     to be useful; upper bound matches the ring's
        //                     virtualisation comfort zone).
        //   - PairingTtl:     30..86400 (seconds; 30s floor stops accidental
        //                     race-only TTLs, 24h upper).
        Register(HudPortBox,          HudPortError,          raw => ValidateIntInRange(raw, 1, 65535),    () => ConfigManager.Current.HUDServerPort.ToString(CultureInfo.InvariantCulture));
        Register(UpdateTimeoutBox,    UpdateTimeoutError,    raw => ValidateIntInRange(raw, 0, 600),      () => ConfigManager.Current.UpdateCheckTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        Register(ScriptTimeoutBox,    ScriptTimeoutError,    raw => ValidateIntInRange(raw, 0, 86400),    () => ConfigManager.Current.ScriptTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        Register(MaxChatBox,          MaxChatError,          raw => ValidateIntInRange(raw, 0, 1000),     () => ConfigManager.Current.MaxConcurrentChatScripts.ToString(CultureInfo.InvariantCulture));
        Register(MaxWebhookBox,       MaxWebhookError,       raw => ValidateIntInRange(raw, 0, 1000),     () => ConfigManager.Current.MaxConcurrentWebhookScripts.ToString(CultureInfo.InvariantCulture));
        Register(MaxWebhookBodyBytesBox, MaxWebhookBodyBytesError, raw => ValidateLongInRange(raw, 1, 1L << 30), () => ConfigManager.Current.MaxWebhookBodyBytes.ToString(CultureInfo.InvariantCulture));
        Register(MaxAssetSizeBytesBox,   MaxAssetSizeBytesError,   raw => ValidateLongInRange(raw, 1, 1L << 30), () => ConfigManager.Current.MaxAssetSizeBytes.ToString(CultureInfo.InvariantCulture));
        Register(UrlImageCacheTtlBox,    UrlImageCacheTtlError,    raw => ValidateIntInRange(raw, 0, 720),  () => ConfigManager.Current.UrlImageCacheTtlHours.ToString(CultureInfo.InvariantCulture));
        Register(ArchitectReconnectBackoffBox, ArchitectReconnectBackoffError, raw => ValidateIntInRange(raw, 50, 60_000), () => ConfigManager.Current.ArchitectReconnectBackoffMs.ToString(CultureInfo.InvariantCulture));
        Register(SystemLogMaxRowsBox,    SystemLogMaxRowsError,    raw => ValidateIntInRange(raw, 100, 200_000), () => ConfigManager.Current.SystemLogMaxRows.ToString(CultureInfo.InvariantCulture));
        // Log-history retention cap (days), 1..3650 (~10y). The checkbox decides
        // whether the cap is applied (off → stored as 0); the field stays
        // numerically valid so an unchecked state never blocks Save.
        Register(LogRetentionDaysBox,    LogRetentionDaysError,    raw => ValidateIntInRange(raw, 1, 3650),      () => { var d = ConfigManager.Current.LogRetentionDays; return (d > 0 ? d : 30).ToString(CultureInfo.InvariantCulture); });
        Register(WebSocketServerPortBox, WebSocketServerPortError, raw => ValidateIntInRange(raw, 1, 65535),    () => ConfigManager.Current.WebSocketServerPort.ToString(CultureInfo.InvariantCulture));
        Register(ViewerServerPortBox,    ViewerServerPortError,    raw => ValidateIntInRange(raw, 1, 65535),    () => ConfigManager.Current.ViewerServerPort.ToString(CultureInfo.InvariantCulture));

        // Seed the chrome from the hydrated config values without
        // spamming validation messages for fields the user never edited.
        // If a config write landed an invalid value, the red surface will
        // appear immediately — the dialog shouldn't hide the corruption.
        foreach (var f in _numericFields) ApplyValidation(f);
    }

    private void Register(TextBox box, TextBlock errorText, Func<string, (bool ok, string? message)> validator, Func<string> currentText)
    {
        var field = new NumericField(box, errorText, validator, currentText);
        _numericFields.Add(field);
        box.TextChanged += (_, _) => ApplyValidation(field);
    }

    private void ApplyValidation(NumericField f)
    {
        (bool ok, string? message) = f.Validator(f.Box.Text ?? string.Empty);
        if (ok)
        {
            if (_validBorderBrush is not null) f.Box.BorderBrush = _validBorderBrush;
            f.ErrorText.Visibility = Visibility.Collapsed;
            f.ErrorText.Text = string.Empty;
        }
        else
        {
            if (_invalidBorderBrush is not null) f.Box.BorderBrush = _invalidBorderBrush;
            f.ErrorText.Text = message ?? string.Empty;
            f.ErrorText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Rolls every numeric field that fails its validator back to the
    /// last-persisted config value (re-validating via TextChanged) so no
    /// out-of-range garbage reaches the commit. This is the ONLY sanction
    /// for invalid numerics — no flow may hard-block on one, because the
    /// fields span several Pivot tabs and a blocked flow can point at a
    /// field the user cannot even see (this bricked Force Download for
    /// every install while the un-hydrated viewer port sat empty).
    /// Returns the number of fields rolled back.
    /// </summary>
    private int RollBackInvalidNumericFields(string consequenceNote)
    {
        var invalidFields = new List<NumericField>();
        foreach (var f in _numericFields)
            if (!f.Validator(f.Box.Text ?? string.Empty).ok) invalidFields.Add(f);
        if (invalidFields.Count == 0) return 0;

        foreach (var f in invalidFields)
            f.Box.Text = f.CurrentText();   // roll back to the persisted value (re-validates via TextChanged)
        foreach (var f in _numericFields) ApplyValidation(f);
        GlobalLogger.Log(
            $"Settings: {invalidFields.Count} numeric field(s) had an invalid value — rolled back to the last saved value; {consequenceNote}.",
            "SettingsDialog", LogLevel.System);
        return invalidFields.Count;
    }

    /// <summary>Raised when the settings surface wants its host window closed
    /// (Save success or Cancel). The launcher wires this to Window.Close().</summary>
    public event EventHandler? CloseRequested;

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        // Roll each invalid numeric field back to its last-saved value (so no
        // out-of-range garbage is persisted), surface the error inline + log it
        // non-blocking, and let Save proceed so the rest of the form still commits.
        RollBackInvalidNumericFields("your other changes were saved");

        if (await TryCommitAndPersistAsync().ConfigureAwait(true))
            CloseRequested?.Invoke(this, EventArgs.Empty);
        // On commit failure, keep the window open so the user can correct their
        // input; TryCommitAndPersistAsync logged the underlying fault.
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(this, EventArgs.Empty);

    // Twitch login rule (the real spec): 4–25 chars, letters / digits / underscore,
    // case-insensitive. Anything matching this is a legal login and must be saveable.
    private static readonly System.Text.RegularExpressions.Regex s_twitchLogin =
        new(@"^[A-Za-z0-9_]{4,25}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Commits a Twitch-login text field WITHOUT blocking the save or rewriting the user's
    /// text. A valid-but-unusual login (e.g. "xX_bot_Xx", "h0tdog99") is saved verbatim. A
    /// non-empty value that fails the Twitch login rule is still saved as typed (no hardlock,
    /// no auto-"correction") but surfaces a non-blocking System-log warning so the user knows
    /// it may not match a real account (GlobalLogger, never a ContentDialog). Empty clears the field.
    /// </summary>
    private static string CommitTwitchLogin(TextBox box, string label)
    {
        string raw = box.Text?.Trim() ?? "";
        if (raw.Length > 0 && !s_twitchLogin.IsMatch(raw))
        {
            GlobalLogger.Log(
                $"Settings: '{label}' = \"{raw}\" is not a valid Twitch login (4–25 chars; letters, digits, underscore). Saved as-is — it may not match a real Twitch account.",
                "SettingsDialog", LogLevel.System);
        }
        return raw;
    }

    /// <summary>
    /// Commits every form field back to <see cref="ConfigManager.Current"/>,
    /// writes the config + language files to disk, and returns true on success.
    /// Extracted from the original inline body of <see cref="OnPrimaryClick"/>
    /// so the Force Download path can persist the user's in-flight edits
    /// before exiting Hub — the legacy path skipped this and silently
    /// discarded any field the streamer hadn't already Saved.
    ///
    /// Async so the OneDrive-backed AppData write doesn't peg
    /// the UI thread; only the disk-write call is thread-pooled, every
    /// in-memory field copy still runs on the UI dispatcher so we can
    /// touch the XAML controls without an Invoke wrapper.
    ///
    /// Caller is responsible for running <see cref="RollBackInvalidNumericFields"/>
    /// up front. Returns false (and logs via GlobalLogger) when an
    /// underlying File.Replace / Save throws, so OnPrimaryClick can cancel
    /// the dialog dismissal.
    /// </summary>
    private async System.Threading.Tasks.Task<bool> TryCommitAndPersistAsync()
    {
        try
        {
            var cfg = ConfigManager.Current;

            // Sanity-check port collisions before the save commits.
            // HUDServerPort / WebSocketServerPort / ViewerServerPort must each
            // be unique; the IPC bus on 18081 is also reserved. Conflicts are
            // logged (not modal) and the dialog still saves the rest of the fields; the user
            // sees the conflict in the System Log and corrects on next open.
            int hudPort       = ParseInt(HudPortBox.Text,             cfg.HUDServerPort);
            int wsServerPort  = ParseInt(WebSocketServerPortBox.Text, cfg.WebSocketServerPort);
            int viewerPort    = ParseInt(ViewerServerPortBox.Text,    cfg.ViewerServerPort);
            WarnIfPortCollision(hudPort, wsServerPort, viewerPort);

            // ── Connection
            cfg.StreamerBotUrl        = StreamerBotUrlBox.Text?.Trim() ?? "";
            cfg.StreamerBotPassword   = StreamerBotPasswordBox.Password ?? "";
            // Bot Username is a COMMA-SEPARATED list of Twitch logins — solo setups run a
            // bot account plus the broadcaster account, and WS.RebuildBlockedAccountsCache
            // (Hub/Core/WS.cs) splits the field on ',' before matching. The per-field
            // a "grammar control" validated it against a SINGLE-login regex
            // (^[A-Za-z0-9_]{4,25}$), so a legitimate multi-account list like
            // "PhoenixControls, StreamElements, Streamlabs" was falsely flagged "not a valid
            // Twitch login" in the System Log. That regression is gone: save the field
            // verbatim (trimmed), no validation, no warning — the original behavior.
            // Broadcaster Username stays single-valued and
            // keeps the soft, non-blocking login check.
            cfg.BotUsername           = BotUsernameBox.Text?.Trim() ?? "";
            cfg.BroadcasterUsername   = CommitTwitchLogin(BroadcasterUsernameBox, "Broadcaster Username");
            cfg.BroadcasterUserId     = BroadcasterUserIdBox.Text?.Trim() ?? "";
            cfg.SuppressBroadcasterFollow = SuppressBroadcasterFollowBox.IsChecked ?? true;
            cfg.SuppressBroadcasterChat   = SuppressBroadcasterChatBox.IsChecked ?? false;
            cfg.SuppressBroadcasterRedeem = SuppressBroadcasterRedeemBox.IsChecked ?? true;
            cfg.SharedChatGuestsCanTrigger = SharedChatGuestsTriggerBox.IsChecked ?? false;
            cfg.StreamerBotChatAction = ChatActionBox.Text?.Trim() ?? "";
            cfg.AutoStart             = AutoStartBox.IsChecked ?? true;
            // Written verbatim, unvalidated and untrimmed-of-nothing-but-nulls: it is a
            // credential, and the resolver already treats a whitespace-only value as
            // "no key" rather than guessing what the streamer meant.
            cfg.YouTubeDataApiKey     = YouTubeDataApiKeyBox.Password ?? "";
            cfg.HUDServerPort         = hudPort;
            cfg.LayoutDirectory       = LayoutDirBox.Text?.Trim() ?? "data/layout";

            cfg.UpdateCheckOnStartup        = UpdateCheckOnStartupBox.IsChecked ?? true;
            cfg.UpdateCheckTimeoutSeconds   = ParseInt(UpdateTimeoutBox.Text, cfg.UpdateCheckTimeoutSeconds);

            // ── Logic
            cfg.LogicDirectory              = LogicDirBox.Text?.Trim() ?? "data/logic";
            cfg.ScriptTimeoutSeconds        = ParseInt(ScriptTimeoutBox.Text, cfg.ScriptTimeoutSeconds);
            cfg.MaxConcurrentChatScripts    = ParseInt(MaxChatBox.Text, cfg.MaxConcurrentChatScripts);
            cfg.MaxConcurrentWebhookScripts = ParseInt(MaxWebhookBox.Text, cfg.MaxConcurrentWebhookScripts);
            cfg.WebhookSecret               = WebhookSecretBox.Password ?? "";
            // Persist the per-endpoint secret overrides. CommitWebhookSecrets
            // rewrites cfg.WebhookSecrets from the in-dialog ObservableCollection
            // so removed rows actually disappear from disk (a naive merge would
            // accumulate orphans).
            CommitWebhookSecrets(cfg);
            cfg.MaxWebhookBodyBytes         = ParseInt(MaxWebhookBodyBytesBox.Text, cfg.MaxWebhookBodyBytes);
            cfg.MaxAssetSizeBytes           = ParseInt(MaxAssetSizeBytesBox.Text, cfg.MaxAssetSizeBytes);
            cfg.UrlImageCacheTtlHours       = ParseInt(UrlImageCacheTtlBox.Text, cfg.UrlImageCacheTtlHours);
            cfg.ArchitectReconnectBackoffMs = ParseInt(ArchitectReconnectBackoffBox.Text, cfg.ArchitectReconnectBackoffMs);
            cfg.SystemLogMaxRows            = ParseInt(SystemLogMaxRowsBox.Text, cfg.SystemLogMaxRows);

            // ── Diagnostics → log history. Checkbox off → 0 (disable the sweep);
            // on → the validated day count. Only EventLog + SystemHistory are ever
            // pruned (see DB.RunRetentionSweepAsync).
            cfg.LogRetentionDays = LogRetentionEnabledBox.IsChecked == true
                ? ParseInt(LogRetentionDaysBox.Text, cfg.LogRetentionDays > 0 ? cfg.LogRetentionDays : 30)
                : 0;

            // ── Diagnostics → freeze diagnostics.
            cfg.HangFullMemoryDump = FullMemoryDumpBox.IsChecked == true;

            // ── AI (fields commented out 2026-06-24 — AI deferred. Not written
            //    back, so cfg keeps whatever was previously persisted.)
            // cfg.DefaultAIModel = DefaultAiModelBox.Text?.Trim() ?? "";
            // cfg.OpenAIApiKey   = OpenAiApiKeyBox.Password ?? "";
            // cfg.AnthropicKey   = AnthropicKeyBox.Password ?? "";
            // cfg.CerebrasApiKey = CerebrasApiKeyBox.Password ?? "";
            // cfg.OllamaUrl      = OllamaUrlBox.Text?.Trim() ?? "";
            cfg.DiscordBotToken = DiscordBotTokenBox.Password ?? "";

            // ── WebSocket relay
            cfg.WebSocketServerEnabled = WebSocketServerEnabledBox.IsChecked ?? false;
            cfg.WebSocketServerBindHost = WebSocketServerBindHostBox.Text?.Trim() ?? "";
            cfg.WebSocketServerPort  = wsServerPort;
            cfg.WebSocketServerLanModeEnabled = WebSocketServerLanModeEnabledBox.IsChecked ?? false;
            // WebSocketServerToken is rotate-only and has no input control at all —
            // the Remote panel carries a label plus the "Regenerate token" button, and
            // the raw value is deliberately never surfaced (see the [S38] block in the
            // XAML), so there is nothing to commit here. Rotation persists same-turn
            // inside ConfigManager.RegenerateWebSocketServerToken; if the value is
            // missing or too short on disk, EnsureWebSocketServerToken mints a fresh
            // one at ConfigManager.Load and at WebSocketServer start.

            // ── ViewerServer v2
            cfg.ViewerServerEnabled = ViewerServerEnabledBox.IsChecked ?? false;
            cfg.ViewerServerPort    = viewerPort;
            cfg.ViewerServerLan     = ViewerServerLanBox.IsChecked ?? false;
            cfg.ViewerServerChannel = ViewerServerChannelBox.Text?.Trim() ?? "channel";

            // ── Captions
            cfg.LiveCaptionsEnabled    = LiveCaptionsEnabledBox.IsChecked ?? false;
            cfg.LiveCaptionsAutoLaunch = LiveCaptionsAutoLaunchBox.IsChecked ?? false;
            cfg.LiveCaptionsBroadcastToOverlays = LiveCaptionsBroadcastBox.IsChecked ?? false;
            cfg.LiveCaptionsAllowedLayers = ParseCsvList(LiveCaptionsAllowedLayersBox.Text);
            cfg.TranslationProvider    = TranslationProviderBox.Text?.Trim() ?? "passthrough";
            cfg.TranslationProviderShape = TranslationProviderShapeBox.Text?.Trim() ?? "phoenix";
            cfg.TranslationHttpEndpoint = TranslationHttpEndpointBox.Text?.Trim() ?? "";
            cfg.TranslationApiKey      = TranslationApiKeyBox.Password ?? "";
            cfg.CaptionTargetLanguage  = CaptionTargetLanguageBox.Text?.Trim() ?? "en";

            // ── Features
            cfg.HotkeysEnabled        = HotkeysEnabledBox.IsChecked ?? false;
            cfg.ClipboardWatchEnabled = ClipboardWatchEnabledBox.IsChecked ?? false;

            await ConfigManager.SaveAsync(Paths.AppConfigJson).ConfigureAwait(true);

            // Persist language choice (LanguageConfig — separate from
            // AppConfig). Combo entries are LanguageOption records carrying both
            // endonym (display) and ISO code (persistence). The legacy
            // string-only path is kept as a forgiveness fallback in case a test
            // rig swaps the item list back to raw codes.
            string? langCode = LanguageCombo.SelectedItem switch
            {
                LanguageOption opt => opt.Code,
                string raw         => raw,
                _                  => null,
            };
            if (!string.IsNullOrWhiteSpace(langCode))
            {
                LanguageConfig.Save(langCode);
            }

            // Apply the log-history cap immediately so the change takes effect
            // without a Hub restart (the boot path in DB.Initialize is the other
            // entry point). Fire-and-forget through AsyncErrorBoundary; the sweep
            // body swallows + logs its own failures and only touches the EventLog
            // + SystemHistory log tables.
            if (cfg.LogRetentionDays > 0)
            {
                _ = Phoenix.Controls.Shared.Core.AsyncErrorBoundary.SafeRunAsync(
                    () => DB.Instance.RunRetentionSweepNowAsync(cfg.LogRetentionDays),
                    "SettingsDialog", "apply log-retention cap");
            }

            // Rebuild the active translator so provider / shape / endpoint / key
            // changes take effect without a Hub restart. Reload disposes the
            // outgoing translator (cancelling its in-flight requests), so guard it:
            // a teardown fault must never break the save path itself.
            try
            {
                Phoenix.Controls.Hub.Core.Translation.TranslationService.Instance.Reload();
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("SettingsDialog", "translation reload failed", ex);
            }

            GlobalLogger.Log("Settings saved.", "SettingsDialog", LogLevel.System);
            return true;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("SettingsDialog", "save failed", ex);
            return false;
        }
    }

    private static int ParseInt(string? text, int fallback)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            return v;
        return fallback;
    }

    /// <summary>
    /// Log a System-tier warning when the user's port choices
    /// collide with each other or with the reserved IPC bus port (18081).
    /// Non-blocking: the save still commits. This kind of
    /// repeatable rejection routes through GlobalLogger, not a ContentDialog.
    /// </summary>
    private const int ReservedBusPort = 18081;
    private static void WarnIfPortCollision(int hudPort, int wsServerPort, int viewerPort)
    {
        // HUD vs IPC bus (always reserved).
        if (hudPort == ReservedBusPort)
            GlobalLogger.Log(
                $"HUD port {hudPort} collides with the IPC bus (reserved). Hub will fail to bind.",
                "SettingsDialog", LogLevel.System);
        // WebSocket relay vs HUD / bus.
        if (wsServerPort == hudPort)
            GlobalLogger.Log(
                $"WebSocket relay port {wsServerPort} collides with HUD port.",
                "SettingsDialog", LogLevel.System);
        if (wsServerPort == ReservedBusPort)
            GlobalLogger.Log(
                $"WebSocket relay port {wsServerPort} collides with the IPC bus (reserved).",
                "SettingsDialog", LogLevel.System);
        // ViewerServer v2 vs HUD / WS relay / bus.
        if (viewerPort == hudPort)
            GlobalLogger.Log(
                $"Viewer server port {viewerPort} collides with HUD port.",
                "SettingsDialog", LogLevel.System);
        if (viewerPort == wsServerPort)
            GlobalLogger.Log(
                $"Viewer server port {viewerPort} collides with the WebSocket relay.",
                "SettingsDialog", LogLevel.System);
        if (viewerPort == ReservedBusPort)
            GlobalLogger.Log(
                $"Viewer server port {viewerPort} collides with the IPC bus (reserved).",
                "SettingsDialog", LogLevel.System);
    }

    /// <summary>
    /// Regenerate the WebSocket relay token — operator-facing "panic
    /// button". Calls <see cref="ConfigManager.RegenerateWebSocketServerToken"/>
    /// which mints a fresh 32-byte base64-url token and persists same-turn,
    /// so existing clients are invalidated on their next WS upgrade
    /// (open sockets stay up until restart of the WebSocketServer).
    /// </summary>
    private void OnWebSocketServerTokenRotateClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            ConfigManager.RegenerateWebSocketServerToken();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("SettingsDialog", "regenerate WebSocket token failed", ex);
        }
    }

    private static System.Collections.Generic.List<string> ParseCsvList(string? text)
    {
        var list = new System.Collections.Generic.List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var raw in text.Split(','))
        {
            var t = raw.Trim();
            if (!string.IsNullOrEmpty(t)) list.Add(t);
        }
        return list;
    }

    /// <summary>
    /// "Force download master release" — bypasses the version-equality short-
    /// circuit in CheckCoreAsync. Useful when running a Dev build that
    /// stamps the same version as the latest master release and you want to
    /// flip back to the master release zip.
    ///
    /// Hands off to <see cref="UpdaterProgressDialog"/> for in-app progress
    /// (the Updater writes a JSON pipe at
    /// <c>%AppData%/PhoenixControls/Hub/updater-progress.json</c>; the dialog
    /// polls it every 250 ms). The Settings dialog itself is dismissed before
    /// we open the progress dialog — two stacked ContentDialogs aren't allowed
    /// in WinUI 3.
    /// </summary>
    private async void OnForceDownloadClicked(object sender, RoutedEventArgs e)
    {
        ForceDownloadButton.IsEnabled = false;
        UpdateStatusText.Text = Localizer.T("dialog.settings.status.querying_github",
                                            "Querying GitHub for the latest release…");

        // Persist any in-flight field edits to disk BEFORE we kick
        // off the force-download exit path. The legacy flow ran Force Download
        // straight into Application.Exit() without touching ConfigManager,
        // silently dropping any edit the user hadn't already Saved (typical
        // case: typed a new bot username, hit Force Download to flip back to
        // master, and lost the username edit after relaunch). Invalid numeric
        // fields roll back to their last-saved values exactly like Save does —
        // the update path must never be held hostage by a bad field, least of
        // all one sitting on a different Pivot tab where the user can't see
        // the red border (the old hard-block here bricked Force Download for
        // every install while the un-hydrated viewer port box sat empty).
        RollBackInvalidNumericFields("force download continues with the saved values");
        if (!await TryCommitAndPersistAsync().ConfigureAwait(true))
        {
            UpdateStatusText.Text = Localizer.T(
                "dialog.settings.status.force_download_save_failed",
                "Force download aborted — failed to persist current settings; see System Log.");
            ForceDownloadButton.IsEnabled = true;
            return;
        }

        UpdateChecker? checker = null;
        UpdateStatus status;
        try
        {
            checker = new UpdateChecker();
            status = await checker.ForceDownloadLatestAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("SettingsDialog", "ForceDownloadLatest", ex);
            UpdateStatusText.Text = string.Format(
                Localizer.T("dialog.settings.update.force_download_error_format", "Force download threw: {0}"),
                ex.Message);
            ForceDownloadButton.IsEnabled = true;
            return;
        }
        finally
        {
            checker?.Dispose();
        }

        switch (status)
        {
            case UpdateStatus.ReleaseAvailable rel:
            {
                UpdateStatusText.Text = string.Format(
                    Localizer.T("dialog.settings.update.release_available_format",
                        "Latest release: {0}\nLocal version: {1}\nAsset: {2}\nSHA-256: {3}\nSpawning Phoenix.Controls.Updater — Hub will close shortly."),
                    rel.RemoteTag, rel.LocalVersion, rel.AssetUrl, rel.AssetSha256);

                // Settings is a window now (not a ContentDialog), so the updater
                // progress ContentDialog can open on this window's XamlRoot with
                // no stacking conflict. Keep the window alive so that XamlRoot
                // stays valid for the brief moment before Hub exits.
                XamlRoot? rootForProgress = this.XamlRoot;

                // Spawn-failure is logged inside the flow (CriticalError);
                // on success Hub is already exiting.
                UpdateApplyFlow.BeginApplyWithProgress(rel, rootForProgress, "SettingsDialog");
                break;
            }
            case UpdateStatus.NetworkError ne:
                UpdateStatusText.Text = string.Format(
                    Localizer.T("dialog.settings.update.force_download_failed_format", "Force download failed: {0}"),
                    ne.Message);
                ForceDownloadButton.IsEnabled = true;
                break;
            default:
                UpdateStatusText.Text = string.Format(
                    Localizer.T("dialog.settings.update.unexpected_status_format", "Unexpected status: {0}"),
                    status.GetType().Name);
                ForceDownloadButton.IsEnabled = true;
                break;
        }
    }


    // ────────────────────────────────────────────────────────────────────
    // Per-webhook secret editor
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// In-dialog row model for the per-webhook secret list. The full
    /// secret stays on the row so Edit can re-open the dialog populated; the
    /// list rendering binds <see cref="MaskedSecret"/> so a streamer's screen
    /// share never leaks the plaintext value. Mutable so the Edit handler
    /// can rewrite the secret in place without rebuilding the row.
    /// </summary>
    private sealed class WebhookSecretRow
    {
        public string Path { get; set; } = "";
        public string Secret { get; set; } = "";

        /// <summary>
        /// Bullet-mask of the secret for ListView display. 8 fixed bullets
        /// rather than a length-matched mask so the rendered width doesn't
        /// telegraph the secret's character count.
        /// </summary>
        public string MaskedSecret => string.IsNullOrEmpty(Secret)
            ? "(empty — falls back to default)"
            : "••••••••";
    }

    private readonly ObservableCollection<WebhookSecretRow> _webhookSecretRows = new();

    private void HydrateWebhookSecrets(AppConfig cfg)
    {
        _webhookSecretRows.Clear();
        var src = cfg.WebhookSecrets;
        if (src is not null)
        {
            // Sort by path so the listing order is stable across reloads —
            // dictionary iteration order is insertion-order in .NET Core+ but
            // streamers expect alphabetical when scanning a list of names.
            var keys = new List<string>(src.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var path in keys)
            {
                _webhookSecretRows.Add(new WebhookSecretRow
                {
                    Path   = path,
                    Secret = src[path] ?? "",
                });
            }
        }
        WebhookSecretsList.ItemsSource = _webhookSecretRows;
    }

    private void CommitWebhookSecrets(AppConfig cfg)
    {
        // Replace the dictionary contents wholesale so deletions in the UI
        // actually drop the underlying entry — a merge-only path would
        // accumulate orphans on disk over time.
        cfg.WebhookSecrets ??= new Dictionary<string, string>();
        cfg.WebhookSecrets.Clear();
        foreach (var row in _webhookSecretRows)
        {
            if (string.IsNullOrWhiteSpace(row.Path)) continue;
            cfg.WebhookSecrets[row.Path.Trim()] = row.Secret ?? "";
        }
    }

    private async void OnWebhookSecretsAddClicked(object sender, RoutedEventArgs e)
    {
        var (ok, pathOut, secretOut) = await PromptForWebhookSecretAsync(
            existingPath:   null,
            existingSecret: null).ConfigureAwait(true);
        if (!ok) return;

        // Reject duplicate paths up front — the dictionary semantics would
        // silently overwrite the previous row, which is exactly the bug per-
        // webhook rotation is meant to prevent. Surface via the System Log
        // rather than a modal dialog.
        foreach (var row in _webhookSecretRows)
        {
            if (string.Equals(row.Path, pathOut, StringComparison.OrdinalIgnoreCase))
            {
                GlobalLogger.Log(
                    $"Webhook secret '{pathOut}' already exists — use Edit… to change its value.",
                    "SettingsDialog", LogLevel.System);
                return;
            }
        }

        _webhookSecretRows.Add(new WebhookSecretRow
        {
            Path   = pathOut,
            Secret = secretOut,
        });
    }

    private async void OnWebhookSecretsEditClicked(object sender, RoutedEventArgs e)
    {
        if (WebhookSecretsList.SelectedItem is not WebhookSecretRow selected)
        {
            GlobalLogger.Log("Edit webhook secret: nothing selected.",
                "SettingsDialog", LogLevel.System);
            return;
        }

        var (ok, pathOut, secretOut) = await PromptForWebhookSecretAsync(
            existingPath:   selected.Path,
            existingSecret: selected.Secret).ConfigureAwait(true);
        if (!ok) return;

        // Renamed to a path already in use? Reject — same reasoning as Add.
        if (!string.Equals(pathOut, selected.Path, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var row in _webhookSecretRows)
            {
                if (ReferenceEquals(row, selected)) continue;
                if (string.Equals(row.Path, pathOut, StringComparison.OrdinalIgnoreCase))
                {
                    GlobalLogger.Log(
                        $"Webhook secret '{pathOut}' already exists — pick a different path.",
                        "SettingsDialog", LogLevel.System);
                    return;
                }
            }
        }

        int idx = _webhookSecretRows.IndexOf(selected);
        if (idx < 0) return;
        _webhookSecretRows[idx] = new WebhookSecretRow
        {
            Path   = pathOut,
            Secret = secretOut,
        };
        WebhookSecretsList.SelectedIndex = idx;
    }

    private async void OnWebhookSecretsRemoveClicked(object sender, RoutedEventArgs e)
    {
        if (WebhookSecretsList.SelectedItem is not WebhookSecretRow selected)
        {
            GlobalLogger.Log("Remove webhook secret: nothing selected.",
                "SettingsDialog", LogLevel.System);
            return;
        }

        var confirm = new ContentDialog
        {
            Title             = Localizer.T(
                "dialog.settings.webhook_secret.remove_title",
                "Remove per-webhook secret"),
            Content           = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Localizer.T(
                    "dialog.settings.webhook_secret.remove_body_format",
                    "Remove the per-endpoint secret for '/webhook/{0}'? Requests to this endpoint will fall back to the default webhook secret."),
                selected.Path),
            PrimaryButtonText = Localizer.T("dialog.settings.webhook_secret.remove_confirm", "Remove"),
            CloseButtonText   = Localizer.T("dialog.settings.webhook_secret.cancel", "Cancel"),
            DefaultButton     = ContentDialogButton.Close,
            XamlRoot          = this.XamlRoot,
            RequestedTheme    = ElementTheme.Dark,
        };
        var result = await confirm.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        _webhookSecretRows.Remove(selected);
    }

    /// <summary>
    /// Safe resource-brush resolution for the runtime-built webhook-secret
    /// dialog. The hard <c>(Brush)Application.Current.Resources[key]</c> casts
    /// throw InvalidCastException if a theme key is missing or carries a
    /// non-Brush value; this mirrors the TryGetValue pattern used for the
    /// numeric-field border brushes (RegisterNumericValidators) and falls back
    /// to a solid colour so the dialog can always render.
    /// </summary>
    private static Brush ResolveBrushOrFallback(string key, global::Windows.UI.Color fallback)
    {
        if (Application.Current?.Resources is { } res
            && res.TryGetValue(key, out var found)
            && found is Brush b)
            return b;
        return new SolidColorBrush(fallback);
    }

    /// <summary>
    /// Shared dialog for Add and Edit — returns the entered (path, secret)
    /// when the user confirms, or (false, "", "") on cancel / invalid input.
    /// The path is validated against the same identifier shape HUDServer
    /// already enforces on inbound webhook names ([A-Za-z0-9_-]+, &lt;=64
    /// chars) so we can never persist a row HUDServer would refuse to match
    /// against. Validation failure logs via GlobalLogger (no modal pop-up)
    /// and reuses the same dialog so the user can correct in-place.
    /// </summary>
    private async System.Threading.Tasks.Task<(bool ok, string path, string secret)>
        PromptForWebhookSecretAsync(string? existingPath, string? existingSecret)
    {
        var pathBox = new TextBox
        {
            Text = existingPath ?? "",
            PlaceholderText = Localizer.T(
                "dialog.settings.webhook_secret.path_placeholder",
                "e.g. github (path segment after /webhook/)"),
            Margin = new Thickness(0, 0, 0, 8),
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(Application.Current.Resources["MonoFont"] as string ?? "Consolas"), // [FONTCAST] MonoFont is an <x:String>; a direct cast throws InvalidCastException
        };
        var secretBox = new PasswordBox
        {
            Password = existingSecret ?? "",
            PasswordRevealMode = PasswordRevealMode.Hidden,
            PlaceholderText = Localizer.T(
                "dialog.settings.webhook_secret.secret_placeholder",
                "Per-endpoint HMAC secret"),
        };
        var errorText = new TextBlock
        {
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(Application.Current.Resources["MonoFont"] as string ?? "Consolas"), // [FONTCAST] MonoFont is an <x:String>; a direct cast throws InvalidCastException
            FontSize = 10,
            Foreground = ResolveBrushOrFallback("ErrBrush", Microsoft.UI.Colors.Red),
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock
        {
            Text = Localizer.T("dialog.settings.webhook_secret.path_label", "WEBHOOK PATH"),
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(Application.Current.Resources["MonoFont"] as string ?? "Consolas"), // [FONTCAST] MonoFont is an <x:String>; a direct cast throws InvalidCastException
            FontSize = 11,
            Foreground = ResolveBrushOrFallback("CoalSecondaryTextBrush", Microsoft.UI.Colors.Gray),
            CharacterSpacing = 80,
            Margin = new Thickness(0, 0, 0, 2),
        });
        stack.Children.Add(pathBox);
        stack.Children.Add(new TextBlock
        {
            Text = Localizer.T("dialog.settings.webhook_secret.secret_label", "SECRET"),
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(Application.Current.Resources["MonoFont"] as string ?? "Consolas"), // [FONTCAST] MonoFont is an <x:String>; a direct cast throws InvalidCastException
            FontSize = 11,
            Foreground = ResolveBrushOrFallback("CoalSecondaryTextBrush", Microsoft.UI.Colors.Gray),
            CharacterSpacing = 80,
            Margin = new Thickness(0, 0, 0, 2),
        });
        stack.Children.Add(secretBox);
        stack.Children.Add(errorText);

        var dlg = new ContentDialog
        {
            Title             = existingPath is null
                ? Localizer.T("dialog.settings.webhook_secret.add_title",  "Add per-webhook secret")
                : Localizer.T("dialog.settings.webhook_secret.edit_title", "Edit per-webhook secret"),
            Content           = stack,
            PrimaryButtonText = Localizer.T("dialog.settings.webhook_secret.confirm", "OK"),
            CloseButtonText   = Localizer.T("dialog.settings.webhook_secret.cancel",  "Cancel"),
            DefaultButton     = ContentDialogButton.Primary,
            XamlRoot          = this.XamlRoot,
            RequestedTheme    = ElementTheme.Dark,
            Background        = ResolveBrushOrFallback("CoalSurfaceBrush", Microsoft.UI.Colors.Gray),
        };

        // Validate inside the PrimaryButtonClick so an invalid path doesn't
        // close the dialog — the user can correct in place.
        dlg.PrimaryButtonClick += (s, args) =>
        {
            string raw = (pathBox.Text ?? "").Trim();
            if (!IsValidWebhookPath(raw))
            {
                errorText.Text = Localizer.T(
                    "dialog.settings.webhook_secret.invalid_path",
                    "Path must be 1–64 chars of letters, digits, dashes or underscores.");
                errorText.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };

        var result = await dlg.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return (false, "", "");

        return (true, (pathBox.Text ?? "").Trim(), secretBox.Password ?? "");
    }

    /// <summary>
    /// Mirrors HUDServer.IsValidWebhookName — the persisted dict key must
    /// match the same identifier shape an inbound /webhook/&lt;name&gt;
    /// request is accepted under, otherwise the override could never fire.
    /// Kept private to the dialog rather than shared with HUDServer so the
    /// WinUI assembly doesn't take a runtime dependency on Hub internals
    /// (the validation is small enough to duplicate).
    /// </summary>
    private static bool IsValidWebhookPath(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > 64) return false;
        foreach (char c in raw)
        {
            bool ok = (c >= 'a' && c <= 'z')
                  || (c >= 'A' && c <= 'Z')
                  || (c >= '0' && c <= '9')
                  ||  c == '_' || c == '-';
            if (!ok) return false;
        }
        return true;
    }
}
