using System;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: state.* + public.set command registrations.
    // Lifts the 6 handlers (state.set/get/exists/delete/list_keys + public.set)
    // out of RegisterHubCommands. The idempotent-write contract in state.set,
    // sentinel-pattern existence check in state.exists (mirrors db.check),
    // and the _branchResultKeysLocal tag on public.set are all carried
    // over verbatim.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        private void RegisterStateCommands()
        {
            // state.set(name, value) — persists to DB under state.{name}; fires on_state_change listeners if changed
            _engine.RegisterCommand("state.set", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string name  = bound?.GetOrDefault<string>("Name", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string value = bound?.GetOrDefault<string>("Value", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                if (string.IsNullOrEmpty(name)) return null;
                string key = $"state.{name}";
                // Both the DB read and the persisting write
                // touch SQLite; a DB outage (locked file, disk-full, disposed connection)
                // propagated and tore down the whole script. Catch, log, and surface a
                // fallback error result so the script can branch on failure.
                try
                {
                    string oldVal = await DB.Instance.GetVariableAsync(key, "");
                    // Always persist the write, even when oldVal == newVal. Skipping
                    // the write meant idempotent state.set calls left the DB out-of-sync
                    // with what callers had assumed was committed. We still gate the
                    // on_state_change listener fire on a real change so downstream
                    // scripts don't re-execute on no-op writes.
                    await _engine.SetScriptVarAsync(key, value);
                    if (oldVal != value)
                    {
                        GlobalLogger.Log($"State '{name}': '{oldVal}' → '{value}'", "Script", LogLevel.LogicExecution);
                        await ExecuteOnStateChangeScriptsAsync(name, value, oldVal);
                    }
                }
                catch (Exception ex)
                {
                    GlobalLogger.Log($"state.set('{name}') failed: {ex.Message}", "Script", LogLevel.CriticalError);
                    try { _engine.SetLocalResultVar("result.state_set_error", ex.Message); } catch { /* best-effort */ }
                }
                return null;
            });

            // state.get(name) — stores result in result.state_value
            _engine.RegisterCommand("state.get", async (args) =>
            {
                string name = _engine.CurrentBoundArgs?.GetOrDefault<string>("Name", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(name)) return null;
                // DB read + result-var write both touch
                // SQLite; a DB outage propagated and crashed the script. Catch, log,
                // and fall back to an empty value so the script keeps running.
                try
                {
                    string val = await DB.Instance.GetVariableAsync($"state.{name}", "");
                    await _engine.SetScriptVarAsync("result.state_value", val);
                }
                catch (Exception ex)
                {
                    GlobalLogger.Log($"state.get('{name}') failed: {ex.Message}", "Script", LogLevel.CriticalError);
                    try { _engine.SetLocalResultVar("result.state_value", ""); } catch { /* best-effort */ }
                    try { _engine.SetLocalResultVar("result.state_get_error", ex.Message); } catch { /* best-effort */ }
                }
                return null;
            });

            // state.exists(name) — pure-data: returns "true"/"false". Sentinel pattern
            // mirrors db.check so an empty-string state distinguishes from "missing".
            _engine.RegisterCommand("state.exists", async (args) =>
            {
                string name = _engine.CurrentBoundArgs?.GetOrDefault<string>("Name", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(name)) return "false";
                string sentinel = "\x1f__missing__\x1f" + Guid.NewGuid().ToString("N");
                string val = await DB.Instance.GetVariableAsync($"state.{name}", sentinel);
                return (val == sentinel) ? "false" : "true";
            });

            // state.delete(name) — flow node: removes a state var. Bypasses the
            // generic DB.DeleteVar reserved-prefix block via DB.DeleteStateAsync.
            _engine.RegisterCommand("state.delete", async (args) =>
            {
                string name = _engine.CurrentBoundArgs?.GetOrDefault<string>("Name", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(name)) return null;
                await DB.Instance.DeleteStateAsync(name);
                GlobalLogger.Log($"State '{name}' deleted", "Script", LogLevel.LogicExecution);
                return null;
            });

            // state.list_keys() — pure-data: returns a comma-separated list of state names.
            _engine.RegisterCommand("state.list_keys", async (args) =>
            {
                var names = await DB.Instance.ListStateKeysAsync();
                return string.Join(",", names);
            });

            // public.set(key, value) — script-run-wide variable tier. SetLocalResultVar
            // tags the key in _branchResultKeysLocal so writes inside a parallel_begin
            // branch propagate back to the parent on join. No DB
            // round-trip — the value lives only in _executionVars and vanishes when the
            // script run ends. Reads happen via {public.<key>} substitution against the
            // execution dict (Public.Get's pure-data path emits that reference directly).
            _engine.RegisterCommand("public.set", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string key   = bound?.GetOrDefault<string>("Key", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string value = bound?.GetOrDefault<string>("Value", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                if (string.IsNullOrWhiteSpace(key)) return null;
                _engine.SetLocalResultVar($"public.{key}", value);
                return null;
            });
        }
    }
#pragma warning restore CS1998
}
