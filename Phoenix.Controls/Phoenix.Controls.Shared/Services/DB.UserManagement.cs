using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.Threading.Tasks;

namespace Phoenix.Controls.Shared.Services
{
    // User-Management persistence — the DB half of the welcoming/greeting/groups
    // pre-build tool.
    //
    // Three SYSTEM tables:
    //   UserMgmtConfig   — the whole tool state as ONE private JSON blob (Slug='config'),
    //                      mirroring SchedulingConfig/LoyaltyConfig.
    //   UserMgmtSeen     — the "already welcomed this stream" set. Persisted (not just
    //                      in-memory) so a mid-stream Hub restart does NOT re-greet
    //                      everyone; cleared when the stream goes live (the per-stream
    //                      reset) — see UserManagementService.SetStreamLive.
    //   UserMgmtSeenEver — the LIFETIME known-chatters set backing the first-time
    //                      greeting. Never cleared by the stream lifecycle; only the
    //                      panel's explicit "reset memory" action empties it.
    //
    // All three are tool-private runtime state, not user-scriptable data, so SYSTEM
    // tables are correct (a streamer building their own greeter in Architect keeps using
    // their own open tables, exactly like the reference Welcome.phxg does today).
    //
    // The tool's FOURTH part, the viewer queue, is the deliberate exception and lives
    // NOWHERE in this file: its entries are rows in the OPEN "Queues" table (DB.Queues.cs)
    // because the queue IS a named queue of the generic Queue.* node band, not a private
    // store. That is what makes !join and queue.push("…", …, "<QueueName>") the same
    // write, and it is why "Queues" is absent from DB._systemTables.
    public partial class DB
    {
        internal const string UserMgmtConfigTablesDdl = @"
            CREATE TABLE IF NOT EXISTS UserMgmtConfig (
                Slug      TEXT PRIMARY KEY,
                Json      TEXT    NOT NULL,
                UpdatedAt INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS UserMgmtSeen (
                Name TEXT PRIMARY KEY
            );
            CREATE TABLE IF NOT EXISTS UserMgmtSeenEver (
                Name        TEXT PRIMARY KEY,
                FirstSeenAt INTEGER NOT NULL DEFAULT 0
            );";

        // Symmetry with EnsureScheduling/Counters/LoyaltySchemaMigrations: back-fill any
        // column added after an earlier dev build. Fresh installs no-op through the probe.
        private void EnsureUserMgmtSchemaMigrations()
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var probe = new SqliteCommand("PRAGMA table_info(UserMgmtConfig)", _connection))
            using (var r = probe.ExecuteReader())
            {
                while (r.Read()) existing.Add(r.GetString(1));
            }
            var wanted = new (string Column, string Ddl)[]
            {
                ("Json",      "ALTER TABLE UserMgmtConfig ADD COLUMN Json TEXT DEFAULT ''"),
                ("UpdatedAt", "ALTER TABLE UserMgmtConfig ADD COLUMN UpdatedAt INTEGER NOT NULL DEFAULT 0"),
            };
            foreach (var (column, ddl) in wanted)
            {
                if (existing.Contains(column)) continue;
                using var alter = new SqliteCommand(ddl, _connection);
                alter.ExecuteNonQuery();
            }
        }

        // ── Config blob (system table) ──────────────────────────────────────

        /// <summary>Loads the serialized UserManagementConfig JSON, or null when unset.</summary>
        public async Task<string?> LoadUserMgmtConfigAsync()
            => await QueryScalarAsync<string>(
                "SELECT Json FROM UserMgmtConfig WHERE Slug = 'config' LIMIT 1", _ => { }).ConfigureAwait(false);

        /// <summary>Persists the serialized UserManagementConfig JSON.</summary>
        public async Task SaveUserMgmtConfigAsync(string json, long updatedAtMs)
        {
            await ExecuteAsync(
                @"INSERT INTO UserMgmtConfig (Slug, Json, UpdatedAt) VALUES ('config', @json, @upd)
                  ON CONFLICT(Slug) DO UPDATE SET Json = @json, UpdatedAt = @upd",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@json", json ?? "");
                    cmd.Parameters.AddWithValue("@upd", updatedAtMs);
                }).ConfigureAwait(false);
        }

        // ── Welcomed-set (system table) ─────────────────────────────────────

        /// <summary>All logins already welcomed this stream (lowercased at write time).</summary>
        public async Task<List<string>> LoadUserMgmtSeenAsync()
        {
            var list = new List<string>();
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand("SELECT Name FROM UserMgmtSeen", _connection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await r.ReadAsync().ConfigureAwait(false))
                {
                    if (!r.IsDBNull(0)) list.Add(r.GetString(0));
                }
            }
            finally { ReleaseLock(taken); }
            return list;
        }

        /// <summary>Marks one login as welcomed (idempotent).</summary>
        public async Task AddUserMgmtSeenAsync(string nameLower)
        {
            if (string.IsNullOrWhiteSpace(nameLower)) return;
            await ExecuteAsync(
                "INSERT OR IGNORE INTO UserMgmtSeen (Name) VALUES (@n)",
                cmd => cmd.Parameters.AddWithValue("@n", nameLower)).ConfigureAwait(false);
        }

        /// <summary>Clears the welcomed-set (the per-stream reset on going live).</summary>
        public async Task ClearUserMgmtSeenAsync()
            => await ExecuteAsync("DELETE FROM UserMgmtSeen", _ => { }).ConfigureAwait(false);

        // ── Lifetime known-chatters set (system table) ──────────────────────

        /// <summary>All logins ever seen chatting while the tool was enabled
        /// (lowercased at write time). Backs the once-ever first-time greeting.</summary>
        public async Task<List<string>> LoadUserMgmtSeenEverAsync()
        {
            var list = new List<string>();
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand("SELECT Name FROM UserMgmtSeenEver", _connection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await r.ReadAsync().ConfigureAwait(false))
                {
                    if (!r.IsDBNull(0)) list.Add(r.GetString(0));
                }
            }
            finally { ReleaseLock(taken); }
            return list;
        }

        /// <summary>
        /// The most recently first-seen chatters, newest first, with the timestamp
        /// <see cref="AddUserMgmtSeenEverAsync"/> stamped on them.
        ///
        /// <para>Exists because <see cref="LoadUserMgmtSeenEverAsync"/> selects only Name and
        /// drops FirstSeenAt on the floor — the column has been written since the table was
        /// created, so "when did this viewer first turn up" is already recorded and simply
        /// was not readable. That set-shaped read stays as it is: it feeds the once-ever
        /// greeting gate, which wants a HashSet and nothing else.</para>
        ///
        /// <para>Rows written by a build that predates the column read 0; they sort last
        /// rather than being hidden, so a caller never silently loses a known chatter.</para>
        /// </summary>
        /// <param name="limit">Maximum rows; values below 1 return nothing.</param>
        public async Task<List<(string Name, long FirstSeenAtMs)>> LoadUserMgmtSeenEverRecentAsync(int limit)
        {
            var list = new List<(string, long)>();
            if (limit < 1) return list;
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand(
                    "SELECT Name, FirstSeenAt FROM UserMgmtSeenEver ORDER BY FirstSeenAt DESC, Name ASC LIMIT @lim",
                    _connection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                cmd.Parameters.AddWithValue("@lim", limit);
                using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await r.ReadAsync().ConfigureAwait(false))
                {
                    if (r.IsDBNull(0)) continue;
                    // Coerced, not GetInt64. UserMgmtSeenEver became an OPEN table
                    // in the 2026-08 unlock and db.set_cell binds TEXT, so a
                    // FirstSeenAt of "yesterday" is now reachable. It sorts last
                    // (0) instead of throwing and blanking the panel — the same
                    // treatment rows written before the column existed already get.
                    list.Add((CellText((SqliteDataReader)r, 0),
                              r.IsDBNull(1) ? 0L : CoerceBalance(r.GetValue(1))));
                }
            }
            finally { ReleaseLock(taken); }
            return list;
        }

        /// <summary>Marks one login as known forever (idempotent — the first-seen
        /// timestamp of an existing row is never overwritten).</summary>
        public async Task AddUserMgmtSeenEverAsync(string nameLower, long firstSeenAtMs)
        {
            if (string.IsNullOrWhiteSpace(nameLower)) return;
            await ExecuteAsync(
                "INSERT OR IGNORE INTO UserMgmtSeenEver (Name, FirstSeenAt) VALUES (@n, @ts)",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@n", nameLower);
                    cmd.Parameters.AddWithValue("@ts", firstSeenAtMs);
                }).ConfigureAwait(false);
        }

        /// <summary>Forgets every known chatter (the panel's explicit reset — after
        /// this, everyone is greeted as brand-new again). Never called by lifecycle.</summary>
        public async Task ClearUserMgmtSeenEverAsync()
            => await ExecuteAsync("DELETE FROM UserMgmtSeenEver", _ => { }).ConfigureAwait(false);
    }
}
