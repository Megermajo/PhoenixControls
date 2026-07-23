using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// Point-in-time process + system resource sample for the freeze diagnostics
    /// (Majo, 2026-07-23: "include resource usage logs in regards to Architect —
    /// especially on hang-logs"). Answers the standing question every streaming-PC
    /// freeze raises — "the graphics card was quite in use, but the app should not
    /// be that demanding" — with numbers instead of a hunch.
    ///
    /// CPU and GPU utilization are WINDOW-based (two samples ~500ms apart), so
    /// <see cref="TrySample"/> BLOCKS the calling thread for the window — call it
    /// only from background/diagnostic threads (the watchdog capture worker, the
    /// watchdog poll thread), never the UI thread. GPU numbers come from the PDH
    /// "GPU Engine" counter set — the same source Task Manager's GPU column reads —
    /// summed across engines, so a multi-engine total CAN exceed 100 (each engine
    /// contributes up to 100). Own-process utilization filters the pid_&lt;pid&gt;_*
    /// instances.
    ///
    /// Everything is best-effort: a failed probe yields nulls and the formatters
    /// print "unavailable" — diagnostics must never add a fault to the app they
    /// observe (same contract as NativeMiniDump / GpuTdrProbe).
    /// </summary>
    public static class ResourceUsageProbe
    {
        /// <summary>One sampled view; null fields = that probe was unavailable.</summary>
        public sealed class Snapshot
        {
            public double? ProcessCpuPercent;       // % of TOTAL machine CPU (all cores = 100)
            public double? SystemCpuPercent;        // whole-machine busy %
            public long WorkingSetBytes;
            public long PrivateBytes;
            public long GcHeapBytes;
            public int ThreadCount;
            public double? SystemMemoryLoadPercent; // GlobalMemoryStatusEx dwMemoryLoad
            public long? SystemAvailableBytes;
            public double? GpuProcessPercent;       // engine-sum for THIS process
            public double? GpuTotalPercent;         // engine-sum across ALL processes
            public long? GpuProcessDedicatedBytes;  // this process's dedicated GPU memory
        }

        /// <summary>
        /// Take a windowed sample (~<paramref name="windowMs"/> of wall clock).
        /// Never throws; individual probes degrade to null independently.
        /// </summary>
        public static Snapshot TrySample(int windowMs = 500)
        {
            var snap = new Snapshot();
            windowMs = Math.Clamp(windowMs, 100, 5000);

            // Instantaneous memory/thread numbers first (cheap, no window).
            FillInstant(snap);

            IntPtr query = IntPtr.Zero, engineCounter = IntPtr.Zero, memCounter = IntPtr.Zero;
            bool gpuArmed = false;
            try
            {
                gpuArmed = TryArmGpuQuery(out query, out engineCounter, out memCounter);
            }
            catch { gpuArmed = false; }

            // CPU t0.
            bool haveProcT0 = TryGetProcessCpu(out TimeSpan procT0);
            bool haveSysT0 = GetSystemTimes(out long idle0, out long kernel0, out long user0);
            long wall0 = Stopwatch.GetTimestamp();

            try { Thread.Sleep(windowMs); } catch { /* interrupted — window just shrinks */ }

            // CPU t1.
            double wallSeconds = (Stopwatch.GetTimestamp() - wall0) / (double)Stopwatch.Frequency;
            if (wallSeconds > 0.01)
            {
                if (haveProcT0 && TryGetProcessCpu(out TimeSpan procT1))
                {
                    double busy = (procT1 - procT0).TotalSeconds / (wallSeconds * Environment.ProcessorCount) * 100.0;
                    snap.ProcessCpuPercent = Math.Clamp(busy, 0, 100);
                }
                if (haveSysT0 && GetSystemTimes(out long idle1, out long kernel1, out long user1))
                {
                    // kernel time INCLUDES idle time in the GetSystemTimes contract.
                    double total = (kernel1 - kernel0) + (user1 - user0);
                    double idle = idle1 - idle0;
                    if (total > 0)
                        snap.SystemCpuPercent = Math.Clamp((1.0 - idle / total) * 100.0, 0, 100);
                }
            }

            if (gpuArmed)
            {
                try
                {
                    if (PdhCollectQueryData(query) == 0)
                    {
                        int pid = Environment.ProcessId;
                        string pidPrefix = $"pid_{pid.ToString(CultureInfo.InvariantCulture)}_";

                        if (TryReadCounterArray(engineCounter, out var engineItems))
                        {
                            double totalPct = 0, procPct = 0;
                            bool any = false;
                            foreach (var (name, value) in engineItems)
                            {
                                if (double.IsNaN(value) || value < 0) continue;
                                any = true;
                                totalPct += value;
                                if (name.StartsWith(pidPrefix, StringComparison.OrdinalIgnoreCase))
                                    procPct += value;
                            }
                            if (any)
                            {
                                snap.GpuTotalPercent = totalPct;
                                snap.GpuProcessPercent = procPct;
                            }
                        }

                        if (memCounter != IntPtr.Zero && TryReadCounterArray(memCounter, out var memItems))
                        {
                            double bytes = 0;
                            bool any = false;
                            foreach (var (name, value) in memItems)
                            {
                                if (!name.StartsWith(pidPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                                if (double.IsNaN(value) || value < 0) continue;
                                any = true;
                                bytes += value;
                            }
                            if (any) snap.GpuProcessDedicatedBytes = (long)bytes;
                        }
                    }
                }
                catch { /* GPU numbers stay null */ }
            }

            if (query != IntPtr.Zero) { try { PdhCloseQuery(query); } catch { } }
            return snap;
        }

        /// <summary>
        /// Instant (no CPU/GPU window) one-liner for inline log messages — safe to
        /// build on the watchdog thread mid-stall. Never throws.
        /// </summary>
        public static string TryFormatInstantLine()
        {
            try
            {
                var snap = new Snapshot();
                FillInstant(snap);
                var sb = new StringBuilder(128);
                sb.Append("RAM ").Append(Mb(snap.WorkingSetBytes)).Append(" WS / ")
                  .Append(Mb(snap.PrivateBytes)).Append(" private / ")
                  .Append(Mb(snap.GcHeapBytes)).Append(" GC · ")
                  .Append(snap.ThreadCount).Append(" threads");
                if (snap.SystemMemoryLoadPercent is double load)
                    sb.Append(" · system RAM ").Append(load.ToString("F0", CultureInfo.InvariantCulture)).Append("% used");
                return sb.ToString();
            }
            catch
            {
                return "(resource probe unavailable)";
            }
        }

        /// <summary>Single-line summary of a windowed sample (for breadcrumb logs).</summary>
        public static string FormatLine(Snapshot s)
        {
            var sb = new StringBuilder(192);
            sb.Append("CPU ").Append(Pct(s.ProcessCpuPercent)).Append(" proc / ")
              .Append(Pct(s.SystemCpuPercent)).Append(" system · RAM ")
              .Append(Mb(s.WorkingSetBytes)).Append(" WS (")
              .Append(Mb(s.PrivateBytes)).Append(" private, ")
              .Append(Mb(s.GcHeapBytes)).Append(" GC, ")
              .Append(s.ThreadCount).Append(" threads)");
            if (s.SystemMemoryLoadPercent is double load)
                sb.Append(" · system RAM ").Append(load.ToString("F0", CultureInfo.InvariantCulture)).Append("% used");
            sb.Append(" · GPU ").Append(Pct(s.GpuProcessPercent)).Append(" proc / ")
              .Append(Pct(s.GpuTotalPercent)).Append(" total (engine-sum)");
            if (s.GpuProcessDedicatedBytes is long g)
                sb.Append(" · GPU mem ").Append(Mb(g));
            return sb.ToString();
        }

        /// <summary>
        /// Multi-line section for the freeze report. Includes the GPU-pressure
        /// note when the machine-wide engine-sum shows real contention — the
        /// signal that decides the "GPU was quite under load" question per freeze.
        /// </summary>
        public static string FormatReportSection(Snapshot s)
        {
            var sb = new StringBuilder(512);
            sb.AppendLine($"  Process CPU:   {Pct(s.ProcessCpuPercent)} of machine ({Environment.ProcessorCount} logical cores)");
            sb.AppendLine($"  System CPU:    {Pct(s.SystemCpuPercent)} busy");
            sb.AppendLine($"  Process RAM:   {Mb(s.WorkingSetBytes)} working set · {Mb(s.PrivateBytes)} private · {Mb(s.GcHeapBytes)} GC heap · {s.ThreadCount} threads");
            sb.AppendLine(s.SystemMemoryLoadPercent is double load && s.SystemAvailableBytes is long avail
                ? $"  System RAM:    {load.ToString("F0", CultureInfo.InvariantCulture)}% used · {Mb(avail)} available"
                : "  System RAM:    unavailable");
            if (s.GpuProcessPercent is not null || s.GpuTotalPercent is not null)
            {
                sb.AppendLine($"  GPU (engine-sum, Task-Manager source): {Pct(s.GpuProcessPercent)} this process · {Pct(s.GpuTotalPercent)} machine-wide");
                if (s.GpuProcessDedicatedBytes is long g)
                    sb.AppendLine($"  GPU memory:    {Mb(g)} dedicated (this process)");
                if (s.GpuTotalPercent is double total && total >= 90.0)
                    sb.AppendLine("  NOTE: the GPU was under heavy machine-wide load at capture time — GPU contention " +
                                  "widens every frame and enlarges the stall window, even when the wedge itself is not a GPU call.");
            }
            else
            {
                sb.AppendLine("  GPU:           unavailable (PDH \"GPU Engine\" counters not readable on this system)");
            }
            return sb.ToString().TrimEnd('\r', '\n');
        }

        // ── instant probes ───────────────────────────────────────────────────

        private static void FillInstant(Snapshot snap)
        {
            try
            {
                // Fully qualified — Phoenix.Controls.Hub.Core has its own Process
                // model (ProcessManager.cs) that shadows System.Diagnostics.
                using var p = System.Diagnostics.Process.GetCurrentProcess();
                snap.WorkingSetBytes = p.WorkingSet64;
                snap.PrivateBytes = p.PrivateMemorySize64;
                snap.ThreadCount = p.Threads.Count;
            }
            catch { /* leave zeros */ }
            try { snap.GcHeapBytes = GC.GetTotalMemory(forceFullCollection: false); } catch { }
            try
            {
                var mem = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(mem))
                {
                    snap.SystemMemoryLoadPercent = mem.dwMemoryLoad;
                    snap.SystemAvailableBytes = (long)Math.Min(mem.ullAvailPhys, long.MaxValue);
                }
            }
            catch { /* stays null */ }
        }

        private static bool TryGetProcessCpu(out TimeSpan cpu)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetCurrentProcess();
                cpu = p.TotalProcessorTime;
                return true;
            }
            catch
            {
                cpu = TimeSpan.Zero;
                return false;
            }
        }

        private static string Pct(double? v)
            => v is double d ? d.ToString("F0", CultureInfo.InvariantCulture) + "%" : "?";

        private static string Mb(long bytes)
            => (bytes / (1024.0 * 1024.0)).ToString("F0", CultureInfo.InvariantCulture) + " MB";

        // ── PDH GPU counters ─────────────────────────────────────────────────

        private const uint PDH_MORE_DATA = 0x800007D2;
        private const uint PDH_FMT_DOUBLE = 0x00000200;
        private const uint PDH_FMT_NOCAP100 = 0x00008000;

        private static bool TryArmGpuQuery(out IntPtr query, out IntPtr engineCounter, out IntPtr memCounter)
        {
            query = IntPtr.Zero;
            engineCounter = IntPtr.Zero;
            memCounter = IntPtr.Zero;

            if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0) return false;
            if (PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out engineCounter) != 0)
            {
                try { PdhCloseQuery(query); } catch { }
                query = IntPtr.Zero;
                return false;
            }
            // GPU memory is optional garnish — utilization is the load-bearing number.
            if (PdhAddEnglishCounter(query, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out memCounter) != 0)
                memCounter = IntPtr.Zero;

            // First collect primes the rate counters; the post-window collect
            // (in TrySample) produces the formatted values.
            if (PdhCollectQueryData(query) != 0)
            {
                try { PdhCloseQuery(query); } catch { }
                query = IntPtr.Zero;
                engineCounter = IntPtr.Zero;
                memCounter = IntPtr.Zero;
                return false;
            }
            return true;
        }

        private static bool TryReadCounterArray(IntPtr counter, out (string Name, double Value)[] items)
        {
            items = Array.Empty<(string, double)>();
            uint bufSize = 0, count = 0;
            uint status = PdhGetFormattedCounterArray(
                counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref bufSize, out count, IntPtr.Zero);
            if (status != PDH_MORE_DATA || bufSize == 0) return false;

            IntPtr buffer = Marshal.AllocHGlobal((int)bufSize);
            try
            {
                status = PdhGetFormattedCounterArray(
                    counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref bufSize, out count, buffer);
                if (status != 0) return false;

                int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
                var list = new (string, double)[count];
                int written = 0;
                for (uint i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buffer + (int)(i * itemSize));
                    // CStatus 0 = valid, 1 = valid-new — anything else is a stale/absent value.
                    if (item.FmtValue.CStatus > 1) continue;
                    string name = Marshal.PtrToStringUni(item.szName) ?? string.Empty;
                    list[written++] = (name, item.FmtValue.doubleValue);
                }
                if (written < list.Length) Array.Resize(ref list, written);
                items = list;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PDH_FMT_COUNTERVALUE
        {
            public uint CStatus;
            public double doubleValue; // the union's largest arm; aligned at offset 8
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PDH_FMT_COUNTERVALUE_ITEM
        {
            public IntPtr szName;
            public PDH_FMT_COUNTERVALUE FmtValue;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQuery(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounter(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr hQuery);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhGetFormattedCounterArray(
            IntPtr hCounter, uint dwFormat, ref uint lpdwBufferSize, out uint lpdwItemCount, IntPtr itemBuffer);

        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr hQuery);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

        [StructLayout(LayoutKind.Sequential)]
        private sealed class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
    }
}
