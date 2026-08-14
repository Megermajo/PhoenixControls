using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Shared.Services
{
    /// <summary>
    /// DPAPI-based at-rest protection for the ten secret fields on
    /// <see cref="AppConfig"/>, which are — in declaration order —
    /// <c>DiscordBotToken</c>, <c>OpenAIApiKey</c>, <c>AnthropicKey</c>,
    /// <c>CerebrasApiKey</c>, <c>TranslationApiKey</c>, <c>WebhookSecret</c>,
    /// <c>StreamerBotPassword</c>, <c>WebSocketServerToken</c>,
    /// <c>ObsWebSocketPassword</c> and <c>YouTubeDataApiKey</c>. The list is an
    /// enumerated inventory and goes stale silently — a new
    /// <c>[JsonConverter(typeof(DpapiProtectedStringConverter))]</c> on AppConfig
    /// belongs here too.
    ///
    /// <para>
    /// Why DPAPI: Majo's <c>%AppData%</c> lives inside a OneDrive-synced folder.
    /// Storing API keys / passwords plaintext in <c>config.json</c> there means
    /// every credential is cloud-replicated in clear. <see cref="ProtectedData"/>
    /// with <see cref="DataProtectionScope.CurrentUser"/> ties the ciphertext to
    /// the Windows user profile — an attacker who exfiltrates a synced copy of
    /// config.json from another machine cannot decrypt it, and a cloud-side
    /// breach exposes only ciphertext.
    /// </para>
    ///
    /// <para>
    /// On-disk format: <c>dpapi:v1:&lt;base64 of encrypted bytes&gt;</c>. The
    /// versioned prefix is what lets us tell encrypted from plaintext at read
    /// time — a value missing the prefix is treated as legacy plaintext (so
    /// pre-DPAPI config.json files load without manual intervention) and gets
    /// re-encrypted on the next save.
    /// </para>
    ///
    /// <para>
    /// Failure mode: if DPAPI decryption raises (corrupted bytes, value
    /// originally encrypted under a different user profile, etc.) we log a
    /// CriticalError and return the ciphertext as-is rather than throwing.
    /// Throwing would crash <see cref="ConfigManager.Load"/> on every startup;
    /// returning the ciphertext keeps Hub bootable and lets the user re-enter
    /// the secret in Settings.
    /// </para>
    /// </summary>
    public static class DpapiSecretStore
    {
        /// <summary>
        /// Legacy prefix — values wrapped with <c>optionalEntropy: null</c>.
        /// Still recognised by <see cref="Unprotect"/> for backwards
        /// compatibility; <see cref="Protect"/> always emits the v2 prefix.
        /// </summary>
        public const string Prefix = "dpapi:v1:";

        /// <summary>
        /// Current prefix — values wrapped with the app-scoped entropy
        /// below. New writes use this; v1 values are migrated on the next
        /// save through the round-trip.
        /// </summary>
        public const string PrefixV2 = "dpapi:v2:";

        // App-scoped entropy. Pre-fix the wrap call passed
        // optionalEntropy: null, which means any same-user process —
        // browser extension via native messaging, opportunistic malware,
        // drive-by RAT — can ProtectedData.Unprotect the on-disk ciphertext
        // by knowing only DataProtectionScope.CurrentUser. Mixing a baked-in
        // app salt into the entropy ties the unwrap to "this is the
        // Phoenix Controls suite" so casual same-user decryption needs to
        // also know the salt. Salt itself is not a secret (it ships in the
        // binary) — the defense-in-depth is that an attacker now needs both
        // the user-profile DPAPI key AND knowledge of this constant.
        //
        // Compatibility: v1 ciphertexts (null entropy) keep unwrapping
        // cleanly because Unprotect picks the entropy off the prefix. The
        // first Save after this lift round-trips them through Protect and
        // they land as v2 on disk — no manual migration step.
        private static readonly byte[] s_appEntropy =
            SHA256.HashData(Encoding.UTF8.GetBytes(
                "phoenix-controls/dpapi-secret-store/v1"));

        /// <summary>
        /// Wraps <paramref name="plaintext"/> with DPAPI under the current user
        /// scope and returns <c>dpapi:v2:&lt;base64&gt;</c>. Null / empty
        /// strings pass through unchanged so empty-secret defaults (the common
        /// fresh-install state) stay empty on disk rather than ballooning into
        /// a 50-byte ciphertext.
        ///
        /// Values that already carry the v1 or v2 prefix are returned
        /// unchanged — protects against accidental double-encryption when a
        /// caller round-trips the on-disk form through the getter+setter.
        /// Unprefixed v1 ciphertexts get re-wrapped as v2 on the next save:
        /// callers reach them through Unprotect first, which strips the
        /// prefix, then Protect re-wraps with the current scheme.
        /// </summary>
        public static string Protect(string? plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) return plaintext ?? string.Empty;
            if (plaintext.StartsWith(PrefixV2, StringComparison.Ordinal)) return plaintext;
            if (plaintext.StartsWith(Prefix,   StringComparison.Ordinal)) return plaintext;

            if (!OperatingSystem.IsWindows())
            {
                // DPAPI is Windows-only. Non-Windows is not a supported runtime
                // platform for the suite, but keep the test harness portable
                // by passing through unchanged. The Windows pillars will
                // re-encrypt on first save when they read it back.
                return plaintext;
            }

            try
            {
                byte[] data    = Encoding.UTF8.GetBytes(plaintext);
                byte[] cipher  = ProtectedData.Protect(data, s_appEntropy,
                                                      DataProtectionScope.CurrentUser);
                return PrefixV2 + Convert.ToBase64String(cipher);
            }
            catch (Exception ex)
            {
                // Don't crash config-save over a DPAPI hiccup. The plaintext
                // value flows through unchanged so the user keeps the secret;
                // they get a logged warning that at-rest protection failed.
                GlobalLogger.Log(
                    $"DpapiSecretStore.Protect failed: {ex.Message} — value stored as plaintext.",
                    "DpapiSecretStore", LogLevel.CriticalError);
                return plaintext;
            }
        }

        /// <summary>
        /// Inverse of <see cref="Protect"/>. A value lacking either prefix is
        /// treated as legacy plaintext and returned unchanged (the migration
        /// path: next save re-wraps it). v1 values unwrap with null entropy
        /// for backwards compatibility with legacy config.json on
        /// disk; v2 values unwrap with the app-scoped entropy. A value with
        /// a prefix whose body fails to decrypt is logged at CriticalError
        /// and returned as-is — see class docs for the rationale.
        /// </summary>
        public static string Unprotect(string? ciphertext)
        {
            if (string.IsNullOrEmpty(ciphertext)) return ciphertext ?? string.Empty;

            byte[]? entropy;
            string body;
            if (ciphertext.StartsWith(PrefixV2, StringComparison.Ordinal))
            {
                entropy = s_appEntropy;
                body    = ciphertext.Substring(PrefixV2.Length);
            }
            else if (ciphertext.StartsWith(Prefix, StringComparison.Ordinal))
            {
                // v1 legacy — null entropy. The next save re-wraps as v2.
                entropy = null;
                body    = ciphertext.Substring(Prefix.Length);
            }
            else
            {
                // Legacy plaintext from a pre-DPAPI config.json. The
                // ConfigManager migration path counts these on Load() and
                // logs a System-tier "migrated N secrets" line; here we just
                // pass the value through so the in-memory string is plaintext.
                return ciphertext;
            }

            if (!OperatingSystem.IsWindows())
            {
                // Non-Windows test harness — can't decrypt, but the prefix
                // implies a Windows-produced value. Strip the prefix so the
                // caller doesn't see ciphertext as the plaintext value.
                GlobalLogger.Log(
                    "DpapiSecretStore.Unprotect called on non-Windows; returning empty.",
                    "DpapiSecretStore", LogLevel.System);
                return string.Empty;
            }

            try
            {
                byte[] cipher = Convert.FromBase64String(body);
                byte[] data   = ProtectedData.Unprotect(cipher, entropy,
                                                       DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch (FormatException ex)
            {
                GlobalLogger.Log(
                    $"DpapiSecretStore.Unprotect: malformed base64 in DPAPI-wrapped value: {ex.Message}.",
                    "DpapiSecretStore", LogLevel.CriticalError);
                return ciphertext;
            }
            catch (CryptographicException ex)
            {
                // Most common cause: config.json was copied across user
                // profiles (e.g. OneDrive sync between two PCs). The user
                // needs to re-enter the secret in Settings; until they do,
                // return ciphertext as-is so we don't crash on every read.
                GlobalLogger.Log(
                    $"DpapiSecretStore.Unprotect failed (wrong user profile or corrupt ciphertext): {ex.Message}. " +
                    "The secret must be re-entered in Settings.",
                    "DpapiSecretStore", LogLevel.CriticalError);
                return ciphertext;
            }
            catch (Exception ex)
            {
                GlobalLogger.Log(
                    $"DpapiSecretStore.Unprotect unexpected failure: {ex.Message}.",
                    "DpapiSecretStore", LogLevel.CriticalError);
                return ciphertext;
            }
        }

        /// <summary>
        /// Returns true when <paramref name="value"/> is in the DPAPI-wrapped
        /// on-disk form. Used by <see cref="ConfigManager"/> to count how many
        /// legacy-plaintext secrets it migrated on Load() so the System-tier
        /// log entry can carry an accurate count.
        /// </summary>
        public static bool IsProtected(string? value)
            => !string.IsNullOrEmpty(value)
               && (value.StartsWith(PrefixV2, StringComparison.Ordinal)
                   || value.StartsWith(Prefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// System.Text.Json converter that round-trips an <see cref="AppConfig"/>
    /// secret field through DPAPI: in-memory the property is plaintext (every
    /// existing consumer — <see cref="WS"/>, HttpTranslator, ScriptManager.AI,
    /// SettingsDialog — stays untouched), on-disk it is wrapped via
    /// <see cref="DpapiSecretStore.Protect"/>.
    ///
    /// <para>Read path (file → memory): the JSON string is passed through
    /// <see cref="DpapiSecretStore.Unprotect"/>. Legacy plaintext values pass
    /// through unchanged — they will be re-wrapped on the next save.</para>
    ///
    /// <para>Write path (memory → file): the plaintext value is passed through
    /// <see cref="DpapiSecretStore.Protect"/>. The empty string short-circuits
    /// so a fresh-install config.json keeps human-readable empty-string
    /// defaults for the nine secret fields.</para>
    /// </summary>
    public sealed class DpapiProtectedStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // STJ surfaces both string and null tokens here for string?
            // properties; collapsing null to empty matches AppConfig's
            // empty-default convention.
            if (reader.TokenType == JsonTokenType.Null) return string.Empty;
            string? raw = reader.GetString();
            return DpapiSecretStore.Unprotect(raw);
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(DpapiSecretStore.Protect(value));
        }
    }
}
