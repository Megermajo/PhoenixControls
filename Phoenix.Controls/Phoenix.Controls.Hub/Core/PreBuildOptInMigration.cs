using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// One-shot "everything opt-in" cleanup for the Pre-Build tools. Fresh
    /// installs are already correct (every tool master toggle defaults OFF and
    /// no timer exists), but the SQLite DB in Roaming AppData survives in-place
    /// upgrades — earlier 1.1 test sessions can leave tool config blobs
    /// persisted with Enabled=true and timers checkpointed Running, which then
    /// reload as "active" after this patch installs. This migration forces all
    /// twelve config-gated tools OFF (Loyalty / Automod / Counters / Quotes /
    /// CustomCommands / Scheduling / UserManagement / Alerts / SongRequest /
    /// Polls / Ranks / Soundboard) and demotes Running timers to Paused at the DB level. Every tool added to the
    /// Pre-Build family belongs in RunAsync — the master toggle defaulting OFF
    /// only protects fresh installs, not a DB carried across an upgrade.
    /// The caller (HubBootstrapper) guards it behind the
    /// AppConfig.PreBuildToolsForcedOffMigrated one-shot flag and runs it
    /// BEFORE any tool service loads its blob, so each InitializeAsync reads
    /// the corrected value.
    /// </summary>
    public static class PreBuildOptInMigration
    {
        /// <summary>
        /// Runs the full force-off pass. Returns how many tool configs were
        /// flipped and how many timers were demoted. Malformed blobs are
        /// logged and skipped (the owning service defaults to disabled on the
        /// same parse failure); any DB-level exception propagates so the
        /// caller does NOT set the one-shot flag and the migration retries
        /// next boot instead of half-migrating silently.
        /// </summary>
        public static async Task<(int DisabledTools, int PausedTimers)> RunAsync(DB db)
        {
            int disabled = 0;

            disabled += await ForceToolConfigOffAsync("Loyalty",
                db.LoadLoyaltyConfigAsync, db.SaveLoyaltyConfigAsync).ConfigureAwait(false) ? 1 : 0;
            disabled += await ForceToolConfigOffAsync("Automod",
                db.LoadAutomodConfigAsync, db.SaveAutomodConfigAsync).ConfigureAwait(false) ? 1 : 0;
            disabled += await ForceToolConfigOffAsync("Counters",
                db.LoadCountersConfigAsync, db.SaveCountersConfigAsync).ConfigureAwait(false) ? 1 : 0;
            disabled += await ForceToolConfigOffAsync("Quotes",
                db.LoadQuotesConfigAsync, db.SaveQuotesConfigAsync).ConfigureAwait(false) ? 1 : 0;
            disabled += await ForceToolConfigOffAsync("CustomCommands",
                db.LoadCustomCommandsConfigAsync, db.SaveCustomCommandsConfigAsync).ConfigureAwait(false) ? 1 : 0;
            disabled += await ForceToolConfigOffAsync("Scheduling",
                db.LoadSchedulingConfigAsync, db.SaveSchedulingConfigAsync).ConfigureAwait(false) ? 1 : 0;
            disabled += await ForceToolConfigOffAsync("UserManagement",
                db.LoadUserMgmtConfigAsync, db.SaveUserMgmtConfigAsync).ConfigureAwait(false) ? 1 : 0;
            disabled += await ForceToolConfigOffAsync("Alerts",
                db.LoadAlertsConfigAsync, db.SaveAlertsConfigAsync).ConfigureAwait(false) ? 1 : 0;
            disabled += await ForceToolConfigOffAsync("SongRequest",
                db.LoadSongRequestConfigAsync, db.SaveSongRequestConfigAsync).ConfigureAwait(false) ? 1 : 0;
            disabled += await ForceToolConfigOffAsync("Polls",
                db.LoadPollsConfigAsync, db.SavePollsConfigAsync).ConfigureAwait(false) ? 1 : 0;
            disabled += await ForceToolConfigOffAsync("Ranks",
                db.LoadRanksConfigAsync, db.SaveRanksConfigAsync).ConfigureAwait(false) ? 1 : 0;
            disabled += await ForceToolConfigOffAsync("Soundboard",
                db.LoadSoundboardConfigAsync, db.SaveSoundboardConfigAsync).ConfigureAwait(false) ? 1 : 0;

            int paused = await DemoteRunningTimersAsync(db).ConfigureAwait(false);

            return (disabled, paused);
        }

        /// <summary>
        /// Timers: TimerService.InitializeAsync trusts a persisted
        /// State=Running (the deliberate subathon-survives-restart path), so
        /// demote to Paused at the DB level BEFORE the service loads the rows.
        /// RemainingMs/ElapsedMs are preserved — an explicit Resume continues
        /// exactly where the timer left off. <paramref name="onlySlugPrefix"/>
        /// is a TEST seam: the test suite runs against the process-wide
        /// DB.Instance (the shared databank), and an unfiltered pass there
        /// would demote a REAL running subathon; production passes null (all).
        /// </summary>
        internal static async Task<int> DemoteRunningTimersAsync(DB db, string? onlySlugPrefix = null)
        {
            int paused = 0;
            foreach (var t in await db.GetTimersAsync().ConfigureAwait(false))
            {
                if (t.State != TimerRunState.Running) continue;
                if (onlySlugPrefix is not null &&
                    !(t.Slug ?? "").StartsWith(onlySlugPrefix, StringComparison.Ordinal)) continue;
                t.State = TimerRunState.Paused;
                t.UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await db.UpsertTimerAsync(t).ConfigureAwait(false);
                paused++;
            }
            return paused;
        }

        /// <summary>
        /// Flips ONLY the master "Enabled" key of one tool's persisted JSON
        /// config blob to false, preserving every other setting byte-for-byte.
        /// Uses JsonNode (not the typed model) so a blob written by any build
        /// version round-trips without dropping fields; the key match is
        /// case-insensitive to mirror the services' PropertyNameCaseInsensitive
        /// deserialization. Returns true when a flip was persisted.
        /// Failure split (load-bearing): a MALFORMED blob is logged and skipped
        /// — the service's own deserialization fails identically and falls back
        /// to the Enabled=false default, so skipping is safe. A DB-level
        /// load/save exception PROPAGATES so the caller keeps the one-shot flag
        /// unset and the whole migration retries next boot — swallowing it here
        /// would permanently strand that tool enabled.
        /// </summary>
        internal static async Task<bool> ForceToolConfigOffAsync(
            string toolName, Func<Task<string?>> load, Func<string, long, Task> save)
        {
            string? json = await load().ConfigureAwait(false); // DB errors propagate
            if (string.IsNullOrWhiteSpace(json)) return false; // fresh install — nothing persisted

            JsonObject? obj;
            try
            {
                obj = JsonNode.Parse(json) as JsonObject;
            }
            catch (System.Text.Json.JsonException ex)
            {
                GlobalLogger.Error("PreBuildOptInMigration",
                    $"{toolName} config blob is malformed — skipped (service will default to disabled)", ex);
                return false;
            }
            if (obj is null) return false;

            string? key = null;
            foreach (var kv in obj)
            {
                if (string.Equals(kv.Key, "Enabled", StringComparison.OrdinalIgnoreCase)) { key = kv.Key; break; }
            }
            if (key is null) return false; // absent key deserializes as the false default
            if (obj[key] is not JsonValue val || !val.TryGetValue<bool>(out bool enabled) || !enabled) return false;

            obj[key] = false;
            await save(obj.ToJsonString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ConfigureAwait(false); // DB errors propagate
            GlobalLogger.Log(
                $"{toolName} was persisted as enabled — forced OFF by the one-shot opt-in migration.",
                "PreBuildOptInMigration", LogLevel.System);
            return true;
        }
    }
}
