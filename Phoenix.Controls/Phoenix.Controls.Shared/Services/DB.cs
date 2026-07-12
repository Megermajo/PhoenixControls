// Concurrency / retention / WAL invariants
// ---------------------------------------------------
// 1. `_lock` is a SemaphoreSlim(1,1) and is NON-REENTRANT. A handler that
//    holds it and re-enters another DB.* method on the SAME async context
//    would deadlock. The class guards that case via the AsyncLocal<bool>
//    `_heldByThisAsyncCtx` flag: the AcquireLockAsync / ReleaseLock helpers
//    skip both the WaitAsync and the Release when the current async context
//    already owns the permit, so script handlers can chain DB calls without
//    seizing up. NEVER call _lock.WaitAsync/Release directly inside this
//    file — go through AcquireLockAsync/ReleaseLock to keep the guard intact.
// 2. `EventLog` / `SystemHistory` are append-only and grow unbounded; the
//    Initialize sweep deletes rows older than AppConfig.LogRetentionDays
//    once per process start, and EventLog is additionally capped to the
//    newest AppConfig.EventLogRetentionRows rows by a startup + daily
//    sweep (_eventLogRowCapTimer). Set either cap to 0 to disable.
// 3. WAL on disk grows between Hub restarts; `_walCheckpointTimer` (30 min
//    cadence, started in Initialize) runs `PRAGMA wal_checkpoint(RESTART)`
//    via the AsyncErrorBoundary so the .wal file size stays bounded.
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Shared.Services
{
    public partial class DB : IDisposable, IScriptDb
    {
        // Double-checked locking on the singleton accessor. The previous
        // `??=` is not thread-safe; concurrent first-touch paths (Hub launching
        // Architect via several Task.Run(() => DB.Instance.Initialize())
        // sites) could each construct a DB and run the WAL/DDL block
        // twice on the winner. We keep _instance settable so Dispose() can null
        // it for the test seam.
        private static DB? _instance;
        private static readonly object _instanceLock = new();
        public static DB Instance
        {
            get
            {
                var inst = _instance;
                if (inst != null) return inst;
                lock (_instanceLock)
                {
                    return _instance ??= new DB();
                }
            }
        }

        private string _dbPath;
        private string _connectionString;

        /// <summary>
        /// True when Initialize ran and the underlying SqliteConnection is open.
        /// Surface for the Hub status strip and any health probe; cheap to call.
        /// </summary>
        public bool IsHealthy => _connection != null && _connection.State == ConnectionState.Open;

        /// <summary>Resolved on-disk file backing the SQLite connection — exposed for
        /// status-strip tooltips so the user can see exactly which DB Hub is hitting.</summary>
        public string DatabasePath => _dbPath;

        // Shared persistent connection + semaphore to serialize access
        private SqliteConnection? _connection;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        // Re-entry guard. SemaphoreSlim is non-reentrant;
        // a DB.* method that calls another DB.* method on the same async
        // context (e.g. ClearTableAsync awaits LogEventAsync internally)
        // would deadlock under the bare WaitAsync. AsyncLocal flows through
        // awaits in the same logical call so the inner acquire sees the flag
        // set by the outer and skips both WaitAsync and Release.
        // Use only via AcquireLockAsync / ReleaseLock — never read directly.
        private static readonly AsyncLocal<bool> _heldByThisAsyncCtx = new();

        // Periodic WAL checkpoint. SQLite only auto-
        // checkpoints when the WAL reaches ~1000 pages (~4MB) so a quiet but
        // long-running Hub session can otherwise grow the .wal file far past
        // the main .db. The timer runs PRAGMA wal_checkpoint(RESTART) every
        // 30 minutes; RESTART blocks new readers briefly so subsequent
        // writers can recycle WAL frames from the start of the file.
        private System.Threading.Timer? _walCheckpointTimer;
        private static readonly TimeSpan WalCheckpointInterval = TimeSpan.FromMinutes(30);

        // EventLog row-cap sweep. The day-based retention sweep bounds age,
        // but a busy 24/7 stream writes multi-KB raw-JSON audit rows fast
        // enough to outgrow the day window long before it expires. This timer
        // (plus a startup pass in Initialize) keeps only the newest
        // AppConfig.EventLogRetentionRows rows; the config value is re-read
        // on every tick so a settings change applies without a restart.
        private System.Threading.Timer? _eventLogRowCapTimer;
        private static readonly TimeSpan EventLogRowCapInterval = TimeSpan.FromHours(24);

        // Initialize is sync (callers don't await), so racing threads
        // could each pass the early-out check and double-Open a SqliteConnection.
        // A dedicated sync lock guards the init body without entangling the
        // async _lock that serializes per-query access.
        private readonly object _initLock = new();

        // One-shot disposal flag. Read/written under _initLock. Once
        // set, EnsureConnected refuses to resurrect the connection and the
        // public surface throws ObjectDisposedException. The singleton-null
        // step in Dispose() means production callers normally just get a
        // fresh DB instance on next access, but a test seam that stashes a
        // local reference to the disposed instance must observe the disposal
        // rather than silently re-opening a SqliteConnection.
        private bool _disposed;

        private DB()
        {
            // Shared across Hub and Architect — AppData ensures both processes find the same file.
            // Filename bumped to phoenix_v3.db as part of the brand alignment (Majo
            // confirmed no installed clients on the legacy sovereign_v2.db naming, so
            // the rename ships without a migration shim — fresh installs and existing
            // dev installs alike just get a new DB next to the old one if any).
            _dbPath = Phoenix.Controls.Shared.Core.Paths.RoamingAppData("phoenix_v3.db");
            _connectionString = $"Data Source={_dbPath};";
        }

        public void Initialize(string? customPath = null)
        {
            // Guard the entire body with _initLock so concurrent callers
            // can't both pass the early-out check and double-Open a connection.
            lock (_initLock)
            {
                // Singleton lifecycle: once Dispose has torn the
                // shared connection down, any subsequent Initialize on the
                // same instance is a bug — the test seam in Dispose() also
                // nulls _instance so the next DB.Instance access yields a
                // fresh object. Throw rather than silently resurrect.
                if (_disposed)
                    throw new ObjectDisposedException(nameof(DB));

                // If the connection is already open and the caller
                // wants to redirect the singleton to a NEW path, that is a
                // hard error — switching the backing file out from under
                // already-acquired connection handles silently re-routes every
                // subsequent query to the new path while logs/vars/event-rows
                // from the old session vanish from view. Tests that need a
                // fresh path MUST Dispose first (which nulls _instance) and
                // re-acquire DB.Instance.
                if (_connection != null && _connection.State == System.Data.ConnectionState.Open)
                {
                    if (!string.IsNullOrWhiteSpace(customPath) &&
                        !string.Equals(customPath, _dbPath, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"DB.Initialize: connection already open at '{_dbPath}'. " +
                            $"Refusing to silently re-target '{customPath}'. " +
                            "Dispose the singleton first to switch backing files.");
                    }
                    return;
                }

                // Test seam: when a fixture path is supplied, redirect this singleton's
                // backing file. Combined with Dispose() (which nulls _instance), tests
                // can swap in a temp .db, run, and dispose to restore the default path
                // for subsequent tests. No-arg callers in production are unaffected.
                if (!string.IsNullOrWhiteSpace(customPath))
                {
                    _dbPath = customPath;
                    _connectionString = $"Data Source={_dbPath};";
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);

                _connection = new SqliteConnection(_connectionString);
                _connection.Open();

                GlobalLogger.Log($"Phoenix Controls databank initialized at: {_dbPath}", "System.DB");

                // Verify the on-disk DB is structurally valid BEFORE
                // wal_checkpoint or DDL runs. A SHM/wal-index desync (observed
                // 2026-05-08 on the live phoenix_v3.db) can leave the WAL holding
                // commit frames whose `db_size_after_commit` field truncates the
                // main file far below the schema's referenced root pages. Once
                // those frames checkpoint, every subsequent open returns
                // "database disk image is malformed" and the Architect Databank
                // tab can only enumerate the few tables whose roots survived the
                // truncate. quick_check is the cheap variant of integrity_check
                // and catches this class of structural damage; we treat any
                // non-"ok" result OR SqliteException as fatal-to-this-file.
                if (!IsDatabaseHealthyLocked())
                {
                    GlobalLogger.Log(
                        $"Databank at '{_dbPath}' failed structural check on open. " +
                        "Quarantining the corrupt file set and rebuilding fresh.",
                        "System.DB", LogLevel.CriticalError);
                    QuarantineCorruptDatabaseLocked();
                    _connection = new SqliteConnection(_connectionString);
                    _connection.Open();
                }

                // Drain any stale WAL/SHM state by toggling journal_mode
                // through DELETE before re-enabling WAL. The 2026-05-08 corruption
                // was driven by a stale wal-index in the .db-shm carrying a
                // smaller `db_size` view than the main file, so every subsequent
                // commit landed a truncating frame. Forcing DELETE makes SQLite
                // checkpoint+drop the WAL and remove the SHM, then the WAL set
                // below re-creates them in sync with the current main-file size.
                //
                // Gate the drain on the .wal sibling actually existing — the
                // common case after a clean Hub shutdown is no WAL on disk, so
                // running PRAGMA DELETE was producing a noisy "database is
                // locked" line on every clean boot that wasn't telling Majo
                // anything useful. The drain still runs whenever a .wal exists,
                // which is exactly the only state where stale-index hazard is
                // possible.
                string walPath = _dbPath + "-wal";
                if (File.Exists(walPath))
                {
                    try
                    {
                        using var drain = new SqliteCommand("PRAGMA journal_mode=DELETE;", _connection);
                        // Cap the lock-wait like every other
                        // command in this file. Without it the drain inherits
                        // Microsoft.Data.Sqlite's 30s default command timeout, so
                        // a DELETE checkpoint blocked by the boot log-writer
                        // connection retried SQLITE_BUSY for a full 30s.
                        drain.CommandTimeout = CommandTimeoutSeconds;
                        drain.ExecuteNonQuery();
                    }
                    catch (SqliteException ex)
                    {
                        // A blocked drain (another connection holding a read lock)
                        // is non-fatal — we still re-enter WAL below. Debug tier
                        // because by this point we know a WAL existed and the
                        // drain attempt is diagnostic-only.
                        GlobalLogger.Log(
                            $"WAL drain via journal_mode=DELETE failed at '{_dbPath}': {ex.Message}. " +
                            "Continuing with WAL re-enable; existing SHM state will persist.",
                            "System.DB", LogLevel.Debug);
                    }
                }

                // Enable WAL mode for concurrent read/write
                using (var command = new SqliteCommand("PRAGMA journal_mode=WAL;", _connection))
                {
                    command.CommandTimeout = CommandTimeoutSeconds;
                    command.ExecuteNonQuery();
                }

                // NORMAL is the documented safe pairing with WAL: commits
                // stop fsync-ing individually and the WAL is synced only at
                // checkpoint, cutting per-commit fsync cost dramatically for
                // every write on this connection (vars, events, upserts).
                // A hard power/OS crash can lose the transactions since the
                // last checkpoint but can never corrupt the file — distinct
                // from the 2026-05-08 "malformed image" class, which was a
                // stale wal-index issue unrelated to the synchronous level.
                // synchronous is per-connection state (unlike journal_mode,
                // which persists in the file), so the dedicated log/read
                // connections set it for themselves on open.
                using (var command = new SqliteCommand("PRAGMA synchronous=NORMAL;", _connection))
                {
                    command.CommandTimeout = CommandTimeoutSeconds;
                    command.ExecuteNonQuery();
                }

                // Flush pending WAL data so external tools
                // (e.g. the VS Code SQLite viewer) see recent state. PASSIVE, not
                // TRUNCATE: TRUNCATE takes an exclusive lock and waits for every
                // other connection to release — at boot the GlobalLogger writer
                // pump (a second connection started before DB.Initialize) is
                // actively inserting boot-log rows, so the TRUNCATE checkpoint
                // blocked on it for the full 30s command timeout EVERY launch
                // (the ~30.2s "DB.Initialize" stall in the rolling log). PASSIVE
                // checkpoints whatever frames it can grab without blocking and
                // returns immediately, so a contending writer can't pin it. The
                // .wal stays bounded during the session by the 30-min periodic
                // wal_checkpoint(RESTART) timer (_walCheckpointTimer) that
                // Initialize already starts — full-flush convenience without the
                // boot-time cliff. CommandTimeout still capped at 5s as a
                // belt-and-suspenders bound on any residual contention.
                using (var command = new SqliteCommand("PRAGMA wal_checkpoint(PASSIVE);", _connection))
                {
                    command.CommandTimeout = CommandTimeoutSeconds;
                    command.ExecuteNonQuery();
                }

                // Table 1: System History / Logs
                string createLogsTable = @"
                    CREATE TABLE IF NOT EXISTS SystemHistory (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Timestamp DATETIME DEFAULT CURRENT_TIMESTAMP,
                        Level TEXT,
                        Source TEXT,
                        Message TEXT,
                        RawData TEXT
                    );";

                // Table 2: Persistent Variables
                string createVarsTable = @"
                    CREATE TABLE IF NOT EXISTS Vars (
                        VarKey TEXT PRIMARY KEY,
                        VarValue TEXT,
                        LastModified DATETIME DEFAULT CURRENT_TIMESTAMP
                    );";

                // Table 3: Event Log
                string createEventsTable = @"
                    CREATE TABLE IF NOT EXISTS EventLog (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Timestamp DATETIME DEFAULT CURRENT_TIMESTAMP,
                        EventSource TEXT,
                        EventType TEXT,
                        User TEXT,
                        Payload TEXT
                    );";

                // Indexes on Timestamp — both EventLog and SystemHistory grow
                // unbounded, and the retention DELETE below scans them by
                // Timestamp. Without these indexes the boot-time sweep degraded
                // to a full table scan + WAL drain that landed at ~31 s on
                // user installs that had been running for weeks (DB.Initialize
                // end, elapsed 31643ms). Cheap to
                // build once; pays back every subsequent retention sweep and
                // any future Timestamp-range query.
                string createTimestampIndexes = @"
                    CREATE INDEX IF NOT EXISTS idx_eventlog_ts        ON EventLog(Timestamp);
                    CREATE INDEX IF NOT EXISTS idx_systemhistory_ts   ON SystemHistory(Timestamp);";

                using (var cmd = new SqliteCommand(
                    createLogsTable + createVarsTable + createEventsTable + createTimestampIndexes,
                    _connection))
                    cmd.ExecuteNonQuery();

                // Giveaway system tables (Giveaways / GiveawayTickets /
                // GiveawayActivity) — DDL + CRUD live in DB.Giveaway.cs.
                using (var cmd = new SqliteCommand(GiveawayTablesDdl, _connection))
                    cmd.ExecuteNonQuery();

                // Retention sweep for the unbounded append-
                // only tables. `EventLog` (every external trigger + audit
                // event) and `SystemHistory` (every GlobalLogger.Log) otherwise
                // accumulate at ~thousands of rows per active streaming hour.
                // Reads AppConfig.LogRetentionDays from the loaded config (or
                // the default 30 when DB.Initialize runs before ConfigManager).
                // Set the field to 0 (or negative) to disable the sweep —
                // useful when forensic capture of full history is required.
                //
                // Deferred off the synchronous boot
                // path. Previously this DELETE ran inline inside Initialize,
                // pinning the splash for the full sweep duration. Now it's
                // dispatched to AsyncErrorBoundary right after the WAL
                // checkpoint timer is armed — splash returns immediately,
                // sweep runs against the same _connection serialised by
                // AcquireLockAsync. Logged at completion either way.
                int retentionDays = ConfigManager.Current?.LogRetentionDays ?? 30;
                if (retentionDays > 0)
                {
                    _ = Phoenix.Controls.Shared.Core.AsyncErrorBoundary.SafeRunAsync(
                        () => RunRetentionSweepAsync(retentionDays),
                        "System.DB", "RetentionSweep");
                }

                // EventLog row cap: startup pass + daily timer. The sweep
                // itself reads AppConfig.EventLogRetentionRows per run (and
                // no-ops at <= 0), so the timer is always armed — a config
                // change flips behavior on the next tick without re-Init.
                _ = Phoenix.Controls.Shared.Core.AsyncErrorBoundary.SafeRunAsync(
                    RunEventLogRowCapSweepAsync,
                    "System.DB", "EventLogRowCapSweep");
                _eventLogRowCapTimer?.Dispose();
                _eventLogRowCapTimer = new System.Threading.Timer(
                    _ => _ = Phoenix.Controls.Shared.Core.AsyncErrorBoundary.SafeRunAsync(
                        RunEventLogRowCapSweepAsync, "System.DB", "EventLogRowCapSweep"),
                    null,
                    EventLogRowCapInterval,
                    EventLogRowCapInterval);

                // Periodic WAL checkpoint. The TRUNCATE
                // above only runs once per process; without this timer the
                // .wal file grows unbounded between Hub restarts as writes
                // accumulate beyond SQLite's ~1000-page auto-checkpoint
                // threshold. RESTART blocks new readers briefly so the next
                // writer can recycle WAL frames from page 0 — cheaper than
                // TRUNCATE (which also blocks until the file is truncated on
                // disk), still bounds the .wal. Dispose tears it down.
                _walCheckpointTimer?.Dispose();
                _walCheckpointTimer = new System.Threading.Timer(
                    _ => _ = Phoenix.Controls.Shared.Core.AsyncErrorBoundary.SafeRunAsync(
                        WalCheckpointAsync, "System.DB", "PeriodicWalCheckpoint"),
                    null,
                    WalCheckpointInterval,
                    WalCheckpointInterval);
            }
        }

        /// <summary>
        /// On-demand retention sweep — invoked from Settings when the user changes
        /// the log-history cap so the change takes effect immediately instead of
        /// waiting for the next Hub start (the boot path in <see cref="Initialize"/>
        /// is the other caller). No-op for <paramref name="retentionDays"/> &lt;= 0.
        /// Deletes only the EventLog + SystemHistory log tables; Vars and User_*
        /// tables are never touched.
        /// </summary>
        public Task RunRetentionSweepNowAsync(int retentionDays)
            => RunRetentionSweepAsync(retentionDays);

        // Retention sweep body, run off the boot
        // path. Acquires the shared connection lock so it can't race a
        // script-driven write, and uses the dedicated idx_eventlog_ts /
        // idx_systemhistory_ts indexes created during Initialize so the
        // DELETEs are range scans rather than full-table sweeps. Failures
        // are logged at CriticalError tier and swallowed — a stalled sweep
        // is non-fatal (next boot retries).
        private async Task RunRetentionSweepAsync(int retentionDays)
        {
            if (_disposed) return;
            if (_connection is not { State: ConnectionState.Open }) return;
            if (retentionDays <= 0) return;

            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string cutoff = $"-{retentionDays} days";
                using var sweep = new SqliteCommand(
                    "DELETE FROM EventLog       WHERE Timestamp < datetime('now', @cutoff);" +
                    "DELETE FROM SystemHistory  WHERE Timestamp < datetime('now', @cutoff);",
                    _connection);
                sweep.Parameters.AddWithValue("@cutoff", cutoff);
                sweep.CommandTimeout = CommandTimeoutSeconds;
                int rows = await sweep.ExecuteNonQueryAsync().ConfigureAwait(false);
                sw.Stop();
                if (rows > 0)
                    GlobalLogger.Log(
                        $"Retention sweep deleted {rows} rows older than {retentionDays} days " +
                        $"from EventLog + SystemHistory (elapsed {sw.ElapsedMilliseconds}ms).",
                        "System.DB", LogLevel.System);
            }
            catch (Exception ex)
            {
                GlobalLogger.Log(
                    $"Retention sweep failed: {ex.Message}. Tables may grow unbounded until next restart.",
                    "System.DB", LogLevel.CriticalError);
            }
            finally
            {
                ReleaseLock(taken);
            }
        }

        // EventLog row-cap sweep body — keeps the newest
        // AppConfig.EventLogRetentionRows rows, deleting by rowid range so
        // the DELETE is a primary-key range scan (Id aliases the rowid), not
        // a table scan. Reads the config per run so Settings changes apply on
        // the next tick; <= 0 disables (keep forever). Failures are logged
        // and swallowed like the day-based sweep — the next run retries.
        //
        // Deletes are CHUNKED with the shared lock released between chunks:
        // the very first sweep on a long-lived databank can face millions of
        // backlog rows, and one monolithic DELETE would hold `_lock` (the
        // serializer for every live script-engine DB call) for its whole
        // duration. Each chunk is its own short autocommit transaction, so
        // live writes interleave between chunks and a mid-sweep failure keeps
        // all prior chunks (the next run finishes the remainder).
        private const int EventLogSweepChunkRows = 10_000;

        private async Task RunEventLogRowCapSweepAsync()
        {
            int cap = ConfigManager.Current?.EventLogRetentionRows ?? 10_000;
            if (cap <= 0) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long totalDeleted = 0;
            while (true)
            {
                if (_disposed) return;
                if (_connection is not { State: ConnectionState.Open }) return;

                int deletedThisChunk;
                bool taken = await AcquireLockAsync().ConfigureAwait(false);
                try
                {
                    if (_disposed) return;
                    // max(rowid) - cap keeps the newest cap rows exactly when
                    // the rowid sequence is contiguous, and at most cap when
                    // deletes have left gaps — either way the table stays
                    // bounded. Empty table: max(rowid) is NULL, the comparison
                    // is NULL, no rows match — a benign no-op. The inner
                    // SELECT re-evaluates per chunk so rows logged mid-sweep
                    // move the threshold instead of being over-deleted.
                    using var sweep = new SqliteCommand(
                        "DELETE FROM EventLog WHERE rowid IN (" +
                        "  SELECT rowid FROM EventLog" +
                        "  WHERE rowid < (SELECT max(rowid) FROM EventLog) - @cap" +
                        "  ORDER BY rowid LIMIT @chunk)",
                        _connection);
                    sweep.Parameters.AddWithValue("@cap", cap);
                    sweep.Parameters.AddWithValue("@chunk", EventLogSweepChunkRows);
                    sweep.CommandTimeout = CommandTimeoutSeconds;
                    deletedThisChunk = await sweep.ExecuteNonQueryAsync().ConfigureAwait(false);
                    totalDeleted += deletedThisChunk;
                }
                catch (Exception ex)
                {
                    GlobalLogger.Log(
                        $"EventLog row-cap sweep failed after {totalDeleted} rows: {ex.Message}. " +
                        "Next scheduled sweep finishes the remainder.",
                        "System.DB", LogLevel.CriticalError);
                    return;
                }
                finally
                {
                    ReleaseLock(taken);
                }

                if (deletedThisChunk < EventLogSweepChunkRows) break;
                // Off-lock breather between full chunks so queued script-engine
                // calls drain ahead of the next chunk.
                await Task.Delay(50).ConfigureAwait(false);
            }
            sw.Stop();
            if (totalDeleted > 0)
                GlobalLogger.Log(
                    $"EventLog row-cap sweep deleted {totalDeleted} rows beyond the newest {cap} " +
                    $"(elapsed {sw.ElapsedMilliseconds}ms).",
                    "System.DB", LogLevel.System);
        }

        // Periodic WAL checkpoint body. Routed through the
        // shared `_lock` so it can't race a concurrent script-driven write,
        // and through AsyncErrorBoundary so a transient checkpoint failure
        // (SQLITE_BUSY when a long reader holds back the truncate) is logged
        // and the timer continues. Skips the work when the DB is disposed or
        // its connection isn't open — the next tick will catch up.
        private async Task WalCheckpointAsync()
        {
            if (_disposed) return;
            if (_connection is not { State: ConnectionState.Open }) return;
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return;
                using var cmd = new SqliteCommand("PRAGMA wal_checkpoint(RESTART);", _connection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            finally
            {
                ReleaseLock(taken);
            }
        }

        // Structural-validity gate. Caller MUST hold _initLock and
        // _connection MUST be open. Returns true when SQLite reports "ok" for
        // the entire DB. Any non-"ok" row OR a SqliteException (corruption,
        // missing-page, cantopen) is treated as unhealthy so the caller falls
        // through to QuarantineCorruptDatabaseLocked. quick_check skips the
        // most expensive cross-row consistency walks integrity_check performs
        // — fast enough for every startup, catches the structural damage that
        // motivated this guard.
        private bool IsDatabaseHealthyLocked()
        {
            try
            {
                using var cmd = new SqliteCommand("PRAGMA quick_check;", _connection);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) return false;
                string result = reader.GetString(0);
                return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                // Microsoft.Data.Sqlite typically throws SqliteException for
                // corruption (error code 11/26) but a corrupt header can also
                // surface as a generic exception during prepare/step. Either
                // way, treat as "not healthy" so the caller falls into the
                // recovery path.
                return false;
            }
        }

        // Moves the corrupt .db / .db-wal / .db-shm trio out of the
        // way so a fresh Initialize sweep can recreate the databank in place.
        // Files land under <dir>/quarantine/<utc-stamp>/ alongside the original
        // location; both Hub status surfaces and any future support tooling can
        // pick them up there for offline recovery (sqlite3 .recover, etc.).
        // Caller MUST hold _initLock; this method closes _connection and
        // leaves it null so the caller can reopen against the now-empty path.
        private void QuarantineCorruptDatabaseLocked()
        {
            try { _connection?.Close(); } catch { /* best effort */ }
            try { _connection?.Dispose(); } catch { /* best effort */ }
            _connection = null;
            // Microsoft.Data.Sqlite pools by connection string — clear so the
            // OS handle is released before we try to move the file. Without
            // this, File.Move below fails with "in use by another process" on
            // Windows even though we've already closed and disposed.
            try { SqliteConnection.ClearAllPools(); } catch { /* best effort */ }

            string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
            string dbDir = Path.GetDirectoryName(_dbPath) ?? Phoenix.Controls.Shared.Core.Paths.RoamingAppDataRoot;
            string quarantineDir = Path.Combine(dbDir, "quarantine", ts);
            try
            {
                Directory.CreateDirectory(quarantineDir);
            }
            catch (Exception ex)
            {
                GlobalLogger.Log(
                    $"Quarantine directory create failed at '{quarantineDir}': {ex.Message}. " +
                    "Falling back to delete-only recovery; corrupt files will not be preserved.",
                    "System.DB", LogLevel.CriticalError);
                quarantineDir = string.Empty;
            }

            foreach (string ext in new[] { string.Empty, "-wal", "-shm" })
            {
                string src = _dbPath + ext;
                if (!File.Exists(src)) continue;
                bool moved = false;
                if (!string.IsNullOrEmpty(quarantineDir))
                {
                    string dst = Path.Combine(quarantineDir, Path.GetFileName(src));
                    try
                    {
                        File.Move(src, dst);
                        moved = true;
                        GlobalLogger.Log($"Quarantined corrupt '{src}' → '{dst}'.", "System.DB", LogLevel.System);
                    }
                    catch (IOException ex)
                    {
                        // "File in use" can happen if another process (or a
                        // pooled SQLite handle this AppDomain has yet to GC)
                        // still holds the file. We log + fall through to the
                        // delete attempt; if that also fails we surface a
                        // CriticalError and let the caller re-open.
                        GlobalLogger.Log(
                            $"Quarantine move failed for '{src}': {ex.Message}. Will attempt delete instead.",
                            "System.DB", LogLevel.System);
                    }
                }
                if (!moved)
                {
                    try
                    {
                        File.Delete(src);
                        GlobalLogger.Log($"Deleted corrupt '{src}' (quarantine unavailable).", "System.DB", LogLevel.System);
                    }
                    catch (Exception ex)
                    {
                        GlobalLogger.Log(
                            $"Could not remove corrupt '{src}': {ex.Message}. Subsequent open may fail again.",
                            "System.DB", LogLevel.CriticalError);
                    }
                }
            }
        }

        // ── Internal helpers ───────────────────────────────────────────────

        private const int CommandTimeoutSeconds = 5;

        private static readonly HashSet<string> _systemTables =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "SystemHistory", "Vars", "EventLog",
                // Viewer-roadmap Slice 0 — these are Hub-managed and must NEVER be
                // mutated via remote-bridge `/api/db/*` writes. The bridge guards at
                // the request layer too (only User_* tables accept writes), but
                // listing them here gives defense in depth at the persistence layer.
                "PairedDevices", "RemoteAuditLog",
                // Giveaway system — Hub-managed via the giveaway.* commands and
                // the Hub Giveaway page only; never mutate through generic db.*
                // script commands or the remote bridge. See DB.Giveaway.cs.
                "Giveaways", "GiveawayTickets", "GiveawayActivity",
            };

        private static bool IsValidIdentifier(string name) =>
            !string.IsNullOrWhiteSpace(name) &&
            Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$");

        private static bool IsSystemTable(string tableName) =>
            !string.IsNullOrWhiteSpace(tableName) && _systemTables.Contains(tableName);

        private async Task ExecuteAsync(string query, Action<SqliteCommand> parameterize)
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand(query, _connection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                parameterize(cmd);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            finally
            {
                ReleaseLock(taken);
            }
        }

        private async Task<T?> QueryScalarAsync<T>(string query, Action<SqliteCommand> parameterize)
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand(query, _connection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                parameterize(cmd);
                var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                if (result == null || result == DBNull.Value) return default;
                try
                {
                    var underlyingType = Nullable.GetUnderlyingType(typeof(T));
                    var targetType = underlyingType ?? typeof(T);
                    return (T)Convert.ChangeType(result, targetType);
                }
                catch (InvalidCastException ex)
                {
                    GlobalLogger.Log($"DB type conversion failed for '{typeof(T).Name}': {ex.Message}", "DB", LogLevel.CriticalError);
                    return default;
                }
            }
            catch (Exception ex)
            {
                // EnsureConnected() / ExecuteScalarAsync() can throw
                // (SqliteException, ObjectDisposedException, etc.). Callers like
                // InsertUserRowAsync read the return value and treat default(T)
                // as a benign empty result, so an uncaught throw here surfaced as
                // a silently-lost write. Log and degrade to default so the failure
                // is recorded rather than propagated as an unhandled exception.
                GlobalLogger.Log(string.Format("DB query failed: {0}", ex.Message), "DB", LogLevel.CriticalError);
                return default;
            }
            finally
            {
                ReleaseLock(taken);
            }
        }

        private void EnsureConnected()
        {
            // Fast path — connection already open. Reading the field once into
            // a local avoids a torn read if Initialize is racing us; the
            // re-check under _initLock below is the authority on mutation.
            var existing = _connection;
            if (existing != null && existing.State == System.Data.ConnectionState.Open)
                return;

            // Mutating _connection (assignment OR Open()) under
            // anything other than _initLock races Initialize's open path.
            // Always take _initLock when we're about to construct/open, so
            // the WAL drain + DDL + Open sequence either lands wholly before
            // or wholly after a concurrent EnsureConnected. This is on the
            // slow path (post-dispose-recover or very first call from a
            // non-Initialize entry point), so the lock cost is negligible.
            lock (_initLock)
            {
                // Re-check disposal under the lock so a Dispose
                // that finished after our fast-path read can't be resurrected
                // here. Throwing matches the rest of the public API.
                if (_disposed)
                    throw new ObjectDisposedException(nameof(DB));

                if (_connection == null)
                {
                    _connection = new SqliteConnection(_connectionString);
                    _connection.Open();
                }
                else if (_connection.State != System.Data.ConnectionState.Open)
                {
                    _connection.Open();
                }
            }
        }

        // Symmetric re-entry guard. Returns true when this
        // call actually took the semaphore (caller MUST pass true to
        // ReleaseLock); false when the current async context already held it
        // (no-op — outer scope owns the release). ALWAYS use the pair:
        //   bool taken = await AcquireLockAsync().ConfigureAwait(false);
        //   try { ... } finally { ReleaseLock(taken); }
        // This replaces every previous `_lock.WaitAsync()` / `_lock.Release()`
        // pair in this file. Never call _lock.WaitAsync / _lock.Release
        // directly — the AsyncLocal flag must be set/cleared symmetrically.
        private async Task<bool> AcquireLockAsync()
        {
            if (_heldByThisAsyncCtx.Value) return false;
            await _lock.WaitAsync().ConfigureAwait(false);
            _heldByThisAsyncCtx.Value = true;
            return true;
        }

        private void ReleaseLock(bool taken)
        {
            if (!taken) return;
            _heldByThisAsyncCtx.Value = false;
            _lock.Release();
        }

        // ── Public API ─────────────────────────────────────────────────────

        // WRITING: HUB will primarily use this
        public async Task LogAsync(Log entry)
        {
            await ExecuteAsync(
                "INSERT INTO SystemHistory (Level, Source, Message, RawData) VALUES (@lvl, @src, @msg, @raw)",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@lvl", entry.Level.ToString());
                    cmd.Parameters.AddWithValue("@src", entry.Source);
                    cmd.Parameters.AddWithValue("@msg", entry.Message);
                    cmd.Parameters.AddWithValue("@raw", (object?)entry.RawData ?? DBNull.Value);
                }).ConfigureAwait(false);
        }

        // Dedicated SystemHistory writer connection, owned by GlobalLogger.
        //
        // The shared singleton DB.Instance serializes every operation behind a
        // single SemaphoreSlim (_lock above) — script reads, databank tab
        // queries, ScriptEngine variable preloads, and log writes all queue
        // behind one another. That fold-in is fine for most callers but pins
        // GlobalLogger.WriteEntryAsync to the same gate, so a long-running
        // script-driven SELECT blocks log persistence and pads the bounded log
        // channel until DropOldest kicks in. SQLite in WAL mode supports
        // concurrent readers + one writer per file, so a second connection
        // bypassing the in-process semaphore lets log inserts run alongside
        // shared-connection reads with no extra C# contention.
        //
        // The dedicated connection lazily opens on first use against the same
        // file as DB.Instance (so it sees the SystemHistory table created in
        // Initialize). _logDbLock keeps a single in-flight INSERT at a time —
        // strictly defensive: GlobalLogger pumps its bounded channel with a
        // single reader, so concurrent callers shouldn't appear in normal
        // operation, but the lock keeps the behaviour correct if a future
        // pump fans out or a test seam writes directly.
        private static SqliteConnection? _logDbConnection;
        private static readonly SemaphoreSlim _logDbLock = new SemaphoreSlim(1, 1);
        private static readonly object _logDbInitLock = new();

        /// <summary>
        /// Inserts <paramref name="entry"/> into SystemHistory via a connection
        /// dedicated to log writes. Bypasses <see cref="DB.Instance"/>'s shared
        /// gate so log persistence cannot stall behind script-driven reads.
        /// Throws on connection / SQL failure so callers (GlobalLogger) can
        /// surface the fault through their own error channels.
        /// </summary>
        public static async Task WriteLogDedicatedAsync(Log entry)
        {
            await _logDbLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // EnsureLogDbConnection acquires nested blocking locks
                // (_logDbInitLock + the singleton's _initLock). Running that
                // synchronously here would block this async method's thread —
                // and can stall behind Initialize() if it holds _initLock
                // mid-flight. Offload the blocking acquisition to the thread
                // pool so the await yields instead of pinning the caller.
                // (The slow path only fires on the first log write / after a
                // quarantine; the steady state returns on the fast path.)
                await Task.Run(EnsureLogDbConnection).ConfigureAwait(false);
                using var cmd = new SqliteCommand(
                    "INSERT INTO SystemHistory (Level, Source, Message, RawData) VALUES (@lvl, @src, @msg, @raw)",
                    _logDbConnection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                cmd.Parameters.AddWithValue("@lvl", entry.Level.ToString());
                cmd.Parameters.AddWithValue("@src", entry.Source);
                cmd.Parameters.AddWithValue("@msg", entry.Message);
                cmd.Parameters.AddWithValue("@raw", (object?)entry.RawData ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            finally
            {
                try { _logDbLock.Release(); } catch (ObjectDisposedException) { }
            }
        }

        /// <summary>
        /// Batch variant of <see cref="WriteLogDedicatedAsync"/> — inserts all
        /// of <paramref name="entries"/> under ONE transaction with a single
        /// reused parameterized command, so a drained log burst pays one commit
        /// (one WAL sync boundary) instead of one per entry. A failure mid-batch
        /// rolls the whole batch back (transaction dispose) and throws, so the
        /// caller (GlobalLogger's writer pump) can surface the fault; losing a
        /// full in-flight batch rather than a partial prefix is acceptable for
        /// the log sink. Same dedicated connection + lock as the per-entry path.
        /// </summary>
        public static async Task WriteLogBatchDedicatedAsync(IReadOnlyList<Log> entries)
        {
            if (entries == null || entries.Count == 0) return;
            await _logDbLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Steady state the connection is already open; only the first
                // write (or a post-quarantine reopen) needs the blocking slow
                // path, which is offloaded so its nested locks can't pin the
                // caller's thread (see WriteLogDedicatedAsync).
                if (_logDbConnection is not { State: System.Data.ConnectionState.Open })
                    await Task.Run(EnsureLogDbConnection).ConfigureAwait(false);

                using var tx = _logDbConnection!.BeginTransaction();
                using var cmd = new SqliteCommand(
                    "INSERT INTO SystemHistory (Level, Source, Message, RawData) VALUES (@lvl, @src, @msg, @raw)",
                    _logDbConnection, tx);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                var pLvl = cmd.Parameters.Add("@lvl", SqliteType.Text);
                var pSrc = cmd.Parameters.Add("@src", SqliteType.Text);
                var pMsg = cmd.Parameters.Add("@msg", SqliteType.Text);
                var pRaw = cmd.Parameters.Add("@raw", SqliteType.Text);
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    pLvl.Value = entry.Level.ToString();
                    pSrc.Value = entry.Source;
                    pMsg.Value = entry.Message;
                    pRaw.Value = (object?)entry.RawData ?? DBNull.Value;
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                tx.Commit();
            }
            finally
            {
                try { _logDbLock.Release(); } catch (ObjectDisposedException) { }
            }
        }

        private static void EnsureLogDbConnection()
        {
            // Fast path — already open. State check tolerates a future "auto-
            // close on idle" if we ever swap to pooling.
            if (_logDbConnection is { State: System.Data.ConnectionState.Open }) return;

            lock (_logDbInitLock)
            {
                if (_logDbConnection is { State: System.Data.ConnectionState.Open }) return;

                // Coordinate with the singleton's _initLock so this
                // dedicated log connection cannot open against a file that
                // Initialize is mid-flight on. The original code raced the
                // structural-integrity quarantine path: a log INSERT firing
                // after _connection was nulled but before QuarantineCorrupt-
                // DatabaseLocked moved the .db aside would land an INSERT
                // into a file SQLite was about to rename, occasionally
                // re-corrupting the fresh DB the Initialize sweep just
                // created. Block here until Initialize releases its lock —
                // the wait is bounded to startup and never re-fires after.
                //
                // We touch DB.Instance to construct the singleton on demand
                // (so a log-before-Initialize call still finds an _initLock
                // to wait on), then take that instance's _initLock.
                var inst = Instance;
                lock (inst._initLock)
                {
                    // Disposal can race a late log-flush during shutdown —
                    // bail without opening a fresh handle so we don't leak
                    // a connection that nothing will close. Caller sees the
                    // ObjectDisposedException through the public API.
                    if (inst._disposed)
                        throw new ObjectDisposedException(nameof(DB));

                    // Re-check inside the singleton's lock — another thread
                    // may have raced us through the outer fast path and
                    // already opened the dedicated connection.
                    if (_logDbConnection is { State: System.Data.ConnectionState.Open }) return;

                    // Read _dbPath while we hold _initLock so we cannot
                    // observe a half-completed Initialize(customPath) swap.
                    string path = inst._dbPath;
                    string cs = $"Data Source={path};";

                    try { _logDbConnection?.Dispose(); } catch { /* best effort */ }
                    _logDbConnection = new SqliteConnection(cs);
                    _logDbConnection.Open();

                    // synchronous is per-connection, so the NORMAL pairing
                    // set in Initialize doesn't carry over — without this the
                    // log writer's commits stay at the compiled FULL default
                    // and every log INSERT pays an fsync.
                    using var sync = new SqliteCommand("PRAGMA synchronous=NORMAL;", _logDbConnection);
                    sync.CommandTimeout = CommandTimeoutSeconds;
                    sync.ExecuteNonQuery();
                }
            }
        }

        public async Task SetVariableAsync(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                GlobalLogger.Log(
                    "DB.SetVariable rejected: blank/whitespace key. " +
                    "No fallback applied; write skipped to avoid clobbering an unrelated var.",
                    "DB", LogLevel.CriticalError);
                return;
            }

            await ExecuteAsync(
                "INSERT INTO Vars (VarKey, VarValue) VALUES (@key, @val) ON CONFLICT(VarKey) DO UPDATE SET VarValue=@val, LastModified=CURRENT_TIMESTAMP",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@key", key);
                    cmd.Parameters.AddWithValue("@val", value);
                }).ConfigureAwait(false);
        }

        // Vars-table editability bridge. The Architect Databank Browser
        // edits cells by (tableName, rowId, columnName, value) but Vars is a
        // protected system table on SetCellAsync. Routing through
        // SetVariableAsync requires the VarKey (PK), so the browser needs a
        // way to translate a rowid back to its VarKey. Read-only lookup —
        // no system-table guard needed; the write that follows still goes
        // through SetVariableAsync's own validation.
        public async Task<string?> GetVarKeyByRowIdAsync(long rowId)
        {
            return await QueryScalarAsync<string>(
                "SELECT VarKey FROM Vars WHERE rowid = @rid",
                cmd => cmd.Parameters.AddWithValue("@rid", rowId)).ConfigureAwait(false);
        }

        // READING: All apps can use this
        public async Task<string> GetVariableAsync(string key, string defaultValue = "")
        {
            string? result = await QueryScalarAsync<string>(
                "SELECT VarValue FROM Vars WHERE VarKey = @key",
                cmd => cmd.Parameters.AddWithValue("@key", key)).ConfigureAwait(false);
            return result ?? defaultValue;
        }

        /// <summary>
        /// Batch read of N variables in a single SQLite round-trip. Folds
        /// N+1 GetVariableAsync acquisitions of <see cref="_lock"/> into one
        /// — the dominant chat-command preload latency hotspot — without
        /// changing observable semantics: missing keys are absent from the
        /// returned dictionary (NOT null/empty entries), matching the
        /// caller's "skip if not stored" pattern in
        /// <see cref="ScriptEngine.ExecuteScriptAsync"/>.
        /// </summary>
        public async Task<IDictionary<string, string>> GetVariablesAsync(IEnumerable<string> keys)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (keys == null) return result;

            // Distinct case-insensitively to match the engine's preload
            // de-dup, and to keep the parameter list short. Lists keep
            // insertion order which doesn't matter for a WHERE-IN read.
            var distinctKeys = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in keys)
            {
                if (string.IsNullOrEmpty(k)) continue;
                if (seen.Add(k)) distinctKeys.Add(k);
            }
            if (distinctKeys.Count == 0) return result;

            // Build the parameter list dynamically. SQLite has no native
            // array binding; the canonical pattern is `WHERE x IN (@k0,
            // @k1, ...)`. Concatenating bare keys would expose us to SQL
            // injection — the names come from script content the user
            // authors, so they must be parameterised even though they
            // look like trusted strings.
            var sb = new StringBuilder("SELECT VarKey, VarValue FROM Vars WHERE VarKey IN (");
            for (int i = 0; i < distinctKeys.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("@k").Append(i);
            }
            sb.Append(')');
            string sql = sb.ToString();

            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand(sql, _connection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                for (int i = 0; i < distinctKeys.Count; i++)
                    cmd.Parameters.AddWithValue("@k" + i, distinctKeys[i]);

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    string k = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    string v = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    if (!string.IsNullOrEmpty(k))
                        result[k] = v;
                }
            }
            finally
            {
                ReleaseLock(taken);
            }
            return result;
        }

        public async Task<List<KeyValuePair<string, string>>> GetAllVariablesAsync()
        {
            var vars = new List<KeyValuePair<string, string>>();

            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand(
                    "SELECT VarKey, VarValue FROM Vars ORDER BY VarKey ASC", _connection);
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    string k = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    string v = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    vars.Add(new KeyValuePair<string, string>(k, v));
                }
            }
            finally
            {
                ReleaseLock(taken);
            }

            return vars;
        }

        public async Task LogEventAsync(string source, string type, string user, string payload)
        {
            await ExecuteAsync(
                "INSERT INTO EventLog (EventSource, EventType, User, Payload) VALUES (@src, @type, @user, @payload)",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@src",     source);
                    cmd.Parameters.AddWithValue("@type",    type);
                    cmd.Parameters.AddWithValue("@user",    user);
                    cmd.Parameters.AddWithValue("@payload", payload);
                }).ConfigureAwait(false);
        }

        /// <summary>
        /// Batch variant of <see cref="LogEventAsync"/> — identical rows,
        /// columns and values, but all of <paramref name="events"/> land under
        /// ONE transaction with a single reused parameterized command. Backs
        /// the Hub's coalesced Streamer.bot audit-log writer so event bursts
        /// (hype trains, polls, gift bombs) pay one commit instead of one per
        /// event on the shared connection.
        /// </summary>
        public async Task LogEventsBatchAsync(
            IReadOnlyList<(string Source, string Type, string User, string Payload)> events)
        {
            if (events == null || events.Count == 0) return;
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var tx = _connection!.BeginTransaction();
                using var cmd = new SqliteCommand(
                    "INSERT INTO EventLog (EventSource, EventType, User, Payload) VALUES (@src, @type, @user, @payload)",
                    _connection, tx);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                var pSrc     = cmd.Parameters.Add("@src",     SqliteType.Text);
                var pType    = cmd.Parameters.Add("@type",    SqliteType.Text);
                var pUser    = cmd.Parameters.Add("@user",    SqliteType.Text);
                var pPayload = cmd.Parameters.Add("@payload", SqliteType.Text);
                for (int i = 0; i < events.Count; i++)
                {
                    var (source, type, user, payload) = events[i];
                    pSrc.Value     = source;
                    pType.Value    = type;
                    pUser.Value    = user;
                    pPayload.Value = payload;
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                tx.Commit();
            }
            finally
            {
                ReleaseLock(taken);
            }
        }

        /// <summary>
        /// State-machine delete. <see cref="DeleteVariableAsync"/> blocks `state.*` keys
        /// because the engine writes its own state-machine bookkeeping there; this method
        /// is the script-facing escape hatch that intentionally targets the same prefix.
        /// `name` is the bare state name (no `state.` prefix); empty / whitespace is rejected.
        /// </summary>
        public async Task DeleteStateAsync(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                GlobalLogger.Log(
                    "DB.DeleteState rejected: blank/whitespace state name.",
                    "DB", LogLevel.CriticalError);
                return;
            }
            await ExecuteAsync(
                "DELETE FROM Vars WHERE VarKey = @key",
                cmd => cmd.Parameters.AddWithValue("@key", $"state.{name}"))
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Returns the bare state names (no `state.` prefix) currently set in the DB,
        /// in insertion order. Backs the script-side state.list_keys helper.
        /// </summary>
        public async Task<List<string>> ListStateKeysAsync()
        {
            var result = new List<string>();
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand(
                    "SELECT VarKey FROM Vars WHERE VarKey LIKE 'state.%' ORDER BY VarKey",
                    _connection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    string raw = reader.GetString(0);
                    // Strip the `state.` prefix; defensive against the unlikely "state."
                    // value (length-6 key).
                    result.Add(raw.Length > 6 ? raw.Substring(6) : "");
                }
            }
            finally
            {
                ReleaseLock(taken);
            }
            return result;
        }

        public async Task DeleteVariableAsync(string key)
        {
            // Gate empty/whitespace + reserved-prefix keys at the persistence
            // layer so script-driven `db.delete_var(global._event_queue)` (or any other
            // engine-internal var) is rejected even if a future caller forgets to pre-
            // validate. Mirrors the SetVariableAsync rejection shape.
            if (string.IsNullOrWhiteSpace(key))
            {
                GlobalLogger.Log(
                    "DB.DeleteVariable rejected: blank/whitespace key. " +
                    "No fallback applied; delete skipped to avoid clobbering an unrelated var.",
                    "DB", LogLevel.CriticalError);
                return;
            }
            if (IsReservedVarKey(key))
            {
                GlobalLogger.Log(
                    $"DB.DeleteVariable BLOCKED: '{key}' uses a reserved-prefix " +
                    "(global._*, state.*, leading underscore). Engine-internal state " +
                    "is not script-deletable.",
                    "DB", LogLevel.CriticalError);
                return;
            }

            await ExecuteAsync(
                "DELETE FROM Vars WHERE VarKey = @key",
                cmd => cmd.Parameters.AddWithValue("@key", key))
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Engine-maintenance batch delete for reserved-prefix vars — the
        /// counterpart to <see cref="DeleteVariableAsync"/>'s reserved-key
        /// guard. That guard exists to stop SCRIPT-driven deletes
        /// (db.delete_var) from trampling engine state; this method is the
        /// engine's own housekeeping path (e.g. re-arming a re-saved script's
        /// persisted DoOnce / DoN / FlipFlop vars) and is deliberately NOT
        /// reachable from any registered script command. Blank keys are
        /// skipped; keys are deleted in chunks so one lock acquisition covers
        /// each batch.
        /// </summary>
        public async Task DeleteEngineStateVariablesAsync(IReadOnlyCollection<string> keys)
        {
            if (keys == null || keys.Count == 0) return;

            const int ChunkSize = 100;
            var chunk = new List<string>(ChunkSize);
            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key)) continue;
                chunk.Add(key);
                if (chunk.Count == ChunkSize)
                {
                    await DeleteChunkAsync(chunk).ConfigureAwait(false);
                    chunk.Clear();
                }
            }
            if (chunk.Count > 0)
                await DeleteChunkAsync(chunk).ConfigureAwait(false);

            async Task DeleteChunkAsync(List<string> batch)
            {
                var placeholders = new string[batch.Count];
                for (int i = 0; i < batch.Count; i++) placeholders[i] = "@k" + i;
                string sql = "DELETE FROM Vars WHERE VarKey IN (" + string.Join(",", placeholders) + ")";
                await ExecuteAsync(sql, cmd =>
                {
                    for (int i = 0; i < batch.Count; i++)
                        cmd.Parameters.AddWithValue("@k" + i, batch[i]);
                }).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Keys reserved for engine bookkeeping (per-execution counters,
        /// state-change flipflops, internal queues). Scripts can READ these via
        /// {global._foo} but must not delete or trample them.
        /// </summary>
        private static bool IsReservedVarKey(string key) =>
            key.StartsWith("global._", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("state.",   StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("_",        StringComparison.Ordinal);

        public async Task<List<string>> GetUserTablesAsync()
        {
            var tables = new List<string>();
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand(
                    "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name ASC", _connection);
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    string name = reader.GetString(0);
                    if (!_systemTables.Contains(name))
                        tables.Add(name);
                }
            }
            finally { ReleaseLock(taken); }
            return tables;
        }

        /// <summary>
        /// All tables in the databank — both system (<c>Vars</c> / <c>EventLog</c> /
        /// <c>SystemHistory</c> / paired-device tables) and user. The Architect
        /// Databank Browser needs the union so it can render the System / User
        /// grouping; everything else keeps using <see cref="GetUserTablesAsync"/>.
        /// SQLite-internal tables (anything starting with <c>sqlite_</c>) are
        /// still filtered out — they aren't useful surface for the user.
        /// </summary>
        public async Task<List<string>> GetAllTableNamesAsync()
        {
            var tables = new List<string>();
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand(
                    "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name ASC", _connection);
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    string name = reader.GetString(0);
                    if (name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)) continue;
                    tables.Add(name);
                }
            }
            finally { ReleaseLock(taken); }
            return tables;
        }

        /// <summary>
        /// True when <paramref name="tableName"/> is on the protected system
        /// list (<c>Vars</c> / <c>EventLog</c> / <c>SystemHistory</c> + the
        /// Viewer remote-bridge tables). Public so the Architect Databank
        /// toolbar can flip destructive buttons off without re-encoding the
        /// list — the same identifier list every internal write guard checks.
        /// </summary>
        public static bool IsSystemTableName(string tableName) => IsSystemTable(tableName);

        public async Task CreateUserTableAsync(string tableName, List<(string ColName, string ColType)> columns)
        {
            if (!IsValidIdentifier(tableName))
            {
                GlobalLogger.Log(
                    $"DB.CreateUserTable rejected: invalid table identifier '{tableName}'.",
                    "DB", LogLevel.CriticalError);
                throw new ArgumentException($"Invalid table name: {tableName}");
            }

            // Symmetric guard with DeleteRow/ClearTable/UpdateCell/SetCell: a
            // script that interpolates a user-controlled var into the table-name
            // position must not be able to author a CREATE that would later be
            // picked up by Architect Databank as a 'user' table.
            if (IsSystemTable(tableName))
            {
                GlobalLogger.Log(
                    $"DB.CreateUserTable BLOCKED: '{tableName}' is a protected system table. " +
                    "Script-driven schema operations on system tables are denied.",
                    "DB", LogLevel.CriticalError);
                throw new InvalidOperationException(
                    $"Create on system table '{tableName}' denied.");
            }

            var allowedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "TEXT", "INTEGER", "REAL", "BOOLEAN" };
            var colDefs = new StringBuilder();
            foreach (var (name, type) in columns)
            {
                if (!IsValidIdentifier(name))
                {
                    GlobalLogger.Log(
                        $"DB.CreateUserTable rejected: invalid column identifier '{name}' for table '{tableName}'.",
                        "DB", LogLevel.CriticalError);
                    throw new ArgumentException($"Invalid column name: {name}");
                }
                string safeType = allowedTypes.Contains(type) ? type.ToUpper() : "TEXT";
                colDefs.Append($", [{name}] {safeType}");
            }

            string ddl = $"CREATE TABLE IF NOT EXISTS [{tableName}] (rowid INTEGER PRIMARY KEY AUTOINCREMENT{colDefs});";

            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand(ddl, _connection);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            finally { ReleaseLock(taken); }
        }

        // Dedicated bulk-read connection, used only by GetTableDataAsync.
        //
        // That method streams an ENTIRE table (no LIMIT — RemoteBridgeServer
        // expects full tables) into a DataTable; doing it on the shared
        // connection held `_lock` for the whole load, so the live script
        // engine's DB access queued behind a remote-device table fetch. WAL
        // supports concurrent readers alongside the single writer, so a
        // second connection — same lazy-open + _initLock coordination as the
        // dedicated log connection above — lets the bulk read run without
        // seizing the write serializer. _bulkReadLock keeps one bulk read in
        // flight at a time (defensive, mirrors _logDbLock); it never contends
        // with `_lock`.
        private static SqliteConnection? _bulkReadConnection;
        private static readonly SemaphoreSlim _bulkReadLock = new SemaphoreSlim(1, 1);
        private static readonly object _bulkReadInitLock = new();

        private static void EnsureBulkReadConnection()
        {
            if (_bulkReadConnection is { State: System.Data.ConnectionState.Open }) return;

            lock (_bulkReadInitLock)
            {
                if (_bulkReadConnection is { State: System.Data.ConnectionState.Open }) return;

                // Same Initialize-coordination story as EnsureLogDbConnection:
                // take the singleton's _initLock so this connection can't open
                // against a file mid-quarantine, and read _dbPath under it so a
                // half-completed Initialize(customPath) swap is unobservable.
                var inst = Instance;
                lock (inst._initLock)
                {
                    if (inst._disposed)
                        throw new ObjectDisposedException(nameof(DB));

                    if (_bulkReadConnection is { State: System.Data.ConnectionState.Open }) return;

                    string path = inst._dbPath;
                    string cs = $"Data Source={path};";

                    try { _bulkReadConnection?.Dispose(); } catch { /* best effort */ }
                    _bulkReadConnection = new SqliteConnection(cs);
                    _bulkReadConnection.Open();
                }
            }
        }

        public async Task<DataTable> GetTableDataAsync(string tableName)
        {
            if (!IsValidIdentifier(tableName))
            {
                GlobalLogger.Log(
                    $"DB.GetTableData rejected: invalid table identifier '{tableName}'.",
                    "DB", LogLevel.CriticalError);
                throw new ArgumentException($"Invalid table name: {tableName}");
            }

            var dt = new DataTable();
            await _bulkReadLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Slow path (first call / post-dispose) takes nested blocking
                // locks — hop to the pool so they can't pin this async caller,
                // mirroring WriteLogDedicatedAsync's rationale.
                if (_bulkReadConnection is not { State: ConnectionState.Open })
                    await Task.Run(EnsureBulkReadConnection).ConfigureAwait(false);

                using var checkCmd = new SqliteCommand(
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name", _bulkReadConnection);
                checkCmd.Parameters.AddWithValue("@name", tableName);
                long exists = Convert.ToInt64(await checkCmd.ExecuteScalarAsync().ConfigureAwait(false)!);
                if (exists == 0)
                    throw new InvalidOperationException($"Table '{tableName}' does not exist.");

                using var cmd = new SqliteCommand($"SELECT * FROM [{tableName}]", _bulkReadConnection);
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                dt.Load(reader);
            }
            finally
            {
                try { _bulkReadLock.Release(); } catch (ObjectDisposedException) { }
            }
            return dt;
        }

        /// <summary>
        /// Fetch up to <paramref name="maxRows"/> rows of <paramref name="tableName"/>
        /// alongside each row's SQLite <c>rowid</c>. Caller passes the user-visible
        /// columns (in declaration order, rowid filtered out via
        /// <see cref="GetTableColumnsAsync"/>); the projection is `SELECT rowid,
        /// [col1], [col2], ...` so the rowid is always present even for externally
        /// authored tables that don't declare an `INTEGER PRIMARY KEY` alias.
        /// Used by the Architect Databank Browser so cell-edit / delete-row
        /// operations have a stable identifier.
        /// </summary>
        public async Task<List<(long RowId, string?[] Cells)>> GetRowsWithRowIdAsync(
            string tableName,
            IReadOnlyList<string> columnsInDeclarationOrder,
            int maxRows,
            int offset = 0,
            string? orderByColumn = null,
            bool orderDescending = false)
        {
            if (!IsValidIdentifier(tableName))
            {
                GlobalLogger.Log(
                    $"DB.GetRowsWithRowId rejected: invalid table identifier '{tableName}'.",
                    "DB", LogLevel.CriticalError);
                throw new ArgumentException($"Invalid table name: {tableName}");
            }
            for (int i = 0; i < columnsInDeclarationOrder.Count; i++)
            {
                if (!IsValidIdentifier(columnsInDeclarationOrder[i]))
                {
                    GlobalLogger.Log(
                        $"DB.GetRowsWithRowId rejected: invalid column identifier '{columnsInDeclarationOrder[i]}' for table '{tableName}'.",
                        "DB", LogLevel.CriticalError);
                    throw new ArgumentException($"Invalid column name: {columnsInDeclarationOrder[i]}");
                }
            }
            // Order column must be one of the declared columns or the implicit
            // "rowid"; never raw user input concatenated into SQL. A malformed
            // header click in the UI falls back to rowid-asc rather than
            // becoming an injection surface.
            string orderClause = "rowid";
            if (!string.IsNullOrWhiteSpace(orderByColumn))
            {
                if (orderByColumn == "rowid")
                {
                    orderClause = "rowid";
                }
                else if (IsValidIdentifier(orderByColumn) &&
                         ContainsName(columnsInDeclarationOrder, orderByColumn))
                {
                    orderClause = $"[{orderByColumn}]";
                }
            }
            string direction = orderDescending ? "DESC" : "ASC";

            var rows = new List<(long, string?[])>();
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                string projection = columnsInDeclarationOrder.Count == 0
                    ? "rowid"
                    : "rowid, " + string.Join(", ", columnsInDeclarationOrder.Select(c => $"[{c}]"));
                int limit = maxRows > 0 ? maxRows : 500;
                int off   = offset > 0 ? offset : 0;
                using var cmd = new SqliteCommand(
                    $"SELECT {projection} FROM [{tableName}] ORDER BY {orderClause} {direction} LIMIT @lim OFFSET @off",
                    _connection);
                cmd.Parameters.AddWithValue("@lim", limit);
                cmd.Parameters.AddWithValue("@off", off);
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    long rowId = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                    var cells = new string?[columnsInDeclarationOrder.Count];
                    for (int i = 0; i < cells.Length; i++)
                    {
                        int idx = i + 1;
                        cells[i] = reader.IsDBNull(idx) ? null : reader.GetValue(idx)?.ToString();
                    }
                    rows.Add((rowId, cells));
                }
            }
            finally { ReleaseLock(taken); }
            return rows;
        }

        private static bool ContainsName(IReadOnlyList<string> names, string candidate)
        {
            for (int i = 0; i < names.Count; i++)
                if (string.Equals(names[i], candidate, StringComparison.Ordinal)) return true;
            return false;
        }

        public async Task<long> InsertUserRowAsync(string tableName, Dictionary<string, string> values)
        {
            if (!IsValidIdentifier(tableName))
            {
                GlobalLogger.Log(
                    $"DB.InsertUserRow rejected: invalid table identifier '{tableName}'.",
                    "DB", LogLevel.CriticalError);
                throw new ArgumentException($"Invalid table name: {tableName}");
            }

            // Symmetric guard with the other destructive paths (DeleteRowAsync,
            // ClearTableAsync, UpdateCellAsync, SetCellAsync). InsertUserRowAsync
            // used to skip this check, so a script that substituted a user-
            // controlled var into the table-name position could write rows into
            // Vars/EventLog/SystemHistory/PairedDevices/RemoteAuditLog. Vars in
            // particular is structurally `name`/`value` and would have accepted
            // the insert.
            if (IsSystemTable(tableName))
            {
                GlobalLogger.Log(
                    $"DB.InsertUserRow BLOCKED: '{tableName}' is a protected system table. " +
                    "Script-driven inserts into system tables are denied.",
                    "DB", LogLevel.CriticalError);
                throw new InvalidOperationException(
                    $"Insert into system table '{tableName}' denied.");
            }

            // Iterate keys and values together as KeyValuePairs and skip BOTH
            // when a key fails IsValidIdentifier. The previous implementation incremented
            // the column counter only on valid keys but the parameter counter j on every
            // value, so values for rejected keys silently shifted onto the next valid
            // column's placeholder. Microsoft.Data.Sqlite tolerates extra parameters
            // without error, so the corruption was invisible at the SQL layer.
            var cols = new List<string>();
            var paramNames = new List<string>();
            var paramValues = new List<string>();
            int i = 0;
            foreach (var kv in values)
            {
                if (!IsValidIdentifier(kv.Key))
                {
                    GlobalLogger.Log(
                        $"DB.InsertUserRow: rejected invalid column key '{kv.Key}' for table '{tableName}'; both key and value skipped.",
                        "DB", LogLevel.CriticalError);
                    continue;
                }
                cols.Add($"[{kv.Key}]");
                paramNames.Add($"@p{i}");
                paramValues.Add(kv.Value);
                i++;
            }

            if (cols.Count == 0)
            {
                GlobalLogger.Log(
                    $"DB.InsertUserRow rejected: no valid columns for table '{tableName}'.",
                    "DB", LogLevel.CriticalError);
                return 0;
            }

            string sql = $"INSERT INTO [{tableName}] ({string.Join(", ", cols)}) VALUES ({string.Join(", ", paramNames)}); SELECT last_insert_rowid();";

            return await QueryScalarAsync<long>(sql, cmd =>
            {
                for (int j = 0; j < paramValues.Count; j++)
                    cmd.Parameters.AddWithValue($"@p{j}", (object?)paramValues[j] ?? DBNull.Value);
            }).ConfigureAwait(false);
        }

        public async Task DeleteRowAsync(string tableName, long rowid)
        {
            if (!IsValidIdentifier(tableName))
            {
                GlobalLogger.Log(
                    $"DB.DeleteRow rejected: invalid table identifier '{tableName}'.",
                    "DB", LogLevel.CriticalError);
                throw new ArgumentException($"Invalid table name: {tableName}");
            }

            if (IsSystemTable(tableName))
            {
                GlobalLogger.Log(
                    $"DB.DeleteRow BLOCKED: '{tableName}' is a protected system table. " +
                    "Script-driven destructive operations on system tables are denied.",
                    "DB", LogLevel.CriticalError);
                return;
            }

            await ExecuteAsync(
                $"DELETE FROM [{tableName}] WHERE rowid = @rid",
                cmd => cmd.Parameters.AddWithValue("@rid", rowid))
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Batch variant of <see cref="DeleteRowAsync"/> — identical guards and
        /// row selection, but all of <paramref name="rowIds"/> are deleted via
        /// chunked parameterized IN-lists under ONE transaction, so a
        /// multi-select delete in the Databank Browser pays one commit instead
        /// of one per row on the shared connection. Atomic: any failure rolls
        /// the whole batch back and rethrows.
        /// </summary>
        public async Task DeleteRowsAsync(string tableName, IReadOnlyList<long> rowIds)
        {
            if (rowIds == null || rowIds.Count == 0) return;

            if (!IsValidIdentifier(tableName))
            {
                GlobalLogger.Log(
                    $"DB.DeleteRows rejected: invalid table identifier '{tableName}'.",
                    "DB", LogLevel.CriticalError);
                throw new ArgumentException($"Invalid table name: {tableName}");
            }

            if (IsSystemTable(tableName))
            {
                GlobalLogger.Log(
                    $"DB.DeleteRows BLOCKED: '{tableName}' is a protected system table. " +
                    "Script-driven destructive operations on system tables are denied.",
                    "DB", LogLevel.CriticalError);
                return;
            }

            // SQLite's default host-parameter ceiling is 999; 500 per statement
            // keeps a comfortable margin while still collapsing a large batch
            // into a handful of statements.
            const int ChunkSize = 500;

            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var tx = _connection!.BeginTransaction();
                try
                {
                    for (int offset = 0; offset < rowIds.Count; offset += ChunkSize)
                    {
                        int count = Math.Min(ChunkSize, rowIds.Count - offset);
                        var sql = new StringBuilder("DELETE FROM [")
                            .Append(tableName).Append("] WHERE rowid IN (");
                        for (int i = 0; i < count; i++)
                        {
                            if (i > 0) sql.Append(", ");
                            sql.Append("@r").Append(i);
                        }
                        sql.Append(')');
                        using var cmd = new SqliteCommand(sql.ToString(), _connection, tx);
                        cmd.CommandTimeout = CommandTimeoutSeconds;
                        for (int i = 0; i < count; i++)
                            cmd.Parameters.AddWithValue($"@r{i}", rowIds[offset + i]);
                        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                    tx.Commit();
                }
                catch
                {
                    try { tx.Rollback(); } catch { /* best effort */ }
                    throw;
                }
            }
            finally
            {
                ReleaseLock(taken);
            }
        }

        public async Task ClearTableAsync(string tableName)
        {
            if (!IsValidIdentifier(tableName))
            {
                GlobalLogger.Log(
                    $"DB.ClearTable rejected: invalid table identifier '{tableName}'.",
                    "DB", LogLevel.CriticalError);
                throw new ArgumentException($"Invalid table name: {tableName}");
            }

            if (IsSystemTable(tableName))
            {
                GlobalLogger.Log(
                    $"DB.ClearTable BLOCKED: '{tableName}' is a protected system table. " +
                    "Script-driven destructive operations on system tables are denied.",
                    "DB", LogLevel.CriticalError);
                return;
            }

            // Audit trail before truncate (system-table path already returned above)
            await LogEventAsync(
                "ScriptEngine", "DbClearTable", "",
                $"{{\"tableName\":\"{tableName}\"}}").ConfigureAwait(false);

            await ExecuteAsync($"DELETE FROM [{tableName}]", _ => { })
                .ConfigureAwait(false);
        }

        public async Task<int> GetRowCountAsync(string tableName)
        {
            if (!IsValidIdentifier(tableName))
            {
                GlobalLogger.Log(
                    $"Invalid identifier '{tableName}' rejected on GetRowCountAsync",
                    "DB", LogLevel.Communication);
                return 0;
            }
            return await QueryScalarAsync<int>(
                $"SELECT COUNT(*) FROM [{tableName}]", _ => { }).ConfigureAwait(false);
        }

        /// <summary>
        /// Cheap change-detection probe: highest rowid in
        /// <paramref name="tableName"/>, or -1 when the table is empty (or the
        /// query fails — <see cref="QueryScalarAsync{T}"/> logs and degrades).
        /// <c>max(rowid)</c> is a rightmost-B-tree-leaf seek that materializes
        /// no row payload, so pollers (the EventLog panel's 2 Hz tail) can ask
        /// "did anything land?" without pulling a full row's multi-KB Payload
        /// through the shared connection.
        /// </summary>
        public async Task<long> GetMaxRowIdAsync(string tableName)
        {
            if (!IsValidIdentifier(tableName))
            {
                GlobalLogger.Log(
                    $"Invalid identifier '{tableName}' rejected on GetMaxRowIdAsync",
                    "DB", LogLevel.Communication);
                return -1;
            }
            long? max = await QueryScalarAsync<long?>(
                $"SELECT max(rowid) FROM [{tableName}]", _ => { }).ConfigureAwait(false);
            return max ?? -1;
        }

        public async Task<List<string>> GetColumnValuesAsync(string tableName, string columnName)
        {
            if (!IsValidIdentifier(tableName) || !IsValidIdentifier(columnName))
            {
                GlobalLogger.Log(
                    $"Invalid identifier '{tableName}/{columnName}' rejected on GetColumnValuesAsync",
                    "DB", LogLevel.Communication);
                return new();
            }
            var result = new List<string>();
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                // ORDER BY rowid makes the result deterministic + row-id-ordered, so a
                // GetColumn('rowid') id list lines up index-for-index with a GetColumn of
                // any value column on the same table (and matches the Databank table
                // view's default order) — that index alignment is what makes "row id as
                // source" usable. [rowid] resolves for every Phoenix table: user tables
                // declare it explicitly, system tables alias the implicit rowid.
                using var cmd = new SqliteCommand($"SELECT [{columnName}] FROM [{tableName}] ORDER BY rowid", _connection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                    result.Add(reader.IsDBNull(0) ? "" : reader.GetValue(0).ToString() ?? "");
            }
            finally { ReleaseLock(taken); }
            return result;
        }

        public async Task<Dictionary<string, string>?> FetchRowByIdAsync(string tableName, long rowId)
        {
            if (!IsValidIdentifier(tableName))
            {
                GlobalLogger.Log(
                    $"Invalid identifier '{tableName}' rejected on FetchRowByIdAsync",
                    "DB", LogLevel.Communication);
                return null;
            }
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand(
                    $"SELECT * FROM [{tableName}] WHERE rowid = @rid", _connection);
                cmd.CommandTimeout = CommandTimeoutSeconds;
                cmd.Parameters.AddWithValue("@rid", rowId);
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                if (!await reader.ReadAsync().ConfigureAwait(false)) return null;
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < reader.FieldCount; i++)
                    d[reader.GetName(i)] = reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString() ?? "";
                // Guarantee a deterministic "rowid" entry regardless of table shape.
                // User tables declare an explicit `rowid INTEGER PRIMARY KEY` column
                // (so SELECT * already returns it), but externally-authored / system
                // tables may alias rowid under a different name (e.g. "Id") or not
                // expose it at all in SELECT *. Stamping the queried rowid here makes
                // {Row.rowid} reliable for every table so scripts can act on a
                // fetched row (db.set_cell / db.delete_row). Overwriting a present
                // "rowid" column is a no-op (same value).
                d["rowid"] = rowId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return d;
            }
            finally { ReleaseLock(taken); }
        }

        public async Task<List<string>> GetTableColumnsAsync(string tableName)
        {
            if (!IsValidIdentifier(tableName))
            {
                GlobalLogger.Log(
                    $"Invalid identifier '{tableName}' rejected on GetTableColumnsAsync",
                    "DB", LogLevel.Communication);
                return new List<string>();
            }
            var cols = new List<string>();
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand($"PRAGMA table_info([{tableName}])", _connection);
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    string name = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    if (!string.IsNullOrEmpty(name) && name != "rowid")
                        cols.Add(name);
                }
            }
            finally { ReleaseLock(taken); }
            return cols;
        }

        public async Task<Dictionary<string, string>> GetTableColumnTypesAsync(string tableName)
        {
            if (!IsValidIdentifier(tableName))
            {
                GlobalLogger.Log(
                    $"Invalid identifier '{tableName}' rejected on GetTableColumnTypesAsync",
                    "DB", LogLevel.Communication);
                return new Dictionary<string, string>();
            }
            var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand($"PRAGMA table_info([{tableName}])", _connection);
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    string name = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    string type = reader.IsDBNull(2) ? "TEXT" : reader.GetString(2).ToUpperInvariant();
                    if (!string.IsNullOrEmpty(name) && name != "rowid")
                        types[name] = type;
                }
            }
            catch { /* DB offline — return empty */ }
            finally { ReleaseLock(taken); }
            return types;
        }

        /// <summary>
        /// Full per-column schema rows for <paramref name="tableName"/>, derived
        /// from SQLite's <c>PRAGMA table_info</c>. Surfaces declared SQL type,
        /// nullability (the <c>notnull</c> column inverted), default-value
        /// expression, and primary-key flag — used by the Architect Databank
        /// Inspector's Schema tab. The implicit <c>rowid</c> primary key is
        /// filtered out so the view shows only user-visible columns; an empty
        /// list means the table doesn't exist or DB is offline.
        /// </summary>
        public async Task<List<ColumnSchemaInfo>> GetSchemaAsync(string tableName)
        {
            if (!IsValidIdentifier(tableName))
            {
                GlobalLogger.Log(
                    $"Invalid identifier '{tableName}' rejected on GetSchemaAsync",
                    "DB", LogLevel.Communication);
                return new List<ColumnSchemaInfo>();
            }
            var schema = new List<ColumnSchemaInfo>();
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand($"PRAGMA table_info([{tableName}])", _connection);
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    // PRAGMA table_info columns: cid | name | type | notnull | dflt_value | pk
                    string name    = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    string sqlType = reader.IsDBNull(2) ? "TEXT" : reader.GetString(2).ToUpperInvariant();
                    bool   notNull = !reader.IsDBNull(3) && reader.GetInt64(3) != 0;
                    string? dflt   = reader.IsDBNull(4) ? null : reader.GetValue(4)?.ToString();
                    bool   isPk    = !reader.IsDBNull(5) && reader.GetInt64(5) != 0;
                    if (string.IsNullOrEmpty(name) || name == "rowid") continue;
                    schema.Add(new ColumnSchemaInfo(name, sqlType, !notNull, dflt, isPk));
                }
            }
            catch { /* DB offline — return empty */ }
            finally { ReleaseLock(taken); }
            return schema;
        }

        /// <summary>
        /// Drop a user table outright. System tables (<c>Vars</c> / <c>EventLog</c> /
        /// <c>SystemHistory</c> + the Viewer-roadmap remote-bridge tables) are
        /// blocked at the persistence layer for defense-in-depth — the
        /// Architect Databank toolbar disables the destructive button when one
        /// is selected, but if a bug ever bypasses that guard the DROP still
        /// won't land.
        /// </summary>
        public async Task DropUserTableAsync(string tableName)
        {
            if (!IsValidIdentifier(tableName))
            {
                GlobalLogger.Log(
                    $"DB.DropUserTable rejected: invalid table identifier '{tableName}'.",
                    "DB", LogLevel.CriticalError);
                return;
            }
            if (IsSystemTable(tableName))
            {
                GlobalLogger.Log(
                    $"DB.DropUserTable BLOCKED: '{tableName}' is a protected system table.",
                    "DB", LogLevel.CriticalError);
                return;
            }
            await ExecuteAsync($"DROP TABLE IF EXISTS [{tableName}]", _ => { });
        }

        public async Task AddColumnAsync(string tableName, string columnName, string columnType)
        {
            if (!IsValidIdentifier(tableName) || !IsValidIdentifier(columnName))
            {
                GlobalLogger.Log(
                    $"DB.AddColumn rejected: invalid identifier(s) — table='{tableName}', column='{columnName}'.",
                    "DB", LogLevel.CriticalError);
                return;
            }
            // System-table protection (parity with DeleteRowAsync / ClearTableAsync).
            // Adding a column to SystemHistory / EventLog / Vars from script-driven
            // db.* commands could break the schema invariants other Hub paths depend on.
            if (IsSystemTable(tableName))
            {
                GlobalLogger.Log(
                    $"DB.AddColumn BLOCKED: '{tableName}' is a protected system table.",
                    "DB", LogLevel.CriticalError);
                return;
            }
            string safeType = columnType switch { "INTEGER" => "INTEGER", "REAL" => "REAL", "BOOLEAN" => "BOOLEAN", _ => "TEXT" };
            await ExecuteAsync($"ALTER TABLE [{tableName}] ADD COLUMN [{columnName}] {safeType}", _ => { });
        }

        // Column-level mutations on the Architect Databank Browser.
        // SQLite 3.25+ supports RENAME COLUMN and 3.35+ supports DROP COLUMN
        // (Microsoft.Data.Sqlite 8.x bundles 3.41+); TYPE changes have no
        // ALTER counterpart and require the safe table-recreation pattern.
        // System tables are rejected at this layer in addition to the UI
        // gate (defense in depth, mirroring AddColumn / DeleteRow / etc).
        // Reserved SQLite row-identifier aliases (rowid, oid, _rowid_) and
        // duplicate column-name collisions are screened so a botched edit
        // can't poison the table_info contract subsequent reads depend on.

        // Affinities accepted by ChangeColumnTypeAsync. SQLite has
        // dynamic typing so the declared affinity is advisory, but limiting
        // the surface to the standard five keeps the DDL emitter predictable
        // and matches what the Architect inspector ComboBox offers.
        private static readonly HashSet<string> _allowedColumnAffinities =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "TEXT", "INTEGER", "REAL", "BLOB", "NUMERIC",
            };

        // Reserved column-name aliases SQLite resolves to the rowid
        // even when no INTEGER PRIMARY KEY column is declared. Letting a
        // user rename a column to any of these would silently shadow the
        // implicit rowid SELECT projection elsewhere in the codebase relies
        // on. Compared case-insensitively.
        private static readonly HashSet<string> _reservedColumnAliases =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "rowid", "oid", "_rowid_",
            };

        /// <summary>
        /// Drop a column from a user table via SQLite's native
        /// <c>ALTER TABLE ... DROP COLUMN</c> (3.35+; Microsoft.Data.Sqlite 8.x
        /// bundles 3.41+ so this is always available). System tables and the
        /// primary-key column are rejected with a logged reason. Returns
        /// <c>true</c> when the column was dropped, <c>false</c> on any
        /// rejection or failure.
        /// </summary>
        public async Task<bool> DropColumnAsync(string tableName, string columnName)
        {
            if (!IsValidIdentifier(tableName) || !IsValidIdentifier(columnName))
            {
                GlobalLogger.Log(
                    $"DB.DropColumn rejected: invalid identifier(s) — table='{tableName}', column='{columnName}'.",
                    "DB", LogLevel.CriticalError);
                return false;
            }
            if (IsSystemTable(tableName))
            {
                GlobalLogger.Log(
                    $"DB.DropColumn BLOCKED: '{tableName}' is a protected system table.",
                    "DB", LogLevel.CriticalError);
                return false;
            }

            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();

                // PRAGMA table_info lookup: reject the primary-key column +
                // confirm the column actually exists. Doing it inside the
                // lock keeps the schema view consistent with the DROP that
                // follows on the same connection.
                bool found = false;
                bool isPk   = false;
                using (var info = new SqliteCommand($"PRAGMA table_info([{tableName}])", _connection))
                {
                    info.CommandTimeout = CommandTimeoutSeconds;
                    using var reader = await info.ExecuteReaderAsync().ConfigureAwait(false);
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        string name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                        if (!string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase)) continue;
                        found = true;
                        isPk  = !reader.IsDBNull(5) && reader.GetInt64(5) != 0;
                        break;
                    }
                }
                if (!found)
                {
                    GlobalLogger.Log(
                        $"DB.DropColumn rejected: column '{columnName}' not present in '{tableName}'.",
                        "DB", LogLevel.CriticalError);
                    return false;
                }
                if (isPk)
                {
                    GlobalLogger.Log(
                        $"DB.DropColumn BLOCKED: '{columnName}' is the primary-key column of '{tableName}'. " +
                        "Dropping the PK would invalidate every rowid-keyed row reference.",
                        "DB", LogLevel.CriticalError);
                    return false;
                }

                using (var drop = new SqliteCommand(
                    $"ALTER TABLE [{tableName}] DROP COLUMN [{columnName}]", _connection))
                {
                    drop.CommandTimeout = CommandTimeoutSeconds;
                    await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                // Match AddColumnAsync's reach — no per-call wal checkpoint
                // is fired (the periodic timer covers steady-state growth).
                GlobalLogger.Log(
                    $"DB.DropColumn: '{tableName}.{columnName}' dropped.",
                    "DB", LogLevel.System);
                return true;
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("DB",
                    $"DropColumnAsync failed for '{tableName}.{columnName}'", ex);
                return false;
            }
            finally
            {
                ReleaseLock(taken);
            }
        }

        /// <summary>
        /// Rename a column on a user table via SQLite's native
        /// <c>ALTER TABLE ... RENAME COLUMN</c> (3.25+; always available with
        /// Microsoft.Data.Sqlite 8.x). System tables, reserved rowid aliases
        /// (<c>rowid</c> / <c>oid</c> / <c>_rowid_</c>), and case-insensitive
        /// collisions against an existing column are rejected. Returns
        /// <c>true</c> on success.
        /// </summary>
        public async Task<bool> RenameColumnAsync(string tableName, string oldName, string newName)
        {
            if (!IsValidIdentifier(tableName) || !IsValidIdentifier(oldName) || !IsValidIdentifier(newName))
            {
                GlobalLogger.Log(
                    $"DB.RenameColumn rejected: invalid identifier(s) — table='{tableName}', old='{oldName}', new='{newName}'.",
                    "DB", LogLevel.CriticalError);
                return false;
            }
            if (IsSystemTable(tableName))
            {
                GlobalLogger.Log(
                    $"DB.RenameColumn BLOCKED: '{tableName}' is a protected system table.",
                    "DB", LogLevel.CriticalError);
                return false;
            }
            if (_reservedColumnAliases.Contains(newName))
            {
                GlobalLogger.Log(
                    $"DB.RenameColumn BLOCKED: '{newName}' is a reserved SQLite row-identifier alias.",
                    "DB", LogLevel.CriticalError);
                return false;
            }
            // No-op rename: succeed silently so callers can pipe the same
            // text through Enter without surfacing a spurious error banner.
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
                return true;

            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();

                // Enumerate existing columns once: confirm oldName exists and
                // newName does NOT already exist (case-insensitive — SQLite
                // identifier matching is). A case-only rename
                // (foo → Foo) is allowed because PRAGMA table_info still sees
                // them as the same identifier and SQLite handles it.
                bool sourceFound = false;
                bool targetCollision = false;
                using (var info = new SqliteCommand($"PRAGMA table_info([{tableName}])", _connection))
                {
                    info.CommandTimeout = CommandTimeoutSeconds;
                    using var reader = await info.ExecuteReaderAsync().ConfigureAwait(false);
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        string name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                        if (string.Equals(name, oldName, StringComparison.OrdinalIgnoreCase))
                            sourceFound = true;
                        else if (string.Equals(name, newName, StringComparison.OrdinalIgnoreCase))
                            targetCollision = true;
                    }
                }
                if (!sourceFound)
                {
                    GlobalLogger.Log(
                        $"DB.RenameColumn rejected: column '{oldName}' not present in '{tableName}'.",
                        "DB", LogLevel.CriticalError);
                    return false;
                }
                if (targetCollision)
                {
                    GlobalLogger.Log(
                        $"DB.RenameColumn BLOCKED: '{newName}' already exists in '{tableName}'.",
                        "DB", LogLevel.CriticalError);
                    return false;
                }

                using (var rename = new SqliteCommand(
                    $"ALTER TABLE [{tableName}] RENAME COLUMN [{oldName}] TO [{newName}]", _connection))
                {
                    rename.CommandTimeout = CommandTimeoutSeconds;
                    await rename.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                GlobalLogger.Log(
                    $"DB.RenameColumn: '{tableName}.{oldName}' → '{tableName}.{newName}'.",
                    "DB", LogLevel.System);
                return true;
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("DB",
                    $"RenameColumnAsync failed for '{tableName}.{oldName}' → '{newName}'", ex);
                return false;
            }
            finally
            {
                ReleaseLock(taken);
            }
        }

        /// <summary>
        /// Change a column's declared SQLite affinity. SQLite has no
        /// <c>ALTER COLUMN ... TYPE</c>, so this performs the safe
        /// table-recreation pattern under a single transaction: read
        /// <c>PRAGMA table_info</c>, build a <c>__tmp_&lt;table&gt;</c> with
        /// the new type substituted for <paramref name="columnName"/>,
        /// <c>INSERT INTO ... SELECT *</c> (SQLite coerces on insert), drop
        /// the original, rename the temp back. Only the standard affinities
        /// (<c>TEXT / INTEGER / REAL / BLOB / NUMERIC</c>) are accepted; system
        /// tables and unknown columns are rejected. Returns <c>true</c> on
        /// success.
        /// </summary>
        public async Task<bool> ChangeColumnTypeAsync(string tableName, string columnName, string newAffinityType)
        {
            if (!IsValidIdentifier(tableName) || !IsValidIdentifier(columnName))
            {
                GlobalLogger.Log(
                    $"DB.ChangeColumnType rejected: invalid identifier(s) — table='{tableName}', column='{columnName}'.",
                    "DB", LogLevel.CriticalError);
                return false;
            }
            if (IsSystemTable(tableName))
            {
                GlobalLogger.Log(
                    $"DB.ChangeColumnType BLOCKED: '{tableName}' is a protected system table.",
                    "DB", LogLevel.CriticalError);
                return false;
            }
            if (string.IsNullOrWhiteSpace(newAffinityType) ||
                !_allowedColumnAffinities.Contains(newAffinityType))
            {
                GlobalLogger.Log(
                    $"DB.ChangeColumnType rejected: '{newAffinityType}' is not a standard SQLite affinity " +
                    "(allowed: TEXT, INTEGER, REAL, BLOB, NUMERIC).",
                    "DB", LogLevel.CriticalError);
                return false;
            }
            string normalisedAffinity = newAffinityType.ToUpperInvariant();

            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();

                // Snapshot the full schema so we can faithfully recreate the
                // table with one column's type swapped. Each entry holds the
                // (name, type, notnull, dflt_value, pk) tuple PRAGMA reports.
                var cols = new List<(string Name, string Type, bool NotNull, string? Default, bool Pk)>();
                using (var info = new SqliteCommand($"PRAGMA table_info([{tableName}])", _connection))
                {
                    info.CommandTimeout = CommandTimeoutSeconds;
                    using var reader = await info.ExecuteReaderAsync().ConfigureAwait(false);
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        string name    = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                        string type    = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                        bool   notNull = !reader.IsDBNull(3) && reader.GetInt64(3) != 0;
                        string? dflt   = reader.IsDBNull(4) ? null : reader.GetValue(4)?.ToString();
                        bool   isPk    = !reader.IsDBNull(5) && reader.GetInt64(5) != 0;
                        if (string.IsNullOrEmpty(name)) continue;
                        cols.Add((name, type, notNull, dflt, isPk));
                    }
                }
                if (cols.Count == 0)
                {
                    GlobalLogger.Log(
                        $"DB.ChangeColumnType rejected: table '{tableName}' has no readable schema.",
                        "DB", LogLevel.CriticalError);
                    return false;
                }
                int targetIdx = -1;
                for (int i = 0; i < cols.Count; i++)
                {
                    if (string.Equals(cols[i].Name, columnName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetIdx = i;
                        break;
                    }
                }
                if (targetIdx < 0)
                {
                    GlobalLogger.Log(
                        $"DB.ChangeColumnType rejected: column '{columnName}' not present in '{tableName}'.",
                        "DB", LogLevel.CriticalError);
                    return false;
                }

                // Build the CREATE TABLE __tmp_<name> DDL. Reuse the existing
                // column ordering, preserve PK + NOT NULL + DEFAULT, swap the
                // target column's declared type for the new affinity. The
                // temp name is prefixed with a double-underscore so it can
                // never collide with a user table (IsValidIdentifier accepts
                // names starting with [A-Za-z_], but the user has no UI to
                // author "__tmp_*"); we still defensively drop any leftover
                // temp before creating it.
                string tmpName = "__tmp_" + tableName;
                var ddl = new StringBuilder();
                ddl.Append("CREATE TABLE [").Append(tmpName).Append("] (");
                for (int i = 0; i < cols.Count; i++)
                {
                    if (i > 0) ddl.Append(", ");
                    var c = cols[i];
                    string colType = (i == targetIdx) ? normalisedAffinity : c.Type;
                    ddl.Append('[').Append(c.Name).Append(']');
                    if (!string.IsNullOrEmpty(colType))
                        ddl.Append(' ').Append(colType);
                    if (c.Pk)
                    {
                        // SQLite recognises INTEGER PRIMARY KEY as a rowid
                        // alias; keep the AUTOINCREMENT semantics aligned
                        // with CreateUserTableAsync's "rowid INTEGER PRIMARY
                        // KEY AUTOINCREMENT" emission so the recreated
                        // table behaves identically.
                        ddl.Append(" PRIMARY KEY");
                        if (string.Equals(c.Name, "rowid", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(colType, "INTEGER", StringComparison.OrdinalIgnoreCase))
                            ddl.Append(" AUTOINCREMENT");
                    }
                    if (c.NotNull) ddl.Append(" NOT NULL");
                    if (!string.IsNullOrEmpty(c.Default))
                        ddl.Append(" DEFAULT ").Append(c.Default);
                }
                ddl.Append(");");

                // Column list for INSERT ... SELECT. Quoted identifiers in
                // declaration order on both sides so SQLite's per-column
                // coercion-on-insert lands the data in the new affinity
                // without us having to convert in C#.
                var colList = new StringBuilder();
                for (int i = 0; i < cols.Count; i++)
                {
                    if (i > 0) colList.Append(", ");
                    colList.Append('[').Append(cols[i].Name).Append(']');
                }

                // Single transaction. Microsoft.Data.Sqlite forwards BEGIN /
                // COMMIT through to SQLite; we don't reach for the higher-
                // level DbTransaction since we already serialise on _lock.
                using (var tx = _connection!.BeginTransaction())
                {
                    try
                    {
                        // Defensive cleanup — if a previous attempt crashed
                        // mid-recreate, the leftover __tmp_ table would block
                        // the CREATE below with a "table already exists"
                        // error. DROP IF EXISTS makes the operation idempotent.
                        using (var cleanup = new SqliteCommand(
                            $"DROP TABLE IF EXISTS [{tmpName}]", _connection))
                        {
                            cleanup.Transaction = tx;
                            cleanup.CommandTimeout = CommandTimeoutSeconds;
                            await cleanup.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                        using (var create = new SqliteCommand(ddl.ToString(), _connection))
                        {
                            create.Transaction = tx;
                            create.CommandTimeout = CommandTimeoutSeconds;
                            await create.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                        using (var copy = new SqliteCommand(
                            $"INSERT INTO [{tmpName}] ({colList}) SELECT {colList} FROM [{tableName}]",
                            _connection))
                        {
                            copy.Transaction = tx;
                            copy.CommandTimeout = CommandTimeoutSeconds;
                            await copy.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                        using (var drop = new SqliteCommand(
                            $"DROP TABLE [{tableName}]", _connection))
                        {
                            drop.Transaction = tx;
                            drop.CommandTimeout = CommandTimeoutSeconds;
                            await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                        using (var rename = new SqliteCommand(
                            $"ALTER TABLE [{tmpName}] RENAME TO [{tableName}]", _connection))
                        {
                            rename.Transaction = tx;
                            rename.CommandTimeout = CommandTimeoutSeconds;
                            await rename.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                        tx.Commit();
                    }
                    catch
                    {
                        try { tx.Rollback(); } catch { /* best effort */ }
                        throw;
                    }
                }

                GlobalLogger.Log(
                    $"DB.ChangeColumnType: '{tableName}.{columnName}' → {normalisedAffinity}.",
                    "DB", LogLevel.System);
                return true;
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("DB",
                    $"ChangeColumnTypeAsync failed for '{tableName}.{columnName}' → '{newAffinityType}'", ex);
                return false;
            }
            finally
            {
                ReleaseLock(taken);
            }
        }

        public async Task<long?> FindRowAsync(string tableName, string columnName, string searchValue)
        {
            if (!IsValidIdentifier(tableName) || !IsValidIdentifier(columnName))
            {
                GlobalLogger.Log(
                    $"Invalid identifier '{tableName}/{columnName}' rejected on FindRowAsync",
                    "DB", LogLevel.Communication);
                return null;
            }
            long? result = await QueryScalarAsync<long?>(
                $"SELECT rowid FROM [{tableName}] WHERE [{columnName}] = @val LIMIT 1",
                cmd => cmd.Parameters.AddWithValue("@val", searchValue)).ConfigureAwait(false);
            return result;
        }

        public async Task<string> GetCellAsync(string tableName, long rowId, string columnName)
        {
            if (!IsValidIdentifier(tableName) || !IsValidIdentifier(columnName))
            {
                GlobalLogger.Log(
                    $"Invalid identifier '{tableName}/{columnName}' rejected on GetCellAsync",
                    "DB", LogLevel.Communication);
                return "";
            }
            string? result = await QueryScalarAsync<string>(
                $"SELECT [{columnName}] FROM [{tableName}] WHERE rowid = @rid",
                cmd => cmd.Parameters.AddWithValue("@rid", rowId)).ConfigureAwait(false);
            return result ?? "";
        }

        // ── Viewer-roadmap Slice 0: Remote-bridge tables ─────────────────
        // These two tables back the Hub's pairing + audit subsystems for
        // Phoenix.Controls.Viewer. They are listed in `_systemTables` above
        // so remote /api/db/* writes can never touch them. The Hub still
        // manages them directly through the helpers below.

        public async Task EnsurePairedDevicesTableAsync()
        {
            const string ddl = @"
                CREATE TABLE IF NOT EXISTS PairedDevices (
                    DeviceId  TEXT PRIMARY KEY,
                    Label     TEXT,
                    TokenHash BLOB,
                    TokenSalt BLOB,
                    CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    LastSeen  DATETIME,
                    Revoked   INTEGER DEFAULT 0
                );";
            await ExecuteAsync(ddl, _ => { }).ConfigureAwait(false);
        }

        public async Task EnsureRemoteAuditLogTableAsync()
        {
            const string ddl = @"
                CREATE TABLE IF NOT EXISTS RemoteAuditLog (
                    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    At          DATETIME DEFAULT CURRENT_TIMESTAMP,
                    DeviceId    TEXT,
                    Action      TEXT,
                    TargetTable TEXT,
                    TargetKey   TEXT,
                    Before      TEXT,
                    After       TEXT,
                    Result      TEXT
                );";
            await ExecuteAsync(ddl, _ => { }).ConfigureAwait(false);
        }

        /// <summary>
        /// Inserts (or replaces, on DeviceId conflict) a paired-device row. The
        /// auth manager owns hash/salt derivation; this helper just persists.
        /// </summary>
        public async Task UpsertPairedDeviceAsync(string deviceId, string label, byte[] tokenHash, byte[] tokenSalt)
        {
            const string sql = @"
                INSERT INTO PairedDevices (DeviceId, Label, TokenHash, TokenSalt, CreatedAt, Revoked)
                VALUES (@id, @label, @hash, @salt, CURRENT_TIMESTAMP, 0)
                ON CONFLICT(DeviceId) DO UPDATE SET
                    Label     = excluded.Label,
                    TokenHash = excluded.TokenHash,
                    TokenSalt = excluded.TokenSalt,
                    Revoked   = 0;";
            await ExecuteAsync(sql, cmd =>
            {
                cmd.Parameters.AddWithValue("@id",    deviceId);
                cmd.Parameters.AddWithValue("@label", label ?? "");
                cmd.Parameters.AddWithValue("@hash",  (object?)tokenHash ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@salt",  (object?)tokenSalt ?? DBNull.Value);
            }).ConfigureAwait(false);
        }

        public async Task<List<PairedDeviceRow>> ListPairedDevicesAsync(bool includeRevoked = false)
        {
            var rows = new List<PairedDeviceRow>();
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                string sql = includeRevoked
                    ? "SELECT DeviceId, Label, TokenHash, TokenSalt, CreatedAt, LastSeen, Revoked FROM PairedDevices ORDER BY CreatedAt DESC"
                    : "SELECT DeviceId, Label, TokenHash, TokenSalt, CreatedAt, LastSeen, Revoked FROM PairedDevices WHERE Revoked = 0 ORDER BY CreatedAt DESC";
                using var cmd = new SqliteCommand(sql, _connection);
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    rows.Add(new PairedDeviceRow
                    {
                        DeviceId  = reader.IsDBNull(0) ? "" : reader.GetString(0),
                        Label     = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        TokenHash = reader.IsDBNull(2) ? Array.Empty<byte>() : (byte[])reader.GetValue(2),
                        TokenSalt = reader.IsDBNull(3) ? Array.Empty<byte>() : (byte[])reader.GetValue(3),
                        CreatedAt = reader.IsDBNull(4) ? DateTime.MinValue : reader.GetDateTime(4),
                        LastSeen  = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                        Revoked   = !reader.IsDBNull(6) && reader.GetInt64(6) != 0,
                    });
                }
            }
            finally { ReleaseLock(taken); }
            return rows;
        }

        public async Task<PairedDeviceRow?> GetPairedDeviceAsync(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return null;
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var cmd = new SqliteCommand(
                    "SELECT DeviceId, Label, TokenHash, TokenSalt, CreatedAt, LastSeen, Revoked FROM PairedDevices WHERE DeviceId = @id",
                    _connection);
                cmd.Parameters.AddWithValue("@id", deviceId);
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                if (!await reader.ReadAsync().ConfigureAwait(false)) return null;
                return new PairedDeviceRow
                {
                    DeviceId  = reader.IsDBNull(0) ? "" : reader.GetString(0),
                    Label     = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    TokenHash = reader.IsDBNull(2) ? Array.Empty<byte>() : (byte[])reader.GetValue(2),
                    TokenSalt = reader.IsDBNull(3) ? Array.Empty<byte>() : (byte[])reader.GetValue(3),
                    CreatedAt = reader.IsDBNull(4) ? DateTime.MinValue : reader.GetDateTime(4),
                    LastSeen  = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                    Revoked   = !reader.IsDBNull(6) && reader.GetInt64(6) != 0,
                };
            }
            finally { ReleaseLock(taken); }
        }

        public async Task TouchPairedDeviceLastSeenAsync(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;
            await ExecuteAsync(
                "UPDATE PairedDevices SET LastSeen = CURRENT_TIMESTAMP WHERE DeviceId = @id",
                cmd => cmd.Parameters.AddWithValue("@id", deviceId)).ConfigureAwait(false);
        }

        public async Task RevokePairedDeviceAsync(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;
            await ExecuteAsync(
                "UPDATE PairedDevices SET Revoked = 1 WHERE DeviceId = @id",
                cmd => cmd.Parameters.AddWithValue("@id", deviceId)).ConfigureAwait(false);
        }

        public async Task AppendRemoteAuditAsync(
            string deviceId,
            string action,
            string targetTable,
            string targetKey,
            string? before,
            string? after,
            string result)
        {
            const string sql = @"
                INSERT INTO RemoteAuditLog (DeviceId, Action, TargetTable, TargetKey, Before, After, Result)
                VALUES (@dev, @action, @table, @key, @before, @after, @result)";
            await ExecuteAsync(sql, cmd =>
            {
                cmd.Parameters.AddWithValue("@dev",    deviceId    ?? "");
                cmd.Parameters.AddWithValue("@action", action      ?? "");
                cmd.Parameters.AddWithValue("@table",  targetTable ?? "");
                cmd.Parameters.AddWithValue("@key",    targetKey   ?? "");
                cmd.Parameters.AddWithValue("@before", (object?)before ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@after",  (object?)after  ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@result", result      ?? "");
            }).ConfigureAwait(false);
        }

        public async Task SetCellAsync(string tableName, long rowId, string columnName, string value)
        {
            if (!IsValidIdentifier(tableName) || !IsValidIdentifier(columnName))
            {
                GlobalLogger.Log(
                    $"DB.SetCell rejected: invalid identifier(s) — table='{tableName}', column='{columnName}'.",
                    "DB", LogLevel.CriticalError);
                return;
            }
            // System-table protection (parity with DeleteRowAsync / ClearTableAsync).
            if (IsSystemTable(tableName))
            {
                GlobalLogger.Log(
                    $"DB.SetCell BLOCKED: '{tableName}' is a protected system table.",
                    "DB", LogLevel.CriticalError);
                return;
            }
            await ExecuteAsync(
                $"UPDATE [{tableName}] SET [{columnName}] = @val WHERE rowid = @rid",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@val", value);
                    cmd.Parameters.AddWithValue("@rid", rowId);
                }).ConfigureAwait(false);
        }

        public void Dispose()
        {
            // Stop the periodic WAL checkpoint timer
            // FIRST so a scheduled callback can't fire mid-Dispose and try
            // to take the soon-to-be-disposed semaphore. Timer.Dispose is
            // safe to call multiple times and on a never-started timer.
            try { _walCheckpointTimer?.Dispose(); }
            catch { /* best effort */ }
            _walCheckpointTimer = null;

            // Same reasoning for the EventLog row-cap sweep timer.
            try { _eventLogRowCapTimer?.Dispose(); }
            catch { /* best effort */ }
            _eventLogRowCapTimer = null;

            // Drain in-flight callers before closing the connection / disposing
            // the semaphore. A bare Close+Dispose races with any caller currently
            // awaiting _lock.WaitAsync(): they'd resume after Dispose() and hit a
            // closed connection or a disposed SemaphoreSlim, surfacing as
            // ObjectDisposedException at best and AccessViolation at worst.
            //
            // Acquiring the lock with a bounded wait gives in-flight work a chance
            // to drain. We never deadlock — if drain times out we proceed anyway
            // and log it; the caller is more important than perfect quiescence.
            bool drained = false;
            try
            {
                drained = _lock.Wait(TimeSpan.FromSeconds(5));
            }
            catch (ObjectDisposedException)
            {
                // Already disposed — nothing more to do.
                return;
            }

            try
            {
                if (!drained)
                {
                    GlobalLogger.Error(
                        "DB",
                        "Dispose drain timed out after 5s — closing connection with in-flight callers still pending. " +
                        "Pending awaiters may observe ObjectDisposedException.");
                }

                // Set _disposed under _initLock BEFORE tearing the
                // connection down, so any thread racing through EnsureConnected
                // either observes the flag (and throws) or already passed the
                // fast path (and will fail on a closed connection — which the
                // caller's per-query try/catch already handles). Without the
                // flag, EnsureConnected would happily Open() a fresh
                // SqliteConnection against the disposed singleton.
                lock (_initLock)
                {
                    _disposed = true;
                    // Guard the primary connection teardown so a
                    // throw here can't skip the dedicated log-connection teardown below
                    // (which would leak its SqliteConnection + WAL/-shm handles).
                    try { _connection?.Close(); _connection?.Dispose(); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[DB] primary connection dispose failed: {ex}"); }
                    _connection = null;
                }

                // The dedicated log connection
                // (_logDbConnection, used by GlobalLogger's writer pump via
                // WriteLogDedicatedAsync) was never closed/disposed here — it
                // leaked a SqliteConnection plus its WAL/-shm handles on every
                // shutdown. Tear it down under _logDbInitLock (the same gate
                // EnsureLogDbConnection takes) so a concurrent open can't race
                // the dispose; null it so a later EnsureLogDbConnection re-opens
                // cleanly rather than touching a disposed object.
                try
                {
                    lock (_logDbInitLock)
                    {
                        _logDbConnection?.Close();
                        _logDbConnection?.Dispose();
                        _logDbConnection = null;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DB] log connection dispose failed: {ex}");
                }

                // Same teardown for the dedicated bulk-read connection
                // (GetTableDataAsync) — close under its own init lock so a
                // concurrent EnsureBulkReadConnection can't race the dispose,
                // and null it so a fresh singleton re-opens against the
                // (possibly re-targeted) _dbPath.
                try
                {
                    lock (_bulkReadInitLock)
                    {
                        _bulkReadConnection?.Close();
                        _bulkReadConnection?.Dispose();
                        _bulkReadConnection = null;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DB] bulk-read connection dispose failed: {ex}");
                }
            }
            finally
            {
                if (drained)
                {
                    // Release the slot so any opportunistic re-entrant wait inside
                    // Dispose paths (e.g. logging that loops back through the DB)
                    // doesn't permanently strand a permit.
                    try { _lock.Release(); } catch (ObjectDisposedException) { }
                    catch (SemaphoreFullException) { }
                }
                _lock.Dispose();
            }

            // Clear the singleton reference so the next access creates a fresh instance
            // rather than returning a disposed object.
            if (ReferenceEquals(_instance, this))
                _instance = null;
        }
    }

    /// <summary>
    /// Per-column schema shape returned by <see cref="DB.GetSchemaAsync"/> — feeds
    /// the Architect Databank inspector's Schema tab. The display projections
    /// (`*Display`) collapse the raw fields into the right-hand-column glyphs the
    /// inspector renders (`NULL` / `NOT NULL`, `PK` / blank, default literal).
    /// </summary>
    public sealed record ColumnSchemaInfo(
        string  Name,
        string  SqlType,
        bool    Nullable,
        string? Default,
        bool    PrimaryKey = false)
    {
        /// <summary>"NOT NULL" when the column rejects NULL, dash otherwise.</summary>
        public string NotNullDisplay => Nullable ? "—" : "NOT NULL";

        /// <summary>"PK" when this is a primary-key column, dash otherwise.</summary>
        public string PkDisplay      => PrimaryKey ? "PK" : "—";

        /// <summary>Default-value expression or dash when none was declared.</summary>
        public string DefaultDisplay => string.IsNullOrEmpty(Default) ? "—" : Default!;
    }

    /// <summary>
    /// Row shape from the Hub's `PairedDevices` table — used by RemoteAuthManager
    /// for token verification and by the Remote Devices panel for UI rendering.
    /// </summary>
    public sealed class PairedDeviceRow
    {
        public string   DeviceId  { get; set; } = string.Empty;
        public string   Label     { get; set; } = string.Empty;
        public byte[]   TokenHash { get; set; } = Array.Empty<byte>();
        public byte[]   TokenSalt { get; set; } = Array.Empty<byte>();
        public DateTime CreatedAt { get; set; }
        public DateTime? LastSeen { get; set; }
        public bool     Revoked   { get; set; }
    }
}
