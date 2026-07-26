using System;
using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
// 'Process' binds to a Phoenix type in Hub.Core — alias the BCL one (same
// convention as HangRecoveryLauncher / PillarBootstrap).
using SysProcess = System.Diagnostics.Process;

namespace Phoenix.Controls.Hub.WinUI.Services;

/// <summary>
/// The single hardened exit primitive for every INTENTIONAL Hub shutdown
/// (window close, File → Exit, update-apply). Ends the process with
/// <c>TerminateProcess</c> (<c>Process.Kill()</c> on self) instead of
/// <see cref="Environment.Exit(int)"/> or the natural WinUI exit.
///
/// <para>Why: both Environment.Exit and the natural exit (Application.Start
/// returning after the last window closes) terminate through the Windows
/// <c>ExitProcess</c> sequence — which runs every loaded DLL's
/// <c>DLL_PROCESS_DETACH</c> under the loader lock. This process hosts XAML,
/// WebView2, Win2D, TSF/msctf and (on some machines) AV hook DLLs; any one of
/// them wedging in detach leaves a window-less zombie Hub that still holds the
/// single-instance mutex (the next launch silently exits with "already
/// running") and image-section locks on <c>{app}\Hub\*</c>. Worse, a process
/// already inside ExitProcess frequently cannot be force-killed from outside
/// (TerminateProcess fails on an exiting process), so the auto-updater's
/// wait/kill/Restart-Manager layers all fail and only a reboot clears the
/// install tree — the exact "update fails until I restart the PC" report.
/// TerminateProcess never enters the DLL-detach sequence, so this hazard class
/// cannot occur; the kernel reclaims every handle, socket, mutex and file lock
/// unconditionally. Same rationale as HangRecoveryLauncher's
/// hard-kill ("graceful shutdown would deadlock").</para>
///
/// <para>Because TerminateProcess skips <c>AppDomain.ProcessExit</c>, the two
/// flushes that relied on it are run explicitly here: the Architect
/// sibling-window replay store and the rolling diagnostic file log.</para>
/// </summary>
internal static class HubProcessExit
{
    /// <summary>
    /// Flush what must survive, kill our WebView2 browser children, then
    /// hard-terminate this process. Does not return (except on the
    /// never-expected double-fault path, where it falls back to
    /// <see cref="Environment.Exit(int)"/>). Never throws.
    /// </summary>
    internal static void TerminateSelf(string reason)
    {
        // 1. Final log line + the ProcessExit-replacement flushes. The file-log
        //    Stop drains its queue with a bounded join, so the line below is on
        //    disk before the process dies.
        try
        {
            GlobalLogger.Log(
                $"Hard-terminating the Hub process via TerminateProcess ({reason}) — " +
                "skipping native DLL teardown so shutdown can never wedge into a zombie.",
                "HubProcessExit", LogLevel.System);
        }
        catch { /* logging must never block the exit */ }
        try { Phoenix.Controls.Architect.WinUI.Services.RecentSiblingsStore.FlushNow(); }
        catch { /* best-effort */ }

        // 2. WebView2 browser children don't always notice a terminated parent
        //    promptly — kill them so nothing of this session lingers in Task
        //    Manager (their user-data folder is outside the install tree since
        //    1.0.42, but an orphan is still an orphan). Before the file-log
        //    Stop below so the per-child kill lines still reach disk.
        try { HangRecoveryLauncher.KillOwnWebViewChildren(Environment.ProcessId); }
        catch { /* best-effort */ }

        try { DiagnosticFileLog.Stop(); }
        catch { /* best-effort */ }

        // 3. The exit. TerminateProcess ends the process without running
        //    DLL_PROCESS_DETACH / finalizers / ProcessExit — nothing left that
        //    can hang.
        try { SysProcess.GetCurrentProcess().Kill(); }
        catch
        {
            // Kill on self failing is not a known-real path; keep the legacy
            // exit as the absolute last resort rather than returning to a
            // caller that believes the process is gone.
            try { Environment.Exit(0); } catch { /* already exiting */ }
        }
    }
}
