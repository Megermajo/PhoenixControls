using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Phoenix.Controls.Shared.Models;
using System.Threading.Tasks;

namespace Phoenix.Controls.Shared.Services
{
    // Loyalty persistence — the DB half of the viewer points-economy tool.
    //
    // OPEN-TABLE DOCTRINE (load-bearing): the BALANCE and LEDGER live in ordinary
    // USER tables (name/currency shape — the same wallet the Giveaway ticket-price
    // charges) and are DELIBERATELY NOT in DB._systemTables, so a streamer keeps
    // full db.* script access to build their own functions on the points. Only the
    // tool's private LoyaltyConfig (a single JSON blob, mirroring Timers.Json) is a
    // system table. The DB is the single source of truth for balances — never an
    // in-memory cache — because db.* scripts, the Giveaway charge, and this tool all
    // write the same open table; every mutation is one transaction under the shared
    // AcquireLockAsync guard, rowid-scoped (dup-name safe), non-negativity enforced
    // at the money layer (Credit/Set require >= 0, Debit/bet require a positive
    // amount + funds) — which kills the negative-bet exploit regardless of game
    // logic. CoerceBalance / IsMissingTableOrColumn are shared with DB.Giveaway.cs.
    public partial class DB
    {
        internal const string LoyaltyConfigTablesDdl = @"
            CREATE TABLE IF NOT EXISTS LoyaltyConfig (
                Slug      TEXT PRIMARY KEY,
                Json      TEXT    NOT NULL,
                UpdatedAt INTEGER NOT NULL DEFAULT 0
            );";

        // Symmetry with EnsureGiveaway/TimerSchemaMigrations: back-fill any column
        // added after an earlier dev build. Fresh installs no-op through the probe.
        private void EnsureLoyaltySchemaMigrations()
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var probe = new SqliteCommand("PRAGMA table_info(LoyaltyConfig)", _connection))
            using (var r = probe.ExecuteReader())
            {
                while (r.Read()) existing.Add(r.GetString(1));
            }
            var wanted = new (string Column, string Ddl)[]
            {
                ("Json",      "ALTER TABLE LoyaltyConfig ADD COLUMN Json TEXT DEFAULT ''"),
                ("UpdatedAt", "ALTER TABLE LoyaltyConfig ADD COLUMN UpdatedAt INTEGER NOT NULL DEFAULT 0"),
            };
            foreach (var (column, ddl) in wanted)
            {
                if (existing.Contains(column)) continue;
                using var alter = new SqliteCommand(ddl, _connection);
                alter.ExecuteNonQuery();
            }
        }

        // ── Config blob (system table) ──────────────────────────────────────

        /// <summary>Loads the serialized LoyaltyConfig JSON, or null when unset.</summary>
        public async Task<string?> LoadLoyaltyConfigAsync()
            => await QueryScalarAsync<string>(
                "SELECT Json FROM LoyaltyConfig WHERE Slug = 'config' LIMIT 1", _ => { }).ConfigureAwait(false);

        /// <summary>Persists the serialized LoyaltyConfig JSON.</summary>
        public async Task SaveLoyaltyConfigAsync(string json, long updatedAtMs)
        {
            await ExecuteAsync(
                @"INSERT INTO LoyaltyConfig (Slug, Json, UpdatedAt) VALUES ('config', @json, @upd)
                  ON CONFLICT(Slug) DO UPDATE SET Json = @json, UpdatedAt = @upd",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@json", json ?? "");
                    cmd.Parameters.AddWithValue("@upd", updatedAtMs);
                }).ConfigureAwait(false);
        }

        // ── Open wallet / ledger tables ─────────────────────────────────────

        /// <summary>Creates the OPEN balance (name/currency) and ledger tables if
        /// missing. They are NEVER protected — full db.* access is the point.
        /// A pre-existing user table of the same name is left untouched.</summary>
        /// <remarks>
        /// The name check is <see cref="IsAppOwnedTable"/> — the WIDE list — not
        /// the two-name write lock, and that is deliberate. This method ALTERs
        /// the table it is handed to guarantee the name/currency shape, so
        /// pointing a wallet at (say) <c>Timers</c> would rewrite a table Phoenix
        /// Controls maintains. The 2026-08 unlock opened those tables to row and
        /// cell writes; it did NOT make them legal targets for a schema ensure.
        /// </remarks>
        public async Task EnsureLoyaltyWalletTablesAsync(string balanceTable, string ledgerTable, bool ledgerEnabled)
        {
            if (!IsValidIdentifier(balanceTable) || IsAppOwnedTable(balanceTable))
            {
                GlobalLogger.Log(
                    $"Loyalty: balance table '{balanceTable}' is invalid, or is a table Phoenix Controls maintains " +
                    "(its schema is not ours to rewrite) — not created. Pick a different name for your points table.",
                    "Loyalty", LogLevel.CriticalError);
                return;
            }
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                // `currency` is NOT NULL DEFAULT 0 so a freshly-created wallet can never
                // reach the NULL state at all: a script's db.insert_row that names only
                // the viewer gets 0 rather than NULL. (The hardening in CreditDeltaTx is
                // what repairs a table that PREDATES this DDL, or one the streamer built
                // by hand — CREATE TABLE IF NOT EXISTS leaves those untouched.)
                using (var cmd = new SqliteCommand(
                    $"CREATE TABLE IF NOT EXISTS [{balanceTable}] (rowid INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, currency INTEGER NOT NULL DEFAULT 0);",
                    _connection))
                {
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                if (ledgerEnabled && IsValidIdentifier(ledgerTable) && !IsAppOwnedTable(ledgerTable))
                {
                    using var led = new SqliteCommand(
                        $"CREATE TABLE IF NOT EXISTS [{ledgerTable}] (rowid INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT, recipient TEXT, sender TEXT, amount INTEGER, reason TEXT);",
                        _connection);
                    led.CommandTimeout = CommandTimeoutSeconds;
                    await led.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
            finally { ReleaseLock(taken); }
        }

        // ── Reads ───────────────────────────────────────────────────────────

        /// <summary>Balance for a user (0 when the row/table/column is missing).</summary>
        public async Task<long> LoyaltyGetBalanceAsync(string balanceTable, string name)
        {
            if (!IsValidIdentifier(balanceTable) || IsAppOwnedTable(balanceTable)) return 0;
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    using var cmd = new SqliteCommand(
                        $"SELECT [currency] FROM [{balanceTable}] WHERE [name] = @u COLLATE NOCASE LIMIT 1", _connection);
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    cmd.Parameters.AddWithValue("@u", name ?? "");
                    return CoerceBalance(await cmd.ExecuteScalarAsync().ConfigureAwait(false));
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return 0; }
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>Total named rows in the balance table (0 when missing) —
        /// the real tracked-viewer count, independent of any top-N page cap.</summary>
        public async Task<long> LoyaltyCountViewersAsync(string balanceTable)
        {
            if (!IsValidIdentifier(balanceTable) || IsAppOwnedTable(balanceTable)) return 0;
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    using var cmd = new SqliteCommand(
                        $"SELECT COUNT(*) FROM [{balanceTable}] WHERE [name] IS NOT NULL AND [name] <> ''", _connection);
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    object? raw = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                    return raw is long l ? l : Convert.ToInt64(raw ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return 0; }
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>Top-N standings by balance, rank starting at 1.</summary>
        public async Task<List<LoyaltyStanding>> LoyaltyTopAsync(string balanceTable, int n)
        {
            var list = new List<LoyaltyStanding>();
            if (!IsValidIdentifier(balanceTable) || IsAppOwnedTable(balanceTable) || n <= 0) return list;
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    using var cmd = new SqliteCommand(
                        $"SELECT [name], [currency] FROM [{balanceTable}] " +
                        "WHERE [name] IS NOT NULL AND [name] <> '' " +
                        "ORDER BY CAST([currency] AS INTEGER) DESC LIMIT @lim", _connection);
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    cmd.Parameters.AddWithValue("@lim", n);
                    using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                    int rank = 0;
                    while (await r.ReadAsync().ConfigureAwait(false))
                    {
                        rank++;
                        list.Add(new LoyaltyStanding(
                            r.IsDBNull(0) ? "" : r.GetString(0),
                            CoerceBalance(r.IsDBNull(1) ? null : r.GetValue(1)),
                            rank));
                    }
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return list; }
            }
            finally { ReleaseLock(taken); }
            return list;
        }

        /// <summary>Recent banking-log rows, newest first.</summary>
        public async Task<List<LoyaltyLedgerEntry>> LoyaltyLedgerRecentAsync(string ledgerTable, int limit = 100)
        {
            var list = new List<LoyaltyLedgerEntry>();
            if (!IsValidIdentifier(ledgerTable) || IsAppOwnedTable(ledgerTable) || limit <= 0) return list;
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    using var cmd = new SqliteCommand(
                        $"SELECT rowid, [ts], [recipient], [sender], [amount], [reason] FROM [{ledgerTable}] " +
                        "ORDER BY rowid DESC LIMIT @lim", _connection);
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    cmd.Parameters.AddWithValue("@lim", limit);
                    using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                    while (await r.ReadAsync().ConfigureAwait(false))
                    {
                        list.Add(new LoyaltyLedgerEntry
                        {
                            Id        = r.IsDBNull(0) ? 0 : r.GetInt64(0),
                            Time      = r.IsDBNull(1) ? "" : r.GetString(1),
                            Recipient = r.IsDBNull(2) ? "" : r.GetString(2),
                            Sender    = r.IsDBNull(3) ? "" : r.GetString(3),
                            Amount    = CoerceBalance(r.IsDBNull(4) ? null : r.GetValue(4)),
                            Reason    = r.IsDBNull(5) ? "" : r.GetString(5),
                        });
                    }
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return list; }
            }
            finally { ReleaseLock(taken); }
            return list;
        }

        // ── Atomic mutations ────────────────────────────────────────────────

        /// <summary>Credits a positive delta, creating the row on first earn. Rejects delta &lt;= 0.</summary>
        public Task<LoyaltyResult> LoyaltyCreditAsync(string balanceTable, string name, long delta,
            string? ledgerTable, string sender, string reason)
        {
            if (delta <= 0) return Task.FromResult(LoyaltyResult.Fail(LoyaltyOutcome.Invalid));
            return MutateAsync(balanceTable, async (tx) =>
            {
                await CreditDeltaTx(tx, balanceTable, name, delta).ConfigureAwait(false);
                long bal = await ReadBalanceTx(tx, balanceTable, name).ConfigureAwait(false);
                await AppendLedgerTx(tx, LedgerTableOrNull(ledgerTable), name, sender, delta, reason).ConfigureAwait(false);
                return new LoyaltyResult(LoyaltyOutcome.Ok, bal, delta);
            });
        }

        /// <summary>Debits a positive amount if funds cover it; else NoFunds. Floors at 0.</summary>
        public Task<LoyaltyResult> LoyaltyDebitAsync(string balanceTable, string name, long amount,
            string? ledgerTable, string sinkLabel, string reason)
        {
            if (amount <= 0) return Task.FromResult(LoyaltyResult.Fail(LoyaltyOutcome.Invalid));
            return MutateAsync(balanceTable, async (tx) =>
            {
                long bal = await ReadBalanceTx(tx, balanceTable, name).ConfigureAwait(false);
                if (bal < amount) return LoyaltyResult.Fail(LoyaltyOutcome.NoFunds, bal);
                await CreditDeltaTx(tx, balanceTable, name, -amount).ConfigureAwait(false);
                await AppendLedgerTx(tx, LedgerTableOrNull(ledgerTable), sinkLabel, name, -amount, reason).ConfigureAwait(false);
                return new LoyaltyResult(LoyaltyOutcome.Ok, bal - amount, amount);
            });
        }

        /// <summary>Sets an absolute balance (admin override). Rejects value &lt; 0.</summary>
        public Task<LoyaltyResult> LoyaltySetAsync(string balanceTable, string name, long value,
            string? ledgerTable, string sender, string reason)
        {
            if (value < 0) return Task.FromResult(LoyaltyResult.Fail(LoyaltyOutcome.Invalid));
            return MutateAsync(balanceTable, async (tx) =>
            {
                await SetValueTx(tx, balanceTable, name, value).ConfigureAwait(false);
                await AppendLedgerTx(tx, LedgerTableOrNull(ledgerTable), name, sender, value, reason).ConfigureAwait(false);
                return new LoyaltyResult(LoyaltyOutcome.Ok, value, value);
            });
        }

        /// <summary>Atomic peer transfer. NewBalance is the sender's post-move balance.</summary>
        public Task<LoyaltyResult> LoyaltyTransferAsync(string balanceTable, string from, string to, long amount,
            string? ledgerTable, string reason)
        {
            if (amount <= 0) return Task.FromResult(LoyaltyResult.Fail(LoyaltyOutcome.Invalid));
            return MutateAsync(balanceTable, async (tx) =>
            {
                long bal = await ReadBalanceTx(tx, balanceTable, from).ConfigureAwait(false);
                if (bal < amount) return LoyaltyResult.Fail(LoyaltyOutcome.NoFunds, bal);
                await CreditDeltaTx(tx, balanceTable, from, -amount).ConfigureAwait(false);
                await CreditDeltaTx(tx, balanceTable, to, amount).ConfigureAwait(false);
                await AppendLedgerTx(tx, LedgerTableOrNull(ledgerTable), to, from, amount, reason).ConfigureAwait(false);
                return new LoyaltyResult(LoyaltyOutcome.Ok, bal - amount, amount);
            });
        }

        /// <summary>Batch credit (watch-time payout / raffle payout) — all in ONE
        /// transaction / one lock hold. Skips blank names and non-positive amounts.
        /// Ledger rows only when <paramref name="ledgerTable"/> resolves.</summary>
        public async Task<int> LoyaltyCreditManyAsync(string balanceTable, IReadOnlyList<(string Name, long Amount)> credits,
            string? ledgerTable, string sender, string reason)
        {
            if (credits == null || credits.Count == 0) return 0;
            if (!IsValidIdentifier(balanceTable) || IsAppOwnedTable(balanceTable)) return 0;
            string? lt = LedgerTableOrNull(ledgerTable);
            int applied = 0;
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var tx = _connection!.BeginTransaction();
                try
                {
                    foreach (var (n, amt) in credits)
                    {
                        if (string.IsNullOrWhiteSpace(n) || amt <= 0) continue;
                        await CreditDeltaTx(tx, balanceTable, n, amt).ConfigureAwait(false);
                        if (lt != null) await AppendLedgerTx(tx, lt, n, sender, amt, reason).ConfigureAwait(false);
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

        /// <summary>Resets every balance in the table to 0 (Majo's !dbwipe analogue).
        /// Returns the number of rows reset.</summary>
        public async Task<int> LoyaltyWipeAsync(string balanceTable, string? ledgerTable, string byUser)
        {
            if (!IsValidIdentifier(balanceTable) || IsAppOwnedTable(balanceTable)) return 0;
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var tx = _connection!.BeginTransaction();
                try
                {
                    int rows;
                    using (var cmd = new SqliteCommand($"UPDATE [{balanceTable}] SET [currency] = 0", _connection, tx))
                    {
                        cmd.CommandTimeout = CommandTimeoutSeconds;
                        rows = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                    await AppendLedgerTx(tx, LedgerTableOrNull(ledgerTable), "---- WIPE", byUser, 0, "balance wipe").ConfigureAwait(false);
                    tx.Commit();
                    return rows;
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { try { tx.Rollback(); } catch { } return 0; }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
            finally { ReleaseLock(taken); }
        }

        /// <summary>
        /// Folds duplicate wallets together: for each <c>(From, To)</c> pair the From
        /// row's balance is added to the To row (created when absent) and the From row
        /// is removed — all pairs in ONE transaction. Returns one entry per fold that
        /// actually happened, which is what the caller logs / shows as the result.
        ///
        /// <para>WHY IT EXISTS: before the login fix, chat commands keyed the wallet on
        /// the lowercased DISPLAY name while the watch-time sweep keyed it on the LOGIN,
        /// so a viewer whose display name is not simply their login re-cased ended up
        /// with two rows. This is the repair.</para>
        ///
        /// <para>★ OPT-IN ONLY — never call this from a boot/migration path. The balance
        /// table is an OPEN table the streamer owns and their own db.* scripts write to
        /// it; deciding that two differently-named rows are one person is a judgement
        /// only they can make. The caller is expected to PREVIEW the pairs (the balances
        /// are readable with <see cref="LoyaltyGetBalanceAsync"/> /
        /// <see cref="LoyaltyTopAsync"/>) and act on an explicit press.</para>
        ///
        /// <para>Pairs that name the same row (equal under NOCASE), carry a blank side,
        /// or whose From row does not exist are skipped. A negative From balance — only
        /// reachable by hand-editing, the money layer cannot produce one — is folded as
        /// it stands rather than dropped, so the merge is a true sum in every case.</para>
        /// </summary>
        public async Task<List<LoyaltyWalletMerge>> LoyaltyMergeWalletsAsync(
            string balanceTable, IReadOnlyList<(string From, string To)> pairs, string? ledgerTable, string byUser)
        {
            var merged = new List<LoyaltyWalletMerge>();
            if (pairs == null || pairs.Count == 0) return merged;
            if (!IsValidIdentifier(balanceTable) || IsAppOwnedTable(balanceTable)) return merged;
            string? lt = LedgerTableOrNull(ledgerTable);
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var tx = _connection!.BeginTransaction();
                try
                {
                    foreach (var (from, to) in pairs)
                    {
                        string f = (from ?? string.Empty).Trim();
                        string t = (to ?? string.Empty).Trim();
                        if (f.Length == 0 || t.Length == 0) continue;
                        // Equal under NOCASE is the SAME row (every lookup here collates
                        // NOCASE), so "merging" it would delete the wallet outright.
                        if (string.Equals(f, t, StringComparison.OrdinalIgnoreCase)) continue;

                        long fromBal = await ReadBalanceTx(tx, balanceTable, f).ConfigureAwait(false);
                        int removed = await DeleteWalletRowTx(tx, balanceTable, f).ConfigureAwait(false);
                        if (removed == 0) continue;   // no such wallet — nothing to fold
                        if (fromBal != 0)
                            await CreditDeltaTx(tx, balanceTable, t, fromBal).ConfigureAwait(false);
                        long toBal = await ReadBalanceTx(tx, balanceTable, t).ConfigureAwait(false);
                        await AppendLedgerTx(tx, lt, t, f, fromBal, "wallet merge").ConfigureAwait(false);
                        merged.Add(new LoyaltyWalletMerge(f, t, fromBal, toBal));
                    }
                    tx.Commit();
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex))
                {
                    try { tx.Rollback(); } catch { }
                    merged.Clear();
                    return merged;
                }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
            finally { ReleaseLock(taken); }

            // One line per merge (after the lock is released — logging holds no DB lock).
            foreach (var m in merged)
                GlobalLogger.Log(
                    $"Loyalty wallet merge by {byUser}: folded \"{m.FromName}\" ({m.FromBalance}) into \"{m.ToName}\" — new balance {m.ToBalance}.",
                    "Loyalty", LogLevel.System);
            return merged;
        }

        /// <summary>
        /// Atomic house-game settlement (gamble / slots / roulette). Reads the
        /// balance, resolves the stake (%/all against the live balance), validates
        /// (positive, min/max, funds), then applies the net in ONE transaction —
        /// the <paramref name="won"/> roll and <paramref name="grossMultiplier"/>
        /// come from the caller (RNG seam). A loss is a subtraction of a
        /// provably-positive stake, so no path credits on a negative bet.
        /// </summary>
        public Task<LoyaltyBetResult> LoyaltyPlayHouseAsync(string balanceTable, string name, LoyaltyStake stake,
            int minBet, int maxBet, bool won, double grossMultiplier, string? ledgerTable, string reason)
        {
            if (!IsValidIdentifier(balanceTable) || IsAppOwnedTable(balanceTable))
                return Task.FromResult(new LoyaltyBetResult(LoyaltyOutcome.TableMissing, 0, 0, 0));

            return MutateBetAsync(balanceTable, async (tx) =>
            {
                long balance = await ReadBalanceTx(tx, balanceTable, name).ConfigureAwait(false);
                long s = ResolveStake(stake, balance, maxBet);
                if (s <= 0)                   return new LoyaltyBetResult(LoyaltyOutcome.Invalid, 0, 0, balance);
                if (s < minBet)               return new LoyaltyBetResult(LoyaltyOutcome.BelowMin, s, 0, balance);
                if (maxBet > 0 && s > maxBet) return new LoyaltyBetResult(LoyaltyOutcome.AboveMax, s, 0, balance);
                if (s > balance)              return new LoyaltyBetResult(LoyaltyOutcome.NoFunds, s, 0, balance);

                long net = won
                    ? (long)Math.Round(s * grossMultiplier, MidpointRounding.AwayFromZero) - s
                    : -s;
                await CreditDeltaTx(tx, balanceTable, name, net).ConfigureAwait(false);
                long newBal = await ReadBalanceTx(tx, balanceTable, name).ConfigureAwait(false);
                await AppendLedgerTx(tx, LedgerTableOrNull(ledgerTable), name, "house", net, reason).ConfigureAwait(false);
                return new LoyaltyBetResult(LoyaltyOutcome.Ok, s, net, newBal);
            });
        }

        // ── Transaction plumbing + tx-scoped helpers ────────────────────────

        // Runs body inside one transaction under the shared lock, mapping a
        // missing table/column to TableMissing and rolling back cleanly.
        private async Task<LoyaltyResult> MutateAsync(string balanceTable, Func<SqliteTransaction, Task<LoyaltyResult>> body)
        {
            if (!IsValidIdentifier(balanceTable) || IsAppOwnedTable(balanceTable))
                return LoyaltyResult.Fail(LoyaltyOutcome.TableMissing);
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var tx = _connection!.BeginTransaction();
                try
                {
                    var res = await body(tx).ConfigureAwait(false);
                    if (res.Ok) tx.Commit(); else tx.Rollback();
                    return res;
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex))
                {
                    try { tx.Rollback(); } catch { }
                    return LoyaltyResult.Fail(LoyaltyOutcome.TableMissing);
                }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
            finally { ReleaseLock(taken); }
        }

        private async Task<LoyaltyBetResult> MutateBetAsync(string balanceTable, Func<SqliteTransaction, Task<LoyaltyBetResult>> body)
        {
            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                using var tx = _connection!.BeginTransaction();
                try
                {
                    var res = await body(tx).ConfigureAwait(false);
                    if (res.Ok) tx.Commit(); else tx.Rollback();
                    return res;
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex))
                {
                    try { tx.Rollback(); } catch { }
                    return new LoyaltyBetResult(LoyaltyOutcome.TableMissing, 0, 0, 0);
                }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
            finally { ReleaseLock(taken); }
        }

        // Emulated upsert (the open balance table has NO UNIQUE on name, so
        // ON CONFLICT(name) is invalid): rowid-scoped UPDATE, then INSERT only
        // when no row was touched. rowid-scoping means duplicate-name rows in a
        // hand-built table are never double-mutated.
        //
        // ★ COALESCE(CAST(...)) is load-bearing, not defensive dressing — it is the
        // same form DB.Ranks.AddValueTx uses. This table is OPEN, so a row can exist
        // with a NULL (or text) currency cell: db.insert_row naming only the viewer
        // writes no currency column at all, and db.add_column on a populated table
        // NULLs every pre-existing row (ALTER TABLE ADD COLUMN emits no DEFAULT).
        // A bare `[currency] = [currency] + @d` evaluates to NULL for such a row while
        // STILL reporting changes()==1, so the INSERT fallback below never fires, the
        // cell stays NULL forever (CoerceBalance reads it as 0), and every credit is
        // destroyed while the ledger records it as applied. Coercing first is what
        // makes the first credit repair the row instead of losing money in it.
        private async Task CreditDeltaTx(SqliteTransaction tx, string table, string name, long delta)
        {
            int rows;
            using (var up = new SqliteCommand(
                $"UPDATE [{table}] SET [currency] = COALESCE(CAST([currency] AS INTEGER), 0) + @d " +
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
                    $"INSERT INTO [{table}] ([name], [currency]) VALUES (@u, @d)", _connection, tx);
                ins.CommandTimeout = CommandTimeoutSeconds;
                ins.Parameters.AddWithValue("@u", name ?? "");
                ins.Parameters.AddWithValue("@d", delta);
                await ins.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        private async Task SetValueTx(SqliteTransaction tx, string table, string name, long value)
        {
            int rows;
            using (var up = new SqliteCommand(
                $"UPDATE [{table}] SET [currency] = @v " +
                $"WHERE rowid = (SELECT rowid FROM [{table}] WHERE [name] = @u COLLATE NOCASE LIMIT 1)",
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
                    $"INSERT INTO [{table}] ([name], [currency]) VALUES (@u, @v)", _connection, tx);
                ins.CommandTimeout = CommandTimeoutSeconds;
                ins.Parameters.AddWithValue("@u", name ?? "");
                ins.Parameters.AddWithValue("@v", value);
                await ins.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        // Removes ONE wallet row (rowid-scoped, like every other mutation here, so a
        // duplicate-name row in a hand-built table is never collaterally deleted).
        // Returns the rows affected — 0 means there was no such wallet.
        private async Task<int> DeleteWalletRowTx(SqliteTransaction tx, string table, string name)
        {
            using var del = new SqliteCommand(
                $"DELETE FROM [{table}] " +
                $"WHERE rowid = (SELECT rowid FROM [{table}] WHERE [name] = @u COLLATE NOCASE LIMIT 1)",
                _connection, tx);
            del.CommandTimeout = CommandTimeoutSeconds;
            del.Parameters.AddWithValue("@u", name ?? "");
            return await del.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private async Task<long> ReadBalanceTx(SqliteTransaction tx, string table, string name)
        {
            using var bal = new SqliteCommand(
                $"SELECT [currency] FROM [{table}] WHERE [name] = @u COLLATE NOCASE LIMIT 1", _connection, tx);
            bal.CommandTimeout = CommandTimeoutSeconds;
            bal.Parameters.AddWithValue("@u", name ?? "");
            return CoerceBalance(await bal.ExecuteScalarAsync().ConfigureAwait(false));
        }

        private async Task AppendLedgerTx(SqliteTransaction tx, string? ledgerTable,
            string recipient, string sender, long amount, string reason)
        {
            if (string.IsNullOrEmpty(ledgerTable)) return;
            using var ins = new SqliteCommand(
                $"INSERT INTO [{ledgerTable}] ([ts], [recipient], [sender], [amount], [reason]) " +
                "VALUES (@t, @r, @s, @a, @z)", _connection, tx);
            ins.CommandTimeout = CommandTimeoutSeconds;
            ins.Parameters.AddWithValue("@t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            ins.Parameters.AddWithValue("@r", recipient ?? "");
            ins.Parameters.AddWithValue("@s", sender ?? "");
            ins.Parameters.AddWithValue("@a", amount);
            ins.Parameters.AddWithValue("@z", reason ?? "");
            await ins.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static long ResolveStake(LoyaltyStake stake, long balance, int maxBet)
        {
            switch (stake.Kind)
            {
                case LoyaltyStakeKind.All:
                    long cap = maxBet > 0 ? Math.Min(balance, maxBet) : balance;
                    return Math.Max(0, cap);
                case LoyaltyStakeKind.Percent:
                    double p = Math.Clamp(stake.Percent, 0, 1);
                    return Math.Max(0, (long)Math.Round(balance * p, MidpointRounding.AwayFromZero));
                default:
                    return stake.Amount;
            }
        }

        private static string? LedgerTableOrNull(string? t)
            => (!string.IsNullOrEmpty(t) && IsValidIdentifier(t) && !IsAppOwnedTable(t)) ? t : null;
    }
}
