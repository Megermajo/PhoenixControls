using System.Collections.Generic;
using Phoenix.Controls.Shared.Localization;

namespace Phoenix.Controls.Hub.Core
{
    public record HubFeature(
        string Title,
        string Category,
        string Description,
        string WhereToFind);

    /// <summary>
    /// Catalogue of every user-facing Hub feature, rendered live by the
    /// Tools → Documentation window (DocumentationWindow, reachable via F1).
    /// Add an entry here when a new window, status indicator, menu item, or
    /// notable background service ships.
    /// </summary>
    public static class HubFeatureRegistry
    {
        private static readonly List<HubFeature> _features = new();

        static HubFeatureRegistry()
        {
            RegisterDefaults();
        }

        public static IReadOnlyList<HubFeature> GetAll() => _features;

        private static void Add(string title, string category, string description, string whereToFind)
            => _features.Add(new HubFeature(title, category, description, whereToFind));

        private static void RegisterDefaults()
        {
            // ─────────────────────────────────────────────────────────────
            // MAIN WINDOW
            // ─────────────────────────────────────────────────────────────
            Add(Localizer.T("hub.feature.connection_status_bar.title"),
                Localizer.T("hub.feature.category.main_window"),
                Localizer.T("hub.feature.connection_status_bar.description"),
                Localizer.T("hub.feature.connection_status_bar.where"));

            Add(Localizer.T("hub.feature.websocket_status_footer.title"),
                Localizer.T("hub.feature.category.main_window"),
                Localizer.T("hub.feature.websocket_status_footer.description"),
                Localizer.T("hub.feature.websocket_status_footer.where"));

            // ─────────────────────────────────────────────────────────────
            // GIVEAWAY (top-bar tab)
            // ─────────────────────────────────────────────────────────────
            Add(Localizer.T("hub.feature.giveaway.title"),
                Localizer.T("hub.feature.category.main_window"),
                Localizer.T("hub.feature.giveaway.description"),
                Localizer.T("hub.feature.giveaway.where"));

            // ─────────────────────────────────────────────────────────────
            // DASHBOARD PANELS
            // ─────────────────────────────────────────────────────────────
            Add(Localizer.T("hub.feature.live_event_feed.title"),
                Localizer.T("hub.feature.category.dashboard_panels"),
                Localizer.T("hub.feature.live_event_feed.description"),
                Localizer.T("hub.feature.live_event_feed.where"));

            Add(Localizer.T("hub.feature.chat_monitor.title"),
                Localizer.T("hub.feature.category.dashboard_panels"),
                Localizer.T("hub.feature.chat_monitor.description"),
                Localizer.T("hub.feature.chat_monitor.where"));

            Add(Localizer.T("hub.feature.script_monitor.title"),
                Localizer.T("hub.feature.category.dashboard_panels"),
                Localizer.T("hub.feature.script_monitor.description"),
                Localizer.T("hub.feature.script_monitor.where"));

            Add(Localizer.T("hub.feature.toggling_panels.title"),
                Localizer.T("hub.feature.category.dashboard_panels"),
                Localizer.T("hub.feature.toggling_panels.description"),
                Localizer.T("hub.feature.toggling_panels.where"));

            // ─────────────────────────────────────────────────────────────
            // TOOLS MENU
            // ─────────────────────────────────────────────────────────────
            Add(Localizer.T("hub.feature.auto_updater.title"),
                Localizer.T("hub.feature.category.tools_menu"),
                Localizer.T("hub.feature.auto_updater.description"),
                Localizer.T("hub.feature.auto_updater.where"));

            Add(Localizer.T("hub.feature.settings.title"),
                Localizer.T("hub.feature.category.tools_menu"),
                Localizer.T("hub.feature.settings.description"),
                Localizer.T("hub.feature.settings.where"));

            Add(Localizer.T("hub.feature.system_log.title"),
                Localizer.T("hub.feature.category.tools_menu"),
                Localizer.T("hub.feature.system_log.description"),
                Localizer.T("hub.feature.system_log.where"));

            // ── Two entries carried over from the Dev merge ──────────────────
            // Both describe surfaces that ARE live on this branch (WebhookPanel,
            // EventLogPanel) but were never documented here, so they close a real
            // gap. Category re-pointed from Dev's hub.feature.category.windows_menu
            // to dashboard_panels: D13 retired the Windows-menu category and D14
            // deleted its lang key, so Dev's value would render as a raw key.
            //
            // Dev's three OTHER entries from this block are deliberately dropped:
            // run_diagnostics_tests and reset_layout were deleted by D13 (no such
            // surface exists on this branch, and D14 removed their lang keys — they
            // would render raw keys for features that never shipped), and
            // toggling_panels is already registered above under dashboard_panels.
            Add(Localizer.T("hub.feature.recent_webhooks.title"),
                Localizer.T("hub.feature.category.dashboard_panels"),
                Localizer.T("hub.feature.recent_webhooks.description"),
                Localizer.T("hub.feature.recent_webhooks.where"));

            Add(Localizer.T("hub.feature.event_log_window.title"),
                Localizer.T("hub.feature.category.dashboard_panels"),
                Localizer.T("hub.feature.event_log_window.description"),
                Localizer.T("hub.feature.event_log_window.where"));


            // ─────────────────────────────────────────────────────────────
            // BEHIND THE SCENES
            // ─────────────────────────────────────────────────────────────
            Add(Localizer.T("hub.feature.hud_overlay_server.title"),
                Localizer.T("hub.feature.category.behind_the_scenes"),
                Localizer.T("hub.feature.hud_overlay_server.description"),
                Localizer.T("hub.feature.hud_overlay_server.where"));

            Add(Localizer.T("hub.feature.viewer_server.title"),
                Localizer.T("hub.feature.category.behind_the_scenes"),
                Localizer.T("hub.feature.viewer_server.description"),
                Localizer.T("hub.feature.viewer_server.where"));

            Add(Localizer.T("hub.feature.scheduler.title"),
                Localizer.T("hub.feature.category.behind_the_scenes"),
                Localizer.T("hub.feature.scheduler.description"),
                Localizer.T("hub.feature.scheduler.where"));

            Add(Localizer.T("hub.feature.logic_watcher.title"),
                Localizer.T("hub.feature.category.behind_the_scenes"),
                Localizer.T("hub.feature.logic_watcher.description"),
                Localizer.T("hub.feature.logic_watcher.where"));

            Add(Localizer.T("hub.feature.script_engine.title"),
                Localizer.T("hub.feature.category.behind_the_scenes"),
                Localizer.T("hub.feature.script_engine.description"),
                Localizer.T("hub.feature.script_engine.where"));

            // ─────────────────────────────────────────────────────────────
            // VISUALIST RUNTIME (Phase 2 / 5 / 7 / 9 — Hub-side)
            // ─────────────────────────────────────────────────────────────
            Add(Localizer.T("hub.feature.layer_runtime.title"),
                Localizer.T("hub.feature.category.behind_the_scenes"),
                Localizer.T("hub.feature.layer_runtime.description"),
                Localizer.T("hub.feature.layer_runtime.where"));

            Add(Localizer.T("hub.feature.layer_watcher.title"),
                Localizer.T("hub.feature.category.behind_the_scenes"),
                Localizer.T("hub.feature.layer_watcher.description"),
                Localizer.T("hub.feature.layer_watcher.where"));

            Add(Localizer.T("hub.feature.layer_registry.title"),
                Localizer.T("hub.feature.category.behind_the_scenes"),
                Localizer.T("hub.feature.layer_registry.description"),
                Localizer.T("hub.feature.layer_registry.where"));

            Add(Localizer.T("hub.feature.hud_routes.title"),
                Localizer.T("hub.feature.category.behind_the_scenes"),
                Localizer.T("hub.feature.hud_routes.description"),
                Localizer.T("hub.feature.hud_routes.where"));

            Add(Localizer.T("hub.feature.url_image_cache.title"),
                Localizer.T("hub.feature.category.behind_the_scenes"),
                Localizer.T("hub.feature.url_image_cache.description"),
                Localizer.T("hub.feature.url_image_cache.where"));

            // ─────────────────────────────────────────────────────────────
            // LIVE CAPTIONS + TRANSLATION
            // ─────────────────────────────────────────────────────────────
            Add(Localizer.T("hub.feature.live_captions.title"),
                Localizer.T("hub.feature.category.behind_the_scenes"),
                Localizer.T("hub.feature.live_captions.description"),
                Localizer.T("hub.feature.live_captions.where"));

            Add(Localizer.T("hub.feature.translation_service.title"),
                Localizer.T("hub.feature.category.behind_the_scenes"),
                Localizer.T("hub.feature.translation_service.description"),
                Localizer.T("hub.feature.translation_service.where"));

            // ─────────────────────────────────────────────────────────────
            // AI PROVIDER SUITE
            // (sprints 79–89: AI.StreamText / GenerateImage / VisionDescribe /
            // WithTools layered on top of the original AI.Prompt + AI.Moderate)
            // ─────────────────────────────────────────────────────────────
            Add(Localizer.T("hub.feature.ai_suite.title"),
                Localizer.T("hub.feature.category.behind_the_scenes"),
                Localizer.T("hub.feature.ai_suite.description"),
                Localizer.T("hub.feature.ai_suite.where"));

            // ─────────────────────────────────────────────────────────────
            // TOOLS MENU — the Documentation window itself (this catalogue)
            // This catalogue is rendered by the WinUI DocumentationWindow
            // (Tools → Documentation), which replaced the retired WinForms
            // documentation form. The full node reference is a SEPARATE
            // surface — opened with F1 on a node or from the Help menu —
            // so the entry names both.
            // ─────────────────────────────────────────────────────────────
            Add(Localizer.T("hub.feature.docs_nodes_tab.title"),
                Localizer.T("hub.feature.category.tools_menu"),
                Localizer.T("hub.feature.docs_nodes_tab.description"),
                Localizer.T("hub.feature.docs_nodes_tab.where"));

            // ─────────────────────────────────────────────────────────────
            // HELP MENU
            // ─────────────────────────────────────────────────────────────
            Add(Localizer.T("hub.feature.readme_changelog.title"),
                Localizer.T("hub.feature.category.help_menu"),
                Localizer.T("hub.feature.readme_changelog.description"),
                Localizer.T("hub.feature.readme_changelog.where"));

            Add(Localizer.T("hub.feature.about.title"),
                Localizer.T("hub.feature.category.help_menu"),
                Localizer.T("hub.feature.about.description"),
                Localizer.T("hub.feature.about.where"));

            // Architect-side onboarding entry point. Surfaces here so streamers
            // browsing Hub's feature catalogue learn the bundled samples exist
            // even before they ever open Architect.
            Add(Localizer.T("hub.feature.sample_graph_picker.title"),
                Localizer.T("hub.feature.category.architect"),
                Localizer.T("hub.feature.sample_graph_picker.description"),
                Localizer.T("hub.feature.sample_graph_picker.where"));
        }
    }
}
