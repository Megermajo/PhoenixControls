using System;

namespace Phoenix.Controls.Shared.Models
{
    // Structured error payload routed through GlobalLogger.OnError. Mirrors the
    // information GlobalLogger.OnLogEntry carries but keeps the original
    // Exception (with stack trace) intact for richer downstream consumers like
    // the Live Event Feed redesign or external telemetry sinks.
    public sealed class ErrorEvent
    {
        public DateTime  Timestamp { get; init; } = DateTime.Now;

        //  Offset-aware companion to Timestamp. See the parallel
        // comment on Log.TimestampOffset: DateTime serializes without a
        // timezone offset, so any on-wire JSON consumer should prefer
        // TimestampOffset. GlobalLogger.Error sets both fields from a
        // single DateTimeOffset.Now reading.
        public DateTimeOffset TimestampOffset { get; init; } = DateTimeOffset.Now;
        public string    Source    { get; init; } = "";
        public string    Message   { get; init; } = "";
        public Exception? Exception { get; init; }
        public LogLevel Level { get; init; } = LogLevel.CriticalError;
    }
}
