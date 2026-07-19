using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Phoenix.Controls.Updater;

/// <summary>
/// Pre-swap unlock backstop. The atomic swap in
/// <see cref="UpdateRunner.ApplyArchiveSwap"/> renames the whole install root
/// aside (<c>Directory.Move(installRoot, installRoot.bak)</c>); Windows refuses
/// that rename while ANY process holds a handle to a file underneath it. The
/// updater already waits for / force-kills the six Phoenix image names
/// (<c>UpdateRunner.SuiteImageNames</c>) and the sentinel Hub PID — but that
/// list is blind to child processes a prior Hub session left orphaned:
///
///   • <c>msedgewebview2.exe</c> — the browser subprocesses WebView2 spawns for
///     the in-app doc/changelog viewer and the Visualist preview panels. Their
///     default user-data folder lands INSIDE the install tree
///     (<c>{installRoot}\Hub\Phoenix.Controls.Hub.WinUI.exe.WebView2\</c>) and
///     they keep it open. When the parent Hub dies abnormally — a
///     freeze-recovery hard-kill, or an abrupt <c>Environment.Exit</c> — those
///     children are re-parented and survive, holding the tree until the next
///     reboot. That is the exact "update fails until I restart the PC" symptom.
///
/// This helper uses the Windows <b>Restart Manager</b> (rstrtmgr.dll) to ask
/// the OS which processes are actually locking files under
/// <paramref name="installRoot"/>, logs every one (precise diagnostics for a
/// bug report), and terminates only the ones that are unambiguously ours —
/// leaving anything else (Explorer with the folder open, an AV scanner, the
/// user's editor) alone with a log line so the culprit is visible.
///
/// BCL + Win32 only, like the rest of the Updater — no NuGet, so it keeps
/// working even mid-swap. Every path is guarded: a Restart Manager failure
/// degrades gracefully to the pre-existing behaviour (the caller's
/// <c>Directory.Move</c> throws the sharing violation as it did before).
/// </summary>
internal static class InstallRootLock
{
    /// <summary>The browser subprocess image name WebView2 spawns (no extension, as GetProcessesByName wants).</summary>
    private const string WebViewImageName = "msedgewebview2";

    /// <summary>Cap on how many files we hand Restart Manager. The lock hot-spots
    /// (the WebView2 user-data folder + top-level images) fit far under this;
    /// the cap only bounds the pathological "someone pointed us at a giant tree"
    /// case so registration stays cheap.</summary>
    private const int MaxProbeFiles = 1024;

    /// <summary>
    /// Find every process holding a file open under <paramref name="installRoot"/>
    /// and terminate the ones that are safe to kill (our WebView2 children, any of
    /// the suite image names, or a process whose main module runs from inside the
    /// tree). Returns the number of processes actually killed. Never throws.
    /// </summary>
    internal static int TryReleaseLockers(string installRoot, Action<string> log, IReadOnlyList<string> suiteImageNames)
    {
        int killed = 0;
        try
        {
            string rootFull;
            try { rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot)); }
            catch (Exception ex) { log($"unlock: unparseable install root '{installRoot}': {ex.Message}"); return 0; }

            List<string> probe = CollectProbeFiles(rootFull, log);
            if (probe.Count == 0)
            {
                log("unlock: no probe files under the install root — nothing to query.");
                return 0;
            }

            List<LockerInfo> lockers = FindLockers(probe.ToArray(), log);
            if (lockers.Count == 0)
            {
                log("unlock: Restart Manager reports no process holding the install tree.");
                return 0;
            }

            foreach (LockerInfo locker in lockers)
            {
                if (TryKillIfOurs(locker, rootFull, suiteImageNames, log)) killed++;
            }
            log($"unlock: released {killed} of {lockers.Count} locking process(es).");
        }
        catch (Exception ex)
        {
            log($"unlock: unexpected failure (ignored — swap will retry/normal-fail): {ex.Message}");
        }
        return killed;
    }

    /// <summary>
    /// Kill <paramref name="locker"/> only when it is unambiguously part of this
    /// install: a WebView2 child, one of the suite image names, or a process
    /// whose main module executes from under <paramref name="rootFull"/>. Anything
    /// else is logged and left running. Returns true iff a kill was issued.
    /// </summary>
    private static bool TryKillIfOurs(LockerInfo locker, string rootFull,
        IReadOnlyList<string> suiteImageNames, Action<string> log)
    {
        Process? proc = null;
        try
        {
            proc = Process.GetProcessById(locker.Pid);
            string name = SafeProcessName(proc);
            string? modulePath = SafeMainModulePath(proc);

            string where = modulePath is null ? "(module path unavailable)" : modulePath;
            if (!ShouldKill(locker.Pid, Environment.ProcessId, name, modulePath, rootFull, suiteImageNames))
            {
                if (locker.Pid == Environment.ProcessId)
                {
                    // The running updater's own apphost exe / managed dll can sit
                    // under installRoot (the run-in-place fallback layout at
                    // {installRoot}\Updater\), and Restart Manager reports the
                    // session owner as a locker of its own loaded image. Killing
                    // it here would abort the swap mid-flight and write no result
                    // — the swap retry (or the outer catch) handles the lock, not
                    // a suicide.
                    log($"unlock: locker pid {locker.Pid} is THIS updater process — never terminating self.");
                }
                else
                {
                    log($"unlock: '{locker.AppName}' (pid {locker.Pid}, {name}) holds the install tree but is NOT ours ({where}) — leaving it alone.");
                }
                return false;
            }

            log($"unlock: terminating our locking process '{name}' (pid {locker.Pid}, {where}).");
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(2000);
            return true;
        }
        catch (ArgumentException)
        {
            // Already exited between RmGetList and now — exactly the outcome we want.
            log($"unlock: locking pid {locker.Pid} already gone.");
            return false;
        }
        catch (Exception ex)
        {
            log($"unlock: could not terminate pid {locker.Pid} ({locker.AppName}): {ex.Message}");
            return false;
        }
        finally { proc?.Dispose(); }
    }

    /// <summary>
    /// Pure kill gate (unit-tested). Never the running updater itself — a
    /// self-kill would abort the swap mid-flight (Restart Manager reports the
    /// session owner as a locker of its own loaded image, and in the run-in-place
    /// layout that image is under the install root). Otherwise defers to
    /// <see cref="IsOurProcess"/>.
    /// </summary>
    internal static bool ShouldKill(int candidatePid, int selfPid, string processName,
        string? mainModulePath, string rootFull, IReadOnlyList<string> suiteImageNames)
    {
        if (candidatePid == selfPid) return false;
        return IsOurProcess(processName, mainModulePath, rootFull, suiteImageNames);
    }

    /// <summary>
    /// Pure classifier (unit-tested): is a process one we are allowed to kill to
    /// free the install tree? True for the WebView2 child image, any suite image
    /// name, or a process running from inside <paramref name="rootFull"/>.
    /// </summary>
    internal static bool IsOurProcess(string processName, string? mainModulePath,
        string rootFull, IReadOnlyList<string> suiteImageNames)
    {
        if (string.Equals(processName, WebViewImageName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (suiteImageNames is not null)
        {
            foreach (string img in suiteImageNames)
                if (string.Equals(processName, img, StringComparison.OrdinalIgnoreCase))
                    return true;
        }

        if (!string.IsNullOrEmpty(mainModulePath) && !string.IsNullOrEmpty(rootFull))
        {
            try
            {
                string modFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mainModulePath));
                if (modFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(modFull, rootFull, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { /* unparseable module path — treat as not-ours */ }
        }
        return false;
    }

    /// <summary>
    /// Collect a bounded set of files under <paramref name="rootFull"/> to hand
    /// Restart Manager. Prioritises the two lock hot-spots so the real locker is
    /// always represented even under the cap: (1) everything inside any
    /// <c>*.WebView2</c> user-data folder (where the browser child holds its
    /// exclusive handles), then (2) top-level executables / native libraries
    /// (image-section locks). Best-effort — enumeration faults are swallowed.
    /// Internal for unit testing.
    /// </summary>
    internal static List<string> CollectProbeFiles(string rootFull, Action<string>? log = null)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path)
        {
            if (files.Count >= MaxProbeFiles) return;
            if (seen.Add(path)) files.Add(path);
        }

        if (!Directory.Exists(rootFull)) return files;

        // (1) WebView2 user-data folders anywhere in the tree — the lockfile,
        //     "Local State", and the leveldb set are what msedgewebview2 pins.
        try
        {
            foreach (string udf in Directory.EnumerateDirectories(rootFull, "*.WebView2", SearchOption.AllDirectories))
            {
                if (files.Count >= MaxProbeFiles) break;
                try
                {
                    foreach (string f in Directory.EnumerateFiles(udf, "*", SearchOption.AllDirectories))
                    {
                        Add(f);
                        if (files.Count >= MaxProbeFiles) break;
                    }
                }
                catch (Exception ex) { log?.Invoke($"unlock: could not enumerate WebView2 folder '{udf}': {ex.Message}"); }
            }
        }
        catch (Exception ex) { log?.Invoke($"unlock: WebView2 folder scan failed: {ex.Message}"); }

        // (2) Top-level executables and native libraries — a running exe/dll maps
        //     its image from the tree and locks that file directly.
        foreach (string pattern in new[] { "*.exe", "*.dll" })
        {
            if (files.Count >= MaxProbeFiles) break;
            try
            {
                foreach (string f in Directory.EnumerateFiles(rootFull, pattern, SearchOption.AllDirectories))
                {
                    Add(f);
                    if (files.Count >= MaxProbeFiles) break;
                }
            }
            catch (Exception ex) { log?.Invoke($"unlock: could not enumerate '{pattern}': {ex.Message}"); }
        }

        return files;
    }

    private static string SafeProcessName(Process p)
    {
        try { return p.ProcessName; } catch { return "(unknown)"; }
    }

    private static string? SafeMainModulePath(Process p)
    {
        try { return p.MainModule?.FileName; } catch { return null; }
    }

    // ── Restart Manager (rstrtmgr.dll) interop ──────────────────────────────

    private readonly record struct LockerInfo(int Pid, string AppName);

    /// <summary>
    /// Query Restart Manager for the processes locking <paramref name="files"/>.
    /// Returns an empty list on any failure. Never throws.
    /// </summary>
    private static List<LockerInfo> FindLockers(string[] files, Action<string> log)
    {
        var result = new List<LockerInfo>();
        uint session = 0;
        var sessionKey = new StringBuilder(CCH_RM_SESSION_KEY + 1);

        int rc = RmStartSession(out session, 0, sessionKey);
        if (rc != 0)
        {
            log($"unlock: RmStartSession failed (rc={rc}) — skipping unlock.");
            return result;
        }

        try
        {
            rc = RmRegisterResources(session, (uint)files.Length, files,
                0, null, 0, null);
            if (rc != 0)
            {
                log($"unlock: RmRegisterResources failed (rc={rc}) — skipping unlock.");
                return result;
            }

            // Two-call sizing protocol: probe for the count, then fetch.
            uint pnProcInfoNeeded = 0;
            uint pnProcInfo = 0;
            uint rebootReasons = RmRebootReasonNone;
            rc = RmGetList(session, out pnProcInfoNeeded, ref pnProcInfo, null, ref rebootReasons);

            if (rc == 0 && pnProcInfoNeeded == 0)
                return result; // nothing holds the tree

            if (rc != ERROR_MORE_DATA)
            {
                log($"unlock: RmGetList (sizing) returned rc={rc} — skipping unlock.");
                return result;
            }

            var infos = new RM_PROCESS_INFO[pnProcInfoNeeded];
            pnProcInfo = pnProcInfoNeeded;
            rc = RmGetList(session, out pnProcInfoNeeded, ref pnProcInfo, infos, ref rebootReasons);
            if (rc != 0)
            {
                log($"unlock: RmGetList (fetch) failed (rc={rc}) — skipping unlock.");
                return result;
            }

            for (int i = 0; i < pnProcInfo; i++)
            {
                int pid = infos[i].Process.dwProcessId;
                string appName = infos[i].strAppName ?? "(unknown)";
                result.Add(new LockerInfo(pid, appName));
            }
        }
        catch (Exception ex)
        {
            log($"unlock: Restart Manager query threw ({ex.Message}) — skipping unlock.");
            result.Clear();
        }
        finally
        {
            try { RmEndSession(session); } catch { /* best-effort */ }
        }
        return result;
    }

    private const int CCH_RM_SESSION_KEY = 32;
    private const int CCH_RM_MAX_APP_NAME = 255;
    private const int CCH_RM_MAX_SVC_NAME = 63;
    private const int ERROR_MORE_DATA = 234;
    private const uint RmRebootReasonNone = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    private enum RM_APP_TYPE
    {
        RmUnknownApp = 0,
        RmMainWindow = 1,
        RmOtherWindow = 2,
        RmService = 3,
        RmExplorer = 4,
        RmConsole = 5,
        RmCritical = 1000,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
        public string strServiceShortName;
        public RM_APP_TYPE ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle,
        uint nFiles, string[]? rgsFileNames,
        uint nApplications, RM_UNIQUE_PROCESS[]? rgApplications,
        uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle,
        out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);
}
