// ─────────────────────────────────────────────────────────────────────────────
// ScriptStatusModel — UI-side state for ScriptMonitorWindow rows.
//
// The traffic-light reduction:
//   • Green  = Idle   — no execution in flight, no recent error.
//   • Yellow = Running — at least one execution active in the engine.
//   • Red    = Error  — most recent execution was cancelled (timeout / shutdown)
//                       or the engine logged a CriticalError attributed to this
//                       script.
//
// "Queued" is folded into Running visually: every queued execution that
// successfully passes the semaphore enters the Running state via
// ScriptManager.FireLifecycle(Running). Distinguishing Queued from Running on
// screen would require firing a separate phase before semaphore acquisition,
// which is out of scope for this redesign and would be a ScriptManager change.
//
// ScriptStatusRow is intentionally a plain mutable DTO. Mutation is gated to
// the UI thread by ScriptMonitorWindow's WindowDispatcher.SafeBeginInvoke
// marshal — the model itself is not thread-safe and shouldn't be touched from
// off-UI handlers.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using Phoenix.Controls.Hub.Core;

namespace Phoenix.Controls.Hub.Models
{
    public enum ScriptStatus
    {
        Idle,     // green — no running execution, no recent error
        Running,  // yellow — engine is executing this script
        Error     // red — last execution cancelled / faulted
    }

    public sealed class ScriptStatusRow
    {
        public string ScriptName { get; set; } = "";
        public ScriptStatus Status { get; set; } = ScriptStatus.Idle;
        public int RunningCount { get; set; } = 0;
        public DateTime? LastTrigger { get; set; } = null;
        public string LastTriggerType { get; set; } = "";
        public ScriptManager.ExecutionPolicy Policy { get; set; } = ScriptManager.ExecutionPolicy.Queue;
    }
}
