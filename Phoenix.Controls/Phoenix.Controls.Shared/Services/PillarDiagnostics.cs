using System;

namespace Phoenix.Controls.Shared.Services
{
    /// <summary>
    /// Tiny cross-pillar diagnostics blackboard. A pillar publishes a one-line
    /// status describing what it is currently holding (Architect: open graph's
    /// node/link counts); the Hub-side freeze diagnostics read it when a UI
    /// stall is captured, so a hang log states WHAT the wedged app was showing
    /// without needing a cross-assembly reference (Hub.Core cannot reference the
    /// pillar UI assemblies). Lock-free: a volatile string swap — writers
    /// replace the whole line, readers get the latest complete value.
    ///
    /// Added for the 2026-07 streaming-PC freeze hunt (Majo: "include resource
    /// usage logs in regards to Architect — especially on hang-logs").
    /// </summary>
    public static class PillarDiagnostics
    {
        private static volatile string? s_architectStatus;
        private static long s_architectStatusAtUtcTicks;

        /// <summary>Latest Architect status line, or null when never published.</summary>
        public static string? ArchitectStatus => s_architectStatus;

        /// <summary>UTC time the Architect status was last published (MinValue = never).</summary>
        public static DateTime ArchitectStatusAtUtc
        {
            get
            {
                long ticks = System.Threading.Interlocked.Read(ref s_architectStatusAtUtcTicks);
                return ticks == 0 ? DateTime.MinValue : new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        /// <summary>Publish the Architect pillar's current one-line status.</summary>
        public static void SetArchitectStatus(string? status)
        {
            s_architectStatus = status;
            System.Threading.Interlocked.Exchange(ref s_architectStatusAtUtcTicks, DateTime.UtcNow.Ticks);
        }
    }
}
