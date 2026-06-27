using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// LayerWatcher — FileSystemWatcher on `data/layers/*.phxlayer`.
    /// Mirrors the LogicWatcher pattern: any file change re-deserializes via LayerSerializer
    /// and updates LayerRegistry idempotently.
    ///
    /// M77: Editors save in multiple stages (write to .tmp → rename → modify timestamp), so
    /// FileSystemWatcher fires multiple events for one logical save. We debounce per-file at
    /// 250ms (mirroring LogicWatcher) and then call WaitForFileStable before the reload, so
    /// the reload pipeline pushes to OBS exactly once per logical save instead of 2-5 times.
    /// </summary>
    public class LayerWatcher : IDisposable
    {
        private FileSystemWatcher? _watcher;
        private readonly string _layersPath;
        private readonly LayerRegistry _registry;
        private readonly LayerRuntime? _runtime;

        // Debounce — one timer per file path via the shared PathDebouncer.
        // Mirrors LogicWatcher.DebounceMs.
        private const int DebounceMs = 250;
        private readonly PathDebouncer _debouncer = new();
        private int _disposed;

        // P1-19 — mirrors LogicWatcher . Default FSW InternalBufferSize is
        // 8 KB; a bulk-save burst (e.g. Visualist re-saving every .phxlayer after
        // a preset change) can overflow the kernel-side ring, raising
        // FileSystemWatcher.Error with ERROR_NOTIFY_ENUM_DIR and silently ending
        // layer hot-reload. 64 KB matches LogicWatcher and gives ~8x headroom in
        // the same non-paged pool. OneDrive working trees (Majo's) also raise
        // transient ERROR_INVALID_HANDLE on virtualisation — same Error path.
        private const int InternalBufferBytes = 64 * 1024;

        // Throttle recreate so an unrecoverable path doesn't spin us in a tight
        // FSW-construct loop. 5 s matches LogicWatcher.
        private static readonly TimeSpan RecreateThrottle = TimeSpan.FromSeconds(5);
        private DateTime _lastRecreateAttemptUtc = DateTime.MinValue;
        private readonly object _recreateLock = new();

        public string LayersPath => _layersPath;

        /// <summary>
        /// Test seam: tests inject a counter in place of the real Reload.
        /// Production callers leave this null — Reload(path) is invoked via the default path.
        /// </summary>
        internal Action<string>? ReloadDelegate { get; set; }

        /// <summary>
        /// QC05-03 / QC58-01 — test seam parallel to <see cref="ReloadDelegate"/>.
        /// Production callers leave this null; the default path forwards to
        /// <see cref="LayerRuntime.DiscardLayer"/> on the runtime supplied via the
        /// constructor (or the singleton fallback).
        /// </summary>
        internal Action<string>? DiscardDelegate { get; set; }

        /// <summary>Production constructor — uses default `data/layers/`, the singleton registry, and the singleton runtime.</summary>
        public LayerWatcher() : this(ResolveDefaultPath(), LayerRegistry.Instance, LayerRuntime.Instance) { }

        /// <summary>Test constructor — accepts an explicit folder + registry instance for isolation.</summary>
        public LayerWatcher(string layersPath, LayerRegistry registry)
            : this(layersPath, registry, runtime: null) { }

        /// <summary>
        /// QC05-03 / QC58-01 — explicit runtime injection so tests can supply a
        /// mock and production wires the singleton. A null runtime falls back to
        /// <see cref="LayerRuntime.Instance"/> at discard time so existing callers
        /// (BugFixSweep5_LayerWatcher_Tests) don't have to construct a runtime
        /// just to exercise the registry path.
        /// </summary>
        public LayerWatcher(string layersPath, LayerRegistry registry, LayerRuntime? runtime)
        {
            _layersPath = layersPath;
            _registry   = registry;
            _runtime    = runtime;
            Directory.CreateDirectory(_layersPath);
        }

        private static string ResolveDefaultPath() => Paths.HubLayers;

        public void Start()
        {
            // Initial pass — pick up any files already on disk before the watcher starts.
            ScanAll();

            // P1-19 — Start() is idempotent. If a prior watcher exists (re-Start
            // on hot-reconfigure), tear it down first so we don't leak the FSW.
            if (_watcher != null)
            {
                try { _watcher.EnableRaisingEvents = false; } catch { }
                try { _watcher.Dispose(); } catch { }
                _watcher = null;
            }

            _watcher = BuildWatcher();
            GlobalLogger.Log($"Phoenix Controls Layer Watcher is active on: {_layersPath}", "LayerWatcher");
        }

        // P1-19 — factored out so OnWatcherError → TryRecreateWatcher can rebuild
        // the FSW with the same wiring as the initial Start() call. Mirrors
        // LogicWatcher.BuildWatcher exactly: same buffer size, same handler
        // wiring order, EnableRaisingEvents flipped AFTER every += line.
        private FileSystemWatcher BuildWatcher()
        {
            var watcher = new FileSystemWatcher(_layersPath, "*.phxlayer")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                InternalBufferSize = InternalBufferBytes,
                EnableRaisingEvents = false, // arm AFTER wiring handlers
            };

            watcher.Created += (s, e) =>
            {
                ScheduleReload(e.FullPath);
            };
            watcher.Changed += (s, e) =>
            {
                ScheduleReload(e.FullPath);
            };
            watcher.Deleted += (s, e) =>
            {
                string id = Path.GetFileNameWithoutExtension(e.Name ?? "");
                CancelDebounce(e.FullPath);
                if (!string.IsNullOrEmpty(id))
                {
                    _registry.RemoveLayer(id);
                    // QC05-03 / QC58-01 — also discard the per-widget trigger queues
                    // for this layer so the channel pumps / idle watchdogs / CTSes
                    // don't leak for the lifetime of the Hub process.
                    InvokeDiscard(id);
                }
            };
            watcher.Renamed += (s, e) =>
            {
                // The atomic save produces a PAIR of Renamed events. The NEW name is
                // what decides what each one means (QC58-02 + 0.12.x ~RF fix):
                //
                //   "foo.phxlayer.tmp -> foo.phxlayer"   (new IS a .phxlayer) — the save
                //       landing. Register/reload the final, keep the id.
                //   "foo.phxlayer -> foo.phxlayer~RF1a2b.TMP"  (new is NOT a .phxlayer) —
                //       the Win32 ReplaceFile / atomic save backing up the ORIGINAL before
                //       swapping the new file in. This is NOT a rename-away of the layer.
                //       Pre-fix the handler only special-cased the OldName-ends-in-".tmp"
                //       case, so this ~RF backup hit the else branch and RemoveLayer'd +
                //       discarded the LIVE layer on EVERY save (then scheduled a reload of
                //       a transient that no longer exists), churning the registry and the
                //       per-widget trigger queues. Now keyed on the NEW name: a rename
                //       whose target is not a real *.phxlayer is ignored entirely.
                //   "a.phxlayer -> b.phxlayer"           (both .phxlayer) — a genuine
                //       rename: drop the old id, reload the new.
                string? oldName = e.OldName;
                string? newName = e.Name;

                CancelDebounce(e.OldFullPath);

                bool newIsLayer = !string.IsNullOrEmpty(newName)
                    && newName!.EndsWith(".phxlayer", StringComparison.OrdinalIgnoreCase);
                // Renamed to a transient backup (~RF*.TMP / .tmp / .bak / anything not a
                // real layer) — the original is being preserved in place, not removed.
                if (!newIsLayer) return;

                bool oldIsLayer = !string.IsNullOrEmpty(oldName)
                    && oldName!.EndsWith(".phxlayer", StringComparison.OrdinalIgnoreCase);
                if (oldIsLayer)
                {
                    string oldId = Path.GetFileNameWithoutExtension(oldName!);
                    if (!string.IsNullOrEmpty(oldId))
                    {
                        _registry.RemoveLayer(oldId);
                        // QC05-03 / QC58-01 — discard the per-widget trigger queues only
                        // on a genuine layer→layer rename, never on a save landing.
                        InvokeDiscard(oldId);
                    }
                }
                ScheduleReload(e.FullPath);
            };

            // P1-19 — Error subscription mirrors LogicWatcher . Without
            // this, an InternalBufferSize overflow or OneDrive handle invalidation
            // silently ends layer hot-reload until the Hub is restarted. Wired
            // BEFORE EnableRaisingEvents flips so the first overflow event always
            // has a handler.
            watcher.Error += OnWatcherError;

            watcher.EnableRaisingEvents = true;
            return watcher;
        }

        // P1-19 — mirrors LogicWatcher.OnWatcherError. Logs the inner exception
        // (FileSystemWatcher.ErrorEventArgs.GetException unpacks the
        // Win32Exception that arrives on buffer overflow) and kicks off a
        // throttled recreate.
        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            if (Volatile.Read(ref _disposed) != 0) return;

            var inner = e.GetException();
            GlobalLogger.Error(
                "LayerWatcher",
                inner is Win32Exception
                    ? "FileSystemWatcher error (likely buffer overflow or handle invalidation)"
                    : "FileSystemWatcher error",
                inner);

            TryRecreateWatcher();
        }

        // P1-19 — mirrors LogicWatcher.TryRecreateWatcher. Single-flighted via
        // _recreateLock and throttled to one attempt per RecreateThrottle so an
        // unrecoverable path (deleted dir, ACL change, OneDrive virtualisation)
        // can't pin the watcher in a tight rebuild loop.
        private void TryRecreateWatcher()
        {
            if (Volatile.Read(ref _disposed) != 0) return;

            lock (_recreateLock)
            {
                var now = DateTime.UtcNow;
                if (now - _lastRecreateAttemptUtc < RecreateThrottle)
                {
                    GlobalLogger.Log(
                        "LayerWatcher: skipping recreate (throttled, last attempt < 5 s ago)",
                        "LayerWatcher", LogLevel.System);
                    return;
                }
                _lastRecreateAttemptUtc = now;

                try
                {
                    if (_watcher != null)
                    {
                        try { _watcher.EnableRaisingEvents = false; } catch { /* handle dead */ }
                        try { _watcher.Dispose(); } catch { /* handle dead */ }
                        _watcher = null;
                    }

                    // The directory may have vanished (OneDrive un-pin, user
                    // deletion). Re-ensure before rebuilding so the FSW ctor
                    // doesn't throw.
                    Directory.CreateDirectory(_layersPath);

                    _watcher = BuildWatcher();
                    GlobalLogger.Log(
                        $"LayerWatcher: recreated FileSystemWatcher on '{_layersPath}'",
                        "LayerWatcher", LogLevel.System);
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error("LayerWatcher",
                        "Failed to recreate FileSystemWatcher; will retry on next Error",
                        ex);
                }
            }
        }

        public void Dispose() => Stop();

        public void Stop()
        {
            // BH-031 — flip _disposed BEFORE tearing down the debouncer, so a
            // debounce timer can't be created after Stop has started tearing
            // down. PathDebouncer.Dispose flips its own internal flag inside
            // its lock, mirroring the previous in-line critical section and
            // eliminating the race where a late Reload registered a ghost
            // layer post-shutdown.
            Interlocked.Exchange(ref _disposed, 1);
            // [P1 swarm-audit 2026-05-29] Serialize the _watcher teardown under
            // _recreateLock so this read-modify-write can't interleave with a
            // concurrent TryRecreateWatcher (which also disposes + reassigns
            // _watcher under the same lock). Without it, Stop() and an in-flight
            // recreate could both dispose the field or leave a live FSW orphaned.
            lock (_recreateLock)
            {
                try { _watcher?.Dispose(); } catch { }
                _watcher = null;
            }
            try { _debouncer.Dispose(); } catch { }
        }

        private void ScanAll()
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(_layersPath, "*.phxlayer"))
                    InvokeReload(path);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("LayerWatcher", "initial scan failed", ex);
            }
        }

        /// <summary>
        /// Reset the per-file debounce timer. The reload fires once after DebounceMs of quiet,
        /// then waits for the file's byte stream to stop growing before re-deserializing.
        /// </summary>
        internal void ScheduleReload(string path)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (string.IsNullOrEmpty(path)) return;

            string capturedPath = path;
            _debouncer.Schedule(capturedPath, DebounceMs, () =>
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                // QC58-03 — offload the file-stability polling + the reload to a
                // dedicated task so the debouncer's Timer callback (a thread-pool
                // worker) isn't blocked by up to ~500ms of Thread.Sleep + the
                // synchronous deserialize/registry mutation. Without the offload
                // a burst of layer saves can starve the thread pool of timer
                // callbacks while every reload waits its turn on the same
                // worker. The Task.Run body is now fully async (Task.Delay-based
                // stability polling + Task.Delay-backed read retries) so the
                // pool worker yields between polls instead of pinning a thread.
                //
                // [QC18-S1 P1] Route through AsyncErrorBoundary.SafeRunAsync so a
                // fault inside the reload pipeline (anything the inner
                // ReloadAsync re-throws past its own catch — e.g. an unexpected
                // exception type that WAS swallowed by the legacy bare-Task.Run
                // catch below) lands in GlobalLogger.Error like every other
                // fire-and-forget in Hub. The bare _ = Task.Run(...) outlier was
                // the only fire-and-forget in this file that bypassed the
                // boundary; Bus.cs's peer routes already wrap correctly.
                _ = AsyncErrorBoundary.SafeRunAsync(
                    () => Task.Run(async () =>
                    {
                        if (Volatile.Read(ref _disposed) != 0) return;

                        // Yield briefly until the editor stops flushing bytes — catches the
                        // .tmp → rename → modify pattern where the watcher fires before the
                        // last write has hit disk. Async variant frees the worker thread
                        // across each 50ms poll instead of sleeping it.
                        await DebouncedFileWatcher.WaitForSizeStableAsync(
                            capturedPath, CancellationToken.None,
                            maxAttempts: 10, delayMs: 50).ConfigureAwait(false);

                        // BH-031 — re-check after the WaitForFileStable awaits. Stop() may
                        // have flipped _disposed while we were polling the file size, and
                        // mutating the registry post-shutdown leaves ghost layers behind.
                        if (Volatile.Read(ref _disposed) != 0) return;
                        await InvokeReloadAsync(capturedPath).ConfigureAwait(false);
                    }),
                    "LayerWatcher",
                    $"debounced reload for '{Path.GetFileName(capturedPath)}'");
            });
        }

        private void CancelDebounce(string? path) => _debouncer.Cancel(path);

        /// <summary>
        /// Polls file size until two consecutive reads agree (file has stopped growing) or
        /// maxAttempts expires. Returns silently in any failure mode — the caller's reload
        /// will surface the real error if the file is still unreadable.
        ///
        /// Thin delegate over <see cref="DebouncedFileWatcher.WaitForSizeStable"/>; kept on
        /// LayerWatcher because BugFixSweep5_LayerWatcher_Tests calls it as a static helper.
        /// </summary>
        internal static void WaitForFileStable(string path, int maxAttempts = 10, int delayMs = 50)
            => DebouncedFileWatcher.WaitForSizeStable(path, maxAttempts, delayMs);

        private void InvokeReload(string path)
        {
            var del = ReloadDelegate;
            if (del != null)
            {
                del(path);
                return;
            }
            Reload(path);
        }

        /// <summary>
        /// QC58-03 — async cousin of <see cref="InvokeReload"/> used by the
        /// debounced reload pipeline so the read-retry backoff can yield the
        /// thread-pool worker instead of pinning it on <see cref="Thread.Sleep"/>.
        /// Falls back to the sync test seam delegate when one is injected so
        /// existing BugFixSweep5_LayerWatcher_Tests cases keep their hook.
        /// </summary>
        private Task InvokeReloadAsync(string path)
        {
            var del = ReloadDelegate;
            if (del != null)
            {
                del(path);
                return Task.CompletedTask;
            }
            return ReloadAsync(path);
        }

        /// <summary>
        /// QC05-03 / QC58-01 — forward layer-removal events to LayerRuntime so its
        /// per-widget queue pumps shut down. Tests can inject via
        /// <see cref="DiscardDelegate"/>; production routes to the supplied
        /// runtime instance, falling back to the singleton if none was injected.
        /// Failures are best-effort — a registry mutation must never throw out
        /// of a FileSystemWatcher callback.
        /// </summary>
        private void InvokeDiscard(string layerId)
        {
            if (string.IsNullOrEmpty(layerId)) return;
            var del = DiscardDelegate;
            if (del != null)
            {
                try { del(layerId); }
                catch (Exception ex) { GlobalLogger.Error("LayerWatcher", $"discard delegate threw for '{layerId}'", ex); }
                return;
            }
            try
            {
                var runtime = _runtime ?? LayerRuntime.Instance;
                runtime.DiscardLayer(layerId);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("LayerWatcher", $"DiscardLayer failed for '{layerId}'", ex);
            }
        }

        private void Reload(string path)
        {
            try
            {
                // BH-031 — final disposed check immediately before the registry mutation.
                // Covers the case where the timer body's earlier checks passed but Stop
                // ran between the InvokeReload entry and now.
                if (Volatile.Read(ref _disposed) != 0) return;
                if (!File.Exists(path)) return;

                // QC58-04 — LayerSerializer.Read used to fail on the first
                // IOException (transient file-share contention while the
                // editor is still releasing the file handle), leaving the
                // registry pinned at its prior state. Retry up to 3 times
                // with 50ms backoff before surfacing the error.
                Layer? layer = TryReadWithRetry(path, attempts: 3, backoffMs: 50, out var lastEx);
                if (layer is null)
                {
                    // QC58-04 — persistent read failure is "transient I/O could
                    // not be resolved", not a structural error. The Layer stays
                    // pinned at its prior in-memory state; surface as a Warning-
                    // style log via the System tier instead of Error so the
                    // SystemLog doesn't flash red for the operator on a flake.
                    GlobalLogger.Log(
                        $"LayerWatcher: read of '{Path.GetFileName(path)}' failed after retries ({lastEx?.GetType().Name ?? "unknown"}: {lastEx?.Message ?? "n/a"}) — keeping prior state.",
                        "LayerWatcher", LogLevel.System);
                    return;
                }

                string id = Path.GetFileNameWithoutExtension(path);
                _registry.RegisterLayer(id, layer);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("LayerWatcher", $"failed to load '{Path.GetFileName(path)}'", ex);
            }
        }

        /// <summary>
        /// QC58-03 — async sibling of <see cref="Reload"/>. Used by the debounced
        /// reload pipeline so the IOException retry backoff yields the thread-pool
        /// worker via <see cref="Task.Delay"/> instead of holding it on
        /// <see cref="Thread.Sleep"/>. ScanAll keeps the sync path because the
        /// startup scan runs sequentially on the bootstrap thread anyway.
        /// </summary>
        private async Task ReloadAsync(string path)
        {
            try
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                if (!File.Exists(path)) return;

                var (layer, lastEx) = await TryReadWithRetryAsync(path, attempts: 3, backoffMs: 50).ConfigureAwait(false);
                if (layer is null)
                {
                    GlobalLogger.Log(
                        $"LayerWatcher: read of '{Path.GetFileName(path)}' failed after retries ({lastEx?.GetType().Name ?? "unknown"}: {lastEx?.Message ?? "n/a"}) — keeping prior state.",
                        "LayerWatcher", LogLevel.System);
                    return;
                }

                if (Volatile.Read(ref _disposed) != 0) return;
                string id = Path.GetFileNameWithoutExtension(path);
                _registry.RegisterLayer(id, layer);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("LayerWatcher", $"failed to load '{Path.GetFileName(path)}'", ex);
            }
        }

        /// <summary>
        /// QC58-04 — bounded retry for <see cref="LayerSerializer.Read"/>. Returns
        /// the parsed Layer on success or null after <paramref name="attempts"/>
        /// failures, writing the last exception to <paramref name="lastEx"/>.
        /// Retries only on IO / unauthorized-access errors; deserialization
        /// failures (malformed JSON) fail fast — retrying won't fix a bad file.
        /// </summary>
        private static Layer? TryReadWithRetry(string path, int attempts, int backoffMs, out Exception? lastEx)
        {
            lastEx = null;
            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    return LayerSerializer.Read(path);
                }
                catch (IOException ex)
                {
                    lastEx = ex;
                }
                catch (UnauthorizedAccessException ex)
                {
                    lastEx = ex;
                }
                catch
                {
                    // Anything else (malformed JSON, unexpected types) — fail
                    // fast so the catch in Reload surfaces it. Retrying won't
                    // change a deserialization error.
                    throw;
                }
                if (i + 1 < attempts) Thread.Sleep(backoffMs);
            }
            return null;
        }

        /// <summary>
        /// QC58-03 — async variant of <see cref="TryReadWithRetry"/>; backs off
        /// with <see cref="Task.Delay"/> instead of <see cref="Thread.Sleep"/>
        /// so the calling thread-pool worker is released between attempts.
        /// </summary>
        private static async Task<(Layer? Layer, Exception? LastEx)> TryReadWithRetryAsync(
            string path, int attempts, int backoffMs)
        {
            Exception? lastEx = null;
            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    return (LayerSerializer.Read(path), null);
                }
                catch (IOException ex)
                {
                    lastEx = ex;
                }
                catch (UnauthorizedAccessException ex)
                {
                    lastEx = ex;
                }
                catch
                {
                    // Malformed JSON / unexpected types — fail fast so the catch
                    // in ReloadAsync surfaces it. Retrying won't help.
                    throw;
                }
                if (i + 1 < attempts) await Task.Delay(backoffMs).ConfigureAwait(false);
            }
            return (null, lastEx);
        }
    }
}
