using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Shared.Services
{
    // Timer persistence — the DB half of the subathon-countdown system. Pure
    // storage: each named StreamTimer is serialized WHOLE as JSON into the
    // Timers table (the authoritative RemainingMs / State / settings all live
    // in that blob so a countdown survives a Hub restart), with Name /
    // IsDefault mirrored into indexed columns so the picker + default lookup
    // don't have to parse every JSON row. The tick loop, event→seconds
    // mapping and change events live in the Hub TimerService, not here — DB
    // only round-trips the blob and appends activity rows. Both tables are
    // listed in _systemTables (DB.cs) so the generic db.* script commands and
    // the remote bridge can't mutate them. All methods route through the
    // shared AcquireLockAsync/ReleaseLock guard so they serialize with every
    // other DB.* caller (and re-enter safely from a handler already holding
    // the lock).
    public partial class DB
    {
        internal const string TimerTablesDdl = @"
            CREATE TABLE IF NOT EXISTS Timers (
                Slug       TEXT PRIMARY KEY,
                Name       TEXT    NOT NULL,
                IsDefault  INTEGER NOT NULL DEFAULT 0,
                Json       TEXT    NOT NULL,
                UpdatedAt  INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS TimerActivity (
                Id      INTEGER PRIMARY KEY AUTOINCREMENT,
                Slug    TEXT NOT NULL,
                Time    TEXT NOT NULL,
                Kind    TEXT NOT NULL,
                Message TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_timeractivity_slug ON TimerActivity(Slug);";

        // Schema migration for databanks created by an earlier dev build that
        // carried a partial Timers table (fewer columns than the current DDL).
        // CREATE TABLE IF NOT EXISTS never alters an existing table, so any
        // column added after that build must be back-filled explicitly. The
        // ALTERs all carry a DEFAULT — SQLite refuses to ADD a NOT NULL column
        // without one to an already-populated table — so a fresh install (all
        // columns already present via the DDL above) simply no-ops through the
        // probe. Same probe-then-ALTER shape as EnsureGiveawaySchemaMigrations;
        // called synchronously from DB.Initialize right after TimerTablesDdl.
        private void EnsureTimerSchemaMigrations()
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var probe = new SqliteCommand("PRAGMA table_info(Timers)", _connection))
            using (var r = probe.ExecuteReader())
            {
                while (r.Read()) existing.Add(r.GetString(1));
            }

            var wanted = new (string Column, string Ddl)[]
            {
                ("Name",      "ALTER TABLE Timers ADD COLUMN Name TEXT DEFAULT ''"),
                ("IsDefault", "ALTER TABLE Timers ADD COLUMN IsDefault INTEGER NOT NULL DEFAULT 0"),
                ("Json",      "ALTER TABLE Timers ADD COLUMN Json TEXT DEFAULT ''"),
                ("UpdatedAt", "ALTER TABLE Timers ADD COLUMN UpdatedAt INTEGER NOT NULL DEFAULT 0"),
            };
            foreach (var (column, ddl) in wanted)
            {
                if (existing.Contains(column)) continue;
                using var alter = new SqliteCommand(ddl, _connection);
                alter.ExecuteNonQuery();
            }
        }

        // Deserialize one Timers row's JSON blob back into a StreamTimer,
        // re-syncing the Slug / Name / IsDefault mirror columns onto the object
        // so a hand-edited column (or a legacy blob missing them) still surfaces
        // the row's canonical identity. A blob that fails to parse yields a
        // minimal StreamTimer carrying just the mirror-column identity rather
        // than dropping the row entirely.
        private static StreamTimer ReadTimer(SqliteDataReader r)
        {
            string slug = CellText(r, 0);
            string name = CellText(r, 1);
            // Coerced, not GetInt64. The Json blob was already hand-edit-proof
            // (the try/catch below), but IsDefault was the one unguarded typed
            // read left, and Timers became an OPEN table in the 2026-08 unlock:
            // db.set_cell binds TEXT, so this INTEGER column can now hold "yes".
            bool isDefault = !r.IsDBNull(2) && CoerceBalance(r.GetValue(2)) != 0;
            string json = CellText(r, 3);

            StreamTimer? t = null;
            if (!string.IsNullOrWhiteSpace(json))
            {
                try { t = JsonSerializer.Deserialize<StreamTimer>(json); }
                catch (JsonException) { t = null; }
            }
            t ??= new StreamTimer();
            t.Slug = slug;
            t.Name = name;
            t.IsDefault = isDefault;
            return t;
        }

        /// <summary>All persisted timers, newest-updated first.</summary>
        public async Task<List<StreamTimer>> GetTimersAsync()
        {
            var list = new List<StreamTimer>();
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand(
                    "SELECT Slug, Name, IsDefault, Json FROM Timers ORDER BY UpdatedAt DESC, Slug ASC",
                    _connection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await r.ReadAsync().ConfigureAwait(false))
                    list.Add(ReadTimer((SqliteDataReader)r));
            }
            finally { ReleaseLock(taken); }
            return list;
        }

        /// <summary>Single timer by slug, or null when no such row exists.</summary>
        public async Task<StreamTimer?> GetTimerAsync(string slug)
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand(
                    "SELECT Slug, Name, IsDefault, Json FROM Timers WHERE Slug = @slug LIMIT 1",
                    _connection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                cmd.Parameters.AddWithValue("@slug", slug ?? "");
                using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                if (await r.ReadAsync().ConfigureAwait(false))
                    return ReadTimer((SqliteDataReader)r);
            }
            finally { ReleaseLock(taken); }
            return null;
        }

        /// <summary>
        /// Inserts or replaces a timer — the whole StreamTimer is serialized to
        /// the Json blob, with Name / IsDefault / UpdatedAt mirrored into their
        /// columns so the picker and default lookup stay index-cheap. The
        /// UpdatedAt column is taken from the timer's own stamp (the service
        /// bumps it on every state/settings change).
        /// </summary>
        public async Task UpsertTimerAsync(StreamTimer t)
        {
            if (t is null) return;
            string json = JsonSerializer.Serialize(t);
            await ExecuteAsync(
                @"INSERT INTO Timers (Slug, Name, IsDefault, Json, UpdatedAt)
                  VALUES (@slug, @name, @def, @json, @upd)
                  ON CONFLICT(Slug) DO UPDATE SET
                      Name      = @name,
                      IsDefault = @def,
                      Json      = @json,
                      UpdatedAt = @upd",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@slug", t.Slug ?? "");
                    cmd.Parameters.AddWithValue("@name", t.Name ?? "");
                    cmd.Parameters.AddWithValue("@def", t.IsDefault ? 1 : 0);
                    cmd.Parameters.AddWithValue("@json", json);
                    cmd.Parameters.AddWithValue("@upd", t.UpdatedAtUnixMs);
                }).ConfigureAwait(false);
        }

        /// <summary>Deletes a timer and its activity rows.</summary>
        public async Task DeleteTimerAsync(string slug)
        {
            await ExecuteAsync(
                "DELETE FROM Timers WHERE Slug = @slug;" +
                "DELETE FROM TimerActivity WHERE Slug = @slug;",
                cmd => cmd.Parameters.AddWithValue("@slug", slug ?? "")).ConfigureAwait(false);
        }

        /// <summary>Sets the single app-wide default timer — clears IsDefault on
        /// every other row and sets it on <paramref name="slug"/>, in one
        /// transaction under the shared lock so no window ever shows two
        /// defaults (or none) to a concurrent reader.</summary>
        public async Task SetDefaultTimerAsync(string slug)
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var tx = _connection!.BeginTransaction();
                using (var clear = new SqliteCommand("UPDATE Timers SET IsDefault = 0", _connection, tx))
                {
                    clear.CommandTimeout = CommandTimeoutSeconds;
                    await clear.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                using (var set = new SqliteCommand(
                    "UPDATE Timers SET IsDefault = 1 WHERE Slug = @slug", _connection, tx))
                {
                    set.CommandTimeout = CommandTimeoutSeconds;
                    set.Parameters.AddWithValue("@slug", slug ?? "");
                    await set.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                tx.Commit();
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>Slug of the current default timer, or null when none is flagged.</summary>
        public async Task<string?> GetDefaultTimerSlugAsync()
        {
            return await QueryScalarAsync<string>(
                "SELECT Slug FROM Timers WHERE IsDefault = 1 ORDER BY UpdatedAt DESC LIMIT 1",
                _ => { }).ConfigureAwait(false);
        }

        public async Task LogTimerActivityAsync(string slug, string kind, string message, string isoTime)
        {
            await ExecuteAsync(
                "INSERT INTO TimerActivity (Slug, Time, Kind, Message) VALUES (@slug, @t, @k, @m)",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@slug", slug ?? "");
                    cmd.Parameters.AddWithValue("@t", isoTime ?? "");
                    cmd.Parameters.AddWithValue("@k", kind ?? "INF");
                    cmd.Parameters.AddWithValue("@m", message ?? "");
                }).ConfigureAwait(false);
        }

        /// <summary>Recent activity rows for a timer, newest first: (timeIso, kind, message).</summary>
        public async Task<List<(string Time, string Kind, string Message)>> GetTimerActivityAsync(string slug, int limit = 100)
        {
            var list = new List<(string, string, string)>();
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand(
                    "SELECT Time, Kind, Message FROM TimerActivity WHERE Slug = @slug ORDER BY Id DESC LIMIT @lim",
                    _connection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                cmd.Parameters.AddWithValue("@slug", slug ?? "");
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
