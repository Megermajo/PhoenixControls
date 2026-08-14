using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Shared.Services
{
    // Ranks persistence — the DB half of the watch-time / points rank ladder.
    //
    // OPEN-TABLE DOCTRINE (same as DB.Loyalty.cs / DB.Counters.cs / DB.Quotes.cs): only
    // the tool's private RanksConfig blob is a system table. BOTH data tables are OPEN
    // user tables, deliberately NOT in DB._systemTables, so a streamer keeps full db.*
    // script access:
    //
    //   * "WatchTime" (rowid, name, minutes) — the suite's FIRST watch-hour store. It is
    //     fed by the Loyalty tool's existing watch-time tick (one extra batch inside the
    //     same sweep, never a second tick loop) and is what makes a watch-hour threshold
    //     a real rule rather than the streamer note LoyaltyEarnConfig.RegularThresholdHours
    //     has always been.
    //   * "Ranks" (rowid, name, rank, value) — the LAST rank each viewer was evaluated at.
    //     Without it a Hub restart would re-announce every viewer's current rank on the
    //     next tick, because "did they just cross a threshold?" is only answerable against
    //     a remembered previous answer.
    //
    // Neither table has a UNIQUE constraint on [name] (none of the open tool tables do,
    // and a hand-built one would not either), so ON CONFLICT(name) is INVALID at runtime
    // rather than merely inadvisable. Every write here is therefore the house emulated
    // upsert: a rowid-scoped UPDATE, then an INSERT only when zero rows were touched —
    // rowid-scoping is what keeps duplicate-name rows in a hand-built table from being
    // double-mutated. Copy-from: DB.Loyalty.CreditDeltaTx / DB.Counters.AddCountTx.
    //
    // Every read coerces defensively (CoerceBalance) and degrades a missing table/column
    // to the empty answer (IsMissingTableOrColumn), because an OPEN table means a db.*
    // script can have left text, a BLOB or an out-of-range number in any cell.
    // IsValidIdentifier / IsAppOwnedTable / CoerceBalance / IsMissingTableOrColumn are
    // shared with the sibling DB partials.
    public partial class DB
    {
        /// <summary>Fixed name of the OPEN watch-minutes table. NOT a system table.</summary>
        internal const string WatchTimeTable = "WatchTime";

        /// <summary>Fixed name of the OPEN last-known-rank table. NOT a system table.</summary>
        internal const string RankStateTable = "Ranks";

        /// <summary>The value column of <see cref="WatchTimeTable"/>.</summary>
        internal const string WatchTimeColumn = "minutes";

        /// <summary>The value column of the Loyalty tool's OPEN balance table (see the
        /// CREATE in <c>DB.Loyalty.EnsureLoyaltyWalletTablesAsync</c>). Named here because
        /// the points metric reads that table, and the whole point of
        /// <see cref="RankMetricSource"/> is that no caller outside this layer has to
        /// know a column name to do it.</summary>
        internal const string LoyaltyBalanceColumn = "currency";

        /// <summary>The watch-minute metric source.</summary>
        public static RankMetricSource WatchTimeSource => new(WatchTimeTable, WatchTimeColumn);

        /// <summary>The Loyalty-balance metric source for a given (streamer-configured,
        /// therefore untrusted) balance table. The reads below validate it.</summary>
        public static RankMetricSource PointsSource(string balanceTable)
            => new(balanceTable ?? "", LoyaltyBalanceColumn);

        internal const string RanksConfigTablesDdl = @"
            CREATE TABLE IF NOT EXISTS RanksConfig (
                Slug      TEXT PRIMARY KEY,
                Json      TEXT    NOT NULL,
                UpdatedAt INTEGER NOT NULL DEFAULT 0
            );";

        // Symmetry with EnsureQuotes/Counters/LoyaltySchemaMigrations: back-fill any
        // column added after an earlier dev build. Fresh installs no-op through the probe.
        private void EnsureRanksSchemaMigrations()
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var probe = new SqliteCommand("PRAGMA table_info(RanksConfig)", _connection))
            using (var r = probe.ExecuteReader())
            {
                while (r.Read()) existing.Add(r.GetString(1));
            }
            var wanted = new (string Column, string Ddl)[]
            {
                ("Json",      "ALTER TABLE RanksConfig ADD COLUMN Json TEXT DEFAULT ''"),
                ("UpdatedAt", "ALTER TABLE RanksConfig ADD COLUMN UpdatedAt INTEGER NOT NULL DEFAULT 0"),
            };
            foreach (var (column, ddl) in wanted)
            {
                if (existing.Contains(column)) continue;
                using var alter = new SqliteCommand(ddl, _connection);
                alter.ExecuteNonQuery();
            }
        }

        // ── Config blob (system table) ──────────────────────────────────────

        /// <summary>Loads the serialized RanksConfig JSON, or null when unset.</summary>
        public async Task<string?> LoadRanksConfigAsync()
            => await QueryScalarAsync<string>(
                "SELECT Json FROM RanksConfig WHERE Slug = 'config' LIMIT 1", _ => { }).ConfigureAwait(false);

        /// <summary>Persists the serialized RanksConfig JSON.</summary>
        public async Task SaveRanksConfigAsync(string json, long updatedAtMs)
        {
            await ExecuteAsync(
                @"INSERT INTO RanksConfig (Slug, Json, UpdatedAt) VALUES ('config', @json, @upd)
                  ON CONFLICT(Slug) DO UPDATE SET Json = @json, UpdatedAt = @upd",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@json", json ?? "");
                    cmd.Parameters.AddWithValue("@upd", updatedAtMs);
                }).ConfigureAwait(false);
        }

        // ── Open data tables ────────────────────────────────────────────────

        /// <summary>Creates the OPEN "WatchTime" and "Ranks" tables if missing. Never
        /// system tables — full db.* access is the point. A pre-existing user table of
        /// either name is left untouched.</summary>
        public async Task EnsureRanksDataTablesAsync()
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using (var cmd = new SqliteCommand(
                    $"CREATE TABLE IF NOT EXISTS [{WatchTimeTable}] (rowid INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, [{WatchTimeColumn}] INTEGER);",
                    _connection))
                {
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                using (var cmd = new SqliteCommand(
                    $"CREATE TABLE IF NOT EXISTS [{RankStateTable}] (rowid INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, rank TEXT, value INTEGER);",
                    _connection))
                {
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
            finally { ReleaseLock(taken); }
        }

        // ── Watch-minute accrual ────────────────────────────────────────────

        /// <summary>
        /// Adds minutes to a whole sweep of viewers in ONE transaction / one lock hold —
        /// the primitive the watch-time tick needs, and the reason accrual can ride the
        /// existing sweep instead of adding a second loop. Blank names and non-positive
        /// amounts are skipped. Returns how many rows were actually credited (0 when the
        /// table is missing, rolled back), so a caller comparing against what it asked for
        /// sees a real failure rather than relying on an exception it would never get.
        /// </summary>
        public async Task<int> WatchTimeAddManyAsync(IReadOnlyList<(string Name, long Minutes)> credits)
        {
            if (credits is null || credits.Count == 0) return 0;
            int applied = 0;
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var tx = _connection!.BeginTransaction();
                try
                {
                    foreach (var (name, minutes) in credits)
                    {
                        if (string.IsNullOrWhiteSpace(name) || minutes <= 0) continue;
                        await AddValueTx(tx, WatchTimeTable, WatchTimeColumn, name, minutes).ConfigureAwait(false);
                        applied++;
                    }
                    tx.Commit();
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { try { tx.Rollback(); } catch { } return 0; }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
            finally { ReleaseLock(taken); }
            return applied;
        }

        /// <summary>
        /// Every row of the OPEN watch-minute table, for the in-memory mirror the
        /// presence sampler keeps so a group's watch-hour rule costs a hash lookup on
        /// the chat path instead of a SQLite round-trip.
        ///
        /// <para>Returns raw (name, minutes) pairs INCLUDING duplicate names — a
        /// hand-built table has no UNIQUE constraint (see the file header), so
        /// collapsing them here would quietly disagree with what a
        /// <c>SELECT * FROM WatchTime</c> shows the streamer. The caller sums.</para>
        ///
        /// <para>A missing table degrades to an empty list, like every other read in
        /// this file: the table is the user's and they may drop it.</para>
        /// </summary>
        public async Task<List<(string Name, long Minutes)>> WatchTimeAllAsync()
        {
            var rows = new List<(string, long)>();
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    using var cmd = new SqliteCommand(
                        $"SELECT [name], [{WatchTimeColumn}] FROM [{WatchTimeTable}] " +
                        "WHERE name IS NOT NULL AND name <> ''", _connection);
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                    while (await r.ReadAsync().ConfigureAwait(false))
                    {
                        string name = r.IsDBNull(0) ? "" : r.GetString(0);
                        if (name.Length == 0) continue;
                        rows.Add((name, CoerceBalance(r.IsDBNull(1) ? null : r.GetValue(1))));
                    }
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return rows; }
            }
            finally { ReleaseLock(taken); }
            return rows;
        }

        /// <summary>
        /// Sets a viewer's accrued minutes outright — the manual correction the
        /// watch-time browser offers, and the only write here that is not a delta.
        ///
        /// <para>Uses the same rowid-scoped emulated upsert as the credit path, so a
        /// hand-built table holding duplicate rows for one name has exactly ONE of
        /// them rewritten rather than all of them — matching how a credit behaves,
        /// which is what keeps the two operations telling the same story.</para>
        ///
        /// <para>Returns false when the open table is missing rather than throwing.</para>
        /// </summary>
        public async Task<bool> WatchTimeSetAsync(string name, long minutes)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (minutes < 0) minutes = 0;
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    int rows;
                    using (var up = new SqliteCommand(
                        $"UPDATE [{WatchTimeTable}] SET [{WatchTimeColumn}] = @m " +
                        $"WHERE rowid = (SELECT rowid FROM [{WatchTimeTable}] WHERE [name] = @u COLLATE NOCASE LIMIT 1)",
                        _connection))
                    {
                        up.CommandTimeout = CommandTimeoutSeconds;
                        up.Parameters.AddWithValue("@m", minutes);
                        up.Parameters.AddWithValue("@u", name);
                        rows = await up.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                    if (rows == 0)
                    {
                        using var ins = new SqliteCommand(
                            $"INSERT INTO [{WatchTimeTable}] ([name], [{WatchTimeColumn}]) VALUES (@u, @m)",
                            _connection);
                        ins.CommandTimeout = CommandTimeoutSeconds;
                        ins.Parameters.AddWithValue("@u", name);
                        ins.Parameters.AddWithValue("@m", minutes);
                        await ins.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                    return true;
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return false; }
            }
            finally { ReleaseLock(taken); }
        }

        // The emulated upsert (see the file header for why ON CONFLICT(name) is invalid
        // here): rowid-scoped UPDATE, INSERT only when nothing was touched. Identical in
        // shape to DB.Loyalty.CreditDeltaTx, parameterised on the value column so the one
        // implementation serves the watch-minute table today and any sibling tomorrow.
        private async Task AddValueTx(SqliteTransaction tx, string table, string column, string name, long delta)
        {
            int rows;
            using (var up = new SqliteCommand(
                $"UPDATE [{table}] SET [{column}] = COALESCE(CAST([{column}] AS INTEGER), 0) + @d " +
                $"WHERE rowid = (SELECT rowid FROM [{table}] WHERE [name] = @u COLLATE NOCASE LIMIT 1)",
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
                    $"INSERT INTO [{table}] ([name], [{column}]) VALUES (@u, @d)", _connection, tx);
                ins.CommandTimeout = CommandTimeoutSeconds;
                ins.Parameters.AddWithValue("@u", name ?? "");
                ins.Parameters.AddWithValue("@d", delta);
                await ins.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        // ── Metric reads (the ladder's input) ───────────────────────────────

        /// <summary>One viewer's metric value (0 when the row / table / column is
        /// missing). The source comes from the tool config — the watch-minute table, or
        /// the Loyalty tool's own OPEN balance table — so it is validated here rather
        /// than trusted.</summary>
        public async Task<long> RankValueAsync(RankMetricSource source, string name)
        {
            string table = source.Table, column = source.Column;
            if (!IsValidIdentifier(table) || IsAppOwnedTable(table) || !IsValidIdentifier(column)) return 0;
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    using var cmd = new SqliteCommand(
                        $"SELECT [{column}] FROM [{table}] WHERE [name] = @u COLLATE NOCASE LIMIT 1", _connection);
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    cmd.Parameters.AddWithValue("@u", name ?? "");
                    return CoerceBalance(await cmd.ExecuteScalarAsync().ConfigureAwait(false));
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return 0; }
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>
        /// The metric values for a whole sweep of names, in ONE query per chunk rather
        /// than one query (and one lock acquisition) per viewer — the tick evaluates every
        /// active viewer at once, and N round-trips through the shared connection gate
        /// would make a busy channel's tick the most expensive thing the Hub does.
        /// Names absent from the table are simply absent from the map; the caller reads a
        /// miss as 0.
        /// </summary>
        public async Task<Dictionary<string, long>> RankValuesAsync(
            RankMetricSource source, IReadOnlyList<string> names)
        {
            var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (names is null || names.Count == 0) return map;
            string table = source.Table, column = source.Column;
            if (!IsValidIdentifier(table) || IsAppOwnedTable(table) || !IsValidIdentifier(column)) return map;

            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    foreach (var chunk in Chunk(names, NameChunkSize))
                    {
                        string placeholders = BuildInList(chunk.Count);
                        using var cmd = new SqliteCommand(
                            $"SELECT [name], [{column}] FROM [{table}] WHERE [name] COLLATE NOCASE IN ({placeholders})",
                            _connection);
                        cmd.CommandTimeout = CommandTimeoutSeconds;
                        for (int i = 0; i < chunk.Count; i++) cmd.Parameters.AddWithValue("@n" + i, chunk[i] ?? "");
                        using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                        while (await r.ReadAsync().ConfigureAwait(false))
                        {
                            string key = r.IsDBNull(0) ? "" : (r.GetValue(0)?.ToString() ?? "");
                            if (key.Length == 0) continue;
                            map[key] = CoerceBalance(r.IsDBNull(1) ? null : r.GetValue(1));
                        }
                    }
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return map; }
            }
            finally { ReleaseLock(taken); }
            return map;
        }

        /// <summary>Top-N by metric value, highest first, position starting at 1. The CAST
        /// is load-bearing on an OPEN table: SQLite's storage-class order puts TEXT above
        /// INTEGER, so a db.*-written text cell would otherwise head the board.</summary>
        public async Task<List<(string Name, long Value)>> RankTopAsync(RankMetricSource source, int n)
        {
            var list = new List<(string, long)>();
            string table = source.Table, column = source.Column;
            if (!IsValidIdentifier(table) || IsAppOwnedTable(table) || !IsValidIdentifier(column) || n <= 0) return list;
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    using var cmd = new SqliteCommand(
                        $"SELECT [name], [{column}] FROM [{table}] " +
                        "WHERE [name] IS NOT NULL AND [name] <> '' " +
                        $"ORDER BY CAST([{column}] AS INTEGER) DESC LIMIT @lim", _connection);
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    cmd.Parameters.AddWithValue("@lim", n);
                    using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                    while (await r.ReadAsync().ConfigureAwait(false))
                    {
                        list.Add((
                            r.IsDBNull(0) ? "" : (r.GetValue(0)?.ToString() ?? ""),
                            CoerceBalance(r.IsDBNull(1) ? null : r.GetValue(1))));
                    }
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return list; }
            }
            finally { ReleaseLock(taken); }
            return list;
        }

        // ── Last-known rank (the one-shot guard behind Rank.OnRankUp) ───────

        /// <summary>The rank this viewer was last evaluated at, and the value it was
        /// evaluated from. ("", 0) when unknown — which is exactly what a viewer who has
        /// never been evaluated should read as.</summary>
        public async Task<(string Rank, long Value)> RankStateAsync(string name)
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    using var cmd = new SqliteCommand(
                        $"SELECT [rank], [value] FROM [{RankStateTable}] WHERE [name] = @u COLLATE NOCASE ORDER BY rowid LIMIT 1",
                        _connection);
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    cmd.Parameters.AddWithValue("@u", name ?? "");
                    using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                    if (!await r.ReadAsync().ConfigureAwait(false)) return ("", 0);
                    return (r.IsDBNull(0) ? "" : (r.GetValue(0)?.ToString() ?? ""),
                            CoerceBalance(r.IsDBNull(1) ? null : r.GetValue(1)));
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return ("", 0); }
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>Last-known ranks for a whole sweep, one query per chunk — the batch
        /// sibling of <see cref="RankStateAsync"/>, for the same reason
        /// <see cref="RankValuesAsync"/> exists.</summary>
        public async Task<Dictionary<string, string>> RankStatesAsync(IReadOnlyList<string> names)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (names is null || names.Count == 0) return map;
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    foreach (var chunk in Chunk(names, NameChunkSize))
                    {
                        string placeholders = BuildInList(chunk.Count);
                        using var cmd = new SqliteCommand(
                            $"SELECT [name], [rank] FROM [{RankStateTable}] WHERE [name] COLLATE NOCASE IN ({placeholders}) ORDER BY rowid",
                            _connection);
                        cmd.CommandTimeout = CommandTimeoutSeconds;
                        for (int i = 0; i < chunk.Count; i++) cmd.Parameters.AddWithValue("@n" + i, chunk[i] ?? "");
                        using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                        while (await r.ReadAsync().ConfigureAwait(false))
                        {
                            string key = r.IsDBNull(0) ? "" : (r.GetValue(0)?.ToString() ?? "");
                            if (key.Length == 0) continue;
                            // ORDER BY rowid + first-wins mirrors RankStateAsync's
                            // "ORDER BY rowid LIMIT 1", so a hand-built table holding two
                            // rows for one name resolves to the SAME row in both reads.
                            if (map.ContainsKey(key)) continue;
                            map[key] = r.IsDBNull(1) ? "" : (r.GetValue(1)?.ToString() ?? "");
                        }
                    }
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return map; }
            }
            finally { ReleaseLock(taken); }
            return map;
        }

        /// <summary>Records the rank a sweep of viewers is now at, in ONE transaction.
        /// Returns how many rows were written — 0 when the table is missing (rolled
        /// back), which the caller must treat as "nothing was remembered" rather than
        /// as success.</summary>
        public async Task<int> RankStateSetManyAsync(IReadOnlyList<(string Name, string Rank, long Value)> rows)
        {
            if (rows is null || rows.Count == 0) return 0;
            int applied = 0;
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var tx = _connection!.BeginTransaction();
                try
                {
                    foreach (var (name, rank, value) in rows)
                    {
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        await SetRankStateTx(tx, name, rank ?? "", value).ConfigureAwait(false);
                        applied++;
                    }
                    tx.Commit();
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { try { tx.Rollback(); } catch { } return 0; }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
            finally { ReleaseLock(taken); }
            return applied;
        }

        // Emulated upsert again — the "Ranks" table has no UNIQUE on name either.
        private async Task SetRankStateTx(SqliteTransaction tx, string name, string rank, long value)
        {
            int rows;
            using (var up = new SqliteCommand(
                $"UPDATE [{RankStateTable}] SET [rank] = @r, [value] = @v " +
                $"WHERE rowid = (SELECT rowid FROM [{RankStateTable}] WHERE [name] = @u COLLATE NOCASE ORDER BY rowid LIMIT 1)",
                _connection, tx))
            {
                up.CommandTimeout = CommandTimeoutSeconds;
                up.Parameters.AddWithValue("@r", rank);
                up.Parameters.AddWithValue("@v", value);
                up.Parameters.AddWithValue("@u", name);
                rows = await up.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            if (rows == 0)
            {
                using var ins = new SqliteCommand(
                    $"INSERT INTO [{RankStateTable}] ([name], [rank], [value]) VALUES (@u, @r, @v)", _connection, tx);
                ins.CommandTimeout = CommandTimeoutSeconds;
                ins.Parameters.AddWithValue("@u", name);
                ins.Parameters.AddWithValue("@r", rank);
                ins.Parameters.AddWithValue("@v", value);
                await ins.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        // ── IN-list plumbing ────────────────────────────────────────────────

        // SQLite's default SQLITE_MAX_VARIABLE_NUMBER is 999 on older builds; 200 keeps
        // every chunk far inside that even if a caller ever widens the row shape, and a
        // channel with more concurrent chatters than this simply costs one extra query.
        private const int NameChunkSize = 200;

        // "@n0, @n1, …" — the parameter placeholders for one chunk. The names themselves
        // are always BOUND, never interpolated: a viewer login is external input and this
        // is the one place a sweep would be tempted to build an IN list by concatenation.
        private static string BuildInList(int count)
        {
            var sb = new StringBuilder(count * 5);
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("@n").Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static IEnumerable<List<string>> Chunk(IReadOnlyList<string> names, int size)
        {
            var buffer = new List<string>(Math.Min(size, names.Count));
            foreach (var n in names)
            {
                if (string.IsNullOrWhiteSpace(n)) continue;
                buffer.Add(n);
                if (buffer.Count < size) continue;
                yield return buffer;
                buffer = new List<string>(size);
            }
            if (buffer.Count > 0) yield return buffer;
        }
    }
}
