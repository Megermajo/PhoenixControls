using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: the final five misc handlers that didn't fit any of
    // the prefix-based domain partials.
    //
    //   * `log` — bare logging shim used by hand-authored scripts. Maps
    //     to GlobalLogger.Log at LogicExecution tier; empty messages are
    //     silently dropped.
    //   * `streamerbot.do_action(actionId)` — direct Streamer.bot DoAction
    //     relay. M37 — guards on connection state and writes
    //     `result.sb_dispatched` so the exporter's Done branch can gate on
    //     successful dispatch instead of firing for a silent no-op.
    //   * `process.terminate(instanceId)` — unified spawn-termination path.
    //     Tries the engine's own spawn registry first; if not found, falls
    //     back to ProcessManager so legacy / hand-registered processes
    //     still tear down cleanly.
    //   * `system.log(message[, level])` — typed-string wrapper around
    //     GlobalLogger that accepts a level alias. L14 — unknown level
    //     raises a System-tier warning instead of silently downgrading.
    //   * `event.trigger(EventName, Key1=Val1, ...)` — fires Internal.<n>
    //     scripts and merges their returned vars back into local results.
    //     Uses _eventArgStore (a class-level ConcurrentDictionary<string,
    //     Dictionary<string,string>>) keyed by event name; partial-class
    //     state-share keeps that field accessible without refactoring.
    //
    // Carving these closes the ScriptManager partial-class split —
    // RegisterHubCommands is now an entirely declarative orchestrator
    // calling sibling RegisterXCommands() helpers.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        private void RegisterSystemCommands()
        {
            // log("message")
            _engine.RegisterCommand("log", async (args) => {
                string message = _engine.CurrentBoundArgs?.GetOrDefault<string>("Message", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (!string.IsNullOrEmpty(message))
                    GlobalLogger.Log(message, "Script", LogLevel.LogicExecution);
                return null;
            });

            // streamerbot.do_action(actionId)
            // Fires the named Streamer.bot action. Phase 2: composition routing removed —
            // this command is now strictly a Streamer.bot DoAction relay.
            // M37 — guard with IsConnected before dispatch. Without the connection the
            // SB websocket Send is a silent no-op, but the exporter's Done branch was
            // firing anyway because the handler returned cleanly. The fix is to:
            //   1) refuse to dispatch when SB isn't connected,
            //   2) log a Communication-tier error so the streamer sees it,
            //   3) write result.sb_dispatched = "false" so a Done branch can gate on it,
            //   4) skip the Send entirely so reconnection backlogs don't form silently.
            // Empty / missing actionId is also rejected — same skipped-Done semantics.
            _engine.RegisterCommand("streamerbot.do_action", async (args) => {
                string actionId = _engine.CurrentBoundArgs?.GetOrDefault<string>("ActionId", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrWhiteSpace(actionId))
                {
                    _engine.SetLocalResultVar("result.sb_dispatched", "false");
                    GlobalLogger.Log("streamerbot.do_action: missing action id — skipping dispatch.",
                        "Script", LogLevel.Communication);
                    await Task.CompletedTask;
                    return null;
                }
                if (!WS.Instance.IsConnected)
                {
                    _engine.SetLocalResultVar("result.sb_dispatched", "false");
                    GlobalLogger.Log($"streamerbot.do_action({actionId}): Streamer.bot is not connected — skipping dispatch and Done.",
                        "Script", LogLevel.Communication);
                    await Task.CompletedTask;
                    return null;
                }
                // Real Streamer.bot DoAction takes `action` as an OBJECT { id } or
                // { name } — a bare string only resolved under the StreamSimulator,
                // so this relay (the foundation every twitch.* action node and the
                // Phoenix action pack lean on) silently no-op'd live. The arg can be
                // a GUID (action id) or a name; pick the matching selector.
                object selector = Guid.TryParse(actionId, out _)
                    ? (object)new { id = actionId }
                    : new { name = actionId };
                WS.Instance.Send(JsonSerializer.Serialize(new { request = "DoAction", action = selector }));
                _engine.SetLocalResultVar("result.sb_dispatched", "true");
                //  Was LogLevel.VisualEvent — wrong category (no
                // visual side-effect here). Streamer.bot dispatch is an
                // outbound communication, so route through Communication.
                GlobalLogger.Log($"Triggered Streamer.bot action: {actionId}", "Script", LogLevel.Communication);
                await Task.CompletedTask;
                return null;
            });

            // process.terminate — unified termination path emitted by Process.Terminate.
            // Spawn / lifecycle of long-running async units is owned by the engine-native
            // `process_spawn` block (see ScriptEngine.HandleProcessSpawnBlock);
            // session.start / session.end / process.host / interceptor.start were retired
            // when ProcessManager / SessionManager folded into that primitive.
            _engine.RegisterCommand("process.terminate", async (args) => {
                string id = _engine.CurrentBoundArgs?.GetOrDefault<string>("InstanceId", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(id)) return null;
                // Cancelling the engine-side CTS triggers the spawn task's
                // OperationCanceledException → finally → OnProcessTerminated
                // event → ScriptManager subscriber above → ProcessManager
                // and RemoteBridgeServer. So one call propagates everywhere.
                bool found = _engine.TerminateSpawnedProcess(id);
                if (!found)
                {
                    // Backstop: if the id doesn't match a live engine spawn but
                    // does live in ProcessManager (legacy / hand-registered),
                    // still tear it down so the broadcast clears.
                    ProcessManager.Instance.TerminateProcess(id);
                }
                return null;
            });

            // ── System.Log ──────────────────────────────────────────────────
            // system.log(message[, level])
            //
            // R19 (sweep 14) — typed String reference with Optional Default. The
            // typed manifest declares Level as Optional Default "LogicExecution".
            // Detect "user actually supplied a level" via raw arg count rather
            // than the bound value (which is always populated thanks to Default)
            // so we don't fire phantom typo warnings on the default path.
            _engine.RegisterCommand("system.log", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string message = bound?.GetOrDefault<string>("Message", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(message)) return null;

                bool levelSupplied = (bound?.Raw.Count ?? args.Length) >= 2;
                LogLevel level = LogLevel.LogicExecution;
                if (levelSupplied)
                {
                    // L14 — typoed levels used to silently downgrade to LogicExecution
                    // with no signal to the author. Now we log a warning so the user
                    // knows their `level=InfO` typo went to the default bucket.
                    string raw = bound?.GetOrDefault<string>("Level", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                    string lvl = raw.ToLowerInvariant();
                    LogLevel? parsed = lvl switch
                    {
                        "debug"          => LogLevel.Debug,
                        "system"         => LogLevel.System,
                        "logicexecution" => LogLevel.LogicExecution,
                        "communication"  => LogLevel.Communication,
                        "streamevent"    => LogLevel.StreamEvent,
                        "visualevent"    => LogLevel.VisualEvent,
                        "criticalerror"  => LogLevel.CriticalError,
                        _                => null
                    };
                    if (parsed is null)
                    {
                        GlobalLogger.Log(
                            $"system.log: unknown level '{raw}' — defaulting to LogicExecution.",
                            "ScriptManager", LogLevel.System);
                    }
                    level = parsed ?? LogLevel.LogicExecution;
                }
                GlobalLogger.Log(message, "Script", level);
                return null;
            });

            // ── Internal event trigger ────────────────────────────────────────
            // event.trigger(EventName, Key1=Val1, Key2=Val2, ...)
            // Sweep 16 — EventArgs via ArgType.KvPairs Variadic; binder produces the dict.
            _engine.RegisterCommand("event.trigger", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string eventName = bound?.GetOrDefault<string>("Name", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(eventName)) return null;

                Dictionary<string, string> payload;
                if (bound != null)
                {
                    var ro = bound.GetOrDefault<IReadOnlyDictionary<string, string>>("EventArgs",
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                    payload = new Dictionary<string, string>(ro, StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in args.Skip(1))
                    {
                        var parts = kv.Split('=', 2);
                        if (parts.Length == 2) payload[parts[0].Trim()] = parts[1].Trim();
                    }
                }
                _eventArgStore[eventName] = payload;
                var returnVars = await ExecuteInternalEventAsync($"Internal.{eventName}", payload);
                foreach (var kv in returnVars)
                    _engine.SetLocalResultVar(kv.Key, kv.Value);
                return null;
            });
        }
    }
#pragma warning restore CS1998
}
