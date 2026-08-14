using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.Threading.Tasks;

namespace Phoenix.Controls.Shared.Services
{
    // Counters persistence — the DB half of the named-counters pre-build tool.
    //
    // OPEN-TABLE DOCTRINE (same as DB.Loyalty.cs): the live COUNTS live in an OPEN
    // user table ("Counters", name/count shape) DELIBERATELY NOT in DB._systemTables,
    // so a streamer keeps full db.* script access to read/build on the counts (e.g. a
    // custom !milestone command). Only the tool's private CountersConfig (a single
    // JSON blob, mirroring LoyaltyConfig/Timers.Json) is a system table. The DB is the
    // single source of truth for counts — never an in-memory cache — because db.*
    // scripts and this tool write the same open table; every mutation is one
    // transaction under the shared AcquireLockAsync guard, rowid-scoped (dup-name
    // safe). Floor-at-zero is enforced ATOMICALLY inside the transaction so a
    // concurrent write can't race the read-modify-write. CoerceBalance /
    // IsMissingTableOrColumn / IsValidIdentifier / IsAppOwnedTable are shared with
    // DB.Loyalty.cs / DB.Giveaway.cs.
    public partial class DB
    {
        /// <summary>Fixed name of the OPEN counts table. NOT a system table (db.* readable).</summary>
        internal const string CountersTable = "Counters";

        internal const string CountersConfigTablesDdl = @"
            CREATE TABLE IF NOT EXISTS CountersConfig (
                Slug      TEXT PRIMARY KEY,
                Json      TEXT    NOT NULL,
                UpdatedAt INTEGER NOT NULL DEFAULT 0
            );";

        // Symmetry with EnsureLoyalty/Timer/GiveawaySchemaMigrations: back-fill any
        // column added after an earlier dev build. Fresh installs no-op through the probe.
        private void EnsureCountersSchemaMigrations()
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var probe = new SqliteCommand("PRAGMA table_info(CountersConfig)", _connection))
            using (var r = probe.ExecuteReader())
            {
                while (r.Read()) existing.Add(r.GetString(1));
            }
            var wanted = new (string Column, string Ddl)[]
            {
                ("Json",      "ALTER TABLE CountersConfig ADD COLUMN Json TEXT DEFAULT ''"),
                ("UpdatedAt", "ALTER TABLE CountersConfig ADD COLUMN UpdatedAt INTEGER NOT NULL DEFAULT 0"),
            };
            foreach (var (column, ddl) in wanted)
            {
                if (existing.Contains(column)) continue;
                using var alter = new SqliteCommand(ddl, _connection);
                alter.ExecuteNonQuery();
            }
        }

        // ── Config blob (system table) ──────────────────────────────────────

        /// <summary>Loads the serialized CountersConfig JSON, or null when unset.</summary>
        public async Task<string?> LoadCountersConfigAsync()
            => await QueryScalarAsync<string>(
                "SELECT Json FROM CountersConfig WHERE Slug = 'config' LIMIT 1", _ => { }).ConfigureAwait(false);

        /// <summary>Persists the serialized CountersConfig JSON.</summary>
        public async Task SaveCountersConfigAsync(string json, long updatedAtMs)
        {
            await ExecuteAsync(
                @"INSERT INTO CountersConfig (Slug, Json, UpdatedAt) VALUES ('config', @json, @upd)
                  ON CONFLICT(Slug) DO UPDATE SET Json = @json, UpdatedAt = @upd",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@json", json ?? "");
                    cmd.Parameters.AddWithValue("@upd", updatedAtMs);
                }).ConfigureAwait(false);
        }

        // ── Open counts table ───────────────────────────────────────────────

        /// <summary>Creates the OPEN "Counters" (name/count) table if missing. Never a
        /// system table — full db.* access is the point. A pre-existing user table of
        /// the same name is left untouched.</summary>
        public async Task EnsureCountersTableAsync()
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                // `count` is NOT NULL DEFAULT 0 so a freshly-created table can never
                // reach the NULL state at all: a script's db.insert_row that names only
                // the counter gets 0 rather than NULL. (The COALESCE hardening in
                // AddCountTx is what heals a table that PREDATES this DDL, or one the
                // streamer built by hand — CREATE TABLE IF NOT EXISTS leaves those
                // untouched.)
                using var cmd = new SqliteCommand(
                    $"CREATE TABLE IF NOT EXISTS [{CountersTable}] (rowid INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, [count] INTEGER NOT NULL DEFAULT 0);",
                    _connection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>Current count for a name (0 when the row / table / column is missing).</summary>
        public async Task<long> CounterGetAsync(string name)
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    using var cmd = new SqliteCommand(
                        $"SELECT [count] FROM [{CountersTable}] WHERE [name] = @u COLLATE NOCASE LIMIT 1", _connection);
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    cmd.Parameters.AddWithValue("@u", name ?? "");
                    return CoerceBalance(await cmd.ExecuteScalarAsync().ConfigureAwait(false));
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return 0; }
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>Atomically adds <paramref name="delta"/> (clamped at 0 when
        /// <paramref name="floorAtZero"/>). Returns the resulting value.</summary>
        public async Task<long> CounterAddAsync(string name, long delta, bool floorAtZero)
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var tx = _connection!.BeginTransaction();
                try
                {
                    long result;
                    if (floorAtZero)
                    {
                        long cur = await ReadCountTx(tx, name).ConfigureAwait(false);
                        result = Math.Max(0, cur + delta);
                        await SetCountTx(tx, name, result).ConfigureAwait(false);
                    }
                    else
                    {
                        await AddCountTx(tx, name, delta).ConfigureAwait(false);
                        result = await ReadCountTx(tx, name).ConfigureAwait(false);
                    }
                    tx.Commit();
                    return result;
                }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>Atomically sets the count to <paramref name="value"/> (already
        /// floor-adjusted by the caller when required). Returns the value.</summary>
        public async Task<long> CounterSetAsync(string name, long value)
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var tx = _connection!.BeginTransaction();
                try
                {
                    await SetCountTx(tx, name, value).ConfigureAwait(false);
                    tx.Commit();
                    return value;
                }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>All managed rows (name + count), ordered by name. Empty when the
        /// table is missing.</summary>
        public async Task<List<(string Name, long Count)>> CounterListAsync()
        {
            var list = new List<(string, long)>();
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    using var cmd = new SqliteCommand(
                        $"SELECT [name], [count] FROM [{CountersTable}] ORDER BY [name] COLLATE NOCASE", _connection);
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                    while (await r.ReadAsync().ConfigureAwait(false))
                    {
                        string n = r.IsDBNull(0) ? "" : r.GetString(0);
                        // CoerceBalance (not Convert.ToInt64) matches every other read of
                        // this OPEN table (CounterGetAsync / ReadCountTx) + DB.Loyalty.cs:
                        // a db.*-stored non-integer / BLOB coerces to 0 instead of throwing
                        // an uncaught FormatException that would blank the whole list, and a
                        // stored REAL floors identically to the live reads.
                        long c = CoerceBalance(r.IsDBNull(1) ? null : r.GetValue(1));
                        list.Add((n, c));
                    }
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { /* empty */ }
            }
            finally { ReleaseLock(taken); }
            return list;
        }

        /// <summary>Deletes a counter's row(s) from the open table. Returns HOW MANY rows went:
        /// 0 for an unknown name, and 0 (never a throw) when the table itself is missing.
        ///
        /// The count is the return value rather than a discarded side effect because
        /// <c>CountersService.DeleteAsync</c> has no other way to tell a real delete from a
        /// no-op, and firing the delete side-effects on a name that was never there raises a
        /// phantom Counter.OnChanged and mints a permanent live-channel key.
        ///
        /// Deliberately NOT rowid-scoped, unlike the add/set upserts: those mutate ONE row so a
        /// hand-built table's duplicate-name rows are never double-counted, whereas "delete this
        /// counter" has to leave none of them behind.</summary>
        public async Task<int> CounterDeleteAsync(string name)
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    using var cmd = new SqliteCommand(
                        $"DELETE FROM [{CountersTable}] WHERE [name] = @u COLLATE NOCASE", _connection);
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    cmd.Parameters.AddWithValue("@u", name ?? "");
                    return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return 0; }
            }
            finally { ReleaseLock(taken); }
        }

        // ── Transaction helpers (rowid-scoped emulated upsert; the open table has
        //    NO UNIQUE on name, so ON CONFLICT(name) would be invalid). ───────────

        private async Task<long> ReadCountTx(SqliteTransaction tx, string name)
        {
            using var cmd = new SqliteCommand(
                $"SELECT [count] FROM [{CountersTable}] WHERE [name] = @u COLLATE NOCASE LIMIT 1", _connection, tx);
            cmd.CommandTimeout = CommandTimeoutSeconds;
            cmd.Parameters.AddWithValue("@u", name ?? "");
            return CoerceBalance(await cmd.ExecuteScalarAsync().ConfigureAwait(false));
        }

        // ★ COALESCE(CAST(...)) is load-bearing, not defensive dressing — it is the
        // same form DB.Loyalty.CreditDeltaTx / DB.Ranks.AddValueTx use. "Counters" is
        // OPEN, so a row can exist with a NULL (or text) count cell: db.insert_row
        // naming only the counter writes no count column at all, and db.add_column on
        // a populated table NULLs every pre-existing row (ALTER TABLE ADD COLUMN emits
        // no DEFAULT). A bare `[count] = [count] + @d` evaluates to NULL for such a
        // row while STILL reporting changes()==1, so the INSERT fallback below never
        // fires, the cell stays NULL forever (CoerceBalance reads it as 0), and every
        // increment is swallowed while the caller is told it landed. Coercing first is
        // what makes the first add repair the row instead of losing counts in it.
        // (The floor-at-zero branch in CounterAddAsync needs no COALESCE: it is
        // read-compute-set — ReadCountTx coerces NULL to 0 in C# and SetCountTx
        // writes a plain parameter; no SQL arithmetic ever touches the cell there.)
        private async Task AddCountTx(SqliteTransaction tx, string name, long delta)
        {
            int rows;
            using (var up = new SqliteCommand(
                $"UPDATE [{CountersTable}] SET [count] = COALESCE(CAST([count] AS INTEGER), 0) + @d " +
                $"WHERE rowid = (SELECT rowid FROM [{CountersTable}] WHERE [name] = @u COLLATE NOCASE LIMIT 1)",
                _connection, tx))
            {
                up.CommandTimeout = CommandTimeoutSeconds;
                up.Parameters.AddWithValue("@d", delta);
                up.Parameters.AddWithValue("@u", name ?? "");
                rows = await up.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            if (rows == 0)
            {
                using var ins = new SqliteCommand(
                    $"INSERT INTO [{CountersTable}] ([name], [count]) VALUES (@u, @d)", _connection, tx);
                ins.CommandTimeout = CommandTimeoutSeconds;
                ins.Parameters.AddWithValue("@u", name ?? "");
                ins.Parameters.AddWithValue("@d", delta);
                await ins.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        private async Task SetCountTx(SqliteTransaction tx, string name, long value)
        {
            int rows;
            using (var up = new SqliteCommand(
                $"UPDATE [{CountersTable}] SET [count] = @v " +
                $"WHERE rowid = (SELECT rowid FROM [{CountersTable}] WHERE [name] = @u COLLATE NOCASE LIMIT 1)",
                _connection, tx))
            {
                up.CommandTimeout = CommandTimeoutSeconds;
                up.Parameters.AddWithValue("@v", value);
                up.Parameters.AddWithValue("@u", name ?? "");
                rows = await up.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            if (rows == 0)
            {
                using var ins = new SqliteCommand(
                    $"INSERT INTO [{CountersTable}] ([name], [count]) VALUES (@u, @v)", _connection, tx);
                ins.CommandTimeout = CommandTimeoutSeconds;
                ins.Parameters.AddWithValue("@u", name ?? "");
                ins.Parameters.AddWithValue("@v", value);
                await ins.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
    }
}
