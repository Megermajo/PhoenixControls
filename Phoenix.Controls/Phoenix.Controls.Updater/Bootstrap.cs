using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Phoenix.Controls.Updater;

/// <summary>
/// Self-relocation guard. In the installer / Releases layout the Updater ships
/// at <c>&lt;installRoot&gt;\Updater\Phoenix.Controls.Updater.exe</c> — i.e.
/// INSIDE the very directory the swap renames aside
/// (<see cref="UpdateRunner.ApplyArchiveSwap"/> does
/// <c>Directory.Move(installRoot, installRoot.bak)</c>). Windows refuses that
/// rename while our own process holds the tree open two ways:
///
///   • the spawning Hub launches us with <c>WorkingDirectory = installRoot</c>,
///     so the tree is our current directory (a process CWD cannot be renamed);
///   • our running <c>.exe</c> image is memory-mapped from inside the tree.
///
/// So the swap always threw right after the "sentinel … is gone" line and the
/// console vanished. The fix mirrors every real self-updater (Squirrel /
/// Velopack): before doing anything, copy our own folder to <c>%TEMP%</c> and
/// re-exec from there. The temp copy sits OUTSIDE the tree, waits for the
/// in-tree parent to exit, then renames freely.
///
/// BCL-only, like the rest of the Updater — this runs before
/// <see cref="UpdateRunner"/> is even constructed.
/// </summary>
internal static class UpdaterBootstrap
{
    /// <summary>
    /// If we are running from inside the install root we are about to swap,
    /// copy this folder to <c>%TEMP%</c> and re-exec the copy with
    /// <c>--detached --parent-pid &lt;us&gt;</c>. Returns <c>true</c> when a
    /// temp copy was spawned — the caller MUST return <paramref name="exitCode"/>
    /// from <c>Main</c> immediately so our CWD + image locks release. Returns
    /// <c>false</c> when no relocation is needed (running from outside the tree)
    /// OR the relocation attempt failed — in both cases the caller falls through
    /// and runs the flow in place (the historical best-effort behaviour).
    /// </summary>
    internal static bool TryRelocateToTemp(UpdaterArgs args, string[] originalArgs, out int exitCode)
    {
        exitCode = 0;

        string rootFull;
        string baseDir;
        try
        {
            string installRoot = ResolveEffectiveInstallRoot(args);
            if (string.IsNullOrEmpty(installRoot)) return false;
            rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
            baseDir  = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        }
        catch { return false; }

        // Only relocate when our own exe dir is the install root itself or a
        // descendant of it. A dev flat-bin that lives elsewhere, or a copy
        // already staged in %TEMP%, stays put.
        if (!PathIsBaseOrUnder(baseDir, rootFull)) return false;

        try
        {
            string tempRoot = Path.Combine(
                Path.GetTempPath(),
                "PhoenixControls.Updater." + Guid.NewGuid().ToString("N").Substring(0, 8));

            TrySweepOldTempDirs();               // tidy prior relocations, best-effort
            CopyDir(baseDir, tempRoot);

            string tempExe = Path.Combine(tempRoot, "Phoenix.Controls.Updater.exe");
            if (!File.Exists(tempExe))
            {
                TryLog($"relocation aborted: temp copy is missing the exe ({tempExe}); running in place (may fail).");
                return false;
            }

            var newArgs = new List<string>(originalArgs);
            // Guarantee the temp copy swaps the ORIGINAL tree, not its own temp
            // location. Every parsed mode requires an explicit root on argv, so
            // this injection is defensive (a directly constructed UpdaterArgs
            // could omit it) — the temp copy would otherwise infer the root
            // from its %TEMP% base dir and swap the wrong thing.
            if (string.IsNullOrEmpty(args.InstallRoot))
            {
                newArgs.Add("--target");
                newArgs.Add(rootFull);
            }
            newArgs.Add("--detached");
            newArgs.Add("--parent-pid");
            newArgs.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

            var psi = new ProcessStartInfo(tempExe)
            {
                UseShellExecute  = false,
                CreateNoWindow   = false,   // keep the visible progress console
                WorkingDirectory = tempRoot, // OUTSIDE the install tree
            };
            foreach (string a in newArgs) psi.ArgumentList.Add(a);

            using Process? child = Process.Start(psi);
            TryLog($"relocated to temp and re-exec'd (childPid={child?.Id.ToString(CultureInfo.InvariantCulture) ?? "?"}, temp={tempRoot}, target={rootFull}). Base dir was inside the install tree.");
            return true;
        }
        catch (Exception ex)
        {
            TryLog($"relocation failed ({ex.Message}); running in place (may fail).");
            return false;
        }
    }

    /// <summary>
    /// Detached-copy startup: wait for the in-tree parent to exit so its
    /// mapped image (inside the tree we are about to rename) is released, then
    /// let a short settle pass. No-op when not relocated.
    /// </summary>
    internal static void OnDetachedStartup(UpdaterArgs args)
    {
        if (args.ParentPid <= 0) return;
        try
        {
            using var p = Process.GetProcessById(args.ParentPid);
            TryLog($"detached copy waiting up to 10s for in-tree parent pid {args.ParentPid} to exit.");
            if (!p.WaitForExit(10_000))
                TryLog($"parent pid {args.ParentPid} still alive after 10s; proceeding anyway.");
            else
                TryLog($"parent pid {args.ParentPid} exited; proceeding.");
        }
        catch (ArgumentException) { /* parent already gone — fine */ }
        catch (Exception ex) { TryLog($"parent-exit wait error: {ex.Message}"); }

        // Give the OS a beat to drop the parent's image-section handle on the
        // in-tree Updater.exe before we start renaming the tree.
        try { Thread.Sleep(300); } catch { }
    }

    /// <summary>
    /// The install root the runner will swap: an explicit
    /// <c>--install-root/--target/--repo-root</c> wins, otherwise mirror
    /// <see cref="UpdateRunner"/>'s inference — parent of our base dir when we
    /// sit in an <c>Updater\</c> folder, else the base dir itself.
    /// </summary>
    internal static string ResolveEffectiveInstallRoot(UpdaterArgs args)
    {
        if (!string.IsNullOrEmpty(args.InstallRoot)) return args.InstallRoot!;

        string baseDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var di = new DirectoryInfo(baseDir);
        if (string.Equals(di.Name, "Updater", StringComparison.OrdinalIgnoreCase) && di.Parent is not null)
            return di.Parent.FullName;
        return baseDir;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> equals <paramref name="root"/> or
    /// is nested beneath it. Case-insensitive, separator-normalised, and guarded
    /// against the prefix-collision trap (<c>C:\Foo</c> vs <c>C:\FooBar</c>) via
    /// the trailing-separator compare.
    /// </summary>
    internal static bool PathIsBaseOrUnder(string candidate, string root)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(root)) return false;
        string c, r;
        try
        {
            c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch { return false; }

        if (string.Equals(c, r, StringComparison.OrdinalIgnoreCase)) return true;
        return c.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        foreach (string file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(dst, Path.GetRelativePath(src, file));
            string? targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TrySweepOldTempDirs()
    {
        try
        {
            DateTime cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
            foreach (string dir in Directory.EnumerateDirectories(Path.GetTempPath(), "PhoenixControls.Updater.*"))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                        Directory.Delete(dir, recursive: true);
                }
                catch { /* in-use or gone — skip */ }
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Best-effort console + roaming-log line. The bootstrap runs before
    /// <see cref="UpdateRunner"/> exists, so it writes straight to the same
    /// <c>%AppData%/PhoenixControls/Hub/updater.log</c> the runner appends to,
    /// keeping the relocation visible in the one diagnostics file.
    /// </summary>
    private static void TryLog(string line)
    {
        string stamped = $"[{DateTime.UtcNow:HH:mm:ss.fff}Z] [bootstrap] {line}";
        try { Console.WriteLine(stamped); } catch { }
        try
        {
            string stateDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                UpdateRunner.BrandFolder, "Hub");
            Directory.CreateDirectory(stateDir);
            File.AppendAllText(Path.Combine(stateDir, "updater.log"), stamped + Environment.NewLine);
        }
        catch { /* never throw out of logging */ }
    }
}
