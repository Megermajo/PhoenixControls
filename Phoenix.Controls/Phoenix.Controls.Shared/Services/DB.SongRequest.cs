using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.Threading.Tasks;

namespace Phoenix.Controls.Shared.Services
{
    // Song Request persistence — the DB half of the YouTube request-queue pre-build tool.
    //
    // Like Scheduling and Alerts, and UNLIKE Counters/Loyalty/Quotes, there is NO open
    // user table. The reason is not symmetry, it is correctness: the request QUEUE is
    // session state that names a track the overlay player is part-way through, and a Hub
    // restart cannot restore playback position — so a persisted queue would reload with an
    // entry marked Playing that nothing is playing, and every live-channel key would be
    // wrong from the first tick. The queue therefore lives only in SongRequestService, and
    // the whole persisted state is ONE private JSON blob (command words, role gates, caps,
    // price, approval, volume) in the SongRequestConfig SYSTEM table, Slug='config' —
    // mirroring SchedulingConfig / AlertsConfig.
    //
    // The optional YouTube Data API key is NOT here. It lives on AppConfig behind
    // DpapiProtectedStringConverter, because this blob is stored in SQLite unwrapped.
    public partial class DB
    {
        internal const string SongRequestConfigTablesDdl = @"
            CREATE TABLE IF NOT EXISTS SongRequestConfig (
                Slug      TEXT PRIMARY KEY,
                Json      TEXT    NOT NULL,
                UpdatedAt INTEGER NOT NULL DEFAULT 0
            );";

        // Symmetry with EnsureScheduling/Alerts/Quotes/TimerSchemaMigrations: back-fill any
        // column added after an earlier dev build. Fresh installs no-op through the probe.
        private void EnsureSongRequestSchemaMigrations()
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var probe = new SqliteCommand("PRAGMA table_info(SongRequestConfig)", _connection))
            using (var r = probe.ExecuteReader())
            {
                while (r.Read()) existing.Add(r.GetString(1));
            }
            var wanted = new (string Column, string Ddl)[]
            {
                ("Json",      "ALTER TABLE SongRequestConfig ADD COLUMN Json TEXT DEFAULT ''"),
                ("UpdatedAt", "ALTER TABLE SongRequestConfig ADD COLUMN UpdatedAt INTEGER NOT NULL DEFAULT 0"),
            };
            foreach (var (column, ddl) in wanted)
            {
                if (existing.Contains(column)) continue;
                using var alter = new SqliteCommand(ddl, _connection);
                alter.ExecuteNonQuery();
            }
        }

        // ── Config blob (system table) ──────────────────────────────────────

        /// <summary>Loads the serialized SongRequestConfig JSON, or null when unset.</summary>
        public async Task<string?> LoadSongRequestConfigAsync()
            => await QueryScalarAsync<string>(
                "SELECT Json FROM SongRequestConfig WHERE Slug = 'config' LIMIT 1", _ => { }).ConfigureAwait(false);

        /// <summary>Persists the serialized SongRequestConfig JSON.</summary>
        public async Task SaveSongRequestConfigAsync(string json, long updatedAtMs)
        {
            await ExecuteAsync(
                @"INSERT INTO SongRequestConfig (Slug, Json, UpdatedAt) VALUES ('config', @json, @upd)
                  ON CONFLICT(Slug) DO UPDATE SET Json = @json, UpdatedAt = @upd",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@json", json ?? "");
                    cmd.Parameters.AddWithValue("@upd", updatedAtMs);
                }).ConfigureAwait(false);
        }
    }
}
