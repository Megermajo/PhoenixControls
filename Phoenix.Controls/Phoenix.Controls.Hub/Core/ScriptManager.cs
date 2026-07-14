using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Hub;

namespace Phoenix.Controls.Hub.Core
{
    public partial class ScriptManager : IAsyncDisposable
    {
        // `??=` is not thread-safe. Concurrent first-touch (e.g. from a
        // WS chat callback racing a LayerWatcher InvokeReload) could each
        // construct a ScriptManager and double-run the constructor's side effects
        // (engine wiring, command registration). Double-checked locking on
        // _instanceLock; _instance kept settable to leave room for tests.
        //
        // _instance is declared `volatile` so the DCL read on the
        // fast path (no lock acquired) sees a fully-published reference. On
        // ARM64 — and on any CLR build where the JIT is free to hoist the
        // read past the constructor's stores — a non-volatile field can be
        // observed in a partially-constructed state, returning an instance
        // whose `_engine` / `_chatSemaphore` / etc. are still default.
        // `volatile` forces an acquire-fence on the read and a release-fence
        // on the write, eliminating the torn-init window.
        private static volatile ScriptManager? _instance;
        private static readonly object _instanceLock = new();
        public static ScriptManager Instance
        {
            get
            {
                var inst = _instance;
                if (inst != null) return inst;
                lock (_instanceLock)
                {
                    return _instance ??= new ScriptManager();
                }
            }
        }

        private ScriptEngine _engine;
        private string _logicPath;

        // Initial ScriptRegistry.LoadScripts used to run synchronously
        // inside the ctor. LoadScripts calls WaitForFileStable per .phx file,
        // which spins up to 30 × 50 ms = 1.5 s per locked file (antivirus
        // handoff on OneDrive-backed AppData, mid-export Architect write).
        // With N files in data/logic/ that's an N×1.5 s upper bound stalling
        // whichever thread happens to touch ScriptManager.Instance first —
        // on the splash thread or the UI thread for any test fixture or
        // singleton-bootstrap caller, this was a visible hang.
        //
        // The ctor now kicks LoadScripts onto the thread pool and stashes the
        // resulting Task here. Execute* hot paths await this gate at entry so
        // a chat / event burst arriving mid-init queues behind the registry
        // load instead of racing it. The bootstrapper still has access to
        // the gate via the static InitializeAsync() helper, so the splash
        // screen step ordering is preserved.
        private readonly Task _initTask;

        // Args bounds-check helper. Many RegisterCommand handlers
        // index into `args` after a partial Length check (or none at all); a malformed
        // script could raise IndexOutOfRangeException and tear down the engine. This
        // helper returns string.Empty for missing positions so handlers degrade
        // gracefully instead of throwing.
        internal static string ArgOrEmpty(string[] args, int i) =>
            (args != null && i >= 0 && i < args.Length) ? args[i] : string.Empty;

        // Correct Streamer.bot DoAction envelope: `action` is an OBJECT { name }
        // (or { id }), NOT a bare string. Real SB rejects the bare-string form —
        // it only ever resolved under the StreamSimulator, which is why every
        // twitch.* action node silently no-op'd against a live Streamer.bot.
        // Each live action node routes through here against a user-configured SB
        // wrapper action (the Phoenix Controls action pack); mirrors the
        // twitch.send_chat reference path. No `id` — DoAction is fire-and-forget
        // here (we don't await the response).
        internal static object NamedActionPayload(string actionName, object args)
            => new { request = "DoAction", action = new { name = actionName }, args };

        // Visual.Trigger Args expansion — when the Architect node's fixed Args:Collection
        // input is wired (or a hand-authored visual.trigger() call passes Args="a,b,c"),
        // the value lands as a single "Args" entry in EventData. Split it CSV-wise into
        // Args1=a, Args2=b, Args3=c so widget-side consumers (Result.If's When attr,
        // Text.Render's {Args1} substitution) can read positional values by name. The
        // original "Args" key is dropped after expansion. Explicit ArgsN entries already
        // present in EventData win on collision — that lets a hand-author override a
        // single positional value without rewriting the whole list.
        internal static void ExpandArgsList(Dictionary<string, string> eventData)
        {
            if (eventData == null) return;
            if (!eventData.TryGetValue("Args", out var raw) || string.IsNullOrEmpty(raw)) return;
            var parts = raw.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string key = $"Args{i + 1}";
                if (!eventData.ContainsKey(key))
                    eventData[key] = parts[i].Trim();
            }
            eventData.Remove("Args");
        }

        // Stable, collision-resistant local-var key built from a node id.
        //
        // An earlier note flagged a hypothetical "first 6 chars of the GUID"
        // slice in id-keyed handlers (Array.Push / Array.Unpack
        // / Queue.Pop, but inspection of this file confirms none of those handlers
        // currently key state by node id — they operate on the value list / global
        // queue directly). The helper below is the canonical safe-keying utility
        // for any future handler that wants to derive a per-node ephemeral var
        // name. A 6-char slice has two failure modes:
        //   1. Hex GUIDs with the same leading 6 chars collide silently — two
        //      different nodes share state.
        //   2. IDs shorter than 6 chars (or padded with non-alphanumeric chars)
        //      throw or produce malformed keys.
        //
        // Use the FULL id for keying so collisions are statistically negligible.
        // For log-readability we expose a separate 6-char prefix helper — the
        // logs stay scannable while the runtime keying stays safe. If the input
        // is null/empty we substitute "anon" so a missing id never produces an
        // empty key (which would be a single global bucket).
        //
        // Behavior change note: ephemeral execution-local state previously keyed
        // by a 6-char slice (none in current code, but documented for future use)
        // would not survive an upgrade — the key shape changes. That's acceptable
        // for execution-local result vars; persistent Vars are not in
        // scope for this helper.
        internal static string SafeIdKey(string id)
        {
            return string.IsNullOrWhiteSpace(id) ? "anon" : id.Trim();
        }
        internal static string SafeIdLogPrefix(string id)
        {
            string trimmed = (id ?? string.Empty).Trim();
            if (trimmed.Length == 0) return "anon";
            return trimmed.Length <= 6 ? trimmed : trimmed.Substring(0, 6);
        }

        private static readonly ConcurrentDictionary<string, DateTime> _lastActiveMap =
            new(StringComparer.OrdinalIgnoreCase);

        // Active script execution cancellation tokens — keyed by per-call execution ID.
        // Keying by file name collided when the same script was running for
        // multiple events at once: the first EndExecution would remove the second
        // call's CTS and the older still-running instances couldn't be cancelled.
        private readonly ConcurrentDictionary<long, CancellationTokenSource> _activeCts = new();
        private long _executionIdCounter;

        // Hub-wide shutdown token. Until HubBootstrapper is wired to call
        // SetGlobalShutdownToken at app shutdown, this defaults to CancellationToken.None
        // (i.e. it never fires). When wired, BeginExecutionTracked links every per-script
        // CTS to this token so a global Cancel() actually cancels in-flight script runs
        // instead of relying purely on Stop()/CancelAllScripts being called separately.
        // Static so the wiring layer doesn't need to plumb an instance ref.
        private static CancellationToken _globalShutdownToken = CancellationToken.None;

        /// <summary>
        /// Sets the Hub-wide shutdown <see cref="CancellationToken"/> used to link every
        /// per-script execution CTS. Call this once during Hub startup (e.g. from
        /// HubBootstrapper with the app-shutdown CTS token). Subsequent calls overwrite the prior token —
        /// existing in-flight CTSes already linked to the previous token are unaffected.
        /// Idempotent and safe to call before any script runs (CancellationToken.None is
        /// the documented "never fires" sentinel).
        /// </summary>
        public static void SetGlobalShutdownToken(CancellationToken token)
        {
            _globalShutdownToken = token;
        }

        /// <summary>
        /// Test/diagnostic accessor for the currently-installed global shutdown token.
        /// Returns CancellationToken.None until <see cref="SetGlobalShutdownToken"/> wires it.
        /// </summary>
        internal static CancellationToken GlobalShutdownToken => _globalShutdownToken;

        // Shared HttpClient. Per-call `using var client = new HttpClient()` accumulates
        // sockets in TIME_WAIT and ignores ScriptTimeoutSeconds. One process-wide handler is
        // the documented pattern. Timeout is enforced per-request via CancellationToken instead
        // of HttpClient.Timeout, so different scripts can use different ceilings.
        // AllowAutoRedirect = false. .NET's auto-redirect preserves Authorization /
        // X-API-Key / cookies across cross-host hops, leaking auth tokens to whatever
        // third party the redirect points at. We follow redirects manually via
        // SendWithManualRedirectAsync below, stripping sensitive headers on cross-host
        // redirects and capping at 5 hops.
        private static readonly HttpClient _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            // Floor; per-request CT is the real limiter. Without this the default 100 s
            // ignores ScriptTimeoutSeconds entirely.
            Timeout = TimeSpan.FromSeconds(100),
            MaxResponseContentBufferSize = 5 * 1024 * 1024  // 5 MB cap
        };

        // Sensitive headers stripped on cross-host redirect. Standard list
        // mirrors what curl --location-trusted protects against; "WWW-Authenticate"
        // is a server header so it can't appear on the request anyway. Case-insensitive
        // because callers may spell them either way.
        private static readonly HashSet<string> _sensitiveCrossHostHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Authorization",
            "Proxy-Authorization",
            "Cookie",
            "X-API-Key",
            "X-Auth-Token",
            "X-CSRF-Token",
            "X-Access-Token",
            "anthropic-version",
            "x-api-key"
        };

        private const int MaxRedirectHops = 5;

        // Outbound HTTP concurrency cap. A naked `for_each` in a
        // script that fires http.get / ai.prompt per element could spin up
        // hundreds of in-flight SendAsync calls, saturating the network
        // thread-pool and starving every other Hub subsystem that needs an
        // outbound socket (Streamer.bot WS, RemoteBridge push, Updater
        // probe). 8 is conservative-but-sane: large enough that a script
        // hitting two providers at once still parallelises, small enough
        // that a bug-loop can't take the box down. Only script-driven
        // outbound HTTP flows through this gate (every call goes via
        // SendWithManualRedirectAsync / SendStreamingWithManualRedirectAsync,
        // both of which acquire it); non-script outbound paths (Updater,
        // telemetry) keep their existing dedicated HttpClient and bypass
        // the cap.
        private const int OutboundHttpConcurrencyDefault = 8;
        private static readonly SemaphoreSlim _outboundHttpSem =
            new SemaphoreSlim(OutboundHttpConcurrencyDefault, OutboundHttpConcurrencyDefault);

        // Per-key locks for read-modify-write sites (queue.push, db.increment).
        // Fixed 256-stripe pool: the previous per-key ConcurrentDictionary grew
        // one SemaphoreSlim per distinct key (db.cell::{table}::{row}::{col},
        // cooldown:{key}, …) with no eviction — hundreds of leaked handles over
        // a long stream session. Striping bounds memory permanently; two keys
        // sharing a stripe merely serialize against each other (correctness is
        // unaffected — the lock is still held for the full RMW), and 256 stripes
        // keep that collision rate negligible at realistic concurrency (≤ a few
        // dozen simultaneous scripts).
        private const int RmwStripeCount = 256; // power of two — mask instead of modulo
        private static readonly SemaphoreSlim[] _rmwLocks = CreateRmwStripes();
        private static SemaphoreSlim[] CreateRmwStripes()
        {
            var stripes = new SemaphoreSlim[RmwStripeCount];
            for (int i = 0; i < stripes.Length; i++) stripes[i] = new SemaphoreSlim(1, 1);
            return stripes;
        }
        private static SemaphoreSlim GetRmwLock(string key)
        {
            // Deterministic, cheap, well-distributed enough for lock striping.
            // (string.GetHashCode is randomized per process — fine here, the
            // mapping only needs to be stable within one process lifetime.)
            int h = StringComparer.Ordinal.GetHashCode(key ?? string.Empty);
            return _rmwLocks[h & (RmwStripeCount - 1)];
        }

        // Hard cap on response body size for script-driven HTTP calls. 5 MB is
        // large enough for any realistic API payload but small enough that a runaway
        // server can't OOM the Hub or bloat the SQLite DB if the body is persisted.
        private const int HttpBodyMaxBytes = 5 * 1024 * 1024;

        /// <summary>
        /// Reads up to HttpBodyMaxBytes from the response stream. Truncates at the cap
        /// rather than throwing — script flows expect a string, and a noisy log is more
        /// useful than tearing down the entire script for an oversize response.
        /// </summary>
        private static async Task<string> ReadCappedAsync(HttpResponseMessage resp, CancellationToken ct)
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var ms = new System.IO.MemoryStream();
            byte[] buffer = new byte[8192];
            int read;
            int total = 0;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                int allowed = Math.Min(read, HttpBodyMaxBytes - total);
                if (allowed > 0)
                {
                    ms.Write(buffer, 0, allowed);
                    total += allowed;
                }
                if (total >= HttpBodyMaxBytes)
                {
                    GlobalLogger.Log($"HTTP response body truncated at {HttpBodyMaxBytes / 1024} KB cap.",
                        "Script", LogLevel.CriticalError);
                    break;
                }
            }
            // Use UTF-8 to match what the prior ReadAsStringAsync defaulted to for
            // application/json responses; non-UTF-8 servers will still decode lossy
            // but no worse than before.
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        // Rate-limiting semaphores — initialised in constructor from config
        private SemaphoreSlim _chatSemaphore;
        // _webhookSemaphore is shared across on_webhook,
        // on_hotkey, on_clipboard, and on_websocket fan-out. The AppConfig key
        // name (MaxConcurrentWebhookScripts) suggests it's webhook-only; in
        // practice it caps all four. Renaming the config key would break
        // user config.json files, so the rename + per-flavor split is
        // deferred (AppConfig schema work). Until then, this
        // comment is the source of truth.
        private SemaphoreSlim _webhookSemaphore;
        private SemaphoreSlim _eventSemaphore;

        // Re-entry guard for the event semaphore. Each dispatch site sets it
        // around its execution body once AcquireEventSlotAsync admits the run
        // (the CALLER must set it — an AsyncLocal write inside the async
        // acquire method mutates a copied ExecutionContext and never flows
        // back to the awaiting dispatcher's flow); AcquireEventSlotAsync
        // checks it first so a nested event.trigger from a script's body
        // doesn't try to acquire a second slot from inside the held one. With
        // cap=5 and five outer event scripts each calling event.trigger, the
        // inner calls used to wait 30s and time out (and effectively deadlock
        // the event subsystem while every outer slot was held). AsyncLocal so
        // the flag follows the async-flow, not the thread.
        private static readonly AsyncLocal<bool> _eventSemaphoreHeld = new();

        // Result of an event-slot acquire attempt — used so the matching
        // Release call only fires when we actually took a slot (re-entrant
        // calls, timeouts, policy bypasses and discards must NOT release).
        private enum EventSlotResult { Acquired, Reentered, TimedOut, Bypassed, Discarded }

        /// <summary>
        /// Acquires the event-semaphore slot, honoring re-entry and the
        /// per-script execution policy. If the current async flow already
        /// holds a slot (an outer event / chat / startup / bus / state-change
        /// / internal-event script body is executing and called
        /// event.trigger), returns Reentered without touching the semaphore —
        /// the outer slot is still doing the work. An Overlap-policy script
        /// skips the semaphore entirely and returns Bypassed; a Discard-policy
        /// script with an execution already in flight returns Discarded and
        /// the caller must skip the run. On a fresh acquire, returns Acquired
        /// (caller must Release). On a timeout, returns TimedOut (caller must
        /// skip).
        ///
        /// <para>
        /// Caller-side contract for the re-entry flag: this method cannot set
        /// <see cref="_eventSemaphoreHeld"/> itself — an AsyncLocal write
        /// inside an async callee mutates a copied ExecutionContext that is
        /// discarded when the method completes, so the awaiting dispatcher
        /// (and any nested event.trigger it spawns) would never see the flag.
        /// After an admitted result (Acquired / Bypassed / Reentered) the
        /// dispatch site must save the prior flag value, set
        /// <c>_eventSemaphoreHeld.Value = true</c> around the execution body,
        /// and restore the saved value in its <c>finally</c> — the same
        /// save/set/restore the chat path uses around its
        /// _chatSemaphore-gated run.
        /// </para>
        /// </summary>
        private async Task<EventSlotResult> AcquireEventSlotAsync(string fn)
        {
            if (_eventSemaphoreHeld.Value)
                return EventSlotResult.Reentered;
            // Policy gate sits after the re-entry check (a held outer slot
            // already models "this flow is admitted") and before EmitQueued
            // so a bypassed / discarded trigger never shows as queued.
            if (!TryAdmit(fn, out bool bypassSemaphore))
                return EventSlotResult.Discarded;
            if (bypassSemaphore)
                return EventSlotResult.Bypassed;
            if (_eventSemaphore.CurrentCount == 0)
                GlobalLogger.Log($"Rate limit reached for event scripts — queuing '{fn}'", "ScriptManager", LogLevel.System);
            EmitQueued(fn);
            if (!await _eventSemaphore.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false))
            {
                GlobalLogger.Log($"Event script '{fn}' dropped — semaphore acquire timed out (cap={ConfigManager.Current.MaxConcurrentEventScripts}).",
                    "ScriptManager", LogLevel.CriticalError);
                return EventSlotResult.TimedOut;
            }
            return EventSlotResult.Acquired;
        }

        /// <summary>
        /// Companion to <see cref="AcquireEventSlotAsync"/>. Only
        /// releases the semaphore when we actually took a slot (i.e. the
        /// caller saw Acquired); Reentered / TimedOut / Bypassed / Discarded
        /// are no-ops so the outer Acquire/Release pair stays balanced.
        /// The re-entry flag is deliberately NOT cleared here — the dispatch
        /// sites own it (save/set/restore around the execution body, see the
        /// caller-side contract on AcquireEventSlotAsync), and a blanket
        /// clear would clobber an outer flow's still-held flag.
        /// </summary>
        private void ReleaseEventSlot(EventSlotResult slot)
        {
            if (slot != EventSlotResult.Acquired) return;
            try { _eventSemaphore.Release(); } catch (ObjectDisposedException) { }
        }

        // Cooldown.check expiry timestamps — keyed by "cd::<user>" or by an exporter-supplied
        // "__nodecd::<nodeId>::<user>" alias to keep multi-node graphs from sharing one bucket.
        // Stored as ticks-since-epoch (UTC) for atomic compares.
        private static readonly ConcurrentDictionary<string, long> _cooldownExpiryUtc =
            new(StringComparer.OrdinalIgnoreCase);

        // Time.SecondsSinceLastFire backing. Per-key last-fire UTC ticks. Updated
        // atomically on each call; the engine command returns the elapsed seconds before
        // overwriting the slot. Process-lifetime store, like _cooldownExpiryUtc — does
        // not survive a Hub restart by design (uptime-gated alerts re-arm on restart).
        private static readonly ConcurrentDictionary<string, long> _lastFireUtcTicks =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Cancels all currently running scripts.</summary>
        public void CancelAllScripts()
        {
            foreach (var kv in _activeCts)
            {
                try { kv.Value.Cancel(); } catch { }
                GlobalLogger.Log($"Script execution #{kv.Key} manually cancelled.", "ScriptManager", LogLevel.System);
            }
        }

        private int _stopped;

        // Drain duration before disposing the rate-limit semaphores.
        // Gives in-flight script handlers a brief window to unwind so their
        // awaiting `WaitAsync` calls don't surface ObjectDisposedException. The
        // exact value is empirical; long enough for typical handler unwind,
        // short enough that shutdown doesn't visibly hang.
        private const int StopDrainMs = 50;

        /// <summary>
        /// Cancels active scripts and releases rate-limit semaphores. Idempotent.
        ///
        /// <para>
        /// The drain step uses <see cref="Thread.Sleep(int)"/>
        /// so this method is **not safe to call from the UI thread**. UI-reachable
        /// teardown paths must use <see cref="StopAsync"/>, which uses
        /// <see cref="Task.Delay(int)"/> for an awaitable drain. The sync path
        /// is preserved for non-UI shutdown sites (CLI, batch tools, process
        /// exit handlers where blocking the calling thread is benign).
        /// </para>
        /// </summary>
        public void Stop()
        {
            if (!BeginStop()) return;

            // Give in-flight handlers a brief window to release before disposal.
            // Without this, ObjectDisposedException can leak from awaiting `.WaitAsync` when
            // the handler races shutdown. We don't block forever; the engine's CTSes are
            // already cancelled above and that should let ongoing waits unblock.
            try { Thread.Sleep(StopDrainMs); } catch { }

            DisposeRateLimitSemaphores();
        }

        /// <summary>
        /// Async variant of <see cref="Stop"/>. Replaces the
        /// <see cref="Thread.Sleep(int)"/> drain with <see cref="Task.Delay(int)"/>
        /// so UI-thread shutdown paths can await teardown without blocking the
        /// dispatcher. Same observable semantics: cancel active scripts, dispose
        /// per-execution CTS map, brief drain, dispose rate-limit semaphores.
        /// Idempotent — second call returns immediately.
        /// </summary>
        public async Task StopAsync()
        {
            if (!BeginStop()) return;

            // Drain — awaitable so a UI thread can yield instead of blocking.
            try { await Task.Delay(StopDrainMs).ConfigureAwait(false); }
            catch { /* shutdown best-effort */ }

            DisposeRateLimitSemaphores();
        }

        /// <summary>
        /// <see cref="IAsyncDisposable"/> hook so the Hub
        /// shutdown path (MainWindow.Closed) can `await` ScriptManager teardown
        /// alongside the HubServices container. Routes to <see cref="StopAsync"/>
        /// so the drain step uses <see cref="Task.Delay(int)"/>, never the
        /// UI-blocking <see cref="Thread.Sleep(int)"/> in <see cref="Stop"/>.
        /// Idempotent: the BeginStop Interlocked.Exchange gate makes repeated
        /// DisposeAsync calls cheap no-ops.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
        }

        // Shared first half of Stop() / StopAsync(). Returns false when another
        // caller already won the Interlocked.Exchange race — that caller owns
        // the rest of teardown.
        private bool BeginStop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return false;

            CancelAllScripts();

            // Drain the active CTS map — EndExecution disposes per-script tokens
            // normally, but a cancelled-during-shutdown script may not finish in time.
            foreach (var kv in _activeCts)
            {
                try { kv.Value.Dispose(); } catch { }
            }
            _activeCts.Clear();
            return true;
        }

        private void DisposeRateLimitSemaphores()
        {
            try { _chatSemaphore.Dispose(); }    catch { }
            try { _webhookSemaphore.Dispose(); } catch { }
            try { _eventSemaphore.Dispose();   } catch { }
            DisposeSharedConcurrencyPrimitives();
        }

        // The static outbound-HTTP gate was never disposed — a process-lifetime
        // resource leak. ScriptManager is a Hub singleton, so the Stop()/StopAsync()
        // teardown path is the correct place to release it. Guarded + idempotent so
        // a stray second construction within the same process can't double-dispose.
        // The RMW stripe pool is deliberately NOT disposed: the stripes are a fixed
        // 256-element array of SemaphoreSlims (no unmanaged resources — their
        // AvailableWaitHandle is never touched), and disposing them would break any
        // in-flight RMW as well as a ScriptManager restarted within the process.
        private static int _sharedConcurrencyDisposed;
        private static void DisposeSharedConcurrencyPrimitives()
        {
            if (Interlocked.Exchange(ref _sharedConcurrencyDisposed, 1) != 0) return;
            try { _outboundHttpSem.Dispose(); } catch (ObjectDisposedException) { } catch { }
        }

        // Backwards-compatible overload returning just a CTS. New call sites should
        // use BeginExecutionTracked + EndExecutionTracked so concurrent runs of the
        // same script don't collide on a shared map key.
        private CancellationTokenSource BeginExecution(string scriptName)
        {
            return BeginExecutionTracked(scriptName).Cts;
        }

        // Pair-typed bag for the per-execution CTS + its tracking id.
        internal readonly struct ExecutionToken
        {
            public readonly long Id;
            public readonly CancellationTokenSource Cts;
            public readonly string ScriptName;
            public ExecutionToken(long id, CancellationTokenSource cts, string scriptName)
            {
                Id = id;
                Cts = cts;
                ScriptName = scriptName ?? string.Empty;
            }
        }

        // ── Script Monitor — public lifecycle hook ──────────────────────────
        // Per-script execution lifecycle telemetry consumed by ScriptMonitorWindow.
        // Fired from BeginExecutionTracked / EndExecutionTracked for the Running /
        // Finished / Error transitions, and from the OnNodeExecuted hook below for
        // the Trigger phase (a non-chat event-trigger node fired in this script's
        // graph). Subscribers run on the bus event thread — handlers must marshal
        // to the UI thread themselves (see ScriptMonitorWindow).
        public enum ScriptExecutionPhase
        {
            Running,   // Execution slot acquired (passed semaphore), engine starting
            Finished,  // Engine returned without an unhandled fault or cancellation
            Error,     // Cancelled (timeout / manual stop) — treated as red
            Trigger,   // A non-chat trigger node fired in this script (decorative ping)
            // Fired BEFORE awaiting the chat/event/webhook semaphore
            // when the slot count is at zero. The row flips to yellow / "queued"
            // until the slot is acquired (or the 30s WaitAsync times out and the
            // script is dropped — in that case there's no follow-up phase and
            // ScriptHostMonitor resets to Idle on the next registry refresh).
            // Pairs with Running, which fires from BeginExecutionTracked once
            // the semaphore is actually held.
            Queued,
            // The script's Discard execution policy dropped a new trigger
            // because an execution of the same script was already in flight.
            // Fired instead of Queued/Running — a discarded trigger never
            // enters the engine, so no Finished/Error follow-up comes. Not an
            // error: ScriptHostMonitor leaves the row state untouched.
            // Best-effort by design: the running counter is read-then-act, so
            // two simultaneous first triggers may both run — Discard is a
            // drop-while-running valve, not a mutex.
            Discarded
        }

        public sealed class ScriptLifecycleEvent
        {
            public string ScriptName { get; init; } = "";
            public ScriptExecutionPhase Phase { get; init; }
            public string? TriggerNodeType { get; init; }   // populated on Trigger phase
            public DateTime At { get; init; } = DateTime.Now;
        }

        public event Action<ScriptLifecycleEvent>? ScriptLifecycle;

        // ── Event-trigger node filter (Script Monitor) ──────────────────────
        // Returns true when a node title corresponds to an event-trigger node
        // in the Architect graph. The engine's OnNodeExecuted hook only carries
        // the node title (not its category), so we identify trigger families
        // by stable title prefix. The Twitch.ChatMessage exclusion is an
        // explicit project rule — it would
        // flood the window because every chat line fires it.
        //
        // Heuristic accepted in place of shipping NodeRegistry into the Hub:
        // any title that starts with one of the known event prefixes counts.
        // Adjust this list when new trigger families ship.
        private static readonly string[] _eventTriggerPrefixes = new[]
        {
            "Twitch.",
            "OBS.",
            "YouTube.",
            "Kick.",
            "StreamerBot.",
            "Bus.",
            "Time.",
        };
        private static readonly HashSet<string> _eventTriggerExactTitles =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "Event.Trigger",
                "Event.Executor",
                "on_startup",
                "on_chat",
            };

        internal static bool IsEventTriggerNode(string? title)
        {
            if (string.IsNullOrEmpty(title)) return false;
            // Hard exclusion — chat-message events flood the view. Covers the
            // unified Chat.Message node plus all three per-platform chat events.
            if (string.Equals(title, "Twitch.ChatMessage", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(title, "Chat.Message", StringComparison.OrdinalIgnoreCase) ||
                ChatPlatforms.IsChatEventType(title))
                return false;
            if (_eventTriggerExactTitles.Contains(title)) return true;
            foreach (var prefix in _eventTriggerPrefixes)
            {
                if (title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private void FireLifecycle(string scriptName, ScriptExecutionPhase phase, string? triggerNode = null)
        {
            var handler = ScriptLifecycle;
            if (handler == null) return;
            var evt = new ScriptLifecycleEvent
            {
                ScriptName      = scriptName ?? string.Empty,
                Phase           = phase,
                TriggerNodeType = triggerNode,
                At              = DateTime.Now
            };
            // Per-handler try/catch so a faulty subscriber can't tear down the
            // script execution path. Mirrors the pattern in Bus.
            foreach (var d in handler.GetInvocationList())
            {
                try { ((Action<ScriptLifecycleEvent>)d)(evt); }
                catch (Exception ex)
                {
                    GlobalLogger.Error("ScriptManager",
                        $"ScriptLifecycle handler threw for '{scriptName}' phase={phase}", ex);
                }
            }
        }

        // Pre-wait Queued emission for the semaphore-bound script
        // dispatch paths. Each caller previously did:
        //
        //     if (sem.CurrentCount == 0)
        //         GlobalLogger.Log($"Rate limit reached … queuing '{fn}'", …);
        //     if (!await sem.WaitAsync(30s)) { drop }
        //     var token = BeginExecutionTracked(fn);   // fires Running
        //
        // The Script Monitor row therefore never saw a Queued transition —
        // it flipped Idle → Running even when the wait actually blocked for
        // seconds behind a saturated semaphore. We now fire Queued just
        // before each WaitAsync so the dot/text family lights up yellow
        // during the wait, and BeginExecutionTracked still fires Running
        // after WaitAsync returns. The Queued ping is emitted unconditionally
        // (not gated on CurrentCount) so even a one-tick wait has a defined
        // lifecycle predecessor — the dot flicker is invisible at scale but
        // the state machine stays consistent. If the wait times out and the
        // script is dropped, the row stays Queued until ScriptHostMonitor's
        // next registry refresh clears it back to Idle — acceptable, since
        // dropped scripts already surface a CriticalError log entry in the
        // System Log panel.
        private void EmitQueued(string scriptName)
            => FireLifecycle(scriptName, ScriptExecutionPhase.Queued);

        // ── Script Monitor — per-script execution policy ────────────────────
        // The picker in ScriptMonitorWindow writes here; every semaphore-gated
        // dispatch site consults TryAdmit below before its acquire. Queue rides
        // the existing _chatSemaphore / _webhookSemaphore / _eventSemaphore
        // waits; Overlap bypasses the wait so concurrent runs of the same
        // script aren't capped; Discard drops a new trigger while an execution
        // of the script is already in flight (see the Discarded phase comment
        // in ScriptExecutionPhase for the best-effort caveat).
        public enum ExecutionPolicy
        {
            Queue,    // default — wait for the semaphore slot
            Overlap,  // run concurrently — skip the rate-limit semaphore
            Discard   // drop new triggers while the script is already running
        }

        /// <summary>True for policies whose runtime behavior is actually wired
        /// into the dispatch sites — all three are, via <see cref="TryAdmit"/>.
        /// The Script Monitor picker keeps consulting this so a future policy
        /// value can ship persisted-but-unwired without the UI lying about
        /// behavior (it appends a "(not yet enforced)" suffix when false).</summary>
        public static bool IsEnforced(ExecutionPolicy policy) => true;

        private static readonly ConcurrentDictionary<string, ExecutionPolicy> _executionPolicies =
            new(StringComparer.OrdinalIgnoreCase);

        // Per-script running-execution counter — feeds the Discard policy's
        // "is one already in flight?" check. Keyed by script file name
        // (OrdinalIgnoreCase, matching _executionPolicies); incremented in
        // BeginExecutionTracked and decremented (entry removed at zero) in
        // EndExecutionTracked — the single Begin/End funnel every dispatch
        // site routes through, so the count covers all trigger families.
        private static readonly ConcurrentDictionary<string, int> _runningCounts =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Current number of in-flight executions for a script
        /// (0 when idle). Diagnostic/test accessor over the running counter.</summary>
        internal static int GetRunningCount(string scriptName)
            => !string.IsNullOrEmpty(scriptName) && _runningCounts.TryGetValue(scriptName, out int n) ? n : 0;

        private static void IncrementRunningCount(string scriptName)
        {
            if (string.IsNullOrEmpty(scriptName)) return;
            _runningCounts.AddOrUpdate(scriptName, 1, (_, n) => n + 1);
        }

        private static void DecrementRunningCount(string scriptName)
        {
            if (string.IsNullOrEmpty(scriptName)) return;
            // Remove-at-zero keeps the map from accreting one entry per script
            // name for the process lifetime. Both arms are compare-exchange
            // shaped so a concurrent Begin can't be lost: the keyed TryRemove
            // only removes when the value still matches, and TryUpdate only
            // writes over the value we read.
            while (_runningCounts.TryGetValue(scriptName, out int n))
            {
                if (n <= 1)
                {
                    if (_runningCounts.TryRemove(new KeyValuePair<string, int>(scriptName, n)))
                        return;
                }
                else if (_runningCounts.TryUpdate(scriptName, n - 1, n))
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Pre-acquire execution-policy decision shared by every semaphore-gated
        /// dispatch site. Queue (default) admits and takes the semaphore as
        /// before. Overlap admits with <paramref name="bypassSemaphore"/> set
        /// so the caller skips the wait (and its Queued ping) entirely.
        /// Discard checks the running counter: when an execution of this script
        /// is already in flight it fires the Discarded lifecycle ping, logs one
        /// Communication-tier line, and returns false — the caller must skip
        /// the run. The counter check is read-then-act by design (see the
        /// Discarded phase comment in <see cref="ScriptExecutionPhase"/>).
        /// </summary>
        private bool TryAdmit(string scriptName, out bool bypassSemaphore)
        {
            bypassSemaphore = false;
            switch (GetExecutionPolicy(scriptName))
            {
                case ExecutionPolicy.Overlap:
                    bypassSemaphore = true;
                    return true;
                case ExecutionPolicy.Discard:
                    if (GetRunningCount(scriptName) > 0)
                    {
                        GlobalLogger.Log(
                            $"Script '{scriptName}' trigger discarded — an execution is already running (policy: Discard).",
                            "ScriptManager", LogLevel.Communication);
                        FireLifecycle(scriptName, ScriptExecutionPhase.Discarded);
                        return false;
                    }
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>Reads the configured execution policy for a script. Defaults to <see cref="ExecutionPolicy.Queue"/>.</summary>
        public ExecutionPolicy GetExecutionPolicy(string scriptName)
            => _executionPolicies.TryGetValue(scriptName ?? string.Empty, out var p) ? p : ExecutionPolicy.Queue;

        /// <summary>
        /// Persists the user's policy selection for a script. In-memory only — does
        /// not survive Hub restart by design (the picker is a runtime control, not a
        /// config setting). Returns the previously-set policy for diagnostics.
        /// </summary>
        public ExecutionPolicy SetExecutionPolicy(string scriptName, ExecutionPolicy policy)
        {
            if (string.IsNullOrEmpty(scriptName)) return ExecutionPolicy.Queue;
            ExecutionPolicy prev = GetExecutionPolicy(scriptName);
            _executionPolicies[scriptName] = policy;
            return prev;
        }

        /// <summary>
        /// Alias for <see cref="GetExecutionPolicy"/>
        /// matching the <c>GetPolicy</c> spelling at the UI call site.
        /// </summary>
        public ExecutionPolicy GetPolicy(string scriptName) => GetExecutionPolicy(scriptName);

        /// <summary>
        /// Alias for <see cref="SetExecutionPolicy"/>
        /// matching the <c>SetPolicy(name, value)</c> spelling. Returns
        /// the previously-set policy.
        /// </summary>
        public ExecutionPolicy SetPolicy(string scriptName, ExecutionPolicy policy)
            => SetExecutionPolicy(scriptName, policy);

        /// <summary>
        /// Current vs configured execution-slot usage for
        /// the semaphore that gates the given script. Returns (used, total).
        /// Chat-trigger scripts (name starts with <c>on_chat</c>) gate on
        /// <see cref="_chatSemaphore"/> (cap = AppConfig.MaxConcurrentChatScripts);
        /// every other flavor uses <see cref="_eventSemaphore"/>
        /// (cap = AppConfig.MaxConcurrentEventScripts).
        /// Cap=0 in config means "unlimited" — surfaced as <see cref="int.MaxValue"/>
        /// so the panel can render "0 / ∞" without a divide branch. Used is
        /// clamped to [0..total] so a disposal race with CurrentCount never
        /// surfaces a negative.
        /// </summary>
        public (int used, int total) GetQueueDepth(string scriptName)
        {
            string normalized = (scriptName ?? string.Empty).Trim();
            if (normalized.EndsWith(".phx", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(0, normalized.Length - 4);

            SemaphoreSlim? sem;
            int configuredCap;
            if (normalized.StartsWith("on_chat", StringComparison.OrdinalIgnoreCase))
            {
                sem = _chatSemaphore;
                configuredCap = ConfigManager.Current?.MaxConcurrentChatScripts ?? 3;
            }
            else
            {
                sem = _eventSemaphore;
                configuredCap = ConfigManager.Current?.MaxConcurrentEventScripts ?? 8;
            }
            int total = configuredCap > 0 ? configuredCap : int.MaxValue;
            int used = 0;
            try
            {
                int current = sem?.CurrentCount ?? total;
                used = Math.Max(0, Math.Min(total, total - current));
            }
            catch (ObjectDisposedException)
            {
                // Semaphore disposed mid-read (Hub shutdown). "0 held" is
                // the closest honest value at that point.
                used = 0;
            }
            return (used, total);
        }

        // Ambient handle to the CURRENT tracked execution's linked CTS + its
        // configured timeout, consumed by the delay / delay_seconds handlers to
        // re-arm the wall-clock deadline around deliberate waits
        // (ScriptTimeoutSeconds bounds ACTIVE work, not sleeps).
        //
        // AsyncLocal placement: BeginExecutionTracked is SYNCHRONOUS, so the
        // write inside it mutates the calling async method's ExecutionContext
        // and flows forward into that method's subsequent awaits — the engine
        // run and every command handler under it see the scope. A nested
        // execution (event.trigger → ExecuteInternalEventAsync →
        // BeginExecutionTracked) performs its write inside an AWAITED async
        // callee; such writes never propagate back to the caller, so the
        // nested script sees its own scope and the outer scope is restored
        // automatically when the awaited call completes.
        private static readonly AsyncLocal<ExecutionTimeoutScope?> _currentTimeoutScope = new();

        // Pair of (linked CTS, configured ScriptTimeoutSeconds) for one tracked
        // execution. TimeoutSeconds <= 0 means unlimited — the re-arm helpers
        // must not touch the CTS in that case.
        //
        // One scope is SHARED by everything on the execution's flow — including
        // parallel_begin branches, which all sleep on tokens linked to this one
        // root CTS. Re-arms therefore go through per-scope bookkeeping instead
        // of raw last-writer-wins CancelAfter: while ANY delay is outstanding
        // the deadline is the MAX of every outstanding (sleep + budget) request;
        // only when the last delay drains does the deadline drop back to one
        // normal budget. Without this, a short branch's restore clobbers a long
        // branch's extension and the long sleep gets cancelled mid-wait.
        internal sealed class ExecutionTimeoutScope
        {
            private readonly object _gate = new();
            private int _activeDelays;
            private long _maxDeadlineUtcTicks;

            internal ExecutionTimeoutScope(CancellationTokenSource cts, int timeoutSeconds)
            {
                Cts = cts;
                TimeoutSeconds = timeoutSeconds;
            }
            internal CancellationTokenSource Cts { get; }
            internal int TimeoutSeconds { get; }

            internal void ExtendForDelay(int delayMs)
            {
                lock (_gate)
                {
                    _activeDelays++;
                    long candidate = DateTime.UtcNow.Ticks
                        + TimeSpan.FromMilliseconds(delayMs).Ticks
                        + TimeSpan.FromSeconds(TimeoutSeconds).Ticks;
                    if (candidate > _maxDeadlineUtcTicks) _maxDeadlineUtcTicks = candidate;
                    // Arm INSIDE the gate: CancelAfter is last-writer-wins on
                    // the single CTS timer, so the arm order must match the
                    // bookkeeping order — armed outside the lock, a concurrent
                    // short-restore could re-arm AFTER a long-extend and leave
                    // the timer at the short deadline while the long sleep is
                    // still outstanding (false mid-sleep cancellation).
                    TryCancelAfter(Cts, RemainingLocked());
                }
            }

            internal void RestoreAfterDelay()
            {
                lock (_gate)
                {
                    if (_activeDelays > 0) _activeDelays--;
                    if (_activeDelays == 0)
                    {
                        _maxDeadlineUtcTicks = DateTime.UtcNow.Ticks
                            + TimeSpan.FromSeconds(TimeoutSeconds).Ticks;
                    }
                    TryCancelAfter(Cts, RemainingLocked());
                }
            }

            private TimeSpan RemainingLocked()
            {
                long left = _maxDeadlineUtcTicks - DateTime.UtcNow.Ticks;
                return left > 0 ? TimeSpan.FromTicks(left) : TimeSpan.Zero;
            }
        }

        // Test seams — install/inspect the ambient scope on the current async
        // flow without a full ScriptManager execution.
        internal static void SetTimeoutScopeForCurrentFlow(CancellationTokenSource? cts, int timeoutSeconds)
            => _currentTimeoutScope.Value = cts == null ? null : new ExecutionTimeoutScope(cts, timeoutSeconds);
        internal static ExecutionTimeoutScope? CurrentTimeoutScope => _currentTimeoutScope.Value;

        // CancelAfter's TimeSpan overload rejects delays above (uint.MaxValue - 2) ms;
        // clamp so an extreme configured timeout can't turn a re-arm into an
        // ArgumentOutOfRangeException mid-script. CancelAfter on an
        // already-cancelled CTS is a no-op; a disposed CTS (shutdown race) is
        // swallowed like the initial arm in BeginExecutionTracked.
        private static void TryCancelAfter(CancellationTokenSource cts, TimeSpan delay)
        {
            const double maxMs = 4294967293d; // uint.MaxValue - 2
            if (delay.TotalMilliseconds > maxMs) delay = TimeSpan.FromMilliseconds(maxMs);
            try { cts.CancelAfter(delay); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// Called by the delay / delay_seconds handlers BEFORE their sleep:
        /// pushes the current execution's deadline to (sleep + one fresh full
        /// ScriptTimeoutSeconds budget) so a deliberate wait cannot consume the
        /// timeout budget. No-op when the execution is untracked (no ambient
        /// scope) or unlimited (ScriptTimeoutSeconds &lt;= 0).
        /// </summary>
        internal static void ExtendTimeoutBudgetForDelay(int delayMs)
        {
            var scope = _currentTimeoutScope.Value;
            if (scope == null || scope.TimeoutSeconds <= 0 || delayMs <= 0) return;
            scope.ExtendForDelay(delayMs);
        }

        /// <summary>
        /// Called AFTER a delay finishes (normally OR cancelled): drains this
        /// delay's extension; when no delays remain outstanding the deadline
        /// drops back to exactly one normal ScriptTimeoutSeconds budget instead
        /// of the leftover (sleep + budget). Callers must invoke this from a
        /// finally — a sleep cut short by a LOCAL cancel (async_timeout's block
        /// CTS) would otherwise strand the root CTS at the inflated deadline
        /// while the script continues, defeating the per-script timeout guard.
        /// </summary>
        internal static void RestoreTimeoutBudgetAfterDelay()
        {
            var scope = _currentTimeoutScope.Value;
            if (scope == null || scope.TimeoutSeconds <= 0) return;
            scope.RestoreAfterDelay();
        }

        private ExecutionToken BeginExecutionTracked(string scriptName)
        {
            int timeoutSec = ConfigManager.Current.ScriptTimeoutSeconds;

            // Link the per-script CTS with the Hub-wide shutdown token so a global
            // Cancel() propagates to in-flight execution. Two independent triggers must
            // both be able to cancel: the per-script wall-clock timeout and the Hub's
            // shutdown signal. CreateLinkedTokenSource is the standard .NET pattern;
            // calling .CancelAfter on the linked source attaches the timeout to the
            // composite source so disposal cleans up the timer too.
            var shutdown = _globalShutdownToken;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            if (timeoutSec > 0)
            {
                try { cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec)); }
                catch (ObjectDisposedException) { /* race against shutdown; harmless */ }
            }
            // Publish the delay-budget scope for this execution's flow. Set even
            // when timeoutSec <= 0 (unlimited) so a nested unlimited execution
            // shadows an outer limited one instead of re-arming the outer CTS.
            _currentTimeoutScope.Value = new ExecutionTimeoutScope(cts, timeoutSec);
            long id = Interlocked.Increment(ref _executionIdCounter);
            _activeCts[id] = cts;
            // Discard-policy bookkeeping — count this execution as in-flight
            // until the matching EndExecutionTracked. Kept here (not at the
            // dispatch sites) so every trigger family rides the one funnel.
            IncrementRunningCount(scriptName);
            // Script Monitor lifecycle ping. By the time we reach Begin the
            // semaphore slot is already taken, so this is the "Running" transition.
            FireLifecycle(scriptName, ScriptExecutionPhase.Running);
            return new ExecutionToken(id, cts, scriptName);
        }

        // Legacy overload — does best-effort lookup on filename for callers
        // that haven't been migrated. Safer than the old keyed-by-name approach
        // because it cannot evict somebody else's CTS.
        private void EndExecution(string scriptName, CancellationTokenSource cts)
        {
            // Find the matching id (most recent first). If not found, the entry was
            // already removed by another path — that's fine, just dispose.
            long? foundKey = null;
            foreach (var kv in _activeCts)
                if (ReferenceEquals(kv.Value, cts)) { foundKey = kv.Key; break; }
            if (foundKey.HasValue) _activeCts.TryRemove(foundKey.Value, out _);
            // The paired BeginExecution routed through BeginExecutionTracked,
            // which incremented the running counter — balance it here too.
            DecrementRunningCount(scriptName);
            try { cts.Dispose(); } catch { }
        }

        private void EndExecutionTracked(ExecutionToken token)
        {
            _activeCts.TryRemove(token.Id, out _);
            DecrementRunningCount(token.ScriptName);
            // Script Monitor lifecycle: cancellation == red, otherwise green.
            // Unhandled exceptions are caught upstream by the catch (Exception) blocks
            // in each handler and don't propagate here; the window also subscribes to
            // GlobalLogger CriticalError entries from "ScriptEngine" to catch those.
            bool cancelled = false;
            try { cancelled = token.Cts.IsCancellationRequested; } catch { /* disposed */ }
            FireLifecycle(token.ScriptName,
                cancelled ? ScriptExecutionPhase.Error : ScriptExecutionPhase.Finished);
            try { token.Cts.Dispose(); } catch { }
        }

        /// <summary>
        /// Public hook for the catch (Exception) blocks in each script-handler
        /// path to mark a script execution as faulted for the Script Monitor. The
        /// existing log message via GlobalLogger.CriticalError still fires; this
        /// just gives the window a reliable Red ping that doesn't depend on log
        /// string parsing.
        /// </summary>
        public void ReportScriptFault(string scriptName, Exception ex)
        {
            FireLifecycle(scriptName, ScriptExecutionPhase.Error);
        }

        /// <summary>
        /// Handler for <see cref="ScriptRegistry.ScriptContentChanged"/> —
        /// re-arms ALL of the changed (or removed) script's fire-once /
        /// fire-N / alternate state: the engine's in-memory do_n(N): counters
        /// (keyed by file + line index) AND the DB-persisted, node-id-keyed
        /// global._doonce_* / _don_counter_* / _flipflop_* vars the exporter
        /// emits for the Flow.DoOnce / Flow.DoN / Flow.FlipFlop nodes. The
        /// var keys are extracted from the script text itself (prior + fresh
        /// content) — node ids only exist there — so removed nodes' state is
        /// cleaned up alongside surviving nodes' re-arm. The DB purge runs
        /// fire-and-forget behind AsyncErrorBoundary; a failed purge logs and
        /// leaves the old state for the next change to retry.
        /// Internal (not private) so tests can drive the ScriptManager →
        /// engine/DB leg without a registry round-trip. Null-guarded because
        /// a registry raise can theoretically land before the ctor finishes
        /// wiring the engine in exotic test bootstraps.
        /// </summary>
        internal void OnScriptContentChanged(ScriptContentChange? change)
        {
            if (change is null || string.IsNullOrEmpty(change.FileName)) return;
            _engine?.ResetDoNCountersForScript(change.FileName);

            var keys = ExtractNodeStateVarKeys(change.PriorContent, change.NewContent);
            if (keys.Count == 0) return;
            string fileName = change.FileName;
            AsyncErrorBoundary.SafeRunAsync(async () =>
            {
                await DB.Instance.DeleteEngineStateVariablesAsync(keys).ConfigureAwait(false);
                GlobalLogger.Log(
                    $"Script '{fileName}' changed — re-armed {keys.Count} persisted node-state var(s) (DoOnce / DoN / FlipFlop).",
                    "ScriptManager", LogLevel.Communication);
            }, "ScriptManager", $"node-state re-arm ({fileName})");
        }

        // Matches the persisted node-state vars the exporter emits: the node
        // id suffix is a dash-stripped, zero-padded 12-char id, so the tail
        // charset is strictly [A-Za-z0-9_]. Both the bare-assignment form
        // (global._doonce_X = …) and the braced-read form ({global._doonce_X})
        // contain the same match; braces are not part of it.
        private static readonly Regex NodeStateVarRegex = new(
            @"global\.(?:_don_counter_|_doonce_|_flipflop_)[A-Za-z0-9_]+",
            RegexOptions.Compiled);

        /// <summary>
        /// Distinct persisted node-state var keys referenced by either version
        /// of a script's text. Scanning BOTH sides covers pre-existing state
        /// for surviving nodes (present in both), state of nodes the edit
        /// removed (prior only), and is naturally empty for scripts that use
        /// none of the stateful flow nodes. Keys match the Vars-table form
        /// (full "global." prefix). Internal for direct test coverage.
        /// </summary>
        internal static IReadOnlyCollection<string> ExtractNodeStateVarKeys(string? priorContent, string? newContent)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            Collect(priorContent);
            Collect(newContent);
            return keys;

            void Collect(string? src)
            {
                if (string.IsNullOrEmpty(src)) return;
                foreach (Match m in NodeStateVarRegex.Matches(src))
                    keys.Add(m.Value);
            }
        }

        private async Task TimedExecuteAsync(string fn, string content, Dictionary<string, string> vars, CancellationToken token)
        {
            // Live-process instance run — merge the instance's start params (keyed
            // "param.<name>", so they never clobber event vars) so the template body
            // resolves {param.<name>} / {process.instance_id}. This is the single
            // choke point every event family AND schedule tick funnels through, so the
            // ~11 dispatchers need no per-family change. No-op for normal file scripts.
            MergeProcessInstanceVars(fn, vars);

            long memBefore = GC.GetTotalMemory(false);
            var cpuBefore = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
            var wallSw = Stopwatch.StartNew();
            await _engine.ExecuteScriptAsync(content, vars, token);
            wallSw.Stop();
            var cpuElapsed = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
            long memKb = Math.Max(0, (GC.GetTotalMemory(false) - memBefore) / 1024);
            double cpuPct = (wallSw.ElapsedMilliseconds > 0 && Environment.ProcessorCount > 0)
                ? cpuElapsed.TotalMilliseconds / (wallSw.ElapsedMilliseconds * Environment.ProcessorCount) * 100.0
                : 0.0;
            // Wall-clock script duration is what the Script panel surfaces
            // to the user — process CPU time has 15 ms granularity on
            // Windows and rendered short scripts as "0ms".
            ScriptRegistry.Instance.RecordExecution(fn, cpuPct, memKb, wallSw.Elapsed.TotalMilliseconds);
        }

        private ScriptManager()
        {
            // Engine now takes its persistence dependency explicitly.
            // Hub's composition root supplies the DB singleton; the
            // parameterless ctor is reserved for test fixtures that don't
            // care about the seam.
            _engine = new ScriptEngine(DB.Instance);

            // Process unification — bridge engine-level spawn/terminate events
            // through to ProcessManager (in-process state) and RemoteBridgeServer
            // (push to paired Viewers via VIEWER_PROCESS_STATE). The engine owns
            // the cancellation tokens and Task lifecycles; ProcessManager owns the
            // observable record + listeners; RemoteBridgeServer pushes the same
            // transitions out over the bearer-token channel. Three subscribers
            // for one event-source so each layer keeps its single responsibility.
            _engine.OnProcessSpawned += (instanceId, title) =>
            {
                ProcessManager.Instance.CreateProcess(instanceId, title);
                // Wrap the RemoteBridge broadcast so a faulted send (network drop,
                // closed bearer-token connection) lands in GlobalLogger instead of
                // surfacing as an unobserved TaskScheduler exception.
                if (HubHost.RemoteBridge is { } rb)
                    _ = AsyncErrorBoundary.SafeRunAsync(
                        () => rb.BroadcastProcessStateAsync(instanceId, "running"),
                        "ScriptManager", "RemoteBridge.BroadcastProcessState (running)");
            };
            _engine.OnProcessTerminated += (instanceId) =>
            {
                ProcessManager.Instance.TerminateProcess(instanceId);
                if (HubHost.RemoteBridge is { } rb)
                    _ = AsyncErrorBoundary.SafeRunAsync(
                        () => rb.BroadcastProcessStateAsync(instanceId, "ended"),
                        "ScriptManager", "RemoteBridge.BroadcastProcessState (ended)");
            };

            // Twitch action-pack probe — on every (re)connect, enumerate the
            // Streamer.bot actions so twitch.* action nodes can warn LOUDLY when
            // their wrapper action is missing (instead of the old silent no-op).
            // See ScriptManager.Twitch.cs. Subscribed once here in the singleton
            // ctor (process-lifetime) so there's no handler leak.
            HookStreamerBotActionProbe();

            int chatLimit    = ConfigManager.Current.MaxConcurrentChatScripts    > 0 ? ConfigManager.Current.MaxConcurrentChatScripts    : int.MaxValue;
            int webhookLimit = ConfigManager.Current.MaxConcurrentWebhookScripts > 0 ? ConfigManager.Current.MaxConcurrentWebhookScripts : int.MaxValue;
            // Generic event handlers (Twitch sub/cheer, OBS, YouTube, internal event.trigger,
            // startup, bus, state-change, scheduler) used to run behind a hard-coded cap of 5.
            // Now driven by AppConfig.MaxConcurrentEventScripts (default 8) so a busy channel
            // isn't throttled into the 30s queue-then-drop cycle. 0 = unlimited.
            int eventLimit   = ConfigManager.Current.MaxConcurrentEventScripts   > 0 ? ConfigManager.Current.MaxConcurrentEventScripts   : int.MaxValue;
            _chatSemaphore    = new SemaphoreSlim(chatLimit,    chatLimit);
            _webhookSemaphore = new SemaphoreSlim(webhookLimit, webhookLimit);
            _eventSemaphore   = new SemaphoreSlim(eventLimit,   eventLimit);

            string relativeLogic = ConfigManager.Current.LogicDirectory;
            _logicPath = Path.IsPathRooted(relativeLogic)
                ? relativeLogic
                : Path.Combine(Paths.AppBase, relativeLogic);

            // Forward NODE_EXEC events to Architect via Bus.
            // Wrap with AsyncErrorBoundary.SafeRunAsync so a transient bus
            // fault doesn't go silent and shutdown OperationCanceledException
            // doesn't pollute the error channel. Bare `_ = SomeAsync()` on a
            // hot per-node-execution path was unbounded and untracked.
            //
            // Gate emission on Bus.IsArchitectConnected.
            // When Architect is closed (the normal streaming case), every node-exec
            // marker still paid the JSON-serialize + UTF-8 encode + per-client foreach
            // inside BroadcastAsync even though zero subscribers matched. A busy chat
            // (10 msg/s × 5 matching scripts × ~50 nodes each) emitted ~2500 dead
            // broadcasts/sec. The Script Monitor lifecycle ping below is NOT gated —
            // it routes through an in-process event, not the bus.
            _engine.OnNodeExecuted += (nodeId, scriptFile) =>
            {
                if (Bus.Instance.IsArchitectConnected)
                {
                    _ = AsyncErrorBoundary.SafeRunAsync(
                        () => Bus.Instance.BroadcastAsync(new BusMessage
                        {
                            Type    = "DEBUG_NODE_EXEC",
                            Source  = "Hub",
                            Target  = "Architect",
                            Payload = System.Text.Json.JsonSerializer.Serialize(new { nodeId, scriptFile })
                        }),
                        "ScriptManager",
                        "DEBUG_NODE_EXEC broadcast");
                }

                // Script Monitor ping. nodeId here is actually the node TITLE
                // (the engine's "# [Node.Title]" marker format passes the title as
                // the first arg — see ScriptEngine.cs ProcessBlock). Filter
                // to event-trigger node titles ONLY, and explicitly drop
                // "Twitch.ChatMessage" because it would flood the view.
                if (!string.IsNullOrEmpty(scriptFile) && IsEventTriggerNode(nodeId))
                {
                    FireLifecycle(scriptFile, ScriptExecutionPhase.Trigger, nodeId);
                }
            };

            // Forward local-scope variable writes to Architect's
            // local-vars panel. Same SafeRunAsync wrapper as DEBUG_NODE_EXEC: a
            // bus fault must not abort the script's own hot loop.
            // Same Architect-connected gate as DEBUG_NODE_EXEC — DEBUG_VAR_SET fires
            // per local-scope write in script execution, same firehose shape.
            _engine.OnVariableSet += (varName, value, scriptFile) =>
            {
                if (!Bus.Instance.IsArchitectConnected) return;

                _ = AsyncErrorBoundary.SafeRunAsync(
                    () => Bus.Instance.BroadcastAsync(new BusMessage
                    {
                        Type    = "DEBUG_VAR_SET",
                        Source  = "Hub",
                        Target  = "Architect",
                        Payload = System.Text.Json.JsonSerializer.Serialize(new { varName, value, scriptFile })
                    }),
                    "ScriptManager",
                    "DEBUG_VAR_SET broadcast");
            };

            // Content-change → do_n counter invalidation. When Architect
            // re-emits a .phx (or a file is deleted) the engine's per-script
            // do_n(N): counters are keyed by line index and go stale — old
            // keys become dead weight, or pin the wrong line after an edit.
            // ScriptRegistry announces genuine content changes and removals
            // on its Refresh re-scan; drop that file's counters so the blocks
            // re-arm against the fresh line layout. Subscribed once here in
            // the singleton ctor (process-lifetime) so there's no handler leak.
            ScriptRegistry.Instance.ScriptContentChanged += OnScriptContentChanged;

            RegisterHubCommands();
            // Async script load — no longer blocks the ctor (or
            // whichever caller first touches the singleton). The hot path
            // Execute*Async methods await _initTask at entry to make sure
            // a dispatch arriving before the load completes still runs against
            // a fully-populated registry. Captured into a local before the
            // closure to avoid the "capture of `this`" trap during ctor.
            string capturedLogicPath = _logicPath;
            _initTask = Task.Run(() =>
            {
                try { ScriptRegistry.Instance.LoadScripts(capturedLogicPath); }
                catch (Exception ex)
                {
                    GlobalLogger.Error("ScriptManager",
                        "Async ScriptRegistry.LoadScripts failed during ctor init", ex);
                }
            });
        }

        /// <summary>
        /// Public gate so the bootstrapper can `await
        /// ScriptManager.InitializeAsync()` to keep splash-step ordering
        /// (i.e. ensure the registry is fully loaded before the "WIRING
        /// TRIGGERS" step that calls ExecuteOnStartupScriptsAsync). Touching
        /// .Instance starts the background load via the ctor; awaiting the
        /// returned Task blocks the caller until LoadScripts completes.
        /// Calling InitializeAsync multiple times is safe — it returns the
        /// same underlying Task each time.
        /// </summary>
        public static Task InitializeAsync() => Instance._initTask;

        // Many of the lambdas below are `async (args) => { …; return null; }`
        // even though their bodies don't await. The `async`-returns-Task shape is
        // what _engine.RegisterCommand expects (Func<string[], Task<string?>>);
        // keeping the keyword lets future maintainers add `await SomeAsyncCall()`
        // without changing the signature. Suppress the cosmetic CS1998 across the
        // whole registration block rather than scattering Task.FromResult wrappers
        // through ~50 lambdas.
#pragma warning disable CS1998
        private void RegisterHubCommands()
        {
            // ── Misc / system handlers ──────────────────────────────────────
            // Carved into ScriptManager.System.cs (log + streamerbot.do_action +
            // process.terminate + system.log + event.trigger). Closes out the
            // ScriptManager partial-class split — RegisterHubCommands is now
            // an entirely declarative orchestrator.
            RegisterSystemCommands();

            // ── process.start / process.stop (live instances) ────────────────
            RegisterProcessCommands();

            // ── db.* handlers ────────────────────────────────────────────────
            // Carved into ScriptManager.Db.cs. See that file for the contract
            // notes (quote stripping in find_row, RMW lock in
            // increment_cell, stale-result clearing in fetch_row).
            RegisterDbCommands();

            // ── giveaway.* handlers ─────────────────────────────────────────
            // Carved into ScriptManager.Giveaway.cs. create/close/ticket/winner,
            // all driving the shared GiveawayService.Instance.
            RegisterGiveawayCommands();

            // ── delay / timeout primitives ──────────────────────────────────
            // Carved into ScriptManager.Delay.cs (delay / delay_seconds /
            // timeout_check). Cancellation-token honoring on both delays
            // is preserved.
            RegisterDelayCommands();

            // ── math.* ──────────────────────────────────────────────────────
            // Carved into ScriptManager.Math.cs (random/chance/add/subtract/
            // multiply/clamp/divide/modulo/abs/min/max/floor/ceil). All 13
            // handlers register here as a single call; the original source
            // had math.random separated from the rest by a few hundred lines.
            RegisterMathCommands();

            // ── visual.* ────────────────────────────────────────────────────
            // Carved into ScriptManager.Visual.cs. All 5 HUD-broadcasting
            // handlers (set_text/trigger/trigger_queued/set_visible/set_property)
            // registered as a single call.
            RegisterVisualCommands();

            // ── twitch.* chat / channel actions ──────────────────────────────
            // Carved into ScriptManager.Twitch.cs (RegisterTwitchChatCommands).
            // send_chat / timeout / shoutout + ban / create_clip / announcement
            // — direct chat + announcement-tier handlers. 500-char body cap
            // enforced up-front on the message-bearing commands; JSON-safe
            // payload on twitch.ban.
            RegisterTwitchChatCommands();

            // discord.* commands lifted into ScriptManager.Discord.cs.
            RegisterDiscordCommands();

            // HTTP / API commands lifted into ScriptManager.Http.cs.
            RegisterHttpCommands();
            // ── Twitch polls / predictions / rewards / redemptions ────────
            // Carved into ScriptManager.Twitch.cs. Remaining twitch.* surface
            // (chat/timeout/shoutout, moderation, data lookups) is still inline.
            RegisterTwitchCommands();

            // ── State Machine ─────────────────────────────────────────────
            // Carved into ScriptManager.State.cs (state.set/get/exists/delete/
            // list_keys + public.set). The idempotent-write contract and the
            // branch-result tagging are preserved.
            RegisterStateCommands();

            // AI commands lifted into ScriptManager.AI.cs.
            RegisterAICommands();

            // text.builder dispatch stub removed. An earlier change
            // dropped the manifest entry; the dead handler stayed because
            // VerifyCommandManifest demanded it. Now that the manifest no
            // longer advertises "text.builder", the stub is unnecessary and
            // the registration is gone. Text.Builder remains resolved inline
            // by `ScriptExporter.ResolveOutputFromNode`.

            // ── text.* ──────────────────────────────────────────────────────
            // Carved into ScriptManager.Text.cs. All 9 string-manipulation
            // handlers (contains/to_upper/join_list/format/replace/split/
            // length/to_lower/parse_command) registered as a single call;
            // the original source had them in three non-contiguous clusters.
            RegisterTextCommands();

            // ── cooldown / time gates ───────────────────────────────────────
            // Carved into ScriptManager.Cooldown.cs (cooldown.check +
            // time.seconds_since_last_fire). Both consult the class-level
            // _cooldownExpiryUtc / _lastFireUtcTicks dictionaries declared
            // here; the partial sees them through partial-class state-share.
            RegisterCooldownCommands();

            // file.* commands lifted into ScriptManager.File.cs.
            RegisterFileCommands();

            // audio.* commands (play / play_tts / set_volume). Lifted into
            // ScriptManager.Audio.cs so RegisterHubCommands stays smaller; the
            // helper still binds to _engine + AudioService here in this instance.
            RegisterAudioCommands();

            // ── flow.* ──────────────────────────────────────────────────────
            // Carved into ScriptManager.Flow.cs (do_once/do_n). The
            // execution-local counter contract (ephemeral, lost on engine
            // restart) and Reset truthy-set semantics preserved.
            RegisterFlowCommands();

            // ── Type conversion — return result ──────────────────────────
            // Carved into ScriptManager.Convert.cs (to_int/to_string/to_bool/
            // to_float). parse-fail returns "0", ParseTruthy share with
            // the engine's inline wrapper preserved.
            RegisterConvertCommands();

            // ── Array / collection ops — return result ───────────────────
            // Carved into ScriptManager.Array.cs (RegisterArrayCoreCommands).
            // make/get/length/contains/push/filter — comma-separated CSV in/out.
            // OOB clamping, empty-list length, and ordinal-compare
            // contracts are preserved.
            RegisterArrayCoreCommands();

            // ── Bus / IPC ───────────────────────────────────────────────────
            // Carved into ScriptManager.Bus.cs (bus.send / bus.broadcast).
            RegisterBusCommands();

            // ── Wait for visual / event (async latent) ──────────────────────
            // Carved into ScriptManager.Wait.cs (wait_for_visual +
            // wait_for_event). Both surface the outcome through
            // global._wait_ok; wait_for_event also writes global._wait_payload.
            RegisterWaitCommands();

            // ── Chat awaiters + overlay ─────────────────────────────────────
            // Carved into ScriptManager.Chat.cs. wait_for_next / peek_recent
            // (ring-buffer drain) + overlay.push / overlay.clear (HUD broadcast).
            RegisterChatCommands();

            // ── Queue ops ───────────────────────────────────────────────────
            // Carved into ScriptManager.Queue.cs (push/pop/length/clear). The
            // RMW lock on push and the length-as-return-value contract are
            // preserved.
            RegisterQueueCommands();

            // ── Twitch moderation / channel control proxies ──────────────────
            // Carved into ScriptManager.Twitch.cs (RegisterTwitchModerationCommands).
            // Includes the user-only proxies (unban/mod/unmod/vip/unvip) plus
            // the dedicated mod/control handlers (delete_message, slow_mode,
            // follower_mode, sub_only_mode, marker, whisper, update_channel).
            RegisterTwitchModerationCommands();

            // ── Twitch Data queries ─────────────────────────────────────────
            // Carved into ScriptManager.Twitch.cs (RegisterTwitchDataCommands).
            // Read-only Helix-backed lookups: twitch.get_user/get_stream/check_role/
            // get_follow_age/last_active/get_viewers + streamerbot.get_user.
            RegisterTwitchDataCommands();

            // ── YouTube platform actions ─────────────────────────────────────
            // Carved into ScriptManager.YouTube.cs. youtube.* DoAction proxies
            // (send_chat/set_title/set_description/timeout/ban/create_poll/
            // end_poll) + the youtube.get_user data round-trip (phx_yt_* globals).
            RegisterYouTubeCommands();

            // ── Kick platform actions ────────────────────────────────────────
            // Carved into ScriptManager.Kick.cs. kick.* DoAction proxies
            // (send_chat/reply/timeout/ban/unban/untimeout/set_title/set_category/
            // delete_message/set_reward_cost/set_reward_enabled) + the
            // kick.get_user data round-trip (phx_kick_* globals).
            RegisterKickCommands();

            // ── Array slice + helpers ────────────────────────────────────────
            // Carved into ScriptManager.Array.cs (RegisterArrayHelperCommands).
            // slice/sort/shuffle/reverse/unique — CSV in/out, OOB clamping
            // on slice preserved.
            RegisterArrayHelperCommands();

            // ── OBS proxy handlers ───────────────────────────────────────────
            // Carved into ScriptManager.Obs.cs. See that file for the conventions
            // (empty-arg skip-and-log, InvariantCulture numeric forwarding,
            // result.obs_error contract, etc.).
            RegisterObsCommands();

            // Reverse-direction manifest check.
            // Runs BEFORE VerifyCommandManifest so the two complementary checks
            // execute as a pair at the end of the registration block. See
            // ScriptManager.ManifestVerify.cs for the full rationale; in short
            // this asks "every registered engine command has a manifest entry"
            // while VerifyCommandManifest asks the inverse "every manifest
            // entry has a registered engine command".
            VerifyHubCommandsAgainstManifest();

            VerifyCommandManifest();
        }
#pragma warning restore CS1998

        // Cross-check that every command name advertised by the Architect-side
        // exporter (via Phoenix.Controls.Shared.Core.CommandManifest) has a
        // corresponding RegisterCommand call above. Runs once at construction.
        // If this throws, an exporter descriptor names a command the Hub has
        // not implemented — the .phx will dispatch into the void at runtime.
        private void VerifyCommandManifest()
        {
            var missing = Phoenix.Controls.Shared.Core.CommandManifest.All.Keys
                .Where(name => !_engine.HasCommand(name))
                .OrderBy(n => n)
                .ToList();

            if (missing.Count > 0)
            {
                // Surface near-matches so a typo (e.g. `streamerbot.do_action`
                // vs `streamer_bot.do_action`) is obvious in the error.
                var lines = missing.Select(name =>
                {
                    string? hint = Phoenix.Controls.Shared.Core.CommandManifest.SuggestNearMatch(name);
                    return hint != null && !string.Equals(hint, name, StringComparison.OrdinalIgnoreCase)
                        ? $"{name}    (did you mean '{hint}'?)"
                        : name;
                });
                throw new InvalidOperationException(
                    "ScriptManager: the following commands are declared in CommandManifest "
                    + "but have no RegisterCommand call:\n  "
                    + string.Join("\n  ", lines));
            }
        }

        // Stores event arguments keyed by event name for event.trigger dispatch.
        // Promoted to ConcurrentDictionary because event.trigger
        // can run from concurrent script executions; a non-thread-safe Dictionary
        // throws InvalidOperationException ("collection was modified") under load.
        internal readonly ConcurrentDictionary<string, Dictionary<string, string>> _eventArgStore = new();

        public async Task ExecuteEventScriptAsync(string triggerName, ChatMessage chatData,
            IReadOnlyDictionary<string, string>? extraVars = null)
        {
            // Wait for the async ScriptRegistry.LoadScripts kicked off
            // in the ctor so a dispatch arriving before the load completes runs
            // against a fully-populated registry. After init this awaits
            // Task.CompletedTask and is effectively a no-op.
            await _initTask.ConfigureAwait(false);
            // 1. Check for .phx file
            string filePath = Path.Combine(_logicPath, $"{triggerName}.phx");
            if (!File.Exists(filePath))
            {
                GlobalLogger.Log($"No script found for '{triggerName}' (expected: {filePath})", "ScriptManager", LogLevel.Debug);
                return;
            }

            // 2. Build Context
            var vars = BuildChatVars(chatData);
            // Scheduler fires inject event.timestamp / event.count so the
            // Schedule.Cron/RunAt/Recurring node payload bubbles resolve (these were
            // declared autocomplete tokens that nothing populated at runtime).
            if (extraVars != null)
                foreach (var kv in extraVars) vars[kv.Key] = kv.Value;

            // 3. Persistent Load
            vars["user.points"] = await DB.Instance.GetVariableAsync($"user.{chatData.Username?.ToLowerInvariant()}.points", "0");

            // 4. Run with event type context
            string fileName = Path.GetFileName(filePath);
            var slot = await AcquireEventSlotAsync(fileName).ConfigureAwait(false);
            if (slot == EventSlotResult.TimedOut || slot == EventSlotResult.Discarded) return;
            var token = BeginExecutionTracked(fileName);
            // Caller-side re-entry flag — see AcquireEventSlotAsync. Marks this
            // flow as holding an event slot so a nested event.trigger from the
            // script body re-enters instead of waiting on a second slot.
            bool savedHeld = _eventSemaphoreHeld.Value;
            _eventSemaphoreHeld.Value = true;
            try
            {
                if (!ScriptRegistry.Instance.IsEnabled(fileName)) return;
                // Use registry's cached content (applies WaitForFileStable on first read).
                string content = await ScriptRegistry.Instance.GetContentAsync(fileName).ConfigureAwait(false);
                if (string.IsNullOrEmpty(content)) return;
                // This dispatch path is scheduler-only (SchedulerService.FireScriptAsync
                // is its sole caller). Tag the run "Schedule" — NOT "Twitch.ChatMessage" —
                // so ShouldEnterBlock enters the file's on_schedule* / on_interval blocks
                // and SKIPS any on_chat: block in the same file. Previously this masqueraded
                // as a chat run, which (combined with the schedule family being ungated in
                // ShouldEnterBlock) made a reminder script's schedule body fire on every
                // chat message. The scheduler only ever registers files that HAVE a schedule
                // header, so a schedule run always has a block to enter.
                _engine.EventType  = "Schedule";
                _engine.ScriptFile = fileName;
                await TimedExecuteAsync(fileName, content, vars, token.Cts.Token);
            }
            catch (OperationCanceledException)
            {
                GlobalLogger.Log($"Script '{triggerName}' cancelled (timeout or manual).", "ScriptManager", LogLevel.System);
            }
            catch (Exception ex)
            {
                // Carry the full exception (chain +
                // stack) to the ring buffer; label names the failing trigger
                // so SystemHistory rows stay scannable.
                GlobalLogger.Error("ScriptEngine", $"Script Error in {triggerName}", ex);
            }
            finally
            {
                _eventSemaphoreHeld.Value = savedHeld;
                EndExecutionTracked(token);
                ReleaseEventSlot(slot);
            }
        }

        /// <summary>
        /// Scans all .phx files in the logic directory and executes any that contain an
        /// on_chat: block. This allows any exported graph with a ChatMessage trigger to run
        /// without needing a fixed filename (unlike the command-based lookup).
        /// </summary>
        public async Task ExecuteOnChatScriptsAsync(ChatMessage chatData)
        {
            // Async init gate — see ExecuteEventScriptAsync.
            await _initTask.ConfigureAwait(false);
            // Shutdown-teardown guard — once BeginStop has run (or the Hub-wide shutdown
            // token is cancelled) the rate-limit semaphores are being / have been disposed.
            // Drop inbound chat cleanly so a late message can't enter a disposed _chatSemaphore
            // and abort the shutdown coordinator with ObjectDisposedException.
            if (System.Threading.Volatile.Read(ref _stopped) != 0 || GlobalShutdownToken.IsCancellationRequested) return;
            if (!Directory.Exists(_logicPath)) return;
            _lastActiveMap[chatData.Username] = DateTime.UtcNow;

            // Read from the registry's cached content (loaded
            // once and invalidated by LogicWatcher's Refresh) instead of enumerating disk
            // for every chat message.
            //
            // Hoisted vars build + user.points fetch out of the per-script
            // loop. Pre-fix: every matching chat script paid its own
            // BuildChatVars allocation AND its own DB.GetVariableAsync
            // round-trip for `user.{name}.points` even though the value is
            // identical for all scripts spawned by the same chat message —
            // 5 matching scripts on a busy chat triggered 5 serialised
            // lock acquisitions before any script body started running.
            // Resolving once and copying the dict per-script collapses
            // that to one DB hit per message and removes a measurable
            // chunk of the chat-trigger latency Majo reported.
            // Chat is multi-platform: the engine EventType is the message's own
            // platform chat event ("Twitch.ChatMessage" / "YouTube.Message" /
            // "Kick.ChatMessage") so on_chat(platform, ...) headers gate correctly.
            // For YouTube/Kick, selection is the union of on_chat scripts and
            // scripts that declare the platform chat event as a plain
            // on_event(...) — YouTube.Message used to arrive via the generic
            // event path, so hand-authored on_event(YouTube.Message) files must
            // keep firing now that the message rides the chat queue instead.
            // Twitch deliberately does NOT get the union: an
            // on_event(Twitch.ChatMessage) file never fired on chat before the
            // multi-platform pipeline (chat never entered the generic path), and
            // widening it would be a silent behavior change for existing setups.
            string chatEventType = ChatPlatforms.ChatEventTypeFor(chatData.Platform)
                ?? ChatPlatforms.TwitchChatEvent;
            bool unionEventDeclarations = chatEventType != ChatPlatforms.TwitchChatEvent;
            var matching = new List<ScriptInfo>();
            foreach (var info in ScriptRegistry.Instance.WhereEnabled(
                s => s.HasChatHeader || (unionEventDeclarations && s.EventTypes.Contains(chatEventType))))
                matching.Add(info);
            if (matching.Count == 0) return;

            var sharedVars = BuildChatVars(chatData);
            sharedVars["user.points"] = await DB.Instance.GetVariableAsync(
                $"user.{chatData.Username?.ToLowerInvariant()}.points", "0").ConfigureAwait(false);

            // Dispatch mode (AppConfig.ParallelChatScripts):
            //
            // Sequential (default) — one message's matching scripts run one
            // after another in registry order, each awaited before the next
            // starts. This is the historical contract: script B always sees
            // script A's completed SetScriptVarAsync / db writes, so
            // get-then-set on shared persisted vars is safe and chat replies
            // arrive in registry order.
            //
            // Parallel (opt-in) — fan out concurrently and await the batch;
            // _chatSemaphore inside RunChatScriptAsync (MaxConcurrentChatScripts)
            // is the concurrency bound. Faster when one script does slow I/O
            // (ai.prompt / http.*), but ordering and read-modify-write
            // atomicity across siblings are gone: scripts sharing persisted
            // vars must use atomic operations (db.increment), not get-then-set.
            // Isolation contract: each RunChatScriptAsync invocation is its own
            // async flow, so the engine's AsyncLocal-backed EventType /
            // ScriptFile writes (and _eventSemaphoreHeld) set inside it never
            // leak across sibling scripts — the same mechanism parallel_begin's
            // RunParallelBranch relies on. Faults are caught per-script inside
            // RunChatScriptAsync, so WhenAll never observes a script error.
            if (ConfigManager.Current.ParallelChatScripts)
            {
                var scriptRuns = new List<Task>(matching.Count);
                foreach (var info in matching)
                    scriptRuns.Add(RunChatScriptAsync(info.FileName, sharedVars, chatEventType));
                await Task.WhenAll(scriptRuns).ConfigureAwait(false);
            }
            else
            {
                foreach (var info in matching)
                    await RunChatScriptAsync(info.FileName, sharedVars, chatEventType).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// One matching chat script, run as its own async operation so
        /// <see cref="ExecuteOnChatScriptsAsync"/> can dispatch a message's scripts
        /// either sequentially (default) or concurrently
        /// (<see cref="AppConfig.ParallelChatScripts"/>). <paramref name="sharedVars"/>
        /// is read-only here — the per-script copy below keeps sibling scripts
        /// from seeing each other's var mutations.
        /// </summary>
        private async Task RunChatScriptAsync(string fn, Dictionary<string, string> sharedVars, string chatEventType)
        {
            try
            {
                string content = await ScriptRegistry.Instance.GetContentAsync(fn).ConfigureAwait(false);
                if (string.IsNullOrEmpty(content)) return;

                // Per-script copy so script-local var mutations don't
                // bleed into sibling scripts triggered by the same
                // chat message (vars dict is mutated by ExecuteScript).
                // Comparer matches BuildChatVars's default — ScriptEngine
                // re-wraps with OrdinalIgnoreCase at its own boundary.
                var vars = new Dictionary<string, string>(sharedVars);

                // Execution-policy gate — Overlap skips the chat semaphore
                // (and its Queued ping) so concurrent runs of this script
                // aren't capped; Discard drops the trigger while an execution
                // is already in flight. Queue keeps the wait below.
                if (!TryAdmit(fn, out bool bypassSemaphore))
                    return;

                bool acquired = false;
                if (!bypassSemaphore)
                {
                    if (_chatSemaphore.CurrentCount == 0)
                        GlobalLogger.Log($"Rate limit reached for chat scripts — queuing '{fn}'", "ScriptManager", LogLevel.System);
                    EmitQueued(fn);
                    // Bound the wait so a hung chat script can't head-of-line
                    // the entire chat queue. Mirrors the 30s pattern used by the event
                    // and webhook semaphores. Drop this script on timeout.
                    try
                    {
                        acquired = await _chatSemaphore.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        // _chatSemaphore was disposed by shutdown while this chat message was in
                        // flight — drop cleanly so teardown completes (the entry guard in
                        // ExecuteOnChatScriptsAsync covers the common case; this closes the race window).
                        return;
                    }
                    if (!acquired)
                    {
                        GlobalLogger.Log(
                            $"Chat script '{fn}' dropped — semaphore acquire timed out (cap={ConfigManager.Current.MaxConcurrentChatScripts}).",
                            "ScriptManager", LogLevel.CriticalError);
                        return;
                    }
                }
                var token = BeginExecutionTracked(fn);
                // [event-slot relief] This chat script is already gated by
                // _chatSemaphore. Mark the async flow as "holding a slot" so a nested
                // event.trigger from its body re-enters (AcquireEventSlotAsync returns
                // Reentered) instead of seizing a SEPARATE _eventSemaphore slot. Before
                // this, 3 concurrent command-bearing chat scripts could consume 3 of
                // the 5 event slots, starving real Twitch events (sub/cheer/raid) into
                // the 30s queue-then-drop cycle. Captured + restored so the flag is
                // scoped to this script's flow and doesn't leak to sibling scripts.
                bool savedHeld = _eventSemaphoreHeld.Value;
                _eventSemaphoreHeld.Value = true;
                try
                {
                    _engine.EventType  = chatEventType;
                    _engine.ScriptFile = fn;
                    await TimedExecuteAsync(fn, content, vars, token.Cts.Token);
                }
                catch (OperationCanceledException)
                {
                    GlobalLogger.Log($"Script '{fn}' cancelled (timeout or manual).", "ScriptManager", LogLevel.System);
                }
                finally
                {
                    _eventSemaphoreHeld.Value = savedHeld;
                    EndExecutionTracked(token);
                    // An Overlap bypass never took a slot — only release when we did.
                    if (acquired)
                    {
                        try { _chatSemaphore.Release(); } catch (ObjectDisposedException) { }
                    }
                }
            }
            catch (Exception ex)
            {
                // Full exception (chain + stack) to ring buffer.
                GlobalLogger.Error("ScriptEngine", $"Script Error in {fn}", ex);
            }
        }

        private static Dictionary<string, string> BuildChatVars(ChatMessage chatData)
        {
            string[] parts = chatData.Message.Split(' ');
            string cmdPart = parts[0].StartsWith("!") ? parts[0].Substring(1) : parts[0];
            string argsPart = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "";
            // Twitch chat carries usernames as `displayName` (mixed case) per
            // WS.cs, but scripts use `{user.name}` as a stable identity key —
            // most often as a DB key fragment (e.g. user.{name}.points). A
            // viewer who tweaks their display-name capitalization splits their
            // points across two DB rows under the old behaviour. Lowercase
            // here so script land sees a single canonical login regardless of
            // how the chat message was framed; the chat panel still prints
            // chatData.Username verbatim because BuildChatVars is the script-
            // side projection only.
            string userLower = chatData.Username?.ToLowerInvariant() ?? string.Empty;
            return new()
            {
                { "user.name",           userLower },
                { "user.platform",       chatData.Platform ?? ChatPlatforms.Twitch },
                { "user.is_mod",         chatData.IsMod.ToString().ToLower() },
                { "user.is_sub",         chatData.IsSub.ToString().ToLower() },
                { "user.is_broadcaster", chatData.IsBroadcaster.ToString().ToLower() },
                { "user.is_vip",         chatData.IsVip.ToString().ToLower() },
                { "user.sub_months",     chatData.SubMonths.ToString() },
                { "user.color_hex",      chatData.ColorHex },
                { "user.message",        chatData.Message },
                { "event.message",       chatData.Message },
                { "user.command",        cmdPart.ToLower() },
                { "user.args",           argsPart },
                { "event.iscommand",     parts[0].StartsWith("!").ToString().ToLower() },
                // Triggering chat message's id (Twitch/YouTube/Kick), from the
                // per-platform WS mappers. "" until a platform mapper supplies it;
                // exposed to scripts as {event.message_id} and mapped from the
                // Chat.Message node's MessageId output socket. Feeds reply / delete.
                { "event.message_id",    chatData.MessageId ?? "" },
            };
        }

        /// <summary>
        /// Executes .phx scripts for non-chat events (Sub, Raid, Cheer, Follow, etc.)
        /// </summary>
        /// <summary>
        /// Scans all .phx files for on_event({eventType}): blocks and executes any that match.
        /// Covers Twitch, OBS, YouTube, and any other Streamer.bot event types.
        /// </summary>
        public async Task ExecuteGenericEventAsync(string eventType, System.Text.Json.JsonElement data)
        {
            // Async init gate — see ExecuteEventScriptAsync.
            await _initTask.ConfigureAwait(false);

            // Track the broadcaster's own live state from the StreamOnline/Offline
            // events Hub already subscribes to (WS.TwitchEvents). twitch.is_online
            // and twitch.get_stream answer from this for the broadcaster's own
            // channel — Streamer.bot exposes no live metric for an arbitrary user.
            // Runs before the logic-dir guard so tracking works even with no
            // scripts present.
            if (eventType.Equals("Twitch.StreamOnline", StringComparison.OrdinalIgnoreCase))
                MarkBroadcasterLive(true);
            else if (eventType.Equals("Twitch.StreamOffline", StringComparison.OrdinalIgnoreCase))
                MarkBroadcasterLive(false);

            if (!Directory.Exists(_logicPath)) return;

            // Streamer.bot raises channel-point redemptions as
            // "Twitch.RewardRedemption", but the Architect Twitch.PointRedeem node
            // exports on_event(Twitch.PointRedeem), so a real redemption never
            // matched a PointRedeem script. Normalize the matcher to the Phoenix
            // node name; BuildGenericEventVars below still receives the original
            // eventType (the var-builder already accepts both names, ~line 1920).
            string matchType = eventType.Equals("Twitch.RewardRedemption", StringComparison.OrdinalIgnoreCase)
                ? "Twitch.PointRedeem" : eventType;

            // A resub IS a subscription. Streamer.bot raises renewals as a distinct
            // "Twitch.Resub", but streamers author a single Twitch.Sub(scription)
            // alert meant to cover renewals too (its month-milestone messaging only
            // makes sense for resubs). So a Resub additively fires on_event(Twitch.Sub)
            // handlers as well — and still fires a dedicated on_event(Twitch.Resub)
            // handler when one exists (matchType already == "Twitch.Resub"), so a
            // graph that splits the two isn't regressed. BuildGenericEventVars below
            // still receives the original "Twitch.Resub" so {user.tier}/{user.sub_months}
            // resolve from the resub payload.
            bool isResub = eventType.Equals("Twitch.Resub", StringComparison.OrdinalIgnoreCase);
            foreach (var info in ScriptRegistry.Instance.WhereEnabled(s =>
                s.EventTypes.Contains(matchType)
                || (isResub && s.EventTypes.Contains("Twitch.Sub"))))
            {
                try
                {
                    string content = await ScriptRegistry.Instance.GetContentAsync(info.FileName).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(content)) continue;

                    var vars = BuildGenericEventVars(eventType, data);

                    string fn = info.FileName;
                    var slot = await AcquireEventSlotAsync(fn).ConfigureAwait(false);
                    if (slot == EventSlotResult.TimedOut || slot == EventSlotResult.Discarded) continue;
                    var token = BeginExecutionTracked(fn);
                    // Caller-side re-entry flag — see AcquireEventSlotAsync.
                    bool savedHeld = _eventSemaphoreHeld.Value;
                    _eventSemaphoreHeld.Value = true;
                    try
                    {
                        _engine.EventType  = eventType;
                        _engine.ScriptFile = fn;
                        await TimedExecuteAsync(fn, content, vars, token.Cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        GlobalLogger.Log($"Script '{fn}' cancelled (timeout or manual).", "ScriptManager", LogLevel.System);
                    }
                    finally
                    {
                        _eventSemaphoreHeld.Value = savedHeld;
                        EndExecutionTracked(token);
                        ReleaseEventSlot(slot);
                    }
                }
                catch (Exception ex)
                {
                    // Label names the event type + failing script file.
                    GlobalLogger.Error("ScriptEngine", $"Script Error ({eventType}) in {info.FileName}", ex);
                }
            }
        }

        /// <summary>
        /// Scans all .phx files for on_startup: blocks and executes them.
        /// Call once after Hub is fully initialized.
        /// </summary>
        public async Task ExecuteOnStartupScriptsAsync()
        {
            // Async init gate — see ExecuteEventScriptAsync.
            await _initTask.ConfigureAwait(false);
            if (!Directory.Exists(_logicPath)) return;

            foreach (var info in ScriptRegistry.Instance.WhereEnabled(s => s.HasStartupHeader))
            {
                try
                {
                    string content = await ScriptRegistry.Instance.GetContentAsync(info.FileName).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(content)) continue;

                    string fn = info.FileName;
                    var slot = await AcquireEventSlotAsync(fn).ConfigureAwait(false);
                    if (slot == EventSlotResult.TimedOut || slot == EventSlotResult.Discarded) continue;
                    var token = BeginExecutionTracked(fn);
                    // Caller-side re-entry flag — see AcquireEventSlotAsync.
                    bool savedHeld = _eventSemaphoreHeld.Value;
                    _eventSemaphoreHeld.Value = true;
                    try
                    {
                        _engine.EventType  = "Startup";
                        _engine.ScriptFile = fn;
                        await TimedExecuteAsync(fn, content, new Dictionary<string, string>(), token.Cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        GlobalLogger.Log($"Script '{fn}' cancelled (timeout or manual).", "ScriptManager", LogLevel.System);
                    }
                    finally
                    {
                        _eventSemaphoreHeld.Value = savedHeld;
                        EndExecutionTracked(token);
                        ReleaseEventSlot(slot);
                    }
                }
                catch (Exception ex)
                {
                    // Full chain + stack to ring buffer.
                    GlobalLogger.Error("ScriptEngine", $"Startup script error in {info.FileName}", ex);
                }
            }
        }

        /// <summary>
        /// Scans all .phx files for on_bus({busEventType}): blocks and executes them.
        /// Called by Bus when an incoming bus message arrives.
        /// </summary>
        public async Task ExecuteOnBusScriptsAsync(string busEventType, Dictionary<string, string> vars)
        {
            // Async init gate — see ExecuteEventScriptAsync.
            await _initTask.ConfigureAwait(false);
            if (!Directory.Exists(_logicPath)) return;

            foreach (var info in ScriptRegistry.Instance.WhereEnabled(s => s.BusEventTypes.Contains(busEventType)))
            {
                try
                {
                    string content = await ScriptRegistry.Instance.GetContentAsync(info.FileName).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(content)) continue;

                    string fn = info.FileName;
                    var slot = await AcquireEventSlotAsync(fn).ConfigureAwait(false);
                    if (slot == EventSlotResult.TimedOut || slot == EventSlotResult.Discarded) continue;
                    var token = BeginExecutionTracked(fn);
                    // Caller-side re-entry flag — see AcquireEventSlotAsync.
                    bool savedHeld = _eventSemaphoreHeld.Value;
                    _eventSemaphoreHeld.Value = true;
                    try
                    {
                        _engine.EventType    = busEventType;
                        _engine.BusEventType = busEventType;
                        _engine.ScriptFile   = fn;
                        await TimedExecuteAsync(fn, content, vars, token.Cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        GlobalLogger.Log($"Script '{fn}' cancelled (timeout or manual).", "ScriptManager", LogLevel.System);
                    }
                    finally
                    {
                        _eventSemaphoreHeld.Value = savedHeld;
                        EndExecutionTracked(token);
                        ReleaseEventSlot(slot);
                    }
                }
                catch (Exception ex)
                {
                    // Label names the bus event + failing script.
                    GlobalLogger.Error("ScriptEngine", $"Bus script error ({busEventType}) in {info.FileName}", ex);
                }
            }
        }

        /// <summary>
        /// Scans all .phx files for on_webhook("name"): blocks and executes them.
        /// Called by HUDServer when a POST arrives at /webhook/{name}.
        /// </summary>
        public async Task ExecuteOnWebhookScriptsAsync(string webhookName, System.Text.Json.JsonElement data,
            string? rawBody = null, string httpMethod = "POST", string? requestPath = null)
        {
            // Async init gate — see ExecuteEventScriptAsync.
            await _initTask.ConfigureAwait(false);
            if (!Directory.Exists(_logicPath)) return;

            // First matching script wins; ScriptRegistry already warned at load
            // time about duplicate webhook names so this is a deliberate "don't double-fire".
            ScriptInfo? match = null;
            foreach (var info in ScriptRegistry.Instance.WhereEnabled(s => s.WebhookNames.Contains(webhookName)))
            {
                match = info;
                break;
            }
            if (match is null)
            {
                return;
            }

            // Acquire the rate-limit semaphore BEFORE reading the script content
            // and building vars. Previously the file read happened first, so the
            // "queuing" log message was emitted only after a slow disk read had already
            // completed — misleading the order of events and (worse) pre-allocating
            // work for scripts that would then back up at the semaphore. Acquiring
            // first means the queuing log line correctly precedes the wait, and we
            // don't pay for the file read until we actually have the slot.
            string fn = match.FileName;
            // Execution-policy gate — see TryAdmit. Overlap skips the webhook
            // semaphore; Discard drops the trigger while an execution of this
            // script is already in flight.
            if (!TryAdmit(fn, out bool bypassSemaphore))
                return;
            bool slotHeld = false;
            if (!bypassSemaphore)
            {
                if (_webhookSemaphore.CurrentCount == 0)
                    GlobalLogger.Log($"Rate limit reached for webhook scripts — queuing '{fn}'", "ScriptManager", LogLevel.System);
                EmitQueued(fn);
                // Bound the wait so a slow webhook can't park HTTP threads
                // forever. Drop with CriticalError if the cap holds for >30s; the HTTP
                // caller sees the request return without execution.
                if (!await _webhookSemaphore.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false))
                {
                    GlobalLogger.Log(
                        $"Webhook script '{fn}' dropped — semaphore acquire timed out (cap={ConfigManager.Current.MaxConcurrentWebhookScripts}).",
                        "ScriptManager", LogLevel.CriticalError);
                    return;
                }
                slotHeld = true;
            }

            try
            {
                string content = await ScriptRegistry.Instance.GetContentAsync(match.FileName).ConfigureAwait(false);
                if (string.IsNullOrEmpty(content)) return;

                var vars = BuildGenericEventVars($"Webhook.{webhookName}", data);
                vars["webhook.name"] = webhookName;
                // Populate the HTTP.WebhookListener node's Body/Method/Path
                // output vars. Previously only webhook.name + Twitch-shaped fields were
                // set, so {event.body}/{event.method}/{event.path} (the node's three
                // outputs) always resolved empty. rawBody falls back to the JSON text
                // for legacy callers; method is POST (the only verb the route accepts).
                vars["event.body"]    = rawBody ?? data.GetRawText();
                vars["event.payload"] = rawBody ?? data.GetRawText();
                vars["event.method"]  = httpMethod;
                vars["event.path"]    = requestPath ?? $"/webhook/{webhookName}";

                var token = BeginExecutionTracked(fn);
                // Save+restore EventType/ScriptFile so a stale value from a prior
                // execution (e.g. a different webhook whose content was empty) doesn't
                // leak into this run, and this run's value doesn't leak back out to a
                // subsequent dispatch. Mirrors ExecuteInternalEventAsync.
                string savedEventType  = _engine.EventType;
                string savedScriptFile = _engine.ScriptFile;
                try
                {
                    _engine.EventType  = $"Webhook.{webhookName}";
                    _engine.ScriptFile = fn;
                    await TimedExecuteAsync(fn, content, vars, token.Cts.Token);
                }
                catch (OperationCanceledException)
                {
                    GlobalLogger.Log($"Script '{fn}' cancelled (timeout or manual).", "ScriptManager", LogLevel.System);
                }
                finally
                {
                    _engine.EventType  = savedEventType;
                    _engine.ScriptFile = savedScriptFile;
                    EndExecutionTracked(token);
                }
            }
            catch (Exception ex)
            {
                // Label names the webhook + failing script.
                GlobalLogger.Error("ScriptEngine", $"Webhook script error ({webhookName}) in {match.FileName}", ex);
            }
            finally
            {
                // An Overlap bypass never took a slot — only release when we did.
                if (slotHeld)
                {
                    try { _webhookSemaphore.Release(); } catch (ObjectDisposedException) { }
                }
            }
        }

        /// <summary>
        /// Fires every script with an on_clipboard: block when
        /// ClipboardService receives a WM_CLIPBOARDUPDATE. Unlike hotkey /
        /// webhook, clipboard events have no discriminator — the OS doesn't
        /// differentiate by handler — so every subscriber runs in parallel
        /// (gated only by the webhook rate-limit semaphore + 30s acquire).
        /// Vars: event.text holds the new clipboard text content (empty
        /// string if non-text format); clipboard.text mirrors it.
        /// </summary>
        public async Task ExecuteOnClipboardScriptsAsync(string text)
        {
            // Async init gate — see ExecuteEventScriptAsync.
            await _initTask.ConfigureAwait(false);
            if (!Directory.Exists(_logicPath)) return;
            text ??= string.Empty;

            var matches = ScriptRegistry.Instance.WhereEnabled(s => s.HasClipboardHeader).ToList();
            if (matches.Count == 0)
            {
                return;
            }

            // Fan out to all subscribers concurrently. The docstring
            // above advertised "every subscriber runs in parallel" but the
            // pre-fix foreach awaited each script sequentially — a slow
            // subscriber forced its successors to wait. Each task acquires its
            // own semaphore slot inside RunOneAsync so the cap still rate-limits
            // overall concurrency.
            var tasks = new List<Task>(matches.Count);
            foreach (var info in matches)
                tasks.Add(RunOneAsync(info));
            await Task.WhenAll(tasks).ConfigureAwait(false);

            async Task RunOneAsync(ScriptInfo info)
            {
                string fn = info.FileName;
                // Execution-policy gate — see TryAdmit. Overlap skips the
                // shared webhook semaphore; Discard drops the trigger while
                // an execution of this script is already in flight.
                if (!TryAdmit(fn, out bool bypassSemaphore))
                    return;
                bool slotHeld = false;
                if (!bypassSemaphore)
                {
                    EmitQueued(fn);
                    if (!await _webhookSemaphore.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false))
                    {
                        GlobalLogger.Log(
                            $"Clipboard script '{fn}' dropped — semaphore acquire timed out (cap={ConfigManager.Current.MaxConcurrentWebhookScripts}).",
                            "ScriptManager", LogLevel.CriticalError);
                        return;
                    }
                    slotHeld = true;
                }
                try
                {
                    string content = await ScriptRegistry.Instance.GetContentAsync(info.FileName).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(content)) return;

                    var vars = new Dictionary<string, string>
                    {
                        ["event.text"]     = text,
                        ["clipboard.text"] = text,
                    };

                    var token = BeginExecutionTracked(fn);
                    try
                    {
                        _engine.EventType  = "Clipboard.Update";
                        _engine.ScriptFile = fn;
                        await TimedExecuteAsync(fn, content, vars, token.Cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        GlobalLogger.Log($"Script '{fn}' cancelled (timeout or manual).", "ScriptManager", LogLevel.System);
                    }
                    finally
                    {
                        EndExecutionTracked(token);
                    }
                }
                catch (Exception ex)
                {
                    // Label names the failing script file.
                    GlobalLogger.Error("ScriptEngine", $"Clipboard script error in {info.FileName}", ex);
                }
                finally
                {
                    // An Overlap bypass never took a slot — only release when we did.
                    if (slotHeld)
                    {
                        try { _webhookSemaphore.Release(); } catch (ObjectDisposedException) { }
                    }
                }
            }
        }

        /// <summary>
        /// Scans all .phx files for on_hotkey("Ctrl+Shift+P"):
        /// blocks and fires them when HotkeyService receives a WM_HOTKEY
        /// for the matching keystroke. Same rate-limit semaphore as
        /// on_webhook / on_websocket — a hotkey held down can't pile up
        /// unbounded concurrent script executions.
        /// </summary>
        public async Task ExecuteOnHotkeyScriptsAsync(string combo)
        {
            // Async init gate — see ExecuteEventScriptAsync.
            await _initTask.ConfigureAwait(false);
            if (!Directory.Exists(_logicPath)) return;

            // Kept first-match-wins semantics (unlike on_websocket).
            // Rationale: a system-wide hotkey is a
            // user-physical action; firing two scripts off a single keypress is
            // surprising, and the duplicate detector in ScriptRegistry now
            // emits a Communication-tier warning per duplicate combo
            // so the operator sees the collision at refresh time and renames
            // one. If a future workflow needs fan-out, switch this to a
            // foreach + break removal mirroring ExecuteOnWebSocketScriptsAsync.
            ScriptInfo? match = null;
            foreach (var info in ScriptRegistry.Instance.WhereEnabled(s => s.HotkeyBindings.Contains(combo)))
            {
                match = info;
                break;
            }
            if (match is null)
            {
                return;
            }

            string fn = match.FileName;
            // Execution-policy gate — see TryAdmit. Overlap skips the shared
            // webhook semaphore; Discard drops the trigger while an execution
            // of this script is already in flight.
            if (!TryAdmit(fn, out bool bypassSemaphore))
                return;
            bool slotHeld = false;
            if (!bypassSemaphore)
            {
                if (_webhookSemaphore.CurrentCount == 0)
                    GlobalLogger.Log($"Rate limit reached for hotkey scripts — queuing '{fn}'", "ScriptManager", LogLevel.System);
                EmitQueued(fn);
                if (!await _webhookSemaphore.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false))
                {
                    GlobalLogger.Log(
                        $"Hotkey script '{fn}' dropped — semaphore acquire timed out (cap={ConfigManager.Current.MaxConcurrentWebhookScripts}).",
                        "ScriptManager", LogLevel.CriticalError);
                    return;
                }
                slotHeld = true;
            }

            try
            {
                string content = await ScriptRegistry.Instance.GetContentAsync(match.FileName).ConfigureAwait(false);
                if (string.IsNullOrEmpty(content)) return;

                var vars = new Dictionary<string, string>
                {
                    ["hotkey.combo"] = combo,
                    ["event.combo"]  = combo,
                };

                var token = BeginExecutionTracked(fn);
                try
                {
                    _engine.EventType  = $"Hotkey.{combo}";
                    _engine.ScriptFile = fn;
                    await TimedExecuteAsync(fn, content, vars, token.Cts.Token);
                }
                catch (OperationCanceledException)
                {
                    GlobalLogger.Log($"Script '{fn}' cancelled (timeout or manual).", "ScriptManager", LogLevel.System);
                }
                finally
                {
                    EndExecutionTracked(token);
                }
            }
            catch (Exception ex)
            {
                // Label names the hotkey combo + script.
                GlobalLogger.Error("ScriptEngine", $"Hotkey script error ({combo}) in {match.FileName}", ex);
            }
            finally
            {
                // An Overlap bypass never took a slot — only release when we did.
                if (slotHeld)
                {
                    try { _webhookSemaphore.Release(); } catch (ObjectDisposedException) { }
                }
            }
        }

        /// <summary>
        /// Scans all .phx files for on_websocket("name"): blocks and
        /// fires them when a message arrives at /ws/&lt;name&gt; on the
        /// WebSocketServerService. Mirrors the on_webhook contract: same rate
        /// limit (MaxConcurrentWebhookScripts), same 30s acquire bound, same
        /// var population (event.payload + websocket.name).
        /// </summary>
        public async Task ExecuteOnWebSocketScriptsAsync(string wsName, System.Text.Json.JsonElement data)
        {
            // Async init gate — see ExecuteEventScriptAsync.
            await _initTask.ConfigureAwait(false);
            if (!Directory.Exists(_logicPath)) return;

            // Fan out to every matching on_websocket("name") script,
            // not just the first. Pre-fix the loop did `match = info; break;`
            // which silently dropped the second + onward subscribers to the
            // same path — surprising vs on_chat / on_event / on_clipboard which
            // all fan out. The new behavior is consistent with those headers
            // and pairs with the duplicate-detection so a user who
            // accidentally collides gets a Communication-tier warning at
            // registry-load time; an intentional fan-out is now possible and
            // the warning is the operator's "is this on purpose?" prompt.
            var matches = ScriptRegistry.Instance.WhereEnabled(s => s.WebSocketNames.Contains(wsName)).ToList();
            if (matches.Count == 0)
            {
                return;
            }

            // Build the var-set ONCE — payload is identical across all fan-out
            // subscribers, and the per-script copy semantics are the engine's
            // responsibility (ExecuteScriptAsync re-clones into _executionVars).
            string rawPayload =
                data.TryGetProperty("payload", out var rawEl) && rawEl.ValueKind == System.Text.Json.JsonValueKind.String
                    ? rawEl.GetString() ?? ""
                    : data.GetRawText();
            var sharedVars = new Dictionary<string, string>
            {
                ["event.body"]     = rawPayload,
                ["event.payload"]  = rawPayload,
                ["event.path"]     = wsName,
                ["websocket.name"] = wsName,
            };

            foreach (var match in matches)
            {
                string fn = match.FileName;
                // Execution-policy gate — see TryAdmit. Overlap skips the
                // shared webhook semaphore; Discard drops the trigger while
                // an execution of this script is already in flight.
                if (!TryAdmit(fn, out bool bypassSemaphore))
                    continue;
                bool slotHeld = false;
                if (!bypassSemaphore)
                {
                    if (_webhookSemaphore.CurrentCount == 0)
                        GlobalLogger.Log($"Rate limit reached for websocket scripts — queuing '{fn}'", "ScriptManager", LogLevel.System);
                    EmitQueued(fn);
                    if (!await _webhookSemaphore.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false))
                    {
                        GlobalLogger.Log(
                            $"WebSocket script '{fn}' dropped — semaphore acquire timed out (cap={ConfigManager.Current.MaxConcurrentWebhookScripts}).",
                            "ScriptManager", LogLevel.CriticalError);
                        continue;
                    }
                    slotHeld = true;
                }

                try
                {
                    string content = await ScriptRegistry.Instance.GetContentAsync(match.FileName).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(content)) continue;

                    // Per-script copy of the shared var dict — the engine mutates
                    // _executionVars in place when scripts assign locals, so we
                    // must not let one fan-out subscriber bleed into the next.
                    var vars = new Dictionary<string, string>(sharedVars);

                    var token = BeginExecutionTracked(fn);
                    // Save+restore EventType/ScriptFile per fan-out subscriber so
                    // one on_websocket script's state doesn't leak into the next iteration
                    // (or back out to the caller), keeping DEBUG_VAR_SET / NODE_EXEC markers
                    // tagged with the correct websocket name. Mirrors ExecuteInternalEventAsync.
                    string savedEventType  = _engine.EventType;
                    string savedScriptFile = _engine.ScriptFile;
                    try
                    {
                        _engine.EventType  = $"WebSocket.{wsName}";
                        _engine.ScriptFile = fn;
                        await TimedExecuteAsync(fn, content, vars, token.Cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        GlobalLogger.Log($"Script '{fn}' cancelled (timeout or manual).", "ScriptManager", LogLevel.System);
                    }
                    finally
                    {
                        _engine.EventType  = savedEventType;
                        _engine.ScriptFile = savedScriptFile;
                        EndExecutionTracked(token);
                    }
                }
                catch (Exception ex)
                {
                    // Label names the WS connection + failing script.
                    GlobalLogger.Error("ScriptEngine", $"WebSocket script error ({wsName}) in {match.FileName}", ex);
                }
                finally
                {
                    // An Overlap bypass never took a slot — only release when we did.
                    if (slotHeld)
                    {
                        try { _webhookSemaphore.Release(); } catch (ObjectDisposedException) { }
                    }
                }
            }
        }

        /// <summary>
        /// Scans all .phx files for on_state_change({stateName}): blocks and executes them.
        /// Called by the state.set command when a state value changes.
        /// </summary>
        public async Task ExecuteOnStateChangeScriptsAsync(string stateName, string newValue, string oldValue)
        {
            // Async init gate — see ExecuteEventScriptAsync.
            await _initTask.ConfigureAwait(false);
            if (!Directory.Exists(_logicPath)) return;

            foreach (var info in ScriptRegistry.Instance.WhereEnabled(s => s.StateChangeNames.Contains(stateName)))
            {
                try
                {
                    string content = await ScriptRegistry.Instance.GetContentAsync(info.FileName).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(content)) continue;

                    var vars = new Dictionary<string, string>
                    {
                        { "state.name",      stateName },
                        { "state.new_value", newValue  },
                        { "state.old_value", oldValue  }
                    };

                    string fn = info.FileName;
                    var slot = await AcquireEventSlotAsync(fn).ConfigureAwait(false);
                    if (slot == EventSlotResult.TimedOut || slot == EventSlotResult.Discarded) continue;
                    var token = BeginExecutionTracked(fn);
                    // Caller-side re-entry flag — see AcquireEventSlotAsync.
                    bool savedHeld = _eventSemaphoreHeld.Value;
                    _eventSemaphoreHeld.Value = true;
                    try
                    {
                        _engine.EventType  = $"StateChange.{stateName}";
                        _engine.ScriptFile = fn;
                        await TimedExecuteAsync(fn, content, vars, token.Cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        GlobalLogger.Log($"Script '{fn}' cancelled (timeout or manual).", "ScriptManager", LogLevel.System);
                    }
                    finally
                    {
                        _eventSemaphoreHeld.Value = savedHeld;
                        EndExecutionTracked(token);
                        ReleaseEventSlot(slot);
                    }
                }
                catch (Exception ex)
                {
                    // Label names the state key + failing script.
                    GlobalLogger.Error("ScriptEngine", $"State change script error ({stateName}) in {info.FileName}", ex);
                }
            }
        }

        /// <summary>Extracts common context variables from a Streamer.bot event data payload.
        /// eventType is the full Phoenix Controls event identifier ("Twitch.GiftBomb",
        /// "YouTube.Message", etc.) — used to route ambiguous payload fields like userInput
        /// (chat input vs reward input) and message (chat-style vs sub-style) to the right
        /// context var.</summary>
        // Testability seam: was `private static`, now `internal static`
        // so Phoenix.Controls.Tests (covered by InternalsVisibleTo in
        // Phoenix.Controls.Hub.csproj) can round-trip each sub/gift/raid variant
        // through the var-mapping without spinning the full ScriptManager.
        internal static Dictionary<string, string> BuildGenericEventVars(string eventType, System.Text.Json.JsonElement data)
        {
            var vars = new Dictionary<string, string>();
            // Both production callers pass non-null, but this is an internal
            // seam tests (and future callers) reach directly — and this line
            // sits ABOVE the try/catch, so a null here would escape unhandled.
            eventType ??= string.Empty;
            // Source token for every SB-routed event ("Twitch.Follow" → "twitch",
            // "Kick.Follow" → "kick") so scripts can branch cross-platform without
            // string-slicing the event type themselves.
            int sourceDot = eventType.IndexOf('.');
            if (sourceDot > 0)
                vars["user.platform"] = eventType.Substring(0, sourceDot).ToLowerInvariant();
            try
            {
                if (data.TryGetProperty("displayName", out var name))
                    vars["user.name"] = name.GetString() ?? "unknown";
                else if (data.TryGetProperty("user_name", out var uname))
                    vars["user.name"] = uname.GetString() ?? "unknown";

                // Cumulative subscribed months → {user.sub_months} (the Months socket).
                // Streamer.bot carries this under cumMonths / cumulativeMonths /
                // cumulative_months depending on SB version; older/test payloads use bare
                // "cumulative". Pre-fix only "cumulative" was read, so for real SB sub /
                // gift events the key was never written and {user.sub_months} rendered as
                // a literal placeholder (milestone messaging silently saw nothing). Probe
                // all four — same key set as WS.ResolveSubMonths.
                foreach (var monthsKey in new[] { "cumMonths", "cumulativeMonths", "cumulative_months", "cumulative" })
                {
                    if (data.TryGetProperty(monthsKey, out var months))
                    {
                        vars["user.sub_months"] = months.ToString();
                        break;
                    }
                }
                // Rename user.sub_tier → user.tier to align with the Subscription/GiftSub
                // Tier socket name emitted by ScriptExporter.cs.
                //
                // Normalize tier extraction. JsonElement.ToString() on a
                // string element returns the value WITHOUT the surrounding quotes
                // for most STJ versions, BUT the pre-fix code path made
                // {user.tier == "prime"} fragile because GetRawText would have
                // returned the quoted form. To remove all ambiguity, branch on
                // ValueKind: strings unwrap via GetString(); numbers ToString().
                // Tier normalization: Twitch / Streamer.bot encode the
                // sub tier as "1000" / "2000" / "3000" / "Prime". The user-facing
                // convention is 1 / 2 / 3 / prime, so we normalize at this single
                // ingestion point — the ONLY place a tier variable is written in the Hub —
                // which fixes the node's Tier output, alerts.phx, send_chat, and every
                // future consumer at once. Strings unwrap via GetString(); numbers
                // ToString(); NormalizeSubTier maps to the bare form (unknown/already-
                // normalized values pass through). Graphs that compared user.tier == "1000"
                // must compare against "1" now — the intended behavior change.
                if (data.TryGetProperty("tier", out var tier))
                {
                    string tierStr = tier.ValueKind == System.Text.Json.JsonValueKind.String
                        ? (tier.GetString() ?? "")
                        : tier.ToString();
                    vars["user.tier"] = NormalizeSubTier(tierStr);
                }
                if (data.TryGetProperty("bits",        out var bits))   vars["user.bits"]        = bits.ToString();
                if (data.TryGetProperty("viewers",     out var viewers))vars["user.viewers"]     = viewers.ToString();
                if (data.TryGetProperty("title",       out var reward)) vars["user.reward"]      = reward.GetString() ?? "";
                if (data.TryGetProperty("totalGifts",  out var gifts))  vars["user.total_gifts"] = gifts.ToString();

                // PointRedeem userInput goes to user.input (the redeem-input socket).
                // For chat-style events the same field name carries the chat message body.
                if (data.TryGetProperty("userInput", out var input))
                {
                    string inputText = input.GetString() ?? "";
                    if (eventType.Equals("Twitch.PointRedeem", StringComparison.OrdinalIgnoreCase) ||
                        eventType.Equals("Twitch.RewardRedemption", StringComparison.OrdinalIgnoreCase))
                        vars["user.input"] = inputText;
                    else
                        vars["user.message"] = inputText;
                }

                // Route `message` into `user.message` for ALL sub-family
                // events, not just Resub. Pre-fix a first-month Sub-with-message
                // and a GiftSub recipient acknowledgement (which Twitch lets the
                // recipient include in their on-screen thank-you) silently lost
                // the message body. Twitch's chat-side resub note + the first-sub
                // greeting + the gift acknowledgement all surface in the same
                // SB `message` slot, so the routing should be consistent across
                // Sub / Resub / GiftSub / GiftBomb / YouTube.Message.
                if (data.TryGetProperty("message", out var msgEl))
                {
                    string msgStr = msgEl.ValueKind == System.Text.Json.JsonValueKind.String
                        ? (msgEl.GetString() ?? "")
                        : msgEl.ToString();
                    // Twitch.Cheer added to the list. SB cheer events
                    // carry the bits message in the same `message` slot, but the
                    // Cheer node's Message output (mapped to {user.message}) was
                    // never populated because Cheer was excluded here.
                    if (eventType.Equals("Twitch.Sub", StringComparison.OrdinalIgnoreCase) ||
                        eventType.Equals("Twitch.Resub", StringComparison.OrdinalIgnoreCase) ||
                        eventType.Equals("Twitch.GiftSub", StringComparison.OrdinalIgnoreCase) ||
                        eventType.Equals("Twitch.GiftBomb", StringComparison.OrdinalIgnoreCase) ||
                        eventType.Equals("Twitch.Cheer", StringComparison.OrdinalIgnoreCase) ||
                        eventType.Equals("YouTube.Message", StringComparison.OrdinalIgnoreCase))
                        vars["user.message"] = msgStr;
                }

                // GiftBomb / GiftSub specific extracts. Streamer.bot uses
                // "gifter" / "recipient" / "gifts" depending on event variant; we try
                // each likely key shape rather than baking in one assumption.
                if (eventType.Equals("Twitch.GiftBomb", StringComparison.OrdinalIgnoreCase))
                {
                    // Gifter: explicit "gifter" key (string or nested user object), else
                    // the acting user — on a gift event the actor IS the gifter. Pre-fix
                    // this only probed a bare "gifter" key, so SB payloads that carry the
                    // gifter as the flattened displayName / userName (the common shape)
                    // left {user.gifter} unresolved as a literal token.
                    string gifter = ResolveGifter(data);
                    if (!string.IsNullOrEmpty(gifter)) vars["user.gifter"] = gifter;

                    // Count: SB exposes either "gifts" or "count"
                    if (data.TryGetProperty("gifts", out var giftsEl))
                        vars["user.count"] = giftsEl.ToString();
                    else if (data.TryGetProperty("count", out var countEl))
                        vars["user.count"] = countEl.ToString();

                    // Propagate the isAnonymous flag for community-gift
                    // bursts. SB sets "isAnonymous":true plus
                    // gifter.displayName="AnAnonymousGifter" when the gifter elected
                    // the Twitch anonymous-gift affordance. Pre-fix, scripts could
                    // only string-match the literal username (which Twitch can
                    // change at will). Surfaced as "true"/"false" string for
                    // engine-side {user.is_anonymous} comparisons.
                    SetAnonymousFlag(vars, data);
                }
                else if (eventType.Equals("Twitch.GiftSub", StringComparison.OrdinalIgnoreCase))
                {
                    // Gifter: explicit "gifter" key, else the acting user (the gifter is
                    // the actor on a gift event). Recipient: probe SB's documented key
                    // variants (recipientUser / recipientDisplayName / …). Pre-fix both
                    // probed only bare "gifter"/"recipient" keys, so the common SB shapes
                    // left {user.gifter}/{user.recipient} as literal placeholders in chat.
                    string gifter = ResolveGifter(data);
                    if (!string.IsNullOrEmpty(gifter)) vars["user.gifter"] = gifter;

                    string recipient = ResolveRecipient(data);
                    if (!string.IsNullOrEmpty(recipient)) vars["user.recipient"] = recipient;

                    // Same isAnonymous propagation as GiftBomb.
                    SetAnonymousFlag(vars, data);
                }

                // Catalog-driven extraction for the YouTube/Kick event surface.
                // The bespoke Twitch probes above stay behavior-frozen; every
                // event declared in PlatformEventCatalog resolves its vars (and
                // the raw event.payload fallback) through the shared resolver.
                var platformDef = PlatformEventCatalog.Find(eventType);
                if (platformDef is not null)
                    PlatformEventVarResolver.Apply(platformDef, data, vars);
            }
            catch { /* Best effort extraction */ }
            return vars;
        }

        // Extract the Streamer.bot `isAnonymous` field (bool, or
        // string "true"/"false" in older SB releases) and surface it as
        // user.is_anonymous = "true" / "false". Always writes the key (default
        // "false") so script authors don't have to null-check before comparing.
        private static void SetAnonymousFlag(Dictionary<string, string> vars, System.Text.Json.JsonElement data)
        {
            bool isAnon = false;
            if (data.TryGetProperty("isAnonymous", out var anonEl))
            {
                switch (anonEl.ValueKind)
                {
                    case System.Text.Json.JsonValueKind.True:  isAnon = true;  break;
                    case System.Text.Json.JsonValueKind.False: isAnon = false; break;
                    case System.Text.Json.JsonValueKind.String:
                        isAnon = bool.TryParse(anonEl.GetString(), out var parsed) && parsed;
                        break;
                }
            }
            vars["user.is_anonymous"] = isAnon ? "true" : "false";
        }

        // Normalizes a Streamer.bot / Twitch sub-tier string to the user-facing
        // 1 / 2 / 3 / prime form. Raw encodings are "1000" / "2000" / "3000" / "Prime";
        // some SB feeds send "tier 1" or the already-bare "1". Unknown values pass
        // through unchanged so a future tier or a non-standard payload isn't swallowed.
        internal static string NormalizeSubTier(string? raw)
        {
            string t = (raw ?? string.Empty).Trim();
            if (t.Length == 0) return t;
            // Some SB feeds prefix "tier " (e.g. "tier 1"); strip it before mapping.
            if (t.StartsWith("tier ", StringComparison.OrdinalIgnoreCase))
                t = t.Substring(5).Trim();
            return t switch
            {
                "1000" => "1",
                "2000" => "2",
                "3000" => "3",
                _ when t.Equals("prime", StringComparison.OrdinalIgnoreCase) => "prime",
                _ => t   // "1"/"2"/"3" and unknown values pass through
            };
        }

        // Pulls a display name from a JSON value that is either a bare string or a
        // nested user object ({displayName|name|login|userName}). Returns "" if none.
        private static string ExtractName(System.Text.Json.JsonElement el)
        {
            if (el.ValueKind == System.Text.Json.JsonValueKind.String)
                return el.GetString() ?? "";
            if (el.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var k in new[] { "displayName", "name", "login", "userName" })
                    if (el.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var s = v.GetString();
                        if (!string.IsNullOrEmpty(s)) return s!;
                    }
            }
            return "";
        }

        // Resolves the acting user's name from an SB event payload — mirrors
        // WS.TryExtractActor's probe order (nested user{name|login}, then the flattened
        // displayName / userName / user_name fields). On a gift event the actor IS the
        // gifter, so this doubles as the gifter fallback.
        private static string ResolveActorName(System.Text.Json.JsonElement data)
        {
            if (data.TryGetProperty("user", out var u) && u.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var n = ExtractName(u);
                if (!string.IsNullOrEmpty(n)) return n;
            }
            foreach (var k in new[] { "displayName", "userName", "user_name" })
                if (data.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (!string.IsNullOrEmpty(s)) return s!;
                }
            return "";
        }

        // Gifter for a gift / gift-bomb event: explicit "gifter" key (string or nested
        // user object), else the acting user.
        private static string ResolveGifter(System.Text.Json.JsonElement data)
        {
            if (data.TryGetProperty("gifter", out var g))
            {
                var n = ExtractName(g);
                if (!string.IsNullOrEmpty(n)) return n;
            }
            return ResolveActorName(data);
        }

        // Recipient of a gift sub: probe the documented SB key variants (each a string
        // or nested user object). Raw Twitch carries the recipient on the paired
        // channel.subscribe event; SB stitches it onto the gift event as recipientUser*.
        private static string ResolveRecipient(System.Text.Json.JsonElement data)
        {
            foreach (var k in new[] { "recipient", "recipientUser", "recipientDisplayName",
                                      "recipientUserName", "recipient_user", "recipientName" })
                if (data.TryGetProperty(k, out var r))
                {
                    var n = ExtractName(r);
                    if (!string.IsNullOrEmpty(n)) return n;
                }
            return "";
        }

        /// <summary>
        /// Fires all .phx scripts that contain an on_event(Internal.{eventName}) block.
        /// Payload key=value pairs are merged into context vars as event.arg.{key}.
        /// </summary>
        public async Task<Dictionary<string, string>> ExecuteInternalEventAsync(string fullEventType, Dictionary<string, string> payload)
        {
            // Async init gate — see ExecuteEventScriptAsync.
            await _initTask.ConfigureAwait(false);
            // fullEventType = "Internal.GambleResult"
            var returnVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(_logicPath)) return returnVars;

            foreach (var info in ScriptRegistry.Instance.WhereEnabled(s => s.EventTypes.Contains(fullEventType)))
            {
                try
                {
                    string content = await ScriptRegistry.Instance.GetContentAsync(info.FileName).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(content)) continue;

                    var vars = new Dictionary<string, string>();
                    // Inject payload as event.arg.* vars
                    foreach (var kv in payload)
                        vars[$"event.arg.{kv.Key}"] = kv.Value;

                    string fn = info.FileName;
                    // AcquireEventSlotAsync returns Reentered when the
                    // current async flow already holds the event semaphore (i.e.
                    // this call came from an outer event script's body via
                    // event.trigger). Re-entry skips the second acquire so a
                    // chain of 5 event scripts each calling event.trigger does
                    // not self-deadlock at cap=5.
                    var slot = await AcquireEventSlotAsync(fn).ConfigureAwait(false);
                    if (slot == EventSlotResult.TimedOut || slot == EventSlotResult.Discarded) continue;
                    var token = BeginExecutionTracked(fn);
                    // Caller-side re-entry flag — see AcquireEventSlotAsync.
                    bool savedHeld = _eventSemaphoreHeld.Value;
                    _eventSemaphoreHeld.Value = true;
                    // Save+restore EventType, BusEventType, AND ScriptFile so nested
                    // event.trigger calls don't leak the inner state out to the outer caller
                    // and don't tag NODE_EXEC markers with the wrong file.
                    string savedEventType    = _engine.EventType;
                    string savedBusEventType = _engine.BusEventType;
                    string savedScriptFile   = _engine.ScriptFile;
                    try
                    {
                        _engine.EventType    = fullEventType;
                        _engine.BusEventType = ""; // Internal events don't carry a bus type.
                        _engine.ScriptFile   = fn;
                        long _memBefore = GC.GetTotalMemory(false);
                        var _cpuBefore = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
                        var _sw = Stopwatch.StartNew();
                        var execVars = await _engine.ExecuteScriptAsync(content, vars, token.Cts.Token);
                        _sw.Stop();
                        var _cpuElapsed = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - _cpuBefore;
                        double _cpuPct = _sw.ElapsedMilliseconds > 0
                            ? _cpuElapsed.TotalMilliseconds / (_sw.ElapsedMilliseconds * Environment.ProcessorCount) * 100.0
                            : 0.0;
                        ScriptRegistry.Instance.RecordExecution(fn, _cpuPct, Math.Max(0, (GC.GetTotalMemory(false) - _memBefore) / 1024), _sw.Elapsed.TotalMilliseconds);

                        foreach (var kv in execVars.Where(kv =>
                            kv.Key.StartsWith("event.ret.", StringComparison.OrdinalIgnoreCase)))
                            returnVars[kv.Key] = kv.Value;
                    }
                    catch (OperationCanceledException)
                    {
                        GlobalLogger.Log($"Script '{fn}' cancelled (timeout or manual).", "ScriptManager", LogLevel.System);
                    }
                    finally
                    {
                        _eventSemaphoreHeld.Value = savedHeld;
                        _engine.EventType    = savedEventType;
                        _engine.BusEventType = savedBusEventType;
                        _engine.ScriptFile   = savedScriptFile;
                        EndExecutionTracked(token);
                        ReleaseEventSlot(slot);
                    }
                }
                catch (Exception ex)
                {
                    // Label names the internal event type + script.
                    GlobalLogger.Error("ScriptEngine", $"Internal event error ({fullEventType}) in {info.FileName}", ex);
                }
            }
            return returnVars;
        }
        /// <summary>
        /// Parses a "key=value,key=value" header string and adds headers to the HttpClient.
        /// Gracefully skips malformed pairs.
        /// </summary>
        // Tries multiple candidate property names in order; returns first non-empty string found.
        private static string? TryStr(JsonElement el, params string[] keys)
        {
            foreach (var k in keys)
                if (el.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                else if (el.TryGetProperty(k, out var n) &&
                         (n.ValueKind == JsonValueKind.Number || n.ValueKind == JsonValueKind.True || n.ValueKind == JsonValueKind.False))
                    return n.ToString();
            return null;
        }

        private static bool TryBool(JsonElement el, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (!el.TryGetProperty(k, out var v)) continue;
                if (v.ValueKind == JsonValueKind.True)  return true;
                if (v.ValueKind == JsonValueKind.False) return false;
                if (v.ValueKind == JsonValueKind.String)
                    return v.GetString()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
            }
            return false;
        }

        private static void ApplyHeaders(HttpClient client, string headerString)
        {
            if (string.IsNullOrWhiteSpace(headerString)) return;
            foreach (var pair in headerString.Split(','))
            {
                ParseHeaderPair(pair, out var key, out var val);
                if (key == null) continue;
                client.DefaultRequestHeaders.TryAddWithoutValidation(key, val);
            }
        }

        /// <summary>
        /// Per-request header application (the shared HttpClient cannot use
        /// DefaultRequestHeaders without leaking auth across concurrent calls).
        /// Headers go on the request message and the message body's content
        /// headers, depending on which header it is.
        /// </summary>
        private static void ApplyHeadersToRequest(HttpRequestMessage req, string headerString)
        {
            if (string.IsNullOrWhiteSpace(headerString)) return;
            foreach (var pair in headerString.Split(','))
            {
                ParseHeaderPair(pair, out var key, out var val);
                if (key == null) continue;
                // Try request headers first; fall back to content headers (e.g. Content-Type).
                if (!req.Headers.TryAddWithoutValidation(key, val))
                    req.Content?.Headers.TryAddWithoutValidation(key, val);
            }
        }

        /// <summary>
        /// Manual redirect follower with cross-host header stripping.
        /// Replaces the auto-redirect path the shared HttpClient previously used.
        /// On a 3xx response, builds a fresh HttpRequestMessage for the new URL,
        /// preserving the method (or GET for 303) and headers — except sensitive
        /// auth headers, which are stripped if the redirect target is a different
        /// host than the original request. Caps at <see cref="MaxRedirectHops"/>
        /// hops; on overflow returns the last response (the caller will see the
        /// 3xx body and can branch on result.http_status).
        /// </summary>
        internal static async Task<HttpResponseMessage> SendWithManualRedirectAsync(
            HttpRequestMessage initial, CancellationToken ct, bool allowLoopback = false)
        {
            // SSRF gate. The initial URL and every redirect target must
            // pass UrlImageCache.ValidateUrlForOutboundAsync — same allowlist /
            // blocklist the asset-proxy path uses (scheme allowlist + loopback +
            // RFC1918 + 169.254/16 cloud-metadata + IPv6 ULA reject). Without
            // this, a chat-triggered http.get({event.arg.message}) could reach
            // 127.0.0.1:18081 (Bus), 127.0.0.1:18080 (HUDServer control), or
            // 169.254.169.254 (cloud metadata in VM). Validating each redirect
            // hop separately closes the 30x-to-internal-host bypass.
            // allowLoopback=true is reserved for callers wired to a trusted
            // local service (currently only the Ollama AI provider branch).
            string? initialUrl = initial.RequestUri?.AbsoluteUri;
            if (string.IsNullOrEmpty(initialUrl))
                throw new HttpRequestException("HTTP request has no RequestUri.");
            var (preOk, preReason) = await UrlImageCache.ValidateUrlForOutboundAsync(initialUrl, ct, allowLoopback).ConfigureAwait(false);
            if (!preOk)
                throw new HttpRequestException($"SSRF gate rejected {initialUrl}: {preReason}");

            // Acquire the outbound-HTTP gate before any SendAsync hop.
            // Held across all redirect hops of this single logical request so
            // we count "one logical script HTTP call" against the cap, not
            // "one SendAsync invocation" (otherwise a redirect chain
            // double-charges the script and a burst of 3xx responses could
            // still saturate the pool). Released in the finally block once
            // either the final response is returned or an exception unwinds.
            await _outboundHttpSem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                HttpRequestMessage current = initial;
                HttpResponseMessage resp = await _http.SendAsync(current, ct).ConfigureAwait(false);
                int hops = 0;
                while (IsRedirectStatus(resp.StatusCode) && resp.Headers.Location is not null && hops < MaxRedirectHops)
                {
                    hops++;
                    Uri originalHost = current.RequestUri ?? new Uri("http://_none_/");
                    Uri target = resp.Headers.Location.IsAbsoluteUri
                        ? resp.Headers.Location
                        : new Uri(originalHost, resp.Headers.Location);

                    // Re-validate every redirect target. A 30x that
                    // points at 169.254.169.254 / 127.0.0.1 / 10.0.0.0/8 must
                    // be refused exactly the same way the initial URL would be.
                    // allowLoopback propagates so an Ollama-configured caller
                    // can still follow a 30x to a sibling local endpoint.
                    var (hopOk, hopReason) = await UrlImageCache.ValidateUrlForOutboundAsync(target.AbsoluteUri, ct, allowLoopback).ConfigureAwait(false);
                    if (!hopOk)
                    {
                        resp.Dispose();
                        if (current != initial) current.Dispose();
                        throw new HttpRequestException($"SSRF gate rejected redirect to {target}: {hopReason}");
                    }

                    bool crossHost = !string.Equals(originalHost.Host, target.Host, StringComparison.OrdinalIgnoreCase);

                    // 303 forces GET; everything else preserves the original verb.
                    HttpMethod nextMethod = resp.StatusCode == System.Net.HttpStatusCode.SeeOther
                        ? HttpMethod.Get
                        : current.Method;

                    var next = new HttpRequestMessage(nextMethod, target);
                    foreach (var h in current.Headers)
                    {
                        if (crossHost && _sensitiveCrossHostHeaders.Contains(h.Key))
                        {
                            GlobalLogger.Log(
                                $"HTTP redirect: stripping sensitive header '{h.Key}' on cross-host hop ({originalHost.Host} → {target.Host}).",
                                "Script", LogLevel.Communication);
                            continue;
                        }
                        next.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    }
                    // 303 drops the body; other 3xx replays it.
                    if (nextMethod != HttpMethod.Get && current.Content is not null)
                    {
                        next.Content = current.Content;
                    }

                    resp.Dispose();
                    if (current != initial) current.Dispose();
                    current = next;
                    resp = await _http.SendAsync(current, ct).ConfigureAwait(false);
                }

                if (current != initial) current.Dispose();
                return resp;
            }
            finally
            {
                // Release can throw ObjectDisposedException if the
                // semaphore was disposed during shutdown; swallow that race.
                try { _outboundHttpSem.Release(); } catch (ObjectDisposedException) { }
            }
        }

        private static bool IsRedirectStatus(System.Net.HttpStatusCode code) => code switch
        {
            System.Net.HttpStatusCode.MovedPermanently  => true, // 301
            System.Net.HttpStatusCode.Found             => true, // 302
            System.Net.HttpStatusCode.SeeOther          => true, // 303
            System.Net.HttpStatusCode.TemporaryRedirect => true, // 307
            System.Net.HttpStatusCode.PermanentRedirect => true, // 308
            _ => false
        };

        /// <summary>
        /// Strips a single matching pair of surrounding ASCII single
        /// or double quotes from the input. Used to normalise args that may have
        /// been emitted by the Resolve+StripQuotes path or the Materialize path,
        /// so the runtime accepts both forms identically.
        /// </summary>
        internal static string StripBareQuotes(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 2) return s;
            char first = s[0], last = s[s.Length - 1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
                return s.Substring(1, s.Length - 2);
            return s;
        }

        /// <summary>
        /// Single source of truth for the opt-in case-insensitive flag used
        /// by array.contains / array.filter. Accepts the canonical truthy set plus
        /// the explicit literal "ignore_case" / "ignorecase" / "i" so callers can
        /// document intent in their script.
        /// </summary>
        private static bool IsIgnoreCaseFlag(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            string t = s.Trim();
            if (t.Equals("ignore_case", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Equals("ignorecase",  StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Equals("i",           StringComparison.OrdinalIgnoreCase)) return true;
            return ScriptEngine.ParseTruthy(t);
        }

        /// <summary>
        /// Tokenizes a JSON dot-path with `\.` (backslash-dot) escape for
        /// literal dots in keys, and `\\` for a literal backslash. Examples:
        ///   "a.b.c"       → ["a", "b", "c"]
        ///   "user\.name"  → ["user.name"]
        ///   "a\\.b"       → ["a\", "b"]
        /// Empty path returns a single empty segment so callers behave like the
        /// pre-fix `path.Split('.')` did for the empty case.
        /// </summary>
        internal static IEnumerable<string> TokenizeJsonPath(string path)
        {
            if (path is null) yield break;
            var sb = new StringBuilder();
            for (int i = 0; i < path.Length; i++)
            {
                char c = path[i];
                if (c == '\\' && i + 1 < path.Length)
                {
                    char next = path[i + 1];
                    if (next == '.' || next == '\\')
                    {
                        sb.Append(next);
                        i++;
                        continue;
                    }
                    sb.Append(c);
                    continue;
                }
                if (c == '.')
                {
                    yield return sb.ToString();
                    sb.Clear();
                    continue;
                }
                sb.Append(c);
            }
            yield return sb.ToString();
        }

        // Accept both `key:value` (manifesto) and `key=value` (legacy)
        // separators. The Architect template doc says `key:value` but the
        // previous parser only honored `=`, silently dropping every entry.
        private static void ParseHeaderPair(string pair, out string? key, out string? val)
        {
            key = null; val = null;
            if (string.IsNullOrWhiteSpace(pair)) return;
            int sep = pair.IndexOfAny(new[] { ':', '=' });
            if (sep <= 0) return;
            key = pair.Substring(0, sep).Trim();
            val = pair.Substring(sep + 1).Trim();
            if (key.Length == 0) key = null;
        }
    }
}
