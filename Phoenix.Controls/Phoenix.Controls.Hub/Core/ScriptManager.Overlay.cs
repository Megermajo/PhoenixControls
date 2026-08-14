using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: overlay.* command registrations — the AUTHOR-facing half of the
    // Overlay Live Channel (OverlayLiveStore). Hub's own producers publish their
    // reserved key roots (timer.* / counter.* / loyalty.* / caption.* / goal.*)
    // straight into the store; these two commands give a .phx script the same rails
    // with no special treatment, so a widget binds to an author's key exactly as it
    // binds to a tool's.
    //
    // ★ goal.* is the one reserved root that is NOT a tool's. Its producer is
    // GoalChannelProducer (Core/GoalChannelProducer.cs), hooked into
    // ExecuteGenericEventAsync's always-on pre-guard region: Twitch's three
    // channel-goal events and its three charity-CAMPAIGN events become
    // goal.<kind>.{current,target,progress,label}, the exact keys the READER half
    // already bound (NodeTemplates' goal constants in Visualist.Engine and
    // compositor.js's mirrored literals). goal.tip.* remains unproduced — that half
    // belongs to C1's donation ingestion, a SECOND publisher into the SAME key family,
    // which is what "one goal model" means: one key contract, publishers wherever the
    // data lives. A bespoke second root would split one model in two.
    //
    // An author may still publish a goal.* key by hand through overlay.publish below —
    // the channel allows it on purpose (no reservation enforcement: a temp store, last
    // write wins, provenance makes any fight visible). Note the case rule differs by
    // author: a custom_<slug> an author typed is published verbatim, while the Twitch
    // producer lower-cases the machine GoalType it derives — the two-case rule spelled
    // out on NodeTemplates.GoalCustomKindPrefix.
    //
    //   overlay.publish(Key, Value) — flow node Overlay.Publish. Writes one literal
    //       key. Never marks anything dirty by itself: the store's publisher-side gate
    //       means a key no layer subscribed to costs one dictionary write and never
    //       reaches a socket.
    //   overlay.get(Key)            — pure-data node Overlay.Get. Reads the current
    //       value back as a string; the engine round-trips the non-null return as the
    //       node's inline value.
    //
    // Both take their args through CurrentBoundArgs with a positional fallback and
    // bare-quote stripping, and both catch → log → surface `result.overlay_error`
    // rather than letting a store fault tear the script down (the ScriptManager.State.cs
    // contract).
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        /// <summary>
        /// Provenance prefix for every script-originated publish. The store's
        /// <c>Source</c> field is what makes a same-key fight between two producers
        /// visible instead of mysterious, so the script's own file name has to travel
        /// with the write.
        /// </summary>
        private const string ScriptSourcePrefix = "script:";

        /// <summary>
        /// Upper bound on one published value's length, in characters.
        ///
        /// <para>Generous by design — the largest thing a tool publishes today is a
        /// leaderboard array, comfortably inside this — so no legitimate overlay value
        /// is affected. It exists because the command had no bound at all: the store
        /// re-serializes a value on every publish to compare it against the previous
        /// one (the coalescing gate), so a script looping a large string paid that cost
        /// per call AND could grow the store without limit, on a path a viewer can
        /// trigger through chat.</para>
        ///
        /// <para>Companion to <c>OverlayLiveStore.MaxStoreKeys</c>: that one bounds how
        /// MANY keys exist, this one bounds how BIG one is. Neither alone bounds the
        /// store.</para>
        /// </summary>
        internal const int MaxPublishedValueChars = 64 * 1024;

        /// <summary>
        /// Clears <c>result.overlay_error</c> after a successful call.
        ///
        /// <para>Without this the var is write-only-on-failure: one failed publish left it
        /// set for the rest of the execution, so a later successful call still read as an
        /// error and a script branching on it took the failure arm forever. The var being
        /// SHARED by publish and get makes the leak worse — a failed get would poison a
        /// subsequent publish's branch.</para>
        ///
        /// <para><b>This is deliberately NOT what <c>state.*</c> does.</b>
        /// <c>result.state_set_error</c> / <c>result.state_get_error</c> are written only
        /// inside their catch blocks and never cleared, so they carry the same leak. The
        /// overlay surface is modelled on state.* for the SHAPE of the contract (a
        /// branchable result var instead of tearing the script down), not for its
        /// clearing behaviour, which is a bug there rather than a precedent. Do not
        /// "align" this back to state.*; align state.* forward to this.</para>
        ///
        /// <para><b>Bounded caveat inside <c>parallel_begin</c>.</b> Every
        /// <c>SetLocalResultVar</c> tags its key for merge-back to the parent, and the
        /// merge is last-writer-wins across branches. So the clear is skipped unless this
        /// execution actually has an error recorded — otherwise a successful publish in
        /// one branch would tag an empty string and erase a sibling branch's failure at
        /// the join, which is strictly worse than the leak being fixed.</para>
        /// </summary>
        private static void ClearOverlayError(ScriptEngine engine)
        {
            try
            {
                // Read-before-write is what keeps the parallel_begin join honest: a branch
                // that never failed must not write this key at all, because writing it is
                // what enrols it in the merge.
                if (string.IsNullOrEmpty(engine.GetExecutionVar("result.overlay_error"))) return;
                engine.SetLocalResultVar("result.overlay_error", string.Empty);
            }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Recorded when the executing script has no identity — reachable only from a
        /// hand-driven engine (a test harness, or a command invoked outside
        /// ExecuteScriptAsync). Deliberately a visible sentinel rather than an invented
        /// name: "script:?" in the provenance list is a question, a fabricated file name
        /// would be a wrong answer.
        /// </summary>
        internal const string UnknownScriptSource = ScriptSourcePrefix + "?";

        private void RegisterOverlayCommands()
            => RegisterOverlayCommandsOn(_engine, OverlayLiveStore.Instance);

        /// <summary>
        /// Registers the two overlay.* commands against an explicit engine and store.
        /// Production binds <c>_engine</c> + <see cref="OverlayLiveStore.Instance"/>;
        /// the test suite binds its own isolated pair, because sharing the production
        /// singleton across tests is the cross-test coupling the store's internal ctor
        /// exists to avoid.
        /// </summary>
        internal static void RegisterOverlayCommandsOn(ScriptEngine engine, OverlayLiveStore store)
        {
            if (engine is null) throw new ArgumentNullException(nameof(engine));
            if (store  is null) throw new ArgumentNullException(nameof(store));

            // Once-per-(complaint, script) ledger, same shape and same reasoning as
            // RegisterRetiredCommandsOn's: scoped to this registration call rather than
            // to the process, locked because scripts execute concurrently, and the
            // GlobalLogger call always made OUTSIDE the lock.
            //
            // ★ Why this is needed here at all. A freshly-dropped Overlay.Publish node
            // ships with NO Key — the template carries no default — and the empty-key
            // report is LogicExecution, which is visible at the default log level. Wire
            // that node to Chat.Message and the streamer gets one SystemLog row per chat
            // message, all stream, for a single unconfigured node. The two sibling paths
            // (the store's own drop, the retired-command shims) both dedupe; this one
            // was the outlier.
            var notedGate = new object();
            var noted = new HashSet<string>(StringComparer.Ordinal);
            bool NoteOnce(string tag)
            {
                lock (notedGate) return noted.Add(tag + "\0" + ScriptDisplayNameFor(engine));
            }

            // overlay.publish(key, value) — void; flow continues through the node's Done
            // output. No expectedInterval is passed: an author write is one-shot, and the
            // store treats a null interval as "never goes Stale", which is the honest
            // reading of a value nobody promised to refresh. A tool that later writes the
            // same key brings its own cadence along with it.
            engine.RegisterCommand("overlay.publish", async (args) =>
            {
                var bound = engine.CurrentBoundArgs;
                string key   = StripBareQuotes(bound?.GetOrDefault<string>("Key",   ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0)).Trim();
                string value = StripBareQuotes(bound?.GetOrDefault<string>("Value", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1));

                if (key.Length == 0)
                {
                    // The store drops an empty key and logs it ONCE per process, which is
                    // right for its own diagnostics but leaves a repeatedly-broken script
                    // silent. Report it once per SCRIPT — the misconfiguration is a
                    // property of the graph, not of the invocation, so the second row
                    // carries no information the first did not.
                    //
                    // The result var is still set on EVERY call: it is the script's own
                    // branchable signal and deduplicating it would break a `if
                    // {result.overlay_error}` guard on the second call onward.
                    if (NoteOnce("publish:empty-key"))
                        GlobalLogger.Log(
                            "overlay.publish skipped — Key is empty. Live-channel keys are literal lowercase dotted names, "
                            + "e.g. 'alert.headline'. A freshly-dropped Overlay.Publish node has no Key until you type one. "
                            + "Logged once per script file.",
                            "Script", LogLevel.LogicExecution);
                    try { engine.SetLocalResultVar("result.overlay_error", "empty key"); } catch { /* best-effort */ }
                    return null;
                }

                if (value.Length > MaxPublishedValueChars)
                {
                    // Refuse rather than truncate. A truncated JSON blob is worse than no
                    // value at all — it reaches the widget as syntactically broken data
                    // the reader cannot diagnose, whereas a refusal leaves the previous
                    // (valid) value standing and says why.
                    // Keyed per KEY as well as per script, because the message names the
                    // key: deduping on the script alone would report the first oversize
                    // key and then silently refuse every other one for the rest of the
                    // session. Same rule the sibling ledgers follow — RetiredCommands
                    // keys per command because each message names its own replacement,
                    // and the store's key cap keys per source for exactly this reason.
                    // Bounded by the key space, which the store's own cap already bounds.
                    if (NoteOnce("publish:oversize:" + key))
                        GlobalLogger.Log(
                            $"overlay.publish('{key}') refused — the value is {value.Length} characters, over the "
                            + $"{MaxPublishedValueChars}-character limit. Live-channel values are small display values; "
                            + "publish a reference (an id, a URL) and let the widget fetch the payload. "
                            + "Logged once per key per script file.",
                            "Script", LogLevel.LogicExecution);
                    // Names the key: the log is deduped, so on a second offending key the
                    // result var is the only thing the script can see.
                    try { engine.SetLocalResultVar("result.overlay_error", $"value too large: {key}"); } catch { /* best-effort */ }
                    return null;
                }

                try
                {
                    // PublishString, ALWAYS — never a "does this look numeric?" sniff. The
                    // script→widget boundary is string-only, so inferring would silently
                    // turn "007" into 7 and "1e3" into 1000. Coercion lives at the reader
                    // (Var.Live's Number pin), where the author has already declared intent
                    // by choosing a pin type. Tool-published keys keep their real JSON
                    // types, so that pin is exact for them and best-effort for author text.
                    store.PublishString(key, value, ScriptSourceFor(engine));
                    ClearOverlayError(engine);
                }
                catch (Exception ex)
                {
                    GlobalLogger.Log($"overlay.publish('{key}') failed: {ex.Message}", "Script", LogLevel.CriticalError);
                    try { engine.SetLocalResultVar("result.overlay_error", ex.Message); } catch { /* best-effort */ }
                }
                return null;
            });

            // overlay.get(key) — pure data: the exporter emits the call inline and the
            // engine captures the returned string as the node's Value output.
            engine.RegisterCommand("overlay.get", async (args) =>
            {
                var bound = engine.CurrentBoundArgs;
                string key = StripBareQuotes(bound?.GetOrDefault<string>("Key", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0)).Trim();

                try
                {
                    string read = ReadOverlayValueForScript(store, key);
                    ClearOverlayError(engine);
                    return read;
                }
                catch (Exception ex)
                {
                    GlobalLogger.Log($"overlay.get('{key}') failed: {ex.Message}", "Script", LogLevel.CriticalError);
                    try { engine.SetLocalResultVar("result.overlay_error", ex.Message); } catch { /* best-effort */ }
                    // Same fallback shape as state.get: an empty read keeps the script
                    // running instead of propagating a store fault into the interpreter.
                    return string.Empty;
                }
            });
        }

        /// <summary>
        /// The provenance tag for a publish made from <paramref name="engine"/>'s current
        /// execution. <see cref="ScriptEngine.ScriptFile"/> is the identity ScriptManager
        /// already stamps before every <c>ExecuteScriptAsync</c> call and the same one the
        /// debug-trace path marshals to Architect through <c>OnNodeExecuted</c>, so
        /// reusing it keeps one notion of "which script did this".
        /// </summary>
        private static string ScriptSourceFor(ScriptEngine engine)
        {
            string file = engine.ScriptFile ?? string.Empty;
            file = file.Trim();
            return file.Length == 0 ? UnknownScriptSource : ScriptSourcePrefix + file;
        }

        /// <summary>
        /// The <c>overlay.get</c> string contract — the round-trip an author hits first,
        /// so it is spelled out rather than left to whatever <c>ToString()</c> happens to
        /// do:
        /// <list type="bullet">
        ///   <item>absent key ⇒ the empty string (a script has no "missing"; liveness is
        ///     exposed separately through the reader's State pin, from
        ///     <see cref="OverlayLiveStore.GetState"/>);</item>
        ///   <item>JSON string ⇒ its CONTENT, never the quoted form — an author who
        ///     published "hello" reads back <c>hello</c>, not <c>"hello"</c>;</item>
        ///   <item>JSON number / bool ⇒ its literal invariant text (<c>0.62</c>,
        ///     <c>true</c>) — culture can never leak in, because the text comes from the
        ///     JSON writer and not from a numeric ToString;</item>
        ///   <item>JSON null ⇒ the empty string, matching the absent case: the script
        ///     world has no null, and the literal text "null" would read as data;</item>
        ///   <item>JSON array / object (e.g. <c>loyalty.leaderboard</c>) ⇒ its compact
        ///     JSON text, which is the only lossless scalar spelling and is directly
        ///     consumable by <c>http.parse_json</c>.</item>
        /// </list>
        /// Staleness is deliberately not represented here: a stale value is still the
        /// last known value, and hiding it behind an empty string would make a paused
        /// producer indistinguishable from one that never published.
        /// </summary>
        internal static string ReadOverlayValueForScript(OverlayLiveStore store, string key)
        {
            var entry = store.Get(key);
            if (entry is null) return string.Empty;

            JsonNode? node = entry.Value;
            if (node is null) return string.Empty;

            // Switch on the JSON value KIND rather than probing with TryGetValue<string>:
            // the kind is the wire truth, and it keeps a number from ever being coaxed
            // into a string conversion by the node's own generic accessor.
            return node.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.String    => node.GetValue<string>(),
                System.Text.Json.JsonValueKind.Null      => string.Empty,
                System.Text.Json.JsonValueKind.Undefined => string.Empty,
                _                                        => node.ToJsonString(),
            };
        }
    }
#pragma warning restore CS1998
}
