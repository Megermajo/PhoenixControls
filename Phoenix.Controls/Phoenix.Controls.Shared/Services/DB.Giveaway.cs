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
                EntryCommand      TEXT    DEFAULT '!enter',
                TicketsPerMessage INTEGER DEFAULT 1,
                SubscriberBonus   INTEGER DEFAULT 0,
                CapPerUser        INTEGER DEFAULT 0,
                DrawMethod        TEXT    DEFAULT 'weighted',
                Winners           TEXT    DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS GiveawayTickets (
                GiveawayId INTEGER,
                Username   TEXT,
                Role       TEXT    DEFAULT 'viewer',
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

        // SELECT projection that decorates each Giveaways row with the derived
        // entrant count / ticket total / most-recent entry from GiveawayTickets.
        private const string GiveawaySelect = @"
            SELECT g.Id, g.Key, g.Title, g.Status, g.OpenedAt, g.ClosedAt, g.OpenedBy,
                   g.IsDefault, g.EntryCommand, g.TicketsPerMessage, g.SubscriberBonus,
                   g.CapPerUser, g.DrawMethod, g.Winners,
                   (SELECT COUNT(*)            FROM GiveawayTickets t WHERE t.GiveawayId = g.Id) AS Entrants,
                   (SELECT COALESCE(SUM(t.Tickets),0) FROM GiveawayTickets t WHERE t.GiveawayId = g.Id) AS TicketTotal,
                   (SELECT MAX(t.LastEntry)    FROM GiveawayTickets t WHERE t.GiveawayId = g.Id) AS LastEntry
            FROM Giveaways g";

        private static Giveaway ReadGiveaway(SqliteDataReader r) => new()
        {
            Id                = r.GetInt64(0),
            Key               = r.IsDBNull(1) ? "" : r.GetString(1),
            Title             = r.IsDBNull(2) ? "" : r.GetString(2),
            Status            = r.IsDBNull(3) ? "open" : r.GetString(3),
            OpenedAt          = r.IsDBNull(4) ? "" : r.GetString(4),
            ClosedAt          = r.IsDBNull(5) ? null : r.GetString(5),
            OpenedBy          = r.IsDBNull(6) ? "" : r.GetString(6),
            IsDefault         = !r.IsDBNull(7) && r.GetInt64(7) != 0,
            EntryCommand      = r.IsDBNull(8) ? "!enter" : r.GetString(8),
            TicketsPerMessage = r.IsDBNull(9) ? 1 : (int)r.GetInt64(9),
            SubscriberBonus   = r.IsDBNull(10) ? 0 : (int)r.GetInt64(10),
            CapPerUser        = r.IsDBNull(11) ? 0 : (int)r.GetInt64(11),
            DrawMethod        = r.IsDBNull(12) ? "weighted" : r.GetString(12),
            Winners           = r.IsDBNull(13) ? "" : r.GetString(13),
            Entrants          = r.IsDBNull(14) ? 0 : (int)r.GetInt64(14),
            Tickets           = r.IsDBNull(15) ? 0 : (int)r.GetInt64(15),
            LastEntry         = r.IsDBNull(16) ? "" : r.GetString(16),
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
        /// when &gt; 0, and returns the user's new total. A single round-trip
        /// under the shared lock — no read-then-write race even when several
        /// chat handlers fire at once.
        /// </summary>
        public async Task<int> UpsertTicketAsync(long giveawayId, string username, string role,
            int increment, int capPerUser, string lastEntryIso)
        {
            int cap = capPerUser > 0 ? capPerUser : int.MaxValue;
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand(
                    @"INSERT INTO GiveawayTickets (GiveawayId, Username, Role, Tickets, LastEntry)
                      VALUES (@gid, @u, @r, MIN(@inc, @cap), @t)
                      ON CONFLICT(GiveawayId, Username) DO UPDATE SET
                          Tickets   = MIN(Tickets + @inc, @cap),
                          Role      = @r,
                          LastEntry = @t;
                      SELECT Tickets FROM GiveawayTickets WHERE GiveawayId = @gid AND Username = @u;",
                    _connection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                cmd.Parameters.AddWithValue("@gid", giveawayId);
                cmd.Parameters.AddWithValue("@u", username);
                cmd.Parameters.AddWithValue("@r", string.IsNullOrEmpty(role) ? "viewer" : role);
                cmd.Parameters.AddWithValue("@inc", Math.Max(0, increment));
                cmd.Parameters.AddWithValue("@cap", cap);
                cmd.Parameters.AddWithValue("@t", lastEntryIso);
                var v = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                return v is null or DBNull ? 0 : Convert.ToInt32(v, CultureInfo.InvariantCulture);
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
                    @"SELECT Username, Role, Tickets, LastEntry FROM GiveawayTickets
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
