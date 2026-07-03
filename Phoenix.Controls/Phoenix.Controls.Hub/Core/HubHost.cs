namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// T15 retirement seam — process-wide accessors for the HUDServer and
    /// RemoteBridgeServer that ScriptManager / engine command handlers reach
    /// for from a non-UI context. Used to live as static properties on the
    /// WinForms <c>MainForm</c>; with that class deleted in the retirement
    /// commit, the same surface lives here instead.
    ///
    /// The Hub.WinUI bootstrap (<c>HubBootstrapper.BootAsync</c>) constructs
    /// the HUDServer and RemoteBridgeServer once at startup and assigns them
    /// here. Engine code (e.g. <c>ScriptManager.Visual</c>) reads through
    /// <see cref="HUD"/> / <see cref="RemoteBridge"/> to fan out
    /// VISUAL_SET_TEXT / VISUAL_SET_VISIBLE / process-state broadcasts.
    /// </summary>
    public static class HubHost
    {
        /// <summary>The Hub's HUDServer instance — null until the Hub.WinUI
        /// bootstrap finishes step 4. Engine code that fires HUD broadcasts
        /// must null-check before dispatching.</summary>
        public static HUDServer? HUD { get; set; }

        /// <summary>True when <see cref="HUD"/> has been wired by bootstrap.
        /// Cheaper than <c>HUD is not null</c> at hot call sites.</summary>
        public static bool HasHUD => HUD is not null;

        /// <summary>The Hub's RemoteBridgeServer instance — null on builds
        /// where the bridge is disabled or before bootstrap finishes.
        /// ScriptManager.cs uses this to broadcast process-lifecycle state
        /// (running / ended) over the Viewer-facing wire.</summary>
        public static RemoteBridgeServer? RemoteBridge { get; set; }

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
