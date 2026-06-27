using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Phoenix.Controls.Shared.Core;

namespace Phoenix.Controls.Architect.WinUI.Services;

/// <summary>
/// 10-deep MRU of .phxg files the user has opened, persisted at
/// %AppData%/PhoenixControls/Architect/recent-files.json. Falls back to the
/// process working directory when AppData isn't writable.
/// </summary>
public static class RecentFiles
{
    private const int Capacity = 10;
    private const string FileName = "recent-files.json";

    private static string ResolvePath()
    {
        try { return Path.Combine(Paths.RoamingAppData("Architect"), FileName); }
        catch { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName); }
    }

    public static List<string> Load()
    {
        try
        {
            var p = ResolvePath();
            if (!File.Exists(p)) return new List<string>();
            var raw = File.ReadAllText(p);
            var list = JsonSerializer.Deserialize<List<string>>(raw) ?? new List<string>();
            return list.Where(s => !string.IsNullOrWhiteSpace(s))
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .Take(Capacity)
                       .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    public static void Touch(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return;
        // [freeze sweep / S10 P2] Acquire _ioGate synchronously around the
        // read-modify-write so the synchronous Touch() path (called directly
        // by ArchitectSiblingWindow) can't interleave its Load → RemoveAll →
        // Insert → Save with a concurrent TouchDeferred() running the same
        // logic on the thread pool. Pre-fix only TouchDeferred() held the
        // gate, so a save in the main window racing an open in the sibling
        // window could silently drop MRU entries (and risk JSON corruption
        // under OneDrive / AV latency). The gate is the same semaphore the
        // deferred path uses, so the two paths now serialise against each
        // other.
        _ioGate.Wait();
        try
        {
            var list = Load();
            list.RemoveAll(s => string.Equals(s, absolutePath, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, absolutePath);
            if (list.Count > Capacity) list = list.Take(Capacity).ToList();
            Save(list);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    // [freeze sweep] Serializes deferred MRU writes so a burst of open/save
    // operations can't interleave read-modify-writes on the file.
    private static readonly System.Threading.SemaphoreSlim _ioGate = new(1, 1);

    /// <summary>
    /// [freeze sweep] Background variant of <see cref="Touch"/>. The MRU
    /// read-modify-write (File.ReadAllText + File.WriteAllText) used to run on
    /// the UI thread on every file open/save; under OneDrive / AV latency that
    /// stalled the editor right as a save completed. This offloads the whole
    /// read-modify-write to the thread pool, serialized by <see cref="_ioGate"/>
    /// so concurrent open/save bursts can't interleave writes. Losing an MRU
    /// update on a crash is non-fatal — same best-effort posture as the
    /// synchronous path.
    /// </summary>
    public static void TouchDeferred(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return;
        // [S10 P2] The _ioGate acquisition now lives inside Touch() itself, so
        // this path just offloads the (already-gated) read-modify-write to the
        // thread pool. Acquiring the gate here as well would dead-lock against
        // the non-reentrant SemaphoreSlim that Touch() re-acquires on the pool
        // thread.
        _ = AsyncErrorBoundary.SafeRunAsync(
            () => System.Threading.Tasks.Task.Run(() => Touch(absolutePath)),
            "Architect.RecentFiles", "TouchDeferred");
    }

    public static void Remove(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return;
        // [P1 swarm-audit] Acquire _ioGate around the Load → RemoveAll → Save
        // for the same reason Touch() does (lines 48-70): a removal racing a
        // concurrent Touch()/TouchDeferred() on another window could Load stale
        // state and Save it back, silently dropping the Touch's MRU update (or
        // corrupting recent-files.json under OneDrive / AV latency). The gate is
        // the same semaphore both Touch paths use, so all three now serialise.
        _ioGate.Wait();
        try
        {
            var list = Load();
            if (list.RemoveAll(s => string.Equals(s, absolutePath, StringComparison.OrdinalIgnoreCase)) > 0)
                Save(list);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>
    /// 0.10.0 UX P2 — wipe the entire MRU in a single disk write. Pre-P2
    /// the "Clear list" button on RecentFilesDialog iterated each entry
    /// through <see cref="Remove"/>, producing 10 sequential serialise +
    /// File.WriteAllText cycles for a guaranteed-empty list. The single-
    /// write form skips that overhead and also leaves a deterministic
    /// "no recent files" file on disk instead of churning through the
    /// length.
    /// </summary>
    public static void Clear() => Save(new List<string>());

    // ── Pin / unpin (0.10.0 UX P2) ─────────────────────────────────────
    //
    // Pinned entries persist at the top of the MRU and survive being
    // bumped out by the 10-entry cap. Storage lives in a sibling
    // .pinned.json alongside recent-files.json so the existing
    // (path-only) MRU file stays a flat List<string> — no migration.

    private const string PinnedFileName = "recent-files.pinned.json";

    // [P1 swarm-audit 2026-05-29] Serialises the pinned-list load-modify-save
    // sequence in SetPinned. Pre-fix two concurrent SetPinned calls (e.g. two
    // sibling Architect windows toggling pins) could both LoadPinned the same
    // on-disk state, mutate independent copies, and the second File.WriteAllText
    // would clobber the first writer's change (TOCTOU between LoadPinned and the
    // write).
    private static readonly object s_pinnedLock = new();

    private static string ResolvePinnedPath()
    {
        try { return Path.Combine(Paths.RoamingAppData("Architect"), PinnedFileName); }
        catch { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PinnedFileName); }
    }

    /// <summary>
    /// Set of currently-pinned absolute paths. Case-insensitive matching
    /// against the MRU list. Returns an empty set when the pinned file
    /// doesn't exist or fails to parse.
    /// </summary>
    public static HashSet<string> LoadPinned()
    {
        try
        {
            var p = ResolvePinnedPath();
            if (!File.Exists(p)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var raw  = File.ReadAllText(p);
            var list = JsonSerializer.Deserialize<List<string>>(raw) ?? new List<string>();
            return new HashSet<string>(list.Where(s => !string.IsNullOrWhiteSpace(s)),
                StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static bool IsPinned(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return false;
        // [P1 swarm-audit] Read pinned state under s_pinnedLock so this read
        // can't observe a torn/stale snapshot while SetPinned() is mid
        // load-modify-save under the same lock (lines 167-191).
        lock (s_pinnedLock)
        {
            return LoadPinned().Contains(absolutePath);
        }
    }

    public static void SetPinned(string absolutePath, bool pinned)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return;
        // [P1 swarm-audit 2026-05-29] Hold s_pinnedLock across the whole
        // load-modify-save so concurrent toggles can't clobber each other.
        lock (s_pinnedLock)
        {
            var set = LoadPinned();
            bool changed = pinned ? set.Add(absolutePath) : set.Remove(absolutePath);
            if (!changed) return;
            try
            {
                var p = ResolvePinnedPath();
                var dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir!);
                File.WriteAllText(p, JsonSerializer.Serialize(set.ToList(),
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // best effort — losing the pin state is non-fatal
            }
        }
    }

    /// <summary>
    /// MRU + pinned merged into one ordered list: pinned entries first
    /// (in pinned order), then the regular MRU (with pinned entries
    /// de-duped out). Capped at <see cref="Capacity"/>. Used by the
    /// Recent Files dialog so a pinned entry still surfaces even after
    /// 10 newer files pushed it out of the MRU window.
    /// </summary>
    public static List<string> LoadMerged()
    {
        var pinned = LoadPinned();
        var mru    = Load();
        var merged = new List<string>();
        foreach (var p in pinned) merged.Add(p);
        foreach (var m in mru)
            if (!pinned.Contains(m)) merged.Add(m);
        return merged.Take(Capacity).ToList();
    }

    private static void Save(List<string> list)
    {
        try
        {
            var p = ResolvePath();
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir!);
            File.WriteAllText(p, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // best effort — losing MRU is non-fatal
        }
    }
}
