using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
// 'Process' binds to a Phoenix type in this namespace — alias the BCL one
// (same convention as PillarBootstrap).
using SysProcess = System.Diagnostics.Process;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// Self-relaunch recovery for a permanently frozen UI thread. Every recorded
    /// freeze parks the single UI/message-pump thread in a native wait that can't
    /// be un-wedged or restarted in place — so the only real recovery is
    /// relaunching the Hub process. When the <c>UiHangWatchdog</c> confirms a
    /// permanent freeze (still unresponsive ~25s after the trip), it calls
    /// <see cref="Relaunch"/> from its background thread (which stays alive while
    /// the UI is wedged): spawn a fresh Hub, then hard-kill this one.
    ///
    /// Two hazards this class is built around:
    ///   1. Single-instance mutex. The fresh instance would see this (still-alive)
    ///      process's <c>Phoenix.Controls.Hub.SingleInstance</c> mutex, foreground
    ///      the dying window, and exit — leaving NOTHING running. So the relaunch
    ///      passes <c>--recovered-relaunch=&lt;oldPid&gt;</c>; the new instance
    ///      waits for that pid to exit (mutex released) BEFORE its single-instance
    ///      guard runs. See <see cref="DetectRecoveredRelaunch"/> /
    ///      <see cref="WaitForOldInstanceExit"/>, wired at the top of App.OnLaunched.
    ///   2. Restart-loop / fork-bomb. A deterministic re-freeze would relaunch
    ///      forever. A cross-process state file (<see cref="StateFileName"/> in
    ///      %AppData%, outside the install tree) records recent relaunch stamps;
    ///      past <see cref="MaxRelaunchesPerWindow"/> in <see cref="RelaunchWindow"/>
    ///      the relaunch is suppressed and the frozen process is left up (with a
    ///      loud log) for manual intervention.
    /// </summary>
    public static class HangRecoveryLauncher
    {
        public const string RelaunchPidArgPrefix = "--recovered-relaunch=";
        public const string DumpArgPrefix        = "--hang-dump=";

        // Cross-process loop guard. Deliberately small: three auto-recoveries in
        // a quarter hour is already "something is deterministically wrong" —
        // looping past that won't help, so stop and surface it.
        private const string StateFileName = "hang-recovery-state.json";
        public static readonly TimeSpan RelaunchWindow = TimeSpan.FromMinutes(15);
        public const int MaxRelaunchesPerWindow = 3;

        // Let the on-disk log writers drain the relaunch marker and give the
        // fresh instance a beat to reach WaitForOldInstanceExit before we vanish.
        private const int RelaunchDrainDelayMs = 500;

        /// <summary>Parsed <c>--recovered-relaunch</c> handoff from the spawning (frozen) instance.</summary>
        public readonly record struct RecoveredRelaunchInfo(int OldPid, string? DumpPath);

        /// <summary>
        /// Perform the self-relaunch: loop-guard, spawn a fresh Hub, hard-kill
        /// this one. Returns <c>false</c> (leaving the caller alive) when the
        /// loop cap suppressed it or the spawn failed — on success this process
        /// is terminated and the call never returns. Never throws.
        /// </summary>
        public static bool Relaunch(string? dumpPath)
        {
            try
            {
                string stateFile = Paths.RoamingAppData(StateFileName);
                var nowUtc = DateTime.UtcNow;
                var recent = LoadRecentRelaunches(stateFile, nowUtc);

                if (ShouldSuppressRelaunch(recent, out int count))
                {
                    GlobalLogger.Error("HangRecovery",
                        $"Auto-recovery SUPPRESSED: {count} relaunches within the last " +
                        $"{RelaunchWindow.TotalMinutes:0} min reached the cap ({MaxRelaunchesPerWindow}). " +
                        "The freeze looks deterministic — leaving the frozen process up for manual restart " +
                        $"so it can't fork-bomb. Dump: {dumpPath ?? "(none)"}");
                    return false;
                }

                string? exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe))
                {
                    GlobalLogger.Error("HangRecovery", "Cannot relaunch: Environment.ProcessPath is null.");
                    return false;
                }

                int selfPid = Environment.ProcessId;
                var psi = new ProcessStartInfo
                {
                    FileName         = exe,
                    UseShellExecute  = false,
                    WorkingDirectory = AppContext.BaseDirectory,
                };
                // '='-joined single tokens so App's --open positional parser
                // skips them cleanly (it ignores every '--'-prefixed arg).
                psi.ArgumentList.Add(RelaunchPidArgPrefix + selfPid.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(dumpPath))
                    psi.ArgumentList.Add(DumpArgPrefix + dumpPath);

                // Record BEFORE spawning so a spawn that succeeds then faults
                // still counts against the loop cap.
                var updated = new List<DateTime>(recent) { nowUtc };
                SaveRelaunches(stateFile, updated);

                GlobalLogger.Error("HangRecovery",
                    $"UI thread permanently frozen — auto-relaunching Hub (recovery {count + 1}/{MaxRelaunchesPerWindow} " +
                    $"this {RelaunchWindow.TotalMinutes:0}-min window). Spawning a fresh instance, then terminating pid " +
                    $"{selfPid}. Dump: {dumpPath ?? "(none)"}");

                SysProcess? child = SysProcess.Start(psi);
                if (child is null)
                {
                    GlobalLogger.Error("HangRecovery",
                        "Process.Start returned null — NOT terminating self; the app stays up (frozen).");
                    return false;
                }

                // Before hard-killing ourselves, terminate our own WebView2
                // browser children (msedgewebview2.exe). A bare self-Kill can't
                // take entireProcessTree (the CLR forbids killing the current
                // process's own tree), so those children would be orphaned — and
                // their WebView2 user-data folder sits inside the install tree,
                // which then blocks the next auto-update's swap until a reboot.
                // Best-effort; never blocks the relaunch.
                try { KillOwnWebViewChildren(selfPid); }
                catch (Exception ex) { GlobalLogger.Error("HangRecovery", "KillOwnWebViewChildren", ex); }

                // Hard-kill (not Environment.Exit): the UI thread is wedged, so a
                // graceful shutdown would deadlock on it. Kill releases the named
                // single-instance mutex; the fresh instance's WaitForOldInstanceExit
                // blocks on this pid until then. Does not return.
                Thread.Sleep(RelaunchDrainDelayMs);
                SysProcess.GetCurrentProcess().Kill();
                return true; // unreachable
            }
            catch (Exception ex)
            {
                try { GlobalLogger.Error("HangRecovery", "Relaunch failed; the app stays up (frozen).", ex); }
                catch { /* logger itself faulted — nothing to do on a dying process */ }
                return false;
            }
        }

        /// <summary>
        /// Pull the <c>--recovered-relaunch</c> handoff (and optional
        /// <c>--hang-dump</c> path) out of argv. Returns null when this launch
        /// was NOT a hang-recovery relaunch. Pure — safe to call before any
        /// service is up.
        /// </summary>
        public static RecoveredRelaunchInfo? DetectRecoveredRelaunch(string[] argv)
        {
            if (argv is null) return null;
            int? oldPid = null;
            string? dump = null;
            foreach (var a in argv)
            {
                if (string.IsNullOrEmpty(a)) continue;
                if (a.StartsWith(RelaunchPidArgPrefix, StringComparison.Ordinal))
                {
                    string v = a[RelaunchPidArgPrefix.Length..];
                    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid) && pid > 0)
                        oldPid = pid;
                }
                else if (a.StartsWith(DumpArgPrefix, StringComparison.Ordinal))
                {
                    string v = a[DumpArgPrefix.Length..];
                    if (!string.IsNullOrEmpty(v)) dump = v;
                }
            }
            return oldPid is { } p ? new RecoveredRelaunchInfo(p, dump) : null;
        }

        /// <summary>
        /// Block until the prior (frozen) instance with <paramref name="pid"/> has
        /// exited — so its single-instance mutex is released before this instance's
        /// guard runs — or the timeout elapses. A missing pid means it already
        /// exited (the happy path). Never throws.
        /// </summary>
        public static void WaitForOldInstanceExit(int pid, TimeSpan timeout)
        {
            if (pid <= 0) return;
            try
            {
                using var p = SysProcess.GetProcessById(pid);
                int ms = (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
                p.WaitForExit(ms);
            }
            catch (ArgumentException)
            {
                // No such process — already gone. Exactly what we're waiting for.
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("HangRecovery", $"WaitForOldInstanceExit({pid}) failed", ex);
            }
        }

        /// <summary>
        /// True when the recent (already window-pruned) relaunch count has reached
        /// the per-window cap and a further auto-relaunch must be suppressed.
        /// </summary>
        public static bool ShouldSuppressRelaunch(IReadOnlyList<DateTime> recentInWindow, out int count)
        {
            count = recentInWindow?.Count ?? 0;
            return count >= MaxRelaunchesPerWindow;
        }

        /// <summary>
        /// Load the relaunch stamps from <paramref name="stateFilePath"/>, keeping
        /// only those inside <see cref="RelaunchWindow"/> of <paramref name="nowUtc"/>.
        /// A missing / corrupt / locked file yields an empty list (treated as
        /// "no recent relaunches"). Never throws.
        /// </summary>
        public static List<DateTime> LoadRecentRelaunches(string stateFilePath, DateTime nowUtc)
        {
            var result = new List<DateTime>();
            try
            {
                if (!File.Exists(stateFilePath)) return result;
                var ticks = JsonSerializer.Deserialize<List<long>>(File.ReadAllText(stateFilePath))
                            ?? new List<long>();
                var cutoff = nowUtc - RelaunchWindow;
                // A small forward tolerance guards against a clock skew making a
                // just-written stamp look "in the future" and get dropped.
                var future = nowUtc + TimeSpan.FromMinutes(1);
                foreach (var t in ticks)
                {
                    DateTime dt;
                    try { dt = new DateTime(t, DateTimeKind.Utc); }
                    catch { continue; } // out-of-range tick — ignore
                    if (dt >= cutoff && dt <= future) result.Add(dt);
                }
            }
            catch { /* corrupt / locked → no history */ }
            return result;
        }

        /// <summary>
        /// Persist the relaunch stamps (as UTC ticks) to
        /// <paramref name="stateFilePath"/>. Best-effort; never throws.
        /// </summary>
        public static void SaveRelaunches(string stateFilePath, IReadOnlyList<DateTime> stamps)
        {
            try
            {
                var dir = Path.GetDirectoryName(stateFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var ticks = stamps.Select(d => d.ToUniversalTime().Ticks).ToList();
                File.WriteAllText(stateFilePath, JsonSerializer.Serialize(ticks));
            }
            catch { /* best-effort */ }
        }

        // ── WebView2 child cleanup (freeze-recovery + coordinated-shutdown paths) ──

        private const string WebViewImageFile = "msedgewebview2.exe";

        /// <summary>
        /// Terminate the WebView2 browser children (<c>msedgewebview2.exe</c>)
        /// whose parent is <paramref name="selfPid"/>, so a self-<see cref="SysProcess.Kill()"/>
        /// on the wedged UI process doesn't strand them holding the install tree.
        /// Uses a Toolhelp process snapshot to find direct children by parent PID
        /// (the BCL exposes no parent-PID accessor). Best-effort; every step is
        /// guarded so this can never break the relaunch it precedes.
        /// Public because the coordinated-shutdown exit (Hub.WinUI's
        /// HubProcessExit) ends the process with the same TerminateProcess
        /// pattern and needs the identical child cleanup first.
        /// </summary>
        public static void KillOwnWebViewChildren(int selfPid)
        {
            var childPids = new List<int>();
            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == INVALID_HANDLE_VALUE) return;
            try
            {
                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                if (Process32First(snapshot, ref entry))
                {
                    do
                    {
                        if (entry.th32ParentProcessID == (uint)selfPid
                            && string.Equals(entry.szExeFile, WebViewImageFile, StringComparison.OrdinalIgnoreCase))
                        {
                            childPids.Add((int)entry.th32ProcessID);
                        }
                    }
                    while (Process32Next(snapshot, ref entry));
                }
            }
            finally { CloseHandle(snapshot); }

            foreach (int pid in childPids)
            {
                try
                {
                    using var child = SysProcess.GetProcessById(pid);
                    child.Kill(entireProcessTree: true);
                    // Neutral wording — this runs on BOTH the freeze-recovery
                    // relaunch and the normal coordinated-shutdown exit.
                    GlobalLogger.Log($"Terminated orphan-prone WebView2 child pid {pid} before process exit.",
                        "HangRecovery", LogLevel.System);
                }
                catch (ArgumentException) { /* already gone */ }
                catch (Exception ex) { GlobalLogger.Error("HangRecovery", $"kill WebView2 child pid {pid}", ex); }
            }
        }

        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
