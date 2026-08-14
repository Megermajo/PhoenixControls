using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.Threading.Tasks;

namespace Phoenix.Controls.Shared.Services
{
    // Polls & Betting persistence — the DB half of the chat-poll + points-betting tool.
    //
    // Like Scheduling, Alerts and Song Request, and UNLIKE Counters/Loyalty/Quotes, there
    // is NO open user table. The reason is not symmetry, it is correctness: the live poll
    // is a countdown against wall-clock, and a restart cannot resume one — a reloaded poll
    // would report seconds remaining that stopped ticking while the Hub was down. The poll
    // therefore lives only in PollsService, and the whole persisted state is ONE private
    // JSON blob (command words, role gates, durations, betting limits, overlay switch) in
    // the PollsConfig SYSTEM table, Slug='config' — mirroring SongRequestConfig.
    //
    // No money lives here either: stakes are moved through the Loyalty tool's OPEN balance
    // and ledger tables (DB.Loyalty.cs), so a streamer auditing the currency sees the bet
    // and the payout in the one ledger they already read.
    public partial class DB
    {
        internal const string PollsConfigTablesDdl = @"
            CREATE TABLE IF NOT EXISTS PollsConfig (
                Slug      TEXT PRIMARY KEY,
                Json      TEXT    NOT NULL,
                UpdatedAt INTEGER NOT NULL DEFAULT 0
            );";

        // Symmetry with EnsureSongRequest/Scheduling/Alerts/Quotes/TimerSchemaMigrations:
        // back-fill any column added after an earlier dev build. Fresh installs no-op
        // through the probe.
        private void EnsurePollsSchemaMigrations()
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var probe = new SqliteCommand("PRAGMA table_info(PollsConfig)", _connection))
            using (var r = probe.ExecuteReader())
            {
                while (r.Read()) existing.Add(r.GetString(1));
            }
            var wanted = new (string Column, string Ddl)[]
            {
                ("Json",      "ALTER TABLE PollsConfig ADD COLUMN Json TEXT DEFAULT ''"),
                ("UpdatedAt", "ALTER TABLE PollsConfig ADD COLUMN UpdatedAt INTEGER NOT NULL DEFAULT 0"),
            };
            foreach (var (column, ddl) in wanted)
            {
                if (existing.Contains(column)) continue;
                using var alter = new SqliteCommand(ddl, _connection);
                alter.ExecuteNonQuery();
            }
        }

        // ── Config blob (system table) ──────────────────────────────────────

        /// <summary>Loads the serialized PollsConfig JSON, or null when unset.</summary>
        public async Task<string?> LoadPollsConfigAsync()
            => await QueryScalarAsync<string>(
                "SELECT Json FROM PollsConfig WHERE Slug = 'config' LIMIT 1", _ => { }).ConfigureAwait(false);

        /// <summary>Persists the serialized PollsConfig JSON.</summary>
        public async Task SavePollsConfigAsync(string json, long updatedAtMs)
        {
            await ExecuteAsync(
                @"INSERT INTO PollsConfig (Slug, Json, UpdatedAt) VALUES ('config', @json, @upd)
                  ON CONFLICT(Slug) DO UPDATE SET Json = @json, UpdatedAt = @upd",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@json", json ?? "");
                    cmd.Parameters.AddWithValue("@upd", updatedAtMs);
                }).ConfigureAwait(false);
        }
    }
}
