using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// Viewer-roadmap Slice 0 — pairing + token verification.
    ///
    /// Pairing is a two-step exchange:
    ///   1. <see cref="BeginPairing"/> mints a single-use 6-digit code with a
    ///      configurable TTL (<c>AppConfig.RemotePairingTtlSeconds</c>) and stores
    ///      it in memory. The Hub UI surfaces the code; the Viewer enters it.
    ///   2. <see cref="CompletePairingAsync"/> validates the code, generates a
    ///      32-byte token, derives a PBKDF2-SHA256 hash + 16-byte salt
    ///      (200 000 iterations), and persists <c>(deviceId, label, hash, salt)</c>
    ///      to the <c>PairedDevices</c> table. The token is returned ONCE and
    ///      never re-displayed.
    ///
    /// Token verification (<see cref="VerifyTokenAsync"/>) reads the row, runs the
    /// same PBKDF2 derivation against the candidate token, and constant-time
    /// compares against the stored hash. Revoked rows always fail.
    /// </summary>
    public sealed class RemoteAuthManager
    {
        private const int PbkdfIterations = 200_000;
        private const int SaltBytes       = 16;
        private const int HashBytes       = 32;
        private const int TokenBytes      = 32;
        private const int PairingCodeMin  = 100_000;
        private const int PairingCodeMax  = 999_999;

        private readonly DB _db;
        private readonly IClock _clock;
        private readonly ConcurrentDictionary<string, PairingGrant> _pending = new();

        public RemoteAuthManager() : this(DB.Instance, SystemClock.Instance) { }
        public RemoteAuthManager(DB db) : this(db, SystemClock.Instance) { }

        // IClock injection lets tests advance virtual time
        // past TTL expiry instead of sleeping past it. Production callers use
        // the parameterless ctor and get SystemClock.Instance implicitly.
        public RemoteAuthManager(DB db, IClock clock)
        {
            _db    = db    ?? throw new ArgumentNullException(nameof(db));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        /// <summary>
        /// Mint a fresh pairing grant. Returns a tuple of <c>(pairingId, code, expiresAtUtc)</c>.
        /// The Hub UI shows <paramref name="code"/> to the streamer, who reads it to the
        /// Viewer; the Viewer posts <paramref name="pairingId"/> + code to <c>/pair/complete</c>.
        /// </summary>
        public PairingGrant BeginPairing(int? ttlSecondsOverride = null)
        {
            int ttl = ttlSecondsOverride ?? Math.Max(30, ConfigManager.Current.RemotePairingTtlSeconds);
            string pairingId = NewBase64UrlBytes(12);
            string code      = RandomNumberGenerator.GetInt32(PairingCodeMin, PairingCodeMax + 1)
                                                    .ToString("000000");
            var grant = new PairingGrant
            {
                PairingId  = pairingId,
                Code       = code,
                ExpiresAt  = _clock.UtcNow.AddSeconds(ttl),
                Consumed   = 0,
            };
            _pending[pairingId] = grant;
            // Best-effort sweep — drop any expired grants while we're here so the
            // map stays bounded under happy-path use.
            SweepExpired();
            GlobalLogger.Log(
                $"RemoteAuth: pairing grant minted (id={pairingId}, ttl={ttl}s)",
                "RemoteAuth", LogLevel.System);
            return grant;
        }

        /// <summary>
        /// Validate a pairing-complete request and persist the device. Returns the
        /// freshly-issued token on success, or null on any rejection (expired,
        /// already-consumed, code mismatch, malformed input). The token is shown
        /// once to the Viewer and stored only as a PBKDF2 hash.
        /// </summary>
        public async Task<string?> CompletePairingAsync(string pairingId, string code, string deviceLabel)
        {
            if (string.IsNullOrWhiteSpace(pairingId) || string.IsNullOrWhiteSpace(code))
                return null;

            if (!_pending.TryGetValue(pairingId, out var grant))
            {
                GlobalLogger.Log(
                    $"RemoteAuth: pairing complete rejected — unknown pairing id '{pairingId}'.",
                    "RemoteAuth", LogLevel.CriticalError);
                return null;
            }

            // Single-use: atomic compare-and-set so two racing pair-complete
            // requests against the same id can't both succeed.
            if (Interlocked.CompareExchange(ref grant.Consumed, 1, 0) != 0)
            {
                GlobalLogger.Log(
                    $"RemoteAuth: pairing complete rejected — id '{pairingId}' already consumed.",
                    "RemoteAuth", LogLevel.CriticalError);
                return null;
            }

            // Always evict the grant from the pending map after the CAS — even
            // failures below shouldn't leave a single-use grant available again.
            _pending.TryRemove(pairingId, out _);

            if (_clock.UtcNow > grant.ExpiresAt)
            {
                GlobalLogger.Log(
                    $"RemoteAuth: pairing complete rejected — id '{pairingId}' expired.",
                    "RemoteAuth", LogLevel.CriticalError);
                return null;
            }

            if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(grant.Code),
                Encoding.ASCII.GetBytes(code)))
            {
                GlobalLogger.Log(
                    $"RemoteAuth: pairing complete rejected — code mismatch for id '{pairingId}'.",
                    "RemoteAuth", LogLevel.CriticalError);
                return null;
            }

            string deviceId = "dev_" + NewBase64UrlBytes(8);
            string token    = NewBase64UrlBytes(TokenBytes);
            byte[] salt     = RandomNumberGenerator.GetBytes(SaltBytes);
            byte[] hash     = HashToken(token, salt);

            await _db.UpsertPairedDeviceAsync(deviceId, deviceLabel ?? "", hash, salt).ConfigureAwait(false);

            GlobalLogger.Log(
                $"RemoteAuth: paired device '{deviceLabel}' as id={deviceId}.",
                "RemoteAuth", LogLevel.System);
            return PackTokenForWire(deviceId, token);
        }

        /// <summary>
        /// Validates a bearer token presented as <c>{deviceId}:{tokenBase64Url}</c>
        /// and returns the deviceId on success, or null on any failure. The DB row's
        /// LastSeen is touched on success so the Remote Devices panel can show
        /// recency. Revoked rows always fail.
        /// </summary>
        public async Task<string?> VerifyTokenAsync(string? wireToken)
        {
            if (string.IsNullOrWhiteSpace(wireToken)) return null;
            int sep = wireToken.IndexOf(':');
            if (sep <= 0 || sep >= wireToken.Length - 1) return null;
            string deviceId  = wireToken.Substring(0, sep);
            string tokenPart = wireToken.Substring(sep + 1);

            var row = await _db.GetPairedDeviceAsync(deviceId).ConfigureAwait(false);
            if (row is null || row.Revoked) return null;

            byte[] candidate = HashToken(tokenPart, row.TokenSalt);
            if (!CryptographicOperations.FixedTimeEquals(candidate, row.TokenHash))
                return null;

            // Best-effort touch — failures here shouldn't deny the request.
            try { await _db.TouchPairedDeviceLastSeenAsync(deviceId).ConfigureAwait(false); }
            catch { }
            return deviceId;
        }

        public Task RevokeDeviceAsync(string deviceId) => _db.RevokePairedDeviceAsync(deviceId);

        public Task EnsureSchemaAsync() =>
            Task.WhenAll(_db.EnsurePairedDevicesTableAsync(), _db.EnsureRemoteAuditLogTableAsync());

        private static byte[] HashToken(string token, byte[] salt) =>
            Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(token),
                salt,
                PbkdfIterations,
                HashAlgorithmName.SHA256,
                HashBytes);

        private static string NewBase64UrlBytes(int byteCount)
        {
            byte[] buf = RandomNumberGenerator.GetBytes(byteCount);
            return Convert.ToBase64String(buf)
                          .TrimEnd('=')
                          .Replace('+', '-')
                          .Replace('/', '_');
        }

        private static string PackTokenForWire(string deviceId, string token) => $"{deviceId}:{token}";

        private void SweepExpired()
        {
            DateTime cutoff = _clock.UtcNow;
            foreach (var kv in _pending)
            {
                if (kv.Value.ExpiresAt < cutoff)
                    _pending.TryRemove(kv.Key, out _);
            }
        }
    }

    public sealed class PairingGrant
    {
        public string   PairingId { get; set; } = string.Empty;
        public string   Code      { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
        // 0 = available, 1 = consumed (Interlocked.CompareExchange-driven).
        internal int    Consumed;
    }
}
