using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Architect.WinUI.Services;

/// <summary>
///  Persistent record of the previously-open sibling Architect
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
/// Hub-side wire-up lives in : <c>MainWindow.Loaded</c>
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

    // [P1 swarm-audit 2026-05-29] Serialises the Load → mutate → Save sequence
    // in Touch. Pre-fix two concurrent Touch calls (sibling windows opening /
    // focusing in parallel) could both Load the same on-disk list, mutate
    // independent copies, and the second Save would clobber the first writer's
    // change (TOCTOU between Load and Save).
    private static readonly object s_ioLock = new();

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
            // [P1 swarm-audit 2026-05-29] Hold s_ioLock across the whole
            // Load → mutate → Save so concurrent Touch calls can't clobber
            // each other's MRU update.
            lock (s_ioLock)
            {
                var list = Load();
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
                var capped = list
                    .OrderByDescending(e => e.LastOpenUtc)
                    .Take(MaxEntries)
                    .ToList();
                Save(capped);
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
            var list = Load();
            int before = list.Count;
            list.RemoveAll(e =>
                string.Equals(e.Path, absolutePath, StringComparison.OrdinalIgnoreCase));
            if (list.Count != before) Save(list);
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
            Save(list);
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
    /// best-effort.
    /// </summary>
    public static List<Entry> Load()
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

    private static void Save(List<Entry> list)
    {
        string path = ResolvePath();
        string json = JsonSerializer.Serialize(list, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        //  Atomic temp+replace so a concurrent
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
