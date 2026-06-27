using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

// Minimal internal visibility increase for the engine perf test project
// (BugFixSweep3_EnginePerf_Tests). The hot helpers SubstituteVars,
// IsBlockHeader, and SplitArgs are marked `internal` rather than `public`
// to avoid widening the engine's external API; this attribute scopes that
// visibility to the test assembly only. Lives in this source file (rather
// than a separate AssemblyInfo.cs) to keep the perf-batch diff inside one
// engine source file as the task constraints require.
[assembly: InternalsVisibleTo("Phoenix.Controls.Tests")]

namespace Phoenix.Controls.Shared.Core
{
    public class ScriptContext
    {
        public string ProcessId { get; set; } = string.Empty;
        public Dictionary<string, string> Variables { get; } = new Dictionary<string, string>();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  (P1-A24) — AsyncLocal re-entry contract
    // ═══════════════════════════════════════════════════════════════════════
    //
    // ScriptEngine carries five pieces of per-execution state under the
    // AsyncLocal<T> contract — they flow with the logical call chain rather
    // than the physical thread, so nested calls (`event.trigger` invoking a
    // second script while the outer is still on the stack) and parallel
    // branches (`parallel_begin`) each see a coherent snapshot:
    //
    //   • _executionVars         — the live `vars` dictionary for the
    //                              currently-executing script
    //   • _executionCt           — the cancellation token threaded through
    //                              the current execution's awaits
    //   • _executionDepth        — nesting counter, bounded by
    //                              MaxExecutionDepth
    //   • _parallelBranchDepth   — > 0 inside a parallel_begin branch;
    //                              gates _executionVarsLock acquisition
    //   • _branchResultKeysLocal — set of var keys written by the current
    //                              branch that should merge back to the
    //                              parent on branch completion
    //
    // The ONLY legal mutation entry point for these fields is the
    // save/restore block at the top of `ExecuteScriptAsync` (currently around
    // lines 588-616 — search for "Save outer execution context" if the line
    // numbers drift). That block snapshots the prior values, resets / installs
    // fresh per-execution state, and is paired with a `finally` that restores
    // every snapshot in the inverse order. Adding a new piece of per-execution
    // state means adding it to BOTH halves of that block — and nowhere else.
    //
    // FORBIDDEN:
    //
    //   • Do not poke these fields from outside this save/restore block.
    //     There is no other supported writer; in particular, do not stash
    //     and overwrite them from a command handler, a script-engine
    //     extension, or any helper that runs mid-execution. The branch-merge
    //     path inside `RunParallelBranch` is the one internal exception and
    //     mirrors the exact save/restore discipline below.
    //
    //   • Do not call back into ScriptEngine from a script handler
    //     synchronously. Command handlers registered via RegisterCommand
    //     receive an `args` payload and return a Task — they must NOT call
    //     `ExecuteScriptAsync` (or any other entry point that re-enters the
    //     engine) on the same logical call without awaiting through the
    //     normal nested-execution path. Synchronous re-entry sidesteps the
    //     save/restore block, clobbers the live AsyncLocal values for the
    //     outer script, and silently drops local-result writes.
    //
    // FORWARD REFERENCE:
    //
    //   See Section 3 of `TODO.md` ("ScriptEngine formal state machine") for
    //   the deferred upgrade path that replaces this AsyncLocal-with-manual-
    //   save/restore pattern with a proper execution-context object passed
    //   explicitly down the call chain. Until that lands, the rules above
    //   are load-bearing — do not relax them.
    //
    // ═══════════════════════════════════════════════════════════════════════
    public partial class ScriptEngine
    {
        // ─────────────────────────────────────────────────────────────────
        // R23 — compiled regexes promoted to static readonly. These were
        // previously instantiated per script-line/per command/per arg in
        // hot paths. With Compiled+CultureInvariant they parse once at
        // type init and then run as JITted DFAs for every hit.
        // L68 — VarRefRegex is the single-pass substitution pattern used
        // by SubstituteVars (the per-key foreach was quadratic).
        // ─────────────────────────────────────────────────────────────────
        private const RegexOptions HotRegex = RegexOptions.Compiled | RegexOptions.CultureInvariant;
        private static readonly Regex VarRefRegex          = new(@"\{([\$\w\.]+)\}", HotRegex);
        //  DbPreloadRegex previously used `[^}]+` for the suffix which
        // greedily swallowed nested braces — `{global.list{0}}` would parse as
        // a single key `global.list{0` (capturing through the inner `{`), an
        // un-cacheable name that always missed the DB. Tightening to
        // `[\w.\-]+` restricts the suffix to identifier-style characters
        // (letters, digits, underscore, dot, hyphen) so a malformed brace
        // sequence simply skips the preload instead of polluting the cache.
        // Supported character set: `[A-Za-z0-9_.-]`. Document anywhere a
        // script author would consult.
        private static readonly Regex DbPreloadRegex       = new(@"\{((?:global|user|state|var)\.[\w\.\-]+)\}", HotRegex);
        //  Companion: a "stray opening brace" regex used to log a
        // Communication-tier parse-error for unmatched braces. Matches any
        // `{...}` segment containing characters that neither VarRefRegex nor
        // DbPreloadRegex can resolve (e.g. `{global.score:default}` — the
        // ':' would be valid in some future default-value syntax but isn't
        // recognized today, so we surface it so the script author can fix it
        // instead of silently leaving the raw token in the output).
        private static readonly Regex SuspiciousBraceRegex = new(@"\{[^{}]*\}", HotRegex);
        private static readonly Regex AssignmentDetectRegex = new(@"^[\$\w\.]+\s*[\+\-\*\/]?=\s*.+$", HotRegex);
        private static readonly Regex AssignmentParseRegex  = new(@"^([\$\w\.]+)\s*([\+\-\*\/]?=)\s*(.+)$", HotRegex);
        private static readonly Regex IfElifPrefixRegex     = new(@"^(if|elif)\s+", HotRegex);
        private static readonly Regex CallShapeRegex        = new(@"^[\w\.]+\s*\(.*\)$", HotRegex | RegexOptions.Singleline);
        private static readonly Regex CommandParseRegex     = new(@"^([\w\.]+)\s*\((.*)\)$", HotRegex | RegexOptions.Singleline);
        private static readonly Regex KeyValueArgRegex      = new(@"^([\w\.]+)=(.+)$", HotRegex);

        // SubstituteVars bare-name pass: one compiled Regex per distinct var key,
        // cached so the per-call foreach doesn't rebuild + JIT a new pattern every
        // time a busy chat command resolves the same var.
        //
        // QC01-08 — Bounded MRU so a long-lived Hub process that has seen many
        // ad-hoc var names (chat commands writing user.<random>, script DRY-runs
        // exploring throwaway keys, etc.) doesn't grow this cache unboundedly
        // across the process lifetime. Cap is set generously above any real
        // vocabulary size — a script that exceeds it pays at most one Regex
        // recompile per eviction (the eviction cost is amortized).
        private static readonly BoundedMruRegexCache BareNameRegexCache = new(capacity: 256);

        // ─────────────────────────────────────────────────────────────────
        // R12 — Block-header prefixes lifted to a single source of truth.
        // The engine identifies block-header lines (if, for_loop, on_*…)
        // by these prefixes; centralizing them makes future additions a
        // one-line change instead of hunting through the switch in
        // IsBlockHeader. The "if " case is special-cased in the recognizer
        // because it requires Length > 4.
        // ─────────────────────────────────────────────────────────────────
        private static readonly string[] BlockHeaderPrefixes = new[]
        {
            "on_event(",
            "on_chat",
            "on_startup",
            "on_bus(",
            "on_webhook(",
            "on_websocket(",  //  — external WebSocket server message handler
            "on_hotkey(",     //  — system-wide keystroke (Win32 RegisterHotKey)
            "on_clipboard",   //  — clipboard update (WM_CLIPBOARDUPDATE)
            "on_obs(",        // B38 — OBS WebSocket v5 event subscription
            "on_schedule(",
            "on_schedule_once(",
            "on_interval(",
            "on_state_change(",
            "on ",
            "elif ",
        };

        // M14 / L8 / L9 — block-headers introduced by sweep #7. These open
        // engine-native control structures that own their own per-block state
        // (CTS / visited-snapshot / counter) rather than being recognized via
        // the generic IsBlockHeader path. ProcessLine routes them BEFORE the
        // generic block-header dispatch so the recognition is exact-prefix.
        private static readonly string[] Sweep7BlockPrefixes = new[]
        {
            "async_timeout(",
            "sequence_begin",
            "do_n(",
            // Process unification — `process_spawn(<stableId>, <instanceId>, <title>):`
            // launches the indented body as a detached async unit on its own
            // CancellationTokenSource. Recognized BEFORE the generic IsBlockHeader
            // path because spawning the body must NOT block the parent (otherwise
            // it isn't fire-and-forget) and the parent's `_executionVars` /
            // `_executionCt` must not propagate into the spawned task.
            "process_spawn(",
        };

        // ─────────────────────────────────────────────────────────────────
        // R22 — Stable system-var dict, computed once at type init. Empty
        // today (every {system.*} the engine exposes is time-sensitive),
        // but the indirection is intentional so future paths/version vars
        // drop in without re-introducing the per-call allocation. The
        // time-sensitive vars are computed lazily inside the substitute
        // callback (see ResolveSystemVar) only when actually referenced.
        // ─────────────────────────────────────────────────────────────────
        private static readonly Dictionary<string, string> StaticSystemVars =
            new(StringComparer.OrdinalIgnoreCase);

        // QC01-12 — ConcurrentDictionary so RegisterCommand (Hub startup, sometimes
        // off-main-thread for late-bound integrations) can race with the engine's
        // hot _commands.TryGetValue lookups in ExecuteCommandWithResult without
        // tearing the bucket arrays. RegisterCommand is the only writer path; the
        // hot path is read-only.
        private readonly ConcurrentDictionary<string, Func<string[], Task<string?>>> _commands = new(StringComparer.OrdinalIgnoreCase);

        // R19 (sweep 14a) — typed args for the currently-dispatching handler.
        // The dispatch site (ExecuteCommandWithResult) populates this from
        // CommandBinder.BindArgs whenever the command's CommandSpec carries a
        // typed schema (registered via CommandManifest.AddTyped). Handlers
        // pull values via _engine.CurrentBoundArgs.Get<T>("ArgName") instead
        // of int.TryParse(args[1]) etc. Legacy commands (registered via the
        // untyped Add overload) leave this null, and handlers continue to
        // parse the raw string[] just like before.
        //
        // AsyncLocal so concurrent ExecuteScriptAsync calls from different
        // async contexts don't see each other's bindings — same isolation
        // model as the rest of C15's per-execution state. Save+restore at the
        // dispatch site preserves the binding across nested event.trigger.
        private static readonly AsyncLocal<BoundArgs?> _currentBoundArgs = new();
        public BoundArgs? CurrentBoundArgs => _currentBoundArgs.Value;

        // R9 — Persistence dependency injected at construction. DB is
        // the production implementation; tests can inject an in-memory IScriptDb
        // to exercise engine paths without touching SQLite. The parameterless
        // ctor preserves the singleton wiring for the ~30 existing call sites
        // (ScriptManager + test fixtures); the explicit ctor is the new seam.
        private readonly IScriptDb _db;

        public ScriptEngine() : this(DB.Instance) { }

        public ScriptEngine(IScriptDb db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        // ─────────────────────────────────────────────────────────────────
        // C15 — Per-execution mutable state backed by AsyncLocal so that
        // concurrent ExecuteScriptAsync calls from different async contexts
        // (chat handler, webhook handler, bus event handler, etc.) never
        // interleave on shared instance state.
        //
        // Semantics:
        //   • Each independent Task / async-method chain has its own
        //     AsyncLocal slot, so concurrent executions are fully isolated.
        //   • The save+restore pattern in ExecuteScriptAsync still handles
        //     nested re-entrancy (event.trigger → recursive ExecuteScriptAsync
        //     in the SAME async context): the nested call saves the parent's
        //     slot value, writes its own, then restores on exit — exactly as
        //     before, now correctly scoped to the ambient async context.
        //   • _executionVarsLock (B4 parallel_begin safety) stays an instance
        //     semaphore because parallel branches within one execution share the
        //     same vars dict reference and must still serialize mutations.
        // ─────────────────────────────────────────────────────────────────
        private static readonly AsyncLocal<Dictionary<string, string>?> _execVarsLocal    = new();
        private static readonly AsyncLocal<CancellationToken>           _execCtLocal      = new();
        private static readonly AsyncLocal<int>                         _execDepthLocal   = new();
        // _execItersLocal carries a StrongBox<int> (not a bare int) so loop bodies
        // can drive Interlocked.Increment(ref box.Value) — restoring the B4 atomic
        // counter that sweep 10's AsyncLocal<int> conversion accidentally broke.
        // The box reference flows through child tasks (parallel_begin branches),
        // so the cap remains shared within one ExecuteScriptAsync.
        private static readonly AsyncLocal<System.Runtime.CompilerServices.StrongBox<int>?> _execItersLocal = new();
        private static readonly AsyncLocal<int>                         _execStateDpLocal = new();
        private static readonly AsyncLocal<HashSet<string>?>            _execVisitedLocal = new();
        // BH-003 — keys touched by SetLocalResultVar / SetScriptVarAsync inside a
        // parallel_begin branch. parallel_begin's merge-back step only propagates keys
        // recorded here, so bare-assignment writes (which go through HandleAssignment's
        // explicit vars path, not through the result-writer methods) stay branch-local
        // — pinning the D1 isolation guarantee while still surfacing result-bearing
        // command outputs (db.find_row, math.add, etc.) to the outer scope. Null in the
        // parent's flow (no tracking outside parallel branches).
        private static readonly AsyncLocal<HashSet<string>?>            _branchResultKeysLocal = new();

        private Dictionary<string, string>? _executionVars
        {
            get => _execVarsLocal.Value;
            set => _execVarsLocal.Value = value;
        }

        // B4 — parallel_begin branches share the parent vars dict and mutate it
        // concurrently; the semaphore serializes those mutations. Still an instance
        // field — it guards within-execution concurrency, not cross-execution state.
        private readonly SemaphoreSlim _executionVarsLock = new SemaphoreSlim(1, 1);

        // #13 — depth counter for active parallel_begin blocks. The semaphore is only
        // load-bearing when ≥1 parallel branch is in flight; outside parallel_begin
        // the engine runs single-threaded per script flow, so locking is pure overhead.
        // Incremented before Task.Run-ing branches, decremented after Task.WhenAll.
        // Atomic because nested parallel_begin inside a branch can re-enter concurrently.
        private int _parallelBranchDepth;

        private CancellationToken _executionCt
        {
            get => _execCtLocal.Value;
            set => _execCtLocal.Value = value;
        }

        /// <summary>
        /// Public accessor for the current execution's cancellation token. Commands that
        /// own latent waits (delay, wait_for_visual, wait_for_event, HTTP) read this so
        /// the per-script CTS can short-circuit them on timeout / cancel.
        /// </summary>
        public CancellationToken ExecutionToken => _executionCt;

        // Per-execution stack-depth guard. Increments on each ExecuteBlock entry,
        // decrements in finally. Reset on each ExecuteScriptAsync call.
        private int _executionDepth
        {
            get => _execDepthLocal.Value;
            set => _execDepthLocal.Value = value;
        }
        private const int MaxExecutionDepth = 100;

        // Per-execution aggregate loop-iteration budget. Backed by StrongBox so
        // Interlocked.Increment(ref AggregateIterationsRef) stays atomic across
        // parallel_begin branches (which inherit the same box via AsyncLocal flow).
        private int _aggregateLoopIterations
        {
            get => _execItersLocal.Value?.Value ?? 0;
            set
            {
                if (_execItersLocal.Value is null)
                    _execItersLocal.Value = new System.Runtime.CompilerServices.StrongBox<int>(value);
                else
                    _execItersLocal.Value.Value = value;
            }
        }

        // Ref-returning accessor used by Interlocked.Increment at the loop sites.
        // Lazily allocates the box if no ExecuteScriptAsync has set it yet so
        // direct test calls into ExecuteBlock don't NRE.
        private ref int AggregateIterationsRef
        {
            get
            {
                if (_execItersLocal.Value is null)
                    _execItersLocal.Value = new System.Runtime.CompilerServices.StrongBox<int>(0);
                return ref _execItersLocal.Value.Value;
            }
        }
        private const int MaxAggregateLoopIterations = 500;

        // Per-execution state-change recursion depth (saves+restores across nested calls).
        private int _stateRecursionDepth
        {
            get => _execStateDpLocal.Value;
            set => _execStateDpLocal.Value = value;
        }
        private const int MaxStateRecursionDepth = 32;

        // L8 — Per-execution visited-node set (saves+restores per Logic.Sequence arm).
        private HashSet<string>? _visitedNodes
        {
            get => _execVisitedLocal.Value;
            set => _execVisitedLocal.Value = value;
        }

        // L9 — Engine-scoped Flow.DoN counters keyed by the call-site id (line
        // index in hex when the exporter doesn't emit a stable token). Stored
        // in the engine rather than as a global var so the saturate-at-MaxValue
        // path is enforced uniformly even if a script tampers with the global.
        // NOT reset per ExecuteScriptAsync call — the whole point of "do N
        // times" is that the count survives across triggers. Lock guards
        // concurrent Flow.DoN executions across parallel branches / triggers.
        private readonly Dictionary<string, int> _doNCounters =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly object _doNCountersLock = new object();

        // C15 — EventType / BusEventType / ScriptFile are set by ScriptManager immediately
        // before each ExecuteScriptAsync call. Using AsyncLocal ensures concurrent executions
        // each see the value their own caller set rather than the last writer's value.
        private static readonly AsyncLocal<string?> _execEventTypeLocal    = new();
        private static readonly AsyncLocal<string?> _execBusEventTypeLocal = new();
        private static readonly AsyncLocal<string?> _execScriptFileLocal   = new();

        /// <summary>Set by caller before execution to identify the triggering event type.</summary>
        public string EventType
        {
            get => _execEventTypeLocal.Value ?? "";
            set => _execEventTypeLocal.Value = value;
        }

        /// <summary>Set by caller when this execution was triggered by a bus message.</summary>
        public string BusEventType
        {
            get => _execBusEventTypeLocal.Value ?? "";
            set => _execBusEventTypeLocal.Value = value;
        }

        /// <summary>
        /// Fired when a NODE_EXEC marker is encountered during script execution.
        /// Args: (nodeId, scriptFile). Used by Architect's debug trace to flash nodes.
        /// </summary>
        public event Action<string, string>? OnNodeExecuted;

        /// <summary>
        /// Fired when a local-scope variable is written during script execution
        /// (Var.Set's bare assignment path or Public.Set's command path).
        /// Args: (varName, value, scriptFile). Used by Architect's local-vars
        /// panel (P1 #1 phase 2) to surface live debug-time values.
        /// Excludes global.* / user.* writes — those are DB-backed and visible
        /// via the existing Hub Variables panel.
        /// </summary>
        public event Action<string, string, string>? OnVariableSet;

        // Iterates each subscriber under its own try/catch — same pattern as
        // FireNodeExecuted so a faulty Architect handler can't unwind the
        // interpreter loop. Bus broadcasts are best-effort.
        private void FireVariableSet(string varName, string value)
        {
            var handler = OnVariableSet;
            if (handler == null) return;
            foreach (var d in handler.GetInvocationList())
            {
                try { ((Action<string, string, string>)d)(varName, value, ScriptFile); }
                catch (Exception ex)
                {
                    Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                        "ScriptEngine", $"OnVariableSet handler threw for var '{varName}'", ex);
                }
            }
        }

        // Iterates each subscriber under its own try/catch so a faulty external
        // handler (e.g. an Architect callback running on a torn-down panel) can't
        // unwind the script-execution loop and abort the whole .phx run. Without
        // this, any handler exception was thrown straight up through the
        // interpreter's main loop.
        private void FireNodeExecuted(string nodeId)
        {
            var handler = OnNodeExecuted;
            if (handler == null) return;
            foreach (var d in handler.GetInvocationList())
            {
                try { ((Action<string, string>)d)(nodeId, ScriptFile); }
                catch (Exception ex)
                {
                    Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                        "ScriptEngine", $"OnNodeExecuted handler threw for node '{nodeId}'", ex);
                }
            }
        }

        /// <summary>Source script file name set by ScriptManager before execution for debug trace.</summary>
        public string ScriptFile
        {
            get => _execScriptFileLocal.Value ?? "";
            set => _execScriptFileLocal.Value = value;
        }

        public void RegisterCommand(string name, Func<string[], Task<string?>> action)
            => _commands[name] = action;

        public bool HasCommand(string name) => _commands.ContainsKey(name);

        /// <summary>
        /// [ enabler for ] Snapshot of every command currently
        /// registered on this engine instance. Returns a fresh array on each
        /// call (ConcurrentDictionary.Keys enumerates lazily; ToArray pins a
        /// point-in-time snapshot so callers can iterate without racing
        /// concurrent RegisterCommand writers).
        ///
        /// Consumers ( — script-window introspection, future
        /// validation passes) read this to cross-check the manifest against
        /// what's actually wired up at Hub startup, surfacing the difference
        /// as a startup diagnostic instead of failing silently at first call.
        /// </summary>
        public IReadOnlyCollection<string> RegisteredCommandNames => _commands.Keys.ToArray();

        /// <summary>
        /// Read a variable from the active execution context. Returns the empty
        /// string if the var isn't set or no execution is active. Commands use
        /// this for transient bookkeeping vars (e.g. timeout_check reading the
        /// per-execution _script_start_ms stamp) without paying a DB round trip.
        /// </summary>
        public string GetExecutionVar(string key)
        {
            // B4 — also synchronize reads. Dictionary is not safe for concurrent
            // read-while-write either; a parallel branch reading mid-resize can throw
            // or return torn results. Lock scope is the .TryGetValue call only.
            // #13 — only take the lock when ≥1 parallel_begin block is active. Outside
            // parallel_begin the engine runs single-threaded per script flow and the
            // dict mutation cannot race with itself.
            if (_executionVars == null) return string.Empty;
            // [P0 swarm-audit 2026-05-30] Acquire UNCONDITIONALLY. The prior
            // `taken = Volatile.Read(_parallelBranchDepth) > 0` gate was a TOCTOU
            // with the write paths (SetScriptVarAsync / SetLocalResultVar): a
            // nested event.trigger resets depth to 0 mid-flight, so this reader
            // could skip the lock while a parallel branch is mid-write and observe
            // a torn read / InvalidOperationException during a Dictionary resize.
            _executionVarsLock.Wait();
            try
            {
                return _executionVars.TryGetValue(key, out var v) ? v : string.Empty;
            }
            finally { _executionVarsLock.Release(); }
        }

        /// <summary>
        /// Write a variable to both the active execution context (local vars) and the persistent DB.
        /// Use for commands that need persistence (DB nodes, state, etc.).
        /// </summary>
        public async Task SetScriptVarAsync(string key, string value)
        {
            //  DB write FIRST. The previous ordering wrote to
            // _executionVars BEFORE the DB await — on a DB-write failure the
            // in-memory dict carried the new value while the durable store
            // kept the old, and the {global.foo} / {user.foo} reference next
            // script-tick would silently read the stale-DB value through the
            // preload path (since SubstituteVars consults _executionVars
            // first). Reorder so a DB throw leaves both stores consistent
            // (both unchanged), and let the exception bubble — callers wrap
            // SetScriptVarAsync in their own try/catch where DB outages are
            // recoverable (e.g. ScriptManager's command handlers).
            await _db.SetVariableAsync(key, value);

            // In-memory write happens only AFTER the DB write succeeds, so a
            // DB failure can never leave _executionVars and the Vars table
            // disagreeing. B4 lock still serializes parallel_begin branch
            // contention. The branch-result tagging stays post-DB-write per
            //  — only successfully durable values get merged back.
            if (_executionVars != null)
            {
                // [P0 swarm-audit 2026-05-30] Acquire the lock UNCONDITIONALLY.
                // The previous `taken = Volatile.Read(_parallelBranchDepth) > 0`
                // gate was a TOCTOU: the depth read is not atomic with the
                // _executionVars[key]=value write, and a nested event.trigger
                // resets _parallelBranchDepth to 0 mid-flight (Interlocked.Exchange
                // in the re-entry guard). That opens a window where a parallel
                // branch writes the dict while another writer, seeing depth==0,
                // skips the lock — an unsynchronized concurrent Dictionary mutation.
                // Uncontended SemaphoreSlim is near-free and no caller holds this
                // lock across node execution, so always-lock is deadlock-free.
                await _executionVarsLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    _executionVars[key] = value;
                }
                finally { _executionVarsLock.Release(); }
                // BH-003 — record this as a result-style write so parallel_begin's
                // merge-back propagates it to the outer scope. Reaches here only
                // if _db.SetVariableAsync did not throw, so DB and in-memory agree.
                _branchResultKeysLocal.Value?.Add(key);
            }
        }

        /// <summary>
        /// Write a result into the active execution context only — no DB persistence.
        /// Use for pure computation nodes (Text.Builder, Math.*, etc.) that pass values
        /// directly to the next node like Blueprint data pins.
        /// </summary>
        public void SetLocalResultVar(string key, string value)
        {
            if (_executionVars != null)
            {
                // B4 — same hazard as SetScriptVarAsync: parallel branches can
                // mutate _executionVars concurrently, so guard the mutation. Use
                // sync Wait() because this method is sync; contention is short.
                // [P0 swarm-audit 2026-05-30] Acquire UNCONDITIONALLY. The prior
                // `taken = Volatile.Read(_parallelBranchDepth) > 0` gate was a
                // TOCTOU (see SetScriptVarAsync): a nested event.trigger resetting
                // depth to 0 mid-write let one writer skip the lock while another
                // held it, racing the Dictionary write. Uncontended SemaphoreSlim
                // is near-free; no caller holds this lock across node execution.
                _executionVarsLock.Wait();
                try
                {
                    _executionVars[key] = value;
                    // BH-003 — record this as a result-style write so parallel_begin's
                    // merge-back propagates it to the outer scope.
                    _branchResultKeysLocal.Value?.Add(key);
                }
                finally { _executionVarsLock.Release(); }

                // P1 #1 phase 2 — debug feed for the Architect local-vars panel. Public.Set
                // routes through here (and is intentionally NOT in IsReservedLocalKey's skip
                // list for SetLocalResultVar's public.* keys), so the panel sees both Var.Set's
                // bare-assignment writes and Public.Set's command-call writes.
                if (!IsReservedLocalKey(key) || key.StartsWith("public.", StringComparison.OrdinalIgnoreCase))
                    FireVariableSet(key, value);
            }
            else
            {
                GlobalLogger.Log($"SetLocalResultVar: _executionVars is NULL — [{key}] lost!", "ScriptEngine", LogLevel.CriticalError);
            }
        }

        /// <summary>
        /// BH-005 — guarded Add for the per-execution visited-node set.
        /// QC01-07 — Per-branch HashSet snapshot in RunParallelBranch makes the
        /// visited-set private to each parallel_begin branch (and to each
        /// HandleSequenceBlock arm). With no cross-branch sharing, the previous
        /// _executionVarsLock acquisition here is no longer load-bearing — and
        /// dropping it  removes a cross-branch serialization point
        /// that was funneling every NODE_EXEC marker through a single
        /// semaphore. The visited-set is now single-writer per branch.
        /// Returns true when there was no set (de-dup disabled) or the id was newly added.
        /// </summary>
        private bool TryAddVisited(string id)
        {
            var visited = _visitedNodes;
            if (visited == null) return true;
            return visited.Add(id);
        }

        // ─────────────────────────────────────────────────────────────────
        // MAIN EXECUTION LOOP
        // ─────────────────────────────────────────────────────────────────

        public async Task<Dictionary<string, string>> ExecuteScriptAsync(
            string scriptContent,
            Dictionary<string, string> contextVars,
            CancellationToken cancellationToken = default)
        {
            var lines = scriptContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            // Merge context vars into a mutable dict for this execution
            var vars = new Dictionary<string, string>(contextVars, StringComparer.OrdinalIgnoreCase);

            // Stamp the start of this execution so timeout_check / Async.Timeout
            // can compute elapsed time relative to the current event handler.
            // Always overwrite — nested executions (event.trigger) want their own
            // budget, not the outer caller's.
            vars["global._script_start_ms"] = Environment.TickCount64.ToString();

            // Pre-load any {global.*} / {user.*} / {state.*} / {var.*} variable references
            // found in the script that aren't already in vars. Covers values set by previous
            // executions (DB-only at this point). state.set / var writes go through the same
            // Vars table via SetScriptVarAsync, so GetVariableAsync resolves them
            // identically — no separate code path needed.
            //
            // Batch-loaded via IScriptDb.GetVariablesAsync to fold N+1 round-
            // trips through DB._lock into one. Chat-command latency
            // diagnostics traced ~30 sequential lock acquisitions per
            // chat-message dispatch (5 matching scripts × ~5 distinct DB
            // refs each + 5 user.points reads in ScriptManager) directly to
            // this loop's per-key await; the batch read collapses each
            // script's fan-out into a single query while preserving the
            // "skip if absent" semantics that the previous != "" guard
            // implemented.
            var dbPreloadKeys = DbPreloadRegex.Matches(scriptContent)
                .Cast<System.Text.RegularExpressions.Match>()
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(k => !vars.ContainsKey(k))
                .ToList();
            if (dbPreloadKeys.Count > 0)
            {
                var loaded = await _db.GetVariablesAsync(dbPreloadKeys).ConfigureAwait(false);
                foreach (var kv in loaded)
                {
                    if (!string.IsNullOrEmpty(kv.Value))
                        vars[kv.Key] = kv.Value;
                }
            }

            // Save outer execution context so nested ExecuteScriptAsync calls (via event.trigger)
            // don't null out the parent's _executionVars, which would silently drop all
            // SetLocalResultVar writes (db.find_row out-vars, db.insert_row, etc.) in the outer script.
            // BH-008: include the event-context AsyncLocals (EventType / BusEventType / ScriptFile);
            // their writers (ScriptManager.ExecuteEventScriptAsync, etc.) set them before the inner
            // call, and without restore the outer's sibling on_event headers see the inner trigger's
            // event type after the nested call returns.
            var savedVars             = _executionVars;
            var savedCt               = _executionCt;
            var savedDepth            = _executionDepth;
            var savedAggregateIters   = _aggregateLoopIterations;
            var savedStateDepth       = _stateRecursionDepth;
            var savedVisited          = _visitedNodes;
            var savedEventType        = _execEventTypeLocal.Value;
            var savedBusEventType     = _execBusEventTypeLocal.Value;
            var savedScriptFile       = _execScriptFileLocal.Value;
            //  _branchResultKeysLocal was previously absent from the
            // save/restore set. A nested ExecuteScriptAsync (event.trigger
            // inside a parallel_begin branch) would have its
            // SetLocalResultVar / SetScriptVarAsync writes tagged into the
            // OUTER branch's resultKeys set, leaking branch-private result
            // names back into the outer parallel_begin merge. Snapshot and
            // null on entry so the nested execution starts with no
            // tagging (it isn't itself inside a parallel branch); restore
            // in finally so the outer branch's tagging resumes intact.
            var savedBranchResultKeys = _branchResultKeysLocal.Value;
            _branchResultKeysLocal.Value = null;
            //  _parallelBranchDepth was not in the save/restore set.
            // A nested ExecuteScriptAsync (via event.trigger) inherited the
            // outer's depth — if the inner threw out of a parallel_begin
            // body that had incremented but not yet decremented, the engine
            // stayed permanently "in parallel mode" (every SetLocalResultVar
            // /  SetScriptVarAsync / GetExecutionVar would take the
            // _executionVarsLock on subsequent unrelated executions). Snapshot
            // and reset to 0 on entry; restore in finally.
            var savedParallelDepth    = Interlocked.Exchange(ref _parallelBranchDepth, 0);

            // State-change ping-pong guard: each nested ExecuteScriptAsync whose
            // EventType is "StateChange.*" bumps the depth counter. Restored on
            // exit, so counter is per-chain and unwinds cleanly. When the cap is
            // exceeded we abort early using the same sentinel-write pattern as
            // MaxExecutionDepth — no exception, callers get an empty result.
            bool isStateChangeEntry = !string.IsNullOrEmpty(EventType)
                && EventType.StartsWith("StateChange.", StringComparison.OrdinalIgnoreCase);
            if (isStateChangeEntry)
                _stateRecursionDepth++;
            if (_stateRecursionDepth > MaxStateRecursionDepth)
            {
                try
                {
                    await _db.SetVariableAsync(
                        "global._engine_state_recursion_observed",
                        $"depth>{MaxStateRecursionDepth}");
                }
                catch { /* sentinel write is best-effort */ }
                GlobalLogger.Log(
                    $"State-change recursion limit ({MaxStateRecursionDepth}) reached — aborting (EventType={EventType}).",
                    "ScriptEngine", LogLevel.CriticalError);
                // This early return bypasses the try-finally below, so any state we
                // mutated during the save/snapshot phase (lines above) must be hand-
                // restored here or it leaks into the next execution on this async flow.
                // _executionVars/_executionCt/_executionDepth/_aggregateLoopIterations/
                // _visitedNodes/_exec*Local are not assigned until after this point, so
                // they remain the parent's values and need no restore — but
                // _branchResultKeysLocal (nulled on entry) and _parallelBranchDepth
                // (exchanged to 0 on entry) were already overwritten.
                _stateRecursionDepth         = savedStateDepth;
                _branchResultKeysLocal.Value = savedBranchResultKeys;
                Interlocked.Exchange(ref _parallelBranchDepth, savedParallelDepth);
                return vars;
            }

            // Expose to commands so they can write results back via SetScriptVarAsync / SetLocalResultVar
            _executionVars            = vars;
            _executionCt              = cancellationToken;
            _executionDepth           = 0;
            _aggregateLoopIterations  = 0;
            _visitedNodes             = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                await ExecuteBlock(lines, 0, lines.Length - 1, 0, vars);
            }
            finally
            {
                _executionVars            = savedVars;
                _executionCt              = savedCt;
                _executionDepth           = savedDepth;
                _aggregateLoopIterations  = savedAggregateIters;
                _stateRecursionDepth      = savedStateDepth;
                _visitedNodes             = savedVisited;
                _execEventTypeLocal.Value    = savedEventType;
                _execBusEventTypeLocal.Value = savedBusEventType;
                _execScriptFileLocal.Value   = savedScriptFile;
                //  Restore outer's branch-result tagging slot so a
                // surrounding parallel_begin (around the nested call) keeps
                // recording its own result-style writes after we return.
                _branchResultKeysLocal.Value = savedBranchResultKeys;
                //  Restore parent's parallel-branch depth even if the
                // inner script threw out of a parallel_begin body — pins the
                // invariant that the depth reflects the outer's state when
                // this call returns.
                Interlocked.Exchange(ref _parallelBranchDepth, savedParallelDepth);
            }
            return vars;
        }

        /// <summary>Execute lines[start..end] that belong to the given indent level.</summary>
        private async Task ExecuteBlock(string[] lines, int start, int end, int indent, Dictionary<string, string> vars)
        {
            // Guard against runaway nested if/loop blocks blowing the host stack.
            // We set a sentinel in the DB so callers / tests can observe that the
            // depth limit was hit, log it, and unwind cleanly. We deliberately do
            // NOT throw — a thrown exception out of script execution fault-trips
            // the entire ExecuteScriptAsync call and confuses callers that wrap
            // the result via Task.Wait. Halting the deepest block is sufficient.
            if (_executionDepth >= MaxExecutionDepth)
            {
                try
                {
                    await _db.SetVariableAsync(
                        "global._engine_depth_limit_observed",
                        $"depth>={MaxExecutionDepth}");
                }
                catch { /* sentinel write is best-effort */ }
                GlobalLogger.Log(
                    $"Script execution depth limit ({MaxExecutionDepth}) reached — halting this block.",
                    "ScriptEngine", LogLevel.CriticalError);
                return;
            }
            _executionDepth++;
            try
            {
            int i = start;
            while (i <= end)
            {
                _executionCt.ThrowIfCancellationRequested();
                string raw = i < lines.Length ? lines[i] : "";
                if (string.IsNullOrWhiteSpace(raw))
                {
                    i++; continue;
                }
                // Node execution markers — fire event, then skip like a normal comment.
                // L8 — re-visits of the same node id within the current visit-window
                // are suppressed (no second OnNodeExecuted fire). Logic.Sequence
                // (sequence_begin) snapshots+restores _visitedNodes per arm so
                // independent paths through the graph each see a fresh history.
                string trimmedRaw = raw.TrimStart();
                // New format: # [Node.Title]
                if (trimmedRaw.StartsWith("# [") && trimmedRaw.EndsWith("]"))
                {
                    string title = trimmedRaw[3..^1];
                    if (TryAddVisited(title))
                        FireNodeExecuted(title);
                    i++; continue;
                }
                // Legacy format: # NODE_EXEC:{guid}
                if (trimmedRaw.StartsWith("# NODE_EXEC:"))
                {
                    string nodeId = trimmedRaw.Substring("# NODE_EXEC:".Length).Trim();
                    if (TryAddVisited(nodeId))
                        FireNodeExecuted(nodeId);
                    i++; continue;
                }
                if (trimmedRaw.StartsWith("#"))
                {
                    i++; continue;
                }

                int lineIndent = GetIndent(raw);
                if (lineIndent < indent) break; // Dedented out of this block
                if (lineIndent > indent) { i++; continue; } // Child line, handled by parent

                string line = StripInlineComment(raw.Trim());
                i = await ProcessLine(lines, i, end, indent, line, vars);
            }
            }
            finally
            {
                _executionDepth--;
            }
        }

        private async Task<int> ProcessLine(string[] lines, int i, int end, int indent, string line, Dictionary<string, string> vars)
        {
            // ── Sweep #7 engine-native blocks (M14 / L8 / L9) ────────────
            // Recognized BEFORE the generic IsBlockHeader path because they
            // own per-block state (CTS for async_timeout, visit-snapshot for
            // sequence_begin, saturating counter for do_n) that the generic
            // ExecuteBlock dispatcher can't supply.
            if (line.StartsWith("async_timeout(") && line.EndsWith("):"))
                return await HandleAsyncTimeoutBlock(lines, i, end, indent, line, vars);
            if (line == "sequence_begin")
                return await HandleSequenceBlock(lines, i, end, indent, vars);
            if (line.StartsWith("do_n(") && line.EndsWith("):"))
                return await HandleDoNBlock(lines, i, end, indent, line, vars);
            if (line.StartsWith("process_spawn(") && line.EndsWith("):"))
                return HandleProcessSpawnBlock(lines, i, end, indent, line, vars);
            if (line == "sequence_end" || line == "do_n_end" || line == "async_timeout_end")
                return i + 1;

            // ── Event block headers ──────────────────────────────────────
            if (IsBlockHeader(line))
            {
                if (ShouldEnterBlock(line, vars))
                {
                    int blockEnd = FindBlockEnd(lines, i, indent);
                    await ExecuteBlock(lines, i + 1, blockEnd, indent + 1, vars);
                    return blockEnd + 1;
                }
                else
                {
                    int skipTo = FindBlockEnd(lines, i, indent);
                    // Check for else: / on_timeout: / on_late: at same level
                    int next = skipTo + 1;
                    while (next <= end && string.IsNullOrWhiteSpace(lines[next])) next++;
                    if (next <= end)
                    {
                        string nextTrimmed = StripInlineComment(lines[next].Trim());
                        if ((nextTrimmed == "else:" || nextTrimmed == "on_timeout:" || nextTrimmed == "on_late:" || nextTrimmed == "completed:") &&
                            GetIndent(lines[next]) == indent)
                        {
                            // if was FALSE — execute the else/timeout body directly
                            int elseEnd = FindBlockEnd(lines, next, indent);
                            await ExecuteBlock(lines, next + 1, elseEnd, indent + 1, vars);
                            return elseEnd + 1;
                        }
                    }
                    return skipTo + 1;
                }
            }

            // ── else: / on_timeout: / on_late: / completed: (only reached if preceding block consumed control) ──
            if (line == "else:" || line == "on_timeout:" || line == "on_late:" || line == "completed:")
            {
                int blockEnd = FindBlockEnd(lines, i, indent);
                return blockEnd + 1; // Skip — preceding condition was satisfied
            }

            // ── Variable assignment ──────────────────────────────────────
            if (AssignmentDetectRegex.IsMatch(line))
            {
                await HandleAssignment(line, vars);
                return i + 1;
            }

            // ── for_loop(first, last): ──────────────────────────────────
            // E1 — accept the Pythonic colon-terminated form `for_loop(...):` in
            // addition to the canonical bare-paren form the exporter emits.
            // on_event(...): / if ...: already accept the colon via IsBlockHeader,
            // so Pythonic for_loop / while_loop / for_each are normalized here for
            // language-surface consistency. Strip exactly one trailing ':'.
            if ((line.StartsWith("for_loop(") || line.StartsWith("while_loop(") || line.StartsWith("for_each("))
                && line.EndsWith("):"))
                line = line[..^1];

            if (line.StartsWith("for_loop(") && line.EndsWith(")"))
            {
                var p = ExtractArgs(line, "for_loop");
                if (p.Length >= 2 && int.TryParse(SubstituteVars(p[0], vars), out int first) &&
                    int.TryParse(SubstituteVars(p[1], vars), out int last))
                {
                    int blockEnd = FindBlockEnd(lines, i, indent);
                    string idxKey = $"global._loop_idx_{i}";
                    // Per-loop index key — each for_loop in the script gets its own
                    // namespaced variable so nested loops don't clobber the outer's
                    // {loop.index}. Suffix is the loop's source-line index in hex —
                    // unique per loop within a single execution. The legacy
                    // {loop.index} write is preserved below for compatibility with
                    // existing exports until B3 swaps the exporter to per-loop ids.
                    // If the exporter embedded a node-id token (3rd arg), use it so the key
                    // matches what ScriptExporter emits for the Index data-output socket.
                    // Fall back to line-index hex for hand-authored scripts without the tag.
                    string perLoopIdxKey = p.Length >= 3 && !string.IsNullOrWhiteSpace(p[2])
                        ? $"loop.index_{p[2].Trim()}"
                        : $"loop.index_{i:x}";

                    // H14 — Flow.ForLoop ignores reverse direction. If first > last we
                    // count down, allowing scripts like for_loop(10, 1) to iterate
                    // in descending order. Optional 4th arg is Step (defaults to 1);
                    // negative steps further override direction.
                    int step = 1;
                    if (p.Length >= 4 && int.TryParse(SubstituteVars(p[3], vars), out int parsedStep) && parsedStep != 0)
                        step = parsedStep;
                    bool descending = first > last && step > 0 ? true : step < 0;
                    int absStep = Math.Abs(step);
                    int idxStart = first;
                    int idxEnd   = last;
                    int signedStep = descending ? -absStep : absStep;
                    bool LoopCond(int x) => descending ? x >= idxEnd : x <= idxEnd;
                    for (int idx = idxStart; LoopCond(idx); idx += signedStep)
                    {
                        // Honor cancellation between iterations and aggregate-cap so a
                        // for_loop can't multiply per-instance safety in nested loops.
                        _executionCt.ThrowIfCancellationRequested();
                        // B4 — Interlocked so the cap is honored when for_loop runs
                        // inside a parallel_begin branch (and across branches that
                        // share the per-execution counter).
                        if (Interlocked.Increment(ref AggregateIterationsRef) > MaxAggregateLoopIterations)
                        {
                            GlobalLogger.Log($"for_loop aggregate iteration cap ({MaxAggregateLoopIterations}) reached — halting.", "ScriptEngine", LogLevel.CriticalError);
                            return blockEnd + 1;
                        }
                        vars[idxKey] = idx.ToString();
                        vars[perLoopIdxKey] = idx.ToString();
                        vars["loop.index"] = idx.ToString();
                        await ExecuteBlock(lines, i + 1, blockEnd, indent + 1, vars);
                    }
                    // Look for Completed block
                    int compIdx = blockEnd + 1;
                    while (compIdx <= end && string.IsNullOrWhiteSpace(lines[compIdx])) compIdx++;
                    return blockEnd + 1;
                }
                return i + 1;
            }

            // ── while_loop(condition): ───────────────────────────────────
            if (line.StartsWith("while_loop(") && line.EndsWith(")"))
            {
                int wlPrefixLen = "while_loop(".Length;
                int wlInnerLen  = line.Length - wlPrefixLen - 1; // subtract closing ")"
                string condRaw  = wlInnerLen > 0 ? line.Substring(wlPrefixLen, wlInnerLen) : string.Empty;
                int blockEnd = FindBlockEnd(lines, i, indent);
                // L11 — the previous local 1000-iteration safety counter was dead code
                // because the aggregate cap (MaxAggregateLoopIterations = 500) trips first.
                // The aggregate cap is the canonical guard; rely on it, plus cancellation.
                // cond-not-injection: pass the RAW template to EvaluateCondition,
                // which now substitutes at the leaves. Pre-substituting here would
                // reintroduce the operator-injection bug for while_loop headers.
                while (EvaluateCondition("if " + condRaw + ":", vars))
                {
                    _executionCt.ThrowIfCancellationRequested();
                    // B4 — Interlocked so concurrent parallel_begin branches running
                    // while_loop bodies don't race the aggregate cap counter.
                    if (Interlocked.Increment(ref AggregateIterationsRef) > MaxAggregateLoopIterations)
                    {
                        GlobalLogger.Log($"while_loop aggregate iteration cap ({MaxAggregateLoopIterations}) reached — halting.", "ScriptEngine", LogLevel.CriticalError);
                        return blockEnd + 1;
                    }
                    await ExecuteBlock(lines, i + 1, blockEnd, indent + 1, vars);
                }
                return blockEnd + 1;
            }

            // ── for_each(list): ─────────────────────────────────────────
            if (line.StartsWith("for_each(") && line.EndsWith(")"))
            {
                int fePrefixLen = "for_each(".Length;
                int feInnerLen  = line.Length - fePrefixLen - 1; // subtract closing ")"
                //  StripQuotesAndUnescape so a literal CSV passed via
                // for_each("a,b,c\n") honors embedded escapes; identifier-style
                // / call-shape args are returned unchanged by the helper.
                string listExpr = feInnerLen > 0 ? StripQuotesAndUnescape(line.Substring(fePrefixLen, feInnerLen).Trim()) : string.Empty;
                // BH-007 + BH-001: decide call-vs-data on the PRE-substitution form. The
                // exporter emits literal call shapes (db.get_column(...)) directly into
                // for_each; those must execute. Anything else — variable references, CSV
                // literals, or chat-derived content — is data and must NOT be re-parsed
                // as code (a chat message containing parens previously slipped through
                // the Contains("(") heuristic and reached ExecuteCommandWithResult).
                string listVal;
                if (CallShapeRegex.IsMatch(listExpr))
                {
                    listVal = await ExecuteCommandWithResult(listExpr, vars) ?? "";
                }
                else
                {
                    listVal = SubstituteVars(listExpr, vars);
                }
                // L10 — Items separated by commas may themselves contain `\,` to escape an
                // intended literal comma. Without this every CSV with embedded commas (a
                // perfectly common case in Twitch chat) would corrupt iteration.
                var items = SplitListWithEscape(listVal);
                int blockEnd = FindBlockEnd(lines, i, indent);
                foreach (var item in items)
                {
                    _executionCt.ThrowIfCancellationRequested();
                    // B4 — Interlocked so for_each iterating inside parallel branches
                    // can't double-count or skip the aggregate cap under contention.
                    if (Interlocked.Increment(ref AggregateIterationsRef) > MaxAggregateLoopIterations)
                    {
                        GlobalLogger.Log($"for_each aggregate iteration cap ({MaxAggregateLoopIterations}) reached — halting.", "ScriptEngine", LogLevel.CriticalError);
                        return blockEnd + 1;
                    }
                    vars["loop.item"] = item.Trim();
                    await ExecuteBlock(lines, i + 1, blockEnd, indent + 1, vars);
                }
                return blockEnd + 1;
            }

            // ── parallel_begin / parallel_end ────────────────────────────
            if (line == "parallel_begin")
            {
                int pEnd = FindBlockEnd(lines, i, indent);
                var tasks = new List<Task>();
                var branchVarsList = new List<(Dictionary<string, string> Vars, HashSet<string> ResultKeys)>();

                // BH-004: link a per-block CTS so a faulted/timed-out branch cancels its
                // siblings instead of letting them keep doing real work (DB writes, bus
                // sends, HTTP calls) after the script has effectively failed.
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_executionCt);
                var savedCt = _executionCt;
                _executionCt = linkedCts.Token;

                // #13 — flip lock-required state ON for the duration of this block so
                // SetLocalResultVar / SetScriptVarAsync / GetExecutionVar / TryAddVisited
                // serialize cross-branch access. Decremented in the finally below.
                Interlocked.Increment(ref _parallelBranchDepth);

                // Each top-level entry inside the block (at indent+1) becomes its own branch.
                // FindBlockEnd finds the full extent of any sub-block under that entry so that
                // multi-line branches (if:/for_loop:/etc.) are treated as a single unit.
                int j = i + 1;
                while (j <= pEnd)
                {
                    if (j >= lines.Length) break;
                    string rawBranch = lines[j];
                    if (string.IsNullOrWhiteSpace(rawBranch)) { j++; continue; }
                    string trimmed = rawBranch.TrimStart();
                    if (trimmed.StartsWith("#")) { j++; continue; }

                    int lineInd = GetIndent(rawBranch);
                    if (lineInd < indent + 1) break;   // Escaped block — shouldn't happen
                    if (lineInd > indent + 1) { j++; continue; } // Belongs to previous entry

                    // Determine how far this branch extends (including any sub-blocks it owns)
                    int branchEnd = FindBlockEnd(lines, j, indent + 1);
                    branchEnd = Math.Min(branchEnd, pEnd);

                    int capturedStart = j;
                    int capturedEnd   = branchEnd;
                    var branchVars    = new Dictionary<string, string>(vars, StringComparer.OrdinalIgnoreCase);
                    var resultKeys    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    branchVarsList.Add((branchVars, resultKeys));
                    // BH-003 + BH-004: Task.Run gives each branch an isolated ExecutionContext
                    // so the branch's _executionVars assignment (in RunParallelBranch) doesn't
                    // bleed into siblings; the linkedCts ensures a faulting branch signals the
                    // others to stop. resultKeys is populated by SetLocalResultVar /
                    // SetScriptVarAsync via the _branchResultKeysLocal AsyncLocal so
                    // parallel_begin's merge-back can propagate result-style writes only.
                    tasks.Add(Task.Run(() =>
                        RunParallelBranch(lines, capturedStart, capturedEnd, indent + 1, branchVars, resultKeys, linkedCts)));

                    j = branchEnd + 1;
                }

                try
                {
                    if (tasks.Count > 0)
                        await Task.WhenAll(tasks);
                }
                finally
                {
                    _executionCt = savedCt;
                    Interlocked.Decrement(ref _parallelBranchDepth);

                    // BH-003 — fold branch RESULT writes back into the parent's vars dict
                    // so SetLocalResultVar / SetScriptVarAsync values produced inside
                    // parallel branches are observable after parallel_end. Bare-assignment
                    // writes (HandleAssignment's `vars[key] = val;` path) are NOT
                    // recorded in resultKeys, so they stay branch-local — pinning the D1
                    // isolation guarantee. Last-writer-wins for keys touched by multiple
                    // branches; this matches the H13 contract that any one of the writers
                    // is acceptable on hot-key contention.
                    foreach (var (bv, keys) in branchVarsList)
                    {
                        foreach (var key in keys)
                        {
                            if (bv.TryGetValue(key, out var v))
                                vars[key] = v;
                        }
                    }
                    // Keep _executionVars consistent with the merged parent so the
                    // outer script's commands see the propagated results too.
                    _executionVars = vars;
                }
                return pEnd + 1;
            }
            if (line == "parallel_end" || line == "join_wait")
                return i + 1;

            // ── Regular command call ─────────────────────────────────────
            await ExecuteCommand(line, vars);
            return i + 1;
        }

        // RunParallelBranch, HandleAsyncTimeoutBlock, HandleSequenceBlock, HandleDoNBlock
        // moved to ScriptEngine.ParallelExecution.cs ().

        // _spawnedProcesses, OnProcessSpawned, OnProcessTerminated,
        // TerminateSpawnedProcess, HandleProcessSpawnBlock, StripQuotesAndSubstitute
        // moved to ScriptEngine.ProcessManagement.cs ().

        // IsBlockHeader, ShouldEnterBlock moved to ScriptEngine.Utilities.cs ().

        // ─────────────────────────────────────────────────────────────────
        // CONDITION EVALUATION
        // ─────────────────────────────────────────────────────────────────

        private bool EvaluateCondition(string line, Dictionary<string, string> vars)
        {
            // Strip "if " / "elif " prefix and trailing ":"
            string expr = IfElifPrefixRegex.Replace(line, "").TrimEnd(':').Trim();

            // PARSE-FIRST (cond-not-injection P0): do NOT SubstituteVars on the
            // whole expression before parsing structure. Doing so let a chat-derived
            // value masquerade as operator syntax — e.g. a message starting with
            // "not" makes {user.command}="not", so `if {user.command} == "x":`
            // substituted to `not == "x"`, which EvalSingle parsed as unary
            // negation of (== "x") → TRUE, firing EVERY command gate. The same
            // class covered ` and `/` or `/comparison operators/` in `/`.startswith(`
            // injected through chat. Fix: parse the condition STRUCTURE from the
            // (un-substituted) template, then push SubstituteVars down to the leaf
            // operands inside EvalSingle so a value can never become structure.
            //
            // QC01-05 — Quote-aware split so " and "/" or " inside a string
            // literal (e.g. if {msg} == "salt and pepper":) doesn't split
            // the condition mid-literal. The split runs on the template; an
            // operator hiding inside a {var} value isn't present yet, so it
            // cannot be picked as a separator.
            if (IndexOfOutsideQuotes(expr, " and ") >= 0)
                return SplitOutsideQuotes(expr, " and ").All(p => EvalSingle(p.Trim(), vars));
            if (IndexOfOutsideQuotes(expr, " or ") >= 0)
                return SplitOutsideQuotes(expr, " or ").Any(p => EvalSingle(p.Trim(), vars));

            return EvalSingle(expr, vars);
        }

        // PARSE-FIRST (cond-not-injection): structural tokens (parens, leading
        // `not `, convert.to_* wrappers, .startswith/.endswith, comparison
        // operators, ` in `) are detected on the UN-substituted template `expr`.
        // SubstituteVars(vars) is applied only at the leaves — the operands that
        // are actually compared / tested for truthiness — so a variable's value
        // (e.g. chat text "not ...") can never be reinterpreted as expression
        // syntax. `vars` is threaded through every recursive call.
        private bool EvalSingle(string expr, Dictionary<string, string> vars)
        {
            expr = expr.Trim();

            // Strip optional outer parens so "not (x == y)" works. We must verify
            // the inner string itself is paren-balanced — otherwise "(a) and (b)"
            // would be incorrectly stripped to "a) and (b".
            while (expr.Length >= 2 && expr.StartsWith("(") && expr.EndsWith(")") && IsBalanced(expr[1..^1]))
                expr = expr[1..^1].Trim();

            // Unary negation — recurse on the remainder. Mirrors how the exporter
            // emits whole-expression negation via EmitConditional ("if not (cond):").
            // Runs BEFORE operator search so "not foo == bar" is parsed as
            // "not (foo == bar)", matching the exporter contract.
            if (expr.StartsWith("not ", StringComparison.OrdinalIgnoreCase))
                return !EvalSingle(expr.Substring(4).Trim(), vars);

            // Standalone convert.to_*(...) wrappers — evaluate synchronously so
            // they work inside if/or/and conditions where EvalSingle is sync.
            // Must run BEFORE the operator search so the wrapper isn't mistaken
            // for an arithmetic comparand.
            if (expr.StartsWith("convert.to_bool(", StringComparison.OrdinalIgnoreCase) && expr.EndsWith(")"))
            {
                //  StripQuotesAndUnescape rather than bare Trim('"') so
                // a literal like convert.to_bool("yes\n") decodes the embedded
                // escapes before ParseTruthy sees the value.
                string inner = StripQuotesAndUnescape(SubstituteVars(expr["convert.to_bool(".Length..^1].Trim(), vars));
                return ParseTruthy(inner);
            }
            if (expr.StartsWith("convert.to_int(", StringComparison.OrdinalIgnoreCase) && expr.EndsWith(")"))
            {
                string inner = StripQuotesAndUnescape(SubstituteVars(expr["convert.to_int(".Length..^1].Trim(), vars));
                return double.TryParse(inner, out double iv) && iv != 0;
            }
            if (expr.StartsWith("convert.to_float(", StringComparison.OrdinalIgnoreCase) && expr.EndsWith(")"))
            {
                string inner = StripQuotesAndUnescape(SubstituteVars(expr["convert.to_float(".Length..^1].Trim(), vars));
                return double.TryParse(inner, out double fv) && fv != 0;
            }
            if (expr.StartsWith("convert.to_string(", StringComparison.OrdinalIgnoreCase) && expr.EndsWith(")"))
            {
                string inner = StripQuotesAndUnescape(SubstituteVars(expr["convert.to_string(".Length..^1].Trim(), vars));
                return !string.IsNullOrEmpty(inner);
            }

            // L22 — Recognize Pythonic `<expr>.startswith(<arg>)` /
            // `<expr>.endswith(<arg>)` calls so the L22 ExporterRegistry
            // emission `if msg.startswith("!alias "):` actually evaluates
            // (pre-fix only the exact-equality arm matched). The receiver/arg
            // are matched on the TEMPLATE; TryEvalStringMethod substitutes them
            // (cond-not-injection) so chat text can't smuggle a leading `not `
            // or operator. Comparison is Ordinal so the boundary check ("!hello"
            // vs "!helloworld") is byte-exact. Returns false on parse miss
            // so the literal-truthy fallthrough still applies.
            if (TryEvalStringMethod(expr, vars, out bool methodResult))
                return methodResult;

            // Find the operator that appears earliest in the expression.
            // Checking all candidates and picking the lowest index prevents a longer operator
            // (e.g. ">=") being skipped because a shorter one (e.g. ">") matched first when
            // iterating in array order.
            // QC01-13 — Quote-aware search so an operator inside a string literal
            // (e.g. "a==b" == "a==b") isn't picked as the split point. Mirrors
            // QC01-05's helper.
            string? bestOp  = null;
            int     bestIdx = int.MaxValue;
            foreach (var op in new[] { "!=", ">=", "<=", "==", ">", "<" })
            {
                int idx = IndexOfOutsideQuotes(expr, op);
                if (idx >= 0 && idx < bestIdx)
                {
                    bestIdx = idx;
                    bestOp  = op;
                }
            }

            if (bestOp != null)
            {
                //  StripQuotesAndUnescape on each side so a comparison
                // like `{name} == "Line A\nLine B"` matches the variable's
                // literal newline-bearing value instead of the raw `\n` text.
                string L  = StripQuotesAndUnescape(SubstituteVars(expr.Substring(0, bestIdx).Trim(), vars));
                string R  = StripQuotesAndUnescape(SubstituteVars(expr.Substring(bestIdx + bestOp.Length).Trim(), vars));
                // Peel any surrounding convert.to_*(...) call so comparisons like
                // "convert.to_int({x}) > 5" reach the inner literal directly.
                L = PeelConvertWrapper(L);
                R = PeelConvertWrapper(R);
                // Normalize boolean shorthands so equality comparisons against
                // convert.to_bool wrappers behave intuitively.
                string lNorm = NormalizeBoolish(L);
                string rNorm = NormalizeBoolish(R);
                bool   ln = double.TryParse(L, out double lv);
                bool   rn = double.TryParse(R, out double rv);
                return bestOp switch
                {
                    "==" => lNorm == rNorm,
                    "!=" => lNorm != rNorm,
                    ">"  => ln && rn && lv > rv,
                    "<"  => ln && rn && lv < rv,
                    ">=" => ln && rn && lv >= rv,
                    "<=" => ln && rn && lv <= rv,
                    _    => false
                };
            }

            // Membership check: "X in comma,separated,list".
            // M13 — share the engine's SplitListWithEscape helper so `\,` decodes
            // to a literal comma both here and in for_each (L10). Without the
            // shared escape, Logic.EnumMatch and the engine's `in` operator
            // would corrupt every list whose entries contain commas (chat
            // messages, JSON-ish payloads). Dropping individual surrounding
            // quotes after the split mirrors prior behavior.
            // QC01-13 — Quote-aware " in " search so a literal " in " inside
            // a quoted operand (e.g. "stand in line" in foo,bar) doesn't get
            // picked as the membership operator.
            int inIdxQA = IndexOfOutsideQuotes(expr, " in ");
            if (inIdxQA >= 0)
            {
                //  StripQuotesAndUnescape on both sides; also decode
                // each list entry so commas (\,) and embedded newlines
                // (\n) inside quoted entries are honored intentfully.
                string lhs   = StripQuotesAndUnescape(SubstituteVars(expr.Substring(0, inIdxQA).Trim(), vars));
                string rhs   = StripQuotesAndUnescape(SubstituteVars(expr.Substring(inIdxQA + 4).Trim(), vars));
                var    items = SplitListWithEscape(rhs).Select(x => StripQuotesAndUnescape(x));
                return items.Contains(lhs, StringComparer.OrdinalIgnoreCase);
            }

            // Boolean literal check — invariant culture so a tr-TR host doesn't
            // map 'true' through the dotless-i transform. QC01-04 sweep.
            //  StripQuotesAndUnescape so `"true\n"` (whitespace-padded by
            // an emit quirk) still classifies as a boolean.
            return StripQuotesAndUnescape(SubstituteVars(expr, vars)).Trim().ToLowerInvariant() is "true" or "1" or "yes";
        }

        /// <summary>
        /// L22 — Recognize the Pythonic string-method shape `<receiver>.startswith(<arg>)`
        /// and `<receiver>.endswith(<arg>)` inside a single condition expression and
        /// evaluate it against the resolved (already SubstituteVars'd) operands.
        /// Returns true and writes the result via <paramref name="result"/> when the
        /// shape matches; returns false (with <c>result = false</c>) when the
        /// expression isn't one of these calls so the caller's fallthrough still
        /// runs. Comparison is byte-exact (Ordinal) — that's the boundary check
        /// that stops "!hello" from spuriously matching "!helloworld".
        /// </summary>
        private static readonly Regex StringMethodCallRegex =
            new(@"^(?<recv>.+?)\.(?<method>startswith|endswith)\s*\(\s*(?<arg>.*)\s*\)\s*$",
                RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private bool TryEvalStringMethod(string expr, Dictionary<string, string> vars, out bool result)
        {
            result = false;
            if (string.IsNullOrEmpty(expr)) return false;
            // Skip cheap if there's no `.` at all — most conditions don't use methods.
            if (expr.IndexOf('.') < 0) return false;

            var m = StringMethodCallRegex.Match(expr);
            if (!m.Success) return false;

            //  StripQuotesAndUnescape on receiver/arg so .startswith /
            // .endswith comparisons honor embedded escapes (e.g. an alias
            // like "Hello\nworld".startswith("Hello\n")). cond-not-injection:
            // the shape is matched on the template, then the receiver/arg are
            // SubstituteVars'd here so a {var} value can't smuggle a leading
            // `not `/operator into the receiver before this method matched it.
            string receiver = StripQuotesAndUnescape(SubstituteVars(m.Groups["recv"].Value.Trim(), vars));
            string method   = m.Groups["method"].Value.ToLowerInvariant();
            string arg      = StripQuotesAndUnescape(SubstituteVars(m.Groups["arg"].Value.Trim(), vars));

            result = method switch
            {
                "startswith" => receiver.StartsWith(arg, StringComparison.Ordinal),
                "endswith"   => receiver.EndsWith(arg,   StringComparison.Ordinal),
                _            => false,
            };
            return true;
        }

        /// <summary>
        /// Returns true if the parens in <paramref name="s"/> are balanced.
        /// Used by <see cref="EvalSingle"/> to safely strip outer parens — we only
        /// strip when the inner string is itself balanced, otherwise a string like
        /// "(a) and (b)" would be incorrectly reduced to "a) and (b".
        /// </summary>
        private static bool IsBalanced(string s)
        {
            int depth = 0;
            bool inQuote = false;
            foreach (char c in s)
            {
                if (c == '"') { inQuote = !inQuote; continue; }
                if (inQuote) continue;
                if (c == '(') depth++;
                else if (c == ')') { depth--; if (depth < 0) return false; }
            }
            return depth == 0;
        }

        /// <summary>
        /// Strips a single outer convert.to_int / to_float / to_string / to_bool
        /// wrapper from <paramref name="s"/>, returning the inner string. If the
        /// expression isn't wrapped, returns it unchanged. Lets comparison
        /// operands tunnel through the typeless conversion calls.
        /// </summary>
        private static string PeelConvertWrapper(string s)
        {
            foreach (var p in new[] { "convert.to_int(", "convert.to_float(", "convert.to_string(", "convert.to_bool(" })
            {
                if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase) && s.EndsWith(")"))
                    //  StripQuotesAndUnescape so a wrapped literal like
                    // convert.to_string("Line\nBreak") yields a value with
                    // an embedded newline rather than the raw `\n` text.
                    return StripQuotesAndUnescape(s.Substring(p.Length, s.Length - p.Length - 1).Trim());
            }
            return s;
        }

        /// <summary>
        /// Normalizes boolean shorthands so "==" / "!=" comparisons line up with
        /// the engine's "true/1/yes" contract. "1"/"yes" → "true", "0"/"no" →
        /// "false"; everything else passes through unchanged.
        /// </summary>
        private static string NormalizeBoolish(string s)
        {
            string lower = s.Trim().ToLowerInvariant();
            return lower switch
            {
                "1" or "yes" => "true",
                "0" or "no"  => "false",
                _            => s
            };
        }

        // ─────────────────────────────────────────────────────────────────
        // VARIABLE ASSIGNMENT
        // ─────────────────────────────────────────────────────────────────

        private async Task HandleAssignment(string line, Dictionary<string, string> vars)
        {
            var m = AssignmentParseRegex.Match(line);
            if (!m.Success) return;

            string key    = m.Groups[1].Value;
            string op     = m.Groups[2].Value;
            string rawFull = m.Groups[3].Value.Trim();

            string val;
            if (op == "=" && rawFull.StartsWith("not ", StringComparison.OrdinalIgnoreCase))
            {
                // RHS like "not global._flipflop" or "not convert.to_bool({x})".
                // The `not ` here is from the exporter template (a negate
                // assignment), so strip it and evaluate the remainder via
                // EvalSingle, which substitutes the operand at the leaf
                // (cond-not-injection — don't pre-substitute the whole RHS, that
                // would let a value introduce a spurious leading `not `).
                // Stored as the engine's canonical "true"/"false" — same contract
                // as EvalSingle's boolean-literal recognizer, so the result
                // round-trips through subsequent reads of this var (FlipFlopHandler
                // etc.) without re-parsing surprises.
                val = (!EvalSingle(rawFull.Substring(4).Trim(), vars)).ToString().ToLowerInvariant();
            }
            else if (op == "=" && CallShapeRegex.IsMatch(rawFull))
            {
                // RHS is a command call — execute it and capture the return value
                string? result = await ExecuteCommandWithResult(rawFull, vars);
                val = result ?? "";
            }
            else
            {
                //  StripQuotesAndUnescape: decode `\n` / `\r` / `\\` /
                // `\"` (plus `\t` / `\uXXXX` for forward-compatibility) so
                // an assignment like `msg = "Line A\nLine B"` lands a
                // two-line value in vars instead of the literal `\n` text.
                val = SubstituteVars(StripQuotesAndUnescape(rawFull), vars);
            }

            if (key.StartsWith("user.") || key.StartsWith("global."))
            {
                if (op == "=")
                {
                    await SetScriptVarAsync(key, val);
                }
                else
                {
                    // BH-006: compound assignment must accept floats. Previously a float-
                    // valued var (e.g. global.score = "3.5") silently no-op'd every += / -=
                    // because int.TryParse rejected it and the else-branch didn't exist.
                    // Try int first to preserve existing integer semantics, then fall back
                    // to double, and log when neither side parses (instead of swallowing).
                    string curStr = await ResolveVar(key, vars);
                    if (int.TryParse(curStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int curInt) &&
                        int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int vInt))
                    {
                        int res = op switch { "+=" => curInt + vInt, "-=" => curInt - vInt, "*=" => curInt * vInt, "/=" => vInt != 0 ? curInt / vInt : curInt, _ => curInt };
                        await SetScriptVarAsync(key, res.ToString(CultureInfo.InvariantCulture));
                    }
                    else if (double.TryParse(curStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double curD) &&
                             double.TryParse(val,    NumberStyles.Float, CultureInfo.InvariantCulture, out double vD))
                    {
                        double res = op switch { "+=" => curD + vD, "-=" => curD - vD, "*=" => curD * vD, "/=" => vD != 0.0 ? curD / vD : curD, _ => curD };
                        await SetScriptVarAsync(key, res.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        GlobalLogger.Log(
                            $"Compound assignment '{op}' skipped — neither side parses as int or float. key='{key}', cur='{curStr}', val='{val}'.",
                            "ScriptEngine", LogLevel.CriticalError);
                    }
                }
            }
            else
            {
                vars[key] = val;
                // P1 #1 phase 2 — fan local-scope assignment to debug subscribers
                // (Architect local-vars panel). Skip the engine's reserved bookkeeping
                // keys so the panel doesn't show internal state as "user variables".
                if (!IsReservedLocalKey(key))
                    FireVariableSet(key, val);
            }
        }

        // Local-var keys the engine writes for its own bookkeeping (parallel branches,
        // event-trigger return plumbing, internal flip-flops). Excluded from the
        // OnVariableSet feed so debug consumers see real user-authored writes only.
        private static bool IsReservedLocalKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return true;
            if (key.StartsWith("_",          StringComparison.Ordinal)) return true;
            if (key.StartsWith("public.",    StringComparison.OrdinalIgnoreCase)) return true; // surfaces via Public.Set's command path instead
            if (key.StartsWith("loop.",      StringComparison.OrdinalIgnoreCase)) return true;
            if (key.StartsWith("event.",     StringComparison.OrdinalIgnoreCase)) return true;
            if (key.StartsWith("Row.",       StringComparison.OrdinalIgnoreCase)) return true;
            if (key.StartsWith("result.",    StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ─────────────────────────────────────────────────────────────────
        // COMMAND EXECUTION
        // ─────────────────────────────────────────────────────────────────

        private async Task ExecuteCommand(string line, Dictionary<string, string> vars)
            => await ExecuteCommandWithResult(line, vars);

        private async Task<string?> ExecuteCommandWithResult(string line, Dictionary<string, string> vars)
        {
            var m = CommandParseRegex.Match(line);
            if (!m.Success)
            {
                //  Previously this branch silently skipped any line
                // that didn't match the `name(args)` shape — turning a
                // typo'd command into a no-op with zero diagnostics. Logging
                // at Communication tier surfaces it in the script-author's
                // feed so they can spot the broken line, without elevating
                // to CriticalError (the engine continues running the rest
                // of the block; the line is still dropped).
                GlobalLogger.Log(
                    $"parse: skipped line in {ScriptFile}: '{line}' — unrecognized command shape (expected name(args))",
                    "ScriptEngine", LogLevel.Communication);
                return null;
            }

            // QC01-04 — Invariant culture: command names are ASCII identifiers
            // (e.g. send_chat, db.find_row). On a tr-TR host the default ToLower()
            // maps 'I' → 'ı' (dotless i), which would corrupt names containing
            // 'I'/'i' and break command lookup. ToLowerInvariant is the contract.
            string func = m.Groups[1].Value.ToLowerInvariant();
            // R24 — SplitArgs returns a trimmed string[] directly; the
            // legacy Linq Select(Trim).ToArray() pass that allocated a
            // second array per call has been folded into SplitArgs itself.
            string[] rawArgs = SplitArgs(m.Groups[2].Value);

            // BH-001 — argument resolution must distinguish CODE (function calls present
            // in the script's source) from DATA (substituted variable content, including
            // chat input and event payloads). Previously the engine ran CallShapeRegex on
            // the SUBSTITUTED form too, so a chat user typing `db.set_variable(global.bot_owner, attacker)`
            // and any script using `{event.arg.message}` as an argument got remote
            // execution against the engine's full command surface.
            //
            // Rule of thumb implemented below:
            //   • Literal `func(args)` in the script source         → recurse (path 1)
            //   • Literal `key=func(args)` in the script source     → recurse on the rhs (path 2)
            //   • Anything that depends on substitution             → data, never re-parsed.
            //
            // R23 — call/equals matchers are static readonly (CallShapeRegex,
            // KeyValueArgRegex) so they're not re-instantiated per command.
            string[] args = new string[rawArgs.Length];
            for (int idx = 0; idx < rawArgs.Length; idx++)
            {
                // QC01-11 — Do NOT strip outer quotes before call-shape
                // classification. A literal `"send_chat(\"hello\")"` argument
                // is a STRING value the user wants to pass through verbatim;
                // pre-stripping the surrounding quotes made it look like a
                // function call and triggered a recursive execution. Keep the
                // quote-strip on the default data path only.
                string raw = rawArgs[idx];
                //  Strip outer quotes AND decode escape sequences the
                // exporter emitted inside the literal (\n, \r, \\, \" — plus
                // \t / \uXXXX for forward-compatibility with 's
                // emit changes). For unquoted args the helper returns the
                // value unchanged, preserving the legacy contract for
                // identifier-style positional args (global.score, etc.).
                string trimmedNoQuotes = StripQuotesAndUnescape(raw);

                // (1) Pre-substitution: raw arg is itself a function call —
                //     let the recursive invocation handle its own SubstituteVars
                //     at the correct argument boundaries (prevents comma leakage).
                //     Only fires when the unquoted raw text is itself a call (no
                //     surrounding quotes in the source).
                if (CallShapeRegex.IsMatch(raw))
                {
                    string? innerResult = await ExecuteCommandWithResult(raw, vars);
                    args[idx] = innerResult ?? "";
                    continue;
                }

                // (2) Pre-substitution: raw is `key=func(args)` literal in the script
                //     source. The right-hand side is recursed (via RegisterCommand) but
                //     we still control which text reaches the recursion — the rhs comes
                //     from the script, not from a variable.
                var rawEqMatch = KeyValueArgRegex.Match(raw);
                if (rawEqMatch.Success && CallShapeRegex.IsMatch(rawEqMatch.Groups[2].Value.Trim()))
                {
                    string? innerResult = await ExecuteCommandWithResult(rawEqMatch.Groups[2].Value.Trim(), vars);
                    args[idx] = $"{rawEqMatch.Groups[1].Value}={innerResult ?? ""}";
                    continue;
                }

                // Default path: substitute and treat the result as data. No further
                // re-parsing — even if the substituted text happens to look like a
                // function call, we will NOT execute it. Quote-strip happens here
                // so quoted string-literal args pass through with their outer quotes
                // removed exactly as before.
                args[idx] = SubstituteVars(trimmedNoQuotes, vars);
            }

            if (_commands.TryGetValue(func, out var cmd))
            {
                // Per-command trace removed: it fired once per node executed and
                // flooded the System Log on any non-trivial graph. Per-execution
                // summaries (one line per script run) live at the call sites in
                // ScriptManager and remain. Re-add a debug-only trace here only
                // if a future verbose-script-logging setting is added.

                // R19 (sweep 14a) — typed-arg binding. If the command was
                // registered via CommandManifest.AddTyped, bind the raw args
                // against the spec and stash the BoundArgs in AsyncLocal so
                // the handler can pull typed values via CurrentBoundArgs.
                // Save+restore the prior value across the await so nested
                // event.trigger calls don't strand the outer binding.
                //
                // Sweep 18 — CommandSpec.Args is non-nullable; commands not in
                // the manifest (test-fixture handlers injected directly into
                // _commands) still hit the bound = null path via the
                // TryGetValue gate. Zero-arg manifest entries flow through
                // BindArgs with an empty spec list — the binder skips its
                // for-loop and returns a BoundArgs with an empty dict, which
                // is correct.
                BoundArgs? previousBound = _currentBoundArgs.Value;
                BoundArgs? bound = null;
                if (CommandManifest.All.TryGetValue(func, out var spec))
                {
                    var bindErrors = new List<string>();
                    bound = CommandBinder.BindArgs(spec.Args, args, bindErrors);
                    if (bindErrors.Count > 0)
                    {
                        GlobalLogger.Log(
                            $"CommandBinder: '{func}' arg coercion issues — {string.Join("; ", bindErrors)}",
                            "ScriptEngine", LogLevel.LogicExecution);
                    }
                }
                _currentBoundArgs.Value = bound;
                try
                {
                    return await cmd(args);
                }
                finally
                {
                    _currentBoundArgs.Value = previousBound;
                }
            }

            GlobalLogger.Log($"Unknown command: '{func}' (raw args: '{m.Groups[2].Value}') in {ScriptFile}", "ScriptEngine", LogLevel.CriticalError);
            return null;
        }

        // SubstituteVars, ResolveSystemVar, IsPositionalPlaceholder, ResolveVar
        // moved to ScriptEngine.Variables.cs ().

        // GetIndent, StripInlineComment, FindBlockEnd, ExtractArgs, SplitArgs,
        // ParseTruthy, SplitListWithEscape moved to ScriptEngine.Utilities.cs ().

        // ─────────────────────────────────────────────────────────────────
        //  Inverse of ScriptExporter.EscapeStringLiteral. The exporter
        // backslash-escapes characters that would otherwise break a `"..."`
        // literal in the emitted .phx (`\\`, `\"`, `\n`, `\r`, and as of
        //  also `\t` plus arbitrary control chars via `\uXXXX`); the
        // engine must reverse that on every value the parser hands back to a
        // command/condition. Without this, a literal `"Hello\nWorld"` in the
        // .phx reaches the runtime as the eight-character string `Hello\nWorld`
        // instead of the seven-char one with an embedded newline.
        //
        // Forward-compatible with 's emit additions: handles `\t`,
        // `\b`, `\f`, and `\uXXXX` even though the current exporter doesn't
        // emit them yet. Unknown escapes (e.g. `\q`) drop the backslash and
        // keep the literal char — matches the user's mental model that the
        // backslash is the escape introducer, not part of the value.
        //
        // Marked internal so the parallel test assembly can pin the
        // exporter↔engine round-trip on every escape sequence.
        // ─────────────────────────────────────────────────────────────────
        /// <summary>
        ///  Strip outer `"..."` quotes from <paramref name="s"/> and decode
        /// any backslash escapes the exporter emitted inside the literal. Used
        /// at the parser boundaries that previously called bare `Trim('"')` —
        /// the quote-strip alone left `\n` / `\r` / `\"` / `\\` literal in the
        /// command-argument stream. For inputs WITHOUT surrounding quotes,
        /// returns the value unchanged so unquoted args like `global.score`
        /// don't get falsely decoded.
        /// </summary>
        internal static string StripQuotesAndUnescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                return UnescapeStringLiteral(s.Substring(1, s.Length - 2));
            return s;
        }

        internal static string UnescapeStringLiteral(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            // Fast path: no backslashes → nothing to decode. The vast majority
            // of script args have no escape sequences, so this keeps the
            // common-case cost at one scan.
            if (s.IndexOf('\\') < 0) return s;

            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char next = s[i + 1];
                switch (next)
                {
                    case '\\': sb.Append('\\'); i++; break;
                    case '"':  sb.Append('"');  i++; break;
                    case '\'': sb.Append('\''); i++; break;
                    case 'n':  sb.Append('\n'); i++; break;
                    case 'r':  sb.Append('\r'); i++; break;
                    case 't':  sb.Append('\t'); i++; break;
                    case 'b':  sb.Append('\b'); i++; break;
                    case 'f':  sb.Append('\f'); i++; break;
                    case '0':  sb.Append('\0'); i++; break;
                    case 'u':
                        // \uXXXX — exactly four hex digits. If the sequence is
                        // malformed (too few digits, non-hex chars), keep the
                        // raw backslash + 'u' so we don't silently swallow
                        // user-typed text that just happened to contain '\u'.
                        if (i + 5 < s.Length &&
                            TryParseHex4(s, i + 2, out int code))
                        {
                            sb.Append((char)code);
                            i += 5;
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                    default:
                        // Unknown escape — drop the backslash, keep the char.
                        sb.Append(next);
                        i++;
                        break;
                }
            }
            return sb.ToString();
        }

        private static bool TryParseHex4(string s, int idx, out int value)
        {
            value = 0;
            for (int k = 0; k < 4; k++)
            {
                char ch = s[idx + k];
                int d;
                if      (ch >= '0' && ch <= '9') d = ch - '0';
                else if (ch >= 'a' && ch <= 'f') d = 10 + (ch - 'a');
                else if (ch >= 'A' && ch <= 'F') d = 10 + (ch - 'A');
                else { value = 0; return false; }
                value = (value << 4) | d;
            }
            return true;
        }

        // ─────────────────────────────────────────────────────────────────
        // QC01-08 — Bounded MRU regex cache.
        //
        // The bare-name SubstituteVars path used a ConcurrentDictionary keyed by
        // var name that grew unboundedly over the process lifetime. This wrapper
        // caps the cache at `capacity` entries via an MRU policy:
        //
        //   • LinkedList<KeyValuePair<string,Regex>> tracks usage order
        //     (front = most recently used, tail = next eviction candidate);
        //   • Dictionary<string, LinkedListNode<...>> fronts it for O(1) lookup.
        //
        // GetOrAdd:
        //   • Hit  → move the node to the front, return the cached Regex.
        //   • Miss → invoke factory, append at front, evict from tail if over cap.
        //
        // Thread-safety: a single lock serializes all access. The hot path
        // (SubstituteVars) is per-script-execution and not contended enough to
        // need lock striping; the prior ConcurrentDictionary made the lookup
        // lock-free but the contention budget on a chat-heavy stream is well
        // within a single-lock budget (the work behind the lock is two
        // hashtable ops + one list-splice, all O(1)).
        //
        // Intentional scope: ScriptEngine-private (no need to expose). Tests
        // exercise behaviour through SubstituteVars's public surface.
        // ─────────────────────────────────────────────────────────────────
        private sealed class BoundedMruRegexCache
        {
            private readonly int _capacity;
            private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, Regex>>> _index;
            private readonly LinkedList<KeyValuePair<string, Regex>> _order = new();
            private readonly object _lock = new();

            public BoundedMruRegexCache(int capacity)
            {
                if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
                _capacity = capacity;
                _index    = new Dictionary<string, LinkedListNode<KeyValuePair<string, Regex>>>(capacity, StringComparer.Ordinal);
            }

            public Regex GetOrAdd(string key, Func<string, Regex> factory)
            {
                lock (_lock)
                {
                    if (_index.TryGetValue(key, out var existing))
                    {
                        // MRU bump — move to front.
                        _order.Remove(existing);
                        _order.AddFirst(existing);
                        return existing.Value.Value;
                    }
                }
                // Build the regex OUTSIDE the lock — Regex compilation is the
                // expensive part and we'd rather not hold the lock during JIT.
                // Two threads racing on the same missing key will each build a
                // Regex, then the second insertion wins via the re-check below.
                var rx = factory(key);
                lock (_lock)
                {
                    if (_index.TryGetValue(key, out var raced))
                    {
                        _order.Remove(raced);
                        _order.AddFirst(raced);
                        return raced.Value.Value;
                    }
                    var node = new LinkedListNode<KeyValuePair<string, Regex>>(new KeyValuePair<string, Regex>(key, rx));
                    _order.AddFirst(node);
                    _index[key] = node;
                    if (_index.Count > _capacity && _order.Last is { } evict)
                    {
                        _order.RemoveLast();
                        _index.Remove(evict.Value.Key);
                    }
                    return rx;
                }
            }

            // Test/diagnostics hook — not load-bearing on the hot path.
            internal void Clear()
            {
                lock (_lock)
                {
                    _index.Clear();
                    _order.Clear();
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // QC01-08 — Hook to reset Flow.DoN counters for a single script.
        //
        // _doNCounters is keyed by "<ScriptFile>:line_<hexIndex>" so each call
        // site has its own slot. The counters intentionally survive across
        // triggers — that's the whole point of "do N times" being durable.
        // BUT if Architect re-saves a .phxg and the exporter re-emits the .phx
        // with new line indices, the OLD keys for that file become dead weight.
        // LogicWatcher's reload pathway is the right place to invoke this; the
        // wiring lives in another slice, so for now this method just provides
        // the surface and clears anything keyed under the named script file.
        // ─────────────────────────────────────────────────────────────────
        public void ResetDoNCountersForScript(string scriptFile)
        {
            if (string.IsNullOrEmpty(scriptFile)) return;
            string prefix = scriptFile + ":line_";
            lock (_doNCountersLock)
            {
                // Tolerate the engine's _doNCounters being keyed under either
                // the bare file name or a fully-qualified path. We match by
                // exact prefix against the key as-stored.
                var doomed = new List<string>();
                foreach (var kv in _doNCounters)
                {
                    if (kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                        doomed.Add(kv.Key);
                }
                foreach (var k in doomed) _doNCounters.Remove(k);
            }
        }
    }
}
