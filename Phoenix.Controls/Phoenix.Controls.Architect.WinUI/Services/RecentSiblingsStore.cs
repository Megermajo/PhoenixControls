using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Architect.WinUI.Services;

/// <summary>
/// Persistent record of the previously-open sibling Architect
/// windows so Hub's <c>MainWindow.Loaded</c> can replay them on next
/// boot. 0.10.0 scope listed "multi-window paradigm restored" as
/// load-bearing; <see cref="ArchitectWindowRegistry"/> tracks live
/// windows in-memory only, and <see cref="SiblingWindowStateStore"/>
/// persists per-window geometry keyed by absolute path — but neither
/// records the SET of open windows. Without this store, every Hub
/// restart drops the user back to a single embedded canvas.
///
/// <para>
/// The store is intentionally limited to absolute paths the sibling
/// window has loaded — Untitled (unsaved) siblings are NOT recorded
/// because they have no on-disk identity to replay against; the
/// 0.10.0 spec calls out that fresh Untitled windows survive only
/// the current session.
/// </para>
///
/// <para>
/// Hub-side wire-up: <c>MainWindow.Loaded</c>
/// reads <see cref="Load"/> and spawns each entry via
/// <see cref="ArchitectWindowRegistry.OpenFileAsync"/>, then clears
/// the file once the replay completes so a future crash doesn't
/// re-open windows the user explicitly closed.
/// </para>
/// </summary>
public static class RecentSiblingsStore
{
    // Cap on persisted entries — guards against a malicious / faulty
    // caller writing an unbounded list and stalling Hub boot.
    private const int MaxEntries = 32;

    // Debounce window for coalescing Touch bursts (open + focus + save can
    // fire back-to-back) into a single disk write.
    private const int FlushDebounceMs = 500;

    // Serialises disk writes only. Pre-fix the whole Load → mutate → Save
    // sequence ran under one lock ON THE UI THREAD per Touch; two concurrent
    // Touch calls could otherwise both Load the same on-disk list, mutate
    // independent copies, and the second Save would clobber the first
    // writer's change (TOCTOU between Load and Save). The in-memory cache
    // below is now the single source of truth, so this lock only has to keep
    // two flushes from interleaving the file.
    private static readonly object s_ioLock = new();

    // Guards the in-memory cache + debounce state. Mutations (Touch/Remove/
    // Replace) are memory-only under this lock, so callers on the UI thread
    // never wait on disk; the debounced flush snapshots the cache and writes
    // on the thread pool.
    private static readonly object s_cacheLock = new();
    private static List<Entry>? s_cache;
    private static Timer? s_flushTimer;
    private static long s_mutationSeq;   // bumped under s_cacheLock per mutation
    private static long s_flushedSeq;    // last seq written to disk; under s_ioLock
    private static bool s_exitFlushHooked;

    public sealed class Entry
    {
        public string Path { get; set; } = string.Empty;
        public DateTime LastOpenUtc { get; set; }
        // Lower = more recently focused (0 = front-most). Hub replays
        // in descending order so the front-most window ends up Activated
        // last and reclaims the focus.
        public int FocusOrder { get; set; }
    }

    /// <summary>
    /// Append or update the entry for <paramref name="absolutePath"/>.
    /// Existing entries get their timestamp refreshed; new entries push
    /// older ones out once <see cref="MaxEntries"/> is exceeded. Empty
    /// paths (Untitled siblings) are ignored.
    /// </summary>
    public static void Touch(string absolutePath, int focusOrder = 0)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return;
        try
        {
            lock (s_cacheLock)
            {
                var list = EnsureCacheLocked();
                // Remove any prior entry for this path (case-insensitive on Windows).
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(list[i].Path, absolutePath, StringComparison.OrdinalIgnoreCase))
                        list.RemoveAt(i);
                }
                list.Add(new Entry
                {
                    Path = absolutePath,
                    LastOpenUtc = DateTime.UtcNow,
                    FocusOrder = focusOrder,
                });
                // Sort newest-first by LastOpenUtc and cap.
                s_cache = list
                    .OrderByDescending(e => e.LastOpenUtc)
                    .Take(MaxEntries)
                    .ToList();
                MarkDirtyAndScheduleFlushLocked();
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"RecentSiblingsStore.Touch '{absolutePath}' failed: {ex.Message}",
                "RecentSiblingsStore", LogLevel.Debug);
        }
    }

    /// <summary>
    /// Drop the entry for <paramref name="absolutePath"/>. Called from
    /// <see cref="Phoenix.Controls.Architect.WinUI.Hosting.ArchitectSiblingWindow"/>'s
    /// Closed handler so a deliberately-closed window doesn't re-open
    /// on next boot. Empty / missing paths are no-ops.
    /// </summary>
    public static void Remove(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return;
        try
        {
            bool changed;
            lock (s_cacheLock)
            {
                var list = EnsureCacheLocked();
                int before = list.Count;
                list.RemoveAll(e =>
                    string.Equals(e.Path, absolutePath, StringComparison.OrdinalIgnoreCase));
                changed = list.Count != before;
                if (changed)
                {
                    s_mutationSeq++;
                    HookExitFlushLocked();
                }
            }
            // Terminal window-close path — flush synchronously so the removal
            // (and any still-debounced Touch) lands before the host process
            // can call Environment.Exit.
            if (changed) TryFlush();
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"RecentSiblingsStore.Remove '{absolutePath}' failed: {ex.Message}",
                "RecentSiblingsStore", LogLevel.Debug);
        }
    }

    /// <summary>
    /// Replace the persisted set with <paramref name="entries"/>. Hub
    /// boot replay uses this to clear the file once each entry has been
    /// re-opened. Pass an empty list to reset.
    /// </summary>
    public static void Replace(IEnumerable<Entry> entries)
    {
        try
        {
            var list = entries?.ToList() ?? new List<Entry>();
            lock (s_cacheLock)
            {
                s_cache = list;
                s_mutationSeq++;
                HookExitFlushLocked();
            }
            TryFlush();
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"RecentSiblingsStore.Replace failed: {ex.Message}",
                "RecentSiblingsStore", LogLevel.Debug);
        }
    }

    /// <summary>
    /// Read the persisted entries. Returns an empty list when the
    /// file is missing, empty, or unparseable — boot replay is
    /// best-effort. Served from the in-memory cache after the first
    /// read so repeat calls never re-hit disk.
    /// </summary>
    public static List<Entry> Load()
    {
        try
        {
            lock (s_cacheLock)
            {
                // Copy so callers (Hub replay sorts in place) can't mutate
                // the cache's list.
                return new List<Entry>(EnsureCacheLocked());
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"RecentSiblingsStore.Load failed: {ex.Message}",
                "RecentSiblingsStore", LogLevel.System);
            return new List<Entry>();
        }
    }

    private static List<Entry> EnsureCacheLocked()
        => s_cache ??= LoadFromDisk();

    private static List<Entry> LoadFromDisk()
    {
        try
        {
            string path = ResolvePath();
            if (!File.Exists(path)) return new List<Entry>();
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return new List<Entry>();
            var list = JsonSerializer.Deserialize<List<Entry>>(json);
            return list ?? new List<Entry>();
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"RecentSiblingsStore.Load failed: {ex.Message}",
                "RecentSiblingsStore", LogLevel.System);
            return new List<Entry>();
        }
    }

    private static void MarkDirtyAndScheduleFlushLocked()
    {
        s_mutationSeq++;
        HookExitFlushLocked();
        if (s_flushTimer is null)
        {
            s_flushTimer = new Timer(static _ => TryFlush(), null,
                FlushDebounceMs, Timeout.Infinite);
        }
        else
        {
            s_flushTimer.Change(FlushDebounceMs, Timeout.Infinite);
        }
    }

    private static void HookExitFlushLocked()
    {
        // A pending debounced flush can be killed by the host's
        // Environment.Exit(0); ProcessExit still runs on that path, so a
        // one-time hook guarantees the last mutation lands on disk.
        if (s_exitFlushHooked) return;
        s_exitFlushHooked = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryFlush();
    }

    private static void TryFlush()
    {
        try
        {
            List<Entry> snapshot;
            long seq;
            lock (s_cacheLock)
            {
                if (s_cache is null) return;
                snapshot = new List<Entry>(s_cache);
                seq = s_mutationSeq;
            }
            lock (s_ioLock)
            {
                // A concurrent flush already wrote this state (or newer) —
                // skipping keeps a stale snapshot from overwriting it.
                if (seq <= s_flushedSeq) return;
                Save(snapshot);
                s_flushedSeq = seq;
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"RecentSiblingsStore flush failed: {ex.Message}",
                "RecentSiblingsStore", LogLevel.Debug);
        }
    }

    private static void Save(List<Entry> list)
    {
        string path = ResolvePath();
        string json = JsonSerializer.Serialize(list, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        // Atomic temp+replace so a concurrent
        // reader (boot replay) or a second Architect process never observes a
        // half-written file (File.WriteAllText truncates-then-writes in place).
        // Readers see either the old or the new complete file.
        try
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: null);
            else                   File.Move(tmp, path);
        }
        catch
        {
            // Fall back to a direct write if temp+replace isn't possible (e.g.
            // cross-volume temp dir) — MRU state is best-effort, never fatal.
            File.WriteAllText(path, json);
        }
    }

    private static string ResolvePath()
    {
        try
        {
            string dir = Paths.RoamingAppData("Architect");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "recent-siblings.json");
        }
        catch
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recent-siblings.json");
        }
    }
}
