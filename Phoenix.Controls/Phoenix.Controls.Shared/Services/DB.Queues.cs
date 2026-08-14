using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Shared.Services
{
    // Named-queue persistence — the store behind the generalised Queue.* node band
    // and, through it, the User-Management viewer queue.
    //
    // OPEN-TABLE DOCTRINE (same as DB.Counters.cs / DB.Quotes.cs): the entries live
    // in an OPEN user table ("Queues") DELIBERATELY NOT in DB._systemTables, so a
    // streamer keeps full db.* script access to the line — reading who is waiting is
    // the single most common thing a custom overlay or a !whosnext command wants, and
    // locking it would force every such graph to go through a node we happened to
    // ship. The DB is the single source of truth: nothing here is cached, because
    // db.* scripts and the tool write the same table.
    //
    // ORDERING is (priority DESC, rowid ASC) everywhere — one ORDER BY, used by the
    // list read, the position read and the pop, so the answer a viewer is told and the
    // entry that actually comes off the front can never disagree. All-zero priorities
    // degrade to plain insertion-order FIFO, which is what the legacy pipe-string
    // queue does and therefore what an author already expects.
    //
    // ★ The LEGACY unnamed queue is NOT here. Queue.* with no Name stays on the
    // pipe-delimited Var global._event_queue (ScriptManager.Queue.cs) — live scripts
    // read that var directly, so migrating it would regress them.
    //
    // Every mutation is one transaction under the shared AcquireLockAsync guard and is
    // ROWID-SCOPED: pop and remove SELECT the front-most matching row and then delete
    // BY ROWID, never `DELETE ... WHERE entry = @e`. That is the same discipline
    // DB.Counters.AddCountTx applies for the same reason — the open table carries NO
    // UNIQUE constraint (a streamer's own rows are welcome in it), so a name-scoped
    // write would hit every duplicate at once. There is no upsert here at all, and that
    // is not an omission: a queue only ever appends and removes, so the ON CONFLICT
    // question the counter/balance tables have to answer never arises.
    // CoerceBalance / IsMissingTableOrColumn are shared with DB.Giveaway.cs.
    public partial class DB
    {
        /// <summary>Fixed name of the OPEN named-queue table. NOT a system table (db.* readable).</summary>
        internal const string QueuesTable = "Queues";

        /// <summary>The ordering every read and the pop share: priority first, then
        /// insertion order. Kept as one constant so a future change can't move the
        /// front of the line for one caller and not another.</summary>
        private const string QueueOrderBy = "ORDER BY [priority] DESC, rowid ASC";

        /// <summary>The table's shape. One spelling, shared by the explicit ensure and by
        /// the push path's self-heal, so the two can never create it differently.</summary>
        private const string QueuesTableDdl =
            "CREATE TABLE IF NOT EXISTS [" + QueuesTable + "] (rowid INTEGER PRIMARY KEY AUTOINCREMENT, " +
            "[name] TEXT, [entry] TEXT, [payload] TEXT, [priority] INTEGER);";

        /// <summary>Creates the OPEN "Queues" table if missing. Never a system table —
        /// full db.* access is the point. A pre-existing user table of the same name is
        /// left untouched.</summary>
        public async Task EnsureQueuesTableAsync()
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand(QueuesTableDdl, _connection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>Appends an entry to the back of the named queue's priority tier and
        /// returns the queue's new length. Duplicates are NOT rejected here — dedup is a
        /// policy the caller owns (the viewer queue refuses a second !join; a script
        /// work-queue may legitimately push the same id twice).
        ///
        /// The missing-table guard CREATES AND RETRIES instead of degrading to a neutral
        /// value the way every read verb below does, and the asymmetry is the point: a
        /// read that answers "empty" for a table that is not there is honest, while a push
        /// answering "length 0" would hand the caller a number it prints straight to chat
        /// ("you are #0 of 0") for an entry that was silently dropped. This table is
        /// Hub-owned, and NamedQueueService ensures it exactly once per process behind a
        /// latch — so a databank replaced or rebuilt after that latch flipped leaves the
        /// store missing with nothing left to re-create it. Re-running the same idempotent
        /// DDL costs one statement on a path that only runs when the table is genuinely
        /// gone. A second failure propagates exactly as it does today: CREATE IF NOT
        /// EXISTS cannot repair a streamer's own pre-existing "Queues" table that is
        /// missing a column, and pretending otherwise would put us back at the silent
        /// drop.</summary>
        public async Task<int> QueuePushAsync(string queue, string entry, string payload, long priority)
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    return await QueuePushOnceAsync(queue, entry, payload, priority).ConfigureAwait(false);
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex))
                {
                    GlobalLogger.Log(
                        $"Queue push found the open '{QueuesTable}' table missing ({ex.Message}) — recreating it and retrying the push.",
                        "System.DB", LogLevel.System);
                    using (var ddl = new SqliteCommand(QueuesTableDdl, _connection))
                    {
                        ddl.CommandTimeout = CommandTimeoutSeconds;
                        await ddl.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                    return await QueuePushOnceAsync(queue, entry, payload, priority).ConfigureAwait(false);
                }
            }
            finally { ReleaseLock(taken); }
        }

        // One insert + post-insert count in one transaction. Split out of QueuePushAsync
        // so the self-heal above can run the identical attempt twice without duplicating
        // the statement pair — and deliberately WITHOUT taking the lock, because the
        // caller already holds it for both attempts and the recreate in between.
        private async Task<int> QueuePushOnceAsync(string queue, string entry, string payload, long priority)
        {
            using var tx = _connection!.BeginTransaction();
            try
            {
                using (var ins = new SqliteCommand(
                    $"INSERT INTO [{QueuesTable}] ([name], [entry], [payload], [priority]) VALUES (@q, @e, @p, @pr)",
                    _connection, tx))
                {
                    ins.CommandTimeout = CommandTimeoutSeconds;
                    ins.Parameters.AddWithValue("@q", queue ?? "");
                    ins.Parameters.AddWithValue("@e", entry ?? "");
                    ins.Parameters.AddWithValue("@p", payload ?? "");
                    ins.Parameters.AddWithValue("@pr", priority);
                    await ins.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                int len = await QueueCountTx(tx, queue ?? "").ConfigureAwait(false);
                tx.Commit();
                return len;
            }
            catch { try { tx.Rollback(); } catch { } throw; }
        }

        /// <summary>Removes and returns the FRONT entry of the named queue, or null when
        /// it is empty / the table is missing. Read and delete share one transaction so a
        /// concurrent pop can never hand the same entry to two callers.</summary>
        public async Task<QueueEntry?> QueuePopAsync(string queue)
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    using var tx = _connection!.BeginTransaction();
                    try
                    {
                        QueueEntry? head = null;
                        using (var sel = new SqliteCommand(
                            $"SELECT rowid, [entry], [payload], [priority] FROM [{QueuesTable}] " +
                            $"WHERE [name] = @q COLLATE NOCASE {QueueOrderBy} LIMIT 1",
                            _connection, tx))
                        {
                            sel.CommandTimeout = CommandTimeoutSeconds;
                            sel.Parameters.AddWithValue("@q", queue ?? "");
                            using var r = await sel.ExecuteReaderAsync().ConfigureAwait(false);
                            if (await r.ReadAsync().ConfigureAwait(false))
                            {
                                head = new QueueEntry
                                {
                                    RowId = r.GetInt64(0),
                                    Queue = queue ?? "",
                                    Entry = r.IsDBNull(1) ? "" : r.GetString(1),
                                    Payload = r.IsDBNull(2) ? "" : r.GetString(2),
                                    Priority = CoerceBalance(r.IsDBNull(3) ? null : r.GetValue(3)),
                                    Position = 1,
                                };
                            }
                        }
                        if (head is null) { tx.Rollback(); return null; }

                        using (var del = new SqliteCommand(
                            $"DELETE FROM [{QueuesTable}] WHERE rowid = @r", _connection, tx))
                        {
                            del.CommandTimeout = CommandTimeoutSeconds;
                            del.Parameters.AddWithValue("@r", head.RowId);
                            await del.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                        tx.Commit();
                        return head;
                    }
                    catch { try { tx.Rollback(); } catch { } throw; }
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return null; }
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>Every entry in the named queue, front first. Empty when the queue —
        /// or the whole table — does not exist.</summary>
        public async Task<List<QueueEntry>> QueueListAsync(string queue)
        {
            var list = new List<QueueEntry>();
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    using var cmd = new SqliteCommand(
                        $"SELECT rowid, [entry], [payload], [priority] FROM [{QueuesTable}] " +
                        $"WHERE [name] = @q COLLATE NOCASE {QueueOrderBy}", _connection);
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    cmd.Parameters.AddWithValue("@q", queue ?? "");
                    using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                    int pos = 0;
                    while (await r.ReadAsync().ConfigureAwait(false))
                    {
                        pos++;
                        list.Add(new QueueEntry
                        {
                            RowId = r.GetInt64(0),
                            Queue = queue ?? "",
                            Entry = r.IsDBNull(1) ? "" : r.GetString(1),
                            Payload = r.IsDBNull(2) ? "" : r.GetString(2),
                            // CoerceBalance (not Convert.ToInt64) matches every other read of
                            // an OPEN table: a db.*-stored non-integer or BLOB coerces to 0
                            // instead of throwing an uncaught FormatException that would blank
                            // the whole line.
                            Priority = CoerceBalance(r.IsDBNull(3) ? null : r.GetValue(3)),
                            Position = pos,
                        });
                    }
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { /* empty */ }
            }
            finally { ReleaseLock(taken); }
            return list;
        }

        /// <summary>How many entries the named queue holds (0 when missing).</summary>
        public async Task<int> QueueLengthAsync(string queue)
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    using var cmd = new SqliteCommand(
                        $"SELECT COUNT(*) FROM [{QueuesTable}] WHERE [name] = @q COLLATE NOCASE", _connection);
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    cmd.Parameters.AddWithValue("@q", queue ?? "");
                    return (int)CoerceBalance(await cmd.ExecuteScalarAsync().ConfigureAwait(false));
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return 0; }
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>The 1-based place of <paramref name="entry"/> in the named queue, or 0
        /// when it is not queued.
        ///
        /// Counted as "how many rows sort AHEAD of me" rather than by paging the list and
        /// finding an index, so one round-trip answers it and the tie-break is spelled out
        /// in the same (priority, rowid) terms <see cref="QueueOrderBy"/> uses — the number
        /// a viewer is told and the row that actually pops can never disagree. No match
        /// yields no rows, which ExecuteScalar reports as null and CoerceBalance turns into
        /// the documented 0.</summary>
        public async Task<int> QueuePositionAsync(string queue, string entry)
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    using var cmd = new SqliteCommand(
                        $"SELECT 1 + (SELECT COUNT(*) FROM [{QueuesTable}] a " +
                        $"            WHERE a.[name] = @q COLLATE NOCASE " +
                        $"              AND (a.[priority] > m.[priority] " +
                        $"                OR (a.[priority] = m.[priority] AND a.rowid < m.rowid))) " +
                        $"FROM [{QueuesTable}] m " +
                        $"WHERE m.[name] = @q COLLATE NOCASE AND m.[entry] = @e COLLATE NOCASE " +
                        $"ORDER BY m.[priority] DESC, m.rowid ASC LIMIT 1",
                        _connection);
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    cmd.Parameters.AddWithValue("@q", queue ?? "");
                    cmd.Parameters.AddWithValue("@e", entry ?? "");
                    return (int)CoerceBalance(await cmd.ExecuteScalarAsync().ConfigureAwait(false));
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return 0; }
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>Removes the FRONT-most row matching <paramref name="entry"/> and returns
        /// it, or null when it was not queued. Rowid-scoped: a hand-built table holding two
        /// rows with the same entry loses exactly one of them, never both.</summary>
        public async Task<QueueEntry?> QueueRemoveAsync(string queue, string entry)
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    using var tx = _connection!.BeginTransaction();
                    try
                    {
                        QueueEntry? hit = null;
                        using (var sel = new SqliteCommand(
                            $"SELECT rowid, [entry], [payload], [priority] FROM [{QueuesTable}] " +
                            $"WHERE [name] = @q COLLATE NOCASE AND [entry] = @e COLLATE NOCASE " +
                            $"{QueueOrderBy} LIMIT 1", _connection, tx))
                        {
                            sel.CommandTimeout = CommandTimeoutSeconds;
                            sel.Parameters.AddWithValue("@q", queue ?? "");
                            sel.Parameters.AddWithValue("@e", entry ?? "");
                            using var r = await sel.ExecuteReaderAsync().ConfigureAwait(false);
                            if (await r.ReadAsync().ConfigureAwait(false))
                            {
                                hit = new QueueEntry
                                {
                                    RowId = r.GetInt64(0),
                                    Queue = queue ?? "",
                                    Entry = r.IsDBNull(1) ? "" : r.GetString(1),
                                    Payload = r.IsDBNull(2) ? "" : r.GetString(2),
                                    Priority = CoerceBalance(r.IsDBNull(3) ? null : r.GetValue(3)),
                                };
                            }
                        }
                        if (hit is null) { tx.Rollback(); return null; }

                        using (var del = new SqliteCommand(
                            $"DELETE FROM [{QueuesTable}] WHERE rowid = @r", _connection, tx))
                        {
                            del.CommandTimeout = CommandTimeoutSeconds;
                            del.Parameters.AddWithValue("@r", hit.RowId);
                            await del.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                        tx.Commit();
                        return hit;
                    }
                    catch { try { tx.Rollback(); } catch { } throw; }
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return null; }
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>Empties the named queue (no-op when absent).</summary>
        public async Task QueueClearAsync(string queue)
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    using var cmd = new SqliteCommand(
                        $"DELETE FROM [{QueuesTable}] WHERE [name] = @q COLLATE NOCASE", _connection);
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    cmd.Parameters.AddWithValue("@q", queue ?? "");
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { /* nothing to clear */ }
            }
            finally { ReleaseLock(taken); }
        }

        // Length inside an open transaction — the push path needs the post-insert count
        // without releasing the lock in between, otherwise the number the viewer is told
        // ("you are #3 of 4") could be computed against a line a concurrent join already
        // changed.
        private async Task<int> QueueCountTx(SqliteTransaction tx, string queue)
        {
            using var cmd = new SqliteCommand(
                $"SELECT COUNT(*) FROM [{QueuesTable}] WHERE [name] = @q COLLATE NOCASE", _connection, tx);
            cmd.CommandTimeout = CommandTimeoutSeconds;
            cmd.Parameters.AddWithValue("@q", queue ?? "");
            return (int)CoerceBalance(await cmd.ExecuteScalarAsync().ConfigureAwait(false));
        }
    }
}
