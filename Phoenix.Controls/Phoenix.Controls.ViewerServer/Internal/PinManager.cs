using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading;

namespace Phoenix.Controls.ViewerServer.Internal;

/// <summary>
/// Generates and rotates the 6-digit pairing PIN shown in Hub UI, plus
/// validates the (pairingId, code, devicePubId) triple a remote viewer
/// presents during <c>POST /v/api/pair/complete</c>.
///
/// The "current PIN" is regenerated each time the previous one expires
/// without being claimed, so a remote viewer that opens the pair page
/// 70 seconds late never sees a stale code.
/// </summary>
internal sealed class PinManager
{
    /// <summary>
    /// Hard cap on outstanding pairing handles across all sources. Pairs with
    /// the per-IP limit in <see cref="PairingRateLimiter"/> as the second
    /// memory bound — even an attacker with thousands of source IPs that
    /// each slip below the per-IP cap cannot make _pending grow without
    /// bound. Each entry expires on its own TTL via PrunePending; this cap
    /// rejects new BeginPairing calls once the dict is full.
    /// </summary>
    internal const int MaxPending = 64;

    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, PendingPairing> _pending = new();
    private readonly object _currentLock = new();
    private string _currentPin = "";
    // Sentinel: "already expired" so the very first GetCurrent() falls
    // through the lock and generates under the mutex — no double-mint
    // even if two threads race in at startup. Keep this in the field
    // declaration (not the ctor) so the ordering is independent of
    // ctor body edits, and don't change it to UtcNow-_ttl: _ttl is a
    // ctor param so a field initializer can't reach it.
    private DateTimeOffset _currentExpiresAt = DateTimeOffset.MinValue;

    public PinManager(TimeSpan ttl) { _ttl = ttl; }

    public (string Pin, DateTimeOffset ExpiresAt) GetCurrent()
    {
        lock (_currentLock)
        {
            if (_currentExpiresAt <= DateTimeOffset.UtcNow)
            {
                _currentPin = GenerateSixDigitCode();
                _currentExpiresAt = DateTimeOffset.UtcNow + _ttl;
            }
            return (_currentPin, _currentExpiresAt);
        }
    }

    /// <summary>
    /// Mints a transient pairing handle. The remote viewer holds the
    /// returned <c>pairingId</c> until the streamer reads the on-screen
    /// PIN; calling <see cref="TryClaim"/> with the matching code
    /// finalizes the pairing.
    /// </summary>
    public (string PairingId, DateTimeOffset ExpiresAt) BeginPairing(string deviceLabel)
    {
        // Prune first so an in-flight expiry frees room before we count.
        PrunePending();
        if (_pending.Count >= MaxPending)
            throw new InvalidOperationException("Pairing handle store at capacity.");

        var (pin, expiresAt) = GetCurrent();
        string pairingId = NewToken(16);
        _pending[pairingId] = new PendingPairing(pin, expiresAt, deviceLabel);
        return (pairingId, expiresAt);
    }

    /// <summary>
    /// Validates the pairing handle, expected PIN, and viewer-supplied
    /// public id. Single-use: the entry is removed on success or
    /// invalidated on failure.
    /// </summary>
    public bool TryClaim(string pairingId, string code, string devicePubId, out string deviceLabel)
    {
        deviceLabel = "";
        if (!_pending.TryRemove(pairingId, out var pending)) return false;
        if (pending.ExpiresAt <= DateTimeOffset.UtcNow) return false;
        if (!FixedTimeEquals(code, pending.Pin)) return false;
        if (string.IsNullOrWhiteSpace(devicePubId)) return false;

        // Burn the current PIN so a claim immediately invalidates the
        // on-screen code — the next caller sees a fresh one.
        lock (_currentLock)
        {
            _currentExpiresAt = DateTimeOffset.MinValue;
        }

        deviceLabel = pending.DeviceLabel;
        return true;
    }

    private void PrunePending()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _pending)
        {
            if (kv.Value.ExpiresAt <= now) _pending.TryRemove(kv.Key, out _);
        }
    }

    private static string GenerateSixDigitCode()
    {
        // RandomNumberGenerator.GetInt32 has uniform distribution; the
        // System.Random alternative would skew the leading digits.
        int n = RandomNumberGenerator.GetInt32(0, 1_000_000);
        return n.ToString("D6");
    }

    internal static string NewToken(int byteLen)
    {
        Span<byte> buf = stackalloc byte[byteLen];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToHexString(buf).ToLowerInvariant();
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    private sealed record PendingPairing(string Pin, DateTimeOffset ExpiresAt, string DeviceLabel);
}
