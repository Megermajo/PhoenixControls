using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Visualist.WinUI.Services;

/// <summary>
/// Pillar-local helper that enumerates the Hub media library
/// (<c>data/media/</c>) and resolves references to media files across all
/// <c>.phxlayer</c> files on disk plus an optional in-memory layer (the
/// currently-loaded document, which may carry unsaved edits).
///
/// <para>
/// Pre-0.9.0 Visualist shipped a media-library panel with delete affordance
/// (CHANGELOG 0.6.4). The 0.9.0 WinUI redesign dropped the surface. This
/// reintroduces the delete flow via <see cref="Dialogs.MediaLibraryDialog"/>
/// — this service is the data-side plumbing.
/// </para>
///
/// <para>
/// Reference scanning walks each layer's widgets → triggers → graph nodes
/// and inspects the <c>Path</c> attribute on <c>Image.Load</c>,
/// <c>Video.Load</c>, and <c>Audio.Load</c> nodes (the only widget-graph
/// node templates that take a media path — see
/// <c>Phoenix.Controls.Visualist.Engine.Core.NodeTemplates</c>). Attribute
/// values round-trip as JSON-quoted strings (e.g. <c>"foo/bar.png"</c>) so
/// the comparison strips a single set of outer double quotes when present.
/// </para>
///
/// <para>
/// This service lives in <c>Phoenix.Controls.Visualist.WinUI</c> rather than
/// Shared — the media library is owned by Visualist's design-time concerns
/// even though the files happen to live under Hub's <c>data/</c> tree.
/// </para>
/// </summary>
public static class MediaLibrary
{
    /// <summary>Node templates whose <c>Path</c> attribute references a media file.</summary>
    private static readonly string[] s_mediaLoaderNodeTitles =
    {
        "Image.Load",
        "Video.Load",
        "Audio.Load",
    };

    /// <summary>The single attribute key those loader nodes use.</summary>
    private const string PathAttributeKey = "Path";

    /// <summary>One entry per file under <see cref="MediaRoot"/>.</summary>
    public sealed record MediaItem(
        string FileName,
        string RelativePath,
        string FullPath,
        long   SizeBytes);

    /// <summary>The on-disk root the dialog enumerates and the deleter operates on.</summary>
    public static string MediaRoot => Paths.HubMedia;

    /// <summary>
    /// Enumerates every file under <see cref="MediaRoot"/> (recursive) and
    /// returns a flat list ordered by relative path. Returns an empty list
    /// (without throwing) when the root is missing — first-run scenario.
    /// </summary>
    public static IReadOnlyList<MediaItem> Enumerate()
    {
        string root = MediaRoot;
        if (!Directory.Exists(root)) return Array.Empty<MediaItem>();

        var results = new List<MediaItem>();
        foreach (var full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            // Skip dotfiles like .gitkeep / .DS_Store — they aren't user media
            // and showing them as deletable entries is misleading.
            string name = Path.GetFileName(full);
            if (name.StartsWith('.')) continue;

            long size;
            try { size = new FileInfo(full).Length; }
            catch { size = 0; }

            string rel = Path.GetRelativePath(root, full).Replace('\\', '/');
            results.Add(new MediaItem(name, rel, full, size));
        }

        results.Sort(static (a, b) =>
            string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    /// <summary>
    /// Scan every <c>.phxlayer</c> under <see cref="Paths.HubLayers"/> plus
    /// the optional in-memory <paramref name="liveLayer"/> for references to
    /// <paramref name="item"/>. Match is case-insensitive on the relative
    /// path the loader-node's <c>Path</c> attribute carries.
    ///
    /// <para>
    /// Returns the human-readable list of locations that hold a reference —
    /// e.g. <c>"alerts.phxlayer → AlertBox.onStartup"</c>. An empty list
    /// means the file is unreferenced and safe to delete.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> FindReferences(MediaItem item, Layer? liveLayer, string? liveLayerFileName)
    {
        if (item is null) return Array.Empty<string>();

        var refs = new List<string>();

        // Walk on-disk layers first.
        string layersRoot = Paths.HubLayers;
        if (Directory.Exists(layersRoot))
        {
            foreach (var path in Directory.EnumerateFiles(layersRoot, "*.phxlayer", SearchOption.TopDirectoryOnly))
            {
                Layer? layer;
                try { layer = LayerSerializer.Read(path); }
                catch
                {
                    // A malformed .phxlayer shouldn't block the delete
                    // workflow — log via the caller (we keep this service
                    // logger-free so it stays testable).
                    continue;
                }

                CollectReferences(layer, Path.GetFileName(path), item.RelativePath, refs);
            }
        }

        // Then the in-memory document if its path differs from any on-disk
        // file we just scanned. The doc's saved twin may be stale, so the
        // in-memory copy is authoritative for the currently-loaded layer.
        if (liveLayer is not null)
        {
            string liveName = string.IsNullOrEmpty(liveLayerFileName) ? "(unsaved layer)" : liveLayerFileName;

            // Drop the on-disk hit for the same file — the in-memory copy
            // supersedes it for the active document.
            if (!string.IsNullOrEmpty(liveLayerFileName))
                refs.RemoveAll(s => s.StartsWith(liveLayerFileName + " ", StringComparison.OrdinalIgnoreCase));

            CollectReferences(liveLayer, liveName, item.RelativePath, refs);
        }

        return refs;
    }

    private static void CollectReferences(Layer layer, string layerLabel, string relativePath, List<string> sink)
    {
        if (layer.Widgets is null) return;

        foreach (var widget in layer.Widgets)
        {
            if (widget.Triggers is null) continue;

            foreach (var trigger in widget.Triggers)
            {
                if (trigger.Graph?.Nodes is null) continue;

                foreach (var node in trigger.Graph.Nodes)
                {
                    if (!IsMediaLoaderNode(node.Title)) continue;
                    if (!node.Attributes.TryGetValue(PathAttributeKey, out var raw)) continue;

                    string candidate = UnquoteAttribute(raw);
                    if (string.IsNullOrEmpty(candidate)) continue;

                    if (PathsEqual(candidate, relativePath))
                    {
                        string widgetLabel = string.IsNullOrEmpty(widget.Name) ? "(unnamed widget)" : widget.Name;
                        sink.Add($"{layerLabel} → {widgetLabel}.{trigger.Name}");
                    }
                }
            }
        }
    }

    private static bool IsMediaLoaderNode(string title)
    {
        foreach (var t in s_mediaLoaderNodeTitles)
        {
            if (string.Equals(title, t, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Attribute values from the registry are stored JSON-quoted (e.g.
    /// <c>"foo/bar.png"</c>) so the runtime parser can treat them as
    /// expressions. The media-loader nodes only ever take a string literal
    /// here — strip a single matching pair of outer quotes so the comparison
    /// sees the bare relative path. Backslashes are normalised to forward
    /// slashes to match <see cref="MediaItem.RelativePath"/>.
    /// </summary>
    private static string UnquoteAttribute(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        string s = raw.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            s = s.Substring(1, s.Length - 2);
        return s.Replace('\\', '/');
    }

    private static bool PathsEqual(string a, string b)
    {
        // Strip a leading "./" or "/" for either side — both are valid in
        // authored attribute values but the canonical relative path has no
        // leader.
        return string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);

        static string Normalise(string s)
        {
            s = s.Replace('\\', '/').Trim();
            if (s.StartsWith("./", StringComparison.Ordinal)) s = s.Substring(2);
            while (s.StartsWith('/')) s = s.Substring(1);
            return s;
        }
    }

    /// <summary>
    /// Delete <paramref name="item"/> from disk. Returns <c>true</c> on
    /// success, <c>false</c> when the file no longer exists (already gone —
    /// treated as success-with-warning by the caller). Throws on any other
    /// IO failure so the dialog can surface the exception via
    /// <see cref="GlobalLogger.Error(string, string, Exception)"/>.
    /// </summary>
    public static bool DeleteFromDisk(MediaItem item)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        if (!File.Exists(item.FullPath)) return false;

        // Capture the cache key BEFORE deleting — it's derived from mtime, which
        // is gone once the file is unlinked. (Thumbnail-cache cleanup gotcha:
        // skipping this orphans the .png under thumbcache/ forever.)
        string? cachePath = TryGetCachePath(item.FullPath);

        File.Delete(item.FullPath);

        if (cachePath is not null)
        {
            try { if (File.Exists(cachePath)) File.Delete(cachePath); }
            catch { /* orphan cleanup is best-effort — never block the delete */ }
        }
        return true;
    }

    // ─── Disk-backed thumbnail cache ────────────────────────────────
    //
    // Pre-0.9.0 Visualist cached decoded thumbnails on disk so reopening the
    // media picker / docked panel didn't re-decode every image. The WinUI port
    // dropped it, so each reload paid a full decode pass. This reintroduces a
    // LocalAppData/Visualist/thumbcache/ store keyed by SHA-256(fullpath + "|" +
    // mtime.Ticks) → decoded PNG bytes. The key folds the modified-time so an
    // edited file (same path, new mtime) misses the stale entry and re-decodes.
    //
    // The decode itself (source bytes → PNG) is owned by the WinUI consumers
    // (they hold the BitmapImage/encoder types); this service is the byte store:
    // it hands out cached bytes, accepts freshly-decoded bytes to store, and
    // cleans up on delete. Callers fall back to a live decode on a cache miss
    // and then call StoreThumbnail to populate the cache for next time.

    private const string ThumbCacheSubdir = "Visualist/thumbcache";

    /// <summary>The LocalAppData folder PNG thumbnails are cached under.</summary>
    public static string ThumbCacheRoot => Paths.LocalAppData(ThumbCacheSubdir);

    /// <summary>
    /// Derive the cache file path for a media file: SHA-256 of
    /// <c>fullPath|mtime.Ticks</c> → <c>{ThumbCacheRoot}/{hash}.png</c>. Returns
    /// <c>null</c> when the source no longer exists (mtime unobtainable).
    /// </summary>
    public static string? TryGetCachePath(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return null;
        DateTime mtimeUtc;
        try
        {
            if (!File.Exists(fullPath)) return null;
            mtimeUtc = File.GetLastWriteTimeUtc(fullPath);
        }
        catch { return null; }

        string keySource = $"{Path.GetFullPath(fullPath)}|{mtimeUtc.Ticks}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(keySource));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash) sb.Append(b.ToString("x2"));
        return Path.Combine(ThumbCacheRoot, sb.ToString() + ".png");
    }

    /// <summary>
    /// Synchronous cache lookup — returns the cached PNG bytes for
    /// <paramref name="fullPath"/> if a fresh (path + mtime) entry exists, else
    /// <c>null</c>. Never throws: a corrupt / unreadable cache entry returns
    /// <c>null</c> so the caller falls back to a live decode.
    /// </summary>
    public static byte[]? TryGetCachedThumbnail(string fullPath)
    {
        string? cachePath = TryGetCachePath(fullPath);
        if (cachePath is null) return null;
        try
        {
            return File.Exists(cachePath) ? File.ReadAllBytes(cachePath) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Returns cached PNG bytes for <paramref name="fullPath"/>, or <c>null</c>
    /// on a cache miss (caller then live-decodes and calls
    /// <see cref="StoreThumbnail"/>). Async wrapper over the disk read so the
    /// caller can await it uniformly alongside its decode path.
    /// </summary>
    public static async Task<byte[]?> GetThumbnailAsync(string fullPath, CancellationToken ct = default)
    {
        string? cachePath = TryGetCachePath(fullPath);
        if (cachePath is null) return null;
        try
        {
            if (!File.Exists(cachePath)) return null;
            using var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            using var ms = new MemoryStream();
            await fs.CopyToAsync(ms, ct).ConfigureAwait(false);
            return ms.ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// Persist freshly-decoded PNG <paramref name="pngBytes"/> for
    /// <paramref name="fullPath"/> into the disk cache. Best-effort: any IO
    /// failure (read-only volume, race) is swallowed — the cache is a pure
    /// optimisation and must never break the calling UI flow.
    /// </summary>
    public static void StoreThumbnail(string fullPath, byte[] pngBytes)
    {
        if (pngBytes is null || pngBytes.Length == 0) return;
        string? cachePath = TryGetCachePath(fullPath);
        if (cachePath is null) return;
        try
        {
            Directory.CreateDirectory(ThumbCacheRoot);
            // Write to a temp sibling then move into place so a concurrent reader
            // never sees a half-written PNG.
            string tmp = cachePath + ".tmp";
            File.WriteAllBytes(tmp, pngBytes);
            File.Move(tmp, cachePath, overwrite: true);
        }
        catch { /* best-effort cache write */ }
    }

    // ─── Unified import + kind classification ──────────────────────
    //
    // Pre-fix there were TWO divergent import paths: MediaPickerDialog flat-copied
    // into the media root (no subfolder, no sanitize), while MainView.ImportMedia
    // routed into images/videos/audio with sanitization. Same library, two
    // layouts. This is the single convention all callers (docked panel, node
    // picker, File→Import Media) route through.

    public enum Kind { Image, Video, Audio, Other }

    private static readonly string[] s_imageExt = { ".png", ".webp", ".jpg", ".jpeg", ".gif", ".bmp" };
    private static readonly string[] s_videoExt = { ".webm", ".mp4", ".mov", ".m4v" };
    private static readonly string[] s_audioExt = { ".mp3", ".wav", ".ogg", ".m4a", ".flac" };

    /// <summary>Coarse media classification from the file extension.</summary>
    public static Kind ClassifyKind(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (Array.IndexOf(s_imageExt, ext) >= 0) return Kind.Image;
        if (Array.IndexOf(s_videoExt, ext) >= 0) return Kind.Video;
        if (Array.IndexOf(s_audioExt, ext) >= 0) return Kind.Audio;
        return Kind.Other;
    }

    /// <summary>The (label, ext) pairs a media file picker should offer, image-first.</summary>
    public static IReadOnlyList<(string Label, string Ext)> PickerExtensions()
    {
        var list = new List<(string, string)>();
        foreach (var e in s_imageExt) list.Add(("Image", e));
        foreach (var e in s_videoExt) list.Add(("Video", e));
        foreach (var e in s_audioExt) list.Add(("Audio", e));
        return list;
    }

    /// <summary>
    /// Copy <paramref name="sourcePath"/> into the media root under its kind
    /// subfolder (images/videos/audio), sanitizing the name and resolving
    /// collisions with a <c>_2</c>/<c>_3</c> suffix. Returns the forward-slash
    /// relative path, or <c>null</c> for an unsupported type / missing source.
    /// </summary>
    public static string? Import(string sourcePath)
    {
        if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) return null;
        string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        string? sub = ClassifyKind(sourcePath) switch
        {
            Kind.Image => "images",
            Kind.Video => "videos",
            Kind.Audio => "audio",
            _          => null,
        };
        if (sub is null) return null;

        string root = MediaRoot;
        string targetDir = Path.Combine(root, sub);
        Directory.CreateDirectory(targetDir);

        string sanitized = SanitizeName(Path.GetFileNameWithoutExtension(sourcePath));
        if (string.IsNullOrEmpty(sanitized)) sanitized = "media";

        string candidate = Path.Combine(targetDir, sanitized + ext);
        int n = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(targetDir, $"{sanitized}_{n}{ext}");
            n++;
        }
        File.Copy(sourcePath, candidate, overwrite: false);
        return Path.GetRelativePath(Path.GetFullPath(root), candidate).Replace('\\', '/');
    }

    private static string SanitizeName(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name.ToLowerInvariant())
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-')
                sb.Append(c);
            else if (c == ' ')
                sb.Append('_');
        }
        return sb.ToString().Trim('.', '-', '_');
    }
}
