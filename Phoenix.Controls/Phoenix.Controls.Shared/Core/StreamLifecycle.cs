using System;

namespace Phoenix.Controls.Shared.Core
{
    /// <summary>
    /// Single source of truth for the two unified, multi-platform
    /// stream-lifecycle triggers — <c>Stream.GoingLive</c> and
    /// <c>Stream.SessionEnd</c>. Maps the Streamer.bot stream on/off event-type
    /// strings to the platform tokens used in the <c>on_go_live(...)</c> /
    /// <c>on_session_end(...)</c> headers and to the trigger kind. One place —
    /// the Architect exporter (header emission), the ScriptEngine block gate
    /// (<c>ShouldEnterBlock</c>), the ScriptRegistry header scan, and the Hub
    /// dispatch (<c>ScriptManager.ExecuteGenericEventAsync</c>) all derive from
    /// these constants so the token spelling can never drift. Direct sibling of
    /// <see cref="ChatPlatforms"/> (whose Twitch/YouTube/Kick tokens are reused
    /// here as the canonical platform spelling).
    ///
    /// These nodes are ADDITIVE to the existing per-platform stream events
    /// (Kick.StreamOnline, YouTube.BroadcastStarted/Ended, …) which keep their
    /// own <c>on_event(...)</c> dispatch untouched — the unified node just fans
    /// the same underlying events into one checkmark-gated block.
    /// </summary>
    public static class StreamLifecycle
    {
        /// <summary>Which unified trigger an event feeds, or <see cref="None"/>.</summary>
        public enum Kind
        {
            None = 0,
            GoingLive,
            SessionEnd,
        }

        // Header selectors — also the ScriptRegistry scan roots and the engine
        // block-header prefixes. Emitted with an explicit platform list, e.g.
        // "on_go_live(twitch, youtube, kick):".
        public const string GoLiveHeader     = "on_go_live";
        public const string SessionEndHeader = "on_session_end";

        /// <summary>
        /// Internal debounce window (seconds) enforced between two fires of the
        /// SAME node instance. Going live on several platforms at once raises a
        /// burst of on/off events within a few seconds; the cooldown collapses
        /// that burst so each placed node fires exactly once per go-live /
        /// session-end. Baked in (not user-configurable) per the node's design.
        /// </summary>
        public const int CooldownSeconds = 10;

        // The Streamer.bot event-type strings that mean "the broadcaster just
        // went live" / "the session just ended", per platform. All six already
        // arrive at the Hub (WS.TwitchEvents / WS.KickEvents / the YouTube
        // broadcast lifecycle) and flow through
        // ScriptManager.ExecuteGenericEventAsync.
        public const string TwitchOnline   = "Twitch.StreamOnline";
        public const string TwitchOffline  = "Twitch.StreamOffline";
        public const string YouTubeOnline  = "YouTube.BroadcastStarted";
        public const string YouTubeOffline = "YouTube.BroadcastEnded";
        public const string KickOnline     = "Kick.StreamOnline";
        public const string KickOffline    = "Kick.StreamOffline";

        /// <summary>
        /// The unified trigger an event-type string feeds, or
        /// <see cref="Kind.None"/> when the event is not a stream-lifecycle one.
        /// </summary>
        public static Kind KindForEvent(string? eventType)
        {
            if (string.IsNullOrEmpty(eventType)) return Kind.None;
            if (Eq(eventType, TwitchOnline) || Eq(eventType, YouTubeOnline) || Eq(eventType, KickOnline))
                return Kind.GoingLive;
            if (Eq(eventType, TwitchOffline) || Eq(eventType, YouTubeOffline) || Eq(eventType, KickOffline))
                return Kind.SessionEnd;
            return Kind.None;
        }

        /// <summary>
        /// Platform token ("twitch" / "youtube" / "kick") for a stream-lifecycle
        /// event-type string, or null when the event is not one. Shared spelling
        /// with <see cref="ChatPlatforms"/> so on_go_live/on_session_end platform
        /// lists gate identically to on_chat.
        /// </summary>
        public static string? PlatformForEvent(string? eventType)
        {
            if (string.IsNullOrEmpty(eventType)) return null;
            if (Eq(eventType, TwitchOnline)  || Eq(eventType, TwitchOffline))  return ChatPlatforms.Twitch;
            if (Eq(eventType, YouTubeOnline) || Eq(eventType, YouTubeOffline)) return ChatPlatforms.YouTube;
            if (Eq(eventType, KickOnline)    || Eq(eventType, KickOffline))    return ChatPlatforms.Kick;
            return null;
        }

        /// <summary>True when the event feeds the <c>Stream.GoingLive</c> node.</summary>
        public static bool IsGoingLiveEvent(string? eventType) => KindForEvent(eventType) == Kind.GoingLive;

        /// <summary>True when the event feeds the <c>Stream.SessionEnd</c> node.</summary>
        public static bool IsSessionEndEvent(string? eventType) => KindForEvent(eventType) == Kind.SessionEnd;

        private static bool Eq(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }
}
