using System;
using System.Globalization;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.WinUI.Panels.UserManagementPanel;

/// <summary>
/// One waiting viewer in the queue list.
///
/// UNLIKE <see cref="WelcomeRowVm"/> / <see cref="GroupRowVm"/> this row is NOT config
/// and carries no <c>changed</c> callback or <c>ToSnapshot</c>: the queue lives in the
/// databank's open "Queues" table, not in the tool's JSON blob, so there is nothing for
/// the debounced save to project back. Rows are rebuilt wholesale whenever the line
/// changes — from a chat command, a script's <c>Queue.Push</c>, or the panel's own
/// remove button — which is also why every property here is read-only. The one action a
/// row offers is removal, and that goes straight to the service (and therefore to the
/// same store and the same <c>Queue.OnChanged</c> event a chat removal raises).
/// </summary>
public sealed class QueueRowVm : ObservableObject
{
    private readonly QueueEntry _entry;

    public QueueRowVm(QueueEntry entry)
        => _entry = entry ?? throw new ArgumentNullException(nameof(entry));

    /// <summary>The platform login — the identity the service matches on, and what the
    /// remove button hands back.</summary>
    internal string Login => _entry.Entry ?? "";

    /// <summary>1-based place in the line, as of the read that built this row.</summary>
    public string PositionText => _entry.Position.ToString(CultureInfo.InvariantCulture);

    /// <summary>The spelling to show. Falls back to the login when a graph pushed the
    /// entry with no payload — a script-driven queue.push is a first-class way into this
    /// line, so the row must still print something.</summary>
    public string Display
        => string.IsNullOrWhiteSpace(_entry.Payload) ? (_entry.Entry ?? "") : _entry.Payload;

    /// <summary>Priority badge, shown only when the entry actually carries weight —
    /// a line where everyone is at 0 should not be decorated with nine zeroes.</summary>
    public string PriorityText
        => _entry.Priority == 0 ? "" : "+" + _entry.Priority.ToString(CultureInfo.InvariantCulture);

    public Microsoft.UI.Xaml.Visibility PriorityVisibility
        => _entry.Priority == 0
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>Secondary line: the login, shown only when it differs from the display
    /// name — repeating an identical string twice is noise, and the difference is exactly
    /// what a moderator needs when the two disagree.</summary>
    public string LoginHint
        => string.Equals(Display, Login, StringComparison.OrdinalIgnoreCase) ? "" : Login;

    public Microsoft.UI.Xaml.Visibility LoginHintVisibility
        => LoginHint.Length == 0
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;
}
