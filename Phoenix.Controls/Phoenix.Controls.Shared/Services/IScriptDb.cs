using System.Collections.Generic;
using System.Threading.Tasks;

namespace Phoenix.Controls.Shared.Services
{
    // R9 — DI seam for the script engine's persistence dependency.
    // DB implements this in production; tests can swap a
    // lightweight in-memory implementation without spinning up SQLite.
    // Surface is intentionally narrow: only the var-store methods the
    // engine actually calls. Other consumers of DB (logging, tables,
    // snapshots) keep using the singleton directly.
    public interface IScriptDb
    {
        Task<string> GetVariableAsync(string key, string defaultValue = "");
        Task SetVariableAsync(string key, string value);

        // Batch read — used by ScriptEngine's per-execution variable
        // preload to fold N+1 round-trips into a single query. The default
        // (interface) implementation falls back to per-key GetVariableAsync
        // so existing IScriptDb implementations keep working without code
        // changes; production DB overrides for the SQLite WHERE-IN path.
        // Returns a dictionary containing only the keys whose backing row
        // exists (missing keys are absent, NOT null/empty entries) so
        // callers can distinguish "row absent" from "row stored as empty".
        async Task<IDictionary<string, string>> GetVariablesAsync(IEnumerable<string> keys)
        {
            var result = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
            {
                string val = await GetVariableAsync(key, "").ConfigureAwait(false);
                if (!string.IsNullOrEmpty(val))
                    result[key] = val;
            }
            return result;
        }
    }
}
