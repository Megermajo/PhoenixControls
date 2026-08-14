namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// T15 retirement seam — process-wide accessor for the HUDServer that
    /// ScriptManager / engine command handlers reach
    /// for from a non-UI context. Used to live as static properties on the
    /// WinForms <c>MainForm</c>; with that class deleted in the retirement
    /// commit, the same surface lives here instead.
    ///
    /// The Hub.WinUI bootstrap (<c>HubBootstrapper.BootAsync</c>) constructs
    /// the HUDServer once at startup and assigns it here. Readers are the UI
    /// surfaces that need the live server object — the WebhookPanel
    /// (<c>OnWebhookFired</c>), the StatusStrip FPS readout, and MainWindow's
    /// shutdown. <b>No engine command handler reads it any more:</b> the three
    /// that did (visual.set_text / set_visible / set_property) were deleted in
    /// V4 part C together with HUDServer's untargeted broadcast methods, and
    /// script-driven overlay data now travels the addressed Overlay Live
    /// Channel instead.
    /// </summary>
    public static class HubHost
    {
        /// <summary>The Hub's HUDServer instance — null until the Hub.WinUI
        /// bootstrap finishes step 4. Every reader must null-check: panels are
        /// constructed before the bootstrap assigns this.</summary>
        public static HUDServer? HUD { get; set; }

        /// <summary>
        /// The Hub's external
        /// WebSocket listener instance (the <c>on_websocket("name"):</c>
        /// surface). Null when AppConfig.WebSocketServerEnabled=false (the
        /// bootstrapper skips construction) or before HubBootstrapper finishes
        /// its opt-in service phase. The Hub StatusStrip reads
        /// <see cref="WebSocketServerService.ConnectedClientCount"/> +
        /// <see cref="WebSocketServerService.IsListening"/> through this
        /// accessor to render its "WS: N / :PORT" badge live.
        /// </summary>
        public static WebSocketServerService? WebSocketServer { get; set; }
    }
}
