using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: db.* command registrations.
    // Lifts the 13 DB-backed handlers (find_row, get/set/increment_cell,
    // insert/delete_row, clear_table, get_column, delete_var, row_count, check,
    // fetch_row) out of RegisterHubCommands. Behaviour, contracts, and the
    // result.* writes are unchanged.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        private void RegisterDbCommands()
        {
            // M24 — strip surrounding quotes from the inputs (function-call expressions
            // pass through as bare values; literal strings come quoted). Numeric search
            // values are detected and round-tripped through long.ToString so SQLite's
            // type-affinity rules treat them as INTEGER candidates rather than as the
            // raw quoted form. This is a runtime-side hardening: even if an exporter
            // emits a quoted numeric like `"5"`, FindRow still matches an INTEGER 5
            // column. Non-numeric values pass through as TEXT.
            _engine.RegisterCommand("db.find_row", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string tableName   = StripBareQuotes(bound?.GetOrDefault<string>("Table", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                string columnName  = StripBareQuotes(bound?.GetOrDefault<string>("Column", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1));
                string searchValue = StripBareQuotes(bound?.GetOrDefault<string>("SearchValue", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2));
                string resultKey   = bound?.GetOrDefault<string>("ResultKey", ArgOrEmpty(args, 3)) ?? ArgOrEmpty(args, 3);
                if (string.IsNullOrEmpty(tableName) || string.IsNullOrEmpty(columnName) || string.IsNullOrEmpty(resultKey))
                    return null;
                if (long.TryParse(searchValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long asInt))
                    searchValue = asInt.ToString(CultureInfo.InvariantCulture);
                long? rowId = await DB.Instance.FindRowAsync(tableName, columnName, searchValue);
                _engine.SetLocalResultVar(resultKey, rowId.HasValue ? rowId.Value.ToString() : "");
                return null;
            });

            _engine.RegisterCommand("db.get_cell", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string table  = bound?.GetOrDefault<string>("Table", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                long   rowId  = (bound != null && bound.ContainsKey("RowId"))
                    ? bound.Get<int>("RowId")
                    : (long.TryParse(ArgOrEmpty(args, 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : 0L);
                string column = bound?.GetOrDefault<string>("Column", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                if (string.IsNullOrEmpty(table) || rowId == 0L || string.IsNullOrEmpty(column)) return "";
                return await DB.Instance.GetCellAsync(table, rowId, column);
            });

            // R19 (sweep 14) — typed mixed-type reference (String + Int + String + String).
            // RowId is bound as ArgType.Int (32-bit ceiling); SQLite ROWIDs are 64-bit
            // but practical row counts on a streamer's local DB stay well under 2^31.
            // Promote the manifest to long once a real overflow case arrives.
            _engine.RegisterCommand("db.set_cell", async (args) => {
                var bound  = _engine.CurrentBoundArgs;
                string table  = bound?.GetOrDefault<string>("Table", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                long   rowId  = (bound != null && bound.ContainsKey("RowId"))
                    ? bound.Get<int>("RowId")
                    : (long.TryParse(ArgOrEmpty(args, 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : 0L);
                string column = bound?.GetOrDefault<string>("Column", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                string value  = bound?.GetOrDefault<string>("Value", ArgOrEmpty(args, 3)) ?? ArgOrEmpty(args, 3);
                if (string.IsNullOrEmpty(table) || rowId == 0L || string.IsNullOrEmpty(column)) return null;
                await DB.Instance.SetCellAsync(table, rowId, column, value);
                return null;
            });

            _engine.RegisterCommand("db.increment_cell", async (args) => {
                // args: table, rowId, col, amount (Optional Default 1)
                var bound = _engine.CurrentBoundArgs;
                string table  = bound?.GetOrDefault<string>("Table", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                long   rowId  = (bound != null && bound.ContainsKey("RowId"))
                    ? bound.Get<int>("RowId")
                    : (long.TryParse(ArgOrEmpty(args, 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : 0L);
                string column = bound?.GetOrDefault<string>("Column", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                int    amt;
                if (bound != null && bound.ContainsKey("Amount")) amt = bound.Get<int>("Amount");
                else
                {
                    string amtRaw = ArgOrEmpty(args, 3);
                    amt = string.IsNullOrEmpty(amtRaw) ? 1
                        : (int.TryParse(amtRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) ? a : 0);
                }
                if (string.IsNullOrEmpty(table) || rowId == 0L || string.IsNullOrEmpty(column))
                    return null;

                // H34 — Get→Set is non-atomic; concurrent chat scripts racing on the
                // same row would lose increments. Per-cell lock keyed by table/row/col
                // collapses concurrent writers without blocking other cells.
                string lockKey = $"db.cell::{table}::{rowId}::{column}";
                var rmwLock = GetRmwLock(lockKey);
                await rmwLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    string currentStr = await DB.Instance.GetCellAsync(table, rowId, column);
                    int.TryParse(currentStr, out int current);
                    int newValue = current + amt;
                    await DB.Instance.SetCellAsync(table, rowId, column, newValue.ToString(CultureInfo.InvariantCulture));
                    return newValue.ToString(CultureInfo.InvariantCulture);
                }
                finally { rmwLock.Release(); }
            });

            // db.insert_row: Table + variadic positional col,val,col,val,...,resultVar.
            // Sweep 16 — added to manifest with Variadic String. The handler still does
            // its own parity-detection logic on the rest list (the script grammar pairs
            // by POSITION, not by `=`, so KvPairs doesn't fit); the binder just gives us
            // the table arg and the rest list typed.
            _engine.RegisterCommand("db.insert_row", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string table = bound?.GetOrDefault<string>("Table", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                IReadOnlyList<string> pairs;
                if (bound != null)
                    pairs = bound.GetOrDefault<IReadOnlyList<string>>("Pairs", Array.Empty<string>());
                else
                {
                    var rest = new string[Math.Max(0, args.Length - 1)];
                    for (int j = 1; j < args.Length; j++) rest[j - 1] = args[j];
                    pairs = rest;
                }
                if (string.IsNullOrEmpty(table) || pairs.Count < 2) return null;

                bool hasResultVar = pairs.Count % 2 == 1;  // odd rest count → trailing resultVar
                int pairEnd = hasResultVar ? pairs.Count - 1 : pairs.Count;
                string resultVar = hasResultVar ? pairs[pairEnd] : "";
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i + 1 < pairEnd; i += 2)
                    values[pairs[i]] = pairs[i + 1];
                long newRowId = await DB.Instance.InsertUserRowAsync(table, values);
                if (!string.IsNullOrWhiteSpace(resultVar))
                    _engine.SetLocalResultVar(resultVar, newRowId.ToString(CultureInfo.InvariantCulture));
                return newRowId.ToString(CultureInfo.InvariantCulture);
            });

            _engine.RegisterCommand("db.delete_row", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string tableName = bound?.GetOrDefault<string>("Table", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                long   rowId     = (bound != null && bound.ContainsKey("RowId"))
                    ? bound.Get<int>("RowId")
                    : (long.TryParse(ArgOrEmpty(args, 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : 0L);
                if (string.IsNullOrEmpty(tableName) || rowId == 0L) return null;
                await DB.Instance.DeleteRowAsync(tableName, rowId);
                return null;
            });

            // R19 (sweep 15) — typed-bind migration for the simple DB family.
            _engine.RegisterCommand("db.clear_table", async (args) => {
                string tableName = _engine.CurrentBoundArgs?.GetOrDefault<string>("TableName", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(tableName)) return null;
                await DB.Instance.ClearTableAsync(tableName);
                return null;
            });

            _engine.RegisterCommand("db.get_column", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string table  = bound?.GetOrDefault<string>("Table", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string column = bound?.GetOrDefault<string>("Column", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(column)) return "";
                var values = await DB.Instance.GetColumnValuesAsync(table, column);
                return string.Join(",", values);
            });

            _engine.RegisterCommand("db.delete_var", async (args) => {
                string key = _engine.CurrentBoundArgs?.GetOrDefault<string>("Key", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(key)) return null;
                await DB.Instance.DeleteVariableAsync(key);
                return null;
            });

            _engine.RegisterCommand("db.row_count", async (args) => {
                string table = _engine.CurrentBoundArgs?.GetOrDefault<string>("Table", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(table)) return "0";
                int count = await DB.Instance.GetRowCountAsync(table);
                return count.ToString();
            });

            // db.check(key) — exists check for Vars. Returns "true"/"false".
            // Uses a per-call sentinel as the default so an empty-string stored value is
            // distinguished from a missing key.
            _engine.RegisterCommand("db.check", async (args) => {
                string key = _engine.CurrentBoundArgs?.GetOrDefault<string>("Key", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(key)) return "false";
                string sentinel = "\x1f__missing__\x1f" + Guid.NewGuid().ToString("N");
                string val = await DB.Instance.GetVariableAsync(key, sentinel);
                return (val == sentinel) ? "false" : "true";
            });

            _engine.RegisterCommand("db.fetch_row", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string table     = bound?.GetOrDefault<string>("Table", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                long   rowId     = (bound != null && bound.ContainsKey("RowId"))
                    ? bound.Get<int>("RowId")
                    : (long.TryParse(ArgOrEmpty(args, 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : 0L);
                string resultKey = bound?.GetOrDefault<string>("ResultKey", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                if (string.IsNullOrEmpty(table) || rowId == 0L || string.IsNullOrEmpty(resultKey))
                    return null;
                var row = await DB.Instance.FetchRowByIdAsync(table, rowId);
                if (row == null)
                {
                    // H37 — clear all per-column writes from any previous successful fetch
                    // under this resultKey. Otherwise scripts that fetch user 5 (success),
                    // then user 6 (miss), then read {Row.points} keep seeing user 5's points.
                    _engine.SetLocalResultVar(resultKey, "");
                    foreach (var col in await DB.Instance.GetTableColumnsAsync(table))
                        _engine.SetLocalResultVar($"{resultKey}.{col}", "");
                    // GetTableColumnsAsync filters out "rowid", so clear it explicitly —
                    // otherwise a miss after a prior successful fetch leaves a stale
                    // {Row.rowid} behind (mirrors the H37 per-column clear above).
                    _engine.SetLocalResultVar($"{resultKey}.rowid", "");
                    return null;
                }
                // FetchRowByIdAsync stamps a deterministic "rowid" key, so this loop
                // exposes {Row.rowid} alongside every column for the fetched row.
                foreach (var kv in row)
                    _engine.SetLocalResultVar($"{resultKey}.{kv.Key}", kv.Value);
                _engine.SetLocalResultVar(resultKey, "found");
                return null;
            });
        }
    }
#pragma warning restore CS1998
}
