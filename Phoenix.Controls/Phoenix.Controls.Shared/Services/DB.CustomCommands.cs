using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.Threading.Tasks;

namespace Phoenix.Controls.Shared.Services
{
    // Custom Chat Commands persistence — the DB half of the text/variable-only
    // custom-command tool.
    //
    // STORAGE DOCTRINE (deviation from DB.Counters/DB.Quotes): this tool has NO open
    // data table. Its ONLY storage is the private config blob — a single JSON row in
    // the SYSTEM table literally named "CustomCommands" (registered in DB._systemTables
    // so db.* / remote-bridge can never mutate it). The {count}/{count.next} tokens do
    // NOT get their own counter table here — they CONSUME the already-built Counters
    // tool (the OPEN "Counters" table via CountersService), so nothing counter-shaped
    // lives in this file.
    //
    // The config-blob shape (Slug PK / Json / UpdatedAt + ON CONFLICT(Slug) upsert) is
    // a byte-for-byte clone of QuotesConfig/CountersConfig — legal because Slug is a
    // real PRIMARY KEY of a system table. EnsureCustomCommandsSchemaMigrations mirrors
    // the sibling probe-and-backfill pattern.
    public partial class DB
    {
        internal const string CustomCommandsTablesDdl = @"
            CREATE TABLE IF NOT EXISTS CustomCommands (
                Slug      TEXT PRIMARY KEY,
                Json      TEXT    NOT NULL,
                UpdatedAt INTEGER NOT NULL DEFAULT 0
            );";

        // Symmetry with EnsureQuotes/Counters/LoyaltySchemaMigrations: back-fill any
        // column added after an earlier dev build. Fresh installs no-op through the probe.
        private void EnsureCustomCommandsSchemaMigrations()
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var probe = new SqliteCommand("PRAGMA table_info(CustomCommands)", _connection))
            using (var r = probe.ExecuteReader())
            {
                while (r.Read()) existing.Add(r.GetString(1));
            }
            var wanted = new (string Column, string Ddl)[]
            {
                ("Json",      "ALTER TABLE CustomCommands ADD COLUMN Json TEXT DEFAULT ''"),
                ("UpdatedAt", "ALTER TABLE CustomCommands ADD COLUMN UpdatedAt INTEGER NOT NULL DEFAULT 0"),
            };
            foreach (var (column, ddl) in wanted)
            {
                if (existing.Contains(column)) continue;
                using var alter = new SqliteCommand(ddl, _connection);
                alter.ExecuteNonQuery();
            }
        }

        // ── Config blob (system table) ──────────────────────────────────────

        /// <summary>Loads the serialized CustomCommandsConfig JSON, or null when unset.</summary>
        public async Task<string?> LoadCustomCommandsConfigAsync()
            => await QueryScalarAsync<string>(
                "SELECT Json FROM CustomCommands WHERE Slug = 'config' LIMIT 1", _ => { }).ConfigureAwait(false);

        /// <summary>Persists the serialized CustomCommandsConfig JSON.</summary>
        public async Task SaveCustomCommandsConfigAsync(string json, long updatedAtMs)
        {
            await ExecuteAsync(
                @"INSERT INTO CustomCommands (Slug, Json, UpdatedAt) VALUES ('config', @json, @upd)
                  ON CONFLICT(Slug) DO UPDATE SET Json = @json, UpdatedAt = @upd",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@json", json ?? "");
                    cmd.Parameters.AddWithValue("@upd", updatedAtMs);
                }).ConfigureAwait(false);
        }
    }
}
