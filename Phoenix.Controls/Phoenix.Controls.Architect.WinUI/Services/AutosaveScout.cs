using System;
using System.Collections.Generic;
using System.IO;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Architect.WinUI.Services;

/// <summary>
/// Boot-time scan for autosave + atomic-save survivors. Catches:
///  - <c>*.phxg.autosave</c> next to a known .phxg whose autosave is newer
///    than the saved file (genuine "you had unsaved work" recovery).
///  - <c>*.phxg.autosave</c> in the untitled-autosave folder (graphs the
///    user was authoring before any manual save).
///  - <c>*.phxg.tmp</c> from <c>GraphSerializer.SaveGraphAsync</c>'s atomic
///    write that didn't complete the rename (crash mid-save).
///
/// Architect UX review P0-3 — pre-fix Architect did neither scan, so a
/// crash mid-save left a stranded .tmp the user had to spot in Explorer
/// and any autosave (which didn't exist) would be similarly invisible.
/// </summary>
public static class AutosaveScout
{
    public enum SurvivorKind { Autosave, AtomicTemp, UntitledAutosave }

    public sealed record Survivor(SurvivorKind Kind, string Path, DateTime LastWriteUtc, string? OriginalPath);

    /// <summary>
    /// Scans the Hub logic folder + untitled autosave folder for survivors.
    /// Non-destructive — just returns what was found; the caller decides
    /// how to surface them (InfoBar with Recover / Discard buttons, etc.).
    /// </summary>
    public static IReadOnlyList<Survivor> Scan()
    {
        var found = new List<Survivor>();

        try
        {
            // Sibling autosaves + atomic-temp stragglers next to .phxg sources.
            var logicDir = Paths.HubLogic;
            if (Directory.Exists(logicDir))
            {
                foreach (var path in Directory.EnumerateFiles(logicDir, "*.phxg" + AutosaveService.AutosaveExtension, SearchOption.AllDirectories))
                {
                    var owner = StripAutosaveSuffix(path);
                    var ownerExists = File.Exists(owner);
                    var autoMtime = File.GetLastWriteTimeUtc(path);
                    // Only surface when the autosave is genuinely newer than the
                    // saved file (or the .phxg never existed).
                    if (!ownerExists || autoMtime > File.GetLastWriteTimeUtc(owner) + TimeSpan.FromSeconds(1))
                    {
                        found.Add(new Survivor(SurvivorKind.Autosave, path, autoMtime,
                            ownerExists ? owner : null));
                    }
                }

                foreach (var path in Directory.EnumerateFiles(logicDir, "*.phxg.tmp", SearchOption.AllDirectories))
                {
                    found.Add(new Survivor(SurvivorKind.AtomicTemp, path,
                        File.GetLastWriteTimeUtc(path),
                        OriginalPath: StripTmpSuffix(path)));
                }
            }

            // Untitled autosaves (the user crashed before naming the graph).
            var untitled = AutosaveService.UntitledAutosaveDir;
            if (Directory.Exists(untitled))
            {
                foreach (var path in Directory.EnumerateFiles(untitled, "*.phxg" + AutosaveService.AutosaveExtension))
                {
                    found.Add(new Survivor(SurvivorKind.UntitledAutosave, path,
                        File.GetLastWriteTimeUtc(path), OriginalPath: null));
                }
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Architect.AutosaveScout", "Scan failed", ex);
        }

        return found;
    }

    private static string StripAutosaveSuffix(string path)
        => path.EndsWith(AutosaveService.AutosaveExtension, StringComparison.OrdinalIgnoreCase)
            ? path.Substring(0, path.Length - AutosaveService.AutosaveExtension.Length)
            : path;

    private static string StripTmpSuffix(string path)
        => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            ? path.Substring(0, path.Length - 4)
            : path;
}
