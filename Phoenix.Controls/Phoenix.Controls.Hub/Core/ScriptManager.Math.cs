using System;
using System.Globalization;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: math.* command registrations.
    // Lifts the 13 pure-data math handlers (random/chance/add/subtract/
    // multiply/clamp/divide/modulo/abs/min/max/floor/ceil) out of the
    // RegisterHubCommands body. They live across two non-contiguous source
    // regions in the original file (math.random was at the top of the bus
    // setup, the rest were in a single block); collapsing them all into
    // one partial method is safe because RegisterCommand stores by name —
    // registration order has no runtime effect.
    //
    // Behaviour-preserving carry-overs:
    //   * math.chance routes through RNG.Chance(double)
    //     so fractional rates and the full 0..100 inclusive range work.
    //   * Typed pulls (float for chance, double for the
    //     binary ops, int for modulo) with String fallback for direct
    //     InvokeCommandAsync test paths.
    //   * Returning null on parse-fail / divide-by-zero preserves the
    //     "graph branch goes nowhere on bad input" semantic.
    //
    // Every double-returning math handler now funnels
    // through NumericGuards.Sanitize before stringifying. Previously
    // math.divide(0,0), math.add(double.MaxValue, double.MaxValue), etc.
    // returned the literal "NaN" / "Infinity" — which poisons every
    // downstream string-to-double TryParse (NaN parses successfully and
    // every comparison vs NaN is false, so guards like `< limit` go quiet
    // wrong instead of loud wrong). Sanitize logs once and returns null,
    // which the engine already treats as "branch goes nowhere on bad input."
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        private void RegisterMathCommands()
        {
            // math.random(min, max) — returns a random integer in [min, max]
            _engine.RegisterCommand("math.random", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                int? min = (bound != null && bound.ContainsKey("Min"))
                    ? bound.Get<int>("Min")
                    : (int.TryParse(ArgOrEmpty(args, 0), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mn) ? mn : (int?)null);
                int? max = (bound != null && bound.ContainsKey("Max"))
                    ? bound.Get<int>("Max")
                    : (int.TryParse(ArgOrEmpty(args, 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mx) ? mx : (int?)null);
                if (min is null || max is null) return null;
                return RNG.Next(min.Value, max.Value).ToString(CultureInfo.InvariantCulture);
            });

            // math.chance(percent[, resultVar]) — rolls a percentage and writes
            // the boolean outcome to the named global. resultVar defaults to
            // "global.chance_result" for backwards compatibility with older
            // exports; the new exporter passes a per-node global so two
            // Math.Chance nodes can't clobber each other.
            _engine.RegisterCommand("math.chance", async (args) => {
                // H1 / H2 — node template declares % Rate as Float, but the previous
                // runtime gated on int.TryParse and `Next(1,100) <= percent` (which
                // could never return true for percent=100 since Next is exclusive on
                // the upper bound). Route through RNG.Chance(double) so
                // both fractional rates and the full 0..100 range work.
                //
                // Typed Float reference. Empty/missing percent
                // coerces to 0.0 (binder default), which means RNG.Chance
                // returns false — matches the legacy "no roll" outcome for missing
                // arg (the old code skipped the whole branch on TryParse failure;
                // returning false instead is closer to the user's mental model:
                // "with no chance set, never succeed"). ResultVar empty falls back
                // to "global.chance_result" for legacy graphs that omit it.
                var bound = _engine.CurrentBoundArgs;
                float percent = (bound != null && bound.ContainsKey("Percent"))
                    ? bound.Get<float>("Percent")
                    : (float.TryParse(ArgOrEmpty(args, 0), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 0f);
                string resultVarRaw = bound?.GetOrDefault<string>("ResultVar", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string targetVar = string.IsNullOrWhiteSpace(resultVarRaw) ? "global.chance_result" : resultVarRaw.Trim();

                bool success = RNG.Chance(percent);
                await _engine.SetScriptVarAsync(targetVar, success.ToString().ToLower());
                return null;
            });

            // math.add/subtract/multiply/clamp — pure computation, return result.
            // Typed Double pulls; legacy `double.TryParse` retained as
            // the bound-is-null fallback for direct InvokeCommandAsync test paths.
            // Returning null when either arg fails to parse preserves the original
            // "graph branch goes nowhere on bad input" semantic.
            _engine.RegisterCommand("math.add", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                double? a = (bound != null && bound.ContainsKey("A"))
                    ? bound.Get<double>("A")
                    : (double.TryParse(ArgOrEmpty(args, 0), NumberStyles.Float, CultureInfo.InvariantCulture, out var ax) ? ax : (double?)null);
                double? b = (bound != null && bound.ContainsKey("B"))
                    ? bound.Get<double>("B")
                    : (double.TryParse(ArgOrEmpty(args, 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var bx) ? bx : (double?)null);
                if (a is null || b is null) return null;
                return NumericGuards.Sanitize(a.Value + b.Value, "math.add");
            });

            _engine.RegisterCommand("math.subtract", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                double? a = (bound != null && bound.ContainsKey("A"))
                    ? bound.Get<double>("A")
                    : (double.TryParse(ArgOrEmpty(args, 0), NumberStyles.Float, CultureInfo.InvariantCulture, out var ax) ? ax : (double?)null);
                double? b = (bound != null && bound.ContainsKey("B"))
                    ? bound.Get<double>("B")
                    : (double.TryParse(ArgOrEmpty(args, 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var bx) ? bx : (double?)null);
                if (a is null || b is null) return null;
                return NumericGuards.Sanitize(a.Value - b.Value, "math.subtract");
            });

            _engine.RegisterCommand("math.multiply", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                double? a = (bound != null && bound.ContainsKey("A"))
                    ? bound.Get<double>("A")
                    : (double.TryParse(ArgOrEmpty(args, 0), NumberStyles.Float, CultureInfo.InvariantCulture, out var ax) ? ax : (double?)null);
                double? b = (bound != null && bound.ContainsKey("B"))
                    ? bound.Get<double>("B")
                    : (double.TryParse(ArgOrEmpty(args, 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var bx) ? bx : (double?)null);
                if (a is null || b is null) return null;
                return NumericGuards.Sanitize(a.Value * b.Value, "math.multiply");
            });

            _engine.RegisterCommand("math.clamp", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                double? v   = (bound != null && bound.ContainsKey("Value"))
                    ? bound.Get<double>("Value")
                    : (double.TryParse(ArgOrEmpty(args, 0), NumberStyles.Float, CultureInfo.InvariantCulture, out var vx) ? vx : (double?)null);
                double? min = (bound != null && bound.ContainsKey("Min"))
                    ? bound.Get<double>("Min")
                    : (double.TryParse(ArgOrEmpty(args, 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var mn) ? mn : (double?)null);
                double? max = (bound != null && bound.ContainsKey("Max"))
                    ? bound.Get<double>("Max")
                    : (double.TryParse(ArgOrEmpty(args, 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var mx) ? mx : (double?)null);
                if (v is null || min is null || max is null) return null;
                // Math.Clamp itself propagates NaN cleanly (NaN
                // clamps to NaN), so sanitize after the call. A NaN Min/Max
                // makes the comparison undefined; the sanitize layer rejects
                // either way.
                return NumericGuards.Sanitize(Math.Clamp(v.Value, min.Value, max.Value), "math.clamp");
            });

            // ── Math ops — pure computation, return result ─────────────────
            _engine.RegisterCommand("math.divide", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                double? a = (bound != null && bound.ContainsKey("A"))
                    ? bound.Get<double>("A")
                    : (double.TryParse(ArgOrEmpty(args, 0), NumberStyles.Float, CultureInfo.InvariantCulture, out var ax) ? ax : (double?)null);
                double? b = (bound != null && bound.ContainsKey("B"))
                    ? bound.Get<double>("B")
                    : (double.TryParse(ArgOrEmpty(args, 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var bx) ? bx : (double?)null);
                if (a is null || b is null || b.Value == 0d) return null;
                // Divide-by-zero is already caught above, but
                // 0.0 / -0.0, MaxValue / MinValue * sign-flip, and divides
                // that overflow to Infinity still slip through. Sanitize
                // catches all of those.
                return NumericGuards.Sanitize(a.Value / b.Value, "math.divide");
            });
            _engine.RegisterCommand("math.modulo", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                int? a = (bound != null && bound.ContainsKey("A"))
                    ? bound.Get<int>("A")
                    : (int.TryParse(ArgOrEmpty(args, 0), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ax) ? ax : (int?)null);
                int? b = (bound != null && bound.ContainsKey("B"))
                    ? bound.Get<int>("B")
                    : (int.TryParse(ArgOrEmpty(args, 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bx) ? bx : (int?)null);
                if (a is null || b is null || b.Value == 0) return null;
                // Integer modulo can't produce NaN/Infinity — no sanitize needed.
                return (a.Value % b.Value).ToString(CultureInfo.InvariantCulture);
            });
            _engine.RegisterCommand("math.abs", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                double? v = (bound != null && bound.ContainsKey("Value"))
                    ? bound.Get<double>("Value")
                    : (double.TryParse(ArgOrEmpty(args, 0), NumberStyles.Float, CultureInfo.InvariantCulture, out var vx) ? vx : (double?)null);
                if (v is null) return null;
                return NumericGuards.Sanitize(Math.Abs(v.Value), "math.abs");
            });
            _engine.RegisterCommand("math.min", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                double? a = (bound != null && bound.ContainsKey("A"))
                    ? bound.Get<double>("A")
                    : (double.TryParse(ArgOrEmpty(args, 0), NumberStyles.Float, CultureInfo.InvariantCulture, out var ax) ? ax : (double?)null);
                double? b = (bound != null && bound.ContainsKey("B"))
                    ? bound.Get<double>("B")
                    : (double.TryParse(ArgOrEmpty(args, 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var bx) ? bx : (double?)null);
                if (a is null || b is null) return null;
                return NumericGuards.Sanitize(Math.Min(a.Value, b.Value), "math.min");
            });
            _engine.RegisterCommand("math.max", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                double? a = (bound != null && bound.ContainsKey("A"))
                    ? bound.Get<double>("A")
                    : (double.TryParse(ArgOrEmpty(args, 0), NumberStyles.Float, CultureInfo.InvariantCulture, out var ax) ? ax : (double?)null);
                double? b = (bound != null && bound.ContainsKey("B"))
                    ? bound.Get<double>("B")
                    : (double.TryParse(ArgOrEmpty(args, 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var bx) ? bx : (double?)null);
                if (a is null || b is null) return null;
                return NumericGuards.Sanitize(Math.Max(a.Value, b.Value), "math.max");
            });
            _engine.RegisterCommand("math.floor", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                double? v = (bound != null && bound.ContainsKey("Value"))
                    ? bound.Get<double>("Value")
                    : (double.TryParse(ArgOrEmpty(args, 0), NumberStyles.Float, CultureInfo.InvariantCulture, out var vx) ? vx : (double?)null);
                if (v is null) return null;
                return NumericGuards.Sanitize(Math.Floor(v.Value), "math.floor");
            });
            _engine.RegisterCommand("math.ceil", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                double? v = (bound != null && bound.ContainsKey("Value"))
                    ? bound.Get<double>("Value")
                    : (double.TryParse(ArgOrEmpty(args, 0), NumberStyles.Float, CultureInfo.InvariantCulture, out var vx) ? vx : (double?)null);
                if (v is null) return null;
                return NumericGuards.Sanitize(Math.Ceiling(v.Value), "math.ceil");
            });
        }
    }
#pragma warning restore CS1998
}
