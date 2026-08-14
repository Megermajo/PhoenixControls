using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.Threading.Tasks;

namespace Phoenix.Controls.Shared.Services
{
    // Scheduling persistence — the DB half of the recurring-messages pre-build tool.
    //
    // Unlike Counters/Loyalty/Quotes there is NO open user table: a schedule carries no
    // data a db.* script needs to read or build on, so the WHOLE tool state (the list of
    // schedules + the master/only-when-live toggles) lives in ONE private JSON blob in the
    // SchedulingConfig SYSTEM table (Slug='config'), mirroring LoyaltyConfig/CountersConfig/
    // Timers.Json. The per-schedule runtime counters (chat lines / fire count / last-post
    // clock) are ephemeral and held only in SchedulingService — never persisted.
    public partial class DB
    {
        internal const string SchedulingConfigTablesDdl = @"
            CREATE TABLE IF NOT EXISTS SchedulingConfig (
                Slug      TEXT PRIMARY KEY,
                Json      TEXT    NOT NULL,
                UpdatedAt INTEGER NOT NULL DEFAULT 0
            );";

        // Symmetry with EnsureCounters/Loyalty/TimerSchemaMigrations: back-fill any column
        // added after an earlier dev build. Fresh installs no-op through the probe.
        private void EnsureSchedulingSchemaMigrations()
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var probe = new SqliteCommand("PRAGMA table_info(SchedulingConfig)", _connection))
            using (var r = probe.ExecuteReader())
            {
                while (r.Read()) existing.Add(r.GetString(1));
            }
            var wanted = new (string Column, string Ddl)[]
            {
                ("Json",      "ALTER TABLE SchedulingConfig ADD COLUMN Json TEXT DEFAULT ''"),
                ("UpdatedAt", "ALTER TABLE SchedulingConfig ADD COLUMN UpdatedAt INTEGER NOT NULL DEFAULT 0"),
            };
            foreach (var (column, ddl) in wanted)
            {
                if (existing.Contains(column)) continue;
                using var alter = new SqliteCommand(ddl, _connection);
                alter.ExecuteNonQuery();
            }
        }

        // ── Config blob (system table) ──────────────────────────────────────

        /// <summary>Loads the serialized SchedulingConfig JSON, or null when unset.</summary>
        public async Task<string?> LoadSchedulingConfigAsync()
            => await QueryScalarAsync<string>(
                "SELECT Json FROM SchedulingConfig WHERE Slug = 'config' LIMIT 1", _ => { }).ConfigureAwait(false);

        /// <summary>Persists the serialized SchedulingConfig JSON.</summary>
        public async Task SaveSchedulingConfigAsync(string json, long updatedAtMs)
        {
            await ExecuteAsync(
                @"INSERT INTO SchedulingConfig (Slug, Json, UpdatedAt) VALUES ('config', @json, @upd)
                  ON CONFLICT(Slug) DO UPDATE SET Json = @json, UpdatedAt = @upd",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@json", json ?? "");
                    cmd.Parameters.AddWithValue("@upd", updatedAtMs);
                }).ConfigureAwait(false);
        }
    }
}
