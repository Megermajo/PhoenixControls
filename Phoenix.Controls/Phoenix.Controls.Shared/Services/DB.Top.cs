using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Phoenix.Controls.Shared.Services
{
    // db.top — the generic top-N leaderboard read over any OPEN user table, added in
    // the 2026-08 tool-node cut as the generic replacement for the retired per-tool
    // leaderboard reads (points.top over the Loyalty balance table, rank.top over the
    // WatchTime/balance tables). Direct clone of DB.Ranks.RankTopAsync with the label
    // column made a parameter.
    //
    // Guard posture after the 2026-08 unlock: the hard reject is now scoped to the
    // WRITE-LOCKED (security) tables only — PairedDevices holds token hashes and
    // RemoteAuditLog is evidence, so neither belongs in a leaderboard. Every other
    // table is fair game, which is the point: a streamer can now run
    // db.top("GiveawayTickets", "Tickets", "Username", 5) or board their Counters
    // exactly as they would board their own table. The old blanket system-table
    // reject was inherited from RankTopAsync and blocked 23 tables no read could
    // harm, while the sibling reads (db.get_cell / db.find_row / db.get_column)
    // never blocked any of them — so it wasn't even protecting a boundary.
    //
    // Values come back as the RAW cell text (trimmed of nothing) rather than a
    // coerced long: an open table can legitimately hold decimals, and the numeric
    // coercion this read needs is confined to the ORDER BY (CAST AS REAL — SQLite's
    // storage-class order puts TEXT above INTEGER, so a db.*-written text cell would
    // otherwise head the board; REAL rather than INTEGER so "12.5" outranks "12").
    public partial class DB
    {
        /// <summary>Top-N rows of a table ordered numerically DESC by
        /// <paramref name="valueColumn"/>. Returns (label, raw value text) pairs,
        /// highest first; empty on an invalid/write-locked table, an unknown column, or a
        /// non-positive count — the standard degrade-to-empty db.* read posture.</summary>
        public async Task<List<(string Label, string Value)>> TopAsync(
            string table, string valueColumn, string labelColumn, int n)
        {
            var list = new List<(string, string)>();
            if (!IsValidIdentifier(table)
                || !IsValidIdentifier(valueColumn) || !IsValidIdentifier(labelColumn)
                || n <= 0)
                return list;
            // The one db.* read with a table guard, and it stays honest: an empty
            // board is indistinguishable from "table empty", so say so in the log.
            if (WriteLockReason(table) is { } lockReason)
            {
                LogWriteRefusal("TOP-N read", table, lockReason);
                return list;
            }

            bool taken = await AcquireLockAsync().ConfigureAwait(false);
            try
            {
                EnsureConnected();
                try
                {
                    using var cmd = new SqliteCommand(
                        $"SELECT [{labelColumn}], [{valueColumn}] FROM [{table}] " +
                        $"WHERE [{labelColumn}] IS NOT NULL AND [{labelColumn}] <> '' " +
                        $"ORDER BY CAST([{valueColumn}] AS REAL) DESC LIMIT @lim", _connection);
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    cmd.Parameters.AddWithValue("@lim", n);
                    using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                    while (await r.ReadAsync().ConfigureAwait(false))
                    {
                        // INVARIANT stringify, never a bare ToString(): a REAL-affinity
                        // cell arrives as a boxed double, and the current culture on a
                        // de/fr machine renders 12.5 as "12,5" — which both misprints the
                        // value and injects a phantom entry into the comma-joined Values
                        // CSV the handler builds (the adversarial review's catch; the
                        // retired rank.top never hit this because CoerceBalance flattened
                        // everything to long).
                        list.Add((
                            InvariantCellText(r.IsDBNull(0) ? null : r.GetValue(0)),
                            InvariantCellText(r.IsDBNull(1) ? null : r.GetValue(1))));
                    }
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return list; }
            }
            finally { ReleaseLock(taken); }
            return list;
        }

        // Culture-proof cell → text. Microsoft.Data.Sqlite boxes per storage class
        // (long / double / string / byte[]), so the two numeric shapes get explicit
        // invariant formatting and everything else rides Convert.ToString with the
        // invariant provider — the same posture CoerceBalance's fallback takes.
        private static string InvariantCellText(object? v) => v switch
        {
            null => "",
            double d => d.ToString(CultureInfo.InvariantCulture),
            float f => f.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
        };
    }
}
