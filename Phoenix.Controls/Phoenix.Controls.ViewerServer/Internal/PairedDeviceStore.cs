using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phoenix.Controls.ViewerServer.Internal;

/// <summary>
/// PBKDF2-hashed bearer token store, persisted as JSON next to the Hub's
/// other per-user state under <c>%LOCALAPPDATA%/PhoenixControls/Hub/</c>.
///
/// Tokens themselves never touch disk — only the salt + hash. Mirrors the
/// scheme described in <c>Viewer_Roadmap.md</c> § "Auth & pairing", just
/// JSON-backed instead of in the SQLite databank (a DB schema
/// bump can fold this in later without changing the public surface here).
/// </summary>
internal sealed class PairedDeviceStore
{
    private const int SaltLen = 16;
    private const int HashLen = 32;
    private const int Iterations = 200_000;

    // Token rotation policy:
    //   • Absolute max age — 30 days from IssuedAtUtc.
    //   • Idle max         — 7 days since LastUsedUtc.
    // Either limit firing rejects the token and forces a re-pair. The
    // device row stays in the store (revoked-style) so audit lists still
    // show it; a fresh pair issues a new id+token.
    internal static readonly TimeSpan AbsoluteMaxAge = TimeSpan.FromDays(30);
    internal static readonly TimeSpan IdleMaxAge     = TimeSpan.FromDays(7);

    // Verification cache: PBKDF2-SHA256 at 200_000 iterations
    // costs ~20-50ms per call on a modern desktop CPU. Once a token has
    // verified against a specific device we cache the SHA-256 of
    // (deviceId || ':' || token) along with the matched deviceId, so
    // subsequent requests skip the PBKDF2 cost and instead do one
    // constant-time SHA-256 comparison. TTL keeps the cache small and
    // guarantees revocations / expirations take effect within the
    // window even on a hot path.
    private static readonly TimeSpan VerifyCacheTtl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, VerifyCacheEntry> _verifyCache = new();

    private readonly string _path;
    private readonly object _persistLock = new();
    private readonly ConcurrentDictionary<string, Entry> _byDeviceId = new();

    // Guards the mutable PairedDevice fields
    // (LastSeen / LastUsedUtc / Revoked). The ConcurrentDictionary makes the
    // map thread-safe, but the device objects it holds were mutated + read
    // without synchronisation: concurrent Verify calls write LastSeen/
    // LastUsedUtc while another Verify (or Revoke) reads Revoked, a data race
    // on plain reference-type fields. One process-wide lock serialises every
    // device-field read/write so reads observe consistent values and a
    // revocation is never torn against an in-flight LastSeen update.
    private readonly object _deviceStateLock = new();

    public PairedDeviceStore(string path)
    {
        _path = path;
        Load();
    }

    public IReadOnlyList<PairedDevice> List()
    {
        // read Revoked under the device-state
        // lock so the snapshot is consistent against a concurrent Revoke
        // (which writes Revoked under the same lock) — Verify (line 149)
        // and Revoke (line 235) already guard this field.
        lock (_deviceStateLock)
        {
            return _byDeviceId.Values
                .Where(e => !e.Device.Revoked)
                .Select(e => e.Device)
                .ToList();
        }
    }

    /// <summary>Hashes the freshly-minted token and stores the salt+hash for the new device id.</summary>
    public PairedDevice Issue(string deviceLabel, out string token)
    {
        string deviceId = "dev_" + PinManager.NewToken(8);
        Span<byte> tokenBytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(tokenBytes);
        token = Convert.ToBase64String(tokenBytes)
                       .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        byte[] salt = RandomNumberGenerator.GetBytes(SaltLen);
        byte[] hash = HashToken(token, salt);

        var now = DateTimeOffset.UtcNow;
        var device = new PairedDevice
        {
            DeviceId    = deviceId,
            Label       = deviceLabel,
            PairedAt    = now,
            LastSeen    = now,
            IssuedAtUtc = now,
            LastUsedUtc = now,
        };
        _byDeviceId[deviceId] = new Entry(device, salt, hash);
        Persist();
        return device;
    }

    /// <summary>
    /// Look up a device by its bearer token. Constant-time per entry —
    /// can't short-circuit on the first byte mismatch without leaking a
    /// timing oracle for the salt + hash combo.
    /// </summary>
    public PairedDevice? Verify(string token)
    {
        var now = DateTimeOffset.UtcNow;

        // Fast-path: if this exact token was verified
        // recently the SHA-256(token) fingerprint maps to the
        // matching deviceId, skipping the N × PBKDF2 sweep. The
        // fingerprint itself is the constant-time equality proxy —
        // SHA-256 collision resistance means a fingerprint match
        // implies token equality. Cache hits still re-check
        // Revoked + IsExpired against the live device row so
        // revocations and rotations apply within the TTL window.
        string tokenFingerprint = TokenFingerprint(token);
        if (_verifyCache.TryGetValue(tokenFingerprint, out var cached) &&
            cached.ExpiresAt > now &&
            _byDeviceId.TryGetValue(cached.DeviceId, out var cachedEntry))
        {
            // read Revoked + write LastSeen/
            // LastUsedUtc under the device-state lock so a concurrent Revoke
            // can't be torn against this update.
            bool cacheHit = false;
            lock (_deviceStateLock)
            {
                if (!cachedEntry.Device.Revoked)
                {
                    if (IsExpired(cachedEntry.Device, now))
                    {
                        _verifyCache.TryRemove(tokenFingerprint, out _);
                        return null;
                    }
                    cachedEntry.Device.LastSeen    = now;
                    cachedEntry.Device.LastUsedUtc = now;
                    cacheHit = true;
                }
            }
            if (cacheHit) return cachedEntry.Device;
            // not a usable cache hit (revoked) — fall through to the PBKDF2 sweep.
        }

        foreach (var entry in _byDeviceId.Values)
        {
            // guard the Revoked read.
            bool revoked;
            lock (_deviceStateLock) { revoked = entry.Device.Revoked; }
            if (revoked) continue;
            byte[] candidate = HashToken(token, entry.Salt);
            if (CryptographicOperations.FixedTimeEquals(candidate, entry.Hash))
            {
                // expiry check + LastSeen/
                // LastUsedUtc writes under the device-state lock.
                lock (_deviceStateLock)
                {
                    // Rotation policy: reject expired tokens
                    // *after* the constant-time hash check so an attacker
                    // can't distinguish "wrong token" from "expired" by
                    // timing. The check runs unconditionally on the
                    // matched entry only.
                    if (IsExpired(entry.Device, now))
                    {
                        return null;
                    }

                    entry.Device.LastSeen    = now;
                    entry.Device.LastUsedUtc = now;
                }

                // Cache the (fingerprint → deviceId) mapping so
                // future requests with this token skip PBKDF2.
                _verifyCache[tokenFingerprint] = new VerifyCacheEntry(
                    entry.Device.DeviceId,
                    now + VerifyCacheTtl);
                PruneVerifyCache(now);

                return entry.Device;
            }
        }
        return null;
    }

    /// <summary>SHA-256 of the raw token, used as a (non-reversible)
    /// dictionary key for the verify cache. We don't store the token
    /// itself anywhere — only the salted PBKDF2 hash on disk (long-term)
    /// and this in-memory fingerprint (short-term).</summary>
    private static string TokenFingerprint(string token)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(token), hash);
        return Convert.ToHexString(hash);
    }

    private void PruneVerifyCache(DateTimeOffset now)
    {
        // Snapshot before mutating: enumerating a ConcurrentDictionary while
        // calling TryRemove on it has no ordering/visibility guarantee, so an
        // expired entry could be skipped. .ToArray() takes a consistent
        // snapshot of the current pairs to iterate against.
        var snapshot = _verifyCache.ToArray();
        foreach (var kv in snapshot)
        {
            if (kv.Value.ExpiresAt <= now)
                _verifyCache.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>Drop any cached verification for this device. Called
    /// from <see cref="Revoke"/> so revocations take effect immediately,
    /// not after the cache TTL.</summary>
    private void InvalidateVerifyCache(string deviceId)
    {
        // Snapshot before mutating (same reason as PruneVerifyCache): missing
        // an entry here would leave a revoked device with a usable stale
        // fast-path cache entry, so deterministic removal matters for security.
        var snapshot = _verifyCache.ToArray();
        foreach (var kv in snapshot)
        {
            if (kv.Value.DeviceId == deviceId)
                _verifyCache.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>
    /// True if the device's bearer token has exceeded either the
    /// absolute age cap (from <see cref="PairedDevice.IssuedAtUtc"/>) or
    /// the idle cap (from <see cref="PairedDevice.LastUsedUtc"/>).
    /// </summary>
    internal static bool IsExpired(PairedDevice device, DateTimeOffset now)
    {
        if (now - device.IssuedAtUtc > AbsoluteMaxAge) return true;
        if (now - device.LastUsedUtc > IdleMaxAge)     return true;
        return false;
    }

    public bool Revoke(string deviceId)
    {
        if (!_byDeviceId.TryGetValue(deviceId, out var entry)) return false;
        // write Revoked under the device-state
        // lock so a concurrent Verify (reading Revoked / writing LastSeen)
        // observes a consistent value rather than racing this mutation.
        lock (_deviceStateLock)
        {
            entry.Device.Revoked = true;
        }
        // Drop any cached fast-path entry so a revoked
        // device is rejected on the very next Verify call.
        InvalidateVerifyCache(deviceId);
        Persist();
        return true;
    }

    private static byte[] HashToken(string token, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(token, salt, Iterations, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(HashLen);
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            using var fs = File.OpenRead(_path);
            var dto = JsonSerializer.Deserialize<PersistedFile>(fs);
            if (dto?.Devices == null) return;
            foreach (var d in dto.Devices)
            {
                if (string.IsNullOrEmpty(d.DeviceId)) continue;
                // Back-compat: older records did not
                // persist IssuedAtUtc / LastUsedUtc. Treat missing
                // (default) values as "same as PairedAt / LastSeen"
                // so legacy devices keep working until they hit the
                // normal rotation window.
                var issuedAt = d.IssuedAtUtc == default ? d.PairedAt : d.IssuedAtUtc;
                var lastUsed = d.LastUsedUtc == default ? d.LastSeen : d.LastUsedUtc;

                var device = new PairedDevice
                {
                    DeviceId    = d.DeviceId,
                    Label       = d.Label ?? "",
                    PairedAt    = d.PairedAt,
                    LastSeen    = d.LastSeen,
                    Revoked     = d.Revoked,
                    IssuedAtUtc = issuedAt,
                    LastUsedUtc = lastUsed,
                };
                // decode salt/hash per-entry so a
                // single corrupt base64 field skips only that device instead
                // of throwing out of the loop into the bare catch below, which
                // would wipe ALL paired devices and force everyone to re-pair.
                byte[] salt, hash;
                try
                {
                    salt = Convert.FromBase64String(d.SaltBase64 ?? "");
                    hash = Convert.FromBase64String(d.HashBase64 ?? "");
                }
                catch (FormatException)
                {
                    continue;
                }
                _byDeviceId[d.DeviceId] = new Entry(device, salt, hash);
            }
        }
        catch
        {
            // A corrupt store falls back to empty — better to pair again
            // than to refuse to start. The on-disk file is rewritten on
            // the next successful Issue/Revoke.
        }
    }

    private void Persist()
    {
        lock (_persistLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                // snapshot the mutable device
                // fields (LastSeen / Revoked / LastUsedUtc) under the
                // device-state lock so a concurrent Verify/Revoke can't tear
                // a value mid-write into the persisted file.
                List<PersistedDevice> devices;
                lock (_deviceStateLock)
                {
                    devices = _byDeviceId.Values.Select(e => new PersistedDevice
                    {
                        DeviceId    = e.Device.DeviceId,
                        Label       = e.Device.Label,
                        PairedAt    = e.Device.PairedAt,
                        LastSeen    = e.Device.LastSeen,
                        Revoked     = e.Device.Revoked,
                        IssuedAtUtc = e.Device.IssuedAtUtc,
                        LastUsedUtc = e.Device.LastUsedUtc,
                        SaltBase64  = Convert.ToBase64String(e.Salt),
                        HashBase64  = Convert.ToBase64String(e.Hash),
                    }).ToList();
                }
                var dto = new PersistedFile
                {
                    Devices = devices,
                };
                using var fs = File.Create(_path);
                JsonSerializer.Serialize(fs, dto, JsonOpts);
            }
            catch
            {
                // Best-effort: if we can't write, keep the in-memory
                // store correct so the running session keeps working.
                // Next successful write rebuilds the file.
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private sealed record Entry(PairedDevice Device, byte[] Salt, byte[] Hash);

    /// <summary>One row in the verify-cache fast path.
    /// Maps a SHA-256(token) fingerprint to the deviceId it most
    /// recently verified against, with a hard expiry so revocations
    /// and rotations take effect within the TTL even on stale
    /// entries that never get invalidated explicitly.</summary>
    private sealed record VerifyCacheEntry(string DeviceId, DateTimeOffset ExpiresAt);

    private sealed class PersistedFile
    {
        public List<PersistedDevice> Devices { get; set; } = new();
    }

    private sealed class PersistedDevice
    {
        public string DeviceId { get; set; } = "";
        public string? Label { get; set; }
        public DateTimeOffset PairedAt { get; set; }
        public DateTimeOffset LastSeen { get; set; }
        public bool Revoked { get; set; }
        // Added with the rotation policy. `default` on legacy
        // payloads is back-filled from PairedAt / LastSeen during Load().
        public DateTimeOffset IssuedAtUtc { get; set; }
        public DateTimeOffset LastUsedUtc { get; set; }
        public string? SaltBase64 { get; set; }
        public string? HashBase64 { get; set; }
    }
}
