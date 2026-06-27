using System;

namespace Phoenix.Controls.Hub.WinUI.Panels.EventLogPanel;

/// <summary>
/// Row VM for the EventLog tail panel — one entry from the SQLite
/// <c>EventLog</c> table. Fields mirror the column layout returned by
/// <see cref="Phoenix.Controls.Shared.Services.DB.GetRowsWithRowIdAsync"/>:
/// <c>Timestamp / EventSource / EventType / User / Payload</c>. The panel
/// renders timestamp + source + event-type + payload-summary; <see cref="User"/>
/// is preserved for potential future filtering even though it's not surfaced
/// in the row template.
///
/// All properties are init-only (no notification surface) — the VM rebuilds
/// the row list on every refresh rather than mutating individual rows, so a
/// PropertyChanged event would have no subscribers and produces a CS0067
/// warning. If a future feature wants to update a row in place (e.g.
/// streaming payload preview), reintroduce INotifyPropertyChanged then.
///
/// B43 (audit/winui-regressions-2026-05-24): EventLog was queryable only
/// through the Architect Databank tab with manual refresh. This panel
/// gives Hub a live tail surface so operators can watch external triggers
/// + audit events arrive without leaving the Hub workspace.
/// </summary>
public sealed class EventLogRowVm
{
    public EventLogRowVm(long rowId, DateTime timestamp, string source, string eventType, string user, string payload)
    {
        RowId = rowId;
        Timestamp = timestamp;
        TimestampText = timestamp.ToLocalTime().ToString("HH:mm:ss");
        Source = source ?? string.Empty;
        EventType = eventType ?? string.Empty;
        User = user ?? string.Empty;
        Payload = payload ?? string.Empty;
        PayloadSummary = BuildPayloadSummary(Payload);
    }

    /// <summary>SQLite rowid — opaque stable identifier; used to dedup rows during polling refresh.</summary>
    public long RowId { get; }
    public DateTime Timestamp { get; }
    public string TimestampText { get; }
    public string Source { get; }
    public string EventType { get; }
    public string User { get; }
    public string Payload { get; }
    public string PayloadSummary { get; }

    /// <summary>
    /// Collapse a (potentially long, potentially JSON) Payload value to a
    /// single-line preview suitable for the row's last column. Newlines and
    /// tabs become spaces; runs of whitespace collapse to one space; the
    /// string is truncated to MaxSummaryChars with an ellipsis suffix.
    /// </summary>
    private const int MaxSummaryChars = 160;
    private static string BuildPayloadSummary(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var buf = new System.Text.StringBuilder(Math.Min(raw.Length, MaxSummaryChars + 8));
        bool prevWasSpace = false;
        for (int i = 0; i < raw.Length && buf.Length < MaxSummaryChars; i++)
        {
            char c = raw[i];
            if (c == '\r' || c == '\n' || c == '\t' || c == ' ')
            {
                if (!prevWasSpace) { buf.Append(' '); prevWasSpace = true; }
            }
            else
            {
                buf.Append(c);
                prevWasSpace = false;
            }
        }
        string s = buf.ToString().Trim();
        if (raw.Length > MaxSummaryChars) s += "…";
        return s;
    }
}
