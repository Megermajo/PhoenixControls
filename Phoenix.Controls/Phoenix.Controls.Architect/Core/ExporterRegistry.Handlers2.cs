// Handler implementation classes carved from ExporterRegistry.cs ().
// Owns: all IExporterHandler implementations used by RegisterImperative —
//   BranchHandler, IfHandler, SwitchHandler, SequenceHandler, FlipFlopHandler,
//   DoOnceHandler, DoNHandler, ForLoopHandler, WhileLoopHandler, CooldownHandler,
//   IsValidHandler, RerouteHandler, DelayHandler, EnumMatchHandler, ForEachHandler,
//   WaitForVisualHandler, WaitForEventHandler, ChatWaitForNextHandler,
//   ChatPeekRecentHandler, TimeoutHandler, ParallelHandler, JoinHandler,
//   QueuePopHandler, ArrayUnpackHandler, all Db*Handlers, VisualTriggerHandler,
//   StateSwitchHandler, VarSetHandler, PublicSetHandler, MathChanceHandler,
//   TwitchLastActiveHandler, TwitchGetViewersHandler, EventTriggerHandler,
//   EventReturnHandler, MacroCallHandler, ProcessSpawnHandler,
//   ProcessEntryHandler, ProcessExitHandler.

using System;
using System.Collections.Generic;
using System.Linq;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Architect.Core
{    internal sealed class BranchHandler : IExporterHandler
    {
        public string NodeTitle => "Logic.Branch";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string cond = ctx.Resolve(node, "Condition", "true");
            ctx.EmitConditional(node, cond, "True", "False", prefix, indent);
        }
    }

    internal sealed class IfHandler : IExporterHandler
    {
        public string NodeTitle => "Logic.If";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string a  = ctx.Materialize(node, "A", "\"\"");
            string b  = ctx.Materialize(node, "B", "\"\"");
            string op = node.GetAttr("Operator", "==");
            ctx.EmitConditional(node, $"{a} {op} {b}", "True", "False", prefix, indent);
        }
    }

    // #28 — Logic.Gate handler removed. The template was deprecated and has zero
    // .phxg references in `Phoenix.Controls.Hub/data/`. Restore from git history
    // if a use case ever resurfaces.

    internal sealed class SwitchHandler : IExporterHandler
    {
        public string NodeTitle => "Logic.Switch";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string val = ctx.Resolve(node, "Value", "\"\"");
            // Process all non-Default cases first, then Default — otherwise a
            // `Default`-first socket order produces an `else:` with no preceding
            // `if`, and a real case after Default emits as `if` instead of `elif`.
            var allOuts   = node.Sockets.Where(s => s.Type == SocketType.Output).ToList();
            var realCases = allOuts.Where(s => s.Name != "Default").ToList();
            var defaultS  = allOuts.FirstOrDefault(s => s.Name == "Default");

            // Track configured case values to detect duplicates and warn (#27).
            // M11 — duplicate cases produce unreachable arms (the first match
            // always wins). Emit ONLY the first occurrence and surface a
            // ValidationWarning for each dropped duplicate so the user sees it
            // in the script header. Don't emit the dead code.
            var seenCaseValues = new System.Collections.Generic.HashSet<string>();
            bool any = false;
            foreach (var cs in realCases)
            {
                var target = ctx.GetTargetNode(node.Id, cs.Id);
                if (target == null) continue;
                string caseVal = node.GetAttr(cs.Name, cs.Name);
                if (!seenCaseValues.Add(caseVal))
                {
                    ctx.AddRuntimeWarning(
                        $"Logic.Switch: duplicate case value \"{caseVal}\" on socket '{cs.Name}' — arm dropped (unreachable; the first matching case wins).",
                        node.Id);
                    continue;
                }
                string esc = ctx.EscapeStringLiteral(caseVal);
                ctx.Emit($"{prefix}{(any ? "elif" : "if")} {val} == \"{esc}\":");
                ctx.ProcessNode(target, indent + 1);
                any = true;
            }
            if (defaultS != null)
            {
                var defaultT = ctx.GetTargetNode(node.Id, defaultS.Id);
                if (defaultT != null)
                {
                    if (any) ctx.Emit($"{prefix}else:");
                    // If no real case fired, the Default body becomes a bare
                    // `if true:` so the indent is well-formed and the runtime
                    // unconditionally takes it.
                    else ctx.Emit($"{prefix}if true:");
                    ctx.ProcessNode(defaultT, indent + 1);
                }
            }
        }
    }

    internal sealed class SequenceHandler : IExporterHandler
    {
        public string NodeTitle => "Logic.Sequence";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            // Order arms by leading-integer in the socket name (so "1, 2, 3, 10"
            // sorts numerically instead of lexicographically as "1, 10, 2, 3").
            // Names without a leading integer fall back to socket creation order.
            var outs = node.Sockets
                .Where(s => s.Type == SocketType.Output)
                .Select((s, idx) => (Socket: s, OrigIndex: idx))
                .OrderBy(t =>
                {
                    string n = t.Socket.Name.TrimStart();
                    int end = 0;
                    while (end < n.Length && char.IsDigit(n[end])) end++;
                    return end > 0 && int.TryParse(n.Substring(0, end), out int v) ? v : int.MaxValue;
                })
                .ThenBy(t => t.OrigIndex);
            foreach (var t in outs)
            {
                var target = ctx.GetTargetNode(node.Id, t.Socket.Id);
                if (target != null) ctx.ProcessNode(target, indent);
            }
        }
    }

    internal sealed class FlipFlopHandler : IExporterHandler
    {
        public string NodeTitle => "Flow.FlipFlop";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string id = ctx.IdPrefix(node);
            // Toggle the global as a boolean; compare it as a boolean. Earlier
            // versions emitted `if global... == "true":` which never matched
            // because the runtime stores the toggle as a true/false bool.
            //
            // Audit fix — the RHS read MUST be braced ({global._flipflop_X}), not
            // bare. The engine's DB-preload (DbPreloadRegex) only rehydrates braced
            // {global.*} refs at script start; a bare read on a fresh execution
            // (every event/chat trigger is its own ExecuteScriptAsync) is never in
            // vars, so `not global._flipflop_X` evaluated the literal identifier as
            // false and fired branch A on every trigger, never toggling. The
            // assignment LHS stays bare (HandleAssignment takes the key from the raw
            // line and only substitutes the RHS, so the write target is safe); the
            // following condition reads the just-written value from _executionVars.
            ctx.Emit($"{prefix}global._flipflop_{id} = not {{global._flipflop_{id}}}");
            ctx.EmitConditional(node, $"global._flipflop_{id}", "A", "B", prefix, indent);
        }
    }

    internal sealed class DoOnceHandler : IExporterHandler
    {
        public string NodeTitle => "Flow.DoOnce";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string id = ctx.IdPrefix(node);
            // Audit fix — braced read so the engine DB-preloads the persisted state
            // (same root cause as Flow.FlipFlop). A bare `global._doonce_X` in the
            // condition was never rehydrated on a fresh execution, so the comparison
            // saw the literal identifier and the body ran on every trigger instead
            // of once. The set line's LHS stays bare (assignment key is safe).
            ctx.Emit($"{prefix}if {{global._doonce_{id}}} != \"done\":");
            ctx.Emit($"{prefix}    global._doonce_{id} = \"done\"");
            // [audit 2026-06-01] "Out" is the UNCONDITIONAL flow continuation that
            // runs after the once-guard block, not a child nested inside it — so it
            // emits at the same indent as the `if`, NOT indent+1. Passing indent+1
            // double-indented every node downstream of Flow.DoOnce.
            ctx.FollowNamed(node, "Out", indent);
        }
    }

    internal sealed class DoNHandler : IExporterHandler
    {
        public string NodeTitle => "Flow.DoN";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string n = ctx.Resolve(node, "N", "3");
            string counter = $"global._don_counter_{ctx.IdPrefix(node)}";
            ctx.Emit($"{prefix}{counter} += 1");
            ctx.Emit($"{prefix}if {counter} <= {n}:");
            ctx.FollowNamed(node, "Loop Body", indent + 1);
            var comp = ctx.GetNamedTarget(node, "Completed");
            if (comp != null)
            {
                ctx.Emit($"{prefix}else:");
                ctx.ProcessNode(comp, indent + 1);
            }
        }
    }

    internal sealed class ForLoopHandler : IExporterHandler
    {
        public string NodeTitle => "Flow.ForLoop";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string first = ctx.Resolve(node, "First", "0");
            string last  = ctx.Resolve(node, "Last", "10");
            //  Use the strict B8 callable detector instead of Contains("(")
            // so a user-typed parenthesised string literal like "(foo)" doesn't
            // spuriously hoist into a _pre_ global. The MaterializeInput path in
            // ScriptExporter already gates on CallableRegex; ForLoopHandler was the
            // only handler still running the loose substring heuristic.
            if (ScriptExporter.IsCallableExpression(first))
            {
                string preVar = $"global._pre_{node.Id[..6]}_first";
                ctx.Emit($"{prefix}{preVar} = {first}");
                first = $"{{{preVar}}}";
            }
            if (ScriptExporter.IsCallableExpression(last))
            {
                string preVar = $"global._pre_{node.Id[..6]}_last";
                ctx.Emit($"{prefix}{preVar} = {last}");
                last = $"{{{preVar}}}";
            }
            ctx.Emit($"{prefix}for_loop({first}, {last}, {node.Id[..6]})");
            ctx.FollowNamed(node, "Loop Body", indent + 1);
            ctx.FollowNamed(node, "Completed", indent);
        }
    }

    internal sealed class WhileLoopHandler : IExporterHandler
    {
        public string NodeTitle => "Flow.WhileLoop";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string cond = ctx.Resolve(node, "Condition", "true");
            ctx.Emit($"{prefix}while_loop({cond})");
            ctx.FollowNamed(node, "Loop Body", indent + 1);
            ctx.FollowNamed(node, "Completed", indent);
        }
    }

    internal sealed class CooldownHandler : IExporterHandler
    {
        public string NodeTitle => "Flow.Cooldown";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string user     = ctx.Resolve(node, "User", "\"\"");
            string globalCd = node.GetAttr("GlobalCooldown", "0");
            string userCd   = node.GetAttr("UserCooldown",   "0");
            ctx.EmitConditional(node,
                $"cooldown.check({user}, {globalCd}, {userCd})",
                "Ready", "Blocked", prefix, indent);
        }
    }

    // Flow.Select removed — pure-data multiplexer; resolved inline via
    // ComputeInlineValue ("select(idx, A, B, C, D)") and the dedicated
    // hoist branch in ResolveOutputFromNode.

    internal sealed class IsValidHandler : IExporterHandler
    {
        public string NodeTitle => "Flow.IsValid";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string val = ctx.Resolve(node, "Value", "\"\"");
            // H15 — extend the predicate to all reasonable "is the value usable"
            // checks: not empty, not the literal string "0" / "false" / "null".
            // Previously the contract said "valid" but we only checked empty string.
            // The trailing parenthesised group covers the case-insensitive forms.
            string cond = $"{val} != \"\" and {val} != \"0\" and {val} != \"false\" and {val} != \"False\" and {val} != \"null\" and {val} != \"NULL\"";
            ctx.EmitConditional(node, cond, "True", "False", prefix, indent);
        }
    }

    internal sealed class RerouteHandler : IExporterHandler
    {
        public string NodeTitle => "Flow.Reroute";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
            => ctx.FollowNamed(node, "Out", indent);
    }

    internal sealed class DelayHandler : IExporterHandler
    {
        public string NodeTitle => "Flow.Delay";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string seconds = ctx.Resolve(node, "Seconds", "1.0");
            ctx.Emit($"{prefix}delay_seconds({seconds})");
            ctx.FollowNamed(node, "Then", indent);
        }
    }

    internal sealed class EnumMatchHandler : IExporterHandler
    {
        public string NodeTitle => "Logic.EnumMatch";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string val      = ctx.Resolve(node, "Value", "\"\"");
            string listVal  = ctx.Resolve(node, "List", "");
            string matchVar = $"global._ematch_{node.Id[..6]}";

            ctx.Emit($"{prefix}{matchVar} = \"\"");

            if (!string.IsNullOrEmpty(listVal))
            {
                string valVar  = $"global._ematch_val_{node.Id[..6]}";
                string listVar = $"global._ematch_list_{node.Id[..6]}";
                ctx.Emit($"{prefix}{valVar} = {val}");
                ctx.Emit($"{prefix}{listVar} = {listVal}");
                ctx.Emit($"{prefix}if {{{valVar}}} in {{{listVar}}}:");
                ctx.Emit($"{prefix}    {matchVar} = {{{valVar}}}");
                ctx.Emit($"{prefix}else:");
                ctx.Emit($"{prefix}    {matchVar} = \"\"");
            }
            else
            {
                string entries = node.GetAttr("Entries", "");
                var items = entries.Split(',')
                                   .Select(e => e.Trim())
                                   .Where(e => e.Length > 0)
                                   .ToList();
                bool first = true;
                foreach (var item in items)
                {
                    string esc = ctx.EscapeStringLiteral(item);
                    ctx.Emit($"{prefix}{(first ? "if" : "elif")} {val} == \"{esc}\":");
                    ctx.Emit($"{prefix}    {matchVar} = \"{esc}\"");
                    first = false;
                }
                if (!first) { ctx.Emit($"{prefix}else:"); ctx.Emit($"{prefix}    {matchVar} = \"\""); }
            }

            ctx.Emit($"{prefix}if {{{matchVar}}} != \"\":");
            ctx.FollowNamed(node, "Match", indent + 1);
            var noMatch = ctx.GetNamedTarget(node, "NoMatch");
            if (noMatch != null) { ctx.Emit($"{prefix}else:"); ctx.ProcessNode(noMatch, indent + 1); }
        }
    }

    internal sealed class ForEachHandler : IExporterHandler
    {
        public string NodeTitle => "Flow.ForEach";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string listExpr = ctx.Resolve(node, "List", "\"\"");
            string listVar = $"global._foreach_{node.Id[..6]}";
            ctx.Emit($"{prefix}{listVar} = {listExpr}");
            ctx.Emit($"{prefix}for_each({{{listVar}}})");
            ctx.FollowNamed(node, "Loop Body", indent + 1);
            ctx.FollowNamed(node, "Done", indent);
        }
    }

    internal sealed class WaitForVisualHandler : IExporterHandler
    {
        public string NodeTitle => "Async.WaitForVisual";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string layerId     = ctx.Resolve(node, "LayerID",     "\"\"");
            string widgetId    = ctx.Resolve(node, "WidgetID",    "\"\"");
            string triggerName = ctx.Resolve(node, "TriggerName", "\"onTrigger\"");
            string timeout     = ctx.Resolve(node, "TimeoutMS",   "10000");

            // Optional dynamic input sockets (added by the user for EventData) become trailing
            // key=value args on the call so the Hub-side ScriptManager handler can fold them
            // into the trigger's EventData dictionary.
            var extraArgs = string.Join(", ",
                node.Sockets
                    .Where(s => s.Type == SocketType.Input
                                && !s.IsPlaceholder
                                && s.Name != "Flow"
                                && s.Name != "LayerID"
                                && s.Name != "WidgetID"
                                && s.Name != "TriggerName"
                                && s.Name != "TimeoutMS")
                    .Select(s => $"{s.Name}={ctx.StripQuotes(ctx.Resolve(node, s.Name, "\"\""))}"));
            string trailing = string.IsNullOrEmpty(extraArgs) ? "" : $", {extraArgs}";

            ctx.Emit($"{prefix}wait_for_visual({layerId}, {widgetId}, {triggerName}, {timeout}{trailing})");
            // The runtime sets global._wait_ok to "true" if the wait completed,
            // "false" on timeout. Branch on it so the Done body can't fall
            // through into the Timeout body.
            ctx.EmitConditional(node,
                "{global._wait_ok} == \"true\"",
                "Done", "Timeout", prefix, indent);
        }
    }

    internal sealed class WaitForEventHandler : IExporterHandler
    {
        public string NodeTitle => "Async.WaitForEvent";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string evType  = ctx.Resolve(node, "EventType", "\"VISUAL_COMPLETE\"");
            string timeout = ctx.Resolve(node, "TimeoutMS", "10000");
            ctx.Emit($"{prefix}wait_for_event({evType}, {timeout})");
            ctx.EmitConditional(node,
                "{global._wait_ok} == \"true\"",
                "Received", "Timeout", prefix, indent);
        }
    }

    // Chat.WaitForNext — runtime-pause + filter-on-username-or-command pair.
    // Per-node global var names so two Chat.WaitForNext nodes in the same script
    // don't clobber each other's Username / Message outputs (the wait_for_event
    // pattern reuses fixed globals; we picked per-id here because two waits are
    // a more plausible authoring case for chat than for bus events).
    internal sealed class ChatWaitForNextHandler : IExporterHandler
    {
        public string NodeTitle => "Chat.WaitForNext";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string user    = ctx.Resolve(node, "User",      "\"\"");
            string command = ctx.Resolve(node, "Command",   "\"\"");
            string timeout = ctx.Resolve(node, "TimeoutMS", "30000");
            string id      = ctx.IdPrefix(node, 6);
            string okVar   = $"global._chat_wait_ok_{id}";
            string userVar = $"global._chat_wait_user_{id}";
            string msgVar  = $"global._chat_wait_msg_{id}";

            // Cache per-output result vars so ResolveOutputFromNode can hand them
            // to downstream consumers wired to Username / Message.
            ctx.NodeResultVars[$"{node.Id}_Username"] = $"{{{userVar}}}";
            ctx.NodeResultVars[$"{node.Id}_Message"]  = $"{{{msgVar}}}";

            ctx.Emit($"{prefix}chat.wait_for_next({user}, {command}, {timeout}, \"{okVar}\", \"{userVar}\", \"{msgVar}\")");
            ctx.EmitConditional(node,
                $"{{{okVar}}} == \"true\"",
                "Got", "TimedOut", prefix, indent);
        }
    }

    // Chat.PeekRecent — non-blocking snapshot of the recent-chat ring. Per-node
    // global var names mirror Chat.WaitForNext; downstream Usernames / Messages
    // outputs read from NodeResultVars.
    internal sealed class ChatPeekRecentHandler : IExporterHandler
    {
        public string NodeTitle => "Chat.PeekRecent";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string n        = ctx.Resolve(node, "N", "1");
            string id       = ctx.IdPrefix(node, 6);
            string usersVar = $"global._chat_peek_users_{id}";
            string msgsVar  = $"global._chat_peek_msgs_{id}";

            ctx.NodeResultVars[$"{node.Id}_Usernames"] = $"{{{usersVar}}}";
            ctx.NodeResultVars[$"{node.Id}_Messages"]  = $"{{{msgsVar}}}";

            ctx.Emit($"{prefix}chat.peek_recent({n}, \"{usersVar}\", \"{msgsVar}\")");
            ctx.FollowNamed(node, "Done", indent);
        }
    }

    // Async.Timeout — branches on whether the current event-handler script has
    // already burned through more than MS milliseconds. The runtime
    // `timeout_check(ms)` writes "true" / "false" into global._timeout_ok which
    // the if-block below routes on. OnTime fires while inside budget, Late
    // fires once the budget is gone.
    internal sealed class TimeoutHandler : IExporterHandler
    {
        public string NodeTitle => "Async.Timeout";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string ms = ctx.Resolve(node, "MS", "5000");
            ctx.Emit($"{prefix}timeout_check({ms})");
            ctx.Emit($"{prefix}if {{global._timeout_ok}} == \"true\":");
            ctx.FollowNamed(node, "OnTime", indent + 1);
            var lateTarget = ctx.GetNamedTarget(node, "Late");
            if (lateTarget != null)
            {
                ctx.Emit($"{prefix}else:");
                ctx.ProcessNode(lateTarget, indent + 1);
            }
        }
    }

    internal sealed class ParallelHandler : IExporterHandler
    {
        public string NodeTitle => "Async.Parallel";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            // M15 — iterate every Branch* output that exists on the live node so
            // the 3 â†’ 8 template raise actually emits all wired branches. Order
            // by the trailing integer so Branch1..Branch10 stay numerically
            // sorted (mirrors SequenceHandler's leading-integer ordering, just
            // applied to the suffix). Unwired branches are no-op via FollowNamed.
            ctx.Emit($"{prefix}parallel_begin");
            var branches = node.Sockets
                .Where(s => s.Type == SocketType.Output
                         && s.Name.StartsWith("Branch", System.StringComparison.Ordinal))
                .OrderBy(s =>
                {
                    string suffix = s.Name.Substring("Branch".Length);
                    return int.TryParse(suffix, out int v) ? v : int.MaxValue;
                });
            foreach (var b in branches)
                ctx.FollowNamed(node, b.Name, indent + 1);
            ctx.Emit($"{prefix}parallel_end");
        }
    }

    internal sealed class JoinHandler : IExporterHandler
    {
        public string NodeTitle => "Async.Join";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            ctx.Emit($"{prefix}join_wait");
            ctx.FollowNamed(node, "Flow", indent);
        }
    }

    // Queue.Pop emits the runtime queue.pop call passing the names of the two
    // output globals. The runtime fills them with the eventid + payload of the
    // popped entry and sets global.queue_empty so the Empty branch below can
    // route on whether anything was actually popped.
    internal sealed class QueuePopHandler : IExporterHandler
    {
        public string NodeTitle => "Queue.Pop";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string id6 = node.Id[..6];
            string eventidVar = $"global._queue_pop_eventid_{id6}";
            string payloadVar = $"global._queue_pop_payload_{id6}";
            ctx.Emit($"{prefix}queue.pop(\"{eventidVar}\", \"{payloadVar}\")");
            // Always emit the queue_empty guard so an empty queue never
            // accidentally fires the Done branch. Done runs only when the
            // queue actually had something; Empty (when wired) runs otherwise.
            ctx.EmitConditional(node,
                "{global.queue_empty} != \"true\"",
                "Done", "Empty", prefix, indent);
        }
    }

    // Queue.Length removed — pure-data; resolved inline via ComputeInlineValue
    // ("queue.length()") and the dedicated hoist branch in ResolveOutputFromNode.

    // Array.Push captures the engine's returned list into a per-node global so
    // the List output socket can carry the post-push list to downstream nodes.
    // The runtime command `array.push(list, value)` returns the appended list as
    // its result, which the assignment statement persists into the global.
    internal sealed class ArrayPushHandler : IExporterHandler
    {
        public string NodeTitle => "Array.Push";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string list = ctx.Resolve(node, "List",  "\"\"");
            string val  = ctx.Resolve(node, "Value", "\"\"");
            string outVar = $"global._array_push_{node.Id[..6]}";
            ctx.Emit($"{prefix}{outVar} = array.push({list}, {val})");
            ctx.NodeResultVars[node.Id] = $"{{{outVar}}}";
            ctx.FollowNamed(node, "Done", indent);
        }
    }

    internal sealed class ArrayUnpackHandler : IExporterHandler
    {
        public string NodeTitle => "Array.Unpack";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string arr = ctx.Resolve(node, "Array", "\"\"");
            string[] slots = { "Item 0", "Item 1", "Item 2", "Item 3", "Item 4" };
            for (int i = 0; i < slots.Length; i++)
            {
                var outSock = node.Sockets.FirstOrDefault(s => s.Type == SocketType.Output && s.Name == slots[i]);
                if (outSock == null) continue;
                bool connected = ctx.IsOutputConnected(node.Id, outSock.Id);
                if (connected)
                {
                    string varName = $"global._unpack_{node.Id[..6]}_{i}";
                    ctx.Emit($"{prefix}{varName} = array.get({arr}, {i})");
                }
            }
            var restSock = node.Sockets.FirstOrDefault(s => s.Type == SocketType.Output && s.Name == "Rest");
            if (restSock != null && ctx.IsOutputConnected(node.Id, restSock.Id))
            {
                string restVar = $"global._unpack_{node.Id[..6]}_rest";
                ctx.Emit($"{prefix}{restVar} = array.slice({arr}, 5)");
            }
        }
    }

    // Audit fix — DB.SetVariable/Increment emit a bare script assignment, which the
    // engine persists ONLY when the key starts with user./global. (HandleAssignment).
    // An un-prefixed key (e.g. "points") silently landed in execution-local vars and
    // was lost between triggers — the Databank node looked like it stored but didn't.
    // Auto-namespace bare keys to global. so they persist + DB-preload; explicitly
    // prefixed keys are respected as-is. Get/Set/Increment all normalize identically
    // so the read key always matches the written key.
    internal static class DbVarKeyNormalizer
    {
        private static readonly string[] Persistable =
            { "user.", "global.", "state.", "var.", "public." };
        public static string Normalize(string strippedKey)
        {
            if (string.IsNullOrWhiteSpace(strippedKey)) return "user.points";
            foreach (var p in Persistable)
                if (strippedKey.StartsWith(p, System.StringComparison.OrdinalIgnoreCase))
                    return strippedKey;
            return "global." + strippedKey;
        }
    }

    internal sealed class DbGetVariableHandler : IExporterHandler
    {
        public string NodeTitle => "DB.GetVariable";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string key  = ctx.Resolve(node, "Key", "\"user.points\"");
            string rVar = ctx.GetDbGetResultVar(node);
            string keyN = DbVarKeyNormalizer.Normalize(ctx.StripQuotes(key));
            ctx.Emit($"{prefix}{rVar} = {{{keyN}}}");
            ctx.FollowFlow(node, indent);
        }
    }

    internal sealed class DbSetVariableHandler : IExporterHandler
    {
        public string NodeTitle => "DB.SetVariable";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string key = ctx.Resolve(node, "Key", "\"user.points\"");
            // Treat blank/whitespace Key as missing, and auto-namespace un-prefixed
            // keys to global. so the bare assignment actually persists across triggers
            // (see DbVarKeyNormalizer). Default "user.points" is unchanged.
            string keyStripped = DbVarKeyNormalizer.Normalize(ctx.StripQuotes(key));
            string val = ctx.Resolve(node, "Value", "0");
            ctx.Emit($"{prefix}{keyStripped} = {val}");
            ctx.FollowNamed(node, "Done", indent);
        }
    }

    internal sealed class DbIncrementHandler : IExporterHandler
    {
        public string NodeTitle => "DB.Increment";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            // 2026-06-08 — DB.Increment now uses the DB.SetCell socket shape (TableName /
            // RowId / Column input sockets, Amount in place of Value) so it surfaces the
            // databank dropdown pickers and matches the other DB nodes. The reads below
            // are the pre-restructure logic with the socket names renamed (Key→Column,
            // Row→RowId); TableName is read from its attribute, which the new TableName
            // socket's pill writes to. Old graphs are migrated in
            // GraphSerializer.MigrateNodes so wires + values carry over and the emitted
            // .phx is byte-for-byte unchanged (ExporterGoldenTests).
            string tableName = node.GetAttr("TableName", "User_Counter");
            string key = ctx.Resolve(node, "Column", "\"user.points\"");
            string amt = ctx.Resolve(node, "Amount", "1");
            string row = ctx.Resolve(node, "RowId", "");

            string outVar = $"_db_inc_{node.Id[..6]}";
            ctx.NodeResultVars[node.Id] = $"global.{outVar}";

            if (!string.IsNullOrEmpty(row) && row != "0")
            {
                // Single atomic call. db.increment_cell returns the new value
                // server-side but the engine has no "assign command return to
                // var" syntax today, so we follow the atomic update with a
                // get_cell to capture the post-increment value into outVar
                // for downstream consumers. The data store update itself is
                // atomic — the brief read-after window only affects outVar's
                // freshness for display purposes, not the persisted total.
                string nodePrefix = node.Id.Replace("-", "")[..6];
                string amtMat = amt;
                if (amt.Contains("("))
                {
                    string amtVar = $"global._pre_{nodePrefix}_amt";
                    ctx.Emit($"{prefix}{amtVar} = {amt}");
                    amtMat = amtVar;
                }
                //  tableName + key are interpolated INSIDE double-quoted
                // literals, so a user-typed value containing `"` or `\` would break
                // the parse. EscapeStringLiteral after StripQuotes preserves the
                // intended characters while keeping the emitted literal valid.
                string tblEsc = ctx.EscapeStringLiteral(ctx.StripQuotes(tableName));
                string keyEsc = ctx.EscapeStringLiteral(ctx.StripQuotes(key));
                ctx.Emit($"{prefix}db.increment_cell(\"{tblEsc}\", {row}, \"{keyEsc}\", {amtMat})");
                ctx.Emit($"{prefix}global.{outVar} = db.get_cell(\"{tblEsc}\", {row}, \"{keyEsc}\")");
            }
            else
            {
                // Blank Key would emit ` = value` (unparseable); auto-namespace
                // un-prefixed keys to global. so the increment persists across triggers
                // (see DbVarKeyNormalizer), matching DbSetVariableHandler / DbGetVariableHandler.
                string keyStripped = DbVarKeyNormalizer.Normalize(ctx.StripQuotes(key));
                //  Two statements via two Emit calls so the line writer
                // is responsible for inserting Environment.NewLine — the previous
                // single Emit with an embedded `\n` mixed line endings on Windows
                // (the rest of the file ends each Emit with CRLF; the inner `\n`
                // produced a bare LF in the middle).
                // Audit fix — the read side MUST be braced so the engine DB-preloads
                // the prior value on a fresh execution; a bare `math.add(global.points, n)`
                // parsed the literal identifier as 0 and lost the running total.
                ctx.Emit($"{prefix}global.{outVar} = math.add({{{keyStripped}}}, {amt})");
                ctx.Emit($"{prefix}{keyStripped} = global.{outVar}");
            }

            ctx.FollowNamed(node, "Done", indent);
        }
    }

    internal sealed class DbCheckExistsHandler : IExporterHandler
    {
        public string NodeTitle => "DB.CheckExists";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string key = ctx.Resolve(node, "Key", "\"var\"");
            ctx.EmitConditional(node, $"db.check({key})", "True", "False", prefix, indent);
        }
    }

    internal sealed class DbFindRowHandler : IExporterHandler
    {
        public string NodeTitle => "DB.FindRow";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string table  = ctx.Materialize(node, "TableName", "\"\"");
            string col    = ctx.Materialize(node, "Column",    "\"\"");
            string val    = ctx.Materialize(node, "Value",     "\"\"");
            string ridVar = $"global._rid_{node.Id.Replace("-","")[..6]}";
            ctx.Emit($"{prefix}db.find_row({table}, {col}, {val}, \"{ridVar}\")");
            ctx.NodeResultVars[node.Id] = ridVar;
            ctx.EmitConditional(node, $"{ridVar} != \"\"", "Found", "NotFound", prefix, indent);
        }
    }

    internal sealed class DbSetCellHandler : IExporterHandler
    {
        public string NodeTitle => "DB.SetCell";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string table = ctx.Materialize(node, "TableName", "\"\"");
            string rowId = ctx.Materialize(node, "RowId",     "0");
            string col   = ctx.Materialize(node, "Column",    "\"\"");
            string val   = ctx.Materialize(node, "Value",     "\"\"");
            ctx.Emit($"{prefix}db.set_cell({table}, {rowId}, {col}, {val})");
            ctx.FollowNamed(node, "Done", indent);
        }
    }

    internal sealed class DbInsertRowHandler : IExporterHandler
    {
        public string NodeTitle => "DB.InsertRow";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string table   = ctx.Materialize(node, "TableName", "\"\"");
            string col     = ctx.Materialize(node, "Column",    "\"\"");
            string val     = ctx.Materialize(node, "Value",     "\"\"");
            string rowVar  = node.GetAttr("NewRowId", $"global._newrowid_{node.Id.Replace("-","")[..6]}");
            ctx.Emit($"{prefix}db.insert_row({table}, {col}, {val}, \"{rowVar}\")");
            ctx.NodeResultVars[node.Id] = rowVar;
            ctx.FollowNamed(node, "Done", indent);
        }
    }

    internal sealed class DbDeleteRowHandler : IExporterHandler
    {
        public string NodeTitle => "DB.DeleteRow";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string table = ctx.Materialize(node, "TableName", "\"\"");
            string rowId = ctx.Materialize(node, "RowId", "0");
            ctx.Emit($"{prefix}db.delete_row({table}, {rowId})");
            ctx.FollowNamed(node, "Done", indent);
        }
    }

    internal sealed class DbFetchRowHandler : IExporterHandler
    {
        public string NodeTitle => "DB.FetchRow";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string table  = ctx.Materialize(node, "TableName", "\"\"");
            string rowId  = ctx.Materialize(node, "RowId", "0");
            string rowVar = node.GetAttr("Row", $"global._row_{node.Id.Replace("-","")[..6]}");
            ctx.Emit($"{prefix}db.fetch_row({table}, {rowId}, \"{rowVar}\")");
            // [audit 2026-06-01] Register the fetched row var so downstream nodes
            // wired to the "Row" output (and column-socket synthesis in
            // ScriptExporter) can resolve it — mirrors DbFindRowHandler /
            // DbInsertRowHandler which both register NodeResultVars. Was missing,
            // so the "Row" output silently failed to resolve.
            ctx.NodeResultVars[node.Id] = rowVar;
            ctx.EmitConditional(node, $"{rowVar} != \"\"", "Found", "NotFound", prefix, indent);
        }
    }

    internal sealed class VisualTriggerHandler : IExporterHandler
    {
        public string NodeTitle => "Visual.Trigger";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string layerId     = ctx.Resolve(node, "LayerID",     "\"\"");
            string widgetId    = ctx.Resolve(node, "WidgetID",    "\"\"");
            string triggerName = ctx.Resolve(node, "TriggerName", "\"onTrigger\"");

            // Args is a fixed Collection input. Hub's ScriptManager splits "a,b,c" into
            // Args1=a, Args2=b, Args3=c on delivery so widget-side consumers can read
            // positional values via {Args1} text-substitution and Result.If's When attr.
            // Skip when unwired/empty so existing graphs don't trip golden diffs.
            string argsValue = ctx.StripQuotes(ctx.Resolve(node, "Args", "\"\""));
            string argsKvp   = string.IsNullOrEmpty(argsValue) ? "" : $", Args={argsValue}";

            // Dynamic input sockets (anything beyond the fixed Flow + addressing pins +
            // Args) become trailing key=value args so the Hub-side visual.trigger_queued
            // handler can fold them into the EventData dictionary.
            var varSockets = node.Sockets
                .Where(s => s.Type == SocketType.Input
                            && !s.IsPlaceholder
                            && s.Name != "Flow"
                            && s.Name != "LayerID"
                            && s.Name != "WidgetID"
                            && s.Name != "TriggerName"
                            && s.Name != "Args")
                .ToList();
            string args = string.Join(", ", varSockets.Select(s =>
                $"{s.Name}={ctx.StripQuotes(ctx.Resolve(node, s.Name, "\"\""))}"));
            string argsStr = args.Length > 0 ? $", {args}" : "";
            ctx.Emit($"{prefix}visual.trigger_queued({layerId}, {widgetId}, {triggerName}{argsKvp}{argsStr})");
            ctx.FollowNamed(node, "Done", indent);
        }
    }

    internal sealed class StateSwitchHandler : IExporterHandler
    {
        public string NodeTitle => "State.Switch";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string name = ctx.Resolve(node, "Name", "phase");
            // Treat blank/whitespace Name as missing — otherwise emits `{state.""}`
            // which never resolves at runtime.
            if (string.IsNullOrWhiteSpace(ctx.StripQuotes(name))) name = "phase";
            // Process all non-Default cases first, then Default — same shape as
            // Logic.Switch (see SwitchHandler).
            var allOuts   = node.Sockets.Where(s => s.Type == SocketType.Output).ToList();
            var realCases = allOuts.Where(s => s.Name != "Default").ToList();
            var defaultS  = allOuts.FirstOrDefault(s => s.Name == "Default");

            bool any = false;
            foreach (var cs in realCases)
            {
                var target = ctx.GetTargetNode(node.Id, cs.Id);
                if (target == null) continue;
                string caseVal = node.GetAttr(cs.Name, cs.Name);
                ctx.Emit($"{prefix}{(any ? "elif" : "if")} {{state.{name}}} == \"{caseVal}\":");
                ctx.ProcessNode(target, indent + 1);
                any = true;
            }
            if (defaultS != null)
            {
                var defaultT = ctx.GetTargetNode(node.Id, defaultS.Id);
                if (defaultT != null)
                {
                    if (any) ctx.Emit($"{prefix}else:");
                    else ctx.Emit($"{prefix}if true:");
                    ctx.ProcessNode(defaultT, indent + 1);
                }
            }
        }
    }

    internal sealed class VarSetHandler : IExporterHandler
    {
        public string NodeTitle => "Var.Set";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            // GetAttrOrFallback (vs GetAttr) treats a blank/whitespace value as
            // missing — otherwise an empty VariableName produces `var. = "x"`.
            string varName = node.GetAttrOrFallback("VariableName", "myVar");
            string val = ctx.Resolve(node, "Value", "\"\"");
            ctx.Emit($"{prefix}var.{varName} = {val}");
            ctx.FollowNamed(node, "Flow", indent);
        }
    }

    // Emits public.set("key", value). Differs from VarSetHandler in that the
    // command-call form routes through ScriptManager â†’ SetLocalResultVar, which
    // tags the key in _branchResultKeysLocal so writes inside parallel_begin
    // branches merge back on join. Var.Set's bare-assignment path is branch-local
    // by design (BH-003 contract).
    internal sealed class PublicSetHandler : IExporterHandler
    {
        public string NodeTitle => "Public.Set";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string keyName = node.GetAttrOrFallback("KeyName", "myKey");
            string val = ctx.Resolve(node, "Value", "\"\"");
            //  KeyName is interpolated INSIDE a double-quoted literal so a
            // user-typed key containing `"` or `\` would break the parse. Escape
            // before interpolating so `Hello "world"` becomes a valid `"Hello \"world\""`.
            ctx.Emit($"{prefix}public.set(\"{ctx.EscapeStringLiteral(keyName)}\", {val})");
            ctx.FollowNamed(node, "Flow", indent);
        }
    }

    internal sealed class MathChanceHandler : IExporterHandler
    {
        public string NodeTitle => "Math.Chance";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            // Per-node result var so two Math.Chance nodes don't clobber each
            // other's roll. The runtime command writes its boolean outcome to
            // the named global passed as the second argument.
            string rate     = ctx.Resolve(node, "% Rate", "50");
            string resultVar = $"global._chance_{ctx.IdPrefix(node)}";
            ctx.Emit($"{prefix}math.chance({rate}, \"{resultVar}\")");
            ctx.EmitConditional(node, $"{{{resultVar}}} == \"true\"", "Success", "Fail", prefix, indent);
        }
    }

    internal sealed class TwitchLastActiveHandler : IExporterHandler
    {
        public string NodeTitle => "Twitch.LastActive";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string username = ctx.Resolve(node, "Username", "\"\"");
            string minutes  = ctx.Resolve(node, "Minutes",  "5");
            string inactVar = $"global._inactive_{node.Id[..6]}";
            string minsVar  = $"global._mins_ago_{node.Id[..6]}";
            ctx.NodeResultVars[$"{node.Id}_MinutesAgo"] = $"{{{minsVar}}}";
            ctx.Emit($"{prefix}twitch.last_active({username}, {minutes}, \"{inactVar}\", \"{minsVar}\")");
            ctx.Emit($"{prefix}if {{{inactVar}}} == \"true\":");
            ctx.FollowNamed(node, "Inactive", indent + 1);
            var activeTarget = ctx.GetNamedTarget(node, "Active");
            if (activeTarget != null) { ctx.Emit($"{prefix}else:"); ctx.ProcessNode(activeTarget, indent + 1); }
        }
    }

    internal sealed class TwitchGetViewersHandler : IExporterHandler
    {
        public string NodeTitle => "Twitch.GetViewers";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string viewersVar = $"global._viewers_{node.Id[..6]}";
            ctx.NodeResultVars[$"{node.Id}_Viewers"] = $"{{{viewersVar}}}";
            ctx.Emit($"{prefix}twitch.get_viewers(\"{viewersVar}\")");
            ctx.FollowNamed(node, "Done", indent);
        }
    }

    internal sealed class EventTriggerHandler : IExporterHandler
    {
        public string NodeTitle => "Event.Trigger";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string evName = ctx.Resolve(node, "eventName", node.GetAttrOrFallback("EventName", "MyEvent"));
            var varSockets = node.Sockets
                .Where(s => s.Type == SocketType.Input && !s.IsPlaceholder && s.Name != "Flow"
                         && !s.Name.Equals("eventName", System.StringComparison.OrdinalIgnoreCase))
                .ToList();

            var argParts = new List<string>();
            foreach (var s in varSockets)
            {
                string val = ctx.Resolve(node, s.Name, "\"\"");
                if (val.Contains('|'))
                {
                    // Sanitize the socket name when it's interpolated into a
                    // runtime identifier — `.ToLower()` alone leaves spaces and
                    // punctuation intact, which produced unparseable script when
                    // a parameter was inline-renamed to e.g. "User Name".
                    string tempVar = $"global._evtrig_{node.Id[..6]}_{ctx.SanitizeIdentifier(s.Name.ToLower())}";
                    string makeArgs = string.Join(", ", val.Split('|'));
                    ctx.Emit($"{prefix}{tempVar} = array.make({makeArgs})");
                    argParts.Add($"{s.Name}={{{tempVar}}}");
                }
                else
                {
                    argParts.Add($"{s.Name}={ctx.StripQuotes(val)}");
                }
            }
            string args = string.Join(", ", argParts);
            string argsStr = args.Length > 0 ? $", {args}" : "";
            ctx.Emit($"{prefix}event.trigger({evName}{argsStr})");
            ctx.FollowNamed(node, "Flow", indent);
        }
    }

    internal sealed class EventReturnHandler : IExporterHandler
    {
        public string NodeTitle => "Event.Return";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            foreach (var s in node.Sockets
                .Where(s => s.Type == SocketType.Input && !s.IsPlaceholder && s.Name != "Flow"))
                ctx.Emit($"{prefix}event.ret.{s.Name} = {ctx.Resolve(node, s.Name, "\"\"")}");
            // Terminal node — no flow output
        }
    }

    // HttpParseJsonHandler removed — pure-data; resolved inline via
    // ComputeInlineValue and the dedicated hoist branch in ResolveOutputFromNode.

    internal sealed class MacroCallHandler : IExporterHandler
    {
        public string NodeTitle => "Macro.Call";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string macroId = node.GetAttr("MacroId", "");
            string macroName = node.GetAttr("MacroName", "Macro");

            var macro = string.IsNullOrEmpty(macroId)
                ? null
                : ctx.Graph.Macros.FirstOrDefault(m => m.MacroId == macroId);

            if (macro == null)
            {
                ctx.Emit($"{prefix}# MACRO_CALL:{macroName} (not found — inline expansion skipped)");
                ctx.FollowNamed(node, "Flow", indent);
                return;
            }

            // Surface authoring mistakes that MacroCallHandler would otherwise
            // paper over. These warnings ride at the call-site indent so they
            // survive the `.Skip(3)` header strip done below on the inner script.
            var allEntries = macro.Graph.Nodes.Where(n => n.Title == "Macro.Entry").ToList();
            if (allEntries.Count > 1)
                ctx.Emit($"{prefix}# WARNING: Macro '{macroName}' has multiple Macro.Entry nodes ({allEntries.Count}) — only the first is bound at the call site. Delete duplicates or merge their outputs.");
            var allExits = macro.Graph.Nodes.Where(n => n.Title == "Macro.Exit").ToList();
            if (allExits.Count > 1)
                ctx.Emit($"{prefix}# WARNING: Macro '{macroName}' has multiple Macro.Exit nodes ({allExits.Count}) — only the first is used. Delete duplicates or merge their inputs.");

            ctx.Emit($"{prefix}# BEGIN MACRO: {macroName}");

            // Compose a slot prefix that's unique per (macro, call-site) pair.
            // Use the FULL dash-stripped MacroId — earlier code truncated to 12
            // hex chars, which let two distinct macros sharing those 12 chars
            // collide on the cycle-detection key in CtxExportMacroSubGraph and
            // mis-fire as recursion.
            string stableMacroId = macroId.Replace("-", "");
            string slotPrefix = $"_macro_{stableMacroId}_{ctx.IdPrefix(node)}";

            var entryNode = macro.Graph.Nodes.FirstOrDefault(n => n.Title == "Macro.Entry");
            if (entryNode != null)
            {
                var entryOutputs = entryNode.Sockets
                    .Where(s => s.Type == SocketType.Output && !s.IsPlaceholder && s.Name != "Flow")
                    .ToList();
                foreach (var eSocket in entryOutputs)
                {
                    var callInputSocket = node.Sockets.FirstOrDefault(s => s.Type == SocketType.Input && s.Name == eSocket.Name);
                    if (callInputSocket != null)
                    {
                        string val = ctx.Resolve(node, eSocket.Name, "\"\"");
                        // Sanitize so a parameter renamed to e.g. "User Name"
                        // produces a valid identifier; the read side in
                        // ScriptExporter.ResolveOutputFromNode applies the same
                        // sanitizer so both halves of the binding converge.
                        ctx.Emit($"{prefix}global.{slotPrefix}_{ctx.SanitizeIdentifier(eSocket.Name)} = {val}");
                    }
                    else
                    {
                        // Silent name drift — Entry exposes a parameter the Call doesn't have.
                        // Surface it so authors notice the broken contract.
                        ctx.Emit($"{prefix}# WARNING: Macro '{macroName}' Macro.Entry output '{eSocket.Name}' is unbound — missing socket on the Macro.Call. Refresh the Macro.Call (re-open the macro editor) or rename the socket to match.");
                    }
                }
            }

            string subScript = ctx.ExportMacroSubGraph(macro.Graph, slotPrefix);
            var subLines = subScript.Split(new[] { "\r\n", "\r", "\n" }, System.StringSplitOptions.None)
                                    .Skip(3)
                                    .Select(l => string.IsNullOrWhiteSpace(l) ? l : prefix + l);
            foreach (var l in subLines)
                ctx.AppendRawLine(l);

            ctx.Emit($"{prefix}# END MACRO: {macroName}");
            ctx.FollowNamed(node, "Flow", indent);
        }
    }

    /// <summary>
    /// Process.Spawn — emits a `process_spawn(stableId, instanceId, "Title"):`
    /// block that the engine recognizes as the unified detached-async primitive
    /// (see <c>ScriptEngine.HandleProcessSpawnBlock</c>). The body of
    /// the named Process is inline-expanded inside the block, mirrored
    /// on MacroCallHandler — same slot-prefix isolation pattern, but the engine
    /// runs the indented body on a fresh Task with isolated AsyncLocal vars and
    /// its own CancellationTokenSource. The parent script flows past Done
    /// immediately; Process.Terminate(InstanceId) cancels the body later.
    ///
    /// Per-call-site instance id: minted as a per-node global so two
    /// Process.Spawn nodes in the same script don't collide on the engine's
    /// _spawnedProcesses dictionary keys. The InstanceId output socket
    /// resolves to that same per-node global so downstream Process.Terminate
    /// nodes can wire it.
    /// </summary>
    internal sealed class ProcessSpawnHandler : IExporterHandler
    {
        public string NodeTitle => "Process.Spawn";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string processId   = node.GetAttr("ProcessId", "");
            string processName = node.GetAttr("ProcessName", "Process");

            var process = string.IsNullOrEmpty(processId)
                ? null
                : ctx.Graph.Processes.FirstOrDefault(p => p.ProcessId == processId);

            // Per-call-site instance var so the InstanceId output resolves and
            // two Process.Spawn nodes don't share the same instance id.
            string idSlot = $"global._proc_inst_{ctx.IdPrefix(node)}";
            ctx.NodeResultVars[$"{node.Id}_InstanceId"] = $"{{{idSlot}}}";

            // Mint a fresh instance id literal at script-build time. The
            // engine ALSO falls back to Guid.NewGuid() when the instance arg
            // is empty, so we don't strictly need a value here — but a stable
            // per-node literal makes log lines reproducible across runs.
            string instanceLiteral = "\"" + Guid.NewGuid().ToString() + "\"";
            ctx.Emit($"{prefix}{idSlot} = {instanceLiteral}");

            if (process == null)
            {
                ctx.Emit($"{prefix}# PROCESS_SPAWN:{processName} (not found — body skipped)");
                ctx.FollowNamed(node, "Done", indent);
                return;
            }

            ctx.Emit($"{prefix}# BEGIN PROCESS: {processName}");

            string stableProcessId = processId.Replace("-", "");
            string slotPrefix = $"_process_{stableProcessId}_{ctx.IdPrefix(node)}";

            // Var-in: walk Process.Entry's output sockets and emit one
            // assignment per socket BEFORE the spawn block. The body's
            // ResolveOutputFromNode arm for Process.Entry reads the same
            // global._process_<id>_<arg> slot via _macroContextId, so the
            // write/read pair matches. Mirrors MacroCallHandler.
            var entryNode = process.Graph.Nodes.FirstOrDefault(n => n.Title == "Process.Entry");
            if (entryNode != null)
            {
                var entryOutputs = entryNode.Sockets
                    .Where(s => s.Type == SocketType.Output && !s.IsPlaceholder && s.Name != "Flow")
                    .ToList();
                foreach (var eSocket in entryOutputs)
                {
                    var callInputSocket = node.Sockets.FirstOrDefault(s => s.Type == SocketType.Input && s.Name == eSocket.Name);
                    if (callInputSocket != null)
                    {
                        string val = ctx.Resolve(node, eSocket.Name, "\"\"");
                        ctx.Emit($"{prefix}global.{slotPrefix}_{ctx.SanitizeIdentifier(eSocket.Name)} = {val}");
                    }
                    else
                    {
                        // Socket-name drift between Process.Entry and
                        // Process.Spawn — same warning shape as MacroCallHandler.
                        ctx.Emit($"{prefix}# WARNING: Process '{processName}' Process.Entry output '{eSocket.Name}' is unbound — missing socket on the Process.Spawn. Refresh the Process.Spawn (re-open the editor) or rename the socket to match.");
                    }
                }
            }

            // Header: process_spawn("<stableId>", {<idSlot>}, "<title>"):
            ctx.Emit($"{prefix}process_spawn(\"{processId}\", {{{idSlot}}}, \"{processName}\"):");

            // Inline-expand the process body at indent+1, just like
            // MacroCallHandler. ExportMacroSubGraph re-uses the macro
            // sub-graph exporter which is graph-shape-agnostic — works for
            // a process graph too.
            string subScript = ctx.ExportMacroSubGraph(process.Graph, slotPrefix);
            string innerPrefix = prefix + "    ";
            var subLines = subScript.Split(new[] { "\r\n", "\r", "\n" }, System.StringSplitOptions.None)
                                    .Skip(3)
                                    .Select(l => string.IsNullOrWhiteSpace(l) ? l : innerPrefix + l);
            foreach (var l in subLines)
                ctx.AppendRawLine(l);

            ctx.Emit($"{prefix}# END PROCESS: {processName}");
            ctx.FollowNamed(node, "Done", indent);
        }
    }

    /// <summary>
    /// Process.Entry — flow-marker, no runtime emit. Entry into a process body
    /// is driven by ProcessEventNode (special-cased in ScriptExporter), which
    /// follows this node's Flow output at indent 0. Reaching Entry through a
    /// downstream flow path is a graph-shape mistake (entry can't be reached
    /// from inside its own body); register the title here so ProcessNode's
    /// unknown-handler throw doesn't trip on graphs the validator hasn't yet
    /// caught.
    /// </summary>
    internal sealed class ProcessEntryHandler : IExporterHandler
    {
        public string NodeTitle => "Process.Entry";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            // No-op: Process.Entry's role is fulfilled by ProcessEventNode's
            // entry-point handling. ProcessNode reaching it via flow is rare
            // and a no-op is the safest behaviour.
            ctx.FollowNamed(node, "Flow", indent);
        }
    }

    /// <summary>
    /// Process.Exit — flow-terminator marker. Reaching Process.Exit ends the
    /// process body cleanly; we emit a trace comment so the .phx export
    /// records the exit point but follow nothing further. The engine's
    /// `process_spawn(...):` block ends naturally when ExecuteBlock runs
    /// out of body lines, which is the canonical clean-completion path.
    /// </summary>
    internal sealed class ProcessExitHandler : IExporterHandler
    {
        public string NodeTitle => "Process.Exit";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            ctx.Emit($"{prefix}# Process.Exit reached — process body completes.");
            // Deliberately no FollowNamed — Exit is a terminator.
        }
    }

}