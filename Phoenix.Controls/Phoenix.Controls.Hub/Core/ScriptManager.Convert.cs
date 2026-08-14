using System.Globalization;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: convert.* command registrations.
    // Lifts the 4 type-coercion handlers (to_int / to_string / to_bool /
    // to_float) out of RegisterHubCommands. R19 sweep-15 single-string-input
    // shape is preserved (the binder doesn't coerce: scripts always pass raw
    // strings, and these handlers carry the richer clamping / truthy /
    // locale-safe parsing).
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        private void RegisterConvertCommands()
        {
            _engine.RegisterCommand("convert.to_int", async (args) => {
                // H7 — node description says "Returns 0 if parsing fails", so honor it.
                // Returning null was breaking downstream Math.Add etc. that expected a
                // numeric string.
                string value = _engine.CurrentBoundArgs?.GetOrDefault<string>("Value", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    return ((int)v).ToString(CultureInfo.InvariantCulture);
                return "0";
            });
            _engine.RegisterCommand("convert.to_string", async (args) => {
                string value = _engine.CurrentBoundArgs?.GetOrDefault<string>("Value", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                return value;
            });
            _engine.RegisterCommand("convert.to_bool", async (args) => {
                // L7 — share the truthy logic with the engine's inline wrapper
                // so a script using `if convert.to_bool({x}):` and
                // `result = convert.to_bool({x})` agree on edge cases.
                string value = _engine.CurrentBoundArgs?.GetOrDefault<string>("Value", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                return (ScriptEngine.ParseTruthy(value) ? "true" : "false");
            });
            _engine.RegisterCommand("convert.to_float", async (args) => {
                string value = _engine.CurrentBoundArgs?.GetOrDefault<string>("Value", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v))
                    return v.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return "0.0";
            });
        }
    }
#pragma warning restore CS1998
}
