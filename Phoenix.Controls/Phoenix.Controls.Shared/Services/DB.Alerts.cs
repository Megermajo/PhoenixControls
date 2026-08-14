using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.Threading.Tasks;

namespace Phoenix.Controls.Shared.Services
{
    // Alerts persistence — the DB half of the event-response pre-build tool.
    //
    // Like Scheduling there is NO open user table: an alert rule carries nothing a
    // db.* script needs to read, so the WHOLE tool state (per-event responses + size
    // tiers + the master toggle) lives in ONE private JSON blob in the AlertsConfig
    // SYSTEM table (Slug='config'). Fire counters are ephemeral (AlertsService only).
    public partial class DB
    {
        internal const string AlertsConfigTablesDdl = @"
            CREATE TABLE IF NOT EXISTS AlertsConfig (
                Slug      TEXT PRIMARY KEY,
                Json      TEXT    NOT NULL,
                UpdatedAt INTEGER NOT NULL DEFAULT 0
            );";

        // Symmetry with the sibling tools: back-fill any column added after an earlier
        // dev build. Fresh installs no-op through the probe.
        private void EnsureAlertsSchemaMigrations()
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var probe = new SqliteCommand("PRAGMA table_info(AlertsConfig)", _connection))
            using (var r = probe.ExecuteReader())
            {
                while (r.Read()) existing.Add(r.GetString(1));
            }
            var wanted = new (string Column, string Ddl)[]
            {
                ("Json",      "ALTER TABLE AlertsConfig ADD COLUMN Json TEXT DEFAULT ''"),
                ("UpdatedAt", "ALTER TABLE AlertsConfig ADD COLUMN UpdatedAt INTEGER NOT NULL DEFAULT 0"),
            };
            foreach (var (column, ddl) in wanted)
            {
                if (existing.Contains(column)) continue;
                using var alter = new SqliteCommand(ddl, _connection);
                alter.ExecuteNonQuery();
            }
        }

        // ── Config blob (system table) ──────────────────────────────────────

        /// <summary>Loads the serialized AlertsConfig JSON, or null when unset.</summary>
        public async Task<string?> LoadAlertsConfigAsync()
            => await QueryScalarAsync<string>(
                "SELECT Json FROM AlertsConfig WHERE Slug = 'config' LIMIT 1", _ => { }).ConfigureAwait(false);

        /// <summary>Persists the serialized AlertsConfig JSON.</summary>
        public async Task SaveAlertsConfigAsync(string json, long updatedAtMs)
        {
            await ExecuteAsync(
                @"INSERT INTO AlertsConfig (Slug, Json, UpdatedAt) VALUES ('config', @json, @upd)
                  ON CONFLICT(Slug) DO UPDATE SET Json = @json, UpdatedAt = @upd",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@json", json ?? "");
                    cmd.Parameters.AddWithValue("@upd", updatedAtMs);
                }).ConfigureAwait(false);
        }
    }
}
