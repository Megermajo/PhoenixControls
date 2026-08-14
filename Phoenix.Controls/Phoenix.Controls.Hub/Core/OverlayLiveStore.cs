using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>Liveness of a single live-channel key, as seen by <see cref="OverlayLiveStore.GetState"/>.</summary>
    public enum LiveState
    {
        /// <summary>Nothing has ever been published under this key.</summary>
        Missing,
        /// <summary>Published, and either interval-less or inside its freshness window.</summary>
        Active,
        /// <summary>Published with an expected interval, but nothing has arrived for 3 intervals.</summary>
        Stale,
    }

    /// <summary>
    /// One key's current value in the Overlay Live Channel.
    ///
    /// <paramref name="Value"/> is the publisher's own <see cref="JsonNode"/> instance — it is
    /// stored as-is (no defensive copy) and MUST be treated as read-only by everyone, including
    /// the publisher, after the <see cref="OverlayLiveStore.Publish"/> call returns. Frame builds
    /// hand out <c>DeepClone()</c>s precisely because a node carries a parent pointer and cannot
    /// be re-parented.
    ///
    /// <paramref name="ValueJson"/> is the cached serialization of <paramref name="Value"/>. It
    /// exists so coalescing (the check that makes a 1 Hz publisher of 14 unchanged fields cost
    /// zero wire bytes) is an O(1) string compare instead of a re-serialize per publish.
    /// </summary>
    public sealed record LiveEntry(
        string    Key,
        JsonNode? Value,          // null is a legal published value
        string    ValueJson,      // cached Value?.ToJsonString() ?? "null" — O(1) coalescing compare
        long      Seq,
        string    Source,         // "tool:Timer" | "script:<file>" | "process:<id>" | "test"
        DateTime  LastWriteUtc,
        TimeSpan? ExpectedInterval);   // null => never goes Stale

    /// <summary>
    /// OverlayLiveStore — the Hub half of the Overlay Live Channel: ONE key/value channel that
    /// carries every piece of ambient data an overlay widget can bind to (timers, counters,
    /// loyalty, captions, goals, and anything an author publishes via <c>overlay.publish</c>).
    /// "No special treatment" — a Hub tool's key and an author's key ride the exact same rails.
    ///
    /// Shape of the traffic it replaces: today each data family invents its own broadcast frame
    /// (TIMER_UPDATE / COUNTER_UPDATE / CAPTION_UPDATE …) and pushes it to every browser on every
    /// change. This store instead keeps the authoritative value, learns from each browser's
    /// LIVE_HELLO which keys that layer actually reads, and emits one coalesced LIVE_PATCH per
    /// layer per <see cref="PumpIntervalMs"/> containing only the keys that both changed AND are
    /// subscribed. A key nobody binds costs one dictionary write and nothing else.
    ///
    /// Pillar rule: this lives in the Hub runtime — the only process that executes logic and owns
    /// the HUD server. Architect/Visualist never reference it; the browser sees it only through
    /// the three wire frames (LIVE_HELLO inbound, LIVE_SNAPSHOT / LIVE_PATCH outbound), all of
    /// which ride <c>HUDServer.SendToLayerAsync</c> on <c>/hud/&lt;layerId&gt;</c>.
    /// </summary>
    public sealed class OverlayLiveStore
    {
        /// <summary>
        /// THE single cadence constant for the channel. 1 Hz matches the fastest producer we have
        /// (the TimerService tick), so a faster pump would only ship duplicate frames. Raising it
        /// to 10 Hz is a knob, not a rewrite — but the browser-side in-flight guard must land
        /// first, otherwise a patch arriving mid-render stacks render passes.
        /// </summary>
        public const int PumpIntervalMs = 1000;

        /// <summary>
        /// Hard ceiling on DISTINCT keys the channel will hold. Not a design limit — a
        /// real deployment uses tens (the tool families plus whatever an author
        /// publishes by hand). It exists because the store has no remove API and no
        /// TTL, so a script publishing a computed key per viewer or per message would
        /// otherwise grow it without bound, and two lock-held full-store walks (the 1 Hz
        /// staleness scan and the 30-tick resync sweep) grow with it. Sized well above
        /// any legitimate use so hitting it means an authoring bug, which is exactly
        /// what the once-per-process CriticalError says.
        /// </summary>
        public const int MaxStoreKeys = 4096;

        /// <summary>Per-layer exact-key subscription cap — a hostile/buggy client guard, not a design limit.</summary>
        public const int MaxSubscriptionKeys = 512;

        /// <summary>Per-layer prefix subscription cap. Prefixes are O(n) per publish, so the cap is tighter.</summary>
        public const int MaxSubscriptionPrefixes = 64;

        /// <summary>
        /// How long a connected browser may stay silent before we call out the most likely cause
        /// (a stale cached compositor.js in the OBS Browser Source). Diagnostic only — nothing is
        /// disconnected or degraded when it elapses.
        /// </summary>
        public const int HelloDeadlineMs = 5000;

        /// <summary>A key goes Stale after this many missed expected intervals.</summary>
        public const int StaleIntervalMultiplier = 3;

        /// <summary>
        /// Every Nth pump tick re-ships every subscribed key to every subscribed layer, whether it
        /// changed or not. This is a required repair path, not belt-and-braces:
        /// <c>HUDServer.SendToLayerAsync</c> resolves on ENQUEUE into a per-socket bounded channel
        /// whose <c>FullMode</c> is DropOldest, and the eviction is merely counted — never surfaced
        /// to us. So a frame can be silently destroyed after we already cleared the dirty set, and
        /// coalescing then guarantees the producer's next identical publish does NOT re-dirty. The
        /// key stays wrong until its value happens to change again, which for
        /// <c>loyalty.leaderboard</c> or a one-shot <c>overlay.publish</c> key means indefinitely.
        /// The bespoke per-tool pushes this channel replaces re-sent the whole value on every
        /// change, so a drop there cost exactly one frame; coalescing converts the same drop into
        /// permanent wrongness, and this tick is what pays that back. It also heals a subscription
        /// lost to <c>OnLayerRemoved</c> and any dispatch the epoch gate skipped.
        ///
        /// Threading <c>TryEnqueue</c>'s boolean back through <see cref="LayerDispatchDelegate"/>
        /// would be the precise fix, but that delegate is shared with <c>LayerRuntime</c> — a future
        /// improvement, deliberately not widened here.
        /// </summary>
        public const int ResyncIntervalTicks = 30;

        /// <summary>
        /// Production instance. Hub's bootstrapper wires <see cref="Dispatcher"/> and calls
        /// <see cref="StartPump"/>; every tool/script publish path resolves THIS instance so all
        /// producers share one key namespace.
        /// </summary>
        public static OverlayLiveStore Instance { get; } = new();

        /// <summary>
        /// Internal so the only way an outside assembly can reach the channel is
        /// <see cref="Instance"/>, while the test project (a friend assembly) constructs its own
        /// isolated stores. Sharing a singleton across tests is the flake class this repo already
        /// pays for with DB.Instance — the channel does not repeat it.
        /// </summary>
        internal OverlayLiveStore() { }

        /// <summary>
        /// Outbound seam → <c>HUDServer.SendToLayerAsync</c>, wired by HubBootstrapper next to
        /// <c>LayerRuntime.Instance.Dispatcher</c>. Never <c>Bus.BroadcastAsync</c> — the bus does
        /// not route Hub→Hub, so a bus broadcast would reach no browser at all.
        ///
        /// Null until wired: publishes still land in the store and still accumulate per-layer
        /// dirty sets, so the first tick after wiring ships the accumulated truth rather than
        /// losing it.
        /// </summary>
        public LayerDispatchDelegate? Dispatcher { get; internal set; }

        /// <summary>
        /// Optional SOCKET-addressed outbound seam → <c>HUDServer.SendToSocketAsync</c>,
        /// wired beside <see cref="Dispatcher"/>. Used for exactly one frame: the
        /// <c>LIVE_SNAPSHOT</c> that answers a <c>LIVE_HELLO</c>.
        ///
        /// <para><b>Why the layer-wide seam is wrong for that one frame.</b> A HELLO is
        /// per-SOCKET — every browser source sends its own on connect, and OBS re-opens
        /// one on every scene flip — but <see cref="Dispatcher"/> fans to every socket on
        /// the layer. The snapshot carries <c>changedKeys = null</c>, i.e. "repaint
        /// everything", so one browser connecting forced a full repaint on every OTHER
        /// browser already showing that layer. With N sources on one <c>.phxlayer</c>,
        /// one save is N² full render passes on the OBS render thread. No wrong pixels;
        /// real work, at exactly the moment the streamer is editing.</para>
        ///
        /// <para>Null-safe by construction: when this is unset (a test store, or a Hub
        /// built before the seam existed) <c>HandleHelloAsync</c> falls back to
        /// <see cref="Dispatcher"/> and behaves exactly as it did — correct, just
        /// noisier. The reply target itself is an opaque token the transport hands back;
        /// the store never learns what a socket is.</para>
        ///
        /// <para><b>The sibling sockets are not left behind, and that took two things.</b>
        /// A subscription is per-LAYER (<c>_subs[id] = sub</c>), so a second browser's
        /// HELLO speaks for every socket on that layer, and the old layer-wide fan quietly
        /// covered for it. With an addressed snapshot, <c>HandleHelloAsync</c> must handle
        /// the siblings explicitly: it PRESERVES the layer's pending dirty set (dropping
        /// it would strand every key that was dirty at HELLO time), and it re-dirties the
        /// whole matched set when the subscription genuinely CHANGED rather than merely
        /// being re-announced. Both arrive as an ordinary <c>LIVE_PATCH</c> on the next
        /// tick.</para>
        ///
        /// <para>To be accurate about the win: a patch is not cheaper to RENDER than a
        /// snapshot — a changed key repaints the widgets bound to it either way. What is
        /// saved is the <i>full</i> repaint a snapshot forces (<c>changedKeys = null</c>,
        /// i.e. every bound widget on the layer) landing on every sibling every time any
        /// socket connects. A reconnect that changes nothing now costs the siblings
        /// nothing at all, which on an OBS scene flip is the difference that matters.</para>
        /// </summary>
        public Func<object, object, CancellationToken, Task>? SocketDispatcher { get; internal set; }

        // ── State ─────────────────────────────────────────────────────────────
        // One monitor guards store + dirty + subscriptions + the once-log ledger.
        // Publishes arrive from timer ticks, chat continuations and DB continuations, so the
        // guard has to be real — but at 1 Hz with a handful of keys a single lock is both correct
        // and cheaper than any striped/concurrent alternative. Two absolute rules inside it:
        // never take a DB lock, never await. Frames are built after the monitor is released.
        private readonly object _lock = new();

        private readonly Dictionary<string, LiveEntry> _store = new(StringComparer.Ordinal);

        // layerId -> keys changed since that layer's last emitted frame.
        private readonly Dictionary<string, HashSet<string>> _dirty = new(StringComparer.Ordinal);

        // layerId -> what that layer told us it reads (last LIVE_HELLO wins, wholesale).
        private readonly Dictionary<string, LayerSubscription> _subs = new(StringComparer.Ordinal);

        // key -> the ONLY layers that key may be delivered to, or absent when the key goes to
        // every subscribing layer (the overwhelmingly common case — every tool key and every
        // author key is unscoped).
        //
        // This exists for one live setting: AppConfig.LiveCaptionsAllowedLayers. Before the
        // channel, a non-empty allowlist meant CAPTION_UPDATE went ONLY to the named layers,
        // and a streamer who allowlisted just their `captions` layer did it precisely to keep
        // speech off a shared/restream layer. Routing per subscribed key does NOT subsume that:
        // the layer still subscribes caption.*, so without a scope the captions would start
        // painting exactly where the streamer forbade them. Publishing a scoped key still writes
        // the store — Get/GetState/Provenance/overlay.get see it, same as the subscription gate
        // already does for a key nobody reads — only the DELIVERY set is narrowed.
        //
        // Scope is last-write-wins alongside Source, for the same reason: the channel's rule is
        // that the latest writer's intent governs, and provenance is what makes a same-key fight
        // visible.
        private readonly Dictionary<string, HashSet<string>> _scopes = new(StringComparer.Ordinal);

        // layerId -> monotonic subscription generation, bumped by every accepted LIVE_HELLO.
        // The pump captures it when it snapshots a layer's dirty set and re-checks it immediately
        // before dispatching, so a HELLO that raced the dispatch loop cannot be undone by a patch
        // built from the pre-HELLO world.
        //
        // Deliberately NOT cleared by ClearLayer: keeping it monotonic for process life is what
        // rules out ABA — a reconnect that reset the counter to 1 could match an in-flight capture
        // from the PREVIOUS session and let a superseded patch through.
        private readonly Dictionary<string, long> _epoch = new(StringComparer.Ordinal);

        // key -> the liveness verdict Hub last shipped (or last decided) for that key.
        //
        // This ledger is why staleness can travel at all. The obvious alternative — ship
        // LastWriteUtc plus the interval and let the browser's clock decide — breaks against
        // coalescing: a producer republishing an identical value refreshes LastWriteUtc WITHOUT
        // shipping a frame, so the browser's copy of the timestamp ages and it would declare a
        // perfectly healthy key stale. Hub is the only party that can see that refresh, so Hub owns
        // the verdict and the wire carries it as the entry's "s" field.
        private readonly Dictionary<string, LiveState> _lastState = new(StringComparer.Ordinal);

        // Layers that have EVER sent a LIVE_HELLO. Sticky for process life: it is the negative
        // half of the stale-compositor diagnostic ("connected but never spoke the protocol").
        private readonly HashSet<string> _helloSeen = new(StringComparer.Ordinal);

        // Layers with a live connection we're currently expecting a HELLO from. Cleared by
        // ClearLayer so the next connect re-arms cleanly.
        private readonly HashSet<string> _connectionNoted = new(StringComparer.Ordinal);

        // One ledger for every "log this exactly once" diagnostic (subscription-cap truncation,
        // odd proto, HELLO deadline, unserializable value). One mechanism instead of four flags,
        // and every token is namespaced by its condition so they can't collide.
        private readonly HashSet<string> _onceLogged = new(StringComparer.Ordinal);

        // Process-wide sequence counter, bumped ONLY on a real value change. Static because the
        // sequence is a property of the process's view of the world, not of one store instance —
        // a browser that reconnects to a fresh store must not see the sequence run backwards.
        private static long _seq;

        // Pump lifecycle — same shape as TimerService's tick loop so shutdown ordering is one
        // pattern across the Hub runtime.
        private readonly object _loopGate = new();
        private bool _loopStarted;
        private CancellationTokenSource? _loopCts;
        private Task? _loopTask;

        // Pump tick counter, sole purpose being the ResyncIntervalTicks modulo. Interlocked rather
        // than folded into _lock because it is touched exactly once per tick and by nobody else.
        private long _tickCount;

        /// <summary>
        /// The running pump loop, or null when stopped. Internal test seam — reference identity
        /// across two <see cref="StartPump"/> calls is what proves the idempotence guard is real; a
        /// second loop would double every layer's frame rate and leak a CancellationTokenSource
        /// that <see cref="ShutdownAsync"/> can never reach.
        /// </summary>
        internal Task? LoopTaskForTests { get { lock (_loopGate) return _loopTask; } }

        /// <summary>
        /// One layer's subscription. Prefixes are stored in their COMPARE form — the trailing
        /// <c>*</c> is stripped at parse time, so <c>"timer.main.*"</c> becomes <c>"timer.main."</c>
        /// and the per-publish match is a bare Ordinal StartsWith. Keeping the dot in the compare
        /// form is what makes <c>timer.main2.progress</c> a non-match.
        /// </summary>
        private sealed class LayerSubscription
        {
            public readonly HashSet<string> Keys     = new(StringComparer.Ordinal);
            public readonly HashSet<string> Prefixes = new(StringComparer.Ordinal);
        }

        /// <summary>
        /// True when two subscriptions read exactly the same thing.
        ///
        /// <para>Used to tell a REANNOUNCE (an OBS scene flip re-opening the socket with an
        /// unchanged key set — the overwhelmingly common case) from a genuine change (the
        /// author saved a graph that binds different keys). Only the latter has to disturb
        /// the layer's other sockets.</para>
        ///
        /// <para>Set comparison, not sequence: the browser builds its key list by walking
        /// widget graphs, so the same bindings can arrive in a different order between
        /// two page loads and that is not a change.</para>
        /// </summary>
        private static bool SameSubscription(LayerSubscription a, LayerSubscription b)
            => a is not null && b is not null
            && a.Keys.SetEquals(b.Keys)
            && a.Prefixes.SetEquals(b.Prefixes);

        /// <summary>
        /// One frame entry in flight: the stored value plus the liveness verdict decided for it.
        /// The verdict is computed INSIDE <c>_lock</c> (that is where <c>_lastState</c> lives) and
        /// carried out here, so the frame build itself — DeepClone and all — still runs unlocked.
        /// </summary>
        private readonly record struct FrameEntry(LiveEntry Entry, LiveState State);

        // Trim/empty normalisation shared by every public entry point. Takes string? so callers
        // passing a non-nullable string produce no nullable-flow noise.
        private static string Norm(string? s) => string.IsNullOrWhiteSpace(s) ? string.Empty : s.Trim();

        // Claims a one-shot diagnostic token. MUST be called with _lock held; the actual
        // GlobalLogger.Log call belongs OUTSIDE the monitor (the logger takes its own locks and
        // fans out to synchronous subscribers — holding ours across that is a lock-ordering trap).
        private bool ClaimOnceUnlocked(string token) => _onceLogged.Add(token);

        // The one place a per-layer dirty set is created. MUST be called with _lock held.
        private HashSet<string> DirtySetForUnlocked(string layerId)
        {
            if (!_dirty.TryGetValue(layerId, out var set))
                _dirty[layerId] = set = new HashSet<string>(StringComparer.Ordinal);
            return set;
        }

        // THE publisher-side gate — one implementation, called from every path that can make a key
        // worth shipping (a real value change, and a staleness transition). Only layers whose
        // LIVE_HELLO said they read this key get it in their dirty set; for everyone else the write
        // stops here and never reaches a socket. MUST be called with _lock held.
        //
        // `collectInto`, when non-null, also receives every layer id the key was delivered to —
        // i.e. exactly the subscriber set, computed by the same DeliverableToUnlocked pass rather
        // than by a second walk that could disagree with it. The hot publish path passes null and
        // allocates nothing; only the rare stale-transition diagnostic asks for the list.
        private void DirtyForSubscribersUnlocked(string key, List<string>? collectInto = null)
        {
            foreach (var pair in _subs)
            {
                if (!DeliverableToUnlocked(pair.Key, pair.Value, key)) continue;
                DirtySetForUnlocked(pair.Key).Add(key);
                collectInto?.Add(pair.Key);
            }
        }

        /// <summary>
        /// THE routing decision, in ONE place: may <paramref name="key"/> reach
        /// <paramref name="layerId"/>? Subscription first (does that layer read the key), then the
        /// optional publish scope (is that layer allowed to receive it at all).
        ///
        /// Every path that selects keys for a layer goes through here — the publisher-side dirty
        /// gate, the <see cref="ResyncIntervalTicks"/> repair sweep, and the LIVE_SNAPSHOT build.
        /// That is deliberate: a scope honoured in only one of the three is worse than no scope,
        /// because the leak then happens on the 30th tick or on the next browser-source refresh
        /// instead of immediately, which is exactly the class of bug nobody reproduces.
        ///
        /// MUST be called with <c>_lock</c> held.
        /// </summary>
        private bool DeliverableToUnlocked(string layerId, LayerSubscription sub, string key)
            => Matches(sub, key) && ScopeAllowsUnlocked(key, layerId);

        /// <summary>
        /// The scope half of <see cref="DeliverableToUnlocked"/>, split out because the pump needs it
        /// on its own: it re-checks the scope when it turns a dirty key into a frame entry, so a key
        /// that was dirtied while unscoped and re-published under a scope a moment later cannot ride
        /// the already-queued entry out to an excluded layer. Without that, saving a new allowlist
        /// would still leak one frame per excluded layer — a one-second window nobody would ever
        /// reproduce, on the exact setting this scope exists to honour.
        ///
        /// MUST be called with <c>_lock</c> held.
        /// </summary>
        private bool ScopeAllowsUnlocked(string key, string layerId)
            => !_scopes.TryGetValue(key, out var scope) || scope.Contains(layerId);

        /// <summary>
        /// Normalises a caller's publish scope to the store's own layer-id spelling, or null when
        /// the scope carries no usable entry.
        ///
        /// Collapsing an all-blank scope to "unscoped" rather than "deliver to nobody" is the
        /// deliberate choice: an empty <c>LiveCaptionsAllowedLayers</c> has ALWAYS meant "every
        /// layer" in this feature, and a scope that silently delivers to no one would reproduce
        /// the very failure this parameter exists to prevent — a configured value that quietly
        /// stops data reaching an overlay.
        ///
        /// Layer ids are compared Ordinal, matching <c>_subs</c> and
        /// <c>LayerRegistry</c>'s own dictionaries — so a hand-typed allowlist entry has to match
        /// the layer id exactly, byte for byte, precisely as the <c>SendToLayerAsync</c> fan-out
        /// it replaces did.
        /// </summary>
        private static HashSet<string>? NormalizeScope(IReadOnlyCollection<string>? allowedLayers)
        {
            if (allowedLayers is null || allowedLayers.Count == 0) return null;
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in allowedLayers)
            {
                string id = Norm(raw);
                if (id.Length > 0) set.Add(id);
            }
            return set.Count > 0 ? set : null;
        }

        // ── Publish (any thread) ──────────────────────────────────────────────

        /// <summary>
        /// Writes <paramref name="key"/> into the channel. ALWAYS writes the store — the
        /// subscription gate (and the optional <paramref name="allowedLayers"/> scope) applies only
        /// to the dirty set, so a key no layer reads is still visible to <c>overlay.get</c>,
        /// <see cref="GetState"/> and <see cref="Provenance"/>; it simply never leaves the process.
        ///
        /// Coalescing: an incoming value whose JSON is identical to the stored one refreshes
        /// <see cref="LiveEntry.LastWriteUtc"/> (so staleness stays honest) and re-stamps
        /// provenance, but does NOT mark the key dirty and does NOT bump the sequence.
        /// </summary>
        /// <param name="value">Any JSON value, including <c>null</c> — null is a legal published value.</param>
        /// <param name="source">Provenance tag, e.g. <c>"tool:Timer"</c> / <c>"script:alerts.phx"</c>.</param>
        /// <param name="expectedInterval">
        /// Publish cadence, when the producer has one. Drives <see cref="GetState"/>: null means
        /// the key never goes Stale (a one-shot author value), which is the right default.
        /// </param>
        /// <param name="allowedLayers">
        /// Optional delivery scope. <c>null</c> — the default, and what every tool key uses — means
        /// "every subscribing layer". A non-null scope narrows delivery to those layer ids only:
        /// the store still records the value exactly as it does for a key nobody subscribes to,
        /// but no other layer's dirty set, resync sweep or LIVE_SNAPSHOT will carry it. The one
        /// production user is <c>AppConfig.LiveCaptionsAllowedLayers</c>, whose whole purpose is
        /// keeping speech off a shared/restream layer that nonetheless binds a caption widget.
        /// </param>
        public void Publish(string key, JsonNode? value, string source, TimeSpan? expectedInterval = null,
                            IReadOnlyCollection<string>? allowedLayers = null)
        {
            string k = Norm(key);
            if (k.Length == 0)
            {
                bool firstEmpty;
                lock (_lock) firstEmpty = ClaimOnceUnlocked("empty-key");
                if (firstEmpty)
                    GlobalLogger.Log(
                        "OverlayLiveStore: a publish arrived with an empty key — dropped. Live-channel keys are literal lowercase dotted names (e.g. 'timer.main.progress').",
                        "OverlayLiveStore", LogLevel.System);
                return;
            }

            string src = Norm(source);
            if (src.Length == 0) src = "unknown";

            // Serialize ONCE, here, for two reasons: the cached string is the coalescing compare,
            // and it is also the only place an unserializable node can be caught cheaply. A double
            // that is NaN/±Infinity produces a JsonValue that constructs fine and then throws when
            // written — if we let that node into the store it would blow up every frame build for
            // that layer, forever. Normalising it to JSON null keeps the channel healthy and makes
            // the breakage visible in the log and on the wire.
            JsonNode? node = value;
            string json;
            Exception? stringifyEx = null;
            try
            {
                json = node?.ToJsonString() ?? "null";
            }
            catch (Exception ex)
            {
                stringifyEx = ex;
                node = null;
                json = "null";
            }

            var now = DateTime.UtcNow;
            bool logUnserializable = false;
            // Set under the lock, logged outside it (ClaimOnceUnlocked's contract).
            bool logKeyCap = false;
            string droppedKeyCapKey = "";
            // Normalised outside the monitor — it touches nothing shared, and the allocation has
            // no business happening under the publish lock.
            HashSet<string>? scope = NormalizeScope(allowedLayers);

            lock (_lock)
            {
                // Scope is recorded FIRST, on both branches, because DirtyForSubscribersUnlocked
                // below reads it back out of _scopes rather than taking it as an argument — one
                // gate for the publish path, the staleness scan, the resync sweep and the
                // snapshot, instead of four call sites that can disagree. Last-write-wins, exactly
                // like Source: an unscoped write CLEARS a previous scope, which is what makes
                // "the streamer emptied the allowlist" take effect on the next caption.
                //
                // One bounded consequence, recorded so it reads as a decision and not an
                // oversight: a scope that WIDENS on a coalesced write (the streamer clears the
                // allowlist while the value happens to be byte-identical) does not dirty the
                // newly-included layers, because the coalesced branch dirties nothing at all —
                // see its own comment for why it deliberately owns no liveness bookkeeping. Those
                // layers are healed by the ResyncIntervalTicks sweep, i.e. within 30 ticks, and in
                // practice by the very next differing value. A per-tick scope-transition ledger
                // would close a self-healing 30 s window at the cost of a second mechanism that
                // has to stay in step with this one.
                // Key-space cap is evaluated BEFORE the scope write so a refused key
                // leaves no trace at all. Writing the scope first would let a refused
                // key accumulate a _scopes entry per distinct key — the very unbounded
                // growth the cap exists to stop, just moved into a different dict.
                // Only a key that is genuinely NEW can be refused; an existing key
                // always proceeds, so updates and the coalesced path are untouched.
                //
                // ★ The cap applies ONLY to script/process publishers. A tool's key set is
                // bounded by construction, so capping tools would buy nothing and cost a
                // great deal: once a runaway script had filled the store, every key a tool
                // publishes LATER — `goal.*` when Goals is enabled mid-stream, a fresh
                // `caption.*`, a new timer's root — would be refused for the rest of the
                // session, giving a dead widget with no diagnostic. Scripts are the only
                // unbounded vector (computed keys), so they are the only ones bounded.
                bool capApplies = src.StartsWith("script:", StringComparison.Ordinal)
                               || src.StartsWith("process:", StringComparison.Ordinal);
                bool refusedNewKey = capApplies && !_store.ContainsKey(k) && _store.Count >= MaxStoreKeys;
                if (refusedNewKey)
                {
                    // Keyed per publishing SOURCE, not one process-wide token: the offender
                    // is the script, and naming only the first one for the whole session
                    // would hide a second runaway entirely. Bounded by the script count.
                    logKeyCap = ClaimOnceUnlocked("keycap:" + src);
                    droppedKeyCapKey = k;
                }
                else
                {

                if (scope is null) _scopes.Remove(k);
                else               _scopes[k] = scope;

                if (_store.TryGetValue(k, out var existing)
                    && string.Equals(existing.ValueJson, json, StringComparison.Ordinal))
                {
                    // Coalesced write. Value and Seq are deliberately untouched: the JSON is
                    // identical, so swapping the stored node would be reference churn with no
                    // observable effect. Source DOES advance — last-write-wins is the channel's
                    // design, and provenance is how a same-key fight between two producers becomes
                    // visible instead of mysterious.
                    _store[k] = existing with
                    {
                        Source       = src,
                        LastWriteUtc = now,
                        // The freshness window survives a coalesced write that declares none. An
                        // author's overlay.publish carries no interval, so overwriting a tool's
                        // interval with null the moment the author happens to publish the same
                        // value would permanently disable that key's staleness — GetState could
                        // never report Stale again, and the overlay would keep painting a dead
                        // timer as live. A caller that DOES declare an interval still wins.
                        ExpectedInterval = expectedInterval ?? existing.ExpectedInterval,
                    };

                    // No liveness bookkeeping here on purpose. A recovered producer whose value
                    // coalesces still has to reach the browser — it is wearing a stale badge on a
                    // key that is alive again — but RecomputeStaleTransitionsUnlocked already
                    // catches that flip, in BOTH directions, at the top of the next tick. Doing it
                    // here as well was a second path to the same outcome, worth at most one pump
                    // interval of latency and costing a duplicate mechanism that no test could
                    // isolate without reaching into the dirty set. One mechanism, one place.
                }
                else
                {
                    // Same freshness-window question as the coalesced branch above, and the
                    // COMMONER half of it: an author publishing a DIFFERENT value carries no
                    // interval, so taking the caller's null unconditionally would wipe a tool's
                    // window on the first ordinary overwrite.
                    //
                    // But blanket inheritance is the opposite false verdict — a genuine one-shot
                    // author key would inherit some tool's cadence and go Stale within seconds.
                    // So inheritance is scoped to the SAME producer: a tool republishing its own
                    // key keeps its declared cadence even on a tick where it passes none, while a
                    // different writer taking the key over brings its own liveness semantics
                    // (including "no cadence, never stale") along with it. Provenance already
                    // records who won, so the two stay consistent.
                    TimeSpan? interval = expectedInterval;
                    if (interval is null
                        && _store.TryGetValue(k, out var prior)
                        && string.Equals(prior.Source, src, StringComparison.Ordinal))
                    {
                        interval = prior.ExpectedInterval;
                    }

                    long seq = Interlocked.Increment(ref _seq);
                    _store[k] = new LiveEntry(k, node, json, seq, src, now, interval);
                    // A real write is Active by definition — LastWriteUtc is now. Recording the
                    // verdict here keeps the transition scan from re-reporting it as news.
                    _lastState[k] = LiveState.Active;
                    DirtyForSubscribersUnlocked(k);
                }

                } // end: not refused by the key-space cap

                if (stringifyEx is not null) logUnserializable = ClaimOnceUnlocked($"unserializable:{k}");
            }

            if (logUnserializable)
                GlobalLogger.Error("OverlayLiveStore",
                    $"key '{k}' (source '{src}') was published with a value that cannot be written as JSON — stored as null instead. A NaN / ±Infinity number is the usual cause.",
                    stringifyEx);

            if (logKeyCap)
                GlobalLogger.Log(
                    $"Overlay live channel is holding {MaxStoreKeys} keys — refusing new key '{droppedKeyCapKey}' (source '{src}'). " +
                    "Updates to existing keys still apply, and the pre-built tools are never refused. This almost " +
                    "always means a script publishes a COMPUTED key (e.g. 'score_{user.name}') on a per-viewer or " +
                    "per-message trigger; publish to a fixed key instead. Further refusals from this source are not logged.",
                    "OverlayLiveStore", LogLevel.CriticalError);
        }

        /// <summary>Publishes a string value. See <see cref="Publish"/> for <paramref name="allowedLayers"/>.</summary>
        public void PublishString(string key, string value, string source, TimeSpan? expectedInterval = null,
                                  IReadOnlyCollection<string>? allowedLayers = null)
            => Publish(key, JsonValue.Create(value), source, expectedInterval, allowedLayers);

        /// <summary>
        /// Publishes a number. A non-finite double (NaN / ±Infinity — e.g. a progress ratio that
        /// divided by zero) is normalised to JSON null by <see cref="Publish"/> and logged once.
        /// See <see cref="Publish"/> for <paramref name="allowedLayers"/>.
        /// </summary>
        public void PublishNumber(string key, double value, string source, TimeSpan? expectedInterval = null,
                                  IReadOnlyCollection<string>? allowedLayers = null)
            => Publish(key, JsonValue.Create(value), source, expectedInterval, allowedLayers);

        /// <summary>Publishes a boolean value. See <see cref="Publish"/> for <paramref name="allowedLayers"/>.</summary>
        public void PublishBool(string key, bool value, string source, TimeSpan? expectedInterval = null,
                                IReadOnlyCollection<string>? allowedLayers = null)
            => Publish(key, JsonValue.Create(value), source, expectedInterval, allowedLayers);

        // ── Retraction ────────────────────────────────────────────────────────

        /// <summary>
        /// Retracts <paramref name="key"/>: publishes a JSON-null TOMBSTONE from
        /// <paramref name="source"/>, which readers render as nothing.
        ///
        /// <para><b>Why this is an API and not four copies of one line.</b> Every
        /// producer that has ever needed to withdraw a key has open-coded
        /// <c>Publish(key, null, …)</c> — Counters did, Timer did — and every producer
        /// that did NOT think of it shipped the same bug: switch the tool off
        /// mid-session and its keys stay in the store at their last value. With no
        /// declared cadence <see cref="ComputeState"/> can only answer Active, so the
        /// overlay keeps painting a number that is no longer merely old but WRONG, with
        /// nothing anywhere to say so. Naming the operation is what makes "and retract
        /// on disable" a thing a producer can be reviewed against.</para>
        ///
        /// <para><b>Why a tombstone and not a delete.</b> Removing the entry outright
        /// would take the key back to <see cref="LiveState.Missing"/> — indistinguishable
        /// from "never published" — and, worse, nothing would be dirtied, so an overlay
        /// already showing the old value would keep showing it until the next resync.
        /// A null write travels the ordinary publish path: it dirties subscribers, rides
        /// the next patch, and reaches the browser as an explicit "this is gone".</para>
        ///
        /// <para>Retracting an absent key is a deliberate no-op rather than a null write:
        /// a producer sweeping a list of keys it MIGHT have published must not be able to
        /// mint tombstones for keys nobody ever had, which would grow the store through
        /// the very door the key cap closes.</para>
        /// </summary>
        public void Retract(string key, string source)
        {
            string k = Norm(key);
            if (k.Length == 0) return;

            lock (_lock)
            {
                if (!_store.ContainsKey(k)) return;
            }

            Publish(k, null, source);
        }

        /// <summary>
        /// Retracts every key under a dotted <paramref name="rootPrefix"/> (e.g.
        /// <c>"loyalty"</c> ⇒ <c>loyalty.leaderboard</c>, <c>loyalty.currency</c>, …) and
        /// returns how many were tombstoned.
        ///
        /// <para>Prefix-based rather than a hand-listed key set on purpose: a hand list is
        /// exactly the thing that goes stale when a producer gains a key, and it goes
        /// stale SILENTLY — the new key is simply never retracted, which is the original
        /// bug wearing a fix's clothing. Matching on the live store means a key can only
        /// escape retraction by not existing.</para>
        ///
        /// <para>Scoped to <paramref name="source"/>: a key a script has taken over
        /// (last-write-wins is the channel's rule) is left to its new owner rather than
        /// silently blanked by a tool the streamer switched off.</para>
        ///
        /// <para><b>Ownership is SAMPLED, not held.</b> <c>Publish</c> takes the same
        /// non-reentrant monitor, so the tombstones are written after the scan releases
        /// it, and the check is re-taken immediately before each write to keep the window
        /// as small as a non-atomic sequence allows. A script that claims one of these
        /// keys inside that window still has its value overwritten by the tombstone —
        /// stated plainly because the alternative reading ("only keys this producer owns
        /// are EVER retracted") is not what the code can promise. Making it exact would
        /// mean a source-conditional write inside the publish lock, which is more
        /// machinery than a disable path warrants.</para>
        /// </summary>
        public int RetractRoot(string rootPrefix, string source)
        {
            string root = Norm(rootPrefix);
            if (root.Length == 0) return 0;
            if (!root.EndsWith('.')) root += ".";

            string src = Norm(source);
            List<string> victims;

            // Snapshot under the lock, publish outside it: Publish takes the same lock,
            // and it is not re-entrant.
            lock (_lock)
            {
                victims = new List<string>();
                foreach (var kv in _store)
                {
                    if (!kv.Key.StartsWith(root, StringComparison.Ordinal)) continue;
                    if (src.Length > 0 && !string.Equals(kv.Value.Source, src, StringComparison.Ordinal)) continue;
                    // Already tombstoned — retracting again would be a no-change write
                    // that still advances provenance for no reason.
                    if (kv.Value.Value is null) continue;
                    victims.Add(kv.Key);
                }
            }

            int retracted = 0;
            foreach (string k in victims)
            {
                // Re-check ownership as late as possible. Not atomic with the write — see
                // the remarks — but it closes the common case where a script claimed the
                // key while we were walking a large store.
                lock (_lock)
                {
                    if (!_store.TryGetValue(k, out var current)) continue;
                    if (current.Value is null) continue;
                    if (src.Length > 0 && !string.Equals(current.Source, src, StringComparison.Ordinal)) continue;
                }

                Publish(k, null, source);
                retracted++;
            }
            return retracted;
        }

        // ── Read-back (overlay.get, diagnostics) ──────────────────────────────

        /// <summary>Current entry for <paramref name="key"/>, or null if nothing was ever published under it.</summary>
        public LiveEntry? Get(string key)
        {
            string k = Norm(key);
            if (k.Length == 0) return null;
            lock (_lock) return _store.TryGetValue(k, out var e) ? e : null;
        }

        /// <summary>
        /// Liveness of one key: <see cref="LiveState.Missing"/> when never published,
        /// <see cref="LiveState.Active"/> forever when the producer declared no interval, and
        /// otherwise Active until <see cref="StaleIntervalMultiplier"/> intervals have elapsed
        /// with no write.
        /// </summary>
        public LiveState GetState(string key)
        {
            var entry = Get(key);
            return entry is null ? LiveState.Missing : ComputeState(entry, DateTime.UtcNow);
        }

        /// <summary>
        /// The liveness rule, in ONE place — <see cref="GetState"/>, the pump's transition scan and
        /// every frame's <c>s</c> field all go through here, so the answer a script reads and the
        /// answer the browser paints can never disagree.
        /// </summary>
        private static LiveState ComputeState(LiveEntry entry, DateTime nowUtc)
        {
            if (entry.ExpectedInterval is not TimeSpan interval) return LiveState.Active;

            // A zero/negative interval cannot define a freshness window. Treat it as
            // never-stale rather than instantly-stale so a producer that passes TimeSpan.Zero
            // gets the interval-less behaviour instead of a permanently red key.
            if (interval <= TimeSpan.Zero) return LiveState.Active;

            // Compare in ticks with a guarded multiply — TimeSpan arithmetic on a very large
            // declared interval would otherwise throw OverflowException from a diagnostic getter.
            long window = interval.Ticks > long.MaxValue / StaleIntervalMultiplier
                ? long.MaxValue
                : interval.Ticks * StaleIntervalMultiplier;
            return (nowUtc - entry.LastWriteUtc).Ticks <= window ? LiveState.Active : LiveState.Stale;
        }

        /// <summary>
        /// Decides an entry's liveness AND records the verdict in <c>_lastState</c>, so the frame we
        /// are about to build and the next tick's transition scan agree on what the browser was last
        /// told — otherwise a frame whose verdict differed from the recorded one would be re-shipped
        /// forever as a phantom transition. MUST be called with <c>_lock</c> held.
        /// </summary>
        private LiveState RecordStateUnlocked(LiveEntry entry, DateTime nowUtc)
        {
            var state = ComputeState(entry, nowUtc);
            _lastState[entry.Key] = state;
            return state;
        }

        /// <summary>
        /// The wire spelling of a verdict — lower-case by contract.
        /// <see cref="LiveState.Missing"/> never appears in a frame: absence of the key IS missing.
        /// </summary>
        private static string WireState(LiveState state) => state == LiveState.Stale ? "stale" : "active";

        /// <summary>
        /// Whole-store diagnostic snapshot, ordered by key. The returned entries carry the live
        /// <see cref="JsonNode"/> instances — read them, never mutate them.
        /// </summary>
        public IReadOnlyList<LiveEntry> Snapshot()
        {
            lock (_lock) return _store.Values.OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Who wrote what, and when — the answer to "two producers are fighting over one key".
        /// Ordered by key.
        /// </summary>
        public IReadOnlyList<(string Key, string Source, DateTime At)> Provenance()
        {
            lock (_lock)
                return _store.Values
                    .OrderBy(e => e.Key, StringComparer.Ordinal)
                    .Select(e => (Key: e.Key, Source: e.Source, At: e.LastWriteUtc))
                    .ToList();
        }

        /// <summary>
        /// Reverse lookup for the diagnostic question "who is listening to this key?" — the layers
        /// whose current subscription matches <paramref name="key"/>, ordered by layer id.
        ///
        /// SUBSCRIPTION only: a layer that subscribes the key but sits outside the key's publish
        /// scope is still listed, because "the widget binds this key" and "the routing layer will
        /// deliver it" are two different questions and this one answers the first. When an overlay
        /// is unexpectedly blank, seeing the layer listed here is what tells the operator the graph
        /// is right and the scope (e.g. <c>LiveCaptionsAllowedLayers</c>) is what withheld it.
        /// </summary>
        public IReadOnlyList<string> LayersSubscribedTo(string key)
        {
            string k = Norm(key);
            if (k.Length == 0) return Array.Empty<string>();
            lock (_lock)
                return _subs.Where(pair => Matches(pair.Value, k))
                            .Select(pair => pair.Key)
                            .OrderBy(id => id, StringComparer.Ordinal)
                            .ToList();
        }

        // ── Subscriptions (from LIVE_HELLO) ───────────────────────────────────

        /// <summary>
        /// Handles a browser's LIVE_HELLO: replaces that layer's subscription wholesale, drops its
        /// pending patch (the snapshot we are about to send supersedes it), then sends
        /// LIVE_SNAPSHOT with every store entry the new subscription matches.
        ///
        /// Faults from <see cref="Dispatcher"/> propagate: the HUDServer call site fires this
        /// through <c>AsyncErrorBoundary.SafeRunAsync</c> exactly like TRANSLATE_REQUEST, so
        /// swallowing here would only hide a broken socket twice over.
        /// </summary>
        /// <param name="proto">Client protocol version. &lt; 1 is rejected; &gt; 1 is accepted and logged once.</param>
        /// <param name="keys">Exact keys and <c>".*"</c> prefixes, in the client's own order (which is what truncation honours).</param>
        /// <param name="replyTo">
        /// Opaque token identifying the socket the HELLO arrived on, for
        /// <see cref="SocketDispatcher"/>. Null (or an unwired socket seam) falls back to
        /// the layer-wide <see cref="Dispatcher"/> — same frame, same content, just
        /// delivered to everyone on the layer instead of to the asker.
        /// </param>
        public async Task HandleHelloAsync(string layerId, int proto, IReadOnlyList<string> keys,
                                           object? replyTo = null, CancellationToken ct = default)
        {
            string id = Norm(layerId);
            // HUDServer guards an empty layerId on every per-layer inbound case; this is the
            // belt-and-braces half — a HELLO with no layer has nowhere to send its snapshot.
            if (id.Length == 0) return;

            if (proto != 1)
            {
                bool firstProto;
                lock (_lock) firstProto = ClaimOnceUnlocked($"proto:{id}");
                if (firstProto)
                    GlobalLogger.Log(
                        proto < 1
                            ? $"OverlayLiveStore: LIVE_HELLO from layer '{id}' declared proto={proto}, below the minimum supported protocol (1) — subscription ignored."
                            : $"OverlayLiveStore: LIVE_HELLO from layer '{id}' declared proto={proto}, newer than this Hub understands (1) — accepted and handled as proto 1.",
                        "OverlayLiveStore", LogLevel.System);
                if (proto < 1) return;
            }

            // Parse OUTSIDE the monitor: caps and prefix normalisation touch nothing shared.
            var sub = new LayerSubscription();
            int droppedKeys = 0, droppedPrefixes = 0;
            foreach (string raw in keys ?? Array.Empty<string>())
            {
                string entry = Norm(raw);
                if (entry.Length == 0) continue;

                if (entry.EndsWith(".*", StringComparison.Ordinal))
                {
                    // Strip only the '*', keeping the dot — see LayerSubscription.
                    string compare = entry.Substring(0, entry.Length - 1);
                    // A duplicate at the cap is not a drop; it changes nothing.
                    if (sub.Prefixes.Count >= MaxSubscriptionPrefixes && !sub.Prefixes.Contains(compare))
                    {
                        droppedPrefixes++;
                        continue;
                    }
                    sub.Prefixes.Add(compare);
                }
                else
                {
                    if (sub.Keys.Count >= MaxSubscriptionKeys && !sub.Keys.Contains(entry))
                    {
                        droppedKeys++;
                        continue;
                    }
                    sub.Keys.Add(entry);
                }
            }

            var matched = new List<FrameEntry>();
            bool logCap = false;
            var nowUtc = DateTime.UtcNow;
            lock (_lock)
            {
                // ★ Did this HELLO actually CHANGE what the layer reads?
                //
                // A subscription is per-LAYER, so a second browser's HELLO replaces the key
                // set for every socket on that layer. While the snapshot fanned layer-wide
                // that was self-correcting — everyone got the new state. Now the snapshot
                // answers only the asker, so on a genuine key-set change the OTHER sockets
                // would keep rendering against a subscription that no longer describes them
                // until the resync sweep came round.
                //
                // Detecting the change is what lets both properties hold at once: the
                // common case (a reconnect re-announcing the SAME keys, which is every OBS
                // scene flip) does not re-dirty the whole key space, while a real change
                // does, so a sibling socket is never left reading a subscription that no
                // longer describes it.
                bool subscriptionChanged =
                    _subs.TryGetValue(id, out var priorSub) && !SameSubscription(priorSub, sub);

                _subs[id] = sub;
                // Bumping the epoch beside the subscription swap is the other half of
                // "a snapshot supersedes a pending patch". Dropping the dirty set alone is not
                // enough: the pump snapshots and clears under this same lock but DISPATCHES
                // outside it, so a patch built from the pre-HELLO world can still be in flight
                // right now. The epoch lets the pump notice that and skip it.
                _epoch[id] = _epoch.TryGetValue(id, out long previousEpoch) ? previousEpoch + 1 : 1;

                // ★ The pending dirty set is preserved when the snapshot is going to ONE
                // SOCKET, and only dropped when it fans to the whole layer.
                //
                // Clearing it unconditionally was correct only while every HELLO answered
                // the entire layer — then the snapshot really did supersede the pending
                // patch for every reader. With an addressed snapshot it does not: the
                // OTHER sockets on the layer get neither the snapshot nor the patch, so
                // every key that was dirty at HELLO time is silently lost to them. And
                // multiple sockets per layer is the normal shape, not an edge case — a
                // second OBS browser source, a `?widget=` preview and the Visualist
                // design-time preview all connect to the same /hud/<id> and each sends its
                // own HELLO. The keys the siblings missed would sit unsent until some
                // producer happened to touch them again, which for an event-driven family
                // means indefinitely.
                //
                // Keeping the set costs the ASKER one redundant patch for keys it just
                // received in its snapshot — bounded, idempotent, and vastly cheaper than
                // the frozen-widget failure it prevents.
                bool addressedToOneSocket = replyTo is not null && SocketDispatcher is not null;
                if (!addressedToOneSocket) _dirty.Remove(id);
                _helloSeen.Add(id);

                // Same gate as the publish path, scope included: a snapshot is a full-state frame,
                // so it is the fastest way to leak a scoped key — every browser-source refresh
                // rebuilds it from the whole store.
                foreach (var entry in _store.Values)
                    if (DeliverableToUnlocked(id, sub, entry.Key))
                        matched.Add(new FrameEntry(entry, RecordStateUnlocked(entry, nowUtc)));

                // A genuine key-set change re-points what EVERY socket on the layer reads,
                // so the siblings need the full new set, not just whatever happened to be
                // dirty. (After the _dirty.Remove above, or the removal would eat it.)
                if (subscriptionChanged && matched.Count > 0)
                {
                    var set = DirtySetForUnlocked(id);
                    foreach (var frameEntry in matched) set.Add(frameEntry.Entry.Key);
                }

                if (droppedKeys > 0 || droppedPrefixes > 0) logCap = ClaimOnceUnlocked($"cap:{id}");
            }

            if (logCap)
                GlobalLogger.Log(
                    $"OverlayLiveStore: LIVE_HELLO from layer '{id}' exceeded the subscription caps — kept {sub.Keys.Count}/{MaxSubscriptionKeys} keys and {sub.Prefixes.Count}/{MaxSubscriptionPrefixes} prefixes, dropped {droppedKeys} key(s) and {droppedPrefixes} prefix(es). The layer's remaining bindings still update; the dropped ones will not.",
                    "OverlayLiveStore", LogLevel.System);

            // Answer the ASKER when the transport gave us one, and only fall back to the
            // layer-wide fan when it did not. A snapshot is a full repaint instruction;
            // sending it to sockets that did not ask is pure waste on the render thread.
            var socketDispatcher = SocketDispatcher;
            var dispatcher = Dispatcher;
            if (replyTo is null || socketDispatcher is null)
            {
                if (dispatcher is null) return;
            }

            try
            {
                object frame = BuildFrame("LIVE_SNAPSHOT", matched);
                if (replyTo is not null && socketDispatcher is not null)
                    await socketDispatcher(replyTo, frame, ct).ConfigureAwait(false);
                else
                    await dispatcher!(id, frame, ct).ConfigureAwait(false);
            }
            catch
            {
                // The snapshot never reached the socket — and we already dropped this layer's dirty
                // set on the assumption that it would. Re-dirty everything the new subscription
                // matches so the next pump tick re-ships it; without this the overlay is starved
                // until some producer happens to change a value, which for a one-shot author key or
                // a rarely-moving leaderboard means indefinitely.
                lock (_lock)
                {
                    // …but only while this HELLO is still the current one. A newer HELLO has its own
                    // snapshot in flight, and re-dirtying against a superseded subscription would
                    // ship keys the browser no longer reads (the pump resolves dirty keys straight
                    // out of the store, without re-checking the match).
                    if (_subs.TryGetValue(id, out var current) && ReferenceEquals(current, sub))
                    {
                        var set = DirtySetForUnlocked(id);
                        foreach (var frameEntry in matched) set.Add(frameEntry.Entry.Key);
                    }
                }
                // Rethrow: the HUDServer call site fires this through AsyncErrorBoundary.SafeRunAsync
                // exactly like TRANSLATE_REQUEST, so swallowing here would hide a broken socket.
                throw;
            }
        }

        /// <summary>
        /// Forgets a layer's subscription and pending patch — called when its last browser socket
        /// closes or the layer leaves the registry.
        ///
        /// The stale-compositor diagnostic state is deliberately NOT cleared: <c>_helloSeen</c> and
        /// the once-log ledger are sticky for process life, because OBS reconnects a browser source
        /// on every scene flip and a per-reconnect re-log would be pure spam. Neither is the layer's
        /// epoch — see its field comment: resetting it would re-open the ABA hole it exists to close.
        /// </summary>
        public void ClearLayer(string layerId)
        {
            string id = Norm(layerId);
            if (id.Length == 0) return;
            lock (_lock)
            {
                _subs.Remove(id);
                _dirty.Remove(id);
                _connectionNoted.Remove(id);
            }
        }

        /// <summary>
        /// Arms the HELLO deadline for a layer that just got a browser connection. HUDServer calls
        /// this right after the connection registers, then calls
        /// <see cref="ReportHelloDeadline"/> once <see cref="HelloDeadlineMs"/> has elapsed.
        /// </summary>
        public void NoteConnection(string layerId)
        {
            string id = Norm(layerId);
            if (id.Length == 0) return;
            lock (_lock) _connectionNoted.Add(id);
        }

        /// <summary>
        /// The HELLO deadline verdict. Logs ONCE per layer, and only when the layer still has live
        /// browser connections yet has never sent a LIVE_HELLO — the signature of an OBS Browser
        /// Source running a cached compositor.js from before the live channel existed. Silent in
        /// every other case (browser already gone, protocol spoken, layer never armed).
        /// </summary>
        public void ReportHelloDeadline(string layerId, int liveConnections)
        {
            string id = Norm(layerId);
            if (id.Length == 0) return;
            // No connections left means the browser simply went away before the deadline — a
            // scene flip, not a stale script. Nothing to diagnose.
            if (liveConnections <= 0) return;

            bool first;
            lock (_lock)
            {
                if (!_connectionNoted.Contains(id)) return; // never armed — not our verdict to give
                if (_helloSeen.Contains(id)) return;        // the compositor spoke; all is well
                first = ClaimOnceUnlocked($"hello-deadline:{id}");
            }

            if (first)
                GlobalLogger.Log(
                    $"OverlayLiveStore: layer '{id}' has {liveConnections} live browser connection(s) but sent no LIVE_HELLO within {HelloDeadlineMs} ms — the OBS Browser Source is almost certainly running a cached copy of compositor.js from before the live channel existed, so nothing on that overlay will receive live data. Refresh the browser source (right-click the source → Refresh cache of current page) to pick up the current overlay script.",
                    "OverlayLiveStore", LogLevel.System);
        }

        // ── Pump ──────────────────────────────────────────────────────────────

        /// <summary>Starts the coalescing pump at <see cref="PumpIntervalMs"/>. Idempotent.</summary>
        public void StartPump()
        {
            lock (_loopGate)
            {
                if (_loopStarted) return;
                _loopStarted = true;
                _loopCts = new CancellationTokenSource();
                var ct = _loopCts.Token;
                _loopTask = Task.Run(() => PumpLoopAsync(ct));
            }
            GlobalLogger.Log($"OverlayLiveStore pump started ({PumpIntervalMs} ms cadence).",
                "OverlayLiveStore", LogLevel.System);
        }

        /// <summary>Stops the pump and awaits its drain, so nothing is mid-dispatch when Hub tears the HUD server down.</summary>
        public async Task ShutdownAsync()
        {
            CancellationTokenSource? cts;
            Task? loop;
            lock (_loopGate)
            {
                cts = _loopCts; loop = _loopTask;
                _loopStarted = false; _loopCts = null; _loopTask = null;
            }
            try { cts?.Cancel(); } catch { /* best-effort */ }
            if (loop is not null)
            {
                try { await loop.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected */ }
                catch (Exception ex) { GlobalLogger.Error("OverlayLiveStore", "pump loop drain failed", ex); }
            }
            cts?.Dispose();
            GlobalLogger.Log("OverlayLiveStore pump stopped.", "OverlayLiveStore", LogLevel.System);
        }

        private async Task PumpLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(PumpIntervalMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                // Outer boundary: a fault anywhere in frame assembly must not end the pump for
                // the rest of the process's life. The strict 4-arg overload so a cancellation
                // from an UNRELATED token still reaches the error channel.
                await AsyncErrorBoundary.SafeRunAsync(
                    () => PumpOnceAsync(ct), "OverlayLiveStore", "live-channel pump tick", ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// One pump tick: emit a LIVE_PATCH to every layer with a non-empty dirty set. Internal so
        /// the test suite drives the channel by hand — no timer, no Task.Delay, no wall-clock
        /// dependency (the same InternalsVisibleTo seam TimerService.TickOnceAsync uses).
        /// </summary>
        internal async Task PumpOnceAsync(CancellationToken ct)
        {
            var dispatcher = Dispatcher;
            // Nothing wired yet — keep the dirty sets so the first tick after wiring ships the
            // accumulated truth instead of a hole. The tick is not counted either: a resync that
            // cannot be delivered is not a resync.
            if (dispatcher is null) return;

            var nowUtc = DateTime.UtcNow;
            bool resync = Interlocked.Increment(ref _tickCount) % ResyncIntervalTicks == 0;

            // Snapshot-then-clear under the monitor; build and send outside it.
            var pending = new List<(string LayerId, long Epoch, List<FrameEntry> Entries)>();
            // Stays null on every tick with no first-time stale transition on a subscribed key,
            // which is very nearly all of them.
            List<string>? staleNotices;
            lock (_lock)
            {
                // BEFORE the snapshot, so a key that just went Stale rides this very tick.
                staleNotices = RecomputeStaleTransitionsUnlocked(nowUtc);
                if (resync) ResyncEverySubscribedKeyUnlocked();

                foreach (var pair in _dirty)
                {
                    if (pair.Value.Count == 0) continue;
                    var entries = new List<FrameEntry>(pair.Value.Count);
                    foreach (string key in pair.Value)
                        // The scope is re-checked here and not just at dirty time, so a key whose
                        // scope narrowed between the publish and this tick cannot ride an
                        // already-queued dirty entry to an excluded layer. The SUBSCRIPTION is
                        // deliberately not re-checked — that is the epoch gate's job, below.
                        if (ScopeAllowsUnlocked(key, pair.Key) && _store.TryGetValue(key, out var entry))
                            entries.Add(new FrameEntry(entry, RecordStateUnlocked(entry, nowUtc)));
                    if (entries.Count > 0)
                        pending.Add((pair.Key, _epoch.TryGetValue(pair.Key, out long epoch) ? epoch : 0, entries));
                }
                _dirty.Clear();
            }

            // Outside the monitor, and BEFORE the dispatch loop: the notice explains a frozen
            // reading a viewer may already be looking at, so it should not queue behind a wedged
            // socket's AsyncErrorBoundary timeout. Each line was already claimed once-per-key
            // inside the lock, so this cannot repeat on the next tick.
            if (staleNotices is not null)
                foreach (string notice in staleNotices)
                    GlobalLogger.Log(notice, "OverlayLiveStore", LogLevel.System);

            foreach (var (layerId, epoch, entries) in pending)
            {
                // The epoch gate. A LIVE_HELLO that landed after we cleared this layer's dirty set
                // has ALREADY sent a whole-store LIVE_SNAPSHOT carrying newer truth, so shipping
                // this patch now would reorder the wire and repaint the browser with pre-HELLO
                // values — permanently, because _dirty is gone and the producer's next identical
                // publish coalesces. One check closes the whole family: this reorder, two concurrent
                // HELLOs, and a duplicate hello on reconnect.
                //
                // And we deliberately do NOT re-dirty the skipped keys: re-dirtying would ship
                // exactly the superseded values the snapshot just replaced. The ResyncIntervalTicks
                // sweep is the safety net if the snapshot itself was lost.
                bool superseded;
                lock (_lock) superseded = !_epoch.TryGetValue(layerId, out long current) || current != epoch;
                if (superseded) continue;

                object frame = BuildFrame("LIVE_PATCH", entries);
                // Per-layer boundary: one wedged or throwing socket must cost that layer its
                // frame and nothing more — the remaining layers still get theirs, and the pump
                // survives either way.
                await AsyncErrorBoundary.SafeRunAsync(
                    () => dispatcher(layerId, frame, ct),
                    "OverlayLiveStore", $"LIVE_PATCH dispatch to layer '{layerId}'", ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Recomputes liveness for every key that declared a cadence and dirties the ones whose
        /// verdict moved. Runs at the top of every pump tick, with <c>_lock</c> held.
        ///
        /// A transition is rare, so in steady state this is a walk over a handful of
        /// interval-bearing keys that dirties nothing at all — coalescing stays fully intact.
        /// Interval-less keys are skipped outright: they are Active forever, so there is no
        /// transition to find.
        /// </summary>
        /// <returns>
        /// One fully-formatted diagnostic line per key that went Stale while at least one layer was
        /// subscribed to it, or null — the overwhelmingly common answer — when there were none. The
        /// caller emits the lines AFTER releasing the monitor, because GlobalLogger takes its own
        /// locks and fans out to synchronous subscribers (the rule <see cref="ClaimOnceUnlocked"/>
        /// documents). Returning null rather than an empty list keeps the steady-state tick
        /// allocation-free.
        ///
        /// WHY this diagnostic exists at all: staleness otherwise has NO default surface. The
        /// verdict is real and correct — <see cref="liveRenderableValue"/>'s browser-side twin
        /// keeps painting the last value on purpose — but it only becomes visible as the value of
        /// a pin the author had to wire. For the timer trio that pin (<c>Live</c>) is brand new,
        /// so no ALREADY-SAVED widget can possibly be wired to it. A streamer who deletes a timer
        /// mid-stream therefore gets a frozen <c>01:23:45</c> on the overlay for the rest of the
        /// session with nothing said anywhere. One log line is the floor.
        ///
        /// SUBSCRIBED keys only. An unsubscribed key going stale is nobody's problem: nothing is
        /// painting it, so there is no wrong pixel to explain. Logging those would mean a line per
        /// leftover key of every timer ever deleted, which is how a useful diagnostic becomes
        /// noise nobody reads.
        /// </returns>
        private List<string>? RecomputeStaleTransitionsUnlocked(DateTime nowUtc)
        {
            List<string>? staleNotices = null;

            foreach (var entry in _store.Values)
            {
                if (entry.ExpectedInterval is null) continue;

                LiveState state = ComputeState(entry, nowUtc);
                if (_lastState.TryGetValue(entry.Key, out var previous) && previous == state) continue;

                _lastState[entry.Key] = state;

                // Collect the subscriber list only on a Stale flip — the one case that can log.
                // Every other transition (including the recovery back to Active) allocates nothing.
                List<string>? subscribers = state == LiveState.Stale ? new List<string>() : null;

                // Same publisher-side gate as a real value change: a liveness flip on a key no
                // layer reads costs one dictionary write and never reaches a socket.
                DirtyForSubscribersUnlocked(entry.Key, subscribers);

                // Order matters: claim the once-token only AFTER finding a subscriber. Claiming
                // first would burn the token on an unsubscribed flip, and the report a widget
                // author actually needs — the flip that happens once a layer IS reading the key —
                // would then be silently swallowed.
                if (subscribers is not { Count: > 0 }) continue;
                if (!ClaimOnceUnlocked("stale:" + entry.Key)) continue;

                subscribers.Sort(StringComparer.Ordinal);
                staleNotices ??= new List<string>();
                staleNotices.Add(
                    $"OverlayLiveStore: key '{entry.Key}' (source '{entry.Source}') went STALE — nothing has " +
                    $"republished it within {FormatInterval(entry.ExpectedInterval.Value)} × {StaleIntervalMultiplier}. " +
                    $"Subscribed layer(s): {string.Join(", ", subscribers)}. The last value keeps rendering on " +
                    "purpose (blanking it would flicker the overlay on a one-beat hiccup), so a widget bound to " +
                    "this key is now showing a FROZEN reading. A deleted timer is the usual cause. Read the " +
                    "node's Live pin (or Var.Live's State pin) to branch on this in the widget graph. " +
                    "Logged once per key.");
            }

            return staleNotices;
        }

        // Renders the declared cadence for the stale notice. Whole seconds when it divides evenly
        // (every current producer ticks at 1 s), milliseconds otherwise — a bare TimeSpan.ToString()
        // would print "00:00:01" where "1s" is what a reader wants.
        private static string FormatInterval(TimeSpan interval)
            => interval.TotalMilliseconds % 1000 == 0
                ? $"{(long)interval.TotalSeconds}s"
                : $"{(long)interval.TotalMilliseconds}ms";

        /// <summary>
        /// The <see cref="ResyncIntervalTicks"/> repair sweep: re-dirty every stored key each layer
        /// subscribes to, regardless of whether it changed. With <c>_lock</c> held; see the
        /// constant's remarks for why a silently dropped frame is otherwise permanent rather than
        /// momentary.
        ///
        /// "Unconditionally" means unconditional on CHANGE, never on ROUTING: the sweep goes through
        /// <see cref="DeliverableToUnlocked"/> exactly like the publish path, so a scoped key cannot
        /// leak here. Bypassing the scope in this one place would have been the worst possible
        /// failure shape — captions correctly withheld from a shared layer for 29 ticks and then
        /// delivered on the 30th, i.e. every half minute, unreproducibly.
        /// </summary>
        private void ResyncEverySubscribedKeyUnlocked()
        {
            foreach (var pair in _subs)
            {
                HashSet<string>? set = null;
                foreach (var entry in _store.Values)
                {
                    if (!DeliverableToUnlocked(pair.Key, pair.Value, entry.Key)) continue;
                    (set ??= DirtySetForUnlocked(pair.Key)).Add(entry.Key);
                }
            }
        }

        // ── Internals ─────────────────────────────────────────────────────────

        /// <summary>
        /// Subscription match: an exact-set hit, or an Ordinal StartsWith against any stored
        /// prefix. Ordinal on both halves — keys are literal, and case-folding the subscription
        /// while publishes stay literal would silently break every binding.
        /// </summary>
        private static bool Matches(LayerSubscription sub, string key)
        {
            if (sub.Keys.Contains(key)) return true;
            foreach (string prefix in sub.Prefixes)
                if (key.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// Builds a LIVE_SNAPSHOT / LIVE_PATCH frame. Runs OUTSIDE <c>_lock</c> — DeepClone walks
        /// arbitrarily large author payloads and has no business holding the publish path up, which
        /// is why each entry's liveness verdict was decided under the lock and travels in on
        /// <see cref="FrameEntry"/>.
        ///
        /// The frame's <c>seq</c> is the GLOBAL counter read at build time — never the maximum over
        /// the frame's own entries. Max-over-entries runs BACKWARDS: a patch carries A@seq10, then a
        /// graph change narrows the layer's subscription to B (last written at seq5) and the
        /// resulting snapshot would report 5. The browser ignores any frame whose <c>seq</c> is
        /// lower than the one it already applied, so it would discard the snapshot that is actually
        /// newer. Reading the counter is also what gives an entry-less frame a sane marker, with no
        /// special case.
        /// </summary>
        private static object BuildFrame(string type, List<FrameEntry> entries)
        {
            // Deterministic key order: the wire text is then stable across runs, which makes the
            // frames diffable in DevTools and assertable in tests. Sorting a handful of keys at
            // 1 Hz is free.
            entries.Sort(static (a, b) => string.CompareOrdinal(a.Entry.Key, b.Entry.Key));

            long seq = Interlocked.Read(ref _seq);
            var wire = new object[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i].Entry;
                // DeepClone is mandatory, not defensive: a JsonNode holds a parent pointer, so
                // handing the stored instance to anything that adopts it throws
                // InvalidOperationException — and the second frame carrying the same key would
                // throw even if the first succeeded.
                wire[i] = new { k = entry.Key, v = entry.Value?.DeepClone(), s = WireState(entries[i].State) };
            }

            return new { type, seq, entries = wire };
        }
    }
}
