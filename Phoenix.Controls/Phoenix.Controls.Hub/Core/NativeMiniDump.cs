using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// Native minidump-on-hang — the instrument that sees BELOW the managed→native
    /// boundary where every recorded UI freeze actually lives.
    ///
    /// The ClrMD <see cref="HangStackCapture"/> can only walk MANAGED stacks, but
    /// every captured freeze (2026-07-01, 2026-07-14 ×3, 2026-07-16) shows the UI
    /// thread parked with ZERO managed frames above <c>Application.Start</c> — it is
    /// blocked in a NATIVE call (GPU/DXGI present, WebView2 RPC, DirectWrite, a
    /// non-pumping OS wait) that ClrMD structurally cannot name. A minidump captures
    /// the native thread stacks + module list, so opening the <c>.dmp</c> in
    /// WinDbg/VS shows the UI thread's native call stack and names the blocking
    /// module — turning a recurring mystery into a fixable bug.
    ///
    /// Written via <c>dbghelp!MiniDumpWriteDump</c> on THIS (hung, not crashed)
    /// process: the heap is intact, so an in-process write from a healthy background
    /// thread is safe and standard. MiniDumpWriteDump briefly suspends the other
    /// threads while it walks them. By default NO full heap
    /// (<c>MiniDumpWithFullMemory</c> = 0x2 is excluded) — thread stacks + module
    /// info are enough to name a blocking module and keep the file to tens of MB,
    /// not hundreds. No NuGet dependency: dbghelp.dll ships with Windows.
    ///
    /// OPT-IN FULL-MEMORY VARIANT (2026-07 streaming-PC investigation): the
    /// stacks-only dump proved the UI thread idle in a clean top-level GetMessage
    /// while DispatcherQueue callbacks never ran — but WITHOUT heap pages the
    /// DispatcherQueue / CoreMessaging / XAML-core internals that would explain the
    /// wake failure are not inspectable. <c>AppConfig.HangFullMemoryDump</c> lets
    /// Majo arm a full-memory dump on the FIRST capture of a stall (multi-GB, so:
    /// its own filename prefix, a 2-file retention cap, and a free-space guard that
    /// silently falls back to the lightweight dump when the disk can't take it).
    /// </summary>
    public static class NativeMiniDump
    {
        private const string DumpFilePrefix = "ui-hang-dump-";
        private const string FullDumpFilePrefix = "ui-hang-fulldump-";
        private static readonly TimeSpan DumpFileMaxAge = TimeSpan.FromDays(14);

        // Dumps are far larger than the text captures — bound the folder by COUNT
        // as well as age, so a bad multi-restart day can't fill a streamer's disk.
        private const int MaxRetainedDumps = 6;

        // Full-memory dumps are multi-GB — keep only the two newest. Two (not
        // one) so the freeze that matters isn't overwritten by a follow-up
        // freeze of the relaunched process before Majo collects the file.
        private const int MaxRetainedFullDumps = 2;

        // A full-memory dump is roughly the process's committed private memory.
        // Demand that plus a safety margin in free disk space, else fall back to
        // the lightweight dump — a diagnostics feature must never be the thing
        // that fills a streamer's system drive mid-stream.
        private const long FullDumpFreeSpaceMarginBytes = 1L * 1024 * 1024 * 1024;

        // In-flight full dumps carry this suffix until the write completes (see
        // TryWriteToFile) — a killed process can only ever leave a *.partial
        // carcass behind, never a truncated .dmp. Carcasses older than this age
        // are reclaimed by the prune (an ACTIVE write belongs to a process that
        // started it minutes ago at most; anything older is dead).
        private const string PartialSuffix = ".partial";
        private static readonly TimeSpan PartialMaxAge = TimeSpan.FromMinutes(10);

        // MINIDUMP_TYPE — a "walkable hang dump WITHOUT the full heap". Normal(0)
        // already carries the thread list, module list and thread-stack memory
        // needed to walk every native stack; the rest sharpen symbol / handle
        // resolution in WinDbg at negligible size. Values are the real Win32
        // constants — note WithHandleData is 0x4 (0x2 is WithFullMemory, which we
        // must NOT set or dumps balloon to gigabytes).
        [Flags]
        private enum MiniDumpType : uint
        {
            Normal                = 0x00000000,
            WithFullMemory        = 0x00000002,
            WithHandleData        = 0x00000004,
            WithUnloadedModules   = 0x00000020,
            WithProcessThreadData = 0x00000100,
            WithFullMemoryInfo    = 0x00000800,
            WithThreadInfo        = 0x00001000,
        }

        private const MiniDumpType HangDumpType =
            MiniDumpType.Normal
            | MiniDumpType.WithHandleData
            | MiniDumpType.WithUnloadedModules
            | MiniDumpType.WithProcessThreadData
            | MiniDumpType.WithFullMemoryInfo
            | MiniDumpType.WithThreadInfo;

        // The opt-in variant: everything the lightweight dump carries PLUS the
        // full heap, so WinDbg can inspect DispatcherQueue / CoreMessaging /
        // XAML-core state (the exact blind spot the streaming-PC wake-failure
        // analysis hit at flags 0x1924).
        private const MiniDumpType FullHangDumpType = HangDumpType | MiniDumpType.WithFullMemory;

        [DllImport("dbghelp.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MiniDumpWriteDump(
            IntPtr hProcess, uint processId, SafeHandle hFile,
            MiniDumpType dumpType, IntPtr exceptionParam,
            IntPtr userStreamParam, IntPtr callbackParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        /// <summary>
        /// Write a native minidump of THIS process into <paramref name="directory"/>
        /// as <c>ui-hang-dump-*.dmp</c> and prune old siblings. Returns the written
        /// path, or null with <paramref name="error"/> set — never throws (a
        /// diagnostics failure must not add a second fault to an already-frozen app).
        /// </summary>
        public static string? TryWriteToFile(string directory, out string? error)
            => TryWriteToFile(directory, fullMemory: false, out _, out error);

        /// <summary>
        /// Like <see cref="TryWriteToFile(string, out string?)"/>, but when
        /// <paramref name="fullMemory"/> is true attempts a FULL-MEMORY dump
        /// (<c>ui-hang-fulldump-*.dmp</c>, multi-GB). If the target drive lacks
        /// the estimated space, silently falls back to the lightweight dump —
        /// <paramref name="wroteFullMemory"/> reports what was actually written.
        /// </summary>
        public static string? TryWriteToFile(string directory, bool fullMemory, out bool wroteFullMemory, out string? error)
        {
            wroteFullMemory = false;
            if (fullMemory && !HasSpaceForFullDump(directory))
                fullMemory = false;

            string? path = null;
            string? writePath = null;
            try
            {
                Directory.CreateDirectory(directory);
                string prefix = fullMemory ? FullDumpFilePrefix : DumpFilePrefix;
                path = Path.Combine(
                    directory, $"{prefix}{DateTime.Now:yyyyMMdd-HHmmss-fff}.dmp");
                // Full dumps stream for tens of seconds; a hard process kill
                // mid-write (auto-relaunch, user End-Task) runs NO cleanup, so
                // they are written under a .partial name and renamed only on
                // success — a truncated dump can never masquerade as a real
                // capture, and stale .partial carcasses are reclaimed by the
                // prune. Lightweight dumps are sub-second; they keep the direct
                // write.
                writePath = fullMemory ? path + PartialSuffix : path;

                using (var fs = new FileStream(writePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    bool ok = MiniDumpWriteDump(
                        GetCurrentProcess(), (uint)Environment.ProcessId, fs.SafeFileHandle,
                        fullMemory ? FullHangDumpType : HangDumpType,
                        IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    if (!ok)
                    {
                        int win32 = Marshal.GetLastWin32Error();
                        error = $"MiniDumpWriteDump failed (Win32 {win32}: {new Win32Exception(win32).Message}).";
                        // A failed write leaves an empty/partial file — drop it so a
                        // truncated dump can't masquerade as a real capture.
                        fs.Dispose();
                        SafeDelete(writePath);
                        // A failed FULL write (e.g. mid-write disk exhaustion) still
                        // has the lightweight dump as a fallback worth trying.
                        if (fullMemory)
                            return TryWriteToFile(directory, fullMemory: false, out wroteFullMemory, out error);
                        return null;
                    }
                }

                if (fullMemory)
                    File.Move(writePath, path);

                PruneOldDumps(directory);
                wroteFullMemory = fullMemory;
                error = null;
                return path;
            }
            catch (Exception ex)
            {
                // Best-effort remove of a half-written file on any throw.
                if (writePath is not null) SafeDelete(writePath);
                if (fullMemory)
                    return TryWriteToFile(directory, fullMemory: false, out wroteFullMemory, out error);
                string detail = ex.ToString();
                error = detail.Length > 600 ? detail[..600] : detail;
                return null;
            }
        }

        // Free-space guard for the full-memory variant: a full dump is roughly
        // the process's committed private memory, so demand that plus a fixed
        // margin. Any probe failure (weird path, substituted drive) errs on the
        // side of the LIGHTWEIGHT dump — never gamble with the system drive.
        private static bool HasSpaceForFullDump(string directory)
        {
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(directory));
                if (string.IsNullOrEmpty(root)) return false;
                long estimate = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
                return new DriveInfo(root).AvailableFreeSpace >= estimate + FullDumpFreeSpaceMarginBytes;
            }
            catch
            {
                return false;
            }
        }

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* locked / already gone — nothing to do */ }
        }

        // Keep the logs folder bounded: prune by age (14 days) first, then cap the
        // survivors to the newest MaxRetainedDumps by write time. The lightweight
        // and full-memory families prune independently (disjoint prefixes,
        // separate caps) — six lightweight dumps are tens of MB, two full dumps
        // can be several GB.
        private static void PruneOldDumps(string directory)
        {
            PruneDumpFamily(directory, DumpFilePrefix, MaxRetainedDumps);
            PruneDumpFamily(directory, FullDumpFilePrefix, MaxRetainedFullDumps);
            PruneStalePartials(directory);
        }

        // Reclaim .partial carcasses left by a process that was killed mid-write
        // (the auto-relaunch's hard kill runs no cleanup). Age-gated so a full
        // dump ACTIVELY being written by a sibling/dying process is never
        // deleted out from under it.
        private static void PruneStalePartials(string directory)
        {
            try
            {
                var cutoff = DateTime.UtcNow - PartialMaxAge;
                foreach (var file in Directory.EnumerateFiles(directory, FullDumpFilePrefix + "*.dmp" + PartialSuffix))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoff)
                            File.Delete(file);
                    }
                    catch { /* locked / already gone — skip */ }
                }
            }
            catch { /* pruning is best-effort */ }
        }

        private static void PruneDumpFamily(string directory, string prefix, int maxRetained)
        {
            try
            {
                var files = Directory.EnumerateFiles(directory, prefix + "*.dmp").ToList();

                var cutoff = DateTime.UtcNow - DumpFileMaxAge;
                foreach (var file in files.ToList())
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoff)
                        {
                            File.Delete(file);
                            files.Remove(file);
                        }
                    }
                    catch { /* locked / already gone — skip */ }
                }

                if (files.Count > maxRetained)
                {
                    foreach (var file in files
                                 .OrderByDescending(f => { try { return File.GetLastWriteTimeUtc(f); } catch { return DateTime.MinValue; } })
                                 .Skip(maxRetained))
                    {
                        try { File.Delete(file); } catch { /* best-effort */ }
                    }
                }
            }
            catch { /* pruning is best-effort */ }
        }
    }
}
