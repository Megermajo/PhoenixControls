using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Shared.Services
{
    /// <summary>
    /// ConfigManager — central config loader/saver.
    /// All components read from ConfigManager.Current instead of parsing JSON themselves.
    /// Hub calls Load() at startup; Architect will call Save() when the user edits settings.
    /// </summary>
    public static class ConfigManager
    {
        private static readonly JsonSerializerOptions _writeOpts = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        // Guards the mint+Save sequences in
        // EnsureWebSocketServerToken / RegenerateWebSocketServerToken so two
        // threads can't mint divergent tokens (TOCTOU on Current.WebSocketServerToken).
        // A dedicated synchronous lock — not _deferredWriteGate, which is an async
        // SemaphoreSlim guarding the deferred-write path and would deadlock a
        // synchronous critical section.
        private static readonly object _webSocketTokenGate = new();

        /// <summary>The active application configuration. Always non-null after Load().</summary>
        public static AppConfig Current { get; private set; } = new AppConfig();

        /// <summary>
        /// Loads configuration from the given JSON file.
        /// If the file does not exist, writes defaults and returns.
        /// <para>
        /// On first load against the unified <see cref="Paths.AppConfigJson"/>,
        /// if no file exists yet but a legacy per-pillar <c>{AppBase}/data/config.json</c>
        /// (or any of its sibling-pillar variants) has content, the first non-empty
        /// candidate is seeded into the new location before deserialization. A
        /// System-tier log entry names the migration source so the operator can
        /// audit which pillar's config was canonicalised.
        /// </para>
        /// <para>
        /// After deserialization, if any of the eight DPAPI-protected
        /// secret fields landed as legacy plaintext (neither the <c>dpapi:v1:</c>
        /// nor the current <c>dpapi:v2:</c> prefix in the JSON), the next Save
        /// will re-wrap them automatically because
        /// the serializer always Protects on write. A System-tier log entry
        /// notes how many secrets crossed the boundary.
        /// </para>
        /// </summary>
        public static void Load(string path)
        {
            try
            {
                // One-shot migration from per-pillar legacy paths.
                // Runs only when the unified path doesn't yet exist; we copy
                // the FIRST non-empty legacy candidate (Hub's wins because
                // Hub.WinUI is listed first in LegacyAppConfigJsonCandidates,
                // and Hub is the runtime that holds the canonical credential
                // set).
                TryMigrateLegacyConfig(path);

                if (!File.Exists(path))
                {
                    // Mint the WebSocket relay token before persisting
                    // defaults so the first config.json the user ever has on
                    // disk already carries a valid token. Without this, the
                    // first Hub boot writes a token-less config; ViewerServer
                    // / WS upgrades then fail with close 1008 until the user
                    // opens Settings (which used to be the only mint trigger).
                    EnsureWebSocketServerToken();
                    Save(path);
                    return;
                }

                string json = File.ReadAllText(path);

                // Count plaintext secrets BEFORE deserialization so
                // we can emit a single accurate migration log line. After the
                // converter has run, every in-memory secret string is
                // plaintext regardless of how it arrived on disk.
                int plaintextSecretCount = CountPlaintextSecrets(json);

                Current = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();

                if (plaintextSecretCount > 0)
                {
                    GlobalLogger.Log(
                        $"Migrated {plaintextSecretCount} AppConfig secrets to DPAPI-protected storage.",
                        "ConfigManager", LogLevel.System);
                    // Persist immediately so the at-rest form gets wrapped on
                    // the next observable read, not on whichever future Save
                    // the user triggers via Settings.
                    Save(path);
                }

                // JSON containing explicit `null` for a collection-typed field
                // overrides the property initializer with null, NRE-trapping every
                // downstream `Webhooks.TryGetValue` / `LiveCaptionsAllowedLayers.Contains`
                // / `Schedules.ForEach` site. Re-hydrate after deserialize so the API
                // contract ("collection fields are never null") holds regardless of
                // what shape the user's config.json arrived in.
                Current.Webhooks                  ??= new();
                Current.WebhookSecrets            ??= new();
                Current.LiveCaptionsAllowedLayers ??= new();
                Current.Schedules                 ??= new();

                // Lazy-init the WebSocket relay token at Load time so
                // ViewerServer / WS upgrades never fail with close 1008
                // because the operator hadn't opened Settings yet. Mints +
                // persists same-turn when the persisted value is empty or
                // too short to be credible (operator-typed gibberish).
                if (EnsureWebSocketServerToken())
                {
                    Save(path);
                }
            }
            catch (Exception ex)
            {
                // Rotate the corrupted file aside before resetting Current to
                // defaults. Without rotation, the next Save(path) call writes default
                // contents to the same path and silently destroys credentials, webhook
                // secrets, AI keys, etc. CriticalError matches the sister Save failure
                // path — credential loss is not a "System"-level event.
                try
                {
                    if (File.Exists(path))
                    {
                        string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
                        string sidecar = $"{path}.bad-{stamp}.json";
                        File.Move(path, sidecar, overwrite: false);
                        GlobalLogger.Log(
                            $"ConfigManager: corrupt config rotated to '{sidecar}' before reset to defaults.",
                            "ConfigManager", LogLevel.CriticalError);
                    }
                }
                catch (Exception rotEx)
                {
                    GlobalLogger.Log(
                        $"ConfigManager: rotation of '{path}' failed: {rotEx.Message}. Original file may be overwritten on next Save.",
                        "ConfigManager", LogLevel.CriticalError);
                }

                GlobalLogger.Log($"ConfigManager: Failed to load '{path}': {ex.Message} — using defaults.", "ConfigManager", LogLevel.CriticalError);
                Current = new AppConfig();
            }
        }

        /// <summary>
        /// Saves the current configuration to the given JSON file.
        /// Creates parent directories if they do not exist.
        /// Uses an atomic temp-then-replace pattern so a crash mid-write can
        /// never truncate the destination — mirrors LayerSerializer.Write
        /// (). The temp file is left on disk on failure so the user
        /// can recover credentials / webhook secrets manually rather than
        /// silently losing them.
        /// </summary>
        public static void Save(string path)
        {
            string json;
            try { json = JsonSerializer.Serialize(Current, _writeOpts); }
            catch (Exception ex)
            {
                GlobalLogger.Log($"ConfigManager: Failed to serialize config for '{path}': {ex.Message}", "ConfigManager", LogLevel.CriticalError);
                return;
            }
            WriteJsonAtomic(path, json);
        }

        // [freeze sweep] Serializes deferred writes to the same file so a burst
        // of UI toggles can't collide on the shared <c>.tmp</c> path.
        private static readonly System.Threading.SemaphoreSlim _deferredWriteGate = new(1, 1);

        /// <summary>
        /// [freeze sweep] Fire-and-forget background save for UI hot paths
        /// (rail / inspector toggle, recent-dir remember, the debounced layout
        /// flush). Serializes <see cref="Current"/> on the CALLING thread —
        /// sub-millisecond and race-free, since the worker never touches
        /// <c>Current</c> — then offloads only the disk write (File.WriteAllText
        /// + atomic File.Replace, the part that stalls under OneDrive / AV
        /// latency) onto the thread pool. Before this, every rail/inspector
        /// toggle and file open/save did the synchronous write on the UI
        /// thread, contributing to the "Architect freezes at random" report.
        /// <para/>
        /// Shutdown / teardown paths must still call the synchronous
        /// <see cref="Save"/> so the final write is guaranteed to land before
        /// the process or window goes away.
        /// </summary>
        public static void SaveDeferred(string path)
        {
            _ = AsyncErrorBoundary.SafeRunAsync(
                async () =>
                {
                    // Serialize INSIDE the gate so serialization and write are
                    // atomic together. Serializing on the caller thread before
                    // the gate let two concurrent SaveDeferred calls race: an
                    // earlier-serialized (stale) snapshot could acquire the gate
                    // after a later one and overwrite the newer config on disk.
                    await _deferredWriteGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        string json;
                        try { json = JsonSerializer.Serialize(Current, _writeOpts); }
                        catch (Exception ex)
                        {
                            GlobalLogger.Log($"ConfigManager: Failed to serialize config for deferred save '{path}': {ex.Message}", "ConfigManager", LogLevel.CriticalError);
                            return;
                        }
                        await Task.Run(() => WriteJsonAtomic(path, json)).ConfigureAwait(false);
                    }
                    finally { _deferredWriteGate.Release(); }
                },
                "ConfigManager", "SaveDeferred");
        }

        /// <summary>
        /// Atomic temp-then-replace write shared by <see cref="Save"/> and
        /// <see cref="SaveDeferred"/>, so both honour the same crash-safety and
        /// tmp-cleanup contract. A crash mid-write can never truncate the
        /// destination — the temp file is left on disk on failure so the user
        /// can recover credentials / webhook secrets manually rather than
        /// silently losing them.
        /// </summary>
        private static void WriteJsonAtomic(string path, string json)
        {
            string tempPath = path + ".tmp";
            bool swapped = false;
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(tempPath, json);

                // File.Replace falls back to File.Move for the first-write
                // case (destination doesn't exist yet) — Replace requires
                // both paths to exist. File.Move is atomic on the same volume
                // on Windows.
                if (File.Exists(path))
                {
                    // File.Replace gives us atomic swap-plus-optional-backup.
                    // Pass null for the backup path — we already rotate
                    // corrupted reads aside in Load(), so no second backup
                    // is needed here.
                    File.Replace(tempPath, path, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tempPath, path);
                }
                swapped = true;
            }
            catch (Exception ex)
            {
                GlobalLogger.Log($"ConfigManager: Failed to save '{path}': {ex.Message}", "ConfigManager", LogLevel.CriticalError);
            }
            finally
            {
                // Mirror LayerSerializer's tmp-cleanup contract:
                // if the swap never happened, the orphaned .tmp would sit
                // next to the live config carrying stale DPAPI-wrapped
                // secrets the user may have already rotated. Best-effort
                // delete so failed saves don't accumulate junk.
                if (!swapped && File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); }
                    catch { /* best effort cleanup */ }
                }
            }
        }

        /// <summary>
        /// Async save for UI callers (the Settings window, the Viewer panel,
        /// WelcomeDialog) that await the disk write without pegging the UI thread
        /// — important when AppData is OneDrive-backed and a write can stall on
        /// cloud sync. Serializes through the SAME <see cref="_deferredWriteGate"/>
        /// as <see cref="SaveDeferred"/> (serialize + write both inside the gate)
        /// so two concurrent awaited saves — e.g. the now-non-modal Settings
        /// window and the Viewer panel both calling SaveAsync — can't race the
        /// shared "&lt;path&gt;.tmp" file (a stale earlier snapshot overwriting a
        /// newer one, or one caller's File.Replace hitting a tmp the other already
        /// consumed). The temp-then-replace atomicity + tmp-cleanup contract is
        /// preserved via <see cref="WriteJsonAtomic"/>.
        /// </summary>
        public static async Task SaveAsync(string path)
        {
            await _deferredWriteGate.WaitAsync().ConfigureAwait(false);
            try
            {
                string json;
                try { json = JsonSerializer.Serialize(Current, _writeOpts); }
                catch (Exception ex)
                {
                    GlobalLogger.Log($"ConfigManager: Failed to serialize config for '{path}': {ex.Message}", "ConfigManager", LogLevel.CriticalError);
                    return;
                }
                await Task.Run(() => WriteJsonAtomic(path, json)).ConfigureAwait(false);
            }
            finally { _deferredWriteGate.Release(); }
        }

        // ────────────────────────────────────────────────────────────────────
        // migration helpers
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The eight secret fields <see cref="CountPlaintextSecrets"/> scans for, to detect
        /// legacy-format configs landing on first DPAPI-aware load.
        ///
        /// ★ This is NOT the complete set of DPAPI-wrapped properties, and saying so is the
        /// point: <c>AppConfig.ObsWebSocketPassword</c> and <c>AppConfig.YouTubeDataApiKey</c>
        /// both carry <c>[JsonConverter(typeof(DpapiProtectedStringConverter))]</c> and are
        /// absent from this array. The converter is the authority on what gets wrapped, so
        /// those two are still written protected; what they miss is only the eager re-save
        /// this count triggers, so a legacy config whose ONLY plaintext secret is one of them
        /// stays plaintext on disk until the next Save rather than being rewritten on load.
        /// Reconciling the two lists changes migration behaviour and belongs in its own change.
        /// </summary>
        private static readonly string[] s_protectedSecretFields = {
            "OpenAIApiKey",
            "AnthropicKey",
            "CerebrasApiKey",
            "DiscordBotToken",
            "StreamerBotPassword",
            "WebhookSecret",
            "TranslationApiKey",
            "WebSocketServerToken",
        };

        /// <summary>
        /// Seeds the unified <paramref name="targetPath"/> from the
        /// first non-empty legacy per-pillar candidate. No-op when the target
        /// already exists or when no legacy candidate has content.
        /// </summary>
        internal static void TryMigrateLegacyConfig(string targetPath)
        {
            try
            {
                if (File.Exists(targetPath)) return;

                foreach (string candidate in Paths.LegacyAppConfigJsonCandidates)
                {
                    if (string.Equals(candidate, targetPath, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!File.Exists(candidate)) continue;

                    string content;
                    try { content = File.ReadAllText(candidate); }
                    catch { continue; }
                    if (string.IsNullOrWhiteSpace(content)) continue;

                    // Found the seed. Ensure the target directory exists,
                    // then copy verbatim — the next Load() pass deserialises
                    // it normally and the next Save() rewraps any plaintext
                    // secrets to DPAPI form.
                    string? dir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(targetPath, content);

                    GlobalLogger.Log(
                        $"ConfigManager: migrated legacy per-pillar config from '{candidate}' " +
                        $"to unified '{targetPath}'. Subsequent saves write to the unified path only.",
                        "ConfigManager", LogLevel.System);
                    return;
                }
            }
            catch (Exception ex)
            {
                // Migration is best-effort. If it fails the unified path stays
                // absent, Load() falls through to Save(defaults) and the user
                // re-enters their settings — not silent, just inconvenient.
                GlobalLogger.Log(
                    $"ConfigManager: legacy per-pillar migration failed: {ex.Message}",
                    "ConfigManager", LogLevel.CriticalError);
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // WebSocket relay token lazy-init
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Minimum entropy required of <see cref="AppConfig.WebSocketServerToken"/>.
        /// 32 bytes → base64-url-encoded ~43 chars. Anything shorter on disk is
        /// treated as operator-typed gibberish (e.g. "abc" in config.json) and
        /// regenerated. Mirrors the WebSocketServerService constant so the two
        /// invariants stay aligned without a Shared→Hub reference flip.
        /// </summary>
        private const int WebSocketServerTokenByteLength = 32;

        /// <summary>
        /// Ensures <see cref="AppConfig.WebSocketServerToken"/> on
        /// <see cref="Current"/> carries a credible base64-url-encoded shared
        /// secret. Returns <c>true</c> when a fresh token was minted (caller
        /// is expected to persist), <c>false</c> when the existing value was
        /// already acceptable. Does NOT save to disk — that's the caller's
        /// responsibility so we don't double-save during the
        /// <see cref="Load"/> + <see cref="Save"/> sequence.
        /// </summary>
        public static bool EnsureWebSocketServerToken()
        {
            // Re-check inside the lock so a racing
            // thread that already minted a token wins; without this two threads
            // can both pass the pre-check and mint divergent tokens.
            lock (_webSocketTokenGate)
            {
                string existing = Current.WebSocketServerToken ?? "";
                if (!string.IsNullOrEmpty(existing) && existing.Length >= WebSocketServerTokenByteLength)
                    return false;

                Current.WebSocketServerToken = MintWebSocketServerToken();
                GlobalLogger.Log(
                    "ConfigManager: minted fresh AppConfig.WebSocketServerToken (lazy-init at Load). " +
                    "Clients must connect with ?token=<value>; rotate via Settings.",
                    "ConfigManager", LogLevel.System);
                return true;
            }
        }

        /// <summary>
        /// Operator-facing "panic button" — regenerate the WebSocket
        /// relay token unconditionally and persist same-turn. Invalidates
        /// every connected client (existing sockets stay up until the next
        /// upgrade re-checks <c>?token=</c>; restart the WebSocketServer to
        /// kick them off). Logs a System-tier entry so the rotation is
        /// audit-visible.
        /// </summary>
        public static void RegenerateWebSocketServerToken()
        {
            // Mint+Save under the same gate as
            // EnsureWebSocketServerToken so an operator rotation can't interleave
            // with a lazy-init mint and leave a divergent token persisted.
            lock (_webSocketTokenGate)
            {
                Current.WebSocketServerToken = MintWebSocketServerToken();
                GlobalLogger.Log(
                    "ConfigManager: regenerated AppConfig.WebSocketServerToken on operator request. " +
                    "Restart the WebSocketServer to disconnect existing clients.",
                    "ConfigManager", LogLevel.System);
                try
                {
                    Save(Paths.AppConfigJson);
                }
                catch (Exception ex)
                {
                    GlobalLogger.Log(
                        $"ConfigManager: failed to persist regenerated WebSocketServerToken: {ex.Message}",
                        "ConfigManager", LogLevel.CriticalError);
                }
            }
        }

        /// <summary>
        /// Mint a 32-byte cryptographically-random token, base64-url
        /// encoded (so it slots cleanly into a <c>?token=</c> query string
        /// without %-encoding round-trips that an operator might botch).
        /// </summary>
        private static string MintWebSocketServerToken()
        {
            byte[] raw = RandomNumberGenerator.GetBytes(WebSocketServerTokenByteLength);
            return Convert.ToBase64String(raw)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }

        /// <summary>
        /// Counts how many of the eight DPAPI-protected secret
        /// fields are present in the raw JSON with a non-empty value that is
        /// NOT in a DPAPI-wrapped on-disk form. The test goes through
        /// <see cref="DpapiSecretStore.IsProtected"/>, which recognises BOTH
        /// the legacy <see cref="DpapiSecretStore.Prefix"/> (v1) and the
        /// current <see cref="DpapiSecretStore.PrefixV2"/> that
        /// <see cref="DpapiSecretStore.Protect"/> actually emits — testing only
        /// the v1 prefix counted every correctly-protected value as plaintext,
        /// which made <see cref="Load"/> log a bogus migration line and re-Save
        /// config.json on every single boot. Run before deserialization
        /// because the converter normalises everything to plaintext in-memory.
        /// </summary>
        private static int CountPlaintextSecrets(string json)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return 0;

                int count = 0;
                foreach (string field in s_protectedSecretFields)
                {
                    if (!doc.RootElement.TryGetProperty(field, out JsonElement el)) continue;
                    if (el.ValueKind != JsonValueKind.String) continue;
                    string? value = el.GetString();
                    if (string.IsNullOrEmpty(value)) continue;
                    if (!DpapiSecretStore.IsProtected(value))
                        count++;
                }
                return count;
            }
            catch
            {
                // Malformed JSON is the rotate-aside path's problem, not ours.
                return 0;
            }
        }
    }
}
