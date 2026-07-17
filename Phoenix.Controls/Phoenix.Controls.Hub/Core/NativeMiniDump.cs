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
    /// threads while it walks them. Deliberately NO full heap
    /// (<c>MiniDumpWithFullMemory</c> = 0x2 is excluded) — thread stacks + module
    /// info are enough to name the blocker and keep the file to tens of MB, not
    /// hundreds. No NuGet dependency: dbghelp.dll ships with Windows.
    /// </summary>
    public static class NativeMiniDump
    {
        private const string DumpFilePrefix = "ui-hang-dump-";
        private static readonly TimeSpan DumpFileMaxAge = TimeSpan.FromDays(14);

        // Dumps are far larger than the text captures — bound the folder by COUNT
        // as well as age, so a bad multi-restart day can't fill a streamer's disk.
        private const int MaxRetainedDumps = 6;

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
        {
            string? path = null;
            try
            {
                Directory.CreateDirectory(directory);
                path = Path.Combine(
                    directory, $"{DumpFilePrefix}{DateTime.Now:yyyyMMdd-HHmmss-fff}.dmp");

                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    bool ok = MiniDumpWriteDump(
                        GetCurrentProcess(), (uint)Environment.ProcessId, fs.SafeFileHandle,
                        HangDumpType, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    if (!ok)
                    {
                        int win32 = Marshal.GetLastWin32Error();
                        error = $"MiniDumpWriteDump failed (Win32 {win32}: {new Win32Exception(win32).Message}).";
                        // A failed write leaves an empty/partial file — drop it so a
                        // truncated dump can't masquerade as a real capture.
                        fs.Dispose();
                        SafeDelete(path);
                        return null;
                    }
                }

                PruneOldDumps(directory);
                error = null;
                return path;
            }
            catch (Exception ex)
            {
                // Best-effort remove of a half-written file on any throw.
                if (path is not null) SafeDelete(path);
                string detail = ex.ToString();
                error = detail.Length > 600 ? detail[..600] : detail;
                return null;
            }
        }

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* locked / already gone — nothing to do */ }
        }

        // Keep the logs folder bounded: prune by age (14 days) first, then cap the
        // survivors to the newest MaxRetainedDumps by write time.
        private static void PruneOldDumps(string directory)
        {
            try
            {
                var files = Directory.EnumerateFiles(directory, DumpFilePrefix + "*.dmp").ToList();

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

                if (files.Count > MaxRetainedDumps)
                {
                    foreach (var file in files
                                 .OrderByDescending(f => { try { return File.GetLastWriteTimeUtc(f); } catch { return DateTime.MinValue; } })
                                 .Skip(MaxRetainedDumps))
                    {
                        try { File.Delete(file); } catch { /* best-effort */ }
                    }
                }
            }
            catch { /* pruning is best-effort */ }
        }
    }
}
