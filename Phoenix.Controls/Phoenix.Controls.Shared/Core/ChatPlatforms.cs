using System;

namespace Phoenix.Controls.Shared.Core
{
    /// <summary>
    /// The chat-capable platforms and the mapping between their Streamer.bot
    /// event-type strings, the platform tokens used in on_chat(platform, ...)
    /// headers, and the {user.platform} script variable. One place — WS routing,
    /// ScriptManager dispatch, the ScriptEngine block gate, and the exporter all
    /// derive from these constants so the token spelling can never drift.
    /// </summary>
    public static class ChatPlatforms
    {
        public const string Twitch  = "twitch";
        public const string YouTube = "youtube";
        public const string Kick    = "kick";

        public const string TwitchChatEvent  = "Twitch.ChatMessage";
        public const string YouTubeChatEvent = "YouTube.Message";
        public const string KickChatEvent    = "Kick.ChatMessage";

        /// <summary>All platform tokens, in canonical display order.</summary>
        public static readonly string[] All = { Twitch, YouTube, Kick };

        /// <summary>True when the event-type string is one of the platform chat events.</summary>
        public static bool IsChatEventType(string? eventType) => FromEventType(eventType) is not null;

        /// <summary>
        /// Platform token for a chat event-type string ("Twitch.ChatMessage" → "twitch"),
        /// or null when the event type is not a chat event.
        /// </summary>
        public static string? FromEventType(string? eventType)
        {
            if (string.IsNullOrEmpty(eventType)) return null;
            if (eventType.Equals(TwitchChatEvent,  StringComparison.OrdinalIgnoreCase)) return Twitch;
            if (eventType.Equals(YouTubeChatEvent, StringComparison.OrdinalIgnoreCase)) return YouTube;
            if (eventType.Equals(KickChatEvent,    StringComparison.OrdinalIgnoreCase)) return Kick;
            return null;
        }

        /// <summary>Chat event-type string for a platform token, or null for an unknown token.</summary>
        public static string? ChatEventTypeFor(string? platform)
        {
            if (string.IsNullOrEmpty(platform)) return null;
            if (platform.Equals(Twitch,  StringComparison.OrdinalIgnoreCase)) return TwitchChatEvent;
            if (platform.Equals(YouTube, StringComparison.OrdinalIgnoreCase)) return YouTubeChatEvent;
            if (platform.Equals(Kick,    StringComparison.OrdinalIgnoreCase)) return KickChatEvent;
            return null;
        }
    }
}
