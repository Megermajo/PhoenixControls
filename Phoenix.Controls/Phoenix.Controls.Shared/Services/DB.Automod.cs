using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Shared.Services
{
    // Automod persistence — the DB half of the spam-filter / automod pre-build tool.
    //
    // STORAGE SPLIT (locked decision — DIFFERS from the audit doc):
    //   * AutomodConfig  = SYSTEM table, one JSON blob (ON CONFLICT(Slug), legal —
    //     Slug is a real PK). Created in DB.Initialize; registered in _systemTables.
    //   * AutomodLog     = SYSTEM table, append-only audit for the Activity tab
    //     (tool-internal, matching GiveawayActivity/TimerActivity precedent).
    //     Created in DB.Initialize; registered in _systemTables.
    //   * AutomodStrikes = OPEN user table (rowid/name/count/last_ts). DELIBERATELY
    //     NOT a system table so a streamer keeps full db.* access (build !warnings /
    //     !pardon). Created lazily by AutomodService (EnsureAutomodStrikesTableAsync).
    //
    // The DB is the single source of truth for strikes — never an in-memory cache —
    // because db.* scripts and this tool write the same open table. The escalation
    // strike bump is ONE transaction under the shared AcquireLockAsync guard, rowid-
    // scoped (dup-name safe, NEVER ON CONFLICT(name)), with the strike DECAY computed
    // inside the transaction so a concurrent write can't race the read-modify-write.
    // CoerceBalance / IsMissingTableOrColumn are shared with DB.Loyalty / DB.Counters.
    public partial class DB
    {
        /// <summary>Fixed name of the OPEN strikes table. NOT a system table (db.* readable).</summary>
        internal const string AutomodStrikesTable = "AutomodStrikes";

        internal const string AutomodTablesDdl = @"
            CREATE TABLE IF NOT EXISTS AutomodConfig (
                Slug      TEXT PRIMARY KEY,
                Json      TEXT    NOT NULL,
                UpdatedAt INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS AutomodLog (
                Id       INTEGER PRIMARY KEY AUTOINCREMENT,
                Time     TEXT NOT NULL,
                Name     TEXT NOT NULL,
                Platform TEXT NOT NULL,
                Rule     TEXT NOT NULL,
                Action   TEXT NOT NULL,
                Detail   TEXT NOT NULL
            );";

        // Symmetry with EnsureCounters/Loyalty/Timer/GiveawaySchemaMigrations: back-fill
        // any column added after an earlier dev build. Fresh installs no-op through the
        // probe. Both SYSTEM tables (AutomodConfig + AutomodLog) are probed here.
        private void EnsureAutomodSchemaMigrations()
        {
            void Backfill(string table, (string Column, string Ddl)[] wanted)
            {
                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var probe = new SqliteCommand($"PRAGMA table_info({table})", _connection))
                using (var r = probe.ExecuteReader())
                {
                    while (r.Read()) existing.Add(r.GetString(1));
                }
                foreach (var (column, ddl) in wanted)
                {
                    if (existing.Contains(column)) continue;
                    using var alter = new SqliteCommand(ddl, _connection);
                    alter.ExecuteNonQuery();
                }
            }

            Backfill("AutomodConfig", new (string, string)[]
            {
                ("Json",      "ALTER TABLE AutomodConfig ADD COLUMN Json TEXT DEFAULT ''"),
                ("UpdatedAt", "ALTER TABLE AutomodConfig ADD COLUMN UpdatedAt INTEGER NOT NULL DEFAULT 0"),
            });
            Backfill("AutomodLog", new (string, string)[]
            {
                ("Time",     "ALTER TABLE AutomodLog ADD COLUMN Time TEXT DEFAULT ''"),
                ("Name",     "ALTER TABLE AutomodLog ADD COLUMN Name TEXT DEFAULT ''"),
                ("Platform", "ALTER TABLE AutomodLog ADD COLUMN Platform TEXT DEFAULT ''"),
                ("Rule",     "ALTER TABLE AutomodLog ADD COLUMN Rule TEXT DEFAULT ''"),
                ("Action",   "ALTER TABLE AutomodLog ADD COLUMN Action TEXT DEFAULT ''"),
                ("Detail",   "ALTER TABLE AutomodLog ADD COLUMN Detail TEXT DEFAULT ''"),
            });
        }

        // ── Config blob (system table) ──────────────────────────────────────

        /// <summary>Loads the serialized AutomodConfig JSON, or null when unset.</summary>
        public async Task<string?> LoadAutomodConfigAsync()
            => await QueryScalarAsync<string>(
                "SELECT Json FROM AutomodConfig WHERE Slug = 'config' LIMIT 1", _ => { }).ConfigureAwait(false);

        /// <summary>Persists the serialized AutomodConfig JSON.</summary>
        public async Task SaveAutomodConfigAsync(string json, long updatedAtMs)
        {
            await ExecuteAsync(
                @"INSERT INTO AutomodConfig (Slug, Json, UpdatedAt) VALUES ('config', @json, @upd)
                  ON CONFLICT(Slug) DO UPDATE SET Json = @json, UpdatedAt = @upd",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@json", json ?? "");
                    cmd.Parameters.AddWithValue("@upd", updatedAtMs);
                }).ConfigureAwait(false);
        }

        // ── Append-only audit log (system table) ────────────────────────────

        /// <summary>Appends one moderation-audit row.</summary>
        public async Task LogAutomodAsync(string isoTime, string name, string platform, string rule, string action, string detail)
        {
            await ExecuteAsync(
                "INSERT INTO AutomodLog (Time, Name, Platform, Rule, Action, Detail) VALUES (@t, @n, @p, @r, @a, @d)",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@t", isoTime ?? "");
                    cmd.Parameters.AddWithValue("@n", name ?? "");
                    cmd.Parameters.AddWithValue("@p", platform ?? "");
                    cmd.Parameters.AddWithValue("@r", rule ?? "");
                    cmd.Parameters.AddWithValue("@a", action ?? "");
                    cmd.Parameters.AddWithValue("@d", detail ?? "");
                }).ConfigureAwait(false);
        }

        /// <summary>Recent audit rows, newest first.</summary>
        public async Task<List<AutomodLogEntry>> GetAutomodLogAsync(int limit = 100)
        {
            var list = new List<AutomodLogEntry>();
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand(
                    "SELECT Id, Time, Name, Platform, Rule, Action, Detail FROM AutomodLog ORDER BY Id DESC LIMIT @lim",
                    _connection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                cmd.Parameters.AddWithValue("@lim", limit);
                using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await r.ReadAsync().ConfigureAwait(false))
                {
                    list.Add(new AutomodLogEntry
                    {
                        Id       = r.IsDBNull(0) ? 0  : r.GetInt64(0),
                        Time     = r.IsDBNull(1) ? "" : r.GetString(1),
                        Name     = r.IsDBNull(2) ? "" : r.GetString(2),
                        Platform = r.IsDBNull(3) ? "" : r.GetString(3),
                        Rule     = r.IsDBNull(4) ? "" : r.GetString(4),
                        Action   = r.IsDBNull(5) ? "" : r.GetString(5),
                        Detail   = r.IsDBNull(6) ? "" : r.GetString(6),
                    });
                }
            }
            finally { ReleaseLock(taken); }
            return list;
        }

        // ── Open strikes table ──────────────────────────────────────────────

        /// <summary>Creates the OPEN "AutomodStrikes" table if missing. Never a system
        /// table — full db.* access (a streamer builds !warnings / !pardon) is the point.
        /// A pre-existing user table of the same name is left untouched.</summary>
        public async Task EnsureAutomodStrikesTableAsync()
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand(
                    $"CREATE TABLE IF NOT EXISTS [{AutomodStrikesTable}] (rowid INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, [count] INTEGER, last_ts INTEGER);",
                    _connection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>
        /// Atomic escalation strike bump. ONE transaction under the shared lock:
        /// read (count, last_ts) → decay <c>count</c> by one per <paramref name="decayHours"/>
        /// elapsed since last_ts (floor 0) → newCount = decayed + 1 → write
        /// (newCount, <paramref name="nowUnixSec"/>) → return newCount. Rowid-scoped
        /// emulated upsert (dup-name safe; NEVER ON CONFLICT(name)). The DB is the
        /// single truth — no in-memory strike cache.
        /// </summary>
        public async Task<long> AutomodStrikeBumpAsync(string name, long nowUnixSec, double decayHours)
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var tx = _connection!.BeginTransaction();
                try
                {
                    var (count, lastTs) = await ReadStrikeTx(tx, name).ConfigureAwait(false);
                    long decayed = DecayStrikes(count, lastTs, nowUnixSec, decayHours);
                    long newCount = decayed + 1;
                    await WriteStrikeTx(tx, name, newCount, nowUnixSec).ConfigureAwait(false);
                    tx.Commit();
                    return newCount;
                }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>Pure decay helper (also used by the effective-strike read): reduce
        /// <paramref name="count"/> by one per <paramref name="decayHours"/> elapsed
        /// since <paramref name="lastTs"/>, floored at 0. decayHours &lt;= 0 disables decay.</summary>
        internal static long DecayStrikes(long count, long lastTs, long nowUnixSec, double decayHours)
        {
            if (count <= 0 || decayHours <= 0 || lastTs <= 0 || nowUnixSec <= lastTs) return Math.Max(0, count);
            double hours = (nowUnixSec - lastTs) / 3600.0;
            long steps = (long)Math.Floor(hours / decayHours);
            return steps <= 0 ? count : Math.Max(0, count - steps);
        }

        /// <summary>RAW stored strike count for a name (0 when the row/table is missing) —
        /// no decay is applied. Only correct for callers that genuinely want the stored
        /// number (an "as banked" report); anything that has to agree with what
        /// <see cref="AutomodStrikeBumpAsync"/> would do must use
        /// <see cref="AutomodStrikeReadAsync"/> and decay the pair.</summary>
        public async Task<long> AutomodStrikeGetAsync(string name)
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    var (count, _) = await ReadStrikeTx(null, name).ConfigureAwait(false);
                    return count;
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return 0; }
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>Raw stored strike row — (count, last_ts), both 0 when the row/table is
        /// missing. This is the read-only seam for anything that must mirror enforcement:
        /// the bump transaction decays BEFORE it adds, so a caller holding only the raw
        /// count is a full decay window wrong for an idle offender. Decay the pair with
        /// <see cref="DecayStrikes"/> (or its Hub-side mirror) to get the effective count.
        /// Same no-transaction read as <see cref="AutomodStrikeGetAsync"/> — no new SQL.</summary>
        public async Task<(long Count, long LastTs)> AutomodStrikeReadAsync(string name)
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try { return await ReadStrikeTx(null, name).ConfigureAwait(false); }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return (0, 0); }
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>Resets a user's strikes (Pardon) — deletes the row (no-op if absent).</summary>
        public async Task AutomodStrikePardonAsync(string name)
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    using var cmd = new SqliteCommand(
                        $"DELETE FROM [{AutomodStrikesTable}] WHERE [name] = @u COLLATE NOCASE", _connection);
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    cmd.Parameters.AddWithValue("@u", name ?? "");
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { /* nothing to pardon */ }
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>All strike rows (name, raw count, last_ts), highest count first.
        /// Empty when the table is missing.</summary>
        public async Task<List<(string Name, long Count, long LastTs)>> AutomodStrikesListAsync()
        {
            var list = new List<(string, long, long)>();
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    using var cmd = new SqliteCommand(
                        $"SELECT [name], [count], last_ts FROM [{AutomodStrikesTable}] ORDER BY [count] DESC, [name] COLLATE NOCASE", _connection);
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                    while (await r.ReadAsync().ConfigureAwait(false))
                    {
                        string n = r.IsDBNull(0) ? "" : r.GetString(0);
                        long c = CoerceBalance(r.IsDBNull(1) ? null : r.GetValue(1));
                        long ts = r.IsDBNull(2) ? 0 : CoerceBalance(r.GetValue(2));
                        list.Add((n, c, ts));
                    }
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { /* empty */ }
            }
            finally { ReleaseLock(taken); }
            return list;
        }

        // ── Transaction helpers (rowid-scoped emulated upsert; NO UNIQUE on name,
        //    so ON CONFLICT(name) would be invalid). Mirror DB.Counters. ──────────

        private async Task<(long Count, long LastTs)> ReadStrikeTx(SqliteTransaction? tx, string name)
        {
            using var cmd = new SqliteCommand(
                $"SELECT [count], last_ts FROM [{AutomodStrikesTable}] WHERE [name] = @u COLLATE NOCASE LIMIT 1", _connection, tx);
            cmd.CommandTimeout = CommandTimeoutSeconds;
            cmd.Parameters.AddWithValue("@u", name ?? "");
            using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            if (await r.ReadAsync().ConfigureAwait(false))
            {
                long c = CoerceBalance(r.IsDBNull(0) ? null : r.GetValue(0));
                long ts = r.IsDBNull(1) ? 0 : CoerceBalance(r.GetValue(1));
                return (c, ts);
            }
            return (0, 0);
        }

        private async Task WriteStrikeTx(SqliteTransaction tx, string name, long count, long lastTs)
        {
            int rows;
            using (var up = new SqliteCommand(
                $"UPDATE [{AutomodStrikesTable}] SET [count] = @c, last_ts = @t " +
                $"WHERE rowid = (SELECT rowid FROM [{AutomodStrikesTable}] WHERE [name] = @u COLLATE NOCASE LIMIT 1)",
                _connection, tx))
            {
                up.CommandTimeout = CommandTimeoutSeconds;
                up.Parameters.AddWithValue("@c", count);
                up.Parameters.AddWithValue("@t", lastTs);
                up.Parameters.AddWithValue("@u", name ?? "");
                rows = await up.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            if (rows == 0)
            {
                using var ins = new SqliteCommand(
                    $"INSERT INTO [{AutomodStrikesTable}] ([name], [count], last_ts) VALUES (@u, @c, @t)", _connection, tx);
                ins.CommandTimeout = CommandTimeoutSeconds;
                ins.Parameters.AddWithValue("@u", name ?? "");
                ins.Parameters.AddWithValue("@c", count);
                ins.Parameters.AddWithValue("@t", lastTs);
                await ins.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
    }
}
