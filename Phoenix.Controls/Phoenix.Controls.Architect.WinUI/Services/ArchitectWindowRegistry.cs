using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Architect.WinUI.Hosting;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Architect.WinUI.Services;

/// <summary>
/// Process-wide registry of open Architect sibling-graph windows, keyed
/// by absolute .phxg path (case-insensitive on Windows). Implements the
/// "exactly one Architect window per .phxg, focus the existing one if
/// already open" rule for multi-window restoration. Untitled "new"
/// windows are tracked under
/// <see cref="UntitledKeyPrefix"/>-{guid} so multiple New invocations
/// each get a separate sibling.
///
/// Pre-T15 the equivalent live behind <c>new MainForm(initialFile).Show()</c>
/// — pre-T15 ALSO allowed multiple opens of the same file (each spawned a
/// fresh form). The 0.10.0 spec narrows that to "exactly one per .phxg"
/// so users don't accidentally end up editing the same graph in two
/// windows with divergent in-memory state.
/// </summary>
public static class ArchitectWindowRegistry
{
    private const string UntitledKeyPrefix = "__untitled:";

    // ConcurrentDictionary so any future dispatcher-hop callers
    // (e.g. bus / IPC opens off the UI thread) don't race on the
    // process-wide sibling-window registry.
    private static readonly ConcurrentDictionary<string, ArchitectSiblingWindow> s_open
        = new(StringComparer.OrdinalIgnoreCase);

    // Per-path async-load reservation set. The dedup contract
    // ("exactly one Architect window per .phxg") was racy after the
    // sync→async conversion: OpenFileAsync registered the window only AFTER
    // the 100–1000ms LoadGraphAsync completed, so two concurrent opens for
    // the same path both saw s_open.TryGetValue == false and each built a
    // window. We close the window by reserving the key the instant we decide
    // to create a window — BEFORE awaiting the load. A concurrent caller that
    // arrives while a load is in flight finds the key already reserved and
    // backs off (returns null) rather than spawning a duplicate. The reserve
    // + s_open check happens under a lock so the check-then-reserve sequence
    // is atomic even when callers hop threads. The reservation is cleared in
    // a finally so a failed load doesn't permanently block re-opening the
    // path.
    private static readonly object s_reserveGate = new();
    private static readonly HashSet<string> s_loading
        = new(StringComparer.OrdinalIgnoreCase);

    // Serializes the multi-step read-then-modify sequences over
    // s_open (FindKey → TryRemove(oldKey) → s_open[newKey] in Rebind; the
    // FindKey + TryRemove pair in Unregister). ConcurrentDictionary makes each
    // individual op atomic, but Rebind's "find old key, then swap to a new key"
    // is several ops; without this gate a concurrent Unregister/Register can
    // interleave and leave the registry inconsistent (old key still mapped, or
    // two keys pointing at one window). Register/Unregister/FindKey are all
    // invoked under this lock so the compound updates stay consistent.
    private static readonly object s_registryLock = new();

    /// <summary>
    /// Open <paramref name="absolutePath"/> in a sibling Architect
    /// window. If a window for that path is already open, brings it to
    /// the front and returns it. Returns null on load failure (the
    /// underlying ArchitectViewModel.OpenAsync already logs the cause).
    ///
    /// Was synchronous OpenFile; converted to await the async
    /// load path so the deadlock-prone sync shim on ArchitectViewModel
    /// could be removed. Sync void call sites (drag-drop handlers, recent
    /// menu) wrap this in AsyncErrorBoundary.SafeRunAsync.
    /// </summary>
    public static async System.Threading.Tasks.Task<ArchitectSiblingWindow?> OpenFileAsync(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            throw new ArgumentException("Path required", nameof(absolutePath));

        string key = NormalizeKey(absolutePath);

        // Atomic check-then-reserve. Under the gate we (a) re-focus a
        // live existing window, (b) evict a dead one, or (c) reserve the key
        // for THIS call so a concurrent open of the same path during the async
        // load backs off instead of building a duplicate window.
        ArchitectSiblingWindow? existing = null;
        lock (s_reserveGate)
        {
            if (s_open.TryGetValue(key, out existing))
            {
                // handled outside the lock (Activate() can throw on a dead HWND
                // and we don't want to hold the gate across that).
            }
            else if (s_loading.Contains(key))
            {
                // Another OpenFileAsync is mid-load for this exact path. Honour
                // the one-window contract by backing off — the first call will
                // register + activate the window. Logged, not modal.
                GlobalLogger.Log(
                    $"ArchitectWindowRegistry: '{absolutePath}' is already opening in another sibling window; skipping duplicate.",
                    "ArchitectWindowRegistry", LogLevel.Debug);
                return null;
            }
            else
            {
                // Reserve the key before we release the gate + await the load.
                s_loading.Add(key);
            }
        }

        if (existing is not null)
        {
            // Validate liveness: if the cached window
            // is dead (its OnClosed→Unregister never ran, or its native window
            // was torn down out-of-band), Activate() throws on the destroyed
            // HWND. Pre-fix that throw was swallowed and a dead reference
            // returned, so re-opening the file silently did nothing. Now we
            // evict the zombie and fall through to recreate a fresh window.
            try { existing.Activate(); return existing; }
            catch
            {
                s_open.TryRemove(key, out _);
                // Dead window evicted — claim the load reservation and rebuild.
                lock (s_reserveGate)
                {
                    if (s_loading.Contains(key))
                    {
                        // A concurrent caller already grabbed the reservation
                        // while we were evicting; let it own the recreate.
                        return null;
                    }
                    s_loading.Add(key);
                }
            }
        }

        try
        {
            var window = new ArchitectSiblingWindow();
            try
            {
                if (!await window.LoadGraphAsync(absolutePath).ConfigureAwait(true))
                {
                    // Load failed; close the empty shell so we don't pollute
                    // the registry. ArchitectViewModel.OpenAsync already logged
                    // the cause via GraphSerializer.OnLoadFailed.
                    try { window.Close(); } catch { /* best-effort */ }
                    return null;
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("ArchitectWindowRegistry",
                    $"OpenFileAsync '{absolutePath}'", ex);
                try { window.Close(); } catch { /* best-effort */ }
                return null;
            }

            Register(window, key);
            try { window.Activate(); } catch { /* best-effort */ }
            return window;
        }
        finally
        {
            // Always release the reservation — a failed/cancelled load must not
            // permanently block re-opening the path.
            lock (s_reserveGate) { s_loading.Remove(key); }
        }
    }

    /// <summary>
    /// Open a fresh, unsaved graph in a new sibling window. Each call
    /// spawns a distinct window — Untitled siblings are not deduped
    /// because there's no identity to coalesce on until the user saves.
    /// After Save / Save As the window's key migrates to the new path
    /// via <see cref="Rebind"/>.
    /// </summary>
    public static ArchitectSiblingWindow OpenNew()
    {
        var window = new ArchitectSiblingWindow();
        string key = UntitledKeyPrefix + Guid.NewGuid().ToString("N");
        Register(window, key);
        try { window.Activate(); } catch { /* best-effort */ }
        return window;
    }

    /// <summary>
    /// Migrate a window's registry key. Sibling window calls this after
    /// a Save As / first Save so subsequent OpenFile(samepath) calls
    /// re-focus this window instead of spawning a duplicate.
    /// </summary>
    public static void Rebind(ArchitectSiblingWindow window, string? newAbsolutePath)
    {
        if (window is null) return;

        // Hold s_registryLock across the whole find-old-key →
        // check-clash → swap-to-new-key sequence so a concurrent
        // Unregister/Register can't interleave mid-rebind and leave the old key
        // still mapped (stale-reference refocus) or two keys on one window.
        lock (s_registryLock)
        {
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
                    $"ArchitectWindowRegistry: refusing to rebind — '{newAbsolutePath}' is already open in another sibling window.",
                    "ArchitectWindowRegistry", LogLevel.System);
                return;
            }

            if (oldKey is not null) s_open.TryRemove(oldKey, out _);
            s_open[newKey] = window;
        }
    }

    /// <summary>
    /// Bring the sibling
    /// window that owns <paramref name="vm"/> to the front. Used by
    /// SubGraphWindow's "Reveal call-site" so clicking the chrome button
    /// in a macro/process editor focuses the parent .phxg's window
    /// before the canvas frame + flash runs. No-op when no sibling owns
    /// that AVM (the call originated from MainView's embedded canvas in
    /// Hub, which isn't a sibling window — in that case the parent
    /// canvas's RevealNodeFromShell still does the visual work; only
    /// the bring-to-front is skipped).
    /// </summary>
    public static void ActivateOwnerOf(Phoenix.Controls.Architect.WinUI.ViewModels.ArchitectViewModel vm)
    {
        if (vm is null) return;
        try
        {
            // Iterate under s_registryLock so a concurrent Register /
            // Unregister / Rebind can't mutate s_open mid-foreach (which would
            // throw InvalidOperationException). Matches the write-side locking in
            // Rebind/Unregister/Register. The inner Activate() can throw on a dead
            // HWND but is already wrapped, so holding the gate across it is safe.
            lock (s_registryLock)
            {
                foreach (var kvp in s_open)
                {
                    if (ReferenceEquals(kvp.Value.GetViewModelForRegistry(), vm))
                    {
                        try { kvp.Value.Activate(); } catch { /* best-effort */ }
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"ArchitectWindowRegistry.ActivateOwnerOf failed: {ex.Message}",
                "ArchitectWindowRegistry", LogLevel.Debug);
        }
    }

    /// <summary>
    /// Internal — sibling window calls this from its Closed handler so a
    /// fresh OpenFile of the same path spawns a new window instead of
    /// trying to activate a corpse.
    /// </summary>
    internal static void Unregister(ArchitectSiblingWindow window)
    {
        if (window is null) return;
        // Hold s_registryLock across find-then-remove so this can't
        // race a concurrent Rebind's compound key swap.
        lock (s_registryLock)
        {
            string? key = FindKey(window);
            if (key is not null) s_open.TryRemove(key, out _);
        }
    }

    private static void Register(ArchitectSiblingWindow window, string key)
    {
        // Single op, but taken under s_registryLock so it serializes
        // against Rebind/Unregister's compound updates to s_open.
        lock (s_registryLock)
        {
            s_open[key] = window;
        }
    }

    private static string? FindKey(ArchitectSiblingWindow window)
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
