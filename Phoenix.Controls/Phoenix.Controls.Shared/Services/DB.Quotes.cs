using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Shared.Services
{
    // Quotes persistence — the DB half of the databank-backed quote-store pre-build
    // tool.
    //
    // OPEN-TABLE DOCTRINE (same as DB.Counters.cs / DB.Loyalty.cs): the QUOTES live
    // in an OPEN user table ("Quotes", num/text/name/added_by/added_at) DELIBERATELY
    // NOT in DB._systemTables, so a streamer keeps full db.* script access to the
    // quotes (e.g. a custom !lastquote command). Only the tool's private
    // QuotesConfig (a single JSON blob, mirroring CountersConfig/LoyaltyConfig) is a
    // system table. The DB is the single source of truth — never an in-memory cache
    // — because db.* scripts and this tool write the same open table.
    //
    // NUMBERING: #N = MAX(num)+1 at add time, computed and inserted in ONE
    // transaction under the shared AcquireLockAsync guard, so the read-then-write
    // can never race two concurrent adds into the same number (stable gaps — a
    // deleted #N is never reused, keeping clip/VOD references valid). num is read
    // DEFENSIVELY everywhere (CAST + clamp — see EffectiveNumSql), because an open
    // table means a db.* script can have written text or an out-of-int-range value.
    // IsMissingTableOrColumn / IsValidIdentifier / IsAppOwnedTable are shared with
    // DB.Counters.cs / DB.Loyalty.cs / DB.Giveaway.cs.
    public partial class DB
    {
        /// <summary>Fixed name of the OPEN quotes table. NOT a system table (db.* readable).</summary>
        internal const string QuotesTable = "Quotes";

        internal const string QuotesConfigTablesDdl = @"
            CREATE TABLE IF NOT EXISTS QuotesConfig (
                Slug      TEXT PRIMARY KEY,
                Json      TEXT    NOT NULL,
                UpdatedAt INTEGER NOT NULL DEFAULT 0
            );";

        // Symmetry with EnsureCounters/Loyalty/TimerSchemaMigrations: back-fill any
        // column added after an earlier dev build. Fresh installs no-op through the probe.
        private void EnsureQuotesSchemaMigrations()
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var probe = new SqliteCommand("PRAGMA table_info(QuotesConfig)", _connection))
            using (var r = probe.ExecuteReader())
            {
                while (r.Read()) existing.Add(r.GetString(1));
            }
            var wanted = new (string Column, string Ddl)[]
            {
                ("Json",      "ALTER TABLE QuotesConfig ADD COLUMN Json TEXT DEFAULT ''"),
                ("UpdatedAt", "ALTER TABLE QuotesConfig ADD COLUMN UpdatedAt INTEGER NOT NULL DEFAULT 0"),
            };
            foreach (var (column, ddl) in wanted)
            {
                if (existing.Contains(column)) continue;
                using var alter = new SqliteCommand(ddl, _connection);
                alter.ExecuteNonQuery();
            }
        }

        // ── Config blob (system table) ──────────────────────────────────────

        /// <summary>Loads the serialized QuotesConfig JSON, or null when unset.</summary>
        public async Task<string?> LoadQuotesConfigAsync()
            => await QueryScalarAsync<string>(
                "SELECT Json FROM QuotesConfig WHERE Slug = 'config' LIMIT 1", _ => { }).ConfigureAwait(false);

        /// <summary>Persists the serialized QuotesConfig JSON.</summary>
        public async Task SaveQuotesConfigAsync(string json, long updatedAtMs)
        {
            await ExecuteAsync(
                @"INSERT INTO QuotesConfig (Slug, Json, UpdatedAt) VALUES ('config', @json, @upd)
                  ON CONFLICT(Slug) DO UPDATE SET Json = @json, UpdatedAt = @upd",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@json", json ?? "");
                    cmd.Parameters.AddWithValue("@upd", updatedAtMs);
                }).ConfigureAwait(false);
        }

        // ── Open quotes table ───────────────────────────────────────────────

        /// <summary>Creates the OPEN "Quotes" table if missing. Never a system table —
        /// full db.* access is the point. A pre-existing user table of the same name
        /// is left untouched.</summary>
        public async Task EnsureQuotesTableAsync()
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand(
                    $"CREATE TABLE IF NOT EXISTS [{QuotesTable}] (rowid INTEGER PRIMARY KEY AUTOINCREMENT, num INTEGER, text TEXT, name TEXT, added_by TEXT, added_at TEXT);",
                    _connection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>Adds a quote, assigning num = MAX(num)+1 (or 1 for the first),
        /// computed and inserted in ONE transaction under the lock so two concurrent
        /// adds can never collide on num. The MAX is derived DEFENSIVELY — an open-table
        /// cell can hold text or a value past int range. Returns the new number.</summary>
        public async Task<int> QuoteAddAsync(string text, string name, string addedBy)
        {
            string addedAt = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var tx = _connection!.BeginTransaction();
                try
                {
                    // MAX+1 MUST be read-and-written under the same lock+tx — this is the
                    // stable-gaps numbering guarantee (never reuse a deleted number).
                    //
                    // num comes out of an OPEN table, so db.set_cell / db.insert_row can
                    // have left ANYTHING in it. Three defenses, because one poisoned cell
                    // must never break !addquote nor mint a colliding number:
                    //   • CAST(num AS INTEGER) — SQLite's storage-class order puts TEXT
                    //     ABOVE INTEGER, so a text cell wins a raw MAX(num), and
                    //     non-numeric text coerces to 0 in arithmetic ('gap' + 1 = 1) —
                    //     handing out a DUPLICATE #1.
                    //   • CoerceBalance + clamp — Convert.ToInt32 on a boxed long past
                    //     int.MaxValue THROWS OverflowException, which rolls the tx back
                    //     and rethrows into an uncaught chat-command failure.
                    //   • NextFreeQuoteNumberAsync — the final guarantee that whatever the
                    //     candidate ends up being, no existing row already reads back as it.
                    long max;
                    using (var maxCmd = new SqliteCommand(
                        $"SELECT COALESCE(MAX(CAST(num AS INTEGER)), 0) FROM [{QuotesTable}]", _connection, tx))
                    {
                        maxCmd.CommandTimeout = CommandTimeoutSeconds;
                        max = CoerceBalance(await maxCmd.ExecuteScalarAsync().ConfigureAwait(false));
                    }
                    int candidate = max < 1 ? 1
                                  : max >= int.MaxValue ? int.MaxValue
                                  : (int)max + 1;
                    int next = await NextFreeQuoteNumberAsync(candidate, tx).ConfigureAwait(false);
                    using (var ins = new SqliteCommand(
                        $"INSERT INTO [{QuotesTable}] (num, text, name, added_by, added_at) VALUES (@n, @t, @who, @by, @at)",
                        _connection, tx))
                    {
                        ins.CommandTimeout = CommandTimeoutSeconds;
                        ins.Parameters.AddWithValue("@n", next);
                        ins.Parameters.AddWithValue("@t", text ?? "");
                        ins.Parameters.AddWithValue("@who", name ?? "");
                        ins.Parameters.AddWithValue("@by", addedBy ?? "");
                        ins.Parameters.AddWithValue("@at", addedAt);
                        await ins.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                    tx.Commit();
                    return next;
                }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
            finally { ReleaseLock(taken); }
        }

        // A row's EFFECTIVE number — what ReadEntry reports, and therefore what the panel
        // shows and what a viewer types after !delquote. CAST forces the numeric reading
        // of an open-table cell (text "gap" → 0) and the MIN/MAX pair clamps it into int
        // range exactly like ReadEntry's narrowing. Addressing rows by this expression is
        // what keeps a poisoned row REPAIRABLE from the UI: for INTEGER cells, whole-valued
        // REALs and past-int-range values (both sides clamp to the same bound), the number
        // you see IS the number that fetches, edits and deletes it. Two db.*-only edges sit
        // outside that promise, where SQL and CoerceBalance narrow differently: a REAL like
        // -7.5 (SQL CAST truncates toward zero → -7, CoerceBalance floors → -8) and TEXT
        // with a numeric prefix like '7 quotes' (CAST reads the prefix → 7, CoerceBalance's
        // TryParse pair → 0). Those rows stay addressable — by the SQL reading rather than
        // the displayed one — so they remain repairable, just not by eye.
        //
        // ORDER BY rowid on the three row-ADDRESSING reads below (get / edit / delete):
        // two rows CAN land on the same effective number (db.* again — QuoteAddAsync's
        // NextFreeQuoteNumberAsync probes with this same expression, so the tool itself can
        // no longer mint one), and unordered SQLite is free to return either. Oldest-wins
        // makes all three hit the SAME row, so a repeated !delquote peels duplicates off in
        // a predictable order instead of at random. NextFreeQuoteNumberAsync's own probe
        // stays unordered on purpose — it only asks WHETHER any row holds the number, and
        // every candidate row answers that identically.
        private const string EffectiveNumSql =
            "MAX(MIN(CAST(num AS INTEGER), 2147483647), -2147483648)";

        /// <summary>First unused quote number at or below <paramref name="candidate"/>,
        /// probed inside the caller's transaction. The normal path costs ONE probe and no
        /// walk: MAX+1 is free by construction, so stable gaps are preserved. The walk
        /// only engages when an out-of-range cell pinned the candidate at the int ceiling,
        /// where filling a gap beats minting a duplicate the UI could never delete. Every
        /// step down means the number really is taken, so the walk is bounded by the row
        /// count.</summary>
        private async Task<int> NextFreeQuoteNumberAsync(int candidate, SqliteTransaction tx)
        {
            while (true)
            {
                using (var probe = new SqliteCommand(
                    $"SELECT 1 FROM [{QuotesTable}] WHERE {EffectiveNumSql} = @n LIMIT 1", _connection, tx))
                {
                    probe.CommandTimeout = CommandTimeoutSeconds;
                    probe.Parameters.AddWithValue("@n", candidate);
                    if (await probe.ExecuteScalarAsync().ConfigureAwait(false) is null) return candidate;
                }
                // Only reachable with ~2 billion rows occupying every number down to 1.
                if (candidate <= 1) return 1;
                candidate--;
            }
        }

        /// <summary>The quote with this number, or null (missing row / table).</summary>
        public async Task<QuoteEntry?> QuoteGetByNumberAsync(int num)
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    using var cmd = new SqliteCommand(
                        $"SELECT num, text, name, added_by, added_at FROM [{QuotesTable}] " +
                        $"WHERE {EffectiveNumSql} = @n ORDER BY rowid LIMIT 1", _connection);
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    cmd.Parameters.AddWithValue("@n", num);
                    using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                    return await r.ReadAsync().ConfigureAwait(false) ? ReadEntry(r) : null;
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return null; }
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>A random quote, or null when the table is empty / missing.</summary>
        public async Task<QuoteEntry?> QuoteGetRandomAsync()
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    using var cmd = new SqliteCommand(
                        $"SELECT num, text, name, added_by, added_at FROM [{QuotesTable}] ORDER BY RANDOM() LIMIT 1", _connection);
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                    return await r.ReadAsync().ConfigureAwait(false) ? ReadEntry(r) : null;
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return null; }
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>Total number of quotes (0 when the table is missing).</summary>
        public async Task<long> QuoteCountAsync()
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    using var cmd = new SqliteCommand(
                        $"SELECT COUNT(*) FROM [{QuotesTable}]", _connection);
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    object? o = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                    return o is null || o is DBNull ? 0 : Convert.ToInt64(o, System.Globalization.CultureInfo.InvariantCulture);
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return 0; }
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>Deletes quote #num (rowid-scoped so a dup-num row is dropped once,
        /// oldest first — see EffectiveNumSql on why the reads are ordered).
        /// Survivors are NOT renumbered — stable gaps. Returns true when a row went.</summary>
        public async Task<bool> QuoteDeleteAsync(int num)
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    using var cmd = new SqliteCommand(
                        $"DELETE FROM [{QuotesTable}] WHERE rowid = " +
                        $"(SELECT rowid FROM [{QuotesTable}] WHERE {EffectiveNumSql} = @n ORDER BY rowid LIMIT 1)",
                        _connection);
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    cmd.Parameters.AddWithValue("@n", num);
                    return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false) > 0;
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return false; }
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>Updates quote #num's text/name in place (rowid-scoped, oldest-first on
        /// a dup-num collision — same row QuoteGetByNumberAsync/QuoteDeleteAsync address —
        /// and NO renumber). Returns true when a row was updated. Never inserts (edit of a
        /// missing number is a no-op).
        ///
        /// A NULL <paramref name="text"/> or <paramref name="name"/> means "leave this
        /// column alone" — a PARTIAL edit, which is what the Architect Quote.Edit node
        /// needs so an unwired pin can fix one half of a quote without blanking the other.
        /// It is expressed as COALESCE inside the single UPDATE rather than as a
        /// read-then-write in the service, so a partial edit stays as atomic as a whole-row
        /// one; a read-modify-write would open a window for a concurrent chat/panel edit to
        /// be silently overwritten with the value we read a moment ago. Blanking a column is
        /// therefore an EMPTY STRING, never a null: the panel always passes both columns
        /// non-null, and the node passes "" once its ClearEmpty pin is on.</summary>
        public async Task<bool> QuoteUpdateAsync(int num, string? text, string? name)
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    using var cmd = new SqliteCommand(
                        $"UPDATE [{QuotesTable}] SET text = COALESCE(@t, text), name = COALESCE(@who, name) WHERE rowid = " +
                        $"(SELECT rowid FROM [{QuotesTable}] WHERE {EffectiveNumSql} = @n ORDER BY rowid LIMIT 1)",
                        _connection);
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    cmd.Parameters.AddWithValue("@t", (object?)text ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@who", (object?)name ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@n", num);
                    return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false) > 0;
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return false; }
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>All quotes, ordered by number. Empty when the table is missing.</summary>
        public async Task<List<QuoteEntry>> QuoteListAsync()
        {
            var list = new List<QuoteEntry>();
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    using var cmd = new SqliteCommand(
                        $"SELECT num, text, name, added_by, added_at FROM [{QuotesTable}] ORDER BY num", _connection);
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                    while (await r.ReadAsync().ConfigureAwait(false))
                        list.Add(ReadEntry(r));
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { /* empty */ }
            }
            finally { ReleaseLock(taken); }
            return list;
        }

        // Column order: 0 num, 1 text, 2 name, 3 added_by, 4 added_at.
        private static QuoteEntry ReadEntry(SqliteDataReader r) => new()
        {
            // OPEN table (db.* can store any dynamic-typed value): coerce defensively so a
            // non-integer / BLOB cell can't throw an uncaught FormatException /
            // InvalidCastException that blanks the panel or crashes !quote — same posture
            // as DB.Counters.CounterListAsync (CoerceBalance) and DB.Loyalty. The
            // narrowing CLAMPS (never wraps): a past-int-range cell must read back as the
            // ceiling — the value EffectiveNumSql also addresses it by — instead of
            // wrapping to some other number that !delquote could never match.
            Number  = (int)Math.Clamp(CoerceBalance(r.IsDBNull(0) ? null : r.GetValue(0)), int.MinValue, int.MaxValue),
            Text    = r.IsDBNull(1) ? "" : (r.GetValue(1)?.ToString() ?? ""),
            Name    = r.IsDBNull(2) ? "" : (r.GetValue(2)?.ToString() ?? ""),
            AddedBy = r.IsDBNull(3) ? "" : (r.GetValue(3)?.ToString() ?? ""),
            AddedAt = r.IsDBNull(4) ? "" : (r.GetValue(4)?.ToString() ?? ""),
        };
    }
}
