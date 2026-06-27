using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phoenix.Controls.Updater;

/// <summary>
/// <c>.phxupdate</c> manifest model. The archive is a plain zip file with a
/// top-level <c>manifest.json</c> entry; the rest of the entries are the
/// suite tree (<c>Hub/</c>, <c>Architect/</c>, <c>Visualist/</c>, etc.) the
/// runner extracts into the install root.
///
/// Schema (per <c>extras.jsx §11</c>):
/// <code>
/// {
///   "version":     "0.7.0",                       // semver of the release
///   "releaseTag":  "release/0.7.0",                // matches the GitHub tag
///   "createdUtc":  "2026-05-05T12:00:00Z",
///   "files": [
///     { "path": "Hub/Phoenix.Controls.Hub.exe", "sha256": "abc..." },
///     { "path": "Architect/Phoenix.Controls.Architect.exe", "sha256": "def..." },
///     ...
///   ],
///   "minHostVersion": "0.6.0"                       // optional
/// }
/// </code>
///
/// The manifest's per-file SHAs let the runner verify each extracted file
/// matches what the release pipeline produced — defence-in-depth on top of
/// the archive-wide SHA-256 the caller already verified before invoking
/// <c>Updater.exe --update</c>.
///
/// Forward-compatibility: <see cref="LoadFromArchive"/> returns null when the
/// archive lacks a manifest (legacy <c>.zip</c> with a <c>phoenix-controls/</c>
/// root) — the runner falls back to a manifest-free extraction in that case.
/// </summary>
public sealed class UpdateManifest
{
    [JsonPropertyName("version")]        public string  Version       { get; init; } = "";
    [JsonPropertyName("releaseTag")]     public string? ReleaseTag    { get; init; }
    [JsonPropertyName("createdUtc")]     public string? CreatedUtc    { get; init; }
    [JsonPropertyName("files")]          public List<ManifestFile> Files { get; init; } = new();
    [JsonPropertyName("minHostVersion")] public string? MinHostVersion { get; init; }

    /// <summary>
    /// Reads <c>manifest.json</c> from the root of <paramref name="archivePath"/>
    /// (or any subfolder; the first match wins). Returns null when the archive
    /// has no manifest — legacy <c>.zip</c> distributions land here.
    /// </summary>
    public static UpdateManifest? LoadFromArchive(string archivePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            ZipArchiveEntry? entry = null;
            foreach (var e in zip.Entries)
            {
                // Match either "manifest.json" at the root, or
                // "<anything>/manifest.json" exactly one level deep.
                string name = e.FullName.Replace('\\', '/');
                if (string.Equals(name, "manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    entry = e; break;
                }
                if (name.IndexOf('/') == name.LastIndexOf('/')
                    && name.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    entry = e; // keep looking for a root one, but accept this as fallback
                }
            }
            if (entry is null) return null;

            using Stream stream = entry.Open();
            return JsonSerializer.Deserialize<UpdateManifest>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch
        {
            // Swallow — runner treats null as "no manifest, fall through".
            // Errors are surfaced through the runner's log via the caller.
            return null;
        }
    }

    /// <summary>
    /// Verifies that every file in <see cref="Files"/> exists at
    /// <paramref name="installRoot"/> with the manifest's expected SHA-256.
    /// Returns the list of mismatched / missing entries; an empty list means
    /// the install matches the manifest exactly.
    /// </summary>
    public List<string> VerifyInstall(string installRoot)
    {
        var problems = new List<string>(0);
        // QC08-02: anchor + normalise installRoot once so every manifest entry
        // can be checked against it for traversal. The read-only verify path
        // doesn't write files, but a future selective-extract refactor would
        // inherit any laxity here.
        string rootFull = Path.GetFullPath(installRoot);
        string rootFullWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        foreach (ManifestFile mf in Files)
        {
            if (string.IsNullOrWhiteSpace(mf.Path) || string.IsNullOrWhiteSpace(mf.Sha256))
            {
                problems.Add($"manifest entry missing path/sha256: '{mf.Path}'");
                continue;
            }

            // QC08-02: reject absolute paths / drive specifiers / traversal up-front.
            string relative = mf.Path.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relative)
                || relative.Contains("..", StringComparison.Ordinal))
            {
                problems.Add($"unsafe path rejected: {mf.Path}");
                continue;
            }

            string full;
            try { full = Path.GetFullPath(Path.Combine(rootFull, relative)); }
            catch (Exception ex)
            {
                problems.Add($"path resolve failed: {mf.Path}: {ex.Message}");
                continue;
            }

            if (!full.StartsWith(rootFullWithSep, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"path escapes install root: {mf.Path}");
                continue;
            }

            if (!File.Exists(full))
            {
                problems.Add($"missing: {mf.Path}");
                continue;
            }

            string actual;
            try { actual = ComputeSha256(full); }
            catch (Exception ex)
            {
                problems.Add($"could not hash {mf.Path}: {ex.Message}");
                continue;
            }

            // QC08-05: constant-time hex compare. Mismatched-length hashes are
            // an immediate fail anyway, but FixedTimeEquals removes the timing
            // signal entirely.
            byte[] actualBytes = HexToBytes(actual);
            byte[] expectedBytes = HexToBytes(mf.Sha256);
            if (actualBytes.Length != expectedBytes.Length
                || !CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes))
            {
                problems.Add($"sha256 mismatch: {mf.Path} (expected {mf.Sha256}, got {actual})");
            }
        }
        return problems;
    }

    private static byte[] HexToBytes(string hex)
    {
        if (string.IsNullOrEmpty(hex) || (hex.Length & 1) != 0) return Array.Empty<byte>();
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out bytes[i])) return Array.Empty<byte>();
        }
        return bytes;
    }

    private static string ComputeSha256(string path)
    {
        using FileStream fs = File.OpenRead(path);
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed class ManifestFile
{
    [JsonPropertyName("path")]   public string Path   { get; init; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
}
