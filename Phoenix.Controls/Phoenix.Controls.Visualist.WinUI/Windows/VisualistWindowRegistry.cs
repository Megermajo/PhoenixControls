using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

// Namespace deliberately uses `Hosting` rather than `Windows` to avoid
// shadowing the `Windows.*` system namespace inside the Visualist.WinUI
// assembly (the Canvas / Controls layers reference Windows.Storage by
// short name; nesting these files under …WinUI.Windows would make
// `Windows.Storage` resolve to the Visualist sub-namespace first and
// fail to compile). Mirrors Architect.WinUI/Windows/ → Hosting layout.
namespace Phoenix.Controls.Visualist.WinUI.Hosting;

/// <summary>
/// Visualist multi-window
/// registry. Tracks every open Visualist sibling window so File → Open /
/// drag-drop / recent-files can dedupe by absolute .phxlayer path
/// (case-insensitive on Windows): a second open of the same file focuses
/// the existing window instead of spawning a duplicate.
///
/// Shape mirrors <c>Architect.WinUI/Services/ArchitectWindowRegistry.cs</c>;
/// each
/// pillar owns its own copy of the pattern rather than sharing code, so
/// Visualist's live preview / layer keying needs can evolve independently
/// of Architect's graph keying.
///
/// Untitled (not-yet-saved) windows are tracked under
/// <see cref="UntitledKeyPrefix"/>-{guid} so multiple New invocations each
/// get a separate sibling. After Save / Save As the window's key migrates
/// to the absolute path via <see cref="Rebind"/>.
/// </summary>
public static class VisualistWindowRegistry
{
    private const string UntitledKeyPrefix = "__untitled:";

    // Concurrent so future dispatcher-hop callers (bus / IPC opens off the
    // UI thread, drag-drop handoffs) don't race on the process-wide
    // registry. Matches the Architect-side concurrency posture.
    private static readonly ConcurrentDictionary<string, VisualistSiblingWindow> s_open
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Open a fresh, unsaved Visualist sibling window. Each call spawns a
    /// distinct window because Visualist documents aren't keyed by an
    /// absolute path until the user saves — the registry tracks each
    /// window under a synthetic GUID so the future Save-As path can
    /// rebind to a path key without rewriting the registry contract.
    ///
    /// The returned window is already <c>Activate()</c>d; callers don't
    /// need to touch its lifecycle beyond the optional close path. The
    /// window auto-unregisters on Closed.
    /// </summary>
    public static Task<VisualistSiblingWindow> OpenNewWindowAsync()
    {
        var window = new VisualistSiblingWindow();
        string key = UntitledKeyPrefix + Guid.NewGuid().ToString("N");
        Register(window, key);

        try { window.Activate(); }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"VisualistWindowRegistry: Activate failed for new sibling window: {ex.Message}",
                "VisualistWindowRegistry", LogLevel.Debug);
        }

        return Task.FromResult(window);
    }

    /// <summary>
    /// Open <paramref name="absolutePath"/> in a sibling Visualist window.
    /// If a window for that path is already open, brings it to the front
    /// and returns it. Returns null on load failure (the underlying
    /// <c>VisualistViewModel.OpenLayer</c> already logs the cause).
    ///
    /// Mirrors Architect's <c>OpenFileAsync</c> — the await is to keep the
    /// API uniform with future async LoadLayer paths (today's
    /// VisualistViewModel.OpenLayer is synchronous, but the sync-over-async
    /// problem doesn't bite because LayerSerializer reads are file-only and
    /// the disk I/O is brief).
    /// </summary>
    public static Task<VisualistSiblingWindow?> OpenFileAsync(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            throw new ArgumentException("Path required", nameof(absolutePath));

        string key = NormalizeKey(absolutePath);

        if (s_open.TryGetValue(key, out var existing))
        {
            try { existing.Activate(); } catch { /* best-effort */ }
            return Task.FromResult<VisualistSiblingWindow?>(existing);
        }

        var window = new VisualistSiblingWindow();
        try
        {
            if (!window.LoadLayer(absolutePath))
            {
                // Load failed — close the empty shell so we don't pollute
                // the registry.
                try { window.Close(); } catch { /* best-effort */ }
                return Task.FromResult<VisualistSiblingWindow?>(null);
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("VisualistWindowRegistry",
                $"OpenFileAsync '{absolutePath}'", ex);
            try { window.Close(); } catch { /* best-effort */ }
            return Task.FromResult<VisualistSiblingWindow?>(null);
        }

        Register(window, key);
        try { window.Activate(); } catch { /* best-effort */ }
        return Task.FromResult<VisualistSiblingWindow?>(window);
    }

    /// <summary>
    /// Migrate a window's registry key. Sibling windows call this after a
    /// Save As / first Save so subsequent <see cref="OpenFileAsync"/> calls
    /// with the same path re-focus this window instead of spawning a
    /// duplicate. Pass <c>null</c> to rebind back to an untitled key (e.g.
    /// on a future "discard path" flow).
    /// </summary>
    public static void Rebind(VisualistSiblingWindow window, string? newAbsolutePath)
    {
        if (window is null) return;

        string? oldKey = FindKey(window);
        string newKey = string.IsNullOrWhiteSpace(newAbsolutePath)
            ? UntitledKeyPrefix + Guid.NewGuid().ToString("N")
            : NormalizeKey(newAbsolutePath!);

        // If another window already owns the new key, we'd be creating a
        // duplicate. Log and skip the rebind — the caller (Save As path)
        // can decide whether to surface this.
        if (s_open.TryGetValue(newKey, out var clash) && !ReferenceEquals(clash, window))
        {
            GlobalLogger.Log(
                $"VisualistWindowRegistry: refusing to rebind — '{newAbsolutePath}' is already open in another sibling window.",
                "VisualistWindowRegistry", LogLevel.System);
            return;
        }

        if (oldKey is not null) s_open.TryRemove(oldKey, out _);
        s_open[newKey] = window;
    }

    /// <summary>
    /// Look up the sibling window currently bound to
    /// <paramref name="absolutePath"/>, if any. Returns null when no
    /// sibling has the file open — callers use this to decide whether to
    /// spawn a fresh sibling or activate the existing one.
    /// </summary>
    public static VisualistSiblingWindow? FindByPath(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return null;
        string key = NormalizeKey(absolutePath);
        return s_open.TryGetValue(key, out var w) ? w : null;
    }

    /// <summary>
    /// Internal — sibling window calls this from its Closed handler so a
    /// fresh OpenFile of the same path spawns a new window instead of
    /// trying to activate a corpse.
    /// </summary>
    internal static void Unregister(VisualistSiblingWindow window)
    {
        if (window is null) return;
        string? key = FindKey(window);
        if (key is not null) s_open.TryRemove(key, out _);
    }

    /// <summary>Snapshot of currently open Visualist sibling windows. Primarily for diagnostic purposes.</summary>
    public static IReadOnlyCollection<VisualistSiblingWindow> Snapshot()
    {
        // ConcurrentDictionary.Values returns a snapshot under the hood;
        // wrap in a fresh list so callers can't accidentally mutate the
        // backing store via an upcast to ICollection<T>.
        return new List<VisualistSiblingWindow>(s_open.Values);
    }

    /// <summary>Returns the count of currently open Visualist sibling windows.</summary>
    public static int Count => s_open.Count;

    private static void Register(VisualistSiblingWindow window, string key)
    {
        s_open[key] = window;
        // Auto-unregister so a re-open after close doesn't grow the
        // registry. The Closed handler is the only path that touches
        // s_open from the window's own lifecycle — every other mutation
        // goes through Register / Rebind / Unregister.
        window.Closed += (_, _) =>
        {
            try { s_open.TryRemove(key, out _); } catch { /* best-effort */ }
        };
    }

    private static string? FindKey(VisualistSiblingWindow window)
    {
        foreach (var kvp in s_open)
        {
            if (ReferenceEquals(kvp.Value, window)) return kvp.Key;
        }
        return null;
    }

    private static string NormalizeKey(string absolutePath)
    {
        try { return Path.GetFullPath(absolutePath); }
        catch { return absolutePath; }
    }
}
