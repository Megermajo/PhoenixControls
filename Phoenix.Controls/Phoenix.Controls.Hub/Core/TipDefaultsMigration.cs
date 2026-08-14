using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// One-shot "tips start OFF" migration.
    ///
    /// WHY IT IS NEEDED AT ALL. Two tip settings shipped ENABLED —
    /// <c>LoyaltyEarnConfig.TipEnabled</c>/<c>TipPerUnit</c> and
    /// <c>TimerActionConfig.TipPerUnitSeconds</c> — and were harmless only because
    /// Phoenix subscribed no donation source, so no tip event could ever reach
    /// them. Donation ingestion removes that accident: on the first connected
    /// broker those settings become live money and live subathon time, with no
    /// one having consented. Flipping the model defaults fixes a FRESH install
    /// only; the SQLite databank in Roaming AppData survives in-place upgrades, so
    /// an existing config blob would reload with the old enabled values and start
    /// paying out. This corrects those blobs exactly once.
    ///
    /// DELIBERATELY NOT part of <c>PreBuildOptInMigration</c>. That one flips only
    /// the top-level master <c>Enabled</c> key of a tool blob and is explicitly
    /// documented as top-level-by-design; these settings are nested
    /// (<c>Earn.TipEnabled</c>) and, for timers, live inside a typed per-row blob.
    /// Different shape, different one-shot flag, own file.
    ///
    /// Guarded by <c>AppConfig.TipDefaultsForcedOffMigrated</c> and run from
    /// HubBootstrapper BEFORE LoyaltyService/TimerService load their config, so
    /// each InitializeAsync reads the corrected value.
    /// </summary>
    public static class TipDefaultsMigration
    {
        /// <summary>
        /// Runs the full pass. Returns what it changed. Malformed blobs are logged
        /// and skipped (the owning service fails the same deserialization and
        /// falls back to its defaults, which are now OFF anyway); any DB-level
        /// exception PROPAGATES so the caller leaves the one-shot flag unset and
        /// the migration retries next boot rather than half-migrating silently.
        /// </summary>
        public static async Task<(bool LoyaltyChanged, int TimersChanged)> RunAsync(DB db, string? onlySlugPrefix = null)
        {
            bool loyalty = await ForceLoyaltyTipOffAsync(db).ConfigureAwait(false);
            int timers = await ZeroTimerTipSecondsAsync(db, onlySlugPrefix).ConfigureAwait(false);
            return (loyalty, timers);
        }

        /// <summary>
        /// Zeroes <c>Earn.TipEnabled</c> / <c>Earn.TipPerUnit</c> in the persisted
        /// LoyaltyConfig blob, preserving every other field.
        ///
        /// Uses JsonNode rather than the typed model so a blob written by any build
        /// version round-trips without dropping unknown fields, and matches keys
        /// case-insensitively to mirror the service's
        /// <c>PropertyNameCaseInsensitive</c> deserialization. Writes only when
        /// something actually changed.
        /// </summary>
        internal static async Task<bool> ForceLoyaltyTipOffAsync(DB db)
        {
            string? json = await db.LoadLoyaltyConfigAsync().ConfigureAwait(false); // DB errors propagate
            if (string.IsNullOrWhiteSpace(json)) return false;                       // fresh install

            JsonObject? root;
            try
            {
                root = JsonNode.Parse(json) as JsonObject;
            }
            catch (System.Text.Json.JsonException ex)
            {
                GlobalLogger.Error("TipDefaultsMigration",
                    "Loyalty config blob is malformed — skipped (service defaults to tips off)", ex);
                return false;
            }
            if (root is null) return false;

            var earn = FindChildObject(root, "Earn");
            if (earn is null) return false; // no Earn section ⇒ defaults apply ⇒ already off

            bool changed = false;
            changed |= SetBoolFalse(earn, "TipEnabled");
            changed |= SetIntZero(earn, "TipPerUnit");
            if (!changed) return false;

            await db.SaveLoyaltyConfigAsync(root.ToJsonString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    .ConfigureAwait(false); // DB errors propagate
            GlobalLogger.Log(
                "Loyalty tip earn was persisted as enabled — forced OFF by the one-shot tip-defaults migration. " +
                "Re-enable it in the Loyalty page if you want tips to pay points.",
                "TipDefaultsMigration", LogLevel.System);
            return true;
        }

        /// <summary>
        /// Zeroes <c>Actions.TipPerUnitSeconds</c> on every persisted timer.
        ///
        /// Timers are stored as one row per timer with the whole typed
        /// <c>StreamTimer</c> serialized into its Json column, and
        /// <c>UpsertTimerAsync</c> re-serializes from the typed model — so unlike
        /// the Loyalty blob there is no JsonNode round-trip available here; the
        /// typed object is mutated directly, exactly as the sibling
        /// PreBuildOptInMigration mutates <c>State</c>.
        ///
        /// <paramref name="onlySlugPrefix"/> is the same TEST seam the sibling
        /// migration carries, and for the same reason: the suite runs against the
        /// process-wide <c>DB.Instance</c> — the REAL databank — so an unfiltered
        /// pass inside a test would silently reconfigure a live subathon.
        /// Production passes null.
        /// </summary>
        internal static async Task<int> ZeroTimerTipSecondsAsync(DB db, string? onlySlugPrefix = null)
        {
            int changed = 0;
            foreach (var t in await db.GetTimersAsync().ConfigureAwait(false))
            {
                if (t?.Actions is null) continue;
                if (onlySlugPrefix is not null &&
                    !(t.Slug ?? "").StartsWith(onlySlugPrefix, StringComparison.Ordinal)) continue;
                if (t.Actions.TipPerUnitSeconds == 0) continue;

                t.Actions.TipPerUnitSeconds = 0;
                t.UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await db.UpsertTimerAsync(t).ConfigureAwait(false);
                changed++;
            }
            if (changed > 0)
            {
                GlobalLogger.Log(
                    $"Zeroed the tip seconds-per-unit on {changed} timer(s) — tips no longer extend a subathon " +
                    "until you set that rate yourself.",
                    "TipDefaultsMigration", LogLevel.System);
            }
            return changed;
        }

        // ── JsonNode helpers (case-insensitive, write-only-when-changed) ─────

        private static JsonObject? FindChildObject(JsonObject parent, string name)
        {
            foreach (var kv in parent)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                    return kv.Value as JsonObject;
            return null;
        }

        private static string? FindKey(JsonObject obj, string name)
        {
            foreach (var kv in obj)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                    return kv.Key;
            return null;
        }

        private static bool SetBoolFalse(JsonObject obj, string name)
        {
            string? key = FindKey(obj, name);
            if (key is null) return false;
            if (obj[key] is not JsonValue v || !v.TryGetValue<bool>(out bool cur) || !cur) return false;
            obj[key] = false;
            return true;
        }

        private static bool SetIntZero(JsonObject obj, string name)
        {
            string? key = FindKey(obj, name);
            if (key is null) return false;
            if (obj[key] is not JsonValue v || !v.TryGetValue<int>(out int cur) || cur == 0) return false;
            obj[key] = 0;
            return true;
        }
    }
}
