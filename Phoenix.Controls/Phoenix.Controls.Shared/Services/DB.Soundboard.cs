using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.Threading.Tasks;

namespace Phoenix.Controls.Shared.Services
{
    // Soundboard persistence — the DB half of the clip-playback pre-build tool.
    //
    // Like Alerts / Scheduling / Song Request there is NO open user table, and the
    // reason here is that the tool genuinely owns no data: the clips are FILES in
    // data/media (the media library owns those) and the rows are configuration. So the
    // WHOLE tool state (row list + overlay hookup + master toggle) lives in ONE private
    // JSON blob in the SoundboardConfig SYSTEM table (Slug='config'). Cooldown buckets
    // are deliberately in-memory only — a cooldown that survived a Hub restart would be
    // a worse answer than one that resets with the stream.
    public partial class DB
    {
        internal const string SoundboardConfigTablesDdl = @"
            CREATE TABLE IF NOT EXISTS SoundboardConfig (
                Slug      TEXT PRIMARY KEY,
                Json      TEXT    NOT NULL,
                UpdatedAt INTEGER NOT NULL DEFAULT 0
            );";

        // Symmetry with the sibling tools: back-fill any column added after an earlier
        // dev build. Fresh installs no-op through the probe.
        private void EnsureSoundboardSchemaMigrations()
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var probe = new SqliteCommand("PRAGMA table_info(SoundboardConfig)", _connection))
            using (var r = probe.ExecuteReader())
            {
                while (r.Read()) existing.Add(r.GetString(1));
            }
            var wanted = new (string Column, string Ddl)[]
            {
                ("Json",      "ALTER TABLE SoundboardConfig ADD COLUMN Json TEXT DEFAULT ''"),
                ("UpdatedAt", "ALTER TABLE SoundboardConfig ADD COLUMN UpdatedAt INTEGER NOT NULL DEFAULT 0"),
            };
            foreach (var (column, ddl) in wanted)
            {
                if (existing.Contains(column)) continue;
                using var alter = new SqliteCommand(ddl, _connection);
                alter.ExecuteNonQuery();
            }
        }

        // ── Config blob (system table) ──────────────────────────────────────

        /// <summary>Loads the serialized SoundboardConfig JSON, or null when unset.</summary>
        public async Task<string?> LoadSoundboardConfigAsync()
            => await QueryScalarAsync<string>(
                "SELECT Json FROM SoundboardConfig WHERE Slug = 'config' LIMIT 1", _ => { }).ConfigureAwait(false);

        /// <summary>Persists the serialized SoundboardConfig JSON.</summary>
        public async Task SaveSoundboardConfigAsync(string json, long updatedAtMs)
        {
            await ExecuteAsync(
                @"INSERT INTO SoundboardConfig (Slug, Json, UpdatedAt) VALUES ('config', @json, @upd)
                  ON CONFLICT(Slug) DO UPDATE SET Json = @json, UpdatedAt = @upd",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@json", json ?? "");
                    cmd.Parameters.AddWithValue("@upd", updatedAtMs);
                }).ConfigureAwait(false);
        }
    }
}
