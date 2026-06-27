using System;

namespace Phoenix.Controls.Hub.WinUI.Panels.WebhookPanel;

/// <summary>
/// C16 (audit/winui-regressions-2026-05-24) — row VM for the Hub
/// "Recent Webhooks" tail panel. One entry per fire surfaced by
/// <see cref="Phoenix.Controls.Hub.Core.HUDServer.OnWebhookFired"/>.
///
/// Mirrors the shape of <see cref="EventLogPanel.EventLogRowVm"/> —
/// immutable properties, no PropertyChanged surface, the panel VM
/// rebuilds its row list on every fire rather than mutating in place
/// so a notification surface would have no subscribers.
/// </summary>
public sealed class WebhookRowVm
{
    public WebhookRowVm(
        DateTime firedAtUtc,
        string endpoint,
        string remoteAddress,
        int payloadBytes)
    {
        FiredAtUtc      = firedAtUtc;
        // Render in the operator's local timezone — the audit panel is a
        // diagnostic surface, not a wire format. HH:mm:ss is enough
        // resolution to correlate against external logs (webhook callers
        // typically log at second precision themselves).
        TimestampText   = firedAtUtc.ToLocalTime().ToString("HH:mm:ss");
        Endpoint        = endpoint ?? string.Empty;
        RemoteAddress   = remoteAddress ?? string.Empty;
        PayloadBytes    = payloadBytes;
        PayloadSizeText = FormatBytes(payloadBytes);
    }

    public DateTime FiredAtUtc      { get; }
    public string   TimestampText   { get; }
    public string   Endpoint        { get; }
    public string   RemoteAddress   { get; }
    public int      PayloadBytes    { get; }
    public string   PayloadSizeText { get; }

    /// <summary>
    /// Human-readable byte count for the size column. Sticks to integer
    /// units (B / kB / MB) — fractional kB is noise on a diagnostic surface
    /// and keeps the cell width predictable. Webhook bodies above 1MB are
    /// already rejected by the HUDServer guard, so a cap of "MB" is
    /// sufficient.
    /// </summary>
    private static string FormatBytes(int bytes)
    {
        if (bytes < 0) return "?";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024} kB";
        return $"{bytes / (1024 * 1024)} MB";
    }
}
