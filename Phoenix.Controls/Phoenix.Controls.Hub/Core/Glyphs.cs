namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// Hub's icon glyph table — Segoe Fluent Icons codepoints with semantic
    /// role names. Per Design_Orders.md §8 Hub now joins the per-pillar set
    /// (Architect / Visualist / Hub each own their own paint code and constants
    /// table). Codepoints come from Segoe Fluent Icons (Win10 19041+);
    /// compatible with Segoe MDL2 Assets fallback.
    /// </summary>
    public static class Glyphs
    {
        // ── Identity / brand ─────────────────────────────────────────────────
        public const char PhoenixApp      = (char)0xE945;
        public const char PillarArchitect = (char)0xE70F;
        public const char PillarVisualist = (char)0xE7C5;
        public const char PillarHub       = (char)0xE839;

        // ── Caption-bar buttons ─────────────────────────────────────────────
        public const char CaptionMin     = (char)0xE921;
        public const char CaptionMax     = (char)0xE922;
        public const char CaptionRestore = (char)0xE923;
        public const char CaptionClose   = (char)0xE8BB;

        // ── Universal commands ──────────────────────────────────────────────
        public const char CmdNew      = (char)0xE710;
        public const char CmdOpen     = (char)0xE8E5;
        public const char CmdSave     = (char)0xE74E;
        public const char CmdSettings = (char)0xE713;
        public const char CmdHelp     = (char)0xE897;
        public const char CmdSearch   = (char)0xE721;
        public const char CmdRefresh  = (char)0xE72C;
        public const char CmdUndo     = (char)0xE7A7;
        public const char CmdRedo     = (char)0xE7A6;
        public const char CmdDelete   = (char)0xE74D;
        public const char CmdPin      = (char)0xE840;

        // ── Status / signaling ──────────────────────────────────────────────
        public const char StatusConnected    = (char)0xE73E;
        public const char StatusDisconnected = (char)0xE711;
        public const char StatusWarning      = (char)0xE7BA;
        public const char StatusInfo         = (char)0xE946;
        public const char StatusRunning      = (char)0xE768;

        // ── Playback ────────────────────────────────────────────────────────
        public const char PlayRun         = (char)0xE768;
        public const char PlayPause       = (char)0xE769;
        public const char PlayStop        = (char)0xE71A;
        public const char PlayStepBack    = (char)0xE892;
        public const char PlayStepForward = (char)0xE893;

        // ── Domain glyphs ───────────────────────────────────────────────────
        public const char DomLayer    = (char)0xF22B;
        public const char DomWidget   = (char)0xE713;
        public const char DomTrigger  = (char)0xE945;
        public const char DomMacro    = (char)0xF2B7;
        public const char DomVariable = (char)0xE943;
        public const char DomDatabase = (char)0xEE94;
        public const char DomEvent    = (char)0xE945;
        public const char DomFlow     = (char)0xE76C;
        public const char DomChat     = (char)0xE8BD;

        // ── Media ──────────────────────────────────────────────────────────
        public const char MediaImage  = (char)0xEB9F;
        public const char MediaVideo  = (char)0xE714;
        public const char MediaAudio  = (char)0xE767;
        public const char MediaFolder = (char)0xE8B7;
        public const char MediaFile   = (char)0xE8A5;

        // ── Canvas tools ───────────────────────────────────────────────────
        public const char ToolPan       = (char)0xE8A7;
        public const char ToolZoomFit   = (char)0xE9A6;
        public const char ToolGridSnap  = (char)0xF0E2;
        public const char ToolSelection = (char)0xE7C9;

        // ── Inline Unicode geometry ─────────────────────────────────────────
        public const char Play        = '▶';
        public const char Pause       = '⏸';
        public const char Stop        = '■';
        public const char Diamond     = '◆';
        public const char Dot         = '●';
        public const char Triangle    = '▲';
        public const char DownChevron = '▼';
        public const char Warning     = '⚠';
        public const char Check       = '✓';
        public const char Cross       = '✕';
        public const char Arrow       = '➔';

        public static char ResolveCodepoint(string role) => role switch
        {
            "phoenix_app"          => PhoenixApp,
            "pillar_architect"     => PillarArchitect,
            "pillar_visualist"     => PillarVisualist,
            "pillar_hub"           => PillarHub,
            "caption_minimize"     => CaptionMin,
            "caption_maximize"     => CaptionMax,
            "caption_restore"      => CaptionRestore,
            "caption_close"        => CaptionClose,
            "cmd_new"              => CmdNew,
            "cmd_open"             => CmdOpen,
            "cmd_save"             => CmdSave,
            "cmd_settings"         => CmdSettings,
            "cmd_help"             => CmdHelp,
            "cmd_search"           => CmdSearch,
            "cmd_refresh"          => CmdRefresh,
            "cmd_undo"             => CmdUndo,
            "cmd_redo"             => CmdRedo,
            "cmd_delete"           => CmdDelete,
            "cmd_pin"              => CmdPin,
            "status_connected"     => StatusConnected,
            "status_disconnected"  => StatusDisconnected,
            "status_warning"       => StatusWarning,
            "status_info"          => StatusInfo,
            "status_running"       => StatusRunning,
            "play_run"             => PlayRun,
            "play_pause"           => PlayPause,
            "play_stop"            => PlayStop,
            "play_step_back"       => PlayStepBack,
            "play_step_forward"    => PlayStepForward,
            "dom_layer"            => DomLayer,
            "dom_widget"           => DomWidget,
            "dom_trigger"          => DomTrigger,
            "dom_macro"            => DomMacro,
            "dom_variable"         => DomVariable,
            "dom_database"         => DomDatabase,
            "dom_event"            => DomEvent,
            "dom_flow"             => DomFlow,
            "dom_chat"             => DomChat,
            "media_image"          => MediaImage,
            "media_video"          => MediaVideo,
            "media_audio"          => MediaAudio,
            "media_folder"         => MediaFolder,
            "media_file_generic"   => MediaFile,
            "tool_pan"             => ToolPan,
            "tool_zoom_fit"        => ToolZoomFit,
            "tool_grid_snap"       => ToolGridSnap,
            "tool_selection"       => ToolSelection,
            _                      => '\0',
        };
    }
}
