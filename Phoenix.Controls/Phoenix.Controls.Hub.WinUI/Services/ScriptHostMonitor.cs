using System.Collections.Concurrent;
using System.IO;
using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.WinUI.Contracts;

namespace Phoenix.Controls.Hub.WinUI.Services;

// Script panel data + control surface. The existing Hub splits its script
// state across two singletons:
//
//   • ScriptRegistry holds the on-disk catalogue: FileName, FullPath,
//     IsEnabled, LastRunAt, LastCpuPercent, LastMemKb. Fires OnChanged on
//     add/remove/SetEnabled/RecordExecution.
//   • ScriptManager fires ScriptLifecycle (Running / Finished / Error /
//     Trigger) with a ScriptName when an execution slot is acquired or
//     completes. It does NOT track cumulative run counts or last error
//     text — those live nowhere today.
//
// The IScriptHostMonitor contract wants both flavours plus a RunCount and a
// LastError string. We synthesise the missing pieces in-memory:
//   - RunCount: ++ on every Phase==Finished or Phase==Error
//   - LastError: latched to "(cancelled or timed out)" on Phase==Error,
//     cleared on the next successful Finished. The script engine logs the
//     real exception via GlobalLogger.Error, so deeper detail stays in the
//     System Log panel — this is just a terse marker for the row.
//   - Current ScriptState: derived from the latest Phase observed
//     (default Idle; Phase==Queued ⇒ Queued; Phase==Running ⇒ Running;
//     Phase==Finished ⇒ Idle; Phase==Error ⇒ Errored; Phase==Trigger and
//     Phase==Discarded don't change row state).
//   - Queued is wired now: ScriptManager fires it before each
//     chat/event/webhook WaitAsync, and the row flips to yellow ("queued")
//     until BeginExecutionTracked fires Running after the slot is acquired.
public sealed class ScriptHostMonitor : IScriptHostMonitor, IDisposable
{
    private sealed class Live
    {
        public ScriptState State    { get; set; } = ScriptState.Idle;
        public int         RunCount { get; set; }
        public string?     LastError;
    }

    private readonly IUiDispatcher _ui;
    private readonly IPillarNavigator _navigator;
    private readonly ConcurrentDictionary<string, Live> _live = new(StringComparer.OrdinalIgnoreCase);

    private readonly Action _onRegistryChanged;
    private readonly Action<ScriptManager.ScriptLifecycleEvent> _onLifecycle;
    // Fan-out: every subscriber to StatusChanged
    // receives every update; no primary-subscriber gate.
    private int _disposed;

    public ScriptHostMonitor(IUiDispatcher uiDispatcher, IPillarNavigator navigator)
    {
        _ui = uiDispatcher;
        _navigator = navigator;

        _onRegistryChanged = () =>
        {
            // Bulk change (load/refresh/enable-toggle/RecordExecution): emit
            // one StatusChanged per known script so panels can refresh rows.
            // Keep it cheap — Snapshot() is what the panel will call to repaint.
            foreach (var info in ScriptRegistry.Instance.GetAll())
                Raise(BuildStatus(info));
        };

        _onLifecycle = evt =>
        {
            // ★ Live PROCESS instances are excluded outright, before any state is
            // taken. They fire lifecycle events under a synthetic
            // "process::<guid>" ScriptName, but ScriptRegistry keeps them in its
            // separate `_processInstances` map — `GetAll()` returns `_scripts`
            // only — so the resolve below could never match one, and the fallback
            // synthesised a status with `Path: ""`. ScriptViewModel keys its rows
            // by Path, so EVERY unresolvable instance landed on one shared ""
            // row whose label flipped between instances as they ran: N live
            // processes rendered as one row lying about all of them.
            //
            // Excluding is honest rather than lossy — a process instance has no
            // Scripts-panel row today either way; it only corrupted a shared one.
            // The real home is the Routines panel (TODO RT1), which needs the
            // enumeration APIs and the `kind` discriminator on ScriptStatus that
            // RT0 adds. Delete this guard when RT0 lands; until then it also
            // stops `_live` growing one permanent entry per spawned instance id.
            if (IsProcessInstanceName(evt.ScriptName)) return;

            var live = _live.GetOrAdd(evt.ScriptName, _ => new Live());
            switch (evt.Phase)
            {
                case ScriptManager.ScriptExecutionPhase.Queued:
                    // Fires before WaitAsync on the chat/event/
                    // webhook semaphore path. Row flips to yellow; the
                    // subsequent Running phase from BeginExecutionTracked
                    // promotes it to green once the slot is held.
                    live.State = ScriptState.Queued;
                    break;
                case ScriptManager.ScriptExecutionPhase.Running:
                    live.State = ScriptState.Running;
                    break;
                case ScriptManager.ScriptExecutionPhase.Finished:
                    live.State    = ScriptState.Idle;
                    live.RunCount++;
                    live.LastError = null;
                    break;
                case ScriptManager.ScriptExecutionPhase.Error:
                    live.State    = ScriptState.Errored;
                    live.RunCount++;
                    live.LastError = "execution cancelled or timed out — see System Log for detail";
                    break;
                case ScriptManager.ScriptExecutionPhase.Trigger:
                    // Decorative ping — a non-chat trigger node fired. Don't
                    // perturb State/RunCount; the panel may flash the row but
                    // the Snapshot() row stays stable.
                    return;
                case ScriptManager.ScriptExecutionPhase.Discarded:
                    // Discard-policy drop — the trigger never ran, so this is
                    // neither an error nor a completed run. Don't touch State /
                    // RunCount / LastError: the row keeps reflecting whatever
                    // in-flight execution caused the discard, and ScriptManager's
                    // Communication-tier log line is the visible trace. (Live has
                    // no neutral "last trigger" field to stamp, so this is a
                    // deliberate no-op.)
                    return;
            }

            // Resolve the registry row (FileName matches ScriptName; engine
            // uses the .phx filename as the canonical id). If the registry
            // hasn't seen the file yet — e.g. an event during initial
            // LoadScripts — synthesise a minimal status so the lifecycle
            // event isn't lost.
            var info = ScriptRegistry.Instance
                .GetAll()
                .FirstOrDefault(s => string.Equals(s.FileName, evt.ScriptName, StringComparison.OrdinalIgnoreCase));
            ScriptStatus status = info != null
                ? BuildStatus(info)
                : new ScriptStatus(
                    Path:         "",
                    Name:         StripExtension(evt.ScriptName),
                    State:        live.State,
                    Enabled:      true,
                    LastCpu:      TimeSpan.Zero,
                    LastRamBytes: 0,
                    RunCount:     live.RunCount,
                    LastError:    live.LastError);
            Raise(status);
        };

        ScriptRegistry.Instance.OnChanged   += _onRegistryChanged;
        ScriptManager.Instance.ScriptLifecycle += _onLifecycle;
    }

    public IReadOnlyList<ScriptStatus> Snapshot()
    {
        var all = ScriptRegistry.Instance.GetAll();
        var result = new List<ScriptStatus>();
        foreach (var info in all)
            result.Add(BuildStatus(info));
        return result;
    }

    public event EventHandler<ScriptStatus>? StatusChanged;

    public Task SetEnabledAsync(string scriptName, bool enabled, CancellationToken ct = default)
    {
        // ScriptRegistry stores scripts keyed by FileName ("foo.phx"), but
        // panels may pass "foo" without the extension — accept both.
        string fileName = ResolveFileName(scriptName);
        ScriptRegistry.Instance.SetEnabled(fileName, enabled);
        return Task.CompletedTask;
    }

    public Task ReloadAsync(string scriptName, CancellationToken ct = default)
    {
        // The registry doesn't expose a single-file refresh path; Refresh()
        // re-scans the whole logic dir. Cheap (~1ms per file under normal
        // load) and keeps the on-disk and in-memory views in lockstep with
        // what LogicWatcher would produce on its 250 ms debounce.
        ScriptRegistry.Instance.Refresh();
        return Task.CompletedTask;
    }

    public async Task OpenInArchitectAsync(string scriptName, CancellationToken ct = default)
    {
        string fileName = ResolveFileName(scriptName);
        var info = ScriptRegistry.Instance
            .GetAll()
            .FirstOrDefault(s => string.Equals(s.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        if (info is null || string.IsNullOrEmpty(info.FullPath))
        {
            GlobalLogger.Log(
                $"ScriptHostMonitor: cannot open '{scriptName}' in Architect — script not registered.",
                "ScriptHostMonitor", LogLevel.Communication);
            return;
        }

        // The Architect editor authors .phxg graphs; .phx is the exporter
        // output. Same directory, same stem, swap .phx for .phxg. If the
        // .phxg sibling is missing, fall back to opening the .phx — the
        // navigator's destination handles the "graph not found" case.
        // The File.Exists probe is offloaded to a background thread so the
        // UI thread never blocks on slow storage while the user clicks
        // "Edit in Architect".
        string phxgPath = Path.ChangeExtension(info.FullPath, ".phxg");
        bool phxgExists = await Task.Run(() => File.Exists(phxgPath), ct).ConfigureAwait(true);
        if (!phxgExists) phxgPath = info.FullPath;

        // Single-HUB collapse: no more sibling exe spawn.
        // Hub.MainWindow swaps MainPaneRegion to the Architect tab via
        // IPillarNavigator. The deep-link open hint is reserved for a
        // future Architect tab activation handler.
        _navigator.NavigateTo(PillarKind.Architect, phxgPath);
    }

    private void Raise(ScriptStatus status)
    {
        _ui.Post(() => StatusChanged?.Invoke(this, status));
    }

    private ScriptStatus BuildStatus(ScriptInfo info)
    {
        _live.TryGetValue(info.FileName, out var live);
        // Real CPU time recorded by ScriptManager.TimedExecuteAsync. The older
        // path multiplied LastCpuPercent by 10 ms and produced readings off
        // by orders of magnitude on short scripts (a 5 ms run with 10% CPU
        // rendered as "100ms"). LastCpuMs is the actual elapsed CPU time;
        // keep the legacy 0-on-missing fallback so a never-run script still
        // shows a clean "0ms" rather than dashes.
        double cpuMs  = info.LastCpuMs ?? 0d;
        var lastCpu   = TimeSpan.FromMilliseconds(Math.Max(0d, cpuMs));
        long lastRam  = (info.LastMemKb ?? 0) * 1024L;

        return new ScriptStatus(
            Path:         info.FullPath ?? "",
            Name:         StripExtension(info.FileName),
            State:        live?.State    ?? ScriptState.Idle,
            Enabled:      info.IsEnabled,
            LastCpu:      lastCpu,
            LastRamBytes: lastRam,
            RunCount:     live?.RunCount ?? 0,
            LastError:    live?.LastError);
    }

    /// <summary>
    /// True for the synthetic script name a live process instance executes under
    /// (<c>"process::&lt;instanceId&gt;"</c>, minted in
    /// <c>ScriptManager.Process.cs</c> / <c>ScriptRegistry.RegisterProcessInstance</c>).
    /// Ordinal, matching how both of those sites build and split the prefix.
    /// </summary>
    internal static bool IsProcessInstanceName(string? scriptName)
        => scriptName != null && scriptName.StartsWith("process::", StringComparison.Ordinal);

    private static string ResolveFileName(string scriptName)
    {
        if (string.IsNullOrEmpty(scriptName)) return "";
        return scriptName.EndsWith(".phx", StringComparison.OrdinalIgnoreCase)
            ? scriptName
            : scriptName + ".phx";
    }

    private static string StripExtension(string fileName) =>
        fileName.EndsWith(".phx", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        ScriptRegistry.Instance.OnChanged -= _onRegistryChanged;
        ScriptManager.Instance.ScriptLifecycle -= _onLifecycle;
    }
}
