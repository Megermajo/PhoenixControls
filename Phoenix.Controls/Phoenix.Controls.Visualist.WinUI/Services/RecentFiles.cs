using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Phoenix.Controls.Shared.Core;

namespace Phoenix.Controls.Visualist.WinUI.Services;

/// <summary>
/// 10-deep MRU of .phxlayer files the user has opened, persisted at
/// %AppData%/PhoenixControls/Visualist/recent-files.json. Falls back to the
/// process working directory when AppData isn't writable.
///
/// Parallel of <c>Phoenix.Controls.Architect.WinUI.Services.RecentFiles</c>
/// — kept per-pillar deliberately (each pillar owns its own MRU file)
/// rather than lifting to Shared. The two are ~70 LOC of JSON-list
/// scaffolding; duplicating beats coupling the pillars through a
/// shared service for what is genuinely per-pillar metadata.
/// </summary>
public static class RecentFiles
{
    private const int Capacity = 10;
    private const string FileName = "recent-files.json";

    private static string ResolvePath()
    {
        try { return Path.Combine(Paths.RoamingAppData("Visualist"), FileName); }
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

    /// <summary>
    /// Opt-in MRU hygiene pass: drop entries whose backing file no longer
    /// exists on disk, persist the cleaned list, and return the survivors.
    ///
    /// Deliberately NOT folded into <see cref="Load"/> — <c>Load</c> stays a
    /// lightweight read with no I/O beyond the JSON file (it is called on
    /// every recent-menu rebuild and dialog reload), per the service's design
    /// intent. Callers that want a proactive cleanup (app startup, the Recent
    /// Files dialog reload, the chrome menu rebuild) invoke <c>Prune</c>
    /// explicitly so the stale-link cost is paid once, not on every read.
    ///
    /// <see cref="File.Exists"/> is a stat — safe and cheap for a ≤10-entry
    /// MRU. Returns the pruned list so a caller can bind it directly without a
    /// second <c>Load</c>. Best-effort: any failure leaves the persisted list
    /// untouched and returns whatever <c>Load</c> yielded.
    /// </summary>
    public static List<string> Prune()
    {
        var list = Load();
        if (list.Count == 0) return list;

        var alive = list.Where(File.Exists).ToList();

        // Only rewrite the file when something actually changed — avoids
        // churning the MRU file (and its mtime) on every startup when all
        // entries are still valid.
        if (alive.Count != list.Count)
            Save(alive);

        return alive;
    }

    public static void Touch(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return;
        var list = Load();
        list.RemoveAll(s => string.Equals(s, absolutePath, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, absolutePath);
        if (list.Count > Capacity) list = list.Take(Capacity).ToList();
        Save(list);
    }

    public static void Remove(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return;
        var list = Load();
        if (list.RemoveAll(s => string.Equals(s, absolutePath, StringComparison.OrdinalIgnoreCase)) > 0)
            Save(list);
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
