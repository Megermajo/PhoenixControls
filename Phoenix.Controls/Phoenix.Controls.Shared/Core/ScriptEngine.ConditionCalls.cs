// ScriptEngine partial — pre-execution of command CALLS inside if/elif/while
// conditions.
//
// EvaluateCondition/EvalSingle are synchronous parsers: they can compare,
// negate, test membership and truthiness, but they cannot EXECUTE a command.
// The exporter, however, legitimately emits registered-command calls directly
// into condition position — `if text.contains({x}, %):` (Logic.Branch),
// `if cooldown.check(...)` (Flow.Cooldown), `if db.check(...)`
// (DB.CheckExists), `while_loop(some.call(...))` — and without this pass every
// one of those conditions fell through EvalSingle's literal-truthy check and
// evaluated FALSE forever (the string "text.contains(...)" is never "true").
//
// This pass runs in the async line pipeline BEFORE the sync evaluator: it
// walks the condition's STRUCTURE on the un-substituted template (the same
// parse-first contract as the cond-not-injection fix — a value arriving via
// {var} substitution can never become code because substitution happens later,
// inside EvalSingle's leaves), finds operands that are literal call shapes of
// REGISTERED commands, executes them via the same ExecuteCommandWithResult
// path command lines use, and splices the result back as a {__cond_call_N}
// temp-var reference resolved at the leaf. The temp-var splice (rather than a
// quoted literal) is load-bearing: a result containing quotes/operators/" and "
// would otherwise re-enter the quote/operator scanners and distort the
// condition's STRUCTURE — result text must stay data.
//
// Gates, in order, for an operand to execute:
//   1. it is authored in the script SOURCE (template text, pre-substitution);
//   2. it fully matches the `name(args)` command shape (CommandParseRegex);
//   3. the callee is NOT a convert.to_* wrapper (EvalSingle evaluates those
//      natively, synchronously — keep that path authoritative) and NOT a
//      .startswith/.endswith receiver method (TryEvalStringMethod owns those);
//   4. the callee is a REGISTERED command. Unregistered shapes (e.g.
//      `msg.startswith("!x")`, plain parenthesised text) pass through
//      untouched for the sync evaluator to handle.
//
// while_loop re-runs this pass every iteration, so a condition like
// `while_loop(cooldown.check(...))` re-executes the call per iteration —
// evaluate-once hoisting at export time could not provide that.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Phoenix.Controls.Shared.Core
{
    public partial class ScriptEngine
    {
        // Hoisted to avoid re-allocating the operator/separator arrays on every
        // condition evaluation. Element ORDER is load-bearing:
        //  - ComparisonOps: the earliest-index search picks the first-listed
        //    operator on ties, so "!=" must precede "=", ">=" before ">", etc.
        //  - BoolSeparators: " and " before " or " mirrors precedence.
        private static readonly string[] ComparisonOps = { "!=", ">=", "<=", "==", ">", "<" };
        private static readonly string[] BoolSeparators = { " and ", " or " };

        /// <summary>
        /// Rewrite an `if ...:` / `elif ...:` line (or a while_loop-synthesized
        /// equivalent) so that registered-command call shapes authored in the
        /// condition are executed and replaced by their quoted results.
        /// Returns the line unchanged when there is nothing to execute.
        /// </summary>
        internal async Task<string> PreExecuteConditionCallsAsync(string line, Dictionary<string, string> vars)
        {
            // Fast gate: no parens → no call shape anywhere.
            if (line.IndexOf('(') < 0) return line;

            var m = IfElifPrefixRegex.Match(line);
            if (!m.Success || !line.EndsWith(":")) return line;

            string prefix = m.Value;
            string expr   = line.Substring(prefix.Length, line.Length - prefix.Length - 1).Trim();
            if (expr.Length == 0) return line;

            var ctx = new ConditionCallContext();
            string rewritten = await PreExecuteConditionExprAsync(expr, vars, ctx).ConfigureAwait(false);
            return rewritten == expr ? line : prefix + rewritten + ":";
        }

        // Per-condition-evaluation state: temp-var counter plus an operand
        // cache so the SAME call text appearing in several comparison legs
        // (Flow.IsValid emits one resolved input into six comparisons)
        // executes exactly once per evaluation.
        private sealed class ConditionCallContext
        {
            internal int Counter;
            internal readonly Dictionary<string, string> OperandKeys = new(StringComparer.Ordinal);
        }

        // Mirror EvaluateCondition's structural precedence exactly: " and "
        // wins over " or "; each part is then a single leaf for EvalSingle
        // (which does not re-split). Chains are evaluated HERE, part by part,
        // with SHORT-CIRCUIT parity to EvaluateCondition's .All/.Any — a
        // side-effecting call guarded behind an earlier decisive part
        // (`if {ok} == "yes" and cooldown.check(...)`) must never execute
        // when the guard already settled the outcome. The chain collapses to
        // a literal "true"/"false" so the sync evaluator sees the computed
        // result and never re-orders the work.
        private async Task<string> PreExecuteConditionExprAsync(string expr, Dictionary<string, string> vars, ConditionCallContext ctx)
        {
            foreach (var sep in BoolSeparators)
            {
                if (IndexOfOutsideQuotes(expr, sep) < 0) continue;
                bool isAnd = sep == " and ";
                foreach (var rawPart in SplitOutsideQuotes(expr, sep))
                {
                    string part  = rawPart.Trim();
                    string done  = await PreExecuteConditionLeafAsync(part, vars, ctx).ConfigureAwait(false);
                    bool   truth = EvalSingle(done, vars);
                    if (isAnd && !truth) return "false";
                    if (!isAnd && truth) return "true";
                }
                return isAnd ? "true" : "false";
            }
            string leaf = await PreExecuteConditionLeafAsync(expr, vars, ctx).ConfigureAwait(false);
            return leaf;
        }

        private async Task<string> PreExecuteConditionLeafAsync(string leaf, Dictionary<string, string> vars, ConditionCallContext ctx)
        {
            string t = leaf.Trim();
            if (t.Length == 0 || t.IndexOf('(') < 0) return leaf;

            // Structural peel order mirrors EvalSingle: parens, then `not `.
            if (t.Length >= 2 && t.StartsWith("(") && t.EndsWith(")") && IsBalanced(t[1..^1]))
            {
                string inner = t[1..^1].Trim();
                string done  = await PreExecuteConditionLeafAsync(inner, vars, ctx).ConfigureAwait(false);
                return done == inner ? leaf : "(" + done + ")";
            }

            if (t.StartsWith("not ", StringComparison.OrdinalIgnoreCase))
            {
                string inner = t.Substring(4).Trim();
                string done  = await PreExecuteConditionLeafAsync(inner, vars, ctx).ConfigureAwait(false);
                return done == inner ? leaf : "not " + done;
            }

            // Comparison: transform each operand side independently so shapes
            // like `some.call(...) >= 50` and `{x} == other.call()` both work.
            // Earliest-operator + quote-aware search mirrors EvalSingle.
            string? bestOp  = null;
            int     bestIdx = int.MaxValue;
            foreach (var op in ComparisonOps)
            {
                int idx = IndexOfOutsideQuotes(t, op);
                if (idx >= 0 && idx < bestIdx) { bestIdx = idx; bestOp = op; }
            }
            if (bestOp != null)
            {
                string left  = t.Substring(0, bestIdx).Trim();
                string right = t.Substring(bestIdx + bestOp.Length).Trim();
                string ldone = await PreExecuteCallOperandAsync(left, vars, ctx).ConfigureAwait(false);
                string rdone = await PreExecuteCallOperandAsync(right, vars, ctx).ConfigureAwait(false);
                return (ldone == left && rdone == right) ? leaf : $"{ldone} {bestOp} {rdone}";
            }

            string result = await PreExecuteCallOperandAsync(t, vars, ctx).ConfigureAwait(false);
            return result == t ? leaf : result;
        }

        private async Task<string> PreExecuteCallOperandAsync(string operand, Dictionary<string, string> vars, ConditionCallContext ctx)
        {
            // Quoted text is a string VALUE, never code — same rule as the
            // command-arg loop's "do not strip quotes before classification".
            if (operand.Length == 0 || operand[0] == '"') return operand;

            var call = CommandParseRegex.Match(operand);
            if (!call.Success) return operand;

            string callee = call.Groups[1].Value;
            // convert.to_* stays with EvalSingle's native (sync) wrappers;
            // .startswith/.endswith stay with TryEvalStringMethod.
            if (callee.StartsWith("convert.to_", StringComparison.OrdinalIgnoreCase)) return operand;
            if (callee.EndsWith(".startswith", StringComparison.OrdinalIgnoreCase) ||
                callee.EndsWith(".endswith",  StringComparison.OrdinalIgnoreCase)) return operand;
            if (!_commands.ContainsKey(callee)) return operand;

            // Identical call text within ONE condition evaluation executes
            // once and re-uses its temp var (Flow.IsValid repeats the same
            // resolved input across six comparison legs).
            if (ctx.OperandKeys.TryGetValue(operand, out var seen))
                return "{" + seen + "}";

            string? result = await ExecuteCommandWithResult(operand, vars).ConfigureAwait(false);

            // Splice via a per-evaluation temp var, not a quoted literal: the
            // result may contain quotes / operators / " and " which would be
            // re-parsed as condition STRUCTURE by the sync scanners. A var
            // reference keeps the rewritten template inert; SubstituteVars
            // resolves it at the leaf, where values are always data. Keys are
            // re-used across while_loop iterations by design (overwrite).
            string key = $"__cond_call_{++ctx.Counter}";
            ctx.OperandKeys[operand] = key;
            vars[key] = result ?? string.Empty;
            return "{" + key + "}";
        }
    }
}
