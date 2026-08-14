using System.Globalization;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.WinUI.Panels.SongRequestPanel;

/// <summary>
/// One row of the request queue. Read-only by design — unlike QuoteRowVm there is nothing
/// on a request a streamer edits in place; the row's whole job is to describe the entry
/// and carry the stable <see cref="Id"/> its three buttons act on.
///
/// Addressing by Id rather than by position is load-bearing for a live queue: a chat
/// request landing (or a skip advancing) between the streamer reading a row and clicking
/// it would shift every position, and a position-keyed remove would then drop the wrong
/// song. The service's Remove/Approve/Deny-by-id overloads exist for exactly this.
/// </summary>
public sealed class SongRequestRowVm : ObservableObject
{
    public SongRequestRowVm(SongRequestEntry entry, int position)
    {
        Id = entry.Id;
        Position = position;
        Title = entry.DisplayTitle;
        Requester = entry.RequestedBy;
        VideoId = entry.VideoId;
        IsPending = entry.Status == SongRequestStatus.Pending;
        DurationText = entry.DurationSeconds > 0
            ? FormatDuration(entry.DurationSeconds)
            // "—" rather than "0:00": no API key means the length was never read, and a
            // zero-length song is a different (and impossible) claim.
            : "—";
    }

    public string Id { get; }
    public int Position { get; }
    public string Title { get; }
    public string Requester { get; }
    public string VideoId { get; }
    public string DurationText { get; }
    public bool IsPending { get; }

    public string PositionText => Position.ToString(CultureInfo.InvariantCulture);
    public string RequesterHint =>
        string.Format(Localizer.T("panel.songrequest.queue.row.by", "by {0}"), Requester);

    public string StatusText => IsPending
        ? Localizer.T("panel.songrequest.queue.row.awaiting_approval", "AWAITING APPROVAL")
        : Localizer.T("panel.songrequest.queue.row.waiting", "WAITING");

    /// <summary>Approve / Deny only exist for a request under review — a request already
    /// in the line is removed, not "denied".</summary>
    public Visibility PendingVisibility => IsPending ? Visibility.Visible : Visibility.Collapsed;

    private static string FormatDuration(int seconds)
    {
        int h = seconds / 3600, m = seconds % 3600 / 60, s = seconds % 60;
        return h > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", h, m, s)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", m, s);
    }
}
