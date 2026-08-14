using System;
using System.Collections.Generic;
using System.Linq;

namespace Phoenix.Controls.Shared.Models
{
    /// <summary>A message sent over the Bus IPC channel between Hub, Visualist, and Architect.</summary>
    public class BusMessage
    {
        public string Type    { get; set; } = "";   // e.g. VISUAL_TRIGGER, VISUAL_COMPLETE, HUB_EVENT
        public string Source  { get; set; } = "";   // e.g. Hub, Visualist, Architect
        public string Target  { get; set; } = "*";  // e.g. * (broadcast), Visualist, Hub
        public string Payload { get; set; } = "{}"; // JSON string
        public DateTime SentAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Structured payload for VISUAL_TRIGGER bus messages.
    /// Serialized as JSON into BusMessage.Payload.
    /// Phase 2 shape: triggers are addressed to a specific (layer, widget, trigger) tuple.
    /// </summary>
    public class VisualTriggerPayload
    {
        /// <summary>Layer name = `.phxlayer` filename stem (e.g., "main" for "main.phxlayer").</summary>
        public string LayerId { get; set; } = "";

        /// <summary>Widget GUID inside the layer's Widgets collection.</summary>
        public string WidgetId { get; set; } = "";

        /// <summary>Trigger name on the widget (e.g., "onStartup", "onTrigger:boss_arrival").</summary>
        public string TriggerName { get; set; } = "";

        /// <summary>
        /// Event data injected into the trigger graph at runtime.
        /// Keys use the same naming convention as Hub context vars:
        /// "user.name", "user.bits", "event.viewers", etc.
        /// </summary>
        public Dictionary<string, string> EventData { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Correlation token. Set when a script awaits VISUAL_COMPLETE (wait_for_visual);
        /// echoed by the browser so the matching pending wait can resolve.
        /// Empty for fire-and-forget triggers.
        /// </summary>
        public string WaitId { get; set; } = "";
    }

    /// <summary>
    /// Structured payload for VISUAL_COMPLETE bus messages.
    /// Browsers emit this when a trigger graph reaches its terminal state
    /// (Visual.Complete sink visited, or Display chain settled if no sink is wired).
    /// </summary>
    public class VisualCompletePayload
    {
        public string LayerId { get; set; } = "";
        public string WidgetId { get; set; } = "";
        public string TriggerName { get; set; } = "";

        /// <summary>Echoed waitId from the originating RUN_TRIGGER (empty if the trigger was fire-and-forget).</summary>
        public string WaitId { get; set; } = "";
    }

    /// <summary>
    /// Structured payload for CAPTION_UPDATE bus + WS messages.
    /// Hub's LiveCaptionService captures captions via UIA from Windows 11 LiveCaptions,
    /// optionally translates them, then broadcasts. Browsers store the latest in
    /// compositor.js's <c>state.caption</c> for the Caption.LiveCaption node to consume.
    /// </summary>
    public class CaptionUpdatePayload
    {
        /// <summary>The original caption text as captured from the source.</summary>
        public string Original { get; set; } = "";

        /// <summary>Translated text. Falls back to <see cref="Original"/> when no translator is wired.</summary>
        public string Translated { get; set; } = "";

        /// <summary>Detected source language (BCP-47, e.g. "en"). Empty if unknown.</summary>
        public string SourceLanguage { get; set; } = "";

        /// <summary>Configured target language (BCP-47, e.g. "de"). Empty if translation is disabled.</summary>
        public string TargetLanguage { get; set; } = "";
    }


    public enum LogLevel
    {
        System,
        Communication,
        StreamEvent,
        LogicExecution,
        VisualEvent,
        CriticalError,
        Debug,
        // Warning is the bridge from third-party / soft-fault
        // sources (OBS reconnect blip, rate-limit backoff, etc.) into the
        // SystemLog panel's WARN chip. Pre-fix the chip was wired to a level
        // SystemLogSource.MapLevel never produced, so it filtered to an
        // empty set. Appended at the end so existing persisted int values
        // (DB log rows seeded before this change) keep their meaning.
        Warning,
    }

    public class Log
    {
        public int Id { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;

        // Offset-aware companion to Timestamp. DateTime serializes
        // without a zone (the local wall-clock is written but the offset is
        // lost on a foreign reader), so any on-wire JSON sink — RemoteBridge,
        // a future telemetry exporter, the SystemLog JSON dump — should
        // prefer TimestampOffset for the persisted form. UI rendering keeps
        // using Timestamp (Kind=Local) so existing formatters don't have to
        // re-bind. GlobalLogger populates both fields from a single
        // DateTimeOffset.Now reading to avoid skew.
        public DateTimeOffset TimestampOffset { get; set; } = DateTimeOffset.Now;
        public LogLevel Level { get; set; } = LogLevel.System;
        public string Source { get; set; } = "System";
        public string Message { get; set; } = "";
        public string RawData { get; set; } = "";

        // Live exception preserved through OnLogEntry / GetRecentLogs.
        // Populated by GlobalLogger.Error(source, label, ex). Not surfaced via
        // DB.LogAsync (the SystemHistory table is string-only); persistence
        // sinks that want the stack trace should serialize Exception?.ToString()
        // — never the object itself (System.Exception is not JSON-friendly:
        // circular ref via TargetSite/Method, runtime-only fields, etc.).
        // Null on every non-Error log entry.
        public Exception? Exception { get; set; }
    }

    public class Event
    {
        public string EventSource { get; set; } = "Twitch";
        public string EventType { get; set; } = "ChatMessage";
        public string User { get; set; } = "";
        public string PayloadJson { get; set; } = "";
        public DateTime ReceivedAt { get; set; } = DateTime.Now;
    }

    // Distinct from `Phoenix.Controls.Shared.WinUI.Contracts.ChatMessage`
    // (a record with `Body`, used by the Hub WinUI panel layer). THIS type is the SCRIPT-ENGINE-FACING
    // chat envelope — its `Message` field is read by the runtime (ScriptManager, ScriptEngine) and
    // its IsMod / IsSub / etc. flags drive script-side role gating. Do NOT swap the two without
    // tracing every consumer; the WinUI Contracts record was introduced precisely to give the WinUI
    // chrome a stable contract without the script-runtime baggage on this class.
    public class ChatMessage
    {
        public string Username { get; set; } = "";
        public string Message { get; set; } = "";
        public string ColorHex { get; set; } = "#FFFFFF";
        // Chat platform token ("twitch" / "youtube" / "kick" — see ChatPlatforms).
        // Defaults to twitch so every pre-multi-platform construction site and
        // test keeps its original meaning without edits.
        public string Platform { get; set; } = Phoenix.Controls.Shared.Core.ChatPlatforms.Twitch;
        // Per-message id of the triggering chat message (Twitch/YouTube/Kick).
        // Defaults to "" so every pre-existing construction site and test keeps
        // working unchanged — same additive pattern as Platform. Surfaced to
        // scripts as {event.message_id} (BuildChatVars) and as the Chat.Message
        // node's MessageId output socket; consumed by twitch.reply / kick.reply /
        // *.delete_message and by the Automod tool's Delete ladder rung.
        //
        // ★ NOTHING POPULATES IT IN THIS BUILD, and every consumer above is dark
        // because of it. This comment claimed "populated by the per-platform WS
        // mappers" until 2026-08-03; no mapper does. WS.cs builds every ChatMessage
        // without touching this property — the Twitch chat arm's inline initializer,
        // TryBuildYouTubeChatMessage and TryBuildKickChatMessage are the only three
        // build sites — so the value a consumer reads is always "". They are written to
        // degrade honestly rather than to pretend: twitch.reply / kick.reply log
        // "empty messageId — skipping", and Automod's capability gate refuses the
        // Delete rung and says why once (ScriptManager.Automod.cs,
        // ResolveAutomodDeleteAction). Wiring this up is a WS.cs mapper change and
        // nothing else — the moment a mapper fills it, all of those light up with no
        // edit anywhere downstream. Do not "fix" a dark consumer by making it
        // optimistic; fix the mapper.
        public string MessageId { get; set; } = "";
        // Platform LOGIN of the chatter (Twitch: the lowercase ASCII handle;
        // YouTube/Kick: whatever login/handle the payload carries, else "").
        // Distinct from Username, which Twitch fills with the DISPLAY name —
        // possibly localized/non-ASCII and entirely different from the login
        // (the QC06-03 bot-guard lesson). Defaults "" so every pre-existing
        // construction site and test keeps working unchanged — same additive
        // pattern as Platform/MessageId. Consumed by the User-Management tool,
        // whose group members / personalized rows are keyed by login.
        public string Login { get; set; } = "";
        public bool IsBroadcaster { get; set; } = false;
        public bool IsMod { get; set; } = false;
        public bool IsSub { get; set; } = false;
        public bool IsVip { get; set; } = false;
        public int SubMonths { get; set; } = 0;
        public string RoleBadge =>
            IsBroadcaster ? "[Broadcaster]" :
            IsMod         ? "[Mod]" :
            IsVip         ? "[VIP]" :
            IsSub         ? "[Sub]" : "";
    }
}
