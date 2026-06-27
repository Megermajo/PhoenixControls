using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// Viewer-roadmap Slice 0 — thin wrapper over <see cref="DB.AppendRemoteAuditAsync"/>.
    /// Every remote write (success OR rejection) flows through here so the audit
    /// trail is the single point of truth for "who did what to which row, when,
    /// and what was the outcome."
    ///
    /// Failures during the audit write itself surface as CriticalError but do not
    /// throw — auditing must never block the original request from completing
    /// (or from being denied), and a swallowed log is preferable to a cascade
    /// failure that leaves the user mid-operation with no clear recovery.
    /// </summary>
    public static class RemoteAuditLogger
    {
        public const string ResultOk        = "OK";
        public const string ResultRejected  = "REJECTED";
        public const string ResultFailed    = "FAILED";

        public static async Task LogAsync(
            string deviceId,
            string action,
            string targetTable,
            string targetKey,
            string? before,
            string? after,
            string result)
        {
            try
            {
                await DB.Instance.AppendRemoteAuditAsync(
                    deviceId, action, targetTable, targetKey, before, after, result)
                    .ConfigureAwait(false);
            }
            catch (System.Exception ex)
            {
                GlobalLogger.Log(
                    $"RemoteAuditLogger: failed to write audit row " +
                    $"(device='{deviceId}', action='{action}', table='{targetTable}'): {ex.Message}",
                    "RemoteAudit", LogLevel.CriticalError);
            }
        }
    }
}
