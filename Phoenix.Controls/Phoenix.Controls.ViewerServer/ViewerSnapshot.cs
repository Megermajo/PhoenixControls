using System;
using System.Collections.Generic;

namespace Phoenix.Controls.ViewerServer;

/// <summary>
/// Bootstrap payload returned by <c>GET /v/api/snapshot</c>. Mirrors the
/// shape laid out in <c>Viewer_Roadmap.md</c> § "Snapshot payload" but
/// scoped read-only — no databank, no process lifecycle.
/// </summary>
public sealed class ViewerSnapshot
{
    public string Channel { get; init; } = "";
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public ViewerConnectionStatus ConnectionStatus { get; init; } = new();
    public IReadOnlyList<ViewerLiveFeedEntry> LiveFeed { get; init; } = Array.Empty<ViewerLiveFeedEntry>();
    public IReadOnlyList<ViewerChatMessage>   Chat     { get; init; } = Array.Empty<ViewerChatMessage>();
    public IReadOnlyList<ViewerScriptStatus>  Scripts  { get; init; } = Array.Empty<ViewerScriptStatus>();
    public IReadOnlyList<ViewerSystemLogEntry> SystemLog { get; init; } = Array.Empty<ViewerSystemLogEntry>();
}

/// <summary>One row in the Live Event Feed panel.</summary>
public sealed record ViewerLiveFeedEntry(
    DateTimeOffset Timestamp,
    string         Kind,        // "Sub" | "Chat" | "Redeem" | "Raid" | "Visual" | "Follow"
    string         Who,
    string         Detail);

/// <summary>One chat line. Send-as-bot is not exposed on the wire.</summary>
public sealed record ViewerChatMessage(
    DateTimeOffset Timestamp,
    string         Role,        // "Broadcaster" | "Mod" | "Vip" | "Sub" | "Viewer" | "Bot"
    string         Username,
    string         Body);

/// <summary>Read-only mirror of a Hub script's runtime status.</summary>
public sealed record ViewerScriptStatus(
    string   Path,
    string   Name,
    string   State,             // "Idle" | "Running" | "Errored" | "Queued"
    bool     Enabled,
    double   LastCpuMs,
    long     LastRamBytes,
    int      RunCount,
    string?  LastError);

/// <summary>One row in the System Log panel.</summary>
public sealed record ViewerSystemLogEntry(
    DateTimeOffset Timestamp,
    string         Level,       // "Debug" | "Info" | "Warn" | "Error"
    string         Source,
    string         Message);

/// <summary>Connection-status strip. Architect/Visualist excluded — the design says viewer skips them.</summary>
public sealed class ViewerConnectionStatus
{
    public string StreamerBot { get; init; } = "Disconnected";
    public string HudOverlay  { get; init; } = "Disconnected";
    public string IpcBus      { get; init; } = "Disconnected";
    public string TwitchIrc   { get; init; } = "Disconnected";
}

/// <summary>
/// Discriminated push event sent over the WS channel. Wire format wraps
/// this in the existing <c>BusMessage</c> envelope with type
/// prefix <c>VIEWER_</c>.
/// </summary>
public abstract record ViewerEvent(string Kind);

public sealed record ViewerLiveFeedAppended(ViewerLiveFeedEntry Entry)
    : ViewerEvent("VIEWER_LIVEFEED");

public sealed record ViewerChatAppended(ViewerChatMessage Message)
    : ViewerEvent("VIEWER_CHAT");

public sealed record ViewerScriptStatusChanged(ViewerScriptStatus Status)
    : ViewerEvent("VIEWER_SCRIPT_STATUS");

public sealed record ViewerSystemLogAppended(ViewerSystemLogEntry Entry)
    : ViewerEvent("VIEWER_SYSTEM_LOG");

public sealed record ViewerConnectionStatusChanged(ViewerConnectionStatus Status)
    : ViewerEvent("VIEWER_CONNECTION_STATUS");
