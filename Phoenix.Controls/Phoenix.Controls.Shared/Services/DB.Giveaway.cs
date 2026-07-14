using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Shared.Services
{
    // Giveaway persistence — the DB half of the giveaway system. Pure storage:
    // create / read / atomic ticket upsert / close / winner record / activity
    // log. The weighted-draw RANDOMNESS lives in the Hub GiveawayService, not
    // here — DB only returns the entrant pool. All methods route through the
    // shared AcquireLockAsync/ReleaseLock guard so they serialize with every
    // other DB.* caller (and re-enter safely from a handler that already holds
    // the lock). The three tables are listed in _systemTables (DB.cs) so the
    // generic db.* script commands and the remote bridge can't mutate them.
    public partial class DB
    {
        internal const string GiveawayTablesDdl = @"
            CREATE TABLE IF NOT EXISTS Giveaways (
                Id                INTEGER PRIMARY KEY AUTOINCREMENT,
                Key               TEXT UNIQUE,
                Title             TEXT,
                Status            TEXT    DEFAULT 'open',
                OpenedAt          TEXT,
                ClosedAt          TEXT,
                OpenedBy          TEXT,
                IsDefault         INTEGER DEFAULT 0,
                SubBonusFactor    REAL    DEFAULT 1.0,
                ModBonusFactor    REAL    DEFAULT 1.0,
                CapPerUser        INTEGER DEFAULT 0,
                TicketPrice       INTEGER DEFAULT 0,
                Winners           TEXT    DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS GiveawayTickets (
                GiveawayId INTEGER,
                Username   TEXT,
                Role       TEXT    DEFAULT 'viewer',
                IsMod      INTEGER DEFAULT 0,
                Tickets    INTEGER DEFAULT 0,
                LastEntry  TEXT,
                PRIMARY KEY (GiveawayId, Username)
            );
            CREATE TABLE IF NOT EXISTS GiveawayActivity (
                Id         INTEGER PRIMARY KEY AUTOINCREMENT,
                GiveawayId INTEGER,
                Time       TEXT,
                Kind       TEXT,
                Message    TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_gwtickets_gid  ON GiveawayTickets(GiveawayId);
            CREATE INDEX IF NOT EXISTS idx_gwactivity_gid ON GiveawayActivity(GiveawayId);";

        // Schema migration for databanks created before the settings rework:
        // CREATE TABLE IF NOT EXISTS never touches an existing table, so the
        // SubBonusFactor / TicketPrice columns have to be added explicitly. The
        // legacy columns (EntryCommand / TicketsPerMessage / SubscriberBonus /
        // DrawMethod) are left in place on old files — nothing reads them any
        // more. Called synchronously from DB.Initialize right after
        // GiveawayTablesDdl.
        private void EnsureGiveawaySchemaMigrations()
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var probe = new SqliteCommand("PRAGMA table_info(Giveaways)", _connection))
            using (var r = probe.ExecuteReader())
            {
                while (r.Read()) existing.Add(r.GetString(1));
            }

            var wanted = new (string Column, string Ddl)[]
            {
                ("SubBonusFactor", "ALTER TABLE Giveaways ADD COLUMN SubBonusFactor REAL DEFAULT 1.0"),
                ("TicketPrice",    "ALTER TABLE Giveaways ADD COLUMN TicketPrice INTEGER DEFAULT 0"),
                ("ModBonusFactor", "ALTER TABLE Giveaways ADD COLUMN ModBonusFactor REAL DEFAULT 1.0"),
            };
            foreach (var (column, ddl) in wanted)
            {
                if (existing.Contains(column)) continue;
                using var alter = new SqliteCommand(ddl, _connection);
                alter.ExecuteNonQuery();
            }

            // GiveawayTickets grew its own migrated column with the IsSub/IsMod
            // node rework: the entrant's mod flag is a separate column so a
            // subscriber-moderator keeps BOTH facts (the single Role badge can
            // only carry one). Same probe-then-ALTER shape as above.
            var existingTickets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var probe = new SqliteCommand("PRAGMA table_info(GiveawayTickets)", _connection))
            using (var r = probe.ExecuteReader())
            {
                while (r.Read()) existingTickets.Add(r.GetString(1));
            }
            if (!existingTickets.Contains("IsMod"))
            {
                using var alter = new SqliteCommand(
                    "ALTER TABLE GiveawayTickets ADD COLUMN IsMod INTEGER DEFAULT 0", _connection);
                alter.ExecuteNonQuery();
            }
        }

        // SELECT projection that decorates each Giveaways row with the derived
        // entrant count / ticket total / most-recent entry from GiveawayTickets.
        private const string GiveawaySelect = @"
            SELECT g.Id, g.Key, g.Title, g.Status, g.OpenedAt, g.ClosedAt, g.OpenedBy,
                   g.IsDefault, g.SubBonusFactor, g.ModBonusFactor, g.CapPerUser, g.TicketPrice, g.Winners,
                   (SELECT COUNT(*)            FROM GiveawayTickets t WHERE t.GiveawayId = g.Id) AS Entrants,
                   (SELECT COALESCE(SUM(t.Tickets),0) FROM GiveawayTickets t WHERE t.GiveawayId = g.Id) AS TicketTotal,
                   (SELECT MAX(t.LastEntry)    FROM GiveawayTickets t WHERE t.GiveawayId = g.Id) AS LastEntry
            FROM Giveaways g";

        private static Giveaway ReadGiveaway(SqliteDataReader r) => new()
        {
            Id                    = r.GetInt64(0),
            Key                   = r.IsDBNull(1) ? "" : r.GetString(1),
            Title                 = r.IsDBNull(2) ? "" : r.GetString(2),
            Status                = r.IsDBNull(3) ? "open" : r.GetString(3),
            OpenedAt              = r.IsDBNull(4) ? "" : r.GetString(4),
            ClosedAt              = r.IsDBNull(5) ? null : r.GetString(5),
            OpenedBy              = r.IsDBNull(6) ? "" : r.GetString(6),
            IsDefault             = !r.IsDBNull(7) && r.GetInt64(7) != 0,
            SubscriberBonusFactor = r.IsDBNull(8) ? 1.0 : r.GetDouble(8),
            ModBonusFactor        = r.IsDBNull(9) ? 1.0 : r.GetDouble(9),
            CapPerUser            = r.IsDBNull(10) ? 0 : (int)r.GetInt64(10),
            TicketPrice           = r.IsDBNull(11) ? 0 : (int)r.GetInt64(11),
            Winners               = r.IsDBNull(12) ? "" : r.GetString(12),
            Entrants              = r.IsDBNull(13) ? 0 : (int)r.GetInt64(13),
            Tickets               = r.IsDBNull(14) ? 0 : (int)r.GetInt64(14),
            LastEntry             = r.IsDBNull(15) ? "" : r.GetString(15),
        };

        /// <summary>Inserts a new open giveaway and returns its row id.</summary>
        public async Task<long> CreateGiveawayAsync(string key, string title, string openedBy, string openedAtIso)
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand(
                    @"INSERT INTO Giveaways (Key, Title, Status, OpenedAt, OpenedBy)
                      VALUES (@key, @title, 'open', @opened, @by);
                      SELECT last_insert_rowid();", _connection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                cmd.Parameters.AddWithValue("@key", key);
                cmd.Parameters.AddWithValue("@title", title);
                cmd.Parameters.AddWithValue("@opened", openedAtIso);
                cmd.Parameters.AddWithValue("@by", openedBy ?? "");
                var idObj = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                return Convert.ToInt64(idObj, CultureInfo.InvariantCulture);
            }
            finally { ReleaseLock(taken); }
        }

        public async Task<List<Giveaway>> GetGiveawaysAsync()
        {
            var list = new List<Giveaway>();
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand(GiveawaySelect + " ORDER BY g.Id DESC", _connection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await r.ReadAsync().ConfigureAwait(false))
                    list.Add(ReadGiveaway((SqliteDataReader)r));
            }
            finally { ReleaseLock(taken); }
            return list;
        }

        public Task<Giveaway?> GetGiveawayAsync(long id)
            => GetGiveawayWhereAsync("WHERE g.Id = @v", id.ToString(CultureInfo.InvariantCulture), isInt: true);

        public Task<Giveaway?> GetGiveawayByKeyAsync(string key)
            => GetGiveawayWhereAsync("WHERE g.Key = @v", key, isInt: false);

        private async Task<Giveaway?> GetGiveawayWhereAsync(string whereClause, string value, bool isInt)
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand($"{GiveawaySelect} {whereClause} LIMIT 1", _connection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                if (isInt) cmd.Parameters.AddWithValue("@v", long.Parse(value, CultureInfo.InvariantCulture));
                else       cmd.Parameters.AddWithValue("@v", value);
                using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                if (await r.ReadAsync().ConfigureAwait(false))
                    return ReadGiveaway((SqliteDataReader)r);
            }
            finally { ReleaseLock(taken); }
            return null;
        }

        public async Task<long?> GetDefaultGiveawayIdAsync()
        {
            var v = await QueryScalarAsync<long?>(
                "SELECT Id FROM Giveaways WHERE IsDefault = 1 ORDER BY Id DESC LIMIT 1",
                _ => { }).ConfigureAwait(false);
            return v;
        }

        /// <summary>Sets/clears the single app-wide default. Setting one clears the rest.</summary>
        public async Task SetDefaultGiveawayAsync(long id, bool isDefault)
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                if (isDefault)
                {
                    using var clear = new SqliteCommand("UPDATE Giveaways SET IsDefault = 0", _connection);
                    clear.CommandTimeout = CommandTimeoutSeconds;
                    await clear.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                using var set = new SqliteCommand("UPDATE Giveaways SET IsDefault = @d WHERE Id = @id", _connection);
                set.CommandTimeout = CommandTimeoutSeconds;
                set.Parameters.AddWithValue("@d", isDefault ? 1 : 0);
                set.Parameters.AddWithValue("@id", id);
                await set.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            finally { ReleaseLock(taken); }
        }

        public async Task SetGiveawayStatusAsync(long id, string status, string? closedAtIso)
        {
            await ExecuteAsync(
                "UPDATE Giveaways SET Status = @s, ClosedAt = @c WHERE Id = @id",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@s", status);
                    cmd.Parameters.AddWithValue("@c", (object?)closedAtIso ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@id", id);
                }).ConfigureAwait(false);
        }

        /// <summary>
        /// Atomically adds <paramref name="increment"/> tickets for a user
        /// (insert-on-first-entry), clamped to <paramref name="capPerUser"/>
        /// when &gt; 0. The clamp gates GROWTH only — a count already above a
        /// since-lowered cap is preserved as-is, never trimmed (the MAX/MIN
        /// pair in the UPDATE). Returns the user's new total plus whether the
        /// clamp truncated the increment (Clamped = the increment could not be
        /// applied in full because of the cap; a pure read — increment &lt;= 0
        /// — is never Clamped). A single round-trip under the shared lock —
        /// the leading previous-count SELECT rides the same serialized batch,
        /// so there is no read-then-write race even when several chat handlers
        /// fire at once.
        /// </summary>
        public async Task<(int Tickets, bool Clamped)> UpsertTicketAsync(long giveawayId, string username, string role,
            int increment, int capPerUser, string lastEntryIso, bool isMod = false)
        {
            int cap = capPerUser > 0 ? capPerUser : int.MaxValue;
            int inc = Math.Max(0, increment);
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand(
                    @"SELECT COALESCE((SELECT Tickets FROM GiveawayTickets WHERE GiveawayId = @gid AND Username = @u), 0);
                      INSERT INTO GiveawayTickets (GiveawayId, Username, Role, IsMod, Tickets, LastEntry)
                      VALUES (@gid, @u, @r, @m, MIN(@inc, @cap), @t)
                      ON CONFLICT(GiveawayId, Username) DO UPDATE SET
                          Tickets   = MAX(Tickets, MIN(Tickets + @inc, @cap)),
                          Role      = @r,
                          IsMod     = @m,
                          LastEntry = @t;
                      SELECT Tickets FROM GiveawayTickets WHERE GiveawayId = @gid AND Username = @u;",
                    _connection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                cmd.Parameters.AddWithValue("@gid", giveawayId);
                cmd.Parameters.AddWithValue("@u", username);
                cmd.Parameters.AddWithValue("@r", string.IsNullOrEmpty(role) ? "viewer" : role);
                cmd.Parameters.AddWithValue("@m", isMod ? 1 : 0);
                cmd.Parameters.AddWithValue("@inc", inc);
                cmd.Parameters.AddWithValue("@cap", cap);
                cmd.Parameters.AddWithValue("@t", lastEntryIso);

                // Microsoft.Data.Sqlite executes the batch statement-by-statement
                // as the reader advances: result set 1 = previous count, the
                // INSERT produces no result set, result set 2 = new total.
                int previous = 0, newTotal = 0;
                using var r = (SqliteDataReader)await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                if (await r.ReadAsync().ConfigureAwait(false))
                    previous = r.IsDBNull(0) ? 0 : (int)r.GetInt64(0);
                if (await r.NextResultAsync().ConfigureAwait(false) &&
                    await r.ReadAsync().ConfigureAwait(false))
                    newTotal = r.IsDBNull(0) ? 0 : (int)r.GetInt64(0);

                // Clamped ⇔ the increment did not land in full: covers both
                // "already at/over cap" (newTotal == previous) and a partial
                // top-up to the cap. long math guards int overflow at the rim.
                bool clamped = inc > 0 && newTotal < (long)previous + inc;
                return (newTotal, clamped);
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>
        /// Atomic priced ticket purchase — balance read, channel-point deduction
        /// and ticket upsert in ONE transaction under the shared lock, so a
        /// concurrent purchase can never charge without granting (or vice
        /// versa). The currency lives in a USER table the author picked on the
        /// node (fixed contract: column <c>name</c> = username, column
        /// <c>currency</c> = balance; the Architect picker's one-click create
        /// makes exactly that shape as "ChannelPoints").
        ///
        /// Grant math:
        ///  · cap semantics mirror UpsertTicketAsync (growth-only; cap 0 = unlimited);
        ///  · <paramref name="isAll"/> = false → all-or-nothing on the
        ///    cap-clamped request: if the balance can't cover it, NOTHING is
        ///    charged or granted (NoFunds);
        ///  · <paramref name="isAll"/> = true → grant the maximum the balance
        ///    AND the cap allow ("buy as many as possible"); with a free price
        ///    and no cap there is no natural bound, so it falls back to
        ///    <paramref name="requested"/>;
        ///  · price &lt;= 0 → free entry, the currency table is never touched.
        /// A user with no row in the currency table counts as balance 0.
        /// </summary>
        public async Task<GiveawayPurchaseResult> PurchaseGiveawayTicketsAsync(
            long giveawayId, string username, string role, int requested, int capPerUser,
            int price, string currencyTable, bool isAll, string lastEntryIso, bool isMod = false)
        {
            bool priced = price > 0;
            if (priced && (!IsValidIdentifier(currencyTable) || IsSystemTable(currencyTable)))
                return new GiveawayPurchaseResult(
                    await GetTicketsForUserAsync(giveawayId, username).ConfigureAwait(false),
                    0, 0, -1, Capped: false, NoFunds: false, TableMissing: true);

            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var tx = _connection!.BeginTransaction();
                try
                {
                    // Current tickets (0 on first entry).
                    int current;
                    using (var cur = new SqliteCommand(
                        "SELECT COALESCE((SELECT Tickets FROM GiveawayTickets WHERE GiveawayId = @gid AND Username = @u), 0)",
                        _connection, tx))
                    {
                        cur.CommandTimeout = CommandTimeoutSeconds;
                        cur.Parameters.AddWithValue("@gid", giveawayId);
                        cur.Parameters.AddWithValue("@u", username);
                        current = Convert.ToInt32(await cur.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
                    }

                    long capRemaining = capPerUser > 0 ? Math.Max(0, (long)capPerUser - current) : long.MaxValue;

                    // Balance (priced only). Missing row = 0; missing table or
                    // missing name/currency column = TableMissing outcome. Any
                    // OTHER SqliteException (BUSY, IOERR, CORRUPT, …) is a real
                    // DB failure and must surface as one — the outer catch
                    // rolls back and rethrows — never masquerade as a config
                    // problem the streamer would chase in the wrong place.
                    long balance = -1;
                    if (priced)
                    {
                        try
                        {
                            using var bal = new SqliteCommand(
                                $"SELECT [currency] FROM [{currencyTable}] WHERE [name] = @u LIMIT 1",
                                _connection, tx);
                            bal.CommandTimeout = CommandTimeoutSeconds;
                            bal.Parameters.AddWithValue("@u", username);
                            var v = await bal.ExecuteScalarAsync().ConfigureAwait(false);
                            balance = CoerceBalance(v);
                        }
                        catch (SqliteException ex) when (IsMissingTableOrColumn(ex))
                        {
                            tx.Rollback();
                            return new GiveawayPurchaseResult(current, 0, 0, -1,
                                Capped: false, NoFunds: false, TableMissing: true);
                        }
                    }

                    // Grant math.
                    int grant;
                    bool capped = false, noFunds = false;
                    if (!isAll)
                    {
                        if (requested <= 0)   // pure read — service normally pre-filters
                        {
                            tx.Rollback();
                            return new GiveawayPurchaseResult(current, 0, 0, priced ? balance : -1, false, false, false);
                        }
                        long want = Math.Min(requested, capRemaining);
                        if (want <= 0)
                        {
                            tx.Rollback();
                            return new GiveawayPurchaseResult(current, 0, 0, priced ? balance : -1,
                                Capped: true, NoFunds: false, TableMissing: false);
                        }
                        if (priced && balance < want * price)
                        {
                            tx.Rollback();
                            return new GiveawayPurchaseResult(current, 0, 0, balance,
                                Capped: false, NoFunds: true, TableMissing: false);
                        }
                        grant = (int)want;
                        capped = want < requested;   // cap truncated the request (grant still lands)
                    }
                    else
                    {
                        long bound = capRemaining;
                        if (priced) bound = Math.Min(bound, balance / price);
                        if (capRemaining <= 0)
                        {
                            tx.Rollback();
                            return new GiveawayPurchaseResult(current, 0, 0, priced ? balance : -1,
                                Capped: true, NoFunds: false, TableMissing: false);
                        }
                        if (priced && bound <= 0)
                        {
                            tx.Rollback();
                            return new GiveawayPurchaseResult(current, 0, 0, balance,
                                Capped: false, NoFunds: true, TableMissing: false);
                        }
                        // Free + unlimited cap has no natural bound — fall back
                        // to the node's Increment so "IsAll" stays a no-op there.
                        if (bound == long.MaxValue) bound = Math.Max(0, requested);
                        if (bound <= 0)
                        {
                            tx.Rollback();
                            return new GiveawayPurchaseResult(current, 0, 0, -1, false, false, false);
                        }
                        grant = (int)Math.Min(bound, int.MaxValue);
                    }

                    long cost = priced ? (long)grant * price : 0;

                    if (cost > 0)
                    {
                        // rowid-scoped so duplicate name rows (user-authored
                        // table) never get charged more than once.
                        using var pay = new SqliteCommand(
                            $"UPDATE [{currencyTable}] SET [currency] = [currency] - @cost " +
                            $"WHERE rowid = (SELECT rowid FROM [{currencyTable}] WHERE [name] = @u LIMIT 1)",
                            _connection, tx);
                        pay.CommandTimeout = CommandTimeoutSeconds;
                        pay.Parameters.AddWithValue("@cost", cost);
                        pay.Parameters.AddWithValue("@u", username);
                        await pay.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    // Same growth-only upsert shape as UpsertTicketAsync — the
                    // MIN clamp stays as a backstop even though grant is already
                    // cap-clamped above (both run under the same lock hold).
                    long capParam = capPerUser > 0 ? capPerUser : int.MaxValue;
                    int newTotal;
                    using (var up = new SqliteCommand(
                        @"INSERT INTO GiveawayTickets (GiveawayId, Username, Role, IsMod, Tickets, LastEntry)
                          VALUES (@gid, @u, @r, @m, MIN(@inc, @cap), @t)
                          ON CONFLICT(GiveawayId, Username) DO UPDATE SET
                              Tickets   = MAX(Tickets, MIN(Tickets + @inc, @cap)),
                              Role      = @r,
                              IsMod     = @m,
                              LastEntry = @t;
                          SELECT Tickets FROM GiveawayTickets WHERE GiveawayId = @gid AND Username = @u;",
                        _connection, tx))
                    {
                        up.CommandTimeout = CommandTimeoutSeconds;
                        up.Parameters.AddWithValue("@gid", giveawayId);
                        up.Parameters.AddWithValue("@u", username);
                        up.Parameters.AddWithValue("@r", string.IsNullOrEmpty(role) ? "viewer" : role);
                        up.Parameters.AddWithValue("@m", isMod ? 1 : 0);
                        up.Parameters.AddWithValue("@inc", grant);
                        up.Parameters.AddWithValue("@cap", capParam);
                        up.Parameters.AddWithValue("@t", lastEntryIso);
                        newTotal = Convert.ToInt32(await up.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
                    }

                    tx.Commit();
                    int purchased = newTotal - current;
                    return new GiveawayPurchaseResult(newTotal, purchased, cost,
                        priced ? balance - cost : -1, capped, noFunds, TableMissing: false);
                }
                catch
                {
                    try { tx.Rollback(); } catch { /* already rolled back / disposed */ }
                    throw;
                }
            }
            finally { ReleaseLock(taken); }
        }

        // "no such table" / "no such column" are the config mistakes the
        // TableMissing outcome exists for; everything else rethrows.
        private static bool IsMissingTableOrColumn(SqliteException ex) =>
            ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase);

        // The currency cell lives in a USER-authored table — SQLite dynamic
        // typing means it can hold anything (float-as-text, "gold", a BLOB).
        // Unreadable values coerce to 0 ("no balance" → NoFunds), never throw:
        // a viewer's garbage cell must not abort the whole script run.
        private static long CoerceBalance(object? v)
        {
            switch (v)
            {
                case null or DBNull: return 0;
                case long l: return l;
                case double d: return double.IsFinite(d) ? (long)Math.Floor(d) : 0;
            }
            string? s;
            try { s = Convert.ToString(v, CultureInfo.InvariantCulture); }
            catch { return 0; }
            s = s?.Trim();
            if (string.IsNullOrEmpty(s)) return 0;
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long b)) return b;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double f) && double.IsFinite(f))
                return (long)Math.Floor(f);
            return 0;
        }

        /// <summary>Sets the per-user ticket cap (0 = unlimited). Existing ticket
        /// counts above a newly lowered cap are left untouched — the cap gates
        /// future increments only (the growth-only MAX/MIN clamp in
        /// UpsertTicketAsync), so an over-cap holder keeps their count and
        /// simply gains nothing until the cap rises above it again.</summary>
        public async Task SetGiveawayCapPerUserAsync(long id, int capPerUser)
        {
            await ExecuteAsync(
                "UPDATE Giveaways SET CapPerUser = @cap WHERE Id = @id",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@cap", Math.Max(0, capPerUser));
                    cmd.Parameters.AddWithValue("@id", id);
                }).ConfigureAwait(false);
        }

        /// <summary>Sets the channel-point price per ticket (0 = free entry).</summary>
        public async Task SetGiveawayTicketPriceAsync(long id, int price)
        {
            await ExecuteAsync(
                "UPDATE Giveaways SET TicketPrice = @p WHERE Id = @id",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@p", Math.Max(0, price));
                    cmd.Parameters.AddWithValue("@id", id);
                }).ConfigureAwait(false);
        }

        /// <summary>Sets the subscriber ticket-weight factor (1 = no bonus).</summary>
        public async Task SetGiveawaySubBonusFactorAsync(long id, double factor)
        {
            await ExecuteAsync(
                "UPDATE Giveaways SET SubBonusFactor = @f WHERE Id = @id",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@f", factor);
                    cmd.Parameters.AddWithValue("@id", id);
                }).ConfigureAwait(false);
        }

        /// <summary>Sets the moderator ticket-weight factor (1 = no bonus).</summary>
        public async Task SetGiveawayModBonusFactorAsync(long id, double factor)
        {
            await ExecuteAsync(
                "UPDATE Giveaways SET ModBonusFactor = @f WHERE Id = @id",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@f", factor);
                    cmd.Parameters.AddWithValue("@id", id);
                }).ConfigureAwait(false);
        }

        /// <summary>
        /// Batch-updates entrant roles from a fresh pre-draw lookup (one
        /// transaction inside one lock hold). Missing users no-op.
        /// </summary>
        public async Task UpdateGiveawayEntrantRolesAsync(long giveawayId, IReadOnlyDictionary<string, string> roleByUser)
        {
            if (roleByUser.Count == 0) return;
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var tx = _connection!.BeginTransaction();
                foreach (var kv in roleByUser)
                {
                    using var cmd = new SqliteCommand(
                        "UPDATE GiveawayTickets SET Role = @r WHERE GiveawayId = @gid AND Username = @u",
                        _connection, tx);
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    cmd.Parameters.AddWithValue("@r", kv.Value);
                    cmd.Parameters.AddWithValue("@gid", giveawayId);
                    cmd.Parameters.AddWithValue("@u", kv.Key);
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                tx.Commit();
            }
            finally { ReleaseLock(taken); }
        }

        public async Task<int> GetTicketsForUserAsync(long giveawayId, string username)
        {
            var v = await QueryScalarAsync<long?>(
                "SELECT Tickets FROM GiveawayTickets WHERE GiveawayId = @gid AND Username = @u",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@gid", giveawayId);
                    cmd.Parameters.AddWithValue("@u", username);
                }).ConfigureAwait(false);
            return v.HasValue ? (int)v.Value : 0;
        }

        /// <summary>(total tickets across all users, distinct entrant count).</summary>
        public async Task<(int Total, int Entrants)> GetGiveawayTotalsAsync(long giveawayId)
        {
            int total = 0, entrants = 0;
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand(
                    @"SELECT COALESCE(SUM(Tickets),0), COUNT(*) FROM GiveawayTickets WHERE GiveawayId = @gid",
                    _connection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                cmd.Parameters.AddWithValue("@gid", giveawayId);
                using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                if (await r.ReadAsync().ConfigureAwait(false))
                {
                    total    = r.IsDBNull(0) ? 0 : (int)r.GetInt64(0);
                    entrants = r.IsDBNull(1) ? 0 : (int)r.GetInt64(1);
                }
            }
            finally { ReleaseLock(taken); }
            return (total, entrants);
        }

        /// <summary>Entrants for a giveaway, sorted by ticket count descending.</summary>
        public async Task<List<GiveawayEntrant>> GetGiveawayEntrantsAsync(long giveawayId)
        {
            var list = new List<GiveawayEntrant>();
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand(
                    @"SELECT Username, Role, Tickets, LastEntry, IsMod FROM GiveawayTickets
                      WHERE GiveawayId = @gid ORDER BY Tickets DESC, Username ASC", _connection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                cmd.Parameters.AddWithValue("@gid", giveawayId);
                using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await r.ReadAsync().ConfigureAwait(false))
                {
                    list.Add(new GiveawayEntrant
                    {
                        Username  = r.IsDBNull(0) ? "" : r.GetString(0),
                        Role      = r.IsDBNull(1) ? "viewer" : r.GetString(1),
                        Tickets   = r.IsDBNull(2) ? 0 : (int)r.GetInt64(2),
                        LastEntry = r.IsDBNull(3) ? "" : r.GetString(3),
                        IsMod     = !r.IsDBNull(4) && r.GetInt64(4) != 0,
                    });
                }
            }
            finally { ReleaseLock(taken); }
            return list;
        }

        /// <summary>Appends a winner to the comma-separated Winners list and flips Status to 'drawn'.</summary>
        public async Task AppendGiveawayWinnerAsync(long giveawayId, string winnerName)
        {
            await ExecuteAsync(
                @"UPDATE Giveaways
                  SET Winners = CASE WHEN Winners IS NULL OR Winners = '' THEN @w ELSE Winners || ', ' || @w END,
                      Status  = 'drawn'
                  WHERE Id = @id",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@w", winnerName);
                    cmd.Parameters.AddWithValue("@id", giveawayId);
                }).ConfigureAwait(false);
        }

        public async Task LogGiveawayActivityAsync(long giveawayId, string kind, string message, string timeIso)
        {
            await ExecuteAsync(
                "INSERT INTO GiveawayActivity (GiveawayId, Time, Kind, Message) VALUES (@gid, @t, @k, @m)",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@gid", giveawayId);
                    cmd.Parameters.AddWithValue("@t", timeIso);
                    cmd.Parameters.AddWithValue("@k", kind);
                    cmd.Parameters.AddWithValue("@m", message);
                }).ConfigureAwait(false);
        }

        /// <summary>Recent activity rows for a giveaway, newest first: (timeIso, kind, message).</summary>
        public async Task<List<(string Time, string Kind, string Message)>> GetGiveawayActivityAsync(long giveawayId, int limit = 50)
        {
            var list = new List<(string, string, string)>();
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand(
                    "SELECT Time, Kind, Message FROM GiveawayActivity WHERE GiveawayId = @gid ORDER BY Id DESC LIMIT @lim",
                    _connection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                cmd.Parameters.AddWithValue("@gid", giveawayId);
                cmd.Parameters.AddWithValue("@lim", limit);
                using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await r.ReadAsync().ConfigureAwait(false))
                {
                    list.Add((
                        r.IsDBNull(0) ? "" : r.GetString(0),
                        r.IsDBNull(1) ? "INF" : r.GetString(1),
                        r.IsDBNull(2) ? "" : r.GetString(2)));
                }
            }
            finally { ReleaseLock(taken); }
            return list;
        }
    }
}
