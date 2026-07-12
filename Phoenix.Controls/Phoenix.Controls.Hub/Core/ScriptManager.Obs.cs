using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: obs.* command registrations.
    // OBS control surface — dual transport. The three transform handlers
    // (obs.set_source_position / _scale / _rotation) prefer Hub's own
    // ObsWebSocketClient: when the direct OBS WS v5 connection is up they run
    // GetSceneItemId → SetSceneItemTransform themselves (see
    // ApplyObsTransformAsync) and surface OBS's own failure comment in
    // `result.obs_error`. Every other obs.* command — and the transforms when
    // the direct connection is down — dispatches a Phoenix action-pack
    // wrapper (e.g. `Phoenix: OBS Set Scene`) via DispatchNamedAction:
    // DoAction with action:{ name } against a user-configured Streamer.bot
    // action that wraps SB's native OBS sub-action. The previous bare-string
    // DoAction("ObsSetScene", …) only resolved under the StreamSimulator, so OBS
    // nodes silently no-op'd against a live Streamer.bot (same class of bug the
    // twitch.* nodes had — see ScriptManager.Twitch.cs). Conventions:
    //   * Empty required string args → log + return (no dispatch).
    //   * Numeric/bool args forwarded as invariant strings on the SB relay
    //     (they surface as %variable% values inside the SB action); the
    //     direct path keeps them numeric.
    //   * `result.obs_error` is "" after a successful direct dispatch and
    //     after every SB relay (fire-and-forget — no response to inspect);
    //     a failed direct dispatch writes the diagnostic into it. This
    //     preserves the downstream `if {result.obs_error}` script contract
    //     on both transports.
    //   * DispatchNamedAction logs LOUDLY (CriticalError) when SB is disconnected
    //     or the wrapper action is missing — no more silent no-op.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        private void RegisterObsCommands()
        {
            // obs.set_scene(scene)
            _engine.RegisterCommand("obs.set_scene", async (args) =>
            {
                string scene = _engine.CurrentBoundArgs?.GetOrDefault<string>("Scene", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrWhiteSpace(scene))
                {
                    GlobalLogger.Log("obs.set_scene: empty scene — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                DispatchNamedAction("obs.set_scene", PhxSbActions.ObsSetScene, new { scene });
                await _engine.SetScriptVarAsync("result.obs_error", "");
                await Task.CompletedTask;
                return null;
            });

            // obs.set_source_visible(scene, source, visible)
            _engine.RegisterCommand("obs.set_source_visible", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string scene  = bound?.GetOrDefault<string>("Scene", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string source = bound?.GetOrDefault<string>("Source", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                bool   visible = (bound != null && bound.ContainsKey("Visible"))
                    ? bound.Get<bool>("Visible")
                    : (bool.TryParse(ArgOrEmpty(args, 2), out var b) && b);
                if (string.IsNullOrWhiteSpace(scene))
                {
                    GlobalLogger.Log("obs.set_source_visible: empty scene — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                if (string.IsNullOrWhiteSpace(source))
                {
                    GlobalLogger.Log("obs.set_source_visible: empty source — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                DispatchNamedAction("obs.set_source_visible", PhxSbActions.ObsSourceVisible,
                    new { scene, source, visible = visible ? "true" : "false" });
                await _engine.SetScriptVarAsync("result.obs_error", "");
                await Task.CompletedTask;
                return null;
            });

            // obs.refresh_browser_source(scene, source, url)
            // The "Phoenix: OBS Refresh Browser" action SETS the source's URL (to
            // %link%) and reloads it, so a URL is required — sending an empty %link%
            // would blank the browser source. url is forwarded as the `link` arg.
            _engine.RegisterCommand("obs.refresh_browser_source", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string scene  = bound?.GetOrDefault<string>("Scene", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string source = bound?.GetOrDefault<string>("Source", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string url    = bound?.GetOrDefault<string>("Url", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                if (string.IsNullOrWhiteSpace(scene))
                {
                    GlobalLogger.Log("obs.refresh_browser_source: empty scene — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                if (string.IsNullOrWhiteSpace(source))
                {
                    GlobalLogger.Log("obs.refresh_browser_source: empty source — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                if (string.IsNullOrWhiteSpace(url))
                {
                    GlobalLogger.Log(
                        "obs.refresh_browser_source: empty URL — skipping. The Phoenix: OBS Refresh Browser " +
                        "action sets the source's URL, so a URL is required (sending an empty one would blank the source).",
                        "Script", LogLevel.Communication);
                    return null;
                }
                DispatchNamedAction("obs.refresh_browser_source", PhxSbActions.ObsRefreshBrowser, new { scene, source, link = url });
                await _engine.SetScriptVarAsync("result.obs_error", "");
                await Task.CompletedTask;
                return null;
            });

            // obs.start_recording() — no args.
            _engine.RegisterCommand("obs.start_recording", async (args) =>
            {
                DispatchNamedAction("obs.start_recording", PhxSbActions.ObsStartRecording, new { });
                await _engine.SetScriptVarAsync("result.obs_error", "");
                await Task.CompletedTask;
                return null;
            });

            // obs.stop_recording() — no args.
            _engine.RegisterCommand("obs.stop_recording", async (args) =>
            {
                DispatchNamedAction("obs.stop_recording", PhxSbActions.ObsStopRecording, new { });
                await _engine.SetScriptVarAsync("result.obs_error", "");
                await Task.CompletedTask;
                return null;
            });

            // obs.start_streaming() — no args.
            _engine.RegisterCommand("obs.start_streaming", async (args) =>
            {
                DispatchNamedAction("obs.start_streaming", PhxSbActions.ObsStartStreaming, new { });
                await _engine.SetScriptVarAsync("result.obs_error", "");
                await Task.CompletedTask;
                return null;
            });

            // obs.stop_streaming() — no args.
            _engine.RegisterCommand("obs.stop_streaming", async (args) =>
            {
                DispatchNamedAction("obs.stop_streaming", PhxSbActions.ObsStopStreaming, new { });
                await _engine.SetScriptVarAsync("result.obs_error", "");
                await Task.CompletedTask;
                return null;
            });

            // obs.save_replay_buffer() — no args.
            _engine.RegisterCommand("obs.save_replay_buffer", async (args) =>
            {
                DispatchNamedAction("obs.save_replay_buffer", PhxSbActions.ObsSaveReplay, new { });
                await _engine.SetScriptVarAsync("result.obs_error", "");
                await Task.CompletedTask;
                return null;
            });

            // obs.set_source_position(scene, source, x, y) — manifest declares X/Y as Float.
            _engine.RegisterCommand("obs.set_source_position", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string scene  = bound?.GetOrDefault<string>("Scene", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string source = bound?.GetOrDefault<string>("Source", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                double x;
                double y;
                if (bound != null)
                {
                    x = bound.GetOrDefault<double>("X", 0d);
                    y = bound.GetOrDefault<double>("Y", 0d);
                }
                else
                {
                    x = double.TryParse(ArgOrEmpty(args, 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var xv) ? xv : 0d;
                    y = double.TryParse(ArgOrEmpty(args, 3), NumberStyles.Float, CultureInfo.InvariantCulture, out var yv) ? yv : 0d;
                }
                if (string.IsNullOrWhiteSpace(scene))
                {
                    GlobalLogger.Log("obs.set_source_position: empty scene — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                if (string.IsNullOrWhiteSpace(source))
                {
                    GlobalLogger.Log("obs.set_source_position: empty source — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                // Direct transport first: a live OBS WS connection gives a
                // real success/failure response instead of the SB relay's
                // fire-and-forget. Field names are the OBS v5
                // SceneItemTransform keys.
                var obs = ObsWebSocketClient.Current;
                if (obs is { IsConnected: true })
                {
                    await ApplyObsTransformAsync(obs, "obs.set_source_position", scene, source,
                        new Dictionary<string, double>
                        {
                            ["positionX"] = x,
                            ["positionY"] = y,
                        });
                    return null;
                }
                DispatchNamedAction("obs.set_source_position", PhxSbActions.ObsSourcePosition, new {
                    scene,
                    source,
                    x = x.ToString(CultureInfo.InvariantCulture),
                    y = y.ToString(CultureInfo.InvariantCulture),
                });
                await _engine.SetScriptVarAsync("result.obs_error", "");
                await Task.CompletedTask;
                return null;
            });

            // obs.set_source_scale(scene, source, scaleX, scaleY) — manifest declares ScaleX/ScaleY as Float.
            _engine.RegisterCommand("obs.set_source_scale", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string scene  = bound?.GetOrDefault<string>("Scene", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string source = bound?.GetOrDefault<string>("Source", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                double scaleX;
                double scaleY;
                if (bound != null)
                {
                    scaleX = bound.GetOrDefault<double>("ScaleX", 1d);
                    scaleY = bound.GetOrDefault<double>("ScaleY", 1d);
                }
                else
                {
                    scaleX = double.TryParse(ArgOrEmpty(args, 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var sx) ? sx : 1d;
                    scaleY = double.TryParse(ArgOrEmpty(args, 3), NumberStyles.Float, CultureInfo.InvariantCulture, out var sy) ? sy : 1d;
                }
                if (string.IsNullOrWhiteSpace(scene))
                {
                    GlobalLogger.Log("obs.set_source_scale: empty scene — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                if (string.IsNullOrWhiteSpace(source))
                {
                    GlobalLogger.Log("obs.set_source_scale: empty source — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                // Direct-vs-relay split — see obs.set_source_position.
                var obs = ObsWebSocketClient.Current;
                if (obs is { IsConnected: true })
                {
                    await ApplyObsTransformAsync(obs, "obs.set_source_scale", scene, source,
                        new Dictionary<string, double>
                        {
                            ["scaleX"] = scaleX,
                            ["scaleY"] = scaleY,
                        });
                    return null;
                }
                DispatchNamedAction("obs.set_source_scale", PhxSbActions.ObsSourceScale, new {
                    scene,
                    source,
                    scaleX = scaleX.ToString(CultureInfo.InvariantCulture),
                    scaleY = scaleY.ToString(CultureInfo.InvariantCulture),
                });
                await _engine.SetScriptVarAsync("result.obs_error", "");
                await Task.CompletedTask;
                return null;
            });

            // obs.set_source_rotation(scene, source, degrees) — manifest declares Degrees as Float.
            _engine.RegisterCommand("obs.set_source_rotation", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string scene  = bound?.GetOrDefault<string>("Scene", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string source = bound?.GetOrDefault<string>("Source", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                double degrees;
                if (bound != null && bound.ContainsKey("Degrees")) degrees = bound.Get<double>("Degrees");
                else degrees = double.TryParse(ArgOrEmpty(args, 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0d;
                if (string.IsNullOrWhiteSpace(scene))
                {
                    GlobalLogger.Log("obs.set_source_rotation: empty scene — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                if (string.IsNullOrWhiteSpace(source))
                {
                    GlobalLogger.Log("obs.set_source_rotation: empty source — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                // Direct-vs-relay split — see obs.set_source_position.
                var obs = ObsWebSocketClient.Current;
                if (obs is { IsConnected: true })
                {
                    await ApplyObsTransformAsync(obs, "obs.set_source_rotation", scene, source,
                        new Dictionary<string, double>
                        {
                            ["rotation"] = degrees,
                        });
                    return null;
                }
                DispatchNamedAction("obs.set_source_rotation", PhxSbActions.ObsSourceRotation, new {
                    scene,
                    source,
                    degrees = degrees.ToString(CultureInfo.InvariantCulture),
                });
                await _engine.SetScriptVarAsync("result.obs_error", "");
                await Task.CompletedTask;
                return null;
            });

            // obs.set_filter_visible(scene, source, filter, visible)
            _engine.RegisterCommand("obs.set_filter_visible", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string scene   = bound?.GetOrDefault<string>("Scene", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string source  = bound?.GetOrDefault<string>("Source", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string filter  = bound?.GetOrDefault<string>("Filter", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                bool   visible = (bound != null && bound.ContainsKey("Visible"))
                    ? bound.Get<bool>("Visible")
                    : (bool.TryParse(ArgOrEmpty(args, 3), out var b) && b);
                if (string.IsNullOrWhiteSpace(scene))
                {
                    GlobalLogger.Log("obs.set_filter_visible: empty scene — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                if (string.IsNullOrWhiteSpace(source))
                {
                    GlobalLogger.Log("obs.set_filter_visible: empty source — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                if (string.IsNullOrWhiteSpace(filter))
                {
                    GlobalLogger.Log("obs.set_filter_visible: empty filter — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                DispatchNamedAction("obs.set_filter_visible", PhxSbActions.ObsFilterVisible,
                    new { scene, source, filter, visible = visible ? "true" : "false" });
                await _engine.SetScriptVarAsync("result.obs_error", "");
                await Task.CompletedTask;
                return null;
            });

            // on_obs("EventType") manifest stub. The matching script header is
            // detected by ScriptRegistry's regex and dispatched from
            // DispatchObsEvent below; this RegisterCommand call exists purely to
            // satisfy VerifyCommandManifest's "every manifest entry must be
            // registered" check. It is never actually invoked from inside a .phx
            // body — the script grammar only ever encounters `on_obs(...)` as a
            // block-header prefix, which ScriptEngine routes via BlockHeaderPrefixes
            // (not the command dispatch path).
            _engine.RegisterCommand("on_obs", async (args) =>
            {
                GlobalLogger.Log(
                    "on_obs(...) was invoked as a command from script body — this is a header-only construct, ignored. The matching on_obs block header is dispatched by ObsWebSocketClient + ScriptManager.DispatchObsEvent.",
                    "Script", LogLevel.Communication);
                await Task.CompletedTask;
                return null;
            });

            // obs.take_screenshot(scene, source, path)
            _engine.RegisterCommand("obs.take_screenshot", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string scene  = bound?.GetOrDefault<string>("Scene", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string source = bound?.GetOrDefault<string>("Source", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string path   = bound?.GetOrDefault<string>("Path", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                if (string.IsNullOrWhiteSpace(scene))
                {
                    GlobalLogger.Log("obs.take_screenshot: empty scene — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                if (string.IsNullOrWhiteSpace(source))
                {
                    GlobalLogger.Log("obs.take_screenshot: empty source — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                if (string.IsNullOrWhiteSpace(path))
                {
                    GlobalLogger.Log("obs.take_screenshot: empty path — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                DispatchNamedAction("obs.take_screenshot", PhxSbActions.ObsScreenshot, new { scene, source, path });
                await _engine.SetScriptVarAsync("result.obs_error", "");
                await Task.CompletedTask;
                return null;
            });
        }

        // Direct-OBS transform dispatch shared by the three obs.set_source_*
        // handlers. Runs the two-step GetSceneItemId → SetSceneItemTransform
        // against the live ObsWebSocketClient and maps the outcome onto the
        // script-facing `result.obs_error` contract: "" on success, a
        // diagnostic string on failure — plus a Communication-tier log line
        // so the operator sees WHY a transform didn't land.
        private async Task ApplyObsTransformAsync(
            ObsWebSocketClient obs, string command, string scene, string source,
            IReadOnlyDictionary<string, double> transformFields)
        {
            var result = await obs.SetSceneItemTransformAsync(scene, source, transformFields);
            if (result.Success)
            {
                await _engine.SetScriptVarAsync("result.obs_error", "");
                return;
            }

            string error = string.IsNullOrWhiteSpace(result.Comment)
                ? $"OBS request failed (code {result.Code})"
                : result.Comment;
            GlobalLogger.Log($"{command} → direct OBS dispatch failed: {error}",
                "Script", LogLevel.Communication);
            await _engine.SetScriptVarAsync("result.obs_error", error);
        }

        /// <summary>
        /// Fans an inbound OBS WebSocket v5 event out to every enabled
        /// script declaring an <c>on_obs("&lt;eventType&gt;")</c> header with a
        /// matching event type. Called by <c>HubBootstrapper</c>'s subscription
        /// to <see cref="ObsWebSocketClient.EventReceived"/>; the bus
        /// <c>OBS_EVENT</c> broadcast for Architect's debug-trace + future
        /// panels is emitted by the bootstrapper alongside this dispatch (same
        /// pattern as the WS.cs scene-change dual-routing).
        ///
        /// Script-side vars populated:
        ///   event.type — the bare OBS event name (e.g. CurrentProgramSceneChanged)
        ///   event.data — the raw eventData JSON object as a string ("{}" when
        ///                the event carries no data)
        ///   obs.event_type / obs.event_data — duplicates for explicit-namespace
        ///                                     scripts that prefer not to lean on the
        ///                                     generic event.* namespace
        ///   EventData    — uppercase passthrough used by graphs that wire the
        ///                  OBS.Event node's EventData socket without going through
        ///                  the exporter's event.data alias
        ///
        /// Fan-out is parallel (mirrors on_clipboard / on_websocket),
        /// rate-limited by the shared event semaphore via
        /// <see cref="AcquireEventSlotAsync"/>. Multiple scripts declaring
        /// the same event type all fire.
        /// </summary>
        public async Task DispatchObsEvent(string eventType, string payload)
        {
            // Async init gate — see ExecuteEventScriptAsync. Without
            // this a very fast OBS-event-on-startup could land before
            // ScriptRegistry has finished its cold-load and find zero matches.
            await _initTask.ConfigureAwait(false);
            if (string.IsNullOrEmpty(eventType)) return;
            if (!System.IO.Directory.Exists(_logicPath)) return;

            payload ??= "{}";

            var matches = ScriptRegistry.Instance
                .WhereEnabled(s => s.ObsEventTypes.Contains(eventType))
                .ToList();
            if (matches.Count == 0)
            {
                return;
            }

            // Build the shared var-set ONCE — every matched script gets the
            // same payload, and the engine's ExecuteScriptAsync re-clones into
            // its private _executionVars per run so per-script mutation is
            // safely isolated.
            var sharedVars = new Dictionary<string, string>
            {
                ["event.type"]      = eventType,
                ["event.data"]      = payload,
                ["obs.event_type"]  = eventType,
                ["obs.event_data"]  = payload,
                ["EventData"]       = payload,
            };

            var tasks = new List<Task>(matches.Count);
            foreach (var info in matches)
                tasks.Add(RunOneAsync(info));
            await Task.WhenAll(tasks).ConfigureAwait(false);

            async Task RunOneAsync(ScriptInfo info)
            {
                string fn = info.FileName;
                var slot = await AcquireEventSlotAsync(fn).ConfigureAwait(false);
                if (slot == EventSlotResult.TimedOut || slot == EventSlotResult.Discarded) return;
                // Caller-side re-entry flag — see AcquireEventSlotAsync.
                bool savedHeld = _eventSemaphoreHeld.Value;
                _eventSemaphoreHeld.Value = true;
                try
                {
                    string content = await ScriptRegistry.Instance
                        .GetContentAsync(info.FileName).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(content)) return;

                    var token = BeginExecutionTracked(fn);
                    try
                    {
                        _engine.EventType  = $"OBS.{eventType}";
                        _engine.ScriptFile = fn;
                        await TimedExecuteAsync(fn, content, sharedVars, token.Cts.Token);
                    }
                    catch (System.OperationCanceledException)
                    {
                        GlobalLogger.Log(
                            $"Script '{fn}' cancelled (timeout or manual).",
                            "ScriptManager", LogLevel.System);
                    }
                    finally
                    {
                        EndExecutionTracked(token);
                    }
                }
                catch (System.Exception ex)
                {
                    GlobalLogger.Error(
                        "ScriptEngine",
                        $"OBS event script error ({eventType}) in {info.FileName}",
                        ex);
                }
                finally
                {
                    _eventSemaphoreHeld.Value = savedHeld;
                    ReleaseEventSlot(slot);
                }
            }
        }
    }
#pragma warning restore CS1998
}
