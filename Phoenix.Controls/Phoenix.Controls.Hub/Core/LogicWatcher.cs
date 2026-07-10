using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.Core
{
    public class LogicWatcher : IDisposable
    {
        private FileSystemWatcher? _watcher;
        private string _logicPath = string.Empty;

        // Debounce: editor saves typically produce Created + Changed (and
        // sometimes Renamed) events for one logical save. We coalesce them via
        // the shared PathDebouncer on two kinds of key: a single global key
        // for the registry refresh (LogicWatcher refreshes the whole
        // ScriptRegistry rather than per-file, so one slot suffices) and a
        // per-path "backup:<file>" key so the .bak-ladder rotation runs once
        // per logical save instead of once per raw Changed event.
        private const int DebounceMs = 250;
        private const string DebounceKey = "logic-refresh";
        private readonly PathDebouncer _debouncer = new();
        private int _disposed;

        // FSW hardening for OneDrive working trees.
        //
        // Default InternalBufferSize is 8 KB — small enough that a bulk-save
        // burst (e.g. Architect re-exporting every .phx after a graph rename)
        // can overflow the kernel-side ring, after which Windows raises
        // FileSystemWatcher.Error with ERROR_NOTIFY_ENUM_DIR and the watcher
        // silently stops delivering events until the user touches a file
        // manually. Cranking to 64 KB gives ~8x more headroom; the buffer is
        // backed by non-paged pool so we don't go further than the docs
        // recommend.
        //
        // OneDrive specifically (Majo's working tree is on it) generates
        // additional transient ERROR_INVALID_HANDLE notifications when the
        // sync engine virtualises a file — those land on the Error event and
        // would silently end script reload too if we don't subscribe.
        private const int InternalBufferBytes = 64 * 1024;

        // Throttle recreation so an unrecoverable path (e.g. directory
        // permanently deleted) can't spin us in a tight FSW-construct loop.
        // 5 s is fast enough to recover from a transient OneDrive hiccup and
        // slow enough to log a single recovery line per persistent failure.
        private static readonly TimeSpan RecreateThrottle = TimeSpan.FromSeconds(5);
        private DateTime _lastRecreateAttemptUtc = DateTime.MinValue;
        private readonly object _recreateLock = new();

        /// <summary>Fires after each successful ScriptRegistry refresh (post-debounce).
        /// SchedulerService subscribes so an edited <c>on_schedule</c> / <c>on_interval</c>
        /// header in a saved <c>.phx</c> takes effect without a Hub restart.</summary>
        public event Action? OnRefresh;

        public LogicWatcher()
        {
            // ConfigManager.Current.LogicDirectory may
            // be null (missing/blank config key); default to "data/logic" so the
            // Path.IsPathRooted / Path.Combine below never deref a null.
            string rel = ConfigManager.Current.LogicDirectory ?? "data/logic";
            if (Path.IsPathRooted(rel))
            {
                _logicPath = rel;
            }
            else
            {
                // Solution-anchored: walks up to the Phoenix.Controls folder, joins
                // Hub/<rel>. Falls back to AppBase-relative when not running from
                // a dev tree (production deploy).
                string projectSrc = ResolveProjectSourcePath(AppDomain.CurrentDomain.BaseDirectory);
                _logicPath = Path.Combine(projectSrc, rel);
            }
            Directory.CreateDirectory(_logicPath);
        }

        /// <summary>
        /// Returns the Hub project source folder, anchored to the Phoenix.Controls
        /// solution folder. Test seam: callers can pass an alternate
        /// <paramref name="startDir"/>; production passes BaseDirectory. Falls
        /// back to <paramref name="startDir"/> when the solution folder isn't
        /// reachable so callers always receive a usable path.
        /// </summary>
        internal static string ResolveProjectSourcePath(string startDir)
        {
            if (string.IsNullOrWhiteSpace(startDir))
                startDir = AppDomain.CurrentDomain.BaseDirectory;

            string? sln = Paths.FindSolutionRoot(startDir);
            if (sln != null)
                return Path.Combine(sln, Paths.HubProjectFolderName);

            GlobalLogger.Log(
                $"LogicWatcher: '{Paths.SolutionFolderName}' folder not found above '{startDir}'; falling back.",
                "LogicWatcher",
                LogLevel.Communication);
            return startDir;
        }

        public void Start()
        {
            // Start() must be idempotent. Calling it a second time
            // (e.g. on settings-driven hot reconfigure of LogicDirectory) used
            // to leak the previous FileSystemWatcher: a fresh _watcher was
            // assigned over the field, the old one kept raising events on the
            // dead Refresh path, and Dispose() only tore down whichever one
            // was current at the time. Tear the prior watcher down here so
            // repeated Start() calls converge on a single live watcher.
            // Serialize the _watcher mutations under
            // _recreateLock (the same lock TryRecreateWatcher already holds) so a
            // re-Start can't interleave with an in-flight Error-driven recreate.
            lock (_recreateLock)
            {
                if (_watcher != null)
                {
                    try { _watcher.EnableRaisingEvents = false; } catch { }
                    try { _watcher.Dispose(); } catch { }
                    _watcher = null;
                }

                _watcher = BuildWatcher();
            }
            GlobalLogger.Log($"Phoenix Logic Watcher is active on: {_logicPath}", "LogicWatcher");
        }

        // Factored out so the Error-recovery path can rebuild the
        // FSW with the same wiring as the initial Start() call.
        private FileSystemWatcher BuildWatcher()
        {
            var watcher = new FileSystemWatcher(_logicPath, "*.phx")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                InternalBufferSize = InternalBufferBytes,
                // Watch subfolders too so saves to live-process templates under
                // processes/<graph>/ trigger a refresh. ScriptRegistry.Refresh stays
                // top-level only (it enumerates *.phx non-recursively), so a template
                // change just drives ProcessTemplateRegistry.Refresh via OnRefresh.
                IncludeSubdirectories = true,
                EnableRaisingEvents = false, // arm AFTER wiring handlers
            };

            watcher.Created += (s, e) =>
            {
                ScheduleRefresh();
            };
            watcher.Changed += (s, e) =>
            {
                // FSW emits several Changed events per logical save. A
                // per-path debounce slot collapses that burst so one save
                // rotates the .bak ladder exactly once; a genuinely later
                // save re-arms the timer and rotates again. The debounced
                // callback offloads via Task.Run — WaitForFileStable +
                // RotateBackup do up to ~3s of Thread.Sleep + sync File.Copy,
                // which must not pin the debounce timer's callback thread —
                // and routes through AsyncErrorBoundary so a fault inside
                // BackupAndScheduleRefresh lands in GlobalLogger.Error like
                // every other fire-and-forget in Hub, instead of escaping as
                // an unobserved Task exception.
                string capturedPath = e.FullPath;
                _debouncer.Schedule("backup:" + capturedPath, DebounceMs, () =>
                {
                    _ = AsyncErrorBoundary.SafeRunAsync(
                        () => Task.Run(() => BackupAndScheduleRefresh(capturedPath)),
                        "LogicWatcher",
                        $"backup+refresh for '{Path.GetFileName(capturedPath)}'");
                });
            };
            watcher.Deleted += (s, e) =>
            {
                ScheduleRefresh();
            };
            watcher.Renamed += (s, e) =>
            {
                ScheduleRefresh();
            };

            // OneDrive transient hiccups (handle invalidation,
            // buffer overflow during bulk save) silently end script reload
            // unless Error is wired. Log via GlobalLogger.Error so the stack
            // trace survives, then attempt one throttled recreate.
            watcher.Error += OnWatcherError;

            watcher.EnableRaisingEvents = true;
            return watcher;
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            if (Volatile.Read(ref _disposed) != 0) return;

            var inner = e.GetException();
            // Many native FSW errors arrive as Win32Exception under the hood
            // (ERROR_NOTIFY_ENUM_DIR = 1022 on buffer overflow). We let
            // GlobalLogger format whatever the runtime hands us; the
            // AggregateException/InnerException walk in Error()
            // means a wrapped exception still surfaces with full context.
            GlobalLogger.Error(
                "LogicWatcher",
                inner is Win32Exception
                    ? "FileSystemWatcher error (likely buffer overflow or handle invalidation)"
                    : "FileSystemWatcher error",
                inner);

            TryRecreateWatcher();
        }

        private void TryRecreateWatcher()
        {
            if (Volatile.Read(ref _disposed) != 0) return;

            lock (_recreateLock)
            {
                // Throttle: a permanently unrecoverable path (deleted dir,
                // ACL change) would otherwise pin us in a tight rebuild loop.
                var now = DateTime.UtcNow;
                if (now - _lastRecreateAttemptUtc < RecreateThrottle)
                {
                    GlobalLogger.Log(
                        "LogicWatcher: skipping recreate (throttled, last attempt < 5 s ago)",
                        "LogicWatcher", LogLevel.System);
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

                    // The directory may have vanished (OneDrive un-pin,
                    // user deletion). Re-ensure before rebuilding so the
                    // FSW ctor doesn't throw.
                    Directory.CreateDirectory(_logicPath);

                    _watcher = BuildWatcher();
                    GlobalLogger.Log(
                        $"LogicWatcher: recreated FileSystemWatcher on '{_logicPath}'",
                        "LogicWatcher", LogLevel.System);
                }
                catch (Exception ex)
                {
                    // Recreate itself failed (path gone, ACL denial, OneDrive
                    // still virtualizing the folder). Log and back off — the
                    // next Error event (or restart) will try again.
                    GlobalLogger.Error("LogicWatcher",
                        "Failed to recreate FileSystemWatcher; will retry on next Error",
                        ex);
                }
            }
        }

        /// <summary>Reset the debounce timer; Refresh fires after DebounceMs of quiet.</summary>
        private void ScheduleRefresh()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _debouncer.Schedule(DebounceKey, DebounceMs, () =>
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                try { ScriptRegistry.Instance.Refresh(_logicPath); }
                catch (Exception ex)
                {
                    GlobalLogger.Error("LogicWatcher", "Refresh failed", ex);
                    return;
                }
                // OnRefresh runs on the PathDebouncer's timer
                // callback thread; SchedulerService.Reload() does synchronous
                // Directory.GetFiles() + File.ReadAllText() that would block the
                // timer thread (stalling all further debounce callbacks). Offload
                // to the task pool so the timer thread returns immediately.
                _ = Task.Run(() =>
                {
                    try { OnRefresh?.Invoke(); }
                    catch (Exception ex)
                    {
                        GlobalLogger.Error("LogicWatcher", "OnRefresh subscriber threw", ex);
                    }
                });
            });
        }

        private void BackupAndScheduleRefresh(string changedPath)
        {
            // Disposed-check at task entry. A Stop() that arrives
            // between the FSW callback and the offloaded task firing must abort
            // the sleeps + sync IO before they accumulate on a dead watcher.
            if (Volatile.Read(ref _disposed) != 0) return;

            // Wait for the writer to finish before snapshotting; then schedule
            // the (debounced) Refresh.
            bool stable = WaitForFileStable(changedPath);

            // Re-check after the polling sleeps. Stop() may have flipped
            // _disposed while we waited; we still want to skip the backup IO
            // and the (now-stale) refresh kick.
            if (Volatile.Read(ref _disposed) != 0) return;

            // Only rotate the backup ladder once the file is confirmed stable —
            // otherwise we'd snapshot a half-written script into .bak1 (and push
            // the last good copy down the ladder). WaitForFileStable already
            // logged the timeout. We still schedule the refresh so the eventual
            // save is picked up by the registry once the writer releases it.
            if (stable)
                RotateBackup(changedPath);

            ScheduleRefresh();
        }

        /// <summary>
        /// Block briefly until the file can be opened with FileShare.None — i.e.
        /// no other process holds an exclusive write lock. Bounded retry so a
        /// permanent locker doesn't hang the watcher thread.
        /// Returns <c>true</c> once an exclusive read succeeds (file stable) or the
        /// file is gone; <c>false</c> if every attempt was exhausted while the file
        /// stayed locked — callers should skip backing up a still-incomplete file.
        /// </summary>
        private static bool WaitForFileStable(string path, int maxAttempts = 30, int delayMs = 100)
        {
            for (int i = 0; i < maxAttempts; i++)
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                    return true; // got an exclusive read — file is stable
                }
                catch (IOException)
                {
                    Thread.Sleep(delayMs);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(delayMs);
                }
                catch
                {
                    return true; // any other exception (e.g. file deleted) — give up
                }
            }

            // Exhausted every retry while the writer still held the lock. Surface
            // it so an operator can see why a backup was skipped, then signal the
            // caller to leave the (incomplete) file alone.
            GlobalLogger.Log(
                $"WaitForFileStable: '{Path.GetFileName(path)}' still locked after {maxAttempts * delayMs}ms — skipping backup of incomplete file",
                "LogicWatcher",
                LogLevel.Communication);
            return false;
        }

        /// <summary>
        /// Keeps the last 3 versions of a changed script as .bak1 / .bak2 / .bak3.
        /// .bak1 is the most recent backup; .bak3 is the oldest.
        /// Snapshots all source bytes up-front so a concurrent deleter can't leave
        /// the ladder half-rotated (e.g., bak3 populated but bak2 empty).
        /// </summary>
        private static void RotateBackup(string path)
        {
            try
            {
                // The sole caller (BackupAndScheduleRefresh) only reaches here
                // after WaitForFileStable confirmed the writer released the
                // file, so the ladder never snapshots a half-written script.
                // No re-confirm needed — a writer that grabs the file between
                // the gate and this copy is a fresh save, which raises its own
                // Changed burst and rotates again once IT stabilises; the
                // SafeReadAllBytes snapshots below tolerate any transient
                // lock by capturing whatever is readable.
                string bak1 = path + ".bak1";
                string bak2 = path + ".bak2";
                string bak3 = path + ".bak3";

                byte[]? currentBytes = SafeReadAllBytes(path);
                byte[]? bak1Bytes    = SafeReadAllBytes(bak1);
                byte[]? bak2Bytes    = SafeReadAllBytes(bak2);

                // Write the new state from snapshots. If the snapshot for a
                // given source is null (file vanished mid-rotation), fall back
                // to the next-newest source so the ladder never has a hole.
                if (bak2Bytes != null)
                    SafeWriteAllBytes(bak3, bak2Bytes);

                byte[]? newBak2 = bak1Bytes ?? currentBytes;
                if (newBak2 != null)
                    SafeWriteAllBytes(bak2, newBak2);

                if (currentBytes != null)
                    SafeWriteAllBytes(bak1, currentBytes);
            }
            catch (Exception ex)
            {
                // Route through GlobalLogger.Error so
                // the InnerException chain + stack are captured in the
                // SystemHistory ring buffer. Label names the failing file so
                // the row stays scannable.
                GlobalLogger.Error("LogicWatcher", $"backup rotation failed for '{Path.GetFileName(path)}'", ex);
            }
        }

        private static byte[]? SafeReadAllBytes(string path)
        {
            try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
            catch { return null; }
        }

        private static void SafeWriteAllBytes(string path, byte[] bytes)
        {
            try { File.WriteAllBytes(path, bytes); }
            catch (Exception ex)
            {
                // Carry the full exception (stack +
                // InnerException) so I/O-permission errors stay diagnosable;
                // label names the .bak file the write was targeting.
                GlobalLogger.Error("LogicWatcher", $"bak write '{Path.GetFileName(path)}' failed", ex);
            }
        }

        // Stop and Dispose are aliases — Stop() exists so HubBootstrapper.ShutdownAsync
        // can use the same call shape as LayerWatcher/HUDServer/Bus.
        public void Stop() => Dispose();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            // Guard the _watcher teardown under
            // _recreateLock and re-check null inside the lock — closes the
            // check-then-act race with a concurrent TryRecreateWatcher that
            // disposes + reassigns _watcher under the same lock.
            try
            {
                lock (_recreateLock)
                {
                    if (_watcher != null)
                    {
                        _watcher.EnableRaisingEvents = false;
                        _watcher.Dispose();
                        _watcher = null;
                    }
                }
            }
            catch { /* best-effort */ }

            try { _debouncer.Dispose(); } catch { }
        }
    }
}
